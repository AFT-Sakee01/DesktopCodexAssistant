# Radar Claude Adaptation And Clock Ladder SPEC (Split 2/2)

## Metadata

- Document: `Codex-RadarClaudeAdaptationAndClockLadder-SPEC-v1.0.5.03-20260711-103509.md`
- Generated model: Claude Fable 5
- Timestamp local: `2026-07-11T10:35:09+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.03`
- Target implementation version: `1.0.5.04` 或 `1.0.5.05`（与 SPEC-1 并行，按完成顺序取号，见并行执行契约）
- Status: approved（含 C2 配色阶梯，用户已于 2026-07-11 批准；全部 Phase 可实施）
- Source: 自 `Codex-RadarHardeningConsolidated-SPEC-v1.0.5.03-20260711-094500.md` 拆分（第二部分 Claude 适配 + 第三部分时钟遗留项）

## Goal

Claude 模型映射的增减/改名/重排适配（自动切换候选过滤、source_display_name 改名跟随、sort_order 重排、选中模型下架定向通知），以及时钟表盘两项遗留：`HasSecondRunSuffix` 的 n 系后缀兼容、状态色阶梯后移一个窗口（已批准，含同窗口二次发布注意事项）。约束沿用来源 SPEC 原文。

## 并行执行契约

- **文件所有权（本 SPEC 独占写）**：`Core/ClaudeRadarReader.cs`、`Core/ClaudeRadarForm.cs`、`Core/ClaudeRadarModelMapEditorForm.cs`、`Core/RadarClockDial.cs`。
- **禁止触碰**：`Core/CodexRadarModelCatalog.cs`、`Core/CodexRadarForm.cs`（含全部 partial）、`Settings/WidgetSettings.cs`（归 SPEC-1）。涉及 `CodexRadarForm.cs` 的单点（共享窗口候选过滤，以及 Claude 目录事件通知若调用点位于该文件）已划归 SPEC-1；实现中发现新的跨界点时记录到 GoalSpec 留给 SPEC-1，不得越界修改。
- 与 SPEC-1（`Codex-RadarCodexDataHardening-SPEC-v1.0.5.03-20260711-103509.md`）并行实施；**部署串行**：先完成者取版本 `1.0.5.04`，后完成者 rebase 后取 `1.0.5.05`。共享文档冲突由后完成者合并解决。
- 例外说明：`RadarClockDial.RunSelfTest` 的调用点在 1.0.5.03 已接线，本 SPEC 只改 `RadarClockDial.cs` 内部即可，无需动调用方。

## Findings（Claude 适配 F1-F3；F1 的 Codex 部分已归 SPEC-1）

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

### Phase B0: Baseline Checks

```powershell
rg -n "Version = \"1\.0\.5\.0[0-9]\"" Core\ProductIdentity.cs
rg -n "source_display_name" Core\ClaudeRadarReader.cs
rg -n "HistoricalOnly" Core\ClaudeRadarForm.cs
```

第一条实施前应无结果，第二条实施前不应命中候选过滤逻辑；确认无其他代理在改同一批文件。

### Phase B1: Claude 模型映射增减适配（F2）

1. **自动切换候选过滤**：`TryFindClaudeClockAutoSwitchTarget` 跳过 `metric.HistoricalOnly` 的候选，并对照模型映射表跳过 `Enabled=false` 或 `status=deleted` 的 source_key。共享窗口 `TryFindClaudeSharedClockAutoSwitchTarget` 的候选过滤已划归 SPEC-1 Phase A9（`CodexRadarForm.cs` 文件所有权在 SPEC-1），本 SPEC 不触碰该文件。

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

### Phase B2: 已知限制文档（F3，不改代码）

在 `Docs/CodexRadar-Architecture.md` 与 `Docs/Codex-ClaudeRadar-Architecture.md` 的模型目录/映射章节追加 F3 的三条已知限制。

验收：

```powershell
rg -n "m-key|换代仍需发版" Docs\CodexRadar-Architecture.md Docs\Codex-ClaudeRadar-Architecture.md
```

## 时钟表盘遗留项（RadarClockDialConsolidation 已于 1.0.5.03 执行；以下为执行后追加）

### Phase C1: 数据标签 "n" 后缀徽标兼容（确定项）

站点已把晚间批次标签从 `pm` 改为 `n`（实测 `7.10_n`；`TryReadCodexModelIqDataWindow` 的日期解析已支持 n→12 点窗口，无需改动）。但已实现的 `Core/RadarClockDial.cs` 中 `HasSecondRunSuffix` 只认 `pm2/pm_2/pm-2`——晚间第二次测试的"2"徽标在 `n2/n_2/n-2` 标签下会丢。

实现：`HasSecondRunSuffix` 同等识别 n 系后缀；`RadarClockDial.RunSelfTest` 补断言 `n2`→true、`n_2`→true、`n`→false、`pm2`→true（回归）。

