# Claude CLD-only 与 DeepSeek 余额退役 GoalSpec

- 执行模型：Codex
- Goal：完整执行 `Opus48-ClaudeCldTileOnlyAndDeepSeekBalanceRemoval-SPEC-v1.0.6.25-20260721-165145.md`，把 Claude 数据面收敛为 CLD 官方额度链，并把 DeepSeek 收敛为无凭据服务健康。
- SPEC 路径：`Docs/Technical/Opus48-ClaudeCldTileOnlyAndDeepSeekBalanceRemoval-SPEC-v1.0.6.25-20260721-165145.md`
- SPEC SHA256：`7041AA99EC557E7788581C387F4FC27F3214EE8F1D0AD49A6B279F164A6D6B1D`
- 实现版本：`1.0.6.30`
- 执行时间：2026-07-22 03:16:02 +09:00

## 1. 结果

SPEC 的 A–E 阶段已在一个可回滚的原子正式版本中完成。右侧 CLD tile 保留，模型标签固定为 `Claude`，只消费官方 Claude Code usage/statusline 额度；5 小时和周额度必须同时带各自 reset 与可信更新时间，完整且新鲜时才发布、落盘或恢复。DeepSeek API key、余额、余额历史、余额告警和设置入口已从运行时移除，只保留无凭据服务健康灯。

## 2. 需求映射

### 阶段 A：切除 DeepSeek 余额

- 删除 `Core/DeepSeekBalanceMonitor.cs`、余额显示、缓存签名、余额告警分类、设置页 key/余额入口和 SecretStore DeepSeek key 读写。
- 新增 `Core/DeepSeekServiceMonitor.cs`，以无凭据 `GET https://api.deepseek.com/models` 判定服务网关可达性；2xx/400/401/402/422 视为服务可达，403/429/5xx 视为已知不可用，无响应视为未知/不可达。
- timer、网络变化和手动刷新 join 进程级 single-flight；正常 60 秒、异常 300 秒。Network history 只记录健康、错误码和 joined consumer，不记录凭据、余额或响应正文。
- DeepSeek 健康灯保留在 Network/Codex IQ 投影中；`BuildServiceHealth()` 直接投影 raw state，避免提醒开关或告警防抖把真实故障误画为绿色。

### 阶段 B：CLD 锚定官方额度链

- Claude family 只接受 `ClaudeCodeUsageScheduler` 结果；社区 Radar snapshot、模型选择和本地历史 fallback 不再进入 family state。
- `BuildRadarTileSnapshot(Claude)` 固定 `ModelName=Claude`，IQ、评分与效率恒 unknown。
- `ClaudeCodeUsageReader` 统一完整性门要求：两个 percent、两个 reset、可信且 6 小时内的新鲜 `SourceUpdatedUtc` 缺一不可。
- OAuth partial 可由官方 Messages rate-limit header 补齐；补齐后再次过完整性门。仍不完整时返回 `QUOTA_INCOMPLETE`，进入失败退避并保留 last-good。
- Scheduler 发布、`claude-quota.ini` 原子写入和启动恢复均复用同一完整性门；partial 不覆盖内存或磁盘 last-good。

### 阶段 C：删除社区链与旧呈现层

删除：

- `Core/ClaudeRadarReader.cs`
- `Core/ClaudeRadarSnapshotScheduler.cs`
- `Core/ClaudeRadarModels.cs`
- `Core/ClaudeRadarModelMapEditorForm.cs`
- Claude community rating、public Radar cache/history、模型时钟选择与不可达 EvenRow Claude 呈现分支

Codex 公共 Radar 链保持不变。Claude family cache/restore 只保存官方 quota；旧 `claude-radar-*` 磁盘文件不再读取或写入，但未自动销毁，以免执行不可逆数据删除并保留人工回滚证据。

### 阶段 D：入口、设置与文档收敛

