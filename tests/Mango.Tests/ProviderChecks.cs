using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mango.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

internal static class ProviderChecks
{
    private const string NativeSecret = "provider-fixture-secret";
    private const string ChatBody = """
        {"model":"test","messages":[{"role":"user","content":"Hello"}],"temperature":0.25,
         "stream":true,"stream_options":{"include_usage":true},"max_tokens":25,
         "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}]}
        """;
    private const string Stream = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: [DONE]\n\n";

    public static async Task RunAsync()
    {
        var endpoint = new ProviderEndpoint(Guid.NewGuid(), ProviderProfile.UnslothStudio, ProviderMode.Managed,
            "http://127.0.0.1:8888", ProviderSecrets.Protect(NativeSecret), null, null);
        Check(endpoint.ProtectedToken != NativeSecret && ProviderSecrets.Open(endpoint.ProtectedToken!) == NativeSecret,
            "provider token protected for the Windows user");
        var chat = DeploymentFor(endpoint.Id, InferenceOperation.Chat);
        var vision = chat with { Model = chat.Model with { Id = "vision", Inputs = ["image", "text"] } };
        await Fails(409, () =>
        {
            ProviderCatalog.RequireCompatibleAlias(chat, [vision]);
            return Task.CompletedTask;
        });
        var embedding = DeploymentFor(endpoint.Id, InferenceOperation.Embeddings);
        var incompatible = embedding with { Model = embedding.Model with { Id = "other", EmbeddingDimensions = 4 } };
        await Fails(409, () =>
        {
            ProviderCatalog.RequireCompatibleAlias(embedding, [incompatible]);
            return Task.CompletedTask;
        });

        var emitted = false;
        using var fixture = new HttpClient(new Handler(async request =>
        {
            Check(request.Headers.Authorization?.Parameter == NativeSecret, "native credential only");
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            Check(root.GetProperty("model").GetString() == "instance-id", "alias resolves to loaded instance");
            if (request.RequestUri!.AbsolutePath.EndsWith("count_tokens", StringComparison.Ordinal))
                return Response(HttpStatusCode.OK, """{"input_tokens":10}""");
            Check(root.GetProperty("temperature").GetDouble() == 0.25 &&
                root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean() &&
                root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString() == "lookup",
                "supported inference parameters and client tools survive forwarding");
            Check(!root.GetProperty("enable_tools").GetBoolean() && !root.GetProperty("mcp_enabled").GetBoolean() &&
                root.GetProperty("context_overflow").GetString() == "error", "Unsloth host tools and truncation disabled");
            emitted = true;
            var response = Response(HttpStatusCode.OK, Stream, "text/event-stream");
            response.Headers.TryAddWithoutValidation("Set-Cookie", $"token={NativeSecret}");
            return response;
        }));
        var adapter = new UnslothAdapter(fixture, endpoint);
        var context = Context(ChatBody);
        using (var request = await InferenceRequest.ReadAsync(InferenceOperation.Chat, context.Request, default))
        {
            var execution = await request.ForwardAsync(chat, adapter, context, default);
            Check(emitted && ReadBody(context) == Stream && context.Response.ContentType!.StartsWith("text/event-stream"),
                "exact SSE forwarding");
            Check(execution.PromptTokens == 10 && execution.PromptCountSource == "provider" && execution.FirstOutput.HasValue,
                "provider token and first output timing recorded");
            Check(!context.Response.Headers.ContainsKey("Set-Cookie"), "upstream credential headers are excluded");
        }

        await Fails(400, async () => await InferenceRequest.ReadAsync(InferenceOperation.Chat,
            Context("""{"model":"test","enable_tools":true}""").Request, default));
        await Fails(400, async () => await InferenceRequest.ReadAsync(InferenceOperation.Speech,
            Context("""{"model":"test","input":"Hello","provider_id":"external"}""").Request, default));
        await RejectTranscriptionFieldAsync("prompt", "This field would be ignored by the provider.");
        await RejectTranscriptionFieldAsync("provider_id", "external");
        await Fails(422, () => Run(InferenceOperation.Chat, ChatBody.Replace("25", "2050"), chat, adapter));
        await Fails(422, () => Run(InferenceOperation.Chat, """
            {"model":"test","messages":[{"role":"user","content":[{"type":"image_url",
              "image_url":{"url":"http://127.0.0.1/private"}}]}]}
            """, vision, adapter));

        using var malformed = new HttpClient(new Handler(_ => Task.FromResult(Response(HttpStatusCode.OK,
            """{"data":[{"index":0,"embedding":[0.5]}]}"""))));
        var lm = new LmStudioAdapter(malformed, endpoint with { Profile = ProviderProfile.LmStudio });
        await Fails(502, () => Run(InferenceOperation.Embeddings, """{"model":"test","input":"small"}""", embedding, lm));
        await Fails(422, () => Run(InferenceOperation.Embeddings,
            """{"model":"test","input":"small","dimensions":3}""", embedding, lm));

        using var upstreamError = new HttpClient(new Handler(_ => Task.FromResult(Response(HttpStatusCode.Unauthorized,
            $"{{\"error\":\"{NativeSecret}\"}}"))));
        var refused = new LmStudioAdapter(upstreamError, endpoint with { Profile = ProviderProfile.LmStudio });
        await Fails(422, () => Run(InferenceOperation.Chat,
            ChatBody.Replace("max_tokens", "max_completion_tokens"), chat, refused));
        var llama = new LlamaAdapter(upstreamError, endpoint with { Profile = ProviderProfile.LlamaSingle });
        var budget = new JsonObject { ["max_completion_tokens"] = 25 };
        llama.Prepare(InferenceOperation.Chat, budget);
        Check(!budget.ContainsKey("max_completion_tokens") && budget["max_tokens"]?.GetValue<int>() == 25,
            "llama receives the same total generation limit through max_tokens");
        var error = await Fails(502, () => Run(InferenceOperation.Chat, ChatBody, chat, refused));
        Check(!error.Message.Contains(NativeSecret), "upstream error body and secrets are excluded");
        await CheckResidentImageAsync(endpoint);
        Console.WriteLine("Provider fixture checks passed.");
    }

