# Fable5-NetworkMonitor-Layout-SPEC — 网络监控窗口布局重构执行规格（错误升行版）

适用版本：起点 `1.0.4.23`。状态：draft（待批准后执行）。
面向执行 AI。执行前必读：根 `AGENTS.md`（ARM64/部署/索引规则）、`Docs/AGENTS.md`（文档规则）、`Docs/Fable5-Frontend-Rendering-Technical.md`（**§5 禁改清单与 §6 查验流程对本任务强制生效**）、`Docs/NetworkMonitor-Architecture.md`、`Docs/Component-Refresh-Rules.md` §6。

> 用户已确认的两条前提，本规格据此收敛（与更早的可变窗口草稿不同）：
> 1. **窗口是固定画布**，运行尺寸最大 628×250 物理像素；**不需要考虑小于该尺寸的收紧或行降级**。
> 2. **必须按错误态最长文本做预算**，而不是正常态——有些板块（PING、GFW、DNS 告警）平时短，出错时会变成很长一串。

---

## 0. 问题定义（审查实证）

现状 Classic 布局（`Core/NetworkMonitorForm.cs`）：头部 + **固定 7 行等高**（IP4/IP6/IF/DNS/WIFI/PING/GFW）。元素遮挡有四个确切来源：

| # | 来源 | 证据 |
|---|---|---|
| P1 | **固定 7 行 → 底部拥挤**。`rowHeight = Math.Max(S(12), (content.Bottom-rowTop)/7)`（`DrawContentClassic`）。7 行始终全画，正常态其实用不满，出错行变多又没有空间腾挪 | `DrawContentClassic` |
| P2 | **DNS 告警覆盖层与 GFW 行文字真重叠**。`DrawDnsAlertOverlay`/标题栏告警槽把告警塞进已有文字的缝隙，GFW 或状态长时被盖 | `DrawDnsAlertOverlay`、`DrawHeader` 告警块 |
| P3 | **头部四段挤压**。`DrawHeader` 中 alert 文本用 `DrawFixedText`（**不缩字**）画进 status 与云瓦片之间的夹缝，夹缝可为 0 宽 | `DrawHeader` 告警块 |
| P4 | **IPv6 行长串/缺失两极**。压缩用固定字符上限（`BuildMeasuredAddressRowText(..., 24)`）而非按像素测量；无可显示地址时行仍占位，浪费一行 | `DrawInfoRow` IP6 分支 |

数据层（`NetworkMonitorReader`）与刷新调度**完全不在本任务范围内**。

## 1. 方案总纲：先计划、后绘制（Plan-then-Draw）

把"绘制时各自算矩形 + 两处右侧叠加"改为：每帧先构建一份**行计划**（一个 `ClassicRow[]`，含每行类型/内容/颜色），几何在计划阶段一次算完；绘制阶段只按行索引填内容，不做任何布局决策。行是纯垂直堆叠，矩形互不相交是**结构保证**，不再靠"小心避免"。

### 1.1 行类型

```csharp
private enum ClassicRowKind { Info, Dns, PingGfw, Alert }

private sealed class ClassicRow
{
    public ClassicRowKind Kind;
    public string Label;         // IP4/IP6/IF/WIFI/PING/GFW/告警
    public string Value;
    public Color  ValueColor;
    public string Value2;        // 仅 PingGfw：GFW 段
    public Color  Value2Color;
}
```

### 1.2 行计划规则（`BuildClassicRowPlan`）

自上而下按现状视觉顺序，条件与合并如下：

