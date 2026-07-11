# Radar Clock Dial Consolidation SPEC

## Metadata

- Document: `Codex-RadarClockDialConsolidation-SPEC-v1.0.5.01-20260710-235413.md`
- Generated model: Claude Fable 5
- Timestamp UTC: `2026-07-10T14:54:13Z`
- Timestamp local: `2026-07-10T23:54:13+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.01`
- Target implementation version: `1.0.5.03`（建议在 `1.0.5.02` 模型目录加固之后实施；两者代码面基本不重叠，可独立排期）
- Status: approved（用户已确认：只整合代码、两窗合用一套；视觉优化项 V2、V3 获批纳入实现，V1 不采纳）
- Decision log: 2026-07-11 用户批复"做v2和v3"；V1（标记点去重）不采纳，环内其余元素保持现状

## Goal

把 Radar 时钟表盘的状态判定、几何计算和绘制原语收编为一个共享模块 `RadarClockDial`，消除 CodexRadarForm 与 ClaudeRadarForm 之间约 12 个方法的镜像复制，删除死代码，使"时钟一共有几种状态、每种状态画什么"在一个文件里一眼可读。

硬约束：

- 渲染结果除两处已获批的视觉变更（V2 顶部刻度改中性白、V3 缺失状态底环改低透明度红）外逐像素不变（环、弧、点、徽标、中心三行文字全部保持现状）。
- 周期时长保持调用方参数：Codex 表盘 12 小时，Claude 表盘（共享窗口 Claude 模式与独立窗口）24 小时。不得在共享模块内写死。
- 速蹬倒计时表盘仍为 Codex 专属叠加层，语义不变，仅复用共享绘制原语。
- 不改任何设置项、枚举（`RadarClockTimeDisplayMode` 等）、设置绑定和文件格式。

## Requirement Inventory（现状逻辑反推的需求清单）

这是共享模块必须原样承载的行为，也是实现后的对照验收基准。

### R1 互斥主状态（5 种）

| 状态 | 触发条件 | 环显示 | 现实现位置 |
| --- | --- | --- | --- |
| 速蹬倒计时（仅 Codex） | 窗口开 + `closed_at` 已知且未到 + `CodexRadarSpeedWindowCountdownEnabled` | 天蓝消退环（剩余/总时长）+ 日期 + `HH:MM` 倒计时 + `RST`，整体取代时钟 | `CodexRadarForm.EvenRow.cs` `DrawEvenRowSpeedWindowCountdownDial` |
| 本周期已更新 | IQ 数据时间 ≥ 当前周期边界 | 绿色；仅当刷新标记可见时画"标记→当前时刻"弧，否则无弧 | 两窗 `ComputeXxxDialStatusColor` 绿分支 |
| 等待本周期 | IQ 数据落在上一周期 | 黄色；画"周期边界→当前时刻"已流逝弧 | `IsXxxDialWaitingForCurrentCycle` |
| 整周期缺失 | IQ 数据早于上一周期 | 红色已流逝弧（最小 2°）+ 全圈黄色警告底环；同时是模型自动切换的触发条件 | `IsXxxDialOverdue` |
| 无数据 | 批次与本地刷新时间均未知 | 灰色，无弧 | `ComputeXxxDialStatusColor` 灰分支 |

### R2 正交叠加项（3 项）

1. **请求中闪烁**：radar 请求进行中时，状态色在偶数渲染帧降到 alpha 104。
2. **晚间第二次测试徽标**：IQ 数据标签后缀解析出 `pm2`/`pm_2`/`pm-2` 时，顶部边界标记内侧画警告色小"2"。
3. **固定装饰**：白色 alpha 46 底环轨道、顶部绿色边界刻度、绿色刷新标记圆点（上次实际 IQ 刷新时间在本圈龄内时可见）、白色当前时刻圆点。

### R3 中心三行文字

1. 日期行：IQ 数据标签主段，`7.6`/`7/6` 格式化为 `7月6日`。
2. 时间行：按 `RadarClockTimeDisplayMode` 四选一——UTC 当前 / 本机当前(NOW) / 上次尝试刷新(LAST) / 上次实际刷新(REF)，未知显示 `--:--`。
3. 模式行：对应短标签 `UTC` / `NOW` / `LAST` / `REF`。

### R4 周期几何

- 周期边界：`cycleHours >= 23.5` 取当日 0 点；否则取 `now.Hour` 向下对齐到周期整数倍小时。
- 指针从顶部（-90°）顺时针，角度 = 流逝小时 / 周期 × 360°。
- 刷新标记角度：标记时间龄 ∈ [0, cycleHours) 才可见，角度对边界取模归一。

