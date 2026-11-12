using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

internal static partial class PeerChecks
{
    private static async Task TestPhaseSixAsync(string root, TailnetStatus status, IPAddress address)
    {
        const string providerToken = "fixture-provider-token";
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = await StartProviderAsync(stopped, providerToken);
        await using var ingress = await Host.StartAsync(Path.Combine(root, "public-ingress"), status, Port(address));
        var ownerPath = Path.Combine(root, "public-owner");
        var ownerPort = Port(address);
        var owner = await Host.StartAsync(ownerPath, status, ownerPort);
        try
        {
            var created = await Local(http, ingress, "network/create", new
            {
                name = "HTTPS ingress", password = Password, role = LocalRole.Leech
            });
            var identity = created.GetProperty("identity").GetProperty("id").GetGuid();
            var recovery = created.GetProperty("recoveryRecord").GetString();
            var admin = await Unlock(http, ingress, Password);
            var key = (await Issue(http, ingress, admin, "https client")).GetProperty("secret").GetString()!;
            Check(!(await Local(http, ingress, "public-ingress")).GetProperty("listening").GetBoolean(), "issuing a key leaves public ingress off");
            var joined = await Local(http, owner, "network/join", new JoinNetworkRequest("Model host", ingress.TailAddress,
                identity, Password, BenchmarkPolicy.Never, LocalRole.Contributor));
            var node = joined.GetProperty("node").GetGuid();
            var ownerAdmin = await Unlock(http, owner, Password);
            var providerAddress = provider.Urls.Single();
            await Local(http, owner, "providers", new
            {
                profile = ProviderProfile.LlamaSingle, mode = ProviderMode.Attached, address = providerAddress, token = providerToken
            }, ownerAdmin);
            await Eventually(() => ingress.Network.Snapshot.Records.Models.Any(model => model.Owner == node) &&
                ingress.Network.Snapshot.Records.Providers?.Any(item => item.Owner == node) == true, "owner publishes its initial settings");
            var model = ingress.Network.Snapshot.Records.Models.Single(item => item.Owner == node);
            var registered = ingress.Network.Snapshot.Records.Providers!.Single(item => item.Owner == node);
            var modelUpdate = new SharedModelUpdate(node, model.Deployment, model.Revision, "remote-first",
                new() { ["Reasoning"] = 4 }, new(2048, null, null, null));
            await Local(http, ingress, "network/models", modelUpdate, expected: 403);
            await Local(http, ingress, "network/models", modelUpdate, admin);
            await Eventually(() => Reported(ingress, node, model.Deployment) is
                { EffectiveAlias: "remote-first", Intent.State: IntentState.Applied }, "remote alias and capability apply on the owner");
            Check(Reported(ingress, node, model.Deployment)?.EffectiveRatings?["Reasoning"] == 4, "effective rating is observed");

            await Request(http, HttpMethod.Put, owner.TailAddress + "/api/local/settings", key, new { activityMonitoring = false },
                admin, expected: 404);
            await Local(http, ingress, "network/models", new
            {
                owner = node, deployment = model.Deployment, alias = "blocked", ratings = new { }, activityMonitoring = false
            }, admin, expected: 400);
            await Local(http, ingress, "network/providers", new
            {
                owner = node, id = registered.Id, revision = registered.Revision, profile = registered.Profile,
                address = providerAddress, enabled = true, mode = "managed", token = "forbidden"
            }, admin, expected: 400);

            var newAddress = $"http://127.0.0.1:{Port(IPAddress.Loopback)}";
            await Local(http, ingress, "network/providers", new SharedProviderUpdate(node, registered.Id, registered.Revision,
                registered.Profile, newAddress, true), admin);
            await Eventually(() => ObservedEndpoint(ingress, node, registered.Id)?.Intent?.State == IntentState.LocalAction,
                "remote retarget of a credentialed endpoint needs local approval");
            Check(ObservedEndpoint(ingress, node, registered.Id)?.Address == providerAddress,
                "pending request does not move the credentialed provider");
            var pending = ingress.Network.Snapshot.Records.Providers!.Single(item => item.Id == registered.Id);
            await Local(http, ingress, "network/providers", new SharedProviderUpdate(node, registered.Id, pending.Revision,
                registered.Profile, providerAddress, true), admin);
            await Eventually(() => ObservedEndpoint(ingress, node, registered.Id)?.Intent?.State == IntentState.Applied,
                "restoring the requested endpoint matches the applied registration");

            var extraAddress = $"http://127.0.0.1:{Port(IPAddress.Loopback)}";
            await Local(http, ingress, "network/providers", new SharedProviderUpdate(node, null, null,
                ProviderProfile.LlamaSingle, extraAddress, true), admin);
            var extra = ingress.Network.Snapshot.Records.Providers!.Single(item => item.Address == extraAddress);
            await Eventually(() => ObservedEndpoint(ingress, node, extra.Id) is { Intent.State: IntentState.Applied, Address: not null },
                "a new loopback registration is applied by the remote owner");
            var added = (await Local(http, owner, "providers")).GetProperty("endpoints").EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == extra.Id);
            Check(added.GetProperty("mode").GetString() == "attached" &&
                added.GetProperty("credentialReference").ValueKind == JsonValueKind.Null &&
                added.GetProperty("ownedProcessId").ValueKind == JsonValueKind.Null,
                "a shared registration grants no credential or process authority");
            await Local(http, ingress, "network/providers", new SharedProviderUpdate(node, extra.Id, extra.Revision,
                extra.Profile, extra.Address, false), admin);
            await Eventually(() => ObservedEndpoint(ingress, node, extra.Id) is { Intent.State: IntentState.Applied, Address: null },
                "a remote disable settles an uncredentialed attached endpoint");

            var online = ingress.Network.Snapshot.Records.Models.Single(item => item.Id == model.Id);
            await owner.DisposeAsync();
            var delivery = await Local(http, ingress, "network/models", modelUpdate with
            {
                Revision = online.Revision, Alias = "offline-request", Ratings = new() { ["Reasoning"] = 0, ["Other"] = null },
                Serving = new(4096, null, null, null)
            }, admin);
            Check(delivery.GetProperty("pending").GetInt32() == 1 &&
                ingress.Network.Snapshot.Records.Models.Single(item => item.Id == model.Id).Alias == "offline-request" &&
                Reported(ingress, node, model.Deployment)?.EffectiveAlias == "remote-first", "offline request and last-applied data stay separate");
            var remoteStatus = (await Local(http, ingress, "network")).GetProperty("nodes")[0];
            Check(!remoteStatus.GetProperty("reachableAtLastCheck").GetBoolean(), "the network view reports last failed contact");
            await Local(http, ingress, "network/models", modelUpdate with { Revision = online.Revision }, admin, expected: 409);
            owner = await Host.StartAsync(ownerPath, status, ownerPort);
            await Eventually(() => Reported(ingress, node, model.Deployment) is
                { EffectiveAlias: "offline-request", Context: 2048, Intent.State: IntentState.Applied },
                "reconnecting owner applies requested changes and reports real provider context");
            var observed = Reported(ingress, node, model.Deployment)!;
            Check(observed.EffectiveRatings?["Reasoning"] == 0 && observed.EffectiveRatings["Other"] is null &&
                observed.EffectiveServing?.ServedContext == 4096, "rated zero, unrated and effective configuration round trip");

            var port = Port(IPAddress.Loopback);
            await Local(http, ingress, "public-ingress", new PublicIngressSettings(true, port, null, null), expected: 204);
            var upstream = $"http://127.0.0.1:{port}";
            using var rootKey = RSA.Create(2048);
            using var rootCertificate = CreateRoot(rootKey);
            using var serverKey = RSA.Create(2048);
            using var serverCertificate = CreateServer(serverKey, rootCertificate);
            var overwrite = false;
            await using var proxy = await StartTlsProxyAsync(upstream, serverCertificate, () => Volatile.Read(ref overwrite));
            using var https = new HttpClient(new HttpClientHandler
            {
                UseProxy = false,
                ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                    TrustTestRoot(certificate, errors, rootCertificate)
            }) { Timeout = TimeSpan.FromSeconds(20) };
            var origin = proxy.Urls.Single();
            await Local(http, ingress, "public-ingress", new PublicIngressSettings(true, port, null, origin), expected: 204);
            var check = await Local(http, ingress, "public-ingress/check", new { }, method: HttpMethod.Post);
            Check(!check.GetProperty("reached").GetBoolean(), "reachability check rejects the fixture CA outside the configured client trust");
            foreach (var path in new[] { "/", "/api/local/settings", "/api/local/network/events", "/peer/info", "/v1/responses" })
                await Request(https, HttpMethod.Get, origin + path, key, expected: 404);
            foreach (var path in Enum.GetValues<InferenceOperation>().Select(InferencePaths.Path))
                await Request(https, HttpMethod.Post, origin + path, expected: 401);
            await Request(https, HttpMethod.Post, origin + "/v1/models", key, expected: 404);
            await Request(https, HttpMethod.Get, origin + "/v1/models", key);
            var wrong = key[..^1] + (key[^1] == 'A' ? "B" : "A");
            await Request(https, HttpMethod.Get, origin + "/v1/models", wrong, expected: 401);
            await Request(https, HttpMethod.Get, origin + "/v1/models", PeerCredentials.Key(Guid.NewGuid(), PeerCredentials.Secret()), expected: 401);
            await Request(https, HttpMethod.Post, origin + "/v1/embeddings", key, new { oversized = "unused" }, expected: 403);
            using (var rejected = new HttpRequestMessage(HttpMethod.Post, upstream + "/v1/chat/completions"))
            {
                var unread = new UnreadUpload(32 * 1024 * 1024);
                rejected.Content = unread;
                rejected.Headers.ExpectContinue = true;
                using var denied = await http.SendAsync(rejected, HttpCompletionOption.ResponseHeadersRead);
                Check(denied.StatusCode == HttpStatusCode.Unauthorized && !unread.Started, "authentication precedes the public upload");
            }
            using (var rejected = new HttpRequestMessage(HttpMethod.Post, upstream + "/v1/chat/completions"))
            {
                var unread = new UnreadUpload(65 * 1024 * 1024);
                rejected.Content = unread;
                rejected.Headers.Authorization = new("Bearer", key);
                rejected.Headers.ExpectContinue = true;
                using var denied = await http.SendAsync(rejected, HttpCompletionOption.ResponseHeadersRead);
                Check(denied.StatusCode == HttpStatusCode.RequestEntityTooLarge && !unread.Started, "an oversized upload is rejected without reading it");
            }

            using var feedRequest = new HttpRequestMessage(HttpMethod.Get, owner.LocalAddress + "/api/local/network/events");
            feedRequest.Headers.Authorization = new("Bearer", owner.Session);
            using var feed = await http.SendAsync(feedRequest, HttpCompletionOption.ResponseHeadersRead);
            using var reader = new StreamReader(await feed.Content.ReadAsStreamAsync());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using (var streamRequest = TlsChat(origin, key, stream: true))
            using (var streamed = await https.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
            {
                if (!streamed.IsSuccessStatusCode)
                    throw new InvalidOperationException($"TLS stream returned {(int)streamed.StatusCode}: {await streamed.Content.ReadAsStringAsync(deadline.Token)}");
                using var output = new StreamReader(await streamed.Content.ReadAsStreamAsync(deadline.Token));
                Check((await output.ReadLineAsync(deadline.Token))?.Contains("active-fixture") == true,
                    "first stream chunk arrives while the provider is still running");
                while (true)
                {
                    using var frame = JsonDocument.Parse((await reader.ReadLineAsync(deadline.Token))!);
                    if (frame.RootElement.GetProperty("requests") is not { ValueKind: JsonValueKind.Object } requests ||
                        requests.GetProperty("running").GetInt32() == 0)
                        continue;
                    Check(requests.GetProperty("serving").EnumerateArray().Any(attempt =>
                        attempt.GetProperty("state").GetString() == "running" &&
                        attempt.GetProperty("source").GetProperty("address").GetString() == "127.0.0.1" &&
                        attempt.GetProperty("source").GetProperty("provenance").GetString() == "proxyObserved"),
                        "serving feed receives proxy-observed attribution and ignores spoofed source");
                    break;
                }
            }
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(6));

            Volatile.Write(ref overwrite, true);
            await Local(http, ingress, "public-ingress", new PublicIngressSettings(true, port, "127.0.0.1", origin), expected: 204);
            using (var trusted = TlsChat(origin, key, stream: false))
            using (var response = await https.SendAsync(trusted))
                Check(response.IsSuccessStatusCode, "trusted proxy keyed inference completes");
            var activity = JsonSerializer.SerializeToElement(owner.Network.SchedulingView(), Json);
            Check(activity.GetProperty("serving").EnumerateArray().Any(attempt =>
                attempt.GetProperty("source").GetProperty("provenance").GetString() == "trustedProxy" &&
                attempt.GetProperty("source").GetProperty("address").GetString() == "127.0.0.1"),
                "trusted proxy observation overrides the spoof and is preserved across peers");

            var doomed = await Issue(http, ingress, admin, "revoked public key");
            await Local(http, ingress, "network/keys/" + doomed.GetProperty("key").GetProperty("id").GetGuid(), admin: admin, method: HttpMethod.Delete);
            await Request(https, HttpMethod.Get, origin + "/v1/models", doomed.GetProperty("secret").GetString(), expected: 401);
            await Local(http, ingress, "network/recover", new { recoveryRecord = recovery, newPassword = Recovered });
            await Request(https, HttpMethod.Get, origin + "/v1/models", key);
            admin = await Unlock(http, ingress, Recovered);
            await Local(http, ingress, "network/reset", new { confirm = true, newPassword = Reset }, admin);
            await Request(https, HttpMethod.Get, origin + "/v1/models", key, expected: 401);
            Check(ingress.Network.Snapshot.Records.Providers?.Any(item => item.Id == registered.Id) == true &&
                ingress.Network.Snapshot.Records.Models.Any(item => item.Alias == "offline-request"), "full reset keeps shared provider and model settings");
            await Local(http, ingress, "public-ingress", PublicIngressSettings.Off, expected: 204);
            Check(!(await Local(http, ingress, "public-ingress")).GetProperty("listening").GetBoolean(), "local owner can close the dedicated upstream");
        }
        finally { await owner.DisposeAsync(); }
    }

    private static ObservedDeployment? Reported(Host host, Guid owner, string id) =>
        host.Network.Snapshot.Records.Catalogs.FirstOrDefault(item => item.Node == owner)?.Models.FirstOrDefault(item => item.Id == id);

    private static ObservedProvider? ObservedEndpoint(Host host, Guid owner, Guid id) =>
        host.Network.Snapshot.Records.Catalogs.FirstOrDefault(item => item.Node == owner)?.Providers?.FirstOrDefault(item => item.Id == id);

    private static HttpRequestMessage TlsChat(string origin, string key, bool stream)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, origin + "/v1/chat/completions");
        request.Headers.Authorization = new("Bearer", key);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.19");
        request.Content = JsonContent.Create(new
        {
            model = "offline-request", stream, messages = new[] { new { role = "user", content = "hello" } }
        }, options: Json);
        return request;
    }

    private static X509Certificate2 CreateRoot(RSA key)
    {
        var request = new CertificateRequest("CN=Mango test root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
    }

    private static X509Certificate2 CreateServer(RSA key, X509Certificate2 root)
    {
        var request = new CertificateRequest("CN=Mango test proxy", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        using var certificate = request.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1),
            RandomNumberGenerator.GetBytes(16));
        using var signed = certificate.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
    }

    private static bool TrustTestRoot(X509Certificate2? server, SslPolicyErrors errors, X509Certificate2 root)
    {
        if (server is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
            return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        return chain.Build(server);
    }

    private static async Task<WebApplication> StartTlsProxyAsync(string upstream, X509Certificate2 certificate, Func<bool> overwrite)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        app.Lifetime.ApplicationStopped.Register(http.Dispose);
        app.Run(async context =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstream + context.Request.Path);
            if (context.Request.Headers.Authorization.Count > 0)
                request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToArray());
            if (overwrite())
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress!.ToString());
            else if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var values))
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", values.ToArray());
            if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            {
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.ContentLength = context.Request.ContentLength;
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType!);
            }
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            if (response.Headers.TryGetValues("Mango-Ingress", out var marker))
                context.Response.Headers["Mango-Ingress"] = marker.ToArray();
            await using var data = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            var buffer = new byte[4096];
            int count;
            while ((count = await data.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
            }
        });
        await app.StartAsync();
        return app;
    }

    private sealed class UnreadUpload(long bytes) : HttpContent
    {
        public bool Started { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Started = true;
            throw new InvalidOperationException("The unauthenticated upload began.");
        }
        protected override bool TryComputeLength(out long length)
        {
            length = bytes;
            return true;
        }
    }
}
