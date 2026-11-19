using Mango.Host;
using Mango.Logging;
using Mango.Providers;
using Mango.Resources;
using Mango.Scheduling;

namespace Mango.Peers;

internal sealed partial class PeerNetwork
{
    private const int MaxIngress = 512;
    private const int MaxDestinations = 12;
    private const long MaxIngressBytes = 384L * 1024 * 1024;
    private readonly ServingQueue serving = new(providers, time);
    private readonly string schedulingEpoch = Guid.NewGuid().ToString("N");
    private readonly RouteMemory affinity = new(time);
    private readonly object routing = new();
    private readonly Dictionary<Guid, IngressRequest> ingress = [];
    private readonly Dictionary<Guid, (Guid Node, Guid Suggested)> advice = [];
    private readonly Dictionary<(Guid, string), HostUse> hostUse = [];
    private readonly Queue<RecentService> recentServices = new();
    private readonly Queue<IngressActivity> recentIngress = new();
    private ITimer? recentRoutingExpiry;
    private int schedulingGeneration;
    private readonly object discoveryLock = new();
    private Task? discovery;
    private DateTimeOffset discoverAfter;
    private long ingressBytes;
    private int discoveryRuns;
    private int submissions;
    private int stopQueries;
    private int placementChecks;
    private int digestMerges;

    private sealed class HostUse
    {
        public int Active { get; set; }
        public double? RuntimeMs { get; set; }
        public DateTimeOffset AvoidUntil { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public int Failures { get; set; }
    }

    private sealed record RecentService(Guid Origin, AttemptTicket Ticket, InferenceShape Shape, DateTimeOffset At);
    private sealed record IngressActivity(Guid Id, string Alias, InferenceOperation Operation, InferenceSource Source,
        string? KeyName, KeyPriority Priority, DateTimeOffset Arrived, DateTimeOffset Finished, AttemptTrace[] Attempts,
        bool Output, string? Reason);

    public int QueueWaitSeconds => Snapshot.Records.Queue?.WaitSeconds ?? QueuePolicy.DefaultWaitSeconds;

    private void ClearSchedulingMemory()
    {
        serving.ClearActivity();
        lock (routing)
        {
            schedulingGeneration++;
            affinity.Clear();
            advice.Clear();
            recentServices.Clear();
            recentIngress.Clear();
            ArmRoutingExpiry();
            foreach (var key in hostUse.Where(item => item.Value.Active == 0).Select(item => item.Key).ToArray())
                hostUse.Remove(key);
        }
        SignalRequests();
    }

    private void ArmRoutingExpiry()
    {
        if (recentRoutingExpiry is null)
            return;
        var lastIngress = recentIngress.TryPeek(out var ingressItem) ? ingressItem.Finished : DateTimeOffset.MaxValue;
        var lastService = recentServices.TryPeek(out var serviceItem) ? serviceItem.At : DateTimeOffset.MaxValue;
        var first = lastIngress < lastService ? lastIngress : lastService;
        var delay = first == DateTimeOffset.MaxValue ? Timeout.InfiniteTimeSpan : first.AddMinutes(5) - clock.GetUtcNow();
        recentRoutingExpiry.Change(delay >= TimeSpan.Zero || delay == Timeout.InfiniteTimeSpan ? delay : TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
    }

    private void ExpireRoutingMemory()
    {
        bool changed;
        lock (routing)
        {
            var cutoff = clock.GetUtcNow().AddMinutes(-5);
            changed = recentIngress.TryPeek(out var item) && item.Finished <= cutoff;
            while (recentIngress.TryPeek(out var ingressItem) && ingressItem.Finished <= cutoff)
                recentIngress.Dequeue();
            while (recentServices.TryPeek(out var serviceItem) && serviceItem.At <= cutoff)
                recentServices.Dequeue();
            ArmRoutingExpiry();
        }
        if (changed)
            SignalRequests();
    }

    public async Task SetQueuePolicyAsync(HttpRequest request, int seconds, CancellationToken cancellation, int? expectedWaitSeconds = null)
    {
        if (seconds is <= 0 or > QueuePolicy.MaxWaitSeconds)
            throw new PeerFailure(400, $"Enter a queue wait from 1 to {QueuePolicy.MaxWaitSeconds} seconds.");
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireMember(current);
            RequireAdmin(current, AdminHeader(request));
            if (expectedWaitSeconds is { } expected && expected != (current.Records.Queue?.WaitSeconds ?? QueuePolicy.DefaultWaitSeconds))
                throw new PeerFailure(409, "The queue wait changed elsewhere. Review its current value.", "conflict");
            return current.Records.Queue?.WaitSeconds == seconds ? current.Disk : current.Disk with
            {
                Records = current.Records with { Queue = new(Next(current, self.Id), seconds) }
            };
        }, cancellation);
        await PushChangesAsync(before, after, cancellation);
    }

