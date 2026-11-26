# Mango resources and scheduling requirements

The hosting program decides whether it can serve a valid request. Mango coordinates provider slots, local contribution, benchmarks, and queue ownership. Peer advertisements are routing hints. The serving node checks the current facts when the work arrives.

## Local activity and contributor state

**RESOURCE-01 — C.** Nodes support contributor and leech roles. Switching to leech withdraws local inference availability, aborts active inference, and unloads models or shuts down the inference servers under Mango's control. Requests can still enter the network through that node.

**RESOURCE-02 — C.** Sustained external CPU or GPU activity makes the whole node a leech, including YOLO deployments. Local serving yields to the user's workload and ingress remains available. Activity monitoring, thresholds, and cooldown are local-only settings.

**RESOURCE-03 — I.** Attribute provider inference and benchmarks to Mango using provider process and worker evidence. Only sustained attributable external CPU or GPU activity can start the activity cooldown. Surface missing attribution as a diagnostic.

**RESOURCE-04 — C, Q15 as amended.** Sample at intervals of at least two seconds. Keep the default ten-second sustained-use trigger and five-minute quiet cooldown. Renew quiet time on renewed external activity. Log activity withdrawals separately when enabled. RAM, VRAM, and Windows commit observations are advisory outside the additional-load fit check.

**RESOURCE-05 — I.** Manual leech mode persists until the owner changes it. Cooldown expiry cannot undo manual withdrawal, maintenance, disabled contribution, or an unresolved managed lifecycle operation. Legacy memory-pressure holds cannot block serving.

**RESOURCE-06 — I.** On a leech transition, withdraw local admission, report queued and active outcomes to ingresses, cancel managed inference, and verify the authorized release. Report unresolved provider work or release failure. Continue routing from the ingress.

**RESOURCE-07 — C, Q09/Q15 as amended.** Use a bounded provider-specific cancellation deadline. Terminate only a process Mango is authorized to manage and can identify. Attached endpoints retain their own lifecycle authority. Missing telemetry and missing unload permission on an attached provider do not block ordinary forwarding.

| State | Local inference behavior | Exit condition |
| --- | --- | --- |
| Manual leech | No admissions; cancel and release managed inference | Explicit local owner action |
| Contributor idle or busy | Forward eligible work within queue and provider slot rules | User activity, manual withdrawal, or maintenance |
| Withdrawing | Refuse admissions; cancel work and release resources | Release verified or fault identified |
| Automatic cooldown | Local serving is dormant, with routing and GUI available | Configured quiet period and any pending release settled |
| Unresolved provider lifecycle | Hold work that would conflict with the unresolved operation and report its cause | Provider settlement or verified recovery |

## Model residency and memory fit

**MEMORY-01 — C, owner 2026-11-21.** Check RAM and per-device VRAM before an additional non-YOLO model load would leave multiple models resident under the owner's co-residency policy. That is the only RAM/VRAM fit check. Forward a sole-model load and valid inference to a loaded model to the host program for its capacity decision.

**MEMORY-02 — I.** For a checked additional load, compare actual headroom and simultaneous model requirements, including the load peak and already committed loads. Count resident allocations once. Serialize or reserve checked loads so two admissions cannot spend the same headroom. If co-residency cannot be established, use the alternate-first wait-and-swap path in MOVE-01.

**MEMORY-03 — I.** Treat host RAM, each discrete GPU's VRAM, and unified memory appropriately. Do not add separate GPU memories as if any model could use the sum. Do not count Apple unified memory twice as independent RAM and VRAM. Respect the backend's actual sharding and device placement.

**MEMORY-04 — C, Q14 as amended.** Apply owner-configured memory margins only to checked co-residency. Default host RAM margin is 15 percent clamped from 2 GiB to 5 GiB. Default VRAM margin is 1 GiB per discrete GPU. The owner may set either as low as zero after the one-time local acknowledgement. Count unified memory as a single pool.

**MEMORY-05 — I.** Use provider slot and lifecycle facts for execution scheduling. Track ownership until request completion or cancellation is settled. Reservations expire with the Mango process. On startup, observe actual provider residency and work before admitting conflicting requests. Treat saved pending entries as evidence to recheck, retain verified idle residents, and acquire reservations only for new admissions. CPU/GPU utilization informs user-activity detection. It does not take part in a model-fit calculation.

**MEMORY-06 — C, owner 2026-11-21.** Use comparable native requirements or automatic benchmark measurements for checked co-residency. Explicit owner RAM/VRAM overrides take precedence. Surface native allocation failures and refresh affected evidence. An allocation failure does not withdraw the node or blacklist ordinary serving.

**MEMORY-07 — C, owner 2026-11-21.** Additional YOLO models load individually on demand alongside resident YOLO models without a Mango fit check or waiting for their requests to finish, when the provider supports and accepts the load. When a switch requires evicting YOLO residents, wait until the last active request on the group finishes, then unload the entire resident group and verify release. Load only the requested model. Use MOVE-01 in both directions between YOLO and non-YOLO.

