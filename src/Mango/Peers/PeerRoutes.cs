using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mango.Providers;
using Microsoft.AspNetCore.Http.Features;

namespace Mango.Peers;

internal static class PeerRoutes
{
    public static void MapLocal(RouteGroupBuilder local, PeerNetwork peers)
    {
        var network = local.MapGroup("/network");
        network.MapGet("", (HttpRequest request) => peers.View(request));
        network.MapGet("/scheduling", () => peers.SchedulingView());
        network.MapPut("/queue", (QueueSettings input, HttpContext context) =>
            HandleAsync(context, token => peers.SetQueuePolicyAsync(context.Request, input.WaitSeconds, token)));
        network.MapPut("/discover", (DiscoveryRequest input, HttpContext context) =>
            HandleAsync(context, token => peers.DiscoverAsync(input.Address, token)));
        network.MapPut("/refresh", (HttpContext context) => HandleAsync(context, peers.RefreshAsync));
        network.MapPut("/create", (CreateNetworkRequest input, HttpContext context) =>
            HandleAsync(context, token => peers.CreateAsync(input, token)));
        network.MapPut("/join", (JoinNetworkRequest input, HttpContext context) =>
            HandleAsync(context, token => peers.JoinAsync(input, token)));
        network.MapPut("/unlock", (UnlockRequest input, HttpContext context) =>
            HandleAsync(context, token => peers.UnlockAsync(input, context.Connection.RemoteIpAddress!, token)));
        network.MapPut("/recover", (RecoverRequest input, HttpContext context) =>
            HandleAsync(context, token => peers.RecoverAsync(input, token)));
        network.MapPut("/reset", (ResetRequest input, HttpContext context) =>
            HandleAsync(context, token => peers.ResetAsync(context.Request, input, token)));
        network.MapPost("/keys", (KeyIssueRequest input, HttpContext context) =>
            HandleAsync(context, token => peers.IssueAsync(context.Request, input, token)));
        network.MapDelete("/keys/{id:guid}", (Guid id, HttpContext context) =>
            HandleAsync(context, token => peers.RevokeAsync(context.Request, id, token)));
        network.MapDelete("/nodes/{id:guid}", (Guid id, HttpContext context) =>
            HandleAsync(context, token => peers.RemoveAsync(context.Request, id, token)));
    }

    public static async Task<WebApplication> StartAsync(PeerNetwork peers, IPAddress[] addresses, int port, CancellationToken cancellation)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.MaxConcurrentConnections = 1536;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestBodySize = PeerLimits.ControlBytes;
            foreach (var address in addresses)
                options.Listen(address, port);
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (!addresses.Contains(context.Connection.LocalIpAddress) || !Tailnet.Contains(context.Connection.RemoteIpAddress) ||
                request.Host.Port != port || !IPAddress.TryParse(request.Host.Host.Trim('[', ']'), out var host) ||
                !host.Equals(context.Connection.LocalIpAddress) || request.Headers.Origin.Count != 0 ||
                request.Headers.ContainsKey("Sec-Fetch-Site"))
            {
                context.Response.StatusCode = 403;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            if (request.Path.StartsWithSegments("/v1"))
            {
                await peers.InferenceAsync(context, next, localSession: false);
                return;
            }
            if (!request.Path.StartsWithSegments("/peer"))
            {
                context.Response.StatusCode = 404;
                return;
            }
            if (request.Path.Equals("/peer/info", StringComparison.OrdinalIgnoreCase) ||
                request.Path.Equals("/peer/enroll", StringComparison.OrdinalIgnoreCase))
            {
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = 4096;
                await next(context);
                return;
            }
            var access = peers.Authenticate(request);
            if (access is null)
            {
                context.Response.StatusCode = 401;
                return;
            }
            peers.SetResponseHeaders(context.Response);
            if (access.Notice is { } notice)
            {
                context.Response.StatusCode = 409;
                await context.Response.WriteAsJsonAsync(notice, context.RequestAborted);
                return;
            }
            context.Items[typeof(PeerAuthentication)] = access;
            var route = request.Path.Value ?? "";
            peers.ObservePeer(access.Node, request, refresh: route is "/peer/ping" ||
                route.StartsWith("/peer/key/", StringComparison.Ordinal) || route.StartsWith("/peer/inference/", StringComparison.Ordinal));
            await next(context);
        });

        var peer = app.MapGroup("/peer");
        peer.MapGet("/info", () => peers.Introduction() is { } info ? Results.Json(info) : Results.NotFound());
        peer.MapPost("/enroll", (EnrollmentRequest input, HttpContext context) =>
            HandleAsync(context, token => peers.AcceptEnrollmentAsync(input, context.Connection.RemoteIpAddress!, token)));
        peer.MapGet("/ping", () => Results.NoContent());
        peer.MapGet("/versions", () => new PeerVersions(peers.Snapshot.Versions));
        peer.MapPost("/pull", (PeerVersions known) => PeerNetwork.ValidVersions(known.Records)
            ? Results.Json(peers.Snapshot.Records.Delta(known.Records)) : Results.BadRequest());
        peer.MapPost("/push", (NetworkRecords incoming, HttpContext context) =>
            HandleAsync(context, token => peers.MergeAsync(incoming, token)));
        peer.MapPost("/notice", (PeerNotice notice, HttpContext context) =>
            HandleAsync(context, token => peers.ApplyNoticeAsync(notice, token)));
        peer.MapGet("/key/{id:guid}", (Guid id) => new PeerKeyReply(peers.Snapshot.Keys.GetValueOrDefault(id)));
        peers.MapAttemptRoutes(peer);
        ProviderRoutes.MapInference(app, peers);
        try
        {
            await app.StartAsync(cancellation);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static Task HandleAsync<T>(HttpContext context, Func<CancellationToken, Task<T>> action) =>
        HandleAsync(context, async token => await context.Response.WriteAsJsonAsync(await action(token), token), noContent: false);

    private static async Task HandleAsync(HttpContext context, Func<CancellationToken, Task> action, bool noContent = true)
    {
        try
        {
            await action(context.RequestAborted);
            if (noContent)
                context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (PeerFailure error)
        {
            context.Response.StatusCode = error.Status;
            await context.Response.WriteAsJsonAsync(new { error = error.Message }, context.RequestAborted);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "Mango could not save network state." }, context.RequestAborted);
        }
    }

    private sealed record DiscoveryRequest(string? Address);
    private sealed record QueueSettings(int WaitSeconds);
}
