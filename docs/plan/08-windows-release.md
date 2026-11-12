# Phase 8 Windows packaging and release completion

Deliver the complete non-deferred Windows application as a reproducible local package, with compatibility cutoffs, clear update unavailability, and bounded final qualification. Depends on phases 1–7. This is the final implementation phase, not permission to implement deferred features.

Read the shared foundation, NFR-01–06, UPDATE-01–11 with the Q26 deferral, and the applicable [Acceptance scenarios](../requirements/06-acceptance-and-coverage.md). The historical proposed numeric limits do not become release gates.

## Packaging and lifecycle

1. Produce a reproducible Windows package from the application project, including its static UI and required runtime components or an explicit supported runtime prerequisite. Prefer a per-user package and simple install/remove path over a custom installer engine. Keep machine-wide services and elevation out unless a concrete supported provider integration requires the owner to perform a separate local action.
2. Include launch, headless, optional start-at-login, and uninstall instructions. Start-at-login must preserve the user's manual leech/resource preferences. Removing application files must not silently delete provider models or unrelated data; explicit Mango data clearing uses the existing UI/reset behavior.
3. Keep runtime configuration, node/API/provider credentials, recovery exports, model weights, benchmark profiles, request/media payloads, and logs out of the repository and release archive. Ship sample configuration without secrets only if needed to explain setup. Do not bundle the owner's questionnaire answer export into the installed application.
4. Document how to install/authenticate Tailscale, connect the supported provider profiles, choose managed/attached authority and benchmark policy, configure public ingress if wanted, and recover a forgotten password versus perform a full reset. Use the GUI's real behavior and tested limitations; avoid a separate operational-control toolkit.

## Compatibility cutoffs

Use one release descriptor containing application version and protocol cutoff/window ID. It is immutable build metadata, not downloaded on each request. Peers in the same window communicate despite different app versions; a protocol-breaking release changes the window. Compare it during peer introduction/reconnection and reuse the result. If the other peer is in a different window, show the mismatch and do not decode unsupported control/inference messages.

The comparison works in both directions: an older release must not treat all numerically newer app versions as compatible. A minimal version/status response can explain an incompatibility without attempting full protocol negotiation. Do not implement a current/previous-version feature matrix, certification system, or negotiation packet on every inference request.

## Update integration points only

No update server exists. The release must state that automatic update checking/installation is unavailable and must not contact a placeholder public URL. Add only the small concrete seams needed by a future updater: current release descriptor, local maintenance deferral, stop-new-admissions/drain operation already used by the scheduler, and a status/result location the GUI can display.

Document the future flow beside these seams: verify a publisher release and local eligibility; share update intent; warn on unknown/incompatible peers; require the user's compatibility-risk override; queue together; install in rolling order with local deferral and supported rollback. A bad signature is never an overrideable compatibility warning. Preserve local resource controls and exclude providers, drivers, Tailscale and weights from Mango's update authority.

Do not implement a release server, remote installer, signing infrastructure, generic update state machine, package mirror, rollback framework, or recurring update polling now. Manual replacement of the Mango package may keep a last-good configuration copy using the existing snapshot mechanism. Describe data-format compatibility rather than promising every downgrade can work.

## Final qualification

Reuse the existing phase checks. Run one connected end-to-end pass covering setup and reconnect, all three adapters, supported inference modalities, resource withdrawal/recovery, queued priority/scarcity behavior, cache affinity, safe failure rerouting, public ingress, four logging modes, partial collection and clearing. Confirm ordinary password recovery preserves a working key and full reset invalidates it while preserving reusable model settings.

Use actual Windows provider/hardware checks for behavior that fixtures cannot establish. Record tested combinations and known limitations. Simulated peers can cover the 20-peer/100-deployment metadata envelope; do not pretend that this proves inference throughput on 20 physical machines. Do not build a general simulator or require unavailable hardware merely to execute unrelated unit tests.

Measure the approved targets on named hardware: idle background CPU below 1 percent of one core averaged over 15 minutes, background resident memory at most 150 MiB, usually zero unchanged-idle Mango traffic with the accepted upper ceiling, local UI feedback near 200 ms, and local healthy-path routing/admission overhead below 25 ms p95 with its documented exclusions. Report the browser/UI, Tailscale and provider footprints separately and in the installed total. If a target is missed, identify and fix the actual hot path; do not add a telemetry service to measure it permanently.

Check specifically that the successful known-key path has no password derivation, full-catalog hash computation, peer-online scan, fresh protocol negotiation, or remote revocation-age verification. Count actual calls during idle, normal requests, metadata changes, and missing-model discovery. These are bounded release checks, not permanent intrusive verification.

## Coverage and deferred boundaries

| Requirement area | Owning implementation phase |
| --- | --- |
| Windows process, persistence, local controls | 1 |
| Providers, inference contracts, aliases, effective context | 2 |
| Tailnet discovery/enrollment, eventual state, keys, recovery/reset | 3 |
| Benchmark modes, memory profiles, activity protection | 4 |
| Concurrent routing, queues, scarcity, affinity, relocation/retry | 5 |
| Complete network GUI, local/remote setting boundaries, HTTPS ingress | 6 |
| Request/operational logs, daily/on-demand aggregate, retention/clear | 7 |
| Packaging, cutoff windows, update hooks, performance qualification | 8 |

The `best` classifier, non-Windows peers, embedded Tailscale, full update infrastructure, extra API families, model distribution/training, and persistent inference jobs remain deferred. Capability labels and per-model ratings are already useful catalog data; the reserved `best` name returns an unavailable feature rather than silently selecting a model. No deferred feature is a prerequisite to shipping this release.

## Done

The Windows package installs/runs through the documented path, all non-deferred user flows work, relevant checks pass, and actual limitations/performance results are recorded. The source tree and runtime data are cleanly separated. Completion does not require publication, a hosted service, a remote Git repository, or any unrequested infrastructure.
