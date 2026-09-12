# Codex / Claude Radar 数据所有者架构

适用版本：2.0.0.43

本文说明 `CodexRadarForm` 作为永久 headless owner 时的 Codex 公共 Radar、Codex/Claude 官方额度、服务健康、任务状态和只读投影。

## 1. 当前职责

`CodexRadarForm` 的类名为兼容保留；它是数据所有者，不是可见 Radar 窗口。Codex family 维护公共 Radar 模型数据与个人额度，Claude family 只维护官方 Claude Code 额度与相关服务状态。两枚右侧额度方块始终从各自 family 的缓存构建，不依赖当前选中的 family。

相关源码：

| 文件 | 职责 |
| --- | --- |
| `Core/CodexRadarForm.cs` | headless 生命周期、统一调度、Codex 公共 Radar、额度与任务状态 |
| `Core/CodexRadarForm.CodexRadarIntelligence.cs` | 官网综合智能与工程效率公开接口的 schema 适配、模型合并及诊断 |
| `Core/CodexRadarForm.RuntimeState.cs` | `RadarFamilyRuntimeState`、双额度趋势与 family 隔离 |
| `Core/CodexRadarForm.ProjectionState.cs` | 同代 published state 原子替换与 tile/IQ 投影 clone |
| `Core/CodexRadarForm.ClaudeUsage.cs` | 官方 Claude Code usage 调度结果接入 |
| `Core/CodexRadarForm.TileSnapshot.cs` | tile、Codex IQ board、重置与速蹬 board、服务健康的 cache-only 投影 |
| `Core/CodexQuotaHistoryStore.cs` | 已接受 Codex 额度的 7 天无凭据历史、重置分类与后台批量 JSONL 持久化 |
| `Core/ResetSpeedBoardSnapshot.cs` / `Core/ResetSpeedBoardForm*.cs` | 第六左侧 board 的只读 DTO、停靠生命周期与固定尺寸绘制 |
| `Core/OwnerOperationGeneration.cs` | Start/Stop/挂起恢复 generation、取消与迟到提交边界 |
| `Core/BoundedHttpTextReader.cs` / `Core/CodexRadarUrlPolicy.cs` | 有界 HTTP 文本读取与 Radar 精确 URL/SSRF 策略 |
| `Core/ClaudeCodeUsageReader.cs` / `Core/ClaudeCodeUsageScheduler.cs` | Claude 官方额度读取、单飞调度与缓存提交 |
| `Core/DeepSeekServiceMonitor.cs` | 无凭据 DeepSeek 服务可达性探测 |
| `Core/DeepSeekBalanceMonitor.cs` | 可选凭据的官方余额读取、48 小时本地趋势与只读快照 |
| `Core/StatuspageMonitor.cs` | OpenAI/Anthropic 官方状态单飞监控 |
| `Core/CodexTaskMonitorReader.cs` / `Core/CodexTaskPresentation.cs` | Codex 会话增量读取和共享任务快照 |
| `Core/WidgetForm.TileColumn.cs` | 把两个 family 的额度快照写入同一 `MetricTileFeed` |

可见消费者：

| 消费者 | owner API | 内容 |
| --- | --- | --- |
| 右侧 Codex tile / expand | `BuildRadarTileSnapshot(Codex)` | Codex 模型、额度、重置与 5 小时/周趋势预测 |
| 右侧 CLD tile / expand | `BuildRadarTileSnapshot(Claude)` | 固定 Claude/CLD 标签、官方额度、重置与同构趋势预测 |
| 左侧 Codex IQ board | `BuildCodexIqBoardSnapshot()` / `BuildServiceHealth()` | Codex 全模型 IQ、成本、耗时、token、额度趋势、名册与四项服务健康 |
| 左侧重置与速蹬 board | `BuildResetSpeedBoardSnapshot()` | 7 天周额度余量、重置事件、速蹬窗口、重置卡余量、最近到期，以及 Radar 的发重置卡/硬重置判断 |
| Codex Task board / Operation | `CodexTaskPresentation.SnapshotProvider` | 本地 Codex 会话任务状态 |
| 本机 `--balances` CLI | `WidgetForm.BuildAiBalanceShareSnapshot()` | 同一运行实例内的 Codex/Claude 额度与 DeepSeek 余额，只读 JSON |

