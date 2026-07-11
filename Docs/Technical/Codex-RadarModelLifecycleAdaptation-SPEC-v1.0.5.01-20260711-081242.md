# Radar Model Lifecycle Adaptation SPEC

> 状态（2026-07-11）：本 SPEC 未单独执行，全部内容已并入合并版 `Codex-RadarHardeningConsolidated-SPEC-v1.0.5.03-20260711-094500.md`（第二部分，Phase B0-B3）。请以合并版为准，本文件仅供追溯。

## Metadata

- Document: `Codex-RadarModelLifecycleAdaptation-SPEC-v1.0.5.01-20260711-081242.md`
- Generated model: Claude Fable 5
- Timestamp UTC: `2026-07-10T23:12:42Z`
- Timestamp local: `2026-07-11T08:12:42+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.01`
- Target implementation version: `1.0.5.04`（建议顺序：1.0.5.02 目录+缓存加固 → 1.0.5.03 时钟整合 → 本 SPEC；如需提前可与 1.0.5.03 互换，两者代码面不重叠）
- Status: draft
- Source: 从 `Codex-CodexRadarModelCatalogHardening-SPEC-v1.0.5.01-20260710-232034.md` 拆出（2026-07-11 用户指示独立成篇）；该 SPEC 保留目录逻辑与缓存保真范围

## Goal

改善 Codex Radar 与 Claude Radar 对站点模型增减、改名、重排的适配能力：

1. Codex 目录引入完整性门禁，杜绝局部读取误删在售模型。
2. 两侧对"当前选中模型被下架"给出定向通知与即时切换评估。
3. 目录换代时通知合并为摘要，消除逐条弹窗风暴。
4. Claude 自动切换候选过滤已停更/禁用模型。
5. Claude 映射表跟随站点改名与重排，同时保护用户手动自定义。

不改变模型身份语义（Codex 语义 key、Claude 站点 m-key）、目录/映射文件的既有列语义（只新增列）、`DeleteAfterMissingCount`/`ModelDeleteMissingThreshold` 的删除阈值语义和发布策略。

## Findings

### F1（修复）：Codex 目录对模型增减的适配缺陷

1. **无完整性门禁（核心）**：`CodexRadarModelCatalog.MergeAndSave` 有三个喂入点——公开 JSON（`Core/CodexRadarForm.cs` 约 7317 行）与 HTML 兜底（约 6418、6650 行）。任何非空发现列表都会让未出现的模型 `MissingCount++`，3 次即从目录删除。HTML 对比表可能只含当天有数据的子集、JSON 也可能处于站点半发布状态——连续 3 次局部读取会**误删仍然在售的模型**。对照：Claude 侧有 `IsCompleteModelCatalog`（`Core/ClaudeRadarReader.cs` 约 1365 行：ok 标志 + 数量一致 + key 非空唯一）门禁，不完整时跳过缺失计数；Codex 完全没有等价机制。
2. **选中模型下架无定向处理**：模型 Unavailable/Deleted 有通用通知，但不指明它是当前选中模型；`RadarClockAutoSwitchModelEnabled=false` 时用户停留在死模型上（表盘变红）且得不到解释；开启时也要等到时钟周期过期才切换。
3. **换代通知风暴**：目录换代（如 5.5→5.6）时一次合并产生大量 Added/Deleted 事件，逐条弹通知。
4. **已符合项（不改）**：`TryFindRadarClockAutoSwitchTarget`（约 5617 行）已过滤 `Available=false`，自动切换不会选中已下架模型。

### F2（修复）：Claude 模型映射对增减/改名/重排的适配缺陷

1. **自动切换候选不过滤模型状态**：独立窗口 `TryFindClaudeClockAutoSwitchTarget`（`Core/ClaudeRadarForm.cs` 约 885-924 行）把全部 `ModelMetrics` 收为候选，不排除 `HistoricalOnly` 与映射表中 `Enabled=false`/`deleted` 的条目——可能自动切换到已停更的历史模型。共享窗口的 `TryFindClaudeSharedClockAutoSwitchTarget`（`Core/CodexRadarForm.cs` 约 5646 行）候选来自 `ClockModelCandidates`，是否含历史模型需实现前核实，缺过滤则一并补。
2. **DisplayName 永不跟随站点改名**：`ApplyModelMapUpdate`（`Core/ClaudeRadarReader.cs` 约 1840 行）只在本地名为空时写入站点名——因手动编辑器（`ClaudeRadarModelMapEditorForm`）允许用户自定义名称，不能盲目覆盖，但结果是站点改名后本地标签永久停留在旧名，且无任何提示。
3. **sort_order 只在新增时赋值**：站点重排后本地顺序不跟随（2026-07-11 实测 `claude-radar-model-map.ini` 存在 m9 与 m2 同为 2、m10 与 m6 同为 6 的重复序号）。
4. **选中 m-key 被删无定向通知**：与 F1-2 同类。

