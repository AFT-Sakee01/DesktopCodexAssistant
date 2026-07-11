# Radar Codex Data Hardening SPEC (Split 1/2)

## Metadata

- Document: `Codex-RadarCodexDataHardening-SPEC-v1.0.5.03-20260711-103509.md`
- Generated model: Claude Fable 5
- Timestamp local: `2026-07-11T10:35:09+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.03`
- Target implementation version: `1.0.5.04` 或 `1.0.5.05`（与 SPEC-2 并行，按完成顺序取号，见并行执行契约）
- Status: approved（全部 Phase 可实施，无待批项）
- Source: 自 `Codex-RadarHardeningConsolidated-SPEC-v1.0.5.03-20260711-094500.md` 拆分（第一部分 A0-A8 + Codex 侧适配 A9）

## Goal

Codex 侧数据层与目录层一次加固：目录合并裁决修复、默认模型迁移补跑、标签大小写、缓存内容签名持久化（绿点跨重启）、空 key 哨兵与 CheckedAt、未知窗口保真、额度重置与幻影池的确定性判别（新生窗口算术，无等待兜底）、目录完整性门禁、选中模型下架定向处理与换代通知合并。约束沿用来源 SPEC 原文。

## 并行执行契约

- **文件所有权（本 SPEC 独占写）**：`Core/CodexRadarModelCatalog.cs`、`Core/CodexRadarForm.cs`（含全部 partial）、`Core/CodexRadarForm.CodexUsage.cs`、`Settings/WidgetSettings.cs`、`Core/RadarSoftwareModeController.cs`。
- **禁止触碰**：`Core/ClaudeRadarReader.cs`、`Core/ClaudeRadarForm.cs`、`Core/ClaudeRadarModelMapEditorForm.cs`、`Core/RadarClockDial.cs`（归 SPEC-2）。
- 与 SPEC-2（`Codex-RadarClaudeAdaptationAndClockLadder-SPEC-v1.0.5.03-20260711-103509.md`）可在各自分支/worktree 并行实施；**部署串行**：先完成者取版本 `1.0.5.04` 并走完整正式部署，后完成者 rebase 后取 `1.0.5.05` 再部署。共享文档（`AGENTS.md`、`CHANGELOG.jsonl`、`INTERFACE_INDEX.jsonl`、`Docs/Technical/INDEX.jsonl`）由后完成者合并时解决冲突。

## 第一部分：目录与缓存加固（原 ModelCatalogHardening SPEC，Findings F1-F10）

### F1（修复）：目录重复记录合并的可用性判定与意图相反

`Core/CodexRadarModelCatalog.cs` `LoadModels` 的重复 key 合并分支（约 96-123 行）注释写 "Prefer an available/newer record"，实现却是：

```csharp
existing.Available = existing.Available || candidate.Available;
existing.MissingCount = Math.Min(existing.MissingCount, candidate.MissingCount);
```

后果：旧记录 available、较新记录 unavailable（MissingCount=2）时，合并结果为 available 且 MissingCount=0——站点已下架的模型被复活，且 `DeleteAfterMissingCount = 3` 的累计被清零，清理最多延迟 3 个刷新周期。

### F2（修复）：默认模型迁移的空 key 路径漏网

`Settings/WidgetSettings.cs` 中 `ApplyCodexRadarDefaultModelMigration`（settingsVersion < 62 分支，约 1978 行）在 `settings.Normalize()` 之前运行，只匹配已解析出的 key 字符串。若旧配置文件写有 `CodexRadarModelKey=`（空值），迁移时 key 为空不命中，之后 Normalize 才经 `LegacyKeyFromVersion` 落到 `gpt_55_xhigh`（PreviousDefaultModelKey）——用户被留在已归档模型上，未迁往 `gpt_56_sol_medium`。

### F3（修复）：GetDisplayLabel 从 key 生成标签时 family 段全小写

`Core/CodexRadarModelCatalog.cs` `GetDisplayLabel`（约 309-329 行）：站点 label 缺失时，`gpt_57_nova_high` 会显示为 `GPT-5.7 nova high`，与种子标签 `GPT-5.6 Sol medium` 的大小写风格不一致。

### F4（仅记录）：版本号解析假设主版本为一位数

`GetDisplayLabel` 与 `CodexRadarForm.EvenRow.cs` 的 `FormatCodexRadarCurrentModelShortLabel` 均使用 `^gpt_([0-9])([0-9]+)_` 拆分主/次版本；假想 `gpt_104_high`（GPT-10.4）会拆成 "GPT-1.04"。同时 `NormalizeModelKey` 的折叠不可逆（`gpt_5_12` 与 `gpt_51_2` 都归一到 `gpt_512`）。当前站点模型不受影响。

