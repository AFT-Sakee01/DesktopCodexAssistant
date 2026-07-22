# 界面收敛：退役全部经典浮窗，只保留磁贴/停靠板/设置/操作面板（Interface Convergence SPEC）

- 版本：1.0.6.25（或执行时的下一个可用版本号；本 SPEC 建议拆成多个小版本分阶段落地）
- 生成模型：Claude Opus 4.8
- 生成时间：2026-07-21T18:46:23+09:00（UTC 2026-07-21T09:46:23Z）
- 主题：把桌面 UI 收敛为**四类可见面**——① 右侧 10 个指标磁贴及其扩展窗；② 左侧 5 个停靠 tab 及其面板；③ 设置界面；④ 左下角操作面板。除此以外的所有**经典独立浮窗形态**全部退役。

---

## 0. 最重要的约束（先读，红线）

**"废弃窗口" ≠ "删除类"。** 有两个窗口是右侧磁贴 / 左侧看板的**数据 owner**，只能改成永久隐藏的 headless 实例，**删了对应磁贴就没数据**：

| 窗口 | 它喂谁 | 处置 |
|---|---|---|
| `CodexRadarForm` | 右侧 `CodexQuota`/`ClaudeQuota` 磁贴（`BuildRadarTileSnapshot`，WidgetForm.TileColumn.cs:213-214）+ 左侧 Codex IQ 看板（`BuildCodexIqBoardSnapshot`，"sole data owner"，OperationForm.CodexIqBoard.cs:3） | **保留实例，headless 化**（永不 Show，只当数据源） |
| `PowerThermalForm` | 右侧 `Power` 磁贴（`BuildStripSnapshot`，WidgetForm.TileColumn.cs:184） | **保留实例，headless 化** |

同理，多个 **reader 是数据层**，窗口废了也必须留：`CleanIpConnectionReader.Shared`（被左侧 Network 面板复用，Dock.cs:413）、`NetworkMonitorReader`、`ClaudeRadarReader` / `ClaudeRadarSnapshotScheduler`（headless CodexRadarForm 的 Claude 数据）、各 `PerfSnapshot` 采样。

违反这条红线的表现：右侧 Power / Codex / Claude 磁贴、左侧 IQ 看板变空白。验证第 (6) 项专门守它。

---

## 1. 目标

当前一份数据往往有**两套并存的可见形态**：经典独立浮窗（旧）与磁贴/停靠面板（新）。本 SPEC 砍掉旧的一套，只留新的四类可见面，让"目前的窗口"= 用户实际在用的那套。

具体：
1. 主窗口强制磁贴列，退役"经典主面板"呈现。
2. Radar 强制磁贴 + headless 数据 owner，退役"经典 Radar 独立小窗"呈现。
3. 功耗温度退役独立窗，改 headless 数据 owner（Power 磁贴照常）。
4. 网络监控只保留左侧停靠形态，退役"浮动（undocked）"形态。
5. 删除连接检测独立窗（`ConnectionCheckForm`），出口画像 reader 转由 Network 面板独占。
6. 删除独立 Claude 窗（`ClaudeRadarForm`）——本项吸收并取代既有的 [Opus48-StandaloneClaudeWindowRetirement-SPEC](Docs/Technical/Opus48-StandaloneClaudeWindowRetirement-SPEC-v1.0.6.25-20260721-091125.md)，见 §7。
7. 设置收敛：删除所有已无可见消费者的窗口位置/尺寸/透明度/缩放/显示器/呈现模式键，schema 递增并一次性迁移。

**非目标**：不动数据采集与额度算法；不动右侧磁贴、左侧五板、设置、操作面板这四类的功能与外观；不改共享数据 reader 的取数频率与缓存不变量。

---

## 2. 现状普查（分层窗口全集，已核实）

`LayeredWidgetFormBase` 子类共 17 个，按去留归类：

