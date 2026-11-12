using Mango.Logging;
using Mango.Providers;
using Mango.Resources;
using Mango.Scheduling;

namespace Mango.Peers;

internal sealed partial class PeerNetwork
{
    internal LogStore Logs { get; } = new(directory, time ?? TimeProvider.System);
    private readonly SemaphoreSlim logSchedule = new(0, 1);
    private readonly Guid localLogWriter = Guid.NewGuid();
    private ResourceEvent? lastLoggedResourceEvent;

    private static LogConfiguration LogConfigurationFor(PeerSnapshot snapshot)
    {
        var identity = snapshot.Records.Identity;
        var policy = identity is null ? snapshot.Disk.LocalLogPolicy : snapshot.Records.Logs;
        var generation = identity is null ? snapshot.Disk.LocalLogDeletion :
            snapshot.Records.LogDeletion is { } clear && clear.CompareTo(identity.Generation) > 0 ? clear : identity.Generation;
        return new(new(identity?.Id, generation), policy ?? (identity is null ? LogPolicy.Default : LogPolicy.ExistingNetworkDefault),
            snapshot.Self?.Id ?? snapshot.Local?.Node ?? Guid.Empty, snapshot.Self?.Name);
    }

    private void ConfigureLogs(PeerSnapshot before, PeerSnapshot after)
    {
        var previous = LogConfigurationFor(before);
        var next = LogConfigurationFor(after);
        if (previous != next)
        {
            Logs.Configure(next);
            if (logSchedule.CurrentCount == 0)
                logSchedule.Release();
        }
    }

    public async Task CaptureInferenceAsync(InferenceOperation operation, HttpContext context, Func<Task> inference)
    {
        var config = Logs.Configuration;
        if (config.Policy.Mode == RequestLogMode.Off || !Logs.Available(config.Epoch))
        {
            await inference();
            return;
        }
        var capture = new LogCapture(Logs, config, context) { Clock = clock, Started = clock.GetTimestamp() };
        var at = clock.GetUtcNow();
        var access = InferenceAccess.Of(context);
        var fallback = new LogEntry(capture.Id, LogKind.Request, at, config.Node, config.Name, Request: capture.Id,
            Ingress: config.Node, IngressName: config.Name, Key: access?.KeyId, KeyName: access?.KeyName,
            Source: InferenceSource.Of(context), Task: operation, Priority: access?.Priority);
        capture.Entry = fallback;
        try { await capture.RunAsync(inference); }
        finally { capture.Finish(fallback); }
    }

    private void RecordRoute(LogCapture? capture, IngressRequest request, InferenceRequest payload)
    {
        if (capture?.Entry is not { } entry)
            return;
        var current = Snapshot;
        var last = request.Attempts.LastOrDefault();
        capture.Entry = entry with
        {
            Alias = request.Alias, Deployment = last?.Deployment,
            Outcome = last?.State.ToString().ToLowerInvariant(),
            Reason = request.Reason ?? last?.Reason,
            Routes = request.Attempts.Select(attempt => new LogRoute(attempt.Attempt, attempt.Node,
                current.Nodes.GetValueOrDefault(attempt.Node)?.Name, attempt.Deployment, attempt.State.ToString().ToLowerInvariant(),
                attempt.Status, attempt.Reason, attempt.QueueMilliseconds, attempt.LoadMilliseconds)).ToArray(),
            Items = payload.Shape.Items,
            RequestedPixels = payload.Operation == InferenceOperation.Images ? payload.Shape.WorkUnits : null
        };
    }

    private void RecordServing(LogConfiguration config, Guid origin, AttemptTicket ticket, InferenceRequest payload,
        InferenceAccess access, AttemptReply result, DateTimeOffset at, long started)
    {
        if (config.Policy.Mode == RequestLogMode.Off || config.Epoch.Generation != ticket.LogGeneration)
            return;
        var timing = result.Timing;
        Logs.Queue(config, new(Guid.NewGuid(), LogKind.Attempt, at, config.Node, config.Name,
            Request: ticket.Request, Attempt: ticket.Attempt, Ingress: origin, IngressName: Snapshot.Nodes.GetValueOrDefault(origin)?.Name,
            Key: access.KeyId, KeyName: access.KeyName, Source: ticket.Source ?? InferenceSource.Unknown, Alias: payload.Alias,
            Deployment: ticket.Deployment, Task: payload.Operation, Priority: access.Priority,
            Outcome: result.State.ToString().ToLowerInvariant(), Reason: result.Reason, Status: result.Status,
            RequestBytes: payload.Bytes, ResponseBytes: timing?.OutputBytes, ContextTokens: timing?.ContextTokens,
            PromptTokens: timing?.PromptTokens, OutputTokens: timing?.OutputTokens, PromptCountSource: timing?.PromptCountSource,
            QueueMilliseconds: result.QueueMilliseconds, LoadMilliseconds: result.LoadMilliseconds,
            PreparationMilliseconds: timing?.Preparation?.TotalMilliseconds,
            ProviderHeadersMilliseconds: timing?.ProviderHeaders.TotalMilliseconds,
            FirstOutputMilliseconds: timing?.FirstOutput?.TotalMilliseconds,
            FirstTokenMilliseconds: timing?.FirstToken?.TotalMilliseconds,
            ElapsedMilliseconds: clock.GetElapsedTime(started).TotalMilliseconds, TokensPerSecond: timing?.TokensPerSecond,
            Items: payload.Shape.Items, RequestedPixels: payload.Operation == InferenceOperation.Images ? payload.Shape.WorkUnits : null,
            AudioSeconds: timing?.AudioSeconds, CompletedAt: clock.GetUtcNow()));
    }

