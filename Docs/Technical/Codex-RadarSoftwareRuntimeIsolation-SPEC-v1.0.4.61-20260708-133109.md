# Codex Radar Software Runtime Isolation SPEC

版本：`1.0.4.61`  
生成时间：`2026-07-08 13:31:09 +09:00`  
状态：`draft`  
生成模型：`Codex`

## 1. Goal

把当前 Codex Radar 窗口内的 Codex/Claude 软件族切换、额度运行态、Radar 网站状态和 API 告警状态拆成明确边界，保证 Codex 与 Claude 的运行数据、缓存恢复、额度消耗环、重置保护和错误提示互不污染。

本 spec 给后续 goal 执行使用。开始执行后本文视为冻结快照；需求变动必须新建下一版 spec，不在本文上继续改。

## 2. 背景结论

当前实现已经做了一部分隔离：

- Codex 额度磁盘缓存使用 `%LOCALAPPDATA%\DesktopCodexAssistant\quota.ini`。
- Claude 额度磁盘缓存使用 `%LOCALAPPDATA%\DesktopCodexAssistant\claude-quota.ini`。
- Codex/Claude Radar 网站缓存已有 `Codex.` / `Claude.` 前缀。
- 网站异步刷新捕获了请求时的软件族和模型 key，完成时会检查当前模式，避免旧请求直接覆盖当前可见快照。
- `CodexQuotaGoalPlanner` 只读取 Codex 的 `quota.ini`，不读 Claude 额度缓存。

但仍有核心运行态共享：

- `CodexRadarForm.ClaudeUsage.cs` 同时承担切换决策、前台检测、缓存恢复、额度刷新请求和重绘。
- `CodexRadarForm.cs` 内的 `quotaSnapshot`、`quotaSourceKnown`、`lastFiveHourQuotaReadPercent`、`lastWeeklyQuotaReadPercent`、`fiveHourConsumptionRingBaselinePercent`、`weeklyQuotaAtFiveHourWindowStartPercent`、`fiveHourQuotaProtectionUtc`、`weeklyQuotaProtectionUtc` 等字段仍是单实例共享。
- Codex 和 Claude 都调用 `ApplyCodexQuotaSnapshot(...)`，该方法写入同一组额度状态并触发同一套消耗环判断。
- `radarServiceHealth` 是单个字段，CodexRadar 网站和 ClaudeRadar 网站健康状态会写入同一处。
- `quotaSourceKnown` 是全局优先判断，可能让一个软件族的 known 状态影响另一个软件族。
- Codex RSS / 速蹬 / 额外重置保护是 Codex 语义，但保护字段是全局字段。
- `codexRadarDisplayModeCache` 只保存粗粒度 `RadarSnapshot`、`QuotaSnapshot`、`RadarHealth`，没有保存完整额度消耗基线、重置保护、API 防抖、刷新时间和 provider 状态。

这些共享字段是近期“周额度消耗环从 100 开始扣”“切换时瞬间出现 Rader 错误”“Claude/Codex 状态疑似串扰”的主要风险源。

## 3. 目标范围

必须完成：

1. 抽离独立的软件族切换控制器。
2. 建立 Codex/Claude 分开的运行态容器。
3. 让额度快照、额度消耗环基线、额度 source-known 状态、重置保护状态按软件族隔离。
4. 让 CodexRadar/ClaudeRadar 网站健康状态按软件族隔离。
5. 让 API 摘要/LED 告警防抖不跨软件族泄漏。
6. 保持现有窗口视觉布局、尺寸、文字、配色和刷新策略不发生无关变化。
7. 保留现有 Codex/Claude 自动切换语义，并用自测固定行为。
8. 更新受影响的架构文档、刷新规则、功能索引、接口索引和维护日志。
9. ARM64 构建、测试、覆盖正式 exe 并重启程序。

## 4. 非目标

本 spec 不做以下事情：

- 不重做 Codex Radar 或 Claude Radar 的 UI 布局。
- 不新增外部网站或 API。
- 不改变 codexradar.com 或 claudecoderadar.com 数据解析业务含义。
- 不改变 Claude 独立窗口的布局和核心绘制语义。
- 不删除现有 Claude 使用量调度器；如果它仍是跨窗口去重的正确边界，可以继续保留。
- 不恢复 Dock、Launchpad、顶部栏或 Direct2D 项目。
- 不编译 x64，除非用户另行明确要求。

## 5. 现有入口与需要重点阅读的文件

执行前必须重新读取这些文件的相关区域，不能只依赖本文快照：

