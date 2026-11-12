using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;

internal static partial class PeerChecks
{
    private const string Password = "Mango test master password";
    private const string Recovered = "Mango recovered master password";
    private const string Reset = "Mango reset master password";
    private static readonly JsonSerializerOptions Json = PeerNetwork.Json;

    public static async Task RunAsync(string root)
    {
        TestMerge();
        await TestUpdateAsync(root);
        await TestLogStoreAsync(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var status = await Tailnet.ReadAsync(timeout.Token);
        if (status.LocalAddresses.FirstOrDefault() is not { } address)
        {
            Console.WriteLine("Peer record checks passed. The live Windows Tailscale check requires a connected client.");
            return;
        }

        var output = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            await TestTailnetAsync(root, status, address);
            await TestPhaseSixAsync(root, status, address);
            await TestPhaseSevenAsync(root, status, address);
            await TestPhaseEightAsync(root, status, address);
        }
        finally { Console.SetOut(output); }
        Console.WriteLine("Peer checks passed on the live Windows Tailscale interface with two Mango instances.");
        Console.WriteLine("Shared host intent, local dashboard and TLS public ingress checks passed.");
        Console.WriteLine("Log capture, partial collection, retention and offline deletion checks passed.");
        Console.WriteLine("Release window and host-local maintenance checks passed.");
    }

