# Hardware Support

This repository branch is maintained as the dedicated UX3407N / UX3607O edition.

## Supported Target

- ASUS UX3407N
- ASUS UX3607O
- Windows on Arm
- ARM64 build output

## Support Policy

The current UI layout, power behavior, thermal sampling policy, and hardware-monitoring assumptions are tuned for UX3407N / UX3607O systems.

Other Windows on Arm machines may start and display generic data, but they are outside the support target for this branch. x64 Windows machines require a separate x64 build and separate validation.

## Hardware-Specific Areas

- Thermal readings depend on ACPI/WMI thermal zones exposed by the firmware.
- NPU and GPU readings depend on Windows PDH counter availability.
- Battery and power readings depend on Windows battery, power-plan, and WMI providers.
- ASUS battery actions depend on MyASUS package aliases and are not portable to other vendors.

## Release Rule

Do not publish this branch as a generic Windows monitor. Label builds and archives as UX3407N / UX3607O dedicated artifacts.
