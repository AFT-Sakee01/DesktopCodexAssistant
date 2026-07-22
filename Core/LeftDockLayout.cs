using System;
using System.Collections.Generic;
using System.Drawing;

internal static class LeftDockLayout
{
    private const int LogicalGap = 10;
    private static readonly EdgeDockTabRole[] DefaultQueueOrder =
    {
        EdgeDockTabRole.Network,
        EdgeDockTabRole.SpecBoard,
        EdgeDockTabRole.CodexTask,
        EdgeDockTabRole.Guard,
        EdgeDockTabRole.CodexIq
    };

    public static Rectangle ResolveWorkArea(WidgetSettings settings)
    {
        return settings.GetWorkAreaForModule(WidgetSettings.ModuleOperation);
    }

    public static int ResolveTransparencyOverride(WidgetSettings settings, EdgeDockTabRole role)
    {
        switch (role)
        {
            case EdgeDockTabRole.Network:
                return settings.NetworkMonitorTransparencyOverridePercent;
            case EdgeDockTabRole.CodexTask:
                return settings.CodexTaskBoardTransparencyOverridePercent;
            case EdgeDockTabRole.Guard:
                return settings.GuardBoardTransparencyOverridePercent;
            case EdgeDockTabRole.CodexIq:
                return settings.CodexIqBoardTransparencyOverridePercent;
            default:
                return settings.SpecBoardTransparencyOverridePercent;
        }
    }

    public static int ResolveScaleOverride(WidgetSettings settings, EdgeDockTabRole role)
    {
        switch (role)
        {
            case EdgeDockTabRole.Network:
                return settings.NetworkMonitorScaleOverridePercent;
            case EdgeDockTabRole.CodexTask:
                return settings.CodexTaskBoardScaleOverridePercent;
            case EdgeDockTabRole.Guard:
                return settings.GuardBoardScaleOverridePercent;
            case EdgeDockTabRole.CodexIq:
                return settings.CodexIqBoardScaleOverridePercent;
            default:
                return settings.SpecBoardScaleOverridePercent;
        }
    }

    public static Size ResolveTabSize(WidgetSettings settings, EdgeDockTabRole role, float roleLayerScale)
    {
        if (settings != null && settings.LeftDockAutoArrangeEnabled && IsRoleEnabled(settings, role))
        {
            Rectangle workArea = ResolveWorkArea(settings);
            EdgeDockTabRole[] roles = ResolveEnabledQueue(settings);
            float dpiScale = ResolveDpiScale(settings, role, roleLayerScale);
            Size[] sizes = ResolveAutoTabSizes(settings, roles, workArea, dpiScale);
            for (int i = 0; i < roles.Length && i < sizes.Length; i++)
            {
                if (roles[i] == role)
                {
                    return sizes[i];
                }
            }
        }

        return new Size(
            Math.Max(1, (int)Math.Round(EdgeDockTabForm.LogicalWidth * roleLayerScale)),
            Math.Max(1, (int)Math.Round(EdgeDockTabForm.LogicalHeight * roleLayerScale)));
    }

    public static int ResolveTabCenterY(WidgetSettings settings, EdgeDockTabRole role, float roleLayerScale)
    {
        if (!settings.LeftDockAutoArrangeEnabled)
        {
            int configured = ResolveConfiguredCenterY(settings, role);
            if (configured != WidgetSettings.AutoLeftDockTabCenterY)
            {
                return configured;
            }

            return ResolveLegacyAutoTabCenterY(settings, role, roleLayerScale);
        }

        Rectangle workArea = ResolveWorkArea(settings);
        float dpiScale = ResolveDpiScale(settings, role, roleLayerScale);
        Rectangle[] bounds = ResolveAutoTabBounds(settings, workArea, dpiScale);
        EdgeDockTabRole[] roles = ResolveEnabledQueue(settings);
        for (int i = 0; i < roles.Length && i < bounds.Length; i++)
        {
            if (roles[i] == role)
            {
                return bounds[i].Top + bounds[i].Height / 2;
            }
        }

        // Disabled roles do not own a visible tab, but returning a bounded value keeps callers safe
        // while a settings preview is switching the role off.
        return workArea.Top + workArea.Height / 2;
    }

