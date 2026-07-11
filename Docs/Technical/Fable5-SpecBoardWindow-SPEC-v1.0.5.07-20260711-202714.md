# Spec Board Window SPEC

## Metadata

- Document: `Fable5-SpecBoardWindow-SPEC-v1.0.5.07-20260711-202714.md`
- Generated model: Claude Fable 5
- Timestamp local: `2026-07-11T20:27:14+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.07`
- Target implementation version: `1.0.5.08+`（执行 AI 按当时版本递增）
- Status: draft（用户选定方案 2 布局、双击触发、20s 自动收回；spec 正文待用户确认后改 approved）
- Ledger registration: 已在 `D:\E_Drive_Files\Codexproject\_spec_board\SPEC_BOARD.jsonl` 登记为 `pending`

## Goal

新增跨项目 **Spec Board 看板窗口**：读取开发环境级中央账本，按"左项目栏 + 右行动流"布局显示各项目 spec 的 pending（未执行）/ awaiting_verify（待验证）/ done（完成）状态，避免漏执行与重复执行。双击左下角操作面板圆圈呼出/收回（覆盖原 QuickGrid 12 格小面板入口），默认 20 秒无交互自动收回。AI 写入入口是已存在的用户级 skill `spec-board`（`C:\Users\GengH\.claude\skills\spec-board\SKILL.md`）；本窗口对账本**只读**。

## 0. 既有资源（执行前必读）

| 资源 | 位置 | 本 spec 的用法 |
|---|---|---|
| 中央账本（已引导） | `D:\E_Drive_Files\Codexproject\_spec_board\SPEC_BOARD.jsonl` | 唯一数据源，只读 |
| 项目注册表（已引导） | `D:\E_Drive_Files\Codexproject\_spec_board\PROJECTS.json` | 项目名/根目录/spec_glob，对账扫描用 |
| 写入契约 | `C:\Users\GengH\.claude\skills\spec-board\SKILL.md` | 状态机与行 schema 的权威定义，窗口解析必须与其一致 |
| 分层窗口基类 | `Core/LayeredWidgetFormBase.cs` | 窗口必须继承并复用 `NativeMethods.LayeredBitmapSurface`、`UiFontCache`、`DesignTokens`、`BurnInProtection` |
| 双击入口现状 | `Core/OperationForm.cs` `OnMouseDoubleClick`（RadialDial Core 命中与经典 Start 按钮两分支均调 `ToggleQuickGridWindow`） | 改调 `ToggleSpecBoardWindow` |
| QuickGrid 实现 | `Core/OperationForm.QuickGrid.cs`（`OperationQuickGridForm`、`ToggleQuickGridWindow`、`RunQuickGridSelfTest`） | 入口被覆盖，代码本次保留（见 §4.1） |
| 渲染验收 harness | `Core/RenderSampleSupport.cs` + `DesktopCodexAssistant.cs` 的 `--render-*` 分发 | 新增 `--render-specboard` |
| 设置模式 | `Settings/WidgetSettings.cs`（defaults/clone/load/save/normalize/迁移/`--test-settings-bindings`） | 新设置键全链路覆盖（根 AGENTS.md 运行时不变量） |
| 布局测量规则 | 根 `AGENTS.md`：禁止手猜像素 Y；用 `GetSingleLineHeight`/`TextRenderer.MeasureText` 实测累加 | 全部行高/段高按实测字体累加 |

## 1. 范围与非目标

**范围**：SpecBoardForm 窗口（数据读取、对账、渲染、交互、设置、渲染 harness、自测）、OperationForm 双击入口改接、设置页与布局页新增项、文档与索引同步、ARM64 部署。

**非目标**：
- 不实现账本写入 UI（写入只走 skill；窗口内不提供改状态按钮，第一版保持只读避免双写冲突）。
- 不迁移或重写 QuickGrid（只摘除双击入口）。
- 不做多显示器逐屏记忆（沿用现有单一坐标 + 工作区钳制模式）。
- 不新增网络请求（本窗口纯本地文件）。

## 2. 数据层

### 2.1 账本解析

新建 `Core/SpecBoardReader.cs`（后台读取器，遵循"后台读取、克隆快照、UI 不阻塞"不变量）：

