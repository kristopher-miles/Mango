# Mango acceptance and specification coverage

These are future acceptance scenarios, not tests that have already run. Confirmed and inferred behavior can guide later test design. Scenarios containing proposed policy remain conditional on the referenced decision. No live providers, GPUs, public ingress, or multi-machine topology were exercised while preparing these documents.

## Acceptance scenarios

| ID | Setup and observable result | Requirements and status |
| --- | --- | --- |
| AC-01 | Create a one-node network, export its recovery record, then restart. The node reconnects without requiring the master password for ordinary inference. | AUTH-01–03, NET-01; C/I |
| AC-02 | A new machine on a different physical network discovers and joins through Tailscale after Mango authentication. A tailnet device without enrollment cannot use peer authority. | NET-01–07, SCOPE-03; C/I; discovery details Q02/Q05 |
| AC-03 | Two networks share a display name. First enrollment identifies the intended fingerprint rather than selecting the fastest responder. | NET-04–05; I/P Q05 |
| AC-04 | Concurrently edit configuration and reenroll stale peers without a majority. Master-authorized newest changes converge, ties resolve deterministically, and stable node identity/join ordering is retained. | NET-05, SYNC-03–05; C/I Q05 |
| AC-05 | Leave 20 peers idle with unchanged models for 15 minutes. Measure CPU, memory, control bytes, and wakeups; there are no recurrent full catalog scans. Report total dependency footprint separately. | NFR-01–03, SYNC-01/06; C/P Q01/Q22 |
| AC-06 | Change availability, lose a pushed delta, then reconnect or perform daily/manual reconciliation. Peers converge without resurrecting removed models or fetching every full catalog for every event. | SYNC-01–07; C/I/P Q05/Q22 |
| AC-07 | Remove the first node. Reachable nodes still route inference, and the next eligible lowest-join-ID peer can collect logs. | SCOPE-02, LOG-10–12; C/I/P Q05/Q25 |
| AC-08 | Create a key on one node and immediately use it on a peer that missed the push. A sequential, bounded lookup recovers a verifiable record. A known wrong secret does not trigger lookup. | KEY-01/05–07; C/I/P Q07/Q08 |
| AC-09 | Flood an internet ingress with random key IDs, repeated IDs, varied source addresses, and oversized bodies. Peer queries, memory, and CPU stay bounded; ordinary known-key requests retain service within stated capacity. | KEY-06–08, NFR-05; I/P Q08 |
| AC-10 | A low-priority text key tries high priority, image generation, and administration. Every route, including forwarding through another peer, preserves its scope and fixed priority. | KEY-01–03, AUTH-07; C/I/P Q04/Q08 |
| AC-11 | Revoke a key, disconnect a peer, and rejoin it. It reconciles before service if another peer is reachable. If isolated, its local key record can serve, including the accepted stale-key case; known revocations never resurrect. | KEY-09–11; C/I Q07 |
| AC-12 | Perform full reset. Keys and private logs are cleared; model aliases, capabilities, inventory, and matching benchmarks survive. Reenrollment uses new authority; old snapshots cannot restore old access or logs. | AUTH-05–06; C/I Q06 |
| AC-13 | Enable public ingress. Valid HTTPS inference works from outside Tailscale; enrollment, GUI, log download, and provider management are unreachable through that public surface. | PUBLIC-01–04; C/I/P Q03 |
| AC-14 | Qualify each supported provider/version/profile. Inventory, inference, streaming, cancellation, limits, and declared management operations agree with observations; unsupported operations are explicit. | PROVIDER-01–07, API-01–04; C/I/P Q09/Q10 |
| AC-15 | Send a provider-side code execution request through a localhost-backed ingress. It does not gain host execution under an inference-only key. Client-executed tool-call output still works where supported. | API-05; P Q11 |
| AC-16 | Alias GGUF and MLX variants together. Matching modalities are accepted despite different quality; conflicting modalities are rejected. A feature unsupported by one deployment is routed only to an eligible one. | MODEL-01–05; C/I/P Q12 |
| AC-17 | A shared embedding alias has incompatible dimensions or different vector spaces. Apply the approved output-contract/pinning policy and show the limitation without pretending to assess intelligence. | MODEL-06; P Q12 |
| AC-18 | The model's theoretical context exceeds its served context. Requests just within the served total budget succeed; those above it are rejected or use a different eligible deployment. Include template, tools, output reserve, and multimodal overhead. | CONTEXT-01–04; C/I/P Q13 |
| AC-19 | Exercise Never, Now, and When convenient benchmark policies, including a model added later. Now blocks node serving until complete; convenient waits five locally idle minutes and yields to any client request without new consent prompts. | BENCH-01–06; C/I Q13 |
| AC-20 | Two models' weights fit but their simultaneous context peaks do not. Admission serializes them. Two racing admissions cannot reserve the same remaining RAM or VRAM. Include unified-memory and multi-GPU cases. | MEMORY-01–06, BENCH-03/06; C/I/P Q14 |
| AC-21 | Mango inference is busy without external load. It does not trigger its own user-activity withdrawal. Start a non-LLM workload: admission closes, active inference aborts, memory release is verified, and cooldown follows local settings. | RESOURCE-01–07; C/I/P Q09/Q15 |
| AC-22 | Manual leech mode survives a restart and an expired automatic cooldown. Other peers cannot disable the host's activity protection or change its memory margin. | RESOURCE-05, UI-02; C/I |
| AC-23 | Submit independent requests from several ingresses to an alias on two machines. Capacity is used concurrently and each attempt has exactly one execution owner; ambiguity does not cause blind duplicate starts. | QUEUE-01–03, ROUTE-01; C/I |
| AC-24 | Submit related agent follow-ups and unrelated work under the same key. Follow-ups may reuse cache; unrelated work can run elsewhere. Unloading invalidates affinity, and retained routing state expires. | AFFINITY-01–05; C/I/P Q19 |
| AC-25 | A low-priority request has no alternate placement while higher-priority active work can move elsewhere next time. Allow the scarce request to queue without interrupting execution. Without that alternate, reject under normal priority rules. | QUEUE-04, ROUTE-02/03; C Q17 |
| AC-26 | Admit higher-priority work. Evict only conflicting lower-priority queued work; preserve the explicit scarcity exception and independent-resource work. Reroute evictions or return overload. | QUEUE-05–07; C Q16/Q17/Q18 |
| AC-27 | Place a flexible high-priority embedding job on a smaller eligible host. When scarce low-priority work arrives at its current host, reserve the scarce next start and advise the ingress to route the next flexible request elsewhere. | ROUTE-01–04; C Q17 |
| AC-28 | A large-model request arrives while small-model work occupies its only host. Finish active work under the approved policy, prevent perpetual backfill, unload safely, and advise ingresses to move future small-model work. | MOVE-01–03, ROUTE-06; C/I/P Q18 |
| AC-29 | Fail a serving node while its ingress client stays connected. Before client output, retry only after the prior attempt is failed/stopped; partial output prevents retry. Client disconnect cancels downstream work and carries no resume guarantee. | MOVE-04/05, QUEUE-09; C/I Q20/Q29 |
| AC-30 | Make a master-authorized remote model edit while a peer is offline, then reconcile newest-wins state while preserving local safety. Use ordinary labeled controls, keyboard navigation, readable status and responsive progress. | UI-01–08, MODEL-07; C/I Q05/Q23 |
| AC-31 | Check Off, Sparse, Verbose and Everything. Verify client IP provenance, key name but no credential tokens, input-only versus input/output capture, and independently disableable operational logs. | LOG-01–09; C/I Q19/Q24 |
| AC-32 | Collect a day across mixed timezones with an offline peer, repeated collection, a collector failure, and a daylight-saving boundary. Aggregates deduplicate attempts and identify missing data. | LOG-10–14; C/I/P Q25 |
| AC-33 | Enforce byte and age retention, then clear the network while a peer is offline. On reconnect, that peer applies deletion before upload. Exported user copies are not falsely claimed deleted. | LOG-15–18; C/I/P Q25 |
| AC-34 | Initially show updates unavailable when no update server is configured, preserving the future coordination interfaces. Later qualify verified rolling update behavior against eligible, incompatible, offline and deferring hosts. | UPDATE-01–04/08–11; C/I Q26 |
| AC-35 | Connect distinct versions in the same protocol cutoff window; reject unsafe communication across a later cutoff in either direction. Do not require a current/previous-version matrix. | UPDATE-05–07; C/I Q27 |
| AC-36 | Review future best-model scenarios without enabling the feature. The catalog retains required information; no hidden classifier runs in ordinary routing. | BEST-01–12; F/I/P Q28 |

