# Radar IQ Dynamic Scale And Baseline Summary

Version: 1.0.4.25
Generated: 2026-07-07 10:06:00 +09:00
Model: Codex

## Goal

Keep the Codex Radar IQ ring aligned with the live CodexRadar website instead of a fixed local scale, while preserving manual override control for the IQ baseline.

## Implementation

- `Core/CodexRadarForm.cs` stores `CodexRadarSnapshot.ModelIqDisplayMaxScore` and persists it as `DisplayMaxScore` in `codex-radar-cache.ini`.
- `ApplyCodexModelIqDisplayMaxFromSource` scans `current.json` `model_iq` data across all models; `ApplyCodexRadarHtmlModelIqDisplayMax` scans homepage `IQ指数` history during HTML fallback.
- `GetCodexModelIqDisplayMaxScore` chooses the website-derived max first, then falls back to current score, normal high, history, and offline protection.
- `GetCodexModelIqBaselineScore` replaces direct pass-count comparison for IQ status. Automatic mode follows website `valid_tasks`, infers score scale from `score/tasks/passed`, and derives baseline `n/N` from the website normal band. Manual mode uses `CodexModelIqBaselinePassed` and `CodexModelIqBaselineValidTasks`.
- `NormalizeCodexModelIqValidTaskCount` now preserves website task counts within the safe settings range instead of folding everything back to 10 tasks.
- `Settings/WidgetSettings.cs` adds `CodexModelIqBaselineAutoEnabled` and `CodexModelIqBaselineValidTasks` with defaults, clone, load/save, normalization, and self-test coverage. `Settings/Win11SettingsForm.cs` exposes the controls in the Codex Radar IQ group.

## Rendering Rule

The IQ ring center displays the website score without a percent sign. The ring scale follows `ModelIqDisplayMaxScore`; with current website data that resolves to 120. Green is drawn up to the lower of current score and baseline score. If current score is below baseline, red extends counterclockwise from the baseline point. If current score is above baseline, gold extends clockwise from the baseline point.

## Validation

- `Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-iq-dynamic-test.exe -Platform arm64`
- `_build\DesktopCodexAssistant-arm64-iq-dynamic-test.exe --test`
- `_build\DesktopCodexAssistant-arm64-iq-dynamic-test.exe --test-layout`
- `_build\DesktopCodexAssistant-arm64-iq-dynamic-test.exe --test-settings-bindings`
- `_build\DesktopCodexAssistant-arm64-iq-dynamic-test.exe --test-logger`
- `_build\DesktopCodexAssistant-arm64-iq-dynamic-test.exe --render-codexradar --out .\_build\iq-dynamic-render`

## Notes

The internal score clamp is only a defensive parse guard. It is not the visual scale. The visual scale must remain driven by website data or cached website-derived `DisplayMaxScore`.