- `Core/CodexRadarForm.ClaudeUsage.cs`
  - `UpdateEffectiveCodexRadarSoftwareModeIfNeeded`
  - `ResolveCodexRadarSoftwareModeFromSignals`
  - `TryDetectForegroundCodexRadarSoftware`
  - `HandleCodexRadarSoftwareChanged`
  - `RestoreCodexRadarDisplayForCurrentMode`
  - `CacheCodexRadarDisplayMode`
  - `TryRestoreCodexRadarDisplayModeCache`
  - `RequestSelectedQuotaUsageRefresh`
  - `RunSoftwareModeGateSelfTest`
- `Core/CodexRadarForm.CodexUsage.cs`
  - Codex provider quota read path
  - `ApplyCodexQuotaSnapshot(...)` call site
- `Core/CodexRadarForm.cs`
  - quota fields
  - `ApplyCodexQuotaSnapshot`
  - `UpdateQuotaReadDeltaTracking`
  - `LoadSelectedQuotaCacheIntoDisplay`
  - `TryReadQuotaIniSnapshot`
  - `TryWriteQuotaIniSnapshot`
  - reset/RSS protection functions
  - `RefreshCodexRadarStatusIfNeeded`
  - `SetRadarServiceHealth`
  - API summary/debounce functions
  - render scene cache key construction
- `Core/CodexRadarForm.EvenRow.cs`
  - quota rendering input
  - bottom software-family label
  - API summary rendering
  - clock cycle selection
- `Core/ClaudeRadarReader.cs`
  - Claude public Radar data
  - Claude quota cache write helper
- `Core/ClaudeCodeUsageReader.cs`
  - Claude usage snapshot semantics
- `Settings/WidgetSettings.cs`
  - `CodexRadarSoftwareMode`
  - settings persistence and self-test coverage
