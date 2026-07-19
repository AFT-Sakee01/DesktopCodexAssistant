# Spec Board 架构

适用版本：1.0.6.03

本文负责跨项目 spec 账本读取、对账、看板窗口、交互和只读边界。

## 数据流

`OperationForm.ToggleSpecBoardWindow` 按需创建 `SpecBoardForm`。窗口通过后台任务调用 `SpecBoardReader.Read`，逐行读取 `SpecBoardLedgerPath` 指向的 `SPEC_BOARD.jsonl`，并从同目录读取 `PROJECTS.json`。reader 只向 UI 发布完整 `SpecBoardSnapshot`；绘制路径只消费快照，不执行磁盘 IO。

账本状态封闭为 `pending`、`needs_revision`、`awaiting_verify`、`done`、`abandoned`。`needs_revision` 使用 `revision_requested_utc` 作为事件时间，缺失时回退 `updated_utc`；所有账本行解析 `UpdatedUtc` 供新鲜度判定。对账阶段另外合成只存在于磁盘的 `unregistered` 行，并把仍需处理但文件不存在的账本行标记为 `FileMissing`。坏 JSON、未知状态或缺字段行按行跳过并累计 `MalformedLines`；账本缺失或 IO 失败生成 `LedgerMissing` 空态，不创建文件。

项目注册表不可用时，账本 `project` 仍直接形成项目栏，对账停用并在 footer 显示警告。注册项目的 `root` 不可达时跳过该项目，不把全部 spec 误报为未登记。`GoalSpec` 文件不进入对账结果。

## 窗口与交互

`SpecBoardForm` 继承 `LayeredWidgetFormBase`，复用 `NativeMethods.LayeredBitmapSurface`、`UiFontCache`、`DesignTokens` 和 `BurnInProtection`。窗口无任务栏按钮、无激活显示。`SpecBoardWidth/Height`（默认 648×400）是 96-DPI 逻辑尺寸：`GetDesiredSize` 按 `LayerScale`（DPI × 生效窗口缩放）放大成物理窗口尺寸；`SpecBoardScaleOverridePercent=-1` 时生效值来自全局分辨率兼容缩放，显式 `40..200` 时覆盖全局并由左缘 tab 共同跟随。内容和画布必须走同一缩放系数，否则高 DPI/独立缩放下内容会拥挤或裁切。标题、项目行、段头、卡片和 footer 高度均来自当前字体实测；卡片副行的项目名按实测可用宽度显示全名，放不下才从尾部截断（保住右侧事件时间，`FitProjectLabel`）。

`SpecBoardWidth` 允许 240–700：逻辑宽小于 `SpecBoardForm.CompactRailMinimumLogicalWidth = 360` 时进入**紧凑单列模式**——项目栏、项目过滤与新鲜度点隐藏（卡片副行本就带项目名，信息不丢；粘滞的项目过滤自动复位为"全部"），行动流占满全宽，footer（管理/关闭/统计）由两种布局共享的 `DrawBoardFooter` 绘制、始终保留。紧凑模式有独立自测（`RunCompactLayoutSelfTest`：栏隐藏、过滤复位、卡片近全宽不越界不压 footer），`--render-specboard sample` 额外产出 `specboard-compact.png`。

宽布局下左栏按项目过滤，右栏依次显示未登记、需要执行、需要修改、等待验证。四段同时非空时按右栏实测高度压缩段头、卡片和间距，每段仍至少保留一张完整卡片；不足处在段头或段尾显示 `+N`。卡片单击等待 `SystemInformation.DoubleClickTime` 后把 spec 绝对路径写入系统剪贴板；复制成功后，窗口右下角显示约 2 秒的绿色“已复制 Spec 绝对路径”提示，并复用维护 tick 清除。双击取消待复制动作并通过系统默认程序打开 spec，丢失或异常路径回退到项目目录。双击后的第二次 MouseUp 会被吞掉，保证不会在打开文件后再次覆盖剪贴板。项目行、卡片和 footer 的管理/关闭命中区保留原动作；左键点击其余非控件/空白区域直接收起小看板。主看板本身不提供状态写入控件。

## 新鲜度

`SpecBoardSeenStateStore` 固定使用 `%LOCALAPPDATA%\DesktopCodexAssistant\SpecBoardSeenState.json` 保存项目到 `lastSeenUtc` 的映射。文件首次不存在或损坏时，用第一次完整快照的 `ScanTimeUtc` 为现有项目播种，升级后不会满屏新点；之后新出现且没有缓存条目的项目以 `DateTime.MinValue` 为基线。

