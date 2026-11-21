# Mango WebUI repair verification

This is a historical verification record. For current dashboard and serving behavior requirements, see the [November 21 serving boundary](../requirements/09-recorded-decisions.md#serving-boundary--november-21-2026).

Implementation follows [the repair plan](../plan/09-webui-redesign.md) and [F01–F20 in the critical review](2026-11-14-webui-critical-review.md). This records exercised behavior, rather than treating the visual direction board as a finished UI.

## Sidebar follow-up

The owner's subsequent layout correction removes the full-width top header and its reserved 60 px row. Branding and host identity now live in the navigation sidebar; administration state, Lock, and unfinished changes sit with contribution status at its bottom. On narrow screens this becomes bottom navigation, leaving the top of the viewport for page content. Labels and usage/install instructions refer to the sidebar.

The build, full Mango host suite, 18 frontend checks, JavaScript syntax checks and whitespace checks passed after this adjustment. A fresh visual recheck was stopped by Computer Use because it could not confidently determine the browser URL; no further UI automation was attempted. The screenshots and rendered observations below document the earlier shell, before this follow-up, and do not certify the new sidebar layout.

## Automated evidence

- `dotnet build Mango.slnx --no-restore`: passed, no warnings or errors. Static assets are included in the build output.
- `dotnet run --project tests/Mango.Tests -c Debug --no-restore`: passed. Includes provider fixtures and Generic lifecycle/streaming, real local HTTP authorization, guarded preferences, shared intents and local ownership, TLS ingress boundaries, log capture/collection/retention/deletion, maintenance, resource admission/benchmarks, scheduling and cancellation. Two Mango processes communicated over the live Windows Tailscale interface on one physical computer.
- `node --test tests/webui/*.test.cjs`: 18 passing checks. Covers all routes and legacy/object links; no Save/disclosure controls and asset availability; serialized/coalesced writes, invalid independent fields, same-field and disjoint conflicts, failed/paused queues and hardware review; truthful bounded telemetry and freshness; Create/Join unlock sequencing and partial/uncertain failures; exact logging write fields and clearing stale qualification state.
- `node tests/webui/enrollment-live.cjs` with `DOTNET` set to the local SDK: passed against two newly created temporary installations. The actual frontend enrollment helper performed Create and separately Join, received ordinary expiring admin sessions, immediately changed the protected queue, and proved that Lock invalidated its token. The script stops its hosts; it does not touch the owner's default installation.

## Rendered and interaction evidence

Actual Edge review used a temporary data directory and the existing Generic HTTP fixture. No default Mango configuration was used. Browser extension control became unreliable; native computer-use controls and Edge's device toolbar continued the review. Console inspection distinguished extension-injected favicon CSP errors from application errors; Mango's CSP was not relaxed for the tool.

- Overview rendered real Windows total CPU, GPU-engine activity and RAM values. The integrated GPU was identified as shared RAM, with dedicated VRAM unavailable. At 1280 × 800, the primary controls and plot fit before scroll.
- Serving was reviewed populated, at desktop size and 390 × 844 / 320 CSS px. At 960 × 720, the fixed shell remained visible during model/qualification scrolling. Fields reflowed without clipping; subtab overflow remained navigable.
- Connected a Generic provider through the UI. Edited model alias to `fixture-chat` and fallback context to 2048 directly in its card; verified persisted values after host/page reload.
- Entered a capability rating while locked, unlocked through the global dialog, and verified the pending rating became the shared value. The 320 px header showed both expiry and Lock. Manual Lock returned the header and protected queue/key/logging controls to locked state.
- Changed cooldown rapidly through 302, 303 and 304, switched tabs, and verified 304 on the host. Deferred maintenance updated automatically. Entering a zero RAM reserve displayed explicit consent, retained the draft, and did not change the host reserve; clearing the field restored the default without recording consent.
- Queue timeout, disabled public-listener port and logging retention changes persisted through their automatic writers. The rendered logging test found a rejected write containing computed read metadata; the writer now sends only editable fields and its revision guard. Retention changed from seven to eight days and survived reload. Public access remained off throughout.
- Entered complete local fixture limits in Serving and enabled contribution there. The backend reported `readyToLoad` for the qualified unloaded model and `limits` for the unqualified second model. A request through Mango loaded the qualified model on demand and returned `Hello from the fixture.` This validates provisioning and protocol behavior, not real model performance or calibrated engine memory.
- Selected the loaded model with Use locally and verified the canonical selection on the host and Overview. A separately requested provider appeared in the same Serving selector with its requested address, local permissions and Approve requested connection action. Its empty model/qualification state was inspected without approving or connecting that reserved invalid address.
- All 19 page destinations were inspected through the rendered shell. Network separates computers, queue, keys, public access, recovery and setup; Protection separates activity, memory and benchmarks; System separates startup, updates, logging and data; Activity separates Live and History. Existing membership now explains why setup is complete and links directly to Serving.
- Browser Back and Forward restored the expected Activity/History destinations. Arrow-right followed by Enter activated Serving subtabs. Keyboard focus was visibly outlined on the benchmark selector. The native unlock dialog and focus return were exercised.

## Acceptance map

| Plan checks | Evidence |
| --- | --- |
| A01 routing | Pure route checks; rendered 19 destinations; Back/Forward; page reload and private fragment bootstrap |
| A02 layout | Actual Overview 1280 × 800; intermediate 960 × 720; Serving and administration at 390 × 844 and 320 CSS px |
| A03 preferences | No static/generated Save or Edit-mode controls; actual provider, model, rating, local preferences, maintenance and qualification edits; domain writer/HTTP checks |
| A04–A05 persistence | Delayed/coalesced coordinator tests; guarded local/provider/model/budget/queue/logging/ingress writes; UI rapid edit and navigation |
| A06–A07 authority | Actual frontend helper against two hosts; rejected-password distinction; token-specific server lock; UI unlock, resumed draft and locked controls |
| A08 ownership | Shared-intent/local-provider checks; local-only qualification and contribution remain usable while admin is locked; requested local approval is exposed in Serving |
| A09 catalog | Actual Generic connection and direct model/rating editing; named lifecycle actions; preserved Generic fixture checks; bulk alias scope/count/Mixed state |
| A10 protection | Actual incomplete/reduced-reserve draft behavior; server consent and budget validation; hardware-review coordinator check; resource admission tests |
| A11 telemetry | Real Windows readings plus null/multiple-GPU/ordering/freshness/gap/history-bound tests |
| A12–A13 operations | Existing real-host logging/key/recovery/ingress checks; separate rendered pages and preserved explicit action handlers |
| A14 accessibility | Native controls/labels, exclusive page accessibility trees, keyboard tab activation and visible focus, native dialog, textual plot description, responsive reflow |
| A15 brand | Actual screenshots listed below; original SVG mark/glyphs, consistent typography, spacing, colors and scope/readiness language |
| A16 delivery | Build/assets, full host checks, unchanged pre-existing Generic work retained, usage and requirements updated |
| A17 provisioning | Live Create/Join helper plus UI provider/model/qualification/contribution workflow and successful JIT fixture inference; authoritative readiness tests |

## Actual application screenshots

- [Overview, normal window](mango-overview-implemented.png)
- [Editable models and local qualification, normal window](mango-serving-implemented.png)
- [Overview at 1280 × 800](mango-overview-1280.png)
- [Serving at 1280 × 800](mango-serving-1280.png)
- [Serving at 390 px](mango-serving-390.png)
- [Serving at 320 px](mango-serving-320.png)
- [Unlocked administration at 320 px](mango-unlocked-320.png)

## Limits of this qualification

No second physical machine, real LLM engine, discrete multi-GPU hardware, screen reader, or Windows startup registration was exercised. The startup page correctly explains that registration is unavailable for a non-default test data directory. Multiple/discrete GPU cases are automated fixtures, not measurements of physical devices. A 320 CSS px emulation was used for reflow; browser 400% zoom is not separately claimed.

Network enrollment and destructive/security-sensitive key/recovery/reset cases were automated against disposable hosts rather than performed against the owner's network. The normal UI unlock/lock path was exercised; no new real inference key was issued or copied into the clipboard. Provider credential storage remains DPAPI-backed; only the existing local browser-session bearer is in sessionStorage. Admin tokens/passwords remain transient, and one-time secrets are cleared when their dialog closes.
