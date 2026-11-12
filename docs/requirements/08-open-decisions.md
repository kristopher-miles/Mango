# Mango original decision questionnaire

This is the original question set, retained as history. All 29 questions received responses; see [Recorded decisions](09-recorded-decisions.md) for the current resolution. The recommendations below are historical and do not override those responses.

This was the complete question set for the original requirements draft. Each recommendation is a proposed answer, not a decision already made. The other documents preserve confirmed requirements separately from these proposals.

You can answer by number, or say “Accept the recommendations except Q…” and list the changes. An accepted answer will update the requirements before the implementation plan is written. Technology choices and development stages are intentionally deferred.

## Product and connectivity

### Q01 Platforms and scale

Which platforms and operating envelope should the first qualified release promise?

**Recommended:** Windows, Apple-silicon macOS, and Linux for the Mango client and GUI, with provider/telemetry support qualified separately. Start with 1–20 nodes and 100 deployments. Use the proposed idle targets of under 1 percent of one CPU core, 150 MiB background-service memory, and 1 KiB/s average control traffic per node as targets to validate, not promises already established. Report the total footprint including Tailscale and the open GUI separately.

This makes “lightweight” testable without claiming unlimited scale. A narrower platform set would reduce initial qualification work. Affects PRODUCT-01, NFR-01–04.

### Q02 Tailscale installation

Should Mango require the existing Tailscale client or embed Tailscale?

**Recommended:** require an installed, authenticated client initially and make setup checks clear in the GUI. Retain embedding as a packaging option to assess after the stack is chosen. `tsnet` is real, but it creates another authenticated device identity and ties the direct embedding path to Go.

Embedding could improve first-run convenience, but its lifecycle, device permissions, and footprint need qualification. Neither option removes user authentication. Affects NET-03, PRODUCT-02.

### Q03 Public HTTPS

Which public exposure path should Mango support, and should it ever enable exposure automatically?

**Recommended:** exposure is off until the local owner enables it. Support a general HTTPS reverse proxy, with Tailscale Funnel as an optional guided path when its restrictions fit. Creating an inference key never enables public serving by itself. Publish only the inference surface.

A Funnel-only choice is simpler but imposes its domain, port, platform, and bandwidth constraints. Affects PUBLIC-01–04.

## Authority and consistency

### Q04 Administrative access from peers

Does joining the network grant unrestricted settings administration, or should sensitive changes require a fresh master-password unlock?

**Recommended:** all enrolled nodes may use resources and view shared state; any node may administer the network after a short local master unlock. Require it for key management, enrollment, shared configuration, retention clearing, and update policy. Keep resource protection and process-control permission local to the host regardless of unlock elsewhere.

This retains peer equality while limiting what an unattended unlocked GUI or ordinary peer credential can change. Public inference keys never become administrator credentials. Affects AUTH-07, UI setting boundaries.

### Q05 Shared changes during partitions

Should the network prioritize safe global ordering or accept administrative changes on isolated peers?

**Recommended:** serialize membership, ordered join IDs, authority changes, and shared configuration commits through a temporary coordinator with fenced authority and a majority of enrolled voting peers. Any node can originate the change. Inference and immediate local resource withdrawal continue without a majority. Model observations remain host-owned.

The cost is significant: in a two-node network, both must be online for globally committed writes. An alternative is eventual merging with explicit conflicts and weaker ordering guarantees. Do not hide this tradeoff behind “decentralized.” Affects NET-05, SYNC-03–05, LOG-12. Also determines whether Q06/Q07 can promise authoritative freshness.

### Q06 Password recovery and compromise

What should recovery preserve, and must the printed recovery record rebuild authority after all peer state is lost?

**Recommended:** support both password replacement on a surviving network and authority recovery from an adequate offline recovery record. Ordinary recovery rotates the master/recovery authority and invalidates administrative sessions while preserving enrolled nodes and existing inference keys. Offer a separate compromise-recovery action that revokes peers/keys and requires reenrollment. Neither path recovers deleted logs or provider secrets automatically.

A full compromise recovery is more disruptive but appropriate when secrets were stolen. Lost-all-peers recovery needs more than a short password-reset code and cannot reconstruct a lost catalog or key registry without a replica/backup. Forced recovery without the old quorum starts a new network generation and prevents old partitions from regaining write authority automatically. Affects AUTH-04–06.

### Q07 Revocation while peers are offline

How much stale authorization is acceptable, and what happens to work already using a revoked key?

**Recommended:** public ingress may accept only authoritative revocation state no older than 60 seconds, refreshed on demand during traffic; if freshness cannot be established, fail closed for new public work. Cancel queued and active requests as soon as their serving node learns the revocation, and refresh authority for long-running public work under the same bound. Trusted private peer inference may continue while partitioned, but known revocations always win.

