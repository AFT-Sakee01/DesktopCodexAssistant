# Fable5-Settings-Refactor-Spec — 设置页面重构执行规格

面向执行 AI 的实施规格。用户已确认方向：**WarmCard 美术风格 + 布局可视化页 + 变体缩略图选择器 + 每页「复杂选项」折叠区 + 搜索跳页与未保存提示 + 文案通俗化**。用户额外要求：可自行增删设置项，但完成后必须**明文列出增删/移动清单**（见 §5，汇报时引用）。

执行前先读 `AGENTS.md`（ARM64 默认、部署规则、CHANGELOG/索引维护）。

---

## 1. T0 — 基础改造（可能已完成：先检测，命中则跳过，丢失则按本节实现补齐）

本仓库处于并发编辑状态。下述 T0 改动曾在工作区落地（当时全量编译通过、`--test-settings-bindings` PASS），但**不保证执行时仍然存在**。每个子任务附检测命令：命中且内容一致 → 直接跳过；缺失或被覆盖 → 按给出的实现重做。若只残留一部分，以本节为准补齐。

### T0-A `DesignTokens.SettingsWarmTheme` 主题色板

检测：`grep -n "SettingsWarmTheme" Core/DesignTokens.cs` 有命中即已存在（再抽查两三个色值是否与下面一致）。

实现：在 `Core/DesignTokens.cs` 的 `NeonGeekTheme` 类之前插入（`OledAmber`/`OledCard` 为文件中已有类）：

```csharp
// Warm settings-window theme: aligns the settings UI with the OLED-safe WarmCard floating-window
// scheme — warm-gray fills, the OledCard text ramp, and the OledAmber accent, with no blue hue
// anywhere. Colors are opaque because the settings window is a normal (non-layered) form.
public static class SettingsWarmTheme
{
    public static readonly Color WindowBase = Color.FromArgb(26, 24, 21);
    public static readonly Color InputBackground = Color.FromArgb(36, 33, 29);
    public static readonly Color CardRest = Color.FromArgb(40, 37, 33);
    public static readonly Color CardHover = Color.FromArgb(52, 48, 42);
    public static readonly Color DividerLines = Color.FromArgb(60, 55, 48);
    public static readonly Color TextPrimary = Color.FromArgb(224, 218, 206);
    public static readonly Color TextSecondary = Color.FromArgb(196, 190, 178);
    public static readonly Color TextMuted = Color.FromArgb(138, 133, 124);
    public static readonly Color Accent = OledAmber.Base;
    public static readonly Color AccentHover = OledAmber.Bright;
    public static readonly Color AccentPressed = OledAmber.Dim;
    public static readonly Color NavSelectedBg = Color.FromArgb(30, 214, 154, 58);
    public static readonly Color NavHoverBg = Color.FromArgb(46, 42, 37);
    public static readonly Color ToggleTrackOff = Color.FromArgb(58, 53, 46);
    public static readonly Color ToggleTrackHover = Color.FromArgb(72, 66, 57);
    public static readonly Color ToggleKnob = Color.FromArgb(226, 220, 208);
    public static readonly Color ButtonRest = Color.FromArgb(48, 44, 39);
    public static readonly Color ButtonHover = Color.FromArgb(58, 53, 46);
    public static readonly Color ButtonPressed = Color.FromArgb(40, 37, 33);
    public static readonly Color ErrorText = OledCard.DotDanger;
}
```

### T0-B 设置窗口配色全量切换到 SettingsWarmTheme

检测：`grep -n "NeonGeekTheme\|DesignTokens.SettingsTheme\." Settings/Win11SettingsForm.cs` **无命中**即已完成。

替换点（共 7 处，全部在 `Settings/Win11SettingsForm.cs`）：

