using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mango.Providers;
using Mango.Peers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

internal static class ProviderChecks
{
    private const string NativeSecret = "provider-fixture-secret";
    private const string ChatBody = """
        {"model":"test","messages":[{"role":"user","content":"Hello"}],"temperature":0.25,
         "chat_template_kwargs":{"enable_thinking":false},"reasoning_effort":"low",
         "stream":true,"stream_options":{"include_usage":true},"max_tokens":25,
         "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object"}}}]}
        """;
    private const string Stream = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: [DONE]\n\n";

    public static async Task RunAsync()
    {
        var observed = new ObservedDeployment("fixture", "fixture", null, null, ["text", "image"], ["text"],
            [InferenceOperation.Chat], 262144, null, true, true, null, 1, null, "fixture", ClientTools: true);
        var catalogue = JsonSerializer.SerializeToElement(PeerNetwork.DescribeModel("fixture", [observed]));
        Check(catalogue.GetProperty("context_size").GetInt32() == 262144 &&
            catalogue.GetProperty("capabilities").EnumerateArray().Any(value => value.GetString() == "vision"),
            "catalogue preserves observed loaded context and vision");
        foreach (var models in new[] { new[] { observed with { Loaded = false } },
            new[] { observed, observed with { Context = null } } })
            Check(!JsonSerializer.SerializeToElement(PeerNetwork.DescribeModel("fixture", models))
                .TryGetProperty("context_size", out _), "unknown serving context is omitted from the catalogue");
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
                !root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean() &&
                root.GetProperty("reasoning_effort").GetString() == "low" &&
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

        using (var opaque = await InferenceRequest.ReadAsync(InferenceOperation.Chat,
            Context("""{"model":"test","enable_tools":true}""").Request, default))
            Check(opaque.Alias == "test", "provider fields do not veto routing metadata extraction");
        await Fails(400, async () => await InferenceRequest.ReadAsync(InferenceOperation.Speech,
            Context("""{"model":"test","input":"Hello","provider_id":"external"}""").Request, default));
        await RejectTranscriptionFieldAsync("prompt", "This field would be ignored by the provider.");
        await RejectTranscriptionFieldAsync("provider_id", "external");
        await Fails(422, () => Run(InferenceOperation.Chat, ChatBody.Replace("25", "2050"), chat, adapter));
        await Fails(422, () => Run(InferenceOperation.Chat, """
            {"model":"test","messages":[{"role":"user","content":[{"type":"image_url",
              "image_url":{"url":"http://127.0.0.1/private"}}]}]}
            """, vision, adapter));

        await CheckEmbeddingsAsync(endpoint, embedding);

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
        using var allocation = new HttpClient(new Handler(request => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("count_tokens", StringComparison.Ordinal)
                ? Response(HttpStatusCode.OK, """{"input_tokens":10}""")
                : Response(HttpStatusCode.OK, "data: {\"error\":{\"message\":\"out of memory\"}}\n\n", "text/event-stream"))));
        var exhausted = await Fails(503, () => Run(InferenceOperation.Chat, ChatBody, chat, new UnslothAdapter(allocation, endpoint)));
        Check(exhausted.AllocationFailure, "provider streaming allocation errors invalidate the local placement");
        using var unlabeledStream = new HttpClient(new Handler(_ => Task.FromResult(
            Response(HttpStatusCode.OK, Stream, "application/octet-stream"))));
        var generic = new LmStudioAdapter(unlabeledStream, endpoint with { Profile = ProviderProfile.Generic });
        var invalidStream = await Fails(502, () => Run(InferenceOperation.Chat, ChatBody, chat, generic));
        Check(invalidStream.Message.Contains("event stream"), "unlabeled chat streams are rejected as invalid provider responses");
        await CheckGenericInventoryAsync(endpoint);
        await CheckGenericMediaAsync(endpoint);
        await CheckLmStudioUnloadAsync(endpoint);
        await CheckResidentImageAsync(endpoint);
        await CheckTransparentHopsAsync(endpoint);
        Console.WriteLine("Provider fixture checks passed.");
    }

    private static async Task CheckTransparentHopsAsync(ProviderEndpoint endpoint)
    {
        var image = "data:image/png;base64," + new string('A', 256 * 1024);
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Count retained images." });
        for (var index = 0; index < 6; index++)
            content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject {
                ["url"] = image, ["provider_image_option"] = index } });
        var chat = new JsonObject {
            ["model"] = "test", ["messages"] = new JsonArray(new JsonObject {
                ["role"] = "provider_role", ["content"] = content, ["provider_message_option"] = true }),
            ["chat_template_kwargs"] = new JsonObject { ["provider_nested"] = new JsonObject { ["level"] = 7 },
                ["enable_thinking"] = "provider-decides", ["preserve_thinking"] = true },
            ["web_search_options"] = new JsonObject { ["provider_mode"] = "test" },
            ["tools"] = new JsonArray(new JsonObject { ["type"] = "provider_tool", ["opaque"] = new JsonArray(1, 2) }),
            ["provider_extension"] = new JsonObject { ["nested"] = new JsonArray(true, "retained") }
        };
        foreach (var body in new[] { chat, (JsonObject)chat.DeepClone(),
            JsonNode.Parse("""{"model":"test","input":{"provider_input":"opaque"},"encoding_format":"provider-format","provider_extension":{"retain":true}}""")!.AsObject(),
            JsonNode.Parse("""{"model":"test","state":{"provider_state":[1,2]},"questions":{"reply":{"type":"provider_decides","extra":true}},"provider_extension":{"retain":true}}""")!.AsObject() })
        {
            var operation = body.ContainsKey("messages") ? InferenceOperation.Chat :
                body.ContainsKey("input") ? InferenceOperation.Embeddings : InferenceOperation.SystemOne;
            if (body != chat && operation == InferenceOperation.Chat) body["max_tokens"] = 0;
            var expected = (JsonObject)body.DeepClone();
            expected["model"] = "instance-id";
            var calls = 0;
            using var fixture = new HttpClient(new Handler(async request => {
                calls++;
                var actual = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                Check(JsonNode.DeepEquals(actual, expected), "all provider fields survive ingress and peer parsing except model resolution");
                Check(operation != InferenceOperation.Chat || body.ContainsKey("max_tokens") ||
                    !actual!.AsObject().ContainsKey("max_tokens"), "an omitted output limit stays omitted");
                return Response(HttpStatusCode.OK, """{"provider_reply":"unchanged"}""");
            }));
            var adapter = new LmStudioAdapter(fixture, endpoint with { Profile = ProviderProfile.Generic });
            using var ingress = await InferenceRequest.ReadAsync(operation, Context(body.ToJsonString()).Request, default);
            using var transfer = ingress.CreateBody();
            var servingContext = Context(await transfer.ReadAsStringAsync());
            using var peer = await InferenceRequest.ReadAsync(operation, servingContext.Request, default);
            if (operation == InferenceOperation.Chat)
                Check(peer.Shape.MediaItems == 6 && peer.Shape.PromptUnits < 4096,
                    "large retained data URLs count as six media items rather than base64 text context");
            var deployment = DeploymentFor(endpoint.Id, operation);
            deployment = deployment with { Model = deployment.Model with { Inputs = ["image", "text"],
                ReportedMediaTokensPerItem = 1024, Instances = [new("instance-id", 262144, 1, "fixture")] } };
            var timing = await peer.ForwardAsync(deployment, adapter, servingContext, default);
            Check(calls == 1 && (operation != InferenceOperation.Chat || timing.PromptTokens < 10240),
                "otherwise admissible opaque request reaches provider without a tokenizer preflight");
        }
        var oversized = Context("{}");
        oversized.Request.ContentLength = ProviderLimits.MaxRequestBytes + 1L;
        await Fails(413, async () => await InferenceRequest.ReadAsync(InferenceOperation.Chat, oversized.Request, default));
    }

    private static async Task CheckLmStudioUnloadAsync(ProviderEndpoint endpoint)
    {
        var loaded = true;
        var race = false;
        var refuse = false;
        var unavailable = false;
        var key = "sample";
        var unloads = 0;
        using var fixture = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(unavailable ? Response(HttpStatusCode.ServiceUnavailable, "{}") :
                    Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { models = new[] { new {
                        key, type = "llm", loaded_instances = loaded ? new[] { new { id = "loaded" } } : []
                    } } })));
            unloads++;
            if (race) loaded = false;
            if (race || refuse) return Task.FromResult(Response(HttpStatusCode.Conflict, "{}"));
            loaded = false;
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        }));
        var adapter = new LmStudioAdapter(fixture, endpoint with { Profile = ProviderProfile.LmStudio },
            (_, _) => Task.FromResult<LmStudioDevices?>(null));
        var model = (await adapter.InspectAsync(default)).Models.Single();
        loaded = false;
        await adapter.UnloadAsync(model, default);
        Check(unloads == 0, "an already unloaded LM Studio instance is verified without a duplicate unload");
        loaded = race = true;
        await adapter.UnloadAsync(model, default);
        Check(unloads == 1 && !loaded, "an unload race succeeds only after fresh inventory confirms absence");
        loaded = refuse = true;
        race = false;
        await Fails(409, () => adapter.UnloadAsync(model, default));
        unavailable = true;
        await Fails(503, () => adapter.UnloadAsync(model, default));
        unavailable = false;
        key = "replacement-model";
        var before = unloads;
        await Fails(503, () => adapter.UnloadAsync(model, default));
        Check(unloads == before, "a reused instance identifier cannot unload a different model");
    }

    private static async Task CheckGenericInventoryAsync(ProviderEndpoint endpoint)
    {
        var inventory = """
            {"models":[{"key":"sample","type":"llm","input_modalities":["text","image"],"client_tools":true,"media_tokens_per_item":1024,
            "loaded_instances":[{"id":"loaded","busy":true,"config":{"context_size":262144,"parallel":1}}]}]}
            """;
        using var fixture = new HttpClient(new Handler(_ => Task.FromResult(Response(HttpStatusCode.OK, inventory))));
        var adapter = new LmStudioAdapter(fixture, endpoint with { Profile = ProviderProfile.Generic });
        var model = (await adapter.InspectAsync(default)).Models.Single();
        Check(model.Inputs.Contains("image") && model.ClientTools && model.Instances.Single().Context == 262144,
            "generic explicit vision, client tools and loaded context survive normalization");
        var boundedMedia = new Deployment(endpoint.Id, model, new(null, [], null, null, null, null, 0), true, false, null);
        Check(boundedMedia.MediaTokensPerItem == 1024 && new InferenceShape("sample", InferenceOperation.Chat,
            ["text", "image"], false, false, 49, 8192, 1, 1, 1, null, null)
            .Fits(model.Inputs, model.ClientTools, model.StructuredOutput, boundedMedia.Context,
                boundedMedia.MediaTokensPerItem, model.EmbeddingEncodings),
            "reported enforced media token allowance enables one-image context admission without a configured override");
        Check((boundedMedia with { Options = boundedMedia.Options with { MediaTokensPerItem = 2048 } }).MediaTokensPerItem == 2048,
            "a larger configured conservative media allowance remains effective");
        var fiveImages = new InferenceShape("sample", InferenceOperation.Chat, ["text", "image"],
            false, false, 49, 8192, 5, 1, 1, null, null);
        Check(fiveImages.Fits(model.Inputs, model.ClientTools, model.StructuredOutput, boundedMedia.Context,
            boundedMedia.MediaTokensPerItem, model.EmbeddingEncodings) &&
            Mango.Resources.ResourceProtection.Covers(new(InferenceOperation.Chat, 262144, 1, 1), fiveImages.Demand(262144)) &&
            Mango.Resources.ResourceProtection.Covers(new(InferenceOperation.Chat, 262144, 1, 5), fiveImages.Demand(262144)),
            "media evidence count does not impose a provider input cap");
        var validInventory = inventory;
        foreach (var invalidBound in new[] { "0", "-1", "1.5", "\"1024\"", "4194305" })
        {
            inventory = validInventory.Replace("\"media_tokens_per_item\":1024", "\"media_tokens_per_item\":" + invalidBound);
            await Fails(502, () => adapter.InspectAsync(default));
        }
        inventory = validInventory;
        Check(model.Instances.Single().Busy && await adapter.CheckIdleAsync(model, default) == false,
            "generic inventory reports busy instances");
        inventory = inventory.Replace("true", "false");
        Check(await adapter.CheckIdleAsync(model, default) == true, "generic inventory confirms an idle instance");
        inventory = inventory.Replace(",\"busy\":false", "");
        Check(await adapter.CheckIdleAsync(model, default) is null, "missing busy flag cannot confirm idle");
        inventory = """{"models":[{"key":"sample","type":"llm"}]}""";
        await Fails(502, () => adapter.CheckIdleAsync(model, default));
    }

    private static async Task CheckGenericMediaAsync(ProviderEndpoint endpoint)
    {
        using var fixture = new HttpClient(new Handler(async request =>
        {
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/api/v1/models")
                return Response(HttpStatusCode.OK, """
                    {"models":[
                      {"key":"draw","type":"image","loaded_instances":[{"id":"draw-1"}]},
                      {"key":"listen","type":"transcription","loaded_instances":[{"id":"listen-1"}]},
                      {"key":"speak","type":"speech","loaded_instances":[{"id":"speak-1"}]}
                    ]}
                    """);
            if (route == "/v1/audio/transcriptions")
            {
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(part =>
                    part.Headers.ContentDisposition!.Name!.Trim('"'));
                Check(await parts["model"].ReadAsStringAsync() == "listen-1" &&
                    (await parts["file"].ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 1, 2, 3 }),
                    "generic transcription forwards the instance and audio file");
                return Response(HttpStatusCode.OK, """{"text":"Hello"}""");
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var model = body.RootElement.GetProperty("model").GetString();
            return (route, model) switch
            {
                ("/v1/images/generations", "draw-1") => Response(HttpStatusCode.OK, """{"data":[{"b64_json":"AA=="}]}"""),
                ("/v1/audio/speech", "speak-1") => Response(HttpStatusCode.OK, "audio bytes", "audio/wav"),
                _ => throw new InvalidOperationException("Unexpected generic inference route or instance.")
            };
        }));
        var adapter = new LmStudioAdapter(fixture, endpoint with { Profile = ProviderProfile.Generic });
        var models = (await adapter.InspectAsync(default)).Models;
        Deployment Find(string key) => new(endpoint.Id, models.Single(model => model.NativeId == key),
            new(key, [], null, null, null, null, 0), true, false, "fixture");
        await Run(InferenceOperation.Images, """{"model":"draw","prompt":"A mango"}""", Find("draw"), adapter);
        await Run(InferenceOperation.Speech, """{"model":"speak","input":"Hello","response_format":"wav"}""", Find("speak"), adapter);

        var context = Context("");
        context.Request.ContentType = "multipart/form-data; boundary=test";
        using var upload = new MemoryStream([1, 2, 3]);
        var file = new FormFile(upload, 0, 3, "file", "fixture.wav") { Headers = new HeaderDictionary(), ContentType = "audio/wav" };
        context.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["model"] = "listen" },
            new FormFileCollection { file });
        var transcription = Find("listen");
        Check(transcription.Model.Inputs.SequenceEqual(["audio"]) && transcription.Model.Outputs.SequenceEqual(["text"]),
            "generic transcription publishes audio input and text output");
        using var payload = await InferenceRequest.ReadAsync(InferenceOperation.Transcription, context.Request, default);
        await payload.ForwardAsync(transcription, adapter, context, default);
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

    private static async Task CheckEmbeddingsAsync(ProviderEndpoint endpoint, Deployment embedding)
    {
        endpoint = endpoint with { Profile = ProviderProfile.LmStudio };
        int? requestedDimensions = null;
        var output = """{"data":[{"index":0,"embedding":[0.5,0.25]},{"index":1,"embedding":[0.1,0.2]}]}""";
        using var fixture = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/models")
                return Response(HttpStatusCode.OK, """
                    {"models":[
                      {"key":"native-model","type":"embedding","loaded_instances":[]},
                      {"key":"native-model","type":"embedding","loaded_instances":[
                        {"id":"instance-id","config":{"context_length":512}}]}
                    ]}
                    """);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var hasDimensions = body.RootElement.TryGetProperty("dimensions", out var dimensions);
            Check((hasDimensions ? dimensions.GetInt32() : (int?)null) == requestedDimensions,
                "embedding requests preserve the client's dimension parameter");
            return Response(HttpStatusCode.OK, output);
        }));
        var devices = new LmStudioDevices(Json("""{"deviceIdentifier":"here"}"""), Json("""
            [{"modelKey":"native-model"},{"modelKey":"native-model","deviceIdentifier":"linked"}]
            """), Json("""
            [{"modelKey":"native-model","deviceIdentifier":"linked","identifier":"instance-id"}]
            """));
        var adapter = new LmStudioAdapter(fixture, endpoint, (_, _) => Task.FromResult<LmStudioDevices?>(devices));
        var models = (await adapter.InspectAsync(default)).Models;
        var local = models.Single(model => model.Origin == ModelOrigin.Local);
        var discovered = models.Single(model => model.Origin == ModelOrigin.LmLink);
        Check(local.Id != discovered.Id && local.Instances.Length == 0 && !local.SupportsLoad &&
            discovered.Instances.Single().Id == "instance-id" && discovered.EmbeddingDimensions is null,
            "LM Studio's shared model keys preserve separate device identities and exact loaded instance ownership");
        var unreported = embedding with { Model = discovered };
        Check(unreported.ServingDisabled && !unreported.Eligible && !(embedding with { Model = local }).ServingDisabled &&
            (embedding with { Model = local, Options = embedding.Options with { ServingDisabled = true } }).ServingDisabled,
            "existing settings default LM Link copies off, leave local copies on, and retain an old disabled choice");
        unreported = unreported with { Options = unreported.Options with { ServingOverride = true } };
        var batch = Context("""{"model":"test","input":["first","second"]}""");
        using (var request = await InferenceRequest.ReadAsync(InferenceOperation.Embeddings, batch.Request, default))
        {
            Check(unreported.Eligible && request.Shape.Fits(["text"], false, false, unreported.Context, null, ["float"]),
                "an enabled embedding model with a served context needs no reported vector length");
            await request.ForwardAsync(unreported, adapter, batch, default);
            Check(ReadBody(batch) == output, "an embedding batch with unreported dimensions is returned unchanged");
        }

        requestedDimensions = 3;
        output = """{"data":[{"index":0,"embedding":[0.5,0.25,0.1]}]}""";
        const string resized = """{"model":"test","input":"small","dimensions":3}""";
        await Run(InferenceOperation.Embeddings, resized, embedding, adapter);

        output = """{"data":[{"index":0,"embedding":[]}]}""";
        await Fails(502, () => Run(InferenceOperation.Embeddings, resized, embedding, adapter));
        output = """{"data":[{"index":0,"embedding":[0.5]},{"index":1,"embedding":[0.25,0.1]}]}""";
        await Fails(502, () => Run(InferenceOperation.Embeddings,
            """{"model":"test","input":["first","second"],"dimensions":3}""", embedding, adapter));

        var base64 = embedding with { Model = embedding.Model with { EmbeddingEncodings = ["float", "base64"] } };
        const string encoded = """{"model":"test","input":"small","encoding_format":"base64","dimensions":3}""";
        output = """{"data":[{"index":0,"embedding":"AAAAAA=="}]}""";
        await Run(InferenceOperation.Embeddings, encoded, base64, adapter);
        output = """{"data":[{"index":0,"embedding":"AA=="}]}""";
        await Fails(502, () => Run(InferenceOperation.Embeddings, encoded, base64, adapter));

        devices = devices with { Loaded = Json("""
            [{"modelKey":"native-model","identifier":"instance-id"},
             {"modelKey":"native-model","deviceIdentifier":"linked","identifier":"instance-id"}]
            """) };
        Check((await adapter.InspectAsync(default)).Models.All(model => model.Instances.Length == 0 && model.AmbiguousInstance),
            "an identifier shared by loaded copies cannot safely select either device");
        await Fails(503, () => adapter.ConfirmDeploymentAsync(InferenceOperation.Embeddings, discovered, discovered.Instances[0], default));

        static JsonElement Json(string value)
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
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
