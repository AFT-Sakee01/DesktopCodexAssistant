# 组件刷新规则

适用版本：1.0.4.29

本文集中记录 Desktop Codex Assistant 各组件的刷新、轮询、手动刷新、网络事件、单飞任务和暂停恢复规则。修改定时器、刷新 token、网络事件、后台任务节流、测试模式或显示恢复逻辑时，应同步更新本文。

## 1. 维护边界

需要同步更新本文的变更：

- `WidgetSettings.Get*Interval*` 返回值、性能模式语义或默认刷新间隔变化。
- 任一窗口 `Timer`、`FileSystemWatcher`、`NetworkChange`、全局热键、显示恢复、手动刷新或测试模式行为变化。
- 任一 reader 的 `requestRunning`、缓存 TTL、冷却、状态滞后确认、generation 或取消策略变化。
- 隐藏、息屏、会话锁定、系统挂起、显示恢复和分辨率切换期间刷新策略变化。
- 操作面板刷新按钮、设置页手动刷新按钮或 `ForceRefresh` 覆盖范围变化。

不需要同步更新本文的变更：

- 纯视觉颜色、字体、文案、局部坐标变化，且不影响刷新频率或触发条件。
- 不改变外部行为的内部重命名。

## 2. 全局时间基准

主要时间策略集中在 `Settings/WidgetSettings.cs`。

| 策略 | 性能 | 均衡 | 省电 | 说明 |
| --- | ---: | ---: | ---: | --- |
| 主性能采样 | 500 ms | 1000 ms | 2500 ms | 主窗口 PDH 快照调度。 |
| 普通面板调度 | 500 ms | 1000 ms | 3000 ms | 子面板检查上限；无显示变化时不重绘。 |
| GPU/NPU 昂贵采样 | 1000 ms | 2000 ms | 5000 ms | 避免高频枚举大量 GPU Engine 计数器。 |
| 悬停动画 | 16 ms | 33 ms | 100 ms | 只在透明度或按压动画未完成时使用。 |
| 静止交互轮询 | 30 ms | 100 ms | 250 ms | 鼠标位置、自动穿透和防遮挡轮询。 |
| 本地网络信息 | 2 s | 5 s | 网络事件驱动 | 省电模式依赖 `NetworkChange`。 |
| 公网 IP | 5 min | 10 min | 15 min | 仅真实网络为 `Online` 时执行。 |

连通性检测：

| 网络状态 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| `Online` | 10 s | 30 s | 60 s |
| `NeedsValidation` | 5 s | 10 s | 30 s |
| `Offline` | 3 s | 5 s | 10 s |
| `AdapterMissing` | 不轮询 | 不轮询 | 不轮询 |
| `Unknown` | 立即 | 立即 | 立即 |

DNS 检测：

| DNS 最差状态 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| `Unknown` | 15 s | 30 s | 60 s |
| 异常、劫持或不可用 | 30 s | 60 s | 120 s |
| 全正常 | 5 min | 10 min | 15 min |

## 3. 主窗口与全局协调

源码：`Core/WidgetForm.cs`、`Performance/PdhSampler.cs`

| 项目 | 规则 |
| --- | --- |
| 主控制 tick | 使用主性能采样间隔；每次检查停止事件、设置热加载、全屏可见性、PDH 采样和绘制。 |
| 隐藏状态 | 控制循环保留。省电模式隐藏时跳过昂贵 PDH 采样；性能/均衡仍按当前策略保持必要状态。 |
| 昂贵硬件采样 | GPU/NPU 等按 1/2/5 秒独立节流，不跟随主 CPU/磁盘快照高频刷新。 |
| 设置热加载 | `settings.ini` 由 `FileSystemWatcher` 和主 tick 修改时间检查共同覆盖。 |
| 显示恢复 | 首次延迟 350 ms，最多 3 次；后续重试间隔 1500 ms。恢复会重建 layered-window 资源、重定位并强制刷新。 |
| SeelenUI 拉前 | 设置开启时按本地整点和半点计划；最大化或全屏前台场景按现有逻辑跳过。 |
| Win+D 恢复 | 全局 Win+D 后延迟 2000 ms 执行本程序和 SeelenUI 拉前；不拦截系统显示桌面，设置可关闭。 |
| 休眠唤醒重启 | `PBT_APMRESUMEAUTOMATIC`、`PBT_APMRESUMESUSPEND` 或 `PBT_APMRESUMECRITICAL` 后先完成 3 轮显示恢复，再按设置重启 SeelenUI 和本程序；设置可关闭，30 s 内重复恢复事件只处理一次。 |
| 敏感鼠标模式 | 悬停隐藏默认用以鼠标为中心的正方形命中范围与各窗口矩形相交，默认边长 100 px，可在 10-300 px 设置；关闭后退回点命中。 |
| 延迟显现 | 普通“鼠标移上去隐藏”在鼠标离开判定区后继续隐藏 `1-10 s`，默认 1 s；离开倒计时内重新进入判定区并停留 `0.1-5 s`，默认 0.5 s，才重置本轮离开倒计时。倒计时到期时若鼠标仍在判定区，窗口保持隐藏。 |
| 覆盖开启 | 自动隐藏已经激活时，普通鼠标移动不再释放隐藏；只有鼠标进入任一程序窗口的敏感命中范围后才重置空闲计时并恢复。 |
| 反向隐藏 | 操作面板手动隐藏时，鼠标进入某个程序窗口的敏感命中范围会临时恢复该窗口，移开后按 1-30 s 延迟重新隐藏。 |
| 防烧屏位移 | 每 7 min 生成新 slot；主窗口和子窗口按各自 salt 微位移。 |
| 操作面板刷新 | `ForceRefreshAllModules()` 触发主采样、磁盘用量刷新、Codex、功耗、网络和 CleanIP 刷新。 |
| 诊断日志 | 主采样诊断最多每 15 min 写一次；`TimingStats12h` 耗时摘要也最多每 15 min 写一次，具体样本只保存在内存滚动窗口中。UI 无响应看门狗由后台线程每 2 s 检查 UI 心跳，超过 10 s 写入 `ui-hang-watchdog.jsonl`，持续无响应每 30 s 重复记录，5 min 以上挂起空洞不报。 |

