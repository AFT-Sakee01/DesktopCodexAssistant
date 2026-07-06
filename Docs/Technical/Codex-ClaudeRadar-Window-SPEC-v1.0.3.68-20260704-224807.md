# Claude Radar Window SPEC

Spec version: `1.0.3.68`
Created at: `2026-07-04T22:48:07+09:00`
Target module: `Claude Radar 独立监测窗口`
Status: `draft`

Primary files expected during implementation:

- `Core/ClaudeRadarForm.cs`
- `Core/ClaudeRadarReader.cs`
- `Core/ClaudeRadarModels.cs`
- `Core/CodexRadarForm.cs`
- `Core/CodexRadarForm.EvenRow.cs`
- `Core/CodexRadarForm.ClaudeUsage.cs`
- `Settings/WidgetSettings.cs`
- `Settings/Win11SettingsForm.cs`
- `Core/WidgetForm.cs`
- `DesktopCodexAssistant.cs`
- `Docs/Component-Refresh-Rules.md`
- `Docs/Indexes/FEATURE_INDEX.jsonl`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`
- `Docs/Maintenance/CHANGELOG.jsonl`

## Goal

Build a separate `Claude Radar` window that visually and behaviorally matches the current `Codex Radar` window where the Claude data source supports it.

The window must:

1. Use Claude Radar public data from `https://claudecoderadar.com/`.
2. Reuse the existing Codex Radar layout, ring drawing, status summary, settings patterns, cache discipline, notifications, and ARM64 verification flow.
3. Keep Claude data, Codex data, model catalogs, quota cache, and test state isolated so the two radar windows cannot pollute each other.
4. Add an adjustable model mapping table because Claude Radar uses internal keys such as `m1`, while the community-rating API uses semantic keys such as `opus48_high`.
5. Implement a local seven-day quota-radar history fallback. When the site does not provide a complete trend, use the locally recorded last seven days: highest value maps to the vertical top, lowest value maps to the vertical bottom, and existing quota-radar color/average rules otherwise remain unchanged.

## Source Facts

Verified on `2026-07-04`:

- `https://claudecoderadar.com/` exposes sections for `额度雷达`, `降智雷达`, and `社区体感分`. It states the fixed benchmark task set has `10道`.
- `https://claudecoderadar.com/data/claude-code-radar.json` returns JSON and is the primary usable source for Claude quota and IQ data.
- `https://claudecoderadar.com/current.json`, `https://claudecoderadar.com/feed.xml`, and `https://claudecoderadar.com/api/v1/current` currently return HTML, not JSON/RSS payloads. Do not reuse the Codex `current.json` or RSS reset reader for Claude unless these routes later become structured.
- `https://claudecoderadar.com/api/model-ratings?history=14` returns JSON community rating data with semantic model IDs.
- The homepage script currently declares Claude internal model keys including `m1`, `m2`, `m4`, `m3`, `m5`, `m7`, and `m6`.
- The current data endpoint currently reports model rows such as `m1: Opus 4.8 high`, `m2: Sonnet 5 max`, `m4: Sonnet 5 xhigh`, `m3: Sonnet 5 high`, `m5: Fable 5 xhigh`, and `m6: Haiku 4.5`.
- The current quota object currently has only one trend point for 5h and 7d and an empty `tiers` array, so a full Codex-style trend cannot be inferred solely from the site yet.

These facts are not permanent contracts. The implementation must treat every public field as optional and must preserve the last good business snapshot when a request or parser fails.

## Reused Indexed Context

Reuse these existing project concepts:

- `codex_radar.model_iq_efficiency`
- `codex_radar.service_health_quota_radar`
- `codex_radar.quota_consumption_ring`
- `window_layout.multi_display_modules`
- `settings.win11_settings_form`
- `internal_api.widget_settings`
- `internal_api.ui_font_cache`
- `service.windows.layered_window`
- `resource_directory.technical_specs`

Do not restore Dock, Launchpad, top bar, Direct2D, or the old visible three-row service-health panel.

## Non-Goals

- Do not implement Claude速蹬 or reset RSS behavior by scraping HTML text. Claude Radar currently does not expose a structured equivalent.
- Do not use Claude Code paid API calls as a health probe if they would consume quota or tokens.
- Do not merge Claude Radar model keys into Codex Radar model cache.
- Do not make the Claude window depend on the Codex window being visible.
- Do not introduce x64-specific work. ARM64 remains the default.
- Do not delete existing Codex Radar rollback code while building the first Claude Radar version.

## Target Window

Create a separate floating layered window with the same operating model as existing child windows:

- Owned and coordinated by `WidgetForm`.
- Independent visibility, position, display selection, opacity, hover behavior, and sizing settings.
- Uses `NativeMethods.LayeredBitmapSurface`, `UiFontCache`, `DesignTokens`, and `BurnInProtection`.
- Clones background snapshots before drawing.
- Releases and recreates render resources across display suspend/resume.

The first implementation should target the current `EvenRow` style because that is the active Codex Radar layout. Other visual variants can be added later only if they are already generic enough to reuse without large duplication.

### Scene Bitmap Cache

Use方案 2 for frequent in-window element replacement: pre-render each scene into an off-screen bitmap and switch by submitting the cached bitmap through the existing layered-window surface.

Rules:

- Keep one native layered window. Do not switch to another window just to replace all elements.
- Cache rendered bitmaps by window size, render variant, software family, content opacity, background opacity, burn-in color-protection state, blink phase, selected model, and a compact display-data signature.
- When data, layout, opacity, render variant, or software family changes, the key changes and the scene is redrawn.
- When only the scene switches back to a previously rendered state, copy the cached bitmap into the upload buffer and call `UpdateLayeredWindow`; do not re-run all GDI+ drawing.
- Bound the cache. For the current compact radar size, six cached bitmaps are well under 100 MB; future larger windows must keep the same cap or calculate memory before raising it.
- Clear cached scene bitmaps when the window size changes, display resources reset, or the form closes.
- Network, disk, and cache reads must never be triggered by scene switching. Scene switching is paint-only.

