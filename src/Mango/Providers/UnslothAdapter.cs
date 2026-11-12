using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mango.Providers;

internal sealed class UnslothAdapter(HttpClient http, ProviderEndpoint endpoint) : ProviderAdapter(http, endpoint)
{
    public override async Task<ProviderInventory> InspectAsync(CancellationToken cancellation)
    {
        var health = await JsonAsync("/api/health", cancellation);
        if (health.Text("service") is not { } service || !service.Contains("Unsloth", StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure(502, "This endpoint did not identify itself as Unsloth Studio.");
        var version = health.Text("version") ?? health.Text("studio_version");
        var list = await JsonAsync("/v1/models?output_modalities=all", cancellation);
        if (list.Get("data").ValueKind != JsonValueKind.Array)
            throw new ProviderFailure(502, "Unsloth did not return its model catalog.");
        var resident = await JsonAsync("/api/inference/loaded-models", cancellation, optional: true);
        var image = await JsonAsync("/api/inference/images/status", cancellation, optional: true);
        var local = Managed ? await JsonAsync("/api/models/local", cancellation, optional: true) : default;
        var localModels = local.Get("models").Items().Where(row => !row.IsTrue("partial") && row.Text("id") is not null)
            .GroupBy(row => row.Text("model_id") ?? row.Text("display_name") ?? "", StringComparer.Ordinal)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var loaded = resident.Get("data").Items().Where(row => row.Text("id") is not null)
            .DistinctBy(row => row.Text("id")).ToDictionary(row => row.Text("id")!, StringComparer.Ordinal);
        var models = new List<ProviderModel>();
        foreach (var row in list.Get("data").Items().Take(ProviderLimits.MaxModels))
        {
            if (row.Text("id") is not { Length: > 0 } id)
                continue;
            var known = loaded.GetValueOrDefault(id);
            var isLoaded = known.ValueKind != JsonValueKind.Undefined || row.IsTrue("loaded");
            var publicQuant = row.Text("quant");
            var localId = publicQuant is not null && id.EndsWith($":{publicQuant}", StringComparison.OrdinalIgnoreCase)
                ? id[..^(publicQuant.Length + 1)] : id;
            var disk = localModels.GetValueOrDefault(localId);
            var task = row.Text("task") ?? disk.Text("task");
            var architecture = row.Get("architecture");
            var inputs = architecture.Get("input_modalities").Strings();
            var outputs = architecture.Get("output_modalities").Strings();
            var operation = task switch
            {
                "text-to-image" => InferenceOperation.Images,
                "automatic-speech-recognition" => InferenceOperation.Transcription,
                "text-to-speech" => InferenceOperation.Speech,
                "feature-extraction" or "embeddings" => InferenceOperation.Embeddings,
                "text-generation" or "image-text-to-text" => InferenceOperation.Chat,
                null when outputs.Contains("decisions") => InferenceOperation.SystemOne,
                null when outputs.Contains("embedding") || outputs.Contains("embeddings") => InferenceOperation.Embeddings,
                null => InferenceOperation.Chat,
                _ => (InferenceOperation?)null
            };
            var status = isLoaded && operation is InferenceOperation.Chat or InferenceOperation.Embeddings
                ? await JsonAsync($"/api/inference/status?model={Uri.EscapeDataString(id)}", cancellation, optional: true)
                : default;
            if (inputs.Length == 0)
                inputs = operation == InferenceOperation.Transcription ? ["audio"] :
                    status.IsTrue("is_vision") || row.IsTrue("is_vision") ? ["image", "text"] : ["text"];
            if (outputs.Length == 0)
                outputs = operation switch
                {
                    InferenceOperation.Chat or InferenceOperation.Transcription => ["text"],
                    InferenceOperation.Images => ["image"],
                    InferenceOperation.Speech => ["audio"],
                    InferenceOperation.Embeddings => ["embedding"],
                    _ => []
                };
            var variant = known.Text("quant") ?? row.Text("quant") ?? status.Text("gguf_variant");
            var format = disk.Text("model_format") ?? (status.IsTrue("is_gguf") ? "gguf" : null);
            var context = known.Number("context_length") ?? row.Number("context_length") ?? status.Number("context_length");
            var slots = known.Number("parallel_slots") ?? status.Number("parallel_slots");
            var residentId = id == status.Text("active_model") || id == status.Text("model_identifier") ||
                loaded.Count == 1 && loaded.ContainsKey(id) ? status.Text("model_identifier") : null;
            var localRef = disk.Text("id");
            var managementId = residentId ?? localRef ?? id;
            var fingerprint = ProviderIdentity.Hash(Endpoint.Id.ToString("N"), id, variant, version,
                context?.ToString(), slots?.ToString(), status.Text("engine"), status.Text("backend"),
                status.Text("engine_precision"), status.Text("engine_kv_cache_dtype"));
            var instances = isLoaded ? new[] { new ProviderInstance(id, context, slots, fingerprint) } : [];
            models.Add(Model(id, row.Text("display_name") ?? id, variant, inputs, outputs,
                operation.HasValue ? [operation.Value] : [], instances,
                known.Number("native_context_length") ?? row.Number("native_context_length") ?? status.Number("native_context_length"),
                tools: status.IsTrue("supports_tools") || row.IsTrue("supports_tools"),
                structured: operation == InferenceOperation.Chat && (status.IsTrue("is_gguf") || known.ValueKind != JsonValueKind.Undefined),
                dimensions: row.Number("embedding_dimensions"),
                encodings: operation == InferenceOperation.Embeddings ? ["float"] : [],
                managementId: managementId, installed: operation != InferenceOperation.SystemOne || isLoaded,
                supportsLoad: operation is InferenceOperation.Transcription ||
                    operation is not (null or InferenceOperation.SystemOne) && localRef is not null && format is not null,
                supportsUnload: operation is not (null or InferenceOperation.SystemOne), format: format));
        }
        if (image.IsTrue("loaded") && image.Text("repo_id") is { Length: > 0 } repo)
        {
            var native = image.Text("display_repo_id") ?? repo;
            var existing = models.FirstOrDefault(model => model.Operations.Contains(InferenceOperation.Images) &&
                (model.NativeId == native || model.NativeId == repo));
            var generation = image.Text("resident_load_id") ?? image.Text("load_id");
            var fingerprint = ProviderIdentity.Hash(Endpoint.Id.ToString("N"), native, repo, version, image.Text("engine"),
                image.Text("base_repo"), image.Text("family"), image.Text("gguf_filename"), image.Text("model_kind"));
            var variant = image.Text("gguf_filename") ?? existing?.Variant;
            if (existing is not null)
            {
                models.Remove(existing);
                native = existing.NativeId;
            }
            models.Add(Model(native, existing?.DisplayName ?? native, variant, ["text"], ["image"],
                [InferenceOperation.Images], [new(native, null, null, fingerprint, NativeGeneration: generation)], null,
                managementId: repo, supportsLoad: existing?.SupportsLoad == true, supportsUnload: generation is not null,
                format: image.Text("model_kind")));
        }
        return new(version, models.DistinctBy(model => model.Id).ToArray(), Managed, Managed, false);
    }

    public override async Task ConfirmDeploymentAsync(InferenceOperation operation, ProviderModel model, ProviderInstance instance,
        CancellationToken cancellation)
    {
        if (operation != InferenceOperation.Images)
            return;
        var status = await JsonAsync("/api/inference/images/status", cancellation);
        if (!status.IsTrue("loaded") || status.Text("repo_id") != model.ManagementId ||
            instance.NativeGeneration != (status.Text("resident_load_id") ?? status.Text("load_id")))
            throw new ProviderFailure(409, "The selected Unsloth image deployment is no longer resident.");
    }

    public override void Prepare(InferenceOperation operation, JsonObject payload)
    {
        if (operation == InferenceOperation.Speech && payload["stream_format"] is JsonValue format &&
            format.TryGetValue<string>(out var streamFormat) && streamFormat != "audio")
            throw new ProviderFailure(422, "This Unsloth speech endpoint supports audio responses.");
        if (operation != InferenceOperation.Chat)
            return;
        payload["enable_tools"] = false;
        payload["mcp_enabled"] = false;
        payload["max_tool_calls_per_message"] = 0;
        payload["context_overflow"] = "error";
    }

    public override async Task<int?> CountChatAsync(JsonObject payload, CancellationToken cancellation)
    {
        try
        {
            var result = await JsonAsync("/api/inference/chat/count_tokens", cancellation, payload, optional: true);
            return result.Number("input_tokens") ?? result.Number("prompt_tokens") ?? result.Number("token_count");
        }
        catch (ProviderFailure error) when (error.Status == 503)
        {
            return null;
        }
    }

    public override async Task<bool?> CheckIdleAsync(ProviderModel model, CancellationToken cancellation)
    {
        if (!model.Operations.Contains(InferenceOperation.Chat))
            return null;
        var result = await JsonAsync($"/api/inference/active-generations?model={Uri.EscapeDataString(model.NativeId)}",
            cancellation, optional: true);
        return result.Get("active").ValueKind == JsonValueKind.Array ? result.Get("active").GetArrayLength() == 0 : null;
    }

    public override async Task LoadAsync(ProviderModel model, int? context, CancellationToken cancellation)
    {
        if (!Managed || model.Operations.Contains(InferenceOperation.SystemOne))
        {
            await base.LoadAsync(model, context, cancellation);
            return;
        }
        if (model.Operations.Contains(InferenceOperation.Transcription))
        {
            await JsonAsync("/api/inference/audio/stt/load", cancellation, new { model = model.ManagementId }, lifecycle: true);
            return;
        }
        if (model.Operations.Contains(InferenceOperation.Images))
        {
            await JsonAsync("/api/inference/images/load", cancellation, new { model_path = model.ManagementId }, lifecycle: true);
            return;
        }
        var body = new Dictionary<string, object>
        {
            ["model_path"] = model.ManagementId,
            ["trust_remote_code"] = false,
            ["is_gguf"] = model.Format == "gguf"
        };
        if (model.Format == "gguf" && model.Variant is { } variant)
            body["gguf_variant"] = variant;
        if (context.HasValue)
        {
            body["max_seq_length"] = context.Value;
            if (model.Format == "gguf")
                body["context_length"] = context.Value;
        }
        await JsonAsync("/api/inference/load", cancellation, body, lifecycle: true);
    }

    public override async Task UnloadAsync(ProviderModel model, CancellationToken cancellation)
    {
        if (!Managed || model.Operations.Contains(InferenceOperation.SystemOne))
        {
            await base.UnloadAsync(model, cancellation);
            return;
        }
        if (model.Operations.Contains(InferenceOperation.Images))
        {
            if (model.Instances.FirstOrDefault() is not { NativeGeneration: { Length: > 0 } load } instance)
                throw new ProviderFailure(409, "Unsloth did not report the selected image load identity.");
            await ConfirmDeploymentAsync(InferenceOperation.Images, model, instance, cancellation);
            await JsonAsync("/api/inference/images/unload", cancellation, new { expected_load_id = load }, lifecycle: true);
            return;
        }
        if (model.Operations.Contains(InferenceOperation.Transcription))
        {
            await JsonAsync($"/api/inference/audio/stt/unload?model={Uri.EscapeDataString(model.ManagementId)}&wait=false",
                cancellation, new { }, lifecycle: true);
            return;
        }
        await JsonAsync("/api/inference/unload", cancellation, new { model_path = model.ManagementId }, lifecycle: true);
    }
}