- `Docs/CodexRadar-Architecture.md`
- `Docs/Codex-ClaudeRadar-Architecture.md`
- `Docs/Component-Refresh-Rules.md`
- `Docs/Fable5-Data-Sources-And-Caching-Technical.md`
- `Docs/Indexes/FEATURE_INDEX.jsonl`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`

## 6. 设计原则

### 6.1 软件族是状态边界

Codex 和 Claude 必须拥有各自的运行态。任何“上次读取”“基线”“known/unknown”“保护态”“网站健康”“刷新时间”“防抖状态”都不能只用一个全局字段表示。

允许共享：

- 绘制代码。
- 字体、颜色 token、layered-window 提交管线。
- 通用 DTO 类型，但使用时必须绑定软件族。
- Claude 使用量调度器，只要它的输出写入 Claude 运行态，不写入 Codex 运行态。
- 公共网络在线状态，例如系统离线状态。

禁止共享：

- 活动额度快照。
- 额度 source-known 标志。
- 5 小时/周额度上一次读取值。
- 5 小时/周额度消耗环基线。
- Codex RSS/速蹬/额外重置保护状态。
- CodexRadar/ClaudeRadar 网站健康状态。
- API 摘要防抖稳定错误。
- 模式切换中的“上一帧可见数据”。

### 6.2 切换控制器必须是纯决策

软件族切换控制器只做决策，不做副作用。

允许：

- 接收设置模式、运行进程状态、前台识别结果、上一有效模式。
- 返回下一有效模式、是否变化、原因、是否需要前台检测、建议刷新目标。

禁止：

- 直接调用 WinForms/GDI 绘制。
- 直接请求网络。
- 直接读写 quota/cache 文件。
- 直接修改 `CodexRadarForm` 字段。
- 直接弹通知。
- 直接写日志，除非是纯自测失败异常。

### 6.3 切换事务必须短且可推理

一次软件族切换只允许做：

1. 保存当前 active 软件族的运行态。
2. 设置新的 active 软件族。
3. 从目标软件族内存状态恢复可见数据。
4. 目标软件族内存缺失时，从目标软件族磁盘缓存恢复。
5. 为目标软件族安排下一次额度/网站刷新。
6. 清理或切换到目标软件族自己的 API 防抖状态。
7. 触发重绘。

不得在切换事务里同步执行远程请求或把另一个软件族的数据套到当前软件族。

### 6.4 视觉默认不变

这是运行态重构，不是 UI 改版。除非修复串扰必须改动，窗口尺寸、模块位置、字体、颜色、文本、环宽、线宽、透明度和当前布局都必须保持。

## 7. 数据结构规格

### 7.1 新增 `RadarSoftwareModeController`

建议文件：

- `Core/RadarSoftwareModeController.cs`

建议类型：

```csharp
internal sealed class RadarSoftwareModeController
{
    public RadarSoftwareModeDecision Resolve(RadarSoftwareModeInput input);
}
```

建议输入：

```csharp
internal sealed class RadarSoftwareModeInput
{
    public CodexRadarSoftwareMode ConfiguredMode { get; set; }
    public CodexRadarSoftwareMode PreviousEffectiveMode { get; set; }
    public SoftwareRuntimePresenceSnapshot Presence { get; set; }
    public bool ForegroundDetected { get; set; }
    public CodexRadarSoftwareMode ForegroundMode { get; set; }
}
```

建议输出：

```csharp
internal sealed class RadarSoftwareModeDecision
{
    public CodexRadarSoftwareMode PreviousEffectiveMode { get; set; }
    public CodexRadarSoftwareMode EffectiveMode { get; set; }
    public bool Changed { get; set; }
    public bool ShouldDetectForeground { get; set; }
    public SelectedQuotaRefreshTarget SelectedQuotaRefreshTarget { get; set; }
    public string Reason { get; set; }
}
```

如果现有 `SoftwareRuntimePresenceSnapshot` 是 form 内私有类型，需要移动到独立文件并保持不可变语义。

### 7.2 新增 `RadarFamilyRuntimeState`

建议文件：

- `Core/RadarFamilyRuntimeState.cs`

建议结构：

```csharp
internal sealed class RadarFamilyRuntimeState
{
    public CodexRadarSoftwareMode Family { get; private set; }
    public string ModelKey { get; set; }
    public CodexRadarSnapshot RadarSnapshot { get; set; }
    public QuotaRuntimeState Quota { get; private set; }
    public ServiceHealthState RadarSiteHealth { get; set; }
    public ApiAlertDebounceRuntimeState ApiAlertDebounce { get; private set; }
    public DateTime LastRadarStatusRefreshUtc { get; set; }
    public DateTime NextRadarStatusRefreshUtc { get; set; }
    public string RadarStatusRefreshTrigger { get; set; }
    public long Revision { get; set; }
}
```

`Revision` 每次影响绘制的数据变化时递增，用于 render scene cache key，避免旧 bitmap 因共享 key 继续显示。

### 7.3 新增 `QuotaRuntimeState`

建议结构：

```csharp
internal sealed class QuotaRuntimeState
{
    public CodexQuotaSnapshot Snapshot { get; set; }
    public bool SourceKnown { get; set; }
    public int LastFiveHourReadPercent { get; set; }
    public int LastWeeklyReadPercent { get; set; }
    public DateTime LastReadSourceUtc { get; set; }
    public int FiveHourConsumptionRingBaselinePercent { get; set; }
    public DateTime TrackedFiveHourResetLocal { get; set; }
    public int WeeklyQuotaAtFiveHourWindowStartPercent { get; set; }
    public QuotaProtectionState Protection { get; set; }
}
```

### 7.4 新增 `QuotaProtectionState`

Codex 需要；Claude 默认空。

```csharp
internal sealed class QuotaProtectionState
{
    public DateTime FiveHourProtectionUtc { get; set; }
    public DateTime WeeklyProtectionUtc { get; set; }
    public bool FiveHourProtectionGold { get; set; }
    public bool WeeklyProtectionGold { get; set; }
    public string LastRadarResetEventId { get; set; }
    public string LastRadarOpenEventId { get; set; }
}
```

要求：

- Codex RSS/速蹬/额外重置只能写 Codex 的 `QuotaProtectionState`。
- Claude 不得消费 Codex 的 `quota-reset-state.ini`。
- 如果未来 Claude 也有类似 reset event，必须另建 family-keyed 状态或 Claude 专用状态文件，不能复用 Codex 文件。

### 7.5 新增或迁移 `ApiAlertDebounceRuntimeState`

现有 `ResetCodexApiServiceAlertDebounceForDisplayContextSwitch` 只能清空共享防抖状态。重构后应改为每个软件族独立保存：

- 当前候选签名。
- 候选首次出现时间。
- 已稳定错误签名。
- 已稳定错误级别。
- 上次正常时间。
- 上次切入此软件族时间。

切换时不得把 A 软件族已稳定错误直接显示到 B 软件族。目标软件族如果有历史错误，必须经过 fresh observation 或明确仍在有效 TTL 内的本族状态确认后才显示。

## 8. 行为规格

### 8.1 自动/手动软件族选择必须保留现有矩阵

以下行为必须与现有 `RunSoftwareModeGateSelfTest` 一致：

| 配置 | 上一有效模式 | 运行状态 | 前台检测 | 结果 |
|---|---|---|---|---|
| `Codex` | 任意 | 任意 | 任意 | `Codex` |
| `Claude` | 任意 | 任意 | 任意 | `Claude` |
| `Auto` | `Claude` | 两者都未运行 | 无 | `Claude` |
| `Auto` | `Claude` | 仅 Codex 运行 | 无 | `Codex` |
| `Auto` | `Codex` | 仅 Claude 运行 | 无 | `Claude` |
| `Auto` | `Codex` | 两者运行 | 前台识别 Claude | `Claude` |
| `Auto` | `Claude` | 两者运行 | 前台识别失败 | `Claude` |

还必须保留：

- 仅当 `Auto` 且 Codex/Claude 两者都运行时才做前台检测。
- fixed mode 不受前台窗口和进程状态影响。
- `Smooth` / `Balanced` / `BatterySaver` 下自动检测间隔保持现有 2/5/10 秒策略。
- 没有任何支持软件运行时，不请求任何额度刷新。

### 8.2 额度快照写入

新增统一入口：

```csharp
ApplyQuotaSnapshot(CodexRadarSoftwareMode family, CodexQuotaSnapshot snapshot, bool sourceKnown, bool appRunning, DateTime nowLocal, DateTime detectedUtc, string sourceKind)
```

要求：

- 写入 `runtimeStates[family].Quota`。
- 不写入另一软件族的 `QuotaRuntimeState`。
- Codex provider spike guard 只对 Codex provider 路径生效。
- Claude usage/cache 不触发 Codex provider spike guard。
- Codex RSS/速蹬 reset protection 只套到 Codex quota。
- Claude quota 失败时保留 Claude last-good，不清空 Codex。
- Codex quota 失败时保留 Codex last-good，不清空 Claude。
- `LoadSelectedQuotaCacheIntoDisplay(preserveExistingOnMiss)` 改为加载当前 active family 的缓存到当前 active family state；miss 时只影响 active family。

### 8.3 额度消耗环基线

每个软件族独立维护：

- 5 小时上一次读取余额。
- 周额度上一次读取余额。
- 5 小时消耗环起点。
- 周额度在 5 小时窗口开始时的余额。
- 5 小时 reset boundary。
- source updated utc。

必须保留现有修复：

- 两次读取结果完全一致时，5 小时消耗环保持上一次可见值。
- provider 返回 near-full 但旧 reset 未到期时，不能污染 Codex baseline。
- 周额度 baseline 卡在 95+ 且稳定低周额度返回时，能自修复。

新增要求：

- 上述逻辑分别对 Codex 和 Claude 运行，不得共用 baseline。
- 切换软件族时不调用 `InitializeQuotaReadDeltaTracking` 重置另一软件族 baseline。
- 每条 quota decision history 必须能看出 `software_family`、`source_kind`、`five_hour_balance`、`weekly_balance`、两个 consumption ring 的计算理由。

### 8.4 网站状态与快照

`RefreshCodexRadarStatusIfNeeded` 或其拆分后的替代实现必须做到：

- 捕获请求时的软件族和模型 key。
- 请求完成后只写入请求所属软件族的 `RadarFamilyRuntimeState`。
- 如果完成时当前 active 软件族已变化，不得把结果直接写到可见 active state。
- 可以更新对应软件族的后台 state，方便切回时立即显示。
- CodexRadar 网站健康写 Codex state。
- ClaudeRadar 网站健康写 Claude state。
- 不再把两者都写入单个 `radarServiceHealth`。

### 8.5 API 摘要与 LED

右侧 API 摘要和 LED 必须读取当前 active family 的状态。

要求：

- Codex 模式下的 Rader 表示 codexradar.com。
- Claude 模式下的 Rader 表示 claudecoderadar.com。
- OpenAI status、Claude public status、DeepSeek 余额这些服务级状态不能因为软件族运行态拆分而被错误删除。
- 软件族切换后，不允许出现一帧“另一个软件族遗留的 Rader 无法连接”。
- 10 秒错误防抖仍然有效：错误必须在当前软件族下稳定存在满 10 秒才显示；恢复正常立即清除。

### 8.6 缓存恢复

内存缓存必须从 `codexRadarDisplayModeCache` 粗粒度缓存升级为 family runtime state。

要求：

- 切到 Codex 时先使用 Codex 内存 state。
- 切到 Claude 时先使用 Claude 内存 state。
- 目标内存 state 没有有效 quota 时，才读目标 family 的磁盘 quota cache。
- 目标磁盘 cache miss 时，不拿另一个软件族的 quota 顶替。
- `preserveExistingOnMiss=true` 只能保留当前 active family 自己的旧显示。

### 8.7 渲染读取

渲染层允许继续复用 `DrawCodexRadarModulesEvenRow`，但数据输入必须来自 active runtime state。

要求：

- `GatherQuotaDisplayState` 不直接读共享 `quotaSnapshot`。
- `IsSelectedQuotaValueKnown` 不再先看全局 `quotaSourceKnown`。
- scene cache key 必须包含 active family 和 active state revision。
- 切换后第一帧不能显示另一软件族的 quota 数字、Rader 状态、LLM/RC/软件族文本组合。

## 9. 实施阶段

### Phase 0：基线保护

1. 记录当前 dirty worktree。
2. 读取本 spec 和相关 owner 文档。
3. 运行或至少确认当前 `RunSoftwareModeGateSelfTest` 覆盖矩阵。
4. 生成 baseline render sample：
   - `--render-codexradar`
   - `--render-clauderadar`
5. 记录当前 `quota-decision-history` 最新几行，确认修复前/修复后的字段。

验收：

- 未开始改代码前就已确认当前切换矩阵。
- baseline 截图可用于改后对照。

### Phase 1：抽离软件族控制器

1. 新增 `RadarSoftwareModeController`。
2. 把 `ResolveCodexRadarSoftwareModeFromSignals` 迁移为控制器纯逻辑。
3. 前台检测保留在 Win32 adapter，不进入控制器。
4. `CodexRadarForm` 只调用控制器得到 decision。
5. `RunSoftwareModeGateSelfTest` 改为测试控制器。
6. 保留旧方法名 wrapper 一轮也可以，但 wrapper 只能转调控制器，不能保留另一套逻辑。

验收：

- `--test` 覆盖 fixed mode、auto none、auto single app、auto both app foreground、foreground failure fallback。
- 代码搜索确认切换决策只有一个实现源。

### Phase 2：建立 family runtime state

1. 新增 `RadarFamilyRuntimeState`、`QuotaRuntimeState`、`QuotaProtectionState`。
2. `CodexRadarForm` 初始化两个 state：
   - `Codex`
   - `Claude`
3. 新增 `ActiveRadarFamilyState` 访问器。
4. 把当前 active 显示字段改为从 active state 读取。
5. 保留兼容 wrapper 时，wrapper 必须只代理 active state，不能继续作为事实源。

验收：

- 切换 Codex/Claude 时 active state 指针变化。
- 非 active state 的 quota/radar/health 数据不被重置。
- render scene cache key 能区分两个 family。

### Phase 3：额度隔离

1. 将 `ApplyCodexQuotaSnapshot` 改名或替换为 `ApplyQuotaSnapshot(family, ...)`。
2. 将 `UpdateQuotaReadDeltaTracking` 改为接收 `QuotaRuntimeState`。
3. 将 `InitializeQuotaReadDeltaTracking`、`ResetQuotaReadDeltaTracking` 改为接收 `QuotaRuntimeState`。
4. Codex provider call site 显式传 `Codex`。
5. Claude usage call site 显式传 `Claude`。
6. `LoadSelectedQuotaCacheIntoDisplay` 改为按 active family 写入 active quota state。
7. `TryReadQuotaIniSnapshot` / `TryWriteQuotaIniSnapshot` 保持 family-aware，不退回交叉读取。
8. quota decision history 增加 `software_family` 字段。

验收：

- Codex quota 更新不会改变 Claude quota state。
- Claude quota 更新不会改变 Codex quota state。
- Codex provider spike guard 自测仍通过。
- duplicate same-balance keep-ring 自测对 Codex/Claude 都通过。
- decision history 每条新记录都有 `software_family`。

### Phase 4：重置保护隔离

1. 把 `fiveHourQuotaProtectionUtc`、`weeklyQuotaProtectionUtc`、gold flags 迁移进 Codex 的 `QuotaProtectionState`。
2. Codex `quota-reset-state.ini` 只读写 Codex protection。
3. `ApplyQuotaResetProtections` 接收 family/state；Claude family 返回原 quota，不套 Codex 保护。
4. Codex RSS/速蹬 event 只更新 Codex state。

验收：

- 构造 Codex reset event 后，Codex quota 可进入保护态。
- 同一 event 下 Claude quota 不进入保护态。
- 切到 Claude 时不显示 Codex “速蹬/已重置”保护文字。
- 切回 Codex 时 Codex 保护态仍按自己的释放规则存在或释放。

### Phase 5：Radar 网站健康与 API 防抖隔离

1. 将 `radarServiceHealth` 替换为 family state 内的 `RadarSiteHealth`。
2. `SetRadarServiceHealth` 改为 `SetRadarServiceHealth(family, health)`。
3. API 摘要读取 active family 的 Radar health。
4. API 防抖状态按 family 保存。
5. 切换时不再简单清空全局状态，而是激活目标 family 的防抖上下文。

验收：

- CodexRadar 网站失败只影响 Codex 模式的 Rader。
- ClaudeRadar 网站失败只影响 Claude 模式的 Rader。
- 手动或自动切换后的第一帧不出现另一个 family 的旧错误。
- 持续真实错误满 10 秒后仍能显示。
- 错误恢复后立即清除。

### Phase 6：切换事务收口

1. 新增单一切换入口，例如 `SwitchCodexRadarSoftwareFamily(next, reason)`。
2. 所有设置变更、自动检测、前台切换都走该入口。
3. 入口内禁止网络请求。
4. 入口内只允许读取目标 family 本地 cache。
5. 入口内触发目标 family refresh scheduling。
6. 删除或废弃零散的 `HandleCodexRadarSoftwareChanged` 副作用散落。

验收：

- 代码搜索确认所有 effective software mode 变化只经过单一入口。
- 设置切换、自动切换、启动恢复、模型切换不会绕过 family state。

### Phase 7：文档、索引、版本和部署

1. 更新 `Core/ProductIdentity.cs` 与根 `AGENTS.md` 版本。
2. 更新 `Docs/CodexRadar-Architecture.md`。
3. 如 Claude 独立窗口共享边界有变化，更新 `Docs/Codex-ClaudeRadar-Architecture.md`。
4. 更新 `Docs/Component-Refresh-Rules.md`。
5. 更新 `Docs/Fable5-Data-Sources-And-Caching-Technical.md`，如果持久化文件、cache key 或状态文件语义发生变化。
6. 更新 `Docs/Indexes/FEATURE_INDEX.jsonl` 相关行。
7. 更新 `Docs/Interfaces/INTERFACE_INDEX.jsonl` 相关行。
8. 追加 `Docs/Maintenance/CHANGELOG.jsonl`。
9. 构建 ARM64，覆盖正式 exe，重启。

验收：

- 所有文档版本与 `ProductIdentity.Version` 一致。
- JSONL 全部可解析且 id 唯一。
- 正式 exe 文件版本等于新版本。
- 程序重启后只有一个正式进程。

## 10. 必须新增或扩展的自测

### 10.1 软件族控制器自测

扩展 `RunSoftwareModeGateSelfTest` 或新增 `RadarSoftwareModeController.RunSelfTest`：

- fixed Codex 忽略 foreground/presence。
- fixed Claude 忽略 foreground/presence。
- auto + none keeps previous。
- auto + Codex only selects Codex。
- auto + Claude only selects Claude。
- auto + both + foreground Codex selects Codex。
- auto + both + foreground Claude selects Claude。
- auto + both + foreground failure keeps previous。
- only both-running auto mode returns `ShouldDetectForeground=true`。
- selected quota refresh target:
  - active Codex + Codex running => Codex。
  - active Codex + Codex stopped => None。
  - active Claude + Claude running => Claude。
  - active Claude + Claude stopped => None。
  - no supported apps => None。

### 10.2 额度隔离自测

新增或扩展 quota self-test：

- Codex sample `five=67 weekly=13` 写入后，Claude state 仍默认。
- Claude sample `five=88 weekly=44` 写入后，Codex state 仍为 `67/13`。
- Codex duplicate source same balance 保留 Codex 消耗环。
- Claude duplicate source same balance 保留 Claude 消耗环。
- Codex provider early reset spike 不污染 Codex baseline。
- Claude near-full sample 不触发 Codex provider spike guard，也不写 Codex baseline。
- Codex RSS reset protection 后，Claude quota 不变。
- cache miss with preserveExistingOnMiss 不拿另一 family 的 quota。

### 10.3 Radar 网站健康隔离自测

新增服务状态 self-test：

- CodexRadar health = Unavailable，ClaudeRadar health = Normal，active Claude 时 Rader 正常。
- ClaudeRadar health = Unavailable，CodexRadar health = Normal，active Codex 时 Rader 正常。
- 从 Codex 切 Claude 后，不显示 Codex stale Radar error。
- 从 Claude 切 Codex 后，不显示 Claude stale Radar error。
- 当前 family 的错误稳定满 10 秒后才显示。

### 10.4 异步完成隔离自测

用 fake reader 或测试 hook 模拟：

- Codex 网站请求发出后立即切到 Claude，Codex 请求完成，只更新 Codex state，不改 Claude active display。
- Claude 额度请求发出后立即切到 Codex，Claude 请求完成，只更新 Claude state，不改 Codex active display。
- 切回对应 family 后能看到对应 family 的后台完成数据。

### 10.5 渲染回归自测

扩展 `--test-layout` 或 render self-test：

- Codex active render 非空。
- Claude active render 非空。
- 快速切换 120 次 scene cache 不超过既定上限。
- 切换后底部软件族文本、边框颜色、额度数字来源一致。
- 没有因 state split 导致 GDI/USER handle 增长。

## 11. 手工验收矩阵

### 11.1 运行场景

| 场景 | 操作 | 期望 |
|---|---|---|
| 仅 Codex 运行 | 设置 Auto | 显示 Codex，额度来自 Codex，Claude state 不变化 |
| 仅 Claude 运行 | 设置 Auto | 显示 Claude，额度来自 Claude，Codex state 不变化 |
| 两者运行，Codex 前台 | 设置 Auto | 显示 Codex |
| 两者运行，Claude 前台 | 设置 Auto | 显示 Claude |
| 两者运行，前台无法识别 | 设置 Auto | 保持上一有效模式 |
| fixed Codex | Claude 前台 | 仍显示 Codex |
| fixed Claude | Codex 前台 | 仍显示 Claude |
| 两者都停止 | Auto | 保持上一有效模式，不请求额度刷新 |

### 11.2 串扰场景

| 场景 | 操作 | 期望 |
|---|---|---|
| Codex 额度失败 | 切到 Claude | Claude 额度不灰、不被清空 |
| Claude 额度失败 | 切到 Codex | Codex 额度不灰、不被清空 |
| CodexRadar 网站失败 | 切到 Claude | Claude Rader 不显示 Codex 错误 |
| ClaudeRadar 网站失败 | 切到 Codex | Codex Rader 不显示 Claude 错误 |
| Codex 触发 RSS reset | 切到 Claude | Claude 不显示“已重置/速蹬” |
| Claude quota near-full | 切回 Codex | Codex 周额度消耗环不从 100 开始扣 |
| Codex provider spike | 切到 Claude 再切回 | Codex 自修复，Claude 不受影响 |

### 11.3 视觉场景

必须在 2880x1800 主屏截图或 render sample 中确认：

- Codex 边框仍为深蓝 3 px 内边框。
- Claude 边框仍为橙色 3 px 内边框。
- 额度环大小、位置、文字字号不因重构改变。
- 右侧 API 摘要位置不变。
- 底部软件族文本仍是 Codex 蓝色斜体 / Claude 橙色粗体。
- IQ、效率、时钟、额度雷达线不发生无关位移。

## 12. 构建与验证命令

候选构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-radar-runtime-isolation-test.exe -Platform arm64
```

