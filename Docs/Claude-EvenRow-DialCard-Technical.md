# Claude-EvenRow-DialCard-Technical.md — EvenRow 表盘状态卡技术说明

> 作者模型：Claude（按项目规则，非 Codex 模型生成的文档使用模型名前缀）
> 适用版本：1.0.4.18
> 适用窗口：CodexRadar EvenRow 变体（`Core/CodexRadarForm.EvenRow.cs`）、独立 Claude Radar EvenRow（`Core/ClaudeRadarForm.cs`）

## 1. 概览

EvenRow 变体右侧的状态区域是**图形化状态卡**，由两部分组成：

```
┌────────────────────────┬─────────┐
│      ╭───────╮         │  ● R    │
│     │  7月6日 │         │  ● O    │  ← 竖排服务LED灯柱（右缘锚定）
│     │  11:36  │         │  ● C    │
│      ╰───────╯         │  ● D    │
│    24小时批次表盘        │         │
└────────────────────────┴─────────┘
```

- **24小时批次表盘**（方案2）：合并"数据批次身份"与"数据新鲜度"两个信息。
- **竖排服务LED灯柱**（方案1竖排变体）：逐服务健康状态，替代原来的轮播告警文字。

整窗布局（从左到右）：5个指标圆环（含下方分隔线+品牌/RC/LLM信息栏）→ 额度雷达线（全高）→ 表盘 → LED灯柱。

## 2. 表盘（Batch Dial）

### 2.1 几何
- 表盘直径 = `min(可用宽度, 窗口内容高度) - S(1)`，即高度受限（默认 522×120 窗口下约 104 物理像素）。
- **左对齐 + 左移**：表盘在其区域内左对齐，并整体左移 `S(3)*0.5`（默认200%缩放下约3物理像素），使其在"额度线—LED灯柱"之间视觉居中。LED灯柱锚定在单元格**右缘**（`cellRect.Right - rightPad - ledColumnWidth`），表盘移动不影响灯柱位置。
- 轨道：整圆，`DesignTokens.White(46)`，线宽 `S(2)`。

### 2.2 批次标记与弧
- **标记点**（白色小圆点，`White(235)`，直径 `S(4)`）落在表盘边缘的批次时刻位置：
  - 24小时映射：0:00 在正上方（-90°），12:00 在正下方，角度 = `小时/24*360-90`。
  - 后缀 `am` → 0:00；`pm` → 12:00；`HH:mm` 形态（如 Claude 标签 "7/6 10:59" 的时间部分）→ 真实钟面位置。
- **晚测第二次**：后缀 `pm2` / `pm_2` / `pm-2`（尾数≥2）时，在标记点内侧绘制黄色小"2"角标（`ParseEvenRowBatchSuffix` / `ParseClaudeEvenRowBatchSuffix`）。
- **弧**：从批次标记顺时针扫到"当前时刻"的钟面位置（`sweep = (now角度 - 批次角度) mod 360`），线宽同轨道、圆头端帽。弧长即"批次距今在钟面上走过的距离"；颜色见 §3。

### 2.3 中心文字
- 上行：批次日期，统一格式化为 **"x月x日"**（`FormatEvenRowDialDate` / `FormatClaudeEvenRowDialDate`：`"7.6"` 或 `"7/6"` → `"7月6日"`；已含"月"或无法解析则原样显示，shrink-to-fit 防溢出）。白色 `White(235)`。
- 下行：最近一次本地成功刷新的 `HH:mm`（取自原"已更新/HH:mm"文字的时间段），颜色跟随 §3 的四级色。
- 中心文字区宽度 = 表盘直径 × 0.72。

### 2.4 批次标签解析链
`GetCodexModelIqDataLabelDisplayText` / `GetClaudeModelIqDataLabelDisplayText`（数据源不变）
→ `SplitEvenRowStatusHeroLabel`：按下划线切分（`"7.6_am"` → 主体+后缀）；无下划线时按"最后一个空格 + 其后含冒号"切分（`"7/6 10:59"` → `"7/6"`+`"10:59"`）；都不匹配则整体作主体。
→ 主体走日期格式化，后缀走 `ParseEvenRowBatchSuffix`（am/pm/pm2/时间/未知）。
未知形态安全回退：主体整体 shrink-fit 显示在表盘中心，无标记点无弧。

## 3. 四级新鲜度颜色规则（用户定义）

实现：`ComputeEvenRowDialStatusColor`（Codex）/ `ComputeClaudeEvenRowDialStatusColor`（Claude），作用于**弧 + 中心时间文字**。

**更新周期**：Codex 一天两测 → `cycle = 12h`；Claude 一天一测 → `cycle = 24h`。