Claude tile 不生成模型 IQ 或效率；其 `IqKnown`、`EfficiencyKnown` 恒为 `false`，且没有社区评分字段。

## 2. 总体数据流

```mermaid
flowchart LR
    A["WidgetForm hidden host"] --> B["CodexRadarForm headless owner"]
    B --> C["Codex public Radar reader"]
    B --> D["Codex usage provider"]
    B --> E["ClaudeCodeUsageScheduler"]
    E --> F["ClaudeCodeUsageReader"]
    F --> G["claude-quota.ini"]
    B --> H["StatuspageMonitor"]
    B --> I["DeepSeekServiceMonitor"]
    B --> J["CodexTaskMonitorReader"]
    C --> K["Codex RadarFamilyRuntimeState"]
    D --> K
    E --> L["Claude RadarFamilyRuntimeState quota"]
    G --> L
    H --> M["service cache"]
    I --> M
    K --> R["CodexQuotaHistoryStore memory"]
    R --> S["7-day JSONL (background flush)"]
    K --> N["cache-only projections"]
    R --> N
    L --> N
    M --> N
    J --> O["CodexTaskPresentation snapshot"]
    N --> P["right tiles / Codex IQ / Reset-Speed boards"]
    N --> T["current-user balance pipe"]
    T --> U["--balances JSON"]
    O --> Q["Codex Task board / Operation"]
```

网络、磁盘和 provider 工作只在 owner 的既有调度链执行。绘制表面只读取投影，不建立 reader、timer、watcher 或请求。

## 3. Headless 生命周期

`WidgetForm.EnsureCodexRadarWindow()` 构造 owner，并显式调用 `StartHeadlessDataOwner()`：

- 创建隐藏 HWND，供显示电源通知和 `BeginInvoke` 回调使用。
- 应用运行时设置后启动 backend scheduler。
- 不调用 `Show()`，也不执行定位、hover、透明度、burn-in、Z-order 或 layered bitmap 工作。
- `StartHeadlessDataOwner()` / 恢复建立新 generation；所有 Radar、额度、reset credits、Statuspage 与 DeepSeek completion 都捕获对应 lease。
- `StopHeadlessDataOwner()`、挂起和 Dispose 取消当前 generation；迟到结果不得写状态、缓存、业务日志、通知或 UI 回调。

owner 生命周期不受 tile 是否显示影响。全屏隐藏只处理可见表面；显示器关闭、会话锁定或系统挂起时，远程轮询按 `Docs/Component-Refresh-Rules.md` 暂停，恢复后错峰续跑。

## 4. Family 状态隔离

Codex family 保存公共 Radar 模型快照、模型目录、额度、服务状态和各自 deadline。Claude family 只保存官方额度快照、额度来源、reset anchor、消耗基线、趋势样本和服务状态，不保存公共 Radar 模型、IQ 或评分目录。两侧的 5 小时与周额度分别拥有活跃时间样本、近时钟样本和 reset identity；任一 family 或额度窗口都不能复用另一侧的速率。

请求开始时捕获 family，完成时只写回对应状态。当前 active family 在请求期间改变时，结果仍可更新原 family 缓存，但不能覆盖另一 family。`BuildRadarTileSnapshot(Codex)` 与 `BuildRadarTileSnapshot(Claude)` 在同一个 feed 构建周期分别读取对应状态，所以两枚额度方块可同时稳定显示。

## 5. 软件 Family 选择

`CodexRadarSoftwareMode` 支持固定 Codex、固定 Claude 和 Auto。Auto 复用 `SoftwareRuntimePresence`：

1. 固定模式、两者都未运行或只有一者运行时，不查询前台窗口。
2. 两者都运行时才使用包路径、专用进程名、产品元数据和受限标题 fallback 识别前台软件。
3. 无法识别或命中本程序时，保持上一次有效 family。

family 选择只影响 provider 调度优先级和相关服务语义，不决定 Codex/CLD tile 是否存在，也不为 Claude 选择模型。

## 6. Codex 公共 Radar 与模型目录

