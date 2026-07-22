# GoalSpec：界面收敛与经典浮窗退役执行说明

- Goal：执行 `Opus48-InterfaceConvergenceRetireClassicFloaters-SPEC-v1.0.6.25-20260721-094623.md`
- 执行模型：Codex
- 完成版本：1.0.6.29
- 完成时间：2026-07-21T21:00:00+09:00（UTC 2026-07-21T12:00:00Z）
- 时区：Asia/Tokyo（UTC+09:00）
- SPEC 路径：`Docs/Technical/Opus48-InterfaceConvergenceRetireClassicFloaters-SPEC-v1.0.6.25-20260721-094623.md`
- SPEC SHA-256：`A6915CBA735F96246C86D1EBD6AB7DCB4B5EE7317AC2D3060FAA4BAB2CFCF96F`
- 执行报告：`Docs/Reports/Audits/Codex-Interface-Convergence-Audit-And-Simplification-Report-v1.0.6.29-20260721.md`
- 部署 SHA-256：`241219C96C385BA884D3F789DA1333455E5873439230594A57E717FDE605A3D3`
- 状态：complete

## 1. Goal 与完成结论

Goal 要求把 DesktopCodexAssistant 从经典独立浮窗与新磁贴/Dock 并存的双界面，收敛为四类用户界面：右侧 10 个指标磁贴及按需扩展窗、左侧 5 个 Dock tab 及按需内容板、设置界面、Operation 面板；同时保留 Radar/Power 等隐藏数据 owner，不破坏采样和缓存。

完成后的正式运行拓扑为 10 个 `MetricTileForm`、5 个 `EdgeDockTabForm`、1 个 Operation 顶层面，共 16 个常驻可见表面。设置、磁贴展开窗和 Dock 内容板按需出现；Widget、Radar、Power 与 Network owner 不再形成经典可见浮窗。1.0.6.29 ARM64 已部署并通过运行态窗口枚举。

## 2. 需求映射

| SPEC 要求 | 实现位置 | 完成证据 |
|---|---|---|
| 主窗固定磁贴列、退役经典面板 | `Core/WidgetForm.cs`、`Core/WidgetForm.TileColumn.cs`、`Core/MetricTileForm.cs` | 10 个合法 tile 恒定创建；经典绘制文件和 `--render-widget` 已移除；运行可见 10 tile |
| Radar 固定磁贴并保留数据 owner | `Core/CodexRadarForm.cs`、`Core/CodexRadarForm.RuntimeState.cs`、`Core/CodexRadarForm.TileSnapshot.cs` | headless timer/通知 HWND/缓存持续工作；可见 `Show` 路径为 0；Codex/Claude 两族磁贴自测通过 |
| Power 改为 headless owner | `Core/PowerThermalForm.cs`、`Core/PowerThermalForm.Snapshot.cs` | 隐藏状态允许采样；Power tile 快照继续更新；Stop/Dispose 幂等 |
| Network 只保留 Dock | `Core/NetworkMonitorForm.cs`、`Core/NetworkMonitorForm.Dock.cs`、`Core/NetworkMonitorForm.DockedLayout.cs` | `StartDockedOwner` 无 show-then-hide；Network tab/board 渲染通过；浮动设置和经典绘制已删 |
| 删除独立 Claude 窗 | 共享 `CodexRadarForm` family scheduler、`ClaudeRadarModels`/reader | `ClaudeRadarForm.cs` 已移除；Claude 配额仍由 Claude tile 和 Codex IQ/Network 状态消费 |
| 删除 Connection Check 窗但保留出口画像 | `Core/NetworkMonitorForm.Dock.cs`、Clean IP shared reader | `ConnectionCheckForm*.cs` 已移除；Network Dock 仍显示出口画像 |
| 设置 schema 精确迁移 | `Settings/WidgetSettings.cs` | schema 85；90 canonical + 11 alias 迁移注册表；原子重写夹具和正式设置 0 泄漏 |
| 删除旧设置 UI 与无效 Operation 开关 | `Settings/Win11SettingsForm.cs`、`Core/OperationForm.RadialDial.cs` | 26 个兼容属性不再暴露；3 个 no-op 开关移除；settings bindings/Operation 自测通过 |
| 固定五 Dock 与布局编辑范围 | `Core/EdgeDockTabForm.cs`、`Core/LeftDockLayout.cs`、`Core/GlobalLayoutEditorForm.cs` | Network/Spec/Codex Task/Guard/Codex IQ 五角色生命周期与颜色自测通过；编辑器只枚举现行结构 |
| 文档和索引同步 | `README.md`、`AGENTS.md`、`Docs/`、FEATURE/INTERFACE/Technical/CHANGELOG | Docs Gate PASS；active 索引不再推荐已删命令或退休键 |
| 可回滚部署 | 项目外 retained-build-backups 与正式入口 | 两份完整项目快照、三个正式二进制节点、原始 schema 83 设置、Git bundle；正式三文件 hash 一致 |