    public object SchedulingView()
    {
        var counts = serving.Counts;
        var servingActivity = serving.Activity();
        var cutoff = clock.GetUtcNow().AddMinutes(-5);
        lock (routing)
            return new
            {
                QueueWaitSeconds, counts.Waiting, counts.Running, DiscoveryRuns = Volatile.Read(ref discoveryRuns),
                Submissions = Volatile.Read(ref submissions), StopQueries = Volatile.Read(ref stopQueries),
                PlacementChecks = Volatile.Read(ref placementChecks), DigestMerges = Volatile.Read(ref digestMerges),
                Serving = servingActivity, LocalResources = providers.Resources?.Status,
                Ingress = ingress.Values.Take(48).Select(item => new { item.Id, item.Alias, item.Operation, item.Source,
                    item.Access.KeyId, item.Access.KeyName, item.Access.Priority, item.Arrived, item.QueueUntil, item.OutputStarted,
                    Attempts = item.Attempts.ToArray() }).ToArray(),
                Recent = recentIngress.Where(item => item.Finished >= cutoff).TakeLast(48).ToArray()
            };
    }

    public object ModelList(InferenceAccess? access)
    {
        var inventory = Inventory().ToArray();
        var native = inventory.SelectMany(item => item.Model.ProviderIdentifiers ?? []).ToHashSet(StringComparer.Ordinal);
        var names = inventory.Where(item => item.Model.LocallyAvailable == true &&
                item.Model.Operations.Any(operation => access?.Allows(operation) != false))
            .SelectMany(item => RoutingNames(item.Model, native)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return new { @object = "list", data = names.Select(alias => DescribeModel(alias,
            inventory.Where(item => item.Model.LocallyAvailable == true &&
                item.Model.Operations.Any(operation => access?.Allows(operation) != false) &&
                RoutingNames(item.Model, native).Contains(alias)).Select(item => item.Model).ToArray(), access)).ToArray() };
    }

    internal static object DescribeModel(string alias, ObservedDeployment[] models, InferenceAccess? access = null)
    {
        var row = new Dictionary<string, object?>
        {
        ["id"] = alias, ["object"] = "model", ["owned_by"] = "mango",
        ["input_modalities"] = models.SelectMany(model => model.Inputs).Distinct().Order().ToArray(),
        ["output_modalities"] = models.SelectMany(model => model.Outputs).Distinct().Order().ToArray(),
        ["capabilities"] = models.SelectMany(model => model.Operations).Where(operation => access?.Allows(operation) != false)
            .Select(operation => operation.ToString().ToLowerInvariant())
            .Concat(models.Any(model => model.Inputs.Contains("image")) ? new[] { "vision" } : [])
            .Concat(models.Any(model => model.ClientTools) ? new[] { "tools" } : []).Distinct().Order().ToArray()
        };
        var pooled = models.Count(model => string.Equals(model.EffectiveAlias, alias, StringComparison.OrdinalIgnoreCase)) > 1;
        var context = pooled ? models.Where(model => model.Loaded || model.Workloads?.Any(work => work.Qualified) == true)
            .Select(model => model.Context).DefaultIfEmpty().Max() :
            models.Any(model => model.Loaded && model.Context is null) ? null :
                models.Where(model => model.Loaded).Select(model => model.Context).DefaultIfEmpty().Min();
        if (context is not null) row["context_size"] = context;
        return row;
    }

