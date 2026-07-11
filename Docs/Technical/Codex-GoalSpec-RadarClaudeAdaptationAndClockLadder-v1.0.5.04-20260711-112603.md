# GoalSpec: Radar Claude Adaptation And Clock Ladder

## Goal

执行 `Codex-RadarClaudeAdaptationAndClockLadder-SPEC-v1.0.5.03-20260711-103509.md`，完成 Claude 模型生命周期适配、历史候选过滤、`n2` 徽标、获批的时钟延迟色阶和 ARM64 正式发布。

## Spec Identity

- Spec: `Docs/Technical/Codex-RadarClaudeAdaptationAndClockLadder-SPEC-v1.0.5.03-20260711-103509.md`
- SHA-256: `EF1B6250219F07DE5FA6F3EB31227F79769AAFB4E88E027BD759EAB83838405E`
- Implemented version: `1.0.5.04`
- Generated model: Codex
- Time: `2026-07-11T11:26:03.3730354+09:00`

## Requirement Mapping

- Claude 模型映射保存来源显示名和历史状态；改名、缺失、恢复、禁用及旧格式读取由 `ClaudeRadarReader`、`ClaudeRadarModels` 和映射编辑器处理。
- 自动切换候选过滤 `HistoricalOnly`，共享窗口和独立窗口继续复用确定性选择器。
- `RadarClockDial` 识别 `pm2`、`n2` 及分隔符变体，第二次发布只显示徽标，不创造新周期。
- 获批的状态阶梯为落后 0-1 个窗口绿色、2 个窗口黄色、3 个及以上红色；普通绿色使用全不透明 `SuccessSoft` 并在表盘范围内保存/恢复裁剪和文字渲染状态。

## Implementation And Boundaries

实现位于 `Core/ClaudeRadarModels.cs`、`Core/ClaudeRadarReader.cs`、`Core/ClaudeRadarForm.cs`、`Core/ClaudeRadarModelMapEditorForm.cs` 与 `Core/RadarClockDial.cs`。`.04` 权威产物从隔离树构建，并把 Codex 数据加固独占文件恢复到执行前备份，避免把后续 `.05` 功能夹带进 `.04`。主工作树中的共享文档、接口索引和表盘实现保留为当前版本。

透明位图验收发现 ClearType、主 Success RGB 与 PArgb 的组合会导致测试样本首帧丢失同级像素。生产修复只在共享表盘局部使用 `AntiAliasGridFit`、`SuccessSoft` 和矩形裁剪，不新增每帧 bitmap。测试先预热，再以第二轮样本作证据。

## Verification

- ARM64 构建成功：`DesktopCodexAssistant-arm64-radar-claude-clock-v1.0.5.04.exe`。
- `--test`、`--test-layout`、`--test-settings-bindings`、`--test-radar-display-lifecycle --iterations 100`、`--test-logger`、`--test-display-recovery` 全部退出 0。
- 13 张非桌面 Radar PNG 均非空；Codex/Claude 522x120 样本左右区域都有有效像素，速蹬开启和关闭样本完整。
- Release、D 正式路径和 E 镜像均为 `1.0.5.04`，长度 `1783808`，SHA-256 `6D4363E860FA697977AECFA448C8428011F8D5FE25F9F689C705935A1F76A64B`。
- 正式进程从 D 路径重启为 PID `60952`，单实例且 `Responding=True`。

## Deployment

- Backup: `D:/E_Drive_Files/Codexproject/desktopdata/DesktopCodexAssistant/_build/formal-backups/20260711-112549-radar-claude-clock-1.0.5.04`
- Release: `Release/DesktopCodexAssistant-arm64.exe`
- Architecture: ARM64 only; x64 intentionally not built.

## Deviations And Limits

- 原合并 Spec 已明确拆成 Codex 数据子 Spec和本子 Spec，本文件只封闭 Claude/时钟范围。
- 为保证 `.04` 不夹带 `.05`，权威构建使用隔离树；测试夹具的冷启动预热不属于运行时功能。
- Claude 仍以来源 key 作为稳定身份；上游复用旧 key 表示全新模型时无法自动区分，按 Spec 保留为已知限制。
