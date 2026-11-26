using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Mango.Host;
using Mango.Providers;
using Mango.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static class StartupRecoveryChecks
{
    private const long GiB = 1024L * 1024 * 1024;
    private const string Shutdown = "A provider was still settling during shutdown.";

    public static async Task RunAsync(string root)
    {
        await CheckMissingLoadAsync(Path.Combine(root, "startup-missing"), true);
        await CheckMissingLoadAsync(Path.Combine(root, "startup-interrupted"), false);
        await CheckMissingLoadAsync(Path.Combine(root, "startup-surviving-worker"), true, liveWorker: true);
        await CheckResidentAsync(Path.Combine(root, "startup-resident"));
        Console.WriteLine("Offline startup observation and renewable reservation checks passed.");
    }

    private static async Task CheckMissingLoadAsync(string directory, bool removeArtifact, bool liveWorker = false)
    {
        var settings = await SettingsAsync(directory);
        var warm = new Inventory("warm-fixture");
        await using var server = await FixtureAsync(warm);
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        var gguf = Path.Combine(directory, "startup-fixture.gguf");
        await File.WriteAllTextAsync(gguf, "inventory placeholder");
        Guid ownedId, warmId;
        string deployment;
        await using (var setup = new ProviderCatalog(directory))
        {
            await setup.InitializeAsync();
            ownedId = (await setup.ConfigureAsync(new(null, ProviderProfile.LlamaSingle, ProviderMode.Managed,
                $"http://127.0.0.1:{port}", null, false, Path.Combine(directory, "llama-server.exe"), gguf), default)).Id;
            deployment = setup.View.Models.Single(model => model.EndpointId == ownedId).Id;
            warmId = (await setup.ConfigureAsync(new(null, ProviderProfile.Generic, ProviderMode.Managed,
                server.Urls.Single(), null, false, null, null), default)).Id;
        }
        if (removeArtifact)
            File.Delete(gguf);
        var stored = new SnapshotFile<ResourceData>(Path.Combine(directory, "resources.json"), _ => null);
        var missingAndStopped = removeArtifact && !liveWorker;
        var evidence = new PendingRelease(ownedId, deployment, liveWorker ? [Environment.ProcessId] : [], Loading: true);
        var hold = missingAndStopped ? new ResourceHold(ContributionState.Fault, ProviderCatalog.ReleaseDeadline, [evidence]) :
            new ResourceHold(ContributionState.Withdrawing, Shutdown, [evidence]);
        await stored.LoadAsync(ResourceData.Empty with { Hold = hold });
        using var resources = Protection(directory, settings);
        await resources.InitializeAsync(requireStartupInspection: true);
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        Check(!resources.ServingAllowed, "startup does not expose uninspected capacity");
        await resources.InspectStartupAsync(providers, default);
        Check(resources.Status.State == ContributionState.Contributor && !resources.HasWork(ownedId) &&
            !resources.HasWork(warmId), "startup reconciles without reviving a resource reservation");
        Check(providers.StartupEvidence.Length == (missingAndStopped ? 0 : 1),
            "only missing files plus verified absence clear the old load");
        Check(providers.View.Models.Single(item => item.Id == deployment) is { Online: false } configured &&
            configured.Model.Installed == !removeArtifact, "offline configured model keeps accurate installed diagnostic");
        var resident = providers.View.Models.Single(model => model.EndpointId == warmId);
        Check(resident.Instance is not null && warm.Lifecycle == 0, "an idle resident is reused at startup");
        using var payload = await PayloadAsync(warm.Key);
        using var response = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = response;
        var started = providers.TryStart(payload, resident.Id);
        Check(started.Execute is not null && resources.HasWork(warmId), "only a fresh safe admission takes a new reservation");
        Check((await started.Execute!(context, default)).Failure is null && warm.Requests == 1 &&
            !resources.HasWork(warmId) && warm.Lifecycle == 0,
            "fresh request executes once on the existing instance and settles its new reservation");
        await stored.LoadAsync(ResourceData.Empty);
        Check(missingAndStopped ? stored.Value.Hold is null : stored.Value.Hold?.Pending?.Single().Loading == true,
            "only unresolved evidence remains durable after startup");
    }

    private static async Task CheckResidentAsync(string directory)
    {
        var settings = await SettingsAsync(directory);
        var native = new Inventory("resident-fixture") { Busy = true };
        await using var server = await FixtureAsync(native);
        Guid endpoint;
        string deployment;
        await using (var setup = new ProviderCatalog(directory))
        {
            await setup.InitializeAsync();
            endpoint = (await setup.ConfigureAsync(new(null, ProviderProfile.Generic, ProviderMode.Attached,
                server.Urls.Single(), null, false, null, null), default)).Id;
            deployment = setup.View.Models.Single().Id;
        }
        using var resources = Protection(directory, settings);
        await resources.InitializeAsync();
        await using var providers = new ProviderCatalog(directory, resources);
        await providers.InitializeAsync();
        await resources.InspectStartupAsync(providers, default);
        using var request = await PayloadAsync(native.Key);
        Check(providers.StartupEvidence.Single().Deployment == deployment &&
            providers.View.Models.Single().Instance is not null && !resources.HasWork(endpoint) &&
            providers.TryStart(request, deployment).Status == AdmissionStatus.Wait,
            "a fresh busy native resident remains fenced without disk history or an old reservation");
        await resources.AdvanceWithdrawalAsync(providers, default);
        native.FailInventory = true;
        await providers.RefreshAsync(endpoint, default);
        Check(providers.StartupEvidence.Length == 1 && providers.TryStart(request, deployment).Execute is null,
            "failed inventory does not settle a previously observed live operation");
        using (var reopened = Protection(directory, settings))
        {
            await reopened.InitializeAsync();
            await using var reopenedProviders = new ProviderCatalog(directory, reopened);
            await reopenedProviders.InitializeAsync();
            await reopened.InspectStartupAsync(reopenedProviders, default);
            Check(reopened.ServingAllowed && !reopened.HasWork(endpoint) && reopenedProviders.StartupEvidence.Length == 1 &&
                reopenedProviders.View.Endpoints.Single().Online == false,
                "failed initial inventory scopes historical uncertainty to the affected provider");
        }
        native.FailInventory = false;
        native.Busy = null;
        await providers.RefreshAsync(endpoint, default);
        Check(providers.View.Models.Single().Online && providers.TryStart(request, deployment).Status == AdmissionStatus.Wait,
            "reachable inventory without verified idle status stays fenced");
        native.Busy = false;
        await providers.RefreshAsync(endpoint, default);
        Check(providers.StartupEvidence.Length == 0 && !resources.HasWork(endpoint) && native.Lifecycle == 0 &&
            providers.View.Models.Single().Instance is not null, "verified idle clears the fence and keeps the resident");
    }

    private static async Task<SnapshotFile<LocalSettings>> SettingsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        await settings.LoadAsync(new LocalSettings { Role = LocalRole.Contributor, BenchmarkPolicy = BenchmarkPolicy.Never });
        return settings;
    }

    private static ResourceProtection Protection(string directory, SnapshotFile<LocalSettings> settings) =>
        new(directory, settings, (_, now) => new(now, 16 * GiB, 12 * GiB, 0, 0, [], "fixture", [], true, null),
            () => ((ulong)(16 * GiB), (ulong)(12 * GiB)));

    private static async Task<InferenceRequest> PayloadAsync(string model)
    {
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "fixture" })
        };
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = body;
        return await InferenceRequest.ReadAsync(InferenceOperation.Chat, context.Request, default);
    }

    private static async Task<WebApplication> FixtureAsync(Inventory state)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapGet("/api/v1/models", state.Read);
        app.MapPost("/api/v1/models/load", state.RejectLifecycle);
        app.MapPost("/api/v1/models/unload", state.RejectLifecycle);
        app.MapPost("/v1/chat/completions", state.Complete);
        await app.StartAsync();
        return app;
    }

    private sealed class Inventory(string key)
    {
        public string Key => key;
        public bool? Busy { get; set; } = false;
        public bool FailInventory { get; set; }
        public int Lifecycle;
        public int Requests;

        public IResult Read()
        {
            if (FailInventory) return Results.StatusCode(503);
            var instance = new Dictionary<string, object>
            {
                ["id"] = key, ["config"] = new { context_length = 4096, parallel = 1 }
            };
            if (Busy is { } busy) instance["busy"] = busy;
            return Results.Json(new { models = new[] { new { key, type = "llm", loaded_instances = new[] { instance } } } });
        }

        public IResult RejectLifecycle()
        {
            Interlocked.Increment(ref Lifecycle);
            return Results.StatusCode(409);
        }

        public IResult Complete()
        {
            Interlocked.Increment(ref Requests);
            return Results.Json(new { id = "fixture-success" });
        }
    }

    private static void Check(bool result, string description)
    {
        if (!result) throw new InvalidOperationException($"Startup recovery check failed: {description}");
    }
}
