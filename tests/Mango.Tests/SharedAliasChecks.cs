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
        const string largeId = "fixture-vision";
        const string imageId = "fixture-draw";
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(18) };
        var calls = new ConcurrentDictionary<string, TaskCompletionSource<string>>(StringComparer.Ordinal);
        var holds = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
        await using var smallProvider = await StartAliasProviderAsync(smallId, 2048, false, imageId, calls, holds);
        await using var largeProvider = await StartAliasProviderAsync(largeId, 8192, true, imageId, calls, holds);
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
        Check(listed.GetProperty("context_size").GetInt32() == 8192, "a shared alias advertises the largest observed context");
        Check(Served(await Chat("ordinary text")) == smallId, "text can use the smaller text-only deployment under the mixed alias");
        var picture = await Request(http, HttpMethod.Post, small.LocalAddress + "/v1/chat/completions", small.Session, new
        {
            model = alias,
            messages = new[] { new { role = "user", content = new[] { new { type = "image_url", image_url = new { url = "data:image/png;base64,AAAA" } } } } },
            max_tokens = 16
        });
        Check(Served(picture) == largeId, "image input goes only to the capable deployment");
        var longInput = new string('x', 4096);
        Check(Served(await Chat(longInput)) == largeId && await Started(longInput) == largeId,
            "the larger context handles long text without trying the smaller deployment");
        Check(Served(await Chat("provider measured overflow")) == largeId && await Started("provider measured overflow") == smallId,
            "an authoritative context refusal before output safely retries the other deployment");
        await Request(http, HttpMethod.Post, small.LocalAddress + "/v1/chat/completions", small.Session, new
        {
            model = alias, messages = new[] { new { role = "user", content = new string('x', 9000) } }, max_tokens = 16
        }, expected: 422);

        Hold("first short");
        Hold("second short");
        var first = Chat("first short");
        var firstHost = await Started("first short");
        var second = Chat("second short", large);
        var secondHost = await Started("second short");
        Check(firstHost != secondHost, "simultaneous eligible requests spread across the two hosts");
        Release("first short");
        Release("second short");
        await Task.WhenAll(first, second);

        Hold("race small ingress");
        Hold("race large ingress");
        var left = Chat("race small ingress");
        var right = Chat("race large ingress", large);
        var raced = await Task.WhenAll(Started("race small ingress"), Started("race large ingress"));
        Check(raced.Distinct().Count() == 2, "two simultaneous ingresses cannot pile onto one conflicted host while another can serve");
        Release("race small ingress");
        Release("race large ingress");
        await Task.WhenAll(left, right);

        Hold("draw blocker");
        Hold("queued first");
        var drawing = Request(http, HttpMethod.Post, large.LocalAddress + "/v1/images/generations", large.Session,
            new { model = imageId, prompt = "draw blocker", size = "64x64" });
        Check(await Started("draw blocker") == imageId, "a different ingress occupies the large host doing image work");
        var queuedFirst = Chat("queued first");
        Check(await Started("queued first") == smallId, "text uses the open host while the image endpoint is active");
        var queuedSecond = Chat("queued second");
        await Eventually(() => JsonSerializer.SerializeToElement(small.Network.SchedulingView(), Json)
            .GetProperty("waiting").GetInt32() == 1, "both text requests stay on the selected host while image work continues");
        Release("queued first");
        Check(Served(await queuedFirst) == smallId && Served(await queuedSecond) == smallId &&
            !drawing.IsCompleted, "both text requests use the open host without disturbing image generation");
        var waitingLong = Chat(longInput + " while drawing");
        await Eventually(() => JsonSerializer.SerializeToElement(large.Network.SchedulingView(), Json)
            .GetProperty("waiting").GetInt32() == 1 && !calls.ContainsKey(longInput + " while drawing"),
            "a long request waits for the larger host while it serves another operation");
        Release("draw blocker");
        await drawing;
        Check(Served(await waitingLong) == largeId, "after image work finishes, the pending long request runs on the only fitting host");
        Check(calls.Values.All(call => call.Task.IsCompletedSuccessfully), "all provider calls settle");

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
        ConcurrentDictionary<string, TaskCompletionSource<string>> calls, ConcurrentDictionary<string, TaskCompletionSource> holds)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        object Row(string id, string type, string[] modalities) => new
        {
            key = id, type, input_modalities = modalities, media_tokens_per_item = vision ? 512 : (int?)null,
            loaded_instances = new[] { new { id, config = new { context_length = context, parallel = 1 } } }
        };
        app.MapGet("/api/v1/models", () => new { models = vision
            ? new[] { Row(native, "llm", ["text", "image"]), Row(image, "image", ["text"]) }
            : new[] { Row(native, "llm", ["text"]) } });
        app.MapPost("/v1/chat/completions", async (HttpContext web) =>
        {
            using var body = await JsonDocument.ParseAsync(web.Request.Body, cancellationToken: web.RequestAborted);
            Check(body.RootElement.GetProperty("model").GetString() == native, "shared alias rewrites to the serving model's ID");
            var content = body.RootElement.GetProperty("messages")[0].GetProperty("content");
            var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : "vision";
            Check(vision || content.ValueKind == JsonValueKind.String, "text-only fixture never receives an image");
            await Record(text, native, web.RequestAborted);
            if (text.Length > context || !vision && text == "provider measured overflow")
                return Results.BadRequest(new { error = new { code = "context_length_exceeded" } });
            return Results.Json(new { id = native });
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
