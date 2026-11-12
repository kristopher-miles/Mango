# Mango shared implementation foundation

This is the common contract for eight implementation phases. Read this document and the current phase; consult only the requirement sections that phase names. The subject requirements and recorded owner decisions govern. Historical questionnaire recommendations do not override them. This plan does not implement Mango.

## Delivery order

| Phase | Deliverable | Depends on |
| --- | --- | --- |
| [1 Windows host](01-windows-host.md) | Background process, persistent settings, local UI and tray entry | None |
| [2 Provider integration](02-provider-integration.md) | Three adapters, model catalog, actual inference protocols | 1 |
| [3 Peer network and authority](03-peer-network-and-authority.md) | Tailscale joining, reconciliation, immutable keys, recovery/reset | 1–2 |
| [4 Resource protection](04-resource-protection.md) | Benchmark policy, memory admission, activity withdrawal | 1–3 |
| [5 Scheduling and routing](05-scheduling-and-routing.md) | Distributed requests, priority/scarcity queues, affinity and failure retry | 2–4 |
| [6 Network GUI and public access](06-gui-and-public-access.md) | Complete administration and optional public HTTPS inference | 3–5 |
| [7 Logging](07-logging-and-retention.md) | Request/operational logs, daily collection and deletion | 3–6 |
| [8 Windows release](08-windows-release.md) | Packaging, cutoff handling, update hooks and final qualification | 1–7 |

Each phase is a reviewable increment. Earlier phases need not deliver later user flows; they must not claim those flows work. Keep intermediate builds private until resource control, key enforcement, and routing are connected.

## Implementation shape

Use C# on .NET 10 LTS, ASP.NET Core Kestrel for local/peer/inference HTTP, and ordinary HTML/CSS/JavaScript for the responsive browser UI. Use Windows Forms `NotifyIcon` only for a tray menu opening that UI and switching local contribution. One background process owns state, queues, adapters, and HTTP. Headless mode omits the tray; closing a browser leaves inference running. Start per user, with optional login startup. An OS service, Electron, WebView wrapper, separate Node server, frontend framework, message broker, and container platform are unnecessary.

This uses supported platform facilities: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [Kestrel](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel?view=aspnetcore-10.0), and [NotifyIcon](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/controls/notifyicon-component-windows-forms). Pin the supported .NET 10 servicing version during implementation; do not introduce a runtime version-discovery service.

Keep one application project, one small test project, and static UI assets. Organize folders by host, providers, peers, scheduling, and logging as those features arrive. Use direct calls and plain records. Add an interface only at the provider boundary or a platform boundary that actually needs replacement in a meaningful test. No generic event bus, plugin marketplace, dependency-injection wrappers, or future-platform implementation.

Persist small configuration snapshots as JSON using a serialized writer, temporary file, atomic replacement, and a last-good copy. Use separate files for local settings, shared records, and benchmark profiles so a shared update cannot rewrite local protection. Append logs to bounded daily files. Runtime queues and affinity remain in memory. No database or distributed transaction system is needed at the approved scale. Persist data outside the checkout under the current user's Mango application-data directory.

## Shared records and ownership

| Information | Ownership and persistence |
| --- | --- |
| Network ID, reset generation, protocol cutoff | Small shared identity record |
| Node ID, join order, address hints | Shared membership; stable node ID is not an IP |
| Master/recovery credential state, inference-key records | Shared authority; reusable secrets are not request logs |
| Model alias, capabilities, ratings, desired endpoint settings | Master-authorized shared records, newest wins per edited record |
| Provider credentials, process authority, activity policy, reserves, benchmark policy | Local host only |
| Observed models, loaded instances, effective limits, benchmark results | Host-owned observations; share only what routing/UI need |
| Request ID, attempt ID, deadline, key/node identity, source IP, session hint | Ingress context carried to the serving node |
| Queues, memory reservations, recent route hints | Serving/ingress memory; no disk backlog |
| Logging mode/retention, queue wait setting | Shared policy; application of local resource safety stays local |

Use IDs to correlate work, not to create durable receipt chains. Keep enough recent attempt state to settle a live retry; discard it after completion or bounded expiry. Do not retain request content merely for future features.

## Runtime path

For a known client credential, ingress performs local verification and scope checks, resolves the alias from its cached catalog, and sends the request to one candidate. The serving node checks the authenticated forwarded authority and its local admission state, then queues or starts provider work. Local requests use the same admission function directly. Reuse HTTP connections and stream with backpressure; do not serialize through a central router or buffer an entire response before forwarding.

