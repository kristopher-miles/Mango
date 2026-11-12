using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mango.Logging;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

internal static partial class PeerChecks
{
    private const string LogZone = "America/Los_Angeles";
    private const string TextBody = "{ \"model\": \"fixture-chat\", \"stream\": true, \"messages\": [ {\"role\":\"user\",\"content\":\"log fixture\"} ] }\n";
    private const string TextReply = "data: {\"choices\":[{\"delta\":{\"content\":\"fixture\"}}]}\n\n" +
        "data: {\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3},\"timings\":{\"predicted_per_second\":19}}\n\n" +
        "data: [DONE]\n\n";
    private const string SpeechBody = "{ \"model\":\"fixture-speech\", \"voice\":\"fixture\", \"input\":\"hello\" }";

    private static async Task TestPhaseSevenAsync(string root, TailnetStatus status, IPAddress address)
    {
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(25) };
        var firstPath = Path.Combine(root, "logs-first");
        var ownerPath = Path.Combine(root, "logs-owner");
        var ownerPort = Port(address);
        await using var first = await Host.StartAsync(firstPath, status, Port(address));
        var owner = await Host.StartAsync(ownerPath, status, ownerPort);
        await using var provider = await StartLogProviderAsync();
        var wav = LogWav();
        try
        {
            await Local(http, first, "logs/settings", LogSettings(RequestLogMode.Everything));
            await SendLogBody(http, first.LocalAddress, first.Session, "/v1/chat/completions", Encoding.UTF8.GetBytes(TextBody),
                "application/json", expected: 503);
            var prior = (await WaitForLogEntries(first.Network.Logs, Guid.Empty,
                entries => entries.Count(entry => entry.Kind == LogKind.Request) == 1)).Single(entry => entry.Kind == LogKind.Request);

            var created = await Local(http, first, "network/create", new
            {
                name = "Log ingress", password = Password, benchmarkPolicy = "never", role = "leech"
            });
            var network = created.GetProperty("identity").GetProperty("id").GetGuid();
            var recovery = created.GetProperty("recoveryRecord").GetString()!;
            var admin = await Unlock(http, first, Password);
            await Local(http, owner, "network/join", new
            {
                name = "Log serving", network, address = first.TailAddress, password = Password,
                benchmarkPolicy = "never", role = "contributor"
            });
            var ownerAdmin = await Unlock(http, owner, Password);
            await Local(http, owner, "providers", new
            {
                profile = "unslothStudio", mode = "attached", address = provider.Urls.Single(), token = "fixture-secret-for-provider"
            }, ownerAdmin);
            var node = owner.Network.Snapshot.Self!.Id;
            var here = first.Network.Snapshot.Self!.Id;
            await Eventually(() => first.Network.Snapshot.Records.Catalogs.Any(item =>
                item.Node == node && item.Models.Any(model => model.EffectiveAlias == "fixture-speech")), "media provider is advertised");
            var issued = await Local(http, first, "network/keys", new
            {
                name = "logging-fixture-key", priority = "normal", scopes = new[] { "chat", "speech", "transcription" }, neverExpires = true
            }, admin, HttpMethod.Post);
            var key = issued.GetProperty("secret").GetString()!;
            Check((await WaitForLogEntries(first.Network.Logs, Guid.Empty, entries => entries.Any(entry => entry.Id == prior.Id)))
                .Any(entry => entry.Id == prior.Id), "ordinary first enrollment preserves local standalone history");
            var foreign = owner.Network.Snapshot;
            var logTransfer = new LogTransfer(first.Network.Logs.Configuration.Epoch, LogDate(), LogZone, Guid.Empty, 0);
            using (var forbidden = new HttpRequestMessage(HttpMethod.Post, first.TailAddress + "/peer/logs/page"))
            {
                forbidden.Headers.Authorization = new("Bearer", foreign.Token);
                forbidden.Headers.Add(PeerNetwork.NodeHeader, node.ToString("N"));
                forbidden.Headers.Add(PeerNetwork.NetworkHeader, network.ToString("N"));
                forbidden.Headers.Add(PeerNetwork.DigestHeader, foreign.Digest);
                forbidden.Headers.Add(PeerNetwork.ReleaseHeader, MangoRelease.Current.Wire);
                forbidden.Content = System.Net.Http.Json.JsonContent.Create(logTransfer, options: Json);
                using var reply = await http.SendAsync(forbidden);
                Check(reply.StatusCode == HttpStatusCode.BadRequest, "a peer cannot request standalone records");
            }

            var originalRequests = 0;
            LogEntry? capturedChat = null;
            await Local(http, first, "logs/settings", LogSettings(RequestLogMode.Everything), expected: 403);
            foreach (var mode in Enum.GetValues<RequestLogMode>())
            {
                await Local(http, first, "logs/settings", LogSettings(mode), admin);
                await Eventually(() => owner.Network.Logs.Configuration.Policy.Mode == mode, "shared logging mode converges");
                var inputs = new[]
                {
                    (Path: "/v1/chat/completions", Kind: InferenceOperation.Chat, Content: Encoding.UTF8.GetBytes(TextBody),
                        Media: "application/json", Expected: Encoding.UTF8.GetBytes(TextReply)),
                    (Path: "/v1/audio/speech", Kind: InferenceOperation.Speech, Content: Encoding.UTF8.GetBytes(SpeechBody),
                        Media: "application/json", Expected: wav),
                    (Path: "/v1/audio/transcriptions", Kind: InferenceOperation.Transcription, Content: LogMultipart(wav),
                        Media: "multipart/form-data; boundary=mango-log-test", Expected: "{\"text\":\"fixture\"}"u8.ToArray())
                };
                foreach (var input in inputs)
                {
                    var received = await SendLogBody(http, first.TailAddress, key, input.Path, input.Content, input.Media);
                    Check(received.SequenceEqual(input.Expected), $"normal {mode} inference returns exact fixture response for {input.Kind}");
                    if (mode == RequestLogMode.Off)
                        continue;
                    originalRequests++;
                    var entries = await WaitForLogEntries(first.Network.Logs, here,
                        items => items.Count(item => item.Kind == LogKind.Request) == originalRequests);
                    var record = entries.Last(item => item.Kind == LogKind.Request);
                    Check(record.Task == input.Kind && record.KeyName == "logging-fixture-key" && record.Source?.Address == address.ToString() &&
                        record.Routes is [{ Node: var destination }] && destination == node, "ingress log identity and destination");
                    Check((record.Input is not null) == (mode >= RequestLogMode.Verbose) &&
                        (record.Output is not null) == (mode == RequestLogMode.Everything), $"payload availability follows {mode}");
                    if (mode >= RequestLogMode.Verbose)
                        await CheckPayload(first.Network.Logs, record, false, input.Content);
                    if (mode == RequestLogMode.Everything)
                        await CheckPayload(first.Network.Logs, record, true, input.Expected);
                    if (mode == RequestLogMode.Everything && input.Kind == InferenceOperation.Chat)
                        capturedChat = record;
                }
                if (mode == RequestLogMode.Off)
                {
                    await Local(http, first, "network/refresh", new { }, expected: 204);
                    await WaitForLogEntries(first.Network.Logs, here, entries => entries.Any(entry => entry.Kind == LogKind.Operation));
                    var page = await LogPage(first.Network.Logs, here);
                    Check(page.Coverage == LogCoverage.Off && !page.Entries.Any(entry => entry.Kind == LogKind.Request) &&
                        !(await LogPage(owner.Network.Logs, node)).Entries.Any(entry => entry.Kind == LogKind.Attempt),
                        "request logging Off is visible even while independent operational logging is enabled");
                }
            }
            var attempts = await WaitForLogEntries(owner.Network.Logs, node, items => items.Any(item =>
                item.Kind == LogKind.Attempt && item.Request == capturedChat!.Request));
            var executed = attempts.Single(item => item.Kind == LogKind.Attempt && item.Request == capturedChat!.Request);
            Check(executed.Ingress == here && executed.Input is null && executed.Output is null && executed.OutputTokens == 3 &&
                executed.PromptTokens == 12 && executed.TokensPerSecond == 19 && executed.FirstTokenMilliseconds is not null,
                "serving metadata links to ingress and records measured token units without a second payload copy");
            var wave = attempts.LastOrDefault(item => item.Task == InferenceOperation.Speech);
            Check(wave?.AudioSeconds > 0, "binary speech duration uses WAV bytes");
            var operations = (await LogPage(first.Network.Logs, here)).Entries.Count(item => item.Kind == LogKind.Operation);
            await Local(http, first, "logs/settings", LogSettings(RequestLogMode.Everything, operational: false), admin);
            await Eventually(() => !owner.Network.Logs.Configuration.Policy.Operational, "the operational toggle converges");
            await Local(http, first, "network/refresh", new { }, expected: 204);
            Check((await LogPage(first.Network.Logs, here)).Entries.Count(item => item.Kind == LogKind.Operation) == operations,
                "operational logging can be disabled while Everything stays enabled");

            var rejected = "{\"model\":\"fixture-chat\",\"api_key\":\"fixture-credential-in-body\"}"u8.ToArray();
            await SendLogBody(http, first.TailAddress, key, "/v1/chat/completions", rejected, "application/json", expected: 400);
            originalRequests++;
            await SendLogBody(http, first.TailAddress, key, "/v1/audio/transcriptions", LogMultipart(wav, "Authorization: Bearer fixture-hidden-part\r\n"),
                "multipart/form-data; boundary=mango-log-test");
            originalRequests++;
            var captured = await WaitForLogEntries(first.Network.Logs, here,
                entries => entries.Count(entry => entry.Kind == LogKind.Request) == originalRequests);
            Check(captured.Where(item => item.Kind == LogKind.Request).TakeLast(2)
                .All(item => item.Input is { Exact: false, Stored: 0 }), "known body credentials are excluded rather than captured");
            var zip = await ReadLogZip(http, first);
            Check(zip.Manifest.GetProperty("incompletePayloads").GetInt32() >= 2 &&
                zip.Entries.All(pair => !Encoding.UTF8.GetString(pair.Value).Contains(key, StringComparison.Ordinal) &&
                    !Encoding.UTF8.GetString(pair.Value).Contains("fixture-secret-for-provider", StringComparison.Ordinal) &&
                    !Encoding.UTF8.GetString(pair.Value).Contains("fixture-credential-in-body", StringComparison.Ordinal) &&
                    !Encoding.UTF8.GetString(pair.Value).Contains("fixture-hidden-part", StringComparison.Ordinal) &&
                    !Encoding.UTF8.GetString(pair.Value).Contains("fixture-secret-cookie", StringComparison.Ordinal)),
                "export reports exclusions and omits key, provider, cookie and multipart credentials");
            Check(zip.Entries.Values.Any(value => value.SequenceEqual(wav)), "download includes exact retained binary payload");

            await SendLogBody(http, owner.TailAddress, key, "/v1/audio/speech", Encoding.UTF8.GetBytes(SpeechBody), "application/json");
            await WaitForLogEntries(owner.Network.Logs, node, items => items.Any(item => item.Kind == LogKind.Request));
            var epoch = first.Network.Logs.Configuration.Epoch;
            await Local(http, first, "network/recover", new { recoveryRecord = recovery, newPassword = Recovered });
            admin = await Unlock(http, first, Recovered);
            await Request(http, HttpMethod.Get, first.TailAddress + "/v1/models", key);
            Check(first.Network.Logs.Configuration.Epoch == epoch, "ordinary recovery preserves the retained log generation");
            await owner.DisposeAsync();
            await CollectLogDay(http, first);
            var partial = await CollectLogDay(http, first);
            Check(partial.GetProperty("requests").GetInt32() == originalRequests + 1 &&
                partial.GetProperty("collection").GetProperty("sources").EnumerateArray().Any(item =>
                    item.GetProperty("node").GetGuid() == node && item.GetProperty("coverage").GetString() == "unavailable"),
                "repeated offline collection exposes useful partial totals without duplicate originals");
            owner = await Host.StartAsync(ownerPath, status, ownerPort);
            var late = await CollectLogDay(http, first);
            Check(late.GetProperty("requests").GetInt32() == originalRequests + 2 &&
                late.GetProperty("collection").GetProperty("sources").EnumerateArray().Any(item =>
                    item.GetProperty("node").GetGuid() == node && item.GetProperty("coverage").GetString() == "received"),
                $"a late serving peer adds its missed original request. Expected {originalRequests + 2}, saw {late.GetProperty("requests")}. {late.GetProperty("collection")}");
            var mirrored = await CollectLogDay(http, owner);
            Check(mirrored.GetProperty("requests").GetInt32() == originalRequests + 1,
                "a higher-join node retrieves the elected collector's aggregate without copying standalone history");

            await owner.DisposeAsync();
            var clear = await Local(http, first, "logs/clear", new { confirm = true }, admin, HttpMethod.Post);
            Check(clear.GetProperty("localCleared").GetBoolean() &&
                clear.GetProperty("clear").GetProperty("pending").GetArrayLength() == 1 &&
                !(await LogPage(first.Network.Logs, Guid.Empty)).Entries.Any(), "clear removes owned local history and tracks the offline peer");
            owner = await Host.StartAsync(ownerPath, status, ownerPort);
            await Eventually(() => owner.Network.Logs.Available(first.Network.Logs.Configuration.Epoch) &&
                first.Network.Snapshot.Records.Catalogs.Any(item => item.Node == node &&
                    item.AppliedLogGeneration == first.Network.Logs.Configuration.Epoch.Generation), "the offline peer physically applies and acknowledges deletion");
            Check(!(await LogPage(owner.Network.Logs, node)).Entries.Any(entry => entry.Kind is LogKind.Request or LogKind.Attempt) &&
                (await CollectLogDay(http, first)).GetProperty("requests").GetInt32() == 0 &&
                (await ReadLogZip(http, first)).Entries.Count == 2, "cleared payloads cannot return through collection");
            await SendLogBody(http, first.TailAddress, key, "/v1/audio/speech", Encoding.UTF8.GetBytes(SpeechBody), "application/json");
            await WaitForLogEntries(first.Network.Logs, here, items => items.Any(item => item.Kind == LogKind.Request));
            await Local(http, first, "network/reset", new { confirm = true, newPassword = Reset }, admin);
            Check(!(await LogPage(first.Network.Logs, here)).Entries.Any() &&
                (await ReadLogZip(http, first)).Entries.Count == 2, "full reset uses the same local deletion path");
            await Request(http, HttpMethod.Get, first.TailAddress + "/v1/models", key, expected: 401);
        }
        finally { await owner.DisposeAsync(); }
    }

    private static LogSettingsUpdate LogSettings(RequestLogMode mode, bool operational = true) => new(mode, operational, LogZone, 7, 30, 1024);
    private static DateOnly LogDate() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, LogPolicy.Zone(LogZone)!).DateTime);
    private static LogWindow LogTestWindow() => LogWindow.For(LogDate(), LogPolicy.Zone(LogZone)!);
    private static Task<LogPage> LogPage(LogStore store, Guid node) => store.ReadPageAsync(store.Configuration.Epoch, LogTestWindow(), node, 0, default);

    private static async Task<LogEntry[]> WaitForLogEntries(LogStore store, Guid node, Func<LogEntry[], bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            var entries = (await LogPage(store, node)).Entries;
            if (ready(entries))
                return entries;
            try { await Task.Delay(40, timeout.Token); }
            catch (OperationCanceledException) { throw new InvalidOperationException($"Log records did not become available. Found {entries.Length}."); }
        }
    }

    private static async Task CheckPayload(LogStore store, LogEntry entry, bool response, byte[] expected)
    {
        var payload = response ? entry.Output : entry.Input;
        var transfer = new LogPayloadTransfer(store.Configuration.Epoch, LogDate(), LogZone, entry.Node, entry.Id, entry.At,
            entry.Sequence, response, 0);
        var actual = await store.ReadChunkAsync(transfer, LogTestWindow(), default);
        Check(payload is { Exact: true } && payload.Stored == expected.Length && actual?.Data.SequenceEqual(expected) == true,
            $"exact {(response ? "response" : "request")} bytes for {entry.Task}");
    }

    private static async Task<JsonElement> CollectLogDay(HttpClient http, Host host)
    {
        await Local(http, host, "logs/collect", new { day = LogDate() }, method: HttpMethod.Post, expected: 202);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var view = await Local(http, host, $"logs?day={LogDate():yyyy-MM-dd}");
            if (!view.GetProperty("collection").GetProperty("running").GetBoolean())
                return view;
            await Task.Delay(80, timeout.Token);
        }
    }

    private static async Task<(Dictionary<string, byte[]> Entries, JsonElement Manifest)> ReadLogZip(HttpClient http, Host host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, host.LocalAddress + $"/api/local/logs/export?day={LogDate():yyyy-MM-dd}");
        request.Headers.Authorization = new("Bearer", host.Session);
        using var response = await http.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "application/zip",
            "local aggregate export downloads a ZIP");
        using var memory = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var entries = new Dictionary<string, byte[]>();
        foreach (var item in zip.Entries)
        {
            using var stream = item.Open();
            using var data = new MemoryStream();
            await stream.CopyToAsync(data);
            entries[item.FullName] = data.ToArray();
        }
        return (entries, JsonSerializer.Deserialize<JsonElement>(entries["manifest.json"], Json));
    }

    private static async Task<byte[]> SendLogBody(HttpClient http, string baseUrl, string key, string path, byte[] body,
        string type, int expected = 200)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path);
        request.Headers.Authorization = new("Bearer", key);
        request.Headers.TryAddWithoutValidation("Cookie", "fixture=fixture-secret-cookie");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        using var response = await http.SendAsync(request);
        var data = await response.Content.ReadAsByteArrayAsync();
        Check((int)response.StatusCode == expected, $"logged inference expected {expected}, got {(int)response.StatusCode} at {path}");
        return data;
    }

    private static byte[] LogMultipart(byte[] wav, string extraHeader = "") =>
        Encoding.ASCII.GetBytes("--mango-log-test\r\nContent-Disposition: form-data; name=\"model\"\r\n\r\nfixture-asr\r\n" +
            "--mango-log-test\r\nContent-Disposition: form-data; name=\"file\"; filename=\"fixture.wav\"\r\n" +
            "Content-Type: audio/wav\r\n" + extraHeader + "\r\n").Concat(wav).Concat("\r\n--mango-log-test--\r\n"u8.ToArray()).ToArray();

    private static byte[] LogWav()
    {
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data);
        writer.Write("RIFF"u8);
        writer.Write(52);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(8000);
        writer.Write(16000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(16);
        writer.Write(new byte[16]);
        return data.ToArray();
    }

    private static async Task<WebApplication> StartLogProviderAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization != "Bearer fixture-secret-for-provider")
                context.Response.StatusCode = 401;
            else
                await next(context);
        });
        app.MapGet("/api/health", () => new { service = "Unsloth Studio", version = "log-fixture" });
        app.MapGet("/v1/models", () => new { data = new[]
        {
            new { id = "fixture-chat", task = "text-generation", loaded = true, context_length = 2048 },
            new { id = "fixture-speech", task = "text-to-speech", loaded = true, context_length = 2048 },
            new { id = "fixture-asr", task = "automatic-speech-recognition", loaded = true, context_length = 2048 }
        } });
        app.MapGet("/api/inference/status", () => new { context_length = 2048, parallel_slots = 1 });
        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null);
            context.Response.ContentType = "text/event-stream";
            var split = TextReply.IndexOf("\n\n", StringComparison.Ordinal) + 2;
            await context.Response.WriteAsync(TextReply[..split]);
            await context.Response.Body.FlushAsync();
            await Task.Delay(30, context.RequestAborted);
            await context.Response.WriteAsync(TextReply[split..]);
        });
        app.MapPost("/v1/audio/speech", () => Results.Bytes(LogWav(), "audio/wav"));
        app.MapPost("/v1/audio/transcriptions", () => Results.Text("{\"text\":\"fixture\"}", "application/json"));
        await app.StartAsync();
        return app;
    }

    private static async Task TestLogStoreAsync(string root)
    {
        var timezone = LogPolicy.Zone(LogZone)!;
        var spring = LogWindow.For(new(2026, 3, 8), timezone);
        var autumn = LogWindow.For(new(2026, 11, 1), timezone);
        Check(spring.End - spring.Start == TimeSpan.FromHours(23) && autumn.End - autumn.Start == TimeSpan.FromHours(25),
            "IANA daily boundaries follow both daylight-saving transitions");

        var policy = LogPolicy.Default with { TimeZone = LogZone, Mode = RequestLogMode.Everything };
        var node = Guid.NewGuid();
        var config = new LogConfiguration(new(Guid.NewGuid(), new(1, node)), policy, node, "Journal fixture");
        var path = Path.Combine(root, "bounded-log-test");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var slow = new LogStore(path, TimeProvider.System, token => release.Task.WaitAsync(token)))
        {
            await slow.InitializeAsync(config);
            using var output = new MemoryStream();
            var context = new DefaultHttpContext();
            context.Response.Body = output;
            var capture = new LogCapture(slow, config, context) { Started = Stopwatch.GetTimestamp() };
            var chunk = new byte[1024];
            var timer = Stopwatch.StartNew();
            await capture.RunAsync(async () =>
            {
                for (var i = 0; i < 540; i++)
                    await context.Response.Body.WriteAsync(chunk);
            }).WaitAsync(TimeSpan.FromSeconds(2));
            timer.Stop();
            Console.WriteLine($"Log backpressure fixture forwarded 540 KiB in {timer.Elapsed.TotalMilliseconds:0.0} ms with the writer paused.");
            Check(output.Length == 540 * chunk.Length && slow.HasDrops,
                $"slow writer keeps token output flowing. Fixture forwarded in {timer.Elapsed.TotalMilliseconds:0} ms.");
            release.TrySetResult();
            await Eventually(() => JsonSerializer.SerializeToElement(slow.Status, Json).GetProperty("bytes").GetInt64() > 0,
                "writer resumes after fixture release");
            capture.Finish(new(capture.Id, LogKind.Request, DateTimeOffset.UtcNow, node, config.Name));
            var records = await WaitForLogEntries(slow, node, entries => entries.Any(item => item.Id == capture.Id));
            Check(records.Single(item => item.Id == capture.Id).Output is { Exact: false, Stored: > 0 } payload &&
                payload.Stored < payload.Bytes, "retained prefix is marked incomplete after backpressure");
        }

        var broken = 0;
        await using (var failing = new LogStore(Path.Combine(root, "failed-log-test"), TimeProvider.System, _ =>
        {
            if (Interlocked.Increment(ref broken) == 1)
                throw new IOException("Fixture log disk failure.");
            return Task.CompletedTask;
        }))
        {
            await failing.InitializeAsync(config);
            var context = new DefaultHttpContext();
            using var output = new MemoryStream();
            context.Response.Body = output;
            var capture = new LogCapture(failing, config, context) { Started = Stopwatch.GetTimestamp() };
            await capture.RunAsync(async () => await context.Response.Body.WriteAsync("still delivered"u8.ToArray()));
            capture.Finish(new(capture.Id, LogKind.Request, DateTimeOffset.UtcNow, node, config.Name));
            var records = await WaitForLogEntries(failing, node, entries => entries.Any(item => item.Id == capture.Id));
            Check(Encoding.ASCII.GetString(output.ToArray()) == "still delivered" && failing.HasDrops &&
                records.Single(item => item.Id == capture.Id).Output is { Exact: false, Stored: 0 },
                "failed disk writes neither block output nor claim exact capture");
        }

        var quotaPath = Path.Combine(root, "quota-log-test");
        var smaller = config with { Policy = policy with { CapMiB = 1 } };
        await using (var quota = new LogStore(quotaPath, TimeProvider.System))
        {
            await quota.InitializeAsync(smaller);
            var first = QueueLarge(quota, smaller);
            await WaitForLogEntries(quota, node, entries => entries.Any(item => item.Id == first));
            var second = QueueLarge(quota, smaller);
            var entries = await WaitForLogEntries(quota, node, items => items.Any(item => item.Id == second));
            var status = JsonSerializer.SerializeToElement(quota.Status, Json);
            Check(entries.All(item => item.Id != first) && status.GetProperty("bytes").GetInt64() <= 1024 * 1024 &&
                status.GetProperty("shortened").GetBoolean(), "the byte cap evicts the oldest retained payload and metadata");
        }
        await using (var restarted = new LogStore(quotaPath, TimeProvider.System))
        {
            await restarted.InitializeAsync(smaller);
            Check((await LogPage(restarted, node)).Shortened, "byte-cap coverage survives restart");
        }

        var clock = new JournalClock(DateTimeOffset.UtcNow);
        await using (var retained = new LogStore(Path.Combine(root, "retention-log-test"), clock))
        {
            await retained.InitializeAsync(config);
            var id = QueueLarge(retained, config);
            var entry = (await WaitForLogEntries(retained, node, items => items.Any(item => item.Id == id)))
                .Single(item => item.Id == id);
            await retained.ImportAsync(config.Epoch, node, [entry], default, metadataOnly: true);
            clock.Now = clock.Now.AddDays(8);
            await retained.PrepareAsync(default);
            var historical = await LogPage(retained, node);
            var raw = await retained.ReadPageAsync(config.Epoch, LogTestWindow(), node, 0, default, rawOnly: true);
            var transfer = new LogPayloadTransfer(config.Epoch, LogDate(), LogZone, node, id, entry.At, entry.Sequence, false, 0);
            Check(historical.Entries.Single().Id == id && raw.Coverage == LogCoverage.BeyondRetention &&
                await retained.ReadChunkAsync(transfer, LogTestWindow(), default) is null, "aggregate metadata outlives raw payloads");
            clock.Now = clock.Now.AddDays(23);
            await retained.PrepareAsync(default);
            Check((await LogPage(retained, node)).Coverage == LogCoverage.BeyondRetention, "aggregate retention removes the historical copy");
        }

        var journalPath = Path.Combine(root, "recover-log-test");
        var firstId = Guid.NewGuid();
        await using (var first = new LogStore(journalPath, TimeProvider.System))
        {
            await first.InitializeAsync(config);
            first.Queue(config, new(firstId, LogKind.Operation, DateTimeOffset.UtcNow, node, config.Name));
            await WaitForLogEntries(first, node, entries => entries.Any(item => item.Id == firstId));
        }
        var ndjson = Directory.EnumerateFiles(journalPath, "events.ndjson", SearchOption.AllDirectories).Single();
        await File.AppendAllTextAsync(ndjson, "{\"partial");
        await using (var restarted = new LogStore(journalPath, TimeProvider.System))
        {
            await restarted.InitializeAsync(config);
            var next = Guid.NewGuid();
            restarted.Queue(config, new(next, LogKind.Operation, DateTimeOffset.UtcNow, node, config.Name));
            var records = await WaitForLogEntries(restarted, node, entries => entries.Any(item => item.Id == next));
            Check(records.Any(item => item.Id == firstId) && !File.ReadAllText(ndjson).Contains("\"partial"),
                "restart repairs the torn final NDJSON record without dropping an earlier event");

            var remote = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var payloadBytes = "older event bytes"u8.ToArray();
            var events = new[] { 20L, 30, 40, 50 }.Select(seq =>
                new LogEntry(Guid.NewGuid(), LogKind.Request, now, remote, "Late collector", Sequence: seq,
                    Input: seq == 40 ? new(payloadBytes.Length, payloadBytes.Length, true, "text/plain") : null)).ToArray();
            await restarted.ImportAsync(config.Epoch, remote, [events[1], events[3]], default);
            await restarted.StageChunkAsync(config.Epoch, events[2].Id, false, 0, payloadBytes, default);
            await restarted.ImportAsync(config.Epoch, remote, [events[0], events[2]], default);
            await restarted.ImportAsync(config.Epoch, remote, [events[1]], default);
            var all = await LogPage(restarted, remote);
            var page = await restarted.ReadPageAsync(config.Epoch, LogTestWindow(), remote, 30, default);
            Check(all.Entries.Select(item => item.Sequence).SequenceEqual([20L, 30, 40, 50]) &&
                page.Entries.Select(item => item.Sequence).SequenceEqual([40L, 50]), "late inserts and repeated merge preserve paging order and identity");
            await CheckPayload(restarted, page.Entries[0], false, payloadBytes);
        }

        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var fenced = new LogStore(Path.Combine(root, "fenced-log-test"), TimeProvider.System,
            token => paused.Task.WaitAsync(token)))
        {
            await fenced.InitializeAsync(config);
            fenced.Queue(config, new(Guid.NewGuid(), LogKind.Request, DateTimeOffset.UtcNow, node, config.Name));
            var cleared = config with { Epoch = config.Epoch with { Generation = new(2, node) } };
            fenced.Configure(cleared);
            Check(await fenced.PrepareAsync(default), "clear applies before queued disk work can resume");
            paused.TrySetResult();
            var fresh = Guid.NewGuid();
            fenced.Queue(cleared, new(fresh, LogKind.Operation, DateTimeOffset.UtcNow, node, config.Name));
            var records = await WaitForLogEntries(fenced, node, entries => entries.Any(item => item.Id == fresh));
            Check(records.Select(item => item.Id).SequenceEqual([fresh]), "an old queued record cannot resurrect after clear");
            fenced.Configure(config);
            Check(!await fenced.PrepareAsync(default), "a stale local snapshot cannot move deletion backward");
            fenced.Configure(cleared);
            Check(await fenced.PrepareAsync(default) && (await LogPage(fenced, node)).Entries.Single().Id == fresh,
                "the newer generation retains its own records after reconciliation");
        }
    }

    private static Guid QueueLarge(LogStore store, LogConfiguration config)
    {
        var id = Guid.NewGuid();
        var bytes = new byte[625 * 1024];
        Check(store.Reserve(bytes.Length) && store.QueuePayload(config.Epoch, id, false, [bytes]), "queue quota fixture payload");
        store.Queue(config, new(id, LogKind.Request, DateTimeOffset.UtcNow, config.Node, config.Name,
            Input: new(bytes.Length, 0, true, "application/octet-stream")));
        return id;
    }

    private sealed class JournalClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