## Requests and queue ownership

**QUEUE-01 — C.** Accept simultaneous incoming requests from different nodes and clients. Each node tracks requests it receives. Work destined for another machine is submitted only to that serving node's queue, rather than duplicated across every peer.

**QUEUE-02 — I.** The ingress tracks the client connection, authentication, routing attempts, deadline, and terminal outcome. The serving node tracks admission, loading, execution, and its waiting queue. Use a network-unique request ID and an attempt ID so a transfer, retry, or log aggregation cannot count multiple attempts as separate client requests.

**QUEUE-03 — I.** Transfer requires an explicit accepted or rejected result. An ambiguous network failure must not cause another node to start the same request while the original may still be executing. Reserve one execution owner per attempt; use cancellation acknowledgment or expiry/fencing before safe reassignment. Do not promise general exactly-once execution across failures.

**QUEUE-04 — C, Q17.** Reject a lower-priority request without queueing when serving it would displace higher-priority work and no permitted placement exists, except for the explicit scarcity exception in ROUTE-03. Same/higher priority may queue within configured bounds. Never interpret this exception as permission to interrupt active inference.

**QUEUE-05 — C, Q16/Q17.** A higher-priority arrival displaces lower-priority queued work competing for the same resources, except an admitted scarce request protected by ROUTE-03 when the flexible higher-priority work can be routed elsewhere. Return displaced requests to ingress for bounded rerouting or overload. Independent resource sets continue working.

**QUEUE-06 — C, Q16/Q17.** Apply priority conflicts to actual slot, lifecycle, residency, and benchmark contention. Independent work may proceed if it cannot delay reserved work. Preserve the scarcity exception.

**QUEUE-07 — C, Q18.** Ordinary priority reorders waiting work and does not interrupt active inference. Active cancellation is reserved for user-activity or explicit leech, explicit cancellation, maintenance, credential revocation, failures, or separately approved preemption. Request-driven eviction follows MOVE-01 and waits for conflicting requests to finish.

**QUEUE-08 — C, Q08/Q21/Q29.** Use bounded in-memory queues. Maximum queue residence is a master-controlled network setting defaulting to five minutes. Remove the proposed eight-per-key limit and 120-second deadline. Choose generous count/byte limits for plausible contexts and media rather than artificially restricting trusted clients. The former 64-request count is not an approved cap. No disk queue or crash resume is required; a still-connected ingress may retry failed serving work under MOVE-04/05.

**QUEUE-09 — I.** Cancel queued work promptly when its client disconnects, deadline expires, key is revoked, or it is displaced. Propagate cancellation to provider work already underway. A slow client must not hold unbounded generated output or an execution slot indefinitely.

**QUEUE-10 — I, Q21.** Distinguish invalid input, authentication/scope failure, unsupported operation, unavailable destination, overload, timeout, withdrawal, and provider failure. Use 503 for unavailable capacity and 429 only for actual ingress/unknown-key protection, not an invented per-key concurrency quota. Once output is committed, follow stream termination/error semantics; do not pretend the HTTP status can change.

## Routing and priority order

**ROUTE-01 — C.** For independent requests on a shared alias, try an eligible idle worker before queueing behind a busy one. Use native capabilities to identify alternatives. Unknown or estimated fit cannot justify leaving a usable worker idle. Preserve scarce placements for requests that need them.

**ROUTE-02 — C, Q17.** A scarce request may outrank a more flexible higher-priority request when the latter has another eligible destination for its next work. This improves the number of requests served by the network without preempting the ongoing request. Otherwise priority and FIFO apply normally.

**ROUTE-03 — C, Q17.** Permit a lower-priority scarce request to wait behind active higher-priority work when that work has another eligible destination for later requests. Let active work finish, reserve the scarce request, and advise the original ingress. Verify authorization, provider capabilities, and actual alternate eligibility before granting the exception.

**ROUTE-04 — I.** Eligibility includes authorization, contribution, provider protocol and features, addressable deployment identity, actual slot or lifecycle status, and any additional-load rule that applies. Use provider-reported context as a routing signal. Host programs decide whether valid requests fit. Missing measurements do not disqualify an otherwise eligible worker.

**ROUTE-05 — C, Q05.** Probe likely destinations when requests arrive. If none are known, use bounded full-network discovery with a shared cooldown initially set to ten seconds. Coalesce simultaneous discoveries. Avoid a continuously probed live map and send the body only to the selected serving node. Check current authorization, queue, provider, and applicable load constraints there.

**ROUTE-06 — P, Q18/Q21.** Retain useful residents and reserve an admitted scarce request's next start. Stop conflicting backfill when it would postpone that reservation. Use MOVE-01 and MEMORY-07 for residency switches.