## 4. Codex Radar

源码：`Core/CodexRadarForm.cs`、`Core/CodexRadarModelCatalog.cs`

| 项目 | 规则 |
| --- | --- |
| UI 调度 | 使用普通面板调度 500/1000/3000 ms，并贴近秒边界；网站请求完成后可立即绘制。 |
| 随机整窗测试 | 测试开启时暂停真实网站、Claude 和额度轮询；手动 token 立即重建，自动刷新最多 1 s 一次。旧五阶段连接流程已删除，不参与真实轮询。 |
| 检测软件 Auto | `CodexRadarSoftwareMode=Auto` 先读取共享 `SoftwareRuntimePresence` 快照；固定 Codex/Claude、两者都未运行或只有一者运行时不查询前台窗口。只有 Codex 与 Claude 都运行时，才按前台窗口标题/进程名识别 `codex` 或 `claude`；识别不到或命中本程序时保持上一次有效软件。有效软件或模型切换前会把当前 Radar 快照、额度快照、额度来源和 Radar 服务健康态写入内存缓存；切换后优先恢复目标软件同模型的内存缓存，其次读磁盘缓存，仍缺失时保留上一帧内容，直到新网站/额度数据成功返回，避免切换瞬间出现空态或错误态闪烁。`--test` 通过 `RunSoftwareModeGateSelfTest` 覆盖固定模式、无进程、单进程、双进程前台检测失败和 selected-provider 额度刷新门控。 |
| 运行态快照 | `SoftwareRuntimePresence` 只按明确可执行名查询 Codex/Claude 进程，性能/均衡/省电刷新间隔为 3/5/10 s；绘制路径只读最近快照，不做进程枚举或前台窗口查询。 |
| 额度进程检查 | Codex 额度路径复用共享运行态快照。两者都未运行时跳过个人额度 provider 和本地 session 额度读取，保留最近快照；启动、恢复、网络变化、手动刷新、软件切换或测试模式恢复只通过统一 selected-provider gate 排队当前有效且正在运行的软件，不再同时 prime Codex provider 与 Claude Code usage。 |
| Claude Radar 运行态 | 独立 Claude Radar 窗口只在构造、设置应用、手动刷新和性能模式定时器中刷新共享运行态快照；额度数字绘制只读本地缓存快照，已知数值在 Codex/Claude 任一程序运行时为白色，无数值或两者都未运行时才灰显。公共 Claude Radar 网站数据不按本地进程状态门控。 |
| Claude Radar 场景缓存 | 独立 Claude Radar 窗口最多缓存 6 张预渲染 scene bitmap，key 包含窗口尺寸、渲染变体、透明度、防烧屏、运行态、请求状态、动画/状态轮换相位、模型、模型 `latest_at`、底部 `RC` rating key/label、余额、IQ、效率、服务状态和额度线签名；尺寸变化、显示挂起和关闭时释放。底部栏绘制只消费 `ClaudeRadarSnapshot` 已解析字段，禁止在 paint 路径读取 `claude-radar-model-map.ini` 或其它磁盘文件。Claude 家族底部简称使用前两位家族前缀和 Codex 同类档位规则，例如 `Opus 4.8 high` -> `Op4.8H`、`Fable 5 max` -> `Fa5MAX`、`Sonnet 5 ultra` -> `So5Ult`。`--test-layout` 会验证状态矩阵渲染非空、scene cache 超限裁剪、释放清空，以及 120 次高频切换中 6 个 warmed scenes 后续全部命中缓存；`--test-radar-display-lifecycle` 会在禁用远程源的情况下反复运行 Codex/Claude Radar display suspend/resume 并检查 handle/GDI/USER 增量；`--render-clauderadar` 会输出 normal、missing-data、warning、error、offline、test-randomized 及对应 2880x1800 桌面图。 |
| Claude Radar 额度线 | 独立窗口额度线读取 `data/claude-code-radar.json` 的 `quota.chart.trend`，兼容 `d7` / `total_7d` 等 7d 图表 key；无图表时退回 `quota.base_d7_trend`，再退回 `claude-radar-quota-history.jsonl` 最近 7 天，最后才用单点当前值。`quota.base_d7` 缺失时从 `quota.metrics` 的 d7/total_7d 行取当前值并按 metric/update/run 签名写历史。`claude-radar-cache.ini` 保存 `QuotaLineKnown/Current/Previous/Min/Max/Average/Metric/SourceMode`，启动和失败合并不清空额度线；paint 路径只消费已解析快照。`--test` 覆盖当前站点 `quota.metrics + chart.key=total_7d` 形状和缓存 round-trip。 |
| Claude Radar 首页 metadata fallback | 独立窗口优先读取 `data/claude-code-radar.json`；首页 metadata fallback 受 `ClaudeRadarHomepageFallbackEnabled` 门控，关闭时不得读取首页 HTML，也不得因为 JSON 失败或 metadata 不完整而探测首页。开启时只在主 JSON 请求失败、`iq.models` 缺失、模型名缺失或模型名等于 key/key-only 时，才顺序读取首页 HTML 并解析 `MODEL_NAMES` 作为弱 metadata fallback；JSON 已足够时不得预读首页。首页 fallback 只补 source key/display name 等弱 metadata，不伪造 IQ、效率、额度、reset、服务健康或社区评分；homepage-only 目录是弱目录，不参与 `MissingSuccessCount` 或连续缺失计数，不会把未在首页出现的旧模型标记为 temporarily_missing/deleted，也不得触发模型删除。 |
| Claude Radar 服务健康 | 独立窗口右侧 `R/C/U` 方块分别表示 Claude Radar data/homepage、Claude 官方 Statuspage summary、Claude Code usage。`C` 不再代表社区评分；社区评分只影响体感分数据。官方 Claude 状态只在 Claude Radar 窗口真实网站刷新路径中顺序读取 `https://status.claude.com/api/v2/summary.json`，不探测 Codex/OpenAI/ChatGPT。状态颜色和叉号语义为：正常绿色无叉、离线灰色无叉、元素缺失灰叉、服务响应但不可用黄叉、DNS/TLS/超时/连接失败红叉。右侧服务点和 API 摘要共用 `ApplyClaudeServiceAlertDebounce`，新错误需连续存在 10 s 才显示，恢复正常立即清除；随机测试模式旁路防抖。 |
| Claude Radar 模型目录安全 | 只有 `ok=true` 且 `iq.models` 数组完整、每项都有唯一 key、可解析模型数等于原始模型项数时，才把本次响应视为完整目录并推进 `MissingSuccessCount`/temporarily_missing/deleted。非空但部分损坏的 JSON、`ok=false`、schema 缺失和 homepage-only 弱目录都只更新已见项，不证明旧模型消失。`--test` 通过 reader 失败 fixture 覆盖 HTTP 失败、超时、离线、DISABLED 和 unsupported/unavailable 状态映射。 |
| Claude Radar 模型目录 | 成功读取 JSON 后更新 `claude-radar-model-map.ini`；首次建表不弹通知，之后新增、暂缺、恢复和连续缺失删除按 `claude-radar-notification-state.ini` 去重后通过托盘通知。设置页的模型选择使用五列按钮网格，空槽显示禁用 `--`，`自动` 映射为空 key，pending/temporarily_missing/deleted/disabled 模型只显示但不可选；“编辑映射”只修改 display_name、rating_key、sort_order 和 enabled。`source_key` 与社区评分 `rating_key` 是两个外部命名空间，新模型或缺失 `rating_key` 的启用项保持 pending，不能按显示名自动合并成 active；旧缓存中 `active` 但缺失 `rating_key` 的行也必须在保存和设置选择器中降级为不可选。`--test-layout` 通过内存状态覆盖通知首次触发、同状态不重复、重启状态不重复、删除和删除后再新增。 |
| Claude Radar 个人余额 | 独立窗口在启用、可见、未挂起、非随机测试且本地 Claude 进程运行时通过 `ClaudeCodeUsageScheduler` 加入共享 Claude Code usage 刷新；成功结果由调度器写入一次 `claude-quota.ini`，窗口消费同一结果并立即更新 5h/7d 额度环。启动或网站缓存加载时会叠加最新 `claude-quota.ini`；个人缓存必须同时含 5h 和 7d 百分比才有效，没有个人缓存时才使用带 `QuotaSource=site` 标记的站点公开 quota usage 作为显示 fallback。`claude-radar-cache.ini` 同时保存 `SelectedModelKey/Name`、选中模型 `LatestAtUtc`、`CommunityKnown/RatingKey/Label/Average` 与 `QuotaLine*`，用于启动、失败合并、IQ 时钟稳定回显和额度线保留；`RC` 来源为 `model-ratings` 中 `average` 最高的一条，平分时取 `count` 更大的条目。`claude-radar-quota-history.jsonl` 写入按 metric/update/run 全量去重，读取时跳过单行坏 JSON 不阻断后续好行，裁剪只保留有效新行。Reader 存储隔离自测使用临时目录证明随机测试快照不写缓存/模型表/额度/历史，Claude 写入不修改 Codex 哨兵缓存，并校验社区评分元数据与额度线可 round-trip。 |
| Codex provider 用量 | 只由 selected-provider gate 在当前检测软件为 `CODEX` 且 Codex 本地程序运行时读取 `chatgpt.com/backend-api/wham/usage`；正常 5 min，普通失败 10 min，HTTP 429 冷却 15 min，单飞。凭据只读取 `CODEX_ACCESS_TOKEN` 或 `%USERPROFILE%\.codex\auth.json` / `CODEX_HOME\auth.json`，不写回、不刷新本地认证文件。`used_percent`/`used_percentage` 按百分数解释，`utilization` 的 0–1 值才按比例换算；若单次 provider 样本把 5 小时或周余额从高位直接降为 0 且 reset 时间没有实质推进，则丢弃该快照并继续保留上一帧和缓存。AI 请求保护手动阻断或自动模式命中 GFW 明确阻断时，本轮不读取 token、不发起 HTTP，结果按 `AI_BLOCK` 失败间隔处理。 |
| 活跃额度 fallback | Codex 正在运行且 provider 无新鲜快照时，10/15/30 s 刷新本地 session 额度。 |
| 非活跃额度 fallback | 旧的 Codex 不运行低频 session 读取只作为回滚边界保留；当前活动调度在 Codex 未运行、Claude 运行但未选中 Codex、或两者都停时不读取本地 session 额度。reset 到期显示依赖已缓存快照和保护逻辑，不启动额外 session 扫描。 |
| Claude Code 用量 | Codex Radar 共享过渡窗口只由 selected-provider gate 在当前检测软件为 `CLAUDE` 且 Claude 本地程序运行时加入刷新；独立 Claude Radar 作为固定 Claude family 窗口，在启用、可见、未挂起、非随机测试且 Claude 本地程序运行时加入刷新。两者共用进程级 `ClaudeCodeUsageScheduler`：任意时刻只允许一个 Claude Code usage 请求，正常 5 min、普通失败 10 min、HTTP 429 15 min 退避；两个窗口在同一轮请求中消费同一个结果，成功只写一次 `claude-quota.ini`。默认读取链路不发 Claude API 请求：先读 `%LOCALAPPDATA%\DesktopCodexAssistant\claude-statusline-quota.ini`，有效且不超过 360 min 时写入隔离的 `claude-quota.ini` 并刷新 5h/7d 环；没有缓存时尝试安装 `%USERPROFILE%\.claude\desktop-codex-statusline-bridge.ps1` 和 Claude Code `statusLine` 命令，等待 Claude Code 下一次真实 statusline 渲染写入缓存。若用户已有自定义 statusline，程序不覆盖并返回 `STATUSLINE_CUSTOM`，继续使用公开站点 fallback 或旧缓存。保留的 `ReadViaSetupToken` 仅作为非默认回滚路径，里面仍有 `claude setup-token` OAuth usage 与 Messages header 解析；调度器仅在检测到显式 setup-token 时才可能触达 `api.anthropic.com`，并只记录 host/结果摘要，不记录 token 或响应正文。 |
| 会话文件 | `%USERPROFILE%\.codex\sessions` watcher 只置失效标记，真实读取仍走额度刷新路径。 |
| 余额判定日志 | 跟随真实额度读取完成后写一条 `quota-decision-history.jsonl`，记录余额、消耗尾段、基线、来源类型、上游 used 字段原值/归一化值和原因；可疑 provider 零值被丢弃时也写一条原因日志。不在绘制、动画或测试随机刷新路径写入；日志 15 s/32 KiB 批量落盘，约 48 h 滚动保留。 |
| CodexRadar 网站 | 启动、恢复、模型切换、数据源设置变化和手动刷新仍触发一次错峰请求；常规自动刷新在北京时间每小时整点执行一次，网站未出新批次不再加密轮询；失败、超时或不可用 10 min 重试；读取链路按设置在公开 `current.json`、首页 HTML 回退和 RSS 回退之间分层执行；公开 JSON 成功但缺少首页额度雷达、网页短数据标签、IQ 常态区或 IQ 图表显示上限时，也会读取首页 HTML 补齐展示字段，补齐失败不覆盖已成功的 JSON 数据。IQ 显示上限从同一次 `model_iq` 全模型分数或首页 `IQ指数` 历史值提取，不建立额外轮询。 |
| Model IQ 时钟 | EvenRow 时钟只用模型 IQ 数据窗口或 Claude 模型 `latest_at` 判断新鲜度，不用本地抓取时间冒充数据更新。变黄边界按本机系统时间计算，不按北京时间：Codex 为 12 h 一圈，系统 0:00 和 12:00 边界开始等待当前半日批次；Claude 为 24 h 一圈，系统每天 0:00 开始等待当天模型 IQ。当前时间白点从顶部边界顺时针前进；12 点钟方向固定绘制绿色竖线；小绿点表示上次记录到新 IQ 内容的位置，Codex 保留 12 h、Claude 保留 24 h，当前指针下一圈到达该位置后不再显示。小绿点仍有效时，圆弧从小绿点顺时针连接到当前白点；小绿点过期后不再用旧刷新点绘制连接弧。Codex 抓到同一批旧 IQ 内容时保留旧 `RefreshedUtc`，避免每小时同内容请求移动绿点。时钟中心下方时间由 `RadarClockTimeDisplayMode` 控制，默认 UTC，可切到当前本机时间、上次尝试刷新时间或上次实际 IQ 刷新时间；时间下方同色显示 `UTC`、`NOW`、`LAST` 或 `REF` 短标签，不改变既有日期/时间位置；渲染缓存包含当前分钟签名，避免绿点到期后继续使用旧 bitmap。当前周期数据到达后变绿；只有上一个完整周期仍无数据时才在黄色满环上叠红色。`RadarClockAutoSwitchModelEnabled` 默认开启：过期且当前模型未达上一个周期边界时，Codex 从 7 天模型缓存里找最近达到边界的模型，Claude 从本次 `iq.models` 指标列表里找最近达到边界的模型并写入对应模型 key；没有候选时只变红，不重复写设置。 |
| 模型切换 | 优先加载对应模型缓存，并安排约 1 s 后请求；模型目录由成功 `model_iq` 响应或首页 HTML 模型目录更新；网页短数据标签、IQ 常态区和 IQ 显示上限随同一次请求链路补齐。 |
| CodexRadar 服务检测 | 设置页“检测服务可用性”按钮触发一次性探测公开摘要、授权 API、首页 HTML 和 RSS，结果写入本地诊断文件并追加网络检查历史；不建立额外轮询。旧竖向 Rader/Claude/ChatGPT 健康面板已删除，Rader 状态仍随网站刷新更新，用于当前 `EvenRow` API 摘要和 `R/O/C/D` LED 列。API/LED 非检测中错误经过 10 s 防抖，同一服务错误连续存在满窗口才显示；恢复为正常时立即清除。 |
| Claude 状态 | 用于当前 `EvenRow` 单行 API 摘要；正常 15 min，非正常或失败 2 min 重试，单飞。AI 请求保护命中时不请求 `status.claude.com`，按不可用状态进入异常重试。旧竖向三行面板已删除，不占布局宽度。 |
| 五阶段连接 | 已删除。timer、网络变化、显示恢复和操作面板强制刷新都不会启动网络/DNS/隧道/OpenAI/本地 Codex 五段诊断；当前代码不再包含旧绘制、快照、调度和 ChatGPT/OpenAI 探测回滚路径。 |
| DeepSeek 余额 | 读取 `https://api.deepseek.com/user/balance`；正常 60 s，失败 5 min；网络变化和操作面板强制刷新会立即请求。API key 只从 `DEEPSEEK_API_KEY` 环境变量或 `%LOCALAPPDATA%\DesktopCodexAssistant\deepseek-api-key.txt` 读取，不写入设置或日志。成功余额样本写入 `deepseek-balance-history.jsonl`，保留 48 h，用于估算 24 h 消耗。当前底部 DS 元信息不按余额、24 h 消耗或高低峰改变颜色；文案追加按北京时间 `09:00-12:00`、`14:00-18:00` 判定的 `高峰`，其余为 `低谷`。 |
| 网络事件 | `NetworkChange` 标记服务网络失效并请求 DeepSeek 余额；个人额度只按当前有效且正在运行的软件排队 Codex provider 或 Claude Code 用量刷新，不再同时触碰两套 provider。下一次 Codex Radar UI 调度刷新单行 API 摘要需要的 Rader/Claude/OpenAI 兜底状态；旧五阶段连接流程已删除，不因网络事件启动五段探测。 |
| 挂起/锁屏 | 显示器关闭、会话锁定或系统挂起时停止 Codex 轮询；恢复后网站/服务请求错峰启动，个人额度只经 selected-provider gate 排队当前有效且正在运行的软件 provider，不让两套额度请求同时到期。 |

