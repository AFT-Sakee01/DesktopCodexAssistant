# Claude Radar Architecture

适用版本：1.0.5.39

This document records the current standalone Claude Radar window implementation. It is intentionally separate from the Codex Radar architecture because the two windows must not share cache files, model catalogs, or provider queues.

## Runtime Ownership

- `Core/WidgetForm.cs` owns the Claude Radar window lifetime. It creates `ClaudeRadarForm`, applies settings, forwards display suspend/resume, fullscreen visibility, shared interaction ticks, and shutdown.
- `Core/ClaudeRadarForm.cs` owns only the standalone layered window, render scene cache, public-data refresh scheduling, Claude Code usage result application, notifications, and deterministic render fixtures.
- `Core/ClaudeRadarSnapshotScheduler.cs` owns process-wide single-flight scheduling for public Claude Radar website reads. It wraps `ClaudeRadarReader.ReadSnapshot`, keys requests by selected model and data-source settings, writes request-level network history once, and returns cloned snapshots to each consumer.
- `Core/ClaudeRadarReader.cs` owns public Claude Radar defensive parsing, model-map maintenance, local quota-history fallback, public cache writes, and parser self-tests.
- `Core/ClaudeCodeUsageScheduler.cs` owns the process-wide single-flight cadence for Claude Code usage so Codex Radar Claude mode and the standalone Claude Radar window consume one shared result.
- `Core/ClaudeCodeUsageReader.cs` owns authenticated Claude Code usage reads. Successful personal quota snapshots are written through the shared `ClaudeRadarReader.TryWriteClaudeCodeQuotaCache` writer by the scheduler.
- `Core/StatuspageMonitor.cs` owns OpenAI and Claude official Statuspage reads for both radar windows, using one process-wide request per service key.
- `Core/DeepSeekBalanceMonitor.cs` owns DeepSeek public API status and optional Claude-view DS balance reads for the shared Codex Radar window and the standalone Claude window, using one encrypted key file, endpoint, refresh cadence, display text, history file and cache signature rule.
- `Core/ServiceAlertDebouncer.cs` owns the shared 10 second new-error debounce logic while each window still owns its own debounce state container.
- `ClaudeRadarClockAutoSwitchSelector` in `Core/ClaudeRadarModels.cs` owns the pure latest-model decision shared by standalone Claude Radar and shared-window Claude mode. It includes the current model in the comparison, preserves it on equal timestamps, and performs no settings or network I/O.

## Data Sources

| Source | Owner | Purpose | Cache |
|---|---|---|---|
| `https://claudecoderadar.com/data/claude-code-radar.json` | `ClaudeRadarSnapshotScheduler` -> `ClaudeRadarReader.ReadSnapshot` | IQ, efficiency, public quota, quota radar, site updated time, model catalog | `claude-radar-cache.ini`, `claude-radar-quota-history.jsonl` |
| `https://claudecoderadar.com/api/model-ratings?history=14` | `ClaudeRadarReader.ReadSnapshot` | Community rating data in a separate semantic key namespace | `claude-radar-model-map.ini` stores user mapping |
| `https://claudecoderadar.com/` | `ClaudeRadarReader.TryFetchHomepageMetadata` | Weak fallback for `MODEL_NAMES` metadata only | No business-data cache |
| `https://status.claude.com/api/v2/summary.json` | `StatuspageMonitor` | Claude public service state for the `C` service square | In-memory status snapshot |
| `https://status.openai.com/api/v2/summary.json` | `StatuspageMonitor` | OpenAI Statuspage state for the `O` service LED, matching the shared Codex Radar Claude-mode service column without reading Codex quota data | In-memory status snapshot |
| Claude Code personal usage chain | `ClaudeCodeUsageScheduler` -> `ClaudeCodeUsageReader.Read` | With an explicit setup token, OAuth usage is authoritative; without one, the passive statusline bridge remains zero-API. | `claude-statusline-quota.ini` / `claude-code-oauth-token.bin` -> `claude-quota.ini` |
| `https://api.deepseek.com/user/balance` | `DeepSeekBalanceMonitor.RefreshIfNeeded` | Public DeepSeek API status for `D`; optional bottom `DS:￥n` balance for Claude views | `deepseek-balance-history.jsonl`; key in `deepseek-api-key.bin` only for balance |

