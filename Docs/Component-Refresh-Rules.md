# 组件刷新规则

适用版本：2.0.0.2

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
| 悬停动画 | 16 ms | 33 ms | 100 ms | 只在透明度/按压动画未收敛时运行 |
| 静止交互轮询 | 30 ms | 100 ms | 250 ms | 鼠标、自动穿透、自动隐藏、两级防烧屏和层级维护 |
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
| 应用窗口事件 | 前台窗口 Hook 始终启用；只有最大化自动隐藏或全屏/最大化/遮挡可见性模式才启用对象 Hook 和主采样周期完整枚举。事件按 HWND 合并，125 ms 批处理，每批最多 64 项，队列上限 256 项；溢出退化为一次完整枚举。同产品测试/辅助进程的窗口事件按 PID 身份缓存过滤。 |
| 昂贵硬件 | GPU/NPU 按 1/2/5 s 独立 deadline；不能被更快的 CPU tick 放大。 |
| 设置热加载 | `settings.ini` 由 `FileSystemWatcher` 与主 tick 的修改时间检查共同覆盖。 |
| 右侧 tiles | 10 个 `MetricTileForm` 只消费同一次 feed；方块和 hover expand 不自行采样。 |
| 左侧 docks | 5 个 `EdgeDockTabForm` 复用各自既有 hover tick；展开 board 的业务刷新由 owner 管理。 |
| 全屏 | 隐藏 visible tiles/tabs/boards/Operation；不停止 Radar/Power headless backend。 |
| 显示恢复 | 首轮延迟 350 ms，最多 3 轮；后续重试 1500 ms。重枚举 work-area、重建 visible layered resources、重定位并强制刷新。 |
| Win+D | 全局 Win+D 后延迟 2000 ms 执行本程序和 SeelenUI 拉前；不拦截系统显示桌面。 |
| 休眠唤醒 | `PBT_APMRESUME*` 后完成显示恢复，再按设置重启 SeelenUI/本程序；30 s 内重复恢复事件只处理一次。 |
| 强制刷新 | `ForceRefreshAllModules()` 使 PDH、磁盘用量、Radar、Power/Thermal 与 Network 到期；Network 同时请求共享 Clean IP reader。 |
| 诊断 | 主采样与 12 h timing 摘要最多每 15 min 记录一次；UI watchdog 后台每 2 s 检查心跳，超过 10 s 记录，持续卡住每 30 s 重复，恢复后补一条 responsive 记录。快照包含 UI managed thread ID、窗口事件接收/合并/丢弃/溢出/处理/批次/完整刷新计数和当前待处理量。 |

hover/自动隐藏规则：

- 敏感鼠标模式默认以鼠标为中心的 100 px 正方形与 visible surface 相交，范围 10-300 px；关闭后按点命中。
- 离开判定区后延迟显现默认 1 s；倒计时内重新进入需连续停留默认 0.5 s 才重置。
- 自动隐藏激活后普通鼠标移动不释放，必须进入任一 visible surface 的敏感范围。
- 两级防烧屏默认开启：`BurnInLevelOneIdleSeconds = 10` 后进入一级，随后 `BurnInLevelTwoDelaySeconds = 30` 后进入二级；允许范围分别为 1-300 秒和 1-600 秒。
- 防烧屏复用 hidden `WidgetForm` 的静止交互轮询和五个 `EdgeDockTabForm` 的 120 ms hover tick，不新增 timer。一级/二级激活后，纯鼠标移动只驱动左 tab 局部恢复或右侧 tile/expand 整组恢复；点击、滚轮、键盘输入、显示挂起与布局编辑会归零状态并重新计时。
- Operation RadialDial 核心 keep-alive 复用共享交互 tick，不建立 timer；达到空闲阈值后只改变一次核心视觉。
- hidden host 只协调防烧屏状态，不提交像素；headless owners 不进入 hover、click-through、burn-in 或 Z-order 轮询。

## 4. Codex / Claude Radar headless owner

源码：`Core/CodexRadarForm.cs`、`Core/CodexRadarForm.RuntimeState.cs`、`Core/CodexRadarForm.ProjectionState.cs`、`Core/CodexRadarForm.TileSnapshot.cs`、`Core/OwnerOperationGeneration.cs`、`Core/ClaudeCodeUsageReader.cs`、`Core/ClaudeCodeUsageScheduler.cs`、`Core/DeepSeekServiceMonitor.cs`