This introduces bounded security-related network checks. Immediate worldwide revocation with indefinite offline availability is not achievable. A longer window improves availability but increases exposure. Affects KEY-07/09–11.

### Q08 Key policy and limits

Should keys have a fixed priority, what additional scopes should exist, and which initial limits are appropriate?

**Recommended:** one fixed low/normal/high priority per key, default normal; permitted task types plus optional alias restrictions; unique display name; 90-day default expiry with an explicit non-expiring choice for private integrations. Begin with the unknown-key lookup limits in KEY-07 and finite configurable per-key rate, concurrency, and payload limits. Label these limits as per-ingress unless a later network-wide quota design is explicitly required.

Trusted peer-originated work carries its node identity and defaults to normal priority; elevation requires administrative policy. Forwarding public traffic never removes its original restrictions. Do not promise a strict global rate quota from independent node counters. The exact workload-dependent valid-key limits can be tuned during qualification. Affects KEY-01–08, QUEUE-08.

## Providers and model contracts

### Q09 Authority over existing inference servers

May Mango shut down an existing server it did not start, and who grants that authority?

**Recommended:** the local owner explicitly places each provider in managed or attached mode. Managed mode grants a defined load/unload/stop path and is required for a promise of automatic resource reclamation. Attached mode permits inference but clearly states when Mango cannot release the provider's resources. Do not stop unrelated processes or the provider GUI merely because names match.

Automatic downloads, provider installation, training, and weight distribution remain outside the initial scope. This qualifies the original blanket shutdown behavior where Mango lacks control. Affects PROVIDER-03–07, RESOURCE-06/07, SCOPE-09.

### Q10 Initial API coverage

Which exact provider profiles and client protocols must the first complete release support?

**Recommended:** qualify Unsloth Studio/API and `unsloth run`, LM Studio native management plus inference, and supported llama-server single-model/router profiles. Cover model listing, chat completions with streaming and client-executed tool calls, embeddings, image generation, transcription, speech generation, and a specifically named structured-decision contract. Use the documented System One style as the decision-contract candidate to validate. Support each operation where the installed backend/model provides it.

Treat full Responses, Anthropic Messages, video, realtime duplex audio, and general persistent job/file workflows as separately approved extensions. This keeps image/audio/decisions in scope without promising every vendor API. Affects PROVIDER-07, API-01–04.

### Q11 Provider tools and code execution

May an inference request cause a provider to run code, browse, or invoke tools on a contributing host?

**Recommended:** no provider-side execution by default. Preserve tool calls returned to and executed by the client. Explicitly disable backend-hosted tools when forwarding into a localhost provider, because the backend may otherwise trust the request as local.

Allowing host execution would need separate local permission, key scope, isolation, and failure semantics. It should not be smuggled into ordinary text inference. Affects API-05.

### Q12 Alias compatibility beyond modalities

Should alias validation remain strictly modality-only, or add technical checks for outputs that must be interchangeable?

**Recommended:** retain exact matching of input/output modality sets and never validate quality claims. Also require compatible embedding dimensions/encoding and warn that equal dimensions do not establish a common vector space. Pin an embedding collection to a deployment or owner-declared compatible family. Preserve deployment-specific feature and context checks for every request.

The embedding rule is an explicit extension to your validation limit; it prevents silent corruption without judging model intelligence. If rejected, record that callers assume all embedding compatibility risk. Affects MODEL-02–06.

### Q13 Benchmark permission and context verification

Should full-context tests run automatically even when expensive, and how should unreported limits be handled?

**Recommended:** get local permission for the benchmark suite, show its expected size, and make it cancelable. Measure short, half, and full usable context with reserved output and synthetic input, subject to a time/resource budget. Mark skipped or failed tests incomplete and schedule conservatively. Require a configured or verified served limit when the provider cannot report one; never silently substitute the model's theoretical maximum.

This retains your full-context measurement requirement while allowing an enormous or unsafe test to be deferred honestly. Affects CONTEXT-04, BENCH-01–06.

### Q14 Memory safety defaults

Which default safety margins and overlap policy should apply before a node is fully characterized?

**Recommended:** reserve the greater of 15 percent or 2 GiB host/unified memory and the greater of 10 percent or 1 GiB per discrete GPU, adjustable locally. Use conservative non-overlap for unmeasured combinations. Require context/concurrency peak evidence and a live admission check before overlapping models.

These are starting values to calibrate, not universal guarantees. A provider can still allocate unexpectedly, and another local program can race for memory. Affects MEMORY-01–06, BENCH-06.

