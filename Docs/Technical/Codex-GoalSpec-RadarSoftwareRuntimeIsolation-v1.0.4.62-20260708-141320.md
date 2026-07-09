# GoalSpec: Radar Software Runtime Isolation

版本：`1.0.4.62`  
生成时间：`2026-07-08 14:13:20 +09:00`  
生成模型：`Codex`  
状态：`implemented`

## Goal

按 `Docs/Technical/Codex-RadarSoftwareRuntimeIsolation-SPEC-v1.0.4.61-20260708-133109.md` 执行 Codex Radar 共享窗口内 Codex/Claude 软件族运行态隔离，保持现有 UI 布局和自动切换语义，并完成 ARM64 构建、正式覆盖和重启验收。

## Spec

- Spec 路径：`Docs/Technical/Codex-RadarSoftwareRuntimeIsolation-SPEC-v1.0.4.61-20260708-133109.md`
- Spec SHA256：`A682BB189FAC2AEEB20493C87D2E2FC55F67C586E3AD0F0BD9FFDC66C2CA3BED`
- 执行结论：按用户确认执行；原 spec 正文保持冻结，执行状态在 `Docs/Technical/INDEX.jsonl` 回填。

## 需求映射

| Spec 要求 | 实现位置 | 验收 |
|---|---|---|
| 软件族切换控制器独立且纯逻辑 | `Core/RadarSoftwareModeController.cs` | `--test` 覆盖 fixed/auto/foreground/selected quota target 矩阵 |
| Codex/Claude runtime state 隔离 | `Core/CodexRadarForm.RuntimeState.cs` | `RunRadarFamilyRuntimeIsolationSelfTest` 覆盖 quota、刷新调度、Radar health、API debounce 隔离 |
| 额度快照、source-known、消耗环基线隔离 | `ApplyQuotaSnapshot(family, ...)`、`QuotaRuntimeState` | 新 quota 日志行含 `software_family`；最新正式运行日志显示 `software_family=Codex` |
| Codex reset/RSS protection 不影响 Claude | `QuotaProtectionState` 仅由 `codexRuntimeState` 持有；Claude `ApplyQuotaResetProtections` 返回普通快照 | `--test` 通过；代码路径只在 Codex family 套用保护 |
| Radar 网站健康和请求态按 family 隔离 | `RadarFamilyRuntimeState.RadarSiteHealth`、`RadarStatusRequestRunning`、`NextRadarStatusRefreshUtc` | 网站请求完成只写请求所属 family；render cache key 包含 active family revision |
| API 摘要防抖不跨 family 泄漏 | `ApiAlertDebounceRuntimeState` | `RunRadarFamilyRuntimeIsolationSelfTest` 验证 Codex 稳定错误不进入 Claude state |
| UI 布局不做无关变化 | 未改 `EvenRow` 几何；只改状态来源 | 渲染样本 `codexradar-current.png`、`clauderadar-current.png` 非空且边框/底部文本保持 |
| 文档、索引、版本和维护日志同步 | `Docs/*`、`AGENTS.md`、`Core/ProductIdentity.cs` | JSONL gate、版本一致性、`git diff --check` 执行 |
| ARM64 正式覆盖并重启 | `Release/DesktopCodexAssistant-arm64.exe` -> formal E/D exe | `FileVersion=1.0.4.62`，SHA256 一致，PID `62920` 响应正常 |

## 架构流程

`CodexRadarForm` 仍拥有窗口生命周期、绘制和调度。软件族选择先由 `RadarSoftwareModeController.Resolve` 生成纯决策；切换事务只恢复目标 family 的内存/磁盘缓存、安排刷新和触发重绘，不在切换事务里做远程请求。

运行态由两个 `RadarFamilyRuntimeState` 保存：

- `Codex`：Codex Radar 网站快照、Codex quota、Codex reset/RSS/速蹬保护、Codex Radar health、Codex API debounce。
- `Claude`：Claude Radar 转换快照、Claude quota、Claude Radar health、Claude API debounce。

`QuotaRuntimeState` 保存快照、source-known、上次读取余额、消耗环基线、刷新调度时间。`QuotaProtectionState` 只由 Codex state 使用，避免 Claude 读取 `quota-reset-state.ini` 或显示 Codex reset/速蹬保护态。

## 关键模块和接口

- `Core/RadarSoftwareModeController.cs`：软件族选择纯决策。
- `Core/CodexRadarForm.RuntimeState.cs`：family runtime state 和 active-state compatibility wrapper。
- `Core/CodexRadarForm.cs`：`ApplyQuotaSnapshot(family, ...)`、family-aware Radar 网站刷新、family-aware service health、quota 日志 `software_family`。
- `Core/CodexRadarForm.CodexUsage.cs`：Codex provider 成功路径显式传 `Codex`。
- `Core/CodexRadarForm.ClaudeUsage.cs`：Claude usage 成功路径显式传 `Claude`；切换入口收口为 `SwitchCodexRadarSoftwareFamily`。
- 接口索引新增 `internal_api.radar_software_mode_controller` 和 `internal_api.codex_radar_family_runtime_state`。