### R5 与自动切换的耦合

`ApplyRadarClockAutoSwitchIfNeeded`（Codex 共享窗）与 `ApplyClaudeRadarClockAutoSwitchIfNeeded`（独立 Claude 窗）使用与表盘相同的周期边界数学判定"当前模型数据是否早于上一周期边界"。整合后两者必须调用共享模块的同一个边界函数，保证表盘变红与自动切换触发永远同源。

## 现状问题清单（整合动机）

1. **双份镜像**：`ClaudeRadarForm.cs` 完整复制了 EvenRow 的时钟实现（`ComputeClaudeEvenRowDialStatusColor`、`IsClaudeEvenRowDialOverdue`、`IsClaudeEvenRowDialWaitingForCurrentCycle`、`GetClaudeEvenRowDialCycleBoundaryLocal`、`TryGetClaudeEvenRowClockMarkerAngle`、`ComputeClaudeEvenRowClockSweep`、`DrawClaudeEvenRowClockDot`、`DrawClaudeEvenRowClockBoundaryTick`、`FormatClaudeEvenRowDialDate`、`SplitClaude*HeroLabel`、`ParseClaudeEvenRowBatchSuffix`、`GetClaudeEvenRowDialModeLabel`/`TimeText` 等），仅前缀不同。
2. **状态判定三次平行计算**：`ComputeEvenRowDialStatusColor`（颜色）、`IsEvenRowDialOverdue`（红）、`IsEvenRowDialWaitingForCurrentCycle`（黄）各自独立算一遍边界并比较，三处必须永远一致却没有任何机制保证。
3. **死代码**：
   - `DrawEvenRowBatchDial` 开头的 `GetCodexModelIqUpdateStatusText` 调用产出 `updateText`/`legacyUpdateColor`，二者在方法内再未使用（Codex 侧 `CodexRadarForm.EvenRow.cs:334-336`）。
   - `TryGetEvenRowBatchHour`（及 Claude 镜像若存在）全仓库无调用者——旧版"批次标记点"残留。
   - `ParseEvenRowBatchSuffix` 的 4 个输出中表盘只消费 `secondRun`；`phaseKnown`/`night`/`suffixTimeText` 仅为已死的 `TryGetEvenRowBatchHour` 服务。
4. **状态、几何、绘制混编**：两窗的 `DrawXxxEvenRowBatchDial` 各约 180 行，一个方法内完成状态判定 + 角度计算 + GDI 绘制，无处可一眼看出"共 5 种状态"。

## Target Architecture

新增单一文件 `Core/RadarClockDial.cs`，含四层，全部 `internal static`（绘制上下文除外）：

### 1. 状态机（唯一含业务分支的地方）

```csharp
internal enum RadarClockDialPhase
{
    NoData,        // 灰
    CurrentCycle,  // 绿
    WaitingCycle,  // 黄
    MissedCycle    // 红 + 警告底环
}
```

`ComputePhase(batchKnown, batchTime, localKnown, cycleHours, now)` 一次算出边界并返回唯一 Phase；颜色由 `GetPhaseColor(phase)` 单一 switch 给出。现有三个平行判定函数全部废除——颜色、黄弧、红环从此不可能不一致。

### 2. 状态快照（每帧一算，纯数据）

```csharp
internal sealed class RadarClockDialState
{
    public RadarClockDialPhase Phase;
    public Color StatusColor;          // 已含闪烁衰减
    public float CurrentAngle;         // 当前时刻指针角
    public bool RefreshMarkerVisible;
    public float RefreshMarkerAngle;
    public float ArcStartAngle;        // 弧线规则算好后的最终值
    public float ArcSweepDegrees;
    public bool WarningRingVisible;    // MissedCycle 专属
    public bool SecondRunBadge;
    public string DateText;            // 已格式化 "7月6日"
    public string TimeText;            // 已按显示模式解析
    public string ModeLabel;           // UTC/NOW/LAST/REF
}
```

`ComputeState(in RadarClockDialInput input)` 为纯函数：输入含 batch/local 时间、cycleHours、now、requestRunning、renderTick 奇偶、数据标签原文、显示模式及各模式候选时间戳。窗体字段（如 `lastCodexRadarStatusAttemptLocal`）由各窗体取好后作为入参传入，模块不回访窗体。

