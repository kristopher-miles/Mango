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
        await resources.SaveBudgetAsync(update, providers, default);

        var managed = endpoint with { Mode = ProviderMode.Managed, CanLoad = true, CanUnload = true };
        Check(resources.TryAdmit(model, managed, Chat, ResourceWork.Load).Status == AdmissionStatus.Wait,
            "a transient load peak can exceed memory even when execution would fit");
        free = 1450 * MiB;
        Tick(2);
        var loading = resources.TryAdmit(model, managed, Chat, ResourceWork.Load).Require();
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
        resumed.RetryRelease();
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
