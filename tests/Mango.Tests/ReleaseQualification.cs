using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static partial class PeerChecks
{
    public static async Task QualifyReleaseAsync(string root, string executable)
    {
        var tailnet = await Tailnet.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(8)).Token);
        var address = tailnet.LocalAddresses.FirstOrDefault(item => item.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            ?? throw new InvalidOperationException("Release qualification requires the Windows Tailscale IPv4 interface.");
        var ports = new HashSet<int>();
        while (ports.Count < 20)
            ports.Add(Port(address));
        var localPort = ports.First();
        var own = Guid.NewGuid();
        var network = Guid.NewGuid();
        var token = PeerCredentials.Secret();
        var keyId = Guid.NewGuid();
        var keySecret = PeerCredentials.Secret();
        var generation = new Revision(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), own);
        var self = new PeerNode(own, tailnet.Device!, "Packaged Mango", Tailnet.Origin(address, localPort), generation, generation,
            PeerCredentials.Digest(token));
        var simulated = ports.Skip(1).ToDictionary(port => port, port => new PeerNode(Guid.NewGuid(), $"release-{port}", $"Peer {port}",
            Tailnet.Origin(address, port), generation, generation, PeerCredentials.Digest(PeerCredentials.Secret())));
        var catalogs = simulated.Values.Select(node => new NodeCatalog(node.Id, generation, node.Name, LocalRole.Contributor,
            Enumerable.Range(0, 5).Select(index => new ObservedDeployment($"model-{index}", $"Model {index}", null, "fixture",
                ["text"], ["text"], [InferenceOperation.Chat], 2048, LimitSource.Reported, true, true, null, 1, null, "fixture",
                LocallyAvailable: true)).ToArray(), SchedulingEpoch: Guid.NewGuid().ToString("N"))).ToArray();
        var records = NetworkRecords.Empty with
        {
            Identity = new(network, generation, MangoRelease.Current.Window),
            Authority = PeerCredentials.Password(Password, PeerCredentials.Digest(PeerCredentials.Secret()), generation),
            Nodes = simulated.Values.Prepend(self).ToArray(), Catalogs = catalogs,
            Keys = [new(keyId, "Release check", [InferenceScope.Chat], KeyPriority.Normal, null, own, generation,
                PeerCredentials.Digest(keySecret))],
            Models = catalogs.SelectMany(catalog => catalog.Models.Select(model => new SharedModel(
                PeerValidation.ModelId(catalog.Node, model.Id), catalog.Node, model.Id, generation,
                $"remote-{catalog.Node:N}-{model.Id}", []))).ToArray()
        };
        var context = SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(root).ToUpperInvariant()));
        var disk = new PeerDisk(records, new(own, self.Device, WindowsSecrets.Protect(token, context)));
        await new SnapshotFile<PeerDisk>(Path.Combine(root, "network.json"), PeerValidation.Disk).LoadAsync(disk);
        await new SnapshotFile<LocalSettings>(Path.Combine(root, "local-settings.json"), LocalSettings.Validate)
            .LoadAsync(new LocalSettings { Role = LocalRole.Contributor });

        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        await using var fixtures = await StartQualificationFixturesAsync(address, simulated, network, calls);
        var provider = fixtures.Urls.Single(url => url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        var endpoint = new ProviderEndpoint(Guid.NewGuid(), ProviderProfile.LlamaSingle, ProviderMode.Attached, provider, null, null, null);
        await new SnapshotFile<ProviderSettings>(Path.Combine(root, "providers.json"), _ => null)
            .LoadAsync(new([endpoint], [], null));
        using var host = Process.Start(ReleaseCommand(executable, root, "--headless", "--peer-port", localPort.ToString()))!;
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(25) };
        try
        {
            Uri? uri = null;
            for (var retry = 0; retry < 20 && uri is null; retry++)
            {
                using var command = Process.Start(ReleaseCommand(executable, root, "--url"))!;
                var output = await command.StandardOutput.ReadToEndAsync();
                await command.WaitForExitAsync();
                if (Uri.TryCreate(output.Trim(), UriKind.Absolute, out var found) && found.Fragment.StartsWith("#session="))
                    uri = found;
                else
                    await Task.Delay(250);
            }
            if (uri is null)
                throw new InvalidOperationException("The packaged WinExe did not return a local session URL.");
            var origin = uri.GetLeftPart(UriPartial.Authority);
            var session = uri.Fragment["#session=".Length..];
            Task<System.Text.Json.JsonElement> LocalCall(string route, object? body = null, string? admin = null, int expected = 200) =>
                Request(http, body is null ? HttpMethod.Get : HttpMethod.Put, origin + "/api/local/" + route, session, body, admin, expected);
            async Task WaitFor(Func<Task<bool>> condition)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                while (!await condition())
                    await Task.Delay(100, deadline.Token);
            }
            await WaitFor(async () => (await LocalCall("network")).GetProperty("ready").GetBoolean() &&
                (await LocalCall("providers")).GetProperty("models").GetArrayLength() == 5);
            var catalog = (await LocalCall("providers")).GetProperty("models");
            foreach (var model in catalog.EnumerateArray())
                await LocalCall("resources/budget", new
                {
                    deployment = model.GetProperty("id").GetString(), operation = "chat", units = 2048, items = 1,
                    mediaItems = 0, loadRamMiB = 1024, executeRamMiB = 128, parallelSlots = 1,
                    allowOverlap = false, allowAttached = true
                }, expected: 204);
            var ownAlias = catalog[0].GetProperty("alias").GetString()!;
            await WaitFor(async () => (await LocalCall("network")).GetProperty("models").GetArrayLength() == 100);
            var knownKey = PeerCredentials.Key(keyId, keySecret);
            var body = new { model = ownAlias, messages = new[] { new { role = "user", content = "release qualification" } }, max_tokens = 16 };
            var inference = origin + "/v1/chat/completions";
            async Task Send() => await Request(http, HttpMethod.Post, inference, knownKey, body);
            await Send();
            await Task.Delay(TimeSpan.FromSeconds(3));
            calls.Clear();

            host.Refresh();
            var startCpu = host.TotalProcessorTime;
            var peak = host.WorkingSet64;
            var interval = Stopwatch.StartNew();
            Console.WriteLine("Beginning 15 minutes of packaged Mango idle with 20 peers and 100 deployments. The browser is closed.");
            while (interval.Elapsed < TimeSpan.FromMinutes(15))
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                host.Refresh();
                peak = Math.Max(peak, host.WorkingSet64);
            }
            var cpu = (host.TotalProcessorTime - startCpu).TotalMilliseconds * 100 / interval.Elapsed.TotalMilliseconds;
            var idleCalls = calls.Values.Sum();
            Console.WriteLine($"Idle seconds={interval.Elapsed.TotalSeconds:F1}, CPU per core={cpu:F4}%, peak resident={peak / 1048576d:F1} MiB, peer HTTP calls={idleCalls}.");
            Check(cpu < 1 && peak <= 150 * 1048576 && idleCalls == 0, "installed idle targets at the metadata envelope");

            calls.Clear();
            var overhead = new double[100];
            var total = new double[100];
            for (var i = 0; i < overhead.Length; i++)
            {
                var watch = Stopwatch.StartNew();
                await Send();
                total[i] = watch.Elapsed.TotalMilliseconds;
                var scheduling = await LocalCall("network/scheduling");
                var last = scheduling.GetProperty("recent").EnumerateArray().Last().GetProperty("attempts")[0];
                var duration = TimeSpan.Parse(last.GetProperty("timing").GetProperty("elapsed").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture).TotalMilliseconds;
                overhead[i] = Math.Max(0, total[i] - duration);
            }
            Array.Sort(total);
            Array.Sort(overhead);
            Console.WriteLine($"100 warmed known-key requests. HTTP p95={total[94]:F3} ms, p95 excluding provider envelope={overhead[94]:F3} ms, peer calls={RouteCounts(calls)}.");
            Check(overhead[94] < 25 && calls.Keys.All(route => route == "/peer/push"), "healthy path stays below 25 ms and performs no peer lookups or negotiation");

            var admin = (await LocalCall("network/unlock", new { password = Password })).GetProperty("token").GetString();
            calls.Clear();
            await LocalCall("network/queue", new { waitSeconds = 30 }, admin, 204);
            Console.WriteLine($"One shared metadata edit peer calls: {RouteCounts(calls)}.");
            Check(calls.GetValueOrDefault("/peer/push") == 19 && calls.Count == 1, "one shared edit pushes one delta to each peer");

            calls.Clear();
            var missing = new { model = "missing-release-alias", body.messages, body.max_tokens };
            var previous = (await LocalCall("network/scheduling")).GetProperty("discoveryRuns").GetInt32();
            await Request(http, HttpMethod.Post, inference, knownKey, missing, expected: 503);
            await Request(http, HttpMethod.Post, inference, knownKey, missing, expected: 503);
            var scans = (await LocalCall("network/scheduling")).GetProperty("discoveryRuns").GetInt32() - previous;
            Console.WriteLine($"Two missing-model requests. Discovery runs={scans}, observed fixture calls={RouteCounts(calls)}.");
            Check(scans == 1 && calls.GetValueOrDefault("/peer/info") == 19, "missing models share one discovery cooldown at the envelope");
        }
        finally
        {
            if (!host.HasExited)
            {
                using var quit = Process.Start(ReleaseCommand(executable, root, "--quit"))!;
                await quit.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                if (!host.WaitForExit(10000))
                    host.Kill();
            }
        }
    }

    private static string RouteCounts(ConcurrentDictionary<string, int> calls) => calls.Count == 0 ? "0" :
        string.Join(", ", calls.OrderBy(item => item.Key).Select(item => $"{item.Key}={item.Value}"));

    private static ProcessStartInfo ReleaseCommand(string executable, string directory, params string[] arguments)
    {
        var command = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var item in arguments.Concat(["--data-dir", directory]))
            command.ArgumentList.Add(item);
        return command;
    }

    private static async Task<WebApplication> StartQualificationFixturesAsync(IPAddress address, Dictionary<int, PeerNode> peers,
        Guid network, ConcurrentDictionary<string, int> calls)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            foreach (var port in peers.Keys)
                options.Listen(address, port);
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!peers.TryGetValue(context.Connection.LocalPort, out var peer))
            {
                await next(context);
                return;
            }
            calls.AddOrUpdate(context.Request.Path.Value!, 1, (_, previous) => previous + 1);
            context.Response.Headers[PeerNetwork.ReleaseHeader] = MangoRelease.Current.Wire;
            context.Response.Headers[PeerNetwork.NodeHeader] = peer.Id.ToString("N");
            context.Response.Headers[PeerNetwork.NetworkHeader] = network.ToString("N");
            context.Response.Headers[PeerNetwork.DigestHeader] = context.Request.Headers[PeerNetwork.DigestHeader].ToString();
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            context.Response.StatusCode = context.Request.Path == "/peer/info" ? 404 : 204;
        });
        app.MapGet("/props", () => new { default_generation_settings = new { n_ctx = 2048 }, total_slots = 1, build_info = "release fixture" });
        app.MapGet("/v1/models", () => new { data = Enumerable.Range(0, 5).Select(index => new { id = $"release-{index}" }) });
        app.MapPost("/v1/chat/completions/input_tokens", () => new { input_tokens = 12 });
        app.MapPost("/v1/chat/completions", () => new { id = "release-reply", usage = new { prompt_tokens = 12, completion_tokens = 3 } });
        await app.StartAsync();
        return app;
    }
}
