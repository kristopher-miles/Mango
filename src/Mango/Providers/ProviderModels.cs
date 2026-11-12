using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Mango.Providers;

internal enum ProviderProfile { LmStudio, UnslothStudio, UnslothRun, LlamaSingle, LlamaRouter, LlamaEmbedding }
internal enum ProviderMode { Attached, Managed }
internal enum InferenceOperation { Chat, Embeddings, Images, Transcription, Speech, SystemOne }
internal enum LimitSource { Reported, Configured }

internal sealed record ProviderEndpoint(Guid Id, ProviderProfile Profile, ProviderMode Mode, string Address,
    string? ProtectedToken, string? Executable, string? ModelFile);

internal sealed record EndpointUpdate(Guid? Id, [property: JsonRequired] ProviderProfile Profile,
    [property: JsonRequired] ProviderMode Mode, string? Address,
    string? Token, bool ClearToken, string? Executable, string? ModelFile);

internal sealed record ModelOptions(string Alias, Dictionary<string, int?> Ratings, int? ServedContext,
    int? EmbeddingDimensions, string? EmbeddingFamily, int? MediaTokensPerItem, long Revision);

internal sealed record ModelUpdate(string Id, string? Alias, Dictionary<string, int?>? Ratings, int? ServedContext,
    int? EmbeddingDimensions, string? EmbeddingFamily, int? MediaTokensPerItem, long Revision);

internal sealed record ProviderSettings(ProviderEndpoint[] Endpoints, Dictionary<string, ModelOptions> Models, string? Selected)
{
    public static readonly ProviderSettings Empty = new([], [], null);
}

internal sealed record ProviderInstance(string Id, int? Context, int? Slots, string Fingerprint, bool Busy = false,
    string? NativeGeneration = null);

internal sealed record ProviderModel(string Id, string NativeId, string ManagementId, string DisplayName,
    string? Variant, string[] Inputs, string[] Outputs, InferenceOperation[] Operations, ProviderInstance[] Instances,
    int? TheoreticalContext, bool ClientTools, bool StructuredOutput, int? EmbeddingDimensions,
    string[] EmbeddingEncodings, long? ArtifactBytes = null, bool Installed = true,
    bool SupportsLoad = true, bool SupportsUnload = true, string? Format = null);

internal sealed record ProviderInventory(string? Version, ProviderModel[] Models, bool CanLoad, bool CanUnload,
    bool CanStop, int? OwnedProcessId = null);

internal sealed record Deployment(Guid EndpointId, ProviderModel Model, ModelOptions Options, bool Online, bool Busy,
    string? Version)
{
    public string Id => Model.Id;
    public string Alias => Options.Alias;
    public ProviderInstance? Instance => Model.Instances.FirstOrDefault(instance => !instance.Busy)
        ?? Model.Instances.FirstOrDefault();
    public int? Context => Instance?.Context ?? Options.ServedContext;
    public LimitSource? ContextSource => Instance?.Context is not null ? LimitSource.Reported
        : Options.ServedContext is not null ? LimitSource.Configured : null;
    public int? EmbeddingDimensions => Model.EmbeddingDimensions ?? Options.EmbeddingDimensions;
    public string EmbeddingSpace => Options.EmbeddingFamily is { } family ? $"family:{family}" : Id;
    public bool Eligible => Online && Model.Installed && Instance is not null && Model.Operations.Any(operation => operation switch
    {
        InferenceOperation.Chat or InferenceOperation.SystemOne => Context is not null,
        InferenceOperation.Embeddings => Context is not null && EmbeddingDimensions is not null,
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
}

internal sealed class ProviderFailure(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

internal static class ProviderIdentity
{
    public static string Hash(params string?[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', parts))));

    public static string Model(Guid endpoint, string nativeId) => Hash(endpoint.ToString("N"), nativeId);
}
