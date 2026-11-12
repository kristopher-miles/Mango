# Phase 2 Provider integration and model catalog

Deliver usable local inference through Unsloth, LM Studio, and llama.cpp, with accurate model identity and management capability. This is the adapter boundary used by every later phase. Depends on phase 1.

Read the shared foundation and PROVIDER-01–07, API-01–05, MODEL-01–08, and CONTEXT-01–04 in [Provider requirements](../requirements/02-providers-and-models.md). Benchmark execution belongs to phase 4.

## Work

1. Add endpoint registration for the three providers, including stored credential references and explicit managed/attached mode. Check recognized local endpoints or the configured address once during setup. Do not scan arbitrary services or install providers. Use the owning host's credential storage; never replicate provider tokens.
2. Implement one concrete adapter per provider, using the common inference shape where it actually matches. Keep management methods provider-specific. The small adapter contract covers inventory, loaded instances/effective limits, supported operations, execute/stream/cancel, load/unload, and permitted shutdown. An unsupported result is better than a dummy successful implementation.
3. For managed LM Studio and Unsloth, use their native loaded-model inventory and unload operations. For llama.cpp, distinguish single-model processes from router-mode management. Track only explicitly authorized processes for launch/stop; never terminate unrelated processes by name. A refusal or absent lifecycle operation remains visible as an attached-mode limitation.
4. Populate the catalog with provider IDs, variants, aliases, exact input/output modalities, supported task/protocol features, installed/loaded/busy state, effective context, and configuration fingerprint. Show theoretical limits separately. Obtain effective context from actual instance properties or require a configured served limit when unavailable.
5. Add alias and capability editing locally. Models can have arbitrary labels with independent ratings; one label never replaces the others. Validate matching modalities for an alias and lightweight embedding dimensions/encoding checks. Allow an explicit deployment or owner-declared family to keep embedding consumers consistent; do not build collection storage or semantic quality verification. Never claim different quantizations share a cache.
6. Add the local inference routes below. Preserve request parameters and streaming framing rather than translating every backend into an invented universal message language. Reject materially unsupported operations clearly. Disable provider-side tool execution; returning tool-call data to the client remains supported.

| Public-shaped route | Required behavior |
| --- | --- |
| `GET /v1/models` | Client-visible aliases; distinguish eligible from merely installed in Mango diagnostics |
| `POST /v1/chat/completions` | Streaming/nonstreaming chat, supported structured output, client-executed tool calls |
| `POST /v1/embeddings` | Input/encoding/dimension compatibility and appropriate limits |
| `POST /v1/images/generations` | Supported synchronous image generation contract |
| `POST /v1/audio/transcriptions` | Supported file upload/transcription contract |
| `POST /v1/audio/speech` | Supported speech output, streamed when the backend supports it |
| `POST /v1/systemone` | The documented System One structured-decision contract; map only an equivalent backend operation |

Confirm exact provider routes and payloads against the installed version during implementation. Decision inference is in scope; automatic `best` routing is not. Do not claim a backend supports a route because another adapter does. If an available backend requires only a deferred job/file workflow for an operation, report that limitation and use a supported synchronous deployment for the required acceptance case; do not silently implement a job service.

## Context and execution path

Check prompt/template/tools/media plus reserved output against the selected instance's served limit. Use an existing tokenizer/count API where necessary, preferably locally or on the selected provider. Do not query every peer to count tokens. Keep estimate provenance and fail clearly rather than silently truncate. Alias selection may later try a different eligible deployment in phase 5.

Reuse HTTP clients/connections, stream response bytes with backpressure, and propagate cancellation. Track load/execution boundaries and timings now as in-memory request information; phase 7 consumes them without a new event framework. Until resource admission is implemented, local bring-up uses one explicitly selected already-loaded model at a time and does not advertise network contribution.

Refresh inventory on setup, local management actions, supported provider events, manual refresh, and relevant stale/error signals. With no changes or work, fallback inventory checks occur no more than hourly. Do not add per-model health polling.

## Minimum checks

