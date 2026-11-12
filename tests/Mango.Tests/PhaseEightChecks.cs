using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mango.Host;
using Mango.Peers;

internal static partial class PeerChecks
{
    private static async Task TestPhaseEightAsync(string root, TailnetStatus status, IPAddress address)
    {
        var build = MangoRelease.Current;
        var sameWindow = new MangoRelease("0.9.0", build.Window);
        var nextWindow = new MangoRelease("1.0.0", build.Window + 1);
        Check(build == MangoRelease.Parse(build.Wire) && MangoRelease.Parse("0.1.0/0") is null, "build release is well-formed");
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        var firstPort = Port(address);
        var secondPort = Port(address);
        while (secondPort == firstPort)
            secondPort = Port(address);
        await using var first = await Host.StartAsync(Path.Combine(root, "release-first"), status, firstPort);
        var secondPath = Path.Combine(root, "release-second");
        var second = await Host.StartAsync(secondPath, status, secondPort, sameWindow);
        try
        {
            var created = await Local(http, first, "network/create", new CreateNetworkRequest("Release one", Password));
            var identity = created.GetProperty("identity").GetProperty("id").GetGuid();
            await Local(http, second, "network/join", new JoinNetworkRequest("Release two", first.TailAddress, identity, Password));
            await Local(http, second, "release/deferral", new { deferred = true }, expected: 204);
            Check(!await second.Network.DrainForMaintenanceAsync(TimeSpan.FromSeconds(1), default) && !second.Network.Draining,
                "local deferral blocks a maintenance drain");
            var same = (await Local(http, first, "network")).GetProperty("nodes")[0];
            Check(same.GetProperty("compatible").GetBoolean() && same.GetProperty("release").GetProperty("version").GetString() == sameWindow.Version,
                "different app versions in one window enroll and reconcile");

            await second.DisposeAsync();
            second = await Host.StartAsync(secondPath, status, secondPort, nextWindow);
            foreach (var (from, to) in new[] { (first, second), (second, first) })
            {
                var view = (await Local(http, from, "network")).GetProperty("nodes")[0];
                Check(!view.GetProperty("compatible").GetBoolean(), "reconnect reports an incompatible window in both directions");
                await Request(http, HttpMethod.Get, to.TailAddress + "/peer/info");
                foreach (var route in new[] { "push", "inference/Chat" })
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, to.TailAddress + "/peer/" + route);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", from.Network.Snapshot.Token);
                    request.Headers.Add(PeerNetwork.NodeHeader, from.Network.Snapshot.Self!.Id.ToString("N"));
                    request.Headers.Add(PeerNetwork.NetworkHeader, identity.ToString("N"));
                    request.Headers.Add(PeerNetwork.ReleaseHeader, from.Network.Release.Wire);
                    request.Content = new StringContent("{ invalid JSON", Encoding.UTF8, "application/json");
                    using var response = await http.SendAsync(request);
                    Check(response.StatusCode == HttpStatusCode.UpgradeRequired &&
                        response.Headers.GetValues(PeerNetwork.ReleaseHeader).Single() == to.Network.Release.Wire,
                        "different windows reject control and inference before decoding the body");
                }
            }
            var deferral = await Local(http, second, "release");
            Check(deferral.GetProperty("maintenanceDeferred").GetBoolean() &&
                deferral.GetProperty("updates").GetString() == "Automatic updates are unavailable.", "deferral persists without an update service");

            await second.DisposeAsync();
            second = await Host.StartAsync(secondPath, status, secondPort, sameWindow);
            Check((await Local(http, first, "network")).GetProperty("nodes")[0].GetProperty("compatible").GetBoolean(),
                "compatible reconnect clears the cached cutoff diagnosis");
        }
        finally { await second.DisposeAsync(); }
    }
}
