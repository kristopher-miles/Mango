# Mango resources and scheduling requirements

The serving node is the final authority over its resources. Other nodes use its advertisements to select candidates, then request admission. This permits simultaneous clients without treating a replicated catalog as a reservation system.

## Local activity and contributor state

**RESOURCE-01 — C.** Nodes support contributor and leech roles. Switching to leech withdraws local inference availability, aborts active inference, and unloads models or shuts down the inference servers under Mango's control. Requests can still enter the network through that node.

**RESOURCE-02 — C.** Detect high system activity not caused by monitored inference processes. When detected, become a leech until the configured cooldown has elapsed. Activity monitoring, thresholds, enablement, and cooldown are local-only settings. Five minutes is the user's example default, not yet a measured optimum.

**RESOURCE-03 — I.** Track the managed inference process tree, including workers, when attributing load. CPU, GPU, RAM pressure, and device memory matter. Do not assume aggregate GPU utilization minus one process counter is a reliable measure on every operating system. Missing attribution must be visible.

**RESOURCE-04 — C, Q15.** Use local observations at no more than one sample every two seconds; owners may choose a lower frequency, meaning a longer interval. Cutoff percentages and cooldown are configurable per node. Retain the initial 10-second sustained-load trigger and five-minute quiet cooldown as defaults, restarting quiet time on renewed load. Memory pressure immediately triggers withdrawal/cooldown. Record its cause, observations, and whether managed inference was loading/running in the separately disableable operational log.

**RESOURCE-05 — I.** Manual leech mode persists until the user changes it. An automatic cooldown cannot undo manual withdrawal, maintenance, a resource fault, or disabled contribution. Restarting or resuming the computer must not silently clear those constraints.

**RESOURCE-06 — I.** Withdraw admission before beginning cancellation. Notify ingresses of queued and active request outcomes, cancel provider work, then verify resource release. Publish failure if a server refuses to stop or memory remains allocated. Do not advertise a successful return of resources merely because a stop command was sent.

**RESOURCE-07 — C, Q09/Q15.** Allow a short adapter-specific cancellation deadline, then terminate only an explicitly managed process if necessary. Unknown telemetry or an unsupported shutdown path defaults to refusing automatic contribution until the owner selects a safe local policy. Attached-only endpoints may still be used with an explicit limitation, but cannot promise automatic reclamation.

| State | Local inference behavior | Exit condition |
| --- | --- | --- |
| Manual leech | No admissions; cancel and release managed inference | Explicit local owner action |
| Contributor idle or busy | Admit within current measured limits | User activity, manual withdrawal, maintenance, or failure |
| Withdrawing | Refuse admissions; cancel work and release resources | Release verified or fault identified |
| Automatic cooldown | No inference; routing and GUI remain available | Full quiet period plus safe health and resource checks |
| Resource fault | No unsafe new work; report the cause | Recovery and revalidation |

## Memory and execution admission

**MEMORY-01 — C.** Do not schedule beyond the node's configured RAM/VRAM safety margin. If two needed models will not fit together, wait for one request to finish and its model to unload before admitting the next load.

**MEMORY-02 — I.** Check actual free memory and reserve the expected peak before loading or starting work. Concurrent admissions on one host must not each allocate the same free capacity. Count loading, running, and retained caches; release a reservation only after cancellation or completion is settled and the resource state is known.

**MEMORY-03 — I.** Treat host RAM, each discrete GPU's VRAM, and unified memory appropriately. Do not add separate GPU memories as if any model could use the sum. Do not count Apple unified memory twice as independent RAM and VRAM. Respect the backend's actual sharding and device placement.

**MEMORY-04 — C, Q14.** Default reserved host RAM is 15 percent, clamped between 2 GiB and 5 GiB; default reserved VRAM is a flat 1 GiB per discrete GPU. Local owners may lower a reserve to zero. Before first accepting a below-default setting on a node, explain possible instability or shutdown and record consent; do not repeatedly prompt on that node afterward. Honor the chosen reserve. Unified memory remains one shared pool, not separately counted RAM plus VRAM.

**MEMORY-05 — I.** Resource admission includes measured execution concurrency and request size, not only whether weights fit. A provider with one proven slot gets one execution at a time. Provider internal queues must not hide Mango's ordering or continue running work Mango believes canceled.

**MEMORY-06 — I.** If an allocation fails, refresh observations, invalidate the unsafe profile, and stop retrying the same placement repeatedly. Surface a meaningful resource failure. A drop in free memory may require withdrawal even when the activity detector cannot identify its owner.

## Requests and queue ownership

**QUEUE-01 — C.** Accept simultaneous incoming requests from different nodes and clients. Each node tracks requests it receives. Work destined for another machine is submitted only to that serving node's queue, rather than duplicated across every peer.

