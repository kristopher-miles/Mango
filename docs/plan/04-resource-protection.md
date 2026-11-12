# Phase 4 Benchmarks and local resource protection

Deliver safe local admission and automatic withdrawal when the owner needs the machine. This phase owns resource truth; network scheduling must call it rather than reproduce its logic. Depends on phases 1–3.

Read the shared foundation, BENCH-01–06, RESOURCE-01–07 and MEMORY-01–06. Windows telemetry is implemented here; other operating-system collectors are deferred.

## Work

1. Read Windows CPU/process, physical-memory and GPU observations using available OS/runtime counters. Track explicitly managed provider worker trees. Reuse native facilities or a narrowly justified vendor library where necessary; do not launch a new command-line telemetry process every sample. Identify unsupported measurements honestly rather than reporting zero usage.
2. Run one local observation loop, at most once every two seconds and slower when the owner selects a longer interval. Do not run one loop per model or send raw samples across the network. Feed the resource gate and activity detector from the same observations. Fresh checks immediately before admission are allowed when the last observation is inadequate.
3. Implement local states: contributor, withdrawing, automatic cooldown, manual leech, and fault. Stop admissions first, cancel active/queued provider work, unload through adapter APIs, and stop only explicitly managed processes if needed. Inspect actual release once the transition completes; keep a visible fault if release fails. Push the material availability transition, not each sample.
4. Detect external CPU/GPU load excluding monitored inference workers where attribution supports it. Use configurable cutoffs, the accepted initial sustained-load duration, and a continuous quiet cooldown. Renew the quiet interval on renewed load. Memory pressure withdraws immediately even if Mango caused it; include that cause in the future operational-log record. Manual leech survives cooldown and restart.
5. Activate memory reserves saved in phase 1. Track each discrete GPU separately and host/unified memory as appropriate. Do not sum devices into an imaginary pool. Admission checks expected incremental peak plus retained/loading/running allocations against current free resources and the owner's configured reserve. Do not double-count already resident weights as both free-memory loss and a fresh reservation.
6. Serialize the short reservation decision within the host. Never hold the lock through loading or inference. Concurrent requests cannot spend the same free memory. Reservations survive loading/cancellation until the adapter's state is settled; stale peer hints cannot bypass them. Allocation failure invalidates the unsafe profile and stops repeated retries of the same placement.

## Benchmark policy

Enrollment selects Never, Now, or When convenient, and that local choice persists for later model/provider additions. Changing the policy is a local action, not another per-model consent flow.

- **Never:** do not automatically benchmark. Use provider-reported or owner-configured limits and conservative single-execution admission. Absence of a measurement is visible. If no usable memory estimate exists, request a local profile/limit rather than invent a capacity or run a hidden benchmark.
- **Now:** finish required profiles before this node serves inference. Run one benchmark at a time. A failed or canceled test is incomplete; show the error and let the owner retry or change policy, without silently opening contribution.
- **When convenient:** after five minutes without client work on this node, start the next pending benchmark. Any incoming client work cancels it and takes precedence. Network activity elsewhere does not suppress this node's benchmark. Partial profiles remain incomplete and can resume at the next missing measurement during a later idle period.

For applicable text-like deployments, measure short, half-context, and full usable context with reserved output. Separate cold load, warm execution, and cache reuse. Store load delay, prompt processing, first-token time, tokens/second, elapsed time, RAM/VRAM peaks, request dimensions, and the relevant artifact/runtime/device/context/concurrency fingerprint. Use appropriate workload dimensions for image/audio rather than text-context tests.

Retain matching profiles across restarts and full reset. Invalidate only affected measurements when configuration changes. Use measured peaks and live admission for overlap; unmeasured overlap stays serialized. Do not test every possible model pair or infer full-context/concurrency capacity from a tiny prompt. The user's below-default reserve choice is honored after their one recorded warning; it does not turn an unknown model requirement into a known one.

