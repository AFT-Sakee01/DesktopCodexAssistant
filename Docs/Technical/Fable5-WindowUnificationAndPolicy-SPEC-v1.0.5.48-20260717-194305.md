# Fable5-WindowUnificationAndPolicy-Spec — 窗口生命周期整合、透明度体系与电源模式策略中心化执行规格

生成时间：2026-07-17 19:43（Asia/Tokyo）。审查基线：`ProductIdentity.Version = 1.0.5.48`，git HEAD `e7154c1`（工作区在生成时存在未提交改动，执行前必须先确认工作区已完成同步提交，禁止在脏工作区上开始 T0）。

本文档面向**执行 AI**：每个任务给出目的、前置检测、精确步骤、禁止事项、可机器判定的验收标准与回滚方案。**验收标准全部是硬性 Gate，任何一条不满足即判定该任务失败，必须回滚后重试或上报，不允许"基本通过"。**

用户已裁决的两个设计决策（执行 AI 不得偏离，也不得重新征询）：

1. **透明度模型 = 全局默认 + 每窗口覆盖**：全局"整体透明度"必须对所有分层窗口生效；每个窗口可选择"跟随全局"或设置独立值。
2. **电源模式 = 仅策略中心化**：把散落的刷新间隔收敛为单一策略表。**不**新增档位、**不**做每窗口独立档位、**不**做自动切换增强，所有现有数值保持逐字节不变。

---

## 0. 执行 AI 必读