This is a CPU optimization. It intentionally spends a small amount of memory to avoid repeated text measurement, ring drawing, arc drawing, and layout work during frequent switches.

### Software Family Chrome

Each Radar window should have a fixed software family once separate Codex and Claude windows exist:

- Codex Radar: Codex quota source, Codex cache namespace, deep blue software chrome.
- Claude Radar: Claude quota source, Claude cache namespace, orange software chrome.

For the current shared Codex Radar transition window, the existing effective software family can still drive the preview, but the future Claude Radar window must not depend on automatic foreground-app switching.

Visual rules:

- Draw a 3 px inward rounded border over the whole radar window.
- Codex family border: deep blue.
- Claude family border: orange.
- Bottom metadata must not show `SF:`. Show only the product name.
- Codex label: `Codex`, blue, italic.
- Claude label: `Claude`, orange, bold.
- Quota ring numbers should return to white for normal running data, independent of Codex/Claude family color.
- Quota ring numbers gray out when the quota value is unknown or both supported local apps are not running. If either Codex or Claude is running, numeric quota text stays white even when the currently selected family is the other app.

### Runtime Optimization And Probe Gating

Historical pre-1.0.3.76 and pre-1.0.3.79 code scan findings retained as context:

- `ResolveCodexRadarSoftwareMode` previously fell through to `TryDetectForegroundCodexRadarSoftware`, which called foreground-window and process APIs when Auto mode could not resolve by configuration alone.
- Codex process polling was already name-specific and performance-mode throttled, but Claude and Codex presence still needed to be resolved as one shared snapshot instead of separate one-off checks.
- Network-change callbacks previously requested both Codex provider usage and Claude usage; the refresh methods later filtered by effective mode, but the trigger queues still got touched unnecessarily.
- `DeepSeek` already short-circuits when no API key is configured; `CodexConnectionFlowEnabled=false` already prevents hidden five-stage diagnostics from running.
- Service-health probes still have a visible consumer in the compact API summary; future independent Radar windows must make every probe conditional on an active visible consumer, not on retained rollback drawing code.

Required optimization rules:

1. Process presence snapshot:
   - Maintain a cached `SoftwareRuntimePresence`-style snapshot with at least `codex_running`, `claude_running`, `checked_utc`, and an optional `changed_reason`.
   - Refresh the snapshot on the existing performance-mode cadence, explicit settings/software changes, process-start relevant events if available, and manual refresh.
   - Process enumeration must be done by required executable names only and never from paint code.
   - All consumers must read the same snapshot: Auto software selection, quota number color, quota refresh gating, bottom brand preview, and notification decisions.

2. Auto software selection:
   - If the user selected fixed `Codex` or fixed `Claude`, do not query the foreground window.
   - In Auto mode, check the cached process presence snapshot before foreground detection.
   - If neither Codex nor Claude is running, keep the last effective family for cache/display continuity, mark `supported_app_running=false`, and skip foreground-window detection.
   - If only Codex is running, select Codex without foreground-window detection.
   - If only Claude is running, select Claude without foreground-window detection.
   - Query foreground-window/process/title only when both Codex and Claude are running.
   - If both are running but foreground detection fails or points to this app, keep the last effective family instead of oscillating.

3. Quota ring number color:
   - The quota ring number color is a local-app presence indicator, not a quota-source health indicator.
   - If either Codex or Claude is running, quota ring numbers are white for numeric values.
   - If both Codex and Claude are stopped, quota ring numbers are gray for cached numeric values.
   - If there is no numeric quota value at all, the placeholder/unknown display may be gray, but do not infer "both apps stopped" from a web/API failure alone.
   - Source failures, expired auth, unsupported region, or unavailable quota APIs must be represented by the service/status text and logs, not by changing a valid numeric quota value away from white.

4. Personal quota refresh:
   - When both supported apps are stopped, do not run Codex provider or Claude usage refresh. Keep the last good snapshot and rely on the low-frequency process presence poll or an explicit event/manual refresh to re-prime the active source.
   - When exactly one supported app is running, only that app's usage provider may be queued or refreshed.
   - When both supported apps are running, refresh only the effective family in the shared transition window. Future separate Codex Radar and Claude Radar windows refresh their own fixed family independently.
   - Network-change, manual-refresh, software-switch, and resume triggers must enqueue only providers that have an active visible consumer.
   - A provider that is not selected, not visible, or blocked by `both_apps_stopped` must not update its `next_refresh` trigger just because a global event happened.

5. Public radar and service probes:
   - Public radar data, model discovery, IQ, efficiency, and site quota-radar reads are web data and are not tied to local Codex/Claude process presence.
   - They still must skip hidden/disabled/suspended windows and must obey their own cadence, single-flight, backoff, and test-mode rules.
   - HTML/RSS/homepage fallbacks are sequential fallback layers after the primary source fails; do not request them speculatively in parallel.
   - A missing model is deleted only after a complete successful catalog response confirms absence; partial failures, unsupported-region pages, and schema errors must not trigger deletion.
   - Service-health probes run only when their result is visible or feeds an active alert. Retained rollback UI code is not a consumer by itself.
   - DeepSeek balance requests require a configured API key and a visible consumer; no-key state is local and must not schedule web requests.

