# GoalSpec: Codex Task Monitor Port

## Goal And Spec

- Goal: execute the approved Codex task-monitor backend port end to end, including implementation, verification, documentation, ARM64 deployment, and runtime audit.
- Goal thread: `019e87cb-fc85-7fb2-adc6-456eba4c9eef`.
- Spec: `Docs/Technical/Fable5-CodexTaskMonitorPort-SPEC-v1.0.5.39-20260716-193026.md`.
- Spec SHA-256: `22F3A3403FEAE2B75B95947061AFE06F82E3E556E8ADC825B2936594B39D9603`.
- Implemented version: `1.0.5.41`.
- Time: `2026-07-16T20:33:40.803+09:00` (`Asia/Tokyo`).

## Requirement Mapping

| Requirement | Implementation |
| --- | --- |
| Local rollout discovery without a second watcher | `CodexRadarForm` remains the owner of `quotaSessionWatcher`. It forwards create/change/delete/rename notifications to `CodexTaskMonitorReader` and supplies shared rollout-file enumeration. |
| Incremental, bounded parsing | `CodexTaskMonitorReader` keeps a per-file byte offset, retains incomplete UTF-8/JSON tails, accepts final JSON without a newline, bounds first reads to 8 MiB, and caps tracked tasks at 64. |
| Seven-state lifecycle | The reader exposes `Active`, `Listening`, `Idle`, `Paused`, `Error`, `Completed`, and `Aborted` with the required priority, active window, terminal hold, error hold, and pause behavior. |
| Thread-safe frontend contract | Immutable snapshots are returned by `GetSnapshot()`. `SnapshotChanged` and `AttentionRaised` are worker-thread events; the host owns construction, disposal, and UI marshaling. |
| Stable task numbering | A bounded FIFO number pool deduplicates releases and enforces configurable cooldown before reuse. |
| Privacy-preserving diagnostics | Prompt and response bodies are rejected before deserialization. Dumps expose only hashed rollout keys, workspace leaf names, lifecycle state, timestamps, and numeric usage. |
| Backend configuration and migration | `WidgetSettings` schema 70 persists seven backend settings with defaults, clamps, clone/save/load, normalization, migration, and settings-binding coverage. |
| Diagnostic and regression entrypoints | `--test-codex-task-monitor` runs focused fixtures; `--dump-codex-tasks` prints a privacy-safe JSON snapshot; the focused suite is also part of `--test`. |

## Architecture And Reuse

- `CodexRadarForm` is the host boundary. It owns the existing watcher and supplies file lists; `CodexTaskMonitorReader` owns parsing, lifecycle state, numbering, snapshots, and notifications.
- File notifications request precise incremental work. Full reconciliation occurs only for initial load, settings changes, create/rename events, watcher errors, and the existing 30-second fallback.
- Status projection is gated to at most once per second through the existing WinForms update path. The reader creates no timer, watcher, or recursive enumerator.
- `CodexRadarForm.EnumerateCodexRolloutFiles` is shared by quota discovery, newest-session checks, the task monitor, and the dump command.
- Reused interface entries include `event.codex.sessions_watcher`, `resource_directory.codex_sessions`, `internal_api.widget_settings`, and `command.application.cli`. New reusable contracts are indexed as `internal_api.codex_task_monitor_reader`, `event.codex_task_monitor_updates`, and `config.codex_task_monitor_backend`.

## Data, Settings, Logging, And Safety

- No new persistence file is introduced. The only new persisted values are the seven `CodexTaskMonitor*` keys in the existing settings INI.
- Rollout files are opened with `FileShare.ReadWrite | FileShare.Delete`; malformed lines and individual file failures are isolated and cannot terminate the host.
- Content fields and `rate_limits` data are not retained. Workspace output is reduced to the final path segment, and rollout identity is emitted as `rollout:<16 hex>`.
- High-frequency file changes do not produce a recursive scan or a UI refresh for every event. Snapshot events fire only when the public snapshot actually changes.
- The implementation targets the existing ARM64 WinForms runtime and does not introduce an x64 build or a new frontend surface.

## Verification Evidence

- ARM64 candidate: `_build/release-candidate/DesktopCodexAssistant-1.0.5.41-arm64.exe`.
- Candidate and formal SHA-256: `DD8A0B901769D726623D3D2F824C42B6578A2BEE5BB9ADCAABD9F915063D2763`.
- `--test-codex-task-monitor`: exit `0`; passed no-newline, silent, aborted, heartbeat, UTF-8, degraded input, seven states, cooldown, offset continuation, truncated tail, cap/fallback/terminal priority, and attention-once cases.
- `--test-settings-bindings`: exit `0`; task-monitor settings, fixture round trip, and full round trip passed with 249 persisted properties, 257 supported properties, and 8 explicit exemptions.
- `--test`: exit `0` on native ARM64 and included the task-monitor suite.
- Real `--dump-codex-tasks`: returned active local tasks with only hashed rollout keys and workspace leaf names; no slash or backslash appeared in a workspace leaf.
- Static audit: the reader contains no `FileSystemWatcher`, timer, recursive file enumeration, or `rate_limits` parser; `git diff --check` passed.
- Documentation JSONL parse and uniqueness checks passed before final indexing; the final Gate is recorded in the maintenance entry.
- Formal runtime: `DesktopCodexAssistant.exe` version `1.0.5.41`, PID `28552` at deployment verification, with the same candidate hash and a successful post-restart dump.
- Source backup: `_build/source-backups/codex-task-monitor-20260716-200819`.
- Formal rollback backups: `_build/formal-backups/DesktopCodexAssistant-1.0.5.40-before-codex-task-monitor-20260716-203040.exe` and `_build/formal-backups/DesktopCodexAssistant-1.0.5.41-before-refresh-gate-20260716-203306.exe`.

## Spec Deviations And Limits

- No visible task-monitor UI was added. This is the explicit backend-only boundary of the approved Spec; future presentation code must consume the host-owned reader contract.
- Completion attention is emitted once immediately when a task first reaches the terminal state. No extra grace period was invented because the Spec requires once-only delivery.
- `ActiveCount` follows the upstream visible tracked-task semantics rather than introducing a second count definition.
- Rollout JSON is an external, unstable local format. Unknown records are ignored and individual parse failures degrade locally, but a future incompatible schema can still require parser maintenance.
- State freshness ultimately depends on rollout file writes. A silent producer can transition through the configured active and idle thresholds even if its external process remains alive.

## Status

The approved backend scope, persistence migration, diagnostics, regression coverage, documentation, ARM64 deployment, and runtime verification are complete for `1.0.5.41`.
