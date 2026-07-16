# GoalSpec：Spec Board Manager And Freshness

## 执行标识

- Goal：执行 `Fable5-SpecBoardManagerAndFreshness-SPEC-v1.0.5.11-20260712-013045.md`，完成实现、验收、文档与 ARM64 正式部署。
- SPEC：`Docs/Technical/Fable5-SpecBoardManagerAndFreshness-SPEC-v1.0.5.11-20260712-013045.md`
- SPEC SHA-256：`FB8209E091C66336EA0F6865BD89785125C5F5A08B03F7A5BB987F0D393931FB`
- 实施版本：`1.0.5.15`
- 完成时间：`2026-07-12T02:44:47.298+09:00`（Asia/Tokyo） / `2026-07-11T17:44:47.298Z`

## 需求映射与实现

| 验收项 | 实现与证据 |
| --- | --- |
| G1 状态机与解析 | 新增 `SpecBoardStatus` 五态单一词表；reader 解析 `needs_revision`、`updated_utc`、备注、操作者与各状态时间；非法状态仍计入 malformed。 |
| G2 主看板显示 | 行动流按未登记、需要执行、需要修改、等待验证分区；新增紫色修改态、独立计数、动态压缩和溢出计数；单击复制绝对路径并显示窗口内成功提示，双击才打开文件。 |
| G3 新鲜度 | `SpecBoardSeenStateStore` 在 `%LOCALAPPDATA%/DesktopCodexAssistant/SpecBoardSeenState.json` 保存项目已读基线；缺失/损坏时按扫描时间播种；具体项目点击标已读，全部项目不清除。 |
| G4 管理窗口 | 主看板左下角“管理”打开独立原生 WinForms 主从双栏；支持搜索、项目/状态筛选、排序、详情与折叠危险区；看板隐藏不关闭管理窗。 |
| G5 详情读写 | 支持五态强制修改、备注保存、未登记条目登记、打开/定位文件；所有程序侧账本写入集中到 `SpecBoardLedgerStore`。 |
| G6 并发安全 | 写前完整重读，以 id 和预期 `updated_utc` 检测冲突；写前生成 `.bak`，同目录 `.tmp` 后 `File.Replace`；watcher 验收确认原子写不产生 malformed 瞬态。 |
| G7 删除语义 | 账本条目删除与“条目+源文件”删除分离；源文件删除默认要求大小写完全一致的文件名、检查 Technical INDEX 引用并再次确认，通过 Shell 回收站；失败保留账本行。 |
| G8 批量操作 | 多选支持跨项目/跨状态批量改态和仅删除账本行；不提供批量源文件删除。 |
| G9 设置绑定 | 设置 schema 66 新增管理窗宽 `720`、高 `520`、危险区输入确认 `true`，含持久化、迁移、范围和设置页绑定测试。 |
| G10 文档与部署 | 架构、刷新规则、README、功能索引、接口索引、skill 契约、维护日志及本 GoalSpec 已同步；ARM64 正式三路径部署并启动。 |

## 架构与关键边界

主看板和 `SpecBoardReader` 保持只读；交互式写入仅允许 `SpecBoardManagerForm → SpecBoardLedgerStore → SPEC_BOARD.jsonl`。AI 会话仍通过 `spec-board` skill 写入。管理写采用“重读—定位—冲突校验—备份—临时文件—原子替换”，避免覆盖外部 AI 刚写入的状态并避免 watcher 读到半文件。

新鲜度缓存只保存本机 UI 已读状态，不进入项目配置；判定使用项目内非合成行的最大 `UpdatedUtc`。删除源文件使用 Windows Shell 回收站能力，不执行永久删除。

## 复用的索引与接口

- 功能：`spec_board.window`、`spec_board.manager_window`、`spec_board.freshness_indicator`。
- 接口：`file_format.spec_board.ledger`、`file_format.spec_board.seen_state`、`file_format.spec_board.ledger_backup`、`internal_api.spec_board.manager`、`command.spec_board.manager_test`。
- 既有资源：`LayeredWidgetFormBase`、`WidgetSettings`、现有 FileSystemWatcher/500 ms debounce、Windows Shell 回收站。

## 验证证据

- ARM64 构建：`Build-Arm64.ps1 -OutputPath _build/DesktopCodexAssistant-arm64-spec-manager-v1.0.5.15.exe`，成功，无编译错误。
- 自动化：`--test`、`--test-settings`、`--test-layout`、`--test-operation-display`、`--test-logger`、`--test-display-recovery`、`--test-radar-display-lifecycle --iterations 100`、`--test-specboard-manager`、`--test-settings-bindings` 均退出 0。
- 管理器专项覆盖状态/备注/登记 UI 路径、跨项目跨状态批量、冲突、`.bak`、原子 watcher、INDEX 引用、锁文件回滚、成功送回收站和独立窗口生命周期。
- 渲染：sample/current 均成功；四个 OLED 样式 `amberhud/phosphor/typographic/warmcard` 的蓝色主导像素均为 0；经典样式人工检查显示四行动分区、五态计数与管理入口无裁切。
- 正式 EXE 再跑 `--test-specboard-manager` 退出 0。
- 三路径 `Release`、D 正式、E 镜像均为 `1.0.5.15`、长度 `1907712`、SHA-256 `F7847767D11A3050D1D72DD808DF469C16F8702FA3354BFC62776CB1BACEF8A9`。
- D 正式进程 PID `89608`，`Responding=True`。

## 偏离、兼容与限制

- 项目接口索引的 direction 词表仅允许 `consume/provide/internal`，双向账本按 `internal` 登记，而不是 SPEC 文案中的 `both`。
- 源文件送回收站使用 `shell32!SHFileOperation`，符合“进入回收站、不永久删除”的行为约束。
- 分层窗口的桌面截图受 WorkerW/合成层影响，视觉验收以确定性 renderer 和窗口内自测为主。
- 仅构建并部署 ARM64；未生成 x64 正式产物。
- 执行 AI 按 skill 边界只把中央账本推进到 `awaiting_verify`，最终 `done` 需由验证 AI 或用户验收后写入。

## 回滚

部署前的三个 `1.0.5.13` 正式文件已保存在 `D:/E_Drive_Files/Codexproject/desktopdata/DesktopCodexAssistant/_build/formal-backups/20260712-024340-specboard-manager-1.0.5.15`。账本管理写入还会保留单份 `SPEC_BOARD.jsonl.bak`；本地新鲜度缓存可安全删除并在下次读取时重新播种。
