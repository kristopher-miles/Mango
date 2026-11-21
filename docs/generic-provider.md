# Generic provider integration for Mango

A hosting program can serve as a Mango provider by exposing its installed models, loaded instances, and lifecycle operations over HTTP. Use LM Studio's native REST v1 routes for management and the inference route that matches each model. The smallest useful host supports two chat models and one resident model at a time. [LM Studio list models](https://lmstudio.ai/docs/developer/rest/list), [load](https://lmstudio.ai/docs/developer/rest/load), [unload](https://lmstudio.ai/docs/developer/rest/unload)

## Connect the host

Run the provider independently on the Windows machine that owns the models. Bind it to a loopback origin such as `http://127.0.0.1:12345` and serve the routes below at the origin root. Mango accepts loopback HTTP or HTTPS and requires HTTPS for a non-loopback origin. When the provider requires authentication, accept the same `Authorization: Bearer <token>` header on inventory, lifecycle, and inference requests. Mango stores the token on the owning node. Use an HTTP server that accepts chunked request bodies as well as bodies with `Content-Length`. Mango can send HTTP/1.1 requests using `Transfer-Encoding: chunked`.

In Mango, open **Providers and models**, choose **Generic endpoint**, enter the origin and any bearer token, then choose **Managed** and save. **Attached** permits inventory and inference on loaded models. The hosting program owns its process and exposes model load and unload over HTTP.