**ROUTE-07 — I.** Routing must terminate. Track attempted destinations and request deadlines, reject loops, use retry backoff, and retain FIFO age during a legitimate reroute without trusting client-supplied timestamps. FIFO is defined by admission order for equal contenders at the serving node, not a perfectly synchronized global arrival clock.

## Affinity and temporary request state

**AFFINITY-01 — C.** Remember routing decisions for subsequent related client requests. Waiting briefly for the endpoint with a useful cache may be better than moving to an idle endpoint that must process the entire prompt again.

**AFFINITY-02 — C.** Temporarily retain enough request information to distinguish continuation from an unrelated request by the same client. An unrelated request may use another deployment concurrently. One API key or source IP must not automatically pin all client traffic to one machine.

**AFFINITY-03 — C, Q19.** Prefer an explicit client session/conversation identifier scoped to authenticated key identity. Otherwise compare a bounded in-memory representation of relevant message prefixes and generation context, with a privacy-preserving digest where sufficient. Default to five minutes after the last completion and a bounded per-key and per-node footprint. Persist no prompt bodies solely for routing affinity. The five-minute default is accepted for ordinary affinity as well as future `best` routing.

**AFFINITY-04 — I.** Cache affinity is tied to the real provider instance, model/tokenizer/configuration, and usable cached prefix. The same alias does not make caches interchangeable. Invalidate hints after unloading, restart, relevant reconfiguration, or loss of state. An adapter that cannot confirm cache behavior may advertise only a best-effort preference.

**AFFINITY-05 — C, Q19.** Bound cache waiting by the client's deadline and estimated cache benefit. Consider bounded waiting for a related continuation only when its expected completion is better than the alternative. Affinity follows authorization, provider eligibility, priority, and ROUTE-01 for independent requests.

## Relocation and failures

**MOVE-01 — C, owner 2026-11-21.** If local work would wait for conflicting requests or require a model swap, first try an eligible alternate node. If none can serve it, wait for the conflicting work to finish, unload the required residents through authorized lifecycle operations, verify release, then load and serve the requested model. Apply the same sequence in both directions between YOLO and non-YOLO and unload YOLO residents as a group. Keep later work from extending an admitted switch indefinitely under the queue rules. Missing unload authority or unresolved release blocks that local switch.

**MOVE-02 — I.** Internal route advice goes to the Mango ingress, not a mandatory HTTP redirect to the external client. The ingress preserves the client's URL, credentials, and stream. Advice is a hint that still requires current admission checks.

**MOVE-03 — I.** Moving a waiting request transfers its payload and ownership, not live KV cache. Moving future requests may lose cache benefit. Native backend conversation IDs or uploaded-file handles may prohibit movement until equivalent state is available.

**MOVE-04 — C, Q20/Q29.** Before execution begins, an eligible alternative may be tried. Once execution starts, do not start another attempt until the running attempt has failed or is known stopped. While the client is still connected and no output has been delivered, prefer a safe retry at another eligible node over exposing the error. Once any output, including an error, has reached the client, do not automatically retry. Never concatenate a fresh generation onto a partial result or duplicate possible side effects.

**MOVE-05 — C, Q20/Q29.** The ingress retains enough request state to reroute after a serving-node/network failure while its client remains connected. Distinguish failure from uncertain reachability: a timeout alone does not prove remote execution stopped; cancellation acknowledgment or bounded execution expiry can establish safe retry ownership. If no safe destination exists, fail the client. Client disconnect has no resume guarantee; forward cancellation to the serving node to avoid wasted work. Ingress-process loss does not require durable queue recovery.

## Scheduling examples

| Situation | Required or proposed outcome |
| --- | --- |
| Two independent requests, alias available on two eligible nodes | Use both workers concurrently when supported, even when the requests share a key. |
| Agent continuation on a busy instance with useful cache | Consider a bounded wait under AFFINITY-05; reroute if the benefit is absent or deadline is at risk. |
| High-priority small embedding request and scarce large-model request | Place embeddings on the smaller compatible machine; priority does not entitle it to the fastest GPU. |
| Low-priority request behind high-priority work | Admit the scarcity exception if an alternate can host subsequent flexible work; otherwise reject. |
| High-priority request admitted behind active normal work | Under Q18's recommendation, let active work finish; displace lower-priority waiting competitors. |
| Normal request arrives while low-priority work waits | Evict affected low-priority queued work and attempt bounded rerouting. |
| A model swap is needed and no alternate can serve | Wait for conflicting work to finish, unload the required residents, verify release, then load only the requested model. Evict resident YOLO models together. |
| Owner starts a game | Withdraw, abort managed inference, release resources, and enter the local cooldown. |
| Two ingresses simultaneously see one free slot | Only one admission succeeds; the other gets a queue decision or reroute/rejection. |
| Lower-priority work can use an independent provider slot | Admit it if it cannot delay reserved work. |