| AC-37 | While idle, no periodic full-peer online-map probes occur; local inventory fallback is no more than hourly. Each running node attempts daily reconciliation even when contribution is disabled. | SYNC-06, NFR-03; C Q01/Q22 |
| AC-38 | Request hashes match: no generic config refresh. A mismatch triggers targeted reconciliation; missing known model hosts trigger coalesced full discovery no more than once per ten-second cooldown. | SYNC-06, ROUTE-05; C Q05 |
| AC-39 | In a multi-node network with no reachable peer, refuse key issuance/revocation; allow it with one reachable peer, or in a known singleton. Merge independent key changes and preserve revocations. | KEY-02/09; C Q07 |
| AC-40 | Verify RAM reserve clamps at 2 and 5 GiB and VRAM defaults to flat 1 GiB. Accept a zero reserve after one recorded local warning and do not ask again on that node. | MEMORY-04; C Q14 |
| AC-41 | Submit ten requests with one key. Apply actual resource capacity and shared network policy rather than an eight-per-key quota. Queue residence defaults to five minutes and changes under master authority. | KEY-02, QUEUE-08; C Q08/Q21 |
| AC-42 | A future model has several task ratings, while each classified request chooses exactly one configured task keyword. No model loses other ratings when one task is selected. | BEST-08; F Q28 |
| AC-43 | Recover a forgotten master password with the recovery credential. Existing inference keys, peer enrollment, configuration, benchmarks, and logs survive; old administrative sessions end. Full reset remains a separate explicit action. | AUTH-01/05/07; C owner clarification |

