using System.Security.Cryptography;
using System.Text.Json;
using Mango.Host;
using Mango.Logging;
using Mango.Providers;
using Mango.Resources;

namespace Mango.Peers;

internal enum KeyPriority { Low, Normal, High }
internal enum InferenceScope { Chat, Embeddings, Images, Transcription, Speech, SystemOne, ImageInput, AudioInput }
internal enum RecordKind { Authority, Node, Key, Model, Catalog, RetiredNode, QueuePolicy, Provider, LogPolicy, LogDeletion }
internal enum IntentState { Applied, Pending, LocalAction, Rejected }

internal readonly record struct Revision(long Time, Guid Writer) : IComparable<Revision>
{
    public int CompareTo(Revision other)
    {
        var time = Time.CompareTo(other.Time);
        return time == 0 ? Writer.CompareTo(other.Writer) : time;
    }
}

internal sealed record NetworkIdentity(Guid Id, Revision Generation, int ProtocolWindow);

internal sealed record AuthorityRecord(Revision Revision, string Salt, string PasswordVerifier, string RecoveryVerifier);
internal sealed record QueuePolicy(Revision Revision, int WaitSeconds)
{
    public const int DefaultWaitSeconds = 300;
    public const int MaxWaitSeconds = 3600;
}
internal sealed record PeerNode(Guid Id, string Device, string Name, string Address, Revision Joined,
    Revision Revision, string Verifier, bool Removed = false);
internal sealed record InferenceKey(Guid Id, string Name, InferenceScope[] Scopes, KeyPriority Priority,
    DateTimeOffset? ExpiresAt, Guid CreatedBy, Revision Revision, string Verifier, bool Revoked = false);
internal sealed record SharedModel(string Id, Guid Owner, string Deployment, Revision Revision, string Alias,
    Dictionary<string, int?> Ratings, bool Removed = false, ModelServing? Serving = null);
internal sealed record SharedProvider(Guid Id, Guid Owner, Revision Revision, ProviderProfile Profile, string Address, bool Enabled);
internal sealed record IntentReport(Revision Revision, IntentState State, string? Reason = null);
internal sealed record ObservedProvider(Guid Id, ProviderProfile? Profile, string? Address, string? Version,
    DateTimeOffset? ObservedAt, bool? Reachable, IntentReport? Intent);
internal sealed record ObservedDeployment(string Id, string DisplayName, string? Variant, string? ProviderVersion,
    string[] Inputs, string[] Outputs, InferenceOperation[] Operations, int? Context, LimitSource? ContextSource,
    bool Online, bool Loaded, string? Fingerprint, int? Slots, int? EmbeddingDimensions, string EmbeddingSpace,
    bool? LocallyAvailable = null, bool ClientTools = false, bool StructuredOutput = false,
    int? MediaTokensPerItem = null, string[]? EmbeddingEncodings = null, string? PlacementEpoch = null,
    ObservedWorkload[]? Workloads = null, Guid? Endpoint = null, string? EffectiveAlias = null,
    Dictionary<string, int?>? EffectiveRatings = null, ModelServing? EffectiveServing = null, IntentReport? Intent = null,
    bool Busy = false, int BenchmarksComplete = 0, int BenchmarksPartial = 0);
internal sealed record ObservedWorkload(Workload Limit, double? PromptMilliseconds, double? CacheSavingsMilliseconds, int Slots = 1);
internal sealed record NodeCatalog(Guid Node, Revision Revision, string Name, LocalRole Role, ObservedDeployment[] Models,
    ContributionState? ResourceState = null, string? SchedulingEpoch = null, ObservedProvider[]? Providers = null,
    string? ContributionReason = null, LocalRole? PreferredRole = null, BenchmarkPolicy? LocalBenchmarkPolicy = null,
    DateTimeOffset? ObservedAt = null, string? AppVersion = null, Revision? AppliedLogGeneration = null);
internal sealed record RetiredNode(string Verifier, Guid Node, Revision Revision);
internal sealed record LocalMembership(Guid Node, string Device, string? ProtectedSecret,
    Guid? JoiningNetwork = null, string? Issuer = null);
internal sealed record PeerDisk(NetworkRecords Records, LocalMembership? Local, long LastObserved = 0,
    LogPolicy? LocalLogPolicy = null, Revision? LocalLogDeletion = null)
{
    public static readonly PeerDisk Empty = new(NetworkRecords.Empty, null);
}

