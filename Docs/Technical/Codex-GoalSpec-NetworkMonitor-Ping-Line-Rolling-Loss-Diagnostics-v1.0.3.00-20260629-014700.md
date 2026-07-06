# GoalSpec: Network Monitor PING Line Rolling Loss Diagnostics

Version: `1.0.3.00`  
Completed at: `2026-06-29T01:47:00+09:00`  
Goal: `执行spec文件，一口气到底不要中断。结束后编译arm64并重启程序。`

## Spec

- Spec path: `Docs/Technical/NetworkMonitor-Ping-Line-Rolling-Loss-Diagnostics-SPEC-v1.0.3.00-20260629-003325.md`
- Spec SHA256: `2B097E51848048F602D496AEF0CED1E6A5329808E0F873C39ECAAC66B33C09E6`
- Generated model: `Codex`

## Requirement Mapping

- PING rolling loss: implemented in `NetworkMonitorReader` with gateway/public/Baidu sample windows, 60-sample cap, 15-minute TTL, 10-sample warm-up, one-decimal loss and `(lost/total)` UI text.
- PING right-side diagnostics: implemented in `PingRollingSnapshot` and `NetworkMonitorForm` with local, WAN, global block, Baidu, ICMP, captive, offline and adapter status priorities.
- Wall/Baidu fallback: explicit GFW suspected DNS/TCP/TLS/SNI/HTTP while `Online` switches active PING profile to `BAIDU`; it does not suppress GFW or cloud results.
- Cloud endpoints: order changed to `Cf Ak Gi Aw Az Go`; Aliyun/Tencent removed; Azure uses public Azure Status RSS; Akamai uses Statuspage v2 summary; Google alerts show `Google`.
- Non-Online cloud behavior: `CloudEndpointProbeReader` cancels/stops cloud work outside `Online`, returns no-alert `Unknown` endpoints, and UI hides the header-side cloud alert area.
- DNS alert strip: IPv6 row right side rotates complete `DNSn` messages using existing `DnsServerDetails`; no extra DNS probing.
- Logging: rolling ping diagnosis transitions plus loss confirmation/recovery write bounded `network-check-history.jsonl` entries without raw gateway, DNS or public target addresses.

## Key Modules

- `Performance/NetworkMonitorReader.cs`: rolling PING scheduler, windows, diagnosis, Baidu fallback, stale-result discard, history logging.
- `Performance/PdhModels.cs`: `PingRollingSnapshot`, diagnosis enums, updated cloud endpoint defaults.
- `Core/NetworkMonitorForm.cs`: PING text/status rendering, DNS alert strip, cloud alert hiding, display self-tests.
- `Performance/CloudEndpointProbe.cs`: Azure RSS and Akamai v2 status mapping, updated cloud target set and region filtering.
- `Performance/CloudEndpointProbeReader.cs`: non-Online cloud suppression and cancellation.
- `Core/NetworkCheckHistoryLogger.cs`: rolling loss logging self-test coverage.

## Index Reuse

- Updated `network_monitor.loss_tolerant_probes`.
- Added `network_monitor.rolling_ping_diagnostics`.
- Updated `internal_api.network_monitor_reader`, `internal_api.snapshot_models`, `internal_api.cloud_endpoint_probe_reader`, `external_api.cloud.health_targets`, `external_api.gfw.probe_hosts`.
- Added `external_api.baidu.ping_fallback`, `external_api.azure.status_rss`, `external_api.akamai.status_v2`.

## Validation

- ARM64 temporary build succeeded: `_build/DesktopCodexAssistant-arm64-1.0.3.00-rolling-ping.exe`.
- Required checks exited 0:
  - `--test`
  - `--test-layout`
  - `--test-settings-bindings`
  - `--test-logger`
- Additional display recovery check exited 0: `--test-display-recovery`.
- JSONL validation passed before maintenance append:
  - `FEATURE_INDEX.jsonl`: 11 lines, 11 unique feature IDs
  - `INTERFACE_INDEX.jsonl`: 77 lines, 77 unique IDs
  - `CHANGELOG.jsonl`: 105 lines before append
  - `Docs/Technical/INDEX.jsonl`: 2 lines before completion entry
- Release ARM64 build succeeded: `Release/DesktopCodexAssistant-arm64.exe`.
- Formal executable restarted from `E:\Codexproject\desktopdata\DesktopCodexAssistant\DesktopCodexAssistant.exe`, PID `5116`.
- Formal executable verified:
  - FileVersion/ProductVersion: `1.0.3.00`
  - PE Machine: `0xAA64`
  - SHA256: `72D2F2B9D98F86BDD6AA96D5998BE5AF74DC38EFA2DB291A72DC1878EDAC5A7A`

## Deviations And Limits

- No new user setting was added; cadence and thresholds are built-in as requested by the spec.
- Real ISP packet loss, real GFW blocking, and live Azure/Akamai incidents were not manually simulated. The branch behavior is covered by internal self-tests and source-specific status classification tests.
- Only ARM64 was built, consistent with project rules and the goal.
