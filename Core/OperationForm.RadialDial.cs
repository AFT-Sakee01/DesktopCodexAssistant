using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// RadialDial (added 1.0.4.57, tiered custom-icon menu 1.0.4.58, generic multi-level tree 1.0.4.59+):
// the operation panel is always docked to the screen's bottom-left corner (PositionOperationWindow
// anchors left to workArea.Left and bottom to workArea.Bottom), so a fan menu can only grow right
// (+X) and up (-Y) from its core -- a 90-degree quadrant, never a full circle. RadialDial owns its
// own geometry, hit-testing, icon set and mouse routing in this file; OperationForm.cs only branches
// into it via IsRadialDialActive() at the top of GetDesiredSize(), the mouse handlers, one case in
// the DrawOperationWindow dispatcher, and one idle-collapse check inside ProcessSharedInteractionTick.
//
// The menu is a generic tree (RadialNode: Id/Label/color/icon delegate/Children/Execute/toggle/
// unavailable/busy/tooltip), not a hand-coded set of enums switched over per level. Extending the
// menu -- adding a button or a whole new nesting level -- means adding nodes inside BuildRadialRoots()
// and nothing else; ComputeRadialLayout/hit-testing/drawing/click-dispatch all walk the tree
// generically and auto-distribute each level's siblings across a density-aware arc inside the
// up-right quadrant. Sparse levels keep the old compact 8-82 degree arc; denser levels expand
// toward the full 0-90 degree quadrant so large setting groups do not bunch up in the middle.
//
// Levels stay visible simultaneously while navigating deeper (drilling into a category does NOT hide
// the ring(s) above it) -- radialSelectionPathIds records which sibling is "expanded" at each depth,
// and every level from the root ring down to (path.Count + 1) renders every tick. Gray rail lines
// connect siblings within a level; a light-green line traces core -> selected L1 -> selected L2 -> ...
// to show the active drill path across rings. Selected (expanded) branch nodes get a blue ring +
// light-blue fill; toggle-type leaves show green (on) / red (off) fill instead of their category tint.
// The core shows the active L1 category's icon plus small dots for how many rings are open, and
// doubles as the open/close control. An idle timer (ShouldRadialIdleCollapse, ticked from
// OperationForm.ProcessSharedInteractionTick) auto-collapses the whole menu after a few seconds with
// no interaction, since levels no longer collapse themselves while browsing.
internal sealed partial class OperationForm
{
    // Angles are degrees measured counter-clockwise from straight right (0 = east, 90 = north/up),
    // matching the up-right quadrant the window is physically allowed to occupy.
    private const float RadialSparseArcStartDeg = 8.0f;
    private const float RadialSparseArcEndDeg = 82.0f;
    private const float RadialMediumArcStartDeg = 2.0f;
    private const float RadialMediumArcEndDeg = 88.0f;
    private const float RadialDenseArcStartDeg = 0.0f;
    private const float RadialDenseArcEndDeg = 90.0f;
    private const int RadialMediumArcMinItems = 4;
    private const int RadialDenseArcMinItems = 7;
    private const int RadialCoreAutoHideThresholdDimAlpha = 13;
    private const int RadialCoreAutoHideThresholdRingAlpha = 77;
    private const float RadialGapScale = 0.34f;
    private const float RadialLevelSpacingMultiplier = 2.0f;
    private const float RadialMinItemSpacingScale = 1.22f;
    private static readonly Color RadialSettingsColor = Color.FromArgb(198, 193, 182);
    private static readonly Color RadialSystemColor = Color.FromArgb(120, 214, 189);
    private static readonly Color RadialPowerColor = Color.FromArgb(255, 176, 89);
    private static readonly Color RadialAssistColor = Color.FromArgb(214, 150, 214);
    private static readonly Color RadialBatteryColor = Color.FromArgb(140, 214, 150);
    private static readonly Color RadialAdvancedColor = Color.FromArgb(224, 120, 120);
    private static readonly Color RadialSelectedColor = Color.FromArgb(120, 178, 255);

    private bool radialMenuOpen;
    private List<string> radialSelectionPathIds = new List<string>();
    private List<RadialNode> cachedRadialRoots;
    private bool cachedRadialRootsHasBattery;
    private int radialHoveredLevel = -1;
    private int radialHoveredIndex = -1;
    private int radialPressedLevel = -1;
    private int radialPressedIndex = -1;
    private bool radialCoreHovered;
    private bool radialCorePressed;
    private DateTime radialCoreHoverStartedUtc = DateTime.MinValue;
    private bool radialCoreAutoHideThresholdVisualActive;
    private DateTime radialLastInteractionUtc = DateTime.MinValue;
    private int radialCtfRestartRunning;
    private bool cachedRadialRootsSettingsLogicExtension;

    // A node with Children.Count > 0 is a branch (expands one more ring when clicked); a node with
    // no children is a leaf (Execute fires immediately). GetToggleState non-null marks a leaf whose
    // fill color reflects an on/off setting instead of its category tint.
    private sealed class RadialNode
    {
        public string Id;
        public string Label;
        public Color BaseColor;
        public Action<Graphics, RectangleF> DrawIcon;
        public List<RadialNode> Children = new List<RadialNode>();
        public Action Execute;
        public Func<bool> GetToggleState;
        public Func<bool> IsUnavailable;
        public Func<bool> IsBusy;
        public Func<string> GetTooltip;

        public bool IsBranch
        {
            get { return this.Children.Count > 0; }
        }
    }

    private sealed class RadialSettingToggleDescriptor
    {
        public string PropertyName;
        public string Id;
        public string Label;
        public Color Color;
        public Action<Graphics, RectangleF> DrawIcon;
        public Func<WidgetSettings, bool> GetState;
        public bool RequiresConfirmation;
        public string ConfirmationText;
    }

    private enum RadialHitKind
    {
        None,
        Core,
        Item
    }

    private struct RadialHitResult
    {
        public RadialHitKind Kind;
        public int Level;
        public int Index;
        public RadialNode Node;
    }

    private sealed class RadialLevelLayout
    {
        public List<RadialNode> Nodes;
        public RectangleF[] Rects;
    }

    private sealed class RadialLayout
    {
        public Size WindowSize;
        public RectangleF Core;
        public List<RadialLevelLayout> Levels = new List<RadialLevelLayout>();
    }

    private bool IsRadialDialActive()
    {
        return this.CurrentSettings.OperationRenderVariant == OperationRenderVariant.RadialDial;
    }

    // Test/render-harness only: jumps to the root ring (open, nothing drilled) since that is the
    // most illustrative single frame of the tiered design.
    internal void SetRadialDialExpandedForSample(bool expanded)
    {
        this.radialMenuOpen = expanded;
        this.radialSelectionPathIds = new List<string>();
    }

    private void ClearRadialTransientState()
    {
        this.radialHoveredLevel = -1;
        this.radialHoveredIndex = -1;
        this.radialPressedLevel = -1;
        this.radialPressedIndex = -1;
        this.radialCoreHovered = false;
        this.radialCorePressed = false;
        ClearRadialCoreAutoHideThresholdVisual();
    }

    // ---------------------------------------------------------------------------------------------
    // Tree data -- the only place that needs editing to add a button, a toggle, or a whole new
    // nesting level. Geometry/hit-testing/drawing/click-dispatch below are generic over this tree.
    // ---------------------------------------------------------------------------------------------

    private static RadialNode NewBranch(string id, string label, Color color, Action<Graphics, RectangleF> drawIcon)
    {
        return new RadialNode { Id = id, Label = label, BaseColor = color, DrawIcon = drawIcon };
    }

    private static RadialNode NewLeaf(
        string id,
        string label,
        Color color,
        Action<Graphics, RectangleF> drawIcon,
        Action execute,
        Func<string> tooltip,
        Func<bool> isUnavailable = null,
        Func<bool> isBusy = null)
    {
        return new RadialNode
        {
            Id = id,
            Label = label,
            BaseColor = color,
            DrawIcon = drawIcon,
            Execute = execute,
            GetTooltip = tooltip,
            IsUnavailable = isUnavailable,
            IsBusy = isBusy
        };
    }

    private static RadialNode NewToggle(
        string id,
        string label,
        Color color,
        Action<Graphics, RectangleF> drawIcon,
        Func<bool> getState,
        Action execute,
        Func<string> tooltip)
    {
        return new RadialNode
        {
            Id = id,
            Label = label,
            BaseColor = color,
            DrawIcon = drawIcon,
            Execute = execute,
            GetToggleState = getState,
            GetTooltip = tooltip
        };
    }

    private List<RadialNode> GetRadialRoots()
    {
        bool hasBattery = ShouldShowBatteryCareButtons();
        bool settingsLogicExtension =
            this.CurrentSettings != null &&
            this.CurrentSettings.OperationSettingsLogicExtensionEnabled;
        if (this.cachedRadialRoots == null ||
            hasBattery != this.cachedRadialRootsHasBattery ||
            settingsLogicExtension != this.cachedRadialRootsSettingsLogicExtension)
        {
            this.cachedRadialRoots = BuildRadialRoots(hasBattery);
            this.cachedRadialRootsHasBattery = hasBattery;
            this.cachedRadialRootsSettingsLogicExtension = settingsLogicExtension;
        }

        return this.cachedRadialRoots;
    }

