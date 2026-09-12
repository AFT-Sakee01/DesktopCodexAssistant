# Work Board 合并交付说明（GoalSpec）

- 文档类型：GoalSpec（执行完成快照）
- 交付版本：`2.0.0.54`
- 完成时间：`2026-09-12T17:03:28+09:00`
- 生成模型：Opus 5
- goal：把左缘 Dock 的 Spec Board 与 Codex Task 两个角色合并为单一 Work Board（绘制标题 `WORKBENCH`），按规格 P0–P3 全部执行完毕
- spec 路径：`Docs/Technical/Opus5-WorkBoardMerge-SPEC-v2.0.0.51-20260912-133632.md`
- spec SHA-256：`6362d8ff6d6ab8360a946b20b4de80b26cc1026af917a3a960e63afe527bff5d`

---

## 1. 需求映射

| 规格条目 | 实现 | 状态 |
|---|---|---|
| §4.1 合成层 `WorkBoardComposer` | `Core/WorkBoardComposer.cs`，纯函数、零 IO、零 timer、不修改入参 | 完成（P0） |
| §4.2 四级归属解析 | `root` 叶名 → `project.name` → `workspace_aliases` → 未归属；同层撞名按注册表顺序取首个并只记诊断 | 完成（P0） |
| §4.3 合并版式 | 五段行动流、瘦身会话卡、项目栏 `▶N` 胶囊、「未归属」伪行 | 完成（P1） |
| §4.3.1 降级阶梯 | `SpecBoardForm.ComputeSectionPlan` | 完成（P1） |
| §4.4 存活窗体与采样搬家 | `OperationCodexTaskBoardForm` 删除；`CodexRadarForm.SampleCodexTaskTimeline` | 完成（P2） |
| §4.5 设置迁移 | **99 → 100**（规格写 98→99，见 §6 偏离） | 完成（P2） |
| §4.6 拓扑收敛 | Dock 8→7，全局布局 20→19 | 完成（P2） |
| §5 P3 疑似提示 | `WorkBoardComposer.ApplySpecHints` + `WorkBoardSpecSessionHintEnabled` | 完成（P3） |
| §3.5 文档漂移订正 | `Docs/SpecBoard-Architecture.md` 左缘停靠节按七角色重写 | 完成 |

## 2. 实现范围与架构流程

```
SpecBoardReader.Read ──┐
                       ├─► WorkBoardComposer.Compose ─► WorkBoardModel ─► SpecBoardForm 绘制
CodexTaskPresentation ─┘        (纯函数)                                   ├─ 项目栏（▶N / 未归属）
   ▲                                                                      ├─ 五段行动流（降级阶梯）
   └── CodexRadarForm（headless owner，持有 reader + 累积时间线）           └─ 时间线泳道（footer 切换）
```

后端完全未改：`CodexTaskMonitorReader` 仍由 headless `CodexRadarForm` 持有并注册进 `CodexTaskPresentation.SnapshotProvider`，合并只动消费侧。这是整件事风险可控的根本原因。

## 3. 关键模块与接口

- `WorkBoardComposer`：`Compose` / `ResolveProjectName` / `ApplySpecHints` / `ExtractSpecStem` / `GetSectionLabel` / `GetSectionColor` / `RunSelfTest`
- `SpecBoardForm`：`BuildWorkBoardModel` / `ComputeSectionPlan` / `DrawWorkFlow` / `DrawLiveCard` / `DrawTimeline` / `RefreshTaskSampleIfDue` / `RunSectionLadderSelfTest`
- `CodexRadarForm.SampleCodexTaskTimeline`（新，累积搬家落点）
- `SpecBoardReader.ReadWorkspaceAliases` + `SpecBoardProject.WorkspaceAliases` + `MaxWorkspaceAliases = 16`
- 复用的索引项：`internal_api.codex_task_presentation`、`file_format.spec_board.projects`、`file_format.spec_board.ledger`、`internal_api.left_dock.edge_tab`

## 4. 数据与配置

新增 `WorkBoardView` / `WorkBoardTimelineMinutes` / `WorkBoardSpecSessionHintEnabled`（默认 `Table` / `45` / `true`），设置版本 `100` 从退役的 `CodexTaskBoardView` / `CodexTaskBoardTimelineMinutes` 迁移取值。`CodexTaskBoard{Width,Height,View,TimelineMinutes,ScaleOverridePercent,TransparencyOverridePercent,LeftDockEnabled,LeftDockTabCenterY}` 降为兼容持久化，已进 UI 绑定豁免表。`LeftDockButtonOrder` 由 `NormalizeColumnButtonOrder` 自动剔除退役的 `CodexTask` token 并保留其余相对顺序。程序仍**永不写** `PROJECTS.json`。

## 5. 验证证据

| 检查 | 结果 |
|---|---|
| ARM64 构建 | 成功，warning 0 |
| `--test` | exit 0 |
| `--test-layout` | exit 0，`Layout scaling policy: PASS` |
| `--test-settings-bindings` | exit 0，209 persisted / 215 public / 6 exemptions |
| `--test-operation-panel` | exit 0，含 `Work Board composition policy: PASS` |
| `--test-display-recovery` | exit 0 |
| `--render-specboard sample` | 五段 400/620 两张样张目测通过：段头全可见、footer 未被侵占、无裁切 |
| `--render-operation` | 七枚 dock tab 样张，codex 一枚不再产出 |
| `validate_docs.py` | `RESULT: PASS`，exit 0 |
| `git diff --check` | exit 0 |

执行过程中自检抓到并修复三处实际缺陷：`SectionPlan` 可访问性不一致（CS0050）、段名误用 `SpecBoardStatus.DisplayName` 导致措辞与合并前不符、`BuildTimeline` 缺 `maximumLanes` 实参。另有一处 P0 阶段的测试自身缺陷（把 reader 层行为断言写进了 composer 夹具）已在当期修正。

## 6. 对规格的偏离

1. **设置迁移 98→99 顺延为 99→100**。并发会话在 P0 与本期之间把 `CurrentSettingsVersion` 推到了 99，规格写作时的基线已失效。
2. **`Compose` 签名用 `WorkBoardFilter` 结构体而非 `string projectFilter`**。规格 §4.1 原文是「签名形如」，此处取更安全的形式：「未归属」需要一个哨兵值，而项目名来自用户自己写的 JSON，任何魔法字符串都有撞名风险。
3. **P1 与 P2 未分别部署，三期代码一次交付、只部署一次**。用户明确要求「继续做完」，分三次部署会让常驻程序反复重启；规格的逐期验证门仍逐一执行（P1 的降级阶梯经样张与自测验完才动 P2）。代价是用户没有经历「两块板并存对照」这一阶段。
4. **`RenderEdgeDockTabSample` 迁至新文件 `Core/OperationForm.EdgeDockSample.cs`** 而非随 `Core/OperationForm.CodexTasks.cs` 一并删除——它渲染的是 dock tab 拓扑，与任务看板无关。

## 7. 限制与残余风险

- 尚未真机交互验证 hover 展开、项目过滤联动两半、外部点击收回与 footer 时间线切换；样张与自检覆盖绘制与几何，不覆盖指针行为。
- `≈` 疑似提示是文本启发式，同项目内标题措辞接近的两条 spec 可能误提示；已用同项目限定、`≈` 强制标记与可关闭门控限制影响面。
- `BunkyoUNV` 未补 `workspace_aliases` 时其会话落入「未归属」属预期行为（规格 §9.2-5），不是缺陷。
- 回滚需要同时回退设置版本 100。
