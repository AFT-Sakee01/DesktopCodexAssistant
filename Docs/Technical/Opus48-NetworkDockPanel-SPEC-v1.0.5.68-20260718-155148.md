# 网络窗口左侧停靠面板实施规格（Network Dock Panel SPEC）

- 版本：1.0.5.68
- 生成模型：Claude Opus 4.8
- 生成时间：2026-07-18T15:51:48+09:00（UTC 2026-07-18T06:51:48Z）
- 主题：网络监控窗口加入左侧梯形展开队列，重设计为信息完整的停靠面板

---

## 1. 目标

把网络监控窗口接入既有的左侧梯形停靠队列（现有成员：Spec Board、Codex Task Board），成为第三席。停靠展开时窗口尺寸跟随 `SpecBoardWidth` / `SpecBoardHeight`（默认 648×400），利用比常驻窗口（520×250）更大的空间，一次放下当前被截断或缺席的全部内容：

1. 完整 IPv6 地址、网关、MAC、WiFi 详情，不再缩字。
2. DNS 每台服务器独立成行（地址 + 延迟 + 状态）。
3. **质量区改为 pathping 语义：路径上每一跳都有独立延迟与丢包数据**（本 SPEC 最大的新增件）。
4. 连接检测窗口的三个徽章方格（纯净度评分 95A / 原生IP / 住宅IP）进入本面板，与出口 IP、归属地、ASN 组成"出口画像带"。

非目标：不改动连接检测窗口自身；不改动网络窗口的浮动（非停靠）形态；不新增任何外部 API 数据源。

---

## 2. 现状约束（实施前必须成立的前提）

- `EdgeDockTabForm`：10×30 逻辑右向梯形，`LogicalWidth = 10`、`LogicalHeight = 30`，靠 `HoverEntered` / `HoverExited` / `PollTick` 三个事件驱动宿主板。自动槽位公式 `工作区中线 ± LeftDockTabAutoOffsetY(=20)`。
- 停靠板定位约定：`workArea.Left + S(EdgeDockTabForm.LogicalWidth)`，纵向以 Tab 中心为中心，见 `SpecBoardForm.PositionAtLeftDock`。
- 收起机制：`LeftDockCollapseSeconds` 倒计时（`UpdateDockCollapse`）+ `OutsideClickDismissalMonitor`（`UpdateOutsideClickDismissal`）。
- `NetworkMonitorReader` 持有全部网络状态，UI 只拿克隆快照；绘制路径禁止发起 I/O。
- `CleanIpConnectionReader` 当前由 `ConnectionCheckForm` 独占（构造时 `new`、`OnFormClosed` 时 `Dispose`），内部按 `ConnectionCheckIntervalSeconds` + 整点计划 + 错误重试自节流。
- 两个窗口均由 `WidgetForm.OnShown` 创建，进程生命周期内常驻。

---

## 3. 交付项

### 3.1 PathPing 探测器（新文件 `Performance/PathPingProbe.cs`）

**数据模型**（加入 `Performance/PdhModels.cs`）：

```
PathPingHopSnapshot { int HopNumber; string Address; bool Responding; bool IsGateway; bool IsTarget;
                      double AvgLatencyMs; double LossPercent; int SampleCount; int MergedHopCount;
                      PathPingHopSeverity Severity }
PathPingSnapshot     { string TargetLabel; bool PathKnown; bool Stale; DateTime LastTraceLocal;
                       int RoundCount; PathPingHopSnapshot[] Hops; double EndToEndLatencyMs;
                       double EndToEndLossPercent; PathPingBlame Blame; int BlameHopNumber;
                       string BlameText; bool IcmpUnavailable }
PathPingHopSeverity  { None, Normal, RateLimited, Loss, Unresponsive }
PathPingBlame        { None, NodeRateLimit, LinkLoss, Unreachable, IcmpUnavailable }
```

两者实现 `Clone()`，遵循"reader 返回深克隆"约定。

**阶段一 · 路径发现**：对 `ConnectivityTarget`（`1.1.1.1`）以 `PingOptions.Ttl = 1..MaxHops(30)` 递增发送 ICMP，`DontFragment = true`，单跳超时 `TraceTimeoutMs = 1000`。`IPStatus.TtlExpired` 回包取 `reply.Address` 作为该跳地址；`IPStatus.Success` 表示到达目标，停止。无回包记为不响应节点（`Responding = false`）保留占位。

重新发现触发条件（满足其一）：
- `networkGeneration` 变化或 `InterfaceId` 变化（换网/换接口）；
- 距上次发现超过 `PathRediscoverIntervalMinutes = 10`；
- 连续 `PathFailureRediscoverRounds = 3` 轮目标不可达。