    // Capped at 3 level-1 / 5 level-2 / 7 level-3 siblings so every ring stays visually tight; the
    // caps double at each depth since a level further from the core has more arc circumference.
    private List<RadialNode> BuildRadialRoots(bool includeBattery)
    {
        RadialNode settings = NewBranch("settings", "设置", RadialSettingsColor, this.DrawHexNutGlyph);
        settings.Children.Add(NewLeaf(
            "normal_settings",
            "程序设置",
            RadialSettingsColor,
            this.DrawAppWindowGlyph,
            ExecuteAppSettingsButtonDoubleClick,
            () => "程序设置"));

        RadialNode special = NewBranch("special_settings", "特殊设置", RadialAdvancedColor, this.DrawBeakerGlyph);
        special.Children.Add(NewToggle(
            "link_block",
            "链接阻断",
            RadialAdvancedColor,
            this.DrawChainLinkGlyph,
            () => this.CurrentSettings.AiRequestProtectionManualBlockEnabled,
            ExecuteRadialLinkBlockToggle,
            () => this.CurrentSettings.AiRequestProtectionManualBlockEnabled
                ? "点击关闭链接阻断"
                : "点击开启链接阻断\r\n阻断本程序的 OpenAI / ChatGPT / Claude 请求"));
        special.Children.Add(NewToggle(
            "quota_plan",
            "额度计划",
            RadialAdvancedColor,
            this.DrawPieWedgeGlyph,
            () => this.CurrentSettings.CodexQuotaPlanEnabled,
            ExecuteRadialQuotaPlanToggle,
            () => this.CurrentSettings.CodexQuotaPlanEnabled
                ? "点击关闭额度计划"
                : "点击开启额度计划\r\n阈值和 goal 列表在普通设置中调整"));
        special.Children.Add(NewLeaf(
            "ctf_restart",
            "CTF 重启",
            RadialAdvancedColor,
            this.DrawKeyboardGlyph,
            BeginRadialCtfRestart,
            () => IsRadialCtfRestartBusy() ? "正在提权重启 ctfmon.exe..." : "提权重启当前会话的 ctfmon.exe",
            isBusy: IsRadialCtfRestartBusy));
        settings.Children.Add(special);

        settings.Children.Add(NewLeaf(
            "windows_settings",
            "系统设置",
            RadialSettingsColor,
            (g, r) => DrawTileGridGlyph(g, r, 2),
            () => ExecuteButton(WindowsSettingsButtonIndex, MouseButtons.Left),
            () => "系统设置\r\nWindows 设置"));

        if (this.CurrentSettings.OperationSettingsLogicExtensionEnabled)
        {
            settings.Children.Add(BuildRadialCommonLogicBranch(includeBattery));
            settings.Children.Add(BuildRadialAllSettingsBranch());
        }

        RadialNode system = NewBranch("system", "系统", RadialSystemColor, this.DrawWrenchGlyph);
        RadialNode power = NewBranch("power", "电源", RadialPowerColor, this.DrawPowerRingGlyph);
        power.Children.Add(NewLeaf(
            "power_menu",
            "电源菜单",
            RadialPowerColor,
            this.DrawGaugeGlyph,
            () => ExecuteButton(WindowsPowerMenuButtonIndex, MouseButtons.Left),
            () => "打开 SeelenUI 电源界面\r\n不可用时尝试 Windows 安全菜单",
            isBusy: () => Interlocked.CompareExchange(ref this.seelenPowerMenuRequestRunning, 0, 0) != 0));
        power.Children.Add(NewLeaf(
            "pulse_dock",
            "置顶 Dock",
            RadialPowerColor,
            this.DrawDockChevronGlyph,
            PulseSeelenDockFromOperationPanel,
            () => "拉到前 Seelen Dock"));
        power.Children.Add(NewLeaf(
            "restart_app",
            "重启程序",
            RadialPowerColor,
            this.DrawRestartLoopGlyph,
            ExecuteRestartButtonDoubleClick,
            () => "重启 SeelenUI 和本程序"));
        system.Children.Add(power);
        system.Children.Add(NewLeaf(
            "task_manager",
            "任务管理器",
            RadialSystemColor,
            this.DrawBarsGlyph,
            () => ExecuteButton(TaskManagerButtonIndex, MouseButtons.Left),
            () => "打开任务管理器"));
        system.Children.Add(NewLeaf(
            "quick_settings",
            "快速设置",
            RadialSystemColor,
            this.DrawTogglesGlyph,
            () => ExecuteButton(WindowsQuickSettingsButtonIndex, MouseButtons.Left),
            () => "打开快速设置\r\n使用快捷键 Win+A"));
        system.Children.Add(NewSettingToggle(
            "SpecBoardAutoPopupEnabled",
            "spec_board_auto_popup",
            "自动 Spec 面板",
            RadialSystemColor,
            this.DrawAppWindowGlyph,
            s => s.SpecBoardAutoPopupEnabled));
        system.Children.Add(NewLeaf(
            "refresh",
            "刷新",
            RadialSystemColor,
            this.DrawRefreshLoopGlyph,
            () => ExecuteButton(RefreshButtonIndex, MouseButtons.Left),
            () => "刷新所有模块"));

        RadialNode assist = NewBranch("assist", "辅助", RadialAssistColor, (g, r) => DrawSparkleGlyph(g, r, 6));
        assist.Children.Add(NewLeaf(
            "ai_studio",
            "AI Studio",
            RadialAssistColor,
            (g, r) => DrawSparkleGlyph(g, r, 4),
            () => ExecuteButton(WindowsAiStudioButtonIndex, MouseButtons.Left),
            () => this.windowsAiStudioAvailable
                ? "打开 AI Studio"
                : "AI Studio 当前不可用\r\n未检测到 ms-clicktodo 协议或 CoreAI 包",
            isUnavailable: () => !this.windowsAiStudioAvailable));
        assist.Children.Add(NewLeaf(
            "live_captions",
            "实时字幕",
            RadialAssistColor,
            this.DrawCaptionBubbleGlyph,
            () => ExecuteButton(LiveCaptionsButtonIndex, MouseButtons.Left),
            () => this.liveCaptionsAvailable
                ? "打开实时字幕"
                : "实时字幕当前不可用\r\n未检测到系统实时字幕入口",
            isUnavailable: () => !this.liveCaptionsAvailable));
        assist.Children.Add(NewToggle(
            "hover_opacity",
            "悬停透明度",
            RadialAssistColor,
            this.DrawHalfMoonGlyph,
            () => this.CurrentSettings.ForceHoverOpacityActive,
            () => ExecuteButton(HoverOpacityToggleButtonIndex, MouseButtons.Left),
            () => this.CurrentSettings.ForceHoverOpacityActive ? "恢复模块透明度" : "切换到悬停透明度"));
        if (includeBattery)
        {
            assist.Children.Add(NewLeaf(
                "battery_care_pause",
                "电池保护暂停",
                RadialBatteryColor,
                this.DrawBatteryPauseGlyph,
                () => ExecuteButton(BatteryCarePauseButtonIndex, MouseButtons.Left),
                () => "关闭电池保护 24 小时",
                isBusy: () => this.batteryCarePauseRunning));
            assist.Children.Add(NewLeaf(
                "battery_limit_restore",
                "电池保护恢复",
                RadialBatteryColor,
                this.DrawBatteryCheckGlyph,
                () => ExecuteButton(BatteryLimitRestoreButtonIndex, MouseButtons.Left),
                () => "开启电池保护",
                isBusy: () => this.batteryLimitRestoreRunning));
        }

        return new List<RadialNode> { settings, system, assist };
    }

    private RadialNode BuildRadialCommonLogicBranch(bool includeBattery)
    {
        RadialNode common = NewBranch("common_logic", "常用逻辑", RadialSettingsColor, this.DrawTogglesGlyph);

        RadialNode visibility = NewBranch("common_visibility", "显示隐藏", RadialSettingsColor, this.DrawHalfMoonGlyph);
        visibility.Children.Add(NewLeaf(
            "common_hover_opacity_action",
            "悬停透明度",
            RadialAssistColor,
            this.DrawHalfMoonGlyph,
            () => ExecuteButton(HoverOpacityToggleButtonIndex, MouseButtons.Left),
            () => this.CurrentSettings.ForceHoverOpacityActive ? "恢复模块透明度" : "切换到悬停透明度"));
        visibility.Children.Add(NewSettingToggle("HoverOpacityEnabled", "common_hover_hide", "靠近隐藏", RadialSettingsColor, this.DrawHalfMoonGlyph, s => s.HoverOpacityEnabled));
        visibility.Children.Add(NewSettingToggle("AutoHoverOpacityIdleEnabled", "common_idle_hide", "空闲隐藏", RadialSettingsColor, this.DrawHalfMoonGlyph, s => s.AutoHoverOpacityIdleEnabled));
        visibility.Children.Add(NewSettingToggle("AutoHoverOpacityMaximizedEnabled", "common_max_hide", "最大化隐藏", RadialSettingsColor, this.DrawAppWindowGlyph, s => s.AutoHoverOpacityMaximizedEnabled));
        visibility.Children.Add(NewSettingToggle("OperationRadialCoreAutoHideKeepAliveEnabled", "common_core_keepalive", "圆圈保持", RadialSettingsColor, this.DrawPowerRingGlyph, s => s.OperationRadialCoreAutoHideKeepAliveEnabled));
        visibility.Children.Add(NewSettingToggle("BurnInHiddenModeColorProtectionEnabled", "common_burnin", "反色防烧屏", RadialSettingsColor, this.DrawSparkleShieldGlyph, s => s.BurnInHiddenModeColorProtectionEnabled));
        common.Children.Add(visibility);

        RadialNode aiQuota = NewBranch("common_ai_quota", "AI/额度", RadialAdvancedColor, this.DrawPieWedgeGlyph);
        aiQuota.Children.Add(NewToggle(
            "common_link_block",
            "链接阻断",
            RadialAdvancedColor,
            this.DrawChainLinkGlyph,
            () => this.CurrentSettings.AiRequestProtectionManualBlockEnabled,
            ExecuteRadialLinkBlockToggle,
            () => this.CurrentSettings.AiRequestProtectionManualBlockEnabled
                ? "点击关闭链接阻断"
                : "点击开启链接阻断\r\n阻断本程序的 OpenAI / ChatGPT / Claude 请求"));
        aiQuota.Children.Add(NewSettingToggle("AiRequestProtectionAutoEnabled", "common_ai_auto_block", "自动阻断", RadialAdvancedColor, this.DrawChainLinkGlyph, s => s.AiRequestProtectionAutoEnabled));
        aiQuota.Children.Add(NewToggle(
            "common_quota_plan",
            "额度计划",
            RadialAdvancedColor,
            this.DrawPieWedgeGlyph,
            () => this.CurrentSettings.CodexQuotaPlanEnabled,
            ExecuteRadialQuotaPlanToggle,
            () => this.CurrentSettings.CodexQuotaPlanEnabled
                ? "点击关闭额度计划"
                : "点击开启额度计划\r\n阈值和 goal 列表在普通设置中调整"));
        aiQuota.Children.Add(NewSettingToggle("CodexQuotaPlanAutoResumePausedGoals", "common_quota_auto_resume", "恢复上次", RadialAdvancedColor, this.DrawRestartLoopGlyph, s => s.CodexQuotaPlanAutoResumePausedGoals));
        common.Children.Add(aiQuota);

        RadialNode radar = NewBranch("common_radar", "Radar 数据", RadialAssistColor, (g, r) => DrawSparkleGlyph(g, r, 4));
        radar.Children.Add(NewSettingToggle("RadarClockAutoSwitchModelEnabled", "common_radar_auto_model", "自动模型", RadialAssistColor, this.DrawRefreshLoopGlyph, s => s.RadarClockAutoSwitchModelEnabled));
        common.Children.Add(radar);

        RadialNode shortcuts = NewBranch("common_system_shortcuts", "系统快捷", RadialSystemColor, this.DrawWrenchGlyph);
        shortcuts.Children.Add(NewLeaf("common_task_manager", "任务管理器", RadialSystemColor, this.DrawBarsGlyph, () => ExecuteButton(TaskManagerButtonIndex, MouseButtons.Left), () => "打开任务管理器"));
        shortcuts.Children.Add(NewLeaf("common_quick_settings", "快速设置", RadialSystemColor, this.DrawTogglesGlyph, () => ExecuteButton(WindowsQuickSettingsButtonIndex, MouseButtons.Left), () => "打开快速设置\r\n使用快捷键 Win+A"));
        shortcuts.Children.Add(NewLeaf("common_system_tools", "系统工具", RadialSystemColor, (g, r) => DrawTileGridGlyph(g, r, 3), ExecuteRadialSystemToolsMenu, () => "Windows 系统工具菜单\r\n设备管理器、磁盘管理等"));
        shortcuts.Children.Add(NewLeaf("common_power_menu", "电源菜单", RadialPowerColor, this.DrawGaugeGlyph, () => ExecuteButton(WindowsPowerMenuButtonIndex, MouseButtons.Left), () => "打开 SeelenUI 电源界面\r\n不可用时尝试 Windows 安全菜单", isBusy: () => Interlocked.CompareExchange(ref this.seelenPowerMenuRequestRunning, 0, 0) != 0));
        shortcuts.Children.Add(NewLeaf("common_refresh", "刷新", RadialSystemColor, this.DrawRefreshLoopGlyph, () => ExecuteButton(RefreshButtonIndex, MouseButtons.Left), () => "刷新所有模块"));
        shortcuts.Children.Add(NewLeaf("common_restart_app", "重启程序", RadialPowerColor, this.DrawRestartLoopGlyph, ExecuteRestartButtonDoubleClick, () => "重启 SeelenUI 和本程序"));
        shortcuts.Children.Add(NewLeaf("common_pulse_dock", "置顶 Dock", RadialPowerColor, this.DrawDockChevronGlyph, PulseSeelenDockFromOperationPanel, () => "拉到前 Seelen Dock"));
        common.Children.Add(shortcuts);

        RadialNode assist = NewBranch("common_assist", "辅助入口", RadialAssistColor, (g, r) => DrawSparkleGlyph(g, r, 6));
        assist.Children.Add(NewLeaf("common_ai_studio", "AI Studio", RadialAssistColor, (g, r) => DrawSparkleGlyph(g, r, 4), () => ExecuteButton(WindowsAiStudioButtonIndex, MouseButtons.Left), () => this.windowsAiStudioAvailable ? "打开 AI Studio" : "AI Studio 当前不可用\r\n未检测到 ms-clicktodo 协议或 CoreAI 包", isUnavailable: () => !this.windowsAiStudioAvailable));
        assist.Children.Add(NewLeaf("common_live_captions", "实时字幕", RadialAssistColor, this.DrawCaptionBubbleGlyph, () => ExecuteButton(LiveCaptionsButtonIndex, MouseButtons.Left), () => this.liveCaptionsAvailable ? "打开实时字幕" : "实时字幕当前不可用\r\n未检测到系统实时字幕入口", isUnavailable: () => !this.liveCaptionsAvailable));
        if (includeBattery)
        {
            assist.Children.Add(NewLeaf("common_battery_care_pause", "电池暂停", RadialBatteryColor, this.DrawBatteryPauseGlyph, () => ExecuteButton(BatteryCarePauseButtonIndex, MouseButtons.Left), () => "关闭电池保护 24 小时", isBusy: () => this.batteryCarePauseRunning));
            assist.Children.Add(NewLeaf("common_battery_limit_restore", "电池恢复", RadialBatteryColor, this.DrawBatteryCheckGlyph, () => ExecuteButton(BatteryLimitRestoreButtonIndex, MouseButtons.Left), () => "开启电池保护", isBusy: () => this.batteryLimitRestoreRunning));
        }
        common.Children.Add(assist);

        RadialNode networkPower = NewBranch("common_network_power", "网络功耗", RadialSystemColor, this.DrawGaugeGlyph);
        networkPower.Children.Add(NewSettingToggle("GfwProbeEnabled", "common_gfw_probe", "GFW 检测", RadialSystemColor, this.DrawChainLinkGlyph, s => s.GfwProbeEnabled));
        networkPower.Children.Add(NewLeaf("common_network_refresh", "刷新", RadialSystemColor, this.DrawRefreshLoopGlyph, () => ExecuteButton(RefreshButtonIndex, MouseButtons.Left), () => "刷新所有模块"));
        common.Children.Add(networkPower);

        RadialNode maintenance = NewBranch("common_maintenance", "维护调试", RadialAdvancedColor, this.DrawKeyboardGlyph);
        maintenance.Children.Add(NewSettingToggle("AlertTestEnabled", "common_alert_test", "告警测试", RadialAdvancedColor, this.DrawBeakerGlyph, s => s.AlertTestEnabled));
        maintenance.Children.Add(NewSettingToggle("ForceShowForegroundFpsEnabled", "common_fps", "显示 FPS", RadialAdvancedColor, this.DrawGaugeGlyph, s => s.ForceShowForegroundFpsEnabled));
        common.Children.Add(maintenance);

        return common;
    }

