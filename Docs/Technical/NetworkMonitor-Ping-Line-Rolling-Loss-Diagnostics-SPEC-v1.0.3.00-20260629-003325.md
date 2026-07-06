# Network Monitor PING Line Rolling Loss Diagnostics SPEC

Spec version: `1.0.3.00`
Created at: `2026-06-29T00:33:25+09:00`
Target module: `网络监控窗口`
Primary files:

- `Performance/NetworkMonitorReader.cs`
- `Performance/PdhModels.cs`
- `Core/NetworkMonitorForm.cs`
- `Settings/WidgetSettings.cs`
- `Docs/NetworkMonitor-Architecture.md`
- `Docs/Component-Refresh-Rules.md`
- `Docs/Indexes/FEATURE_INDEX.jsonl`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`
- `Docs/Maintenance/CHANGELOG.jsonl`

## Goal

Improve the `PING` row from a coarse 4-packet ICMP snapshot into a rolling, low-impact connectivity quality display:

1. Show more precise packet loss in the `PING` line using a rolling sample window instead of a single 4-packet round.
2. Add a right-side status indicator on the `PING` row that identifies the likely failing segment when high latency or packet loss is detected.
3. When the app has determined that the network is effectively inside the wall / global internet is blocked, fall back from global public ping targets to Baidu ping targets.
4. Update cloud endpoint tiles and alerts: remove Tencent/Aliyun, add Azure/Akamai, use `Cf Ak Gi Aw Az Go` order, hide cloud alerts outside `Online`, and use provider-specific public status feeds/APIs.
5. Add an IPv6-row right-side DNS alert strip that rotates DNS errors when any DNS status is yellow or worse.
6. Preserve existing network monitor invariants: no UI-thread network I/O, background snapshots are cloned, stale results cannot overwrite a newer network generation, and GFW/cloud probes remain independently scheduled.

## Existing Context

Relevant indexed items:

- `network_monitor.loss_tolerant_probes`
- `internal_api.network_monitor_reader`
- `internal_api.gfw_probe_reader`
- `internal_api.cloud_endpoint_probe`
- `external_api.microsoft.connecttest`
- `external_api.gfw.probe_hosts`
- `event.network.change`
- `logging.network_check_history`

Current behavior:

- `NetworkMonitorReader.MeasureConnectivity()` pings `1.1.1.1` four times.
- `PacketLossPercent` therefore has coarse 25% steps.
- `BuildConnectivityText()` renders a single `loss N%` value in the `PING` row.
- Existing `LocalNetworkDegraded` uses thresholds: loss `>= 15%`, jitter `>= 250 ms`, latency `>= 800 ms`.
- GFW detection can already produce explicit suspected DNS/TCP/TLS/SNI/HTTP blocking states.
- Cloud endpoint tiles currently display six services and a header-side cloud alert; the new target set and alert visibility rules supersede the previous `Al` and `Tx` tiles.
- DNS status already exists per DNS server and is rendered in the DNS row; the new requirement adds a separate right-aligned rotating alert on the IPv6 row.

## Non-Goals

- Do not integrate `packetlosstest.com` or depend on its WebSocket/WebRTC servers.
- Do not require a VPS, private server, paid API, or new external daemon.
- Do not run high-frequency probes against a single public endpoint.
- Do not change GFW probe cadence or cloud endpoint probe cadence except where their existing status is read to choose the active PING target profile.
- Do not use logged-in Azure Service Health APIs. Azure must use the public Azure Status RSS feed only.
- Do not keep Tencent Cloud or Aliyun cloud endpoint tiles in the six-tile cloud strip after this change.
- Do not add user settings in the first implementation unless a setting is strictly required; prefer conservative built-in defaults.

## Target Behavior

### PING Row Text

Replace the coarse single-round loss text with rolling statistics:

```text
OK PUB | 32ms | jitter 4ms | loss 1.7% (1/60)
OK BAIDU | 28ms | jitter 3ms | loss 0.0% (0/60)
FAIL PUB | ICMP不可用
```

Rules:

- `loss` must show one decimal place when sample count is sufficient.
- Include count detail as `(lost/total)` so the precision is visible.
- If fewer than 10 samples are available, show `warming` or `采样中` rather than implying precision.
- If HTTP/NCSI confirms online but public ICMP has zero successful samples across the active rolling window, show `ICMP不可用` instead of `loss 100%`.
- Existing `AccessState` remains authoritative for online/offline/captive portal classification.

### Right-Side Status Indicator

Add a compact right-side status area to the `PING` row, similar in spirit to the existing cloud endpoint alert area. It must not overlap the main PING text.

Suggested statuses:

| Status text | Severity | Condition |
| --- | --- | --- |
| `LOCAL LOSS` / `本地丢包` | warning/error | Default gateway rolling loss exceeds threshold. |
| `LOCAL LAT` / `本地高延迟` | warning | Default gateway rolling latency or jitter exceeds threshold. |
| `WAN LOSS` / `公网丢包` | warning/error | Gateway is healthy, global public target group has packet loss. |
| `WAN LAT` / `公网高延迟` | warning | Gateway is healthy, global public target group has high latency or jitter. |
| `GLOBAL BLOCK` / `墙内` | warning | Existing GFW snapshot is an explicit suspected block status. |
| `BAIDU LOSS` / `百度丢包` | warning/error | Baidu fallback group is active and degraded. |
| `BAIDU LAT` / `百度高延迟` | warning | Baidu fallback group is active and high latency/jitter. |
| `ICMP BLOCK` / `ICMP禁用` | neutral/warning | HTTP/NCSI is online but all active ICMP targets fail while gateway is healthy. |
| `CAPTIVE` / `需验证` | warning | `AccessState.NeedsValidation`. |
| `OFFLINE` / `离线` | error | `AccessState.Offline`. |
| `ADAPTER` / `网卡` | error | `AccessState.AdapterMissing`. |

Display priority:

1. `AdapterMissing`
2. `NeedsValidation`
3. `Offline`
4. Default gateway loss/high latency
5. Explicit GFW suspected block
6. Active public or Baidu target group loss/high latency
7. ICMP blocked
8. No status text when healthy

The status text should be short enough for compact window widths. If space is constrained, prefer abbreviated English tokens over clipping Chinese text.

### Rolling Sample Windows

Implement rolling ICMP samples as background state owned by `NetworkMonitorReader`.

Required sample groups:

1. `Gateway`: current default gateway for the selected adapter.
2. `Public`: rotating public anycast targets:
   - `1.1.1.1`
   - `1.0.0.1`
   - `8.8.8.8`
   - `8.8.4.4`
   - `9.9.9.9`
   - `149.112.112.112`
3. `Baidu`: fallback group used only after wall/global-block classification:
   - `www.baidu.com`

Sampling cadence:

| Performance mode | Total rolling ping interval | Notes |
| --- | ---: | --- |
| `Smooth` | 2 seconds | Rotate public targets; gateway may be checked every cycle or every other cycle. |
| `Balanced` | 5 seconds | Default behavior. |
| `BatterySaver` | 10 seconds | Avoid aggressive probing on battery saver. |

Window size:

- Keep up to 60 samples per group by default.
- Store both successes and failures.
- Clear rolling windows when selected adapter identity, primary IP, default gateway, or `networkGeneration` changes.
- Do not clear rolling windows for DNS order-only changes.
- Samples older than 15 minutes should be discarded even if the window is not full.

Latency and jitter:

- Latency is the arithmetic mean of successful samples.
- Jitter is the average absolute difference between adjacent successful samples in that group.
- A group with fewer than 3 successful samples has unknown jitter.

Loss:

- Loss percent = failed samples / total samples.
- Do not classify loss until `total >= 10`.
- Warning threshold: loss `>= 2%`.
- Error/degraded threshold: loss `>= 10%`.
- Keep the existing `LocalNetworkDegraded` threshold of `>= 15%` unless implementation explicitly updates docs and tests.

High latency:

- Gateway high latency warning: average `>= 30 ms` or jitter `>= 20 ms`.
- Public target high latency warning: average `>= 300 ms` or jitter `>= 120 ms`.
- Baidu fallback high latency warning: average `>= 150 ms` or jitter `>= 80 ms`.
- Existing severe `LocalNetworkDegraded` thresholds remain available for upper-layer false-positive gating.

### Wall / Baidu Fallback

Define `inside wall` / global-block active when all are true:

1. `AccessState` is `Online`.
2. `GfwProbeSnapshot.Status` is one of:
   - `SuspectedDns`
   - `SuspectedTcp`
   - `SuspectedTlsSni`
   - `SuspectedHttp`
3. The GFW snapshot is not stale according to the existing GFW reader rules.

When active:

- The `PING` row active target profile changes from `PUB` to `BAIDU`.
- Public rolling samples may continue at low cadence only if already implemented cheaply, but the displayed loss/latency must use Baidu samples.
- The right-side status should show `GLOBAL BLOCK` / `墙内` unless Baidu fallback itself is degraded, in which case show `BAIDU LOSS` or `BAIDU LAT`.
- Do not use Baidu fallback to suppress or recolor GFW/cloud endpoint results. It is only for the `PING` row quality display.

When inactive:

- The active target profile is `PUB`.
- Baidu fallback sampling should stop or be heavily throttled.

### ICMP Block Classification

If HTTP/NCSI is online and the gateway has successful samples, but the active public/Baidu target group has `0` successful samples across at least 10 total samples:

- Show `ICMP不可用` in the PING text.
- Show `ICMP BLOCK` / `ICMP禁用` on the right.
- Do not mark `AccessState` as offline.
- Do not set `LocalNetworkDegraded` solely from this condition.

### Data Model

Add fields to `NetworkMonitorSnapshot` or a nested cloneable model, for example:

```csharp
internal sealed class PingRollingSnapshot
{
    public string ActiveProfile { get; set; }       // PUB, BAIDU
    public string ActiveTargetLabel { get; set; }   // current target label for display
    public int SampleCount { get; set; }
    public int LostCount { get; set; }
    public double LossPercent { get; set; }
    public double LatencyMs { get; set; }
    public double JitterMs { get; set; }
    public bool StatsReady { get; set; }
    public bool IcmpBlocked { get; set; }
    public string DiagnosisText { get; set; }
    public PingPathDiagnosis Diagnosis { get; set; }
    public PingDiagnosisSeverity Severity { get; set; }
}
```

Clone rules:

- All nested sample snapshots returned to UI must be deep-copied.
- The raw rolling sample queues may remain private mutable reader state and must not be exposed to UI.

### Reader Scheduling

Add one single-flight rolling ping task path:

- Do not block `GetSnapshot()`.
- Do not run more than one rolling ping task at a time.
- Record `networkGeneration`, `InterfaceId`, primary IP, and default gateway signature at task start.
- Discard task results if identity changed before commit.
- Use `Ping.Send()` or async equivalent from a background task only.
- Keep each individual ping timeout short, recommended `1000 ms` for public/Baidu and `500 ms` for gateway.

Avoid duplicating work:

- `MeasureConnectivity()` may continue to run NCSI and coarse ping during transition, but final display should use rolling stats when ready.
- If the rolling sampler has enough samples, `MeasureConnectivity()` should reuse those stats for `LatencyMs`, `JitterMs`, and `PacketLossPercent`, or at minimum avoid contradicting the PING row.

### UI Rendering

Modify `NetworkMonitorForm`:

- Split the PING row into a main text area and a right-side status area.
- The right status area should have fixed or measured width, then the main text should fit into remaining space.
- Add diagnosis fields to `HasSameDisplayData()` so status changes trigger redraw.
- Use existing `DesignTokens` colors:
  - healthy/no text: no status or muted
  - warning/high latency/ICMP blocked/wall: warning color
  - severe loss/offline/adapter: danger color
- Keep text inside bounds at compact window sizes. If needed, shorten the status text before reducing core PING value visibility.

### Cloud Endpoint Tiles and Alerts

Replace the cloud endpoint set and order with:

| Tile | Provider | Source | Normal color |
| --- | --- | --- | --- |
| `Cf` | Cloudflare | Existing Cloudflare Statuspage summary source | Same green as other normal overseas services |
| `Ak` | Akamai | `https://www.akamaistatus.com/api/v2/summary.json` | Same green |
| `Gi` | GitHub | Existing GitHub Statuspage summary source | Same green |
| `Aw` | AWS | Existing AWS lightweight HTTP/status check | Same green |
| `Az` | Azure | Azure Status public RSS, preferred endpoint `https://azure.status.microsoft/en-us/status/feed/` | Same green |
| `Go` | Google Cloud | Existing Google Cloud public incidents JSON | Same green |

