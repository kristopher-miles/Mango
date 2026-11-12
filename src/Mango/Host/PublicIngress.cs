using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Mango.Peers;
using Mango.Providers;

namespace Mango.Host;

internal sealed record PublicIngressSettings([property: JsonRequired] bool Enabled, [property: JsonRequired] int Port,
    string? TrustedProxy, string? HttpsOrigin)
{
    public static readonly PublicIngressSettings Off = new(false, 53782, null, null);
}

internal sealed class PublicIngress(string directory, PeerNetwork peers) : IAsyncDisposable
{
    private const string CheckHeader = "Mango-Ingress";
    private readonly string check = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly SnapshotFile<PublicIngressSettings> store = new(Path.Combine(directory, "public-ingress.json"), Validate);
    private readonly SemaphoreSlim writer = new(1);
    private readonly HttpClient probe = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
    { Timeout = TimeSpan.FromSeconds(5) };
    private WebApplication? listener;
    private string? error;
    private ProbeResult? lastCheck;

    public object View()
    {
        var settings = store.Value;
        return new
        {
            Settings = settings,
            Listening = Volatile.Read(ref listener) is not null,
            Upstream = $"http://127.0.0.1:{settings.Port}",
            Error = Volatile.Read(ref error) ?? store.RecoveryWarning,
            LastCheck = Volatile.Read(ref lastCheck)
        };
    }

    public async Task InitializeAsync()
    {
        await store.LoadAsync(PublicIngressSettings.Off);
        if (!store.Value.Enabled)
            return;
        try { listener = await StartAsync(store.Value.Port, CancellationToken.None); }
        catch (IOException) { error = $"Mango could not bind the public inference upstream on port {store.Value.Port}."; }
    }

    public async Task ConfigureAsync(PublicIngressSettings input, CancellationToken cancellation)
    {
        var next = input with { TrustedProxy = Blank(input.TrustedProxy), HttpsOrigin = Blank(input.HttpsOrigin) };
        if (Validate(next) is { } invalid)
            throw new PeerFailure(400, invalid);
        if (next.HttpsOrigin is { } origin)
            next = next with { HttpsOrigin = new Uri(origin).GetLeftPart(UriPartial.Authority) };
        await writer.WaitAsync(cancellation);
        try
        {
            var previous = listener;
            var keep = next.Enabled && previous is not null && next.Port == store.Value.Port;
            WebApplication? replacement = null;
            try
            {
                if (next.Enabled && !keep)
                {
                    try { replacement = await StartAsync(next.Port, cancellation); }
                    catch (IOException) { throw new PeerFailure(409, $"Mango could not bind the public inference upstream on port {next.Port}."); }
                }
                await store.UpdateAsync(_ => next, cancellation);
            }
            catch
            {
                await StopAsync(replacement);
                throw;
            }
            Volatile.Write(ref error, null);
            Volatile.Write(ref lastCheck, null);
            if (!keep)
            {
                Volatile.Write(ref listener, replacement);
                await StopAsync(previous);
            }
        }
        finally { writer.Release(); }
    }

    public async Task<object> CheckAsync(CancellationToken cancellation)
    {
        var settings = store.Value;
        if (Volatile.Read(ref listener) is null || settings.HttpsOrigin is null)
            throw new PeerFailure(409, "Enable the upstream and enter its HTTPS origin before checking.");
        var reached = false;
        try
        {
            using var response = await probe.GetAsync(new Uri(new Uri(settings.HttpsOrigin), "/v1/models"),
                HttpCompletionOption.ResponseHeadersRead, cancellation);
            reached = response.StatusCode == HttpStatusCode.Unauthorized &&
                response.Headers.TryGetValues(CheckHeader, out var values) && values.SequenceEqual([check]);
        }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        var result = new ProbeResult(DateTimeOffset.UtcNow, reached);
        if (ReferenceEquals(settings, store.Value))
            Volatile.Write(ref lastCheck, result);
        return result;
    }

    public void Map(RouteGroupBuilder local)
    {
        var route = local.MapGroup("/public-ingress");
        route.MapGet("", View);
        route.MapPut("", (PublicIngressSettings input, HttpContext context) => PeerRoutes.HandleAsync(context,
            token => ConfigureAsync(input, token)));
        route.MapPost("/check", (HttpContext context) => PeerRoutes.HandleAsync(context, CheckAsync));
    }

    private async Task<WebApplication> StartAsync(int port, CancellationToken cancellation)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxConcurrentConnections = 256;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.MaxRequestBodySize = ProviderLimits.MaxRequestBytes;
            options.Limits.MaxRequestBufferSize = 256 * 1024;
            options.Limits.MaxResponseBufferSize = 64 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(1);
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var settings = store.Value;
            if (!settings.Enabled || settings.Port != port || !PeerNetwork.IsInferenceRequest(context.Request))
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers[CheckHeader] = check;
            InferenceSource.Set(context, InferenceSource.FromProxy(context, settings.TrustedProxy));
            await peers.InferenceAsync(context, next, localSession: false);
        });
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

    private static async Task StopAsync(WebApplication? app)
    {
        if (app is null)
            return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        finally { await app.DisposeAsync(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(listener);
        probe.Dispose();
        writer.Dispose();
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Validate(PublicIngressSettings settings)
    {
        if (settings.Port is < 1024 or > 65535)
            return "Use an upstream port from 1024 to 65535.";
        if (settings.TrustedProxy is { } address &&
            (InferenceSource.Parse(address) is not { } ip || !IPAddress.IsLoopback(ip) ||
                ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            return "Enter the local IPv4 loopback address of the proxy or leave it blank.";
        if (settings.HttpsOrigin is { } origin && (origin.Length > 512 ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Port is < 1 or > 65535 ||
            url.UserInfo.Length != 0 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0))
            return "Enter an HTTPS origin without credentials, paths, queries, or fragments.";
        return null;
    }

    private sealed record ProbeResult(DateTimeOffset At, bool Reached);
}
