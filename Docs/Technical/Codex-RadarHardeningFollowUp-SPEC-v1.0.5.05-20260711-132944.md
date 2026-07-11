# Radar Hardening Follow-Up SPEC

## Metadata

- Document: `Codex-RadarHardeningFollowUp-SPEC-v1.0.5.05-20260711-132944.md`
- Generated model: Claude Fable 5
- Timestamp local: `2026-07-11T13:29:45+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.05`
- Target implementation version: `1.0.5.06`
- Status: approved（用户 2026-07-11 批复"做成收尾spec"）
- Source: 1.0.5.04/1.0.5.05 落地后的全量复审（复审确认两版实现符合设计；本 SPEC 收拢复审发现的 2 个残余问题、1 个 lint、1 项终证分析与 2 项验收遗留）

## Goal

收尾 Radar 加固系列：修复额度身份判别的反向拒绝残余、补齐绿色状态色变更的申报与证据、清理死字段、用已捕获的原始响应终证幻影池来源、完成绿点跨重启的实弹验收，并处置 `NO_STATUSLINE_CACHE` 日志噪音。全部为小改动，一次构建部署完成。

## Findings

### F1（修复）：tracked 锚在错误池上时真实样本被反向拒绝

2026-07-11 11:32:03 实录（quota-decision-history.jsonl）：1.0.5.04 重启把 tracked 锚在满额池（100/100）后，真实样本（5h=22/wk=88，anchor_age≈207min）被 `interference_pool_sample_ignored` 拒绝；约 70 秒后 session 样本经 `reset_confirmed_by_session` 翻正身份，此后幻影反向样本（11:33:12，96/99）被正确拒绝。本次自愈快是因为 CLI 活跃；若 CLI 空闲且 tracked 窗口未到期，最坏需等 30 分钟 `gap_rebaseline`。判别机制本身工作正常，缺的是"纠错方向"的持续性通道。

### F2（申报缺口）：绿色状态色改为 SuccessSoft 未申报

`RadarClockDial.GetPhaseColor` 的 CurrentCycle 返回 `SuccessSoft` alpha 255（代码注释：规避 GDI+ PArgb color-key 冲突），而刷新标记点仍为 `Success` alpha 245——同帧两种绿。该变更属 V2/V3 之外的第三处视觉偏差，1.0.5.03 的 GoalSpec 与 CHANGELOG 均未申报，color-key 冲突证据也未留档。

### F3（lint）：`RadarClockDialInput.LocalTime` 无消费者

`ComputeState` 只读 `LocalKnown`；时间行使用 `LastActualLocal`。字段与两处调用方赋值均为死代码。

### F4（终证分析）：幻影池来源可用已捕获的原始响应定案

`codex-usage-identity-change-*.json` 已滚动捕获 ≥5 份身份异变原始响应。最新样本证实账号存在 `additional_rate_limits`（如 "GPT-5.3-Codex-Spark" / `codex_bengalfox`，带独立 primary window）——**多池并存实锤**。对比各份抓取的顶层 `rate_limit` 锚点与 `additional_rate_limits` 各池锚点，即可判定幻影样本是否为后端把附加池视图错误地放进了顶层字段。

### F5（验收遗留）：绿点跨重启的实弹验收尚未发生

11:32 的绿点重置由 1.0.5.04（未含签名修复）造成；`ContentSignature` 键现已在缓存中。1.0.5.05 部署后尚未发生过重启，A4 修复的最终人工验收缺一次实弹。本 SPEC 自身的部署重启即为验收时机。

### F6（噪音处置）：`NO_STATUSLINE_CACHE` 反复失败日志

`claude_code_usage` 调度被"前台软件切换"触发后以 `Source=statusline-cache` 启动，桌面端从不运行 statusLine（既有事实，Claude 用量走 setup-token + oauth/usage 主链路），该路径每次必然 `Success=False, NO_STATUSLINE_CACHE`。预先存在的噪音，非本系列引入。

## Implementation Plan

### Phase 0: Baseline Checks

```powershell
rg -n "Version = \"1\.0\.5\.05\"" Core\ProductIdentity.cs
rg -n "reset_confirmed_by_rejected_persistence" Core\CodexRadarForm.cs
rg -n "LocalTime" Core\RadarClockDial.cs
```