## 3. 最终架构流程

### 3.1 根生命周期

`Program/Main` 启动隐藏的 `WidgetForm` 协调宿主。宿主创建并维护十磁贴、五 Dock tab、Operation，以及三个数据/内容 owner：

1. `CodexRadarForm.StartHeadlessDataOwner` 创建不可见通知句柄并启动共享 family 刷新。
2. `PowerThermalForm.StartHeadlessDataOwner` 创建不可见通知句柄并启动功耗温度采样。
3. `NetworkMonitorForm.StartDockedOwner` 直接启动 Dock 内容 owner，不经历可见浮窗阶段。
4. `WidgetForm.TileColumn` 定期读取缓存快照并推送到十个独立 `MetricTileForm`。
5. 五个 `EdgeDockTabForm` 通过统一的 `LeftDockLayout` 排列，并按悬停/交互显示对应内容板。
6. 退出时先停止 owner，再 Dispose timer、SystemEvents、reader/provider 与原生通知，避免 never-shown Form 绕过 `FormClosed` 清理。

### 3.2 数据所有权

- Codex/Claude：一个 headless `CodexRadarForm` 是两族配额磁贴与 Codex IQ 的唯一快照 owner。每个 family 有独立 due 状态，仍复用 single-flight 和原刷新节奏。
- Power：一个 headless `PowerThermalForm` 采样；磁贴只读取缓存，不触发磁盘或设备查询。
- Network：`NetworkMonitorForm` 保留 reader/timer、PathPing/GFW/Cloud probe、DNS/link helper 和 `CleanIpConnectionReader.Shared`，只删除独立浮窗绘制。
- Widget：`WidgetForm` 只编排生命周期和 tile column，不承担经典面板绘制。

## 4. 文件与接口收敛

当前工作项目删除 9 个专用于已退役形态的源码文件：

- `Core/WidgetForm.DenseGrid.cs`
- `Core/WidgetForm.GuardStrip.cs`
- `Core/WidgetForm.PowerStrip.cs`
- `Core/WidgetForm.RenderSample.cs`
- `Core/ClaudeRadarForm.cs`
- `Core/ConnectionCheckForm.cs`
- `Core/ConnectionCheckForm.RenderSample.cs`
- `Core/CodexRadarForm.RenderSample.cs`
- `Core/PowerThermalForm.RenderSample.cs`

同时删除 `--render-widget`、`--render-codexradar`、`--render-clauderadar`、`--render-connectioncheck`、`--render-powerthermal` 五个命令入口。现行 tile column、Network Dock、Operation 渲染与诊断入口保留。

`FEATURE_INDEX.jsonl` 与 `INTERFACE_INDEX.jsonl` 已更新为现行入口；被移除能力保留历史/removed 状态但不再推荐不存在的命令或文件。最终索引分别为 65 和 197 个唯一 ID。

## 5. 设置、迁移和兼容

### 5.1 schema 85

`WidgetSettings.CurrentVersion` 为 85。退休注册表包含 90 个旧窗口 canonical key 与 11 个 alias，覆盖 Claude、Connection、经典 Main、Codex Radar、Power 和 Network 浮动窗的启用、位置、尺寸、透明度、缩放、显示器、工作区、render variant 和旧模式选择。

84→85 迁移在删除旧键前，优先把 `CodexRadarWidth/Height` 转为 `MetricTileExpandWidth/Height`，再回退 `ClockWidth/Height`；与输入键顺序无关。保存使用原子替换，测试验证版本、101 个名称全缺失、无 `.tmp` 和再次加载/保存稳定。

