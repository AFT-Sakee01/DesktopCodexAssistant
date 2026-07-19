# 三轮审查问题修复实施规格（Review Findings Remediation SPEC，修订版）

- 版本：1.0.6.02
- 生成模型：Claude Fable 5
- 生成时间：2026-07-19T17:19:53+09:00（UTC 2026-07-19T08:19:53Z）
- 主题：修复 2026-07-19 三轮审查（全量代码审查、底层架构评审、设置全局生效性审查）确认的 13 项缺陷 + 1 项可选优化，并为每项定义可机器判定的验收条件
- 修订说明：本文件取代 `Fable5-ReviewFindingsRemediation-SPEC-v1.0.6.02-20260719-165608.md`（该版未开始执行）。P0-1 至 P3-10 与原版一致；新增 P2-11 至 P3-14 四项，来自设置部分全局生效性审查。

---

## 1. 目标与来源

本规格来自同日完成的三轮审查：第一轮全量代码审查（安全/可靠性/死代码），第二轮底层架构评审（异常兜底/线程安全），第三轮设置全局生效性审查（全局选项对全部窗口的分发链路核查）。所有问题均已在源码中定位确认，不是推测。执行本规格的 AI 按 §3 交付项逐项修复，按每项"验收条件"与 §4 全局验收流水线判定完成。

**验收方式硬约束（优先级高于一切实现偏好）**：

1. 全部验收必须是命令行可完成的操作：构建脚本、`--test*` 自检、`--render-*` 采样 PNG、grep/脚本断言、文件哈希比对。
2. **禁止**使用 computer-use、GUI 自动化、桌面截图、鼠标键盘驱动等任何"操作用户电脑"的验收手段；分层窗口的视觉验证一律以 `--render-*` 产出的 PNG 为准。
3. 任何单条验收步骤预期耗时 ≤ 2 分钟；§4 全套验收（含 ARM64 构建与部署）总预算 ≤ 10 分钟。**禁止**引入"观察 N 分钟""soak/过夜运行"类验收；禁止使用 `--diagnose-idle-cpu` 默认 30 分钟模式作为验收。
4. 崩溃恢复（P0-3）不得以杀死正式运行实例做"真实崩溃演练"验收，只允许自检 seam 验证。

---

## 2. 范围外（本规格明确不做）

| 事项 | 处置 |
|---|---|
| WidgetSettings 属性表驱动重构（收敛 defaults/clone/load/save/normalize 七处同步） | 另立 SPEC，本次不动（P2-12 的完整性自检是它的前置护栏） |
| .NET 8/10 迁移 | 保留为期权，触发条件是 idle 功耗或 async HTTP 成为瓶颈 |
| GuardRuntime 断网自动睡眠失败后不恢复守护（`GuardRuntime.RequestAutoSleep`） | 已评审为可接受设计（Windows 空闲策略兜底 + `LastActionDetail` 提示），不改 |
| 全仓 138 处空 catch 的 best-effort 风格 | 项目既定风格，不改 |
| CodexRadarForm 巨石拆分 | 不做 |
| 停靠看板不参与全局布局编辑器 | 按设计（位置由 Tab 中心 + 工作区推导），不改 |

---

## 3. 交付项

每项包含：现状（已确认的缺陷）、要求行为、验收条件。引用代码一律"文件 + 成员名"。除特别说明外，禁止新增命令行参数（新自检并入既有 `--test*` 旗标，避免 INTERFACE_INDEX 膨胀）。

### P0-1 设置文件原子保存

**现状**：`Settings/WidgetSettings.cs` 的 `SaveToPath` 用 `File.WriteAllLines` 直接覆盖目标文件。进程在写入中途被终止（掉电/崩溃/部署停止）会截断设置文件，而 `LoadFromPath` 吞异常继续，用户全部布局与 Guard 状态静默回落默认值。`SecretStore.WriteSecret` 与 statusline bridge 脚本均已采用临时文件+替换，唯独设置文件没有。