**QUEUE-02 — I.** The ingress tracks the client connection, authentication, routing attempts, deadline, and terminal outcome. The serving node tracks admission, loading, execution, and its waiting queue. Use a network-unique request ID and an attempt ID so a transfer, retry, or log aggregation cannot count multiple attempts as separate client requests.

**QUEUE-03 — I.** Transfer requires an explicit accepted or rejected result. An ambiguous network failure must not cause another node to start the same request while the original may still be executing. Reserve one execution owner per attempt; use cancellation acknowledgment or expiry/fencing before safe reassignment. Do not promise general exactly-once execution across failures.

**QUEUE-04 — C, Q17.** Reject a lower-priority request without queueing when serving it would displace higher-priority work and no permitted placement exists, except for the explicit scarcity exception in ROUTE-03. Same/higher priority may queue within configured bounds. Never interpret this exception as permission to interrupt active inference.

**QUEUE-05 — C, Q16/Q17.** A higher-priority arrival displaces lower-priority queued work competing for the same resources, except an admitted scarce request protected by ROUTE-03 when the flexible higher-priority work can be routed elsewhere. Return displaced requests to ingress for bounded rerouting or overload. Independent resource sets continue working.

**QUEUE-06 — C, Q16/Q17.** Apply queue rejection and eviction to competing resource sets, not the whole node. Independent GPU/CPU work may proceed if it does not delay reserved work. The scarcity exception is part of admission and must survive ordinary priority purging.

**QUEUE-07 — C, Q18.** Ordinary higher-priority arrivals do not abort active requests. They reorder or displace waiting work. Active aborts are reserved for local resource protection, explicit cancellation, security revocation, failures, or a separately approved preemption policy. Idle loaded models may be unloaded; loading work that has not begun inference may be canceled safely when appropriate.

**QUEUE-08 — C, Q08/Q21/Q29.** Use bounded in-memory queues. Maximum queue residence is a master-controlled network setting defaulting to five minutes. Remove the proposed eight-per-key limit and 120-second deadline. Choose generous count/byte limits for plausible contexts and media rather than artificially restricting trusted clients. The former 64-request count is not an approved cap. No disk queue or crash resume is required; a still-connected ingress may retry failed serving work under MOVE-04/05.

**QUEUE-09 — I.** Cancel queued work promptly when its client disconnects, deadline expires, key is revoked, or it is displaced. Propagate cancellation to provider work already underway. A slow client must not hold unbounded generated output or an execution slot indefinitely.

**QUEUE-10 — I, Q21.** Distinguish invalid input, authentication/scope failure, unsupported operation, unavailable destination, overload, timeout, withdrawal, and provider failure. Use 503 for unavailable capacity and 429 only for actual ingress/unknown-key protection, not an invented per-key concurrency quota. Once output is committed, follow stream termination/error semantics; do not pretend the HTTP status can change.

## Routing and priority order

**ROUTE-01 — C.** When an alias has multiple eligible deployments, use another endpoint for simultaneous independent work when that increases served requests. Avoid placing a small embedding job on the only large-model machine merely because the large machine is faster.

**ROUTE-02 — C, Q17.** A scarce request may outrank a more flexible higher-priority request when the latter has another eligible destination for its next work. This improves the number of requests served by the network without preempting the ongoing request. Otherwise priority and FIFO apply normally.

**ROUTE-03 — C, Q17.** Permit a lower-priority request with only one eligible destination to queue behind active higher-priority work if that higher-priority work can move to another eligible destination for subsequent requests. Let the active request finish, reserve the scarce request, and advise the original ingress to route future flexible work elsewhere. Apply safety, context, modalities, and actual alternate eligibility before granting this exception. If no alternate exists, ordinary lower-priority rejection applies.

**ROUTE-04 — I.** Eligibility includes the whole request: required protocol, features, modalities, context and output reserve, device safety, contribution state, and authorization. A model that fits its weights on another machine but cannot handle this request's context is not an alternate destination.

**ROUTE-05 — C, Q05.** Use cached model hosts and probe/admit directly against likely destinations when real requests arrive. If no known host can be found, perform bounded full-network discovery to find newly joined hosts, with a shared per-node cooldown initially ten seconds. Coalesce simultaneous discoveries. Do not maintain a live peer map by continuous probing or broadcast request bodies. The serving node validates current resource admission.

**ROUTE-06 — P, Q18/Q21.** Prevent load/unload thrashing by retaining useful loaded models until they obstruct better admissions, and by reserving an admitted scarce request's next feasible start. Stop backfilling conflicting work when it would postpone that reservation. Any backfill must fit without delaying it. Outside the explicit scarcity exception, priority can starve low-priority work; do not silently age it into a higher priority.