Attach a cached network-state digest and revision timestamp to existing peer traffic. Compute digests when metadata changes, not by hashing the whole catalog for each request. Matching metadata causes no synchronization call. A mismatch schedules one coalesced targeted refresh. Unknown key lookup, stale configuration, missing model hosts, and real admission failures are the reasons for extra calls; ordinary valid requests do not negotiate the protocol or ask peers for permission.

Check the cutoff window when establishing/reestablishing peer contact, and cache the result until that connection/peer release changes. Keep identity checks conventional and local on established sessions. Password derivation, enrollment, key creation, and recovery are control operations, never token-generation work. Do not add credential freshness leases, heartbeat membership services, signatures per streamed chunk, or quorum checks.

Tailscale is installed/authenticated separately. Existing peers are trusted, single-owner machines. Maintain normal HTTP/input/secret boundaries and the requested bounded unknown-key lookup protection for public access; do not build a hostile-peer consensus system. The GUI/admin surface remains private and public exposure is an explicit local choice.

## Policies all phases preserve

- Configuration converges eventually, newest authorized edit wins; independent key changes merge and a known revocation never resurrects. No majority is needed. Key issue/revoke requires known network size one or one other reachable enrolled node.
- Rejoin synchronizes before serving when a peer is reachable, starting with the oldest known node. Isolated local key acceptance is allowed, including the acknowledged stale-key risk.
- Most unchanged idle periods have zero Mango traffic. Idle inventory fallback is at most hourly; daily reconciliation still runs on disabled/leech nodes. Genuine coordination and triggered synchronization are exempt.
- Throughput comes before priority and FIFO. The explicit scarce-placement exception can admit lower-priority waiting work when flexible higher-priority future work can go elsewhere. It never interrupts the running request.
- The local owner can always reclaim resources. Unknown remote advertisements never override actual local memory admission or manual leech mode.
- Ordinary password recovery preserves issued keys, peers, configuration, benchmarks, and logs. Full reset separately clears keys/private records and requires reenrollment while preserving reusable model metadata.
- No per-key concurrency quota. Queue residence defaults to five minutes and is master-configurable. Capacity and generous finite payload/queue bounds protect the process without inventing a strict distributed quota counter.
- Retry a failed attempt only while the client is connected and before any output reaches it. Do not restart a still-running or ambiguously stopped attempt elsewhere. Client disconnect forwards cancellation and has no resume guarantee.

## Scope and remaining implementation choices

Implement Windows peers and existing Unsloth, LM Studio, and llama.cpp services. Client OS is unrestricted. Cover listing, chat/streaming/client-executed tool calls, embeddings, images, transcription, speech, and the selected structured-decision contract where a backend supplies it. Do not execute tools on provider hosts.

Do not implement macOS/Linux peers, embedded Tailscale, `best` classification, video, full Responses/Messages compatibility, realtime audio, persistent job/file workflows, model installation/download/training, hosted update infrastructure, or a multi-user permission system. Store arbitrary capability ratings now because the catalog/UI need them; do not add a dormant classifier framework. The reserved `best` name can return feature unavailable.

Unspecified timeouts and generous byte/count limits are implementation settings to choose from actual supported workloads, not new product requirements. Keep them in one settings definition, document units, and expose only owner-relevant controls. Do not quietly promote the old eight-per-key limit, 64-item cap, 120-second deadline, or 90-day expiry proposal into approved policy. Key creation presents an explicit expiry or never-expire choice.

## Verification and completion

Each phase names the small checks required for its behavior. Use normal tests for merge rules, scheduler branches, and credential boundaries; use one representative live provider/Windows check where a fake cannot demonstrate behavior. Do not build a simulation platform, certification matrix, load-test service, or formal security program. Reuse the same tests through later phases instead of repeating full qualification after every document or edit.

At release, measure the accepted idle CPU/memory/traffic targets and admission overhead at the stated 1–20-peer/100-deployment envelope. Report the GUI/browser and Tailscale footprint separately as well as total usage. These are engineering targets, not claims of achieved performance. Hardware benchmarks and activity calibration occur only during implementation. A phase is done when its deliverable works, named checks pass, and its limits are documented; deferred features are not hidden completion gates.