### 5.2 兼容属性

26 个没有当前可见消费者的属性继续可读写，但从 Win11 设置 UI 隐藏，包括通用 click-through/旧主窗缩放、SpecBoard 旧坐标、旧模型版本，以及 21 个 Radar 时钟/圆环/IQ/效率呈现属性。

四个旧 Dock enable 属性保留解析但 Normalize 恒真：`SpecBoardLeftDockEnabled`、`CodexTaskBoardLeftDockEnabled`、`GuardBoardLeftDockEnabled`、`CodexIqBoardLeftDockEnabled`。`PowerThermalIntegratedEnabled` 保留兼容但不再暴露，当前 Power 恒由磁贴集成。派生 `CodexModelIqBaselineMode` 继续内部隐藏。

Radar 随机健康测试设置继续保留，因为其当前消费者是 Network Dock 服务 LED，而不是已删除的独立 Radar 窗。

## 6. 日志、错误处理与边界

- headless owner 使用显式、幂等 Start/Stop/Dispose，覆盖 never-shown Form 不触发 `FormClosed` 的边界。
- display suspend/recovery 只影响采样和当前表面，不允许恢复经典 layered window。
- 设置迁移采用临时文件和原子替换；失败不应留下半写文件。
- 旧设置只做白名单迁移，不保存或输出令牌。Claude/Codex provider、SecretStore 和日志脱敏边界未改变。
- 测试夹具会故意写入 provider failure，执行报告不把这些预期测试记录误报为正式运行错误。
- Codex provider 仍有极低概率的 check-then-`BeginInvoke` 关闭竞态；现有 Dispose 防御和压力测试未复现，列为后续统一 dispatch 封装候选。

## 7. 安全与兼容性

- 没有新增网络端点、认证方式、外部端口或敏感设置。
- 数据 reader、共享缓存、probe 限速与刷新频率不因窗口退役而改变。
- schema 85 只删除已无现行消费者的持久化名称；仍可能被旧版本代码读取的非呈现数据保持兼容。
- D/E 是同一物理目录的 SUBST 别名，部署只覆盖根目录和 Release 两个不同文件，不把盘符别名当作第二份备份。
- 当前工作树在任务前已包含用户改动；执行过程没有 reset、checkout 或提交，也没有回退用户改动。

## 8. 规范偏离与解释

1. **永久 headless 标志**：实现没有复用 `hiddenForFullscreen` 表示永久隐藏，而是建立显式 headless 生命周期。原因是 fullscreen 隐藏会暂停 Power 采样，语义不等价；这是增强，不改变规范目标。
2. **保留 Main display/work-area 能力**：没有删除仍被十磁贴列定位和显示恢复消费的主显示器/工作区数据，只删除经典主窗几何键。
3. **共享 Codex/Claude scheduler**：删除独立 Claude 窗后，Claude family 由共享 Radar owner 主动调度，而不是复制一套 owner；保持 single-flight 与缓存不变量。
4. **Power 集成开关兼容**：`PowerThermalIntegratedEnabled` 没有从模型物理删除，而是隐藏并固定集成语义，避免旧文件或内部绑定中断。
5. **Dock 开关兼容**：四个历史 Dock enable 键保留解析但强制为真，五角色固定存在；设置页不再允许产生规范禁止的四板拓扑。
6. **26 个属性先隐藏后删除**：这些无现行 UI 消费者的兼容属性没有在同一版本从模型抹除，降低旧配置读取与内部序列化风险；它们不再是用户可配置界面。
7. **阶段版本合并**：1.0.6.27 与 1.0.6.28 分别作为独立源码/测试阶段完成，但没有单独正式部署；阶段 3–5 在最终 1.0.6.29 一次交付。没有虚构 .27/.28 正式二进制。
8. **旧 paint helper 延后物理删除**：EvenRow/ThreePane 与部分 Power/Radar helper 中仍混有 snapshot/共享绘制依赖。本轮先保证入口永久不可达；待共享 helper 抽离后再独立删除，避免扩大 Goal。

这些差异均服务于数据连续性、兼容性或可验证性，未保留任何规范禁止的可见经典窗口。

## 9. 验证证据

### 9.1 最终同一候选

