# Radar Clock Time Mode Label

Version: 1.0.4.27
Generated model: Codex
Timestamp: 2026-07-07T10:29:00+09:00

## Scope

Codex Radar and Claude Radar EvenRow clock dials now render a small time-source label under the existing center time.

## Requirement Mapping

- `UTC` means the center time is UTC.
- `LAST` means the center time is the last attempted refresh time.
- `REF` means the center time is the last successful IQ refresh time.
- `NOW` means the center time is the current local time.
- The label color is the same brush used for the time text.
- Existing date, time, ring, marker and pointer rectangles were not resized or moved.

## Implementation

- Codex Radar: `Core/CodexRadarForm.EvenRow.cs` adds `GetEvenRowDialModeLabel` and draws the label in a new `modeRect` below the existing `timeRect`.
- Claude Radar: `Core/ClaudeRadarForm.cs` adds `GetClaudeEvenRowDialModeLabel` and draws the label in a new `modeRect` below the existing `timeRect2`.

## Verification

- Candidate ARM64 build succeeded.
- Candidate `--test`, `--test-layout`, `--test-settings-bindings` and `--test-logger` exited 0.
- Candidate `--render-codexradar` and `--render-clauderadar` produced EvenRow images showing the label below the time without moving the existing date/time text.