只有 Codex family 读取公共 Radar 数据与模型目录。`/api/radar-insights` schema 1 的 `comprehensive_points` 是综合 IQ 权威来源，adapter 接受网站已发布且保持同一 points 契约的 `comprehensive_arithmetic_mean` 与 `comprehensive_weighted_mean`，未知算法仍拒绝整批；`/api/intelligence-efficiency-metrics` schema 3 为同模型补充工程通过数、任务数、平均成本、平均耗时和平均 token。adapter 按 `model + effort` 原子配对并把平均 token 转为既有投影所需的总量，任一重复键、缺项或 schema 不匹配即拒绝整批。综合 IQ 覆盖 `current.json` 兼容快照时，模型值、完整目录及 `ModelIqSourceUpdatedAtLocal` 必须作为同一来源一起复制，不能让新 Astra 数据沿用旧批次时间。`current.json` schema 2 继续负责速蹬窗口、RSS 地址和兼容性 IQ 回退；内容签名未变化时保留 source timestamp，fetch timestamp 不能伪造新批次。首页 HTML 只允许补结构化数据缺少的速蹬窗口，以及首页“重置雷达”区成对发布的“发重置卡/硬重置”状态、短结论和更新时间；这些字段有界解析、缓存并保留最多 7 天，TTL 必须按上游判断时间计算，不能由后续 IQ 抓取续期。首页撤下该区块或时间缺失、过期时清空两行并显示官网暂无判断，不能继续展示历史结论。完整综合模型目录才能推进模型缺失计数，部分损坏数据只能补充已见模型，不能证明其它模型消失。

分布式 Radar 的 `comparisons` 键可能附加 `_distributed` 等来源后缀。适配器先尝试精确键，再根据节点自身的 model 与 reasoning effort 生成稳定模型键；只有唯一匹配才接受，重复歧义时 fail closed。看板的 `Current` 标记跟随用户选择的稳定模型键，而不是固定跟随 `latest` 根节点。上游 `recent_days` ISO 时间戳按秒保留并以本地 ISO 秒精度写入缓存，旧版 `yyyy-MM-dd-am/pm` 历史仍可读取；来源任务数使用 10000 的防御上限，不再受手动校验设置的 100 条上限截断。

Codex 的模型选择、IQ、评分、效率、通知状态与 `Codex IQ` board 均留在 Codex 侧。Claude family 不参与公共 Radar 请求、模型目录、模型自动切换或数据周期判定。刷新周期与重试规则只在 `Docs/Component-Refresh-Rules.md` 维护。

## 7. 额度数据与身份保护

### 7.1 Selected-provider gate

个人额度只为当前有效且对应本地程序正在运行的 family 排队：

- Codex：ChatGPT backend usage 与本地 session fallback。
- Claude：`ClaudeCodeUsageScheduler` 的官方 usage/statusline 链。
- 两者都未运行时保留最近快照，不同时 prime 两套 provider。

启动、恢复、网络变化、手动刷新或 family 切换只让适用 provider 到期。可见消费者始终只读已提交的 last-good 快照。

大陆出口保护在冷启动/换网未知期会 fail-closed，因此敏感端点可能先得到 `AI_BLOCK`。`WidgetForm` 确认出口为境外后调用 `CodexRadarForm.RequestSensitiveAiRefreshAfterEgressAuthorization()`，仅把适用 Codex/Claude 额度、reset credits 与 OpenAI/Anthropic Statuspage 调度置为到期；公共 Radar、DeepSeek 与其它网络探测不受该边沿影响。

### 7.2 Codex 账户身份

Codex CLI 一次只持有一个账户：`<CODEX_HOME>/auth.json`（未设置时为 `%USERPROFILE%\.codex\auth.json`）。切换账户就是覆写该文件，因此身份必须随每次读取重新解析，不能在启动时确定一次。

`CodexHome` 是 Codex CLI 路径的唯一解析点：`ResolveRoot()` / `ResolveAuthJsonPath()` / `ResolveSessionsPath()` / `ResolveSessionIndexPath()` 一律遵循 `CODEX_HOME`，令 token、rollout 历史与 `session_index.jsonl` 始终来自同一个 home。

