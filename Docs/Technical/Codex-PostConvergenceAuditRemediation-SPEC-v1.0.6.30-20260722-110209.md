# 新版界面收敛后多角度审计修复实施规格

- 文档类型：Implementation SPEC（执行规格快照）
- 基线版本：`1.0.6.30`
- 创建时间：`2026-07-22T11:02:09+09:00`
- 生成模型：Codex
- 当前状态：`draft`，尚未授权执行
- 适用平台：Windows on Arm / ARM64；禁止在本规格执行中构建、验证或发布 x64
- 审查对象：当前工作树中的新版十磁贴、五 Dock、Operation、Settings，以及永久 headless 的 Radar/Power 数据 owner
- 目标版本：从执行时的下一个可用版本开始；建议五个批次各占一个独立版本，不得覆盖并发任务已经占用的版本号

> 本文件是尚未执行的不可变规格。开始实现后不得直接修改正文；若范围、风险边界或验收条件需要变化，必须创建带新时间戳的替代 SPEC，并在 Technical INDEX 与 Spec Board 中标明取代关系。

## 0. 执行红线

1. 先读取执行时最新的根 `AGENTS.md`、`Docs/AGENTS.md`、README、构建脚本，以及 FEATURE/INTERFACE 索引命中行；不得用本规格覆盖更新后的项目规则。
2. 当前工作树包含大量用户改动和未跟踪的新拓扑文件。执行前必须生成只读状态清单和完整源码备份；不得 reset、checkout、覆盖或删除无法确认归属的改动。
3. `Opus48-ChinaEgressAiGuard-SPEC-v1.0.5.69-20260722-033409.md` 当前仍是独立 pending 规格。`Core/ChinaEgressWarningForm.cs` 不属于本规格的功能实现范围：不得在本规格中接线或删除；源清单是否纳入它取决于该独立规格在执行时是否已经批准并实施。
4. 测试不得读取真实 `auth.json`、Claude setup-token、Cookie、环境中的真实访问令牌或真实认证响应；所有认证、迁移、SSRF、超限和响应体测试必须使用临时目录、注入传输或内存 fixture。
5. 不得为验收调用 ChatGPT、Anthropic、Codex Radar full API、DeepSeek 等认证或可能计费接口。公开无认证 endpoint 的 live smoke 只能作为补充，不能替代确定性 fixture。
6. 不得恢复 Dock、Launchpad、顶部栏、Direct2D 项目或已经退役的独立 `ClaudeRadarForm` / `ConnectionCheckForm`。
7. 不得让 `CodexRadarForm` 或 `PowerThermalForm` 再次可见；修复应保持“headless owner 生产快照，可见 tile/board 只消费快照”的现行架构。
8. 不得通过吞异常、无限重试、延长 UI 线程等待或保留两套实现来规避验收。所有异步取消、降级和兼容分支必须有明确终止条件。
9. 源码或运行时行为变更完成后，遵循执行时根 `AGENTS.md` 的 ARM64 备份、覆盖和重启默认规则；文档阶段不得仅为验证文档而覆盖正式 EXE。
10. 本规格不包含 GitHub push。除非用户在执行回合明确要求，否则只维护本地 Git 与正式 ARM64 产物。

## 1. 目标

本规格修复 1.0.6.30 界面收敛后审查确认的发布完整性、数据显示、网页契约、API/凭据安全、并发生命周期和死代码问题，使以下不变量同时成立：

1. 干净冷启动一次性创建并管理十个 `MetricTileForm`，不依赖设置重载、显示恢复或人工操作补建。
2. 正式构建只编译显式登记且可复现的源码；干净 Git worktree 能构建出同一功能拓扑，临时或未跟踪 `.cs` 不会被静默纳入。
3. Claude 百分比、Clean IP、Codex IQ 趋势、网页时间戳和 stale 状态均按真实数据语义显示，不出现“后端已更新、前端仍旧值”。
4. Codex Radar `current.json` 是 IQ 的结构化权威来源；fallback 只补缺失字段，不解析错误内容，也不覆盖更新、更可信的数据。
5. 所有外部 HTTP 正文均有解压后的字节上限和绝对截止时间；远端数据不能控制客户端访问本机或内网。
6. DPAPI 密文不可因解密失败被当作明文覆盖；认证响应不原样落盘；外部配置写入原子且不覆盖并发修改。
7. owner 停止、挂起或代次变化后，旧任务不能迟到提交、写盘或驱动 UI；投影读取同一代不可变快照且不执行磁盘 I/O。
8. 已退役的可见渲染、窗口、网络链、系统接口和主题资源从正式二进制中删除；仍被快照、自检或公共组件使用的 helper 先迁移再删。

## 2. 范围外

1. 不改变当前十个右侧 tile、五个左侧 Dock、Operation 和 Settings 的产品拓扑。
2. 不新增 Claude community Radar、DeepSeek 余额、独立连接检测窗口或旧 Radar/Power 浮窗。
3. 不重新设计视觉主题、字号、磁贴顺序或左侧 Dock 交互；仅允许为数据归因、stale 状态和错误状态增加必要且克制的显示。
4. 不修改 Seelen UI、系统任务栏、CTF helper、温控硬件绑定或多显示器布局规则，除非本规格的测试证明它们被直接回归。
5. 不将程序迁移到新 UI 框架、Direct2D、WinUI、WPF、.NET 新运行时或新的 HTTP 第三方库。
6. 不保证 Codex Radar 未公开字段永久兼容；要求的是显式 schema 门、last-good 降级和可诊断失败。
7. 不删除历史 SPEC、GoalSpec、报告或 CHANGELOG 行。

