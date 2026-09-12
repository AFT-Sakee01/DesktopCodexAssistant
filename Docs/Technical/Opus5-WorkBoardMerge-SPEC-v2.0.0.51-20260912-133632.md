# Spec Board 与 Codex Task 合并为 Work Board 实施规格

- 文档类型：Implementation SPEC（执行规格快照）
- 基线版本：`2.0.0.51`
- 创建时间：`2026-09-12T13:36:32+09:00`
- 生成模型：Opus 5
- 当前状态：`draft`，尚未授权执行
- 适用平台：ASUS UX3407N / UX3607O 专用 Windows on Arm 分支，ARM64
- 合并对象：左缘 Dock 的 `SpecBoard` 与 `CodexTask` 两个角色及其展开看板
- 目标交付：单一 **Work Board**（绘制标题 `WORKBENCH`）停靠板；左缘 Dock 由八角色收敛为七角色
- 修订：`2026-09-12T13:52+09:00` 按用户授权回填 §9 全部未决事项（板名、默认高度、P3、别名），正文其余部分未变；实现尚未开始

> 本文件是尚未执行的不可变规格。开始实现后不得直接修改正文；若范围、风险边界或验收条件需要变化，必须创建带新时间戳的替代 SPEC，并在 Technical INDEX 与 Spec Board 中标明取代关系。

---

## 0. 执行红线

1. **不得改动 Codex 任务后端。** `Core/CodexTaskMonitorReader.cs` 由 headless 的 `CodexRadarForm` 持有并注册进 `CodexTaskPresentation.SnapshotProvider`，本规格只改消费侧。禁止新增 watcher、timer、会话文件解析路径，禁止放宽「不解析正文、完整 cwd 和 rate_limits」的既有边界。
2. **不得改动 Spec 管理窗口。** `SpecBoardManagerForm` 是账本唯一程序内写入口，刻意不继承 `LayeredWidgetFormBase`，生命周期与主看板独立。本规格不触碰其布局、写入路径、危险区与回收站删除逻辑。
3. **不得让程序写 `PROJECTS.json`。** §4.2 的 `workspace_aliases` 是**只读消费**的可选字段：程序读到就用，读不到就走回退。新增或修改该字段是用户或外部 `spec-board` skill 的职责。`Docs/SpecBoard-Architecture.md`「存储边界」一节的只读约束继续成立。
4. **不得对 spec 与会话做逐条归属。** 后端不提供可靠依据，归属只做到**项目级**。禁止在账本、快照或 UI 文案中出现「该会话正在执行该 spec」这类断言。P3 的启发式提示只是提示，必须带显式 `≈` 标记、只在同项目内比对、绝不写账本。
5. **不得让会话事件触发 `SpecBoardAutoPopupEnabled` 弹窗。** 自动弹窗的触发源保持为 spec 账本与 `SpecGlob` 目录 watcher。会话告警继续走 `AlertCodexTaskEnabled` 与右侧 Codex tile。
6. **不得新增可见 Surface，不得新增 render 入口。** 支持的 render CLI 仍为 `--render-networkmonitor` / `--render-tilecolumn` / `--render-operation` 与既有 board renderer。
7. **不得改 `EdgeDockTabRole.SpecBoard` 枚举值的名字。** `LeftDockButtonOrder` 以角色 token 持久化，改名会让既有设置失效。合并后该角色的**显示名**改为 Work Board，枚举标识符保持 `SpecBoard`。
8. **每一期必须在上一期验收通过后才能开始。** P1 验收未通过时不得进入 P2 —— 这是本规格唯一的止损点，见 §7。
9. 不得吞异常、无限重试或保留双实现来规避验收。降级分支必须有明确终止条件与可见降级标识。
10. 本规格产生源码变更，因此 P1、P2 完成后按根 `AGENTS.md` 默认规则构建 ARM64、备份并覆盖正式 exe、用 `Start-DesktopAssistant.ps1` 重启。P0 为纯逻辑与自测，不触发部署。

---

## 1. 目标

把左缘 Dock 的 Spec Board（账本态：跨项目 spec，要做什么）与 Codex Task（现场态：本地 Codex 会话，正在做什么）合并为单一 **Work Board**，使以下两个问题在一次 hover 内同时可答：

- 这个 `pending` spec 当前有没有会话在做？
- 这个正在跑的会话属于哪个项目？