- `WidgetSettings.CurrentSettingsVersion` 从 85 升至 86。
- canonical save 退休 7 个键：`DeepSeekApiKeyRevision`、`AlertDeepSeekBalanceEnabled`、`ClaudeRadarJsonEnabled`、`ClaudeRadarHomepageFallbackEnabled`、`ClaudeRadarCommunityRatingsEnabled`、`ClaudeRadarLocalQuotaFallbackEnabled`、`ClaudeRadarModelKey`。
- 迁移覆盖 Version=85 和无 Version 的旧文件，使用同目录临时文件后原子 Replace/Move；fixture 验证保留键、不残留 tmp、退休键不重新输出。
- Settings 仅保留 Claude setup-token 管理，不再显示 Claude community 或 DeepSeek key/余额控件。
- 活文档、FEATURE/INTERFACE 索引和版本头同步到 1.0.6.30；退休接口保留稳定 ID 并标记 `removed`。

### 阶段 E：构建、备份与正式部署

- 源码备份：`E:\Codexproject\desktopdata\DesktopCodexAssistant-retained-build-backups\source-backups\20260722-021226-pre-cld-deepseek-v1.0.6.29`，201 文件，8,313,662 bytes。
- 正式备份：`E:\Codexproject\desktopdata\DesktopCodexAssistant-retained-build-backups\formal-backups\20260722-030132-pre-cld-only-v1.0.6.29`，包含根/Release 1.0.6.29 EXE 与 schema 85 设置副本。
- 旧 PID 16088 经 `--stop` 正常退出，未强杀。
- 最终候选、根入口与 Release 均为 2,201,600 bytes、PE machine `0xAA64`、File/Product version `1.0.6.30`、SHA256 `CCCFCEA02CAD8D6DD52B37E824D98AA8FBE46D39B76197E784086E814DA594D5`。
- 新 PID 59356 从 E: 根入口启动并持续 `Responding=True`。

## 3. 架构与数据流

```text
Claude running
  -> ClaudeCodeUsageScheduler (single-flight/backoff)
  -> ClaudeCodeUsageReader (OAuth usage -> bounded Messages headers -> statusline)
  -> complete snapshot gate
  -> Claude family quota + atomic claude-quota.ini
  -> BuildRadarTileSnapshot(Claude)
  -> CLD tile / expand

shared owner tick / network event / manual refresh
  -> DeepSeekServiceMonitor (single-flight, no credential)
  -> raw service-health snapshot
  -> Network + Codex IQ health projection
```

复用的主要索引项为 `claude_radar.claude_code_usage`、`codex_radar.deepseek_service_health`、`codex_radar.quota_consumption_ring`、`internal_api.claude_code_usage_reader`、`internal_api.claude_code_usage_scheduler`、`external_api.deepseek.service_health`、`internal_api.deepseek_service_monitor` 与 `config.settings_ini`。

## 4. 验证证据

最终候选：`_build/claude-cld-only-v1.0.6.30-final/DesktopCodexAssistant-arm64.exe`。

- `--test`：exit 0；包含 Claude 完整性门、OAuth partial/header fallback、statusline missing-reset、cache missing-reset、last-good 保护、DeepSeek 三态分类与 schema 86/versionless migration fixture。
- `--test-settings-bindings`：exit 0。
- `--test-layout`：exit 0。
- `--test-operation-panel`：exit 0；五 Dock 梯形/箭头/3px 边框、外部点击收起与 PathPing 均 PASS。
- `--test-radar-display-lifecycle --iterations 20`：PASS，handles -1、GDI 0、USER -1。
- `--test-logger`：PASS。
- `--render-tilecolumn`：15 张样张生成；`tilecolumn-large.png` 为 10 tiles，`tileexpand-claudequota.png` 显示 `CLAUDE 77%`，并同时显示 5h 与周额度及两组 reset。
- 静态扫描：`ClaudeRadarReader`、`ClaudeRadarSnapshotScheduler`、`DeepSeekBalanceMonitor`、community rating symbols、`claudecoderadar`、`deepseek-api-key`、`deepseek-balance-history`、`/user/balance`、`Claude.Model.` 在生产代码中均为 0；5 个目标旧文件和 DeepSeek monitor 文件均不存在。
- FEATURE 66 个唯一 ID、INTERFACE 199 个唯一 ID；active feature/interface 对 removed ID 的引用为 0。
- `python Docs/validate_docs.py`：PASS（224 条既有 warning）；`git diff --check` 无 whitespace error，仅换行风格提示。

