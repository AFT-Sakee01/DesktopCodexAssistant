# 独立 Claude 窗口退役实施规格（Standalone Claude Window Retirement SPEC）

- 版本：1.0.6.25（或执行时的下一个可用版本号）
- 生成模型：Claude Opus 4.8
- 生成时间：2026-07-21T18:11:25+09:00（UTC 2026-07-21T09:11:25Z）
- 主题：退役独立 Claude Radar 窗口（`ClaudeRadarForm`），删除其窗口专属设置，把 Claude 显示所有权固化到共享 Radar 小窗；**Claude 数据层与共享 Radar 的 CLAUDE 模式设置全部保留不动**

---

## 1. 目标

独立 Claude Radar 小窗与共享 Radar 小窗（`CodexRadarForm`）的 CLAUDE 模式在信息上完全重叠——共享小窗已能按前台自动切到 CLAUDE 并显示同一套 Claude 数据。本 SPEC 退役独立窗口这一层，收拢仅服务它的设置项，让设置面更贴合"目前的窗口"。

具体：
1. `WidgetForm` 不再创建/托管 `ClaudeRadarForm`，删除 `Core/ClaudeRadarForm.cs`。
2. 把"共享窗口拥有 Claude 时钟选择"这一分支固化为恒真（原来靠 `ClaudeRadarEnabled` 反选）。
3. 删除 **仅** 服务独立窗口的设置键（透明度/缩放/位置/显示器/服务探针令牌/随机测试/该模块的 LayoutWorkArea 缓存），schema 84→85 并做一次性迁移。
4. 设置 UI：拆掉"独立小窗"分组与"Claude Radar 位置"分组，"Claude Radar"页改名为面向"共享 Radar 的 Claude 数据"。
5. 清理布局编辑器、操作面板径向菜单、渲染样张与自测入口里的独立窗口条目。

**非目标（严格不许动）**：
- 不动任何 Claude **数据层**：`ClaudeRadarReader`、`ClaudeRadarSnapshotScheduler`、`ClaudeRadarModels`、`ClaudeRadarClockAutoSwitchSelector`、`ClaudeCodeUsageReader`、`ClaudeQuotaRingShared`、`ClaudeRadarModelMapEditorForm`——共享 Radar 的 CLAUDE 模式依赖它们。
- 不删共享 Radar 的 Claude 设置键（见 §3.1 白名单）。
- 不动 DeepSeek 余额、Claude Code 用量令牌、Radar 通用时钟设置。
- 不改共享 Radar 的 CLAUDE 模式呈现效果——退役后前台切到 Claude 应用时，共享小窗显示必须与现状一致。

---

## 2. 现状约束（实施前必须成立的前提，已核实）

- 独立窗口**仍在运行**：`WidgetForm.EnsureClaudeRadarWindow()` 在 `ClaudeRadarEnabled` 为真时 `new ClaudeRadarForm(...)`（`Core/WidgetForm.cs:405-426`），字段 `claudeRadarForm`（:74），关闭走 `CloseClaudeRadarWindow()`（:446-461）。
- 所有权固化点：`Core/CodexRadarForm.cs:6522-6527` 调 `ClaudeRadarClockAutoSwitchSelector.ShouldSharedWindowOwnClaudeSelection(this.CurrentSettings.ClaudeRadarEnabled)`；定义在 `Core/ClaudeRadarModels.cs:338-341`，当前语义 `return !standaloneClaudeRadarEnabled;`。选择器自测在 `Core/ClaudeRadarModels.cs:373-378` 断言双向行为。
- 共享 Radar **依赖 Claude 数据层**（核实）：`CodexRadarForm.cs` / `CodexRadarForm.EvenRow.cs` 使用 `ClaudeRadarReader`；`CodexRadarForm.cs` 使用 `ClaudeRadarSnapshotScheduler`。这些**保留**。
- `ClaudeRadarForm` 这个**类型**在主树的引用仅来自：`WidgetForm.cs`（宿主）、`DesktopCodexAssistant.cs`（渲染样张 + 生命周期自测）；`ClaudeQuotaRingShared.cs:49`、`RadarBottomInfoTextRenderer.cs:9` 只是历史注释。删除该文件不会波及数据层。
- 设置项归属（核实）——**窗口专属（可删）** 只在 `ClaudeRadarForm` / 设置持久化 / 布局编辑器里出现；**共享（须留）** 会被 `CodexRadarForm` 读取。
- `ClaudeRadarLayoutWorkArea{Left,Top,Width,Height}` 是分辨率兼容子系统按 `ModuleClaudeRadar` 维护的工作区缓存（`WidgetSettings.GetModuleLayoutWorkArea` 等）；模块退役后这 4 个字段随之失去消费者，一并删除。**其余 24 个 `*LayoutWorkArea*` 字段是活的，禁止删除。**
- schema 当前 84（`WidgetSettings`）。参照 1.0.6.24 的做法：升 schema、首次加载原子规范化保存、加旧版本 fixture 自测。