internal readonly record struct RecordVersion(RecordKind Kind, string Id, Revision Revision, bool Tombstone = false)
{
    public int CompareTo(RecordVersion other)
    {
        var tombstone = Tombstone.CompareTo(other.Tombstone);
        return tombstone == 0 ? Revision.CompareTo(other.Revision) : tombstone;
    }
}

internal sealed record NetworkRecords(NetworkIdentity? Identity, AuthorityRecord? Authority, PeerNode[] Nodes,
    InferenceKey[] Keys, SharedModel[] Models, NodeCatalog[] Catalogs, RetiredNode[] Retired, QueuePolicy? Queue = null,
    SharedProvider[]? Providers = null, LogPolicy? Logs = null, Revision? LogDeletion = null)
{
    public static readonly NetworkRecords Empty = new(null, null, [], [], [], [], []);

    public RecordVersion[] Versions() =>
        (Authority is { } authority ? new[] { new RecordVersion(RecordKind.Authority, "", authority.Revision) } : [])
        .Concat(Nodes.Select(Version)).Concat(Keys.Select(Version)).Concat(Models.Select(Version))
        .Concat(Catalogs.Select(Version)).Concat(Retired.Select(Version))
        .Concat(Queue is { } queue ? [Version(queue)] : [])
        .Concat((Providers ?? []).Select(Version))
        .Concat(Logs is { } logs ? [new(RecordKind.LogPolicy, "", logs.Revision)] : [])
        .Concat(LogDeletion is { } clear ? [new(RecordKind.LogDeletion, "", clear)] : [])
        .OrderBy(record => record.Kind).ThenBy(record => record.Id, StringComparer.Ordinal).ToArray();

    public NetworkRecords Delta(RecordVersion[] known)
    {
        var versions = known.ToDictionary(record => (record.Kind, record.Id));
        bool Newer(RecordVersion record) =>
            !versions.TryGetValue((record.Kind, record.Id), out var existing) || record.CompareTo(existing) > 0;
        return new(Identity, Authority is { } authority && Newer(new(RecordKind.Authority, "", authority.Revision))
            ? authority : null, Nodes.Where(node => Newer(Version(node))).ToArray(),
            Keys.Where(key => Newer(Version(key))).ToArray(), Models.Where(model => Newer(Version(model))).ToArray(),
            Catalogs.Where(catalog => Newer(Version(catalog))).ToArray(), Retired.Where(node => Newer(Version(node))).ToArray(),
            Queue is { } queue && Newer(Version(queue)) ? queue : null,
            Providers?.Where(provider => Newer(Version(provider))).ToArray(),
            Logs is { } logs && Newer(new(RecordKind.LogPolicy, "", logs.Revision)) ? logs : null,
            LogDeletion is { } clear && Newer(new(RecordKind.LogDeletion, "", clear)) ? clear : null);
    }

    public NetworkRecords Merge(NetworkRecords incoming)
    {
        if (Identity is null || Identity != incoming.Identity)
            return this;
        var authority = incoming.Authority is { } next &&
            (Authority is null || next.Revision.CompareTo(Authority.Revision) > 0) ? next : Authority;
        var nodes = Merge(Nodes, incoming.Nodes, Version);
        var keys = Merge(Keys, incoming.Keys, Version);
        var models = Merge(Models, incoming.Models, Version);
        var catalogs = Merge(Catalogs, incoming.Catalogs, Version);
        var retired = Merge(Retired, incoming.Retired, Version);
        var queue = incoming.Queue is { } policy &&
            (Queue is null || policy.Revision.CompareTo(Queue.Revision) > 0) ? policy : Queue;
        var providers = incoming.Providers is { Length: > 0 } desired ? Merge(Providers ?? [], desired, Version) : Providers;
        var logs = incoming.Logs is { } logPolicy &&
            (Logs is null || logPolicy.Revision.CompareTo(Logs.Revision) > 0) ? logPolicy : Logs;
        var clear = incoming.LogDeletion is { } deletion &&
            (LogDeletion is null || deletion.CompareTo(LogDeletion.Value) > 0) ? deletion : LogDeletion;
        return authority == Authority && nodes == Nodes && keys == Keys && models == Models && catalogs == Catalogs &&
            retired == Retired && queue == Queue && providers == Providers && logs == Logs && clear == LogDeletion
            ? this : new(Identity, authority, nodes, keys, models, catalogs, retired, queue, providers, logs, clear);
    }

    private static T[] Merge<T>(T[] current, T[] incoming, Func<T, RecordVersion> version)
    {
        if (incoming.Length == 0)
            return current;
        var result = current.ToDictionary(value => version(value).Id, StringComparer.Ordinal);
        var changed = false;
        foreach (var value in incoming)
        {
            var item = version(value);
            if (result.TryGetValue(item.Id, out var previous) && item.CompareTo(version(previous)) <= 0)
                continue;
            result[item.Id] = value;
            changed = true;
        }
        return changed ? result.Values.ToArray() : current;
    }

    private static RecordVersion Version(PeerNode node) => new(RecordKind.Node, node.Id.ToString("N"), node.Revision, node.Removed);
    private static RecordVersion Version(InferenceKey key) => new(RecordKind.Key, key.Id.ToString("N"), key.Revision, key.Revoked);
    private static RecordVersion Version(SharedModel model) => new(RecordKind.Model, model.Id, model.Revision);
    private static RecordVersion Version(NodeCatalog catalog) => new(RecordKind.Catalog, catalog.Node.ToString("N"), catalog.Revision);
    private static RecordVersion Version(RetiredNode node) => new(RecordKind.RetiredNode, node.Verifier, node.Revision);
    private static RecordVersion Version(QueuePolicy policy) => new(RecordKind.QueuePolicy, "", policy.Revision);
    private static RecordVersion Version(SharedProvider provider) =>
        new(RecordKind.Provider, $"{provider.Owner:N}/{provider.Id:N}", provider.Revision);

    public bool IsEmptyDelta => Authority is null && Nodes.Length == 0 && Keys.Length == 0 && Models.Length == 0 &&
        Catalogs.Length == 0 && Retired.Length == 0 && Queue is null && (Providers is null or { Length: 0 }) &&
        Logs is null && LogDeletion is null;
}