候选自测：

```powershell
.\_build\DesktopCodexAssistant-arm64-radar-runtime-isolation-test.exe --test
.\_build\DesktopCodexAssistant-arm64-radar-runtime-isolation-test.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-radar-runtime-isolation-test.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-radar-runtime-isolation-test.exe --test-logger
.\_build\DesktopCodexAssistant-arm64-radar-runtime-isolation-test.exe --test-radar-display-lifecycle --iterations 120
```

渲染样本：

```powershell
New-Item -ItemType Directory -Force .\_build\radar-runtime-isolation-render | Out-Null
.\_build\DesktopCodexAssistant-arm64-radar-runtime-isolation-test.exe --render-codexradar --out .\_build\radar-runtime-isolation-render
.\_build\DesktopCodexAssistant-arm64-radar-runtime-isolation-test.exe --render-clauderadar --out .\_build\radar-runtime-isolation-render
```

JSONL 与路径 gate：

```powershell
@'
import json, collections, sys
files = {
    'Docs/Indexes/FEATURE_INDEX.jsonl': 'feature_id',
    'Docs/Interfaces/INTERFACE_INDEX.jsonl': 'id',
    'Docs/Technical/INDEX.jsonl': 'id',
    'Docs/Maintenance/CHANGELOG.jsonl': 'id',
}
failed = False
for path, key in files.items():
    seen = collections.Counter()
    with open(path, encoding='utf-8') as fh:
        for line_no, line in enumerate(fh, 1):
            if not line.strip():
                continue
            try:
                obj = json.loads(line)
            except Exception as exc:
                print(f'FAIL {path}:{line_no}: {exc}')
                failed = True
                continue
            value = obj.get(key)
            if value:
                seen[value] += 1
    duplicates = [key for key, count in seen.items() if count > 1]
    if duplicates:
        print(f'FAIL {path} duplicate {key}: {duplicates}')
        failed = True
print('FAIL' if failed else 'PASS: jsonl parse + id uniqueness')
sys.exit(1 if failed else 0)
'@ | python -
```

