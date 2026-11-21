# Use Mango on Windows

The November 21 [serving boundary](requirements/09-recorded-decisions.md#serving-boundary--november-21-2026) is the current policy. It is pending implementation and rollout. Installed releases can still show the earlier resource qualification and benchmark controls.

## Install the Windows package

Build `dist/Mango-win-x64.zip` from PowerShell 7 using the SDK pinned in `global.json`. The ZIP includes the Windows x64 runtime, static UI, and installation guide. [Windows install and removal](windows-install.md)

```powershell
./scripts/package-windows.ps1
```

The script prints the archive path and SHA-256. Use `-Dotnet <full path>` if the pinned SDK is outside `PATH`. The package uses fixed archive timestamps, ordinal entry order, and pinned runtime 10.0.15. Use the same Windows tooling and source for repeat builds.

## Build and run from source

Install the .NET 10 SDK pinned in `global.json`. From PowerShell at the checkout root, build, run the host checks, and launch the desktop app.

```powershell
dotnet build -c Release
dotnet run --project tests/Mango.Tests -c Release
Start-Process .\src\Mango\bin\Release\net10.0-windows\Mango.exe
```

The host runs in the Windows notification area. Open Mango from its tray menu or launch the app again. Closing the browser leaves the host running. The tray menu also switches the local role preference and quits Mango. The Windows startup control in the browser can register the tray host for the current user's next login.

Use the DLL with `dotnet` for console commands. Run `--url` from a second shell to get a temporary local browser session URL. Keep that URL private while the host is running. Stop the host with Ctrl+C in the first shell or `--quit` in a second shell.

```powershell
dotnet .\src\Mango\bin\Release\net10.0-windows\Mango.dll --headless
dotnet .\src\Mango\bin\Release\net10.0-windows\Mango.dll --url
dotnet .\src\Mango\bin\Release\net10.0-windows\Mango.dll --quit
```

Settings live in `%LOCALAPPDATA%\Mango`. With that directory, Mango listens at `http://127.0.0.1:53781` by default. Configure inference clients on this machine with `http://127.0.0.1:53781/v1` and a scoped inference key from **Network → Keys**. If a listener cannot take its port at startup, Mango opens **System → Node port**. Choose another port and restart Mango to use it for both local and Tailscale connections, then update your client's base URL. For isolated runs, pass `--data-dir <private directory>` to each command. Without a saved node port, isolated runs pick an available loopback port, and `--port <port>` can select an explicit one. Windows startup registration uses the default data directory.

If the primary settings file is unreadable, Mango reports recovery from the last good snapshot in the browser. A subsequent settings change retains the unreadable file as `local-settings.json.invalid-*`. If neither copy is usable, check `startup-error.txt` in the data directory. This diagnostic also reports startup binding failures.

## Find your way around

Mango opens on **Overview**, with contribution, current work, and a ten-minute local resource plot. The **This computer** card shows the Tailscale inference API URL when this computer is enrolled and listening. Click the URL to copy it, or double-click it to open **Network → Keys**. The **Keys** link opens the same page. Administration stays in its current lock state, and clients still need an inference key.

Resource history starts when the view opens; missing counters and gaps are labeled. Total CPU and GPU activity are distinct from the external activity used by protection. GPU utilization is the busiest reported engine; integrated GPU memory is shared RAM.

The fixed navigation opens **Overview**, **Serving**, **Activity**, **Network**, **Protection**, and **System**. Related settings use tabs. Only the current page scrolls. Preferences update automatically: text fields after a short pause or when you leave the field, switches and selections immediately. There are no Save buttons. Inline status distinguishes updating, incomplete, failed, and changed-elsewhere values. Failed drafts stay in this tab, with a sidebar link to unfinished changes; review conflicts or retry after correcting the problem. Closing or reloading with unfinished changes warns before discarding them.

The sidebar always shows network administration state. **Unlock** opens password entry; **Lock** revokes this browser's administration token on the host. Administration expires after ten minutes and is kept only in memory. Local protection and existing provider credentials/process permissions remain local controls, independent of network administration. If the local browser session expires, open Mango again from its tray menu.

## Connect a local model server

Open **Serving**, choose **Add provider**, select the profile for an existing LM Studio, Unsloth Studio, Unsloth run, or llama.cpp server, enter its address and bearer token, then choose **Connect provider**. Existing provider fields are immediately editable and update automatically. A blank token preserves the stored credential; enter a replacement or use **Clear saved token** explicitly. Choose **Generic endpoint** for a server that implements the generic provider contract and enter its origin explicitly. The other profiles use their loopback defaults when the address is blank. HTTP is available on loopback. An explicitly configured non-loopback provider uses HTTPS. Mango protects saved provider tokens with Windows DPAPI for the current user. [Generic provider guide and runnable fixture](generic-provider.md)

**Attached** mode lets Mango inspect the provider and send inference requests. **Managed** mode authorizes the provider's supported model load and unload operations. To let Mango launch and stop a single-model llama-server, give it the full paths to the existing `llama-server.exe` and local GGUF file. An already running process stays under its original owner. The llama.cpp embeddings profile is for a single server started in embedding mode.

Load the model in its provider or use the authorized **Load** action. Use **Refresh catalog** to observe external changes. Each model card shows its provider ID, optional alias, capability ratings, and relevant fallback limits. **Use locally** gives that deployment preference when otherwise comparable placements are available. To clear the preference, choose **Automatic placement** under **Local model preference** in Serving. `GET /v1/models` lists eligible provider IDs and any user-set aliases within the caller's scope. Deployments under one embedding alias need a common vector space. Use a deployment-specific alias or configure an owner-declared compatible family.

Give compatible deployments the same alias to serve requests on either host. Chat deployments under one alias can have different context and input capabilities. Mango uses each request's inputs and estimated context when selecting a host, checks for current work, and prefers a placement that can run without displacing another resident model. A request that needs the larger context or image input can wait on the host that supports it. The shared alias lists the largest observed context available to the caller.

Use the loopback host URL and current session bearer for the local API. The command `--url` returns the bearer as the `session` fragment. Keep it private. This PowerShell request invokes a chat alias.

```powershell
$session = [uri](dotnet .\src\Mango\bin\Release\net10.0-windows\Mango.dll --url)
$base = $session.GetLeftPart([System.UriPartial]::Authority)
$headers = @{ Authorization = 'Bearer ' + $session.Fragment.Substring(9) }
$body = @{
    model = '<your-model-alias>'
    messages = @(@{ role = 'user'; content = 'Say hello.' })
    max_tokens = 64
} | ConvertTo-Json -Depth 5
Invoke-RestMethod "$base/v1/chat/completions" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
```

For the already-loaded workflow, disable just-in-time model loading in LM Studio and automatic switching and downloading in Unsloth. Start `unsloth run` with `--disable-tools`. Mango accepts client-executed function tools on deployments that report support and disables Unsloth host-side tool execution in forwarded requests.

The inference surface includes chat completions, embeddings, images, audio transcription, speech, and System One decisions when the provider supports the protocol. Image generation returns inline base64 and transcription accepts multipart audio. The provider decides what it can execute. Mango schedules around provider slots, benchmarks, and queue priority. See the [provider qualification results](plan/02-provider-integration.md) for historically tested combinations.

### Serve a prepared Strata model

Prepare a model in Strata Desktop or Terminal first. In **Serving → Add provider**, choose **Strata** and connect. Mango looks for Strata Control in standard Windows install locations. For a portable or custom installation, enter the local Strata installation folder. If Strata keeps its run configurations elsewhere, enter the folder that contains the `strata-*.json` files. **Refresh catalog** picks up changes made in Strata.

Choose a prepared text chat model. Use **Served context** to configure a smaller native context on the managed runtime where needed. Mango preserves the prepared model and starts a private loopback runtime. It schedules one Strata request at a time. A sole-model load goes to Strata for its capacity decision.

A managed Strata runtime can occupy an exclusive provider slot, so another request may need to reroute or wait. Close separate conflicting Strata instances through their owning program. **Stop and release computer** also cancels a managed load in progress. Mango follows the [residency policy](requirements/03-resources-and-scheduling.md#model-residency-and-memory-fit) when a request requires a model switch.

### Enable local inference

1. Turn on **Contribute this computer** in **Serving**. The machine can still accept and route requests as an ingress while it is a leech.
2. Models are enabled by default except identified LM Link models, which you can enable on their cards. Use **Serve all models**, **Serve no models**, or **Disable serving** to set what Mango routes.
3. Load the model in its provider for an attached endpoint. Mango can load a managed deployment on demand when the provider exposes that operation. The hosting program accepts or refuses the load and subsequent inference.

Use the benchmark controls for native timing and memory measurements. **Never** skips automatic runs. **Run now** schedules measurement. **When convenient** runs after five locally idle minutes and yields to client work. Running and settling benchmarks can occupy the provider. Missing or failed measurements remain visible without blocking ordinary serving.

Use measured RAM and per-device VRAM requirements or local overrides to configure checked co-residency. The check applies before an additional non-YOLO load. If the request needs a swap, Mango tries an eligible alternate first. Otherwise it waits for conflicting requests to finish, verifies the necessary unloads, then loads and serves the requested model. See [YOLO](yolo.md) for group eviction and on-demand loading.

Sustained external CPU or GPU activity makes the node yield local serving, including YOLO. Mango's own inference and benchmarks are excluded from the trigger. The node remains an ingress and returns after the configured quiet cooldown. Missing attribution and memory pressure are diagnostic observations.

When Mango can confirm the loopback LM Studio server with the installed `lms` CLI, Serving shows separate cards for local and LM Link copies of the same model key. For a managed local copy with one exact indexed file, Mango lets LM Studio assign the instance name and verifies the device and file. When a linked copy uses the same name, Mango can serve the verified local copy if LM Studio reports matching instance settings. Use LM Studio's device selector to unload a shared name. Client-facing names come from the provider or an owner alias. LM Link execution belongs to the linked host.

## Set up a private Mango network

Install and sign in to Tailscale separately. Mango binds its peer and keyed inference listener to the Windows Tailscale adapter. The default peer port is `53781`. The browser settings and administrative API use the separate loopback listener.

Open **Network → Setup** in the local UI. On the first node, choose **Create network**, choose the local role and benchmark policy, and save the recovery record outside the network. On another node, choose **Find peers**, confirm the expected network ID, and join with the master password. A known Tailscale IP and optional port also work for manual enrollment. Create and Join reuse the password just entered to establish an ordinary ten-minute administration session, then open this computer in **Serving**. No second password entry is needed. If enrollment succeeds but unlock fails, the UI says so and offers the sidebar Unlock control without repeating enrollment. Mango reuses the enrolled node credential on subsequent launches; browser administration starts locked on a new page load. Use **Refresh network** to check the listener, reconcile state, or diagnose a failed connection.

The node view shows the last observed reachability, provider and model state, applied limits, and benchmark coverage. Unlock network administration to request a provider registration or edit an existing model or provider on any enrolled node. A saved request remains pending until the owning node reports applying it. For address changes that need new credentials or lifecycle permission, open **Serving** on the owning computer. The requested address and applied connection remain distinct; **Approve requested connection** grants the required local approval. Pending new connections also appear in the provider selector. Contribution, thresholds, reserves, benchmark policy, credentials, and process permissions are set on the owning machine.

Unlock network administration to change shared aliases and ratings or issue and revoke inference keys. Copy each newly issued key when it appears; closing its dialog clears it from the page. It can access the permitted `/v1` routes over Tailscale or on the local loopback address using `Authorization: Bearer <key>`. Requests can enter any enrolled node and use an eligible contributor. A known key can work from cached authority while that node is isolated. Issuing or revoking keys in an enrolled multi-node network requires another reachable member.

The serving node queues work when capacity is busy. Network administrators can change the five-minute maximum wait under **Private network → Inference queue**. The wait budget carries across retries. Mango returns HTTP 503 for unavailable capacity and 504 when the wait expires. Disconnecting cancels the request. Mango can retry a confirmed failure before output begins while the client is still connected.

Use the optional `Mango-Session` request header to identify a conversation. The value is scoped to the authenticated key and the requested model identifier or alias. Mango can also recognize related chat requests from their message prefixes. Cache affinity lasts five minutes after completion and depends on an eligible live model instance.

To rotate the master password, unlock administration and use **Change master password** in **Network → Recovery**. Existing nodes, inference keys, and the recovery record stay valid. Mango checks enrolled computers first and asks for confirmation if any are unreachable. Those nodes receive the new password verifier when they reconnect. Use **Recover a forgotten password** if you need to regain access with the recovery record. **Full network reset** removes keys and private request state and requires peers to reenroll. It preserves reusable model settings and benchmarks. A recovery record cannot reconstruct missing data.

**Live requests** shows ingress placement and retries, plus this host's loading, queue, and execution state. The open page subscribes to local changes. The view keeps a bounded recent history in memory.

## Optional public HTTPS inference

Public inference starts off. To enable it, open **Public HTTPS inference**, enable the local upstream, and choose a port. The default upstream is `http://127.0.0.1:53782`. Point an existing HTTPS reverse proxy at that upstream. Enable streaming and request bodies up to 64 MiB, and give the upstream time to cover the configured queue wait and model preparation. Forward the inference `Authorization` header. Only the dedicated upstream should be proxied.

For example, this location block can go inside an existing HTTPS server in NGINX. Use the port configured in Mango. `proxy_set_header` replaces the client-provided forwarding header. [NGINX HTTP proxy module](https://nginx.org/en/docs/http/ngx_http_proxy_module.html)

```nginx
location / {
    proxy_pass http://127.0.0.1:53782;
    proxy_http_version 1.1;
    proxy_set_header X-Forwarded-For $remote_addr;
    client_max_body_size 64m;
    proxy_request_buffering off;
    proxy_buffering off;
    proxy_read_timeout 360s;
}
```

Set **Trusted proxy IPv4 address** to the proxy's loopback source IP only if the proxy overwrites `X-Forwarded-For` with the observed client address. Leave the field blank when attribution is unavailable. Mango reports the proxy address as the observed source in that case.

On a tailnet already configured for Funnel, the owner can run `tailscale funnel 53782` in the foreground and use the reported HTTPS origin. Stop the foreground command to withdraw that exposure. Leave the trusted proxy field blank for this setup. [Tailscale Funnel prerequisites and limits](https://tailscale.com/docs/features/tailscale-funnel)

Use **Check HTTPS from this node** after entering the origin. It checks that the HTTPS endpoint reaches this upstream using the host's normal certificate trust. To verify internet reachability, test the endpoint from outside the tailnet. Public requests use `Authorization: Bearer <inference-key>` with the key's configured scope and priority.

The public upstream serves these methods and paths.

- `GET /v1/models`
- `POST /v1/chat/completions`
- `POST /v1/embeddings`
- `POST /v1/images/generations`
- `POST /v1/audio/transcriptions`
- `POST /v1/audio/speech`
- `POST /v1/systemone`

## Logs and retention

Open **Logs and retention** to view the available history for a day, filter by key name or source address, and see original requests separately from serving attempts. **Collect from network** refreshes coverage and retrieves new records. The lowest reachable enrolled node collects at the end of the day in the shared IANA timezone. The view remains available with partial results while collection runs or a peer is offline.

Open **System → Logging policy** and unlock network administration to edit the shared policy. Preferences update automatically. Sparse is the default and keeps metadata. Off stops recording new inference requests. Verbose also retains validated request bodies, and Everything also retains delivered response bodies. The chosen mode applies when the request starts. The independent **Retain operational events** setting covers events such as resource withdrawal and provider lifecycle failures. The default limits are seven days of raw records, thirty days of aggregate metadata, and 1 GiB of log storage per node. The oldest eligible files are evicted first when the cap shortens retention.

New networks use the first host's IANA timezone. Existing networks with no saved logging policy use UTC until a network administrator sets the timezone.

**Download day** exports a ZIP with `records.ndjson`, retained body files, and a coverage manifest. Check the manifest for unavailable peers, expired or incomplete bodies, and storage limits. Transport credentials and known credential-bearing fields are excluded. Model content can still contain private information. Coverage can improve on a later collection when peers return. If a collector is lost, its aggregate may be recoverable from original records still within retention.

**Clear logs** advances a network deletion generation. Reachable nodes clear their Mango-managed records and payloads, and offline nodes remain pending until they apply the deletion on reconnect or are removed. A full network reset also clears logs. Password recovery preserves them. Exported ZIP files are outside Mango's managed storage.


## Serving readiness and measurements

**Serving** brings together provider connection, model configuration, local lifecycle actions, contribution, YOLO, and measurements. Remote readiness shows its observation time. Provider credentials, lifecycle permissions, and co-residency choices are controlled on the owning machine. Local deployments appear before LM Link models.

Readiness reflects contribution and cooldown, provider reachability and compatibility, actual slots, benchmark or lifecycle activity, and any applicable additional-load check. A model with authorized on-demand loading can be a candidate while unloaded.

Local RAM/VRAM margins apply to checked additional loads. Blank margins restore defaults and a below-default margin needs a one-time local acknowledgement. Use comparable measurements or enter an explicit owner override. Recheck evidence after changing provider or device configuration. **Serving → Aliases** shows the deployments affected by a capability edit.

**Activity → Live** shows current and recent work. **History** has filters, 50-row pages, collection coverage, and export. Logging policy lives in **System**; retained-data clearing lives in **System → Data**. **Network** separates computers, queue, inference keys, public access, recovery, and setup. Creating a key does not enable public access. Recovery and full reset remain separate named operations.
