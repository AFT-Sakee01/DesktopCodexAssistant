# GoalSpec: Radar Hardening Follow-Up

## Metadata

- Goal thread: `019f4ed4-da61-7833-8bf6-66fd935a228a`
- Goal: execute the Radar hardening follow-up SPEC and close implementation, verification, ARM64 deployment, overnight observation, and documentation.
- SPEC: `Docs/Technical/Codex-RadarHardeningFollowUp-SPEC-v1.0.5.05-20260711-132944.md`
- SPEC SHA-256: `01B902632DDE61FC894373FE3F381127B99EE96A26E16D9497ED849A70DE212D`
- Implementation version: `1.0.5.06`
- Final operational audit: `2026-07-12T10:06:29+09:00`
- Generated model: Codex
- Status: implemented

The repository and formal runtime had advanced to `1.0.5.15` before the delayed overnight audit completed. This document records the historical `1.0.5.06` implementation and deployment. Current source inspection confirms that the F1, F3, and F6 logic remains present in the later tree; no later code was reverted for this closure.

## Requirement Mapping

| Requirement | Implementation and evidence | Result |
| --- | --- | --- |
| F1 rejected-persistence repair | Added separate in-memory 5-hour and weekly `RejectedIdentityPersistenceState` instances per software family. Three matching rejected samples spanning at least 10 minutes accept from the third sample with `reset_confirmed_by_rejected_persistence`; accepted samples, changed identities, runtime resets, and process restarts clear evidence. Existing acceptance branches and the 30-minute gap fallback remain higher priority. Three deterministic replay tests passed. | PASS |
| F2 SuccessSoft declaration | Built an isolated primary-`Success` alpha-255 QA variant and compared it with the production `SuccessSoft` alpha-255 build. The QA inspection lost sibling content on the PArgb current sample, while `SuccessSoft` retained the complete 522x120 frame. Production keeps `SuccessSoft`; the refresh marker remains `Success` alpha 245. | PASS |
| F3 dead `LocalTime` field | Removed `RadarClockDialInput.LocalTime` and assignments in the Codex, Claude, and clock self-test inputs. Targeted search has no field or assignment remaining. | PASS |
| F4 phantom-pool attribution | Compared all five captured identity-change responses. The last three top-level primary windows exactly match the Spark additional pool while their secondary windows do not, proving a hybrid top-level provider view. | PASS |
| F5 restart marker evidence | The first deployment pull changed real content and correctly advanced `RefreshedUtc`; a second restart with stable content preserved both `ContentSignature` and `RefreshedUtc` exactly while `SavedUtc` advanced. | PASS |
| F6 expected statusline-cache noise | `ClaudeCodeUsageScheduler.ShouldLogCompletion` logs the first `NO_STATUSLINE_CACHE`, suppresses repeats, and resets only after success. The compiled self-test passed. During the `.06` runtime interval, the log contains one `NO_STATUSLINE_CACHE` completion at `2026-07-11 13:56:24Z`; later entries are the separate `.07+` `NO_SETUP_TOKEN` behavior. | PASS |
| ARM64 release | Candidate and Release test matrices passed, Release/D/E matched, the D formal process restarted and responded, and the old executables/cache were backed up. | PASS |
| Overnight F1 observation | 20.163 hours and 1,313 quota decisions were audited across the preserved F1 implementation. Five interference samples were rejected; the maximum still-rejected persistence count was 1 for both windows, with zero count-at-least-3 violations. | PASS |

## Implementation Architecture

### Quota Identity Repair

`Core/CodexRadarForm.RuntimeState.cs` owns two non-persistent trackers in each `QuotaRuntimeState`: `FiveHourRejectedIdentity` and `WeeklyRejectedIdentity`. `Core/CodexRadarForm.cs` passes the appropriate tracker into `EvaluateQuotaWindowIdentity`.

The decision order is:

