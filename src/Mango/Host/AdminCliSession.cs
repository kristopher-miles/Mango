using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mango.Peers;

namespace Mango.Host;

internal sealed class AdminCliSession(string profile)
{
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mango.Admin");
    private readonly byte[] context = SHA256.HashData(Encoding.UTF8.GetBytes(profile));
    private string Pathname => Path.Combine(directory, Convert.ToHexString(context) + ".session");

    public AdminAccess? Read()
    {
        if (!File.Exists(Pathname))
            return null;
        EnsurePrivate();
        if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(Pathname) != PrivateFile)
            throw new IOException("The Mango administration session file must have mode 0600.");
        var value = File.ReadAllText(Pathname);
        if (OperatingSystem.IsWindows())
            value = WindowsSecrets.Open(value, context);
        var session = JsonSerializer.Deserialize<AdminAccess>(value, PeerNetwork.Json);
        return session is { } access && access.ExpiresAt > DateTimeOffset.UtcNow && PeerCredentials.IsSecret(access.Token) ? access : null;
    }

    public void Save(AdminAccess access)
    {
        EnsurePrivate();
        var pending = Pathname + "." + Guid.NewGuid().ToString("N");
        var value = JsonSerializer.Serialize(access, PeerNetwork.Json);
        if (OperatingSystem.IsWindows())
            value = WindowsSecrets.Protect(value, context);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = PrivateFile;
        try
        {
            using (var stream = new StreamWriter(new FileStream(pending, options)))
                stream.Write(value);
            File.Move(pending, Pathname, overwrite: true);
        }
        finally { File.Delete(pending); }
    }

    public void Delete()
    {
        if (File.Exists(Pathname))
            File.Delete(Pathname);
    }

    private void EnsurePrivate()
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory, PrivateDirectory);
            if (File.GetUnixFileMode(directory) != PrivateDirectory)
                throw new IOException("The Mango administration session directory must have mode 0700.");
        }
        else
            Directory.CreateDirectory(directory);
    }
}
