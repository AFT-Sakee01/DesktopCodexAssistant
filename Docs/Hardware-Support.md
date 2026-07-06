# Hardware Support

适用版本：1.0.4.18

This repository branch is the UX3407N / UX3607O dedicated Windows on Arm edition. ARM64 is the only default build and test architecture; x64 is built only on explicit request and is not part of routine releases (see root `AGENTS.md`, Product Scope).

## Supported Target

- ASUS UX3407N
- ASUS UX3607O
- Windows on Arm, ARM64 build output

## Support Policy

The current UI layout, power behavior, thermal sampling policy, and hardware-monitoring assumptions are primarily tuned for UX3407N / UX3607O systems.

Other Windows machines may start and display generic data. Hardware-specific readings depend on each device's firmware, Windows providers, and performance counters, so some values can be unavailable or less complete outside the tuned ASUS device family. Generic fallbacks must not silently replace the UX3407N / UX3607O calibrated behavior.

## Hardware-Specific Areas

- Thermal readings depend on ACPI/WMI thermal zones exposed by the firmware.
- NPU and GPU readings depend on Windows PDH counter availability.
- Battery and power readings depend on Windows battery, power-plan, and WMI providers.
- ASUS battery actions depend on MyASUS package aliases and are not portable to other vendors.

## Release Rule

Label builds and archives as UX3407N / UX3607O tuned artifacts. Routine releases ship the ARM64 binary only; do not describe this branch or its artifacts as a generic Windows hardware monitor.
