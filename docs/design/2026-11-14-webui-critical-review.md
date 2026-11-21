# Mango WebUI critical design review

This is a historical design review. For current dashboard and serving behavior requirements, see the [November 21 serving boundary](../requirements/09-recorded-decisions.md#serving-boundary--november-21-2026).

Date: November 14, 2026. Scope: the current working checkout, including the in-progress generic-provider changes. This review precedes redesign implementation.

## Verdict

Mango's interface makes the user do the work of an interface designer. It exposes configuration structures, scatters related decisions across a long document, and expects the owner to remember where controls live, which changes need another click, and whether they currently have permission to use them. That is an inadequate daily operating interface for a resource-sensitive network application.

The problem is structural. Changing the colors and rounding more corners would leave the failures intact. Mango needs a real application shell, separate task pages, immediately persisted preferences, a useful overview, and a visible administration state. The existing interface is effectively a backend checklist rendered as forms. It must become a tool for answering ordinary questions: Is my computer contributing? Why is it waiting? Which model is available? What needs my attention? Can I change this setting now?

The most revealing failure is the owner's reported create/join experience: enter the network password, successfully establish membership, then receive “Unlock network administration with the master password” when trying to manage that network. Mango has just received the credential and immediately behaves as though it has not. This is unnecessary friction introduced by the UI's separation of backend operations.

## Evidence and limits

Reviewed all eight current WebUI assets under `src/Mango/wwwroot`, the local settings and provider routes, network authority and catalog handlers, resource observation types, relevant requirements, and existing checks. Built the current app successfully with the pinned .NET SDK: zero warnings and errors. Inspected the running fresh-host UI in Edge using a disposable data directory, including the first screen and Contribution anchor. The owner supplied screenshots of the locked-administration error and the model-serving/provisioning view. The lock error's wording matches `PeerAuthority.RequireAdmin`; the second screenshot demonstrates the passive model blocks described in F20.

The live browser review used an approximately 1030 × 1005 window. The first screen was dominated by the title, host metadata, update preferences, and maintenance actions. Following Contribution scrolled to a full-height form and removed navigation from view. The enrolled/model-rich states below are supported by their source and existing handler tests, not claimed as freshly exercised multi-host browser sessions. No participant study, timing study, screen-reader audit, or production settings mutation was performed. Judgments about daily friction are heuristic analysis, with reproducible evidence cited beside each finding.

Severity: **Critical** means a core workflow is blocked or substantially misrepresented; **High** means recurring use is unnecessarily difficult or unreliable; **Medium** means a concrete clarity, efficiency, or presentation defect. These are usability severities, not claims of exploitable security vulnerabilities.

## What daily use actually requires

| User intention | Current experience | Required outcome |
| --- | --- | --- |
| Open Mango and check that everything is fine | Read setup, version, filesystem path, and update controls before reaching live work or resource observations | Overview shows contribution state and reason, model preference, running/waiting counts, and local resource history immediately |
| Reclaim this computer | Find Contribution in the long document, change role, then find the save button after protection and benchmark controls | A prominent contribution control persists immediately and shows the effective transition, including withdrawal or cooldown |
| Create or join a network and manage it | Supply password for setup; separately find Unlock and supply the same password again | Successful create/join establishes the normal short-lived admin session automatically |
| Make a newly added node ready to serve | Traverse provider registration, passive network model blocks, separate editors, Contribution, and a separate resource-budget selector | One serving workspace exposes provider configuration, editable models, permissions, readiness blockers, and the actions needed to make this node useful |
| Understand whether edits are permitted | Infer state from missing forms, disabled controls, or a server rejection | Persistent labeled Locked/Unlocked status, expiry information, and an obvious unlock/lock action |
| Choose or load a model | Find Providers, pass the registration form, expand a deployment, find its action | Models page presents state, selection, load action, and a precise reason when unavailable |
| Diagnose a waiting request | Search live lists, network observations, resource text, and benchmark settings | Activity identifies the reason and destination, with direct links to the relevant model or protection page |
| Add an inference client | Find keys deep in Private network, locate Unlock elsewhere in that section, then issue and copy a key | Dedicated Keys view with contextual unlock and an obvious one-time copy flow |
| Adjust a preference | Edit a form and remember an additional save step | Valid edits persist automatically with nearby progress and failure feedback |
| Diagnose a host under pressure | Find a prose resource snapshot below Contribution | See truthful CPU/GPU/RAM/VRAM series, current values, freshness, and protection state |
| Inspect a day's usage | Traverse query controls, coverage, aggregate lists, potentially thousands of rows, and retention/deletion forms | Logs viewer with bounded results; logging policy and deletion controls have their own destinations |

## Findings

### F01 Critical — Setup authenticates the user but leaves administration locked

`network.js:153–164` calls `network/create` or `network/join`, reloads settings/network, and stops. Only the separate unlock flow at `network.js:166–177` assigns `networkAdmin`. `PeerRoutes.cs:30–35` exposes these as separate operations; `PeerAuthority.cs:208–258` issues and checks the short-lived admin token. The create/join UI never bridges them.

The result is an absurd onboarding dead end: the person who just created the network cannot immediately configure it without entering the same password again. A successful join has the same unnecessary second step. This is not a reason to remove authorization checks. It is a reason to finish the authenticated workflow.

Reproduced against the disposable host's real HTTP handlers: creation succeeded, the subsequent network view reported `unlocked: false`, and a queue-policy update returned HTTP 403. A separate password unlock changed the view to `unlocked: true` and the same queue-policy update succeeded. The join path was traced in source; this review did not enroll another physical machine.

**Repair contract:** use the just-submitted password to obtain the normal admin session after successful create or join, before presenting the workflow as ready for management. Keep the password transient, clear it after the sequence, and never persist it to browser storage or logs. If enrollment succeeds but unlocking fails, show that partial success honestly and offer a focused unlock retry; do not repeat enrollment. Retain server enforcement, session lifetime, and authority invalidation.

### F02 Critical — Administration state is hidden instead of being part of the application shell

The unlock form and the text “Network administration is unlocked” live inside `#network-enrolled` in `index.html:115–123`. The timer in `network.js` removes the token after expiry and reloads network state. There is no persistent global lock control, visible expiry, or contextual unlock entry in the provider workflow. The original unlock form also remains present when the unlocked text appears. `logs.js` sends users back to Private network in explanatory text.

Mango makes authority a scavenger hunt. A user working on a model should not discover permission state by provoking a rejection. Disappearing controls also conceal what the product can do.

**Repair contract:** an always-visible administration control in the app header, labeled with state and scope. Every protected page provides a direct contextual unlock path that returns to the attempted task. Preserve drafts through expiry; distinguish local-session expiry from network-admin expiry. A manual Lock action must actually end the usable admin session, not merely change a badge. Local-only protection controls must remain usable without network admin authority.

### F03 Critical — The navigation is a table of contents for a giant form

`index.html:24–31` contains six fragment links. They scroll to anchors; they do not select pages. `mango.css:14` centers one narrow document, and the navigation has no persistent positioning. Resource protection and Windows startup do not have their own top-level entries. The browser inspection confirmed that following Contribution leaves the navigation above the viewport.

Every added provider, deployment, peer, or log entry increases the distance between unrelated tasks. Users must retain a spatial map of a document whose length changes during use. This directly violates the requested separate-page/tabbed model.

**Repair contract:** persistent navigation, one active page, clear current location, stable URLs, browser back/forward, and meaningful task groups. Allow sensible scrolling within the active page or a data list; do not hide overflow to manufacture a misleading “no scrolling” demo. Unrelated settings must never be stacked below the active task.

### F04 Critical — The landing screen prioritizes maintenance over operating the product

The opening Host status card contains a static “Local host ready,” role preference, provider count, network summary, release/protocol text, data directory, two update preferences, and update actions (`index.html:32–53`). Its role is informational; it cannot pause contribution or select a model. The useful live resource observation is much later at `index.html:363–399`.

“Local host ready” is technically compatible with “Providers: Not configured” and “Network: Not configured,” but it is a poor product-level answer to “Can I use this?” The first screen visually rewards rare maintenance tasks and buries the frequent ones.

**Repair contract:** Overview must show current effective contribution and its reason, a contribution control, preferred local model, useful request counts, local resource graph, and specific next actions. Version and storage paths belong in System. Surface maintenance only when it requires attention.

### F05 High — Preferences use eleven Save button definitions

Ten static button definitions appear in `index.html`: update preferences (46), provider request (136), model request (152), queue limit (160), provider (244), local ingress (298), local settings (359), local limits (394), retention policy (453), and startup preference (470). `mango.js:382` adds “Save model” for every deployment editor. The adjacent alias-rating form also requires “Apply rating to alias.”

A checkbox looks changed before its effect exists. That forces the user to remember a separate commit step, navigate to it, and distinguish visual state from persisted state. “Start Mango when I sign in” is an especially clear example of a preference that should not require a second button.

**Repair contract:** no Save buttons anywhere, and no relabeling them Apply or Done while retaining the same commit barrier. Existing preferences persist on valid change. Immediate controls and text/numeric controls need deliberate input timing, nearby progress, success, and error states. Creation/destruction/execution actions remain explicit verbs: Create network, Join network, Add provider, Issue key, Load model, Retry benchmark, Download, Revoke, Reset. Those actions must perform real operations, not act as disguised preference saves.

### F06 High — Blindly attaching autosave to current forms would introduce data-loss bugs

`mango.js:105–119` submits a full settings snapshot and calls `loadSettings`, which repopulates every field. `loadProviders` replaces the entire deployment list; it preserves expanded IDs, not unfinished field values (`mango.js:407–424`). The local model path uses revisions, and shared intents use revisions in `network-catalog.js:103–135`. The queue field is refreshed whenever it is not the active element (`network.js:109–110`). Resource polling protects one dirty form, but switching target clears that guard (`resources.js:145–148`).

These are incompatible with a naive “POST on every keystroke” conversion. A slower response can overwrite a newer edit; an invalid field can prevent an unrelated setting from persisting; a refreshed catalog can erase another model's draft; stale revisions can reject the next autosave. The current local settings endpoint also replaces the settings record, apart from explicitly retained maintenance fields (`LocalWeb.cs:92–120`).

**Repair contract:** serialize writes per persisted record, coalesce pending edits, retain drafts while requests run, never render stale responses over newer input, and use the latest accepted revision. Handle multi-tab/tray concurrent changes without silently overwriting unrelated preferences. Keep validation at the server. Show unsent/error state until the change is acknowledged; do not announce success just because a request was started. Navigation must neither discard pending work nor trap the user in an editor.

### F07 High — Core model actions are concealed inside collapsed deployment details

`mango.js:331–352` creates a `details` element containing model information, local selection, and load/unload actions. First-render local models are collapsed because expanded IDs begin empty. Network nodes also use details; remote nodes begin collapsed (`network-catalog.js:203–243`).

Selecting or loading a model is everyday work, not optional diagnostic detail. Hiding those controls makes an apparently passive list conceal the application's useful functions. Summaries also compress status into a long text string instead of a consistent row structure.

**Repair contract:** visible model rows/cards with name, host, task support, loaded/available state, selection, and primary action. Advanced metadata may use a clearly labeled secondary detail view. Important settings and actions must not require guessing which disclosure to expand.

### F08 High — Related settings are duplicated without a clear source of truth

Local models have alias, serving fields, and rating editors in `mango.js`; shared models have another editor in `network-catalog.js`. A third alias-wide rating form appears below the local list. Provider registration also has local and shared-request editors. Role and benchmark choices repeat in join, create, and Contribution forms. Resource limits use a separate deployment selector far from the model's other configuration.

Some underlying distinctions are legitimate: local process permission is not a shared provider request, and onboarding must collect initial choices. The UI fails to communicate those distinctions as one coherent workflow. The same object appears to have several unrelated editing surfaces.

**Repair contract:** one canonical destination per editable property, with explicit ownership. Setup uses initial choices from the same state model. Overview shortcuts bind to the same persisted state. Model detail links to its resource qualification without making users identify the deployment twice. If bulk alias editing remains, label its scope and make it an explicit bulk operation instead of a competing source of truth.

### F09 High — Local authority and shared intent look too similar

Provider origin, local token, executable permission, desired remote configuration, and observed host state all appear as ordinary form content. The backend carefully distinguishes these; the presentation demands that users infer the boundary from scattered explanatory sentences. `providerObservation` and `intentLabel` already expose requested/applied/pending states, but mostly as prose.

**Repair contract:** consistent “This computer” and “Network” scope labels, owning-host identification, and separate requested/applied state. Remote local-only controls must explain where they can be changed. Autosave must say “Saved · waiting for host” when that is the truth; persistence is not the same as application.

### F10 High — Resource monitoring is a text snapshot, not a monitoring interface

`resources.js:98–108` assembles free RAM, external CPU/GPU, device memory, and limitations into one paragraph. There is no history, graph, freshness treatment, or visual relationship between pressure and contribution changes. A Refresh button sits beside a five-second polling loop. Errors from initial and periodic resource loads are discarded at the end of the file.

This hides both trends and collection failure. A stale old paragraph can look current. The owner cannot tell whether a spike was transient, whether cooldown is working, or whether “no data” means zero use.

**Repair contract:** a bounded local history graph on Overview, current values, timestamps, honest unavailable/gap states, per-device handling, and textual equivalents. `ResourceObservation` already contains timestamps, total/free RAM, external CPU/GPU, and device memory. External utilization is not total host utilization and must not be relabeled as such. Integrated graphics use shared memory; do not invent dedicated VRAM. New history must not enable persistent request logging or network polling.

### F11 High — First-run setup offers forms instead of a guided decision

Discovery, an empty Join form, and collapsed Create live inside the same long page (`index.html:62–110`). Join requires a peer address, network ID, node name, master password, role, and benchmark policy. Below this, unrelated provider, public ingress, protection, and logging forms are already present.

Avoiding accidental duplicate network creation is correct. Hiding the create path inside a disclosure does not make onboarding understandable. The user still needs a clear choice and an explanation when discovery fails.

**Repair contract:** a focused setup surface with explicit Create/Join choices, Tailscale status, discovery results, clear retry/manual join, and useful next steps. Successful setup ends with an authenticated management state, recovery information where applicable, and a direct path to adding a provider. Never equate failure to discover a peer with permission to silently create a new network.

### F12 High — Errors and completion feedback are often detached from their controls

Most provider/model actions report to one `#provider-status`; network operations share `#network-status`. A model far down the list can fail while its feedback appears above the list. `submit` assumes the first button is the commit control and reports generic “Saving…”/“Saved.” even when used for log deletion. This helper will also break if save buttons are simply removed.

**Repair contract:** field or record-level feedback, specific action results, and a global attention summary only when useful. Failed changes retain entered values. Validation explains the field and correction. A summary never substitutes for feedback beside the failing operation. Existing live regions are a useful foundation, but autosave should not seize focus or announce every telemetry sample. [W3C status-message guidance](https://www.w3.org/WAI/WCAG22/Understanding/status-messages.html)

### F13 High — Permission transitions do not refresh all affected workflows

Unlocking calls `loadNetwork`, while logging policy's disabled state comes from `loadLogs`. `logs.js` initially loads independently and refreshes on query/collection actions. Therefore an unlock does not itself guarantee that the logging editor becomes usable. Existing local provider/model editors are still shown and can rely on a later server rejection to reveal the missing authority.

**Repair contract:** one shared administration-state transition updates every dependent surface. Expiry, manual lock, authority replacement, and failed authorization use the same mechanism. Avoid separate booleans drifting between pages. Keep protected affordances discoverable with an explanation and an unlock action.

### F14 High — Logs combine investigation, policy editing, and deletion in one accumulating page

`index.html:401–464` places queries, collection coverage, usage lists, records, retention, and clearing sequentially. `logs.js` can render a view capped at 10,000 records using `replaceChildren`, with no UI pagination. The horizontal table wrapper does not bound the page's vertical growth.

A growing log report pushes settings and deletion controls farther away. Conversely, a user inspecting one request is surrounded by policy and destructive controls that are irrelevant to that task.

**Repair contract:** bounded/paged results, filters adjacent to results, useful empty states, and separate logging-policy and clear-data destinations. Preserve partial coverage, dropped/truncated data, payload sensitivity, export semantics, and pending clear acknowledgements.

### F15 Medium — The visual system has a palette, but almost no operational hierarchy

`mango.css` supplies neutral backgrounds, green buttons, rounded cards, and a large headline. Nearly every region uses the same card treatment. Status is mostly prose, long identifiers compete with names, and there is no deliberate glyph system, brand mark, metric treatment, or compact toolbar. The declared Inter font is not bundled or fetched, so it is a preference rather than an assured brand asset.

The screenshot reads as a styled administrative form. The oversized “Network and local settings” heading does little work; update controls command more visual weight than the state of the inference network.

**Repair contract:** a cohesive Mango identity with a small original mark, a restrained warm accent, neutral surfaces, consistent type/spacing, purposeful status treatments, and one line-icon family with text labels. Graphics should clarify resources, state, and empty workflows. No decorative hero section, gratuitous gradients, stock illustration parade, or icon-only mystery controls.

### F16 Medium — Product language frequently reflects implementation rather than intent

“Leech,” “requested deployment settings,” “configured context,” “fallback served context,” “local public ingress,” and raw IDs require translation. Contribution appears as Leech, Manual leech, role preference, and effective contribution in different places. “Check GitHub now” names an implementation source when the user wants to check for updates.

**Repair contract:** establish a short vocabulary. Explain contribution as whether this computer serves network requests; preserve the underlying role values. Clearly separate configured context from observed context. Show friendly names first and IDs only where copying or diagnosis requires them. State disabled-action reasons directly.

### F17 Medium — Key and recovery workflows have weak containment

Inference keys share the huge Private network section with nodes, model configuration, queue policy, recovery, and reset. One-time secrets appear in a shared warning panel at the section's end. Selection of the secret text is implemented, but there is no dedicated Copy key button. Recovery and full reset are adjacent disclosures.

**Repair contract:** dedicated Keys and Recovery destinations, a focused one-time secret view, a clear Copy action, and separate danger treatment for reset. Preserve immutable key fields, explicit expiry/never-expire, propagation state, recovery retention guarantees, and reset confirmation. Hiding destructive operations in details is not a substitute for explaining their consequences.

### F18 Medium — Accessibility foundations exist, but the redesign needs real interaction semantics

Native labels, fieldsets, buttons, focus outlines, live regions, and a narrow-width grid are present. Those are valuable. They do not solve the long tab sequence, disappearing navigation, refresh-induced focus loss, hidden primary actions, or dense information presentation. The current stylesheet only adjusts a few layouts at 36rem; no claim of full accessibility compliance is justified by that breakpoint.

**Repair contract:** semantic navigation; correctly implemented tabs where a tab widget is used; predictable focus and keyboard operation; accessible charts with text summaries; status distinguished by text/glyph as well as color; zoom/reflow without lost controls. Follow the [W3C tabs interaction pattern](https://www.w3.org/WAI/ARIA/apg/patterns/tabs/) and [reflow guidance](https://www.w3.org/WAI/WCAG22/Understanding/reflow.html). A fixed-height shell must still accommodate small screens and enlarged text.

### F19 Medium — Existing qualification does not protect the requested user experience

`tests/Mango.Tests/Program.cs:61` checks that the browser page includes “Local settings.” The project has substantive server tests for authority, providers, resources, logging, and public boundaries, and historical manual UI qualification in phase six. It does not currently enforce separate pages, absence of Save controls, autosave races, post-setup unlocking, persistent lock state, or useful overview telemetry.

**Repair contract:** small deterministic behavioral tests for autosave and authority transitions, plus rendered review of the actual app in representative states. A passing build and matching title string cannot certify a usable product. Test failures during rapid edits, expired admin sessions, offline hosts, empty catalogs, missing telemetry, and narrow windows—not only a happy-path screenshot.

### F20 Critical — Provisioning a node is fragmented into passive reports and hidden editors

The owner's model screenshot shows the failure directly: each model consumes a tall block of prose repeating requested/applied state, alias, unloaded/unavailable state, modalities, unknown context, absent benchmarks, and absent ratings. Its one visible editing affordance is “Edit requested model.” There is no working configuration surface in the row, no single clear readiness explanation, and no obvious sequence from discovered model to usable serving node.

`network-catalog.js` builds these observations in `modelObservation` and sends edits to the separate `modelIntentForm` through `modelRequest`. Local provider registration and model lifecycle live in `mango.js`; local protection and deployment budget qualification live elsewhere in `resources.js`. The user must mentally join these subsystems while repeatedly locating the same model. A network inventory dump is not a provisioning workflow.

“Unavailable for routing when observed” is an honest cached fact, but it is not an adequate explanation of how to fix the situation. “Context unknown” and “0 complete” leave the owner to infer whether to load the model, enter a serving limit, configure provider authority, qualify memory, permit an attached provider, or wait for another host. Each model also repeats “No capability ratings” rather than presenting an immediately editable rating control with a clear scope. The screen spends space repeating facts while hiding the work.

**Repair contract:** provide one canonical serving/provisioning screen scoped to a selected node/provider. Provider configuration, routine model fields, relevant serving settings, primary lifecycle/selection actions, and actionable readiness blockers are visible and usable immediately. No Edit button, accordion, or detail-expansion prerequisite to expose routine settings. An authorized user can correct an alias/context/capability/rating directly. Creation/connection and model load remain explicit operations; changes to existing settings autosave. A newly created or joined node arrives here already unlocked, with provider discovery/configuration and the remaining local prerequisites in context.

The single screen must also expose the applicable local permission and resource-qualification work needed to finish setup, without sending the user through unrelated pages to reselect the node and model. These controls must use the canonical state and write handlers, not create a second conflicting editor. Context-sensitive sections are appropriate for inapplicable fields; hiding required fields behind “advanced” is not. Selection of a host/provider and paging/filtering model rows can bound the screen at scale. This must not turn into another page containing every setting for every host.

Readiness is an evidence-based outcome: configured provider, discovered model, usable serving metadata, lifecycle/attached authority where needed, resource qualification required by the selected policy, and current contribution state. Surface the precise blockers and direct corrective controls. Do not invent safe memory numbers, bypass benchmarks, claim a cached remote model is ready now, or equate unloaded with always unusable when just-in-time loading is supported. Preserve local-only authority; remote provisioning shows the exact work that must be completed on its owner.

**Acceptance:** starting with a fresh node, create or join, configure its provider, edit its model settings, complete applicable permission/resource prerequisites, and reach a truthful serving-ready state through the same provisioning workspace. Repeat for an existing node editing several models directly. Verify no routine edit requires exposing an editor first and no settings require Save/Apply. A blocked or remote case must state what remains and where it can actually be resolved.

## Preserve the parts that are correct

The redesign must retain authenticated private administration, local-only protection and credentials, explicit process authority, resource validation and reduced-reserve consent, revision conflicts, requested-versus-applied reporting, honest cached reachability, streamed activity, bounded backend state, key revocation rules, recovery/reset distinctions, and public inference isolation. Native controls and the existing plain HTML/CSS/JavaScript architecture are adequate. None of the failures above requires a new frontend framework or an unrelated service.

The general review lens is whether the product communicates state, supports recognizable actions, prevents avoidable errors, and removes irrelevant competition for attention. Those are established usability concerns, not a preference for fashionable decoration. [Nielsen Norman Group's usability heuristics](https://www.nngroup.com/articles/ten-usability-heuristics/)

## Required handoff to the repair plan

The repair plan must map every finding to an implementation decision and a check. It must specify page ownership for every current setting/action, an actual visual system and glyph strategy, overview composition, truthful telemetry semantics, autosave mechanics and concurrency, setup-to-unlock behavior, global authority state, and scoped verification.

Hard acceptance gates:

1. Meaningful groups have their own pages in a convenient persistent tabbed/navigation shell. The application is no longer one giant page.
2. Overview contains the important operating controls and a real local resource-usage graph.
3. There are no Save buttons or renamed equivalents for preferences; valid changes persist automatically.
4. Create and Join automatically establish the normal admin session using the credential already supplied. No second password entry is needed on the successful path.
5. Locked/unlocked state and its control remain plainly visible on every page. Authority changes update all protected workflows.
6. Primary model, contribution, provider, and key workflows are discoverable without opening arbitrary collapsing sections.
7. The visual redesign is cohesive and complete, with intentional brand, graphics, glyphs, hierarchy, responsive behavior, and accessibility.
8. Existing uncommitted provider work and backend safety boundaries remain intact. No redesign work may silently delete capabilities to make the screen look simpler.
9. Node/provider provisioning is a coherent single-screen workflow with directly visible model editing and readiness controls. Creating or joining a network leads into that workspace already authenticated; configuring a usable node must not require traversing scattered hidden editors.

The next artifact is `docs/plan/09-webui-redesign.md`, to be designed and written by a subagent using this report. Implementation begins only after that plan exists and the requested Codex goal references it.
