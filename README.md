# Sysacta

A native Windows control MCP server for AI agents.

Sysacta is a Windows 11 desktop utility that exposes structured system-management tools through the Model Context Protocol (MCP). It is designed for local and self-hosted personal AI agents without granting arbitrary shell or GUI access.

> **Status:** Phase 0 (foundation) is implemented: MCP server, per-client Bearer tokens and permissions, audit log, WPF management console and tray icon, plus two reference tools — `audio_get_state` and `audio_set_volume`. Everything else below is planned. There are no release builds yet; build from source. See [docs/phase-0-progress.md](docs/phase-0-progress.md) for verification results and known limitations.

## Currently available

| Tool | Description | Default |
| --- | --- | --- |
| `audio_get_state` | Master volume (0–100), mute state and identity of an audio endpoint (default device or id/alias) | Enabled |
| `audio_set_volume` | Sets the absolute master volume (0–100) and verifies it by reading it back | Enabled |

## Planned features

| Category | Capabilities |
| --- | --- |
| Audio | Volume, mute, per-app volume, audio devices |
| Display | Brightness, contrast, display settings and supported DDC/CI controls |
| Media | Playback control and media session information |
| Power | Lock, sleep, shutdown, restart and power plans |
| System | CPU, GPU, memory, storage and supported hardware sensors |
| Apps & processes | App discovery, allowlisted launch and process management |
| Network | Connection information, traffic and adapter management |
| Bluetooth | Device discovery and supported connection controls |
| Notifications | Windows notifications |

Availability will depend on hardware, Windows APIs, permissions and configuration.

## Design principles

- Windows-native, using C# and .NET 10 LTS.
- Modern WPF management console and system tray operation.
- Streamable HTTP transport with per-client Bearer tokens.
- Fine-grained tool permissions; potentially disruptive operations disabled by default.
- Local audit logs, explicit result verification where possible, and no telemetry.
- No arbitrary command execution, file access or GUI automation.
- Localhost by default; optional remote access through a trusted HTTPS reverse proxy.

## Build and run

Requirements: Windows 11 x64 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build Sysacta.sln -c Release
dotnet test Sysacta.sln -c Release
src\Sysacta.App\bin\Release\net10.0-windows\Sysacta.exe
```

On first start Sysacta opens the management console, starts the MCP server on `http://127.0.0.1:8765/mcp` and keeps running in the notification area when the window is closed (use the tray menu → 終了 to exit). Data (settings, token hashes, audit log) is stored in `%LOCALAPPDATA%\Sysacta`.

## Connecting an MCP client

1. Open the console → **クライアント** (Clients), enter a name and click **発行** (Issue). The token is shown only once.
2. Click **接続設定をコピー** (Copy connection settings) and add it to your client. The format differs between clients; a typical one is:

```json
{
  "mcpServers": {
    "sysacta": {
      "url": "http://127.0.0.1:8765/mcp",
      "headers": { "Authorization": "Bearer YOUR_TOKEN" }
    }
  }
}
```

3. Allow or deny individual tools per client on the same page; enable or disable tools globally on **ツール** (Tools). Changes apply to the next request.

Every request needs a valid token, including from localhost. For remote access keep the localhost binding and put an HTTPS reverse proxy (for example Tailscale Serve) in front, then add its host name under **接続** (Connection) → allowed hosts.

## Architecture and specification

See the [implementation specification (日本語)](docs/spec.md) for the agreed requirements, proposed MCP tools, architecture, security model and acceptance criteria.

For contributors (and coding agents): [AGENTS.md](AGENTS.md) (rules), [docs/tools.md](docs/tools.md) (adding a tool), [docs/security.md](docs/security.md) (security boundaries), [docs/manual-tests.md](docs/manual-tests.md) (hardware test status).

## Roadmap

- [x] Phase 0 — MCP server, authentication, authorization, settings UI and audit logs (see [progress](docs/phase-0-progress.md) for items verified only by automated tests)
- [ ] Phase 1 — Audio, display, media, power, system monitoring and notifications
- [ ] Phase 2 — Apps and processes, networking and Bluetooth
- [ ] Phase 3 — Testing, security review and Windows distribution

## Distribution

Self-contained Windows x64 ZIP and installer packages are planned for GitHub Releases (Phase 3). No builds are available yet. Unsigned binaries may be blocked by SmartScreen or Smart App Control.

## License

Not yet selected.
