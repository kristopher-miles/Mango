using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Mango.Peers;
using Mango.Providers;
using Mango.Resources;
using Mango.Scheduling;
using Microsoft.AspNetCore.Http;

internal static class SchedulingChecks
{
    private static readonly Guid Key = Guid.NewGuid();
    private static readonly Guid Origin = Guid.NewGuid();
    private static readonly Guid Alternate = Guid.NewGuid();
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync()
    {
        await IndependentAndFifoAsync();
        await PriorityAsync();
        await ScarcityAsync();
        await ChangedScarcityAsync(changeCatalog: true);
        await ChangedScarcityAsync(changeCatalog: false);
        await CancellationAsync();
        Console.WriteLine("Scheduling, queue ownership and cancellation checks passed.");
    }

    private static async Task IndependentAndFifoAsync()
    {
        var one = new Backend();
        var two = new Backend();
        await using var first = new ServingQueue(one);
        await using var second = new ServingQueue(two);
        var r1 = await Submit(first, "one", "small");
        var r2 = await Submit(second, "two", "small");
        var separate = await Submit(first, "separate", "independent");
        await Task.WhenAll(one.Started("one"), two.Started("two"), one.Started("separate"));
        Check(first.Counts == (0, 2) && second.Counts == (0, 1), "same key on separate hosts and resource sets");

        var age = DateTimeOffset.UtcNow;
        var recent = await Work("recent", "small", arrived: age);
        var rerouted = await Work("rerouted", "small", arrived: age.AddSeconds(-1));
        var recentTask = first.SubmitAsync(recent, default);
        var reroutedTask = first.SubmitAsync(rerouted, default);
        Check(first.Counts.Waiting == 2, "both racing admissions wait behind one occupied slot");
        one.Release("one");
        await one.Started("rerouted");
        Check(!one.DidStart("recent"), "FIFO retains the original ingress age across attempts");
        one.Release("rerouted");
        await one.Started("recent");
        one.Release("recent");
        one.Release("separate");
        two.Release("two");
        Check((await Task.WhenAll(r1, r2, separate, recentTask, reroutedTask).WaitAsync(Timeout))
            .All(item => item.State == AttemptState.Completed), "independent and FIFO work completes once");
    }

    private static async Task PriorityAsync()
    {
        var backend = new Backend();
        await using var queue = new ServingQueue(backend);
        var active = await Submit(queue, "active", "small", KeyPriority.Low);
        await backend.Started("active");
        var displaced = await Submit(queue, "displaced", "small", KeyPriority.Low);
        var independent = await Submit(queue, "independent", "independent", KeyPriority.Low);
        await backend.Started("independent");
        var higher = await Submit(queue, "higher", "small", KeyPriority.High);
        var rejected = await displaced.WaitAsync(Timeout);
        Check(rejected.State == AttemptState.Rejected && rejected.Status == 503 && !active.IsCompleted && !independent.IsCompleted,
            "higher work purges only conflicting waiting work");
        backend.Release("active");
        await backend.Started("higher");
        var lower = await Submit(queue, "lower", "small", KeyPriority.Low);
        Check((await lower.WaitAsync(Timeout)).State == AttemptState.Rejected && !backend.DidStart("lower"),
            "ordinary lower-priority request cannot wait behind active higher work");
        backend.Release("independent");
        backend.Release("higher");
        await Task.WhenAll(active, independent, higher).WaitAsync(Timeout);
    }

    private static async Task ScarcityAsync()
    {
        var backend = new Backend();
        await using var queue = new ServingQueue(backend);
        var advised = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirm = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var high = await Submit(queue, "high", "small", KeyPriority.High, advise: (_, node) => advised.TrySetResult(node));
        await backend.Started("high");
        var low = await Submit(queue, "scarce", "large", KeyPriority.Low, alternate: async (probe, token) =>
        {
            checking.TrySetResult();
            await confirm.Task.WaitAsync(token);
            return await NormalAlternates(probe, token);
        });
        await checking.Task.WaitAsync(Timeout);
        var independent = await Submit(queue, "independent", "independent", KeyPriority.High);
        await backend.Started("independent").WaitAsync(TimeSpan.FromSeconds(1));
        confirm.TrySetResult();
        Check(await advised.Task.WaitAsync(Timeout) == Alternate && !high.IsCompleted && !low.IsCompleted,
            "scarce placement reserves next start and advises flexible ingress without preempting");
        var backfill = await Submit(queue, "backfill", "small", KeyPriority.High);
        backend.Release("high");
        await backend.Started("scarce");
        Check(!backend.DidStart("backfill"), "conflicting high work cannot backfill ahead of the reservation");
        backend.Release("scarce");
        await backend.Started("backfill");
        backend.Release("backfill");
        backend.Release("independent");
        await Task.WhenAll(high, low, backfill, independent).WaitAsync(Timeout);
    }

