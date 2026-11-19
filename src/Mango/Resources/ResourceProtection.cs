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
    private long? releaseSample;
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

    public async Task InitializeAsync()
    {
        await store.LoadAsync(ResourceData.Empty);
        lastClient = clock.GetUtcNow();
        unsafePlacements.UnionWith(store.Value.Unsafe);
        policy = settings.Value.BenchmarkPolicy;
        var hold = store.Value.Hold;
        withdrawalCause = hold?.Cause ?? WithdrawalCause.Safety;
        if (hold is not null)
            status = new(hold.State, hold.Reason, null);
        else if (settings.Value.Role == LocalRole.Contributor)
            status = new(ContributionState.Contributor, null, null);
        if (hold?.State == ContributionState.Withdrawing ||
            hold?.State != ContributionState.Fault && settings.Value.Role == LocalRole.Leech)
            BeginWithdrawal(hold?.Reason ?? "Local leech mode is enabled.");
        settings.Changed += SettingsChanged;
    }

    public void RestoreUnsettled(ProviderCatalog providers)
    {
        foreach (var pending in store.Value.Hold?.Pending ?? [])
            if (pending.OwnedProcesses.Length == 0 || pending.OwnedProcesses.Any(WindowsObservation.ProcessExists))
                providers.RestoreUnsettled(pending.Endpoint, pending.Deployment, pending.Loading);
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
        {
            if (model.Yolo)
                return settings.Value.Role == LocalRole.Contributor && Volatile.Read(ref stopping) == 0 &&
                    !model.ServingDisabled && model.Online && model.Model.Installed && !model.Model.AmbiguousInstance &&
                    (model.Eligible || endpoint.Mode == ProviderMode.Managed && endpoint.CanLoad && model.Model.SupportsLoad) &&
                    Operations(model, endpoint).Any(operation => requested is null || operation == requested);
            if (model.ServingDisabled || !ServingAllowed || !model.Model.Installed ||
                !model.Eligible && !(endpoint.CanLoad && model.Model.SupportsLoad))
                return false;
            return Operations(model, endpoint).Any(operation =>
            {
                if (requested is not null && requested != operation)
                    return false;
                var budget = FindBudget(model, endpoint, operation);
                return BudgetBlocker(model, endpoint, operation, budget) is null;
            });
        }
    }

    private (string State, string Reason, string Action)? BudgetBlocker(Deployment model, EndpointStatus endpoint,
        InferenceOperation operation, ResourceBudget? budget)
    {
        if (budget is null)
            return ("limits", "Set workload and memory limits for this model and operation.", "qualification");
        if (!AttributionAllows(endpoint, settings.Value, observed, budget.AllowAttached))
            return endpoint.Mode == ProviderMode.Attached
                ? ("attached", "Acknowledge the attached provider limitation in local qualification.", "qualification")
                : ("attribution", "Managed contribution requires a native unload or stop path.", "provider");
        if (unsafePlacements.Contains(FaultKey(budget)))
            return ("allocation", "An allocation failed. Correct the local limits or explicitly retry the benchmark.", "qualification");
        if (operation is InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne &&
            model.Context is { } context && budget.Workload.Units < context)
            return ("contextLimit", "The workload limit is smaller than the served context. Qualify the full context.", "qualification");
        return null;
    }

    // Read-only explanation of the same availability facts; admission still checks live pressure and reserves capacity.
    public ServingReadiness Readiness(Deployment model, EndpointStatus endpoint, InferenceOperation? requested = null,
        string? exclusiveBlocker = null)
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
            if (exclusiveBlocker is not null) return State("busy", exclusiveBlocker, "provider");
            if (model.Yolo)
                return State(model.Instance is null ? "readyToLoad" : "ready",
                    "YOLO: the provider owns native defaults and capacity. Mango automatic resource protection is bypassed for this model.");
            if (status.State != ContributionState.Contributor) return State("protection", status.Reason ?? "Local protection is holding contribution.", "protection");
            var latest = CurrentObservation;
            var operations = Operations(model, endpoint).Where(operation => requested is null || requested == operation).ToArray();
            if (operations.Length == 0) return State("protocol", "The provider has not reported a supported inference operation.", "provider");
            if (!model.Eligible && !(endpoint.CanLoad && model.Model.SupportsLoad))
            {
                if (model.Instance is null) return State("load", "Load this model in its provider. This registration has no authorized load action.", "provider");
                if (model.Context is null) return State("context", "Enter the served context when the provider cannot report it.", "model");
                return State("protocol", "The provider has not confirmed this model's serving capabilities.", "provider");
            }
            var candidates = operations.Select(operation => (Operation: operation, Budget: FindBudget(model, endpoint, operation))).ToArray();
            var qualified = candidates.FirstOrDefault(candidate => BudgetBlocker(model, endpoint, candidate.Operation, candidate.Budget) is null);
            if (qualified.Budget is null)
            {
                var blocker = BudgetBlocker(model, endpoint, candidates[0].Operation, candidates[0].Budget)!.Value;
                return State(blocker.State, blocker.Reason, blocker.Action);
            }
            if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.Now && !benchmarksReady)
                return State("benchmark", "The selected benchmark policy requires complete local profiles before inference.", "qualification");
            if (model.Busy || model.Instance?.Busy == true || reservations.Any(item => item.Endpoint == model.EndpointId && !item.Settled))
                return State("busy", "This provider is busy. New work may wait for capacity.");
            if (Pressure(latest, settings.Value) is { } pressure) return State("pressure", pressure, "protection");
            var cost = model.Instance is null ? qualified.Budget.Load : qualified.Budget.Execute;
            if (model.Instance is null && cost.RamBytes == 0 && cost.DeviceBytes.Values.All(value => value == 0))
                return State("loadLimit", CanUseNativeBenchmark(model, endpoint)
                    ? "Benchmark this model to measure its startup memory."
                    : "Enter a measured peak memory allowance before loading this model.", "qualification");
            if (!Fits(cost, latest, latest?.TotalRam > 0 ? latest.FreeRam : null, settings.Value,
                settledExclusive: endpoint.ExclusiveHost && model.Instance is not null))
                return State("capacity", "The qualified workload does not fit current RAM or dedicated VRAM reserves. Work may wait.", "qualification");
            return model.Instance is null ? State("readyToLoad", "Ready for automatic loading when a matching request arrives.")
                : State("ready", "Ready for matching requests. Admission checks live resources before starting work.");
        }
    }

    internal static bool CanUseNativeBenchmark(Deployment model, EndpointStatus endpoint) =>
        endpoint.Mode == ProviderMode.Managed &&
        (endpoint.CanUnload && model.Model.SupportsUnload || endpoint.CanStop || endpoint.Executable is not null);

    // Engine admission can sample through a memory-only hold after native work has settled.
    private bool NativeBenchmarkAllowed(LocalSettings current) =>
        status.State == ContributionState.Contributor ||
        status.State == ContributionState.Fault && observed is { WorkerAttribution: true } latest &&
        store.Value.Hold?.Pending is not { Length: > 0 } &&
        Pressure(latest, current) is { } memoryPressure && status.Reason == memoryPressure &&
        latest.ExternalCpu < current.CpuCutoffPercent && latest.ExternalGpu < current.GpuCutoffPercent;

    public Admission TryAdmit(Deployment model, EndpointStatus endpoint, Workload workload, ResourceWork work,
        bool profiling = false, long payloadBytes = 0, bool speedSample = false)
    {
        if (model.Yolo)
        {
            lock (sync)
            {
                if (!profiling && ForcedBenchmarkHoldsServing)
                    return new(AdmissionStatus.Wait, ForcedBenchmarkWait, null);
                if (!profiling && nativeBenchmark)
                    return new(AdmissionStatus.Wait, "Waiting for the native benchmark to settle.", null);
                if (!IsAvailable(model, endpoint, workload.Operation) || work == ResourceWork.Benchmark &&
                    (!speedSample || model.Instance is null || !NativeBenchmarkAllowed(settings.Value) ||
                        reservations.Any(item => !item.Settled)))
                    return Refuse("This model is paused, unavailable, or delegates qualification to its provider.");
                if (work == ResourceWork.Load && (endpoint.Mode != ProviderMode.Managed || !endpoint.CanLoad || !model.Model.SupportsLoad))
                    return Refuse("The provider does not authorize loading this deployment.");
                // A zero-cost lease tracks cancellation and uncertain completion, not capacity.
                var native = new ResourceReservation(this, model.EndpointId, model.Id, work, "yolo:" + model.Id,
                    MemoryCost.Empty, true, sampleNumber, null, yolo: true);
                reservations.Add(native);
                return new(AdmissionStatus.Admitted, null, native);
            }
        }
        var sampled = Observation;
        var needsFreshGpu = windows is not null && sampled is not null && sampled.Devices.Any(device => !device.Integrated) &&
            clock.GetUtcNow() - sampled.At > TimeSpan.FromSeconds(LocalSettings.MinimumSampleSeconds);
        var freshGpu = needsFreshGpu ? windows!.ReadFreshGpuMemory() : null;
        lock (sync)
        {
            var current = settings.Value;
            var providerCapacity = profiling && CanUseNativeBenchmark(model, endpoint);
            if (!(providerCapacity ? NativeBenchmarkAllowed(current) : status.State == ContributionState.Contributor) ||
                current.Role != LocalRole.Contributor ||
                Volatile.Read(ref stopping) != 0)
                return Refuse(status.Reason ?? "This node is in leech mode.");
            if (work == ResourceWork.Load && (endpoint.Mode != ProviderMode.Managed || !endpoint.CanLoad || !model.Model.SupportsLoad))
                return Refuse("The provider does not authorize loading this deployment.");
            if (!profiling && ForcedBenchmarkHoldsServing)
                return new(AdmissionStatus.Wait, ForcedBenchmarkWait, null);
            if (!profiling && nativeBenchmark)
                return new(AdmissionStatus.Wait, "Waiting for the native benchmark to settle.", null);
            if (!profiling && work != ResourceWork.Load && current.BenchmarkPolicy == BenchmarkPolicy.Now && !benchmarksReady)
                return Refuse("The local benchmark policy requires complete profiles before inference.");
            var latest = CurrentObservation;
            if (needsFreshGpu && freshGpu is not null && latest is not null)
                latest = latest with { Devices = freshGpu };
            var budget = FindBudget(model, endpoint, workload.Operation);
            if (budget is null || !Covers(budget.Workload, workload))
                return Refuse("Set a local memory and workload limit for this deployment and operation.");
            if (!AttributionAllows(endpoint, current, latest, budget.AllowAttached))
                return Refuse(endpoint.Mode == ProviderMode.Attached
                    ? "Confirm the attached provider limitation in this deployment's local memory limits."
                    : "Managed contribution requires a native unload or stop path.");
            var fingerprint = FaultKey(budget);
            if (unsafePlacements.Contains(fingerprint) && !(providerCapacity && runningForcedBenchmark))
                return Refuse("An allocation failed for this placement. Update its local limits or retry explicitly.");
            if (work == ResourceWork.Execute && workload.MediaItems > 0 && budget.Workload.MediaItems == 0)
                return Refuse("A host media memory qualification is required for this workload.");
            var cost = providerCapacity ? MemoryCost.Empty : work switch
            {
                ResourceWork.Load => budget.Load,
                ResourceWork.Benchmark when speedSample && budget.Estimated =>
                    EstimateBudget(model, workload.Operation, budget.Configuration, budget.Runtime, workload).Execute,
                _ => ProjectExecutionCost(budget, workload, payloadBytes)
            };
            if (work == ResourceWork.Load && !providerCapacity && cost.RamBytes == 0 && cost.DeviceBytes.Values.All(value => value == 0))
                return Refuse("Set a peak memory allowance before loading this model.");
            var overlap = !endpoint.ExclusiveHost && AllowsOverlap(budget) &&
                !profiling && work == ResourceWork.Execute && workload.Items == 1 &&
                workload.MediaItems <= budget.Workload.MediaItems;
            var peersOnEndpoint = reservations.Where(item => item.Endpoint == model.EndpointId && !item.Settled).ToArray();
            var slots = OverlapSlots(model, budget);
            if (reservations.Any(item => !item.Settled &&
                !(providerCapacity && !endpoint.ExclusiveHost && item.NativeBenchmark && !item.Running &&
                    item.Endpoint != endpoint.Id) &&
                (!overlap || !item.Overlap || !item.Running || (item.Endpoint == model.EndpointId
                    ? item.Deployment != model.Id : !SeparateWorkers(latest, item.Endpoint, model.EndpointId)))) ||
                peersOnEndpoint.Length >= slots)
                return new(AdmissionStatus.Wait, "Local resources are reserved by another provider operation.", null);
            if (providerCapacity)
            {
                var probe = new ResourceReservation(this, model.EndpointId, model.Id, work, fingerprint, cost,
                    overlap: false, sampleNumber, latest?.Workers.GetValueOrDefault(model.EndpointId), nativeBenchmark: true);
                reservations.Add(probe);
                return new(AdmissionStatus.Admitted, null, probe);
            }
            long? freeRam = null;
            var freeCommit = latest?.FreeCommit;
            try
            {
                var memory = endpoint.ExclusiveHost && work == ResourceWork.Load && windows is not null
                    ? ReadStrataMemory() : ram();
                freeRam = (long)memory.Available;
                latest = latest is null
                    ? new(clock.GetUtcNow(), (long)memory.Total, freeRam.Value, null, null, [], null, [], false, null)
                    : latest with { TotalRam = (long)memory.Total, FreeRam = freeRam.Value };
            }
            catch (Win32Exception)
            {
                if (endpoint.ExclusiveHost && work == ResourceWork.Load)
                    return Refuse("Mango could not verify Windows memory and commit headroom for Strata.");
                freeRam = latest?.TotalRam > 0 ? latest.FreeRam : null;
                WarnTelemetry();
            }
            if (Pressure(latest, current) is { } pressure)
            {
                BeginWithdrawal(pressure);
                return Refuse(pressure);
            }
            if (endpoint.ExclusiveHost && work == ResourceWork.Load && freeCommit is { } commit &&
                commit - ConfiguredRamReserve(latest, current) < cost.RamBytes)
                return new(AdmissionStatus.Wait, "Strata needs more Windows commit headroom before loading.", null, MemoryWait: true);
            if (!Fits(cost, latest, freeRam, current, settledExclusive: endpoint.ExclusiveHost && work != ResourceWork.Load))
                return new(AdmissionStatus.Wait, "The request does not fit current RAM or per-device VRAM reserves.", null, MemoryWait: true);
            var lease = new ResourceReservation(this, model.EndpointId, model.Id, work, fingerprint, cost, overlap,
                sampleNumber, latest?.Workers.GetValueOrDefault(model.EndpointId));
            reservations.Add(lease);
            return new(AdmissionStatus.Admitted, null, lease);

            (ulong Total, ulong Available) ReadStrataMemory()
            {
                var actual = WindowsMemory.ReadWithCommit();
                freeCommit = checked((long)actual.AvailableCommit);
                return (actual.Total, actual.Available);
            }
        }
    }

    private static Admission Refuse(string reason) => new(AdmissionStatus.Unavailable, reason, null);

    private bool AllowsOverlap(ResourceBudget budget) => settings.Value.BenchmarkPolicy != BenchmarkPolicy.Never && budget.AllowOverlap;

    public int OverlapSlots(Deployment model, ResourceBudget budget) => model.Instance?.Slots is <= 0 ? 0 :
        AllowsOverlap(budget) ? Math.Min(model.Instance?.Slots ?? 1, budget.ParallelSlots) : 1;
    private static bool SeparateWorkers(ResourceObservation? latest, Guid first, Guid second) =>
        latest is not null && latest.WorkerAttribution && latest.Workers.TryGetValue(first, out var a) && latest.Workers.TryGetValue(second, out var b) &&
        !a.Processes.Intersect(b.Processes).Any();

    public bool CanBackfill(ServingPlacement a, ServingPlacement b)
    {
        lock (sync)
        {
            if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.Never || observed is not { } latest ||
                a.Model.Instance is null || b.Model.Instance is null || a.Workload.Items != 1 || b.Workload.Items != 1 ||
                a.Budget is not { AllowOverlap: true } first || b.Budget is not { AllowOverlap: true } second ||
                !SeparateWorkers(latest, a.Endpoint.Id, b.Endpoint.Id) ||
                first.Execute.DeviceBytes.Keys.Intersect(second.Execute.DeviceBytes.Keys).Any())
                return false;
            var combined = new MemoryCost(first.Execute.RamBytes + second.Execute.RamBytes,
                first.Execute.DeviceBytes.Concat(second.Execute.DeviceBytes).ToDictionary());
            return Fits(combined, latest, latest.FreeRam, settings.Value);
        }
    }

    public bool CanReleaseForLoad(ServingPlacement target, Guid victimEndpoint)
    {
        lock (sync)
            return target.Budget?.Load is { } load && observed?.Workers.GetValueOrDefault(victimEndpoint) is { } worker &&
                (load.RamBytes > 0 && worker.ResidentBytes > 0 || load.DeviceBytes.Any(pair => pair.Value > 0 &&
                    worker.DedicatedBytes?.GetValueOrDefault(pair.Key) > 0));
    }

    internal static bool Covers(Workload limit, Workload actual) =>
        limit.Operation == actual.Operation && limit.Units >= actual.Units;

    internal static MemoryCost ProjectExecutionCost(ResourceBudget budget, Workload actual, long payloadBytes = 0)
    {
        var ratio = Math.Max(1m, (decimal)actual.MediaItems / Math.Max(1, budget.Workload.MediaItems)) *
            Math.Max(1m, (decimal)actual.Items / Math.Max(1, budget.Workload.Items));
        long Scale(long value, long extra = 0) => (long)Math.Min(long.MaxValue,
            decimal.Ceiling(value * ratio + extra));
        // Account for buffered JSON/string copies separately from provider execution.
        var bytes = Math.Clamp(payloadBytes, 0, ProviderLimits.MaxRequestBytes) * 4;
        return new(Scale(budget.Execute.RamBytes, bytes),
            budget.Execute.DeviceBytes.ToDictionary(item => item.Key, item => Scale(item.Value)));
    }

    private static bool AttributionAllows(EndpointStatus endpoint, LocalSettings settings, ResourceObservation? observation,
        bool allowAttached) => endpoint.Mode == ProviderMode.Attached ? allowAttached :
        !settings.ActivityMonitoring || (endpoint.CanUnload || endpoint.CanStop ||
            !endpoint.Online && endpoint.Executable is not null);

    private bool Fits(MemoryCost cost, ResourceObservation? latest, long? freeRam, LocalSettings current,
        bool settledExclusive = false)
    {
        if (latest is null) return true;
        var reserve = Math.Max(ConfiguredRamReserve(latest, current),
            MinimumFree(latest.TotalRam, current.RamCutoffPercent));
        var outstandingRam = reservations.Sum(item => Unused(item, latest, null));
        if (latest.TotalRam > 0 && freeRam is not null && cost.RamBytes > freeRam - reserve - outstandingRam)
            return false;
        foreach (var id in cost.DeviceBytes.Keys.Concat(reservations.SelectMany(item => item.Cost.DeviceBytes.Keys)).Distinct())
        {
            var device = latest.Devices.FirstOrDefault(item => item.Id == id && !item.Integrated);
            if (device is not { TotalBytes: { } total, UsedBytes: { } used })
                continue;
            var outstanding = reservations.Sum(item => Unused(item, latest, id));
            if (settledExclusive && outstanding == 0 && cost.DeviceBytes.GetValueOrDefault(id) == 0)
                continue;
            var deviceReserve = Math.Max((long)current.EffectiveVramReserveMiB * MiB, MinimumFree(total, current.VramCutoffPercent));
            if (cost.DeviceBytes.GetValueOrDefault(id) > total - used - deviceReserve - outstanding)
                return false;
        }
        return true;
    }

    private static long MinimumFree(long total, int cutoff) => (long)Math.Ceiling(total * (100 - cutoff) / 100.0);

    private static long ConfiguredRamReserve(ResourceObservation? latest, LocalSettings current) =>
        (long)current.EffectiveRamReserveMiB(LocalSettings.DefaultRamReserveMiB((ulong)(latest?.TotalRam ?? 0))) * MiB;

    private long Unused(ResourceReservation item, ResourceObservation latest, string? device)
    {
        var reserved = device is null ? item.Cost.RamBytes : item.Cost.DeviceBytes.GetValueOrDefault(device);
        if (reserved == 0 || reservations.Count(other => other.Endpoint == item.Endpoint) > 1 ||
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
            if (item.Settled && item.Cost.DeviceBytes.Values.Any(value => value != 0))
                Interlocked.Exchange(ref settlementSample, 1);
            if (item.Settled && item.Cost.DeviceBytes.Values.All(value => value == 0))
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
            lock (sync)
                BeginWithdrawal("Mango could not persist a failed memory allocation.");
        }
        lock (sync)
        {
            if (observed is { } current && current.TotalRam > 0)
            {
                try
                {
                    var free = (long)ram().Available;
                    if (Pressure(current with { FreeRam = free }, settings.Value) is { } pressure)
                        BeginWithdrawal(pressure);
                }
                catch (Win32Exception) { WarnTelemetry(); }
            }
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
            if (withdraw || pending.Length != 0)
                await SaveHoldAsync(new(ContributionState.Withdrawing,
                    withdrawalReason ?? "A provider was still settling during shutdown.", pending, withdrawalCause));
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
            var pressure = nativeBenchmark ? null : Pressure(latest, current);
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
            if (pressure is not null)
                BeginWithdrawal(pressure);
            else
            {
                if (high)
                    highSince ??= latest.At;
                else
                    highSince = null;
                if (latest.At - highSince >= Sustained)
                    BeginWithdrawal("Sustained external CPU or GPU activity.");
                else if (withdrawalCause == WithdrawalCause.ExternalActivity)
                    BeginWithdrawal("Sustained external CPU or GPU activity.");
                else if (status.State is ContributionState.ManualLeech or ContributionState.Withdrawing)
                    SetStatus(new(ContributionState.Contributor, null, null));
            }
        }
    }

    private static string? Pressure(ResourceObservation? latest, LocalSettings current)
    {
        if (latest is null) return null;
        var reserve = ConfiguredRamReserve(latest, current);
        WorkerMemory? exclusive = null;
        if (latest.ExclusiveEndpoint is { } endpoint)
        {
            if (latest.FreeCommit is { } commit && commit < reserve)
                return "Windows commit headroom is too low to keep the Strata runtime loaded.";
            latest.Workers.TryGetValue(endpoint, out exclusive);
        }
        var freeForPressure = Math.Min(latest.TotalRam, latest.FreeRam + (exclusive?.ResidentBytes ?? 0));
        if (latest.TotalRam > 0 && (latest.FreeRam < reserve || (latest.TotalRam - freeForPressure) * 100.0 >= latest.TotalRam * current.RamCutoffPercent))
            return "Host RAM is below the reserve or above its pressure cutoff.";
        foreach (var device in latest.Devices.Where(device => !device.Integrated))
            if (device is { TotalBytes: { } total, UsedBytes: { } used })
            {
                var external = Math.Max(0, used - (exclusive?.DedicatedBytes?.GetValueOrDefault(device.Id) ?? 0));
                if (total - external < (long)current.EffectiveVramReserveMiB * MiB || external * 100.0 >= total * current.VramCutoffPercent)
                    return $"{DedicatedMemoryPressure}{device.Name}.";
            }
        return null;
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
            foreach (var lease in reservations.Where(item => !item.Yolo || settings.Value.Role == LocalRole.Leech))
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
                Pressure(latest, settings.Value) is null &&
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
            release = providers.ReleaseManagedAsync(cancellation, includeYolo: settings.Value.Role == LocalRole.Leech);
            _ = release.ContinueWith(_ => Signal(), TaskScheduler.Default);
            releaseSample = null;
            return;
        }
        if (release is { IsCompleted: true } finished)
        {
            var result = await finished;
            string? error = result.Error;
            lock (sync)
            {
                error ??= VerifyRelease(result, beforeRelease, observed);
                error ??= Pressure(CurrentObservation, settings.Value);
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
            ? new ResourceHold(current.State, current.Reason!, current.State == ContributionState.Fault ? Pending(providers) : null, withdrawalCause) : null;
        if (!withdraw && (prior?.State != hold?.State || prior?.Reason != hold?.Reason || prior?.Cause != hold?.Cause ||
                !(prior?.Pending ?? []).Select(item => (item.Deployment, item.Loading))
                    .SequenceEqual((hold?.Pending ?? []).Select(item => (item.Deployment, item.Loading)))))
            await SaveHoldAsync(hold);
    }

    private void ConfirmReleased(ProviderCatalog providers, Guid endpoint)
    {
        lock (sync)
        {
            providers.ConfirmReleased(endpoint, includeYolo: settings.Value.Role == LocalRole.Leech);
            foreach (var lease in reservations.Where(item => item.Endpoint == endpoint && !item.Running &&
                (!item.Yolo || settings.Value.Role == LocalRole.Leech)).ToArray())
                Settle(lease, true);
        }
    }

    private PendingRelease[] Pending(ProviderCatalog providers)
    {
        lock (sync)
        {
            var endpoints = providers.View.Endpoints;
            var pending = reservations.Where(item => !item.Settled).Select(item => (item.Endpoint, item.Deployment))
                .Concat(providers.Unsettled).Distinct().OrderBy(item => item.Deployment, StringComparer.Ordinal);
            return pending.Select(item =>
            {
                var root = endpoints.FirstOrDefault(endpoint => endpoint.Id == item.Endpoint)?.OwnedProcessId;
                var prior = store.Value.Hold?.Pending?.FirstOrDefault(value => value.Deployment == item.Deployment);
                var tree = root is { } pid
                    ? (observed?.Workers.GetValueOrDefault(item.Endpoint)?.Processes ?? []).Append(pid).Distinct().Take(256).ToArray()
                    : prior?.OwnedProcesses ?? [];
                return new PendingRelease(item.Endpoint, item.Deployment, tree,
                    providers.HasUnsettledLoad(item.Deployment) || reservations.Any(reservation =>
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
