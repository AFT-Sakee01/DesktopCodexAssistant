using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

internal enum BurnInVisualLevel
{
    Normal = 0,
    LevelOne = 1,
    LevelTwo = 2
}

internal static class BurnInProtection
{
    // Level one lowers the whole right-side presentation enough to materially reduce OLED load while
    // retaining legibility. Hover restoration is handled by the right-column owner as one group.
    public const int LevelOneLuminancePercent = 45;
    public const int MainWidgetSalt = 1;
    public const int CodexRadarSalt = 7;
    public const int PowerThermalSalt = 13;
    public const int NetworkMonitorSalt = 19;
    public const int ConnectionCheckSalt = 23;
    public const int OperationPanelSalt = 29;
    public const int SpecBoardSalt = 73;
    public const int SpecBoardDockTabSalt = 37;
    public const int CodexTaskBoardSalt = 41;
    public const int CodexTaskBoardDockTabSalt = 43;
    public const int NetworkMonitorDockTabSalt = 47;
    public const int GuardBoardSalt = 53;
    public const int GuardBoardDockTabSalt = 59;
    public const int MetricTileColumnSalt = 61;
    public const int MetricTileExpandSalt = 67;
    public const int CodexIqBoardSalt = 71;
    public const int CodexIqBoardDockTabSalt = 79;
    public const int LeftDockButtonColumnSalt = 83;

    private const int ShiftIntervalMinutes = 7;
    private static int currentVisualLevel;
    private static readonly Point[] RuntimeOffsets = new Point[]
    {
        new Point(1, 0),
        new Point(2, 0),
        new Point(2, 1),
        new Point(1, 2),
        new Point(0, 2),
        new Point(-1, 2),
        new Point(-2, 1),
        new Point(-2, 0),
        new Point(-2, -1),
        new Point(-1, -2),
        new Point(0, -2),
        new Point(1, -2),
        new Point(2, -1),
        new Point(3, 0),
        new Point(0, 3),
        new Point(-3, 0),
        new Point(0, -3)
    };

    public static bool ShouldRefreshPosition(ref long lastSlot)
    {
        long slot = GetCurrentSlot();
        if (slot == lastSlot)
        {
            return false;
        }

        lastSlot = slot;
        return true;
    }

    public static BurnInVisualLevel CurrentVisualLevel
    {
        get { return (BurnInVisualLevel)Volatile.Read(ref currentVisualLevel); }
    }

    public static bool SetCurrentVisualLevel(BurnInVisualLevel level)
    {
        int normalized = (int)NormalizeVisualLevel(level);
        return Interlocked.Exchange(ref currentVisualLevel, normalized) != normalized;
    }

    public static BurnInVisualLevel ResolveVisualLevel(
        TimeSpan idleDuration,
        bool enabled,
        int levelOneIdleSeconds,
        int levelTwoDelaySeconds)
    {
        if (!enabled)
        {
            return BurnInVisualLevel.Normal;
        }

        int levelOneSeconds = Math.Max(
            WidgetSettings.MinBurnInLevelOneIdleSeconds,
            Math.Min(WidgetSettings.MaxBurnInLevelOneIdleSeconds, levelOneIdleSeconds));
        int levelTwoSeconds = Math.Max(
            WidgetSettings.MinBurnInLevelTwoDelaySeconds,
            Math.Min(WidgetSettings.MaxBurnInLevelTwoDelaySeconds, levelTwoDelaySeconds));
        double idleSeconds = Math.Max(0.0, idleDuration.TotalSeconds);
        if (idleSeconds >= levelOneSeconds + levelTwoSeconds)
        {
            return BurnInVisualLevel.LevelTwo;
        }

        return idleSeconds >= levelOneSeconds
            ? BurnInVisualLevel.LevelOne
            : BurnInVisualLevel.Normal;
    }

    public static Color InvertColor(Color color)
    {
        return Color.FromArgb(color.A, 255 - color.R, 255 - color.G, 255 - color.B);
    }

    public static bool ShouldResetActivityTimer(
        BurnInVisualLevel currentLevel,
        bool pointerMoved,
        bool mouseButtonDown,
        bool systemInputChanged)
    {
        if (!pointerMoved && !mouseButtonDown && !systemInputChanged)
        {
            return false;
        }

        // Motion is a localized reveal only after protection starts. Button, wheel and keyboard
        // input still represent deliberate activity and restart both protection thresholds.
        return NormalizeVisualLevel(currentLevel) == BurnInVisualLevel.Normal ||
            mouseButtonDown ||
            (systemInputChanged && !pointerMoved);
    }

    public static BurnInVisualLevel NormalizeVisualLevel(BurnInVisualLevel level)
    {
        if (level < BurnInVisualLevel.Normal)
        {
            return BurnInVisualLevel.Normal;
        }

        if (level > BurnInVisualLevel.LevelTwo)
        {
            return BurnInVisualLevel.LevelTwo;
        }

        return level;
    }