`CodexHome.ReadCurrentIdentity()` 按 auth.json 的写入时间与长度缓存解析结果，命中变化才重解析：主键取 `tokens.account_id`，缺失时在 `tokens.id_token` 的 JWT payload 里取 `https://api.openai.com/auth.chatgpt_account_id`，再缺失时回落到 `chatgpt_user_id`；`auth_mode = apikey` 归入固定的 `apikey` 键。展示字段取 JWT 的 `email` 与 `chatgpt_plan_type`，并由 usage 端点返回的 `email` / `plan_type` 覆盖（`RefreshCodexAccountIdentity()` 只在两侧账户键一致时合并）。JWT 只在内存中解码，access/refresh/id token 一律不落盘、不入日志；对外的显示名由 `CodexAccountIdentity.ResolveDisplayLabel()` 生成，邮箱本地部分打码。

### 7.3 Codex 额度

Codex provider 只读已配置的环境变量或 Codex `auth.json` 凭据，不写回。5 小时与周额度分别维护 reset anchor、余额、来源和消耗基线。只有通过窗口身份、漂移容差与异常跃迁保护的结果才提交到缓存。

`ReadQuotaSnapshot()` 先解析一次身份，再把 provider / session / cache 三级回退的结果统一打上 `AccountKey` 与 `AccountLabel`。`codex-radar-cache.ini` 写入 `AccountKey=`；读回时账户不匹配的缓存直接降级为 default，不作为当前余额展示。

rollout 的 `rate_limits` 块不含任何账户 id（同一个被续用的 session 文件里可以并存两个账户的记录），因此 `IsSessionQuotaEventAttributable()` 对该来源加两道拒绝：事件时间早于当前账户的 `active_since_utc` 时拒绝（本机只见过一个账户时该闸门不启用），事件 `plan_type` 与当前账户 plan 冲突时拒绝。

`quota-decision-history.jsonl` 只记录额度判定所需摘要，不记录 token、提示词、响应正文或授权 header。另有 `codex-quota-seven-day-history.jsonl` 只保存通过保护链的时间、5 小时/周余量、reset anchor、重置卡计数与重置类型；同样不保存凭据、token、请求正文或身份信息。

### 7.4 Claude 额度

Claude Code 用量由进程级 `ClaudeCodeUsageScheduler` 单飞读取，结果一次提交到 Claude family，并由 `ClaudeCodeUsageReader` 原子写入 `claude-quota.ini`。只有同时包含 5 小时/周额度、两组 reset、可信更新时间且满足新鲜度规则的完整快照才会发布、落盘或在启动时恢复；部分结果保留 last-good。

CLD tile 的固定模型标签为 `Claude`，紧凑标题为 `CLD`；额度与两个 reset 只来自官方 usage/statusline 链。详细边界见 `Docs/Codex-ClaudeRadar-Architecture.md`。

### 7.5 双窗口趋势与续航

`CodexRadarForm.ApplyQuotaSnapshot()` 先按快照的 `AccountKey` 调用 `EnsureCodexQuotaStateForAccount()`：Codex family 的 `QuotaRuntimeState` 按账户整体换出/换入，离开的账户被寄存在进程内（上限 `CodexAccountStore.MaxAccounts = 12`，按最旧 burn clock 淘汰），切回时恢复它自己的样本而不是从零重来。`RecordQuotaBurnSamples()` 另有一道防御：快照账户与当前 trend 账户不一致时直接丢弃。没有这层隔离，一次切换会被读成一次巨量消耗。

随后只把通过既有 identity、漂移和异常跃迁保护的已接受快照交给 `RecordQuotaBurnSamples()`。每个 family 的 5 小时与周额度分别维护两条进程内历史：活跃时间轴用于回答“保持当前使用强度还能用多久”，近时钟时间轴用于在活跃样本不足时给出节奏估算。软件未运行、长时间 owner tick 间断或新活跃会话会重建活跃历史，但不会把这段时间计入活跃速率；reset identity 改变或余额上升只清除对应额度窗口的两条历史。

`TryComputeQuotaBurnRate()` 对最近 1.5 个活跃小时的 5 小时额度、最近 6 个活跃小时的周额度，以及各自最近 5/24 个时钟小时进行估算。至少需要 10 个活跃分钟或 30 个时钟分钟，并且整数百分比来源必须出现至少 1% 的已接受下降；端点速率与 pairwise 中位速率组合以减轻单次整数跳变。样本跨度、下降幅度和点数共同形成低/中/高置信度。

