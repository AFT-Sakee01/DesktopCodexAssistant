# Spec Board Manager Workbench Redo SPEC

## Metadata

- Document: `Fable5-SpecBoardManagerWorkbenchRedo-SPEC-v1.0.5.25-20260713-120933.md`
- Generated model: Claude Fable 5
- Timestamp local: `2026-07-13T12:09:33+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.25`
- Target implementation version: `1.0.5.26+`
- Status: approved（用户 2026-07-13 六方案比选后拍板方案 A"全自绘工作台"，并授权直接实施部署）
- Supersedes: `Fable5-SpecBoardManagerAndFreshness-SPEC-v1.0.5.11-20260712-013045.md` 的 §3 管理窗口部分（数据层/状态机/新鲜度不变）

## Goal

**完全重写** `Settings/SpecBoardManagerForm.cs` 为全自绘工作台：抛弃系统标题栏与全部原生控件观感（白滚动条/白下拉箭头/系统按钮），采用与六个挂件窗口相同的绘制语言（DesignTokens、圆角外壳、自绘控件），状态修改从"下拉+应用"两步式改为**五胶囊单击直接生效**。账本写入层（`SpecBoardLedgerStore`）与安全语义（重读-定位-写入、`.bak`、回收站、索引引用阻断、输入文件名解锁）全部保留不动。

## 0. 保持不变的边界

| 项 | 约束 |
|---|---|
| 集成点 | 构造签名 `SpecBoardManagerForm(WidgetSettings)`、`ActivateOrShow()`、`FormClosed` 事件（`SpecBoardForm.OpenManagerWindow` 不改） |
| 写入层 | `SpecBoardLedgerStore.TrySetStatus/TrySetNote/TryRegister/TryRemoveRows/TryRemoveRowAndRecycleFile/IsReferencedByTechnicalIndex` 原样调用，`updated_by="User (SpecBoardManager)"` |
| 设置键 | `SpecBoardManagerWidth/Height`（96-DPI 逻辑尺寸 × DpiX/96）、`SpecBoardManagerDangerZoneRequiresTypedConfirm` 复用，不新增键 |
| 测试入口 | `--test-specboard-manager` 继续跑 `RunSelfTest`（内容重写）；`--render-specboardmanager sample|current` 继续真屏客户区截取 |
| 数据 | `SpecBoardReader.Read` 快照；五态+unregistered；窗口对 `_spec_board` 目录只读（写入仅经 LedgerStore） |

## 1. 窗体架构（非分层的自绘 Form）

- `FormBorderStyle.None` + `SetStyle(UserPaint | AllPaintingInWmPaint | OptimizedDoubleBuffer | ResizeRedraw)`。**不用** `LayeredWidgetFormBase`/UpdateLayeredWindow——分层管线不合成子窗口，而备注/搜索/确认框必须是真实可聚焦的原生 TextBox（无边框、深色背景，嵌入自绘卡片内视觉无缝）。这是与主看板管线的刻意差异，须写入架构文档。
- 圆角外壳：`Region` 由圆角 `GraphicsPath` 生成，随尺寸更新；外描边 1px `DesignTokens.Colors.Border`。
- `WndProc` `WM_NCHITTEST`：标题区（除关闭按钮）返回 `HTCAPTION` 实现拖动；四边/四角 `S(6)` 热区返回 HT 边缘值实现缩放，`MinimumSize` = 逻辑最小值 × DPI。
- 全部尺寸经 `S()`（`DpiX/96`），行高由字体实测，禁止手猜像素。
- 仅 3 个原生子控件：搜索框、备注框（多行）、删除确认框——全部 `BorderStyle.None` + `Control` 底色嵌入自绘容器。其余（按钮、胶囊、筛选、列表、滚动条、表头、关闭钮）全部 OnPaint 自绘 + 命中矩形表（沿用 `SpecBoardForm` 的 hitTargets 模式）。

## 2. 布局（自上而下）

```
┌ 自绘标题栏：SPEC 管理            [✕]           ← 拖动区
├ 工具行：[项目 ▾(深色弹出菜单)] [全部|未登记|未执行|需修改|待验证|完成|中断 状态胶囊组(互斥)]
│         [🔍搜索框________]  [批量登记未登记项]
├────────────┬──────────────────────────────
│ 左列表(44%) │ 右详情
│ 色点+标题    │ 单选: 标题(粗体≤2行省略) / 项目·文件名✓ / 全路径(EllipsisPath)
│ 自绘细滚动条 │       时间线 / 状态胶囊行(当前实心其余描边,单击即写账本)
│ Ctrl/Shift  │       备注卡(嵌原生TextBox)+[保存](脏时点亮) / [打开][定位][危险▸]
│ 多选        │       危险区(红描边卡:删除条目/确认框/删除条目并删源文件)
│             │ 未登记: 胶囊行替换为[登记为未执行]
│             │ 多选N≥2: "已选N项"+胶囊行(批量)+[批量删除条目]
│             │ 零选: 居中灰字提示
└────────────┴──────────────────────────────
```

