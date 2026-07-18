# Fable5-CodexTaskBubbleCardBoard-Spec — Codex 任务看板气泡卡片重设计与官方会话标题接入执行规格

生成时间：2026-07-18 00:55（Asia/Tokyo）。基线：`ProductIdentity.Version = 1.0.5.60`（任务看板已完成向 Spec 看板的冷色板对齐）。

用户已裁决（不得偏离、不得重新征询）：

1. 重设计对象是 **Codex 任务看板窗口本体**：以气泡卡片形态**替换**现有八列表格视图与窄窗紧凑卡片视图（"更换"，非并存第四视图）。
2. 每张卡片内容 = **官方会话标题 + 上下文水位环 + 状态时长与模型**。不含 token 细分（本轮/累计数字列随表格视图一并退役，聚合信息留 footer）。
3. 时间线视图保留为可切换的第二视图，footer 胶囊与切换语义不变。
4. 上游参考：LH-03/codex-monitor-hud（MIT，用户已确认参考许可）的 Split 气泡模式内容密度与 `session_index.jsonl` 标题源；不移植其独立气泡窗窗体形态。

## 1. 现状事实（2026-07-18 审查）

- 标题源：`%USERPROFILE%\.codex\session_index.jsonl` 每行 `{"id":"<会话uuid>","thread_name":"<官方标题>","updated_at":...}`（本机实测 335 行）；rollout 文件名尾部 36 字符即同一 uuid（`rollout-<ts>-<uuid>.jsonl`），可直接 join。`CodexTaskMonitorReader` 目前完全未读该文件，快照无 Title 字段。
- 看板视图：`OperationCodexTaskBoardForm` 三密度——`DrawTable`（八列表格，逻辑宽 ≥ 360）、`DrawCard`（窄窗紧凑卡）、`DrawTimeline`；`ActiveView` 在 `IsCompact` 或设置缺失时强制 `Table`。`CodexTaskBoardView { Table, Timeline }` 为持久化枚举（ini 字符串）。
- 配色已是共享冷色板 `DesignTokens.Colors.*`（1.0.5.60），本次沿用，不引入新色相。

## 2. 设计

### 2.1 Reader：官方标题接入（`Core/CodexTaskMonitorReader.cs`）

1. 从 rollout 文件名尾部解析会话 uuid（`rollout-*.jsonl` 扩展名前最后 36 字符，格式校验失败则视为无 uuid）。
2. 新增 session index 读取：`~/.codex/session_index.jsonl`，按 `LastWriteTimeUtc` 变化才全量重读（去抖进既有刷新 tick，不新增定时器/watcher）；单行解析失败跳过该行；文件缺失/不可读 → 空映射，功能降级。防膨胀护栏：文件 > 1 MB 时只读末尾 1 MB 内的完整行。
3. `CodexTaskSnapshot` 新增 `public string Title`（构造函数尾部追加参数，clone 链同步）；uuid 无映射时为空串。
4. 自测（进 `--test-codex-task-monitor`）：假 index 文件（含中文标题、坏行、重复 id 取后者）、文件名 uuid 提取、无 index 文件降级、mtime 未变不重读（读取计数断言）。

### 2.2 看板：气泡卡片视图（`Core/OperationForm.CodexTasks.cs` + `Core/CodexTaskPresentation.cs`）

1. `CodexTaskRowModel` 新增 `Title`；`BuildRows` 透传，空标题回退空串。
2. 新 `DrawBubbleCards`：响应式卡片流——卡片最小逻辑宽 280，按窗口宽自动 1–2 列（648 默认宽 = 2 列），行方向从上往下填充；最多显示 `MaximumVisibleRows` 计算的卡片数。**删除** `DrawTable`、旧 `DrawCard` 及其专属常量/列宽/表头绘制；`IsCompact` 概念退役（窄窗自然回落 1 列）。
3. 卡片结构（全部沿用现有冷色板与语义色，字体走 `GetCrispUiFont`）：
   - 卡片壳：`Surface` 圆角填充 + `Border` 描边；有待处理注意态时描边换该任务状态色。
   - 左上：状态点 + `#编号 工作区叶`（`Text` 色、粗体）。
   - 第二行：官方标题（`GlyphMuted`，省略号截断；空标题时该行显示 `—` 占位，卡高不变，保证网格稳定）。
   - 第三行：状态文本（状态色）+ 持续时长（沿用现有 `DetailText` 时长格式）+ 模型简称（`ShortenModel`，`GlyphMuted`）。
   - 右侧：上下文水位**环**（直径约 34 逻辑 px，`GetContextBarColor` 三档色阶，环心显示整数百分比；≥80% 时百分比转 `Danger` 红，复用现有 `ContextCritical` 语义）。
4. 视图枚举：`CodexTaskBoardView` 保持 `{ Table, Timeline }` 两成员与 ini 兼容——`Table` 值语义折算为气泡卡片视图（沿用 OperationRenderVariant 折算先例，枚举成员名不改）；`ActiveView`/`ToggleView`/footer 胶囊文字 `时间线`/`卡片` 相应更新（原"表格"字样退役）。
5. 时间线视图：泳道左标签仍 `#编号 工作区`（不塞标题，空间不够）；其余不动。
6. 点击契约不变：footer 胶囊保留动作，其余表面（含卡片本体）仍是空白关闭面；停靠、外部点击收起、夜间/勿扰、提醒分类逻辑零改动。
7. 渲染样张：`operation-codex-tasks.png`（648×400 双列卡片，含长中文标题截断样例与 80% 红水位样例）、`-timeline.png` 不变语义、`-compact.png` 改为 300×400 单列卡片样例。样张假数据补 `Title` 字段。

### 2.3 明确不做

token 细分展示、深链打开、独立气泡小窗、标题可见性开关、成本估算、MCP 注册表（均另行裁决）。

## 3. 验收（Gate）

- [ ] `Build-Arm64.ps1` 无警告；`--test-codex-task-monitor`（含 2.1.4 新断言）、`--test-operation-panel`、`--test-layout`、`--test-settings-bindings` 全绿。
- [ ] `grep -n "DrawTable\|TableWorkspaceMaxWidth\|CompactTableMinimumLogicalWidth" Core/OperationForm.CodexTasks.cs` 零命中（表格路径删净；时间线/卡片自身常量除外）。
- [ ] 三张样张目视验收：双列卡片、标题截断、水位环三档色、单列窄窗回落。
- [ ] `--dump-codex-tasks` 输出含 `title` 字段；本机真实 Codex 会话联调确认标题与 Codex 桌面端一致、无标题会话正常降级。
- [ ] 部署四正式路径并重启，运行 ≥ 10 分钟无 error.log 新增。
- [ ] 文档四步走：`Docs/CodexRadar-Architecture.md` §5.2 重写视图描述与标题源、INTERFACE_INDEX 登记 `~/.codex/session_index.jsonl` 新外部资源、CHANGELOG 一条、`Docs/validate_docs.py` 全绿、Spec Board 推 awaiting_verify。
