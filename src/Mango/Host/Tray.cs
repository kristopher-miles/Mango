using System.Drawing;

namespace Mango.Host;

internal static class Tray
{
    public static void Run(SnapshotFile<LocalSettings> settings, IHostApplicationLifetime lifetime,
        Action openBrowser, bool openOnStartup)
    {
        ApplicationConfiguration.Initialize();
        using var window = new Control();
        _ = window.Handle;
        using var context = new ApplicationContext();
        using var menu = new ContextMenuStrip();
        using var icon = new NotifyIcon
        {
            Text = "Mango",
            Icon = SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        menu.Items.Add("Open Mango", null, (_, _) => openBrowser());
        var role = menu.Items.Add("");
        menu.Opening += (_, _) => role.Text = settings.Value.Role == LocalRole.Leech
            ? "Prefer contributor mode"
            : "Use leech mode";
        Task<string?>? pendingRoleChange = null;
        role.Click += async (_, _) =>
        {
            role.Enabled = false;
            pendingRoleChange = Task.Run(() => SwitchRoleAsync(settings));
            var error = await pendingRoleChange;
            pendingRoleChange = null;
            if (error is not null)
                icon.ShowBalloonTip(5000, "Mango could not save the role", error, ToolTipIcon.Error);
            role.Enabled = true;
        };
        menu.Items.Add("Quit Mango", null, (_, _) => lifetime.StopApplication());
        icon.DoubleClick += (_, _) => openBrowser();
        using var stop = lifetime.ApplicationStopping.Register(() => window.BeginInvoke(context.ExitThread));
        if (openOnStartup)
            openBrowser();
        Application.Run(context);
        if (pendingRoleChange?.GetAwaiter().GetResult() is { } error)
            Console.Error.WriteLine($"Mango could not save the role: {error}");
    }

    private static async Task<string?> SwitchRoleAsync(SnapshotFile<LocalSettings> settings)
    {
        try
        {
            await settings.UpdateAsync(current => current with
            {
                Role = current.Role == LocalRole.Leech ? LocalRole.Contributor : LocalRole.Leech
            });
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }
}