## 5. 功耗与温度

源码：`Core/PowerThermalForm.cs`

| 项目 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| 功耗 | 1 s | 2 s | 5 s |
| 温度低于 65 C | 2 s | 5 s | 10 s |
| 65 C 至 69.9 C | 1.5 s | 3 s | 5 s |
| 70 C 至 89.9 C | 1 s | 2 s | 3 s |
| 90 C 及以上 | 1 s | 1 s | 1 s |

规则：

- 功耗和温度有独立 deadline，但由同一个后台采样任务合并满足。
- 采样运行中再次到期时只合并为一个待处理请求，不堆积任务。
- 严重温度采样优先于省电策略。
- 显示器关闭、会话锁定或系统挂起时停止采样；恢复后清空缓存并立即采样。

## 6. 网络监控

源码：`Core/NetworkMonitorForm.cs`、`Performance/NetworkMonitorReader.cs`、`Performance/GfwProbeReader.cs`、`Performance/CloudEndpointProbeReader.cs`

| 项目 | 规则 |
| --- | --- |
| 网络窗口 UI tick | 使用普通面板调度 500/1000/3000 ms；只在显示字段变化、尺寸变化或动画需要时重绘。 |
| 本地网卡 | 首次、手动刷新、网卡选择变化、网络事件或 2 s/5 s 到期刷新；省电模式仅事件驱动。 |
| 连通性 | 根据实际状态使用全局连通性表；`AdapterMissing` 不周期请求。在线结果会额外输出本地链路劣化标记，阈值为丢包 `>=15%`、抖动 `>=250 ms` 或延迟 `>=800 ms`。 |
| PING 滚动采样 | 仅 `Online` 时单飞后台采样；性能/均衡/省电间隔 2 s/5 s/10 s。每轮最多探测默认网关和当前活动组，公网组轮转多个 anycast IP，墙内判定后活动组切换为百度。 |
| 公网 IP | 仅 `Online` 时按 5/10/15 min；只请求 `api.ipify.org` 并校验结果必须是 IPv4。 |
| DNS | DNS 地址签名变化立即测；否则按 DNS 最差状态自适应周期；单轮最多 2 个 DNS 并发。UDP/TCP 无响应需同地址连续两轮失败才置灰，本地链路劣化时保持黄色问题。 |
| 网络事件 | 30 s 防抖；只标记本地信息失效、增加 generation、清空公网 IP/连通性/DNS deadline。GFW/云服务不直接跟随事件刷新，需本地快照确认连接恢复、网卡 ID、默认网关或主 IP 地址变化后才强制刷新。DNS 顺序或地址抖动只重测 DNS/连通性，不重置 GFW/云服务周期。PING 滚动窗口随 generation、网卡、主 IP 或默认网关变化清空。 |
| 过期结果 | 公网 IP、DNS、连通性和 PING 滚动任务提交前必须验证 generation 和 `InterfaceId`；PING 还验证主 IP/默认网关签名，不匹配则丢弃并设为可重试。 |
| 手动刷新 | 网络窗口 `ForceRefresh()` 重置本地、公网、连通性、DNS、GFW 和云服务 deadline。 |
| 网络检查历史 | `network-check-history.jsonl` 先写入内存缓冲，15 s、32 KiB 或进程退出时批量追加落盘；启动时修剪旧记录，运行中每 6 h 粗粒度修剪一次。 |

