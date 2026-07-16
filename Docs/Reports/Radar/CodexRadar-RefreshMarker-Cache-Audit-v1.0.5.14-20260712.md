# Codex Radar 刷新绿点与缓存语义审计报告

适用版本：1.0.5.14
审计时间：2026-07-12 02:38:10 +09:00
审计性质：静态代码审计 + 本机运行缓存/日志取证；本报告不包含程序修复。

> 解决状态：RDR-01、RDR-02、RDR-04、RDR-05（共享成功语义）已在 1.0.5.17 修复并部署，详见 CHANGELOG `change-20260712T033000Z-1-0-5-17-radar-refresh-marker-cache-fix`；RDR-05 后半（独立 Claude `claude-radar-cache.ini` 单模型缓存）已在 1.0.5.18 改为按模型分区 + 7 天 TTL，详见 `change-20260712T035500Z-1-0-5-18-claude-radar-cache-model-partition`。文本日志假 Z 取证问题（第五阶段第 1 项）已在 1.0.5.19 修复：本地时间输出真实 UTC 偏移，详见 `change-20260712T081800Z-1-0-5-19-logger-utc-offset-normalization`。RDR-03 属固有语义（Codex 站点无稳定发布字段）无法消除；报告第 2 阶段 8 字段大拆分未纳入修复。本报告正文保持审计原貌，不回改。

## 1. 审计目标

回答以下问题，并排查同类风险：

1. Codex Radar 为什么会在约 02:00 显示绿点。
2. 该绿点是否能证明网站在 02:00 发布了新 IQ 数据。
3. Codex/Claude、共享窗口/独立窗口的缓存恢复、last-good 回退、模型切换和时间字段中，是否还存在相同或相近的语义错误。

## 2. 结论摘要

02:00 附近的绿点不是网站发布时间。缓存中的绿点时间实际为本机时间 01:35:51；该时刻确实发生了一次 Codex Radar 成功请求，但请求读到的仍是已经存在的 `7.11_pm` 批次。

最符合代码和运行时间线的根因是：共享窗口从 Claude 切回 Codex 时，内存恢复门槛把“额度缓存已知”误当成“Radar 快照也已恢复”。磁盘里的 Codex IQ 快照可能因此未加载，随后同一批网站数据失去比较基线，被当成首次看到的内容，绿点被写成请求时间。

审计共确认 5 项问题或风险：

| ID | 严重度 | 结论 | 是否会移动绿点 |
|---|---|---|---|
| RDR-01 | 高 | 软件族恢复把额度与 Radar 快照合并判定，可能跳过磁盘 IQ 缓存 | 是 |
| RDR-02 | 高 | Codex 内容签名包含派生效率和展示元数据，同一批次可被误判为新内容 | 是 |
| RDR-03 | 中 | Codex 绿点本质是本机 first-seen，不是网站 publish time | 新批次时必然显示抓取时间 |
| RDR-04 | 中 | 重启后的“上次尝试刷新”可能取网站 `monitored_at/checked_at`，不是本机请求时间 | 否，但时间文案错误 |
| RDR-05 | 中 | 共享 Claude 的 last-good 回退丢失失败语义；独立 Claude 单文件缓存只保留最后一个模型 | Claude 绿点通常不移动，但状态/回显可能错误或缺失 |

另有一个取证可靠性问题：传统文本日志使用本地 `DateTime.Now`，却通过 `ToString("u")` 输出带 `Z` 的文本。该日志不能按真正 UTC 直接解释；`network-check-history.jsonl` 的 `timestamp_utc` 和 `timestamp_local` 才是本次时间线的主要依据。

## 3. 02:00 绿点的证据时间线

