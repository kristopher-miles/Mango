# Mango recorded decisions

Requirements revision 2, November 11, 2026. The submitted questionnaire matches the original baseline digest. All 29 decisions have responses; none is blank. This revision records policy decisions, not an implementation plan.

[Original answer export](decisions/2026-11-11-answers.json) preserves the exact submission and original source snapshot. The updated subject documents are the current requirements. Custom answers override conflicting old recommendations; a custom answer does not automatically accept every unrelated proposed default.

## Main changes

- Windows first; other peer operating systems later, with API clients unrestricted by operating system.
- Trusted single-owner peers, eventual newest-wins configuration, request-carried hashes, and no quorum or routine online-map probes.
- Local key verification with rejoin reconciliation and an accepted stale-key risk when isolated; immutable keys and one-other-online-peer requirement for issuance/revocation.
- Persistent benchmark policy, owner-controlled reserves, scarce-placement priority exception, five-minute queues, and ingress-mediated failure retries.
- Four request logging levels, client-IP attribution, separate optional operational logs, and partial aggregate delivery.
- Update integration points only initially; protocol compatibility defined by release cutoffs.

## Interpretation and remaining parameters

- Q07’s phrase about when keys are allowed is read as: issue/revoke only in a known one-node network or with at least one other reachable enrolled peer. No majority is required.
- Q15 lower sampling frequency means longer than two seconds between samples, not more frequent sampling.
- Q24 uses the explicitly offered four-mode split: Off, Sparse, Verbose inputs, Everything inputs and outputs.
- Shared network concurrency/payload policy removes per-key quotas; it does not authorize a new distributed counter/consensus service. The scheduler still admits against actual local capacity.
- Q06 defines full-reset behavior. The subsequent owner clarification resolves ordinary forgotten-password recovery: replace the master password using the recovery key without resetting inference keys. Preserve peers, configuration, benchmarks, and logs; invalidate old administrative sessions. Full reset remains a separate explicit operation.
- Exact equal-time/clock-skew conflict ordering, unbenchmarked capacity estimates, generous queue/payload counts, default key expiry, and numerical unknown-key limits remain implementation parameters or proposals. They must not contradict the approved low-overhead policy.
- For Q28, one keyword applies to each request, never to the model’s whole capability profile. The broader future routing scaffolding remains documented; unaccepted tie-break/fallback details stay proposals.

## Submitted answers

### Q01 Platforms and scale

**Submitted selection:** Narrow the launch platforms or scale; specify below.

**Owner’s answer:**