Under the [serving boundary](requirements/09-recorded-decisions.md#serving-boundary--november-21-2026) pending rollout, the provider owns capacity for lone-model startup and valid inference. Enable **Contributor** on the owning node. Use **Fallback served context** to request a native context at load when needed. Report the actual effective context for routing. Keep local workers attributable to the provider when possible so Mango can distinguish inference and benchmarks from the user's other activity.

Before inference, Mango revalidates the selected Generic instance against current inventory. If it has retired or changed, Mango releases the old reservation and keeps the admitted request resident while refreshing and scheduling again within its original admission deadline. Requests already submitted to inference are never replayed through this recovery path.

## List installed models

`GET /api/v1/models` returns the complete inventory as JSON, including installed models whose `loaded_instances` array is empty. Use a stable, case-sensitive `key` for each model or independently loadable variant. Give each new loaded instance its own opaque, case-sensitive `id` and keep it stable for that load. Use the same instance ID in load responses, inventory, unload requests, and inference. A new load needs a fresh ID, including after server restart, so a stale request cannot target its replacement.

```json
{
  "version": "example-host-1",
  "max_loaded_models": 1,
  "models": [
    {
      "key": "sample-a",
      "type": "llm",
      "display_name": "Sample A",
      "max_context_length": 8192,
      "loaded_instances": [
        {
          "id": "sample-a-1",
          "busy": false,
          "config": { "context_length": 4096, "parallel": 1 }
        }
      ]
    },
    {
      "key": "sample-b",
      "type": "llm",
      "loaded_instances": []
    }
  ]
}
```

| Field | Contract |
| --- | --- |
| `models` | Required array with up to 256 installed models. Use an empty array for a healthy host with zero installed models. |
| `key`, `type`, `loaded_instances` | Required on each model. Keys must be unique and nonempty. Instance IDs must be nonempty and unique across the endpoint. |
| `max_loaded_models` | Optional Mango extension. Set it to the maximum number of distinct model keys that may be resident at once, from 1 to 256. Set it to `1` on a single-model host so Mango can schedule an unload before a cold load. |
| `loads_settled` | Optional boolean. Return `true` only when the same synchronized inventory snapshot confirms that no load is executing, queued, or still running in a detached worker. Omit it or return `false` if any load state is unknown. Mango uses affirmative evidence alongside an empty instance list to reconcile a load restored after restart. |
| `config.context_length` | Report the effective context in tokens for each loaded instance. Mango can use the owner's configured fallback when the field is absent. `max_context_length` is the model's theoretical maximum. |
| `config.parallel` | Optional positive number of inference slots. Mango also applies its own concurrency and memory policy. |
| `busy` | Optional Mango extension. Report `true` while the instance is processing work and `false` once that work has stopped. Mango can use an explicit idle observation after a cancelled request. With the field omitted, Mango may need to observe an unload before clearing uncertain work. |
| `version`, `selected_variant`, `quantization.name`, `format`, `size_bytes` | Optional runtime and artifact metadata. Update the relevant metadata when weights, runtime, or configuration change so Mango can identify the changed deployment. |

Serve a complete, current snapshot on every successful inventory request. Keep resident instances visible while they are busy. If inventory is temporarily unknown, return `503` until it can report authoritative state. Mango treats a successful empty instance list as evidence that the model is unloaded. Inventory has a ten-second timeout and an 8 MiB response limit.

## Load, unload, and swap

| Request | Behavior | Example success response |
| --- | --- | --- |
| `POST /api/v1/models/load` with `{"model":"sample-a","context_length":4096,"echo_load_config":true}` | Load the installed model by key. Use the server default when `context_length` is omitted. Accept `echo_load_config` and report the effective settings in inventory. Publish the new instance once it can accept inference. | `{"type":"llm","status":"loaded","instance_id":"sample-a-1","load_config":{"context_length":4096}}` |
| `POST /api/v1/models/unload` with `{"instance_id":"sample-a-1"}` | Unload that exact instance. Remove it from inventory once it has stopped and released model memory. Keep the installed model in the list. | `{"instance_id":"sample-a-1"}` |

Serialize changes to resident state with inference admission. Reject a load when capacity is full or an unload when the instance is active with `409`. Use `404` for unknown keys or instance IDs, `400` or `422` for invalid settings, and `507` if the engine cannot allocate model memory. Repeated requests must be safe. In particular, never unload a replacement using a retired instance ID, and never silently evict a model to honor a new load. If a control request disconnects, cancel the transition or finish it and keep inventory authoritative about the resulting state.

Mango allows up to five minutes for each lifecycle response, then polls inventory for up to 40 seconds to confirm the result. A full single-slot provider can make work reroute or wait. The [YOLO residency policy](yolo.md) governs request-driven unloads. An explicit swap can use **Unload model** followed by **Load model**. Use **Refresh catalog** after direct provider changes.

## Serve inference by instance ID

Mango accepts the provider model identifier or a user-set alias from the client and sends the loaded instance's `id` as the request's `model`. Resolve that exact instance. Return an HTTP error if it is gone, busy, or incompatible. Keep just-in-time loading and automatic eviction disabled on these routes, and stop active inference promptly when its HTTP connection is cancelled. Keep provider-executed tools, web access, and MCP execution disabled for these requests.

| Inventory `type` | Inference contract |
| --- | --- |
| `llm` | `POST /v1/chat/completions`. Support normal JSON completions and streamed chunks with `Content-Type: text/event-stream` ending in `data: [DONE]`. Use `max_tokens` for the output limit. Return an error when a request cannot fit. |
| `embedding` | `POST /v1/embeddings`. Return one indexed float vector per input, all the same length within a batch. `embedding_dimensions` is optional metadata. Mango forwards a client's `dimensions` parameter to the provider. |
| `decision` | `POST /v1/systemone`. Accept System One `state` and named `questions`, and return the corresponding `answers`. |
| `image` | `POST /v1/images/generations`. Accept a text `prompt` and `response_format: "b64_json"`. Mango forwards options such as `size` and `steps` using those field names. Return synchronous image bytes encoded in `data[].b64_json`. |
| `transcription` | `POST /v1/audio/transcriptions`. Accept a multipart `file` and `model` instance ID, plus supported language, response format, and timestamp options. Return the requested text or structured transcription. |
| `speech` | `POST /v1/audio/speech`. Accept text `input`, a supported `voice`, and the requested `response_format`. Return the audio bytes with the matching content type. |

The three media types use the same inventory and lifecycle operations. Leave **Fallback served context** blank for them. The provider enforces its image and audio capacity. Give each independently loadable operation its own model key, even when two operations share weights.

Only advertise features that work on the corresponding inference route. Set `capabilities.vision` or `has_vision_adapter` for inline image input, `capabilities.trained_for_tool_use` for client-executed function calls, and `capabilities.structured_output` for structured chat output. For chat benchmarks, include usage with `prompt_tokens` and `completion_tokens` when requested with `stream_options.include_usage`. [Chat completions](https://lmstudio.ai/docs/developer/openai-compat/chat-completions), [embeddings](https://lmstudio.ai/docs/developer/openai-compat/embeddings), [System One](https://lmstudio.ai/docs/developer/jev-compat/systemone)

## Run the reference endpoint

From the repository root, run the [loopback fixture](../tests/Mango.Tests/GenericProviderChecks.cs) with the SDK in `global.json`. It serves two fake chat models with one shared slot and prints its origin. Register it as a **Generic endpoint** using the sample bearer token `mango-fixture-token`.

```powershell
dotnet run --project tests/Mango.Tests -c Release -- --generic-provider-fixture
```

To exercise a scheduled swap on the single-slot fixture, configure the two sample models with a 4096-token native served context. Load `sample-a`, then send a request for `sample-b` to Mango's `/v1/chat/completions`. With no eligible alternate, the request waits for active work on `sample-a` to finish, confirms its unload, and loads only `sample-b`. Use a scoped key or the authorized local session.

To exercise the HTTP contract directly, substitute the printed origin for `<origin>` and run this against a fresh fixture.

```powershell
$origin = '<origin>'
$headers = @{ Authorization = 'Bearer mango-fixture-token' }
function Invoke-Lifecycle($action, $body) {
    Invoke-RestMethod "$origin/api/v1/models/$action" -Method Post -Headers $headers `
        -ContentType application/json -Body ($body | ConvertTo-Json)
}

Invoke-RestMethod "$origin/api/v1/models" -Headers $headers
$first = Invoke-Lifecycle load @{ model = 'sample-a'; context_length = 4096; echo_load_config = $true }
Invoke-Lifecycle unload @{ instance_id = $first.instance_id }
$second = Invoke-Lifecycle load @{ model = 'sample-b'; context_length = 4096; echo_load_config = $true }
Invoke-RestMethod "$origin/v1/chat/completions" -Method Post -Headers $headers `
    -ContentType application/json -Body (@{
        model = $second.instance_id
        messages = @(@{ role = 'user'; content = 'Say hello.' })
        max_tokens = 32
    } | ConvertTo-Json -Depth 5)
Invoke-Lifecycle unload @{ instance_id = $second.instance_id }
```

Run `dotnet run --project tests/Mango.Tests -c Release` to check discovery, load context, a scheduled single-slot swap, streaming, restart, and attached mode against the same fixture.