    private RadialNode BuildRadialAllSettingsBranch()
    {
        RadialNode all = NewBranch("all_settings", "全部开关", RadialSettingsColor, (g, r) => DrawTileGridGlyph(g, r, 3));

        RadialNode system = NewBranch("all_system", "系统启动", RadialSystemColor, this.DrawWrenchGlyph);
        system.Children.Add(NewSettingToggle("StartupEnabled", "all_startup", "开机启动", RadialSystemColor, this.DrawPowerRingGlyph, s => s.StartupEnabled, true, "此操作会修改当前用户的 Windows 开机启动项。确定继续？"));
        system.Children.Add(NewSettingToggle("VisibilityOverlapIgnoresOperationPanelEnabled", "all_overlap_ignore", "忽略遮挡", RadialSystemColor, this.DrawAppWindowGlyph, s => s.VisibilityOverlapIgnoresOperationPanelEnabled));
        system.Children.Add(NewSettingToggle("FallbackDisconnectedDisplaysEnabled", "all_display_fallback", "显示器回退", RadialSystemColor, this.DrawAppWindowGlyph, s => s.FallbackDisconnectedDisplaysEnabled));
        system.Children.Add(NewSettingToggle("ResolutionCompatibilityModeEnabled", "all_resolution_compat", "分辨率兼容", RadialSystemColor, (g, r) => DrawTileGridGlyph(g, r, 2), s => s.ResolutionCompatibilityModeEnabled));
        system.Children.Add(NewSettingToggle("SeelenDockForegroundPulseEnabled", "all_seelen_pulse", "Dock 拉前", RadialPowerColor, this.DrawDockChevronGlyph, s => s.SeelenDockForegroundPulseEnabled));
        system.Children.Add(NewSettingToggle("WinDRecoveryPulseEnabled", "all_wind_recovery", "Win+D 恢复", RadialPowerColor, this.DrawRestartLoopGlyph, s => s.WinDRecoveryPulseEnabled));
        system.Children.Add(NewSettingToggle("PowerResumeRestartEnabled", "all_resume_restart", "唤醒重启", RadialPowerColor, this.DrawRestartLoopGlyph, s => s.PowerResumeRestartEnabled));
        all.Children.Add(system);

        RadialNode visibility = NewBranch("all_visibility", "隐藏防烧屏", RadialSettingsColor, this.DrawHalfMoonGlyph);
        visibility.Children.Add(NewSettingToggle("HoverOpacityEnabled", "all_hover_hide", "靠近隐藏", RadialSettingsColor, this.DrawHalfMoonGlyph, s => s.HoverOpacityEnabled));
        visibility.Children.Add(NewSettingToggle("SensitiveMouseModeEnabled", "all_sensitive_mouse", "敏感鼠标", RadialSettingsColor, this.DrawHalfMoonGlyph, s => s.SensitiveMouseModeEnabled));
        visibility.Children.Add(NewSettingToggle("HoverOpacityRevealDelayEnabled", "all_reveal_delay", "延迟显现", RadialSettingsColor, this.DrawHalfMoonGlyph, s => s.HoverOpacityRevealDelayEnabled));
        visibility.Children.Add(NewSettingToggle("HoverOpacityCoverEnabled", "all_hover_cover", "覆盖开启", RadialSettingsColor, this.DrawAppWindowGlyph, s => s.HoverOpacityCoverEnabled));
        visibility.Children.Add(NewSettingToggle("ReverseHoverOpacityRevealEnabled", "all_reverse_reveal", "反向隐藏", RadialSettingsColor, this.DrawHalfMoonGlyph, s => s.ReverseHoverOpacityRevealEnabled));
        visibility.Children.Add(NewSettingToggle("BurnInHiddenModeColorProtectionEnabled", "all_burnin", "反色防烧屏", RadialSettingsColor, this.DrawSparkleShieldGlyph, s => s.BurnInHiddenModeColorProtectionEnabled));
        RadialNode autoHide = NewBranch("all_auto_hide", "自动隐藏", RadialSettingsColor, this.DrawHalfMoonGlyph);
        autoHide.Children.Add(NewSettingToggle("AutoHoverOpacityIdleEnabled", "all_idle_hide", "空闲隐藏", RadialSettingsColor, this.DrawHalfMoonGlyph, s => s.AutoHoverOpacityIdleEnabled));
        autoHide.Children.Add(NewSettingToggle("AutoHoverOpacityMaximizedEnabled", "all_max_hide", "最大化隐藏", RadialSettingsColor, this.DrawAppWindowGlyph, s => s.AutoHoverOpacityMaximizedEnabled));
        autoHide.Children.Add(NewSettingToggle("OperationRadialCoreAutoHideKeepAliveEnabled", "all_core_keepalive", "圆圈保持", RadialSettingsColor, this.DrawPowerRingGlyph, s => s.OperationRadialCoreAutoHideKeepAliveEnabled));
        autoHide.Children.Add(NewSettingToggle("OperationRadialKeepOpenAfterLeafClickEnabled", "all_radial_leaf_keepopen", "末端保持", RadialSettingsColor, this.DrawTogglesGlyph, s => s.OperationRadialKeepOpenAfterLeafClickEnabled));
        visibility.Children.Add(autoHide);
        all.Children.Add(visibility);

        RadialNode radar = NewBranch("all_radar_common", "Radar 通用", RadialAssistColor, (g, r) => DrawSparkleGlyph(g, r, 4));
        radar.Children.Add(NewSettingToggle("RadarClockAutoSwitchModelEnabled", "all_radar_auto_model", "自动模型", RadialAssistColor, this.DrawRefreshLoopGlyph, s => s.RadarClockAutoSwitchModelEnabled));
        all.Children.Add(radar);

        all.Children.Add(BuildRadialAllCodexSettingsBranch());
        RadialNode networkPower = NewBranch("all_network_power_test", "网络功耗测试", RadialSystemColor, this.DrawGaugeGlyph);
        networkPower.Children.Add(NewSettingToggle("GfwProbeEnabled", "all_gfw_probe", "GFW 检测", RadialSystemColor, this.DrawChainLinkGlyph, s => s.GfwProbeEnabled));
        networkPower.Children.Add(NewSettingToggle("AlertTestEnabled", "all_alert_test", "告警测试", RadialAdvancedColor, this.DrawBeakerGlyph, s => s.AlertTestEnabled));
        networkPower.Children.Add(NewSettingToggle("ForceShowForegroundFpsEnabled", "all_fps", "显示 FPS", RadialAdvancedColor, this.DrawGaugeGlyph, s => s.ForceShowForegroundFpsEnabled));
        networkPower.Children.Add(NewSettingToggle("OperationSettingsLogicExtensionEnabled", "all_settings_logic_extension", "设置逻辑扩展", RadialSettingsColor, this.DrawTogglesGlyph, s => s.OperationSettingsLogicExtensionEnabled));
        all.Children.Add(networkPower);

        return all;
    }

