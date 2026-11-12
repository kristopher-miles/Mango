using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mango.Providers;

namespace Mango.Resources;

internal sealed record BenchmarkResult(TimeSpan Elapsed, int? PromptTokens, int? OutputTokens,
    TimeSpan? FirstToken, double? PromptMilliseconds, double? TokensPerSecond, int? CachedTokens, bool? ReusedCache);

internal static class BenchmarkProtocol
{
    private const int Output = 16;

    public static (string Name, Workload Workload)[] Plan(Deployment model, InferenceOperation operation, Workload limit)
    {
        if (operation is InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne)
        {
            var context = model.Context ?? throw new ProviderFailure(409, "Set a served context before benchmarking.");
            if (context > limit.Units)
                throw new ProviderFailure(409, "The local workload allowance must cover the full served context.");
            var full = new Workload(operation, context, limit.Items);
            return [("short", full with { Units = Math.Min(256, context) }), ("half", full with { Units = context / 2 }),
                ("full", full), ("reuse", full)];
        }
        if (operation == InferenceOperation.Images)
        {
            var width = Math.Clamp((int)Math.Sqrt(limit.Units), 1, 4096);
            var height = (int)Math.Clamp(limit.Units / width, 1, 4096);
            limit = limit with { Units = (long)width * height, Width = width, Height = height };
        }
        if (operation == InferenceOperation.Transcription)
        {
            if (limit.Units < 46)
                throw new ProviderFailure(409, "The benchmark needs room for a synthetic PCM audio file.");
            limit = limit with { Units = limit.Units & ~1L };
        }
        return [("warm", limit)];
    }

