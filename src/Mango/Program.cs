using System.ComponentModel;
using System.Diagnostics;
using Mango.Host;

namespace Mango;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(args);
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }

        var directory = options.DataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mango");
        var diagnostic = Path.Combine(directory, "startup-error.txt");
        FileStream? lease = null;
        try
        {
            var instance = new InstanceControl(directory);
            if (options.ControlOnly)
            {
                if (!Directory.Exists(directory))
                    throw new IOException("Mango is not running for this data directory.");
                Console.WriteLine(instance.ContactAsync(options.Quit).GetAwaiter().GetResult());
                return 0;
            }

            Directory.CreateDirectory(directory);
            lease = instance.TryAcquire();
            if (lease is null)
            {
                if (!options.Background && !options.Headless)
                    OpenBrowser(instance.ContactAsync(quit: false).GetAwaiter().GetResult());
                return 0;
            }

            Run(options, directory, instance, diagnostic);
            return 0;
        }
        catch (Exception error)
        {
            var message = $"Mango could not start or contact its host. {error.Message}";
            Console.Error.WriteLine(message);
            if (lease is not null)
            {
                try
                {
                    File.WriteAllText(diagnostic, message);
                }
                catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Could not write the startup diagnostic: {writeError.Message}");
                }
            }
            lease?.Dispose();
            lease = null;
            if (!options.Headless && !options.ControlOnly)
                MessageBox.Show(message, "Mango", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static void Run(LaunchOptions options, string directory, InstanceControl instance, string diagnostic)
    {
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        settings.LoadAsync(new LocalSettings()).GetAwaiter().GetResult();
        var web = new LocalWeb();
        var app = web.Build(options.Port, directory, settings, new LoginStartup(options.DataDirectory));
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
            var url = web.BrowserUrl(app.Urls.Single());
            var control = instance.ServeAsync(url, app.Lifetime);
            if (control.IsFaulted)
                control.GetAwaiter().GetResult();
            try
            {
                File.Delete(diagnostic);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not remove the previous startup diagnostic: {error.Message}");
            }

            try
            {
                if (!options.Headless)
                    Tray.Run(settings, app.Lifetime, () => OpenBrowser(url), openOnStartup: !options.Background);
            }
            finally
            {
                if (!options.Headless)
                    app.Lifetime.StopApplication();
                app.WaitForShutdownAsync().GetAwaiter().GetResult();
                control.GetAwaiter().GetResult();
            }
        }
        finally
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            MessageBox.Show("Mango could not open your default browser.", "Mango",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed record LaunchOptions(bool Headless, bool Background, bool Quit, bool PrintUrl, string? DataDirectory, int Port)
    {
        public bool ControlOnly => Quit || PrintUrl;

        public static LaunchOptions Parse(string[] args)
        {
            var headless = false;
            var background = false;
            var quit = false;
            var printUrl = false;
            var port = 0;
            string? directory = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--headless": headless = true; break;
                    case "--background": background = true; break;
                    case "--quit": quit = true; break;
                    case "--url": printUrl = true; break;
                    case "--data-dir" when index + 1 < args.Length:
                        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[++index]));
                        break;
                    case "--port" when index + 1 < args.Length && int.TryParse(args[index + 1], out port) && port is >= 0 and <= 65535:
                        index++;
                        break;
                    default:
                        throw new ArgumentException($"Invalid option {args[index]}. Use --headless, --background, --quit, --url, --data-dir <path>, or --port <0-65535>.");
                }
            }
            if ((headless ? 1 : 0) + (background ? 1 : 0) + (quit ? 1 : 0) + (printUrl ? 1 : 0) > 1)
                throw new ArgumentException("Use only one launch mode at a time.");
            return new LaunchOptions(headless, background, quit, printUrl, directory, port);
        }
    }
}
