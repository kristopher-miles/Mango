using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Mango.Host;
using Mango.Resources;
using Mango.Scheduling;

namespace Mango.Providers;

internal sealed record EndpointStatus(Guid Id, ProviderProfile Profile, ProviderMode Mode, string Address,
    Guid? CredentialReference, string? Executable, string? ModelFile, bool Online, string? Version,
    string? Error, DateTimeOffset? ObservedAt, bool CanLoad, bool CanUnload, bool CanStop, int? OwnedProcessId,
    int? MaxLoadedModels = null, bool CredentialNeeded = false, string? ConfigurationRevision = null,
    string? StrataControl = null, string? StrataModels = null, bool HostReserved = false, DateTimeOffset? LoadingSince = null)
{
    public bool ExclusiveHost => Profile == ProviderProfile.Strata;
    [JsonIgnore] public ProviderProfile NetworkProfile => WireProfile(Profile);
    [JsonIgnore] public int[]? ExactProcessIds { get; init; }

    internal static ProviderProfile WireProfile(ProviderProfile profile) =>
        profile == ProviderProfile.Strata ? ProviderProfile.Generic : profile;
}

internal sealed record CatalogView(EndpointStatus[] Endpoints, Deployment[] Models, string? Selected, string? RecoveryWarning);
internal sealed record ProviderLifecycle(string DeploymentId, bool Loading, TimeSpan Elapsed);
internal sealed record ReleasedEndpoint(Guid Id, int? StoppedProcess);
internal sealed record ReleaseResult(ReleasedEndpoint[] Released, string? Error = null, bool AwaitingStop = false);
internal sealed record ServingPlacement(Deployment Model, EndpointStatus Endpoint, ResourceBudget? Budget, Workload Workload);
internal sealed record ExecutionResult(bool Settled, ProviderFailure? Failure, ProviderExecution? Timing);
internal sealed record StartExecution(AdmissionStatus Status, string? Reason,
    Func<HttpContext, CancellationToken, Task<ExecutionResult>>? Execute = null, string? Epoch = null);

internal sealed class ProviderCatalog(string directory, ResourceProtection? resources = null) : IAsyncDisposable, IServingBackend
{
    private sealed record Observation(ProviderInventory Inventory, string? Error, DateTimeOffset ObservedAt,
        bool AllocationFailed = false, bool CredentialNeeded = false);

    private readonly SnapshotFile<ProviderSettings> store = new(Path.Combine(directory, "providers.json"), Validate);
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim operation = new(1);
    private readonly SemaphoreSlim input = new(16);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly ConcurrentDictionary<Guid, byte> pendingRefreshes = new();
    private readonly ConcurrentDictionary<Guid, ProviderAdapter> adapters = [];
    private readonly Dictionary<Guid, Observation> observations = [];
    private readonly Dictionary<Guid, (CancellationTokenSource Stop, Task Work)> watchers = [];
    private readonly ConcurrentDictionary<string, Guid> uncertainModels = new();
    private CatalogView view = new([], [], null, null);
    private readonly ConcurrentDictionary<string, (Guid Endpoint, int Count)> activeModels = new();
    private ProviderExecution? lastExecution;
    private ProviderLifecycle? lastLifecycle;
    private sealed record ExclusiveLoad(Guid Endpoint, CancellationTokenSource Stop, DateTimeOffset Started);
    private volatile ExclusiveLoad? exclusiveLoad;
    public event Action? Changed;
    public event Action? AdmissionChanged;
    public ResourceProtection? Resources => resources;
    internal (Guid Endpoint, string Deployment)[] Unsettled =>
        uncertainModels.Select(item => (item.Value, item.Key)).ToArray();

    public bool HasUnsettled(string deployment) => uncertainModels.ContainsKey(deployment);
    internal bool HasActiveRequests => !activeModels.IsEmpty;
    internal CatalogView ResourceView
    {
        get
        {
            var snapshot = View;
            return snapshot with { Endpoints = snapshot.Endpoints.Select(endpoint => endpoint.ExclusiveHost
                ? endpoint with { ExactProcessIds = (adapters.GetValueOrDefault(endpoint.Id) as StrataAdapter)?.ObservedProcessIds() }
                : endpoint).ToArray() };
        }
    }

    // A live canceled load can still be in transport, even when the provider is currently idle.
    private readonly ConcurrentDictionary<string, bool> uncertainLoads = new();
    internal bool HasUnsettledLoad(string deployment) => uncertainLoads.ContainsKey(deployment);
    internal bool HasUnsettledWork(EndpointStatus endpoint) =>
        uncertainModels.Any(item => endpoint.ExclusiveHost || item.Value == endpoint.Id);

    internal void RestoreUnsettled(Guid endpoint, string deployment, bool loading = false)
    {
        if (adapters.ContainsKey(endpoint))
        {
            uncertainModels[deployment] = endpoint;
            if (loading) uncertainLoads[deployment] = true;
        }
    }

    internal void ConfirmReleased(Guid endpoint, bool includeYolo = true)
    {
        foreach (var item in uncertainModels.Where(item => item.Value == endpoint && (includeYolo || !IsYolo(item.Key))))
        {
            uncertainModels.TryRemove(item.Key, out _);
            uncertainLoads.TryRemove(item.Key, out _);
        }
    }

    private bool IsYolo(string id) => View.Models.Any(model => model.Id == id && model.Yolo);

    public CatalogView View
    {
        get
        {
            var snapshot = Volatile.Read(ref view);
            return activeModels.IsEmpty ? snapshot : snapshot with
            {
                Models = snapshot.Models.Select(model => activeModels.ContainsKey(model.Id) ? model with { Busy = true } : model).ToArray()
            };
        }
    }
    public ProviderExecution? LastExecution => Volatile.Read(ref lastExecution);
    internal event Action<string, bool, int>? LifecycleFailed;
    internal Func<string, Workload, string, Action<BenchmarkResult?>?>? BeginBenchmarkLog { get; set; }
    public ProviderLifecycle? LastLifecycle => Volatile.Read(ref lastLifecycle);
    public void ClearPrivateExecution() => Volatile.Write(ref lastExecution, null);

