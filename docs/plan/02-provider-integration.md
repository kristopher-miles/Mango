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

## Done

The same local UI can register the three providers and present their actual models and capabilities. Required protocols work on suitable installed deployments. The adapter contract is small enough that another provider can be added as one adapter and relevant fixtures, without changing the scheduler. Memory profiling, global catalog edits, public exposure, model downloads, and `best` selection are not added here.