- 逐行解析 `SPEC_BOARD.jsonl`。行 schema 见 skill 文件；必需字段 `id`/`project`/`spec_path`/`title`/`status`/`updated_utc`。
- `status` 封闭词表：`pending` / `awaiting_verify` / `done` / `abandoned`。未知值按解析失败行处理。
- **坏行容忍**：单行 JSON 解析失败或缺必需字段时跳过该行、计数 `MalformedLines`，不影响其余行；快照中携带该计数供 footer 显示。
- 文件不存在 / IO 异常：快照标记 `LedgerMissing`，窗口显示空态（§3.4），不抛 UI 异常，不自建文件。
- 解析统一走 `SharedEncoding`（UTF-8 无 BOM 读取）。
- 时间字段解析失败时该 spec 仍显示，相对时长显示 `--`。

### 2.2 项目注册表

- 解析 `PROJECTS.json`（`projects[]` 的 `name`/`display`/`root`/`spec_glob`）。
- 解析失败或缺失：窗口仍可用（账本行的 `project` 字段直接作为项目名；对账功能停用并在 footer 提示"项目注册表不可用"）。

### 2.3 对账扫描（防漏登记）

- 对每个项目：枚举 `root` + `spec_glob` 匹配的文件名（**只列目录，不读文件内容**），与账本中该项目的 `spec_path` 集合比对。
- 磁盘有、账本无 → 合成"**未登记**"（`unregistered`）伪条目，橙色显示（§3.3）。文件名中 `GoalSpec` 命中者排除（账本只收实施规格，与 skill 边界一致）。
- 账本有（且状态非 done/abandoned）、磁盘无 → 该行标记"**文件丢失**"（`FileMissing=true`），灰显。done/abandoned 行不做存在性检查（历史文件可能归档）。
- 项目 `root` 目录不存在 → 该项目跳过对账、标记不可达，不算作全部未登记。
- 扫描在后台任务执行，超时 3 秒放弃本轮（网络盘防卡），沿用单飞模式（`TryStartOrJoin` 风格）。

### 2.4 快照模型

`SpecBoardSnapshot`（不可变克隆）：`Rows`（含合成未登记行）、`Projects`（显示序 = PROJECTS.json 顺序，未注册项目追加尾部）、每项目计数（pending/awaiting_verify/unregistered/done/abandoned）、`MalformedLines`、`LedgerMissing`、`LedgerLastWriteLocal`、`ScanTimeUtc`。

### 2.5 %LOCALAPPDATA% 不变量的豁免说明

