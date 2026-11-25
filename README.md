# Sysacta

A native Windows control MCP server for AI agents.

Sysacta is a planned Windows 11 desktop utility that exposes structured system-management tools through the Model Context Protocol (MCP). It is designed for local and self-hosted personal AI agents without granting arbitrary shell or GUI access.

> **Status:** Design complete; implementation has not started. All features below are planned, not currently available.

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

## Architecture and specification

See the [implementation specification (日本語)](docs/spec.md) for the agreed requirements, proposed MCP tools, architecture, security model and acceptance criteria.

## Roadmap

- [ ] Phase 0 — MCP server, authentication, authorization, settings UI and audit logs
- [ ] Phase 1 — Audio, display, media, power, system monitoring and notifications
- [ ] Phase 2 — Apps and processes, networking and Bluetooth
- [ ] Phase 3 — Testing, security review and Windows distribution

## Distribution

Self-contained Windows x64 ZIP and installer packages are planned for GitHub Releases. No builds are available yet.

## License

Not yet selected.
