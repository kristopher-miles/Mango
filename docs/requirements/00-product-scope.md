# Mango product scope

Mango shares inference capacity across the user's machines without requiring a particular inference engine or a common local network. It routes whole requests to existing servers, protects each machine's local workload, and exposes a consistent catalog and access policy.

## Product requirements

**PRODUCT-01 — C.** Support any number of installed peers as a product concept. Each peer can access inference resources across the enrolled Mango network. The supported and tested scale must be stated explicitly; the product cannot promise unlimited capacity or cost-free replication.

**PRODUCT-02 — C.** Make Tailscale the network foundation from the beginning. Machines may be on different physical networks. Users authenticate with Tailscale and separately enroll into the intended Mango network.

**PRODUCT-03 — C.** Make nodes lightweight, favor pushed changes and demand-driven requests, and avoid constant network polling. A contributor may become a leech manually or automatically when other activity needs its resources.

**PRODUCT-04 — C.** Connect to Unsloth, LM Studio, and llama.cpp out of the gate. Keep additional providers and inference protocols extensible. Text, image, audio, embeddings, and structured decisions belong in the product scope; the initial protocol scope follows the accepted Q10 decision.

**PRODUCT-05 — C.** Prefer serving as many requests as possible with the available nodes, then request priority, then first in first out. Preserve the explicit low-priority rejection and queue eviction rules. Apply the accepted resource-specific purge and scarce-placement exception from Q16–Q18.

**PRODUCT-06 — C.** Provide a responsive GUI on every machine. It can configure shared model endpoints, aliases, capabilities, and ratings across the network. Resource protection settings remain local to the machine they protect.

**PRODUCT-07 — C.** Support scoped, individually revocable inference keys; optional internet HTTPS serving; coordinated software updates; configurable logging; and daily or on-demand network log aggregation.

**PRODUCT-08 — F.** Reserve the future `best` routing feature described in its own document. Its model-selection logic is not part of the initial implementation.

## Terms

| Term | Meaning |
| --- | --- |
| Mango network | An application trust group with its own identity and credentials; it is not automatically the whole tailnet. |
| Peer or node | One enrolled Mango installation, identified independently of hostname and IP address. |
| Join ID | A persistent, ordered enrollment identifier. Its order determines the oldest eligible log collector. It is not a routing priority. |
| Contributor | A node allowed to accept inference work on its local resources. |
| Leech | A node that may submit and route requests but contributes no inference compute. |
| Ingress node | The Mango node a client connects to. It retains the client connection and routes work. |
| Serving node | The node that admits a request to its execution queue and controls the local provider. |
| Provider | An existing inference server and its management interface, such as LM Studio. |
| Model artifact | A particular set of weights, quantization, tokenizer, and associated files. |
| Model instance | A model loaded with a particular context, device allocation, runtime, and concurrency configuration. |
| Model alias | The client-visible name that may group compatible deployments, even of different quality or quantization. |
| Capability | A user-defined task label such as coding, writing, or classification. This differs from a technical feature such as image input or tool-call output. |
| Rating | A user assessment of a deployment's strength at one capability; it is not a verified benchmark score. |
| Modality | A type of input or output, such as text, image, audio, or embedding vectors. |
| Eligible destination | A contributor satisfying the request's authorization, protocol, modalities, context, and resource requirements. |
| Open capacity | Capacity available to admit execution now within safety limits, including the chosen policy for loading an unloaded model. It does not mean merely that a model is installed. |
| Affinity | A preference to return related requests to the same provider instance to reuse its cache or state. |
| Queue | Work admitted to wait at a serving node. An ingress forwarding record is not a second execution queue. |

## Settled interpretations

**SCOPE-01 — I.** The network distributes independent requests. It does not combine GPU memory across machines, split one model across the WAN, or transfer live model execution between unlike runtimes. A backend may itself use multiple local GPUs; Mango treats that as one serving deployment.