Windows is the first GUI path and the first listener to master (though of course clients can enter the network for requests from any node, and might have an arbitrary OS. Defer mac and linux nodes for later implementation steps. Performance targets acceptable, though idle periods with no requests and no changes to network configuration should have average traffic of 0 most of the time.

### Q02 Tailscale installation

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** require an installed, authenticated client initially and make setup checks clear in the GUI. Retain embedding as a packaging option to assess after the stack is chosen. `tsnet` is real, but it creates another authenticated device identity and ties the direct embedding path to Go.

### Q03 Public HTTPS

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** exposure is off until the local owner enables it. Support a general HTTPS reverse proxy, with Tailscale Funnel as an optional guided path when its restrictions fit. Creating an inference key never enables public serving by itself. Publish only the inference surface.

### Q04 Administrative access from peers

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** all enrolled nodes may use resources and view shared state; any node may administer the network after a short local master unlock. Require it for key management, enrollment, shared configuration, retention clearing, and update policy. Keep resource protection and process-control permission local to the host regardless of unlock elsewhere.

### Q05 Shared changes during partitions

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

This answer may work, but I want to avoid building a large amount of infrastructure around something like this. I don't want giant receipts being saved, and tons of tooling. I don't want all nodes to have to be online for any writes to be permanent, for example. On the other hand, I find it acceptable for configuration changes to be "newest wins" when it's an explicit decision made with the master key. The first thing a newly-joining node or a node that has been offline should do when re-joining is to reconcile its data with the network, grabbing whatever the most up-to-date information is that it can find beginning at the first node. Other nodes won't proactively probe all others all the time to keep an up-to-date internal map of "who is online". Rather, they'll make that probe when a request comes in directly to the nodes that hold that model. If none are found, they'll then perform a full network probe to see if a new node has joined that is serving it, with a cooldown of, say, 10 seconds. It's okay if information gets stale about other nodes so long as it reconciles over time.

Which BTW, another place to put that is requests sent. If they came with a tiny packet of the "network config hash" and its timestamp, that would also allow the node serving the request to say "hey, I think your config is out of date" or to realize "crap, my own config is out of date, I need to request an update from the client who just sent this.

We have one advantage here. Every network belongs to one user. We can assume there are no hostile peers acting maliciously, only machines that may genuinely be out of date.

### Q06 Password recovery and compromise

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

Important that private information in a network like this = log data, which might have sensitive user requests. That's what we should clear. Important authority = issuing new keys.

Non private information = models, aliases, machines on the network, routed model capabilities, etc. If we do a full reset, we should preserve the latter information while dumping all issued keys and clearing logs. Each machine will likely need to reapply to join, but it could preserve its information about models such that it can start serving them again with the same stored benchmarks once the new master password is registered. It's okay if it's a little disruptive, we expect it to be rare.

### Q07 Revocation while peers are offline

**Submitted selection:** Written answer without a selected option.

**Owner’s answer:**

Valid keys are stored locally. If a key is revoked, that status is pushed across the network when the revocation is made. If the machine it is pushed to has an older version of the authoritative hashed record, it replaces. If it is pushed to a machine that was offline, when that machine first comes online, it should update its stored keys from another peer before even taking a request (unless there are none, of course). Only if there are no other peers can its record be considered the most up to date (since it has no one to tell it otherwise). This does mean that there are rare cases a revoked key could continue to work on a client that was down intermittently, if the entire rest of the network is down when that client comes back online. That's acceptable. For safety reasons, keys should only be ALLOWED to be issued or revoked unless the network has a known size of 1, OR at least 1 other node is online. (rare conflicts are still possible, and we should try to apply all changes in them where they dont' conflict. For example, creating one key doesn't prevent revoking another. Keys can't be modified once created, so we don't have to worry about modify actions. They're made with a name, permissions, and priority, and those stay forever until revoked.

### Q08 Key policy and limits

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

non-expire is always a choice for any key created if the user wishes. Concurrency and payload limits and for the whole network not per key, we have priority for that. IE: if the user wants to send 10 requests on one key, they can. We don't have to worry about hardening this private service with extra verification infrastructure. Payload limits are set as high as is practical for requests, but most requests will be much lower than the limit in practice.

### Q09 Authority over existing inference servers

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** the local owner explicitly places each provider in managed or attached mode. Managed mode grants a defined load/unload/stop path and is required for a promise of automatic resource reclamation. Attached mode permits inference but clearly states when Mango cannot release the provider's resources. Do not stop unrelated processes or the provider GUI merely because names match.

### Q10 Initial API coverage

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** qualify Unsloth Studio/API and `unsloth run`, LM Studio native management plus inference, and supported llama-server single-model/router profiles. Cover model listing, chat completions with streaming and client-executed tool calls, embeddings, image generation, transcription, speech generation, and a specifically named structured-decision contract. Use the documented System One style as the decision-contract candidate to validate. Support each operation where the installed backend/model provides it.

**Owner’s answer:**

Unsloth and LM studio both give ways to know which models are loaded, and instruct models to be unloaded. These should be used when in managed mode.

### Q11 Provider tools and code execution

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** no provider-side execution by default. Preserve tool calls returned to and executed by the client. Explicitly disable backend-hosted tools when forwarding into a localhost provider, because the backend may otherwise trust the request as local.

**Owner’s answer:**

Only client-executed tools for now in initial release.

### Q12 Alias compatibility beyond modalities

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** retain exact matching of input/output modality sets and never validate quality claims. Also require compatible embedding dimensions/encoding and warn that equal dimensions do not establish a common vector space. Pin an embedding collection to a deployment or owner-declared compatible family. Preserve deployment-specific feature and context checks for every request.

**Owner’s answer:**

I want minimal verification infrastructure here. But knowing what kind of output/input are required prevents a request from failing.

### Q13 Benchmark permission and context verification

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** get local permission for the benchmark suite, show its expected size, and make it cancelable. Measure short, half, and full usable context with reserved output and synthetic input, subject to a time/resource budget. Mark skipped or failed tests incomplete and schedule conservatively. Require a configured or verified served limit when the provider cannot report one; never silently substitute the model's theoretical maximum.

**Owner’s answer:**

When joining a node selects what to do about benchmarks as "never", "now", and "when convenient." This setting applies to any later models or providers added to that node without another request for consent. Now means that all benchmarks must be finished before that machine serves anything. when convenient automatically tries to add a benchmark whenever there are no client requests to that node (not the whole network!) after a simple 5 minute cooldown, and can be interrupted by any incoming requests.

### Q14 Memory safety defaults

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

15 percent with a min of 2gb and a max of 5gb host RAM by default. 1gb host VRAM by default (this number doesn't scale, it's flat). It can be set as low as 0mb, which will require the user accept a warning that it may cause system instability or shutdown if they set the number lower than the default. However, we honor the user's choice once they consent. (and they don't have to consent again once they do so on that node).

### Q15 Activity detection and cooldown

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** local CPU/GPU/memory observation with up to 2-second samples, a 10-second sustained external-load trigger, immediate withdrawal for severe memory pressure, and five continuous quiet minutes before resuming. Renew the quiet period on new load. Manual leech mode persists. Treat missing attribution as a visible limitation and require a conservative local choice before automatic contribution.

**Owner’s answer:**

2 seconds seems like the maximum frequency, we don't want to hit the machine with constant polling. Can be set lower per machine. Also we should let the user set cutoff percentages and cooldown time, per node. memory pressure cause instant cooldown.(which we log, since this seems like an obvious point of failure if we start shutting down on our own induced loads)

### Q16 Scope of queue purging

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** clear only conflicting lower-priority work. A job using an independent GPU or genuinely nonconflicting CPU capacity should continue or be admitted if it cannot delay the higher-priority reservation.

