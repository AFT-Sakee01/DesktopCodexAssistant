# GoalSpec: Radar Codex Data Hardening

## Goal And Spec

执行拆分后的 Codex 数据子 Spec，修复模型目录合并与完整性、缓存签名跨重启保真、额度窗口身份判定、通知聚合和账户端点错峰，并完成 ARM64 正式验收。

- Spec: `Docs/Technical/Codex-RadarCodexDataHardening-SPEC-v1.0.5.03-20260711-103509.md`
- SHA-256: `0D6251EA6EEE13AF8B73E4A5B8882AA198457112A54C598D08C5AD3F97A22389`
- Implemented version: `1.0.5.05`
- Time: `2026-07-11T11:33:55.3987844+09:00`
- Generated model: Codex

## Requirement Mapping

### Model Catalog And Settings

- `CodexRadarModelCatalog.MergeDuplicateCatalogRecord` 以较新记录整条覆盖；同时间可用优先，再相同保留先到记录。
- `MergeCatalogRecords` 区分完整与不完整目录。两处 HTML 调用明确传 `false`；JSON 只有来源数量与归一化去重数量相等时才允许推进缺失计数。
- 默认模型迁移在 `Normalize` 后补跑，Version 1..61 的空 key 能迁移并同步旧枚举。
- fallback label 只把非 effort family 段首字母大写，`xhigh/high/medium/low` 保持小写。
- 4 条以上模型事件汇总通知；当前选择模型不可用/删除时明确提示，删除后可强制从非历史可用候选自动切换。

### IQ Cache

- 每个模型缓存持久化 `ContentSignature` 与 `CheckedAtUtc`；同内容跨重启保留原 `RefreshedUtc`。
- 空 key 使用 `Model.default.`；未知 `DataWindowHour` 往返为空，不再默认为上午。
- 共享窗口 Claude 候选映射 `HistoricalOnly` 并在选择器中过滤。独立 Claude Radar 没有同构的 `RefreshedUtc` 内容签名保留路径，因此未做伪同步修改。

### Quota Identity

- `EvaluateQuotaWindowIdentity` 对 5 小时和周窗口独立判定，±2 分钟为同一身份。
- 身份变化由旧窗口到期、新生满额窗口、6 小时内 reset 事件、session 来源或超过 30 分钟间隔确认；否则恢复该环上个接受值。
- 两环均拒绝时使用 `interference_pool_sample_ignored`，不更新 provider cache、`quota.ini`、source time 或消耗环基线。
- 身份变化原始 body 只写 `codex-usage-identity-change-*.json`，不包含授权 header/token，保留最近 8 份。
- usage provider 与 reset-credits 启动至少双向错峰 10 秒，原刷新周期不变。

## Architecture And Reuse

主要实现位于 `Core/CodexRadarModelCatalog.cs`、`Core/CodexRadarForm.cs`、`Core/CodexRadarForm.CodexUsage.cs`、`Core/CodexRadarForm.RuntimeState.cs`、`Settings/WidgetSettings.cs` 和 `Core/RadarSoftwareModeController.cs`。复用既有 `RadarFamilyRuntimeState`、`QuotaDecisionHistoryLogger`、`ClaudeRadarClockAutoSwitchSelector`、模型通知状态文件和 Logger 数据目录，没有新增常驻线程或绘制路径 IO。

渲染验收夹具按表盘类型选择稳定的测试合成路径：倒计时使用真实背景/内容分层，普通时钟使用生产整层绘制。该逻辑仅在 `--render-codexradar` 命令执行，不进入正式窗口运行路径。

## Verification Evidence

- ARM64 Release 构建成功，版本 `1.0.5.05`。
- `--test`、`--test-layout`、`--test-settings-bindings`、`--test-radar-display-lifecycle --iterations 100`、`--test-logger`、`--test-display-recovery` 全部退出 0；生命周期 `handles_delta=0`、`gdi_delta=0`、`user_delta=-1`。
- 6 张 Codex Radar PNG 均非空；所有 522x120 样本左右区域都有有效像素，速蹬开关两态完整。
- 首次 `.05` 刷新写入 `ContentSignature` 和 `CheckedAtUtc`；再次重启后 `SavedUtc` 前进，但 `RefreshedUtc=2026-07-11T02:32:03.3609807Z` 与签名保持不变。
- 没有新增 `Model..`/`Codex.Model..` 空前缀；部署后 `error.log` 修改时间未变化。
- Release、D 正式和 E 镜像均为长度 `1802752`、SHA-256 `E29CB0229FA7A4089706BB3EE3A2F0FBE0C54BDA0A7A2264A769862C09427ADD`。
- 最终 D 正式进程 PID `47288`，单实例且 `Responding=True`。

## Deviations And Clarifications

- `.04` 已被并行 Claude/时钟子 Spec 使用，因此本子 Spec 按项目版本规则发布为 `.05`。
- Spec 的新生窗口明确样本比名义窗口长 1 秒/33 秒；实现复用 2 分钟身份容差作为负 anchor age 的时钟偏差边界，实际接受范围为 -2..8 分钟。
- 透明 PArgb 渲染夹具暴露 GDI+ 组合缺陷；最终只修改测试夹具合成策略，生产时钟的局部裁剪/抗锯齿修复属于 `.04` 子 Spec。

## Known Limits

GPT-10+ key 拆分与归一化碰撞、旧默认目录一次通知、缺失 Passed 估算、旧 5.6 缓存身份、7 天 TTL 后空窗以及速蹬状态不缓存，均已写入 `Docs/CodexRadar-Architecture.md` 的已知限制章节。

## Deployment

- Backup: `D:/E_Drive_Files/Codexproject/desktopdata/DesktopCodexAssistant/_build/formal-backups/20260711-113155-radar-codex-data-hardening-1.0.5.05`
- Release: `Release/DesktopCodexAssistant-arm64.exe`
- Formal: D path; mirror: E path
- Architecture: ARM64 only; x64 intentionally not built.
