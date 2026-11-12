using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mango.Host;
using Mango.Logging;
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
    private readonly HttpClient http = CreatePeerClient(8);
    private readonly HttpClient inferenceHttp = CreatePeerClient(MaxIngress);
    private static readonly string? AppVersion = typeof(PeerNetwork).Assembly.GetName().Version?.ToString();

    private static HttpClient CreatePeerClient(int connections) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        MaxConnectionsPerServer = connections,
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
        var members = current.Nodes.Values.Where(node => !node.Removed).OrderBy(node => node.Joined).ThenBy(node => node.Id)
            .Select((node, index) => (Node: node, JoinOrder: index + 1)).ToArray();
        var contact = members.Where(item => item.Node.Id != current.Self?.Id).Select(item =>
        {
            var node = item.Node;
            var last = contacts.GetValueOrDefault(node.Id);
            return new
            {
                node.Id, node.Name, node.Address, node.Joined, item.JoinOrder, last?.LastContact, last?.LastChecked,
                ReachableAtLastCheck = last?.Reachable,
                Current = last is { Reachable: true } && last.Digest == current.Digest
            };
        });
        return new
        {
            Identity = current.Records.Identity,
            LocalNode = current.Self is { } self ? new { self.Id, self.Name, self.Address, self.Joined,
                members.Single(member => member.Node.Id == self.Id).JoinOrder } : null,
            Reenroll = current.Records.Identity is not null && (!IsMember || DeviceChanged(current)),
            Ready, Isolated = reconciliationStatus.Isolated, LastReconciled = reconciliationStatus.At,
            Tailnet = new { status.Message, Addresses = bound.Select(address => Tailnet.Origin(address, port)).ToArray(), Error = listenerError },
            RecoveryWarning = store.RecoveryWarning,
            Nodes = contact.ToArray(),
            Keys = current.Records.Keys.Select(KeyView).ToArray(),
            Models = current.Records.Models.Where(model => !model.Removed).ToArray(),
            ProviderIntents = current.Records.Providers ?? [],
            Catalogs = current.Records.Catalogs,
            QueueWaitSeconds,
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
        await Logs.InitializeAsync(LogConfigurationFor(Snapshot));
        recentRoutingExpiry = clock.CreateTimer(_ => ExpireRoutingMemory(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Logs.Applied += QueueCatalog;
        providers.Changed += QueueCatalog;
        providers.AdmissionChanged += ProviderSettled;
        providers.LifecycleFailed += LogLifecycleFailure;
        providers.BeginBenchmarkLog = BeginBenchmarkLog;
        serving.ActivityChanged += SignalRequests;
        if (providers.Resources is { } protection)
            protection.Changed += ResourceStatusChanged;
        settings.Changed += QueueCatalog;
    }

    public async Task RunAsync(CancellationToken hostStopping)
    {
        using var registration = hostStopping.Register(stopping.Cancel);
        var cancellation = stopping.Token;
        Task? logs = null;
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
            logs = RunLogDaysAsync(cancellation);
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
                    LogOperation("reconciliation", "Mango could not persist or reconcile the latest network change.");
                    listenerError = "Mango could not persist or reconcile the latest network change. Use Refresh to retry.";
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            NetworkChange.NetworkAddressChanged -= AddressChanged;
            if (logs is not null)
                await logs;
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

    private void ResourceStatusChanged()
    {
        ObserveResourceLog();
        QueueCatalog();
        SignalRequests();
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
                        listener = await PeerRoutes.StartAsync(this, status.LocalAddresses, port, cancellation);
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
            ConfigureLogs(before, after);
            Volatile.Write(ref snapshot, after);
            AuthorityChanged(before, after);
            if (before.Self?.Id != after.Self?.Id || before.Records.Models != after.Records.Models ||
                before.Records.Providers != after.Records.Providers)
                QueueCatalog();
            if (before.Digest != after.Digest || before.Self?.Id != after.Self?.Id)
                SignalNetwork();
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
        var (endpointReports, modelReports) = await ApplyIntentsAsync(cancellation);
        var view = providers.View;
        var protection = providers.Resources;
        var preferences = settings.Value;
        var role = protection is { ServingAllowed: false } ? LocalRole.Leech : preferences.Role;
        var (before, after) = await ChangeAsync(current =>
        {
            if (current.Self is not { } self || DeviceChanged(current))
                return current.Disk;
            var observations = view.Models.OrderBy(model => model.Id, StringComparer.Ordinal)
                .Take(PeerLimits.ModelsPerNode).Select(model =>
                {
                    var (complete, partial) = protection?.BenchmarkCounts(model) ?? (0, 0);
                    return ObserveDeployment(model, view) with
                    {
                        Endpoint = model.EndpointId, EffectiveAlias = model.Alias, EffectiveRatings = model.Options.Ratings,
                        EffectiveServing = ModelServing.Of(model.Options), Intent = modelReports.GetValueOrDefault(model.Id),
                        Busy = model.Busy || model.Instance?.Busy == true, BenchmarksComplete = complete, BenchmarksPartial = partial
                    };
                }).ToArray();
            var existing = current.Records.Catalogs.FirstOrDefault(catalog => catalog.Node == self.Id);
            var endpoints = view.Endpoints.Select(endpoint => new ObservedProvider(endpoint.Id, endpoint.Profile, endpoint.Address,
                SafeDetail(endpoint.Version, 512), endpoint.ObservedAt, endpoint.ObservedAt is null ? null : endpoint.Online,
                endpointReports.GetValueOrDefault(endpoint.Id))).Concat(endpointReports
                .Where(item => view.Endpoints.All(endpoint => endpoint.Id != item.Key))
                .Select(item => new ObservedProvider(item.Key, null, null, null, null, null, item.Value)))
                .OrderBy(endpoint => endpoint.Id).ToArray();
            var catalog = new NodeCatalog(self.Id, Next(current, self.Id), self.Name, role, observations,
                protection?.Status.State, schedulingEpoch, endpoints,
                SafeDetail(protection?.Status.Reason, 300) ?? (role != preferences.Role ? "Waiting for local resource qualification." : null),
                preferences.Role, preferences.BenchmarkPolicy, clock.GetUtcNow(), AppVersion,
                Logs.Available(LogConfigurationFor(current).Epoch) ? Logs.AppliedEpoch?.Generation : null);
            var changed = existing is null || !JsonSerializer.SerializeToUtf8Bytes(catalog with
                { Revision = existing.Revision, ObservedAt = existing.ObservedAt }, Json)
                .AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(existing, Json));
            var existingModels = current.Records.Models.Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
            var created = view.Models.Where(model => !existingModels.Contains(PeerValidation.ModelId(self.Id, model.Id)))
                .Take(PeerLimits.Models - existingModels.Count).Select(model => ToShared(self.Id, model, catalog.Revision)).ToArray();
            var providerIntents = current.Records.Providers ?? [];
            var addedEndpoints = view.Endpoints.Where(endpoint => providerIntents.All(item => item.Owner != self.Id || item.Id != endpoint.Id))
                .Take(PeerLimits.Providers - providerIntents.Length).Select(endpoint => new SharedProvider(endpoint.Id, self.Id,
                    catalog.Revision, endpoint.Profile, endpoint.Address, true)).ToArray();
            if (!changed && created.Length == 0 && addedEndpoints.Length == 0)
                return current.Disk;
            var records = current.Records.Merge(NetworkRecords.Empty with
            {
                Identity = current.Records.Identity,
                Catalogs = changed ? [catalog] : [],
                Models = created, Providers = addedEndpoints
            });
            return current.Disk with { Records = records };
        }, cancellation);
        await PushChangesAsync(before, after, cancellation);
    }

    private static string? SafeDetail(string? text, int maximum) => text is { Length: > 0 } && !text.Any(char.IsControl)
        ? text[..Math.Min(text.Length, maximum)] : null;

    private static SharedModel ToShared(Guid node, Deployment deployment, Revision revision) =>
        new(PeerValidation.ModelId(node, deployment.Id), node, deployment.Id, revision,
            deployment.Alias, deployment.Options.Ratings, Serving: ModelServing.Of(deployment.Options));

    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        Logs.Applied -= QueueCatalog;
        providers.Changed -= QueueCatalog;
        providers.AdmissionChanged -= ProviderSettled;
        providers.LifecycleFailed -= LogLifecycleFailure;
        providers.BeginBenchmarkLog = null;
        serving.ActivityChanged -= SignalRequests;
        if (providers.Resources is { } protection)
            protection.Changed -= ResourceStatusChanged;
        settings.Changed -= QueueCatalog;
        await StopListenerAsync();
        await serving.DisposeAsync();
        lock (routing)
        {
            recentRoutingExpiry?.Dispose();
            recentRoutingExpiry = null;
        }
        await StopLogCollectionAsync();
        await Logs.DisposeAsync();
        affinity.Dispose();
        inferenceHttp.Dispose();
        http.Dispose();
        stopping.Dispose();
    }

    private sealed record PeerContact(DateTimeOffset? LastContact, bool Reachable, string? Digest, DateTimeOffset? LastChecked = null);
    private sealed record ReconciliationStatus(DateTimeOffset? At, bool Isolated);
}
