# Per-model YOLO mode

YOLO is off by default. On the owning computer, open the model card and choose **Enable YOLO**. The option is local to that host's stable provider/deployment identity; aliases and remote registry edits do not enable it. All supported providers can use it.

The provider owns capacity and input validation for an enabled model. Mango uses the adapter's existing native load action with no Mango context override; an unmanaged or non-loadable registration still requires loading in its provider. Mango preserves the complete inference payload apart from model routing and provider credentials, and returns native inference statuses and bodies, including capacity errors.

YOLO models share native provider capacity with other YOLO models on the host. When a protected model needs the host, Mango waits for work in the other mode to settle and uses managed unload to release the resident YOLO models. A later YOLO request can switch back once protected work has settled. Evicted models reload on demand through their normal provider lifecycle. A resident without a verified managed unload and reload path can block a switch.

Automatic pressure withdrawal still preserves YOLO work. The model's real memory remains visible in telemetry, and the protected mode applies its usual headroom and workload checks after a switch. The local benchmark policy can take a short native sample from a resident YOLO chat, embedding, or decision model. Full memory qualification applies to protected models.

Authentication, inference scopes, routing capability selection, body/transport bounds, cancellation tracking, verified lifecycle settlement, provider identity checks, explicit serving disable, contributor pause, provider unload and stop remain in effect. An uncertain request/load still must settle before it can be reused. Disabling YOLO restores ordinary resource checks without changing native settings or saved qualification budgets.

The existing authenticated local control surface also accepts `PUT /api/local/providers/models/{deployment}/yolo` with `{"enabled":true,"revision":<current model options revision>}`. A stale revision or busy provider is rejected. The option and its state are exposed in the local model card and peer model observation. Peers that predate YOLO may hide the model while its host has a resource hold; they need the compatible general build to route in that condition.
