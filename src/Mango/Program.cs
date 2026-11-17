using System.ComponentModel;
using System.Diagnostics;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Mango.Resources;

namespace Mango;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == WindowsJob.StarterArgument)
            return WindowsJob.StartChildAsync(args).GetAwaiter().GetResult();
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

            using var package = UpdateInstall.HoldPackage();
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
        var nodePort = settings.Value.NodePort;
        var loopbackPort = nodePort ?? options.Port ?? (options.DataDirectory is null ? LocalWeb.DefaultPort : 0);
        using var resources = new ResourceProtection(directory, settings);
        var providers = new ProviderCatalog(directory, resources);
        var peers = new PeerNetwork(directory, nodePort ?? options.PeerPort ?? PeerLimits.DefaultPort, providers, settings);
        using var updates = new WindowsUpdates(directory, options.RestartArguments(), settings, peers, options.UpdateReady is not null);
        peers.Updates = updates;
        var ingress = new PublicIngress(directory, peers);
        var web = new LocalWeb();
        try
        {
            resources.InitializeAsync().GetAwaiter().GetResult();
            providers.InitializeAsync().GetAwaiter().GetResult();
            resources.RestoreUnsettled(providers);
            peers.InitializeAsync().GetAwaiter().GetResult();
            ingress.InitializeAsync().GetAwaiter().GetResult();
            var startup = new LoginStartup(options.DataDirectory);
            var app = web.Build(loopbackPort, directory, settings, startup, providers, peers, ingress);
            int? unavailablePort = null;
            try
            {
                try { app.StartAsync().GetAwaiter().GetResult(); }
                catch (Exception error) when (loopbackPort != 0 && PortBinding.IsUnavailable(error))
                {
                    app.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    unavailablePort = loopbackPort;
                    app = web.Build(0, directory, settings, startup, providers, peers, ingress, unavailablePort);
                    app.StartAsync().GetAwaiter().GetResult();
                }
                peers.RefreshTransportAsync(app.Lifetime.ApplicationStopping).GetAwaiter().GetResult();
                RunHost(app, options, directory, instance, diagnostic, settings, web, providers, peers, updates, unavailablePort);
            }
            finally
            {
                app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            ingress.DisposeAsync().AsTask().GetAwaiter().GetResult();
            peers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            providers.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void RunHost(WebApplication app, LaunchOptions options, string directory, InstanceControl instance, string diagnostic,
        SnapshotFile<LocalSettings> settings, LocalWeb web, ProviderCatalog providers, PeerNetwork peers, WindowsUpdates updates,
        int? unavailableLoopbackPort)
    {
        var inventory = providers.RunAsync(app.Lifetime.ApplicationStopping);
        var network = peers.RunAsync(app.Lifetime.ApplicationStopping, refreshTransport: false);
        var protection = providers.Resources!.RunAsync(providers, app.Lifetime.ApplicationStopping);
        var updateChecks = updates.RunAsync(app.Lifetime);
        try
        {
            var url = web.BrowserUrl(app.Urls.Single());
            var portUnavailable = unavailableLoopbackPort is not null || peers.ServicePortUnavailable;
            var control = instance.ServeAsync(url, app.Lifetime);
            if (control.IsFaulted)
                control.GetAwaiter().GetResult();
            if (options.UpdateReady is { } nonce)
            {
                var ready = UpdateInstall.ReadyPath(directory, nonce);
                File.WriteAllText(ready + ".tmp", MangoRelease.Current.Version);
                File.Move(ready + ".tmp", ready);
            }
            try
            {
                if (portUnavailable)
                {
                    var message = unavailableLoopbackPort is { } port ? $"Mango could not bind 127.0.0.1:{port}."
                        : $"Mango could not bind the Tailscale port {peers.ServicePort}.";
                    File.WriteAllText(diagnostic, $"{message} Open System → Node port to choose another port.");
                }
                else File.Delete(diagnostic);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Could not remove the previous startup diagnostic: {error.Message}");
            }

            try
            {
                if (!options.Headless)
                    Tray.Run(settings, app.Lifetime, () => OpenBrowser(url), openOnStartup: !options.Background || portUnavailable);
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
            app.Lifetime.StopApplication();
            Task.WhenAll(inventory, network, protection, updateChecks).GetAwaiter().GetResult();
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

    private sealed record LaunchOptions(bool Headless, bool Background, bool Quit, bool PrintUrl, string? DataDirectory, int? Port,
        int? PeerPort, string? UpdateReady)
    {
        public bool ControlOnly => Quit || PrintUrl;

        public string RestartArguments() => (Headless ? "--headless" : "--background") +
            (Port is { } port ? $" --port {port}" : "") + (PeerPort is { } peerPort ? $" --peer-port {peerPort}" : "") +
            (DataDirectory is null ? "" : " --data-dir \"" + DataDirectory +
                (Path.EndsInDirectorySeparator(DataDirectory) ? "\\" : "") + "\"");

        public static LaunchOptions Parse(string[] args)
        {
            var headless = false;
            var background = false;
            var quit = false;
            var printUrl = false;
            int? port = null;
            int? peerPort = null;
            string? directory = null;
            string? updateReady = null;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--headless": headless = true; break;
                    case "--background": background = true; break;
                    case "--quit": quit = true; break;
                    case "--url": printUrl = true; break;
                    case "--update-ready" when index + 1 < args.Length && Guid.TryParseExact(args[index + 1], "N", out _):
                        updateReady = args[++index];
                        break;
                    case "--data-dir" when index + 1 < args.Length:
                        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[++index]));
                        break;
                    case "--port" when index + 1 < args.Length && int.TryParse(args[index + 1], out var localPort) && localPort is >= 0 and <= 65535:
                        port = localPort;
                        index++;
                        break;
                    case "--peer-port" when index + 1 < args.Length && int.TryParse(args[index + 1], out var servicePort) && servicePort is >= 1 and <= 65535:
                        peerPort = servicePort;
                        index++;
                        break;
                    default:
                        throw new ArgumentException($"Invalid option {args[index]}. Use --headless, --background, --quit, --url, --data-dir <path>, --port <0-65535>, or --peer-port <1-65535>.");
                }
            }
            if ((headless ? 1 : 0) + (background ? 1 : 0) + (quit ? 1 : 0) + (printUrl ? 1 : 0) > 1)
                throw new ArgumentException("Use only one launch mode at a time.");
            return new LaunchOptions(headless, background, quit, printUrl, directory, port, peerPort, updateReady);
        }
    }
}
