# Mango WebUI redesign implementation plan

This phase records earlier design and qualification. The [November 21 serving boundary](../requirements/09-recorded-decisions.md#serving-boundary--november-21-2026) supersedes any conflicting guidance on execution admission, benchmarks, activity withdrawal, model eviction, or co-residency. See the [implementation and rollout plan](10-serving-boundary.md) for the current work.

Date: November 14, 2026. Status: implemented; see [verification and limitations](../design/2026-11-14-webui-verification.md). Governing critique: [Mango WebUI critical design review](../design/2026-11-14-webui-critical-review.md). Visual direction: [Mango design board](../design/mango-redesign-direction.svg).

Owner's subsequent correction supersedes the top-header layout below: the sidebar alone contains branding, navigation, host identity, and administration controls beside contribution status at its bottom. Remove the reserved top-header row. Narrow screens use bottom navigation and status controls. The original design board and initial screenshots predate this correction.

## Outcome and nonnegotiable behavior

Turn Mango into a calm, useful control surface for daily inference work. The first screen answers whether this computer is contributing, which model is preferred, what is running or waiting, and how local resources are changing. Every meaningful setting group has a dedicated page within persistent navigation. Preferences persist when changed. No Save button, renamed Apply button, footer commit bar, or modal Done button may act as a preference commit step.

Creating or joining a network immediately obtains the normal short-lived administration session using the password just entered and lands in the new node's Serving workspace. Network administration state remains visible on every page, with a direct unlock or lock control. The UI must never make a successful new member re-enter that same password just to begin management.

Serving is one canonical working screen for getting a node useful: provider configuration, local authority, directly editable model settings, lifecycle/selection actions, readiness reasons, and applicable resource qualification are present together. Routine fields are ready to edit on entry. No Edit button, disclosure, or switch into editing mode stands between the user and alias/context/capability/rating changes. Selecting a host/provider/model scopes this working screen; it does not unlock a hidden editor. This requirement incorporates the owner's second screenshot and critique F20.

Preserve all current capabilities and ownership rules. Keep .NET, static HTML/CSS, and plain JavaScript. Do not add a frontend framework, chart dependency, icon package, remote font/CDN request, new service, telemetry database, or generic configuration framework. Existing dirty generic-provider changes are part of the working baseline, not changes to discard.

## Design direction

Mango should feel like a precise desktop utility with a warm identity. Use a compact original mango silhouette and a small leaf, restrained amber emphasis, deep green text accents, bright neutral working surfaces, and consistent line glyphs. Reserve visual weight for current state and decisions. The interface should not resemble a landing page, a stack of setup cards, or a developer console.

### Tokens and typography

| Token | Value | Use |
| --- | --- | --- |
| Canvas | `#F5F6F3` | Application background |
| Surface | `#FFFFFF` | Working panels, fields, dialogs |
| Ink | `#192522` | Primary text; 15.80:1 on white |
| Muted ink | `#5A6862` | Secondary text; 5.85:1 on white, 5.39:1 on canvas |
| Border | `#CBD4CE` | Nonessential separators; do not rely on it alone to identify a control |
| Mango amber | `#EAA43A` | Selected accent and primary action background; ink text gives 7.43:1 |
| Amber ink | `#9A5600` | Warning text and CPU line; 5.67:1 on white |
| Green | `#23624C` | Healthy state and RAM line; 7.18:1 on white |
| Blue | `#2563B3` | GPU line; 5.98:1 on white |
| Plum | `#7350A4` | Dedicated VRAM line; 6.15:1 on white |
| Danger | `#B23832` | Failures and destructive actions; 5.99:1 on white |
| Focus | `#1D6B8B` | Three-pixel focus outline with two-pixel offset; 5.95:1 on white |

Ratios above were calculated from sRGB relative luminance for the named pairs. Recheck actual composite states, selected backgrounds, disabled controls, and chart treatments during implementation. Amber is not body text on white. Use ink on amber buttons, white on green buttons only where needed, and explicit text/glyphs for all status colors. Form boundaries use a stronger border than decorative separators.

Use `"Segoe UI Variable", "Segoe UI", system-ui, sans-serif`. No font download. Body and form labels are 14–15px with 1.45 line height; secondary text is at least 13px. Page title is 24px/600; subsection title 16px/600; key metric 28px with tabular numerals. Limit uppercase to short 11–12px metric labels. Use 4/8/12/16/24/32px spacing, 8px field radii, 12px panel radii, and a subtle border rather than shadows on every container. Maximum working width is 1280px; settings forms normally stop at 760px. Label/value/action rows are more compact than one card per input.

### Mark, glyphs, and graphics

Create `wwwroot/mango.svg`: one asymmetric amber fruit shape and one green leaf, recognizable at 24px. Pair it with the word Mango at 20px/650. Use the same mark as favicon; do not introduce a new tray-icon implementation in this UI change.

Create a local SVG symbol sheet `wwwroot/glyphs.svg` with a 24 × 24 viewBox, 1.75px strokes, rounded ends, and consistent optical weight. Needed glyphs: overview/grid, model/layers, provider/server, activity/pulse, network/nodes, protection/shield, system/sliders, lock/unlock, check, warning, error, add, refresh, copy, download, external link, and chevrons. Draw the small set directly; no unrelated icon catalog. Visible navigation and action labels accompany glyphs. Decorative SVG is `aria-hidden`; an icon-only Copy control, if space forces one, needs an accessible name and tooltip.

The main graphic is a real resource chart. Secondary graphics are compact status markers and a restrained empty-state icon. No decorative illustrations, photographic hero images, gradients, oversized slogans, animation loops, or emoji icons. The design board uses explicitly illustrative values; application charts must use observed data only.

## Shell, routing, and scrolling

Use a 60px persistent header with Mango, current node/network name, connection state, and the administration control. At desktop widths of 960px and above, use a 184px left rail below the header for six labeled destinations: Overview, Serving, Activity, Network, Protection, System. At narrower widths, turn that rail into a 44px horizontal tab strip below the header; retain labels, active indication, keyboard access, and visible overflow affordances. Keep the active tab in view without moving focus. No hamburger gate for core pages.

Use native navigation links styled as tabs for full page routes, with `aria-current="page"`. Use real `tablist`/`tab`/`tabpanel` semantics only for the subordinate tab views, including roving focus, arrow keys, Home/End, and Enter/Space. Only the active page and active subordinate panel participate in display and focus. Do not leave every page stacked in the document. Main navigation routes are links, not controls with incomplete ARIA tab behavior.

Store the route in hashes such as `#/serving/{node}/{provider}?model={deployment}&operation=chat`. Consume and remove the existing `#session=…` bootstrap fragment before route initialization; never put a token in a route. Support reload, Back, Forward, unknown-route fallback to Overview, direct links to a model/provider/node, and old anchors redirected to their new destinations. The route must not depend on successfully loading another page. Page changes move focus to the page title when initiated through navigation; refreshes never move it.

The header and navigation remain available while one active working region scrolls. Use `min-height: 0`, not clipped content. Long data sets are paged or placed in bounded result regions. At narrow widths or high zoom, allow the active page to grow and scroll naturally below the sticky shell; do not create nested scroll traps or require two-dimensional scrolling for forms. A real data table may have an explicitly labeled horizontal overflow region.

### Page and ownership map

| Destination and route | Dedicated pages or subviews | Owns these settings and actions |
| --- | --- | --- |
| Overview `#/overview` | One operating dashboard | Contribution switch bound to the same local role record; preferred local deployment selector bound to selection; effective state/reason; request counts; resource chart; attention links. No full configuration forms |
| Serving `#/serving/{node}/{provider}` | Serving workspace; Aliases; Measurements for the currently selected model | One visible provider configuration/local-access strip, editable model rows, load/unload/preference actions, contextual readiness and selected-model workload/memory qualification. Owns host/profile/origin/enablement/token/mode/process paths; alias/context/embedding/media/capability/rating fields; attached/overlap/slot acknowledgements; refresh/stop/remove/add provider; benchmark retry and measurements. Generic remains supported. Aliases is a clearly scoped bulk-rating view. Measurements is secondary evidence, never a gate for ordinary editing |
| Activity `#/activity/live` | Live; History `#/activity/history` | Live ingress/serving work and reasons; history day/key/source filters; summary and paged rows; collection and coverage; Download day. No retention or clearing forms under results |
| Network `#/network/nodes` | Nodes; Queue; Keys; Public access; Recovery; Setup | Membership, identity, Tailscale diagnostics, targeted refresh/discovery, remove peer; queue wait; issue/revoke keys; local public upstream configuration and HTTPS check; recovery/reset; create/join. Node rows open that node's Serving workspace instead of duplicating editors |
| Protection `#/protection/activity` | Activity; Memory; Benchmarks | Local activity monitoring, sample interval, quiet cooldown, CPU/GPU/RAM/VRAM cutoffs; host RAM and per-discrete-GPU reserve defaults/overrides and consent; local benchmark policy, progress, and links to model measurements; release verification. Role is controlled on Overview; show its effective state here with a link |
| System `#/system/startup` | Startup; Updates; Logging policy; Data | Login startup; daily update checks and maintenance deferral; check for updates, peer proposal, partial-peer override and outcomes; log mode, operational logging, IANA timezone, retention/cap; data location/version/protocol/recovery diagnostics; explicit retained-log clearing with acknowledgements |

Each listed subview is an exclusive page/panel, not a heading in an endless page. Serving preserves node/provider/model selection and filters; Nodes has a visible route back from Serving. Existing `#/models`, `#/providers`, and old matching anchors route into Serving rather than creating additional editors. An unenrolled host can still use local providers and protection; Network Setup is a focused page, not a forced modal covering usable local functionality. Overview shows an actionable setup state until enrollment exists.

### Complete relocation of current UI groups

| Current controls | New location and rule |
| --- | --- |
| `host-status`, `role-status`, provider count, network summary | Overview status strip; truthful readiness derived from actual state |
| `release-status`, data directory, recovery warnings | System Updates/Data; actionable warnings additionally linked from Overview |
| `maintenance`, update check/share/partial | System Updates; two preferences autosave independently; proposal remains an explicit operation |
| Network discovery/join/create forms and repeated role/benchmark selectors | Network Setup choice and steps; initial role/policy collected once, then the canonical settings state is refreshed |
| `network-unlock`, unlocked text | One shell control and one reusable native dialog; remove the old inline form |
| `network-models`, requested model/provider forms | Node summary links plus canonical Serving workspace; preserve desired/observed state without repeated prose blocks |
| `network-queue` | Network Queue, one automatic numeric preference |
| Key policy/list/issue/scopes/expiry | Network Keys with visible list and Issue key operation |
| Recovery/reset/one-time secret/download | Network Recovery and focused secret dialog; Create/Issue results can open that dialog directly |
| Provider registration/edit/list/refresh/token/process fields | Serving provider strip and visible local-authority fields; Add creates a registration without changing screens |
| Local selection/deployment list/model editor | Serving directly editable model rows; Overview selection is a synchronized shortcut, not separate storage |
| Alias-wide rating form | Serving Aliases, explicit scope showing affected deployment count and mixed values; valid rating edits auto-update that alias's members |
| Received/serving request disclosures | Activity Live visible rows; Overview shows only counts and a short recent summary |
| Public form/check | Network Public access, marked This computer |
| Contribution role | Overview contribution switch and Serving's contextual local-readiness control bind the same record; raw `leech`/`contributor` values stay unchanged |
| Activity/reserve/benchmark fields | The three Protection subpages, one group per page |
| Resource state/observations/release | Overview state/chart, Protection status, contextual release action |
| Resource deployment selector/limits/GPU peaks/passes/retry | Serving's always-present selected-model qualification pane; measurement evidence is secondary; no need to navigate elsewhere and reselect the model |
| Log query/results/export/collection | Activity History, bounded result pages and explicit coverage |
| Log mode/retention/operational events/timezone | System Logging policy; warning stays beside payload modes |
| Clear logs | System Data, separate danger section with confirmation and pending-peer state |
| Windows startup | System Startup; visible unavailable explanation for custom data directories instead of unexplained disappearance |

## Serving workspace and node provisioning

This is the main corrective response to F20, not merely a new list skin. On entry, show a node selector, provider selector, Add provider, and a short readiness summary. Default to this computer and the first configured provider; after setup default to the new node and its first unfinished prerequisite. With no provider, show the Add provider fields directly in the normal provider area. Adding a provider keeps the user in the same workspace and loads its model inventory.

The provider strip contains profile, origin, enabled state, refresh, and local management/credential controls immediately. For local single-model llama providers, required executable/GGUF fields appear when their mode/profile makes them applicable. Existing bearer tokens are represented by “Token stored”; the blank replacement field is labeled “New token · leave blank to keep.” Shared requested origin/profile and observed host values use a compact requested-versus-applied annotation only when they differ, not repeated paragraphs for every model. A remote provider shows local-only credentials/process controls as a clear owning-computer boundary rather than a simulated editable form.

Below this strip, a model worklist occupies the flexible column; an always-visible 320px “Qualification for [model]” pane occupies the right. The first model is selected automatically. Selection changes the qualification context, but every model row already contains its editable alias, applicable context/embedding/media fields, capability/rating inputs, lifecycle/preference actions, and an authoritative readiness reason. There is no Edit requested model button and no accordion. Keyboard focus goes directly into normal inputs. Unknown observed context is displayed beside the editable fallback/configured value; zero ratings or no benchmarks do not become fabricated errors.

Use five model rows per page, a search field, operation filter, and stable node/provider selectors to bound growth. Each row can use two compact lines rather than forcing every field into a tiny column. Relevant modality fields remain visible; omit only genuinely inapplicable fields, not required fields called advanced. At narrow widths, the selected-model qualification region follows the selected row in the same page, with a visible heading and no disclosure. Preserve other row drafts when selecting or paging. Never paginate an in-flight focused row out from under the user.

The qualification pane has the selected operation, required workload/sequence/media fields, load/execution RAM and per-discrete-GPU peaks, attached-provider acknowledgement, overlap qualification/slots, and benchmark policy/progress/retry when relevant. These are the canonical handlers and state, not copied forms. The small local contribution and benchmark-policy controls shown here bind to the same record as Overview/Protection so a new node can be finished without a tour of settings pages. Detailed calibration measurements remain a secondary view; required configuration is never hidden there.

Show a short ordered list of actual blockers and the control that resolves each: provider unavailable → editable origin/token and Refresh; host-local authority needed → local permission section; required served limit unknown → that visible model field; missing usable resource budget → visible qualification inputs; explicit benchmark policy waiting → progress/retry; node not contributing → local contribution control; memory pressure/cooldown/busy work → effective state and wait explanation. On a remote node, label local-only fixes “Complete on [node]” and show last observation, instead of pretending this GUI can grant its permissions.

Readiness must come from the same authoritative facts as provider placement/resource admission. Reuse `ResourceProtection` availability and reason logic and `ProviderCatalog.Placement` capability facts, or expose a small structured diagnostic result from those existing components. Do not call reservation or load operations merely to render readiness, and do not replicate safety/scheduler decisions in JavaScript. Distinguish configured, discoverable, loadable on demand, presently loaded, currently busy/pressured, and available at last observation. Unloaded can be eligible for supported managed just-in-time loading; absence of ratings is not a routing blocker; benchmarks are required only when actual policy/capability requires them. A readiness check is an observation, not a guarantee that a future request will be admitted.

The end-to-end workflow is Create/Join → automatic admin unlock → Serving for this node → Add/connect provider → inspect automatically discovered editable models → fill only actual missing limits/permissions → automatically persisted configuration → observed ready/loadable or a precise remaining reason. The user never opens an Edit mode, locates a separate model settings form, or reselects the same model on another page to finish basic provisioning.

## Overview composition

At 1280 × 800, all primary controls and the resource plot must appear before scrolling. Use 24px page padding, a compact title/status line, and a two-column grid: flexible resource/activity column and a 288px control column. At less than 1100px, use a 260px control column. Below 960px, stack controls before the chart. Avoid a giant greeting/title.

The top strip gives This computer, effective contribution state with a reason, and running/waiting counts. The control column contains “Contribute this computer” with an on/off switch and explicit pending/withdrawal/cooldown feedback; preferred local deployment selector and availability; “Activity protection on/off” as a read-only summary linked to Protection; and at most three concrete attention items. Changing the role does not claim that active work has stopped until the resource state confirms it.

The main panel contains “Local resources,” current readings, the last ten minutes of observed history, a freshness label, and a short metric explanation. Below it show at most five recent work rows with “View activity.” A normal idle state is calm, not a warning. Missing setup offers Create network/Join network and Add provider links. A fault links directly to the failing provider, model limit, network, or protection page.

Do not repeat the full settings form here. Preferred deployment and contribution are deliberate quick controls bound to the same data and write queues used in Serving; their mirrors are synchronized shortcuts, never independent stored values.

## Administration and setup

### One administration state

Maintain one in-memory object containing status, token, expiry, and reason. `networkAdmin` must cease being a free-floating token used differently by each script. Reuse the shared API helper to attach the token. A direct state-update function updates the shell and all protected page controls; no event bus or separate authentication framework is needed.

Header states are “Not joined,” “Admin locked · Unlock,” “Admin unlocked · 9 min · Lock,” and a progress/error state. Both the label and action remain visible on mobile. The countdown is based on the server expiry, updates at most once per minute, and never extends the session. Before a protected write, check expiry again; the server remains authoritative. Returning from a hidden tab rechecks network state and expiry before enabling protected editing.

Unlock opens a focused native `<dialog>` with password, explanation of the ten-minute session, and Unlock. Escape closes without losing the current page/draft; focus returns to the trigger. On success, refresh network permission state and affected logging/provider/model/queue/key views, while retaining local drafts and selections. If the server reports invalid authority, mark locked everywhere and offer unlock beside the failed draft. Do not send every 401 to this dialog: an invalid local bearer session requires “Open Mango from the tray again”; an incorrect master password stays an unlock-form error; an expired admin session locks protected settings. Preserve HTTP status and a small machine-readable reason where needed in the API helper.

Add a narrow local `PUT /api/local/network/lock` operation that removes the presented admin token digest from `unlocks` under the existing `authorization` lock. It is idempotent and does not revoke other browser sessions, membership, or inference keys. Manual Lock stops queued protected writes, waits for already-issued writes to settle, revokes the token, clears it, and updates every page. If revocation cannot be confirmed, show “Local controls locked; server sign-out unconfirmed until session expires,” keep a retry action, and do not claim confirmed revocation. Server invalidation after expiry/recovery/reset continues to work unchanged.

Do not persist admin passwords or admin tokens to localStorage/sessionStorage. The existing local browser-session bootstrap is a separate credential and retains its established handling. Reload therefore locks network admin; ordinary page/tab navigation does not.

### Create and join finish the authenticated workflow

Use existing create/join and unlock routes sequentially in one UI operation. Hold the submitted password only in the operation's local scope. After create/join succeeds, call the existing unlock endpoint with that same password, then adopt its returned token and expiry, refresh all dependent state, and show success. Clear the field immediately and clear references when the sequence finishes. Do not introduce password caching, an auto-renew session, a special bypass token, or weaker server checks.

Create shows its one-time recovery record after automatic unlock, then lands on Serving for this node with provider fields immediately available. Join lands directly on the same node-scoped workspace, already unlocked. A failed enrollment does not attempt unlock. A successful enrollment followed by failed unlock remains enrolled and displays “Joined; administration could not unlock” with a focused unlock retry. A lost create/join response triggers a read of local enrollment state before retrying, so double-click, timeout, or refresh cannot create duplicate setup operations. Recovery-record loss on an interrupted creation must be reported honestly; do not fabricate an export of a secret the server no longer has.

Preserve authorization distinctions: local activity/reserves/benchmark policy/startup/public ingress are local controls. `ConfigureLocalProviderAsync` currently permits token/mode/process-only changes when the existing profile/address are unchanged, and local approval of a matching authorized desired registration. Keep those paths usable without blanket network-admin gating. Shared origin/profile changes, aliases, queue, keys, membership, and network log policy retain their existing checks. Before enrollment, existing local-only model/provider/log workflows remain available.

## Autosave behavior and consistency

### User-visible contract

Checks, switches, radio choices, and selects persist on `change`. Plain text/numeric preferences queue after 600ms of inactivity when valid, and flush on blur or Enter. Do not write during IME composition. Optional blank values map deliberately to their existing null/default semantics; partial numbers, whitespace-only required names, or incomplete multi-field records do not go over the wire. Show “Complete the highlighted fields” instead of calling them saved.

Each edited field or record shows unobtrusive “Updating…,” “Up to date,” “Waiting for host,” “Not updated · reason,” or “Conflict · changed elsewhere.” “Saved” text is allowed as an acknowledgement; Save buttons are not. Errors stay beside their input and are announced politely once. No routine success toast storm. Editing continues while a request runs. Closing a detail view is navigation, never a save operation.

One small `preferences.js` helper owns a per-record draft, accepted baseline, dirty field set, request sequence, debounce, and at most one in-flight write. Domain scripts provide field validation, payload construction, and acknowledgement handling. Different records may save independently. While one write runs, coalesce subsequent edits and send the latest pending state afterwards. Never replace a field with an older response. Distinguish accepted server state from newer input. Never rebuild an active editor to refresh unrelated telemetry or list state.

Page changes keep drafts and queues in memory and flush valid pending edits. A small shell indicator links to any failed or incomplete edits on another page. Closing/reloading the browser with unresolved writes uses a native unload warning where supported; do not claim that a fetch during pagehide is durable. Nothing is stored in browser storage to manufacture an offline queue. Secret drafts are excluded from cross-page retention.

### Atomicity at existing write boundaries

Frontend serialization alone cannot protect two tabs or the tray. Use the current server locks and `SnapshotFile.UpdateAsync` closures to make compare-and-update atomic. Do not implement read-then-write checks outside those boundaries. Keep existing callers working; new expectation fields are optional for old local API callers, while the new UI always sends them.

| Record | Minimal consistency change |
| --- | --- |
| Local preferences and maintenance | Add an allowlisted field-update request on the existing local settings surface, with changed fields and their expected previous values. Merge into the latest record inside `UpdateAsync`; compare only touched fields. Include consent as explicit request metadata, not a writable settings property. Reuse validation and update-preference callbacks. This prevents a threshold edit from overwriting a simultaneous tray role change. No unrestricted JSON Patch/reflection writer |
| Startup | One boolean operation; refresh actual OS state after success/failure. Never copy stale unrelated settings into this operation |
| Queue wait | One scalar operation; update only the intended value and refresh acknowledgement. The UI supplies an optional expected value so same-field concurrent edits are reviewed |
| Shared models/providers | Reuse current revision guards. After each acknowledgement, fetch the accepted record/revision before the next write. Compare the draft's touched fields with changed server fields on conflict; merge disjoint changes, but require the user to resolve overlapping changes |
| Existing local provider | Add an optional expected nonsecret configuration guard under the existing provider/intents write serialization. Keep token handling separate: null means preserve, explicit clear means remove. A credential update must not erase a concurrent origin/mode change |
| Local model before enrollment | Reuse the existing local model revision. Enrolled models use the canonical shared-intent editor rather than a second competing local metadata form |
| Public ingress | Add an optional expected settings guard inside its existing writer before binding/persistence. Merge only changed valid fields into the acknowledged record; retain the existing listener replacement/error behavior |
| Model resource budget | Compare the expected budget/configuration inside the resource store update, not just in the earlier resource lock. Preserve busy-work, hardware, peak, slot, and calibration validation; reject changed deployment/hardware identity. Do not auto-overwrite a newer budget |
| Logging policy | Add an optional expected policy revision checked inside `ChangeAsync`. Preserve standalone versus network authority. A conflict refreshes current policy and retains the user's edited fields; unchanged fields cannot overwrite a newer policy |

Use a visible “Use current value” or “Keep my edit” only to resolve a real overlap conflict; these are not routine save steps. “Keep my edit” performs a new authorized write against the latest revision. No blind automatic replay after a 409. On 403, pause protected queues and ask for unlock in context. On a transient connection failure, preserve the draft and provide Retry; do not endlessly resubmit or retry non-idempotent creation, issuance, reset, or lifecycle operations.

### Domain rules that cannot be lost during conversion

| Domain | Automatic edit behavior |
| --- | --- |
| Existing provider | Origin/profile/enablement are shared registration fields; mode/token/process paths are clearly local. Changes to dependent fields remain a single valid record operation. Show when a saved remote intent still needs the owning computer's local permission |
| New provider | Add provider is a real creation action after choosing host/profile/origin and any local access. It creates exactly one registration and leaves the same Serving workspace ready with editable models. No incomplete registration is created on the first keystroke |
| Token/password fields | Provider token replacement submits on a completed change/blur or Enter, not a keystroke debounce. Clear the secret field after completion; report failure and request re-entry when necessary. Blank leaves the existing token unchanged; Clear token is an explicit credential-removal action. Setup/unlock/recovery passwords are submitted only by their named operation |
| Model metadata | Alias and each serving field use one canonical record. Modality-specific fields appear on the appropriate page, with configured versus observed values visible. Add rating creates a local draft row; it is committed automatically when label/rating are valid. Removing an existing rating persists immediately; blank rating means unrated, not deletion |
| Alias-wide ratings | Dedicated Aliases view explicitly says “Changes affect N deployments.” Show Mixed when members differ. Editing a valid rating performs the existing scoped bulk update automatically; never present a misleading single shared value or a second local per-model editor |
| Reduced reserves | Draft the below-default value, show the existing risk and unchecked consent inline, then persist automatically when consent is explicitly checked. Never infer consent from typing. Keep default/effective values visible. An incomplete reserve change does not block an independent role/threshold write |
| Initial resource budget | Wait until required workload, RAM, and every applicable discrete-GPU peak are valid. On the final valid change, persist automatically. Show missing fields and the currently effective budget. Do not activate half a budget or fill unknown measured values with zero |
| Overlap/attached authority | Preserve explicit qualification and attached-provider acknowledgements. Disabling overlap coherently sets one slot; enabling it does not bypass qualified-count validation. Keep hardware calibration and measurement invalidation semantics |
| Public upstream | Explain consequences beside its switch. Configuration fields persist without enabling exposure. Turning on is the explicit enable choice and only succeeds with valid current fields; failed binding leaves truthful effective state. Changing origin/proxy/port invalidates prior reachability evidence as the current backend already does |
| Logging policy | Mode changes show exact payload implications; retain Off/Sparse/Verbose/Everything. Automatic changes remain subject to existing authority and retention validation. Do not turn on logging for the dashboard |
| Explicit actions | Create, Join, Issue key, Revoke, Recover, Reset, Remove, Stop, Load/Unload, Retry benchmark, Collect, Download, and Check remain labeled operations, with pending/result feedback and required existing confirmation. They are not run by generic autosave |

## Resource chart and activity

Reuse `/api/local/resources` and the existing observation timestamp. Maintain at most 120 samples over a ten-minute window in browser memory. Poll the local resources route every five seconds only while a relevant visible page needs it; never run overlapping requests. Keep the existing open-browser activity stream and cleanup. Do not add peer probes, a network-wide refresh loop, persisted telemetry, or request payload retention.

Deduplicate by `observation.at`, plot using observed timestamps rather than fetch time, and discard out-of-order observations. Repeated cached samples are not new measurements. A graph begins empty and earns points over time; it must not fabricate a full history. A resumed/failed interval is a gap. Mark stale when the observation is older than `max(15 seconds, 2 × configured sample interval)` and show the actual sample interval; do not force protection sampling faster to make the chart prettier. A failed request preserves previous data with a visible stale/error label.

Use an inline SVG plot with a 0–100% axis, time labels, light grid, and direct legend labels. Four small selectable metric cards share the plot; CPU/RAM/GPU/VRAM can be toggled with labeled controls. Lines have distinct dash/marker patterns in addition to color. Current RAM/VRAM labels also show used/total GiB. Provide a text summary of current readings, extrema within the observed window, timestamp, and limitations; do not announce every new point through a live region.

Extend `ResourceObservation` with optional total CPU and total GPU-busy fields computed from the already-read Windows counters. CPU uses the existing busy/total deltas before worker subtraction. GPU is the busiest reported GPU engine across all processes, matching the existing engine grouping but without excluding managed PIDs; label it “GPU · busiest engine,” not average utilization or a sum of engines. Do not change `ExternalCpu`, `ExternalGpu`, `ManagedCpu`, protection thresholds, or attribution/calibration behavior. If a total metric is unavailable, show unavailable; the protection page may separately show the existing clearly labeled external-workload metrics. Do not synthesize total CPU by blindly adding clamped components.

RAM usage is total minus free. Dedicated VRAM uses each discrete device's known used/total values; expose a device selector when more than one exists and label the selected device. Do not add unlike devices into an unexplained total. Integrated GPUs show “Shared system memory,” without a fake VRAM percentage. Missing/zero-invalid denominators, unreported counters, unavailable devices, and unsupported topology produce gaps and explanations, never zero-use claims. If a device changes, start a distinct series for its stable ID and avoid connecting it to the previous device's line.

Activity Live uses visible rows with alias, operation, state, destination, wait/rejection reason, priority, queue/load duration, and reroute history in a detail view. Keep Received and Serving as tabs on the Activity page or separate labeled sections within its bounded list, not hidden controls containing primary actions. Overview is bounded to five summaries. History shows 50 rows per UI page, retaining backend caps/coverage and export of all available records. Filter changes reload immediately/debounced as appropriate; Collection remains an explicit network operation. Do not refetch the whole history during every resource tick.

## Everyday workflows and language

| Workflow | Intended sequence |
| --- | --- |
| Check health | Open Overview; read contribution/reason and resource graph; follow only a concrete attention item |
| Pause contribution | Toggle Contribute this computer off; immediate write; show Withdrawing until confirmed off. Resume uses the same control and respects cooldown, benchmarks, and resource gates |
| Select/load a model | Serving shows visible status and actions; choose preferred deployment or Load. Disabled controls explain actual capability, authority, busy state, or missing qualification beside its controls |
| Edit a shared model | Enter Serving; unlock in context if necessary; alias/context/capability/rating inputs are already visible; edit and see Updating then Saved/pending/applied. No Edit button or jump to another form |
| Add provider and provision node | Create/Join automatically unlocks and opens Serving → enter provider fields → Add provider → configure discovered model rows and the visible qualification pane → see actual ready/loadable state or precise blocker |
| Diagnose waiting work | Activity row explains reason; link opens the exact node/provider/model in Serving or the relevant Protection page. Return preserves filters and selection |
| Issue a client key | Network → Keys → Issue key; immutable name/scope/priority/explicit expiry; create once; focused one-time secret view with Copy and Hide, delivery state, and no public exposure side effect |
| Change retention | System → Logging policy; unlock inline if network-owned; edit and observe acknowledgement. Data clearing remains a distinct confirmed action on Data |
| Recover access | Network → Recovery explains password recovery separately from full reset. Preserve existing data-retention guarantees; reset has explicit destructive styling and confirmation |

Use “Contribute this computer,” “Not contributing,” “Contributing,” “Withdrawing,” “Cooling down,” and “Needs attention” in primary UI. Explain stored Leech/Contributor terminology in supporting copy if needed. Use “Provider,” “Model,” “This computer,” “Network,” “Requested,” “Applied,” “Waiting for host,” and “Last observed” consistently. “Check for updates” replaces “Check GitHub now.” Names lead; identifiers remain selectable in the relevant detail view. Never describe cached peer observations as guaranteed current online state.

## File-level implementation scope

| Files | Planned change |
| --- | --- |
| `wwwroot/index.html` | New shell, pages/subtabs, focused setup/unlock/secret dialogs, canonical setting containers, labels and status targets; all Save controls removed |
| `wwwroot/mango.css` | Replace document styling with the defined token system, shell, rows, metric/chart styles, responsive/zoom/focus/reduced-motion rules |
| New `wwwroot/shell.js` | Small route/page/tab/focus controller and shell state rendering; no client framework |
| New `wwwroot/preferences.js` | Small per-record autosave coordinator with deterministic testable timing and state transitions |
| `wwwroot/mango.js` | Shared API error metadata, settings write integration, provider/list/local-access flows; remove submit-button assumptions and competing local model metadata editor |
| `wwwroot/network.js` | Shared admin state, automatic post-create/join unlock, lock lifecycle, keys/recovery/setup and queue integration |
| `wwwroot/network-catalog.js` | Canonical Serving workspace with immediately editable model rows/provider intent, read-only node summaries, drafts/revisions and ownership labels |
| `wwwroot/dashboard.js` | Overview composition and bounded activity rendering; public settings move to their exclusive page while preserving handler ownership |
| `wwwroot/resources.js` | Resource history/graph, Serving qualification/Measurements integration, scoped polling, validation/consent handling and visible errors |
| `wwwroot/logs.js` | History pagination/filtering, separate policy/data page integration, shared admin state refresh, exact operation feedback |
| New `wwwroot/mango.svg`, `wwwroot/glyphs.svg` | Original mark, favicon, finite glyph family; no remote assets |
| `Host/LocalWeb.cs`, `Host/LocalSettings.cs` | Guarded field-level preference changes using existing validation and serialized persistence; preserve legacy route contract and update callbacks |
| `Peers/PeerAuthority.cs`, `Peers/PeerRoutes.cs` | Narrow current-token Lock operation; structured admin-session failure reason if required; existing create/join/unlock security preserved |
| `Peers/PeerIntents.cs`, `Providers/ProviderCatalog.cs`, `Providers/ProviderModels.cs`, `Providers/ProviderRoutes.cs` | Only necessary expected-state plumbing for local provider edits; reuse model/shared intent revision contracts and existing local-authority exceptions |
| `Host/PublicIngress.cs`, `Resources/ResourceProfiles.cs`, `Peers/PeerLogs.cs`, `Logging/LogRecords.cs` | Optional expected-state/revision checks at existing atomic boundaries; no persistence-system rewrite |
| `Resources/ResourceTypes.cs`, `Resources/WindowsObservation.cs` | Add optional total metrics from existing observations; existing safety metric semantics unchanged |
| `Resources/ResourceProtection.cs`, `Providers/ProviderCatalog.cs`, relevant peer catalog observations | Reuse or minimally expose structured, side-effect-free readiness diagnostics from existing eligibility/admission facts; preserve cache freshness and avoid a second JS scheduler |
| `tests/Mango.Tests/*` and a small `tests/webui` directory | Focused server guards/auth/telemetry checks and Node built-in test/assert checks for autosave/routing/history transformations; update page-title smoke check |
| `docs/usage.md`, `docs/windows-install.md`, applicable requirement/phase-six notes | New page names/workflows, automatic persistence and admin behavior, chart meanings/limits, preserved local controls and verification evidence |

Before editing a shared function, search every caller and relevant fixture. `ResourceObservation` constructor additions must be optional or all fixtures updated. Review `EndpointUpdate` callers and the in-progress generic fixture before touching its shape. Preserve working-tree changes in provider code, tests, and usage docs. Packaging already copies `wwwroot/**/*`; verify new SVG/JS assets are in the built output rather than adding a second asset pipeline. The existing case-sensitive static routes/CSP must permit the chosen local SVG references without weakening policy.

## Implementation order

1. **Capture the baseline and contracts.** Read the current diff, enumerate setting/action destinations against the relocation table, and add focused regression scenarios for post-setup unlock, admin state transitions, draft preservation, and local settings concurrent edits. Use disposable host data. Do not exercise reset/public exposure against real user state.
2. **Build the complete shell and visual primitives.** Replace layout, add route/page/subtab behavior, mark/glyphs, and basic state/field/row components using native DOM helpers. Move all existing features into their destinations immediately; do not leave a second old mega-page as the primary implementation.
3. **Repair administration and onboarding.** Implement global state and Lock, post-create/join Unlock, contextual dialog and dependent control refresh. Verify immediate management after both setup paths before proceeding.
4. **Implement reliable automatic persistence.** Add the small coordinator and the minimal server guards, then convert local preferences, maintenance/startup, queue, model/provider edits, limits, public ingress, and logging policy. Remove each Save control with its handler, not by CSS. Keep explicit creation/lifecycle/destructive operations separate.
5. **Complete Serving and daily workflows.** Provider configuration, directly editable model rows/actions, always-present qualification pane, side-effect-free authoritative readiness, aliases bulk view, node links, key copy/recovery containment, activity/history pagination and errors. Verify a fresh node can become usable on this one screen. Remove duplicate editors and obsolete selectors/submit helpers once callers are migrated.
6. **Complete Overview and telemetry.** Add truthful total observation fields, bounded local history, chart/text equivalents, actual effective contribution and preferred-model controls, useful attention links, and independent empty/error states.
7. **Qualify and finish.** Run focused checks and the existing host suite once implementation stabilizes. Review rendered screens at the required sizes/states, fix functional/visual defects, update docs, and record actual results. Do not call the work complete with a mockup, passing build alone, disabled controls, fake data, hidden Save buttons, or unimplemented subpages.

## Acceptance checks

| ID | Pass condition and evidence |
| --- | --- |
| A01 Pages | Only one full page/subpage is active and focusable. All destinations in the map work with links, Back/Forward, reload, direct object routes, and legacy-anchor redirect. Session bootstrap still works without exposing a token |
| A02 Layout | At 1280 × 800, primary Overview controls and resource plot fit before scroll. Header/nav/admin state remain visible while working. At 960 × 720, 390 × 844, and 320 CSS px/400% zoom, no form controls are lost or clipped; only active-page content scrolls |
| A03 No Save | Inspect static markup and generated editor DOM for Save controls and disguised commit footers. Toggle/edit every preference family and verify persistence after reload. Creation/action buttons perform named operations only |
| A04 Autosave races | Deterministically delay responses while making three rapid edits; final server/UI state equals the newest valid edit. Stale responses and unrelated polling cannot overwrite a draft or move its caret. Invalid and valid independent edits do not block each other |
| A05 Concurrent writers | Two browser/API writers and a simulated tray role update preserve disjoint preference edits. For guarded records, same-field/revision conflict shows current and draft values and requires explicit resolution; stale budgets/providers/logging/public settings do not silently overwrite current state. The single-value queue preference also guards same-field concurrent edits |
| A06 Setup unlock | On disposable hosts, both Create and Join require password entry once, establish ordinary admin token/expiry, show unlocked shell, and permit an immediate shared model or queue edit. Failed setup never unlocks. Setup-success/unlock-failure is honest and does not replay enrollment |
| A07 Authority lifecycle | Locked/unlocked/expiry/manual lock/recovery or reset invalidation update every protected view including Logging policy. Lock invalidates that token server-side. Local bearer expiry is distinct. Drafts survive admin expiry and navigation; unrelated local-only controls still work |
| A08 Ownership | Shared/local scope is explicit. Remote users cannot alter another host's local protection, credentials, or process permission. Existing local provider credential/mode/process-only edits and matching desired-registration approval remain usable without blanket admin gating |
| A09 Catalog workflows | Serving shows alias/context/relevant capability/rating inputs and selection/load/unload on entry without Edit/disclosure prerequisites. Add/refresh/remove/stop providers work including Generic. One canonical workspace shows requested/applied/pending/conflicted state; aliases bulk scope is unmistakable |
| A10 Limits and consent | Below-default reserves require actual consent. Blank defaults, per-device limits, attached/overlap acknowledgement, hardware changes, busy work, and benchmark invalidation retain server validation. Incomplete new budgets never become active |
| A11 Telemetry | Tests cover total/external metric distinction, timestamp deduplication, out-of-order samples, 120-point bound, empty startup, pause/resume gap, failure/staleness, null data, multiple discrete GPUs and integrated/shared memory. Real Windows readings appear; no synthetic history, peer polling, or request logging is introduced |
| A12 Activity/logs | Bounded live summaries and 50-row history pages retain request reasons/timing/priority/reroutes. Filters, collection, partial coverage, dropped capture, export, policy, and clear acknowledgements work. Logging off leaves live resources/activity available |
| A13 Key/recovery/public | One-time key Copy/Hide and recovery download work; expiry/scopes/priority and propagation rules preserved. Recovery and reset remain distinct. Issuing a key does not enable public access; ingress config and reachability states remain truthful; admin endpoints remain excluded |
| A14 Accessibility | Keyboard-only navigation/edit/setup/key flow works; tab semantics correct; dialogs trap/return focus and Escape behaves; labels/errors/statuses are accessible; chart has text alternative; contrast and focus visible; reduced motion respected. Record any untested assistive-technology behavior honestly |
| A15 Brand and polish | Actual rendered app matches the design tokens/board direction across Overview, populated Serving with immediately editable models and qualification, dedicated settings pages, setup, locked state, empty state, and failure state. Consistent spacing/icons/type, no raw IDs dominating, no placeholder copy or decorative excess |
| A16 Regression and delivery | Build and existing Mango checks pass, new static assets package, existing dirty generic-provider changes remain, UI docs match actual behavior, no saved secrets in browser storage/logs, and no real user configuration was changed by qualification |
| A17 Node provisioning | Starting from Create and separately Join, land already unlocked in this node's Serving workspace. Configure a provider and make a representative model usable without leaving the screen or opening an Edit mode/disclosure. Required local permissions and qualification fields are present in context. Test missing context, attached permission, no budget, benchmark policy, busy/pressure, remote local-action, and stale observation. Verify unloaded JIT-capable models and unrated models are not falsely labeled blocked. Every readiness explanation comes from authoritative backend facts |

Use Node's built-in test/assert facilities for the small pure coordinator/history/route logic; no new browser-test dependency solely for this redesign. Use the available browser/computer tool for actual rendered acceptance and keyboard checks. Existing C# handler checks should exercise the atomic concurrency guards and real local HTTP boundary. Test security and destructive flows on disposable fixtures, not by turning on public access or resetting the owner's network. Do not rerun unrelated hardware qualification after every CSS change; run the relevant checks when their logic changes.

### Critique coverage

| Finding | Repair | Acceptance |
| --- | --- | --- |
| F01 Setup does not unlock | Sequential setup/unlock using transient submitted credential; partial-success recovery | A06 |
| F02 Hidden administration state | Persistent shell control, contextual dialog, real Lock | A07, A14 |
| F03 Giant document navigation | Exclusive routed pages/subtabs and bounded scroll regions | A01, A02 |
| F04 Wrong landing priorities | Operating Overview with quick controls/resources/attention | A02, A11, A15 |
| F05 Save controls | Automatic persistence and separate named operations | A03 |
| F06 Unsafe naive autosave | Per-record queue/drafts, atomic guards, revisions/conflicts | A04, A05, A10 |
| F07 Collapsed core model actions | Visible editable model rows/actions and always-present qualification in Serving | A09, A17 |
| F08 Repeated editors | Canonical Serving workspace and shared shortcut state | A03, A09 |
| F09 Unclear ownership | Scope labels and requested/applied/effective states | A08, A09 |
| F10 Text-only/stale resources | Real bounded chart, timestamps, gaps, total/external labels | A11 |
| F11 Form-based first run | Focused Create/Join choice and successful authenticated handoff | A06, A14 |
| F12 Detached feedback | Field/record progress/errors, operation-specific results | A04, A14 |
| F13 Permission drift | One auth transition updates all consumers | A07, A08 |
| F14 Unbounded mixed log page | Paged History; separate policy/Data pages | A12 |
| F15 Weak visual hierarchy | Cohesive token/mark/glyph/layout system | A15 |
| F16 Implementation-first language | Consistent operating vocabulary and friendly-name hierarchy | A09, A15 |
| F17 Secret/recovery containment | Dedicated Keys/Recovery and focused one-time secret view | A13 |
| F18 Interaction accessibility | Native controls, correct tabs/dialogs/focus/reflow/text chart | A02, A14 |
| F19 Inadequate UX qualification | Deterministic failure/race tests and actual rendered review | A01–A17 |
| F20 Fragmented provisioning and passive reports | Immediately editable Serving workspace, contextual local qualification, automatic authenticated setup handoff, authoritative readiness | A06, A09, A17 |

## Completion boundary and known limits

This plan defines a complete UI repair, not a platform rewrite. Keep the current ten-minute admin lifetime, existing provider/network contracts, supported hardware observations, and authorization boundaries. Do not add account roles, hosted administration, provider installation, theme preferences, configurable dashboards, or persistent telemetry history.

The graph records only samples observed while the relevant UI is open; its time window can contain gaps. Total GPU is the busiest reported engine, not a universal physical-device utilization measurement. Missing Windows counters stay unavailable. Cross-host behavior needs the disposable multi-host fixture and, where feasible, a real peer check; do not claim a second physical machine was tested unless it was. The design board is a visual target, not evidence that application behavior has been implemented.
