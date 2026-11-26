using System.Collections.Concurrent;
using Mango.Resources;

namespace Mango.Providers;

internal sealed partial class ProviderCatalog
{
    private sealed record StartupWork(PendingRelease[] Evidence, bool ReachableOrObserved);
    private readonly ConcurrentDictionary<Guid, StartupWork> startupWork = new();
    private const string StartupWorkWait = "Waiting for the provider to confirm that work from before Mango started has settled.";

    internal PendingRelease[] StartupEvidence => startupWork.Values.SelectMany(work => work.Evidence).ToArray();

    internal async Task<bool> InspectStartupAsync(PendingRelease[] history, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            foreach (var group in history.GroupBy(item => item.Endpoint).Where(group => adapters.ContainsKey(group.Key)))
                startupWork[group.Key] = new(group.GroupBy(item => item.Deployment).Select(items => items.First() with
                {
                    Loading = items.Any(item => item.Loading),
                    OwnedProcesses = items.SelectMany(item => item.OwnedProcesses).Distinct().Take(256).ToArray()
                }).ToArray(), group.Any(HasSurvivingProcess));
            foreach (var endpoint in store.Value.Endpoints)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(endpoint.Profile == ProviderProfile.Strata ? StrataAdapter.ReleaseTimeout : ProviderLimits.ControlTimeout);
                try { await RefreshOneAsync(endpoint.Id, timeout.Token, startupScan: true); }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { Publish(); }
            }
            return history.Length != 0 && history.All(item =>
                observations.GetValueOrDefault(item.Endpoint) is { ConnectionRefused: true } &&
                adapters.TryGetValue(item.Endpoint, out var adapter) && MissingLocalLoad(item, adapter)) &&
                store.Value.Endpoints.Where(endpoint => endpoint.Mode == ProviderMode.Managed).All(endpoint =>
                    observations.GetValueOrDefault(endpoint.Id) is { Error: null } ||
                    history.Any(item => item.Endpoint == endpoint.Id));
        }
        finally { ReleaseOperation(); }
    }

    private async Task CheckStartupInventoryAsync(Guid id, ProviderAdapter adapter, ProviderInventory inventory,
        bool startupScan, CancellationToken cancellation)
    {
        var prior = startupWork.GetValueOrDefault(id)?.Evidence ?? [];
        var idle = new Dictionary<string, bool?>(StringComparer.Ordinal);
        foreach (var model in inventory.Models.Where(item => item.Instances.Length != 0 &&
            (startupScan || prior.Any(evidence => evidence.Deployment == item.Id))))
        {
            if (model.Instances.Any(instance => instance.Busy))
            {
                idle[model.Id] = false;
                continue;
            }
            if (cancellation.IsCancellationRequested)
            {
                idle[model.Id] = null;
                continue;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try { idle[model.Id] = await adapter.CheckIdleAsync(model, timeout.Token); }
            catch (Exception error) when (error is ProviderFailure or IOException or OperationCanceledException)
            {
                idle[model.Id] = null;
            }
        }

        var evidence = prior.Where(item =>
        {
            if (idle.TryGetValue(item.Deployment, out var state))
                return state != true;
            return inventory.Models.Any(model => model.Id == item.Deployment && model.AmbiguousInstance) ||
                (item.Loading ? !inventory.LoadsSettled : HasSurvivingProcess(item));
        }).ToDictionary(item => item.Deployment, StringComparer.Ordinal);
        foreach (var (deployment, state) in idle)
            if (state == false)
                evidence.TryAdd(deployment, new(id, deployment, []));
        if (evidence.Count == 0)
            startupWork.TryRemove(id, out _);
        else
            startupWork[id] = new(evidence.Values.ToArray(), true);
    }

    private void CheckFailedStartupInventory(Guid id, ProviderAdapter adapter, ProviderFailure error)
    {
        if (!startupWork.TryGetValue(id, out var prior))
            return;
        var evidence = error.ConnectionRefused
            ? prior.Evidence.Where(item => !MissingLocalLoad(item, adapter)).ToArray() : prior.Evidence;
        if (evidence.Length == 0)
            startupWork.TryRemove(id, out _);
        else
            startupWork[id] = new(evidence, prior.ReachableOrObserved || evidence.Any(HasSurvivingProcess) ||
                adapter is LlamaAdapter llama && llama.HasPossibleLocalRuntime());
    }

    // A refusal alone does not prove a load stopped before binding. The configured
    // artifact must also be missing, with no owned process, sampled workers or live saved PIDs.
    private bool MissingLocalLoad(PendingRelease item, ProviderAdapter adapter) =>
        item.Loading && adapter is LlamaAdapter { ConfiguredModel: { Installed: false } configured, OwnedProcessId: null } llama &&
        configured.Id == item.Deployment && !HasSurvivingProcess(item) && !llama.HasPossibleLocalRuntime();

    private bool HasSurvivingProcess(PendingRelease item) =>
        item.OwnedProcesses.Any(WindowsObservation.ProcessExists) ||
        resources?.Observation?.Workers.GetValueOrDefault(item.Endpoint)?.Processes.Any(WindowsObservation.ProcessExists) == true;

    internal void RefreshObservedStartupWork()
    {
        foreach (var (id, work) in startupWork)
            if (work.ReachableOrObserved)
                SignalRefresh(id);
    }

    private Guid? StartupBlocker(Deployment target, CatalogView snapshot, bool load)
    {
        foreach (var (endpoint, work) in startupWork)
        {
            if (endpoint != target.EndpointId && (!load || !work.ReachableOrObserved))
                continue;
            foreach (var item in work.Evidence)
            {
                var model = snapshot.Models.FirstOrDefault(model => model.Id == item.Deployment && model.EndpointId == endpoint);
                if (endpoint != target.EndpointId && model?.Model.Origin == ModelOrigin.LmLink)
                    continue;
                // Independently observed YOLO execution does not reserve its siblings.
                if (item.Deployment == target.Id || item.Loading || model is null || !target.Yolo || !model.Yolo)
                    return endpoint;
            }
        }
        return null;
    }

    private void CheckStartupWork(Deployment target, CatalogView snapshot, bool load)
    {
        if (StartupBlocker(target, snapshot, load) is not { } endpoint)
            return;
        SignalRefresh(endpoint);
        throw new ProviderFailure(503, StartupWorkWait, capacityWait: true);
    }
}
