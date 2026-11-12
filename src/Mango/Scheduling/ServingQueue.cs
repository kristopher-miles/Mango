using System.Threading.Channels;
using Mango.Peers;
using Mango.Providers;
using Mango.Resources;

namespace Mango.Scheduling;

internal interface IServingBackend
{
    event Action? Changed;
    event Action? AdmissionChanged;
    ResourceProtection? Resources { get; }
    ServingPlacement? Placement(InferenceShape shape, string id, bool allowUnverifiedContext = true);
    bool Conflicts(ServingPlacement a, ServingPlacement b);
    bool HasUnsettled(string deployment);
    StartExecution TryStart(InferenceRequest request, string id);
    Task PreparePlacementAsync(ServingPlacement placement, Func<string, Task> displaced, CancellationToken cancellation);
}

internal sealed class ServingQueue : IAsyncDisposable
{
    private const int MaxWaiting = 512;
    private const int MaxAttempts = 8192;
    private const long MaxWaitingBytes = 384L * 1024 * 1024;
    private readonly IServingBackend providers;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private readonly Dictionary<(Guid Origin, Guid Attempt), Entry> entries = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly CancellationTokenSource stop = new();
    private readonly Task pump;
    private int waiting;
    private long waitingBytes;
    private long sequence;
    private long capacityRevision;
    private int activityGeneration;
    private readonly Queue<ServingActivity> recentActivity = new();
    private (Entry Entry, Task<AttemptReply?> Work)? loading;
    private (Entry Entry, Task<ScarcityProof?> Check, ScarcityCheck Target)? verifying;
    public event Action? ActivityChanged;

    internal sealed class Work(Guid origin, AttemptTicket ticket, InferenceRequest payload, InferenceAccess access,
        HttpContext context, Func<AlternateProbe, CancellationToken, Task<Guid?>> alternate, Action<Work, Guid> advise,
        Func<string, CancellationToken, Task> displaced, Func<string> digest, Func<InferenceShape, Guid?>? suggest = null)
    {
        public Guid Origin { get; } = origin;
        public AttemptTicket Ticket { get; } = ticket;
        public InferenceRequest Payload { get; } = payload;
        public InferenceAccess Access { get; } = access;
        public HttpContext Context { get; } = context;
        public AlternateProbe Probe { get; } = new(ticket.Key, payload.Shape);
        public Func<AlternateProbe, CancellationToken, Task<Guid?>> Alternate { get; } = alternate;
        public Action<Work, Guid> Advise { get; } = advise;
        public Func<string, CancellationToken, Task> Displaced { get; } = displaced;
        public Func<string> Digest { get; } = digest;
        public Func<InferenceShape, Guid?>? Suggest { get; } = suggest;
        public ScarcityProof? Protection { get; set; }
    }

    internal sealed record ScarcityProof(string Digest, Dictionary<Guid, AlternateProbe> Flexible);
    private sealed record ScarcityCheck(string Digest, Guid[] Blockers);

    private sealed class Entry(DateTimeOffset retainUntil)
    {
        public DateTimeOffset RetainUntil { get; set; } = retainUntil;
        public TaskCompletionSource<AttemptReply> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Work? Work { get; set; }
        public CancellationTokenSource? Stop { get; set; }
        public CancellationTokenRegistration Cancellation { get; set; }
        public ServingPlacement? Placement { get; set; }
        public AttemptReply State { get; set; } = new(AttemptState.Waiting);
        public long Sequence { get; set; }
        public long LastLoadRevision { get; set; } = -1;
        public bool AwaitingSettlement { get; set; }
        public ScarcityCheck? Denied { get; set; }
        public bool Rechecking { get; set; }
        public long QueuedAt { get; init; }
        public int ActivityGeneration { get; init; }
        public double? QueueMilliseconds { get; set; }
        public long? LoadingAt { get; set; }
        public double? LoadMilliseconds { get; set; }
        public string? WaitingReason { get; set; } = "Checking local resources.";
    }