    private static async Task ChangedScarcityAsync(bool changeCatalog)
    {
        var backend = new Backend();
        await using var queue = new ServingQueue(backend);
        var advised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revision = "before";
        var high = await Submit(queue, "high", "small", KeyPriority.High, advise: (_, _) => advised.TrySetResult());
        await backend.Started("high");
        var low = await Submit(queue, "scarce", "large", KeyPriority.Low,
            alternate: (probe, token) => revision == "before" ? NormalAlternates(probe, token) : Task.FromResult<Guid?>(null),
            digest: () => revision);
        await advised.Task.WaitAsync(Timeout);
        Task<AttemptReply>? newcomer = null;
        if (changeCatalog)
        {
            revision = "after";
            queue.Wake();
        }
        else
            newcomer = await Submit(queue, "pinned", "pinned", KeyPriority.High);
        Check((await low.WaitAsync(Timeout)).State == AttemptState.Rejected && !backend.DidStart("scarce"),
            changeCatalog ? "scarcity is lost when the cached alternate is no longer eligible" :
                "scarcity is lost when another conflicting higher-priority job has no alternate");
        backend.Release("high");
        if (newcomer is not null)
        {
            await backend.Started("pinned");
            backend.Release("pinned");
            await newcomer.WaitAsync(Timeout);
        }
        await high.WaitAsync(Timeout);
    }

    private static async Task CancellationAsync()
    {
        var backend = new Backend();
        await using var queue = new ServingQueue(backend);
        var work = await Work("running", "small");
        using var disconnect = new CancellationTokenSource();
        var active = queue.SubmitAsync(work, disconnect.Token);
        await backend.Started("running");
        var duplicate = await queue.SubmitAsync(work, default).WaitAsync(Timeout);
        Check(duplicate.State == AttemptState.Uncertain && !duplicate.Retryable, "duplicate submission cannot run or retry active work");
        var timeout = await Work("deadline", "small", expires: DateTimeOffset.UtcNow.AddMilliseconds(100));
        var expired = await queue.SubmitAsync(timeout, default).WaitAsync(Timeout);
        Check(expired.Status == 504 && !backend.DidStart("deadline"), "queue timer expires without polling or starting work");

        var pending = await Work("queued", "small");
        using var queuedDisconnect = new CancellationTokenSource();
        var waiting = queue.SubmitAsync(pending, queuedDisconnect.Token);
        await queuedDisconnect.CancelAsync();
        Check((await waiting.WaitAsync(Timeout)).State == AttemptState.Rejected && !backend.DidStart("queued"),
            "waiting client disconnect cancels the queued payload");
        await disconnect.CancelAsync();
        Check((await active.WaitAsync(Timeout)).State == AttemptState.Stopped && backend.Releases("running") == 1,
            "active disconnect releases exactly one proven reservation");
        var delayed = await Work("fenced", "small");
        Check((await queue.StatusAsync(Origin, delayed.Ticket.Attempt, null, default)).State == AttemptState.Uncertain,
            "missing state without a fence is not proof");
        var fence = await queue.StatusAsync(Origin, delayed.Ticket.Attempt, delayed.Ticket.QueueUntil, default);
        var replay = await queue.SubmitAsync(delayed, default).WaitAsync(Timeout);
        Check(fence.Retryable && replay == fence && !backend.DidStart("fenced"), "cancel fences a submission that arrives later");
    }

    private static Task<Guid?> NormalAlternates(AlternateProbe probe, CancellationToken _) =>
        Task.FromResult<Guid?>(probe.Shape.Alias == "small" ? Alternate : null);

    private static async Task<Task<AttemptReply>> Submit(ServingQueue queue, string name, string alias,
        KeyPriority priority = KeyPriority.Normal, Func<AlternateProbe, CancellationToken, Task<Guid?>>? alternate = null,
        Action<ServingQueue.Work, Guid>? advise = null, Func<string>? digest = null) =>
        queue.SubmitAsync(await Work(name, alias, priority, alternate, advise, digest), default);

