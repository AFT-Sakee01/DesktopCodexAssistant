# 性能采样、可见表面与运行时架构

适用版本：2.0.0.4

本文说明性能采样、隐藏宿主、headless 数据所有者、左右边缘可见表面、分层渲染、可见性、显示恢复与布局编辑的现行边界。

## 1. 当前可见拓扑

常驻运行时只有下列用户表面：

| 区域 | 数量 | 表面 |
| --- | ---: | --- |
| 右侧方块列 | 10 | CPU、MEM、DISK、NET、GPU、NPU、PWR、GUARD、Codex 额度、Claude 额度；每项是独立 `MetricTileForm` |
| 左侧停靠列 | 5 | Network、Spec Board、Codex Task、GUARD、Codex IQ 的 tab；每个 tab 控制对应 board |
| 操作 | 1 | `OperationForm` |
| 设置 | 按需 | `Win11SettingsForm`，是任务栏和 Alt+Tab 中的普通设置窗口 |

隐藏运行时对象不属于可见拓扑：

| 对象 | 角色 |
| --- | --- |
| `WidgetForm` | 隐藏消息循环、性能采样与生命周期协调宿主 |
| `CodexRadarForm` | 永久 headless 的 Codex/Claude 双 family 数据所有者 |
| `PowerThermalForm` | 永久 headless 的功耗/电池/温度数据所有者 |

`NetworkMonitorForm` 只提供 Dock board 与其 tab，不提供浮动网络表面。Clean IP 画像作为 Network board 的一部分展示。设置窗口按需显示，不参与常驻边缘排列。

## 2. 所有权与数据流

```mermaid
flowchart LR
    A["WidgetForm hidden host"] --> B["PdhSampler / histories"]
    A --> C["PowerThermalForm headless owner"]
    A --> D["CodexRadarForm headless owner"]
    A --> E["NetworkMonitorForm Dock owner"]
    A --> F["OperationForm"]
    A --> G["Win11SettingsForm on demand"]
    B --> H["MetricTileFeed"]
    C --> I["BuildStripSnapshot cache-only"]
    D --> J["Codex + Claude tile snapshots cache-only"]
    I --> H
    J --> H
    H --> K["10 MetricTileForm instances"]
    K --> L["MetricTileExpandForm on hover"]
    D --> M["Codex IQ / task / service snapshots"]
    M --> N["Codex IQ / Codex Task boards"]
    E --> O["Network reader + CleanIpConnectionReader.Shared"]
    O --> P["Network Dock board"]
```

关键边界：

- 数据 owner 决定采样、请求、缓存和单飞；可见表面只消费快照。
- snapshot builder 可以 clone、格式化和映射，但不能发起 I/O。
- `WidgetForm` 统一分发设置、全屏状态、显示挂起/恢复、前台层级和退出清理。
- board 与 tab 是一个模块的两个表面；全局布局只编辑 tab 的结构位置，不把展开 board 重复计数。

## 3. 隐藏宿主 `WidgetForm`

`WidgetForm.OnShown` 完成一次启动编排后保持自身隐藏。它保留 HWND 和 WinForms message loop，用于：

- 主 PDH tick、设置热加载与性能快照历史。
- 全屏/遮挡/手动隐藏协调。
- 显示器、会话、电源恢复与全局热键。
- 创建和释放 visible surfaces 与 headless owners。
- `ForceRefreshAllModules()`、设置预览和正式保存分发。
- 统一 Z-order、Win+D/SeelenUI 恢复和退出。

`ApplicationWindowStateTracker` 始终只保留低频前台窗口 Hook；最大化自动隐藏或全屏/最大化/遮挡可见性策略启用时才动态注册对象 Hook，并执行主采样周期的完整窗口枚举。回调只进入有界合并器，`WidgetForm` 每 125 ms 在 UI 线程批量消费并至多执行一次可见性更新；同产品辅助进程会在入队前过滤。命名停止事件由 ThreadPool 注册等待直接向宿主 HWND 投递 `WM_CLOSE`，退出不依赖可能被消息风暴饿死的 `WM_TIMER`。

隐藏宿主不创建 layered bitmap，不参与屏幕定位、hover、burn-in 或全局布局编辑，也不能被桌面宿主模式重新设为 `WS_VISIBLE`。

## 4. Headless 数据所有者

### 4.1 Radar owner

`WidgetForm.EnsureCodexRadarWindow()` 构造 `CodexRadarForm` 后调用 `StartHeadlessDataOwner()`。owner 显式创建隐藏 HWND、启动 backend scheduler，并同时维护 Codex 与 Claude 两个隔离 family。关闭时必须先 `StopHeadlessDataOwner()` 再 `Dispose()`。

可见消费者只调用：

