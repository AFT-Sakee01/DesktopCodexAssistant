# Claude 收敛为「仅 CLD 磁贴 + 官方额度链」并切除 DeepSeek 余额（Claude→CLD-only & DeepSeek Balance Removal SPEC）

- 版本：1.0.6.25（或执行时的下一个可用版本号；建议分阶段各发一小版本）
- 生成模型：Claude Opus 4.8
- 生成时间：2026-07-22T01:51:45+09:00（UTC 2026-07-21T16:51:45Z）
- 主题：把 Claude 在应用里收敛到**只剩右侧 CLD 额度磁贴**，其额度改锚定**已存在的官方 `ClaudeCodeUsage` 链**；删除 3580 行冻结的社区雷达链与全部 Claude 呈现层；切除 **DeepSeek 余额**（保留 DeepSeek 服务健康灯）。

---

## 0. 核心洞察（决定本 SPEC 可行性）

**不用从零重写。官方 Claude 额度链已存在且已被调用：**
- `ClaudeCodeUsageReader`（setup-token 直连 `https://api.anthropic.com/api/oauth/usage`）+ `ClaudeCodeUsageScheduler` + `ClaudeCodeUsageSnapshot`。
- `ClaudeCodeUsageSnapshot` 字段：`FiveHourPercent` / `FiveHourResetLocal(Known)` / `WeeklyPercent` / `WeeklyResetLocal(Known)` / `SourceUpdatedUtc(Known)`——**正好是 CLD 磁贴所需**。
- `CodexRadarForm.ClaudeUsage.cs` 已在调它：`ClaudeCodeUsageScheduler.RequestRefresh`（:507）、`TryStartOrJoin`（:540）、`ApplyClaudeUsageSchedulerResult`（:565）、`ConvertClaudeCodeUsageReadResult`（:745）。

所以「重写」= **把 CLD 磁贴的额度数据锚定到官方链**，再删掉社区链 `ClaudeRadarReader`（3580 行，抓 `claudecoderadar.com`，据现状该源 2026-07-12 已冻结）。

**红线**：改完后右侧 `ClaudeQuota`(CLD) 磁贴仍须显示 Claude 五小时/周额度与重置时间。验证 §5(4) 专门守它。

---

## 1. 目标与去留总表

| 项 | 处置 |
|---|---|
| 右侧 `ClaudeQuota`(CLD) 磁贴 | **保留**，额度改走官方 `ClaudeCodeUsage` 链 |
| 官方链 `ClaudeCodeUsageReader`/`ClaudeCodeUsageScheduler`/`ClaudeCodeUsageSnapshot`/`ClaudeQuotaRingShared` | **保留** |
| Claude setup-token（`ClaudeSetupToken*` 命令/存储） | **保留**（官方链凭证） |
| IQ 面板 DeepSeek 服务健康灯（`RadarServiceHealth` 的 deepseek 项） | **保留**（用户明确） |
| 社区链 `ClaudeRadarReader`(3580) + `ClaudeRadarSnapshotScheduler`(352) | **删除** |
| `ClaudeRadarModels`(523) 中社区/时钟/所有权逻辑 | **删除**（保留仍被磁贴引用的类型，见 §3.3） |
| `CodexRadarForm.ClaudeUsage.cs`(822) 中社区接线 | **手术：断社区、留官方** |
| Claude 呈现层：EvenRow Claude 显示、`ShouldSharedWindowOwnClaudeSelection` 时钟选择、社区评分 CommunityRating | **删除** |
| DeepSeek **余额** `DeepSeekBalanceMonitor`(1104) + 告警 + 显示 + 配置对话框 + key | **删除** |
| Claude 数据链路/模型设置（见 §3.5） | **删除** |

**非目标**：不动右侧其余 9 个磁贴与四类可见面；不动 Codex 侧额度/IQ；不动 DeepSeek 服务健康探测。

---

## 2. 阶段划分（每阶段独立可编译/部署/回滚）

**A. 切除 DeepSeek 余额（最独立，先做试水）** → §3.1
**B. CLD 磁贴额度锚定官方链** → §3.2
**C. 删社区链主体 + Claude 呈现层** → §3.3
**D. 设置收敛 + schema 迁移 + 入口清理** → §3.4–3.5
**E. 构建 ARM64 → 备份 → 覆盖 → 重启**

---

## 3. 交付项（带文件/行号，均需先删消费者再删声明/文件）

### 3.1 阶段 A · 切除 DeepSeek 余额