第一条命中；第二条应无结果；第三条命中字段定义（实施后应消失）。

### Phase 1: 身份纠错持续性通道（F1）

在 `EvaluateQuotaWindowIdentity` 的拒绝路径外增加纠错追踪（每 family 的 QuotaRuntimeState 内，不持久化，重启清零）：

1. 样本被 `interference_pool_sample_ignored` 拒绝时，记录其身份锚点、首见时刻与连续计数；身份与上一条被拒样本不同（±2 分钟容差）或期间出现任何被接受样本 → 计数清零重记。
2. 同一身份连续被拒 ≥`QuotaRejectedPersistenceMinSamples`（=3）且跨度 ≥`QuotaRejectedPersistenceMinMinutes`（=10）→ 第 3 条起接受并重建基线，reason=`reset_confirmed_by_rejected_persistence`。
3. 语义依据：幻影是单发响应，永远无法连续 3 次同一身份不被真实样本打断；只有"tracked 本身锚错了"才会出现真实身份被持续拒绝——此时翻正是唯一正确动作。

约束：

- 六个既有接受通道优先级不变，本通道只在全部拒绝后追踪。
- 阈值常量化；detail 记录 `rejected_persistence_count` 与首见时刻。
- 与 `gap_rebaseline` 并存：谁先满足谁生效。

自测（扩展既有 quota 身份自测）：

1. 回放 11:32 案例：tracked 锚错，真实身份样本连续 3 条（跨 10 分钟）→ 第 3 条接受，reason=`reset_confirmed_by_rejected_persistence`。
2. 幻影单发：1 条被拒后出现 identity_same 被接受样本 → 计数清零，幻影不会积累。
3. 两池交替：身份 A、B 交替被拒 → 各自计数无法达 3，最终由 `gap_rebaseline` 兜底。

验收：

```powershell
rg -n "reset_confirmed_by_rejected_persistence|RejectedPersistence" Core\CodexRadarForm.cs
```

命中实现、常量与自测；部署后观察一晚，决策日志中被拒样本不再出现同一身份连续 ≥3 条仍被拒的序列。

### Phase 2: SuccessSoft 变更申报与证据复核（F2）

1. **证据复核**：用渲染样本重现——临时把 CurrentCycle 色改回 `Success` alpha 255 构建 QA 版，生成表盘处于绿相的样本，核对是否出现像素丢失/透明穿透（PArgb color-key 冲突的表现）。
2. **复核成立** → 保持 `SuccessSoft`，在 `Docs/Maintenance/CHANGELOG.jsonl` 补一条 change 记录申报该色值变更及原因，并在 `Docs/Claude-EvenRow-DialCard-Technical.md`（或对应表盘技术文档）记录"状态绿=SuccessSoft、标记点绿=Success"的并存事实与依据。
3. **复核证伪** → 状态色回退为 `Success` alpha 245（与标记点统一），渲染样本重截，CHANGELOG 申报回退。

约束：不改 V2/V3/V4 已申报的其余视觉；不动刷新标记点颜色（除非走回退分支后自然统一）。

验收：CHANGELOG 出现对应记录；QA 对比样本留档于 GoalSpec。

### Phase 3: 删除死字段（F3）

删除 `RadarClockDialInput.LocalTime` 字段及 `CodexRadarForm.EvenRow.cs`、`ClaudeRadarForm.cs` 两处调用方赋值。

验收：

```powershell
rg -n "LocalTime" Core\RadarClockDial.cs Core\CodexRadarForm.EvenRow.cs Core\ClaudeRadarForm.cs
```

仅允许命中与本字段无关的符号（如 `LastAttemptLocal`、`LastActualLocal`）。

### Phase 4: 幻影池归因终证（F4，分析任务，不改产品代码）

