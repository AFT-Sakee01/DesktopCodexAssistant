using System;
using System.Drawing;

internal static class HoverInteractionPolicy
{
    internal sealed class HoverOpacityDelayState
    {
        internal bool RawHoverActive;
        internal bool DelayedRevealActive;
        internal DateTime EnteredActivationRangeUtc = DateTime.MinValue;
        internal DateTime LeftActivationRangeUtc = DateTime.MinValue;

        internal void Reset()
        {
            this.RawHoverActive = false;
            this.DelayedRevealActive = false;
            this.EnteredActivationRangeUtc = DateTime.MinValue;
            this.LeftActivationRangeUtc = DateTime.MinValue;
        }
    }

    public static bool IsCursorInActivationRange(WidgetSettings settings, Rectangle windowBounds)
    {
        return IsPointInActivationRange(settings, CursorPosition(), windowBounds);
    }

    internal static bool IsPointInActivationRange(WidgetSettings settings, Point cursor, Rectangle windowBounds)
    {
        if (windowBounds.Width <= 0 || windowBounds.Height <= 0)
        {
            return false;
        }

        if (settings == null || !settings.SensitiveMouseModeEnabled)
        {
            return windowBounds.Contains(cursor);
        }

        Rectangle cursorRange = GetCursorActivationSquare(settings, cursor);
        return RectanglesTouchOrOverlap(cursorRange, windowBounds);
    }

    public static bool IsReverseRevealActive(WidgetSettings settings, Rectangle windowBounds, ref DateTime revealUntilUtc)
    {
        return IsReverseRevealActiveAt(settings, CursorPosition(), windowBounds, DateTime.UtcNow, ref revealUntilUtc);
    }

    public static bool IsHoverOpacityTargetActive(
        WidgetSettings settings,
        Rectangle windowBounds,
        bool hiddenForFullscreen,
        bool visible,
        ref DateTime revealUntilUtc,
        HoverOpacityDelayState delayState)
    {
        return IsHoverOpacityTargetActive(
            settings,
            windowBounds,
            hiddenForFullscreen,
            visible,
            ref revealUntilUtc,
            delayState,
            false);
    }

    public static bool IsHoverOpacityTargetActive(
        WidgetSettings settings,
        Rectangle windowBounds,
        bool hiddenForFullscreen,
        bool visible,
        ref DateTime revealUntilUtc,
        HoverOpacityDelayState delayState,
        bool autoHideKeepAliveActive)
    {
        return IsHoverOpacityTargetActiveAt(
            settings,
            CursorPosition(),
            windowBounds,
            hiddenForFullscreen,
            visible,
            DateTime.UtcNow,
            ref revealUntilUtc,
            delayState,
            autoHideKeepAliveActive);
    }

    internal static bool IsHoverOpacityTargetActiveAt(
        WidgetSettings settings,
        Point cursor,
        Rectangle windowBounds,
        bool hiddenForFullscreen,
        bool visible,
        DateTime nowUtc,
        ref DateTime revealUntilUtc,
        HoverOpacityDelayState delayState)
    {
        return IsHoverOpacityTargetActiveAt(
            settings,
            cursor,
            windowBounds,
            hiddenForFullscreen,
            visible,
            nowUtc,
            ref revealUntilUtc,
            delayState,
            false);
    }

    internal static bool IsHoverOpacityTargetActiveAt(
        WidgetSettings settings,
        Point cursor,
        Rectangle windowBounds,
        bool hiddenForFullscreen,
        bool visible,
        DateTime nowUtc,
        ref DateTime revealUntilUtc,
        HoverOpacityDelayState delayState,
        bool autoHideKeepAliveActive)
    {
        if (settings == null || hiddenForFullscreen || !visible)
        {
            ResetDelayState(delayState);
            revealUntilUtc = DateTime.MinValue;
            return false;
        }

        if (autoHideKeepAliveActive)
        {
            ResetDelayState(delayState);
            revealUntilUtc = DateTime.MinValue;
            return false;
        }

        bool hoverOpacityEnabled = settings.HoverOpacityEnabled;
        bool forceHoverOpacityActive = settings.ForceHoverOpacityActive;
        if (!hoverOpacityEnabled && !forceHoverOpacityActive)
        {
            ResetDelayState(delayState);
            revealUntilUtc = DateTime.MinValue;
            return false;
        }

        if (IsReverseRevealActiveAt(settings, cursor, windowBounds, nowUtc, ref revealUntilUtc))
        {
            ResetDelayState(delayState);
            return false;
        }

        if (forceHoverOpacityActive)
        {
            ResetDelayState(delayState);
            return true;
        }

        bool rawHoverActive = IsPointInActivationRange(settings, cursor, windowBounds);
        return IsDelayedHoverOpacityActiveAt(settings, rawHoverActive, nowUtc, delayState);
    }