6. Rendering, logging, and cache:
   - Scene bitmap cache keys should include the effective family and the coarsened `supported_app_running` state, not raw foreground window handles, process IDs, or rapidly changing process metadata.
   - Quota decision logs should be written for real quota snapshot changes, reset/protection decisions, and process-presence state transitions that affect display. Do not write recurring identical `both_apps_stopped` idle polls.
   - Settings changes should restart only the schedulers affected by changed settings; saving unrelated settings must not re-prime all provider queues.

Implementation update for the current shared Codex Radar window:

- `SoftwareRuntimePresence` is the shared process-presence source for Codex and Claude. It queries only explicit executable names, caches by performance mode cadence, and is never refreshed from paint code.
- Fixed Codex/Claude software modes refresh the shared process snapshot for display state but never query the foreground window.
- Auto mode now uses the process snapshot first: no app running keeps the previous family, one app running selects that family, and foreground-window detection is only used when both apps are running.
- Manual refresh, network events, software switch, and test-mode recovery enqueue only the selected provider that has a running local software consumer. They no longer touch both Codex provider usage and Claude Code usage queues.
- The quota-ring numeric text is white when a numeric quota value is known and either supported local app is running; it is gray only when the numeric value is unknown or both supported apps are stopped.
- The pre-rendered scene cache signature includes the coarsened supported-app-running state and quota-value-known state, so start/stop transitions cannot reuse a stale quota-number color.
- The old Codex-only process check remains as a rollback helper, but the active quota path reads the shared snapshot.

Implementation update for the independent Claude Radar window:

- `ClaudeRadarForm` keeps its own cached `SoftwareRuntimePresence` snapshot and refreshes it on construction, settings apply, manual refresh, and the existing performance-mode timer.
- Claude Radar quota-number rendering reads only the cached snapshot. `DrawWindow`, `DrawContentLayer`, and `GetQuotaNumberColor` must not enumerate processes or query the foreground window.
- Claude Radar public website reads are not gated by local Codex/Claude process presence because IQ, efficiency, model catalog, service state, and quota-radar data are public web state. They remain gated only by enabled/visible/suspended state, single-flight, backoff, and test-mode rules.
- Claude Radar numeric quota text follows the same rule as Codex Radar: known values stay white when either Codex or Claude is running, and become gray only when both supported local apps are stopped.
- Claude Radar independently schedules Claude Code usage reads for its fixed Claude family when the window is enabled, visible, not suspended, not in random test mode, and the local Claude process is running. Success writes `claude-quota.ini` and updates the quota rings immediately; source failures preserve the last good numeric quota and update only the usage service state.
- Claude Code usage parsing must require both 5h and 7d percentage values before writing `claude-quota.ini`; partial API responses are treated as incomplete and preserve the last good cache.
- `claude-quota.ini` is written through one shared writer using a process-local lock, unique temporary file, and atomic replacement. Codex Radar Claude mode and the independent Claude Radar window must not maintain separate temp-file writers for the same cache file.
- Claude Radar website cache stores only site quota values and marks them with `QuotaSource=site`; personal Claude Code quota is overlaid after saving the website snapshot. Cache loads must not trust legacy quota fields without that source marker, so a stale personal snapshot cannot pollute the public-site fallback.
- Claude Radar now has a sequential homepage metadata fallback gated by `ClaudeRadarHomepageFallbackEnabled`. When the switch is off, it must not read `https://claudecoderadar.com/`; when it is on, it fetches that homepage only after the primary data JSON fails, `iq.models` is missing, or a model name is missing/only equal to its key. The fallback parses `MODEL_NAMES` for model key/name metadata only; it must not fabricate IQ, efficiency, quota, or reset values.
- Homepage-derived model catalogs are weak catalogs. They may add or name known model keys, but they do not prove absence, so the model-map updater must not increment missing counts or mark existing models temporarily missing/deleted from a homepage-only fallback.

Additional runtime audit notes:

- `OperationForm` foreground FPS and foreground memory probes are intentionally not tied to Codex/Claude presence. They are separate operation-panel features and must remain gated by their own visibility/settings/performance-mode rules.
- SeelenUI process checks are tied to the operation module's start/exit controls and should not be deduplicated into `SoftwareRuntimePresence`, which is scoped only to Codex/Claude family selection and quota display state.
- Network monitor, connection check, DeepSeek, and cloud endpoint readers are timed/service probes with their own consumers. Apply this optimization pattern only by skipping disabled/hidden/no-key/no-consumer cases, not by checking Codex/Claude process presence.
- Future additions should prefer one cached coarse state per subsystem and pass that state into drawing/layout code; drawing code must not trigger process enumeration, foreground-window inspection, network I/O, or parser refresh.

2026-07-05 implementation audit update:

- Startup, resume, test-mode exit, and data-source recovery must not prime both Codex provider usage and Claude Code usage. They must call the shared selected-provider gate, which first reads `SoftwareRuntimePresence` and then queues only the current effective family when its local app is running.
- `PrimeCodexProviderUsageRefresh` and `PrimeClaudeUsageRefresh` style helpers are disallowed in active scheduling code because they bypass selected-family and running-state gates. New refresh triggers should use `RequestSelectedQuotaUsageRefresh` or a narrower helper with the same preconditions.
- The old local Codex session fallback may remain for rollback and for selected Codex while Codex is running, but active UI scheduling must not read session logs when both supported apps are stopped or when the selected family is not running.
- `SoftwareRuntimePresence` is intentionally coarse. Cache keys and quota text color may use `any_supported_app_running`, but they must not include foreground window handles, process IDs, process titles, or command lines.
- Random-test modes must remain in-memory and must not write quota, model, history, notification, or service-health cache files. They may simulate running/stopped states for rendering only.
- DeepSeek balance, network monitor, connection check, and cloud endpoint readers are optimized with local no-key/disabled/hidden/no-consumer checks. They must not be made dependent on Codex/Claude process presence because they serve separate modules.

