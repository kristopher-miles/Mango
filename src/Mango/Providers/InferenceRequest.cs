using System.Diagnostics;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mango.Peers;
using Mango.Logging;
using Mango.Resources;
using Microsoft.AspNetCore.Http.Features;

namespace Mango.Providers;

internal sealed record ProviderExecution(string DeploymentId, string InstanceFingerprint, InferenceOperation Operation,
    long AliasRevision, int? PromptTokens, string? PromptCountSource, TimeSpan ProviderHeaders,
    TimeSpan? FirstOutput, TimeSpan Elapsed, TimeSpan? Preparation = null, TimeSpan? FirstToken = null,
    int? ContextTokens = null, int? OutputTokens = null, double? TokensPerSecond = null, long? OutputBytes = null,
    double? AudioSeconds = null);

internal sealed record InferenceShape(string Alias, InferenceOperation Operation, string[] Inputs, bool Tools,
    bool Structured, long PromptUnits, int OutputUnits, int MediaItems, int Items, long WorkUnits,
    int? Dimensions, string? Encoding, bool CanCountPrompt = false)
{
    public Workload Demand(int? context, long? qualifiedUnits = null) => new(Operation,
        Operation is InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne
            ? context ?? qualifiedUnits ?? ProviderLimits.MaxContext : WorkUnits, Items, MediaItems);

    public bool Fits(string[] inputs, bool tools, bool structured, int? context, int? mediaTokens,
        string[]? encodings, bool allowUnverifiedContext = false)
    {
        if (!Inputs.All(inputs.Contains) || Tools && !tools || Structured && !structured)
            return false;
        if (Operation is not (InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne))
            return true;
        // Context and memory are reserved on the serving host. Estimated prompt
        // syntax/length is not a public routing veto; the provider validates input.
        return allowUnverifiedContext || context is > 0 && (MediaItems == 0 || mediaTokens is > 0);
    }

    public bool FitsEstimatedContext(long? context, int? mediaTokens)
    {
        if (Operation is not (InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne))
            return true;
        return context is > 0 && (MediaItems == 0 || mediaTokens is > 0) &&
            PromptUnits + (long)MediaItems * mediaTokens.GetValueOrDefault() + OutputUnits <= context;
    }

    public bool Valid => ProviderIdentity.ValidIdentifier(Alias) && Enum.IsDefined(Operation) &&
        Inputs is { Length: <= 3 } && Inputs.All(input => input is "text" or "image" or "audio") &&
        PromptUnits is >= 0 and <= 128L * ProviderLimits.MaxRequestBytes && OutputUnits >= 0 &&
        MediaItems is >= 0 and <= ProviderLimits.MaxRequestBytes && Items is >= 1 and <= ProviderLimits.MaxRequestBytes &&
        WorkUnits is >= 1 and <= ProviderLimits.MaxRequestBytes && Encoding is null or "float" or "base64" &&
        Dimensions is null or > 0;
}

