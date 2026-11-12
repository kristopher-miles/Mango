# Mango

Mango is designed to route inference requests among existing model servers over a private Tailscale network. Windows is the first peer platform.

Start with the [eight-phase implementation plan](docs/plan/00-shared-foundation.md). Each phase has its own document; the foundation contains the shared runtime and ownership contracts. The [requirements index](docs/requirements/README.md) and [recorded decisions](docs/requirements/09-recorded-decisions.md) preserve the product baseline and all 29 questionnaire responses.

The [original HTML questionnaire](docs/requirements/mango-questionnaire.html) and [submitted JSON](docs/requirements/decisions/2026-11-11-answers.json) are retained against their original baseline for reference and import compatibility.

## Run the Windows host

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

Load the model in its provider or use the authorized **Load model** action. Use **Refresh catalog** to observe external changes. In a deployment's details, set its alias, capability ratings, and any missing served limits. Select **Use for local inference** on the loaded deployment. The local list at `GET /v1/models` shows that selected alias once it is eligible. Embedding clients can pin the deployment, or use deployments in an owner-declared compatible family. Equal vector dimensions alone do not guarantee a shared vector space.

Use the loopback host URL and current session bearer for the local API. The command `--url` returns the bearer as the `session` fragment. Keep it private. For example, PowerShell can invoke the selected chat model as follows.

```powershell
$session = [uri](dotnet .\src\Mango\bin\Release\net10.0-windows\Mango.dll --url)
$base = $session.GetLeftPart([System.UriPartial]::Authority)
$headers = @{ Authorization = 'Bearer ' + $session.Fragment.Substring(9) }
$body = @{
    model = '<your-selected-alias>'
    messages = @(@{ role = 'user'; content = 'Say hello.' })
    max_tokens = 64
} | ConvertTo-Json -Depth 5
Invoke-RestMethod "$base/v1/chat/completions" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
```

For the already-loaded workflow, disable just-in-time model loading in LM Studio and automatic switching and downloading in Unsloth. Start `unsloth run` with `--disable-tools`. Mango accepts client-executed function tools on deployments that report support and disables Unsloth host-side tool execution in forwarded requests.

The local inference surface includes chat completions, embeddings, images, audio transcriptions, speech, and System One decisions when the selected deployment advertises that protocol. Image generation returns inline base64. Transcriptions accept a multipart audio file. The current implementation limits local inference to one request at a time and checks provider state after an interrupted response. See the [provider plan and qualification results](docs/plan/02-provider-integration.md) for tested versions and runtime limits.

Network routing and automatic resource protection arrive in later phases. The contributor preference remains local saved intent.

## Set up a private Mango network

Install and sign in to Tailscale separately. Mango binds its peer and keyed inference listener to the Windows Tailscale adapter. The default peer port is `53781`. The browser settings and administrative API use the separate loopback listener.

Open **Private network** in the local UI. On the first node, choose **Create network** and save the recovery record outside the network. On another node, choose **Find peers**, confirm the expected network ID, and join with the master password. A known Tailscale IP and optional port also work for manual enrollment. Mango reuses the enrolled node credential on subsequent launches. Use **Refresh network** to check the listener, reconcile state, or diagnose a failed connection.

Unlock network administration to change shared aliases and ratings or issue and revoke inference keys. Save each newly issued key when it appears. It can access the permitted `/v1` routes over Tailscale or on the local loopback address using `Authorization: Bearer <key>`. Inference uses the ingress node's selected local deployment in this phase. A known key can work from cached authority while that node is isolated. Issuing or revoking keys in an enrolled multi-node network requires another reachable member.

Use **Recover a forgotten password** to replace the master password and keep existing inference keys. **Full network reset** is a separate, confirmed action that removes keys and private request state and requires peers to reenroll. It preserves reusable model settings and benchmarks. A recovery record cannot reconstruct missing data.

Runtime state, credentials, logs, models, and generated application builds do not belong in Git. No remote repository or hosted service is required by this plan.
