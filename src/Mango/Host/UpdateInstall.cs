using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace Mango.Host;

internal sealed class UpdateInstall(string dataDirectory, string restartArguments)
{
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const string Marker = "Mango.package";
    private readonly string install = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
    internal string Receipt => Path.Combine(dataDirectory, "update-result.json");

    internal static FileStream? HoldPackage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, Marker);
        return File.Exists(path) ? File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    internal bool Available => RuntimeInformation.ProcessArchitecture == Architecture.X64 &&
        string.Equals(Environment.ProcessPath, Path.Combine(install, "Mango.exe"), StringComparison.OrdinalIgnoreCase) &&
        File.Exists(Path.Combine(install, Marker)) && File.Exists(Path.Combine(install, "coreclr.dll")) &&
        Path.GetDirectoryName(install) is not null && !InsideInstall(dataDirectory);

    private bool InsideInstall(string path) =>
        path.Equals(install, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal async Task<string> StageAsync(UpdateFeed feed, UpdateManifest manifest, CancellationToken cancellation)
    {
        var parent = new DirectoryInfo(Path.GetDirectoryName(install)!);
        for (DirectoryInfo? directory = new(install); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Automatic updates need a local program folder without junctions or symlinks.");
        var drive = new DriveInfo(parent.Root.FullName);
        if (drive.DriveType != DriveType.Fixed || drive.AvailableFreeSpace < MaxExpandedBytes + manifest.Bytes)
            throw new IOException("The program drive does not have enough local disk space to stage the update.");

        var stage = install + ".update-" + Guid.NewGuid().ToString("N");
        var download = stage + ".zip";
        try
        {
            await feed.DownloadAsync(manifest, download, cancellation);
            using var zip = ZipFile.OpenRead(download);
            if (zip.Entries.Count > 2048 || zip.Entries.Sum(entry => entry.Length) > MaxExpandedBytes)
                throw new InvalidDataException("The signed ZIP exceeds the Windows package limits.");
            cancellation.ThrowIfCancellationRequested();
            zip.ExtractToDirectory(stage);
            var exe = Path.Combine(stage, "Mango.exe");
            if (!File.Exists(exe) || FileVersionInfo.GetVersionInfo(exe).ProductVersion != manifest.Version ||
                File.ReadAllText(Path.Combine(stage, Marker)) != manifest.Platform ||
                !File.Exists(Path.Combine(stage, "wwwroot", "index.html")) || !File.Exists(Path.Combine(stage, "coreclr.dll")))
                throw new InvalidDataException("The Windows package does not match the signed release.");
            return stage;
        }
        catch
        {
            Discard(stage);
            throw;
        }
        finally { File.Delete(download); }
    }

    internal void Discard(string stage)
    {
        if (Path.GetDirectoryName(stage) == Path.GetDirectoryName(install) &&
            stage.StartsWith(install + ".update-", StringComparison.Ordinal) &&
            Guid.TryParseExact(stage[(install.Length + ".update-".Length)..], "N", out _) && Directory.Exists(stage))
            Directory.Delete(stage, recursive: true);
    }

    internal async Task ApplyAsync(string stage, string version, CancellationToken cancellation)
    {
        using var script = new StreamReader(typeof(UpdateInstall).Assembly.GetManifestResourceStream("Mango.ApplyUpdate")!);
        using var current = Process.GetCurrentProcess();
        var helper = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(install)!
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ReadToEnd())) })
            helper.ArgumentList.Add(argument);
        var nonce = Guid.NewGuid().ToString("N");
        helper.Environment["MANGO_UPDATE_INSTALL"] = install;
        helper.Environment["MANGO_UPDATE_STAGE"] = stage;
        var ready = ReadyPath(dataDirectory, nonce);
        helper.Environment["MANGO_UPDATE_READY"] = ready;
        helper.Environment["MANGO_UPDATE_RECEIPT"] = Receipt;
        helper.Environment["MANGO_UPDATE_ARGS"] = restartArguments;
        helper.Environment["MANGO_UPDATE_PID"] = Environment.ProcessId.ToString();
        helper.Environment["MANGO_UPDATE_STARTED"] = current.StartTime.ToUniversalTime().Ticks.ToString();
        helper.Environment["MANGO_UPDATE_NONCE"] = nonce;
        helper.Environment["MANGO_UPDATE_VERSION"] = version;
        using var process = Process.Start(helper) ?? throw new IOException("Mango could not start the update installer.");
        var wait = Stopwatch.StartNew();
        while (!File.Exists(ready + ".armed"))
        {
            if (process.HasExited || wait.Elapsed > TimeSpan.FromSeconds(10))
                throw new IOException("The update installer could not start under this Windows account.");
            await Task.Delay(100, cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        File.WriteAllText(ready + ".go", "");
    }

    internal static string ReadyPath(string directory, string nonce) => Path.Combine(directory, $"update-ready-{nonce}");
}