合并采用**项目为轴的统一行动流**：进行中的会话作为「进行中」段落坐在 spec 流水线顶端，左侧项目栏**同时过滤会话与 spec**。这是合并的核心收益，也是上下分区或视图切换两种备选形态无法提供的（见 §9.1）。

---

## 2. 范围外

- 右侧十一枚 `MetricTileForm` 磁贴，包括 Codex tile 与其 `MetricTileExpandForm` 展开面板的 token 明细 —— token 细节继续由该面板承载，不在 Work Board 重复。
- `CodexTaskMonitorReader` 的采集逻辑、会话发现、官方标题读取（`session_index.jsonl`）、告警判定。
- Spec 账本的读取边界、对账算法、`SpecBoardPathPolicy`、`SpecBoardLedgerStore` 写入路径、`SpecBoardSeenStateStore`。
- 其余六个停靠角色（Network / GUARD / Codex IQ / Reset·Speed / System Day / Captions）的行为。
- 配色方案变体（Typographic / AmberHud / WarmCard / Phosphor）的语义色定义。
- x64 构建与发布。

---

## 3. 现状事实基线

### 3.1 两块板的当前形态

| | Spec Board | Codex Task |
|---|---|---|
| 窗体 | `Core/SpecBoardForm.cs`（顶层类，继承 `LayeredWidgetFormBase`） | `OperationCodexTaskBoardForm`（`Core/OperationForm.CodexTasks.cs` 内嵌 private sealed class） |
| Dock 角色 | `EdgeDockTabRole.SpecBoard`，`DesignTokens.Colors.WarningDeep`（226,117,49） | `EdgeDockTabRole.CodexTask`，`DesignTokens.Colors.Success`（134,238,100） |
| 默认逻辑尺寸 | `SpecBoardWidth/Height` = 648 × 400，范围 240–700 × 240–800 | `CodexTaskBoardWidth/Height` = 648 × 400 |
| 数据源 | `SpecBoardReader.Read` 后台任务 → `SpecBoardSnapshot` | `CodexTaskPresentation.GetSnapshot()`（内存快照，零 IO） |
| 刷新 | `MaintenanceIntervalMs = 500`、`PollFallbackSeconds = 60`、`ReconcileIntervalMinutes = 5`、`ReconcileTimeoutMs = 3000` | `RefreshIntervalMs = 2000` |
| 布局 | 左项目栏 + 右行动流四段；`CompactRailMinimumLogicalWidth = 360` 以下进紧凑单列 | 双列气泡卡（`CodexTaskBoardMaximumRows = 10`）或时间线泳道，`CodexTaskBoardView` 二选一 |
| footer | `DrawBoardFooter`：管理 / 关闭 / 统计 | `DrawFooter`：时间线↔卡片 / 关闭 / 共 N |

### 3.2 数据契约

`SpecBoardRow`（`Core/SpecBoardReader.cs`）：`Id` / `Project` / `SpecPath` / `Title` / `Status` / `EventTimeUtc` / `FileMissing` / `IsUnregistered` / `ProjectRoot` / `UpdatedUtc` / 五个状态时间戳 / `UpdatedBy` / `Note` / `AbandonedReason`。状态封闭为 `pending` / `needs_revision` / `awaiting_verify` / `done` / `abandoned`，另有对账合成的 `unregistered`。

`SpecBoardProject`：`Name` / `Display` / `Root` / `SpecGlob` / `Reachable`。

`CodexTaskSnapshot`（`Core/CodexTaskMonitorReader.cs`）：`FileKey` / `TaskNumber` / **`WorkspaceLeaf`** / `Model` / `Status` / `StartedAtLocal` / `LastEventLocal` / `TerminalStatus` / `TerminalAtLocal` / `TerminalSilent` / `LastTokenUsage` / `TotalTokenUsage` / `ContextPercent` / `Title`。

`CodexTaskPresentation` 已有的映射：`BuildRing` / `BuildBadge` / `BuildRows` / `BuildTimeline` / `SampleTimeline` / `GetContextBarColor`；阈值 `ContextWarningPercent = 60`、`ContextCriticalPercent = 80`、`MaximumTimelineSegmentsPerTask = 96`。

### 3.3 连接键已天然存在

`CodexTaskSnapshot.WorkspaceLeaf`（会话 cwd 的叶目录名）可直接对上 `PROJECTS.json` 中 `projects[].root` 的叶名。本机注册表当前七项的叶名为：

