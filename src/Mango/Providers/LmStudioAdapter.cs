using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mango.Providers;

internal sealed class LmStudioAdapter(HttpClient http, ProviderEndpoint endpoint,
    Func<Uri, CancellationToken, Task<LmStudioDevices?>>? inspectDevices = null) : ProviderAdapter(http, endpoint)
{
    private bool Generic => Endpoint.Profile == ProviderProfile.Generic;
    public override bool TransparentInference => Generic;
    private readonly HashSet<string> deviceAwareKeys = new(StringComparer.Ordinal);
    private LmStudioDevices? deviceInventory;

    public override void Prepare(InferenceOperation operation, JsonObject payload)
    {
        if (!Generic && operation == InferenceOperation.Chat && payload["max_completion_tokens"] is not null)
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
            if (Generic)
            {
                var reportedInputs = row.Get("input_modalities").Strings();
                if (reportedInputs.Length != 0 && reportedInputs.All(value => value is "text" or "image" or "audio"))
                    input = reportedInputs;
                else if (capabilities.Strings().Contains("vision")) input = ["image", "text"];
            }
            var instances = row.Get("loaded_instances").Items().Where(instance => instance.Text("id") is { Length: > 0 })
                .Select(instance =>
                {
                    var config = instance.Get("config");
                    var id = instance.Text("id")!;
                    var configuration = ProviderIdentity.Hash(Endpoint.Id.ToString("N"), key, version, variant,
                        row.Long("size_bytes")?.ToString(), config.ValueKind == JsonValueKind.Undefined ? null : config.GetRawText());
                    return new ProviderInstance(id, config.Number("context_length") ?? (Generic ? config.Number("context_size") : null), config.Number("parallel"),
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
                tools: capabilities.IsTrue("trained_for_tool_use") || Generic &&
                    (row.IsTrue("client_tools") || row.IsTrue("supports_tools") || capabilities.IsTrue("client_tools") ||
                     capabilities.IsTrue("tools") || capabilities.Strings().Contains("tools")),
                structured: type == "llm" && (!Generic || capabilities.IsTrue("structured_output")),
                dimensions: row.Number("embedding_dimensions"), encodings: type == "embedding" ? ["float"] : [],
                artifactBytes: row.Long("size_bytes"), format: row.Text("format"),
                mediaTokensPerItem: Generic ? row.Number("media_tokens_per_item") : null));
        }
        var devices = Generic ? null : await (inspectDevices ?? LmStudioDevices.InspectAsync)(new Uri(Endpoint.Address), cancellation);
        Volatile.Write(ref deviceInventory, devices);
        var inventory = Generic ? models.ToArray() : models.GroupBy(model => model.NativeId, StringComparer.Ordinal)
            .SelectMany(group => ResolveDevices(group.ToArray(), devices)).Take(ProviderLimits.MaxModels).ToArray();
        return new(version, inventory, Managed, Managed, false,
            MaxLoadedModels: Generic ? result.Number("max_loaded_models") : null);
    }

    private ProviderModel[] ResolveDevices(ProviderModel[] rows, LmStudioDevices? devices)
    {
        var key = rows[0].NativeId;
        var copies = devices?.Downloaded.Items().Where(item => item.Text("modelKey") == key)
            .GroupBy(devices.Device, StringComparer.Ordinal).ToArray() ?? [];
        if (devices is null || copies.Length == 0 || rows.Length > copies.Sum(group => group.Count()))
        {
            var model = rows.FirstOrDefault(item => item.Instances.Length > 0) ?? rows[0];
            return rows.Length > 1 || deviceAwareKeys.Contains(key)
                ? [model with { AmbiguousInstance = true, Instances = [], SupportsLoad = false, SupportsUnload = false }]
                : [model];
        }

        var loaded = devices.Loaded.Items().Where(item => item.Text("modelKey") == key).ToArray();
        var identifierCounts = devices.Loaded.Items().ToLookup(item => item.Text("identifier"), StringComparer.Ordinal);
        if (copies.Length > 1 || copies.Any(copy => copy.Key is not null)) deviceAwareKeys.Add(key);
        var instances = rows.SelectMany(row => row.Instances).ToLookup(instance => instance.Id, StringComparer.Ordinal);
        return copies.Select(copy =>
        {
            var remote = copy.Key is { Length: > 0 };
            var onDevice = loaded.Where(item => devices.Device(item) == copy.Key).ToArray();
            var downloaded = copy.First();
            var known = onDevice.Where(item => item.Text("identifier") is { } id && identifierCounts[id].Count() == 1)
                .SelectMany(item => instances[item.Text("identifier")!].Take(1)).DistinctBy(item => item.Id).ToArray();
            var matching = rows.Where(row => row.ArtifactBytes == downloaded.Long("sizeBytes") && row.Format == downloaded.Text("format"));
            var model = rows.FirstOrDefault(row => row.Instances.Any(item => known.Contains(item))) ?? matching.FirstOrDefault() ?? rows[0];
            return model with
            {
                Id = DeviceModelId(key, copy.Key),
                Instances = known, Origin = remote ? ModelOrigin.LmLink : ModelOrigin.Local,
                SourceDevice = remote ? devices.Name(copy.Key!) : null,
                SupportsLoad = !remote && rows.Length == 1 && copies.Length == 1 && onDevice.Length == known.Length,
                SupportsUnload = !remote && onDevice.Length == known.Length,
                AmbiguousInstance = known.Length == 0 && onDevice.Length != 0
            };
        }).ToArray();
    }

    private string DeviceModelId(string key, string? device) => device is null ? ProviderIdentity.Model(Endpoint.Id, key)
        : ProviderIdentity.Hash(Endpoint.Id.ToString("N"), key, device);

    private async Task<LmStudioDevices?> CurrentInstancesAsync(CancellationToken cancellation)
    {
        if (Volatile.Read(ref deviceInventory) is not { } inventory) return null;
        var loaded = inspectDevices is null ? await LmStudioDevices.ReadLoadedAsync(cancellation)
            : (await inspectDevices(new Uri(Endpoint.Address), cancellation))?.Loaded ?? default;
        return loaded.ValueKind == JsonValueKind.Array ? inventory with { Loaded = loaded } : null;
    }

    public override async Task ConfirmDeploymentAsync(InferenceOperation operation, ProviderModel model, ProviderInstance instance,
        CancellationToken cancellation)
    {
        if (model.Origin is null || model is { Origin: ModelOrigin.Local, SupportsLoad: true }) return;
        var current = await CurrentInstancesAsync(cancellation);
        var matches = current?.Loaded.Items().Where(item => item.Text("identifier") == instance.Id).ToArray() ?? [];
        if (matches.Length != 1 || matches[0].Text("modelKey") != model.NativeId ||
            DeviceModelId(model.NativeId, current!.Device(matches[0])) != model.Id)
            throw new ProviderFailure(503, "LM Studio could not confirm the loaded instance on its expected device.");
    }

    public override async Task<bool?> CheckIdleAsync(ProviderModel model, CancellationToken cancellation)
    {
        if (!Generic)
        {
            if (model.Origin is null || await CurrentInstancesAsync(cancellation) is not { } current) return null;
            var onDevice = current.Loaded.Items().Where(item => item.Text("modelKey") == model.NativeId &&
                DeviceModelId(model.NativeId, current.Device(item)) == model.Id).ToArray();
            if (onDevice.Any(item => item.Text("status") is "generating" || item.Number("queued") > 0)) return false;
            return onDevice.All(item => item.Text("status") == "idle") ? true : null;
        }
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
            if (row.Get("media_tokens_per_item").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) &&
                row.Number("media_tokens_per_item") is not (>= 1 and <= ProviderLimits.MaxContext))
                throw new ProviderFailure(502, "Generic media_tokens_per_item must be a positive bounded token count.");
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
        {
            if (!Generic && !await InstanceStillLoadedAsync(model, instance, cancellation))
                continue;
            if (model.Origin == ModelOrigin.Local && !model.SupportsLoad)
            {
                await ConfirmDeploymentAsync(default, model, instance, cancellation);
                // Panini embedding instances are exposed in HTTP inventory but require
                // the native local lifecycle control to release their allocations.
                if (!Generic && new Uri(Endpoint.Address).IsLoopback &&
                    model.Operations.SequenceEqual([InferenceOperation.Embeddings]))
                {
                    await LmStudioDevices.UnloadLocalAsync(instance.Id, cancellation);
                    if (await InstanceStillLoadedAsync(model, instance, cancellation))
                        throw new ProviderFailure(503, "LM Studio did not release the verified local embedding instance.");
                    continue;
                }
            }
            try
            {
                await JsonAsync("/api/v1/models/unload", cancellation, new { instance_id = instance.Id }, lifecycle: true);
            }
            catch (ProviderFailure) when (!Generic)
            {
                // LM Studio can finish an earlier unload between inventory and this request.
                // Only accept that outcome when fresh inventory proves the instance is gone.
                if (await InstanceStillLoadedAsync(model, instance, cancellation)) throw;
            }
        }
    }

    private async Task<bool> InstanceStillLoadedAsync(ProviderModel model, ProviderInstance instance,
        CancellationToken cancellation)
    {
        var inventory = await ReadInventoryAsync(cancellation);
        var matches = inventory.Get("models").Items()
            .Where(row => row.Get("loaded_instances").Items().Any(item => item.Text("id") == instance.Id)).ToArray();
        if (matches.Length == 0) return false;
        if (matches.Length != 1 || matches[0].Text("key") != model.NativeId)
            throw new ProviderFailure(503, "LM Studio could not confirm the instance belongs to the intended model.");
        return true;
    }
}
