# Publish Mango for Windows

Use PowerShell 7, Git, and the .NET SDK pinned in `global.json`. Push the reviewed commit to `main` first. The script builds that commit, even when the checkout has local edits. Publishing needs a GitHub credential with write access to Mango (from Git Credential Manager, `GH_TOKEN`, or `GITHUB_TOKEN`).

```powershell
./scripts/release-windows.ps1             # v0.0.1, then v0.0.2, etc.
./scripts/release-windows.ps1 -BuildOnly  # build to dist without publishing
./scripts/release-windows.ps1 -Bump Minor # explicit: e.g. v0.1.0
./scripts/release-windows.ps1 -Bump Major # explicit: e.g. v1.0.0
```

Run one release at a time. GitHub receives a draft, the Windows ZIP, and its SHA-256 file. The script publishes only after GitHub confirms the upload. If an upload fails, check the draft on GitHub before retrying. Use `-Dotnet <full path>` if the SDK isn't in `PATH` and `-Notes <text>` for release notes.