```
DesktopCodexAssistant, CodexSleepGuard, SeelenNotificationGuard,
BilibiliautoPrint, WSLmanager, seelenUIprogram, 根本俊男教授课程库
```

前六项与会话 cwd 叶名同形，直接命中。第七项 `BunkyoUNV` 的 `root` 叶名是「根本俊男教授课程库」而会话 cwd 叶名多为 `BunkyoUNV`，属于必须靠别名解决的情形。**不需要放宽 reader 的 cwd 边界**。

### 3.4 会被合并影响的既有登记

- `EdgeDockTabRole` 八值枚举（`Core/EdgeDockTabForm.cs`）
- `LeftDockLayout.ResolveEnabledQueue` / `ResolveAutoTabBounds` / `ResolveTabCenterY`
- `GlobalLayoutEditorForm.BuildEditableSurfaceIds`（当前 20 项）
- `EdgeDockTabForm.RunSelfTest`（八角色映射、八枚自动 tab 不重叠断言）
- `BurnInProtection.CodexTaskBoardDockTabSalt`
- `WidgetSettings.CurrentSettingsVersion = 98`
- FEATURE_INDEX：`codex_radar.task_monitor_frontend`、`operation.left_edge_dock`、`window_layout.edge_column_auto_arrangement`、`window_layout.global_layout_structural_visibility`、`window_policy.single_style_schedule_scale_alert_hotkeys`
- 根 `AGENTS.md`「eight left-edge dock tabs/boards」与「exactly 20 structural items」两处

### 3.5 已确认的现存文档漂移（本规格顺带订正）

`Docs/SpecBoard-Architecture.md`「左缘停靠（EdgeDockTab）」一节仍写「七个固定停靠角色」并逐处使用「七枚 / 五枚」，但 Captions 角色已于 `2.0.0.48` 加入，实际为八个。该节在 P2 重写时必须按合并后的**七角色**事实重新表述，不得保留旧数字。

---

## 4. 目标架构

### 4.1 合成层 `WorkBoardComposer`（新增，纯函数）

新增 `Core/WorkBoardComposer.cs`，签名形如：

```
WorkBoardModel Compose(SpecBoardSnapshot spec, CodexTaskMonitorSnapshot tasks,
                       string projectFilter, DateTime nowLocal, WorkBoardLimits limits)
```

产出 `WorkBoardModel { IList<WorkBoardProjectRow> ProjectRows; IList<WorkBoardLiveRow> LiveRows; IList<WorkBoardSection> SpecSections; WorkBoardDiagnostics Diagnostics; }`。

约束：

- **零 IO、零 timer、零 UI 类型**（`Color` 除外，与 `CodexTaskPresentation` 现有边界一致）。
- 两个输入都已是 cache-only 克隆快照，合成层不得修改入参。
- 可脱离窗体构造与自测，供 `--test-operation-panel` 调用。
- `projectFilter` 为 `null` 表示「全部」；特殊值表示「未归属」，此时 `SpecSections` 必须为空集合（未归属没有 spec 行），`LiveRows` 只含无归属会话。

### 4.2 归属解析

解析顺序（全部 `OrdinalIgnoreCase`）：

1. `WorkspaceLeaf` == `project.Root` 的叶名
2. `WorkspaceLeaf` == `project.Name`
3. `WorkspaceLeaf` ∈ `project.workspace_aliases[]`（`PROJECTS.json` 新增**可选**字符串数组；缺失或类型不符时按空数组处理，不得因此判定整个注册表不可用）
4. 以上全不命中 → 归入「未归属」伪项目

边界：

- 叶名撞车（两个项目同叶名）时按注册表顺序取首个，并在**本轮聚合一条不含正文的诊断日志**，不弹窗、不改变 UI 行为。
- `project.Reachable == false` 的项目仍参与归属匹配（`root` 不可达不代表会话不属于它），与现有「不可达项目跳过对账」的规则互不影响。
- 归属结果只用于分组与过滤，不写入任何持久化文件。

### 4.3 合并后的可见结构

单一 Work Board，默认逻辑尺寸 **648 × 400 不变**（`SpecBoardWidth` / `SpecBoardHeight` 的默认值、范围与现状完全一致）。合并后段数由 4 增为 5，默认高度下空间更紧，因此段高度分配改用 §4.3.1 的降级阶梯，而不是提高默认值。用户需要更宽裕的版面时自行把 `SpecBoardHeight` 调到 800 以内任意值。