| 窗口类 | 角色 | 归类 |
|---|---|---|
| `WidgetForm` | 根宿主 | 保留（但退役经典面板呈现） |
| `MetricTileForm` ×10 | 右侧磁贴 | **保留（四类之一）** |
| `MetricTileExpandForm` | 磁贴扩展窗（自绘 Cpu/Mem/Disk/Net/Gpu/Npu/Thermal/BurnDown，吃快照不复用旧窗渲染） | **保留** |
| `EdgeDockTabForm` ×5 | 左侧 5 个梯形 tab | **保留** |
| `NetworkMonitorForm` | Network（docked=保留 / 浮动=废） | 保留类，删浮动形态 |
| `SpecBoardForm` | 左侧 SpecBoard 面板 | **保留** |
| `OperationCodexTaskBoardForm` | 左侧 Codex 任务面板 | **保留** |
| `GuardBoardForm` | 左侧 GUARD 面板 | **保留** |
| `CodexIqBoardForm` | 左侧 Codex IQ 面板 | **保留** |
| `OperationForm` | 左下操作面板 | **保留（四类之一）** |
| `OperationQuickGridForm` / `OperationLauncherTrioForm` | 操作面板子件 | **保留** |
| `CodexRadarForm` | Radar 可见窗 + **数据 owner** | **headless 保留**（§0） |
| `PowerThermalForm` | 功耗温度可见窗 + **数据 owner** | **headless 保留**（§0） |
| `ClaudeRadarForm` | 独立 Claude 窗 | **删除**（§0 数据层转 headless CodexRadarForm） |
| `ConnectionCheckForm` | 连接检测独立窗 | **删除**（reader 保留） |
| `OpacityPolicyProbeForm` | 透明度策略自测探针 | 保留（基础设施/自测，非功能窗） |

其他 `Form`：`Win11SettingsForm`（设置主窗，保留）、`GlobalLayoutEditorForm`/`NetworkProbeTargetEditorForm`/`SpecBoardManagerForm`/`ClaudeRadarModelMapEditorForm`（设置子窗，保留）、`AiQuickMenuForm`（操作面板触发，保留）、`MaskForm`（遮罩基础设施，保留）。

右侧 10 磁贴（`MetricTileId`）：`Cpu / Memory / Disk / Network / Gpu / Npu / Power / Guard` + Radar 磁贴 `CodexQuota / ClaudeQuota`。左侧 5 tab（`EdgeDockTabRole`）：`Network / SpecBoard / CodexTask / Guard / CodexIq`。

---

## 3. 交付项（分阶段，每阶段可独立编译/部署/回滚）

### 阶段 1 · 强制新呈现模式，退役经典面板/浮动路径

- `WidgetForm`：`MainWidgetPresentationMode` 强制 `EdgeTileColumn`、`RadarPresentationMode` 强制 `EdgeTiles`（收敛为单一路径）。删除"经典主面板"绘制与其独立窗显示分支（`ShouldHideClassicPanelWindow` 等相关逻辑简化为恒真隐藏经典面）。保留磁贴列创建/定位。
- `NetworkMonitorForm`：`NetworkMonitorLeftDockEnabled` 强制真，删除 undocked 浮动形态的显示路径与经典 strip 绘制（`DrawContentDocked` 保留）。
- 验收：主窗口只剩右侧磁贴列；网络只在左侧 tab 悬停展开；无经典浮窗残留。

### 阶段 2 · CodexRadarForm / PowerThermalForm headless 化

- 两者在 `WidgetForm` 里继续 `new`、继续 `ApplyRuntimeSettings` 与数据轮询，但**永不 `Show` 到屏幕**（构造后即 `SetHiddenForFullscreen(true)` 并跳过定位/可见性策略）。确保 `BuildRadarTileSnapshot` / `BuildCodexIqBoardSnapshot` / `BuildStripSnapshot` 仍按现有节流产出。
- 删除这两个窗口的"可见形态"专属逻辑：定位、显示器分配、透明度/缩放应用、悬停隐藏参与、布局编辑器可拖拽项（`GlobalLayoutEditorForm` 里 `ModuleCodexRadar` / `ModulePowerThermal` 的结构面与拖拽）。
- 验收：右侧 Power / CodexQuota / ClaudeQuota 磁贴、左侧 IQ 看板数据正常；屏幕上再无这两个独立小窗。