    private static async Task TestTailnetAsync(string root, TailnetStatus status, IPAddress address)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        var firstPort = Port(address);
        var secondPort = Port(address);
        while (secondPort == firstPort)
            secondPort = Port(address);
        var firstPath = Path.Combine(root, "peer-first");
        var secondPath = Path.Combine(root, "peer-second");
        var stoppedStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = await StartProviderAsync(stoppedStream);
        var first = await Host.StartAsync(firstPath, status, firstPort);
        Host? second = null;
        try
        {
            var created = await Local(http, first, "network/create", new
            {
                name = "First", password = Password, benchmarkPolicy = BenchmarkPolicy.WhenConvenient
            });
            var network = created.GetProperty("identity").GetProperty("id").GetGuid();
            var recovery = created.GetProperty("recoveryRecord").GetString()!;
            var firstAdmin = await Unlock(http, first, Password);
            var issued = await Issue(http, first, firstAdmin, "fixture-chat");
            var key = issued.GetProperty("secret").GetString()!;
            var keyId = issued.GetProperty("key").GetProperty("id").GetGuid();
            await Request(http, HttpMethod.Get, first.TailAddress + "/peer/info");
            await Request(http, HttpMethod.Get, first.TailAddress + "/api/local/network", key, expected: 404);
            await Request(http, HttpMethod.Get, first.LocalAddress + "/api/local/network", key, expected: 401);
            await Request(http, HttpMethod.Get, first.TailAddress + "/v1/models", key);
            await Request(http, HttpMethod.Get, first.LocalAddress + "/v1/models", key);
            var wrongSecret = key[..^1] + (key[^1] == 'A' ? "B" : "A");
            await Request(http, HttpMethod.Get, first.LocalAddress + "/v1/models", wrongSecret, expected: 401);
            await Request(http, HttpMethod.Post, first.LocalAddress + "/v1/embeddings", key, expected: 403);

            second = await Host.StartAsync(secondPath, status, secondPort);
            var discovered = await Local(http, second, "network/discover", new { address = first.TailAddress });
            Check(discovered.GetArrayLength() == 1 &&
                discovered[0].GetProperty("peer").GetProperty("identity").GetProperty("id").GetGuid() == network,
                "discovery identifies the selected network");
            var join = new JoinNetworkRequest("Second", first.TailAddress, network, Password, BenchmarkPolicy.Now);
            await Local(http, second, "network/join", join with { Network = Guid.NewGuid() }, expected: 409);
            await Local(http, second, "network/join", join with { Password = "incorrect password" }, expected: 401);
            var joined = await Local(http, second, "network/join", join);
            var secondId = joined.GetProperty("node").GetGuid();
            Check((await Local(http, first, "settings")).GetProperty("settings").GetProperty("benchmarkPolicy").GetString() == "whenConvenient" &&
                (await Local(http, second, "settings")).GetProperty("settings").GetProperty("benchmarkPolicy").GetString() == "now",
                "create and join persist each host's own benchmark choice without replication");
            await Request(http, HttpMethod.Get, second.LocalAddress + "/v1/models", key);

            var registration = new { profile = "llamaSingle", mode = "attached", address = provider.Urls.Single() };
            await Local(http, first, "providers", registration, expected: 403);
            await Local(http, first, "providers", registration, firstAdmin);
            var catalog = await Local(http, first, "providers");
            var model = catalog.GetProperty("models")[0];
            var deployment = model.GetProperty("id").GetString()!;
            var edit = new { id = deployment, alias = "shared-fixture", ratings = new { reasoning = 4 }, revision = 0 };
            await Local(http, first, "providers/models", edit, expected: 403);
            await Local(http, first, "providers/models", edit, firstAdmin, expected: 204);
            await Eventually(() => second.Network.Snapshot.Records.Models.Any(entry => entry.Alias == "shared-fixture"),
                "peer receives independently revised catalog alias");
            await Local(http, first, "providers/selection", new { id = deployment }, expected: 204);
            var chat = new { model = "shared-fixture", messages = new[] { new { role = "user", content = "hello" } } };
            var response = await Request(http, HttpMethod.Post, first.TailAddress + "/v1/chat/completions", key, chat);
            Check(response.GetProperty("id").GetString() == "fixture-response", "inference credential reaches the selected provider");
            await Request(http, HttpMethod.Post, first.LocalAddress + "/v1/chat/completions", key,
                new
                {
                    model = "shared-fixture",
                    messages = new[] { new { role = "user", content = new[] { new { type = "image_url", image_url = new { url = "data:image/png;base64,AAAA" } } } } }
                }, expected: 403);

            var secondAdmin = await Unlock(http, second, Password);
            var activeKey = await Issue(http, first, firstAdmin, "active-revocation");
            var activeSecret = activeKey.GetProperty("secret").GetString()!;
            var activeId = activeKey.GetProperty("key").GetProperty("id").GetGuid();
            using (var stream = new HttpRequestMessage(HttpMethod.Post, first.TailAddress + "/v1/chat/completions"))
            {
                stream.Headers.Authorization = new AuthenticationHeaderValue("Bearer", activeSecret);
                stream.Content = JsonContent.Create(new { chat.model, chat.messages, stream = true }, options: Json);
                using var started = await http.SendAsync(stream, HttpCompletionOption.ResponseHeadersRead);
                Check(started.IsSuccessStatusCode, "key starts an active provider stream");
                await Local(http, second, $"network/keys/{activeId}", admin: secondAdmin, method: HttpMethod.Delete);
                await stoppedStream.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Request(http, HttpMethod.Get, first.LocalAddress + "/v1/models", activeSecret, expected: 401);
            }
            await TestReroutesAsync(http, first, second, firstAdmin, secondAdmin, key);
            await Local(http, first, "network/recover", new { recoveryRecord = recovery, newPassword = Recovered });
            await Issue(http, second, secondAdmin, "old-admin-refused", expected: 403);
            await Request(http, HttpMethod.Get, second.LocalAddress + "/v1/models", key);
            secondAdmin = await Unlock(http, second, Recovered);

            await first.DisposeAsync();
            await Local(http, second, "network/refresh", new { }, expected: 204);
            await Request(http, HttpMethod.Get, second.LocalAddress + "/v1/models", key);
            await Issue(http, second, secondAdmin, "isolated-refused", expected: 409);
            await Local(http, second, $"network/keys/{keyId}", admin: secondAdmin, method: HttpMethod.Delete, expected: 409);

            first = await Host.StartAsync(firstPath, status, firstPort);
            firstAdmin = await Unlock(http, first, Recovered);
            var issuedWithPeer = await Issue(http, second, secondAdmin, "one-peer-online");
            Check(issuedWithPeer.GetProperty("delivery").GetProperty("confirmed").GetInt32() == 1, "one peer confirms issuance");

            var unknownId = Guid.NewGuid();
            var unknownSecret = PeerCredentials.Secret();
            var state = second.Network.Snapshot;
            await second.Network.MergeAsync(NetworkRecords.Empty with
            {
                Identity = state.Records.Identity,
                Keys = [new(unknownId, "lookup-only", [InferenceScope.Chat], KeyPriority.High, null, secondId,
                    state.Next(secondId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), PeerCredentials.Digest(unknownSecret))]
            }, default);
            Check(!first.Network.Snapshot.Keys.ContainsKey(unknownId), "test key begins unknown locally");
            await Request(http, HttpMethod.Get, first.LocalAddress + "/v1/models", PeerCredentials.Key(unknownId, unknownSecret));
            Check(first.Network.Snapshot.Keys.ContainsKey(unknownId), "unknown key fetches and caches authenticated verifier");

            var diskPath = Path.Combine(secondPath, "network.json");
            var stale = await File.ReadAllBytesAsync(diskPath);
            await Local(http, first, $"network/keys/{keyId}", admin: firstAdmin, method: HttpMethod.Delete);
            await Request(http, HttpMethod.Get, second.LocalAddress + "/v1/models", key, expected: 401);
            await second.DisposeAsync();
            await File.WriteAllBytesAsync(diskPath, stale);
            second = await Host.StartAsync(secondPath, status, secondPort);
            Check(second.Network.Snapshot.Self?.Id == secondId, "restart retains enrolled ID without a password");
            await Request(http, HttpMethod.Get, second.LocalAddress + "/v1/models", key, expected: 401);

            var preResetKey = issuedWithPeer.GetProperty("secret").GetString()!;
            await second.DisposeAsync();
            second = null;
            var reset = await Local(http, first, "network/reset", new { confirm = true, newPassword = Reset }, firstAdmin);
            Check(reset.GetProperty("delivery").GetProperty("pending").GetInt32() == 1, "offline reset delivery reported pending");
            Check(first.Network.Snapshot.Records.Models.Any(entry => entry.Alias == "shared-fixture"), "full reset preserves aliases");
            await Request(http, HttpMethod.Get, first.TailAddress + "/v1/models", preResetKey, expected: 401);
            second = await Host.StartAsync(secondPath, status, secondPort);
            Check(second.Network.Snapshot.Self is null && second.Network.Snapshot.Keys.Count == 0,
                "offline peer receives reset notice under retired credentials before serving");
            await Request(http, HttpMethod.Get, second.LocalAddress + "/v1/models", preResetKey, expected: 401);
            await Local(http, second, "network/join", join with { Password = Recovered }, expected: 401);
            var reenrolled = await Local(http, second, "network/join", join with { Password = Reset });
            Check(reenrolled.GetProperty("node").GetGuid() == secondId, "reenrollment preserves the host's reusable model identity");
            var view = await Local(http, first, "network");
            var saved = await File.ReadAllTextAsync(Path.Combine(firstPath, "network.json"));
            Check(!view.ToString().Contains("verifier", StringComparison.OrdinalIgnoreCase) &&
                !view.ToString().Contains("protectedSecret", StringComparison.OrdinalIgnoreCase) &&
                !saved.Contains(Reset) && !saved.Contains(preResetKey), "administration view and disk omit reusable plaintext credentials");
        }
        finally
        {
            if (second is not null)
                await second.DisposeAsync();
            await first.DisposeAsync();
        }
    }

    private static void TestMerge()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var b = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var generation = new Revision(1, a);
        var identity = new NetworkIdentity(Guid.NewGuid(), generation, 1);
        var first = new InferenceKey(Guid.NewGuid(), "one", [InferenceScope.Chat], KeyPriority.Normal, null, a,
            new(12, a), PeerCredentials.Digest(PeerCredentials.Secret()));
        var other = first with { Id = Guid.NewGuid(), Name = "two", CreatedBy = b, Revision = new(12, b) };
        var stale = NetworkRecords.Empty with { Identity = identity, Keys = [first] };
        var next = stale with { Keys = [other, first with { Revoked = true, Revision = new(11, b) }] };
        var merged = stale.Merge(next);
        Check(merged.Keys.Length == 2 && merged.Keys.Single(key => key.Id == first.Id).Revoked, "independent key updates and revocation");
        Check(ReferenceEquals(merged, merged.Merge(stale).Merge(next)) &&
            next.Merge(stale).Versions().SequenceEqual(merged.Versions()), "duplicates and reversed delivery converge");
        Check(new Revision(12, b).CompareTo(new(12, a)) > 0, "equal-time writer ordering");
        var modelId = PeerValidation.ModelId(a, "fixture");
        var model = new SharedModel(modelId, a, "fixture", new(15, a), "original", []);
        var active = merged with { Models = [model] };
        var deleted = active with { Models = [model with { Revision = new(15, b), Removed = true }] };
        Check(deleted.Merge(active).Models[0].Removed, "stale model state cannot undo deletion");
        var disk = new PeerSnapshot(new(deleted, null), null);
        Check(disk.Next(a, 5).Time == 16 && deleted.Delta(disk.Versions).IsEmptyDelta &&
            deleted.Delta(active.Versions()).Models.Length == 1, "clock skew and targeted revision delta");
        Check(!Tailnet.Contains(IPAddress.Loopback) && Tailnet.Address("100.64.0.1:4321")?.Port == 4321 &&
            Tailnet.Address("https://100.64.0.1") is null && Tailnet.Address("127.0.0.1") is null,
            "peer addresses are literal Tailscale origins");
    }

    private static async Task<string> Unlock(HttpClient http, Host host, string password) =>
        (await Local(http, host, "network/unlock", new { password })).GetProperty("token").GetString()!;

    private static Task<JsonElement> Issue(HttpClient http, Host host, string admin, string name, int expected = 200) =>
        Local(http, host, "network/keys", new { name, priority = "normal", scopes = new[] { "chat" }, neverExpires = true },
            admin, HttpMethod.Post, expected);

    private static Task<JsonElement> Local(HttpClient http, Host host, string route, object? body = null,
        string? admin = null, HttpMethod? method = null, int expected = 200) =>
        Request(http, method ?? (body is null ? HttpMethod.Get : HttpMethod.Put), host.LocalAddress + "/api/local/" + route,
            host.Session, body, admin, expected);

    private static async Task<JsonElement> Request(HttpClient http, HttpMethod method, string url,
        string? bearer = null, object? body = null, string? admin = null, int expected = 200)
    {
        using var request = new HttpRequestMessage(method, url);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (admin is not null)
            request.Headers.Add(PeerNetwork.AdminSessionHeader, admin);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request);
        if ((int)response.StatusCode != expected)
        {
            var detail = response.Content.Headers.ContentType?.MediaType == "application/json" ?
                await response.Content.ReadFromJsonAsync<JsonElement>(Json) : default;
            var error = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("error", out var value) ? value.ToString() : "";
            throw new InvalidOperationException($"Peer check failed: {method} {new Uri(url).AbsolutePath} expected HTTP {expected}, " +
                $"got {(int)response.StatusCode}. {error}");
        }
        return response.Content.Headers.ContentType?.MediaType == "application/json"
            ? await response.Content.ReadFromJsonAsync<JsonElement>(Json) : default;
    }

    private static async Task Eventually(Func<bool> condition, string description)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            try { await Task.Delay(50, stop.Token); }
            catch (OperationCanceledException) { throw new InvalidOperationException($"Peer check failed: {description}"); }
        }
    }

    private static int Port(IPAddress address)
    {
        var socket = new TcpListener(address, 0);
        socket.Start();
        try { return ((IPEndPoint)socket.LocalEndpoint).Port; }
        finally { socket.Stop(); }
    }

    private static async Task<WebApplication> StartProviderAsync(TaskCompletionSource stoppedStream, string? providerToken = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/props", () => new { default_generation_settings = new { n_ctx = 2048 }, build_info = "fixture" });
        app.MapGet("/v1/models", () => new { data = new[] { new { id = "fixture" } } });
        var uncertain = 0;
        app.MapGet("/slots", () => new[] { new { is_processing = Volatile.Read(ref uncertain) != 0 } });
        app.MapPost("/v1/chat/completions/input_tokens", () => new { input_tokens = 12 });
        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            Check(providerToken is null ? context.Request.Headers.Authorization.Count == 0 :
                context.Request.Headers.Authorization.ToString() == $"Bearer {providerToken}", "Mango credentials stay outside provider requests");
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            if (prompt == "fail before first output")
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { error = "fixture refusal" }, context.RequestAborted);
                return;
            }
            if (prompt == "unconfirmed stop")
            {
                Volatile.Write(ref uncertain, 1);
                context.Abort();
                return;
            }
            if (!body.RootElement.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
            {
                await context.Response.WriteAsJsonAsync(new { id = "fixture-response" }, context.RequestAborted);
                return;
            }
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"id\":\"active-fixture\"}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            if (prompt == "fail after first output")
            {
                await Task.Delay(150, context.RequestAborted);
                context.Abort();
                return;
            }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                stoppedStream.TrySetResult();
            }
        });
        await app.StartAsync();
        return app;
    }

    private static async Task TestReroutesAsync(HttpClient http, Host serving, Host ingress, string firstAdmin, string secondAdmin, string key)
    {
        var settings = (await Local(http, serving, "settings")).GetProperty("settings").Deserialize<LocalSettings>(Json)!;
        await Local(http, serving, "settings", new { settings = settings with { Role = LocalRole.Contributor } }, expected: 204);
        await Local(http, serving, "network/queue", new { waitSeconds = 15 }, expected: 403);
        await Local(http, serving, "network/queue", new { waitSeconds = 15 }, firstAdmin, expected: 204);
        await Eventually(() => ingress.Network.QueueWaitSeconds == 15, "master queue setting replicates");
        await Eventually(() => ingress.Network.Snapshot.Records.Catalogs.Any(catalog => catalog.Role == LocalRole.Contributor &&
            catalog.Models.Any(model => model.LocallyAvailable == true)), "remote fixture advertises contribution");
        await Eventually(() => serving.Network.Snapshot.Digest == ingress.Network.Snapshot.Digest, "routing fixtures reach the same digest");
        var before = await Local(http, ingress, "network/scheduling");
        foreach (var session in new[] { "before", "after", "ambiguous" })
        {
            using var request = Chat(session == "before" ? new string('x', 3000) : "seed a related route", session);
            using var response = await http.SendAsync(request);
            Check(response.IsSuccessStatusCode, "one authenticated remote attempt succeeds, including provider-counted context fit");
        }
        var healthy = await Local(http, ingress, "network/scheduling");
        Check(healthy.GetProperty("submissions").GetInt32() == before.GetProperty("submissions").GetInt32() + 3 &&
            healthy.GetProperty("stopQueries").GetInt32() == before.GetProperty("stopQueries").GetInt32() &&
            healthy.GetProperty("digestMerges").GetInt32() == before.GetProperty("digestMerges").GetInt32(),
            "matching digest uses one submission and no status or metadata fetch");

        var from = ingress.Network.Snapshot;
        var serve = serving.Network.Snapshot;
        var epoch = serve.Records.Catalogs.Single(item => item.Node == serve.Self!.Id).SchedulingEpoch;
        var controlId = Guid.NewGuid();
        using (var request = Control(Guid.NewGuid().ToString("N")))
        using (var response = await http.SendAsync(request))
        {
            var state = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            Check(state.GetProperty("state").GetString() == "uncertain", "a new serving process cannot fence a predecessor's attempt");
        }
        using (var request = Control(epoch!))
        using (var response = await http.SendAsync(request))
        {
            var state = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            Check(state.GetProperty("state").GetString() == "rejected", "the original serving process can fence an undelivered submission");
        }

        var fallbackCalls = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var fallback = builder.Build();
        fallback.MapGet("/props", () => new { default_generation_settings = new { n_ctx = 2048 }, build_info = "fallback" });
        fallback.MapGet("/v1/models", () => new { data = new[] { new { id = "fallback" } } });
        fallback.MapPost("/v1/chat/completions/input_tokens", () => new { input_tokens = 12 });
        fallback.MapPost("/v1/chat/completions", () =>
        {
            Interlocked.Increment(ref fallbackCalls);
            return Results.Json(new { id = "fallback-response" });
        });
        await fallback.StartAsync();
        await Local(http, ingress, "providers", new { profile = "llamaSingle", mode = "attached", address = fallback.Urls.Single() }, secondAdmin);
        var deployment = (await Local(http, ingress, "providers")).GetProperty("models")[0].GetProperty("id").GetString();
        await Local(http, ingress, "providers/models", new { id = deployment, alias = "shared-fixture", revision = 0 }, secondAdmin, expected: 204);

        using (var request = Chat("fail before first output", "before"))
        using (var response = await http.SendAsync(request))
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
            Check(response.IsSuccessStatusCode && body.GetProperty("id").GetString() == "fallback-response" && fallbackCalls == 1,
                "confirmed remote failure before output reroutes to one fallback");
        }
        using (var request = Chat("fail after first output", "after", stream: true))
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        using (var body = new StreamReader(await response.Content.ReadAsStreamAsync()))
        {
            Check(response.IsSuccessStatusCode && (await body.ReadLineAsync())?.Contains("active-fixture") == true,
                "stream forwards the first serving chunk");
            try { await body.ReadToEndAsync(); }
            catch (IOException) { }
            Check(fallbackCalls == 1, "a failed partial stream never starts a second response");
        }
        using (var request = Chat("unconfirmed stop", "ambiguous"))
        using (var response = await http.SendAsync(request))
            Check(response.StatusCode == HttpStatusCode.BadGateway && fallbackCalls == 1,
                "unknown provider outcome returns failure without running duplicate inference");

        await Local(http, serving, "logs/settings", LogSettings(Mango.Logging.RequestLogMode.Sparse), firstAdmin);
        var ingressId = ingress.Network.Snapshot.Self!.Id;
        var retried = (await WaitForLogEntries(ingress.Network.Logs, ingressId, entries => entries.Any(item =>
            item.Kind == Mango.Logging.LogKind.Request && item.Routes is { Length: 2 })))
            .Single(item => item.Kind == Mango.Logging.LogKind.Request && item.Routes is { Length: 2 });
        await CollectLogDay(http, serving);
        await CollectLogDay(http, serving);
        var retryExport = await ReadLogZip(http, serving);
        var related = System.Text.Encoding.UTF8.GetString(retryExport.Entries["records.ndjson"]).Split('\n',
                StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<Mango.Logging.LogEntry>(line, Json))
            .Where(item => item?.Request == retried.Request).ToArray();
        Check(related.Count(item => item?.Kind == Mango.Logging.LogKind.Request) == 1 &&
            related.Count(item => item?.Kind == Mango.Logging.LogKind.Attempt) == 2,
            "a live reroute counts one original request and two attempts after repeated collection");

        await fallback.StopAsync();

        HttpRequestMessage Chat(string text, string session, bool stream = false)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ingress.TailAddress + "/v1/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Add("Mango-Session", session);
            request.Content = JsonContent.Create(new
            {
                model = "shared-fixture", messages = new[] { new { role = "user", content = text } }, stream
            }, options: Json);
            return request;
        }

        HttpRequestMessage Control(string servingEpoch)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, serving.TailAddress + "/peer/inference/control");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", from.Token);
            request.Headers.Add(PeerNetwork.NodeHeader, from.Self!.Id.ToString("N"));
            request.Headers.Add(PeerNetwork.NetworkHeader, from.Records.Identity!.Id.ToString("N"));
            request.Headers.Add(PeerNetwork.DigestHeader, from.Digest);
            request.Headers.Add(PeerNetwork.ReleaseHeader, MangoRelease.Current.Wire);
            request.Content = JsonContent.Create(new { attempt = controlId, fenceMilliseconds = 15000, servingEpoch }, options: Json);
            return request;
        }
    }

    private static void Check(bool value, string description)
    {
        if (!value)
            throw new InvalidOperationException($"Peer check failed: {description}");
    }

    private sealed class Host(WebApplication app, ProviderCatalog providers, PeerNetwork network, PublicIngress publicIngress, Task worker,
        string localAddress, string tailAddress, string session) : IAsyncDisposable
    {
        private bool closed;
        public PeerNetwork Network { get; } = network;
        public string LocalAddress { get; } = localAddress;
        public string TailAddress { get; } = tailAddress;
        public string Session { get; } = session;

        public static async Task<Host> StartAsync(string directory, TailnetStatus tailnet, int port, MangoRelease? release = null)
        {
            Directory.CreateDirectory(directory);
            var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
            await settings.LoadAsync(new LocalSettings());
            var providers = new ProviderCatalog(directory);
            await providers.InitializeAsync();
            var peers = new PeerNetwork(directory, port, providers, settings, _ => Task.FromResult(tailnet), build: release);
            await peers.InitializeAsync();
            var ingress = new PublicIngress(directory, peers);
            await ingress.InitializeAsync();
            var local = new LocalWeb();
            var app = local.Build(0, directory, settings, new LoginStartup(directory), providers, peers, ingress);
            await app.StartAsync();
            var worker = Task.WhenAll(peers.RunAsync(app.Lifetime.ApplicationStopping), providers.RunAsync(app.Lifetime.ApplicationStopping));
            var address = app.Urls.Single();
            var session = new Uri(local.BrowserUrl(address)).Fragment["#session=".Length..];
            var host = new Host(app, providers, peers, ingress, worker, address, Tailnet.Origin(tailnet.LocalAddresses[0], port), session);
            try
            {
                await Eventually(() => peers.Ready || worker.IsFaulted, "peer startup reconciliation");
                if (worker.IsFaulted)
                    await worker;
                return host;
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (closed)
                return;
            closed = true;
            app.Lifetime.StopApplication();
            try { await worker; }
            finally
            {
                await publicIngress.DisposeAsync();
                await Network.DisposeAsync();
                await app.StopAsync();
                await app.DisposeAsync();
                await providers.DisposeAsync();
            }
        }
    }
}