    private RadialNode BuildRadialAllCodexSettingsBranch()
    {
        RadialNode codex = NewBranch("all_codex", "Codex 设置", RadialAdvancedColor, this.DrawPieWedgeGlyph);

        RadialNode ai = NewBranch("all_codex_ai", "AI 阻断", RadialAdvancedColor, this.DrawChainLinkGlyph);
        ai.Children.Add(NewSettingToggle("AiRequestProtectionAutoEnabled", "all_ai_auto_block", "自动阻断", RadialAdvancedColor, this.DrawChainLinkGlyph, s => s.AiRequestProtectionAutoEnabled));
        ai.Children.Add(NewToggle(
            "all_ai_manual_block",
            "手动阻断",
            RadialAdvancedColor,
            this.DrawChainLinkGlyph,
            () => this.CurrentSettings.AiRequestProtectionManualBlockEnabled,
            ExecuteRadialLinkBlockToggle,
            () => this.CurrentSettings.AiRequestProtectionManualBlockEnabled ? "点击关闭链接阻断" : "点击开启链接阻断"));
        codex.Children.Add(ai);

        RadialNode link = NewBranch("all_codex_link", "Codex 链路", RadialAdvancedColor, this.DrawChainLinkGlyph);
        link.Children.Add(NewSettingToggle("CodexRadarPublicJsonEnabled", "all_codex_public_json", "公开 JSON", RadialAdvancedColor, this.DrawChainLinkGlyph, s => s.CodexRadarPublicJsonEnabled));
        link.Children.Add(NewSettingToggle("CodexRadarHtmlFallbackEnabled", "all_codex_html", "HTML 回退", RadialAdvancedColor, this.DrawAppWindowGlyph, s => s.CodexRadarHtmlFallbackEnabled));
        link.Children.Add(NewSettingToggle("CodexRadarRssFallbackEnabled", "all_codex_rss", "RSS 提醒", RadialAdvancedColor, this.DrawRefreshLoopGlyph, s => s.CodexRadarRssFallbackEnabled));
        codex.Children.Add(link);

        RadialNode quotaPlan = NewBranch("all_codex_quota_plan", "额度计划", RadialAdvancedColor, this.DrawPieWedgeGlyph);
        quotaPlan.Children.Add(NewToggle(
            "all_quota_plan_enabled",
            "启用计划",
            RadialAdvancedColor,
            this.DrawPieWedgeGlyph,
            () => this.CurrentSettings.CodexQuotaPlanEnabled,
            ExecuteRadialQuotaPlanToggle,
            () => this.CurrentSettings.CodexQuotaPlanEnabled ? "点击关闭额度计划" : "点击开启额度计划"));
        quotaPlan.Children.Add(NewSettingToggle("CodexQuotaPlanAutoResumePausedGoals", "all_quota_auto_resume", "恢复上次", RadialAdvancedColor, this.DrawRestartLoopGlyph, s => s.CodexQuotaPlanAutoResumePausedGoals));
        codex.Children.Add(quotaPlan);

        RadialNode protection = NewBranch("all_codex_quota_protection", "额度保护", RadialAdvancedColor, this.DrawBatterySparkGlyph);
        protection.Children.Add(NewSettingToggle("CodexQuotaDueResetProtectionEnabled", "all_quota_due_reset", "到期重置", RadialAdvancedColor, this.DrawRefreshLoopGlyph, s => s.CodexQuotaDueResetProtectionEnabled));
        protection.Children.Add(NewSettingToggle("CodexQuotaRssResetProtectionEnabled", "all_quota_rss_reset", "RSS 重置", RadialAdvancedColor, this.DrawRefreshLoopGlyph, s => s.CodexQuotaRssResetProtectionEnabled));
        protection.Children.Add(NewSettingToggle("CodexQuotaProviderZeroDropProtectionEnabled", "all_quota_zero_drop", "零值保护", RadialAdvancedColor, this.DrawBatterySparkGlyph, s => s.CodexQuotaProviderZeroDropProtectionEnabled));
        protection.Children.Add(NewSettingToggle("CodexQuotaDuplicateSameBalanceRingProtectionEnabled", "all_quota_same_ring", "保留消耗环", RadialAdvancedColor, this.DrawPieWedgeGlyph, s => s.CodexQuotaDuplicateSameBalanceRingProtectionEnabled));
        protection.Children.Add(NewSettingToggle("CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled", "all_quota_five_spike", "5h 提前保护", RadialAdvancedColor, this.DrawGaugeGlyph, s => s.CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled));
        protection.Children.Add(NewSettingToggle("CodexQuotaProviderWeeklySpikeProtectionEnabled", "all_quota_week_spike", "周突增保护", RadialAdvancedColor, this.DrawGaugeGlyph, s => s.CodexQuotaProviderWeeklySpikeProtectionEnabled));
        protection.Children.Add(NewSettingToggle("CodexQuotaStrictFiveHourResetBoundaryEnabled", "all_quota_strict_5h", "严格 5h", RadialAdvancedColor, this.DrawWrenchGlyph, s => s.CodexQuotaStrictFiveHourResetBoundaryEnabled));
        protection.Children.Add(NewSettingToggle("CodexQuotaWeeklyBaselineAutoRepairEnabled", "all_quota_week_repair", "周基线修复", RadialAdvancedColor, this.DrawWrenchGlyph, s => s.CodexQuotaWeeklyBaselineAutoRepairEnabled));
        codex.Children.Add(protection);

        RadialNode tests = NewBranch("all_codex_tests", "健康测试", RadialAdvancedColor, this.DrawBeakerGlyph);
        tests.Children.Add(NewSettingToggle("CodexRadarRandomTestEnabled", "all_codex_random", "随机健康", RadialAdvancedColor, this.DrawSparkleGlyphFour, s => s.CodexRadarRandomTestEnabled));
        tests.Children.Add(NewSettingToggle("CodexRadarRandomTestAutoRefresh", "all_codex_random_auto", "自动轮换", RadialAdvancedColor, this.DrawRefreshLoopGlyph, s => s.CodexRadarRandomTestAutoRefresh));
        codex.Children.Add(tests);

        return codex;
    }

    private RadialNode NewSettingToggle(
        string propertyName,
        string id,
        string label,
        Color color,
        Action<Graphics, RectangleF> drawIcon,
        Func<WidgetSettings, bool> getState,
        bool requiresConfirmation = false,
        string confirmationText = null)
    {
        RadialSettingToggleDescriptor descriptor = new RadialSettingToggleDescriptor
        {
            PropertyName = propertyName,
            Id = id,
            Label = label,
            Color = color,
            DrawIcon = drawIcon,
            GetState = getState,
            RequiresConfirmation = requiresConfirmation,
            ConfirmationText = confirmationText
        };
        return NewToggle(
            descriptor.Id,
            descriptor.Label,
            descriptor.Color,
            descriptor.DrawIcon,
            () => GetRadialSettingState(descriptor),
            () => ExecuteRadialSettingToggle(descriptor),
            () => GetRadialSettingToggleTooltip(descriptor));
    }

    private bool GetRadialSettingState(RadialSettingToggleDescriptor descriptor)
    {
        return this.CurrentSettings != null &&
            descriptor.GetState != null &&
            descriptor.GetState(this.CurrentSettings);
    }

    private string GetRadialSettingToggleTooltip(RadialSettingToggleDescriptor descriptor)
    {
        bool enabled = GetRadialSettingState(descriptor);
        return (enabled ? "点击关闭" : "点击开启") + descriptor.Label + "\r\n" + descriptor.PropertyName;
    }

