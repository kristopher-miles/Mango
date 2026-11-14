using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mango.Providers;

internal sealed class LmStudioAdapter(HttpClient http, ProviderEndpoint endpoint) : ProviderAdapter(http, endpoint)
{
    private bool Generic => Endpoint.Profile == ProviderProfile.Generic;

    public override void Prepare(InferenceOperation operation, JsonObject payload)
    {
        if (operation == InferenceOperation.Chat && payload["max_completion_tokens"] is not null)
            throw new ProviderFailure(422, Generic ? "Use max_tokens to limit output on the generic profile."
                : "Use max_tokens to limit output on this LM Studio profile.");
    }

    public override async Task<ProviderInventory> InspectAsync(CancellationToken cancellation)
    {
        var result = await ReadInventoryAsync(cancellation);
        var version = result.Text("version");
        var models = new List<ProviderModel>();
        foreach (var row in result.Get("models").Items().Take(ProviderLimits.MaxModels))
        {
            if (row.Text("key") is not { Length: > 0 } key)
                continue;
            var capabilities = row.Get("capabilities");
            var type = row.Text("type");
            var variant = row.Text("selected_variant") ?? row.Get("quantization").Text("name") ?? row.Text("format");
            var input = Generic && type == "transcription" ? new[] { "audio" } :
                capabilities.IsTrue("vision") || row.IsTrue("has_vision_adapter") ? ["image", "text"] : ["text"];
            var instances = row.Get("loaded_instances").Items().Where(instance => instance.Text("id") is { Length: > 0 })
                .Select(instance =>
                {
                    var config = instance.Get("config");
                    var id = instance.Text("id")!;
                    var configuration = ProviderIdentity.Hash(Endpoint.Id.ToString("N"), key, version, variant,
                        row.Long("size_bytes")?.ToString(), config.ValueKind == JsonValueKind.Undefined ? null : config.GetRawText());
                    return new ProviderInstance(id, config.Number("context_length"), config.Number("parallel"),
                        ProviderIdentity.Hash(Endpoint.Id.ToString("N"), key, id, version, variant,
                            config.ValueKind == JsonValueKind.Undefined ? null : config.GetRawText()),
                        Busy: Generic && instance.IsTrue("busy"),
                        ConfigurationFingerprint: configuration);
                }).ToArray();
            var (outputs, operations) = type switch
            {
                "llm" => (new[] { "text" }, new[] { InferenceOperation.Chat }),
                "embedding" => (["embedding"], [InferenceOperation.Embeddings]),
                "decision" => (["decisions"], [InferenceOperation.SystemOne]),
                "image" when Generic => (["image"], [InferenceOperation.Images]),
                "transcription" when Generic => (["text"], [InferenceOperation.Transcription]),
                "speech" when Generic => (["audio"], [InferenceOperation.Speech]),
                _ => (Array.Empty<string>(), Array.Empty<InferenceOperation>())
            };
            models.Add(Model(key, row.Text("display_name") ?? key, variant,
                input, outputs, operations, instances, row.Number("max_context_length"),
                tools: capabilities.IsTrue("trained_for_tool_use"),
                structured: type == "llm" && (!Generic || capabilities.IsTrue("structured_output")),
                dimensions: row.Number("embedding_dimensions"), encodings: type == "embedding" ? ["float"] : [],
                artifactBytes: row.Long("size_bytes"), format: row.Text("format")));
        }
        return new(version, models.ToArray(), Managed, Managed, false,
            MaxLoadedModels: Generic ? result.Number("max_loaded_models") : null);
    }

    public override async Task<bool?> CheckIdleAsync(ProviderModel model, CancellationToken cancellation)
    {
        if (!Generic)
            return null;
        var result = await ReadInventoryAsync(cancellation);
        var instances = result.Get("models").Items().SingleOrDefault(row => row.Text("key") == model.NativeId)
            .Get("loaded_instances").Items().ToArray();
        if (instances.Any(instance => instance.IsTrue("busy")))
            return false;
        return instances.All(instance => instance.Get("busy").ValueKind == JsonValueKind.False) ? true : null;
    }

    private async Task<JsonElement> ReadInventoryAsync(CancellationToken cancellation)
    {
        var result = await JsonAsync("/api/v1/models", cancellation);
        var rows = result.Get("models");
        if (rows.ValueKind != JsonValueKind.Array)
            throw new ProviderFailure(502, Generic ? "The generic provider must return a models array from /api/v1/models."
                : "This endpoint did not return the LM Studio native model list.");
        if (!Generic)
            return result;

        if (rows.GetArrayLength() > ProviderLimits.MaxModels ||
            result.Get("max_loaded_models").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
            result.Number("max_loaded_models") is not (> 0 and <= ProviderLimits.MaxModels))
            throw new ProviderFailure(502, "The generic provider returned an invalid model count or capacity.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            var key = row.Text("key");
            var instances = row.Get("loaded_instances");
            if (string.IsNullOrWhiteSpace(key) || !keys.Add(key) || string.IsNullOrWhiteSpace(row.Text("type")) ||
                instances.ValueKind != JsonValueKind.Array)
                throw new ProviderFailure(502, "Each generic model requires a unique key, a type, and a loaded_instances array.");
            foreach (var instance in instances.EnumerateArray())
            {
                var id = instance.Text("id");
                if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) ||
                    instance.Get("busy").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.True or JsonValueKind.False))
                    throw new ProviderFailure(502, "Generic inventory requires unique loaded instance IDs and boolean busy flags when supplied.");
            }
        }
        return result;
    }

    public override async Task LoadAsync(ProviderModel model, int? context, CancellationToken cancellation)
    {
        if (!Managed)
        {
            await base.LoadAsync(model, context, cancellation);
            return;
        }
        var body = new Dictionary<string, object> { ["model"] = model.ManagementId, ["echo_load_config"] = true };
        if (context.HasValue)
            body["context_length"] = context.Value;
        await JsonAsync("/api/v1/models/load", cancellation, body, lifecycle: true);
    }

    public override async Task UnloadAsync(ProviderModel model, CancellationToken cancellation)
    {
        if (!Managed)
        {
            await base.UnloadAsync(model, cancellation);
            return;
        }
        foreach (var instance in model.Instances)
            await JsonAsync("/api/v1/models/unload", cancellation, new { instance_id = instance.Id }, lifecycle: true);
    }
}