2026-07-05 model-selector implementation update:

- `Win11SettingsForm` now renders `ClaudeRadarModelKey` as a generated five-column button grid backed by `ClaudeRadarReader.LoadModelMap()`.
- Each row has five slots; trailing empty slots display disabled `--` buttons. The control recomputes slot width when the settings row narrows so the five-column layout stays inside the card at low resolutions.
- The `自动` slot remains selectable and maps to an empty model key. Active/enabled Claude source keys are selectable; pending, temporarily missing, deleted, or otherwise unavailable keys may be shown for continuity but are disabled.
- The mapping editor remains beside the selector as the source of display name, rating key, sort order, enabled flag, and status. Saving the editor rebuilds the grid without losing the current selected key.
- `--test-settings-bindings` now asserts the Claude model grid exists, uses a slot count divisible by five, keeps empty slots disabled, keeps unavailable model slots disabled, and preserves the responsive slot width policy.

2026-07-05 service-health and deletion-safety update:

- The compact Claude Radar service row now maps `R/C/U` to Claude Radar data/homepage, Claude public Statuspage summary, and Claude Code usage respectively. Community ratings remain a data source but no longer occupy the `C` service-health square.
- Claude public status is read from `https://status.claude.com/api/v2/summary.json` on the Claude Radar public-data refresh path only; the window still does not probe Codex, OpenAI, or ChatGPT.
- Service squares now distinguish the required visual semantics: normal green without a cross, offline gray without a cross, incomplete gray cross, unavailable yellow cross, and unreachable red cross.
- Model deletion is now gated by an explicit complete-catalog check. `ok=false`, missing `iq.models`, non-array/empty catalogs, duplicate/missing source keys, or a parsed-model count that differs from the raw model count are incomplete and must not increment `MissingSuccessCount`.
- Quota history self-tests now cover duplicate signature suppression, malformed JSONL line tolerance, and retention trimming. A bad history line is skipped without blocking later valid rows.
- `--render-clauderadar` now produces deterministic visual acceptance fixtures for `normal`, `missing-data`, `warning`, `error`, `offline`, and `test-randomized`, plus a 2880x1800 desktop screenshot for each state while keeping the legacy `clauderadar-evenrow.png` and `clauderadar-2880x1800.png` names.
- `--test-layout` now runs `ClaudeRadarForm.RunRenderResourceSelfTest`, which renders every acceptance state, asserts the output is nonblank, verifies the 6-entry scene-cache cap, and verifies render-buffer/cache disposal clears all local GDI+ state.
- `--diagnose-radar-runtime --diagnose-seconds N` now records current-process CPU, working set, private bytes, handle count, GDI objects, and USER objects into local `radar-runtime-diagnosis-*.txt/.json` reports.
- Unknown Claude Radar model metrics now render the time/token efficiency rings as `--` with muted labels instead of showing the default `100`, so missing-data/error fixtures do not imply valid business data.

2026-07-05 1.0.3.88 acceptance hardening:

- `--test` now calls `CodexRadarForm.RunSoftwareModeGateSelfTest`, covering fixed Codex/Claude modes, no supported app, single supported app, both-app foreground detection, foreground failure fallback, and selected-provider quota refresh targets.
- `ClaudeRadarReader.RunSelfTest` now includes failure-state fixtures for disabled JSON, HTTP failure, timeout, offline, and unsupported/unavailable responses.
- `ClaudeRadarReader.RunSelfTest` also uses a temporary storage root to prove Claude Radar writes only Claude-prefixed cache/model/quota/history files, does not read Codex sentinel cache files, and does not write storage from random test snapshots.
- `ClaudeRadarForm.RunRenderResourceSelfTest` now includes notification-state de-duplication fixtures for first notification, unchanged restart state, deletion, and deletion followed by re-addition.
- `ClaudeRadarForm.RunRenderResourceSelfTest` now includes last-good failure merge and public refresh single-flight fixtures, so failed public data requests preserve existing business data while applying the newest service error state.
- Remaining Claude Radar spec gap is now running the heavier live performance matrix with that diagnostic entrypoint: only-Codex, only-Claude, both windows, high-frequency switching, and longer open/close or suspend/resume runs.

2026-07-05 1.0.3.89 runtime diagnostics hardening:

- `--diagnose-radar-runtime` now accepts `--diagnose-target-pid <pid>` and `--diagnose-label <name>` so acceptance runs can sample the already-running formal DesktopCodexAssistant process instead of only sampling the short-lived diagnostic command process.
- Diagnosis reports now include the target process name, target PID, and normalized scenario label in both text and JSON outputs.
- Live target-PID samples collected after formal deployment:
  - `only-codex-live`: 60 seconds against PID 37876 with `ClaudeRadarEnabled` absent/default false; `cpu_avg=0.17`, `cpu_max=0.60`, `working_set_mb=109.7`, `private_mb=68.3`, `handles_delta=3`, `gdi_delta=0`, `user_delta=4`.
  - `both-window-live`: 60 seconds against PID 61488 after temporarily enabling `ClaudeRadarEnabled=True`; `cpu_avg=0.20`, `cpu_max=0.60`, `working_set_mb=111.2`, `private_mb=69.4`, `handles_delta=59`, `gdi_delta=0`, `user_delta=5`.
  - The original settings file was restored after the both-window sample and the formal E target restarted as PID 38904 with FileVersion/ProductVersion `1.0.3.89` and SHA256 `08CEE7D46C48C5EC6C600A3A3578EA8D4F1ED65D76F17FB42C94AD38D840E85A`.

2026-07-05 1.0.3.90 independent Radar lifecycle update:

- `WidgetSettings.CodexRadarEnabled` is now persisted and exposed in the Codex Radar settings page. The default is `true`, so existing installs keep the current Codex Radar window unless the user explicitly disables it.
- `WidgetForm` now creates or closes Codex Radar and Claude Radar child windows from their independent enable flags. This makes only-Codex (`CodexRadarEnabled=true`, `ClaudeRadarEnabled=false`), only-Claude (`CodexRadarEnabled=false`, `ClaudeRadarEnabled=true`), and both-window (`true/true`) production runtime sampling expressible without code changes.
- `ClaudeRadarForm.RunRenderResourceSelfTest` now includes a 120-cycle high-frequency scene switch over six deterministic acceptance snapshots. It asserts the six warm scenes are drawn once, subsequent switches hit the scene bitmap cache, and the cache never exceeds six entries.
- `--test-settings-open-close --iterations <n>` repeatedly opens and closes the Win11 settings window, collecting handle/GDI/USER deltas to detect settings UI leaks.
- `--test-radar-display-lifecycle --iterations <n>` creates Codex and Claude Radar handles without showing production windows, disables remote data sources, repeatedly calls display suspend/resume on both child windows, and checks handle/GDI/USER deltas.
- Release validation used `Start-Process -Wait -PassThru` for the GUI subsystem executable. ARM64 Release tests passed: `--test`, `--test-layout`, `--test-settings-bindings`, `--test-settings-open-close --iterations 200`, `--test-radar-display-lifecycle --iterations 100`, `--test-display-recovery`, and `--test-logger`.
- Resource stress evidence: settings open/close 200 iterations reported `handles_delta=15`, `gdi_delta=0`, `user_delta=0`; radar display lifecycle 100 iterations reported `handles_delta=0`, `gdi_delta=0`, `user_delta=0`.
- Release render evidence: `--render-clauderadar` produced 14 files and `--render-codexradar` produced 9 files under `_build`; visual inspection covered the 2880x1800 Claude warning fixture and the Codex EvenRow fixture.
- Formal deployment evidence: D and E formal targets both report FileVersion/ProductVersion `1.0.3.90` and SHA256 `230AFC18FB491F8439AA7A47BF5EB7E37CA110BD3931E8F367EB77DCC07BFA54`; backup directory `_build/formal-backups/20260705-150313-radar-lifecycle-1.0.3.90`; final restored E target PID `60040`.
- Live target-PID samples collected after formal deployment:
  - `only-codex-live-1-0-3-90`: 60 seconds against PID 69436 with default settings; `cpu_avg=0.17`, `cpu_max=0.60`, `working_set_mb=110.5`, `private_mb=69.2`, `handles_delta=-39`, `gdi_delta=0`, `user_delta=0`.
  - `only-claude-steady-1-0-3-90`: 60 seconds against PID 55444 after `CodexRadarEnabled=false`, `ClaudeRadarEnabled=true`, and a 15-second warm-up; `cpu_avg=0.09`, `cpu_max=0.43`, `working_set_mb=103.9`, `private_mb=63.9`, `handles_delta=-30`, `gdi_delta=0`, `user_delta=3`.
  - `both-window-steady-1-0-3-90`: 60 seconds against PID 78296 after `CodexRadarEnabled=true`, `ClaudeRadarEnabled=true`, and a 15-second warm-up; `cpu_avg=0.15`, `cpu_max=0.52`, `working_set_mb=117.9`, `private_mb=80.2`, `handles_delta=1`, `gdi_delta=0`, `user_delta=2`.
  - Immediate post-start only-Claude and both-window samples showed one-time handle/GDI/USER growth while child windows were being created; the warmed samples above are the steady-state acceptance evidence.

## Data Reader

Add a dedicated reader, for example `ClaudeRadarReader`, with single-flight request paths:

| Source | Endpoint | Purpose | Normal cadence | Failure cadence |
| --- | --- | --- | ---: | ---: |
| Claude Radar data | `https://claudecoderadar.com/data/claude-code-radar.json` | IQ, model table, quota calibration, site updated time | Beijing hourly | 10 min |
| Claude community ratings | `https://claudecoderadar.com/api/model-ratings?history=14` | RC/community score equivalent | 15 min or endpoint `refresh_seconds` | 5 min |
| Claude homepage | `https://claudecoderadar.com/` | Optional script/display metadata fallback only | follow data refresh only when needed | 10 min |
| Claude Code usage | existing Claude Code OAuth usage reader | Personal 5h/7d quota rings | reuse current Claude quota cadence | reuse current Claude quota failure cadence |

Rules:

- Use independent locks and request-running flags.
- No network I/O on the UI thread.
- Slow requests must not pile up on timer ticks.
- Use no-store/no-cache headers and bounded timeouts.
- Request failure preserves last successful Claude business data.
- Parser failure on one optional submodule must mark only that submodule incomplete, not clear the full snapshot.

## Model Mapping Table

Create an adjustable persisted mapping table, for example:

`%LOCALAPPDATA%\DesktopCodexAssistant\claude-radar-model-map.ini`

Each row should include:

- `source_key`: Claude Radar internal key, for example `m1`.
- `display_name`: human-readable model name, for example `Opus 4.8 high`.
- `rating_key`: optional community API key, for example `opus48_high`.
- `color`: persisted or generated display color.
- `sort_order`: UI order.
- `enabled`: whether the model can be selected.
- `historical_only`: whether current data marks it as historical only.
- `status`: `active`, `pending`, `temporarily_missing`, or `deleted`.
- `last_seen_utc`.
- `missing_success_count`.

Discovery rules:

1. Read `iq.models` and `iq.table.cols` from `claude-code-radar.json`.
2. Use homepage `MODEL_NAMES` only as a weak fallback if `ClaudeRadarHomepageFallbackEnabled` is on and the JSON model list is missing or incomplete.
3. Read `api/model-ratings` semantic IDs separately.
4. Never auto-merge different key spaces just because names look similar. New or ambiguous mappings become `pending`.
5. If an existing model is missing from one successful response, mark `temporarily_missing`; after three consecutive successful responses where it is still missing, mark `deleted` and notify once.
6. If a deleted model reappears, restore it as `active` and notify once.
7. The settings UI must allow editing `display_name`, `rating_key`, `sort_order`, and `enabled`. It must not allow two active rows to share the same `source_key`.

2026-07-05 mapping-safety implementation update:

- `ClaudeRadarReader.UpdateModelMap` no longer derives `rating_key` from display names such as `Opus 4.8 high`.
- Newly discovered source keys are saved as `pending` with an empty `rating_key`, even when their display name could be normalized to an existing community-rating ID.
- Existing rows become `active` only when they already contain an explicit non-empty `rating_key` and the ratings API either confirms that key or is unavailable for this refresh. If the ratings API is available and the explicit key is absent, the row remains or returns to `pending`.
- Disabled rows resolve to `disabled`; temporarily missing/deleted rows still follow the consecutive-success missing threshold and reappeared notification flow.
- `ClaudeRadarReader.SaveModelMap` and the internal map writer normalize enabled rows without a `rating_key` to `pending`, so the mapping editor cannot accidentally persist an ambiguous row as `active`.
- `ClaudeRadarReader.RunSelfTest` includes in-memory model-map tests proving that a name-matchable new model does not auto-merge, the same row activates after an explicit `rating_key` is supplied, an invalid explicit key returns to `pending`, and two same-name catalog rows keep distinct `source_key` entries.

2026-07-05 post-review hardening:

- Persisted rows with `enabled=true`, empty `rating_key`, and stale `status=active` are normalized back to `pending`.
- The settings selector and legacy combo fallback also require a non-empty `rating_key` before a Claude model source key can be selected.

Defect boundary:

- A wrong user mapping can still display the wrong community score next to a model. To limit damage, cache keys must always include both data source and source key, for example `ClaudeRadar.IQ.m1` and `ClaudeRadar.Rating.opus48_high`.

## IQ And Efficiency

Use the Claude site data as the source of truth when available:

- Task count is currently `10`.
- Score follows the same observed family as Codex Radar: `passed / valid_tasks * 150` when raw score is absent.
- Normal band must be read from the site data if exposed. If no band is available, fallback to the current observed Claude chart band, not Codex's band.
- IQ label rules remain: below band `降智`, inside band `常态`, above band `增智`.
- IQ ring allows values above 100.
- Historical-only models should be selectable only if the user explicitly enables them or selects them before they become historical-only; otherwise keep them visible but disabled.

Efficiency:

- Time efficiency can use table/history duration fields if available.
- Token efficiency can use current `总tokens` values. If historical token fields are absent, show current-only efficiency and mark dynamic baselines unavailable rather than fabricating history.
- Baseline modes should mirror Codex Radar: absolute, previous seven days, previous thirty days, and all-record average. If a data family lacks enough historical fields, the UI must show why that baseline is unavailable.
- The existing test controls should be mirrored with Claude-specific keys and must not write to Codex test state.

## Quota Rings

Personal quota rings should reuse the existing Claude Code usage path already used by the Codex Radar software switch:

- 5h ring maps to Claude Code `five_hour`.
- 7d ring maps to Claude Code `seven_day`.
- Cache file remains separate from Codex, for example existing `%LOCALAPPDATA%\DesktopCodexAssistant\claude-quota.ini`.
- Ring number color follows the shared local-app presence rule above: white when either Codex or Claude is running, gray only when both supported apps are stopped.
- Consumption-ring logic should reuse the Codex/Claude isolated quota application code, but its state must be keyed by software family and window so the new Claude Radar window does not steal baselines from the Codex Radar window.

If the user is not logged in to Claude Code or the usage API fails, preserve the last good snapshot if it is still valid and show the failure in status text/logs. Do not gray a valid numeric ring solely because the source failed while either supported local app is running.

## Quota Radar Line

The site's public quota object currently exposes partial calibration fields:

- `quota.base_h5`
- `quota.base_d7`
- `quota.base_h5_trend`
- `quota.base_d7_trend`
- `quota.usage`
- `quota.cal.run_id`
- `quota.updated_at`

Implement a local seven-day fallback history:

`%LOCALAPPDATA%\DesktopCodexAssistant\claude-radar-quota-history.jsonl`

Each row:

- `schema_version`
- `timestamp_utc`
- `source_updated_at`
- `run_id`
- `metric`: `base_h5` or `base_d7`
- `value`
- `source_url`

Append rules:

1. Append only when `source_updated_at` or `run_id` changes.
2. Keep raw rows for 30 days for diagnostics.
3. Render from rows whose `timestamp_utc` is within the last seven days.
4. If the site later provides at least two trend points, prefer the site trend but still keep local rows for fallback.
5. If only one local sample exists, draw the value at the vertical midpoint and do not draw a colored delta segment.
6. If `max == min`, use a padded range centered on the value to avoid division by zero and visual noise.
7. If a single obvious transport/parser failure occurs, do not append a null or zero row.

Rendering rules:

- The line is vertical like the current Codex quota-radar line.
- In fallback mode, top maps to the highest local seven-day value and bottom maps to the lowest local seven-day value.
- Average line uses the average of rendered seven-day rows.
- Colored segment connects previous sample to current sample.
- Current sample gets the blue point.
- Keep the existing color rules unless implementation finds that Claude's data semantics make them misleading:
  - above average and above previous: green
  - above average but below previous: light green
  - below average but above previous: yellow
  - below average and below previous: orange
  - below previous seven-day minimum: red
  - above previous seven-day maximum: gold

