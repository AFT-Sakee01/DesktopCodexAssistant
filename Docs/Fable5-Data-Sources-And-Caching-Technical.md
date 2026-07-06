# Fable5-Data-Sources-And-Caching-Technical — 数据源、Fallback 链与缓存位置详解

适用版本：`1.0.4.18`。生成时间：2026-07-06。
本文回答三个问题：**每个模块从哪个网站/本地文件的哪个位置读什么、失败时按什么顺序 fallback、结果缓存在哪个文件里**。所有 URL、路径、常量均直接摘自源码并附出处；刷新频率与调度规则的权威文档是 `Docs/Component-Refresh-Rules.md`，本文只在必要处引用不重复。

约定：`<DATA>` = `%LOCALAPPDATA%\DesktopCodexAssistant`（`Logger.DirectoryPath`）；`<HOME>` = `%USERPROFILE%`。

---

## 1. Codex Radar 网站数据（模型 IQ / 通过率 / 效率 / 批次）

源码：`Core/CodexRadarForm.cs`（常量在文件头 33–66 行附近）。

### 1.1 数据源与读取位置

| 优先级 | URL | 读取内容 | 门控设置 |
|---|---|---|---|
| 1 | `https://codexradar.com/current.json` | 当日批次模型数据：`passed`、通过率、`total_tokens`/`n_input_tokens`+`n_output_tokens`、`serial_task_seconds`（用于 token/时间效率）、状态色（green/yellow/orange/red 别名归一化）、数据日期与上下午窗口 | `CodexRadarPublicJsonEnabled` |
| 1b | `https://codexradar.com/api/v1/current` | 授权完整 API（服务可用性探测按钮也会测它） | 同上 |
| 2 | `https://codexradar.com/`（首页 HTML） | 两种用途：(a) **补齐**——JSON 成功但缺首页额度雷达、网页短数据标签或 IQ 常态区时抓取补齐，补齐失败不覆盖 JSON 结果；(b) **回退**——JSON 失败时作为数据回退 | `CodexRadarHtmlFallbackEnabled` |
| 3 | `https://codexradar.com/feed.xml`（RSS） | 最后一层回退（文件内 9576 行附近） | `CodexRadarRssFallbackEnabled` |
| 附 | `https://codexradar.com/api/model-ratings?history=14` | 模型社区评分 14 天历史 | — |

- 效率计算：`模型效率 = (当前 passed/total_tokens) ÷ (基线 passed/total_tokens)`，时间效率同理用 `serial_task_seconds`；基线存在 `settings.ini` 的 `CodexModelIq*BaselinePassed` 系列键。
- 刷新节奏：启动/恢复/模型切换/数据源设置变化/手动刷新触发一次错峰请求；常规自动刷新在**北京时间每小时整点**一次（`TimeZoneUtilities.GetNextBeijingHourUtc`）；失败 10 min 重试。

### 1.2 缓存

| 文件 | 内容 | TTL |
|---|---|---|
| `<DATA>\codex-radar-cache.ini` | 按 `软件模式+模型 key` 前缀分组的键值：`SavedUtc / RefreshedUtc / DataDate / DataWindowHour / DataLabel / PassRate / Passed / ValidTasks / Status / TimeEfficiency / TokenEfficiency / EfficiencyPassed / EfficiencySeconds / EfficiencyTokens / NormalLow / NormalHigh / History`。启动时 `LoadCodexRadarCache` 先回显缓存再联网 | `CodexModelCacheRetentionDays = 7` 天；Codex 模式还兼容读 legacy 前缀 |
| `<DATA>\codex-radar-models.ini` | 模型目录（`CodexRadarModelCatalog`），驱动设置页模型下拉 | 持久 |
| `<DATA>\quota-reset-state.ini` | 额度 reset 到期后的保护状态（`LoadQuotaResetState`，约 13008 行） | 持久 |
| `<DATA>\codex-radar-service-probe.txt` | 设置页"检测服务可用性"按钮的一次性诊断输出 | 覆盖写 |