1. 编写一次性分析脚本（scratch，不入库）：解析全部 `codex-usage-identity-change-*.json`，对每份输出顶层 `rate_limit.primary/secondary` 的 `reset_at`/`used_percent` 与 `additional_rate_limits[]` 各池同字段的对照表。
2. 判定：若被拒样本的顶层锚点与某个附加池（如 Spark）锚点吻合 → 定案"后端把附加池视图放进了顶层字段"；若不吻合 → 维持"副本不一致"假设，继续依赖滚动抓取。
3. 结论写入 `Docs/Fable5-Data-Sources-And-Caching-Technical.md` 的 wham/usage 章节（幻影来源、多池结构、判别机制引用），并在已知限制中登记。

验收：文档出现归因结论与对照证据摘要；对照表原始输出留档于 GoalSpec。

### Phase 5: 绿点跨重启实弹验收（F5，随本版部署执行）

部署本版本前记录 `%LOCALAPPDATA%\DesktopCodexAssistant\codex-radar-cache.ini` 当前模型的 `RefreshedUtc` 与 `ContentSignature`；部署重启后等待首抓完成：

- 站点内容未变 → `RefreshedUtc` 保持原值、表盘绿点不跳到重启时刻 → A4 验收通过；
- 站点内容恰好变化 → `RefreshedUtc` 更新属正确行为，验收顺延至下一次内容稳定期的重启。

结果写入 GoalSpec 的 Verification Evidence。

### Phase 6: `NO_STATUSLINE_CACHE` 噪音处置（F6）

核实"前台软件切换"触发的 `Source=statusline-cache` 路径在桌面端是否恒失败（读代码确认无 statusLine 缓存生成方）。确认后二选一（以改动最小者优先）：

1. 该触发源在桌面端直接跳过 statusline-cache 尝试，走既有 oauth 主链路或静默返回；
2. 保留尝试但把必然失败的完成日志降为一次性（同一 ErrorCode 只记首个，直至成功过一次）。

约束：不改变 setup-token + oauth/usage 主链路的任何行为与节奏。

验收：部署后日志不再周期性出现 `NO_STATUSLINE_CACHE` 的 `Success=False` 完成记录（或仅出现一次）。

### Phase 7: 版本与文档同步

版本 `1.0.5.05` → `1.0.5.06`，同步：

- `Core/ProductIdentity.cs`、`AGENTS.md`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`：quota 身份判别条目补 `rejected_persistence` 通道
- `Docs/Fable5-Data-Sources-And-Caching-Technical.md`：Phase 4 归因结论
- `Docs/Maintenance/CHANGELOG.jsonl` change + deploy 记录（含 Phase 2 的申报条目）
- `Docs/Technical/INDEX.jsonl`：登记本 SPEC 与对应 GoalSpec

## Verification Matrix

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-radar-hardening-followup-v1.0.5.06.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-radar-hardening-followup-v1.0.5.06.exe --test
.\_build\DesktopCodexAssistant-arm64-radar-hardening-followup-v1.0.5.06.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-radar-hardening-followup-v1.0.5.06.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-radar-hardening-followup-v1.0.5.06.exe --test-radar-display-lifecycle --iterations 100
```

渲染样本抽查（Phase 2 若走回退分支则重截绿相样本）。全部通过后按正式部署流程（Release 复测 → 备份 → D/E 双路径 → 重启 D 实例 → SHA256/版本三方核对 → CHANGELOG deploy 记录），部署重启同时执行 Phase 5 实弹验收。

## Acceptance Summary

| 项 | 验收条件 |
| --- | --- |
| F1 纠错通道 | 3 个自测场景通过；rg 命中实现/常量/自测；一晚日志无"同一身份连续 ≥3 条被拒"序列 |
| F2 申报补齐 | QA 对比样本留档；CHANGELOG 出现申报（保持或回退二选一）；技术文档记录两绿并存或统一结论 |
| F3 死字段 | 三文件 rg 无 `LocalTime` 残留 |
| F4 终证 | 对照表留档 GoalSpec；数据源文档出现归因结论与已知限制 |
| F5 绿点验收 | 部署重启后 `RefreshedUtc` 不变（或因内容真变而顺延，需注明） |
| F6 噪音 | 部署后 `NO_STATUSLINE_CACHE` 失败记录消失或仅一次 |
| 发布 | 版本 `1.0.5.06` 三方（Release/D/E）SHA256 与版本一致，D 实例重启后 Responding |