正式运行验证：

- 真实 `settings.ini` 从 Version=85 迁移为 Version=86，7 个退休键全部移除，`.tmp` 不存在；209→202 keys。除当前 2× DPI/工作区派生尺寸外，其余保留键值不变。
- 旧 `deepseek-balance-history.jsonl` 在新进程运行 75.9 秒后仍为 138,423 bytes，LastWriteTimeUtc 保持 `2026-07-21T18:13:42.2720569Z`，证明新版本停止余额历史写入。
- 新 `deepseek_service` network-history 行为 `Normal/available`，只含 health、service-known、service-available、error-code 与 joined-consumers，余额字段为 0。
- `error.log` 在新进程启动后无新增写入。

渲染证据目录：`E:\Codexproject\desktopdata\DesktopCodexAssistant-retained-build-backups\render-proof\20260722-cld-only-final`。

## 5. 安全、兼容与错误处理

- Claude setup-token 继续只从环境变量或 DPAPI CurrentUser 文件读取；不写入 settings、日志或 snapshot。Authorization header、OAuth token、完整响应正文均不记录。
- DeepSeek 不再读取或持久化 key，探测无认证；运行日志不含账户、余额、响应正文、IP 或 DNS 地址。
- 所有网络链保持 single-flight、超时和失败退避；显示只消费已提交 snapshot。
- 旧余额/Claude community 文件未自动删除；代码不再读取，回滚时可配合备份恢复旧程序。

## 6. SPEC 偏离与判定

- SPEC 建议“每阶段一小版本、逐阶段部署”。本次在同一 1.0.6.30 中保留 A–E 独立代码/测试证据并做一次原子正式部署，避免反复替换正在运行的桌面程序；没有伪造未发生的阶段部署。
- Claude 服务健康灯按 SPEC §4 的建议保留，因为它与 OpenAI/DeepSeek 状态共用现行投影；只删除 Claude community 数据，不删除官方服务健康。
- `Docs/Claude-EvenRow-DialCard-Technical.md` 与 `Docs/Fable5-Data-Sources-And-Caching-Technical.md` 已是带 superseded/tombstone 的冻结历史快照；依照文档治理规则不再次改写历史正文，现行事实写入活架构文档和本 GoalSpec。
- Statuspage 端点在本机出现连接重置，因此 DeepSeek 健康探测采用官方文档定义的无凭据 `/models` 网关响应分类。依据为 DeepSeek 官方 [List Models](https://api-docs.deepseek.com/api/list-models)、[Error Codes](https://api-docs.deepseek.com/quick_start/error_codes) 与 [Status](https://status.deepseek.com/) 页面。

## 7. 限制与遗留风险

- 当前机器虽有 Claude 进程并已安装 statusline bridge，但本次部署观察窗口内尚未生成真实 `claude-statusline-quota.ini`/`claude-quota.ini`；因此 CLD 数据完整性以确定性 fixture、cache fixture 和渲染 harness 验证，真实账号值不会被测试读取或记录。
- 旧磁盘文件仍存在是有意的兼容/回滚边界；恢复旧 1.0.6.29 时需同时使用 formal backup 的 schema 85 设置副本。
- 工作树包含任务开始前已有的大量用户修改；本次未执行 reset、checkout 或无关清理。

## 8. 完成状态

实现、验证、备份、ARM64 正式部署和运行时审计均完成。SPEC Board 可从 `awaiting_verify` 更新为 `done`。
