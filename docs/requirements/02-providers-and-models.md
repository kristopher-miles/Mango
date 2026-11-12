# Mango provider and model requirements

Mango routes requests through existing serving infrastructure. Each adapter must describe what its particular server can actually do, including lifecycle control and limits. Sharing an HTTP request format does not make management interfaces interchangeable.

## Provider integration

**PROVIDER-01 — C.** Ship support for Unsloth, LM Studio, and llama.cpp. Additional providers must be addable without changing the scheduler's fundamental rules. Version-specific capabilities must be explicit and testable.

**PROVIDER-02 — C, Q10.** Adapters declare supported inventory, authentication, limits, inference/streaming, cancellation, lifecycle, concurrency, health, and resource operations. In managed mode, use Unsloth and LM Studio interfaces to inspect loaded models and request unloading. Verify observed release rather than treating a successful command as proof of freed memory. Unsupported operations remain explicit.

**PROVIDER-03 — C, Q09.** Distinguish managed providers, for which the local owner grants Mango lifecycle authority, from attached providers, for which Mango can only use an existing endpoint. Automatic resource reclamation requires managed authority and a demonstrated release path. An attached server with no unload/stop permission cannot be presented as fully reclaimable.

**PROVIDER-04 — I.** Only the host of a provider executes its local process controls. Identify processes by durable ownership evidence rather than killing all processes with a matching name. Never terminate an unrelated server, provider GUI, training task, or user session to reclaim inference memory. Detect when another controller changes the same server and refresh state.

**PROVIDER-05 — C.** Support endpoint and model configuration from any authorized node's GUI. The owning node validates and applies the change. Changes needing local credentials, new executable access, or expanded shutdown authority must be completed locally, consistent with local resource-control ownership.

**PROVIDER-06 — I.** Discover only configured or recognized local endpoints and user-added endpoints within the approved boundary. Do not run arbitrary URLs, shell commands, or adapter code supplied by an inference client. Store provider tokens on the owning node; shared configuration can show a credential reference and validity state, not the secret.

**PROVIDER-07 — C, Q10.** Qualify Unsloth Studio/API and the `unsloth run` serving profile explicitly, LM Studio's native management API plus inference API, and supported llama-server single-model and router profiles. Record the exact tested versions during implementation rather than declaring every release compatible. An exported Unsloth model served by llama.cpp uses the llama.cpp adapter, not a separate imagined protocol.

## Public inference contracts

**API-01 — C.** Support text, image, audio, structured decisions, and extension to new inference protocols. Embeddings are included because they appear in the scheduling examples. Scope authorization by actual operation and payload, not only by URL or model alias.

**API-02 — C, Q10.** The first compatibility contract should include model listing, chat completions with streaming and client-executed tool calls, embeddings, image generation, audio transcription, speech generation, and one named structured-decision API. Expose only features supported by the chosen deployment. Full Responses, Messages, video, realtime duplex audio, and other contracts are additional explicit coverage decisions, not implied by the phrase OpenAI-compatible.

**API-03 — I.** Preserve supported request parameters, response semantics, structured output, usage accounting, cancellation, and streaming error behavior. Reject an unsupported parameter or endpoint clearly when dropping it would change meaning. Do not pretend that a plain chat response implements a structured-decision protocol.

**API-04 — I.** An adapter declares whether a request is synchronous, streamed, or a job with later result retrieval; what state or file handles it creates; and how cancellation and cleanup work. Provider-local IDs are namespaced and routed back to their owner. A server-held conversation or media handle cannot move to another endpoint unless the required state is transferable.

**API-05 — C, Q11.** Permit model-generated tool calls that the client executes, but disable provider-side code execution, web tools, and MCP execution through Mango by default. A localhost backend may otherwise interpret a proxied request as locally trusted. Any future server-side tool permission must be separate from inference permission and reviewed at the owning node.

## Catalog and aliases

**MODEL-01 — C.** Detect hosted models from their endpoints. Allow the user to assign an alias, capability labels, and a rating for each label. A request using an alias is served by an eligible deployment registered under that alias. Translate the alias to the provider's real identifier at the boundary.

**MODEL-02 — C.** The same alias may represent different quantizations, formats, machines, or even unrelated models of different quality. Validate that deployments sharing it accept the same input modalities and produce the same output modalities. Do not validate honesty of names or ratings, model intelligence, or equivalence of weights.

**MODEL-03 — I.** Retain each deployment's real provider identity, artifact identity where available, loaded-instance identity, actual context limit, technical features, performance, and memory profile. An alias is a grouping label, not a reason to average away hard limits or copy another instance's cache claim.

**MODEL-04 — I.** Separate user task ratings from protocol support. A model rated highly for coding is not thereby proven to support tools, structured JSON, image input, or a particular decision schema. Request-specific features must be matched against the deployment's actual support.

**MODEL-05 — P, Q12/Q28.** Use shared capability labels with deployment-specific integer ratings from 0 to 5; leave unrated distinct from 0. Allow bulk assignment to an alias, with individual overrides. Enforce exact input/output modality sets for shared aliases initially. Broader subset matching is a later policy choice.

