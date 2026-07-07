# Claude Radar Architecture

适用版本：1.0.4.27

This document records the current standalone Claude Radar window implementation. It is intentionally separate from the Codex Radar architecture because the two windows must not share cache files, model catalogs, or provider queues.

## Runtime Ownership

- `Core/WidgetForm.cs` owns the Claude Radar window lifetime. It creates `ClaudeRadarForm`, applies settings, forwards display suspend/resume, fullscreen visibility, shared interaction ticks, and shutdown.
- `Core/ClaudeRadarForm.cs` owns only the standalone layered window, render scene cache, public-data refresh scheduling, Claude Code usage refresh scheduling, notifications, and deterministic render fixtures.
- `Core/ClaudeRadarReader.cs` owns public Claude Radar website reads, defensive parsing, model-map maintenance, local quota-history fallback, public cache writes, and parser self-tests.
- `Core/ClaudeCodeUsageReader.cs` owns authenticated Claude Code usage reads. Successful personal quota snapshots are written through the shared `ClaudeRadarReader.TryWriteClaudeCodeQuotaCache` writer.

## Data Sources

| Source | Owner | Purpose | Cache |
|---|---|---|---|
| `https://claudecoderadar.com/data/claude-code-radar.json` | `ClaudeRadarReader.ReadSnapshot` | IQ, efficiency, public quota, quota radar, site updated time, model catalog | `claude-radar-cache.ini`, `claude-radar-quota-history.jsonl` |
| `https://claudecoderadar.com/api/model-ratings?history=14` | `ClaudeRadarReader.ReadSnapshot` | Community rating data in a separate semantic key namespace | `claude-radar-model-map.ini` stores user mapping |
| `https://claudecoderadar.com/` | `ClaudeRadarReader.TryFetchHomepageMetadata` | Weak fallback for `MODEL_NAMES` metadata only | No business-data cache |
| `https://status.claude.com/api/v2/summary.json` | `ClaudeRadarReader.ReadClaudePublicStatusState` | Claude public service state for the `C` service square | Stored only inside the latest snapshot/cache state |
| Claude Code statusline bridge | `ClaudeCodeUsageReader.Read` | Personal 5h/7d quota rings from Claude Code's own statusline JSON stream. This default path performs no Claude API/model request and spends no extra Claude tokens. | `claude-statusline-quota.ini` -> `claude-quota.ini` |

Public Claude Radar website data is not gated by local Codex/Claude process presence. Personal Claude Code usage is gated by the standalone window being enabled, visible, not suspended, not in random test mode, and the local Claude process being present.

Claude Radar quota line data comes from the public `quota` block. The reader prefers `quota.chart.trend` for the 7d quota trend, accepts the current site `chart.key` values such as `d7` and `total_7d`, then falls back to `base_d7_trend`. When the site has only a single usable point, the reader uses the local `claude-radar-quota-history.jsonl` 7-day values; the current `base_d7` or `quota.metrics` d7 value is recorded with a metric/update/run signature so refreshes do not duplicate the same calibration run.

Claude Code personal quota now defaults to a passive statusline bridge. `ClaudeCodeUsageReader.Read` installs `%USERPROFILE%/.claude/desktop-codex-statusline-bridge.ps1` and a Claude Code `statusLine` command only when no custom statusline exists, then reads `%LOCALAPPDATA%/DesktopCodexAssistant/claude-statusline-quota.ini`. Claude Code writes that cache when it already renders its own statusline during real user activity. If a custom statusline command exists, the program does not overwrite it; the user can merge the bridge manually or continue with public-site fallback data. The older `claude setup-token` path remains in `ClaudeCodeUsageReader.ReadViaSetupToken` as a non-default retained fallback, but the app no longer calls it automatically because its Messages-header fallback can spend a small amount of Claude quota.

## Model Mapping

Claude Radar site model keys such as `m1` and community rating keys such as `opus48_high` are different namespaces. `claude-radar-model-map.ini` links them explicitly:

- New source keys default to `pending` with an empty `rating_key`.
- Display-name matches do not auto-merge rating keys.
- Enabled rows without a nonempty `rating_key` are normalized back to `pending`.
- Missing/deleted state can advance only after a complete `ok=true` `iq.models` catalog with unique nonempty keys and a matching parsed/raw model count.
- Homepage-only metadata is weak and never increments missing/deleted counters.

The settings page renders the selectable model list as a generated five-column button grid. Disabled continuity slots and trailing placeholders render as disabled buttons.

## Rendering

`ClaudeRadarForm` draws the same compact EvenRow visual contract as the Codex Radar window:

- Two efficiency rings.
- 5h and 7d personal quota rings.
- IQ ring.
- Vertical quota radar line.
- Right-side three-line service/data/update status panel.
- Bottom `RC` / `LLM` / software-family metadata row.
- Orange 3 px software-family inner border.
- Model IQ clock uses a 24-hour clockwise dial: the white dot is the current-time pointer, a small green vertical tick marks the 12 o'clock/day-boundary position, and the smaller green dot marks the selected model `latest_at` for up to 24 hours, disappearing after the next full lap reaches that position. While the green dot is valid, the visible arc connects that refresh dot clockwise to the current white dot; after expiry the old refresh dot is not reused as an arc origin. `RadarClockTimeDisplayMode` controls the center lower time for both Codex and Claude Radar; the default is UTC, with current local time, last attempt refresh, and last actual IQ refresh as alternatives. `1.0.4.27` also draws the matching short label `UTC`/`NOW`/`LAST`/`REF` below that time in the same status color without moving the existing date or time rectangles.