- `BuildRadarTileSnapshot(Codex)`
- `BuildRadarTileSnapshot(Claude)`
- `BuildCodexIqBoardSnapshot()`
- `BuildServiceHealth()`
- `CodexTaskPresentation.SnapshotProvider`

以上 API 只读缓存。详细规则见 `Docs/CodexRadar-Architecture.md` 与 `Docs/Codex-ClaudeRadar-Architecture.md`。

### 4.2 Power/Thermal owner

`WidgetForm.OnShown` 构造 `PowerThermalForm` 后调用 `StartHeadlessDataOwner()`。owner 通过隐藏 HWND 接收电源广播，在单飞 worker 中采样，再由 `BuildStripSnapshot()` 把缓存投影给 `PWR` tile。关闭时调用 `StopHeadlessDataOwner()`。

owner 不显示、不定位、不渲染，也不参加 hover、burn-in 或 Z-order。详细规则见 `Docs/PowerThermal-Architecture.md`。

### 4.3 生命周期门控

headless owners 永不调用 `Show()`。全屏状态只隐藏可见表面，不停止这两个 backend；显示器关闭、会话锁定和系统挂起仍会暂停有外部成本的采样/轮询，恢复后使相关缓存到期并错峰续跑。具体刷新规则只在 `Docs/Component-Refresh-Rules.md` 维护。

## 5. 右侧 10 个指标方块

`Core/MetricTileModel.cs` 定义稳定顺序：

```text
Cpu, Memory, Disk, Network, Gpu, Npu, Power, Guard, CodexQuota, ClaudeQuota
```

每个 ID 对应一个独立 `MetricTileForm`。`WidgetForm.BuildMetricTileFeed()` 在控制 tick 中组装一次输入：

- 当前 `PerfSnapshot`。
- CPU/内存/磁盘/网络/GPU/NPU 历史缓冲。
- `PowerThermalForm.BuildStripSnapshot()` 的缓存副本。
- GUARD 状态。
- Codex 与 Claude 的 `RadarTileSnapshot`。

`PushMetricTileFeed()` 把同一个 feed 推给全部 tile；方块不自行采样。鼠标悬停时 `MetricTileExpandForm` 使用同一 feed 和相同 tile ID 展开详情，也不建立 reader 或 timer。

两级防烧屏由 `WidgetForm.UpdateBurnInProtectionTriggers()` 统一发布。一级通过 `LayeredWidgetFormBase.PresentationLuminancePercent` 把 10 个 tile 与当前展开窗按同一亮度策略处理；`WidgetForm.UpdateMetricTileBurnInPresentation()` 命中任意一个右侧 tile 或展开窗时，整组临时恢复亮度。二级由 `MetricTileForm.ResolveBurnInRingColor()` 只反转环形强调色，由 `MetricTileForm.ShouldDrawCenterText()` 抑制 tile 中心白字，并由 `MetricTileExpandForm.ShouldDrawNeutralText()` 抑制展开窗的白色/中性色文字，避免把整个位图做全局反色。

右列排列由 `RightTileButtonOrder`、启用状态、0–100 分布间距、整组 Y 偏移和目标工作区解析。分布值 0 时相邻 tile 紧贴；100 时首个 tile 贴工作区顶部、末个贴底部，其余可用空白在 9 个间隔间均匀分摊；中间值线性使用对应比例的可用空白。自动排列保持整列贴住工作区右缘；防烧屏只对整组应用共享 Y 偏移，不能让 10 个方块各自漂移而破坏列结构。

“主显示器/主工作区”设置仍是右侧 tile 的布局基线。目标显示器断开时按设置决定回退到主显示器或保留上次工作区；这些设置不依赖隐藏宿主是否可见。

## 6. 左侧 5 个停靠位

固定角色顺序为：

```text
Network, SpecBoard, CodexTask, Guard, CodexIq
```

`LeftDockLayout` 根据启用项、稳定顺序、真实 DPI 尺寸、0–100 分布间距和整组 Y 偏移计算 5 个 tab。分布值 0 时梯形按钮紧挨；100 时整列覆盖工作区完整高度；整数余数在 4 个间隔间均匀分摊，任意两个间隔最多相差 1 像素。`EdgeDockTabForm` 负责：

- 左缘绝对可达的梯形 tab。
- 悬停展开与离开/外部点击收起。
- 固定角色色、隐藏态透明度、两级防烧屏 tab 配色和 board 内描边。
- 共享 120 ms 交互 tick，不建立全局 mouse hook。
- `ApplyRuntimeOffsetWithPinnedX`：X 固定在工作区左缘，只允许整组 Y 微位移。

展开 board 的 X 统一固定为 `workArea.Left + tab.Width`，因此五块 board 水平对齐。board 之间按产品规则互斥；后打开者收起其它 board。每个 board 仍拥有自己的数据和显示 tick，tab 不复制业务 reader。

