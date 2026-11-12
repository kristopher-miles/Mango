using Mango.Host;

namespace Mango.Peers;

internal sealed partial class PeerNetwork
{
    internal WindowsUpdates? Updates { get; set; }

    internal async Task<bool> PredecessorUpdatedAsync(Guid id, string version, CancellationToken cancellation)
    {
        var current = Snapshot;
        return current.Nodes.TryGetValue(id, out var peer) && !peer.Removed &&
            await SendAsync<object>(current, peer, "ping", null, cancellation) is { Authenticated: true } &&
            peerReleases.GetValueOrDefault(id) is { Compatible: true, Release: { } release } &&
            UpdateManifest.ParseVersion(release.Version) >= UpdateManifest.ParseVersion(version);
    }

    internal async Task<object> ShareUpdateAsync(string version, bool allowPartial, CancellationToken cancellation)
    {
        var current = Snapshot;
        if (!IsMember || !Ready)
            throw new PeerFailure(409, "Join a Mango network before sharing an update.");

        var members = current.Others.OrderBy(peer => peer.Joined).ThenBy(peer => peer.Id).ToArray();
        var statuses = new string[members.Length];
        async Task RequestAsync(int index, int? delay, Guid? after, CancellationToken token)
        {
            var response = await SendAsync<UpdateReply>(current, members[index], "update",
                new UpdateOffer(version, delay, after), token, timeout: TimeSpan.FromSeconds(18));
            statuses[index] = response?.Body?.State ?? "Unreachable or incompatible";
        }
        var options = new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellation };
        await Parallel.ForEachAsync(Enumerable.Range(0, members.Length), options, (i, token) => new(RequestAsync(i, null, null, token)));
        var needsOverride = !allowPartial && statuses.Any(state => state is not
            ("Eligible" or "Already current" or "Updates are off" or "Maintenance deferred" or "This installation cannot update automatically"));
        if (!needsOverride)
        {
            var eligible = Enumerable.Range(0, members.Length).Where(i => statuses[i] == "Eligible").ToArray();
            Guid? previous = null;
            foreach (var i in eligible)
            {
                await RequestAsync(i, 60, previous, cancellation);
                if (statuses[i] == "Queued")
                    previous = members[i].Id;
            }
        }
        return new { Version = version, NeedsOverride = needsOverride,
            Peers = members.Select((peer, i) => new { peer.Name, State = statuses[i] }).ToArray() };
    }
}