1. 先读根目录 `AGENTS.md` 全部内容并遵守，特别是：ARM64 默认、禁止未经要求编译 x64、部署规则（构建→备份正式 exe→覆盖→重启）、"新设置必须覆盖 defaults/clone/load/save/normalization/settings UI/migration version/`--test-settings-bindings`"规则、CHANGELOG/索引维护规则。
2. 本仓库处于并发编辑状态。**每次修改文件前必须重新读取该文件当前内容**——本文档中的类名/成员名是审查时证据，执行时以内容搜索定位为准，禁止凭行号盲改。
3. 逐任务执行、逐任务验收、逐任务提交 git，提交信息格式 `Spec-WU-T<n>: <一句话>`。禁止把多个任务混在一个提交里。
4. 构建命令统一为：
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\spec-wu\t<任务号>.exe
   ```
5. 自检矩阵（`<exe>` 为刚构建产物；全部退出码 0 且输出含各自 PASS 字样才算通过）：
   ```powershell
   & <exe> --test-logger;  & <exe> --test-layout;  & <exe> --test-settings-bindings
   & <exe> --test-settings-open-close;  & <exe> --test-display-recovery
   & <exe> --test-operation-panel;  & <exe> --test-specboard-manager
   & <exe> --test-codex-task-monitor
   & <exe> --test-radar-display-lifecycle --iterations 100
   ```
   （`--test` 含真实网络探测，仅 T0 基线与最终 Gate 各跑一次，网络失败不算任务失败但须记录。）
6. 渲染验收统一模式（9 组）：
   ```powershell
   & <exe> --render-codexradar --out _build\spec-wu\render\<tag>\codex
   & <exe> --render-clauderadar --out _build\spec-wu\render\<tag>\claude
   & <exe> --render-connectioncheck --out _build\spec-wu\render\<tag>\conn
   & <exe> --render-networkmonitor --out _build\spec-wu\render\<tag>\net
   & <exe> --render-powerthermal --out _build\spec-wu\render\<tag>\power
   & <exe> --render-widget --out _build\spec-wu\render\<tag>\widget
   & <exe> --render-operation --out _build\spec-wu\render\<tag>\op
   & <exe> --render-specboard sample --out _build\spec-wu\render\<tag>\specboard
   & <exe> --render-specboardmanager sample --out _build\spec-wu\render\<tag>\specmgr
   ```
7. 像素对比：复用既有 `_validation/Compare-RenderSamples.py`。"渲染无回归"的机器判定 = 对 before/after 两目录运行输出 `RESULT: PASS`（阈值默认 0.1%；纯重构任务期望 0 差异，0 < diff ≤ 0.1% 须人工说明并记录，> 0.1% 直接 FAIL）。
8. 文档 Gate：每个任务收尾必须运行 doc-governance 校验脚本（项目内如无副本，先把 skill 的 `validate_docs.py` 复制为 `Docs/validate_docs.py` 并纳入 T0 提交）：
   ```powershell
   python Docs\validate_docs.py --root .
   ```
   全绿才算任务完成。

---

## 1. 现状事实（2026-07-17 审查）

### 1.1 分层窗口清单

全部继承 `Core/LayeredWidgetFormBase.cs`（上一轮 Spec-T4 产物，已上收：分层位图表面、渲染缓冲、`RenderLayeredWindow` 模板、`S()` 缩放、`RoundedRectangle`、烧屏 slot、`CreateParams` 位组合）：

| 窗口 | 文件 | 行数 |
|---|---|---:|
| WidgetForm（主窗口/协调者） | Core/WidgetForm.cs | 3,841 |
| CodexRadarForm | Core/CodexRadarForm.cs + 5 partial | ~19k 合计 |
| ClaudeRadarForm | Core/ClaudeRadarForm.cs | 4,220 |
| OperationForm（+3 个嵌套子窗体：QuickGrid / LauncherTrio / CodexTaskBoard） | Core/OperationForm.cs + 8 partial | ~10k 合计 |
| NetworkMonitorForm | Core/NetworkMonitorForm.cs | 3,565 |
| PowerThermalForm | Core/PowerThermalForm.cs | 2,774 |
| ConnectionCheckForm | Core/ConnectionCheckForm.cs | 1,612 |
| SpecBoardForm | Core/SpecBoardForm.cs | 2,485 |
| EdgeDockTabForm（左缘停靠 tab，SpecBoard 与任务看板共用） | Core/EdgeDockTabForm.cs | 303 |

### 1.2 生命周期样板重复清单（本次整合主对象）

以下方法名在各窗体中近似复制（`grep -hoE "void [A-Z][A-Za-z]+\(" <8 个窗体主文件> | sort | uniq -c` 实测）：

| 重复实现 | 份数 |
|---|---:|
| `RecoverAfterDisplayResume` / `OnSizeChanged` / `ApplyRuntimeSettings` | 8 |
| `SetHiddenForFullscreen` / `PrepareForDisplaySuspend` / `WndProc` / `OnShown` / `OnFormClosed` | 7 |
| `OnTimerTick` | 6 |
| `UpdateHoverAnimationTimer` / `OnHoverTimerTick` / `SetSharedInteractionPolling` / `SetAutoHideKeepAliveActive` / `ForceRefresh` / `ConfigureGraphics` / `ApplyPerformanceTimerIntervals` / `ApplyClickThroughStyle` | 5 |
| `DrawFittedText` | 4 |

### 1.3 透明度现状矩阵（bug 证据）

设置键（均在 `Settings/WidgetSettings.cs`，钳制范围 `MinBackgroundTransparency=0` .. `MaxBackgroundTransparency=90`）：

| 键 | 语义（设置 UI 文案） | 默认值 |
|---|---|---:|
| `ApplicationTransparencyPercent` | "主窗口整体透明度"（实际是内容层 alpha） | 0 |
| `BackgroundTransparencyPercent` | 主窗口背景 | 9 |
| `CodexRadarTransparencyPercent` / `ClaudeRadarTransparencyPercent` / `PowerThermalTransparencyPercent` / `NetworkMonitorTransparencyPercent` / `ConnectionCheckTransparencyPercent`（另有 Border 键） / `OperationBackgroundTransparencyPercent` | 各窗口**背景**透明度 | 各自默认 |

消费矩阵（grep 实测）：

| 窗口 | 消费 `ApplicationTransparencyPercent`（整体） | 有本窗口背景透明度键 |
|---|:-:|:-:|
| WidgetForm | ✅（`GetContentOpacityAlpha`） | ✅ |
| CodexRadarForm | ✅ | ✅ |
| ClaudeRadarForm | ✅ | ✅ |
| NetworkMonitorForm | ✅ | ✅ |
| PowerThermalForm | ✅ | ✅ |
| ConnectionCheckForm | ✅ | ✅ |
| **OperationForm（含 QuickGrid/LauncherTrio 子窗）** | ❌ | ✅ |
| **CodexTaskBoard（Operation 子窗）** | ❌（仅借用 owner 背景 alpha） | ❌ |
| **SpecBoardForm** | ❌（无任何透明度处理） | ❌ |
| **EdgeDockTabForm** | ❌（无任何透明度处理） | ❌ |

即用户报告的"通用透明度没法对每个窗口生效"的根因：**4 类窗口从未接入全局整体透明度，其中 SpecBoard/停靠 tab/任务看板连背景透明度键都没有。** 此外悬停透明度（`ApplyHoverTransparencyTarget`，5% 目标 alpha）只在 5 个窗体各自复制了一份，SpecBoard/停靠 tab 同样没有。

### 1.4 电源模式现状

- 枚举 `WidgetPerformanceMode { WindowsPowerMode, Smooth, Balanced, BatterySaver }`（`Settings/WidgetSettings.cs`）。`GetEffectivePerformanceMode` 把 `WindowsPowerMode` 经 `PowerThermalForm.ReadCurrentSystemPowerModeText()` 映射为实际三档，带 2 秒静态缓存（`EffectivePerformanceModeCacheMs = 2000`）。
- 刷新间隔散落在 `WidgetSettings` 的 9 个静态函数中，每个函数内部是 if/else 三档字面量：`GetWidgetSampleIntervalMs`、`GetPanelRenderIntervalMs`、`GetExpensiveHardwareSampleIntervalMs`、`GetHoverAnimationIntervalMs`、`GetInteractionIdlePollingIntervalMs`、`GetNetworkLocalRefreshIntervalMs`、`GetNetworkIdlePollingIntervalMs`、`GetNetworkConnectivityIntervalMs(mode, state)`、`GetNetworkDnsProbeIntervalMs(mode, state)`；另有 `ShouldEnableProcessPowerSaving`。
- `Docs/Component-Refresh-Rules.md` §2 是这些数值的人读镜像（唯一权威表），当前靠人工保持同步，无机器校验。
- 遗留别名：`PowerSavingEnabled` 是 `PerformanceMode` 的派生兼容键（Save 双写）。

---

## 2. 目标设计

### 2.1 透明度：生效值管线（收进基类）

单一生效公式，实现于 `LayeredWidgetFormBase`，所有窗体不再各自覆盖 `GetApplicationOpacityAlpha`：

```
生效整体透明度% = (本窗口覆盖值 >= 0) ? 本窗口覆盖值 : 全局 ApplicationTransparencyPercent
整体 alpha      = ComputeOpacityAlpha(生效整体透明度%)
最终 alpha      = 悬停动画(整体 alpha)        // 悬停驱动器见 T4，T2 阶段先保留各窗体现有悬停回调钩子
```

- 基类新增虚钩子 `protected virtual int WindowTransparencyOverridePercent => -1;`（-1 = 跟随全局）与 `protected virtual int ApplyHoverAlpha(int alpha) => alpha;`；`GetApplicationOpacityAlpha()` 在基类实现上述公式并**不再是 virtual 供窗体整体替换**。
- 背景透明度维持每窗口现有键与语义**完全不变**（禁止改名/改语义）；SpecBoard、任务看板、停靠 tab 在 T3 补齐背景键不在本次范围——本 SPEC 只为它们接入**整体**透明度（停靠 tab 跟随其宿主看板的生效值）。
- `RenderSample` 各入口已直接调用 `form.GetApplicationOpacityAlpha()`，管线收敛后自动跟随，无需改动。

### 2.2 每窗口覆盖设置（9 个新键）

| 新键 | 覆盖对象 |
|---|---|
| `MainWidgetTransparencyOverridePercent` | 主窗口 |
| `CodexRadarTransparencyOverridePercent` | Codex 共享雷达 |
| `ClaudeRadarTransparencyOverridePercent` | Claude 独立雷达 |
| `PowerThermalTransparencyOverridePercent` | 功耗温度 |
| `NetworkMonitorTransparencyOverridePercent` | 网络监控 |
| `ConnectionCheckTransparencyOverridePercent` | 连接检测 |
| `OperationTransparencyOverridePercent` | 操作面板（含 QuickGrid/LauncherTrio 子窗） |
| `SpecBoardTransparencyOverridePercent` | Spec 看板（含其停靠 tab） |
| `CodexTaskBoardTransparencyOverridePercent` | Codex 任务看板（含其停靠 tab） |

- 取值范围 `-1..90`，默认 `-1`（跟随全局）；`Normalize` 钳制到该范围。
- 设置 UI：在各窗口既有分区追加一行"整体透明度覆盖"，数值 `-1` 的帮助文案写明"−1 = 跟随全局整体透明度"。
- 全部新键按 AGENTS.md 规则走 defaults/clone/load/save/normalization/settings UI/migration version/`--test-settings-bindings` 全链路。

### 2.3 电源模式策略中心化

- 新建 `Settings/WidgetRefreshPolicy.cs`：`internal static class WidgetRefreshPolicy`。核心是**一张只读策略表**（`PolicyKind` 枚举 × 三档 struct 行 `PolicyRow { int Smooth; int Balanced; int BatterySaver; }`），加网络状态维度的两张子表（connectivity / DNS，按 `NetworkAccessState` 分行）。所有现有数值原样搬入，`int.MaxValue`（不轮询）与 `0`（立即）语义保留。
- `WidgetSettings` 现有 9 个 `Get*IntervalMs` 与 `ShouldEnableProcessPowerSaving` **签名不变**，改为薄委托转发到策略表（调用方零改动）。`GetEffectivePerformanceMode` 及其 2 秒缓存、`WindowsPowerMode` 映射逻辑原样保留位置不动。
- 防漂移 Gate：新增自检 `--test-refresh-policy`（挂入 `--test` 聚合），把当前全部（函数 × 三档 × 网络状态）组合的期望值**以字面量断言**写死（期望值从 T0 基线 exe 的实测输出抄录），任何数值漂移即 FAIL。
- `Docs/Component-Refresh-Rules.md` §2 仍是人读权威表，追加一句"代码侧单一事实源：`WidgetRefreshPolicy`（数值以 `--test-refresh-policy` 断言锁定）"。

### 2.4 生命周期第二次上收（组合式控制器）

- 新建 `Core/WidgetLifecycleController.cs`：每个窗体持有一个实例（组合，不加深继承层级）。收敛职责：
  1. 显示挂起/恢复编排（`PrepareForDisplaySuspend`/`RecoverAfterDisplayResume` 的公共骨架：释放/重建分层资源→重定位→强制重绘，窗体差异化部分通过回调注入）；
  2. 全屏隐藏（`SetHiddenForFullscreen` 公共部分）；
  3. 点击穿透样式（`ApplyClickThroughStyle`）；
  4. 性能三档 timer 间隔应用（`ApplyPerformanceTimerIntervals`，读 `WidgetRefreshPolicy`）;
  5. 悬停透明度动画驱动（`UpdateHoverAnimationTimer`/`OnHoverTimerTick`/5% 目标 alpha 插值，即 §2.1 的 `ApplyHoverAlpha` 提供者）；
  6. 共享交互轮询登记（`SetSharedInteractionPolling`/`SetAutoHideKeepAliveActive`）。
- `WidgetForm` 仍是全局协调者（AGENTS.md 不变量）：它对子窗口的调用点从"逐窗体直呼私有方法"改为统一走控制器接口。
- 迁移策略：**一次只迁一个窗体**，迁完立即构建 + 该窗体渲染对比 + 全量自检，独立提交（`Spec-WU-T4.<n>: <Form> onto WidgetLifecycleController`）。窗体确有特殊行为（如 NetworkMonitor 双缓冲、CodexRadar 暂停恢复错峰）的，保留派生私有部分，禁止强行上收。

### 2.5 文本适配绘制统一

- 在 `Core/DrawingUtil.cs` 新增共享 `DrawFittedText`（API 形态强制"测量宽度 = 绘制宽度"：只接收一个目标矩形参数，内部完成 shrink-to-fit 测量与绘制，杜绝历史上"测量宽度 ≠ 实际绘制宽度"的 bug 模式复发）。4 份窗体私有实现逐一迁移删除。

### 2.6 CodexRadarForm 拆分（机械 partial 拆分）

- 沿既有 partial 先例继续拆主文件（16,137 行 / 495 方法）：调度循环（tick 顺序）、快照合并（数据源→合并快照）、左侧双环绘制、时钟盘/LED 列绘制、额度区绘制各成一个 partial。**仅移动代码，不改任何逻辑**。

### 2.7 后续整合项（本 SPEC 内为收尾任务，允许分批执行）

- 双雷达渲染组件归一（额度环/IQ 时钟盘/服务 LED 列/底部元信息行做成"快照 + 矩形 → 绘制"的独立组件，CodexRadarForm 与 ClaudeRadarForm 退化为布局壳）——T7。
- `WidgetSettings` 拆域（按窗口域拆 partial 文件，`settings.ini` 键名与格式零变化）——T8。

---

## 3. 任务规格

> 通用禁止事项（适用于所有任务）：
> - 禁止改动 AGENTS.md Runtime Invariants 全部条目；禁止编译/发布 x64；禁止恢复 Dock/Launchpad/顶栏/Direct2D。
> - 禁止改变 `settings.ini` 已有键名与已有语义（新增键允许，删除/改名/改语义不允许）。
> - 纯重构任务（T1/T4/T5/T6/T7/T8）禁止改变任何可见像素（以 Compare-RenderSamples 判定）。
> - 每个任务完成后必须：更新 `Docs/Maintenance/CHANGELOG.jsonl`（一事一条）、按需更新 FEATURE/INTERFACE INDEX 与受影响活文档（刷新"适用版本"行）、跑 `Docs/validate_docs.py` 全绿、独立 git 提交。
> - 涉及运行时行为的任务按 AGENTS.md 默认部署规则执行：构建 ARM64 → 备份正式 exe → 覆盖 → 重启，并在 CHANGELOG 记 deployment 条目。

### T0 — 基线固化（必须最先执行）

前置：`git status --porcelain` 确认工作区干净（脏则先完成同步提交）。

步骤：
1. 构建 `_build\spec-wu\t0-baseline.exe`；构建输出存 `_build\spec-wu\baseline\build-warnings.txt` 并统计 warning 行数。
2. 跑 §0.5 全部自检（含 `--test`）输出存 `_build\spec-wu\baseline\selftest\`。
3. 跑 §0.6 全部 9 组渲染，`<tag>=baseline`。
4. 用 baseline exe 实测记录 §2.3 需要的全部策略数值组合（写入 `_build\spec-wu\baseline\policy-values.md`，作为 T1 字面量断言的抄录源）。
5. 若项目内无 `Docs/validate_docs.py`，从 doc-governance skill 复制并提交。

**验收（Gate）**：
- [ ] 9 个渲染子目录每个至少 1 个 PNG；全部自检 PASS；`policy-values.md` 覆盖 9 个函数 × 3 档（网络两函数另 × 全部 `NetworkAccessState`）。
- [ ] `python Docs\validate_docs.py --root .` 全绿。

### T1 — 电源模式策略中心化（§2.3）

步骤：
1. 新建 `Settings/WidgetRefreshPolicy.cs`，数值从 `policy-values.md` 抄录入表。
2. `WidgetSettings` 的 9 个 `Get*` + `ShouldEnableProcessPowerSaving` 改为薄委托；删除函数体内原字面量分支。
3. 新增 `--test-refresh-policy` 自检并挂入 `--test` 聚合；断言字面量来自 `policy-values.md`。
4. `Docs/Component-Refresh-Rules.md` §2 追加代码事实源指针；INTERFACE_INDEX 登记新自检命令；FEATURE_INDEX 视情况更新。

**禁止**：改动任何数值；改动 `GetEffectivePerformanceMode` 的缓存与映射；触碰任何调用方。

**验收（Gate）**：
- [ ] `--test-refresh-policy` PASS 且断言条数 ≥ 3 档 × 9 函数 + 网络状态组合全覆盖。
- [ ] `grep -n "== WidgetPerformanceMode.Smooth" Settings/WidgetSettings.cs` 中间隔函数区域零命中（字面量分支已清除；`GetEffectivePerformanceMode` 与语义判断除外）。
- [ ] 全量自检 PASS；渲染 9 组 vs baseline `RESULT: PASS`（期望 0 差异）。
- [ ] 回滚方案：`git revert` 单提交即可（无设置键变化）。

### T2 — 全局整体透明度对所有窗口生效（bug 修复，§2.1）

步骤：
1. 在 `LayeredWidgetFormBase` 落地 §2.1 生效值管线（`GetApplicationOpacityAlpha` 收为基类实现 + 两个虚钩子）。
2. 已消费窗体（WidgetForm/CodexRadar/ClaudeRadar/NetworkMonitor/PowerThermal/ConnectionCheck）：删除各自 `GetApplicationOpacityAlpha` override，悬停逻辑暂以 `ApplyHoverAlpha` override 保留原实现（T4 再收驱动器）。
3. 未接入窗体逐个接入：OperationForm（其 `GetLayeredWindowOpacityAlpha` 与全局值做 alpha 合成，保持既有操作面板语义叠加而非替换）、OperationQuickGridForm、OperationLauncherTrioForm、OperationCodexTaskBoardForm、SpecBoardForm、EdgeDockTabForm（跟随宿主看板生效值）。
4. 同步 `Docs/SpecBoard-Architecture.md`、`Docs/CodexRadar-Architecture.md` 等受影响活文档的透明度描述。

**禁止**：改变默认渲染结果（全局默认值为 0，默认设置下像素必须逐字节不变）；改动背景透明度键的语义。

**验收（Gate）**：
- [ ] 默认设置下渲染 9 组 vs baseline `RESULT: PASS`（期望 0 差异）。
- [ ] 行为验证：临时设置 `ApplicationTransparencyPercent=50` 重新渲染 9 组，**每一组** PNG 与 baseline 对比均出现差异（证明全局值现在对所有窗口生效），验证输出存 `_build\spec-wu\t2\opacity-50-proof.txt`；验证后恢复设置。
- [ ] `grep -rn "override byte GetApplicationOpacityAlpha" Core` 零命中（管线已收敛）。
- [ ] 全量自检 PASS；部署后正式 exe 运行 ≥ 10 分钟无 error.log 新增。

### T3 — 每窗口透明度覆盖设置（§2.2）

步骤：
1. 按 §2.2 表新增 9 个 `-1..90` 设置键，走 AGENTS.md 新设置全链路（defaults=-1/clone/load/save/normalize/UI/migration/`--test-settings-bindings`）。
2. 各窗体 `WindowTransparencyOverridePercent` override 返回对应键值；停靠 tab 读宿主看板键。
3. 设置 UI 各分区追加行与帮助文案；`Win11SettingsForm` 行高按 AGENTS.md 规则用实测字体高度累加，禁止手写固定 Y。
4. 同步各窗口架构活文档与 FEATURE_INDEX（每窗口条目补设置键）。

**验收（Gate）**：
- [ ] `--test-settings-bindings`、`--test-settings-open-close` PASS（含新键 round-trip）。
- [ ] 默认设置（全部 -1）下渲染 9 组 vs baseline `RESULT: PASS`。
- [ ] 行为验证：仅设 `SpecBoardTransparencyOverridePercent=60`，渲染证明 SpecBoard 与其停靠 tab 变化而其余 7 组 0 差异；证据存 `_build\spec-wu\t3\override-proof.txt`。
- [ ] 部署后在正式运行时人工可调（设置窗口出现 9 行新设置）。

### T4 — 生命周期第二次上收（§2.4）

步骤：
1. 先产出差异对照表 `_build\spec-wu\t4\diff-matrix.md`：对 §1.2 每个重复方法名，逐窗体列出实现差异点，标注"可上收公共骨架 / 窗体私有保留"。**对照表未完成禁止动代码。**
2. 新建 `Core/WidgetLifecycleController.cs` 与基类钩子；随后按"主窗口最后、简单窗体先行"顺序逐窗体迁移（建议：ConnectionCheck → PowerThermal → NetworkMonitor → SpecBoard/EdgeDockTab → ClaudeRadar → Operation（含子窗） → CodexRadar → WidgetForm），每窗体独立提交。
3. 迁移悬停动画驱动器时，T2 留下的各窗体 `ApplyHoverAlpha` override 收敛为控制器统一实现，窗体侧删除副本。
4. 更新 INTERFACE_INDEX（新增 WidgetLifecycleController 条目）与 `Docs/Component-Refresh-Rules.md` 受影响小节。

**禁止**：改变 `CreateParams` 位组合、暂停/恢复顺序、烧屏 salt、悬停 5% 目标 alpha 等任何行为语义；在对照表标注"窗体私有"的差异点上强行上收。

**验收（Gate，每窗体迁移后 + 全部完成后各一次）**：
- [ ] 渲染 9 组 vs baseline `RESULT: PASS`（期望 0 差异）。
- [ ] `--test-radar-display-lifecycle --iterations 100` 三个资源 delta ≤ baseline 各自数值（逐项比较，不得变差）；`--test-display-recovery` PASS。
- [ ] 全部完成后：§1.2 表中"可上收"标注的方法在各窗体的私有副本清零（以对照表逐项 grep 验证，结果存 `_build\spec-wu\t4\dedup-proof.txt`）。
- [ ] 部署后正式 exe 运行 ≥ 30 分钟，无 error.log 新增，`ui-hang-watchdog.jsonl` 无新记录。

### T5 — DrawFittedText 统一（§2.5）

步骤：共享实现进 `Core/DrawingUtil.cs`；4 份私有副本逐一迁移删除；`grep -rn "DrawFittedText" Core` 仅剩共享实现与调用点。

**验收（Gate）**：渲染 9 组 vs baseline `RESULT: PASS`（0 差异）；全量自检 PASS。

### T6 — CodexRadarForm 机械拆分（§2.6）

步骤：按 §2.6 目标 partial 逐块移动代码（每块一个提交）；仅移动与访问级别最小调整，零逻辑改动。

**验收（Gate）**：
- [ ] `Core/CodexRadarForm.cs` 主文件 ≤ 9,000 行；每个新 partial ≤ 4,000 行且职责单一（文件头一句话注明职责）。
- [ ] 渲染（codex 组全部变体）vs baseline `RESULT: PASS`（0 差异）；全量自检 PASS。
- [ ] `Docs/CodexRadar-Architecture.md` §1 文件清单更新。

### T7 — 双雷达渲染组件归一（§2.7，允许另立子批次执行）

步骤：按"额度环 → 时钟盘 → LED 列 → 底部元信息行"顺序逐组件提取（每组件一个提交），两窗体改为调用组件；组件放 `Core/`，命名沿用 `ClaudeQuotaRingShared` 先例。

**验收（Gate）**：每组件提取后 codex + claude 两组渲染 vs baseline `RESULT: PASS`（0 差异）；全部完成后两窗体中对应绘制私有方法清零。

### T8 — WidgetSettings 拆域（§2.7，允许另立子批次执行）

步骤：把 `Settings/WidgetSettings.cs` 按域拆为 partial 文件（如 `WidgetSettings.Radar.cs`、`WidgetSettings.Network.cs`、`WidgetSettings.Operation.cs`、`WidgetSettings.Appearance.cs`、`WidgetSettings.Policy.cs`），类名不变、键名不变、`settings.ini` 字节级格式不变。

**验收（Gate）**：
- [ ] `--test-settings-bindings`、`--test-settings-open-close` PASS；用 T0 的 settings.ini 快照做 load→save round-trip，输出与拆分前逐字节一致（证据存 `_build\spec-wu\t8\roundtrip-proof.txt`）。
- [ ] 单文件 ≤ 3,000 行。

---

## 4. 最终 Gate（全部任务完成后）

- [ ] §0.5 全部自检（含 `--test` 与新增 `--test-refresh-policy`）PASS。
- [ ] 默认设置下渲染 9 组 vs baseline `RESULT: PASS`。
- [ ] `Docs/Component-Refresh-Rules.md`、各窗口架构活文档"适用版本"行已刷新；FEATURE/INTERFACE INDEX 与 CHANGELOG 完整；`Docs/validate_docs.py` 全绿。
- [ ] 正式 ARM64 部署完成（AGENTS.md 部署规则），CHANGELOG 含 deployment 条目，运行 ≥ 30 分钟无 error.log 新增。
- [ ] Spec Board 账本推进到 awaiting_verify。