弧线规则原样收编：标记可见 → 标记→当前弧；否则 Waiting/Missed → 边界→当前弧；否则无弧；Missed 额外全圈警告环且弧最小 2°。

### 3. 绘制（无任何 if 业务分支，只按 State 画）

```csharp
internal sealed class RadarClockDialDrawContext
{
    public float LayerScale;
    public Font DayFont;
    public Font TimeFont;
    public Font ModeFont;
    public Action<Graphics, string, Font, Brush, RectangleF, StringAlignment, float> DrawFittedText;
}

public static void Draw(Graphics g, RectangleF rect, RadarClockDialState state, RadarClockDialDrawContext ctx);
```

字体与 shrink-to-fit 文本由各窗体经上下文注入（保持各窗体 fontCache 与 `DrawXxxFittedText` 现状，遵守"测量宽度必须等于绘制宽度"的既有约束）。几何原语 `DrawDot` / `DrawBoundaryTick` / `NormalizeSweep` / `GetMarkerAngle` 设为 `internal static`，供速蹬倒计时表盘直接复用。

### 4. 自测

`RadarClockDial.RunSelfTest()` 收编现有 `RunClaudeEvenRowDialFreshnessSelfTest` 并扩展为 12h + 24h 双周期真值表（见验收）。原调用点（`ClaudeRadarForm.cs` 自测链）替换为对共享自测的调用；`RadarSoftwareModeController.RunSelfTest()` 末尾追加同一调用，保证 `--test` 两条路径都覆盖。

### 窗体侧收窄后的样子

- `CodexRadarForm.EvenRow.cs` `DrawEvenRowBatchDial`：速蹬倒计时门 → 组装 `RadarClockDialInput`（cycleHours=12，共享窗 Claude 模式=24）→ `ComputeState` → `Draw`。预计 180 行缩到约 40 行。
- `ClaudeRadarForm.cs` `DrawClaudeEvenRowBatchDial`：同构，cycleHours=24。12 个镜像方法整体删除。
- 两窗自动切换的边界判定改调 `RadarClockDial.GetCycleBoundaryLocal`。

## Deletions

| 删除项 | 位置 | 依据 |
| --- | --- | --- |
| `updateText`/`legacyUpdateColor` 计算块 | `CodexRadarForm.EvenRow.cs` 表盘方法开头 | 产出无消费者（`GetCodexModelIqUpdateStatusText` 本身保留，通知链路仍在用则不动；仅删表盘内的调用） |
| `TryGetEvenRowBatchHour` + Claude 镜像 | 两窗 | 全仓库无调用者，实现前以 `rg` 复核 |
| `ParseEvenRowBatchSuffix` 收窄为 `HasSecondRunSuffix(suffix) -> bool` | 共享模块 | 表盘仅消费 secondRun；`am/pm/HH:mm` 后缀解析随死代码一并退役 |
| Claude 侧 12 个镜像方法 | `ClaudeRadarForm.cs` | 由共享模块替代 |
| `RunClaudeEvenRowDialFreshnessSelfTest` | `ClaudeRadarForm.cs` | 移入 `RadarClockDial.RunSelfTest` 且断言只增不减 |

约束：删除前逐项 `rg` 确认无其他调用者；`GetCodexModelIqUpdateStatusText`、`GetCodexModelIqDataLabelDisplayText` 等仍被通知/其他面板使用的函数一律保留。

## Visual Refinements（V2、V3 已获批，纳入实现范围；V1 不采纳）

- **V1 标记点与指针点重叠去噪**：不采纳。绿色刷新标记点与白色当前时刻点的重叠保持现状。
- **V2 顶部边界刻度改中性色**（已批准）：边界刻度现恒为 Success 绿，即使表盘处于红/灰状态——绿色因此有两个含义（"周期起点"与"数据新鲜"）。改为 `DesignTokens.White(170)`，让绿色专属"本周期已更新"。作用范围：两窗批次时钟的 `DrawEvenRowClockBoundaryTick` 调用点；速蹬倒计时表盘的顶部刻度本就使用倒计时天蓝色，不受影响。绿色刷新标记圆点保持 Success 绿不变。
- **V3 缺失状态单色化**（已批准）：现状是全圈黄色警告环（Warning alpha 220）上叠状态色已流逝弧，黄红混排传达两种紧急度。改为底环 `DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 90)`，已流逝弧保持 Danger alpha 245 高亮红——一种颜色一个含义（红=缺数据）。最小 2° 弧、全圈底环的几何均不变，仅换色。

