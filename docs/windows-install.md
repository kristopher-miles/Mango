# Install Mango on Windows

Mango ships as a Windows x64 ZIP with the .NET 10.0.15 runtime and browser UI. Windows 11 Pro x64 was used for release qualification.

## Launch

Extract `Mango-win-x64.zip` into a new folder you own, such as `%LOCALAPPDATA%\Programs\Mango`. Run `Mango.exe` to open the local browser UI and notification-area icon. Closing the browser leaves the host running. Use the tray menu to reopen the UI, change the local contribution preference, or quit.

To launch without a tray icon, use `Start-Process .\Mango.exe -ArgumentList '--headless'` from the extracted folder. In PowerShell, pipe console output from the Windows executable when asking for the local session URL or stopping the process.

```powershell
$session = .\Mango.exe --url | Select-Object -First 1
.\Mango.exe --quit | Out-Null
```

The session URL contains a temporary bearer in its fragment. Keep it private. `--background` starts the tray host without opening the browser. The **Start at login** setting registers the executable for the current Windows user and uses the saved contribution and resource settings. Isolated runs can supply `--data-dir <private directory>` with each command. Start at login uses `%LOCALAPPDATA%\Mango`.

## Join and serve

1. Install Tailscale on each participating Windows host and sign in to the same tailnet. Verify the address under **Private network**. Mango uses tailnet port `53781` by default. [Tailscale Windows installation](https://tailscale.com/docs/install/windows)
2. Create a network on the first node and save its recovery record privately. On another node, discover the first peer or enter its address and exact network ID. Join with the master password. An enrolled node reuses its node credential after restart.
3. In **Providers and models**, choose the profile for your existing LM Studio, Unsloth Studio, Unsloth run, or llama.cpp server. Enter the server origin and any provider bearer. Attached mode uses the provider without granting Mango lifecycle authority. Managed mode enables the supported load and unload operations. Launching a managed llama-server also needs its local executable and GGUF paths. Keep LM Studio automatic model switching disabled for already-loaded workflows and use `unsloth run --disable-tools` for Unsloth run.
4. To serve inference, choose **Contributor**. Choose **Never** to use owner-configured resource limits, **Now** to benchmark before serving, or **When convenient** for benchmarks after five minutes without client work. Set resource limits under **Resource protection** and explicitly allow an attached deployment to remain resident after Mango withdraws if you choose that mode.
5. Unlock network administration to issue a scoped inference key. Use `Authorization: Bearer <inference-key>` on the allowed `/v1` endpoints. Public HTTPS is opt-in. To enable it, point an HTTPS reverse proxy at the dedicated loopback upstream shown under **Public HTTPS inference**, with request and response streaming enabled. Configure a trusted proxy address only if the proxy replaces client-supplied forwarding headers with the observed client address. Test the configured HTTPS origin in Mango and verify external reachability from outside the tailnet. [Tailscale Funnel option and prerequisites](https://tailscale.com/docs/features/tailscale-funnel)

Password recovery uses the recovery record to replace the master password while preserving inference keys and logs. Full network reset invalidates keys, clears managed log history and requires peers to reenroll. It preserves reusable model settings and benchmarks.

## Replace or remove the package

Automatic updates are unavailable. To replace Mango, quit it and extract the new package into a fresh folder. Reenable **Start at login** from the new executable if its path changed. Back up the Mango data directory while the host is stopped before changing releases. A prior binary can be restored only when it supports the saved data format. Use the saved pre-upgrade data if a newer version changed it incompatibly.

To uninstall, disable **Start at login**, quit Mango, and remove the extracted program folder. Mango settings and logs live in the separate data directory. Use **Clear logs** or **Full network reset** for their documented effects. Provider applications and weights live in their own locations.
