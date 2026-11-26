using System.ComponentModel;
using Mango.Host;
using Mango.Providers;

namespace Mango.Resources;

internal sealed partial class ResourceProtection : IDisposable
{
    internal const long MiB = 1024 * 1024;
    private static readonly TimeSpan Sustained = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BenchmarkIdle = TimeSpan.FromMinutes(5);
    private const string DedicatedMemoryPressure = "Dedicated memory pressure on ";
    private const string HostMemoryPressure = "Host RAM is below the reserve or above its pressure cutoff.";
    private const string CommitPressure = "Windows commit headroom is too low to keep the Strata runtime loaded.";
    private const string ShutdownSettlement = "A provider was still settling during shutdown.";
    private const string LegacySettlement = "Verifying unfinished provider work.";
    private readonly object sync = new();
    private readonly SnapshotFile<LocalSettings> settings;
    private readonly SnapshotFile<ResourceData> store;
    private readonly Func<CatalogView, DateTimeOffset, ResourceObservation> sample;
    private readonly Func<(ulong Total, ulong Available)> ram;
    private readonly TimeProvider clock;
    private readonly WindowsObservation? windows;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly List<ResourceReservation> reservations = [];
    private readonly HashSet<string> unsafePlacements = new(StringComparer.Ordinal);
    private ResourceStatus status = new(ContributionState.Withdrawing, "Checking local resources.", null);
    private ResourceObservation? observed;
    private long sampleNumber;
    private int settlementSample;
    private bool telemetryWarning;
    private bool startupInspectionPending;
    private ResourceObservation? hardwareObservation;
    private ResourceObservation? CurrentObservation => observed is { } value &&
        clock.GetUtcNow() - value.At <= TimeSpan.FromSeconds(settings.Value.SampleSeconds + 5) ? value : null;
    private TaskCompletionSource nextObservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? highSince;
    private DateTimeOffset? quietSince;
    private DateTimeOffset lastClient;
    private ResourceEvent? lastEvent;
    private bool withdraw;
    private string? withdrawalReason;
    private WithdrawalCause withdrawalCause;
    private Task<ReleaseResult>? release;
    private ResourceObservation? beforeRelease;
    private long? recoverableReleaseSample;
    private DateTimeOffset? recoverableReleaseAt;
    private CancellationTokenSource? benchmarkStop;
    private Task? benchmark;
    private BenchmarkPolicy policy;
    private int stopping;
    public event Action? Changed;
    public event Action? CapacityChanged;

    public ResourceProtection(string directory, SnapshotFile<LocalSettings> settings,
        Func<CatalogView, DateTimeOffset, ResourceObservation>? sample = null,
        Func<(ulong Total, ulong Available)>? ram = null, TimeProvider? clock = null)
    {
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        this.ram = ram ?? WindowsMemory.Read;
        windows = sample is null ? new WindowsObservation() : null;
        this.sample = sample ?? windows!.Sample;
        store = new(Path.Combine(directory, "resources.json"), Validate);
    }

    public ResourceStatus Status { get { lock (sync) return status; } }
    public ResourceObservation? Observation { get { lock (sync) return observed; } }
    public ResourceEvent? LastEvent { get { lock (sync) return lastEvent; } }

    public async Task InitializeAsync(bool requireStartupInspection = false)
    {
        await store.LoadAsync(ResourceData.Empty);
        startupInspectionPending = requireStartupInspection;
        lastClient = clock.GetUtcNow();
        unsafePlacements.UnionWith(store.Value.Unsafe);
        policy = settings.Value.BenchmarkPolicy;
        var hold = store.Value.Hold;
        if (hold is { Cause: WithdrawalCause.Safety } && (hold.Reason is HostMemoryPressure or CommitPressure ||
            hold.Reason.StartsWith(DedicatedMemoryPressure, StringComparison.Ordinal)))
        {
            hold = hold.Pending is { Length: > 0 }
                ? hold with { State = ContributionState.Withdrawing, Reason = LegacySettlement }
                : null;
            await store.UpdateAsync(data => data with { Hold = hold });
        }
        withdrawalCause = hold?.Cause ?? WithdrawalCause.Safety;
        if (hold is not null)
            status = new(hold.State, hold.Reason, null);
        else if (settings.Value.Role == LocalRole.Contributor)
            status = new(ContributionState.Contributor, null, null);
        if (hold?.State == ContributionState.Withdrawing && !IsStartupHistory(hold) ||
            hold?.State != ContributionState.Fault && settings.Value.Role == LocalRole.Leech)
            BeginWithdrawal(hold?.Reason ?? "Local leech mode is enabled.");
        settings.Changed += SettingsChanged;
    }

