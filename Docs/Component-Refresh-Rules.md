# 组件刷新规则

适用版本：2.0.0.63

本文是全项目刷新间隔、timer 所有权、手动刷新、网络事件、单飞、冷却和暂停恢复策略的唯一事实源。

## 1. 维护边界

以下变化必须同步本文：

- `WidgetSettings.Get*Interval*`、性能模式语义或默认间隔。
- timer、watcher、`NetworkChange`、电源/会话/显示通知和全局热键。
- reader 的单飞、缓存 TTL、冷却、generation、epoch、取消或迟到结果规则。
- 全屏、息屏、锁屏、挂起、显示恢复与分辨率变化期间的刷新策略。
- Operation、Settings 或 board 的手动刷新 token 与 `ForceRefresh` 覆盖范围。

纯颜色、字体、文字或不影响触发条件的局部几何变化不需要更新本文。

## 2. 全局时间基准

主要时间策略集中在 `Settings/WidgetSettings.cs`。

| 策略 | 性能 | 均衡 | 省电 | 说明 |
| --- | ---: | ---: | ---: | --- |
| 主性能采样 | 500 ms | 1000 ms | 2500 ms | hidden `WidgetForm` 的 PDH 快照调度 |
| 普通 owner/board 调度 | 500 ms | 1000 ms | 3000 ms | 只检查 deadline；显示字段未变时不重绘 |
| GPU/NPU 昂贵采样 | 1000 ms | 2000 ms | 5000 ms | 与 CPU/内存快照独立节流 |
| 交互动效 | 16 ms | 33 ms | 100 ms | 只在控件 hover/按压动画未收敛时运行 |
| 静止交互轮询 | 30 ms | 100 ms | 250 ms | 自动穿透、两级防烧屏、扇形盘闲置收起和层级维护 |
| 本地网络信息 | 2 s | 5 s | 网络事件驱动 | 省电模式不做固定周期网卡枚举 |
| 公网 IP | 5 min | 10 min | 15 min | 仅真实网络为 `Online` 时请求 |

连通性检测：

| 状态 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| `Online` | 10 s | 30 s | 60 s |
| `NeedsValidation` | 5 s | 10 s | 30 s |
| `Offline` | 3 s | 5 s | 10 s |
| `AdapterMissing` | 不轮询 | 不轮询 | 不轮询 |
| `Unknown` | 立即 | 立即 | 立即 |

DNS 检测：

| 最差状态 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| `Unknown` | 15 s | 30 s | 60 s |
| 异常、劫持或不可用 | 30 s | 60 s | 120 s |
| 全正常 | 5 min | 10 min | 15 min |

## 3. Hidden Widget host 与可见表面

源码：`Core/WidgetForm.cs`、`Core/WidgetForm.TileColumn.cs`、`Performance/PdhSampler.cs`

| 项目 | 规则 |
| --- | --- |
| 主控制 tick | hidden `WidgetForm` 按主性能间隔检查设置热加载、全屏/显示状态、PDH 采样和 `MetricTileFeed` 推送；宿主自身不绘制。停止事件另由 ThreadPool 注册等待直接投递 `WM_CLOSE`，主 tick 轮询仅作兼容兜底。 |
| 内存压力 | `PdhSampler` 在同一次主 PDH 采样读取 `Memory\\Committed Bytes`、`Memory\\Commit Limit` 与 `Memory\\Pages Output/sec`；只对换出量按 `MemoryPressureTracker.PageOutSmoothingSeconds = 10` 秒做时间 EWMA，不把 DLL/EXE/映射文件等页面读入当作压力。正常升黄色需 `WarningPromotionDelaySeconds = 10` 秒，升红色需 `CriticalPromotionDelaySeconds = 5` 秒；红转黄恢复需 `CriticalRecoveryDelaySeconds = 30` 秒，黄转绿需 `NormalRecoveryDelaySeconds = 60` 秒。Commit 达到 98% 或可用物理内存低于临界头寸时立即红色。`WidgetForm` 以时间戳保留最近 60 秒压力状态，不新增 timer。 |
| 应用窗口事件 | 前台窗口 Hook 始终启用；只有全屏/最大化/遮挡可见性模式才启用对象 Hook 和主采样周期完整枚举。事件按 HWND 合并，125 ms 批处理，每批最多 64 项，队列上限 256 项；溢出退化为一次完整枚举。同产品测试/辅助进程的窗口事件按 PID 身份缓存过滤。 |
| 昂贵硬件 | GPU/NPU 按 1/2/5 s 独立 deadline；不能被更快的 CPU tick 放大。 |
| 设置热加载 | `settings.ini` 由 `FileSystemWatcher` 与主 tick 的修改时间检查共同覆盖。 |
| 右侧 tiles | 11 个 `MetricTileForm` 只消费同一次 feed；方块和 hover expand 不自行采样。 |
| 左侧 docks | 7 个 `EdgeDockTabForm` 复用各自既有 hover tick；展开 board 的业务刷新由 owner 管理。 |
| 全屏 | 隐藏 visible tiles/tabs/boards/Operation；不停止 Radar/Power headless backend。 |
| 显示恢复 | 首轮延迟 350 ms，最多 3 轮；后续重试 1500 ms。重枚举 work-area、重建 visible layered resources、重定位并强制刷新。 |
| Win+D | 全局 Win+D 后延迟 2000 ms 执行本程序和 SeelenUI 拉前；不拦截系统显示桌面。 |
| 休眠唤醒 | `PBT_APMRESUME*` 后完成显示恢复，再按设置重启 SeelenUI/本程序；30 s 内重复恢复事件只处理一次。 |
| 强制刷新 | `ForceRefreshAllModules()` 使 PDH、磁盘用量、Radar、Power/Thermal 与 Network 到期；Network 同时请求共享 Clean IP reader。 |
| 诊断 | 主采样与 12 h timing 摘要最多每 15 min 记录一次；UI watchdog 后台每 2 s 检查心跳，超过 10 s 记录，持续卡住每 30 s 重复，恢复后补一条 responsive 记录。快照包含 UI managed thread ID、窗口事件接收/合并/丢弃/溢出/处理/批次/完整刷新计数和当前待处理量。 |

