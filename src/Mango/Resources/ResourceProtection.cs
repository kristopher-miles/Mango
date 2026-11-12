using System.ComponentModel;
using Mango.Host;
using Mango.Providers;

namespace Mango.Resources;

internal sealed partial class ResourceProtection : IDisposable
{
    internal const long MiB = 1024 * 1024;
    private static readonly TimeSpan Sustained = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BenchmarkIdle = TimeSpan.FromMinutes(5);
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
    private TaskCompletionSource nextObservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset? highSince;
    private DateTimeOffset? quietSince;
    private DateTimeOffset? lastClient;
    private ResourceEvent? lastEvent;
    private bool withdraw;
    private string? withdrawalReason;
    private Task<ReleaseResult>? release;
    private ResourceObservation? beforeRelease;
    private long? releaseSample;
    private CancellationTokenSource? benchmarkStop;
    private Task? benchmark;
    private BenchmarkPolicy policy;
    private int stopping;
    public event Action? Changed;

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
        unsafePlacements.UnionWith(store.Value.Unsafe);
        policy = settings.Value.BenchmarkPolicy;
        var hold = store.Value.Hold;
        if (hold is not null)
            status = new(hold.State, hold.Reason, null);
        if (hold?.State == ContributionState.Withdrawing ||
            hold?.State != ContributionState.Fault && settings.Value.Role == LocalRole.Leech)
            BeginWithdrawal(hold?.Reason ?? "Local leech mode is enabled.");
        settings.Changed += SettingsChanged;
    }

    public void RestoreUnsettled(ProviderCatalog providers)
    {
        foreach (var pending in store.Value.Hold?.Pending ?? [])
            if (pending.OwnedProcesses.Length == 0 || pending.OwnedProcesses.Any(WindowsObservation.ProcessExists))
                providers.RestoreUnsettled(pending.Endpoint, pending.Deployment);
    }

    private void SettingsChanged()
    {
        lock (sync)
        {
            if (settings.Value.Role == LocalRole.Leech && status.State is not (ContributionState.ManualLeech or ContributionState.Fault))
                BeginWithdrawal("Local leech mode is enabled.");
            var next = settings.Value.BenchmarkPolicy;
            if (policy != next)
            {
                policy = next;
                SetBenchmarkReady(next == BenchmarkPolicy.Never);
                if (next == BenchmarkPolicy.Never)
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
            if (settings.Value.BenchmarkPolicy == BenchmarkPolicy.WhenConvenient)
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

    public bool HasRunning(Guid endpoint)
    {
        lock (sync)
            return reservations.Any(item => item.Endpoint == endpoint && item.Running);
    }

    public bool IsAvailable(Deployment model, EndpointStatus endpoint, InferenceOperation? requested = null)
    {
        lock (sync)
        {
            if (!ServingAllowed || observed is null || !model.Model.Installed ||
                !model.Eligible && !(endpoint.CanLoad && model.Model.SupportsLoad))
                return false;
            return Operations(model, endpoint).Any(operation =>
            {
                if (requested is not null && requested != operation)
                    return false;
                var budget = FindBudget(model, endpoint, operation);
                return budget is not null && AttributionAllows(endpoint, settings.Value, observed, budget.AllowAttached) &&
                    !unsafePlacements.Contains(FaultKey(budget)) &&
                    (operation is not (InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne) ||
                        model.Context is null || budget.Workload.Units >= model.Context);
            });
        }
    }

    public Admission TryAdmit(Deployment model, EndpointStatus endpoint, Workload workload, ResourceWork work, bool profiling = false)
    {
        var sampled = Observation;
        var needsFreshGpu = windows is not null && sampled is not null && sampled.Devices.Any(device => !device.Integrated) &&
            clock.GetUtcNow() - sampled.At > TimeSpan.FromSeconds(LocalSettings.MinimumSampleSeconds);
        var freshGpu = needsFreshGpu ? windows!.ReadFreshGpuMemory() : null;
        lock (sync)
        {
            if (status.State != ContributionState.Contributor || settings.Value.Role != LocalRole.Contributor ||
                Volatile.Read(ref stopping) != 0)
                return Refuse(status.Reason ?? "This node is in leech mode.");
            if (work == ResourceWork.Load && (endpoint.Mode != ProviderMode.Managed || !endpoint.CanLoad || !model.Model.SupportsLoad))
                return Refuse("The provider does not authorize loading this deployment.");
            var current = settings.Value;
            if (!profiling && work != ResourceWork.Load && current.BenchmarkPolicy == BenchmarkPolicy.Now && !benchmarksReady)
                return Refuse("The local benchmark policy requires complete profiles before inference.");
            if (observed is not { } latest || clock.GetUtcNow() - latest.At > TimeSpan.FromSeconds(current.SampleSeconds + 5))
                return Refuse("Waiting for current Windows memory and activity observations.");
            if (latest.Hardware is null)
                return Refuse("Windows GPU memory topology is unavailable.");
            if (needsFreshGpu && ReferenceEquals(latest, sampled))
            {
                if (freshGpu is null || freshGpu.Any(device => !device.Integrated && device.UsedBytes is null))
                {
                    BeginWithdrawal("A fresh reading of dedicated GPU memory is unavailable.");
                    return Refuse("Waiting for a fresh dedicated GPU memory reading.");
                }
                latest = latest with { Devices = freshGpu };
            }
            var budget = FindBudget(model, endpoint, workload.Operation);
            if (budget is null || !Covers(budget.Workload, workload))
                return Refuse("Set a local memory and workload limit for this deployment and operation.");
            if (!AttributionAllows(endpoint, current, latest, budget.AllowAttached))
                return Refuse(endpoint.Mode == ProviderMode.Attached
                    ? "Confirm the attached provider limitation in this deployment's local memory limits."
                    : "Automatic protection requires attributable workers and a verified managed release path.");
            var fingerprint = FaultKey(budget);
            if (unsafePlacements.Contains(fingerprint))
                return Refuse("An allocation failed for this placement. Update its local limits or retry explicitly.");
            var cost = work == ResourceWork.Load ? budget.Load : budget.Execute;
            if (work == ResourceWork.Load && cost.RamBytes == 0 && cost.DeviceBytes.Values.All(value => value == 0))
                return Refuse("Set a peak memory allowance before loading this model.");
            var overlap = current.BenchmarkPolicy != BenchmarkPolicy.Never && budget.AllowOverlap &&
                !profiling && work == ResourceWork.Execute && workload.Items == 1;
            var peersOnEndpoint = reservations.Where(item => item.Endpoint == model.EndpointId && !item.Settled).ToArray();
            var slots = Math.Min(budget.ParallelSlots, model.Instance?.Slots ?? 1);
            if (reservations.Any(item => !item.Settled &&
                (!overlap || !item.Overlap || !item.Running || (item.Endpoint == model.EndpointId
                    ? item.Deployment != model.Id : !SeparateWorkers(latest, item.Endpoint, model.EndpointId)))) ||
                peersOnEndpoint.Length >= slots)
                return new(AdmissionStatus.Wait, "Local resources are reserved by another provider operation.", null);
            long freeRam;
            try { freeRam = (long)ram().Available; }
            catch (Win32Exception)
            {
                BeginWithdrawal("A fresh reading of host RAM is unavailable.");
                return Refuse("Waiting for a fresh host RAM reading.");
            }
            if (Pressure(latest with { FreeRam = freeRam }, current) is { } pressure)
            {
                BeginWithdrawal(pressure);
                return Refuse(pressure);
            }
            if (!Fits(cost, latest, freeRam, current))
                return new(AdmissionStatus.Wait, "The request does not fit current RAM or per-device VRAM reserves.", null);
            var lease = new ResourceReservation(this, model.EndpointId, model.Id, work, fingerprint, cost, overlap,
                sampleNumber, latest.Workers.GetValueOrDefault(model.EndpointId));
            reservations.Add(lease);
            return new(AdmissionStatus.Admitted, null, lease);
        }
    }

    private static Admission Refuse(string reason) => new(AdmissionStatus.Unavailable, reason, null);
    private static bool SeparateWorkers(ResourceObservation latest, Guid first, Guid second) =>
        latest.Workers.TryGetValue(first, out var a) && latest.Workers.TryGetValue(second, out var b) &&
        !a.Processes.Intersect(b.Processes).Any();

    internal static bool Covers(Workload limit, Workload actual) =>
        limit.Operation == actual.Operation && limit.Units >= actual.Units && limit.Items >= actual.Items &&
        limit.MediaItems >= actual.MediaItems;

    private static bool AttributionAllows(EndpointStatus endpoint, LocalSettings settings, ResourceObservation observation,
        bool allowAttached) => endpoint.Mode == ProviderMode.Attached ? allowAttached :
        !settings.ActivityMonitoring || (endpoint.CanUnload || endpoint.CanStop ||
            !endpoint.Online && endpoint.Executable is not null) && observation.WorkerAttribution;

    private bool Fits(MemoryCost cost, ResourceObservation latest, long freeRam, LocalSettings current)
    {
        var reserve = Math.Max((long)current.EffectiveRamReserveMiB(LocalSettings.DefaultRamReserveMiB((ulong)latest.TotalRam)) * MiB,
            MinimumFree(latest.TotalRam, current.RamCutoffPercent));
        var outstandingRam = reservations.Sum(item => Unused(item, latest, null));
        if (cost.RamBytes > freeRam - reserve - outstandingRam)
            return false;
        foreach (var id in cost.DeviceBytes.Keys.Concat(reservations.SelectMany(item => item.Cost.DeviceBytes.Keys)).Distinct())
        {
            var device = latest.Devices.FirstOrDefault(item => item.Id == id && !item.Integrated);
            if (device is not { TotalBytes: { } total, UsedBytes: { } used })
                return false;
            var outstanding = reservations.Sum(item => Unused(item, latest, id));
            var deviceReserve = Math.Max((long)current.EffectiveVramReserveMiB * MiB, MinimumFree(total, current.VramCutoffPercent));
            if (cost.DeviceBytes.GetValueOrDefault(id) > total - used - deviceReserve - outstanding)
                return false;
        }
        return true;
    }

    private static long MinimumFree(long total, int cutoff) => (long)Math.Ceiling(total * (100 - cutoff) / 100.0);

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
            if (item.Settled && item.Cost.DeviceBytes.Values.All(value => value == 0))
                Remove(item);
        }
        Signal();
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
                catch (Win32Exception) { BeginWithdrawal("Windows could not confirm memory after an allocation failure."); }
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
                var view = providers.View;
                if (now >= nextSample)
                {
                    ResourceObservation latest;
                    try { latest = sample(view, now); }
                    catch (Win32Exception)
                    {
                        latest = new(now, 0, 0, null, null, [], null, [], false,
                            "Windows resource observations are temporarily unavailable.");
                    }
                    sampled = now;
                    nextSample = sampled + interval;
                    ApplyObservation(latest);
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
                    withdrawalReason ?? "A provider was still settling during shutdown.", pending));
        }
    }

    internal void ApplyObservation(ResourceObservation latest)
    {
        lock (sync)
        {
            var current = settings.Value;
            if (observed is { } previous && latest.At - previous.At > TimeSpan.FromSeconds(current.SampleSeconds + 3))
                highSince = quietSince = null;
            observed = latest;
            sampleNumber++;
            nextObservation.TrySetResult();
            nextObservation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (var item in reservations.Where(item => item.Settled && item.Sample < sampleNumber).ToArray())
                Remove(item);
            ObserveBenchmark(latest);
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
            var pressure = Pressure(latest, current);
            var unknown = latest.Hardware is null || latest.Devices.Any(device => !device.Integrated &&
                (device.TotalBytes is null || device.UsedBytes is null)) ||
                current.ActivityMonitoring && (!latest.WorkerAttribution || latest.ExternalCpu is null || latest.ExternalGpu is null);
            var high = current.ActivityMonitoring && (latest.ExternalCpu >= current.CpuCutoffPercent ||
                latest.ExternalGpu >= current.GpuCutoffPercent);
            if (status.State == ContributionState.Cooldown)
            {
                if (pressure is not null || high || unknown)
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
            else if (unknown)
            {
                highSince = null;
                if (status.State == ContributionState.Contributor)
                    BeginWithdrawal(latest.Limitation ?? "A required resource observation became unavailable.");
                else
                    SetStatus(new(ContributionState.Withdrawing, latest.Limitation ?? "Waiting for reliable resource observations.", null));
            }
            else
            {
                if (high)
                    highSince ??= latest.At;
                else
                    highSince = null;
                if (latest.At - highSince >= Sustained)
                    BeginWithdrawal("Sustained external CPU or GPU activity.");
                else if (status.State is ContributionState.ManualLeech or ContributionState.Withdrawing)
                    SetStatus(new(ContributionState.Contributor, null, null));
            }
        }
    }

    private static string? Pressure(ResourceObservation latest, LocalSettings current)
    {
        if (latest.TotalRam <= 0)
            return null;
        var reserve = (long)current.EffectiveRamReserveMiB(LocalSettings.DefaultRamReserveMiB((ulong)latest.TotalRam)) * MiB;
        if (latest.FreeRam < reserve || (latest.TotalRam - latest.FreeRam) * 100.0 >= latest.TotalRam * current.RamCutoffPercent)
            return "Host RAM is below the reserve or above its pressure cutoff.";
        foreach (var device in latest.Devices.Where(device => !device.Integrated))
            if (device is { TotalBytes: { } total, UsedBytes: { } used } &&
                (total - used < (long)current.EffectiveVramReserveMiB * MiB || used * 100.0 >= total * current.VramCutoffPercent))
                return $"Dedicated memory pressure on {device.Name}.";
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
            highSince = quietSince = null;
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
        if (status == next)
            return;
        status = next;
        Changed?.Invoke();
    }

    internal async Task AdvanceWithdrawalAsync(ProviderCatalog providers, CancellationToken cancellation)
    {
        if (withdraw && release is null)
        {
            if (observed is null)
                return;
            beforeRelease = observed;
            foreach (var pending in store.Value.Hold?.Pending ?? [])
                if (pending.OwnedProcesses.Length != 0 && pending.OwnedProcesses.All(pid => !WindowsObservation.ProcessExists(pid)))
                    ConfirmReleased(providers, pending.Endpoint);
            await SaveHoldAsync(new(ContributionState.Withdrawing, withdrawalReason!, Pending(providers)));
            release = providers.ReleaseManagedAsync(cancellation);
            _ = release.ContinueWith(_ => Signal(), TaskScheduler.Default);
            releaseSample = null;
            return;
        }
        if (release is { IsCompleted: true } finished)
        {
            var result = await finished;
            if (result.Released.Length != 0 && result.Error is null)
            {
                releaseSample ??= sampleNumber;
                if (sampleNumber <= releaseSample)
                    return;
            }
            string? error = result.Error;
            lock (sync)
            {
                error ??= VerifyRelease(result, beforeRelease!, observed!);
                if (result.Error is null && error is not null && sampleNumber < releaseSample + 3)
                    return;
                if (error is null)
                    foreach (var endpoint in result.Released)
                        ConfirmReleased(providers, endpoint.Id);
                release = null;
                withdraw = false;
                if (error is not null)
                    SetStatus(new(ContributionState.Fault, error, null));
                else if (settings.Value.Role == LocalRole.Leech)
                    SetStatus(new(ContributionState.ManualLeech, withdrawalReason, null));
                else
                    SetStatus(new(ContributionState.Cooldown, withdrawalReason, null));
            }
        }
        var current = Status;
        var prior = store.Value.Hold;
        var hold = current.State is ContributionState.Cooldown or ContributionState.Fault
            ? new ResourceHold(current.State, current.Reason!, current.State == ContributionState.Fault ? Pending(providers) : null) : null;
        if (!withdraw && (prior?.State != hold?.State || prior?.Reason != hold?.Reason ||
            !(prior?.Pending ?? []).Select(item => item.Deployment).SequenceEqual((hold?.Pending ?? []).Select(item => item.Deployment))))
            await SaveHoldAsync(hold);
    }

    private void ConfirmReleased(ProviderCatalog providers, Guid endpoint)
    {
        lock (sync)
        {
            providers.ConfirmReleased(endpoint);
            foreach (var lease in reservations.Where(item => item.Endpoint == endpoint && !item.Running).ToArray())
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
                return new PendingRelease(item.Endpoint, item.Deployment, tree);
            }).ToArray();
        }
    }

    private async Task SaveHoldAsync(ResourceHold? hold)
    {
        try { await store.UpdateAsync(data => data with { Hold = hold }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            lock (sync)
                SetStatus(new(ContributionState.Fault, "Mango could not save the local resource safety state.", null));
        }
    }

    private static string? VerifyRelease(ReleaseResult result, ResourceObservation before, ResourceObservation after)
    {
        foreach (var endpoint in result.Released)
        {
            var prior = before.Workers.GetValueOrDefault(endpoint.Id);
            var remaining = after.Workers.GetValueOrDefault(endpoint.Id);
            if (endpoint.StoppedProcess is { } stopped)
            {
                if (remaining is not null || WindowsObservation.ProcessExists(stopped) ||
                    prior?.Processes.Any(WindowsObservation.ProcessExists) == true)
                    return "A worker from the managed provider tree is still running.";
            }
            else if (prior is null || remaining is null || !VerifiedMemoryDrop(prior, remaining,
                before.Devices.Any(device => !device.Integrated)))
                return "The provider reported unload, but Windows did not confirm a drop in its resident memory.";
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
