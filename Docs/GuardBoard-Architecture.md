# Guard Board 架构

适用版本：1.0.6.03

本文负责电源守护状态机（睡眠防护、亮屏计时、断网自动睡眠、电池保护暂停窗口）、GUARD 看板窗口的布局与交互，以及该窗口与「特殊设置」三项程序守护的共用边界。

## 定位

GUARD 是左缘停靠队列的第四个成员，与 Spec Board、Codex 任务板、网络停靠面板并列。它把两处原本分散的功能合并到一个窗口：

- **电源守护**：原独立工具 `CodexSleepGuard`（`E:\Codexproject\desktopdata\CodexSleepGuard` 的 PowerShell 脚本）的三项能力，在本程序内用 C# 重新实现。
- **程序守护**：`AiQuickMenuForm`（特殊设置窗）的链接阻断、额度计划、CTF 重启三项，此处只是第二个入口，逻辑仍归特殊设置窗所属的 owner 路径。
- **电池保护**：原先只在扇形速控盘「辅助」分支下作为两个一次性按钮存在（`common_battery_care_pause` / `common_battery_limit_restore`），此处补上 24 小时暂停窗口的倒计时。

`AiQuickMenuForm` 与扇形速控盘的电池按钮保持存在，不因本窗口新增而移除；两处读写同一批设置键。

## 状态机

`GuardRuntime` 持有全部电源守护状态，`GuardBoardForm` 只负责绘制与命中。

`SetThreadExecutionState` 的标志按**调用线程**注册，线程退出即失效。因此 `GuardRuntime` 的所有状态变更必须发生在 UI 线程，不得放进 `Task.Run`；`ApplyExecutionState` 用单次 `ES_CONTINUOUS`（可选叠加 `ES_SYSTEM_REQUIRED` / `ES_DISPLAY_REQUIRED`）同时覆盖上锁与解锁两种情形。`GuardBoardForm.Dispose` 调用 `ReleaseAll`，避免进程退出时把要求留在一个已消失的线程上。

五条不变量：

- 亮屏计时是睡眠防护的**严格扩展**。`StartDisplayGuard` 隐含开启睡眠防护；`SetSleepGuard(false)` 同时清除亮屏计时。不存在「屏幕常亮但系统可休眠」的组合。
- 连通性未知**绝不**启动离线计时。`ResolveOnline` 返回 `null` 时按在线处理——误判离线的代价是在用户操作中途让机器睡过去。
- 断网计时可以持续显示网络状态，但只有用户已开启睡眠防护时才允许请求系统睡眠。默认未武装状态绝不因离线自动睡眠。
- 请求系统睡眠前先解除全部守护。持有 `ES_SYSTEM_REQUIRED` 的同时调用 `SetSuspendState` 是自相矛盾的，Windows 会忽略其中一个而文档未定义是哪个。
- 收到系统恢复事件后清除旧离线起点，从恢复后的下一次确定离线读数重新计满一个完整阈值，避免长时间睡眠后立即再次入睡。

`RequestAutoSleep` 记录 `lastAutoSleepUtc`，在一个完整阈值周期内不再触发第二次；`WidgetForm.HandlePowerBroadcast` 在系统恢复广播到达时经 `OperationForm.NotifyGuardBoardSystemResume` 调用 `GuardRuntime.OnSystemResume` 重置旧离线起点。两层保护共同避免睡眠/唤醒回环。

连通性来自网络窗口而非独立探测：`WidgetForm` 把 `NetworkMonitorForm.GetGuardOnlineState` 接到 `OperationForm.GuardNetworkOnlineProvider`，只有 `Online` / `Offline` 返回确定值，`Unknown` 与 `AdapterMissing` 返回 `null`。provider 缺失时回退 `NetworkInterface.GetIsNetworkAvailable`。

### 电池保护倒计时

MyASUS 的电池保养暂停固定持续 24 小时（`GuardRuntime.BatteryCarePauseHours = 24`），之后自行恢复 80% 上限。**本程序无法回读 MyASUS 的真实剩余时间**，`GuardBatteryCarePauseUntilUtcTicks` 记录的是我们成功发出 `acin_set` 的时刻加 24 小时；窗口文案按此口径书写，不宣称读取了 MyASUS 内部状态。倒计时只在命令确实离开进程后才起算——`RequestBatteryCareFromGuardBoard` 的完成回调在失败时不写时钟。