internal sealed class PeerSnapshot
{
    public PeerSnapshot(PeerDisk disk, string? token)
    {
        Disk = disk;
        Token = token;
        Versions = Records.Versions();
        Nodes = Records.Nodes.ToDictionary(node => node.Id);
        Keys = Records.Keys.ToDictionary(key => key.Id);
        Retired = Records.Retired.ToDictionary(node => node.Verifier, StringComparer.OrdinalIgnoreCase);
        Self = Local is { JoiningNetwork: null } local && Nodes.TryGetValue(local.Node, out var member) &&
            !member.Removed && member.Device == local.Device && PeerCredentials.Verify(token, member.Verifier) ? member : null;
        Latest = Math.Max(Disk.LastObserved, Math.Max(Records.Identity?.Generation.Time ?? 0,
            Versions.Select(version => version.Revision.Time).DefaultIfEmpty().Max()));
        Digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Records.Identity, Versions }, SnapshotFile<PeerDisk>.Json)));
    }

    public PeerDisk Disk { get; }
    public NetworkRecords Records => Disk.Records;
    public LocalMembership? Local => Disk.Local;
    public RecordVersion[] Versions { get; }
    public Dictionary<Guid, PeerNode> Nodes { get; }
    public Dictionary<Guid, InferenceKey> Keys { get; }
    public Dictionary<string, RetiredNode> Retired { get; }
    public PeerNode? Self { get; }
    public string? Token { get; }
    public long Latest { get; }
    public string Digest { get; }

    public Revision Next(Guid writer, long now) => new(Math.Max(now, Latest + 1), writer);
    public IEnumerable<PeerNode> Others => Nodes.Values.Where(node => !node.Removed && node.Id != Self?.Id)
        .OrderBy(node => node.Joined).ThenBy(node => node.Id);
}

internal static class PeerLimits
{
    public const int DefaultPort = 53781;
    public const int Nodes = 64;
    public const int Keys = 2048;
    public const int Models = 2048;
    public const int ModelsPerNode = 256;
    public const int Retired = 512;
    public const int Providers = 512;
    public const int Versions = Nodes * 2 + Keys + Models + Retired + Providers + 4;
    public const int ControlBytes = 8 * 1024 * 1024;
    public const int DiscoveryCandidates = 128;
    public const int UnknownPeers = 3;
    public const int UnknownConcurrent = 2;
    public const int UnknownSources = 1024;
    public const int UnknownNegative = 1024;
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ReconcileTimeout = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan KeyLookupTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan NegativeLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan UnlockLifetime = TimeSpan.FromMinutes(10);
}

