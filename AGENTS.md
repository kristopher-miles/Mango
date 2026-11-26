# Mango project rules

Mango is a bridge between model hosting programs. The hosting program decides whether it can serve a valid request. Mango owns the public API contract, authorization and key scopes, operator serving settings, queue priority and deadlines, benchmark occupancy, and attempt ownership. Provider capabilities inform routing. Try an eligible idle worker for an independent request before queueing it behind a busy alias worker. Preserve one execution owner and the client's original deadline across reroutes.

## Agent workflow

- Use simulated timers for all idle tests, including release qualification. Never run realtime idle tests.
- Start Mango repairs without waiting for current network work to finish. Repairs take priority. Taking network clients offline is acceptable and preferred when it helps the repair proceed.
- Work in the primary branch. Create another branch only when the user explicitly requests it.
- Fix reported bugs in Mango itself. Use client-specific investigation to gather evidence for the product fix. A repair is complete after releasing the Mango fix and updating the affected client through Mango's ordinary update process.

## Host activity and model residency

- Sustained external CPU or GPU activity makes a contributing node yield to the user and enter its configured quiet cooldown. Attribute provider inference and benchmarks to Mango where observable. CPU and GPU utilization drive activity detection. RAM, VRAM, and commit pressure do not cause withdrawal. Leech mode applies to the whole node, including YOLO deployments, while ingress still routes requests.
- Check RAM and per-device VRAM only before an additional non-YOLO model load would leave multiple models resident under the owner's co-residency policy. Use comparable native or measured requirements or explicit owner overrides. Count load peaks and resident memory once. If fit cannot be established, use an eligible alternate or the normal wait-and-swap path for an authorized local load. Send sole-model loads and valid requests to loaded models to the provider. Benchmark completion, resource estimates, and stale allocation faults are never ordinary serving requirements.
- When a legitimate request would wait for conflicting work or require a model swap, first try an eligible alternate node. If none can serve it, wait for the conflicting requests to finish, unload only the residents required for the new load using authorized lifecycle operations, verify release, then load and serve the requested model. Apply the same sequence in both directions between YOLO and non-YOLO. Keep later conflicting work from prolonging an admitted switch under the queue rules.
- Evict resident YOLO models as a group after the last active request in the group finishes. Load only the requested model after a switch. On later YOLO requests, load each additional YOLO model as needed alongside resident YOLO models, without Mango's memory fit check or waiting for their requests to finish, when the provider supports and accepts that load.

Use [the recorded boundary decision](docs/requirements/09-recorded-decisions.md#serving-boundary--november-21-2026) and [acceptance requirements](docs/requirements/06-acceptance-and-coverage.md) when changing serving, routing, benchmarks, or host activity. Update the rules and acceptance cases together when the owner changes the boundary.