## 持久化

守护状态是运行时状态而非偏好，但仍全部持久化：无人值守的机器随时可能睡眠或断电，只在退出时落盘的守护会恰好在最需要它的时候消失。`GuardBoardForm.PersistRuntimeState` 在每次状态变更后立即经 `OperationForm.PersistGuardStateFromBoard` 写盘。

| 设置键 | 含义 |
|---|---|
| `GuardBoardLeftDockEnabled` | 是否在左缘停靠队列中显示 GUARD tab |
| `GuardBoardLeftDockTabCenterY` | tab 中心 Y；`-1` 为自动（工作区中线 +3 槽位） |
| `GuardBoardAutoHideSeconds` | 手动打开后的空闲自动收起秒数，默认 30 |
| `GuardSleepEnabled` | 睡眠防护开关，重启后恢复 |
| `GuardSleepSinceUtcTicks` | 睡眠防护起始时刻（**起点**，只在过去有效） |
| `GuardDisplayMinutes` | 亮屏计时档位，取值必须落在 `GuardDisplayMinuteSteps` |
| `GuardDisplayUntilUtcTicks` | 亮屏计时截止时刻（**终点**，只在未来有效） |
| `GuardOfflineThresholdMinutes` | 断网自动睡眠阈值，取值必须落在 `GuardOfflineThresholdMinuteSteps` |
| `GuardBatteryCarePauseUntilUtcTicks` | 电池保护暂停窗口终点 |

两个档位是**阶梯**而非区间：`GuardDisplayMinuteSteps = {30, 60, 120, 300, 480}` 分、`GuardOfflineThresholdMinuteSteps = {1, 5, 10, 30}` 分，与 CodexSleepGuard 原下拉框一致。`NormalizeGuardDisplayMinutes` / `NormalizeGuardOfflineThresholdMinutes` 对越界值取最近档位而非夹到区间端点，手工改坏的 settings.ini 不会产生 UI 显示不出来的时长。

两个"终点"型 tick 过期即视为未武装，"起点"型 tick 落在未来时回退为当前时刻。窗口尺寸复用 `SpecBoardWidth/Height`（默认 648×400）与 `SpecBoardTransparencyOverridePercent` / `SpecBoardScaleOverridePercent`，与网络停靠面板的做法相同，四个停靠板因此展开成同一矩形。

## 窗口与布局

`GuardBoardForm` 继承 `LayeredWidgetFormBase`，绘制在 `GuardBoardForm.Layout.cs`。视觉家族与 SpecBoardForm / NetworkMonitorForm.DockedLayout 一致：`AppBackground` 底色 @238、`Border` 发丝线、`S(12)/S(9.2)/S(9)/S(7.8)` 字号阶梯。所有行高经 `MeasureLineHeight` 实测，不写死像素。

本窗口不参与隐藏模式配色保护（与 SpecBoardForm 相同传 `false`）：其去饱和会压平绿/黄/红的状态编码，而这正是守护状态的唯一载体。

### 左栏：计时环与两条轨道

外环始终承载**当前正在发生的那个守护**：有亮屏计时走计时剩余（黄），否则走睡眠防护已持续时长（绿，以 `SleepGuardGaugeHours = 12` 小时为参考扫程）。内环只在两者同时运行时出现。早期版本把外环固定绑给亮屏计时，结果在最常见的状态（只开睡眠防护）下整块最大元素是空的，读起来像坏了而不是空闲。

环下方两条同构轨道，共用 `DrawTrackRow`：

- **断网自动睡眠**：在线时标记停在 0，离线后随时长右移；进度过 0.6 转危险色。它回答的是「离睡过去还有多远」，不只是「断了没有」。
- **电池保护**：暂停期间填充条走向 24 小时后的自动恢复；行右侧内嵌暂停/恢复按钮。

### 右栏：控制卡片

两个分段（电源守护 / 程序守护）各三张卡片。卡片控件自右向左布局，文本块按剩余宽度测量——反过来做就是窄宽度下文字压到开关底下的成因。