| 优先级 | 条件 | 颜色 |
|---|---|---|
| 1 | 本地"套圈"：本地成功刷新距今 > 1×cycle；或网站"套圈"：批次距今 > 2×cycle | **红** `Danger` |
| 2 | 本地未更新：本地成功刷新距今 > 2小时（应用正常时几分钟就刷一次，超2小时=本地抓取故障） | **橙** `(232,128,54)`（品牌橙，与黄区分） |
| 3 | 网站未更新：批次距今 > 1×cycle（站点没按节奏发布新批次） | **黄** `Warning` |
| 4 | 已更新（批次在周期内且本地抓取健康） | **绿** `Success` |
| — | 批次与本地时间都未知 | 灰 `GlyphMuted` |

- "网站未更新且套了一圈"取 2×cycle：第一个 cycle 过后进入黄（未更新），再整整多错过一个 cycle 才升红。
- 数据来源：
  - Codex 批次时刻 = `ModelIqDataDateLocal.Date + (WindowStartHour>=12 ? 12h : 0h)`（`ModelIqDataDateKnown` 守卫）；本地刷新 = `ModelIqRefreshedAtLocal`（`ModelIqRefreshedAtKnown` 守卫）。
  - Claude 批次时刻 = `GetClaudeLatestMetricLocalTime(local)`；本地刷新 = `local.CheckedAtLocal`。
- 刷新请求进行中沿用原闪烁机制（`renderTickCount` 奇偶帧 alpha 104）。

## 4. 服务 LED 灯柱

- 竖排，右缘锚定，列宽 `S(14)`；每行 = 圆点 `S(5)` + 单字母标签（`White(170)`，字号 `8*scale`）。
- **Codex 窗口**：R（雷达站）/ O（OpenAI）/ C（Claude，含 Claude Code 用量，key 前缀 `claude` 同时匹配 `claude_code_usage`）/ D（DeepSeek，仅 API key 已配置时显示）。
  颜色来源：复用 `GetCodexApiServiceAlertCandidates()` —— 无该服务的告警候选即绿 `Success`；有则取候选自带颜色；key 含 `:checking` 时闪烁。
- **Claude 窗口**：R（雷达数据 `DataState`）/ C（Claude 状态页 `ClaudeStatusState`）/ U（Claude Code 用量 `ClaudeCodeState`），`Normal`→绿、`Unknown`→灰、其余走 `GetClaudeServiceAlertColor`。

## 5. 布局数学（默认几何：窗口 522×120 @ scale=2）

内容边界：左右各内缩 `S(8)`=16、上 `S(3)`、下 `S(3)` → 内容宽 490、高 108。

| 元素 | 宽度 |
|---|---|
| leftInset | S(2)=4 |
| 圆环区：5 × (环径40 + 肩部 S(6)=12) + 4 × ringGap S(4)=8 | 5×52+32 = 292 |
| radarGap（额度线左） | elementGap×0.3 = 3 |
| 额度线列 | 40 |
| radarGap（额度线右） | 3 |
| 状态区（表盘 dial=高-4=104 + S(3)=6 + LED S(14)=28 + 左右 pad S(1)+S(2)=6） | 144 |
| rightInset | S(2)=4 |
| **合计** | **490**（= 窗口 522 − 32，零死区饱和） |

- `GetEvenRowStatusZoneWidth` 与绘制端使用同一公式（fit 宽 == draw 宽，见 FitFontSize 教训）。
- 窗口比饱和宽度更宽时：圆环格宽有上限，多余宽度汇入状态区并聚集在**表盘与LED之间**（表盘左对齐），不会散布在表盘四周。

## 6. 验证方法

- 渲染基线（真实物理尺寸）：`DesktopCodexAssistant.exe --render-codexradar --out <dir>`、`--render-clauderadar --out <dir>`。
- 真实窗口像素级验证（层叠工具窗，普通截图工具不可用）：`EnumWindows` 按 PID 找 HWND（`SetThreadDpiAwarenessContext(-4)` 取物理矩形）→ `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT=2)`。
- 回归：`--test` / `--test-layout` / `--test-settings-bindings` / `--test-operation-panel`。

## 7. 已知边界与残留风险

- 弧表达的是"钟面距离"：批次超过一个表盘周期（24h）后弧回绕视觉变短，此时以颜色（红/黄）为准。
- `pm2` 后缀的具体形态是按合理猜测兼容的（`pm2`/`pm_2`/`pm-2`），站点实际发布后如格式不同会安全回退为文字，不破版。
- "本地未更新"的 2 小时阈值是工程判断值（正常刷新间隔为分钟级），如需调整只改 `ComputeEvenRowDialStatusColor` / `ComputeClaudeEvenRowDialStatusColor` 中的 `2.0`。
- 表盘/灯柱颜色仅使用既有 OLED 安全色板（Success/Warning/Danger/GlyphMuted/品牌橙 232,128,54），无蓝色。
