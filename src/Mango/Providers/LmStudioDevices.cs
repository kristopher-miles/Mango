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

    private static async Task<JsonElement> ReadAsync(string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(Cli)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start) ?? throw new IOException("Could not start the LM Studio CLI.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);
            var output = await stdout;
            await stderr;
            if (process.ExitCode != 0 || output.Length > ProviderLimits.MaxControlBytes) return default;
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        finally
        {
            if (!process.HasExited)
                try { process.Kill(); } catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }
    }
}