1. 类顶部 15 个 `private static readonly Color` 字段全部改引 `DesignTokens.SettingsWarmTheme`，映射：`MicaBase→WindowBase`、`MicaLayer→InputBackground`、`CardRest→CardRest`、`CardHover→CardHover`、`StrokeColor/DividerColor/ControlBorder→DividerLines`、`ControlBg→InputBackground`、`TextPrimary/TextSecondary→同名`、`TextTertiary→TextMuted`、`AccentClr→Accent`、`AccentHover→AccentHover`、`AccentPressed→AccentPressed`、`ErrorClr→ErrorText`。
2. `BuildShell` 中关闭按钮 hover：`NeonGeekTheme.ElectricPurple` → `SettingsWarmTheme.ErrorText`（关闭键悬停用警示红，前景白）。
3. `ToggleSwitch.OnPaint` 未选中轨道色：`NeonGeekTheme.ToggleTrackHover/ToggleTrackOff` → `SettingsWarmTheme` 同名。
4. `ToggleSwitch.OnPaint` 旋钮：`SettingsTheme.ToggleKnob` → `SettingsWarmTheme.ToggleKnob`。
5. `NavigationItem.OnPaint` 背景：`SettingsTheme.NavSelectedBg/NavHoverBg` → `SettingsWarmTheme` 同名。
6. `BuildCommandButton`：`SettingsTheme.ButtonRest/ButtonHover/ButtonPressed` → `SettingsWarmTheme` 同名。
7. `ApplyClaudeRadarModelButtonStyle` 选中态硬编码蓝 `FromArgb(37, 99, 235)` / 白字 / 边框 `FromArgb(147, 197, 253)` → `SettingsWarmTheme.Accent` / `Color.Black` / `SettingsWarmTheme.AccentHover`；未选中态 `SettingsTheme.ButtonRest` → `SettingsWarmTheme.ButtonRest`。

注意：`DesignTokens.SettingsTheme` 类本身**保留不删**（可能有别处引用，且作为历史 token 无害）。

### T0-C 「复杂选项」折叠机制（仅机制，页面启用在 T1）

检测：`grep -n "AdvancedSectionHeader" Settings/Win11SettingsForm.cs` 有命中即已存在。机制未被 T1 启用时 UI 行为与原版一致，属预期。

实现（全部在 `Settings/Win11SettingsForm.cs`）：

1. 字段：`CategoryPage` 增加 `public AdvancedSectionHeader AdvancedHeader; public bool AdvancedExpanded;`；`SettingGroupData` 增加 `public bool Advanced;`。
2. `AddPageGrouped` 组循环开头解析 `!` 前缀，并在第一个高级组前插入折叠头；高级组的标题与卡片初始隐藏：

```csharp
string groupTitle = groupDef[0];
// A leading '!' marks the group as advanced: it renders collapsed under the
// per-page "复杂选项" header until the user expands it (or searches).
bool advanced = groupTitle.Length > 0 && groupTitle[0] == '!';
if (advanced) { groupTitle = groupTitle.Substring(1); }

if (advanced && page.AdvancedHeader == null)
{
    AdvancedSectionHeader advancedHeader = new AdvancedSectionHeader(GetUiFont(10.0f, FontStyle.Bold), GetUiFont(8.5f));
    advancedHeader.Width = 1152;
    advancedHeader.Height = 64;
    advancedHeader.Margin = new Padding(0, 32, 0, 6);
    advancedHeader.Click += delegate { ToggleAdvancedSection(page); };
    page.AdvancedHeader = advancedHeader;
    stack.Controls.Add(advancedHeader);
}
// 之后：group.Advanced = advanced;  titleLabel.Visible = !advanced;  card.Visible = !advanced;
```

   组循环结束后、`page.ScrollPanel.Resize += ...` 之前：`if (page.AdvancedHeader != null) { page.AdvancedHeader.AdvancedRowCount = CountAdvancedRows(page); }`

3. 三个辅助方法（放在 `AddPageGrouped` 与 `BuildPageHeading` 之间）：