交互与防烧屏规则：

- 两级防烧屏默认开启：`BurnInLevelOneIdleSeconds = 10` 后进入一级，随后 `BurnInLevelTwoDelaySeconds = 30` 后进入二级；允许范围分别为 1-300 秒和 1-600 秒。
- 防烧屏复用 hidden `WidgetForm` 的静止交互轮询和七个 `EdgeDockTabForm` 的 120 ms hover tick，不新增 timer。一级/二级激活后，纯鼠标移动只驱动左 tab 局部恢复，或让右侧 tile/expand 整组恢复亮度与原始强调色；二级真实状态仍锁定，所以白色/中性文字继续隐藏，离开右侧组后重新反色。进入二级的边沿立即收起当前 hover expand。点击、滚轮、键盘输入、显示挂起与布局编辑会归零状态并重新计时。
- 右侧 11 个 tile 与 hover expand 默认设置 `WS_EX_TRANSPARENT` 以穿透点击；hover 继续复用光标位置轮询，不新增 hook/timer。额度彩蛋状态机同样只消费已发布快照，已知空→恢复只登记一次，第一次展开后清除；彩蛋文字只由展开窗绘制，常驻 tile 不绘制。
- hidden host 只协调防烧屏状态，不提交像素；headless owners 不进入 hover、click-through、burn-in 或 Z-order 轮询。

## 4. Codex / Claude Radar headless owner

源码：`Core/CodexRadarForm.cs`、`Core/CodexRadarForm.RuntimeState.cs`、`Core/CodexRadarForm.ProjectionState.cs`、`Core/CodexRadarForm.TileSnapshot.cs`、`Core/OwnerOperationGeneration.cs`、`Core/ClaudeCodeUsageReader.cs`、`Core/ClaudeCodeUsageScheduler.cs`、`Core/DeepSeekServiceMonitor.cs`、`Core/DeepSeekBalanceMonitor.cs`

| 项目 | 规则 |
| --- | --- |
| 生命周期 | `WidgetForm` 构造后显式调用 `StartHeadlessDataOwner()`；owner 创建隐藏 HWND 并启动 backend scheduler，但不调用 `Show()`。Start/恢复建立 generation，Stop/挂起先取消并失效 generation，重复调用幂等。 |
| owner tick | 使用普通调度 500/1000/3000 ms，并贴近秒边界；tick 只检查各数据源 deadline 与单飞状态。 |
| family 隔离 | Codex family 保存公共 Radar/模型/额度；Claude family 只保存官方额度与服务状态。请求同时捕获 family 与 owner generation；迟到结果不得写状态、缓存、日志、通知或 UI。 |
| visible snapshots | producer 一次替换 `RadarPublishedProjectionState`；`BuildRadarTileSnapshot`、`BuildCodexIqBoardSnapshot`、`BuildResetSpeedBoardSnapshot`、`BuildServiceHealth` 和 task provider 从同一 published state 或 owner 已载入内存 clone，不触发网络、provider、磁盘或自动切换。 |
| fullscreen | 全屏标志不停止 backend；显示器关闭、会话锁定或系统挂起停止 Radar 轮询，恢复后错峰刷新。 |
| 随机测试 | 暂停真实网站、额度和服务轮询；手动 token 立即重建，自动 fixture 最快 1 s 一次；不得写真实缓存。 |

### 4.1 软件 presence 与 selected-provider gate

- 共享 `SoftwareRuntimePresence` 常规按性能模式 3/5/10 s 检查明确进程名、包身份和已学习别名。
- 只有 Codex/Claude 都在运行且 Auto 模式需要判定前台时，才使用共享身份分类器。
- 低频发现最多每 60 s 扫描一次带主窗口的进程；产品元数据缓存上限 64 个路径。
- 个人额度只为当前有效且正在运行的 family 排队；两者都未运行时保留快照，不同时 prime 两套 provider。
- Codex `FiveHourLimitAbsent` 的实测周速率环复用 owner tick 的纯内存活动时钟；family 切换、进程停止、挂起或超过 90 s 调度断档会切断样本段。

### 4.2 网站、额度与服务

