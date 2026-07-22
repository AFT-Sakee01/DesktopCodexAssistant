# Guard Board 架构

适用版本：2.0.0.1

本文负责电源守护状态机（睡眠防护、亮屏计时、断网自动睡眠、电池保护暂停窗口）、GUARD 看板窗口的布局与交互，以及该窗口与「特殊设置」三项程序守护的共用边界。

## 定位

GUARD 是左缘停靠队列的第四个成员，与 Network、Spec Board、Codex 任务板和第五席 Codex IQ 看板并列。它把两处原本分散的功能合并到一个窗口：

- **电源守护**：原独立工具 `CodexSleepGuard`（`E:\Codexproject\desktopdata\CodexSleepGuard` 的 PowerShell 脚本）的三项能力，在本程序内用 C# 重新实现。
- **程序守护**：`AiQuickMenuForm`（特殊设置窗）的链接阻断、额度计划、CTF 重启三项，此处只是第二个入口，逻辑仍归特殊设置窗所属的 owner 路径。
- **电池保护**：原先只在扇形速控盘「辅助」分支下作为两个一次性按钮存在（`common_battery_care_pause` / `common_battery_limit_restore`），此处补上 24 小时暂停窗口的倒计时。

`AiQuickMenuForm` 与扇形速控盘的电池按钮保持存在，不因本窗口新增而移除；两处读写同一批设置键。

## 状态机

`GuardRuntime` 持有全部电源守护状态，`GuardBoardForm` 只负责绘制与命中。

### 两层防睡眠

`ApplyExecutionState` 同时施加两层：

- **持久电源请求（S0 唯一有效层）**：`NativeMethods.PowerRequestGuard` 用 `PowerCreateRequest` 建立一个贯穿守护生命周期的请求对象，按需 `PowerSetRequest` / `PowerClearRequest` 三类请求——`SystemRequired`（睡眠或亮屏时）、`ExecutionRequired`（睡眠时）、`DisplayRequired`（亮屏时）。这是本机 **S0 Modern Standby** 上真正把机器留在活动相位的机制，对应独立工具 `CodexSleepGuard` 的 1.0.0.2 修复。
- **兼容层 ES 标志**：单次 `SetThreadExecutionState`（`ES_CONTINUOUS` 可选叠加 `ES_SYSTEM_REQUIRED` / `ES_DISPLAY_REQUIRED`），S3 系统仍靠它。

只用 `ES_SYSTEM_REQUIRED` 的旧实现在本机上随显示器熄灭与其它桌面应用一并被挂起，守护静默失效——这正是"旧的防止睡眠对本机无效"的根因，故补上持久电源请求层。`SetThreadExecutionState` 的标志按**调用线程**注册，线程退出即失效；电源请求对象本身不受线程亲和约束，但为与 ES 标志保持一致的调用次序，两层的所有变更都必须发生在 UI 线程，不得放进 `Task.Run`。`PowerRequestGuard.Sync` 先置位需要的请求再清除不需要的，收紧守护的过渡永不留下无保护空档；每个方法都是尽力而为且绝不抛出——电源 API 失败必须降级到 ES 标志，不能拖垮维护 tick。`GuardBoardForm.Dispose` 调用 `ReleaseAll`：清除三项请求、`CloseHandle` 请求对象、并用 `ES_CONTINUOUS` 归还标志，避免进程退出时把要求留在已消失的线程或未释放的请求对象上。

`SystemPowerRequestActive` / `ExecutionPowerRequestActive` / `DisplayPowerRequestActive` 三个只读属性反映 OS **实际接受**的请求（API 失败即为 `false`，而非守护开关的镜像），供看板绘制电源请求状态块。

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

守护状态是运行时状态而非偏好，但仍全部持久化：无人值守的机器随时可能睡眠或断电，只在退出时落盘的守护会恰好在最需要它的时候消失。`GuardBoardForm.PersistRuntimeState` 在每次状态变更后立即经 `OperationForm.PersistGuardStateFromBoard` 写盘。该路径只传递六个 GUARD 运行态字段；`WidgetForm.PersistGuardStateFromOperationPanel` 将它们合并进最后一次已提交的 `savedSettings.Clone()`，再走统一 `SaveSettings`。不得直接保存任一窗口持有的 `CurrentSettings`，因为设置窗口预览会临时替换运行态副本，直接写盘会让用户取消的预览值一并落盘。