**SCOPE-02 — I.** Peer equality does not require a permanently running central inference server. The first node initializes the network; it need not stay alive to route inference. A temporary coordination role for shared metadata or logs does not grant ownership of other nodes' local controls.

**SCOPE-03 — I.** Tailnet membership alone does not grant Mango membership. Conversely, Mango enrollment does not bypass Tailscale policy or expose unrelated services on another machine.

**SCOPE-04 — I.** A leech still receives client requests, control messages, and catalog updates. It must remain usable as an ingress and as a GUI. Shutting down inference does not mean shutting down Mango or Tailscale.

**SCOPE-05 — I.** Model availability has several states: installed but not loaded; loading; loaded and available; loaded and busy; unloading; disabled; unreachable; and failed. A stale advertisement is a routing hint, not permission to exceed the serving node's current resources.

**SCOPE-06 — I.** Aliases do not certify model quality, identical tokenization, identical embeddings, or shared caches. GGUF and MLX deployments can share a name while retaining separate measurements and provider identities.

**SCOPE-07 — I.** Resource-driven relocation usually changes where queued work or the next request runs. Completing an active request before unloading, as in the supplied example, is distinct from aborting it because local user activity takes precedence.

**SCOPE-08 — C, Q05.** Each network belongs to one user. Enrolled peers are trusted and assumed nonmalicious; stale state and failures remain expected. Avoid infrastructure designed to reach consensus among hostile peers. Public or unknown inference callers are outside this trust assumption. A serving peer necessarily sees the requests it serves.

## Scope boundaries

**SCOPE-09 — C, Q09/Q10.** Integrate installed providers and models first. Do not add a model marketplace, training, automatic weight downloads, weight distribution, billing, or a general remote shell. Starting or stopping an explicitly managed provider is within scope. Future protocol adapters must not turn ordinary inference access into arbitrary host administration.

**SCOPE-10 — I.** Publishing a public inference endpoint is optional and separate from creating a key. A key does not create a DNS name, issue a TLS certificate, open a firewall, or configure a public proxy.

**SCOPE-11 — I.** Requirements may later be implemented in stages. No stage order, sprint breakdown, technology selection, or implementation estimate is approved by this draft.

## Accepted performance targets

**NFR-01 — C, Q01.** Qualify Windows first for the GUI and contributing Mango service. Defer macOS and Linux peer implementations to later steps. API clients may use any operating system and submit through any enrolled ingress. Initially test 1–20 peers and 100 advertised deployments; qualify provider and telemetry combinations individually.

**NFR-02 — C, Q01.** With 20 peers, no inference, GUI closed, and an unchanged catalog: target less than 1 percent of one CPU core averaged over 15 minutes and at most 150 MiB resident memory for Mango's background service. Measure the GUI, Tailscale, providers, and optional embedded networking separately and also report the total installed idle footprint. Do not hide a bundled dependency's cost.

**NFR-03 — C, Q01/Q22.** Unchanged idle networks should generate zero Mango application traffic most of the time. Keep the 1 KiB/s average ceiling as a maximum, not a target traffic rate. Do not maintain a continuously current online-peer map. Allow pushed changes, daily reconciliation, at most hourly idle inventory fallback, and genuine current coordination. Measure Mango traffic separately from Tailscale transport maintenance.

**NFR-04 — C, Q01.** Target local GUI feedback within 200 ms for ordinary actions; show progress for network-dependent operations. Target less than 25 ms p95 Mango admission and routing overhead on a local healthy path, excluding authorization refresh, provider work, model loading, queue time, and WAN transit. Report these exclusions with measurements.

**NFR-05 — I.** Bound queues, connection buffers, cached request state, pending synchronization, unknown-key lookups, and logs. Saturation must produce a defined failure rather than unbounded RAM use or silent data loss.

**NFR-06 — I.** Timing claims must be measured on named hardware and scenarios. Reachability, revocation, and metadata freshness guarantees must name their behavior under sleep, packet loss, partitions, and clock changes.