- Exercise one actual supported version/profile of each provider for listing, basic chat, streaming cancellation, and its claimed managed unload behavior. Record those versions and unsupported operations in a short support table, not a certification service.
- Use small captured fixtures for alias/modalities, embedding shape, context overflow, backend errors, and parameters that must not be silently lost.
- Exercise one working deployment per required non-chat route; provider absence is a documented incomplete acceptance item, not a reason to invent support.
- Check that native credentials and Mango authority are not leaked into client responses and that remote-shaped requests cannot enable backend tool execution.

## Qualification

These checks used the Mango loopback listener and actual provider processes on Windows. The Release host and provider fixture checks also passed. Fixtures exercise alias conflicts, embedding result shape, context overflow, input and output parameter preservation, SSE framing, rejection of hosted tool and external-provider controls, DPAPI round trips, sanitized errors, and replacement of a resident image load.

| Provider profile exercised | Version | Checks through Mango |
| --- | --- | --- |
| LM Studio managed REST | LM Studio API in installed Bionic 1.1.9 build 6 | Registered the unloaded Qwen2.5-0.5B-Instruct Q4_0 GGUF, applied a 2,048-token instance limit, ran chat and disconnected after the first SSE event, then observed native unload. The backend log recorded that it stopped generation on disconnect. |
| Unsloth Studio API-only | Unsloth 2026.11.4 | Resolved an installed local GGUF through its native inventory handle, loaded it at 2,048 tokens, ran Qwen chat, then observed zero loaded models after native unload. |
| Unsloth run managed | Unsloth 2026.11.4 with server-side tools disabled | Reported the loaded Qwen model and effective 2,048-token context, ran chat, disconnected after the first SSE event, and confirmed native unload. Kokoro through audio.cpp returned RIFF WAV and Qwen3 ASR transcribed that upload. SDXL Turbo through Diffusers 0.42.1 and PyTorch 2.15.0 CPU returned a synchronous 512×512 PNG as inline base64. |
| llama-server single model and router | b12292, commit `af6b5243d` | Managed single-model launch, chat, SSE disconnect with an observed idle slot, and owned-process stop passed. Router load, context, chat, unload, and native SSE inventory updates on external load/unload passed. |
| llama-server embeddings and decisions | b12292, commit `af6b5243d` | Qwen3-Embedding-0.6B returned batches of 1,024-element float vectors and base64 vectors. LiquidAI d1-omni-600M returned a named System One decision. |

The single-model embeddings profile handles versions that report an embedding model as text in their general model list. Mango reads the actual served context and embedding dimensions when the server reports them. LM Studio embedding deployments can supply missing dimensions and a served limit locally. Text requests use provider token counts where available and a conservative estimate otherwise. Models that lack a reliable media token counter require a configured per-item budget for inline chat or decision media.

Loaded models stay visible when busy. The local bring-up API serves the explicitly selected loaded deployment, with one active request at a time. A client disconnect is forwarded upstream. If the provider cannot confirm that generation stopped, Mango waits for a verified unload or an idle observation before accepting another local submission. Native lifecycle or format support varies by model and installed engine. Use provider controls to load media that needs an engine-specific recipe, and refresh Mango's catalog to observe it. Mango uses the native resident image identity to guard generation and unload.

LM Studio's supported Mango routes are chat, embeddings, and System One on suitable installed models. Its chat profile uses `max_tokens`. Llama chat translates `max_completion_tokens` to the same total generation limit through `max_tokens`. Unsloth supplies the tested synchronous image and audio contracts. The qualified speech deployment returns whole-file audio. Transcription accepts the file, model, language, response format, and timestamp granularities supported by the Unsloth profile. Llama router and single-model profiles expose the operations their inventory establishes.

Attached providers retain their lifecycle authority. Mango directly stops only the provider process it launched. Provider-local output links, persistent jobs, and provider-executed tools are outside this phase's API.

## Done

The same local UI can register the three providers and present their actual models and capabilities. Required protocols work on suitable installed deployments. The adapter contract is small enough that another provider can be added as one adapter and relevant fixtures, without changing the scheduler. Memory profiling, global catalog edits, public exposure, model downloads, and `best` selection are not added here.