### F3（仅记录）：不修的既有取舍

1. Codex 目录换代仍需发版更新 `DefaultModelKey`/种子列表/`IsDefaultModelKey` 清单（动态化收益不足，维持硬编码）。
2. Claude m-key 身份完全依赖站点键稳定性：站点若复用旧 m-key 指向新模型，本地 IQ 历史与评级归属会串。
3. Claude deleted 条目永留映射表（`Enabled=false`）属保历史的主动取舍；Codex 侧达到删除阈值即从目录移除，两侧策略不同但各自成立。

## Implementation Plan

### Phase 0: Baseline Checks

```powershell
rg -n "Version = \"1\.0\.5\.0[0-9]\"" Core\ProductIdentity.cs
rg -n "completeCatalog|CompleteCatalog" Core\CodexRadarModelCatalog.cs Core\CodexRadarForm.cs
rg -n "source_display_name" Core\ClaudeRadarReader.cs
```

后两条实施前应无结果；确认无其他代理在改同一批文件。

### Phase 1: Codex 目录增减适配（F1）

1. **完整性门禁**：`MergeAndSave` 增加 `bool completeCatalog` 参数（或等价重载）：

   - `completeCatalog=false` 时只执行"已见模型刷新（Available=true、MissingCount=0、LastSeenUtc 更新）+ 新模型加入"，**跳过**未出现模型的 `MissingCount++`/Unavailable/Deleted 整段。
   - 公开 JSON 路径的完整性判定仿照 Claude 侧 `IsCompleteModelCatalog`：提取的目录列表非空、key 唯一、与 `model_iq` 源内模型数一致时视为完整。
   - HTML 兜底两处调用（约 6418、6650 行）恒传 `completeCatalog=false`——对比表天然是子集，只允许它补充新模型与刷新已见，不允许它衰减目录。

2. **选中模型下架定向处理**：`ShowCodexRadarModelCatalogNotifications` 中，若 `update.Unavailable`/`update.Deleted` 含 `NormalizeModelKey(CurrentSettings.CodexRadarModelKey)`，通知文案明确"当前选中模型"；Deleted 且 `RadarClockAutoSwitchModelEnabled=true` 时清空 `lastRadarClockAutoSwitchSignature` 并立即触发一次 `ApplyRadarClockAutoSwitchIfNeeded`（不等周期过期）。

3. **换代通知合并**：单次 `CodexRadarModelCatalogUpdate` 的 Added+Unavailable+Deleted 事件总数 ≥ 4 时，合并为一条"模型目录换代"摘要通知（含各类数量与至多 3 个代表模型名），不再逐条弹出。阈值以常量定义。文案组装提取为纯函数以便自测。

约束：

- 目录文件格式不变；`DeleteAfterMissingCount=3` 语义不变（只是仅在完整目录下计数）。
- F1-4 已符合项不改动。

自测（并入目录自测入口，若 1.0.5.02 已建 `CodexRadarModelCatalog.RunSelfTest` 则扩展之）：

1. 完整目录缺某模型 → MissingCount++；不完整目录缺同一模型 → MissingCount 不变。
2. 不完整目录含新模型 → 正常加入且 Added 事件产生。
3. 摘要合并：构造 5 个事件的 update，断言分类计数与代表模型名正确。

验收：

```powershell
rg -n "completeCatalog|CompleteCatalog" Core\CodexRadarModelCatalog.cs Core\CodexRadarForm.cs
```

三个 `MergeAndSave` 调用点均显式传入完整性实参；HTML 两处为 false。

### Phase 2: Claude 模型映射增减适配（F2）

1. **自动切换候选过滤**：`TryFindClaudeClockAutoSwitchTarget` 跳过 `metric.HistoricalOnly` 的候选，并对照模型映射表跳过 `Enabled=false` 或 `status=deleted` 的 source_key；共享窗口 `TryFindClaudeSharedClockAutoSwitchTarget` 实现前核实 `ClockModelCandidates` 来源，缺过滤则同规则补齐。