**标题栏**：`WORKBENCH` + spec 三色计数（红 = 未登记+需要执行、紫 = 需要修改、黄 = 等待验证）+ 分隔 + 绿色 `▶N` 活跃会话数 + 时钟。计数随项目过滤联动。

**项目栏**（宽布局）：在现有「新鲜度点 / 项目名 / 红-紫-黄计数」之后追加绿色 `▶N` 活跃胶囊；计数为零且无会话时仍显示 `✓`。存在无归属会话时，栏底以 `railsep` 分隔追加「未归属」伪行。该伪行**不参与**新鲜度标记、不写 `SpecBoardSeenState.json`。

**行动流段序**（自上而下，固定）：

| 段 | 语义色 | 来源 |
|---|---|---|
| `▶ 进行中` | `Success` | `LiveRows` |
| `◆ 未登记` | `WarningDeep` | 对账合成行 |
| `◆ 需要执行` | `Danger` | `pending` |
| `◆ 需要修改` | `AccentAlt` | `needs_revision` |
| `◆ 等待验证` | `Warning` | `awaiting_verify` |

段高度分配见 §4.3.1。

#### 4.3.1 段高度降级阶梯（默认 400 逻辑高的硬约束）

默认 648 × 400 下，标题栏与 footer 占去约 66 逻辑像素，行动流可用高约 334。单段「段头 + 一张完整卡片」按实测约 22 + 46 = 68，五段同时非空需要约 340 —— **超出可用高度**。因此旧的「每段至少保留一张完整卡片」契约在五段下不再普遍成立，替换为如下按序分配的降级阶梯：

1. **空段折叠**：`count == 0` 的段只绘制一行段头（含 `· 0`），不预留卡片位。`需要修改` 段在实际账本中长期为 0，这一条通常就能腾出一整段的高度。
2. **优先级排序**：按 `进行中` → `未登记` → `需要执行` → `需要修改` → `等待验证` 的顺序从上往下分配剩余预算。`进行中` 永远第一顺位 —— 它是最易变、最需要即时可见的一段。
3. **完整卡片分配**：在预算内，依次给每个非空段分配至少一张完整卡片；卡片数上限仍为宽布局 3 张（`进行中` 段）与既有 spec 段上限。
4. **退化为计数行**：预算耗尽后剩余的非空段退化为「段头 + 计数 + `+N`」单行，不绘制卡片。段头本身**永不省略**，保证五段的存在与数量始终可见。
5. **footer 不可侵占**：任何情况下 footer 与标题栏高度固定，降级只发生在行动流区内。

`SpecBoardHeight` 调高时阶梯自然向上退回：约 470 逻辑高可容纳五段各一张卡片，约 620 起接近合并前的宽松观感。该阶梯必须有自测（§5 P1），覆盖「五段全非空」「仅进行中非空」「全空」「需要修改为 0 的真实形态」四种输入。

**会话卡形态**（相对现有气泡卡瘦身）：

```
● #N  <WorkspaceLeaf>            [未归属]      <ContextPercent>%
<Title（官方会话标题，单行省略）>
<状态> · <时长> · <Model>              ▓▓▓▓░░░░░░  ← 细上下文条
```

- 圆环 `DrawWaterRing` 与四段 token 行**不进合并板**；上下文水位改用 4 逻辑像素高的细条，颜色继续走 `CodexTaskPresentation.GetContextBarColor`，阈值不变。
- 状态色继续走 `CodexTaskPresentation` 既有映射；`NeedsAttention` 的呈现方式保持现有语义。
- 宽布局「进行中」段默认展示上限 3 张，紧凑模式 1 张，超出走 `+N`。

**时间线**：footer 的「时间线」按钮把**右栏**换成泳道图，项目栏保留并继续过滤；项目栏、标题栏、footer 不变。泳道数据继续来自 `CodexTaskPresentation.BuildTimeline`。

**footer**：`管理` / `时间线`↔`卡片` / `关闭` + 统计（`✓done · ×abandoned · N 会话`）。「管理」行为不变，仍打开 `SpecBoardManagerForm`。

**紧凑模式**：`SpecBoardWidth < CompactRailMinimumLogicalWidth`（360）时项目栏隐藏、项目过滤复位为「全部」、每段上限压到 1 张，footer 始终保留 —— 沿用现有紧凑模式契约，只是段数变为 5。

### 4.4 存活窗体与生命周期