**要求**：`SaveToPath` 改为写 `<path>.tmp` → 目标存在时 `File.Replace`、不存在时 `File.Move`（与 `SecretStore.WriteSecret` 同模式）；无论成败均清理本次临时文件；启动时残留的旧 `.tmp` 不得阻塞下一次保存。

**验收**：
1. 新自检并入 `--test-settings-bindings` 调用链（放入 `WidgetSettings.RunFullRoundTripSelfTest` 或同文件新私有方法），断言全部成立：
   - 对临时目录路径 `SaveToPath` 后：无 `.tmp` 残留；文件可被 `LoadFromPath` 读回且字段 round-trip（沿用现有断言）。
   - 预置一个损坏内容的 `<path>.tmp` 后再 `SaveToPath`：保存成功且 `.tmp` 消失。
   - 用 `FileShare.None` 独占句柄锁定目标文件后 `SaveToPath`：调用失败（异常被自检捕获），释放句柄后目标文件字节内容与锁定前完全一致（写前后 SHA256 相同）。
2. `DesktopCodexAssistant.exe --test-settings-bindings` 输出 `Settings binding policy: PASS`，退出码 0。
3. grep 断言：`SaveToPath` 方法体内不存在对最终 `path` 的直接 `File.WriteAllLines`/`File.WriteAllText` 调用（写 `.tmp` 的调用除外）。

### P0-2 Logger 写盘防御

**现状**：`Core/Logger.cs` 的 `AppendImmediate`（`File.AppendAllText`）无异常捕获，`Info`/`Error` 会把 IO 异常抛回任意调用方。部署流程"`--stop` 旧实例→启动新实例"存在双进程并发追加同一日志文件的窗口（`AppendAllText` 以 `FileShare.Read` 打开，后到者得共享冲突异常）；`Program.Main` 中最早的 `LogInfo("Starting...")` 调用在 try 块之外，最坏情况启动即崩。

**要求**：`Logger.Info`/`Logger.Error`/`Logger.Flush`/`ProbeDetail` 的全部文件 IO 不得向调用方抛出异常（IO/UnauthorizedAccess 静默降级）；写失败时 INFO 内容保留在缓冲等待下次 flush 或丢弃（二选一，代码注释写明取舍）；ERROR 写失败不得影响调用方的原始异常处理流程。可选强化：追加改用 `FileStream(..., FileShare.ReadWrite)` 降低跨进程冲突概率。

**验收**：
1. 新自检并入 `Logger.RunStoragePolicySelfTest`（由 `--test-logger` 驱动），断言全部成立：
   - 将自检专用日志路径重定向到临时目录（不得触碰正式日志），用 `FileShare.None` 独占打开活动日志文件，随后调用 `Info`、`Error`、`Flush` 各一次：无任何异常传播到自检代码。
   - 释放独占句柄后再次调用 `Error` 与 `Flush`：新的 ERROR 行成功落盘（文件内容包含该行）。
2. `DesktopCodexAssistant.exe --test-logger` 输出 `Logger storage policy: PASS`，退出码 0。
3. 现有轮转/目录上限自检保持 PASS（防御性 catch 不得吞掉轮转逻辑的错误路径）。

### P0-3 全局异常兜底与崩溃自愈

**现状**：全仓无 `Application.ThreadException`、`AppDomain.CurrentDomain.UnhandledException` 注册（仅 `Main` 有启动 try）。UI 线程任何未捕获异常都会终止进程；`SetThreadExecutionState` 注册随线程消失，**睡眠守护静默失效**——这与 GuardBoard 的存在目的直接冲突。GuardRuntime 状态已持久化（`GuardRuntime.LoadFromSettings` 会恢复守护），缺的只是"崩溃后有东西负责重启"。

