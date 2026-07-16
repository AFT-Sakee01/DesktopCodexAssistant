# Spec Board Manager And Freshness SPEC

## Metadata

- Document: `Fable5-SpecBoardManagerAndFreshness-SPEC-v1.0.5.11-20260712-013045.md`
- Generated model: Claude Fable 5
- Timestamp local: `2026-07-12T01:30:45+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.11`
- Target implementation version: `1.0.5.12+`（执行 AI 按当时版本递增）
- Status: draft（用户已拍板三项范围决策，见下；正文待用户确认后改 approved）
- Ledger registration: 待登记（本 spec 完成撰写后用 `spec-board` skill 登记为 `pending`）
- Depends on: `Docs/Technical/Fable5-SpecBoardWindow-SPEC-v1.0.5.07-20260711-202714.md`（已 implemented，1.0.5.08）；本 spec 只增量扩展，不重开已验收的 §1–§4。

## 用户拍板的三项范围决策

1. 管理窗口布局：**B 主从双栏（列表+详情面板）为主体，吸收 E 的危险区隔离**——日常操作（改状态/写备注/打开文件）在详情面板；`删除账本条目`与`删除条目并删除源文件`两个不可逆操作摘到独立的二次确认危险区，不与安全按钮混排。
2. "删减文件"语义：**两者都要，各自独立按钮**——"删除账本条目"（只动账本行）与"删除账本条目并删除源文件"（连项目里的 spec `.md` 一起处理）是两个独立按钮，确认强度不同。
3. **新增 `needs_revision`（需要修改）为正式账本状态**，状态机从 4 态扩到 5 态；须同步更新 `spec-board` skill 契约、`SpecBoardReader` 解析、主看板配色与分段显示。

## Goal

在已实施的 Spec Board 看板窗口基础上新增两块能力：

1. **新鲜度标记**：左项目栏能一眼看出"哪个项目有新 spec 事件"，与既有红/黄"欠账"计数正交，不互相覆盖。
2. **Spec 管理窗口**：主看板左下角新增入口按钮，打开一个原生 WinForms 管理窗口，可对任意 spec **强制改状态（含新增的 needs_revision）、写/改备注、删除账本条目、连源文件一起删除**，覆盖主看板"只读"边界——这是本项目里唯一允许绕过 `spec-board` skill 直接写账本的路径，因此必须自带并发安全和审计留痕。

## 0. 既有资源（执行前必读）

| 资源 | 位置 | 本 spec 的用法 |
|---|---|---|
| 主看板窗口 | `Core/SpecBoardForm.cs`（1274 行）、`Core/OperationForm.SpecBoard.cs`、`Core/SpecBoardReader.cs`（438 行） | 在此基础上扩展，不重写既有渲染/交互 |
| 账本状态词表现状 | `SpecBoardReader.cs:73` `AllowedStatuses = { "pending", "awaiting_verify", "done", "abandoned" }` | 扩为 5 值，见 §1 |
| 账本行模型 | `SpecBoardReader.cs:8-39` `SpecBoardRow`（`Id`/`Project`/`SpecPath`/`Title`/`Status`/`EventTimeUtc`/`FileMissing`/`IsUnregistered`/`ProjectRoot`/`AbsolutePath`） | 新增字段见 §1.2 |
| 快照模型 | `SpecBoardReader.cs:50-69` `SpecBoardSnapshot` | 无需破坏性改动，新增字段做加法 |
| 项目栏渲染 | `SpecBoardForm.cs:398` `DrawProjectRail` | 加新鲜度圆点，见 §2 |
| 行动流渲染 | `SpecBoardForm.cs:470` `DrawActionFlow` / `:497` `DrawSection` | 加第 4 段"需要修改"，见 §1.3 |
| 卡片点击现状 | `SpecBoardForm.cs` 单击延迟复制路径 / 双击打开文件（`cardSingleClickTimer`、`pendingCardSingleClick`、`suppressedCardMouseUpPath`） | 不变；管理入口是新按钮，不复用卡片点击 |
| 设置模式 | `Settings/WidgetSettings.cs`（`SpecBoard*` 键簇，约 244-251/455-460/712-717/927-932/1176-1181/1417-1422/1638-1643/2334-2339/2893-2925/5482-5551/5992-6000） | 新键全链路照此模式扩展 |
| 账本写入契约 | `spec-board` skill（junction：`C:\Users\GengH\.claude\skills\spec-board\` ≡ `C:\Users\GengH\.codex\skills\spec-board\`，编辑任一路径即生效） | 状态机文字与示例同步扩到 5 态，见 §1.4 |
| 原生设置窗口先例 | `Settings/Win11SettingsForm.cs`（`SettingRow`、深色卡片风格、非分层原生 `Form`） | 管理窗口的窗体/控件风格参照，不复用其页面逻辑 |
| 存储边界 | `Docs/SpecBoard-Architecture.md` §存储边界 | 新鲜度本地缓存和账本备份文件的落位依据，见 §2.1、§4.4 |
| 布局测量规则 | 根 `AGENTS.md`：禁止手猜像素 Y | 管理窗口原生控件用 WinForms 布局容器/锚定，不手写像素坐标 |

## 1. 状态机扩展：`needs_revision`

### 1.1 状态机

```
pending（未执行）
  → awaiting_verify（执行完，待验证）
      → done（验证完成）
      → needs_revision（验证发现 spec 本身有问题，需要修改）
  → needs_revision（执行前发现 spec 有问题，需要修改）← 新路径