## 3. 已确认问题与需求映射

| ID | 级别 | 已确认问题 | 主要定位 | 目标批次 |
|---|---|---|---|---|
| REL-01 | P1 | `WidgetForm.OnShown` 在启用 child lifecycle 前调用设置应用，十 tile 创建被门控挡住，后续 tick 不补建 | `Core/WidgetForm.cs`、`Core/WidgetForm.TileColumn.cs` | A |
| REL-02 | P1 | 新拓扑核心源码未进入 Git，干净 clone 无法复现；构建脚本递归吞入全部 `.cs` | `Build-Arm64.ps1`、当前 Git 状态 | A |
| DATA-01 | P1 | Claude `used_percent=1` 被误当 0–1 比例并乘 100 | `Core/ClaudeCodeUsageReader.cs` `TryGetUsedPercent` | A |
| UI-01 | P1 | Clean IP 快照更新不参与 Network 重绘条件 | `Core/NetworkMonitorForm.cs` `OnTimerTick` | B |
| UI-02 | P1 | Codex IQ 签名只记录趋势/额度列表数量，不记录实际绘制值 | `Core/CodexIqBoardForm.cs` `BuildSnapshotSignature` | B |
| WEB-01 | P1 | `current.json` 请求失败直接返回，HTML fallback 真正需要时不可达 | `Core/CodexRadarForm.cs` `TryReadCodexRadarStatus` | B |
| WEB-02 | P1 | JSON 解析失败时把 JSON 正文交给 HTML parser；部分 fallback 整体覆盖有效 JSON | 同上及 `CopyCodex*Snapshot` | B |
| WEB-03 | P2 | 不校验 `schema_version`，`model_iq.updated_at` 被抓取时间替代 | `TryParseCodexRadarStatus` | B |
| WEB-04 | P2 | `model-ratings` 和部分 `quota_radar` 链仍刷新，但无可见消费者 | Radar reader、`RadarTileSnapshot`、旧 renderer | B/E |
| WEB-05 | P2 | 外部数据声明归因要求，但收敛后的正常 Codex IQ/CDX 表面未形成明确归因 | Codex IQ board、tile expand、公开 API requirements | B |
| SEC-01 | P1 | DPAPI 解密/格式异常被当作旧明文并原地覆盖原密文 | `Core/SecretStore.cs` `TryReadOrMigrateSecret` | C |
| SEC-02 | P2 | `current.json.links.full_api` 可控制手动服务探测 URL，形成盲 SSRF | `FormatCodexRadarCurrentProbe`、`ReadCodexRadarProbeEndpoint` | C |
| SEC-03 | P2 | 多个外部响应 `ReadToEnd()` 无正文上限，超时不是绝对 body deadline | Radar、Claude、Statuspage、Cloud、CleanIP、GFW | C |
| SEC-04 | P2 | ChatGPT 已认证完整响应在身份变化时原样明文落盘，且发生在异常快照拒绝前 | `Core/CodexRadarForm.CodexUsage.cs` | C |
| SEC-05 | P2 | `auth.json` 无大小限制且递归寻找任意嵌套 `access_token` | `GetCodexAccessToken`、`FindCodexAccessToken` | C |
| SEC-06 | P2 | Claude statusline 脚本和 `settings.json` 直接覆盖，缺少原子提交与并发检测 | `Core/ClaudeCodeUsageReader.cs` | C |
| SEC-07 | P3 | Logger 缺少统一脱敏层；未来异常正文可能把 token/Cookie 写盘 | `Core/Logger.cs`、`NetworkCheckHistoryLogger` | C |
| SEC-08 | P3 | Codex app-server 可执行文件允许可写目录/裸 PATH 回退，身份验证不足 | `Core/CodexQuotaGoalPlanner.cs` | C |
| SEC-09 | P3 | GFW TLS 诊断阶段接受证书，结果命名容易被误解为证书可信 | `Performance/GfwProbeReader.cs` | C |
| LIFE-01 | P2 | Claude 6 小时 TTL 只约束缓存读写；连续失败后进程内旧值仍标为已知 | Claude scheduler result、`BuildRadarTileSnapshot` | D |
| LIFE-02 | P2 | owner Stop/Dispose 不取消在途 Radar/Codex 请求，迟到任务仍可能写状态、缓存或 BeginInvoke | `StopHeadlessDataOwner` 与异步完成路径 | D |
| LIFE-03 | P2 | Radar snapshot、model key、revision 分次写入，投影无统一锁，可能跨代组合 | family runtime producer、`BuildCodexIqBoardSnapshot` | D |
| LIFE-04 | P2 | 协调器的显示挂起调用没有直接关闭两个 headless owner 的采样门，依赖其 HWND 再收系统消息 | Widget/Radar/Power suspend API | D |
| PERF-01 | P2 | 声称 cache-only 的 IQ 投影每 5 秒在 UI 线程读取模型目录 INI | `CodexRadarForm.TileSnapshot.cs`、`CodexRadarModelCatalog.LoadModels` | D |
| DEAD-01 | P2 | 永久 headless 的 Codex Radar 旧 paint/hover/EvenRow/cache 仍完整编译 | `CodexRadarForm*`、`RadarBottomInfoTextRenderer` | E |
| DEAD-02 | P2 | Power headless owner 仍保留旧 ThreePane 绘制；摘要 helper 与绘制代码耦合 | `PowerThermalForm.cs`、`PowerThermalForm.ThreePane.cs` | E |
| DEAD-03 | P2 | QuickGrid 已无产品入口，但被旧自测和 render sample 保活 | `OperationForm.QuickGrid.cs` 等 | E |
| DEAD-04 | P2/P3 | 已禁用 AppBar、Launchpad Shell、DWM thumbnail、Network 浮窗 helper 和旧设置主题仍存在 | `Interop/NativeMethods.cs`、Network/Settings 资源 | E |
| DEAD-05 | P2 | 旧 Statuspage wrapper、无消费者 service push、若干 private 方法仅有声明 | Radar/Widget/Operation/Network | E |
| DOC-01 | P3 | INTERFACE INDEX 的 tile render sample 路径指向不存在的旧文件名 | `Docs/Interfaces/INTERFACE_INDEX.jsonl` | A/E |

