# Post-Convergence Audit Remediation 执行报告

- 报告版本：`2.0.0.0`
- 报告时间：`2026-07-22T15:04:29.905+09:00`
- 执行模型：Codex
- SPEC：`Docs/Technical/Codex-PostConvergenceAuditRemediation-SPEC-v1.0.6.30-20260722-110209.md`
- SPEC SHA256：`0EE671169FF4175668E438048C09B695A456C1A1A82C2785E360BE9FCA66565C`
- GoalSpec：`Docs/Technical/Codex-GoalSpec-PostConvergenceAuditRemediation-v2.0.0.0-20260722-150429.md`

## 1. 结论

SPEC A-E 五批已实现并通过最终 tracked-source ARM64 候选的完整再验收。正式根入口和 `Release/DesktopCodexAssistant-arm64.exe` 已覆盖为 `2.0.0.0`，与候选 SHA256 完全一致；新进程稳定运行，无新增 Fatal、错误日志写入或 UI watchdog。

## 2. 交付矩阵

| 批次 | 主要问题 | 交付结果 | 主要确定性证据 |
| --- | --- | --- | --- |
| A | 未跟踪源码、递归构建、十 tile 冷启动、Claude 百分比单位 | 完成 | manifest 正/负 Gate、冷启动真实 `WidgetForm`、百分比边界 fixture |
| B | Clean IP/IQ 重绘脱钩、Radar schema/fallback、旧 service push | 完成 | 内容签名变化/稳定断言、schema 2 与 fallback fixture、现行 render |
| C | DPAPI 误迁移、无界 HTTP、SSRF、raw body、auth、statusline、日志、Codex CLI 路径 | 完成 | 密文原字节 fixture、HTTP/URL fake transport、日志脱敏、路径/签名策略 fixture |
| D | TTL、迟到提交、混代快照、挂起门控、投影磁盘 I/O | 完成 | 360 min 边界、generation stop、20k producer/10k projection、100 次零 I/O |
| E | 旧 Radar/Power/QuickGrid/Network renderer 与系统接口 | 完成 | 目标文件删除、禁用符号生产命中 0、35 张现行渲染 |

## 3. 构建与源码边界

执行命令：

```powershell
.\Build-Arm64.ps1 -Platform arm64 -RequireTrackedSources `
  -OutputPath .\_build\post-convergence-final\DesktopCodexAssistant-arm64.exe