`SpecBoardWidth < GuardBoardForm.CompactRingMinimumLogicalWidth = 460` 时进入**紧凑单列模式**：左栏整体撤除，电池保护降级为电源守护分段下的第四张卡片。功能在窄宽度下只允许重排，不允许消失。

### 交互

命中区在绘制时登记（`recordHitTargets`），`OnMouseUp` 线性匹配。档位 `+/-` 走 `StepValue`，到达阶梯两端停住而不回绕：多点一下就从 8 小时跳回 30 分会静默终结一次长守护。

`GuardBoardForm.RunSelfTest` 在两种宽度下校验全部命中区存在、面积非零且两两不重叠——控件画得出来却点不到、或两个控件共用像素（先登记者静默获胜），都是截图上看不出来的故障。

## 生命周期与互斥

`OperationForm.GuardBoard.cs` 持有窗口并转发所有对外命令。启动时即构造隐藏窗口，即使 `GuardBoardLeftDockEnabled=false` 也不能省略：状态机住在窗口里，若窗口不存在或维护 timer 随 tab 一起停掉，已持久化的守护和到期动作都会失效。

四个停靠板互斥：展开任一板收起其余三个。四条展开路径共用 `OperationForm.CollapseLeftDockBoardsExcept(LeftDockBoardKind)`，成员表由 `GetLeftDockBoardMembership` 单点维护——早期各路径手写同伴列表，`PrepareForCodexTaskOverlayShow` 因此漏掉了 GUARD，从 GUARD 梯形移到任务梯形时两板会叠在一起。互斥不是观感问题：梯形间距 40 逻辑像素而板高 400，两板大面积重叠，被盖住的那块的 `UpdateDockCollapse` 会把落在重叠区的光标读成仍悬停在自己身上，收起计时器永不启动。`RunLeftDockMutualExclusionSelfTest` 断言成员表覆盖枚举全部取值，新增第五块板漏登记会直接让 `--test` 失败。全屏隐藏、显示挂起/恢复分别经 `SetGuardBoardHiddenForFullscreen`、`PrepareGuardBoardForDisplaySuspend`、`RecoverGuardBoardAfterDisplayResume`。

维护 tick 500 ms，从窗口构造完成起持续运行，与 tab 是否启用、面板是否展开无关——守护要跨夜生效。守护状态色只在看板内容中表达；常驻 tab 固定使用队列第四位的紫色角色编码，不再因有守护、电池暂停或空闲而变色。窗口可见时每 tick 无条件重绘，因为板上每个倒计时都是秒级精度。

防烧屏 salt：`GuardBoardSalt = 53`、`GuardBoardDockTabSalt = 59`。防烧屏配色保护开启且 GUARD 看板收起时，梯形保持低亮灰色，中央箭头仍显示紫色；看板真正展开后梯形才恢复紫色，收起后立即回灰。展开的 GUARD 看板外沿使用同一紫色的共享 Radar 风格内描边。梯形与箭头处于同一分层位图和同一个窗口边界内，因此 `GuardBoardDockTabSalt` 的 Y 轴微位移同时覆盖两者；展开看板的 `PositionAtLeftDock` 使用 `ApplyRuntimeOffsetWithPinnedX` 固定 X，只保留 `GuardBoardSalt` 的 Y 轴微位移。共享绘制、定位和命中契约见 `Docs/Performance-And-Window-Runtime.md` §6.1。

## 渲染取样

`--render-guard sample --out <dir>` 产出四张 PNG：`guard-idle`（全空闲）、`guard-armed`（睡眠 2h18m + 亮屏剩余 4:32:10 + 电池暂停 8 小时）、`guard-offline`（离线 6/10 分，红条）、`guard-compact`（320 逻辑宽单列）。`--render-guard current` 按真实 settings.ini 出 `guard-current.png`。

取样状态需要「已持续数小时」的守护，`GuardRuntime.BackdateSleepGuardForRenderSample` 专供该用途；harness 在每个窗口析构前调用 `ReleaseAll`，避免一次渲染在调用进程上留下 `ES_SYSTEM_REQUIRED`。
