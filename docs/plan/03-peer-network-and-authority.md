# Phase 3 Peer network and authority

Deliver joining and reconnecting Windows peers over installed Tailscale, shared metadata convergence, inference keys, and the two distinct recovery operations. This phase publishes the catalog but does not yet distribute execution; routing arrives in phase 5. Depends on phases 1–2.

Read the shared foundation and [Network and security](../requirements/01-network-and-security.md), especially NET, SYNC, AUTH and KEY requirements. Public listener setup belongs to phase 6.

## Network and enrollment

Use the installed Tailscale client to obtain address/peer information during setup and actual discovery. Reuse supported local notifications where convenient; do not make an unstable watch API mandatory. The setup page explains missing login, permissions, or networking. Keep a manual remembered peer address fallback.

Create a network ID, master-password verifier, independent recovery credential, and first node identity on first setup. Expose the printable/exportable recovery record only in the local administrative flow. Joining checks the selected network identity over its Tailscale address, accepts the master password in the protected enrollment exchange, and creates a random persistent node credential. Enrolled peers use their cached node credentials afterward; no password prompt on every boot.

Use established framework cryptographic operations: salted slow derivation for human-password verification, a high-entropy recovery secret, fast cryptographic digests for random node/API tokens, constant-time comparison, and Windows-protected local secrets. Slow password work runs only at enrollment/unlock/recovery. No custom encryption protocol, per-request signatures, CA service, or extra key-negotiation round trips. Tailscale supplies private transport encryption; only its interface accepts peer traffic. Local/browser and optional public HTTPS surfaces have separate bindings and access rules.

Store peer token verifiers with membership, not reusable peer secrets. Each node stores its own secret locally. Authenticate incoming peer requests with cached membership/credential data. Because enrolled peers are trusted, they can forward the identity and scope established at ingress; never trust those fields on public client requests. Master-authorized mutations are checked at their originating node, then accepted from authenticated enrolled peers. This is deliberately not a hostile-peer trust system.

## Minimal reconciliation

1. Give each editable shared record a stable ID and revision `(timestamp, writer node ID)`. Produce the next timestamp as the greater of current UTC milliseconds and one more than the largest observed revision timestamp. Compare the tuple deterministically. This keeps edits after an observed change newer despite a slow local clock; it does not pretend to know true real-time order between disconnected machines.
2. Apply newest-wins per independently edited record, not by replacing the entire database on a higher timestamp. Keep host observations separate from owner configuration. Retain compact deletion markers. No history of every edit or acknowledgment receipt is needed.
3. Assign a stable join ordering using the issuing peer's next observed enrollment timestamp plus node ID as a tie-breaker. Reenrollment during ordinary reconnect retains the ID. This preserves oldest-known ordering without a globally allocated sequence or quorum. Concurrent disconnected enrollments get deterministic ordering rather than a fictional globally exact birth order.
4. Compute a stable digest when records change; retain its timestamp. Carry this small summary on existing peer requests/responses. On mismatch, compare compact record revisions and fetch changed records from that peer, coalescing concurrent refreshes. A full snapshot is a fallback for initial join or missing state, not the normal request path.
5. Push changed records once to known peers on mutation. A failed delivery remains stale/pending; recover through the next relevant contact, reconnect, manual refresh, or daily pass. Do not run perpetual retry or online-map loops.
6. At startup/reconnect, contact the oldest known node first, then other known reachable peers to merge newer independent state. Reconcile key/reset/deletion information before enabling request admission when another peer is reachable. If none is reachable after bounded discovery, allow local state under the accepted isolation policy. Do not create a new network automatically.
7. Run one jittered daily reconciliation attempt on every running node, including leeches and nodes with disabled contribution. During idle time, no additional peer-presence scans. Route-triggered discovery is implemented in phase 5.

## Inference keys

Create opaque keys containing a public key ID and random secret. Store/replicate only the verifier plus immutable name, scopes, priority, selected expiry or never-expire, and creation identity. Before issue/revoke, permit a known singleton or establish that at least one other enrolled node is reachable. One reply is sufficient; no majority or all-peers acknowledgment gate. Persist locally and push the change, clearly reporting incomplete propagation.

Merge issuance and revocation by key ID. Distinct keys' changes coexist; revocation of a given key wins even if another snapshot still contains its issuance. Do not allow editing an issued key. Detect ordinary duplicate display names at creation using known state; if offline concurrent creation collides, show the stable IDs to distinguish them without changing immutable permissions or conflating the credentials.

Known-key requests validate locally. Unknown IDs query enrolled peers one at a time with a bounded total deadline and coalescing; known wrong secrets, expiry, scope failure, and revocation never trigger a search. Retain only a small bounded negative cache. The original numerical lookup limits remain tuning choices, not mandatory settings. Bound unknown-client work without imposing valid-key concurrency quotas. No recurring revocation-age checks.

## Recovery and reset

Ordinary forgotten-password recovery verifies the recovery credential, replaces the master-password verifier, invalidates administrative sessions, and propagates that record. Existing inference keys, peer enrollment, configuration, benchmarks, and logs remain. Returning peers learn the new administrative state through reconciliation; do not claim immediate password invalidation on an unreachable machine.

Full reset is an explicit separate action: new authority/reset generation, no valid old inference keys, private log/request-state deletion, and peer reenrollment. Preserve reusable model metadata and benchmark fingerprints. Old generations cannot restore authority or private records on reconnect. When restoring after complete state loss, explain that a recovery record cannot reconstruct absent catalog/key/log data. Reuse existing snapshot/reconciliation paths rather than a recovery coordinator.

## Minimum checks

- Two Windows peers on different networks join through Tailscale, reconnect without a password, and exchange a catalog change. A wrong network/credential is rejected.
- Small merge tests cover reversed/duplicate delivery, equal timestamps, skewed clocks, independent key issue/revoke, deletion, and stale-peer reconnect. Verify no full snapshot or password derivation occurs on matching ordinary requests.
- Exercise singleton key changes, multi-node isolation refusal, one-peer-online success, and accepted local key use while isolated.
- Recover a forgotten password without losing a key; full reset invalidates it while preserving aliases/benchmarks. Confirm secrets are absent from shared non-secret UI output.

## Done

Peers converge through pushes and actual contact, with clear stale states and no continuous membership service. Membership, administration, and inference credentials work without a global lock or verification round trip on every inference request. The protocol cutoff value is carried now; phase 8 supplies release-facing compatibility behavior.