`ClaudeRadarReader.TryFetchJson` 必须保留每个数据源声明的 URI：主数据只请求精确的 `/data/claude-code-radar.json`，不得追加 `?t=`、`?cb=`、`?v=` 等 cache-buster。站点目前会把带任意查询参数的该静态路径路由为 SPA HTML，而精确路径返回 JSON；新鲜度通过 `Cache-Control: no-store, no-cache` 与 `Pragma: no-cache` 请求头保证。模型评分地址自身声明的 `?history=14` 必须原样保留。`ClaudeRadarReader.RunSelfTest` 对这两个 URI 约束做回归断言。

Public Claude Radar website data is not gated by local Codex/Claude process presence. Personal Claude Code usage is gated by the consumer window: standalone Claude Radar must be enabled, visible, not suspended, not in random test mode, and have the local Claude process present; Codex Radar Claude mode must pass the selected-provider gate. Both consumers join the same process-wide `ClaudeCodeUsageScheduler` request and receive the same result when they overlap.

Claude Radar quota line data comes from the public `quota` block. The reader prefers `quota.chart.trend` for the 7d quota trend, accepts the current site `chart.key` values such as `d7` and `total_7d`, then falls back to `base_d7_trend`. When the site has only a single usable point, the reader uses the local `claude-radar-quota-history.jsonl` 7-day values; the current `base_d7` or `quota.metrics` d7 value is recorded with a metric/update/run signature so refreshes do not duplicate the same calibration run. The standalone Claude window draws this as `ClaudeRadarQuotaLineSnapshot`; Codex Radar Claude mode converts the same snapshot into the shared `CodexQuotaRadarSnapshot` so the IQ-left vertical line keeps the same average tick, current dot, and trend segment behavior.

`ClaudeCodeUsageScheduler` calls `ClaudeCodeUsageReader.Read`. When an explicit setup token exists, `ReadViaSetupToken` calls OAuth usage first; only non-authentication failures may use the Messages-header fallback and then a fresh statusline cache. OAuth 401/403 returns `TOKEN_INVALID`, skips Messages, and drives the settings-page rebind warning. Without a token, the reader uses the passive statusline bridge, installs `%USERPROFILE%/.claude/desktop-codex-statusline-bridge.ps1` only when no custom statusline exists, and returns `NO_SETUP_TOKEN` if no fresh cache becomes available. Local token storage is `%LOCALAPPDATA%/DesktopCodexAssistant/claude-code-oauth-token.bin`, protected by `SecretStore` with DPAPI CurrentUser; a legacy `.txt` token is migrated once and renamed `.txt.migrated`. The scheduler logs only host/source and result summary, never token or response body.

## Model Mapping

Claude Radar site model keys such as `m1` and community rating keys such as `opus48_high` are different namespaces. `claude-radar-model-map.ini` links them explicitly:

- New source keys default to `pending` with an empty `rating_key`.
- Display-name matches do not auto-merge rating keys.
- Enabled rows without a nonempty `rating_key` are normalized back to `pending`.
- Missing/deleted state can advance only after a complete `ok=true` `iq.models` catalog with unique nonempty keys and a matching parsed/raw model count.
- Homepage-only metadata is weak and never increments missing/deleted counters.
- The pipe-delimited map has an eleventh `source_display_name` column. Ten-column rows remain readable; the first complete catalog establishes their source-name baseline without inventing a rename event.
- A site rename updates both names only while `display_name` still equals the previous `source_display_name`. A user-customized `display_name` is retained and a rename notification reports the new site name.
- Complete catalogs rewrite live `sort_order` values to the exact site order and place absent retained rows afterward in their previous relative order.

The settings page renders the selectable model list as a generated five-column button grid. Disabled continuity slots and trailing placeholders render as disabled buttons.

When the 24-hour clock is overdue, the selector evaluates eligible `iq.models` entries rather than excluding the current key. `HistoricalOnly`, disabled, and deleted rows are excluded before the pure latest-model selector runs. It switches only when another model is the global latest candidate; an already-latest current model produces no settings write. Missing/deleted events for `ClaudeRadarModelKey` explicitly identify that the current selected model is affected. With both Radar windows enabled, standalone Claude Radar is the sole `ClaudeRadarModelKey` auto-switch writer. Shared-window Claude mode takes ownership only when the standalone window is disabled, preventing two snapshots from alternating one shared setting.

Known lifecycle limits are intentional: Codex default-model seeds still require a release when a generation changes; Claude model identity assumes each site `m-key` is never reused for a different model; deleted Claude rows remain disabled in the map for history continuity while Codex removes rows after its deletion threshold.