    private void LogOperation(string outcome, string reason, string? deployment = null, int? status = null)
    {
        var config = Logs.Configuration;
        if (!config.Policy.Operational)
            return;
        Logs.Queue(config, new(Guid.NewGuid(), LogKind.Operation, clock.GetUtcNow(), config.Node, config.Name,
            Outcome: outcome, Reason: reason, Deployment: deployment, Status: status));
    }

    private void ObserveResourceLog()
    {
        if (providers.Resources?.LastEvent is { } change && !ReferenceEquals(Interlocked.Exchange(ref lastLoggedResourceEvent, change), change))
            LogOperation("resource-protection", change.Reason);
    }

    private void LogLifecycleFailure(string deployment, bool load, int status) => LogOperation(
        load ? "provider-load" : "provider-unload", "The provider lifecycle operation failed.", deployment, status);

    private Action<BenchmarkResult?>? BeginBenchmarkLog(string deployment, Workload workload, string pass)
    {
        var config = Logs.Configuration;
        if (config.Policy.Mode == RequestLogMode.Off || !Logs.Available(config.Epoch))
            return null;
        var at = clock.GetUtcNow();
        return result => Logs.Queue(config, new(Guid.NewGuid(), LogKind.Benchmark, at, config.Node, config.Name,
            Deployment: deployment, Task: workload.Operation, Outcome: result is null ? "failed" : "completed", Reason: pass,
            PromptTokens: result?.PromptTokens, OutputTokens: result?.OutputTokens, FirstTokenMilliseconds: result?.FirstToken?.TotalMilliseconds,
            ElapsedMilliseconds: result?.Elapsed.TotalMilliseconds, TokensPerSecond: result?.TokensPerSecond, Items: workload.Items,
            CompletedAt: clock.GetUtcNow()));
    }

    public async Task<object> SetLogPolicyAsync(HttpRequest request, LogSettingsUpdate input, CancellationToken cancellation)
    {
        var (before, after) = await ChangeAsync(current =>
        {
            var standalone = current.Records.Identity is null;
            var writer = standalone ? localLogWriter : RequireMember(current).Id;
            if (!standalone)
                RequireAdmin(current, AdminHeader(request));
            var policy = new LogPolicy(Next(current, writer), input.Mode, input.Operational, input.TimeZone,
                input.RawDays, input.AggregateDays, input.CapMiB);
            if (!policy.Valid)
                throw new PeerFailure(400, "Choose a valid log mode, IANA timezone, retention of 1 to 365 days, and cap of 1 to 16384 MiB.");
            return standalone ? current.Disk with { LocalLogPolicy = policy } :
                current.Disk with { Records = current.Records with { Logs = policy } };
        }, cancellation);
        await Logs.PrepareAsync(stopping.Token);
        return await PushChangesAsync(before, after, stopping.Token);
    }

    public async Task<object> ClearLogsAsync(HttpRequest request, bool confirm, CancellationToken cancellation)
    {
        if (!confirm)
            throw new PeerFailure(400, "Confirm that Mango should clear retained logs.");
        var (before, after) = await ChangeAsync(current =>
        {
            var standalone = current.Records.Identity is null;
            var writer = standalone ? localLogWriter : RequireMember(current).Id;
            if (!standalone)
                RequireAdmin(current, AdminHeader(request));
            var revision = Next(current, writer);
            if (current.Disk.LocalLogDeletion is { } local && revision.CompareTo(local) <= 0)
                revision = new(local.Time + 1, writer);
            return standalone ? current.Disk with { LocalLogDeletion = revision } :
                current.Disk with { Records = current.Records with { LogDeletion = revision } };
        }, cancellation);
        var localCleared = await Logs.PrepareAsync(stopping.Token);
        var delivery = await PushChangesAsync(before, after, stopping.Token);
        return new { LocalCleared = localCleared, Delivery = delivery, Clear = LogClearView(Snapshot) };
    }

    private object LogClearView(PeerSnapshot snapshot)
    {
        var config = LogConfigurationFor(snapshot);
        var marker = snapshot.Records.LogDeletion ?? snapshot.Disk.LocalLogDeletion;
        var pending = marker is null ? [] : snapshot.Others.Where(node => !snapshot.Records.Catalogs.Any(catalog =>
            catalog.Node == node.Id && catalog.AppliedLogGeneration == config.Epoch.Generation)).Select(node => new { node.Id, node.Name }).ToArray();
        return new { Generation = marker, LocalApplied = Logs.Available(config.Epoch), Pending = pending };
    }
}