重新发现期间保留旧路径并置 `Stale = true`，UI 继续显示旧数据，不闪空。

**阶段二 · 逐跳滚动采样**：对每个 `Responding` 跳直接发 echo（不用 TTL），超时 `HopTimeoutMs = 1000`；每跳一个独立 `PingSampleWindow`（复用现有类），窗口 `HopSampleMinCount = 5` / `HopSampleMaxCount = 20`，TTL 沿用 `RollingPingSampleTtl`。每轮内各跳**串行**发包并在跳之间插入 `HopSpacingMs = 60` 错峰，避免突发。轮询间隔按性能模式与展开状态取值，见 §3.5。

**归因判定**（必须与真 pathping 语义一致，这是本模块的正确性核心）：

设跳 i 的丢包率为 `L(i)`，目标跳为 `T`。

- `L(i) >= HopLossWarnPercent(=2.0)` 且 `L(T) < HopLossWarnPercent` 且其下游所有跳丢包均低于阈值 → 跳 i 标 `RateLimited`（琥珀），**不**判定链路故障。中间路由器对直连 ICMP 限速极常见，误判成链路丢包是 pathping 类工具最典型的错误。
- 存在最小的跳 i 使得从 i 起到目标每一跳丢包均 `>= HopLossWarnPercent` → 跳 i 及其下游标 `Loss`（红），`Blame = LinkLoss`，`BlameHopNumber = i`，`BlameText = "丢包始于第 i 跳 <地址>"`。
- 目标不可达且路径中断 → `Blame = Unreachable`。
- 已有 `PingRollingSnapshot.IcmpBlocked` 为真 → 整个模块置 `IcmpUnavailable = true`，`Hops` 为空，UI 降级（§3.6）。

**连续不响应跳合并**：相邻的 `Responding = false` 跳合并为一行，`MergedHopCount` 记录合并数，`HopNumber` 取首跳号。显示上限 `MaxDisplayHops`（见 §3.6）。

**接入**：`NetworkMonitorReader` 新增 `PathPingProbeReader` 字段与单飞调度（对齐现有 `rollingPingRequestRunning` 模式），结果写入 `NetworkMonitorSnapshot.PathPing`。现有 `PingRollingSnapshot` **保留不动**——端到端指标与 GFW 抑制逻辑仍依赖它。

### 3.2 出口画像共享快照

`CleanIpConnectionReader` 增加进程级共享实例：

- 新增 `internal static CleanIpConnectionReader Shared`（懒加载，进程生命周期持有，不 Dispose）。
- `ConnectionCheckForm` 改为使用 `Shared`，并**移除** `OnFormClosed` 中的 `this.reader.Dispose()`。
- `NetworkMonitorForm` 同样使用 `Shared`，在其现有 timer tick 中调用 `GetSnapshot(CurrentSettings)`。

由于 reader 内部已按 `ConnectionCheckIntervalSeconds` + 整点计划 + 错误重试自节流，两个调用方共享同一节流状态，**外部 API 查询频次不因本改动增加**。这是本节唯一必须守住的不变量。

`Dispose()` 对共享实例必须幂等无害（加 `isShared` 守卫），因为 RenderSample 会构造并释放临时窗体。

### 3.3 停靠 Tab 与设置

- 新增第三个 `EdgeDockTabForm`，`logName = "NetworkMonitorDockTab"`，新增 `BurnInProtection.NetworkMonitorDockTabSalt` 与 `NetworkMonitorSalt`。
- 自动槽位：`工作区中线 - S(LeftDockTabAutoOffsetY * 3)`（即 −60），位于队列最上方；Spec(−20)、Codex(+20) 位置不变，三个 30px Tab 互不重叠。`EdgeDockTabForm.RunSelfTest` 增加三 Tab 不重叠断言。
- 新增设置键：`NetworkMonitorLeftDockEnabled`（bool，默认 false）、`NetworkMonitorLeftDockTabCenterY`（int，默认 `AutoLeftDockTabCenterY`，复用 `NormalizeLeftDockTabCenterY`）。按根 `AGENTS.md` 要求覆盖 defaults / clone / load / save / normalize / 设置 UI / migration / `--test-settings-bindings`。
- **Tab 状态变色**（三个 Tab 中唯一动态着色者）：`Offline` / `AdapterMissing` → `DangerGlyph`；`LocalNetworkDegraded` 或 `Blame = LinkLoss` → 琥珀；否则 `DesignTokens.Colors.Accent`。停靠收起后大红叉不可见，Tab 变色是替代的被动告警通道，不可省略。
- 停靠启用时：常驻窗口隐藏，仅由 Tab 悬停展开；停靠关闭时行为与现状完全一致（双态并存，无破坏性）。

### 3.4 Docked 布局绘制