账本与注册表是**用户开发环境资产**（与源码、Docs 同级的跨项目数据），不是本程序运行态数据，故存放在 `D:\E_Drive_Files\Codexproject\_spec_board\` 而非 `%LOCALAPPDATA%`。窗口对其只读，程序自身不在该目录写任何文件；本程序的运行态数据（若将来有缓存）仍归 `%LOCALAPPDATA%\DesktopCodexAssistant`。此豁免须写入 `Docs/SpecBoard-Architecture.md` 与 INTERFACE_INDEX 对应行的 `purpose`。

## 3. 窗口 SpecBoardForm

### 3.1 架构与归属

- 新建 `Core/SpecBoardForm.cs`（+ 渲染分部文件按需要），继承 `LayeredWidgetFormBase`。
- 由 `OperationForm` 持有与创建（对齐 `quickGridForm` 的 `Ensure/Dispose` 模式），设置经 `ApplyRuntimeSettings` 注入；`WidgetForm` 的全屏隐藏/挂起恢复/显示恢复广播链路必须覆盖本窗口（对齐 QuickGrid 现状：若 QuickGrid 未入该链路，则 SpecBoard 补入）。
- 显示挂起/恢复时释放并重建渲染资源（分层窗口不变量）。
- 不进入任务栏、不抢焦点（`ShowWithoutActivation`），置顶层级与其他挂件一致。

### 3.2 几何与布局（方案 2）

默认 432×400（随 `ScaleResolutionCompatibilitySize` 缩放）。所有行高由字体实测累加，禁止手写固定 Y。

```
┌──────────────────────────────────────────────┐
│ Header: "SPEC BOARD"    ●红n ●黄n ●绿n  HH:mm │ ← 标题行，右侧总计数+账本最后写入时间
├───────────────┬──────────────────────────────┤
│ 左栏(宽37%)    │ 右侧行动流                     │
│ [全部]  n/n    │ ◆未登记 · n      (橙段,有才显) │
│ 项目A   红/黄  │  卡片…                        │
│ 项目B   红/黄  │ ◆需要执行 · n    (红段)        │
│ 项目C   ✓     │  卡片: |红边条 标题      相对时长 │
│ …             │        项目 · 版本 · 事件时间   │
│               │ ◆等待验证 · n    (黄段)        │
│ ────────────  │  卡片…                        │
│ 完成n · 放弃n  │  (放不下时: "+N 更多")         │
└───────────────┴──────────────────────────────┘
```

- 内容内边距 10px（缩放前），左右栏间 1px 竖分隔线（`DesignTokens.Colors.Border` 弱化 alpha）。
- **左栏**：行 = 项目显示名（超宽尾部省略号）+ 右侧计数。计数规则：`pending+unregistered` 红字 / `awaiting_verify` 黄字，两者都为 0 时显示绿色 `✓`。首行固定"全部"。选中行画 `Surface` 圆角高亮。底部一行合计：`完成 n · 放弃 n`；有坏行或注册表异常时此行追加橙色 `⚠n`。
- **右侧行动流**：段顺序 未登记(橙)→需要执行(红)→等待验证(黄)。空段不画段头。段内排序：登记/事件时间升序（压得越久越靠上）。卡片 = `Surface` 圆角矩形 + 左侧 2px 状态色边条（左直角右圆角）；第一行 spec 短标题（账本 `title`，超宽省略）+ 右对齐相对时长；第二行 muted 副文：`项目名 · 状态事件描述`（如 `DCA · 登记于 07-11 13:43`、`DCA · 执行完成 14:26`）。`FileMissing` 行整卡灰显加"文件丢失"后缀。
- 相对时长：`<60min → Xm`，`<48h → Xh`，否则 `Xd`；解析失败 `--`。
- **溢出**：右侧按可用高度装卡，装不下时段尾画 muted `+N 更多`（未登记段优先完整显示，其次红段、后黄段）。左栏项目行装不下时同样 `+N`。
- 项目名缩写不做硬编码映射：左栏用 `display` 全名+省略号；卡片副文项目名超过 12 字符时取首尾拼接或既有 `FitFontSize` 缩排（注意：测量宽必须等于实际绘制可用宽——历史 bug 模式）。
- OLED 变体：跟随现有变体系统（`OledVariantPainting`，Typographic/AmberHud/WarmCard/Phosphor），状态三色在无蓝变体下沿用各变体的语义色替换表；实现细节遵循 `Docs/Fable5-Frontend-Rendering-Technical.md` 的变体规则与绘制禁改清单。

### 3.3 状态色

| 状态 | 色 | 语义 |
|---|---|---|
| unregistered | `WarningDeep`(橙) | 磁盘有 spec 文件但账本没登记 |
| pending | `Danger`(红) | 已登记未执行 |
| awaiting_verify | `Warning`(黄) | 执行完待验证 |
| done | `Success`(绿) | 验证完成（只出现在计数，不占卡片） |
| abandoned | muted 灰 | 只出现在合计 |

### 3.4 空态

- `LedgerMissing`：居中两行——`账本未找到` + muted 路径全文。
- 账本为空/全部完成：右侧居中绿字 `没有待办 spec ✓`，左栏照常。

### 3.5 渲染 harness

- 新增 `--render-specboard`，接入 `RenderSampleSupport` 现有 sample/current 语义：
  - `sample`：内置确定性 fixture（≥3 项目、含 unregistered/pending/awaiting_verify/done/FileMissing/坏行计数、含超长标题触发省略号、含溢出触发 `+N`），不读真实账本。
  - `current`：读真实账本与注册表。
- PNG 输出路径与命名对齐其余 6 个窗口。

## 4. 交互

### 4.1 双击操作面板圆圈：呼出/收回（覆盖 QuickGrid 入口）

- `OperationForm.OnMouseDoubleClick` 两处 `ToggleQuickGridWindow()` 调用（RadialDial `RadialHitKind.Core` 分支与经典 `StartButtonIndex` 分支）改为 `ToggleSpecBoardWindow()`。
- `ToggleSpecBoardWindow`：不可见 → `Ensure` + 定位（§4.3）+ 显示 + 触发一次账本读取与对账 + 启动自动收回计时；可见 → 收回（Hide，不 Dispose，窗体复用）。
- **QuickGrid 处置**：`OperationQuickGridForm` 代码与 `RunQuickGridSelfTest` 本次保留（最小风险；12 格能力已被 RadialDial 设置扩展收纳），但唯一用户入口消失：FEATURE_INDEX 对应行改 `deprecated` 并在描述注明"入口被 Spec Board 覆盖（本 spec），物理删除待后续版本决定"；`ToggleQuickGridWindow` 若因此完全无调用方，与自测的引用关系由执行 AI 核实后决定保留理由或一并降级，禁止留下编译警告。

### 4.2 自动收回

- 显示后启动收回计时器，超时 `SpecBoardAutoHideSeconds`（默认 20s）自动 Hide。
- 鼠标位于窗口内：暂停计时；离开：重置并重新计时。窗口内任何点击也重置计时。
- 设置为 0 = 永不自动收回（只能双击或手动收回）。
- 计时器只在窗口可见期间运行（隐藏即停，符合空闲零负担原则）。

### 4.3 默认位置与布局设置

- 锚定规则（`SpecBoardLeftX`/`SpecBoardBottomY` 为 -1 时）：左边缘与操作面板窗口左边缘对齐；底边 = 操作面板窗口顶边 - 10px；随工作区钳制（复用现有 `workArea` 钳制模式）。操作面板移动后下次显示时重新锚定。
- 用户在设置布局页修改后写入具体坐标（≥0），此后不再自动锚定；布局页提供"恢复自动"入口（置回 -1，语义写进设置页说明文字）。
- 全局布局编辑器（`GlobalLayoutEditorForm`）若枚举窗口列表，需把本窗口加入可拖拽集合。

### 4.4 左栏过滤与行为

- 单击左栏项目行：右侧行动流过滤为该项目；再次单击该行或单击"全部"取消。选中态不持久化（每次呼出重置为"全部"）。
- 单击右侧卡片：`Process.Start` 用系统默认程序打开 `root` + `spec_path` 绝对路径（`UseShellExecute = true`）；`FileMissing`/unregistered-路径异常时打开所在目录。打开文件后重置自动收回计时。
- 不提供任何状态修改交互（只读，见 §1）。

### 4.5 刷新（同步 `Docs/Component-Refresh-Rules.md`）

| 触发 | 行为 |
|---|---|
| 窗口显示 | 立即读账本 + 对账扫描（后台单飞） |
| 可见期间账本文件变更 | FileSystemWatcher（500ms 防抖合并）触发重读；watcher 随窗口首次显示创建、Dispose 释放；watcher 失效时回退 60s 轮询兜底 |
| 可见期间 | 每 5min 重跑一次对账扫描 |
| 隐藏期间 | 无任何定时器、无 watcher 事件处理（挂起） |

无网络请求；不参与 AI 请求保护。

## 5. 设置（全链路：defaults / clone / load / save / normalize / 设置 UI / 迁移版本 / `--test-settings-bindings`）

| 键 | 类型 | 默认 | 归一化 | 设置页位置 |
|---|---|---|---|---|
| `SpecBoardWidth` | int | 432 | 钳 [320, 700] | 布局页 |
| `SpecBoardHeight` | int | 400 | 钳 [240, 800] | 布局页 |
| `SpecBoardLeftX` | int | -1（自动锚定） | -1 或 ≥0；越界回 -1 | 布局页 |
| `SpecBoardBottomY` | int | -1（自动锚定） | 同上 | 布局页 |
| `SpecBoardAutoHideSeconds` | int | 20 | 钳 [0, 600]，0=不收回 | 操作面板设置区 |
| `SpecBoardLedgerPath` | string | `D:\E_Drive_Files\Codexproject\_spec_board\SPEC_BOARD.jsonl` | 去引号/trim；空回默认 | 操作面板设置区（文本框+浏览） |

- 两个分辨率预设（参照既有 853/1070 行附近的预设块）都要给出上述默认。
- 迁移：新增键走现有 settings 迁移版本机制，旧 ini 无键时取默认。
- `PROJECTS.json` 路径不单独设键：固定为账本同目录下的 `PROJECTS.json`（随 `SpecBoardLedgerPath` 派生），避免两处配置漂移。

## 6. 文档与索引任务（遵循 `Docs/AGENTS.md`）

1. 新建活文档 `Docs/SpecBoard-Architecture.md`（适用版本 + 数据流 + 状态机 + 刷新规则引用 + LOCALAPPDATA 豁免说明），并把它登记进 `Docs/AGENTS.md` §2 文档地图表。
2. `FEATURE_INDEX.jsonl`：新增 `spec_board.window` 行（aliases 含：spec看板/spec board/待执行/待验证/漏执行/账本）；QuickGrid 对应行改 `deprecated`（见 §4.1）。
3. `INTERFACE_INDEX.jsonl` 新增：`file_format.spec_board.ledger`（SPEC_BOARD.jsonl，direction=consume）、`file_format.spec_board.projects`（PROJECTS.json，consume）、`command.spec_board.render`（`--render-specboard`）；`command.operation.quick_grid` 类既有行（若存在）状态同步。
4. `Docs/Component-Refresh-Rules.md`：新增 Spec Board 行（§4.5 表）。
5. `CHANGELOG.jsonl`：实施完成后按 §6 规约追加变更与部署记录；本 spec 的 INDEX 行 status 回填 `implemented`。
6. 执行完成后用 `spec-board` skill 把账本行 `DesktopCodexAssistant.spec_board_window` 推进到 `awaiting_verify`；验证 AI 通过后推进 `done`。

## 7. 验收条件（全绿才算完成）

### G1 设置绑定
`DesktopCodexAssistant.exe --test-settings-bindings` 通过，且证据输出覆盖全部 6 个新键（默认值、clone、save/load round-trip、归一化钳制各at least一例：如 `SpecBoardAutoHideSeconds=-5 → 0`、`=9999 → 600`）。

### G2 布局自测
`--test-layout`（或新增 `RunSpecBoardSelfTest` 挂入 `--test`）断言：左栏矩形与右侧矩形不相交；任一卡片矩形不越窗口内容区；溢出 fixture 下 `+N 更多` 出现且最后一张完整卡片底边 ≤ 内容区底边；header/左栏/右侧段头全部行高来自字体实测（断言实现方式：布局器输出的矩形做程序化不相交/包含检查）。

### G3 渲染验收
`--render-specboard sample` 生成确定性 PNG：肉眼核对 fixture 的橙/红/黄段齐全、灰显丢失行、坏行 `⚠` 计数、超长标题省略号、`+N 更多`；4 个 OLED 变体各出一张无蓝色残留（AmberHud/Phosphor 下三态仍可区分）。`--render-specboard current` 读真实账本出图并显示本 spec 的 pending 卡片。

### G4 双击触发/收回
手动验证并录入 changelog 证据：RadialDial 模式双击圆心 → 窗口出现在操作面板上方（左对齐、底边在面板顶边上方 10px）；再次双击 → 收回；经典模式 Start 按钮双击同理；**双击后 QuickGrid 12 格面板不再出现**；操作面板其余单击/右键行为不变。

### G5 自动收回
设置 5s → 显示后不动鼠标 ~5s 收回；鼠标悬停在窗口内超过 5s 不收回，移出后重新计时收回；设 0 → 60s 内不收回。计时窗口隐藏后 `--test` 进程无遗留活动定时器（复用现有空闲计时器审计方式）。

### G6 数据健壮性
账本追加一行坏 JSON → 窗口其余行正常、footer 出现 `⚠1`；临时改 `SpecBoardLedgerPath` 指向不存在文件 → 空态显示路径、无异常日志刷屏；恢复后 watcher 2 秒内回满。

### G7 对账
在本项目 `Docs/Technical/` 临时创建 `Codex-GateTest-SPEC-v0-00000000-000000.md`（不登记）→ 5min 内（或重新呼出立即）橙色未登记卡出现；删除该文件后消失。把账本某 pending 行的 `spec_path` 改为不存在路径 → 该卡灰显"文件丢失"。测试后还原账本。

### G8 skill 往返
用 `spec-board` skill 登记一条测试 spec → 窗口可见状态下 2 秒内出现（watcher 生效）；推进 `awaiting_verify` → 卡片从红段移到黄段；推进 `done` → 卡片消失、左栏计数与底部合计更新；测试行最后改 `abandoned` 收尾。

### G9 文档 Gate
`Docs/AGENTS.md` §8 两段 python 校验全 PASS（JSONL 可解析、id 唯一、路径存在）；§6 触发表核对：FEATURE/INTERFACE/Component-Refresh-Rules/SpecBoard-Architecture/文档地图全部就位；本 spec INDEX 行回填 `implemented` 并记 `spec_sha256`。

### G10 部署
ARM64 Release 构建零警告；备份现有正式 exe → 覆盖 → 重启；重启后版本号一致（`ProductIdentity.Version` == 根 AGENTS.md Current version == changelog version），双击呼出窗口显示真实账本数据。

## 8. 风险与回滚

- **双击入口覆盖**：QuickGrid 代码保留，回滚 = 还原 `OnMouseDoubleClick` 两处调用。
- **账本并发写**：skill 侧写入是整行级、窗口只读+防抖重读，坏行容忍兜底半写状态；无锁设计可接受（写入频率极低）。
- **对账误报**：`spec_glob` 只匹配 `*-SPEC-*.md` 且排除 `GoalSpec`；若其他项目命名不符，修 PROJECTS.json 的 `spec_glob`，不改代码。
- **路径硬编码**：账本默认路径进设置键可改；换机器改设置即可。
