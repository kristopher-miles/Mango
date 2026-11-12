using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text.Json;
using Mango.Peers;

namespace Mango.Host;

internal sealed record UpdateOffer(string Version, int? DelaySeconds = null, Guid? After = null);
internal sealed record UpdateReply(string State);

internal sealed class WindowsUpdates : IDisposable
{
    private readonly SnapshotFile<LocalSettings> settings;
    private readonly PeerNetwork peers;
    private readonly UpdateInstall installer;
    private readonly HttpClient http = new(new SocketsHttpHandler { MaxConnectionsPerServer = 4 })
    {
        Timeout = Timeout.InfiniteTimeSpan,
        MaxResponseContentBufferSize = 256 * 1024
    };
    private readonly UpdateFeed feed;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object sync = new();
    private readonly string? failedVersion;
    private string status;
    private bool checkRequested;
    private (UpdateManifest Release, DateTimeOffset Until)? verified;
    private sealed record QueuedUpdate(string Version, DateTimeOffset Due, Guid? After, DateTimeOffset Deadline);
    private QueuedUpdate? queued;
    private bool installing;

    public WindowsUpdates(string directory, string restartArguments, SnapshotFile<LocalSettings> settings, PeerNetwork peers, bool restarted)
    {
        this.settings = settings;
        this.peers = peers;
        installer = new(directory, restartArguments);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mango", peers.Release.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        feed = new(http, UpdateFeed.ReadPublisher());
        status = installer.Available ? "Ready to check GitHub for updates." : "Automatic installation requires an extracted Windows x64 package.";
        try
        {
            if (File.Exists(installer.Receipt) && JsonSerializer.Deserialize<Receipt>(File.ReadAllText(installer.Receipt),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { } receipt)
            {
                status = receipt.Message;
                failedVersion = receipt.Failed ? receipt.Version : null;
            }
        }
        catch (Exception error) when (Expected(error)) { status = "Mango could not read the previous update result."; }
        if (restarted)
            status = $"Restarted into Mango {peers.Release.Version}.";
    }

    public string Status => Volatile.Read(ref status);
    public bool Available => installer.Available;
    private sealed record Receipt(string Version, bool Failed, string Message);

    public void CheckNow()
    {
        lock (sync)
        {
            if (installing || queued is not null)
                return;
            checkRequested = true;
        }
        Volatile.Write(ref status, "Checking GitHub for updates…");
        Signal();
    }

    public void PreferencesChanged() => Signal();

    public async Task<UpdateReply> OfferAsync(UpdateOffer input, CancellationToken cancellation)
    {
        if (UpdateManifest.ParseVersion(input.Version) is null || input.DelaySeconds is < 0 or > 7200 ||
            input.After == Guid.Empty || input.After is not null && input.After == peers.Snapshot.Self?.Id)
            return new("Invalid version");
        if (!settings.Value.DailyUpdates)
            return new("Updates are off");
        if (settings.Value.MaintenanceDeferred)
            return new("Maintenance deferred");
        if (!Available)
            return new("This installation cannot update automatically");
        if (input.Version == failedVersion)
            return new("A previous install failed");

        if (input.DelaySeconds is { } delay)
        {
            lock (sync)
            {
                if (installing || queued is not null)
                    return new(queued?.Version == input.Version ? "Queued" : "Another update is in progress");
                if (verified is not { } previous || previous.Until < DateTimeOffset.UtcNow || previous.Release.Version != input.Version)
                    return new("Update verification expired");
                var due = DateTimeOffset.UtcNow.AddSeconds(delay);
                queued = new(input.Version, due, input.After, due.AddMinutes(30));
                Volatile.Write(ref status, $"Mango {input.Version} is queued for this host.");
            }
            Signal();
            return new("Queued");
        }

        try
        {
            var candidate = await FindAsync(input.Version, cancellation);
            if (candidate is null)
                return new("Publisher release not found");
            if (candidate.Window != peers.Release.Window)
                return new("Incompatible protocol window");
            if (UpdateManifest.ParseVersion(candidate.Version) <= UpdateManifest.ParseVersion(peers.Release.Version))
                return new("Already current");
            if (!candidate.CanInstall(peers.Release))
                return new("Incompatible protocol window");
            lock (sync)
                verified = (candidate, DateTimeOffset.UtcNow.AddMinutes(5));
            return new("Eligible");
        }
        catch (Exception error) when (Expected(error) && !cancellation.IsCancellationRequested)
        { return new("Could not verify the publisher release"); }
    }

    public async Task<object> ShareAsync(bool allowPartial, CancellationToken cancellation)
    {
        try
        {
            var candidate = await FindAsync(null, cancellation);
            if (candidate is null)
                throw new PeerFailure(409, "No verified Mango release is available to share.");
            if (candidate.Window != peers.Release.Window)
                throw new PeerFailure(409, "The latest release crosses a protocol cutoff. Install it manually before sharing updates in its window.");
            return await peers.ShareUpdateAsync(candidate.Version, allowPartial, cancellation);
        }
        catch (Exception error) when (Expected(error) && !cancellation.IsCancellationRequested)
        { throw new PeerFailure(502, "Mango could not verify the release on GitHub."); }
    }

    public async Task RunAsync(IHostApplicationLifetime lifetime)
    {
        var stopping = lifetime.ApplicationStopping;
        var next = DateTimeOffset.UtcNow.AddMinutes(Random.Shared.Next(5, 16));
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                var automatic = settings.Value.DailyUpdates && !settings.Value.MaintenanceDeferred && Available;
                QueuedUpdate? pending;
                bool manual;
                lock (sync)
                {
                    if (!automatic && queued is not null)
                    {
                        queued = null;
                        Volatile.Write(ref status, "The queued update was canceled by local maintenance preferences.");
                    }
                    pending = queued;
                    manual = checkRequested;
                    checkRequested = false;
                    installing = pending?.Due <= now || pending is null && (manual || automatic && next <= now);
                }
                if (installing)
                {
                    var handedOff = false;
                    try
                    {
                        if (pending?.After is { } previous && !await peers.PredecessorUpdatedAsync(previous, pending.Version, stopping))
                        {
                            var retry = DateTimeOffset.UtcNow.AddMinutes(1);
                            lock (sync)
                                queued = retry < pending.Deadline ? pending with { Due = retry } : null;
                            Volatile.Write(ref status, retry < pending.Deadline
                                ? "Waiting for the previous peer to restart."
                                : "Peer rollout paused because the previous peer has not restarted. Mango will check again tomorrow.");
                            next = DateTimeOffset.UtcNow.AddDays(1);
                            continue;
                        }
                        lock (sync) queued = null;
                        handedOff = await CheckAsync(pending?.Version, lifetime);
                    }
                    finally { lock (sync) installing = false; }
                    if (handedOff)
                        return;
                    next = DateTimeOffset.UtcNow.AddDays(1);
                    continue;
                }
                var due = pending?.Due ?? (automatic ? next : now.AddDays(1));
                await wake.WaitAsync(due > now ? due - now : TimeSpan.Zero, stopping);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private async Task<bool> CheckAsync(string? version, IHostApplicationLifetime lifetime)
    {
        var stopping = lifetime.ApplicationStopping;
        string? staged = null;
        var draining = false;
        var handedOff = false;
        try
        {
            Volatile.Write(ref status, "Checking GitHub for updates…");
            var candidate = await FindAsync(version, stopping);
            if (candidate is null)
                Volatile.Write(ref status, "No verified Mango release is available.");
            else if (UpdateManifest.ParseVersion(candidate.Version) <= UpdateManifest.ParseVersion(peers.Release.Version))
                Volatile.Write(ref status, "Mango is up to date.");
            else if (!candidate.CanInstall(peers.Release))
                Volatile.Write(ref status, $"Mango {candidate.Version} crosses a protocol cutoff and needs a manual install.");
            else if (candidate.Version == failedVersion)
                Volatile.Write(ref status, $"Mango {candidate.Version} previously failed to install. See update-result.json in the data folder.");
            else if (!CanApply())
                Volatile.Write(ref status, $"Mango {candidate.Version} is available. Automatic installation is off or deferred.");
            else
            {
                Volatile.Write(ref status, $"Downloading Mango {candidate.Version}…");
                using var download = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                download.CancelAfter(TimeSpan.FromMinutes(5));
                staged = await installer.StageAsync(feed, candidate, download.Token);
                if (!CanApply())
                {
                    Volatile.Write(ref status, "Installation was deferred by local settings.");
                    return false;
                }
                Volatile.Write(ref status, "Waiting for active inference to finish…");
                draining = true;
                if (!await peers.DrainForMaintenanceAsync(TimeSpan.FromSeconds(60), stopping) || !CanApply())
                {
                    Volatile.Write(ref status, "Mango will retry later. The host was busy or maintenance was deferred.");
                    return false;
                }
                stopping.ThrowIfCancellationRequested();
                await installer.ApplyAsync(staged, candidate.Version, stopping);
                handedOff = true;
                Volatile.Write(ref status, $"Restarting into Mango {candidate.Version}…");
                lifetime.StopApplication();
            }
        }
        catch (Exception error) when (Expected(error) && !stopping.IsCancellationRequested)
        {
            Volatile.Write(ref status, $"Mango could not update. {error.Message}");
        }
        finally
        {
            if (!handedOff)
            {
                if (draining)
                    peers.ResumeAdmissions();
                if (staged is not null)
                    try { installer.Discard(staged); }
                    catch (Exception error) when (Expected(error)) { }
            }
        }
        return handedOff;
    }

    private bool CanApply() => Available && settings.Value.DailyUpdates && !settings.Value.MaintenanceDeferred;

    private async Task<UpdateManifest?> FindAsync(string? version, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        return await feed.FindAsync(version, deadline.Token);
    }

    private void Signal()
    {
        lock (sync)
            if (wake.CurrentCount == 0)
                wake.Release();
    }

    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or
        HttpRequestException or JsonException or OperationCanceledException or Win32Exception;

    public void Dispose()
    {
        http.Dispose();
        wake.Dispose();
    }
}
