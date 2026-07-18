# Fable5-CodexTaskTitleBackend-Spec — Codex 官方会话标题接入 reader（仅后端）执行规格

生成时间：2026-07-18 00:58（Asia/Tokyo）。基线：`ProductIdentity.Version = 1.0.5.60`。
本 SPEC 取代 `Fable5-CodexTaskBubbleCardBoard-SPEC-v1.0.5.60-20260718-005510.md`（按用户裁决拆分：**本 SPEC 只做后端标题接入**；气泡卡片看板重设计属前端，方案另行批准后另立 SPEC，本 SPEC 不含任何 UI/绘制改动）。

上游参考：MIT 许可 LH-03/codex-monitor-hud 的 `session_index.jsonl` 标题源（用户已确认参考许可）。执行 AI 必读 WU-SPEC §0 的通用约束（构建/自检/文档 Gate/并发编辑规则）。

## 1. 数据契约（2026-07-18 本机实测）

- `%USERPROFILE%\.codex\session_index.jsonl`：每行独立 JSON `{"id":"<uuid>","thread_name":"<官方标题>","updated_at":"<ISO8601>"}`；实测 335 行，含中文标题。Codex 桌面端在会话命名/改名时追加或重写。
- rollout 文件名 `rollout-<时间戳>-<uuid>.jsonl`，扩展名前最后 36 字符即会话 uuid，与 index 的 `id` 精确对应（实测 `019f7074-805e-7f10-8db0-36087a3a6796` 两侧一致）。
- 该文件是 Codex 内部格式，无稳定性承诺：所有格式假设集中在 `CodexTaskMonitorReader`，解析失败一律降级为空标题，不崩、不影响既有七态与 token 解析。

## 2. 实现（全部在 `Core/CodexTaskMonitorReader.cs`）

1. **uuid 提取**：`internal static string TryExtractSessionUuid(string path)` 纯函数——取文件名去扩展名后的最后 36 字符，校验 `8-4-4-4-12` 十六进制段式；不合式返回空串。`FileState` 增加 `SessionUuid` 字段，在 `GetOrCreateStateLocked` 创建时填充一次。
2. **index 读取**：新增私有状态 `sessionTitles`（`Dictionary<string,string>`，OrdinalIgnoreCase）、`sessionIndexLastWriteUtc`、`sessionIndexPath`（默认 `%USERPROFILE%\.codex\session_index.jsonl`，`internal` 测试注入点覆盖）。在 `ProcessBatch` 的锁内、`PublishSnapshotLocked` 之前调用 `RefreshSessionTitlesLocked()`：
   - `File.GetLastWriteTimeUtc` 与缓存一致 → 直接返回（**不新增定时器/watcher，复用既有刷新批次**）；
   - 变化 → 全量重读建映射；重复 `id` 取文件中靠后者；单行解析失败跳过；
   - 文件缺失/IO 异常 → 清空或保留旧映射（缺失清空、异常保留），永不抛出；
   - 防膨胀护栏：文件 > 1 MB 时 seek 到末尾 1 MB 只读完整行（丢弃首个残行）。
3. **快照面**：`CodexTaskSnapshot` 构造函数尾部追加 `string title`，新增只读属性 `Title`（null → 空串）；`Clone`、`GetSignature`（追加 title，使标题晚到时能触发 `SnapshotChanged`）同步。`PublishSnapshotLocked` 按 `state.SessionUuid` 查映射，无命中传空串。
4. **诊断面**：`SerializeSnapshot`（`--dump-codex-tasks`）字典追加 `"title"`。
5. **调用点适配**：仓库内 `new CodexTaskSnapshot(` 共 4 处（reader Clone/Publish、`CodexTaskPresentation.cs:646` 附近、`OperationForm.CodexTasks.cs:311` 附近的样张假数据），后两处**仅追加参数**（样张可给代表性中文标题），不做任何其他前端改动。
6. **对前端的冻结契约**：`Title` 为官方会话标题，空串 = 无映射/未命名/降级；前端 SPEC 直接引用本节，不触碰解析内部。

## 3. 自测（扩展 `RunSelfTest`，随 `--test-codex-task-monitor`）

- `TryExtractSessionUuid`：合式 rollout 名提取正确；短名/非 hex/无 uuid 名返回空串。
- index 解析：中文标题、坏行跳过、重复 id 取后者。
- 集成：临时目录写 `session_index.jsonl` + 带 uuid 的 rollout 文件，注入测试路径,断言快照 `Title` 命中;无 index 文件 → 空标题降级;mtime 未变不重读(重读计数断言);标题变化触发一次 `SnapshotChanged`。

## 4. 验收（Gate）

- [ ] `Build-Arm64.ps1` 无警告；`--test-codex-task-monitor`（含 §3 全部新断言）、`--test`、`--test-settings-bindings` 全绿。
- [ ] `--dump-codex-tasks` 输出含 `title`；本机真实 Codex 会话联调：标题与 Codex 桌面端一致、无标题会话空串降级。
- [ ] 渲染零回归：`--render-operation` 与部署前基线对比，样张仅允许因假数据补 `Title` 而**不变**（本 SPEC 无 UI 改动，任何像素差异即 FAIL）。
- [ ] 无新增定时器/watcher/线程（代码评审断言：只在既有 `ProcessBatch` 批次内检查 mtime）。
- [ ] 部署四正式路径并重启，≥ 10 分钟无 error.log 新增。
- [ ] 文档：`Docs/CodexRadar-Architecture.md` 数据面增补标题源一句、INTERFACE_INDEX 登记外部资源 `~/.codex/session_index.jsonl`、CHANGELOG 一条、`Docs/validate_docs.py` 全绿、Spec Board 推 awaiting_verify。
