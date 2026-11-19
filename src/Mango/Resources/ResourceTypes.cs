using Mango.Providers;

namespace Mango.Resources;

internal enum ContributionState { Contributor, Withdrawing, Cooldown, ManualLeech, Fault }
internal enum WithdrawalCause { Safety, ExternalActivity }
internal enum AdmissionStatus { Admitted, Wait, Unavailable }
internal enum ResourceWork { Load, Execute, Benchmark }

internal sealed record DeviceMemory(string Id, string Name, bool Integrated, long? TotalBytes, long? UsedBytes);
internal sealed record WorkerMemory(int[] Processes, long ResidentBytes, Dictionary<string, long>? DedicatedBytes,
    long? PeakResidentBytes, long? SharedGpuBytes = null);
internal sealed record ResourceObservation(DateTimeOffset At, long TotalRam, long FreeRam, double? ExternalCpu,
    double? ExternalGpu, DeviceMemory[] Devices, string? Hardware, Dictionary<Guid, WorkerMemory> Workers,
    bool WorkerAttribution, string? Limitation, double? ManagedCpu = null, double? TotalCpu = null, double? TotalGpu = null,
    long? FreeCommit = null, Guid? ExclusiveEndpoint = null);

internal sealed record MemoryCost(long RamBytes, Dictionary<string, long> DeviceBytes)
{
    public static readonly MemoryCost Empty = new(0, []);
}

// A text unit is one token in one sequence. Images use total pixels, audio uses bytes or input characters.
internal readonly record struct Workload(InferenceOperation Operation, long Units, int Items = 1, int MediaItems = 0,
    int? Width = null, int? Height = null);
internal sealed record ResourceBudget(string Deployment, string Configuration, string? Runtime, Workload Workload,
    MemoryCost Load, MemoryCost Execute, bool AllowOverlap, int ParallelSlots = 1, bool AllowAttached = false, bool Estimated = false);
internal sealed record PendingRelease(Guid Endpoint, string Deployment, int[] OwnedProcesses, bool Loading = false);
internal sealed record ResourceHold(ContributionState State, string Reason, PendingRelease[]? Pending = null,
    WithdrawalCause Cause = WithdrawalCause.Safety);
internal sealed record ResourceEvent(DateTimeOffset At, string Reason, bool Loading, bool Running,
    long? FreeRamBytes, double? ExternalCpu, double? ExternalGpu);

internal sealed record BenchmarkMeasurement(Workload Workload, string Pass, int? PromptTokens, int? OutputTokens,
    TimeSpan Elapsed, TimeSpan? FirstToken, double? PromptMilliseconds, double? TokensPerSecond, bool? ReusedCache,
    MemoryCost? ObservedIncrement, long? WorkerPeakRamBytes, int Samples, int? CachedTokens = null);
internal sealed record BenchmarkProfile(string Deployment, string Configuration, string Runtime, InferenceOperation Operation,
    DateTimeOffset Updated, BenchmarkMeasurement[] Measurements, bool Complete, string? Error, BenchmarkSpeed? Speed = null);
internal sealed record BenchmarkSpeed(DateTimeOffset At, double? TokensPerSecond, string? Error,
    TimeSpan? Elapsed = null, int? PromptTokens = null, int Attempts = 0, bool? ProviderGenerationRate = null,
    TimeSpan? FirstToken = null, int? OutputTokens = null);
internal sealed record ResourceData(ResourceBudget[] Budgets, BenchmarkProfile[] Profiles, string[] Unsafe,
    ResourceHold? Hold)
{
    public static readonly ResourceData Empty = new([], [], [], null);
}

internal sealed record ResourceStatus(ContributionState State, string? Reason, DateTimeOffset? QuietUntil);
internal sealed record ServingReadiness(string State, string Reason, string? Action, bool Available);
internal sealed record Admission(AdmissionStatus Status, string? Reason, ResourceReservation? Reservation, bool MemoryWait = false)
{
    public ResourceReservation Require() => Reservation ??
        throw new ProviderFailure(503, Reason ?? "Local admission is unavailable.", capacityWait: Status == AdmissionStatus.Wait,
            memoryWait: MemoryWait);
}

internal sealed class ResourceReservation(ResourceProtection owner, Guid endpoint, string deployment, ResourceWork work,
    string fingerprint, MemoryCost cost, bool overlap, long sample, WorkerMemory? baseline, bool yolo = false,
    bool nativeBenchmark = false) : IDisposable
{
    internal readonly CancellationTokenSource Stop = new();
    internal readonly Guid Endpoint = endpoint;
    internal readonly string Deployment = deployment;
    internal readonly ResourceWork Work = work;
    internal readonly string Fingerprint = fingerprint;
    internal readonly MemoryCost Cost = cost;
    internal readonly bool Overlap = overlap;
    internal readonly long Sample = sample;
    internal readonly WorkerMemory? Baseline = baseline;
    internal readonly bool Yolo = yolo;
    internal readonly bool NativeBenchmark = nativeBenchmark;
    internal bool Running = true;
    internal bool Settled;
    internal bool Disposed;

    public CancellationToken Cancellation => Stop.Token;
    public void Cancel() => owner.Cancel(this);
    public void Complete(bool settled = true) => owner.Settle(this, settled);
    public void ReleaseUnstarted() => owner.ReleaseUnstarted(this);
    public void Dispose() => owner.Settle(this, false);
}
