using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Mango.Host;

internal sealed class LinuxUpdateInstall(string dataDirectory) : IUpdateInstall
{
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private static readonly UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly string? root = Environment.GetEnvironmentVariable("MANGO_MANAGED_ROOT");
    private readonly string install = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public string Platform => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "linux-x64",
        Architecture.Arm64 => "linux-arm64",
        _ => "linux-unsupported"
    };

    public string Receipt => Path.Combine(dataDirectory, "update-result.json");

    public bool Available => OperatingSystem.IsLinux() && Platform != "linux-unsupported" &&
        root == Path.GetDirectoryName(install) && root is not null && File.Exists(Path.Combine(root, "current")) &&
        Environment.ProcessPath == Path.Combine(install, "Mango") && !dataDirectory.StartsWith(install + "/", StringComparison.Ordinal) &&
        dataDirectory != install && File.Exists(Path.Combine(install, "libcoreclr.so"));

    public async Task<string> StageAsync(UpdateFeed feed, UpdateManifest manifest, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsLinux() || !Available)
            throw new IOException("Linux automatic updates require the managed systemd launcher.");
        var stage = Path.Combine(root!, "stage-" + Guid.NewGuid().ToString("N"));
        var download = stage + ".zip";
        try
        {
            await feed.DownloadAsync(manifest, download, cancellation);
            using var zip = ZipFile.OpenRead(download);
            if (zip.Entries.Count > 2048 || zip.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
                throw new InvalidDataException("The signed ZIP exceeds Mango's package limits.");
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(stage, Executable);
            zip.ExtractToDirectory(stage);
            var executable = Path.Combine(stage, "Mango");
            if (!File.Exists(executable) || File.ReadAllText(Path.Combine(stage, "Mango.package")) != Platform ||
                !File.Exists(Path.Combine(stage, "wwwroot", "index.html")) || !File.Exists(Path.Combine(stage, "libcoreclr.so")))
                throw new InvalidDataException("The Linux package does not match the signed release.");
            File.SetUnixFileMode(executable, Executable);
            return stage;
        }
        catch
        {
            Discard(stage);
            throw;
        }
        finally { File.Delete(download); }
    }

    public Task ApplyAsync(string stage, string version, CancellationToken cancellation)
    {
        if (!IsStage(stage) || UpdateManifest.ParseVersion(version) is null)
            throw new InvalidDataException("The staged Linux update is invalid.");
        var name = $"release-{version}-{Guid.NewGuid():N}";
        var release = Path.Combine(root!, name);
        var pending = Path.Combine(root!, "pending");
        var temporary = pending + "." + Guid.NewGuid().ToString("N");
        cancellation.ThrowIfCancellationRequested();
        Directory.Move(stage, release);
        try
        {
            File.WriteAllText(temporary, name);
            File.Move(temporary, pending);
        }
        catch
        {
            Directory.Move(release, stage);
            throw;
        }
        finally { File.Delete(temporary); }
        return Task.CompletedTask;
    }

    public void Discard(string stage)
    {
        if (IsStage(stage) && Directory.Exists(stage))
            Directory.Delete(stage, recursive: true);
    }

    private bool IsStage(string path) => root is not null && Path.GetDirectoryName(path) == root &&
        Path.GetFileName(path) is { Length: 38 } name && name.StartsWith("stage-", StringComparison.Ordinal) &&
        Guid.TryParseExact(name.AsSpan(6), "N", out _);
}