### 阶段 3 · 删除 ClaudeRadarForm 与 ConnectionCheckForm

- `ClaudeRadarForm`：按 §7 吸收的子 SPEC 执行——删文件、删宿主、`ShouldSharedWindowOwnClaudeSelection` 固化为恒真、Claude 数据层（`ClaudeRadarReader` 等）改由 headless `CodexRadarForm` 持有（现已如此，核对即可）。
- `ConnectionCheckForm`：删文件、删 `WidgetForm.connectionCheckForm` 宿主与调度、删渲染自测入口（`--render-connectioncheck`）。**保留 `CleanIpConnectionReader.Shared`**（Network 面板已用）。
- 验收：全仓无 `ClaudeRadarForm` / `ConnectionCheckForm` 类型的活引用（历史注释除外）；网络面板"出口画像"三徽章仍在。

### 阶段 4 · 设置收敛（schema bump + 一次性迁移）

删除**已无可见消费者**的设置键（按窗口聚类）：

- Claude 独立窗 16 键（见吸收的子 SPEC §3.1，白名单键保留）。
- 连接检测：`ConnectionCheckEnabled?` / 位置 `ConnectionCheckWidth/Height/LeftX/BottomY` / `ConnectionCheckTransparencyPercent` / `ConnectionCheckBorderTransparencyPercent` / `ConnectionCheckTransparencyOverridePercent` / `ConnectionCheckScaleOverridePercent` / `ConnectionCheckDisplayDeviceName` / `ConnectionCheckLayoutWorkArea*`；**保留** `ConnectionCheckIntervalSeconds`（reader 节流仍用）与 `CleanIpBadgeTestMode`（若面板仍用）。
- 经典主面板：主窗口位置 `Width/Height/LeftX/BottomY`、`MainWidgetPresentationMode`（收敛后可去枚举或锁定）、`RadarPresentationMode`。
- Radar 独立窗（headless 后不再需要窗口几何）：`CodexRadar` 与 `ClaudeRadar` 的位置/尺寸/透明度/缩放/显示器/`*LayoutWorkArea*`；**保留** Radar 数据链路、模型、时钟、额度保护等全部**数据侧**键。
- 功耗温度独立窗几何：`PowerThermal` 位置/尺寸/透明度/缩放/显示器/`*LayoutWorkArea*`；**保留** `PowerThermalIntegratedEnabled`、告警阈值、`PowerThermalManualEnergySaverThresholdPercent` 等**数据/逻辑侧**键。

要求：每类删除都要清 `WidgetSettings` 的声明/defaults/Clone/Normalize/Save/ApplyValue/自测**全部自引用**，以及分辨率兼容子系统里对应的 `Module*` 分支；`Win11SettingsForm` 同步删页/组/标题/提示/绑定豁免。**schema 递增**，旧配置首次加载原子规范化保存；新增旧版本 fixture 自测断言废弃键不再输出、保留键不丢值。逐项跑 `--test-settings-bindings` 保持 PASS。

> 注意：`*LayoutWorkArea*` 只删"被废窗口所属模块"那几组（Claude/CodexRadar/PowerThermal/ConnectionCheck/主窗口经典面）；左侧板与操作面板等**仍显示**的模块，其 work-area 缓存是分辨率兼容机制的活状态，**禁止删**。

### 阶段 5 · 操作面板与布局编辑器清理

- `OperationForm.RadialDial`：删除径向菜单里指向已废窗口的开关（`ClaudeRadarEnabled`、`ConnectionCheck*`、经典呈现切换等），重排空缺节点。
- `GlobalLayoutEditorForm`：删除已废/headless 模块的可拖拽项，只留仍可见面的布局项。
- `DesktopCodexAssistant.cs`：删 `--render-clauderadar` / `--render-connectioncheck` 分派与其 RenderSample；`TestRadarDisplayLifecyclePolicy` 去掉 Claude 与不再可见窗口的构造。