1. Accept the existing unknown-anchor, identity-same, expiry, newborn, corroborating-event, session, or gap-rebaseline branches and clear rejected evidence.
2. Otherwise classify the sample as `interference_pool_sample_ignored` and track its reset identity using the existing two-minute identity tolerance.
3. If the same rejected identity reaches `QuotaRejectedPersistenceMinSamples = 3` across at least `QuotaRejectedPersistenceMinMinutes = 10`, accept it as `reset_confirmed_by_rejected_persistence`, rebuild the normal baseline, and clear the tracker.

Decision logs expose count and first-seen UTC independently for the 5-hour and weekly windows. The repair evidence is deliberately in memory so a process restart cannot turn stale diagnostic history into acceptance authority.

### Scheduler Log Suppression

`Core/ClaudeCodeUsageScheduler.cs` keeps one process-local `noStatusLineCacheCompletionLogged` flag under `SyncRoot`. It affects only completion-log emission. It does not change refresh cadence, reader invocation, setup-token discovery, OAuth requests, cache writes, or caller outcomes.

### Clock Input Cleanup

The shared clock still receives `LocalKnown`, `BatchTimeLocal`, `RefreshMarkerTimeLocal`, and the last-attempt/last-actual timestamps it consumes. Removing `LocalTime` changes no rendering or state calculation.

## F2 Pixel Evidence

| Variant | Artifact | SHA-256 | Inspection |
| --- | --- | --- | --- |
| Production `SuccessSoft` alpha 255 | `_build/radar-hardening-followup-v1.0.5.06-successsoft/codexradar-current.png` | `A9BA1E7E4CB113D7F276CB7F8801CD40A2F7C9A9574411A27E09FC0FE86B7222` | Complete 522x120 frame, including five rings, footer, clock, and LEDs. |
| QA primary `Success` alpha 255 | `_build/radar-hardening-followup-v1.0.5.06-success-qa/codexradar-current.png` | `1586DE9E0935BDA278F6DC1F1E48946AAD55FC89ECCD0BB4C79F8D68874D0BC6` | PArgb inspection loses already-drawn sibling content; only the right-side dial/border remains visible. |

The production source was restored immediately after the QA build. `RadarClockDial.GetPhaseColor(CurrentCycle)` and its deterministic self-tests use opaque `DesignTokens.Colors.SuccessSoft`.

## F4 Raw Provider Comparison

Every capture contains additional pool `GPT-5.3-Codex-Spark` / `codex_bengalfox`.

| Capture | Top primary reset/used | Spark primary reset/used | Primary match | Top secondary reset/used | Spark secondary reset/used | Secondary match |
| --- | --- | --- | --- | --- | --- | --- |
| `20260711-113203` | `1783742703/78` | `1783755124/0` | no | `1784329503/12` | `1784341924/0` | no |
| `20260711-113312` | `1783747036/4` | `1783755193/0` | no | `1784333836/1` | `1784341993/0` | no |
| `20260711-130721` | `1783760842/0` | `1783760842/0` | yes | `1784329503/16` | `1784347642/0` | no |
| `20260711-131224` | `1783761145/0` | `1783761145/0` | yes | `1784329503/16` | `1784347945/0` | no |
| `20260711-131646` | `1783761406/0` | `1783761406/0` | yes | `1784329503/16` | `1784348206/0` | no |

Conclusion: the provider can project an additional-pool primary window into top-level `rate_limit` while retaining a base-pool secondary window. There is no stable response field that identifies the top-level pool, so production continues to use reset identity and corroborating evidence rather than hard-coded pool names.

## F5 Restart Evidence

Predeployment selected model: `Codex.Model.gpt_56_sol_medium`.

- Before deployment: `RefreshedUtc=2026-07-11T02:32:03.3609807Z`; signature represented `7.10_n`.
- First `.06` pull: real content changed to `7.11_pm`; `RefreshedUtc` correctly advanced to `2026-07-11T04:54:21.5286139Z`.
- Stable-content restart before: signature `639193248000000000|12|7.11_pm|7|10|105|green|165|42|7|7975292|1504|-1|-1|135`, `RefreshedUtc=2026-07-11T04:54:21.5286139Z`.
- Stable-content restart after: the same signature and `RefreshedUtc`; `SavedUtc` advanced to `2026-07-11T04:55:18.3132439Z`.

