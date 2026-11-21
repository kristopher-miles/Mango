# Install Mango on Windows

Mango ships as a Windows x64 ZIP with the .NET 10.0.15 runtime and browser UI. Windows 11 Pro x64 was used for release qualification.

## Launch

Extract `Mango-win-x64.zip` into a new folder you own, such as `%LOCALAPPDATA%\Programs\Mango`. Run `Mango.exe` to open the local browser UI and notification-area icon. Closing the browser leaves the host running. Use the tray menu to reopen the UI, change the local contribution preference, or quit.

To launch without a tray icon, use `Start-Process .\Mango.exe -ArgumentList '--headless'` from the extracted folder. In PowerShell, pipe console output from the Windows executable when asking for the local session URL or stopping the process.

```powershell
$session = .\Mango.exe --url | Select-Object -First 1
.\Mango.exe --quit | Out-Null
```

The session URL contains a temporary bearer in its fragment. Keep it private. The default installation uses `http://127.0.0.1:53781` for the browser and `http://127.0.0.1:53781/v1` for local inference with a scoped key. `--background` starts the tray host without opening the browser. **System → Startup** registers the executable for the current Windows user and uses the saved contribution and resource settings. Isolated runs can supply `--data-dir <private directory>` with each command. Startup registration uses `%LOCALAPPDATA%\Mango`.

## Join and serve

1. Install Tailscale on each participating Windows host and sign in to the same tailnet. Verify the address under **Network → Computers**. Mango uses tailnet port `53781` by default. [Tailscale Windows installation](https://tailscale.com/docs/install/windows)
2. Create a network on the first node and keep its recovery record privately. On another node, discover the first peer or enter its address and exact network ID. Join with the master password. Both Create and Join use the password just entered to unlock administration for ten minutes and open **Serving**. The sidebar always shows lock state and expiry. An enrolled node reuses its node credential after restart.
3. In **Serving**, choose the profile for your existing LM Studio, Unsloth Studio, Unsloth run, llama.cpp server, or Generic endpoint. Enter the server origin and any provider bearer, then connect. Attached mode uses the provider without granting Mango lifecycle authority. Managed mode enables the supported load and unload operations. Launching a managed llama-server also needs its local executable and GGUF paths. Keep LM Studio automatic model switching disabled for already-loaded workflows and use `unsloth run --disable-tools` for Unsloth run.
4. Configure the model and enable **Contribute this computer**. The provider owns sole-model startup and inference capacity. If you configure several models to be resident together, use matching RAM/VRAM measurements or local overrides for checked loads, or [enable YOLO](yolo.md) for the models that may share native capacity. Older releases can still show the previous qualification prerequisites. See the [serving boundary](requirements/09-recorded-decisions.md#serving-boundary--november-21-2026) for the policy pending rollout.
5. Open **Network → Keys** to issue a scoped inference key; use the sidebar's Unlock control if the administration session has expired. Use `Authorization: Bearer <inference-key>` on the allowed `/v1` endpoints. Public HTTPS is opt-in. To enable it, point an HTTPS reverse proxy at the dedicated loopback upstream shown under **Network → Public access**, with request and response streaming enabled. Configure a trusted proxy address only if the proxy replaces client-supplied forwarding headers with the observed client address. Test the configured HTTPS origin in Mango and verify external reachability from outside the tailnet. [Tailscale Funnel option and prerequisites](https://tailscale.com/docs/features/tailscale-funnel)

For Strata Desktop or Terminal, prepare a model before connecting. Select **Strata** in Serving to let Mango launch the private runtime. [Strata setup and resource limits](usage.md#serve-a-prepared-strata-model)

Password recovery uses the recovery record to replace the master password while preserving inference keys and logs. Full network reset invalidates keys, clears managed log history and requires peers to reenroll. It preserves reusable model settings and benchmarks.

## Replace or remove the package

If you installed v0.0.1, replace it manually once to get the updater. Quit Mango and extract the new ZIP into a fresh folder. Reenable **Start Mango when I sign in** from the new executable if its path changed. Keep the application in a dedicated, writable folder on a local drive, and keep its data directory separate.

In **System → Updates**, enable **Check for safe updates daily**. Mango checks GitHub while running. It verifies Mango's signature and the ZIP, then installs a newer release in the same communications protocol window after active inference finishes. A protocol cutoff requires a manual upgrade. Use **Check for updates** to check immediately. **Defer future maintenance on this host** pauses automatic installs.

**Queue update on peers** asks other nodes to verify the same release independently. Only nodes that enabled automatic updates can queue it. Mango staggers the restart windows. If a peer cannot verify eligibility or is unreachable, the sender must explicitly choose to proceed with the verified peers. An offline node can check for itself when it is running again.

Mango keeps the previous program folder next to the updated installation. If the replacement cannot start, Mango attempts to restore the previous folder and records the result in `update-result.json` in the data directory. Keep a backup of the data directory before manually crossing a cutoff. Restore an older binary only when it can read the saved data.

To uninstall, disable **Start Mango when I sign in**, quit Mango, and remove the extracted program folder. Mango settings and logs live in the separate data directory. Use **Clear logs** or **Full network reset** for their documented effects. Provider applications and weights live in their own locations.
