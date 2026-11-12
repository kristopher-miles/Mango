# Mango research and recommendations

Historical research from the original draft. Some recommendations were rejected or changed. [Recorded decisions](09-recorded-decisions.md) and the revised requirements govern, including eventual reconciliation, local cached authorization, and cutoff-based compatibility.

The sources below are official project documentation, source documentation, and security guidance checked on November 11, 2026. They establish external constraints. Mango's proposed behavior and numerical defaults are engineering recommendations, not results measured by these sources. Provider support must eventually be qualified against specific released versions.

## PAIR and the product motivation

NVIDIA documents PAIR as routing independent requests between machines on the same local network. Its getting-started guide names Ollama, LM Studio, and llama.cpp, and explicitly says it does not pool GPU memory or split a request across systems. The guide also describes individual node updates. These facts support the comparison with Mango's WAN-first, extensible direction. They do not prove that PAIR has excessive idle resource use or that WAN operation is technically impossible. Those remain the user's experience and motivation, not a benchmark conclusion. [NVIDIA PAIR getting started](https://docs.nvidia.com/local-ai/nvpair/getting-started/)

**Recommendation:** make Mango's idle footprint and message rate measurable acceptance targets, and retain whole-request routing as the product boundary. Avoid making unmeasured superiority claims.

## Tailscale integration and public access

`tsnet` embeds Tailscale inside a Go application using a userspace network stack. It creates its own tailnet device identity and supports interactive authentication or authentication keys. Embedding can simplify packaging, but does not eliminate account sign-in, device approval, or network policy. [Tailscale tsnet](https://tailscale.com/docs/features/tsnet)

**Recommendation:** start with an installed Tailscale client, pending Q02. Evaluate embedding against the chosen application stack and measured total footprint later; do not select the whole stack merely because an embedding library exists.

Tailscale can use direct paths or relays when direct connectivity cannot be established. Relays may increase latency and reduce throughput, so routing cannot assume all tailnet peers behave like LAN hosts. [Tailscale connection types](https://tailscale.com/docs/reference/connection-types)

**Recommendation:** keep discovery and metadata compact, stream inference, use bounded timeouts, and account for actual request transfer costs. Text-only requests can still include long contexts; audio and image payloads can be much larger.

Funnel supplies public TLS access, with restrictions on tailnet domain names, listening ports, platforms, and bandwidth. It is a distinct public exposure mechanism, not a consequence of ordinary tailnet connectivity. [Tailscale Funnel](https://tailscale.com/docs/features/tailscale-funnel)

**Recommendation:** treat Funnel as an optional convenience, with a general HTTPS reverse-proxy path. Mango must authenticate inference independently and keep its administrative endpoints private. Q03 determines supported packaging.

Tailscale grants express access between sources and destinations at network and application levels. Mango still has to implement its own application permission semantics. [Tailscale grants](https://tailscale.com/docs/features/access-control/grants)

**Recommendation:** scope tailnet connectivity to Mango services instead of requiring unrelated host services to be reachable. A successful network connection is not sufficient Mango enrollment or inference-key authority.

The Tailscale local client API exposes an IPN notification watch. Its documentation marks that watch API as unstable, while identifying `WhoIs` as stable. [Tailscale local client API](https://pkg.go.dev/tailscale.com/client/local)

**Recommendation:** prefer events where supported, but qualify versions and retain a bounded fallback. Do not promise a portable, permanent discovery-event contract before checking the supported platform combinations.

## Provider findings

| Provider | Verified finding | Consequence for Mango |
| --- | --- | --- |
| LM Studio | Its native API includes listing, loading, and unloading, separately from compatibility inference endpoints. [API overview](https://lmstudio.ai/docs/developer/rest) | Use its management API for lifecycle control; a shared inference format alone is insufficient. |
| LM Studio | Model records distinguish loaded-instance `config.context_length` from `max_context_length`. [List models](https://lmstudio.ai/docs/developer/rest/list) | Use effective instance settings when checking fit. |
| LM Studio | Model loading can return the final applied configuration and load time. [Load a model](https://lmstudio.ai/docs/developer/rest/load) | Verify actual load configuration instead of trusting only the requested value. |
| llama.cpp | Current llama-server documentation includes context/slot information, prompt-cache controls, and router-mode model management and events. [llama-server documentation](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md) | Qualify mode and version. Cache reuse belongs to a compatible instance; management support is not universal across older builds. |
| Unsloth | Its API guide documents chat, embeddings, image, audio, and structured decisions, conditional on version/backend/model. It also documents server-side tools and their bind-dependent defaults. [Unsloth API guide](https://unsloth.ai/docs/basics/api) | Integrate real serving profiles and probe operations. Explicitly disable host-executed tools when proxying remote inference into a localhost backend. |

**Recommendation:** retain separate provider, model artifact, alias, and loaded-instance records. Approve a concrete first-release protocol list in Q10. Do not claim every provider supports every modality merely because the Mango catalog can describe it.

## Activity monitoring and memory safety

NVIDIA's NVML process-memory documentation identifies a Windows WDDM case where per-process GPU memory is unavailable through that field. This is a concrete counterexample to assuming one vendor API can provide complete cross-platform attribution. [NVML process information](https://docs.nvidia.com/deploy/nvml-api/620/api/structnvmlProcessInfo__v1__t.html)

**Recommendation:** qualify native telemetry per OS/runtime/device, expose missing measurements, and default to conservative admission. Activity sampling is local work and can be necessary without violating the goal of avoiding repeated network polls. A benchmark is evidence of a workload's observed peak, not a guarantee that all larger workloads or overlapping models will fit.

## Credential and denial-of-service protection

OWASP recommends salted, slow, memory-hard password hashing and identifies Argon2id as a preferred option. That guidance concerns human passwords; Mango must distinguish their handling from high-entropy inference tokens. [OWASP password storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)

**Recommendation:** use vetted identity and secret-storage components, separate password/recovery authority from runtime peer credentials, and review the enrollment design before coding. Do not design security around broadcasting a reusable master secret.

OWASP's API resource-consumption guidance calls for limits on payload size, execution work, memory, and request rates. Rate limits alone do not bound the cost of one oversized request or a lookup that fans out to many peers. [OWASP API resource consumption](https://api-security.owasp.org/editions/2023/en/0xa4-unrestricted-resource-consumption/)

**Recommendation:** combine per-source limits with global bounds, concurrent-work limits, deadlines, and bounded caches. Never propagate an unknown-key query recursively. Limit valid credentials too: authentication does not establish a safe compute budget.

A disconnected node cannot learn a newly issued revocation until communication resumes. This is a constraint of the requested topology, not a provider defect. **Recommendation:** approve a bounded stale-authorization window and a fail-closed public policy in Q07. The proposed 60 seconds is a policy choice, not a guarantee available from push-only daily synchronization.

## Update verification

The Update Framework describes signed metadata, hashes, versions, expiration, and trust roles to resist compromised or stale update distribution. HTTPS transport alone does not supply those update guarantees. TUF concerns update discovery and verification; it does not itself implement Mango's installation rollback. [TUF overview](https://theupdateframework.io/docs/overview/)

**Recommendation:** use an established signed update system and separate compatibility override from authenticity verification. Queue a coordinated release but prefer rolling installation to avoid taking all serving capacity down together. Q26 and Q27 settle rollout and supported interoperability.

## Design constraints that need decisions

| Tension in the requested behavior | Recommended resolution | Decision |
| --- | --- | --- |
| No constant polling, but prompt failure and revocation detection | Events and demand-driven checks, with bounded low-cost exceptions | Q07/Q22 |
| Any peer can edit state, but global join ordering and recovery must be consistent | Initiate from any peer; serialize sensitive commits with explicit partition behavior | Q05/Q06 |
| Existing endpoints, but guaranteed shutdown and reclamation | Explicit managed authority or a clearly limited attached mode | Q09 |
| Same alias means same modalities, but embeddings can silently corrupt a collection | Validate dimensions and warn/pin semantic compatibility without rating quality | Q12 |
| Scarce models always win, but request priority must also govern admission | Keep priority constraints hard; use scarcity among equal-priority contenders | Q17 |
| Keep queues short, but do not starve a scarce large request | Reserve its next feasible start and bound backfilling | Q18 |
| Cache affinity, but independent requests should run in parallel | Session/prefix affinity, not key-wide or IP-wide pinning | Q19 |
| Exact verbose logging, but credentials must not be copied into daily archives | Exact inference payloads with explicit credential exclusions | Q24 |
| Update together, but preserve usable inference capacity | Coordinate intent and queueing, then roll installations | Q26 |

These recommendations should be accepted, modified, or rejected in the single question set. No protocol, consensus library, GUI framework, database, updater, or programming language is selected here.
