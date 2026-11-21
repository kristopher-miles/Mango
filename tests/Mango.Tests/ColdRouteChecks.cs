using System.Net;
using System.Text;
using System.Text.Json;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Mango.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class ColdRouteChecks
{
    public static async Task RunAsync(string root)
    {
        const long GiB = 1024L * 1024 * 1024;
        var directory = Directory.CreateDirectory(Path.Combine(root, "qualified-cold-route")).FullName;
        var loaded = false;
        Func<Task>? observeStartup = null;
        Task? startupObservation = null;
        var loads = 0;
        var refuseLoad = false;
        var calls = 0;
        var retiredDispatches = 0;
        var abortNext = false;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var native = builder.Build();
        native.MapGet("/api/v1/models", () => new { models = new[] { new
        {
            key = "qualified-cold", type = "llm", size_bytes = 64 * 1024 * 1024, max_context_length = 1048576,
            loaded_instances = loaded ? new[] { new { id = "actual-instance-" + loads, config = new { context_length = 262144, parallel = 1 } } } : []
        } } });
        native.MapPost("/api/v1/models/load", async (HttpContext context) =>
        {
            if (refuseLoad)
                return Results.Conflict();
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            Check(!body.RootElement.TryGetProperty("context_length", out _), "cold load uses provider defaults");
            loads++; loaded = true;
            startupObservation = observeStartup?.Invoke();
            return Results.Ok();
        });
        native.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            if (!loaded || body.RootElement.GetProperty("model").GetString() != "actual-instance-" + loads)
                retiredDispatches++;
            Check(loaded && body.RootElement.GetProperty("model").GetString() == "actual-instance-" + loads,
                "qualified cold request reaches the actual loaded provider instance");
            calls++;
            if (abortNext)
            {
                abortNext = false;
                context.Abort();
                return Results.Empty;
            }
            return Results.Json(new { id = "cold-route-pass" });
        });
        await native.StartAsync();
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor, BenchmarkPolicy = BenchmarkPolicy.Never });
        var pressure = false;
        var failedSamples = 0;
        ResourceObservation Sample() => new(DateTimeOffset.UtcNow, 128 * GiB, pressure ? GiB : 100 * GiB,
            0, 0, [], "fixture", [], true, null);
        using var resources = new ResourceProtection(directory, settings, (_, _) => failedSamples-- > 0
                ? Sample() with { WorkerAttribution = false, ExternalCpu = null, ExternalGpu = null,
                    Limitation = "fixture startup attribution" } : Sample(),
            () => ((ulong)(128 * GiB), (ulong)(pressure ? GiB : 100 * GiB)));
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        var endpoint = await providers.ConfigureAsync(new(null, ProviderProfile.Generic, ProviderMode.Managed,
            native.Urls.Single(), null, false, null, null), default);
        resources.ApplyObservation(Sample());
        await using var network = new PeerNetwork(directory, 0, providers, settings);
        await network.InitializeAsync();
        var model = providers.View.Models.Single();
        var requestBody = Encoding.UTF8.GetBytes("""
            {"model":"qualified-cold","messages":[{"role":"user","content":"ordinary synthetic request"}],"max_tokens":8192}
            """);
        DefaultHttpContext Request()
        {
            var context = new DefaultHttpContext();
            context.Items[typeof(InferenceAccess)] = new InferenceAccess(null, null, [], KeyPriority.Low, default);
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(requestBody);
            context.Response.Body = new MemoryStream();
            return context;
        }
        async Task Reject(int status)
        {
            try { await network.RouteAsync(InferenceOperation.Chat, Request(), default); }
            catch (ProviderFailure error) when (error.Status == status) { return; }
            throw new InvalidOperationException("Expected cold route refusal " + status);
        }
        refuseLoad = true;
        try
        {
            await providers.ManageAsync(model.Id, true, default);
            throw new InvalidOperationException("Expected a busy rejection from the generic provider.");
        }
        catch (ProviderFailure error) when (error.Status == 409) { }
        Check(!providers.HasUnsettled(model.Id) && !providers.HasUnsettledLoad(model.Id),
            "an explicit generic load refusal releases the reservation for the next attempt");
        refuseLoad = false;
        var catalogue = JsonSerializer.SerializeToElement(network.ModelList(null), PeerNetwork.Json);
        var row = catalogue.GetProperty("data").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "qualified-cold");
        Check(!row.TryGetProperty("context_size", out _) && !row.TryGetProperty("max_context_length", out _),
            "qualified cold catalogue omits unknown loaded context and architectural maximum");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        failedSamples = 1;
        observeStartup = () => resources.SampleAndApplyAsync(() => providers.View, deadline.Token);
        var request = Request();
        await network.RouteAsync(InferenceOperation.Chat, request, deadline.Token);
        await startupObservation!;
        Check(failedSamples == 0 && !resources.Observation!.WorkerAttribution && resources.ServingAllowed,
            "real serving queue dispatches once despite incomplete startup attribution");
        Check(request.Response.StatusCode == 200 && loads == 1 && calls == 1 &&
            providers.View.Models.Single().Context == 262144, "saved 262144 qualification routes through ingress and serving cold admission");
        loaded = false; // Genuine provider idle expiry, without refreshing Mango.
        Check(providers.View.Models.Single().Instance?.Id == "actual-instance-1",
            "the fake provider retired its instance while Mango still caches it");
        var afterIdle = Request();
        await network.RouteAsync(InferenceOperation.Chat, afterIdle, deadline.Token);
        Check(afterIdle.Response.StatusCode == 200 && loads == 2 && calls == 2 && retiredDispatches == 0 &&
            providers.View.Models.Single().Instance?.Id == "actual-instance-2",
            "the first request after real idle refreshes, reloads and dispatches exactly once");
        abortNext = true;
        try
        {
            await network.RouteAsync(InferenceOperation.Chat, Request(), deadline.Token);
            throw new InvalidOperationException("Expected the submitted connection failure.");
        }
        catch (ProviderFailure error) when (error.Status is 502 or 503) { }
        Check(calls == 3 && loads == 2 && retiredDispatches == 0,
            "a connection lost after submission is never replayed as a stale-instance retry");
        loaded = false;
        await providers.RefreshAsync(endpoint.Id, default);
        await providers.SetServingAsync(model.Id, false, default);
        await Reject(503);
        await providers.SetServingAsync(model.Id, true, default);
        pressure = true;
        resources.ApplyObservation(Sample());
        await network.RouteAsync(InferenceOperation.Chat, Request(), deadline.Token);
        Check(loads == 3 && calls == 4, "a sole cold load and inference reach the provider under tight headroom");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
