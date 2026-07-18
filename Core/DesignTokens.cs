using System;
using System.Drawing;

internal static class DesignTokens
{
    public const string UiFontFamily = "Microsoft YaHei UI";
    public const string MonoFontFamily = "Consolas";
    private static readonly Color BurnInWhite = Color.FromArgb(238, 241, 244);

    public static class Colors
    {
        public static readonly Color AppBackground = Color.FromArgb(18, 19, 22);
        public static readonly Color Surface = Color.FromArgb(26, 31, 36);
        public static readonly Color NotifyIconSurface = Color.FromArgb(25, 27, 32);
        public static readonly Color ThermalChipSurface = Color.FromArgb(28, 28, 30);
        public static readonly Color PreviewBack = Color.FromArgb(33, 33, 36);
        public static readonly Color PreviewShell = Color.FromArgb(34, 34, 38);
        public static readonly Color PreviewCard = Color.FromArgb(8, 8, 10);
        public static readonly Color PreviewCardHover = Color.FromArgb(58, 58, 64);
        public static readonly Color PreviewHeader = Color.FromArgb(12, 12, 15);
        public static readonly Color PreviewHeaderHover = Color.FromArgb(74, 74, 82);
        public static readonly Color Window = Color.FromArgb(32, 32, 32);
        public static readonly Color Control = Color.FromArgb(45, 45, 48);
        public static readonly Color ControlActive = Color.FromArgb(58, 58, 62);
        public static readonly Color ControlPressed = Color.FromArgb(54, 54, 58);
        public static readonly Color Border = Color.FromArgb(72, 72, 78);
        public static readonly Color Badge = Color.FromArgb(154, 160, 170);
        public static readonly Color Text = Color.FromArgb(232, 235, 238);
        public static readonly Color TextStrong = Color.FromArgb(236, 240, 242);
        public static readonly Color TextMuted = Color.FromArgb(202, 211, 218);
        public static readonly Color SubtleText = Color.FromArgb(188, 194, 198);
        public static readonly Color Glyph = Color.FromArgb(232, 236, 239);
        public static readonly Color GlyphMuted = Color.FromArgb(128, 135, 144);
        public static readonly Color TextOnAccent = Color.Black;
        public static readonly Color TextOnDanger = Color.White;
        public static readonly Color Accent = Color.FromArgb(82, 211, 255);
        public static readonly Color AccentSoft = Color.FromArgb(64, 171, 255);
        public static readonly Color AccentAction = Color.FromArgb(58, 148, 255);
        public static readonly Color AccentDeep = Color.FromArgb(38, 145, 238);
        public static readonly Color AccentBorder = Color.FromArgb(144, 214, 255);
        public static readonly Color AccentGradientStart = Color.FromArgb(32, 189, 255);
        public static readonly Color AccentGradientEnd = Color.FromArgb(68, 126, 255);
        public static readonly Color AccentAlt = Color.FromArgb(226, 126, 255);
        public static readonly Color SpeedWindowCountdown = Color.FromArgb(56, 189, 248);
        // Sky blue for the "quota reset detected" full ring (replaced the old celebratory rainbow,
        // which is now reserved for a sub-day reset-credit indicator).
        public static readonly Color QuotaResetSkyBlue = Color.FromArgb(56, 189, 248);
        public static readonly Color Success = Color.FromArgb(134, 238, 100);
        public static readonly Color SuccessSoft = Color.FromArgb(95, 218, 132);
        public static readonly Color SuccessText = Color.FromArgb(170, 238, 190);
        public static readonly Color QuotaGood = Color.FromArgb(76, 214, 116);
        public static readonly Color Warning = Color.FromArgb(255, 226, 92);
        public static readonly Color WarningSoft = Color.FromArgb(255, 226, 92);
        public static readonly Color WarningDeep = Color.FromArgb(226, 117, 49);
        public static readonly Color Danger = Color.FromArgb(255, 92, 104);
        public static readonly Color DangerText = Color.FromArgb(255, 178, 186);
        public static readonly Color DangerBorder = Color.FromArgb(255, 132, 142);
        public static readonly Color DangerStrong = Color.FromArgb(255, 44, 58);
        public static readonly Color DangerClose = Color.FromArgb(232, 72, 74);
        public static readonly Color DangerDeep = Color.FromArgb(218, 34, 52);
        public static readonly Color DangerMuted = Color.FromArgb(225, 48, 65);
        public static readonly Color DangerGlyph = Color.FromArgb(255, 72, 86);
        public static readonly Color QuotaDanger = Color.FromArgb(126, 18, 28);
        public static readonly Color MediaGradientStart = Color.FromArgb(33, 43, 60);
        public static readonly Color MediaGradientEnd = Color.FromArgb(69, 148, 203);
        public static readonly Color FallbackIconGradientStart = Color.FromArgb(76, 191, 255);
        public static readonly Color FallbackIconGradientEnd = Color.FromArgb(122, 236, 177);
    }