| 本机时间（+09:00） | 证据 | 解释 |
|---|---|---|
| 2026-07-11 23:22 左右 | Codex Radar 页面显示 `7.11_pm` 更新于北京时间 22:22 | 换算为日本时间约 23:22，早于绿点两小时以上 |
| 2026-07-12 00:00:01 | 文本日志已有 `7.11_pm` 的签名比较记录 | 程序在午夜已经处理过该批次，不可能到 01:35 才首次由网站发布 |
| 2026-07-12 01:35:51 | `network-check-history.jsonl` 记录 `codex_radar_status` 成功，触发为“异常状态重试” | 该时刻确实请求成功 |
| 2026-07-12 01:35:51 | 文本日志记录软件族从 Claude 切换到 Codex | 与缓存恢复缺口发生条件一致 |
| 2026-07-12 02:35:09 | `codex-radar-cache.ini` 的 `SavedUtc` | 只是缓存再次写盘，不是 IQ 更新 |
| 当前缓存 | `RefreshedUtc=2026-07-11T16:35:51.4877338Z` | 换算为本机时间 01:35:51，正是绿点来源 |

当前选中模型缓存还显示：

```text
DataDate=2026-07-11
DataWindowHour=12
DataLabel=7.11_pm
PassRate=105
RefreshedUtc=2026-07-11T16:35:51.4877338Z
CheckedAtUtc=2026-07-11T11:31:00.0000000Z
```

`CheckedAtUtc` 对应网站额度/重置雷达的 `monitored_at`，不是 IQ 发布时间，也不是程序 01:35 的请求完成时间。

## 4. 时间字段当前实际含义

| 业务时间 | 理想字段 | 当前实现 |
|---|---|---|
| 网站 IQ 批次身份 | `batch_id` 或模型 + 日期 +窗口 | `ModelIqDataDateLocal`、`ModelIqDataWindowStartHourLocal`、`ModelIqDataLabel` |
| 网站真实发布时间 | `source_published_utc` | Codex 没有稳定字段；Claude 使用模型 `latest_at` |
| 本机第一次看到该批次 | `first_seen_utc` | Codex 使用 `ModelIqRefreshedAtLocal/RefreshedUtc`，但会被恢复和签名问题重写 |
| 本机最近一次尝试 | `last_attempt_utc` | 运行期有独立字段；重启初值却可能回退到网站 `checked_at/monitored_at` |
| 本机最近一次成功请求 | `last_success_utc` | 没有独立、持久化且语义稳定的字段 |
| 缓存最近写盘 | `cache_saved_utc` | `SavedUtc`，语义基本正确 |
| 内容修订发生时间 | `content_changed_utc` | 没有独立字段；当前与 first-seen 共用绿点字段 |

当前一个 `ModelIqRefreshedAtLocal` 同时承担“本机首次看到”“内容变化时间”“时钟绿点”“上次实际刷新时间”四种职责，这是问题容易反复出现的结构性原因。

## 5. 详细发现

### RDR-01：额度已知会阻止 Radar 磁盘缓存恢复

**状态：代码可证，且与本机时间线一致。**

相关入口：

- `Core/CodexRadarForm.ClaudeUsage.cs`：`RestoreCodexRadarDisplayForCurrentMode`
- `Core/CodexRadarForm.ClaudeUsage.cs`：`TryRestoreCodexRadarDisplayModeCache`
- `Core/CodexRadarForm.RuntimeState.cs`：`RadarFamilyRuntimeState`

`TryRestoreCodexRadarDisplayModeCache` 的首个快速返回条件是：模型 key 相同，并且 Radar 快照可用 **或** `state.Quota.SourceKnown`。该分支返回 `true` 后，调用方不会执行 `LoadCodexRadarCache`。

因此下列状态会被错误视为“恢复完成”：

```text
目标软件族额度缓存：有效
目标软件族 RadarSnapshot：默认值、缺失或不含 IQ
目标软件族磁盘 IQ 缓存：有效
```

随后网站请求产生一个带 `DateTime.Now` 的 Codex 快照，但内存中没有旧 IQ 签名和旧 `RefreshedUtc` 可比较，于是现有批次被记录成新的 first-seen。

该问题对 Codex/Claude 两个方向都具有结构上的可达性，因为恢复门槛是共享代码；Codex 更容易形成可见假绿点，因为 Codex 解析器先把请求时间写入 `ModelIqRefreshedAtLocal`，Claude 通常使用站点 `latest_at`。

### RDR-02：内容签名混入非批次身份字段

