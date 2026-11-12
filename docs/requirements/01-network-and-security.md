# Mango network and security requirements

Tailscale supplies private connectivity. Mango supplies enrollment, resource access, model metadata, and inference-key authority. These are separate trust boundaries, including when all devices belong to the same person.

## Connectivity and joining

**NET-01 — C.** A newly started node actively discovers reachable Mango peers through its tailnet and negotiates enrollment. It prompts for the master password when joining an existing network. An already enrolled node reconnects with its own identity rather than asking for the master password after every boot.

**NET-02 — I.** A peer must retain a small inbound service for enrollment handshakes, authenticated control messages, and inference traffic. Active discovery removes repeated scans by existing peers; it cannot eliminate listeners needed to receive requests. The requirement is low idle cost, not absence of listening sockets.

**NET-03 — P, Q02/Q22.** Use the installed and authenticated Tailscale client initially. Read the known peer set, try remembered Mango peers first, then probe only candidate tailnet addresses on the Mango service port with bounded concurrency and a deadline. Do not scan the tailnet address range or unrelated ports. Support a manual peer address when enumeration or discovery is restricted. A failed search must not silently create a second network.

**NET-04 — I.** Discovery reveals only enough information to identify a candidate network and negotiate supported versions. A node must authenticate the selected network before disclosing secrets or accepting configuration. Joining an unintended network with the same display name must not be possible solely because its response arrived first.

**NET-05 — I, Q05.** Retain stable node identities and join ordering for discovery and log collection, without majority voting or a consensus service. Rejoining nodes start reconciliation at the oldest known peer and continue through reachable peers as necessary. Enrollment establishes the intended network identity; copying an installation must not silently create duplicate live identities. Deterministic ordering of concurrent enrollments is a later implementation detail.

**NET-06 — C.** Peer traffic must work across the internet without requiring LAN discovery. Handle direct and relayed Tailscale paths. Do not assume low latency, unlimited bandwidth, synchronized uptime, or a permanent public IP.

**NET-07 — I.** Tailscale logout, expired device authorization, policy changes, or unavailable networking must produce a visible diagnosis. Local inference may continue if otherwise authorized and safe. Such a node must not claim successful network replication.

## Index and synchronization

**SYNC-01 — C.** Maintain a local network index. Push updates when model availability changes and reconcile once daily. A node may request an immediate refresh, including on join. Use deltas and revision summaries where possible rather than routinely transferring the complete catalog.

**SYNC-02 — I.** Propagate enrollment and removal, model registration and removal, aliases and capabilities, effective serving limits, contributor state, key issuance and revocation, logging policy, and update coordination. Dynamic queue and capacity hints may travel with admission responses or material state changes; per-token broadcasts are unnecessary.

**SYNC-03 — I.** Replicated changes require an authenticated origin, identity, revision, and replay protection. Duplicate or out-of-order delivery must be harmless. Deletions and revocations must not be undone by an old snapshot. Fetch a full snapshot only when missing history or a revision mismatch requires it.

**SYNC-04 — C, Q05/Q07.** Use lightweight eventual reconciliation. Master-authorized configuration changes use newest-wins conflict resolution; independent changes should merge where possible. Do not require a majority, all peers online, a consensus coordinator, or large receipt histories for durable writes. Key creation/revocation has the separate one-other-online-peer rule in KEY-09. Define deterministic equal-time ordering and clock-skew handling in the implementation plan; a hash alone cannot order conflicting versions.

**SYNC-05 — I.** Model observations and local resource withdrawal remain owned by the reporting machine and take effect there immediately, including without a majority. Shared requested configuration and locally observed operational state must be distinguishable. A remote edit cannot overwrite a newer local safety state.