GFW：

- 仅真实网络为 `Online` 时启动；真实 `Online` 但当前活动目标的滚动 PING 丢包率 `>= 2%` 且已确认时不启动新探测，显示黄色不可判定。
- GFW 不直接消费连通性 4 包 Ping 的丢包门控；单包丢失会显示 25%，必须由当前活动目标的 PING 滚动窗口确认丢包后才门控 GFW。网关侧 ICMP 丢包、延迟和抖动诊断只显示在 PING 行，不门控 GFW。
- `Unknown`、离线或 GFW 专用本地链路门控只返回临时显示，不清空内部上次探测时间，避免恢复后被误判为“首次自动检测”。
- 设置范围 15-240 min，默认 30 min。
- 手动刷新由 `GfwProbeManualRefreshToken` 触发。
- 同一时间最多一个探测任务。
- 已在跑的探测如果遇到本地链路门控，完成后丢弃，避免旧结果覆盖不可判定状态。
- 候选站点同一异常层至少 2 个命中才输出明确疑似阻断；少量或分散异常保持不可判定。
- 详细日志在手动、首次、状态变化或每 6 h 写一次。

云服务：

- 与 GFW 结论解耦，但复用 GFW 间隔和手动 token。
- 手动刷新有 45 s 冷却；地区设置变化强制刷新相关官方状态源。
- 正常官方 API 缓存 30 min，普通 HTTPS 正常缓存 15 min，异常/慢响应缓存 2 min，无法连接缓存 45 s，未知缓存 30 s。
- 状态变化需 30 s 滞后确认，避免网络抖动造成频繁日志和 UI 变色。
- 本地链路劣化时，DNS/TCP/TLS/超时等链路敏感的非全数失败会从红色无法连接降为黄色本地丢包影响；官方故障或官方降级不降级。
- 同一时间最多一个云服务探测任务；新强制刷新会取消旧请求。真实状态不是 `Online` 时停止/取消云服务探测并返回无告警 `Unknown` 方块，标题栏云服务告警槽隐藏云服务错误提示。
- 云服务顺序固定为 `Cf Ak Gi Aw Az Go`；Akamai 使用 Statuspage v2 summary，Azure 使用公开 Azure Status RSS。地区设置变化会刷新 Cloudflare、Akamai、Azure、Google 这类带地区过滤的官方状态源。