## 4. 总体架构决策

### 4.1 构建源边界

新增受版本控制的 `Build-Sources.json`，作为正式 C# 编译输入的唯一事实源：

```json
{
  "schema_version": 1,
  "sources": [
    "DesktopCodexAssistant.cs",
    "Core/...cs",
    "Settings/...cs",
    "Performance/...cs",
    "Interop/...cs"
  ]
}
```

约束：

1. 路径相对项目根、使用正斜杠、大小写唯一、不得包含 `..`、绝对路径、通配符或项目根外文件。
2. `Build-Arm64.ps1` 只编译清单项，不再把递归枚举结果直接交给编译器。
3. 构建前仍递归扫描受管源码目录并与清单对账：缺文件、重复项、清单外 `.cs`、目录外路径均失败并列出差异。
4. 普通开发构建允许清单内文件尚未 commit，但正式部署 Gate 必须证明所有清单项已由 `git ls-files` 跟踪，且不存在清单外未跟踪 `.cs`。
5. `RenderSample` 和自测源码必须显式列入，不得用文件名模式粗暴排除。
6. 新文件格式登记到 `INTERFACE_INDEX`；README 只在构建入口说明需要时更新，不复制清单内容。

### 4.2 数据源优先级

1. Codex IQ：`current.json` schema 2.x 结构化数据 > 进程内 last-good > 磁盘 last-good。
2. HTML 首页不再作为 IQ 数据源。HTML fallback 仅可解析确认仍由服务端静态输出的字段，并且只能填补目标字段的 unknown 值。
3. RSS 只承担它当前已经明确拥有的 reset/feed 语义，不得补写 IQ、额度或模型目录。
4. Claude 个人额度只来自官方 usage/statusline 链；社区 Radar 和公开网页额度不得恢复。
5. 可见窗口永远不发起网络或磁盘读取，只消费 owner 发布的不可变快照。

### 4.3 安全默认值

1. 认证数据解析失败、schema 未知、密文解密失败、URL 不在 allowlist 或响应超限时一律失败关闭，同时保留 last-good，不做猜测性迁移或跨源覆盖。
2. URL allowlist 在请求前后都检查：规范化 URI、scheme、host、port、userinfo、DNS 解析结果和每次 redirect 目标。
3. 所有响应大小指解压后的 UTF-8 字节数；`Content-Length` 仅作预检，不能代替流式累计上限。
4. 日志、诊断文件和 UI 不包含 token、Cookie、Authorization、完整认证正文、账户 ID、完整 JWT 或 DPAPI 密文。

### 4.4 生命周期与快照

1. 每个 headless owner 持有单调递增 generation 和可取消的停止信号。
2. 异步任务捕获启动 generation；提交 UI、写缓存、发通知前必须确认 owner 未停止且 generation 仍匹配。
3. family 状态按一个锁或一次不可变对象替换提交；消费者在同一临界区 clone，禁止从多个可变字段拼接跨代快照。
4. `PrepareForDisplaySuspend` 必须直接关闭 owner 的网络/采样门，不能依赖稍后收到第二份系统消息。

## 5. 批次 A：发布完整性与基础正确性

### A1. 建立可复现 Git/构建基线（REL-02、DOC-01）

实施要求：

1. 记录执行开始时的 `git status --short`、当前版本、HEAD、正式 EXE 版本/哈希和全部未跟踪 `.cs`。
2. 对每个未跟踪源码按“现行拓扑必要 / 独立 pending SPEC / 临时产物 / 未知”分类；未知项不得擅自删除。
3. 将已在 1.0.6.30 正式功能中使用的新拓扑源码纳入本地 Git 历史；不得推送远端。
4. 创建并接入 §4.1 的 `Build-Sources.json`。正式 Gate 从 Git 索引或干净 worktree 构建，不从污染工作目录证明可复现性。
5. 修正 INTERFACE INDEX 中 `Core/MetricTileColumnForm.RenderSample.cs` 的过期路径为当前实际文件，并增加索引路径存在性 Gate。