Provider changes:

- Remove `Al` / Aliyun and `Tx` / Tencent from the displayed cloud endpoint strip.
- `Google Cloud` must display as `Google` in alert text when it reports an error; tile label remains `Go`.
- Azure must parse the public RSS feed and classify feed items. It must not use APIs that require Azure login or tenant-specific authorization.
- Akamai must use its v2 public status API. Start with `summary.json`; implementation may also use `incidents.json` or `scheduled-maintenances.json` if needed for better reason text.
- All normal cloud endpoint tiles, including `Az` and `Ak`, use the same normal green. Do not preserve the old domestic light-green treatment for removed Aliyun/Tencent entries.

Cloud alert visibility:

- When `AccessState` is not `Online`, cloud endpoint detection must stop or be cancelled according to existing cancellation rules.
- When `AccessState` is not `Online`, hide the header-side cloud alert area entirely.
- Non-Online cloud tiles may remain hidden or rendered muted according to existing layout constraints, but they must not display stale red/orange/yellow provider alerts.
- Returning to `Online` may resume cloud probing according to existing probe cadence and identity-change rules; do not reset cadence repeatedly on transient `Unknown` unless existing NetworkChange identity rules require it.

Cloud status mapping:

| Provider/source status | Display status | Alert reason |
| --- | --- | --- |
| Statuspage `indicator=none`; component `operational`; no active incident in selected region | Normal | none |
| Statuspage `indicator=minor`; component `degraded_performance`; incident `impact=minor` | Slow/Abnormal depending latency and incident text | `性能下降` or incident summary |
| Statuspage component `partial_outage` or incident `impact=major` | Abnormal | `部分中断` or incident summary |
| Statuspage component `major_outage` or page `indicator=critical` | Down/Abnormal | `重大中断` or incident summary |
| Statuspage maintenance `scheduled`, `in_progress`, or equivalent | Abnormal if active in selected region; otherwise informational/no alert | `计划维护` |
| Azure RSS has no current items | Normal | none |
| Azure RSS item title/description indicates outage, service issue, impact, unavailable, or degradation | Abnormal/Down by severity keywords | `服务异常`, `服务中断`, or item title summary |
| Azure RSS item indicates advisory or maintenance only | Abnormal or informational warning | `状态公告` or `计划维护` |
| Google Cloud `status_impact=SERVICE_DISRUPTION` or high/medium severity in selected region | Abnormal/Down by severity | `服务中断` or incident summary |
| Google Cloud `status_impact=SERVICE_INFORMATION` or low severity | Abnormal warning only when selected region matches; otherwise no alert | `服务公告` |
| Source fetch DNS/TCP/TLS/timeout failure | Down unless local network is degraded; then existing local-degraded downgrade may apply | existing transport failure reason |