**要求**：
1. `Main` 在 `Application.Run` 之前注册：`Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException)`、`Application.ThreadException`、`AppDomain.CurrentDomain.UnhandledException`。
2. `ThreadException`（可恢复的 UI 异常）：`Logger.Error` 记录后继续运行，不弹阻塞对话框。
3. `UnhandledException`：`Logger.Error` 记录 → 判定是否自拉起 → 满足条件则复用现有 `RestartApplication`/`--restart-after-pid` 机制启动新实例（保持 `--desktop-parent` 原样传递）后退出。
4. 防重启风暴：新增静态纯函数（建议 `Program.ShouldRestartAfterFatalException(DateTime lastFatalUtc, DateTime nowUtc)`），语义为距上次致命异常不足抑制窗口（常量命名建议 `FatalRestartSuppressionMinutes = 15`）时返回 false（只记录、不自拉起）。
5. 不新增命令行参数；自检并入 `--test-display-recovery`。

**验收**：
1. `--test-display-recovery` 自检新增断言全部成立：
   - `ShouldRestartAfterFatalException(DateTime.MinValue, now)` == true（首次崩溃自拉起）。
   - 距上次 5 分钟 == false；距上次 16 分钟 == true。
   - 重启参数构造结果包含 `--restart-after-pid ` + 当前进程 pid（可直接复用/断言 `RestartApplication` 使用的参数构造逻辑，允许为此提取参数构造纯函数）。
2. `DesktopCodexAssistant.exe --test-display-recovery` 输出 PASS，退出码 0。
3. grep 断言：`DesktopCodexAssistant.cs` 中 `ThreadException` 与 `UnhandledException` 的注册各 ≥ 1 处。
4. 重申：不得杀正式实例做真实演练。

### P1-4 SecretStore 明文密钥迁移残留清理

**现状**：`Core/SecretStore.cs` 的 `MoveLegacySecretToMigratedFile` 把明文旧密钥文件改名为 `.migrated` 无限期保留，`RunSelfTest` 甚至断言该残留存在。影响 Claude OAuth setup-token（`claude-code-oauth-token.txt`）与 DeepSeek API key 旧文件——DPAPI 加密的意义被磁盘上的明文副本抵消。

**要求**：
1. 迁移成功（加密文件写入完成）后**删除**明文旧文件，不再产生 `.migrated` 副本；加密写入失败时保留明文原文件（不得先删后写导致丢密钥）。
2. `TryReadOrMigrateSecret` 每次成功读取加密文件时，顺带清理同目录同名 `.migrated*` 历史残留（一次性治愈存量机器）。
3. `DeleteMigratedLegacyFiles` 保留（清理用途）。

**验收**：
1. `SecretStore.RunSelfTest` 更新断言全部成立：
   - 仅有明文 legacy 文件 → 迁移后：加密文件存在且 `Unprotect` round-trip 成功；legacy 文件与 `legacy + ".migrated"` 均不存在。
   - 预置加密文件 + 历史 `.migrated` 残留 → 读取成功后残留被清理。
   - 加密路径不可写（指向已被独占锁定的文件）时迁移失败 → 明文原文件仍在（密钥不丢失）。
2. `--test` 中 `ClaudeCodeUsageReader.RunSelfTest`（含 setup-token 迁移自检）PASS；`--test-settings-bindings` PASS。
3. grep 断言：`Core/SecretStore.cs` 中不存在"将 legacy 改名保留"的 `File.Move(legacyTextPath, ...)` 模式；`.migrated` 字符串仅出现在清理逻辑与自检中。

### P1-5 共享 JavaScriptSerializer 线程安全

**现状**：`Performance/CloudEndpointProbe.cs` 的 `private static readonly JavaScriptSerializer Json` 被最多 `MaxConcurrentRequests = 3` 个并发探测任务共享，而 `JavaScriptSerializer` 实例不保证线程安全。全仓其余位置均为每次局部 `new`。

**要求**：删除该静态共享实例，各解析点改为局部 `new JavaScriptSerializer()`。同时核查全仓不存在其他跨线程共享的静态 `JavaScriptSerializer`；实例字段（如 `CodexQuotaGoalPlanner.CodexAppServerClient.serializer`）须确认单线程使用后方可保留。

**验收**：
1. grep 断言：`rg "static readonly JavaScriptSerializer" Core Performance Settings Interop` 0 命中。
2. `--test` 中 `CloudEndpointProbe.RunSelfTest` PASS。

