using System.Reflection;
using Microsoft.Win32;

namespace Mango.Host;

internal sealed class LoginStartup(string? dataDirectory)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "Mango";

    public bool Available => dataDirectory is null;

    public bool IsEnabled()
    {
        if (!Available)
            return false;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(EntryName) is string;
    }

    public void SetEnabled(bool enabled)
    {
        if (!Available)
            throw new ArgumentException("Start at login is available for the default Mango data directory.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            key.DeleteValue(EntryName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Mango's executable path is unavailable.");
        var command = $"\"{executable}\"";
        if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            command += $" \"{Assembly.GetExecutingAssembly().Location}\"";
        command += " --background";
        if (command.Length > 260)
            throw new ArgumentException("Mango's launch command is too long for Windows startup. Move the build to a shorter path.");
        key.SetValue(EntryName, command);
    }
}
