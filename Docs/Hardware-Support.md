# Hardware Support

This repository branch is maintained as the UX3407N / UX3607O tuned edition with public ARM64 and x64 builds.

## Supported Target

- ASUS UX3407N
- ASUS UX3607O
- Windows on Arm
- ARM64 build output
- 64-bit Windows
- x64 build output

## Support Policy

The current UI layout, power behavior, thermal sampling policy, and hardware-monitoring assumptions are primarily tuned for UX3407N / UX3607O systems.

Other Windows machines may start and display generic data. Hardware-specific readings depend on each device's firmware, Windows providers, and performance counters, so some values can be unavailable or less complete outside the tuned ASUS device family.

## Hardware-Specific Areas

- Thermal readings depend on ACPI/WMI thermal zones exposed by the firmware.
- NPU and GPU readings depend on Windows PDH counter availability.
- Battery and power readings depend on Windows battery, power-plan, and WMI providers.
- ASUS battery actions depend on MyASUS package aliases and are not portable to other vendors.

## Release Rule

Label builds and archives as UX3407N / UX3607O tuned artifacts, with separate ARM64 and x64 binaries.
