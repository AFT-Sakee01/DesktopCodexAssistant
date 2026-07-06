# Claude — Codex Radar 渲染变体与额度雷达线改造说明

> 本文档由 **Claude（claude-opus-4-8 / claude-sonnet-5）** 撰写并对应其实现的改动。按项目规则，非 Codex 模型生成的新文档以模型短名 `Claude-` 前缀命名。本文汇总 2026-07-03 一轮由 Claude 完成的 Codex Radar 前端与全窗口渲染变体相关改动，覆盖版本 `1.0.3.34` 至 `1.0.3.41`。所有改动均只影响本地 GDI+ 绘制与设置读写，未改变网络、缓存、线程或持久化数据语义。

## 1. 背景

- 本轮起点：一处未提交的工作区改动把 `CodexRadarForm.DrawCodexRadarModules` 静默改成绘制另一套简化 4 格圆环布局，导致既有 `DrawCodexRadarWidget`/`DrawQuotaWidget` 渲染树成为死代码、且与文档/索引描述脱节。
- 用户要求：先恢复旧版复杂布局；随后提出「在设置里加载不同窗口模板，便于测试与回滚」的诉求，并逐步细化为 Codex Radar 的多套均匀分布布局、额度雷达线加粗、以及把切换能力推广到所有窗口。

## 2. 渲染变体切换机制（`1.0.3.35` 起，Claude 实现）

核心思想：同一个已编译 exe 内并存多套窗口视觉实现，通过设置项实时切换，随时回退，不需重编译或重启。

- `WidgetSettings.CodexRadarRenderVariant` 枚举：`Classic` / `EvenGrid` / `EvenRow`。
- `CodexRadarForm` 拆为 `partial class`：`Core/CodexRadarForm.cs` 保留数据层与经典绘制 `DrawCodexRadarModulesClassic`；每个变体独立文件 `Core/CodexRadarForm.<Name>.cs` 只放该变体的绘制方法。
- `DrawCodexRadarModules` 顶部按枚举 `switch` 分发。删除某变体 = 删文件 + 去 case。
- 设置页「Codex Radar → 渲染变体」下拉，经现有 75 ms 预览节流实时生效。

约束：该机制只切换绘制路径，不隔离数据模型或线程模型。若未来某变体需要不同取数或并发方式，应走独立分支流程，而非复用此开关。

## 3. Codex Radar 两套均匀分布变体（`1.0.3.36`，Claude 实现）

用户要求窗口内元素均匀分配、不能一边拥挤一边空，且颜色方案不得改变。

- `EvenGrid`（`Core/CodexRadarForm.EvenGrid.cs`）：上方一行六等分单元格（时间效率、Token 效率、5 小时额度、周额度、IQ、额度雷达）+ 下方细页脚状态条三等分（社区体感评分、五阶段连接点+摘要、Model IQ 更新时间）。
- `EvenRow`（`Core/CodexRadarForm.EvenRow.cs`）：单行，加权列宽——5 个环各权重 1.0、额度雷达列 0.5（压缩其左右空白）、状态列 1.6（放下 `RC:xxx` / 连接点+已通过 / `已更新/HH:mm`，不截断）。
- 共享绘制层放在 `Core/CodexRadarForm.cs`：`GatherQuotaDisplayState`（额度金色保护/消耗环基线/随机测试）、`GetCodexConnectionStatusSummary`、`DrawEvenLayoutQuotaCell/IqCell/EfficiencyCell/RadarCell/ConnectionSummary`、`GetEvenLayoutCellRects`。这些 `DrawEvenLayout*` 方法**刻意不调用** `OffsetCodexRadarElementRect`，因此均布变体不受手动布局与元素偏移设置影响，网格完全由窗口尺寸自动等分。
- 圆环视觉（底环、消耗环、余额环、IQ 基线/超额/不足弧、效率环基础/低效/高效弧）逐像素复用既有颜色常量与绘制顺序；金色保护、速蹬轮播、`已重置`、离线灰点、请求中闪烁等状态语义与经典布局一致。

## 4. 额度雷达线改造（`1.0.3.37`、`1.0.3.39`、`1.0.3.41`，Claude 实现）

