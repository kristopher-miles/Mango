using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mango.Providers;

internal sealed class LmStudioAdapter(HttpClient http, ProviderEndpoint endpoint) : ProviderAdapter(http, endpoint)
{
    public override void Prepare(InferenceOperation operation, JsonObject payload)
    {
        if (operation == InferenceOperation.Chat && payload["max_completion_tokens"] is not null)
            throw new ProviderFailure(422, "Use max_tokens to limit output on this LM Studio profile.");
    }

    public override async Task<ProviderInventory> InspectAsync(CancellationToken cancellation)
    {
        var result = await JsonAsync("/api/v1/models", cancellation);
        if (result.Get("models").ValueKind != JsonValueKind.Array)
            throw new ProviderFailure(502, "This endpoint did not return the LM Studio native model list.");
        var version = result.Text("version");
        var models = new List<ProviderModel>();
        foreach (var row in result.Get("models").Items().Take(ProviderLimits.MaxModels))
        {
            if (row.Text("key") is not { Length: > 0 } key)
                continue;
            var capabilities = row.Get("capabilities");
            var type = row.Text("type");
            var variant = row.Text("selected_variant") ?? row.Get("quantization").Text("name") ?? row.Text("format");
            var input = capabilities.IsTrue("vision") || row.IsTrue("has_vision_adapter") ? new[] { "image", "text" } : ["text"];
            var instances = row.Get("loaded_instances").Items().Where(instance => instance.Text("id") is { Length: > 0 })
                .Select(instance =>
                {
                    var config = instance.Get("config");
                    var id = instance.Text("id")!;
                    return new ProviderInstance(id, config.Number("context_length"), config.Number("parallel"),
                        ProviderIdentity.Hash(Endpoint.Id.ToString("N"), key, id, version, variant,
                            config.ValueKind == JsonValueKind.Undefined ? null : config.GetRawText()));
                }).ToArray();
            var (outputs, operations) = type switch
            {
                "llm" => (new[] { "text" }, new[] { InferenceOperation.Chat }),
                "embedding" => (["embedding"], [InferenceOperation.Embeddings]),
                "decision" => (["decisions"], [InferenceOperation.SystemOne]),
                _ => (Array.Empty<string>(), Array.Empty<InferenceOperation>())
            };
            models.Add(Model(key, row.Text("display_name") ?? key, variant,
                input, outputs, operations, instances, row.Number("max_context_length"),
                tools: capabilities.IsTrue("trained_for_tool_use"), structured: type == "llm",
                dimensions: row.Number("embedding_dimensions"), encodings: type == "embedding" ? ["float"] : [],
                artifactBytes: row.Long("size_bytes"), format: row.Text("format")));
        }
        return new(version, models.ToArray(), Managed, Managed, false);
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