---

## 4. 关键约束与陷阱清单

1. **数据 owner 红线**（§0）：CodexRadarForm、PowerThermalForm 只隐藏不删；删任一都会让对应磁贴/看板掉数据。
2. **reader 保留**：`CleanIpConnectionReader.Shared`、`NetworkMonitorReader`、`ClaudeRadarReader`、`ClaudeRadarSnapshotScheduler`、各 PerfSnapshot 采样与 `PathPingProbe` 全部保留。
3. **NetworkMonitorForm 一类两态**：只删浮动路径，不删类。
4. **work-area 缓存分模块**：只删废窗口所属模块的 `*LayoutWorkArea*`（阶段 4 注）。
5. **并发在建**：`Settings/WidgetSettings.cs`、`Settings/Win11SettingsForm.cs` 有大量 Codex 未提交改动，逐阶段与在建对齐，勿从旧基线覆盖。
6. **执行顺序**：先"删可见形态/消费者"→ 再"删设置声明 + schema bump"→ 最后"删文件"，每步编译通过再进下一步。

---

## 5. 验证要求

1. 全仓编译零错误；搜 `ClaudeRadarForm` / `ConnectionCheckForm` 无活引用；搜经典面板/浮动呈现分支已移除。
2. 各阶段 `--test-settings-bindings` PASS；新增各版本 fixture 迁移自测 PASS（废弃键消失、保留键留值、`Version` 正确递增）。
3. `--test-layout` PASS（布局编辑器只剩可见面模块，无残缺项）。
4. `ClaudeRadarModels` 选择器自测改为"共享窗口恒拥有 Claude 所有权"并 PASS。
5. `EdgeDockTabForm.RunSelfTest`：左侧 5 tab 槽位不重叠、都在工作区内。
6. **数据 owner 回归（红线）**：headless 化后，右侧 Power / CodexQuota / ClaudeQuota 磁贴与左侧 Codex IQ 看板数据非空且与退役前一致。用 `--render-tilecolumn` / `--render-operation`（IQ 看板样张）人工核对。
7. 按根 `AGENTS.md`：逐阶段构建 ARM64 → 备份现有正式 exe → 覆盖 → 从 E: 入口重启。

---

## 6. 建议落地节奏

一次做完风险过高，建议按阶段各发一个小版本：**阶段1 → 2 → 3 → 4 → 5**，每版独立备份/部署/可回滚。阶段 2（headless 化）是全 SPEC 风险最高的一步，单独一版并重点验红线。

---

## 7. 与既有 SPEC 的关系

本 SPEC **吸收并取代** [Opus48-StandaloneClaudeWindowRetirement-SPEC-v1.0.6.25-20260721-091125.md](Docs/Technical/Opus48-StandaloneClaudeWindowRetirement-SPEC-v1.0.6.25-20260721-091125.md)：独立 Claude 窗退役是本 SPEC 阶段 3 的一部分。spec-board 上把旧条目转 `abandoned`（reason：并入本界面收敛 SPEC），本 SPEC 另登记一条 `pending`。

---

## 8. 文档同步（验证触发表）

- `Docs/Performance-And-Window-Runtime.md`：窗口清单只剩四类可见面 + 两个 headless 数据 owner；说明呈现模式已收敛。
- `Docs/CodexRadar-Architecture.md` / `Docs/NetworkMonitor-Architecture.md` / 功耗、Claude 相关架构文：标注独立窗退役、数据 owner headless 化。
- `Docs/Indexes/FEATURE_INDEX.jsonl` / `Docs/Interfaces/INTERFACE_INDEX.jsonl`：移除已删窗口/设置键；`CodexRadarForm`/`PowerThermalForm` 标为 headless data owner。
- `Docs/Maintenance/CHANGELOG.jsonl`：每阶段一条 `refactor` + 一条 `deployment`。
- 根 `AGENTS.md` / `README.md`：版本与窗口清单更新。