验收：

1. 清单 JSON 可解析，路径唯一且全部存在。
2. 在源码目录放入未列入清单的临时 `.cs` 后构建必须失败且错误明确；删除临时文件后恢复成功。
3. 从 `git archive` 或独立干净 worktree 构建 ARM64 候选成功。
4. 干净构建包含十 tile、五 Dock 与 headless owner 所需类型；不包含未批准的 `ChinaEgressWarningForm`，除非其独立 SPEC 已先实施并更新清单。

### A2. 修复冷启动 tile 生命周期（REL-01）

实施要求：

1. 保持 `WidgetForm` 宿主不可见；不得通过恢复旧 Widget 绘制解决。
2. 在 Codex/Power owner、Network、Operation 与 provider delegate 初始化完成后，显式、幂等调用 `EnsureMetricTileWindows` / `ApplyMetricTilePresentation`，然后再启动主 timer。
3. 重复设置应用、显示恢复和多次调用不得创建第 11 个 tile、重复 expand panel 或重复事件订阅。
4. 关闭阶段仍按现行顺序释放 tile、expand、owner 和共享订阅。

验收：

1. 新增真实 `Show`/`OnShown` 生命周期自检：不读取正式设置，不在真实桌面显示；首次事件循环后精确存在 10 个未释放 tile 和 1 个 expand panel。
2. 连续应用设置 20 次、显示挂起/恢复 20 次后仍为 10+1，事件回调次数无倍增。
3. 冷启动首个有效 feed 后所有启用 tile 可见且数据模型非 null；禁用 tile 保持隐藏。

### A3. 修复 Claude 百分比单位（DATA-01）

实施要求：

1. `utilization` 仅在 0–1 范围按比例乘 100。
2. `used_percent` / `used_percentage` 始终按 0–100 百分数解释，`1` 表示 1%。
3. NaN、Infinity、负数、超过 100 或不可解析值不得静默发布为正常值。
4. 剩余百分比的 clamp 只能发生在字段语义确认之后，不得掩盖来源单位错误。

验收 fixture 至少覆盖：`0`、`0.01`、`1`、`1.01`、`50`、`99.9`、`100`、`-1`、`101`、字符串和 null；明确断言 `used_percent=1 -> remaining=99`、`utilization=1 -> remaining=0`。

### A4. 批次 A Gate