`BuildRadarTileSnapshot()` 只从同代 published projection 计算展示 DTO：优先发布活跃时间续航，活跃样本不足时才以近 24 小时节奏作为周额度主结论。`MetricTileExpandForm.DrawRadarQuota()` 将续航与 reset 距离比较，明确显示“预计多久用完并早于重置多久”或“可撑到重置并多余多久”；5 小时窗口在底部独立给出相同判断。周趋势实线只占图表前 68%，剩余区域用于虚线预测、耗尽交点和 reset 线。计算和绘制都不启动 provider、网络或磁盘读取，进程重启后重新积累样本。

### 7.6 Codex 7 天重置与速蹬历史

`CodexQuotaHistoryStore` 仅接收 `ApplyQuotaSnapshot()` 已接受且允许记录 decision 的 Codex family 快照，并且只接收**重置保护改写之前**的那份读数：`ApplyQuotaSnapshot()` 先用 `CaptureAcceptedQuotaForHistory()` 克隆已接受快照，再让 `ApplyQuotaResetProtections()` 原地把展示快照改写成 100；`RecordAcceptedQuotaHistory()` 是写入本存储的唯一入口，只接受这份已接受读数。保护值继续供右侧 tile、provider 缓存与 `codex-quota.ini` 回退使用，但不得进入历史——`Record()` 会把强制的 44 → 100 读成一次重置事件，而 `ForceWeeklyQuotaToFull()` 同时清掉的 `weekly_reset_known` 还会让下一次真实重置失去判定自然重置所需的 anchor。`RunQuotaHistoryProtectionSelfTest()`（`--test-layout`）守这条不变量。每行带 `account_key`，`Record()` 的重置分类与采样间隔过滤只与**同账户**的上一条比较，`GetSnapshot(nowUtc, accountKey)` 只投影该账户的行；没有 `account_key` 的历史行留在 `unknown` 桶里，不并入任何账户。内存立即更新，磁盘由 15 秒 ThreadPool timer 批量写入；普通样本至少间隔 15 分钟，或周余量变化达到 3%，周余量回升达到 5% 时立即登记。旧 reset anchor 前 15 分钟至后 6 小时内的回升标记为自然重置；重置卡计数同时减少时标记为重置卡；其余标记为硬重置。每 6 小时和 owner 退出时原子裁剪到最近 7 天、最多 2048 行。

### 7.7 账户名单与切换

`CodexAccountStore` 维护本机 Codex 账户名单。名单在正常使用中被动建立：`RefreshCodexAccountIdentity()` 每次解析出账户后调用 `ObserveActiveIdentity()`，首次见到的账户分配一个稳定的 A/B/C… 字母并登记，auth.json 变动时刷新其凭据快照。字母一经分配不再变动，读不到邮箱的账户就以字母作为全名显示。

- `codex-accounts.jsonl`（明文索引）只存 `account_key`、`letter`、打码邮箱、`plan_type`、`user_id`、`auth_mode` 与时间戳，不存 token，也不存完整邮箱。
- `<account_key>.bin` 存该账户 auth.json 的完整内容，经 `SecretStore` 的 DPAPI（CurrentUser）保护，与 DeepSeek key、Claude setup token 同一防护等级。
- `TrySwitchTo()` 只在用户显式点击时执行：校验快照解析出的账户与目标一致后，先把现有 auth.json 复制为 `auth.json.dca-bak`，再原子替换。校验失败一律 fail closed。

`BuildResetSpeedBoardSnapshot()` 从同代 Codex published projection、reset-credit 缓存和 store 内存生成 7 个日点、最近事件及仍在 7 天来源 TTL 内的 Radar 重置判断，并由 `FillResetSpeedAccounts()` 附上当前账户与名单。名单来自 owner 内存缓存（`PeekCodexAccountRoster()`），投影路径本身不读账户存储。右侧 Codex tile 恒定显示当前账户（`RadarTileSnapshot.AccountLabel` / `AccountLetter`），不提供切换入口；切换只在左侧 board 上。`ResetSpeedBoardForm` 每 5 秒 clone 一次；绘制路径和 snapshot 构建都不读取磁盘、凭据或网络。该持久历史与 Radar 判断只服务 Codex board，不改变 Claude family 的官方额度趋势和右侧续航计算。

## 8. 服务健康

