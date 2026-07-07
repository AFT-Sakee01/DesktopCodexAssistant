# Radar Clock Refresh Arc Fix

版本：1.0.4.24
生成时间：2026-07-07 03:02:00 +09:00
生成模型：Codex

## 目标

修正 Codex/Claude Radar 时钟圆弧语义：圆弧必须从上一次实际刷新点顺时针连接到当前白点，而不是从 12 点周期边界开始。Codex 小绿点保留 12 小时，Claude 小绿点保留 24 小时，过期后不再显示，也不再作为圆弧起点。

## 实现

- `Core/CodexRadarForm.EvenRow.cs`：`DrawEvenRowBatchDial` 先计算有效刷新点角度，再用 `ComputeEvenRowClockSweep` 绘制“刷新点 -> 当前白点”的顺时针连接弧；没有有效刷新点时不再用旧刷新点绘制连接弧。
- `Core/ClaudeRadarForm.cs`：`DrawClaudeEvenRowBatchDial` 使用同样规则，基于选中模型 `latest_at` 计算 24 小时刷新点和连接弧。
- `TryGetEvenRowClockMarkerAngle` / `TryGetClaudeEvenRowClockMarkerAngle` 保留真实年龄限制：Codex 满 12 小时隐藏，Claude 满 24 小时隐藏；刷新点可以跨周期显示，直到当前指针下一圈到达该点。
- `RunEvenRowDialFreshnessSelfTest` 和 `RunClaudeEvenRowDialFreshnessSelfTest` 增加跨周期连接弧断言，覆盖“未满保留期仍可见”和“满保留期隐藏”。

## 验证

- `Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-clock-link-1.0.4.24-test.exe -Platform arm64` 通过。
- 测试构建 `--test`、`--test-layout`、`--test-settings-bindings`、`--test-logger` 全部通过。
- 测试构建 `--render-codexradar` 和 `--render-clauderadar` 生成样本；EvenRow 样本确认 12 点绿线、当前白点和服务 LED 正常。

## 边界

- 12 点绿色竖线只表示周期边界，不是连接弧的固定起点。
- 圆弧只在存在未过期刷新点时表达“上次刷新到现在”的时间跨度。
- 过期的 `RefreshedUtc` / `latest_at` 仍可留在缓存中用于历史和回显，但绘制路径不会再把它作为小绿点或连接弧起点。
