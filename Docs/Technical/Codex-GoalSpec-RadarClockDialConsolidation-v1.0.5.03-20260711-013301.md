# GoalSpec: Radar Clock Dial Consolidation

- Goal：执行 `Codex-RadarClockDialConsolidation-SPEC-v1.0.5.01-20260710-235413.md`，把共享 Codex/Claude Radar 与独立 Claude Radar 的普通 IQ 时钟收敛到单一状态机、几何和绘制模块，并完成 ARM64 验收与正式部署。
- 实现版本：`1.0.5.03`
- 执行模型：Codex
- 完成时间 UTC：`2026-07-10T16:33:01.4022819Z`
- 完成时间本地：`2026-07-11T01:33:01.4022819+09:00`
- 时区：`Asia/Tokyo (+09:00)`
- Spec 路径：`Docs/Technical/Codex-RadarClockDialConsolidation-SPEC-v1.0.5.01-20260710-235413.md`
- Spec SHA256：`3F2EB7A6872B2722F070F36B361C5F3EF414DC8042EF763FF1FC2C47266F76A0`

## 需求映射

| Spec 要求 | 实现与证据 |
| --- | --- |
| R1 互斥主状态 | `RadarClockDialPhase` 与 `RadarClockDial.ComputePhase` 单次输出 NoData、CurrentCycle、WaitingCycle 或 MissedCycle。 |
| R2 正交叠加项 | `ComputeState` 统一处理请求中 alpha 104、第二次晚测徽标、刷新点和当前时间点。 |
| R3 中心三行文字 | `ComputeState` 统一格式化日期、UTC/NOW/LAST/REF 时间与模式标签；各窗继续使用自己的 fitted-text 委托。 |
| R4 周期几何 | `GetCycleBoundaryLocal`、`GetMarkerAngle` 与 `NormalizeSweep` 统一 12 h/24 h 边界、角度和扫角。 |
| R5 自动切换耦合 | `ApplyRadarClockAutoSwitchIfNeeded` 与 `ApplyClaudeRadarClockAutoSwitchIfNeeded` 都调用 `RadarClockDial.GetCycleBoundaryLocal`。 |
| V2 | 普通 IQ 时钟顶部边界刻度改为 `DesignTokens.White(170)`；速蹬倒计时继续使用天蓝色。 |
| V3 | MissedCycle 底环改为 `Danger` alpha 90，当前周期弧保留 `Danger` alpha 245。 |

## 实现范围与架构

新增 `Core/RadarClockDial.cs`，包含输入、互斥 Phase、每帧状态快照、纯状态计算、共享几何原语、GDI+ 绘制与 12 h/24 h 真值表自测。`CodexRadarForm.DrawEvenRowBatchDial` 与 `ClaudeRadarForm.DrawClaudeEvenRowBatchDial` 只提取各自快照字段、周期时长、请求状态、字体和文字拟合回调，然后调用 `ComputeState` 与 `Draw`。

Codex 速蹬结束倒计时仍是共享窗专属覆盖层，状态和布局未迁移，仅复用 `RadarClockDial.DrawBoundaryTick`。旧的两窗颜色判定、等待/过期判定、边界、刷新点角度、扫角、点/刻度绘制、日期格式化、批次小时解析和 Claude 镜像自测均已删除。

## 关键模块与复用项

- `Core/RadarClockDial.cs`：`internal_api.radar_clock_dial`，共享状态机、几何、绘制和自测。
- `Core/CodexRadarForm.EvenRow.cs`：12 h Codex / 24 h 共享 Claude 输入组装；速蹬覆盖层保持独立。
- `Core/ClaudeRadarForm.cs`：独立 Claude 24 h 输入组装；自动切换复用共享边界。
- `Core/RadarSoftwareModeController.cs`：核心 `--test` 链调用共享自测。
- 复用现有 `DesignTokens`、`UiFontCache`、各窗 fitted-text 路径、`ClaudeRadarClockAutoSwitchSelector` 与 layered-window 渲染契约。
- 更新功能索引：`codex_radar.model_iq_efficiency`、`codex_radar.service_health_quota_radar`、`claude_radar.window`。

## 数据、配置、日志与兼容性

没有新增或修改设置键、枚举、缓存、文件格式、外部 URL、网络请求或持久化数据。共享计算和绘制不访问窗体快照、不执行磁盘/网络 I/O，也不新增高频日志。Codex 仍由调用方传入 12 h，Claude 模式和独立 Claude 窗仍传入 24 h；ARM64 目标和 UX3407N/UX3607O 产品边界不变。

## 验证证据

- A1/A2 静态检索：Spec 列出的 Claude/Codex 镜像方法、`TryGetEvenRowBatchHour`、`phaseKnown`、`suffixTimeText`、`updateText`、`legacyUpdateColor` 均无命中；`GetCycleBoundaryLocal` 只有共享定义，两窗与两处自动切换均为调用方。
- 临时 ARM64 构建：`_build/DesktopCodexAssistant-arm64-radar-clock-dial-v1.0.5.03.exe` 成功。
- 临时测试：`--test`、`--test-layout`、`--test-settings-bindings`、`--test-radar-display-lifecycle --iterations 100`、`--test-logger`、`--test-display-recovery` 全部 exit 0。
- 隔离像素基线：用任务前备份的四个目标文件加当前其余源码构建 `1.0.5.02` 基线；新旧各生成 21 张 PNG。所有 522x120 样本在 `x < 350` 与 `x >= 470` 均为 0 像素差异，速蹬倒计时启用样本逐像素完全相同；普通表盘差异仅位于 `x=357..462`，与 V2/V3 范围一致。
- Release ARM64 构建成功；上述六项测试再次全部 exit 0。
- Release 渲染生成 21 张非空 PNG，采样结果为 `png-sample-ok count=21 total_distinct_sample_colors=23253`。
- Release、D 正式路径、E 镜像均为 `1.0.5.03`，长度 `1776128`，SHA256 `CB626461CA7F1EC320FBB506527AA7202F4361B2E74D40C25B42E76EC17EDCCD`；D 实例 PID `31696`，`Responding=True`，进程数 1。

## Spec 偏离与限制

- `ComputeState` 使用普通引用参数而非示例中的 `in` 修饰符，避免给当前 .NET Framework 单文件构建引入 readonly-ref 元数据依赖；函数仍然不修改输入且无副作用。
- `RadarClockDialDrawContext` 比示例多一个 `BadgeFont` 字段，用于逐像素保留两窗既有第二次晚测徽标字号；其余字体与 fitted-text 仍由窗体注入。
- 第一次尝试使用旧 Release 做像素基线时发现它未覆盖任务开始时工作树的全部既有源码，因此未将该结果用于验收；最终证据来自任务前文件备份构建的隔离基线。
- 只构建、验证和部署 ARM64；按项目规则未生成 x64。

## 产物与备份

- Release：`Release/DesktopCodexAssistant-arm64.exe`
- Release 渲染：`_build/radar-clock-dial-v1.0.5.03-release-render`
- 隔离像素对比：`_build/radar-clock-dial-isolated-compare-sequential`
- 源码任务备份：`_build/task-backups/20260711-011522-radar-clock-dial-v1.0.5.02`
- 正式程序备份：`D:/E_Drive_Files/Codexproject/desktopdata/DesktopCodexAssistant/_build/formal-backups/20260711-013149-radar-clock-dial-1.0.5.03`
