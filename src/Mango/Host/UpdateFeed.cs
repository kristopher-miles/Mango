using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mango.Host;

internal sealed record UpdateManifest(string Version, int Window, bool Cutoff, string Platform, long Bytes, string Sha256)
{
    public const long MaxBytes = 256L * 1024 * 1024;

    internal static Version? ParseVersion(string? value) =>
        value is { Length: > 0 and <= 17 } && System.Version.TryParse(value, out var version) &&
        version.Build is >= 0 and <= 65534 && version.Revision == -1 && version.Major <= 65534 && version.Minor <= 65534 &&
        version.ToString() == value ? version : null;

    internal bool CanInstall(MangoRelease installed) => !Cutoff && Window == installed.Window &&
        ParseVersion(installed.Version) is { } current && ParseVersion(Version) > current;
}

internal sealed class UpdateFeed(HttpClient http, string publisher)
{
    private const string Repository = "kristopher-miles/Mango";
    internal const string Archive = "Mango-win-x64.zip";
    private const string Manifest = "mango-update.json";
    private const string Signature = "mango-update.sig";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Asset(string Name, long Size);
    private sealed record Release(string Tag_Name, bool Draft, bool Prerelease, Asset[] Assets);

    internal static string ReadPublisher()
    {
        using var reader = new StreamReader(typeof(UpdateFeed).Assembly.GetManifestResourceStream("Mango.UpdatePublisher")!);
        return reader.ReadToEnd();
    }

    internal async Task<UpdateManifest?> FindAsync(string? requested, CancellationToken cancellation)
    {
        if (requested is not null && UpdateManifest.ParseVersion(requested) is null)
            return null;
        var path = requested is null ? "latest" : $"tags/v{requested}";
        using var response = await http.GetAsync($"https://api.github.com/repos/{Repository}/releases/{path}", cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(Json, cancellation);
        if (release is not { Draft: false, Prerelease: false, Tag_Name: { Length: > 1 } tag, Assets: { } assets } ||
            tag[0] != 'v' || UpdateManifest.ParseVersion(tag[1..]) is null || (requested is not null && tag != $"v{requested}") ||
            !assets.Any(a => a.Name == Archive && a.Size is > 0 and <= UpdateManifest.MaxBytes) ||
            !assets.Any(a => a.Name == Manifest && a.Size is > 0 and <= 4096) ||
            !assets.Any(a => a.Name == Signature && a.Size == 384))
            return null;
        var version = tag[1..];
        var metadata = http.GetByteArrayAsync(Url(version, Manifest), cancellation);
        var signature = http.GetByteArrayAsync(Url(version, Signature), cancellation);
        await Task.WhenAll(metadata, signature);
        var manifest = Verify(await metadata, await signature, publisher, version);
        if (!assets.Any(a => a.Name == Archive && a.Size == manifest.Bytes))
            throw new InvalidDataException("The GitHub ZIP size does not match the signed release.");
        return manifest;
    }

    internal static UpdateManifest Verify(byte[] metadata, byte[] signature, string publisher, string version)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publisher);
        if (metadata.Length is 0 or > 4096 || !rsa.VerifyData(metadata, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new InvalidDataException("Mango could not verify the publisher's release signature.");
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(metadata, Json);
        if (manifest is not { Window: > 0, Platform: "win-x64", Bytes: > 0 and <= UpdateManifest.MaxBytes, Sha256.Length: 64 } ||
            manifest.Version != version || ParseHash(manifest.Sha256) is null)
            throw new InvalidDataException("The signed release metadata is invalid.");
        return manifest;
    }

    internal async Task DownloadAsync(UpdateManifest manifest, string path, CancellationToken cancellation)
    {
        using var response = await http.GetAsync(Url(manifest.Version, Archive), HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != manifest.Bytes)
            throw new InvalidDataException("The downloaded ZIP has an unexpected size.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var body = await response.Content.ReadAsStreamAsync(cancellation);
        await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[65536];
        long received = 0;
        for (int count; (count = await body.ReadAsync(buffer, cancellation)) != 0;)
        {
            if ((received += count) > manifest.Bytes)
                throw new InvalidDataException("The downloaded ZIP exceeded its signed size.");
            hash.AppendData(buffer.AsSpan(0, count));
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
        if (received != manifest.Bytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), ParseHash(manifest.Sha256)!))
            throw new InvalidDataException("The downloaded ZIP does not match the signed release.");
    }

    private static byte[]? ParseHash(string text)
    {
        try { return Convert.FromHexString(text); }
        catch (FormatException) { return null; }
    }

    private static string Url(string version, string asset) =>
        $"https://github.com/{Repository}/releases/download/v{version}/{asset}";
}
