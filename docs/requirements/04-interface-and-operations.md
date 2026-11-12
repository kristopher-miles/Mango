# Mango interface and operations requirements

Every node provides a clear view of the network while keeping local resource protection under that machine's control. Logs and updates are network operations with explicit completion and failure states; an offline peer must never be reported as updated or cleared.

## GUI and settings ownership

**UI-01 — C.** Provide a responsive, intuitive GUI on each machine. From any node, an authorized user can inspect the network, configure provider/model listening and registration settings, change aliases, assign capabilities and ratings, and see the result across the network.

**UI-02 — C.** Activity monitoring, automatic withdrawal, cooldown, and RAM/VRAM safety margins can be changed only on the relevant machine. Remote access to shared model settings does not grant permission to disable another machine's protection.

**UI-03 — I.** Distinguish requested configuration, committed shared configuration, and effective host configuration. Show pending, applied, rejected, conflicted, and offline states. Do not display an optimistic save as completed when the owning node has not accepted it. Reconfiguring or disabling an endpoint must account for active work.

**UI-04 — I.** First-run setup supports creating a network or joining an existing one, Tailscale connection checks, credential recovery/export, contributor or leech selection, provider registration, lifecycle permission, and benchmark permission. If an existing network is temporarily unreachable, explain the failure rather than steering the user into accidental duplicate initialization.

**UI-05 — I.** The network view shows node identity and join order; contributor state and reason; reachability and metadata freshness; app/provider versions; models and aliases; effective context; resource limits; benchmark confidence; and loaded, running, queued, or unavailable status.

**UI-06 — I.** Request diagnostics explain the chosen destination, wait reason, effective priority, model loading, reroutes, rejection, and local withdrawal. Expose timing estimates as estimates. Live views must not force persistent request logging when logging is off.

**UI-07 — C, Q23.** Use standard, low-cost accessibility features: native labels, keyboard operation, readable contrast, scalable text, and clear status indicators. Support responsive windows and progress during lengthy operations without building an expensive accessibility verification framework. Closing the GUI does not stop background contribution unless the owner chose that behavior.

**UI-08 — C, Q23.** Use a local desktop/tray experience with a background service and a browser-accessible local GUI for headless installations. Keep the administrative surface private and authenticated. Responsive does not automatically mean a native mobile application or internet-accessible admin page.

### Setting boundaries

| Setting | Owner and allowed editing location |
| --- | --- |
| Model alias, task capabilities, ratings, shared model catalog | Shared; editable from any authorized node |
| Provider endpoint registration and model serving configuration | Shared requested settings; validated and applied by owning host |
| Provider credential and process-control permission | Secret/local authority on provider host |
| Activity monitoring, thresholds, cooldown, contributor override | Local machine only |
| Memory safety margin and device-use constraints | Local machine only |
| Inference keys, membership, master/recovery changes | Network administration from any permitted node, subject to consistency policy |
| Public ingress enablement, proxy binding, TLS material | Local host action, subject to network key policy |
| Logging mode, retention, aggregation timezone, network clearing | Shared policy, administered from any authorized node |
| Update proposal and shared update policy | Network-level coordination |
| Update installation permissions and local maintenance deferral | Local host constraints; cannot be overridden by an ordinary inference client |

## Logging modes and request records

**LOG-01 — C, Q24.** Provide Off, Sparse, Verbose, and Everything. Sparse retains metadata only. Verbose adds exact inference request bodies with credential exclusions. Everything adds response bodies too. Off disables request logging. These four labels use the owner-approved split between verbose input-only and complete input/output capture.

**LOG-02 — C.** Sparse metadata supports analysis by client/key name, model, destination, speed, elapsed wall time, request length, time to first token, model load delay, and queue delay. Key names are unique non-private labels chosen at creation and remain resolvable for historical reporting.