This proves that restart alone does not move the green refresh marker when site content is unchanged.

## Overnight Production Audit

Audit source: `%LOCALAPPDATA%\DesktopCodexAssistant\quota-decision-history.jsonl`.

- Window: `2026-07-11T04:55:04Z` through `2026-07-12T01:04:51.051552Z` (`20.163` hours).
- Decisions: `1,313`.
- `interference_pool_sample_ignored`: `5`.
- Maximum persistence count while still rejected: 5-hour `1`, weekly `1`.
- Rejected samples with either count at least 3: `0`.
- `reset_confirmed_by_rejected_persistence`: `0`, because no real identity repeated long enough to require repair.

The five rejected samples used different or interrupted reset identities. The observation therefore proves the acceptance invariant: no same identity remained rejected for three consecutive persistence samples.

## Verification Evidence

Both the candidate and final `1.0.5.06` Release artifact passed:

```text
--test                                               exit 0
--test-layout                                        PASS
--test-settings-bindings                             exit 0
--test-radar-display-lifecycle --iterations 100      PASS; handles_delta=6, gdi_delta=0, user_delta=-1
--test-logger                                        PASS
--test-display-recovery                              PASS
```

Final sample generation produced 21 Codex/Claude PNGs. All were nonblank, and every 522x120 Radar sample contained effective pixels in both left and right regions. Targeted searches confirmed the persistence implementation and tests, the absence of the dead clock field/assignments, and the retained SuccessSoft color.

The first candidate compilation encountered one transient `csc.exe` process failure (`-2146232797`); an immediate isolated retry succeeded. It was not a source compilation diagnostic and did not recur in candidate or Release builds.

## Deployment Evidence

- Previous formal version: `1.0.5.05`, SHA-256 `E29CB0229FA7A4089706BB3EE3A2F0FBE0C54BDA0A7A2264A769862C09427ADD`.
- `.06` Release/D/E version: `1.0.5.06`.
- `.06` length: `1,808,384` bytes.
- `.06` SHA-256: `372B6C8567C851A40D2E5471AD8AD305C2D0F7BF76FA885F1949261EE2AC8F6C`.
- `.06` final deployment PID: `52496`, D formal path, responding, one process.
- Backup: `D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant\_build\formal-backups\20260711-135403-radar-hardening-followup-1.0.5.06`.

The formal product was subsequently upgraded by independent completed work. At final audit time Release/D/E are byte-identical `1.0.5.15` artifacts with SHA-256 `F7847767D11A3050D1D72DD808DF469C16F8702FA3354BFC62776CB1BACEF8A9`; the current D process is PID `89608` and responding. This supersession is not a `.06` deployment mismatch.

## Documentation and Indexes

- `Docs/Fable5-Data-Sources-And-Caching-Technical.md` records the hybrid provider view and online identity limits.
- `Docs/Claude-EvenRow-DialCard-Technical.md` records SuccessSoft state green versus Success marker green.
- `Docs/Indexes/FEATURE_INDEX.jsonl` records the rejected-persistence feature and reuse constraints.
- `Docs/Interfaces/INTERFACE_INDEX.jsonl` records family runtime state and scheduler log semantics.
- `Docs/Maintenance/CHANGELOG.jsonl` contains the `.06` visual declaration, behavior change, and deployment entries.

## Security and Compatibility

- Raw identity diagnostics remain body-only and contain no authorization header or token.
- Rejected-persistence state is not written to disk.
- F6 does not change credential access or network requests.
- No x64 artifact was built; project policy and this release used ARM64 only.

## Deviations and Remaining Limits

- Final GoalSpec generation was delayed until the required overnight production window existed.
- Later releases restarted the process during the 20-hour observation, but source inspection confirms that F1 remained present and decision records retained the new count fields. A restart only clears in-memory evidence, which is the specified behavior.
- Provider pool identity remains externally unstable; hard-coded pool-name selection would be unsafe.
- No required implementation, verification, deployment, or documentation item remains open for the `1.0.5.06` follow-up SPEC.