V2/V3 属于既定视觉变更，不适用 A4 的"逐像素不变"要求；两项差异必须在 CHANGELOG change 记录中逐项声明，并重新截取受影响的渲染样本作为新基准。

> 状态（2026-07-11）：本 SPEC 的 V1-V3 范围已于 1.0.5.03 执行部署完毕。下方 V4/V5 为执行后追加的遗留项，已移入合并版 `Codex-RadarHardeningConsolidated-SPEC-v1.0.5.03-20260711-094500.md`（第三部分 Phase C1/C2），此处保留原文供追溯。

- **V4 状态色阶梯整体后移一个窗口（待用户批准，2026-07-11 提出）**：实测（2026-07-11）站点的发布节奏是"窗口 N 的数据在窗口 N+1 甚至更晚发布"（7.10 晚间批次次日 08:42 晨才到），导致按现规则"绿=本窗口有数据"几乎不可达——绿点（刷新成功）亮着而中心文字长期黄色，凌晨到清晨常态红色。方案：状态色以"落后窗口数"分档——落后 ≤1 窗口=绿（站点正常节奏）、恰 2 窗口=黄（超出正常滞后，与现有 auto-switch 触发条件 `batch < previousBoundary` 天然对齐）、≥3 窗口=红（真正断更）。真值表：

  | batchTime 相对边界 | 现规则 | V4 规则 |
  | --- | --- | --- |
  | ≥ boundary（本窗口） | 绿 | 绿 |
  | ≥ previousBoundary（落后 1） | 黄 | 绿 |
  | ≥ previousBoundary − cycle（落后 2） | 红 | 黄 |
  | 更早（落后 ≥3） | 红 | 红 |

  批准后 R1 表与 A3 真值表按此更新；黄/红的弧线绘制规则（边界→当前已流逝弧、红态全圈警告环）随档位平移，不另改几何。该项取代此前讨论的"红色改缺两窗"单点方案。
- **V5 数据标签 "n" 后缀兼容（纳入实现）**：站点已把晚间批次标签从 `pm` 改为 `n`（实测 `7.10_n`，JSON 日期解析 `TryReadCodexModelIqDataWindow` 已支持 n→12 点窗口）。但徽标解析 `ParseEvenRowBatchSuffix`（收编后为 `HasSecondRunSuffix`）只认 `pm2/pm_2/pm-2`——晚间第二次测试的"2"徽标在 `n2/n_2/n-2` 标签下会丢。收编时同等识别 n 系后缀；自测补 `n2`→true、`n`→false 断言。观察项：若站点未来同日同时出现 `pm` 与 `n` 两个批次（三窗口制），12 小时周期假设需重估，本 SPEC 不预做。

## Acceptance Criteria

### A1 单一实现

```powershell
rg -n "ComputeClaudeEvenRowDialStatusColor|IsClaudeEvenRowDialOverdue|IsClaudeEvenRowDialWaitingForCurrentCycle|GetClaudeEvenRowDialCycleBoundaryLocal|TryGetClaudeEvenRowClockMarkerAngle|ComputeClaudeEvenRowClockSweep|DrawClaudeEvenRowClockDot|DrawClaudeEvenRowClockBoundaryTick|ParseClaudeEvenRowBatchSuffix|FormatClaudeEvenRowDialDate" Core
rg -n "TryGetEvenRowBatchHour|ComputeEvenRowDialStatusColor|IsEvenRowDialOverdue|IsEvenRowDialWaitingForCurrentCycle" Core
```

两条均应无结果（全部由 `RadarClockDial` 内的单一定义替代）。

```powershell
rg -n "GetCycleBoundaryLocal|ComputePhase|ComputeState" Core\RadarClockDial.cs Core\CodexRadarForm.EvenRow.cs Core\CodexRadarForm.cs Core\ClaudeRadarForm.cs
```

边界函数定义仅出现在 `RadarClockDial.cs`；两窗表盘与两处自动切换均为调用方。

### A2 死代码清零

```powershell
rg -n "updateText|legacyUpdateColor" Core\CodexRadarForm.EvenRow.cs
rg -n "TryGetEvenRowBatchHour|phaseKnown|suffixTimeText" Core
```

第一条无结果；第二条仅允许命中 `RadarClockDial` 自测或注释中的历史说明。

### A3 状态机真值表自测（`RadarClockDial.RunSelfTest`，随 `--test` 执行）

以 `now = 2026-07-07 13:00`（12h 周期，边界 12:00）与 `now = 2026-07-07 13:00`（24h 周期，边界 00:00）分别断言：

