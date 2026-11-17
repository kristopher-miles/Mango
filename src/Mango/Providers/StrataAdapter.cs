using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mango.Host;

namespace Mango.Providers;

internal sealed class StrataAdapter(HttpClient http, ProviderEndpoint endpoint, string dataDirectory, Action changed)
    : ProviderAdapter(http, endpoint)
{
    internal static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(55);
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private readonly string directory = Path.GetFullPath(Path.Combine(dataDirectory, "strata", endpoint.Id.ToString("N")));
    private readonly string profile = "mango-" + endpoint.Id.ToString("N");
    private string RuntimeDirectory => Path.Combine(directory, "runtime");
    private string PendingPath => Path.Combine(directory, "pending");
    private string ConfigPath => Path.Combine(RuntimeDirectory, "config", "launch.json");
    private bool pending = File.Exists(Path.Combine(dataDirectory, "strata", endpoint.Id.ToString("N"), "pending"));
    private volatile WindowsJob? job;
    private string? bearer;
    private string? control;
    private string? version;
    private string? instance;
    private string? activeModel;
    private long lastUse;
    private Prepared[] models = [];
    private sealed record Prepared(string Id, string NativeId, string Path, string Digest, int? Context, string? Backend, bool Present);

    protected override string? Bearer => bearer;
    internal bool Reserved => pending || job is not null;
    internal bool IsIdleExpired => instance is null || ObservedProcessIds() is { Length: 0 } ||
        Stopwatch.GetElapsedTime(lastUse) >= TimeSpan.FromMinutes(5);
    internal void Touch() => lastUse = Stopwatch.GetTimestamp();
    internal int[] ProcessIds => job?.Processes() ?? [];
    internal int[]? ObservedProcessIds()
    {
        try { return ProcessIds; }
        catch (Exception error) when (error is Win32Exception or ObjectDisposedException) { return null; }
    }
    public override int? OwnedProcessId => ObservedProcessIds()?.FirstOrDefault() is > 0 and var pid ? pid : null;

    public override async Task<ProviderInventory> InspectAsync(CancellationToken cancellation)
    {
        if (pending && (job is null || ProcessIds.Length == 0))
            await VerifyReleaseAsync(cancellation);
        else if (!pending)
            await DetectAsync(cancellation);
        var arguments = new List<string> { "models", "list", "--installed", "--output", "json" };
        if (Endpoint.StrataModels is { } install)
            arguments.AddRange(["--install-dir", install]);
        var inventory = await CommandAsync(arguments, cancellation, TimeSpan.FromSeconds(45));
        if (inventory.Get("models").ValueKind != JsonValueKind.Array)
            throw new ProviderFailure(502, "Strata Control did not return a prepared model list.");
        var found = new List<Prepared>();
        foreach (var row in inventory.Get("models").Items().Take(ProviderLimits.MaxModels))
        {
            var id = row.Text("id");
            var path = row.Text("source_config");
            var digest = row.Text("source_sha256");
            if (!ProviderIdentity.ValidIdentifier(id) || path is null || !Path.IsPathFullyQualified(path) ||
                digest is not { Length: 64 })
                continue;
            var config = await ReadConfigAsync(path, digest, cancellation);
            var native = config?["model_name"]?.GetValueKind() == JsonValueKind.String
                ? config["model_name"]!.GetValue<string>() : null;
            if (!ProviderIdentity.ValidIdentifier(native))
                continue;
            found.Add(new(id!, native!, path, digest, ValidContext(NativeArgument(config!, "--max-context") ?? row.Number("context_tokens")), row.Text("backend"),
                row.Text("component_status") == "present"));
        }
        models = found.DistinctBy(model => model.Id).ToArray();

        ProviderInstance? running = null;
        if (pending && instance is not null && models.FirstOrDefault(model => model.Id == activeModel) is { } active)
            running = await ObserveAsync(active.NativeId, instance, cancellation);
        return new(version, models.Select(model =>
            Model(model.NativeId, model.NativeId, model.Backend, ["text"], ["text"], [InferenceOperation.Chat],
                model.Id == activeModel && running is not null ? [running] : [], null, managementId: model.Id,
                installed: model.Present, supportsLoad: model.Present, supportsUnload: model.Present, format: "GGUF")
            with { Id = ProviderIdentity.Model(Endpoint.Id, model.Id), PreparedContext = model.Context,
                PreparedConfiguration = ProviderIdentity.Hash(control, version, model.Digest) }).ToArray(),
            CanLoad: true, CanUnload: true, CanStop: pending, MaxLoadedModels: 1);
    }

    public override async Task LoadAsync(ProviderModel model, int? context, CancellationToken cancellation)
    {
        if (pending)
            throw new ProviderFailure(409, "Stop and release the current Strata runtime first.");
        var selected = models.FirstOrDefault(item => item.Id == model.ManagementId && item.Present)
            ?? throw new ProviderFailure(409, "Refresh the prepared Strata models before loading this model.");
        var config = await ReadConfigAsync(selected.Path, selected.Digest, cancellation)
            ?? throw new ProviderFailure(409, "The Strata run configuration changed. Refresh the provider and review its memory limits.");

        // The credential travels through the private process environment, including Strata's readiness probe.
        foreach (var key in new[] { "api_key", "telemetry_read_key", "mcp_servers", "mcpServers", "before_load", "log", "api_monitor" })
            config.Remove(key);
        config["host"] = "127.0.0.1";
        var port = new Uri(Endpoint.Address).Port;
        config["port"] = port;
        config["parallel"] = 1;
        config["lazy_load"] = false;
        if (context is { } limit)
        {
            if (limit > selected.Context)
                throw new ProviderFailure(400, "The served context cannot exceed the prepared Strata context.");
            SetNativeArgument(config, "--max-context", limit);
            if (NativeArgument(config, "--kv-resident") > limit)
                SetNativeArgument(config, "--kv-resident", limit);
        }

        try
        {
            var status = await CommandAsync(["runtime", "status", "--profile", profile, "--output", "json"], cancellation);
            if (status.Text("state") != "stopped")
                throw new ProviderFailure(409, "Strata reports an existing Mango runtime. Stop and release it first.");
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            await File.WriteAllTextAsync(ConfigPath, config.ToJsonString(), cancellation);
            var profiles = await CommandAsync(["profile", "list", "--output", "json"], cancellation);
            if (!profiles.Get("profiles").Items().Any(item => item.Text("id") == profile))
                await CommandAsync(["profile", "create", profile, "--from-config", ConfigPath, "--output", "json"], cancellation);
            cancellation.ThrowIfCancellationRequested();
            using (var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true })
            {
                try { listener.Start(); }
                catch (SocketException) { throw new ProviderFailure(409, "The private Strata port is already in use. Choose another loopback port."); }
            }

            job = new WindowsJob();
            await File.WriteAllTextAsync(PendingPath, "", cancellation);
            pending = true;
            bearer = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            activeModel = selected.Id;
            changed();
            var ready = await CommandAsync(["runtime", "start", "--profile", profile, "--wait", "1800", "--shutdown-timeout", "12",
                "--die-with-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--output", "json"],
                cancellation, TimeSpan.FromMinutes(31), job);
            var id = ready.Text("instance_id");
            if (!ready.IsTrue("generation_ready") || ready.Text("state") != "ready" || !ProviderIdentity.ValidIdentifier(id) ||
                ready.Text("api_url") != Endpoint.Address || await ObserveAsync(selected.NativeId, id!, cancellation) is null)
                throw new ProviderFailure(502, "Strata did not verify the expected loaded model and private endpoint.");
            instance = id;
            Touch();
        }
        finally { changed(); }
    }

    public override Task UnloadAsync(ProviderModel model, CancellationToken cancellation) => StopAsync(cancellation);

    public override async Task StopAsync(CancellationToken cancellation)
    {
        if (!pending && job is null)
            return;
        try
        {
            if (job is not null && ProcessIds.Length != 0)
            {
                try
                {
                    await CommandAsync(["runtime", "stop", "--profile", profile, "--wait", "23", "--output", "json"],
                        cancellation, TimeSpan.FromSeconds(27));
                }
                catch (Exception error) when (error is ProviderFailure or OperationCanceledException) { }
                if (ProcessIds.Length != 0)
                {
                    using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    grace.CancelAfter(TimeSpan.FromSeconds(2));
                    try { await WaitForExitAsync(grace.Token); }
                    catch (OperationCanceledException) { job.Terminate(); }
                }
                await WaitForExitAsync(cancellation);
            }
            await VerifyReleaseAsync(cancellation);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or Win32Exception or UnauthorizedAccessException)
        {
            throw new ProviderFailure(503, "Mango could not verify Strata's runtime exited. Stop and release it again.");
        }
        finally { changed(); }
    }

    private async Task WaitForExitAsync(CancellationToken cancellation)
    {
        while (ProcessIds.Length != 0)
            await Task.Delay(100, cancellation);
    }

    private async Task VerifyReleaseAsync(CancellationToken cancellation)
    {
        if (control is null)
            await DetectAsync(cancellation);
        var cleaned = await CommandAsync(["runtime", "cleanup", "--profile", profile, "--output", "json"],
            cancellation, TimeSpan.FromSeconds(25));
        var status = await CommandAsync(["runtime", "status", "--profile", profile, "--output", "json"], cancellation);
        if (!cleaned.IsTrue("ok") || status.Text("state") != "stopped" || ProcessIds.Length != 0)
            throw new ProviderFailure(503, "Strata has not confirmed the Mango runtime released this computer.");
        if (File.Exists(ConfigPath))
            File.Delete(ConfigPath);
        File.Delete(PendingPath);
        pending = false;
        job?.Dispose();
        job = null;
        bearer = instance = activeModel = null;
    }

    private async Task<ProviderInstance?> ObserveAsync(string nativeId, string expected, CancellationToken cancellation)
    {
        var telemetry = await JsonAsync("/v1/telemetry", cancellation);
        if (telemetry.Get("instance").Text("id") != expected || telemetry.Get("instance").Text("state") != "ready" ||
            telemetry.Get("model").Text("id") != nativeId || !telemetry.Get("model").IsTrue("loaded"))
            return null;
        var status = await JsonAsync("/v1/status", cancellation);
        var activity = status.Get("activity").Get("in_flight");
        if (status.Text("model") != nativeId || !status.IsTrue("loaded") ||
            ValidContext(status.Get("context").Number("native")) is not { } context ||
            activity.ValueKind != JsonValueKind.Number || !activity.TryGetInt32(out var inFlight) || inFlight < 0)
            return null;
        return new(nativeId, context, 1, ProviderIdentity.Hash(expected, status.Text("engine")), inFlight != 0,
            NativeGeneration: expected, ConfigurationFingerprint: ProviderIdentity.Hash(status.Text("engine"), context.ToString(CultureInfo.InvariantCulture)));
    }

    public override async Task<bool?> CheckIdleAsync(ProviderModel model, CancellationToken cancellation) =>
        !pending ? true : instance is null ? null : (await ObserveAsync(model.NativeId, instance, cancellation)) is { } live ? !live.Busy : null;

    public override async Task ConfirmDeploymentAsync(InferenceOperation operation, ProviderModel model, ProviderInstance expected,
        CancellationToken cancellation)
    {
        if (instance is null || expected.NativeGeneration != instance ||
            await ObserveAsync(model.NativeId, instance, cancellation) is not { Busy: false } live || live.Fingerprint != expected.Fingerprint)
            throw new ProviderFailure(409, "The verified Strata model is no longer ready.");
    }

    private static int? ValidContext(int? context) => context is > 0 and <= ProviderLimits.MaxContext ? context : null;

    private static int? NativeArgument(JsonObject config, string flag)
    {
        foreach (var key in new[] { "user_args", "args" })
            if (config[key] is JsonArray args)
                for (var i = 0; i + 1 < args.Count; i++)
                    if (args[i]?.GetValueKind() == JsonValueKind.String && args[i]!.GetValue<string>() == flag &&
                        args[i + 1]?.GetValueKind() == JsonValueKind.String &&
                        int.TryParse(args[i + 1]!.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                        return value;
        return null;
    }

    private static void SetNativeArgument(JsonObject config, string flag, int number)
    {
        if (config["user_args"] is not JsonArray args)
        {
            args = [];
            config["user_args"] = args;
        }
        for (var i = args.Count - 1; i >= 0; i--)
            if (args[i]?.GetValueKind() == JsonValueKind.String && args[i]!.GetValue<string>() == flag)
            {
                if (i + 1 < args.Count && args[i + 1]?.GetValueKind() == JsonValueKind.String &&
                    !args[i + 1]!.GetValue<string>().StartsWith("--", StringComparison.Ordinal))
                    args.RemoveAt(i + 1);
                args.RemoveAt(i);
            }
        args.Add(flag);
        args.Add(number.ToString(CultureInfo.InvariantCulture));
    }

    private static async Task<JsonObject?> ReadConfigAsync(string path, string digest, CancellationToken cancellation)
    {
        try
        {
            await using var file = File.OpenRead(path);
            if (file.Length is < 2 or > 1024 * 1024)
                return null;
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, cancellation);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(digest, StringComparison.OrdinalIgnoreCase))
                return null;
            return JsonNode.Parse(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsSpan(3) : bytes) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task DetectAsync(CancellationToken cancellation)
    {
        var found = FindControl(Endpoint.StrataControl);
        if (found is null)
            throw new ProviderFailure(503, "Install Strata Desktop or Terminal, or enter its bundled Control folder.");
        if (control == found && version is not null)
            return;
        control = found;
        version = null;
        var help = await RunAsync(["runtime", "start", "--help"], cancellation, ProviderLimits.ControlTimeout);
        if (help.Exit != 0 || !help.Output.Contains("--die-with-pid", StringComparison.Ordinal))
            throw new ProviderFailure(503, "Update Strata Control to a version with managed runtime ownership.");
        var info = await RunAsync(["--version"], cancellation, ProviderLimits.ControlTimeout);
        var value = info.Output.Trim();
        if (value.IndexOf("; python", StringComparison.Ordinal) is var pathStart && pathStart >= 0)
            value = value[..pathStart] + ")";
        if (info.Exit != 0 || !ProviderIdentity.ValidIdentifier(value))
            throw new ProviderFailure(503, "Strata Control did not report its version.");
        version = value;
    }

    private static string? FindControl(string? folder)
    {
        if (folder is not null)
            return new[] { folder, Path.Combine(folder, "control") }.FirstOrDefault(ValidControl);
        var strata = Path.Combine(Local, "Strata");
        var terminal = Path.Combine(strata, "terminal", "current");
        var active = Path.Combine(strata, "components", "control");
        string? updated = null;
        try
        {
            var pointer = Path.Combine(active, "active.json");
            if (new FileInfo(pointer) is { Exists: true, Length: < 65536 })
            {
                using var json = JsonDocument.Parse(File.ReadAllText(pointer));
                var name = json.RootElement.Text("folder");
                if (!string.IsNullOrEmpty(name) && name is not ("." or "..") &&
                    name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+'))
                    updated = Path.Combine(active, name);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new[] { Environment.GetEnvironmentVariable("STRATA_CONTROL_DIR"), updated,
            Path.Combine(Local, "Programs", "Strata", "control"), Path.Combine(strata, "control"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Strata", "control"), terminal }
            .OfType<string>().FirstOrDefault(ValidControl);
    }

    private static string Python(string path)
    {
        var bundled = Path.Combine(path, "python", "python.exe");
        return File.Exists(bundled) ? bundled : Path.Combine(path, ".venv", "Scripts", "python.exe");
    }

    private static bool ValidControl(string path) => Path.IsPathFullyQualified(path) &&
        File.Exists(Python(path)) && File.Exists(Path.Combine(path, "strata.py")) &&
        Directory.Exists(Path.Combine(path, "strata_control"));

    private async Task<JsonElement> CommandAsync(IEnumerable<string> args, CancellationToken cancellation,
        TimeSpan? timeout = null, WindowsJob? owner = null)
    {
        var (exit, output) = await RunAsync(args, cancellation, timeout ?? ProviderLimits.ControlTimeout, owner);
        if (exit != 0)
            throw new ProviderFailure(502, $"Strata Control reported an unsuccessful operation (exit {exit}).",
                allocationFailure: IsAllocationFailure(output));
        try
        {
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new ProviderFailure(502, "Strata Control returned invalid command output."); }
    }

    private async Task<(int Exit, string Output)> RunAsync(IEnumerable<string> args, CancellationToken cancellation,
        TimeSpan timeout, WindowsJob? owner = null)
    {
        cancellation.ThrowIfCancellationRequested();
        var script = Path.Combine(control!, "strata.py");
        var python = Python(control!);
        var start = new ProcessStartInfo(owner is null ? python : Environment.ProcessPath!)
        {
            WorkingDirectory = control!, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = owner is not null,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in new[] { "PYTHONHOME", "PYTHONPATH", "PYTHONSTARTUP", "VIRTUAL_ENV", "STRATA_PYTHON",
            "STRATA_NATIVE_PYTHON", "STRATA_API_KEY", "STRATA_TELEMETRY_KEY", "STRATA_INSTALL_DIR" })
            start.Environment.Remove(key);
        start.Environment["PYTHONNOUSERSITE"] = start.Environment["PYTHONUTF8"] = "1";
        start.Environment["STRATA_NO_ACQUIRE"] = "1";
        start.Environment["STRATA_CONTROL_DIR"] = control!;
        start.Environment["STRATA_PROFILE_DIR"] = Path.Combine(directory, "profiles");
        start.Environment["STRATA_RUNTIME_DIR"] = RuntimeDirectory;
        if (bearer is not null)
            start.Environment["STRATA_API_KEY"] = bearer;
        if (owner is not null)
        {
            if (Path.GetFileName(start.FileName).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(WindowsJob).Assembly.Location);
            start.ArgumentList.Add(WindowsJob.StarterArgument);
            start.ArgumentList.Add(python);
        }
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(script);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        try
        {
            using var process = Process.Start(start) ?? throw new ProviderFailure(502, "Mango could not start Strata Control.");
            try
            {
                if (owner is not null)
                {
                    owner.Add(process);
                    process.StandardInput.BaseStream.WriteByte((byte)'1');
                    process.StandardInput.Close();
                    changed();
                }
                var stdout = ReadOutputAsync(process.StandardOutput, capture: true, deadline.Token);
                var stderr = ReadOutputAsync(process.StandardError, capture: false, deadline.Token);
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
                return (process.ExitCode, await stdout);
            }
            finally
            {
                if (!process.HasExited)
                    process.Kill();
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ProviderFailure(504, "Strata Control did not finish before Mango's deadline.");
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException)
        {
            throw new ProviderFailure(502, "Mango could not run the bundled Strata Control.");
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, bool capture, CancellationToken cancellation)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellation)) != 0)
        {
            if (!capture)
                continue;
            if (result.Length + count > ProviderLimits.MaxControlBytes)
                throw new ProviderFailure(502, "Strata Control output exceeded the supported size.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
