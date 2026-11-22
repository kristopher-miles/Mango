using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Mango.Host;
using Mango.Peers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static partial class PeerChecks
{
    internal static async Task RunSharedAliasAsync(string root)
    {
        var status = await Tailnet.ReadAsync(CancellationToken.None);
        await TestSharedAliasAsync(root, status, status.LocalAddresses.First());
        Console.WriteLine("Shared alias routing checks passed.");
    }

    private static async Task TestSharedAliasAsync(string root, TailnetStatus status, IPAddress address)
    {
        const string alias = "pooled-chat";
        const string smallId = "fixture-text";
        var legacyInstance = "mango-" + new string('7', 64);
        const string largeId = "fixture-vision";
        var runtimeId = Guid.NewGuid().ToString("N");
        const string imageId = "fixture-draw";
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(18) };
        var calls = new ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        var holds = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        await using var smallProvider = await StartAliasProviderAsync(smallId, 200_000, false, imageId, calls, holds, legacyInstance);
        await using var largeProvider = await StartAliasProviderAsync(largeId, 262_144, true, imageId, calls, holds, runtimeId);
        await using var small = await Host.StartAsync(Path.Combine(root, "alias-small"), status, Port(address));
        await using var large = await Host.StartAsync(Path.Combine(root, "alias-large"), status, Port(address));
        var created = await Local(http, small, "network/create", new { name = "Alias small", password = Password, benchmarkPolicy = BenchmarkPolicy.Never });
        await Local(http, large, "network/join", new JoinNetworkRequest("Alias large", small.TailAddress,
            created.GetProperty("identity").GetProperty("id").GetGuid(), Password, BenchmarkPolicy.Never));
        await Register(small, smallProvider, smallId);
        await Register(large, largeProvider, largeId);
        await Eventually(() => new[] { small, large }.All(host => host.Network.Snapshot.Records.Catalogs.Count(catalog =>
            catalog.Role == LocalRole.Contributor && catalog.Models.Any(model => model.EffectiveAlias == alias && model.LocallyAvailable == true)) == 2),
            "both hosts receive both applied alias entries");

        Task<JsonElement> Chat(string prompt, Host? ingress = null) => Request(http, HttpMethod.Post,
            (ingress ?? small).LocalAddress + "/v1/chat/completions", (ingress ?? small).Session,
            new { model = alias, messages = new[] { new { role = "user", content = prompt } }, max_tokens = 16 });
        Task<string> Started(string prompt) => calls.GetOrAdd(prompt, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(TimeSpan.FromSeconds(7));
        void Hold(string prompt) => holds[prompt] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Release(string prompt) => holds[prompt].TrySetResult();
        static string Served(JsonElement response) => response.GetProperty("id").GetString()!;

        var catalogue = await Request(http, HttpMethod.Get, small.LocalAddress + "/v1/models", small.Session);
        var listed = catalogue.GetProperty("data").EnumerateArray().Single(item => item.GetProperty("id").GetString() == alias);
        Check(listed.GetProperty("context_size").GetInt32() == 262_144, "a shared alias advertises the largest observed context");
        Check(!catalogue.GetProperty("data").EnumerateArray().Any(item => new[] { legacyInstance, runtimeId }.Contains(item.GetProperty("id").GetString())),
            "the model list uses stable names for both legacy Mango and native UUID instances");
        var ordinary = await Chat("ordinary text", large);
        Check(Served(ordinary) == smallId && ordinary.GetProperty("model").GetString() == alias,
            "a forwarded legacy instance returns only the requested public alias");
        await Request(http, HttpMethod.Post, large.LocalAddress + "/v1/chat/completions", large.Session, new
        {
            model = legacyInstance, messages = new[] { new { role = "user", content = "private identifier" } }
        }, expected: 400);
        var key = (await Issue(http, large, await Unlock(http, large, Password), "fixture-bot")).GetProperty("secret").GetString();
        var toolReply = await Request(http, HttpMethod.Post, large.TailAddress + "/v1/chat/completions", key, new
        {
            model = alias,
            messages = new[] { new { role = "system", content = "fixture tool request" },
                new { role = "user", content = "Previous turn" }, new { role = "user", content = "Choose a function" } },
            tools = new[] { "lookup", "check" }.Select(name => new { type = "function", function = new { name } }).ToArray(),
            tool_choice = "auto",
            max_tokens = 8192
        });
        Check(Served(toolReply) == smallId, "a scoped chat request with client tools can be forwarded to the capable alias host");
        var picture = await Request(http, HttpMethod.Post, small.LocalAddress + "/v1/chat/completions", small.Session, new
        {
            model = alias,
            messages = new[] { new { role = "user", content = new[] { new { type = "image_url", image_url = new { url = "data:image/png;base64,AAAA" } } } } },
            max_tokens = 16
        });
        Check(Served(picture) == largeId, "image input goes only to the capable deployment");
        var longInput = new string('x', 220_000);
        Check(Served(await Chat(longInput)) == largeId && await Started(longInput) == largeId,
            "the larger context handles long text without trying the smaller deployment");
        Check(Served(await Chat("provider measured overflow")) == largeId && await Started("provider measured overflow") == smallId,
            "an authoritative context refusal before output safely retries the other deployment");
        Check(Served(await Chat("compressible " + new string('c', 300_000))) == largeId,
            "an uncertain byte estimate above both limits still allows native tokenization");
        await Request(http, HttpMethod.Post, small.LocalAddress + "/v1/chat/completions", small.Session, new
        {
            model = alias, messages = new[] { new { role = "user", content = new string('x', 270_000) } }, max_tokens = 16
        }, expected: 422);

        var firstPrompt = "compressible " + new string('a', 220_000);
        var secondPrompt = "compressible " + new string('b', 220_000);
        Hold(firstPrompt);
        Hold(secondPrompt);
        using (var first = await OpenStream(firstPrompt, small))
        using (var firstBody = new StreamReader(await first.Content.ReadAsStreamAsync()))
        {
            Check((await firstBody.ReadLineAsync())?.Contains("generating") == true, "the first native stream has generated output");
            using var second = await OpenStream(secondPrompt, large);
            using var secondBody = new StreamReader(await second.Content.ReadAsStreamAsync());
            Check((await secondBody.ReadLineAsync())?.Contains("generating") == true &&
                await Started(firstPrompt) != await Started(secondPrompt), "staggered long streams generate on different native workers");
            Release(firstPrompt);
            Release(secondPrompt);
            Check((await firstBody.ReadToEndAsync()).Contains("[DONE]") && (await secondBody.ReadToEndAsync()).Contains("[DONE]"),
                "both overlapping streams finish");
        }

        foreach (var secondIngress in new[] { small, large })
        {
            var pair = secondIngress == small ? "one ingress" : "two ingresses";
            var leftPrompt = "compressible left " + pair + new string('a', 220_000);
            var rightPrompt = "compressible right " + pair + new string('b', 220_000);
            Hold(leftPrompt);
            Hold(rightPrompt);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<HttpResponseMessage> Race(string prompt, Host ingress)
            {
                await start.Task;
                return await OpenStream(prompt, ingress);
            }
            var left = Race(leftPrompt, small);
            var right = Race(rightPrompt, secondIngress);
            start.SetResult();
            using var leftResponse = await left;
            using var rightResponse = await right;
            using var leftBody = new StreamReader(await leftResponse.Content.ReadAsStreamAsync());
            using var rightBody = new StreamReader(await rightResponse.Content.ReadAsStreamAsync());
            try
            {
                var outputs = await Task.WhenAll(leftBody.ReadLineAsync(), rightBody.ReadLineAsync());
                Check(outputs.All(line => line?.Contains("generating") == true && line.Contains($"\"model\":\"{alias}\"")) &&
                    await Started(leftPrompt) != await Started(rightPrompt),
                    $"simultaneous requests through {pair} generate on different workers before either is released");
            }
            finally { Release(leftPrompt); Release(rightPrompt); }
            Check((await leftBody.ReadToEndAsync()).Contains("[DONE]") && (await rightBody.ReadToEndAsync()).Contains("[DONE]"),
                "both simultaneously submitted streams complete");
        }

        Hold("draw blocker");
        Hold("queued first");
        var drawing = Request(http, HttpMethod.Post, large.LocalAddress + "/v1/images/generations", large.Session,
            new { model = imageId, prompt = "draw blocker", size = "64x64" });
        Check(await Started("draw blocker") == imageId, "a different ingress occupies the large host doing image work");
        var lowKey = (await Local(http, large, "network/keys", new
        {
            name = "low-priority responder", priority = "low", scopes = new[] { "chat", "imageInput" }, neverExpires = true
        }, await Unlock(http, large, Password), HttpMethod.Post)).GetProperty("secret").GetString();
        Check(Served(await Request(http, HttpMethod.Post, large.TailAddress + "/v1/chat/completions", lowKey, new
        {
            model = alias, messages = new[] { new { role = "user", content = "low-priority independent request" } }
        })) == smallId && !drawing.IsCompleted, "a low-priority client uses the free alias host during higher-priority work");

        var beforeDiscovery = (await Local(http, large, "network/scheduling")).GetProperty("discoveryRuns").GetInt32();
        await Request(http, HttpMethod.Post, large.TailAddress + "/v1/chat/completions", lowKey, new
        {
            model = alias,
            messages = new[] { new { role = "user", content = new[] { new { type = "image_url",
                image_url = new { url = "data:image/png;base64,AAAA" } } } } }
        }, expected: 503);
        Check((await Local(http, large, "network/scheduling")).GetProperty("discoveryRuns").GetInt32() == beforeDiscovery + 1,
            "a definitive priority refusal refreshes an exhausted route even when the tried host is reachable");

        var queuedFirst = Chat("queued first");
        Check(await Started("queued first") == smallId, "text uses the open host while the image endpoint is active");
        var queuedSecond = Chat("queued second");
        await Eventually(() => JsonSerializer.SerializeToElement(small.Network.SchedulingView(), Json)
            .GetProperty("waiting").GetInt32() == 1, "both text requests stay on the selected host while image work continues");
        Release("queued first");
        Check(Served(await queuedFirst) == smallId && Served(await queuedSecond) == smallId &&
            !drawing.IsCompleted, "both text requests use the open host without disturbing image generation");
        var waitingLong = Chat(longInput + " while drawing");
        Check(await Started(longInput + " while drawing") == smallId, "an idle smaller host gets the request before a busy larger worker");
        await Eventually(() => JsonSerializer.SerializeToElement(large.Network.SchedulingView(), Json)
            .GetProperty("waiting").GetInt32() == 1,
            "after a provider context refusal the request waits for its remaining host");
        Release("draw blocker");
        await drawing;
        Check(Served(await waitingLong) == largeId, "after image work finishes, the pending long request runs on the only fitting host");
        Check(calls.Values.All(call => call.Task.IsCompletedSuccessfully), "all provider calls settle");

        async Task<HttpResponseMessage> OpenStream(string prompt, Host ingress)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ingress.LocalAddress + "/v1/chat/completions");
            request.Headers.Authorization = new("Bearer", ingress.Session);
            request.Content = JsonContent.Create(new { model = alias, stream = true, messages = new[] { new { role = "user", content = prompt } } });
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            return response;
        }

        async Task Register(Host host, WebApplication provider, string native)
        {
            var admin = await Unlock(http, host, Password);
            var settings = (await Local(http, host, "settings")).GetProperty("settings").Deserialize<LocalSettings>(Json)!;
            await Local(http, host, "settings", new { settings = settings with { Role = LocalRole.Contributor } }, expected: 204);
            await Local(http, host, "providers", new { profile = "generic", mode = "attached", address = provider.Urls.Single() }, admin);
            var menu = await Local(http, host, "providers");
            var model = menu.GetProperty("models").EnumerateArray().Single(candidate =>
                candidate.GetProperty("model").GetProperty("nativeId").GetString() == native);
            await Local(http, host, "providers/models", new { id = model.GetProperty("id").GetString(), alias, revision = 0 }, admin, expected: 204);
        }
    }

    private static async Task<WebApplication> StartAliasProviderAsync(string native, int context, bool vision, string image,
        ConcurrentDictionary<string, TaskCompletionSource<string>> calls, ConcurrentDictionary<string, TaskCompletionSource> holds,
        string? instanceId = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        object Row(string id, string type, string[] modalities) => new
        {
            key = id, type, input_modalities = modalities, media_tokens_per_item = vision ? 512 : (int?)null,
            client_tools = !vision,
            loaded_instances = new[] { new { id = id == native ? instanceId ?? id : id, config = new { context_length = context, parallel = 1 } } }
        };
        app.MapGet("/api/v1/models", () => new { models = vision
            ? new[] { Row(native, "llm", ["text", "image"]), Row(image, "image", ["text"]) }
            : new[] { Row(native, "llm", ["text"]) } });
        app.MapPost("/v1/chat/completions", async (HttpContext web) =>
        {
            using var body = await JsonDocument.ParseAsync(web.Request.Body, cancellationToken: web.RequestAborted);
            Check(body.RootElement.GetProperty("model").GetString() == (instanceId ?? native),
                "shared alias rewrites to the serving model's ID");
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : "vision";
            Check(vision || content.ValueKind == JsonValueKind.String, "text-only fixture never receives an image");
            if (text == "fixture tool request")
                Check(!vision && body.RootElement.GetProperty("tools").GetArrayLength() == 2 &&
                    body.RootElement.GetProperty("max_tokens").GetInt32() == 8192,
                    "the serving host receives both function definitions and the caller's output limit");
            if (body.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean())
            {
                web.Response.ContentType = "text/event-stream";
                var chunk = JsonSerializer.Serialize(new { model = instanceId ?? native, choices = new[] { new { delta = new { content = "generating" } } } });
                await web.Response.WriteAsync($"data: {chunk}\n\n", web.RequestAborted);
                await web.Response.Body.FlushAsync(web.RequestAborted);
                await Record(text, native, web.RequestAborted);
                await web.Response.WriteAsync("data: [DONE]\n\n", web.RequestAborted);
                return Results.Empty;
            }
            await Record(text, native, web.RequestAborted);
            var tokens = text.StartsWith("compressible ", StringComparison.Ordinal) ? text.Length / 4 : text.Length;
            if (tokens > context || !vision && text == "provider measured overflow")
                return Results.BadRequest(new { error = new { code = "context_length_exceeded" } });
            return Results.Json(new { id = native, model = instanceId ?? native });
        });
        if (vision)
            app.MapPost("/v1/images/generations", async (HttpContext web) =>
            {
                using var body = await JsonDocument.ParseAsync(web.Request.Body, cancellationToken: web.RequestAborted);
                await Record(body.RootElement.GetProperty("prompt").GetString()!, image, web.RequestAborted);
                return Results.Json(new { data = new[] { new { b64_json = "AAAA" } } });
            });
        await app.StartAsync();
        return app;

        async Task Record(string name, string provider, CancellationToken cancellation)
        {
            calls.GetOrAdd(name, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(provider);
            if (holds.TryGetValue(name, out var hold))
                await hold.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellation);
        }
    }
}