Known drawback:

- With a sliding seven-day min/max, the same numeric value can move vertically as old samples expire. This is acceptable for the first version but should be documented in the UI or architecture notes.

## Service Health Summary

Mirror the compact API summary behavior instead of restoring the hidden three-row service panel.

Candidate services:

- `Rader`: Claude Radar data endpoint and optional homepage fallback.
- `Claude`: Anthropic public status or existing Claude health reader if available.
- `ClaudeCode`: personal Claude Code usage endpoint if the user is in a mode that needs personal quota rings.

Status meanings should match the network monitor cloud-service square semantics:

- normal: green or neutral text, no cross.
- offline: gray.
- incomplete: gray cross when a required element cannot be parsed from an otherwise reachable source.
- unavailable: yellow cross when the service responds but does not provide usable data.
- unreachable: red cross for DNS, TLS, timeout, or connection failure.

Do not probe Codex, OpenAI, or ChatGPT from the Claude Radar window unless a shared lower-level network state already exists.

## Settings

Add a `Claude Radar` settings area with:

- Enable/disable window.
- Position, display, opacity, size, hover behavior, and render variant where applicable.
- Model selector generated from the Claude model mapping table. Use the same button-grid style as Codex: five slots per row; empty slots show `--` and are disabled.
- Mapping table editor.
- Data-source switches:
  - Claude Radar JSON
  - homepage metadata fallback
  - community ratings
  - local seven-day quota fallback
- Test mode:
  - realtime/test toggle
  - manual random refresh
  - auto random refresh
  - per-model forced IQ/efficiency/quota samples
- Manual service availability check.

New settings must cover defaults, clone, load, save, normalization, migration version, settings UI, and `--test-settings-bindings`.

## Cache And Isolation

Expected local files:

- `%LOCALAPPDATA%\DesktopCodexAssistant\claude-radar-cache.ini`
- `%LOCALAPPDATA%\DesktopCodexAssistant\claude-radar-model-map.ini`
- `%LOCALAPPDATA%\DesktopCodexAssistant\claude-radar-quota-history.jsonl`
- existing `%LOCALAPPDATA%\DesktopCodexAssistant\claude-quota.ini`

Rules:

- Cache keys must include `ClaudeRadar`.
- Model caches must include `source_key`.
- Community rating caches must include `rating_key`.
- Do not read Codex Radar cache as a fallback.
- Do not let Codex Radar read Claude Radar cache as a fallback.
- Test snapshots are in-memory only and must not write caches.
- JSONL cache writes must be bounded, rolling, UTF-8, and validated by self-tests.

## Notifications

Windows notifications:

- New Claude model discovered.
- Existing Claude model deleted after the missing-success threshold.
- Deleted Claude model reappeared.
- Claude Radar data endpoint changed schema enough that required fields become unavailable for the selected model.

Notifications must be deduplicated by event key and last-seen state, not by display text alone.

## Implementation Plan

1. Add data models and `ClaudeRadarReader`.
2. Add local cache and model mapping table with parser/writer tests.
3. Add `ClaudeRadarForm` by reusing generic drawing helpers from Codex Radar where possible.
4. Split Codex-specific drawing helpers only where required to avoid copy/paste drift.
5. Wire `WidgetForm` lifetime, suspend/resume, display positioning, settings preview, and shutdown.
6. Add settings UI and model mapping editor.
7. Add service checks, notifications, and test mode.
8. Update documentation, feature index, interface index, component refresh rules, maintenance log, and version.
9. Build ARM64, run self-tests, render screenshots, then deploy/restart only when the executing goal requires formal overwrite.

## Acceptance Criteria

Functional:

1. A separate Claude Radar window can be enabled and positioned independently.
2. The window does not change the existing Codex Radar window layout or data.
3. Claude IQ data renders from `/data/claude-code-radar.json`.
4. The model selector is generated dynamically from Claude data.
5. The adjustable mapping table can map `m*` keys to community-rating semantic keys.
6. Ambiguous models enter `pending` instead of auto-merging.
7. New/deleted/reappeared model notifications fire once per state change.
8. Personal Claude quota rings use Claude usage data and remain isolated from Codex quota cache.
9. Claude quota-radar line uses site trend when available and local seven-day fallback when the site trend is incomplete.
10. The seven-day fallback does not append duplicate rows for the same `updated_at` or `run_id`.
11. Min/max fallback handles one-sample and equal-min-max cases without division by zero.
12. The service summary distinguishes unreachable, unavailable, incomplete, offline, and normal states.
13. Test mode can randomize the full Claude Radar window without writing real caches.
14. Closing, display suspend, session lock, and app exit release all GDI and background resources.

Safety:

1. No UI-thread network I/O.
2. All background snapshots are cloned before UI drawing.
3. All remote requests are single-flight.
4. Failed requests preserve the last successful business snapshot.
5. Claude and Codex caches cannot cross-read by accident.
6. JSONL history is bounded and does not log tokens, credentials, or raw auth responses.
7. Settings migration cannot silently enable the new window for existing users unless the product already defaults to showing new modules.

## Acceptance Plan

The implementation is accepted only when every gate below has explicit evidence. A gate that depends on live public data may be marked `blocked_by_remote` only when the local parser, cache, fallback, and render tests use recorded fixtures that cover the same schema shape.

1. Scope gate:
   - Confirm the diff is limited to Claude Radar, shared radar rendering helpers, settings, cache/log interfaces, documentation, indexes, and version files.
   - Confirm Dock, Launchpad, top bar, Direct2D, unrelated window layout, and unrelated settings are not changed.
   - Confirm the existing Codex Radar can still run by itself with the same default settings and no forced Claude Radar enablement.

