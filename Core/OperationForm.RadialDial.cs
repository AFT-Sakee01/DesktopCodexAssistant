using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// RadialDial (added 1.0.4.57, tiered menu + custom icon set added 1.0.4.58): the operation panel is
// always docked to the screen's bottom-left corner (PositionOperationWindow anchors left to
// workArea.Left and bottom to workArea.Bottom), so a fan menu can only grow right (+X) and up (-Y)
// from its core -- a 90-degree quadrant, never a full circle. This is genuinely a different
// interaction model from the other five OperationRenderVariant members (Classic/Typographic/
// AmberHud/WarmCard/Phosphor), which only swap paint while sharing one flat 6x2 button grid
// (GetButtonRects/HitTest/ExecuteButton/IsButtonVisible in OperationForm.cs). RadialDial owns its
// own geometry, hit-testing, icon set and mouse routing here; OperationForm.cs only branches into
// this file via IsRadialDialActive() at the top of GetDesiredSize() and the mouse handlers, plus one
// case in the DrawOperationWindow dispatcher. None of the grid code is touched.
//
// The menu is a tiered tree, not a flat ring: Root shows category nodes; clicking one drills into
// that category's children; the Settings category has one further branch (SpecialSettingsBranch,
// labelled "特殊设置") that drills one level deeper into the three actions that used to live only in
// the AiQuickMenuForm popup. Every leaf action still reuses the existing single-purpose
// implementation instead of duplicating it (ExecuteButton for plain single-click buttons,
// PulseSeelenDockFromOperationPanel / ExecuteRestartButtonDoubleClick / ExecuteAppSettingsButtonDoubleClick
// for the two pairs already split apart, setAiBlockAction/setQuotaPlanAction/RunElevatedCtfmonRestartHelper
// for the three folded-in items). All icon glyphs below are drawn fresh for this variant -- none call
// the flat grid's Draw*Glyph methods.
internal sealed partial class OperationForm
{
    // Angles are degrees measured counter-clockwise from straight right (0 = east, 90 = north/up),
    // matching the up-right quadrant the window is physically allowed to occupy. A small inset from
    // the exact 0/90 edges keeps items from being clipped against the window bounds.
    private const float RadialArcStartDeg = 8.0f;
    private const float RadialArcEndDeg = 82.0f;
    private const float RadialGapScale = 0.34f;
    private const float RadialMinItemSpacingScale = 1.22f;

    private RadialLevel radialLevel = RadialLevel.Collapsed;
    private RadialCategoryId? radialActiveCategory;
    private int radialHoveredIndex = -1;
    private int radialPressedIndex = -1;
    private bool radialCoreHovered;
    private bool radialCorePressed;
    private int radialCtfRestartRunning;

    private enum RadialLevel
    {
        Collapsed,
        Root,
        Category,
        SubCategory
    }

    private enum RadialCategoryId
    {
        Settings,
        Power,
        SystemTools,
        Battery,
        Assist
    }

    private enum RadialItemId
    {
        NormalSettings,
        WindowsSettings,
        SpecialSettingsBranch,
        PowerMenu,
        RestartApp,
        PulseDock,
        TaskManager,
        QuickSettings,
        SystemToolsMenu,
        Refresh,
        BatteryCarePause,
        BatteryLimitRestore,
        AiStudio,
        LiveCaptions,
        HoverOpacityToggle,
        LinkBlockToggle,
        QuotaPlanToggle,
        CtfRestart
    }

    private enum RadialHitKind
    {
        None,
        Core,
        Item
    }

    private sealed class RadialLayout
    {
        public RadialLevel Level;
        public Size WindowSize;
        public RectangleF Core;
        public List<RadialCategoryId> Categories;
        public List<RadialItemId> Items;
        public RectangleF[] Rects;
    }

    private bool IsRadialDialActive()
    {
        return this.currentSettings.OperationRenderVariant == OperationRenderVariant.RadialDial;
    }

    // Test/render-harness only: jumps straight to the category ring (Root) instead of the collapsed
    // core, since that is the most illustrative single frame of the new tiered design.
    internal void SetRadialDialExpandedForSample(bool expanded)
    {
        this.radialLevel = expanded ? RadialLevel.Root : RadialLevel.Collapsed;
    }