| 数据源 | 正常周期 | 失败/异常 | 额外触发 |
| --- | --- | --- | --- |
| Codex 公开 Radar | 北京时间每小时整点一次 | 10 min 重试 | 启动、恢复、Codex 模型/源变化、手动刷新 |
| Codex usage provider | 5 min | 普通失败 10 min；HTTP 429 15 min | selected-provider gate、手动刷新 |
| Codex 7 天额度历史落盘 | 接受额度快照时更新内存；最多每 15 s 后台批量落盘 | 失败时保留运行期内存，不能影响额度提交 | 每 6 h 或 owner 退出时裁剪为最近 7 天、最多 2048 行 |
| Codex 本地 session fallback | 性能/均衡/省电 10/15/30 s | 仅 provider 无新鲜快照 | 只在 Codex 正在运行时 |
| Claude Code usage | 5 min | 普通失败 10 min；HTTP 429 15 min | selected-provider gate、setup token/恢复/手动刷新 |
| OpenAI/Anthropic Statuspage | 正常 15 min | 异常或失败 2 min | 启动、网络变化、服务 token、手动刷新 |
| DeepSeek service monitor | 正常 60 s | 失败/未知 5 min | 启动、网络变化、手动刷新 |
| DeepSeek balance monitor | 有 key 且成功 5 min | 失败 10 min；无 key 15 min | 启动、网络变化、手动刷新、Key 更新/清除 |

约束：

- 各来源单飞；Claude 官方 usage 的消费者 join 同一 scheduler 请求，同一 Statuspage serviceKey、DeepSeek service 和 DeepSeek balance 请求各只发一次。
- Claude usage 只有两组百分比、两组 reset 与可信来源时间均完整且新鲜时才提交；部分结果进入失败退避并保留 last-good。
- Radar 网站同内容保留原数据时间，不能用抓取时间伪造新批次。
- `current.json` schema 2 是模型 IQ 主源；HTML 仅在速蹬窗口缺失时补该窗口，不读取已删除的 model-ratings 或 quota_radar 链。
- provider 与 reset-credit 请求双向错峰至少 10 s。
- 新服务错误经 10 s `ServiceAlertDebouncer` 稳定后发布，恢复立即清除；family 切换不继承另一 family 错误。
- AI 请求保护命中时不读凭据、不发 OpenAI/ChatGPT/Claude/Anthropic 请求。

### 4.3 Codex IQ 与任务

- Codex IQ board 可见时每 5 s clone `BuildCodexIqBoardSnapshot()`；既有 500 ms board tick 只做 tab/收起/定位/绘制节流。
- board 隐藏、全屏或显示挂起时停止展示轮询；不改变 Radar 网站业务周期。
- 模型目录由 owner 在启动、成功刷新或显式 reload 时载入内存；连续 UI projection 不读取文件或目录。`TimingStats` 以 `codex.iq_snapshot_projection` 记录内存投影耗时，15 分钟摘要包含 P95/P99/max，不逐次写盘。
- `%USERPROFILE%\.codex\sessions` 只有一套递归 watcher。任务 reader 按文件事件增量尾读，watcher 漏报时每 30 s 后台完整对账；不创建独立 timer。

## 5. Power / Thermal headless owner

源码：`Core/PowerThermalForm.cs`、`Core/PowerThermalForm.Snapshot.cs`

| 项目 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| 功耗 | 1 s | 2 s | 5 s |
| 温度低于 65°C | 2 s | 5 s | 10 s |
| 65-69.9°C | 1.5 s | 3 s | 5 s |
| 70-89.9°C | 1 s | 2 s | 3 s |
| 90°C 及以上 | 1 s | 1 s | 1 s |

规则：

