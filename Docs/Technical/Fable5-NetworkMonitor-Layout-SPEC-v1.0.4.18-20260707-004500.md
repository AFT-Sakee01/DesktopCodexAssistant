# Fable5-NetworkMonitor-Layout-SPEC — 网络监控窗口布局重构执行规格

适用版本：起点 `1.0.4.18`。状态：draft（待用户批准后执行）。
面向执行 AI。执行前必读：根 `AGENTS.md`（ARM64/部署/索引规则）、`Docs/AGENTS.md`（文档规则）、`Docs/Fable5-Frontend-Rendering-Technical.md`（**§5 禁改清单与 §6 查验流程对本任务强制生效**）、`Docs/NetworkMonitor-Architecture.md`、`Docs/Component-Refresh-Rules.md` §6。

---

## 0. 问题定义（审查实证，2026-07-07）

现状 Classic 布局（`Core/NetworkMonitorForm.cs`）：头部 + **固定 7 行等高**（IP4/IP6/IF/DNS/WIFI/PING/GFW），元素遮挡有四个确切来源：

| # | 来源 | 证据 |
|---|---|---|
| P1 | **固定行数 + 行高下限 → 底部溢出**。`rowHeight = Math.Max(S(12), (content.Bottom-rowTop)/7)`（`DrawContentClassic`）。最小窗口高 112px 时 7×24px 行 + 头部 ≥36px + 边距必然超出内容区，下部行画到窗口边界外/互相压叠 | `WidgetSettings.MinNetworkMonitorHeight = 112` |
| P2 | **DNS 告警覆盖层与 GFW 行文字真重叠**。`DrawDnsAlertOverlay` 按产品决定不预留宽度直接叠在 GFW 行右下（1.0.4.16），GFW 文字长时被盖住——changelog 该条 residual_risks 已明示 | `DrawDnsAlertOverlay`；`change-20260706T073125Z-1-0-4-16` |
| P3 | **头部四段挤压**。`DrawHeader` 中 alert 文本用 `DrawFixedText`（**不缩字**）画进 status 与公网 IP 之间的夹缝矩形，夹缝可为 0 宽；公网 IPv6 压缩是固定 16 字符而非按像素测量 | `DrawHeader` 693–701 行附近 |
| P4 | **IPv6 行长串/缺失两极**。行值压缩用固定 24 字符上限（`BuildMeasuredAddressRowText(..., 24)`）而非按实际可用像素测量；无可显示地址时行仍占位显示 `--`，浪费一行高度 | `DrawInfoRow` IP6 分支 |

数据层（`NetworkMonitorReader`）与刷新调度**完全不在本任务范围内**。

## 1. 方案总纲：RowPlan 自适应行布局器

把"绘制时各自算矩形 + 两处右侧叠加"改为**先计划、后绘制**：每帧构建一个 `NetworkRowPlan`（显示哪些行、每行的 label/value/attachment 三段矩形），几何在计划阶段一次算完并保证互不相交；绘制阶段只按矩形填内容，不做任何布局决策。遮挡在机制上不可能发生。

### 1.1 行模型

```csharp
private sealed class NetworkRowSlot
{
    public string Label;              // "IP4" / "IP6" / "IF" / "DNS" / "WIFI" / "PING" / "GFW"
    public int Priority;              // 数值越小越先被降级隐藏
    public bool Visible;
    public RectangleF LabelRect;      // 固定 S(42) 宽，现状不变
    public RectangleF ValueRect;      // 弹性
    public RectangleF AttachmentRect; // 右侧附件槽，宽度按内容实测，可为空
}
```

行清单与显示条件（Priority 从低到高 = 先隐藏到后隐藏）：

| 行 | 显示条件 | Priority | Attachment |
|---|---|---|---|
| IF | 恒显 | 1 | 无 |
| WIFI | 仅 `snapshot.IsWifi`；有线时整行隐藏 | 2 | 无 |
| IP6 | 存在可显示 IPv6（见 §1.4）；否则整行隐藏 | 3 | 无 |
| DNS | 恒显 | 4 | 无 |
| GFW | 恒显 | 5 | **DNS 告警条**（自覆盖层降级为附件，见 §1.3） |
| IP4 | 恒显 | 6 | 云服务 6 瓦片（现状迁入） |
| PING | 恒显 | 7 | 无 |