    public async Task RouteAsync(InferenceOperation operation, HttpContext context, CancellationToken cancellation)
    {
        var arrived = clock.GetUtcNow();
        var age = clock.GetTimestamp();
        var log = LogCapture.Of(context);
        var logGeneration = log is null ? Logs.Configuration.Epoch.Generation : log.Generation;
        var limit = TimeSpan.FromSeconds(QueueWaitSeconds);
        if (providers.Resources is { } resources)
            await resources.ClientArrivedAsync(cancellation);
        using var payload = await providers.ReadInferenceAsync(operation, context, cancellation);
        log?.AcceptedInput();
        if (log?.Entry is { } captured)
            log.Entry = captured with { Alias = payload.Alias };
        if (string.Equals(payload.Alias, "best", StringComparison.OrdinalIgnoreCase) &&
            !Inventory().Any(item => HasProviderIdentifier(item.Model, payload.Alias)))
            throw new ProviderFailure(501, "Automatic best-model selection is not available.");
        var access = InferenceAccess.Of(context) ?? throw new ProviderFailure(401, "The inference credential is required.");
        var source = InferenceSource.Of(context);
        var session = context.Request.Headers["Mango-Session"].ToString();
        if (session.Length > 256)
            throw new ProviderFailure(400, "Mango-Session may contain up to 256 characters.");
        session = session.Length == 0 ? null : ProviderIdentity.Hash(session);
        var prefixes = session is null ? payload.Prefixes() : [];
        var identity = access.KeyId ?? Guid.Empty;
        var item = new IngressRequest(log?.Id ?? Guid.NewGuid(), arrived, arrived + limit, access, source, payload.Alias, operation);
        int generation;
        lock (routing)
        {
            if (ingress.Count >= MaxIngress || ingressBytes + payload.Bytes > MaxIngressBytes)
                throw new ProviderFailure(429, "Mango's ingress input limit is busy.");
            ingress.Add(item.Id, item);
            ingressBytes += payload.Bytes;
            generation = schedulingGeneration;
        }
        SignalRequests();
        context.Response.OnStarting(() =>
        {
            lock (routing)
            {
                item.OutputStarted = true;
                if (ingress.ContainsKey(item.Id) && item.Attempts.Count > 0)
                {
                    var index = item.Attempts.Count - 1;
                    item.Attempts[index] = item.Attempts[index] with { State = AttemptState.Running };
                }
            }
            SignalRequests();
            return Task.CompletedTask;
        });
        try
        {
            var hint = affinity.Find(identity, payload.Shape, session, prefixes, arrived);
            var tried = new HashSet<Guid>();
            var unreachable = new Dictionary<Guid, (string? Address, string Epoch)>();
            var attemptCount = 0;
            var choices = Candidates(payload.Shape);
            var searched = false;
            var onlyUnreachable = true;
            if (choices.Length == 0 && Ready && IsMember)
            {
                await DiscoverMissingAsync(cancellation);
                searched = true;
                choices = Candidates(payload.Shape);
            }
            choices = await ObserveRouteChoicesAsync(choices, payload.Shape, access.KeyId, cancellation);
            AttemptReply? last = null;
            Guid? suggested = null;
            while (attemptCount < MaxDestinations && limit > clock.GetElapsedTime(age))
            {
                cancellation.ThrowIfCancellationRequested();
                var attempt = Guid.NewGuid();
                var choice = Choose(choices, tried, hint, suggested, item, attempt, limit - clock.GetElapsedTime(age));
                if (choice is null)
                {
                    if (searched || !onlyUnreachable || tried.Count == 0 || !Ready || !IsMember)
                        break;
                    searched = true;
                    await DiscoverMissingAsync(cancellation);
                    choices = await ObserveRouteChoicesAsync(Candidates(payload.Shape), payload.Shape, access.KeyId, cancellation);
                    foreach (var candidate in choices)
                        if (unreachable.TryGetValue(candidate.Node, out var failed) &&
                            (failed.Address != Snapshot.Nodes.GetValueOrDefault(candidate.Node)?.Address || failed.Epoch != candidate.ServingEpoch))
                            tried.Remove(candidate.Node);
                    continue;
                }
                tried.Add(choice.Node);
                SignalRequests();
                attemptCount++;
                var elapsed = clock.GetElapsedTime(age);
                var remaining = limit - elapsed;
                var here = Snapshot.Self?.Id ?? Guid.Empty;
                var address = Snapshot.Nodes.GetValueOrDefault(choice.Node)?.Address;
                var tryOtherHost = choice is { VerifiedFit: true, Busy: false, Displaces: false } &&
                    choices.Any(other => !tried.Contains(other.Node) && other is { VerifiedFit: true, Busy: false, Displaces: false });
                var ticket = new AttemptTicket(item.Id, attempt, access.KeyId, choice.Deployment,
                    clock.GetUtcNow() - elapsed, clock.GetUtcNow() + remaining, session, choice.ServingEpoch, source, logGeneration, tryOtherHost);
                var started = clock.GetTimestamp();
                var result = new AttemptReply(AttemptState.Uncertain, 503, "The attempt ended without a confirmed result.");
                try
                {
                    if (choice.Node == here)
                        result = await SubmitLocalAsync(here, ticket, payload, access, context, cancellation);
                    else
                        result = await SubmitPeerAsync(choice.Node, ticket, elapsed, remaining, payload, context, cancellation);
                }
                finally
                {
                    lock (routing)
                    {
                        item.OutputStarted |= context.Response.HasStarted;
                        var index = item.Attempts.Count - 1;
                        item.Attempts[index] = item.Attempts[index] with { State = result.State, Status = result.Status, Timing = result.Timing,
                            Reason = result.Reason, QueueMilliseconds = result.QueueMilliseconds, LoadMilliseconds = result.LoadMilliseconds };
                        var use = hostUse[(choice.Node, choice.Deployment)];
                        use.Active--;
                        if (result.State == AttemptState.Completed)
                        {
                            use.Failures = 0;
                            var measured = result.Timing?.Elapsed.TotalMilliseconds ?? clock.GetElapsedTime(started).TotalMilliseconds;
                            use.RuntimeMs = use.RuntimeMs is { } prior ? (prior * 3 + measured) / 4 : measured;
                        }
                        if (result.State == AttemptState.Rejected)
                        {
                            use.Failures = Math.Min(5, use.Failures + 1);
                            use.AvoidUntil = clock.GetUtcNow().AddMilliseconds(125 << use.Failures);
                        }
                    }
                    SignalRequests();
                }
                if (result.State == AttemptState.Completed)
                {
                    lock (routing)
                        if (generation == schedulingGeneration)
                            affinity.Remember(new(item.Id, identity, payload.Alias, operation, session, prefixes.LastOrDefault(),
                                choice with { Epoch = result.Epoch }, clock.GetUtcNow().AddMinutes(5),
                                advice.TryGetValue(item.Id, out var route) && route.Node == choice.Node ? route.Suggested : null));
                    return;
                }
                cancellation.ThrowIfCancellationRequested();
                if (item.OutputStarted)
                {
                    context.Abort();
                    return;
                }
                last = result;
                onlyUnreachable &= result.Unreachable;
                if (result.Unreachable)
                    unreachable[choice.Node] = (address, choice.ServingEpoch);
                if (!result.Retryable)
                    break;
                suggested = result.SuggestedNode;
            }
            if (last is not null && (last.State != AttemptState.Rejected || clock.GetElapsedTime(age) < limit))
                throw new ProviderFailure(last.Status, last.Reason ?? "The inference attempt failed.");
            if (clock.GetElapsedTime(age) >= limit)
                throw new ProviderFailure(504, "The request expired while waiting for inference.");
            if (MatchingInventory(payload.Alias).Any(host =>
                    host.Model.LocallyAvailable == true))
                throw new ProviderFailure(422, "The available deployments do not support this operation, context, or request features.");
            throw new ProviderFailure(503, "No eligible model host has available inference capacity.");
        }
        catch (ProviderFailure error)
        {
            item.Reason = error.Message;
            throw;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            item.Reason = context.RequestAborted.IsCancellationRequested ? "The client disconnected." : "Inference authority or host service ended.";
            throw;
        }
        finally
        {
            RecordRoute(log, item, payload);
            lock (routing)
            {
                ingress.Remove(item.Id);
                advice.Remove(item.Id);
                ingressBytes -= payload.Bytes;
                if (generation == schedulingGeneration)
                {
                    if (recentIngress.Count == 64)
                        recentIngress.Dequeue();
                    recentIngress.Enqueue(new(item.Id, item.Alias, item.Operation, item.Source, item.Access.KeyName, item.Access.Priority,
                        item.Arrived, clock.GetUtcNow(), item.Attempts.ToArray(), item.OutputStarted, item.Reason));
                    ArmRoutingExpiry();
                }
            }
            SignalRequests();
        }
    }

