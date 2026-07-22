using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

internal static class BurnInProtection
{
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

}
