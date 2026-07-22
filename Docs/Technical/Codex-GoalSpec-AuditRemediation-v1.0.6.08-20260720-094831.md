# GoalSpec：Audit Remediation 执行记录

生成时间：2026-07-20 09:48:31 +09:00  
执行模型：Codex  
实现版本：1.0.6.08  
状态：implemented / awaiting independent verification  

## Goal

执行 `Docs/Technical/Fable5-AuditRemediation-SPEC-v1.0.6.04-20260720-022259.md`，按 A-D 四批实现、CLI 验收、版本提升、备份、ARM64 正式覆盖和重启，并同步项目文档、索引、维护记录及 Spec Board 状态。

## SPEC 身份

- 路径：`Docs/Technical/Fable5-AuditRemediation-SPEC-v1.0.6.04-20260720-022259.md`
- SHA-256：`F5FEC16B9AAFFBF405F9D27D1B3680D13E34874DB1005A1D0773225B541BC013`
- 原始版本：1.0.6.04
- 最终版本：1.0.6.08

## 需求映射

| 需求 | 交付 |
| --- | --- |
| A1 | SecretStore 统一令牌入口、legacy 清除、污染 `.bin` 自愈与密文优先回归 |
| A2 | `SpecBoardPathPolicy` 统一项目根信任边界，打开、定位和删除路径全部复用 |
| A3 | `FatalRestartBudget` 跨进程状态与重启目标主模块身份双重校验 |
| A4 | CLI 可选值统一拒绝把下一 `--switch` 当参数值 |
| B1 | GUARD 运行态合并到已提交设置快照，不保存预览 |
| B2 | 四角色 tab/看板在挂起、全屏隐藏和恢复时遵守显示及资源生命周期 |
| B3 | `LeftDockLayout` 统一工作区、角色队列、缩放尺寸和视觉槽位迁移 |
| B4 | 设置缩放编辑器即时归一到 `-1` 或 `40..200` |
| C1 | Cloud/GFW/PathPing/FixedPing 使用单飞后消费和网络请求身份提交 |
| C2 | NCSI 4 KiB 上限与门户 HTTP/DNS TCP 整轮绝对 deadline |
| C3 | Claude Radar 复用共享交互 tick 维护鼠标穿透样式 |
| C4 | 有效密文与残留 legacy 并存时清理明文的直接 fixture |
| D1 | Spec Board 2 MiB/64 KiB/5000/64/512 上限、贯穿取消、后台管理窗加载和写前拒绝 |
| D2 | Logger 命名互斥、200 ms 降级、append-only fallback 与跨轮转共享 |
| D3 | 索引必填 Gate、entrypoint 警告、历史空版本回填和负向 fixture |

## 实现范围

核心改动集中在：

- 安全与恢复：`Core/SecretStore.cs`、`Core/SpecBoardPathPolicy.cs`、`Core/FatalRestartBudget.cs`、`DesktopCodexAssistant.cs`
- 停靠和设置一致性：`Core/LeftDockLayout.cs`、`Core/EdgeDockTabForm.cs`、四角色 owner、`Settings/WidgetSettings.cs`、`Settings/Win11SettingsForm.cs`
- 网络可靠性：`Performance/CloudEndpointProbeReader.cs`、`Performance/GfwProbeReader.cs`、`Performance/PathPingProbe.cs`、`Performance/FixedPingProbe.cs`、`Performance/NetworkMonitorReader.cs`、`Performance/PdhModels.cs`
- 窗口交互：`Core/ClaudeRadarForm.cs`
- Spec Board 长期边界：`Core/SpecBoardReader.cs`、`Core/SpecBoardForm.cs`、`Settings/SpecBoardManagerForm.cs`、`Core/SpecBoardLedgerStore.cs`
- 日志协调：`Core/Logger.cs`
- 文档治理：`Docs/validate_docs.py`、活文档、两个 JSONL 索引和 CHANGELOG

未新增应用 CLI 参数；只修正既有解析语义。未构建 x64，未提交或推送 GitHub。

## 架构流程

### Spec Board

`SpecBoardForm/Manager -> Task.Run -> SpecBoardReader.Read(token) -> bounded file/project/directory traversal -> complete SpecBoardSnapshot -> generation check -> UI apply`

紧凑窗口先读取基础账本；需要对账时创建 3 秒 linked token。超时后取消底层遍历并回退基础快照。Manager 的新加载取消旧 token，关闭时取消在途任务。LedgerStore 使用严格有界读取，先完成序列化大小校验，再更新 `.bak` 和原子替换正式账本。

### Logger

`Logger caller -> process SyncRoot -> named mutex (<=200 ms) -> size check -> rotate -> append -> release -> throttled directory cleanup`

互斥超时或不可用时走 `append-only + skip rotation + one process-local direct warning`，不递归调用 Logger。所有存储异常继续被抑制。

### 网络探针

网络 generation、接口、状态签名和目标签名组成请求身份。触发 token/trigger/force 只在成功取得单飞后消费；所有进度、样本、快照和完成日志在提交前复核身份。网络切换、离线、门控或配置变化使旧 epoch 失效。

## 关键接口与复用资源

复用并更新的定位包括：

