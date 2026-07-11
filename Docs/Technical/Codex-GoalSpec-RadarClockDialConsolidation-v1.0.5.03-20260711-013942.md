# GoalSpec: Radar Clock Dial Consolidation Final Acceptance

- Goal：执行 `Codex-RadarClockDialConsolidation-SPEC-v1.0.5.01-20260710-235413.md`，完成共享 Radar IQ 时钟整合、视觉等价验收、文档闭环与 ARM64 正式部署。
- 实现版本：`1.0.5.03`
- 执行模型：Codex
- 完成时间 UTC：`2026-07-10T16:39:42.9066316Z`
- 完成时间本地：`2026-07-11T01:39:42.9066316+09:00`
- 时区：`Asia/Tokyo (+09:00)`
- Spec 路径：`Docs/Technical/Codex-RadarClockDialConsolidation-SPEC-v1.0.5.01-20260710-235413.md`
- Spec SHA256：`3F2EB7A6872B2722F070F36B361C5F3EF414DC8042EF763FF1FC2C47266F76A0`

## 最终实现

`Core/RadarClockDial.cs` 统一承载 `RadarClockDialPhase`、`ComputePhase`、`ComputeState`、周期边界、刷新点角度、扫角、日期/后缀/时间模式解析、GDI+ 绘制原语和 12 h/24 h 自测。共享 `CodexRadarForm` 与独立 `ClaudeRadarForm` 仅负责提取各自快照字段并注入字体和 fitted-text 委托；两处自动模型切换统一调用 `GetCycleBoundaryLocal`。

状态颜色保持单一 Phase switch。为保留既有 missing/error/offline 语义，本地检查已完成但模型批次时间未知时仍显示红色状态文字，但不推断边界弧或满环；只有 `BatchKnown` 能证明周期年龄并启用 Waiting/Missed 的几何。这一边界由共享自测覆盖。

Codex 速蹬结束倒计时仍为专属覆盖层，业务状态、比例、中心文字和天蓝色刻度不变，只复用 `DrawBoundaryTick`。V2 将普通 IQ 时钟顶部周期刻度改为 `White(170)`；V3 将已知整周期缺失底环改为 `Danger` alpha 90，当前周期弧继续使用 `Danger` alpha 245。

## 需求与索引映射

- R1/R2/R3/R4：由 `internal_api.radar_clock_dial` 的状态、快照、几何和绘制统一实现。
- R5：`ApplyRadarClockAutoSwitchIfNeeded` 和 `ApplyClaudeRadarClockAutoSwitchIfNeeded` 复用同一边界函数。
- 功能索引：`codex_radar.model_iq_efficiency`、`codex_radar.service_health_quota_radar`、`claude_radar.window`。
- 活文档：`Docs/CodexRadar-Architecture.md`、`Docs/Codex-ClaudeRadar-Architecture.md`、`Docs/Claude-EvenRow-DialCard-Technical.md`、`Docs/Interface-And-Reuse-Resources.md`。
- 无新增设置键、枚举、外部 URL、缓存、持久化文件、网络请求或高频日志。

## 最终验证证据

- Spec A1/A2 的全部旧镜像和死代码检索无结果；共享边界函数只有一个定义，两窗绘制和两处自动切换均为调用方。
- 临时与最终 Release ARM64 均构建成功。
- 最终 Release 的 `--test`、`--test-layout`、`--test-settings-bindings`、`--test-radar-display-lifecycle --iterations 100`、`--test-logger`、`--test-display-recovery` 全部 exit 0。
- 最终 Release 生成 21 张非空 PNG：`png-sample-ok count=21 total_distinct_sample_colors=22911`。
- 使用任务前四个目标文件加当前其余源码构建隔离旧实现；再构建“共享新实现但临时恢复 V2/V3 旧颜色”的 QA 版本。两者各生成 21 张图：Codex 所有 522x120 样本逐像素完全一致；Claude 每张仅剩 18–22 个实时白点移动像素，最坏 22，位置均在当前时刻点附近。该结果证明状态几何、弧线、日期/时间/模式文字、徽标和布局未因整合改变。
- 真实 V2/V3 新实现与旧基线的差异只位于普通表盘区域；速蹬倒计时启用样本逐像素一致。
- Release、D 正式路径与 E 镜像均为 `1.0.5.03`，长度 `1776640`，SHA256 `665583347F2A5EEFB8A74E67B5C1B5B3942F0427996E4CC4616B9EBA9D8F1645`。
- D 正式实例 PID `66948`，`Responding=True`，进程数 1。

## 偏离、兼容性与限制

- `ComputeState` 没有采用示例中的 `in` 修饰符，以避免当前 .NET Framework 单文件编译目标引入 readonly-ref 元数据依赖；函数仍不修改输入且不执行 I/O。
- `RadarClockDialDrawContext` 增加 `BadgeFont`，用于保持两窗第二次晚测徽标的原字号，其余字体和 fitted-text 行为仍由窗体注入。
- 任务中间 GoalSpec 在最终审计前过早记录了“已完成”；随后发现批次未知时错误推断整周期几何，因此该文档在技术索引中标记 `abandoned`，本文件是最终验收记录。
- 仅构建、验证和部署 ARM64；按项目规则未生成 x64。

## 产物与备份

- `Release/DesktopCodexAssistant-arm64.exe`
- `_build/radar-clock-dial-v1.0.5.03-release-render-final`
- `_build/radar-clock-dial-geometry-equivalence-fixed`
- `_build/task-backups/20260711-011522-radar-clock-dial-v1.0.5.02`
- `D:/E_Drive_Files/Codexproject/desktopdata/DesktopCodexAssistant/_build/formal-backups/20260711-013149-radar-clock-dial-1.0.5.03`