Network 是 Dock-only；其采样、PathPing、固定 Ping、Clean IP 和 board 缓存规则见 `Docs/NetworkMonitor-Architecture.md`。

## 7. Operation 与 Settings

`OperationForm` 是常驻可见表面，拥有 RadialDial、快速开关、刷新、设置入口和 GUARD 联动。动画 timer 只在按压或悬停状态尚未收敛时运行；静止交互复用 `WidgetForm` 的共享 tick。

`Win11SettingsForm` 按需创建，`ShowInTaskbar=true`。设置预览经 75 ms debounce 应用；保存写 `settings.ini`，取消或异常关闭恢复打开时 baseline。设置窗口不是 layered edge surface，不参加 burn-in，也不进入 16 项全局布局清单。

## 8. 性能模式

用户可选性能、均衡和省电；内部 `Smooth` 是“性能”的兼容枚举名。模式影响：

- 主 PDH 和普通调度检查频率。
- GPU/NPU 等昂贵计数器的独立采样频率。
- hover 动画与静止交互轮询频率。
- 网络本地枚举、连通性、PING、DNS 与公网 IP 节流。
- 功耗和温度 deadline。
- 进程优先级与 Windows Power Throttling。

所有具体间隔、单飞、冷却、网络事件和暂停恢复表只在 `Docs/Component-Refresh-Rules.md` 维护。架构文档只保留所有权和边界，避免出现第二份会漂移的数字表。

## 9. 可见性与全屏

`WidgetForm` 统一计算全屏、遮挡、自动隐藏、手动隐藏和反向恢复。规则按表面类型分开：

| 类型 | 全屏/隐藏行为 |
| --- | --- |
| 右侧 tile / expand | 隐藏可见表面；feed owner 可继续维护必要缓存 |
| 左侧 tab / board | 隐藏或收起可见表面；board 自身按模块规则停止不必要绘制 |
| Operation | 停止动画/FPS 等展示工作并隐藏 |
| Settings | 用户任务窗口，不被 edge-widget 自动隐藏规则当作布局表面 |
| headless Radar/Power owners | 不因全屏标志停止 backend；仍服从显示关闭、会话锁定和系统挂起 |
| hidden Widget host | 始终保持消息循环与协调职责 |

hover、click-through、敏感鼠标范围、延迟显现和反向隐藏只作用于有命中表面的可见 layered forms。不得把 headless owner 或 hidden host 加入交互轮询清单。

## 10. 显示挂起与恢复

显示关闭或系统显示栈恢复时，协调顺序为：

1. 标记 display suspend，停止可见表面的动画/绘制并释放 layered render resources。
2. 通知需要暂停外部采样的 owner/reader。
3. 恢复后延迟执行多轮显示器/work-area 重新枚举。
4. 重建可见表面的 bitmap、Graphics、字体缓存和命中区。
5. 按右列/左列结构重新定位，再恢复 tab、board 与 Operation。
6. 使数据缓存到期，由各 owner 的单飞规则错峰刷新。
7. 重申 TopMost 组顺序，并按设置处理 SeelenUI 与进程重启流程。

迟到的后台结果必须验证 owner 状态、generation、接口或请求 key，不能在恢复后覆盖新显示/网络状态。Radar owner 的 Stop/挂起会先失效并取消 generation，恢复只建立一个新 generation 并 prime 一轮，避免 refresh storm 或双 timer。

## 11. 分层渲染资源

可见 layered forms 统一复用：

- `NativeMethods.LayeredBitmapSurface`
- `UiFontCache`
- `DesignTokens`
- `BurnInProtection` 的像素微迁移、两级视觉状态、强调色反转和右列降亮处理

内容变化时才重建像素；仅整体透明度变化时复用现有位图提交 Alpha。尺寸变化、显示挂起和关闭必须释放 Bitmap、Graphics、字体、Region 与原生句柄。绘制失败可记录诊断并降级，但不能在 paint 路径同步访问网络、WMI、文件或外部进程。

headless owners 不拥有展示缓冲。Codex/Power 的旧 renderer 已删除，只保留抽象基类要求的空 `DrawWindowContent` 和恒 false 的 `CanRenderLayeredWindow()`；hidden `WidgetForm` 同样不提交 layered bitmap。

## 12. Burn-in、交互与 Z-order