随机测试模式（`CodexRadarRandomTestEnabled`）**不写任何缓存**，且暂停真实轮询。

---

## 2. Codex 个人额度（5h / 周额度环）

源码：`Core/CodexRadarForm.CodexUsage.cs`（provider 路径）、`Core/CodexRadarForm.cs` 13200–13600 行附近（本地 session 路径）。

### 2.1 主路径：ChatGPT provider API

- URL：`https://chatgpt.com/backend-api/wham/usage`，超时 10 s。
- 凭据（只读，不写回、不刷新）：
  1. 环境变量 `CODEX_ACCESS_TOKEN`（Process→User→Machine 三级查找）；
  2. `%CODEX_HOME%\auth.json`（若设置了 CODEX_HOME）；
  3. `<HOME>\.codex\auth.json` —— 用 JavaScriptSerializer 递归找第一个 `access_token`/`accessToken` 字段。
- 节奏：正常 300 s / 失败 600 s / HTTP 429 冷却 900 s，单飞；快照新鲜窗口 900 s。
- 门控：仅当检测软件为 CODEX 且本地 Codex 进程在运行（`SoftwareRuntimePresence`）；AI 请求保护（手动阻断或 GFW 明确阻断）命中时本轮不读 token、不发请求，按 `AI_BLOCK` 失败间隔处理。

### 2.2 Fallback：本地 Codex CLI session 文件

- 目录：`<HOME>\.codex\sessions`（`quotaSessionsPath`，13415 行），`FileSystemWatcher` 监听 `rollout-*.jsonl` 只置失效标记。
- 读取方式：按 LastWriteTime 排序，最多扫 `MaxQuotaRolloutFilesToScan = 80` 个文件；每个文件**从尾部按 `QuotaTailChunkBytes = 1 MB` 分块反向读取**，找含 `"rate_limits"` 的最新事件行；解析 `rate_limits.primary`（5 小时窗口）与 `rate_limits.secondary`（周窗口）两个 slot 的用量百分比和 reset 时间。
- 结果带内存缓存：文件路径+写入时间+长度不变则直接复用（`codexQuotaSnapshotCache*` 字段，30 s 内不重验最新文件）。
- 触发条件：Codex 正在运行且 provider 无新鲜快照时，10/15/30 s（性能/均衡/省电）刷新；Codex 不运行时不扫描。

### 2.3 缓存与历史

| 文件 | 写入者 | 用途 |
|---|---|---|
| `<DATA>\quota.ini` | CodexRadarForm（13846 行：Claude 模式写 `claude-quota.ini`，否则写 `quota.ini`） | 最近一次成功的 5h/周额度快照；`CodexQuotaGoalPlanner`（额度计划）只复用此缓存做暂停/恢复判断 |
| `<DATA>\quota-decision-history.jsonl` | `QuotaDecisionHistoryLogger` | 每次真实额度读取后的判定记录；15 s/32 KiB 批量落盘，约 48 h 滚动 |
| `<DATA>\codex-quota-plan-state.json` | `CodexQuotaGoalPlanner` | 额度计划 goal 暂停/恢复状态（通过 `codex app-server` 写 `usageLimited`/`active`） |

---

## 3. Claude Radar 网站数据（独立 Claude 窗口）

源码：`Core/ClaudeRadarReader.cs`（URL 常量 13–16 行）、`Core/ClaudeRadarForm.cs`。

### 3.1 数据源与 fallback 链

| 优先级 | URL | 读取内容 | 门控 |
|---|---|---|---|
| 1 | `https://claudecoderadar.com/data/claude-code-radar.json` | 主数据：`iq.models` 数组（模型 key、显示名、IQ、效率、状态）、站点公开 quota usage | `ClaudeRadarJsonEnabled` |
| 2 | `https://claudecoderadar.com/`（首页 HTML） | **弱 metadata fallback**：仅当 JSON 失败、`iq.models` 缺失、模型名缺失或名=key 时，解析首页 `MODEL_NAMES` 补 key/显示名。**只补名字，不伪造 IQ/效率/额度/服务健康**；homepage-only 目录不推进缺失计数、不触发模型删除 | `ClaudeRadarHomepageFallbackEnabled`（关闭时严禁探测首页） |
| 附 | `https://claudecoderadar.com/api/model-ratings?history=14` | 社区评分（底部 `RC` 取 average 最高一条，平分取 count 大者） | `ClaudeRadarCommunityRatingsEnabled` |
| 附 | `https://status.claude.com/api/v2/summary.json` | Claude 官方 Statuspage（右侧 `C` 方块），只在本窗口真实刷新路径顺序读取 | — |