    private void ExecuteRadialSettingToggle(RadialSettingToggleDescriptor descriptor)
    {
        bool next = !GetRadialSettingState(descriptor);
        if (descriptor.RequiresConfirmation)
        {
            DialogResult result = MessageBox.Show(
                this,
                descriptor.ConfirmationText ?? ("确定要切换“" + descriptor.Label + "”？"),
                descriptor.Label,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result != DialogResult.Yes)
            {
                return;
            }
        }

        if (this.setBooleanSettingAction == null)
        {
            ShowOperationNotification("设置切换", "当前宿主不支持此设置。", ToolTipIcon.Warning);
            return;
        }

        try
        {
            if (!this.setBooleanSettingAction(descriptor.PropertyName, next))
            {
                ShowOperationNotification("设置切换", descriptor.Label + " 切换失败。", ToolTipIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            ShowOperationNotification("设置切换", descriptor.Label + " 切换失败。", ToolTipIcon.Warning);
        }
    }

    private void DrawSparkleGlyphFour(Graphics g, RectangleF rect)
    {
        DrawSparkleGlyph(g, rect, 4);
    }

    private void DrawSparkleGlyphSix(Graphics g, RectangleF rect)
    {
        DrawSparkleGlyph(g, rect, 6);
    }

    private void DrawSparkleShieldGlyph(Graphics g, RectangleF rect)
    {
        DrawSparkleGlyph(g, rect, 5);
    }

    private bool IsRadialCtfRestartBusy()
    {
        return Interlocked.CompareExchange(ref this.radialCtfRestartRunning, 0, 0) != 0;
    }

    // Re-resolves the stored id chain against the live tree every time (rather than holding node
    // references) so a tree rebuild (e.g. battery visibility flips) or a since-removed node can never
    // leave a dangling reference; a stale id silently truncates the path instead of throwing.
    private List<RadialNode> ResolveSelectionPath()
    {
        List<RadialNode> resolved = new List<RadialNode>();
        List<RadialNode> siblings = GetRadialRoots();
        foreach (string id in this.radialSelectionPathIds)
        {
            RadialNode found = siblings.Find(n => n.Id == id);
            if (found == null)
            {
                break;
            }

            resolved.Add(found);
            siblings = found.Children;
        }

        if (resolved.Count != this.radialSelectionPathIds.Count)
        {
            List<string> trimmed = new List<string>();
            for (int i = 0; i < resolved.Count; i++)
            {
                trimmed.Add(resolved[i].Id);
            }

            this.radialSelectionPathIds = trimmed;
        }

        return resolved;
    }

    // ---------------------------------------------------------------------------------------------
    // Geometry -- one ring per currently-open level (root always, plus one more per drilled node),
    // rendered simultaneously so drilling deeper never hides the rings above it.
    // ---------------------------------------------------------------------------------------------

    private RadialLayout ComputeRadialLayout()
    {
        int margin = S(3);
        int coreSize = GetStartButtonSize();
        RadialLayout layout = new RadialLayout();

        if (!this.radialMenuOpen)
        {
            layout.WindowSize = new Size(margin * 2 + coreSize, margin * 2 + coreSize);
            layout.Core = new RectangleF(margin, margin, coreSize, coreSize);
            return layout;
        }

        List<RadialNode> resolvedPath = ResolveSelectionPath();
        List<List<RadialNode>> levelNodeLists = new List<List<RadialNode>>();
        levelNodeLists.Add(GetRadialRoots());
        for (int i = 0; i < resolvedPath.Count; i++)
        {
            levelNodeLists.Add(resolvedPath[i].Children);
        }

        int itemSize = GetSmallButtonSize();
        float coreRadius = coreSize / 2.0f;
        float previousRadius = coreRadius;
        float maxRight = coreRadius;
        float maxUp = coreRadius;
        List<PointF[]> levelOffsets = new List<PointF[]>();

        for (int levelIdx = 0; levelIdx < levelNodeLists.Count; levelIdx++)
        {
            int count = levelNodeLists[levelIdx].Count;
            float baseRadius = levelIdx == 0
                ? coreRadius + coreSize * RadialGapScale + itemSize / 2.0f
                : previousRadius + itemSize * (RadialGapScale + 1.0f) * RadialLevelSpacingMultiplier;
            float arcStartDeg;
            float arcEndDeg;
            GetRadialArcForLevel(count, out arcStartDeg, out arcEndDeg);
            float radius = ComputeRingRadius(baseRadius, count, itemSize, arcStartDeg, arcEndDeg);
            previousRadius = radius;
            PointF[] offsets = ComputeArcOffsets(count, radius, arcStartDeg, arcEndDeg);
            levelOffsets.Add(offsets);
            AccumulateExtent(offsets, itemSize, ref maxRight, ref maxUp);
        }

        float coreCenterX = margin + coreRadius;
        int windowWidth = (int)Math.Ceiling(coreCenterX + maxRight + margin);
        int windowHeight = (int)Math.Ceiling(margin + coreRadius + maxUp + margin);
        float coreCenterY = windowHeight - margin - coreRadius;

        layout.WindowSize = new Size(windowWidth, windowHeight);
        layout.Core = new RectangleF(coreCenterX - coreRadius, coreCenterY - coreRadius, coreSize, coreSize);

        for (int levelIdx = 0; levelIdx < levelNodeLists.Count; levelIdx++)
        {
            RadialLevelLayout ll = new RadialLevelLayout();
            ll.Nodes = levelNodeLists[levelIdx];
            ll.Rects = BuildRectsFromOffsets(levelOffsets[levelIdx], coreCenterX, coreCenterY, itemSize);
            layout.Levels.Add(ll);
        }

        return layout;
    }

    // Grows the ring radius beyond the resting gap-from-previous-ring radius whenever the arc is
    // dense enough that evenly-spaced items would otherwise overlap.
    private static float ComputeRingRadius(float baseRadius, int itemCount, float itemSize, float arcStartDeg, float arcEndDeg)
    {
        if (itemCount <= 1)
        {
            return baseRadius;
        }

        float archSpanDeg = arcEndDeg - arcStartDeg;
        double gapRad = (archSpanDeg / (itemCount - 1)) * Math.PI / 180.0;
        double sinHalfGap = Math.Sin(gapRad / 2.0);
        if (sinHalfGap < 0.001)
        {
            return baseRadius;
        }

        float required = (float)((itemSize * RadialMinItemSpacingScale) / (2.0 * sinHalfGap));
        return Math.Max(baseRadius, required);
    }

    private static void GetRadialArcForLevel(int itemCount, out float startDeg, out float endDeg)
    {
        if (itemCount >= RadialDenseArcMinItems)
        {
            startDeg = RadialDenseArcStartDeg;
            endDeg = RadialDenseArcEndDeg;
            return;
        }

        if (itemCount >= RadialMediumArcMinItems)
        {
            startDeg = RadialMediumArcStartDeg;
            endDeg = RadialMediumArcEndDeg;
            return;
        }

        startDeg = RadialSparseArcStartDeg;
        endDeg = RadialSparseArcEndDeg;
    }

    // Offsets are in "quadrant space": origin at the core center, +X right, +Y UP (not screen Y-down
    // yet -- BuildRectsFromOffsets flips the sign when placing items).
    private static PointF[] ComputeArcOffsets(int count, float radius, float arcStartDeg, float arcEndDeg)
    {
        PointF[] result = new PointF[count];
        if (count <= 0)
        {
            return result;
        }

        if (count == 1)
        {
            double midRad = ((arcStartDeg + arcEndDeg) / 2.0) * Math.PI / 180.0;
            result[0] = new PointF((float)(Math.Cos(midRad) * radius), (float)(Math.Sin(midRad) * radius));
            return result;
        }

        for (int i = 0; i < count; i++)
        {
            double t = (double)i / (count - 1);
            double angleDeg = arcStartDeg + t * (arcEndDeg - arcStartDeg);
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

    private RadialHitResult RadialHitTest(Point point)
    {
        RadialLayout layout = ComputeRadialLayout();
        if (IsPointInRadialCore(layout, point))
        {
            return new RadialHitResult { Kind = RadialHitKind.Core, Level = -1, Index = -1 };
        }

        for (int levelIdx = 0; levelIdx < layout.Levels.Count; levelIdx++)
        {
            RadialLevelLayout ll = layout.Levels[levelIdx];
            for (int i = 0; i < ll.Rects.Length; i++)
            {
                if (ll.Rects[i].Contains(point.X, point.Y))
                {
                    return new RadialHitResult { Kind = RadialHitKind.Item, Level = levelIdx, Index = i, Node = ll.Nodes[i] };
                }
            }
        }

        return new RadialHitResult { Kind = RadialHitKind.None, Level = -1, Index = -1 };
    }

    internal bool IsRadialCoreAutoHideKeepAliveActive()
    {
        if (!IsRadialDialActive() ||
            this.formClosing ||
            this.hiddenForFullscreen ||
            this.displaySuspended ||
            !this.Visible ||
            this.IsDisposed ||
            !this.IsHandleCreated)
        {
            return false;
        }

        return IsPointInRadialCore(ComputeRadialLayout(), PointToClient(Cursor.Position));
    }

    private static bool IsPointInRadialCore(RadialLayout layout, Point point)
    {
        return layout != null && layout.Core.Contains(point.X, point.Y);
    }

    internal void ClearRadialCoreAutoHideThresholdVisual()
    {
        this.radialCoreHoverStartedUtc = DateTime.MinValue;
        this.radialCoreAutoHideThresholdVisualActive = false;
    }

    private void HandleRadialMouseMove(MouseEventArgs e)
    {
        ResetRadialIdleCollapseTimerForInteraction();

        RadialHitResult hit = RadialHitTest(e.Location);
        bool coreHover = hit.Kind == RadialHitKind.Core;
        int hoverLevel = hit.Kind == RadialHitKind.Item ? hit.Level : -1;
        int hoverIndex = hit.Kind == RadialHitKind.Item ? hit.Index : -1;
        if (coreHover == this.radialCoreHovered &&
            hoverLevel == this.radialHoveredLevel &&
            hoverIndex == this.radialHoveredIndex)
        {
            return;
        }

        this.radialCoreHovered = coreHover;
        if (!coreHover)
        {
            ClearRadialCoreAutoHideThresholdVisual();
        }

        this.radialHoveredLevel = hoverLevel;
        this.radialHoveredIndex = hoverIndex;
        UpdateRadialHoverToolTip(hit, e.Location);
        RenderLayeredWindow();
    }

    private void HandleRadialMouseLeave()
    {
        if (!this.radialCoreHovered && this.radialHoveredIndex < 0)
        {
            return;
        }

        this.radialCoreHovered = false;
        this.radialHoveredLevel = -1;
        this.radialHoveredIndex = -1;
        ClearRadialCoreAutoHideThresholdVisual();
        HideHoverToolTip();
        RenderLayeredWindow();
    }

    private void HandleRadialMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left && e.Button != MouseButtons.Right)
        {
            return;
        }

        if (this.radialMenuOpen)
        {
            ResetRadialIdleCollapseTimerForInteraction();
        }

        HideHoverToolTip();
        RadialHitResult hit = RadialHitTest(e.Location);
        this.radialCorePressed = hit.Kind == RadialHitKind.Core;
        this.radialPressedLevel = e.Button == MouseButtons.Left && hit.Kind == RadialHitKind.Item ? hit.Level : -1;
        this.radialPressedIndex = e.Button == MouseButtons.Left && hit.Kind == RadialHitKind.Item ? hit.Index : -1;
        RenderLayeredWindow();
    }

    private void HandleRadialMouseUp(MouseEventArgs e)
    {
        RadialHitResult hit = RadialHitTest(e.Location);
        bool corePressed = this.radialCorePressed;
        int pressedLevel = this.radialPressedLevel;
        int pressedIndex = this.radialPressedIndex;
        this.radialCorePressed = false;
        this.radialPressedLevel = -1;
        this.radialPressedIndex = -1;
        RenderLayeredWindow();

        if (e.Button == MouseButtons.Right)
        {
            if (hit.Kind == RadialHitKind.Core && corePressed)
            {
                OpenWindowsSystemToolsMenu();
                ResetRadialIdleCollapseTimerForInteraction();
            }

            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (hit.Kind == RadialHitKind.Core && corePressed)
        {
            HandleSpecBoardEntryMouseUp(SpecBoardEntryClickTarget.RadialCore);
            return;
        }

        if (hit.Kind != RadialHitKind.Item || hit.Level != pressedLevel || hit.Index != pressedIndex)
        {
            return;
        }

        RadialNode node = hit.Node;
        if (node.IsBranch)
        {
            List<RadialNode> resolvedPath = ResolveSelectionPath();
            bool alreadyActive = hit.Level < resolvedPath.Count && resolvedPath[hit.Level].Id == node.Id;
            int keepCount = Math.Min(hit.Level, this.radialSelectionPathIds.Count);
            List<string> newPathIds = this.radialSelectionPathIds.GetRange(0, keepCount);
            if (!alreadyActive)
            {
                newPathIds.Add(node.Id);
            }

            this.radialSelectionPathIds = newPathIds;
            ResetRadialIdleCollapseTimerForInteraction();
            ClearRadialTransientState();
            ApplyRadialSizeAndPosition();
            return;
        }

        if (node.IsUnavailable != null && node.IsUnavailable())
        {
            return;
        }

        if (node.IsBusy != null && node.IsBusy())
        {
            return;
        }

        if (node.Execute != null)
        {
            node.Execute();
        }

        if (ShouldKeepRadialMenuOpenAfterLeafClick())
        {
            ResetRadialIdleCollapseTimerForInteraction();
            ClearRadialTransientState();
            RenderLayeredWindow();
            return;
        }

        CloseRadialMenu();
    }

    private void ExecuteRadialCoreSingleClick()
    {
        if (this.radialMenuOpen)
        {
            CloseRadialMenu();
        }
        else
        {
            OpenRadialMenu();
        }
    }

    private bool ShouldKeepRadialMenuOpenAfterLeafClick()
    {
        return this.CurrentSettings == null ||
            this.CurrentSettings.OperationRadialKeepOpenAfterLeafClickEnabled;
    }

    private void OpenRadialMenu()
    {
        PrepareForRadialOverlayShow();
        this.radialMenuOpen = true;
        this.radialSelectionPathIds = new List<string>();
        this.radialLastInteractionUtc = DateTime.UtcNow;
        ClearRadialTransientState();
        ApplyRadialSizeAndPosition();
    }

    private void CloseRadialMenu()
    {
        this.radialMenuOpen = false;
        this.radialSelectionPathIds = new List<string>();
        ClearRadialTransientState();
        ApplyRadialSizeAndPosition();
    }

    // Mirrors the resize+reposition+redraw the periodic tick already does (OperationForm.cs's timer
    // handler), fired immediately on every navigation change instead of waiting up to one tick.
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

    // Called from OperationForm.ProcessSharedInteractionTick (the shared per-tick hook other windows
    // also use) since, unlike before, navigating no longer hides anything on its own -- the menu now
    // only ever retracts by an explicit core click or by sitting idle for a few seconds.
    private bool TickRadialIdleCollapse()
    {
        if (!ShouldRadialIdleCollapse(DateTime.UtcNow))
        {
            return false;
        }

        CloseRadialMenu();
        return true;
    }

    private bool UpdateRadialCoreAutoHideThresholdVisual(DateTime nowUtc)
    {
        bool coreKeepAliveActive =
            this.CurrentSettings != null &&
            this.CurrentSettings.OperationRadialCoreAutoHideKeepAliveEnabled &&
            IsRadialCoreAutoHideKeepAliveActive();

        return UpdateRadialCoreAutoHideThresholdVisual(nowUtc, coreKeepAliveActive);
    }

    private bool UpdateRadialCoreAutoHideThresholdVisual(DateTime nowUtc, bool coreKeepAliveActive)
    {
        if (!coreKeepAliveActive)
        {
            bool wasActive = this.radialCoreAutoHideThresholdVisualActive;
            ClearRadialCoreAutoHideThresholdVisual();
            return wasActive;
        }

        if (this.radialCoreHoverStartedUtc == DateTime.MinValue)
        {
            this.radialCoreHoverStartedUtc = nowUtc;
        }

        bool shouldBeActive =
            (nowUtc - this.radialCoreHoverStartedUtc).TotalSeconds >= GetRadialCoreAutoHideThresholdSeconds();
        if (shouldBeActive == this.radialCoreAutoHideThresholdVisualActive)
        {
            return false;
        }

        this.radialCoreAutoHideThresholdVisualActive = shouldBeActive;
        return true;
    }

    private int GetRadialCoreAutoHideThresholdSeconds()
    {
        int seconds = this.CurrentSettings == null
            ? WidgetSettings.DefaultAutoHoverOpacityIdleSeconds
            : this.CurrentSettings.AutoHoverOpacityIdleSeconds;
        if (seconds < WidgetSettings.MinAutoHoverOpacityIdleSeconds)
        {
            return WidgetSettings.MinAutoHoverOpacityIdleSeconds;
        }

        if (seconds > WidgetSettings.MaxAutoHoverOpacityIdleSeconds)
        {
            return WidgetSettings.MaxAutoHoverOpacityIdleSeconds;
        }

        return seconds;
    }

    private bool ShouldRadialIdleCollapse(DateTime nowUtc)
    {
        int collapseSeconds = GetRadialIdleCollapseSeconds();
        return this.radialMenuOpen &&
            collapseSeconds > WidgetSettings.NeverOperationRadialIdleCollapseSeconds &&
            (nowUtc - this.radialLastInteractionUtc).TotalSeconds >= collapseSeconds;
    }

    private int GetRadialIdleCollapseSeconds()
    {
        if (this.CurrentSettings == null)
        {
            return WidgetSettings.DefaultOperationRadialIdleCollapseSeconds;
        }

        int seconds = this.CurrentSettings.OperationRadialIdleCollapseSeconds;
        if (seconds <= WidgetSettings.NeverOperationRadialIdleCollapseSeconds)
        {
            return WidgetSettings.NeverOperationRadialIdleCollapseSeconds;
        }

        if (seconds < WidgetSettings.MinOperationRadialIdleCollapseSeconds)
        {
            return WidgetSettings.MinOperationRadialIdleCollapseSeconds;
        }

        if (seconds > WidgetSettings.MaxOperationRadialIdleCollapseSeconds)
        {
            return WidgetSettings.MaxOperationRadialIdleCollapseSeconds;
        }

        return seconds;
    }

    private void ResetRadialIdleCollapseTimerForInteraction()
    {
        if (this.radialMenuOpen &&
            this.CurrentSettings != null &&
            this.CurrentSettings.OperationRadialIdleResetOnInteractionEnabled)
        {
            this.radialLastInteractionUtc = DateTime.UtcNow;
        }
    }

    private void UpdateRadialHoverToolTip(RadialHitResult hit, Point location)
    {
        string text = GetRadialTooltipText(hit);
        if (string.IsNullOrEmpty(text))
        {
            HideHoverToolTip();
            return;
        }

        this.toolTipButton = 0;
        this.hoverToolTip.Hide(this);
        this.hoverToolTip.Show(text, this, new Point(location.X + S(12), location.Y + S(18)), 5000);
    }

    private string GetRadialTooltipText(RadialHitResult hit)
    {
        if (hit.Kind == RadialHitKind.Core)
        {
            string primary = this.radialMenuOpen ? "收起操作面板" : "展开操作面板";
            string doubleClickAction = this.CurrentSettings.OperationDoubleClickSpecialMenuEnabled
                ? "双击：特殊菜单"
                : "双击：开关隐藏模式";
            return primary + "\r\n" + doubleClickAction;
        }

        if (hit.Kind != RadialHitKind.Item || hit.Node == null)
        {
            return string.Empty;
        }

        if (hit.Node.GetTooltip != null)
        {
            return hit.Node.GetTooltip();
        }

        return hit.Node.Label;
    }

    // ---------------------------------------------------------------------------------------------
    // Action dispatch for the handful of leaves that need more than a plain ExecuteButton call
    // ---------------------------------------------------------------------------------------------

    private void ExecuteRadialSystemToolsMenu()
    {
        OpenWindowsSystemToolsMenu();
    }

    private void ExecuteRadialLinkBlockToggle()
    {
        if (this.setAiBlockAction == null)
        {
            return;
        }

        try
        {
            this.setAiBlockAction(!this.CurrentSettings.AiRequestProtectionManualBlockEnabled);
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
            this.setQuotaPlanAction(!this.CurrentSettings.CodexQuotaPlanEnabled);
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
        if (!this.radialMenuOpen)
        {
            DrawRadialCore(g, layout.Core, new List<RadialNode>());
            return;
        }

        List<RadialNode> resolvedPath = ResolveSelectionPath();

        for (int levelIdx = 0; levelIdx < layout.Levels.Count; levelIdx++)
        {
            DrawRadialSameLevelRail(g, layout.Levels[levelIdx].Rects);
        }

        DrawRadialPathConnectors(g, layout, resolvedPath);

        for (int levelIdx = 0; levelIdx < layout.Levels.Count; levelIdx++)
        {
            RadialLevelLayout ll = layout.Levels[levelIdx];
            for (int i = 0; i < ll.Rects.Length; i++)
            {
                bool selected = levelIdx < resolvedPath.Count && resolvedPath[levelIdx].Id == ll.Nodes[i].Id;
                bool hovered = levelIdx == this.radialHoveredLevel && i == this.radialHoveredIndex;
                bool pressed = levelIdx == this.radialPressedLevel && i == this.radialPressedIndex;
                DrawRadialNode(g, ll.Rects[i], ll.Nodes[i], selected, hovered, pressed);
            }
        }

        DrawRadialCore(g, layout.Core, resolvedPath);
    }

    // Gray "rail" connecting every sibling within one level, in the same angular order they're
    // drawn -- a visual backbone so a ring of buttons reads as one group.
    private void DrawRadialSameLevelRail(Graphics g, RectangleF[] rects)
    {
        if (rects.Length < 2)
        {
            return;
        }

        PointF[] centers = new PointF[rects.Length];
        for (int i = 0; i < rects.Length; i++)
        {
            centers[i] = new PointF(rects[i].X + rects[i].Width / 2.0f, rects[i].Y + rects[i].Height / 2.0f);
        }

        using (Pen pen = new Pen(DesignTokens.WithAlpha(Color.White, ScaleAlpha(46, GetBackgroundOpacityAlpha())), Math.Max(1.0f, 1.2f * this.LayerScale)))
        {
            g.DrawLines(pen, centers);
        }
    }

    // Light-green breadcrumb: core -> selected L1 -> selected L2 -> ... tracing the active drill path
    // across however many rings are currently open.
    private void DrawRadialPathConnectors(Graphics g, RadialLayout layout, List<RadialNode> resolvedPath)
    {
        if (resolvedPath.Count == 0)
        {
            return;
        }

        PointF previous = new PointF(layout.Core.X + layout.Core.Width / 2.0f, layout.Core.Y + layout.Core.Height / 2.0f);
        Color lineColor = DesignTokens.WithAlpha(Color.FromArgb(150, 235, 180), ScaleAlpha(190, GetBackgroundOpacityAlpha()));
        using (Pen pen = new Pen(lineColor, Math.Max(1.3f, 1.8f * this.LayerScale)))
        {
            for (int levelIdx = 0; levelIdx < resolvedPath.Count; levelIdx++)
            {
                RadialLevelLayout ll = layout.Levels[levelIdx];
                int index = ll.Nodes.FindIndex(n => n.Id == resolvedPath[levelIdx].Id);
                if (index < 0)
                {
                    break;
                }

                RectangleF r = ll.Rects[index];
                PointF center = new PointF(r.X + r.Width / 2.0f, r.Y + r.Height / 2.0f);
                g.DrawLine(pen, previous, center);
                previous = center;
            }
        }
    }

    // Fill precedence: unavailable > busy > toggle-state (green/red) > selected-on-path (blue) >
    // plain category tint.
    private void DrawRadialNode(Graphics g, RectangleF rect, RadialNode node, bool selected, bool hovered, bool pressed)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        bool unavailable = node.IsUnavailable != null && node.IsUnavailable();
        bool busy = node.IsBusy != null && node.IsBusy();
        bool isToggle = node.GetToggleState != null;
        bool toggleOn = isToggle && node.GetToggleState();

        int backgroundAlpha = GetBackgroundOpacityAlpha();
        double hover = hovered ? 1.0 : 0.0;
        double press = pressed ? 1.0 : 0.0;
        int fillAlpha = ScaleAlpha(ClampByte((int)Math.Round(60 + hover * 52 + press * 34)), backgroundAlpha);
        int outlineAlpha = ScaleAlpha(ClampByte((int)Math.Round(46 + hover * 68 + press * 38)), backgroundAlpha);

        Color fill;
        Color border;
        bool blueSelected = false;
        if (unavailable)
        {
            fill = DesignTokens.WithAlpha(DesignTokens.Colors.Control, ScaleAlpha(34, backgroundAlpha));
            border = DesignTokens.White(outlineAlpha);
        }
        else if (busy)
        {
            fill = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, ScaleAlpha(ClampByte((int)Math.Round(46 + hover * 40)), backgroundAlpha));
            border = DesignTokens.White(outlineAlpha);
        }
        else if (isToggle)
        {
            Color toggleColor = toggleOn ? DesignTokens.Colors.Success : DesignTokens.Colors.Danger;
            fill = DesignTokens.WithAlpha(MutedCategoryTint(toggleColor), fillAlpha);
            border = DesignTokens.WithAlpha(toggleColor, ScaleAlpha(190, backgroundAlpha));
        }
        else if (selected)
        {
            fill = DesignTokens.WithAlpha(MutedCategoryTint(RadialSelectedColor), ScaleAlpha(ClampByte((int)Math.Round(110 + hover * 40 + press * 24)), backgroundAlpha));
            border = DesignTokens.WithAlpha(RadialSelectedColor, ScaleAlpha(220, backgroundAlpha));
            blueSelected = true;
        }
        else
        {
            fill = DesignTokens.WithAlpha(MutedCategoryTint(node.BaseColor), fillAlpha);
            border = DesignTokens.White(outlineAlpha);
        }

        using (SolidBrush brush = new SolidBrush(fill))
        {
            g.FillEllipse(brush, rect);
        }

        using (Pen pen = new Pen(border, Math.Max(1.0f, (blueSelected ? 1.6f : 1.0f) * this.LayerScale)))
        {
            g.DrawEllipse(pen, rect);
        }

        if (node.IsBranch)
        {
            Color ringColor = blueSelected ? RadialSelectedColor : node.BaseColor;
            using (Pen expandPen = new Pen(DesignTokens.WithAlpha(ringColor, ScaleAlpha(150, backgroundAlpha)), Math.Max(0.9f, 1.1f * this.LayerScale)))
            {
                g.DrawEllipse(expandPen, RectangleF.Inflate(rect, Math.Max(1.5f, rect.Width * 0.09f), Math.Max(1.5f, rect.Height * 0.09f)));
            }
        }

        node.DrawIcon(g, GetIconRect(rect));

        if (unavailable)
        {
            using (SolidBrush veil = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, ScaleAlpha(116, backgroundAlpha))))
            {
                g.FillEllipse(veil, rect);
            }
        }
    }