DNS 告警：

- DNS 探测仍只由 DNS 行现有调度触发；Classic 布局的 DNS 告警仅消费 `DnsServerDetails`，不发起额外探测，并与云服务错误共用标题栏告警槽，不再绘制独立右下角覆盖层。
- 当 DNS 全部正常或尚无可显示错误时，不占用标题栏告警槽。
- DNS 行保留网卡返回的 DNS 优先级顺序，错误状态只改变颜色，不把报错项提前。
- 当 DNS 为 `Problem`、`Hijacked` 或 `Unavailable` 时，按 DNS 行从左到右的原始优先级顺序追加到标题栏告警队列；显示顺序为 `DNS!`、具体原因 `!`，异常提示不显示 `DNS1` / `DNS2` 编号。原因示例包括 `污染`、`仅TCP`、`返回SERVFAIL`、`无地址`、`NX验证失败`、`地址无效` 等，颜色复用 DNS 行状态色。
- 如果多个 DNS 的异常提示文字和状态完全相同，标题栏告警会追加紧凑 DNS 地址后缀，例如 `仅TCP@1.1.1.1!`，使轮换在视觉上可区分；该后缀仅用于屏幕显示，不写入网络检测日志。

## 7. CleanIP 连接检测

源码：`Core/ConnectionCheckForm.cs`、`Performance/CleanIpConnectionReader.cs`