超时统一 10 s。右侧 `R/C/U` 方块 = Radar 数据源 / Claude 官方状态 / Claude Code usage。

### 3.2 缓存

| 文件 | 内容 |
|---|---|
| `<DATA>\claude-radar-cache.ini` | 网站快照 + `SelectedModelKey/Name` + `CommunityKnown/RatingKey/Label/Average`（启动/失败合并时回显底部 RC/LLM） |
| `<DATA>\claude-radar-model-map.ini` | 模型目录映射（source_key ↔ rating_key ↔ display_name/sort_order/enabled）；只有 `ok=true` 且目录完整的响应才推进 temporarily_missing/deleted 计数（连续缺失阈值 `ModelDeleteMissingThreshold = 3`）。**paint 路径禁止读此文件** |
| `<DATA>\claude-radar-notification-state.ini` | 模型新增/暂缺/恢复/删除托盘通知去重状态 |
| `<DATA>\claude-radar-quota-history.jsonl` | 额度历史，按 metric/update/run 全量去重，单行坏 JSON 跳过不阻断 |

---

## 4. Claude Code 个人额度（5h / 7d 环）

源码：`Core/ClaudeCodeUsageReader.cs`。这是最容易误解的链路——**默认路径不发任何 Claude API 请求**。

### 4.1 默认链（零 API 消耗）

1. **statusline 桥缓存**：读 `<DATA>\claude-statusline-quota.ini`；有效期 `StatusLineCacheMaxAgeMinutes = 360`（6 小时）。有效则写入 `<DATA>\claude-quota.ini` 并刷新额度环，结束。
2. **桥安装**：缓存缺失时，自动安装 `<HOME>\.claude\desktop-codex-statusline-bridge.ps1`（文件内嵌 PowerShell 脚本，标记 `# Desktop Codex Assistant Claude statusline bridge v2`），并把它注册进 `<HOME>\.claude\settings.json` 的 `statusLine` 命令。之后等 Claude Code CLI 下一次真实渲染 statusline 时，由桥脚本把额度写入上述 ini（`Source=claude_statusline`）。
   - 用户已有自定义 statusline → **不覆盖**，返回 `STATUSLINE_CUSTOM`，继续用站点公开 quota 或旧缓存。
   - Claude 桌面 App 不执行 statusLine（终端特性），纯桌面机器缓存会永远为空 → 落到第 3 步。
3. **setup-token 路径**（仅当已配置 token 才走）：token 来源 = 环境变量 `CLAUDE_CODE_OAUTH_TOKEN` 或 `<DATA>\claude-code-oauth-token.txt`（`NormalizeSetupToken` 能剥掉 `export X=`、`$env:X=` 等包装）。
   - 先 GET `https://api.anthropic.com/api/oauth/usage`（免费，header `anthropic-beta: oauth-2025-04-20`）；
   - 失败再 fallback POST `https://api.anthropic.com/v1/messages` 读响应头限额（**会消耗极少量配额**）。
   - 注意：按 `Component-Refresh-Rules.md` §4，当前调度**默认不调用** `ReadViaSetupToken`，它是保留的非默认回滚路径；实际代码在 statusline 缓存无效且 token 已配置时仍会走它（`Read()` 104–107 行）。
4. 都不可用 → `NO_STATUSLINE_CACHE` / `NO_SETUP_TOKEN` 错误态；显示层可用带 `QuotaSource=site` 的站点公开数据兜底（个人缓存必须同时含 5h 和 7d 才有效）。