```csharp
private static int CountAdvancedRows(CategoryPage page)
{
    int count = 0;
    for (int g = 0; g < page.Groups.Count; g++)
    {
        if (page.Groups[g].Advanced) { count += page.Groups[g].Editors.Count; }
    }
    return count;
}

private void ToggleAdvancedSection(CategoryPage page)
{
    page.AdvancedExpanded = !page.AdvancedExpanded;
    if (page.AdvancedHeader != null) { page.AdvancedHeader.Expanded = page.AdvancedExpanded; }
    ApplyAdvancedVisibility(page);
    LayoutPage(page);
}

private void ApplyAdvancedVisibility(CategoryPage page)
{
    string query = (this.searchBox == null ? string.Empty : this.searchBox.Text ?? string.Empty).Trim();
    if (query.Length > 0)
    {
        // Search visibility is authoritative while a query is active.
        ApplySearchFilter();
        return;
    }

    for (int g = 0; g < page.Groups.Count; g++)
    {
        SettingGroupData group = page.Groups[g];
        if (!group.Advanced) { continue; }
        if (group.TitleLabel != null) { group.TitleLabel.Visible = page.AdvancedExpanded; }
        if (group.Card != null)
        {
            group.Card.Visible = page.AdvancedExpanded;
            if (page.AdvancedExpanded) { group.Card.LayoutRows(); }
        }
    }
}
```

4. `ApplySearchFilter` 改造：开头加 `bool searching = query.Length > 0;`；页循环内加

```csharp
if (page.AdvancedHeader != null)
{
    // While searching, advanced rows surface directly, so the collapse header hides.
    page.AdvancedHeader.Visible = !searching;
}
```

   组循环内加 `bool allowed = !group.Advanced || page.AdvancedExpanded || searching;`，行匹配改为 `bool match = allowed && (pageMatch || groupTitleMatch || 名称/标题匹配);`。

5. `LayoutPage` 在设置 `page.Heading.Width` 后加：`if (page.AdvancedHeader != null) { page.AdvancedHeader.Width = width; }`。

6. 新嵌套控件（放在 `FluentScrollPanel` 之前；`MicaBase/CardRest/CardHover/StrokeColor/AccentClr/TextPrimary/TextTertiary/GetIconFont/CreateRoundRectangle` 均为外层类已有成员；chevron 用 Segoe 图标 E70D（展开）/ E76C（收起）的 `\u` 转义写法）：

```csharp
// ── AdvancedSectionHeader ────────────────────────────────────────────
// Clickable "复杂选项" divider: everything below it stays collapsed until
// the user expands it, keeping everyday pages short. Searching bypasses it.
private sealed class AdvancedSectionHeader : Panel
{
    private readonly Font titleFont;
    private readonly Font hintFont;
    private bool expanded;
    private bool hover;

    public AdvancedSectionHeader(Font titleFont, Font hintFont)
    {
        this.titleFont = titleFont;
        this.hintFont = hintFont;
        this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                      ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        this.BackColor = MicaBase;
        this.Cursor = Cursors.Hand;
    }

    public int AdvancedRowCount { get; set; }

    public bool Expanded
    {
        get { return this.expanded; }
        set { this.expanded = value; this.Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        Rectangle rect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
        using (GraphicsPath path = CreateRoundRectangle(rect, DesignTokens.Radius.SettingsCard))
        using (SolidBrush bg = new SolidBrush(this.hover ? CardHover : CardRest))
        {
            g.FillPath(bg, path);
        }

        using (GraphicsPath path = CreateRoundRectangle(rect, DesignTokens.Radius.SettingsCard))
        using (Pen pen = new Pen(StrokeColor))
        {
            g.DrawPath(pen, path);
        }

        // Chevron glyph: E70D = down, E76C = right (Segoe MDL2 / Fluent Icons).
        string chevron = this.expanded ? "" : "";
        Font icoFont = GetIconFont();
        using (SolidBrush brush = new SolidBrush(AccentClr))
        {
            float iconY = (this.Height - icoFont.Height) / 2f + 1;
            g.DrawString(chevron, icoFont, brush, 20, iconY);
        }

        using (SolidBrush brush = new SolidBrush(TextPrimary))
        {
            g.DrawString("复杂选项", this.titleFont, brush, 58, (this.Height - this.titleFont.Height) / 2f - 8);
        }

        string hint = this.expanded
            ? "点击收起"
            : "不常用的精细调整与测试项" + (this.AdvancedRowCount > 0 ? "（" + this.AdvancedRowCount.ToString(CultureInfo.InvariantCulture) + " 项）" : string.Empty);
        using (SolidBrush brush = new SolidBrush(TextTertiary))
        {
            g.DrawString(hint, this.hintFont, brush, 58, this.Height / 2f + 2);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        this.hover = true;
        this.Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        this.hover = false;
        this.Invalidate();
        base.OnMouseLeave(e);
    }
}
```