项目全部非合成账本行的最大 `UpdatedUtc` 大于缓存值时，左栏项目名前显示 6px 青色实心点。单击具体项目行时以当前快照 `ScanTimeUtc` 标记已读并原子保存；单击“全部”不清除任何项目。该信号与红色未执行/未登记、紫色需要修改、黄色待验证计数正交。

`SpecBoardAutoPopupEnabled` 默认开启。`OperationForm` 在启动时创建隐藏的 `SpecBoardForm` 句柄并调用 `StartAutoPopupMonitoring`；窗口隐藏时仍监听中央账本和 `PROJECTS.json` 派生出的各项目 `SpecGlob` 目录。首次完整扫描仅把现有 `project + spec_path` 写入进程内基线，不弹出旧项；之后第一次出现的未登记、待执行、需要修改或待验证文件才触发弹窗。watcher 信号经 500 ms 防抖后立即完整对账，60 s 普通轮询和 5 min 完整对账负责恢复丢失事件。

自动弹窗把新卡片绘制为 Accent 高亮，并按 `SpecBoardAutoPopupSeconds`（默认 5 s，范围 1–120 s）关闭。鼠标进入窗口会暂停并持续重置完整倒计时，移出后重新计时；该倒计时与手动打开窗口使用的 `SpecBoardAutoHideSeconds` 分离。小看板 footer 的“关闭”按钮只关闭小看板，不影响独立管理窗或后台监测。

## 管理窗口

主看板 footer 的“管理”按钮或双击启动器的“Spec 管理”按钮通过 `SpecBoardForm.ShowManagerWindow` 直接打开非模态的全自绘工作台 `SpecBoardManagerForm`。启动器入口不会先显示小看板。窗体 `FormBorderStyle.None` + 圆角 `Region` + 自绘标题栏（`WM_NCHITTEST` 返回 `HTCAPTION` 实现拖动、四边 `S(6)` 热区实现缩放、自绘关闭按钮），**刻意不继承 `LayeredWidgetFormBase`**——分层管线不合成子窗口，而备注/搜索/删除确认必须是可聚焦的原生 `TextBox`（全窗仅这 3 个原生子控件，无边框深色嵌入自绘卡片）。其余全部 OnPaint 自绘 + 命中矩形表（沿用主看板 hitTargets 模式）。管理窗与主看板生命周期独立，主看板自动收回不会关闭管理窗。

交互：顶部为项目筛选（深色 `ContextMenuStrip`）、七枚互斥状态筛选胶囊、搜索框与"批量登记未登记项"；左列表（44% 宽）为色点+标题+项目行，Ctrl/Shift 多选；右详情为粗体标题（≤2 行省略）、"项目 · 文件名 ✓"元信息、`PathEllipsis` 全路径、时间线、**五枚状态胶囊（当前实心、其余描边，单击立即写账本）**、备注卡（脏时保存钮点亮、切换选择前弹未保存确认）、打开/定位/危险按钮。未登记行胶囊替换为"登记为未执行"；多选 N≥2 时右侧变批量模式（胶囊批量应用，含未登记项则登记为该状态；批量删除仅账本条目）。列表有焦点时数字键 1-5 直接应用五态，Esc 关窗。排序固定为状态优先级内按最近更新倒序。

绘制规则：`SpecBoardManagerWidth/Height` 是 96-DPI 逻辑尺寸（×`DpiX/96` 并钳制工作区）；行高全部字体实测。**滚动区（左列表与右详情）必须绘制进离屏位图再贴回**——`TextRenderer` 走 GDI 输出、不受 GDI+ `Graphics.SetClip` 约束，直接在窗口 Graphics 上绘制会让滚动后的文字穿透到工具栏/标题栏；位图边界是硬裁剪。原生输入框同理不吃位图裁剪，部分滚出可视区时用 `Control.Region` 裁掉越界部分。命中矩形统一经 `hitOffset` 平移并与可视区求交。选择变化时右侧滚动复位、危险区收起、备注脏状态确认丢弃；危险区展开时自动滚到底部使其入视野。`--render-specboardmanager sample|current --out <dir>` 在屏幕中央真屏截取（`CopyFromScreen` 客户区）fixture 的 detail/batch/min 与真实账本的 current-detail/current-unregistered 共五张 PNG 供布局与遮挡验收。