owner 复用进程级 `StatuspageMonitor` 与 `DeepSeekServiceMonitor`，向 Codex IQ board 发布四项健康状态；Network Dock 不再持有这组重复 LED：

| 标识 | 含义 |
| --- | --- |
| `R` | Codex 公共 Radar 数据源 |
| `O` | OpenAI 官方状态 |
| `C` | Anthropic 官方状态与 Claude usage |
| `D` | DeepSeek 服务可达性 |

这里的 DeepSeek 健康项不读取凭据，只输出 `known`、`available`、错误码与检查时间。HTTP 鉴权或请求格式响应可证明网关可达；无响应、拒绝或服务端故障按服务异常语义分类。

账户余额是独立的 `DeepSeekBalanceMonitor`：只有用户配置 API Key 后才访问官方 `/user/balance`，并把当前余额、24 小时本地消耗、预计可用时间和最多 96 个绘图点作为 clone 快照交给右侧 `DS` tile/expand。Key 使用 CurrentUser DPAPI envelope，余额历史不包含 key、Authorization header 或响应正文；服务健康失败与账户鉴权失败不得互相改色或覆盖。

服务错误经过 `ServiceAlertDebouncer`；检测中立即发布，新错误稳定后发布，恢复立即清除。`BuildServiceHealth()` 只复制已有状态，不触发探测。具体调度与手动刷新语义见 `Docs/Component-Refresh-Rules.md`。

## 9. Codex Task 后端

owner 注册 `CodexTaskPresentation.SnapshotProvider`，并在既有 scheduler 中请求轻量任务刷新：

- `%USERPROFILE%\.codex\sessions` 只维护一套递归 watcher。
- watcher 事件用于逐文件增量尾读，低频完整对账兜底漏报。
- reader 不创建独立 timer；presentation 层把缓存映射为颜色、环、徽标和行模型。
- 展示不输出提示词、回复或完整会话路径。

owner 停止时清除 provider，避免消费者调用已销毁实例。

## 10. 左侧 Radar Boards 投影

`BuildCodexIqBoardSnapshot()` 只复制 Codex family 的全模型数据、选中模型历史、额度趋势、名册和服务健康。`CodexIqBoardForm` 不访问网站、不读取公共 Radar 文件，也不建立 reader。

IQ board 的 tab、展开/收起和渲染属于左侧停靠运行时，不改变 owner 的业务周期。

`BuildResetSpeedBoardSnapshot()` 同样只复制 Codex published quota/Radar、reset-credit 缓存和已载入的 7 天历史。`ResetSpeedBoardForm` 与 Spec Board 保持相同逻辑尺寸；底部“近期重置”缩为左半区并最多列两条事件，右半区“重置概率”显示 Radar 日期及“发重置卡/硬重置”的状态和短结论。状态宽度由实际等宽字体测量，短结论独占下一行完整宽度并按实测宽度缩放、禁止省略，使“本轮是硬重置，不会发新卡”“官方重置窗口已开启”等完整句子不被截断；官网未提供有效判断时显示缺省提示而不渲染过期两行。黄色角色边框、绿色额度轨迹、青色速蹬圆环、重置类型与判断色只承担展示语义。

Codex IQ 与重置/速蹬 board 的底部操作轨都提供绿色“刷新”和红色“关闭”。刷新只重新克隆已发布快照并重绘，不触发 owner 调度周期、网络访问或持久化读取；顶部不再绘制第二个关闭入口。

三块 Codex 派生 board 使用各自空间约束下的可读性字号层级。Codex Task 卡片标题/行/副行/底栏为 `S(11.5)/S(9.6)/S(8.2)/S(8.4)`，token 与时间轴标签为 `S(7.4)`；Codex IQ 标题 `S(13)`、正文 `S(9)`、强调正文 `S(9.6)`、辅助文字 `S(8)`、等宽数值 `S(9)`、leader 数值 `S(20)`，迷你趋势的 `100` 标签下限为 7 像素；重置与速蹬标题 `S(13)`、段头 `S(9.2)`、正文 `S(8.5)`、辅助文字 `S(7.8)`、等宽数值 `S(8.8)`、表盘主数值 `S(20)`。布局仍以实测文字尺寸、卡片可用宽度和省略规则为边界，不因放大字号改变快照或调度语义。

