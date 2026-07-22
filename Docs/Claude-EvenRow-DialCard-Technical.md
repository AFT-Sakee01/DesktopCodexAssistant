# Claude-EvenRow-DialCard-Technical.md — EvenRow 表盘状态卡技术说明

> **历史快照（SUPERSEDED）**
>
> 本文冻结于 `1.0.5.06` 的旧 EvenRow/独立 Claude Radar 绘制实现；截至 `1.0.6.29`，`ClaudeRadarForm` 与可见 `CodexRadarForm` 已移除，Radar 仅由永久 headless owner 提供缓存投影。本文只供历史追溯，**禁止作为现行实现、修改或验收依据**。当前 Radar 所有权和展示边界以 [`CodexRadar-Architecture.md`](CodexRadar-Architecture.md)、[`Codex-ClaudeRadar-Architecture.md`](Codex-ClaudeRadar-Architecture.md) 与 [`Performance-And-Window-Runtime.md`](Performance-And-Window-Runtime.md) 为准。

> 作者模型：Claude（按项目规则，非 Codex 模型生成的文档使用模型名前缀）
> 快照版本：1.0.5.06
> 历史适用窗口：CodexRadar EvenRow 变体（`Core/CodexRadarForm.EvenRow.cs`）、独立 Claude Radar EvenRow（`Core/ClaudeRadarForm.cs`）

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

### 2.2 当前指针、刷新标记与弧
- **当前指针**（白色小圆点，`White(235)`，直径 `S(4)`）落在当前时刻位置：0:00 在正上方（-90°），12:00 在正下方，角度按周期内已过时间顺时针增加。
- **刷新标记**（绿色小圆点，`Success`，直径 `S(3)`）落在上次记录到新 IQ 内容的位置；Codex 共享窗口保留 12 小时，Claude 保留 24 小时，下一圈指针到达该位置后不再绘制。Codex 同内容重复请求会保留旧 `RefreshedUtc`，不移动刷新标记。
- **时间模式短标**（`UTC` / `LAST` / `REF` / `NOW`）绘制在圈内时间下方，颜色与时间一致；短标使用新增矩形，不能挤压或移动既有日期、时间、外环、绿点和白点。
- **周期边界刻度**：12 点钟方向使用 `DesignTokens.White(170)` 中性白色竖线，避免与“当前周期已更新”的 Success 绿混淆；绿色刷新标记保持不变。
- **晚测第二次**：后缀 `pm2` / `pm_2` / `pm-2`（尾数≥2）时，在标记点内侧绘制黄色小"2"角标（`RadarClockDial.HasSecondRunSuffix`）。
- **弧**：从周期顶部边界顺时针扫到当前时刻位置，线宽同轨道、圆头端帽。弧长即当前周期已走过的距离；颜色见 §3。

### 2.3 中心文字
- 上行：批次日期由 `RadarClockDial.FormatDate` 统一格式化为 **"x月x日"**（`"7.6"` 或 `"7/6"` → `"7月6日"`；已含"月"或无法解析则原样显示，shrink-to-fit 防溢出）。白色 `White(235)`。
- 下行：按 `RadarClockTimeDisplayMode` 显示 UTC、本机当前、上次尝试刷新或上次实际 IQ 刷新的 `HH:mm`，颜色跟随 §3 的四级色。
- 中心文字区宽度 = 表盘直径 × 0.72。

### 2.4 批次标签解析链
`GetCodexModelIqDataLabelDisplayText` / `GetClaudeModelIqDataLabelDisplayText`（数据源不变）
→ `RadarClockDial.SplitDataLabel`：按下划线切分（`"7.6_am"` → 主体+后缀）；无下划线时按"最后一个空格 + 其后含冒号"切分（`"7/6 10:59"` → `"7/6"`+`"10:59"`）；都不匹配则整体作主体。
→ 主体走 `RadarClockDial.FormatDate`，后缀只由 `RadarClockDial.HasSecondRunSuffix` 判定晚间第二次徽标；旧批次小时解析已经删除。
未知形态安全回退：主体整体 shrink-fit 显示在表盘中心，无标记点无弧。

## 3. 边界新鲜度颜色规则

实现：`RadarClockDial.ComputePhase` 每帧只计算一次互斥状态，`ComputeState` 同时产出颜色、角度、弧线、标记和中心文字，`Draw` 供共享 Codex/Claude 窗与独立 Claude 窗复用。窗体保留各自字体缓存与 fitted-text 委托，不在共享模块中访问快照或执行 I/O。

**更新周期**：Codex 一天两测 → `cycle = 12h`；Claude 一天一测 → `cycle = 24h`。边界使用本机系统时间：Codex 为系统 0:00/12:00，Claude 为系统 0:00。

| 优先级 | 条件 | 颜色 |
|---|---|---|
| 1 | 当前周期已有对应模型 IQ 数据窗口或 Claude `latest_at` | **绿** `SuccessSoft`，alpha 255 |
| 2 | 当前周期还没有，但上一个周期有数据 | **黄** `Warning` |
| 3 | 已知批次时间早于上一周期边界 | **红** `Danger`，低透明度红色满环上叠加高亮红色当前周期弧 |
| — | 批次与本地时间都未知 | 灰 `GlyphMuted` |

- 红色只在错过上一个完整周期后出现；本地抓取时间不再作为“已更新”或“本地故障”的颜色依据。
- 本地检查时间已知但模型批次时间未知时沿用红色状态文字，但不推断边界弧或满环；只有 `BatchKnown` 能证明错过周期并启用这两项几何。
- 数据来源：
  - Codex 批次时刻 = `ModelIqDataDateLocal.Date + (WindowStartHour>=12 ? 12h : 0h)`（`ModelIqDataDateKnown` 守卫）；绿色刷新标记 = `ModelIqRefreshedAtLocal`（`ModelIqRefreshedAtKnown` 守卫）。
  - Claude 批次时刻与绿色刷新标记 = `GetClaudeLatestMetricLocalTime(local)`。
- 刷新请求进行中沿用原闪烁机制（`renderTickCount` 奇偶帧 alpha 104）。
- 状态弧的绿色与刷新标记刻意不同：当前周期状态使用不透明 `SuccessSoft`，刷新标记仍使用 `Success` alpha 245。主 `Success` alpha 255 在真实 PArgb 分层夹具中会触发 GDI+ 像素丢失，导致同层左侧五环、底栏等已绘内容不可见；`SuccessSoft` 保留完整 522×120 画面，因此不能为视觉统一而替换。

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

- 弧表达的是当前周期进度，始终从顶部边界顺时针前进；批次是否过期以颜色（红/黄/绿）为准。
- `pm2` 后缀的具体形态是按合理猜测兼容的（`pm2`/`pm_2`/`pm-2`），站点实际发布后如格式不同会安全回退为文字，不破版。
- 表盘/灯柱颜色仅使用既有 OLED 安全色板（SuccessSoft/Success/Warning/Danger/GlyphMuted/品牌橙 232,128,54），无蓝色。