### F5（仅记录）：IsDefaultModelKey 不再涵盖 5.5/5.4 的一次性通知噪音

目录文件被删除重建后 `CreateDefaultModels` 只含 5.6 五个种子；若站点当时仍列出 5.5 模型，`MergeAndSave` 会对它们各弹一次"新模型加入检测列表"通知。一次性、无功能影响。

### F6（修复，已实证）：绿点（IQ 刷新标记）跨重启重置

绿点时间由 `PreserveCodexModelIqRefreshTimeIfContentUnchanged`（`Core/CodexRadarForm.cs` 约 6006 行）维护：新抓取与上一份快照比对**内容签名**（`BuildCodexModelIqContentSignature`，约 6032 行），相同才保留旧 `ModelIqRefreshedAtLocal`。会话内上一份是上次抓取结果，比对成立；**重启后上一份换成 `LoadCodexRadarCache` 读回的快照，而缓存写入/读取往返不是签名保真的**：

- 写入器（约 10620 行）无视 Known 标志一律写值：`DataWindowHour` 未知写 0、`DataLabel` 未知写格式化兜底、`NormalLow/High` 写原始值、`DisplayMaxScore` 未知写计算兜底；
- 读取器（约 10452 行）强行富化：窗口小时解析成功即 `WindowKnown=true`、`Passed` 缺失按分数估算且 `PassedKnown=true`、`EfficiencyKnown/InputKnown` 用 `>0` 重新推导；
- 签名把 Known 标志编入（`known ? 值 : -1/""`）。

只要任一字段往返不保真，重启后首抓即被误判"内容变化"→ 绿点 = 抓取时刻 ≈ 重启时刻；此后同会话内签名恢复一致。实证：运行日志 `2026-07-10 23:32:33 Starting`（重启），缓存 `RefreshedUtc=23:32:54`（重启后 21 秒首抓），其后至 00:40 多次保存未再移动——会话内保留正常、跨重启必失效。

### F7（修复）：空 key 缓存前缀退化 + 构造函数 CheckedAt 死逻辑

- `GetLegacyCodexRadarCachePrefix`（约 10732 行）对空 modelKey 生成 `Model..` 退化前缀（实际缓存文件已存在 `Claude.Model..SavedUtc` 条目）：ClaudeRadarModelKey 为空（自动/默认）时写入该条目，之后切到具体模型旧条目成孤儿；将来 key 再变空时会读到与当时站点默认模型不符的旧数据。
- 构造函数约 715-717 行用 `CheckedAtKnown` 初始化 `lastCodexRadarStatusAttemptLocal`，但 `LoadCodexRadarCache` 从不设置 CheckedAt——该分支恒为 false（死逻辑）。后果：重启后 LAST 时钟模式显示 `--:--` 直到首抓。

### F8（修复）：未知数据窗口被"上午化"

写入器把 `WindowKnown=false` 持久化为 `DataWindowHour=0`，读回后变成 `WindowKnown=true`、上午窗口——"未知"信息在往返中丢失并变成断言。当前表盘 batchTime 计算对 unknown 与 0 等效处理，故无可见显示差异，但它是 F6 签名失真的来源之一，且任何将来依赖 `ModelIqDataWindowKnown` 的逻辑都会被误导。

### F9（仅记录）：缓存机制既有取舍

1. 缓存读回时 `Passed` 缺失会按分数估算并标记 `PassedKnown=true`（估算值当真值展示）。
2. `gpt_5_6_*` 旧身份前缀的缓存条目在 key 归一化换代后不可读，靠 7 天保留期自然清理（一次性升级代价）。
3. `SavedUtc` 超过 `CodexModelCacheRetentionDays`（7 天）整条丢弃：长时间未运行后重启，绿点/IQ 空白直至首抓（设计取舍）。
4. `CheckedAt` 之外的速蹬窗口状态（open/closed_at）不入缓存：重启后倒计时表盘要等首抓才恢复。
5. 缓存写入的原子性（tmp + `File.Replace`）、同进程静态锁、过期清理经审查无问题。

### F10（修复，已实证）：额度环无法区分"统一重置"与"干扰池样本"

`quota-decision-history.jsonl` 实证了两类事件在现有规则下不可区分：