| 项目 | 规则 |
| --- | --- |
| UI tick | 使用普通面板调度 500/1000/3000 ms；只有三框实际显示字段变化、尺寸变化、位置位移或透明动画需要时重绘；旧详情字段如延迟、IP、ASN、地区和触发来源变化不再触发重绘。 |
| 设置间隔 | `ConnectionCheckIntervalSeconds` 范围 15-600 s；当前默认设置值为 600 s，代码 fallback 为 60 s。 |
| 首次和联网 | 首次启动或从断网变为联网时立即检测。 |
| 每小时计划 | 每小时一次，随机偏移正负 5 min 并包含秒，避免整点集中请求。 |
| 错误重试 | 失败状态下在每 10 min 时间槽重试，同一时间槽只试一次。 |
| 手动刷新 | 设置页 token 触发，操作面板强制刷新走 `RequestRefresh()`。 |
| 网络事件 | 只使网络状态缓存失效；真实网络判断和请求在 reader 调度路径执行。 |
| 测试模式 | 测试快照稳定缓存；只有测试模式或手动 token 变化时重建。 |
| 单飞 | `requestRunning` 防止 CleanIP 请求并发。 |

## 8. 操作面板

源码：`Core/OperationForm.cs`、`Core/ForegroundFpsReader.cs`