    public async Task InitializeAsync()
    {
        await store.LoadAsync(ProviderSettings.Empty);
        foreach (var endpoint in store.Value.Endpoints)
            adapters[endpoint.Id] = CreateAdapter(endpoint);
        Publish();
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        try
        {
            await RefreshAsync(null, stopping);
            while (!stopping.IsCancellationRequested)
            {
                var wait = View.Endpoints.Any(endpoint => endpoint.HostReserved) ? TimeSpan.FromSeconds(10) : ProviderLimits.InventoryInterval;
                if (!await wake.WaitAsync(wait, stopping))
                {
                    if (wait == ProviderLimits.InventoryInterval)
                        await RefreshAsync(null, stopping);
                    else
                        await ReleaseIdleStrataAsync(stopping);
                    continue;
                }
                await Task.Delay(ProviderLimits.RefreshCoalesceInterval, stopping);
                foreach (var id in pendingRefreshes.Keys)
                {
                    if (!pendingRefreshes.TryRemove(id, out _))
                        continue;
                    await operation.WaitAsync(stopping);
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                        timeout.CancelAfter(ProviderLimits.ControlTimeout);
                        try
                        {
                            if (adapters.ContainsKey(id))
                                await RefreshOneAsync(id, timeout.Token);
                        }
                        catch (OperationCanceledException) when (!stopping.IsCancellationRequested) { }
                    }
                    finally { ReleaseOperation(); }
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    public async Task<EndpointStatus> ConfigureAsync(EndpointUpdate input, CancellationToken cancellation, Guid? registration = null,
        bool automatic = false)
    {
        await EnterConfigurationAsync(automatic, cancellation);
        try
        {
            var current = store.Value;
            var previous = current.Endpoints.FirstOrDefault(endpoint => endpoint.Id == input.Id);
            if (input.ExpectedConfiguration is not null && (previous is null || ConfigurationRevision(previous) != input.ExpectedConfiguration))
                throw new ProviderFailure(409, "This provider changed elsewhere. Review its current settings.", code: "conflict");
            if (input.Id.HasValue && previous is null)
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            if (previous is null && current.Endpoints.Length >= ProviderLimits.MaxEndpoints)
                throw new ProviderFailure(409, "The local provider registration limit was reached.");
            if (!Enum.IsDefined(input.Profile) || !Enum.IsDefined(input.Mode))
                throw new ProviderFailure(400, "Choose a supported provider profile and management mode.");
            if (input.Token is { Length: > ProviderLimits.MaxTokenLength } ||
                input.Token?.Any(character => character < '!' || character > '~') == true ||
                input.ClearToken && !string.IsNullOrEmpty(input.Token))
                throw new ProviderFailure(400, "Enter one valid provider bearer token or clear the saved token.");
            var address = NormalizeAddress(string.IsNullOrWhiteSpace(input.Address) ? DefaultAddress(input.Profile) : input.Address);
            if (automatic && previous?.Address == address && previous.Profile == input.Profile)
                return View.Endpoints.Single(item => item.Id == previous.Id);
            if (automatic && (!new Uri(address).IsLoopback || previous is { Mode: ProviderMode.Managed } or { ProtectedToken: not null }))
                throw new ProviderFailure(412, "Approve the provider address and any credentials or process access on the owning host.");
            var endpoint = new ProviderEndpoint(previous?.Id ?? registration ?? Guid.NewGuid(), input.Profile, input.Mode, address,
                input.ClearToken ? null : !string.IsNullOrEmpty(input.Token) ? ProviderSecrets.Protect(input.Token)
                    : previous?.Address == address && previous.Profile == input.Profile ? previous.ProtectedToken : null,
                BlankToNull(input.Executable), BlankToNull(input.ModelFile), BlankToNull(input.StrataControl), BlankToNull(input.StrataModels));
            ValidateEndpoint(endpoint);
            if (previous is not null && previous != endpoint && OwnsRuntime(adapters[endpoint.Id]))
                throw new ProviderFailure(409, "Stop the process Mango started before changing this registration.");
            if (previous != endpoint && WorkInProgress(endpoint.Id))
                throw new ProviderFailure(automatic ? 423 : 409, "Wait for work on this provider to settle before changing its registration.");
            if (previous != endpoint && uncertainModels.Values.Contains(endpoint.Id))
                throw new ProviderFailure(409, "Confirm the provider has stopped the prior request before changing its registration.");
            if (current.Endpoints.Any(existing => existing.Id != endpoint.Id && existing.Address == endpoint.Address))
                throw new ProviderFailure(409, "This provider address is already registered.");
            await store.UpdateAsync(value => value with
            {
                Endpoints = value.Endpoints.Where(item => item.Id != endpoint.Id).Append(endpoint).ToArray()
            }, cancellation);
            if (previous != endpoint)
            {
                await StopWatchAsync(endpoint.Id);
                adapters[endpoint.Id] = CreateAdapter(endpoint);
                observations.Remove(endpoint.Id);
                Publish();
            }
            if (automatic)
                SignalRefresh(endpoint.Id);
            else
                await RefreshOneAsync(endpoint.Id, cancellation);
            return View.Endpoints.Single(item => item.Id == endpoint.Id);
        }
        finally { ReleaseOperation(); }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellation, bool automatic = false)
    {
        await EnterConfigurationAsync(automatic, cancellation);
        try
        {
            if (!adapters.TryGetValue(id, out var adapter))
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            if (automatic && store.Value.Endpoints.Single(item => item.Id == id) is
                { Mode: ProviderMode.Managed } or { ProtectedToken: not null })
                throw new ProviderFailure(412, "Confirm disabling this provider on the owning host.");
            if (OwnsRuntime(adapter))
                throw new ProviderFailure(409, "Stop the process Mango started before removing this registration.");
            if (WorkInProgress(id))
                throw new ProviderFailure(automatic ? 423 : 409, "Wait for work on this provider to settle before removing it.");
            if (uncertainModels.Values.Contains(id))
                throw new ProviderFailure(409, "Confirm the provider has stopped the prior request before removing it.");
            var modelIds = View.Models.Where(model => model.EndpointId == id).Select(model => model.Id).ToHashSet();
            await store.UpdateAsync(value => value with
            {
                Endpoints = value.Endpoints.Where(item => item.Id != id).ToArray(),
                Models = value.Models.Where(pair => !modelIds.Contains(pair.Key)).ToDictionary(),
                Selected = value.Selected is { } selected && modelIds.Contains(selected) ? null : value.Selected
            }, cancellation);
            adapters.TryRemove(id, out _);
            observations.Remove(id);
            await StopWatchAsync(id);
            Publish();
        }
        finally { ReleaseOperation(); }
    }

    public async Task RefreshAsync(Guid? id, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            if (id.HasValue && !adapters.ContainsKey(id.Value))
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            foreach (var endpoint in store.Value.Endpoints)
                if (!id.HasValue || id == endpoint.Id)
                    await RefreshOneAsync(endpoint.Id, cancellation);
        }
        finally { ReleaseOperation(); }
    }

    public async Task EditAsync(ModelUpdate input, CancellationToken cancellation, bool automatic = false)
    {
        await EnterConfigurationAsync(automatic, cancellation);
        try
        {
            var model = Find(input.Id);
            if (model.Options.Revision != input.Revision)
                throw new ProviderFailure(409, "This model changed. Review its current values.", code: "conflict");
            var updated = new ModelOptions(BlankToNull(input.Alias), input.Ratings ?? [],
                input.ServedContext, input.EmbeddingDimensions, BlankToNull(input.EmbeddingFamily),
                input.MediaTokensPerItem, model.Options.Revision + 1, model.Options.ServingDisabled, model.Options.ServingOverride,
                AliasExplicit: true, Yolo: model.Yolo);
            ValidateModel(updated);
            RequireCompatibleAlias(model with { Options = updated }, View.Models);
            if (ModelServing.Of(model.Options) != ModelServing.Of(updated) && WorkInProgress(model.EndpointId))
                throw new ProviderFailure(423, "Wait for work on this provider to settle before changing its serving limits.");
            await store.UpdateAsync(value => value with { Models = new(value.Models) { [model.Id] = updated } }, cancellation);
            Publish();
        }
        finally { ReleaseOperation(); }
    }

    public Task SetServingAsync(string id, bool enabled, CancellationToken cancellation) =>
        UpdateServingAsync(() => [Find(id)], enabled, cancellation);

    public async Task SetYoloAsync(string id, ModelYoloUpdate update, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            var model = Find(id);
            if (model.Options.Revision != update.Revision)
                throw new ProviderFailure(409, "This model's local settings changed. Refresh before changing YOLO mode.");
            if (update.Enabled && adapters[model.EndpointId] is StrataAdapter)
                throw new ProviderFailure(409, "Strata requires Mango's resource protection.");
            if (WorkInProgress(model.EndpointId))
                throw new ProviderFailure(423, "Wait for provider work to settle before changing YOLO mode.");
            await store.UpdateAsync(value => value with
            {
                Models = new(value.Models) { [id] = model.Options with
                    { Yolo = update.Enabled, Revision = model.Options.Revision + 1 } }
            }, cancellation);
            Publish();
        }
        finally { ReleaseOperation(); }
    }

    public Task SetProviderServingAsync(Guid id, bool enabled, CancellationToken cancellation) =>
        UpdateServingAsync(() =>
        {
            var catalog = View;
            if (!catalog.Endpoints.Any(item => item.Id == id))
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            return catalog.Models.Where(model => model.EndpointId == id).ToArray();
        }, enabled, cancellation);

    private async Task UpdateServingAsync(Func<Deployment[]> select, bool enabled, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            var changed = select().Where(model => model.Options.ServingOverride != enabled).ToArray();
            if (changed.Length == 0) return;
            await store.UpdateAsync(value =>
            {
                var models = new Dictionary<string, ModelOptions>(value.Models);
                foreach (var model in changed)
                    models[model.Id] = model.Options with
                    {
                        ServingDisabled = !enabled, ServingOverride = enabled, Revision = model.Options.Revision + 1
                    };
                return value with { Models = models, Selected = !enabled && changed.Any(model => model.Id == value.Selected) ? null : value.Selected };
            }, cancellation);
            Publish();
        }
        finally { ReleaseOperation(); }
    }

