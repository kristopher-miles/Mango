# Use Mango on Windows

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

The inference surface includes chat completions, embeddings, images, audio transcriptions, speech, and System One decisions when an eligible deployment advertises the protocol. Image generation returns inline base64. Transcriptions accept a multipart audio file. Local policy, qualified limits, and provider slots determine which executions can overlap. See the [provider plan and qualification results](plan/02-provider-integration.md) for tested versions and runtime limits.

### Enable local inference

1. In **Contribution**, select **Contributor** and save. Choose **Never** to use estimated or owner-configured limits without automatic benchmarks, **Now** to finish benchmarks before serving, or **When convenient** to benchmark after five minutes without client work. You can also choose the local benchmark policy when creating or joining a network.
2. Models are enabled for serving by default except identified LM Link models, which you can enable on their cards. Use **Serve all models** or **Serve no models** to update all currently listed models on the selected provider, including models in other categories. Use **Disable serving** on a single card to stop routing new work to it. Mango estimates memory limits and allows 16 embedding inputs, four System One items, or one item for other operations by default. Select **View limits** to override the estimates. To use an attached provider, confirm that its model may remain resident after Mango withdraws.
3. On the same **Serving** screen, load the deployment if its provider requires a manual load. Mango can load eligible managed deployments on demand. With **Now**, the resource panel shows benchmark progress or the reason it is waiting. Mango withdraws and verifies release of managed resources when Windows reports pressure or sustained external activity. See the [Windows qualification and limits](plan/04-resource-protection.md#windows-qualification).

Only sustained measured external CPU or GPU use starts the user-activity cooldown. Mango's own requests, model switches, memory pressure and missing observations do not start that timer. Known memory limits, native busy or unsettled work, failed release and manual contribution settings still control readiness. Missing telemetry produces a warning and is reconsidered on the next report.

When Mango can confirm the loopback LM Studio server with the installed `lms` CLI, Serving shows separate cards for local and LM Link copies of the same model key. Mango routes to uniquely identified loaded instances. Choose the desired device in LM Studio for a shared model key that needs loading. LM Link execution runs on the linked device, while Mango's local limits cover the relay on this computer.

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


## Serving readiness and qualification

**Serving** keeps provider connection, model configuration, local lifecycle actions, contribution, benchmark policy, and qualification together. The computer selector changes the owning host; remote credentials, process permission, and resource limits cannot be edited here. Remote readiness is labeled with its observation time, rather than presented as a live measurement. **All** shows five models per page. Each category shows its whole list on one page. Models on the provider's own device appear before LM Link models.

Readiness comes from the same host facts used by admission: contribution/protection state, provider reachability and supported operations, context, local budgets and attached-provider permission, benchmark policy, live reservations, pressure, and allocation failures. An unloaded model with supported on-demand loading is not blocked merely because it is unloaded. Unrated capabilities do not imply unavailable inference.

Blank memory reserves restore platform defaults. Reducing a reserve below its default requires the explicit acknowledgement on **Protection → Memory**. Qualification budgets need every required workload, RAM, and per-device VRAM value before they take effect. A changed deployment or hardware configuration requires review; old limits are not silently reapplied. **Serving → Aliases** shows how many deployments a capability edit affects and displays Mixed when their ratings differ.

**Activity → Live** shows current and recent work. **History** has filters, 50-row pages, collection coverage, and export. Logging policy lives in **System**; retained-data clearing lives in **System → Data**. **Network** separates computers, queue, inference keys, public access, recovery, and setup. Creating a key does not enable public access. Recovery and full reset remain separate named operations.