    private void ClearRadialTransientState()
    {
        this.radialHoveredIndex = -1;
        this.radialPressedIndex = -1;
        this.radialCoreHovered = false;
        this.radialCorePressed = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Tree data
    // ---------------------------------------------------------------------------------------------

    private List<RadialCategoryId> GetRootCategories()
    {
        List<RadialCategoryId> categories = new List<RadialCategoryId>
        {
            RadialCategoryId.Settings,
            RadialCategoryId.Power,
            RadialCategoryId.SystemTools
        };
        if (ShouldShowBatteryCareButtons())
        {
            categories.Add(RadialCategoryId.Battery);
        }

        categories.Add(RadialCategoryId.Assist);
        return categories;
    }

    private static List<RadialItemId> GetCategoryChildren(RadialCategoryId category)
    {
        switch (category)
        {
            case RadialCategoryId.Settings:
                return new List<RadialItemId> { RadialItemId.NormalSettings, RadialItemId.SpecialSettingsBranch, RadialItemId.WindowsSettings };
            case RadialCategoryId.Power:
                return new List<RadialItemId> { RadialItemId.PowerMenu, RadialItemId.PulseDock, RadialItemId.RestartApp };
            case RadialCategoryId.SystemTools:
                return new List<RadialItemId> { RadialItemId.TaskManager, RadialItemId.QuickSettings, RadialItemId.SystemToolsMenu, RadialItemId.Refresh };
            case RadialCategoryId.Battery:
                return new List<RadialItemId> { RadialItemId.BatteryCarePause, RadialItemId.BatteryLimitRestore };
            case RadialCategoryId.Assist:
                return new List<RadialItemId> { RadialItemId.AiStudio, RadialItemId.LiveCaptions, RadialItemId.HoverOpacityToggle };
            default:
                return new List<RadialItemId>();
        }
    }

    // The only branch deeper than one level: Settings > 特殊设置 > (link block / quota plan / CTF).
    private static List<RadialItemId> GetSpecialSettingsChildren()
    {
        return new List<RadialItemId> { RadialItemId.LinkBlockToggle, RadialItemId.QuotaPlanToggle, RadialItemId.CtfRestart };
    }

    private static string GetCategoryName(RadialCategoryId category)
    {
        switch (category)
        {
            case RadialCategoryId.Settings: return "设置";
            case RadialCategoryId.Power: return "电源";
            case RadialCategoryId.SystemTools: return "系统工具";
            case RadialCategoryId.Battery: return "电池维护";
            case RadialCategoryId.Assist: return "辅助功能";
            default: return string.Empty;
        }
    }

    private static Color GetCategoryColor(RadialCategoryId category)
    {
        switch (category)
        {
            case RadialCategoryId.Settings: return Color.FromArgb(198, 193, 182);
            case RadialCategoryId.Power: return Color.FromArgb(255, 176, 89);
            case RadialCategoryId.SystemTools: return Color.FromArgb(120, 214, 189);
            case RadialCategoryId.Battery: return Color.FromArgb(140, 214, 150);
            case RadialCategoryId.Assist: return Color.FromArgb(214, 150, 214);
            default: return DesignTokens.Colors.GlyphMuted;
        }
    }

    // The "advanced" branch (特殊设置 and its three children) gets its own color instead of
    // inheriting Settings' -- crossing into it is a deliberate second click, and the color change
    // reinforces that these three actions are a different kind of thing (process kill / elevation).
    private static readonly Color RadialAdvancedColor = Color.FromArgb(224, 120, 120);

    private static Color GetLeafColor(RadialItemId item)
    {
        switch (item)
        {
            case RadialItemId.NormalSettings:
            case RadialItemId.WindowsSettings:
                return GetCategoryColor(RadialCategoryId.Settings);
            case RadialItemId.SpecialSettingsBranch:
            case RadialItemId.LinkBlockToggle:
            case RadialItemId.QuotaPlanToggle:
            case RadialItemId.CtfRestart:
                return RadialAdvancedColor;
            case RadialItemId.PowerMenu:
            case RadialItemId.RestartApp:
            case RadialItemId.PulseDock:
                return GetCategoryColor(RadialCategoryId.Power);
            case RadialItemId.TaskManager:
            case RadialItemId.QuickSettings:
            case RadialItemId.SystemToolsMenu:
            case RadialItemId.Refresh:
                return GetCategoryColor(RadialCategoryId.SystemTools);
            case RadialItemId.BatteryCarePause:
            case RadialItemId.BatteryLimitRestore:
                return GetCategoryColor(RadialCategoryId.Battery);
            default:
                return GetCategoryColor(RadialCategoryId.Assist);
        }
    }

    // Blends a category hue into a warm-neutral base at restrained saturation, matching the
    // project's "no peak-white/saturated fills" convention even though Classic isn't OLED-only.
    private static Color MutedCategoryTint(Color categoryColor)
    {
        int r = (int)Math.Round(categoryColor.R * 0.40 + 233.0 * 0.60);
        int g = (int)Math.Round(categoryColor.G * 0.40 + 228.0 * 0.60);
        int b = (int)Math.Round(categoryColor.B * 0.40 + 220.0 * 0.60);
        return Color.FromArgb(ClampByte(r), ClampByte(g), ClampByte(b));
    }

    private bool IsRadialItemUnavailable(RadialItemId item)
    {
        if (item == RadialItemId.AiStudio)
        {
            return !this.windowsAiStudioAvailable;
        }

        if (item == RadialItemId.LiveCaptions)
        {
            return !this.liveCaptionsAvailable;
        }

        return false;
    }

    private bool IsRadialItemBusy(RadialItemId item)
    {
        if (item == RadialItemId.PowerMenu)
        {
            return Interlocked.CompareExchange(ref this.seelenPowerMenuRequestRunning, 0, 0) != 0;
        }

        if (item == RadialItemId.BatteryCarePause)
        {
            return this.batteryCarePauseRunning;
        }

        if (item == RadialItemId.BatteryLimitRestore)
        {
            return this.batteryLimitRestoreRunning;
        }

        if (item == RadialItemId.CtfRestart)
        {
            return Interlocked.CompareExchange(ref this.radialCtfRestartRunning, 0, 0) != 0;
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // Geometry -- single ring per level (root categories, or the current level's children)
    // ---------------------------------------------------------------------------------------------

    private RadialLayout ComputeRadialLayout()
    {
        int margin = S(3);
        int coreSize = GetStartButtonSize();
        RadialLayout layout = new RadialLayout();
        layout.Level = this.radialLevel;

        int count;
        if (this.radialLevel == RadialLevel.Root)
        {
            layout.Categories = GetRootCategories();
            count = layout.Categories.Count;
        }
        else if (this.radialLevel == RadialLevel.Category && this.radialActiveCategory.HasValue)
        {
            layout.Items = GetCategoryChildren(this.radialActiveCategory.Value);
            count = layout.Items.Count;
        }
        else if (this.radialLevel == RadialLevel.SubCategory)
        {
            layout.Items = GetSpecialSettingsChildren();
            count = layout.Items.Count;
        }
        else
        {
            layout.WindowSize = new Size(margin * 2 + coreSize, margin * 2 + coreSize);
            layout.Core = new RectangleF(margin, margin, coreSize, coreSize);
            layout.Rects = new RectangleF[0];
            return layout;
        }

        int itemSize = GetSmallButtonSize();
        float baseRadius = coreSize / 2.0f + coreSize * RadialGapScale + itemSize / 2.0f;
        float radius = ComputeRingRadius(baseRadius, count, itemSize);
        PointF[] offsets = ComputeArcOffsets(count, radius);

        float maxRight = coreSize / 2.0f;
        float maxUp = coreSize / 2.0f;
        AccumulateExtent(offsets, itemSize, ref maxRight, ref maxUp);

        float coreCenterX = margin + coreSize / 2.0f;
        int windowWidth = (int)Math.Ceiling(coreCenterX + maxRight + margin);
        int windowHeight = (int)Math.Ceiling(margin + coreSize / 2.0f + maxUp + margin);
        float coreCenterY = windowHeight - margin - coreSize / 2.0f;

        layout.WindowSize = new Size(windowWidth, windowHeight);
        layout.Core = new RectangleF(coreCenterX - coreSize / 2.0f, coreCenterY - coreSize / 2.0f, coreSize, coreSize);
        layout.Rects = BuildRectsFromOffsets(offsets, coreCenterX, coreCenterY, itemSize);
        return layout;
    }

    // Grows the ring radius beyond the resting gap-from-core radius whenever the arc is dense enough
    // that evenly-spaced items would otherwise overlap.
    private static float ComputeRingRadius(float baseRadius, int itemCount, float itemSize)
    {
        if (itemCount <= 1)
        {
            return baseRadius;
        }

        float archSpanDeg = RadialArcEndDeg - RadialArcStartDeg;
        double gapRad = (archSpanDeg / (itemCount - 1)) * Math.PI / 180.0;
        double sinHalfGap = Math.Sin(gapRad / 2.0);
        if (sinHalfGap < 0.001)
        {
            return baseRadius;
        }

        float required = (float)((itemSize * RadialMinItemSpacingScale) / (2.0 * sinHalfGap));
        return Math.Max(baseRadius, required);
    }

    // Offsets are in "quadrant space": origin at the core center, +X right, +Y UP (not screen Y-down
    // yet -- BuildRectsFromOffsets flips the sign when placing items).
    private static PointF[] ComputeArcOffsets(int count, float radius)
    {
        PointF[] result = new PointF[count];
        if (count <= 0)
        {
            return result;
        }

        if (count == 1)
        {
            double midRad = ((RadialArcStartDeg + RadialArcEndDeg) / 2.0) * Math.PI / 180.0;
            result[0] = new PointF((float)(Math.Cos(midRad) * radius), (float)(Math.Sin(midRad) * radius));
            return result;
        }

        for (int i = 0; i < count; i++)
        {
            double t = (double)i / (count - 1);
            double angleDeg = RadialArcStartDeg + t * (RadialArcEndDeg - RadialArcStartDeg);
            double angleRad = angleDeg * Math.PI / 180.0;
            result[i] = new PointF((float)(Math.Cos(angleRad) * radius), (float)(Math.Sin(angleRad) * radius));
        }

        return result;
    }

    private static void AccumulateExtent(PointF[] offsets, int itemSize, ref float maxRight, ref float maxUp)
    {
        float half = itemSize / 2.0f;
        for (int i = 0; i < offsets.Length; i++)
        {
            maxRight = Math.Max(maxRight, offsets[i].X + half);
            maxUp = Math.Max(maxUp, offsets[i].Y + half);
        }
    }

    private static RectangleF[] BuildRectsFromOffsets(PointF[] offsets, float coreCenterX, float coreCenterY, int size)
    {
        RectangleF[] rects = new RectangleF[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
        {
            float cx = coreCenterX + offsets[i].X;
            float cy = coreCenterY - offsets[i].Y;
            rects[i] = new RectangleF(cx - size / 2.0f, cy - size / 2.0f, size, size);
        }

        return rects;
    }

    // ---------------------------------------------------------------------------------------------
    // Hit-testing / mouse routing (branched into from OperationForm.cs's OnMouseDown/Up/Move/Leave)
    // ---------------------------------------------------------------------------------------------

    private RadialHitKind RadialHitTest(Point point, out int index)
    {
        index = -1;
        RadialLayout layout = ComputeRadialLayout();
        if (layout.Core.Contains(point.X, point.Y))
        {
            return RadialHitKind.Core;
        }

        if (layout.Rects != null)
        {
            for (int i = 0; i < layout.Rects.Length; i++)
            {
                if (layout.Rects[i].Contains(point.X, point.Y))
                {
                    index = i;
                    return RadialHitKind.Item;
                }
            }
        }

        return RadialHitKind.None;
    }

    private void HandleRadialMouseMove(MouseEventArgs e)
    {
        int index;
        RadialHitKind kind = RadialHitTest(e.Location, out index);
        bool coreHover = kind == RadialHitKind.Core;
        int itemHover = kind == RadialHitKind.Item ? index : -1;
        if (coreHover == this.radialCoreHovered && itemHover == this.radialHoveredIndex)
        {
            return;
        }

        this.radialCoreHovered = coreHover;
        this.radialHoveredIndex = itemHover;
        UpdateRadialHoverToolTip(kind, index, e.Location);
        RenderLayeredWindow();
    }

    private void HandleRadialMouseLeave()
    {
        if (!this.radialCoreHovered && this.radialHoveredIndex < 0)
        {
            return;
        }

        this.radialCoreHovered = false;
        this.radialHoveredIndex = -1;
        HideHoverToolTip();
        RenderLayeredWindow();
    }

    private void HandleRadialMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        HideHoverToolTip();
        int index;
        RadialHitKind kind = RadialHitTest(e.Location, out index);
        this.radialCorePressed = kind == RadialHitKind.Core;
        this.radialPressedIndex = kind == RadialHitKind.Item ? index : -1;
        RenderLayeredWindow();
    }

    private void HandleRadialMouseUp(MouseEventArgs e)
    {
        int index;
        RadialHitKind kind = RadialHitTest(e.Location, out index);
        bool corePressed = this.radialCorePressed;
        int pressedIndex = this.radialPressedIndex;
        this.radialCorePressed = false;
        this.radialPressedIndex = -1;
        RenderLayeredWindow();

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (kind == RadialHitKind.Core && corePressed)
        {
            NavigateRadialBack();
            return;
        }

        if (kind != RadialHitKind.Item || index != pressedIndex)
        {
            return;
        }

        if (this.radialLevel == RadialLevel.Root)
        {
            List<RadialCategoryId> categories = GetRootCategories();
            if (index < 0 || index >= categories.Count)
            {
                return;
            }

            this.radialActiveCategory = categories[index];
            this.radialLevel = RadialLevel.Category;
            ClearRadialTransientState();
            ApplyRadialSizeAndPosition();
            return;
        }

        if (this.radialLevel == RadialLevel.Category || this.radialLevel == RadialLevel.SubCategory)
        {
            List<RadialItemId> items = this.radialLevel == RadialLevel.SubCategory
                ? GetSpecialSettingsChildren()
                : GetCategoryChildren(this.radialActiveCategory.GetValueOrDefault());
            if (index < 0 || index >= items.Count)
            {
                return;
            }

            RadialItemId item = items[index];
            if (item == RadialItemId.SpecialSettingsBranch)
            {
                this.radialLevel = RadialLevel.SubCategory;
                ClearRadialTransientState();
                ApplyRadialSizeAndPosition();
                return;
            }

            ExecuteRadialItem(item);
            CollapseRadialFully();
        }
    }

    private void NavigateRadialBack()
    {
        if (this.radialLevel == RadialLevel.Collapsed)
        {
            this.radialLevel = RadialLevel.Root;
        }
        else if (this.radialLevel == RadialLevel.Root)
        {
            this.radialLevel = RadialLevel.Collapsed;
        }
        else if (this.radialLevel == RadialLevel.SubCategory)
        {
            this.radialLevel = RadialLevel.Category;
        }
        else if (this.radialLevel == RadialLevel.Category)
        {
            this.radialLevel = RadialLevel.Root;
            this.radialActiveCategory = null;
        }

        ClearRadialTransientState();
        ApplyRadialSizeAndPosition();
    }

    private void CollapseRadialFully()
    {
        this.radialLevel = RadialLevel.Collapsed;
        this.radialActiveCategory = null;
        ClearRadialTransientState();
        ApplyRadialSizeAndPosition();
    }

    // Mirrors the resize+reposition+redraw the periodic tick already does (OperationForm.cs's timer
    // handler), fired immediately on every level change instead of waiting up to one tick interval.
    private void ApplyRadialSizeAndPosition()
    {
        Size desired = GetDesiredSize();
        if (this.Size != desired)
        {
            this.Size = desired;
        }

        PositionOperationWindow();
        RenderLayeredWindow();
    }

    private void UpdateRadialHoverToolTip(RadialHitKind kind, int index, Point location)
    {
        string text = GetRadialTooltipText(kind, index);
        if (string.IsNullOrEmpty(text))
        {
            HideHoverToolTip();
            return;
        }

        this.toolTipButton = 0;
        this.hoverToolTip.Hide(this);
        this.hoverToolTip.Show(text, this, new Point(location.X + S(12), location.Y + S(18)), 5000);
    }

    private string GetRadialTooltipText(RadialHitKind kind, int index)
    {
        if (kind == RadialHitKind.Core)
        {
            switch (this.radialLevel)
            {
                case RadialLevel.Collapsed: return "展开操作面板";
                case RadialLevel.Root: return "收起";
                case RadialLevel.SubCategory: return "返回设置";
                default: return "返回";
            }
        }

        if (kind != RadialHitKind.Item)
        {
            return string.Empty;
        }

        if (this.radialLevel == RadialLevel.Root)
        {
            List<RadialCategoryId> categories = GetRootCategories();
            if (index < 0 || index >= categories.Count)
            {
                return string.Empty;
            }

            return GetCategoryName(categories[index]);
        }

        List<RadialItemId> items = this.radialLevel == RadialLevel.SubCategory
            ? GetSpecialSettingsChildren()
            : GetCategoryChildren(this.radialActiveCategory.GetValueOrDefault());
        if (index < 0 || index >= items.Count)
        {
            return string.Empty;
        }

        return GetLeafTooltipText(items[index]);
    }

    private string GetLeafTooltipText(RadialItemId item)
    {
        if (item == RadialItemId.SpecialSettingsBranch)
        {
            return "特殊设置\r\n链接阻断 / 额度计划 / CTF 重启";
        }

        if (IsRadialItemUnavailable(item))
        {
            if (item == RadialItemId.AiStudio)
            {
                return "AI Studio 当前不可用\r\n未检测到 ms-clicktodo 协议或 CoreAI 包";
            }

            if (item == RadialItemId.LiveCaptions)
            {
                return "实时字幕当前不可用\r\n未检测到系统实时字幕入口";
            }

            return "当前系统入口不可用";
        }

        switch (item)
        {
            case RadialItemId.NormalSettings:
                return "程序设置";
            case RadialItemId.WindowsSettings:
                return "系统设置\r\nWindows 设置";
            case RadialItemId.PowerMenu:
                return "打开 SeelenUI 电源界面\r\n不可用时尝试 Windows 安全菜单";
            case RadialItemId.Refresh:
                return "刷新所有模块";
            case RadialItemId.PulseDock:
                return "拉到前 Seelen Dock";
            case RadialItemId.RestartApp:
                return "重启 SeelenUI 和本程序";
            case RadialItemId.BatteryCarePause:
                return "关闭电池保护 24 小时";
            case RadialItemId.BatteryLimitRestore:
                return "开启电池保护";
            case RadialItemId.TaskManager:
                return "打开任务管理器";
            case RadialItemId.AiStudio:
                return "打开 AI Studio";
            case RadialItemId.QuickSettings:
                return "打开快速设置\r\n使用快捷键 Win+A";
            case RadialItemId.LiveCaptions:
                return "打开实时字幕";
            case RadialItemId.HoverOpacityToggle:
                return this.currentSettings.ForceHoverOpacityActive ? "恢复模块透明度" : "切换到悬停透明度";
            case RadialItemId.SystemToolsMenu:
                return "Windows 系统工具菜单\r\n设备管理器、磁盘管理等";
            case RadialItemId.LinkBlockToggle:
                return this.currentSettings.AiRequestProtectionManualBlockEnabled
                    ? "点击关闭链接阻断"
                    : "点击开启链接阻断\r\n阻断本程序的 OpenAI / ChatGPT / Claude 请求";
            case RadialItemId.QuotaPlanToggle:
                return this.currentSettings.CodexQuotaPlanEnabled
                    ? "点击关闭额度计划"
                    : "点击开启额度计划\r\n阈值和 goal 列表在普通设置中调整";
            case RadialItemId.CtfRestart:
                return IsRadialItemBusy(RadialItemId.CtfRestart)
                    ? "正在提权重启 ctfmon.exe..."
                    : "提权重启当前会话的 ctfmon.exe";
            default:
                return string.Empty;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Action dispatch
    // ---------------------------------------------------------------------------------------------

    private void ExecuteRadialItem(RadialItemId item)
    {
        if (IsRadialItemUnavailable(item) || IsRadialItemBusy(item))
        {
            return;
        }

        switch (item)
        {
            case RadialItemId.WindowsSettings:
                ExecuteButton(WindowsSettingsButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.PowerMenu:
                ExecuteButton(WindowsPowerMenuButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.Refresh:
                ExecuteButton(RefreshButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.PulseDock:
                PulseSeelenDockFromOperationPanel();
                break;
            case RadialItemId.RestartApp:
                ExecuteRestartButtonDoubleClick();
                break;
            case RadialItemId.BatteryCarePause:
                ExecuteButton(BatteryCarePauseButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.BatteryLimitRestore:
                ExecuteButton(BatteryLimitRestoreButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.NormalSettings:
                ExecuteAppSettingsButtonDoubleClick();
                break;
            case RadialItemId.TaskManager:
                ExecuteButton(TaskManagerButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.AiStudio:
                ExecuteButton(WindowsAiStudioButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.QuickSettings:
                ExecuteButton(WindowsQuickSettingsButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.LiveCaptions:
                ExecuteButton(LiveCaptionsButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.HoverOpacityToggle:
                ExecuteButton(HoverOpacityToggleButtonIndex, MouseButtons.Left);
                break;
            case RadialItemId.SystemToolsMenu:
                ExecuteRadialSystemToolsMenu();
                break;
            case RadialItemId.LinkBlockToggle:
                ExecuteRadialLinkBlockToggle();
                break;
            case RadialItemId.QuotaPlanToggle:
                ExecuteRadialQuotaPlanToggle();
                break;
            case RadialItemId.CtfRestart:
                BeginRadialCtfRestart();
                break;
        }
    }

    private void ExecuteRadialSystemToolsMenu()
    {
        if (!NativeMethods.OpenWindowsStartContextMenu())
        {
            ShowOperationNotification(
                "系统工具菜单",
                "未能打开 Windows 系统工具菜单。",
                ToolTipIcon.Warning);
        }
    }

    private void ExecuteRadialLinkBlockToggle()
    {
        if (this.setAiBlockAction == null)
        {
            return;
        }

        try
        {
            this.setAiBlockAction(!this.currentSettings.AiRequestProtectionManualBlockEnabled);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            ShowOperationNotification("链接阻断", "切换失败。", ToolTipIcon.Warning);
        }
    }

    private void ExecuteRadialQuotaPlanToggle()
    {
        if (this.setQuotaPlanAction == null)
        {
            return;
        }

        try
        {
            this.setQuotaPlanAction(!this.currentSettings.CodexQuotaPlanEnabled);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            ShowOperationNotification("额度计划", "切换失败。", ToolTipIcon.Warning);
        }
    }

    private void BeginRadialCtfRestart()
    {
        if (Interlocked.CompareExchange(ref this.radialCtfRestartRunning, 1, 0) != 0)
        {
            return;
        }

        string correlationId = Guid.NewGuid().ToString("N");
        Stopwatch stopwatch = Stopwatch.StartNew();
        Program.LogInfo("radial_dial_ctfmon_restart_requested correlation_id=" + correlationId);

        Task.Run(delegate
        {
            bool success = false;
            string detail = string.Empty;
            try
            {
                success = Program.RunElevatedCtfmonRestartHelper(correlationId, out detail);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                detail = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                Program.LogInfo(
                    "radial_dial_ctfmon_restart_completed correlation_id=" +
                    correlationId +
                    ", success=" +
                    success.ToString() +
                    ", elapsed_ms=" +
                    stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    ", detail=" +
                    detail);
                Interlocked.Exchange(ref this.radialCtfRestartRunning, 0);
            }

            string resultDetail = detail;
            bool resultSuccess = success;
            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (this.IsDisposed)
                        {
                            return;
                        }

                        ShowOperationNotification(
                            "CTF 重启",
                            resultSuccess ? "已提权重启 ctfmon.exe。" : ("重启失败：" + resultDetail),
                            resultSuccess ? ToolTipIcon.Info : ToolTipIcon.Warning);
                        RenderLayeredWindow();
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }
        });

        RenderLayeredWindow();
    }

    // ---------------------------------------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------------------------------------

    private void DrawOperationWindowRadialDial(Graphics g)
    {
        ConfigureGraphics(g);
        RadialLayout layout = ComputeRadialLayout();
        if (layout.Categories != null)
        {
            for (int i = 0; i < layout.Rects.Length; i++)
            {
                DrawRadialCategoryNode(g, layout.Rects[i], layout.Categories[i], i == this.radialHoveredIndex, i == this.radialPressedIndex);
            }
        }
        else if (layout.Items != null)
        {
            for (int i = 0; i < layout.Rects.Length; i++)
            {
                DrawRadialLeafNode(g, layout.Rects[i], layout.Items[i], i == this.radialHoveredIndex, i == this.radialPressedIndex);
            }
        }

        DrawRadialCore(g, layout.Core);
    }

    // ----- shared circle chrome -----

    private void GetRadialFillAndBorder(Color? tint, bool unavailable, bool busy, bool hovered, bool pressed, out Color fill, out Color border)
    {
        int backgroundAlpha = GetBackgroundOpacityAlpha();
        double hover = hovered ? 1.0 : 0.0;
        double press = pressed ? 1.0 : 0.0;
        int fillAlpha = ScaleAlpha(ClampByte((int)Math.Round(60 + hover * 52 + press * 34)), backgroundAlpha);
        int outlineAlpha = ScaleAlpha(ClampByte((int)Math.Round(46 + hover * 68 + press * 38)), backgroundAlpha);
        if (unavailable)
        {
            fill = DesignTokens.WithAlpha(DesignTokens.Colors.Control, ScaleAlpha(34, backgroundAlpha));
        }
        else if (busy)
        {
            fill = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, ScaleAlpha(ClampByte((int)Math.Round(46 + hover * 40)), backgroundAlpha));
        }
        else if (tint.HasValue)
        {
            fill = DesignTokens.WithAlpha(MutedCategoryTint(tint.Value), fillAlpha);
        }
        else
        {
            fill = DesignTokens.White(fillAlpha);
        }

        border = DesignTokens.White(outlineAlpha);
    }

    private void DrawRadialCore(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        int backgroundAlpha = GetBackgroundOpacityAlpha();
        double hover = this.radialCoreHovered ? 1.0 : 0.0;
        double press = this.radialCorePressed ? 1.0 : 0.0;

        Color? tint = null;
        if (this.radialLevel == RadialLevel.SubCategory)
        {
            tint = RadialAdvancedColor;
        }
        else if (this.radialLevel == RadialLevel.Category && this.radialActiveCategory.HasValue)
        {
            tint = GetCategoryColor(this.radialActiveCategory.Value);
        }

        // A soft diagonal two-stop gradient reads as a considered, lightly domed surface instead of
        // the flat single-tone disc the first RadialDial cut shipped with.
        Color baseLight = tint.HasValue
            ? DesignTokens.WithAlpha(Lighten(MutedCategoryTint(tint.Value), 0.22), ScaleAlpha(ClampByte((int)Math.Round(150 + hover * 60 + press * 30)), backgroundAlpha))
            : DesignTokens.WithAlpha(Color.FromArgb(250, 248, 244), ScaleAlpha(ClampByte((int)Math.Round(130 + hover * 60 + press * 30)), backgroundAlpha));
        Color baseDeep = tint.HasValue
            ? DesignTokens.WithAlpha(Darken(MutedCategoryTint(tint.Value), 0.16), ScaleAlpha(ClampByte((int)Math.Round(96 + hover * 40 + press * 24)), backgroundAlpha))
            : DesignTokens.WithAlpha(Color.FromArgb(214, 208, 198), ScaleAlpha(ClampByte((int)Math.Round(74 + hover * 40 + press * 24)), backgroundAlpha));

        RectangleF outer = rect;
        RectangleF ring = RectangleF.Inflate(rect, -Math.Max(1.5f, rect.Width * 0.09f), -Math.Max(1.5f, rect.Height * 0.09f));

        using (GraphicsPath outerPath = new GraphicsPath())
        {
            outerPath.AddEllipse(outer);
            using (PathGradientBrush glow = new PathGradientBrush(outerPath))
            {
                glow.CenterColor = DesignTokens.WithAlpha(baseLight, ScaleAlpha(200, backgroundAlpha));
                glow.SurroundColors = new Color[] { DesignTokens.WithAlpha(baseDeep, 0) };
                glow.FocusScales = new PointF(0.2f, 0.2f);
                g.FillEllipse(glow, outer);
            }
        }

        using (LinearGradientBrush fillBrush = new LinearGradientBrush(rect, baseLight, baseDeep, LinearGradientMode.ForwardDiagonal))
        {
            g.FillEllipse(fillBrush, ring);
        }

        using (Pen outerPen = new Pen(DesignTokens.WithAlpha(Color.White, ScaleAlpha(ClampByte((int)Math.Round(70 + hover * 60 + press * 30)), backgroundAlpha)), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawEllipse(outerPen, ring);
        }

        using (Pen hairline = new Pen(DesignTokens.WithAlpha(Color.White, ScaleAlpha(30, backgroundAlpha)), Math.Max(0.75f, 0.85f * this.LayerScale)))
        {
            g.DrawEllipse(hairline, RectangleF.Inflate(outer, -0.5f, -0.5f));
        }

        DrawRadialCoreGlyph(g, GetIconRect(ring));
    }

    private void DrawRadialCategoryNode(Graphics g, RectangleF rect, RadialCategoryId category, bool hovered, bool pressed)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        Color fill;
        Color border;
        GetRadialFillAndBorder(GetCategoryColor(category), false, false, hovered, pressed, out fill, out border);

        using (SolidBrush brush = new SolidBrush(fill))
        {
            g.FillEllipse(brush, rect);
        }

        using (Pen pen = new Pen(border, Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawEllipse(pen, rect);
        }

        // Thin outer ring hints "this expands further" -- distinguishes category nodes and the
        // SpecialSettingsBranch leaf from terminal actions at a glance.
        using (Pen expandPen = new Pen(DesignTokens.WithAlpha(GetCategoryColor(category), ScaleAlpha(150, GetBackgroundOpacityAlpha())), Math.Max(0.9f, 1.1f * this.LayerScale)))
        {
            g.DrawEllipse(expandPen, RectangleF.Inflate(rect, Math.Max(1.5f, rect.Width * 0.09f), Math.Max(1.5f, rect.Height * 0.09f)));
        }

        DrawRadialCategoryGlyph(g, GetIconRect(rect), category);
    }

    private void DrawRadialLeafNode(Graphics g, RectangleF rect, RadialItemId item, bool hovered, bool pressed)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        bool unavailable = IsRadialItemUnavailable(item);
        bool busy = IsRadialItemBusy(item);
        bool expandable = item == RadialItemId.SpecialSettingsBranch;
        Color? tint = null;
        if (!unavailable && !busy)
        {
            if (item == RadialItemId.LinkBlockToggle && this.currentSettings.AiRequestProtectionManualBlockEnabled)
            {
                tint = DesignTokens.Colors.Danger;
            }
            else if (item == RadialItemId.QuotaPlanToggle && this.currentSettings.CodexQuotaPlanEnabled)
            {
                tint = Color.FromArgb(150, 200, 235);
            }
            else
            {
                tint = GetLeafColor(item);
            }
        }

        Color fill;
        Color border;
        GetRadialFillAndBorder(tint, unavailable, busy, hovered, pressed, out fill, out border);

        using (SolidBrush brush = new SolidBrush(fill))
        {
            g.FillEllipse(brush, rect);
        }

        using (Pen pen = new Pen(border, Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawEllipse(pen, rect);
        }

        if (expandable)
        {
            using (Pen expandPen = new Pen(DesignTokens.WithAlpha(RadialAdvancedColor, ScaleAlpha(150, GetBackgroundOpacityAlpha())), Math.Max(0.9f, 1.1f * this.LayerScale)))
            {
                g.DrawEllipse(expandPen, RectangleF.Inflate(rect, Math.Max(1.5f, rect.Width * 0.09f), Math.Max(1.5f, rect.Height * 0.09f)));
            }
        }

        DrawRadialLeafGlyph(g, GetIconRect(rect), item);

        if (unavailable)
        {
            using (SolidBrush veil = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, ScaleAlpha(116, GetBackgroundOpacityAlpha()))))
            {
                g.FillEllipse(veil, rect);
            }
        }
    }

    private static Color Lighten(Color c, double amount)
    {
        int r = (int)Math.Round(c.R + (255 - c.R) * amount);
        int g = (int)Math.Round(c.G + (255 - c.G) * amount);
        int b = (int)Math.Round(c.B + (255 - c.B) * amount);
        return Color.FromArgb(ClampByte(r), ClampByte(g), ClampByte(b));
    }

    private static Color Darken(Color c, double amount)
    {
        int r = (int)Math.Round(c.R * (1.0 - amount));
        int g = (int)Math.Round(c.G * (1.0 - amount));
        int b = (int)Math.Round(c.B * (1.0 - amount));
        return Color.FromArgb(ClampByte(r), ClampByte(g), ClampByte(b));
    }

    // ---------------------------------------------------------------------------------------------
    // Icon glyphs -- all fresh drawings for this variant; none reuse the flat grid's Draw*Glyph set.
    // ---------------------------------------------------------------------------------------------

    private static Pen NewGlyphPen(float width)
    {
        Pen pen = new Pen(DesignTokens.Glyph(240), width);
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        return pen;
    }

    // Collapsed/Root: a four-point compass spark (launcher mark). Category/SubCategory: a left
    // chevron, so the core visually doubles as a "back" affordance the moment you drill in.
    private void DrawRadialCoreGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) / 2.0f;

        if (this.radialLevel == RadialLevel.Category || this.radialLevel == RadialLevel.SubCategory)
        {
            using (Pen pen = NewGlyphPen(Math.Max(1.5f, 2.0f * this.LayerScale)))
            {
                g.DrawLines(pen, new PointF[]
                {
                    new PointF(cx + r * 0.42f, cy - r * 0.62f),
                    new PointF(cx - r * 0.46f, cy),
                    new PointF(cx + r * 0.42f, cy + r * 0.62f)
                });
            }

            return;
        }

        using (GraphicsPath star = new GraphicsPath())
        {
            PointF[] points = new PointF[8];
            for (int i = 0; i < 8; i++)
            {
                double angle = Math.PI / 4.0 * i;
                float len = (i % 2 == 0) ? r * 0.95f : r * 0.36f;
                points[i] = new PointF(cx + (float)Math.Cos(angle) * len, cy + (float)Math.Sin(angle) * len);
            }

            star.AddPolygon(points);
            using (SolidBrush brush = new SolidBrush(DesignTokens.Glyph(250)))
            {
                g.FillPath(brush, star);
            }
        }

        float dotR = r * 0.16f;
        using (SolidBrush dotBrush = new SolidBrush(DesignTokens.Glyph(255)))
        {
            g.FillEllipse(dotBrush, cx - dotR, cy - dotR, dotR * 2.0f, dotR * 2.0f);
        }
    }

    private void DrawRadialCategoryGlyph(Graphics g, RectangleF rect, RadialCategoryId category)
    {
        switch (category)
        {
            case RadialCategoryId.Settings:
                DrawHexNutGlyph(g, rect);
                break;
            case RadialCategoryId.Power:
                DrawPowerRingGlyph(g, rect);
                break;
            case RadialCategoryId.SystemTools:
                DrawWrenchGlyph(g, rect);
                break;
            case RadialCategoryId.Battery:
                DrawBatterySparkGlyph(g, rect);
                break;
            case RadialCategoryId.Assist:
                DrawSparkleGlyph(g, rect, 6);
                break;
        }
    }

    private void DrawRadialLeafGlyph(Graphics g, RectangleF rect, RadialItemId item)
    {
        switch (item)
        {
            case RadialItemId.NormalSettings:
                DrawAppWindowGlyph(g, rect);
                break;
            case RadialItemId.WindowsSettings:
                DrawTileGridGlyph(g, rect, 2);
                break;
            case RadialItemId.SpecialSettingsBranch:
                DrawBeakerGlyph(g, rect);
                break;
            case RadialItemId.PowerMenu:
                DrawGaugeGlyph(g, rect);
                break;
            case RadialItemId.RestartApp:
                DrawRestartLoopGlyph(g, rect);
                break;
            case RadialItemId.PulseDock:
                DrawDockChevronGlyph(g, rect);
                break;
            case RadialItemId.TaskManager:
                DrawBarsGlyph(g, rect);
                break;
            case RadialItemId.QuickSettings:
                DrawTogglesGlyph(g, rect);
                break;
            case RadialItemId.SystemToolsMenu:
                DrawTileGridGlyph(g, rect, 3);
                break;
            case RadialItemId.Refresh:
                DrawRefreshLoopGlyph(g, rect);
                break;
            case RadialItemId.BatteryCarePause:
                DrawBatteryPauseGlyph(g, rect);
                break;
            case RadialItemId.BatteryLimitRestore:
                DrawBatteryCheckGlyph(g, rect);
                break;
            case RadialItemId.AiStudio:
                DrawSparkleGlyph(g, rect, 4);
                break;
            case RadialItemId.LiveCaptions:
                DrawCaptionBubbleGlyph(g, rect);
                break;
            case RadialItemId.HoverOpacityToggle:
                DrawHalfMoonGlyph(g, rect);
                break;
            case RadialItemId.LinkBlockToggle:
                DrawChainLinkGlyph(g, rect);
                break;
            case RadialItemId.QuotaPlanToggle:
                DrawPieWedgeGlyph(g, rect);
                break;
            case RadialItemId.CtfRestart:
                DrawKeyboardGlyph(g, rect);
                break;
        }
    }

    private void DrawHexNutGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) / 2.0f;
        PointF[] hex = new PointF[6];
        for (int i = 0; i < 6; i++)
        {
            double angle = Math.PI / 3.0 * i - Math.PI / 2.0;
            hex[i] = new PointF(cx + (float)Math.Cos(angle) * r * 0.96f, cy + (float)Math.Sin(angle) * r * 0.96f);
        }

        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            g.DrawPolygon(pen, hex);
            g.DrawEllipse(pen, cx - r * 0.32f, cy - r * 0.32f, r * 0.64f, r * 0.64f);
        }
    }

    private void DrawPowerRingGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) / 2.0f;
        using (Pen pen = NewGlyphPen(Math.Max(1.6f, 2.1f * this.LayerScale)))
        {
            g.DrawArc(pen, cx - r * 0.78f, cy - r * 0.78f, r * 1.56f, r * 1.56f, -60, 300);
            g.DrawLine(pen, cx, cy - r * 0.98f, cx, cy - r * 0.24f);
        }
    }

    private void DrawWrenchGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) / 2.0f;
        using (Pen pen = NewGlyphPen(Math.Max(1.5f, 2.0f * this.LayerScale)))
        {
            g.DrawLine(pen, cx - r * 0.62f, cy + r * 0.62f, cx + r * 0.30f, cy - r * 0.30f);
            g.DrawArc(pen, cx + r * 0.06f, cy - r * 0.94f, r * 0.62f, r * 0.62f, 30, 260);
            g.DrawArc(pen, cx - r * 0.94f, cy + r * 0.06f, r * 0.62f, r * 0.62f, 210, 260);
        }
    }

    private void DrawBatterySparkGlyph(Graphics g, RectangleF rect)
    {
        RectangleF body = new RectangleF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.22f, rect.Width * 0.72f, rect.Height * 0.56f);
        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            using (GraphicsPath path = RoundedRectangle(body, Math.Max(1.2f, body.Height * 0.20f)))
            {
                g.DrawPath(pen, path);
            }

            g.DrawLine(pen, body.Right, body.Top + body.Height * 0.28f, body.Right + rect.Width * 0.10f, body.Top + body.Height * 0.28f);
            g.DrawLine(pen, body.Right, body.Bottom - body.Height * 0.28f, body.Right + rect.Width * 0.10f, body.Bottom - body.Height * 0.28f);

            PointF cx1 = new PointF(body.Left + body.Width * 0.58f, body.Top + body.Height * 0.12f);
            PointF cx2 = new PointF(body.Left + body.Width * 0.34f, body.Top + body.Height * 0.55f);
            PointF cx3 = new PointF(body.Left + body.Width * 0.62f, body.Top + body.Height * 0.55f);
            PointF cx4 = new PointF(body.Left + body.Width * 0.40f, body.Top + body.Height * 0.98f);
            g.DrawLines(pen, new PointF[] { cx1, cx2, cx3, cx4 });
        }
    }