**状态：代码和运行日志均已证实。**

相关入口：

- `Core/CodexRadarForm.cs`：`PreserveCodexModelIqRefreshTimeIfContentUnchanged`
- `Core/CodexRadarForm.cs`：`BuildCodexModelIqContentSignature`
- `Core/CodexRadarForm.cs`：`MergeCodexModelIqHistory`
- `Core/CodexRadarForm.cs`：`ApplyCodexModelIqEfficiencyFromHistory`

当前签名包含：

- 日期、窗口、标签、通过数、任务数、IQ、状态；
- token/time 效率；
- passed、total tokens、serial seconds；
- 正常区间上下限；
- IQ 环显示上限。

后半部分不是批次身份。JSON、HTML 和历史合并路径对 token、耗时、效率、正常区间或显示上限的补全/取整方式不同，即使批次、通过数和 IQ 完全相同，签名仍可变化。

本机日志已经出现同一 `7.11_pm` 批次的差异：

```text
Source: ...|7.11_pm|7|10|105|green|165|42|7|7975292|1504|-1|-1|135
Target: ...|7.11_pm|7|10|105|green|164|16|7|8000000|3960...|90|110|135
```

这证明签名能被“数据源补全差异”触发，而不是只被“网站发布新 IQ 批次”触发。

### RDR-03：Codex 绿点不是网站发布时间

**状态：设计语义与用户理解不一致。**

相关入口：

- `Core/CodexRadarForm.cs`：`TryParseCodexRadarStatus`
- `Core/CodexRadarForm.cs`：`TryParseCodexRadarHtmlStatus`
- `Core/CodexRadarForm.EvenRow.cs`：`DrawEvenRowBatchDial`
- `Core/RadarClockDial.cs`：`GetMarkerAngle`

Codex JSON 与 HTML 解析器都先将 `ModelIqRefreshedAtLocal` 设置为 `DateTime.Now`。只有后续签名比较认定内容完全相同时，才恢复旧时间。因此，即使恢复逻辑完全正确，Codex 新批次绿点也只能表示“程序第一次成功看到该内容的时间”，不能证明网站就在该时刻发布。

Claude 的模型数据包含 `latest_at`，共享与独立 Claude 时钟都以它作为绿点，因此 Claude 侧不存在相同的基础语义缺口。

### RDR-04：“上次尝试刷新”在重启后可能显示网站监测时间

**状态：代码可证。**

相关入口：

- `Core/CodexRadarForm.cs`：构造函数
- `Core/CodexRadarForm.cs`：`TryParseCodexRadarStatus`
- `Core/CodexRadarForm.cs`：`WriteCodexRadarCacheHardeningValues`
- `Core/CodexRadarForm.EvenRow.cs`：`TryGetEvenRowLastAttemptRefreshLocal`

Codex JSON 根节点的 `checked_at` 或 `monitored_at` 被写入 `snapshot.CheckedAtLocal`，持久化名称却是 `CheckedAtUtc`。窗口重启时，构造函数又使用该值初始化 `lastCodexRadarStatusAttemptLocal`。

结果是：用户选择时钟模式 `LAST REF` 后，在本次进程尚未真正请求之前，显示的可能是网站额度雷达的监测时间，而不是程序上次尝试请求 IQ 的时间。

本机缓存的 `CheckedAtUtc=2026-07-11T11:31:00Z` 就来自站点 `monitored_at`。这与 01:35、02:00 的程序请求均无关。

### RDR-05：Claude 回退和模型缓存仍有两处相近风险

**状态：共享回退为代码可证；单模型缓存为结构性限制。**

相关入口：

- `Core/ClaudeRadarSnapshotScheduler.cs`：`ExecuteRequest`
- `Core/CodexRadarForm.cs`：`TryReadClaudeRadarStatusForSharedWindow`
- `Core/CodexRadarForm.cs`：`ConvertClaudeRadarSnapshotForSharedWindow`
- `Core/ClaudeRadarReader.cs`：`LoadCache`、`TrySaveCache`