2. Data acquisition gate:
   - Record one successful Claude Radar JSON fetch and one parsed fixture for each supported section: IQ, efficiency, quota radar, service status, and metadata.
   - Record one failure fixture each for HTTP failure, timeout, schema-missing, unsupported-region/unavailable, and partial-data responses.
   - Verify failed requests keep the last successful business snapshot while the service state reports the actual failure reason.
   - Verify all remote requests are background single-flight and do not block paint, settings save, or window dragging.

3. Model mapping gate:
   - Start with an empty mapping table and verify discovered Claude models appear as selectable generated buttons.
   - Add mappings for m1/m2/m3-style keys and verify they resolve to the intended community-rating keys.
   - Feed ambiguous, newly added, temporarily missing, deleted, and reappeared model fixtures.
   - Verify new/deleted/reappeared notifications fire once per state change and do not repeat after restart when state is unchanged.
   - Verify missing-success threshold prevents temporary API omissions from being treated as deletion.

4. Cache isolation gate:
   - Verify Claude Radar reads and writes only Claude-prefixed cache files and never falls back to Codex Radar cache.
   - Verify Codex Radar cannot read Claude Radar cache after switching windows, switching software mode, or restarting.
   - Verify test mode snapshots remain in memory and do not write real cache, quota, or history files.
   - Verify JSONL history retention, rolling bounds, UTF-8 line parsing, and duplicate suppression for identical `updated_at` or `run_id`.

5. Visual and interaction gate:
   - Capture desktop screenshots at 2880x1800 for Codex Radar and Claude Radar, including normal, missing-data, warning, error, and test-randomized states.
   - Compare element positions against the current Codex Radar reference: same ring sizes, same text scale policy, same spacing rules, and software-family border only.
   - Confirm Codex uses the deep-blue 3 px inner border and blue italic brand text; Claude uses the orange 3 px inner border and orange bold brand text.
   - Confirm no text is clipped in Chinese or English labels, and no module overlaps after DPI scaling and opacity changes.
   - Confirm window enable/disable, positioning, display suspend/resume, session lock/unlock, settings preview, and tray/settings interactions do not leak handles.

6. Performance gate:
   - Measure idle CPU with only Codex Radar enabled, only Claude Radar enabled, and both enabled.
   - Measure forced high-frequency visual switching with the scene bitmap cache enabled and confirm paint work does not run remote I/O or full parser refresh.
   - Confirm memory added by pre-rendered scene caches stays comfortably below the user's 100 MB tolerance.
   - Confirm timers back off on failure, skip hidden/disabled windows, and stop during suspend or app exit.
   - Confirm no GDI object count growth after at least 10 minutes of normal refresh plus repeated settings open/close.
   - Verify Auto software mode does not call foreground-window detection when fixed mode is selected, when neither app is running, or when only one supported app is running.
   - Verify foreground-window detection is called only when both Codex and Claude are running, and failure keeps the previous effective family.
   - Verify quota ring numbers are gray only when the quota value is unknown or in the `codex_running=false && claude_running=false` state; they remain white for numeric values when either app is running.
   - Verify network-change/manual-refresh/resume triggers enqueue only providers with an active visible consumer and do not touch inactive provider queues.
   - Verify recurring identical `both_apps_stopped` polls do not append quota decision log rows.

7. Release gate:
   - Run the verification commands below and record exit codes.
   - Validate feature index, interface index, technical index, maintenance log, and all new JSONL fixture/history files line by line.
   - Append maintenance history with version, changed files, validation evidence, residual risks, and token usage source.
   - Build ARM64 release, back up existing formal executables, overwrite only after tests pass, then restart and confirm the running process path, version, hash, and responsiveness.

Verification:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-claude-radar.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-claude-radar.exe --test
.\_build\DesktopCodexAssistant-arm64-claude-radar.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-claude-radar.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-claude-radar.exe --test-settings-open-close --iterations 200
.\_build\DesktopCodexAssistant-arm64-claude-radar.exe --test-radar-display-lifecycle --iterations 100
.\_build\DesktopCodexAssistant-arm64-claude-radar.exe --test-logger
.\_build\DesktopCodexAssistant-arm64-claude-radar.exe --render-clauderadar --out .\_build\claude-radar-render
```

Also required:

- Validate `Docs/Indexes/FEATURE_INDEX.jsonl`.
- Validate `Docs/Interfaces/INTERFACE_INDEX.jsonl`.
- Validate `Docs/Maintenance/CHANGELOG.jsonl`.
- Validate any new Claude Radar JSONL cache test fixture.
- Run `git diff --check`.
- Inspect at least one 2880x1800 screenshot because the user's main screen is 2880x1800.

## Documentation Updates Required During Implementation

- `Docs/Component-Refresh-Rules.md`: Claude Radar timer, single-flight, cache, failure retry, and suspend/resume rules.
- `Docs/Indexes/FEATURE_INDEX.jsonl`: new `claude_radar.*` feature rows.
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`: new external API rows for Claude Radar data/rating endpoints and new file formats.
- `Docs/Maintenance/CHANGELOG.jsonl`: verified implementation record.
- A new or updated Claude Radar architecture document under `Docs/`.

## Open Constraints

- Claude Radar may add or remove fields without a versioned schema. Parser code must be defensive.
- The community rating API uses a different key namespace from IQ data. The mapping table is necessary, but user edits can still be wrong.
- Local seven-day quota fallback is not the same as a complete public trend. It represents only successful app observations.
- If the app is not running for several days, the local quota radar will have holes.
- If the site publishes only one quota point, visual trend direction is unavailable.
- If token history remains latest-only, token efficiency baselines should be marked unavailable instead of guessed.
