using System.Net;
using System.Text.Json;
using Mango.Host;
using Mango.Providers;
using Mango.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class ResourceChecks
{
    private const long MiB = 1024 * 1024;
    private const long Total = 16 * 1024 * MiB;
    private static readonly Workload Chat = new(InferenceOperation.Chat, 512);

    public static async Task RunAsync(string root)
    {
        var visited = new HashSet<int>();
        WindowsObservation.WalkDescendants(1, new() { [1] = [2, 3], [3] = [4], [4] = [5] },
            (_, child) => { visited.Add(child); return true; });
        Check(visited.SetEquals([2, 3, 4, 5]), "a leaf sibling cannot hide later model and vision descendants");
        visited.Clear();
        var starts = new Dictionary<int, long> { [1] = 100, [2] = 50, [3] = 110, [4] = 120, [5] = 70 };
        WindowsObservation.WalkDescendants(1, new() { [1] = [2, 3], [2] = [5], [3] = [4] },
            (parent, child) =>
            {
                if (!WindowsObservation.IsCurrentChild(starts[parent], starts[child])) return false;
                visited.Add(child);
                return true;
            });
        Check(visited.SetEquals([3, 4]), "fresh creation identities exclude reused-parent PID ancestry and its subtree while retaining live workers");
        await CheckLoadedLmStudioBenchmarkAsync(root);
        await CheckColdBenchmarkSlotsAsync(root);
        await CheckOfflineLauncherReleaseAsync(root);
        await CheckTransientReleaseRecoveryAsync(root);
        var directory = Directory.CreateDirectory(Path.Combine(root, "resources")).FullName;
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings
        {
            Role = LocalRole.Contributor, RamReserveMiB = 0, ReducedReserveConsent = true, RamCutoffPercent = 100,
            BenchmarkPolicy = BenchmarkPolicy.WhenConvenient, CooldownSeconds = 4
        });
        var clock = new Clock();
        var free = 900 * MiB;
        var worker = 0L;
        var extra = false;
        var runtime = "first";
        var calls = 0;
        var providerIdle = true;
        var endpointId = Guid.Empty;
        var blockCall = int.MaxValue;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdForcedSample = false;
        var forcedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishForced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var fixture = builder.Build();
        fixture.MapGet("/props", () => new
        {
            default_generation_settings = new { n_ctx = 512 }, total_slots = 2, build_info = runtime,
            chat_template = "fixture"
        });
        fixture.MapGet("/v1/models", () => new
        {
            data = (extra ? new[] { "first", "second" } : ["first"]).Select(id => new { id })
        });
        fixture.MapGet("/slots", () => new[] { new { is_processing = !providerIdle } });
        fixture.MapPost("/v1/chat/completions/input_tokens", async (HttpContext context) =>
            new { input_tokens = (await PromptAsync(context)).Tokens });
        fixture.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var (count, speed) = await PromptAsync(context);
            if (speed)
            {
                if (holdForcedSample)
                {
                    forcedStarted.TrySetResult();
                    await finishForced.Task.WaitAsync(context.RequestAborted);
                }
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: {\"timings\":{\"predicted_n\":25,\"predicted_ms\":1000}}\n\ndata: [DONE]\n\n");
                return;
            }
            if (Interlocked.Increment(ref calls) == Volatile.Read(ref blockCall))
            {
                blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n");
            await context.Response.WriteAsync(
                $"data: {{\"usage\":{{\"prompt_tokens\":{count},\"completion_tokens\":1,\"prompt_tokens_details\":{{\"cached_tokens\":0}}}}}}\n\ndata: [DONE]\n\n");
        });
        await fixture.StartAsync();
        using var resources = new ResourceProtection(directory, settings,
            (_, now) => Sample(now), () => ((ulong)Total, (ulong)free), clock);
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        var endpoint = await providers.ConfigureAsync(new(null, ProviderProfile.LlamaSingle, ProviderMode.Attached,
            fixture.Urls.Single(), null, false, null, null), default);
        endpointId = endpoint.Id;
        var model = providers.View.Models.Single();
        var update = new BudgetUpdate(model.Id, Chat.Operation, Chat.Units, 1, 0, 1200, 600, null, null,
            AllowOverlap: true, ParallelSlots: 2, AllowAttached: true);
        resources.ApplyObservation(Sample(clock.GetUtcNow()));
        var managed = endpoint with { Mode = ProviderMode.Managed, CanLoad = true, CanUnload = true };
        Check(resources.Readiness(model, managed, Chat.Operation) is { State: "ready", Available: true } &&
            resources.Estimate(model, managed, Chat.Operation) is { Estimated: true, Workload.Items: 1, AllowOverlap: false } &&
            resources.Estimate(model, managed, InferenceOperation.Embeddings) is { Estimated: true, Workload.Items: 16 } &&
            resources.Estimate(model, managed, InferenceOperation.SystemOne) is { Estimated: true, Workload.Items: 4 },
            "observed hardware supplies usable operation-specific fallback limits before any owner edit");
        Check(resources.Readiness(model, endpoint, Chat.Operation).Available, "attached serving needs no execution budget");
        var strata = managed with { Profile = ProviderProfile.Strata };
        resources.TryAdmit(model, strata, new(InferenceOperation.Chat, 200_000), ResourceWork.Execute).Require().Complete();
        await resources.SaveBudgetAsync(update, providers, default);

        var revision = JsonSerializer.SerializeToElement(resources.View(providers), SnapshotFile<ResourceData>.Json)
            .GetProperty("models")[0].GetProperty("budgetRevision").GetString();
        await resources.SaveBudgetAsync(update with { LoadRamMiB = 1250, ExpectedRevision = revision }, providers, default);
        try
        {
            await resources.SaveBudgetAsync(update with { ExpectedRevision = revision }, providers, default);
            throw new InvalidOperationException("A stale UI budget overwrote another editor.");
        }
        catch (ProviderFailure error) when (error.Status == 409) { }
        await resources.SaveBudgetAsync(update, providers, default);

        Check(resources.TryAdmit(model, managed, Chat, ResourceWork.Load, additionalLoad: true).Status == AdmissionStatus.Wait,
            "a transient load peak can exceed memory even when execution would fit");
        free = 1450 * MiB;
        Tick(2);
        var loading = resources.TryAdmit(model, managed, Chat, ResourceWork.Load).Require();
        foreach (var scenario in new[] { "attribution", "cpu", "gpu", "memory", "topology", "exception" })
        {
            using var startup = new ResourceProtection(Directory.CreateDirectory(Path.Combine(directory, "startup-" + scenario)).FullName, settings,
                (_, now) => scenario == "exception" ? throw new System.ComponentModel.Win32Exception() : Sample(now) with {
                    WorkerAttribution = scenario != "attribution", ExternalCpu = scenario == "cpu" ? null : 0,
                    ExternalGpu = scenario == "gpu" ? null : 0, TotalRam = scenario == "memory" ? 0 : Total,
                    Hardware = scenario == "topology" ? null : "test-device" },
                () => scenario == "memory" ? throw new System.ComponentModel.Win32Exception() : ((ulong)Total, (ulong)free), clock);
            await startup.InitializeAsync();
            startup.ApplyObservation(Sample(clock.GetUtcNow()));
            await startup.SaveBudgetAsync(update, providers, default);
            var lease = startup.TryAdmit(model, managed, Chat, ResourceWork.Load).Require();
            for (var i = 0; i < 4; i++) await startup.SampleAndApplyAsync(() => providers.View, default);
            Check(startup.Status.State == ContributionState.Contributor && !lease.Cancellation.IsCancellationRequested &&
                startup.IsAvailable(model, managed, Chat.Operation), "repeated missing telemetry keeps own load and placement alive");
            lease.Complete();
            var execution = startup.TryAdmit(model, managed, Chat, ResourceWork.Execute).Require();
            execution.Complete();
            startup.ApplyObservation(Sample(clock.GetUtcNow()));
            Check(startup.ServingAllowed, "usable telemetry resumes observation without a cooldown");
        }
        foreach (var scenario in new[] { "insufficient", "sufficient", "absent", "stale" })
        {
            using var headroom = new ResourceProtection(
                Directory.CreateDirectory(Path.Combine(directory, "headroom-reread-" + scenario)).FullName, settings,
                (_, now) => Sample(now), () => throw new System.ComponentModel.Win32Exception(), clock);
            await headroom.InitializeAsync();
            headroom.ApplyObservation(Sample(clock.GetUtcNow()) with { FreeRam = scenario == "sufficient" ? 1450 * MiB : 900 * MiB });
            await headroom.SaveBudgetAsync(update, providers, default);
            if (scenario == "absent")
                headroom.ApplyObservation(Sample(clock.GetUtcNow()) with { TotalRam = 0, FreeRam = 0 });
            if (scenario == "stale") clock.Advance(settings.Value.SampleSeconds + 6);
            var attempt = headroom.TryAdmit(model, managed, Chat, ResourceWork.Load, additionalLoad: true);
            attempt.Reservation?.Complete();
            Check(scenario != "sufficient"
                    ? attempt.Status == AdmissionStatus.Wait && attempt.MemoryWait
                    : attempt.Status == AdmissionStatus.Admitted,
                $"RAM reread failure uses only current measured headroom ({scenario}); got {attempt.Status}, memoryWait={attempt.MemoryWait}");
        }
        await CheckPoolsAsync();
        Check(resources.Readiness(model, endpoint, Chat.Operation).State == "busy", "readiness reports live reservations without taking another");
        Check(resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "the load reservation excludes concurrent inference before the worker allocates");
        loading.Complete();
        free = 900 * MiB;
        Tick(2);

        var racing = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(
            () => resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute))));
        Check(racing.Count(result => result.Reservation is not null) == 2, "racing requests share exactly two native slots");
        var running = racing.Where(result => result.Reservation is not null).Select(result => result.Require()).ToArray();
        running[0].Complete(settled: false);
        running[1].Complete();
        Check(resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "unconfirmed cancellation retains its lease");
        resources.ConfirmSettled(model.Id);
        var failed = resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require();
        await resources.AllocationFailedAsync(failed);
        failed.Complete();
        using (var reloaded = new ResourceProtection(directory, settings, (_, now) => Sample(now), () => ((ulong)Total, 0), clock))
        {
            await reloaded.InitializeAsync();
            reloaded.TryAdmit(model, managed, Chat, ResourceWork.Load).Require().Complete();
            reloaded.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require().Complete();
        }
        await resources.SaveBudgetAsync(update, providers, default);
        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
        Tick(301);
        resources.AdvanceBenchmarks(providers, default);
        Check(calls == 0 && Profile(model.Id).ValueKind == JsonValueKind.Null, "Never skips automatic benchmarks");
        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.WhenConvenient });
        await resources.ClientArrivedAsync(default);
        Tick(299);
        resources.AdvanceBenchmarks(providers, default);
        Check(calls == 0, "convenient benchmark waits for five local idle minutes");
        blockCall = 2;
        Tick(1);
        await EventuallyAsync(() => blocked.Task.IsCompleted);
        Check(resources.RouteTiming(model, Chat.Operation).GenerationTokensPerSecond == 25, "native generation rate is preferred");
        await resources.ClientArrivedAsync(default);
        var partial = Profile(model.Id);
        Check(!partial.GetProperty("complete").GetBoolean() && partial.GetProperty("measurements").GetArrayLength() == 1 &&
            partial.GetProperty("error").ValueKind == JsonValueKind.Null, "client arrival interrupts and retains the finished pass");
        var incoming = resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require();
        incoming.Complete();
        Tick(300);
        await EventuallyAsync(() => Profile(model.Id).GetProperty("complete").GetBoolean());
        Check(calls == 5, "convenient policy resumes at the first missing pass");

        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Now });
        Check(resources.ServingAllowed, "Now allows serving while profiles are being scheduled");
        resources.AdvanceBenchmarks(providers, default);
        Check(resources.ServingAllowed, "Now reuses complete matching evidence");
        extra = true;
        await providers.RefreshAsync(endpoint.Id, default);
        resources.AdvanceBenchmarks(providers, default);
        Check(resources.ServingAllowed, "a new model can serve before it is benchmarked");
        var added = providers.View.Models.Single(item => item.Id != model.Id);
        await EventuallyAsync(() => Profile(added.Id).ValueKind != JsonValueKind.Null && Profile(added.Id).GetProperty("complete").GetBoolean());
        Check(calls == 9, "new deployments inherit the local policy");
        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
        await resources.RetryBenchmarkAsync(model.Id, Chat.Operation, providers, default);
        await EventuallyAsync(() => Profile(model.Id).ValueKind != JsonValueKind.Null && Profile(model.Id).GetProperty("complete").GetBoolean());
        Check(calls == 13, "Never permits an explicitly requested benchmark");
        await EventuallyAsync(() => !resources.ForcedBenchmarkHoldsServing);

        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.WhenConvenient });
        var displaced = resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require();
        holdForcedSample = true;
        await resources.RetryBenchmarkAsync(model.Id, Chat.Operation, providers, default, sampleOnly: true);
        Check(displaced.Cancellation.IsCancellationRequested && resources.ForcedBenchmarkHoldsServing,
            "a forced benchmark cancels active inference");
        resources.AdvanceBenchmarks(providers, default);
        Check(!forcedStarted.Task.IsCompleted &&
            resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "the forced benchmark waits for native settlement and holds new inference");
        displaced.Complete();
        await EventuallyAsync(() => forcedStarted.Task.IsCompleted);
        await resources.ClientArrivedAsync(default);
        Check(resources.ForcedBenchmarkHoldsServing &&
            resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "new client work cannot preempt a forced benchmark");
        finishForced.TrySetResult();
        await EventuallyAsync(() => !resources.ForcedBenchmarkHoldsServing);
        Check(Profile(model.Id).GetProperty("complete").GetBoolean() &&
            Profile(model.Id).GetProperty("speed").GetProperty("providerGenerationRate").GetBoolean(),
            "a forced warm sample preserves qualification and records provider timing provenance");
        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
        var unconfirmed = resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require();
        await resources.RetryBenchmarkAsync(model.Id, Chat.Operation, providers, default, sampleOnly: true);
        Tick((int)ProviderLimits.LifecycleTimeout.TotalSeconds + 1);
        resources.AdvanceBenchmarks(providers, default);
        Check(!resources.ForcedBenchmarkHoldsServing && unconfirmed.Cancellation.IsCancellationRequested &&
            resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "a forced wait expires without falsely settling the previous native operation");
        unconfirmed.Complete();
        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Now });
        await resources.SaveBudgetAsync(update with { Items = 2 }, providers, default);
        Check(resources.ServingAllowed && Profile(model.Id).ValueKind == JsonValueKind.Null &&
            Profile(added.Id).GetProperty("complete").GetBoolean(), "a workload change invalidates only its own evidence");
        runtime = "different";
        await providers.RefreshAsync(endpoint.Id, default);
        resources.AdvanceBenchmarks(providers, default);
        Check(resources.ServingAllowed && Profile(model.Id).ValueKind == JsonValueKind.Null,
            "runtime fingerprint invalidates only matching placement evidence");

        await settings.UpdateAsync(value => value with { Role = LocalRole.Leech });
        await resources.AdvanceWithdrawalAsync(providers, default);
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(resources.Status.State == ContributionState.ManualLeech, "manual withdrawal releases admissions and remains manual");
        Tick(500);
        Check(resources.Status.State == ContributionState.ManualLeech, "quiet cooldown never clears manual leech");
        using (var restarted = new ResourceProtection(directory, settings, (_, now) => Sample(now), () => ((ulong)Total, (ulong)free), clock))
        {
            await restarted.InitializeAsync();
            restarted.ApplyObservation(Sample(clock.GetUtcNow()));
            await restarted.AdvanceWithdrawalAsync(providers, default);
            await restarted.AdvanceWithdrawalAsync(providers, default);
            Check(restarted.Status.State == ContributionState.ManualLeech, "manual leech survives restart");
        }
        await settings.UpdateAsync(value => value with { Role = LocalRole.Contributor, BenchmarkPolicy = BenchmarkPolicy.Never });
        Tick(2);
        model = providers.View.Models.Single(item => item.Id == model.Id);
        await resources.SaveBudgetAsync(update, providers, default);
        providerIdle = false;
        var unfinished = resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require();
        unfinished.Complete(settled: false);
        await resources.RunAsync(providers, new CancellationToken(canceled: true));
        using var resumed = new ResourceProtection(directory, settings, (_, now) => Sample(now), () => ((ulong)Total, (ulong)free), clock);
        await resumed.InitializeAsync();
        await using var resumedProviders = new ProviderCatalog(directory, resumed);
        await resumedProviders.InitializeAsync();
        resumed.RestoreUnsettled(resumedProviders);
        resumed.ApplyObservation(Sample(clock.GetUtcNow()));
        await resumed.AdvanceWithdrawalAsync(resumedProviders, default);
        await FinishWithdrawalAsync(ContributionState.Fault);
        Check(!resumed.ServingAllowed, "shutdown keeps attached work unavailable while cancellation is unconfirmed");
        providerIdle = true;
        clock.Advance(1);
        resumed.ApplyObservation(Sample(clock.GetUtcNow()));
        await resumed.AdvanceWithdrawalAsync(resumedProviders, default);
        await FinishWithdrawalAsync(ContributionState.Contributor);
        Console.WriteLine("Resource admission and benchmark checks passed.");

        async Task CheckPoolsAsync()
        {
            using var pools = new ResourceProtection(Directory.CreateDirectory(Path.Combine(directory, "checked-pools")).FullName,
                settings, (_, now) => Sample(now), () => ((ulong)Total, (ulong)(2048 * MiB)), clock);
            await pools.InitializeAsync();
            var latest = Sample(clock.GetUtcNow()) with { Devices = [
                new("a", "first", false, 8L * 1024 * MiB, 6L * 1024 * MiB),
                new("b", "second", false, 8L * 1024 * MiB, 2L * 1024 * MiB)] };
            pools.ApplyObservation(latest);
            Deployment Native(long? host, Dictionary<string, long> devices) => model with
                { Model = model.Model with { NativeLoadPeak = new(host, devices) } };
            bool Fits(Deployment target) => pools.AdditionalLoadFits(target, Chat.Operation);
            var cold = new BenchmarkMeasurement(Chat, "cold", null, null, TimeSpan.Zero, null, null, null, null,
                new(100, new() { ["a"] = 40 }), null, 2, SettledIncrement: new(70, new() { ["a"] = 20 }), LoadIdentity: "current");
            var warm = cold with { Pass = "warm", ObservedIncrement = new(60, new() { ["a"] = 5 }) };
            var profile = new BenchmarkProfile(model.Id, "", "", Chat.Operation, clock.GetUtcNow(), [cold, warm], true, null);
            var combined = ResourceProtection.MeasuredLoadCost(profile, "current");
            Check(combined?.RamBytes == 130 && combined.DeviceBytes["a"] == 40 &&
                ResourceProtection.MeasuredLoadCost(profile, "changed") is null, "only comparable peaks cover cold load and warm execution together");
            var known = Native(1024 * MiB, new() { ["a"] = MiB, ["b"] = 256 * MiB });
            Check(Fits(known) && !Fits(Native(1024 * MiB, new() { ["a"] = 1280 * MiB, ["b"] = 0 })) &&
                !Fits(Native(1024 * MiB, new() { ["a"] = MiB })), "a checked load must fit each discrete pool, including its margin");
            await pools.SaveBudgetAsync(update with { LoadOnly = true, LoadRamMiB = 32, LoadVramMiB = new() { ["a"] = 1 } },
                providers, default);
            Check(Fits(Native(Total, new() { ["a"] = Total, ["b"] = 0 })),
                "owner values override native values only for supplied pools");
            var attempt = pools.TryAdmit(known, managed, Chat, ResourceWork.Load, additionalLoad: true).Require();
            Check(pools.TryAdmit(known, managed, Chat, ResourceWork.Load, additionalLoad: true).Status == AdmissionStatus.Wait,
                "a racing load cannot spend the active reservation");
            attempt.Complete();
            pools.ApplyObservation(latest with { Hardware = "unified", Devices = [new("integrated", "shared", true, Total, Total)] });
            Check(Fits(Native(2048 * MiB, [])) && !Fits(Native(2049 * MiB, [])),
                "integrated memory shares exactly one RAM pool");
        }

        ResourceObservation Sample(DateTimeOffset now) =>
            new(now, Total, free, 2, 3, [], "test-device", new() { [endpointId] = new([1234], worker, [], worker) }, true, null);

        void Tick(int seconds)
        {
            clock.Advance(seconds);
            resources.ApplyObservation(Sample(clock.GetUtcNow()));
        }

        JsonElement Profile(string id) => JsonSerializer.SerializeToElement(resources.View(providers),
                SnapshotFile<ResourceData>.Json).GetProperty("models").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == id).GetProperty("profile");

        async Task EventuallyAsync(Func<bool> ready)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!ready())
            {
                resources.AdvanceBenchmarks(providers, deadline.Token);
                await Task.Delay(25, deadline.Token);
            }
        }

        async Task FinishWithdrawalAsync(ContributionState expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (resumed.Status.State == ContributionState.Withdrawing)
            {
                await resumed.AdvanceWithdrawalAsync(resumedProviders, deadline.Token);
                await Task.Delay(25, deadline.Token);
            }
            Check(resumed.Status.State == expected, $"withdrawal should reach {expected}");
        }
    }

    private static async Task CheckLoadedLmStudioBenchmarkAsync(string root)
    {
        Check(BenchmarkProtocol.StreamLooksBuffered(TimeSpan.FromSeconds(32), TimeSpan.FromSeconds(32.25)) &&
            !BenchmarkProtocol.StreamLooksBuffered(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)),
            "a late output burst cannot authenticate a generation rate");
        using var vllm = JsonDocument.Parse("""
            {"usage":{"completion_tokens":128},"metrics":{"generation_time_ms":3175,"tokens_per_second":12}}
            """);
        Check(BenchmarkProtocol.ReadMetrics(vllm.RootElement, default).Item4 == 40,
            "provider decode timing excludes prefill from the rate");
        var directory = Directory.CreateDirectory(Path.Combine(root, "lmstudio-benchmark")).FullName;
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor,
            BenchmarkPolicy = BenchmarkPolicy.WhenConvenient, SampleSeconds = 2 });
        var clock = new Clock();
        var calls = 0;
        var speedCalls = 0;
        var loads = 0;
        var unloads = 0;
        var loaded = true;
        var slowUnload = false;
        var keepLoaded = false;
        var contextLength = 512;
        var endpointId = Guid.Empty;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var fixture = builder.Build();
        fixture.MapGet("/api/v1/models", () => new { models = new[] { new {
            key = "bounded", type = "llm", max_context_length = 32768,
            loaded_instances = loaded ? new[] { new { id = "bounded-live", config = new { context_length = contextLength, parallel = 1 } } } : []
        } } });
        fixture.MapPost("/api/v1/models/unload", async () => {
            unloads++; loaded = keepLoaded;
            if (slowUnload) await Task.Delay(TimeSpan.FromSeconds(7));
            return Results.Ok();
        });
        fixture.MapPost("/api/v1/models/load", () => { loads++; loaded = true; return Results.Ok(); });
        fixture.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var (tokens, speed) = await PromptAsync(context);
            if (speed && Interlocked.Increment(ref speedCalls) == 1)
            {
                context.Response.StatusCode = 503;
                return;
            }
            context.Response.ContentType = "text/event-stream";
            if (speed)
            {
                await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"1\"}}]}\n\n");
                await Task.Delay(80, context.RequestAborted);
                await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"2\"}}]}\n\n");
                await Task.Delay(600, context.RequestAborted);
                await context.Response.WriteAsync("data: {\"usage\":{\"completion_tokens\":11}}\n\ndata: [DONE]\n\n");
                return;
            }
            Interlocked.Increment(ref calls);
            await context.Response.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"ok\"}}}}],\"usage\":{{\"prompt_tokens\":{tokens},\"completion_tokens\":1}}}}\n\ndata: [DONE]\n\n");
        });
        await fixture.StartAsync();
        ResourceObservation Sample() => new(clock.GetUtcNow(), Total, Total - (loaded ? 1024 * MiB : 0), 1, 1, [], "fixture", new()
            { [endpointId] = new([1234], loaded ? 1024 * MiB : 0, [], 1024 * MiB) }, true, null);
        using var resources = new ResourceProtection(directory, settings, (_, _) => Sample(),
            () => ((ulong)Total, (ulong)(Total - 1024 * MiB)), clock);
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        var endpoint = await providers.ConfigureAsync(new(null, ProviderProfile.LmStudio, ProviderMode.Managed,
            fixture.Urls.Single(), null, false, null, null), default);
        endpointId = endpoint.Id;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        JsonElement Profile() => JsonSerializer.SerializeToElement(resources.View(providers), SnapshotFile<ResourceData>.Json)
            .GetProperty("models")[0].GetProperty("profile");
        var worker = resources.RunAsync(providers, deadline.Token);
        var model = providers.View.Models.Single();
        try
        {
            Check(speedCalls == 0 && calls == 0, "the background worker waits for local idle time");
            clock.Advance(301);
            while (Profile().ValueKind == JsonValueKind.Null || Profile().GetProperty("measurements").GetArrayLength() < 4)
                await Task.Delay(25, deadline.Token);
            Check(speedCalls == 1 && Profile().GetProperty("speed").GetProperty("error").ValueKind == JsonValueKind.String,
                "failed speed samples back off while warm qualification proceeds");
            clock.Advance(300);
            while (resources.RouteTiming(model, Chat.Operation).GenerationTokensPerSecond is null)
                await Task.Delay(25, deadline.Token);
            Check(resources.RouteTiming(model, Chat.Operation).GenerationTokensPerSecond > 20 && speedCalls == 2 &&
                !Profile().GetProperty("speed").GetProperty("providerGenerationRate").GetBoolean(),
                "idle retry labels stream estimates and excludes the stream footer");
            Check(calls == 4 && loads == 0 && unloads == 0 && loaded && !Profile().GetProperty("complete").GetBoolean(),
                "a warm sample preserves the running LM Studio configuration");

            await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
            loaded = false;
            await providers.RefreshAsync(endpointId, deadline.Token);
            await resources.RetryBenchmarkAsync(model.Id, Chat.Operation, providers, deadline.Token, sampleOnly: true);
            while (resources.ForcedBenchmarkHoldsServing)
            {
                clock.Advance(1);
                await Task.Delay(30, deadline.Token);
            }
            Check(loads == 1 && unloads == 1 && speedCalls == 3 &&
                Profile().GetProperty("measurements").EnumerateArray().Any(pass => pass.GetProperty("pass").GetString() == "cold"),
                "a forced cold benchmark starts an unmeasured managed model, samples it and releases it");
            loaded = true;
            await providers.RefreshAsync(endpointId, deadline.Token);
        }
        finally
        {
            await deadline.CancelAsync();
            await worker;
        }
        using (var restarted = new ResourceProtection(directory, settings, (_, _) => Sample(), clock: clock))
        {
            await restarted.InitializeAsync();
            restarted.ApplyObservation(Sample());
            Check(restarted.RouteTiming(model, Chat.Operation).GenerationTokensPerSecond > 20, "generation speed survives restart");
            contextLength = 1024;
            await providers.RefreshAsync(endpointId, default);
            Check(restarted.RouteTiming(providers.View.Models.Single(), Chat.Operation).GenerationTokensPerSecond is null,
                "a changed runtime configuration invalidates generation speed");
        }
        using var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await settings.UpdateAsync(value => value with { Role = LocalRole.Leech });
        while (resources.Status.State == ContributionState.Withdrawing)
        {
            await resources.AdvanceWithdrawalAsync(providers, releaseDeadline.Token);
            resources.ApplyObservation(Sample());
            await Task.Delay(25, releaseDeadline.Token);
        }
        Check(!loaded && unloads == 2 && resources.Status.State == ContributionState.ManualLeech,
            "resource withdrawal still unloads the same protected LM Studio instance");
        loaded = true;
        slowUnload = true;
        await providers.RefreshAsync(endpointId, default);
        var released = await providers.ReleaseManagedAsync(default);
        Check(released.Error is null && !loaded && unloads == 3,
            "a timed out unload response is accepted only after fresh inventory verifies release");
        loaded = keepLoaded = true;
        slowUnload = false;
        await providers.RefreshAsync(endpointId, default);
        var refused = await providers.ReleaseManagedAsync(default);
        Check(refused.Error is not null && loaded,
            "a successful unload response cannot clear protection while inventory still shows residency");
        loaded = keepLoaded = false;
        var deployment = providers.View.Models.Single().Id;
        providers.RestoreUnsettled(endpointId, deployment, loading: true);
        await providers.RefreshAsync(endpointId, default);
        Check(providers.HasUnsettledLoad(deployment), "absence cannot settle an interrupted load");
        var late = Task.Run(async () => { await Task.Delay(400); loaded = true; });
        var lateRelease = await providers.ReleaseManagedAsync(default);
        await late;
        Check(lateRelease.Error is null && !loaded && !providers.HasUnsettledLoad(deployment),
            "release waits for late load completion, then unloads and verifies absence");
    }

    private static async Task CheckColdBenchmarkSlotsAsync(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "cold-benchmark-slot")).FullName;
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor, BenchmarkPolicy = BenchmarkPolicy.Now, SampleSeconds = 2 });
        string? resident = "previous";
        var batchModels = false;
        var cancellationModel = false;
        var benchmarkActive = 0;
        var benchmarkStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = 0;
        var holdRefresh = 0;
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var fixture = builder.Build();
        fixture.MapGet("/api/v1/models", async (CancellationToken cancellation) =>
        {
            if (Interlocked.Exchange(ref holdRefresh, 0) != 0)
            {
                refreshing.TrySetResult();
                await releaseRefresh.Task.WaitAsync(cancellation);
            }
            return new
            {
                max_loaded_models = 1, loads_settled = true,
                models = (cancellationModel ? new[] { "cancel-me" } :
                    batchModels ? new[] { "denied", "batch-ok", "first", "second", "previous" } :
                    new[] { "first", "second", "previous" }).Select(key => new
                {
                    key, type = "llm", size_bytes = 64 * MiB, max_context_length = 512,
                    loaded_instances = resident == key ? new[] { new { id = key,
                        busy = Volatile.Read(ref benchmarkActive) != 0, config = new { context_length = 512 } } } : []
                })
            };
        });
        fixture.MapPost("/api/v1/models/load", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            if (resident is not null) return Results.Conflict();
            var model = body.RootElement.GetProperty("model").GetString();
            if (model == "denied") return Results.Conflict();
            resident = model;
            return Results.Ok();
        });
        fixture.MapPost("/api/v1/models/unload", () => { resident = null; Interlocked.Increment(ref releases); return Results.Ok(); });
        fixture.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var (count, speed) = await PromptAsync(context);
            if (resident == "cancel-me")
            {
                Volatile.Write(ref benchmarkActive, 1);
                benchmarkStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                finally { Volatile.Write(ref benchmarkActive, 0); }
            }
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(speed
                ? "data: {\"timings\":{\"predicted_per_second\":25}}\n\ndata: [DONE]\n\n"
                : $"data: {{\"usage\":{{\"prompt_tokens\":{count},\"completion_tokens\":1}}}}\n\ndata: [DONE]\n\n");
        });
        await fixture.StartAsync();
        using var resources = new ResourceProtection(directory, settings, (_, now) =>
            new(now, Total, Total, 0, 0, [], "fixture", [], true, null), () => ((ulong)Total, (ulong)Total));
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        var endpoint = await providers.ConfigureAsync(new(null, ProviderProfile.Generic, ProviderMode.Managed,
            fixture.Urls.Single(), null, false, null, null), default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var previous = providers.View.Models.Single(model => model.Model.NativeId == "previous");
        await providers.SetServingAsync(previous.Id, false, deadline.Token);
        Volatile.Write(ref holdRefresh, 1);
        var refresh = providers.RefreshAsync(endpoint.Id, deadline.Token);
        await refreshing.Task.WaitAsync(deadline.Token);
        var worker = resources.RunAsync(providers, deadline.Token);
        try
        {
            while (JsonSerializer.SerializeToElement(resources.View(providers), SnapshotFile<ResourceData>.Json)
                .GetProperty("benchmark").ValueKind == JsonValueKind.Null)
                await Task.Delay(25, deadline.Token);
            releaseRefresh.TrySetResult();
            await refresh;
            while (Volatile.Read(ref releases) < 3 || providers.View.Models.Any(model => model.Instance is not null))
                await Task.Delay(25, deadline.Token);
            Check(providers.View.Models.Where(model => !model.ServingDisabled)
                .All(model => resources.RouteTiming(model, Chat.Operation).GenerationTokensPerSecond == 25),
                "Mango releases an idle occupied slot and records a warm rate for each eligible cold deployment");
            await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
            batchModels = true;
            await providers.RefreshAsync(endpoint.Id, deadline.Token);
            Check(resources.BenchmarkMissing(providers) == 2, "the batch selects only new or invalid samples");
            while (resources.ForcedBenchmarkHoldsServing)
                await Task.Delay(25, deadline.Token);
            var successful = providers.View.Models.Single(model => model.Model.NativeId == "batch-ok");
            Check(resources.RouteTiming(successful, Chat.Operation).GenerationTokensPerSecond == 25,
                "an engine refusal does not prevent the next missing benchmark");

            cancellationModel = true;
            await providers.RefreshAsync(endpoint.Id, deadline.Token);
            await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Now });
            await benchmarkStarted.Task.WaitAsync(deadline.Token);
            await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
            while (providers.View.Models.Any(model => model.Instance is not null))
                await Task.Delay(25, deadline.Token);
            Check(resident is null, "cancelling a cold benchmark unloads the runtime it started");
            cancellationModel = false;
            await providers.RefreshAsync(endpoint.Id, deadline.Token);
        }
        finally
        {
            await deadline.CancelAsync();
            releaseRefresh.TrySetResult();
            await worker;
        }
        var first = providers.View.Models.Single(model => model.Model.NativeId == "first");
        await resources.RetryBenchmarkAsync(first.Id, Chat.Operation, providers, default);
        var evidence = JsonSerializer.SerializeToElement(resources.View(providers), SnapshotFile<ResourceData>.Json)
            .GetProperty("models").EnumerateArray().Single(item => item.GetProperty("id").GetString() == first.Id)
            .GetProperty("profile");
        Check(evidence.GetProperty("measurements").EnumerateArray().Single().GetProperty("pass").GetString() == "cold" &&
            evidence.GetProperty("speed").ValueKind == JsonValueKind.Null && !evidence.GetProperty("complete").GetBoolean(),
            "retry keeps matching cold-load evidence while discarding the previous warm samples");
    }

    private static async Task CheckOfflineLauncherReleaseAsync(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "offline-launcher-release")).FullName;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var fixture = builder.Build();
        await fixture.StartAsync();
        await using var providers = new ProviderCatalog(directory);
        await providers.InitializeAsync();
        var endpoint = await providers.ConfigureAsync(new(null, ProviderProfile.LlamaSingle, ProviderMode.Managed,
            fixture.Urls.Single(), null, false, Path.Combine(directory, "llama-server.exe"),
            Path.Combine(directory, "model.gguf")), default);
        Check(!endpoint.Online && (await providers.ReleaseManagedAsync(default)).Error is not null,
            "an unrecognized listener cannot prove that a managed llama-server is cold");
        await fixture.StopAsync();
        Check((await providers.ReleaseManagedAsync(default)).Error is null,
            "a stopped on-demand llama-server with a free port does not block release");
        providers.RestoreUnsettled(endpoint.Id, providers.View.Models.Single().Id, loading: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Check((await providers.ReleaseManagedAsync(cancellation.Token)).Error is not null,
            "an interrupted load cannot be cleared by a free port");
    }

    private static async Task CheckTransientReleaseRecoveryAsync(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "transient-release")).FullName;
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor, BenchmarkPolicy = BenchmarkPolicy.Never });
        var clock = new Clock();
        var loaded = true;
        var keepLoaded = false;
        var inspections = 0;
        var offline = false;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var fixture = builder.Build();
        fixture.MapGet("/api/v1/models", () => {
            inspections++;
            if (offline) return Results.StatusCode(503);
            return Results.Json(new { models = new[] { new { key = "release", type = "llm", max_context_length = 512,
                loaded_instances = loaded ? new[] { new { id = "release-live", config = new { context_length = 512 } } } : [] } } });
        });
        fixture.MapPost("/api/v1/models/unload", () => { loaded = keepLoaded; return Results.Ok(); });
        await fixture.StartAsync();
        ResourceObservation Sample() => new(clock.GetUtcNow(), Total, Total, 0, 0, [], "fixture", [], true, null);
        using var resources = new ResourceProtection(directory, settings, (_, _) => Sample(), () => ((ulong)Total, (ulong)Total), clock);
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        await providers.ConfigureAsync(new(null, ProviderProfile.LmStudio, ProviderMode.Managed,
            fixture.Urls.Single(), null, false, null, null), default);
        resources.ApplyObservation(Sample());
        var unknown = Sample() with { WorkerAttribution = false, ExternalCpu = null, ExternalGpu = null };
        for (var i = 0; i < 4; i++) { clock.Advance(1); resources.ApplyObservation(unknown with { At = clock.GetUtcNow() }); }
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(loaded && resources.ServingAllowed, "missing workers never withdraw, unload or latch a fault");
        var warning = resources.LastEvent;
        resources.ApplyObservation(unknown with { At = clock.GetUtcNow() });
        Check(resources.LastEvent == warning, "repeated missing reports deduplicate warning events");
        resources.ApplyObservation(Sample());
        resources.ApplyObservation(Sample() with { ExternalCpu = 100 });
        clock.Advance(10);
        resources.ApplyObservation(Sample() with { ExternalCpu = 100 });
        await Pump(ContributionState.Cooldown);
        Check(!loaded && !resources.ServingAllowed, "only sustained measured external use withdraws into cooldown");
        resources.ApplyObservation(unknown with { At = clock.GetUtcNow() });
        for (var i = 0; i < 300; i++) { clock.Advance(1); resources.ApplyObservation(unknown with { At = clock.GetUtcNow() }); }
        Check(resources.ServingAllowed, "missing reports do not extend a real external cooldown");
        loaded = keepLoaded = true;
        await providers.RefreshAsync(providers.View.Endpoints.Single().Id, default);
        resources.ApplyObservation(Sample() with { FreeRam = 0 });
        Check(resources.ServingAllowed && loaded, "memory pressure alone preserves serving and residency");
        resources.ApplyObservation(Sample() with { ExternalCpu = 100 });
        clock.Advance(10);
        resources.ApplyObservation(Sample() with { ExternalCpu = 100 });
        await Pump(ContributionState.Fault);
        loaded = false;
        clock.Advance(1);
        resources.ApplyObservation(Sample());
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(resources.Status.State == ContributionState.Fault, "hard native release failure stays latched even after quiet evidence");
        offline = true;
        resources.RetryRelease();
        await Pump(ContributionState.Fault);
        Check(!resources.ServingAllowed, "missing fresh native evidence cannot release an apparently empty provider");
        offline = false;
        clock.Advance(1);
        resources.ApplyObservation(Sample());
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(resources.Status.State == ContributionState.Fault, "native release errors require explicit retry");
                Check(WindowsObservation.StableWorkers([(1, 10L)], [(1, 10L)]) &&
            !WindowsObservation.StableWorkers([(1, 10L)], [(1, 11L)]) &&
            !WindowsObservation.StableWorkers([(1, 10L)], [(1, 10L), (2, 20L)]) &&
            !WindowsObservation.StableWorkers([(1, 10L)], []), "new, exited and reused workers cannot prove external CPU usage");
        Console.WriteLine("Telemetry fail-open and external cooldown checks passed.");

        async Task Pump(ContributionState expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            do {
                await resources.AdvanceWithdrawalAsync(providers, deadline.Token);
                if (resources.Status.State == expected) return;
                clock.Advance(1);
                resources.ApplyObservation(resources.Observation! with { At = clock.GetUtcNow() });
                await Task.Delay(10, deadline.Token);
            } while (!deadline.IsCancellationRequested);
            throw new InvalidOperationException($"release should reach {expected}");
        }
    }

    private static async Task<(int Tokens, bool Speed)> PromptAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        var request = document.RootElement;
        return (request.GetProperty("messages")[0].GetProperty("content").GetString()!.Count(value => value == ' ') + 3,
            request.GetProperty("max_tokens").GetInt32() == BenchmarkProtocol.SpeedOutput);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}
