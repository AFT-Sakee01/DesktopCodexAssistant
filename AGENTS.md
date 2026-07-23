# Desktop Codex Assistant Project Rules

The global `C:\Users\GengH\.codex\AGENTS.md` rules apply. This file only records project-specific constraints and overrides; do not duplicate global rules or maintenance history here.

Current version: `2.0.0.17`

## Project AI

- `primary_model: Codex`
- `creator_model_exempt: Codex`
- Codex is the primary project AI. Codex may keep existing project document names without a model prefix; other models must prefix new generated documents with their model short name, for example `Dsv4-xxx.md`.

## Before Editing

- Read `README.md` and the technical document for the affected module.
- Keyword-search `Docs/Indexes/FEATURE_INDEX.jsonl` and `Docs/Interfaces/INTERFACE_INDEX.jsonl`; read only matching JSONL lines unless the index is missing, stale, or ambiguous.
- Keyword-search `Docs/Maintenance/CHANGELOG.jsonl` for maintenance context; do not open the full changelog at task start.
- Treat the worktree as concurrently edited. Preserve unrelated user changes and re-read touched files before applying a patch.
- Search the interface index before adding an API, service, command, event, persistent file, renderer, or reusable helper.

## Product Scope

- This branch is the ASUS UX3407N / UX3607O dedicated Windows on Arm edition.
- ARM64 is the default architecture for builds and tests.
- Do not compile, publish, or validate x64 unless the user explicitly requests x64.
- Keep the product identity `Desktop Codex Assistant UX3407N/UX3607O`, executable name `DesktopCodexAssistant.exe`, and storage root `%LOCALAPPDATA%\DesktopCodexAssistant`.
- Dock, Launchpad, top bar, and the Direct2D project are intentionally disabled. Do not restore or depend on them.
- The canonical visible topology is eleven independent right-edge `MetricTileForm` tiles; seven left-edge dock tabs/boards (Network, Spec Board, Codex Task, GUARD, Codex IQ, Reset / Speed, System Day); `OperationForm`; and the on-demand settings window.
- Global layout editing exposes exactly 19 structural items: the eleven tiles, the seven dock tabs, and Operation. Boards, the settings window, hidden owners, and the hidden host are not editable layout items.
- `WidgetForm` is a hidden coordination host. `CodexRadarForm` and `PowerThermalForm` are permanent headless data owners started and stopped explicitly; the runtime must not call `Show()` for them.
- `NetworkMonitorForm` is Dock-only. Runtime Radar and Power/Thermal presentation belongs to the right tiles, and Clean IP presentation belongs to the Network board; do not create additional surfaces for those owners/readers.
- `ClaudeRadarForm` and `ConnectionCheckForm` are removed; retain only the official `ClaudeCodeUsageReader`/`ClaudeCodeUsageScheduler` quota chain and the Clean IP reader through their current owners.
- The supported render CLIs are `--render-networkmonitor`, `--render-tilecolumn`, `--render-operation`, and the board renderers. Do not add render entrypoints for hidden hosts or headless owners.
- Do not describe this branch or its artifacts as a generic Windows hardware monitor.

## Runtime Invariants

- `WidgetForm` owns coordination for settings, refresh, fullscreen visibility, suspend/resume, display recovery, and visible-surface/headless-owner lifetime while remaining hidden itself.
- Visible layered surfaces must reuse `NativeMethods.LayeredBitmapSurface`, `UiFontCache`, `DesignTokens`, and `BurnInProtection`; release and recreate rendering resources across display suspend/resume. Headless owners do not allocate presentation buffers or participate in hover, positioning, burn-in, or Z-order work.
- `CodexRadarForm` keeps Codex public Radar state isolated from the Claude family, which owns only official Claude quota and service-health state; both publish cache-only snapshots. `PowerThermalForm.BuildStripSnapshot()` is also cache-only. System Day may persist cloned performance and Power/Thermal snapshots, but snapshot construction and board drawing must not start I/O or sampling.
- `PowerThermalIntegratedEnabled` is compatibility-only and hidden from the settings UI; it must not control visibility, lifetime, or sampling. Main display/work-area settings remain active as the right-tile layout baseline.
- Background readers publish cloned snapshots. UI code must not mutate reader-owned state or synchronously block on network work.
- GFW probing and cloud endpoint probing remain independently scheduled; a GFW result must not suppress or recolor cloud probe results.
- New settings must cover defaults, clone, load, save, normalization, settings UI, migration version, and `--test-settings-bindings`.
- Persistent runtime data belongs under `%LOCALAPPDATA%\DesktopCodexAssistant`, not beside the executable.
- Power and thermal behavior is device-family-specific. Generic fallbacks must not silently replace UX3407N / UX3607O calibrated behavior.
- Never hand-guess fixed pixel Y offsets/heights for WinForms `Label`/text-adjacent controls (dialogs, custom rows, etc.) — actual rendered font metrics on the user's machine are routinely taller than assumed and rows silently overlap. Compute each control's height from its actual font via `Win11SettingsForm.GetSingleLineHeight`/`GetWrappedTextHeight` (or `TextRenderer.MeasureText`) and accumulate the next control's Y from the previous control's measured height, the same way `OpenClaudeSetupTokenDialog` and `SettingRow` do it. This applies to any manually laid-out `Form`/`Panel`, not just Settings.

## Verification

- Use the narrowest relevant checks first, then deploy by the default rule below whenever the change affects source code or runtime behavior.
- Relevant executable checks include `--test`, `--test-logger`, `--test-layout`, `--test-settings-bindings`, and `--test-display-recovery`.
- Formal builds must use the exact `Build-Sources.json` source set. Before a formal deployment, run `Build-Arm64.ps1 -RequireTrackedSources` from a local commit so untracked source cannot enter the executable.
- For documentation-only changes, JSONL parsing, path/reference checks, version checks, and `git diff --check` are sufficient.
- After completed source-code or runtime-affecting changes, build ARM64, back up the existing formal executable, overwrite the formal executable, and restart it by default unless the user explicitly says this turn should not compile, overwrite, deploy, or restart.
- Never overwrite the formal executable merely to validate documentation or metadata.

## Records

- Documentation naming, index schemas, changelog format, doc lifecycle, and the docs validation gate are governed by `Docs/AGENTS.md`; follow it for any `Docs/**` or index change.
- Append completed changes and confirmed issues to `Docs/Maintenance/CHANGELOG.jsonl`; never place maintenance entries back in this file.
- Keep `Core/ProductIdentity.cs`, artifacts, and each new changelog record on the same version.
- Update `Docs/Indexes/FEATURE_INDEX.jsonl` when a feature is added, moved, renamed, deprecated, or its recommended tests change.
- Update `Docs/Interfaces/INTERFACE_INDEX.jsonl` when an indexed interface or resource is added, changed, migrated, or deprecated.
- Update `Docs/Component-Refresh-Rules.md` when component refresh intervals, timer ownership, manual refresh tokens, network-event invalidation, single-flight behavior, cooldowns, test refresh behavior, or suspend/resume refresh rules change.
- Historical free-form records migrated from the old `AGENTS.md` are preserved in each changelog object's `legacy_text`.