    public ServingQueue(IServingBackend providers, TimeProvider? time = null)
    {
        this.providers = providers;
        clock = time ?? TimeProvider.System;
        providers.Changed += CapacityChanged;
        providers.AdmissionChanged += CapacityChanged;
        if (providers.Resources is { } resources)
        {
            resources.Changed += CapacityChanged;
            resources.CapacityChanged += CapacityChanged;
        }
        pump = RunAsync();
    }

    public void Wake() => wake.Writer.TryWrite(true);

    private void CapacityChanged()
    {
        Interlocked.Increment(ref capacityRevision);
        Wake();
    }

    public Task<AttemptReply> SubmitAsync(Work work, CancellationToken cancellation)
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            var id = (work.Origin, work.Ticket.Attempt);
            if (entries.TryGetValue(id, out var existing))
            {
                var known = ConfirmSettlement(existing);
                return Task.FromResult(known.State is AttemptState.Waiting or AttemptState.Running or AttemptState.Completed
                    ? new AttemptReply(AttemptState.Uncertain, 409, "This attempt was already submitted.") : known);
            }
            if (entries.Values.Any(item => item.Work is { } prior && prior.Origin == work.Origin && prior.Ticket.Request == work.Ticket.Request))
                return Task.FromResult(new AttemptReply(AttemptState.Uncertain, 409, "The original request still has a serving owner."));
            if (now >= work.Ticket.QueueUntil)
                return Task.FromResult(Deadline());
            if (stop.IsCancellationRequested || cancellation.IsCancellationRequested)
                return Task.FromResult(Cancelled(work));
            if (entries.Count >= MaxAttempts || waiting >= MaxWaiting || waitingBytes + work.Payload.Bytes > MaxWaitingBytes)
                return Task.FromResult(Rejected("The serving queue reached its local capacity."));
            var entry = new Entry(work.Ticket.QueueUntil.AddMinutes(5))
            {
                Work = work,
                Stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stop.Token),
                Sequence = sequence++,
                QueuedAt = clock.GetTimestamp(),
                ActivityGeneration = activityGeneration
            };
            entries.Add(id, entry);
            waiting++;
            waitingBytes += work.Payload.Bytes;
            entry.Cancellation = entry.Stop.Token.Register(Wake);
            ActivityChanged?.Invoke();
            Wake();
            return entry.Complete.Task;
        }
    }

    public async Task<AttemptReply> StatusAsync(Guid origin, Guid attempt, DateTimeOffset? fenceUntil, CancellationToken cancellation)
    {
        Task<AttemptReply>? pending = null;
        AttemptReply state;
        lock (sync)
        {
            var now = clock.GetUtcNow();
            Prune(now);
            if (!entries.TryGetValue((origin, attempt), out var entry))
            {
                if (fenceUntil is null || fenceUntil > now.AddSeconds(Peers.QueuePolicy.MaxWaitSeconds + 20) || entries.Count >= MaxAttempts)
                    return new(AttemptState.Uncertain, 503, "The serving node cannot confirm this attempt stopped.");
                entry = new(fenceUntil.Value.AddMinutes(5)) { State = Rejected("The attempt was fenced before submission.") };
                entries.Add((origin, attempt), entry);
                return entry.State;
            }
            state = ConfirmSettlement(entry);
            if (fenceUntil is not null && entry.Stop is { } source)
            {
                _ = source.CancelAsync();
                pending = entry.Complete.Task;
            }
        }
        if (pending is not null)
            try { return await pending.WaitAsync(TimeSpan.FromSeconds(5), cancellation); }
            catch (TimeoutException) { }
        return state;
    }

    public (int Waiting, int Running) Counts
    {
        get { lock (sync) return (waiting, entries.Values.Count(entry => entry.State.State == AttemptState.Running)); }
    }

    public ServingActivity[] Activity()
    {
        lock (sync)
        {
            var cutoff = clock.GetUtcNow().AddMinutes(-5);
            return entries.Values.Where(entry => entry.Work is not null && entry.ActivityGeneration == activityGeneration)
                .OrderBy(entry => entry.Sequence).Take(48).Select(entry => Describe(entry, entry.Work!, null))
                .Concat(recentActivity.Reverse().Where(item => item.Finished >= cutoff).Take(32)).ToArray();
        }
    }

    public void ClearActivity()
    {
        lock (sync)
        {
            activityGeneration++;
            recentActivity.Clear();
        }
        ActivityChanged?.Invoke();
    }

    private ServingActivity Describe(Entry entry, Work work, DateTimeOffset? finished) =>
        new(work.Ticket.Request, work.Ticket.Attempt, work.Origin, work.Payload.Alias, work.Ticket.Deployment,
            work.Access.Priority, work.Ticket.Source ?? InferenceSource.Of(work.Context), work.Ticket.Arrived, finished,
            entry.State.State, entry.State.Reason ?? (entry.State.State == AttemptState.Waiting ? entry.WaitingReason : null),
            entry.QueueMilliseconds ?? clock.GetElapsedTime(entry.QueuedAt).TotalMilliseconds,
            entry.LoadMilliseconds ?? (entry.LoadingAt is { } loadingAt ? clock.GetElapsedTime(loadingAt).TotalMilliseconds : null),
            entry.LoadingAt is not null);

    private void WaitingReason(Entry entry, string? reason)
    {
        lock (sync)
        {
            if (entry.LoadingAt is not null || entry.WaitingReason == reason)
                return;
            entry.WaitingReason = reason;
            ActivityChanged?.Invoke();
        }
    }

    private void EndLoad(Entry entry)
    {
        if (entry.LoadingAt is not { } at)
            return;
        entry.LoadMilliseconds = (entry.LoadMilliseconds ?? 0) + clock.GetElapsedTime(at).TotalMilliseconds;
        entry.LoadingAt = null;
    }

    public void CancelOrigin(Guid origin)
    {
        lock (sync)
            foreach (var entry in entries.Values.Where(entry => entry.Work?.Origin == origin))
                if (entry.Stop is { } source)
                    _ = source.CancelAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                DateTimeOffset? deadline = await ScheduleAsync();
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                if (deadline is { } until)
                {
                    var remaining = until - clock.GetUtcNow();
                    wait.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                }
                try { await wake.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private async Task<DateTimeOffset?> ScheduleAsync()
    {
        if (loading is { Work.IsCompleted: true } prepared)
        {
            loading = null;
            lock (sync)
            {
                EndLoad(prepared.Entry);
                prepared.Entry.WaitingReason = "Checking capacity after model loading.";
                ActivityChanged?.Invoke();
            }
            if (await prepared.Work is { } error)
                Finish(prepared.Entry, error);
        }
        if (verifying is { Check.IsCompleted: true } verified)
        {
            verifying = null;
            verified.Entry.Rechecking = false;
            if (verified.Entry.State.State == AttemptState.Waiting && verified.Entry.Work is { } pendingWork)
            {
                pendingWork.Protection = await verified.Check;
                verified.Entry.Denied = pendingWork.Protection is null ? verified.Target : null;
            }
        }
        Entry[] pending;
        List<Entry> active;
        lock (sync)
        {
            pending = entries.Values.Where(entry => entry.State.State == AttemptState.Waiting).ToArray();
            active = entries.Values.Where(entry => entry.State.State == AttemptState.Running).ToList();
        }
        foreach (var entry in pending)
        {
            var work = entry.Work!;
            if (Expired(entry))
                Finish(entry, Expiration(entry));
            else if ((entry.Placement = providers.Placement(work.Payload.Shape, work.Ticket.Deployment)) is null)
                Finish(entry, Rejected("The local deployment no longer has eligible resources."));
        }
        var ready = pending.Where(entry => entry.State.State == AttemptState.Waiting).ToArray();
        foreach (var entry in ready.Where(entry => entry.Work!.Protection is not null))
        {
            var work = entry.Work!;
            var proof = work.Protection!;
            var blockers = Higher(work, entry.Placement!, active.Concat(ready)).ToArray();
            var digest = work.Digest();
            entry.Rechecking = proof.Digest != digest || blockers.Any(blocker => !proof.Flexible.ContainsKey(blocker.Ticket.Attempt));
            if (entry.Rechecking)
                StartScarcityCheck(entry, blockers, digest, proof);
        }
        var waitingEntries = ready.OrderByDescending(entry => entry.Work!.Protection is not null).ThenByDescending(entry => entry.Work!.Access.Priority)
            .ThenBy(entry => entry.Work!.Ticket.Arrived).ThenBy(entry => entry.Sequence).ToArray();
        var blocked = new List<Entry>();
        foreach (var entry in waitingEntries)
        {
            var work = entry.Work!;
            var placement = entry.Placement!;
            if (Expired(entry))
            {
                Finish(entry, Expiration(entry));
                continue;
            }
            if (entry.Rechecking)
            {
                WaitingReason(entry, "Rechecking whether alternate hosts can take competing work.");
                blocked.Add(entry);
                continue;
            }
            var ahead = blocked.Where(other => providers.Conflicts(placement, other.Placement!)).ToArray();
            var reason = "Waiting for work that reserved the same resources.";
            if (ahead.Length == 0)
            {
                var start = providers.TryStart(work.Payload, work.Ticket.Deployment);
                if (start.Execute is { } execute)
                {
                    Start(entry, execute, start.Epoch);
                    active.Add(entry);
                    continue;
                }
                if (start.Status == AdmissionStatus.Unavailable)
                {
                    Finish(entry, Rejected(start.Reason ?? "The local deployment is unavailable."));
                    continue;
                }
                reason = start.Reason ?? "Waiting for local capacity.";
            }

            var higher = Higher(work, placement, active.Concat(ahead)).ToArray();
            if (higher.Length != 0 && work.Protection is null)
            {
                var digest = work.Digest();
                if (!active.Any(other => other.Work is { } running && higher.Contains(running)) ||
                    entry.Denied is { } denied && denied.Digest == digest && higher.All(blocker => denied.Blockers.Contains(blocker.Ticket.Attempt)))
                {
                    Finish(entry, Expired(entry) ? Expiration(entry) : Rejected("Higher-priority work needs the same local resources.") with
                    { SuggestedNode = work.Suggest?.Invoke(work.Probe.Shape) });
                    continue;
                }
                StartScarcityCheck(entry, higher, digest);
                WaitingReason(entry, "Checking alternate hosts for higher-priority work.");
                blocked.Add(entry);
                continue;
            }
            blocked.Add(entry);
            WaitingReason(entry, work.Protection is null ? reason : "Reserved for the next safe start on this host.");
            var revision = Volatile.Read(ref capacityRevision);
            if (ahead.Length == 0 && placement.Model.Instance is null && loading is null && entry.LastLoadRevision != revision &&
                !active.Any(other => other.Work is not null && providers.Conflicts(placement, other.Placement!)))
            {
                entry.LastLoadRevision = revision;
                lock (sync)
                {
                    entry.LoadingAt = clock.GetTimestamp();
                    entry.WaitingReason = "Preparing or loading the model.";
                    ActivityChanged?.Invoke();
                }
                var preparation = PrepareAsync(entry.Placement!, work, entry.Stop!.Token);
                loading = (entry, preparation);
                _ = preparation.ContinueWith(_ => Wake(), TaskScheduler.Default);
            }
        }
        return blocked.Where(entry => entry.Work is not null).Min(entry => (DateTimeOffset?)entry.Work!.Ticket.QueueUntil);
    }

    private IEnumerable<Work> Higher(Work work, ServingPlacement placement, IEnumerable<Entry> contenders) => contenders
        .Where(other => other.Work is { } blocker && blocker.Access.Priority > work.Access.Priority &&
            providers.Conflicts(placement, other.Placement!)).Select(other => other.Work).OfType<Work>();

    private void StartScarcityCheck(Entry entry, Work[] blockers, string digest, ScarcityProof? previous = null)
    {
        if (verifying is not null)
            return;
        var check = FindScarcityAsync(entry, blockers, digest, previous);
        verifying = (entry, check, new(digest, blockers.Select(blocker => blocker.Ticket.Attempt).ToArray()));
        _ = check.ContinueWith(_ => Wake(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<ScarcityProof?> FindScarcityAsync(Entry entry, Work[] blockers, string digest, ScarcityProof? previous = null)
    {
        var work = entry.Work!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(entry.Stop!.Token);
        var remaining = work.Ticket.QueueUntil - clock.GetUtcNow();
        deadline.CancelAfter(remaining > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) :
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        try
        {
            if (await work.Alternate(work.Probe, deadline.Token) is not null)
                return null;
            var flexible = previous?.Flexible.ToDictionary() ?? [];
            var probes = blockers.ToDictionary(blocker => blocker.Ticket.Attempt, blocker => blocker.Probe);
            if (previous is not null && previous.Digest != digest)
                foreach (var (id, probe) in previous.Flexible)
                    probes.TryAdd(id, probe);
            if (probes.Count > 32)
                return null;
            var advice = new List<(Work Work, Guid Node)>();
            foreach (var (id, probe) in probes)
            {
                if (previous?.Digest == digest && flexible.ContainsKey(id))
                    continue;
                if (await work.Alternate(probe, deadline.Token) is not { } node || node == Guid.Empty)
                    return null;
                flexible[id] = probe;
                if (blockers.FirstOrDefault(blocker => blocker.Ticket.Attempt == id) is { } blocker)
                    advice.Add((blocker, node));
            }
            foreach (var (blocker, node) in advice)
                blocker.Advise(blocker, node);
            return new(digest, flexible);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return null; }
    }

    private async Task<AttemptReply?> PrepareAsync(ServingPlacement placement, Work work, CancellationToken cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            var remaining = work.Ticket.QueueUntil - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                return null;
            timeout.CancelAfter(remaining);
            await providers.PreparePlacementAsync(placement, id => work.Displaced(id, timeout.Token), timeout.Token);
        }
        catch (OperationCanceledException) { }
        catch (ProviderFailure error) when (error.Status == 409 || error.CapacityWait) { return null; }
        catch (Exception error) when (error is ProviderFailure or IOException or HttpRequestException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception)
        {
            return Rejected("Mango could not prepare the local deployment.");
        }
        return null;
    }

    private void Start(Entry entry, Func<HttpContext, CancellationToken, Task<ExecutionResult>> execute, string? epoch)
    {
        Work work;
        CancellationToken token;
        lock (sync)
        {
            work = entry.Work!;
            token = entry.Stop!.Token;
            RemoveWaiting(work);
            entry.State = new(AttemptState.Running);
            entry.QueueMilliseconds = clock.GetElapsedTime(entry.QueuedAt).TotalMilliseconds;
            ActivityChanged?.Invoke();
        }
        _ = ExecuteAsync(entry, work, execute, epoch, token);
    }

    private async Task ExecuteAsync(Entry entry, Work work, Func<HttpContext, CancellationToken, Task<ExecutionResult>> execute,
        string? epoch, CancellationToken token)
    {
        work.Context.Items[typeof(ServingQueue)] = epoch;
        AttemptReply result;
        var awaitingSettlement = false;
        try
        {
            var execution = await execute(work.Context, token);
            awaitingSettlement = !execution.Settled;
            result = execution switch
            {
                { Settled: false } => new(AttemptState.Uncertain, 502, "The provider did not confirm that inference stopped."),
                { Failure: null } => new(AttemptState.Completed, 200, Epoch: epoch, Timing: execution.Timing),
                _ when token.IsCancellationRequested => Cancelled(work) with { State = AttemptState.Stopped },
                _ => new(AttemptState.Failed, execution.Failure.Status, execution.Failure.Message)
            };
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or OperationCanceledException or HttpRequestException
            or ProviderFailure or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            result = new(AttemptState.Uncertain, 502, "The serving node could not verify that inference stopped.");
        }
        Finish(entry, result, awaitingSettlement);
        Wake();
    }

    private bool Expired(Entry entry) => entry.Stop!.IsCancellationRequested || clock.GetUtcNow() >= entry.Work!.Ticket.QueueUntil;

    private AttemptReply Expiration(Entry entry) => clock.GetUtcNow() >= entry.Work!.Ticket.QueueUntil && !entry.Work.Access.Cancelled.IsCancellationRequested
        ? Deadline() : Cancelled(entry.Work);

    private static AttemptReply Cancelled(Work work) => work.Access.Cancelled.IsCancellationRequested
        ? new(AttemptState.Rejected, 401, "The inference credential was revoked or expired.") : Rejected("Inference was cancelled.");

    private static AttemptReply Deadline() => new(AttemptState.Rejected, 504, "The request expired while waiting for inference.");

    private static AttemptReply Rejected(string reason) => new(AttemptState.Rejected, 503, reason);

    private void Finish(Entry entry, AttemptReply result, bool awaitingSettlement = false)
    {
        lock (sync)
        {
            if (entry.Work is not { } work)
                return;
            if (entry.State.State == AttemptState.Waiting)
                RemoveWaiting(work);
            EndLoad(entry);
            entry.QueueMilliseconds ??= clock.GetElapsedTime(entry.QueuedAt).TotalMilliseconds;
            entry.State = result = result with { QueueMilliseconds = entry.QueueMilliseconds, LoadMilliseconds = entry.LoadMilliseconds };
            if (entry.ActivityGeneration == activityGeneration)
            {
                if (recentActivity.Count == 48)
                    recentActivity.Dequeue();
                recentActivity.Enqueue(Describe(entry, work, clock.GetUtcNow()));
            }
            entry.AwaitingSettlement = awaitingSettlement;
            entry.Work = null;
            entry.RetainUntil = entry.RetainUntil > clock.GetUtcNow().AddMinutes(5)
                ? entry.RetainUntil : clock.GetUtcNow().AddMinutes(5);
            entry.Cancellation.Unregister();
            var source = entry.Stop!;
            entry.Stop = null;
            if (loading is { } loadingWork && ReferenceEquals(loadingWork.Entry, entry))
            {
                _ = source.CancelAsync();
                _ = loadingWork.Work.ContinueWith(_ => source.Dispose(), TaskScheduler.Default);
            }
            else
                source.Dispose();
            entry.Complete.TrySetResult(result);
            ActivityChanged?.Invoke();
        }
    }

    private void RemoveWaiting(Work work)
    {
        waiting--;
        waitingBytes -= work.Payload.Bytes;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in entries.Where(pair => pair.Value.Work is null && pair.Value.RetainUntil <= now &&
                     ConfirmSettlement(pair.Value).State != AttemptState.Uncertain).ToArray())
            entries.Remove(pair.Key);
    }

    private AttemptReply ConfirmSettlement(Entry entry)
    {
        if (entry.AwaitingSettlement && entry.Placement is { } placement && !providers.HasUnsettled(placement.Model.Id))
        {
            entry.AwaitingSettlement = false;
            entry.State = new(AttemptState.Stopped, 503, "The provider confirmed that the earlier inference stopped.");
        }
        return entry.State;
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await pump;
        Entry[] remaining;
        lock (sync)
        {
            remaining = entries.Values.Where(entry => entry.Work is not null).ToArray();
            foreach (var entry in remaining.Where(entry => entry.State.State == AttemptState.Waiting))
                Finish(entry, Rejected("The serving node stopped."));
        }
        await Task.WhenAll(remaining.Select(entry => entry.Complete.Task));
        if (loading is { } preparing)
            await preparing.Work;
        if (verifying is { } checking)
            await checking.Check;
        providers.Changed -= CapacityChanged;
        providers.AdmissionChanged -= CapacityChanged;
        if (providers.Resources is { } resources)
        {
            resources.Changed -= CapacityChanged;
            resources.CapacityChanged -= CapacityChanged;
        }
        stop.Dispose();
    }
}
