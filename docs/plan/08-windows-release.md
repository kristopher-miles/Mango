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

## Release 0.1.0

Build with `./scripts/package-windows.ps1` in PowerShell 7 and the SDK pinned in `global.json`. The script publishes a self-contained Windows x64 release with .NET 10.0.15 and the static browser UI. It stages files in a new directory and writes the ZIP in ordinal path order with fixed entry timestamps. `Install.md` in the archive describes installation, headless and login startup, provider setup, private and public ingress, and removal. [Install and remove Mango](../windows-install.md)

The checkout and a separate source directory without Git metadata both produced `dist/Mango-win-x64.zip` with SHA-256 `441362C2A0B669B311EDD841E186438CE8B52EB71C75CF252A4D1D7C2D22A2FF`. The 435 entries occupy 64,447,333 bytes compressed and 151,816,793 bytes extracted. The package serves static UI files directly and omits the build-specific static endpoint manifest. Every shipped file matched the bytes of the measured installation. Archive inspection confirmed the package contains application and runtime files, the UI, and the install guide.

The build advertises version 0.1.0 and protocol window 1. The private `/peer/info` introduction and `Mango-Release` header are the stable diagnostic surface across windows. Hosts reuse observed release compatibility. Matching windows may differ in application version. A peer control or inference request for an incompatible window receives HTTP 426 before Mango binds its body. The protocol field in the network snapshot preserves its original identity. Live compatibility uses the running build descriptor. Builds before the first release may lack the release header.

Automatic updates are unavailable. The host-local maintenance preference persists alongside resource and role settings. The serving drain closes local admissions, returns waiting attempts for normal rerouting, and lets active work finish to a bounded deadline. A timeout or unresolved provider result leaves the drain incomplete. Resuming local admissions preserves the saved role. Future installers must also quiesce or account for client connections this node owns as an ingress to remote hosts before restarting it.

Future coordination should verify the publisher signature and local eligibility before sharing a release intent. Warn about unknown or incompatible peers and require an explicit override of compatibility risk. Signature failure remains a hard stop. Queue the verified release together and install it in rolling order, respecting local deferral. Verify service health before advancing. A rollback needs a prior binary and data format that are compatible. Mango's update authority covers Mango itself. Tailscale, drivers, provider applications, and model weights retain their separate lifecycle.

## Windows qualification

Qualification used Windows 11 Pro x64 build 26200, an AMD Ryzen 5 7640HS, integrated Radeon 760M graphics, and Tailscale 1.104.5. The Release suite ran on the live Windows Tailscale interface with separate data directories and ports for its peers. The 20-peer metadata scenario ran one extracted Mango process with 19 simulated peers and 100 advertised deployments. It measures routing and idle overhead at that metadata size.

- With the measured host's browser closed, the 901.7-second idle interval averaged 0.1438% of one CPU core and peaked at 126.2 MiB resident. The fixture listeners received zero Mango peer HTTP calls and therefore zero Mango application bytes. Tailscale transport maintenance is outside the HTTP measurement.
- Across 100 warmed known-key requests on loopback, full HTTP p95 was 2.357 ms. After subtracting each request's measured provider preparation and execution envelope, p95 was 1.504 ms. Cached authorization and loopback HTTP overhead remain in the measurement. This workload required no authorization refresh, model load, capacity wait, or WAN transit. The fixture received zero peer calls. The successful path uses the cached key verifier and network digest. Password derivation is limited to authority operations.
- One shared metadata edit sent 19 delta pushes. Two consecutive missing-alias requests shared one discovery run and made 19 introduction calls to the configured fixture peers. Run the bounded measurement again with `dotnet run --project tests/Mango.Tests -c Release -- --qualify-release <extracted-Mango.exe>` on the Windows Tailscale interface.
- The full Release suite passed with zero build warnings. Distinct versions enrolled in window 1. Restarting one peer in window 2 blocked malformed peer control and inference bodies in both directions and left the stable introduction accessible. Returning the peer to window 1 restored compatibility. The local deferral persisted across restart. The drain fixture returned waiting work for rerouting, left active work alone at timeout, and resumed admission after settlement.
- On the final packaged UI in Edge 157.0.4341.46, ten keyboard saves showed visible feedback after a median of 131.5 ms and a maximum of 143.6 ms. The measurement includes browser automation and the loopback response. The release and unavailable-update status appeared in the host panel. PowerShell captured `--url` from the actual Windows executable through a pipe, and `--quit` stopped the host.
- Two packaged processes exercised Tailscale enrollment, keyed cold remote native llama.cpp chat, SSE and exact retained output, one peer submission, model-discovery cooldown, clearing, recovery preserving the key, and full reset invalidating it. The release executable also sent authenticated Qwen SSE through Unsloth Run 2026.11.4 and confirmed native unload. A catalog refresh recovered the initial cold inspection timeout and reported 2,048 tokens of served context. An earlier candidate exercised the installed Bionic 1.1.9 build 6 LM Studio API with native managed Qwen load, reported 2,048-token context, complete SSE, and observed unload. The only later application code change was the local maintenance response status.
- The retained live qualification covers llama.cpp b12292, Unsloth Run and Studio 2026.11.4, and the LM Studio API in Bionic. It includes chat, embeddings, images, speech, transcription, and System One on the model and provider combinations in the provider plan. The Release suite rechecked resource withdrawal and recovery, priority and scarcity queues, cache affinity, retry safety, local TLS ingress, all four log modes, partial collection, and clearing. Recovery preserved a usable key and full reset invalidated it while keeping reusable model settings. [Provider results](02-provider-integration.md) · [Resource results](04-resource-protection.md) · [Routing results](05-scheduling-and-routing.md) · [TLS results](06-gui-and-public-access.md) · [Logging results](07-logging-and-retention.md)

Representative resident working sets were 118.5 MiB for Mango with its GUI open, 715.8 MiB for the nine Edge processes, 133.5 MiB for the three Tailscale processes, and 590.7 MiB for an idle llama-server with Qwen2.5-0.5B Q4_0 resident. Their arithmetic sum was 1,558.5 MiB with the GUI open. Combining the measured Mango idle peak with the Tailscale and loaded llama-server figures gives 850.4 MiB with the GUI closed. The totals combine separate process working-set samples and can count shared pages more than once.

During sleep the host serves no inference. On a detected interface change it reconciles with reachable peers before resuming enrolled service. An isolated host can use its last known key state and cannot learn a new revocation until contact resumes. Lost metadata pushes can converge at targeted or daily reconciliation. The UI reports last-observed reachability. Under packet loss or partitions, a remote attempt whose outcome is uncertain remains ineligible for automatic retry. Key expiry uses wall-clock time, so clock jumps can change when an expiry is observed. Request timing uses local monotonic durations and never subtracts another peer's clock.

Open qualification items are distinct Windows machines over a WAN, outside-tailnet internet or Funnel reachability, discrete GPU VRAM, a Windows sign-out and sign-in cycle, and OS-level wakeup counts. Earlier live TLS checks used a loopback HTTPS reverse proxy and a test-only certificate authority. The package's per-user tray launch and headless executable controls were exercised directly.
