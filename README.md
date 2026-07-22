# Desktop Codex Assistant — V1 Legacy Final

Release version: `1.0.5.68`

This is the frozen V1 release for ASUS UX3407N / UX3607O-focused Windows desktop use. It preserves the classic multi-window layout: the performance widget, Codex Radar, Clean IP / connection check, power and thermal, and network monitor remain separate layered windows.

The left edge has exactly two dock tabs: **Spec Board** and **Codex Task**. Network, GUARD, Codex IQ, the ten-tile topology, and headless Radar/Power owners belong to later versions and are intentionally not part of V1.

## Hardware support

V1 is calibrated primarily for ASUS UX3407N and UX3607O Windows-on-Arm systems. Release assets are provided for ARM64 and x64 Windows.

Other Windows machines may run with degraded or unavailable thermal, NPU, GPU, battery, or vendor-control data when firmware or performance counters expose different interfaces.

## Install and run

Download the matching architecture asset from the GitHub release, extract it to a dedicated folder, then run it directly:

```powershell
.\DesktopCodexAssistant-v1.0.5.68-arm64.exe
.\DesktopCodexAssistant-v1.0.5.68-x64.exe
```

The application stores settings, logs, and caches under `%LOCALAPPDATA%\DesktopCodexAssistant`. On first launch it can migrate settings from `%LOCALAPPDATA%\CodexDeveloperAssistantWindowOnWOA` or `%LOCALAPPDATA%\DesktopPerfWidget-Lite` without overwriting newer files.

Build from source:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\Release\DesktopCodexAssistant-v1.0.5.68-arm64.exe -Platform arm64
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-X64.ps1 -OutputPath .\Release\DesktopCodexAssistant-v1.0.5.68-x64.exe
```

Useful checks:

```powershell
.\Release\DesktopCodexAssistant-v1.0.5.68-arm64.exe --test
.\Release\DesktopCodexAssistant-v1.0.5.68-arm64.exe --test-layout
.\Release\DesktopCodexAssistant-v1.0.5.68-arm64.exe --test-settings-bindings
.\Release\DesktopCodexAssistant-v1.0.5.68-arm64.exe --test-operation-panel
.\Release\DesktopCodexAssistant-v1.0.5.68-arm64.exe --test-codex-task-monitor
```

`--dump-codex-tasks` is read-only: it emits task status, model, numeric token usage, workspace leaf name, and official session title when available. It does not emit prompts, responses, or full session paths.

## Data, services, and references

The program uses the following third-party services for read-only status, quota, connectivity, or exit-profile checks. The machine-readable authority is [INTERFACE_INDEX.jsonl](Docs/Interfaces/INTERFACE_INDEX.jsonl).

| Service | V1 purpose |
|---|---|
| [Codex Radar](https://codexradar.com/) | Codex service status, community model data, and Radar fallback data |
| [OpenAI Status](https://status.openai.com/) / [Anthropic Status](https://status.claude.com/) | Official service-status summaries |
| [Anthropic API](https://api.anthropic.com/) | Claude Code setup-token quota data and headers fallback |
| [ChatGPT](https://chatgpt.com/) | Codex / ChatGPT usage and reset-credit data for a locally authenticated user |
| [DeepSeek API](https://api.deepseek.com/) | Optional user-balance query when configured locally |
| [Microsoft NCSI](http://www.msftconnecttest.com/connecttest.txt), [ipify](https://api.ipify.org/), [cleanip.io](https://cleanip.io/), [Cloudflare DoH](https://cloudflare-dns.com/dns-query) | Connectivity, public-IP, Clean IP, and DNS checks |

Credentials are not committed to the repository. User configuration remains local under `%LOCALAPPDATA%\DesktopCodexAssistant`.

## Third-party interoperability and acknowledgements

- **[Seelen UI](https://github.com/eythaann/Seelen-UI)** (AGPL-3.0): V1 does not include, modify, link, or redistribute Seelen UI code. Optional actions only interact with a separately installed instance through process/window detection and its `slu.exe` CLI.
- **OpenAI Codex CLI**: V1 uses the documented `codex app-server` protocol for quota-plan operations and reads local Codex session metadata for the task board.
- **Anthropic Claude Code**: V1 can use a locally configured setup-token and a status-line cache bridge for quota display.
- **[codex-monitor-hud](https://github.com/LH-03/codex-monitor-hud)** (MIT): the task-monitor presentation follows selected implementation ideas from this project.
- **MyASUS / ASUS System Control Interface**: optional battery-care actions use the locally installed ASUS keyboard-host integration.
- **CodexSleepGuard**: V1 can launch a separately installed companion script from its operation shortcuts; it is not bundled with this release.

## Seelen UI interoperability

Dock launcher, Launchpad, top bar, and the Direct2D project are disabled in V1. The settings window, performance widget, Codex Radar, and classic auxiliary windows remain available.

## Technical documentation

- [Maintenance history](Docs/Maintenance/CHANGELOG.jsonl)
- [Hardware support policy](Docs/Hardware-Support.md)
- [Performance modes and window runtime](Docs/Performance-And-Window-Runtime.md)
- [Component refresh rules](Docs/Component-Refresh-Rules.md)
- [Codex Radar architecture](Docs/CodexRadar-Architecture.md)
- [Claude Radar architecture](Docs/Codex-ClaudeRadar-Architecture.md)
- [Power and thermal architecture](Docs/PowerThermal-Architecture.md)
- [Network monitor architecture](Docs/NetworkMonitor-Architecture.md)
- [Spec Board architecture](Docs/SpecBoard-Architecture.md)
- [Interface and reusable-resource summary](Docs/Interface-And-Reuse-Resources.md)
- [Machine-readable interface index](Docs/Interfaces/INTERFACE_INDEX.jsonl)
- [Machine-readable feature index](Docs/Indexes/FEATURE_INDEX.jsonl)