    private PlacementChoice? Choose(PlacementChoice[] choices, HashSet<Guid> tried, RouteAffinity? hint, Guid? suggested, IngressRequest request,
        Guid attempt, TimeSpan remaining)
    {
        var now = clock.GetUtcNow();
        var selected = providers.View.Selected;
        var here = Snapshot.Self?.Id ?? Guid.Empty;
        lock (routing)
        {
            var available = choices.Where(item => !tried.Contains(item.Node)).Select(item =>
            {
                var use = hostUse.GetValueOrDefault((item.Node, item.Deployment));
                var inFlight = hostUse.Where(entry => entry.Key.Item1 == item.Node).Sum(entry => entry.Value.Active);
                var concurrent = Math.Max(item.Busy ? 1 : 0, inFlight - item.Slots + 1);
                var wait = concurrent * (use?.RuntimeMs ?? 1000);
                if (use?.AvoidUntil > now)
                    wait += (use.AvoidUntil - now).TotalMilliseconds;
                return (Placement: item, Wait: wait, Cost: wait + (item.Loaded ? 0 : 1000) + (item.PromptMilliseconds ?? 0),
                    WaitKnown: concurrent == 0 || use?.RuntimeMs is not null);
            }).OrderByDescending(item => item.Placement.VerifiedFit)
                .ThenByDescending(item => item.Placement.VerifiedFit ? null : item.Placement.Context)
                .ThenBy(item => item.Placement.Displaces).ThenBy(item => item.Wait > 0)
                .ThenBy(item => item.Placement.Scarcity).ThenBy(item => item.Cost)
                .ThenByDescending(item => item.Placement.Deployment == selected).ThenByDescending(item => item.Placement.Node == here)
                .ThenBy(item => HashCode.Combine(here, item.Placement.Node))
                .ThenBy(item => item.Placement.Deployment, StringComparer.Ordinal).ToArray();
            if (available.Length == 0)
                return null;
            var best = available[0];
            if ((suggested ?? hint?.Advice) is { } advised && available.FirstOrDefault(item => item.Placement.Node == advised) is { Placement: not null } recommended &&
                ComparableRoute(recommended.Placement, best.Placement) && recommended.Wait <= best.Wait)
                best = recommended;
            else if (hint?.Placement.Epoch is { } epoch && available.FirstOrDefault(item => item.Placement.Node == hint.Placement.Node &&
                item.Placement.Deployment == hint.Placement.Deployment && item.Placement.Epoch == epoch) is { Placement: not null } cached &&
                ComparableRoute(cached.Placement, best.Placement) && cached.WaitKnown && cached.Wait < remaining.TotalMilliseconds &&
                (cached.Wait == 0 && best.Wait == 0 ||
                    cached.Placement.CacheSavingsMilliseconds is { } savings && cached.Cost - savings < best.Cost))
                best = cached;
            var choice = best.Placement;
            if (hostUse.Count >= 4096)
                foreach (var old in hostUse.Where(entry => entry.Value.Active == 0).OrderBy(entry => entry.Value.LastUsed).Take(256).ToArray())
                    hostUse.Remove(old.Key);
            var key = (choice.Node, choice.Deployment);
            if (!hostUse.TryGetValue(key, out var state))
                hostUse.Add(key, state = new());
            state.Active++;
            state.LastUsed = now;
            request.Attempts.Add(new(attempt, choice.Node, choice.Deployment, now, AttemptState.Waiting, 0));
            return choice;
        }
    }

