# Codex Radar Model Notification State

Version: 1.0.4.37
Generated model: Codex
Timestamp: 2026-07-07T21:25:00+09:00

## Scope

Codex Radar model catalog Windows notifications now use the same persisted state pattern as Claude Radar.

## Problem

Codex Radar can read model catalogs from both `current.json` and homepage HTML fallback. Those sources can differ temporarily, so repeated refreshes may report the same model as newly added more than once.

## Implementation

- Added `%LOCALAPPDATA%/DesktopCodexAssistant/codex-radar-notification-state.ini`.
- Added `LoadCodexRadarNotificationState`, `SaveCodexRadarNotificationState` and `ApplyCodexRadarModelCatalogNotificationState`.
- Notifications are keyed by normalized model key and event state: `Added|available`, `Unavailable|temporarily_missing`, or `Deleted|deleted`.
- The state suppresses repeated notifications for the same model and same event across refreshes and restarts.
- A real state transition, such as deleted then added again, still emits one notification.

## Verification

- Candidate ARM64 build succeeded.
- Candidate `--test`, `--test-layout`, `--test-settings-bindings` and `--test-logger` exited 0.
- `RunCodexRadarNotificationStateSelfTest` covers same-batch duplicate additions, unchanged repeat suppression, restart suppression, deleted state emission, repeated deleted suppression, and deleted-then-added emission.
