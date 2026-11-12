using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Mango.Host;

namespace Mango.Peers;

internal sealed record CreateNetworkRequest(string? Name, string? Password, BenchmarkPolicy? BenchmarkPolicy = null);
internal sealed record JoinNetworkRequest(string? Name, string? Address, Guid Network, string? Password, BenchmarkPolicy? BenchmarkPolicy = null);
internal sealed record EnrollmentRequest(Guid Network, Guid Node, string Device, string Name, int Port, string Verifier, string? Password);
internal sealed record NetworkSetup(NetworkIdentity Identity, Guid Node, string? RecoveryRecord);
internal sealed record UnlockRequest(string? Password);
internal sealed record RecoverRequest(string? RecoveryRecord, string? NewPassword);
internal sealed record ResetRequest(bool Confirm, string? NewPassword);
internal sealed record KeyIssueRequest(string? Name, InferenceScope[]? Scopes, KeyPriority? Priority, bool? NeverExpires,
    DateTimeOffset? ExpiresAt);
internal sealed record KeyDisplay(Guid Id, string Name, InferenceScope[] Scopes, KeyPriority Priority, DateTimeOffset? ExpiresAt,
    Guid CreatedBy, bool Revoked);

internal sealed partial class PeerNetwork
{
    internal const string AdminSessionHeader = "Mango-Admin";
    private readonly object authorization = new();
    private readonly Dictionary<string, AdminSession> unlocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim passwordChecks = new(2);
    private readonly AttemptBudget passwordBudget = new(60, 12, 64);
    private readonly SemaphoreSlim setup = new(1);
    private sealed record AdminSession(Revision Generation, Revision Authority, DateTimeOffset Expires);

    public async Task<NetworkSetup> CreateAsync(CreateNetworkRequest input, CancellationToken cancellation)
    {
        if (!await setup.WaitAsync(0, cancellation))
            throw new PeerFailure(409, "Another enrollment is in progress.");
        try { return await CreateNetworkAsync(input, cancellation); }
        finally { setup.Release(); }
    }

    private async Task<NetworkSetup> CreateNetworkAsync(CreateNetworkRequest input, CancellationToken cancellation)
    {
        var name = RequireName(input.Name, "node");
        var status = RequireInterface();
        var recovery = PeerCredentials.Secret();
        var secret = PeerCredentials.Secret();
        var id = Guid.NewGuid();
        var network = Guid.NewGuid();
        if (Snapshot.Records.Identity is not null || Snapshot.Self is not null)
            throw new PeerFailure(409, "This data directory already contains enrollment state.");
        await SaveBenchmarkChoiceAsync(input.BenchmarkPolicy, cancellation);
        var (_, after) = await ChangeAsync(current =>
        {
            if (current.Records.Identity is not null || current.Self is not null)
                throw new PeerFailure(409, "This data directory already contains enrollment state.");
            var revision = Next(current, id);
            var identity = new NetworkIdentity(network, revision, NetworkIdentity.CurrentProtocolWindow);
            var authority = PeerCredentials.Password(input.Password, PeerCredentials.Digest(recovery), revision);
            var node = new PeerNode(id, status.Device!, name, Tailnet.Origin(bound[0], port), revision, revision,
                PeerCredentials.Digest(secret));
            return new(NetworkRecords.Empty with { Identity = identity, Authority = authority, Nodes = [node] },
                ProtectMembership(id, status.Device!, secret));
        }, cancellation);
        QueueCatalog();
        return new(after.Records.Identity!, id, PeerCredentials.Recovery(network, recovery));
    }