| 行 | 出现条件 | 说明 |
|---|---|---|
| IP4 | 恒显 | 右侧附件：云服务 6 瓦片（现状 `GetCloudEndpointTileStripWidth` 迁入） |
| IP6 | **仅当存在可显示地址**（见 §1.4） | 无可显示地址整行隐藏，其余行自动变高 |
| IF | 恒显 | — |
| DNS | 恒显 | 走现有 `DrawDnsRow` |
| WIFI | 恒显（有线时值为灰显占位，保持现状语义） | — |
| PING·GFW | 两者文本都放得下 → 合并 1 行 | 见 §1.3 |
| PING / GFW | 任一放不下 → 各自独占整行 | 错误升行，见 §1.3 |
| 告警 | **仅当有告警**（云服务红/橙、DNS 异常、云服务测试中） | 底部专属整行，见 §1.5 |

- 正常态 6 行：IP4 / IP6 / IF / DNS / WIFI / PING·GFW。
- 最坏 8 行：IP4 / IP6 / IF / DNS / WIFI / PING / GFW / 告警。
- `rowHeight = 行区高度 / 行数`。**固定 628×250 下 8 行不低于最小可读行高**（`RunClassicRowPlanSelfTest` 断言 `行区/行数 ≥ S(11)`），因此不需要任何降级/隐藏低优先级行的逻辑。

### 1.3 PING·GFW 合并与错误升行（本方案核心）

- 合并判定 `CanMergePingGfwRow`：按当前字体实测 `S(10) + PING标签 + 间隙 + PING值实测宽 + S(14) + GFW标签 + 间隙 + GFW值实测宽 + S(10)`，`≤ this.Width` 则合并。
- 合并行 `DrawMergedPingGfwRow`：PING 段左对齐实测宽，GFW 段紧随其后一直用到行右边界。
- 放不下（GFW 出错文本变长）→ 计划里拆成两个 `Info` 行，各自整行宽，GFW 长文本在自己的整行里显示。
- 这样"平时短、出错长"的 PING/GFW 各自有整行兜底，永不互相挤压。

### 1.4 IPv6 策略（P4）

1. **地址选择**（`TryBuildDisplayIpv6Value`）：解析后保留**非 link-local、非组播**的地址（全局单播 + ULA），`fe80::` 链路本地一律不显示（无诊断价值且必然存在）；一个都不剩 → 返回 false，IP6 行不进计划。`+n` 隐藏计数保留。
2. **像素测量压缩**：在 IP6 行 `DrawInfoRow` 里按 `ValueRect.Width` 实测；放不下则中段省略（保留前 2 组 + 后 2 组，如 `2406:da18:…:5e6f:7890`）。628 宽整行下正常态通常无需省略。
3. IP4 行、头部"公网"值同样改为像素测量压缩，去掉 15/16 字符魔数（复用同一压缩重载）。

### 1.5 告警专属行（消灭 P2/P3）

- `DrawDnsAlertOverlay` 与 `DrawHeader` 里挤缝隙的告警绘制**全部废除**；`DrawFixedText`（不缩字助手）删除。
- 头部只剩三段：NETWORK 标题 / 状态文字 / 云瓦片，互不挤占。
- 告警统一由 `BuildAlertRow` 产出底部整行（`ClassicRowKind.Alert`），无告警时该行不存在：
  - 云服务检测中 → `云服务测试中`；
  - 否则取 `GetCloudEndpointAlertCandidates` 当前轮换项，整行宽足够，**"服务名 原因"一次显示完整**（不再名字/原因两相轮换），多条追加 `(n/总数)`，如 `Cloudflare 状态异常 (1/7)`；
  - DNS 异常按现有逻辑并入候选队列，颜色复用 DNS 行状态色，不额外发起探测。
- 告警文本在整行内 shrink-fit（下限 7.5f 逻辑字号）；极端超长时在**自己的行矩形内**裁剪，永不与其他行相交。

## 2. 范围与禁改