---

## 3. 交付项

### 3.1 设置键去留边界（唯一权威清单）

**删除（独立窗口专属，共 16 键）**
```
ClaudeRadarEnabled
ClaudeRadarTransparencyPercent
ClaudeRadarTransparencyOverridePercent
ClaudeRadarScaleOverridePercent
ClaudeRadarServiceProbeToken
ClaudeRadarWidth  ClaudeRadarHeight  ClaudeRadarLeftX  ClaudeRadarBottomY
ClaudeRadarDisplayDeviceName
ClaudeRadarRandomTestEnabled  ClaudeRadarRandomTestAutoRefresh  ClaudeRadarRandomTestRefreshToken
ClaudeRadarLayoutWorkAreaLeft  ClaudeRadarLayoutWorkAreaTop  ClaudeRadarLayoutWorkAreaWidth  ClaudeRadarLayoutWorkAreaHeight
```

**保留（共享 Radar 的 CLAUDE 模式在用，一个都不许删）**
```
ClaudeRadarModelKey
ClaudeRadarJsonEnabled
ClaudeRadarCommunityRatingsEnabled
ClaudeRadarLocalQuotaFallbackEnabled
ClaudeRadarHomepageFallbackEnabled
DeepSeekApiKeyRevision
（Claude Code 用量令牌命令、RadarClockTimeDisplayMode / RadarClockAutoSwitchModelEnabled 等 Radar 通用键）
```

### 3.2 宿主退役（`Core/WidgetForm.cs`）

- 删除字段 `claudeRadarForm`（:74）、`EnsureClaudeRadarWindow()`（:403-427）、`CloseClaudeRadarWindow()`（:446-461），以及所有调用点（创建调度、可见性/隐藏/缩放/自动隐藏保活的遍历中对 `claudeRadarForm` 的分支）。全仓搜 `claudeRadarForm` 清零。
- 保留 `codexRadarForm` 及共享窗口的一切逻辑。

### 3.3 共享窗口所有权固化（`Core/ClaudeRadarModels.cs` + `Core/CodexRadarForm.cs`）

- `ShouldSharedWindowOwnClaudeSelection` 改为恒返回 `true`（独立窗口不复存在，共享窗口永远拥有 Claude 选择）。保留方法与调用点以最小化改面，或直接内联删除条件——二选一，但语义必须是"共享窗口始终处理 Claude 时钟选择"。
- `CodexRadarForm.cs:6522-6527`：随之简化，去掉对 `ClaudeRadarEnabled` 的读取（该键将不存在）。
- 更新 `ClaudeRadarModels.cs:373-378` 选择器自测：断言改为"共享窗口恒拥有所有权"。

### 3.4 设置模型（`Settings/WidgetSettings.cs`）

- 删除 §3.1 的 16 个属性声明及其在 defaults / `CreateDefaults` / `Clone` / `Normalize` / 各 `Capture*` 与 `Adapt*` / `Save`（ToString 持久化行）/ `ApplyValue`（字符串键分支）/ 旧自测里的**全部**自引用。
- 移除分辨率兼容子系统里的 `ModuleClaudeRadar` 分支：`GetModuleLayoutWorkArea`、`CaptureModuleLayoutWorkArea`、`CaptureCurrentWorkArea`、`CaptureAllModuleLayoutWorkAreas`、以及 `GetWorkAreaForModule` 中对应 case。核对 `Module*` 常量若 `ModuleClaudeRadar` 变孤立则一并删。
- **schema 84→85**：旧配置（含 v84 及更早）首次加载执行一次原子规范化保存，废弃键不再输出。
- 新增 **v84 fixture 自测**：喂入含上述 16 键的 v84 settings，断言加载后 `Version==85` 且 16 个废弃键在 Save 输出中不存在；同时断言保留键（§3.1 白名单）原值不丢。

### 3.5 设置 UI（`Settings/Win11SettingsForm.cs`）

- **"Claude Radar"页**（`BuildPages`，现 :535-544）：
  - 删"独立小窗"分组（`ClaudeRadarEnabled` / `ClaudeRadarTransparencyPercent` / `ClaudeRadarTransparencyOverridePercent`）。
  - 删"!随机测试"分组（`ClaudeRadarRandomTest*`）。
  - "!元数据与诊断"里删 `ClaudeRadarServiceProbeToken`，**保留** `ClaudeRadarHomepageFallbackEnabled`。
  - 页标题/描述改为面向"共享 Radar 的 Claude 数据"（不再叫"独立 Claude 小窗"）。保留：Claude 模型、Claude 数据链路、Claude Code 用量令牌、DeepSeek 余额。
- **"布局与位置"页**（现 :475-492）：
  - "每窗口缩放"删 `ClaudeRadarScaleOverridePercent`。
  - "显示器分配"删 `ClaudeRadarDisplayDeviceName`。
  - 删整个"!Claude Radar 位置"分组（`ClaudeRadarWidth/Height/LeftX/BottomY`）。
