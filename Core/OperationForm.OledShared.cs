using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Shared button chrome for the four OLED-safe restyle schemes (Typographic, AmberHud, WarmCard,
// Phosphor) added in 1.0.3.44. DrawButton hardcodes DesignTokens.Colors.Accent (blue) for the Start
// button and the "active" state, DrawStartGlyph gradients into AccentGradientEnd (blue), and
// DrawMemoryPie/DrawFpsPanel tint their highlight with Accent/AccentSoft/AccentBorder (blue) - none of
// that is guaranteed blue-free by construction, so this file reimplements just the color selection
// while reusing every icon glyph, RoundedSegment shape, and layout call unchanged.
internal sealed partial class OperationForm
{
    private struct OledButtonPalette
    {
        public Color NormalFill;
        public Color NormalBorder;
        public Color PrimaryAccent;
        public Color ActiveFill;
        public Color ActiveBorder;
        public Color ActiveRing;
        public Color WarningFill;
        public Color DangerFill;
        public Color HighlightFill;
    }

    private static OledButtonPalette GetTypographicButtonPalette()
    {
        OledButtonPalette palette = new OledButtonPalette();
        palette.NormalFill = DesignTokens.WithAlpha(DesignTokens.OledTypographic.Muted, 20);
        palette.NormalBorder = DesignTokens.WithAlpha(DesignTokens.OledTypographic.Muted, 60);
        palette.PrimaryAccent = DesignTokens.OledTypographic.Primary;
        palette.ActiveFill = DesignTokens.WithAlpha(DesignTokens.OledTypographic.AccentGood, 60);
        palette.ActiveBorder = DesignTokens.WithAlpha(DesignTokens.OledTypographic.AccentGood, 140);
        palette.ActiveRing = DesignTokens.WithAlpha(DesignTokens.OledTypographic.AccentGood, 110);
        palette.WarningFill = DesignTokens.WithAlpha(DesignTokens.OledTypographic.AccentWarn, 90);
        palette.DangerFill = DesignTokens.WithAlpha(DesignTokens.OledTypographic.AccentDanger, 100);
        palette.HighlightFill = DesignTokens.WithAlpha(DesignTokens.OledTypographic.AccentWarn, 70);
        return palette;
    }

    private static OledButtonPalette GetAmberHudButtonPalette()
    {
        OledButtonPalette palette = new OledButtonPalette();
        palette.NormalFill = DesignTokens.WithAlpha(DesignTokens.OledAmber.Dim, 26);
        palette.NormalBorder = DesignTokens.WithAlpha(DesignTokens.OledAmber.Dim, 130);
        palette.PrimaryAccent = DesignTokens.OledAmber.Bright;
        palette.ActiveFill = DesignTokens.WithAlpha(DesignTokens.OledAmber.Bright, 70);
        palette.ActiveBorder = DesignTokens.WithAlpha(DesignTokens.OledAmber.Bright, 180);
        palette.ActiveRing = DesignTokens.WithAlpha(DesignTokens.OledAmber.Bright, 140);
        palette.WarningFill = DesignTokens.WithAlpha(DesignTokens.OledAmber.Base, 110);
        palette.DangerFill = DesignTokens.WithAlpha(DesignTokens.OledAmber.Danger, 110);
        palette.HighlightFill = DesignTokens.WithAlpha(DesignTokens.OledAmber.Base, 80);
        return palette;
    }

    private static OledButtonPalette GetWarmCardButtonPalette()
    {
        OledButtonPalette palette = new OledButtonPalette();
        palette.NormalFill = DesignTokens.OledCard.CardFill;
        palette.NormalBorder = Color.Transparent;
        palette.PrimaryAccent = DesignTokens.OledCard.DotGood;
        palette.ActiveFill = DesignTokens.WithAlpha(DesignTokens.OledCard.DotGood, 55);
        palette.ActiveBorder = DesignTokens.WithAlpha(DesignTokens.OledCard.DotGood, 140);
        palette.ActiveRing = DesignTokens.WithAlpha(DesignTokens.OledCard.DotGood, 100);
        palette.WarningFill = DesignTokens.WithAlpha(DesignTokens.OledCard.DotWarn, 90);
        palette.DangerFill = DesignTokens.WithAlpha(DesignTokens.OledCard.DotDanger, 100);
        palette.HighlightFill = DesignTokens.WithAlpha(DesignTokens.OledCard.DotWarn, 70);
        return palette;
    }

    private static OledButtonPalette GetPhosphorButtonPalette()
    {
        OledButtonPalette palette = new OledButtonPalette();
        palette.NormalFill = Color.Transparent;
        palette.NormalBorder = DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Faint, 150);
        palette.PrimaryAccent = DesignTokens.OledPhosphor.Bright;
        palette.ActiveFill = DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Bright, 40);
        palette.ActiveBorder = DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Bright, 170);
        palette.ActiveRing = DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Bright, 120);
        palette.WarningFill = DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Warn, 90);
        palette.DangerFill = DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Danger, 100);
        palette.HighlightFill = DesignTokens.WithAlpha(DesignTokens.OledPhosphor.Warn, 70);
        return palette;
    }

    // Mirrors DrawOperationWindowClassic's button set and layout exactly (same fixed sequence of
    // GetButtonRects()-indexed DrawButtonOled calls, same fallback-panel/battery-care/FPS-panel
    // conditionals) - only the per-button color source (palette instead of DesignTokens.Colors) and
    // the drawing primitive (DrawButtonOled instead of DrawButton) differ.
    private void DrawOperationButtonsOled(Graphics g, OledButtonPalette palette)
    {
        RectangleF[] rects = GetButtonRects();
        if (ShouldDrawStartFallbackPanel())
        {
            DrawStartFallbackPanel(g, rects[StartButtonIndex], palette.PrimaryAccent);
        }
        else
        {
            DrawButtonOled(g, rects[StartButtonIndex], StartButtonIndex, true, false, false, true, palette);
        }

        DrawButtonOled(g, rects[WindowsSettingsButtonIndex], WindowsSettingsButtonIndex, false, false, false, false, palette);
        DrawButtonOled(g, rects[HoverOpacityToggleButtonIndex], HoverOpacityToggleButtonIndex, false, false, false, false, palette);
        DrawButtonOled(g, rects[AppSettingsButtonIndex], AppSettingsButtonIndex, false, false, false, false, palette);
        DrawButtonOled(g, rects[RestartButtonIndex], RestartButtonIndex, false, false, false, false, palette);
        if (ShouldShowBatteryCareButtons())
        {
            DrawButtonOled(g, rects[BatteryCarePauseButtonIndex], BatteryCarePauseButtonIndex, false, false, false, false, palette);
            DrawButtonOled(g, rects[BatteryLimitRestoreButtonIndex], BatteryLimitRestoreButtonIndex, false, true, false, false, palette);
        }
        else if (ShouldDrawFpsPanel())
        {
            DrawFpsPanel(g, GetBatteryCareFallbackRect(rects), palette.HighlightFill, palette.ActiveBorder, palette.ActiveRing);
        }

        DrawButtonOled(g, rects[WindowsPowerMenuButtonIndex], WindowsPowerMenuButtonIndex, false, false, false, false, palette);
        DrawButtonOled(g, rects[RefreshButtonIndex], RefreshButtonIndex, false, false, false, false, palette);
        DrawButtonOled(g, rects[TaskManagerButtonIndex], TaskManagerButtonIndex, false, false, false, false, palette);
        DrawButtonOled(g, rects[WindowsQuickSettingsButtonIndex], WindowsQuickSettingsButtonIndex, false, false, false, false, palette);
        DrawButtonOled(g, rects[LiveCaptionsButtonIndex], LiveCaptionsButtonIndex, false, false, false, false, palette);
        DrawButtonOled(g, rects[WindowsAiStudioButtonIndex], WindowsAiStudioButtonIndex, false, false, true, false, palette);
    }

    // Mirrors DrawButton's shape/state/glyph logic exactly - only the fill/border/ring colors are
    // sourced from the OLED palette instead of DesignTokens.Colors.Accent and friends.
    private void DrawButtonOled(Graphics g, RectangleF rect, int button, bool leftSegment, bool topRight, bool bottomRight, bool startButton, OledButtonPalette palette)
    {
        if (!IsButtonVisible(button) || rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        double hover = this.hoverProgress[button];
        double press = button == this.pressedButton ? 1.0 : GetPressProgress(button);
        bool unavailable = IsButtonUnavailable(button);
        bool active = !unavailable && IsStateButtonActive(button);
        if (unavailable)
        {
            hover = 0.0;
            press = 0.0;
        }

        int backgroundAlpha = GetBackgroundOpacityAlpha();
        int fillAlpha = ScaleAlpha(ClampByte((int)Math.Round(58 + hover * 54 + press * 36)), backgroundAlpha);
        int outlineAlpha = ScaleAlpha(ClampByte((int)Math.Round(44 + hover * 70 + press * 40)), backgroundAlpha);
        Color fill;
        if (unavailable)
        {
            fill = DesignTokens.WithAlpha(DesignTokens.Colors.Control, ScaleAlpha(ClampByte((int)Math.Round(34 + press * 16)), backgroundAlpha));
            outlineAlpha = ScaleAlpha(44, backgroundAlpha);
        }
        else if (button == BatteryCarePauseButtonIndex)
        {
            fill = this.batteryCarePauseRunning
                ? DesignTokens.WithAlpha(palette.WarningFill, ClampByte(fillAlpha + ScaleAlpha(22, backgroundAlpha)))
                : DesignTokens.WithAlpha(palette.NormalFill, ScaleAlpha(ClampByte((int)Math.Round(42 + hover * 58 + press * 40)), backgroundAlpha));
        }
        else if (button == BatteryLimitRestoreButtonIndex)
        {
            fill = this.batteryLimitRestoreRunning
                ? DesignTokens.WithAlpha(palette.DangerFill, ClampByte(fillAlpha + ScaleAlpha(28, backgroundAlpha)))
                : DesignTokens.WithAlpha(palette.DangerFill, ScaleAlpha(ClampByte((int)Math.Round(68 + hover * 64 + press * 44)), backgroundAlpha));
        }
        else if (button == StartButtonIndex)
        {
            fill = DesignTokens.WithAlpha(palette.PrimaryAccent, ClampByte(fillAlpha + 6));
        }
        else if (active)
        {
            fill = DesignTokens.WithAlpha(palette.ActiveFill, ScaleAlpha(ClampByte((int)Math.Round(92 + hover * 66 + press * 42)), backgroundAlpha));
        }
        else if (button == LiveCaptionsButtonIndex || button == WindowsAiStudioButtonIndex)
        {
            fill = DesignTokens.WithAlpha(palette.HighlightFill, ScaleAlpha(ClampByte((int)Math.Round(74 + hover * 66 + press * 40)), backgroundAlpha));
        }
        else
        {
            fill = DesignTokens.WithAlpha(palette.NormalFill, fillAlpha);
        }

        Color border = active
            ? DesignTokens.WithAlpha(palette.ActiveBorder, ClampByte(outlineAlpha + ScaleAlpha(72, backgroundAlpha)))
            : DesignTokens.WithAlpha(palette.NormalBorder, outlineAlpha);
        float radius = Math.Max(S(5), rect.Height * 0.24f);
        using (GraphicsPath path = RoundedSegment(rect, radius, leftSegment, topRight, bottomRight))
        {
            using (SolidBrush brush = new SolidBrush(fill))
            {
                g.FillPath(brush, path);
            }

            using (Pen pen = new Pen(border, Math.Max(1.0f, this.scale)))
            {
                g.DrawPath(pen, path);
            }

            if (active)
            {
                RectangleF ringRect = RectangleF.Inflate(rect, -Math.Max(1.0f, this.scale), -Math.Max(1.0f, this.scale));
                using (GraphicsPath ringPath = RoundedSegment(ringRect, Math.Max(S(4), ringRect.Height * 0.22f), leftSegment, topRight, bottomRight))
                using (Pen ringPen = new Pen(palette.ActiveRing, Math.Max(1.0f, this.scale)))
                {
                    g.DrawPath(ringPen, ringPath);
                }
            }
        }

        RectangleF iconRect = GetIconRect(rect);
        if (startButton)
        {
            DrawStartGlyph(g, iconRect, palette.PrimaryAccent);
        }
        else if (button == WindowsSettingsButtonIndex)
        {
            DrawSettingsGlyph(g, iconRect);
        }
        else if (button == WindowsPowerMenuButtonIndex)
        {
            DrawPowerGlyph(g, iconRect);
        }
        else if (button == AppSettingsButtonIndex)
        {
            DrawAppSettingsGlyph(g, iconRect, palette.PrimaryAccent);
        }
        else if (button == RefreshButtonIndex)
        {
            DrawRefreshGlyph(g, iconRect, palette.PrimaryAccent);
        }
        else if (button == RestartButtonIndex)
        {
            DrawRestartGlyph(g, iconRect);
        }
        else if (button == BatteryCarePauseButtonIndex)
        {
            DrawBatteryCareGlyph(g, iconRect);
        }
        else if (button == BatteryLimitRestoreButtonIndex)
        {
            DrawBatteryLimitRestoreGlyph(g, iconRect);
        }
        else if (button == TaskManagerButtonIndex)
        {
            DrawTaskManagerGlyph(g, iconRect, palette.PrimaryAccent);
        }
        else if (button == WindowsAiStudioButtonIndex)
        {
            DrawWindowsAiStudioGlyph(g, iconRect, palette.PrimaryAccent, palette.PrimaryAccent);
        }
        else if (button == WindowsQuickSettingsButtonIndex)
        {
            DrawQuickSettingsGlyph(g, iconRect, palette.PrimaryAccent);
        }
        else if (button == LiveCaptionsButtonIndex)
        {
            DrawLiveCaptionsGlyph(g, iconRect);
        }
        else if (button == HoverOpacityToggleButtonIndex)
        {
            DrawHoverOpacityGlyph(g, iconRect, palette.PrimaryAccent);
        }

        if (unavailable)
        {
            DrawUnavailableButtonOverlay(g, rect, leftSegment, topRight, bottomRight);
        }
    }
}