共享 Claude 的调度器在请求失败时返回 last-good 快照，同时正确设置 `Outcome.Success=false` 和失败的 `Health`。但共享窗口转换层没有把 `Outcome.Success` 传回主请求流程，而是只判断 last-good 快照是否“可用”。主流程因此会进入成功数据应用/缓存分支，转换后的 `ModelIqRefreshSucceeded` 还可能保持 `true`。

该问题通常不会移动 Claude 绿点，因为 Claude 的绿点来自网站 `latest_at`；但它混淆了“本次请求成功”和“仍有旧数据显示”两件事，并可能让后续新增逻辑误判刷新成功。

独立 Claude 的磁盘缓存 `claude-radar-cache.ini` 只保存一个 `SelectedModelKey`，没有按模型分区，也没有读取 TTL。切换模型后文件被覆盖；重启再切回旧模型时无法回显旧模型缓存，而长期不成功刷新时又可能无限读取过期数据。共享 Claude 另有按软件族+模型分组的 `codex-radar-cache.ini`，所以两个 Claude 窗口的重启回显能力并不完全一致。

## 6. 未发现相同绿点问题的路径

以下路径当前不会把普通请求时间直接当成 Claude 的实际刷新时间：

- 独立 Claude 时钟使用 `SelectedModel.LatestAtUtc`。
- 共享 Claude 转换使用同一个 `latest_at` 填充 `ModelIqRefreshedAtLocal`。
- `RadarClockDial.GetMarkerAngle` 只负责把传入时间映射到圆周，不会自行改写时间。
- 请求失败时，独立 Claude 的 `ApplyRefreshResult` 会检查 `Outcome.Success`，并通过 `BuildRefreshFailureDisplaySnapshot` 保留当前展示。

因此不建议修改公共绘图器来解决 02:00 绿点；根因位于数据身份和缓存恢复层。

## 7. 测试覆盖缺口

现有自测覆盖了“同签名保留时间”“签名变化更新时间”“缓存字段往返”和“Codex/Claude 额度运行态隔离”，但未覆盖以下关键组合：

1. 进程启动于软件族 A，软件族 B 只有额度内存态、Radar 仅存在磁盘缓存，然后切换到 B。
2. 同一 IQ 批次从 JSON 路径切到 HTML/历史补全路径，派生效率不同但批次身份相同。
3. 同一批次的网站修订只改变展示上限或正常区间，绿点是否应移动。
4. 共享 Claude 请求失败并返回 last-good 时，`known`、`request_succeeded`、`display_from_fallback` 三种状态是否独立。
5. 独立 Claude 在 M1 -> M2 -> 重启 -> M1 的缓存回显。
6. `LAST REF` 在重启后、第一次真实请求前是否显示本机尝试时间。

`RunRadarFamilyRuntimeIsolationSelfTest` 当前主要验证额度、刷新计划、健康状态和告警防抖隔离，没有覆盖 Radar 磁盘恢复。`RunCodexModelIqRefreshMarkerSelfTest` 使用同一构造器生成两侧快照，没有模拟 JSON/HTML/历史合并产生的规范化差异。

## 8. 建议修复方案

### 第一阶段：阻止继续产生假绿点

1. 将软件族恢复拆成独立结果：`RadarRestored`、`QuotaRestored`、`HealthRestored`。
2. 只要目标 `RadarSnapshot` 不可用，就必须尝试加载目标软件族+模型的磁盘 Radar 缓存；额度已知不得阻止该步骤。
3. 增加“冷启动到 Claude，再切 Codex”和反向组合自测。

### 第二阶段：拆分时间和身份

建议新增明确语义，而不是继续复用 `ModelIqRefreshedAtLocal`：

```text
BatchIdentity                 模型 + 规范化日期 +窗口，或站点稳定 run/batch id
SourcePublishedUtc            站点提供时使用；Codex 缺失时为 null
BatchFirstSeenUtc             本机第一次看到该批次
ContentRevisionSignature      检测同批次内容修订，不控制批次绿点
ContentChangedUtc             本机第一次看到当前修订
LastAttemptUtc                每次本机请求开始
LastSuccessUtc                最近成功获得当前源响应
CacheSavedUtc                 最近写盘
DisplayFromFallback           当前展示是否来自 last-good
```