Region behavior:

- Respect the existing settings entry that selects official cloud status regions.
- Cloudflare and Google Cloud keep their current region matching behavior.
- Azure RSS does not provide a stable per-region structured field in the public feed; match selected regions by title/description keywords when possible. Items with no identifiable region must be treated as global to avoid missing broad outages.
- Akamai Statuspage components/incidents may not provide user-region IDs; treat global Akamai incidents as relevant. If affected component names or incident text indicate a selected region, use that match to prioritize the alert reason.
- Region filtering must never hide a clearly global or unscoped official outage.

Cloud alert text rotation:

- Alert names must use short provider labels: `Cloudflare`, `Akamai`, `Github`, `AWS`, `Azure`, `Google`.
- For Google errors, show `Google!`, not `Google Cloud!`.
- Continue rotating multiple provider alerts, but suppress this entire alert area when `AccessState` is not `Online`.

### DNS Alert Strip

Add a right-aligned DNS alert strip on the IPv6 row, below the six cloud endpoint tiles:

- The strip occupies the right side of the `IP6` row and must not overlap the IPv6 value.
- It is active when any DNS server status is yellow or worse: `Problem`, `Hijacked`, `Unavailable`, or equivalent future non-normal status.
- It rotates through each problematic DNS server one at a time.
- The prefix number is the one-based display order from left to right in the current DNS server list: `DNS1`, `DNS2`, `DNS3`, etc.
- Text examples:
  - `DNS1污染`
  - `DNS2无法连接`
  - `DNS3异常`
  - `DNS1仅TCP`
