using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Mango.Resources;
using Mango.Scheduling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class SettlementChecks
{
    public static async Task RunAsync(string root)
    {
        await CheckLoadBoundariesAsync(root);
        await CheckAsync(root, unload: false);
        await CheckAsync(root, unload: true);
        Console.WriteLine("Cancellation settlement and distinct foreground admission checks passed.");
    }

    private static async Task CheckLoadBoundariesAsync(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "load-boundaries")).FullName;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var gguf = Path.Combine(directory, "removed-fixture.gguf");
        var executable = Path.Combine(directory, "llama-server.exe");
        await File.WriteAllTextAsync(gguf, "fixture inventory only");
        await File.WriteAllTextAsync(executable, "fixture never executed");

        var yoloLoaded = false;
        var yoloLoads = 0;
        var yoloUnloads = 0;
        var removeDuringUnload = false;
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var fixture = builder.Build();
        fixture.MapGet("/api/v1/models", () => new { models = new object[] {
            new { key = "yolo-fixture", type = "llm", loaded_instances = yoloLoaded
                ? new[] { new { id = "yolo-fixture", config = new { context_length = 4096 } } } : [] },
            new { key = "ordinary-fixture", type = "llm", loaded_instances = Array.Empty<object>() }
        } });
        fixture.MapPost("/api/v1/models/load", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            if (body.RootElement.GetProperty("model").GetString() == "yolo-fixture")
            {
                yoloLoaded = true;
                Interlocked.Increment(ref yoloLoads);
                return Results.Ok();
            }
            submitted.TrySetResult();
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
            return Results.Ok();
        });
        fixture.MapPost("/api/v1/models/unload", () =>
        {
            yoloLoaded = false;
            Interlocked.Increment(ref yoloUnloads);
            if (removeDuringUnload)
                File.Delete(gguf);
            return Results.Ok();
        });
        await fixture.StartAsync();

        const long gib = 1024L * 1024 * 1024;
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor, BenchmarkPolicy = BenchmarkPolicy.Never });
        ResourceObservation Sample() => new(DateTimeOffset.UtcNow, 16 * gib, 12 * gib, 0, 0, [], "fixture", [], true, null);
        using var resources = new ResourceProtection(directory, settings, (_, _) => Sample(),
            () => ((ulong)(16 * gib), (ulong)(12 * gib)));
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        var local = await providers.ConfigureAsync(new(null, ProviderProfile.LlamaSingle, ProviderMode.Managed,
            $"http://127.0.0.1:{port}", null, false, executable, gguf), default);
        var managed = await providers.ConfigureAsync(new(null, ProviderProfile.Generic, ProviderMode.Managed,
            fixture.Urls.Single(), null, false, null, null), default);
        resources.ApplyObservation(Sample());
        var removed = providers.View.Models.Single(model => model.EndpointId == local.Id);
        var yolo = providers.View.Models.Single(model => model.Model.NativeId == "yolo-fixture");
        var ordinary = providers.View.Models.Single(model => model.Model.NativeId == "ordinary-fixture");
        Check(removed.Model.Installed && local.CanLoad, "startup inventory can race a removed GGUF");
        await providers.SetYoloAsync(yolo.Id, new(true, yolo.Options.Revision), default);
        await providers.ManageAsync(yolo.Id, true, default);

        async Task RejectMissingFilesAsync()
        {
            try
            {
                await providers.ManageAsync(removed.Id, true, default, benchmark: InferenceOperation.Chat);
                throw new InvalidOperationException("Missing GGUF unexpectedly launched.");
            }
            catch (ProviderFailure error) when (error.Status == 409 && error.Code == "load_rejected") { }
            Check(!providers.HasUnsettled(removed.Id) && !resources.HasWork(local.Id),
                "a definite prelaunch rejection leaves no unsettled load or reservation");
        }

        File.Delete(gguf);
        await RejectMissingFilesAsync();
        Check(Volatile.Read(ref yoloUnloads) == 0 && providers.View.Models.Single(model => model.Id == yolo.Id).Instance is not null,
            "known missing files reject before evicting the healthy group");

        await File.WriteAllTextAsync(gguf, "fixture inventory only");
        removeDuringUnload = true;
        await RejectMissingFilesAsync();
        removeDuringUnload = false;
        Check(Volatile.Read(ref yoloUnloads) == 1, "final prelaunch check covers a file disappearing during group release");
        await providers.ManageAsync(yolo.Id, true, default);
        Check(Volatile.Read(ref yoloLoads) == 2, "the other group can reload after a definite rejection");
        await providers.ManageAsync(yolo.Id, false, default);

        using var cancel = new CancellationTokenSource();
        var load = providers.ManageAsync(ordinary.Id, true, cancel.Token);
        try { await submitted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await cancel.CancelAsync(); }
        try
        {
            await load;
            throw new InvalidOperationException("The submitted load unexpectedly settled.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        Check(providers.HasUnsettledLoad(ordinary.Id) && resources.HasWork(managed.Id),
            "an ambiguous submitted load retains its settlement fence");
        try
        {
            await providers.ManageAsync(yolo.Id, true, default);
            throw new InvalidOperationException("YOLO load bypassed an ambiguous opposite-group load.");
        }
        catch (ProviderFailure error) when (error.Status == 409 && error.Code is null) { }
        Check(Volatile.Read(ref yoloLoads) == 2, "an ambiguous submission still fences the other group");
    }

    private static async Task CheckAsync(string root, bool unload)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "settlement-" + unload)).FullName;
        var loaded = true;
        var busy = false;
        var inconclusive = false;
        object? loadsSettled = null;
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        ProviderCatalog? catalog = null;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var native = builder.Build();
        native.MapGet("/api/v1/models", () =>
        {
            if (catalog?.Unsettled.Length > 0) observations.Writer.TryWrite(busy);
            var instance = new Dictionary<string, object> { ["id"] = "fixture-instance",
                ["config"] = new { context_length = 4096, parallel = 1 } };
            if (!inconclusive) instance["busy"] = busy;
            return Results.Json(new { loads_settled = loadsSettled, models = new[] { new {
                key = "settlement-fixture", type = "llm", max_context_length = 4096,
                loaded_instances = loaded ? new[] { instance } : []
            } } });
        });
        native.MapPost("/api/v1/models/load", () => { loaded = true; return Results.Ok(); });
        native.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                busy = true;
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            Check(!busy && loaded, "no foreground dispatch before native stop confirmation");
            return Results.Json(new { id = "synthetic-success" });
        });
        await native.StartAsync();
        await using var providers = new ProviderCatalog(directory);
        catalog = providers;
        await providers.InitializeAsync();
        await providers.ConfigureAsync(new(null, ProviderProfile.Generic, ProviderMode.Managed,
            native.Urls.Single(), null, false, null, null), default);
        var model = providers.View.Models.Single();
        await using var queue = new ServingQueue(providers);
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings());
        await using var network = new PeerNetwork(directory, 0, providers, settings);
        await network.InitializeAsync();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = providers.RunAsync(lifetime.Token);
        var origin = Guid.NewGuid();
        var key = Guid.NewGuid();
        DefaultHttpContext Context()
        {
            var context = new DefaultHttpContext();
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""
                {"model":"settlement-fixture","messages":[{"role":"user","content":"synthetic"}],"max_tokens":8}
                """));
            context.Response.Body = new MemoryStream();
            context.Items[typeof(InferenceAccess)] = new InferenceAccess(key, "Synthetic key", [InferenceScope.Chat], KeyPriority.Normal, default);
            return context;
        }
        async Task<ServingQueue.Work> Work(DateTimeOffset? until = null)
        {
            var context = Context();
            var payload = await InferenceRequest.ReadAsync(InferenceOperation.Chat, context.Request, default);
            var now = DateTimeOffset.UtcNow;
            return new(origin, new(Guid.NewGuid(), Guid.NewGuid(), key, model.Id, now,
                until ?? now.AddSeconds(25), null, "fixture"), payload,
                InferenceAccess.Of(context)!, context, (_, _) => Task.FromResult<Guid?>(null),
                (_, _) => { }, (_, _) => Task.CompletedTask, () => "fixture");
        }
        try
        {
            using var disconnect = new CancellationTokenSource();
            var background = await Work();
            var first = queue.SubmitAsync(background, disconnect.Token);
            await started.Task.WaitAsync(lifetime.Token);
            await disconnect.CancelAsync();
            var foreground = await Work();
            var second = queue.SubmitAsync(foreground, lifetime.Token);
            Check((await first.WaitAsync(lifetime.Token)).State == AttemptState.Uncertain,
                "canceled submitted attempt remains uncertain while native busy");
            Check(!second.IsCompleted && calls == 1, "distinct request survives cancellation admission race");
            Check(providers.Placement(foreground.Payload.Shape, model.Id) is not null &&
                providers.TryStart(foreground.Payload, model.Id).Execute is null,
                "a readable placement never grants dispatch during uncertainty");
            Check((await queue.SubmitAsync(background, default)).State == AttemptState.Uncertain && calls == 1,
                "uncertain attempt cannot replay");
            var fresh = network.RouteAsync(InferenceOperation.Chat, Context(), lifetime.Token);
            using var cancel = new CancellationTokenSource();
            var canceled = queue.SubmitAsync(await Work(), cancel.Token);
            await cancel.CancelAsync();
            Check((await canceled.WaitAsync(lifetime.Token)).State == AttemptState.Rejected, "cancel while settling");
            var expired = queue.SubmitAsync(await Work(DateTimeOffset.UtcNow.AddMilliseconds(150)), default);
            Check((await expired.WaitAsync(lifetime.Token)).Status == 504, "deadline while native remains busy");
            await observations.Reader.ReadAsync(lifetime.Token);
            await observations.Reader.ReadAsync(lifetime.Token);
            await observations.Reader.ReadAsync(lifetime.Token);
            await observations.Reader.ReadAsync(lifetime.Token);
            Check(!second.IsCompleted && !fresh.IsCompleted && calls == 1, "busy refresh does not admit or reject waiting demand");
            inconclusive = true;
            await observations.Reader.ReadAsync(lifetime.Token);
            await observations.Reader.ReadAsync(lifetime.Token);
            Check(!second.IsCompleted && !fresh.IsCompleted && calls == 1, "inconclusive native state cannot authorize dispatch");
            inconclusive = false;
            busy = false;
            if (unload) loaded = false;
            Check((await second.WaitAsync(lifetime.Token)).State == AttemptState.Completed, "foreground resumes after verified idle or unload");
            await fresh.WaitAsync(lifetime.Token);
            Check(calls == 3, "each distinct foreground request dispatched exactly once");
            Check((await queue.StatusAsync(origin, background.Ticket.Attempt, null, default)).State == AttemptState.Stopped,
                "native evidence settles original owner without replay");
            Check((await queue.SubmitAsync(foreground, default)).Status == 409 && calls == 3, "completed attempt cannot replay");
            await providers.SetServingAsync(model.Id, false, default);
            Check((await queue.SubmitAsync(await Work(), default)).Status == 503 && calls == 3,
                "permanent operator ineligibility rejects");
            if (unload)
            {
                loaded = false;
                providers.RestoreUnsettled(model.EndpointId, model.Id, loading: true);
                await providers.RefreshAsync(model.EndpointId, lifetime.Token);
                Check(providers.HasUnsettledLoad(model.Id), "empty generic inventory alone cannot settle a restored load");
                loadsSettled = "true";
                await providers.RefreshAsync(model.EndpointId, lifetime.Token);
                Check(providers.HasUnsettledLoad(model.Id), "malformed load proof cannot settle a restored load");
                loaded = busy = true;
                loadsSettled = true;
                await providers.RefreshAsync(model.EndpointId, lifetime.Token);
                Check(providers.HasUnsettledLoad(model.Id), "load proof cannot settle a busy resident model");
                loaded = busy = false;
                await providers.RefreshAsync(model.EndpointId, lifetime.Token);
                Check(!providers.HasUnsettledLoad(model.Id), "explicit load proof and empty inventory settle a restored load");
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await run;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
