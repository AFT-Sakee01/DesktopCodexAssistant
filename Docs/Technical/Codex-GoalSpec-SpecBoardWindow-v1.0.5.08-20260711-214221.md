# GoalSpec：Spec Board Window

## 元数据

- Goal：严格执行并验收 Spec Board Window SPEC，完成实现、测试、文档、版本、ARM64 部署与账本状态推进。
- Spec：`Docs/Technical/Fable5-SpecBoardWindow-SPEC-v1.0.5.07-20260711-202714.md`
- Spec SHA-256：`CD35A07CDDC63F8112122060EB8F2D753AE9EA40B57CB32BB224037F51B1EE85`
- 实施版本：`1.0.5.08`
- 执行模型：Codex
- 时间：`2026-07-11T21:42:21.600+09:00`（Asia/Tokyo）
- 状态：implemented；中央账本按执行 AI 边界为 `awaiting_verify`

## 需求映射与实现范围

| Spec | 实现 |
| --- | --- |
| 数据层与坏行容忍 | `Core/SpecBoardReader.cs` 使用 `SharedEncoding.Utf8NoBom` 逐行解析封闭状态词表；坏行计数，账本缺失空态，注册表降级，事件时间容错。 |
| 防漏对账 | 按 `PROJECTS.json` 顺序扫描 `root + spec_glob`，排除 GoalSpec，合成 `unregistered`，标记待办文件丢失和不可达项目；UI 刷新 3s 超时放弃结果。 |
| 分层窗口 | `Core/SpecBoardForm.cs` 继承 `LayeredWidgetFormBase`，复用 layered surface、`UiFontCache`、`DesignTokens`、`BurnInProtection`；左项目栏和右行动流全部行高由字体实测。 |
| 双击入口 | `OperationForm.OnMouseDoubleClick` 的 RadialDial Core 与经典 Start 分支改调 `ToggleSpecBoardWindow`；补齐 `StandardClick`/`StandardDoubleClick` 控件样式。QuickGrid 代码与自测保留、入口废弃。 |
| 自动收回与刷新 | 可见时单一维护 Timer 管理 20s 自动收回、鼠标暂停/移出重置、500ms watcher 防抖、60s 兜底和 5min 对账；隐藏和挂起时停止 Timer/watcher。 |
| 设置与布局 | 六键完成 defaults、两套预设、clone、load/save、normalize、Version 64 迁移、Win11 设置页和浏览按钮、全局布局编辑器拖拽。`-1` 恢复自动锚定。 |
| 渲染与 OLED | `--render-specboard sample/current` 输出 Classic + 四个 OLED fixture 与真实账本；OLED 文字使用灰度抗锯齿，像素审计无蓝色主导残留。 |
| 文档与索引 | 新增 `Docs/SpecBoard-Architecture.md`，同步文档地图、刷新规则、README、人读接口表、FEATURE/INTERFACE/Technical INDEX。 |

## 架构流程与关键边界

`OperationForm` 按需持有 `SpecBoardForm`，并转发设置、全屏隐藏、显示挂起与恢复。看板呼出后后台读取基础快照；需要对账时另起受 3s UI 接受期限约束的扫描任务。后台只发布完整快照，paint 路径不读文件。文件 watcher 只监听中央目录的账本与项目注册表，隐藏时禁用事件。

窗口严格只读。中央 `_spec_board` 是用户开发环境资产，不是程序运行态数据，故不迁入 `%LOCALAPPDATA%`；程序不在该目录创建或修复文件。AI 写入通过 `C:\Users\GengH\.claude\skills\spec-board\SKILL.md` 的状态机和整文件校验契约执行。

状态卡分配保证每个非空段至少一张完整卡，剩余容量再按未登记、需要执行、等待验证分配。文件丢失卡灰显并明确显示“文件丢失”。四个 OLED 变体沿用现有无蓝语义色，不改数据层或线程模型。

## 验收证据

- ARM64 候选与 Release 构建成功；Release FileVersion/ProductVersion `1.0.5.08`。
- `--test`：ARM64 原生进程采样成功。
- `--test-settings-bindings`：六键默认、clone、save/load、低高钳制明确 PASS；235 个持久化属性 round-trip PASS。
- `--test-layout`：PASS；覆盖左右栏不相交、卡片包含、溢出、相对时长、reader 坏行/缺失/对账和隐藏计时器审计。
- `--test-operation-panel`、`--test-logger`、`--test-display-recovery`：PASS。
- `--test-radar-display-lifecycle --iterations 100`：PASS，`handles_delta=0`、`gdi_delta=0`、`user_delta=-1`。
- 操作面板 1.0.5.07 before 与 1.0.5.08 after 的 8 张 PNG（含 QuickGrid/current）全部 `changed_pixels=0`。
- Spec Board sample/current 均 432×400；四个 OLED sample 的 `blue_dominant_pixels=0`。目检确认橙/红/黄段、灰显文件丢失、坏行警告、长标题省略、`+N 更多` 和真实账本卡。
- 真机 RadialDial 与 Classic 双击均呼出 `Spec Board`，位置逻辑缩放后为 216×200、左对齐、与操作面板约 10px 间隔；再次双击收回，窗口总数证明 QuickGrid 未出现。
- 真机 5s 自动收回、悬停 7s 保持、移出 6s 收回、0s 模式 60s 保持全部通过；测试后 settings 文件按原 SHA-256 恢复。
- G8 测试行完成 pending→awaiting_verify→done→abandoned；可见窗口在写入后 2s 内 footer 从“完成 1 · 放弃 0”更新为“完成 0 · 放弃 1”。测试 spec 已删除，账本逐行解析与 ID 唯一为 `OK 2`。
- Docs Gate：JSONL 解析与 ID 唯一 PASS，索引路径存在 PASS，`git diff --check` 无空白错误。
- 部署后 Release、D 正式、E 镜像版本与 SHA-256 一致；最终正式进程 PID 49768，Responding=True。

## Spec 偏离与限制

- 未物理删除 QuickGrid；按 spec 的最小风险选项保留代码和自测，并新增独立 deprecated 功能索引项说明回滚理由。
- G6/G7 的破坏性坏行、缺失路径和临时未登记文件使用隔离临时目录的确定性 reader 自测完成；未污染中央账本。真实中央账本 current 对账同时发现既有未登记项，证明生产扫描路径工作。
- 本执行会话完成验收，但遵循 `spec-board` skill 的角色边界，只把主账本行推进到 `awaiting_verify`；独立验证 AI 通过后才能推进 `done`。
- 仅构建和部署 ARM64；项目规则禁止未明确请求的 x64 验证。