## Interfaces handed forward

Expose a direct local admission function returning admitted reservation, waitable conflict, or unavailable reason; methods for release/cancel; current contributor state; and benchmark estimates. Phase 5 owns queue ordering and destinations. This phase does not create a second scheduler or a network reservation service.

## Minimum checks

- On actual Windows hardware, run monitored inference alone, then an unrelated CPU/GPU workload. Confirm self-load exclusion where supported and real withdrawal, release, and quiet recovery. Record telemetry limitations for the tested hardware.
- A small deterministic check covers racing reservations, transient load peaks, memory-failure cleanup, manual leech persistence, and the one-time warning for a zero reserve.
- Exercise all three benchmark policies, a newly registered model, interrupted convenient work, and a fingerprint change. Do not require a huge full-context benchmark on every development test run.

## Done

The host can actually reclaim managed inference resources and report when it cannot. Attached providers retain their declared limitations. Resource admission is the sole local authority, runs in bounded time, and causes no routine network chatter. Profile files and local protection settings remain outside source control.

## Windows qualification

- Windows 11 Pro with a Ryzen 5 7640HS and Radeon 760M identified the integrated GPU as shared host memory. DXCore enumerated the adapter and PDH returned per-process engine activity. The qualification host had no discrete GPU. Dedicated device budgets and the handling of unknown device readings are enforced by the admission gate.
- A Mango-owned CPU llama.cpp server ran Qwen2.5 0.5B Q4_0 with a 2,048-token served context. A live sample attributed 50.3% CPU to the managed worker and 8.5% to external work while Mango remained Contributor. A separate CPU workload registered 60.4% external CPU and an independent Vulkan llama.cpp run registered 94.7% GPU activity. Each crossed its configured cutoff for ten seconds, withdrew the host, and stopped the owned worker while the unrelated workload was still running. Quiet recovery returned the host to Contributor.
- With activity monitoring disabled, a temporary 1% RAM pressure cutoff triggered withdrawal within 2,067 ms of changing the cutoff. The owned worker exited and the host entered cooldown. The check used a temporary settings directory.
- The live Now profile measured cold load and short, half, full, and repeated full chat passes. The provider reported 2,030 input tokens in the full pass with 16 reserved for output. It reported cached input on the repeated pass. Some short passes fell between Windows samples and have unknown memory peaks. Owner-configured incremental limits remain in force, and observed host free-RAM changes account for shared GPU memory in the same pool.
- The deterministic suite exercises racing and overlapping admissions, transient allocation accounting, failed placement invalidation, manual leech, the one-time reserve warning, all benchmark policies, an interrupted convenient pass, local enrollment choices, a newly registered model, fingerprint changes, and an unconfirmed attached request through shutdown and recovery.

Activity attribution covers owned workers and managed loopback listener process trees. A backend outside that tree can appear as external activity. CPU attribution across multiple Windows processor groups and per-device VRAM on a linked multi-physical adapter are reported as unavailable. An attached endpoint can be explicitly approved for forwarding with activity protection enabled. Mango cancels forwarding and checks for settlement, and the external provider may retain its resident allocations.

Text context passes use synthetic text and provider usage or tokenization evidence. An endpoint that cannot verify the exercised context leaves the profile incomplete under Now. Inline media and overlap require local qualification for those dimensions. A convenient benchmark defers cold unload of the currently selected resident deployment and can resume that pass after the deployment is unloaded.

Phase 5 can use `ResourceProtection.TryAdmit`, `Estimate`, `Status`, and `ClientArrivedAsync`. A lease provides cancellation and remains reserved until completion or verified settlement. Overlap requires an owner-qualified allowance and a native slot count for the same deployment, and separate deployments on the same endpoint remain serialized until independently attributable. With concurrent leases on one endpoint, worker growth is never credited against multiple outstanding peaks. Peers receive effective contribution and deployment availability changes.