    private static async Task<ServingQueue.Work> Work(string name, string alias, KeyPriority priority = KeyPriority.Normal,
        Func<AlternateProbe, CancellationToken, Task<Guid?>>? alternate = null, Action<ServingQueue.Work, Guid>? advise = null,
        Func<string>? digest = null, DateTimeOffset? arrived = null, DateTimeOffset? expires = null)
    {
        var context = new DefaultHttpContext { TraceIdentifier = name };
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            model = alias, messages = new[] { new { role = "user", content = "hello" } }, max_tokens = 8
        })));
        var payload = await InferenceRequest.ReadAsync(InferenceOperation.Chat, context.Request, default);
        var now = DateTimeOffset.UtcNow;
        var ticket = new AttemptTicket(Guid.NewGuid(), Guid.NewGuid(), Key, alias, arrived ?? now, expires ?? now.AddSeconds(20), null, "test");
        return new(Origin, ticket, payload, new(Key, "Fixture", [InferenceScope.Chat], priority, default), context,
            alternate ?? ((_, _) => Task.FromResult<Guid?>(null)), advise ?? ((_, _) => { }), (_, _) => Task.CompletedTask,
            digest ?? (() => "stable"));
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"Scheduling check failed: {name}");
    }

    private sealed class Backend : IServingBackend
    {
        private readonly Guid shared = Guid.NewGuid();
        private readonly Guid separate = Guid.NewGuid();
        private readonly object sync = new();
        private readonly HashSet<Guid> reserved = [];
        private readonly ConcurrentDictionary<string, TaskCompletionSource> started = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> releases = new();
        private readonly ConcurrentDictionary<string, int> releaseCount = new();
        public ResourceProtection? Resources => null;
        public event Action? Changed { add { } remove { } }
        public event Action? AdmissionChanged;

        public ServingPlacement Placement(InferenceShape shape, string id, bool allowUnverifiedContext = true)
        {
            var group = id == "independent" ? separate : shared;
            var model = new ProviderModel(id, id, id, id, null, ["text"], ["text"], [InferenceOperation.Chat],
                [new(id, 2048, 1, id)], null, false, false, null, []);
            var endpoint = new EndpointStatus(group, ProviderProfile.LlamaSingle, ProviderMode.Managed, "fixture", null, null,
                null, true, null, null, null, false, false, false, null);
            return new(new(group, model, new(id, [], 2048, null, null, null, 0), true, false, "fixture"),
                endpoint, null, shape.Demand(2048));
        }

        public bool Conflicts(ServingPlacement a, ServingPlacement b) => a.Endpoint.Id == b.Endpoint.Id;
        public bool HasUnsettled(string deployment) => false;

        public StartExecution TryStart(InferenceRequest request, string id)
        {
            var group = Placement(request.Shape, id).Endpoint.Id;
            lock (sync)
                if (!reserved.Add(group))
                    return new(AdmissionStatus.Wait, "Fixture slot busy.");
            return new(AdmissionStatus.Admitted, null, async (context, token) =>
            {
                var name = context.TraceIdentifier;
                Gate(started, name).TrySetResult();
                try
                {
                    await Gate(releases, name).Task.WaitAsync(token);
                    return new(true, null, null);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return new(true, new(503, "Fixture cancelled."), null);
                }
                finally
                {
                    lock (sync)
                        reserved.Remove(group);
                    releaseCount.AddOrUpdate(name, 1, (_, count) => count + 1);
                    AdmissionChanged?.Invoke();
                }
            });
        }

        public Task PreparePlacementAsync(ServingPlacement placement, Func<string, Task> displaced, CancellationToken cancellation) =>
            throw new InvalidOperationException("The fake provider is already loaded.");

        public Task Started(string name) => Gate(started, name).Task.WaitAsync(Timeout);
        public bool DidStart(string name) => Gate(started, name).Task.IsCompleted;
        public void Release(string name) => Gate(releases, name).TrySetResult();
        public int Releases(string name) => releaseCount.GetValueOrDefault(name);
        private static TaskCompletionSource Gate(ConcurrentDictionary<string, TaskCompletionSource> gates, string name) =>
            gates.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