- 排序固定：状态优先级（未登记→未执行→需修改→待验证→完成→中断）内按最近更新倒序；不再提供排序下拉（搜索+筛选覆盖该需求，减一个控件）。
- 状态筛选胶囊互斥单选，"全部"默认；再次点击当前项回到"全部"。项目筛选用深色 `ContextMenuStrip`（自定义 Renderer 走 DesignTokens 配色）。
- 胶囊点击**立即** `TrySetStatus`（终态保护：done/abandoned 行的胶囊仍可点，允许人工纠错——本窗口定位就是纠错工具）；写入失败弹冲突提示并刷新。
- 选择变化：滚动复位、危险区收起、备注脏状态丢弃（有脏未存时弹确认）。
- 键盘彩蛋：列表有焦点时数字键 `1-5` = 对选中行应用五态之一；`Esc` 关窗。

## 3. 视觉规格

- 底色 `AppBackground`，卡片 `Surface`，选中行 `ControlActive`，文字 `TextStrong/TextMuted`，状态六色沿用主看板（橙未登记/红未执行/紫需修改/黄待验证/绿完成/灰中断）。
- 胶囊：圆角=半高，当前状态实心（状态色底+黑字或深字），非当前描边（状态色边+状态色字），hover 加亮；禁用（多选含未登记行时的批量胶囊）灰化。
- 自绘滚动条：宽 `S(6)` 圆角 thumb，`GlyphMuted` 半透明，hover/拖动加亮；支持滚轮与拖动。
- 关闭按钮：hover 变 `DangerClose` 底色。
- 危险区：红描边（`DangerBorder`）+ 暗红底卡片，"删除条目并删除源文件"按钮未解锁时灰化。

## 4. 自测与渲染验收（重写 RunSelfTest / RenderSamples）

- `RunSelfTest`：fixture 账本 → 断言：窗体尺寸=逻辑×DPI 且 `FormBorderStyle.None`；命中表含 5 个状态胶囊；程序化调用胶囊路径把行写为 `needs_revision` 且 `updated_by` 正确；备注保存路径；未登记行登记路径；批量路径（多选 3 行改 done）；确认框未匹配文件名时删源文件命中目标缺席/禁用、匹配后可用；选择变化后危险区收起。
- `RenderSamples`：sample fixture 出 `detail`（单选+危险区展开）/`batch`（3 选）/`min`（最小尺寸）三张真屏客户区 PNG，屏幕中央定位；current 模式读真实账本出 `current-detail`/`current-unregistered`。逐张人工核对零遮挡、零原生白色残留。

## 5. 文档与索引

1. `Docs/SpecBoard-Architecture.md`：管理窗口小节整段重写（自绘架构、非分层理由、胶囊交互、三原生子控件例外、键盘快捷键）。
2. `FEATURE_INDEX.jsonl`：`spec_board.manager_window` 行刷新 `updated_version` 与 aliases（+胶囊/自绘/workbench）。
3. `CHANGELOG.jsonl`：变更+部署记录；本 spec INDEX 行回填 `implemented`。
4. 账本：本 spec 登记 pending → 实施完推 `awaiting_verify`。

## 6. 验收 Gate

- G1 `--test-specboard-manager` 全绿（含重写断言）。
- G2 `--test-settings-bindings`、`--test-layout` 不回归。
- G3 五张渲染 PNG 人工核对：布局、胶囊态、危险区、最小尺寸无遮挡、无系统白色控件残留。
- G4 真机：主看板"管理"按钮打开新窗体、标题栏拖动、边缘缩放、Esc 关闭、胶囊单击改状态 2 秒内主看板同步。
- G5 文档 Gate 全绿；ARM64 构建零警告；备份-覆盖-重启部署，版本一致。

## 7. 风险与回滚

- 全自绘命中测试是新增复杂度：命中表模式已在 `SpecBoardForm` 验证过，逐 Paint 重建。
- 原生 TextBox 焦点与自绘区域的 Tab 顺序简单化处理（Tab 仅在 3 个输入框间循环）。
- 回滚 = git 还原 `Settings/SpecBoardManagerForm.cs` 单文件（集成点签名不变）。
