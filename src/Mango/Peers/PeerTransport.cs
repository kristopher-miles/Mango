using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Mango.Peers;

internal sealed record PeerIntro(NetworkIdentity Identity, Guid Node, string Device);
internal sealed record DiscoveryResult(string Address, PeerIntro Peer);
internal sealed record PeerNotice(Guid Network, Revision Generation, Guid? Removed = null);
internal sealed record PeerAuthentication(Guid Node, PeerNotice? Notice);
internal sealed record PeerVersions(RecordVersion[] Records);
internal sealed record PeerKeyReply(InferenceKey? Key);
internal sealed record PushResult(int Confirmed, int Pending);

internal sealed partial class PeerNetwork
{
    internal const string NodeHeader = "Mango-Node";
    internal const string NetworkHeader = "Mango-Network";
    internal const string DigestHeader = "Mango-Digest";
    internal const string RevisionHeader = "Mango-Revision";
    private const string PeerRoot = "/peer/";

    private sealed record PeerReply<T>(T? Body, string? Digest, bool Authenticated = true);
    private enum ReconcileResult { Unavailable, Synchronized, Incomplete }

    public PeerIntro? Introduction()
    {
        var current = Snapshot;
        return current.Self is { } self && current.Records.Identity is { } identity && !DeviceChanged(current)
            ? new(identity, self.Id, self.Device) : null;
    }

