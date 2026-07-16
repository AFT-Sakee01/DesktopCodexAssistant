# Codex Task Monitor Port SPEC（仅后端）

## Metadata

- Document: `Fable5-CodexTaskMonitorPort-SPEC-v1.0.5.39-20260716-193026.md`
- Generated model: Claude Fable 5
- Timestamp local: `2026-07-16T19:30:26+09:00`（撰写）；`2026-07-16T19:40:00+09:00` 范围修订：用户拍板本 spec 仅后端
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.39`
- Target implementation version: `1.0.5.40+`（执行 AI 按当时版本递增）
- Status: approved（范围已拍板：**仅后端**。前端展示层由另一份 SPEC 交低级别模型执行，本 spec 的 §4 数据契约就是两者的边界）
- Ledger registration: 已登记 `DesktopCodexAssistant.codex_task_monitor_port`（pending）
- Upstream source: [LH-03/codex-monitor-hud](https://github.com/LH-03/codex-monitor-hud) v2.0.2-preview，MIT 许可证。本 spec 移植其**解析逻辑与状态判定规则**（PowerShell → C#），不搬运其 WPF UI、MCP server、主题系统。

## Goal

给桌面挂件补上"**Codex 任务级实时状态**"的后端能力：从本地 `%USERPROFILE%\.codex\sessions` rollout JSONL 增量解析出每个活跃 Codex 会话的七态状态（运行中 / 等待输入 / 闲置 / 出错 / 已完成 / 已中止 / 暂停）、任务标签与 Token 计量，以线程安全快照 + 变更事件的形式暴露给前端。无网络请求，不读提示词/回复正文（隐私契约与上游一致）。

**本 spec 不含任何 UI 绘制、布局、提醒动画**——前端消费 §4 契约另立 SPEC 实现。与既有能力的边界：CodexRadar 现有的额度/Token/效率管道（账户级聚合）**不动**；本 spec 只新增"按会话文件分组的任务生命周期"数据面。

## 0. 既有资源（执行前必读，硬约束）

| 资源 | 位置 | 本 spec 的用法 |
|---|---|---|
| Codex rollout 会话 watcher | `Core/CodexRadarForm.cs` 字段 `quotaSessionWatcher`（`FileSystemWatcher`，过滤 `rollout-*.jsonl`）+ 既有目录扫描（`MaxQuotaRolloutFilesToScan` 上限、按 LastWriteTimeUtc 排序） | **必须复用**。接口索引 `event.codex.sessions_watcher` 的 reuse 条款明确禁止重复遍历会话目录或新建第二个 watcher。新 reader 通过转发挂接点接收文件变更通知（见 §2.1） |
| 会话目录资源登记 | `Docs/Interfaces/INTERFACE_INDEX.jsonl` 条目 `resource_directory.codex_sessions` | 只读访问；实现后把本功能追加进该条目的 outputs/reuse |
| 独立 reader 结构范式 | `Core/SpecBoardReader.cs`（快照模型 + 解析 + 防抖）、`Core/ClaudeRadarReader.cs` | 新 `CodexTaskMonitorReader` 照此结构：纯解析类 + 不可变快照，不持 UI 引用 |
| token_count 反向尾读 | `Core/CodexRadarForm.cs`（约 12662 起："Quota events are near the end of rollout files. Read backwards in bounded…"） | 有界尾读的既有实现思路可参考；本 spec 的增量偏移读法见 §2.2 |
| 设置存储模式 | `Settings/WidgetSettings.cs`（参照 `SpecBoard*` 键簇的 默认/克隆/归一化/往返 四件套） | 新键簇 `CodexTaskMonitor*` 照此模式；**设置面板的 UI 绑定属前端 spec 范围**，本 spec 只做存储层四件套 + 自测 |
| 上游源码（本地克隆） | scratchpad `codex-monitor-hud/src/MonitorHud.Core.psm1`（634 行，解析核心）、`CodexMonitorHUD.ps1`（状态机 `Get-TaskStatus` 约 1022-1034 行） | 移植对照物。执行时若 scratchpad 已清理，重新 `git clone --depth 1 https://github.com/LH-03/codex-monitor-hud` |

## 1. 数据契约（已在本机 1.0.5.39 环境实测验证，2026-07-16）

rollout 文件路径形如 `~\.codex\sessions\<yyyy>\<MM>\<dd>\rollout-<timestamp>-<uuid>.jsonl`。**会话按创建日期归档**：几天前的会话被继续使用时仍写回原日期目录，因此活跃发现必须按 LastWriteTimeUtc 全树扫描，不能只看今天的目录（上游 `Get-ActiveHudSessionFiles` 注释同此结论）。