新增渲染分支 `DrawContentDocked`，与现有 `DrawContentClassic` 并列，由停靠状态选择；坐标沿用 Classic 的归一化常量模式，参考尺寸 648×400。

垂直分配（逻辑像素，合计 396 ≤ 400）：

| 区域 | 高度 | 内容 |
|---|---|---|
| 头部条 | 40 | `NETWORK` + 接入状态 + 接口/链路摘要；右对齐更新时间 |
| 出口画像带 | 52 | 三个 40×40 徽章方格（评分绿 / 原生蓝 / 类型紫）+ 右侧两行：出口 IP · 归属地 · 组织；ASN · `IpTypeReason` · 检测时间 |
| 双栏主体 | 272 | 见下 |
| 底部条 | 32 | `LastError` + 连通性目标 + 轮次/路径发现时间 |

双栏主体（左 330 / 分隔线 / 右 300）：

- 左栏：身份地址组（接口+MAC、IPv4+网关、IPv6 全串）→ 分隔 → DNS 组（每台一行）→ 分隔 → 出境组（GFW + 云端点逐行全名）。
- 右栏：`PATHPING → <目标>` 表头 + 端到端汇总 → 逐跳表（列：跳号 / 节点 / 延迟 / 丢包，等宽字体右对齐数值）→ 分隔 → 归因结论行（`BlameText`，按 `Blame` 着色）。

`MaxDisplayHops` 随高度伸缩：400 高时 8 行，更高时按行高放宽至 12。

### 3.5 刷新与调度（须同步 `Docs/Component-Refresh-Rules.md`）

| 状态 | pathping 轮询间隔 |
|---|---|
| 停靠展开 / 浮动可见 · Balanced | 3000ms |
| 同上 · PowerSaver | 10000ms |
| 停靠收起 | 暂停逐跳采样，仅保留既有端到端探测 |

停靠收起时不发逐跳包，是本功能常态开销的关键护栏：收起状态相对现状几乎零额外流量。路径发现本身只在 §3.1 的触发条件下运行，不随轮询频率发生。

### 3.6 降级矩阵

| 条件 | 行为 |
|---|---|
| 宽度 < `DockedSingleColumnMinWidth = 460` | 双栏合并为单列纵栈；跳表保留，合并策略更激进，`MaxDisplayHops` 减半；画像带保留（本就是横向布局，压缩右侧上下文） |
| `IcmpUnavailable` | 右栏整栏改显 `PingRollingSnapshot` 三级定点诊断 + "ICMP 不可用，无法逐跳探测"提示行，不留空白 |
| `PathKnown = false`（首次发现中） | 右栏显示"正在发现路径…"占位行 |
| `Stale = true` | 表头追加"路径刷新中"灰字，旧数据继续显示 |
| CleanIp 未检测 / 检测失败 | 三方格显示 `--`（`ScoreLabel` 已有此语义），画像带**不隐藏**（留位优于布局跳动） |
| 无 DNS / 无云端点 | 对应组显示 `--` 单行，不塌陷 |

---

## 4. 验证要求

1. `EdgeDockTabForm.RunSelfTest`：三 Tab 槽位互不重叠且都在工作区内。
2. `PathPingProbe.RunSelfTest`（新增，纯函数，不发包）：
   - 节点限速场景（中间跳丢包、目标不丢）→ `Blame = NodeRateLimit`，该跳 `RateLimited`；
   - 链路丢包场景（自第 5 跳起持续到目标）→ `Blame = LinkLoss` 且 `BlameHopNumber = 5`；
   - 连续不响应跳合并计数正确；
   - `IcmpBlocked` → `IcmpUnavailable`，`Hops` 为空。
3. `--test-settings-bindings` 覆盖两个新设置键。
4. `--test-layout` 通过；新增 docked 渲染采样入口出 PNG，人工核对四区高度与两栏内容不溢出。
5. 按根 `AGENTS.md`：构建 ARM64 → 备份现有正式 exe → 覆盖 → 重启。

## 5. 文档同步（§4 触发表）

- `Docs/NetworkMonitor-Architecture.md`：新增停靠形态、四区布局、pathping 模块。
- `Docs/Component-Refresh-Rules.md`：pathping 轮询间隔与收起暂停规则。
- `Docs/Indexes/FEATURE_INDEX.jsonl`：网络窗口行更新 + 新增 pathping 功能行。
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`：`PathPingProbe`、`CleanIpConnectionReader.Shared`、两个新设置键。
- `Docs/Maintenance/CHANGELOG.jsonl`：一条 `feature` 变更记录 + 一条 `deployment` 记录。
- 根 `AGENTS.md`：`Current version` 修正为 1.0.5.68（现记录 1.0.5.64 已落后于实际的 1.0.5.67）。