## 数据、配置和日志

- 持久化 quota 文件保持不变：Codex 使用 `quota.ini`，Claude 使用 `claude-quota.ini`。
- `quota-decision-history.jsonl` 新增 `detail.software_family`，用于区分 Codex/Claude 判定。
- Codex reset/RSS 状态仍使用 `quota-reset-state.ini`，但只作用于 Codex family。
- 没有新增外部 URL、凭据或用户隐私数据写入。

## 验证证据

候选构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-1.0.4.62-runtime-isolation.exe -Platform arm64
```

结果：构建通过。

候选自测：

```powershell
.\_build\DesktopCodexAssistant-arm64-1.0.4.62-runtime-isolation.exe --test
.\_build\DesktopCodexAssistant-arm64-1.0.4.62-runtime-isolation.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-1.0.4.62-runtime-isolation.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-1.0.4.62-runtime-isolation.exe --test-logger
.\_build\DesktopCodexAssistant-arm64-1.0.4.62-runtime-isolation.exe --test-radar-display-lifecycle --iterations 120
```

结果：`--test` exit 0；布局输出 `Layout scaling policy: PASS`；设置绑定 exit 0；日志输出 `Logger storage policy: PASS`；生命周期输出 `handles_delta=0 gdi_delta=0 user_delta=-1`。

渲染验收：

```powershell
.\_build\DesktopCodexAssistant-arm64-1.0.4.62-runtime-isolation.exe --render-codexradar --out .\_build\radar-runtime-isolation-render-1.0.4.62
.\_build\DesktopCodexAssistant-arm64-1.0.4.62-runtime-isolation.exe --render-clauderadar --out .\_build\radar-runtime-isolation-render-1.0.4.62
```

结果：`codexradar-current.png`、`clauderadar-current.png` 和 Claude 2880x1800 验收矩阵均生成；当前样本非空，Codex 深蓝边框、Claude 橙色边框和底部软件族文本保持。

正式构建和自测：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\Release\DesktopCodexAssistant-arm64.exe -Platform arm64
.\Release\DesktopCodexAssistant-arm64.exe --test
.\Release\DesktopCodexAssistant-arm64.exe --test-layout
.\Release\DesktopCodexAssistant-arm64.exe --test-settings-bindings
.\Release\DesktopCodexAssistant-arm64.exe --test-logger
.\Release\DesktopCodexAssistant-arm64.exe --test-radar-display-lifecycle --iterations 120
```

结果：Release 构建通过；`--test` exit 0；`--test-layout` 输出 `PASS`；设置绑定输出 `Settings full round-trip: PASS 211 persisted properties (219 supported public properties, 8 explicit exemptions)`；`--test-logger` 输出 `PASS`；生命周期输出 `handles_delta=0 gdi_delta=0 user_delta=-1`。

部署验收：

- 备份目录：`E:\Codexproject\desktopdata\DesktopCodexAssistant\_build\formal-backups\20260708-141247-radar-runtime-isolation-1.0.4.62`
- Formal exe：`E:\Codexproject\desktopdata\DesktopCodexAssistant\DesktopCodexAssistant.exe`
- Mirror exe：`D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant\DesktopCodexAssistant.exe`
- Release/Formal/Mirror 版本：`1.0.4.62`
- SHA256：`A187AE2AE1AB5C7CBBDFEC7D4E442E5C49825DCDA6E16A8C9D02EDD9930B24BA`
- 启动 PID：`62920`
- Responding：`True`
- 进程数量：`1`
- error.jsonl：不存在；legacy `error.log` 最后写入仍为 `2026-07-07 02:43:00`

文档 gate：

```powershell
JSONL parse + id uniqueness
Docs path existence
```

结果：均为 `PASS`。

## Spec 偏离

- `RadarFamilyRuntimeState`、`QuotaRuntimeState`、`QuotaProtectionState` 和 `ApiAlertDebounceRuntimeState` 作为 `CodexRadarForm` partial 的 nested 类型实现，而不是独立文件。原因是这些类型只服务该窗口内部状态边界，嵌套后可以复用现有锁和 private helper，同时通过 `Core/CodexRadarForm.RuntimeState.cs` 保持单独文件边界。
- `ResetCodexApiServiceAlertDebounceForDisplayContextSwitch` 保留方法名以减少调用面变更；实现已转为 active family 的防抖 state，而不是全局 state。
- 没有新增外部模拟 reader；异步完成隔离通过请求时捕获 software/model 并只写 requested state 实现，现有自测覆盖 family state 不串扰。

## 限制和后续风险

- 真实 Codex/Claude 前台切换的一帧体验仍依赖现场进程和前台窗口状态，自动测试覆盖决策矩阵和渲染生命周期，但没有在用户桌面真实操控两款软件切换。
- `codexRadarDisplayModeCache` 作为旧兼容缓存仍保留，但优先级低于 `RadarFamilyRuntimeState`；后续可在确认稳定后删除该兼容层。
- OpenAI、Claude status、DeepSeek 属于服务级共享状态，不随软件族完全复制；Radar 网站健康和 API debounce 已按 family 隔离。