### Q15 Activity detection and cooldown

What should trigger withdrawal, when should contribution resume, and what happens when process attribution is unavailable?

**Recommended:** local CPU/GPU/memory observation with up to 2-second samples, a 10-second sustained external-load trigger, immediate withdrawal for severe memory pressure, and five continuous quiet minutes before resuming. Renew the quiet period on new load. Manual leech mode persists. Treat missing attribution as a visible limitation and require a conservative local choice before automatic contribution.

The provisional external-load threshold needs hardware calibration; detecting “any input” should not automatically mean heavy load. Affects RESOURCE-02–07.

## Scheduling and client behavior

### Q16 Scope of queue purging

Should a higher-priority arrival clear every lower-priority queued request on that node, or only requests competing for the same resources?

**Recommended:** clear only conflicting lower-priority work. A job using an independent GPU or genuinely nonconflicting CPU capacity should continue or be admitted if it cannot delay the higher-priority reservation.

This is a deliberate narrowing of “all lower priority requests in the queue.” Literal node-wide purging is simpler to describe but can reject useful work without freeing the needed resource. Affects QUEUE-05/06.

### Q17 Scarcity versus priority

Does a low-priority request with one possible destination outrank a high-priority request with several destinations?

**Recommended:** no. Keep safety, authorization, and the explicit priority admission/rejection rules hard. Route flexible work elsewhere to maximize service, then use fewer eligible destinations to break contention among equal-priority requests, followed by FIFO.

Your “always lower priority” scarcity sentence and the later priority hierarchy conflict in this case. If scarcity should override key priority, say so explicitly; the queue rejection rules will need corresponding changes. Affects PRODUCT-05, ROUTE-02/03.

### Q18 Active work and starvation

Should ordinary priority changes abort running inference, and how should a waiting large model avoid endless postponement by small requests?

**Recommended:** let active requests finish during ordinary scheduling. Higher priority displaces queued work, while local activity, explicit cancellation, revocation, and faults can abort active work. Reserve an admitted scarce request's next feasible start and stop backfilling jobs that would delay it. Permit safe cancellation of loading work before inference begins when justified.

Retain strict priority rather than silently aging low-priority work upward; under sustained high load, low-priority work may starve or be rejected. Affects QUEUE-07, ROUTE-06, MOVE-01.

### Q19 Client identity and affinity

How should Mango identify a continuing conversation, how long should it remember it, and how long may cache reuse delay a request?

**Recommended:** use an explicit session identifier scoped to authenticated key identity, with bounded in-memory prefix comparison/digests as fallback. Expire ordinary affinity five minutes after completion. Keep unrelated work under the same key independent. Wait for a cached instance only when estimated benefit beats another placement and the client deadline permits it.

Never equate source IP or API key with one conversation. Do not persist prompt bodies solely for affinity. Affects AFFINITY-01–05; the future `best` five-minute rule is already confirmed.

### Q20 Retries and partial results

May Mango automatically restart a request elsewhere after execution starts or the client has received output?

**Recommended:** automatic retry only when no output has been committed and the operation is known to be replay-safe. After partial output or possible side effects, return an interruption with request/attempt identity. Require an explicit client retry rather than concatenating a second generation into the original stream.

Reassigning queued work remains allowed after ownership transfer. Live cache or backend-held conversation migration is not promised. Affects QUEUE-03, MOVE-03–05.

### Q21 Queue limits and overload

What bounds and failure contract should apply when work cannot start promptly?

**Recommended:** start with 64 waiting requests per serving node, 8 per key per node, and 120 seconds of queue residence for ordinary text/embedding work, plus finite byte limits and separate media deadlines. Use 429 for a client's rate/quota limit and 503 for unavailable inference capacity, with clear reason codes and useful retry hints. Bound rerouting attempts by distinct candidates and the request deadline.

Long-running media may need larger explicit deadlines; no request should wait indefinitely. These counts are tuning defaults, not a substitute for byte/resource limits. Affects QUEUE-08–10, ROUTE-07.

### Q22 Low traffic and failure detection

Is a small amount of bounded liveness and local inventory checking acceptable when event APIs are insufficient?

**Recommended:** yes. Prefer events, cached state, demand-driven checks, and daily reconciliation. Allow a slow local provider fallback, initially no more often than once a minute while enabled, and cheap bounded checks for active routing, security freshness, and temporary coordination. Do not scan every peer per request or fetch unchanged full catalogs repeatedly.

Without these exceptions, an externally changed server or dead peer can remain stale until the next request or daily refresh. Event API support also differs by provider/version. Affects SYNC-06, NFR-03, NET-03, KEY-09.

## Interface and operations