```powershell
@'
import json, os, sys
failed = False
with open('Docs/Indexes/FEATURE_INDEX.jsonl', encoding='utf-8') as fh:
    for line in fh:
        if not line.strip():
            continue
        obj = json.loads(line)
        if obj.get('status') == 'removed':
            continue
        for path in obj.get('primary_files', []):
            if not os.path.exists(path):
                print('FAIL missing feature file', obj.get('feature_id'), path)
                failed = True
with open('Docs/Technical/INDEX.jsonl', encoding='utf-8') as fh:
    for line in fh:
        if not line.strip():
            continue
        obj = json.loads(line)
        for key in ('doc_path', 'spec_path'):
            path = obj.get(key)
            if path and not os.path.exists(path):
                print('FAIL missing technical path', obj.get('id'), path)
                failed = True
print('FAIL' if failed else 'PASS: path existence')
sys.exit(1 if failed else 0)
'@ | python -
```

Diff gate：

```powershell
git diff --check
```

正式构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\Release\DesktopCodexAssistant-arm64.exe -Platform arm64
```

正式 exe 自测：

```powershell
.\Release\DesktopCodexAssistant-arm64.exe --test
.\Release\DesktopCodexAssistant-arm64.exe --test-layout
.\Release\DesktopCodexAssistant-arm64.exe --test-settings-bindings
.\Release\DesktopCodexAssistant-arm64.exe --test-logger
```

部署要求：

- 备份当前正式 exe 到 `_build/formal-backups/<timestamp>-radar-runtime-isolation-<version>/`。
- 覆盖 `Release/DesktopCodexAssistant-arm64.exe`。
- 覆盖正式运行路径 `DesktopCodexAssistant.exe`。
- 如项目镜像路径仍存在，也同步覆盖镜像。
- 停止旧进程，启动新正式进程。
- 验证 FileVersion/ProductVersion、SHA256、PID、Responding。
- 检查 `%LOCALAPPDATA%\DesktopCodexAssistant\logs\error.jsonl` 和 legacy `error.log` 没有新增相关错误。

## 13. 完成定义

只有同时满足以下条件才算完成：

1. Codex/Claude 切换控制器是独立纯逻辑。
2. Codex 和 Claude 有独立 runtime state。
3. 额度快照和额度消耗环基线按 family 隔离。
4. Codex reset/RSS protection 不影响 Claude。
5. Radar 网站健康按 family 隔离。
6. API 摘要防抖不跨 family 泄漏。
7. 自动/手动切换语义与现有自测矩阵一致。
8. 现有 UI 布局无无关变化。
9. 所有新增/修改自测通过。
10. ARM64 正式构建通过并部署重启。
11. 文档、索引、版本和维护日志同步。
12. 至少一次实际切换 Codex/Claude 后没有观察到一帧错误串扰。

## 14. 回滚点

建议分三次可回滚提交或阶段性备份：

1. **Controller-only 回滚点**：只抽离 `RadarSoftwareModeController`，不改运行态。若失败，回滚该文件和调用点即可。
2. **Quota-state 回滚点**：额度 runtime state 隔离完成，网站健康还未拆。若额度异常，可只回滚 quota state 改动。
3. **Full-state 回滚点**：网站健康、API 防抖和切换事务全部收口。部署前必须完成完整自测。

如果中途发现风险过高，允许保留兼容 wrapper，但 wrapper 必须明确只代理 active family state，不能继续作为事实源。

## 15. 主要风险

- `CodexRadarForm` 历史字段多，若一次性删除共享字段，容易引入编译和渲染回归。应先迁移事实源，再删除或收窄旧字段。
- API 摘要同时消费 family-specific Radar health 和 shared service status，拆分时容易误删 OpenAI/Claude public status。
- Claude 独立窗口和 Codex Radar Claude 模式共享部分 reader/cache，不能因为“隔离”而复制一套重复网络请求。
- render scene cache key 若漏掉 family revision，可能继续显示旧 bitmap。
- 过度清空防抖状态会让真实持续错误显示变慢；不清空则会出现切换瞬间旧错误。应使用 per-family debounce state 和切换时间边界处理。

## 16. 执行前必须向用户确认的事项

如果执行时发现以下情况，必须先暂停询问：

- 需要改变当前窗口布局或元素尺寸才能完成。
- 需要删除现有持久化缓存文件。
- 需要改变 Codex/Claude 自动切换规则。
- 需要新增外部接口或新的联网检测。
- 需要 x64 构建或发布。

## 17. 推荐 goal 文案

后续执行 goal 时可使用：

> 按 `Docs/Technical/Codex-RadarSoftwareRuntimeIsolation-SPEC-v1.0.4.61-20260708-133109.md` 执行 Codex/Claude 运行态隔离与切换控制器重构。严格保持现有 UI 布局和自动切换语义；完成后运行 spec 内所有 ARM64 自测、更新文档/索引/维护日志、覆盖正式 exe 并重启程序。