| 项目 | 规则 |
| --- | --- |
| 生命周期 | `WidgetForm` 构造后显式调用 `StartHeadlessDataOwner()`；owner 创建隐藏 HWND 并启动 backend scheduler，但不调用 `Show()`。Start/恢复建立 generation，Stop/挂起先取消并失效 generation，重复调用幂等。 |
| owner tick | 使用普通调度 500/1000/3000 ms，并贴近秒边界；tick 只检查各数据源 deadline 与单飞状态。 |
| family 隔离 | Codex family 保存公共 Radar/模型/额度；Claude family 只保存官方额度与服务状态。请求同时捕获 family 与 owner generation；迟到结果不得写状态、缓存、日志、通知或 UI。 |
| visible snapshots | producer 一次替换 `RadarPublishedProjectionState`；`BuildRadarTileSnapshot`、`BuildCodexIqBoardSnapshot`、`BuildServiceHealth` 和 task provider 从同一 published state clone，不触发网络、provider、磁盘或自动切换。 |
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
| Codex 本地 session fallback | 性能/均衡/省电 10/15/30 s | 仅 provider 无新鲜快照 | 只在 Codex 正在运行时 |
| Claude Code usage | 5 min | 普通失败 10 min；HTTP 429 15 min | selected-provider gate、setup token/恢复/手动刷新 |
| OpenAI/Anthropic Statuspage | 正常 15 min | 异常或失败 2 min | 启动、网络变化、服务 token、手动刷新 |
| DeepSeek service monitor | 正常 60 s | 失败/未知 5 min | 启动、网络变化、手动刷新 |

约束：

- 各来源单飞；Claude 官方 usage 的消费者 join 同一 scheduler 请求，同一 Statuspage serviceKey 和 DeepSeek service 请求各只发一次。
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

### 8.1 Spec Board

- 可见/自动监测使用既有 500 ms maintenance tick；文件变化 500 ms 防抖。
- 自动监测时 watcher 覆盖账本与注册项目 spec 目录；60 s 轮询和 5 min 完整对账兜底。
- 读取单飞，运行中触发合并；隐藏、挂起、关闭或超时取消，迟到 generation 不提交。
- 自动弹窗基线只在首次完整快照播种；新 pending/needs_revision/awaiting_verify 项才弹出。
- 自动收回与外部点击复用既有 tick，不建立 mouse hook。

### 8.2 Codex Task board

任务数据由 Radar owner 的共享 task provider 更新；board 只做展示、tab、收起和绘制节流，不建立第二个 session watcher 或递归 reader。

### 8.3 GUARD

- `GuardBoardForm` 固定 500 ms 状态 tick，从隐藏构造起运行；board 收起不停止状态机。
- 可见时更新秒级倒计时，隐藏时只维护状态与 tab 颜色。
- 网络未知按在线处理；只有明确离线且睡眠防护已武装时才累计到睡眠。
- 系统恢复清除旧离线起点，必须重新累计完整阈值。
- 显示挂起只释放 board layered resources，不停止状态机。

### 8.4 Codex IQ

可见时每 5 s clone Radar owner 快照；隐藏、全屏或显示挂起时停止展示轮询。tab/收起/定位使用既有 500 ms maintenance tick，不发网络请求。

## 9. Settings 与布局编辑

源码：`Settings/Win11SettingsForm.cs`、`Core/GlobalLayoutEditorForm.cs`

| 项目 | 规则 |
| --- | --- |
| 入口 | 只创建 `Win11SettingsForm`；`ShowInTaskbar=true`。 |
| 预览 | 变更后 75 ms debounce 应用，避免每个控件事件直接写运行时。 |
| 保存/取消 | 保存写 `settings.ini`；取消或异常关闭回滚打开时 baseline。 |
| 全局热键 | 只有 hidden `WidgetForm` 注册；设置变更先注销再按规范化签名注册。 |
| 全局布局 | 恰好 16 项：Operation、5 个 left dock tabs、10 个 right tiles。board、Settings、hidden host、headless owners 和 hover expand 不进入清单。 |
| 显示器 | 保存 `Screen.DeviceName`；主显示/work-area 继续作为右 tile 基线。 |
| Radar | 只配置 Codex 数据 family/模型/源/周期、Codex 与 Claude 官方额度、服务与健康测试；不提供 Claude 社区模型/fallback、DeepSeek key/余额或 owner 可见几何。 |
| Power compatibility | `PowerThermalIntegratedEnabled` UI 隐藏，只兼容旧设置。 |
| Network | 只按 Dock topology 配置目标、board/tab 与 reader；不能切出第二展示形态。 |

GFW、Clean IP 和 Radar 随机测试的“立即刷新”递增对应 token；owner/reader 在成功占用单飞路径时消费。DeepSeek service monitor 不读取凭据，网络变化或共享手动刷新直接使其 deadline 到期。

## 10. 修改检查清单

修改刷新规则后至少检查：

1. 本文是否是该数字/触发条件的唯一登记处。
2. 所有后台任务是否有单飞、取消和迟到结果身份检查。
3. visible surface 是否只读快照，且 hidden/headless 对象未进入绘制与交互 tick。
4. 全屏与显示关闭是否区分：全屏只隐藏表面，显示/会话/挂起才暂停对应 backend。
5. Network 是否仍为 Dock-only，Clean IP 是否仍由共享 reader 提供。
6. 全局布局是否仍恰好 16 项。
7. 是否需要运行 `--test`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery`、`--test-radar-display-lifecycle`、`--test-operation-panel` 或对应 board render。
