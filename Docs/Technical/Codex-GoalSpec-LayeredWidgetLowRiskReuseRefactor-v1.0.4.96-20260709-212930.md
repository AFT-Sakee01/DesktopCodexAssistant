# GoalSpec: Layered Widget Low-Risk Reuse Refactor

版本：`1.0.4.96`
生成时间：`2026-07-09T21:29:30+09:00`
生成模型：`Codex`

## Goal

执行 `Docs/Technical/Codex-LayeredWidgetLowRiskReuseRefactor-SPEC-v1.0.4.95-20260709-211158.md`，完成 4 个低风险复用项、严格验收、文档索引同步和 ARM64 正式部署。

## Spec Reference

- SPEC 路径：`Docs/Technical/Codex-LayeredWidgetLowRiskReuseRefactor-SPEC-v1.0.4.95-20260709-211158.md`
- SPEC SHA256：`A5B651DAD10456D3960B2614C039441B2FFDA432326C3B6722A0AFD22EAE16AA`
- 当前版本：`1.0.4.95`
- 实现版本：`1.0.4.96`

## Requirement Mapping

| SPEC 要求 | 实现证据 |
| --- | --- |
| 共享 UTF-8 no BOM 编码常量 | 新增 `Core/SharedEncoding.cs`，显式 `new UTF8Encoding(false)` 写入点改为 `SharedEncoding.Utf8NoBom` |
| 当前设置引用上移 | `LayeredWidgetFormBase.CurrentSettings` 持有运行设置；7 个子类和相关 partial 文件不再声明 `private WidgetSettings currentSettings` |
| 防烧屏刷新槽上移 | `LayeredWidgetFormBase.ShouldRefreshBurnInPosition()` 持有每窗口实例 slot；各窗口仍在 `ApplyRuntimeOffset` 调用点保留命名 salt |
| 透明度公式复用 | `LayeredWidgetFormBase.ComputeOpacityAlpha()` 统一百分比到 alpha 计算；`OperationForm.GetBackgroundOpacityAlpha()` 保留背景透明度范围 clamp |
| 版本与文档同步 | `Core/ProductIdentity.cs`、根 `AGENTS.md`、接口索引、复用资源文档、运行时文档、刷新规则文档和维护日志同步到 `1.0.4.96` |
| ARM64 验收和部署 | `_build` 临时 ARM64、`Release/DesktopCodexAssistant-arm64.exe` 和正式 D/E exe 均通过版本、哈希和测试核对 |

## Implementation Scope

- 新增：`Core/SharedEncoding.cs`
- 修改：`Core/LayeredWidgetFormBase.cs`
- 修改分层窗口：`WidgetForm`、`CodexRadarForm`、`ClaudeRadarForm`、`PowerThermalForm`、`NetworkMonitorForm`、`ConnectionCheckForm`、`OperationForm` 及相关 partial 文件
- 修改持久化文本写入点：Claude/Codex Radar、Quota、SecretStore、NetworkCheckHistory、UiHangWatchdog、Settings 等显式 no-BOM 调用
- 修改文档和索引：`Docs/Interfaces/INTERFACE_INDEX.jsonl`、`Docs/Interface-And-Reuse-Resources.md`、`Docs/Performance-And-Window-Runtime.md`、`Docs/Component-Refresh-Rules.md`、`Docs/Technical/INDEX.jsonl`、`Docs/Maintenance/CHANGELOG.jsonl`

## Architecture Notes

`LayeredWidgetFormBase` 继续只管理分层窗口公共生命周期和低风险状态，不接管每个窗口的绘制语义。防烧屏 offset 的窗口差异仍由各窗口调用 `BurnInProtection.ApplyRuntimeOffset` 时传入的命名 salt 决定。透明度共享只覆盖百分比到 alpha 的数学公式，不改变隐藏态、反色、hover、reverse reveal 或 OperationForm 的交互命中逻辑。

`SharedEncoding.Utf8NoBom` 只替代已有显式 no-BOM 写入语义。普通 `Encoding.UTF8` 读取或兼容路径没有被批量替换。

## Verification Evidence

已执行并通过：

- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-preversion.exe -Platform arm64`
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe -Platform arm64`
- `.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test`
- `.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-layout`：`Layout scaling policy: PASS`
- `.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-settings-bindings`
- `.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-display-recovery`：`Display recovery layered surface policy: PASS`
- `.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-logger`：`Logger storage policy: PASS`
- `.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-operation-panel`：`Operation panel interaction and performance policy: PASS`
- `.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-radar-display-lifecycle --iterations 100`：`Radar display lifecycle policy: PASS iterations=100 handles_delta=0 gdi_delta=0 user_delta=-1`
- 临时构建渲染样本：7 个 `--render-*` 入口生成 38 张 PNG，像素抽样结果 `png-sample-ok count=38`
- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\Release\DesktopCodexAssistant-arm64.exe -Platform arm64`
- Release 自测：`--test`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery`、`--test-logger`、`--test-operation-panel`、`--test-radar-display-lifecycle --iterations 100` 均通过
- Release 渲染样本：7 个 `--render-*` 入口生成 38 张 PNG，像素抽样结果 `release-png-sample-ok count=38 total_distinct_sample_colors=1338`
- Release exe：FileVersion/ProductVersion `1.0.4.96`，SHA256 `656F13272F38C41C4CD835409C6DD4308325E0AC6431856137643A54A5E876F9`

## Deployment Evidence

- 正式备份目录：`D:/E_Drive_Files/Codexproject/desktopdata/DesktopCodexAssistant/_build/formal-backups/20260709-212919-layered-low-risk-reuse-1.0.4.96`
- Release、D 正式 exe、E 镜像 exe SHA256 均为 `656F13272F38C41C4CD835409C6DD4308325E0AC6431856137643A54A5E876F9`
- Release、D 正式 exe、E 镜像 exe FileVersion/ProductVersion 均为 `1.0.4.96`
- 已停止旧进程 PID `13136`，从 D 正式路径重启 PID `32012`，`Responding=True`

## Spec Deviations

- `ComputeOpacityAlpha` 内部复用 `DesignTokens.ClampByte`，避免在基类新增与 `OperationForm` 私有 `ClampByte` 同名的方法；输出语义与 SPEC 公式一致。
- `CurrentSettings` 替换范围扩展到 Codex/Operation partial 文件，原因是这些 partial 文件访问同一字段；这是为了满足编译和实际运行边界。
- 额外更新 `Docs/Component-Refresh-Rules.md`，因为防烧屏 slot 所属从子类字段迁移到基类方法，刷新规则文档需要描述当前事实。

## Limits

- 未纳入 FileHelper、fontCache、RoundedRectangle、隐藏反色/Graphics 配置等 SPEC 排除项。
- 未构建或部署 x64。
