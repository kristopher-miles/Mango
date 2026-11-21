using System.Diagnostics;
using System.Text;
using Mango.Host;
using Mango.Peers;
using Mango.Providers;
using Mango.Resources;
#if WINDOWS
using System.ComponentModel;
#endif

namespace Mango;

internal static class Program
{
    private const string Usage = "Mango options: --headless, --background (Windows), --quit, --url, --data-dir <path>, --port <0-65535>, --peer-port <1-65535>, --pair <code> or --join <peer-address> <network-id>, with optional --name <node-name>.";

    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && args.FirstOrDefault() == WindowsJob.StarterArgument)
            return WindowsJob.StartChildAsync(args).GetAwaiter().GetResult();
        if (args is ["--help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }
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

            if (OperatingSystem.IsLinux())
            {
                const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Directory.CreateDirectory(directory, privateDirectory);
                if (File.GetUnixFileMode(directory) != privateDirectory)
                    throw new IOException("Mango's data directory must have permissions 0700.");
            }
            else
                Directory.CreateDirectory(directory);
            lease = instance.TryAcquire();
            if (lease is null)
            {
                if (options.Join is not null)
                    throw new IOException("Stop the Mango host for this data directory before enrolling.");
#if WINDOWS
                if (!options.Background && !options.Headless)
                    OpenBrowser(instance.ContactAsync(quit: false).GetAwaiter().GetResult());
#endif
                return 0;
            }

            using var package = OperatingSystem.IsWindows() ? UpdateInstall.HoldPackage() : null;
            Run(options, directory, instance, diagnostic);
            return 0;
        }
        catch (Exception error)
        {
            var message = $"Mango could not start or contact its host. {error.Message}";
            Console.Error.WriteLine(message);
            if (lease is not null && options.Join is null)
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
#if WINDOWS
            if (!options.Headless && !options.ControlOnly && options.Join is null)
                MessageBox.Show(message, "Mango", MessageBoxButtons.OK, MessageBoxIcon.Error);
#endif
            return 1;
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static void Run(LaunchOptions options, string directory, InstanceControl instance, string diagnostic)
    {
        var windows = OperatingSystem.IsWindows();
        var settings = new SnapshotFile<LocalSettings>(Path.Combine(directory, "local-settings.json"), LocalSettings.Validate);
        settings.LoadAsync(new LocalSettings()).GetAwaiter().GetResult();
        var nodePort = settings.Value.NodePort;
        var loopbackPort = nodePort ?? options.Port ?? (options.DataDirectory is null ? LocalWeb.DefaultPort : 0);
        using var resources = windows ? new ResourceProtection(directory, settings) : null;
        var providers = new ProviderCatalog(directory, resources);
        var peers = new PeerNetwork(directory, nodePort ?? options.PeerPort ?? PeerLimits.DefaultPort, providers, settings);
        using var updates = windows
            ? new WindowsUpdates(directory, options.RestartArguments(), settings, peers, options.UpdateReady is not null) : null;
        peers.Updates = updates;
        var ingress = windows ? new PublicIngress(directory, peers) : null;
        var web = new LocalWeb();
        try
        {
            resources?.InitializeAsync().GetAwaiter().GetResult();
            providers.InitializeAsync().GetAwaiter().GetResult();
            resources?.RestoreUnsettled(providers);
            peers.InitializeAsync().GetAwaiter().GetResult();
            if (options.Join is { } join)
            {
                peers.RefreshTransportAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (join.PairingSecret is null)
                    join = join with { Password = ReadJoinPassword() };
                var joined = peers.JoinAsync(join, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine($"Enrolled leech node {joined.Node} in Mango network {joined.Identity.Id}.");
                return;
            }
            ingress?.InitializeAsync().GetAwaiter().GetResult();
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
            ingress?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            peers.DisposeAsync().AsTask().GetAwaiter().GetResult();
            providers.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void RunHost(WebApplication app, LaunchOptions options, string directory, InstanceControl instance, string diagnostic,
        SnapshotFile<LocalSettings> settings, LocalWeb web, ProviderCatalog providers, PeerNetwork peers, WindowsUpdates? updates,
        int? unavailableLoopbackPort)
    {
        var inventory = providers.Resources is null ? Task.CompletedTask : providers.RunAsync(app.Lifetime.ApplicationStopping);
        var network = peers.RunAsync(app.Lifetime.ApplicationStopping, refreshTransport: false);
        var protection = providers.Resources?.RunAsync(providers, app.Lifetime.ApplicationStopping) ?? Task.CompletedTask;
        var updateChecks = updates?.RunAsync(app.Lifetime) ?? Task.CompletedTask;
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
#if WINDOWS
                if (!options.Headless)
                    Tray.Run(settings, app.Lifetime, () => OpenBrowser(url), openOnStartup: !options.Background || portUnavailable);
#endif
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

    private static string? ReadJoinPassword()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine();
        Console.Error.Write("Mango network master password: ");
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return password.ToString();
            }
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                password.Length--;
            else if (!char.IsControl(key.KeyChar))
                password.Append(key.KeyChar);
        }
    }

#if WINDOWS
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
#endif

    private sealed record LaunchOptions(bool Headless, bool Background, bool Quit, bool PrintUrl, string? DataDirectory, int? Port,
        int? PeerPort, string? UpdateReady, JoinNetworkRequest? Join)
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
            string? joinAddress = null;
            string? nodeName = null;
            string? pairingSecret = null;
            var network = Guid.Empty;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--headless": headless = true; break;
                    case "--background" when OperatingSystem.IsWindows(): background = true; break;
                    case "--quit": quit = true; break;
                    case "--url": printUrl = true; break;
                    case "--update-ready" when OperatingSystem.IsWindows() && index + 1 < args.Length && Guid.TryParseExact(args[index + 1], "N", out _):
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
                    case "--join" when joinAddress is null && index + 2 < args.Length &&
                        Tailnet.Address(args[index + 1]) is { } target && Guid.TryParse(args[index + 2], out network) && network != Guid.Empty:
                        joinAddress = target.GetLeftPart(UriPartial.Authority);
                        index += 2;
                        break;
                    case "--pair" when joinAddress is null && index + 1 < args.Length &&
                        PairingCode.Parse(args[index + 1]) is { } pairing:
                        joinAddress = pairing.Target.GetLeftPart(UriPartial.Authority);
                        network = pairing.Network;
                        pairingSecret = pairing.Secret;
                        index++;
                        break;
                    case "--name" when nodeName is null && index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal):
                        nodeName = args[++index];
                        break;
                    default:
                        throw new ArgumentException($"Invalid command line. {Usage}");
                }
            }
            if ((headless ? 1 : 0) + (background ? 1 : 0) + (quit ? 1 : 0) + (printUrl ? 1 : 0) + (joinAddress is not null ? 1 : 0) > 1)
                throw new ArgumentException("Use only one launch mode at a time.");
            if (nodeName is not null && joinAddress is null)
                throw new ArgumentException("Use --name with --pair or --join.");
            var join = joinAddress is null ? null :
                new JoinNetworkRequest(nodeName ?? Environment.MachineName, joinAddress, network, null,
                    BenchmarkPolicy.Never, LocalRole.Leech, pairingSecret);
            return new LaunchOptions(headless || !OperatingSystem.IsWindows(), background, quit, printUrl, directory, port, peerPort, updateReady, join);
        }
    }
}
