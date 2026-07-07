# Radar IQ Ring Delta And Normal Label Summary

Version: 1.0.4.26
Generated: 2026-07-07 10:18:00 +09:00
Model: Codex

## Goal

Separate two IQ concepts that were previously coupled:

- The red/gold ring delta represents movement relative to the configured baseline.
- The text label `降智` / `常态` / `增智` represents whether the website score is outside or inside the website normal band.

## Implementation

- `DrawCodexModelIqBaselineArcs` now draws a full green bottom ring for known IQ data, then overlays only the red or gold delta.
- A below-baseline delta draws red from 12 o'clock counterclockwise.
- An above-baseline delta draws gold from 12 o'clock clockwise.
- `GetCodexModelIqNormalRangeLabel` centralizes label text and color selection from the website normal band.
- EvenRow, classic IQ status text, and OLED IQ severity use the normal-band label rule instead of the baseline delta rule.

## Validation

- `Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-iq-ring-logic-test.exe -Platform arm64`
- `_build\DesktopCodexAssistant-arm64-iq-ring-logic-test.exe --test`
- `_build\DesktopCodexAssistant-arm64-iq-ring-logic-test.exe --test-layout`
- `_build\DesktopCodexAssistant-arm64-iq-ring-logic-test.exe --test-settings-bindings`
- `_build\DesktopCodexAssistant-arm64-iq-ring-logic-test.exe --test-logger`
- `_build\DesktopCodexAssistant-arm64-iq-ring-logic-test.exe --render-codexradar --out .\_build\iq-ring-logic-render`

## Visual Result

With current site data, IQ 90 remains inside the `90-110常态区`, so the label is `常态`; because the configured baseline is higher, the ring still shows a red counterclockwise delta from 12 o'clock.
