using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Mango.Providers;

internal sealed record ProviderExecution(string DeploymentId, string InstanceFingerprint, InferenceOperation Operation,
    long AliasRevision, int? PromptTokens, string? PromptCountSource, TimeSpan ProviderHeaders,
    TimeSpan? FirstOutput, TimeSpan Elapsed);

internal sealed class InferenceRequest(InferenceOperation operation, string alias, JsonObject? json, IFormCollection? form)
    : IDisposable
{
    private const int DefaultMaxOutput = 1024;
    private const int MaxBatch = 128;
    private const int MaxImagePixels = 4096 * 4096;
    private const int MaxSpeechCharacters = 4096;
    private HttpContent? outbound;
    public string Alias { get; } = alias;
    public bool Submitted { get; private set; }
    public bool Settled { get; private set; }

    private static readonly string[] ChatFields = ("model messages stream stream_options max_tokens max_completion_tokens " +
        "temperature top_p top_k min_p typical_p frequency_penalty presence_penalty repetition_penalty repeat_penalty " +
        "stop seed n logprobs top_logprobs logit_bias response_format tools tool_choice parallel_tool_calls " +
        "reasoning_effort user").Split(' ');
    private static readonly string[] EmbeddingFields = ["model", "input", "dimensions", "encoding_format", "user"];
    private static readonly string[] ImageFields = ("model prompt n size width height response_format " +
        "seed steps guidance negative_prompt stream user").Split(' ');
    private static readonly string[] SpeechFields = ["model", "input", "voice", "speed", "response_format",
        "stream_format", "instructions", "language"];
    private static readonly string[] DecisionFields = ["model", "state", "questions", "files", "images"];
    private static readonly string[] TranscriptionFields = ["model", "language", "response_format", "timestamp_granularities[]"];
    private static readonly string[] MessageFields = ["role", "content", "name", "tool_calls", "tool_call_id",
        "function_call", "refusal", "reasoning_content"];

    public static async Task<InferenceRequest> ReadAsync(InferenceOperation operation, HttpRequest request,
        CancellationToken cancellation)
    {
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = ProviderLimits.MaxRequestBytes;
        if (request.ContentLength > ProviderLimits.MaxRequestBytes)
            throw new ProviderFailure(413, "The inference request exceeds Mango's request size limit.");
        try
        {
            if (operation == InferenceOperation.Transcription)
            {
                if (!request.HasFormContentType || request.ContentType?.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase) != true)
                    throw new ProviderFailure(415, "Transcription requires a multipart audio file.");
                var form = await request.ReadFormAsync(cancellation);
                if (form.Files.Count != 1 || form.Files[0].Name != "file" ||
                    form.Files[0].Length is < 1 or > ProviderLimits.MaxAudioBytes)
                    throw new ProviderFailure(400, "Include one nonempty audio file within the upload limit.");
                foreach (var entry in form)
                    if (!TranscriptionFields.Contains(entry.Key, StringComparer.Ordinal) ||
                        entry.Value.Count != 1 && !entry.Key.EndsWith("[]", StringComparison.Ordinal))
                        throw new ProviderFailure(400, "The transcription form contains an unsupported or repeated field.");
                return new(operation, RequireAlias(form["model"].ToString()), null, form);
            }
            if (!request.HasJsonContentType())
                throw new ProviderFailure(415, "This operation requires an application/json request body.");
            var payload = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellation) as JsonObject
                ?? throw new ProviderFailure(400, "Send a JSON object.");
            RequireFields(payload, operation switch
            {
                InferenceOperation.Chat => ChatFields,
                InferenceOperation.Embeddings => EmbeddingFields,
                InferenceOperation.Images => ImageFields,
                InferenceOperation.Speech => SpeechFields,
                _ => DecisionFields
            });
            return new(operation, RequireAlias(String(payload["model"])), payload, null);
        }
        catch (JsonException)
        {
            throw new ProviderFailure(400, "The inference body contains invalid JSON.");
        }
        catch (ArgumentException)
        {
            throw new ProviderFailure(400, "The inference body contains invalid or duplicate fields.");
        }
        catch (InvalidDataException)
        {
            throw new ProviderFailure(400, "The transcription form is invalid or exceeds the upload limits.");
        }
    }

    public async Task<ProviderExecution> ForwardAsync(Deployment deployment, ProviderAdapter adapter,
        HttpContext context, CancellationToken cancellation)
    {
        if (!deployment.Model.Operations.Contains(operation))
            throw new ProviderFailure(422, "This deployment does not support the requested inference operation.");
        var instance = deployment.Instance ?? throw new ProviderFailure(409, "The selected deployment is not loaded.");
        int? promptTokens = null;
        string? promptCountSource = null;
        string? embeddingEncoding = null;
        var embeddingCount = 0;
        var started = Stopwatch.GetTimestamp();
        if (json is { } payload)
        {
            payload["model"] = instance.Id;
            try
            {
                switch (operation)
                {
                    case InferenceOperation.Chat:
                        (promptTokens, promptCountSource) = await PrepareChatAsync(payload, deployment, adapter, cancellation);
                        break;
                    case InferenceOperation.Embeddings:
                        (promptTokens, promptCountSource, embeddingEncoding, embeddingCount) =
                            await PrepareEmbeddingsAsync(payload, deployment, adapter, instance.Id, cancellation);
                        break;
                    case InferenceOperation.Images:
                        CheckImage(payload);
                        break;
                    case InferenceOperation.Speech:
                        CheckSpeech(payload);
                        break;
                    case InferenceOperation.SystemOne:
                        (promptTokens, promptCountSource) = CheckDecision(payload, deployment);
                        break;
                }
                if (operation != InferenceOperation.Chat)
                    adapter.Prepare(operation, payload);
            }
            catch (ArgumentException)
            {
                throw new ProviderFailure(400, "The inference body contains invalid or duplicate fields.");
            }
            outbound = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        }
        else
        {
            var multipart = new MultipartFormDataContent();
            outbound = multipart;
            foreach (var field in form!)
            {
                if (field.Key != "model")
                    foreach (var value in field.Value)
                        multipart.Add(new StringContent(value ?? ""), field.Key);
            }
            multipart.Add(new StringContent(instance.Id), "model");
            var file = form!.Files[0];
            var content = new StreamContent(file.OpenReadStream());
            if (MediaTypeHeaderValue.TryParse(file.ContentType, out var mediaType))
                content.Headers.ContentType = mediaType;
            multipart.Add(content, "file", Path.GetFileName(file.FileName));
        }

        await adapter.ConfirmDeploymentAsync(operation, deployment.Model, instance, cancellation);
        var send = Stopwatch.GetTimestamp();
        Submitted = true;
        using var response = await adapter.ExecuteAsync(operation, outbound, cancellation);
        var headers = Stopwatch.GetElapsedTime(send);
        if (!response.IsSuccessStatusCode)
            Settled = true;
        ProviderAdapter.RequireSuccess(response);
        var target = context.Response;
        target.StatusCode = (int)response.StatusCode;
        target.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        target.Headers.CacheControl = "no-store";
        TimeSpan? firstOutput = null;
        if (embeddingEncoding is not null)
        {
            var bytes = await ProviderAdapter.ReadBoundedAsync(response.Content, ProviderLimits.MaxRequestBytes, cancellation);
            Settled = true;
            CheckEmbeddingResult(bytes, deployment.EmbeddingDimensions!.Value, embeddingEncoding, embeddingCount);
            firstOutput = Stopwatch.GetElapsedTime(send);
            await target.Body.WriteAsync(bytes, cancellation);
        }
        else
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
            {
                firstOutput ??= Stopwatch.GetElapsedTime(send);
                await target.Body.WriteAsync(buffer.AsMemory(0, count), cancellation);
                await target.Body.FlushAsync(cancellation);
            }
            Settled = true;
        }
        return new(deployment.Id, instance.Fingerprint, operation, deployment.Options.Revision,
            promptTokens, promptCountSource, headers, firstOutput, Stopwatch.GetElapsedTime(started));
    }

    private static async Task<(int, string)> PrepareChatAsync(JsonObject payload, Deployment deployment,
        ProviderAdapter adapter, CancellationToken cancellation)
    {
        var limit = RequireContext(deployment);
        if (payload["messages"] is not JsonArray { Count: > 0 } messages)
            throw new ProviderFailure(400, "Chat requires a nonempty messages array.");
        if (payload["tools"] is { } tools &&
            (tools is not JsonArray toolList || toolList.Any(tool => tool is not JsonObject description ||
                String(description["type"]) != "function" || description["function"] is not JsonObject)))
            throw new ProviderFailure(400, "Chat tools must be client-executed function definitions.");
        if (payload["tool_choice"] is JsonObject choice &&
            (String(choice["type"]) != "function" || choice["function"] is not JsonObject))
            throw new ProviderFailure(400, "Chat tool choice must name a client-executed function.");
        var toolsRequested = payload["tools"] is JsonArray { Count: > 0 } || payload["tool_choice"] is JsonObject ||
            String(payload["tool_choice"]) == "required";
        var countEstimate = 256L + 16L * messages.Count;
        var mediaCount = 0;
        foreach (var element in messages)
        {
            if (element is not JsonObject message || String(message["role"]) is not ("user" or "assistant" or "system" or "developer" or "tool" or "function"))
                throw new ProviderFailure(400, "Each chat message requires a supported role.");
            RequireFields(message, MessageFields);
            toolsRequested |= message["tool_calls"] is not null || message["function_call"] is not null ||
                String(message["role"]) is "tool" or "function";
            foreach (var (key, value) in message)
                if (key != "content")
                    countEstimate += Size(value);
            if (message["content"] is not JsonArray parts)
            {
                countEstimate += Size(message["content"]);
                continue;
            }
            foreach (var elementPart in parts)
            {
                if (elementPart is not JsonObject part)
                    throw new ProviderFailure(400, "Chat content parts must be objects.");
                switch (String(part["type"]))
                {
                    case "text" when String(part["text"]) is not null:
                        RequireFields(part, ["type", "text"]);
                        countEstimate += Size(part["text"]) + 8;
                        break;
                    case "image_url" when part["image_url"] is JsonObject image:
                        RequireFields(part, ["type", "image_url"]);
                        RequireFields(image, ["url", "detail"]);
                        RequireInlineMedia(String(image["url"]), "image", deployment);
                        mediaCount++;
                        break;
                    case "input_audio" when part["input_audio"] is JsonObject audio:
                        RequireFields(part, ["type", "input_audio"]);
                        RequireFields(audio, ["data", "format"]);
                        if (!deployment.Model.Inputs.Contains("audio") || String(audio["format"]) is not ("wav" or "mp3") ||
                            String(audio["data"]) is not { Length: > 0 })
                            throw new ProviderFailure(422, "The deployment does not support this inline audio input.");
                        mediaCount++;
                        break;
                    default:
                        throw new ProviderFailure(422, "This chat content part is unsupported.");
                }
            }
        }
        if (toolsRequested && !deployment.Model.ClientTools)
            throw new ProviderFailure(422, "Client-executed function tools are unavailable on this deployment.");
        if (payload["response_format"] is JsonObject format && String(format["type"]) != "text" &&
            !deployment.Model.StructuredOutput)
            throw new ProviderFailure(422, "Structured chat output is unavailable on this deployment.");
        if (payload["n"] is { } choices && Positive(choices, "n") > 8)
            throw new ProviderFailure(400, "Chat supports at most eight choices per request.");
        var output = payload["max_completion_tokens"] ?? payload["max_tokens"];
        if (payload["max_completion_tokens"] is not null && payload["max_tokens"] is not null)
            throw new ProviderFailure(400, "Specify one chat output token limit.");
        var reserved = output is not null ? Positive(output, "maximum output tokens")
            : Math.Min(DefaultMaxOutput, Math.Max(1, limit / 4));
        if (output is null)
            payload["max_tokens"] = reserved;
        if (reserved >= limit)
            throw new ProviderFailure(422, "The reserved output tokens exceed the deployment's usable context.");
        adapter.Prepare(InferenceOperation.Chat, payload);
        var reported = mediaCount == 0 ? await adapter.CountChatAsync(payload, cancellation) : null;
        var estimate = countEstimate + Size(payload["tools"]) + Size(payload["response_format"]) + MediaBudget(mediaCount, deployment);
        return CheckFit(reported, estimate, reserved, limit);
    }

    private static async Task<(int, string, string, int)> PrepareEmbeddingsAsync(JsonObject payload, Deployment deployment,
        ProviderAdapter adapter, string nativeId, CancellationToken cancellation)
    {
        var context = RequireContext(deployment);
        var encoding = String(payload["encoding_format"]) ?? "float";
        if (!deployment.Model.EmbeddingEncodings.Contains(encoding))
            throw new ProviderFailure(422, "The deployment does not support this embedding encoding.");
        if (deployment.EmbeddingDimensions is not { } dimensions ||
            payload["dimensions"] is { } requested && Positive(requested, "dimensions") != dimensions)
            throw new ProviderFailure(422, "The requested embedding dimensions do not match the deployment.");
        var input = payload["input"] ?? throw new ProviderFailure(400, "Embeddings require input.");
        JsonNode[] rows;
        if (String(input) is not null || input is JsonArray { Count: > 0 } tokens && tokens.All(IsToken))
            rows = [input];
        else if (input is JsonArray { Count: > 0 and <= MaxBatch } batch &&
                 (batch.All(item => String(item) is not null) || batch.All(item => item is JsonArray { Count: > 0 } sequence && sequence.All(IsToken))))
            rows = batch.Select(item => item!).ToArray();
        else
            throw new ProviderFailure(400, "Supply text, token IDs, or a bounded batch of one input type.");
        var largest = 0;
        var allReported = true;
        foreach (var row in rows)
        {
            var reported = row is JsonArray sequence ? sequence.Count + 4 :
                await adapter.CountEmbeddingAsync(row, nativeId, cancellation);
            var (count, source) = CheckFit(reported, Size(row) + 32, 0, context);
            allReported &= source == "provider";
            largest = Math.Max(count, largest);
        }
        return (largest, allReported ? "provider" : "conservative estimate", encoding, rows.Length);
    }

    private static void CheckEmbeddingResult(byte[] bytes, int dimensions, string encoding, int count)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var data = document.RootElement.Get("data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != count)
                throw new ProviderFailure(502, "The provider returned an unexpected embedding batch shape.");
            var indexes = new HashSet<int>();
            foreach (var row in data.Items())
            {
                var index = row.Get("index");
                if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var value) ||
                    value < 0 || value >= count || !indexes.Add(value))
                    throw new ProviderFailure(502, "The provider returned invalid embedding indexes.");
                var embedding = row.Get("embedding");
                var matches = encoding == "float"
                    ? embedding.ValueKind == JsonValueKind.Array && embedding.GetArrayLength() == dimensions &&
                      embedding.Items().All(item => item.ValueKind == JsonValueKind.Number)
                    : embedding.ValueKind == JsonValueKind.String &&
                      Convert.TryFromBase64String(embedding.GetString()!, new byte[dimensions * sizeof(float)], out var size) &&
                      size == dimensions * sizeof(float);
                if (!matches)
                    throw new ProviderFailure(502, "The provider returned incompatible embedding dimensions or encoding.");
            }
        }
        catch (JsonException)
        {
            throw new ProviderFailure(502, "The provider returned invalid embedding data.");
        }
    }

    private static void CheckImage(JsonObject payload)
    {
        if (String(payload["prompt"]) is not { Length: > 0 })
            throw new ProviderFailure(400, "Image generation requires a text prompt.");
        if (payload["response_format"] is { } format && String(format) != "b64_json")
            throw new ProviderFailure(422, "Mango supports synchronous inline image results.");
        if (payload["stream"] is { } stream && (stream is not JsonValue flag || !flag.TryGetValue<bool>(out var streaming) || streaming))
            throw new ProviderFailure(422, "The image generation contract returns one synchronous result.");
        payload["response_format"] ??= "b64_json";
        if (payload["n"] is { } count && Positive(count, "n") > 8)
            throw new ProviderFailure(400, "An image request can generate up to eight images.");
        var width = payload["width"] is { } w ? Positive(w, "width") : 0;
        var height = payload["height"] is { } h ? Positive(h, "height") : 0;
        if (String(payload["size"]) is { } size && size != "auto")
        {
            var parts = size.Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height) ||
                width < 1 || height < 1 || payload["width"] is not null || payload["height"] is not null)
                throw new ProviderFailure(400, "Specify one valid image size.");
        }
        if (width > 4096 || height > 4096 || (long)width * height > MaxImagePixels)
            throw new ProviderFailure(422, "The image size exceeds the supported bounds.");
    }

    private static void CheckSpeech(JsonObject payload)
    {
        if (String(payload["input"]) is not { Length: > 0 and <= MaxSpeechCharacters })
            throw new ProviderFailure(400, "Speech input must contain between 1 and 4096 characters.");
        if (payload["voice"] is not null &&
            (String(payload["voice"]) is not { Length: > 0 and <= 128 } voice ||
            voice.Any(character => !char.IsLetterOrDigit(character) && character is not (' ' or '.' or '_' or '-'))))
            throw new ProviderFailure(400, "Choose a provider voice name.");
    }

    private static (int, string) CheckDecision(JsonObject payload, Deployment deployment)
    {
        if (!payload.ContainsKey("state") || payload["questions"] is not JsonObject { Count: > 0 } questions ||
            questions.Any(pair => pair.Value is not JsonObject question ||
                String(question["type"]) is not ("noul" or "choice" or "score")))
            throw new ProviderFailure(400, "System One requires state and named noul, choice, or score questions.");
        var media = 0;
        foreach (var key in new[] { "files", "images" })
        {
            if (payload[key] is null)
                continue;
            if (payload[key] is not JsonArray files)
                throw new ProviderFailure(400, "Decision media must be an array of inline files.");
            foreach (var file in files)
            {
                var value = String(file);
                RequireInlineMedia(value, value?.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase) == true
                    ? "audio" : "image", deployment);
                media++;
            }
        }
        return CheckFit(null, Size(payload["state"]) + Size(questions) + 128 + MediaBudget(media, deployment),
            128, RequireContext(deployment));
    }

    private static void RequireInlineMedia(string? value, string modality, Deployment deployment)
    {
        if (!deployment.Model.Inputs.Contains(modality) ||
            value?.StartsWith($"data:{modality}/", StringComparison.OrdinalIgnoreCase) != true ||
            value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) is < 1 or > 80)
            throw new ProviderFailure(422, "Use inline base64 media supported by the selected deployment.");
    }

    private static long MediaBudget(int count, Deployment deployment) => count == 0 ? 0
        : (long)count * (deployment.Options.MediaTokensPerItem ??
            throw new ProviderFailure(422, "Configure a conservative per-item media token limit for this deployment."));

    private static (int, string) CheckFit(int? reported, long estimate, int reserved, int context)
    {
        var count = reported ?? estimate;
        if (count + reserved > context)
            throw new ProviderFailure(422, "The input plus reserved output does not fit the deployment's served context.");
        return ((int)count, reported.HasValue ? "provider" : "conservative estimate");
    }

    private static int RequireContext(Deployment deployment) => deployment.Context ??
        throw new ProviderFailure(422, "Configure the deployment's served context before using this operation.");

    private static long Size(JsonNode? node) => node is null ? 0
        : Encoding.UTF8.GetByteCount(String(node) ?? node.ToJsonString());

    private static int Positive(JsonNode node, string name) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) && number > 0 ? number
            : throw new ProviderFailure(400, $"Enter a positive integer for {name}.");

    private static bool IsToken(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) && number >= 0;

    private static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string RequireAlias(string? value) => value is { Length: > 0 and <= 200 } ? value
        : throw new ProviderFailure(400, "Specify a Mango model alias.");

    private static void RequireFields(JsonObject payload, string[] allowed)
    {
        if (payload.Any(field => !allowed.Contains(field.Key, StringComparer.Ordinal)))
            throw new ProviderFailure(400, "The inference request contains an unsupported field.");
    }

    public void Dispose() => outbound?.Dispose();
}
