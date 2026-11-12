using System.Security.Cryptography;
using System.Text.Json;
using Mango.Host;
using Mango.Providers;
using Mango.Resources;

namespace Mango.Peers;

internal enum KeyPriority { Low, Normal, High }
internal enum InferenceScope { Chat, Embeddings, Images, Transcription, Speech, SystemOne, ImageInput, AudioInput }
internal enum RecordKind { Authority, Node, Key, Model, Catalog, RetiredNode }

internal readonly record struct Revision(long Time, Guid Writer) : IComparable<Revision>
{
    public int CompareTo(Revision other)
    {
        var time = Time.CompareTo(other.Time);
        return time == 0 ? Writer.CompareTo(other.Writer) : time;
    }
}

internal sealed record NetworkIdentity(Guid Id, Revision Generation, int ProtocolWindow)
{
    public const int CurrentProtocolWindow = 1;
}

internal sealed record AuthorityRecord(Revision Revision, string Salt, string PasswordVerifier, string RecoveryVerifier);
internal sealed record PeerNode(Guid Id, string Device, string Name, string Address, Revision Joined,
    Revision Revision, string Verifier, bool Removed = false);
internal sealed record InferenceKey(Guid Id, string Name, InferenceScope[] Scopes, KeyPriority Priority,
    DateTimeOffset? ExpiresAt, Guid CreatedBy, Revision Revision, string Verifier, bool Revoked = false);
internal sealed record SharedModel(string Id, Guid Owner, string Deployment, Revision Revision, string Alias,
    Dictionary<string, int?> Ratings, bool Removed = false);
internal sealed record ObservedDeployment(string Id, string DisplayName, string? Variant, string? ProviderVersion,
    string[] Inputs, string[] Outputs, InferenceOperation[] Operations, int? Context, LimitSource? ContextSource,
    bool Online, bool Loaded, string? Fingerprint, int? Slots, int? EmbeddingDimensions, string EmbeddingSpace,
    bool? LocallyAvailable = null);
internal sealed record NodeCatalog(Guid Node, Revision Revision, string Name, LocalRole Role, ObservedDeployment[] Models,
    ContributionState? ResourceState = null);
internal sealed record RetiredNode(string Verifier, Guid Node, Revision Revision);
internal sealed record LocalMembership(Guid Node, string Device, string? ProtectedSecret,
    Guid? JoiningNetwork = null, string? Issuer = null);