    public async Task ApplyRatingsAsync(string alias, Dictionary<string, int?> ratings, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            var matches = View.Models.Where(item => item.Alias is not null && SameAlias(item.Alias, alias)).ToArray();
            if (matches.Length == 0)
                throw new ProviderFailure(404, "No deployments have this alias.");
            var updated = new Dictionary<string, ModelOptions>(store.Value.Models);
            foreach (var model in matches)
            {
                var values = new Dictionary<string, int?>(model.Options.Ratings, StringComparer.Ordinal);
                foreach (var rating in ratings)
                    values[rating.Key] = rating.Value;
                var next = model.Options with { Ratings = values, Revision = model.Options.Revision + 1 };
                ValidateModel(next);
                updated[model.Id] = next;
            }
            await store.UpdateAsync(value => value with { Models = updated }, cancellation);
            Publish();
        }
        finally { ReleaseOperation(); }
    }

    public async Task SelectAsync(string? id, CancellationToken cancellation)
    {
        await operation.WaitAsync(cancellation);
        try
        {
            if (id is not null)
            {
                var model = Find(id);
                RequireCompatibleAlias(model, View.Models);
                if (!model.Eligible)
                    throw new ProviderFailure(409, "Choose a loaded model with a verified or configured limit.");
            }
            await store.UpdateAsync(value => value with { Selected = id }, cancellation);
            Publish();
        }
        finally { ReleaseOperation(); }
    }

    public async Task ManageAsync(string id, bool load, CancellationToken cancellation, InferenceOperation? benchmark = null,
        InferenceOperation? requested = null, bool scheduled = false, Func<string, Task>? displaced = null)
    {
        if (benchmark.HasValue)
            await operation.WaitAsync(cancellation);
        else if (!await operation.WaitAsync(0, cancellation))
            throw new ProviderFailure(409, "A provider operation is in progress.");
        await ManageUnderGateAsync(id, load, cancellation, benchmark, requested, scheduled, displaced);
    }

    private async Task ManageUnderGateAsync(string id, bool load, CancellationToken cancellation,
        InferenceOperation? benchmark = null, InferenceOperation? requested = null, bool scheduled = false,
        Func<string, Task>? displaced = null, bool switching = false)
    {
        ResourceReservation? reservation = null;
        Deployment? target = null;
        ExclusiveLoad? loading = null;
        CancellationTokenSource? stop = null;
        var invoked = false;
        var confirmed = false;
        try
        {
            var model = Find(id);
            target = model;
            if (scheduled && resources?.ForcedBenchmarkHoldsServing == true)
                throw new ProviderFailure(503, ResourceProtection.ForcedBenchmarkWait, capacityWait: true);
            if (load && (scheduled || benchmark.HasValue) && model.ServingDisabled)
                throw new ProviderFailure(409, "Serving is disabled for this model.");
            var adapter = adapters[model.EndpointId];
            if (load && !model.Model.SupportsLoad || !load && !model.Model.SupportsUnload)
                throw new ProviderFailure(409, "The provider does not expose this lifecycle operation for the deployment.");
            if (load && !model.Model.Installed)
                throw new ProviderFailure(409, "This model is not confirmed installed.");
            if (load && model.Instance is not null)
                throw new ProviderFailure(409, "This deployment is already loaded.");
            if (!load && model.Instance is null && !adapter.OwnedProcessId.HasValue)
                throw new ProviderFailure(409, "The model is already unloaded or its state is unknown.");
            var endpoint = View.Endpoints.First(item => item.Id == model.EndpointId);
            var operationKind = benchmark ?? requested ?? resources?.LoadOperation(model, endpoint);
            var modeSwitch = load && NeedsModeSwitch(View, model);
            if (modeSwitch && (endpoint.Mode != ProviderMode.Managed || !endpoint.CanLoad))
                throw new ProviderFailure(409, "The provider does not authorize loading this deployment.");
            if (load && resources is not null && !benchmark.HasValue && !model.Yolo &&
                (modeSwitch || adapter is StrataAdapter))
            {
                if (resources.Status is { State: not ContributionState.Contributor } status &&
                    !(modeSwitch && resources.CanPrepareModeSwitch))
                    throw new ProviderFailure(503, status.Reason ?? "Turn on contribution before loading the model.");
                var budget = resources.Estimate(model, endpoint, operationKind!.Value);
                if (budget is null || budget.Load.RamBytes == 0 && budget.Load.DeviceBytes.Values.All(bytes => bytes == 0))
                    throw new ProviderFailure(503, "Set a peak memory allowance before loading this model.");
            }
            if (load && adapter is StrataAdapter)
            {
                if (!activeModels.IsEmpty)
                    throw new ProviderFailure(503, "Waiting for active work before reserving this computer for Strata.", capacityWait: true);
                stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                exclusiveLoad = loading = new(model.EndpointId, stop, DateTimeOffset.UtcNow);
                cancellation = stop.Token;
            }
            if (load)
                await PrepareResidencyAsync(model, displaced, cancellation, loading);
            if (WorkInProgress(model.EndpointId, includeUncertain: load))
                throw new ProviderFailure(409, "Work on this provider must settle before changing model state.");
            if (load && (benchmark.HasValue ? HasUnsettledWork(endpoint) : !uncertainModels.IsEmpty))
                throw new ProviderFailure(409, "Mango has not confirmed that the prior request stopped.");
            if (load && resources is not null)
            {
                reservation = resources.TryAdmit(model, endpoint, new(operationKind!.Value, 1), ResourceWork.Load,
                    profiling: benchmark.HasValue).Require();
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation,
                reservation?.Cancellation ?? CancellationToken.None);
            var started = Stopwatch.GetTimestamp();
            invoked = true;
            if (load)
            {
                await adapter.LoadAsync(model.Model, model.Yolo ? null : model.Options.ServedContext, linked.Token);
                if (adapter.OwnedProcessId.HasValue)
                    Publish();
            }
            else
                await adapter.UnloadAsync(model.Model, cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            try
            {
                do
                {
                    await RefreshOneAsync(model.EndpointId, timeout.Token);
                    if (load && observations.GetValueOrDefault(model.EndpointId)?.AllocationFailed == true)
                        throw new ProviderFailure(503, "The provider reported a failed memory allocation during load.", true);
                    var observedEndpoint = View.Endpoints.First(status => status.Id == model.EndpointId);
                    var observed = View.Models.FirstOrDefault(item => item.Id == id);
                    if (load ? observedEndpoint.Online && observed?.Instance is not null
                        : observedEndpoint.Online && observed?.Instance is null ||
                          !observedEndpoint.Online && !adapter.OwnedProcessId.HasValue &&
                          observedEndpoint.Profile is ProviderProfile.LlamaSingle or ProviderProfile.LlamaEmbedding)
                    {
                        Volatile.Write(ref lastLifecycle, new(id, load, Stopwatch.GetElapsedTime(started)));
                        reservation?.Complete();
                        if (!load && adapter is StrataAdapter)
                            SettleReleased(model.EndpointId);
                        confirmed = true;
                        return;
                    }
                    await Task.Delay(500, timeout.Token);
                } while (true);
            }
            catch (OperationCanceledException) when (!linked.IsCancellationRequested)
            {
                throw new ProviderFailure(502, "Mango could not confirm the requested model state.");
            }
        }
        catch (ProviderFailure error) when (error.Status is >= 500 or 409)
        {
            if (invoked)
                LifecycleFailed?.Invoke(id, load, error.Status);
            if (load && error.Code == "load_rejected")
            {
                reservation?.ReleaseUnstarted();
                reservation = null;
                invoked = false;
            }
            if (target is not null)
                SignalRefresh(target.EndpointId);
            if (error.AllocationFailure && reservation is not null)
            {
                await resources!.AllocationFailedAsync(reservation);
                if (View.Endpoints.First(item => item.Id == target!.EndpointId).Executable is not null &&
                    !adapters[target!.EndpointId].OwnedProcessId.HasValue &&
                    (resources.Observation?.Workers.GetValueOrDefault(target.EndpointId)?.Processes
                        .All(pid => !WindowsObservation.ProcessExists(pid)) ?? true))
                    reservation.Complete();
            }
            throw;
        }
        catch (OperationCanceledException)
        {
            if (target is not null)
                SignalRefresh(target.EndpointId);
            if (!cancellation.IsCancellationRequested && reservation?.Cancellation.IsCancellationRequested == true)
            {
                if (invoked)
                    LifecycleFailed?.Invoke(id, load, 503);
                throw new ProviderFailure(503, "Mango withdrew local resources during model loading.");
            }
            throw;
        }
        catch (ProviderFailure error) when (invoked)
        {
            LifecycleFailed?.Invoke(id, load, error.Status);
            throw;
        }
        catch (Exception error) when (invoked && error is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        {
            LifecycleFailed?.Invoke(id, load, 502);
            throw;
        }
        finally
        {
            if (load && !confirmed && invoked && target is not null && adapters[target.EndpointId] is StrataAdapter { Reserved: true } failed)
            {
                using var cleanup = new CancellationTokenSource(StrataAdapter.ReleaseTimeout);
                try { await ReleaseStrataAsync(target.EndpointId, failed, cleanup.Token); }
                catch (Exception error) when (error is ProviderFailure or OperationCanceledException) { }
            }
            if (reservation is not null)
            {
                if (!invoked)
                    reservation.Complete();
                else if (reservation.Running && target is not null)
                {
                    if (adapters[target.EndpointId] is StrataAdapter { Reserved: false })
                        reservation.Complete();
                    else
                    {
                        uncertainModels[id] = target.EndpointId;
                        if (load) uncertainLoads[id] = false;
                        Publish();
                        SignalRefresh(target.EndpointId);
                    }
                }
                reservation.Dispose();
            }
            if (loading is not null)
            {
                exclusiveLoad = null;
                stop!.Dispose();
                if (View.Endpoints.Any(endpoint => endpoint.Id == loading.Endpoint && endpoint.LoadingSince.HasValue))
                    Publish();
            }
            if (!switching)
            {
                if (scheduled && !invoked)
                    operation.Release();
                else
                    ReleaseOperation();
            }
        }
    }

    private static bool OwnsRuntime(ProviderAdapter adapter) =>
        adapter is StrataAdapter strata ? strata.Reserved : adapter.OwnedProcessId.HasValue;

    internal static string? ExclusiveBlocker(CatalogView snapshot, EndpointStatus endpoint)
    {
        if (snapshot.Endpoints.Any(other => other.Id != endpoint.Id && other.HostReserved))
            return "Waiting for Strata to release this computer.";
        if (endpoint.ExclusiveHost && snapshot.Models.Any(model =>
            model.Instance is not null && model.Model.Origin != ModelOrigin.LmLink &&
            snapshot.Endpoints.Any(other => other.Id == model.EndpointId && other.Mode == ProviderMode.Attached)))
            return AttachedStrataBlocker;
        return null;
    }

    private const string AttachedStrataBlocker = "Unload the attached provider's model or let Mango manage that provider before starting Strata.";

    private static bool DifferentLocalMode(Deployment first, Deployment second) =>
        first.Yolo != second.Yolo && first.Model.Origin != ModelOrigin.LmLink && second.Model.Origin != ModelOrigin.LmLink;

    internal static bool NeedsModeSwitch(CatalogView snapshot, Deployment target) =>
        snapshot.Models.Any(model => model.Instance is not null && DifferentLocalMode(model, target));

    private bool OtherModeHasWork(CatalogView snapshot, Deployment target) =>
        snapshot.Models.Any(model => DifferentLocalMode(model, target) && (model.Busy || HasUnsettled(model.Id)));

    internal const string ModeSwitchWait = "Waiting for the other resource mode to release its resident models.";

    private async Task PrepareResidencyAsync(Deployment target, Func<string, Task>? displaced,
        CancellationToken cancellation, ExclusiveLoad? loading = null)
    {
        var snapshot = View;
        if (OtherModeHasWork(snapshot, target))
            throw new ProviderFailure(503, ModeSwitchWait, capacityWait: true);
        var residents = snapshot.Models.Where(model => model.Instance is not null && DifferentLocalMode(model, target)).ToArray();
        if (residents.Length != 0 && resources is { CanPrepareModeSwitch: false })
            throw new ProviderFailure(503, ModeSwitchWait, capacityWait: true);
        foreach (var resident in residents)
        {
            var owner = snapshot.Endpoints.First(endpoint => endpoint.Id == resident.EndpointId);
            if (owner.Mode != ProviderMode.Managed || !owner.CanUnload || !owner.CanLoad ||
                !resident.Model.SupportsUnload || !resident.Model.SupportsLoad)
                throw new ProviderFailure(503, $"Mango cannot swap {resident.Model.DisplayName} because its provider cannot unload and reload it.",
                    code: "mode_switch_lifecycle");
            if (resident.Busy || resident.Model.Instances.Any(instance => instance.Busy) ||
                WorkInProgress(owner.Id) || HasUnsettledWork(owner) ||
                await adapters[owner.Id].CheckIdleAsync(resident.Model, cancellation) == false)
                throw new ProviderFailure(503, ModeSwitchWait, capacityWait: true);
        }
        await PrepareExclusiveAsync(target, displaced, cancellation, loading);
        if (residents.Length == 0)
            return;
        foreach (var resident in residents)
        {
            if (!View.Models.Any(model => model.Id == resident.Id && model.Instance is not null))
                continue;
            await ManageUnderGateAsync(resident.Id, false, cancellation, switching: true);
            if (displaced is not null)
                await displaced(resident.Id);
        }
        if (NeedsModeSwitch(View, target))
            throw new ProviderFailure(503, ModeSwitchWait, capacityWait: true);
        if (resources is not null)
            await resources.ObserveModeSwitchAsync(cancellation);
    }

    private async Task PrepareExclusiveAsync(Deployment target, Func<string, Task>? displaced,
        CancellationToken cancellation, ExclusiveLoad? loading = null)
    {
        var exclusive = adapters[target.EndpointId] is StrataAdapter;
        var held = View.Endpoints.Where(endpoint => endpoint.HostReserved &&
            (endpoint.Id != target.EndpointId || exclusive && target.Instance is null)).ToArray();
        if (!exclusive && held.Length == 0)
            return;
        if (!activeModels.IsEmpty)
            throw new ProviderFailure(503, "Waiting for active work before switching providers on this computer.", capacityWait: true);
        if (loading is not null)
            Publish();
        foreach (var endpoint in held)
        {
            var residents = View.Models.Where(model => model.EndpointId == endpoint.Id && model.Instance is not null).ToArray();
            await ReleaseStrataAsync(endpoint.Id, (StrataAdapter)adapters[endpoint.Id], cancellation);
            foreach (var resident in residents)
                if (displaced is not null) await displaced(resident.Id);
        }
        if (!exclusive)
            return;

        var others = View.Endpoints.Where(endpoint => !endpoint.ExclusiveHost).ToArray();
        foreach (var endpoint in others)
            await RefreshOneAsync(endpoint.Id, cancellation);
        var snapshot = View;
        var residentsToDrain = snapshot.Models.Where(model => model.EndpointId != target.EndpointId &&
            model.Instance is not null && model.Model.Origin != ModelOrigin.LmLink).ToArray();
        foreach (var resident in residentsToDrain)
        {
            var owner = snapshot.Endpoints.First(endpoint => endpoint.Id == resident.EndpointId);
            if (owner.Mode == ProviderMode.Attached)
                throw new ProviderFailure(503, AttachedStrataBlocker, code: "strata_attached_resident");
            if (resident.Busy || resident.Instance!.Busy || uncertainModels.Count != 0 || !owner.CanUnload || !resident.Model.SupportsUnload)
                throw new ProviderFailure(503, "Waiting for the managed provider to become idle and release its model.", capacityWait: true);
        }
        foreach (var resident in residentsToDrain)
        {
            if (View.Models.Any(model => model.Id == resident.Id && model.Instance is not null))
                await ManageUnderGateAsync(resident.Id, false, cancellation, switching: true);
            if (displaced is not null) await displaced(resident.Id);
        }
        if (others.Any(endpoint => adapters[endpoint.Id].OwnedProcessId.HasValue) ||
            View.Models.Any(model => model.EndpointId != target.EndpointId && model.Instance is not null && model.Model.Origin != ModelOrigin.LmLink))
            throw new ProviderFailure(503, "Mango could not verify that all other providers released this computer.", capacityWait: true);
        if (residentsToDrain.Length != 0 && resources is not null)
            await resources.WaitForObservationAsync(cancellation);
    }

    private async Task ReleaseStrataAsync(Guid id, StrataAdapter strata, CancellationToken cancellation)
    {
        await strata.StopAsync(cancellation);
        SettleReleased(id);
        await RefreshOneAsync(id, cancellation);
        if (resources is not null)
            await resources.WaitForObservationAsync(cancellation);
    }

    private void SettleReleased(Guid endpoint)
    {
        foreach (var item in uncertainModels.Where(item => item.Value == endpoint))
            resources?.ConfirmSettled(item.Key);
        ConfirmReleased(endpoint);
    }

    private async Task ReleaseIdleStrataAsync(CancellationToken cancellation)
    {
        if (!await operation.WaitAsync(0, cancellation))
            return;
        try
        {
            foreach (var endpoint in View.Endpoints.Where(item => item.HostReserved))
                if (adapters[endpoint.Id] is StrataAdapter strata && strata.IsIdleExpired &&
                    !WorkInProgress(endpoint.Id, includeUncertain: false))
                    try { await ReleaseStrataAsync(endpoint.Id, strata, cancellation); }
                    catch (ProviderFailure) { }
        }
        finally { ReleaseOperation(); }
    }

    public async Task StopAsync(Guid id, CancellationToken cancellation)
    {
        if (exclusiveLoad is { } loading && loading.Endpoint == id)
        {
            try { await loading.Stop.CancelAsync(); }
            catch (ObjectDisposedException) { }
            await operation.WaitAsync(cancellation);
        }
        else if (!await operation.WaitAsync(0, cancellation))
            throw new ProviderFailure(409, "A provider operation is in progress.");
        try
        {
            if (!adapters.TryGetValue(id, out var adapter))
                throw new ProviderFailure(404, "Provider endpoint does not exist.");
            if (WorkInProgress(id, includeUncertain: false))
                throw new ProviderFailure(409, "Wait for active work before stopping this provider.");
            if (adapter is StrataAdapter strata)
                await ReleaseStrataAsync(id, strata, cancellation);
            else
            {
                await adapter.StopAsync(cancellation);
                await RefreshOneAsync(id, cancellation);
            }
        }
        finally { ReleaseOperation(); }
    }

    public async Task<InferenceRequest> ReadInferenceAsync(InferenceOperation operationKind, HttpContext context,
        CancellationToken cancellation)
    {
        if (!await input.WaitAsync(0, cancellation))
            throw new ProviderFailure(429, "The local request input limit is busy.");
        try { return await InferenceRequest.ReadAsync(operationKind, context.Request, cancellation); }
        finally { input.Release(); }
    }

    public ServingPlacement? Placement(InferenceShape shape, string id, bool allowUnverifiedContext = true)
    {
        var snapshot = View;
        var model = snapshot.Models.FirstOrDefault(item => item.Id == id);
        return model is null ? null : Placement(shape, model, snapshot, allowUnverifiedContext);
    }

    private ServingPlacement? Placement(InferenceShape shape, Deployment model, CatalogView snapshot, bool allowUnverifiedContext)
    {
        if (model.ServingDisabled || !model.AcceptsIdentifier(shape.Alias) || !model.Model.Installed || !model.Model.Operations.Contains(shape.Operation) ||
            !shape.Fits(model.Model.Inputs, model.Model.ClientTools, model.Model.StructuredOutput, model.Context,
                model.MediaTokensPerItem, model.Model.EmbeddingEncodings, allowUnverifiedContext))
            return null;
        var endpoint = snapshot.Endpoints.First(item => item.Id == model.EndpointId);
        if (!model.Eligible && !(endpoint.Mode == ProviderMode.Managed && endpoint.CanLoad && model.Model.SupportsLoad))
            return null;
        var workload = shape.Demand(model.Context, model.Instance is null
            ? resources?.QualifiedUnits(model, endpoint, shape.Operation) : null);
        var budget = resources?.Estimate(model, endpoint, shape.Operation);
        if (resources is not null && ((!model.Yolo && (budget is null || !ResourceProtection.Covers(budget.Workload, workload))) ||
            !resources.IsAvailable(model, endpoint, shape.Operation, NeedsModeSwitch(snapshot, model))))
            return null;
        return new(model, endpoint, budget, workload);
    }

    public bool Conflicts(ServingPlacement a, ServingPlacement b) => DifferentLocalMode(a.Model, b.Model) || a.Endpoint.Id == b.Endpoint.Id ||
        a.Endpoint.ExclusiveHost || b.Endpoint.ExclusiveHost ||
        resources is null || !resources.CanBackfill(a, b);

    public bool NeedsPreparation(ServingPlacement placement)
    {
        var current = View;
        return placement.Model.Instance is null || NeedsModeSwitch(current, placement.Model) ||
            current.Endpoints.Any(endpoint => endpoint.Id != placement.Endpoint.Id && endpoint.HostReserved);
    }

    internal bool WouldDisplace(ServingPlacement placement)
    {
        var current = View;
        var endpoint = placement.Endpoint;
        if (NeedsModeSwitch(current, placement.Model) || current.Endpoints.Any(other => other.Id != endpoint.Id && other.HostReserved))
            return true;
        if (placement.Model.Instance is not null)
            return false;
        var residents = current.Models.Where(model => model.Instance is not null && model.Model.Origin != ModelOrigin.LmLink).ToArray();
        return endpoint.ExclusiveHost && residents.Length != 0 ||
            endpoint.MaxLoadedModels is { } capacity && residents.Count(model => model.EndpointId == endpoint.Id) >= capacity;
    }

    public async Task PreparePlacementAsync(ServingPlacement placement, Func<string, Task> displaced,
        CancellationToken cancellation)
    {
        if (resources?.ForcedBenchmarkHoldsServing == true)
            throw new ProviderFailure(503, ResourceProtection.ForcedBenchmarkWait, capacityWait: true);
        var target = placement.Model;
        var operationKind = placement.Workload.Operation;
        if (target.Instance is not null)
        {
            if (!await operation.WaitAsync(0, cancellation))
                throw new ProviderFailure(409, "A provider operation is in progress.");
            try { await PrepareResidencyAsync(Find(target.Id), displaced, cancellation); }
            finally { ReleaseOperation(); }
            return;
        }
        if (placement.Endpoint.ExclusiveHost || target.Yolo)
        {
            await ManageAsync(target.Id, true, cancellation, requested: operationKind, scheduled: true, displaced: displaced);
            return;
        }
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var current = View;
            var idle = current.Models.Where(item => !item.Yolo && item.Id != target.Id && item.Instance is { Busy: false } &&
                    item.Model.SupportsUnload && !item.Busy && current.Endpoints.Any(endpoint => endpoint.Id == item.EndpointId &&
                        endpoint.Mode == ProviderMode.Managed && endpoint.CanUnload))
                .OrderByDescending(item => item.EndpointId == target.EndpointId).ThenByDescending(item => item.Model.ArtifactBytes)
                .ToArray();
            var endpointFull = placement.Endpoint.MaxLoadedModels is { } max && current.Models.Count(item =>
                item.EndpointId == target.EndpointId && item.Instance is not null && !DifferentLocalMode(item, target)) >= max;
            var victim = idle.FirstOrDefault(item => item.EndpointId == target.EndpointId && endpointFull);
            if (endpointFull && victim is null)
                throw new ProviderFailure(503, "Waiting for a model slot to become idle.", capacityWait: true);
            if (victim is null)
            {
                try
                {
                    await ManageAsync(target.Id, true, cancellation, requested: operationKind, scheduled: true, displaced: displaced);
                    return;
                }
                catch (ProviderFailure error) when (error.MemoryWait)
                {
                    victim = idle.FirstOrDefault(item => resources?.CanReleaseForLoad(placement, item.EndpointId) == true);
                    if (victim is null)
                        throw;
                }
            }
            await ManageAsync(victim.Id, false, cancellation, scheduled: true);
            await displaced(victim.Id);
            if (resources is not null) await resources.WaitForObservationAsync(cancellation);
        }
    }

    public StartExecution TryStart(InferenceRequest payload, string id)
    {
        if (!operation.Wait(0))
            return new(AdmissionStatus.Wait, "A provider operation is in progress.");
        try
        {
            var placement = Placement(payload.Shape, id);
            if (placement is null)
                return new(AdmissionStatus.Unavailable, "The local deployment cannot serve this request.");
            var model = placement.Model;
            var snapshot = View;
            if (NeedsModeSwitch(snapshot, model) || OtherModeHasWork(snapshot, model))
                return new(AdmissionStatus.Wait, ModeSwitchWait);
            if (ExclusiveBlocker(snapshot, placement.Endpoint) is { } blocker)
                return new(AdmissionStatus.Wait, blocker);
            if (HasUnsettled(id) || !model.Eligible || model.Instance is { Busy: true } || resources is null && !activeModels.IsEmpty)
                return new(AdmissionStatus.Wait, "The deployment is loading or busy.");
            ResourceReservation? reservation = null;
            if (resources is not null)
            {
                var admission = resources.TryAdmit(model, placement.Endpoint, placement.Workload, ResourceWork.Execute,
                    payloadBytes: payload.Bytes);
                if (admission.Reservation is null)
                    return new(admission.Status, admission.Reason);
                reservation = admission.Reservation;
            }
            var adapter = adapters[model.EndpointId];
            MarkActive(model, true);
            return new(AdmissionStatus.Admitted, null,
                (context, cancellation) => ExecuteReservedAsync(payload, model, adapter, reservation, context, cancellation), model.PlacementEpoch);
        }
        finally { operation.Release(); }
    }

    private async Task<ExecutionResult> ExecuteReservedAsync(InferenceRequest payload, Deployment model, ProviderAdapter adapter,
        ResourceReservation? reservation, HttpContext context, CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation,
            reservation?.Cancellation ?? CancellationToken.None);
        ProviderExecution? execution = null;
        ProviderFailure? failure = null;
        bool settled;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            execution = await payload.ForwardAsync(model, adapter, context, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            Volatile.Write(ref lastExecution, execution);
        }
        catch (ProviderFailure error)
        {
            if (error.Status is >= 500 or 409 || error.AllocationFailure)
                SignalRefresh(model.EndpointId);
            if (error.AllocationFailure && reservation is not null)
                await resources!.AllocationFailedAsync(reservation);
            failure = error.Status == 429 ? new(503, error.Message) : error;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or HttpRequestException)
        {
            SignalRefresh(model.EndpointId);
            failure = error is OperationCanceledException ? new(503, "Inference was cancelled.") :
                new(502, "The provider connection failed during inference.");
        }
        finally
        {
            settled = await FinishExecutionAsync(model, adapter, reservation, !payload.Submitted || payload.Settled);
        }
        return new(settled, failure, execution);
    }

    internal async Task<BenchmarkResult> BenchmarkAsync(string id, Workload workload, string pass, string runtime,
        CancellationToken cancellation)
    {
        Deployment model;
        ProviderAdapter adapter;
        ResourceReservation reservation;
        await operation.WaitAsync(cancellation);
        try
        {
            model = Find(id);
            var endpoint = View.Endpoints.First(item => item.Id == model.EndpointId);
            if (ExclusiveBlocker(View, endpoint) is { } blocker)
                throw new ProviderFailure(409, blocker);
            if (!model.Eligible || model.Instance?.ResourceFingerprint != runtime || model.Busy ||
                model.Instance?.Busy == true || uncertainModels.ContainsKey(id))
                throw new ProviderFailure(409, "The benchmark deployment is not settled and ready.");
            var snapshot = View;
            if (NeedsModeSwitch(snapshot, model) || OtherModeHasWork(snapshot, model))
                await PrepareResidencyAsync(model, null, cancellation);
            adapter = adapters[model.EndpointId];
            reservation = resources!.TryAdmit(model, endpoint,
                workload, ResourceWork.Benchmark, profiling: true, speedSample: pass == BenchmarkProtocol.SpeedPass).Require();
            MarkActive(model, true);
        }
        finally { ReleaseOperation(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, reservation.Cancellation);
        var submitted = false;
        var settled = false;
        BenchmarkResult? result = null;
        var log = BeginBenchmarkLog?.Invoke(id, workload, pass);
        try
        {
            result = await BenchmarkProtocol.RunAsync(model, adapter, workload, pass, () => submitted = true,
                () => settled = true, linked.Token);
            return result;
        }
        catch (ProviderFailure error) when (error.AllocationFailure)
        {
            await resources!.AllocationFailedAsync(reservation);
            throw;
        }
        finally
        {
            log?.Invoke(result);
            await FinishExecutionAsync(model, adapter, reservation, !submitted || settled);
        }
    }

    private async Task<bool> FinishExecutionAsync(Deployment model, ProviderAdapter adapter, ResourceReservation? reservation,
        bool settled)
    {
        settled = settled || await ObserveIdleAsync(adapter, model.Model, wait: true, CancellationToken.None);
        await operation.WaitAsync(CancellationToken.None);
        try
        {
            MarkActive(model, false);
            if (adapter is StrataAdapter strata)
                strata.Touch();
            reservation?.Complete(settled);
            if (!settled)
            {
                uncertainModels[model.Id] = model.EndpointId;
                Publish();
                SignalRefresh(model.EndpointId);
            }
        }
        finally { ReleaseOperation(); }
        return settled;
    }

    private void ReleaseOperation()
    {
        operation.Release();
        AdmissionChanged?.Invoke();
    }

    private async Task EnterConfigurationAsync(bool automatic, CancellationToken cancellation)
    {
        if (!automatic)
            await operation.WaitAsync(cancellation);
        else if (!await operation.WaitAsync(0, cancellation))
            throw new ProviderFailure(423, "Waiting for the current provider operation to finish.");
    }

    private bool WorkInProgress(Guid endpoint, bool includeUncertain = true) =>
        activeModels.Values.Any(item => item.Endpoint == endpoint) ||
        (includeUncertain ? resources?.HasWork(endpoint) : resources?.HasRunning(endpoint)) == true;

    private void MarkActive(Deployment model, bool active)
    {
        var count = activeModels.GetValueOrDefault(model.Id).Count + (active ? 1 : -1);
        if (count > 0)
            activeModels[model.Id] = (model.EndpointId, count);
        else
            activeModels.TryRemove(model.Id, out _);
    }

    internal async Task<ReleaseResult> ReleaseManagedAsync(CancellationToken cancellation, bool includeYolo = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(View.Endpoints.Any(endpoint => endpoint.HostReserved) ? StrataAdapter.ReleaseTimeout : TimeSpan.FromSeconds(25));
        var released = new List<ReleasedEndpoint>();
        try
        {
            while (activeModels.Keys.Any(id => includeYolo || !IsYolo(id)) ||
                View.Endpoints.Any(item => resources?.HasRunning(item.Id, includeYolo) == true))
                await Task.Delay(100, deadline.Token);
            await operation.WaitAsync(deadline.Token);
            try
            {
                foreach (var endpoint in View.Endpoints.Where(item => item.Mode == ProviderMode.Managed).ToArray())
                {
                    var adapter = adapters[endpoint.Id];
                    if (adapter is StrataAdapter strata)
                    {
                        if (strata.Reserved)
                        {
                            await ReleaseStrataAsync(endpoint.Id, strata, deadline.Token);
                            released.Add(new(endpoint.Id, null));
                        }
                        continue;
                    }
                    var ownedPid = adapter.OwnedProcessId;
                    var owned = ownedPid.HasValue && (includeYolo || !View.Models.Any(model => model.EndpointId == endpoint.Id && model.Yolo));
                    if (!owned)
                        await RefreshOneAsync(endpoint.Id, deadline.Token);
                    while (!owned && uncertainModels.Any(item => item.Value == endpoint.Id && (includeYolo || !IsYolo(item.Key)) &&
                        uncertainLoads.ContainsKey(item.Key) &&
                        !View.Models.Any(model => model.Id == item.Key && model.Instance is not null)))
                    {
                        // An absent instance cannot prove that a canceled provider load stopped.
                        await Task.Delay(250, deadline.Token);
                        await RefreshOneAsync(endpoint.Id, deadline.Token);
                    }
                    var models = View.Models.Where(item => item.EndpointId == endpoint.Id && item.Instance is not null &&
                        item.Model.Origin != ModelOrigin.LmLink && (includeYolo || !item.Yolo)).ToArray();
                    if (!owned && !View.Endpoints.Any(item => item.Id == endpoint.Id && item.Online))
                        return new(released.ToArray(), "Mango could not verify that the managed provider released its models.");
                    if (models.Length == 0 && !owned && !uncertainModels.Any(item => item.Value == endpoint.Id && (includeYolo || !IsYolo(item.Key))))
                        continue;
                    using var unload = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    unload.CancelAfter(TimeSpan.FromSeconds(6));
                    try
                    {
                        foreach (var model in models)
                            await adapter.UnloadAsync(model.Model, unload.Token);
                    }
                    catch (Exception error) when (error is ProviderFailure or OperationCanceledException or System.ComponentModel.Win32Exception)
                    {
                        // An unload may complete after its short response deadline. Verify with
                        // the remaining release deadline below; never infer release from an error.
                    }
                    if (owned)
                        await adapter.StopAsync(deadline.Token);
                    await RefreshOneAsync(endpoint.Id, deadline.Token, settleOwnedExit: !owned);
                    var refreshed = View;
                    if (!owned && models.Length != 0 && refreshed.Endpoints.Any(item => item.Id == endpoint.Id && item.Online) &&
                        !refreshed.Models.Any(item => item.EndpointId == endpoint.Id && item.Instance is not null &&
                            item.Model.Origin != ModelOrigin.LmLink && (includeYolo || !item.Yolo)))
                        ConfirmReleased(endpoint.Id, includeYolo);
                    if ((!owned && !refreshed.Endpoints.Any(item => item.Id == endpoint.Id && item.Online)) ||
                        refreshed.Models.Any(item => item.EndpointId == endpoint.Id && item.Instance is not null && item.Model.Origin != ModelOrigin.LmLink && (includeYolo || !item.Yolo)) ||
                        !owned && uncertainModels.Any(item => item.Value == endpoint.Id && (includeYolo || !IsYolo(item.Key))))
                        return new(released.ToArray(), "Mango could not verify that the managed provider released its models.");
                    released.Add(new(endpoint.Id, owned ? ownedPid : null));
                }
                var remaining = uncertainModels.Where(item => includeYolo || !IsYolo(item.Key)).Select(item => item.Value)
                    .Distinct().Except(released.Select(item => item.Id)).ToArray();
                foreach (var endpoint in remaining)
                    await RefreshOneAsync(endpoint, deadline.Token);
                if (uncertainModels.Where(item => includeYolo || !IsYolo(item.Key)).Select(item => item.Value).Except(released.Select(item => item.Id)).Any())
                    return new(released.ToArray(), "An attached provider has not confirmed that its request stopped.", AwaitingStop: true);
                return new(released.ToArray());
            }
            finally { ReleaseOperation(); }
        }
        catch (Exception error) when (error is ProviderFailure or IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return new(released.ToArray(), "Mango could not settle and release every managed provider before the cancellation deadline.");
        }
    }

    public void RequestSettlementRefresh(string deployment)
    {
        if (uncertainModels.TryGetValue(deployment, out var endpoint))
            SignalRefresh(endpoint);
    }

    private void SignalRefresh(Guid id)
    {
        pendingRefreshes.TryAdd(id, 0);
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RefreshOneAsync(Guid id, CancellationToken cancellation, bool settleOwnedExit = true)
    {
        try
        {
            var adapter = adapters[id];
            var result = await adapter.InspectAsync(cancellation);
            observations[id] = new(result, null, DateTimeOffset.UtcNow);
            if (adapter is StrataAdapter { Reserved: false })
                SettleReleased(id);
            if (adapter is LlamaAdapter { HasInventoryEvents: true } llama && !watchers.ContainsKey(id))
            {
                var stop = new CancellationTokenSource();
                watchers[id] = (stop, llama.WatchInventoryAsync(() => SignalRefresh(id), stop.Token));
            }
            foreach (var key in uncertainModels.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
            {
                var model = result.Models.FirstOrDefault(model => model.Id == key);
                var loading = uncertainLoads.TryGetValue(key, out var restored);
                if ((!loading || restored && result.LoadsSettled) && (model is null || model.Instances.Length == 0) ||
                    model is { Instances.Length: > 0 } && await ObserveIdleAsync(adapter, model, wait: false, cancellation))
                {
                    uncertainModels.TryRemove(key, out _);
                    uncertainLoads.TryRemove(key, out _);
                    resources?.ConfirmSettled(key);
                }
            }
        }
        catch (ProviderFailure error)
        {
            var previous = observations.GetValueOrDefault(id)?.Inventory;
            var adapter = adapters[id];
            var fallback = adapter is LlamaAdapter { ConfiguredModel: { } configured }
                ? new ProviderInventory(previous?.Version,
                    [previous?.Models.FirstOrDefault(model => model.Id == configured.Id) is { } last
                        ? last with { Instances = [], Installed = configured.Installed } : configured], true, adapter.OwnedProcessId.HasValue,
                    adapter.OwnedProcessId.HasValue, adapter.OwnedProcessId)
                : previous ?? new ProviderInventory(null, [], false, false, false);
            observations[id] = new(fallback, error.Message, DateTimeOffset.UtcNow, error.AllocationFailure,
                error.Status is 401 or 403);
            if (adapter is not StrataAdapter && settleOwnedExit && fallback.CanLoad && !adapter.OwnedProcessId.HasValue && previous?.CanStop == true &&
                (resources?.Observation?.Workers.GetValueOrDefault(id)?.Processes.All(pid => !WindowsObservation.ProcessExists(pid)) ?? true))
                foreach (var key in uncertainModels.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
                {
                    uncertainModels.TryRemove(key, out _);
                    resources?.ConfirmSettled(key);
                }
        }
        Publish();
    }

    private void Publish()
    {
        var value = store.Value;
        var previous = Volatile.Read(ref view);
        var endpoints = new List<EndpointStatus>();
        var models = new List<Deployment>();
        foreach (var endpoint in value.Endpoints)
        {
            var observation = observations.GetValueOrDefault(endpoint.Id);
            var inventory = observation?.Inventory;
            var online = observation is { Error: null };
            var adapter = adapters.GetValueOrDefault(endpoint.Id);
            var strata = adapter as StrataAdapter;
            var loading = exclusiveLoad is { } load && load.Endpoint == endpoint.Id ? load.Started : (DateTimeOffset?)null;
            endpoints.Add(new(endpoint.Id, endpoint.Profile, endpoint.Mode, endpoint.Address,
                endpoint.ProtectedToken is not null ? endpoint.Id : null, endpoint.Executable, endpoint.ModelFile,
                online, inventory?.Version, observation?.Error, observation?.ObservedAt,
                inventory?.CanLoad == true && (online || endpoint.Executable is not null),
                inventory?.CanUnload == true && (online || inventory.CanStop),
                strata is not null ? strata.Reserved || loading.HasValue : inventory?.CanStop == true,
                adapter?.OwnedProcessId ?? inventory?.OwnedProcessId,
                inventory?.MaxLoadedModels, observation?.CredentialNeeded == true, ConfigurationRevision(endpoint),
                endpoint.StrataControl, endpoint.StrataModels, strata?.Reserved == true, loading));
            foreach (var model in inventory?.Models ?? [])
            {
                var options = value.Models.GetValueOrDefault(model.Id) ?? new ModelOptions(null, [], null, null, null, null, 0);
                if (options.AliasExplicit is null && IsLegacyDefaultAlias(model, options.Alias))
                    options = options with { Alias = null };
                var configuration = ProviderIdentity.Hash(endpoint.Profile.ToString(), endpoint.Mode.ToString(), endpoint.Address,
                    FileStamp(endpoint.Executable), FileStamp(endpoint.ModelFile), options.ServedContext?.ToString(),
                    options.EmbeddingDimensions?.ToString(), options.MediaTokensPerItem?.ToString());
                if (model.ReportedMediaTokensPerItem is { } mediaBound)
                    configuration = ProviderIdentity.Hash(configuration, "reported-media", mediaBound.ToString());
                if (model.PreparedConfiguration is { } prepared)
                    configuration = ProviderIdentity.Hash(configuration, prepared);
                var old = previous.Models.FirstOrDefault(item => item.Id == model.Id);
                var instance = model.Instances.FirstOrDefault(item => !item.Busy) ?? model.Instances.FirstOrDefault();
                var epoch = !online || instance is null || uncertainModels.ContainsKey(model.Id) ? null :
                    old?.Instance is { } prior && old.PlacementEpoch is { } existing && old.Configuration == configuration &&
                    old.Options.Revision == options.Revision && old.Version == inventory?.Version && prior.Fingerprint == instance.Fingerprint &&
                    prior.NativeGeneration == instance.NativeGeneration && prior.Id == instance.Id &&
                    previous.Endpoints.First(item => item.Id == endpoint.Id).OwnedProcessId == endpoints[^1].OwnedProcessId
                        ? existing : Guid.NewGuid().ToString("N");
                models.Add(new(endpoint.Id, model, options, online, uncertainModels.ContainsKey(model.Id), inventory?.Version,
                    configuration, epoch));
            }
        }
        Volatile.Write(ref view, new(endpoints.ToArray(), models.ToArray(), value.Selected, store.RecoveryWarning));
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in watchers.Keys.ToArray())
            await StopWatchAsync(id);
        using var timeout = new CancellationTokenSource(View.Endpoints.Any(endpoint => endpoint.HostReserved)
            ? StrataAdapter.ReleaseTimeout : TimeSpan.FromSeconds(10));
        foreach (var adapter in adapters.Values.Where(OwnsRuntime))
        {
            try { await adapter.StopAsync(timeout.Token); }
            catch (Exception error) when (error is OperationCanceledException or System.ComponentModel.Win32Exception or ProviderFailure)
            {
                Console.Error.WriteLine("Mango could not confirm shutdown of an owned provider process.");
            }
        }
        http.Dispose();
        operation.Dispose();
        input.Dispose();
        wake.Dispose();
    }

    private async Task StopWatchAsync(Guid id)
    {
        if (!watchers.Remove(id, out var watcher))
            return;
        using var stop = watcher.Stop;
        await stop.CancelAsync();
        try { await watcher.Work; }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private static async Task<bool> ObserveIdleAsync(ProviderAdapter adapter, ProviderModel model, bool wait,
        CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            do
            {
                var idle = await adapter.CheckIdleAsync(model, timeout.Token);
                if (idle != false || !wait)
                    return idle == true;
                await Task.Delay(200, timeout.Token);
            } while (true);
        }
        catch (Exception error) when (error is ProviderFailure or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private ProviderAdapter CreateAdapter(ProviderEndpoint endpoint) => endpoint.Profile switch
    {
        ProviderProfile.LmStudio or ProviderProfile.Generic => new LmStudioAdapter(http, endpoint),
        ProviderProfile.UnslothStudio or ProviderProfile.UnslothRun => new UnslothAdapter(http, endpoint),
        ProviderProfile.Strata => new StrataAdapter(http, endpoint, directory, () => { Publish(); SignalRefresh(endpoint.Id); }),
        _ => new LlamaAdapter(http, endpoint)
    };

    private Deployment Find(string id) => View.Models.FirstOrDefault(item => item.Id == id)
        ?? throw new ProviderFailure(404, "The deployment was not found in the local inventory.");

    internal static bool IsLegacyDefaultAlias(ProviderModel model, string? alias)
    {
        if (alias is null)
            return false;
        var basename = model.NativeId.Contains('\\') || Path.IsPathRooted(model.NativeId)
            ? Path.GetFileNameWithoutExtension(model.NativeId) : model.NativeId;
        return ValidAlias(basename) ? string.Equals(alias, basename, StringComparison.Ordinal)
            : string.Equals(alias, $"model-{model.Id[..12].ToLowerInvariant()}", StringComparison.Ordinal);
    }

    internal static string DefaultAddress(ProviderProfile profile) => profile switch
    {
        ProviderProfile.Generic => throw new ProviderFailure(400, "Enter the generic provider's origin."),
        ProviderProfile.LmStudio => "http://127.0.0.1:1234",
        ProviderProfile.UnslothStudio or ProviderProfile.UnslothRun => "http://127.0.0.1:8888",
        ProviderProfile.Strata => "http://127.0.0.1:18765",
        _ => "http://127.0.0.1:8080"
    };

    internal static string NormalizeAddress(string address)
    {
        if (address.Length > 2048 || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Port is <= 0 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new ProviderFailure(400, "Use a loopback HTTP or HTTPS provider origin without credentials, paths, or query parameters.");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static void ValidateEndpoint(ProviderEndpoint endpoint)
    {
        _ = NormalizeAddress(endpoint.Address);
        if (endpoint.Id == Guid.Empty || !Enum.IsDefined(endpoint.Profile) || !Enum.IsDefined(endpoint.Mode))
            throw new ProviderFailure(400, "The provider registration is invalid.");
        if (endpoint.Profile == ProviderProfile.Strata)
        {
            var local = new Uri(endpoint.Address);
            if (endpoint.Mode != ProviderMode.Managed || local.Scheme != "http" || local.Host != "127.0.0.1" ||
                endpoint.ProtectedToken is not null || endpoint.Executable is not null || endpoint.ModelFile is not null ||
                endpoint.StrataControl is { } control && !LocalPath(control) ||
                endpoint.StrataModels is { } models && !LocalPath(models))
                throw new ProviderFailure(400, "Managed Strata needs an IPv4 loopback HTTP address and any folder overrides must be absolute local paths.");
            return;
        }
        if (endpoint.StrataControl is not null || endpoint.StrataModels is not null)
            throw new ProviderFailure(400, "Strata folders apply only to a Strata registration.");
        if (endpoint.Executable is null && endpoint.ModelFile is null)
            return;
        var uri = new Uri(endpoint.Address);
        if (endpoint.Mode != ProviderMode.Managed || endpoint.Profile is not (ProviderProfile.LlamaSingle or ProviderProfile.LlamaEmbedding) ||
            uri.Scheme != "http" || uri.Host != "127.0.0.1" ||
            !LocalPath(endpoint.Executable) || !LocalPath(endpoint.ModelFile) ||
            !string.Equals(Path.GetFileName(endpoint.Executable), "llama-server.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(endpoint.ModelFile), ".gguf", StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure(400, "Launching llama-server requires managed single-model mode, IPv4 loopback HTTP, and absolute local executable and GGUF paths.");
    }

    private static bool LocalPath(string? path) => path is { Length: >= 3 and <= 1024 } && char.IsAsciiLetter(path[0]) &&
        path[1] == ':' && path[2] is '\\' or '/' && Path.IsPathFullyQualified(path);

    private static string? FileStamp(string? path)
    {
        if (path is null)
            return null;
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : path;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return path; }
    }

    private static string? Validate(ProviderSettings value)
    {
        try
        {
            if (value.Endpoints is null || value.Models is null || value.Endpoints.Length > ProviderLimits.MaxEndpoints ||
                value.Endpoints.Any(endpoint => endpoint is null) ||
                value.Endpoints.DistinctBy(endpoint => endpoint.Id).Count() != value.Endpoints.Length)
                return "The local provider configuration is invalid.";
            foreach (var endpoint in value.Endpoints)
                ValidateEndpoint(endpoint);
            foreach (var model in value.Models.Values)
                ValidateModel(model);
            return null;
        }
        catch (ProviderFailure error) { return error.Message; }
    }

    internal static void ValidateModel(ModelOptions value)
    {
        if (value is null || value.Alias is not null && !ValidAlias(value.Alias) || value.Ratings is null || value.Ratings.Count > 64 ||
            value.Ratings.Any(rating => string.IsNullOrWhiteSpace(rating.Key) || rating.Key.Length > 100 ||
                rating.Key.Any(char.IsControl) || rating.Value is < 0 or > 5) ||
            value.ServedContext is < 1 or > ProviderLimits.MaxContext ||
            value.EmbeddingDimensions is < 1 or > ProviderLimits.MaxEmbeddingDimensions ||
            value.MediaTokensPerItem is < 1 or > ProviderLimits.MaxContext ||
            value.EmbeddingFamily is { } family && !ValidAlias(family) || value.Revision < 0)
            throw new ProviderFailure(400, "Use a valid model alias, ratings from 0 to 5, and positive model limits.");
    }

    internal static void RequireCompatibleAlias(Deployment model, IEnumerable<Deployment> others)
    {
        if (model.Alias is null)
            return;
        foreach (var other in others.Where(item => !item.ServingDisabled && item.Id != model.Id && SameAlias(item.Alias, model.Alias)))
        {
            if (model.Model.Operations.Contains(InferenceOperation.Embeddings) &&
                other.Model.Operations.Contains(InferenceOperation.Embeddings) &&
                (model.EmbeddingDimensions is { } dimensions && other.EmbeddingDimensions is { } otherDimensions && dimensions != otherDimensions ||
                 !model.Model.EmbeddingEncodings.Intersect(other.Model.EmbeddingEncodings).Any()))
                throw new ProviderFailure(409, "Embeddings under one alias must have compatible dimensions and encodings.");
        }
    }

    internal static bool ValidAlias(string? value) => value is { Length: > 0 and <= 200 } && !SameAlias(value, "best") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '/' or ':');
    private static bool SameAlias(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string? BlankToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string ConfigurationRevision(ProviderEndpoint endpoint) => ProviderIdentity.Hash(
        endpoint.Id.ToString(), endpoint.Profile.ToString(), endpoint.Mode.ToString(), endpoint.Address,
        endpoint.ProtectedToken, endpoint.Executable, endpoint.ModelFile, endpoint.StrataControl, endpoint.StrataModels);
}
