using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mango.Providers;

internal sealed record LmStudioDevices(JsonElement Link, JsonElement Downloaded, JsonElement Loaded)
{
    private static readonly string Cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lmstudio", "bin", "lms.exe");

    public string? Device(JsonElement row)
    {
        var device = row.Text("deviceIdentifier");
        return string.IsNullOrWhiteSpace(device) || device == Link.Text("deviceIdentifier") ? null : device;
    }

    public string? Name(string device) => Link.Get("peers").Items()
        .FirstOrDefault(peer => peer.Text("deviceIdentifier") == device).Text("deviceName");

    public static async Task<LmStudioDevices?> InspectAsync(Uri origin, CancellationToken cancellation)
    {
        if (!origin.IsLoopback) return null;
        if (!File.Exists(Cli)) return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var server = await ReadAsync(["server", "status"], deadline.Token);
            if (!server.IsTrue("running") || server.Number("port") != origin.Port) return null;
            var records = await Task.WhenAll(ReadAsync(["link", "status"], deadline.Token),
                ReadAsync(["ls"], deadline.Token), ReadAsync(["ps"], deadline.Token));
            return records[0].ValueKind == JsonValueKind.Object && records[1].ValueKind == JsonValueKind.Array &&
                records[2].ValueKind == JsonValueKind.Array ? new(records[0], records[1], records[2]) : null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or JsonException) { return null; }
    }

    public static async Task<JsonElement> ReadLoadedAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try { return await ReadAsync(["ps"], deadline.Token); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return default; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception or JsonException) { return default; }
    }

    public static async Task UnloadLocalAsync(string instance, CancellationToken cancellation)
    {
        var result = await RunAsync(["unload", instance], cancellation);
        if (result.ExitCode != 0) throw new ProviderFailure(503, "LM Studio could not release the verified local instance.");
    }

    public static async Task LoadLocalAsync(Uri origin, string path, int? context, CancellationToken cancellation)
    {
        using var control = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        control.CancelAfter(ProviderLimits.ControlTimeout);
        try
        {
            var server = origin.IsLoopback ? await ReadAsync(["server", "status"], control.Token) : default;
            if (!server.IsTrue("running") || server.Number("port") != origin.Port)
                throw new ProviderFailure(409, "LM Studio CLI is not connected to this managed server.", code: "load_rejected");
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested &&
            error is IOException or UnauthorizedAccessException or Win32Exception or JsonException or OperationCanceledException)
        {
            throw new ProviderFailure(409, "Mango could not verify the local LM Studio server.", code: "load_rejected");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(ProviderLimits.LifecycleTimeout);
        try
        {
            var result = await RunAsync(LocalLoadArguments(path, context), deadline.Token);
            if (result.ExitCode == 1)
            {
                var diagnostic = result.Output + result.Error;
                var message = diagnostic.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
                    ? "Update the LM Studio CLI to load an exact local copy."
                    : ProviderAdapter.IsAllocationFailure(diagnostic) || diagnostic.Contains("insufficient system resources", StringComparison.OrdinalIgnoreCase)
                        ? "LM Studio refused the exact local model load because it reported insufficient memory."
                        : "LM Studio rejected the exact local model load.";
                throw new ProviderFailure(409, message, code: "load_rejected");
            }
            if (result.ExitCode != 0)
                throw new ProviderFailure(502, "The LM Studio CLI exited before confirming the exact local model load.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ProviderFailure(504, "LM Studio did not confirm the exact local model load in time.");
        }
    }

    internal static string[] LocalLoadArguments(string path, int? context) =>
        ["load", "--local", "--exact",
            .. (context is { } limit ? new[] { "--context-length", limit.ToString(System.Globalization.CultureInfo.InvariantCulture) } : []),
            "--", path];

    private static async Task<JsonElement> ReadAsync(string[] arguments, CancellationToken cancellation)
    {
        var result = await RunAsync([.. arguments, "--json"], cancellation);
        if (result.ExitCode != 0 || result.Output.Length > ProviderLimits.MaxControlBytes) return default;
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.Clone();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(Cli)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        cancellation.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Could not start the LM Studio CLI.");
        try
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }
}