Codex 绿点应优先使用 `SourcePublishedUtc`；站点不提供时才使用 `BatchFirstSeenUtc`，并在文档/UI 语义中明确它是“首次看到”。

### 第三阶段：缩小签名职责

将当前签名拆成两类：

- `BatchIdentity`：模型、规范化批次日期、窗口、站点稳定 ID；不含效率、正常区间和显示上限。
- `ContentRevisionSignature`：IQ、通过数、任务数、原始 token/耗时等真正内容字段；允许检测同批次修订，但不得覆盖批次绿点。

派生效率、基准设置、展示上限、正常区间和 fallback 补全来源不应进入批次身份。

### 第四阶段：统一 Claude 缓存契约

1. 共享 Claude 必须同时传递 `Outcome.Success`、`Outcome.Health` 和 `DisplayFromFallback`。
2. last-good 可用于显示，但不得把本次请求标记为成功。
3. 独立 Claude 缓存改为按模型分区，并增加明确 TTL/最后成功时间。
4. 共享与独立 Claude 应复用同一个模型缓存仓库或至少使用同一 schema。

### 第五阶段：修正取证时间

1. 文本日志使用 `DateTime.UtcNow` 输出 `Z`，或使用带真实本地偏移的 ISO 8601。
2. 不再用网站 `monitored_at/checked_at` 初始化本机 `LastAttemptUtc`。
3. 网络历史日志增加 `batch_identity`、`source_published_utc`、`batch_first_seen_utc`、`content_revision_changed`、`display_from_fallback` 和 `restore_source`，但不记录完整响应正文。

## 9. 建议验收矩阵

| 场景 | 预期 |
|---|---|
| 同模型、同批次、整点重复请求 | `LastAttemptUtc/LastSuccessUtc` 更新；绿点不动 |
| JSON 成功后切 HTML fallback，同批次派生效率不同 | 展示值允许变化；批次绿点不动 |
| 同批次 IQ/通过数被站点修订 | `ContentChangedUtc` 更新；批次绿点是否移动由明确产品规则决定，默认不动 |
| 新批次出现 | `BatchIdentity` 变化；绿点更新一次 |
| 启动为 Claude，Codex 仅额度内存态但有磁盘 IQ | 切 Codex 立即恢复磁盘 IQ 和旧绿点，再联网 |
| 启动为 Codex，Claude 仅额度内存态但有磁盘 IQ | 行为对称 |
| Claude 请求失败且存在 last-good | 旧数据继续显示；Health 失败；Success=false；绿点不动 |
| Claude M1 -> M2 -> 重启 -> M1 | M1 缓存可恢复，`latest_at` 不变 |
| `LAST REF` 模式重启、尚未发请求 | 不得显示网站 `monitored_at`；显示未知或持久化的本机 LastAttempt |
| 本机时区切换 | UTC 字段不变，显示层按当前时区换算，批次身份不变 |

## 10. 修复优先级

1. **立即修复 RDR-01**：改动小、收益最大，可直接阻止切换导致的假绿点。
2. **随后修复 RDR-02/RDR-03**：建立批次身份与内容修订的双签名/双时间模型。
3. **修复 RDR-04**：避免 `LAST REF` 继续提供错误时间。
4. **统一 RDR-05**：解决共享/独立 Claude 缓存契约差异。
5. **补齐日志和矩阵测试**：否则后续仍难区分网站更新、程序请求、缓存写盘和 last-good 回退。

## 11. 审计边界

- 本报告没有修改代码、设置或运行缓存。
- 网站没有为 Codex 每个 IQ 批次稳定提供可直接复用的发布时间字段时，只能准确记录本机 first-seen，不能反推出网站真实发布时间。
- 本机文本日志包含自测产生的签名记录，不能把每一条签名变化都视为生产请求；本报告只使用与网络历史、缓存和软件族切换时间能够互相印证的记录。
- 当前工作区有其他未提交修改；本报告仅描述 1.0.5.14 工作树中上述成员的现状。