    internal static bool IsReverseRevealActiveAt(
        WidgetSettings settings,
        Point cursor,
        Rectangle windowBounds,
        DateTime nowUtc,
        ref DateTime revealUntilUtc)
    {
        if (settings == null ||
            !settings.ReverseHoverOpacityRevealEnabled ||
            !settings.ManualHoverOpacityActive ||
            !settings.ForceHoverOpacityActive)
        {
            revealUntilUtc = DateTime.MinValue;
            return false;
        }

        if (IsPointInActivationRange(settings, cursor, windowBounds))
        {
            revealUntilUtc = nowUtc.AddSeconds(GetReverseRevealDelaySeconds(settings));
            return true;
        }

        if (revealUntilUtc != DateTime.MinValue && nowUtc <= revealUntilUtc)
        {
            return true;
        }

        revealUntilUtc = DateTime.MinValue;
        return false;
    }

    internal static bool IsDelayedHoverOpacityActiveAt(
        WidgetSettings settings,
        bool rawHoverActive,
        DateTime nowUtc,
        HoverOpacityDelayState state)
    {
        if (state == null)
        {
            return rawHoverActive;
        }

        if (settings == null || !settings.HoverOpacityRevealDelayEnabled)
        {
            state.Reset();
            return rawHoverActive;
        }

        double revealDelaySeconds = GetHoverOpacityRevealDelaySeconds(settings);
        double resetSeconds = GetHoverOpacityRevealResetSeconds(settings);

        if (rawHoverActive)
        {
            if (!state.RawHoverActive)
            {
                state.RawHoverActive = true;
                state.EnteredActivationRangeUtc = nowUtc;
            }

            if (state.DelayedRevealActive &&
                state.EnteredActivationRangeUtc != DateTime.MinValue &&
                (nowUtc - state.EnteredActivationRangeUtc).TotalSeconds >= resetSeconds)
            {
                state.DelayedRevealActive = false;
                state.LeftActivationRangeUtc = DateTime.MinValue;
            }

            return true;
        }

        if (state.RawHoverActive)
        {
            bool resetReached =
                state.EnteredActivationRangeUtc == DateTime.MinValue ||
                (nowUtc - state.EnteredActivationRangeUtc).TotalSeconds >= resetSeconds;
            if (!state.DelayedRevealActive || resetReached)
            {
                state.LeftActivationRangeUtc = nowUtc;
                state.DelayedRevealActive = true;
            }

            state.RawHoverActive = false;
            state.EnteredActivationRangeUtc = DateTime.MinValue;
        }
        else if (!state.DelayedRevealActive)
        {
            state.LeftActivationRangeUtc = DateTime.MinValue;
        }

        if (!state.DelayedRevealActive || state.LeftActivationRangeUtc == DateTime.MinValue)
        {
            return false;
        }

        if ((nowUtc - state.LeftActivationRangeUtc).TotalSeconds < revealDelaySeconds)
        {
            return true;
        }

        state.DelayedRevealActive = false;
        state.LeftActivationRangeUtc = DateTime.MinValue;
        return false;
    }

    internal static Rectangle GetCursorActivationSquare(WidgetSettings settings, Point cursor)
    {
        int side = GetSensitiveMouseRangePixels(settings);
        int halfLow = side / 2;
        int halfHigh = side - halfLow;
        return Rectangle.FromLTRB(
            cursor.X - halfLow,
            cursor.Y - halfLow,
            cursor.X + halfHigh,
            cursor.Y + halfHigh);
    }

    internal static int GetSensitiveMouseRangePixels(WidgetSettings settings)
    {
        int value = settings == null
            ? WidgetSettings.DefaultSensitiveMouseRangePixels
            : settings.SensitiveMouseRangePixels;
        return Clamp(
            value,
            WidgetSettings.MinSensitiveMouseRangePixels,
            WidgetSettings.MaxSensitiveMouseRangePixels);
    }

    internal static int GetReverseRevealDelaySeconds(WidgetSettings settings)
    {
        int value = settings == null
            ? WidgetSettings.DefaultReverseHoverOpacityRestoreDelaySeconds
            : settings.ReverseHoverOpacityRestoreDelaySeconds;
        return Clamp(
            value,
            WidgetSettings.MinReverseHoverOpacityRestoreDelaySeconds,
            WidgetSettings.MaxReverseHoverOpacityRestoreDelaySeconds);
    }

    internal static double GetHoverOpacityRevealDelaySeconds(WidgetSettings settings)
    {
        double value = settings == null
            ? WidgetSettings.DefaultHoverOpacityRevealDelaySeconds
            : settings.HoverOpacityRevealDelaySeconds;
        return Clamp(
            value,
            WidgetSettings.MinHoverOpacityRevealDelaySeconds,
            WidgetSettings.MaxHoverOpacityRevealDelaySeconds);
    }

    internal static double GetHoverOpacityRevealResetSeconds(WidgetSettings settings)
    {
        double value = settings == null
            ? WidgetSettings.DefaultHoverOpacityRevealResetSeconds
            : settings.HoverOpacityRevealResetSeconds;
        return Clamp(
            value,
            WidgetSettings.MinHoverOpacityRevealResetSeconds,
            WidgetSettings.MaxHoverOpacityRevealResetSeconds);
    }

    private static bool RectanglesTouchOrOverlap(Rectangle left, Rectangle right)
    {
        return left.Left <= right.Right &&
            left.Right >= right.Left &&
            left.Top <= right.Bottom &&
            left.Bottom >= right.Top;
    }