**SYNC-06 — C, Q05/Q22.** Prefer events and compact config hash/revision timestamps carried with real peer requests. A mismatch triggers targeted reconciliation; an ordinary request without a mismatch does not trigger a general refresh. No proactive probing solely to track online membership. Idle provider inventory fallback is at most hourly. Every running node attempts reconciliation daily, even if contribution is disabled. Enrollment, settings changes, and routing real work are exempt from the idle cadence.

**SYNC-07 — I.** Failed updates enter a bounded retry path with backoff. Mark a peer's state stale when freshness is unknown. On reconnection, reconcile revocations and deletion markers before advertising that stale local information is current. Do not broadcast a separate full network snapshot in response to each peer's daily refresh.

## Network authority and recovery

**AUTH-01 — C.** The first peer establishes a master password and a separate recovery key. The recovery key can replace a lost master password. Show an exportable, printable recovery record and explain that it should be stored outside the network. Never print or upload it automatically.

**AUTH-02 — C.** A user holding the master password can create inference keys from any node. Joining peers requires that authority as well. Ordinary inference must not require sending the master password with every request.

**AUTH-03 — I.** Keep Tailscale credentials, Mango node identities, master/recovery authority, public inference keys, and provider API tokens separate. Never use the master password as an inference bearer token or a transport key. Never store it in plaintext or copy it into logs, URLs, provider requests, or command-line diagnostics.

**AUTH-04 — I, Q04/Q06.** Use the master password to unlock administrative authority locally with established cryptographic components and appropriate secret storage. Separate node identity, inference credentials, and recovery authority. Keep the implementation small; secure password handling does not require a distributed consensus framework.

**AUTH-05 — C, Q06.** Full reset replaces administrative authority, revokes all issued inference keys, clears sensitive logs and retained request/response data, and requires peers to reapply with the new master password. Preserve non-private inventory, machine descriptions, aliases, capabilities, model settings, and benchmarks. Benchmarks remain reusable when their hardware/model/runtime fingerprint still matches. Preserve metadata, not old authority: old credentials cannot silently restore access. Offline peers apply the reset and deletion state before returning to service. Recovery is intentionally allowed to be disruptive and rare.

Ordinary forgotten-password recovery is separate: use the recovery key to replace the master password while preserving issued inference keys, enrolled peers, model configuration, benchmarks, and logs. Invalidate old administrative sessions and propagate the changed administrative credential record through normal reconciliation. Do not invoke the full-reset path implicitly. This distinction was confirmed after the questionnaire.


**AUTH-06 — I.** Losing both administrative secrets does not erase installed provider models. Existing enrolled peers may still function while their credentials remain usable, but Mango must not offer an unauthenticated takeover path. Creating a replacement network remains possible; imported configuration must not import obsolete authority as valid.

**AUTH-07 — C, Q04.** All enrolled nodes can use resources and view shared state. Require a short administrative unlock for changing shared settings, enrollment, key management, retention deletion, or network update policy. Local-only controls require a session on the target machine. Public inference credentials never grant administrative authority.

## Inference keys

**KEY-01 — C.** Each key has a unique non-private display name for attribution, a stable identifier, allowed inference types, and one of three priority levels. Keys are independently revocable. Creation on any authorized node pushes its verification record to peers.

**KEY-02 — C, Q07/Q08.** Issued keys have immutable names, permissions, and priority until revoked. Use one fixed low/normal/high priority; clients cannot raise it themselves. A non-expiring key is available for any use when the owner chooses it. Concurrency and payload policies are shared network settings, not per-key quotas: one key may submit ten simultaneous requests subject to real capacity and priority. Set payload bounds generously for plausible context/media requests. Do not add exact distributed quota-accounting infrastructure. The default expiration duration is not yet fixed.

Trusted peer-originated requests carry authenticated node identity. Public requests retain their original key scope and priority when forwarded. Ordinary local requests default to normal priority unless configured by the owner.

**KEY-03 — I.** Verify credentials at ingress and enforce their effective authority at the serving node. A forwarded request carries authenticated client/key identity and effective scope and priority; a client-supplied header is not evidence. Do not send Mango credentials to a provider that uses its own credential.