    public static class Alpha
    {
        public const int ShellOutline = 76;
        public const int StrongText = 232;
        public const int MutedText = 204;
        public const int Glyph = 224;
        public const int WeakOutline = 52;
        public const int HoverGlass = 66;
        public const int RestGlass = 42;
        public const int TileShadow = 72;
    }

    public static class Radius
    {
        public const int Widget = 13;
        public const int Panel = 12;
        public const int SettingsCard = 8;
        public const int Small = 5;
    }

    public static class Spacing
    {
        public const int SettingsPagePadding = 16;
        public const int SettingsOuterPadding = 24;
        public const int SettingsCardPaddingX = 40;
        public const int SettingsCardPaddingY = 28;
        public const int SettingsCardGap = 10;
        public const int SettingsButtonGap = 10;
        public const int SettingsCheckGap = 8;
    }

    public static class Sizes
    {
        public const int SettingsButtonWidth = 96;
        public const int SettingsPrimaryButtonWidth = 118;
        public const int SettingsButtonHeight = 34;
        public const int SettingsToggleHeight = 32;
    }

    public static class SettingsTheme
    {
        public static readonly Color MicaBase = Color.FromArgb(32, 32, 32);
        public static readonly Color MicaLayer = Color.FromArgb(43, 43, 43);
        public static readonly Color CardRest = Color.FromArgb(45, 45, 45);
        public static readonly Color CardHover = Color.FromArgb(56, 56, 56);
        public static readonly Color Stroke = Color.FromArgb(18, 255, 255, 255);
        public static readonly Color DividerStroke = Color.FromArgb(15, 255, 255, 255);
        public static readonly Color ControlBg = Color.FromArgb(55, 55, 55);
        public static readonly Color ControlBorder = Color.FromArgb(72, 72, 72);
        public static readonly Color TextPrimary = Color.FromArgb(246, 246, 246);
        public static readonly Color TextSecondary = Color.FromArgb(196, 196, 196);
        public static readonly Color TextTertiary = Color.FromArgb(154, 154, 154);
        public static readonly Color AccentColor = Color.FromArgb(96, 205, 255);
        public static readonly Color AccentHover = Color.FromArgb(118, 218, 255);
        public static readonly Color AccentPressed = Color.FromArgb(62, 154, 214);
        public static readonly Color ErrorText = Color.FromArgb(255, 128, 138);
        public static readonly Color ToggleTrackOff = Color.FromArgb(88, 88, 88);
        public static readonly Color ToggleTrackOffHover = Color.FromArgb(100, 100, 100);
        public static readonly Color ToggleKnob = Color.FromArgb(255, 255, 255);
        public static readonly Color NavSelectedBg = Color.FromArgb(20, 96, 205, 255);
        public static readonly Color NavHoverBg = Color.FromArgb(40, 40, 40);
        public static readonly Color ButtonRest = Color.FromArgb(50, 50, 50);
        public static readonly Color ButtonHover = Color.FromArgb(58, 58, 58);
        public static readonly Color ButtonPressed = Color.FromArgb(42, 42, 42);
    }