    public static async Task<BenchmarkResult> RunAsync(Deployment model, ProviderAdapter adapter, Workload workload,
        string pass, Action submitted, Action settled, CancellationToken cancellation)
    {
        var instance = model.Instance!;
        var operation = workload.Operation;
        int? promptTokens = null;
        int? outputTokens = null;
        TimeSpan? firstToken = null;
        double? promptMilliseconds = null;
        double? tokensPerSecond = null;
        int? cachedTokens = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(ProviderLimits.LifecycleTimeout);
        var token = timeout.Token;
        try
        {
            using HttpContent content = operation == InferenceOperation.Transcription ? Audio(instance.Id, workload)
                : JsonContent.Create(await PayloadAsync(model, adapter, workload, pass, token));
            await adapter.ConfirmDeploymentAsync(operation, model.Model, instance, token);
            var started = Stopwatch.GetTimestamp();
            submitted();
            using var response = await adapter.ExecuteAsync(operation, content, token);
            if (!response.IsSuccessStatusCode)
                settled();
            await ProviderAdapter.RequireInferenceSuccessAsync(response, token);
            if (operation == InferenceOperation.Chat && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
                var terminal = false;
                while (await reader.ReadLineAsync(token) is { } line)
                {
                    if (!line.StartsWith("data: ", StringComparison.Ordinal))
                        continue;
                    if (line == "data: [DONE]")
                    {
                        terminal = true;
                        break;
                    }
                    using var document = JsonDocument.Parse(line.AsMemory(6));
                    var body = document.RootElement;
                    if (body.Get("error") is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } error)
                    {
                        if (ProviderAdapter.IsAllocationFailure(error.GetRawText()))
                            throw new ProviderFailure(503, "The provider could not allocate benchmark memory.", allocationFailure: true);
                        throw new ProviderFailure(502, "The provider reported an error during the benchmark.");
                    }
                    if (firstToken is null && body.Get("choices").Items().Any(item =>
                        item.Get("delta").Text("content") is { Length: > 0 } ||
                        item.Get("delta").Text("reasoning_content") is { Length: > 0 }))
                        firstToken = Stopwatch.GetElapsedTime(started);
                    (promptTokens, outputTokens, promptMilliseconds, tokensPerSecond, cachedTokens) =
                        ReadMetrics(body, (promptTokens, outputTokens, promptMilliseconds, tokensPerSecond, cachedTokens));
                }
                if (!terminal)
                    throw new ProviderFailure(502, "The benchmark stream ended before the provider reported completion.");
            }
            else if (operation is InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne)
            {
                var body = await ProviderAdapter.ReadBoundedAsync(response.Content, ProviderLimits.MaxRequestBytes, token);
                using var document = JsonDocument.Parse(body);
                (promptTokens, outputTokens, promptMilliseconds, tokensPerSecond, cachedTokens) =
                    ReadMetrics(document.RootElement, default);
            }
            else
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                await stream.CopyToAsync(Stream.Null, token);
            }
            settled();
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (operation is InferenceOperation.Chat or InferenceOperation.Embeddings or InferenceOperation.SystemOne)
            {
                var reserved = operation == InferenceOperation.Embeddings ? 0 : operation == InferenceOperation.Chat ? Output : 128;
                var target = workload.Units - reserved;
                var rows = operation == InferenceOperation.Embeddings ? workload.Items : 1;
                if (target > 0 && (promptTokens is null || promptTokens > rows * target ||
                    promptTokens < rows * Math.Max(1, target - Math.Max(16, target / 20))))
                    throw new ProviderFailure(502, "The provider did not confirm the intended benchmark context was processed.");
            }
            return new(elapsed, promptTokens, outputTokens, firstToken, promptMilliseconds,
                tokensPerSecond ?? (outputTokens is > 1 && firstToken is { } first && elapsed > first
                    ? (outputTokens - 1) / (elapsed - first).TotalSeconds : null), cachedTokens,
                pass == "reuse" && cachedTokens.HasValue ? cachedTokens > 0 : null);
        }
        catch (JsonException)
        {
            throw new ProviderFailure(502, "The provider returned an invalid benchmark result.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ProviderFailure(504, "The local benchmark exceeded its time limit.");
        }
    }

    private static (int?, int?, double?, double?, int?) ReadMetrics(JsonElement body,
        (int? Prompt, int? Output, double? Milliseconds, double? Rate, int? Cache) previous)
    {
        var usage = body.Get("usage");
        var timings = body.Get("timings");
        var cached = Nonnegative(usage.Get("prompt_tokens_details").Get("cached_tokens")) ?? Nonnegative(timings.Get("cache_n"));
        return (usage.Number("prompt_tokens") ?? previous.Prompt, usage.Number("completion_tokens") ?? previous.Output,
            Number(timings.Get("prompt_ms")) ?? previous.Milliseconds,
            Number(timings.Get("predicted_per_second")) ?? previous.Rate, cached ?? previous.Cache);
    }

    private static int? Nonnegative(JsonElement value) => value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number >= 0 ? number : null;

    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
        && double.IsFinite(number) && number >= 0 ? number : null;

    private static async Task<JsonObject> PayloadAsync(Deployment model, ProviderAdapter adapter, Workload workload,
        string pass, CancellationToken cancellation)
    {
        var operation = workload.Operation;
        var id = model.Instance!.Id;
        var prefix = pass switch { "short" => "Green", "half" => "Blue", _ => "Purple" };
        var payload = new JsonObject { ["model"] = id };
        switch (operation)
        {
            case InferenceOperation.Chat:
            {
                var message = new JsonObject { ["role"] = "user" };
                payload["messages"] = new JsonArray(message);
                payload["max_tokens"] = Output;
                payload["stream"] = true;
                payload["stream_options"] = new JsonObject { ["include_usage"] = true };
                if (workload.Items != 1)
                    payload["n"] = workload.Items;
                var input = await FitAsync((int)workload.Units - Output, prefix, async text =>
                {
                    message["content"] = text;
                    return await adapter.CountChatAsync(payload, cancellation);
                }, cancellation);
                message["content"] = input;
                break;
            }
            case InferenceOperation.Embeddings:
            {
                var input = await FitAsync((int)workload.Units, prefix, text => adapter.CountEmbeddingAsync(
                    JsonValue.Create(text)!, id, cancellation), cancellation);
                payload["input"] = new JsonArray(Enumerable.Range(0, workload.Items)
                    .Select(_ => (JsonNode?)JsonValue.Create(input)).ToArray());
                break;
            }
            case InferenceOperation.SystemOne:
                payload["state"] = prefix + Repeat(Math.Max(1, (int)workload.Units - 132));
                payload["questions"] = new JsonObject { ["benchmark"] = new JsonObject { ["type"] = "noul" } };
                break;
            case InferenceOperation.Images:
                payload["prompt"] = "A small green circle on a plain gray background";
                payload["size"] = $"{workload.Width}x{workload.Height}";
                payload["n"] = workload.Items;
                break;
            case InferenceOperation.Speech:
                payload["input"] = Repeat(Math.Max(1, ((int)workload.Units + 5) / 6))[..(int)workload.Units];
                break;
        }
        adapter.Prepare(operation, payload);
        return payload;
    }

    private static async Task<string> FitAsync(int target, string prefix, Func<string, Task<int?>> count, CancellationToken cancellation)
    {
        if (target < 16)
            throw new ProviderFailure(409, "The context is too small for the synthetic prompt and reserved output.");
        var low = 1;
        var high = target;
        var best = "";
        while (low <= high)
        {
            cancellation.ThrowIfCancellationRequested();
            var middle = low + (high - low) / 2;
            var prompt = prefix + Repeat(middle);
            var measured = await count(prompt);
            if (measured is null)
                return prefix + Repeat(Math.Max(1, target - 12));
            if (measured <= target)
            {
                best = prompt;
                if (measured >= target - 2)
                    break;
                low = middle + 1;
            }
            else
                high = middle - 1;
        }
        if (best.Length == 0)
            throw new ProviderFailure(409, "The provider's prompt template does not fit the benchmark context.");
        return best;
    }

    private static string Repeat(int count) => string.Concat(Enumerable.Repeat(" mango", count));

    private static MultipartFormDataContent Audio(string model, Workload workload)
    {
        var pcmBytes = Math.Max(2, (int)(workload.Units - 44) & ~1);
        var wav = new byte[44 + pcmBytes];
        using var stream = new MemoryStream(wav);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8); writer.Write(wav.Length - 8);
        writer.Write("WAVEfmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(pcmBytes);
        var tone = Enumerable.Range(0, 3200).Select(index => (short)(
            6000 * Math.Sin(index * 2 * Math.PI * 180 / 16000) * Math.Sin(index * Math.PI / 3200))).ToArray();
        for (var offset = 44; offset < wav.Length; offset += sizeof(short))
            writer.Write(tone[(offset - 44) / sizeof(short) % tone.Length]);
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "benchmark.wav");
        form.Add(new StringContent(model), "model");
        return form;
    }
}