internal sealed class InferenceRequest(InferenceOperation operation, string alias, JsonObject? json, IFormCollection? form)
    : IDisposable
{
    private const int DefaultMaxOutput = 1024;
    private const int MaxBatch = 128;
    private const int MaxImagePixels = 4096 * 4096;
    private const int MaxSpeechCharacters = 4096;
    private static readonly JsonSerializerOptions WireJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private HttpContent? outbound;
    public string Alias { get; } = alias;
    public InferenceOperation Operation => operation;
    public bool Submitted { get; private set; }
    public bool Settled { get; private set; }
    public long Bytes { get; private set; }
    public InferenceShape Shape { get; private set; } = null!;

    public string[] Prefixes()
    {
        if (operation != InferenceOperation.Chat || Bytes > 1024 * 1024 || json?["messages"] is not JsonArray messages)
            return [];
        var prefix = ProviderIdentity.Hash(json["tools"]?.ToJsonString(), json["tool_choice"]?.ToJsonString(),
            json["response_format"]?.ToJsonString(), json["reasoning_effort"]?.ToJsonString(),
            json["chat_template_kwargs"]?.ToJsonString());
        var hashes = new List<string>();
        foreach (var message in messages.Take(32))
        {
            prefix = ProviderIdentity.Hash(prefix, message?.ToJsonString());
            if (String(message?["role"]) == "user")
                hashes.Add(prefix);
        }
        return hashes.ToArray();
    }

    private static Workload ImageDemand(JsonObject payload)
    {
        var width = BoundedInt(payload["width"], 4096, 4096);
        var height = BoundedInt(payload["height"], 4096, 4096);
        if (String(payload["size"])?.Split('x') is [var wide, var high] && int.TryParse(wide, out var w) &&
            int.TryParse(high, out var h) && w is > 0 and <= 4096 && h is > 0 and <= 4096)
            (width, height) = (w, h);
        return new(InferenceOperation.Images, (long)width * height, BoundedInt(payload["n"], 1, 10));
    }

    private static int BoundedInt(JsonNode? value, int fallback, int max) =>
        value is JsonValue item && item.TryGetValue<int>(out var number) && number is > 0 && number <= max ? number : fallback;

    private static readonly string[] ChatFields = ("model messages stream stream_options max_tokens max_completion_tokens " +
        "temperature top_p top_k min_p typical_p frequency_penalty presence_penalty repetition_penalty repeat_penalty " +
        "stop seed n logprobs top_logprobs logit_bias response_format tools tool_choice parallel_tool_calls " +
        "reasoning_effort chat_template_kwargs user").Split(' ');
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
                var audio = new InferenceRequest(operation, RequireIdentifier(form["model"].ToString()), null, form);
                audio.SetShape(request.ContentLength ?? form.Files[0].Length + form.Sum(entry => entry.Value.ToString().Length * 4L) + 4096);
                return audio;
            }
            if (!request.HasJsonContentType())
                throw new ProviderFailure(415, "This operation requires an application/json request body.");
            var payload = await JsonNode.ParseAsync(request.Body, cancellationToken: cancellation) as JsonObject
                ?? throw new ProviderFailure(400, "Send a JSON object.");
            LogCapture.Of(request.HttpContext)?.ExcludeCredentialFields(payload);
            if (operation is not (InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne))
                RequireFields(payload, operation switch
            {
                InferenceOperation.Chat => ChatFields,
                InferenceOperation.Embeddings => EmbeddingFields,
                InferenceOperation.Images => ImageFields,
                InferenceOperation.Speech => SpeechFields,
                _ => DecisionFields
            });
            RequireMediaScope(request.HttpContext, operation, payload);
            var result = new InferenceRequest(operation, RequireIdentifier(String(payload["model"])), payload, null);
            result.SetShape(request.ContentLength ?? Encoding.UTF8.GetByteCount(payload.ToJsonString(WireJson)));
            return result;
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

    private void SetShape(long bytes)
    {
        Bytes = Math.Max(1, bytes);
        if (operation == InferenceOperation.Images)
            CheckImage(json!);
        if (operation == InferenceOperation.Speech)
            CheckSpeech(json!);
        var rows = operation == InferenceOperation.Embeddings ? EmbeddingRows(json!) : [];
        var image = operation == InferenceOperation.Images ? ImageDemand(json!) : default;
        Shape = operation switch
        {
            InferenceOperation.Chat => ChatShape(json!, Alias),
            InferenceOperation.Embeddings => new(Alias, operation, ["text"], false, false,
                rows.Max(row => row is JsonArray sequence ? sequence.Count + 4 : Size(row) + 32),
                0, 0, rows.Length, 1,
                OptionalPositive(json!["dimensions"]), String(json["encoding_format"]) is "base64" ? "base64" :
                    json["encoding_format"] is null || String(json["encoding_format"]) == "float" ? "float" : null,
                CanCountPrompt: rows[0] is not JsonArray),
            InferenceOperation.SystemOne => DecisionShape(json!, Alias),
            InferenceOperation.Images => new(Alias, operation, ["text"], false, false, 0, 0, 0,
                image.Items, image.Units, null, null),
            InferenceOperation.Transcription => new(Alias, operation, ["audio"], false, false, 0, 0, 0,
                1, form!.Files[0].Length, null, null),
            InferenceOperation.Speech => new(Alias, operation, ["text"], false, false, 0, 0, 0,
                1, Math.Max(1, String(json!["input"])?.Length ?? 0), null, null),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    public HttpContent CreateBody() => json is { } payload
        ? JsonContent.Create(payload, options: WireJson) : CreateForm(Alias);

    private MultipartFormDataContent CreateForm(string model)
    {
        var multipart = new MultipartFormDataContent();
        foreach (var field in form!)
            if (field.Key != "model")
                foreach (var value in field.Value)
                    multipart.Add(new StringContent(value ?? ""), field.Key);
        multipart.Add(new StringContent(model), "model");
        var file = form!.Files[0];
        var content = new StreamContent(file.OpenReadStream());
        if (MediaTypeHeaderValue.TryParse(file.ContentType, out var mediaType))
            content.Headers.ContentType = mediaType;
        multipart.Add(content, "file", Path.GetFileName(file.FileName));
        return multipart;
    }

    private static void RequireMediaScope(HttpContext context, InferenceOperation operation, JsonObject payload)
    {
        if (InferenceAccess.Of(context) is not { KeyId: not null } access)
            return;
        if (operation == InferenceOperation.Chat && payload["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
                if (message["content"] is JsonArray parts)
                    foreach (var part in parts.OfType<JsonObject>())
                        Require(String(part["type"]) switch
                        {
                            "image_url" or "input_image" => InferenceScope.ImageInput,
                            "input_audio" => InferenceScope.AudioInput,
                            _ => null
                        });
        }
        if (operation == InferenceOperation.SystemOne)
        {
            foreach (var field in new[] { "files", "images" })
                if (payload[field] is JsonArray files)
                    foreach (var file in files)
                        Require(String(file)?.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase) == true
                            ? InferenceScope.AudioInput : InferenceScope.ImageInput);
        }

        void Require(InferenceScope? scope)
        {
            if (scope.HasValue && !access.Allows(scope.Value))
                throw new ProviderFailure(403, "The inference credential does not allow this media input.");
        }
    }

    public async Task<ProviderExecution> ForwardAsync(Deployment deployment, ProviderAdapter adapter,
        HttpContext context, CancellationToken cancellation)
    {
        // A pre-submission retry replaces the body prepared for the retired instance.
        outbound?.Dispose();
        outbound = null;
        if (!deployment.Model.Operations.Contains(operation))
            throw new ProviderFailure(422, "This deployment does not support the requested inference operation.");
        var instance = deployment.Instance ?? throw new ProviderFailure(409, "The selected deployment is not loaded.");
        int? promptTokens = null;
        string? promptCountSource = null;
        string? embeddingEncoding = null;
        var embeddingCount = 0;
        var started = Stopwatch.GetTimestamp();
        if (json?.DeepClone() is JsonObject payload)
        {
            payload["model"] = instance.Id;
            try
            {
                if (!deployment.Yolo) switch (operation)
                {
                    case InferenceOperation.Chat:
                        (promptTokens, promptCountSource) = await PrepareChatAsync(payload, deployment, adapter, cancellation);
                        break;
                    case InferenceOperation.Embeddings:
                        (promptTokens, promptCountSource, embeddingEncoding, embeddingCount) =
                            await PrepareEmbeddingsAsync(payload, deployment, adapter, instance.Id, cancellation);
                        break;
                    case InferenceOperation.SystemOne:
                        (promptTokens, promptCountSource) = CheckDecision(deployment);
                        break;
                }
                if (!deployment.Yolo && operation != InferenceOperation.Chat)
                    adapter.Prepare(operation, payload);
            }
            catch (ArgumentException)
            {
                throw new ProviderFailure(400, "The inference body contains invalid or duplicate fields.");
            }
            outbound = JsonContent.Create(payload, options: WireJson);
        }
        else
            outbound = CreateForm(instance.Id);

        await adapter.ConfirmDeploymentAsync(operation, deployment.Model, instance, cancellation);
        var send = Stopwatch.GetTimestamp();
        Submitted = true;
        using var response = await adapter.ExecuteAsync(operation, outbound, cancellation);
        var headers = Stopwatch.GetElapsedTime(send);
        if (!response.IsSuccessStatusCode)
            Settled = true;
        if (!deployment.Yolo)
            await ProviderAdapter.RequireInferenceSuccessAsync(response, cancellation, operation == InferenceOperation.Chat);
        var eventStream = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream",
            StringComparison.OrdinalIgnoreCase);
        if (!deployment.Yolo && operation == InferenceOperation.Chat && json?["stream"] is JsonValue streamFlag &&
            streamFlag.TryGetValue<bool>(out var streaming) && streaming && !eventStream)
            throw new ProviderFailure(502, "The provider did not return an event stream for the streaming chat request.");
        var target = context.Response;
        target.StatusCode = (int)response.StatusCode;
        target.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        target.Headers.CacheControl = "no-store";
        var output = new InferenceOutput(context, operation, Alias);
        TimeSpan? firstOutput = null;
        var statistics = context.Items.ContainsKey(typeof(LogConfiguration)) ? new OutputMetrics(operation, send,
            response.Content.Headers.ContentType?.MediaType) : null;
        if (embeddingEncoding is not null)
        {
            var bytes = await ProviderAdapter.ReadBoundedAsync(response.Content, ProviderLimits.MaxRequestBytes, cancellation);
            Settled = true;
            CheckEmbeddingResult(bytes, embeddingEncoding, embeddingCount);
            firstOutput = Stopwatch.GetElapsedTime(send);
            statistics?.Add(bytes);
            for (var offset = 0; offset < bytes.Length; offset += 16 * 1024)
                await output.WriteAsync(bytes.AsMemory(offset, Math.Min(16 * 1024, bytes.Length - offset)), cancellation);
        }
        else
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
            var buffer = new byte[16 * 1024];
            var events = operation == InferenceOperation.Chat && eventStream ? new InferenceEvents(statistics, !deployment.Yolo) : null;
            int count;
            while ((count = await stream.ReadAsync(buffer, cancellation)) != 0)
            {
                firstOutput ??= Stopwatch.GetElapsedTime(send);
                statistics?.Add(buffer.AsSpan(0, count));
                events?.Read(buffer.AsSpan(0, count));
                await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            }
            events?.FinishLine();
            Settled = events?.Completed ?? true;
            if (!Settled)
                throw new ProviderFailure(502, "The provider stream ended before reporting completion.");
        }
        await output.CompleteAsync(cancellation);
        var providerElapsed = Stopwatch.GetElapsedTime(send);
        statistics?.Complete(providerElapsed);
        double? inputAudio = null;
        if (statistics is not null && operation == InferenceOperation.Transcription)
        {
            try
            {
                using var audio = form!.Files[0].OpenReadStream();
                var header = new byte[4096];
                var read = await audio.ReadAtLeastAsync(header, 44, false, cancellation);
                inputAudio = WavSeconds(header.AsSpan(0, read), form.Files[0].Length);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
        return new(deployment.Id, instance.Fingerprint, operation, deployment.Options.Revision,
            statistics?.PromptTokens ?? promptTokens, statistics?.PromptTokens is not null ? "provider usage" : promptCountSource,
            headers, firstOutput, Stopwatch.GetElapsedTime(started), Stopwatch.GetElapsedTime(started, send), statistics?.FirstToken,
            deployment.Context, statistics?.OutputTokens, statistics?.Rate, statistics?.Bytes, inputAudio ?? statistics?.AudioSeconds);
    }

    internal static async Task WriteOutputAsync(HttpContext context, ReadOnlyMemory<byte> bytes, CancellationToken cancellation)
    {
        using var progress = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        progress.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await context.Response.Body.WriteAsync(bytes, progress.Token);
            await context.Response.Body.FlushAsync(progress.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            context.Abort();
            throw;
        }
    }

    private sealed class InferenceEvents(OutputMetrics? statistics, bool interpretErrors = true)
    {
        private readonly byte[] line = new byte[statistics is null ? 4096 : 16 * 1024];
        private int length;
        public bool Completed { get; private set; }

        public void Read(ReadOnlySpan<byte> bytes)
        {
            foreach (var value in bytes)
            {
                if (value == (byte)'\n')
                    FinishLine();
                else if (length < line.Length)
                    line[length++] = value;
            }
        }

        public void FinishLine()
        {
            var row = line.AsSpan(0, length);
            length = 0;
            if (!row.StartsWith("data:"u8))
                return;
            var start = row.Length > 5 && row[5] == (byte)' ' ? 6 : 5;
            var data = row[start..];
            if (!data.IsEmpty && data[^1] == (byte)'\r')
                data = data[..^1];
            if (data.SequenceEqual("[DONE]"u8))
                Completed = true;
            var hasError = data.IndexOf("\"error\""u8) >= 0;
            if (!hasError && (statistics is null || statistics.FirstToken is not null &&
                data.IndexOf("\"usage\""u8) < 0 && data.IndexOf("\"timings\""u8) < 0))
                return;
            try
            {
                using var document = JsonDocument.Parse(line.AsMemory(start, data.Length));
                statistics?.Observe(document.RootElement, streaming: true);
                var error = document.RootElement.Get("error");
                if (interpretErrors && error.ValueKind != JsonValueKind.Undefined && ProviderAdapter.IsAllocationFailure(error.GetRawText()))
                    throw new ProviderFailure(503, "The provider could not allocate model memory.", allocationFailure: true);
            }
            catch (JsonException) { }
        }
    }

    private sealed class OutputMetrics(InferenceOperation operation, long sent, string? mediaType)
    {
        private readonly byte[]? sample = mediaType == "application/json" &&
            operation is InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne ? new byte[64 * 1024] :
            operation == InferenceOperation.Speech ? new byte[4096] : null;
        private int length;
        public long Bytes { get; private set; }
        public int? PromptTokens { get; private set; }
        public int? OutputTokens { get; private set; }
        public TimeSpan? FirstToken { get; private set; }
        public double? Rate { get; private set; }
        public double? AudioSeconds { get; private set; }

        public void Add(ReadOnlySpan<byte> data)
        {
            Bytes += data.Length;
            if (sample is null || length == sample.Length)
                return;
            var copied = Math.Min(data.Length, sample.Length - length);
            data[..copied].CopyTo(sample.AsSpan(length));
            length += copied;
        }

        public void Observe(JsonElement body, bool streaming)
        {
            (PromptTokens, OutputTokens, _, Rate, _) = BenchmarkProtocol.ReadMetrics(body, (PromptTokens, OutputTokens, null, Rate, null));
            if (streaming && FirstToken is null && body.Get("choices").Items().Any(choice =>
                choice.Get("delta").Text("content") is { Length: > 0 } ||
                choice.Get("delta").Text("reasoning_content") is { Length: > 0 } ||
                choice.Get("delta").Get("tool_calls").Items().Any()))
                FirstToken = Stopwatch.GetElapsedTime(sent);
        }

        public void Complete(TimeSpan elapsed)
        {
            if (sample is not null && mediaType == "application/json" && length == Bytes)
            {
                try
                {
                    using var document = JsonDocument.Parse(sample.AsMemory(0, length));
                    Observe(document.RootElement, streaming: false);
                }
                catch (JsonException) { }
            }
            else if (sample is not null && operation == InferenceOperation.Speech)
                AudioSeconds = WavSeconds(sample.AsSpan(0, length), Bytes);
            if (Rate is null && OutputTokens is > 1 && FirstToken is { } first && elapsed > first)
                Rate = (OutputTokens - 1) / (elapsed - first).TotalSeconds;
        }
    }

    private static double? WavSeconds(ReadOnlySpan<byte> header, long length)
    {
        if (header.Length < 12 || !header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
            return null;
        uint rate = 0;
        for (var position = 12; position + 8 <= header.Length;)
        {
            var id = header.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(position + 4, 4));
            var start = position + 8;
            if (id.SequenceEqual("fmt "u8) && size >= 16 && start + 16 <= header.Length)
                rate = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(start + 8, 4));
            if (id.SequenceEqual("data"u8))
                return rate == 0 || start + (long)size > length ? null : (double)size / rate;
            var next = start + (long)size + (size & 1);
            if (next > header.Length)
                break;
            position = (int)next;
        }
        return null;
    }

    private static int? OptionalPositive(JsonNode? value) =>
        value is JsonValue number && number.TryGetValue<int>(out var result) && result > 0 ? result : null;

    // Extract routing and accounting hints; provider input schema remains opaque.
    private static InferenceShape ChatShape(JsonObject payload, string alias)
    {
        var messages = payload["messages"] as JsonArray;
        var prompt = 256L;
        var media = 0;
        var inputs = new HashSet<string>(StringComparer.Ordinal) { "text" };
        var tools = payload["tools"] is not null && payload["tools"] is not JsonArray { Count: 0 } ||
            payload["tool_choice"] is JsonObject || String(payload["tool_choice"]) == "required";
        foreach (var element in messages ?? [])
        {
            if (element is not JsonObject message) { prompt += Size(element); continue; }
            prompt += 16;
            tools |= message["tool_calls"] is not null || message["function_call"] is not null ||
                String(message["role"]) is "tool" or "function";
            foreach (var field in message.Where(field => field.Key != "content")) prompt += Size(field.Value);
            if (message["content"] is not JsonArray parts) { prompt += Size(message["content"]); continue; }
            foreach (var part in parts)
            {
                var kind = part is JsonObject item ? String(item["type"]) : null;
                if (kind is "image_url" or "input_image") { inputs.Add("image"); media++; }
                else if (kind is "input_audio") { inputs.Add("audio"); media++; }
                else prompt += part is JsonObject text && String(text["type"]) == "text" ? Size(text["text"]) + 8 : Size(part);
            }
        }
        return new(alias, InferenceOperation.Chat, inputs.ToArray(), tools,
            payload["response_format"] is JsonObject format && String(format["type"]) != "text",
            prompt + Size(payload["tools"]) + Size(payload["response_format"]),
            OptionalPositive(payload["max_completion_tokens"]) ?? OptionalPositive(payload["max_tokens"]) ?? 0,
            media, OptionalPositive(payload["n"]) ?? 1, 1, null, null, CanCountPrompt: media == 0);
    }

    public long EstimatedChatContext(Deployment deployment) => Shape.PromptUnits + OutputReserve(RequireContext(deployment)) +
        MediaBudget(Shape.MediaItems, deployment);

    private int OutputReserve(int context) => Shape.OutputUnits != 0 ? Shape.OutputUnits :
        Math.Min(DefaultMaxOutput, Math.Max(1, context / 4));

    private async Task<(int, string)> PrepareChatAsync(JsonObject payload, Deployment deployment,
        ProviderAdapter adapter, CancellationToken cancellation)
    {
        var limit = RequireContext(deployment);
        if (Shape.Tools && !deployment.Model.ClientTools)
            throw new ProviderFailure(422, "Client-executed function tools are unavailable on this deployment.");
        if (Shape.Structured && !deployment.Model.StructuredOutput)
            throw new ProviderFailure(422, "Structured chat output is unavailable on this deployment.");
        if (!Shape.Inputs.All(deployment.Model.Inputs.Contains))
            throw new ProviderFailure(422, "This deployment does not support the requested chat inputs.");
        if (adapter.TransparentInference)
            return ((int)Math.Min(int.MaxValue, Shape.PromptUnits + MediaBudget(Shape.MediaItems, deployment)), "routing estimate");
        var reserved = OutputReserve(limit);
        if (reserved >= limit)
            throw new ProviderFailure(422, "The reserved output tokens exceed the deployment's usable context.");
        adapter.Prepare(InferenceOperation.Chat, payload);
        var reported = Shape.MediaItems == 0 ? await adapter.CountChatAsync(payload, cancellation) : null;
        var estimate = Shape.PromptUnits + MediaBudget(Shape.MediaItems, deployment);
        if (reported is null && adapter.ValidatesChatContext)
            return ((int)Math.Min(int.MaxValue, estimate), "routing estimate");
        return CheckFit(reported, estimate, reserved, limit);
    }

    private static Task<(int, string, string?, int)> PrepareEmbeddingsAsync(JsonObject payload, Deployment deployment,
        ProviderAdapter adapter, string nativeId, CancellationToken cancellation)
    {
        var encoding = String(payload["encoding_format"]) ?? "float";
        var rows = EmbeddingRows(payload);
        var largest = rows.Max(row => (int)Math.Min(int.MaxValue, Size(row) + 32));
        return Task.FromResult<(int, string, string?, int)>((largest, "routing estimate",
            encoding is "float" or "base64" ? encoding : null, rows.Length));
    }

    private static JsonNode[] EmbeddingRows(JsonObject payload)
    {
        var input = payload["input"] ?? JsonValue.Create("")!;
        if (String(input) is not null || input is JsonArray { Count: > 0 } tokens && tokens.All(IsToken))
            return [input];
        if (input is JsonArray { Count: > 0 } batch)
            return batch.Select(item => item ?? JsonValue.Create("")!).ToArray();
        return [input];
    }

    private static void CheckEmbeddingResult(byte[] bytes, string encoding, int count)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var data = document.RootElement.Get("data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != count)
                throw new ProviderFailure(502, "The provider returned an unexpected embedding batch shape.");
            var indexes = new HashSet<int>();
            int? vectorLength = null;
            foreach (var row in data.Items())
            {
                var index = row.Get("index");
                if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out var value) ||
                    value < 0 || value >= count || !indexes.Add(value))
                    throw new ProviderFailure(502, "The provider returned invalid embedding indexes.");
                var embedding = row.Get("embedding");
                var length = EmbeddingLength(embedding, encoding);
                if (length == 0 || vectorLength is { } first && length != first)
                    throw new ProviderFailure(502, "The provider returned invalid or inconsistent embedding vectors.");
                vectorLength = length;
            }
        }
        catch (JsonException)
        {
            throw new ProviderFailure(502, "The provider returned invalid embedding data.");
        }
    }

    private static int EmbeddingLength(JsonElement embedding, string encoding)
    {
        if (encoding == "float")
            return embedding.ValueKind == JsonValueKind.Array &&
                embedding.EnumerateArray().All(item => item.ValueKind == JsonValueKind.Number)
                ? embedding.GetArrayLength() : 0;
        return embedding.ValueKind == JsonValueKind.String &&
            Base64.IsValid(embedding.GetString(), out var bytes) && bytes % sizeof(float) == 0
            ? bytes / sizeof(float) : 0;
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

    private (int, string) CheckDecision(Deployment deployment)
    {
        if (!Shape.Inputs.All(deployment.Model.Inputs.Contains))
            throw new ProviderFailure(422, "This deployment does not support the requested decision inputs.");
        return ((int)Math.Min(int.MaxValue, Shape.PromptUnits + MediaBudget(Shape.MediaItems, deployment)), "routing estimate");
    }

    private static InferenceShape DecisionShape(JsonObject payload, string alias)
    {
        var media = 0;
        var inputs = new HashSet<string>(StringComparer.Ordinal) { "text" };
        foreach (var key in new[] { "files", "images" })
            if (payload[key] is JsonArray files)
                foreach (var file in files)
                {
                    inputs.Add(String(file)?.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase) == true ? "audio" : "image");
                    media++;
                }
        return new(alias, InferenceOperation.SystemOne, inputs.ToArray(), false, false,
            Size(payload["state"]) + Size(payload["questions"]) + 128, 128, media, 1, 1, null, null);
    }
    private static void RequireInlineMedia(string? value, string modality)
    {
        if (value?.StartsWith($"data:{modality}/", StringComparison.OrdinalIgnoreCase) != true ||
            value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase) is < 1 or > 80)
            throw new ProviderFailure(422, "Use inline base64 media supported by the selected deployment.");
    }

    private static long MediaBudget(int count, Deployment deployment) => count == 0 ? 0
        : (long)count * (deployment.MediaTokensPerItem ??
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

    private static string RequireIdentifier(string? value) => ProviderIdentity.PublicIdentifier(value) ? value!
        : throw new ProviderFailure(400, "Specify a model identifier or a user-set alias.");

    private static void RequireFields(JsonObject payload, string[] allowed)
    {
        if (payload.Any(field => !allowed.Contains(field.Key, StringComparer.Ordinal)))
            throw new ProviderFailure(400, "The inference request contains an unsupported field.");
    }

    public void Dispose() => outbound?.Dispose();
}