- **干扰池闪跳**：`wham/usage` 会在多个窗口池身份之间不稳定返回——2026-07-11 00:50:06 provider 样本携带 5h reset=04:11:51、余额 97/99，00:53:10 又回到 5h reset=03:04:22、余额 8/60（07-10 09:17、10:50、11:51 同型）。`balance_increased_or_reset` 规则对上涨照单全收，环面闪跳。
- **真实统一重置**：2026-07-10 深夜 Codex 统一重置，08:04:30 起 provider 样本携带全新窗口身份（5h reset=now+5h、weekly reset=now+7d、used=0）且**此后所有样本持续同一新身份**——这次 weekly 满环是正确的。

两者的可判别特征：**干扰池样本的新身份只短暂出现就被旧身份样本打断（00:50→00:53 仅 3 分钟）；真实重置的新身份此后持续存在且不再出现旧身份**。此外真实统一重置通常伴随站点重置事件（`quota-reset-state.ini` 已有 `LastRadarResetEventUtc` 通道）。现有保护（zero-drop/stale/duplicate）全部只防"变少"方向，缺少上涨方向的判别。

## Implementation Plan

### Phase A0: Baseline Checks

确认无其他代理在改同一批文件，且版本仍为 `1.0.5.03`：

```powershell
rg -n "Version = \"1\.0\.5\.03\"" Core\ProductIdentity.cs
rg -n "existing.Available \|\| candidate.Available" Core\CodexRadarModelCatalog.cs
rg -n "ApplyCodexRadarDefaultModelMigration" Settings\WidgetSettings.cs
rg -n "ContentSignature|CheckedAtUtc|Model\.default" Core\CodexRadarForm.cs
```

### Phase A1: 目录重复记录合并改为"较新记录整体为准"（F1）

`Core/CodexRadarModelCatalog.cs` `LoadModels` 合并分支改为以下裁决规则，并把裁决提取为可测的内部静态方法：

```csharp
internal static void MergeDuplicateCatalogRecord(CodexRadarModelInfo existing, CodexRadarModelInfo candidate)
{
    bool candidateWins =
        candidate.LastSeenUtc > existing.LastSeenUtc ||
        (candidate.LastSeenUtc == existing.LastSeenUtc &&
         candidate.Available && !existing.Available);
    if (candidateWins)
    {
        existing.Label = candidate.Label;
        existing.Available = candidate.Available;
        existing.MissingCount = candidate.MissingCount;
        existing.LastSeenUtc = candidate.LastSeenUtc;
    }
}
```

约束：

- 时间戳新者整体覆盖 `Label` / `Available` / `MissingCount` / `LastSeenUtc` 四个字段，不再按字段取乐观值。
- 时间戳相等（含双方均为 `DateTime.MinValue` 的旧文件）时，优先 available 记录；双方可用性也相同则保留先出现的记录，维持文件顺序稳定。
- `seen` 去重、`models.Add(candidate)` 首次插入路径、文件读写格式一律不变。
- 不改 `MergeAndSave` 的站点发现合并逻辑（那是另一条链路，语义正确）。

自测：在 `RadarSoftwareModeController.RunSelfTest()` 末尾仿照 `ClaudeRadarClockAutoSwitchSelector.RunSelfTest()` 的先例增加 `CodexRadarModelCatalog.RunSelfTest()`，至少覆盖：

1. 旧记录 available（LastSeenUtc=T1）+ 新记录 unavailable、MissingCount=2（LastSeenUtc=T2>T1）→ 合并后 unavailable、MissingCount=2、LastSeenUtc=T2。
2. 旧记录 unavailable + 新记录 available（时间戳更新）→ 合并后 available、MissingCount 取新记录值。
3. 双方 LastSeenUtc 均为 `DateTime.MinValue`，一方 available → 合并后 available。
4. 双方时间戳、可用性均相同 → 保留 existing 的 Label（顺序稳定）。

验收：

```powershell
rg -n "existing.Available \|\| candidate.Available|Math.Min\(existing.MissingCount" Core\CodexRadarModelCatalog.cs
rg -n "MergeDuplicateCatalogRecord|CodexRadarModelCatalog.RunSelfTest" Core
```

第一条应无结果；第二条应命中 `CodexRadarModelCatalog.cs` 的定义与 `RadarSoftwareModeController.cs` 的调用。

### Phase A2: 默认模型迁移在 Normalize 后补跑（F2）

`Settings/WidgetSettings.cs` 加载流程中，在 `settings.Normalize()`（约 1992 行）之后、`saveAfterMigration` 落盘判断之前，补跑一次：

```csharp
if (settingsVersion > 0 && settingsVersion < 62)
{
    ApplyCodexRadarDefaultModelMigration(settings);
    settings.CodexRadarModelVersion =
        CodexRadarModelCatalog.LegacyVersionFromKey(settings.CodexRadarModelKey);
}
```

约束：