## 11. Cache-only 展示边界

以下方法必须保持纯缓存读取：

- `BuildRadarTileSnapshot(CodexRadarSoftwareMode)`
- `BuildCodexIqBoardSnapshot()`
- `BuildResetSpeedBoardSnapshot()`
- `BuildServiceHealth()`
- `CodexTaskPresentation.SnapshotProvider`
- `WidgetForm.BuildAiBalanceShareSnapshot()`

它们从一次原子发布的 `RadarPublishedProjectionState` clone 后再格式化和映射，不得跨锁读取可变 owner 字段，也不得发起 HTTP/provider 请求、读取凭据或磁盘缓存、修改 deadline、触发模型切换，或修改 reader-owned state。模型目录只在 owner 启动、成功刷新或显式 cache reload 时载入内存；5 秒 IQ 投影不轮询磁盘。

常驻 `WidgetForm` 只在正式应用生命周期内启动当前 Windows 用户命名空间的 `AiBalanceShareServer`。每次客户端连接只克隆上述已发布额度与 `DeepSeekBalanceMonitor.GetSnapshot()`，按 schema 1 输出一行 JSON；服务端不接受刷新命令或输入数据。`DesktopCodexAssistant.exe --balances` 在获取成功时退出 `0`，常驻实例未运行、访问被拒绝、超时或响应无效时退出 `2` 并把机器可读错误写到 stderr。管道响应不含 token、Cookie、API Key、Authorization、provider 原文或账户标识。

## 12. 设置边界

设置页保留 Codex 公共数据、family 选择、Codex 模型/周期、个人额度、服务探测、Claude setup-token、DeepSeek API Key 与测试配置。Claude 不提供公共数据源、社区评分、模型选择或本地公共额度 fallback；DeepSeek Key 只进入独立 DPAPI 文件，不进入 `settings.ini`、日志或快照。

全局布局编辑器登记 `MetricTile.CodexQuota`、`MetricTile.ClaudeQuota`、`MetricTile.DeepSeekQuota` 以及左侧 `CodexIq`、`ResetSpeed`、`SystemDay` tabs；headless owner 不在 19 个布局项中。

## 13. 故障、安全与验证

- 网络失败保留对应数据源的 last-good snapshot，并按失败策略重试。
- 迟到请求只写回捕获的 family；owner 已停止时丢弃。
- 凭据、Cookie、setup-token、Authorization header、完整响应和用户会话正文不得进入日志；身份变化诊断只记录规范化元数据，不写 provider raw body。
- 所有活动 HTTP 文本路径使用 `BoundedHttpTextReader` 的大小、总时限、取消、解压和严格 UTF-8 边界；Radar 可配置 URL 必须通过 `CodexRadarUrlPolicy` 的精确 HTTPS endpoint 与 DNS 私网拒绝。
- 随机测试状态不能写真实额度缓存、模型目录或历史。
- snapshot 构建异常降级为空/旧快照，不能阻塞 Widget UI tick。
- 余额共享管道使用当前用户身份派生的版本化名称，后台阻塞线程与 UI 消息循环隔离；退出先以有界本机连接唤醒并停止管道，再释放 headless owner。

建议验证：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-test.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-test.exe --test
.\_build\DesktopCodexAssistant-arm64-test.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-test.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-test.exe --test-radar-display-lifecycle --iterations 20
.\_build\DesktopCodexAssistant-arm64-test.exe --render-tilecolumn --out .\_build\tilecolumn
.\_build\DesktopCodexAssistant-arm64-test.exe --render-resetspeedboard --out .\_build\reset-speed
.\_build\DesktopCodexAssistant-arm64-test.exe --balances
```

验收重点是：owner 始终隐藏、Start/Stop 完整、两套额度与四条趋势历史按 family/window 隔离、重置与速蹬投影无同步 I/O、Codex/CLD 展开窗给出 5 小时和周额度的耗尽/重置判断、Codex 第六 board 提供 7 天历史/重置/速蹬/重置卡及两条 Radar 重置判断、CLD 仍只使用官方额度源、DeepSeek 服务健康与账户余额严格分离、`--balances` 只返回同实例快照且不触发网络/凭据读取、Key 只以 DPAPI 密文落盘，以及 11 tile / 7 dock 布局完整。