**MODEL-06 — C, Q12.** Add a technical output-contract check for embeddings, such as vector dimensions and encoding. This is an explicit extension to the user's modality-only validation rule. Even equal-length embeddings from different models need not share a meaningful vector space: warn about mixing them, and pin an embedding collection to an explicit deployment or owner-declared compatible family. Do not claim to verify semantic equivalence.

**MODEL-07 — I.** Alias changes are revisioned and conflict-checked. An in-flight request retains its resolved deployment and original alias revision. Renaming a model cannot retarget a request midstream. When another node is offline, the UI shows that the change is pending there.

**MODEL-08 — I.** Distinguish discoverable, eligible, and immediately available models in listing and UI. A busy model remains in the catalog; a leech advertises no executable capacity. Inaccessible or unverified context information must not be represented as an unlimited context.

## Served context and input fit

**CONTEXT-01 — C.** Detect the maximum context actually served by a configured instance, not only the model's theoretical training limit. Store theoretical and effective limits separately when both are known. Recheck effective limits after reloads and server configuration changes.

**CONTEXT-02 — I.** For generation, fit the encoded prompt, template and system overhead, tool definitions, multimodal tokens, and reserved output into the effective context. Count with the destination's tokenizer or an authoritative provider count when available. Different deployments under one alias may tokenize the same input differently.

**CONTEXT-03 — I.** Do not fill the entire context with input and then expect output to fit. A full-context benchmark means total input plus its reserved output reaches the usable limit. For embedding and media endpoints, use the appropriate input, batch, duration, resolution, or output limits rather than forcing a text context concept onto them.

**CONTEXT-04 — C, Q13.** If an adapter cannot discover a reliable effective limit, require a user-supplied served limit or a bounded verification procedure before advertising that capacity. Mark its provenance as reported, measured, or user-configured. Token estimates must be conservative, and the serving node retains final validation. Never silently truncate a request to make it fit.

## Benchmarking and resource profiles

**BENCH-01 — C.** Measure short, approximately half-context, and full-served-context requests for text-like models where those tests make sense. Measure system resource use during model loading and execution so the scheduler can judge safe overlap. Do not apply these three prompt lengths indiscriminately to image or audio endpoints.

**BENCH-02 — I.** Record load time, prompt processing time or rate where exposed, time to first token, generation rate, completion time, peak RAM, peak VRAM per device, and configuration. Distinguish cold loading, warm execution, and actual cache reuse. Record unsupported or unobserved metrics as unknown, never zero.

**BENCH-03 — I.** A model's resource cost includes weights, context/KV cache, concurrency, temporary buffers, media encoders or projectors, runtime overhead, and transient load peaks. Record the request size and simultaneous work used for each measurement. One isolated load is insufficient evidence that two models at full context can safely overlap.

**BENCH-04 — C, Q13.** At enrollment, select a local benchmark policy: Never, Now, or When convenient. The choice also covers later models/providers without another consent prompt. Now blocks all serving on that node until required benchmarks finish; failures remain visible until resolved or the owner changes policy. When convenient starts a benchmark after five minutes without client work on that node, independent of network-wide activity; any incoming client request interrupts it. Never skips automatic benchmarking and uses conservative resource profiles. Retain synthetic workloads, progress, cancellation, and short/middle/full tests where applicable. A local policy change is allowed without reenrollment.

**BENCH-05 — I.** Bind measurements to the artifact, quantization, runtime version, device configuration, context size, cache configuration, concurrency, and relevant adapter version. Material changes invalidate affected confidence. Rebenchmark on relevant changes or user request, not every startup.

**BENCH-06 — P, Q14.** Allow overlapping deployments only with adequate resource evidence and a current admission check. Use conservative non-overlap for unmeasured combinations; permit a local owner to configure a qualified profile. Do not benchmark every possible combination combinatorially, and do not extrapolate a small-context peak into a claim of full-context safety.

## Required catalog information

This is a logical information contract, not a storage schema.

| Record | Required information |
| --- | --- |
| Provider endpoint | Owner node, adapter and version, endpoint, credential reference, management authority, health, available operations |
| Deployment | Provider model ID, alias, artifact/variant where known, modalities, supported tasks/features, limits, installation/load state |
| Loaded instance | Instance ID, runtime/configuration fingerprint, effective context, devices, slots/concurrency, observed memory, cache/state support |
| Capability assessment | Label, per-deployment rating, editor, revision; explicit missing rating |
| Benchmark | Workload dimensions, cold/warm/cache condition, timings, memory peaks, confidence/provenance, environment, timestamp |
| Availability | Contributor state, accepting/busy/withdrawn, bounded capacity hint, observation revision and freshness |

### Research implications

LM Studio distinguishes loaded-instance context configuration from the model maximum and provides a native management API. llama-server's supported mode and version determine management and event facilities. Unsloth documents an API, including multimodal and decision routes, whose availability depends on version, backend, and model. These are reasons to probe capability rather than infer it from a provider's name. See the source summaries in [Research](07-research-and-recommendations.md).
