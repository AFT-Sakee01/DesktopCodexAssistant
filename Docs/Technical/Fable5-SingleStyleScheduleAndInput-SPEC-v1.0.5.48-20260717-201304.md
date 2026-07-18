# Fable5-SingleStyleScheduleAndInput-Spec — 单一风格收敛、夜间时段策略、每窗口缩放、通知开关与全局热键执行规格

生成时间：2026-07-17 20:13（Asia/Tokyo）。基线：`ProductIdentity.Version = 1.0.5.48`，git HEAD `e7154c1`。

本文档面向**执行 AI**。验收标准全部是硬性 Gate，任何一条不满足即判定任务失败，必须回滚后重试或上报。

用户已裁决的设计决策（不得偏离、不得重新征询）：

1. **不做全局主题切换开关**；渲染风格收敛为**每窗口仅保留当前实际使用的一种**，其余风格代码删除。
2. 采纳并实施：夜间定时降亮 + 勿扰时段、每窗口缩放覆盖、通知分类开关、全局热键。
3. 设置配置档（profiles/presets）**不做**（夜间时段策略已覆盖主要场景）。

## 与 WindowUnificationAndPolicy SPEC 的关系（重要）

本 SPEC 与 `Docs/Technical/Fable5-WindowUnificationAndPolicy-SPEC-v1.0.5.48-20260717-194305.md`（下称 WU-SPEC，当前 pending）并行登记。执行顺序约束：

- **V1（风格收敛）不依赖 WU-SPEC，且建议排在 WU-SPEC T4/T6/T7 之前执行**——先删掉不可达绘制路径能显著缩小生命周期迁移与 CodexRadarForm 拆分的工作面。
- **V2（夜间降亮）与 V3（每窗口缩放）依赖 WU-SPEC T2 完成**（基类统一透明度生效管线是它们的挂载点）。在 T2 完成前禁止开始 V2/V3。
- V4/V5 无依赖，可任意排期。
- 两份 SPEC 的通用禁止事项、构建/自检/渲染/文档 Gate 完全沿用 WU-SPEC §0（含 `Docs/validate_docs.py` 全绿要求），本文不重复，执行前必须先读 WU-SPEC §0。

---

## 1. 现状事实（2026-07-17 审查）

### 1.1 渲染风格残留面

- 1.0.4.56 已把 MainWidget/NetworkMonitor/PowerThermal/ConnectionCheck 四窗硬编码为 Classic，枚举保留单成员以维持 settings.ini 兼容（`Settings/WidgetSettings.cs` 各枚举注释为证）。`CodexRadarRenderVariant` 亦只剩 `EvenRow`。**这些窗口无需再动。**
- 唯一残留多风格处：`OperationRenderVariant { Classic, Typographic, AmberHud, WarmCard, Phosphor, RadialDial }`。用户 settings.ini 实际值为 `RadialDial`（非 paint-only：含独立命中测试与窗口尺寸逻辑，见 `Core/OperationForm.RadialDial.cs`）。
- 挂在 Operation 变体上的文件：`OperationForm.Typographic.cs` / `OperationForm.AmberHud.cs` / `OperationForm.WarmCard.cs` / `OperationForm.Phosphor.cs`（4 个薄 stub）、`OperationForm.OledShared.cs`（267 行共享绘制）、`OperationForm.cs` 内 Classic 平铺网格绘制路径与变体分发 switch、`OperationForm.RenderSample.cs` 的逐变体样张输出。
- 间接消费者：`Core/SpecBoardForm.cs` 的 `SpecBoardPalette` 按 `OperationRenderVariant` 四个 OLED 成员选调色（当前值 RadialDial 走默认分支，即**删除这些分支不改变当前像素**）；`Core/OledVariantPainting.cs`（229 行）与 `Core/DesignTokens.cs` 的 `Oled*` 调色 token 被 SpecBoard/SpecBoardManager/Win11SettingsForm 的**固定主题**继续消费——属共享设计语言，**不是**可切换风格，删除范围必须排除仍有消费者的成员。

### 1.2 时段策略、缩放、通知、热键现状

