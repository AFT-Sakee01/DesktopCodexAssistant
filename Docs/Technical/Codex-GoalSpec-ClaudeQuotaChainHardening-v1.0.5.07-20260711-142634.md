# GoalSpec: Claude Quota Chain Hardening

## Goal And Spec

- Goal: execute the approved Claude quota-chain hardening Spec end to end, including implementation, tests, documentation, ARM64 deployment, and runtime verification.
- Spec: `Docs/Technical/Codex-ClaudeQuotaChainHardening-SPEC-v1.0.5.05-20260711-134303.md`
- Spec SHA-256: `FE6E4E0866DDB6FA5374A45EEDB7A4E15AC1590C37EAB2F40CBBA7EAC94C59C2`
- Implemented version: `1.0.5.07`
- Time: `2026-07-11T14:26:34+09:00` (`Asia/Tokyo`)

## Requirement Mapping

| Requirement | Implementation |
| --- | --- |
| F1 source identity and red reset labels | `ClaudeRadarQuotaSnapshot.Source` and `CodexQuotaSnapshot.SourceKind` propagate `site`/`claude_site_public` and personal sources. `ClaudeQuotaSourcePresentation` forces only public-site reset labels to Danger red in both Claude views. Scene-cache signatures include source. |
| F2 OAuth priority | `ClaudeCodeUsageReader.ResolveReadSources` selects OAuth first whenever an explicit setup token exists; non-authentication failure may fall back to statusline. |
| F3 personal-cache age | Both standalone and shared Claude cache readers require 5h, 7d, an explicit timestamp, and age no greater than 360 minutes. Tests cover 359, 361, and missing timestamp. |
| F4 actionable errors | No token plus unavailable statusline returns `NO_SETUP_TOKEN`. OAuth 401/403 returns `TOKEN_INVALID`; the settings page shows the rebind state from scheduler `LastErrorCode`. |
| F5 bounded Messages fallback | `ResolveSetupTokenResult` skips Messages headers for `TOKEN_INVALID`; only non-authentication failures enter the potentially billable fallback. |
| F6 source-switch baseline | Shared Claude quota state classifies site and personal sources, reinitializes delta tracking on cross-class changes, and logs `detail=source_switch`. |

## Architecture And Reuse

- Reused `ClaudeCodeUsageScheduler` for process-wide single-flight, cadence, cache writes, and error state.
- Reused `ClaudeRadarReader.TryWriteClaudeCodeQuotaCache` and the existing atomic INI format; no new persistence file was introduced.
- Reused `QuotaRingPresentation` and `ForceResetDisplayColor`; source policy remains in Claude callers through `ClaudeQuotaSourcePresentation`.
- Reused `InitializeQuotaReadDeltaTracking` rather than introducing a parallel delta engine.
- Updated feature index entries `claude_radar.window`, `claude_radar.claude_code_usage`, `codex_radar.claude_quota_cache`, and `codex_radar.service_health_quota_radar`.
- Updated Claude usage API, Messages fallback, personal/statusline cache, reader, and scheduler interface entries.

## Data, Logging, Safety, And Compatibility

- Personal quota remains in `%LOCALAPPDATA%/DesktopCodexAssistant/claude-quota.ini`; public-site quota is never written as personal quota.
- Token handling remains DPAPI CurrentUser through `SecretStore`; logs contain error/source summaries, not tokens or response bodies.
- 401/403 now prevents the Messages request, reducing both repeated failures and accidental quota spend.
- Site/personal source switches write the existing quota-decision JSONL schema with `detail=source_switch`; no schema-breaking field replacement was made.
- Codex-family quota colors and behavior are unchanged. Only ARM64 was built and deployed, per project rules.

## Verification Evidence

- ARM64 build: `_build/DesktopCodexAssistant-arm64-claude-quota-chain-v1.0.5.07.exe`.
- `--test`: exit `0`; covers OAuth-first selection, statusline fallback, `NO_SETUP_TOKEN`, `TOKEN_INVALID` Messages suppression, non-auth fallback, cache age boundaries, and source rebaseline.
- `--test-layout`: exit `0`.
- `--test-settings-bindings`: exit `0`; 229 persisted properties, 237 supported properties, 8 explicit exemptions.
- `--test-radar-display-lifecycle --iterations 100`: exit `0`; handles delta `0`, GDI delta `0`, USER delta `-1`.
- Final renders: `_validation/claude-quota-chain-v1.0.5.07-final`.
- Shared Claude site/personal pair: 628 differing pixels; reset-label region site-red count 538 versus personal 174.
- Standalone Claude site/personal pair: 720 differing pixels; reset-label region site-red count 603 versus personal 189.
- Release, E mirror, and D formal executable: version `1.0.5.07`, length `1817088`, SHA-256 `E3E8EA8B23D557C21E1B434A46A1F4B5F01DE06B3601D538E8358868DA3F7433`.
- Formal D instance: PID `72540`, one process, `Responding=True`.
- Formal backup: `D:/E_Drive_Files/Codexproject/desktopdata/DesktopCodexAssistant/_build/formal-backups/20260711-142606-claude-quota-chain-1.0.5.07`.

## Spec Deviations And Additional Findings

- The implementation also added quota source to both scene-cache keys. Without it, a source transition could reuse the previous reset-label color even when the data source had changed.
- The shared-window INI reader was hardened to the same timestamp and 360-minute rule as the standalone reader; otherwise the two Claude views could disagree on the same stale cache.
- The render fixtures now isolate background/content composition and use matched source-pair snapshots. This changes test evidence only, not production geometry.
- One intermediate layout run detected that eight scenarios exceeded the six-entry scene cache and produced no warm hits. The test was corrected to exercise six hot scenes while separately asserting that site and personal keys differ; the production cache limit remains six. The final matrix passed.

## Status

All F1-F6 requirements, documentation, indexes, ARM64 deployment, and final runtime checks are complete for `1.0.5.07`.