本 spec 消费三种记录（其余行全部跳过，包括 `response_item`——那里面才是对话正文，不许解析）：

### 1.1 `turn_context`
```json
{"type":"turn_context","payload":{"model":"...","cwd":"D:\\path\\to\\workspace"}}
```
取 `model` 与 `cwd` 的叶子目录名作为任务标签（workspace leaf）。**只取叶子名**，完整路径不进快照，不进日志。

### 1.2 `event_msg` 生命周期（payload.type ∈ task_started / task_complete / turn_aborted）
- `task_started` → 一轮开始。
- `task_complete` → 终态"完成"；若 `payload.last_agent_message` 为空/空白，视为 **silent complete**（内部静默停止，不触发提醒事件，仅更新状态）。
- `turn_aborted` → 终态"中止"。
- 均取顶层 `timestamp`（ISO-8601 UTC）转本地时刻。

### 1.3 `event_msg` / `token_count`
`payload.info.last_token_usage`（本次调用）与 `payload.info.total_token_usage`（会话累计）：`input_tokens` / `cached_input_tokens` / `output_tokens` / `reasoning_output_tokens` / `total_tokens`；`payload.info.model_context_window`。`input==0 && output==0` 的记录是维护心跳，不更新 Token 计数。`rate_limits` 已由既有额度管道消费，本 reader **不重复解析**（避免双写 quota 快照）。

## 2. Reader 设计：`Core/CodexTaskMonitorReader.cs`

### 2.1 文件发现与通知（复用既有 watcher）
- 在 `CodexRadarForm` 的 `quotaSessionWatcher` 事件处理链上加一个转发挂接点（事件或回调注册），把 `(fullPath, changeType)` 转发给 reader；reader 自身**不创建** FileSystemWatcher、不做递归目录遍历。
- 活跃窗口：LastWriteTimeUtc 距今 ≤ `ActiveWindowMinutes`（默认 30，允许 5–60）分钟的 rollout 文件进入跟踪表；上限 64 个文件；全部超窗时保留最新 1 个。已跟踪文件超窗后从表中移除（终态保持期除外，见 §3）。
- watcher 失效兜底：沿用既有管道的轮询兜底节奏，不新增独立定时器族；允许挂在既有刷新 tick 上做轻量对账。

### 2.2 增量解析（移植 `Get-LatestHudSnapshot` + `Split-HudJsonLines` 语义）
- 每个跟踪文件维护独立的**字节偏移**：首次发现做一次有界尾读（≤8MB，`FileShare.ReadWrite` 打开，UTF-8 手工解码，丢弃可能被截断的首行），之后每次通知只读新追加的字节。
- 追加读要处理**未换行收尾的完整 JSON 行**：最后一段无 `\n` 时尝试整段 JSON 解析，成功即消费（上游发现 task_complete 常在换行落盘前就完整可读）；失败则留作 pending 前缀与下次拼接。
- 解析失败/IO 失败记入该文件的 `LastReadErrorAt`，供状态机出 `error` 态；不抛出、不打断其他文件。

### 2.3 快照模型（不可变，参照 SpecBoardSnapshot）
`CodexTaskSnapshot`：`FileKey`、`TaskNumber`（稳定编号，见下）、`WorkspaceLeaf`、`Model`、`Status`（§3 七态之一）、`StartedAtLocal`、`LastEventLocal`、`TerminalStatus`/`TerminalAtLocal`/`TerminalSilent`、`LastTokenUsage` 五元组、`TotalTokenUsage` 五元组、`ContextPercent`（last input / context window）。聚合快照 `CodexTaskMonitorSnapshot`：任务列表 + `ActiveCount` + 生成时刻。

任务编号移植上游 number pool 语义：可见任务不重编号；编号释放后冷却 `NumberCooldownSeconds`（默认 120）才可复用，FIFO + 去重集，池上限 512。

## 3. 状态机（七态，阈值全部进设置键）

判定顺序（对每个跟踪文件，移植上游 `Get-TaskStatus`）：

1. `paused`：调用方通过 `SetPaused(bool)` 暂停监控（整体开关，非按任务）。
2. 终态保持：最近生命周期事件是 `task_complete`/`turn_aborted` 且距今 ≤ `TerminalHoldSeconds`（默认 120）→ `completed` / `aborted`。保持期结束后回落到常规判定。
3. `error`：`LastReadErrorAt` 距今 ≤ `ErrorHoldSeconds`（默认 30）。
4. 无快照 → `idle`。
5. 距最后写入 ≤ `ActiveSeconds`（默认 12）→ `active`（模型正在产出）。
6. 距最后写入 ≤ `IdleSeconds`（默认 90）→ `listening`（**很可能在等用户输入**——这是本功能的核心信号）。
7. 其余 → `idle`。

