# Radar Backend Reuse GoalSpec

适用版本：1.0.4.78

生成时间：2026-07-09 11:14:14 +09:00

## Goal

执行 `Docs/Technical/Codex-RadarBackendReuse-SPEC-v1.0.4.77-20260709-102446.md`，在保留共享 Codex Radar 窗口和独立 Claude Radar 窗口可同时显示的前提下，统一可复用后端请求、缓存写入、服务告警防抖和网络历史记录。

Spec SHA256：`5E600C8815D40A65E129783C0B5A5100B65764E6A41A4112C236636267A4CEDA`

## 需求映射

| Spec 要求 | 当前实现 |
| --- | --- |
| DeepSeek 统一为唯一后端 | `Core/DeepSeekBalanceMonitor.cs` 统一 key 读取、请求、解析、48h history、display text、alert、cache signature；共享 Codex Radar CLAUDE 模式和独立 Claude Radar 都通过 consumer id 加入同一 single-flight。 |
| Statuspage 统一 | `Core/StatuspageMonitor.cs` 统一 OpenAI/Claude `summary.json` 请求，按 serviceKey single-flight，正常 15 min、失败 2 min，尊重 `AiRequestProtection`。 |
| ClaudeRadar 公共数据 scheduler | `Core/ClaudeRadarSnapshotScheduler.cs` 包装 `ClaudeRadarReader.ReadSnapshot`，按 `selectedModelKey/json/homepage/rating/localQuotaFallback` key 合并同类请求，不同 key 并行，失败时保留 last-good display snapshot。 |
| 服务告警防抖统一 | `Core/ServiceAlertDebouncer.cs` 提供 checking 立即显示、非 checking 新错误 10s 后稳定、恢复立即清除、测试旁路；Codex/Claude 窗口保留各自状态容器。 |
| 网络历史统一 | `StatuspageMonitor`、`ClaudeRadarSnapshotScheduler`、`DeepSeekBalanceMonitor` 写 request-level `network-check-history.jsonl`，包含 `joined_consumers`；窗口 apply 路径不重复写同一请求。 |
| Claude cache 写入加固 | `ClaudeRadarReader.TrySaveCache` 经 `CacheLock`、内容相同跳过、temp 文件、`File.Replace` / `File.Move` 原子替换。 |
| 两窗口独立显示 | `WidgetForm` 生命周期不变；共享窗口只在 CLAUDE 模式消费 Claude 后端，独立 Claude Radar 不读取 Codex quota/reset/provider/CodexRadar.com。 |

## 关键模块

- `Core/DeepSeekBalanceMonitor.cs`：DeepSeek single-flight、history、alert、self-test。
- `Core/StatuspageMonitor.cs`：OpenAI/Claude Statuspage single-flight、状态映射输入、request-level 日志。
- `Core/ClaudeRadarSnapshotScheduler.cs`：ClaudeRadar 公共数据 single-flight、last-good fallback、request key hash 日志。
- `Core/ServiceAlertDebouncer.cs`：跨窗口复用防抖算法。
- `Core/CodexRadarForm.cs` / `Core/CodexRadarForm.RuntimeState.cs`：共享窗口接入 monitor/scheduler，防抖状态按软件族隔离。
- `Core/ClaudeRadarForm.cs`：独立 Claude 窗口接入 monitor/scheduler，保留窗口自己的显示缓存、scene cache 和防抖状态。
- `Core/ClaudeRadarReader.cs`：移除 ReadSnapshot 内部 Claude Statuspage 读取，强化 `claude-radar-cache.ini` 写入。

## 数据与日志

- `network-check-history.jsonl` 新增 request-level 来源：`statuspage_monitor/openai_status`、`statuspage_monitor/claude_status`、`claude_radar_backend/claude_radar_snapshot`、`deepseek_balance/deepseek_balance`。
- 记录字段只包含状态、耗时、错误码、request key hash 和 `joined_consumers`，不保存 API key、token、响应正文、账户标识、IP 或 DNS 详情。
- `deepseek-balance-history.jsonl` 仍只保存 `schema_version`、`timestamp_utc`、`balance_cny`，48h 滚动。
- `claude-radar-cache.ini` 使用 lock + temp + replace/move 写入，防止两个窗口并发或进程中断导致半文件。

## 验证证据

执行文件：`_build/DesktopCodexAssistant-arm64-radar-backend-reuse.exe`

已执行命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-radar-backend-reuse.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-radar-backend-reuse.exe --test
.\_build\DesktopCodexAssistant-arm64-radar-backend-reuse.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-radar-backend-reuse.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-radar-backend-reuse.exe --test-logger
.\_build\DesktopCodexAssistant-arm64-radar-backend-reuse.exe --test-radar-display-lifecycle --iterations 100
.\_build\DesktopCodexAssistant-arm64-radar-backend-reuse.exe --render-codexradar --out .\_build\radar-backend-reuse-codex
.\_build\DesktopCodexAssistant-arm64-radar-backend-reuse.exe --render-clauderadar --out .\_build\radar-backend-reuse-claude
```

证据摘要：

- ARM64 构建成功。
- `--test` 退出码 0，并覆盖 `ClaudeRadarSnapshotScheduler.RunSelfTest`、`StatuspageMonitor.RunSelfTest`、`DeepSeekBalanceMonitor.RunSelfTest`、`ServiceAlertDebouncer.RunSelfTest`。
- `--test-layout` 输出 `Layout scaling policy: PASS`。
- `--test-settings-bindings` 输出 settings fixture/full round-trip PASS。
- `--test-logger` 输出 `Logger storage policy: PASS`。
- `--test-radar-display-lifecycle --iterations 100` 输出 `PASS iterations=100 handles_delta=0 gdi_delta=0 user_delta=-1`。
- `--render-codexradar` 输出 `codexradar-current.png`，图像非空，布局仍为共享 Codex Radar EvenRow。
- `--render-clauderadar` 输出 `clauderadar-current.png`，图像非空，布局仍为独立 Claude Radar。

## Spec 偏离

- 未修改原 SPEC 正文；执行状态通过 `Docs/Technical/INDEX.jsonl` 标记为 `implemented`。
- 未新增 UI 元素、尺寸、坐标或颜色变更。
- 未移除历史中已存在但当前不再调用的旧私有状态页解析方法；行为路径已切换到 `StatuspageMonitor`，后续可做死代码清理。

## 文档与索引

已同步：

- `Docs/Component-Refresh-Rules.md`
- `Docs/CodexRadar-Architecture.md`
- `Docs/Codex-ClaudeRadar-Architecture.md`
- `Docs/Fable5-Data-Sources-And-Caching-Technical.md`
- `Docs/Indexes/FEATURE_INDEX.jsonl`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`
- `Docs/Maintenance/CHANGELOG.jsonl`
- `Docs/Technical/INDEX.jsonl`

## 限制

自动测试覆盖 single-flight、自测、渲染样本和生命周期资源边界；现场外部请求合并数量需在两个窗口同时运行且对应后端到期时通过 `network-check-history.jsonl` 观察 `joined_consumers` 进一步确认。当前实现已把请求级日志集中到 monitor/scheduler，窗口 apply 路径不再重复写同一类 request-level 记录。