T0 验收：全量编译通过；`--test-settings-bindings` PASS；T0-A/B/C 各自的 grep 检测通过。

**其他说明**：`Settings/Win11SettingsForm.cs` 可能存在少量 LF/CRLF 混合行尾，编译无碍，git autocrlf 会在提交时归一，不需专门处理。

## 2. 待执行任务

### T1 — 重写 `BuildPages()`：启用复杂选项 + 新「布局与位置」页（依赖 T0-C）

用下面的目标 schema **整体替换** `Settings/Win11SettingsForm.cs` 中 `BuildPages()` 的九个 `AddPageGrouped` 调用（保留方法名与调用方式）。图标字段沿用现有 Segoe 图标转义写法；「布局与位置」页建议 `""`（Move），若渲染为方块可换 `""`。

```csharp
// 分组名以 '!' 开头 = 收进该页底部的「复杂选项」折叠区。
AddPageGrouped(icon系统, "系统", "开机启动、性能和窗口基础行为。", new string[][]
{
    new string[] { "启动与性能", "StartupEnabled", "PerformanceMode" },
    new string[] { "窗口行为", "VisibilityMode", "ClickThroughMode" },
    new string[] { "AI 请求阻断", "AiRequestProtectionAutoEnabled", "AiRequestProtectionManualBlockEnabled" },
    new string[] { "!恢复与保护", "SeelenDockForegroundPulseEnabled", "WinDRecoveryPulseEnabled", "PowerResumeRestartEnabled" },
    new string[] { "!调试", "ForceShowForegroundFpsEnabled" }
});

AddPageGrouped(icon布局, "布局与位置", "所有浮窗的位置、大小和所在显示器；推荐用可视化编辑直接拖拽。", new string[][]
{
    new string[] { "可视化编辑", GlobalLayoutEditCommandName },
    new string[] { "显示器分配", "FallbackDisconnectedDisplaysEnabled", "MainDisplayDeviceName", "CodexRadarDisplayDeviceName",
                   "ClaudeRadarDisplayDeviceName", "PowerThermalDisplayDeviceName", "NetworkMonitorDisplayDeviceName", "ConnectionCheckDisplayDeviceName", "OperationDisplayDeviceName" },
    new string[] { "!主窗口位置", "Width", "Height", "LeftX", "BottomY" },
    new string[] { "!Codex Radar 位置", "CodexRadarWidth", "CodexRadarHeight", "CodexRadarLeftX", "CodexRadarBottomY" },
    new string[] { "!Claude Radar 位置", "ClaudeRadarWidth", "ClaudeRadarHeight", "ClaudeRadarLeftX", "ClaudeRadarBottomY" },
    new string[] { "!功耗温度位置", "PowerThermalWidth", "PowerThermalHeight", "PowerThermalLeftX", "PowerThermalBottomY" },
    new string[] { "!网络监控位置", "NetworkMonitorWidth", "NetworkMonitorHeight", "NetworkMonitorLeftX", "NetworkMonitorBottomY" },
    new string[] { "!连接检测位置", "ConnectionCheckWidth", "ConnectionCheckHeight", "ConnectionCheckLeftX", "ConnectionCheckBottomY" },
    new string[] { "!操作面板位置", "OperationLeftOffset", "OperationBottomOffset" }
});

AddPageGrouped(icon隐藏, "隐藏与防烧屏", "鼠标靠近时隐藏、空闲自动隐藏和 OLED 防烧屏。", new string[][]
{
    new string[] { "鼠标靠近时隐藏", "HoverOpacityEnabled", "SensitiveMouseModeEnabled", "SensitiveMouseRangePixels" },
    new string[] { "自动隐藏", "AutoHoverOpacityIdleEnabled", "AutoHoverOpacityIdleSeconds", "AutoHoverOpacityMaximizedEnabled", "BurnInHiddenModeColorProtectionEnabled" },
    new string[] { "!延迟显现", "HoverOpacityRevealDelayEnabled", "HoverOpacityRevealDelaySeconds", "HoverOpacityRevealResetSeconds" },
    new string[] { "!覆盖与反向", "HoverOpacityCoverEnabled", "ReverseHoverOpacityRevealEnabled", "ReverseHoverOpacityRestoreDelaySeconds" }
});

AddPageGrouped(icon主窗口, "主窗口", "主监控窗口显示哪些指标、透明度和外观。", new string[][]
{
    new string[] { "显示哪些指标", "ShowCpu", "ShowMemory", "ShowDisk", "ShowNetwork", "ShowGpu", "ShowNpu" },
    new string[] { "透明度", "BackgroundTransparencyPercent", "ApplicationTransparencyPercent" },
    new string[] { "外观风格", "MainWidgetRenderVariant" }
});

AddPageGrouped(iconCodex, "Codex Radar", "Codex 用量监控：模型、时区和外观。", new string[][]
{
    // 手动布局与元素偏移分组维持隐藏（原注释保留：属性仍在 WidgetSettings，settings.ini 配置仍生效）。
    new string[] { "模型与时区", "CodexRadarSoftwareMode", "CodexRadarModelKey", "CodexRadarModelVersion", "DeepSeekApiKeyRevision", "DisplayTimeZoneMode", "DisplayTimeZoneId" },
    new string[] { "透明度", "CodexRadarTransparencyPercent" },
    new string[] { "外观风格", "CodexRadarRenderVariant" },
    new string[] { "!网站数据源", "CodexRadarPublicJsonEnabled", "CodexRadarHtmlFallbackEnabled", "CodexRadarRssFallbackEnabled", "CodexRadarServiceProbeToken" },
    new string[] { "!IQ 测试覆盖", "CodexModelIqTestEnabled", "CodexModelIqTestPassed", "CodexModelIqBaselineMode", "CodexModelIqBaselinePassed" },
    new string[] { "!效率测试覆盖", "CodexModelEfficiencyTestEnabled", "CodexModelTokenEfficiencyTestPercent", "CodexModelTimeEfficiencyTestPercent",
                   "CodexModelTokenEfficiencyBaselineMode", "CodexModelTokenEfficiencyBaselinePassed", "CodexModelTokenEfficiencyBaselineTokens",
                   "CodexModelTimeEfficiencyBaselineMode", "CodexModelTimeEfficiencyBaselinePassed", "CodexModelTimeEfficiencyBaselineSeconds",
                   "CodexModelTokenEfficiencyLowThresholdPercent", "CodexModelTimeEfficiencyLowThresholdPercent" },
    new string[] { "!随机测试", "CodexRadarRandomTestEnabled", "CodexRadarRandomTestAutoRefresh", "CodexRadarRandomTestRefreshToken" }
});

AddPageGrouped(iconClaude, "Claude Radar", "Claude 用量监控独立窗口。", new string[][]
{
    new string[] { "窗口", "ClaudeRadarEnabled", "ClaudeRadarTransparencyPercent" },
    new string[] { "模型", "ClaudeRadarModelKey" },
    new string[] { "!网站数据源", "ClaudeRadarJsonEnabled", "ClaudeRadarHomepageFallbackEnabled", "ClaudeRadarCommunityRatingsEnabled", "ClaudeRadarLocalQuotaFallbackEnabled", "ClaudeRadarServiceProbeToken" },
    new string[] { "!随机测试", "ClaudeRadarRandomTestEnabled", "ClaudeRadarRandomTestAutoRefresh", "ClaudeRadarRandomTestRefreshToken" },
    new string[] { "!外观风格（预留）", "ClaudeRadarRenderVariant" }
});

AddPageGrouped(icon功耗, "功耗与温度", "UX3407N / UX3607O 专用功耗温度窗口。", new string[][]
{
    new string[] { "自动布局与告警", "PowerThermalAutoSizeEnabled", "PowerThermalAutoDirection", "PowerThermalVisibleAlertCount" },
    new string[] { "透明度", "PowerThermalTransparencyPercent" },
    new string[] { "外观风格", "PowerThermalRenderVariant" },
    new string[] { "!测试", "ThermalTestMode" }
});

AddPageGrouped(icon网络, "网络", "网络监控、GFW 检测、云服务和出口身份。", new string[][]
{
    new string[] { "网络监控", "NetworkMonitorAdapterId", "NetworkMonitorTransparencyPercent" },
    new string[] { "GFW 检测", "GfwProbeEnabled", "GfwProbeIntervalMinutes" },
    new string[] { "连接检测", "ConnectionCheckIntervalSeconds", "ConnectionCheckTransparencyPercent", "ConnectionCheckBorderTransparencyPercent" },
    new string[] { "网络监控外观", "NetworkMonitorRenderVariant" },
    new string[] { "连接检测外观", "ConnectionCheckRenderVariant" },
    new string[] { "!手动刷新", "GfwProbeManualRefreshToken", "ConnectionCheckManualRefreshToken" },
    new string[] { "!云服务端点", "CloudEndpointTestSeed", "CloudStatusRegionMask" },
    new string[] { "!测试", "CleanIpBadgeTestMode", "NetworkStatusTestMode" }
});

AddPageGrouped(icon操作面板, "操作面板", "左下角操作面板的按钮、透明度和外观。", new string[][]
{
    new string[] { "按钮与面板", "OperationButtonSize", "OperationPrimaryPanelMode", "OperationBackgroundTransparencyPercent" },
    new string[] { "外观风格", "OperationRenderVariant" },
    new string[] { "!测试", "AlertTestEnabled" }
});
```