- `StartHeadlessDataOwner()` 显式创建隐藏 HWND 和 scheduler；不调用 `Show()`。退出用 `StopHeadlessDataOwner()`。
- 功耗和温度有独立 deadline，但同一个后台 worker 可合并满足；运行中到期只合并一个 pending 请求。
- `GUID_ACDC_POWER_SOURCE`、电量、power scheme 和 energy saver 通知只使功耗立即到期；温度仍走自己的 deadline。
- 严重温度采样优先于省电策略。
- `BuildStripSnapshot()` 只读缓存，不触发 WMI、注册表或 `powercfg`。
- `WidgetForm.BuildMetricTilePowerProjection()` 不建立 timer；只在组装 `MetricTileFeed` 时按 `MetricTilePowerProjectionRefreshIntervalMs = 5000` 最多每 5 秒 clone 一次 System Day 的 `Last24Hours` owner-memory 投影，供 PWR 展开详情绘制趋势与 ETA。
- `WidgetForm.RecordSystemDaySample()` 在既有主采样 tick 中、推送 tile feed 之前，将缓存电量交给 GUARD 检测向上跨越 80%；只在形成新暂停记录时保存设置，不改变历史样本节奏，不新增 timer。PWR 倒计时随既有 feed 刷新，以绝对 UTC 截止时间计算；GUARD 的既有维护 tick 清理到期记录。手动暂停/恢复成功后立即刷新 PWR；与新目标不符的旧历史 ETA 暂不使用。隐藏 tile 不停止检测；休眠期间不采样，恢复后只能用首次可见读数检测跨越，不能重建睡眠期间的精确起点。
- `WidgetForm.MaintainProgramKeepAlive()` 不建立 timer；只在既有主控制 tick 中按 `TranslatorKeepAliveIntervalSeconds = 30` 自门控，且仅在三个保活开关（`TranslatorKeepAliveEnabled` / `CodexAppKeepAliveEnabled` / `ClaudeAppKeepAliveEnabled`）至少有一个打开时执行。命中后用 `ThreadPool.QueueUserWorkItem` 在后台线程依次处理已武装的项：字幕翻译链路走 `TranslatorControlReader.TryEnsureStackAlive()`（内含 WMI 查询与最多四次 `Process.Start`），两个打包桌面应用走 `ProgramKeepAliveGuard.TryEnsureRunning()`（进程/WMI 探测 + `shell:AppsFolder` 拉起）。`translatorKeepAliveRunning` 单飞标志覆盖整轮，保证上一轮未结束时不叠加下一轮。该守护**不受任何看板可见性门控**——它要修复的正是系统睡眠把这些进程带走、此时没有任何看板在场的情况，因此不能复用 `TranslatorControlReader.RefreshIfDue` 那条 2000 ms、看板可见才驱动的路径。桌面应用的存在性判定一律 fail-safe：查询失败按"在运行"处理，宁可守护静默失效，也不能因误判每 30 秒反复拉起一个本来活着的应用。启动翻译器成功后（且 `TranslatorOverlayAutoOpenEnabled` 打开时）再同步调用一次 `TranslatorOverlayController.TryEnsureOverlayOpen()`：它等待翻译器主窗口出现（最多 12 秒）后按一次覆盖字幕按钮。这**不是**周期性动作——只在本程序刚把翻译器启动起来时发生一次，因为翻译器的覆盖窗是 toggle，周期性巡检会把用户自己关掉的窗口一次次重新打开。
- `WidgetForm.MaintainLiveCaptionsWindow()` 同样不建立 timer：在既有主控制 tick 上按 `LiveCaptionsTidyIntervalSeconds = 5` 自门控，仅在 `LiveCaptionsAutoHideEnabled` 打开时执行，命中后在后台线程做一次 `EnumWindows` 找 `LiveCaptionsDesktopWindow`，必要时 `SW_MINIMIZE` + `WS_EX_TOOLWINDOW`，`liveCaptionsTidyRunning` 单飞。间隔比保活短得多，因为它处理的是用户正看着的东西——一条置顶字幕栏横在画面上。**每个字幕宿主实例只处理一次**（按窗口句柄记忆）：用户自己还原的窗口不再被收起，否则就是程序跟用户抢窗口。
- `WidgetForm.MaintainCaptionOverlay()` 只在主控制 tick 上做**生命周期与门控**（面板开关、表面的懒创建、快时钟起停），并顺带踢一次轮询——翻译器没起来时快时钟是停的，这一脚就是发现它起来的那条路径。
- 字幕链路自 2.0.0.73 起有**本宿主中唯一不受 `PerformanceMode` 降速的时钟**：`WidgetForm.captionTimer`，间隔固定 `TranslatorCaptionReader.RefreshIntervalMs = 250`。原因是实测：主 tick 是 `GetWidgetSampleIntervalMs(PerformanceMode)`，BatterySaver 下 2500 ms，而字幕的价值以零点几秒计。旧写法还额外欠一拍——本轮把 UIA 读取扔给后台线程后**立刻拿上一轮的快照去渲染**，于是一读一显各占一个 tick，本程序自己就加了 2.5–5 秒。
- **重绘跟着读取走，不跟着时钟走**：`KickCaptionPoll()` 在后台线程读完后 `BeginInvoke` 回 UI 线程调用 `PresentCaptionSnapshot()`。UIA 慢一次只推迟它自己那一帧，不会让屏幕按时显示过期文本。单飞用 `Interlocked.CompareExchange`（一秒四次时普通 bool 的竞争窗口已经不可忽略，输掉竞争意味着两次重叠的跨进程读）。
- 快时钟**只在翻译器确实在产字幕且显示未挂起时运行**：`PresentCaptionSnapshot()` 按 `TranslatorRunning` 起停，`PrepareForDisplaySuspend` 停表并置 `captionDisplaySuspended`，后者专门用来挡住「挂起之后才完成的那次在途轮询」把时钟重新打开。从不运行翻译器的机器因此一次也不会付这四次/秒的跨进程读。
- 隐藏字幕条（`CaptionOverlayDisplayEnabled = false`）**不停快时钟**：它只阻止绘制（`CaptionOverlayForm.ShouldBeVisible` 返回 false，且 `WidgetForm.PresentCaptionSnapshot` 不再懒创建那个表面），读取与记录照旧。这是刻意的：文章靠 reader 比较相邻两次轮询来闩住定稿句，降低采样率会让句子在两次轮询之间来去而漏掉——因此隐藏字幕条省不下轮询开销。要连读取一起停掉是另一个开关（`CaptionOverlayEnabled`）。
- 悬停淡出（`CaptionOverlayHoverAutoHideEnabled`）走 `LayeredWidgetFormBase` 的**共享悬停轮询**，不在字幕时钟上：自有 timer，间隔 `HoverPollPolicy.IntervalMs = 120`，进 1 tick、出 3 tick 防抖——与右侧磁贴、左侧停靠页签完全同一套。只在字幕条可见、显示未挂起且该设置打开时开表。悬停态变化只调 `RenderLayeredWindow(false)` 重新混合，不重画内容——变的只是窗口 constant alpha（磁贴与页签则相反，它们改的是画面，因此要 `InvalidateLayeredRenderBuffer`）。
- `RecoverAfterDisplayResume` 必须给字幕条调用 `SetDisplaySuspended(false)` 并立刻呈现一帧。2.0.0.73 之前这一步根本不存在，而挂起路径是调用了 `(true)` 的——显示器睡一次之后字幕条在该进程剩余生命周期里永远不再出现（`UpdateSnapshot` 在 `displaySuspended` 时直接返回）。
- 全屏标志不停止采样；显示器关闭、会话锁定或系统挂起停止，恢复后清空时间戳并立即采样。
- `PowerThermalManualEnergySaverThresholdPercent` 只根据最近电池快照影响 `EnergySaverActive`，不新增轮询。
- `PowerThermalIntegratedEnabled` 只兼容读取且 UI 隐藏，不控制 owner、采样或可见性。