    private static bool IsStartupHistory(ResourceHold? hold) =>
        hold is { State: ContributionState.Withdrawing, Cause: WithdrawalCause.Safety,
            Reason: ShutdownSettlement or LegacySettlement };

    internal async Task InspectStartupAsync(ProviderCatalog providers, CancellationToken cancellation)
    {
        var hold = store.Value.Hold;
        var missingLoads = await providers.InspectStartupAsync(hold?.Pending ?? [], cancellation);
        await SampleAndApplyAsync(() => providers.ResourceView, cancellation);
        bool replaceHistoricalHold;
        lock (sync)
        {
            replaceHistoricalHold = !withdraw && settings.Value.Role == LocalRole.Contributor && (IsStartupHistory(hold) ||
                missingLoads && providers.StartupEvidence.Length == 0 &&
                hold is { State: ContributionState.Fault, Cause: WithdrawalCause.Safety, Reason: ProviderCatalog.ReleaseDeadline });
            if (replaceHistoricalHold)
                SetStatus(new(ContributionState.Contributor, null, null));
        }
        if (replaceHistoricalHold)
            await SaveHoldAsync(StartupHistory(providers));
        lock (sync)
            startupInspectionPending = false;
        Changed?.Invoke();
    }

    private static ResourceHold? StartupHistory(ProviderCatalog providers)
    {
        var evidence = providers.StartupEvidence;
        return evidence.Length == 0 ? null :
            new(ContributionState.Withdrawing, ShutdownSettlement, evidence.OrderBy(item => item.Deployment, StringComparer.Ordinal)
                .ThenBy(item => item.Endpoint).Take(ProviderLimits.MaxModels).ToArray());
    }

    private void SettingsChanged()
    {
        lock (sync)
        {
            if (settings.Value.Role == LocalRole.Leech)
                foreach (var lease in reservations.Where(item => item.Yolo)) RequestCancel(lease.Stop);
            if (settings.Value.Role == LocalRole.Leech && status.State is not (ContributionState.ManualLeech or ContributionState.Fault))
                BeginWithdrawal("Local leech mode is enabled.");
            var next = settings.Value.BenchmarkPolicy;
            if (policy != next)
            {
                policy = next;
                SetBenchmarkReady(next == BenchmarkPolicy.Never);
                if (next == BenchmarkPolicy.Never && !runningForcedBenchmark)
                    RequestCancel(benchmarkStop);
            }
        }
        Signal();
    }