删除消费点：
- `Core/CodexRadarForm.EvenRow.cs`：`GetDeepSeekBalanceDisplayText`（:599/603/605）及其在底部信息行的调用。
- `Core/CodexRadarForm.cs`：`RefreshDeepSeekBalanceIfNeeded`（:984/1162/5911）、`RequestDeepSeekBalanceRefresh`（:1161/1267/5875/5884/5901-5908）、DeepSeek 告警 `BuildAlert`/`GetDeepSeekApiAlertColor`（:3901/3914）、`AlertPresentationCategory.DeepSeekBalance`（:3711）、`GetDeepSeekBalanceDisplaySnapshot`（:5947）、`AppendDeepSeekSnapshotCacheSignature`（:16319/16484-16487）、`DeepSeekApiKeyRevision` 比较（:1086/1159）。
- `Core/AlertPresentationPolicy.cs`：枚举成员 `DeepSeekBalance`（:9）+ case（:34-35）。
- 删文件 `Core/DeepSeekBalanceMonitor.cs`（1104 行）。
- `Settings/Win11SettingsForm.cs`：删「DeepSeek 余额」设置组（:519）、提醒分类里的 `AlertDeepSeekBalanceEnabled`（:490）、DeepSeek 配置对话框 `OpenDeepSeekApiKeyDialog`（:2774）与按钮接线（:1279-1285/2526-2528）。
- 删 SecretStore 中 DeepSeek API key 存取（若存在）。

**保留**：`Core/RadarServiceHealth.cs` 的 deepseek 健康项与其在 IQ 面板/网络面板的健康灯（`CodexIqBoardForm*`、`NetworkMonitorForm.Dock*`、`CodexRadarForm.TileSnapshot.cs`）。

设置删除：`DeepSeekApiKeyRevision`、`AlertDeepSeekBalanceEnabled`（WidgetSettings 全部自引用 :380/736/830/953/1045/1176/1264/1473/1598/1918 等 + Win11SettingsForm 绑定）。schema bump #A。

### 3.2 阶段 B · CLD 磁贴额度锚定官方链

- 确认/固化 `RadarFamilyRuntimeState(Claude).Quota`（喂 `BuildRadarTileSnapshot`，`CodexRadarForm.TileSnapshot.cs:33-40`）来自 `ApplyClaudeUsageSchedulerResult`（官方 `ClaudeCodeUsageScheduler` 结果，`ClaudeUsage.cs:565-587`），而非社区 `ClaudeRadarSnapshotScheduler`。
- `CacheCodexRadarDisplayMode`（`ClaudeUsage.cs:289-336`）中 Claude family 的 `state.Quota.Snapshot` 只用官方额度快照来源。
- **CLD 磁贴 `ModelName`**：官方 oauth/usage 不分模型，`ResolveTileModelName`（`TileSnapshot.cs:29`）对 Claude family 改为固定标签（如 "Claude" / "CLD"），不再依赖 `ClaudeRadarModelKey` 与社区 radar 快照。
- 验收：断网/社区源移除的前提下，CLD 磁贴仍显示官方五小时/周额度。

### 3.3 阶段 C · 删社区链主体 + Claude 呈现层

- 删文件 `Core/ClaudeRadarReader.cs`（3580）、`Core/ClaudeRadarSnapshotScheduler.cs`（352）。
- `Core/ClaudeRadarModels.cs`：删社区解析、时钟选择、`ShouldSharedWindowOwnClaudeSelection`（:338）及其自测（:373-378）；**保留**仍被磁贴/官方链引用的纯类型（如 `CodexRadarSoftwareMode` 若定义于此，需核对归属，必要时迁出）。
- `Core/CodexRadarForm.ClaudeUsage.cs`：删社区调度接线（`ClaudeRadarSnapshotScheduler.TryStartOrJoin`/`GetLastGoodSnapshot`，原 `CodexRadarForm.cs:6798-6807` 一带）、社区 radar 快照进 `claudeRuntimeState.RadarSnapshot` 的路径、社区评分 `CommunityRating*`；只留官方 `ApplyClaudeUsageSchedulerResult` 一条。
- `Core/CodexRadarForm.EvenRow.cs` / `CodexRadarForm.cs`：删 Claude 模式的 EvenRow 呈现分支与 Claude 时钟选择调用（headless 后已不可见，代码清除）。CLD 磁贴渲染路径（`MetricTileForm` / `TileSnapshot`）不受影响。
- 全仓搜 `ClaudeRadarReader` / `ClaudeRadarSnapshotScheduler` / `CommunityRating` 无活引用。