| 输入 | 12h 期望 | 24h 期望 |
| --- | --- | --- |
| batch 13:00 当日（≥ 边界） | CurrentCycle / Success | CurrentCycle / Success |
| batch 落在上一周期（12h: 当日 00:00；24h: 前日 00:00） | WaitingCycle / Warning，弧=边界→当前 | WaitingCycle / Warning |
| batch 更早一周期 | MissedCycle / Danger，WarningRing 可见，弧 ≥ 2° | MissedCycle / Danger |
| batch/local 均未知 | NoData / GlyphMuted，无弧 | 同左 |
| 标记时间龄 ≥ cycleHours | RefreshMarkerVisible=false | 同左 |
| 标记跨边界角度归一 | 角度 ∈ [-90°, 270°)，扫角 ∈ [0°, 360°] | 同左 |
| `pm2`/`pm_2`/`pm-2` 后缀 | SecondRunBadge=true；`am`/`pm`/`07:30` 后缀=false | 同左 |
| requestRunning + 偶数帧 | StatusColor alpha=104 | 同左 |

原 `RunClaudeEvenRowDialFreshnessSelfTest` 的既有断言必须全部保留（可等价改写）。

### A4 渲染等价（含 V2/V3 例外）

- `--render-codexradar` 与 `--render-clauderadar` 全部样本中，时钟表盘区域与实现前构建的差异**只允许**是：① 顶部边界刻度由绿变中性白（V2）；② 缺失状态底环由黄变低透明度红（V3）。其余元素（弧线起止、点位、徽标、中心三行文字布局）肉眼无差异；中心时间行随真实时钟变化属预期。`png-sample-ok` 计数与实现前一致。
- 速蹬倒计时两张样本（enabled/disabled）无回归（其顶部刻度为天蓝色，不受 V2 影响）。
- V2/V3 的视觉差异在 CHANGELOG change 记录中逐项声明，新样本作为后续基准。

### A5 标准验证矩阵

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-radar-clock-dial-v1.0.5.03.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-radar-clock-dial-v1.0.5.03.exe --test
.\_build\DesktopCodexAssistant-arm64-radar-clock-dial-v1.0.5.03.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-radar-clock-dial-v1.0.5.03.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-radar-clock-dial-v1.0.5.03.exe --test-radar-display-lifecycle --iterations 100
```

全部通过后按正式部署流程（Release 构建复测 → 备份 → D/E 双路径部署 → 重启 D 实例 → SHA256/版本三方核对 → CHANGELOG deploy 记录）。

### A6 版本与文档同步

- `Core/ProductIdentity.cs`、`AGENTS.md` → `1.0.5.03`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl` 新增 `internal_api.radar_clock_dial`（状态机、绘制上下文、自测入口）
- `Docs/CodexRadar-Architecture.md` / `Docs/Codex-ClaudeRadar-Architecture.md` 时钟章节改述为"共享 RadarClockDial 状态机"
- `Docs/Maintenance/CHANGELOG.jsonl` change + deploy 记录

## Risks And Mitigations

| 风险 | 缓解 |
| --- | --- |
| 两窗现有实现存在未被察觉的细微差异，合并后其一变样 | 实施前先对两窗 12 个镜像方法做逐行 diff，任何差异列入 SPEC 附录并逐条决定保留哪侧语义，禁止隐式取舍 |
| shrink-to-fit 文本回归（历史教训：测量宽度 ≠ 绘制宽度） | 文本绘制经上下文委托回各窗体现有 `DrawXxxFittedText`，共享模块不自建文本测量 |
| 自测覆盖缩水 | A3 明确"原断言只增不减"；`--test` 双路径调用 |
| 与 1.0.5.02 目录加固改动冲突 | 两 SPEC 文件面仅 `CodexRadarForm.EvenRow.cs` 轻度相邻（短标签函数不在本次收编范围），按版本顺序串行实施 |

## Acceptance Summary

| 项 | 验收条件 |
| --- | --- |
| 单一实现 | A1 三组 rg 全部符合；Claude 侧 12 个镜像方法清零 |
| 死代码 | A2 两组 rg 符合 |
| 状态机正确性 | A3 真值表（12h+24h）+ 原 Claude 自测断言全部通过 |
| 视觉等价 | A4 渲染样本仅含 V2/V3 两处声明差异，其余无差异 |
| 回归 | A5 全套测试通过 + 正式部署三方一致 |
| 文档 | A6 索引与架构文档同步 |