### 4.2 双消费者

- CodexRadarForm（Claude 软件模式，`CodexRadarForm.ClaudeUsage.cs`）：300/600/900 s 三档。
- ClaudeRadarForm（固定 Claude 窗口）：5/10/15 min 三档，窗口级单飞；要求 Claude 本地进程运行、窗口可见、非随机测试。
- 成功结果统一写 `<DATA>\claude-quota.ini`（原子写：临时文件 + `File.Replace`，内容相同不重写）。

---

## 5. 其余 AI 服务状态源（CodexRadar 单行 API 摘要消费）

源码：`Core/CodexRadarForm.cs` 头部常量。

| 服务 | URL | 节奏 |
|---|---|---|
| Claude 官方状态 | `https://status.claude.com/api/v2/status.json`（注意与 Claude Radar 窗口用的 `summary.json` 不同端点） | 正常 15 min，异常/失败 2 min，单飞；AI 请求保护命中时不请求 |
| OpenAI 官方状态 | `https://status.openai.com/api/v2/summary.json` | 随网站刷新链路 |
| ChatGPT 可达性 | `https://chatgpt.com/`（HEAD/GET 探测） | 同上 |
| DeepSeek 余额 | `https://api.deepseek.com/user/balance`，Bearer key 来源=env `DEEPSEEK_API_KEY` 或 `<DATA>\deepseek-api-key.txt`（设置页只写这个文件，**不进 settings.ini**，`DeepSeekApiKeyRevision` 递增触发即时刷新） | 正常 60 s / 失败 300 s；成功样本写 `<DATA>\deepseek-balance-history.jsonl` 保留 48 h，用于估算 24 h 消耗；高峰/低谷按北京时间 09:00–12:00、14:00–18:00 判定 |

旧五段连接诊断（网络/DNS/隧道/OpenAI/本地 Codex）已由 `CodexConnectionFlowEnabled = false` 整体停用，不产生任何请求。

---

## 6. 网络监控窗口

源码：`Performance/NetworkMonitorReader.cs`。

| 检测 | 目标 | 说明 |
|---|---|---|
| 连通性 | ICMP `1.1.1.1`；captive portal 验证 `http://www.msftconnecttest.com/connecttest.txt`（期望正文 `Microsoft Connect Test`） | 状态自适应间隔见刷新规则 §2 |
| 公网 IPv4 | `https://api.ipify.org` | 仅 Online；5/10/15 min；结果必须是合法 IPv4 |
| 滚动 PING | 默认网关 + 活动组。公网组轮转 6 个 anycast：`1.1.1.1, 1.0.0.1, 8.8.8.8, 8.8.4.4, 9.9.9.9, 149.112.112.112`；GFW 判定墙内后活动组切换为 `www.baidu.com` | 2/5/10 s 单飞；墙内告警阈值 latency≥150 ms / jitter≥80 ms（公网组阈值更宽） |
| DNS | 对系统 DNS 逐个查询已知域 `www.msftconnecttest.com`（UDP+TCP，含污染/SERVFAIL/NX 验证判定） | 单轮最多 2 个并发；同地址连续两轮失败才置灰 |
| 历史 | `<DATA>\network-check-history.jsonl` | 15 s/32 KiB/退出时批量追加；启动修剪 + 每 6 h 粗修剪 |

## 7. GFW 探测

源码：`Performance/GfwProbeReader.cs`。

- **对照组**（必须至少 1 个可达才有判定资格）：`www.microsoft.com`、`www.bing.com`。
- **候选组**（被墙嫌疑目标）：`www.google.com`、`www.youtube.com`、`x.com`。
- 每域四层探测：系统 DNS → DoH 对照（`https://cloudflare-dns.com/dns-query?name=<域>`）→ TCP 连接 → TLS/SNI 握手 → HTTP。按各层异常计数归类结论：`系统DNS失败但DoH可解析`（DNS 污染）、`TCP可连但TLS/SNI握手失败`（SNI 阻断）等；**同一异常层至少 2 个候选命中才输出明确疑似阻断**，否则不可判定。
- 间隔设置 15–240 min（默认 30）；仅真实 Online 且滚动 PING 丢包 <2% 时启动；单飞。
- 详细日志：`<DATA>\gfw-probe.log`（手动/首次/状态变化/每 6 h 写一次）。

