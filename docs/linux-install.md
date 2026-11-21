# Linux leech node

Mango on Linux routes inference to contributors on your Mango network. It runs in the foreground by default for use with a service manager. The local web interface binds to loopback, and the peer listener binds to the active Tailscale interface. This build supports the standard Linux Tailscale interface `tailscale0` and uses the `tailscale` command on PATH.

## Build

From the repository root with the SDK in `global.json`:

```sh
dotnet publish src/Mango/Mango.csproj -p:MangoLinux=true -c Release -r linux-x64 --self-contained -o out/linux-x64
```

Copy the entire output directory to the Linux host and run `chmod +x Mango` if the transfer dropped the executable bit. Use `linux-arm64` for a glibc-based ARM64 target. Self-contained builds include the .NET runtime.

## Join an existing network

1. Connect the host to the same Tailscale network as an existing Mango peer.
2. Get the peer's numeric Tailscale address and the expected Mango network ID from the existing installation. The peer's `/peer/info` endpoint reports its `identity.id` and uses port `53781` by default.
3. With Mango stopped on Linux, run the following under the account that will run the service. Enter the Mango master password at the hidden prompt. Automation can supply a single line on stdin from a secret manager.

```sh
./Mango --data-dir "$HOME/.local/share/Mango" --join 100.64.0.10:53781 NETWORK_GUID --name mini-server
./Mango --headless --data-dir "$HOME/.local/share/Mango" --port 53781
```

For a custom Tailscale listener port, pass the same `--peer-port` to both commands. Restarting with the same data directory preserves membership. Run `./Mango --data-dir "$HOME/.local/share/Mango" --url` as the service account to open the authenticated local interface. Treat that URL as a local session credential.

Put the second command in your service manager's launch configuration and use the same account, data directory, and PATH on every restart. Start it after Tailscale. Keep the data directory at mode `0700`. It contains the local membership credential, so protect its backups as well. Local providers, contributor mode, public ingress, and automatic package updates are Windows features for this build.

Run the Linux smoke check with `bash tests/linux-leech-smoke.sh out/linux-x64/Mango`.
