# 左侧停靠板 macOS 移植实施规格

- 文档类型：Implementation SPEC（执行规格快照）
- 基线版本：`2.0.0.31`
- 创建时间：`2026-08-11T21:56:24+09:00`
- 生成模型：Opus 5
- 当前状态：`draft`，尚未授权执行
- 适用平台：**产出物为 macOS（Apple Silicon, arm64）**；本规格执行期间不得改变现有 Windows on Arm 分支的任何运行时行为
- 移植对象：七个左侧停靠板（Network / Spec Board / Codex Task / GUARD / Codex IQ / Reset·Speed / System Day）及其壳层
- 目标交付：与 `DesktopCodexAssistant` **并列的新同级项目**，不是本分支的新平台目标

> 本文件是尚未执行的不可变规格。开始实现后不得直接修改正文；若范围、风险边界或验收条件需要变化，必须创建带新时间戳的替代 SPEC，并在 Technical INDEX 与 Spec Board 中标明取代关系。

---

## 0. 执行红线

1. **不修改本分支任何 `.cs`、`Build-*.ps1`、`Build-Sources.json` 或正式 EXE。** 根 `AGENTS.md` 明确本分支是 ASUS UX3407N/UX3607O 专用 Windows on Arm 版，且既有规格已把「迁移到新 UI 框架 / 新 .NET 运行时」列为范围外。本规格通过**复制**源码到新项目实现移植，本仓库只增加文档。
2. 新项目根目录为 `D:\E_Drive_Files\Codexproject\DesktopCodexBoards-macOS\`（与 `desktopdata` 同级）。不得在本仓库内创建 `mac/`、`Avalonia/`、`Shared/` 等子目录。
3. 不得为本规格构建、备份、覆盖或重启 Windows 正式 ARM64 可执行文件。根 `AGENTS.md` 的「源码变更后默认部署」规则在本规格中**不触发**，因为本规格不产生本分支源码变更。
4. 不得引入右侧磁贴、`CodexRadarForm`、`PowerThermalForm`、`WidgetForm`、`OperationForm` 主体、Settings 窗口或任何 WMI / PDH / `wlanapi` / `imm32` 代码路径。
5. 不得把华硕专有行为（`-HWSettingsToast acin_set` 电池养护、UX3407N/UX3607O 功耗温度校准）以任何形式移植或用通用实现「近似」。macOS 上这些能力不存在，必须显式缺省而不是伪造数值。
6. 测试不得读取真实 `auth.json`、Claude setup-token、Cookie 或任何真实访问令牌；不得为验收调用 ChatGPT、Anthropic、Codex Radar full API、DeepSeek 等认证或可能计费接口。
7. 每一批必须在**上一批验收通过后**才能开始。B 批（macOS 壳原型）验收未通过时不得进入 C 批（绘制层）——这是本规格唯一的止损点，见 §7。
8. 不得吞异常、无限重试或保留双实现来规避验收。所有降级分支必须有明确终止条件和可见的降级标识。

---

## 1. 目标

把七个左侧停靠板做成 macOS 上的常驻贴边浮窗，使以下不变量成立：

1. 板的**业务逻辑与几何算式**在 Windows 与 macOS 之间是同一份源码，不存在按平台分叉的两套板实现。
2. 平台相关能力（窗口壳、绘制后端、传感器读取）全部收敛在显式接口后面，新增一个平台只需实现接口，不需要改板。
3. macOS 侧的浮窗行为符合平台习惯：不抢焦点、不占 Dock 图标、跨 Space 常驻、可选点击穿透、随分辨率与显示器变更重新贴边。
4. 无法在 macOS 上实现的能力（壁纸层嵌入、华硕电池养护、部分传感器）**显式缺省并在 UI 上可见**，不用近似值冒充。
5. 现有 Windows 分支的行为、构建产物和验收流程完全不受影响。

---

## 2. 范围外

1. 不移植右侧十一个 `MetricTileForm` 磁贴、`OperationForm` 径向盘、Settings 窗口。
2. 不移植 `CodexRadarForm` / `PowerThermalForm` 的采样链；macOS 侧的板只消费快照，快照来源见 §5 D 批。
3. 不做壁纸层（WorkerW 等价物）嵌入。macOS 无对应能力，见 §3.4。
4. 不做 App Store 上架。沙盒会阻断 System Day 需要的传感器读取，分发路径见 §5 E 批。
5. 不重新设计视觉：配色、字号、间距、板内信息层级一律沿用 `DesignTokens` 与现有 Layout 的既定值。允许且仅允许为「平台能力缺失」增加克制的状态标识。
6. 不引入 Electron、Tauri、React Native 或任何 Web 技术栈。
7. 不改变 `SPEC_BOARD.jsonl`、`CHANGELOG.jsonl` 等既有数据文件的 schema。macOS 侧只读同样的格式。

---

## 3. 现状事实基线

以下数字为 `2.0.0.31` 工作树实测，是本规格全部判断的依据。执行时若偏差超过 10%，必须先重新测量再决定是否仍适用。

### 3.1 移植面规模

| 项 | 实测值 |
|---|---|
| 移植面文件数 | 36 |
| 移植面代码行 | 22,923 |
| WMI / PDH / 性能计数器调用 | **0** |
| `NativeMethods.*` 调用点 | 64 |
| GDI+ 绘制调用点 | 616 |

移植面定义为：`Core/*Board*.cs`、`Core/NetworkMonitorForm*.cs`、`Core/EdgeDockTabForm.cs`、`Core/LayeredWidgetFormBase.cs`、`Core/LeftDockLayout.cs`、`Core/OperationForm.CodexTasks.cs`、`Core/CodexTaskMonitorReader.cs`、`Core/GuardRuntime.cs`。

### 3.2 原生依赖的实际形态

64 个调用点去重后只有 6 类 API，且**全部属于窗口壳层**，没有一处是数据读取：

| Win32 | 用途 | AppKit 对应 |
|---|---|---|
| `SetWindowPos` + SWP 标志 | 定位 / Z 序 / 不激活 | `NSWindow.setFrame(_:display:)` + `level` |
| `WS_EX_LAYERED` + `UpdateLayeredWindow` | 逐像素 alpha | `isOpaque = false` + `backgroundColor = .clear` |
| `WS_EX_TRANSPARENT` | 整窗点击穿透 | `ignoresMouseEvents` |
| `WS_EX_NOACTIVATE` / `WS_EX_TOOLWINDOW` | 不抢焦点 / 不进任务栏 | `NSPanel(.nonactivatingPanel)` + `LSUIElement` |
| `GetWindowLong` / `SetWindowLong` | 改上述样式位 | 直接属性赋值 |
| `TryGetOnAcPower` | 交流供电判定 | `IOPMPowerSource`（唯一非窗口调用，仅 GUARD 用） |

调用点分布：`LayeredWidgetFormBase.cs` 15、`NetworkMonitorForm.cs` 13、`EdgeDockTabForm.cs` 5、`NetworkMonitorForm.Dock.cs` 5，其余分散在各板 1–3 处。`LeftDockLayout.cs`（724 行，全部贴边几何）**零原生调用**。

### 3.3 数据来源分级

| 板 | 数据来源 | 移植成本 |
|---|---|---|
| Spec Board | 磁盘 `SPEC_BOARD.jsonl` | 仅改路径策略 |
| Codex Task | `CodexTaskMonitorReader`（文件 + HTTP） | 零 |
| Reset·Speed | Codex 配额快照 | 零 |
| Codex IQ | 上游 JSON（**已于 2026-07-12 停更**） | 零，但数据源本身已死 |
| Network | `System.Net` 探测 + Clean IP | 零 |
| GUARD | `SetThreadExecutionState` + 华硕电池养护 | 前者可映射，后者**必须删除** |
| System Day | 电量 / 瓦数 / 温度 / 电源模式 | 需 IOKit 重写，见 §5 D |

### 3.4 已确认的不可移植项

| 能力 | 原因 | 处置 |
|---|---|---|
| 壁纸层嵌入（`SetParent` 到 WorkerW） | macOS 无等价窗口层；`.desktopIconWindow` 之下的窗口会被 Sonoma+ 的「点击壁纸显示桌面」手势一并隐藏 | 不实现。左侧板是贴边浮窗，不依赖此能力 |
| 华硕电池养护暂停 | macOS 优化充电不开放第三方控制 | GUARD 板删除该控件与倒计时环 |
| Thermal zone 温度 | macOS 无公开 API，SMC 读取为非公开且随机型变化 | 见 §5 D 的降级要求 |
| IME 转换状态（`imm32`） | 已不在移植面内 | 无需处理 |

### 3.5 运行时前置条件

本分支用 `csc.exe` 直接编译 **.NET Framework 4.x**（引用 `System.Web.Extensions.dll`、`WindowsBase.dll`、`Windows.winmd`）。Framework 在 macOS 上不能运行且无兼容层。**迁移到 .NET 8 是所有后续工作的前置条件**，且必须在 A 批内完成。

---

## 4. 目标架构

三层，自下而上：

```
┌──────────────────────────────────────────────┐
│  BoardShell (Swift / AppKit)                 │  ← 平台层，仅 macOS
│  BoardPanel : NSPanel + DockTabController    │
└──────────────────────────────────────────────┘
                     ↓ C ABI (NativeAOT export)
┌──────────────────────────────────────────────┐
│  BoardCore (C# / .NET 8, 无 UI 框架依赖)      │  ← 共享层，两平台同一份源码
│  七个板的布局、状态机、Reader、Snapshot        │
│  IBoardRenderTarget / IPlatformSensors        │
└──────────────────────────────────────────────┘
                     ↓ 接口实现
┌──────────────────────────────────────────────┐
│  MacSensors (Swift) / WinSensors (C#)        │  ← 平台传感器
└──────────────────────────────────────────────┘
```

**为什么壳层用 Swift 而不是 Avalonia**：`nonactivatingPanel` 属于 `NSWindow` 的 `styleMask`，只能在初始化时传入；Avalonia 已代为创建窗口，事后无法更改。`collectionBehavior`、`ignoresMouseEvents`、精确 window level 同样未被 Avalonia 暴露，需自建 objc runtime 绑定（且 Apple Silicon 上 `objc_msgSend` 不允许通用签名强转，每种参数签名要单独声明）。壳层实际需要的 AppKit 代码约数百行，自建绑定层的维护成本高于直接写 Swift。

**接口契约**（A 批必须定稿，之后不得单方面变更）：

- `IBoardRenderTarget` —— 绘制原语抽象。必须覆盖现有 616 个绘制调用点用到的全部操作，且**不得泄漏 `System.Drawing` 类型**。
- `IPlatformSensors` —— 电量、供电状态、瓦数、温度、电源模式、休眠抑制。每个成员必须区分「不支持」与「暂时读不到」两种状态。
- `IBoardWindowShell` —— 定位、层级、点击穿透、显示器变更通知。

---

## 5. 分批实现

### A 批 —— 逻辑核提取（**全程在 Windows 上完成，零 macOS 依赖**）

| ID | 要求 |
|---|---|
| A-1 | 在新项目建立 `BoardCore` 类库，目标框架 `net8.0`，`EnableWindowsTargeting=false`，禁止引用 `System.Windows.Forms` 与 `System.Drawing.Common` |
| A-2 | 从本分支**复制**移植面 36 个文件；不得引用、软链或 submodule 回本仓库 |
| A-3 | 定义 §4 三个接口，把 616 个绘制调用改为经 `IBoardRenderTarget` 调用 |
| A-4 | 把 64 个 `NativeMethods.*` 调用改为经 `IBoardWindowShell` 调用；`TryGetOnAcPower` 归入 `IPlatformSensors` |
| A-5 | 几何层统一坐标原点：`LeftDockLayout` 及各 `*.Layout.cs` 的 Y 轴改为经显式转换函数取值，**不得在板内直接写 Y 算式**（macOS 原点在左下、Y 向上，与 Windows 相反） |
| A-6 | 存储路径经 `IPlatformPaths` 取得，移除 `%LOCALAPPDATA%` 硬编码 |
| A-7 | 提供 `WinRenderTarget` / `WinSensors` / `WinWindowShell` 的 GDI+ 实现，使 `BoardCore` 在 Windows 上可运行 |

**A 批验收**：`dotnet test` 全绿；在 Windows 上用 `WinRenderTarget` 渲染七个板的 PNG，与本分支 `--render-*` 基线**逐像素比对差异为 0**。这一条是整个规格的地基——它证明抽象没有改变行为。

> A 批完成后即使不继续，本身也是净收益：板与平台解耦，Windows 侧亦更易维护。若 A 批发现耦合远超基线预估（如 A-3 需改动超过 900 处，或 A-7 无法做到像素一致），**在此停止并重新评估**，此时沉没成本最低。

### B 批 —— macOS 壳原型

| ID | 要求 |
|---|---|
| B-1 | `BoardPanel : NSPanel`，`styleMask = [.borderless, .nonactivatingPanel]`，`isOpaque = false`，`backgroundColor = .clear`，`hidesOnDeactivate = false`，`canBecomeKey/canBecomeMain → false` |
| B-2 | `collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]`；`level = .statusBar` |
| B-3 | Info.plist `LSUIElement = true`（无 Dock 图标、无菜单栏） |
| B-4 | 贴边定位基于 `NSScreen.visibleFrame`（已扣除菜单栏与 Dock），监听 `NSApplication.didChangeScreenParametersNotification` 重新贴边 |
| B-5 | 局部点击穿透经覆写 `NSView.hitTest(_:)` 按区域返回 `nil` 实现；整窗穿透用 `ignoresMouseEvents` |
| B-6 | Dock tab 控制器，复刻 `EdgeDockTabForm` 的展开/收起与互斥语义 |
| B-7 | 全屏行为**重新决策并在代码注释中记录理由**：Windows 侧检测到全屏即隐藏；macOS 全屏是独立 Space，默认行为改为跟随（`.fullScreenAuxiliary`），需处理与自动隐藏菜单栏的位置冲突 |

**B 批验收**：一个画占位内容的面板，在真机上满足——失焦不消失、不进 Dock、切换 Space 跟随、进入他人全屏后仍可见、Mission Control 不搬动、外接显示器插拔后重新贴边、Cmd+\` 跳过。**任一条不满足即为 B 批不通过，按 §7 处置。**

### C 批 —— 绘制层移植

| ID | 要求 |
|---|---|
| C-1 | 实现 `MacRenderTarget`。后端在 CoreGraphics 与 SkiaSharp 之间二选一，选型依据必须实测：以 Codex IQ 板（114 个绘制点，移植面内最密）为样本比较文本度量保真度与帧耗时 |
| C-2 | 字体：`UiFontCache` 的 Windows 字体族在 macOS 上映射到等价族，映射表显式登记，不得依赖系统回退 |
| C-3 | 混合脚本（CJK + 拉丁）单次绘制的基线漂移问题，按现有 `RadarBottomInfoTextRenderer` 的按脚本分段方案处理，不得回退到单次绘制 |
| C-4 | 逐板迁移，顺序：Spec Board → Codex Task → Reset·Speed → Network → Codex IQ → GUARD → System Day（由数据最简到最复杂） |

**C 批验收**：每板在 macOS 渲染 PNG 与 A 批 Windows 基线并排人工评审；文本不得截断、不得溢出板边、不得基线漂移。**不要求逐像素一致**（字体栅格化本就不同），要求信息完整可读且层级不变。

### D 批 —— 平台传感器

| ID | 要求 |
|---|---|
| D-1 | 电量百分比、充电中、外接供电 → `IOPMPowerSource` |
| D-2 | 休眠抑制（GUARD）→ `IOPMAssertionCreateWithName`；不得 spawn `caffeinate` 子进程 |
| D-3 | 电源模式 → 低电量模式状态；语义与 Windows 电源计划不同，标签文案须相应调整 |
| D-4 | 瓦数 → 由 `IOPMPowerSource` 的电流电压导出；无法取得时按 D-6 处理 |
| D-5 | 温度 → **默认不实现**。SMC 读取属非公开 API 且随机型变化，按 D-6 声明为不支持 |
| D-6 | 每个不支持的指标，`SystemDayBoardSnapshot` 的对应 `*Known` 标志置 false，板上显示明确的「本平台不提供」标识，**不得显示 0、`--` 之外的任何可能被误读为真实测量值的内容** |
| D-7 | GUARD 板移除电池养护控件与其倒计时环，布局相应收拢，不留空占位 |

**D 批验收**：拔插电源、进入/退出低电量模式、触发休眠抑制后 `pmset -g assertions` 可见对应 assertion；断言释放后自动消失。所有 §3.4 与 D-5/D-6 的缺省项在板上可见且措辞一致。

### E 批 —— 打包与分发

| ID | 要求 |
|---|---|
| E-1 | `BoardCore` 以 NativeAOT 编为 `.dylib`，导出 C ABI；Swift 侧经桥接头调用 |
| E-2 | 打包为 `.app`，arm64-only |
| E-3 | Developer ID 签名 + notarization。**沙盒不启用**（会阻断 D 批传感器读取），故不可上架 App Store，此为已知且接受的取舍 |
| E-4 | 首次启动的权限说明文案；本规格范围内的板不需要辅助功能权限，若实现中发现需要，视为范围变更，须另发 SPEC |

**E 批验收**：在一台未安装任何开发工具的 Apple Silicon Mac 上，双击 `.app` 可直接运行，无 Gatekeeper 警告。

---

## 6. 全局验收条件

1. Windows 分支 `git status` 在整个执行期内**不出现任何 `.cs`、`.ps1`、`.json` 或 EXE 变更**；本仓库仅新增本文件、INDEX 行与 CHANGELOG 行。
2. `BoardCore` 中 `System.Windows.Forms`、`System.Drawing`、`DllImport("user32.dll")` 的文本命中数为 0。
3. 七个板在 macOS 上同时常驻 8 小时，无崩溃、无内存单调增长、无 Space 切换后错位。
4. 空闲态 CPU 占用不高于本分支 Windows 侧同等板的实测值。
5. §3.4 与 §5 D 批列出的每一项不可移植能力，在最终产物中均为显式缺省状态，且有对应的 UI 标识。
6. 新项目自带 README，记录三层架构、接口契约、平台能力缺口表，以及与本分支的源码同步策略（**当前策略：单向复制，不自动同步；本分支后续变更需人工评估是否回灌**）。

---

## 7. 止损点与风险

**唯一强制止损点在 B 批。** B 批验收的七条真机行为若有任一不满足，且在 3 个工作日内无法解决，执行必须暂停并向用户报告，不得进入 C 批。理由：A 批的产出（板与平台解耦）在 Windows 侧独立成立，此时终止无净损失；一旦进入 C 批的 616 个绘制点迁移，成本不可回收。

| 风险 | 判定时点 | 缓解 |
|---|---|---|
| `IBoardRenderTarget` 抽象不足以覆盖全部绘制点 | A 批 | A-3 完成前先枚举全部 616 个调用点的原语种类，接口定稿后再动手改 |
| A 批像素比对无法归零 | A 批验收 | 差异若源于抽象层浮点取整，允许放宽为「差异像素数 < 总数 0.01% 且无结构性偏移」，需在报告中逐板列出 |
| macOS 全屏 / Stage Manager 行为不符预期 | B 批 | 强制止损点，见上 |
| Codex IQ 上游已停更 | 任意 | 该板在 macOS 侧同样只能显示历史值；不得为此新造数据源 |
| 字体度量差异导致板内文本溢出 | C 批 | 沿用 `FitFontSize` 收缩策略，且测量宽度必须等于绘制时的实际可用宽度 |
| NativeAOT 导出与 Swift 桥接的字符串/生命周期错配 | E 批 | E-1 先用最小样例打通再接入全量 |

---

## 8. 交付物清单

| 交付物 | 位置 |
|---|---|
| 本 SPEC | `Docs/Technical/Opus5-MacLeftBoardPort-SPEC-v2.0.0.31-20260811-215624.md` |
| Technical INDEX 登记行 | `Docs/Technical/INDEX.jsonl` |
| Spec Board 登记行 | `_spec_board/SPEC_BOARD.jsonl`（project: `DesktopCodexAssistant`，因本 SPEC 文件物理位于本仓库；产出物所在的新项目在其自身建库后另行登记） |
| 新项目源码 | `D:\E_Drive_Files\Codexproject\DesktopCodexBoards-macOS\` |
| 各批执行报告 | 新项目 `Docs/Reports/` |
| 本仓库 CHANGELOG 行 | `Docs/Maintenance/CHANGELOG.jsonl`，`change_type: docs`，仅记录本 SPEC 创建 |

---

## 9. 未决事项（需用户在授权执行时确认）

1. **新项目路径**：`D:\E_Drive_Files\Codexproject\DesktopCodexBoards-macOS\` 是否合适，或另有偏好。
2. **是否需要 Windows 侧回灌**：A 批产出的解耦后 `BoardCore` 是否要反向替换本分支的板实现。本规格默认**不回灌**（回灌会改动本分支，与红线 1 冲突，需另发 SPEC）。
3. **Codex IQ 板是否纳入**：其上游数据源已停更，可选择在 macOS 侧直接不实现该板。本规格默认纳入。
4. **开发机**：Apple Silicon Mac 是否已具备。B 批起的全部验收都需要真机，无真机则只能执行到 A 批。