internal sealed class PeerFailure(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

internal static class PeerValidation
{
    private static bool Text(string? value, int max) => value is { Length: > 0 } && value.Length <= max && !value.Any(char.IsControl);
    public static bool Valid(Revision revision) => revision.Writer != Guid.Empty && revision.Time is > 0 and < 253402300799999;
    public static bool Valid(NetworkIdentity? identity) =>
        identity is { ProtocolWindow: > 0 } && identity.Id != Guid.Empty && Valid(identity.Generation);
    public static bool ValidScopes(InferenceScope[]? scopes) => scopes is { Length: > 0 and <= 8 } &&
        scopes.All(Enum.IsDefined) && scopes.Distinct().Count() == scopes.Length &&
        scopes.Any(scope => scope is <= InferenceScope.SystemOne);
    public static bool Valid(InferenceKey? key) => key is not null && key.Id != Guid.Empty && Text(key.Name, 120) &&
        ValidScopes(key.Scopes) && Enum.IsDefined(key.Priority) && key.CreatedBy != Guid.Empty && Valid(key.Revision) &&
        PeerCredentials.IsSecret(key.Verifier);
    public static bool Valid(PeerNode? node) => node is not null && node.Id != Guid.Empty && Text(node.Device, 128) &&
        Text(node.Name, 120) && Tailnet.Address(node.Address) is not null && Valid(node.Joined) && Valid(node.Revision) &&
        PeerCredentials.IsSecret(node.Verifier);

    public static bool ValidModelSettings(string? alias, Dictionary<string, int?>? ratings, ModelServing? serving)
    {
        if (alias is null || ratings is null)
            return false;
        var options = new ModelOptions(alias, ratings, null, null, null, null, 0);
        try { ProviderCatalog.ValidateModel(serving?.ApplyTo(options) ?? options); return true; }
        catch (ProviderFailure) { return false; }
    }

    private static bool ValidProvider(SharedProvider? provider)
    {
        if (provider is null || provider.Id == Guid.Empty || provider.Owner == Guid.Empty || !Valid(provider.Revision) ||
            !Enum.IsDefined(provider.Profile) || !Text(provider.Address, 2048))
            return false;
        try { return ProviderCatalog.NormalizeAddress(provider.Address) == provider.Address; }
        catch (ProviderFailure) { return false; }
    }

    private static bool ValidIntent(IntentReport? report) => report is null || Valid(report.Revision) &&
        Enum.IsDefined(report.State) && (report.Reason is null || Text(report.Reason, 300));

    public static bool Valid(NetworkRecords? records)
    {
        if (records is null || records.Nodes is not { Length: <= PeerLimits.Nodes } ||
            records.Keys is not { Length: <= PeerLimits.Keys } || records.Models is not { Length: <= PeerLimits.Models } ||
            records.Catalogs is not { Length: <= PeerLimits.Nodes } || records.Retired is not { Length: <= PeerLimits.Retired } ||
            records.Providers is { Length: > PeerLimits.Providers })
            return false;
        if (records.Identity is null)
            return records.IsEmptyDelta;
        return Valid(records.Identity) &&
            (records.Logs is null || records.Logs.Valid && Valid(records.Logs.Revision)) &&
            (records.LogDeletion is null || Valid(records.LogDeletion.Value)) &&
            (records.Queue is not { } queue || Valid(queue.Revision) && queue.WaitSeconds is > 0 and <= QueuePolicy.MaxWaitSeconds) &&
            (records.Authority is not { } authority ||
             Valid(authority.Revision) && authority.Salt is { Length: 32 } && authority.Salt.All(char.IsAsciiHexDigit) &&
             PeerCredentials.IsSecret(authority.PasswordVerifier) && PeerCredentials.IsSecret(authority.RecoveryVerifier)) &&
            records.Nodes.All(Valid) && records.Nodes.Select(node => node.Id).Distinct().Count() == records.Nodes.Length &&
            records.Keys.All(Valid) && records.Keys.Select(key => key.Id).Distinct().Count() == records.Keys.Length &&
            records.Models.All(model => model is not null && Text(model.Id, 180) && model.Owner != Guid.Empty &&
                Text(model.Deployment, 128) && model.Id == ModelId(model.Owner, model.Deployment) &&
                Valid(model.Revision) && ValidModelSettings(model.Alias, model.Ratings, model.Serving)) &&
            records.Models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() == records.Models.Length &&
            (records.Providers is null || records.Providers.All(ValidProvider) &&
                records.Providers.DistinctBy(provider => (provider.Owner, provider.Id)).Count() == records.Providers.Length) &&
            records.Catalogs.All(catalog => catalog is not null && catalog.Node != Guid.Empty && Text(catalog.Name, 120) &&
                Valid(catalog.Revision) && Enum.IsDefined(catalog.Role) &&
                (catalog.ResourceState is null || Enum.IsDefined(catalog.ResourceState.Value)) &&
                (catalog.PreferredRole is null || Enum.IsDefined(catalog.PreferredRole.Value)) &&
                (catalog.LocalBenchmarkPolicy is null || Enum.IsDefined(catalog.LocalBenchmarkPolicy.Value)) &&
                (catalog.AppVersion is null || Text(catalog.AppVersion, 64)) &&
                (catalog.AppliedLogGeneration is null || Valid(catalog.AppliedLogGeneration.Value)) &&
                (catalog.ContributionReason is null || Text(catalog.ContributionReason, 300)) &&
                (catalog.Providers is null || catalog.Providers.Length <= PeerLimits.Providers &&
                    catalog.Providers.All(provider => provider is not null && provider.Id != Guid.Empty &&
                        (provider.Profile is null || Enum.IsDefined(provider.Profile.Value)) &&
                        (provider.Address is null || Text(provider.Address, 2048)) &&
                        (provider.Version is null || Text(provider.Version, 512)) && ValidIntent(provider.Intent)) &&
                    catalog.Providers.DistinctBy(provider => provider.Id).Count() == catalog.Providers.Length) &&
                (catalog.SchedulingEpoch is null || Guid.TryParseExact(catalog.SchedulingEpoch, "N", out _)) &&
                catalog.Models is { Length: <= PeerLimits.ModelsPerNode } &&
                catalog.Models.All(model => model is not null && Text(model.Id, 128) && Text(model.DisplayName, 512) &&
                    model.Inputs is { Length: <= 16 } && model.Outputs is { Length: <= 16 } &&
                    model.Operations is { Length: <= 6 } && model.Operations.All(Enum.IsDefined) &&
                    model.EmbeddingEncodings is null or { Length: <= 8 } &&
                    (model.EffectiveAlias is null || ValidModelSettings(model.EffectiveAlias, model.EffectiveRatings, model.EffectiveServing)) &&
                    ValidIntent(model.Intent) && model.BenchmarksComplete is >= 0 and <= 6 && model.BenchmarksPartial is >= 0 and <= 6 &&
                    (model.PlacementEpoch is null || Text(model.PlacementEpoch, 128)) &&
                    (model.Workloads is null || model.Workloads.Length <= 6 && model.Workloads.All(work => work is not null &&
                        model.Operations.Contains(work.Limit.Operation) && work.Limit.Units > 0 && work.Limit.Items > 0 &&
                        work.Limit.MediaItems >= 0 && work.Slots is >= 1 and <= 128 && work.PromptMilliseconds is null or >= 0 &&
                        work.CacheSavingsMilliseconds is null or >= 0)))) &&
            records.Catalogs.Select(catalog => catalog.Node).Distinct().Count() == records.Catalogs.Length &&
            records.Retired.All(node => node is not null && PeerCredentials.IsSecret(node.Verifier) && node.Node != Guid.Empty && Valid(node.Revision)) &&
            records.Retired.Select(node => node.Verifier).Distinct(StringComparer.OrdinalIgnoreCase).Count() == records.Retired.Length;
    }

    public static string? Disk(PeerDisk disk) => Valid(disk.Records) && disk.LastObserved is >= 0 and < 253402300799999 &&
        (disk.LocalLogPolicy is null || disk.LocalLogPolicy.Valid) &&
        (disk.LocalLogDeletion is null || Valid(disk.LocalLogDeletion.Value)) &&
        (disk.Local is not { } local || local.Node != Guid.Empty && Text(local.Device, 128) &&
            (local.ProtectedSecret is null || local.ProtectedSecret.Length is > 0 and <= 4096) &&
            (local.JoiningNetwork is null && local.Issuer is null ||
             local.JoiningNetwork is not null && local.JoiningNetwork != Guid.Empty && Tailnet.Address(local.Issuer) is not null))
        ? null : "The saved Mango network records are invalid.";

    public static string ModelId(Guid owner, string deployment) => $"{owner:N}/{deployment}";
}