绘制顺序保持现状视觉顺序（IP4/IP6/IF/DNS/WIFI/PING/GFW 自上而下），Priority 只用于高度不足时的降级次序。

### 1.2 高度分配与降级

1. `rowHeight = (content.Bottom - header.Bottom - S(1)) / visibleRowCount`。
2. 定义最小可读行高 `MinReadableRowHeight = S(13)`。若 `rowHeight < MinReadableRowHeight`，按 Priority 升序隐藏一行后重算，循环直到满足或只剩 4 行（IP4/DNS/PING/GFW 为保底集合，保底集合仍不满足时允许 rowHeight 低于下限但**必须整体位于 content 内**——clamp 到 content.Bottom，禁止画出边界）。
3. 行隐藏时其信息去向：WIFI 隐藏→无（有线本无 WIFI）；IP6 隐藏→无；IF 隐藏→接口名并入 WIFI 行前缀或直接省略（执行 AI 取简单者：省略）。
4. **禁止**用把字体缩到 <8.0f 逻辑字号的方式代替行降级。

### 1.3 附件槽机制（消灭两处叠加）

统一规则：`ValueRect.Width = 行宽 − LabelRect − 间隙 − AttachmentRect.Width − 间隙`，value 的 fit 测量宽 **必须等于** ValueRect.Width（FitFontSize 教训，见前端文档 §1.2）。

- **IP4 行云瓦片**：现有 `GetCloudEndpointTileStripWidth` 逻辑原样迁入 attachment 计算，行为不变。
- **GFW 行 DNS 告警**：`DrawDnsAlertOverlay` 整体废除，改为 GFW 行 attachment：
  - 告警宽 = `min(测量宽+S(4), ValueRect 可让出的最大宽)`，GFW value 至少保留 `S(80)`；
  - 告警文本在自己的矩形内 shrink-fit（下限 7.5f 逻辑字号），再不够时沿用现有轮换机制逐条轮显；
  - 无告警时 attachment 宽 0，GFW value 全宽——**"DNS正常" 绿字仍显示**（保持 1.0.4.15 行为），作为 attachment 常驻但宽度实测；
  - 结果：GFW 文字与 DNS 告警在几何上永不相交。此变更推翻 1.0.4.16 的"不预留宽度"决定，**需在验收时向用户展示对比图确认**。

### 1.4 IPv6 策略（P4）

1. **地址选择**：按序取第一个全局单播 IPv6（复用 `IsPublicRoutableIpv6`）；没有则取第一个 ULA（fd00::/8）；再没有→行隐藏（链路本地 fe80 不显示——它无诊断价值且必然存在）。
2. **像素测量压缩**：废除固定 24 字符上限。`CompactIpAddressForDisplay` 增加按宽度的重载：在 ValueRect.Width 内先试全地址，放不下则中段省略（保留前 2 组 + 后 2 组，如 `2406:da18:…:5e6f:7890`），逐步收缩到最少 `前1组+后1组`；`+n` 计数永远保留且不参与省略。
3. IP4 行同步改为像素测量压缩（同一重载，去掉 15 字符魔数）。
4. 头部"公网"值同样走像素测量压缩（替换固定 16 字符）。

### 1.5 头部三段安全布局（P3）

`DrawHeader` 重写为测量驱动：
1. NETWORK 标题：测量宽（fit 上限 26% 保留）。
2. 公网 IP：右对齐，宽 = `min(测量宽, rect 宽 × 0.4)`，shrink-fit。
3. 中段 = 剩余区间：status 测量宽优先，alert 用剩余宽 **shrink-fit（替换 DrawFixedText）**；剩余宽 < S(30) 时 alert 不在头部显示，改为并入 GFW 行告警轮换队列。
4. 断言：三段矩形互不相交且都在 header 内。

## 2. 范围与禁改

