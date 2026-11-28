# Sysacta

A native Windows control MCP server for AI agents.

Sysacta is a Windows 11 desktop utility that exposes structured system-management tools through the Model Context Protocol (MCP). It is designed for local and self-hosted personal AI agents without granting arbitrary shell or GUI access.

> **Status:** The foundation (Phase 0 and 1A: MCP server, per-client Bearer tokens and permissions, bounded audit log, WPF management console and tray icon), the standard audio tools (Phase 1B, with a device/alias page), and the experimental default audio device tool (`audio_set_default_device`, Phase 1B-EXP, disabled by default and **not yet verified on real hardware**) are implemented, as are the basic display tools (Phase 1C-1: list, state, brightness, contrast, mode listing) and the display mode, power and input tools (Phase 1C-2, all disabled by default). Display **reads** were verified on two external monitors; **changing brightness or contrast has not been verified on real hardware; mode, power and input changes were verified on one monitor only (DELL P2314H), where turning the monitor back on from Sysacta did not work**, and internal panels are untested (see [docs/phase-1c-1-progress.md](docs/phase-1c-1-progress.md) and [docs/phase-1c-2-progress.md](docs/phase-1c-2-progress.md)). Phase 1D media tools are implemented; **Windows read-only enumeration, state reads, and session-ID retention across a reconciled list change were verified, but no media controls were exercised**. Current-session matching is rejected as ambiguous when GSMTC exposes a different COM identity; application-specific control behavior and actual playback changes remain unverified (see [docs/phase-1d-progress.md](docs/phase-1d-progress.md)). Everything else under "Planned features" is not implemented. State-changing audio tools have only partly been verified on real hardware (see [docs/manual-tests.md](docs/manual-tests.md)). There are no release builds yet; build from source. See [docs/phase-1b-progress.md](docs/phase-1b-progress.md) and [docs/phase-1b-exp-progress.md](docs/phase-1b-exp-progress.md) for verification results and known limitations.

## Currently available

| Tool | Description | Default |
| --- | --- | --- |
| `audio_get_state` | Master volume (0–100), mute state and identity of an audio endpoint (default device or id/alias) | Enabled |
| `audio_set_volume` | Sets the absolute master volume (0–100) and verifies it by reading it back | Enabled |
| `audio_list_devices` | Output and input endpoints with state, default roles, aliases, volume and mute | Enabled |
| `audio_set_mute` | Mutes or unmutes an endpoint and verifies it | Enabled |
| `audio_list_sessions` | Per-app playback sessions of an output device | Enabled |
| `audio_set_app_volume` | Sets the volume of one app session (by `session_id`) and verifies it | Enabled |
| `audio_set_app_mute` | Mutes or unmutes one app session and verifies it | Enabled |
| `audio_set_default_device` | **Experimental** (undocumented Windows COM API, unverified on real hardware): sets the default audio device by direction and role (`console` / `multimedia` / `communications` / `all`) | Disabled (Experimental) |
| `display_list_devices` | Active displays with id, kind, connection, current desktop mode and aliases | Enabled |
| `display_get_state` | Current mode, brightness and contrast (0–100) of a display, with per-feature support status | Enabled |
| `display_set_brightness` | Sets hardware brightness (DDC/CI, or Windows control for internal panels) and verifies it; `device: "all"` reports each display | Enabled |
| `display_set_contrast` | Sets hardware contrast over DDC/CI and verifies it | Enabled |
| `display_list_modes` | Resolution / refresh rate combinations Windows enumerates (nothing is applied) | Enabled |
| `display_set_mode` | Applies a listed mode to one non-primary display after Windows' test; not saved; restorable locally from the tray menu or the Devices page | Disabled |
| `display_set_power` | Turns one external monitor on or into its power-saving off state over DDC/CI (MCCS 0xD6, values 0x01/0x04 only) | Disabled |
| `display_set_input` | Switches one external monitor to an input it declares with a standard MCCS 2.x meaning (0x60) | Disabled |
| `media_list_sessions` | Lists system media sessions, safe app labels and supported operations | Enabled |
| `media_get_state` | Reads state, metadata and timeline for one media session | Enabled |
| `media_play` / `media_pause` / `media_stop` | Request an explicit playback state | Enabled |
| `media_next_track` / `media_previous_track` | Request one track skip | Enabled |
| `media_seek` | Request an absolute position within the reported seek range | Enabled |

"Enabled" applies to new installations. Experimental tools and newly added tools on upgrade start disabled and are not granted to existing clients. Device aliases are managed on the **デバイス** (Devices) page of the console. Display support depends on the monitor, cable, dock and the monitor's DDC/CI setting; mirrored displays cannot be adjusted over DDC/CI. `display_get_state` with `include_switching: true` reports the power state and the `input_id` values (the capabilities query can take several seconds). A monitor that is switched off or to an input without a signal may stop answering, so Sysacta cannot always switch it back: make sure the monitor's power button and menu are within reach before enabling these tools.

## Planned features

| Category | Capabilities |
| --- | --- |
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
src\Sysacta.App\bin\Release\net10.0-windows10.0.19041.0\Sysacta.exe
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

Every request needs a valid token, including from localhost.

### Remote access (Tailscale Serve)

Keep the default localhost binding and let an HTTPS reverse proxy forward to the `/mcp` endpoint. With Tailscale Serve, include `/mcp` in the target, because Serve strips the mount path before forwarding:

```bash
tailscale serve --bg --set-path /sysacta http://127.0.0.1:8765/mcp
```

Then, in the console under **接続** (Connection), add the proxy's host name (for example `my-pc.tailnet-name.ts.net`) to the allowed host names and set the external URL (`https://my-pc.tailnet-name.ts.net/sysacta`) so that "copy connection settings" uses it. Clients use that external URL.

Troubleshooting: rejected requests are recorded in the audit log with their method and path — `HostNotAllowed` / `OriginNotAllowed` (403) or an authentication failure code (401). If nothing appears in the audit log, the request did not reach Sysacta; check the proxy and that the client process actually has the token (for example, Codex reads `bearer_token_env_var` in its background app-server, which must be restarted after the variable is set). Behind a proxy every request appears to come from `127.0.0.1`, so the audit log cannot tell remote clients apart by address, and the per-address limits (requests and recorded rejections) are shared. Rejections beyond the recording limits, and requests refused with 429, are only counted: they appear as aggregated records (`×count`) once a minute.

## Architecture and specification

See the [implementation specification (日本語)](docs/spec.md) for the agreed requirements, proposed MCP tools, architecture, security model and acceptance criteria.

For contributors (and coding agents): [AGENTS.md](AGENTS.md) (rules), [docs/tools.md](docs/tools.md) (adding a tool), [docs/security.md](docs/security.md) (security boundaries), [docs/manual-tests.md](docs/manual-tests.md) (hardware test status).

## Roadmap

- [x] Phase 0 — MCP server, authentication, authorization, settings UI and audit logs (see [progress](docs/phase-0-progress.md) for items verified only by automated tests)
- [ ] Phase 1 — Audio (standard tools done; default-device change pending), display (done except changes on the primary display; writes not yet verified on hardware), media (implementation under verification), power, system monitoring and notifications
- [ ] Phase 2 — Apps and processes, networking and Bluetooth
- [ ] Phase 3 — Testing, security review and Windows distribution

## Distribution

Self-contained Windows x64 ZIP and installer packages are planned for GitHub Releases (Phase 3). No builds are available yet. Unsigned binaries may be blocked by SmartScreen or Smart App Control.

## License

Not yet selected.