1. ARM64 构建成功；禁止 x64。
2. `--test`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery`、`--test-radar-display-lifecycle --iterations 20` 全部退出 0。
3. `--render-tilecolumn --out <temp>` 输出完整十 tile，PNG 非空且尺寸/像素签名稳定。
4. 从干净 source boundary 构建的候选与当前工作树构建候选版本、PE machine 和功能自检一致。

## 6. 批次 B：前端重绘与网页契约

### B1. Clean IP 显式失效传播（UI-01）

1. `RefreshCleanIpSnapshot` 返回“可见数据是否变化”，比较集合至少包含检查时间、health、score/grade、native label、IP 类型、IP、位置、ASN、组织及错误状态。
2. `NetworkMonitorForm.OnTimerTick` 将 `cleanIpChanged` 纳入重绘条件。
3. 仅时间戳变化是否重绘必须明确：若 UI 显示该时间则重绘，否则不应导致无意义刷新。
4. 手动全局刷新仍只触发共享 reader，不创建第二个 Clean IP 实例。

验收：其他网络字段完全相同、只改变 Clean IP score/native label/error 时，render revision 和像素签名必须变化；相同快照不得重绘。

### B2. Codex IQ 完整显示签名（UI-02）

1. `BuildSnapshotSignature` 必须覆盖所有实际绘制字段，至少包括 Models、每条 trend 的全部点、WeeklyQuotaRemaining 的全部值、Roster、Services、Refresh、source timestamp、selected model 和错误/stale 状态。
2. 可使用稳定结构 hash，禁止只记录集合数量或对象引用。
3. clone 后签名相同必须意味着当前绘制像素语义相同；非绘制诊断字段可排除并写注释说明。

验收：相同 count、不同点值/顺序/周额度/服务状态均改变签名并触发一次重绘；完全相同快照连续 20 次不重复渲染。

### B3. 重构 Codex Radar source adapter（WEB-01、WEB-02）

1. 将“下载”和“解析”拆成明确方法；JSON 请求失败或 JSON parse 失败后，若允许 HTML fallback，必须真正请求首页，不能把 JSON 正文传给 HTML parser。
2. IQ 只接受 `current.json`；当前首页不含服务端 IQ row 时，不得声称 HTML IQ fallback 成功。
3. fallback 合并按字段执行 `FillUnknownFromFallback`：只补 unknown，不覆盖 JSON 已知 IQ、窗口、额度、时间戳或 health。
4. JSON 主链失败时保留 last-good 并标注 stale/unavailable；不得把失败刷新时间写成数据更新时间。
5. parser fixture 必须包含：完整 schema 2、缺 IQ、缺 quota、未知 major、损坏 JSON、HTML 动态占位、HTML 只有 quota、RSS 正常/损坏。

### B4. Schema、时间戳与归因（WEB-03、WEB-05）

1. 解析并检查 `schema_version`：支持已验证的 2.x；未知 major 不进入正常路径，记录结构化 `schema_incompatible`，保留 last-good。
2. IQ 更新时间优先使用 `model_iq.updated_at`，其次使用响应中明确的数据时间；抓取时间单独保存为 `FetchedAt`，不得冒充 source time。
3. Codex IQ 正常表面必须提供不误导布局的来源归因，例如“数据来源：Codex 雷达 codexradar.com”；归因字符串由代码常量或可信配置提供，不直接渲染远端任意 HTML。
4. 若 API requirements 中归因文字变化，只接受经过长度/字符过滤的纯文本并保留本地安全默认值；不得成为超链接脚本或绘制指令。

### B5. 处理无消费者网页链（WEB-04）

1. 对 `model-ratings` 和 `quota_radar` 做生产调用图复核，列出 producer、缓存、snapshot 字段和可见 consumer。
2. `model-ratings` 若仍无可见消费者，删除其请求、parser、scheduler、snapshot Rating 字段、缓存和索引依赖；不得继续定时请求“以后可能用”。
3. `quota_radar` 若 Codex IQ 周额度实际使用，接入 B2 签名并补测试；若仅由永久隐藏 renderer 使用，则删除请求/解析/fallback 判定。
4. 任何保留链必须有至少一个生产消费者和确定性测试；自测、历史 renderer 或文档引用不算生产消费者。

### B6. 批次 B Gate

1. 全部 parser/merge/signature/Clean IP fixture 退出 0。
2. 公共 live smoke（可选且不作为唯一证据）：`current.json`、RSS、OpenAI/Anthropic status 只发无认证有界 GET，记录 schema/status，不保存正文。
3. `--test-layout`、`--test-operation-panel`、`--render-networkmonitor`、`--render-tilecolumn` 通过。
4. Codex IQ 离屏样张覆盖同长度趋势更新、stale、unknown schema、fallback 失败和来源归因；无白屏、文本溢出或旧曲线残留。

## 7. 批次 C：凭据、HTTP 与外部调用安全

### C1. SecretStore 版本化密文与失败关闭（SEC-01）

1. 新写密文使用明确 envelope，例如 `dpapi-v1:<base64>`。
2. 已带 `dpapi-v1:` 的内容解密失败时：返回错误、保留原文件字节不变、不删除 legacy、不发网络请求。
3. 兼容迁移顺序：带版本 envelope -> 旧版可成功解密的无前缀 DPAPI -> 已知旧 plaintext 路径/严格 token 格式；任意 Base64 或未知文本不得自动当 token。
4. plaintext `.bin` 兼容仅接受拥有明确历史依据的 token 形态、长度和字符集；迁移成功后原子替换，失败保留原文件。
5. 错误日志只记录 error code、路径类别和异常类型，不记录 secret、密文正文或哈希前缀。

验收：有效新密文、有效旧 DPAPI、合法旧明文、随机 Base64、损坏新 envelope、另一用户不可解密模拟、锁定文件、原子替换失败、legacy 清理失败全部有 fixture；损坏密文前后 SHA256 必须一致。

### C2. 统一有界 HTTP 文本读取器（SEC-03）

新增共享内部 helper，并登记 INTERFACE INDEX。最低能力：

1. `Content-Length` 预检、逐块累计解压后字节、达到上限前终止、绝对 deadline、请求 abort/cancellation。
2. 明确返回 `success/status/content_type/bytes/error_code`，超限使用稳定错误码 `BODY_TOO_LARGE`，deadline 使用 `BODY_DEADLINE`。
3. 默认禁止把正文加入异常消息或日志；调用方只在成功且类型/大小符合预期后解析。
4. 推荐上限：认证 usage/reset 512 KiB；public JSON/status 1 MiB；RSS 512 KiB；首页 HTML 2 MiB；小型探测 64–256 KiB；NCSI 保持现有 4 KiB。执行者可基于当前最大正常响应调整，但必须在代码常量和刷新规则文档中写明。
5. `JavaScriptSerializer.MaxJsonLength` 不得继续使用 `int.MaxValue`；必须与对应 body cap 一致，并为数组项数/递归深度设置业务上限。
6. 迁移 Radar、Claude、Statuspage、CloudEndpoint、CleanIp、GFW 等所有活跃 `ReadToEnd` 链；死链直接删除，不为其补 helper。

验收：无 Content-Length、伪造小 Content-Length、chunked 慢流、压缩后超限、刚好上限、上限+1、deadline、取消、正常 UTF-8 和错误编码；每个测试小于 5 秒且进程内存回落，无未观察任务异常。

### C3. Codex Radar 探测 URL allowlist（SEC-02）

1. `links.full_api` 先经过共享 URL validator；只允许 `https`、精确允许的 Codex Radar host、443、无 userinfo、规范化路径。
2. 默认 `AllowAutoRedirect=false`。若业务必须支持 redirect，只能手动逐跳处理并对每一跳重复 allowlist/DNS 检查，最多 3 跳。
3. 阻止 loopback、private、link-local、multicast、unspecified 和本机接口地址；DNS 解析与连接目标不一致时失败。
4. 无效远端 URL 回退到本地编译时常量，或把 full API 标为 unavailable；不得“尽量请求”。

验收 URI：同源 HTTPS、大小写/尾点、HTTP、userinfo、非 443、localhost、127.0.0.1、`[::1]`、RFC1918、link-local、十进制/IPv6 映射、同源到内网 redirect、超长 URL。

### C4. 删除认证原始正文持久化（SEC-04）

1. 从 `CodexQuotaSnapshot` 移除 `ProviderRawResponseBody` 或等价完整正文持有字段。
2. 身份变化诊断发生在异常快照拒绝之后，只记录白名单字段：窗口 reset、used percent、pool/plan 的安全枚举、HTTP status、响应字节数、body SHA256 和 correlation ID。
3. 诊断使用程序 JSONL logger 或新的受控 JSONL schema；不得继续写任意上游 JSON 文件。
4. 对现有 `codex-usage-identity-change-*.json` 只停止新增，不自动读取、上传或删除用户历史文件；清理策略需另行获得用户授权。

### C5. 收紧 `auth.json` 读取（SEC-05）

1. 文件大小先限制后读取；推荐 1 MiB。
2. 仅支持经过 fixture 验证的已知字段路径，不递归寻找任意 `access_token`。
3. 多个候选 token 或 schema 未知时失败关闭，记录 `AUTH_SCHEMA_UNSUPPORTED`，不选择“第一个”。
4. 可解析 JWT 时校验预期 issuer/audience；不可解析 token 不因格式本身写日志或显示。
5. 不修改、不刷新、不迁移 Codex 管理的 `auth.json`。

### C6. Claude statusline 原子集成（SEC-06）

1. 读取 `settings.json` 后记录文件 identity（长度、LastWriteTimeUtc、内容 hash）；提交前重新读取并检测并发变化。
2. 在同目录写唯一 temp，flush 后使用 `File.Replace`/同卷原子替换；保留原 DACL 和现有备份语义。
3. 发生并发变化时重新 merge，最多 2 次；仍冲突则失败且不覆盖。
4. 生成 bridge script 同样原子写；自定义非本程序 statusline 继续拒绝覆盖。
5. JSON 损坏、文件锁定、目录只读、程序中断模拟均不得留下半个 `settings.json` 或 `.ps1`。

### C7. 防御纵深（SEC-07、SEC-08、SEC-09）

1. Logger 在最终序列化前统一脱敏键名和常见文本模式：Authorization、Bearer、Cookie、token、api_key、JWT、setup-token；保留 error code 和相关 ID。
2. `CodexQuotaGoalPlanner` 不直接执行裸 `codex`。解析为绝对规范路径，拒绝当前目录、TEMP 和不可信可写落点；优先验证官方安装路径/签名发布者，无法验证时记录并不启动 app-server。
3. GFW TLS 阶段若为诊断协议握手而允许证书错误，结果必须命名为“协议握手可达”，同时单独记录 certificate trust；该 callback 不得进入认证请求或共享全局 TLS 设置。

### C8. 批次 C Gate

1. `--test`、`--test-logger`、`--test-settings-bindings` 全部退出 0。
2. 测试结束后扫描临时日志/诊断文件：fixture secret、Bearer、Cookie、JWT、完整响应标记命中 0。
3. SSRF fixture 的 loopback/私网请求实际连接次数为 0。
4. 超限/慢流测试有确定截止，不依赖公网。
5. 正式用户 `.claude/settings.json`、真实 `auth.json`、token 文件的 hash/mtime 均未被测试改变。

## 8. 批次 D：TTL、取消、快照与性能

### D1. Claude runtime TTL（LIFE-01）

1. `BuildRadarTileSnapshot(Claude)` 发布前调用统一 freshness 判断。
2. 超过 360 分钟：保留 last-good 内存值用于诊断，但 `QuotaKnown=false`，UI 显示 stale/unknown，不继续把旧百分比作为当前已知值。
3. 新成功完整快照恢复 known；部分、认证失败或网络失败不得刷新 source timestamp。

验收使用可注入时钟覆盖 359:59、360:00、360:01、未来时钟偏差、程序跨午夜和恢复成功。

### D2. owner generation 与取消（LIFE-02）

1. Start/Stop/恢复建立 generation；所有 Radar、Codex usage/reset、Statuspage、DeepSeek completion 捕获 generation。
2. Stop 后取消可取消请求；不可取消请求完成时丢弃结果，不写缓存、不发通知、不 BeginInvoke。
3. `BeginInvoke` 前检查 `IsDisposed`、`IsHandleCreated`、generation；捕获并观察 task exception。
4. Stop/Dispose 幂等，重复调用不抛异常。

验收：阻塞 fake transport 中执行 Stop/Dispose，释放任务后状态 revision、文件 mtime、日志业务事件和 UI 回调计数均不变化。

### D3. 同代不可变快照（LIFE-03）

1. 把 `RadarSnapshot`、`ModelKey`、catalog revision、service health 与相关 source timestamps 组成一次提交单元。
2. producer 一次替换；tile/IQ projection 在同一锁内 clone，离开锁后不再读取可变 owner 字段。
3. 不在锁内做网络、磁盘、日志、绘制或 BeginInvoke。

验收：两个带 generation 哨兵的状态并发交换至少 10,000 次，所有投影只能是完整 A 或完整 B，跨代组合计数为 0。

### D4. 挂起门控（LIFE-04）

1. Widget 调用 `PrepareForDisplaySuspend` 后，Radar network gate 和 Power sampling gate立即为 false。
2. owner 自身收到重复 WM/Power 通知时保持幂等，不重复释放资源。
3. Resume 只 prime 一次刷新/采样；不得造成 refresh storm 或双 timer。

### D5. 消除 cache-only 投影磁盘 I/O（PERF-01）

1. `CodexRadarModelCatalog` 由 owner 在启动/成功刷新/显式缓存 reload 时载入内存。
2. `BuildCodexIqBoardSnapshot` 只读内存 clone，不调用 `LoadModels`、`File.*` 或目录 API。
3. 若模型目录文件在运行中变化，使用 owner 已有 watcher/调度点合并更新；不得由 5 秒 UI timer 轮询磁盘。

验收：注入文件访问计数器，连续 100 次 tile/IQ projection 的文件访问为 0；锁定/删除目录文件不增加 UI projection 延迟或异常。

### D6. 批次 D Gate

1. `--test-radar-display-lifecycle --iterations 50`、`--test-display-recovery`、`--test-layout` 退出 0。
2. 关闭/挂起竞态循环 100 次，无未观察异常、迟到写盘、handle/GDI/USER 单调增长。
3. TimingStats 中 IQ snapshot projection 不包含文件 I/O，P95 不高于修复前基线；若无法取得同机基线，至少记录绝对 P95/P99。

## 9. 批次 E：死代码与依赖收敛

### E1. 删除原则

1. 先生成“符号 -> 生产调用方 -> 自测调用方 -> 迁移目标”清单，再删除。
2. WinForms override、事件委托、反射/CLI 入口和序列化兼容字段不得仅凭文本命中次数判死。
3. 仅被已退休 renderer 自测调用不算生产存活依据；先决定该自测是否仍验证现行产品契约。
4. 每删除一个代码簇立即 ARM64 编译并跑最窄自检，不在一个不可审查 patch 中同时删除全部簇。

### E2. Codex Radar renderer

1. 迁移仍被 snapshot/自测使用的 countdown、短模型名、dial cycle、内容签名等 helper 到 owner/snapshot 对应文件。
2. 删除永久不可达的 `OnPaint -> DrawCodexRadar`、EvenRow 几何、hover、旧边框、旧 layered cache、旧 render callback 和只服务旧窗口的资源。
3. 删除 `RadarBottomInfoTextRenderer` 的生产死链；若某项现行 tile 自测仍使用，迁入 tile 专属 helper。
4. `CodexRadarForm` 保留调度、cache、family runtime、service health、quota 和 snapshot API，不恢复可见能力。

### E3. Power renderer

1. 将 `ThermalSummary` / `BuildThermalSummary` 移入 `PowerThermalForm.Snapshot.cs` 或明确数据层。
2. 删除 `PowerThermalForm.ThreePane.cs` 和仅由旧 `DrawPowerThermalWindow` 调用的 paint/hover/position 分支。
3. 保留 headless sampler、通知 HWND、挂起/恢复和 `BuildStripSnapshot`。

### E4. 旧窗口与系统接口

1. 若 QuickGrid 已确认无产品入口，删除 QuickGrid window、自测和旧 render 分支；把仍有效的 launcher sample 移到正确 owner 文件。
2. 分簇删除无调用方的 AppBar、Shell shortcut/icon、DWM thumbnail P/Invoke、结构体、COM 声明和 wrapper；不得触碰当前任务栏图标、托盘图标或普通 DWM/Win32 调用。
3. 删除 Network 旧浮窗 `DrawInfoRow`/DNS/接口/Wi-Fi/连通性/GFW 文本 helper，以及无消费者的 Radar service push；保留当前 Dock 绘制链。
4. 删除孤立 `SettingsTheme`、`NeonGeekTheme`、旧 Fluent 控件 factory/card/row，仅在全仓无构造、反射和设计器引用后执行。
5. 复核并删除审查列出的单点方法和旧 Statuspage wrapper；任何仍有公共契约的入口应移入适当组件而不是保留空壳。

### E5. 死代码 Gate

1. 禁用符号静态断言：旧 AppBar/Launchpad/DWM thumbnail、QuickGrid、旧 Radar/Power paint 入口在生产源码命中 0；历史 Docs 命中不计。
2. 正式 `Build-Sources.json` 不含已删文件，源码目录也不存在清单外 `.cs`。
3. ARM64 全构建、自检、显示恢复、生命周期压力和四类现行 render sample 全通过。
4. 运行时可见集合仍严格为 10 tile + 5 Dock tab/board + Operation + 按需 Settings；Codex/Power owner 无 `Show`/`ShowDialog` 调用。

## 10. 版本、文档与索引同步

每个批次作为独立、已验证变更使用执行时下一个可用版本；不得预占已经被其他任务使用的版本。每批完成时：

1. 同步 `ProductIdentity.Version`、根 `AGENTS.md` Current version、程序集/文件/产品版本和产物版本。
2. 刷新受影响活文档的适用版本与现状：
   - `Docs/CodexRadar-Architecture.md`
   - `Docs/Codex-ClaudeRadar-Architecture.md`
   - `Docs/NetworkMonitor-Architecture.md`
   - `Docs/PowerThermal-Architecture.md`
   - `Docs/Performance-And-Window-Runtime.md`
   - `Docs/Component-Refresh-Rules.md`
   - `Docs/Interface-And-Reuse-Resources.md`
3. 更新 FEATURE INDEX 中 tile column、Codex IQ、Network Clean IP、Claude quota、Radar web source、headless owner 的文件、入口和推荐测试。
4. 更新 INTERFACE INDEX：新增 build source manifest 和 bounded HTTP helper；修订 SecretStore、statusline、auth、Radar API、raw diagnostic、headless snapshot；删除项标 `removed`，不抹除历史 ID。
5. 每项实质修复追加 CHANGELOG；每次正式部署单独追加 deployment 记录。验证证据必须是实跑输出，Token 不可用时写 null/`platform_unavailable`。
6. 最终执行完成后将本 SPEC 的 Technical INDEX 状态从 `approved` 改为 `implemented`，登记 GoalSpec/执行报告/hash；Spec Board 先进入 `awaiting_verify`，独立验收通过后才进入 `done`。

## 11. 全局验收流水线

每个批次最少执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe -Platform arm64
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --test
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --test-logger
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --test-layout
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --test-settings-bindings
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --test-display-recovery
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --test-operation-panel
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --test-radar-display-lifecycle --iterations 50
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --render-tilecolumn --out .\_build\post-convergence-audit\renders
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --render-networkmonitor --out .\_build\post-convergence-audit\renders
.\_build\post-convergence-audit\DesktopCodexAssistant-arm64.exe --render-operation --out .\_build\post-convergence-audit\renders
python .\Docs\validate_docs.py
git diff --check
```