    // Settings-window theme: aligns the settings UI with the actual floating-window scheme. The
    // theme is near-black (Colors.AppBackground) with
    // red/yellow/green (Colors.Danger/Warning/Success) as the only accent hues — no blue, no amber.
    // Neutral fills/dividers are computed with the same white-alpha-over-background blend the
    // layered windows use for their own card fills (DesignTokens.White(alpha) over AppBackground),
    // just pre-blended to an opaque RGB since this window is a normal (non-layered) form. Text and
    // accent colors are literal Colors.* values, not re-picked hex, so this theme tracks the real
    // widget palette if it ever changes.
    public static class SettingsWarmTheme
    {
        public static readonly Color WindowBase = Colors.AppBackground;
        public static readonly Color InputBackground = Color.FromArgb(31, 32, 35);
        public static readonly Color CardRest = Color.FromArgb(27, 28, 31);
        public static readonly Color CardHover = Color.FromArgb(37, 38, 40);
        public static readonly Color DividerLines = Color.FromArgb(44, 45, 48);
        public static readonly Color TextPrimary = Colors.TextStrong;
        public static readonly Color TextSecondary = Colors.TextMuted;
        public static readonly Color TextMuted = Colors.GlyphMuted;
        public static readonly Color Accent = Colors.Success;
        public static readonly Color AccentHover = Color.FromArgb(152, 241, 123);
        public static readonly Color AccentPressed = Color.FromArgb(107, 190, 80);
        public static readonly Color NavSelectedBg = Color.FromArgb(30, 134, 238, 100);
        public static readonly Color NavHoverBg = Color.FromArgb(37, 38, 40);
        public static readonly Color ToggleTrackOff = Color.FromArgb(40, 41, 44);
        public static readonly Color ToggleTrackHover = Color.FromArgb(50, 50, 53);
        public static readonly Color ToggleKnob = Colors.Glyph;
        public static readonly Color ButtonRest = Color.FromArgb(35, 36, 38);
        public static readonly Color ButtonHover = Color.FromArgb(46, 47, 49);
        public static readonly Color ButtonPressed = Color.FromArgb(25, 26, 29);
        public static readonly Color ErrorText = Colors.Danger;
    }

    public static class NeonGeekTheme
    {
        public static readonly Color WindowBase = Color.FromArgb(12, 12, 14);
        public static readonly Color CardGlassRest = Color.FromArgb(20, 20, 24);
        public static readonly Color CardGlassHover = Color.FromArgb(30, 30, 35);
        public static readonly Color CardGlowBorder = Color.FromArgb(50, 50, 60);
        public static readonly Color NavPillRest = Color.Transparent;
        public static readonly Color NavPillHover = Color.FromArgb(30, 255, 255, 255);
        public static readonly Color TextPrimary = Color.FromArgb(255, 255, 255);
        public static readonly Color TextSecondary = Color.FromArgb(160, 160, 175);
        public static readonly Color TextMuted = Color.FromArgb(100, 100, 115);
        public static readonly Color CyberCyan = Color.FromArgb(0, 240, 255);
        public static readonly Color ElectricPurple = Color.FromArgb(178, 31, 255);
        public static readonly Color ToggleTrackOff = Color.FromArgb(40, 40, 45);
        public static readonly Color ToggleTrackHover = Color.FromArgb(60, 60, 68);
        public static readonly Color DividerLines = Color.FromArgb(35, 35, 45);
        public static readonly Color InputBackground = Color.FromArgb(18, 18, 22);
    }

    public static Font CreateUIFont(float size, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
    {
        return new Font(UiFontFamily, size, style, unit);
    }

    public static Font CreateMonoFont(float size, FontStyle style = FontStyle.Regular, GraphicsUnit unit = GraphicsUnit.Point)
    {
        return new Font(MonoFontFamily, size, style, unit);
    }

    public static Color White(int alpha)
    {
        return WithAlpha(BurnInWhite, Math.Min(alpha, 232));
    }

    public static Color Black(int alpha)
    {
        return WithAlpha(Color.Black, alpha);
    }

    public static Color Text(int alpha)
    {
        return WithAlpha(Colors.Text, alpha);
    }

    public static Color TextStrong(int alpha)
    {
        return WithAlpha(Colors.TextStrong, alpha);
    }

    public static Color TextMuted(int alpha)
    {
        return WithAlpha(Colors.TextMuted, alpha);
    }

    public static Color Glyph(int alpha)
    {
        return WithAlpha(Colors.Glyph, alpha);
    }

    public static Color Accent(int alpha)
    {
        return WithAlpha(Colors.Accent, alpha);
    }

    public static Color AccentAlt(int alpha)
    {
        return WithAlpha(Colors.AccentAlt, alpha);
    }

    public static Color Success(int alpha)
    {
        return WithAlpha(Colors.Success, alpha);
    }

    public static Color Warning(int alpha)
    {
        return WithAlpha(Colors.Warning, alpha);
    }

    public static Color Danger(int alpha)
    {
        return WithAlpha(Colors.Danger, alpha);
    }

    public static Color DangerStrong(int alpha)
    {
        return WithAlpha(Colors.DangerStrong, alpha);
    }

    public static Color WithAlpha(Color color, int alpha)
    {
        return Color.FromArgb(ClampByte(alpha), color.R, color.G, color.B);
    }

    public static int ClampByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 255)
        {
            return 255;
        }

        return value;
    }
}
