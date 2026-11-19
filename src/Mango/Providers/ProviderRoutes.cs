using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Mango.Peers;
using Mango.Logging;
using Mango.Resources;
using Microsoft.AspNetCore.Http.Features;

namespace Mango.Providers;

internal static class ProviderRoutes
{
    private static readonly SemaphoreSlim folderDialog = new(1);

    public static void Map(WebApplication app, RouteGroupBuilder local, ProviderCatalog providers, PeerNetwork peers)
    {
        local.MapGet("/providers", () => providers.View);
        local.MapPut("/providers", (EndpointUpdate update, HttpContext context) =>
            HandleAsync(context, async cancellation => await context.Response.WriteAsJsonAsync(
                await peers.ConfigureLocalProviderAsync(context.Request, update, cancellation), cancellation)));
        local.MapPost("/providers/strata/folder", (HttpContext context) =>
            HandleAsync(context, async cancellation => await context.Response.WriteAsJsonAsync(
                new { Folder = await PickStrataFolderAsync(cancellation) }, cancellation)));
        local.MapDelete("/providers/{id:guid}", (Guid id, HttpContext context) =>
            CommandAsync(context, cancellation => peers.RemoveLocalProviderAsync(context.Request, id, cancellation)));
        local.MapPut("/providers/{id:guid}/stop", (Guid id, HttpContext context) =>
            CommandAsync(context, cancellation => providers.StopAsync(id, cancellation)));
        local.MapPut("/providers/refresh", (RefreshRequest request, HttpContext context) =>
            CommandAsync(context, cancellation => providers.RefreshAsync(request.Id, cancellation)));
        local.MapPut("/providers/models", (ModelUpdate update, HttpContext context) =>
            CommandAsync(context, cancellation => peers.EditLocalModelAsync(context.Request, update, cancellation)));
        local.MapPut("/providers/models/{id}/serving", (string id, ModelServingUpdate update, HttpContext context) =>
            CommandAsync(context, cancellation => providers.SetServingAsync(id, update.Enabled, cancellation)));
        local.MapPut("/providers/models/{id}/yolo", (string id, ModelYoloUpdate update, HttpContext context) =>
            CommandAsync(context, cancellation => providers.SetYoloAsync(id, update, cancellation)));
        local.MapPut("/providers/{id:guid}/serving", (Guid id, ModelServingUpdate update, HttpContext context) =>
            CommandAsync(context, cancellation => providers.SetProviderServingAsync(id, update.Enabled, cancellation)));
        local.MapPut("/providers/ratings", (RatingsRequest request, HttpContext context) =>
            CommandAsync(context, cancellation => peers.EditRatingsAsync(context.Request, request.Alias ?? "", request.Ratings ?? [], cancellation)));
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
                    providers, cancellation, request.SampleOnly)));
            local.MapPut("/resources/benchmark-missing", (HttpContext context) =>
                PeerRoutes.HandleAsync(context, token => context.Response.WriteAsJsonAsync(
                    new { Queued = resources.BenchmarkMissing(providers) }, token), noContent: false));
            local.MapPut("/resources/release", () =>
            {
                resources.RetryRelease();
                return Results.NoContent();
            });
        }

        MapInference(app, peers);
    }

    private static async Task<string?> PickStrataFolderAsync(CancellationToken cancellation)
    {
        if (!await folderDialog.WaitAsync(0, cancellation))
            throw new ProviderFailure(409, "A Strata folder picker is already open.");
        var selected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Choose a local Strata folder", UseDescriptionForTitle = true, ShowNewFolderButton = false
                };
                selected.TrySetResult(dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null);
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                selected.TrySetException(new ProviderFailure(503, "Enter the folder path or open Mango in the Windows desktop session."));
            }
            finally { folderDialog.Release(); }
        }) { IsBackground = true };
        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch
        {
            folderDialog.Release();
            throw;
        }
        return await selected.Task.WaitAsync(cancellation);
    }

    public static void MapInference(WebApplication app, PeerNetwork peers)
    {
        app.MapGet("/v1/models", (HttpContext context) => peers.ModelList(InferenceAccess.Of(context)));
        foreach (var operation in Enum.GetValues<InferenceOperation>())
        {
            app.MapPost(InferencePaths.Path(operation), (HttpContext context) =>
                peers.CaptureInferenceAsync(operation, context,
                    () => HandleAsync(context, cancellation => peers.RouteAsync(operation, context, cancellation))));
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
            await FailureAsync(context, error.Status, error.Message, error.Code);
        }
        catch (ProviderFailure error)
        {
            await FailureAsync(context, error.Status, error.Message, error.Code);
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

    private static async Task FailureAsync(HttpContext context, int status, string message, string? code = null)
    {
        if (LogCapture.Of(context) is { } capture)
            capture.Failure = message;
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
            await context.Response.WriteAsJsonAsync(new { error = message, code });
    }

    private sealed record RefreshRequest(Guid? Id);
    private sealed record RatingsRequest(string? Alias, Dictionary<string, int?>? Ratings);
    private sealed record SelectionRequest([property: JsonRequired] string? Id);
    private sealed record LifecycleRequest(string Id, [property: JsonRequired] bool Load);
    private sealed record BenchmarkRequest(string Deployment, InferenceOperation Operation, bool SampleOnly = false);
}
