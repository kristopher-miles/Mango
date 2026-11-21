using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Mango.Peers;

namespace Mango.Host;

internal static class AdminCli
{
    private const string Usage = """
        Mango admin [--target TAILSCALE_IP[:PORT] | --data-dir PATH] COMMAND

        login                         Read the master password from a hidden prompt or stdin.
        login --code                  Read a one-time login code from a hidden prompt or stdin.
        logout                        End the current session.
        access issue [MINUTES]        Issue a one-time login code for this node.
        access list                   List code IDs, expiry and redemption status.
        access revoke ID              Revoke a code and any session it opened.
        updates enable | disable | defer | resume
        https enable ORIGIN [UPSTREAM_PORT] | disable
        Other commands take an argument where required and --stdin or --file PATH for JSON.
        """;

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args is ["--help"])
            {
                Console.WriteLine(Usage);
                Console.WriteLine("Commands: " + string.Join(", ", AdminCommand.All.Keys));
                return 0;
            }
            Uri? target = null;
            string? directory = null;
            if (args is ["--target", var address, .. var remote])
            {
                target = Tailnet.Address(address) ?? throw new ArgumentException("Enter a numeric Tailscale address.");
                args = remote;
            }
            else if (args is ["--data-dir", var path, .. var local])
            {
                directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                args = local;
            }
            if (args.Length == 0)
                throw new ArgumentException("Choose a command. Run Mango admin --help.");
            if (OperatingSystem.IsMacOS() && target is null)
                throw new ArgumentException("Specify the Tailscale address of the Mango node with --target.");

            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mango");
            var state = new AdminCliSession(target?.GetLeftPart(UriPartial.Authority) ?? ("local:" + directory));
            using var transport = new SocketsHttpHandler
            {
                UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(6)
            };
            string? localToken = null;
            if (target is not null)
                await BindTailnetAsync(transport, target);
            else
            {
                var url = new Uri(await new InstanceControl(directory).ContactAsync(quit: false));
                target = new Uri(url.GetLeftPart(UriPartial.Authority));
                localToken = url.Fragment.StartsWith("#session=", StringComparison.Ordinal) ? url.Fragment[9..] : null;
                if (!PeerCredentials.IsSecret(localToken) || target.Host != "127.0.0.1" || target.Scheme != "http")
                    throw new IOException("Mango returned an invalid local control address.");
            }
            using var http = new HttpClient(transport, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(95) };
            var cli = new Connection(http, target, localToken);

            if (args is ["login"] or ["login", "--code"])
            {
                var code = args.Length == 2;
                var secret = Program.ReadSecret(code ? "Mango one-time login code: " : "Mango network master password: ");
                if (string.IsNullOrEmpty(secret))
                    throw new ArgumentException("No credential was supplied.");
                using var response = await cli.SendAsync("POST", "login", code ? new AdminLogin(null, secret) : new(secret, null));
                await EnsureSuccessAsync(response);
                var access = await response.Content.ReadFromJsonAsync<AdminAccess>(PeerNetwork.Json);
                if (access is null || !PeerCredentials.IsSecret(access.Token) || access.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new IOException("Mango returned an invalid login session.");
                state.Save(access);
                Console.WriteLine($"Logged in. Session expires at {access.ExpiresAt:O}.");
                return 0;
            }
            var session = state.Read();
            if (args is ["logout"])
            {
                if (session is not null)
                {
                    using var response = await cli.SendAsync("POST", "logout", session: session.Token);
                    await EnsureSuccessAsync(response);
                }
                state.Delete();
                Console.WriteLine("Logged out.");
                return 0;
            }
            if (args[0] == "access")
            {
                var token = RequireSession(session);
                using var response = args switch
                {
                    ["access", "issue"] => await cli.SendAsync("POST", "codes", new AdminCodeRequest(), token),
                    ["access", "issue", var minutes] => await cli.SendAsync("POST", "codes", CodeRequest(minutes), token),
                    ["access", "list"] => await cli.SendAsync("GET", "codes", session: token),
                    ["access", "revoke", var id] when Guid.TryParse(id, out var parsed) =>
                        await cli.SendAsync("DELETE", $"codes/{parsed:D}", session: token),
                    _ => throw new ArgumentException("Use access issue [MINUTES], access list, or access revoke ID.")
                };
                await WriteAsync(response);
                return 0;
            }

            var invocation = await ParseCommandAsync(args, cli, session);
            if (!AdminCommand.All.TryGetValue(invocation.Command!, out var command) ||
                command.Resolve(invocation.Argument) is not { } route)
                throw new ArgumentException("Unknown command or invalid argument. Run Mango admin --help.");
            using var result = command.LocalOnly || localToken is not null && session is null && command.Method == "GET"
                ? await cli.LocalAsync(command.Method, route, invocation.Body)
                : await cli.SendAsync("POST", "run", invocation, RequireSession(session));
            await WriteAsync(result);
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or HttpRequestException or
            JsonException or CryptographicException or OperationCanceledException or FormatException)
        {
            Console.Error.WriteLine(error is HttpRequestException or OperationCanceledException
                ? "Mango did not respond. Check the target address and the node's availability." : error.Message);
            return 1;
        }
    }

    private static AdminCodeRequest CodeRequest(string minutes)
    {
        if (!int.TryParse(minutes, out var count) || count is < 1 or > 60)
            throw new ArgumentException("Use a code lifetime between 1 and 60 minutes.");
        return new(count);
    }

    private static async Task<AdminInvocation> ParseCommandAsync(string[] args, Connection cli, AdminAccess? session)
    {
        if (args is ["updates", "enable" or "disable" or "defer" or "resume"])
            return new("updates policy", null, args[1] is "enable" or "disable"
                ? JsonSerializer.SerializeToElement(new { daily = args[1] == "enable" })
                : JsonSerializer.SerializeToElement(new { deferred = args[1] == "defer" }));
        if (args is ["https", "enable", _] or ["https", "enable", _, _] or ["https", "disable"])
        {
            var enabled = args[1] == "enable";
            var origin = enabled ? args[2] : null;
            if (enabled && (!Uri.TryCreate(origin, UriKind.Absolute, out var url) || url.Scheme != "https"))
                throw new ArgumentException("Enter the HTTPS origin served by your reverse proxy.");
            using var response = await cli.SendAsync("POST", "run", new AdminInvocation("https status", null, null),
                RequireSession(session));
            await EnsureSuccessAsync(response);
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(PeerNetwork.Json);
            if (!result.TryGetProperty("settings", out var settings) || settings.Deserialize<PublicIngressSettings>(PeerNetwork.Json) is not { } current)
                throw new IOException("Mango did not return the current HTTPS settings.");
            var port = current.Port;
            if (args.Length == 4 && (!int.TryParse(args[3], out port) || port is < 1024 or > 65535))
                throw new ArgumentException("Use an upstream port from 1024 to 65535.");
            return new("https set", null, JsonSerializer.SerializeToElement(new PublicIngressUpdate(enabled, port,
                current.TrustedProxy, origin ?? current.HttpsOrigin, current), PeerNetwork.Json));
        }

        JsonElement? body = null;
        if (args[^1] == "--stdin")
        {
            body = JsonSerializer.Deserialize<JsonElement>(await Console.In.ReadToEndAsync());
            args = args[..^1];
        }
        else if (args.Length >= 2 && args[^2] == "--file")
        {
            body = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(args[^1]));
            args = args[..^2];
        }
        if (args is ["updates", "share"] && body is null)
            body = JsonSerializer.SerializeToElement(new { allowPartial = false });
        if (args is ["network", "discover"] && body is null)
            body = JsonSerializer.SerializeToElement(new { address = (string?)null });
        if (args.Length == 0 || args.Length > 3)
            throw new ArgumentException("Invalid command. Run Mango admin --help.");
        return new(args.Length == 1 ? args[0] : args[0] + " " + args[1], args.Length == 3 ? args[2] : null, body);
    }

    private static string RequireSession(AdminAccess? session) => session?.Token ??
        throw new ArgumentException("Log in first with Mango admin [--target ADDRESS] login.");

    private static async Task BindTailnetAsync(SocketsHttpHandler transport, Uri target)
    {
        var destination = IPAddress.Parse(target.DnsSafeHost);
        var status = await Tailnet.ReadAsync(CancellationToken.None);
        if (!status.LocalAddresses.Contains(destination) && !status.Candidates.Any(peer => peer.Addresses.Contains(destination)))
            throw new ArgumentException("The target address is not listed by this computer's Tailscale client.");
        var address = status.LocalAddresses.FirstOrDefault(ip => ip.AddressFamily == destination.AddressFamily)
            ?? throw new ArgumentException("A matching local Tailscale interface is required.");
        transport.ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(destination.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Bind(new IPEndPoint(address, 0));
                await socket.ConnectAsync(new IPEndPoint(destination, target.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    private static async Task WriteAsync(HttpResponseMessage response)
    {
        await EnsureSuccessAsync(response);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return;
        await response.Content.CopyToAsync(Console.OpenStandardOutput());
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
            Console.WriteLine();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return;
        var description = $"Mango returned HTTP {(int)response.StatusCode}.";
        if (response.Content.Headers.ContentType?.MediaType is "application/json" or "application/problem+json")
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
                description = error.GetString()!;
        }
        throw new IOException(description);
    }

    private sealed class Connection(HttpClient http, Uri origin, string? loopback)
    {
        public Task<HttpResponseMessage> SendAsync(string method, string path, object? body = null, string? session = null) =>
            RequestAsync(method, (loopback is null ? "/peer/admin/" : "/api/local/admin/") + path, body, session);

        public Task<HttpResponseMessage> LocalAsync(string method, string path, object? body) => loopback is not null
            ? RequestAsync(method, "/api/local/" + path, body, null)
            : throw new ArgumentException("This command requires the Mango service account on the target computer.");

        private async Task<HttpResponseMessage> RequestAsync(string method, string path, object? body, string? session)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(origin, path));
            request.Headers.Add(PeerNetwork.ReleaseHeader, MangoRelease.Current.Wire);
            if (loopback is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loopback);
            if (session is not null)
                request.Headers.Add(RemoteAdmin.SessionHeader, session);
            if (body is not null)
                request.Content = JsonContent.Create(body, options: PeerNetwork.Json);
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        }
    }
}