- **UI 绑定豁免**（`CreateSettingsUiBindingExemptions`，现 :4232-4241）：从 work-area 缓存豁免列表移除 4 个 `ClaudeRadarLayoutWorkArea*`。
- 删对应的标题/提示/范围/枚举绑定与辅助判断中出现的上述 16 键条目。`--test-settings-bindings` 必须仍 PASS。

### 3.6 布局编辑器 / 径向菜单 / 渲染与自测入口

- **`Core/GlobalLayoutEditorForm.cs`**：移除 `ModuleClaudeRadar` 全部处理——结构面登记（:240-242）、启用判定（:303-305）、布局项构造（:356-362）、位置写回（:636-639）、显示器写回（:743-745）；以及 edge/classic 预设里 `ClaudeRadarEnabled = false`（:1012、:1044）。
- **`Core/OperationForm.RadialDial.cs`**：删除 `ClaudeRadarEnabled` 的两个 `NewSettingToggle`（:447、:526）与条目列表中的键（:2550）。核对径向父节点若因此空缺需重排。
- **`DesktopCodexAssistant.cs`**：
  - 删 `--render-clauderadar` 分派（:150-152）与 `RenderClaudeRadarSamples`（:1083-1098）。
  - 删 `ClaudeRadarForm.RunRenderResourceSelfTest()` 调用（:954）。
  - `TestRadarDisplayLifecyclePolicy`（:1593 起）去掉 Claude 构造与所有 `claude.*` 调用，只留 Codex + EdgeDock 生命周期；去掉 `settings.ClaudeRadar*` 赋值。
- **删除文件 `Core/ClaudeRadarForm.cs`**。删后全仓搜类型名 `ClaudeRadarForm` 应仅剩历史注释；`ClaudeQuotaRingShared.cs:49`、`RadarBottomInfoTextRenderer.cs:9` 注释可改为过去式（可选）。

---

## 4. 验证要求

1. `--test-settings-bindings` PASS（豁免列表与页面绑定已同步，无悬挂键）。
2. 新增 v84→85 迁移 fixture 自测 PASS：16 废弃键被丢弃、`Version==85`、白名单保留键值不丢。
3. `ClaudeRadarModels` 选择器自测 PASS（改为共享窗口恒拥有所有权）。
4. `--test-layout` PASS（无 `ModuleClaudeRadar`）；布局编辑器打开无残缺项。
5. 全仓编译零错误；搜索 `claudeRadarForm` / `ClaudeRadarEnabled` / `ClaudeRadar*位置键` 均无残留消费者。
6. **回归核心不变量**：前台切到 Claude 应用时，共享 Radar 小窗仍正确显示 Claude 数据（CLAUDE 模式）。用 `--render-codexradar`（CLAUDE 模式样张）人工核对与退役前一致。
7. 按根 `AGENTS.md`：构建 ARM64 → 备份现有正式 exe → 覆盖 → 从 E: 入口重启。

---

## 5. 文档同步（§4 触发表）

- `Docs/Codex-ClaudeRadar-Architecture.md` / `Docs/Claude-EvenRow-DialCard-Technical.md`：标注独立窗口已退役，Claude 呈现仅经共享 Radar。
- `Docs/Performance-And-Window-Runtime.md`：窗口清单去掉独立 Claude 窗口。
- `Docs/Indexes/FEATURE_INDEX.jsonl`：移除"独立 Claude 窗口"功能行；共享 Radar 行注明承载 Claude。
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`：移除 `ClaudeRadarForm` 接口条目与 16 个废弃设置键；`ShouldSharedWindowOwnClaudeSelection` 语义更新。
- `Docs/Maintenance/CHANGELOG.jsonl`：一条 `refactor` 变更记录 + 一条 `deployment` 记录（schema 84→85、退役 16 键、删除 `ClaudeRadarForm.cs`）。
- 根 `AGENTS.md` / `README.md`：`Current version` → 1.0.6.25；窗口清单更新。
- 渲染样张相关记忆/文档：`--render-*` 从"6 窗口"减为 5（去掉 `--render-clauderadar`）。

---

## 6. 残留风险与顺序建议

- `Settings/WidgetSettings.cs` 与 `Settings/Win11SettingsForm.cs` 目前有大量未提交改动（并发在建）。执行前先与在建改动对齐，避免删除行与新增行冲突；建议在这两个文件的当前工作副本上直接改，而非从旧基线覆盖。
- 删除设置键前务必完成 §3.2/§3.3/§3.6 的消费者清理，否则编译期即报错——把"删消费者→删声明→升 schema→改 UI→删文件"按此顺序执行最安全。
- 白名单键（§3.1）若被误删，前台 Claude 时共享小窗会掉数据——这是本 SPEC 唯一必须守住的红线，验证第 6 项专门覆盖它。