### P1-6 FixedPing 轮进行中保留上一轮结果

**现状**：`Performance/FixedPingProbe.cs` 的 `Poll` 在每轮开始把全部行替换为 `FixedPingStatus.Checking`；`Core/NetworkMonitorForm.DockedLayout.cs` 的 `DrawDockedFixedPingRow` 对 Checking 渲染黄色"检测中"。均衡模式每 3 秒一轮，面板反复闪黄且上次延迟数值消失最长 1 秒。

**要求**：
1. 已有上一轮结果时，新一轮开始仅置快照级 `Running = true`，各行保留上一轮 `Status`/`LatencyMs`/`Reason`；UI 可依据 `Running` 画轻量进行中指示（不强制），但不得整行替换文案或整片变色。
2. 仅以下情形显示"检测中"整行占位：首轮无历史、配置签名变化、断网后恢复且无历史。
3. 状态过渡逻辑提取为可测纯函数（建议静态方法，输入"上一轮快照 + 是否有历史 + 本轮起因"，输出轮开始时应发布的快照）。

**验收**：
1. `FixedPingProbeReader.RunSelfTest` 新增纯逻辑断言：
   - 有历史（3 行含延迟值）+ 新轮开始 → 输出各行保留原 Status 与延迟、快照 `Running == true`。
   - 无历史 + 新轮开始 → 各行为 Checking。
   - 配置签名变化 → 各行为 Checking（旧结果作废）。
2. `--test` 中 Fixed ping 自检 PASS。
3. `DesktopCodexAssistant.exe --render-networkmonitor --out <临时目录>` 退出码 0 且生成 PNG 文件（渲染路径未破坏；不要求像素级比对）。

### P2-7 SpecBoard 打开目标白名单

**现状**：`Core/SpecBoardForm.cs` 的 `OpenRow` 对账本（跨项目 JSONL，由外部 AI 会话写入）中的路径直接 `Process.Start(UseShellExecute = true)`。已校验存在性，但账本被污染指向 `.exe`/`.bat`/`.lnk` 时双击即执行。

**要求**：提取静态纯函数（建议 `SpecBoardForm.ResolveOpenTarget(string absolutePath, string projectRoot)`）：扩展名 ∈ {`.md`, `.markdown`, `.txt`, `.json`, `.jsonl`}（不区分大小写）且文件存在 → 返回文件本身；文件存在但扩展名不在白名单 → 返回其所在目录；其余沿用现有回退（所在目录 → `projectRoot`）。`OpenRow` 与双击路径全部经此函数。

**验收**：
1. `SpecBoardForm.RunSelfTest` 新增断言：临时目录里真实创建的 `.md` → 返回文件自身；`.exe` → 返回其所在目录；不存在的路径 → 现有回退语义不变。
2. `--test-layout` 输出含 SpecBoard 自检 PASS，退出码 0。

### P2-8 CaptivePortal 重定向原因死分支

**现状**：`Performance/NetworkMonitorReader.cs` 的 `CheckCaptivePortal` 中重定向分支为 `string.IsNullOrEmpty(location) ? "门户重定向" : "门户重定向"`——两分支相同，读出的 `Location` 头未使用。

**要求**：提取纯函数（建议 `NetworkMonitorReader.BuildCaptivePortalRedirectReason(string location)`）：`Uri.TryCreate` 能解析出 host → `"门户重定向 → <host>"`（host 截断至 ≤ 48 字符）；否则 `"门户重定向"`。

**验收**：
1. 该纯函数被同文件自检（并入 `RunRollingPingSelfTest` 或新私有自检，由 `--test` 驱动）断言：绝对 URL → 含 host；空串/相对路径/垃圾输入 → 无 host 短文案；超长 host → 截断。
2. `--test` PASS；grep 断言原重复三元分支消失。

### P2-9 WidgetSettings 重复迁移分支合并