The visual contract is intentionally shared where possible, but the data contract is not. The standalone Claude window paints only `ClaudeRadarSnapshot` state produced by `ClaudeRadarReader` and `ClaudeCodeUsageReader`; it must not read Codex Radar snapshots, Codex quota caches, Codex public website results, DeepSeek balance state, or Codex provider queues.

The render path reads only cloned snapshot state and cached runtime presence state. It must not perform network I/O, disk I/O, process enumeration, or parser refresh while painting. The bottom `RC/LLM/Claude` band derives its text from `ClaudeRadarSnapshot.SelectedModel*` and `ClaudeRadarSnapshot.Community`; it must not reload `claude-radar-model-map.ini` during paint. `RC` displays the highest `average` row from the website community ratings payload, using `count` as the tie-breaker, while `LLM` displays the selected model. Claude-family short labels use the first two family letters plus version and tier, for example `Op4.8H`, `Fa5MAX`, and `So5Ult`.

The right-side `R/C/U` service LED column and API summary use the same `ApplyClaudeServiceAlertDebounce` candidates. A new non-normal service error must remain present for 10 seconds before it changes the text or LED color; recovery to normal removes the candidate immediately. Random test mode bypasses the debounce state so generated fixtures remain deterministic.

The scene cache stores at most six pre-rendered bitmaps. The cache key includes window size, render variant, opacity, burn-in color protection, clock time display mode/current minute/last attempt time, runtime presence, request/test state, animation/status rotation phase, model/IQ/efficiency/quota/service signatures, bottom community rating key/label, and the quota radar line signature. Size changes, display suspend, and form close release the cache. `claude-radar-cache.ini` also persists the parsed `QuotaLine*` values so startup or public-data failures keep the last quota line instead of falling back to an empty gray bar.

## Refresh Rules

- Public data refresh is single-flight and runs in a background task.
- Successful public refreshes schedule the next check between 15 and 60 minutes, bounded by the remote community rating `refresh_seconds`.
- Public failures preserve the last successful business snapshot, including bottom-band community/model metadata, and retry after 10 minutes while updating service state.
- Random test mode replaces snapshots in memory and does not call the public reader or write real caches.
- Claude Code usage refresh is separately single-flight and uses the personal quota cache only after a successful read. The default path reads `claude-statusline-quota.ini`, rejects stale statusline data after 360 minutes, converts remaining percentages into the shared quota snapshot, and then writes `claude-quota.ini` through the existing shared writer. It never calls Claude API endpoints. The retained `ReadViaSetupToken` fallback still contains the previous OAuth usage JSON and Messages-header parser for rollback only.
- In the shared transition Codex Radar window, Claude mode reads `ClaudeRadarReader` data instead of Codex Radar public status. When no personal `claude-quota.ini` exists, the shared window uses Claude Radar public `quota.usage` h5/d7 values as a display fallback and suppresses the noisy `NO_SETUP_TOKEN`/legacy `NO_TOKEN` alert because that state means no setup-token source was configured, not that the visible Claude app is necessarily logged out.

## Acceptance Entrypoints

- `--test` covers reader parsing, service status mapping, partial catalog deletion guards, failure-state fixtures, storage isolation, quota-history duplicate/bad-line/trim behavior, Claude Code usage parsing, and selected-provider runtime gates.
- `--test-settings-bindings` covers the Claude Radar settings controls and five-column model selector policy.
- `--test-layout` covers general layout checks plus `ClaudeRadarForm.RunRenderResourceSelfTest`, including notification-state de-duplication, last-good failure merge, public-refresh single-flight, Claude service alert debounce, nonblank render states, scene-cache cap, and render-buffer/cache disposal.
- `--test-settings-open-close --iterations <n>` repeatedly opens and closes the Win11 settings window and asserts bounded handle/GDI/USER deltas.
- `--test-radar-display-lifecycle --iterations <n>` creates Codex/Claude Radar handles with remote sources disabled, repeatedly runs display suspend/resume on both child windows, and asserts bounded handle/GDI/USER deltas.
- `--render-clauderadar --out <dir>` writes deterministic normal, missing-data, warning, error, offline, and test-randomized fixture images plus matching 2880x1800 desktop screenshots, and a `clauderadar-current.png` real-configuration sample (see `Docs/Fable5-Frontend-Rendering-Technical.md` for sample-vs-current semantics).
- `--diagnose-radar-runtime --diagnose-seconds <n> [--diagnose-target-pid <pid>] [--diagnose-label <name>]` samples the current process or a running target process CPU, working set, private bytes, handle count, GDI objects, and USER objects, then writes `radar-runtime-diagnosis-*.txt/.json` under `%LOCALAPPDATA%/DesktopCodexAssistant`.

## Verification Matrix

`CodexRadarEnabled` and `ClaudeRadarEnabled` independently control child-window creation, so the production runtime matrix covers only-Codex, only-Claude, and both-window settings. The heavier UI lifetime checks are covered by `--test-settings-open-close --iterations 200` and `--test-radar-display-lifecycle --iterations 100`. High-frequency scene switching is covered by `ClaudeRadarForm.RunRenderResourceSelfTest`, which cycles six deterministic scenes 120 times and asserts cache hits after warm-up. Historical 1.0.3.90 target-PID resource baselines live in the CHANGELOG record `change-20260705T061200Z-1-0-3-90-radar-independent-lifecycle-stress-validation`.