    private static async Task RejectTranscriptionFieldAsync(string name, string value)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "multipart/form-data; boundary=test";
        using var upload = new MemoryStream([1, 2, 3]);
        var file = new FormFile(upload, 0, 3, "file", "fixture.wav");
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
        {
            ["model"] = "test",
            [name] = value
        }, new FormFileCollection { file });
        await Fails(400, async () => await InferenceRequest.ReadAsync(InferenceOperation.Transcription, context.Request, default));
    }

    private static async Task CheckResidentImageAsync(ProviderEndpoint endpoint)
    {
        var load = "image-load-1";
        var sentImage = false;
        using var fixture = new HttpClient(new Handler(async request =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/health":
                    return Response(HttpStatusCode.OK, """{"service":"Unsloth UI Backend","version":"fixture"}""");
                case "/v1/models":
                case "/api/inference/loaded-models":
                    return Response(HttpStatusCode.OK, """{"data":[]}""");
                case "/api/models/local":
                    return Response(HttpStatusCode.OK, """{"models":[]}""");
                case "/api/inference/images/status":
                    return Response(HttpStatusCode.OK, $$"""
                        {"loaded":true,"repo_id":"ref:private","display_repo_id":"test/image","resident_load_id":"{{load}}",
                         "engine":"diffusers","gguf_filename":"sample.safetensors"}
                        """);
                case "/v1/images/generations":
                    using (var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                    {
                        var body = document.RootElement;
                        Check(body.GetProperty("model").GetString() == "test/image" &&
                            body.GetProperty("response_format").GetString() == "b64_json" &&
                            body.GetProperty("guidance").GetDouble() == 1, "image wire identity and controls");
                    }
                    sentImage = true;
                    return Response(HttpStatusCode.OK, """{"data":[{"b64_json":"AA=="}]}""");
                default:
                    throw new InvalidOperationException("Unexpected fixture route.");
            }
        }));
        var adapter = new UnslothAdapter(fixture, endpoint);
        var inventory = await adapter.InspectAsync(default);
        var model = inventory.Models.Single();
        Check(model.ManagementId == "ref:private" && model.Instances.Single().NativeGeneration == load &&
            model.Operations.SequenceEqual([InferenceOperation.Images]), "resident image discovered outside /v1/models");
        var deployment = new Deployment(endpoint.Id, model, new("test", [], null, null, null, null, 0), true, false, "fixture");
        const string body = """{"model":"test","prompt":"A test image","guidance":1}""";
        await Run(InferenceOperation.Images, body, deployment, adapter);
        Check(sentImage, "resident image forwarded");
        load = "image-load-2";
        sentImage = false;
        await Fails(409, () => Run(InferenceOperation.Images, body, deployment, adapter));
        Check(!sentImage, "a replaced resident image must not receive the request");
    }

    private static Deployment DeploymentFor(Guid endpoint, InferenceOperation operation)
    {
        var model = new ProviderModel("deployment", "native-model", "native-model", "Fixture model", "Q4",
            ["text"], [operation == InferenceOperation.Embeddings ? "embedding" : "text"], [operation],
            [new("instance-id", 2048, 1, "fingerprint")], 32768, true, true, 2, ["float"]);
        return new(endpoint, model, new("test", [], null, null, null, null, 3), true, false, "fixture");
    }

    private static async Task Run(InferenceOperation operation, string json, Deployment deployment, ProviderAdapter adapter)
    {
        var context = Context(json);
        using var payload = await InferenceRequest.ReadAsync(operation, context.Request, default);
        await payload.ForwardAsync(deployment, adapter, context, default);
    }

    private static DefaultHttpContext Context(string json)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ReadBody(HttpContext context) => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string mime = "application/json") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mime) };

    private static async Task<ProviderFailure> Fails(int status, Func<Task> check)
    {
        try { await check(); }
        catch (ProviderFailure error) when (error.Status == status) { return error; }
        throw new InvalidOperationException($"Expected provider error HTTP {status}.");
    }

    private static void Check(bool passed, string label)
    {
        if (!passed)
            throw new InvalidOperationException($"Provider check failed: {label}");
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request);
    }
}