## 6. Network Dock owner

源码：`Core/NetworkMonitorForm.cs`、`Core/NetworkMonitorForm.Dock.cs`、`Performance/NetworkMonitorReader.cs`

| 项目 | 规则 |
| --- | --- |
| owner tick | 500/1000/3000 ms；只在 board 显示字段、尺寸或必要动画变化时重绘。 |
| Dock 交互 | tab/展开/外部点击/离开收起只由 `EdgeDockTabForm` 既有 120 ms tick 驱动。 |
| 本地网卡 | 首次、手动刷新、选择变化、网络事件或 2 s/5 s 到期；省电只事件驱动。 |
| 连通性 | 使用 §2 状态表；`AdapterMissing` 不周期请求。 |
| 滚动 PING | 仅 `Online`；性能/均衡/省电 2/5/10 s；网关与活动目标组单飞。 |
| 公网 IP | 仅 `Online`；5/10/15 min；只接受校验后的 IPv4。 |
| DNS | 地址签名变化立即测，否则按 §2 自适应表；单轮最多 2 个 DNS 并发。 |
| PathPing | 仅 board 展开时运行；均衡 3000 ms、省电 10000 ms，性能按有效模式实现取值；收起完全暂停发包。 |
| 固定 Ping | 复用 PathPing 可见门控与有效模式间隔，不创建 timer；每目标 1000 ms 超时。 |
| Network history | 内存缓冲，15 s、32 KiB 或进程退出时批量追加；启动修剪，运行中约 6 h 粗粒度修剪。 |

网络事件 30 s 防抖，只失效本地、连通性、公网 IP 与 DNS，并推进 generation。接口 ID、主 IP 或网关真正变化后才重置 GFW、云服务、PathPing 与滚动样本。所有后台任务提交前验证 generation、接口和 target/config signature。

### 6.1 GFW 与云服务

- GFW 周期范围 15-240 min，默认 30 min；只在真实 `Online` 且活动目标滚动丢包未达到确认门控时启动。
- 手动 token 只有成功占用单飞任务后才消费；任务占用时保留到下一轮。
- 云服务复用 GFW 间隔和 token，但与 GFW 结果完全解耦。
- 云服务手动刷新冷却 45 s；地区或目标列表变化强制相关源到期。
- 官方 API 正常缓存 30 min；普通 HTTPS 正常 15 min；异常/慢 2 min；无法连接 45 s；unknown 30 s。
- 云服务状态变化需 30 s 滞后确认；官方故障不受本地链路降级规则影响。
- generation、epoch、接口或目标签名变化使旧任务与完成日志失效。

### 6.2 Clean IP

- `CleanIpConnectionReader.Shared` 由 Network board 唯一展示，board 收起不停止 reader。
- `ConnectionCheckIntervalSeconds` 范围 15-600 s，默认 600 s，代码 fallback 60 s。
- 首次或断网恢复立即检测；另有每小时一次、正负 5 min 随机偏移计划。
- 错误状态按 10 min 时间槽重试，同一槽只试一次。
- 设置 token、Network board 刷新和 Operation 全局刷新都调用共享 `RequestRefresh()`。
- 测试模式只重建 clone；`requestRunning` 保证单飞。
- `AiChinaEgressGuardEnabled=true` 时，hidden `WidgetForm` 在既有主 tick 读取同一 `CleanIpConnectionReader.Shared` clone；不新增 timer，也不绕过 reader 的 15-600 s/整点/错误重试节流。
- 出口门控 TTL 为 10 min，并使用快照 `CheckedAtLocal`，重复 tick 不能续鲜。网络地址/可用性事件先把 `EgressIdentityCurrent=false` 并立即失效授权，替换网络查询成功后才能重新放行。
- 冷启动或换网期间未知出口静默阻断；明确大陆/GFW 墙内才显示全屏警告。确认境外的 false→true 边沿只调用 `CodexRadarForm.RequestSensitiveAiRefreshAfterEgressAuthorization()` 唤醒既有额度/Statuspage 调度；不刷新公共 Radar、DeepSeek 或其它网络探测。

## 7. Operation

源码：`Core/OperationForm.cs`、`Core/ForegroundFpsReader.cs`

| 项目 | 规则 |
| --- | --- |
| 动画 | 只在按压/悬停未收敛时启用，间隔为 16/33/100 ms。 |
| 全屏/显示挂起 | 停止动画与 FPS 展示 timer。 |
| 反向隐藏 | 复用 Widget shared interaction tick，不新增常驻 timer。 |
| 刷新 | 更新 MyASUS/系统按钮状态，并调用 `ForceRefreshAllModules()`。 |
| SeelenUI 进程 | Operation 可见时最多每 2 s 检查一次；命令后台单飞，最多等待 1500 ms。 |
| 内存饼图 | 仅对应模式下采样，最多每 2 s 一次，绘制只读缓存。 |
| FPS fallback | 仅应显示时运行，性能/均衡/省电 1/2/5 s；值不变不重绘。 |
| FPS 发现 | 首次/候选缺失时发现；前台变化后冷却 30 s；完整发现间隔 60 s。 |
| Radial 自动收回 | 默认 10 s，范围 1-60 s，0 禁用；复用鼠标事件与 shared tick。 |

