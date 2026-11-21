using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Mango.Host;
using Microsoft.AspNetCore.WebUtilities;

namespace Mango.Peers;

internal sealed record AdminLogin(string? Password, string? Code);
internal sealed record AdminAccess(string Token, DateTimeOffset ExpiresAt);
internal sealed record AdminCodeRequest(int Minutes = 10);
internal sealed record AdminInvocation(string? Command, string? Argument, JsonElement? Body);

internal sealed class RemoteAdmin(PeerNetwork peers, TimeProvider clock) : IDisposable
{
    internal const string SessionHeader = "Mango-Admin-Session";
    private const int Limit = 16;
    private readonly object sync = new();
    private readonly Dictionary<Guid, Grant> codes = [];
    private readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = TimeSpan.FromSeconds(90) };
    private Loopback? loopback;

    private sealed record Loopback(Uri Origin, string Token);
    private sealed record Session(AdminUnlock Unlock, IPAddress Source, Guid? Code);
    private sealed record Grant(string Digest, DateTimeOffset ExpiresAt, NetworkIdentity Identity,
        Revision Authority, bool Redeemed = false);

    internal void SetLoopback(Uri origin, string token) => Volatile.Write(ref loopback, new(origin, token));

    internal void Map(RouteGroupBuilder group)
    {
        group.MapPost("/login", (AdminLogin input, HttpContext context) =>
            PeerRoutes.HandleAsync(context, token => LoginAsync(input, context.Connection.RemoteIpAddress!, token)));
        group.MapPost("/logout", (HttpRequest request, HttpContext context) =>
        {
            Logout(request);
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        });
        group.MapPost("/codes", (AdminCodeRequest input, HttpContext context) => PeerRoutes.HandleAsync(context,
            _ => Task.FromResult(Issue(context.Request, input))));
        group.MapGet("/codes", (HttpRequest request, HttpContext context) => PeerRoutes.HandleAsync(context,
            _ => Task.FromResult(List(request))));
        group.MapDelete("/codes/{id:guid}", (Guid id, HttpContext context) => PeerRoutes.HandleAsync(context, _ =>
        {
            Revoke(context.Request, id);
            return Task.CompletedTask;
        }));
        group.MapPost("/run", (AdminInvocation input, HttpContext context) =>
            PeerRoutes.HandleAsync(context, token => RunAsync(context, input, token), noContent: false));
    }

    private async Task<AdminAccess> LoginAsync(AdminLogin input, IPAddress source, CancellationToken cancellation)
    {
        if (!peers.Ready || !peers.IsMember)
            throw new PeerFailure(409, "This Mango node must be enrolled and reconciled before administration.");
        if ((input.Password is null) == (input.Code is null))
            throw new PeerFailure(400, "Supply one administration credential.");
        if (input.Password is { } password)
        {
            var admin = await peers.UnlockAsync(new(password), source, cancellation);
            lock (sync)
                return Open(admin, source, null);
        }

        if (!peers.TakeLoginAttempt(source))
            throw new PeerFailure(429, "Too many login attempts. Try again shortly.");
        var digest = Decode(input.Code!);
        lock (sync)
        {
            var snapshot = peers.Snapshot;
            Prune(snapshot);
            foreach (var (id, grant) in codes)
            {
                if (digest is null || grant.Digest != digest || grant.Redeemed)
                    continue;
                var admin = peers.GrantAdmin(snapshot);
                codes[id] = grant with { Redeemed = true };
                return Open(admin, source, id);
            }
        }
        throw new PeerFailure(401, "The login code is invalid, expired, or already used.");
    }

    private AdminAccess Open(AdminUnlock admin, IPAddress source, Guid? code)
    {
        Prune(peers.Snapshot);
        if (sessions.Count >= Limit)
        {
            var oldest = sessions.MinBy(item => item.Value.Unlock.ExpiresAt);
            Drop(oldest.Key);
        }
        var token = PeerCredentials.Secret();
        sessions.Add(PeerCredentials.Digest(token), new(admin, source, code));
        return new(token, admin.ExpiresAt);
    }

    private object Issue(HttpRequest request, AdminCodeRequest input)
    {
        if (input.Minutes is < 1 or > 60)
            throw new PeerFailure(400, "Use a code lifetime between 1 and 60 minutes.");
        lock (sync)
        {
            Require(request);
            var snapshot = peers.Snapshot;
            Prune(snapshot);
            if (snapshot.Records.Identity is not { } identity || snapshot.Records.Authority is not { } authority)
                throw new PeerFailure(409, "Network authority is unavailable.");
            if (codes.Count >= Limit)
                throw new PeerFailure(409, "Revoke an existing login code or wait for it to expire.");
            var secret = PeerCredentials.TemporarySecret();
            var id = Guid.NewGuid();
            var expiry = clock.GetUtcNow().AddMinutes(input.Minutes);
            codes[id] = new(PeerCredentials.Digest(secret), expiry, identity, authority.Revision);
            return new { Id = id, Code = WebEncoders.Base64UrlEncode(Convert.FromHexString(secret)), ExpiresAt = expiry };
        }
    }

    private object List(HttpRequest request)
    {
        lock (sync)
        {
            Require(request);
            Prune(peers.Snapshot);
            return codes.Select(item => new
            {
                Id = item.Key, item.Value.ExpiresAt, item.Value.Redeemed
            }).ToArray();
        }
    }

    private void Revoke(HttpRequest request, Guid id)
    {
        lock (sync)
        {
            Require(request);
            codes.Remove(id);
            foreach (var key in sessions.Where(item => item.Value.Code == id).Select(item => item.Key).ToArray())
                Drop(key);
        }
    }

    private void Logout(HttpRequest request)
    {
        var key = SessionKey(request);
        lock (sync)
            if (key is not null && sessions.TryGetValue(key, out var session) && session.Source.Equals(request.HttpContext.Connection.RemoteIpAddress))
                Drop(key);
    }

    private Session Require(HttpRequest request)
    {
        var key = SessionKey(request);
        lock (sync)
        {
            if (key is null || !sessions.TryGetValue(key, out var session) ||
                !session.Source.Equals(request.HttpContext.Connection.RemoteIpAddress) ||
                session.Unlock.ExpiresAt <= clock.GetUtcNow() || !peers.IsAdmin(session.Unlock.Token))
                throw new PeerFailure(401, "Log in to administer this Mango node.", "login_required");
            return session;
        }
    }

    private async Task RunAsync(HttpContext context, AdminInvocation input, CancellationToken cancellation)
    {
        if (input.Command is not { Length: > 0 and <= 64 } || !AdminCommand.All.TryGetValue(input.Command, out var command) ||
            command.LocalOnly || command.Resolve(input.Argument) is not { } path)
            throw new PeerFailure(400, "Unknown admin command or invalid command argument.");
        var session = Require(context.Request);
        var local = Volatile.Read(ref loopback) ?? throw new PeerFailure(503, "Mango's local management service is unavailable.");
        using var request = new HttpRequestMessage(new HttpMethod(command.Method), new Uri(local.Origin, "/api/local/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", local.Token);
        request.Headers.Add(PeerNetwork.AdminSessionHeader, session.Unlock.Token);
        if (input.Body is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } json)
            request.Content = JsonContent.Create(json);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, cancellation);
        }
        catch (HttpRequestException) when (!context.Response.HasStarted)
        {
            throw new PeerFailure(502, "Mango's local management service did not respond.");
        }
    }

    private void Prune(PeerSnapshot snapshot)
    {
        var now = clock.GetUtcNow();
        foreach (var id in codes.Where(item => item.Value.ExpiresAt <= now || item.Value.Identity != snapshot.Records.Identity ||
                     item.Value.Authority != snapshot.Records.Authority?.Revision).Select(item => item.Key).ToArray())
            codes.Remove(id);
        foreach (var key in sessions.Where(item => item.Value.Unlock.ExpiresAt <= now || !peers.IsAdmin(item.Value.Unlock.Token))
                     .Select(item => item.Key).ToArray())
            Drop(key);
    }

    private void Drop(string key)
    {
        if (sessions.Remove(key, out var entry))
            peers.RevokeAdmin(entry.Unlock.Token);
    }

    private static string? SessionKey(HttpRequest request)
    {
        var header = request.Headers[SessionHeader];
        return header.Count == 1 && PeerCredentials.IsSecret(header[0]) ? PeerCredentials.Digest(header[0]!) : null;
    }

    private static string? Decode(string value)
    {
        if (value.Length != 22)
            return null;
        try
        {
            var decoded = WebEncoders.Base64UrlDecode(value);
            return decoded.Length == 16 ? PeerCredentials.Digest(Convert.ToHexString(decoded)) : null;
        }
        catch (FormatException) { return null; }
    }

    public void Dispose() => http.Dispose();
}