- **`SpecBoardForm` 是幸存者**：它已持有管理窗、账本 watcher、`SpecBoardSeenStateStore`、自动弹窗监测与显示挂起/恢复链路。
- **`OperationCodexTaskBoardForm` 删除**，`OperationForm.ToggleCodexTaskBoard` / `EnsureCodexTaskBoardForm` / `HideCodexTaskBoardIfVisible` / `PrepareForCodexTaskOverlayShow` / `DisposeCodexTaskBoardForm` / `ComputeCodexTaskBoardPlacement` / `ShouldDismissCodexTaskBoardClick` / `ComputeCodexTaskFooterLayout` 一并移除。
- 其 `RefreshIntervalMs = 2000` 折进 `SpecBoardForm` 既有 500 ms 维护 tick，以 **2 秒节流**重采会话快照；spec 侧的轮询与对账节奏不变。
- **时间线采样搬家**：`CodexTaskPresentation.SampleTimeline` 的调用点从「板的刷新 tick」移到 `CodexRadarForm` 既有的任务刷新批次（`RefreshCodexTaskMonitorIfNeeded` 之后）。这消掉现存的脆弱约束「采样必须在折叠判断之前执行，否则停靠收起时会断档」—— 累积是数据行为，不应依赖某块板存活或展开。搬家后 `SampleTimeline` 的静态累积字典语义不变。
- `OutsideClickDismissalMonitor` 消费者由两个减为一个；排除区仍含自身窗口、自己的 tab 与 Spec 管理窗。

### 4.5 设置迁移（`CurrentSettingsVersion` 98 → 99）

| 键 | 处理 |
|---|---|
| `SpecBoardWidth` / `SpecBoardHeight` | 保留为合并面尺寸；**默认值与范围均不变**（648 × 400，240–700 × 240–800） |
| `SpecBoardScaleOverridePercent` / `SpecBoardTransparencyOverridePercent` / `SpecBoardLeftDockTabCenterY` / `SpecBoardAutoHideSeconds` / `SpecBoardAutoPopup*` / `SpecBoardLedgerPath` / `SpecBoardManager*` | 全部保留，语义不变 |
| `CodexTaskBoardTimelineMinutes` | 迁移到新键 `WorkBoardTimelineMinutes`（同值、同范围） |
| `CodexTaskBoardView` | 迁移到新键 `WorkBoardView`（同枚举） |
| `CodexTaskBoardWidth` / `Height` / `LeftDockEnabled` / `LeftDockTabCenterY` / `ScaleOverridePercent` / `TransparencyOverridePercent` | 降为兼容持久化：`Normalize` 保留值但不再有消费者，设置 UI 与全局布局编辑器不显示 |
| `LeftDockButtonOrder` | 迁移：摘掉 `CodexTask` token，保留其余六项相对顺序，缺项按 canonical 顺序补齐 |
| `CodexTaskMonitor*` 全部后端键、`AlertCodexTaskEnabled` | **不动** |

新增键必须覆盖默认值、`Clone`、`Load`、`Save`、`Normalize`、设置 UI、迁移版本与 `--test-settings-bindings` 全链路（根 `AGENTS.md` 运行时不变量）。

### 4.6 拓扑

- `EdgeDockTabRole.CodexTask` 枚举值删除；`SpecBoard` 保留（红线 7），其 `Text` 改为 `Workbench`、`AccessibleName` 改为稳定名 `WorkbenchDockTab`（代码标识符 `WorkBoardComposer` / `WorkBoardModel` / `WorkBoardView` 等保持不变，只有面向用户与辅助功能的字符串改名），`EdgeDockTabForm.ResolveQueueAccent` 对该角色继续返回 `WarningDeep`。
- `BurnInProtection.CodexTaskBoardDockTabSalt` 退役。
- 左缘 Dock canonical 顺序变为：Network / **Work Board** / GUARD / Codex IQ / ResetSpeed / SystemDay / Captions（七项）。
- `GlobalLayoutEditorForm.BuildEditableSurfaceIds` 由 20 项减为 19 项（Operation + 7 Dock + 11 Tile）。
- `--render-operation` 的 `operation-codex-tasks*.png` 三张样张改由 `--render-specboard` 产出合并板样张（含「进行中」段、时间线视图、紧凑模式）；不新增 render 入口。

---

## 5. 分批实现

### P0 —— 合成层与归属解析（纯逻辑，零 UI）

