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

internal static class YoloChecks
{
    private const long GiB = 1024L * 1024 * 1024;

    public static async Task RunAsync(string root)
    {
        // Independent host stores, different adapters and arbitrary model names.
        foreach (var profile in new[] { ProviderProfile.LmStudio, ProviderProfile.Generic })
            await HostAsync(Path.Combine(root, "yolo-" + profile), profile);
    }

    private static async Task HostAsync(string directory, ProviderProfile profile)
    {
        Directory.CreateDirectory(directory);
        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<string, bool> { ["native-model"] = false, ["ordinary-model"] = true, ["second-native"] = false };
        var loads = 0;
        var activeA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pressure = false;
        var pressureFromNative = false;
        var nativeKind = "llm";
        var nativeStatus = 200;
        var nativeBody = "{\"result\":\"native\"}";
        var nativeType = "application/json";
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var fixture = builder.Build();
        fixture.MapGet("/api/v1/models", () => new { version = "fixture", max_loaded_models = 2, models = loaded.Select(item => new
        {
            key = item.Key, type = item.Key == "native-model" ? nativeKind : "llm", max_context_length = 8192, size_bytes = 64 * 1024 * 1024,
            loaded_instances = item.Value ? new[] { new { id = item.Key + "-instance", config = new { context_length = 1024, parallel = 2 } } } : []
        }) });
        fixture.MapPost("/api/v1/models/load", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var key = body.RootElement.GetProperty("model").GetString()!;
            if (key == "native-model")
                Check(body.RootElement.EnumerateObject().Select(field => field.Name).ToHashSet().SetEquals(["model", "echo_load_config"]),
                    "YOLO lifecycle sends native defaults without context/GPU/KV overrides");
            loaded[key] = true;
            if (key == "native-model") loads++;
            return Results.Json(new { status = "loaded" });
        });
        fixture.MapPost("/api/v1/models/unload", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var id = body.RootElement.GetProperty("instance_id").GetString()!;
            loaded[id.Replace("-instance", "")] = false;
            if (id == "native-model-instance" && pressureFromNative) pressure = false;
            return Results.Json(new { status = "unloaded" });
        });
        fixture.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            if (body.RootElement.TryGetProperty("max_tokens", out var output) && output.GetInt32() == BenchmarkProtocol.SpeedOutput)
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: {\"timings\":{\"predicted_per_second\":25}}\n\ndata: [DONE]\n\n");
                return;
            }
            if (body.RootElement.TryGetProperty("hold", out var gate) && gate.ValueKind == JsonValueKind.String)
            {
                var first = gate.GetString() == "A";
                (first ? activeA : activeB).TrySetResult();
                await (first ? releaseA : releaseB).Task.WaitAsync(context.RequestAborted);
                await context.Response.WriteAsJsonAsync(new { served = gate.GetString() });
                return;
            }
            if (body.RootElement.GetProperty("model").GetString() == "ordinary-model-instance")
            {
                await context.Response.WriteAsJsonAsync(new { result = "protected" });
                return;
            }
            Check(body.RootElement.GetProperty("model").GetString() == "native-model-instance" &&
                body.RootElement.GetProperty("unknown_provider_field").GetProperty("nested").GetInt32() == 7 &&
                !body.RootElement.TryGetProperty("max_tokens", out _), "YOLO preserves provider body and omitted output limit");
            context.Response.StatusCode = nativeStatus;
            context.Response.ContentType = nativeType;
            await context.Response.WriteAsync(nativeBody);
        });
        fixture.MapPost("/v1/embeddings", () => new { data = new[] { new { embedding = new[] { 0.1, 0.2 } } }, usage = new { prompt_tokens = 8 } });
        fixture.MapPost("/v1/systemone", () => new { answers = new { benchmark = 0.8 } });
        await fixture.StartAsync();
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor, BenchmarkPolicy = BenchmarkPolicy.Now });
        ResourceObservation Sample() => new(DateTimeOffset.UtcNow, 16 * GiB, 12 * GiB,
            0, 0, [new("gpu", "fixture", false, 8 * GiB, pressure ? 8 * GiB : GiB)], "fixture", [], true, null);
        using var resources = new ResourceProtection(directory, settings, (_, _) => Sample(),
            () => ((ulong)(16 * GiB), (ulong)(12 * GiB)));
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        var endpoint = await providers.ConfigureAsync(new(null, profile, ProviderMode.Managed,
            fixture.Urls.Single(), null, false, null, null), default);
        resources.ApplyObservation(Sample());
        Deployment Model() => providers.View.Models.Single(model => model.Model.NativeId == "native-model");
        var model = Model();
        Check(!model.Yolo && providers.View.Models.All(item => !item.Yolo), "YOLO is off by default on every host/provider");
        await providers.EditAsync(new(model.Id, null, [], 4096, null, null, null, model.Options.Revision), default);
        model = Model();
        await providers.SetYoloAsync(model.Id, new(true, model.Options.Revision), default);
        try
        {
            await providers.SetYoloAsync(model.Id, new(false, model.Options.Revision), default);
            throw new InvalidOperationException("Stale policy update accepted");
        }
        catch (ProviderFailure error) when (error.Status == 409) { }
        var secondModel = providers.View.Models.Single(item => item.Model.NativeId == "second-native");
        await providers.SetYoloAsync(secondModel.Id, new(true, secondModel.Options.Revision), default);
        var ordinary = providers.View.Models.Single(item => !item.Yolo);
        await providers.EditAsync(new(ordinary.Id, null, [], 1024, null, null, null, ordinary.Options.Revision), default);
        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
        using var swapDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await ObserveWhileAsync(providers.ManageAsync(Model().Id, true, swapDeadline.Token));
        Check(loads == 1 && Model().Context == 1024 && !loaded["ordinary-model"],
            "a cold YOLO load releases an idle protected model and uses native defaults");

        using var protectedRequest = await ReadRequestAsync("ordinary-model");
        using var nativeRequest = await ReadRequestAsync("native-model");
        await using var network = new PeerNetwork(directory, 0, providers, settings);
        await network.InitializeAsync();
        async Task Route(string name, string? hold = null)
        {
            var context = new DefaultHttpContext();
            context.Items[typeof(InferenceAccess)] = new InferenceAccess(null, null, [], KeyPriority.Normal, default);
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            { model = name, hold, messages = new[] { new { role = "user", content = "synthetic" } } }));
            context.Response.Body = new MemoryStream();
            await network.RouteAsync(InferenceOperation.Chat, context, swapDeadline.Token);
        }
        var firstRequest = Route("native-model", "A");
        await activeA.Task.WaitAsync(swapDeadline.Token);
        var secondRequest = Route("second-native", "B");
        await activeB.Task.WaitAsync(swapDeadline.Token);
        Check(!firstRequest.IsCompleted && loaded["native-model"] && loaded["second-native"],
            "a cold YOLO model starts and generates alongside running YOLO work");
        releaseA.SetResult();
        await firstRequest;
        var nativeWork = providers.TryStart(nativeRequest, Model().Id).Execute!;
        var protectedPlacement = providers.Placement(protectedRequest.Shape, ordinary.Id)!;
        var displaced = new List<string>();
        Task Record(string id) { displaced.Add(id); return Task.CompletedTask; }
        await WaitForOtherModeAsync(() => providers.PreparePlacementAsync(protectedPlacement, Record, swapDeadline.Token));
        await FinishAsync(nativeWork);
        await WaitForOtherModeAsync(() => providers.PreparePlacementAsync(protectedPlacement, Record, swapDeadline.Token));
        Check(loaded["native-model"] && loaded["second-native"], "group eviction waits for the last active YOLO request");
        releaseB.SetResult();
        await secondRequest;

        pressureFromNative = pressure = true;
        resources.ApplyObservation(Sample());
        while (resources.Status.State == ContributionState.Withdrawing)
        {
            await resources.AdvanceWithdrawalAsync(providers, swapDeadline.Token);
            resources.ApplyObservation(Sample());
            await Task.Delay(20, swapDeadline.Token);
        }
        Check(resources.Status.State == ContributionState.Contributor &&
            providers.Placement(protectedRequest.Shape, ordinary.Id) is not null,
            "memory pressure cannot block an authorized swap");
        await ObserveWhileAsync(providers.PreparePlacementAsync(protectedPlacement, Record, swapDeadline.Token));
        Check(displaced.ToHashSet().SetEquals([Model().Id, secondModel.Id]) && !loaded["native-model"] && loaded["ordinary-model"] &&
            resources.Status.State == ContributionState.Contributor,
            "protected preparation unloads YOLO, samples recovered capacity, and loads the protected model");
        pressureFromNative = false;

        var protectedWork = providers.TryStart(protectedRequest, ordinary.Id).Execute!;
        var nativePlacement = providers.Placement(nativeRequest.Shape, Model().Id)!;
        await WaitForOtherModeAsync(() => providers.PreparePlacementAsync(nativePlacement, Record, swapDeadline.Token));
        await FinishAsync(protectedWork);
        Check(!loaded["native-model"], "an evicted model stays unloaded until its next request can run");
        await ObserveWhileAsync(providers.PreparePlacementAsync(nativePlacement, Record, swapDeadline.Token));
        Check(displaced.Take(2).ToHashSet().SetEquals([Model().Id, secondModel.Id]) && displaced.Last() == ordinary.Id && !loaded["second-native"] && loads == 2 &&
            loaded["native-model"] && !loaded["ordinary-model"], "a later YOLO request restores its model after protected work settles");

        loaded["ordinary-model"] = true;
        await providers.RefreshAsync(endpoint.Id, swapDeadline.Token);
        Check(providers.TryStart(nativeRequest, Model().Id).Status == AdmissionStatus.Wait,
            "an already loaded model cannot start while the other mode is resident");
        await ObserveWhileAsync(providers.PreparePlacementAsync(
            providers.Placement(nativeRequest.Shape, Model().Id)!, Record, swapDeadline.Token));
        Check(!loaded["ordinary-model"] && loads == 2, "warm preparation evicts the other mode without reloading the target");

        await resources.RetryBenchmarkAsync(ordinary.Id, InferenceOperation.Chat, providers, swapDeadline.Token, sampleOnly: true);
        while (resources.ForcedBenchmarkHoldsServing)
        {
            resources.ApplyObservation(Sample());
            resources.AdvanceBenchmarks(providers, swapDeadline.Token);
            await Task.Delay(20, swapDeadline.Token);
        }
        var sampled = providers.View.Models.Single(item => item.Id == ordinary.Id);
        var rate = resources.RouteTiming(sampled, InferenceOperation.Chat).GenerationTokensPerSecond;
        Check(rate == 25 && !loaded["ordinary-model"] && !loaded["native-model"],
            $"a protected cold benchmark swaps out YOLO and releases its own slot (rate {rate}, ordinary loaded {loaded["ordinary-model"]}, YOLO loaded {loaded["native-model"]})");
        await providers.ManageAsync(Model().Id, true, swapDeadline.Token);

        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Now });
        model = Model();
        var lease = resources.TryAdmit(model, endpoint, new(InferenceOperation.Chat, 4096), ResourceWork.Execute).Require();
        pressure = true;
        resources.ApplyObservation(Sample());
        Check(!lease.Cancellation.IsCancellationRequested && resources.IsAvailable(model, endpoint) &&
            resources.IsAvailable(providers.View.Models.Single(item => !item.Yolo), endpoint),
            "memory pressure and missing profiles permit both modes to serve");
        lease.Complete(); lease.Dispose();
        await providers.EditAsync(new(Model().Id, "renamed", [], 4096, null, null, null, Model().Options.Revision), default);
        Check(Model().Yolo, "ordinary model edits preserve the independent local opt-in");
        foreach (var status in new[] { 200, 400, 429, 507, 200 })
        {
            var streaming = status == 200 && nativeStatus == 507;
            nativeStatus = status;
            nativeType = streaming ? "text/event-stream" : "application/json";
            nativeBody = streaming ? "data: {\"error\":\"out of memory\"}\n\ndata: [DONE]\n\n" :
                status == 200 ? "{\"result\":\"native\"}" : "{\"error\":\"native capacity or input result\"}";
            var request = new DefaultHttpContext();
            request.Request.ContentType = "application/json";
            request.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""
                {"model":"native-model","messages":[{"role":"user","content":"synthetic"}],"unknown_provider_field":{"nested":7}}
                """));
            using var payload = await providers.ReadInferenceAsync(InferenceOperation.Chat, request, default);
            var start = providers.TryStart(payload, Model().Id);
            Check(start.Execute is not null, "marked model bypasses estimate/reservation/benchmark pressure holds");
            var response = new DefaultHttpContext(); response.Response.Body = new MemoryStream();
            var result = await start.Execute!(response, default);
            var bytes = ((MemoryStream)response.Response.Body).ToArray();
            Check(result.Failure is null && result.Settled && response.Response.StatusCode == status &&
                Encoding.UTF8.GetString(bytes) == nativeBody, "native success and input/capacity errors return unchanged");
        }
        var release = await providers.ReleaseManagedAsync(default);
        Check(release.Error is null && loaded["native-model"] && !loaded["ordinary-model"],
            "automatic release unloads unmarked models and retains marked cotenants");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (resources.Status.State == ContributionState.Withdrawing)
        {
            await resources.AdvanceWithdrawalAsync(providers, deadline.Token);
            resources.ApplyObservation(Sample());
            await Task.Delay(20, deadline.Token);
        }
        Check(resources.Status.State == ContributionState.Contributor,
            "a resident YOLO model does not trigger withdrawal through memory pressure");
        foreach (var kind in new[] { "llm", "embedding", "decision" })
        {
            nativeKind = kind;
            await providers.RefreshAsync(endpoint.Id, deadline.Token);
            var operation = Model().Model.Operations.Single();
            JsonElement SampleEvidence() => JsonSerializer.SerializeToElement(resources.View(providers), SnapshotFile<ResourceData>.Json)
                .GetProperty("models").EnumerateArray().Single(item => item.GetProperty("id").GetString() == Model().Id)
                .GetProperty("profile");
            while (SampleEvidence().ValueKind == JsonValueKind.Null)
            {
                resources.AdvanceBenchmarks(providers, deadline.Token);
                await Task.Delay(20, deadline.Token);
            }
            var sample = SampleEvidence().GetProperty("speed");
            Check(sample.GetProperty("elapsed").ValueKind == JsonValueKind.String && sample.GetProperty("error").ValueKind == JsonValueKind.Null &&
                (operation != InferenceOperation.Chat || resources.RouteTiming(Model(), operation).GenerationTokensPerSecond == 25),
                "each resident YOLO text workload records valid evidence through native admission without reloading");
        }
        Check(loads == 3 && loaded["native-model"], "native benchmark retains the loaded instance across samples");
        nativeKind = "llm";
        await providers.RefreshAsync(endpoint.Id, deadline.Token);
        await providers.SetServingAsync(Model().Id, false, default);
        Check(!resources.IsAvailable(Model(), endpoint), "explicit per-model serving disable still blocks YOLO");
        await providers.SetServingAsync(Model().Id, true, default);
        var running = resources.TryAdmit(Model(), endpoint, new(InferenceOperation.Chat, 1024), ResourceWork.Execute).Require();
        await settings.UpdateAsync(value => value with { Role = LocalRole.Leech });
        Check(running.Cancellation.IsCancellationRequested && !resources.IsAvailable(Model(), endpoint),
            "explicit operator contributor pause cancels YOLO work and prevents admission");
        running.Complete(); running.Dispose();
        await providers.ReleaseManagedAsync(default, includeYolo: true);
        Check(!loaded["native-model"], "explicit operator release can still unload a marked model");
        await providers.SetYoloAsync(Model().Id, new(false, Model().Options.Revision), default);
        Check(!Model().Yolo && !resources.IsAvailable(Model(), endpoint), "disabling the opt-in restores ordinary safeguards");

        async Task ObserveWhileAsync(Task preparation)
        {
            while (!preparation.IsCompleted)
            {
                resources.ApplyObservation(Sample());
                await Task.Delay(20, swapDeadline.Token);
            }
            await preparation;
        }

        async Task<InferenceRequest> ReadRequestAsync(string name)
        {
            var request = new DefaultHttpContext();
            request.Request.ContentType = "application/json";
            request.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(name == "ordinary-model"
                ? """{"model":"ordinary-model","messages":[{"role":"user","content":"synthetic"}],"max_tokens":32}"""
                : """{"model":"native-model","messages":[{"role":"user","content":"synthetic"}],"unknown_provider_field":{"nested":7}}"""));
            return await providers.ReadInferenceAsync(InferenceOperation.Chat, request, swapDeadline.Token);
        }

        async Task FinishAsync(Func<HttpContext, CancellationToken, Task<ExecutionResult>> execute)
        {
            var response = new DefaultHttpContext();
            response.Response.Body = new MemoryStream();
            Check((await execute(response, swapDeadline.Token)).Settled, "work settles before changing resource mode");
        }

        static async Task WaitForOtherModeAsync(Func<Task> prepare)
        {
            try { await prepare(); }
            catch (ProviderFailure error) when (error.CapacityWait) { return; }
            throw new InvalidOperationException("A model swap displaced active work.");
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
