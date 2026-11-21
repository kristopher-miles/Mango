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
        ProviderCatalog.RequireCompatibleAlias(chat, [vision]);
        using (var overflow = Response(HttpStatusCode.BadRequest,
            """{"error":{"type":"invalid_request_error","message":"prompt is too long: 42 tokens > 36 maximum"}}"""))
            await Fails(422, () => ProviderAdapter.RequireInferenceSuccessAsync(overflow, default, chat: true));
        using (var invalid = Response(HttpStatusCode.BadRequest, """{"error":{"message":"unknown sampling parameter"}}"""))
            await Fails(400, () => ProviderAdapter.RequireInferenceSuccessAsync(invalid, default, chat: true));
        using (var rejected = Response(HttpStatusCode.InternalServerError,
            """{"error":{"type":"model_load_failed","message":"Model loading was stopped due to insufficient system resources."}}"""))
        {
            var failure = await Fails(409, () => ProviderAdapter.RequireInferenceSuccessAsync(rejected, default, lmStudioLoad: true));
            Check(failure.Code == "load_rejected", "LM Studio's terminal failure releases the load reservation");
        }
        using (var uncertain = Response(HttpStatusCode.InternalServerError, """{"error":{"type":"internal_error"}}"""))
        {
            var failure = await Fails(502, () => ProviderAdapter.RequireInferenceSuccessAsync(uncertain, default, lmStudioLoad: true));
            Check(failure.Code != "load_rejected", "an ambiguous HTTP failure leaves native settlement unconfirmed");
        }
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
            Check(execution.PromptTokens is null && execution.FirstOutput.HasValue,
                "timing retained without a speculative token count");
            Check(!context.Response.Headers.ContainsKey("Set-Cookie"), "upstream credential headers are excluded");
        }

        using (var opaque = await InferenceRequest.ReadAsync(InferenceOperation.Chat,
            Context("""{"model":"test","enable_tools":true}""").Request, default))
            Check(opaque.Alias == "test", "provider fields do not veto routing metadata extraction");
        await Fails(400, async () => await InferenceRequest.ReadAsync(InferenceOperation.Speech,
            Context("""{"model":"test","input":"Hello","provider_id":"external"}""").Request, default));
        await RejectTranscriptionFieldAsync("prompt", "This field would be ignored by the provider.");
        await RejectTranscriptionFieldAsync("provider_id", "external");
        await Run(InferenceOperation.Chat, ChatBody.Replace("\"max_tokens\":25", "\"max_tokens\":2050"), chat with { Options = chat.Options with { ServedContext = null } }, adapter);
        await Fails(422, () => Run(InferenceOperation.Chat, """
            {"model":"test","messages":[{"role":"user","content":[{"type":"image_url",
              "image_url":{"url":"http://127.0.0.1/private"}}]}]}
            """, vision, adapter));

        await CheckEmbeddingsAsync(endpoint, embedding);
        await CheckPublicInstanceNamesAsync(endpoint, embedding);

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
        using var unlabeledStream = new HttpClient(new Handler(request => Task.FromResult(
            request.Method == HttpMethod.Get ? GenericInstanceInventory() :
                Response(HttpStatusCode.OK, Stream, "application/octet-stream"))));
        var generic = new LmStudioAdapter(unlabeledStream, endpoint with { Profile = ProviderProfile.Generic });
        var genericChat = chat with { Model = (await generic.InspectAsync(default)).Models.Single() };
        var invalidStream = await Fails(502, () => Run(InferenceOperation.Chat, ChatBody, genericChat, generic));
        Check(invalidStream.Message.Contains("event stream"), "unlabeled chat streams are rejected as invalid provider responses");
        await CheckGenericInventoryAsync(endpoint);
        await CheckGenericMediaAsync(endpoint);
        await CheckLmStudioUnloadAsync(endpoint);
        await CheckLmStudioSharedLoadAsync(endpoint);
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
                if (request.Method == HttpMethod.Get) return GenericInstanceInventory();
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
                ReportedMediaTokensPerItem = 1024, Instances = (await adapter.InspectAsync(default)).Models.Single().Instances } };
            var timing = await peer.ForwardAsync(deployment, adapter, servingContext, default);
            Check(calls == 1 && (operation != InferenceOperation.Chat || timing.PromptTokens is null),
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

    private static async Task CheckPublicInstanceNamesAsync(ProviderEndpoint endpoint, Deployment embedding)
    {
        var legacy = "mango-" + new string('a', 64);
        const string name = "hosted-embedding-model";
        var deployment = embedding with
        {
            Model = embedding.Model with { NativeId = name, Instances = [new(legacy, 2048, 1, "legacy")] },
            Options = embedding.Options with { Yolo = true }
        };
        Check(deployment.ProviderIdentifiers.SequenceEqual([name]) && !deployment.AcceptsIdentifier(legacy),
            "legacy Mango instance identifiers remain available for provider control only");
        using var fixture = new HttpClient(new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("model").GetString() == legacy, "provider receives the verified loaded instance");
            return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { model = legacy, data = new[] { 0.125, 0.5 } }));
        }));
        var adapter = new LmStudioAdapter(fixture, endpoint with { Profile = ProviderProfile.LmStudio });
        var context = Context(JsonSerializer.Serialize(new { model = name, input = "embedding fixture" }));
        using var request = await InferenceRequest.ReadAsync(InferenceOperation.Embeddings, context.Request, default);
        await request.ForwardAsync(deployment, adapter, context, default);
        using var result = JsonDocument.Parse(ReadBody(context));
        Check(result.RootElement.GetProperty("model").GetString() == name &&
            result.RootElement.GetProperty("data")[0].GetDouble() == 0.125,
            "public embedding response uses the requested model name and preserves vector data");

        var stream = Encoding.UTF8.GetBytes("data: {\"model\":\"" + legacy + "\",\"detail\":\"" + legacy + "\"}\n\n");
        var expected = Encoding.UTF8.GetString(stream).Replace(legacy, "public-alias", StringComparison.Ordinal);
        for (var split = 1; split < stream.Length; split++)
        {
            context = Context("{}");
            context.Response.ContentType = "text/event-stream";
            var output = new InferenceOutput(context, InferenceOperation.Chat, "public-alias");
            await output.WriteAsync(stream.AsMemory(0, split), default);
            await output.WriteAsync(stream.AsMemory(split), default);
            await output.CompleteAsync(default);
            Check(ReadBody(context) == expected, "legacy IDs stay private across every SSE transport split");
        }
    }

    private static async Task CheckLmStudioSharedLoadAsync(ProviderEndpoint endpoint)
    {
        const string key = "shared";
        const string localPath = "local/gemma q4.gguf";
        const string remotePath = "linked/gemma q4.gguf";
        var linkedInstance = "linked-instance";
        endpoint = endpoint with { Profile = ProviderProfile.LmStudio };
        string? localInstance = null;
        var loadedDevice = "here";
        var loadedPath = localPath;
        var loads = 0;
        var unloads = 0;
        using var fixture = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/models")
                return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { models = new[]
                {
                    new { key, type = "llm", format = "gguf", size_bytes = 100, capabilities = new { vision = false },
                        loaded_instances = localInstance is null ? [] : new[] { new { id = localInstance, config = new { context_length = 8192 } } } },
                    new { key, type = "llm", format = "gguf", size_bytes = linkedInstance == key ? 100 : 200,
                        capabilities = new { vision = linkedInstance != key },
                        loaded_instances = new[] { new { id = linkedInstance,
                            config = new { context_length = linkedInstance == key ? 8192 : 4096 } } } }
                } }));
            Check(request.RequestUri.AbsolutePath == "/api/v1/models/unload", "a shared identifier never reaches the REST load path");
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("instance_id").GetString() == localInstance,
                "shared-key unload addresses only the verified local instance");
            localInstance = null;
            unloads++;
            return Response(HttpStatusCode.OK, "{}");
        }));
        Task<LmStudioDevices?> InspectAsync(Uri _, CancellationToken token)
        {
            var loaded = new List<object> { new { modelKey = key, path = remotePath, deviceIdentifier = "linked", identifier = linkedInstance } };
            if (localInstance is not null)
                loaded.Add(new { modelKey = key, path = loadedPath, deviceIdentifier = loadedDevice, identifier = localInstance });
            return Task.FromResult<LmStudioDevices?>(new(
                JsonSerializer.SerializeToElement(new { deviceIdentifier = "here" }),
                JsonSerializer.SerializeToElement(new[]
                {
                    new { modelKey = key, path = localPath, deviceIdentifier = "here", sizeBytes = 100, format = "gguf" },
                    new { modelKey = key, path = remotePath, deviceIdentifier = "linked", sizeBytes = 200, format = "gguf" }
                }), JsonSerializer.SerializeToElement(loaded)));
        }
        Task LoadAsync(Uri origin, string path, int? context, CancellationToken token)
        {
            Check(origin.IsLoopback && path == localPath && context == 8192,
                "Mango loads the exact local file and requested context");
            Check(LmStudioDevices.LocalLoadArguments(path, context).SequenceEqual(
                ["load", "--local", "--exact", "--context-length", "8192", "--", path]),
                "the CLI receives the exact local path and uses the provider's chosen name");
            localInstance = key;
            loads++;
            return Task.CompletedTask;
        }
        var adapter = new LmStudioAdapter(fixture, endpoint, InspectAsync, LoadAsync);
        var inventory = (await adapter.InspectAsync(default)).Models;
        var local = inventory.Single(item => item.Origin == ModelOrigin.Local);
        var remote = inventory.Single(item => item.Origin == ModelOrigin.LmLink);
        Check(local.SupportsLoad && local.PreparedConfiguration is not null && local.Inputs.SequenceEqual(["text"]) &&
            !remote.SupportsLoad && remote.Inputs.Contains("image"),
            "each device retains its own capabilities and an exact local copy is loadable");
        await Fails(409, () => adapter.LoadAsync(remote, null, default));

        await adapter.LoadAsync(local, 8192, default);
        inventory = (await adapter.InspectAsync(default)).Models;
        local = inventory.Single(item => item.Origin == ModelOrigin.Local);
        var instance = local.Instances.Single();
        Check(loads == 1 && instance.Id == localInstance &&
            inventory.Single(item => item.Origin == ModelOrigin.LmLink).Instances.Single().Id == linkedInstance,
            "the load produces a verified local instance while the linked copy remains resident");
        loadedDevice = "linked";
        await Fails(503, () => adapter.ConfirmDeploymentAsync(InferenceOperation.Chat, local, instance, default));
        loadedDevice = "here";
        loadedPath = remotePath;
        await Fails(503, () => adapter.UnloadAsync(local, default));
        Check(unloads == 0, "an instance with a different file cannot be used or unloaded");
        loadedPath = localPath;
        await adapter.UnloadAsync(local, default);
        Check(unloads == 1 && (await adapter.InspectAsync(default)).Models.Single(item => item.Origin == ModelOrigin.LmLink)
            .Instances.Single().Id == linkedInstance, "explicit local unload leaves the linked instance alone");

        linkedInstance = key;
        await adapter.LoadAsync(local, 8192, default);
        inventory = (await adapter.InspectAsync(default)).Models;
        local = inventory.Single(item => item.Origin == ModelOrigin.Local);
        remote = inventory.Single(item => item.Origin == ModelOrigin.LmLink);
        instance = local.Instances.Single();
        Check(instance.Id == key && instance.Context == 8192 && !local.SupportsUnload && remote.AmbiguousInstance &&
            remote.Instances.Length == 0, "the local native name resolves when both devices report the same settings");
        await adapter.ConfirmDeploymentAsync(InferenceOperation.Chat, local, instance, default);
        await Fails(503, () => adapter.UnloadAsync(local, default));
        Check(unloads == 1 && localInstance == key && linkedInstance == key,
            "an ambiguous unload leaves both the local and linked instances loaded");
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
            .Fits(model.Inputs, model.ClientTools, model.StructuredOutput),
            "reported image input is eligible without a configured override");
        Check((boundedMedia with { Options = boundedMedia.Options with { MediaTokensPerItem = 2048 } }).MediaTokensPerItem == 2048,
            "a larger configured conservative media allowance remains effective");
        var fiveImages = new InferenceShape("sample", InferenceOperation.Chat, ["text", "image"],
            false, false, 49, 8192, 5, 1, 1, null, null);
        Check(fiveImages.Fits(model.Inputs, model.ClientTools, model.StructuredOutput) &&
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
        var retired = transcription with { Model = transcription.Model with {
            Instances = transcription.Model.Instances.Select(instance => instance with { ConfigurationFingerprint = "retired" }).ToArray() } };
        await Fails(409, () => payload.ForwardAsync(retired, adapter, context, default));
        var rejectedBody = (HttpContent)typeof(InferenceRequest).GetField("outbound",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(payload)!;
        await payload.ForwardAsync(transcription, adapter, context, default);
        try
        {
            await rejectedBody.ReadAsByteArrayAsync();
            throw new InvalidOperationException("The rejected multipart body was not disposed before retry.");
        }
        catch (ObjectDisposedException) { }
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
            Check(unreported.Eligible && request.Shape.Fits(["text"], false, false),
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

    private static HttpResponseMessage GenericInstanceInventory() => Response(HttpStatusCode.OK, """
        {"models":[{"key":"native-model","type":"llm","input_modalities":["text","image"],"client_tools":true,
          "loaded_instances":[{"id":"instance-id","busy":false,"config":{"context_size":262144,"parallel":1}}]}]}
        """);

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
