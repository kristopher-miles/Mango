using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Mango.Providers;

internal enum ProviderProfile { LmStudio, UnslothStudio, UnslothRun, LlamaSingle, LlamaRouter, LlamaEmbedding, Generic, Strata }
internal enum ProviderMode { Attached, Managed }
internal enum InferenceOperation { Chat, Embeddings, Images, Transcription, Speech, SystemOne }
internal enum LimitSource { Reported, Configured }
internal enum ModelOrigin { Local, LmLink }

internal sealed record ProviderEndpoint(Guid Id, ProviderProfile Profile, ProviderMode Mode, string Address,
    string? ProtectedToken, string? Executable, string? ModelFile, string? StrataControl = null, string? StrataModels = null);

internal sealed record EndpointUpdate(Guid? Id, [property: JsonRequired] ProviderProfile Profile,
    [property: JsonRequired] ProviderMode Mode, string? Address,
    string? Token, bool ClearToken, string? Executable, string? ModelFile, string? ExpectedConfiguration = null,
    string? StrataControl = null, string? StrataModels = null);

internal sealed record ModelOptions(string? Alias, Dictionary<string, int?> Ratings, int? ServedContext,
    int? EmbeddingDimensions, string? EmbeddingFamily, int? MediaTokensPerItem, long Revision, bool ServingDisabled = false,
    bool? ServingOverride = null, bool? AliasExplicit = null, bool Yolo = false);

internal sealed record ModelYoloUpdate([property: JsonRequired] bool Enabled, [property: JsonRequired] long Revision);

internal sealed record ModelServingUpdate([property: JsonRequired] bool Enabled);

internal sealed record ModelUpdate(string Id, string? Alias, Dictionary<string, int?>? Ratings, int? ServedContext,
    int? EmbeddingDimensions, string? EmbeddingFamily, int? MediaTokensPerItem, long Revision);

internal sealed record ModelServing(int? ServedContext, int? EmbeddingDimensions, string? EmbeddingFamily, int? MediaTokensPerItem)
{
    public static ModelServing Of(ModelOptions options) =>
        new(options.ServedContext, options.EmbeddingDimensions, options.EmbeddingFamily, options.MediaTokensPerItem);

    public ModelOptions ApplyTo(ModelOptions options) => options with
    {
        ServedContext = ServedContext, EmbeddingDimensions = EmbeddingDimensions,
        EmbeddingFamily = EmbeddingFamily, MediaTokensPerItem = MediaTokensPerItem
    };
}

internal sealed record ProviderSettings(ProviderEndpoint[] Endpoints, Dictionary<string, ModelOptions> Models, string? Selected)
{
    public static readonly ProviderSettings Empty = new([], [], null);
}

internal sealed record ProviderInstance(string Id, int? Context, int? Slots, string Fingerprint, bool Busy = false,
    string? NativeGeneration = null, string? ConfigurationFingerprint = null)
{
    public string ResourceFingerprint => ConfigurationFingerprint ?? Fingerprint;
}

internal sealed record ProviderModel(string Id, string NativeId, string ManagementId, string DisplayName,
    string? Variant, string[] Inputs, string[] Outputs, InferenceOperation[] Operations, ProviderInstance[] Instances,
    int? TheoreticalContext, bool ClientTools, bool StructuredOutput, int? EmbeddingDimensions,
    string[] EmbeddingEncodings, long? ArtifactBytes = null, bool Installed = true,
    bool SupportsLoad = true, bool SupportsUnload = true, string? Format = null,
    ModelOrigin? Origin = null, string? SourceDevice = null, bool AmbiguousInstance = false,
    int? ReportedMediaTokensPerItem = null, int? PreparedContext = null, string? PreparedConfiguration = null);

internal sealed record ProviderInventory(string? Version, ProviderModel[] Models, bool CanLoad, bool CanUnload,
    bool CanStop, int? OwnedProcessId = null, int? MaxLoadedModels = null, bool LoadsSettled = false);

internal sealed record Deployment(Guid EndpointId, ProviderModel Model, ModelOptions Options, bool Online, bool Busy,
    string? Version, string? Configuration = null, string? PlacementEpoch = null)
{
    public string Id => Model.Id;
    // Local operator policy, bound to this host's stable deployment identity, never an alias.
    public bool Yolo => Options.Yolo;
    public string? Alias => Options.Alias;
    public ProviderInstance? Instance => Model.Instances.FirstOrDefault(instance => !instance.Busy)
        ?? Model.Instances.FirstOrDefault();
    public string[] ProviderIdentifiers => new[] { Model.NativeId, Instance?.Id }
        .OfType<string>().Where(ProviderIdentity.ValidIdentifier).Distinct(StringComparer.Ordinal).ToArray();
    public bool AcceptsIdentifier(string identifier) => ProviderIdentifiers.Contains(identifier, StringComparer.Ordinal) ||
        string.Equals(Alias, identifier, StringComparison.OrdinalIgnoreCase);
    public int? Context => Instance?.Context ?? (Model.PreparedContext is { } prepared && Options.ServedContext is { } configured
        ? Math.Min(prepared, configured) : Model.PreparedContext ?? Options.ServedContext);
    public LimitSource? ContextSource => Instance?.Context is not null || Model.PreparedContext is not null && Options.ServedContext is null ? LimitSource.Reported
        : Options.ServedContext is not null ? LimitSource.Configured : null;
    public int? EmbeddingDimensions => Model.EmbeddingDimensions ?? Options.EmbeddingDimensions;
    public int? MediaTokensPerItem => Model.ReportedMediaTokensPerItem is { } reported
        ? Math.Max(reported, Options.MediaTokensPerItem ?? 0) : Options.MediaTokensPerItem;
    public string EmbeddingSpace => Options.EmbeddingFamily is { } family ? $"family:{family}" : Id;
    public bool ServingDisabled => Options.ServingOverride is { } enabled ? !enabled
        : Options.ServingDisabled || Model.Origin == ModelOrigin.LmLink;
    public bool Eligible => !ServingDisabled && Online && Model.Installed && Instance is not null && Model.Operations.Any(operation => operation switch
    {
        InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne => Context is not null,
        _ => true
    });
    public bool Available => Eligible && !Busy && Instance is { Busy: false };
}

internal static class ProviderLimits
{
    public const int MaxEndpoints = 32;
    public const int MaxModels = 256;
    public const int MaxTokenLength = 4096;
    public const int MaxContext = 4 * 1024 * 1024;
    public const int MaxEmbeddingDimensions = 65536;
    public const int MaxRequestBytes = 64 * 1024 * 1024;
    public const int MaxAudioBytes = 48 * 1024 * 1024;
    public const int MaxControlBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LifecycleTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan InventoryInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshCoalesceInterval = TimeSpan.FromSeconds(2);
}

internal sealed class ProviderFailure(int status, string message, bool allocationFailure = false, bool capacityWait = false,
    bool memoryWait = false, string? code = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public bool AllocationFailure { get; } = allocationFailure;
    public bool CapacityWait { get; } = capacityWait;
    public bool MemoryWait { get; } = memoryWait;
}

internal static class ProviderIdentity
{
    public static bool ValidIdentifier(string? value) =>
        value is { Length: > 0 and <= 512 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    public static string Hash(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))));

    public static string Model(Guid endpoint, string nativeId) => Hash(endpoint.ToString("N"), nativeId);
}
