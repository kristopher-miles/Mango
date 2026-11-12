# Mango

Mango is designed to route inference requests among existing model servers over a private Tailscale network. Windows is the first peer platform.

Start with the [eight-phase implementation plan](docs/plan/00-shared-foundation.md). Each phase has its own document; the foundation contains the shared runtime and ownership contracts. The [requirements index](docs/requirements/README.md) and [recorded decisions](docs/requirements/09-recorded-decisions.md) preserve the product baseline and all 29 questionnaire responses.

The [original HTML questionnaire](docs/requirements/mango-questionnaire.html) and [submitted JSON](docs/requirements/decisions/2026-11-11-answers.json) are retained against their original baseline for reference and import compatibility.

## Install the Windows package

Build `dist/Mango-win-x64.zip` from PowerShell 7 using the SDK pinned in `global.json`. The ZIP includes the Windows x64 runtime, static UI, and installation guide. [Windows install and removal](docs/windows-install.md)

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

Settings live in `%LOCALAPPDATA%\Mango`. For isolated runs, pass `--data-dir <private directory>` to each command. Windows startup registration uses the default directory. The GUI uses an available IPv4 loopback port by default. Use `--port <port>` to request a specific port when launching the host.

If the primary settings file is unreadable, Mango reports recovery from the last good snapshot in the browser. A subsequent settings change retains the unreadable file as `local-settings.json.invalid-*`. If neither copy is usable, check `startup-error.txt` in the data directory. This diagnostic also reports startup binding failures.

## Connect a local model server

Open **Providers and models**, choose the profile for an existing LM Studio, Unsloth Studio, Unsloth run, or llama.cpp server, then save its address and bearer token. A blank address uses the profile's loopback default. HTTP is available on loopback. An explicitly configured non-loopback provider uses HTTPS. Mango protects saved provider tokens with Windows DPAPI for the current user.

**Attached** mode lets Mango inspect the provider and send inference requests. **Managed** mode authorizes the provider's supported model load and unload operations. To let Mango launch and stop a single-model llama-server, give it the full paths to the existing `llama-server.exe` and local GGUF file. An already running process stays under its original owner. The llama.cpp embeddings profile is for a single server started in embedding mode.

Load the model in its provider or use the authorized **Load model** action. Use **Refresh catalog** to observe external changes. In a deployment's details, set its alias, capability ratings, and any missing served limits. **Use for local inference** gives that deployment preference when otherwise comparable placements are available. `GET /v1/models` lists eligible aliases within the caller's scope. Deployments under one embedding alias need a common vector space. Use a deployment-specific alias or configure an owner-declared compatible family.

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

The inference surface includes chat completions, embeddings, images, audio transcriptions, speech, and System One decisions when an eligible deployment advertises the protocol. Image generation returns inline base64. Transcriptions accept a multipart audio file. Local policy, qualified limits, and provider slots determine which executions can overlap. See the [provider plan and qualification results](docs/plan/02-provider-integration.md) for tested versions and runtime limits.

### Enable local inference

1. In **Contribution**, select **Contributor** and save. Choose **Never** for owner-configured limits, **Now** to finish benchmarks before serving, or **When convenient** to benchmark after five minutes without client work. You can also choose the local benchmark policy when creating or joining a network.
2. In **Resource protection**, select the deployment and operation and save the workload and incremental RAM limits. On hosts with discrete GPUs, enter the load and execution VRAM allowance for each GPU. To use an attached provider, confirm that its model may remain resident after Mango withdraws.
3. In **Providers and models**, load the deployment if its provider requires a manual load. Mango can load eligible managed deployments on demand. With **Now**, the resource panel shows benchmark progress or the reason it is waiting. Mango withdraws and verifies release of managed resources when Windows reports pressure or sustained external activity. See the [Windows qualification and limits](docs/plan/04-resource-protection.md#windows-qualification).

## Set up a private Mango network

Install and sign in to Tailscale separately. Mango binds its peer and keyed inference listener to the Windows Tailscale adapter. The default peer port is `53781`. The browser settings and administrative API use the separate loopback listener.

Open **Private network** in the local UI. On the first node, open **Create a separate network**, choose the local role and benchmark policy, and save the recovery record outside the network. On another node, choose **Find peers**, confirm the expected network ID, and join with the master password. A known Tailscale IP and optional port also work for manual enrollment. Mango reuses the enrolled node credential on subsequent launches. Use **Refresh network** to check the listener, reconcile state, or diagnose a failed connection.

The node view shows the last observed reachability, provider and model state, applied limits, and benchmark coverage. Unlock network administration to request a provider registration or edit an existing model or provider on any enrolled node. A saved request remains pending until the owning node reports applying it. For address changes that need new credentials or lifecycle permission, use **Review on this host** in the owning node's GUI. Contribution, thresholds, reserves, benchmark policy, credentials, and process permissions are set on the owning machine.

Unlock network administration to change shared aliases and ratings or issue and revoke inference keys. Save each newly issued key when it appears. It can access the permitted `/v1` routes over Tailscale or on the local loopback address using `Authorization: Bearer <key>`. Requests can enter any enrolled node and use an eligible contributor. A known key can work from cached authority while that node is isolated. Issuing or revoking keys in an enrolled multi-node network requires another reachable member.

The serving node queues work when capacity is busy. Network administrators can change the five-minute maximum wait under **Private network → Inference queue**. The wait budget carries across retries. Mango returns HTTP 503 for unavailable capacity and 504 when the wait expires. Disconnecting cancels the request. Mango can retry a confirmed failure before output begins while the client is still connected.

Use the optional `Mango-Session` request header to identify a conversation. The value is scoped to the authenticated key and model alias. Mango can also recognize related chat requests from their message prefixes. Cache affinity lasts five minutes after completion and depends on an eligible live model instance.

Use **Recover a forgotten password** to replace the master password and keep existing inference keys. **Full network reset** is a separate, confirmed action that removes keys and private request state and requires peers to reenroll. It preserves reusable model settings and benchmarks. A recovery record cannot reconstruct missing data.

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

Unlock network administration to set the shared policy. Sparse is the default and keeps metadata. Off stops recording new inference requests. Verbose also retains validated request bodies, and Everything also retains delivered response bodies. The chosen mode applies when the request starts. The independent **Retain operational events** setting covers events such as resource withdrawal and provider lifecycle failures. The default limits are seven days of raw records, thirty days of aggregate metadata, and 1 GiB of log storage per node. The oldest eligible files are evicted first when the cap shortens retention.

New networks use the first host's IANA timezone. Existing networks with no saved logging policy use UTC until a network administrator sets the timezone.

**Download day** exports a ZIP with `records.ndjson`, retained body files, and a coverage manifest. Check the manifest for unavailable peers, expired or incomplete bodies, and storage limits. Transport credentials and known credential-bearing fields are excluded. Model content can still contain private information. Coverage can improve on a later collection when peers return. If a collector is lost, its aggregate may be recoverable from original records still within retention.

**Clear logs** advances a network deletion generation. Reachable nodes clear their Mango-managed records and payloads, and offline nodes remain pending until they apply the deletion on reconnect or are removed. A full network reset also clears logs. Password recovery preserves them. Exported ZIP files are outside Mango's managed storage.

Runtime state, credentials, logs, models, and generated application builds do not belong in Git. No remote repository or hosted service is required by this plan.
