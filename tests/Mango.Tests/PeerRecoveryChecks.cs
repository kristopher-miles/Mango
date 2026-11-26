using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Mango.Scheduling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

internal static partial class PeerChecks
{
    public static async Task RunRecoveryAsync(string root)
    {
        await TestListenerRecoveryAsync(Path.Combine(root, "late-tailnet"));
        await TestPeerAddressRecoveryAsync(Path.Combine(root, "changed-tailnet"));
        await TestRetiredServingEpochAsync(Path.Combine(root, "retired-serving-epoch"));
        Console.WriteLine("Offline peer address recovery checks passed.");
    }

    private static async Task TestRetiredServingEpochAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings());
        await using var providers = new ProviderCatalog(directory);
        await providers.InitializeAsync();
        await using var network = new PeerNetwork(directory, 0, providers, settings, _ => Task.FromResult(TailnetStatus.Waiting));
        await network.InitializeAsync();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var fixture = builder.Build();
        // Exercise the attempt routes after the transport and peer authentication boundary.
        network.MapAttemptRoutes(fixture.MapGroup("/peer"));
        await fixture.StartAsync();
        using var lifetime = new CancellationTokenSource();
        var worker = network.RunAsync(lifetime.Token);
        try
        {
            await Eventually(() => network.Ready, "new Mango process has completed startup");
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new(fixture.Urls.Single()) };
            var retiredEpoch = Guid.NewGuid().ToString("N");
            var attempt = Guid.NewGuid();
            using var control = await client.PostAsJsonAsync("/peer/inference/control",
                new AttemptControl(attempt, 1000, retiredEpoch), lifetime.Token);
            var result = await control.Content.ReadFromJsonAsync<AttemptReply>(Json, lifetime.Token);
            Check(result is { State: AttemptState.Uncertain, Retryable: false },
                "a restarted serving process does not authorize retry of an ambiguous old attempt");

            using var replay = new HttpRequestMessage(HttpMethod.Post, "/peer/inference/Chat")
            {
                Content = new StringContent("not an inference body")
            };
            replay.Headers.Add("Mango-Attempt", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
            {
                request = Guid.NewGuid(), attempt, key = Guid.NewGuid(), deployment = "fixture",
                ageMilliseconds = 0, remainingMilliseconds = 1000, servingEpoch = retiredEpoch
            }, Json)));
            using var response = await client.SendAsync(replay, lifetime.Token);
            var refused = await response.Content.ReadFromJsonAsync<AttemptReply>(Json, lifetime.Token);
            Check(refused is { State: AttemptState.Rejected, Status: 503, Reason: "The serving Mango process changed before submission." },
                "an old-epoch submission is refused before its body reaches the provider");
        }
        finally
        {
            await lifetime.CancelAsync();
            await worker;
        }
    }

    private static async Task TestListenerRecoveryAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings());
        await using var providers = new ProviderCatalog(directory);
        await providers.InitializeAsync();
        using var occupied = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var status = TailnetStatus.Waiting;
        await using var network = new PeerNetwork(directory, port, providers, settings,
            _ => Task.FromResult(Volatile.Read(ref status)));
        await network.InitializeAsync();
        using var lifetime = new CancellationTokenSource();
        var worker = network.RunAsync(lifetime.Token);
        try
        {
            await Eventually(() => network.Ready, "startup completes with no Tailscale addresses");
            Volatile.Write(ref status, new("local-device", [IPAddress.Loopback], [], "Connected"));
            await Eventually(() => network.ServicePortUnavailable, "late address probed without an address event");
            occupied.Stop();
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{port}/peer/info", timeout.Token);
                    Check(response.StatusCode == HttpStatusCode.Forbidden, "recovered listener still rejects a non-tailnet client");
                    break;
                }
                catch (HttpRequestException) { await Task.Delay(50, timeout.Token); }
            }
            await Eventually(() => !network.ServicePortUnavailable, "transient port error clears without manual refresh");
        }
        finally
        {
            await lifetime.CancelAsync();
            await worker;
        }
    }

    private static async Task TestPeerAddressRecoveryAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings());
        await using var providers = new ProviderCatalog(directory);
        await providers.InitializeAsync();

        var oldLocal = IPAddress.Parse("100.64.0.11");
        var newLocal = IPAddress.Parse("100.64.0.12");
        var stalePeer = IPAddress.Parse("100.64.0.21");
        var alternate = IPAddress.Parse("100.64.0.22");
        var unrelated = IPAddress.Parse("100.64.0.23");
        var candidates = new TailnetCandidate[] { new("other-device", [unrelated]), new("remote-device", [alternate]) };
        const int port = 53781;
        var status = new TailnetStatus("local-device", [oldLocal], candidates, "Connected");
        var peerId = Guid.NewGuid();
        var pingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var advertisedAddress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inferenceHosts = new List<string>();
        var controlCalls = 0;
        var binds = 0;
        var stops = 0;
        var polls = 0;
        var failAfterBody = false;
        var ambiguousHeaders = false;
        using var handler = new RecoveryHandler();
        await using var network = new PeerNetwork(directory, port, providers, settings,
            _ =>
            {
                Interlocked.Increment(ref polls);
                return Task.FromResult(Volatile.Read(ref status));
            }, startListener: async (addresses, cancellation) =>
            {
                Interlocked.Increment(ref binds);
                var listener = await BindFixtureAsync(addresses, cancellation);
                listener.Lifetime.ApplicationStopping.Register(() => Interlocked.Increment(ref stops));
                return listener;
            }, peerHandler: handler, healthyTransportPollInterval: TimeSpan.FromMilliseconds(100));
        await network.InitializeAsync();
        await network.RefreshTransportAsync(default);
        var created = await network.CreateAsync(new("Recovery fixture", Password), default);
        var peer = new PeerNode(peerId, "remote-device", "Remote fixture", Tailnet.Origin(stalePeer, port),
            created.Identity.Generation, created.Identity.Generation, PeerCredentials.Digest(PeerCredentials.Secret()));
        await network.MergeAsync(NetworkRecords.Empty with { Identity = created.Identity, Nodes = [peer] }, default);
        var original = network.Snapshot;
        var newAddress = Tailnet.Origin(newLocal, port);
        handler.Respond = SendAsync;
        using var lifetime = new CancellationTokenSource();
        var worker = network.RunAsync(lifetime.Token, refreshTransport: false);
        try
        {
            await pingEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var initialPolls = Volatile.Read(ref polls);
            await Eventually(() => Volatile.Read(ref polls) >= initialPolls + 2, "healthy listener checked periodically");
            Check(Volatile.Read(ref binds) == 1 && Volatile.Read(ref stops) == 0,
                "unchanged addresses leave the existing listener active");
            var healthy = Volatile.Read(ref status);
            var readFailure = new TailnetStatus(null, [], [], "Simulated status timeout", [oldLocal]);
            Volatile.Write(ref status, readFailure);
            initialPolls = Volatile.Read(ref polls);
            await Eventually(() => Volatile.Read(ref polls) >= initialPolls + 2, "local interface checked after failed status read");
            Check(Volatile.Read(ref stops) == 0, "status timeout leaves a still-verified listener active");
            Volatile.Write(ref status, healthy with { LocalAddresses = [newLocal] });
            await Eventually(() => network.Snapshot.Self?.Address == newAddress,
                "replacement address persisted without an event while the old listener and reconciliation were active");
            Check(Volatile.Read(ref binds) == 2, "only the replacement address triggers a rebind");
            var disk = new SnapshotFile<PeerDisk>(Path.Combine(directory, "network.json"), PeerValidation.Disk);
            await disk.LoadAsync(PeerDisk.Empty);
            Check(disk.Value.Local == original.Local && network.Snapshot.Self!.Verifier == original.Self!.Verifier &&
                disk.Value.Records.Identity == created.Identity &&
                disk.Value.Records.Nodes.Single(node => node.Id == created.Node).Address == newAddress,
                "address change preserves persisted network identity, membership and verifier");
            Check(!network.Ready, "in-flight reconciliation keeps admission gated after rebinding");
            releasePing.TrySetResult();
            await advertisedAddress.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Eventually(() => network.Ready, "peer becomes ready after transport reconciliation");

            var result = await SubmitAsync();
            Check(result.State == AttemptState.Completed &&
                inferenceHosts.SequenceEqual([stalePeer.ToString(), alternate.ToString()]) && controlCalls == 0,
                "a stale connection falls back to the same device before body transmission");

            inferenceHosts.Clear();
            failAfterBody = true;
            result = await SubmitAsync();
            Check(result.State == AttemptState.Uncertain && controlCalls > 0 &&
                inferenceHosts.SequenceEqual([stalePeer.ToString()]), "body transmission permits settlement queries only");

            inferenceHosts.Clear();
            failAfterBody = false;
            ambiguousHeaders = true;
            controlCalls = 0;
            result = await SubmitAsync();
            Check(result.State == AttemptState.Uncertain && controlCalls > 0 &&
                inferenceHosts.SequenceEqual([stalePeer.ToString()]), "an ambiguous response permits settlement queries only");

            Volatile.Write(ref status, readFailure with { InterfaceAddressesOnReadFailure = [] });
            await Eventually(() => Volatile.Read(ref stops) == 2, "status timeout with actual interface loss closes the listener");
        }
        finally
        {
            releasePing.TrySetResult();
            await lifetime.CancelAsync();
            await worker;
        }

        async Task<AttemptReply> SubmitAsync()
        {
            using var body = new MemoryStream();
            var context = new DefaultHttpContext();
            context.Response.Body = body;
            using var payload = new InferenceRequest(InferenceOperation.Embeddings, "fixture",
                JsonNode.Parse("""{"model":"fixture","input":"synthetic probe"}""")!.AsObject(), null);
            var now = DateTimeOffset.UtcNow;
            var remaining = TimeSpan.FromSeconds(10);
            var ticket = new AttemptTicket(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "fixture",
                now, now + remaining, null, Guid.NewGuid().ToString("N"));
            var result = await network.SubmitPeerAsync(peerId, ticket, TimeSpan.Zero, remaining, payload, context, lifetime.Token);
            Check(result.State != AttemptState.Completed || body.Length > 0, "successful fixture response reaches the client");
            return result;
        }

        async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var path = request.RequestUri!.AbsolutePath;
            var host = request.RequestUri.Host;
            if (path == "/peer/ping")
            {
                if (pingEntered.TrySetResult())
                    await releasePing.Task;
                return PeerResponse(HttpStatusCode.NoContent, digest: advertisedAddress.Task.IsCompleted ? network.Snapshot.Digest : original.Digest);
            }
            if (path == "/peer/versions")
                return PeerResponse(HttpStatusCode.OK, JsonContent.Create(new PeerVersions(original.Versions), options: Json));
            if (path == "/peer/pull")
                return PeerResponse(HttpStatusCode.OK,
                    JsonContent.Create(NetworkRecords.Empty with { Identity = created.Identity }, options: Json));
            if (path == "/peer/push")
            {
                var delta = await request.Content!.ReadFromJsonAsync<NetworkRecords>(Json, cancellation);
                if (delta?.Nodes.Any(node => node.Id == created.Node && node.Address == newAddress) == true)
                    advertisedAddress.TrySetResult();
                return PeerResponse(HttpStatusCode.NoContent);
            }
            if (path == "/peer/inference/control")
            {
                controlCalls++;
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Synthetic settlement outage");
            }
            if (path != "/peer/inference/Embeddings")
                return PeerResponse(HttpStatusCode.NoContent);

            var snapshot = network.Snapshot;
            Check(request.Headers.Authorization?.Parameter == snapshot.Token &&
                request.Headers.GetValues(PeerNetwork.NodeHeader).Single() == snapshot.Self!.Id.ToString("N") &&
                request.Headers.GetValues(PeerNetwork.NetworkHeader).Single() == snapshot.Records.Identity!.Id.ToString("N"),
                "alternate transport carries enrolled peer authentication");
            inferenceHosts.Add(host);
            if (host == stalePeer.ToString() && ambiguousHeaders)
                return new(HttpStatusCode.BadGateway);
            if (host == stalePeer.ToString() && !failAfterBody)
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Synthetic connect failure");
            await request.Content!.CopyToAsync(Stream.Null, cancellation);
            if (failAfterBody)
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Synthetic disconnect after writing");

            var response = PeerResponse(HttpStatusCode.OK);
            response.Content = JsonContent.Create(new { data = new[] { new { embedding = new[] { 0.1, 0.2 } } } });
            return response;
        }

        HttpResponseMessage PeerResponse(HttpStatusCode code, HttpContent? content = null, string? digest = null)
        {
            var reply = new HttpResponseMessage(code) { Content = content };
            reply.Headers.Add(PeerNetwork.NodeHeader, peerId.ToString("N"));
            reply.Headers.Add(PeerNetwork.NetworkHeader, created.Identity.Id.ToString("N"));
            reply.Headers.Add(PeerNetwork.ReleaseHeader, network.Release.Wire);
            reply.Headers.Add(PeerNetwork.DigestHeader, digest ?? network.Snapshot.Digest);
            return reply;
        }
    }

    private static async Task<WebApplication> BindFixtureAsync(IPAddress[] addresses, CancellationToken cancellation)
    {
        Check(addresses.Length == 1 && Tailnet.Contains(addresses[0]), "only the selected address is passed to the binder");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        await app.StartAsync(cancellation);
        return app;
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            (_, _) => throw new InvalidOperationException("The peer fixture has not started.");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Respond(request, cancellationToken);
    }
}
