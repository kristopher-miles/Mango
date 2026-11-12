using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mango.Providers;

internal sealed class LlamaAdapter(HttpClient http, ProviderEndpoint endpoint) : ProviderAdapter(http, endpoint)
{
    private Process? owned;
    private bool Router => Endpoint.Profile == ProviderProfile.LlamaRouter;
    public bool HasInventoryEvents => Router;
    private bool CanLaunch => Managed && !Router && Endpoint.Executable is not null && Endpoint.ModelFile is not null;
    protected override string InferenceQuery => Router ? "?autoload=false" : "";
    public override int? OwnedProcessId => owned is { HasExited: false } process ? process.Id : null;

    public ProviderModel? ConfiguredModel => CanLaunch
        ? Model(Path.GetFileNameWithoutExtension(Endpoint.ModelFile!),
            Path.GetFileNameWithoutExtension(Endpoint.ModelFile!), null, [], [], [], [], null,
            installed: File.Exists(Endpoint.ModelFile)) : null;

    public override async Task<ProviderInventory> InspectAsync(CancellationToken cancellation)
    {
        var root = await JsonAsync("/props", cancellation);
        if ((root.Text("role") == "router") != Router || root.Get("default_generation_settings").ValueKind == JsonValueKind.Undefined)
            throw new ProviderFailure(502, "llama-server does not match the selected single-model or router profile.");
        var version = root.Text("build_info");
        var list = await JsonAsync(Router ? "/models" : "/v1/models", cancellation);
        if (list.Get("data").ValueKind != JsonValueKind.Array)
            throw new ProviderFailure(502, "llama-server did not return a model list.");
        var models = new List<ProviderModel>();
        foreach (var row in list.Get("data").Items().Take(ProviderLimits.MaxModels))
        {
            if (row.Text("id") is not { Length: > 0 } id)
                continue;
            var state = row.Get("status").Text("value");
            var loaded = Router ? state == "loaded" : !root.IsTrue("is_sleeping");
            var props = Router
                ? loaded ? await JsonAsync(Target("/props", id), cancellation, optional: true) : default
                : root;
            var modalities = row.Get("architecture");
            var inputs = modalities.Get("input_modalities").Strings();
            if (inputs.Length == 0 && loaded)
                inputs = props.Get("modalities").IsTrue("vision") ? ["image", "text"] : ["text"];
            var outputs = modalities.Get("output_modalities").Strings().Select(value => value == "embeddings" ? "embedding" : value)
                .Distinct(StringComparer.Ordinal).ToArray();
            var embedding = Endpoint.Profile == ProviderProfile.LlamaEmbedding ||
                row.Get("status").Get("args").Strings().Any(argument => argument is "--embedding" or "--embeddings");
            if (embedding)
                outputs = ["embedding"];
            else if (outputs.Length == 0 && loaded)
                outputs = ["text"];
            var operations = outputs.Select(output => output switch
            {
                "text" => (InferenceOperation?)InferenceOperation.Chat,
                "embeddings" or "embedding" => InferenceOperation.Embeddings,
                "decisions" => InferenceOperation.SystemOne,
                _ => null
            }).OfType<InferenceOperation>().Distinct().ToArray();
            var meta = row.Get("meta");
            var context = props.Get("default_generation_settings").Number("n_ctx") ?? meta.Number("n_ctx");
            var caps = props.Get("chat_template_caps");
            var fingerprint = ProviderIdentity.Hash(Endpoint.Id.ToString("N"), id, version, meta.Text("ftype"),
                props.Text("model_path"), props.Text("chat_template"), props.Text("model_ftype"),
                context?.ToString(), props.Number("total_slots")?.ToString(), row.Get("status").Text("preset"));
            var instances = loaded ? new[] { new ProviderInstance(id, context, props.Number("total_slots"), fingerprint) } : [];
            models.Add(Model(id, id, meta.Text("ftype") ?? props.Text("model_ftype"),
                inputs, outputs, operations, instances, meta.Number("n_ctx_train") ?? row.Number("context_length"),
                tools: caps.IsTrue("supports_tools") || caps.IsTrue("supports_tool_calls"),
                structured: operations.Contains(InferenceOperation.Chat),
                dimensions: operations.Contains(InferenceOperation.Embeddings) ? meta.Number("n_embd") : null,
                encodings: operations.Contains(InferenceOperation.Embeddings) ? ["base64", "float"] : [],
                artifactBytes: meta.Long("size"), installed: state is not ("downloading" or "incomplete"), format: "gguf"));
        }
        return new(version, models.ToArray(), Managed && (Router || CanLaunch), Managed && (Router || OwnedProcessId.HasValue),
            OwnedProcessId.HasValue, OwnedProcessId);
    }