needs_revision → pending（改完，重新排队等执行）← 新路径
任意阶段（除 done）→ abandoned（放弃）
```

`done` 是终态，不可再转移（与既有规则一致）。`abandoned` 也是终态。

### 1.2 `SpecBoardRow` 新增字段

- `RevisionRequestedUtc` / 对应 `EventTimeUtc` 取值：状态为 `needs_revision` 时，`EventTimeUtc` 取账本行的 `revision_requested_utc`（新可选字段），缺失时回退 `updated_utc`。
- `UpdatedUtc`（新增，`DateTime?`）：解析账本行 `updated_utc`，供 §2 新鲜度判定使用。此前 reader 未消费该字段，本 spec 起必须解析。
- `AllowedStatuses` 扩为 `{ "pending", "awaiting_verify", "done", "abandoned", "needs_revision" }`；未知值仍按坏行跳过、计入 `MalformedLines`（不变）。

### 1.3 主看板显示

`DrawActionFlow` 新增第 4 段，顺序改为：**未登记（橙）→ 需要执行（红）→ 需要修改（紫，`DesignTokens.Colors.AccentAlt`）→ 等待验证（黄）**。放在"需要执行"之后、"等待验证"之前——needs_revision 通常是执行前被打回，语义上仍属"需要执行方向的行动"，与 pending 相邻更符合阅读顺序。段内排序规则、`+N 更多` 溢出规则与既有三段一致（`DrawSection` 直接复用，只新增一次调用）。

左项目栏计数：现状"红=pending+unregistered，黄=awaiting_verify，全零显示绿勾"扩为三色计数——红=pending+unregistered，紫=needs_revision，黄=awaiting_verify；全零（含 needs_revision）才显示绿勾。

### 1.4 `spec-board` skill 契约同步

编辑 `spec-board` skill（两个路径互为同一文件，改一处即可）：

- 状态机图更新为 §1.1 的 5 态图。
- 行 schema 说明新增可选字段：`revision_requested_utc` / `revision_requested_local`。
- 新增一条示例："验证 AI 发现 spec 本身有缺陷（而非实施有误）时，把 `status` 改 `needs_revision`，`note` 写清楚问题，追加 `revision_requested_utc`/`_local`；spec 作者修好后改回 `pending` 并刷新 `updated_*`。"
- 明确 needs_revision 通常经由**管理窗口**人工设置，但 skill 驱动的 AI 会话同样有权直接写此状态（例如验证 AI 判断 spec 描述有误）——两条路径都合法，不互斥。

## 2. 新鲜度标记（左项目栏）

### 2.1 本地"已读"缓存

新建 `%LOCALAPPDATA%\DesktopCodexAssistant\SpecBoardSeenState.json`：`{ "<project>": "<lastSeenUtc ISO8601>", ... }`。这是本程序自身的运行态 UI 状态（不是 `_spec_board` 账本数据），落位遵循既有 `%LOCALAPPDATA%\DesktopCodexAssistant` 不变量，不违反 `Docs/SpecBoard-Architecture.md` 的账本只读边界。

- 首次运行（缓存文件不存在）：为快照里出现的每个项目写入 `lastSeen = 本次 ScanTimeUtc`，视为"现状即基线"，**不**在首次启动就把所有项目标成新——否则每次全新安装都会满屏紫点，没有信噪比。
- 读取失败（损坏 JSON）：按"无基线"处理，等同首次运行重新播种，不抛异常、不刷屏日志。
- 写入时机：项目行在左栏被单击选中（过滤到该项目）时，把该项目 `lastSeen` 更新为**当次快照的 `ScanTimeUtc`**（不是 `DateTime.UtcNow`，避免把"刚点开当下、reader 还没来得及看到的行"误判为已读）并立即持久化。点击"全部"不清任何项目的标记。

### 2.2 判定与渲染

- 项目新鲜度 = 该项目全部账本行 `UpdatedUtc` 的最大值（`unregistered` 合成行没有 `UpdatedUtc`，不参与）。
- `freshness > lastSeen[project]` → `DrawProjectRail` 在该行项目名左侧画一个 6px 实心圆点，颜色取 `DesignTokens.Colors.Accent`（青色，未被状态三色占用，视觉上明确是"独立于红黄紫的另一种信号"）。
- 项目从未出现过（新项目、缓存里没有条目）且不是首次运行播种的那一批 → 视为 `lastSeen = DateTime.MinValue`，天然带新鲜点，符合直觉（新项目第一次出现 spec 当然算新）。

## 3. Spec 管理窗口

### 3.1 入口

`SpecBoardForm` 现有 footer 区域左下角新增一个小图标按钮（复用现有 footer 的字体测量式布局，不手写像素）。单击打开（或激活已存在的）`SpecBoardManagerForm`；再次单击不关闭（管理窗口有自己的关闭按钮），避免和主看板的双击呼出/收回手势混淆。打开管理窗口不影响主看板的自动收回计时——两者是独立窗口，管理窗口打开期间主看板仍可能因超时收回，管理窗口不随之关闭。

### 3.2 窗体架构

新建 `Settings/SpecBoardManagerForm.cs`：原生 `System.Windows.Forms.Form`（非 `LayeredWidgetFormBase`——管理窗口需要真实文本框、下拉框、可聚焦控件，不适合分层位图渲染路径）。窗体外观参照 `Win11SettingsForm` 的深色卡片风格（背景色、圆角面板、`SettingRow` 视觉语言可直接复用其构造模式，但不复用其页面路由逻辑）。`ShowInTaskbar = true`（区别于分层挂件窗口，这是用户主动打开的工具窗口，符合 `Win11SettingsForm` 先例）。非模态：允许同时操作主看板。关闭时不退出程序，仅隐藏/释放窗体资源。

### 3.3 布局（B 主从双栏 + E 危险区隔离）

```
┌─────────────────────────────────────────────────────────┐
│ 工具栏：[搜索______] [项目 ▾] [状态 ▾] [批量登记未登记项]     │
├───────────────────┬─────────────────────────────────────┤
│ 左：spec 列表       │ 右：详情面板                          │
│ (状态点+标题+项目)  │  标题 / 项目 / 路径(+存在性)            │
│ 支持 Ctrl/Shift 多选│  状态: [下拉 5 值] [应用]               │
│                    │  备注: [多行文本框] [保存]              │
│                    │  时间线: registered/executed/verified/ │
│                    │          revision/abandoned（有则显示） │
│                    │  [打开文件] [在资源管理器中显示]         │
│                    │  ──────────────────────────────────  │
│                    │  ▾ 危险操作（默认折叠，红色描边）        │
│                    │    [删除账本条目]                      │
│                    │    确认文本框(需输入文件名匹配)          │
│                    │    [删除账本条目并删除源文件]           │
├───────────────────┴─────────────────────────────────────┤
│ 多选时状态条：已选 N 项 [批量改状态 ▾] [批量删除条目]         │
└─────────────────────────────────────────────────────────┘
```

- **左侧列表**：一行一 spec，含 `unregistered`/`done`/`abandoned`（主看板不展示的历史状态在这里也能看到——这是本窗口区别于只读主看板的核心价值：管理全集，不只是行动流）。排序下拉：状态优先级 / 项目 / 最近更新。搜索框按标题/项目/路径子串过滤。
- **右侧详情面板**：选中单行时填充。`unregistered` 行没有状态下拉，改成一个"登记为未执行"按钮（写入新账本行，`status=pending`，`updated_by="User (SpecBoardManager)"`）。状态下拉 5 值：`未执行`/`需要修改`/`待验证`/`完成`/`中断`，选择后必须点"应用"才提交（防止误触下拉直接改状态）。
- **多选批量条**：仅在左侧多选 ≥2 行时出现，只暴露"批量改状态"（对全部选中行应用同一目标状态）与"批量删除条目"（仅删账本行，不含文件——批量场景下连带删源文件风险过高，不提供）。
- **危险区**：默认折叠，标题带红色描边和警告底色，与详情面板其余安全操作用一条分隔线物理隔开。见 §3.4。

### 3.4 删除语义（两个独立按钮）

1. **删除账本条目**：只从 `SPEC_BOARD.jsonl` 移除该行，磁盘上的 spec `.md` 文件不受影响。确认方式：标准 Yes/No 确认对话框（内容含 spec 标题），无需输入匹配文本——因为这个操作不损失源码内容，随时可以用 skill 重新登记。
2. **删除账本条目并删除源文件**：额外要求——
   - 危险区内有一个文本框，用户必须输入与该 spec **文件名完全一致**的字符串（区分大小写），按钮才从禁用变可用；防止"鼠标一滑就删源文件"。
   - 执行删除前检查：该项目自己的 `Docs/Technical/INDEX.jsonl` 是否仍引用这个 `doc_path`。若引用仍存在，弹阻断提示"该文件仍被 <project> 的 INDEX.jsonl 引用，请先在该项目里按其 `Docs/AGENTS.md` 规则清理索引，或点『我知道，仍要删除』强制继续"——不静默留下悬空索引行（管理窗口不代管其他项目的文档治理，只做提醒，不擅自跨项目改索引）。
   - 物理文件删除走 Windows 回收站（`SHFileOperation` / `Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`），**不做永久删除**——误删可从回收站找回，这是比自建归档目录更省心也更安全的兜底。
   - 账本行与物理文件的删除在同一次用户确认后连续执行；文件删除失败（占用/权限）时账本行**不删除**（先删文件、成功后再摘账本行，保证两者要么都成、要么都不动，不留半写状态）。

### 3.5 并发安全与审计（管理窗口写账本的硬性前提）

管理窗口是本项目里唯一绕过 `spec-board` skill 直接改账本的路径，必须比 skill 更严格：

- 任何"应用"/"删除"点击时，**先重新从磁盘整份重读账本**（不信任窗口打开时缓存的快照——此刻可能有别的 AI 会话正通过 skill 改同一账本），按 `id` 定位目标行；找不到（已被别处删除/改动）→ 弹冲突提示并刷新列表，拒绝本次写入，不覆盖别人的修改。
- 写入前把当前账本文件整份复制为 `SPEC_BOARD.jsonl.bak`（单份滚动备份，不是版本历史，只做"手滑改错了能一键找回上一版"的安全网）。
- 每次写入都刷新 `updated_utc`/`updated_local`，`updated_by` 固定写 `"User (SpecBoardManager)"`——让之后任何读账本的 AI 会话一眼看出这行是人工在管理窗口改的，不是 skill 走状态机走出来的。
- 写入用临时文件 + 原子替换（写 `.tmp` 成功后 `File.Replace`），避免主看板的 `FileSystemWatcher` 在写入过程中读到半份 JSON 触发误报坏行。
- 备注编辑同样遵循"重读-定位-写入"三步，即使只改 `note` 不改 `status`。

### 3.6 与主看板的联动

管理窗口写账本后，主看板既有的 `FileSystemWatcher` + 500ms 防抖会照常拿到变更（无需新增专门通知通道）；若主看板此刻隐藏，下次呼出时的常规刷新会读到最新状态。§2 的新鲜度判定基于 `UpdatedUtc`，管理窗口的人工修改同样会点亮项目新鲜度圆点——这是合理行为：无论是 AI 执行完还是人工在管理窗口强制改的，对"这个项目最近发生了状态变化"这件事本身都成立。

## 4. 设置新增

| 键 | 类型 | 默认 | 归一化 | 说明 |
|---|---|---|---|---|
| `SpecBoardManagerWidth` | int | 720 | 钳 [560, 1000] | 管理窗口默认宽 |
| `SpecBoardManagerHeight` | int | 520 | 钳 [400, 900] | 管理窗口默认高 |
| `SpecBoardManagerDangerZoneRequiresTypedConfirm` | bool | true | — | 关闭后"删除条目并删除源文件"退化为普通确认框（供用户自行承担风险时提速）；默认必须开 |

全链路要求同 §0 表格所述模式：defaults / clone / load / save / normalize / 设置页 UI（放在操作面板设置区，紧邻既有 `SpecBoardAutoHideSeconds` 一栏） / 迁移版本 / `--test-settings-bindings` 断言全部覆盖，不新增例外。

新鲜度缓存文件路径不建键——固定 `%LOCALAPPDATA%\DesktopCodexAssistant\SpecBoardSeenState.json`，理由同既有运行态文件（不需要用户可配置，配置反而增加误用面）。

## 5. 文档与索引任务

1. `Docs/SpecBoard-Architecture.md`：新增"管理窗口"小节（架构、并发安全三步、删除语义、危险区规则）；"新鲜度"小节（本地缓存路径、首次运行播种规则、判定公式）；"存储边界"小节补一句 `SpecBoardSeenState.json` 与 `.bak` 备份同属程序自身运行态数据。适用版本号刷新。
2. `Docs/Indexes/FEATURE_INDEX.jsonl`：新增 `spec_board.manager_window`、`spec_board.freshness_indicator` 两行；`spec_board.window` 行的 `aliases` 补充"needs_revision/需要修改/spec管理/强制完成/删除spec"等检索词。
3. `Docs/Interfaces/INTERFACE_INDEX.jsonl`：新增 `file_format.spec_board.seen_state`（`SpecBoardSeenState.json`，direction=both）、`file_format.spec_board.ledger_backup`（`.bak`，direction=provide）；既有账本接口行补充新字段说明。
4. `Docs/Component-Refresh-Rules.md`：管理窗口不引入新定时器/轮询（纯用户触发写入），仅需一句说明"管理窗口写入后复用主看板既有 watcher 链路，不新增刷新机制"。
5. `spec-board` skill：按 §1.4 同步 5 态状态机、新字段、needs_revision 使用场景。
6. `CHANGELOG.jsonl`：实施完成后按规约追加变更/部署记录；本 spec 的 INDEX 行回填 `implemented`。
7. 执行完成后用 `spec-board` skill 把本 spec 自己的账本行推进 `awaiting_verify`；验证通过后推 `done`。

## 6. 验收条件

### G1 状态机与解析
`SpecBoardReader` 单元自测（沿用既有 `RunSpecBoardReaderSelfTest` 风格）覆盖：`needs_revision` 行正确解析、`UpdatedUtc` 字段解析、坏状态值仍归为 `MalformedLines`、`revision_requested_utc` 缺失时 `EventTimeUtc` 回退 `updated_utc`。

### G2 主看板显示
`--render-specboard sample` 新 fixture 含至少一条 `needs_revision` 行：紫色段在"需要执行"和"等待验证"之间正确出现；左栏三色计数（红/紫/黄）与 fixture 数字一致；4 个 OLED 变体紫色替换色无蓝色残留。

### G3 新鲜度
干净环境首次启动 → 无论账本有多少行，左栏无青色点（播种验证）；随后账本某项目新增一行或改一行的 `updated_utc` → 重新呼出主看板，该项目青点出现；单击该项目行过滤 → 青点消失且 `SpecBoardSeenState.json` 落盘对应项目时间戳更新；点击"全部"不清除其他项目青点。损坏 `SpecBoardSeenState.json` → 不抛异常，等同重新播种。

### G4 管理窗口打开与布局
footer 新按钮可点击并打开 `SpecBoardManagerForm`；关闭主看板不影响已打开的管理窗口；管理窗口关闭不影响主看板；窗体尺寸设置项生效（改 `SpecBoardManagerWidth/Height` 后重开窗口应用新尺寸）。

### G5 详情面板读写
选中一行 `pending` spec → 状态下拉默认显示"未执行"；切到"需要修改"点应用 → 账本行 `status` 变 `needs_revision`、`updated_by="User (SpecBoardManager)"`、`updated_utc` 刷新；备注框修改并保存 → 账本 `note` 字段更新，其余字段不受影响；选中 `unregistered` 行只出现"登记为未执行"按钮，点击后账本新增一行 `pending`。

### G6 并发安全
测试步骤：管理窗口打开且选中某行 → 用 `spec-board` skill 在另一进程/终端把同一行状态改掉 → 回到管理窗口点"应用" → 必须弹冲突提示、不覆盖 skill 刚写的值、列表自动刷新为最新状态。验证 `SPEC_BOARD.jsonl.bak` 在每次成功写入前生成且内容为写入前版本。验证写入过程中主看板 `FileSystemWatcher` 不产生 `MalformedLines` 误报（原子替换生效）。

### G7 删除语义
"删除账本条目"：确认后该行从账本消失，磁盘 `.md` 文件仍存在。"删除条目并删除源文件"：确认文本框内容不匹配文件名时按钮保持禁用；输入匹配后可点击；若该项目 INDEX.jsonl 仍引用该路径 → 先弹阻断提示，点"我知道，仍要删除"后才继续；执行后文件出现在 Windows 回收站（非永久删除），账本行同步消失；模拟文件删除失败（占用文件句柄）场景 → 账本行不应被删除，弹出失败提示。

### G8 批量操作
左侧多选 3 行（跨项目、跨状态）→ 底部批量条出现；"批量改状态"应用统一目标值后 3 行账本同步；"批量删除条目"确认后 3 行从账本消失、磁盘文件均不受影响。

### G9 设置绑定
`--test-settings-bindings` 覆盖新增 3 个键的默认值/clone/save-load round-trip/归一化钳制断言全部通过。

### G10 文档与部署
`Docs/AGENTS.md` §8 校验 Gate 全绿；触发表核对（FEATURE/INTERFACE/Component-Refresh-Rules/SpecBoard-Architecture/skill 全部同步）；本 spec INDEX 行回填 `implemented` 并记 `spec_sha256`；ARM64 构建零警告，备份现有正式 exe → 覆盖 → 重启，重启后版本号一致，管理窗口在正式 exe 中可正常打开并完成一次真实的状态强制修改。

## 7. 风险与回滚

- **needs_revision 是破坏性 schema 变更**：任何仍按 4 态词表硬编码的既有代码路径（若有遗漏）会把该状态误判为坏行——G1 必须覆盖全部既有 `AllowedStatuses` 消费点，执行 AI 需先搜索全仓库对该常量和状态字符串的引用，不能只改定义处。
- **管理窗口误删风险**：靠"回收站而非永久删除"+"输入文件名确认"+"INDEX 引用阻断"三层兜底；回滚路径 = 从回收站还原文件 + 用 `SPEC_BOARD.jsonl.bak` 覆盖账本。
- **并发写冲突**：管理窗口"重读-定位-写入"三步 + 原子替换是唯一防线；不引入文件锁（本地单机低频操作，锁的复杂度不值得）。
- **新鲜度噪音**：首次运行播种规则专门防止"升级到新版本当天满屏新点"；若仍观察到误报，回滚点是把 §2.1 播种逻辑改为"首次运行不显示任何点"的更保守版本。