1. 新增 `Core/WorkBoardComposer.cs` 与模型类型，按 §4.1 / §4.2 实现。
2. 新增 `WorkBoardComposer.RunSelfTest()`，挂进 `--test-operation-panel`，至少覆盖：
   - 叶名直接命中、`project.Name` 命中、alias 命中、全不命中落未归属；
   - 叶名撞车取注册表首个且只产生一条诊断；
   - `workspace_aliases` 缺失 / 非数组 / 含空串时按空数组处理且不影响其他项目；
   - `projectFilter` 为未归属时 `SpecSections` 为空、`LiveRows` 只含无归属会话；
   - 空 spec 快照 + 非空会话、非空 spec + 空会话、两者皆空三种退化；
   - 输入快照在 `Compose` 前后引用相等且内容未被修改。
3. `SpecBoardReader` 解析 `PROJECTS.json` 时读取可选 `workspace_aliases`，纳入现有有界读取（2 MiB / 64 KiB 行 / 64 项目）约束，超限计入 `MalformedLines`。

**P0 验收**：`--test-operation-panel` 全绿；`--test` 退出码 0；无 UI 与窗体改动；不部署。

### P1 —— 进行中段与项目栏胶囊（两块板并存）

1. 抽取 `DrawSection` 的卡片外壳为共享绘制原语（背景、圆角、左侧语义色条、内边距、命中矩形登记）。**不得**把会话伪造成 `SpecBoardRow` —— 那会让不存在的路径流进 `SpecBoardPathPolicy`、剪贴板复制与 shell 打开路径。
2. `SpecBoardForm` 接入 `WorkBoardComposer`，新增「进行中」段与会话卡绘制（§4.3），项目栏追加 `▶N` 胶囊与「未归属」伪行。
3. 段高度分配改为 §4.3.1 的降级阶梯（空段折叠 → 优先级排序 → 完整卡片 → 退化为计数行），并为其编写自测，覆盖「五段全非空」「仅进行中非空」「全空」「需要修改为 0 的真实形态」四种输入。**默认尺寸不改**。
4. 绘制标题由 `SPEC BOARD` 改为 `WORKBENCH`；`this.Text` 与 `AccessibleName` 同步（§4.6）。
5. 500 ms 维护 tick 增加 2 秒节流的会话重采；内容签名比较后才重绘。
6. 项目过滤同时作用于两半；紧凑模式复位规则不变。
7. **此期 `CodexTaskBoard` 仍然存在且行为不变**，供用户对照验收。