- 无任何按时间的策略设置（无夜间时段、无降亮系数、无勿扰）。烧屏防护全部为事件驱动（`BurnInProtection` 微位移 + 隐藏模式低亮度色变换 `ApplyHiddenModeColorProtection`，后者即现成的全窗位图降亮变换）。
- 缩放仅有全局 `ResolutionCompatibilityScalePercent`（经 `LayeredWidgetFormBase.ApplyLayerScaleFromSettings` 进入 `LayerScale`）与 CodexRadar 专属 `CodexRadarManualRingScalePercent` / `CodexRadarManualTextScalePercent` 两个特例。
- 提醒来源多（额度告警 `AlertPercent`、RSS 重置、Statuspage 服务健康、Codex 任务提醒事件、DeepSeek 余额），但可控开关只有 `AlertPercent` / `AlertIconVisible` / `AlertTestEnabled`，无分类开关。
- 无用户可配置热键（仅内置 `GlobalWinDWatcher`）。

---

## 2. 任务规格

> 通用禁止事项、每任务收尾流程（CHANGELOG 一事一条 / 索引与活文档同步 / `Docs/validate_docs.py` 全绿 / 独立提交 `Spec-SS-V<n>: <一句话>` / 按 AGENTS.md 部署）全部沿用 WU-SPEC §3 引言，另加：
> - settings.ini 已有键**保留可解析**：被删风格的枚举旧值在 load 时静默折算到保留风格（沿用 1.0.4.56 单成员枚举先例），禁止删除键或在旧值上抛异常。
> - 新增设置键一律走 AGENTS.md 全链路（defaults/clone/load/save/normalize/UI/migration/`--test-settings-bindings`）。

### V1 — 渲染风格收敛为单一风格

**目的**：操作面板只保留 RadialDial；删除 Classic 平铺网格与四个 OLED paint 变体的全部不可达路径。

步骤：
1. 前置检测：确认正式运行时 settings.ini `OperationRenderVariant=RadialDial`（若不是，停下向用户报告实际值再定保留对象）。
2. `OperationRenderVariant` 收敛为单成员 `{ RadialDial }`；解析旧值（Classic/Typographic/AmberHud/WarmCard/Phosphor 及任意未知值）一律折算 RadialDial；设置 UI 删除该下拉行。
3. 删除 4 个变体 stub partial；删除 `OperationForm.cs` 与 `OperationForm.RenderSample.cs` 中 Classic 网格绘制与变体分发路径中**仅被被删变体使用**的方法/字段/常量（编译器 + grep 是裁判）。**注意**：RadialDial 复用的任何公共绘制助手保留；QuickGrid/LauncherTrio/CodexTaskBoard 子窗不在删除范围。
4. `SpecBoardForm` 调色：删除按四个 OLED 成员分支的 `SpecBoardPalette` 选择，收敛为当前默认分支的固定调色。
5. `OperationForm.OledShared.cs`、`OledVariantPainting.cs`、`DesignTokens.Oled*`：逐成员判定——失去全部消费者的删除；仍被 SpecBoard/SpecBoardManager/Win11SettingsForm 固定主题消费的保留。判定清单存 `_build\spec-ss\v1\consumer-matrix.md`。
6. 同步 `Docs/Component-Refresh-Rules.md`（如涉及）、操作面板相关活文档、FEATURE_INDEX（变体条目标记 removed）。

**验收（Gate）**：
- [ ] `grep -rn "Typographic\|AmberHud\|WarmCard\|Phosphor" Core Settings` 剩余命中全部在 `consumer-matrix.md` 白名单内（固定主题消费者 + `StringFormat.GenericTypographic` 这类同名无关 API）。
- [ ] 渲染对比：`--render-operation` 与 `--render-specboard sample` 中**保留风格**的 PNG vs 基线 0 差异；被删变体样张从输出消失且清单与本任务列表一致（证据存 `_build\spec-ss\v1\removed-samples.txt`）。其余 7 组渲染 vs 基线 `RESULT: PASS`。
- [ ] 用含 `OperationRenderVariant=Classic` 的临时 settings.ini 启动折算验证：加载后 Save 回写为 RadialDial，无异常日志。
- [ ] 全量自检 PASS；`--test-operation-panel` PASS；部署后正式 exe 运行 ≥ 10 分钟无 error.log 新增。
- [ ] `Core/OperationForm.cs` 行数净减 ≥ 400 行（未达标说明 Classic 路径没删干净，重查）。