    private static bool ComparableRoute(PlacementChoice candidate, PlacementChoice best) =>
        candidate.VerifiedFit == best.VerifiedFit && candidate.Displaces == best.Displaces &&
        (candidate.VerifiedFit || candidate.Context == best.Context);

    private async Task<PlacementChoice[]> ObserveRouteChoicesAsync(PlacementChoice[] choices, InferenceShape shape, Guid? key,
        CancellationToken cancellation)
    {
        if (choices.Select(choice => choice.Node).Distinct().Take(2).Count() < 2)
            return choices;
        var current = Snapshot;
        var here = current.Self?.Id ?? Guid.Empty;
        var probes = choices.OrderByDescending(choice => choice.VerifiedFit).GroupBy(choice => choice.Node)
            .Select(group => group.First()).Take(4);
        var states = await Task.WhenAll(probes.Select(async choice =>
        {
            if (choice.Node == here)
                return (choice.Node, choice.Deployment, State: LocalPlacementState(shape, choice.Deployment, routing: true));
            Interlocked.Increment(ref placementChecks);
            var reply = current.Nodes.TryGetValue(choice.Node, out var peer) && !peer.Removed
                ? await SendAsync<PlacementCheckReply>(current, peer, "inference/eligible", new PlacementCheck(key, choice.Deployment, shape, Routing: true),
                    cancellation, refresh: true, timeout: TimeSpan.FromMilliseconds(750)) : null;
            return (choice.Node, choice.Deployment, State: reply?.Body);
        }));
        var live = states.ToDictionary(state => (state.Node, state.Deployment), state => state.State);
        return choices.Select(choice => live.GetValueOrDefault((choice.Node, choice.Deployment)) is { Eligible: true } state
            ? choice with { Busy = state.Busy ?? choice.Busy, Displaces = state.Displaces ?? choice.Displaces } : choice).ToArray();
    }

    private async Task<AttemptReply> SubmitLocalAsync(Guid origin, AttemptTicket ticket, InferenceRequest payload,
        InferenceAccess access, HttpContext context, CancellationToken cancellation)
    {
        var generation = Volatile.Read(ref schedulingGeneration);
        var log = Logs.Configuration;
        var received = clock.GetUtcNow();
        var started = clock.GetTimestamp();
        if (log.Policy.Mode != RequestLogMode.Off && log.Epoch.Generation == ticket.LogGeneration)
            context.Items[typeof(LogConfiguration)] = log;
        var result = await serving.SubmitAsync(new(origin, ticket, payload, access, context, FindAlternateAsync, Advise, DisplaceAsync,
            () => Snapshot.Digest, Suggest), cancellation);
        RecordServing(log, origin, ticket, payload, access, result, received, started);
        if (result.State == AttemptState.Completed)
            lock (routing)
            {
                if (generation == schedulingGeneration)
                {
                    if (recentServices.Count == 512)
                        recentServices.Dequeue();
                    recentServices.Enqueue(new(origin, ticket, payload.Shape, clock.GetUtcNow()));
                    ArmRoutingExpiry();
                }
            }
        return result;
    }