    public override async Task<int?> CountChatAsync(JsonObject payload, CancellationToken cancellation)
    {
        var result = await JsonAsync("/v1/chat/completions/input_tokens" + InferenceQuery, cancellation, payload, optional: true);
        return result.Number("input_tokens");
    }

    public override void Prepare(InferenceOperation operation, JsonObject payload)
    {
        if (operation == InferenceOperation.Chat && payload["max_completion_tokens"] is { } limit)
        {
            payload["max_tokens"] = limit.DeepClone();
            payload.Remove("max_completion_tokens");
        }
    }

    public override async Task<int?> CountEmbeddingAsync(JsonNode input, string nativeId, CancellationToken cancellation)
    {
        var result = await JsonAsync(Target("/tokenize", nativeId), cancellation,
            new { content = input, add_special = true }, optional: true);
        return result.Get("tokens").ValueKind == JsonValueKind.Array ? result.Get("tokens").GetArrayLength() : null;
    }

    public override async Task<bool?> CheckIdleAsync(ProviderModel model, CancellationToken cancellation)
    {
        var slots = await JsonAsync(Target("/slots", model.NativeId), cancellation, optional: true);
        return slots.ValueKind == JsonValueKind.Array && slots.GetArrayLength() > 0 &&
               slots.Items().All(slot => slot.Get("is_processing").ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? slots.Items().All(slot => !slot.IsTrue("is_processing")) : null;
    }

    public async Task WatchInventoryAsync(Action changed, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Get, "/models/sse", null, cancellation);
                RequireSuccess(response);
                await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
                var buffer = new byte[1024];
                var prefix = "data:"u8.ToArray();
                var matched = 0;
                int count;
                while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
                    for (var index = 0; index < count; index++)
                    {
                        var value = buffer[index];
                        if (value == '\n')
                            matched = 0;
                        else if (matched >= 0 && matched < prefix.Length)
                        {
                            matched = value == prefix[matched] ? matched + 1 : -1;
                            if (matched == prefix.Length)
                                changed();
                        }
                    }
            }
            catch (Exception error) when (error is IOException or HttpRequestException or ProviderFailure) { }
            await Task.Delay(ProviderLimits.InventoryInterval, cancellation);
        }
    }

    public override async Task LoadAsync(ProviderModel model, int? context, CancellationToken cancellation)
    {
        if (Managed && Router)
        {
            await JsonAsync("/models/load", cancellation, new { model = model.ManagementId }, lifecycle: true);
            return;
        }
        if (!CanLaunch)
        {
            await base.LoadAsync(model, context, cancellation);
            return;
        }
        if (OwnedProcessId.HasValue)
            throw new ProviderFailure(409, "Mango's llama-server is already running.");
        if (!File.Exists(Endpoint.Executable) || !File.Exists(Endpoint.ModelFile))
            throw new ProviderFailure(409, "The authorized executable or GGUF file is missing.");
        var uri = new Uri(Endpoint.Address);
        using var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(uri.Host, uri.Port, cancellation);
            throw new ProviderFailure(409, "A process already occupies the configured llama-server address.");
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused) { }

        var start = new ProcessStartInfo(Endpoint.Executable!)
        {
            WorkingDirectory = Path.GetDirectoryName(Endpoint.Executable)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--host", uri.Host, "--port", uri.Port.ToString(),
                     "--model", Endpoint.ModelFile!, "--alias", model.NativeId, "--parallel", "1",
                     "--no-webui", "--jinja", "--no-context-shift" })
            start.ArgumentList.Add(argument);
        if (context.HasValue)
        {
            start.ArgumentList.Add("--ctx-size");
            start.ArgumentList.Add(context.Value.ToString());
        }
        if (Endpoint.Profile == ProviderProfile.LlamaEmbedding)
            start.ArgumentList.Add("--embedding");
        if (Endpoint.ProtectedToken is { } protectedToken)
            start.Environment["LLAMA_ARG_API_KEY"] = ProviderSecrets.Open(protectedToken);
        owned?.Dispose();
        owned = Process.Start(start) ?? throw new ProviderFailure(502, "The authorized llama-server could not start.");
    }

    public override async Task UnloadAsync(ProviderModel model, CancellationToken cancellation)
    {
        if (Managed && Router)
            await JsonAsync("/models/unload", cancellation, new { model = model.ManagementId }, lifecycle: true);
        else
            await StopAsync(cancellation);
    }

    public override async Task StopAsync(CancellationToken cancellation)
    {
        if (!OwnedProcessId.HasValue || owned is null)
        {
            await base.StopAsync(cancellation);
            return;
        }
        try
        {
            owned.Kill(entireProcessTree: true);
            await owned.WaitForExitAsync(cancellation);
        }
        catch (InvalidOperationException) when (owned.HasExited) { }
        owned.Dispose();
        owned = null;
    }

    private string Target(string path, string model) =>
        Router ? $"{path}?model={Uri.EscapeDataString(model)}&autoload=false" : path;
}