2. **站点改名跟随**：映射表新增第 11 列 `source_display_name`（文件头注释同步），记录最近一次站点名：

   - 读取兼容 10 列旧行（缺列视为空串）。
   - 站点名变化时：若本地 `display_name` 等于旧 `source_display_name`（用户未自定义过）→ 跟随更新两列；否则只更新 `source_display_name` 并产生 Renamed 事件通知，本地名保留用户值。

3. **sort_order 重排跟随**：仅 `completeModelCatalog=true` 时，把本次出现的条目按 metrics 顺序重写 `sort_order`（0..n-1），未出现条目按原相对顺序排在其后。消除现存的重复序号漂移。

4. **选中模型下架定向通知**：`ClaudeRadarModelCatalogEventKind.Deleted`/`TemporarilyMissing` 命中 `ClaudeRadarModelKey`（含共享窗口使用场景）时，通知文案明确"当前选中模型"。

约束：

- 手动编辑器的用户自定义名永不被自动覆盖——跟随更新只发生在"本地名==旧站点名"的未自定义状态。
- m-key 身份语义、deleted 条目留存策略不变（F3 已知限制）。
- 映射文件除新增一列外格式不变；旧版本程序读到 11 列文件的行为需实现前核实（多余列应被忽略；若不然，改为文件版本号升级方案并在 GoalSpec 说明）。

自测（扩展 `ClaudeRadarReader` 既有映射自测）：

1. HistoricalOnly 候选不被 `TryFindClaudeClockAutoSwitchTarget` 选中。
2. 改名三场景：未自定义 → 双列跟随；已自定义 → 仅 `source_display_name` 更新 + Renamed 事件；名称未变 → 无事件。
3. 完整目录重排后 sort_order 与站点顺序一致、未出现条目排尾；10 列旧行读取兼容。

验收：

```powershell
rg -n "source_display_name" Core\ClaudeRadarReader.cs Core\ClaudeRadarModelMapEditorForm.cs
rg -n "HistoricalOnly" Core\ClaudeRadarForm.cs
```

第一条命中读写与表头注释；第二条命中候选过滤。人工验收：`claude-radar-model-map.ini` 表头含 `source_display_name`、重复 sort_order 在下次完整读取后消失。

### Phase 3: 已知限制文档（F3，不改代码）

在 `Docs/CodexRadar-Architecture.md` 与 `Docs/Codex-ClaudeRadar-Architecture.md` 的模型目录/映射章节追加 F3 的三条已知限制。

验收：

```powershell
rg -n "m-key|换代仍需发版" Docs\CodexRadar-Architecture.md Docs\Codex-ClaudeRadar-Architecture.md
```

### Phase 4: 版本与文档同步

实现通过后版本提升到目标版本，同步：

- `Core/ProductIdentity.cs`、`AGENTS.md`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`：目录/映射条目补述完整性门禁、`source_display_name`、通知合并
- `Docs/Maintenance/CHANGELOG.jsonl` change + deploy 记录
- `Docs/Technical/INDEX.jsonl` 登记本 SPEC 与对应 GoalSpec

## Verification Matrix

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-model-lifecycle-adaptation.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-model-lifecycle-adaptation.exe --test
.\_build\DesktopCodexAssistant-arm64-model-lifecycle-adaptation.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-model-lifecycle-adaptation.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-model-lifecycle-adaptation.exe --test-radar-display-lifecycle --iterations 100
```

全部通过后按正式部署流程（Release 构建复测 → 备份 → D/E 双路径部署 → 重启 D 实例 → SHA256/版本三方核对 → CHANGELOG deploy 记录）。

## Acceptance Summary

| 项 | 验收条件 |
| --- | --- |
| F1 完整性门禁 | 三个 MergeAndSave 调用点显式传完整性实参、HTML 恒 false；"不完整目录不衰减"自测通过 |
| F1 定向处理 | 选中模型下架通知含"当前选中"；Deleted+auto-switch 开启时即时触发切换评估 |
| F1 通知合并 | ≥4 事件合并为摘要通知，分类计数自测通过 |
| F2 候选过滤 | HistoricalOnly/disabled/deleted 不被自动切换选中，自测通过 |
| F2 改名跟随 | 三场景自测通过；用户自定义名不被覆盖 |
| F2 重排跟随 | 完整目录后 sort_order 无重复且与站点一致；10 列旧行兼容 |
| F3 文档 | 两份架构文档出现已知限制条目 |
| 发布 | 目标版本三方（Release/D/E）SHA256 与版本一致，D 实例重启后 Responding |