**ROUTE-07 — I.** Routing must terminate. Track attempted destinations and request deadlines, reject loops, use retry backoff, and retain FIFO age during a legitimate reroute without trusting client-supplied timestamps. FIFO is defined by admission order for equal contenders at the serving node, not a perfectly synchronized global arrival clock.

## Affinity and temporary request state

**AFFINITY-01 — C.** Remember routing decisions for subsequent related client requests. Waiting briefly for the endpoint with a useful cache may be better than moving to an idle endpoint that must process the entire prompt again.

**AFFINITY-02 — C.** Temporarily retain enough request information to distinguish continuation from an unrelated request by the same client. An unrelated request may use another deployment concurrently. One API key or source IP must not automatically pin all client traffic to one machine.

**AFFINITY-03 — C, Q19.** Prefer an explicit client session/conversation identifier scoped to authenticated key identity. Otherwise compare a bounded in-memory representation of relevant message prefixes and generation context, with a privacy-preserving digest where sufficient. Default to five minutes after the last completion and a bounded per-key and per-node footprint. Persist no prompt bodies solely for routing affinity. The five-minute default is accepted for ordinary affinity as well as future `best` routing.

**AFFINITY-04 — I.** Cache affinity is tied to the real provider instance, model/tokenizer/configuration, and usable cached prefix. The same alias does not make caches interchangeable. Invalidate hints after unloading, restart, relevant reconfiguration, or loss of state. An adapter that cannot confirm cache behavior may advertise only a best-effort preference.

**AFFINITY-05 — C, Q19.** Bound cache waiting by the client's deadline and an estimated cache benefit. Prefer waiting only when expected wait plus cached execution is better than the alternative. Make uncertainty visible in diagnostics; cache preference never overrides safety, scope, context fit, or the accepted queue priority rules.

## Relocation and failures

**MOVE-01 — C.** When a larger model needs a machine occupied by smaller models, finish appropriate active requests, unload the smaller models, and look up alternate hosts from saved catalog knowledge. If another host can serve them, tell their ingress to route future work there. Avoid a whole-network search for every displacement.

**MOVE-02 — I.** Internal route advice goes to the Mango ingress, not a mandatory HTTP redirect to the external client. The ingress preserves the client's URL, credentials, and stream. Advice is a hint that still requires current admission checks.

**MOVE-03 — I.** Moving a waiting request transfers its payload and ownership, not live KV cache. Moving future requests may lose cache benefit. Native backend conversation IDs or uploaded-file handles may prohibit movement until equivalent state is available.

**MOVE-04 — C, Q20/Q29.** Before execution begins, an eligible alternative may be tried. Once execution starts, do not start another attempt until the running attempt has failed or is known stopped. While the client is still connected and no output has been delivered, prefer a safe retry at another eligible node over exposing the error. Once any output, including an error, has reached the client, do not automatically retry. Never concatenate a fresh generation onto a partial result or duplicate possible side effects.

**MOVE-05 — C, Q20/Q29.** The ingress retains enough request state to reroute after a serving-node/network failure while its client remains connected. Distinguish failure from uncertain reachability: a timeout alone does not prove remote execution stopped; cancellation acknowledgment or bounded execution expiry can establish safe retry ownership. If no safe destination exists, fail the client. Client disconnect has no resume guarantee; forward cancellation to the serving node to avoid wasted work. Ingress-process loss does not require durable queue recovery.

## Scheduling examples

| Situation | Required or proposed outcome |
| --- | --- |
| Two independent requests, alias available on two idle nodes | Run on both where safe; do not serialize merely because the key is the same. |
| Agent continuation on a busy instance with useful cache | Consider a bounded wait under AFFINITY-05; reroute if the benefit is absent or deadline is at risk. |
| High-priority small embedding request and scarce large-model request | Place embeddings on the smaller compatible machine; priority does not entitle it to the fastest GPU. |
| Low-priority request behind high-priority work | Admit the scarcity exception if an alternate can host subsequent flexible work; otherwise reject. |
| High-priority request admitted behind active normal work | Under Q18's recommendation, let active work finish; displace lower-priority waiting competitors. |
| Normal request arrives while low-priority work waits | Evict affected low-priority queued work and attempt bounded rerouting. |
| Small models on the large machine; a large model is requested | Finish active work as permitted, reserve the large request, unload, and advise ingresses about alternatives. |
| Owner starts a game | Withdraw, abort managed inference, release resources, and enter the local cooldown. |
| Two ingresses simultaneously see one free slot | Only one admission succeeds; the other gets a queue decision or reroute/rejection. |
| Lower-priority work can use an independent GPU | Under Q16's recommendation, admit it if it cannot delay higher-priority reservations. |