### V2 — 夜间定时降亮 + 勿扰时段（依赖 WU-SPEC T2）

**目的**：在每日固定时段自动降低全部窗口亮度并静默提醒。

新增设置键：

| 键 | 类型/范围 | 默认 |
|---|---|---|
| `NightScheduleEnabled` | bool | false |
| `NightScheduleStartMinutes` / `NightScheduleEndMinutes` | int 0..1439（本地时间自午夜分钟数，允许跨午夜区间） | 1380（23:00）/ 420（07:00） |
| `NightDimLuminancePercent` | int 10..100（100=不降亮） | 60 |
| `NightQuietHoursEnabled` | bool（时段内静默 V4 全部分类提醒） | true |

步骤：
1. 判定器进 `Core/BurnInProtection.cs` 旁新文件 `Core/NightScheduleController.cs`：纯函数 `IsInNightWindow(settings, DateTime localNow)`（跨午夜区间正确处理），由各窗口既有 tick 复用查询，**不新增定时器**。
2. 降亮实现挂在 `LayeredWidgetFormBase` 渲染管线：夜间窗口内对渲染位图应用亮度系数变换（实现方式沿用 `BurnInProtection.ApplyHiddenModeColorProtection` 的全窗位图变换模式，做成参数化亮度版本）；进入/退出夜间时段各触发一次重绘（复用烧屏 slot 类似的分钟级检查，状态不变不重绘）。
3. 勿扰：夜间窗口内挂起 V4 定义的各分类提醒的**用户可见呈现**（数据采集与状态机不停，只静默展示/闪烁/图标），退出时段后不补发历史提醒。
4. `Docs/Component-Refresh-Rules.md` 新增小节记录判定周期与重绘规则。

**验收（Gate）**：
- [ ] 新键全链路 + `--test-settings-bindings` PASS。
- [ ] 行为验证：临时设 `NightScheduleEnabled=true` 且区间覆盖当前时刻，9 组渲染全部相对基线出现差异且整体变暗（抽样像素亮度均值下降证据存 `_build\spec-ss\v2\dim-proof.txt`）；区间外渲染 0 差异。跨午夜区间（如 23:00–07:00）在 06:59 与 07:01 两个模拟时刻判定结果分别为 true/false（单元断言进自检）。
- [ ] 默认设置（false）下渲染 9 组 vs 基线 `RESULT: PASS`。
- [ ] 全量自检 PASS；部署后跨一次真实时段边界观察降亮切换发生且无 error.log 新增。

### V3 — 每窗口缩放覆盖（依赖 WU-SPEC T2/T3 的覆盖键模型）

**目的**：每窗口可独立缩放，模型与透明度覆盖一致。

新增设置键：`<Window>ScaleOverridePercent`，9 个窗口对象与 WU-SPEC §2.2 表完全一致（停靠 tab 跟随宿主看板），范围 `-1` 或 `40..200`，默认 `-1`（跟随全局 `ResolutionCompatibilityScalePercent`）。

步骤：
1. `LayeredWidgetFormBase.ApplyLayerScaleFromSettings` 扩展：生效缩放 = 覆盖值 ≥ 0 ? 覆盖值 : 全局兼容缩放，经由与 WU-SPEC T3 同构的虚钩子提供；`SetLayerScale` 既有 0.25 下限与失效重建逻辑不变。
2. 窗口尺寸/定位随缩放变化的窗口（各窗体已依赖 `S()` 缩放）逐一验证防遮挡与工作区钳制仍成立。
3. CodexRadar 专属 Ring/Text 手动缩放两键**保留**，语义为"在生效窗口缩放之上的元素级微调"，文档写明叠加关系。
4. 设置 UI 每窗口分区追加一行；帮助文案写明 −1 = 跟随全局。