    internal static void RunSelfTest()
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.SensitiveMouseModeEnabled = true;
        settings.SensitiveMouseRangePixels = 100;
        Rectangle bounds = new Rectangle(100, 100, 40, 40);
        AssertPolicy(!IsPointInActivationRange(settings, new Point(20, 20), bounds), "far cursor should miss");
        AssertPolicy(IsPointInActivationRange(settings, new Point(60, 120), bounds), "100-pixel square should intersect");

        settings.SensitiveMouseModeEnabled = false;
        AssertPolicy(!IsPointInActivationRange(settings, new Point(60, 120), bounds), "plain point should miss when sensitive mode is off");

        settings.SensitiveMouseModeEnabled = true;
        settings.SensitiveMouseRangePixels = 10;
        AssertPolicy(!IsPointInActivationRange(settings, new Point(94, 120), bounds), "small range should miss before the edge");
        AssertPolicy(IsPointInActivationRange(settings, new Point(95, 120), bounds), "small range should intersect at the edge");

        settings.ManualHoverOpacityActive = true;
        settings.ForceHoverOpacityActive = true;
        settings.ReverseHoverOpacityRevealEnabled = true;
        settings.ReverseHoverOpacityRestoreDelaySeconds = 3;
        DateTime revealUntil = DateTime.MinValue;
        DateTime now = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc);
        AssertPolicy(IsReverseRevealActiveAt(settings, new Point(120, 120), bounds, now, ref revealUntil), "reverse reveal should activate on hover");
        AssertPolicy(IsReverseRevealActiveAt(settings, new Point(20, 20), bounds, now.AddSeconds(2), ref revealUntil), "reverse reveal should hold during delay");
        AssertPolicy(!IsReverseRevealActiveAt(settings, new Point(20, 20), bounds, now.AddSeconds(4), ref revealUntil), "reverse reveal should expire");

        HoverOpacityDelayState delayState = new HoverOpacityDelayState();
        settings.HoverOpacityEnabled = true;
        settings.ForceHoverOpacityActive = false;
        settings.ManualHoverOpacityActive = false;
        settings.HoverOpacityRevealDelayEnabled = true;
        settings.HoverOpacityRevealDelaySeconds = 3.0;
        settings.HoverOpacityRevealResetSeconds = 1.0;
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, true, now, delayState), "hover delay should hide while inside");
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, false, now.AddSeconds(0.1), delayState), "hover delay should hold after leaving");
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, true, now.AddSeconds(1.0), delayState), "brief re-entry should stay hidden");
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, false, now.AddSeconds(1.4), delayState), "brief re-entry should not reset leave countdown");
        AssertPolicy(!IsDelayedHoverOpacityActiveAt(settings, false, now.AddSeconds(3.2), delayState), "hover delay should reveal after original countdown");

        delayState.Reset();
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, true, now, delayState), "second hover delay should hide while inside");
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, false, now.AddSeconds(0.1), delayState), "second hover delay should hold after leaving");
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, true, now.AddSeconds(1.0), delayState), "sustained re-entry should stay hidden");
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, true, now.AddSeconds(2.2), delayState), "sustained re-entry should reset countdown");
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, false, now.AddSeconds(2.3), delayState), "reset countdown should hold after second leave");
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, false, now.AddSeconds(4.9), delayState), "reset countdown should not reveal too early");
        AssertPolicy(!IsDelayedHoverOpacityActiveAt(settings, false, now.AddSeconds(5.4), delayState), "reset countdown should reveal after second leave");

        delayState.Reset();
        settings.HoverOpacityRevealDelayEnabled = false;
        AssertPolicy(IsDelayedHoverOpacityActiveAt(settings, true, now, delayState), "disabled hover delay should still hide while inside");
        AssertPolicy(!IsDelayedHoverOpacityActiveAt(settings, false, now.AddSeconds(0.1), delayState), "disabled hover delay should reveal immediately after leaving");

        delayState.Reset();
        settings.HoverOpacityRevealDelayEnabled = true;
        AssertPolicy(
            IsHoverOpacityTargetActiveAt(settings, new Point(120, 120), bounds, false, true, now, ref revealUntil, delayState),
            "hover opacity should hide before keep-alive is applied");
        AssertPolicy(
            !IsHoverOpacityTargetActiveAt(settings, new Point(120, 120), bounds, false, true, now.AddSeconds(0.1), ref revealUntil, delayState, true),
            "keep-alive should suppress hover opacity target activation");
        AssertPolicy(
            revealUntil == DateTime.MinValue && !delayState.RawHoverActive && !delayState.DelayedRevealActive,
            "keep-alive should reset delayed and reverse reveal state");
    }

    private static Point CursorPosition()
    {
        return System.Windows.Forms.Cursor.Position;
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static double Clamp(double value, double min, double max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static void ResetDelayState(HoverOpacityDelayState state)
    {
        if (state != null)
        {
            state.Reset();
        }
    }

    private static void AssertPolicy(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Hover interaction policy self-test failed: " + message);
        }
    }
}