验收：`RunSettingsBindingSelfTest` 的 `VerifySelfTest` 必需绑定全部存在（该清单一项都不能少，见 `Win11SettingsForm.cs` 中 `VerifySelfTest()` 的 `required` 数组）；几何设置项只出现在布局页，不再出现在各窗口页。

### T2 — 渲染变体缩略图选择器（VariantPicker）

把 6 个 `*RenderVariant` 下拉框换成带预览图的卡片选择器（`ClaudeRadarRenderVariant` 例外，保持下拉、留在复杂选项区，因第一版固定 EvenRow 无实际效果）。

- 新嵌套控件 `VariantPicker : Panel`：每个枚举值一张卡片（缩略图 + 变体名），点击选中（琥珀 `AccentClr` 边框高亮），触发 `OnSettingChanged()`。参考现有 `BuildClaudeRadarModelGridSelector` / `LayoutClaudeRadarModelPanel` 的“行内多行控件 + `RelayoutSettingGroup`”模式；行高自适应用同样机制。
- 在 `BuildValueControl` 中：`property.PropertyType.IsEnum && property.Name.EndsWith("RenderVariant") && !property.Name.StartsWith("ClaudeRadar")` → 返回 VariantPicker；`SetEditorValue`/`GetEditorValue` 增加 VariantPicker 分支（在 ComboBox 分支之前）。
- 缩略图来源：各窗体已有 `internal static void RenderVariantSamples(string outputDir)`，PNG 命名 `<prefix>-<variant小写>.png`。前缀映射：`MainWidgetRenderVariant→widget`（WidgetForm）、`CodexRadarRenderVariant→codexradar`、`NetworkMonitorRenderVariant→networkmonitor`、`PowerThermalRenderVariant→powerthermal`、`OperationRenderVariant→operation`、`ConnectionCheckRenderVariant→connectioncheck`。
- **ConnectionCheckForm 需补出图**：`Core/ConnectionCheckForm.RenderSample.cs` 目前只输出拼合图（`cleanip-restyle-variants.png` 等），需仿照其他窗口补充按变体单独输出 `connectioncheck-<variant>.png`（保留原有拼合图输出）。
- 缓存目录：`%LOCALAPPDATA%\DesktopCodexAssistant\variant-samples\v<ProductIdentity 版本>\`；启动设置窗口不预生成——首次选中含 picker 的页时（`SelectPage` 钩子）若缺文件再生成，**且仅当 `owner != null`**（`RunSettingsBindingSelfTest` 以 `new Win11SettingsForm(null, ...)` 构造，绝不能在自测里触发样例渲染）。生成失败或文件缺失时卡片降级为纯文字，不抛异常。
- 缩略图按等比缩放绘制（`Graphics.DrawImage` 高质量插值），卡片一行 2~3 列自适应；确保 `VerifyNoVisibleControlClipping` 通过。

### T3 — 刷新令牌按钮化（小项，随 T2 一起做）

`BuildValueControl` 中 int 属性名以 `RefreshToken` 结尾的（`GfwProbeManualRefreshToken`、`ConnectionCheckManualRefreshToken`、`CodexRadarRandomTestRefreshToken`、`ClaudeRadarRandomTestRefreshToken`）目前落到 NumericUpDown，改为与 `*ServiceProbeToken` 相同的按钮模式（`BuildCommandButton("立即刷新", false)` + Tag 自增 + `OnSettingChanged()`），复用现有 ServiceProbeToken 分支的写法。`SetEditorValue`/`GetEditorValue` 的 Button/Tag 分支已兼容，无需改。

### T4 — 搜索跳页 + 未保存提示

- 搜索跳页：`ApplySearchFilter` 已算出每页可见性（`page.NavItem.Visible`）。补充：查询非空且**当前选中页**无任何可见行时，`SelectPage` 到第一个有匹配的页。避免在每次按键都跳页导致抖动——仅当当前页无匹配时才跳。
- dirty 跟踪：`OnSettingChanged` 中置 `dirty = true`（新字段），保存成功后清 false，`LoadSettings` 完成后清 false。dirty 时状态栏（`statusLabel`）常驻显示“有未保存的更改”（琥珀色，不走 5 秒自动隐藏的 `ShowStatus` 路径，需绕过 `statusTimer`）。
- 关窗确认：`OnFormClosing` 中，若 `dirty && !saved && owner != null && !OwnerFormClosing`，弹 `MessageBox`（是=保存并关闭 / 否=放弃更改 / 取消=留在窗口，`e.Cancel = true`）。保存分支复用底部保存按钮的逻辑（建议把保存逻辑抽成 `bool TrySaveSettings()` 供两处调用）。**自测路径不弹窗**：`RunSettingsBindingSelfTest` 里 `owner == null` 且 `saved = true`，天然绕过，但改动后必须实际跑一遍 `--test-settings-bindings` 确认无阻塞。
- 现有“关窗静默回退预览”（`TryConsumeUnsavedPreview`）逻辑保留不动——放弃更改时它负责把浮窗恢复到 baseline。

### T5 — 文案通俗化

- `GetSettingHint` 的 fallback 从返回属性名改为返回空串（英文属性名直接露给用户是最差体验）。`SettingRow` 已兼容空 hint（不占高度）。
- `SettingTitles` 重写原则：不懂代码的人能看懂。示例（可再润色）：`HoverOpacityEnabled` “悬停透明 95%”→“鼠标靠近时隐藏”；`SensitiveMouseRangePixels` “鼠标判定边长”→“触发距离（像素）”；`CodexRadarSoftwareMode` “检测软件”→“监控哪个软件”；`*ServiceProbeToken` “检测服务可用性”→“测一下数据源是否可用”；`CodexModelIqTestEnabled` “覆盖实时 IQ 数据”→“用测试值代替实时 IQ（调试用）”；`*RenderVariant` 统一为“外观风格”（说明文字移入 hint：“切换后立即预览，可随时切回”）。
- `SettingHints` 补齐所有仍会出现在 UI 的设置项（尤其此前无 hint 的：`Width/Height/LeftX/BottomY` 系列写清参照系“逻辑像素，距屏幕左/下边缘”；`CloudEndpointTestSeed`、`CloudStatusRegionMask`、`ThermalTestMode`、`AlertTestEnabled`、各 `RandomTest*` 写明“仅用于测试显示效果，日常保持关闭”）。
- 新组名 / 页描述已包含在 T1 schema 中，勿再改回“渲染变体”等术语。

### T6 — 构建、自测、部署、记录（按 AGENTS.md 默认规则）

1. `Core/ProductIdentity.cs` 版本号 bump（与 CHANGELOG 记录同版本）。
2. `Build-Arm64.ps1` ARM64 构建；跑 `--test-settings-bindings`、`--test`（如涉及布局再加 `--test-layout`）。
3. 备份现有正式 exe → 覆盖 → 重启（用户已有长期授权：build→test→deploy 全流程不必逐步询问）。
4. `Docs/Maintenance/CHANGELOG.jsonl` 追加记录；`Docs/Indexes/FEATURE_INDEX.jsonl` 更新（设置页结构变化、新布局页、VariantPicker）；若新增可复用控件/接口，更新 `Docs/Interfaces/INTERFACE_INDEX.jsonl`。
5. **向用户明文汇报 §5 的增删/移动清单**（用户明确要求）。

## 3. 验收清单

- [ ] `--test-settings-bindings` PASS（含 `VerifySelfTest` 必需绑定、无控件裁剪、滚轮滚动）
- [ ] `--test` PASS
- [ ] 每个含 `!` 组的页面显示「复杂选项」折叠头，默认收起，点击展开/收起正常，搜索时直接显示匹配的高级项
- [ ] 6 个窗口的外观风格为缩略图卡片选择，点选即预览；ClaudeRadar 保持下拉在复杂选项内
- [ ] 布局页可视化编辑按钮可打开全局布局编辑器；几何数字框全部收在布局页复杂选项内
- [ ] 有未保存更改时状态栏有常驻提示，关窗弹三选确认
- [ ] 设置窗口无任何蓝色/青色/紫色残留（OLED 无蓝约束）
- [ ] 全部标题/提示为通俗中文，无英文属性名裸露

## 4. 关键风险与注意

- `VerifySelfTest` 的 `required` 绑定数组是硬约束，重排页面时一项都不能丢；若刻意从 UI 移除某绑定，必须同步改该数组并在汇报中说明。
- `WidgetSettings` 的属性、持久化、迁移**一律不动**——本次只重构 UI 层。被移出 UI 的属性仍走 defaults/clone/load/save/normalize。
- 样例渲染（`RenderVariantSamples`）会构造真实窗体对象，只允许在 UI 线程、`owner != null` 且目标 PNG 缺失时执行；异常一律吞掉降级为文字卡片。
- 布局相关自绘控件注意 [FitFontSize 教训]（`Docs/` 与记忆中有记录）：测量宽度必须等于实际绘制可用宽度。
- `Core/CodexRadarForm.cs.bak` 是历史备份，不参与编译（构建脚本只收 `*.cs`），别把它当源码改。

## 5. 增删/移动清单（完成后原样向用户汇报，允许执行时微调并同步更新此表）

**删除（仅从设置界面移除，功能与存储保留）**
- 无硬删除。所有原设置项仍可通过界面（含复杂选项区）或 settings.ini 访问。

**新增（UI 元素，非新设置项）**
- 「布局与位置」页（可视化编辑入口 + 显示器分配 + 几何精调）
- 每页「复杂选项」折叠区
- 外观风格缩略图选择器（替代 6 个下拉框）
- 未保存更改常驻提示 + 关窗三选确认
- 4 个 `*RefreshToken` 数字框改为「立即刷新」按钮（交互变化，值语义不变）

**移动**
- 几何设置（7 组宽/高/X/Y/偏移）：各窗口页 → 布局页复杂选项
- 显示器分配 + 全局布局编辑：系统页 → 布局页
- 收进各页复杂选项：恢复与保护、FPS 调试（系统）；延迟显现、覆盖与反向（隐藏页）；网站数据源、IQ/效率测试覆盖、随机测试（Codex/Claude Radar）；ThermalTestMode（功耗）；手动刷新令牌、云服务端点、CleanIp/网络状态测试（网络）；告警测试（操作面板）；ClaudeRadarRenderVariant（预留项）