    public async Task<NetworkSetup> JoinAsync(JoinNetworkRequest input, CancellationToken cancellation)
    {
        if (!await setup.WaitAsync(0, cancellation))
            throw new PeerFailure(409, "Another enrollment is in progress.");
        try
        {
            var name = RequireName(input.Name, "node");
            var status = RequireInterface();
            var target = Tailnet.Address(input.Address, port) ?? throw new PeerFailure(400, "Enter the selected peer's Tailscale address.");
            if (input.Network == Guid.Empty || input.Password is not { Length: > 0 and <= 256 })
                throw new PeerFailure(400, "Enter the selected network ID and master password.");
            if (bound.Any(ip => target == new Uri(Tailnet.Origin(ip, port))))
                throw new PeerFailure(400, "Select a different Mango peer.");
            var intro = await GetIntroAsync(target, cancellation);
            if (intro is null || intro.Identity.Id != input.Network ||
                intro.Identity.ProtocolWindow != NetworkIdentity.CurrentProtocolWindow)
                throw new PeerFailure(409, "The selected address did not identify the expected Mango network.");

            var (_, pending) = await ChangeAsync(current =>
            {
                if (current.Self is not null && !DeviceChanged(current))
                    throw new PeerFailure(409, "This installation is already enrolled.");
                if (current.Records.Identity is { } identity &&
                    (identity.Id != input.Network || intro.Identity.Generation.CompareTo(identity.Generation) < 0))
                    throw new PeerFailure(409, "This installation expects a different network or a newer reset generation.");
                var local = current.Local;
                var removed = local is not null && current.Nodes.TryGetValue(local.Node, out var member) && member.Removed;
                var keepId = local?.Device == status.Device && !removed;
                var id = keepId ? local!.Node : Guid.NewGuid();
                var token = keepId && local!.JoiningNetwork == input.Network && current.Token is { } saved
                    ? saved : PeerCredentials.Secret();
                return current.Disk with
                {
                    Local = ProtectMembership(id, status.Device!, token, input.Network, target.GetLeftPart(UriPartial.Authority))
                };
            }, cancellation);

            NetworkRecords? enrolled;
            var enrollment = new EnrollmentRequest(input.Network, pending.Local!.Node, status.Device!, name, port,
                PeerCredentials.Digest(pending.Token!), input.Password);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token);
                timeout.CancelAfter(PeerLimits.ReconcileTimeout);
                using var response = await http.PostAsJsonAsync(new Uri(target, "/peer/enroll"), enrollment, Json, timeout.Token);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new PeerFailure(401, "The selected peer rejected the master password.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new PeerFailure(429, "The selected peer is limiting enrollment attempts. Try again shortly.");
                if (response.StatusCode == HttpStatusCode.Conflict)
                    throw new PeerFailure(409, "The selected peer cannot enroll this installation right now. Refresh discovery and try again.");
                if (!response.IsSuccessStatusCode)
                    throw new PeerFailure(502, "The selected peer could not enroll this installation.");
                enrolled = await response.Content.ReadFromJsonAsync<NetworkRecords>(Json, timeout.Token);
            }
            catch (Exception error) when (Unavailable(error, cancellation))
            {
                throw new PeerFailure(502, "The selected peer became unreachable during enrollment. Retry the same enrollment.");
            }
            if (!PeerValidation.Valid(enrolled) || enrolled!.Identity != intro.Identity || enrolled.Authority is null ||
                !enrolled.Nodes.Any(node => node.Id == intro.Node && node.Device == intro.Device && !node.Removed) ||
                !enrolled.Nodes.Any(node => node.Id == pending.Local.Node && node.Device == status.Device && !node.Removed &&
                    node.Verifier == enrollment.Verifier))
                throw new PeerFailure(409, "The peer did not return a valid enrollment for this installation.");

            await SaveBenchmarkChoiceAsync(input.BenchmarkPolicy, cancellation);
            ready = false;
            await ChangeAsync(current =>
            {
                if (current.Local != pending.Local)
                    throw new PeerFailure(409, "Enrollment changed while this peer was contacted.");
                return new(enrolled, pending.Local with { JoiningNetwork = null, Issuer = null });
            }, cancellation);
            needsPeerConfirmation = false;
            try { await ReconcileAsync(stopping.Token, gate: true); }
            finally { QueueCatalog(); }
            return new(enrolled.Identity!, pending.Local.Node, null);
        }
        finally { setup.Release(); }
    }

    private async Task SaveBenchmarkChoiceAsync(BenchmarkPolicy? choice, CancellationToken cancellation)
    {
        if (choice is not { } policy)
            return;
        if (!Enum.IsDefined(policy))
            throw new PeerFailure(400, "Choose a local benchmark policy.");
        if (settings.Value.BenchmarkPolicy != policy)
            await settings.UpdateAsync(current => current with { BenchmarkPolicy = policy }, cancellation);
    }

    public async Task<NetworkRecords> AcceptEnrollmentAsync(EnrollmentRequest input, IPAddress address, CancellationToken cancellation)
    {
        if (!Ready)
            throw new PeerFailure(409, "This peer is still reconciling network authority.");
        if (input.Node == Guid.Empty || input.Device is not { Length: > 0 and <= 128 } || input.Device.Any(char.IsControl) ||
            input.Port is < 1 or > 65535 || !PeerCredentials.IsSecret(input.Verifier) || !Tailnet.Contains(address))
            throw new PeerFailure(400, "Invalid Mango enrollment.");
        var name = RequireName(input.Name, "node");
        var origin = Tailnet.Origin(address, input.Port);
        if (!MatchesDevice(new Uri(origin), input.Device))
            throw new PeerFailure(409, "The joining address belongs to a different Tailscale device.");
        var snapshot = Snapshot;
        var self = RequireMember(snapshot);
        if (snapshot.Records.Identity?.Id != input.Network || snapshot.Records.Authority is not { } authority)
            throw new PeerFailure(409, "This peer belongs to a different Mango network.");
        await CheckPasswordAsync(input.Password, authority, address, cancellation);
        var (before, after) = await ChangeAsync(current =>
        {
            RequireMember(current);
            if (current.Records.Identity != snapshot.Records.Identity || current.Records.Authority != authority)
                throw new PeerFailure(409, "Network authority changed. Retry enrollment.");
            if (current.Nodes.TryGetValue(input.Node, out var existing))
            {
                if (existing.Removed || existing.Device != input.Device ||
                    !string.Equals(existing.Verifier, input.Verifier, StringComparison.OrdinalIgnoreCase))
                    throw new PeerFailure(409, "That node identity is already enrolled or retired.");
                return current.Disk;
            }
            if (current.Nodes.Count >= PeerLimits.Nodes || current.Nodes.Values.Any(node => !node.Removed && node.Address == origin))
                throw new PeerFailure(409, "The network is full or that service address is already enrolled.");
            var revision = Next(current, self.Id);
            var member = new PeerNode(input.Node, input.Device, name, origin, revision, revision, input.Verifier.ToUpperInvariant());
            return current.Disk with
            {
                Records = current.Records.Merge(NetworkRecords.Empty with { Identity = current.Records.Identity, Nodes = [member] })
            };
        }, cancellation);
        await PushChangesAsync(before, after, stopping.Token, exclude: input.Node);
        return after.Records;
    }

    public async Task<object> UnlockAsync(UnlockRequest input, IPAddress source, CancellationToken cancellation)
    {
        var current = Snapshot;
        RequireMember(current);
        var authority = current.Records.Authority ?? throw new PeerFailure(409, "No local network authority is available.");
        await CheckPasswordAsync(input.Password, authority, source, cancellation);
        lock (authorization)
        {
            var now = Snapshot;
            if (now.Records.Identity != current.Records.Identity || now.Records.Authority != authority || !IsMember)
                throw new PeerFailure(409, "Network authority changed. Unlock again.");
            var token = PeerCredentials.Secret();
            var expires = clock.GetUtcNow() + PeerLimits.UnlockLifetime;
            if (unlocks.Count >= 16)
                unlocks.Remove(unlocks.MinBy(entry => entry.Value.Expires).Key);
            unlocks.Add(PeerCredentials.Digest(token), new(now.Records.Identity!.Generation, authority.Revision, expires));
            return new { Token = token, ExpiresAt = expires };
        }
    }

    private async Task CheckPasswordAsync(string? password, AuthorityRecord authority, IPAddress source, CancellationToken cancellation)
    {
        if (password is not { Length: > 0 and <= 256 })
            throw new PeerFailure(401, "The master password was rejected.");
        if (!passwordBudget.Take(source, clock.GetUtcNow()) || !await passwordChecks.WaitAsync(0, cancellation))
            throw new PeerFailure(429, "Too many password attempts. Try again shortly.");
        try
        {
            if (!PeerCredentials.VerifyPassword(password, authority))
                throw new PeerFailure(401, "The master password was rejected.");
        }
        finally { passwordChecks.Release(); }
    }

    public void RequireModelAdmin(HttpRequest request)
    {
        var current = Snapshot;
        if (current.Records.Identity is not null)
            RequireAdmin(current, AdminHeader(request));
    }

    private static string? AdminHeader(HttpRequest request) =>
        request.Headers[AdminSessionHeader].Count == 1 ? request.Headers[AdminSessionHeader][0] : null;

    private bool HasAdmin(PeerSnapshot current, string? token)
    {
        if (current.Self is null || DeviceChanged(current) || !PeerCredentials.IsSecret(token))
            return false;
        lock (authorization)
            return unlocks.TryGetValue(PeerCredentials.Digest(token!), out var session) &&
                session.Expires > clock.GetUtcNow() && session.Authority == current.Records.Authority?.Revision &&
                session.Generation == current.Records.Identity?.Generation;
    }

    private void RequireAdmin(PeerSnapshot current, string? token)
    {
        if (!HasAdmin(current, token))
            throw new PeerFailure(403, "Unlock network administration with the master password.");
    }

    public async Task<PushResult> RecoverAsync(RecoverRequest input, CancellationToken cancellation)
    {
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireMember(current);
            if (current.Records.Authority is not { } authority || current.Records.Identity is not { } identity ||
                !PeerCredentials.VerifyRecovery(input.RecoveryRecord, identity.Id, authority))
                throw new PeerFailure(401, "The recovery record was rejected.");
            var replacement = PeerCredentials.Password(input.NewPassword, authority.RecoveryVerifier, Next(current, self.Id));
            return current.Disk with { Records = current.Records with { Authority = replacement } };
        }, cancellation);
        return await PushChangesAsync(before, after, stopping.Token);
    }

    public async Task<object> ResetAsync(HttpRequest request, ResetRequest input, CancellationToken cancellation)
    {
        if (!input.Confirm)
            throw new PeerFailure(400, "Confirm the separate full network reset.");
        var recovery = PeerCredentials.Secret();
        var token = PeerCredentials.Secret();
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireMember(current);
            RequireAdmin(current, AdminHeader(request));
            var revision = Next(current, self.Id);
            var identity = current.Records.Identity! with { Generation = revision };
            var authority = PeerCredentials.Password(input.NewPassword, PeerCredentials.Digest(recovery), revision);
            var retired = current.Records.Retired.Concat(current.Nodes.Values
                .Select(node => new RetiredNode(node.Verifier, node.Id, revision)))
                .DistinctBy(node => node.Verifier, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(node => node.Revision).Take(PeerLimits.Retired).ToArray();
            return new(current.Records with
            {
                Identity = identity, Authority = authority, Keys = [],
                Nodes = [self with { Verifier = PeerCredentials.Digest(token), Revision = revision }],
                Retired = retired,
                Catalogs = current.Records.Catalogs.Select(catalog => catalog with
                {
                    Revision = revision,
                    Models = catalog.Models.Select(model => model with { Online = false, Loaded = false }).ToArray()
                }).ToArray()
            }, ProtectMembership(self.Id, self.Device, token));
        }, cancellation);
        var identity = after.Records.Identity!;
        needsPeerConfirmation = false;
        ready = true;
        var delivery = await PushAsync(before, before.Others, "notice", new PeerNotice(identity.Id, identity.Generation), stopping.Token);
        QueueCatalog();
        return new { Identity = identity, RecoveryRecord = PeerCredentials.Recovery(identity.Id, recovery), Delivery = delivery };
    }

    public async Task ApplyNoticeAsync(PeerNotice notice, CancellationToken cancellation)
    {
        await ChangeAsync(current =>
        {
            if (current.Self is not { } self || current.Records.Identity is not { } identity ||
                notice.Network != identity.Id || !PeerValidation.Valid(notice.Generation))
                return current.Disk;
            var comparison = notice.Generation.CompareTo(identity.Generation);
            if (comparison < 0 || comparison == 0 && notice.Removed != self.Id)
                return current.Disk;
            var records = comparison > 0 ? current.Records with
            {
                Identity = identity with { Generation = notice.Generation },
                Authority = null,
                Keys = [],
                Nodes = [],
                Retired = [],
                Catalogs = current.Records.Catalogs.Select(catalog => catalog with
                {
                    Models = catalog.Models.Select(model => model with { Online = false, Loaded = false }).ToArray()
                }).ToArray()
            } : current.Records with { Nodes = current.Records.Nodes.Select(node => node.Id == self.Id ? node with { Removed = true } : node).ToArray() };
            return new(records, current.Local! with { ProtectedSecret = null });
        }, cancellation);
    }

    public async Task<PushResult> RemoveAsync(HttpRequest request, Guid id, CancellationToken cancellation)
    {
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireMember(current);
            RequireAdmin(current, AdminHeader(request));
            if (id == self.Id || !current.Nodes.TryGetValue(id, out var node) || node.Removed)
                throw new PeerFailure(404, "Select an enrolled peer other than this node.");
            return current.Disk with
            {
                Records = current.Records.Merge(NetworkRecords.Empty with
                {
                    Identity = current.Records.Identity,
                    Nodes = [node with { Removed = true, Revision = Next(current, self.Id) }]
                })
            };
        }, cancellation);
        var identity = before.Records.Identity!;
        await SendAsync<object>(before, before.Nodes[id], "notice", new PeerNotice(identity.Id, identity.Generation, id), stopping.Token);
        return await PushChangesAsync(before, after, stopping.Token);
    }

    public async Task<object> IssueAsync(HttpRequest request, KeyIssueRequest input, CancellationToken cancellation)
    {
        var name = RequireName(input.Name, "key");
        if (!PeerValidation.ValidScopes(input.Scopes) || input.Priority is not { } priority || !Enum.IsDefined(priority) ||
            input.NeverExpires is not { } never || never == input.ExpiresAt.HasValue ||
            input.ExpiresAt is { } expiry && expiry <= clock.GetUtcNow())
            throw new PeerFailure(400, "Choose scopes, a priority, and either a future expiry or never expire.");
        var permission = await AllowKeyMutationAsync(request, cancellation);
        var id = Guid.NewGuid();
        var secret = PeerCredentials.Secret();
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireKeyMutation(current, request, permission);
            if (current.Keys.Count >= PeerLimits.Keys)
                throw new PeerFailure(409, "The network key record limit was reached.");
            if (current.Keys.Values.Any(key => !key.Revoked && string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new PeerFailure(409, "A key already uses that display name.");
            var record = new InferenceKey(id, name, input.Scopes!, priority, input.ExpiresAt, self.Id, Next(current, self.Id),
                PeerCredentials.Digest(secret));
            return current.Disk with { Records = current.Records with { Keys = [.. current.Records.Keys, record] } };
        }, cancellation);
        var delivery = await PushChangesAsync(before, after, stopping.Token);
        return new { Key = KeyView(after.Keys[id]), Secret = PeerCredentials.Key(id, secret), Delivery = delivery };
    }

    public async Task<PushResult> RevokeAsync(HttpRequest request, Guid id, CancellationToken cancellation)
    {
        var permission = await AllowKeyMutationAsync(request, cancellation);
        var (before, after) = await ChangeAsync(current =>
        {
            var self = RequireKeyMutation(current, request, permission);
            if (!current.Keys.TryGetValue(id, out var key))
                throw new PeerFailure(404, "That inference key was not found.");
            if (key.Revoked)
                return current.Disk;
            return current.Disk with
            {
                Records = current.Records.Merge(NetworkRecords.Empty with
                {
                    Identity = current.Records.Identity,
                    Keys = [key with { Revoked = true, Revision = Next(current, self.Id) }]
                })
            };
        }, cancellation);
        return await PushChangesAsync(before, after, stopping.Token);
    }

    private async Task<(NetworkIdentity Identity, Guid? Reachable)> AllowKeyMutationAsync(HttpRequest request, CancellationToken cancellation)
    {
        var current = Snapshot;
        RequireAdmin(current, AdminHeader(request));
        RequireMember(current);
        var others = current.Others.ToArray();
        if (others.Length == 0)
            return (current.Records.Identity!, null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, stopping.Token);
        deadline.CancelAfter(PeerLimits.ReconcileTimeout);
        Guid? reachable = null;
        try
        {
            await Parallel.ForEachAsync(others, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = deadline.Token },
                async (peer, token) =>
                {
                    if (await SendAsync<object>(current, peer, "ping", null, token, refresh: true) is not { Authenticated: true })
                        return;
                    lock (authorization) reachable ??= peer.Id;
                    await deadline.CancelAsync();
                });
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { }
        return reachable.HasValue ? (current.Records.Identity!, reachable) :
            throw new PeerFailure(409, "At least one other enrolled peer must be reachable to change keys.");
    }

    private PeerNode RequireKeyMutation(PeerSnapshot current, HttpRequest request, (NetworkIdentity Identity, Guid? Reachable) permission)
    {
        RequireAdmin(current, AdminHeader(request));
        var self = RequireMember(current);
        if (current.Records.Identity != permission.Identity || current.Others.Any() &&
            (!permission.Reachable.HasValue || !current.Nodes.TryGetValue(permission.Reachable.Value, out var peer) || peer.Removed))
            throw new PeerFailure(409, "Reachable membership changed. Retry the key change.");
        return self;
    }

    private static KeyDisplay KeyView(InferenceKey key) =>
        new(key.Id, key.Name, key.Scopes, key.Priority, key.ExpiresAt, key.CreatedBy, key.Revoked);
}

internal sealed class AttemptBudget(int totalLimit, int sourceLimit, int sourceCapacity)
{
    private readonly Dictionary<IPAddress, int> sources = [];
    private DateTimeOffset window;
    private int total;

    public bool Take(IPAddress source, DateTimeOffset now)
    {
        lock (sources)
        {
            if (now < window || now - window >= TimeSpan.FromMinutes(1))
            {
                window = now;
                total = 0;
                sources.Clear();
            }
            if (source.IsIPv4MappedToIPv6)
                source = source.MapToIPv4();
            var count = sources.GetValueOrDefault(source);
            if (total >= totalLimit || count >= sourceLimit || count == 0 && sources.Count >= sourceCapacity)
                return false;
            sources[source] = count + 1;
            total++;
            return true;
        }
    }
}
