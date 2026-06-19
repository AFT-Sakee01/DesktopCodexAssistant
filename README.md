# Desktop Codex Assistant

UX3407N / UX3607O dedicated edition. This software was created entirely by Codex.

This Windows on Arm application provides a compact developer-assistance workspace with performance, Codex monitoring, power, thermal, network, and connectivity modules.

## Hardware support

This branch is maintained as a dedicated build for ASUS UX3407N and UX3607O Windows on Arm machines. The current power, thermal, window placement, and hardware-monitoring behavior is calibrated against that device family.

Other Windows on Arm or x64 machines are not the support target for this branch. They may run with degraded or missing thermal, NPU, GPU, battery, or vendor-control data.

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
.\DesktopCodexAssistant.exe
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