## Rendering

`ClaudeRadarForm` draws the same compact EvenRow visual contract as the Codex Radar window:

- Two efficiency rings.
- 5h and 7d personal quota rings.
- IQ ring.
- Vertical quota radar line.
- Right-side three-line service/data/update status panel.
- Bottom software-family / `RC` / `DS` / `LLM` metadata row, matching the shared Codex Radar window when it is fixed to Claude mode.
- Orange 3 px software-family inner border.
- `Core/RadarClockDial.cs` owns the shared Model IQ clock state machine, 24-hour geometry and drawing used here and by shared-window Claude mode. `ClaudeRadarForm.DrawClaudeEvenRowBatchDial` only supplies the selected model `latest_at`, local request state, font-cache objects and its existing fitted-text callback. The white dot is the current-time pointer, the neutral-white vertical tick marks the 12 o'clock/day boundary, and the smaller green dot marks `latest_at` for up to 24 hours before expiring at one full lap. Status tolerates one delayed publication window: current-window and one-window-late data are green, two-window-late data are yellow with a boundary-to-now wait arc, and data at least three windows late draw a low-alpha red full ring plus the high-alpha red current-window arc. Labels ending in `pm2` or `n2` (including `_2` and `-2`) show the second-run badge without changing the phase because both publications share one batch window. `ApplyClaudeRadarClockAutoSwitchIfNeeded` calls the same `RadarClockDial.GetCycleBoundaryLocal` function; its switch threshold remains `batch < previousBoundary`, aligned with the yellow phase. `RadarClockTimeDisplayMode` controls the center lower time for both Radar windows and draws the matching `UTC`/`NOW`/`LAST`/`REF` label without moving the established date or time rectangles.

`ClaudeRadarWidth` remains an independent saved setting for deliberate standalone-window tuning. Version 57 only fixes the historical default mismatch: the user-default snapshot now starts at the same width as the shared Codex Radar window, and existing configs still at the old untouched 580 px Claude default migrate once to the current `CodexRadarWidth`.

The visual contract is intentionally shared where possible, but the Codex and Claude business data contracts stay isolated. The standalone Claude window paints `ClaudeRadarSnapshot` state produced by `ClaudeRadarSnapshotScheduler` / `ClaudeRadarReader` and `ClaudeCodeUsageScheduler`, plus shared light service probes from `StatuspageMonitor` and `DeepSeekBalanceMonitor`. Shared Codex Radar Claude mode converts that same snapshot through `ConvertClaudeRadarSnapshotForSharedWindow`: IQ、Token/时间效率、社区评分、额度线和数据时间必须与独立窗同源；数据时间统一调用 `ClaudeRadarReader.ResolveDataObtainedLocalTime`，优先选中模型稳定的 `latest_at`，仅在站点未提供时回退本机抓取时刻。转换自测同时断言这些字段。独立窗仍不得读取 Codex quota 缓存、Codex 公共网站结果、Codex reset-card 状态或 Codex provider 队列。

Standalone positioning stays local to `ClaudeRadarForm.PositionClaudeRadarWindow`. The method computes the saved size/work-area based base location, then applies `BurnInProtection.ApplyRuntimeOffset` with `BurnInProtection.ClaudeRadarSalt = 31`; this keeps the Claude window on the shared 7 minute burn-in micro-shift schedule without sharing the Codex Radar salt or embedding a raw numeric salt in the window code.

Quota reset labels use `ClaudeRadarResetTextFormatter` before paint: long strings such as `13:00 重置` and `7月4日 16:00 重置` are rendered as `13:00` and `07/04`. `ClaudeRadarQuotaSnapshot.Source` and the shared `CodexQuotaSnapshot.SourceKind` preserve whether the rings came from personal data or public-site fallback. Both Claude views render only the public-site reset labels in Danger red with forced color; personal reset labels retain their normal color, and quota numbers, rings, geometry, and Codex-family visuals remain unchanged.

The render path reads only cloned snapshot state and cached runtime presence/service state. It must not perform network I/O, disk I/O, process enumeration, or parser refresh while painting. The bottom `Claude/RC/DS/LLM` band derives `RC` and `LLM` from `ClaudeRadarSnapshot.SelectedModel*` and `ClaudeRadarSnapshot.Community`, and derives `DS` from `DeepSeekBalanceMonitor.GetSnapshot`; it must not reload `claude-radar-model-map.ini` or the DeepSeek key file during paint. `RC` displays the highest `average` row from the website community ratings payload, using `count` as the tie-breaker, while `LLM` displays the selected model. Claude-family short labels use the first two family letters plus version and tier, for example `Op4.8H`, `Fa5MAX`, and `So5Ult`.