- Color must match the DNS status color already used in the DNS row.
- Unlike cloud alerts, do not alternate between provider/name and reason. DNS can fit both identifier and reason in one text, so each rotation frame is a complete message.
- If text still exceeds available width, use the existing fitted/ellipsis text behavior; do not expand or shift the window layout.

DNS reason mapping:

| DNS status/reason | DNS alert text |
| --- | --- |
| `Hijacked` or NXDOMAIN returns an address | `DNSn污染` |
| `Unavailable` due to timeout or UDP/TCP no response | `DNSn无法连接` |
| `Problem` because only TCP works | `DNSn仅TCP` |
| `Problem` because DNS returned error/no answer/NXDOMAIN verification abnormal | `DNSn异常` |
| `Unknown` | No alert unless explicitly promoted to warning elsewhere |

### Logging

When a rolling window changes diagnosis state, log one network check history entry:

- module: `network_monitor`
- check_name: `rolling_ping`
- trigger: `状态变化`, `网络身份变化`, `定时间隔`, or `墙内回退`
- result: compact diagnosis summary
- success: true unless adapter/offline/ICMP blocked with no usable target
- detail:
  - `active_profile`
  - `sample_count`
  - `lost_count`
  - `loss_percent`
  - `latency_ms`
  - `jitter_ms`
  - `diagnosis`