**LOG-03 — C, Q19/Q24.** In every enabled request-log mode, record client source IP, key ID and non-secret name-at-time, request/attempt IDs, ingress/serving node IDs, alias and real deployment, task/priority, timing, outcome, reroutes, and byte/token counts with units. Preserve client IP across internal forwarding for later QoS; accept proxy-origin IP only from a trusted configured proxy. If the original address is unavailable, label it unknown or proxy-observed rather than claiming a false true IP. IP is attribution, not a unique authenticated person or conversation.

**LOG-04 — I.** Timing records distinguish ingress receipt, admission, queue residence, provider load, prompt preparation, first output, completion, and response delivery where measurable. Use monotonic duration measurements on each host and wall-clock timestamps for ordering. Do not subtract unsynchronized clocks on different machines as though they shared one timer, and do not double-count overlapping load and queue intervals.

**LOG-05 — C, Q24.** Interpret the specification's `tts` metric as tokens per second for text; use the full name to distinguish it from text-to-speech. Preserve unknown or inapplicable values as such. Audio and image requests use relevant units such as input seconds, output seconds, pixels, or images, plus common latency measurements.

**LOG-06 — C, Q24.** Default to Sparse. Verbose captures inputs; Everything captures inputs and outputs, including streamed responses. Keep the key identifier/name but exclude actual credential tokens, cookies, master/recovery secrets, and provider secrets in all modes. Prompt and response content may itself be sensitive. Incomplete capture is marked rather than called exact.

**LOG-07 — I.** Log request content at one designated location, preferably ingress, and execution metadata at the serving node. Do not duplicate full payloads at every relay. Synthetic benchmark requests and future internal decision-model calls are identifiable so they do not masquerade as user traffic.

**LOG-08 — I.** Logging is bounded and must not block inference indefinitely. Show dropped, truncated, or failed records and disk exhaustion. A record missing bytes cannot be labeled an exact verbose capture. Temporary routing state is distinct from logging and must expire even when logging is off.

**LOG-09 — C, Q15/Q24.** Operational changes may have their own independently disableable diagnostic log, including resource-pressure withdrawal and provider lifecycle failures. It is not a hidden request-payload log. Required configuration, revocations, deletion markers, and unfinished update state remain persisted operational state even when all logging is off.

## Daily collection and retention

**LOG-10 — C.** At the end of each day, the surviving node with the lowest join ID requests logs from all other peers and builds a network-wide daily log. Support the same operation on demand. The collecting node may be a leech; collecting logs does not require inference contribution.

**LOG-11 — I.** “Surviving” means enrolled, not removed, and currently eligible/reachable under the agreed coordination policy, not merely present in an old catalog. A collector that disappears must not permanently prevent collection. The first node is not a permanent logging master.

**LOG-12 — C, Q05/Q25.** The lowest-join-ID reachable collector gathers daily or on-demand records without a quorum service or continuous liveness map. If partitions cause duplicate collection, deduplicate by node/event identity on merge. Always provide the available partial aggregate when requested, labeled with missing peers and coverage; do not withhold useful logs merely because collection is incomplete.

**LOG-13 — C, Q25.** Use one configurable IANA timezone for network daily boundaries, defaulted from the first node during setup. Store timestamps in UTC. Daylight-saving days may be 23 or 25 hours. Do not independently interpret “end of day” in each peer's local timezone.

**LOG-14 — I.** Collect by node ID and event sequence/watermark in bounded chunks. Track nodes received, offline, logging off, failed, and beyond retention. Allow late arrivals to revise a daily aggregate without duplicating already collected events. Never report a missing peer as zero usage.

**LOG-15 — C.** Configure retention for the whole network, including how much log data any client preserves. Provide a network-wide clear action. Retention applies to raw records, daily aggregates, and attached verbose payloads rather than leaving hidden duplicate copies indefinitely.

**LOG-16 — C, Q25.** Start with seven days of local raw records and thirty days of aggregate metadata, subject to a 1 GiB total Mango log cap per node. Keep Verbose and Everything payloads only for the raw retention window. Delete oldest data first and report when the byte cap shortens the intended time window. These retention defaults are accepted.