从 `active`/`listening` 直接掉到 `idle` 且无终态事件，只按"settled"处理，不产生提醒事件（同上游：timeout without a terminal event is only treated as settled/idle）。

## 4. 对前端的数据契约（本 spec 的对外交付面，前端 SPEC 的唯一依赖）

前端（另一模型实现）只允许通过以下面消费，不许触碰解析内部：

1. **快照拉取**：`CodexTaskMonitorSnapshot GetSnapshot()` — 线程安全、返回不可变对象，任意线程可调；无数据时返回空任务列表而非 null。
2. **变更通知**：`event EventHandler SnapshotChanged` — 快照内容实际变化时触发（去抖：同一 tick 内多文件变更合并为一次）；**在后台线程触发**，前端自己负责 marshal 到 UI 线程（与项目内既有 reader 的事件语义一致）。
3. **提醒事件**：`event EventHandler<CodexTaskAttentionEventArgs> AttentionRaised` — 仅在任务进入 `completed`（非 silent）、`aborted`、`error` 时各触发一次；args 携带 `TaskNumber`、`WorkspaceLeaf`、`Reason`（枚举 completed/aborted/error）、`AtLocal`。提醒如何呈现（高亮/动画/停留时长）完全是前端的事。
4. **控制面**：`SetPaused(bool)`、`bool IsPaused`。
5. **生命周期**：由 `CodexRadarForm`（watcher 宿主）构造与 Dispose；前端不 new、不 Dispose，只订阅。
6. 七态枚举 `CodexTaskStatus { Active, Listening, Idle, Paused, Error, Completed, Aborted }` 与快照类型放在 reader 文件内或独立模型文件，公开给前端引用；**枚举值与语义一经实现即冻结**，前端 spec 直接引用本节。

## 5. 设置键簇 `CodexTaskMonitor*`（仅存储层，照 SpecBoard* 模式）

`Enabled`（默认 true）、`ActiveWindowMinutes`（30，夹取 5–60）、`ActiveSeconds`（12，夹取 3–60）、`IdleSeconds`（90，夹取 30–600）、`TerminalHoldSeconds`（120，夹取 0–1800）、`ErrorHoldSeconds`（30，夹取 5–300）、`NumberCooldownSeconds`（120，夹取 0–3600）。全部走 默认/克隆/归一化/往返 四件套并进 `--test-settings-bindings`。设置面板（Win11SettingsForm）里的可视化绑定不在本 spec 范围，由前端 spec 决定暴露哪些。

## 6. 验收（全部无 UI，可 headless 验证）

1. `Build-Arm64.ps1` 无警告构建；`--test`、`--test-settings-bindings` 全绿。
2. 新增 `--test-codex-task-monitor` 自测：喂入构造的 JSONL 片段（含未换行收尾的 task_complete、silent complete、turn_aborted、维护心跳 token_count、UTF-8 中文 cwd、坏行降级），断言七态判定、编号池冷却、增量偏移续读、截断首行丢弃、AttentionRaised 只触发一次且 silent complete 不触发。
3. 新增诊断通道 `--dump-codex-tasks`：把当前 `GetSnapshot()` 序列化为 JSON 打到 stdout（workspace 只含叶子名），供无前端时人工/脚本验证与前端联调对拍。
4. 真实场景验证：本机开一个 Codex 任务，间隔运行 `--dump-codex-tasks`，观察 active → listening →（完成后）completed → 保持期后回落。
5. 文档四步走：活文档（`Docs/CodexRadar-Architecture.md` 增补数据面描述）、FEATURE_INDEX / INTERFACE_INDEX（更新 `event.codex.sessions_watcher` 与 `resource_directory.codex_sessions` 的 reuse/outputs；登记 `--dump-codex-tasks` 命令行接口与新设置键）、追加 CHANGELOG、校验 Gate 全绿。
6. 版权：`CodexTaskMonitorReader.cs` 文件头注明解析与状态判定逻辑移植自 MIT 许可的 LH-03/codex-monitor-hud v2.0.2-preview。

## 7. 残余风险

- rollout JSONL 是 Codex 的内部格式，无稳定性承诺；所有格式假设集中在 `CodexTaskMonitorReader` 一处，字段缺失时按"跳过该行"降级，不崩、不误报终态。
- 上游 v2.0.2 仍是 preview，状态阈值（12s/90s）是经验值；已全部开成设置键，观感不对可调不改码。
- 前后端分工风险：前端由低级别模型实现，§4 契约必须实现为**编译期可见的公开类型**（而非动态/字符串协定），让前端犯错时在编译期暴露而不是运行期。