| 设置键 | 含义 |
|---|---|
| `GuardBoardLeftDockEnabled` | 旧配置兼容槽；`Normalize` 固定为 `true`，设置页不再显示开关 |
| `GuardBoardLeftDockTabCenterY` | tab 中心 Y；`-1` 为共享布局器按五枚 tab 的实际缩放高度自动排队 |
| `GuardBoardAutoHideSeconds` | 手动打开后的空闲自动收起秒数，默认 30 |
| `GuardBoardTransparencyOverridePercent` | 看板及 GUARD 左缘 tab 的独立透明度覆盖；`-1` 为跟随全局 |
| `GuardBoardScaleOverridePercent` | 看板及 GUARD 左缘 tab 的独立缩放覆盖；`-1` 为跟随全局 |
| `GuardSleepEnabled` | 睡眠防护开关，重启后恢复 |
| `GuardSleepSinceUtcTicks` | 睡眠防护起始时刻（**起点**，只在过去有效） |
| `GuardDisplayMinutes` | 亮屏计时档位，取值必须落在 `GuardDisplayMinuteSteps` |
| `GuardDisplayUntilUtcTicks` | 亮屏计时截止时刻（**终点**，只在未来有效） |
| `GuardOfflineThresholdMinutes` | 断网自动睡眠阈值，取值必须落在 `GuardOfflineThresholdMinuteSteps` |
| `GuardBatteryCarePauseUntilUtcTicks` | 电池保护暂停窗口终点 |

两个档位是**阶梯**而非区间：`GuardDisplayMinuteSteps = {30, 60, 120, 300, 480}` 分、`GuardOfflineThresholdMinuteSteps = {1, 5, 10, 30}` 分，与 CodexSleepGuard 原下拉框一致。`NormalizeGuardDisplayMinutes` / `NormalizeGuardOfflineThresholdMinutes` 对越界值取最近档位而非夹到区间端点，手工改坏的 settings.ini 不会产生 UI 显示不出来的时长。

两个"终点"型 tick 过期即视为未武装，"起点"型 tick 落在未来时回退为当前时刻。窗口尺寸仍复用 `SpecBoardWidth/Height`（默认 648×400），使五个停靠板展开成同一矩形；透明度与缩放则由 `GuardBoardTransparencyOverridePercent` / `GuardBoardScaleOverridePercent` 独立控制，GUARD 左缘 tab 跟随同一组 GUARD 覆盖。Version 80 迁移会把旧设置中的 Spec Board 覆盖值一次性复制到 GUARD 槽位，未显式覆盖时继续保留 `-1` 跟随全局。

## 窗口与布局

`GuardBoardForm` 继承 `LayeredWidgetFormBase`，绘制在 `GuardBoardForm.Layout.cs`。视觉家族与 SpecBoardForm / NetworkMonitorForm.DockedLayout 一致：`AppBackground` 底色 @238、`Border` 发丝线、`S(12)/S(9.2)/S(9)/S(7.8)` 字号阶梯。所有行高经 `MeasureLineHeight` 实测，不写死像素。

### 左栏：计时环与两条轨道

外环始终承载**当前正在发生的那个守护**：有亮屏计时走计时剩余（黄），否则走睡眠防护已持续时长（绿，以 `SleepGuardGaugeHours = 12` 小时为参考扫程）。内环只在两者同时运行时出现。早期版本把外环固定绑给亮屏计时，结果在最常见的状态（只开睡眠防护）下整块最大元素是空的，读起来像坏了而不是空闲。

环下方两条同构轨道，共用 `DrawTrackRow`：

- **断网自动睡眠**：在线时标记停在 0，离线后随时长右移；进度过 0.6 转危险色。它回答的是「离睡过去还有多远」，不只是「断了没有」。
- **电池保护**：暂停期间填充条走向 24 小时后的自动恢复；行右侧内嵌暂停/恢复按钮。

电池保护轨下方是**电源请求状态块**（`DrawPowerRequestInfo`）：`电源请求` 标签后跟系统 / 执行 / 显示三枚 `DrawStateChip` 状态芯片，按 `*PowerRequestActive` 着绿（持有）或灰（未持有），把「机器现在究竟靠什么不睡」这条最关键的信息显式画出，而非让修复隐形。副行经 `NativeMethods.TryGetOnAcPower` 给出 AC 提示：未守护时「未持有电源请求」；已守护且接通电源时绿字「S0 待机感知 · 守护持续有效」；电池供电时黄字警告「待机超时后系统可能中断守护」（`ACLineStatus == 255` 未知时不猜，走中性提示）。该块无命中区，故计时环预算按 `infoHeight + infoGap` 扣除其高度、由环让位而非挤压轨道；块与电池轨间距（`infoGap`）比环与轨间距（`trackGap`）更紧，将两条电源相关行视觉归组。`SpecBoardWidth < CompactRingMinimumLogicalWidth` 的紧凑单列模式撤除整个左栏，因此不绘制该块——防睡眠两层修复仍然生效，只是不显示芯片。