    private IEnumerable<(Guid Node, ObservedDeployment Model, string ServingEpoch)> Inventory()
    {
        var view = providers.View;
        var current = Snapshot;
        var here = current.Self?.Id ?? Guid.Empty;
        if (!serving.Draining)
            foreach (var deployment in view.Models)
                yield return (here, ObserveDeployment(deployment, view), schedulingEpoch);
        if (!Ready || current.Self is null)
            yield break;
        var sharedModels = current.Records.Models.Where(model => !model.Removed).ToDictionary(model => (model.Owner, model.Deployment));
        foreach (var catalog in current.Records.Catalogs.Where(item => item.Node != here && !Incompatible(item.Node) && item.SchedulingEpoch is not null && item.Role == LocalRole.Contributor &&
            current.Nodes.TryGetValue(item.Node, out var node) && !node.Removed))
            foreach (var model in catalog.Models)
                if ((catalog.ResourceState is null or ContributionState.Contributor || model.Yolo) &&
                    sharedModels.TryGetValue((catalog.Node, model.Id), out var shared))
                    yield return (catalog.Node, model.ProviderIdentifiers is not null ? model :
                        model with { EffectiveAlias = model.EffectiveAlias ?? shared.Alias }, catalog.SchedulingEpoch!);
    }

    private static bool HasProviderIdentifier(ObservedDeployment model, string identifier) =>
        model.ProviderIdentifiers?.Contains(identifier, StringComparer.Ordinal) == true;

    private static IEnumerable<string> RoutingNames(ObservedDeployment model, HashSet<string> native)
    {
        foreach (var identifier in model.ProviderIdentifiers ?? [])
            yield return identifier;
        if (model.EffectiveAlias is { } alias && !native.Contains(alias))
            yield return alias;
    }

    private IEnumerable<(Guid Node, ObservedDeployment Model, string ServingEpoch)> MatchingInventory(string identifier)
    {
        var inventory = Inventory().ToArray();
        return Matches(inventory, identifier);
    }

    private static IEnumerable<(Guid Node, ObservedDeployment Model, string ServingEpoch)> Matches(
        (Guid Node, ObservedDeployment Model, string ServingEpoch)[] inventory, string identifier)
    {
        if (inventory.Any(item => HasProviderIdentifier(item.Model, identifier)))
            return inventory.Where(item => HasProviderIdentifier(item.Model, identifier));
        return inventory.Where(item => string.Equals(item.Model.EffectiveAlias, identifier, StringComparison.OrdinalIgnoreCase));
    }

    private ObservedDeployment ObserveDeployment(Deployment model, CatalogView view)
    {
        var endpoint = view.Endpoints.First(item => item.Id == model.EndpointId);
        var protection = providers.Resources;
        var workloads = model.Model.Operations.Select(operation =>
        {
            if (model.Yolo && protection?.IsAvailable(model, endpoint, operation) == true)
            {
                var nativeTiming = protection.RouteTiming(model, operation);
                return new ObservedWorkload(new(operation, model.Context ?? ProviderLimits.MaxContext), null, null,
                    Math.Clamp(model.Instance?.Slots ?? 1, 1, 256), GenerationTokensPerSecond: nativeTiming.GenerationTokensPerSecond,
                    ProviderGenerationRate: nativeTiming.ProviderGenerationRate);
            }
            var budget = protection?.Estimate(model, endpoint, operation);
            if (budget is null || protection?.IsAvailable(model, endpoint, operation) != true)
                return null;
            var slots = protection.OverlapSlots(model, budget);
            if (slots == 0)
                return null;
            var timing = protection.RouteTiming(model, operation);
            var qualified = protection.QualifiedUnits(model, endpoint, operation);
            return new ObservedWorkload(budget.Workload with { Units = qualified ?? budget.Workload.Units },
                timing.PromptMilliseconds, timing.CacheSavingsMilliseconds, slots, Qualified: qualified is not null,
                GenerationTokensPerSecond: timing.GenerationTokensPerSecond, ProviderGenerationRate: timing.ProviderGenerationRate);
        }).OfType<ObservedWorkload>().ToArray();
        return new(model.Id, DisplayName(model.Model.DisplayName), model.Model.Variant, model.Version, model.Model.Inputs,
            model.Model.Outputs, model.Model.Operations, model.Context, model.ContextSource, model.Online, model.Instance is not null,
            model.Instance?.Fingerprint, model.Instance?.Slots, model.EmbeddingDimensions, model.EmbeddingSpace,
            model.Model.Installed && (protection is null ? model.Eligible : workloads.Length != 0), model.Model.ClientTools,
            model.Model.StructuredOutput, model.MediaTokensPerItem, model.Model.EmbeddingEncodings, model.PlacementEpoch,
            protection is null ? null : workloads, EffectiveAlias: model.Alias, Readiness: protection?.Readiness(model, endpoint,
                exclusiveBlocker: ProviderCatalog.ExclusiveBlocker(view, endpoint)),
            Origin: model.Model.Origin, SourceDevice: SafeDetail(model.Model.SourceDevice, 100),
            ProviderIdentifiers: model.ProviderIdentifiers, Yolo: model.Yolo, Busy: model.Busy || model.Instance?.Busy == true);
    }