设置按钮单击/双击使用系统双击时间仲裁；打开设置必须经 hidden host 的 `ShowSettingsWindow()`，先清理 Operation 瞬态状态，再激活已有或新窗口。

## 8. 左侧 boards

### 8.1 Work Board（WORKBENCH，合并 Spec + Codex 会话）

- 可见/自动监测使用既有 500 ms maintenance tick；文件变化 500 ms 防抖。**不存在第二个计时器**：退役的 Codex Task board 的 2 s 刷新折进这个 tick，以 `TaskSampleIntervalMs = 2000` 节流重采会话快照，仅在快照引用变化时重绘。
- 会话快照取自 `CodexTaskPresentation.GetSnapshot()`（内存克隆，零 IO）；展开时 `ShowBoardCore` 强制重采一次，避免首帧显示节流窗口内的旧数据。
- 自动监测时 watcher 覆盖账本与注册项目 spec 目录；60 s 轮询和 5 min 完整对账兜底。
- 读取单飞，运行中触发合并；隐藏、挂起、关闭或超时取消，迟到 generation 不提交。
- 自动弹窗基线只在首次完整快照播种；新 pending/needs_revision/awaiting_verify 项才弹出。**会话状态变化永不触发自动弹窗**——会话提醒走 `AlertCodexTaskEnabled` 与右侧 Codex tile。
- 会话时间线历史由 `CodexRadarForm.SampleCodexTaskTimeline` 在 owner 既有任务刷新批次中累积，**不依赖本看板存活或展开**；`WorkBoardTimelineMinutes` 决定窗口长度。
- 自动收回与外部点击复用既有 tick，不建立 mouse hook。
- 绘制路径调用纯函数 `WorkBoardComposer.Compose`：零 IO、零计时器、不修改入参，因此不引入任何调度语义。

### 8.2 GUARD

- `GuardBoardForm` 固定 500 ms 状态 tick，从隐藏构造起运行；board 收起不停止状态机。
- 可见时更新秒级倒计时，隐藏时只维护状态与 tab 颜色。
- 电源模式档位与省电模式是系统状态，别的程序、Windows 自身和用户都能改，因此**不缓存**：可见时每个 500 ms tick 用 `GuardRuntime.GetLivePowerModeTier()` 与 `NativeMethods.TryGetBatterySaverStatus()` 现读，两者都进重绘签名，外部变更 0.5 s 内跟随重绘；`ShowBoard()` 打开瞬间也重读一次并记录该签名，避免开板后第一次 tick 重复合成同一帧。
- 睡眠防护或亮屏计时活动时，每 30 秒幂等重申 Win32 电源请求与线程 ES 标志；失败项在后续周期重试，不增加请求引用计数。
- 网络未知按在线处理；只有明确离线且睡眠防护已武装时才累计到睡眠。
- 系统恢复清除旧离线起点并释放、重建电源请求句柄；离线必须重新累计完整阈值。
- 显示挂起只释放 board layered resources，不停止状态机；显示恢复释放旧句柄并立即重建当前电源请求。
- GUARD CLI 管道使用单一阻塞 worker 等待连接，不设轮询 timer；有效命令有界等待 UI 线程执行，复用相同状态和持久化路径。

### 8.3 Codex IQ

可见时每 5 s clone Radar owner 快照；隐藏、全屏或显示挂起时停止展示轮询。tab/收起/定位使用既有 500 ms maintenance tick，不发网络请求。

### 8.4 重置与速蹬

- `ResetSpeedBoardForm` 可见时每 5 s clone `BuildResetSpeedBoardSnapshot()`；500 ms maintenance tick 只负责 tab、外部点击、自动收回、定位与必要重绘。
- `CodexQuotaHistoryStore.Record()` 只经 `CodexRadarForm.RecordAcceptedQuotaHistory()` 写入，收到的是 `CaptureAcceptedQuotaForHistory()` 在 `ApplyQuotaResetProtections()` 之前留下的已接受读数；重置保护强制的 100 只作用于展示与缓存，不是一次新的额度采样，不进入历史。15 分钟内且变化小于 3% 的普通样本合并，周余量回升至少 5% 才登记重置事件。合并与重置分类都只与**同一 `account_key`** 的上一条比较。
- 账户切换（board 上点击账户 chip）不是一个刷新周期：`TrySwitchCodexAccount()` 重写 auth.json 后立即换出/换入该账户的 `QuotaRuntimeState`，并把 Codex 额度、provider usage 与 reset-credit 的下次到期时间一起清零，使下一个 owner tick 立刻为新账户取数；reset-credit 快照同时被丢弃，不跨账户沿用。切换本身不绕过任何单飞或冷却。
- 重置分类只使用无凭据摘要：旧 reset anchor 前 15 分钟到后 6 小时为自然重置；重置卡数同时下降为重置卡；其它确认回升为硬重置。
- JSONL 后台批量写入 `%LOCALAPPDATA%\DesktopCodexAssistant\codex-quota-seven-day-history.jsonl`；board 的 5 秒投影只 clone 已加载内存，不同步读文件。
- board 隐藏、全屏或显示挂起时停止展示轮询；历史记录继续服从 Radar owner 的额度调度，不建立 provider、reader 或网络请求。