- 右侧 10 个 tile 在自动模式使用同一个列 salt 和 Y 偏移。
- 左侧 5 个 tab 在自动模式使用同一个列 salt；X 始终钉住 work-area 左缘。
- 展开 board 可以使用自己的 named salt，但固定相同展开 X。
- Operation 使用自己的 named salt。
- `WidgetForm` hidden host 只拥有两级空闲状态，不绘制防烧屏像素；headless owners 完全不参与。
- 一级下，左侧 `EdgeDockTabForm` 静止态绘制深灰色梯形与角色色箭头，悬停只恢复当前梯形的角色色；右侧 tile/expand 使用 `BurnInProtection.LevelOneLuminancePercent = 45`，命中任意右侧窗口时整组恢复亮度。
- 二级保持一级结构，并只反转左箭头与右 tile 环形强调色；右 tile 中心白字及展开窗白色/中性色文字不绘制。角色色标签、灰色轨道、board 内容和 Operation 不做全位图反相。
- 鼠标移动在保护激活后是局部显现手势，不退出状态；点击、滚轮或键盘输入会退出并重启两级计时。显示挂起、布局编辑和关闭也归零状态。
- 夜间计划与防烧屏亮度在 `LayeredWidgetFormBase` 内相乘，任一策略都不能把另一策略已压低的像素重新提亮。
- 旧 `BurnInHiddenModeColorProtectionEnabled` 永久保留为 schema 88 退休输入；schema 89 的 `BurnInProtectionEnabled`、`BurnInLevelOneIdleSeconds` 与 `BurnInLevelTwoDelaySeconds` 是独立新设置，迁移不读取旧布尔值。

TopMost 恢复只遍历当前可见 forms，保持组内顺序，并把本程序表面放在受保护的 Codex 宠物/SeelenUI 层级策略所要求的位置。不得维护包含已删除表面的固定列表。

## 13. 全局布局编辑

`GlobalLayoutEditorForm.BuildEditableSurfaceIds()` 的规范集合恰好 16 项：

```text
Operation
LeftDockTab.Network
LeftDockTab.SpecBoard
LeftDockTab.CodexTask
LeftDockTab.Guard
LeftDockTab.CodexIq
MetricTile.Cpu
MetricTile.Memory
MetricTile.Disk
MetricTile.Network
MetricTile.Gpu
MetricTile.Npu
MetricTile.Power
MetricTile.Guard
MetricTile.CodexQuota
MetricTile.ClaudeQuota
```

进入编辑时，50% 黑色遮罩临时禁用环境隐藏，把这 16 个结构表面保持在遮罩上方。拖拽只修改位置、整列偏移或目标显示器；Enter 保存，Esc 恢复 baseline。

不得加入：

- 5 个展开 board（由各自 tab 代表）。
- `Win11SettingsForm`。
- `WidgetForm` hidden host。
- Radar/Power headless owners。
- hover expand 临时表面。

`--test-layout` 必须断言规范 ID 集合、稳定顺序、启用过滤、列偏移边界和编辑前后可见性恢复。

## 14. 设置兼容边界

- `PowerThermalIntegratedEnabled` 仅兼容读取，在设置 UI 隐藏；它不能控制采样、owner 生命周期或显示。
- 旧独立展示的尺寸、位置、透明度、缩放和变体值不能创建额外表面。
- Network 始终按 Dock 结构运行；旧浮动展示选项不能改变 topology。
- Radar 设置只控制 Codex 公共数据、Codex/Claude 官方额度、服务健康和测试，不包含 Claude 社区模型/fallback 或 DeepSeek key/余额；它不控制 owner 可见性。
- 主显示/work-area 设置继续作为右 tile 列基线；不能因为 hidden host 没有画面而删除。
- 两级防烧屏只作用于五个左 tab 与右侧 tile/expand；Operation、board、Settings、hidden host 和 headless owners 不进入配色投影。
- schema 90 保留 `LeftDockButtonGapPixels` / `RightTileButtonGapPixels` 旧键名以兼容既有 `settings.ini`，但语义改为 0–100 分布值；设置页左右两项都提供滑块与数字输入，既有 0–80 数值迁移时原样保留。

新设置若影响可见表面，必须覆盖 defaults、clone、load/save、normalize、UI、migration 和 `--test-settings-bindings`；兼容键不得重新进入设置 UI。

## 15. 命令行与验证

当前离屏渲染入口：

```text
--render-networkmonitor
--render-tilecolumn
--render-operation
--render-specboard <sample|current>
--render-specboardmanager <sample|current>
--render-guard <sample|current>
```

这些入口只渲染仍存在的可见表面或 board。所有样张数据都应由固定 fixture 或当前只读快照提供，不启动正式后台 owner。

建议验证：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-test.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-test.exe --test
.\_build\DesktopCodexAssistant-arm64-test.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-test.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-test.exe --test-display-recovery
.\_build\DesktopCodexAssistant-arm64-test.exe --test-radar-display-lifecycle
.\_build\DesktopCodexAssistant-arm64-test.exe --test-operation-panel
```

人工核对重点：右侧恰好 10 个独立 tile、左侧恰好 5 个 tab/board、Operation 正常、Settings 可从任务栏/Alt+Tab 返回、全局编辑恰好 16 项、Network 只有 Dock 展示，以及三个隐藏对象从未出现在桌面、任务栏、布局编辑器或渲染样张中。
