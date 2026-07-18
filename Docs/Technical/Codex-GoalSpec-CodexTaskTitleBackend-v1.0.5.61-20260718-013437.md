# Codex Task Title Backend GoalSpec

## 执行标识

- Goal：执行 `Fable5-CodexTaskTitleBackend-SPEC-v1.0.5.60-20260718-005852.md`，只为 Codex 任务监控后端接入官方会话标题，不改动任务看板绘制。
- SPEC 路径：`Docs/Technical/Fable5-CodexTaskTitleBackend-SPEC-v1.0.5.60-20260718-005852.md`
- 用户提供镜像路径：`D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant\Docs\Technical\Fable5-CodexTaskTitleBackend-SPEC-v1.0.5.60-20260718-005852.md`
- SPEC SHA-256：`ab513bd38e49acb8a81a0fc89e721109ded8ee002df5ca44fbf8b8e4dfdc62cc`
- 实现版本：`1.0.5.61`
- 当前正式版本：`1.0.5.63`（后续并行的 Claude 模型格宽度和操作按钮双击行为变更；已确认继续包含本 Goal 的标题后端）
- 执行模型：Codex
- 完成时间：2026-07-18T01:34:37+09:00

## 需求映射与实现

| SPEC 要求 | 实现与证据 |
| --- | --- |
| rollout 文件名 UUID 关联官方标题 | `CodexTaskMonitorReader.TryExtractSessionUuid` 只接受文件名末尾合法 D 格式 UUID，并在 `FileState` 创建时缓存。 |
| 复用既有刷新批次，不新增调度器 | `ProcessBatch` 在原有状态锁内、发布快照前调用 `RefreshSessionTitlesLocked`；源码审计确认未新增 timer、watcher 或线程。 |
| mtime 去抖和鲁棒读取 | 大小写不敏感映射按 mtime 刷新；重复 ID 后者覆盖，坏行隔离；文件缺失清空，瞬时 I/O/权限异常保留最近成功映射。 |
| 1 MiB 护栏 | 索引超过 1 MiB 时只读取尾部 1 MiB，丢弃首个残行；局部宽容 UTF-8 解码避免 seek 落入多字节字符中间。 |
| 冻结快照与诊断契约 | `CodexTaskSnapshot.Title` 为 null-safe 只读属性，`Clone`、`GetSignature`、发布 join 和 `--dump-codex-tasks` JSON 的 `title` 字段同步更新。 |
| 不修改前端 | 四个快照构造点只补充 title 参数；任务板绘制与布局未消费 `Title`，九张渲染样本逐像素零差异。 |

## 架构流程

现有 rollout 增量读取器在批次处理时获得每个文件缓存的会话 UUID；同一批次根据 `~/.codex/session_index.jsonl` 的修改时间决定是否重读标题索引，然后在发布冻结快照前执行 UUID join。标题变化进入既有签名，因此只产生正常的 `SnapshotChanged`，不会影响任务七态、token、终态静默或轮询节奏。

## 关键模块、接口与复用项

- 代码：`Core/CodexTaskMonitorReader.cs`、`Core/CodexTaskPresentation.cs`、`Core/OperationForm.CodexTasks.cs`。
- 功能索引：`codex_radar.task_monitor_backend`。
- 接口索引：`internal.codex_task_monitor_reader`、`event.codex_task_monitor_update`、`file_format.codex_session_index`、`resource.codex_sessions_directory`、`command.cli_test_and_dump`。
- 数据源：`%USERPROFILE%\.codex\session_index.jsonl`；未新增设置键、缓存文件、定时器、文件监听器或后台线程。

## 日志、错误、安全与兼容

- 单行 JSON 解析失败仅跳过该行；文件缺失使标题安全降级为空；非缺失类瞬时读取失败保留最近成功映射，避免抖动。
- 官方标题可能包含用户输入，按 SPEC 仅进入本地冻结快照和显式诊断 dump；不增加 rollout 路径或 UUID 的外泄。
- `session_index.jsonl` 是 Codex 管理的非稳定内部格式；格式变化不会破坏任务状态、token 或生命周期逻辑。
- mtime 未变化即不重读；外部写入者若修改内容但精确保留 mtime，需等后续 mtime 变化才会观察到，这是 SPEC 明确接受的边界。

## 验证证据

- ARM64 候选 `1.0.5.61` 构建成功；十项 WU-SPEC 测试矩阵全部退出 0。
- 任务监控自测通过 UUID 合法性、中文、坏行、重复 ID、mtime 去抖、缺失降级、1 MiB 尾读，以及标题更新恰好一次 `SnapshotChanged`。
- 真实 dump 与最新 rollout/index 精确关联，官方标题和 dump 标题均为“继续 Claude 任务”；所有任务对象均含 `title` 字段。
- `--render-operation` 的九张前后样本经阈值 0 比较，全部 `diff=0.000000`，结果 PASS。
- `1.0.5.61` 已备份旧正式版并部署到 E/D 根目录与 Release 四个正式路径；正式进程 PID 35988 连续监控 601 秒始终响应，`error.log` 长度保持 88733。
- 并行任务随后依次把四个正式路径升级到 `1.0.5.62` 和 `1.0.5.63`。当前 `1.0.5.63` 四路径 SHA-256 均为 `5CC8B97CCD3981F85450A16EC22A0068F1FEC1DFD8EA677ED255768C3EAAB60E`；真实 dump 的六个任务全部含 `title`，四个获得官方标题，“继续 Claude 任务”精确命中一个，PID 20800 正常响应。
- `error.log` 后续新增项是 `CodexTaskPresentation.RunSelfTest` 故意注入的 `provider failure`，不是标题后端运行时异常。

## 部署与偏离说明

- 部署前备份：`_build/formal-backups/20260718-011601-pre-1.0.5.61-codex-task-title-backend`。
- SPEC 基线版本 `1.0.5.60` 已被并行完成的任务看板冷色板占用，因此本独立变更按版本规则使用 `1.0.5.61`；功能范围未偏离。
- 第一次运行监控期间，异步设置测试重启了正式进程；该轮证据废弃，并从新 PID 35988 重新完成完整 601 秒窗口。
- 完成监控后，其他已验证交付把正式文件先后升级为 `1.0.5.62`、`1.0.5.63`。没有回退这些用户变更；每次均确认新版本保留标题契约，最终以当前 `1.0.5.63` 正式 dump 和运行进程为准。

## 结论

SPEC 的后端标题接入、冻结契约、诊断输出、测试、无 UI 变化、四路径部署和运行稳定性要求均已满足。Spec Board 状态应进入 `awaiting_verify`，等待用户或独立验收者确认后再标记 `done`。