- `spec_board.window`、`spec_board.manager_window`
- `network_monitor.loss_tolerant_probes`
- `claude_radar.window`
- `operation.left_edge_dock`
- `file_format.spec_board.ledger`、`file_format.spec_board.projects`、`file_format.spec_board.ledger_backup`
- `internal_api.spec_board.manager`、`internal_api.spec_board.path_policy`
- `internal_api.logger`、`file_format.runtime_logs`
- `internal_api.gfw_probe_reader`、`internal_api.cloud_endpoint_probe_reader`
- `internal_api.network_monitor.pathping_probe`、`internal_api.network_monitor.fixed_ping_probe`
- 新增 `command.docs.validation_gate`

窗口绘制继续复用 `LayeredWidgetFormBase`、`NativeMethods.LayeredBitmapSurface`、`DesignTokens`、`UiFontCache`、`BurnInProtection` 和共享交互 tick；未引入新 UI timer。

## 数据与配置

- SecretStore 使用 DPAPI CurrentUser；legacy 文件只用于迁移并在成功读取/清除后 best-effort 删除。
- `fatal-restart-state.json` 使用原子临时文件和替换/移动，损坏或不可写时按恢复优先策略 fail-open 并记录诊断。
- 左停靠设置迁移 Version 81 只在 Network 槽位仍为哨兵时复制 Spec Board 显式值。
- Spec Board 中央账本仍为外部开发环境资产；本程序只允许管理窗经 LedgerStore 写入。
- Logger 文件格式保持现有 UTF-8 文本兼容，没有格式迁移。

## 日志与错误处理

- 网络探针旧身份不得写快照或完成日志。
- Spec Board 超限只记录聚合计数，不写入被截断正文。
- Logger 的互斥、轮转、追加和目录清理异常不得向调用者传播。
- D2 降级告警直接随 fallback 追加，避免 Logger 递归。
- 文档 Gate 结构/语义失败返回 exit 1；entrypoint 文本定位只警告。

## 安全与兼容

- Spec Board 路径必须位于现有项目根内；拒绝 rooted、UNC、设备路径、遍历和重解析点逃逸。
- 只有文档白名单可直接 shell 打开；删除前再次验证路径并检查 Technical INDEX 引用。
- 崩溃重启只等待/终止主模块路径匹配的进程，降低 PID 复用误伤。
- 网络正文和 DNS TCP 读取均有大小或绝对时间边界。
- Logger 使用 `Local` 互斥，匹配当前单用户交互会话边界。
- 最终产物仅 ARM64，保持当前 WOA 正式运行目标。

## 验证证据

最终候选：

- 路径：`_build/audit-remediation-d-1.0.6.08/DesktopCodexAssistant-arm64.exe`
- FileVersion/ProductVersion：1.0.6.08
- 大小：2,375,680 bytes
- SHA-256：`93F6FBA7C2F8DC09F25E593A0EF986B268C62FA00D77D7C2741C529CAB3DD7F4`

七项顺序 CLI 自检全部 exit 0：

- `--test`：4,251 ms
- `--test-logger`：642 ms
- `--test-layout`：3,600 ms
- `--test-settings-bindings`：7,172 ms
- `--test-display-recovery`：403 ms
- `--test-specboard-manager`：1,960 ms
- `--test-operation-panel`：477 ms

总耗时 18,543 ms。附加 50 轮显示生命周期测试 2,595 ms，handles -1、GDI 0、USER -1。

四组离屏渲染全部 exit 0，共 32 张非空 PNG、1,162,691 bytes：Guard 5、Spec Board 3、Network 5、Claude Radar 19。

静态证据：SpecBoard reader 的无界整文件 API、`Directory.GetFiles` 和旧 3 秒同步等待均为 0 命中；Logger 命名互斥、200 ms 和 delete-sharing 均存在。`git diff --check` 无空白错误。

文档 Gate：当前仓库 exit 0、254 条非阻断警告；临时缺 `status` fixture exit 1 并精确检出。所有正式 JSONL 逐行可解析且 id 唯一。

四个 D/E 根与 Release 正式目标的版本、大小和 SHA-256 一致。最终正式 PID 33536 从 E 盘根程序运行且 `Responding=True`；正式重启后的 `error.log` 没有新增写入。

## SPEC 偏离

1. D3 是可放弃项，本次完整实施，没有放弃。
2. D 批代码曾与 C 批验收在共享工作区并行落地，因而 1.0.6.07 候选可能含部分 D 实现；D 的需求归属、文档、完整验收和正式部署严格落在 1.0.6.08，并在 CHANGELOG 明示。
3. 验收遵守 SPEC 的 CLI-only 边界；没有 GUI 自动化或截图目测。
4. 每批均在 10 分钟验收预算内完成，单项命令未超过 2 分钟。

## 限制与残余风险

- 网络共享上的单次文件系统 API 不能由 CancellationToken 强制打断，取消在返回后的下一检查点生效。
- Logger 超时降级不参与轮转协调，极端文件锁下该条 best-effort 日志仍可能丢失，但主程序不受影响。
- 管理窗用户写动作仍同步执行最多 2 MiB 的严格重读；SPEC 要求的快照 reader 已移出 UI 线程。
- 56 条 entrypoint 警告需要后续人工判断 partial class、继承或索引陈旧，不影响本次发布。
- 未执行 x64 构建、GitHub 推送和 GUI 人工验收。

## 完成状态

实现、文档、索引、维护记录、构建、CLI 验收、备份、四目标覆盖与正式重启均完成。对应实施 SPEC 已进入 `awaiting_verify`，等待独立验证后再推进 `done`；该 Goal 可标记完成。