**现状**：`Settings/WidgetSettings.cs` 的 `LoadFromPath` 中 `settingsVersion < 31` 与 `settingsVersion < 32` 两段分支调用同一个 `ApplyCodexRadarBalancedWidthMigration`。

**要求**：执行前先关键词检索 `Docs/Maintenance/CHANGELOG.jsonl` 中 settings 版本 31/32 相关记录确认语义；若为无意重复 → 合并为单一 `settingsVersion > 0 && settingsVersion < 32` 分支；若为刻意重放 → 保留一段并加注释写明原因。两种处置都必须让该迁移在同一次 Load 中至多执行一次。

**验收**：
1. grep 断言：`LoadFromPath` 内 `ApplyCodexRadarBalancedWidthMigration` 调用点恰好 1 处。
2. `--test-settings-bindings`（含 `RunCompatibilitySelfTest` 的老版本设置文件迁移断言）PASS。

### P3-10 PathPing 路径发现解析一次（可选，允许放弃）

**现状**：`Performance/PathPingProbe.cs` 的 `DiscoverPath` 在 TTL 1..30 循环内每次以字符串目标调 `Ping.Send`；目标为主机名时一次 trace 最多触发 30 次 DNS 解析。

**要求**：循环前解析一次（`IPAddress.TryParse` 失败则 `Dns.GetHostAddresses` 取首个可用地址），循环内以 `IPAddress` 发送；解析失败按现有 PingException 降级路径处理（返回空路径）。

**验收**：
1. `--test` 中 PathPing 自检 PASS。
2. 代码断言：TTL 循环内 `ping.Send` 首参为 `IPAddress` 类型变量而非字符串目标。

### P2-11 独立 Claude Radar 接入悬停透明/隐藏模式链路（设置审查新增）

**现状**：`Core/ClaudeRadarForm.cs` 没有 `ApplyHoverAlpha` 覆盖、全文件零处 HoverOpacity 引用、也不接收 `WidgetForm` 的自动隐藏 keep-alive 复位（`SetAutoHideKeepAliveActive`）；兄弟窗口 `CodexRadarForm` 三者俱全。后果：按隐藏模式热键（`HotkeyToggleHoverOpacity` / 双击 RadialDial 核心圈触发 `WidgetForm.ToggleForcedHoverOpacity`）或自动隐藏（`AutoHoverOpacityMaximizedEnabled`）生效时，所有浮窗淡化，唯独独立 Claude Radar 保持原样。`Docs/Performance-And-Window-Runtime.md` 的参与窗口清单同样缺失 Claude Radar——文档与代码一致地漏了它。

**要求**：
1. `ClaudeRadarForm` 按 `CodexRadarForm` 同模式接入：覆盖 `ApplyHoverAlpha`，经 `HoverInteractionPolicy` 组合手动隐藏、自动隐藏与悬停显隐延迟状态；复用既有 `SetSharedInteractionPolling` 共享轮询，**不新增计时器**（须遵守 `Docs/Component-Refresh-Rules.md`）。
2. `WidgetForm` 的自动隐藏 keep-alive 复位清单（`OperationRadialCoreAutoHideKeepAliveEnabled` 路径）纳入 ClaudeRadar，与 CodexRadar 对称。
3. 文档同步：`Docs/Performance-And-Window-Runtime.md` 悬停透明参与窗口清单加入 Claude Radar；刷新/计时行为若有任何变化，同步 `Docs/Component-Refresh-Rules.md`。

**验收**：
1. grep 断言：`Core/ClaudeRadarForm.cs` 含 `override int ApplyHoverAlpha` ≥ 1 处。
2. `--test-layout` 新增断言（并入 `ClaudeRadarForm.RunRenderResourceSelfTest` 或同文件新自检）：隐藏组合态激活时窗口的 `GetApplicationOpacityAlpha` 结果低于非激活态。
3. `--render-clauderadar --out <临时目录>` 退出码 0 且生成 PNG。
4. `--test` PASS；grep 断言文档参与清单含 "Claude Radar"。

### P2-12 左停靠设置补设置 UI + 绑定覆盖完整性自检（设置审查新增）