- 保留 Normalize 之前的原迁移调用（显式 key 的主路径行为不变，幂等）。
- 补跑后必须用 `LegacyVersionFromKey` 重新同步 `CodexRadarModelVersion`，保持 Normalize 建立的 key→version 一致性。
- 迁移仍只匹配 `PreviousDefaultModelKey`（`gpt_55_xhigh`），不得触碰用户显式选择的其他归档模型。
- `settingsVersion >= 62` 的文件不受任何影响。

自测：在 `RunSettingsLayoutSelfTest`（现有 `AssertLayout` 区域，约 5035 行 `codex56Default` 场景旁）增加空 key 场景：

```csharp
WidgetSettings emptyKeyLegacy = CreateDefaults();
emptyKeyLegacy.CodexRadarModelKey = string.Empty;
emptyKeyLegacy.CodexRadarModelVersion = CodexRadarModelVersion.Gpt55;
emptyKeyLegacy.Normalize();
ApplyCodexRadarDefaultModelMigration(emptyKeyLegacy);
AssertLayout(
    string.Equals(emptyKeyLegacy.CodexRadarModelKey,
        CodexRadarModelCatalog.DefaultModelKey, StringComparison.OrdinalIgnoreCase),
    "empty legacy model key should reach GPT-5.6 default after normalize + migration re-run");
```

验收：

```powershell
rg -n "ApplyCodexRadarDefaultModelMigration" Settings\WidgetSettings.cs
```

应命中 3 处调用（Normalize 前、Normalize 后补跑、自测），外加方法定义与既有 `codex56Default` 自测；且 `--test-settings-bindings` 通过。

### Phase A3: GetDisplayLabel family 段首字母大写（F3）

`Core/CodexRadarModelCatalog.cs` `GetDisplayLabel` 的 key 回退分支：对第三捕获组按 `_` 拆段后，凡不属于努力档集合 `{ xhigh, ultra, high, medium, low }` 的段做首字母大写，努力档保持全小写。

预期输出：

| key | 现状 | 目标 |
| --- | --- | --- |
| `gpt_56_sol_medium` | `GPT-5.6 sol medium` | `GPT-5.6 Sol medium` |
| `gpt_56_terra_medium` | `GPT-5.6 terra medium` | `GPT-5.6 Terra medium` |
| `gpt_57_nova_high` | `GPT-5.7 nova high` | `GPT-5.7 Nova high` |
| `gpt_55_xhigh` | `GPT-5.5 xhigh` | `GPT-5.5 xhigh`（不变） |

约束：

- 站点提供非空 label 时原样返回的优先级不变（首字大写只作用于 key 回退路径）。
- 使用 `ToUpperInvariant` / `ToLowerInvariant`，不引入区域敏感转换。
- `NormalizeModelKey`、`FormatCodexRadarCurrentModelShortLabel` 的短标签（`5.6SM` 等）不受影响。

自测：并入 Phase 1 的 `CodexRadarModelCatalog.RunSelfTest()`，断言上表 4 个键的输出。

验收：自测断言通过；`--render-codexradar` 样本中 `LLM:` 短标签与设置窗口模型下拉标签无回归（人工抽查两张 PNG）。

### Phase A4: 缓存内容签名持久化（F6）

`Core/CodexRadarForm.cs`：

1. `CodexRadarSnapshot` 新增字段 `public string ModelIqCachedContentSignature`，`Clone()` 与 `CreateDefault()` 同步（默认空串）。
2. `SaveCodexRadarCache` 新增写入：

```csharp
values[prefix + "ContentSignature"] = BuildCodexModelIqContentSignature(snapshot);
```

3. `LoadCodexRadarCache` 读回：

```csharp
snapshot.ModelIqCachedContentSignature =
    GetCacheValue(values, prefix + "ContentSignature", string.Empty);
```

4. `PreserveCodexModelIqRefreshTimeIfContentUnchanged` 的来源侧签名优先用存储值：

```csharp
string sourceSignature = !string.IsNullOrEmpty(source.ModelIqCachedContentSignature)
    ? source.ModelIqCachedContentSignature
    : BuildCodexModelIqContentSignature(source);
```

目标侧（新抓取）签名永远现算。这样比较的两端都是"抓取时刻的原始签名"，缓存往返富化被整体绕过；程序关闭期间站点内容真变时签名自然不同，绿点照常更新——语义不变。

5. 诊断日志：当 `source.ModelIqCachedContentSignature` 非空且与目标签名不匹配时，`Program.LogInfo` 记录一行两签名（内容真实变化时才触发，频率等于站点数据变化频率，可接受）。

约束：