    private void Signal()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async Task ClientArrivedAsync(CancellationToken cancellation)
    {
        Task? interrupt = null;
        lock (sync)
        {
            lastClient = clock.GetUtcNow();
            if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient && !runningForcedBenchmark)
            {
                RequestCancel(benchmarkStop);
                interrupt = benchmark;
            }
        }
        if (interrupt is not null)
            try { await interrupt.WaitAsync(TimeSpan.FromSeconds(6), cancellation); }
            catch (TimeoutException) { }
    }

    public bool HasWork(Guid endpoint)
    {
        lock (sync)
            return reservations.Any(item => item.Endpoint == endpoint && !item.Settled);
    }

    public bool HasRunning(Guid endpoint, bool includeYolo = true)
    {
        lock (sync)
            return reservations.Any(item => item.Endpoint == endpoint && item.Running && (includeYolo || !item.Yolo));
    }

    public bool IsAvailable(Deployment model, EndpointStatus endpoint, InferenceOperation? requested = null)
    {
        lock (sync)
            return ServingAllowed && !model.ServingDisabled && !model.Model.AmbiguousInstance && model.Model.Installed &&
                (model.Eligible || endpoint.Mode == ProviderMode.Managed && endpoint.CanLoad && model.Model.SupportsLoad) &&
                Operations(model, endpoint).Any(operation => requested is null || operation == requested);
    }

    public ServingReadiness Readiness(Deployment model, EndpointStatus endpoint, InferenceOperation? requested = null,
        string? exclusiveBlocker = null, bool modeSwitch = false)
    {
        lock (sync)
        {
            var available = IsAvailable(model, endpoint, requested);
            ServingReadiness State(string state, string reason, string? action = null) => new(state, reason, action, available);
            if (model.ServingDisabled) return State("disabled", "Serving is disabled for this model.");
            if (model.Model.AmbiguousInstance) return State("device", "LM Studio did not report a uniquely addressable instance for this device.");
            if (!model.Online) return State("offline", "The provider is unreachable. Check its address, credential, and process.", "provider");
            if (!model.Model.Installed) return State("installation", "Install this model in its provider, then refresh the inventory.", "provider");
            if (settings.Value.Role != LocalRole.Contributor) return State("contribution", "Turn on contribution for this computer to serve network requests.", "contribution");
            if (startupInspectionPending) return State("protection", "Checking which provider work survived startup.");
            if (modeSwitch && available)
                return State("modeSwitch", ProviderCatalog.ModeSwitchWait, "provider");
            if (exclusiveBlocker is not null) return State("busy", exclusiveBlocker, "provider");
            if (status.State != ContributionState.Contributor) return State("protection", status.Reason ?? "Local protection is holding contribution.", "protection");
            var operations = Operations(model, endpoint).Where(operation => requested is null || requested == operation).ToArray();
            if (operations.Length == 0) return State("protocol", "The provider has not reported a supported inference operation.", "provider");
            if (!model.Eligible && !(endpoint.CanLoad && model.Model.SupportsLoad))
            {
                if (model.Instance is null) return State("load", "Load this model in its provider. This registration has no authorized load action.", "provider");
                return State("protocol", "The provider has not confirmed this model's serving capabilities.", "provider");
            }
            if (BenchmarkOccupiesSlot)
                return State("busy", "The local benchmark is occupying capacity.");
            if (model.Busy || model.Instance?.Busy == true || reservations.Any(item => item.Deployment == model.Id && !item.Settled))
                return State("busy", "This deployment has active or settling work.");
            return model.Instance is null ? State("readyToLoad", "Ready for automatic loading when a matching request arrives.")
                : State("ready", "Ready for matching requests.");
        }
    }

    internal static bool CanUseNativeBenchmark(Deployment model, EndpointStatus endpoint) =>
        endpoint.Mode == ProviderMode.Managed &&
        (endpoint.CanUnload && model.Model.SupportsUnload || endpoint.CanStop || endpoint.Executable is not null);

    public Admission TryAdmit(Deployment model, EndpointStatus endpoint, Workload workload, ResourceWork work,
        bool profiling = false, bool speedSample = false, bool additionalLoad = false)
    {
        lock (sync)
        {
            if (!ServingAllowed || model.ServingDisabled)
                return Refuse(status.Reason ?? "Local serving is paused.");
            if (work == ResourceWork.Load && (endpoint.Mode != ProviderMode.Managed || !endpoint.CanLoad || !model.Model.SupportsLoad))
                return Refuse("The provider does not authorize loading this deployment.");
            if (model.Yolo && work == ResourceWork.Benchmark && (!speedSample || model.Instance is null))
                return Refuse("YOLO benchmarks require a loaded instance.");
            if (!profiling && BenchmarkOccupiesSlot)
                return new(AdmissionStatus.Wait, "Waiting for the local benchmark to settle.", null);
            var pending = reservations.Where(item => !item.Settled).ToArray();
            foreach (var other in pending)
            {
                if (profiling && other.NativeBenchmark && !other.Running && other.Endpoint != endpoint.Id && !endpoint.ExclusiveHost)
                    continue;
                if (!other.Running || other.Stop.IsCancellationRequested || profiling || other.Work != ResourceWork.Execute ||
                    other.Yolo != model.Yolo || endpoint.ExclusiveHost ||
                    work == ResourceWork.Execute && other.Endpoint == model.EndpointId && other.Deployment != model.Id && !model.Yolo)
                    return new(AdmissionStatus.Wait, "Waiting for active or settling provider work.", null);
            }
            if (work == ResourceWork.Execute && pending.Count(item => item.Deployment == model.Id) >= (model.Instance?.Slots ?? 1))
                return new(AdmissionStatus.Wait, "Waiting for active or settling provider work.", null);

            var cost = MemoryCost.Empty;
            if (additionalLoad && !model.Yolo && work == ResourceWork.Load)
            {
                var peak = CheckedLoadCost(model, workload.Operation);
                if (peak is null || !FitsAdditionalLoad(peak))
                    return new(AdmissionStatus.Wait, "Co-residency exceeds measured headroom or lacks comparable evidence.", null, MemoryWait: true);
                cost = peak;
            }
            var lease = new ResourceReservation(this, model.EndpointId, model.Id, work,
                ProviderIdentity.Hash(model.Id, Configuration(model), workload.Operation.ToString()), cost, true,
                sampleNumber, CurrentObservation?.Workers.GetValueOrDefault(model.EndpointId), model.Yolo, profiling);
            reservations.Add(lease);
            return new(AdmissionStatus.Admitted, null, lease);
        }
    }

    private static Admission Refuse(string reason) => new(AdmissionStatus.Unavailable, reason, null);

    internal bool AdditionalLoadFits(Deployment model, InferenceOperation operation)
    {
        lock (sync)
            return CheckedLoadCost(model, operation) is { } peak && FitsAdditionalLoad(peak);
    }

    internal static bool Covers(Workload limit, Workload actual) =>
        limit.Operation == actual.Operation && limit.Units >= actual.Units;

    private bool FitsAdditionalLoad(MemoryCost cost)
    {
        if (CurrentObservation is not { TotalRam: > 0, Hardware: not null } latest) return false;
        var free = latest.FreeRam;
        try { free = checked((long)ram().Available); }
        catch (Win32Exception) { WarnTelemetry(); }
        var current = settings.Value;
        if (cost.RamBytes > free - ConfiguredRamReserve(latest, current) - reservations.Sum(item => Unused(item, latest, null)))
            return false;
        var devices = windows?.ReadFreshGpuMemory() ?? latest.Devices;
        foreach (var device in devices.Where(device => !device.Integrated))
        {
            if (!cost.DeviceBytes.TryGetValue(device.Id, out var peak) ||
                device is not { TotalBytes: { } total, UsedBytes: { } used } ||
                peak > 0 && peak > total - used - (long)current.EffectiveVramReserveMiB * MiB -
                    reservations.Sum(item => Unused(item, latest, device.Id)))
                return false;
        }
        return cost.DeviceBytes.Keys.All(id => devices.Any(device => device.Id == id && !device.Integrated));
    }

    private static long ConfiguredRamReserve(ResourceObservation? latest, LocalSettings current) =>
        (long)current.EffectiveRamReserveMiB(LocalSettings.DefaultRamReserveMiB((ulong)(latest?.TotalRam ?? 0))) * MiB;

    private long Unused(ResourceReservation item, ResourceObservation latest, string? device)
    {
        var reserved = device is null ? item.Cost.RamBytes : item.Cost.DeviceBytes.GetValueOrDefault(device);
        if (reserved == 0 || item.Baseline is null || reservations.Count(other => other.Endpoint == item.Endpoint && other.Cost != MemoryCost.Empty) > 1 ||
            !latest.Workers.TryGetValue(item.Endpoint, out var worker))
            return reserved;
        if (device is not null && (worker.DedicatedBytes is null || item.Baseline is { DedicatedBytes: null }))
            return reserved;
        var before = device is null ? item.Baseline?.ResidentBytes ?? 0 : item.Baseline?.DedicatedBytes?.GetValueOrDefault(device) ?? 0;
        var after = device is null ? worker.ResidentBytes : worker.DedicatedBytes!.GetValueOrDefault(device);
        return Math.Max(0, reserved - Math.Max(0, after - before));
    }

    internal void Cancel(ResourceReservation item)
    {
        lock (sync)
            if (!item.Disposed)
                RequestCancel(item.Stop);
    }

    internal void ReleaseUnstarted(ResourceReservation item)
    {
        lock (sync)
            Remove(item);
    }

    internal void Settle(ResourceReservation item, bool settled)
    {
        lock (sync)
        {
            if (!reservations.Contains(item))
                return;
            if (item.Work == ResourceWork.Execute && (item.Running || settled && !item.Settled))
                lastClient = clock.GetUtcNow();
            item.Running = false;
            item.Settled |= settled;
            if (item.Settled && item.Cost != MemoryCost.Empty)
                Interlocked.Exchange(ref settlementSample, 1);
            if (item.Settled && item.Cost == MemoryCost.Empty)
                Remove(item);
        }
        Signal();
        CapacityChanged?.Invoke();
    }

    public void ConfirmSettled(string deployment)
    {
        lock (sync)
            foreach (var item in reservations.Where(item => !item.Running && item.Deployment == deployment).ToArray())
                Settle(item, true);
    }

    private void Remove(ResourceReservation item)
    {
        reservations.Remove(item);
        item.Disposed = true;
        item.Stop.Dispose();
    }

    private static void RequestCancel(CancellationTokenSource? source)
    {
        if (source is not null && !source.IsCancellationRequested)
            _ = source.CancelAsync();
    }

    public async Task AllocationFailedAsync(ResourceReservation reservation)
    {
        if (reservation.Yolo) return;
        lock (sync)
            unsafePlacements.Add(reservation.Fingerprint);
        try
        {
            await store.UpdateAsync(data => data with
            {
                Unsafe = data.Unsafe.Append(reservation.Fingerprint).Distinct(StringComparer.Ordinal).TakeLast(1024).ToArray(),
                Profiles = data.Profiles.Select(profile =>
                    ProviderIdentity.Hash(profile.Deployment, profile.Configuration, profile.Operation.ToString()) == reservation.Fingerprint
                        ? profile with { Complete = false, Error = "The provider reported a failed memory allocation." } : profile).ToArray()
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Mango could not persist the allocation failure diagnostic.");
        }
        Changed?.Invoke();
        Signal();
    }

    public async Task RunAsync(ProviderCatalog providers, CancellationToken cancellation)
    {
        void CatalogUpdated() => CatalogChanged(providers.View);
        providers.Changed += CatalogUpdated;
        var nextSample = DateTimeOffset.MinValue;
        var sampled = DateTimeOffset.MinValue;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var now = clock.GetUtcNow();
                var interval = TimeSpan.FromSeconds(settings.Value.SampleSeconds);
                nextSample = sampled == DateTimeOffset.MinValue ? now : sampled + interval;
                // Keep the reservation until a fresh observation, without imposing the
                // entire idle sampling interval on the next sequential client request.
                if (Volatile.Read(ref settlementSample) != 0 && sampled != DateTimeOffset.MinValue)
                    nextSample = nextSample < sampled.AddSeconds(1) ? nextSample : sampled.AddSeconds(1);
                if (now >= nextSample)
                {
                    Interlocked.Exchange(ref settlementSample, 0);
                    await SampleAndApplyAsync(() => providers.ResourceView, cancellation);
                    providers.RefreshObservedStartupWork();
                    sampled = now;
                    nextSample = sampled + interval;
                }
                await AdvanceWithdrawalAsync(providers, cancellation);
                AdvanceBenchmarks(providers, cancellation);
                var delay = nextSample - clock.GetUtcNow();
                if (delay > TimeSpan.Zero)
                    await wake.WaitAsync(delay, cancellation);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            providers.Changed -= CatalogUpdated;
            Interlocked.Exchange(ref stopping, 1);
            lock (sync)
            {
                RequestCancel(benchmarkStop);
                foreach (var lease in reservations)
                    RequestCancel(lease.Stop);
            }
            try
            {
                if (benchmark is not null)
                    await benchmark;
                if (release is not null)
                    await release;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            var pending = Pending(providers);
            if (withdraw)
                await SaveHoldAsync(new(ContributionState.Withdrawing,
                    withdrawalReason ?? ShutdownSettlement, pending, withdrawalCause));
            else if (pending.Length != 0)
            {
                var current = Status;
                await SaveHoldAsync(current.State is ContributionState.Fault or ContributionState.Cooldown
                    ? new(current.State, current.Reason!, pending, withdrawalCause)
                    : new(ContributionState.Withdrawing, ShutdownSettlement, pending));
            }
        }
    }

    internal Task SampleAndApplyAsync(Func<CatalogView> view, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try { ApplyObservation(sample(view(), clock.GetUtcNow())); }
        catch (Win32Exception)
        {
            ApplyObservation(new(clock.GetUtcNow(), 0, 0, null, null, [], null, [], false,
                "Windows resource observations are temporarily unavailable."));
        }
        CapacityChanged?.Invoke();
        return Task.CompletedTask;
    }

    private void WarnTelemetry()
    {
        if (telemetryWarning) return;
        telemetryWarning = true;
        System.Diagnostics.Trace.TraceWarning("Mango resource telemetry is incomplete; continuing with native provider evidence.");
        lastEvent = new(clock.GetUtcNow(), "Resource telemetry is incomplete; continuing with native provider evidence.",
            false, false, null, null, null);
        Changed?.Invoke();
    }

    internal void ApplyObservation(ResourceObservation latest)
    {
        lock (sync)
        {
            var current = settings.Value;
            if (latest.At > clock.GetUtcNow() || observed is { } newer && latest.At < newer.At)
                return;
            if (observed is { } previous && latest.At - previous.At > TimeSpan.FromSeconds(current.SampleSeconds + 3))
                highSince = null;
            observed = latest;
            if (latest.Hardware is not null && latest.TotalRam > 0) hardwareObservation = latest;
            if (!ReliableReleaseObservation(latest)) WarnTelemetry();
            else telemetryWarning = false;
            sampleNumber++;
            nextObservation.TrySetResult();
            nextObservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var item in reservations.Where(item => item.Settled && item.Sample < sampleNumber).ToArray())
                Remove(item);
            ObserveBenchmark(latest);
            // Only sustained, attributable external usage earns the user-activity delay,
            // including when it overlaps a safety withdrawal or release fault.
            var external = current.ActivityMonitoring && latest.WorkerAttribution &&
                (latest.ExternalCpu >= current.CpuCutoffPercent || latest.ExternalGpu >= current.GpuCutoffPercent);
            highSince = external ? highSince ?? latest.At : null;
            if (latest.At - highSince >= Sustained)
                withdrawalCause = WithdrawalCause.ExternalActivity;
            if (status.State == ContributionState.Fault)
                return;
            if (current.Role == LocalRole.Leech)
            {
                if (status.State is not (ContributionState.ManualLeech or ContributionState.Withdrawing))
                    BeginWithdrawal("Local leech mode is enabled.");
                return;
            }
            if (status.State == ContributionState.Withdrawing && withdraw)
                return;
            var high = external;
            if (status.State == ContributionState.Cooldown)
            {
                if (high)
                    quietSince = null;
                else
                    quietSince ??= latest.At;
                var until = quietSince?.AddSeconds(current.CooldownSeconds);
                if (until <= latest.At)
                    SetStatus(new(ContributionState.Contributor, null, null));
                else
                    status = status with { QuietUntil = until };
                return;
            }
            if (withdrawalCause == WithdrawalCause.ExternalActivity)
                BeginWithdrawal("Sustained external CPU or GPU activity.");
            else if (status.State is ContributionState.ManualLeech or ContributionState.Withdrawing)
                SetStatus(new(ContributionState.Contributor, null, null));
        }
    }

    private void BeginWithdrawal(string reason)
    {
        lock (sync)
        {
            if (withdraw || release is not null)
                return;
            withdraw = true;
            withdrawalReason = reason;
            quietSince = null;
            lastEvent = new(clock.GetUtcNow(), reason, reservations.Any(item => item.Running && item.Work == ResourceWork.Load),
                reservations.Any(item => item.Running && item.Work != ResourceWork.Load),
                observed?.FreeRam, observed?.ExternalCpu, observed?.ExternalGpu);
            SetStatus(new(ContributionState.Withdrawing, reason, null));
            RequestCancel(benchmarkStop);
            foreach (var lease in reservations)
                RequestCancel(lease.Stop);
        }
        Signal();
    }

    private void SetStatus(ResourceStatus next)
    {
        if (next.State == ContributionState.Fault)
            recoverableReleaseSample = null;
        if (status == next)
            return;
        if (next.State == ContributionState.Contributor)
            withdrawalCause = WithdrawalCause.Safety;
        status = next;
        Changed?.Invoke();
    }

    internal async Task AdvanceWithdrawalAsync(ProviderCatalog providers, CancellationToken cancellation)
    {
        lock (sync)
        {
            // Retry only transient verification holds, using a newer, current, quiet sample.
            // Native stop/unload evidence is still obtained by the normal release path below.
            if (status.State == ContributionState.Fault && recoverableReleaseSample is { } failedSample &&
                sampleNumber > failedSample && settings.Value.Role == LocalRole.Contributor &&
                observed is { } latest && latest.At > recoverableReleaseAt &&
                !(latest.WorkerAttribution && (latest.ExternalCpu >= settings.Value.CpuCutoffPercent || latest.ExternalGpu >= settings.Value.GpuCutoffPercent)) &&
                !reservations.Any(item => item.Running) && !providers.HasActiveRequests &&
                !providers.View.Models.Any(item => item.Instance?.Busy == true))
            {
                recoverableReleaseSample = null;
                BeginWithdrawal("Checking that managed inference resources have been released.");
            }
        }
        if (withdraw && release is null)
        {
            beforeRelease = observed;
            foreach (var pending in store.Value.Hold?.Pending ?? [])
                if (pending.OwnedProcesses.Length != 0 && pending.OwnedProcesses.All(pid => !WindowsObservation.ProcessExists(pid)))
                    ConfirmReleased(providers, pending.Endpoint);
            await SaveHoldAsync(new(ContributionState.Withdrawing, withdrawalReason!, Pending(providers), withdrawalCause));
            release = providers.ReleaseManagedAsync(cancellation, includeYolo: true);
            _ = release.ContinueWith(_ => Signal(), TaskScheduler.Default);
            return;
        }
        if (release is { IsCompleted: true } finished)
        {
            var result = await finished;
            string? error = result.Error;
            lock (sync)
            {
                error ??= VerifyRelease(result, beforeRelease, observed);
                if (error is null)
                    foreach (var endpoint in result.Released)
                        ConfirmReleased(providers, endpoint.Id);
                release = null;
                withdraw = false;
                if (error is not null)
                    SetStatus(new(ContributionState.Fault, error, null));
                else if (settings.Value.Role == LocalRole.Leech)
                    SetStatus(new(ContributionState.ManualLeech, withdrawalReason, null));
                else if (withdrawalCause == WithdrawalCause.ExternalActivity)
                    SetStatus(new(ContributionState.Cooldown, withdrawalReason, null));
                else
                    SetStatus(new(ContributionState.Contributor, null, null));
                recoverableReleaseSample = error is not null && (result.Error is null || result.AwaitingStop)
                    ? sampleNumber : null;
                recoverableReleaseAt = clock.GetUtcNow();
            }
        }
        var current = Status;
        var prior = store.Value.Hold;
        var hold = current.State is ContributionState.Cooldown or ContributionState.Fault
            ? new ResourceHold(current.State, current.Reason!, current.State == ContributionState.Fault ? Pending(providers) :
                StartupHistory(providers)?.Pending, withdrawalCause)
            : StartupHistory(providers);
        if (!withdraw && (prior?.State != hold?.State || prior?.Reason != hold?.Reason || prior?.Cause != hold?.Cause ||
                !(prior?.Pending ?? []).Select(item => (item.Deployment, item.Loading, Pids: string.Join(",", item.OwnedProcesses)))
                    .SequenceEqual((hold?.Pending ?? []).Select(item => (item.Deployment, item.Loading, Pids: string.Join(",", item.OwnedProcesses))))))
            await SaveHoldAsync(hold);
    }

    private void ConfirmReleased(ProviderCatalog providers, Guid endpoint)
    {
        lock (sync)
        {
            providers.ConfirmReleased(endpoint, includeYolo: true);
            foreach (var lease in reservations.Where(item => item.Endpoint == endpoint && !item.Running).ToArray())
                Settle(lease, true);
        }
    }

    private PendingRelease[] Pending(ProviderCatalog providers)
    {
        lock (sync)
        {
            var endpoints = providers.View.Endpoints;
            var startup = providers.StartupEvidence.ToDictionary(item => (item.Endpoint, item.Deployment));
            var pending = reservations.Where(item => !item.Settled).Select(item => (item.Endpoint, item.Deployment))
                .Concat(providers.Unsettled).Concat(startup.Values.Select(item => (item.Endpoint, item.Deployment)))
                .Distinct().OrderBy(item => item.Deployment, StringComparer.Ordinal).Take(ProviderLimits.MaxModels);
            return pending.Select(item =>
            {
                var root = endpoints.FirstOrDefault(endpoint => endpoint.Id == item.Endpoint)?.OwnedProcessId;
                var prior = startup.GetValueOrDefault(item) ??
                    store.Value.Hold?.Pending?.FirstOrDefault(value => value.Deployment == item.Deployment);
                var tree = root is { } pid
                    ? (observed?.Workers.GetValueOrDefault(item.Endpoint)?.Processes ?? []).Append(pid).Distinct().Take(256).ToArray()
                    : prior?.OwnedProcesses ?? [];
                return new PendingRelease(item.Endpoint, item.Deployment, tree,
                    providers.HasUnsettledLoad(item.Deployment) || startup.GetValueOrDefault(item)?.Loading == true || reservations.Any(reservation =>
                        reservation.Deployment == item.Deployment && reservation.Work == ResourceWork.Load && !reservation.Settled));
            }).ToArray();
        }
    }

    private async Task SaveHoldAsync(ResourceHold? hold)
    {
        try { await store.UpdateAsync(data => data with { Hold = hold }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (sync)
            {
                recoverableReleaseSample = null;
                SetStatus(new(ContributionState.Fault, "Mango could not save the local resource safety state.", null));
            }
        }
    }

    private bool ReliableReleaseObservation(ResourceObservation value) =>
        value.At <= clock.GetUtcNow() && clock.GetUtcNow() - value.At <= TimeSpan.FromSeconds(settings.Value.SampleSeconds + 5) &&
        value.TotalRam > 0 && value.Hardware is not null && value.WorkerAttribution &&
        value.ExternalCpu is not null && value.ExternalGpu is not null &&
        value.Devices.All(device => device.Integrated || device.TotalBytes is not null && device.UsedBytes is not null);

    private static string? VerifyRelease(ReleaseResult result, ResourceObservation? before, ResourceObservation? after)
    {
        foreach (var endpoint in result.Released)
        {
            var prior = before?.Workers.GetValueOrDefault(endpoint.Id);
            var remaining = after?.Workers.GetValueOrDefault(endpoint.Id);
            if (endpoint.StoppedProcess is { } stopped)
            {
                if (new[] { stopped }.Concat(prior?.Processes ?? []).Concat(remaining?.Processes ?? [])
                    .Distinct().Any(WindowsObservation.ProcessExists))
                    return "A worker from the managed provider tree is still running.";
                if (remaining is not null)
                    System.Diagnostics.Trace.TraceWarning("A stopped provider has a stale worker observation; all tracked processes are gone.");
            }

        }
        return null;
    }

    private static bool VerifiedMemoryDrop(WorkerMemory before, WorkerMemory after, bool dedicatedDevices)
    {
        static bool Dropped(long prior, long remaining) => prior - remaining >= Math.Clamp(prior / 16, MiB, 16 * MiB);
        if (dedicatedDevices && (before.DedicatedBytes is null || after.DedicatedBytes is null))
            return false;
        var dedicated = before.DedicatedBytes?.Where(item => item.Value >= 16 * MiB).ToArray() ?? [];
        if (dedicated.Any(item => !Dropped(item.Value, after.DedicatedBytes!.GetValueOrDefault(item.Key))))
            return false;
        var shared = before.SharedGpuBytes is >= 16 * MiB && after.SharedGpuBytes is { } remainingShared &&
            Dropped(before.SharedGpuBytes.Value, remainingShared);
        if (before.SharedGpuBytes is >= 16 * MiB && !shared)
            return false;
        return Dropped(before.ResidentBytes, after.ResidentBytes) || dedicated.Length != 0 || shared;
    }

    public void RetryRelease()
    {
        lock (sync)
            if (status.State == ContributionState.Fault)
                BeginWithdrawal("Checking that managed inference resources have been released.");
    }

    public void Dispose()
    {
        settings.Changed -= SettingsChanged;
        lock (sync)
            foreach (var item in reservations.ToArray())
                Remove(item);
        windows?.Dispose();
        wake.Dispose();
    }
}
