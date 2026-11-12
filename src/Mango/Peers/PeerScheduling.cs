using Mango.Host;
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
    private readonly Queue<(Guid Id, AttemptTrace[] Attempts, bool Output)> recentIngress = new();
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

    public int QueueWaitSeconds => Snapshot.Records.Queue?.WaitSeconds ?? QueuePolicy.DefaultWaitSeconds;

    private void ClearSchedulingMemory()
    {
        lock (routing)
        {
            affinity.Clear();
            advice.Clear();
            recentServices.Clear();
            recentIngress.Clear();
            foreach (var key in hostUse.Where(item => item.Value.Active == 0).Select(item => item.Key).ToArray())
                hostUse.Remove(key);
        }
    }

    public async Task SetQueuePolicyAsync(HttpRequest request, int seconds, CancellationToken cancellation)
    {
        if (seconds is <= 0 or > QueuePolicy.MaxWaitSeconds)
            throw new PeerFailure(400, $"Enter a queue wait from 1 to {QueuePolicy.MaxWaitSeconds} seconds.");
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireMember(current);
            RequireAdmin(current, AdminHeader(request));
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
        lock (routing)
            return new
            {
                QueueWaitSeconds, counts.Waiting, counts.Running, DiscoveryRuns = Volatile.Read(ref discoveryRuns),
                Submissions = Volatile.Read(ref submissions), StopQueries = Volatile.Read(ref stopQueries),
                PlacementChecks = Volatile.Read(ref placementChecks), DigestMerges = Volatile.Read(ref digestMerges),
                Ingress = ingress.Values.Select(item => new { item.Id, item.Alias, item.Operation, item.Source,
                    item.Access.KeyId, item.Access.Priority, item.Arrived, item.QueueUntil, item.OutputStarted,
                    Attempts = item.Attempts.ToArray() }).ToArray(),
                Recent = recentIngress.Select(item => new { item.Id, item.Attempts, item.Output }).ToArray()
            };
    }

    public object ModelList(InferenceAccess? access)
    {
        var names = Inventory().Where(item => item.Model.LocallyAvailable == true &&
                item.Model.Operations.Any(operation => access?.Allows(operation) != false))
            .Select(item => item.Alias).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        return new { @object = "list", data = names.Select(alias => new { id = alias, @object = "model", owned_by = "mango" }).ToArray() };
    }

    public async Task RouteAsync(InferenceOperation operation, HttpContext context, CancellationToken cancellation)
    {
        var arrived = clock.GetUtcNow();
        var age = clock.GetTimestamp();
        var limit = TimeSpan.FromSeconds(QueueWaitSeconds);
        if (providers.Resources is { } resources)
            await resources.ClientArrivedAsync(cancellation);
        using var payload = await providers.ReadInferenceAsync(operation, context, cancellation);
        if (string.Equals(payload.Alias, "best", StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure(501, "Automatic best-model selection is not available.");
        var access = InferenceAccess.Of(context) ?? throw new ProviderFailure(401, "The inference credential is required.");
        var source = context.Connection.RemoteIpAddress?.ToString() ?? "";
        var session = context.Request.Headers["Mango-Session"].ToString();
        if (session.Length > 256)
            throw new ProviderFailure(400, "Mango-Session may contain up to 256 characters.");
        session = session.Length == 0 ? null : ProviderIdentity.Hash(session);
        var prefixes = session is null ? payload.Prefixes() : [];
        var identity = access.KeyId ?? Guid.Empty;
        var item = new IngressRequest(Guid.NewGuid(), arrived, arrived + limit, access, source, payload.Alias, operation);
        lock (routing)
        {
            if (ingress.Count >= MaxIngress || ingressBytes + payload.Bytes > MaxIngressBytes)
                throw new ProviderFailure(429, "Mango's ingress input limit is busy.");
            ingress.Add(item.Id, item);
            ingressBytes += payload.Bytes;
        }
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
                    choices = Candidates(payload.Shape);
                    foreach (var candidate in choices)
                        if (unreachable.TryGetValue(candidate.Node, out var failed) &&
                            (failed.Address != Snapshot.Nodes.GetValueOrDefault(candidate.Node)?.Address || failed.Epoch != candidate.ServingEpoch))
                            tried.Remove(candidate.Node);
                    continue;
                }
                tried.Add(choice.Node);
                attemptCount++;
                var elapsed = clock.GetElapsedTime(age);
                var remaining = limit - elapsed;
                var here = Snapshot.Self?.Id ?? Guid.Empty;
                var address = Snapshot.Nodes.GetValueOrDefault(choice.Node)?.Address;
                var ticket = new AttemptTicket(item.Id, attempt, access.KeyId, choice.Deployment,
                    clock.GetUtcNow() - elapsed, clock.GetUtcNow() + remaining, session, choice.ServingEpoch);
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
                        item.Attempts[index] = item.Attempts[index] with { State = result.State, Status = result.Status, Timing = result.Timing };
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
                }
                if (result.State == AttemptState.Completed)
                {
                    lock (routing)
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
            if (Inventory().Any(host => string.Equals(host.Alias, payload.Alias, StringComparison.OrdinalIgnoreCase) &&
                    host.Model.LocallyAvailable == true))
                throw new ProviderFailure(422, "The available deployments do not support this operation, context, or request features.");
            throw new ProviderFailure(503, "No eligible model host has available inference capacity.");
        }
        finally
        {
            lock (routing)
            {
                ingress.Remove(item.Id);
                advice.Remove(item.Id);
                ingressBytes -= payload.Bytes;
                if (recentIngress.Count == 256)
                    recentIngress.Dequeue();
                recentIngress.Enqueue((item.Id, item.Attempts.ToArray(), item.OutputStarted));
            }
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
                var concurrent = Math.Max(0, inFlight - item.Slots + 1);
                var wait = concurrent * (use?.RuntimeMs ?? 1000);
                if (use?.AvoidUntil > now)
                    wait += (use.AvoidUntil - now).TotalMilliseconds;
                if (!item.Loaded)
                    wait += 1000;
                return (Placement: item, Wait: wait, Cost: wait + (item.PromptMilliseconds ?? 0),
                    WaitKnown: concurrent == 0 || use?.RuntimeMs is not null);
            }).OrderByDescending(item => item.Placement.VerifiedFit).ThenBy(item => item.Wait > 0)
                .ThenBy(item => item.Placement.Scarcity).ThenBy(item => item.Cost)
                .ThenByDescending(item => item.Placement.Deployment == selected).ThenByDescending(item => item.Placement.Node == here)
                .ThenBy(item => HashCode.Combine(here, item.Placement.Node))
                .ThenBy(item => item.Placement.Deployment, StringComparer.Ordinal).ToArray();
            if (available.Length == 0)
                return null;
            var best = available[0];
            if ((suggested ?? hint?.Advice) is { } advised && available.FirstOrDefault(item => item.Placement.Node == advised) is { Placement: not null } recommended &&
                recommended.Placement.VerifiedFit == best.Placement.VerifiedFit && recommended.Wait <= best.Wait)
                best = recommended;
            else if (hint?.Placement.Epoch is { } epoch && available.FirstOrDefault(item => item.Placement.Node == hint.Placement.Node &&
                item.Placement.Deployment == hint.Placement.Deployment && item.Placement.Epoch == epoch) is { Placement: not null } cached &&
                cached.Placement.VerifiedFit == best.Placement.VerifiedFit && cached.WaitKnown && cached.Wait < remaining.TotalMilliseconds &&
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

    private async Task<AttemptReply> SubmitLocalAsync(Guid origin, AttemptTicket ticket, InferenceRequest payload,
        InferenceAccess access, HttpContext context, CancellationToken cancellation)
    {
        var result = await serving.SubmitAsync(new(origin, ticket, payload, access, context, FindAlternateAsync, Advise, DisplaceAsync,
            () => Snapshot.Digest, Suggest), cancellation);
        if (result.State == AttemptState.Completed)
            lock (routing)
            {
                if (recentServices.Count == 512)
                    recentServices.Dequeue();
                recentServices.Enqueue(new(origin, ticket, payload.Shape, clock.GetUtcNow()));
            }
        return result;
    }

    private IEnumerable<(Guid Node, string Alias, ObservedDeployment Model, string ServingEpoch)> Inventory()
    {
        var view = providers.View;
        var current = Snapshot;
        var here = current.Self?.Id ?? Guid.Empty;
        foreach (var deployment in view.Models)
            yield return (here, deployment.Alias, ObserveDeployment(deployment, view), schedulingEpoch);
        if (!Ready || current.Self is null)
            yield break;
        var sharedModels = current.Records.Models.Where(model => !model.Removed).ToDictionary(model => (model.Owner, model.Deployment));
        foreach (var catalog in current.Records.Catalogs.Where(item => item.Node != here && item.SchedulingEpoch is not null && item.Role == LocalRole.Contributor &&
            (item.ResourceState is null or ContributionState.Contributor) && current.Nodes.TryGetValue(item.Node, out var node) && !node.Removed))
            foreach (var model in catalog.Models)
                if (sharedModels.TryGetValue((catalog.Node, model.Id), out var shared))
                    yield return (catalog.Node, shared.Alias, model, catalog.SchedulingEpoch!);
    }

    private ObservedDeployment ObserveDeployment(Deployment model, CatalogView view)
    {
        var endpoint = view.Endpoints.First(item => item.Id == model.EndpointId);
        var protection = providers.Resources;
        var workloads = model.Model.Operations.Select(operation =>
        {
            var budget = protection?.Estimate(model, endpoint, operation);
            if (budget is null || protection?.IsAvailable(model, endpoint, operation) != true)
                return null;
            var slots = protection.OverlapSlots(model, budget);
            if (slots == 0)
                return null;
            var timing = protection.RouteTiming(model, operation);
            return new ObservedWorkload(budget.Workload, timing.PromptMilliseconds, timing.CacheSavingsMilliseconds, slots);
        }).OfType<ObservedWorkload>().ToArray();
        return new(model.Id, DisplayName(model.Model.DisplayName), model.Model.Variant, model.Version, model.Model.Inputs,
            model.Model.Outputs, model.Model.Operations, model.Context, model.ContextSource, model.Online, model.Instance is not null,
            model.Instance?.Fingerprint, model.Instance?.Slots, model.EmbeddingDimensions, model.EmbeddingSpace,
            model.Model.Installed && (protection is null ? model.Eligible : workloads.Length != 0), model.Model.ClientTools,
            model.Model.StructuredOutput, model.Options.MediaTokensPerItem, model.Model.EmbeddingEncodings, model.PlacementEpoch,
            protection is null ? null : workloads);
    }

    private PlacementChoice[] Candidates(InferenceShape shape)
    {
        var catalog = Inventory().Where(item => item.Model.LocallyAvailable == true).ToArray();
        var models = catalog.Where(item => string.Equals(item.Alias, shape.Alias, StringComparison.OrdinalIgnoreCase) &&
            item.Model.Operations.Contains(shape.Operation)).OrderBy(item => item.Node).ThenBy(item => item.Model.Id, StringComparer.Ordinal).ToArray();
        if (models.Length == 0)
            return [];
        var canonical = models[0].Model;
        bool Fits(ObservedDeployment model, bool allowUnverifiedContext) => shape.Fits(model.Inputs, model.ClientTools,
            model.StructuredOutput, model.Context, model.MediaTokensPerItem, model.EmbeddingDimensions, model.EmbeddingEncodings,
            allowUnverifiedContext);
        var choices = models.Where(item => Fits(item.Model, allowUnverifiedContext: true) &&
                (item.Model.Workloads is null || item.Model.Workloads.Any(work =>
                    ResourceProtection.Covers(work.Limit, shape.Demand(item.Model.Context)))))
            .ToArray();
        var uniqueAliases = catalog.GroupBy(item => item.Alias, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Alias: group.Key, Owners: group.Select(item => item.Node).Distinct().Take(2).ToArray()))
            .Where(group => group.Owners.Length == 1 && !string.Equals(group.Alias, shape.Alias, StringComparison.OrdinalIgnoreCase))
            .GroupBy(group => group.Owners[0]).ToDictionary(group => group.Key, group => group.Count());
        return choices.Where(item => item.Model.Inputs.ToHashSet().SetEquals(canonical.Inputs) &&
                item.Model.Outputs.ToHashSet().SetEquals(canonical.Outputs) && (shape.Operation != InferenceOperation.Embeddings ||
                    item.Model.EmbeddingDimensions == canonical.EmbeddingDimensions && item.Model.EmbeddingSpace == canonical.EmbeddingSpace))
            .Select(item =>
            {
                var timing = item.Model.Workloads?.FirstOrDefault(work => work.Limit.Operation == shape.Operation);
                var scarce = uniqueAliases.GetValueOrDefault(item.Node);
                if (item.Model.Context is { } context && choices.Any(other => other.Node != item.Node && other.Model.Context < context / 2))
                    scarce++;
                return new PlacementChoice(item.Node, item.Model.Id, item.Model.PlacementEpoch, item.Model.Loaded,
                    shape.Items == 1 ? timing?.Slots ?? 1 : 1, timing?.PromptMilliseconds, timing?.CacheSavingsMilliseconds,
                    scarce, item.ServingEpoch, Fits(item.Model, allowUnverifiedContext: false));
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
            await Parallel.ForEachAsync(results.Where(item => item.Peer.Identity == identity), new ParallelOptions
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