- 只新增 `ContentSignature` 键；既有键的写入值和读取逻辑一律不动（F8 除外）。
- 旧缓存文件无该键 → 读回空串 → 回退现算（行为与现状相同），新版本首次保存后即自愈。
- Codex 与共享窗口 Claude family 共用同一套函数，修复同时生效；独立 ClaudeRadarForm 的缓存另有实现，若存在同类保留机制则按同方案同步修复（实现时以 `rg "RefreshedUtc" Core\ClaudeRadarForm.cs Core\ClaudeRadarReader.cs` 核实，有则改，无则在 GoalSpec 中说明）。

自测（扩展 `RunCodexModelIqRefreshMarkerSelfTest`）：

1. 快照 A（RefreshedAt=T0）计算签名存入模拟读回快照 A'（人为翻转一个 Known 标志模拟读取器富化，使 A' 现算签名 ≠ 存储签名）；新抓取 B 与 A 内容相同 → `Preserve(B, A')` 后 B.RefreshedAt == T0。
2. 同构下 B 内容变化 → RefreshedAt 保持 B 自己的新时间。
3. A' 存储签名为空（旧缓存兼容路径）→ 行为退回现算比较。

验收：

```powershell
rg -n "ContentSignature" Core\CodexRadarForm.cs
```

应命中写入、读取、Preserve、自测四处。人工验收（部署后）：记录 `%LOCALAPPDATA%\DesktopCodexAssistant\codex-radar-cache.ini` 当前模型的 `RefreshedUtc` → 重启正式实例并等待首抓完成 → 站点内容未变时 `RefreshedUtc` 保持原值、表盘绿点不跳到重启时刻。

### Phase A5: 空 key 前缀哨兵 + CheckedAt 持久化（F7）

1. `GetLegacyCodexRadarCachePrefix` 开头增加：

```csharp
if (key.Length == 0)
{
    return "Model.default.";
}
```

旧 `Model..` 条目不迁移，靠 7 天保留期自然过期。

2. `SaveCodexRadarCache` / `LoadCodexRadarCache` 新增 `CheckedAtUtc` 键（写：`CheckedAtKnown ? ToUniversalTime "o" : 空串`；读：解析成功则设 `CheckedAtLocal/CheckedAtKnown`），使构造函数 715-717 行的 `lastCodexRadarStatusAttemptLocal` 初始化真正生效。

约束：

- 哨兵仅对空 key 生效，不改任何非空 key 的前缀映射（`Gpt55.`/`Gpt55Medium.`/`Gpt54.`/`Model.<key>.` 全部保持）。
- 不把速蹬窗口状态入缓存（保持 F9-4 已知限制，避免过期开窗状态被错误恢复）。

自测：`GetLegacyCodexRadarCachePrefix("")` 与 `GetLegacyCodexRadarCachePrefix(null)` 返回 `Model.default.`；`GetCodexRadarCachePrefix` 组合后无 `Model..` 形态。

验收：

```powershell
rg -n "Model\.\.|Model.default" Core\CodexRadarForm.cs
rg -n "CheckedAtUtc" Core\CodexRadarForm.cs
```

第一条只允许命中哨兵实现与注释；第二条命中写、读两处。人工验收：重启后 LAST 时钟模式显示重启前最后检查时间而非 `--:--`。

### Phase A6: 未知数据窗口保真（F8）

`SaveCodexRadarCache` 的 `DataWindowHour` 改为：

```csharp
values[prefix + "DataWindowHour"] = snapshot.ModelIqDataWindowKnown
    ? (snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0).ToString(CultureInfo.InvariantCulture)
    : string.Empty;
```

读取器现有 `TryReadCacheInt` 对空串解析失败即保持 `WindowKnown=false`，无需改动。

约束：旧缓存里已写成 0 的"伪已知"条目不追溯修正（无法区分真上午与未知），随数据更新自愈。表盘 batchTime 对 unknown 与 0 等效处理，本项无可见显示变化。

验收：自测断言"WindowKnown=false 的快照 Save→Load 往返后 WindowKnown 仍为 false"；结合 Phase A4 签名持久化后，该字段不再参与重启误判。

### Phase A7: 额度重置确认与干扰池判别（F10）

在 `UpdateQuotaReadDeltaTracking`（`Core/CodexRadarForm.cs` 约 4253 行）的上涨方向增加"窗口身份 + 持续性确认"机制：

1. **窗口身份**：以样本的 5h/weekly reset 时刻为身份锚（±2 分钟容差内视为同一身份）。身份与 tracked 一致的样本走既有 delta 规则，不受本机制影响。