### 8.5 系统日记

- hidden `WidgetForm` 在现有性能 feed 完成后调用 `SystemDayHistoryStore.RecordSample()`；`MinimumSampleSeconds = 55` 保证最多约每分钟一条，不新增硬件 timer 或 sampler。
- 连续 5 分钟无键鼠输入后记为 `Idle`，否则记为 `Active`；`PBT_APMSUSPEND` / `PBT_APMRESUME*` 另记睡眠边界事件。
- JSONL 由后台 `FlushIntervalMs = 15000` 批量落盘；挂起前同步刷出待写行，恢复后重新进入批量模式。保留 8 天、最多 13000 条。
- `SystemDayBoardForm` 可见时每 5 s clone 范围快照；500 ms maintenance tick 只处理 tab、外部点击、自动收回、范围点击、定位和必要重绘。
- board 隐藏、全屏或显示挂起时停止展示刷新；历史记录仍随 hidden host 的有效性能采样运行。绘制路径不读 JSONL，也不调用 Power/Thermal sampler。

### 8.6 字幕

- `TranslatorControlReader`（`Core/TranslatorControlReader.cs`）是 `WidgetForm` 直接构造并调用 `StartHeadlessDataOwner()`/`StopHeadlessDataOwner()` 的第三个 headless data owner，与 §4/§5 的 Codex/Power owner 同一套生命周期契约；但它不是 `Form`，没有 HWND，不接收 Windows 消息，因此不做 `InvokeRequired`/`Invoke` 编排——所有调用固定发生在 UI 线程。
- 轮询固定 `RefreshIntervalMs = 2000`（不随 `PerformanceMode` 变化），由 `CaptionsBoardForm` 自己的 500 ms maintenance tick 在每次 tick 调用 `RefreshIfDue(now, force:false)` 驱动；`RefreshIfDue` 内部按 `nextRefreshUtc` 自门控，实际每 2000 ms 才真正做一次 I/O，与 `RefreshSeelenUiStatus`（§7，`SeelenStatusRefreshIntervalMs = 2000`）同一节流写法。board 隐藏时其 maintenance tick 停止，`RefreshIfDue` 因此完全不被调用——不常驻轮询外部文件或进程。
- 每次到期刷新读取四项：`setting.json`（`ContextAware`/`NumContexts`/`Configs.OpenAI[ConfigIndices.OpenAI].ModelName`/`ApiUrl`）、`translation_history.db` 最近 8 行（经 `Core/MinimalSqliteReader.cs`）、四个外部进程的存在性（`LiveCaptionsTranslator.exe`、`geniex.exe`、Windows 自带 `LiveCaptions.exe` 按进程名；sanitize-proxy 的 `node.exe` 按 `Win32_Process.CommandLine` 含 `sanitize-proxy.js` 过滤），以及注册表值 `HKCU\Software\Microsoft\LiveCaptions\UI\CaptionLanguage`（`TranslatorControlReader.ReadCaptionLanguage`，键或值缺失时快照保持 `CaptionLanguageKnown = false`，不假定默认值）。`GetSnapshot()` 只读缓存 clone，board 绘制路径不做任何 I/O。
- 服务状态条的四个芯片（GenieX / 代理 / 实时字幕 / 翻译器）只反映上一轮快照，本身不做探测；只有处于停止态的芯片才注册点击目标，运行中的芯片完全不可点（GenieX 重载模型约 10 秒，误点代价过高）。点击启动走 `TranslatorControlReader.TryStartMonitoredService`（GenieX = `%LOCALAPPDATA%\GenieX CLI\geniex.exe serve`；代理 = `node "<translator root>\sanitize-proxy.js"`，两者均隐藏窗口并经 `cmd.exe /s /c` 把 stdout/stderr 重定向到 `<translator root>\logs\*.out.log`/`*.err.log`；实时字幕 = `%SystemRoot%\System32\LiveCaptions.exe` 无参数），翻译器芯片复用既有的 `TryToggleTranslatorRunning(start: true)`，不新增第二条启动路径。启动返回值只代表"已发起"，服务是否真的起来由下一轮 2000 ms 刷新判定。
- 全部交互控件（上下文感知开关、轮数步进、模型并列按钮、启动/停止按钮、四个服务芯片、字幕源芯片）走同一条异步链路：点击先同步置位 `operationRunning`/`pendingAction` 并立即重绘一次显示"…"过渡态，再用 `Task.Run` 在后台线程调用 `TranslatorControlReader` 的写入/启动方法，完成后通过 `BeginInvoke` 编组回 UI 线程清状态、刷新缓存快照并重绘；写入期间新点击一律忽略，不排队第二个写入。
- 设置写入（`TryApplySettingChange`）只改写目标字段，其余字段（`ApiKey`、`Temperature`、`Prompt` 等）原样回写；写入成功后若 `LiveCaptionsTranslator.exe` 正在运行则 kill + 以其自身目录为 WorkingDirectory 重新启动（该外部应用只在启动时读一次配置）；未运行则只保存，不主动拉起。
- 字幕源写入（`TryApplyCaptionLanguage`）是本 board 唯一的 HKCU 写入，只在用户点击字幕源芯片时发生，绝不由刷新触发。目标值由纯函数 `ResolveToggledCaptionLanguage` 决定：`en-US` ↔ `zh-CN`，未知/异常值一律解析为 `en-US`（原文英文才能让本地模型真正做 EN→ZH 翻译）。执行顺序固定为：先按点击瞬间采样的"翻译器是否在运行"决定路径 → 写注册表 → 若翻译器当时未运行则到此为止（不拉起任何进程）→ 否则 kill `LiveCaptions.exe`、kill `LiveCaptionsTranslator.exe`、等待 `ProcessRestartGraceMs` 后以其自身目录为 WorkingDirectory 重启翻译器（翻译器启动时会自行重新拉起 Live Captions）。
- 文章区（2.0.0.72 起取代「最近字幕」列表）不参与上面这条 2000 ms 刷新链路：它画的是 `CaptionTranscript`，由 `TranslatorCaptionReader` 在闩住一句时写入，与 `translation_history.db` 无关。绘制路径不做 I/O，也不做换行以外的计算。
- 换行结果按 `(CaptionTranscript.Revision, 文本宽度, LayerScale)` 缓存在 board 内，只有文章真的变了（或宽度/缩放变了）才重新换行；并且只换最新 `ArticleViewEntryLimit = 240` 句。整篇重新换行比整块看板其余部分加起来还贵，而看板每 500 ms 就重绘一次。
- 文章工具条必须在文章之后绘制：页码与两个翻页按钮的可用性都来自只有文章绘制过程才能做的实测（每半区能放几行、总共几页），先画工具条会让它永远落后一帧。
- 文章的全部控件（翻页、编辑、重置、导出、清除、句数）在 `ExecuteAction` 的 `operationRunning` 闸门与 reader 解析之前处理：它们不写 `setting.json`、不碰任何外部进程，没有理由在翻译器重启期间被挡住。「清除」需要两次点击（第一次上膛 6 秒），「句数」与编辑模式的位置保存走 `WidgetSettings.Load/Save`，由 `WidgetForm` 的设置文件监视器广播回其它表面。
- 字幕条编辑模式期间 `CaptionOverlayForm.ApplyGeometry` 直接返回：主控制 tick 仍在按 250 ms 喂快照，若它继续把矩形算回去就会与用户的拖动打架。编辑模式下面板即使没有任何字幕也保持可见（否则说话人一停就没得拖了），退出编辑模式后才恢复「没有文字就不显示」以及按已保存矩形（或自动带位）重新布局。
- 最近一次翻译失败的 toast 通知复用 `Core/ServiceAlertDebouncer.cs` 的 10 s 稳定窗口（`checking` 立即、新错误 10 s 稳定后触发、恢复立即清除），只在稳定态从"无失败"翻转为"有失败"的上升沿调用一次 `WidgetForm.ShowWindowsNotification`；看板内的失败提示条本身不防抖，直接反映 `translation_history.db` 最新一行是否以 `[ERROR]` 开头。
- board 隐藏、全屏或显示挂起时停止展示刷新；`TranslatorControlReader` 自身在 `WidgetForm` 退出前才 `StopHeadlessDataOwner()` + `Dispose()`。