    public static Point ResolveBoardBaseLocation(
        WidgetSettings settings,
        EdgeDockTabRole role,
        float roleLayerScale,
        Size boardSize)
    {
        Rectangle workArea = ResolveWorkArea(settings);
        Size tabSize = ResolveTabSize(settings, role, roleLayerScale);
        int left = workArea.Left + tabSize.Width;
        int top = ResolveTabCenterY(settings, role, roleLayerScale) - boardSize.Height / 2;
        left = Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - boardSize.Width)));
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - boardSize.Height)));
        return new Point(left, top);
    }

    internal static Point ResolveTabRuntimeLocation(
        WidgetSettings settings,
        EdgeDockTabRole role,
        float roleLayerScale,
        int requestedCenterY,
        Size tabSize,
        int legacyBurnInSalt)
    {
        Rectangle workArea = ResolveWorkArea(settings);
        if (!settings.LeftDockAutoArrangeEnabled)
        {
            int manualTop = requestedCenterY - tabSize.Height / 2;
            manualTop = Math.Max(
                workArea.Top,
                Math.Min(manualTop, Math.Max(workArea.Top, workArea.Bottom - tabSize.Height)));
            Point manualBaseLocation = new Point(workArea.Left, manualTop);
            // Manual mode intentionally preserves the historic per-tab salt. Users who placed tabs
            // independently therefore keep the same independent burn-in motion they had before the
            // automatic column was introduced.
            return BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
                manualBaseLocation,
                tabSize,
                workArea,
                legacyBurnInSalt);
        }

        float dpiScale = ResolveDpiScale(settings, role, roleLayerScale);
        Rectangle[] bounds = ResolveAutoTabBounds(settings, workArea, dpiScale);
        EdgeDockTabRole[] roles = ResolveEnabledQueue(settings);
        Rectangle groupBounds = ResolveGroupBounds(bounds);
        for (int i = 0; i < roles.Length && i < bounds.Length; i++)
        {
            if (roles[i] != role)
            {
                continue;
            }

            Point runtimeGroupLocation = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
                groupBounds.Location,
                groupBounds.Size,
                workArea,
                BurnInProtection.LeftDockButtonColumnSalt);
            return ApplySharedColumnVerticalOffset(bounds[i].Location, groupBounds, runtimeGroupLocation, workArea.Left);
        }

        // A disabled role can briefly receive a position request while live settings are being
        // applied. It is not a member of the automatic column, so keep that transition bounded and
        // retain its legacy salt until its form is hidden.
        int fallbackTop = requestedCenterY - tabSize.Height / 2;
        fallbackTop = Math.Max(
            workArea.Top,
            Math.Min(fallbackTop, Math.Max(workArea.Top, workArea.Bottom - tabSize.Height)));
        return BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            new Point(workArea.Left, fallbackTop),
            tabSize,
            workArea,
            legacyBurnInSalt);
    }

    public static bool IsPresentationBlocked(bool displaySuspended, bool hiddenForFullscreen)
    {
        return displaySuspended || hiddenForFullscreen;
    }

    internal static void RunSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.ResolutionCompatibilityModeEnabled = true;
        settings.ResolutionCompatibilityScalePercent = 100;
        Rectangle workArea = new Rectangle(-1920, 40, 1920, 1040);
        int[] scales = { 40, 100, 200 };
        for (int a = 0; a < scales.Length; a++)
        for (int b = 0; b < scales.Length; b++)
        for (int c = 0; c < scales.Length; c++)
        for (int d = 0; d < scales.Length; d++)
        for (int e = 0; e < scales.Length; e++)
        {
            settings.NetworkMonitorScaleOverridePercent = scales[a];
            settings.SpecBoardScaleOverridePercent = scales[b];
            settings.CodexTaskBoardScaleOverridePercent = scales[c];
            settings.GuardBoardScaleOverridePercent = scales[d];
            settings.CodexIqBoardScaleOverridePercent = scales[e];
            Rectangle[] bounds = ResolveAutoTabBounds(settings, workArea, 1.0f);
            for (int i = 0; i < bounds.Length; i++)
            {
                if (!workArea.Contains(bounds[i]) ||
                    i > 0 && (bounds[i - 1].Bottom > bounds[i].Top || bounds[i - 1].Top >= bounds[i].Top))
                {
                    throw new InvalidOperationException("Left dock mixed-scale queue self-test failed.");
                }
            }
        }

        WidgetSettings constrained = WidgetSettings.CreateDefaults();
        constrained.LeftDockAutoArrangeEnabled = true;
        constrained.LeftDockButtonGapPixels = WidgetSettings.MaxColumnButtonGapPixels;
        constrained.NetworkMonitorScaleOverridePercent = 200;
        constrained.SpecBoardScaleOverridePercent = 200;
        constrained.CodexTaskBoardScaleOverridePercent = 200;
        constrained.GuardBoardScaleOverridePercent = 200;
        constrained.CodexIqBoardScaleOverridePercent = 200;
        Rectangle gapLimitedWorkArea = new Rectangle(0, 0, 800, 360);
        Rectangle[] gapLimited = ResolveAutoTabBounds(constrained, gapLimitedWorkArea, 1.0f);
        if (gapLimited.Length != 5 || gapLimited[0].Top < gapLimitedWorkArea.Top ||
            gapLimited[gapLimited.Length - 1].Bottom > gapLimitedWorkArea.Bottom ||
            gapLimited[1].Top - gapLimited[0].Bottom != 15)
        {
            throw new InvalidOperationException("Left dock automatic layout must reduce an impossible custom gap to keep every tab reachable.");
        }

        Rectangle bodyLimitedWorkArea = new Rectangle(0, 0, 800, 240);
        Rectangle[] bodyLimited = ResolveAutoTabBounds(constrained, bodyLimitedWorkArea, 1.0f);
        EdgeDockTabRole[] bodyLimitedRoles = ResolveEnabledQueue(constrained);
        Size[] bodyLimitedSizes = ResolveAutoTabSizes(constrained, bodyLimitedRoles, bodyLimitedWorkArea, 1.0f);
        for (int i = 0; i < bodyLimited.Length; i++)
        {
            if (!bodyLimitedWorkArea.Contains(bodyLimited[i]) || bodyLimited[i].Size != bodyLimitedSizes[i] ||
                i > 0 && bodyLimited[i - 1].Bottom > bodyLimited[i].Top)
            {
                throw new InvalidOperationException("Left dock automatic layout must compact over-height tab bodies consistently with runtime window sizes.");
            }
        }

        WidgetSettings arranged = WidgetSettings.CreateDefaults();
        arranged.ResolutionCompatibilityModeEnabled = true;
        arranged.ResolutionCompatibilityScalePercent = 100;
        arranged.LeftDockAutoArrangeEnabled = true;
        arranged.LeftDockButtonGapPixels = 27;
        arranged.LeftDockGroupOffsetY = 0;
        arranged.LeftDockButtonOrder = new string[] { "CodexIq", "Network", "Guard", "SpecBoard", "CodexTask" };
        arranged.SpecBoardLeftDockEnabled = false;
        arranged.GuardBoardLeftDockEnabled = false;
        Rectangle[] compact = ResolveAutoTabBounds(arranged, workArea, 1.0f);
        EdgeDockTabRole[] compactRoles = ResolveEnabledQueue(arranged);
        if (compact.Length != 5 || compactRoles.Length != 5 ||
            compactRoles[0] != EdgeDockTabRole.CodexIq ||
            compactRoles[1] != EdgeDockTabRole.Network ||
            compactRoles[2] != EdgeDockTabRole.Guard ||
            compactRoles[3] != EdgeDockTabRole.SpecBoard ||
            compactRoles[4] != EdgeDockTabRole.CodexTask ||
            compact[1].Top - compact[0].Bottom != 27 ||
            compact[2].Top - compact[1].Bottom != 27 ||
            compact[3].Top - compact[2].Bottom != 27 ||
            compact[4].Top - compact[3].Bottom != 27)
        {
            throw new InvalidOperationException("Left dock fixed-five custom order or gap self-test failed.");
        }

        Rectangle centeredGroup = ResolveAutoTabGroupBounds(arranged, workArea, 1.0f);
        arranged.LeftDockGroupOffsetY = 100;
        Rectangle shiftedGroup = ResolveAutoTabGroupBounds(arranged, workArea, 1.0f);
        if (shiftedGroup.Top - centeredGroup.Top != 100 ||
            shiftedGroup.Top < workArea.Top || shiftedGroup.Bottom > workArea.Bottom)
        {
            throw new InvalidOperationException("Left dock whole-group vertical offset self-test failed.");
        }

        arranged.LeftDockGroupOffsetY = WidgetSettings.MaxColumnGroupOffsetY;
        Rectangle bottomClamped = ResolveAutoTabGroupBounds(arranged, workArea, 1.0f);
        arranged.LeftDockGroupOffsetY = WidgetSettings.MinColumnGroupOffsetY;
        Rectangle topClamped = ResolveAutoTabGroupBounds(arranged, workArea, 1.0f);
        if (bottomClamped.Bottom != workArea.Bottom || topClamped.Top != workArea.Top)
        {
            throw new InvalidOperationException("Left dock group offset must clamp the whole queue inside the work area.");
        }

        arranged.LeftDockGroupOffsetY = 0;
        Rectangle[] sharedOffsetBase = ResolveAutoTabBounds(arranged, workArea, 1.0f);
        Rectangle sharedOffsetGroup = ResolveGroupBounds(sharedOffsetBase);
        Point simulatedRuntimeGroup = new Point(sharedOffsetGroup.Left, sharedOffsetGroup.Top + 2);
        Point[] sharedOffsetRuntime = new Point[sharedOffsetBase.Length];
        for (int i = 0; i < sharedOffsetBase.Length; i++)
        {
            sharedOffsetRuntime[i] = ApplySharedColumnVerticalOffset(
                sharedOffsetBase[i].Location,
                sharedOffsetGroup,
                simulatedRuntimeGroup,
                workArea.Left);
            if (sharedOffsetRuntime[i].Y - sharedOffsetBase[i].Top != 2)
            {
                throw new InvalidOperationException("Left dock automatic column members must share one burn-in Y delta.");
            }

            if (i > 0 &&
                sharedOffsetRuntime[i].Y - (sharedOffsetRuntime[i - 1].Y + sharedOffsetBase[i - 1].Height) !=
                arranged.LeftDockButtonGapPixels)
            {
                throw new InvalidOperationException("Left dock shared burn-in movement must preserve configured button gaps.");
            }
        }

        WidgetSettings manual = WidgetSettings.CreateDefaults();
        manual.LeftDockAutoArrangeEnabled = false;
        manual.NetworkMonitorLeftDockTabCenterY = 777;
        if (ResolveTabCenterY(manual, EdgeDockTabRole.Network, 1.0f) != 777)
        {
            throw new InvalidOperationException("Disabling left dock auto arrange must preserve per-tab coordinates.");
        }

        settings.NetworkMonitorTransparencyOverridePercent = 41;
        settings.SpecBoardTransparencyOverridePercent = 52;
        settings.CodexTaskBoardTransparencyOverridePercent = 63;
        settings.GuardBoardTransparencyOverridePercent = 74;
        settings.CodexIqBoardTransparencyOverridePercent = 85;
        settings.NetworkMonitorScaleOverridePercent = 45;
        settings.SpecBoardScaleOverridePercent = 85;
        settings.CodexTaskBoardScaleOverridePercent = 125;
        settings.GuardBoardScaleOverridePercent = 175;
        settings.CodexIqBoardScaleOverridePercent = 195;
        if (ResolveTransparencyOverride(settings, EdgeDockTabRole.Network) != 41 ||
            ResolveTransparencyOverride(settings, EdgeDockTabRole.SpecBoard) != 52 ||
            ResolveTransparencyOverride(settings, EdgeDockTabRole.CodexTask) != 63 ||
            ResolveTransparencyOverride(settings, EdgeDockTabRole.Guard) != 74 ||
            ResolveTransparencyOverride(settings, EdgeDockTabRole.CodexIq) != 85 ||
            ResolveScaleOverride(settings, EdgeDockTabRole.Network) != 45 ||
            ResolveScaleOverride(settings, EdgeDockTabRole.SpecBoard) != 85 ||
            ResolveScaleOverride(settings, EdgeDockTabRole.CodexTask) != 125 ||
            ResolveScaleOverride(settings, EdgeDockTabRole.Guard) != 175 ||
            ResolveScaleOverride(settings, EdgeDockTabRole.CodexIq) != 195 ||
            !IsPresentationBlocked(true, false) ||
            !IsPresentationBlocked(false, true) ||
            IsPresentationBlocked(false, false))
        {
            throw new InvalidOperationException("Left dock role-slot or presentation policy self-test failed.");
        }
    }

    // Returns visible tab bounds in the same order as ResolveEnabledQueue. Automatic layout is an
    // all-or-nothing column: disabled roles consume no slot, and order/gap/offset move the active
    // buttons without rewriting their legacy per-role centre coordinates.
    internal static Rectangle[] ResolveAutoTabBounds(WidgetSettings settings, Rectangle workArea, float dpiScale)
    {
        EdgeDockTabRole[] roles = ResolveEnabledQueue(settings);
        int gap = Math.Max(
            WidgetSettings.MinColumnButtonGapPixels,
            Math.Min(WidgetSettings.MaxColumnButtonGapPixels, settings.LeftDockButtonGapPixels));
        int offsetY = Math.Max(
            WidgetSettings.MinColumnGroupOffsetY,
            Math.Min(WidgetSettings.MaxColumnGroupOffsetY, settings.LeftDockGroupOffsetY));
        return ResolveTabBoundsForRoles(settings, roles, workArea, dpiScale, gap, offsetY);
    }

    internal static Rectangle ResolveAutoTabGroupBounds(WidgetSettings settings, Rectangle workArea, float dpiScale)
    {
        return ResolveGroupBounds(ResolveAutoTabBounds(settings, workArea, dpiScale));
    }

    private static Rectangle ResolveGroupBounds(Rectangle[] bounds)
    {
        if (bounds.Length == 0)
        {
            return Rectangle.Empty;
        }

        Rectangle group = bounds[0];
        for (int i = 1; i < bounds.Length; i++)
        {
            group = Rectangle.Union(group, bounds[i]);
        }

        return group;
    }

    // Pure geometry helper: automatic mode moves the column envelope once, then every member reuses
    // that exact vertical delta. Keeping this separate makes the order/gap invariant testable without
    // depending on the wall-clock slot used by BurnInProtection.
    private static Point ApplySharedColumnVerticalOffset(
        Point memberBaseLocation,
        Rectangle groupBaseBounds,
        Point runtimeGroupLocation,
        int pinnedLeft)
    {
        int sharedDeltaY = runtimeGroupLocation.Y - groupBaseBounds.Top;
        return new Point(pinnedLeft, memberBaseLocation.Y + sharedDeltaY);
    }

    internal static EdgeDockTabRole[] ResolveEnabledQueue(WidgetSettings settings)
    {
        List<EdgeDockTabRole> ordered = new List<EdgeDockTabRole>(DefaultQueueOrder.Length);
        HashSet<EdgeDockTabRole> seen = new HashSet<EdgeDockTabRole>();
        string[] configured = settings == null ? null : settings.LeftDockButtonOrder;
        if (configured != null)
        {
            for (int i = 0; i < configured.Length; i++)
            {
                EdgeDockTabRole role;
                if (TryParseRoleId(configured[i], out role) && seen.Add(role) && IsRoleEnabled(settings, role))
                {
                    ordered.Add(role);
                }
            }
        }

        for (int i = 0; i < DefaultQueueOrder.Length; i++)
        {
            EdgeDockTabRole role = DefaultQueueOrder[i];
            if (seen.Add(role) && IsRoleEnabled(settings, role))
            {
                ordered.Add(role);
            }
        }

        return ordered.ToArray();
    }

    internal static bool IsRoleEnabled(WidgetSettings settings, EdgeDockTabRole role)
    {
        if (settings == null)
        {
            return false;
        }

        return role == EdgeDockTabRole.Network ||
            role == EdgeDockTabRole.SpecBoard ||
            role == EdgeDockTabRole.CodexTask ||
            role == EdgeDockTabRole.Guard ||
            role == EdgeDockTabRole.CodexIq;
    }

    private static Rectangle[] ResolveTabBoundsForRoles(
        WidgetSettings settings,
        EdgeDockTabRole[] roles,
        Rectangle workArea,
        float dpiScale,
        int gap,
        int offsetY)
    {
        if (roles == null || roles.Length == 0)
        {
            return new Rectangle[0];
        }

        Rectangle[] bounds = new Rectangle[roles.Length];
        Size[] sizes = ResolveAutoTabSizes(settings, roles, workArea, dpiScale);
        int memberHeight = 0;
        for (int i = 0; i < sizes.Length; i++) memberHeight += sizes[i].Height;
        int effectiveGap = roles.Length <= 1
            ? 0
            : Math.Min(Math.Max(0, gap), Math.Max(0, (workArea.Height - memberHeight) / (roles.Length - 1)));
        int totalHeight = memberHeight + effectiveGap * (roles.Length - 1);

        int top = workArea.Top + (workArea.Height - totalHeight) / 2 + offsetY;
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - totalHeight)));
        for (int i = 0; i < roles.Length; i++)
        {
            bounds[i] = new Rectangle(workArea.Left, top, sizes[i].Width, sizes[i].Height);
            top += sizes[i].Height + effectiveGap;
        }

        return bounds;
    }

    private static Size[] ResolveAutoTabSizes(
        WidgetSettings settings,
        EdgeDockTabRole[] roles,
        Rectangle workArea,
        float dpiScale)
    {
        Size[] sizes = new Size[roles.Length];
        int nominalHeight = 0;
        for (int i = 0; i < roles.Length; i++)
        {
            float scale = dpiScale * ResolveRoleScaleFactor(settings, roles[i]);
            sizes[i] = new Size(
                Math.Max(1, (int)Math.Round(EdgeDockTabForm.LogicalWidth * scale)),
                Math.Max(1, (int)Math.Round(EdgeDockTabForm.LogicalHeight * scale)));
            nominalHeight += sizes[i].Height;
        }

        if (nominalHeight <= Math.Max(1, workArea.Height))
        {
            return sizes;
        }

        // A custom gap is reduced first by ResolveTabBoundsForRoles. If the tab bodies alone still
        // exceed a very short work area, proportionally compact every member so none becomes
        // unreachable. This is an emergency responsive fallback; normal 40-200% role scales retain
        // their requested size on supported desktop work areas.
        double fitScale = Math.Max(1, workArea.Height) / (double)nominalHeight;
        for (int i = 0; i < sizes.Length; i++)
        {
            sizes[i] = new Size(
                Math.Max(1, (int)Math.Floor(sizes[i].Width * fitScale)),
                Math.Max(1, (int)Math.Floor(sizes[i].Height * fitScale)));
        }

        return sizes;
    }

    private static int ResolveLegacyAutoTabCenterY(WidgetSettings settings, EdgeDockTabRole role, float roleLayerScale)
    {
        Rectangle workArea = ResolveWorkArea(settings);
        float dpiScale = ResolveDpiScale(settings, role, roleLayerScale);
        int gap = Math.Max(1, (int)Math.Round(LogicalGap * dpiScale));
        Rectangle[] bounds = ResolveTabBoundsForRoles(
            settings,
            DefaultQueueOrder,
            workArea,
            dpiScale,
            gap,
            0);
        int index = DefaultRoleIndex(role);
        return bounds[index].Top + bounds[index].Height / 2;
    }

    private static bool TryParseRoleId(string value, out EdgeDockTabRole role)
    {
        role = EdgeDockTabRole.Network;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string id = value.Trim();
        for (int i = 0; i < DefaultQueueOrder.Length; i++)
        {
            if (string.Equals(id, DefaultQueueOrder[i].ToString(), StringComparison.OrdinalIgnoreCase))
            {
                role = DefaultQueueOrder[i];
                return true;
            }
        }

        return false;
    }

    private static float ResolveDpiScale(WidgetSettings settings, EdgeDockTabRole role, float roleLayerScale)
    {
        return Math.Max(0.25f, roleLayerScale / Math.Max(0.01f, ResolveRoleScaleFactor(settings, role)));
    }

    private static float ResolveRoleScaleFactor(WidgetSettings settings, EdgeDockTabRole role)
    {
        int value = ResolveScaleOverride(settings, role);
        if (value >= WidgetSettings.MinResolutionCompatibilityScalePercent)
        {
            return Math.Min(WidgetSettings.MaxWindowScaleOverridePercent, value) / 100.0f;
        }

        return settings.GetResolutionCompatibilityScaleFactor();
    }

    private static int ResolveConfiguredCenterY(WidgetSettings settings, EdgeDockTabRole role)
    {
        switch (role)
        {
            case EdgeDockTabRole.Network:
                return settings.NetworkMonitorLeftDockTabCenterY;
            case EdgeDockTabRole.CodexTask:
                return settings.CodexTaskBoardLeftDockTabCenterY;
            case EdgeDockTabRole.Guard:
                return settings.GuardBoardLeftDockTabCenterY;
            case EdgeDockTabRole.CodexIq:
                return settings.CodexIqBoardLeftDockTabCenterY;
            default:
                return settings.SpecBoardLeftDockTabCenterY;
        }
    }

    private static int DefaultRoleIndex(EdgeDockTabRole role)
    {
        for (int i = 0; i < DefaultQueueOrder.Length; i++)
        {
            if (DefaultQueueOrder[i] == role)
            {
                return i;
            }
        }

        return 0;
    }
}