    private void DrawSparkleGlyph(Graphics g, RectangleF rect, int points)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) / 2.0f;
        using (Pen pen = NewGlyphPen(Math.Max(1.3f, 1.7f * this.LayerScale)))
        {
            for (int i = 0; i < points; i++)
            {
                double angle = (Math.PI * 2.0 / points) * i;
                float x1 = cx + (float)Math.Cos(angle) * r * 0.30f;
                float y1 = cy + (float)Math.Sin(angle) * r * 0.30f;
                float x2 = cx + (float)Math.Cos(angle) * r * 0.98f;
                float y2 = cy + (float)Math.Sin(angle) * r * 0.98f;
                g.DrawLine(pen, x1, y1, x2, y2);
            }
        }

        using (SolidBrush brush = new SolidBrush(DesignTokens.Glyph(255)))
        {
            float dotR = r * 0.14f;
            g.FillEllipse(brush, cx - dotR, cy - dotR, dotR * 2.0f, dotR * 2.0f);
        }
    }

    private void DrawAppWindowGlyph(Graphics g, RectangleF rect)
    {
        RectangleF body = new RectangleF(rect.Left + rect.Width * 0.08f, rect.Top + rect.Height * 0.14f, rect.Width * 0.84f, rect.Height * 0.72f);
        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            using (GraphicsPath path = RoundedRectangle(body, Math.Max(1.2f, body.Height * 0.16f)))
            {
                g.DrawPath(pen, path);
            }

            float barY = body.Top + body.Height * 0.30f;
            g.DrawLine(pen, body.Left, barY, body.Right, barY);
        }

        using (SolidBrush dotBrush = new SolidBrush(DesignTokens.Glyph(230)))
        {
            float dotR = body.Height * 0.075f;
            float dotY = body.Top + body.Height * 0.62f;
            g.FillEllipse(dotBrush, body.Left + body.Width * 0.24f - dotR, dotY - dotR, dotR * 2.0f, dotR * 2.0f);
            g.FillEllipse(dotBrush, body.Left + body.Width * 0.50f - dotR, dotY - dotR, dotR * 2.0f, dotR * 2.0f);
        }
    }

    private void DrawTileGridGlyph(Graphics g, RectangleF rect, int gridSize)
    {
        using (SolidBrush brush = new SolidBrush(DesignTokens.Glyph(240)))
        {
            float pad = rect.Width * (gridSize == 2 ? 0.16f : 0.16f);
            float span = rect.Width - pad * 2.0f;
            float cell = span / gridSize;
            float tile = cell * (gridSize == 2 ? 0.66f : 0.54f);
            for (int row = 0; row < gridSize; row++)
            {
                for (int col = 0; col < gridSize; col++)
                {
                    float x = rect.Left + pad + cell * col + (cell - tile) / 2.0f;
                    float y = rect.Top + pad + cell * row + (cell - tile) / 2.0f;
                    using (GraphicsPath path = RoundedRectangle(new RectangleF(x, y, tile, tile), Math.Max(0.8f, tile * 0.22f)))
                    {
                        g.FillPath(brush, path);
                    }
                }
            }
        }
    }

    private void DrawBeakerGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float top = rect.Top + rect.Height * 0.14f;
        float neckBottom = rect.Top + rect.Height * 0.42f;
        float bottom = rect.Top + rect.Height * 0.90f;
        float halfTop = rect.Width * 0.10f;
        float halfBottom = rect.Width * 0.34f;
        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            g.DrawLine(pen, cx - halfTop, top, cx - halfTop, neckBottom);
            g.DrawLine(pen, cx + halfTop, top, cx + halfTop, neckBottom);
            g.DrawLine(pen, cx - halfTop, neckBottom, cx - halfBottom, bottom);
            g.DrawLine(pen, cx + halfTop, neckBottom, cx + halfBottom, bottom);
            g.DrawArc(pen, cx - halfBottom, bottom - rect.Height * 0.10f, halfBottom * 2.0f, rect.Height * 0.20f, 0, 180);
            g.DrawLine(pen, cx - halfTop * 1.6f, top, cx + halfTop * 1.6f, top);
        }

        using (SolidBrush dot = new SolidBrush(DesignTokens.Glyph(220)))
        {
            float dotR = rect.Width * 0.045f;
            g.FillEllipse(dot, cx - rect.Width * 0.08f - dotR, bottom - rect.Height * 0.22f - dotR, dotR * 2.0f, dotR * 2.0f);
            g.FillEllipse(dot, cx + rect.Width * 0.10f - dotR, bottom - rect.Height * 0.30f - dotR, dotR * 2.0f, dotR * 2.0f);
        }
    }

    private void DrawGaugeGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height * 0.62f;
        float r = Math.Min(rect.Width, rect.Height) * 0.46f;
        using (Pen pen = NewGlyphPen(Math.Max(1.3f, 1.7f * this.LayerScale)))
        {
            g.DrawArc(pen, cx - r, cy - r, r * 2.0f, r * 2.0f, 180, 180);
            double needleAngle = Math.PI * 0.72;
            g.DrawLine(pen, cx, cy, cx + (float)Math.Cos(needleAngle) * r * 0.7f, cy - (float)Math.Sin(needleAngle) * r * 0.7f);
        }

        using (SolidBrush dot = new SolidBrush(DesignTokens.Glyph(240)))
        {
            float dotR = r * 0.16f;
            g.FillEllipse(dot, cx - dotR, cy - dotR, dotR * 2.0f, dotR * 2.0f);
        }
    }

    private void DrawRestartLoopGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) * 0.42f;
        using (Pen pen = NewGlyphPen(Math.Max(1.3f, 1.7f * this.LayerScale)))
        {
            g.DrawArc(pen, cx - r, cy - r, r * 2.0f, r * 2.0f, -40, 300);
            double headAngle = (-40) * Math.PI / 180.0;
            float hx = cx + (float)Math.Cos(headAngle) * r;
            float hy = cy + (float)Math.Sin(headAngle) * r;
            g.DrawLines(pen, new PointF[]
            {
                new PointF(hx - r * 0.36f, hy - r * 0.06f),
                new PointF(hx, hy),
                new PointF(hx - r * 0.06f, hy - r * 0.42f)
            });
        }
    }

    private void DrawDockChevronGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float top = rect.Top + rect.Height * 0.18f;
        float mid = rect.Top + rect.Height * 0.52f;
        float baseY = rect.Top + rect.Height * 0.84f;
        float halfW = rect.Width * 0.28f;
        using (Pen pen = NewGlyphPen(Math.Max(1.3f, 1.7f * this.LayerScale)))
        {
            g.DrawLines(pen, new PointF[]
            {
                new PointF(cx - halfW, mid),
                new PointF(cx, top),
                new PointF(cx + halfW, mid)
            });
            g.DrawLine(pen, rect.Left + rect.Width * 0.18f, baseY, rect.Right - rect.Width * 0.18f, baseY);
        }
    }

    private void DrawBarsGlyph(Graphics g, RectangleF rect)
    {
        using (SolidBrush brush = new SolidBrush(DesignTokens.Glyph(240)))
        {
            float baseY = rect.Bottom - rect.Height * 0.14f;
            float barW = rect.Width * 0.18f;
            float gap = rect.Width * 0.10f;
            float[] heights = { 0.40f, 0.72f, 0.56f };
            float x = rect.Left + rect.Width * 0.10f;
            for (int i = 0; i < 3; i++)
            {
                float h = rect.Height * heights[i];
                using (GraphicsPath path = RoundedRectangle(new RectangleF(x, baseY - h, barW, h), Math.Max(0.8f, barW * 0.28f)))
                {
                    g.FillPath(brush, path);
                }

                x += barW + gap;
            }
        }
    }

    private void DrawTogglesGlyph(Graphics g, RectangleF rect)
    {
        using (SolidBrush track = new SolidBrush(DesignTokens.Glyph(120)))
        using (SolidBrush knob = new SolidBrush(DesignTokens.Glyph(245)))
        {
            float rowH = rect.Height * 0.26f;
            float w = rect.Width * 0.72f;
            float x = rect.Left + (rect.Width - w) / 2.0f;
            float[] rowsY = { rect.Top + rect.Height * 0.30f, rect.Top + rect.Height * 0.66f };
            float[] knobT = { 0.72f, 0.28f };
            for (int i = 0; i < 2; i++)
            {
                RectangleF trackRect = new RectangleF(x, rowsY[i] - rowH / 2.0f, w, rowH);
                using (GraphicsPath path = RoundedRectangle(trackRect, rowH / 2.0f))
                {
                    g.FillPath(track, path);
                }

                float knobR = rowH * 0.42f;
                float knobX = trackRect.Left + trackRect.Width * knobT[i];
                g.FillEllipse(knob, knobX - knobR, rowsY[i] - knobR, knobR * 2.0f, knobR * 2.0f);
            }
        }
    }

    private void DrawRefreshLoopGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) * 0.42f;
        using (Pen pen = NewGlyphPen(Math.Max(1.3f, 1.7f * this.LayerScale)))
        {
            g.DrawArc(pen, cx - r, cy - r, r * 2.0f, r * 2.0f, 20, 320);
            double headAngle = 20.0 * Math.PI / 180.0;
            float hx = cx + (float)Math.Cos(headAngle) * r;
            float hy = cy + (float)Math.Sin(headAngle) * r;
            g.DrawLines(pen, new PointF[]
            {
                new PointF(hx + r * 0.10f, hy - r * 0.40f),
                new PointF(hx, hy),
                new PointF(hx + r * 0.42f, hy + r * 0.06f)
            });
        }
    }

    private void DrawBatteryPauseGlyph(Graphics g, RectangleF rect)
    {
        RectangleF body = DrawBatteryOutline(g, rect);
        using (Pen pen = NewGlyphPen(Math.Max(1.4f, 1.8f * this.LayerScale)))
        {
            float barTop = body.Top + body.Height * 0.24f;
            float barBottom = body.Bottom - body.Height * 0.24f;
            float x1 = body.Left + body.Width * 0.40f;
            float x2 = body.Left + body.Width * 0.58f;
            g.DrawLine(pen, x1, barTop, x1, barBottom);
            g.DrawLine(pen, x2, barTop, x2, barBottom);
        }
    }

    private void DrawBatteryCheckGlyph(Graphics g, RectangleF rect)
    {
        RectangleF body = DrawBatteryOutline(g, rect);
        using (Pen pen = NewGlyphPen(Math.Max(1.4f, 1.8f * this.LayerScale)))
        {
            g.DrawLines(pen, new PointF[]
            {
                new PointF(body.Left + body.Width * 0.30f, body.Top + body.Height * 0.52f),
                new PointF(body.Left + body.Width * 0.46f, body.Bottom - body.Height * 0.24f),
                new PointF(body.Left + body.Width * 0.74f, body.Top + body.Height * 0.24f)
            });
        }
    }

    private RectangleF DrawBatteryOutline(Graphics g, RectangleF rect)
    {
        RectangleF body = new RectangleF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.24f, rect.Width * 0.70f, rect.Height * 0.52f);
        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            using (GraphicsPath path = RoundedRectangle(body, Math.Max(1.0f, body.Height * 0.20f)))
            {
                g.DrawPath(pen, path);
            }

            float capH = body.Height * 0.40f;
            g.DrawLine(pen, body.Right, body.Top + (body.Height - capH) / 2.0f, body.Right + rect.Width * 0.10f, body.Top + (body.Height - capH) / 2.0f);
            g.DrawLine(pen, body.Right, body.Bottom - (body.Height - capH) / 2.0f, body.Right + rect.Width * 0.10f, body.Bottom - (body.Height - capH) / 2.0f);
        }

        return body;
    }

    private void DrawCaptionBubbleGlyph(Graphics g, RectangleF rect)
    {
        RectangleF body = new RectangleF(rect.Left + rect.Width * 0.08f, rect.Top + rect.Height * 0.14f, rect.Width * 0.84f, rect.Height * 0.62f);
        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            using (GraphicsPath path = RoundedRectangle(body, Math.Max(1.2f, body.Height * 0.30f)))
            {
                g.DrawPath(pen, path);
            }

            g.DrawLines(pen, new PointF[]
            {
                new PointF(body.Left + body.Width * 0.30f, body.Bottom),
                new PointF(body.Left + body.Width * 0.22f, body.Bottom + rect.Height * 0.14f),
                new PointF(body.Left + body.Width * 0.48f, body.Bottom)
            });

            float lineY1 = body.Top + body.Height * 0.38f;
            float lineY2 = body.Top + body.Height * 0.64f;
            g.DrawLine(pen, body.Left + body.Width * 0.18f, lineY1, body.Right - body.Width * 0.18f, lineY1);
            g.DrawLine(pen, body.Left + body.Width * 0.18f, lineY2, body.Right - body.Width * 0.40f, lineY2);
        }
    }

    private void DrawHalfMoonGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) * 0.44f;
        RectangleF circle = new RectangleF(cx - r, cy - r, r * 2.0f, r * 2.0f);
        using (SolidBrush brush = new SolidBrush(DesignTokens.Glyph(235)))
        {
            using (GraphicsPath half = new GraphicsPath())
            {
                half.AddPie(circle.X, circle.Y, circle.Width, circle.Height, 90, 180);
                g.FillPath(brush, half);
            }
        }

        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            g.DrawEllipse(pen, circle);
        }
    }

    private void DrawChainLinkGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float w = rect.Width * 0.22f;
        float h = rect.Height * 0.16f;
        bool blocked = this.currentSettings.AiRequestProtectionManualBlockEnabled;
        using (Pen pen = NewGlyphPen(Math.Max(1.3f, 1.7f * this.LayerScale)))
        {
            g.DrawArc(pen, cx - w * 1.7f, cy - h, w * 1.5f, h * 2.0f, 90, 180);
            g.DrawArc(pen, cx + w * 0.2f, cy - h, w * 1.5f, h * 2.0f, 270, 180);
            if (blocked)
            {
                g.DrawLine(pen, cx - w * 1.1f, cy - h * 1.5f, cx + w * 1.1f, cy + h * 1.5f);
            }
            else
            {
                g.DrawLine(pen, cx - w * 0.55f, cy, cx + w * 0.55f, cy);
            }
        }
    }

    private void DrawPieWedgeGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) * 0.46f;
        RectangleF circle = new RectangleF(cx - r, cy - r, r * 2.0f, r * 2.0f);
        bool enabled = this.currentSettings.CodexQuotaPlanEnabled;
        using (SolidBrush wedge = new SolidBrush(DesignTokens.Glyph(enabled ? 235 : 150)))
        {
            g.FillPie(wedge, circle.X, circle.Y, circle.Width, circle.Height, -90, 110);
        }

        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            g.DrawEllipse(pen, circle);
        }
    }

    private void DrawKeyboardGlyph(Graphics g, RectangleF rect)
    {
        RectangleF body = new RectangleF(rect.Left + rect.Width * 0.10f, rect.Top + rect.Height * 0.22f, rect.Width * 0.80f, rect.Height * 0.56f);
        using (Pen pen = NewGlyphPen(Math.Max(1.2f, 1.5f * this.LayerScale)))
        {
            using (GraphicsPath path = RoundedRectangle(body, Math.Max(1.2f, body.Height * 0.22f)))
            {
                g.DrawPath(pen, path);
            }

            float rowY = body.Top + body.Height * 0.34f;
            float step = body.Width / 4.0f;
            for (int i = 0; i < 3; i++)
            {
                float x = body.Left + step * (i + 1);
                g.DrawLine(pen, x - step * 0.20f, rowY, x + step * 0.20f, rowY);
            }

            g.DrawLine(
                pen,
                body.Left + body.Width * 0.22f,
                body.Bottom - body.Height * 0.26f,
                body.Right - body.Width * 0.22f,
                body.Bottom - body.Height * 0.26f);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Hidden-mode burn-in hit mask (see EnsureInteractionHitMask's IsRadialDialActive() branch)
    // ---------------------------------------------------------------------------------------------

    private void PaintRadialHitMask(Graphics graphics, Brush brush)
    {
        RadialLayout layout = ComputeRadialLayout();
        graphics.FillEllipse(brush, layout.Core);
        if (layout.Rects == null)
        {
            return;
        }

        for (int i = 0; i < layout.Rects.Length; i++)
        {
            graphics.FillEllipse(brush, layout.Rects[i]);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Self-test (called from OperationForm.RunSelfTest via --test-operation-panel)
    // ---------------------------------------------------------------------------------------------

    private static void RunRadialDialSelfTest()
    {
        OperationForm form = CreateRadialDialSelfTestForm();
        try
        {
            RadialLayout collapsed = form.ComputeRadialLayout();
            AssertSelfTest(collapsed.WindowSize.Width > 0 && collapsed.WindowSize.Height > 0, "radial collapsed size positive");
            AssertSelfTest(collapsed.WindowSize.Width == collapsed.WindowSize.Height, "radial collapsed panel is core-only and square");

            form.radialLevel = RadialLevel.Root;
            RadialLayout root = form.ComputeRadialLayout();
            AssertSelfTest(root.WindowSize.Width > collapsed.WindowSize.Width, "radial root width grows past the collapsed core");
            AssertSelfTest(root.Categories.Count >= 4, "radial root shows at least four categories");
            AssertRadialQuadrant(root);

            form.radialActiveCategory = RadialCategoryId.Settings;
            form.radialLevel = RadialLevel.Category;
            RadialLayout settingsLevel = form.ComputeRadialLayout();
            AssertSelfTest(settingsLevel.Items.Count == 3, "settings category has three children");
            AssertSelfTest(settingsLevel.Items.Contains(RadialItemId.SpecialSettingsBranch), "settings category includes the special-settings branch");
            AssertRadialQuadrant(settingsLevel);

            form.radialLevel = RadialLevel.SubCategory;
            RadialLayout subLevel = form.ComputeRadialLayout();
            AssertSelfTest(subLevel.Items.Count == 3, "special-settings branch has three leaves");
            AssertSelfTest(subLevel.Items.Contains(RadialItemId.CtfRestart), "special-settings branch includes CTF restart");
            AssertRadialQuadrant(subLevel);
        }
        finally
        {
            form.Dispose();
        }
    }

    private static void AssertRadialQuadrant(RadialLayout layout)
    {
        RectangleF windowBounds = new RectangleF(0, 0, layout.WindowSize.Width, layout.WindowSize.Height);
        for (int i = 0; i < layout.Rects.Length; i++)
        {
            AssertSelfTest(windowBounds.Contains(layout.Rects[i]), "radial item stays inside the window bounds");
            AssertSelfTest(
                layout.Rects[i].Right >= layout.Core.Left && layout.Rects[i].Bottom <= layout.Core.Bottom,
                "radial item stays in the up-right quadrant from the core (90 degree constraint)");
        }
    }

    private static OperationForm CreateRadialDialSelfTestForm()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.OperationRenderVariant = OperationRenderVariant.RadialDial;
        settings.Normalize();
        return new OperationForm(
            settings,
            delegate { },
            delegate { },
            delegate { },
            delegate(string title, string message, ToolTipIcon icon) { },
            delegate { return true; },
            delegate { return true; },
            delegate { return true; },
            delegate(bool enabled) { return enabled; },
            delegate(bool enabled) { return enabled; });
    }
}
