using System;
using System.Drawing;

internal static class BurnInProtection
{
    public const int MainWidgetSalt = 1;
    public const int CodexRadarSalt = 7;
    public const int PowerThermalSalt = 13;
    public const int NetworkMonitorSalt = 19;
    public const int ConnectionCheckSalt = 23;
    public const int OperationPanelSalt = 29;

    private const int ShiftIntervalMinutes = 7;
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
}