观察项：若站点未来同日同时出现 `pm` 与 `n` 两个批次（三窗口制），12 小时周期假设需重估，本 SPEC 不预做。

验收：

```powershell
rg -n "n2|HasSecondRunSuffix" Core\RadarClockDial.cs
```

自测断言通过；无渲染基准变化（徽标仅在 n2 数据日出现）。

### Phase C2: 状态色阶梯整体后移一个窗口（V4，用户已于 2026-07-11 批准）

实测（2026-07-11）站点发布节奏是"窗口 N 的数据在窗口 N+1 甚至更晚发布"（7.10 晚间批次次日 08:42 晨才到），导致"绿=本窗口有数据"几乎不可达——绿点亮着而中心文字长期黄色，凌晨至清晨常态红色。

实现：修改 `RadarClockDial.ComputePhase` 阈值，状态按"落后窗口数"分档：

| batchTime 相对边界 | 现规则 | V4 规则 |
| --- | --- | --- |
| ≥ boundary（本窗口） | CurrentCycle 绿 | CurrentCycle 绿 |
| ≥ previousBoundary（落后 1） | WaitingCycle 黄 | CurrentCycle 绿 |
| ≥ previousBoundary − cycle（落后 2） | MissedCycle 红 | WaitingCycle 黄 |
| 更早（落后 ≥3） | MissedCycle 红 | MissedCycle 红 |

黄档与现有 auto-switch 触发条件（`batch < previousBoundary`）天然对齐；弧线/警告环几何随档位平移不另改。`RunSelfTest` 真值表（12h+24h）同步更新；两窗渲染样本重新截取作为新基准，差异在 CHANGELOG 声明。

验收：真值表自测通过；`--render-codexradar` 与 `--render-clauderadar` 中黄/红出现时机符合新档位定义。

**同窗口二次发布注意事项（用户 2026-07-11 提示）**：Codex 站点在一个 12 小时窗口内可能发布两次（第二次测试 `n2`/`pm2`，或数据修正）。同窗口的第二次发布不产生新窗口——`batchTime`（窗口起点）不变，阶梯档位不受影响；变化的只有绿点（刷新标记随内容变化移动）和"2"徽标（Phase C1 的 n 系兼容负责）。`RunSelfTest` 增加断言：同窗口标签从 `n` 变为 `n2`、内容变化后，`ComputePhase` 结果不变，仅 `SecondRunBadge` 翻转为 true。


### Phase C3: 版本与文档同步

实现通过后按并行契约取号（`1.0.5.04` 或 `1.0.5.05`），同步：

- `Core/ProductIdentity.cs`、`AGENTS.md`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`：`internal_api.radar_clock_dial`（ComputePhase 新档位、HasSecondRunSuffix n 系）与 Claude 映射条目（source_display_name、候选过滤）
- `Docs/Codex-ClaudeRadar-Architecture.md`：已知限制条目（Phase B2）与时钟档位新语义
- `Docs/Maintenance/CHANGELOG.jsonl` change + deploy 记录（C2 视觉差异逐项声明，渲染样本重截为新基准）
- `Docs/Technical/INDEX.jsonl`：登记本 SPEC 与对应 GoalSpec

## Verification Matrix

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-claude-adaptation-clock-ladder.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-claude-adaptation-clock-ladder.exe --test
.\_build\DesktopCodexAssistant-arm64-claude-adaptation-clock-ladder.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-claude-adaptation-clock-ladder.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-claude-adaptation-clock-ladder.exe --test-radar-display-lifecycle --iterations 100
```

渲染样本核对：`--render-codexradar` 与 `--render-clauderadar` 全量重截——C2 档位变更导致黄/红出现时机变化属预期差异，逐项与新真值表核对；速蹬倒计时两张样本必须无变化。人工验收：`claude-radar-model-map.ini` 表头含 `source_display_name`、重复 sort_order 在下次完整读取后消失。

全部通过后按正式部署流程（与 SPEC-1 串行取号部署，见并行执行契约）。

## Acceptance Summary

| 项 | 验收条件 |
| --- | --- |
| 适配F2 候选过滤 | HistoricalOnly/disabled/deleted 不被自动切换选中，自测通过 |
| 适配F3 文档 | 两份架构文档出现已知限制条目 |
| C1 n 后缀徽标 | HasSecondRunSuffix 识别 n 系后缀，自测断言通过 |
| C2 配色阶梯（若批准） | ComputePhase 新档位真值表自测通过，渲染样本按新基准重截 |
| C2 二次发布 | 同窗口 n 变 n2 二次发布 Phase 不变、徽标翻转的自测断言通过 |
| 发布 | 按并行契约取号，三方（Release/D/E）SHA256 与版本一致，D 实例重启后 Responding |