### Q23 GUI form and headless machines

Should the product support headless machines, and what form should the GUI take?

**Recommended:** a desktop/tray experience backed by a service, with an authenticated local browser GUI usable for headless deployments. Keep the layout responsive and accessible, and allow shared model administration from any enrolled node. Resource-protection settings still require local host access.

Do not add a native mobile app or publicly exposed admin console to the initial scope. The exact UI technology belongs in the later plan. Affects UI-01–08.

### Q24 Logging contents and privacy

Should sparse logging be the default, what does verbose “exactly” include, and does `tts` mean tokens per second?

**Recommended:** default sparse; interpret `tts` as tokens per second and name it explicitly. Verbose records exact inference request bodies with explicit credential exclusions; do not retain response bodies by default. Off creates no persistent request logs, including a hidden per-request audit trail. Required revocation/configuration/update state remains operational state, not logging.

If full responses are wanted, add them explicitly with retention and storage implications. Prompt text can contain secrets even after transport credentials are removed. Affects LOG-01–09.

### Q25 Daily logs and deletion

Which day boundary, retention, completeness, and clearing behavior should apply across intermittently connected nodes?

**Recommended:** one network timezone selected at setup, defaulting to the first node's timezone; UTC timestamps; seven days of raw records and thirty days of aggregate metadata, within a 1 GiB per-node total cap. The lowest eligible join ID collects; offline peers produce a visibly incomplete aggregate, with late merge and deduplication. Network clear remains pending for offline peers and applies a deletion generation before their old data can rejoin.

This does not guarantee archival redundancy or deletion of user exports. Affects LOG-10–18.

### Q26 Coordinated updates

Does “at the same time” mean coordinated queueing or simultaneous restart, and how much can a node update automatically?

**Recommended:** coordinate one verified release intent and queueing window, then perform rolling installation with bounded draining, local deferral, health verification, and rollback when the data format permits it. Warn before proceeding when a peer cannot verify eligibility; require the initiating user's explicit compatibility-risk override. Never override an invalid signature.

Coordinate Mango only. Leave Tailscale, drivers, providers, and model weights under their own update controls. Simultaneous restarts would deliberately remove all capacity and complicate recovery. Affects UPDATE-01–04/08–11.

### Q27 Mixed-version compatibility

How much backward compatibility should releases promise without requiring all nodes to match?

**Recommended:** publish a feature/protocol compatibility matrix and initially support the current and previous compatible protocol generation. Negotiate shared features regardless of application version numbers. Keep diagnostic connectivity where possible, but reject specifically incompatible operations safely.

“Do not lock out different versions automatically” cannot mean decoding unknown or unsafe protocols. Affects UPDATE-05–07.

## Deferred best routing

### Q28 Best-model interpretation and preparation

Are the proposed capability ratings, capacity tiers, classifier fallback, and reserved name appropriate for the future feature?

**Recommended:** shared labels and deployment-specific 0–5 ratings with unrated distinct from zero; one primary task label; first choose the strongest eligible open-capacity deployment, including safe immediate cold loading; then prefer eligible loaded deployments that can legally queue; then the best other legal queue. Keep five-minute related-request affinity. Use one explicitly configured decision deployment, bounded classification, and an owner-chosen fallback or clear error if it is unavailable. Reserve `best` now, but do not run this feature yet.

This resolves the otherwise ambiguous loaded-but-no-capacity fallback and keeps classification from becoming recursive. It does not judge user model ratings. Affects MODEL-05, BEST-01–12.

## Request recovery

### Q29 Queue survival after crashes

Should queued inference survive a Mango restart and execute later automatically?

**Recommended:** no durable inference queue initially. Keep bounded queues in memory, fail outstanding client connections clearly on restart, and require client retry under the replay policy. Persist configuration and required security/update state, not a hidden backlog of prompts.

Durable media jobs may later require a separate explicit contract with result retrieval, cancellation, retention, and duplicate-execution protection. Affects QUEUE-08, API-04, MOVE-05.

## Answer checklist

| Group | Decisions |
| --- | --- |
| Product and connectivity | Q01, Q02, Q03 |
| Authority and consistency | Q04, Q05, Q06, Q07, Q08 |
| Providers and models | Q09, Q10, Q11, Q12, Q13, Q14, Q15 |
| Scheduling and client behavior | Q16, Q17, Q18, Q19, Q20, Q21, Q22, Q29 |
| Interface and operations | Q23, Q24, Q25, Q26, Q27 |
| Future best routing | Q28 |

After these are answered, the next deliverable is a revised requirements baseline. The staged implementation plan follows that baseline rather than assuming unanswered recommendations have been accepted.
