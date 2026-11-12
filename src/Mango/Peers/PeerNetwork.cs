using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mango.Host;
using Mango.Providers;

namespace Mango.Peers;

internal sealed partial class PeerNetwork(string directory, int port, ProviderCatalog providers,
    SnapshotFile<LocalSettings> settings, Func<CancellationToken, Task<TailnetStatus>>? inspectTailnet = null,
    TimeProvider? time = null) : IAsyncDisposable
{
    private readonly SnapshotFile<PeerDisk> store = new(Path.Combine(directory, "network.json"), PeerValidation.Disk);
    private readonly byte[] credentialContext = SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).ToUpperInvariant()));
    private readonly SemaphoreSlim writer = new(1);
    private readonly SemaphoreSlim transport = new(1);
    private readonly SemaphoreSlim reconciliation = new(1);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<Guid, string> mismatches = new();
    private readonly ConcurrentDictionary<Guid, PeerContact> contacts = new();
    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = 8,
        ConnectTimeout = PeerLimits.CallTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = PeerLimits.ControlBytes };

    private PeerSnapshot snapshot = new(PeerDisk.Empty, null);
    private TailnetStatus tailnet = TailnetStatus.Waiting;
    private WebApplication? listener;
    private IPAddress[] bound = [];
    private string? listenerError;
    private volatile bool ready;
    private int refreshInterface;
    private int refreshCatalog;
    private ReconciliationStatus lastReconciliation = new(null, false);
    private bool needsPeerConfirmation;
    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public PeerSnapshot Snapshot => Volatile.Read(ref snapshot);
    public bool Ready => ready;
    public bool IsMember => Snapshot.Self is not null && !DeviceChanged(Snapshot);
    public bool AllowsLocalInference => Snapshot.Self is null || Ready;
    internal static JsonSerializerOptions Json => SnapshotFile<PeerDisk>.Json;

    public object View(HttpRequest request)
    {
        var current = Snapshot;
        var status = Volatile.Read(ref tailnet);
        var reconciliationStatus = Volatile.Read(ref lastReconciliation);
        var contact = current.Others.Select(node => new
        {
            node.Id, node.Name, node.Address, node.Joined,
            LastContact = contacts.GetValueOrDefault(node.Id)?.LastContact,
            Current = contacts.GetValueOrDefault(node.Id) is { Reachable: true } last && last.Digest == current.Digest
        });
        return new
        {
            Identity = current.Records.Identity,
            LocalNode = current.Self is { } self ? new { self.Id, self.Name, self.Address, self.Joined } : null,
            Reenroll = current.Records.Identity is not null && (!IsMember || DeviceChanged(current)),
            Ready, Isolated = reconciliationStatus.Isolated, LastReconciled = reconciliationStatus.At,
            Tailnet = new { status.Message, Addresses = bound.Select(address => Tailnet.Origin(address, port)).ToArray(), Error = listenerError },
            RecoveryWarning = store.RecoveryWarning,
            Nodes = contact.ToArray(),
            Keys = current.Records.Keys.Select(KeyView).ToArray(),
            Models = current.Records.Models.Where(model => !model.Removed).ToArray(),
            Catalogs = current.Records.Catalogs,
            Unlocked = HasAdmin(current, AdminHeader(request))
        };
    }

    public async Task InitializeAsync()
    {
        await store.LoadAsync(PeerDisk.Empty);
        needsPeerConfirmation = store.RecoveryWarning is not null;
        var disk = store.Value;
        string? token = null;
        if (disk.Local?.ProtectedSecret is { } protectedSecret)
        {
            try { token = WindowsSecrets.Open(protectedSecret, credentialContext); }
            catch (Exception error) when (error is CryptographicException or FormatException) { }
        }
        Volatile.Write(ref snapshot, new(disk, token));
        providers.Changed += QueueCatalog;
        if (providers.Resources is { } protection)
            protection.Changed += QueueCatalog;
        settings.Changed += QueueCatalog;
    }

    public async Task RunAsync(CancellationToken hostStopping)
    {
        using var registration = hostStopping.Register(stopping.Cancel);
        var cancellation = stopping.Token;
        NetworkChange.NetworkAddressChanged += AddressChanged;
        try
        {
            await RefreshTransportAsync(cancellation);
            await ReconcileAsync(cancellation, gate: true);
            for (var retry = 0; !Ready && retry < 3; retry++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 << retry), cancellation);
                await ReconcileAsync(cancellation, gate: true);
            }
            QueueCatalog();
            var daily = NextDaily();
            while (!cancellation.IsCancellationRequested)
            {
                var delay = daily - clock.GetUtcNow();
                if (delay > TimeSpan.Zero)
                    await wake.WaitAsync(delay, cancellation);
                try
                {
                    if (Interlocked.Exchange(ref refreshInterface, 0) != 0 || clock.GetUtcNow() >= daily)
                    {
                        var changed = await RefreshTransportAsync(cancellation);
                        await ReconcileAsync(cancellation, gate: changed || !Ready);
                        daily = NextDaily();
                    }
                    foreach (var item in mismatches.ToArray())
                        if (mismatches.TryRemove(item) && Snapshot.Nodes.TryGetValue(item.Key, out var node) && !node.Removed)
                            await ReconcileOneAsync(node, cancellation);
                    if (Interlocked.Exchange(ref refreshCatalog, 0) != 0)
                        await PublishCatalogAsync(cancellation);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or PeerFailure)
                {
                    listenerError = "Mango could not persist or reconcile the latest network change. Use Refresh to retry.";
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= AddressChanged;
        }
    }

    private DateTimeOffset NextDaily() => clock.GetUtcNow().AddHours(24).AddMinutes(-Random.Shared.Next(0, 31));

    private void AddressChanged(object? sender, EventArgs args)
    {
        Volatile.Write(ref refreshInterface, 1);
        Wake();
    }

    private void QueueCatalog()
    {
        Volatile.Write(ref refreshCatalog, 1);
        Wake();
    }

    private void Wake()
    {
        try { wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void QueueMismatch(Guid node, string? digest)
    {
        var current = Snapshot;
        if (PeerCredentials.IsSecret(digest) && digest != current.Digest && current.Self is not null &&
            current.Nodes.TryGetValue(node, out var known) && !known.Removed && mismatches.TryAdd(node, digest!))
            Wake();
    }

    private async Task<bool> RefreshTransportAsync(CancellationToken cancellation)
    {
        await transport.WaitAsync(cancellation);
        try
        {
            var previous = tailnet;
            var status = await (inspectTailnet ?? Tailnet.ReadAsync)(cancellation);
            Volatile.Write(ref tailnet, status);
            var changed = previous.Device != status.Device || !bound.SequenceEqual(status.LocalAddresses);
            if (changed && Snapshot.Self is not null)
                ready = false;
            if (!bound.SequenceEqual(status.LocalAddresses))
            {
                await StopListenerAsync();
                if (status.LocalAddresses.Length != 0)
                {
                    try
                    {
                        listener = await PeerRoutes.StartAsync(this, providers, status.LocalAddresses, port, cancellation);
                        bound = status.LocalAddresses;
                        listenerError = null;
                    }
                    catch (IOException)
                    {
                        listenerError = $"Mango could not bind the Tailscale service port {port}.";
                    }
                }
            }
            if (bound.Length == 0 && status.LocalAddresses.Length == 0)
                listenerError = null;
            return changed;
        }
        finally { transport.Release(); }
    }

    private async Task StopListenerAsync()
    {
        var previous = listener;
        listener = null;
        bound = [];
        if (previous is null)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await previous.StopAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        finally { await previous.DisposeAsync(); }
    }

    private async Task<(PeerSnapshot Before, PeerSnapshot After)> ChangeAsync(
        Func<PeerSnapshot, PeerDisk> change, CancellationToken cancellation)
    {
        await writer.WaitAsync(cancellation);
        try
        {
            var before = Snapshot;
            var disk = change(before);
            if (ReferenceEquals(disk, before.Disk))
                return (before, before);
            disk = disk with { LastObserved = Math.Max(disk.LastObserved, before.Latest) };
            var token = disk.Local?.ProtectedSecret is not { } encrypted ? null :
                encrypted == before.Local?.ProtectedSecret ? before.Token : WindowsSecrets.Open(encrypted, credentialContext);
            await store.UpdateAsync(_ => disk, cancellation);
            var after = new PeerSnapshot(disk, token);
            Volatile.Write(ref snapshot, after);
            AuthorityChanged(before, after);
            return (before, after);
        }
        finally { writer.Release(); }
    }

    private LocalMembership ProtectMembership(Guid node, string device, string token, Guid? network = null, string? issuer = null) =>
        new(node, device, WindowsSecrets.Protect(token, credentialContext), network, issuer);

    private Revision Next(PeerSnapshot current, Guid node) => current.Next(node, clock.GetUtcNow().ToUnixTimeMilliseconds());
    private bool DeviceChanged(PeerSnapshot current) =>
        tailnet.Device is { } device && current.Local is { } local && device != local.Device;

    private PeerNode RequireMember(PeerSnapshot current)
    {
        if (current.Self is not { } self || DeviceChanged(current))
            throw new PeerFailure(409, "Enroll this installation before changing network state.");
        return self;
    }

    private TailnetStatus RequireInterface()
    {
        var current = tailnet;
        if (current.Device is null || bound.Length == 0)
            throw new PeerFailure(409, listenerError ?? current.Message);
        return current;
    }

    private static string RequireName(string? value, string kind)
    {
        var name = value?.Trim();
        return name is { Length: > 0 and <= 120 } && !name.Any(char.IsControl) ? name :
            throw new PeerFailure(400, $"Enter a {kind} name of at most 120 characters.");
    }

    private static string DisplayName(string value)
    {
        if (Path.IsPathRooted(value))
            value = Path.GetFileName(value);
        return value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl) ? value : "Provider deployment";
    }

    private async Task PublishCatalogAsync(CancellationToken cancellation)
    {
        var view = providers.View;
        var protection = providers.Resources;
        var role = protection is { ServingAllowed: false } ? LocalRole.Leech : settings.Value.Role;
        var (before, after) = await ChangeAsync(current =>
        {
            if (current.Self is not { } self || DeviceChanged(current))
                return current.Disk;
            var observations = view.Models.OrderBy(model => model.Id, StringComparer.Ordinal)
                .Take(PeerLimits.ModelsPerNode).Select(model => new ObservedDeployment(
                    model.Id, DisplayName(model.Model.DisplayName), model.Model.Variant, model.Version,
                    model.Model.Inputs, model.Model.Outputs, model.Model.Operations, model.Context, model.ContextSource,
                    model.Online, model.Instance is not null, model.Instance?.Fingerprint, model.Instance?.Slots,
                    model.EmbeddingDimensions, model.EmbeddingSpace,
                    protection?.IsAvailable(model, view.Endpoints.First(endpoint => endpoint.Id == model.EndpointId)))).ToArray();
            var existing = current.Records.Catalogs.FirstOrDefault(catalog => catalog.Node == self.Id);
            var catalog = new NodeCatalog(self.Id, Next(current, self.Id), self.Name, role, observations,
                protection?.Status.State);
            var changed = existing is null || !JsonSerializer.SerializeToUtf8Bytes(catalog with { Revision = existing.Revision }, Json)
                .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(existing, Json));
            var existingModels = current.Records.Models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
            var created = view.Models.Where(model => !existingModels.Contains(PeerValidation.ModelId(self.Id, model.Id)))
                .Take(PeerLimits.Models - existingModels.Count).Select(model => ToShared(self.Id, model, catalog.Revision)).ToArray();
            if (!changed && created.Length == 0)
                return current.Disk;
            var records = current.Records.Merge(NetworkRecords.Empty with
            {
                Identity = current.Records.Identity,
                Catalogs = changed ? [catalog] : [],
                Models = created
            });
            return current.Disk with { Records = records };
        }, cancellation);
        await PushChangesAsync(before, after, cancellation);
    }

    public async Task PublishModelSettingsAsync(HttpRequest request, IEnumerable<string> ids, CancellationToken cancellation)
    {
        var edited = ids.ToHashSet(StringComparer.Ordinal);
        var models = providers.View.Models.Where(model => edited.Contains(model.Id)).ToArray();
        var (before, after) = await ChangeAsync(current =>
        {
            if (current.Records.Identity is null)
                return current.Disk;
            RequireAdmin(current, AdminHeader(request));
            var self = RequireMember(current);
            var revision = Next(current, self.Id);
            var delta = NetworkRecords.Empty with
            {
                Identity = current.Records.Identity,
                Models = models.Select(model => ToShared(self.Id, model, revision)).Where(model =>
                    !current.Records.Models.Any(existing => existing.Id == model.Id && !existing.Removed &&
                        existing.Alias == model.Alias && existing.Ratings.Count == model.Ratings.Count &&
                        existing.Ratings.All(rating => model.Ratings.TryGetValue(rating.Key, out var value) && value == rating.Value)))
                    .ToArray()
            };
            return current.Disk with { Records = current.Records.Merge(delta) };
        }, cancellation);
        await PushChangesAsync(before, after, cancellation);
    }

    private static SharedModel ToShared(Guid node, Deployment deployment, Revision revision) =>
        new(PeerValidation.ModelId(node, deployment.Id), node, deployment.Id, revision,
            deployment.Alias, deployment.Options.Ratings);

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        providers.Changed -= QueueCatalog;
        if (providers.Resources is { } protection)
            protection.Changed -= QueueCatalog;
        settings.Changed -= QueueCatalog;
        await StopListenerAsync();
        http.Dispose();
        stopping.Dispose();
    }

    private sealed record PeerContact(DateTimeOffset LastContact, bool Reachable, string? Digest);
    private sealed record ReconciliationStatus(DateTimeOffset? At, bool Isolated);
}