## 9. Settings 与布局编辑

源码：`Settings/Win11SettingsForm.cs`、`Core/GlobalLayoutEditorForm.cs`

| 项目 | 规则 |
| --- | --- |
| 入口 | 只创建 `Win11SettingsForm`；`ShowInTaskbar=true`。 |
| 预览 | 变更后 75 ms debounce 应用，避免每个控件事件直接写运行时。 |
| 保存/取消 | 保存写 `settings.ini`；取消或异常关闭回滚打开时 baseline。 |
| 全局热键 | 只有 hidden `WidgetForm` 注册；设置变更先注销再按规范化签名注册。 |
| 全局布局 | 恰好 19 项：Operation、7 个 left dock tabs、11 个 right tiles。board、Settings、hidden host、headless owners 和 hover expand 不进入清单。 |
| 显示器 | 保存 `Screen.DeviceName`；主显示/work-area 继续作为右 tile 基线。 |
| Radar | 配置 Codex 数据 family/模型/源/周期、Codex 与 Claude 官方额度、DeepSeek DPAPI 凭据入口、服务与健康测试；不提供 Claude 社区模型/fallback 或 owner 可见几何。 |
| Power compatibility | `PowerThermalIntegratedEnabled` UI 隐藏，只兼容旧设置。 |
| Network | 只按 Dock topology 配置目标、board/tab 与 reader；不能切出第二展示形态。 |

GFW、Clean IP 和 Radar 随机测试的“立即刷新”递增对应 token；owner/reader 在成功占用单飞路径时消费。DeepSeek service monitor 不读取凭据；balance monitor 仅从 DPAPI / 环境变量读取 Key。网络变化或共享手动刷新使两者各自 deadline 到期，但二者保持独立状态。

## 10. 修改检查清单

修改刷新规则后至少检查：

1. 本文是否是该数字/触发条件的唯一登记处。
2. 所有后台任务是否有单飞、取消和迟到结果身份检查。
3. visible surface 是否只读快照，且 hidden/headless 对象未进入绘制与交互 tick。
4. 全屏与显示关闭是否区分：全屏只隐藏表面，显示/会话/挂起才暂停对应 backend。
5. Network 是否仍为 Dock-only，Clean IP 是否仍由共享 reader 提供。
6. 全局布局是否仍恰好 19 项。
7. 是否需要运行 `--test`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery`、`--test-radar-display-lifecycle`、`--test-operation-panel` 或对应 board render。
