using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Mango.Peers;
using Mango.Resources;
using Microsoft.AspNetCore.Http.Features;

namespace Mango.Providers;

internal static class ProviderRoutes
{
    public static void Map(WebApplication app, RouteGroupBuilder local, ProviderCatalog providers, PeerNetwork peers)
    {
        local.MapGet("/providers", () => providers.View);
        local.MapPut("/providers", (EndpointUpdate update, HttpContext context) =>
            HandleAsync(context, async cancellation => await context.Response.WriteAsJsonAsync(
                await providers.ConfigureAsync(update, cancellation), cancellation)));
        local.MapDelete("/providers/{id:guid}", (Guid id, HttpContext context) =>
            CommandAsync(context, cancellation => providers.RemoveAsync(id, cancellation)));
        local.MapPut("/providers/{id:guid}/stop", (Guid id, HttpContext context) =>
            CommandAsync(context, cancellation => providers.StopAsync(id, cancellation)));
        local.MapPut("/providers/refresh", (RefreshRequest request, HttpContext context) =>
            CommandAsync(context, cancellation => providers.RefreshAsync(request.Id, cancellation)));
        local.MapPut("/providers/models", (ModelUpdate update, HttpContext context) =>
            CommandAsync(context, async cancellation =>
            {
                peers.RequireModelAdmin(context.Request);
                await providers.EditAsync(update, cancellation);
                await peers.PublishModelSettingsAsync(context.Request, [update.Id], cancellation);
            }));
        local.MapPut("/providers/ratings", (RatingsRequest request, HttpContext context) =>
            CommandAsync(context, async cancellation =>
            {
                peers.RequireModelAdmin(context.Request);
                var ids = providers.View.Models.Where(model => string.Equals(model.Alias, request.Alias,
                    StringComparison.OrdinalIgnoreCase)).Select(model => model.Id).ToArray();
                await providers.ApplyRatingsAsync(request.Alias ?? "", request.Ratings ?? [], cancellation);
                await peers.PublishModelSettingsAsync(context.Request, ids, cancellation);
            }));
        local.MapPut("/providers/selection", (SelectionRequest request, HttpContext context) =>
            CommandAsync(context, cancellation => providers.SelectAsync(request.Id, cancellation)));
        local.MapPut("/providers/lifecycle", (LifecycleRequest request, HttpContext context) =>
            CommandAsync(context, cancellation => providers.ManageAsync(request.Id, request.Load, cancellation)));

        if (providers.Resources is { } resources)
        {
            local.MapGet("/resources", () => resources.View(providers));
            local.MapPut("/resources/budget", (BudgetUpdate update, HttpContext context) =>
                CommandAsync(context, cancellation => resources.SaveBudgetAsync(update, providers, cancellation)));
            local.MapPut("/resources/retry", (BenchmarkRequest request, HttpContext context) =>
                CommandAsync(context, cancellation => resources.RetryBenchmarkAsync(request.Deployment, request.Operation,
                    providers, cancellation)));
            local.MapPut("/resources/release", () =>
            {
                resources.RetryRelease();
                return Results.NoContent();
            });
        }

        MapInference(app, peers);
    }

    public static void MapInference(WebApplication app, PeerNetwork peers)
    {
        app.MapGet("/v1/models", (HttpContext context) => peers.ModelList(InferenceAccess.Of(context)));
        foreach (var operation in Enum.GetValues<InferenceOperation>())
        {
            app.MapPost(InferencePaths.Path(operation), (HttpContext context) =>
                HandleAsync(context, cancellation => peers.RouteAsync(operation, context, cancellation)));
        }
    }

    private static Task CommandAsync(HttpContext context, Func<CancellationToken, Task> command) =>
        HandleAsync(context, async cancellation =>
        {
            await command(cancellation);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
        });

    private static async Task HandleAsync(HttpContext context, Func<CancellationToken, Task> handler)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
            context.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
            InferenceAccess.Of(context)?.Cancelled ?? CancellationToken.None);
        try
        {
            await handler(cancellation.Token);
        }
        catch (OperationCanceledException) when (InferenceAccess.Of(context)?.Cancelled.IsCancellationRequested == true)
        {
            await FailureAsync(context, 401, "The inference credential was revoked.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (PeerFailure error)
        {
            await FailureAsync(context, error.Status, error.Message);
        }
        catch (ProviderFailure error)
        {
            await FailureAsync(context, error.Status, error.Message);
        }
        catch (BadHttpRequestException error)
        {
            await FailureAsync(context, error.StatusCode, "The inference body was invalid or exceeded its limit.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException
            or System.ComponentModel.Win32Exception or System.Net.Sockets.SocketException)
        {
            await FailureAsync(context, 502, "The provider operation or local configuration write failed.");
        }
    }

    private static async Task FailureAsync(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        if (context.Request.Path.StartsWithSegments("/v1"))
            await context.Response.WriteAsJsonAsync(new { error = new { message, type = status < 500 ? "invalid_request_error" : "provider_error" } });
        else
            await context.Response.WriteAsJsonAsync(new { error = message });
    }

    private sealed record RefreshRequest(Guid? Id);
    private sealed record RatingsRequest(string? Alias, Dictionary<string, int?>? Ratings);
    private sealed record SelectionRequest([property: JsonRequired] string? Id);
    private sealed record LifecycleRequest(string Id, [property: JsonRequired] bool Load);
    private sealed record BenchmarkRequest(string Deployment, InferenceOperation Operation);
}