- **仅改 Classic 绘制路径**（`DrawContentClassic`/`DrawHeader`/`DrawInfoRow`/`DrawDnsRow`/`DrawDnsAlertOverlay` 及新增 RowPlan 类型）。Typographic/AmberHud/WarmCard/Phosphor 四个变体 partial **本任务不动**（后续可选任务再统一接 RowPlan）。
- 禁改：`NetworkMonitorReader` 及任何数据/调度/单飞逻辑；快照字段；设置键；窗口尺寸语义（物理像素 1:1）；OLED 约束；前端文档 §5 全部条目。
- 颜色、字体族、label 列宽 S(42)、整体视觉风格保持现状——这是布局引擎替换，不是重新设计。

## 3. 实施步骤

1. 新增 `NetworkRowSlot` 与 `BuildRowPlan(Graphics g, RectangleF content, float headerBottom)`（私有，NetworkMonitorForm 内），产出 `List<NetworkRowSlot>`＋断言矩形互不相交。
2. `DrawContentClassic` 改为：DrawHeader（新版）→ BuildRowPlan → 逐行按 slot 绘制（现 DrawInfoRow/DrawDnsRow 改为接收 slot）。
3. 实现 §1.3 附件槽、§1.4 IPv6 像素压缩、§1.5 头部。删除 `DrawDnsAlertOverlay` 与固定字符数压缩路径的死代码。
4. 扩展 `RunNetworkMonitorDisplaySelfTest`（挂在 `--test-layout`）新增断言：
   - a) 构建 RowPlan（注入合成快照）后任意两个可见矩形 `IntersectsWith == false`；
   - b) 最小窗口 260×112 下所有矩形 `Bottom <= content.Bottom`；
   - c) IPv6 场景矩阵：无 IPv6（行隐藏，行数减一）、仅链路本地（行隐藏）、长全局地址（压缩后测量宽 ≤ ValueRect 宽）、多地址（+n 保留）；
   - d) GFW 长文本 + DNS 告警 active：两矩形不相交且告警宽 ≥ 最小值；
   - e) 有线网络：WIFI 行隐藏。
5. `NetworkMonitorForm.RenderSample.cs` 增加 4 个确定性 fixture PNG：`networkmonitor-minheight.png`（260×112）、`networkmonitor-noipv6.png`、`networkmonitor-longipv6-alert.png`（长 IPv6+长 GFW+告警）、`networkmonitor-wired.png`。
6. 文档同步：`Docs/NetworkMonitor-Architecture.md`（布局章节重写为 RowPlan 描述）、`Docs/Component-Refresh-Rules.md` §6 DNS 告警条目（覆盖层→附件槽）、两个 INDEX、CHANGELOG；版本号 +1。

## 4. 验收 Gate（全部满足才算完成）

1. 构建 0 error、警告数不增；`--test-layout`（含新断言）、`--test-logger`、`--test-settings-bindings` 全 PASS。
2. `--render-networkmonitor` 输出：4 个新 fixture 全部生成；用 `_validation/Compare-RenderSamples.py` 断言 **每个 fixture 中不存在被裁剪出内容区的像素**（图像四边 2px 内无非背景色文字像素——脚本加 `--edge-check` 或人工核查并留证）。
3. `networkmonitor-current.png`（真实设置）：IPv6 有值时完整或中段省略显示、无值时该行不出现且其余行变高；贴图对比写入交付说明。
4. 与 1.0.4.18 基线的 normal fixture 对比：允许行距差异，但必须逐项确认 7 行内容文字齐全、云瓦片 6 个齐全、无重叠——以 §3.4a 程序断言为准，同时附 before/after 并排图。
5. P2 行为变更（GFW 让宽给 DNS 告警）单独截图向用户确认后才可合入。
6. 真机部署（按默认部署规则）后：调窗口高度到最小 112px，肉眼确认无溢出；恢复用户原高度。
7. 检查单：`grep -n "DrawDnsAlertOverlay\|BuildMeasuredAddressRowText" Core/NetworkMonitorForm.cs` 旧路径零残留（或仅剩新实现引用）。

## 5. 回滚

单文件为主的改动（NetworkMonitorForm.cs + RenderSample + 文档），git revert 对应提交即可；部署回滚用 `_build/formal-backups/` 最近备份。
