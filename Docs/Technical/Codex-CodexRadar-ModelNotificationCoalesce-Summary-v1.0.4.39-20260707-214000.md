# Codex Radar Model Notification Coalescing

Version: 1.0.4.39
Generated model: Codex
Timestamp: 2026-07-07T21:40:00+09:00

## Scope

Codex Radar model catalog notification de-duplication now coalesces conflicting events inside the same refresh batch before comparing against the persisted notification state.

## Rule

For one normalized model key in one merged refresh result, notification state priority is:

1. `Added|available`
2. `Unavailable|temporarily_missing`
3. `Deleted|deleted`

Only the highest-priority event is compared with `codex-radar-notification-state.ini` and emitted.

## Reason

`current.json` and homepage HTML fallback can temporarily expose different model catalogs. Without batch coalescing, one refresh could carry both `Deleted` and `Added` for the same key, leaving the persisted state on the wrong side and causing repeated `已加入检测列表` notifications.

## Verification

`RunCodexRadarNotificationStateSelfTest` now covers conflicting same-batch `Deleted` + `Added` events and verifies only one added notification is emitted, with subsequent repeats suppressed.
