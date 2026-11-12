# Phase 6 Network GUI and optional public access

Deliver the complete administration experience and optional internet inference, using the working host, adapters, peer state, resource gate, and scheduler. Depends on phases 3–5. Extend the existing local UI rather than introducing a second application.

Read the shared foundation, UI-01–08, the settings-ownership table, PUBLIC-01–04, and KEY-01–11. Logging views are completed in phase 7; update availability is completed in phase 8.

## GUI flows

1. Finish first-run create/join setup, Tailscale diagnostics, network identity display, recovery-record export, contributor/leech choice, provider management permissions, and the persistent Never/Now/When convenient benchmark choice. Keep failure to find an existing network distinct from permission to create a new one.
2. Build a network/model view from cached state: node identity and join order, last-known reachability and observation time, contributor state/reason, provider versions, aliases, modalities, actual served context, loaded/busy status, benchmark completeness, and ratings. Do not label a cached node online with certainty or probe every peer just because the page is open.
3. Permit master-unlocked changes to shared aliases, capability labels and ratings, endpoint registration, and supported serving configuration from any node. Show saved shared intent separately from applied host state. An offline host can apply it on reconciliation. A remote edit requiring new local secrets or process rights remains pending local action; it cannot grant itself those rights.
4. Keep sampling, thresholds, cooldown, reserves, benchmark policy, manual leech, provider credentials and lifecycle authority local. Show remote values read-only where useful. Enforce this in server handlers, not only disabled UI controls.
5. Add key creation/revocation with immutable name, scope, priority, and explicit expiry/never-expire selection. Show the new secret once. Surface the singleton/one-other-peer issuance rule and propagation state. Scope changes require replacing a key, not editing it. Do not add per-key concurrency sliders.
6. Add ordinary password recovery and explicit full reset as visibly different operations. Recovery keeps keys and data. Full reset states that it clears private records and authority while preserving model settings/benchmarks. Reuse phase 3 operations; no duplicate UI-specific recovery logic.
7. Show request placement, load delay, queue reason, priority, reroute, and rejection using the live in-memory request state. The UI subscribes to local changes while open, with no network-wide polling loop or persistent logs forced on. Bound the live list and stop subscriptions when the page closes.

Use normal accessible HTML, responsive layouts, clear labels, and useful progress. Keep provider plumbing out of everyday flows except when the owner must choose managed/attached mode or fix a concrete limitation. No custom accessibility framework or separate mobile app.

## Public inference boundary

Public serving defaults off. The local owner enables a dedicated inference-only binding behind an existing HTTPS reverse proxy or a guided Tailscale Funnel configuration. Document the exact public route allowlist from phase 2. Enrollment, configuration, log retrieval, reset, provider management, and the local GUI never share that public route surface.

Treat TLS/certificate/proxy configuration as explicit local setup. Reuse the existing proxy or Tailscale facilities rather than implementing ACME, DNS automation, or NAT traversal. A created key does not open a listener or modify network policy. The UI reports configured versus actually tested public reachability honestly.

Authenticate before expensive body handling. Known valid keys use local lookup and the existing scheduler. Preserve scope and priority when forwarding; do not send Mango credentials to providers. Unknown IDs use the bounded sequential lookup already implemented. Retain the original request's observed source IP and trust forwarded source headers only from the configured proxy. Show unknown/proxy-observed attribution when the real client address is unavailable.

Set generous finite request-body and connection-buffer bounds suitable for supported context/audio/image requests. Avoid a second strict quota system: normal inference is governed by resource capacity, queue policy and priority. Use the dedicated public/unknown-client boundary to prevent unlimited unauthenticated lookup work. Internal trusted-peer requests do not repeatedly prove the same network authority.

## Minimum checks

- From another node, edit an alias and a capability, including while its owner is offline. Verify reconciliation and effective-state display, and that remote local-protection edits fail in the handler.
- Use keyboard navigation and a narrow window for the real setup/key/model flows. Verify updates remain responsive during a benchmark or queued inference.
- From outside the tailnet, use a valid key through one supported HTTPS path for streaming inference. Check that no admin route is exposed, a wrong/unknown/revoked key follows its intended path, and spoofed source headers are not trusted.
- Confirm creating a key leaves public exposure off and that password recovery preserves that key while full reset removes it.

## Done

A user can configure the network from any enrolled GUI while each host retains its own resource authority. Public inference works only through an explicitly enabled HTTPS ingress and reaches the same scheduler. No public admin console, proxy-hosting service, new provider tooling, or background peer scan is introduced.