| 项目 | 规则 |
| --- | --- |
| 动画定时器 | 只在按压或悬停进度未到目标时运行；间隔使用全局悬停动画 16/33/100 ms。 |
| 全屏/挂起 | 全屏隐藏和显示挂起时停止动画与 FPS 定时器。 |
| 反向隐藏轮询 | 操作面板复用主窗口共享交互 tick 检查手动隐藏下的临时恢复，不新增独立常驻定时器。 |
| 刷新按钮 | 刷新 MyASUS、系统按钮可用性，并调用主窗口 `ForceRefreshAllModules()`。 |
| 左侧主区域模式 | `OperationPrimaryPanelMode` 支持自动、Windows 按钮、内存饼图和隐藏；隐藏模式不保留大按钮区域宽度，小按钮从左侧 margin 开始排布。 |
| SeelenUI 电源菜单 | 后台单飞执行 `slu.exe` 并最多等待 1500 ms；UI 线程不等待外部进程。 |
| SeelenUI 状态 | 操作面板可见时复用共享维护 tick，最多每 2 s 检查一次 `seelen-ui` 进程；自动模式下变化后只重绘左侧按钮/内存区域并刷新隐藏模式命中遮罩。 |
| 内存饼图 | 仅在左侧主区域解析为内存饼图时采样；最多每 2 s 读取一次 `GlobalMemoryStatusEx` 和前台进程 Working Set，绘制时只使用缓存快照。 |
| 单击/双击重启按钮 | 单击行为用 `SystemInformation.DoubleClickTime` 延迟确认，避免和双击重启冲突。 |
| 设置按钮 | 操作面板是 `WS_EX_NOACTIVATE` 窗口，单击程序设置按钮会等待一个系统双击间隔再打开设置；双击不打开设置，而是打开 AI 快速选单。打开已有设置窗或快速选单时必须走宿主 `ShowSettingsWindow()`，先清理瞬态交互状态，再用系统前台激活 API 拉回窗口。 |
| AI 快速选单 | 双击程序设置按钮打开 `AiQuickMenuForm`，菜单包含 AI 阻断开关和 Codex 额度计划。AI 阻断开关保存 `AiRequestProtectionManualBlockEnabled`；开启时阻断本程序 OpenAI/ChatGPT/Claude/Anthropic 请求，并尝试停止正在运行的 Codex/Claude Code 进程。不要在自动测试中触发手动阻断开启路径。 |
| Codex 额度计划 | `CodexQuotaGoalPlanner` 只复用 `%LOCALAPPDATA%\DesktopCodexAssistant\quota.ini` 的剩余额度缓存，默认条件为周额度小于 3% 且 5 小时额度小于 90%，计划默认关闭；触发时通过 `codex app-server` 对已勾选 goal 写入 `usageLimited`。恢复时按 `CodexQuotaPlanResumeConditionMode` 选择只看周额度、只看 5 小时额度或两者都恢复，再写回 `active`。app-server 调用运行在后台任务，失败只记录日志和 UI 状态，不阻塞主 tick。 |
| FPS 回退 | 仅当 FPS 面板应显示时运行；性能/均衡/省电间隔 1/2/5 s；值未变化时不重绘。 |
| FPS counter 发现 | 首次或候选缺失时发现；前台进程变化后的重新发现冷却 30 s；完整发现间隔 60 s。 |
| 防烧屏维护 | 位置位移检查复用主窗口共享维护 tick，不建立额外高频定时器。 |
| 设置窗口恢复 | 设置窗口关闭、异常销毁或被宿主清理后，`WidgetForm` 调用 `OperationForm.ClearTransientInteractionState()` 清除 hover、pressed、tooltip 和鼠标捕获状态，避免操作面板停在半交互状态。 |

## 8.1 鼠标隐藏与延迟显现

- 普通“悬停透明”由 `HoverInteractionPolicy` 统一判断鼠标敏感范围；延迟显现可在设置中关闭。
- 延迟显现开启时，鼠标离开判定区后继续隐藏 `HoverOpacityRevealDelaySeconds`，倒计时内重新进入判定区需持续 `HoverOpacityRevealResetSeconds` 才重置倒计时。
- 延迟显现关闭时，普通悬停隐藏在鼠标离开判定区后立即恢复，不保留倒计时状态。
- 左下角操作面板手动隐藏切换会刷新 `ManualHoverOpacityActive`，即使自动隐藏已经让组合隐藏态处于激活状态，也会把手动来源同步给各浮窗。
- 操作面板刚开启手动隐藏后，在鼠标离开操作面板判定范围前暂不允许反向隐藏恢复操作面板本身；离开后再移回面板，反向隐藏恢复重新生效。

## 9. 设置窗口

源码：`Settings/Win11SettingsForm.cs`