危险区默认折叠并与安全操作分离。“删除账本条目”只删除账本行；“删除条目并删除源文件”默认要求区分大小写输入完整文件名，先检查项目 `Docs/Technical/INDEX.jsonl` 引用并要求显式强制确认，再通过 Windows 回收站删除文件。文件删除失败时不删除账本行。

`SpecBoardLedgerStore` 是程序内唯一账本写入口。每次操作都重新整份读取并用 `id + 旧 UpdatedUtc` 校验，冲突即拒绝覆盖并刷新；成功写入前滚动覆盖 `SPEC_BOARD.jsonl.bak`，同目录写 `.tmp` 后用 `File.Replace` 原子替换。状态、备注、登记、批量和删除共享该路径，人工修改固定写 `updated_by="User (SpecBoardManager)"`。AI 会话仍使用外部 `spec-board` skill，不调用管理服务。

RadialDial 核心圆圈或经典 Start 按钮双击调用 `ToggleLauncherTrioWindow`；这两个入口的单击动作等待 `SystemInformation.DoubleClickTime` 后才提交，双击到达会取消待执行单击并吞掉第二次 MouseUp，避免一次双击同时开关 Radial 菜单或 Windows 开始菜单。Radial 单击菜单、双击启动器和 Spec 小看板三者互斥：每个入口在显示前关闭另外两个，后打开者覆盖先打开者；管理窗不参与互斥。默认位置与操作面板左对齐，底边位于操作面板顶边上方 10 px；`SpecBoardLeftX`、`SpecBoardBottomY` 为 `-1` 时每次呼出重新自动锚定，具体坐标则由全局布局编辑器维护。

## 左缘停靠（EdgeDockTab）

Spec、Codex Task、Network 与 GUARD 四个停靠看板共用 `EdgeDockTabForm`（`Core/EdgeDockTabForm.cs`）——`5×30` 逻辑尺寸、左边全高向右收窄的梯形，中央有同角色色、较低不透明度的向右三角箭头，整体贴在工作区左缘。队列从上到下固定为 Network 蓝、Spec 橙、Codex Task 绿、GUARD 紫；展开看板也继承对应角色色的共享圆角内描边，精确线宽与绘制契约见 `Docs/Performance-And-Window-Runtime.md` §6.1。鼠标移上 tab 即展开对应看板；指针离开看板与 tab 后经 `LeftDockCollapseSeconds`（默认 1 s，范围 0–30）自动收起。**一个看板一枚 tab**，互不影响。

tab 自身用 120 ms 计时器轮询 `Cursor.Position` 判定 hover（不依赖分层窗 alpha 命中测试，整个 `5×30` 矩形都是可命中区）。边框和任务栏仍隐藏，`Text`/`AccessibleName` 使用各自稳定名称，让辅助功能和 UI 验收工具能区分四枚微型窗口。展开时看板左缘落在 `工作区左缘 + tab 宽`，tab 保持可见，指针可以从 tab 连续滑入看板而不触发收起倒计时。tab 是**永久可见**元素，因此四枚都使用独立防烧屏 salt；梯形与箭头绘制在同一分层位图内，`EdgeDockTabForm.PositionAtLeftEdge` 先取得 `ApplyRuntimeOffset`，再由 `PinToLeftEdge` 丢弃水平分量并固定到 `workArea.Left`，所以两者共同承受 Y 轴微位移，鼠标贴住主屏或负坐标副屏的绝对最左像素时仍能命中。四块展开看板同样固定水平锚点：各自 `PositionAtLeftDock` 调用 `ApplyRuntimeOffsetWithPinnedX`，统一停在 `工作区左缘 + tab 宽度`，仅保留独立 salt 的 Y 轴微位移。隐藏模式与防烧屏配色保护的共享视觉契约以 `Docs/Performance-And-Window-Runtime.md` §6.1 为单一事实源。

`LeftDockOutsideClickCollapseEnabled` 默认开启。停靠展开的 Spec/Codex Task 看板，以及 Spec 的自动弹窗态，在用户点击看板外部（桌面、其他窗口或另一块看板）时收回；自身窗口、自己的 tab 与 Spec 管理窗属于排除区，手动打开且未停靠的常驻 Spec 看板不受影响。板内空白区域原有的 `HideBoard()` 语义继续保留，两条关闭路径互补。