2. **核心不变量（语义源头，无等待）**：一个账号同一时刻每种窗口（5h/weekly）只可能有一个活窗口；真实的重置只能以"新生窗口"出现——reset 锚点 ≈ 样本时刻 + 完整窗口长。实证：真实统一重置样本（2026-07-11 08:04:30）锚点差 = 5h00m01s / 7d00m33s、used=0；幻影样本（00:50:06）锚点差 = 3h21m——一个"活到一半"却从未见过的窗口在 tracked 窗口仍存活时出现，违反不变量，纯算术即可判死。

3. **逐环确定性接受规则**（身份变化的样本按环独立裁决，满足任一即接受，全部不满足即拒绝，**无 pending、无延迟兜底**）：
   - `identity_same`：身份与 tracked 一致（±2 分钟容差）→ 走既有 delta 规则；
   - `reset_confirmed_by_expiry`：tracked reset 时刻已过（正常窗口到期）；
   - `reset_confirmed_by_newborn`：新生窗口判据——`窗口长 − (锚点 − 样本时刻) ∈ [0, NewbornToleranceMinutes]`（建议 8 分钟 = 轮询间隔 300s + 缓冲）且该环 used ≈ 0；
   - `reset_confirmed_by_event`：最近 `ResetEventCorroborationHours`（建议 6h）内有站点统一重置事件（复用 `quota-reset-state.ini` 的 `LastRadarResetEventUtc` 通道）；
   - `reset_confirmed_by_session`：样本来自 session 源（本机 CLI 亲历，权威）；
   - `gap_rebaseline`：距上一个被接受样本超过 `GapRebaselineMinutes`（建议 30 分钟，覆盖休眠/离线期间发生的重置——期间窗口可能已从新生长成中年，判据 3 不再适用，以重新基线代替）；
   - 以上全不满足 → `interference_pool_sample_ignored`，样本整体丢弃（含其下跌部分，避免幻影池的低值污染）。
   - 说明：`wham/usage` 无任何请求参数/头可强制后端返回一致池视图，一致性判定只能在客户端完成；上述规则全部是当次样本的确定性算术，真实统一重置**即时上环**，幻影**即时丢弃**，不存在等待窗口。

4. **决策日志新增 reason**：`reset_confirmed_by_expiry` / `reset_confirmed_by_newborn` / `reset_confirmed_by_event` / `reset_confirmed_by_session` / `gap_rebaseline` / `interference_pool_sample_ignored`，detail 中记录锚点差算术值（`anchor_age_minutes`），沿用既有 jsonl schema。

5. **干扰来源诊断抓取**：provider 样本窗口身份与 tracked 不一致时，把原始响应体（剥除 Authorization/token 等敏感头，仅 JSON body）落盘 `codex-usage-identity-change-<yyyyMMdd-HHmmss>.json`，滚动保留最近 8 份。溯源结论（2026-07-11 调查）：干扰样本与 `codex_reset_credits` 轮询完成时刻多次同秒吻合（10:50:27 / 16:03:00 / 08:42:04 / 08:04:31 / 23:16:10），指向"reset-credits 查询与 usage 查询并发时，后端返回积分已套用视图/不一致副本"；客户端现有日志只存解析后数值，无法终证，抓到一份原始异常响应即可定案。

6. **轮询错峰**：`RefreshCodexProviderUsageIfNeeded` 到期时若 `codexResetCreditsRequestRunning` 为真，则将 usage 请求顺延 ≥10 秒（反之亦然），两条对同一账号的读取不再并发——即使后端问题不修，也消除本程序侧的触发面。

约束：

- 下跌方向的既有保护（zero-drop、stale、duplicate）语义不变。
- pending 状态不持久化（重启即弃，重新确认），避免过期候选被错误恢复。
- Claude family 的额度链路不使用该机制（其来源单一），仅 Codex family 生效。

自测（新增场景，挂入既有 quota 自测链；裁决逻辑提取为纯函数，样本与 tracked 状态作为入参注入，不发真实请求）：

1. 回放 00:50 幻影：锚点差 3h21m、tracked 窗口存活 → 整样本丢弃，reason=`interference_pool_sample_ignored`，环值保持。
2. 回放 08:04 真实重置：锚点差 5h00m01s / 7d00m33s、used=0 → 双环即时采纳，reason=`reset_confirmed_by_newborn`。
3. 新生判据容差边界：锚点差 = 窗口长 − 7 分钟 → 接受；窗口长 − 9 分钟 → 拒绝（`NewbornToleranceMinutes=8`）。
4. tracked 5h reset 已过 + 新身份 5h 涨满 → 即时接受，reason=`reset_confirmed_by_expiry`。
5. 重置事件时间在窗口内 + 新身份样本 → 即时接受，reason=`reset_confirmed_by_event`。
6. session 源新身份样本 → 即时接受，reason=`reset_confirmed_by_session`。
7. 距上一接受样本 40 分钟的新身份样本 → 重新基线，reason=`gap_rebaseline`；20 分钟 → 不触发该通道。
8. 逐环独立：5h 新生 + weekly 身份不变 → 5h 采纳、weekly 走常规 delta。