```

结果：退出 0。`Build-Sources.json` 与 `DesktopCodexAssistant.cs`、`Core/`、`Settings/`、`Performance/`、`Interop/` 中发现的全部 C# 精确一致，未跟踪 manifest source 为 0。

负向测试临时加入 `Core/_ManifestNegativeProbe.cs`；构建以如下错误拒绝：

```text
Build source manifest mismatch. Unlisted C# files: Core/_ManifestNegativeProbe.cs
```

探针随后删除。未产生负向候选 EXE。

源码基线本地提交：`78a0722f8902`，提交信息 `Implement post-convergence hardening for 2.0.0.0`。没有 fetch、pull、push、release 或其他远端操作。

## 4. 最终 CLI 验收

以下结果均来自 `-RequireTrackedSources` 重新编译后的最终候选：

| 命令 | 退出码 | 用时 |
| --- | ---: | ---: |
| `--test` | 0 | 28.821 s |
| `--test-logger` | 0 | 0.749 s |
| `--test-layout` | 0 | 4.002 s |
| `--test-settings-bindings` | 0 | 27.348 s |
| `--test-display-recovery` | 0 | 1.187 s |
| `--test-operation-panel` | 0 | 0.743 s |
| `--test-settings-open-close --iterations 50` | 0 | 102.974 s |
| `--test-radar-display-lifecycle --iterations 100` | 0 | 8.441 s |

核心 `--test` 具名日志确认以下安全/生命周期模块通过：

- `BoundedHttpTextReader`
- `OwnerOperationGeneration`
- `CodexRadarUrlPolicy`
- `ClaudeCodeUsageReader` / Scheduler
- `CodexRadarForm.TileSnapshotFamily`
- `CodexQuotaGoalPlanner`
- `RadarRuntimeDiagnostics`
- `MetricTileModel` / Form / ExpandForm
- `WidgetForm.TileColumnRuntime`
- `TimingStats`

`WidgetForm.TileColumnRuntime` 不使用 `Application.DoEvents()`；它同步调用幂等生产启动路径并显式关闭，避免 timer 不断补充消息队列造成自测永久不返回。

## 5. 渲染验收

命令：

```powershell
DesktopCodexAssistant-arm64.exe --render-tilecolumn --out <renders>
DesktopCodexAssistant-arm64.exe --render-networkmonitor --out <renders>
DesktopCodexAssistant-arm64.exe --render-operation --out <renders>
DesktopCodexAssistant-arm64.exe --render-guard sample --out <renders>
```

结果：四项退出 0，共 35 张 PNG。逐文件解码与采样检查满足：

- 宽高均大于 0。
- 非透明采样数大于 0。
- 采样颜色至少 28 种，不是全透明、全黑、全白或单色。
- 十 tile 列完整；Network Dock、Codex IQ board、Operation/Launcher/Dock tabs、Guard 四态均有实际内容。
- 人工查看 `tilecolumn.png`、`networkmonitor-docked.png`、`operation-codex-iq-board.png`、`guard-armed.png` 未发现空白、裁切、错层或旧窗口回流。

## 6. 性能与并发证据

对最终候选的 `CodexRadarForm.BuildCodexIqBoardSnapshot` 预热 250 次后逐次测量 10,000 次：

| 指标 | 结果 |
| --- | ---: |
| P50 | 0.0222 ms |
| P95 | 0.0269 ms |
| P99 | 0.0521 ms |
| Mean | 0.024585 ms |
| Max | 5.36 ms |
| 托管内存前后差 | 176,968 bytes |

配套 fixture：

- 两个 producer 各发布 10,000 次 generation sentinel；consumer 至少 clone 10,000 次，跨代组合计数为 0。
- 连续 100 次 tile/IQ projection 的模型目录文件访问计数为 0。
- 阻塞 fake transport 在 Stop/Dispose 后释放，revision、文件 mtime、业务日志事件和 UI 回调计数不变。
- Radar 生命周期 100 次退出 0，无未观察异常。

正式运行观察 60 秒：PID 55588 持续响应，CPU 使用约为单核 8.1%（18 核整机约 0.45%），句柄 1010→1006，线程 34→34，工作集约 168 MiB，无单调资源增长证据。

## 7. 安全验收

### 7.1 SecretStore 与日志

fixture 覆盖新 envelope、旧 DPAPI、严格旧明文、随机 Base64、损坏 envelope、跨用户解密失败、文件锁、原子替换失败和 legacy 清理失败。失败密文字节/SHA256 不变。

Logger 临时持久化 fixture 验证 Authorization、Bearer、Cookie、access token、api_key、JWT、setup-token 和结构化 token 数组全部被脱敏；生产日志的同一 fixture 标记扫描 4 个活动日志文件，命中 0。

### 7.2 HTTP 与 SSRF

`BoundedHttpTextReader` fixture 覆盖无/伪造 Content-Length、chunked 慢流、压缩后超限、刚好上限、上限+1、deadline、取消、正常/错误 UTF-8。测试使用 fake response，不依赖公网。

`CodexRadarUrlPolicy` 覆盖精确同源 HTTPS、大小写/尾点、HTTP、userinfo、非 443、localhost、IPv4/IPv6 loopback、RFC1918、link-local、十进制/IPv4 映射、同源 redirect 到内网和超长 URL；拒绝项实际连接次数为 0。

### 7.3 认证数据与外部配置

- `settings.ini` SHA256：`CE9F4A2C71AB33BCBF27BED68A8395A5684683C50247B1BE86294EFB765EE056`，保持基线。
- `%USERPROFILE%/.codex/auth.json` SHA256：`C51DCF88382EAFF862B616437B2BF1C7352C675D637246A0BFB6A3389C8D12A9`，保持基线。
- `%USERPROFILE%/.claude/settings.json` SHA256：`663163918AD5A3B9919B2DB3C6D7114BEE6A7E6EAD646591FFA94F069FE2A683`，测试和部署后保持不变。
- `claude-code-oauth-token.bin` 部署前后均不存在。
- statusline bridge 在正式启动时从 v2 受控升级为 v3；当前 SHA256 `B46D7AF1DF3B439F88B8ADE80B205DF5BB46B5F11414D5856B208D3BD9A6306B`，逐字节等于候选内置生成内容。
- 旧 v2 bridge 已从备份 EXE 精确还原，SHA256 `FCA94238E8A7662DE64E6CBDE8DCC38FC415D1FCB402C923086B1B06C9E0DEE0`，与部署前记录一致。

### 7.4 Codex app-server 路径策略

fixture 覆盖：官方签名位置、受保护非官方位置、相对路径、当前目录、TEMP、可写位置、未知 ACL、无签名、错误发布者、reparse point、缺失文件、路径拼接参数及嵌入 NUL。非法路径统一失败关闭；嵌入 NUL 不再从 `Path.IsPathRooted` 抛出未处理异常。

## 8. 静态清理证据

以下生产 C# 固定字符串命中均为 0：

```text
QuickGrid
DrawCodexRadar
DrawPowerThermalWindow
DrawThreePaneContent
RadarBottomInfoTextRenderer
CodexRadarModelRatingsUrl
TryApplyCodexModelRatings
ModelRating
QuotaRadar / quota_radar / CodexQuotaRadar
SHAppBarMessage
DwmRegisterThumbnail
IShellLinkW
```

`CodexRadarForm` 与 `PowerThermalForm` 没有生产 `Show/ShowDialog` 调用，仅保留 headless owner 构造、启动、停止和 snapshot API。

## 9. 文档与索引 Gate

同步项：

- 根 `AGENTS.md`、`Docs/AGENTS.md`、README 和受影响活文档适用版本更新为 `2.0.0.0`。
- FEATURE INDEX 更新十 tile、Dock、Radar/Claude quota、网络、Power、死代码状态和 `codex_goal.quota_plan`。
- INTERFACE INDEX 新增 build manifest、bounded HTTP、URL policy、owner generation、published projection、Codex executable path policy；删除接口保留 ID 并标 `removed`。
- active feature 对 active interface/文件交叉引用问题为 0。

Gate：

```text
python Docs/validate_docs.py --root .  -> PASS（209 warnings，0 errors）
git diff --check                      -> exit 0
JSONL parse + unique ID               -> PASS
```

209 条 warning 来自既有历史 `change_type` vocabulary 和 9 个弱符号定位，不阻断项目的非 strict Gate；未回写历史行。

## 10. 正式部署

部署前：

- 根/Release 版本：`1.0.6.31`
- SHA256：`E4227C430EA6C7E0A5B19469E4F8F7ADC95120F258860D23BF35556FE0E23FCC`
- 正式备份：`E:/Codexproject/desktopdata/DesktopCodexAssistant-retained-build-backups/formal-backups/20260722-145723-pre-post-convergence-v1.0.6.31`

旧 PID 49036 在 75 秒内未处理停止事件。`ui-hang-watchdog.jsonl` 显示 UI 线程在 `widget.hover_tick:complete` 后持续无心跳，且进程仍写 cache；完成备份与路径核对后强制终止。

部署后：

| 项目 | 值 |
| --- | --- |
| 候选/root/Release 版本 | `2.0.0.0` |
| PE machine | `0xAA64` |
| 长度 | `2,138,112` bytes |
| 三者 SHA256 | `07647B84D55A8F5363E091835250EBB6BB28B1023AA9284D3A684275D4B1F460` |
| 新 PID | 55588 |
| 可执行路径 | `E:/Codexproject/desktopdata/DesktopCodexAssistant/DesktopCodexAssistant.exe` |
| Responding | True |
| 新 Fatal / error.log 变化 | 0 / 无变化 |
| 新 watchdog | 0 |

## 11. 异常与复验说明

首次运行最终 50 次 settings 压力测试时，外层 320 秒超时。诊断发现 PID 49564 是修复前从 `_build/post-convergence-a` 启动的旧 `--test`，已运行约 3 小时并累计 3536.86 秒 CPU；另一个本轮压力 PID 被拖慢。精确核对两个 `_build` 路径后终止测试实例，正式 PID 未动。清理环境后，同一最终候选 50 次在 102.974 秒内退出 0。

该事件同时验证了删除无界 `Application.DoEvents()` 的必要性；最终源码和最终进程不存在该残留测试路径。

## 12. 遗留风险

- 旧身份诊断 JSON 和退休缓存按 SPEC 不自动删除，仍占磁盘但新版本不再读写。
- 文档非 strict Gate 仍有 209 条历史 warning；属于后续文档治理工作，不影响 JSONL 正确性和当前实现定位。
- 回滚到 `1.0.6.31` 时应同时恢复双 EXE和备份的 v2 statusline bridge；不得把新 `dpapi-v1:` envelope 当作旧 plaintext。
- 本次只验证 ARM64；没有生成或测试 x64。

## 13. 最终判定

所有 SPEC 硬 Gate 均已获得实际输出；正式 ARM64 部署和运行观察通过。状态可从 `approved/pending` 更新为 `implemented/done`。