## 8. 云服务端点探测（6 方块 Cf Ak Gi Aw Az Go）

源码：`Performance/CloudEndpointProbe.cs`（Targets 数组 27–34 行附近）。

| 键 | 类型 | 数据源 |
|---|---|---|
| cloudflare | Statuspage v2 | `https://www.cloudflarestatus.com/api/v2/summary.json` |
| akamai | Statuspage v2 | `https://www.akamaistatus.com/api/v2/summary.json` |
| github | Statuspage v2 | `https://www.githubstatus.com/api/v2/summary.json` |
| aws | 普通 HTTPS 探测 | `aws.amazon.com`（拼 `https://` 前缀，248 行） |
| azure | RSS | `https://azure.status.microsoft/en-us/status/feed/` |
| google | 事件 JSON | `https://status.cloud.google.com/incidents.json` |

- 采样：每目标 3 次、间隔 10 s、超时 3.8 s、慢阈值 1 s、最多 3 并发。
- 内存缓存 TTL（`EndpointCache`/`TextCache`）：官方 API 正常 30 min / 普通 HTTPS 正常 15 min / 异常或慢 2 min / 无法连接 45 s / 未知 30 s；状态变化 30 s 滞后确认。
- 与 GFW 结论解耦但复用其间隔与手动 token；地区过滤由 `CloudStatusRegionMask` 控制（影响 Cloudflare/Akamai/Azure/Google 的地区维度）。

## 9. CleanIP 连接检测（三徽章窗口）

源码：`Performance/CleanIpConnectionReader.cs`。

- 唯一数据源：`https://cleanip.io/api/v2/me`（超时 9 s），返回出口 IP 的类型判定（原生住宅/广播企业/未广播 IDC/代理风险等，对应 `CleanIpBadgeTestMode` 的枚举）。
- 节奏：设置间隔 15–600 s（默认设置值 600 s）；每小时计划一次（±5 min 随机抖动）；失败在每 10 min 槽重试一次；单飞；`NetworkChange` 只使状态缓存失效。

## 10. 本地系统数据源（无网络）

| 数据 | 来源 |
|---|---|
| CPU/内存/磁盘/GPU/NPU/网速 | PDH 性能计数器（`Performance/PdhSampler` + `PdhNative`），GPU/NPU 引擎枚举独立节流 1/2/5 s |
| 内存饼图 | `GlobalMemoryStatusEx` + 前台进程 Working Set（≤每 2 s） |
| 前台 FPS | FPS 性能计数器发现 + 读取（1/2/5 s，发现冷却 30 s/60 s） |
| 功耗/温度 | 设备家族专用采样（UX3407N/UX3607O 校准，`Core/PowerThermalForm.cs`） |
| Codex/Claude 进程存在 | `SoftwareRuntimePresence` 按可执行名查进程（3/5/10 s） |
| SeelenUI | `seelen-ui` 进程检查（≤每 2 s）；电源菜单经 `slu.exe` 后台单飞（≤1500 ms 等待） |
| Wi-Fi RSSI | WinRT/Native（`Interop/NativeMethods`） |

---

## 11. `<DATA>` 目录文件总表

