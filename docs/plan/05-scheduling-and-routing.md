# Phase 5 Scheduling and routing

Deliver concurrent inference across peers, local execution queues, the accepted priority/scarcity policy, useful cache affinity, and bounded failure retry. Depends on phases 2–4.

Read the shared foundation and QUEUE, ROUTE, AFFINITY and MOVE in [Scheduling requirements](../requirements/03-resources-and-scheduling.md). Reuse phase 4 admission and phase 3 cached authority/catalog state.

## Request ownership and fast path

An ingress owns the client connection and a bounded in-memory request record: original request ID, current attempt ID, effective authority, source IP, deadline, candidate history, and whether any output has reached the client. The serving node owns the admitted waiting/executing attempt and its memory reservation. There is one execution queue for a request, on its selected serving node. Local work calls that same code directly.

Use one authenticated peer HTTP submission that returns admission/error or streams output. Do not build a prepare/commit/reservation negotiation sequence before every request. Retransmitted control/submission IDs are recognized from bounded recent attempt state. Unknown-outcome retry handling is exceptional, not a second round trip on success.

Select from the cached alias deployments, rejecting wrong protocol, modalities, features, context/output fit, or contribution state. Prefer placements that preserve scarce machines for jobs requiring them. Contact likely candidates sequentially, using admission replies and recent actual work as capacity hints. A second independent request can choose another host without waiting for a periodic load scan.

If none of the known model hosts can be found, run one coalesced full discovery over tailnet candidates, with a ten-second per-node cooldown. Do not run it for every busy response or simultaneous missing-model request. Config digest mismatches trigger the phase 3 targeted merge; a matching request does not refresh the whole network.

## Queue rules

Use a small local collection ordered when admissions/completions change it, rather than a constantly running scheduling loop. Wake it on arrival, resource release, cancellation, provider state change, or the next actual deadline. One node can have independent resource sets without separate distributed schedulers.

1. Start immediately when safe capacity is available without delaying reserved work. Admit simultaneous executions only within the provider's proven slots and phase 4 memory limits.
2. A lower-priority request cannot wait behind conflicting higher-priority work unless the explicit scarcity exception applies. Same/higher priority can wait within network queue policy.
3. For the exception, the lower-priority request has only this eligible destination and the flexible higher-priority work can use a different eligible destination for subsequent requests. Use known model hosts and a targeted check when alternate eligibility is uncertain; do not broadcast. Finish the active request, reserve the scarce request's next feasible start, and advise the ingress where future flexible work should go. Do not create speculative remote reservations for requests that do not yet exist.
4. Higher-priority arrivals evict conflicting lower-priority waiting work, excluding an already protected scarce request whose exception still applies. Independent resource work continues. Evicted attempts return a reason and route hint to ingress; ingress tries another eligible destination or returns overload.
5. Within otherwise equal contenders, use priority and FIFO, carrying original ingress age through a legitimate reroute without accepting client-invented timestamps. No automatic priority aging. Backfill only if it cannot postpone the reserved scarce start.
6. Default queue residence is five minutes, set through shared master-authorized configuration. Enforce generous finite count/byte bounds based on supported context/media workloads; no per-key request-count quota. A request waiting through several destinations does not obtain an endless fresh deadline each time.

Ordinary scheduling never aborts active inference. Local activity, explicit client cancellation, known key revocation, or actual failure may do so. When an idle model blocks a better placement, unload it through the adapter before loading the needed model. Do not count memory as available until the release is observed.

## Affinity and relocation

Scope affinity by authenticated identity and explicit session identifier where provided. Otherwise use bounded in-memory message-prefix comparisons/digests to distinguish continuation from unrelated work. Retain it for five minutes after completion. One key/IP can represent several simultaneous conversations.

Tie cache hints to the real model instance and configuration. Invalidate them on unload, restart, or reconfiguration. Wait for cache reuse only if the estimated wait plus saved processing is better than an alternative and fits the client's deadline. When estimates are absent, use a simple best-effort preference; do not run a decision model or probe every cache.

For small models displaced from a large machine, finish active work, unload safely, consult saved alternate-host information, and send future-route advice to their ingresses. Keep the client's URL and connection unchanged. Transfer queued ownership only once; no live KV-cache migration or cross-runtime state translation.

## Failure and cancellation

Before execution starts, rejection can be rerouted directly. After execution starts, retry only when that execution has failed or is known stopped, no output including an error has reached the client, and the client is still listening. Prefer another eligible host before returning a final error. Record each attempt under the original request ID for later logs.

A connection timeout is not proof the backend stopped. On peer failure, use the existing request cancellation/deadline and a targeted status/cancel query if reachable. Where cancellation or process death cannot be established, fail the client rather than blindly running duplicate inference. Do not introduce renewable leases or a receipt service to solve this exceptional uncertainty. Providers that cannot cancel reliably must report that limitation.

Client disconnect cancels queued work and forwards cancellation to the serving provider. Retry does not survive ingress-process loss, and no disk-backed queue is required. Once output is delivered, a failure ends that response under its protocol; it never starts a second answer in the same stream. Backpressure bounds slow-reader buffers.

## Minimum checks

Use a table of concrete scheduler cases with deterministic resource/provider doubles: two independent hosts, two racing admissions, same-key concurrency, ordinary priority rejection, resource-specific purge, scarce low-priority admission behind flexible high-priority work, and no indefinite backfill. Verify cancellation releases the correct reservation once.

Run a two-node live example for streaming, a related follow-up, unrelated parallel requests, model displacement, failure before first output, failure after first output, and client disconnect. Check the metadata counters: matching valid requests do not perform full refreshes, repeated absent-model requests share the discovery cooldown, and retry attempts terminate.

## Windows qualification

- The Release host suite covered concurrent same-key requests, competing reservations, FIFO after reroute, priority rejection and displacement, and independent resource sets. Deterministic queue checks covered the scarcity exception, revalidation when an alternate disappeared or a new higher-priority request could not move, and independent work starting during a targeted eligibility check. Cancellation released one active reservation and fenced a delayed submission.
- Two enrolled Mango processes on the same Windows Tailscale interface used distinct ports and data directories. With CPU llama-server b12292 and Qwen2.5 0.5B Q4_0, a remote cold load streamed over one peer submission and a follow-up reported 214 cached prompt tokens. Unrelated concurrent requests under one key used both serving placements. A disconnected client drained its remote execution. Two missing-alias requests shared one discovery scan.
- A live llama-server router configured for one resident model unloaded an idle deployment before loading the requested deployment. Both aliases used the same GGUF artifact. The next related request for the displaced alias used the alternate host.
- Fault-controlled providers on the real Tailscale listener exercised a confirmed failure before output, a broken partial stream, and an unconfirmed provider stop. Only the confirmed failure before output reached the fallback. Three matching-digest requests made three peer submissions with zero additional digest merges or stop queries. A control request for a different serving process epoch returned an uncertain outcome.
- This run qualified CPU llama-server and the fault-controlled providers on one physical Windows machine. Cache benefits depend on the adapter and observed model instance. Placements with unavailable timing data use best-effort affinity. An unconfirmed stop remains ineligible for retry until the serving process can verify settlement.

## Done

Requests enter any enrolled node and are served by eligible peers without a central router, duplicate execution queues, or routine online-map polling. The observable scheduling examples match the owner's policies. No `best` routing, priority predictor, global optimization solver, distributed quota service, or durable job platform is added.