Do not log resolved public IP addresses, gateway addresses, DNS server addresses, or full host response details.

Packet-loss confirmation logging:

- When rolling loss first reaches a confirmed warning/error state, write a `network_monitor` / `rolling_ping_loss_confirmed` history entry.
- Confirmation requires at least 10 samples and at least two consecutive rolling evaluations above the warning threshold, or one evaluation above the error/degraded threshold with at least 20 samples.
- Include `active_profile`, `group`, `sample_count`, `lost_count`, `loss_percent`, `latency_ms`, `jitter_ms`, and `diagnosis`.
- Write a recovery entry when the same group returns below warning threshold for at least two consecutive evaluations.
- Do not write every sample; log only state transitions or confirmations to avoid high-volume logs.

### Documentation and Index Updates

Implementation must update:

- `Docs/NetworkMonitor-Architecture.md`
  - Section 7 latency/jitter/loss description.
  - Network state and GFW/Baidu fallback behavior.
  - Cloud endpoint target order, Azure/Akamai source behavior, non-Online cloud suppression, and DNS alert strip behavior.
- `Docs/Component-Refresh-Rules.md`
  - Rolling ping cadence by performance mode.
  - Single-flight and network-event clearing rules.
  - Cloud endpoint stopping/hiding behavior outside `Online`.
  - DNS alert rotation behavior if it introduces a timer/rotation dependency.
