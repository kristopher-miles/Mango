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
            new { input_tokens = await PromptAsync(context) });
        fixture.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var count = await PromptAsync(context);
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
        Check(resources.Readiness(model, endpoint, Chat.Operation).State == "attached", "estimated limits require attached-provider consent");
        await resources.SaveBudgetAsync(update, providers, default);
        Check(resources.Readiness(model, endpoint, Chat.Operation) is { State: "ready", Available: true } && !resources.HasWork(endpoint.Id),
            "readiness accepts unrated models without reserving resources");
        await resources.SaveBudgetAsync(update with { AllowAttached = false }, providers, default);
        Check(resources.Readiness(model, endpoint, Chat.Operation).State == "attached", "readiness explains attached permission");
        await resources.SaveBudgetAsync(update, providers, default);

        await resources.SaveBudgetAsync(update with { MediaItems = 5, ExecuteRamMiB = 100, AllowOverlap = false, ParallelSlots = 1 }, providers, default);
        var sixMedia = new Workload(InferenceOperation.Chat, 512, 1, 6);
        var projected = resources.TryAdmit(model, endpoint, sixMedia, ResourceWork.Execute, payloadBytes: MiB).Require();
        Check(projected.Cost.RamBytes == 124 * MiB,
            "six media items project five-item measured cost by6/5 and reserve actual JSON bytes separately");
        projected.Complete();
        free = 110 * MiB;
        Tick(2);
        Check(resources.TryAdmit(model, endpoint, sixMedia, ResourceWork.Execute, payloadBytes: MiB).Status == AdmissionStatus.Wait,
            "removing a media eligibility cap still refuses real insufficient memory headroom");
        free = 900 * MiB;
        Tick(2);
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

        Check(resources.TryAdmit(model, managed, Chat, ResourceWork.Load).Status == AdmissionStatus.Wait,
            "a transient load peak can exceed memory even when execution would fit");
        free = 1450 * MiB;
        Tick(2);
        var loading = resources.TryAdmit(model, managed, Chat, ResourceWork.Load).Require();
        foreach (var scenario in new[] { "recovered", "persistent", "pressure", "topology", "idle" })
        {
            var reads = 0;
            using var startup = new ResourceProtection(Directory.CreateDirectory(Path.Combine(directory, "startup-" + scenario)).FullName, settings,
                (_, now) =>
                {
                    reads++;
                    var value = Sample(now);
                    if (scenario == "recovered" && reads > 1) return value;
                    return value with { WorkerAttribution = false, ExternalCpu = null, ExternalGpu = null,
                        FreeRam = scenario == "pressure" && reads > 1 ? 0 : value.FreeRam,
                        Hardware = scenario == "topology" ? null : value.Hardware,
                        Limitation = "fixture attribution unavailable" };
                }, () => ((ulong)Total, (ulong)free), clock);
            await startup.InitializeAsync();
            startup.ApplyObservation(Sample(clock.GetUtcNow()));
            await startup.SaveBudgetAsync(update, providers, default);
            var lease = scenario == "idle" ? null : startup.TryAdmit(model, managed, Chat, ResourceWork.Load).Require();
            var observe = startup.SampleAndApplyAsync(() => providers.View, default);
            if (scenario is "recovered" or "persistent" or "pressure")
            {
                Check(!observe.IsCompleted && startup.TryAdmit(model, managed, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait &&
                    !lease!.Cancellation.IsCancellationRequested && startup.IsAvailable(model, managed, Chat.Operation),
                    "startup retry retains route eligibility but blocks admission and does not cancel the managed load");
            }
            await observe;
            if (scenario == "recovered")
            {
                Check(reads == 2 && startup.Observation!.WorkerAttribution && startup.Status.State == ContributionState.Contributor &&
                    !lease!.Cancellation.IsCancellationRequested, "only a fresh complete observation recovers startup attribution");
                lease!.Complete();
                var execution = startup.TryAdmit(model, managed, Chat, ResourceWork.Execute).Require();
                execution.Complete();
            }
            else
            {
                Check(startup.Status.State == ContributionState.Withdrawing && reads == (scenario == "persistent" ? 4 : scenario == "pressure" ? 2 : 1),
                    "persistent attribution, observed pressure, other invalid telemetry and idle failures retain original withdrawal");
                if (lease is not null)
                {
                    await Task.Delay(25);
                    Check(lease.Cancellation.IsCancellationRequested, "failed startup retry cancels protected work");
                    lease.Complete();
                }
            }
        }
        Check(resources.Readiness(model, endpoint, Chat.Operation).State == "busy", "readiness reports live reservations without taking another");
        Check(resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "the load reservation excludes concurrent inference before the worker allocates");
        loading.Complete();
        free = 900 * MiB;
        Tick(2);

        var racing = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(
            () => resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute))));
        Check(racing.Count(result => result.Status == AdmissionStatus.Admitted) == 1 &&
            racing.Count(result => result.Status == AdmissionStatus.Wait) == 1, "racing reservations spend free RAM once");
        var first = racing.Single(result => result.Reservation is not null).Require();
        worker = 200 * MiB;
        free = 500 * MiB;
        Tick(2);
        Check(resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "a transient worker allocation still leaves the remainder of its peak reserved");
        free = 1450 * MiB;
        Tick(2);
        var second = resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require();
        Check(resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "local qualification and native slots jointly bound overlap");
        first.Complete(settled: false);
        second.Complete();
        Check(resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "cancelled work keeps its reservation until verified idle");
        resources.ConfirmSettled(model.Id);
        var failed = resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require();
        await resources.AllocationFailedAsync(failed);
        failed.Complete();
        Check(resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Unavailable,
            "allocation failure blocks repeated unsafe admission");
        Check(resources.Readiness(model, endpoint, Chat.Operation).State == "allocation", "readiness points to explicit allocation recovery");
        using (var reloaded = new ResourceProtection(directory, settings, (_, now) => Sample(now), () => ((ulong)Total, (ulong)free), clock))
        {
            await reloaded.InitializeAsync();
            reloaded.ApplyObservation(Sample(clock.GetUtcNow()));
            Check(reloaded.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Unavailable,
                "allocation failure survives restart");
        }
        await resources.SaveBudgetAsync(update, providers, default);

        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
        Tick(301);
        resources.AdvanceBenchmarks(providers, default);
        Check(calls == 0 && Profile(model.Id).ValueKind == JsonValueKind.Null, "Never skips automatic benchmarks");
        var conservative = resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Require();
        Check(resources.TryAdmit(model, endpoint, Chat, ResourceWork.Execute).Status == AdmissionStatus.Wait,
            "Never uses single-execution admission");
        conservative.Complete();

        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.WhenConvenient });
        await resources.ClientArrivedAsync(default);
        Tick(299);
        resources.AdvanceBenchmarks(providers, default);
        Check(calls == 0, "convenient benchmark waits for five local idle minutes");
        blockCall = 2;
        Tick(1);
        resources.AdvanceBenchmarks(providers, default);
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
        Check(!resources.ServingAllowed, "Now closes serving immediately until profiles are checked");
        resources.AdvanceBenchmarks(providers, default);
        Check(resources.ServingAllowed, "Now reuses complete matching evidence");
        extra = true;
        await providers.RefreshAsync(endpoint.Id, default);
        resources.AdvanceBenchmarks(providers, default);
        Check(!resources.ServingAllowed, "Now closes serving after a new model appears");
        var added = providers.View.Models.Single(item => item.Id != model.Id);
        await resources.SaveBudgetAsync(update with { Deployment = added.Id }, providers, default);
        await EventuallyAsync(() => resources.ServingAllowed);
        Check(calls == 9, "new deployments inherit the local policy");
        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Never });
        await resources.RetryBenchmarkAsync(model.Id, Chat.Operation, providers, default);
        await EventuallyAsync(() => Profile(model.Id).ValueKind != JsonValueKind.Null && Profile(model.Id).GetProperty("complete").GetBoolean());
        Check(calls == 13, "Never permits an explicitly requested benchmark");
        await settings.UpdateAsync(value => value with { BenchmarkPolicy = BenchmarkPolicy.Now });
        await resources.SaveBudgetAsync(update with { Items = 2 }, providers, default);
        Check(!resources.ServingAllowed && Profile(model.Id).ValueKind == JsonValueKind.Null &&
            Profile(added.Id).GetProperty("complete").GetBoolean(), "a workload change invalidates only its own evidence");
        runtime = "different";
        await providers.RefreshAsync(endpoint.Id, default);
        resources.AdvanceBenchmarks(providers, default);
        Check(!resources.ServingAllowed && Profile(model.Id).ValueKind == JsonValueKind.Null,
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
        await FinishWithdrawalAsync(ContributionState.Cooldown);
        Console.WriteLine("Resource admission and benchmark checks passed.");

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
        var directory = Directory.CreateDirectory(Path.Combine(root, "lmstudio-benchmark")).FullName;
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor,
            BenchmarkPolicy = BenchmarkPolicy.WhenConvenient });
        var clock = new Clock();
        var calls = 0;
        var loads = 0;
        var unloads = 0;
        var loaded = true;
        var slowUnload = false;
        var keepLoaded = false;
        var endpointId = Guid.Empty;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        await using var fixture = builder.Build();
        fixture.MapGet("/api/v1/models", () => new { models = new[] { new {
            key = "bounded", type = "llm", max_context_length = 32768, size_bytes = MiB,
            loaded_instances = loaded ? new[] { new { id = "bounded-live", config = new { context_length = 512, parallel = 1 } } } : []
        } } });
        fixture.MapPost("/api/v1/models/unload", async () => {
            unloads++; loaded = keepLoaded;
            if (slowUnload) await Task.Delay(TimeSpan.FromSeconds(7));
            return Results.Ok();
        });
        fixture.MapPost("/api/v1/models/load", () => { loads++; loaded = true; return Results.Ok(); });
        fixture.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var tokens = await PromptAsync(context);
            Interlocked.Increment(ref calls);
            context.Response.ContentType = "text/event-stream";
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
        resources.ApplyObservation(Sample());
        resources.AdvanceBenchmarks(providers, default);
        clock.Advance(301);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        JsonElement Profile() => JsonSerializer.SerializeToElement(resources.View(providers), SnapshotFile<ResourceData>.Json)
            .GetProperty("models")[0].GetProperty("profile");
        while (Profile().ValueKind == JsonValueKind.Null || Profile().GetProperty("measurements").GetArrayLength() < 4)
        {
            resources.ApplyObservation(Sample());
            resources.AdvanceBenchmarks(providers, deadline.Token);
            await Task.Delay(25, deadline.Token);
        }
        Check(calls == 4 && loads == 0 && unloads == 0 && loaded,
            "idle LM Studio qualification measures warm passes without replacing the loaded configuration");
        clock.Advance(301);
        resources.ApplyObservation(Sample());
        resources.AdvanceBenchmarks(providers, deadline.Token);
        await Task.Delay(100, deadline.Token);
        Check(calls == 4 && !Profile().GetProperty("complete").GetBoolean(),
            "deferred cold qualification stays incomplete and does not repeat warm work");
        await settings.UpdateAsync(value => value with { Role = LocalRole.Leech });
        while (resources.Status.State == ContributionState.Withdrawing)
        {
            await resources.AdvanceWithdrawalAsync(providers, deadline.Token);
            resources.ApplyObservation(Sample());
            await Task.Delay(25, deadline.Token);
        }
        Check(!loaded && unloads == 1 && resources.Status.State == ContributionState.ManualLeech,
            "resource withdrawal still unloads the same protected LM Studio instance");
        loaded = true;
        slowUnload = true;
        await providers.RefreshAsync(endpointId, default);
        var released = await providers.ReleaseManagedAsync(default);
        Check(released.Error is null && !loaded && unloads == 2,
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
        resources.ApplyObservation(unknown);
        await Pump(ContributionState.Fault);
        Check(!loaded && !resources.ServingAllowed, "native unload with missing process evidence latches a verification fault");
        var count = inspections;
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(inspections == count, "same fault sample cannot initiate recovery");
        clock.Advance(1);
        resources.ApplyObservation(unknown with { At = clock.GetUtcNow() });
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(resources.Status.State == ContributionState.Fault, "continuing unknown attribution stays closed");
        resources.ApplyObservation(Sample() with { ExternalCpu = 100 });
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(resources.Status.State == ContributionState.Fault, "busy external activity stays closed");
        resources.ApplyObservation(Sample() with { At = clock.GetUtcNow().AddSeconds(-1) });
        Check(resources.Observation!.ExternalCpu == 100, "out of order quiet evidence cannot replace newer activity");
        resources.ApplyObservation(Sample() with { At = clock.GetUtcNow().AddSeconds(1) });
        Check(resources.Observation!.ExternalCpu == 100, "future quiet evidence cannot replace current activity");
        resources.ApplyObservation(Sample());
        clock.Advance(settings.Value.SampleSeconds + 6);
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(resources.Status.State == ContributionState.Fault, "stale quiet evidence cannot initiate recovery");
        await settings.UpdateAsync(value => value with { Role = LocalRole.Leech });
        resources.ApplyObservation(Sample());
        await resources.AdvanceWithdrawalAsync(providers, default);
        Check(resources.Status.State == ContributionState.Fault, "operator leech hold prevents automatic recovery");
        await settings.UpdateAsync(value => value with { Role = LocalRole.Contributor });
        await Pump(ContributionState.Cooldown);
        Check(!resources.ServingAllowed, "fresh attributable and native empty evidence enters cooldown without opening ingress");
        resources.ApplyObservation(Sample());
        clock.Advance(299);
        resources.ApplyObservation(Sample());
        Check(!resources.ServingAllowed, "a gap in quiet samples does not bypass cooldown");
        for (var i = 0; i < 300; i++) { clock.Advance(1); resources.ApplyObservation(Sample()); }
        Check(resources.ServingAllowed, "normal five minute quiet cooldown restores contribution");
        loaded = keepLoaded = true;
        await providers.RefreshAsync(providers.View.Endpoints.Single().Id, default);
        resources.ApplyObservation(Sample() with { WorkerAttribution = false });
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
        Console.WriteLine("Transient release recovery checks passed.");

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

    private static async Task<int> PromptAsync(HttpContext context)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
        return document.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Count(value => value == ' ') + 3;
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