**现状**：`SpecBoardLeftDockEnabled`、`CodexTaskBoardLeftDockEnabled`、`NetworkMonitorLeftDockEnabled`、`GuardBoardLeftDockEnabled`、四个 `*LeftDockTabCenterY`、`GuardBoardAutoHideSeconds` 共 9 个设置键：defaults/clone/load/save/normalize/migration 七件套齐全、运行时读取生效，但 `Settings/Win11SettingsForm.cs` 与扇形「全部开关」菜单均无任何绑定或写入路径——唯一改法是手编辑设置文件。违反根 `AGENTS.md` 不变量「新设置必须覆盖设置 UI」。未被拦住的根因：`Win11SettingsForm.VerifySelfTest` 的 `required[]` 是白名单，不在名单里的键静默放过。

**要求**：
1. `Win11SettingsForm` 为 9 键补设置行，归入对应窗口分区（网络监控 / Spec Board / Codex 任务；GUARD 无分区则新建「GUARD 看板」分区）。`*LeftDockTabCenterY` 编辑器须支持 `WidgetSettings.AutoLeftDockTabCenterY` 哨兵语义（自动/手动两态），保存值经 `NormalizeLeftDockTabCenterY` 规范化。
2. `VerifySelfTest` 的 `required[]` 加入全部 9 键。
3. 新增**绑定覆盖完整性自检**（并入 `--test-settings-bindings`）：反射枚举 `WidgetSettings` 全部公共可写属性，每个属性必须命中「已绑定编辑器清单」或「显式豁免清单」之一，否则自检失败。运行时状态键（`GuardSleepEnabled`、`GuardSleepSinceUtcTicks`、`GuardDisplayUntilUtcTicks`、`GuardBatteryCarePauseUntilUtcTicks`、窗口几何/工作区缓存等非用户偏好键）进豁免清单并逐条注明理由。此自检把 AGENTS 的"设置 UI 覆盖"不变量从纪律变成机器护栏。

**验收**：
1. `--test-settings-bindings` 输出 PASS，退出码 0（含新完整性自检）。
2. grep 断言：`required` 数组含 9 个键名各 ≥ 1 次。
3. 机器判据：完整性自检通过即豁免清单成立；执行 AI 须在 CHANGELOG 记录豁免清单条目数及依据摘要。
4. 沿用 `RunFullRoundTripSelfTest` 验证 9 键 UI 行保存回写。

### P3-13 GUARD 板独立透明度/缩放覆盖槽位（设置审查新增）

**现状**：`Core/GuardBoardForm.cs` 的 `WindowTransparencyOverridePercent` / `WindowScaleOverridePercent` 返回 SpecBoard 的对应键；GUARD Tab（`EdgeDockTabForm`）同样借用 SpecBoard 槽位。用户在设置里调「Spec Board 透明度/缩放覆盖」会连带改变 GUARD 板与 GUARD Tab，设置说明未写明。

**要求**：
1. 新增 `GuardBoardTransparencyOverridePercent` / `GuardBoardScaleOverridePercent` 两键（默认哨兵=跟随全局），七件套 + 设置 UI（归入 P2-12 的 GUARD 分区）+ 迁移版本齐全。
2. 迁移保持既有视觉：新键初始化为迁移时 SpecBoard 对应覆盖值（用户若从未设过 SpecBoard 覆盖，则两键为哨兵、跟随全局，视觉无变化）。
3. `GuardBoardForm` 与 GUARD Tab 改读新键；`EdgeDockTabForm` 现有 `followsCodexTaskBoardTransparency` 布尔改为可区分 Spec/CodexTask/Guard 归属的枚举或等价机制。
4. 文档同步：`Docs/GuardBoard-Architecture.md` 更新透明度/缩放来源描述。

**验收**：
1. grep 断言：`Core/GuardBoardForm.cs` 不再引用 `SpecBoardTransparencyOverridePercent` / `SpecBoardScaleOverridePercent`。
2. `--test-settings-bindings` PASS，含迁移断言：预置「SpecBoard 覆盖=60」的旧版本设置文件 → 迁移后 Guard 两键=60；预置无覆盖的旧文件 → Guard 两键为哨兵。
3. `--render-guard --out <临时目录>` 退出码 0 且生成 PNG。

