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
2. On an existing Mango node that supports pairing, open **Network → Computers**, unlock administration, and select **Pair a computer** in the panel header.
3. With Mango stopped on Linux, run the following under the account that will run the service. The issuer must remain reachable over Tailscale. A code for the default IPv4 peer port is 40 characters and expires after one use or one hour. Codes for custom ports or IPv6 carry a few extra characters.

```sh
./Mango --data-dir "$HOME/.local/share/Mango" --pair 'PASTE_CODE_HERE' --name mini-server
./Mango --headless --data-dir "$HOME/.local/share/Mango" --port 53781
```

Older peers can enroll a Linux node with `--join 100.64.0.10:53781 NETWORK_GUID` in place of `--pair`. This prompts for the master password or accepts one line on stdin. The peer's `/peer/info` endpoint reports its `identity.id`.

For a custom Tailscale listener port, pass the same `--peer-port` to both commands. Restarting with the same data directory preserves membership. Run `./Mango --data-dir "$HOME/.local/share/Mango" --url` as the service account to open the authenticated local interface. Treat that URL as a local session credential.

Put the second command in your service manager's launch configuration and use the same account, data directory, and PATH on every restart. Start it after Tailscale. Keep the data directory at mode `0700`. It contains the local membership credential, so protect its backups as well. The [admin CLI](admin-cli.md) can manage the leech node and its HTTPS inference upstream from another computer on the tailnet.

## Managed systemd updates

Create a private install directory writable by the Mango service account and a separate private data directory. Extract the initial release under a name with the pattern `release-VERSION-32_LOWERCASE_HEX_DIGITS`, make its `Mango` executable, and write that directory's basename to the install directory's `current` file. For example, the contents of `/var/lib/mango/install/current` could be `release-0.0.18-3fb62b781eb245beaabd6de97a85a918`. Put `run-linux-managed.sh` at a stable path outside the selected release.

An example systemd service for the `mango` account:

```ini
[Unit]
Description=Mango leech
After=network-online.target tailscaled.service
Wants=network-online.target

[Service]
User=mango
Group=mango
ExecStart=/bin/bash /opt/mango/run-linux-managed.sh /var/lib/mango/install /var/lib/mango/data --port 53781 --peer-port 53781
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
```

Use the same data directory you enrolled. The launcher needs systemd to start it after a clean update handoff. Once running, check `updates status` and opt in with `updates enable` using the [admin CLI](admin-cli.md#updates). [systemd.service reference](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html).

Run the Linux smoke check with `bash tests/linux-leech-smoke.sh out/linux-x64/Mango`.
