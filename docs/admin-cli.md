# Manage a Mango node from the terminal

Use `Mango admin` on Linux or `Mango.Cli.exe admin` from the Windows package. `Mango admin --help` lists the commands. You can run the CLI from any computer on the same tailnet with permission to reach the target's Mango port. The target must be enrolled and running a Mango build that supports the CLI.

For an Apple silicon CLI build, run `dotnet publish src/Mango/Mango.csproj -p:MangoLinux=true -c Release -r osx-arm64 --self-contained -o out/osx-arm64` and use `Mango admin --target ADDRESS`. Install and connect the [Tailscale macOS client](https://tailscale.com/docs/reference/tailscale-cli?tab=macos).

## Log in and hand off access

```sh
Mango admin --target 100.64.0.10 login
Mango admin --target 100.64.0.10 status
Mango admin --target 100.64.0.10 access issue
```

`login` reads the regular Mango master password from a hidden prompt or one line of stdin. `access issue` returns a 22-character code and its ID. Give the recipient the target address and the code. The recipient runs:

```sh
Mango admin --target 100.64.0.10 login --code
Mango admin --target 100.64.0.10 status
Mango admin --target 100.64.0.10 logout
```

The code signs the recipient into that node with the same Mango permissions as a password login. The node accepts it once, and it expires in ten minutes by default. An optional number of minutes from 1 to 60 follows `access issue`. Both login methods open a ten-minute session. `access list` shows code IDs and whether they were redeemed. `access revoke ID` cancels the code and the session it opened. Losing or restarting the issuer clears the codes and sessions. Operations such as removing a peer, changing the network password, or sharing updates still affect the Mango network.

On the target, run the CLI as the service account and use `--data-dir PATH` in place of `--target` to connect through the local control socket. A new local installation can use `network create --stdin` with a JSON object containing `name` and `password`. The local `network recover --stdin` command takes `recoveryRecord` and `newPassword`. Use the normal `--pair` launcher option to enroll a fresh installation. Pairing codes identify the issuer and intended network and can enroll a node for one hour. They are separate from admin login codes.

## Updates

```sh
Mango admin --target 100.64.0.10 updates status
Mango admin --target 100.64.0.10 updates enable
Mango admin --target 100.64.0.10 updates check
```

`updates defer` pauses installation during maintenance and `updates resume` clears the pause. `updates disable` turns off automatic installation. `updates share` asks other opted-in Mango peers to verify and queue the latest signed release.

On Linux use the [managed systemd launcher](linux-install.md#managed-systemd-updates). The node requires signed packages for its OS and architecture from the Mango release feed. It stages compatible releases before draining inference, and the launcher keeps the previous build if the new one fails its startup check. Read `updates status` for the current result.

## HTTPS inference

Mango exposes a loopback inference upstream. Its reverse proxy handles TLS and the external hostname. If using Tailscale Serve, configure the endpoint on the target and use a free HTTPS port if the node already serves something else. [Tailscale Serve reference](https://tailscale.com/docs/reference/tailscale-cli/serve).

```sh
# On the target, under an OS account allowed to configure Tailscale
sudo tailscale serve --bg --https=443 http://127.0.0.1:53782

# On an admin workstation
Mango admin --target 100.64.0.10 https enable https://node.example.ts.net 53782
Mango admin --target 100.64.0.10 https status
Mango admin --target 100.64.0.10 https check
```

For an internet-facing hostname, point your HTTPS reverse proxy to the same upstream. Only routes for inference reach that upstream. Clients use a Mango inference key. Turn the upstream off with `https disable`. Use `https set --stdin` to supply `enabled`, `port`, `httpsOrigin`, and `trustedProxy` for a custom proxy configuration.

## Other commands

Use `--stdin` or `--file PATH` to send JSON to a command. The CLI prints JSON results on stdout and errors on stderr. Sensitive input belongs in a private file or stdin. Commands use the same inputs and validation as their matching Mango web controls.

| Area | Commands |
| --- | --- |
| Node status and configuration | `status`, `scheduling`, `settings show`, `settings patch`, `settings port`, `settings startup` |
| Network and enrollment | `network discover`, `network refresh`, `network queue`, `network remove ID`, `network password`, `network reset`, `pair issue` |
| Inference keys | `keys issue`, `keys revoke ID` |
| Provider and model controls | `providers list`, `providers set`, `providers remove ID`, `providers stop ID`, `providers refresh`, `providers serving ID`, `providers select`, `models set`, `models serving ID`, `models yolo ID`, `models ratings`, `models load`, `network model`, `network provider` |
| Resources and logs | `resources status`, `resources budget`, `resources benchmark`, `resources benchmark-missing`, `resources release`, `logs show [YYYY-MM-DD]`, `logs export YYYY-MM-DD`, `logs settings`, `logs collect`, `logs clear` |

Platform-specific controls remain platform-specific. For example, a Linux leech can change its HTTPS upstream and read the shared model catalog, while local provider and GPU controls apply to a contributing Windows host. OS service lifecycle, reverse proxy setup, and host recovery use the target's ordinary OS administration.

Examples of JSON inputs:

```sh
printf '%s\n' '{"port":53790}' | Mango admin --target 100.64.0.10 settings port --stdin
printf '%s\n' '{"changes":{"activityMonitoring":false},"expected":{"activityMonitoring":true}}' |
  Mango admin --target 100.64.0.10 settings patch --stdin
printf '%s\n' '{"waitSeconds":60}' | Mango admin --target 100.64.0.10 network queue --stdin
printf '%s\n' '{"name":"chat","scopes":["chat"],"priority":"normal","neverExpires":true}' |
  Mango admin --target 100.64.0.10 keys issue --stdin
Mango admin --target 100.64.0.10 logs export 2026-11-21 > mango-logs.zip
```