| 文件 | 写入者 | 内容 | 生命周期 |
|---|---|---|---|
| `settings.ini` | WidgetSettings.Save | 全部设置（Key=Value） | 持久；FileSystemWatcher 热加载 |
| `widget.log` | Logger | INFO（64 KiB/5 min 批量）+ERROR | 3 MB 轮转，目录 .log 总量 10 MB 上限 |
| `error.log` | Logger | ERROR 副本（即时） | 同上 |
| `gfw-probe.log` | Logger.GfwProbe/CloudEndpointProbe | GFW/云探测详情 | 同上 |
| `codex-radar-cache.ini` | CodexRadarForm | 网站数据快照（分模式+模型前缀） | 7 天 TTL |
| `codex-radar-models.ini` | CodexRadarModelCatalog | Codex 模型目录 | 持久 |
| `quota.ini` | CodexRadarForm | Codex 5h/周额度快照 | 持久；QuotaGoalPlanner 复用 |
| `claude-quota.ini` | ClaudeRadarReader.TryWriteClaudeCodeQuotaCache | Claude 5h/7d 额度快照 | 持久；原子写 |
| `claude-statusline-quota.ini` | `~/.claude` 桥脚本（外部进程） | statusline 额度缓存 | 360 min 有效期 |
| `claude-code-oauth-token.txt` | 用户手工放置 | setup-token 明文 | （规划中改 DPAPI，见优化 spec T8） |
| `deepseek-api-key.txt` | 设置页 | DeepSeek key 明文 | 同上 |
| `claude-radar-cache.ini` / `claude-radar-model-map.ini` / `claude-radar-notification-state.ini` / `claude-radar-quota-history.jsonl` | ClaudeRadarReader/Form | 见 §3.2 | 持久 |
| `quota-reset-state.ini` | CodexRadarForm | reset 到期保护状态 | 持久 |
| `codex-quota-plan-state.json` | CodexQuotaGoalPlanner | 额度计划状态 | 持久 |
| `quota-decision-history.jsonl` | QuotaDecisionHistoryLogger | 额度判定历史 | ~48 h 滚动 |
| `network-check-history.jsonl` | NetworkCheckHistoryLogger | 网络检查历史 | 启动+每 6 h 修剪 |
| `deepseek-balance-history.jsonl` | CodexRadarForm | 余额样本 | 48 h |
| `ui-hang-watchdog.jsonl` | UiHangWatchdog | UI 无响应记录（>10 s 挂起） | 持久 |
| `codex-radar-service-probe.txt` | CodexRadarForm | 服务可用性一次性诊断 | 覆盖写 |
| `idle-cpu-diagnosis-*.txt` / `radar-runtime-diagnosis-*` | 诊断命令 | 诊断报告（含 `-latest` 别名） | 手动 |
| `variant-samples\v<版本>\` | Win11SettingsForm | 设置页外观缩略图懒加载缓存 | 按版本目录 |

## 12. 读取的第三方本地文件（本程序不拥有、只读或谨慎写）

| 路径 | 权限 | 用途 |
|---|---|---|
| `<HOME>\.codex\auth.json`（或 `%CODEX_HOME%\auth.json`） | 只读 | Codex access_token（递归找 `access_token`/`accessToken`） |
| `<HOME>\.codex\sessions\**\rollout-*.jsonl` | 只读（watcher 监听） | 本地额度事件（`rate_limits.primary/secondary`），尾部 1 MB 反向扫描，≤80 文件 |
| `<HOME>\.claude\settings.json` | **读写** | 注册 statusLine 桥；已有自定义 statusline 时不覆盖 |
| `<HOME>\.claude\desktop-codex-statusline-bridge.ps1` | **写入** | 本程序安装的桥脚本（v2 标记幂等） |

## 13. 横切规则速查

- **单飞**：所有网络 reader 用 `requestRunning` 防并发；过期结果（generation/网卡/IP 签名不匹配）提交前丢弃。
- **AI 请求保护**（`Core/AiRequestProtection.cs`）：手动阻断开关或自动模式（复用 GFW 明确阻断结论）命中时，阻断本程序对 OpenAI/ChatGPT/Claude/Anthropic 的请求；Codex provider 额度路径连 token 都不读。
- **挂起/锁屏/息屏**：停止各网络轮询；恢复后错峰重启，个人额度只经 selected-provider gate 排队当前运行中的软件。
- **随机测试模式**：Codex/Claude Radar 随机测试快照不写任何缓存/模型表/额度/历史（有 reader 存储隔离自测覆盖）。
- **写缓存原子性**：claude-quota.ini 等采用 temp 文件 + `File.Replace`；内容未变不落盘。