### Q17 Scarcity versus priority

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

Yes, it does. If we can route a high priority request to another machine on its next request, then we can serve more requests at once with our network resources. This does not interrupt an ongoing request, it just allows the lower priority request to queue when it otherwise would not)

### Q18 Active work and starvation

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** let active requests finish during ordinary scheduling. Higher priority displaces queued work, while local activity, explicit cancellation, revocation, and faults can abort active work. Reserve an admitted scarce request's next feasible start and stop backfilling jobs that would delay it. Permit safe cancellation of loading work before inference begins when justified.

### Q19 Client identity and affinity

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** use an explicit session identifier scoped to authenticated key identity, with bounded in-memory prefix comparison/digests as fallback. Expire ordinary affinity five minutes after completion. Keep unrelated work under the same key independent. Wait for a cached instance only when estimated benefit beats another placement and the client deadline permits it.

**Owner’s answer:**

we want to also store client true identity (IP) as part of either logging option. We will use this for later QOS provisioning.

### Q20 Retries and partial results

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

Not after execution starts unless the running execution fails first. Once a client gets the output, no. There's no point, they already got their output, even if that output was an error. Though we should prefer retrying if another node can be found rather than sending the error to client.

### Q21 Queue limits and overload

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

120 of queue residence rejected, configurable master network setting default to 5 minutes. Local inference can be slow. Limits should be set generously for the maximum plausible request given context size, not strictly. Remember: clients are not hostile.

### Q22 Low traffic and failure detection

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

Per hour, not per minute when the network is idle. Once per day every node should attempt to reconcile even if not enabled. Ongoing requests carry signals about node integrity that can prompt more frequent updates, but this should be because a hash. is passed with a request and that hash shows the local node is stale, not because a request prompted a general update with no staleness signal. Genuine coordination such as for adding nodes, changing settings, and routing real current requests is of course exempt from this limitation.

### Q23 GUI form and headless machines

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

Generally yes, though I don't want us jumping through expensive accessibility hoops. It's a pretty basic app. Anything we can cheaply provide without tons of verification is fine.

### Q24 Logging contents and privacy

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

yes, tts appears to be a typo for tps. Verbose excludes credential tokens but DOES record the token identifier name AND also records responses. That's the point of verbose. Input and output are recorded. Operational state changes can have their own logging that will be useful during development and can be disabled. We can also subdivide verbose into "Everything" which includes response bodies and Verbose which excludes them.

### Q25 Daily logs and deletion

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** one network timezone selected at setup, defaulting to the first node's timezone; UTC timestamps; seven days of raw records and thirty days of aggregate metadata, within a 1 GiB per-node total cap. The lowest eligible join ID collects; offline peers produce a visibly incomplete aggregate, with late merge and deduplication. Network clear remains pending for offline peers and applies a deletion generation before their old data can rejoin.

**Owner’s answer:**

generally agree, but an incomplete log should still be provided when requested. A log that's missing pieces is better than a log that has nothing.

### Q26 Coordinated updates

**Submitted selection:** Accept the full recommendation.

**Accepted recommendation:** coordinate one verified release intent and queueing window, then perform rolling installation with bounded draining, local deferral, health verification, and rollback when the data format permits it. Warn before proceeding when a peer cannot verify eligibility; require the initiating user's explicit compatibility-risk override. Never override an invalid signature.

**Owner’s answer:**

This feature won't even work since we won't have an update server yet, we'll just make sure it has plumbing for when that server does exist.

### Q27 Mixed-version compatibility

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

I don't want lots of verification overhead. We instead provide "Cutoffs" in a release that disqualify all older releases when they are made but allow any newer releases that don't themselves come after another cutoff. Other releases that don't include one of these cutoff flags are permitted until the next cutoff flag, creating windows where "we can change somethings so long as the protocol doesn't change." When the protocol genuinely changes, we create a cutoff, bounding into a group of products with compatible communication protocols.

### Q28 Best-model interpretation and preparation

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

The thing I don't like is the "one primary task label" models may have a score at code, creative writing, formal writing, summary generation--arbitrary user tasks. Where models can have competency in all those tasks at different scores.  Incoming REQUESTS must be rated to only whatever ketyword in the system best matches the available options, only one keyword per request.

### Q29 Queue survival after crashes

**Submitted selection:** My own answer / modify the recommendation.

**Owner’s answer:**

If client disconnects or is interrupted, the request can fail with no resume. What I want to be a little more durable is if the network has a failure. Since the client is still listening to whatever node it talked to, that node can check to see if another node can serve the request before failing that client. That's what I mean by durable. If the answer is still no, that client can still fail. Otherwise, the network can retry somewhere else. If the disconnect is on the client side, no retry guarantee is provided or expected. We should even try to forward the cancelation to the destination when a client is no longer listening, that way we don't waste resources for a client who isn't there.

## Next deliverable

The owner has now requested the staged implementation plan. See [Shared implementation foundation](../plan/00-shared-foundation.md). No Mango application implementation or update-service deployment has begun.
