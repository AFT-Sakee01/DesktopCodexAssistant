# DesktopCodexAssistant 当前窗口依赖审计与界面简化报告

- 报告版本：1.0.6.29
- 审计与实施模型：Codex
- 报告时间：2026-07-21T21:00:00+09:00（UTC 2026-07-21T12:00:00Z）
- 项目：`D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant`
- 执行规范：`Docs/Technical/Opus48-InterfaceConvergenceRetireClassicFloaters-SPEC-v1.0.6.25-20260721-094623.md`
- 规范 SHA-256：`A6915CBA735F96246C86D1EBD6AB7DCB4B5EE7317AC2D3060FAA4BAB2CFCF96F`
- 审计范围：当前可见窗口拓扑、窗口生命周期、设置页、设置迁移、命令行入口、绘制代码、共享数据源、文档与索引、ARM64 正式部署

## 1. 执行摘要

本次先完成项目级快照、正式二进制节点和运行设置备份，再按当前实际使用界面收敛冗余元素。最终结论如下：

1. 当前桌面常驻可见面已固定为 **16 个**：右侧 10 个指标磁贴、左侧 5 个 Dock tab、左下 1 个 Operation 面板。
2. 设置窗口只在用户打开时出现；磁贴扩展窗和五个 Dock 内容板只在交互时出现，不计入常驻 16 面。
3. `WidgetForm` 改为隐藏协调宿主；`CodexRadarForm` 与 `PowerThermalForm` 改为永久不可见但继续采样的 headless data owner，避免为简化界面而切断磁贴数据。
4. 主窗经典面板、Radar 经典独立窗、Power 独立窗、Connection Check 独立窗、Claude 独立窗和 Network 浮动窗均不再可达。
5. 工作项目中移除 9 个仅服务于已退役窗口/离屏样图的源码文件；它们全部存在于项目外快照中，可恢复，没有清空备份或执行不可恢复删除。
6. 删除 5 个已退役离屏渲染命令；保留当前磁贴、Dock、Operation 和测试所需命令。
7. 设置 schema 从 84 升至 **85**，迁移并清除 **90 个旧窗口正式键 + 11 个历史别名，共 101 个名称**；正式设置文件复核为 0 泄漏、无遗留 `.tmp`。
8. 设置页另外隐藏 26 个仍需兼容读写、但当前可见界面没有用户可调消费者的属性；四个 Dock 兼容开关固定为真，集成功耗兼容开关保留但不再暴露。
9. ARM64 正式版本 1.0.6.29 已部署并运行。根目录、Release 和最终候选三者字节一致，SHA-256 均为 `241219C96C385BA884D3F789DA1333455E5873439230594A57E717FDE605A3D3`。
10. 构建、自测、布局、设置绑定、Operation、显示恢复、生命周期压力、设置开关压力、渲染、静态扫描、文档 Gate、JSONL 解析及运行窗口枚举均通过。

因此，当前项目已从“同一数据同时维护经典浮窗与新磁贴/Dock 两套界面”收敛为单一现行界面。未删除仍被当前界面消费的数据 reader、缓存、采样器或共享模型。

## 2. 审计判定标准

本报告没有把“启动时不可见”直接等同于“无用”。文件或设置只有同时满足以下条件才进入退役候选：

- 当前 10 个磁贴、5 个 Dock、Operation、设置或按需扩展窗没有直接消费者；
- 不承担 headless 数据采样、共享缓存、生命周期协调、显示恢复或测试基础设施职责；
- 全仓引用、设置绑定、命令分发和文档索引能够与退役结论相互印证；
- 移除后相关 ARM64 测试、离屏渲染和正式运行窗口核验仍通过；
- 变更前已有可验证、可定位的外部备份。

反之，下列“不可见但仍有用”的对象明确保留：