补充硬 Gate：

1. 所有命令退出码 0；不存在“预期通过”替代实际输出。
2. 所有生成 JSONL 逐行解析、ID 唯一、路径存在。
3. render PNG 全部非空，尺寸符合当前布局，像素 alpha/颜色分布非全透明、非全黑、非全白。
4. 正式候选 PE machine 为 ARM64 `0xAA64`，版本与文档一致。
5. 候选、正式根入口与 `Release/DesktopCodexAssistant-arm64.exe` 在部署后版本/长度/SHA256 一致。
6. 新进程启动后旧 PID 退出、PID 响应正常、error log 无新增 Fatal；冷启动十 tile 自检必须在设置重载前完成。
7. 不构建 x64，不 push GitHub，不访问真实认证 API。

## 12. 备份、回滚与中止条件

### 12.1 备份

每批源码修改前备份将触碰的文件；每次正式覆盖前备份正式 EXE、Release EXE、当前版本/哈希，以及可能迁移的 settings/secret/cache 文件。备份目录必须在项目约定的 retained-build-backups 下，记录时间和基线版本。

### 12.2 回滚

1. 批次 A–D 回滚必须同时恢复代码、对应设置/schema、正式 EXE 和接口索引状态。
2. SecretStore envelope 发布后回滚到不识别 envelope 的旧 EXE 会导致 Claude token 不可读；部署包必须保留升级前密文备份，且回滚流程不得把新 envelope 当 plaintext。
3. 批次 E 删除代码后只能通过该批次源码备份/Git commit 回滚，不允许从历史 SPEC 手工复制片段。
4. 任何回滚不得恢复已确认不安全的 raw response 写盘或任意 URL 请求；必要时宁可保持功能 unavailable。

