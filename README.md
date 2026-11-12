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

Phase 1 provides local settings, including activity thresholds and memory reserve consent. Contribution is a saved preference at this stage. Provider and network setup arrive in later phases.

Runtime state, credentials, logs, models, and generated application builds do not belong in Git. No remote repository or hosted service is required by this plan.
