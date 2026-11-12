# Phase 1 Windows host and local settings

Deliver a small Windows background application with a local browser UI, tray entry, and reliable settings persistence. This establishes the owner process for later features, without pretending it already serves inference.

Read [Shared foundation](00-shared-foundation.md), PRODUCT-03/06, SCOPE-04, NFR-02/04/05, and UI-02/07/08. No previous phase is required.

## Work

1. Create the .NET application and test project, using the framework HTTP server and static assets. Keep application dependencies limited to functionality the framework does not provide. Implement one process instance per local Mango data directory; another launch opens the existing UI rather than competing for its files/ports.
2. Add startup/shutdown and the local state directory. Load local configuration, validate recognizable fields and ranges, and preserve the last-good snapshot on an interrupted write. Refuse to overwrite an unreadable state file with defaults silently. Create defaults only on genuine first run.
3. Add the loopback-only GUI endpoint and tray actions: open Mango, choose contributor/leech preference, and quit. The contributor preference is initially saved intent, not a claim that resources are being served. Keep browser-origin checks and local session authentication in the normal HTTP handling; an unrelated website must not become the administrator merely by reaching localhost.
4. Add a minimal responsive page showing setup state, local role preference, data location, and clear not-yet-configured provider/network status. Use ordinary labels and keyboard controls. The UI remains usable while background operations run; later phases extend the same page and endpoints rather than replacing a prototype UI.
5. Add local settings for activity monitoring, sampling interval, cutoff percentages, cooldown, RAM/VRAM reserves, and benchmark policy. Store defaults once. Enforce the two-second minimum sampling interval, five-minute default quiet cooldown, host RAM reserve clamp of 15 percent between 2 and 5 GiB, and flat 1 GiB VRAM reserve. Below-default reserves, including zero, require the one-time local warning/consent record. These controls are inert until phase 4 activates them and must say so.
6. Add a headless launch option and optional start-at-login registration without requiring an elevated Windows service. Keep the tray on its Windows message loop and background I/O asynchronous. Closing the browser does not quit Mango; explicit Quit does.

## Interfaces handed forward

Provide a local-settings record, atomic snapshot writer, cancellation on application shutdown, and the authenticated local UI session. Keep shared network configuration separate from local settings even before networking exists. Later phases add concrete records; do not create an abstract configuration platform.

Bind only loopback at this phase. Provider routes appear in phase 2 and the Tailscale peer listener in phase 3. Report binding conflicts in the UI/startup diagnostic, rather than silently opening public interfaces or killing another application.

## Minimum checks

- Restart after changing settings and after an interrupted snapshot replacement; the last complete settings survive. An invalid file is reported and retained for recovery.
- Launch twice, close the browser, and exit through the tray: observe one host process and the intended lifecycle.
- Check that an unrelated origin cannot change settings, ordinary keyboard controls work, and the headless launch does not need a desktop window.
- Measure initial idle service cost once to catch a bad foundation early. No provider, telemetry, or network workload is required yet.

The Release build and host checks exercise headless startup, duplicate launches, the local session boundary, memory reserve consent, binding conflicts, and recovery from interrupted and invalid snapshots. On the Windows development machine, a second desktop launch opened the running host in Edge, arrow keys and Tab moved through the controls, and Enter saved the role preference. Closing Edge left the host running. The tray host used 74.3 MiB of working set and averaged 0.10% of one CPU core over a 15-second initial idle sample with the browser closed.

The same-user `--quit` command stopped the tray host and Windows message loop. Direct selection of the native tray menu item and an actual Windows sign-in with startup registration enabled remain unverified.

## Done

A Windows user can launch the app, reopen its UI, save local settings, and stop it cleanly. The checkout contains source and reproducible build instructions; runtime configuration and credentials live outside Git. No inference provider installation, network wizard, generic command framework, native mobile interface, or auto-updater is part of this phase.
