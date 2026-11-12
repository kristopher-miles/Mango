using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mango.Peers;
using Mango.Providers;
using Microsoft.AspNetCore.Http.Features;

namespace Mango.Host;

internal sealed class LocalWeb
{
    private const string LoopbackHost = "127.0.0.1";
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public string BrowserUrl(string origin) => $"{origin}/#session={token}";

    public WebApplication Build(int port, string dataDirectory, SnapshotFile<LocalSettings> settings, LoginStartup startup,
        ProviderCatalog providers, PeerNetwork peers, PublicIngress? ingress = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
        });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port);
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false)));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsLocalOrigin(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy =
                "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Content-Type-Options"] = "nosniff";
            var localApi = context.Request.Path.StartsWithSegments("/api/local");
            if (localApi)
            {
                headers.CacheControl = "no-store";
                if (!HasSession(context))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
                    bodyLimit.MaxRequestBodySize = 16 * 1024;
            }
            if (context.Request.Path.StartsWithSegments("/v1"))
            {
                headers.CacheControl = "no-store";
                await peers.InferenceAsync(context, next, HasSession(context));
                return;
            }
            await next(context);
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();

        var defaultRamMiB = LocalSettings.DefaultRamReserveMiB(WindowsMemory.PhysicalBytes());
        var local = app.MapGroup("/api/local");
        local.MapGet("/settings", () =>
        {
            var current = settings.Value;
            return new
            {
                dataDirectory,
                settings = current,
                defaultRamReserveMiB = defaultRamMiB,
                defaultVramReserveMiB = LocalSettings.DefaultVramReserveMiB,
                effectiveRamReserveMiB = current.EffectiveRamReserveMiB(defaultRamMiB),
                effectiveVramReserveMiB = current.EffectiveVramReserveMiB,
                recoveryWarning = settings.RecoveryWarning,
                startupAvailable = startup.Available,
                startsAtLogin = startup.IsEnabled()
            };
        });
        local.MapPut("/settings", async (SettingsUpdate update, CancellationToken cancellationToken) =>
        {
            try
            {
                if (update.Settings is null)
                    return Results.BadRequest(new { error = "Local settings are required." });
                await settings.UpdateAsync(current =>
                {
                    var next = update.Settings;
                    var reduced = next.HasReducedReserve(defaultRamMiB);
                    if (reduced && !current.ReducedReserveConsent && !update.AcceptReducedReserve)
                        throw new ArgumentException("Confirm the reduced memory reserve warning before saving.");
                    return next with
                    {
                        ReducedReserveConsent = current.ReducedReserveConsent || (reduced && update.AcceptReducedReserve)
                    };
                }, cancellationToken);
                return Results.NoContent();
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return Results.Problem(title: "Mango could not save local settings.", detail: error.Message);
            }
        });
        local.MapPut("/startup", (StartupUpdate update) =>
        {
            try
            {
                startup.SetEnabled(update.Enabled);
                return Results.NoContent();
            }
            catch (ArgumentException error)
            {
                return Results.BadRequest(new { error = error.Message });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return Results.Problem(title: "Mango could not change Windows startup.", detail: error.Message);
            }
        });
        PeerRoutes.MapLocal(local, peers);
        ingress?.Map(local);
        ProviderRoutes.Map(app, local, providers, peers);
        return app;
    }

    private static bool IsLocalOrigin(HttpContext context)
    {
        var request = context.Request;
        var localPort = context.Connection.LocalPort;
        if (!IPAddress.Loopback.Equals(context.Connection.RemoteIpAddress) ||
            !IPAddress.Loopback.Equals(context.Connection.LocalIpAddress) ||
            request.Host.Host != LoopbackHost || request.Host.Port != localPort)
            return false;

        var origin = request.Headers.Origin;
        if (origin.Count != 0 && (origin.Count != 1 || origin[0] != $"http://{LoopbackHost}:{localPort}"))
            return false;

        var fetchSite = request.Headers["Sec-Fetch-Site"];
        return fetchSite.Count == 0 || (fetchSite.Count == 1 && fetchSite[0] is "same-origin" or "none");
    }

    private bool HasSession(HttpContext context)
    {
        const string prefix = "Bearer ";
        var header = context.Request.Headers.Authorization;
        return header.Count == 1 && header[0] is { } value &&
            value.Length == prefix.Length + token.Length && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(value[prefix.Length..]), Encoding.UTF8.GetBytes(token));
    }

    private sealed record SettingsUpdate(LocalSettings? Settings, bool AcceptReducedReserve);
    private sealed record StartupUpdate([property: JsonRequired] bool Enabled);
}