**KEY-04 — I.** Replicate a verification record, not reusable bearer secrets. Show a newly issued secret once; provide replacement and revocation instead of secret recovery. Store token verification material appropriate to high-entropy tokens. A human password's slow verification function must not be run for every random internet token.

**KEY-05 — C.** If a presented key is unknown locally, query other peers sequentially. Stop when a valid authoritative record is found, cache it, and accept only after checking the presented secret, scope, expiry, and revocation state. An individual peer's assertion of validity is insufficient without verifiable authority.

**KEY-06 — I.** Distinguish unknown ID from known ID with an incorrect secret, known revocation, expiry, or insufficient scope. Only the genuinely unknown case triggers discovery. Never let a late valid record override a newer revocation. Peer lookups do not recursively fan out.

**KEY-07 — P, Q07/Q08.** Bound unknown-key recovery by source, ingress-wide budget, concurrent work, number of contacted peers, and total elapsed time. Coalesce identical lookups and maintain a bounded short-lived negative cache. Suggested starting limits are 5 misses/minute/source with burst 5, 30/minute/ingress with burst 10, 2 simultaneous peer lookups, at most 3 peers and 2 seconds per miss, and a 30-second negative cache capped at 1,000 entries. These are tuning proposals. Correctness and known-key inference must survive sustained random IDs and source rotation.

**KEY-08 — I.** Minimize unauthenticated work before parsing or buffering large bodies. Bound uploads, generation limits, connections, queues, compressed expansion, media decoding, and slow clients. Protect outbound media fetches from accessing unintended local or tailnet services. Authentication errors must not reveal the peer inventory or whether a specific secret nearly matched.

**KEY-09 — C, Q07.** Validate known keys locally. Push issuance and revocation changes immediately; reconcile stored key records before a new or returning node accepts requests whenever another peer is reachable. If no peer is reachable, the local record is usable, accepting the risk that a revoked key may temporarily work. No 60-second freshness lease or periodic authorization polling. Permit issuing or revoking keys only when the known network size is one or at least one other enrolled peer is currently reachable. Independent issuance/revocation events merge; revocation wins for the same immutable key. An older snapshot must never erase unrelated newer changes or resurrect a revoked key.

**KEY-10 — C, Q07.** Show incomplete propagation and local-only knowledge honestly. Stale acceptance while isolated is an accepted availability tradeoff, not a claim of global freshness. A known local revocation always wins. Retain compact revocation state so old peers cannot reintroduce revoked keys.

**KEY-11 — I, Q07/Q18.** Recheck local key authority before starting queued work. On learning a revocation, reject new and queued work for that key and cancel active work under the accepted security-cancellation rule. Do not poll to renew authorization during long-running requests.

## Optional public HTTPS

**PUBLIC-01 — C.** A node can expose authorized inference over public HTTPS when the user enables it. A public client need not join the tailnet or install Mango. Other peers remain privately reachable through the ingress.

**PUBLIC-02 — I.** Public exposure must include valid TLS, request authentication, rate and resource limits, and streaming support. Keep enrollment, provider administration, GUI control, logs, and update commands outside the public inference surface. Do not publish native provider ports as a shortcut around Mango authorization.

**PUBLIC-03 — C, Q03.** Support an existing HTTPS reverse proxy as the general path; offer Tailscale Funnel as a convenience where its domain, port, platform, and bandwidth restrictions fit. Exposing a service remains an explicit local action. The GUI checks the endpoint and explains who can reach it.

**PUBLIC-04 — I.** Trust proxy identity headers only from configured proxies. Apply effective limits even if the external source address is unavailable. A proxy or node changing addresses must not erase the identity used for per-key accounting.

Local browser access also needs authentication and origin/request protections. Being bound to loopback does not make a request from an unrelated web page an authorized local administrator or unrestricted inference client.