| 对象 | 当前职责 | 结论 |
|---|---|---|
| `WidgetForm` | 应用根生命周期、十磁贴编排、共享子组件协调 | 保留，永久隐藏协调宿主 |
| `CodexRadarForm` | Codex/Claude 配额磁贴与 Codex IQ Dock 的唯一数据 owner | 保留，headless；禁止可见 layered window |
| `PowerThermalForm` | Power 磁贴采样与快照 owner | 保留，headless；禁止可见 layered window |
| `NetworkMonitorForm` | Network Dock 内容板、reader/timer 和共享出口画像生命周期 | 保留 Dock-only，删除浮动形态 |
| `CleanIpConnectionReader.Shared` | Network Dock 的出口画像数据 | 保留 |
| `NetworkMonitorReader`、PathPing/GFW/Cloud probe | Network Dock 的实时网络数据 | 保留 |
| `ClaudeRadarReader`、共享 scheduler、各性能采样器 | 右侧磁贴和 Codex IQ 看板的数据层 | 保留 |
| `OpacityPolicyProbeForm` 等测试探针 | 自动验证基础设施，不是产品功能窗 | 保留 |

## 3. 备份与恢复证据

### 3.1 项目级快照

| 节点 | 路径 | 证据 |
|---|---|---|
| 简化前完整快照 | `D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant-retained-build-backups\project-snapshots\20260721-171920-pre-redundant-simplification-v1.0.6.23` | 8,078 个文件、946,947,766 bytes；源/目标复核 missing=0、extra=0、SHA differences=0；含完整 Git bundle |
| 界面收敛前快照 | `D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant-retained-build-backups\project-snapshots\20260721-190536-pre-interface-convergence-v1.0.6.24` | 2,183 个文件、196,234,793 bytes；SHA differences=0 |
| Git 历史保险 | 第一份快照内 `repository-all.bundle` | `git bundle verify` 完整通过，可独立恢复仓库引用 |
| 原始运行设置 | 第一份快照内 `runtime-state\settings.ini` | schema 83，SHA-256 `F18967BD8F5DE37C2232702306E8DD8C6C5EB35F9FB0778E4626AA815DF58F0D` |

### 3.2 正式部署回滚节点

| 节点 | 路径 | 保存内容 |
|---|---|---|
| 1.0.6.24 | `...\formal-backups\20260721-192800-pre-interface-convergence-phase1-v1.0.6.24` | 根目录与 Release 正式 ARM64；2,508,288 bytes；SHA-256 `4284282CD0E4B6991E82C2729F086E4E97BBC1C6CA1B0B8AA2D0CA7222B2D6ED` |
| 1.0.6.25 | `...\formal-backups\20260721-194030-pre-interface-convergence-phase2-v1.0.6.25` | 根目录与 Release 正式 ARM64；2,469,376 bytes；SHA-256 `002F6C9AA8EE8E1D1FA47530A83F6588E07E813DDC6F042C4B53C3DCE548429E` |
| 1.0.6.26 | `...\formal-backups\20260721-202750-pre-interface-convergence-final-v1.0.6.26` | 根目录与 Release 正式 ARM64；2,472,960 bytes；SHA-256 `4120761B723E144E9949D1DAEAEDF800F32FCA966FC517B0955B6FBF39ED3EE0` |

注意：1.0.6.26 回滚目录中的 `settings.pre-schema85.ini` 是在最终预检之后复制，文件内容已经是 schema 85，SHA-256 为 `3D02FAEA5C2D7D6D18BD4552C0D54E7839B536F2BD4A3C911DBC8543075B3BF2`。真正的迁移前 schema 83 设置在第一份项目快照的 `runtime-state\settings.ini` 中。本报告明确记录这一点，避免仅凭文件名误判。

### 3.3 盘符说明

