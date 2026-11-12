using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Mango.Peers;

internal sealed record TailnetStatus(string? Device, IPAddress[] LocalAddresses, TailnetCandidate[] Candidates, string Message)
{
    public static readonly TailnetStatus Waiting = new(null, [], [], "Checking Tailscale.");
}

internal sealed record TailnetCandidate(string Device, IPAddress[] Addresses);

internal static class Tailnet
{
    public static bool Contains(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64 :
            bytes.AsSpan(0, 6).SequenceEqual(new byte[] { 0xFD, 0x7A, 0x11, 0x5C, 0xA1, 0xE0 });
    }

    public static Uri? Address(string? input, int defaultPort = PeerLimits.DefaultPort)
    {
        if (input is not { Length: > 0 and <= 128 })
            return null;
        var value = input.Trim();
        if (IPAddress.TryParse(value, out var address) && Contains(address))
            return new UriBuilder("http", address.ToString(), defaultPort).Uri;
        if (!value.Contains("://", StringComparison.Ordinal))
            value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !IPAddress.TryParse(uri.DnsSafeHost, out address) || !Contains(address))
            return null;
        return new UriBuilder("http", address.ToString(), uri.IsDefaultPort && !ExplicitPort(value) ? defaultPort : uri.Port).Uri;
    }

    private static bool ExplicitPort(string value)
    {
        var host = value[7..].TrimEnd('/');
        return host.StartsWith('[') ? host.Contains("]:", StringComparison.Ordinal) : host.Contains(':');
    }

    public static string Origin(IPAddress address, int port) =>
        new UriBuilder("http", address.ToString(), port).Uri.GetLeftPart(UriPartial.Authority);

    public static async Task<TailnetStatus> ReadAsync(CancellationToken stopping)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(executable))
            return new(null, [], [], "Tailscale is missing. Install and sign in to the Windows Tailscale client.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("status");
            start.ArgumentList.Add("--json");
            using var process = Process.Start(start);
            if (process is null)
                return new(null, [], [], "Tailscale status could not start.");
            string json;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                json = await output;
                await error;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                    process.Kill();
                throw;
            }
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var state = String(root, "BackendState");
            if (state != "Running")
                return new(null, [], [], "Tailscale is disconnected. Sign in or reconnect before enrolling.");
            if (!root.TryGetProperty("Self", out var self) || String(self, "ID") is not { Length: > 0 } device)
                return new(null, [], [], "Tailscale did not report this device's identity.");
            var bound = NetworkInterface.GetAllNetworkInterfaces()
                .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                    (item.Name.Equals("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                     item.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase)))
                .SelectMany(item => item.GetIPProperties().UnicastAddresses.Select(entry => entry.Address))
                .ToHashSet();
            var local = Addresses(root).Where(bound.Contains).ToArray();
            var candidates = root.TryGetProperty("Peer", out var peers) && peers.ValueKind == JsonValueKind.Object
                ? peers.EnumerateObject().Select(peer => new TailnetCandidate(String(peer.Value, "ID") ?? "",
                    Addresses(peer.Value))).Where(peer => peer.Addresses.Length > 0)
                    .Take(PeerLimits.DiscoveryCandidates).ToArray() : [];
            return new(device, local, candidates, local.Length > 0
                ? "Tailscale is connected." : "Tailscale has no active local interface address.");
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            return new(null, [], [], "Tailscale status timed out.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or JsonException or NetworkInformationException)
        {
            return new(null, [], [], "Tailscale status is unavailable. Check the client and this user's permissions.");
        }
    }

    private static string? String(JsonElement entry, string property) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IPAddress[] Addresses(JsonElement entry) =>
        entry.TryGetProperty("TailscaleIPs", out var addresses) && addresses.ValueKind == JsonValueKind.Array
            ? addresses.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => IPAddress.TryParse(value.GetString(), out var address) ? address : null)
                .OfType<IPAddress>().Where(Contains).OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Distinct().ToArray()
            : [];
}