**验收（Gate）**：
- [ ] 新键全链路 + `--test-settings-bindings`、`--test-layout` PASS。
- [ ] 默认（全 -1）渲染 9 组 vs 基线 `RESULT: PASS`。
- [ ] 行为验证：仅设 `NetworkMonitorScaleOverridePercent=150`，网络监控组 PNG 尺寸按比例变大、其余 8 组 0 差异（证据存 `_build\spec-ss\v3\scale-proof.txt`）。
- [ ] `--test-display-recovery` 与 `--test-radar-display-lifecycle --iterations 100` PASS 且资源 delta 不劣于基线。

### V4 — 通知分类开关

新增设置键（bool，默认全 true，保持现状）：`AlertQuotaEnabled`（额度告警，含 `AlertPercent` 阈值告警）、`AlertResetProtectionEnabled`（RSS/重置保护提示）、`AlertServiceHealthEnabled`（Statuspage 服务健康变化提示）、`AlertCodexTaskEnabled`（Codex 任务提醒事件）、`AlertDeepSeekBalanceEnabled`（DeepSeek 余额低提示）。

步骤：各提醒呈现入口（图标闪烁/颜色告警/文本提示）在展示层按分类开关短路——**只拦呈现，不拦数据采集与状态机**；与 V2 勿扰做与运算（任一为静默即静默）。设置 UI 追加"提醒分类"分组。归属判定清单（每个现有提醒点 → 分类）先存 `_build\spec-ss\v4\alert-matrix.md` 再动手。

**验收（Gate）**：
- [ ] 新键全链路 + `--test-settings-bindings` PASS；默认设置渲染 9 组 vs 基线 `RESULT: PASS`。
- [ ] `alert-matrix.md` 覆盖 grep 到的全部提醒呈现点，无"未分类"残留。
- [ ] 行为验证：用测试模式或 `AlertTestEnabled` 触发额度告警，`AlertQuotaEnabled=false` 时呈现消失、true 时恢复（渲染样张对比证据存档）。

### V5 — 用户可配置全局热键

新增设置键（string，Win32 修饰符+键名格式如 `Ctrl+Alt+H`，空 = 未绑定，默认全空保持现状）：`HotkeyToggleAllWindows`（隐藏/显示全部挂件窗口，走既有手动隐藏来源语义）、`HotkeyToggleHoverOpacity`（等价 QuickGrid"悬停透明度"按钮）、`HotkeyOpenSettings`（打开设置窗口）。

步骤：
1. 注册/注销集中在 `WidgetForm`（全局协调者，AGENTS.md 不变量）：`RegisterHotKey` 于句柄创建后与设置热加载时；`WM_HOTKEY` 分发到既有动作入口，禁止新写第二套隐藏/透明度逻辑。
2. 冲突处理：注册失败（被其他程序占用）写一条 INFO 日志并在设置 UI 该行显示失败态，不弹窗、不重试风暴（最多每次设置变更重试一次）。
3. 解析器 + 归一化：无效字符串折算为空；解析断言进 `--test-settings-bindings` 或独立自检。
4. INTERFACE_INDEX 登记热键通道；`Docs/Component-Refresh-Rules.md` 记录注册时机。

**验收（Gate）**：
- [ ] 新键全链路 + 自检 PASS；默认（全空）行为与现状零差异，渲染 9 组 vs 基线 `RESULT: PASS`。
- [ ] 行为验证：部署后实际绑定 `Ctrl+Alt+H` 触发全窗隐藏/恢复各一次，widget.log 出现对应动作日志；解绑后按键无效果。
- [ ] `--test-settings-open-close` PASS（设置窗口开关不泄漏热键注册：重复开关 10 次后 RegisterHotKey 计数与首次一致，证据存 `_build\spec-ss\v5\hotkey-proof.txt`）。

---

## 3. 最终 Gate（全部任务完成后）

- [ ] WU-SPEC §0.5 全量自检 PASS；默认设置下渲染 9 组 vs 各自基线 `RESULT: PASS`。
- [ ] 受影响活文档"适用版本"刷新；FEATURE/INTERFACE INDEX 与 CHANGELOG 完整；`Docs/validate_docs.py` 全绿。
- [ ] 正式 ARM64 部署完成并运行 ≥ 30 分钟无 error.log 新增。
- [ ] Spec Board 账本推进到 awaiting_verify。