**P1 验收**：
- `--render-specboard sample` 与 `current` 产出含「进行中」段的样张，且紧凑样张 `specboard-compact.png` 仍满足「栏隐藏、过滤复位、卡片近全宽不越界不压 footer」；
- **默认 648 × 400 下五段段头全部可见、footer 未被侵占、无文字裁切**；降级阶梯自测四种输入全绿；额外产出 `specboard-fivesection-400.png` 与 `specboard-fivesection-620.png` 两张样张供目测对比；
- 标题栏显示 `WORKBENCH`；
- `--test-operation-panel`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery` 全绿；
- 真机验证：hover 展开、项目过滤联动两半、外部点击收回、自动弹窗仍只由 spec 事件触发；
- 构建 ARM64 → 备份 → 覆盖正式 exe → `Start-DesktopAssistant.ps1` 重启。

### P2 —— 退役 CodexTask tab（止损点之后）

1. 时间线采样调用点搬家（§4.4），并确认停靠收起、显示挂起、全屏隐藏三种状态下累积不断档。
2. 删除 `OperationCodexTaskBoardForm` 及 §4.4 列出的 `OperationForm` 成员。
3. footer 增加「时间线」按钮与右栏泳道视图（§4.3）。
4. 设置迁移 98 → 99（§4.5）。
5. 拓扑收敛（§4.6）：枚举、salt、canonical 顺序、`BuildEditableSurfaceIds`、`EdgeDockTabForm.RunSelfTest` 断言由八角色改七角色。
6. 文档与索引同步（§8）。

**P2 验收**：见 §6。

### P3 —— 疑似 spec ↔ 会话提示（用户已确认执行）

当 `CodexTaskSnapshot.Title` 包含某 spec 的文件主名（去掉 `-SPEC-v…` 之后的主题段）或 spec 标题时，在该会话卡的副行末尾显示 `≈ <spec 短标题>`。

约束：

1. 匹配为纯文本启发式，**只在归属命中的同一项目内**比对，跨项目不匹配。
2. `≈` 前缀为强制视觉标记，不得省略；命中多条 spec 时只显示最近更新的一条并追加 `+N`。
3. 只做提示：不参与分组、不影响任何计数、不写账本、不进 `WorkBoardModel` 的 `SpecSections`。
4. 新增门控键 `WorkBoardSpecSessionHintEnabled`，默认 `true`，走完整设置链路（默认值 / clone / load / save / normalize / 设置 UI / 迁移 / `--test-settings-bindings`）。
5. 比对在 `WorkBoardComposer` 内完成，保持纯函数与零 IO；自测覆盖「精确命中」「大小写差异命中」「跨项目不命中」「多条命中取最近并显示 `+N`」「关闭门控时无提示」五种输入。

**P3 验收**：上述五种自测全绿；`--render-specboard sample` 产出含 `≈` 提示的样张；真机确认关闭门控后提示消失且卡片版式不跳动。

---

## 6. 全局验收条件

第 1–13 条在 P2 完成后必须全部满足；第 14 条在 P3 完成后追加满足：

1. 左缘 Dock 实际可见七枚 tab，顺序、颜色、自动槽位与防烧屏微位移正确；`EdgeDockTabForm.RunSelfTest` 的七角色映射与七枚自动 tab 不重叠断言通过。
2. `--test`、`--test-layout`、`--test-settings-bindings`、`--test-operation-panel`、`--test-display-recovery`、`--test-codex-task-monitor` 全部通过，退出码 0。
3. `--test-settings-bindings` 覆盖 `WorkBoardView`、`WorkBoardTimelineMinutes`、`WorkBoardSpecSessionHintEnabled` 的默认值 / clone / load / save / normalize / UI 绑定；旧 `CodexTaskBoard*` 键在 98 版设置文件上能正确迁移且不丢值。
4. 全局布局编辑器恰好暴露 19 项，拖动自动模式任一 Dock 成员写整组 Y 偏移。
5. `--render-specboard sample|current`、`--render-specboardmanager sample`、`--render-operation`、`--render-tilecolumn` 全部产出且无裁切、无文字越界、无 footer 遮挡。
6. 真机：会话在项目栏正确归位；无归属会话进「未归属」行。`BunkyoUNV` 未补 `workspace_aliases` 时其会话落入「未归属」属预期行为（§9.2-5），不作为验收失败项；补入 `workspace_aliases: ["BunkyoUNV"]` 后必须归位。
7. 停靠收起 ≥ 10 分钟后展开，时间线历史连续无断档。
8. 自动弹窗仍只由 spec 账本与 `SpecGlob` watcher 触发；会话状态变化不触发弹窗。
9. Spec 管理窗口的打开、状态写入、批量、删除与危险区行为与合并前逐项一致。
10. 根 `AGENTS.md` 的拓扑描述、`Docs/SpecBoard-Architecture.md`、`Docs/Component-Refresh-Rules.md`、两份索引全部与实现一致；`python .\Docs\validate_docs.py` 全绿；`git diff --check` 无输出。
11. 构建使用 `Build-Arm64.ps1 -RequireTrackedSources`，从本地提交出发，`Build-Sources.json` 与实际源集一致。
12. 默认 648 × 400 下五段段头全部可见、footer 未被侵占、无文字裁切；§4.3.1 降级阶梯自测四种输入全绿。
13. 绘制标题为 `WORKBENCH`，`this.Text` = `Workbench`，dock tab `AccessibleName` = `WorkbenchDockTab`。
14. P3 的 `≈` 提示只出现在归属命中的同项目内；关闭 `WorkBoardSpecSessionHintEnabled` 后提示消失且卡片版式不跳动；提示从不影响任何计数。

---

## 7. 止损点与风险

**唯一止损点：P1 验收。** 若 P1 的合并板在真机上出现以下任一情况，不得进入 P2，应保留两块板并回退 P1 的可见改动：

- 五段同时非空时任一段无法保证一张完整卡片；
- 500 ms tick 叠加会话重采后出现可感知的 hover 展开延迟或重绘抖动；
- 项目过滤联动导致 spec 计数与合并前 `--render-specboard current` 的数值不一致。

| 风险 | 缓解 |
|---|---|
| **默认 400 逻辑高下五段放不下各一张卡片**（约需 340 / 可用约 334） | 用户已确认不提高默认值，改用 §4.3.1 降级阶梯：空段折叠 + 优先级分配 + 末段退化为计数行，段头永不省略。P1 验收必须目测 400 与 620 两张样张 |
| 时间线采样搬家改变累积语义 | 搬家与删板分开提交；P2 步骤 1 单独验证断档，通过后才做步骤 2 |
| 设置迁移丢用户自定义顺序 | `LeftDockButtonOrder` 迁移必须有自测：含 / 不含 CodexTask token、乱序、缺项三种输入 |
| 卡片壳抽取波及 Spec 既有命中区 | 抽取后立即跑 `--render-specboard` 与真机单击复制 / 双击打开回归 |
| 并发工作区：`Core/ProductIdentity.cs`、`AGENTS.md`、`Build-Sources.json` 当前均有他人未提交改动 | 执行前重新读取这三个文件；版本号提升与 `Build-Sources.json` 增删只在自己的提交内做最小改动 |

---

## 8. 交付物清单

**新增**
- `Core/WorkBoardComposer.cs`（含模型类型与 `RunSelfTest`）
- `Docs/Technical/` 对应 GoalSpec（执行完成时按根 `AGENTS.md` §执行规格交付生成）

**修改**
- `Core/SpecBoardForm.cs`、`Core/SpecBoardReader.cs`
- `Core/OperationForm.CodexTasks.cs`（删除内嵌板与相关成员）、`Core/OperationForm.SpecBoard.cs`
- `Core/CodexTaskPresentation.cs`（采样调用点契约注释）、`Core/CodexRadarForm.cs`（采样调用点）
- `Core/EdgeDockTabForm.cs`、`Core/LeftDockLayout.cs`、`Core/BurnInProtection.cs`、`Core/GlobalLayoutEditorForm.cs`
- `Settings/WidgetSettings.cs`、`Settings/Win11SettingsForm.cs`
- `Build-Sources.json`、`Core/ProductIdentity.cs`、根 `AGENTS.md`（版本与拓扑）
- `Docs/SpecBoard-Architecture.md`（重写左缘停靠节，订正 §3.5 漂移）、`Docs/Component-Refresh-Rules.md`、`Docs/CodexRadar-Architecture.md`（§9 Codex Task 后端消费方）
- `Docs/AGENTS.md` 文档地图（`SpecBoard-Architecture.md` owner 主题措辞）
- `Docs/Indexes/FEATURE_INDEX.jsonl`、`Docs/Interfaces/INTERFACE_INDEX.jsonl`、`Docs/Maintenance/CHANGELOG.jsonl`、`Docs/Technical/INDEX.jsonl`

**删除**
- `OperationCodexTaskBoardForm` 及其 render 样张入口

---

## 9. 已确认事项（用户已于 2026-09-12 授权前回填）

### 9.1 形态

采用**统一行动流**（用户确认）。两个备选已评估并否决，若日后改选需另发 SPEC：

- **上下分区**（上带会话横条 / 下带 spec 流）：实现更省事，但项目栏只能管下半边，丢掉合并的核心收益。
- **双视图切换**（footer 切「规格 / 任务」）：最省事，设置迁移与拓扑改动完全一样，但两块内容仍看不到一起，等于只省一枚 tab。

### 9.2 其余五项

| # | 事项 | 决定 |
|---|---|---|
| 1 | 合并板绘制标题 | 定为 `WORKBENCH`（大写英文，与其余六块板一致；避开架构文档中已用「工作台」描述 Spec 管理窗的措辞冲突）。代码标识符保持 `WorkBoard*` |
| 2 | `SpecBoardHeight` 默认值 | **保持 400 不变**，不提高。五段放不下的问题由 §4.3.1 降级阶梯解决；需要宽裕版面由用户自行调高（上限 800） |
| 3 | 「未归属」伪项目行 | 无未归属会话时**完全隐藏**（沿用规格原设计） |
| 4 | P3 疑似匹配 | **执行**。门控键 `WorkBoardSpecSessionHintEnabled`，默认 `true`，细则见 §5 P3 |
| 5 | `BunkyoUNV` 的 `workspace_aliases` | 用户表示无所谓。维持红线 3：程序只读消费该字段，不写 `PROJECTS.json`；不在验收清单中强制要求用户补该字段，`BunkyoUNV` 未补时其会话落入「未归属」属预期行为，不算缺陷 |

本节回填发生在实现开始**之前**，正文其余部分未变。实现一旦开始，本文件转为不可变快照。
