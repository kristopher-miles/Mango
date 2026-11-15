using System.Net;
using System.Text;
using System.Text.Json;
using Mango.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static class GenericProviderChecks
{
    private const string Token = "mango-fixture-token";
    private const string Stream = """
        data: {"id":"fixture","object":"chat.completion.chunk","created":0,"model":"__instance__","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello from the fixture."},"finish_reason":null}]}

        data: {"id":"fixture","object":"chat.completion.chunk","created":0,"model":"__instance__","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: {"id":"fixture","object":"chat.completion.chunk","created":0,"model":"__instance__","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":5,"total_tokens":17}}

        data: [DONE]
        """ + "\n\n";

    public static async Task RunFixtureAsync()
    {
        await using var fixture = await StartFixtureAsync();
        Console.WriteLine($"Generic provider fixture at {fixture.Urls.Single()}");
        await fixture.WaitForShutdownAsync();
    }

    public static async Task RunAsync(string root)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "generic-provider")).FullName;
        await using var fixture = await StartFixtureAsync();
        await using var providers = new ProviderCatalog(directory);
        await providers.InitializeAsync();
        var update = new EndpointUpdate(null, ProviderProfile.Generic, ProviderMode.Managed,
            fixture.Urls.Single(), Token, false, null, null);
        await RejectAsync(400, () => providers.ConfigureAsync(update with { Address = null }, default));
        var endpoint = await providers.ConfigureAsync(update, default);
        await RejectAsync(409, () => providers.ConfigureAsync(update with
        {
            Id = endpoint.Id, ExpectedConfiguration = "stale-ui-configuration"
        }, default));
        Check(endpoint is { Online: true, CanLoad: true, CanUnload: true, CanStop: false, MaxLoadedModels: 1 },
            "managed endpoint advertises one slot and HTTP lifecycle");
        var first = providers.View.Models.Single(model => model.Model.NativeId == "sample-a");
        var second = providers.View.Models.Single(model => model.Model.NativeId == "sample-b");
        Check(first.Alias is null && second.Alias is null, "provider IDs are not user aliases");
        Check(first.Instance is null && second.Instance is null && !first.Model.StructuredOutput, "unloaded catalog and capabilities");
        await providers.EditAsync(new(first.Id, first.Alias, null, 2048, null, null, null, first.Options.Revision), default);
        await providers.EditAsync(new(second.Id, second.Alias, null, 4096, null, null, null, second.Options.Revision), default);
        await providers.ManageAsync(first.Id, load: true, default);
        first = providers.View.Models.Single(model => model.Id == first.Id);
        Check(first.Instance is { Context: 2048 } && first.Instance.Id != first.Model.NativeId,
            "requested context and instance confirmed by inventory");
        await RejectAsync(409, () => providers.ManageAsync(second.Id, load: true, default));

        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"model":"sample-b","messages":[{"role":"user","content":"Hello"}],"max_tokens":32,"stream":true}
            """));
        using var output = new MemoryStream();
        context.Response.Body = output;
        using var request = await providers.ReadInferenceAsync(InferenceOperation.Chat, context, default);
        var placement = providers.Placement(request.Shape, second.Id) ??
            throw new InvalidOperationException("The installed generic model was not considered for loading.");
        var displaced = new List<string>();
        await providers.PreparePlacementAsync(placement, id =>
        {
            displaced.Add(id);
            Check(providers.View.Models.All(model => model.Instance is null), "unload observed before next load");
            return Task.CompletedTask;
        }, default);
        Check(displaced.SequenceEqual([first.Id]), "scheduler displaced the original model");
        var instance = providers.View.Models.Single(model => model.Id == second.Id).Instance ??
            throw new InvalidOperationException("The replacement instance was not observed.");
        var execution = providers.TryStart(request, second.Id);
        var serve = execution.Execute ?? throw new InvalidOperationException(execution.Reason);
        var result = await serve(context, default);
        Check(result is { Settled: true, Failure: null } && context.Response.ContentType!.StartsWith("text/event-stream") &&
            Encoding.UTF8.GetString(output.ToArray()) == Stream.Replace("__instance__", instance.Id),
            "provider model ID routed to the new instance over SSE");
        var resident = providers.View.Models.Single(model => model.Id == second.Id);
        Check(resident.ProviderIdentifiers.SequenceEqual(["sample-b", instance.Id]) &&
            providers.Placement(request.Shape with { Alias = instance.Id }, second.Id) is not null,
            "the provider's distinct loaded-instance ID also routes to the instance");

        await using var restarted = new ProviderCatalog(directory);
        await restarted.InitializeAsync();
        await restarted.RefreshAsync(endpoint.Id, default);
        Check(restarted.View.Endpoints.Single() is { Profile: ProviderProfile.Generic, Online: true },
            "persisted profile and bearer token work after restart");
        await restarted.ManageAsync(second.Id, load: false, default);
        var attached = await restarted.ConfigureAsync(update with
        {
            Id = endpoint.Id, Mode = ProviderMode.Attached, Token = null
        }, default);
        Check(attached is { Online: true, CanLoad: false, CanUnload: false }, "attached endpoint lacks lifecycle authority");
        await RejectAsync(409, () => restarted.ManageAsync(first.Id, load: true, default));
        Console.WriteLine("Generic provider registration, load, swap, streaming and unload checks passed.");
    }

    private static async Task<WebApplication> StartFixtureAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
        var fixture = builder.Build();
        string[] keys = ["sample-a", "sample-b"];
        var gate = new object();
        Loaded? loaded = null;
        var busy = false;

        fixture.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != $"Bearer {Token}")
                context.Response.StatusCode = 401;
            else
                await next(context);
        });
        fixture.MapGet("/api/v1/models", () =>
        {
            lock (gate)
                return new
                {
                    version = "mango-fixture-1",
                    max_loaded_models = 1,
                    models = keys.Select(key => new
                    {
                        key, type = "llm", max_context_length = 8192, size_bytes = 1048576,
                        loaded_instances = loaded is { } current && current.Key == key
                            ? new[] { new { id = current.Id, busy, config = new { context_length = current.Context, parallel = 1 } } }
                            : []
                    }).ToArray()
                };
        });
        fixture.MapPost("/api/v1/models/load", (JsonElement body) =>
        {
            var key = body.Text("model");
            if (key is null || !keys.Contains(key))
                return Results.NotFound();
            var requestedContext = body.Number("context_length");
            if (body.Get("context_length").ValueKind != JsonValueKind.Undefined && requestedContext is not (<= 8192))
                return Results.BadRequest();
            var context = requestedContext ?? 4096;
            lock (gate)
            {
                if (loaded is not null)
                    return Results.Conflict();
                loaded = new(key, $"{key}-{Guid.NewGuid():N}", context);
                return Results.Json(new { type = "llm", instance_id = loaded.Id, status = "loaded",
                    load_config = new { context_length = context, parallel = 1 } });
            }
        });
        fixture.MapPost("/api/v1/models/unload", (JsonElement body) =>
        {
            lock (gate)
            {
                if (loaded is null || loaded.Id != body.Text("instance_id"))
                    return Results.NotFound();
                if (busy)
                    return Results.Conflict();
                var id = loaded.Id;
                loaded = null;
                return Results.Json(new { instance_id = id });
            }
        });
        fixture.MapPost("/v1/chat/completions", async (HttpContext context, JsonElement body) =>
        {
            // shortcut: canned completions exercise the wire protocol, use a real engine to measure inference.
            string id;
            lock (gate)
            {
                if (loaded is null || loaded.Id != body.Text("model") || busy)
                {
                    context.Response.StatusCode = 409;
                    return;
                }
                id = loaded.Id;
                busy = true;
            }
            try
            {
                if (body.IsTrue("stream"))
                {
                    context.Response.ContentType = "text/event-stream";
                    await context.Response.WriteAsync(Stream.Replace("__instance__", id), context.RequestAborted);
                }
                else
                    await context.Response.WriteAsJsonAsync(new
                    {
                        id = "fixture", @object = "chat.completion", created = 0, model = id,
                        choices = new[] { new { index = 0, message = new { role = "assistant", content = "Hello from the fixture." },
                            finish_reason = "stop" } }
                    }, context.RequestAborted);
            }
            finally
            {
                lock (gate)
                    busy = false;
            }
        });
        await fixture.StartAsync();
        return fixture;
    }

    private static async Task RejectAsync(int status, Func<Task> run)
    {
        try { await run(); }
        catch (ProviderFailure error) when (error.Status == status) { return; }
        throw new InvalidOperationException($"Expected generic provider failure HTTP {status}.");
    }

    private static void Check(bool passed, string label)
    {
        if (!passed)
            throw new InvalidOperationException($"Generic provider check failed: {label}");
    }

    private sealed record Loaded(string Key, string Id, int Context);
}