    private PlacementChoice[] Candidates(InferenceShape shape)
    {
        var catalog = Inventory().ToArray();
        var models = Matches(catalog, shape.Alias).Where(item => item.Model.LocallyAvailable == true &&
            item.Model.Operations.Contains(shape.Operation)).OrderBy(item => item.Node).ThenBy(item => item.Model.Id, StringComparer.Ordinal).ToArray();
        if (models.Length == 0)
            return [];
        var canonical = models[0].Model;
        bool Fits(ObservedDeployment model, bool allowUnverifiedContext) => shape.Fits(model.Inputs, model.ClientTools,
            model.StructuredOutput, model.Context, model.MediaTokensPerItem, model.EmbeddingEncodings,
            allowUnverifiedContext);
        var choices = models.Where(item => Fits(item.Model, allowUnverifiedContext: true) &&
                (item.Model.Workloads is null || item.Model.Workloads.Any(work =>
                    ResourceProtection.Covers(work.Limit, shape.Demand(item.Model.Context,
                        !item.Model.Loaded && work.Qualified ? work.Limit.Units : null)))))
            .ToArray();
        var native = catalog.SelectMany(item => item.Model.ProviderIdentifiers ?? []).ToHashSet(StringComparer.Ordinal);
        var uniqueAliases = catalog.Where(item => item.Model.LocallyAvailable == true)
            .SelectMany(item => RoutingNames(item.Model, native).Select(identifier => (item.Node, Alias: identifier)))
            .GroupBy(item => item.Alias, StringComparer.Ordinal)
            .Select(group => (Alias: group.Key, Owners: group.Select(item => item.Node).Distinct().Take(2).ToArray()))
            .Where(group => group.Owners.Length == 1 && !string.Equals(group.Alias, shape.Alias, StringComparison.OrdinalIgnoreCase))
            .GroupBy(group => group.Owners[0]).ToDictionary(group => group.Key, group => group.Count());
        return choices.Where(item => shape.Operation != InferenceOperation.Embeddings ||
                item.Model.EmbeddingDimensions == canonical.EmbeddingDimensions && item.Model.EmbeddingSpace == canonical.EmbeddingSpace)
            .Select(item =>
            {
                var timing = item.Model.Workloads?.FirstOrDefault(work => work.Limit.Operation == shape.Operation);
                var contextBound = (long?)item.Model.Context ?? (!item.Model.Loaded && timing is { Qualified: true } ? timing.Limit.Units : (long?)null);
                var scarce = uniqueAliases.GetValueOrDefault(item.Node);
                if (item.Model.Context is { } context && choices.Any(other => other.Node != item.Node && other.Model.Context < context / 2))
                    scarce++;
                var busy = item.Model.Busy || item.Model.Readiness?.State == "busy";
                var residentBusy = busy && !item.Model.Loaded && item.Model.Endpoint is { } endpoint &&
                    catalog.Any(other => other.Node == item.Node && other.Model.Endpoint == endpoint && other.Model.Loaded);
                return new PlacementChoice(item.Node, item.Model.Id, item.Model.PlacementEpoch, item.Model.Loaded,
                    shape.Items == 1 ? timing?.Slots ?? 1 : 1, timing?.PromptMilliseconds, timing?.CacheSavingsMilliseconds,
                    scarce, item.ServingEpoch,
                    shape.FitsEstimatedContext(contextBound, item.Model.MediaTokensPerItem), contextBound, busy, residentBusy);
            }).ToArray();
    }

    private Guid? Suggest(InferenceShape shape)
    {
        var here = Snapshot.Self?.Id ?? Guid.Empty;
        return Candidates(shape).Where(item => item.Node != here && item.VerifiedFit).OrderBy(item => item.Scarcity)
            .ThenByDescending(item => item.Loaded).FirstOrDefault()?.Node;
    }

