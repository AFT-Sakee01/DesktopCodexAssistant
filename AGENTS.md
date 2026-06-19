# Desktop Codex Assistant Project Rules

The global `C:\Users\GengH\.codex\AGENTS.md` rules apply. This file only records project-specific constraints and overrides; do not duplicate global rules or maintenance history here.

Current version: `1.0.2.80`

## Before Editing

- Read `README.md`, the latest relevant entries in `CHANGELOG.jsonl`, `Docs/INTERFACE_INDEX.jsonl`, and the technical document for the affected module.
- Treat the worktree as concurrently edited. Preserve unrelated user changes and re-read touched files before applying a patch.
- Search `Docs/INTERFACE_INDEX.jsonl` before adding an API, service, command, event, persistent file, renderer, or reusable helper.

## Product Scope

- This branch is the ASUS UX3407N / UX3607O dedicated Windows on Arm edition.
- ARM64 is the default architecture for builds and tests.
- Do not compile, publish, or validate x64 unless the user explicitly requests x64.
- Keep the product identity `Desktop Codex Assistant UX3407N/UX3607O`, executable name `DesktopCodexAssistant.exe`, and storage root `%LOCALAPPDATA%\DesktopCodexAssistant`.
- Dock, Launchpad, top bar, and the Direct2D project are intentionally disabled. Do not restore or depend on them.
- Do not describe this branch or its artifacts as a generic Windows hardware monitor.

## Runtime Invariants

- `WidgetForm` owns coordination for settings, refresh, fullscreen visibility, suspend/resume, display recovery, and child-window lifetime.
- Layered windows must reuse `NativeMethods.LayeredBitmapSurface`, `UiFontCache`, `DesignTokens`, and `BurnInProtection`; release and recreate rendering resources across display suspend/resume.
- Background readers publish cloned snapshots. UI code must not mutate reader-owned state or synchronously block on network work.
- GFW probing and cloud endpoint probing remain independently scheduled; a GFW result must not suppress or recolor cloud probe results.
- New settings must cover defaults, clone, load, save, normalization, settings UI, migration version, and `--test-settings-bindings`.
- Persistent runtime data belongs under `%LOCALAPPDATA%\DesktopCodexAssistant`, not beside the executable.
- Power and thermal behavior is device-family-specific. Generic fallbacks must not silently replace UX3407N / UX3607O calibrated behavior.

## Verification

- Use the narrowest relevant checks first; run ARM64 build or binary self-tests only when required by the change or requested by the user.
- Relevant executable checks include `--test`, `--test-logger`, `--test-layout`, `--test-settings-bindings`, and `--test-display-recovery`.
- For documentation-only changes, JSONL parsing, path/reference checks, version checks, and `git diff --check` are sufficient.
- Never overwrite the formal executable merely to validate documentation or metadata.

## Records

- Append completed changes and confirmed issues to root `CHANGELOG.jsonl`; never place maintenance entries back in this file.
- Keep `Core/ProductIdentity.cs`, artifacts, and each new changelog record on the same version.
- Update `Docs/INTERFACE_INDEX.jsonl` when an indexed interface or resource is added, changed, migrated, or deprecated.
- Update `Docs/Component-Refresh-Rules.md` when component refresh intervals, timer ownership, manual refresh tokens, network-event invalidation, single-flight behavior, cooldowns, test refresh behavior, or suspend/resume refresh rules change.
- Historical free-form records migrated from the old `AGENTS.md` are preserved in each changelog object's `legacy_text`.