    public async Task<DiscoveryResult[]> DiscoverAsync(string? address, CancellationToken cancellation)
    {
        var changed = await RefreshTransportAsync(cancellation);
        if (changed && IsMember)
            await ReconcileAsync(cancellation, gate: true);
        var current = Snapshot;
        if (!string.IsNullOrWhiteSpace(address))
        {
            var target = Tailnet.Address(address, port) ?? throw new PeerFailure(400, "Enter a Tailscale IP address and optional service port.");
            var found = await GetIntroAsync(target, cancellation);
            return found is null ? [] : [new(target.GetLeftPart(UriPartial.Authority), found)];
        }
        var foundPeers = new System.Collections.Concurrent.ConcurrentBag<DiscoveryResult>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(6));
        var remembered = current.Others.SelectMany(Origins).Distinct().ToArray();
        async Task ProbeAsync(IEnumerable<Uri> addresses)
        {
            await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = deadline.Token },
                async (target, token) =>
                {
                    if (await GetIntroAsync(target, token) is { } info && info.Node != current.Self?.Id)
                        foundPeers.Add(new(target.GetLeftPart(UriPartial.Authority), info));
                });
        }
        try
        {
            await ProbeAsync(remembered);
            await ProbeAsync(tailnet.Candidates.SelectMany(peer => peer.Addresses).Select(ip => new Uri(Tailnet.Origin(ip, port)))
                .Except(remembered).Take(PeerLimits.DiscoveryCandidates));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        return foundPeers.GroupBy(found => (found.Peer.Identity.Id, found.Peer.Node))
            .Select(group => group.OrderBy(item => item.Address, StringComparer.Ordinal).First())
            .OrderBy(item => item.Peer.Identity.Id).ThenBy(item => item.Peer.Node).ToArray();
    }

    private async Task<PeerIntro?> GetIntroAsync(Uri target, CancellationToken cancellation)
    {
        try
        {
            using var timeout = CallDeadline(cancellation);
            using var response = await http.GetAsync(new Uri(target, PeerRoot + "info"), timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;
            var reply = await response.Content.ReadFromJsonAsync<PeerIntro>(Json, timeout.Token);
            return reply is not null && PeerValidation.Valid(reply.Identity) && reply.Node != Guid.Empty &&
                reply.Device is { Length: > 0 and <= 128 } && MatchesDevice(target, reply.Device) ? reply : null;
        }
        catch (Exception error) when (Unavailable(error, cancellation)) { return null; }
    }

    private bool MatchesDevice(Uri origin, string device)
    {
        if (!IPAddress.TryParse(origin.DnsSafeHost, out var ip))
            return false;
        var reported = tailnet.Candidates.FirstOrDefault(item => item.Addresses.Contains(ip));
        return reported is null || reported.Device.Length == 0 || reported.Device == device;
    }

    private IEnumerable<Uri> Origins(PeerNode node)
    {
        var address = Tailnet.Address(node.Address);
        if (address is not null && MatchesDevice(address, node.Device))
            yield return address;
        foreach (var ip in tailnet.Candidates.Where(candidate => candidate.Device == node.Device).SelectMany(candidate => candidate.Addresses))
        {
            var alternate = new Uri(Tailnet.Origin(ip, address?.Port ?? port));
            if (alternate != address)
                yield return alternate;
        }
    }

    public PeerAuthentication? Authenticate(HttpRequest request)
    {
        var current = Snapshot;
        var secret = PeerCredentials.Bearer(request);
        if (current.Self is null || DeviceChanged(current) || current.Records.Identity is not { } identity ||
            !Guid.TryParseExact(request.Headers[NetworkHeader].ToString(), "N", out var network) || network != identity.Id ||
            !Guid.TryParseExact(request.Headers[NodeHeader].ToString(), "N", out var node) || !PeerCredentials.IsSecret(secret))
            return null;
        if (current.Nodes.TryGetValue(node, out var member) && PeerCredentials.Verify(secret, member.Verifier))
            return new(node, member.Removed ? new(network, identity.Generation, node) : null);
        var digest = PeerCredentials.Digest(secret!);
        return current.Retired.TryGetValue(digest, out var retired) && retired.Node == node
            ? new(node, new(network, identity.Generation)) : null;
    }

    public void SetResponseHeaders(HttpResponse response)
    {
        var current = Snapshot;
        if (current.Self is not { } self || current.Records.Identity is not { } identity)
            return;
        response.OnStarting(() =>
        {
            var latest = Snapshot;
            if (latest.Self?.Id != self.Id || latest.Records.Identity?.Id != identity.Id)
                latest = current;
            response.Headers[NodeHeader] = self.Id.ToString("N");
            response.Headers[NetworkHeader] = identity.Id.ToString("N");
            response.Headers[DigestHeader] = latest.Digest;
            response.Headers[RevisionHeader] = latest.Latest.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });
    }

    public void ObservePeer(Guid peer, HttpRequest request, bool refresh)
    {
        var digest = request.Headers[DigestHeader].ToString();
        if (!PeerCredentials.IsSecret(digest))
            return;
        contacts[peer] = new(clock.GetUtcNow(), true, digest);
        if (refresh)
            QueueMismatch(peer, digest);
    }

    private async Task<PeerReply<T>?> SendAsync<T>(PeerSnapshot from, PeerNode peer, string route, object? body,
        CancellationToken cancellation, bool refresh = false)
    {
        if (from.Self is not { } self || from.Records.Identity is not { } identity || from.Token is null || DeviceChanged(from))
            return null;
        foreach (var target in Origins(peer).Take(2))
        {
            try
            {
                using var timeout = CallDeadline(cancellation);
                using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, new Uri(target, PeerRoot + route));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", from.Token);
                request.Headers.Add(NodeHeader, self.Id.ToString("N"));
                request.Headers.Add(NetworkHeader, identity.Id.ToString("N"));
                request.Headers.Add(DigestHeader, from.Digest);
                request.Headers.Add(RevisionHeader, from.Latest.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (body is not null)
                    request.Content = JsonContent.Create(body, options: Json);
                using var response = await http.SendAsync(request, timeout.Token);
                if (route == "ping" && response.StatusCode == HttpStatusCode.Unauthorized &&
                    await GetIntroAsync(target, timeout.Token) is { } intro && intro.Node == peer.Id &&
                    intro.Device == peer.Device && intro.Identity.Id == identity.Id)
                {
                    if (intro.Identity.Generation.CompareTo(identity.Generation) > 0)
                        await ApplyNoticeAsync(new(identity.Id, intro.Identity.Generation), cancellation);
                    return new(default, null, Authenticated: false);
                }
                if (Header(response, NodeHeader) != peer.Id.ToString("N") ||
                    Header(response, NetworkHeader) != identity.Id.ToString("N"))
                    continue;
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    var notice = await response.Content.ReadFromJsonAsync<PeerNotice>(Json, timeout.Token);
                    if (notice is not null)
                        await ApplyNoticeAsync(notice, cancellation);
                    return null;
                }
                if (!response.IsSuccessStatusCode)
                    continue;
                var digest = Header(response, DigestHeader);
                var result = response.StatusCode == HttpStatusCode.NoContent ? default :
                    await response.Content.ReadFromJsonAsync<T>(Json, timeout.Token);
                contacts[peer.Id] = new(clock.GetUtcNow(), true, digest);
                if (refresh)
                    QueueMismatch(peer.Id, digest);
                return new(result, digest);
            }
            catch (Exception error) when (Unavailable(error, cancellation)) { }
        }
        if (contacts.TryGetValue(peer.Id, out var previous))
            contacts[peer.Id] = previous with { Reachable = false };
        return null;
    }

    private CancellationTokenSource CallDeadline(CancellationToken cancellation)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token);
        timeout.CancelAfter(PeerLimits.CallTimeout);
        return timeout;
    }

    private static bool Unavailable(Exception error, CancellationToken cancellation) =>
        error is HttpRequestException or IOException or JsonException ||
        error is OperationCanceledException && !cancellation.IsCancellationRequested;

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private async Task<PushResult> PushChangesAsync(PeerSnapshot before, PeerSnapshot after, CancellationToken cancellation,
        Guid? exclude = null)
    {
        if (before.Records.Identity != after.Records.Identity || before.Digest == after.Digest || after.Self is null)
            return new(0, 0);
        var delta = after.Records.Delta(before.Versions);
        if (delta.IsEmptyDelta)
            return new(0, 0);
        return await PushAsync(after, after.Others.Where(peer => peer.Id != exclude), "push", delta, cancellation);
    }

    private async Task<PushResult> PushAsync(PeerSnapshot from, IEnumerable<PeerNode> peers, string route, object body,
        CancellationToken cancellation)
    {
        var targets = peers.ToArray();
        var acknowledged = 0;
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation },
            async (peer, token) =>
            {
                if (await SendAsync<object>(from, peer, route, body, token, refresh: route == "push") is not null)
                    Interlocked.Increment(ref acknowledged);
            });
        return new(acknowledged, targets.Length - acknowledged);
    }

    public async Task RefreshAsync(CancellationToken cancellation)
    {
        var changed = await RefreshTransportAsync(cancellation);
        await ReconcileAsync(cancellation, gate: changed || !Ready);
        QueueCatalog();
    }

    private async Task ReconcileAsync(CancellationToken cancellation, bool gate)
    {
        await reconciliation.WaitAsync(cancellation);
        if (gate)
            ready = false;
        var synchronized = 0;
        var incomplete = 0;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token);
            var current = Snapshot;
            var others = current.Others.ToArray();
            var waves = (Math.Max(0, others.Length - 1) + 11) / 12;
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(PeerLimits.ReconcileTimeout.TotalSeconds, 4 + 4 * waves)));
            if (current.Self is not null && !DeviceChanged(current))
            {
                async Task ContactAsync(PeerNode peer, CancellationToken token)
                {
                    if (Snapshot.Self is null)
                        return;
                    var result = await ReconcileOneAsync(peer, token);
                    if (result == ReconcileResult.Synchronized)
                        Interlocked.Increment(ref synchronized);
                    if (result == ReconcileResult.Incomplete)
                        Interlocked.Increment(ref incomplete);
                }

                try
                {
                    if (others.Length != 0)
                    {
                        await ContactAsync(others[0], deadline.Token);
                        await Parallel.ForEachAsync(others.Skip(1), new ParallelOptions
                        {
                            MaxDegreeOfParallelism = 12,
                            CancellationToken = deadline.Token
                        }, async (peer, token) => await ContactAsync(peer, token));
                    }
                    await UpdateAddressAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
            }
            if (synchronized > 0)
                needsPeerConfirmation = false;
            Volatile.Write(ref lastReconciliation, new(clock.GetUtcNow(), others.Length > 0 && synchronized == 0 && incomplete == 0));
        }
        finally
        {
            if (gate && !cancellation.IsCancellationRequested)
                ready = Snapshot.Self is null || (!needsPeerConfirmation && (synchronized > 0 || incomplete == 0));
            reconciliation.Release();
        }
    }

    private async Task<ReconcileResult> ReconcileOneAsync(PeerNode peer, CancellationToken cancellation)
    {
        var current = Snapshot;
        var ping = await SendAsync<object>(current, peer, "ping", null, cancellation);
        if (ping is null)
            return ReconcileResult.Unavailable;
        if (!ping.Authenticated)
            return ReconcileResult.Incomplete;
        if (ping.Digest == current.Digest)
            return ReconcileResult.Synchronized;
        try
        {
            var versions = await SendAsync<PeerVersions>(current, peer, "versions", null, cancellation);
            if (versions?.Body is not { } remote || !ValidVersions(remote.Records))
                return ReconcileResult.Incomplete;
            var reply = await SendAsync<NetworkRecords>(current, peer, "pull", new PeerVersions(current.Versions), cancellation);
            if (reply?.Body is not { } delta || !PeerValidation.Valid(delta))
                return ReconcileResult.Incomplete;
            var observed = remote.Records.Select(record => record.Revision.Time).DefaultIfEmpty().Max();
            await MergeAsync(delta, cancellation, observed);
            var merged = Snapshot;
            if (merged.Self is not null && merged.Records.Identity == current.Records.Identity)
            {
                var changes = merged.Records.Delta(remote.Records);
                if (!changes.IsEmptyDelta)
                    await SendAsync<object>(merged, peer, "push", changes, cancellation);
            }
            return ReconcileResult.Synchronized;
        }
        catch (Exception error) when (error is OperationCanceledException or PeerFailure or IOException)
        {
            return ReconcileResult.Incomplete;
        }
    }

    private async Task UpdateAddressAsync(CancellationToken cancellation)
    {
        if (bound.Length == 0)
            return;
        var (before, after) = await ChangeAsync(current =>
        {
            if (current.Self is not { } self || DeviceChanged(current))
                return current.Disk;
            var address = bound.Any(ip => Tailnet.Origin(ip, port) == self.Address) ? self.Address : Tailnet.Origin(bound[0], port);
            if (address == self.Address)
                return current.Disk;
            var records = current.Records.Merge(NetworkRecords.Empty with
            {
                Identity = current.Records.Identity,
                Nodes = [self with { Address = address, Revision = Next(current, self.Id) }]
            });
            return current.Disk with { Records = records };
        }, cancellation);
        await PushChangesAsync(before, after, cancellation);
    }

    public static bool ValidVersions(RecordVersion[]? versions) => versions is { Length: <= PeerLimits.Versions } &&
        versions.All(version => Enum.IsDefined(version.Kind) && version.Id is { Length: <= 180 } &&
            PeerValidation.Valid(version.Revision)) &&
        versions.Select(version => (version.Kind, version.Id)).Distinct().Count() == versions.Length;

    public async Task MergeAsync(NetworkRecords incoming, CancellationToken cancellation, long observed = 0)
    {
        if (!PeerValidation.Valid(incoming))
            throw new PeerFailure(400, "Invalid Mango network records.");
        observed = Math.Max(observed, incoming.Versions().Select(record => record.Revision.Time).DefaultIfEmpty().Max());
        await ChangeAsync(current =>
        {
            if (current.Self is null || current.Records.Identity != incoming.Identity)
                throw new PeerFailure(409, "The Mango network generation changed.");
            var merged = current.Records.Merge(incoming);
            if (ReferenceEquals(merged, current.Records) && observed <= current.Latest)
                return current.Disk;
            if (!PeerValidation.Valid(merged))
                throw new PeerFailure(409, "The network record limit was reached.");
            var removed = merged.Nodes.Any(node => node.Id == current.Self.Id && node.Removed);
            return current.Disk with
            {
                Records = merged,
                LastObserved = observed,
                Local = removed ? current.Local! with { ProtectedSecret = null } : current.Local
            };
        }, cancellation);
    }
}
