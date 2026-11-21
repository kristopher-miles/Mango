# Per-model YOLO mode

YOLO is a local per-model setting. Enable **YOLO** on the owning computer for models that may share native provider capacity. The [November 21 serving boundary](requirements/09-recorded-decisions.md#serving-boundary--november-21-2026) specifies the rollout policy.

For a group of YOLO models, send additional loads through the provider's native lifecycle without a Mango RAM or VRAM fit check. The provider accepts or refuses each load and validates inference. An attached model must be loaded through its provider. Client-facing model names come from the provider or an owner alias.

| Incoming request and current residency | What Mango does |
| --- | --- |
| YOLO request, only YOLO models resident and native capacity available | Load only the requested model if needed. Other YOLO models can keep generating while the provider accepts the load. |
| YOLO request, non-YOLO model must leave | Try an eligible alternate. If none can serve, wait for conflicting work to finish, unload the required non-YOLO residents and verify release, then load and serve only the requested YOLO model. |
| Non-YOLO request, YOLO models must leave | Try an eligible alternate. If none can serve, wait for the last running YOLO request to finish, unload all YOLO residents and verify release, then load and serve the requested non-YOLO model. |

If Mango lacks unload authority or cannot verify release, reroute or wait. Subsequent YOLO models load individually as requests arrive. When the provider accepts an additional YOLO load alongside another YOLO model, that other model stays loaded and may keep generating.

Sustained external CPU or GPU activity makes the whole node a leech, including YOLO. The quiet cooldown and authorized managed release apply. The node remains available as an ingress. Native benchmark measurements can still be collected when scheduled.

YOLO preserves key scopes, transport bounds, queue priority, deadlines, provider slots, contribution and cooldown, and attempt settlement. If you disable YOLO, a new additional load uses checked co-residency where configured. A sole-model load or valid inference on a loaded model still goes to the provider for the capacity decision.

The authenticated local API accepts `PUT /api/local/providers/models/{deployment}/yolo` with `{"enabled":true,"revision":<model options revision>}`. The setting belongs to the local deployment, independent of its public alias.