验收：

```powershell
rg -n "reset_confirmed_by|reset_pending_hold|interference_pool_sample_ignored" Core\CodexRadarForm.cs
```

命中判定实现、日志 reason 与自测。人工验收（部署后观察一晚）：`quota-decision-history.jsonl` 中不再出现 3 分钟级的余额闪跳往返；真实窗口到期的上涨即时生效。

### Phase A8: 已知限制文档（F4、F5、F9，不改代码）

在 `Docs/CodexRadar-Architecture.md` 的模型目录与缓存章节追加"已知限制"：

1. 模型 key 归一化假设 GPT 主版本为一位数；`gpt_5_12` 与 `gpt_51_2` 归一后同键、两位数主版本（GPT-10+）显示拆分会出错。站点出现该形态时需同步修订 `NormalizeModelKey` 折叠正则与 `GetDisplayLabel` / `FormatCodexRadarCurrentModelShortLabel` 的拆分正则。
2. 目录文件删除重建后，若站点仍列出 5.5/5.4 模型，会各弹一次"新模型加入检测列表"通知；属一次性噪音，不做抑制。
3. 缓存读回时 `Passed` 缺失按分数估算并标记为已知（估算值当真值展示）。
4. `gpt_5_6_*` 旧身份缓存条目在 key 归一化换代后不可读，由 7 天保留期自然清理。
5. 缓存保留期 7 天：长时间未运行后重启，绿点/IQ 空白直至首次抓取。
6. 速蹬窗口状态不入缓存：重启后倒计时表盘等待首次抓取才恢复（避免过期开窗状态被错误恢复，属主动取舍）。

验收：

```powershell
rg -n "已知限制" Docs\CodexRadar-Architecture.md
```

### Phase A9: Codex 目录增减适配（适配F1）

1. **完整性门禁**：`MergeAndSave` 增加 `bool completeCatalog` 参数（或等价重载）：

   - `completeCatalog=false` 时只执行"已见模型刷新（Available=true、MissingCount=0、LastSeenUtc 更新）+ 新模型加入"，**跳过**未出现模型的 `MissingCount++`/Unavailable/Deleted 整段。
   - 公开 JSON 路径的完整性判定仿照 Claude 侧 `IsCompleteModelCatalog`：提取的目录列表非空、key 唯一、与 `model_iq` 源内模型数一致时视为完整。
   - HTML 兜底两处调用（约 6418、6650 行）恒传 `completeCatalog=false`——对比表天然是子集，只允许它补充新模型与刷新已见，不允许它衰减目录。

2. **选中模型下架定向处理**：`ShowCodexRadarModelCatalogNotifications` 中，若 `update.Unavailable`/`update.Deleted` 含 `NormalizeModelKey(CurrentSettings.CodexRadarModelKey)`，通知文案明确"当前选中模型"；Deleted 且 `RadarClockAutoSwitchModelEnabled=true` 时清空 `lastRadarClockAutoSwitchSignature` 并立即触发一次 `ApplyRadarClockAutoSwitchIfNeeded`（不等周期过期）。

3. **换代通知合并**：单次 `CodexRadarModelCatalogUpdate` 的 Added+Unavailable+Deleted 事件总数 ≥ 4 时，合并为一条"模型目录换代"摘要通知（含各类数量与至多 3 个代表模型名），不再逐条弹出。阈值以常量定义。文案组装提取为纯函数以便自测。

约束：

- 目录文件格式不变；`DeleteAfterMissingCount=3` 语义不变（只是仅在完整目录下计数）。
- F1-4 已符合项不改动。

自测（扩展第一部分 Phase A1 建立的 `CodexRadarModelCatalog.RunSelfTest`）：

1. 完整目录缺某模型 → MissingCount++；不完整目录缺同一模型 → MissingCount 不变。
2. 不完整目录含新模型 → 正常加入且 Added 事件产生。
3. 摘要合并：构造 5 个事件的 update，断言分类计数与代表模型名正确。

验收：

```powershell
rg -n "completeCatalog|CompleteCatalog" Core\CodexRadarModelCatalog.cs Core\CodexRadarForm.cs
```