- `Docs/Indexes/FEATURE_INDEX.jsonl`
  - Update `network_monitor.loss_tolerant_probes` or add a child feature such as `network_monitor.rolling_ping_diagnostics`.
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`
  - Update `internal_api.network_monitor_reader`, `internal_api.snapshot_models`, and `external_api.gfw.probe_hosts` reuse notes if the GFW status is consumed for fallback.
  - Add or update external entries for Baidu ping fallback, Azure Status RSS, Akamai Status v2 API, and Google Cloud alert naming/region behavior as appropriate.
- `Docs/Maintenance/CHANGELOG.jsonl`
  - Append a verified change entry.

If any new setting is added, implementation must also update:

- defaults
- clone
- load/save
- normalization
- settings UI
- migration version
- `--test-settings-bindings`

## Acceptance Criteria

Functional:

1. The PING row displays rolling loss with one decimal precision and `(lost/total)` once enough samples exist.
2. Loss no longer jumps only in 25% steps under normal rolling operation.
3. Gateway loss or high latency is reported as local/router/Wi-Fi side status.
4. Public target loss or high latency is reported as public/WAN side status when gateway is healthy.
5. Existing explicit GFW suspected statuses switch the active PING profile to Baidu.
6. While Baidu fallback is active, the PING line clearly shows `BAIDU` and uses Baidu rolling statistics.
7. If HTTP/NCSI is online but ICMP is unusable, the row says `ICMP不可用` instead of offline or `100% loss`.
8. UI remains stable at compact sizes and the right-side status does not overlap the main PING text.
9. Cloud endpoint tile order is exactly `Cf Ak Gi Aw Az Go`.
10. `Al` and `Tx` tiles are gone, and `Az`/Azure plus `Ak`/Akamai are present.
11. All normal cloud endpoint tiles use the same green normal color.
12. Google Cloud alerts display `Google`, not `Google Cloud`.
13. When network status is not `Online`, cloud endpoint probing stops/cancels and the header-side cloud alert area is hidden.
14. Azure status uses the public Azure Status RSS feed and does not use login-required Azure APIs.
15. Akamai status uses the public v2 status API and maps page, component, incident, and maintenance states to visible alert reasons.
16. Cloud status alerts respect the existing region selection setting, with unscoped/global official outages still shown.
17. The IPv6 row right side displays a rotating DNS alert when DNS status is yellow or worse, with text such as `DNS1污染` or `DNS2无法连接`.
18. DNS alert colors match the corresponding DNS row status colors.
19. DNS alert rotation shows complete DNS identifier plus reason in one frame and does not alternate separate name/reason frames.

Safety:

1. No network I/O runs on the UI thread.
2. Rolling ping tasks are single-flight.
3. Stale samples from a previous network identity cannot commit after network changes.
4. Public targets are rotated; the app must not repeatedly hammer a single public IP.
5. Baidu probing only runs when fallback is active, or at a very low warm-up cadence if implementation justifies it.
6. No raw IP/gateway/DNS addresses are written to `network-check-history.jsonl`.
7. Cloud endpoint work is not started while `AccessState` is `Offline`, `AdapterMissing`, `NeedsValidation`, or non-stable `Unknown`.
8. DNS alert rendering is display-only and does not start extra DNS probes.
9. Packet-loss confirmation logs are transition-based and cannot grow on every sample.

Verification:

1. ARM64 build succeeds:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-1.0.3.00-rolling-ping.exe -Platform arm64
```

2. Required executable checks:

```powershell
.\_build\DesktopCodexAssistant-arm64-1.0.3.00-rolling-ping.exe --test
.\_build\DesktopCodexAssistant-arm64-1.0.3.00-rolling-ping.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-1.0.3.00-rolling-ping.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-1.0.3.00-rolling-ping.exe --test-logger
```

3. Add or extend internal self-tests to cover:

- rolling loss percentage math
- insufficient sample warm-up state
- ICMP blocked classification
- gateway degraded vs public degraded diagnosis priority
- GFW suspected status selecting Baidu profile
- stale generation result discard
- clone isolation for new snapshot fields
- cloud tile order and provider replacement
- non-Online cloud alert hiding and cloud probe suppression
- Google alert name shortening
- Azure RSS status classification
- Akamai v2 status classification
- region filtering for scoped and global cloud incidents
- DNS alert reason mapping, color mapping, and rotation
- packet-loss confirmation and recovery history logging

4. Validate JSONL files line by line:

- `Docs/Indexes/FEATURE_INDEX.jsonl`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`
- `Docs/Maintenance/CHANGELOG.jsonl`

5. Run `git diff --check` on touched files.

## Open Constraints

- Public DNS IPs are anycast targets. They improve precision for general public-network quality but do not prove a specific geographic route such as Singapore.
- Baidu is also not a stable precision benchmark. It is only the requested fallback signal when the app has already determined that global internet probing is blocked.
- The implementation should keep current GFW/cloud endpoint meaning intact; the new PING row status is a local display aid, not a replacement for those modules.