    private async Task<Guid?> FindAlternateAsync(AlternateProbe probe, CancellationToken cancellation)
    {
        var current = Snapshot;
        var here = current.Self?.Id ?? Guid.Empty;
        var options = Candidates(probe.Shape).Where(item => item.Node != here).ToArray();
        var uncertain = options.Length > 4 || options.Any(option => !option.VerifiedFit);
        foreach (var option in options.Where(option => option.VerifiedFit).Take(4))
            if (current.Nodes.TryGetValue(option.Node, out var peer) && !peer.Removed)
            {
                Interlocked.Increment(ref placementChecks);
                var reply = await SendAsync<PlacementCheckReply>(current, peer, "inference/eligible",
                    new PlacementCheck(probe.Key, option.Deployment, probe.Shape), cancellation, refresh: true);
                if (reply?.Body is { Eligible: true })
                    return peer.Id;
                uncertain |= reply?.Body is null;
            }
        return uncertain ? Guid.Empty : null;
    }

    private void Advise(ServingQueue.Work work, Guid node) => _ = AdviseAsync(work.Origin, work.Ticket.Request, node, stopping.Token);

    private async Task DisplaceAsync(string deployment, CancellationToken cancellation)
    {
        RecentService[] affected;
        lock (routing)
            affected = recentServices.Where(item => item.Ticket.Deployment == deployment && item.At >= clock.GetUtcNow().AddMinutes(-5))
                .GroupBy(item => (item.Origin, item.Ticket.Key, item.Ticket.Session)).Select(group => group.Last()).TakeLast(8).ToArray();
        var here = Snapshot.Self?.Id ?? Guid.Empty;
        foreach (var previous in affected)
        {
            var alternate = Candidates(previous.Shape).Where(item => item.Node != here).OrderBy(item => item.Scarcity)
                .ThenByDescending(item => item.Loaded).FirstOrDefault();
            if (alternate is not null)
                await AdviseAsync(previous.Origin, previous.Ticket.Request, alternate.Node, cancellation);
        }
    }

    private async Task AdviseAsync(Guid origin, Guid request, Guid suggested, CancellationToken cancellation)
    {
        var current = Snapshot;
        var here = current.Self?.Id ?? Guid.Empty;
        if (origin == here)
            ReceiveAdvice(here, new(request, suggested));
        else if (current.Nodes.TryGetValue(origin, out var ingressNode) && !ingressNode.Removed)
            try { await SendAsync<object>(current, ingressNode, "inference/advice", new RouteAdvice(request, suggested), cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private void ReceiveAdvice(Guid servingNode, RouteAdvice notice)
    {
        if (notice.Suggested == servingNode || !Snapshot.Nodes.TryGetValue(notice.Suggested, out var node) || node.Removed)
            return;
        lock (routing)
        {
            if (ingress.TryGetValue(notice.Request, out var item) && item.Attempts.LastOrDefault()?.Node == servingNode)
                advice[notice.Request] = (servingNode, notice.Suggested);
            affinity.Advise(notice.Request, servingNode, notice.Suggested);
        }
    }

    private Task DiscoverMissingAsync(CancellationToken cancellation)
    {
        lock (discoveryLock)
        {
            if (discovery is { IsCompleted: false })
                return discovery.WaitAsync(cancellation);
            var now = clock.GetUtcNow();
            if (now < discoverAfter)
                return Task.CompletedTask;
            discoverAfter = now.AddSeconds(10);
            discovery = SearchAsync();
            return discovery.WaitAsync(cancellation);
        }
    }

    private async Task SearchAsync()
    {
        Interlocked.Increment(ref discoveryRuns);
        try
        {
            var results = await DiscoverAsync(null, stopping.Token);
            var current = Snapshot;
            var identity = current.Records.Identity;
            if (identity is null)
                return;
            await Parallel.ForEachAsync(results.Where(item => item.Compatible && item.Peer.Identity == identity), new ParallelOptions
            {
                MaxDegreeOfParallelism = 8, CancellationToken = stopping.Token
            }, async (result, token) =>
            {
                var peer = current.Nodes.GetValueOrDefault(result.Peer.Node) ??
                    new PeerNode(result.Peer.Node, result.Peer.Device, "Mango", result.Address, identity.Generation, identity.Generation, "");
                if (!peer.Removed)
                    await ReconcileOneAsync(peer with { Address = result.Address }, token);
            });
        }
        catch (Exception error) when (error is IOException or PeerFailure or OperationCanceledException) { }
    }
}