三个 `MergeAndSave` 调用点均显式传入完整性实参；HTML 两处为 false。

4. **共享窗口 Claude 候选过滤（自 SPEC-2 划入，因涉及 `CodexRadarForm.cs` 文件所有权）**：`TryFindClaudeSharedClockAutoSwitchTarget`（约 5646 行）实现前核实 `ClockModelCandidates` 是否含 HistoricalOnly/已删除模型，缺过滤则按 SPEC-2 Phase B1 的同一规则补齐；自测断言历史模型不被共享窗口自动切换选中。

### Phase A10: 版本与文档同步

实现通过后按并行契约取号（`1.0.5.04` 或 `1.0.5.05`），同步：

- `Core/ProductIdentity.cs`、`AGENTS.md`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`：目录/缓存条目（合并语义、ContentSignature/CheckedAtUtc/Model.default 新键、完整性门禁、重置判别、自测入口）
- `Docs/Fable5-Data-Sources-And-Caching-Technical.md`：缓存签名持久化与新键说明
- `Docs/CodexRadar-Architecture.md`：已知限制条目（Phase A8）
- `Docs/Maintenance/CHANGELOG.jsonl` change + deploy 记录
- `Docs/Technical/INDEX.jsonl`：登记本 SPEC 与对应 GoalSpec

## Verification Matrix

实现后至少执行（沿用项目标准流程）：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-radar-codex-data-hardening.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-radar-codex-data-hardening.exe --test
.\_build\DesktopCodexAssistant-arm64-radar-codex-data-hardening.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-radar-codex-data-hardening.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-radar-codex-data-hardening.exe --test-radar-display-lifecycle --iterations 100
```

渲染样本核对：`--render-codexradar` 生成的 PNG 中抽查 `LLM:` 短标签与速蹬倒计时两张样本无视觉回归。

缓存人工验收（正式部署后执行，作为 F6/F7 的最终确认）：

1. 记录 `%LOCALAPPDATA%\DesktopCodexAssistant\codex-radar-cache.ini` 中当前模型的 `RefreshedUtc`，确认新增 `ContentSignature`/`CheckedAtUtc` 键已写入。
2. 重启正式实例，等待首次抓取完成（约 1 分钟）。
3. 站点内容未变时：`RefreshedUtc` 保持原值、表盘绿点不跳到重启时刻、LAST 时钟模式显示重启前检查时间。
4. 确认文件中不再产生新的 `Model..` 前缀条目。

全部通过后按正式部署流程：`Build-Arm64.ps1` 输出 `Release/DesktopCodexAssistant-arm64.exe` → Release 自测复跑 → 备份旧正式 exe → 部署 D/E 双正式路径 → 重启 D 实例并确认 `Responding=True` → SHA256/版本三方一致核对 → 写 CHANGELOG deploy 记录。

## Acceptance Summary

| 项 | 验收条件 |
| --- | --- |
| F1 合并语义 | 旧乐观合并表达式清零；`MergeDuplicateCatalogRecord` 4 个自测场景全部通过；`--test` 通过 |
| F2 迁移补跑 | Normalize 后补跑存在且重同步 version 枚举；空 key 自测断言通过；`--test-settings-bindings` 通过；`settingsVersion >= 62` 无行为变化 |
| F3 标签大小写 | 4 个键的标签断言通过；非空站点 label 路径不变；渲染样本抽查无回归 |
| F6 绿点跨重启 | `ContentSignature` 写/读/Preserve/自测四处齐备；3 个自测场景通过；人工验收重启后 `RefreshedUtc` 不变、绿点不跳 |
| F7 空 key + CheckedAt | 空 key 前缀为 `Model.default.`；`CheckedAtUtc` 往返生效；重启后 LAST 模式不再 `--:--`；无新增 `Model..` 条目 |
| F8 窗口保真 | WindowKnown=false 快照 Save→Load 往返后仍为 false 的自测断言通过 |
| F10 重置判别 | 8 个自测场景（幻影即时丢弃/新生即时采纳/容差边界/到期/事件/session/间隙重基线/逐环独立）通过；部署后一晚决策日志无闪跳，真实重置在下一个轮询周期内上环 |
| F4/F5/F9 文档 | `Docs/CodexRadar-Architecture.md` 出现全部已知限制条目；缓存文档同步 |
| A9 Codex 适配 完整性门禁 | 三个 MergeAndSave 调用点显式传完整性实参、HTML 恒 false；"不完整目录不衰减"自测通过 |
| 发布 | 按并行契约取号，三方（Release/D/E）SHA256 与版本一致，D 实例重启后 Responding |
