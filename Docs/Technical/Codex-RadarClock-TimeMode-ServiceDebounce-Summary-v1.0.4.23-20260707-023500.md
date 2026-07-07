# Radar Clock Time Mode And Service Debounce

版本：1.0.4.23
生成时间：2026-07-07 02:35:00 +09:00
生成模型：Codex

## 目标

修复 Codex/Claude Radar 时钟绿点过期后仍可能由旧渲染缓存显示的问题；在时钟 12 点方向增加绿色竖线；新增时钟中心时间来源设置；为 Codex Radar 右侧 `R/O/C/D`、Claude Radar 右侧 `R/C/U` 网络服务 LED 和 API 摘要增加 10 秒错误防抖。

## 实现范围

- `Settings/WidgetSettings.cs` 新增 `RadarClockTimeDisplayMode`，`CurrentSettingsVersion` 升到 51。默认值为 `Utc`，并接入默认值、用户默认快照、Clone、Normalize、Save、ApplyValue 和兼容自测。
- `Settings/Win11SettingsForm.cs` 在 Codex Radar 的“模型与时区”组加入“时钟时间来源”下拉，选项为 UTC、当前时间、上次尝试刷新、上次实际刷新；该设置同时影响 Codex Radar 和 Claude Radar。
- `Core/CodexRadarForm.EvenRow.cs` 和 `Core/ClaudeRadarForm.cs` 的时钟中心下方时间改为读取 `RadarClockTimeDisplayMode`；两个时钟都在 12 点方向绘制绿色竖线。
- `Core/CodexRadarForm.cs` 在场景缓存 key 中加入当前分钟、时间模式和上次尝试刷新时间，防止小绿点超过保留窗口后继续复用旧 bitmap。
- `Core/CodexRadarForm.cs` 新增 `ApplyCodexApiServiceAlertDebounce`。非检测中错误必须对同一服务连续存在满 10 秒才进入 API 摘要和 `R/O/C/D` LED；恢复为正常时立即清除；随机测试和服务健康测试模式旁路防抖。
- `Core/ClaudeRadarForm.cs` 新增 `ApplyClaudeServiceAlertDebounce`。Claude 独立窗口的 API 摘要和 `R/C/U` LED 共用防抖候选；非正常服务错误连续存在满 10 秒才显示，恢复时立即清除，随机测试模式旁路防抖。

## 边界

- Codex 绿点仍按 12 小时保留，Claude 绿点仍按 24 小时保留；角度计算保持“下一圈指针到达绿点位置后删除”的规则。
- 防抖只影响显示候选，不改变后台请求、日志、服务健康原始状态或手动检测结果。
- 当前分钟进入缓存 key 后，时钟视觉最多按分钟刷新；这避免每秒重绘小窗口，同时解决绿点长期冻结。

## 验证

- `Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-clock-debounce-1.0.4.23-test.exe -Platform arm64` 通过。
- 测试构建 `--test`、`--test-layout`、`--test-settings-bindings`、`--test-logger` 全部通过。
- 测试构建 `--render-codexradar` 和 `--render-clauderadar` 生成样本；EvenRow 样本确认 12 点绿色竖线、默认 UTC 时间和 LED 列可见。
- Release 构建 `Release\DesktopCodexAssistant-arm64.exe` 通过。
- Release `--test`、`--test-layout`、`--test-settings-bindings`、`--test-logger` 全部通过。
- Release `--render-codexradar` 和 `--render-clauderadar` 生成正式样本。
- 第二轮修正后 `Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-clock-debounce-1.0.4.23-test2.exe -Platform arm64`、`--test`、`--test-layout`、`--test-settings-bindings`、`--test-logger` 通过；`RunClaudeServiceAlertDebounceSelfTest` 覆盖 Claude 服务错误 10 秒防抖和恢复立即清除。
- 第二轮 `--render-codexradar` / `--render-clauderadar` 样本确认 Codex `R/O/C/D`、Claude `R/C/U` 服务点仍可见，时钟 12 点绿色竖线保留。
