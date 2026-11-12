using Mango.Peers;
using Mango.Providers;

namespace Mango.Scheduling;

internal enum AttemptState { Waiting, Running, Rejected, Stopped, Failed, Uncertain, Completed }

internal sealed record AttemptReply(AttemptState State, int Status = 503, string? Reason = null,
    string? Epoch = null, Guid? SuggestedNode = null, ProviderExecution? Timing = null, bool Unreachable = false,
    double? QueueMilliseconds = null, double? LoadMilliseconds = null)
{
    public bool Retryable => State is AttemptState.Rejected or AttemptState.Stopped or AttemptState.Failed &&
        (Status is 409 or 422 || Status >= 500);
}

internal sealed record AlternateProbe(Guid? Key, InferenceShape Shape);

internal sealed record AttemptTicket(Guid Request, Guid Attempt, Guid? Key, string Deployment,
    DateTimeOffset Arrived, DateTimeOffset QueueUntil, string? Session, string ServingEpoch, InferenceSource? Source = null,
    Revision? LogGeneration = null);

internal sealed record PlacementChoice(Guid Node, string Deployment, string? Epoch, bool Loaded, int Slots,
    double? PromptMilliseconds, double? CacheSavingsMilliseconds, int Scarcity, string ServingEpoch, bool VerifiedFit);

internal sealed record AttemptTrace(Guid Attempt, Guid Node, string Deployment, DateTimeOffset Started,
    AttemptState State, int Status, ProviderExecution? Timing = null, string? Reason = null,
    double? QueueMilliseconds = null, double? LoadMilliseconds = null);

internal sealed record ServingActivity(Guid Request, Guid Attempt, Guid Origin, string Alias, string Deployment,
    KeyPriority Priority, InferenceSource Source, DateTimeOffset Arrived, DateTimeOffset? Finished, AttemptState State,
    string? Reason, double QueueMilliseconds, double? LoadMilliseconds, bool Loading);

internal sealed class IngressRequest(Guid id, DateTimeOffset arrived, DateTimeOffset queueUntil, InferenceAccess access,
    InferenceSource source, string alias, InferenceOperation operation)
{
    public Guid Id { get; } = id;
    public DateTimeOffset Arrived { get; } = arrived;
    public DateTimeOffset QueueUntil { get; } = queueUntil;
    public InferenceAccess Access { get; } = access;
    public InferenceSource Source { get; } = source;
    public string Alias { get; } = alias;
    public InferenceOperation Operation { get; } = operation;
    public List<AttemptTrace> Attempts { get; } = [];
    public bool OutputStarted { get; set; }
    public string? Reason { get; set; }
}