### P3-14 修正 GUARD 持久化过时注释（设置审查新增，顺手项）

**现状**：`Core/OperationForm.GuardBoard.cs` 的 `PeekGuardRuntime` 注释声称 guard 状态"per-process and not persisted"、null 即守护全关——与实际相反：`PersistGuardStateFromBoard` 落盘、`GuardBoardForm` 构造时 `GuardRuntime.LoadFromSettings` 恢复、启动时必建板。

**要求**：注释改为反映实际（状态持久化于设置文件、启动即恢复；null 仅意味着板尚未构造）。

**验收**：
1. grep 断言：`Core/OperationForm.GuardBoard.cs` 中 "not persisted" 0 命中。
2. `--test` PASS。

---

## 4. 全局验收流水线（全部 CLI，总预算 ≤ 10 分钟）

执行 AI 完成 §3 后按顺序执行；任何一步失败即未完成，修复后从该步重跑：

| 步骤 | 命令 | 通过判据 | 预期耗时 |
|---|---|---|---|
| 1 构建 | `powershell -File Build-Arm64.ps1 -OutputPath <候选路径>` | `Built ...` 且退出码 0 | ≤ 2 分钟 |
| 2 自检 | 候选 exe 依次跑 `--test`、`--test-logger`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery` | 全部 PASS、退出码 0 | 每条 ≤ 1 分钟 |
| 3 渲染 | `--render-networkmonitor`、`--render-specboard`、`--render-clauderadar`、`--render-guard` 各输出到临时目录 | 退出码 0 且 PNG 生成 | ≤ 1 分钟 |
| 4 grep 断言 | §3 各项的 grep 断言逐条执行 | 全部符合 | ≤ 1 分钟 |
| 5 版本与文档 | 提升 `ProductIdentity.Version` 与根 `AGENTS.md` Current version；逐项追加 CHANGELOG（`fix`/`perf`/`ui_change`，一事一条，验证证据写实跑输出）；按 §5 同步文档与索引；跑 `Docs/AGENTS.md` §8 验证 Gate 全绿 | Gate 输出 2 个 PASS | ≤ 1 分钟 |
| 6 部署 | 按根 `AGENTS.md` 默认规则：备份正式 exe → 覆盖 → 重启（全 CLI） | 版本/长度/SHA256 核对一致 | ≤ 2 分钟 |
| 7 回填 | 本 SPEC 在 `Docs/Technical/INDEX.jsonl` 的行 status 回填 `implemented`；Spec Board 账本推进 `awaiting_verify` | JSONL 可解析 | ≤ 1 分钟 |

**再次重申禁止事项**：验收全程不得使用 computer-use/GUI 自动化/桌面截图；不得引入任何 >10 分钟的等待或观察步骤；崩溃恢复不做真实崩溃演练。

---

## 5. 文档同步要求

- 本次不新增命令行参数、外部 URL、持久化文件；P3-13 新增 2 个设置键（须按下表同步）。
- `FEATURE_INDEX.jsonl`：P2-12/P3-13 涉及的四个停靠看板功能行补 `setting_keys`（9+2 键归位）；P2-11 若 Claude Radar 功能行列出推荐测试则核对 `--test-layout` 仍适用。其余项仅当推荐测试变化时更新。
- `Docs/Performance-And-Window-Runtime.md`：P2-11 参与窗口清单；P3-13 若触碰 salt/合成描述则一并核对。
- `Docs/GuardBoard-Architecture.md`：P3-13 透明度/缩放来源。
- `Docs/Component-Refresh-Rules.md`：P1-6 不改变轮询间隔与单飞语义（只改轮内呈现），默认无需更新；P2-11 要求不新增计时器，若执行中改动了任何间隔/调度，必须同步。
- CHANGELOG：§3 每个交付项一条记录；部署另记一条 `deployment`。
