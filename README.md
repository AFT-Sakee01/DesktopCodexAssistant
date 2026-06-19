# Desktop Codex Assistant

UX3407N / UX3607O tuned edition with public ARM64 and x64 builds. This software was created entirely by Codex.

This Windows desktop application provides a compact developer-assistance workspace with performance, Codex monitoring, power, thermal, network, and connectivity modules.

## Hardware support

This branch is maintained with primary calibration for ASUS UX3407N and UX3607O Windows on Arm machines, and public binaries are provided for both ARM64 and x64 Windows.

Other Windows machines may run with degraded or missing thermal, NPU, GPU, battery, or vendor-control data when their firmware or performance counters expose different interfaces.

## Seelen UI interoperability

This project does not include, modify, link, or redistribute Seelen UI code. Optional Seelen UI actions only interoperate with a separately installed Seelen UI instance through process/window detection and the installed `slu.exe` command-line tool. Seelen UI is a separate project licensed under AGPL-3.0; its name is used only to describe interoperability.

Kept:

- Settings window
- Performance widget panel
- CodexRadar module

Disabled:

- Dock launcher
- Launchpad
- Top bar
- Direct2D project

Build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-X64.ps1
```

Run:

```powershell
.\Release\DesktopCodexAssistant-arm64.exe
.\Release\DesktopCodexAssistant-x64.exe
```

Operation panel self-test:

```powershell
.\DesktopCodexAssistant.exe --test-operation-panel
```

Logs and settings are stored under `%LOCALAPPDATA%\DesktopCodexAssistant`.

When the renamed application starts for the first time, it migrates existing settings from `%LOCALAPPDATA%\CodexDeveloperAssistantWindowOnWOA` or `%LOCALAPPDATA%\DesktopPerfWidget-Lite` without overwriting newer files.

## Technical documentation

- [Maintenance history](CHANGELOG.jsonl)
- [Hardware support policy](Docs/Hardware-Support.md)
- [Performance modes and window runtime](Docs/Performance-And-Window-Runtime.md)
- [Component refresh rules](Docs/Component-Refresh-Rules.md)
- [Codex monitor architecture](Docs/CodexRadar-Architecture.md)
- [Power and thermal window architecture](Docs/PowerThermal-Architecture.md)
- [Network monitor architecture](Docs/NetworkMonitor-Architecture.md)
- [Interface and reusable resource summary](Docs/Interface-And-Reuse-Resources.md)
- [Machine-readable interface index](Docs/INTERFACE_INDEX.jsonl)
- [Operation panel interaction and performance execution spec](Docs/Technical/OperationPanel-Interaction-And-Performance-SPEC-v1.0.2.64-20260616-030809.md)