### 12.3 立即中止条件

出现以下任一情况应停止当前批次，不部署正式 EXE，并将 Spec Board 标为 `needs_revision` 或记录实现阻塞：

1. 干净 worktree 无法重现十 tile/五 Dock 拓扑。
2. 需要读取真实凭据、调用认证接口或修改用户外部配置才能完成自动验收。
3. SecretStore 测试改变了失败密文的字节或 hash。
4. SSRF 测试实际连接 loopback/私网。
5. Stop/Dispose 后仍发生缓存写入、通知或 UI 提交。
6. 删除旧 renderer 后 tile、Network、Codex IQ 或 Power snapshot 缺字段/空白。
7. 文档 Gate、Git source manifest Gate 或任一必跑自检失败。

## 13. 完成定义

只有以下条件全部满足，本 SPEC 才可标为 implemented，随后交给独立验证者：

1. REL、DATA、UI、WEB、SEC、LIFE、PERF、DEAD、DOC 全部条目均有代码变更或“复核后不成立”的可重复证据；不得静默跳过。
2. 冷启动十 tile、Claude 1% 单位、Clean IP/IQ 重绘、Radar fallback/schema、SecretStore fail-closed、SSRF、body cap、TTL、generation、snapshot atomicity 和 cache-only 零 I/O 均有确定性回归测试。
3. 正式构建源集合可复现，所有正式源码已跟踪且清单外 `.cs` 为 0。
4. 认证正文不再原样落盘，日志脱敏 fixture 命中 0，外部配置原子写 fixture 全通过。
5. 旧 Radar/Power renderer、QuickGrid 和禁用系统接口已按依赖顺序删除，现行功能与渲染验收无回归。
6. ARM64 候选、全自检、生命周期压力、render、Docs Gate、版本/hash/PE 检查全部通过。
7. CHANGELOG、FEATURE/INTERFACE INDEX、活文档、Technical INDEX、GoalSpec/执行报告和 Spec Board 状态均已同步。