### 3.4 阶段 D · 入口与自测清理

- `Core/OperationForm.RadialDial.cs`：删径向菜单里 `ClaudeRadarJsonEnabled` 等社区开关（原 :447/526/2550 一带的 Claude 项）。
- `DesktopCodexAssistant.cs`：删 `--render-clauderadar` 残留与相关自测；`TestRadarDisplayLifecyclePolicy` 去 Claude 社区构造。
- `GlobalLayoutEditorForm.cs`：无 Claude 可拖拽项残留（独立窗已删）。

### 3.5 阶段 D · 设置收敛（schema bump #B）

删除（社区 Claude，已无消费者）：`ClaudeRadarJsonEnabled`、`ClaudeRadarHomepageFallbackEnabled`、`ClaudeRadarCommunityRatingsEnabled`、`ClaudeRadarLocalQuotaFallbackEnabled`、`ClaudeRadarModelKey`（阶段 B 后磁贴不再用）。清 WidgetSettings 全部自引用 + Win11SettingsForm「Claude Radar」页对应组（:514-519 一带）+ 绑定豁免。

**保留**：Claude setup-token 相关命令/存储（官方链凭证）、`RadarClock*`、`AlertServiceHealthEnabled`（DeepSeek/Claude 健康灯告警仍用）。

旧配置首次加载原子规范化保存；新增旧版本 fixture 自测断言废弃键不再输出、保留键不丢值；`--test-settings-bindings` 保持 PASS。

---

## 4. 待执行时判定的点

1. **Claude 服务健康灯**（`RadarServiceHealth` 的 claude 项，与 Radar/OpenAI/DeepSeek 同列）：用户只明确保留 DeepSeek 灯。建议保留 Claude 灯（与其余三灯一体，删单项要改数组与 IQ 面板 4→3 灯布局）；执行前向用户确认一句。
2. **`CodexRadarSoftwareMode` / `RadarFamilyRuntimeState` 等共享类型**若定义在待删的 `ClaudeRadarModels.cs` 内，需迁出到保留文件，不可随文件删。
3. CLD 磁贴的 IQ/Rating/Efficiency 字段（`RadarTileSnapshot:37-45`）Claude 侧改为恒 `*Known=false`（官方链不产出这些），确认磁贴渲染对全 false 容错。

---

## 5. 验证要求

1. 各阶段全仓编译零错误；`--test-settings-bindings` PASS；新增各版本 fixture 迁移自测 PASS。
2. 搜 `DeepSeekBalanceMonitor` / `ClaudeRadarReader` / `ClaudeRadarSnapshotScheduler` / `CommunityRating` 无活引用。
3. `--test-layout` PASS；IQ 面板 DeepSeek（及 Claude，若保留）健康灯仍在。
4. **红线回归**：CLD 磁贴显示官方五小时/周额度与重置时间；断开社区源不影响它。用 `--render-tilecolumn` 人工核对 CLD 磁贴。
5. DeepSeek 余额相关 UI/告警/配置对话框全部消失；DeepSeek 服务健康探测与健康灯不受影响。
6. 按根 `AGENTS.md`：逐阶段构建 ARM64 → 备份现有正式 exe → 覆盖 → 从 E: 入口重启。

---

## 6. 与既有 SPEC 的关系

承接 `Opus48-InterfaceConvergenceRetireClassicFloaters-SPEC`（界面收敛，headless 化与独立 Claude 窗删除已落地）。本 SPEC 是其后续：把 Claude **数据层**也收敛到只剩 CLD 磁贴 + 官方额度链。两者不冲突；本 SPEC 另在 spec-board 登记一条 `pending`。

---

## 7. 文档同步（验证触发表）

- `Docs/Codex-ClaudeRadar-Architecture.md` / `Docs/Claude-EvenRow-DialCard-Technical.md` / `Docs/Fable5-Data-Sources-And-Caching-Technical.md`：标注社区雷达链退役、CLD 磁贴改官方 oauth/usage、DeepSeek 余额移除。
- `Docs/Indexes/FEATURE_INDEX.jsonl` / `Docs/Interfaces/INTERFACE_INDEX.jsonl`：移除 `ClaudeRadarReader`/`ClaudeRadarSnapshotScheduler`/`DeepSeekBalanceMonitor` 与废弃设置键。
- `Docs/Maintenance/CHANGELOG.jsonl`：每阶段一条 `refactor` + 一条 `deployment`。
- 根 `AGENTS.md` / `README.md`：版本与数据源清单更新。