internal sealed record PeerDisk(NetworkRecords Records, LocalMembership? Local, long LastObserved = 0)
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
    InferenceKey[] Keys, SharedModel[] Models, NodeCatalog[] Catalogs, RetiredNode[] Retired)
{
    public static readonly NetworkRecords Empty = new(null, null, [], [], [], [], []);

    public RecordVersion[] Versions() =>
        (Authority is { } authority ? new[] { new RecordVersion(RecordKind.Authority, "", authority.Revision) } : [])
        .Concat(Nodes.Select(Version)).Concat(Keys.Select(Version)).Concat(Models.Select(Version))
        .Concat(Catalogs.Select(Version)).Concat(Retired.Select(Version))
        .OrderBy(record => record.Kind).ThenBy(record => record.Id, StringComparer.Ordinal).ToArray();

    public NetworkRecords Delta(RecordVersion[] known)
    {
        var versions = known.ToDictionary(record => (record.Kind, record.Id));
        bool Newer(RecordVersion record) =>
            !versions.TryGetValue((record.Kind, record.Id), out var existing) || record.CompareTo(existing) > 0;
        return new(Identity, Authority is { } authority && Newer(new(RecordKind.Authority, "", authority.Revision))
            ? authority : null, Nodes.Where(node => Newer(Version(node))).ToArray(),
            Keys.Where(key => Newer(Version(key))).ToArray(), Models.Where(model => Newer(Version(model))).ToArray(),
            Catalogs.Where(catalog => Newer(Version(catalog))).ToArray(), Retired.Where(node => Newer(Version(node))).ToArray());
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
        return authority == Authority && nodes == Nodes && keys == Keys && models == Models && catalogs == Catalogs &&
            retired == Retired ? this : new(Identity, authority, nodes, keys, models, catalogs, retired);
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

    public bool IsEmptyDelta => Authority is null && Nodes.Length == 0 && Keys.Length == 0 && Models.Length == 0 &&
        Catalogs.Length == 0 && Retired.Length == 0;
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
    public const int Versions = Nodes * 2 + Keys + Models + Retired + 1;
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

    public static bool Valid(NetworkRecords? records)
    {
        if (records is null || records.Nodes is not { Length: <= PeerLimits.Nodes } ||
            records.Keys is not { Length: <= PeerLimits.Keys } || records.Models is not { Length: <= PeerLimits.Models } ||
            records.Catalogs is not { Length: <= PeerLimits.Nodes } || records.Retired is not { Length: <= PeerLimits.Retired })
            return false;
        if (records.Identity is null)
            return records.IsEmptyDelta;
        return Valid(records.Identity) &&
            (records.Authority is not { } authority ||
             Valid(authority.Revision) && authority.Salt is { Length: 32 } && authority.Salt.All(char.IsAsciiHexDigit) &&
             PeerCredentials.IsSecret(authority.PasswordVerifier) && PeerCredentials.IsSecret(authority.RecoveryVerifier)) &&
            records.Nodes.All(Valid) && records.Nodes.Select(node => node.Id).Distinct().Count() == records.Nodes.Length &&
            records.Keys.All(Valid) && records.Keys.Select(key => key.Id).Distinct().Count() == records.Keys.Length &&
            records.Models.All(model => model is not null && Text(model.Id, 180) && model.Owner != Guid.Empty &&
                Text(model.Deployment, 128) && model.Id == ModelId(model.Owner, model.Deployment) &&
                Text(model.Alias, 200) && Valid(model.Revision) &&
                model.Ratings is { Count: <= 128 } && model.Ratings.All(item => Text(item.Key, 100) && item.Value is null or >= 0 and <= 5)) &&
            records.Models.Select(model => model.Id).Distinct(StringComparer.Ordinal).Count() == records.Models.Length &&
            records.Catalogs.All(catalog => catalog is not null && catalog.Node != Guid.Empty && Text(catalog.Name, 120) &&
                Valid(catalog.Revision) && Enum.IsDefined(catalog.Role) &&
                (catalog.ResourceState is null || Enum.IsDefined(catalog.ResourceState.Value)) &&
                catalog.Models is { Length: <= PeerLimits.ModelsPerNode } &&
                catalog.Models.All(model => model is not null && Text(model.Id, 128) && Text(model.DisplayName, 512) &&
                    model.Inputs is { Length: <= 16 } && model.Outputs is { Length: <= 16 } &&
                    model.Operations is { Length: <= 6 } && model.Operations.All(Enum.IsDefined))) &&
            records.Catalogs.Select(catalog => catalog.Node).Distinct().Count() == records.Catalogs.Length &&
            records.Retired.All(node => node is not null && PeerCredentials.IsSecret(node.Verifier) && node.Node != Guid.Empty && Valid(node.Revision)) &&
            records.Retired.Select(node => node.Verifier).Distinct(StringComparer.OrdinalIgnoreCase).Count() == records.Retired.Length;
    }

    public static string? Disk(PeerDisk disk) => Valid(disk.Records) && disk.LastObserved is >= 0 and < 253402300799999 &&
        (disk.Local is not { } local || local.Node != Guid.Empty && Text(local.Device, 128) &&
            (local.ProtectedSecret is null || local.ProtectedSecret.Length is > 0 and <= 4096) &&
            (local.JoiningNetwork is null && local.Issuer is null ||
             local.JoiningNetwork is not null && local.JoiningNetwork != Guid.Empty && Tailnet.Address(local.Issuer) is not null))
        ? null : "The saved Mango network records are invalid.";

    public static string ModelId(Guid owner, string deployment) => $"{owner:N}/{deployment}";
}