## Coverage of the original specification

| Original requirement area | Primary record |
| --- | --- |
| Low footprint, WAN connectivity, and broader providers | PRODUCT-02–04; NFR-01–06; NET-01–07; PROVIDER-01 |
| First peer, master password, recovery key, equal peers | AUTH-01–07; SCOPE-02; NET-05 |
| Active joining and leech/contributor roles | NET-01–03; RESOURCE-01–07 |
| External activity, aborts, unloading, local cooldown | RESOURCE-02–07; UI-02 |
| Push index updates, daily refresh, manual refresh | SYNC-01–07 |
| Unsloth, LM Studio, llama.cpp, extensible modalities | PROVIDER-01–07; API-01–05 |
| Coordinated update checks, eligibility, override, mixed versions | UPDATE-01–11 |
| Scoped named keys, three priorities, revoke and propagate | KEY-01–04; LOG-02 |
| Sequential unknown-key pull and anti-abuse limits | KEY-05–10 |
| Public HTTPS inference | PUBLIC-01–04 |
| Endpoint discovery, aliases, capabilities, ratings | MODEL-01–08 |
| Actual served context, short/middle/full benchmarks | CONTEXT-01–04; BENCH-01–06 |
| Cross-format aliases, modality validation, no quality validation | MODEL-02–06 |
| Concurrent clients and per-serving-node queues | QUEUE-01–03 |
| Alternate destinations and cache-aware follow-ups | ROUTE-01; AFFINITY-01–05 |
| Temporary request state and unrelated parallel work | AFFINITY-02–03 |
| Measured overlap, RAM/VRAM margin, serialized model loads | MEMORY-01–06; BENCH-03/06 |
| Relocation, unloading small models, ingress route advice | MOVE-01–05; ROUTE-06 |
| Scarce-destination preference and low network traffic | ROUTE-02–05; Q17 |
| Low-priority rejection and higher-priority queue purging | QUEUE-04–07; Q16/Q18 |
| Throughput before priority before FIFO | PRODUCT-05; ROUTE-01–07 |
| Future best classifier, context fit, available/loaded fallback | BEST-01–10 |
| Future best five-minute profile and related-request affinity | BEST-05; AFFINITY-03 |
| Network-wide GUI model settings and local host controls | UI-01–08 and setting-boundary table |
| Verbose, sparse, off; names and request timing metrics | LOG-01–09 |
| Oldest surviving daily collector and on-demand collection | LOG-10–14 |
| Network retention and clearing all saved logs | LOG-15–18 |
| Requirements first, questions together, plan later | Requirements index; SCOPE-11; Open decisions |

## Status before implementation planning

All 29 responses and the password-recovery clarification are recorded in [Recorded decisions](09-recorded-decisions.md). The subject requirements and these scenarios reflect those decisions. The [eight-phase plan](../plan/00-shared-foundation.md) identifies implementation ownership and the small checks needed at each stage. Hardware behavior still requires live qualification; application implementation has not begun.