The right-side `R/O/C/D` service LED column and API summary use the same `ApplyClaudeServiceAlertDebounce` candidates, backed by `ServiceAlertDebouncer`. `R` is Claude Radar data, `O` is OpenAI Statuspage, `C` accepts both Claude Statuspage and Claude Code usage alerts, and `D` is DeepSeek public API status. `D` remains visible without an API key; unauthenticated 401/402/422 responses mean the API gateway is reachable, while DNS/TLS/timeout/connection failures, 5xx/429, or unexpected response structure become the LED/API alert. A new non-normal service error must remain present for 10 seconds before it changes the text or LED color; recovery to normal removes the candidate immediately. Random test mode bypasses the debounce state so generated fixtures remain deterministic.

The scene cache stores at most six pre-rendered bitmaps. The cache key includes window size, render variant, opacity, burn-in color protection, clock time display mode/current minute/last attempt time, runtime presence, request/test state, animation/status rotation phase, model/IQ/efficiency/quota source/service signatures, OpenAI status, DeepSeek API/balance signature, bottom community rating key/label, and the quota radar line signature. Including quota source prevents a site-to-personal transition from reusing the red-label scene. Size changes, display suspend, and form close release the cache. `claude-radar-cache.ini` also persists the parsed `QuotaLine*` values so startup or public-data failures keep the last quota line instead of falling back to an empty gray bar.

## Refresh Rules

- Public data refresh is scheduled by `ClaudeRadarSnapshotScheduler` and runs in a background task. Shared Codex Radar Claude mode and the standalone Claude Radar window join the same running task when their request key (`selectedModelKey | json | homepage | ratings | localQuotaFallback`) matches; different keys run independently.
- Successful public refreshes schedule the next check between 15 and 60 minutes, bounded by the remote community rating `refresh_seconds`.
- Public failures preserve the last successful business snapshot, including bottom-band community/model metadata, and retry after 10 minutes while updating service state.
- Random test mode replaces snapshots in memory and does not call the public reader or write real caches.
- Claude Code usage refresh is process-wide single-flight through `ClaudeCodeUsageScheduler`, shared by Codex Radar Claude mode and standalone Claude Radar. An explicit setup token makes OAuth usage the first source; authentication failures return `TOKEN_INVALID` without Messages fallback, while non-authentication failures may use Messages headers and then fresh statusline data. Without a token, the reader uses the statusline bridge and returns `NO_SETUP_TOKEN` when unavailable. Both statusline and persisted personal `claude-quota.ini` data expire after 360 minutes; the persisted cache also requires an explicit timestamp.
- OpenAI and Claude Statuspage reads are probed through `StatuspageMonitor` only while a consuming window is enabled, visible, not suspended, and not in random test mode: normal 15 minutes, non-normal or failure 2 minutes, with AI request protection respected. They are status-only reads and never trigger Codex quota/provider logic. The monitor writes one request-level network history row with `joined_consumers`.
- DeepSeek public API status is refreshed through `DeepSeekBalanceMonitor` whenever the shared Radar or standalone Claude Radar is active and not in random test mode. Both Claude views consume the same snapshot for `DS:￥n`; the shared Codex-mode view consumes the same snapshot for the `D` LED while keeping bottom `RS`. Normal refresh is 60 seconds, failure retry is 5 minutes, and `DeepSeekApiKeyRevision` forces an immediate refresh. The monitor writes one request-level network history row with `joined_consumers`, service status and balance status.
- In the shared transition Codex Radar window, Claude mode reads `ClaudeRadarSnapshotScheduler` data instead of Codex Radar public status. It maps selected-model IQ/efficiency, community rating, and `QuotaLine` into the shared Codex Radar snapshot; IQ, quota-line, or community-rating data is enough for the refresh result to update the display. When no personal `claude-quota.ini` exists, the shared window uses Claude Radar public `quota.usage` h5/d7 values as a display fallback and suppresses the noisy `NO_SETUP_TOKEN`/legacy `NO_TOKEN` alert because that state means no setup-token source was configured, not that the visible Claude app is necessarily logged out.

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