- 全高竖条：`DrawEvenLayoutRadarCell` 在整个单元格高度上绘制额度雷达竖条（去掉早期误加的环方形约束与「额度雷达」文字标签），与经典布局同尺度，恢复趋势段/蓝点/箭头/平均横杠可读性。
- 加粗 50%：`DrawCodexQuotaRadarVerticalLine` 新增 `strokeScale` 参数（默认 1.0，经典布局不变）。因线、彩色趋势段、平均横杠、蓝点、趋势箭头均从 `stroke` 派生，均布变体传 `1.5` 即整体放大内部元素；`DrawEvenLayoutRadarCell` 同步加宽雷达线列。
- 趋势箭头（chevron）改造（`1.0.3.41`）：`DrawCodexQuotaRadarChevronLine` 把顶角从约 44° 放大到 **120°**（半顶角 60°，故 `height = width / (2·tan60°) = width / 3.4641`），使每个箭头更扁平、更宽；`DrawCodexQuotaRadarTrendArrows` 把箭头线宽系数由 `stroke·0.32` 降到 `stroke·0.22`，使箭头更细。

## 5. 隐藏手动布局设置（`1.0.3.39`，Claude 实现）

- 设置页 Codex Radar 移除「手动布局」与「元素偏移：效率/连接/额度IQ」四个分组；对应 `WidgetSettings` 属性保留（Classic 若经 `settings.ini` 设置仍生效，均布变体本就忽略）。
- `Win11SettingsForm.VerifySelfTest` 必需绑定清单同步移除 `CodexRadarManualLayoutEnabled` 与 `CodexRadarConnectionLineOffsetX`，避免隐藏后自测因找不到控件失败。

## 6. 渲染变体推广到全部窗口（`1.0.3.40`，Claude 实现）

用户确认「全都要」且「现在就显示下拉（只有经典）」。

- 5 个独立枚举：`MainWidgetRenderVariant` / `NetworkMonitorRenderVariant` / `PowerThermalRenderVariant` / `ConnectionCheckRenderVariant` / `OperationRenderVariant`，每个仅含 `Classic`。每窗口独立枚举，确保某窗口新增变体不会出现在其它窗口下拉。
- 5 个窗体改 `partial class`，各自内容绘制方法（`WidgetForm.DrawWidgetContent`、`NetworkMonitorForm`/`PowerThermalForm`/`ConnectionCheckForm` 的 `DrawContent`、`OperationForm.DrawOperationWindow`）改为顶部按枚举 `switch` 分发，经典实现移到同名 `*Classic` 方法。
- 设置页各窗口新增「渲染变体」下拉；`WidgetSettings` 全流程补齐（默认/Clone/Save/ApplyValue/`CurrentSettingsVersion` 35→36）；`--test-settings-bindings` 覆盖 5 个新下拉。
- 详见索引 `Docs/Indexes/FEATURE_INDEX.jsonl` 的 `ui.per_window_render_variant`、`Docs/Interfaces/INTERFACE_INDEX.jsonl` 的 `internal_api.window_render_variant`。

## 7. 无头渲染自检工具（Claude 新增）

- 该分层工具窗口未在开始菜单注册，computer-use 无法截图。Claude 新增 `DesktopCodexAssistant.exe --render-codexradar --out <dir>`（`Core/CodexRadarForm.RenderSample.cs`，test-only），离屏把 Classic/EvenGrid/EvenRow 各渲染一帧 PNG（2x 模拟 2880×1800@200%），另输出一张放大的 `radar-solo.png` 专门核对额度雷达线。
- 关键坑（Claude 排查并记录）：`WidgetSettings.Normalize()` 会强制关闭 `CodexRadarTestMode`（历史行为，防止升级后残留测试态）。因此渲染自检不能依赖设置 `CodexRadarTestMode`，需在构造窗体后直接把 `codexRadarSnapshot` 赋为 `BuildTestCodexRadarSnapshot(...)` 才能真正渲染额度雷达趋势内容。

## 8. 新增变体的标准做法

1. 给对应窗口枚举加一个成员。
2. 新建 `Core/<Form>.<Name>.cs`（`partial class`），只放该变体绘制方法，复用该窗口既有数据/字段，不新增共享 private 字段。
3. 在该窗口 `Draw*Content` 分发 `switch` 加一个 case。
4. 设置页该窗口「渲染变体」下拉的 `EnumOption.ToString` 加中文名；必要时把绑定加入 `VerifySelfTest`。
5. 删除变体 = 删文件 + 去 case，不牵动共享数据层。

## 9. 验证与部署

- 每一步均 ARM64 构建并运行 `--test`、`--test-layout`、`--test-settings-bindings`、`--test-logger`；额度雷达线与均布布局另用 `--render-codexradar` 离屏渲染人工核对。
- 正式 exe 按用户既定授权自动备份、覆盖 D:/E: 两处并重启，逐版本记录于 `Docs/Maintenance/CHANGELOG.jsonl`（版本 `1.0.3.34`–`1.0.3.41`）。
