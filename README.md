# Codex Developer Assistant Window on WOA

UX3407N / UX3607O dedicated edition.

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
```

Run:

```powershell
.\CodexDeveloperAssistantWindowOnWOA.exe
```

Logs and settings are stored under `%LOCALAPPDATA%\CodexDeveloperAssistantWindowOnWOA`.

When the renamed application starts for the first time, it migrates existing settings from `%LOCALAPPDATA%\DesktopPerfWidget-Lite` without overwriting newer files.

## Technical documentation

- [Hardware support policy](Docs/Hardware-Support.md)
- [Performance modes and window runtime](Docs/Performance-And-Window-Runtime.md)
- [Codex monitor architecture](Docs/CodexRadar-Architecture.md)
- [Power and thermal window architecture](Docs/PowerThermal-Architecture.md)
- [Network monitor architecture](Docs/NetworkMonitor-Architecture.md)