| 项目 | 规则 |
| --- | --- |
| 默认界面 | 设置入口只创建基于 WinUI/Fluent 设置结构重写的 `Win11SettingsForm`，第一页是 `控制中心` 模块仪表盘，模块卡片只导航到现有详细设置页；旧 `SettingsForm` 和 `DESKTOP_CODEX_LEGACY_SETTINGS` 回退已移除。 |
| 视觉资源 | 当前设置界面、控制中心模块卡片和 AI 快速选单共用 `DesignTokens` 的颜色、圆角、按钮尺寸和间距；设置窗口本体使用 `DesignTokens.SettingsWarmTheme` 的 WarmCard 暖灰/琥珀色板，不再使用 NeonGeek 蓝紫色板；跨窗口设置风格控件放在 `Settings/SettingsFluentResources.cs`，WinForms 控件字体必须通过窗口级 `UiFontCache.GetUiPoint` 复用，不能把 Point 字号接到悬浮窗 Pixel 字体路径；现行源码的导航图标使用 Segoe Fluent Icons / MDL2 字体字形。若恢复此前的 Phosphor PNG 设计要求，需要同时恢复资源加载器和 `--test-settings-bindings` 图标存在性断言。 |
| 布局边界 | 设置窗口首选尺寸按 2880x1740 参考工作区比例动态缩放，并受当前工作区上限保护；低分辨率下最小尺寸必须收敛到屏幕内，左侧导航从 409 px 收缩到 300/220 px，内容依靠滚动承载。设置页卡片、页脚按钮和输入控件必须随内容区宽度重排；中文标题和说明高度按字体实际测量，不使用固定 24/34 px 行高；导航行、控制中心面板和设置行需要保留明显垂直间距，避免图标、标题、说明和按钮互相压叠；设置页不得暴露“记录窗口排版日志”这类内部诊断按钮；`--test-settings-bindings` 覆盖动态分辨率尺寸、最小窗口下的控件越界检查。 |
| 页面结构 | 设置窗口包含独立「布局与位置」页；所有窗口宽高、左边距、底边距和操作面板偏移都收在该页「复杂选项」下，各业务页只保留日常开关、透明度和外观。分组名前缀 `!` 表示默认折叠到每页「复杂选项」区；搜索时高级项直接参与匹配，当前页无匹配行时跳到第一个匹配页。 |
| 外观选择 | `MainWidgetRenderVariant`、`CodexRadarRenderVariant`、`NetworkMonitorRenderVariant`、`PowerThermalRenderVariant`、`ConnectionCheckRenderVariant` 和 `OperationRenderVariant` 使用 `VariantPicker` 缩略图卡片选择器；`ClaudeRadarRenderVariant` 仍是复杂选项中的下拉预留项。缩略图懒加载到 `%LOCALAPPDATA%\DesktopCodexAssistant\variant-samples\v<ProductIdentity.Version>\`，仅真实宿主设置窗触发，`--test-settings-bindings` 不渲染样例。 |
| 实时预览 | 设置变更后 75 ms debounce 应用预览，避免每个控件事件立即写入运行窗口。 |
| 任务切换 | 设置窗口必须 `ShowInTaskbar=true`，作为普通应用窗口出现在任务栏和 Alt+Tab；用户切到浏览器复制内容后应能直接通过 Alt+Tab 切回设置窗。 |
| AI 阻断设置 | 系统页“AI 阻断”分组包含 `AiRequestProtectionAutoEnabled` 和 `AiRequestProtectionManualBlockEnabled`。自动模式默认开启，只复用网络监控已有 GFW 明确阻断结论，不新增网络探测、代理或系统防火墙规则。 |
| 显示器选择 | 设置页显示器下拉从 `Screen.AllScreens` 枚举当前 DISPLAY；保存值为 `Screen.DeviceName`，空值表示主显示器/自动。指定显示器断开时默认回退到主显示器，关闭回退后保留模块上次工作区基准，不挪回已有显示器。 |
| 全局编辑 | 设置页“全局编辑”打开 50% 黑色全屏布局编辑遮罩；进入编辑模式后临时禁用所有隐藏/悬停透明规则，并把模块窗口保持在遮罩之上。拖拽只改位置和模块目标显示器，鼠标移动期间实时调用 `PreviewSettings` 并刷新窗口层级，Enter 调用 `SaveSettings` 写回，Esc 还原进入编辑前的预览设置。 |
| 页脚状态 | 保存成功或失败提示显示 5 s 后自动隐藏；存在未保存改动时底部状态栏常驻显示“有未保存的更改”，关闭窗口时提供保存并关闭、放弃更改、取消三选确认。 |
| 手动刷新 token | GFW、CleanIP 和 Codex/Claude Radar 随机测试通过 `立即刷新` 按钮递增 token 并传回 `WidgetSettings`，由对应窗口/reader 识别变化。 |
| 空闲 CPU 诊断 | 设置页“立即检查”只在点击时运行一次，采样约 1.5 s 当前 CPU/进程，并扫描最近 30 min 事件日志；结果写入本地诊断报告，不建立常驻定时器。 |
| 保存 | 点击保存写入 `settings.ini`，主窗口 watcher 和修改时间检查负责外部热加载。 |
| 取消 | 恢复打开设置前的 baseline，不应触发额外持久化写入。 |
| 异常关闭 | `Win11SettingsForm.OnFormClosing` 会回滚未保存预览；若窗口异常销毁或只触发 `Disposed/FormClosed`，宿主必须通过 `ISettingsWindow.TryConsumeUnsavedPreview()` 只消费一次 baseline 并回滚，不能只依赖 `OnFormClosing`。 |
| Codex 模型选择 | `CodexRadarModelKey` 使用 `%LOCALAPPDATA%\DesktopCodexAssistant\codex-radar-models.ini` 生成下拉框；暂不可用模型保留在下拉中并标注，不再手动填写 key。 |
| DeepSeek 配置 | 设置页 Codex Radar 的“模型与时区”组只写 `%LOCALAPPDATA%\DesktopCodexAssistant\deepseek-api-key.txt`，不把 key 写入 `settings.ini`；保存或清除后递增 `DeepSeekApiKeyRevision` 触发 Codex Radar 立即刷新余额状态。 |

## 10. 修改检查清单

修改刷新规则后至少检查：

1. `Docs/Component-Refresh-Rules.md` 是否需要同步。
2. `Docs/Interfaces/INTERFACE_INDEX.jsonl` 中对应接口、配置、命令或资源是否需要更新。
3. `Docs/Performance-And-Window-Runtime.md`、`Docs/CodexRadar-Architecture.md` 或 `Docs/NetworkMonitor-Architecture.md` 是否有重复表格需要同步。
4. 是否仍满足“同类网络请求单飞、过期结果不可覆盖新状态、隐藏/挂起时停止不必要绘制”的约束。
5. 是否需要运行 `--test-settings-bindings`、`--test-settings-open-close`、`--test-layout`、`--test-radar-display-lifecycle`、`--test-display-recovery`、`--test-operation-panel` 或网络/窗口截图验证。