本机 `E:\` 是 `D:\E_Drive_Files` 的 SUBST 映射，即 `E:\: => D:\E_Drive_Files`。报告中的 D/E 路径指向同一物理项目，不代表存在两套互相独立的数据副本。项目外 retained-build-backups 仍与工作目录分离，未被清理流程触碰。

## 4. 最终可见窗口拓扑

### 4.1 常驻 16 面

| 区域 | 数量 | 当前表面 |
|---|---:|---|
| 右侧指标列 | 10 | CPU、Memory、Disk、Network、GPU、NPU、Power、Guard、Codex Quota、Claude Quota 磁贴 |
| 左侧 Dock tab | 5 | Network、Spec、Codex Task、Guard、Codex IQ |
| 左下 | 1 | Operation 面板 |
| 合计 | **16** | 固定常驻可见拓扑 |

按需界面仍保留：设置主窗及其编辑子窗、磁贴展开窗、五个 Dock 内容板、Operation 的交互子件。它们只在用户操作时显示，不会在启动阶段形成额外经典浮窗。

### 4.2 正式运行枚举

- 正式进程：PID `49812`，`Responding=True`，从 `E:\Codexproject\desktopdata\DesktopCodexAssistant\DesktopCodexAssistant.exe` 启动。
- 顶层 HWND 总数为 45，其中可见 16；不可见 HWND 包含 headless owner、消息/协调窗和框架内部句柄，不属于用户界面泄漏。
- 可见项严格为 10 个 `MetricTile*`、5 个 `*DockTab` 和 1 个 49×49 Operation 面板。
- 启动前 5 秒采样：首个可见面约 4,756 ms 出现；约 5,847 ms 达到 16；采样最大值 16；禁止的经典窗口瞬态出现次数为 0。
- 初始化过程中观察到的临时空白 1×1 HWND 不可见，不构成界面闪现。

## 5. 已简化的文件与入口

### 5.1 从工作项目移除的 9 个源码文件

| 文件 | 原职责 | 退役原因 | 恢复来源 |
|---|---|---|---|
| `Core/WidgetForm.DenseGrid.cs` | 经典主面板密集网格 | 当前主界面固定十磁贴列，无可见消费者 | 两份项目快照 |
| `Core/WidgetForm.GuardStrip.cs` | 经典主面板 Guard strip | Guard 已由磁贴与 Dock 承载 | 两份项目快照 |
| `Core/WidgetForm.PowerStrip.cs` | 经典主面板 Power strip | Power 已由磁贴承载 | 两份项目快照 |
| `Core/WidgetForm.RenderSample.cs` | 经典主面板离屏样图 | 对应呈现与 CLI 同时退役 | 两份项目快照 |
| `Core/ClaudeRadarForm.cs` | 独立 Claude 浮窗 | Claude 数据由共享 headless Radar owner 提供，显示由 Claude 磁贴承担 | 两份项目快照 |
| `Core/ConnectionCheckForm.cs` | Connection Check 独立浮窗 | 出口画像已由 Network Dock 独占复用 | 两份项目快照 |
| `Core/ConnectionCheckForm.RenderSample.cs` | Connection Check 样图 | 对应窗口和 CLI 已退役 | 两份项目快照 |
| `Core/CodexRadarForm.RenderSample.cs` | 经典 Radar 样图 | Radar 只保留 headless owner 与磁贴/Dock 消费 | 两份项目快照 |
| `Core/PowerThermalForm.RenderSample.cs` | Power 独立窗样图 | Power 只保留 headless owner 与磁贴消费 | 两份项目快照 |

这些文件只从当前工作树移除；备份副本没有删除，Git 工作区也没有被 reset 或清空。因此它们是“当前实现中删除、可从备份恢复”，不是物理不可恢复删除。

### 5.2 删除的 5 个旧命令行入口

- `--render-widget`
- `--render-codexradar`
- `--render-clauderadar`
- `--render-connectioncheck`
- `--render-powerthermal`

当前仍有价值的 `--render-tilecolumn`、`--render-networkmonitor`、`--render-operation-panel` 及各自测命令继续保留。

### 5.3 生命周期收敛

- `WidgetForm` 不再绘制经典内容，也不会作为用户可见 layered window 恢复；只负责根生命周期和当前表面编排。
- `CodexRadarForm` 和 `PowerThermalForm` 不再调用可见 `Show` 路径；通过显式 Start/Stop headless 生命周期启动 timer、通知 HWND、缓存与采样，并在 Dispose 中幂等释放。
- `CodexRadarForm` 同时调度 Codex 与 Claude family，避免删除独立 Claude 窗后 Claude 数据停止刷新；磁贴模型名解析改为按 family 读取缓存。
- `NetworkMonitorForm` 通过 `StartDockedOwner` 直接进入隐藏 owner 状态，不再使用“先显示再隐藏”，因此启动时没有 Network 经典窗闪现。
- 全局布局编辑器只枚举当前结构上存在的 10 个磁贴和 5 个 Dock tab，不会为了编辑位置临时复活经典窗。

## 6. 设置审计与 schema 85 清理

### 6.1 101 个从持久化文件彻底退休的名称

迁移注册表由 90 个 canonical key 和 11 个历史 alias 组成。读入旧文件时先完成必要的数据迁移，再在一次原子重写中删除这些名称，避免枚举顺序影响结果。

| 分组 | 数量 | 已退休 canonical key |
|---|---:|---|
| Claude 独立窗 | 17 | `ClaudeRadarEnabled`, `ClaudeRadarTransparencyPercent`, `ClaudeRadarTransparencyOverridePercent`, `ClaudeRadarScaleOverridePercent`, `ClaudeRadarServiceProbeToken`, `ClaudeRadarWidth`, `ClaudeRadarHeight`, `ClaudeRadarLeftX`, `ClaudeRadarBottomY`, `ClaudeRadarDisplayDeviceName`, `ClaudeRadarRandomTestEnabled`, `ClaudeRadarRandomTestAutoRefresh`, `ClaudeRadarRandomTestRefreshToken`, `ClaudeRadarLayoutWorkAreaLeft`, `ClaudeRadarLayoutWorkAreaTop`, `ClaudeRadarLayoutWorkAreaWidth`, `ClaudeRadarLayoutWorkAreaHeight` |
| Connection Check 独立窗 | 14 | `ConnectionCheckWidth`, `ConnectionCheckHeight`, `ConnectionCheckLeftX`, `ConnectionCheckBottomY`, `ConnectionCheckTransparencyPercent`, `ConnectionCheckBorderTransparencyPercent`, `ConnectionCheckTransparencyOverridePercent`, `ConnectionCheckScaleOverridePercent`, `ConnectionCheckDisplayDeviceName`, `ConnectionCheckLayoutWorkAreaLeft`, `ConnectionCheckLayoutWorkAreaTop`, `ConnectionCheckLayoutWorkAreaWidth`, `ConnectionCheckLayoutWorkAreaHeight`, `ConnectionCheckRenderVariant` |
| 经典主窗 | 17 | `Width`, `Height`, `LeftX`, `BottomY`, `BackgroundTransparencyPercent`, `MainWidgetRenderVariant`, `MainWidgetPresentationMode`, `RadarPresentationMode`, `MetricOrder`, `ShowCpu`, `ShowMemory`, `ShowDisk`, `ShowNetwork`, `ShowGpu`, `ShowNpu`, `CpuCoreWarningPercent`, `CpuCoreCriticalPercent` |
| Codex Radar 经典窗 | 14 | `CodexRadarEnabled`, `CodexRadarWidth`, `CodexRadarHeight`, `CodexRadarLeftX`, `CodexRadarBottomY`, `CodexRadarTransparencyPercent`, `CodexRadarTransparencyOverridePercent`, `CodexRadarScaleOverridePercent`, `CodexRadarDisplayDeviceName`, `CodexRadarLayoutWorkAreaLeft`, `CodexRadarLayoutWorkAreaTop`, `CodexRadarLayoutWorkAreaWidth`, `CodexRadarLayoutWorkAreaHeight`, `CodexRadarRenderVariant` |
| Power 独立窗 | 16 | `PowerThermalWidth`, `PowerThermalHeight`, `PowerThermalLeftX`, `PowerThermalBottomY`, `PowerThermalTransparencyPercent`, `PowerThermalTransparencyOverridePercent`, `PowerThermalScaleOverridePercent`, `PowerThermalDisplayDeviceName`, `PowerThermalLayoutWorkAreaLeft`, `PowerThermalLayoutWorkAreaTop`, `PowerThermalLayoutWorkAreaWidth`, `PowerThermalLayoutWorkAreaHeight`, `PowerThermalRenderVariant`, `PowerThermalAutoSizeEnabled`, `PowerThermalAutoDirection`, `PowerThermalVisibleAlertCount` |
| Network 浮动窗 | 12 | `NetworkMonitorWidth`, `NetworkMonitorHeight`, `NetworkMonitorLeftX`, `NetworkMonitorBottomY`, `NetworkMonitorTransparencyPercent`, `NetworkMonitorDisplayDeviceName`, `NetworkMonitorLayoutWorkAreaLeft`, `NetworkMonitorLayoutWorkAreaTop`, `NetworkMonitorLayoutWorkAreaWidth`, `NetworkMonitorLayoutWorkAreaHeight`, `NetworkMonitorRenderVariant`, `NetworkMonitorLeftDockEnabled` |

11 个兼容别名：

`BackgroundTransparency`, `ClockWidth`, `ClockHeight`, `ClockLeftX`, `ClockBottomY`, `ClockTransparencyPercent`, `PowerThermalBackgroundTransparencyPercent`, `PowerThermalAutoSizeDirection`, `PowerThermalVisibleAlerts`, `NetworkMonitorBackgroundTransparencyPercent`, `ConnectionCheckBackgroundTransparencyPercent`。

### 6.2 数据迁移与新键

- schema 84 到 85 的迁移会优先把旧 `CodexRadarWidth/Height` 映射到现行 `MetricTileExpandWidth/Height`；只有前者不存在时才回退到历史 `ClockWidth/Height`。
- 迁移顺序与 INI 中键的排列无关；测试夹具覆盖“旧键先出现/后出现”和别名组合。
- 新扩展窗默认尺寸为 `MetricTileExpandWidth=522`、`MetricTileExpandHeight=120`。
- 保存采用临时文件、原子替换和清理语义；正式文件 `Version=85`，101 个退休名称全部不存在，旁路 `.tmp` 不存在。
- 正式设置路径：`C:\Users\GengH\AppData\Local\DesktopCodexAssistant\settings.ini`；当前 SHA-256 为 `3D02FAEA5C2D7D6D18BD4552C0D54E7839B536F2BD4A3C911DBC8543075B3BF2`。

### 6.3 从设置页隐藏、但暂保留兼容读写的 26 个属性

这些属性没有当前可见界面的有效用户可调消费者，但为避免一次性破坏旧代码/旧配置读取，暂不从模型删除，只从设置 UI 和现行索引的“可编辑项”中移除：

1. `ClickThroughMode`
2. `MainWidgetScaleOverridePercent`
3. `SpecBoardLeftX`
4. `SpecBoardBottomY`
5. `CodexRadarModelVersion`
6. `RadarClockTimeDisplayMode`
7. `CodexRadarSpeedWindowCountdownEnabled`
8. `CodexRadarQuotaResetRainbowEnabled`
9. `DisplayTimeZoneMode`
10. `DisplayTimeZoneId`
11. `CodexModelIqTestEnabled`
12. `CodexModelIqTestPassed`
13. `CodexModelIqBaselineAutoEnabled`
14. `CodexModelIqBaselinePassed`
15. `CodexModelIqBaselineValidTasks`
16. `CodexModelEfficiencyTestEnabled`
17. `CodexModelTokenEfficiencyTestPercent`
18. `CodexModelTimeEfficiencyTestPercent`
19. `CodexModelTokenEfficiencyBaselineMode`
20. `CodexModelTokenEfficiencyBaselinePassed`
21. `CodexModelTokenEfficiencyBaselineTokens`
22. `CodexModelTimeEfficiencyBaselineMode`
23. `CodexModelTimeEfficiencyBaselinePassed`
24. `CodexModelTimeEfficiencyBaselineSeconds`
25. `CodexModelTokenEfficiencyLowThresholdPercent`
26. `CodexModelTimeEfficiencyLowThresholdPercent`

Operation 径向菜单中与 `CodexModelIqTestEnabled`、`CodexModelIqBaselineAutoEnabled`、`CodexModelEfficiencyTestEnabled` 对应的三个无效开关也已删除，避免显示可操作但不改变当前表面的控件。

### 6.4 固定兼容开关

- `SpecBoardLeftDockEnabled`
- `CodexTaskBoardLeftDockEnabled`
- `GuardBoardLeftDockEnabled`
- `CodexIqBoardLeftDockEnabled`

上述四键为了旧配置兼容仍可解析，但 Normalize 恒定为 `True`，设置页不再提供关闭入口；连同固定的 Network Dock，五个角色始终存在。

`PowerThermalIntegratedEnabled` 同样保留兼容读取并固定现行集成语义，不再出现在设置 UI。派生兼容属性 `CodexModelIqBaselineMode` 继续保持内部隐藏。Radar 的随机健康测试设置没有删除，因为它仍驱动 Network Dock 的服务状态 LED；只修正文案，明确其当前消费者。

## 7. 实施阶段与版本

| 版本 | 阶段 | 结果 |
|---|---|---|
| 1.0.6.24 | 收敛前基线 | 项目快照与正式二进制回滚源 |
| 1.0.6.25 | 阶段 1 | 主窗固定十磁贴；Network 固定 Dock-only；经典主绘制退役并正式部署 |
| 1.0.6.26 | 阶段 2 | Radar/Power 改为 headless data owner 并正式部署 |
| 1.0.6.27 | 阶段 3 | 删除独立 Claude/Connection 类与旧离屏 CLI；本地验证节点，未单独正式部署 |
| 1.0.6.28 | 阶段 4 | schema 85、90+11 精确迁移；本地验证节点，未单独正式部署 |
| 1.0.6.29 | 阶段 5/最终 | 设置 UI、Operation、索引、活文档、五 Dock 生命周期与固定 16 面收敛；正式部署 |

1.0.6.27 与 1.0.6.28 是可追溯的源码/测试阶段，并未制作或宣称为正式部署二进制；它们在 1.0.6.29 中统一交付。

## 8. 验证证据

### 8.1 最终候选测试

最终同一 SHA 候选通过：

- `--test`
- `--test-layout`
- `--test-operation-panel`
- `--test-radar-display-lifecycle --iterations 50`

独立复核又对同一最终候选执行：

- `--test`
- `--test-layout`
- `--test-settings-bindings`
- `--test-operation-panel`
- `--test-radar-display-lifecycle --iterations 10`

均退出 0；独立生命周期复核为 handles delta `+5`、GDI delta `0`、USER delta `0`。

最终测试覆盖补丁之前、与最终运行逻辑和设置行为同源的完整 Gate 还通过：

- `--test-display-recovery`
- `--test-settings-open-close --iterations 50`
- `--test-codex-task-monitor`
- `--test-logger`

最近一次完整压力证据为 handles delta `+20`、GDI delta `0`、USER delta `0`、hotkeys delta `0`。最后的差异只增加五角色 EdgeDock 自测覆盖，不改变运行时或设置语义；随后最终候选的核心/布局/Operation/50 轮生命周期及独立复核再次通过。

### 8.2 离屏渲染与视觉检查

| 组 | PNG 数 | 总大小 | 结果 |
|---|---:|---:|---|
| Network Dock | 4 | 182,603 bytes | 非空，现行 Dock/docked/narrow/discovery 布局通过 |
| 十磁贴与扩展窗 | 15 | 175,507 bytes | 非空，tile column 与关键展开窗通过 |
| Operation | 13 | 273,902 bytes | 非空，径向面板通过 |
| 合计 | **32** | **632,012 bytes** | 0 个零长度文件 |

人工检查覆盖右侧磁贴列、Operation 径向面板、Network Dock，并在前序同源候选检查 Power 展开窗和 Codex IQ Dock；未见缺项、重叠或经典表面复活。

### 8.3 静态与文档检查

- C# 中 `ClaudeRadarForm`、`ConnectionCheckForm`、五个已删除 CLI 和 ClassicPanel/ClassicWindow 命中为 0。
- Radar/Power headless owner 的可见 `Show` 调用为 0。
- 26 个兼容属性在设置 UI 中泄漏为 0；Operation 三个 no-op 开关泄漏为 0。
- 9 个退役文件在当前工作项目中均不存在。
- `FEATURE_INDEX.jsonl`：65 个唯一 ID；`INTERFACE_INDEX.jsonl`：197 个唯一 ID；active 条目不再引用退休键或已删除命令。
- `python .\Docs\validate_docs.py`：`RESULT: PASS`。240 条提示均来自保留的历史快照措辞，不是现行文档失败。
- `git diff --check`：无错误；仅有既有换行风格提示。

### 8.4 正式部署一致性

| 文件 | 版本 | 长度 | SHA-256 |
|---|---|---:|---|
| `DesktopCodexAssistant.exe` | 1.0.6.29 | 2,303,488 | `241219C96C385BA884D3F789DA1333455E5873439230594A57E717FDE605A3D3` |
| `Release/DesktopCodexAssistant-arm64.exe` | 1.0.6.29 | 2,303,488 | 同上 |
| `_build/interface-convergence-final/DesktopCodexAssistant-arm64.exe` | 1.0.6.29 | 2,303,488 | 同上 |

PE machine 为 `0xAA64`（ARM64）。旧 PID `42188` 通过应用 `--stop` 正常退出，约 1.66 秒后确认进程消失，没有强制终止；新正式 PID 为 `49812`。

测试夹具会故意触发 provider failure，因此错误日志中存在对应测试记录；它们不能被表述为“错误日志为空”。最终正常模式启动后未观察到新增运行错误。

## 9. 当前仍保留的非现行绘制代码

以下内容经审计确认不再从当前可见路径调用，但本轮没有强行删除：

- `CodexRadarForm.EvenRow.cs` 及部分旧 Radar paint/helper；
- `PowerThermalForm` 中与旧独立窗绘制耦合的 helper；
- ThreePane/EvenRow 等历史表现枚举与兼容模型；
- 被标记 superseded 的 Fable5 frontend/data 和 Claude EvenRow 文档快照。

保留原因不是它们仍应显示，而是其中夹杂 snapshot 生成、缓存格式或共享绘制 helper。一次性删除会把“界面简化”扩大为数据模型重构。现行入口已静态不可达、设置页不可选、运行枚举不可见，因此暂留不会恢复旧窗口。建议将其作为后续独立任务：先把仍被磁贴/自测复用的 helper 抽出到中性模块，再删除死 paint 分支。

## 10. 残余风险与后续建议

| 风险 | 等级 | 说明与建议 |
|---|---|---|
| Codex provider 异步 check-then-`BeginInvoke` 关闭竞态 | 低 | 极窄窗口内可能在句柄检查后进入关闭；现有 Dispose/异常防御与压力测试未复现。后续可统一封装安全 UI dispatch。 |
| 无 `Version=` 的非常旧设置文件 | 低 | 不自动强制 canonical rewrite，以免误判第三方/手工文件；若导入此类文件，应先复制备份并显式迁移。 |
| 旧 paint/helper 仍编译 | 低 | 当前路径永久不可达，不是可见冗余；后续抽取共享 snapshot 后再做第二轮删除。 |
| 历史日志和快照仍出现旧窗口名称 | 信息 | 为可追溯性有意保留；现行文档顶部和索引已标记 superseded/removed，不应批量改写历史。 |
| 工作树包含本任务前已有改动 | 信息 | 本次未 reset、checkout 或提交，不声称所有 dirty 状态均由本任务产生；恢复时应新建副本，不要覆盖当前工作树。 |

## 11. 恢复建议

如需恢复，本轮最安全的顺序是：

1. 先正常停止当前正式实例，并确认 PID 消失。
2. 不覆盖当前工作目录；优先把完整项目快照复制到新的同级目录，在副本中核验和构建。
3. 若只回滚程序，使用对应 formal-backups 中同版本的 root/release 二进制，并在覆盖前再次核对 SHA-256。
4. 若要复原迁移前设置，使用第一份项目快照的 `runtime-state\settings.ini`（schema 83），不要误用 1.0.6.26 节点里名称为 `settings.pre-schema85.ini`、实际已是 schema 85 的文件。
5. 恢复单个退役源码文件时，也必须同时恢复其调用入口和对应测试；仅复制一个 partial class 文件可能导致编译失败或重新产生不可达代码。

## 12. 最终判定

本次审计认定的“目前显示窗口用不到的元素”已在有完整备份的前提下完成简化：9 个专属源码文件、5 个旧 CLI、101 个退休设置名称、26 个无现行消费者的设置 UI 项和 3 个 Operation 无效开关均已处理。当前仍不可见的 owner/reader 均有明确数据消费者，不能视为垃圾文件。

最终版本 1.0.6.29 达到规范目标：**只保留 10 磁贴 + 5 Dock + Operation 的 16 个常驻可见面，设置与扩展界面按需打开，无经典浮窗或启动闪现；数据采样、网络画像、Codex/Claude 配额、Power 和 Codex IQ 均继续工作。**