以下命令在最终 ARM64 候选上退出 0：

- `--test`
- `--test-layout`
- `--test-operation-panel`
- `--test-radar-display-lifecycle --iterations 50`

独立复核在同一 SHA 上执行 `--test`、`--test-layout`、`--test-settings-bindings`、`--test-operation-panel`、`--test-radar-display-lifecycle --iterations 10`，全部退出 0；生命周期增量 handles `+5`、GDI `0`、USER `0`。

同源完整 Gate 还覆盖 `--test-display-recovery`、`--test-settings-open-close --iterations 50`、`--test-codex-task-monitor` 与 `--test-logger`；最近完整压力增量 handles `+20`、GDI `0`、USER `0`、hotkeys `0`。

### 9.2 渲染、静态和文档

- Network 4 PNG / 182,603 bytes；tiles 15 PNG / 175,507 bytes；Operation 13 PNG / 273,902 bytes；合计 32 PNG / 632,012 bytes，零长度 0。
- 视觉检查覆盖 tile column、Operation radial、Network Dock、Power expand 与 Codex IQ。
- 已删类/CLI/经典入口静态命中 0；headless 可见 `Show` 0；设置 UI 26 项泄漏 0；Operation no-op 泄漏 0。
- Docs Gate PASS；JSONL 可逐行解析、ID 唯一；`git diff --check` 无错误。

### 9.3 部署与运行态

- 最终 PE machine：`0xAA64`。
- 根目录、Release、候选均为 1.0.6.29、2,303,488 bytes、SHA-256 `241219C96C385BA884D3F789DA1333455E5873439230594A57E717FDE605A3D3`。
- 旧 PID 42188 正常退出，没有强制终止；新 PID 49812 从 E: 根入口启动并正常响应。
- 顶层窗口总数 45、可见 16；可见集合严格为 10 tile + 5 Dock tab + Operation。
- 5 秒启动采样最大可见数 16，禁止的经典/Network/Radar/Power 瞬态为 0。
- 正式设置 `Version=85`，101 个退休名称泄漏 0，`MetricTileExpandWidth/Height=522/120`，四个 Dock 兼容开关和 Power 集成开关为 True，无 `.tmp`。

## 10. 备份与恢复

主要完整快照：

- `D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant-retained-build-backups\project-snapshots\20260721-171920-pre-redundant-simplification-v1.0.6.23`
- `D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant-retained-build-backups\project-snapshots\20260721-190536-pre-interface-convergence-v1.0.6.24`

正式回滚节点：

- `...\formal-backups\20260721-192800-pre-interface-convergence-phase1-v1.0.6.24`
- `...\formal-backups\20260721-194030-pre-interface-convergence-phase2-v1.0.6.25`
- `...\formal-backups\20260721-202750-pre-interface-convergence-final-v1.0.6.26`

第一份完整快照还包含 schema 83 原始运行设置和 `repository-all.bundle`。1.0.6.26 节点中的 `settings.pre-schema85.ini` 名称虽如此，内容已是 schema 85；恢复迁移前设置必须使用第一份快照的 `runtime-state/settings.ini`。

## 11. 交付物

- 正式 ARM64：`DesktopCodexAssistant.exe`
- Release：`Release/DesktopCodexAssistant-arm64.exe`
- 执行报告：`Docs/Reports/Audits/Codex-Interface-Convergence-Audit-And-Simplification-Report-v1.0.6.29-20260721.md`
- 本 GoalSpec：`Docs/Technical/Codex-GoalSpec-InterfaceConvergenceRetireClassicFloaters-v1.0.6.29-20260721-210000.md`
- 功能索引：`Docs/Indexes/FEATURE_INDEX.jsonl`
- 接口索引：`Docs/Interfaces/INTERFACE_INDEX.jsonl`
- 技术索引：`Docs/Technical/INDEX.jsonl`
- 维护日志：`Docs/Maintenance/CHANGELOG.jsonl`

## 12. 完成判定

SPEC 的用户可见目标、数据连续性红线、设置迁移、回滚、测试、文档和正式部署均已满足。最终常驻可见面固定为 16；经典浮窗不再可达或闪现；当前磁贴与 Dock 所需的数据 owner/reader 均保留并通过测试。Goal 可标记为 complete。