外部点击由共享 `OutsideClickDismissalMonitor` 处理，不使用失焦事件、长期鼠标捕获或全局鼠标钩子，也不新增计时器。`EdgeDockTabForm` 的既有 120 ms hover tick 读取 `GetAsyncKeyState(VK_LBUTTON)` 的当前按下位与“上次查询后按过”位，并把单调递增的点击序号分别交给两块看板消费；Spec 的 500 ms 维护 tick 为无 tab 的自动弹窗补兜底。进程内所有左键异步状态读取必须经该监测器，避免某个调用方提前消耗低位。外部点击收回后，tab 在 800 ms 内且光标尚未离开 tab 区时禁止重新展开，避免左缘点击造成“收回后秒开”；一旦离开 tab 即解除抑制。

tab 中心 Y 由各自 `*LeftDockTabCenterY` 指定，`-1`（`AutoLeftDockTabCenterY`）表示自动：从上到下为 Network `-3`、Spec `-1`、Codex `+1`、GUARD `+3` 个 `LeftDockTabAutoOffsetY=20`，四枚 30 px 高的 tab 相邻而不重叠。停靠开启时看板必须在启动时就构造（即使收起）——tab 是它唯一的常驻表面，由所属宿主的运行时设置链路负责建立。收起状态下看板维护继续驱动 tab 的防烧屏漂移与收起倒计时；全屏隐藏与显示挂起会一并停掉 tab。

几何、自动槽位、边缘命中和隐藏/保护态视觉层级有独立自测（`EdgeDockTabForm.RunSelfTest`：`5×30` 梯形方向、中央右箭头、蓝橙绿紫角色映射、四枚自动 tab 不重叠、主屏与负坐标副屏左缘可命中、收起保护态灰色、展开态恢复角色色）；外部点击的边沿、双消费者、命中排除与回弹抑制由 `OutsideClickDismissalMonitor.RunSelfTest` 覆盖。两者都随 `--test-operation-panel` 运行；`--render-operation` 为 Network、Spec、Codex、GUARD 各产出一张 8× 状态条，顺序为普通静止/悬停、隐藏静止、保护收起、保护展开/展开悬停。

## 操作面板双击启动器（LauncherTrio）

双击操作核心弹出 `OperationLauncherTrioForm`（`Core/OperationForm.LauncherTrio.cs`）：三个圆形按钮沿星座面板同款弧线排布在第二层级位置。按钮直径为主 Start 按钮的 0.80×；弧半径复用 RadialDial 的 8°–82° 稀疏弧与第二层半径公式。美术继续使用低透明度暗调圆盘、分类色外光环、极淡白内环、柔和字形和细灰连接线。自上而下：① Spec 管理（`OpenSpecBoardManagerWindow`，直接显示管理窗）② Codex 任务（`ToggleCodexTaskBoard`，见 `Docs/CodexRadar-Architecture.md` §5.2）③ 睡眠防护（`LaunchSleepGuard`，优先运行 E: 的 `Start-CodexSleepGuard.cmd`，再回退 D: 镜像）。`LauncherTrioAction`、`TrioLabels`、`TrioTints` 三者的下标顺序必须一致，索引 0 位于弧顶。旧版 QuickGrid/12 格入口已从此启动器删除；QuickGrid 实现保留为兼容代码，但不再由双击菜单暴露。窗体继承 `LayeredWidgetFormBase`，点按钮外或选完即隐藏；`--render-operation` 产出 `operation-launcher-trio.png` 供美术验收。

OLED Typographic、AmberHud、WarmCard、Phosphor 变体复用现有语义色，且使用灰度文字抗锯齿，避免 ClearType 在分层位图中生成蓝色子像素。

## 刷新与生命周期

全部间隔、单飞、防抖、隐藏、挂起和恢复规则由 `Docs/Component-Refresh-Rules.md` 的 Spec Board 表负责。`OperationForm` 转发设置、全屏隐藏、显示挂起和恢复；普通隐藏且自动弹窗开启时 watcher 与 500 ms 维护计时器继续运行，显示挂起、全屏隐藏、关闭自动弹窗或应用退出时全部停止。

## 存储边界

`D:\E_Drive_Files\Codexproject\_spec_board\` 是用户开发环境级中央资产，与各项目源码和 Docs 同级，不是普通运行态缓存。主看板和 reader 对其只读；用户主动打开的管理窗口是唯一程序内写入例外，写入只限 `SPEC_BOARD.jsonl`、同目录临时文件和单份 `.bak`，不修改 `PROJECTS.json` 或其他项目索引。AI 状态写入继续只通过外部 `spec-board` skill。`SpecBoardSeenState.json` 是本程序 UI 已读状态，固定放在 `%LOCALAPPDATA%\DesktopCodexAssistant`。