    public static Point ApplyRuntimeOffset(Point baseLocation, Size windowSize, Rectangle workArea, int salt)
    {
        if (windowSize.Width <= 0 || windowSize.Height <= 0 || RuntimeOffsets.Length == 0)
        {
            return baseLocation;
        }

        long slot = GetCurrentSlot();
        int index = (int)((slot + salt) % RuntimeOffsets.Length);
        if (index < 0)
        {
            index += RuntimeOffsets.Length;
        }

        Point offset = RuntimeOffsets[index];
        int left = Clamp(baseLocation.X + offset.X, workArea.Left, Math.Max(workArea.Left, workArea.Right - windowSize.Width));
        int top = Clamp(baseLocation.Y + offset.Y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - windowSize.Height));
        return new Point(left, top);
    }

    public static Point ApplyRuntimeOffsetWithPinnedX(Point baseLocation, Size windowSize, Rectangle workArea, int salt)
    {
        Point runtimeLocation = ApplyRuntimeOffset(baseLocation, windowSize, workArea, salt);
        int maximumLeft = Math.Max(workArea.Left, workArea.Right - windowSize.Width);
        int pinnedLeft = Clamp(baseLocation.X, workArea.Left, maximumLeft);
        // Left-docked boards deliberately keep their shared horizontal anchor. Independent salts may
        // still move them vertically, but X drift would make the four expanded surfaces visibly split.
        return new Point(pinnedLeft, runtimeLocation.Y);
    }

    public static void ApplyLuminance(Bitmap bitmap, int luminancePercent)
    {
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return;
        }

        int percent = Clamp(luminancePercent, 0, 100);
        if (percent >= 100)
        {
            return;
        }

        Rectangle bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            int byteCount = stride * data.Height;
            byte[] pixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);
            for (int y = 0; y < data.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < data.Width; x++)
                {
                    int index = row + x * 4;
                    pixels[index] = (byte)Clamp((int)Math.Round(pixels[index] * percent / 100.0), 0, 255);
                    pixels[index + 1] = (byte)Clamp((int)Math.Round(pixels[index + 1] * percent / 100.0), 0, 255);
                    pixels[index + 2] = (byte)Clamp((int)Math.Round(pixels[index + 2] * percent / 100.0), 0, 255);
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static long GetCurrentSlot()
    {
        return DateTime.UtcNow.Ticks / (TimeSpan.TicksPerMinute * ShiftIntervalMinutes);
    }

    private static int Clamp(int value, int minimum, int maximum)
    {
        if (value < minimum)
        {
            return minimum;
        }

        if (value > maximum)
        {
            return maximum;
        }

        return value;
    }

    internal static void RunSelfTest()
    {
        int levelOneSeconds = WidgetSettings.DefaultBurnInLevelOneIdleSeconds;
        int levelTwoSeconds = WidgetSettings.DefaultBurnInLevelTwoDelaySeconds;
        if (ResolveVisualLevel(TimeSpan.FromSeconds(levelOneSeconds - 0.01), true, levelOneSeconds, levelTwoSeconds) != BurnInVisualLevel.Normal ||
            ResolveVisualLevel(TimeSpan.FromSeconds(levelOneSeconds), true, levelOneSeconds, levelTwoSeconds) != BurnInVisualLevel.LevelOne ||
            ResolveVisualLevel(TimeSpan.FromSeconds(levelOneSeconds + levelTwoSeconds - 0.01), true, levelOneSeconds, levelTwoSeconds) != BurnInVisualLevel.LevelOne ||
            ResolveVisualLevel(TimeSpan.FromSeconds(levelOneSeconds + levelTwoSeconds), true, levelOneSeconds, levelTwoSeconds) != BurnInVisualLevel.LevelTwo ||
            ResolveVisualLevel(TimeSpan.FromHours(1), false, levelOneSeconds, levelTwoSeconds) != BurnInVisualLevel.Normal)
        {
            throw new InvalidOperationException("Two-level burn-in idle thresholds are not deterministic.");
        }

        Color sample = Color.FromArgb(91, 12, 34, 56);
        Color inverted = InvertColor(sample);
        if (inverted.A != 91 || inverted.R != 243 || inverted.G != 221 || inverted.B != 199 ||
            InvertColor(inverted).ToArgb() != sample.ToArgb())
        {
            throw new InvalidOperationException("Burn-in accent inversion must preserve alpha and be reversible.");
        }

        if (!ShouldResetActivityTimer(BurnInVisualLevel.Normal, true, false, true) ||
            ShouldResetActivityTimer(BurnInVisualLevel.LevelOne, true, false, true) ||
            !ShouldResetActivityTimer(BurnInVisualLevel.LevelOne, false, false, true) ||
            !ShouldResetActivityTimer(BurnInVisualLevel.LevelTwo, true, true, true) ||
            ShouldResetActivityTimer(BurnInVisualLevel.LevelTwo, false, false, false))
        {
            throw new InvalidOperationException("Burn-in activity reset and localized hover policy is not deterministic.");
        }

        SetCurrentVisualLevel(BurnInVisualLevel.LevelTwo);
        if (CurrentVisualLevel != BurnInVisualLevel.LevelTwo)
        {
            throw new InvalidOperationException("Burn-in visual level publication failed.");
        }

        SetCurrentVisualLevel(BurnInVisualLevel.Normal);
        Console.WriteLine("Burn-in protection: PASS level1=10s level2=+30s luminance=45 inversion=accent-only");
    }

}
