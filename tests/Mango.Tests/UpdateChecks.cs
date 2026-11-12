using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Mango.Host;

internal static partial class PeerChecks
{
    private static async Task TestUpdateAsync(string root)
    {
        using var key = RSA.Create(3072);
        var publisher = key.ExportSubjectPublicKeyInfoPem();
        byte[] zip = [1, 2, 3, 4];
        var manifest = new UpdateManifest("0.0.3", 1, false, "win-x64", zip.Length, Convert.ToHexString(SHA256.HashData(zip)));
        var metadata = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var signature = key.SignData(metadata, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        Check(UpdateFeed.Verify(metadata, signature, publisher, "0.0.3") == manifest, "publisher signature validates the release");
        Check(manifest.CanInstall(new MangoRelease("0.0.2", 1)) &&
            !manifest.CanInstall(new MangoRelease("0.0.3", 1)) &&
            !manifest.CanInstall(new MangoRelease("0.0.4", 1)) &&
            !manifest.CanInstall(new MangoRelease("0.0.2", 2)) &&
            !(manifest with { Cutoff = true }).CanInstall(new MangoRelease("0.0.2", 1)),
            "only newer releases in the current protocol window can auto-install");
        try
        {
            UpdateFeed.Verify(metadata, signature, publisher, "0.0.4");
            throw new Exception("Signed metadata must match the GitHub tag.");
        }
        catch (InvalidDataException) { }
        metadata[^2] ^= 1;
        try
        {
            UpdateFeed.Verify(metadata, signature, publisher, "0.0.3");
            throw new Exception("Modified metadata must fail verification.");
        }
        catch (InvalidDataException) { }

        using var http = new HttpClient(new UpdateAssetHandler(zip));
        var feed = new UpdateFeed(http, publisher);
        var download = Path.Combine(root, "update-fixture.zip");
        await feed.DownloadAsync(manifest, download, default);
        Check(File.ReadAllBytes(download).SequenceEqual(zip), "download matches the signed ZIP");
        zip[0] ^= 1;
        try
        {
            await feed.DownloadAsync(manifest, download + ".tampered", default);
            throw new Exception("A modified ZIP must fail verification.");
        }
        catch (InvalidDataException) { }
    }

    private sealed class UpdateAssetHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