### 右栏：控制卡片

两个分段（电源守护 / 程序守护）各三张卡片。卡片控件自右向左布局，文本块按剩余宽度测量——反过来做就是窄宽度下文字压到开关底下的成因。

`SpecBoardWidth < GuardBoardForm.CompactRingMinimumLogicalWidth = 460` 时进入**紧凑单列模式**：左栏整体撤除，电池保护降级为电源守护分段下的第四张卡片。功能在窄宽度下只允许重排，不允许消失。

### 交互

命中区在绘制时登记（`recordHitTargets`），`OnMouseUp` 线性匹配。档位 `+/-` 走 `StepValue`，到达阶梯两端停住而不回绕：多点一下就从 8 小时跳回 30 分会静默终结一次长守护。

`GuardBoardForm.RunSelfTest` 在两种宽度下校验全部命中区存在、面积非零且两两不重叠——控件画得出来却点不到、或两个控件共用像素（先登记者静默获胜），都是截图上看不出来的故障。

## 生命周期与互斥

`OperationForm.GuardBoard.cs` 持有窗口并转发所有对外命令。启动时即构造隐藏窗口及其固定左缘 tab；状态机住在窗口里，若窗口不存在或维护 timer 随看板收起而停掉，已持久化的守护和到期动作都会失效。

五个停靠板互斥：展开任一板收起其余四个。全部展开路径共用 `OperationForm.CollapseLeftDockBoardsExcept(LeftDockBoardKind)`，成员表由 `GetLeftDockBoardMembership` 单点维护——早期各路径手写同伴列表，`PrepareForCodexTaskOverlayShow` 因此漏掉了 GUARD，从 GUARD 梯形移到任务梯形时两板会叠在一起。互斥不是观感问题：梯形间距 40 逻辑像素而板高 400，两板大面积重叠，被盖住的那块的 `UpdateDockCollapse` 会把落在重叠区的光标读成仍悬停在自己身上，收起计时器永不启动。`RunLeftDockMutualExclusionSelfTest` 断言成员表覆盖枚举全部取值，后续新增看板漏登记会直接让 `--test` 失败。全屏隐藏、显示挂起/恢复分别经 `SetGuardBoardHiddenForFullscreen`、`PrepareGuardBoardForDisplaySuspend`、`RecoverGuardBoardAfterDisplayResume`。

维护 tick 500 ms，从窗口构造完成起持续运行，与面板是否展开无关——守护要跨夜生效。守护状态色只在看板内容中表达；常驻 tab 固定使用队列第四位的紫色角色编码，不再因有守护、电池暂停或空闲而变色。窗口可见时每 tick 无条件重绘，因为板上每个倒计时都是秒级精度。

防烧屏 salt：`GuardBoardSalt = 53`、`GuardBoardDockTabSalt = 59`。常驻梯形始终使用紫色角色编码，普通隐藏状态只降低既有填充、边框和箭头 Alpha，不改变颜色。展开的 GUARD 看板外沿使用同一紫色的共享 Radar 风格内描边。梯形与箭头处于同一分层位图和同一个窗口边界内，因此 `GuardBoardDockTabSalt` 的 Y 轴微位移同时覆盖两者；展开看板的 `PositionAtLeftDock` 使用 `ApplyRuntimeOffsetWithPinnedX` 固定 X，只保留 `GuardBoardSalt` 的 Y 轴微位移。共享绘制、定位和命中契约见 `Docs/Performance-And-Window-Runtime.md` §6.1。

## 渲染取样

`--render-guard sample --out <dir>` 产出四张 PNG：`guard-idle`（全空闲）、`guard-armed`（睡眠 2h18m + 亮屏剩余 4:32:10 + 电池暂停 8 小时）、`guard-offline`（离线 6/10 分，红条）、`guard-compact`（320 逻辑宽单列）。`--render-guard current` 按真实 settings.ini 出 `guard-current.png`。

取样状态需要「已持续数小时」的守护，`GuardRuntime.BackdateSleepGuardForRenderSample` 专供该用途；harness 在每个窗口析构前调用 `ReleaseAll`，避免一次渲染在调用进程上留下 `ES_SYSTEM_REQUIRED`。