    private void DrawRadialCore(Graphics g, RectangleF rect, List<RadialNode> resolvedPath)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        int backgroundAlpha = GetBackgroundOpacityAlpha();
        double hover = this.radialCoreHovered ? 1.0 : 0.0;
        double press = this.radialCorePressed ? 1.0 : 0.0;
        Color? tint = resolvedPath.Count > 0 ? (Color?)resolvedPath[0].BaseColor : null;

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

        DrawRadialCoreGlyph(g, GetIconRect(ring), resolvedPath);
        if (this.radialMenuOpen && resolvedPath.Count > 0)
        {
            DrawRadialCoreDepthDots(g, ring, resolvedPath.Count + 1);
        }

        if (this.radialCoreAutoHideThresholdVisualActive)
        {
            DrawRadialCoreAutoHideThresholdVisual(g, outer);
        }
    }

    private void DrawRadialCoreAutoHideThresholdVisual(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        CompositingMode previousMode = g.CompositingMode;
        try
        {
            // SourceCopy changes the final layered-window pixel alpha. SourceOver would only tint
            // the already drawn core and leave it opaque against the desktop.
            g.CompositingMode = CompositingMode.SourceCopy;
            using (SolidBrush dimBrush = new SolidBrush(Color.FromArgb(RadialCoreAutoHideThresholdDimAlpha, 0, 0, 0)))
            {
                g.FillEllipse(dimBrush, rect);
            }

            float borderWidth = Math.Max(1.0f, 3.0f * this.LayerScale);
            RectangleF borderRect = RectangleF.Inflate(rect, -borderWidth / 2.0f, -borderWidth / 2.0f);
            using (Pen borderPen = new Pen(Color.FromArgb(RadialCoreAutoHideThresholdRingAlpha, 90, 235, 140), borderWidth))
            {
                g.DrawEllipse(borderPen, borderRect);
            }
        }
        finally
        {
            g.CompositingMode = previousMode;
        }
    }

    // Collapsed: four-point launcher spark. Open with nothing drilled: back/close chevron. Open with
    // a category active: that category's own icon, so the core always shows "where the outermost
    // selection currently is" per the requested core redesign.
    private void DrawRadialCoreGlyph(Graphics g, RectangleF rect, List<RadialNode> resolvedPath)
    {
        if (!this.radialMenuOpen)
        {
            DrawRadialSparkGlyph(g, rect);
            return;
        }

        if (resolvedPath.Count == 0)
        {
            DrawRadialBackChevronGlyph(g, rect);
            return;
        }

        resolvedPath[0].DrawIcon(g, rect);
    }

    private void DrawRadialSparkGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) / 2.0f;
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

    private void DrawRadialBackChevronGlyph(Graphics g, RectangleF rect)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float r = Math.Min(rect.Width, rect.Height) / 2.0f;
        using (Pen pen = NewGlyphPen(Math.Max(1.5f, 2.0f * this.LayerScale)))
        {
            g.DrawLines(pen, new PointF[]
            {
                new PointF(cx + r * 0.42f, cy - r * 0.62f),
                new PointF(cx - r * 0.46f, cy),
                new PointF(cx + r * 0.42f, cy + r * 0.62f)
            });
        }
    }

    // Small progress dots along the core's bottom edge, one per currently-open ring, so "how deep am
    // I" is legible at a glance without needing to fit text inside a small circle.
    private void DrawRadialCoreDepthDots(Graphics g, RectangleF ringRect, int visibleLevels)
    {
        float cx = ringRect.Left + ringRect.Width / 2.0f;
        float cy = ringRect.Top + ringRect.Height / 2.0f;
        float r = Math.Min(ringRect.Width, ringRect.Height) / 2.0f;
        float dotR = Math.Max(1.3f, r * 0.11f);
        double spanDeg = 64.0;
        double startDeg = 90.0 - spanDeg / 2.0;
        using (SolidBrush dotBrush = new SolidBrush(DesignTokens.Glyph(250)))
        {
            for (int i = 0; i < visibleLevels; i++)
            {
                double t = visibleLevels == 1 ? 0.5 : (double)i / (visibleLevels - 1);
                double angleDeg = startDeg + t * spanDeg;
                double angleRad = angleDeg * Math.PI / 180.0;
                float dx = cx + (float)Math.Cos(angleRad) * r * 0.82f;
                float dy = cy + (float)Math.Sin(angleRad) * r * 0.82f;
                g.FillEllipse(dotBrush, dx - dotR, dy - dotR, dotR * 2.0f, dotR * 2.0f);
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

    // Blends a category hue into a warm-neutral base at restrained saturation, matching the
    // project's "no peak-white/saturated fills" convention even though Classic isn't OLED-only.
    private static Color MutedCategoryTint(Color categoryColor)
    {
        int r = (int)Math.Round(categoryColor.R * 0.40 + 233.0 * 0.60);
        int g = (int)Math.Round(categoryColor.G * 0.40 + 228.0 * 0.60);
        int b = (int)Math.Round(categoryColor.B * 0.40 + 220.0 * 0.60);
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
        bool blocked = this.CurrentSettings.AiRequestProtectionManualBlockEnabled;
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
        bool enabled = this.CurrentSettings.CodexQuotaPlanEnabled;
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
        for (int levelIdx = 0; levelIdx < layout.Levels.Count; levelIdx++)
        {
            RectangleF[] rects = layout.Levels[levelIdx].Rects;
            for (int i = 0; i < rects.Length; i++)
            {
                graphics.FillEllipse(brush, rects[i]);
            }
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
            List<RadialNode> roots = form.GetRadialRoots();
            AssertSelfTest(roots.Count >= 1 && roots.Count <= 3, "radial root category count is within the level-1 cap of 3");
            AssertRadialSiblingCaps(roots, 1, "root");

            form.radialMenuOpen = true;
            form.radialSelectionPathIds = new List<string>();
            RadialLayout rootLayout = form.ComputeRadialLayout();
            AssertSelfTest(rootLayout.Levels.Count == 1, "only the root ring is visible before any category is selected");
            Point coreCenter = new Point(
                (int)Math.Round(rootLayout.Core.Left + rootLayout.Core.Width / 2.0f),
                (int)Math.Round(rootLayout.Core.Top + rootLayout.Core.Height / 2.0f));
            Point outsideCore = new Point((int)Math.Ceiling(rootLayout.Core.Right + 8.0f), coreCenter.Y);
            AssertSelfTest(IsPointInRadialCore(rootLayout, coreCenter), "radial core keep-alive hit test should include the core center");
            AssertSelfTest(!IsPointInRadialCore(rootLayout, outsideCore), "radial core keep-alive hit test should reject points outside the core");
            AssertRadialQuadrant(rootLayout);

            form.radialSelectionPathIds = new List<string> { "settings" };
            RadialLayout settingsLayout = form.ComputeRadialLayout();
            AssertSelfTest(settingsLayout.Levels.Count == 2, "the root ring stays visible alongside the settings children ring (no auto-hide on drill-in)");
            AssertSelfTest(settingsLayout.Levels[1].Nodes.Count == 3, "settings has three children");
            AssertSelfTest(settingsLayout.Levels[1].Nodes.Exists(n => n.Id == "special_settings"), "settings children include the special-settings branch");
            AssertRadialQuadrant(settingsLayout);

            form.CurrentSettings.OperationSettingsLogicExtensionEnabled = true;
            form.cachedRadialRoots = null;
            roots = form.GetRadialRoots();
            AssertRadialSiblingCaps(roots, 1, "extended root");
            RadialNode settingsRoot = FindRadialNode(roots, "settings");
            AssertSelfTest(settingsRoot != null, "extended roots include settings");
            AssertSelfTest(settingsRoot.Children.Count == 5, "settings has five children when settings logic extension is enabled");
            AssertSelfTest(settingsRoot.Children[0].Id == "normal_settings", "extended settings keeps normal settings first");
            AssertSelfTest(settingsRoot.Children[1].Id == "special_settings", "extended settings keeps special settings second");
            AssertSelfTest(settingsRoot.Children[2].Id == "windows_settings", "extended settings keeps Windows settings third");
            AssertSelfTest(FindRadialNode(settingsRoot.Children, "common_logic") != null, "extended settings include the common logic branch");
            AssertSelfTest(FindRadialNode(settingsRoot.Children, "all_settings") != null, "extended settings include the all settings branch");
            AssertRadialSettingToggleCatalog();

            form.radialSelectionPathIds = new List<string> { "settings" };
            RadialLayout extendedSettingsLayout = form.ComputeRadialLayout();
            AssertSelfTest(extendedSettingsLayout.Levels.Count == 2, "extended settings ring stays visible alongside the root ring");
            AssertSelfTest(extendedSettingsLayout.Levels[1].Nodes.Count == 5, "extended settings layout shows five settings children");
            AssertRadialLevelArcCoverage(
                extendedSettingsLayout,
                1,
                3.0,
                87.0,
                "extended settings medium-density ring uses most of the available quadrant");
            AssertRadialQuadrant(extendedSettingsLayout);

            form.radialSelectionPathIds = new List<string> { "settings", "all_settings", "all_codex", "all_codex_quota_protection" };
            RadialLayout quotaProtectionLayout = form.ComputeRadialLayout();
            AssertSelfTest(quotaProtectionLayout.Levels.Count == 5, "deep all-settings quota protection path renders all open rings");
            AssertSelfTest(quotaProtectionLayout.Levels[4].Nodes.Count == 8, "quota protection branch keeps related switches together");
            AssertRadialLevelArcCoverage(
                quotaProtectionLayout,
                4,
                1.0,
                89.0,
                "dense quota protection ring uses the full available quadrant");
            AssertRadialQuadrant(quotaProtectionLayout);

            form.CurrentSettings.OperationSettingsLogicExtensionEnabled = false;
            form.cachedRadialRoots = null;
            form.radialSelectionPathIds = new List<string> { "settings", "special_settings" };
            RadialLayout specialLayout = form.ComputeRadialLayout();
            AssertSelfTest(specialLayout.Levels.Count == 3, "all three rings stay visible when drilled to special settings");
            AssertSelfTest(specialLayout.Levels[2].Nodes.Count == 3, "special settings has three leaves");
            AssertSelfTest(specialLayout.Levels[2].Nodes.Exists(n => n.Id == "ctf_restart"), "special settings includes CTF restart");
            AssertRadialQuadrant(specialLayout);

            List<RadialNode> resolved = form.ResolveSelectionPath();
            AssertSelfTest(
                resolved.Count == 2 && resolved[0].Id == "settings" && resolved[1].Id == "special_settings",
                "resolved selection path matches the stored id chain");

            form.radialSelectionPathIds = new List<string> { "settings", "does_not_exist" };
            List<RadialNode> truncated = form.ResolveSelectionPath();
            AssertSelfTest(truncated.Count == 1 && truncated[0].Id == "settings", "a stale selection id truncates the resolved path instead of throwing");
            AssertSelfTest(form.radialSelectionPathIds.Count == 1, "resolving a stale path also trims the stored id list");

            form.radialMenuOpen = true;
            form.CurrentSettings.OperationRadialIdleCollapseSeconds = WidgetSettings.DefaultOperationRadialIdleCollapseSeconds;
            form.radialLastInteractionUtc = DateTime.UtcNow.AddSeconds(-100);
            AssertSelfTest(form.ShouldRadialIdleCollapse(DateTime.UtcNow), "idle timeout elapsed should request a collapse");
            form.radialLastInteractionUtc = DateTime.UtcNow;
            AssertSelfTest(!form.ShouldRadialIdleCollapse(DateTime.UtcNow), "recent interaction should not request a collapse");
            form.CurrentSettings.OperationRadialIdleCollapseSeconds = WidgetSettings.NeverOperationRadialIdleCollapseSeconds;
            form.radialLastInteractionUtc = DateTime.UtcNow.AddSeconds(-1000);
            AssertSelfTest(!form.ShouldRadialIdleCollapse(DateTime.UtcNow), "idle timeout set to never should not request a collapse");
            form.CurrentSettings.OperationRadialIdleCollapseSeconds = 10;
            form.CurrentSettings.OperationRadialIdleResetOnInteractionEnabled = false;
            DateTime noResetInteractionUtc = DateTime.UtcNow.AddSeconds(-5);
            form.radialLastInteractionUtc = noResetInteractionUtc;
            form.ResetRadialIdleCollapseTimerForInteraction();
            AssertSelfTest(form.radialLastInteractionUtc == noResetInteractionUtc, "disabled interaction reset leaves the idle collapse timer unchanged");
            form.CurrentSettings.OperationRadialIdleResetOnInteractionEnabled = true;
            form.ResetRadialIdleCollapseTimerForInteraction();
            AssertSelfTest(form.radialLastInteractionUtc > noResetInteractionUtc, "enabled interaction reset refreshes the idle collapse timer");
            form.CurrentSettings.OperationRadialKeepOpenAfterLeafClickEnabled = true;
            AssertSelfTest(form.ShouldKeepRadialMenuOpenAfterLeafClick(), "default leaf click setting keeps the radial menu open");
            form.CurrentSettings.OperationRadialKeepOpenAfterLeafClickEnabled = false;
            AssertSelfTest(!form.ShouldKeepRadialMenuOpenAfterLeafClick(), "disabled leaf click setting closes the radial menu");

            form.CurrentSettings.OperationRadialCoreAutoHideKeepAliveEnabled = true;
            form.CurrentSettings.AutoHoverOpacityIdleSeconds = 2;
            DateTime visualStartUtc = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc);
            AssertSelfTest(!form.UpdateRadialCoreAutoHideThresholdVisual(visualStartUtc, true), "core keep-alive threshold starts without an immediate visual change");
            AssertSelfTest(!form.radialCoreAutoHideThresholdVisualActive, "core keep-alive visual is inactive before the threshold");
            AssertSelfTest(!form.UpdateRadialCoreAutoHideThresholdVisual(visualStartUtc.AddSeconds(1.9), true), "core keep-alive visual stays inactive before configured auto-hide seconds");
            AssertSelfTest(form.UpdateRadialCoreAutoHideThresholdVisual(visualStartUtc.AddSeconds(2.0), true), "core keep-alive visual changes when configured auto-hide seconds elapse");
            AssertSelfTest(form.radialCoreAutoHideThresholdVisualActive, "core keep-alive visual is active at the threshold");
            AssertSelfTest(form.UpdateRadialCoreAutoHideThresholdVisual(visualStartUtc.AddSeconds(2.1), false), "core keep-alive visual clears after leaving the core");
            AssertSelfTest(!form.radialCoreAutoHideThresholdVisualActive, "core keep-alive visual is inactive after leaving the core");
            RunRadialCoreSourceAlphaSelfTest(form);
        }
        finally
        {
            form.Dispose();
        }
    }

    private static void RunRadialCoreSourceAlphaSelfTest(OperationForm form)
    {
        using (Bitmap bitmap = new Bitmap(80, 80, PixelFormat.Format32bppPArgb))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (SolidBrush opaqueBrush = new SolidBrush(Color.FromArgb(255, 255, 255, 255)))
        {
            RectangleF rect = new RectangleF(16.0f, 16.0f, 48.0f, 48.0f);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(opaqueBrush, rect);

            form.DrawRadialCoreAutoHideThresholdVisual(graphics, rect);

            Color center = bitmap.GetPixel(40, 40);
            AssertSelfTest(center.A == RadialCoreAutoHideThresholdDimAlpha, "core keep-alive center alpha must be transparent to desktop");

            int maxAlpha = 0;
            int ringPixels = 0;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int alpha = bitmap.GetPixel(x, y).A;
                    if (alpha > maxAlpha)
                    {
                        maxAlpha = alpha;
                    }

                    if (alpha > RadialCoreAutoHideThresholdDimAlpha)
                    {
                        ringPixels++;
                    }
                }
            }

            AssertSelfTest(maxAlpha <= RadialCoreAutoHideThresholdRingAlpha, "core keep-alive ring must not inherit opaque pixels underneath");
            AssertSelfTest(ringPixels > 0, "core keep-alive ring pixels are present");
            AssertSelfTest(bitmap.GetPixel(0, 0).A == 0, "core keep-alive visual leaves exterior pixels transparent");
        }
    }

    private static RadialNode FindRadialNode(List<RadialNode> nodes, string id)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Id == id)
            {
                return nodes[i];
            }
        }

        return null;
    }

    private static void AssertRadialSiblingCaps(List<RadialNode> nodes, int level, string path)
    {
        int cap = GetRadialSiblingCap(level);
        AssertSelfTest(nodes.Count <= cap, "radial sibling count for " + path + " is within level " + level.ToString(CultureInfo.InvariantCulture) + " cap " + cap.ToString(CultureInfo.InvariantCulture));
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Children.Count > 0)
            {
                AssertRadialSiblingCaps(nodes[i].Children, level + 1, path + "/" + nodes[i].Id);
            }
        }
    }

    private static int GetRadialSiblingCap(int level)
    {
        if (level <= 1)
        {
            return 3;
        }

        if (level == 2)
        {
            return 5;
        }

        if (level == 3)
        {
            return 7;
        }

        if (level == 4)
        {
            return 9;
        }

        if (level == 5)
        {
            return 11;
        }

        return 13;
    }

    private static void AssertRadialSettingToggleCatalog()
    {
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        string[] names = new string[]
        {
            "StartupEnabled",
            "VisibilityOverlapIgnoresOperationPanelEnabled",
            "AiRequestProtectionAutoEnabled",
            "AiRequestProtectionManualBlockEnabled",
            "CodexQuotaPlanEnabled",
            "CodexQuotaPlanAutoResumePausedGoals",
            "SeelenDockForegroundPulseEnabled",
            "WinDRecoveryPulseEnabled",
            "PowerResumeRestartEnabled",
            "ForceShowForegroundFpsEnabled",
            "ResolutionCompatibilityModeEnabled",
            "FallbackDisconnectedDisplaysEnabled",
            "HoverOpacityEnabled",
            "SensitiveMouseModeEnabled",
            "AutoHoverOpacityIdleEnabled",
            "AutoHoverOpacityMaximizedEnabled",
            "OperationRadialCoreAutoHideKeepAliveEnabled",
            "OperationRadialKeepOpenAfterLeafClickEnabled",
            "OperationSettingsLogicExtensionEnabled",
            "SpecBoardAutoPopupEnabled",
            "BurnInHiddenModeColorProtectionEnabled",
            "HoverOpacityRevealDelayEnabled",
            "HoverOpacityCoverEnabled",
            "ReverseHoverOpacityRevealEnabled",
            "RadarClockAutoSwitchModelEnabled",
            "CodexRadarPublicJsonEnabled",
            "CodexRadarHtmlFallbackEnabled",
            "CodexRadarRssFallbackEnabled",
            "CodexQuotaDueResetProtectionEnabled",
            "CodexQuotaRssResetProtectionEnabled",
            "CodexQuotaProviderZeroDropProtectionEnabled",
            "CodexQuotaDuplicateSameBalanceRingProtectionEnabled",
            "CodexQuotaProviderFiveHourEarlyResetSpikeProtectionEnabled",
            "CodexQuotaProviderWeeklySpikeProtectionEnabled",
            "CodexQuotaStrictFiveHourResetBoundaryEnabled",
            "CodexQuotaWeeklyBaselineAutoRepairEnabled",
            "CodexRadarRandomTestEnabled",
            "CodexRadarRandomTestAutoRefresh",
            "GfwProbeEnabled",
            "AlertTestEnabled"
        };

        for (int i = 0; i < names.Length; i++)
        {
            AssertSelfTest(seen.Add(names[i]), "radial setting toggle catalog has no duplicate property " + names[i]);
            System.Reflection.PropertyInfo property = typeof(WidgetSettings).GetProperty(names[i]);
            AssertSelfTest(property != null && property.PropertyType == typeof(bool), "radial setting toggle property is a WidgetSettings bool: " + names[i]);
        }

        AssertSelfTest(seen.Contains("AiRequestProtectionManualBlockEnabled"), "radial setting catalog includes manual AI block special-case toggle");
        AssertSelfTest(seen.Contains("CodexQuotaPlanEnabled"), "radial setting catalog includes quota plan special-case toggle");
        AssertSelfTest(seen.Contains("StartupEnabled"), "radial setting catalog includes startup confirmation toggle");
    }

    private static void AssertRadialLevelArcCoverage(RadialLayout layout, int levelIdx, double maxFirstAngleDeg, double minLastAngleDeg, string message)
    {
        AssertSelfTest(levelIdx >= 0 && levelIdx < layout.Levels.Count, message + " has a valid level");
        RectangleF[] rects = layout.Levels[levelIdx].Rects;
        AssertSelfTest(rects.Length >= 2, message + " has multiple items");

        double firstAngleDeg = GetRadialItemAngleDeg(layout, rects[0]);
        double lastAngleDeg = GetRadialItemAngleDeg(layout, rects[rects.Length - 1]);
        AssertSelfTest(firstAngleDeg <= maxFirstAngleDeg, message + " starts near the right edge");
        AssertSelfTest(lastAngleDeg >= minLastAngleDeg, message + " ends near the top edge");
    }

    private static double GetRadialItemAngleDeg(RadialLayout layout, RectangleF rect)
    {
        double coreCx = layout.Core.Left + layout.Core.Width / 2.0;
        double coreCy = layout.Core.Top + layout.Core.Height / 2.0;
        double itemCx = rect.Left + rect.Width / 2.0;
        double itemCy = rect.Top + rect.Height / 2.0;
        double right = itemCx - coreCx;
        double up = coreCy - itemCy;
        return Math.Atan2(up, right) * 180.0 / Math.PI;
    }

    private static void AssertRadialQuadrant(RadialLayout layout)
    {
        RectangleF windowBounds = new RectangleF(0, 0, layout.WindowSize.Width, layout.WindowSize.Height);
        for (int levelIdx = 0; levelIdx < layout.Levels.Count; levelIdx++)
        {
            RectangleF[] rects = layout.Levels[levelIdx].Rects;
            for (int i = 0; i < rects.Length; i++)
            {
                AssertSelfTest(windowBounds.Contains(rects[i]), "radial item stays inside the window bounds");
                AssertSelfTest(
                    rects[i].Right >= layout.Core.Left && rects[i].Bottom <= layout.Core.Bottom,
                    "radial item stays in the up-right quadrant from the core (90 degree constraint)");
            }
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
            delegate(bool enabled) { return enabled; },
            delegate(string propertyName, bool enabled) { return enabled; });
    }
}
