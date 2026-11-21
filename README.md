# Mango

Mango bridges existing AI model servers on a private Tailscale network. It gives clients one inference API and routes requests across eligible machines. The model hosting program decides whether it can serve a valid request. Mango also coordinates local user-activity cooldown and explicitly configured model co-residency. The Windows app includes a tray icon and a local browser interface.

- [Download Mango for Windows](https://github.com/kristopher-miles/Mango/releases/latest)
- [Build and enroll a Linux leech node](docs/linux-install.md)
- [Manage Mango nodes from the terminal](docs/admin-cli.md)
- [Install and use Mango](docs/windows-install.md) · [Full usage and build guide](docs/usage.md)
- [Project requirements](docs/requirements/README.md) · [Serving boundary and rollout plan](docs/plan/10-serving-boundary.md)
- [Build and publish a release](docs/releases.md)