**LOG-17 — I.** Network clear records a durable deletion generation, clears reachable local stores and aggregates, and marks unreachable peers pending. On reconnection, apply the deletion generation before accepting or uploading old logs. Clearing is not complete until all enrolled peers acknowledge or are explicitly removed. Data already exported by the user is outside Mango's control.

**LOG-18 — I.** Explain that a crashed or missing collector's local aggregate may be lost unless the original peer records still exist. Do not promise durable archival redundancy beyond the chosen retention and replication policy. Do not let log collection or verbose payload transfers monopolize inference traffic or interfere with local user activity.

## Software updates and compatibility

Q26 accepts the coordinated-update behavior as a future contract. Initially provide the integration points and an explicit unavailable state: no update server exists yet, so discovery, eligibility, or installation must not claim to work. Do not build or deploy a release service as part of the initial scope. Protocol cutoff reporting remains applicable now.

**UPDATE-01 — C.** When a node proposes an update, other nodes independently check the public release endpoint, determine whether their own host is eligible for that release, and queue eligible updates together. If compatibility cannot be verified for a peer, warn the initiating user against proceeding until the warning is manually overridden.

**UPDATE-02 — I.** Eligibility includes OS, architecture, Mango version, protocol range, configuration/data format migrations, required provider/API versions, runtime prerequisites, and available installation resources. A public version string alone is insufficient evidence of eligibility.

**UPDATE-03 — I.** Authenticate release metadata and artifacts against an established publisher trust root; verify integrity and protect against rollback or stale metadata. A peer's claim that a file is safe is not verification. A user override of mixed-version risk must not become an override of a bad artifact signature.

**UPDATE-04 — C, Q26.** Coordinate a common release intent and queueing window, then install in a controlled rolling order with local maintenance deferral and enough remaining capacity where possible. Simultaneous queueing does not require simultaneous restarts. Before restart, stop new admissions, drain work to a bounded deadline, and reroute waiting requests; do not silently abort active work to meet a synchronization timestamp.

**UPDATE-05 — C.** Different application versions do not automatically prevent connection. Warn and discourage avoidable divergence, but allow interoperable behavior where supported.

**UPDATE-06 — I.** Use the protocol cutoff window independently from application version numbers; retain only necessary operation/adapter capability checks. A peer may remain connected for diagnostics while a specific unsupported operation is unavailable. Never accept an unsafe or undecodable message solely to avoid a version lockout.

**UPDATE-07 — C, Q27.** Use release cutoffs to define protocol compatibility windows. A protocol-breaking release starts a new window; releases within the same window may communicate without a broad per-version certification matrix. Each peer compares the advertised window, so an older peer cannot assume a newer release remains compatible after another cutoff. Different version numbers alone do not block connection within a window; incompatible windows reject unsafe protocol operations with a clear diagnosis.

**UPDATE-08 — I.** Update states include proposed, verifying, eligible, incompatible, unreachable/unknown, queued, draining, installing, restarted, healthy, failed, and rolled back where supported. Offline nodes learn the desired release on reconnect and reverify before installing. They cannot be shown as already queued locally without acknowledgment.

**UPDATE-09 — C, Q26.** Back up Mango configuration before migration, use a recoverable install path, verify service health, and roll back only when the previous binary can safely read the preserved data. Treat an irreversible schema migration as a separate compatibility constraint, not something a binary swap automatically reverses.

**UPDATE-10 — C, Q26.** Coordinate Mango updates first. Do not automatically upgrade Tailscale, GPU drivers, provider applications, or model weights. Report dependencies that need local action. Avoid a peer instructing another peer to execute arbitrary downloads or commands.

**UPDATE-11 — I.** Automatic checks use bounded timeouts and backoff. Release verification failures, no eligible build, partial rollout, and a peer refusing or postponing an update remain visible. An update cannot disable local activity protection or rewrite local process permissions.