- **仅改 Classic 绘制路径**：`DrawContentClassic`/`DrawHeader`/`DrawInfoRow`/`DrawDnsRow`，新增 `ClassicRow`/`ClassicRowKind`/`BuildClassicRowPlan`/`CanMergePingGfwRow`/`BuildAlertRow`/`DrawMergedPingGfwRow`/`DrawAlertRow`/`TryBuildDisplayIpv6Value`；删除 `DrawDnsAlertOverlay`、`DrawFixedText` 与固定字符压缩死代码。
- Typographic/AmberHud/WarmCard/Phosphor 四变体 partial **不动**。
- 禁改：`NetworkMonitorReader` 及任何数据/调度/单飞逻辑；快照字段；设置键；窗口尺寸物理像素 1:1 语义；OLED 约束；前端文档 §5 全部条目。
- 颜色、字体族、label 列宽、整体视觉风格保持现状——布局引擎替换，不是重设计。

## 3. 实施步骤

1. 加 `ClassicRow`/`ClassicRowKind` 与 `BuildClassicRowPlan(Graphics g, float rowAreaHeight)`（私有）。
2. `DrawContentClassic` 改为：DrawHeader（简化版）→ BuildClassicRowPlan → 按 `row.Kind` 分派 `DrawInfoRow`/`DrawDnsRow`/`DrawMergedPingGfwRow`/`DrawAlertRow`。
3. 实现 §1.3 合并/升行、§1.4 IPv6、§1.5 告警行与头部简化；删除死代码。
4. `RunNetworkMonitorDisplaySelfTest`（挂 `--test-layout`）末尾加 `RunClassicRowPlanSelfTest`，断言：正常态合并 PING·GFW 且有 IP6 行且无告警行；`fe80::` 或空 IPv6 时无 IP6 行；GFW 超长文本时 PING/GFW 拆成两行；有 DNS 告警时恰好新增一条底部告警行；行数 ≤ 8 且 `行区/行数 ≥ S(11)`。
5. `NetworkMonitorForm.RenderSample.cs` 增加 3 个 628×250 确定性 fixture：`networkmonitor-fixture-normal.png`、`networkmonitor-fixture-noipv6.png`、`networkmonitor-fixture-errors.png`（长 GFW + 云异常 → 升行 + 告警行）。
6. 文档同步：`Docs/NetworkMonitor-Architecture.md`（新增行计划小节 + 告警行改写）、`Docs/Component-Refresh-Rules.md` DNS 告警条目、两个 INDEX、CHANGELOG；版本号 +1。

## 4. 验收 Gate（全部满足才算完成）

1. 构建 0 error、警告不增；`--test-layout`（含 `RunClassicRowPlanSelfTest`）、`--test-logger`、`--test-settings-bindings` 全 PASS。
2. `--render-networkmonitor` 三个 fixture 生成并人工核查：normal 显示 6 行且 IPv6 完整无省略、PING·GFW 同行；noipv6 显示 5 行更高、公网退回 IPv4；errors 显示 PING 与 GFW 各占一行 + 底部告警行，且**无任何文字越出内容区或互相重叠**。
3. `networkmonitor-current.png`（真实设置）尺寸等于 settings.ini 网络窗口宽高；IPv6 有值完整/省略、无值时该行消失。
4. P2/P3 行为变更（告警从标题栏缝隙移到底部专属行）单独截图向用户确认后合入。
5. 真机部署（默认部署规则）后肉眼确认正常态与断网/DNS 异常态均无遮挡。

## 5. 回滚

改动集中在 `NetworkMonitorForm.cs` + `NetworkMonitorForm.RenderSample.cs` + 文档；git revert 对应提交即可，部署回滚用 `_build/formal-backups/` 最近备份。

## 附：本方案已做过一次可行性验证

作者曾按本规格实现并实测：ARM64 构建通过、`--test-layout`（含上述断言）PASS、三个 fixture 渲染确认（normal 6 行 IPv6 完整、noipv6 让位变高、errors 升行 + `Cloudflare 状态异常 (1/7)` 告警行、全程无重叠）。该次实现**已按用户要求整体回退**（本仓库不保留其代码），本文档是交付执行 AI 的最终方案；执行 AI 从当前基线重新实现即可，可预期与验证结果一致。
