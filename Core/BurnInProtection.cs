using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

internal static class BurnInProtection
{
    public const int MainWidgetSalt = 1;
    public const int CodexRadarSalt = 7;
    public const int PowerThermalSalt = 13;
    public const int NetworkMonitorSalt = 19;
    public const int ConnectionCheckSalt = 23;
    public const int OperationPanelSalt = 29;

    private const int ShiftIntervalMinutes = 7;
    private const int GrayscaleTolerance = 18;
    private const int TextFringeCleanupPasses = 2;
    private const int TextFringeMinBrightness = 96;
    private const int TextFringeMinLuminance = 118;
    private const int TextFringeMinChannel = 54;
    private const int TextFringeMaxChroma = 104;
    private const int TextFringeMaxSaturationPercent = 42;
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

    public static bool ShouldApplyHiddenModeColorProtection(WidgetSettings settings, bool hiddenOpacityActive)
    {
        return settings != null &&
            settings.BurnInHiddenModeColorProtectionEnabled &&
            hiddenOpacityActive;
    }

    public static void ApplyHiddenModeColorProtection(Bitmap bitmap)
    {
        if (bitmap == null || bitmap.Width <= 0 || bitmap.Height <= 0)
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
            bool[] transparentMask = new bool[data.Width * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, byteCount);

            for (int y = 0; y < data.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < data.Width; x++)
                {
                    int index = row + x * 4;
                    byte premultipliedBlue = pixels[index];
                    byte premultipliedGreen = pixels[index + 1];
                    byte premultipliedRed = pixels[index + 2];
                    byte alpha = pixels[index + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    int red = Unpremultiply(premultipliedRed, alpha);
                    int green = Unpremultiply(premultipliedGreen, alpha);
                    int blue = Unpremultiply(premultipliedBlue, alpha);
                    if (IsGrayscale(red, green, blue))
                    {
                        transparentMask[y * data.Width + x] = true;
                    }
                }
            }

            for (int pass = 0; pass < TextFringeCleanupPasses; pass++)
            {
                bool changed = false;
                bool[] nextMask = (bool[])transparentMask.Clone();
                for (int y = 0; y < data.Height; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < data.Width; x++)
                    {
                        int maskIndex = y * data.Width + x;
                        if (transparentMask[maskIndex] ||
                            !HasTransparentNeighbor(transparentMask, data.Width, data.Height, x, y))
                        {
                            continue;
                        }

                        int index = row + x * 4;
                        byte alpha = pixels[index + 3];
                        if (alpha == 0)
                        {
                            continue;
                        }

                        int red = Unpremultiply(pixels[index + 2], alpha);
                        int green = Unpremultiply(pixels[index + 1], alpha);
                        int blue = Unpremultiply(pixels[index], alpha);
                        if (IsLikelyTextFringe(red, green, blue))
                        {
                            nextMask[maskIndex] = true;
                            changed = true;
                        }
                    }
                }

                transparentMask = nextMask;
                if (!changed)
                {
                    break;
                }
            }

            for (int y = 0; y < data.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < data.Width; x++)
                {
                    int index = row + x * 4;
                    byte alpha = pixels[index + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    int red = Unpremultiply(pixels[index + 2], alpha);
                    int green = Unpremultiply(pixels[index + 1], alpha);
                    int blue = Unpremultiply(pixels[index], alpha);
                    if (transparentMask[y * data.Width + x])
                    {
                        pixels[index] = 0;
                        pixels[index + 1] = 0;
                        pixels[index + 2] = 0;
                        pixels[index + 3] = 0;
                        continue;
                    }

                    pixels[index] = Premultiply(255 - blue, alpha);
                    pixels[index + 1] = Premultiply(255 - green, alpha);
                    pixels[index + 2] = Premultiply(255 - red, alpha);
                }
            }

            Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    public static void ConfigureGraphics(Graphics graphics, bool hiddenModeColorProtectionActive)
    {
        if (graphics == null)
        {
            return;
        }

        if (hiddenModeColorProtectionActive)
        {
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
            graphics.PixelOffsetMode = PixelOffsetMode.None;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            return;
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.PixelOffsetMode = PixelOffsetMode.Default;
        graphics.InterpolationMode = InterpolationMode.Bilinear;
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

    private static int Unpremultiply(byte value, byte alpha)
    {
        if (alpha == 255)
        {
            return value;
        }

        return Clamp((int)Math.Round(value * 255.0 / Math.Max(1, (int)alpha)), 0, 255);
    }

    private static byte Premultiply(int value, byte alpha)
    {
        return (byte)Clamp((int)Math.Round(Clamp(value, 0, 255) * alpha / 255.0), 0, 255);
    }

    private static bool IsGrayscale(int red, int green, int blue)
    {
        int max = Math.Max(red, Math.Max(green, blue));
        int min = Math.Min(red, Math.Min(green, blue));
        return max - min <= GrayscaleTolerance;
    }

    private static bool IsLikelyTextFringe(int red, int green, int blue)
    {
        int max = Math.Max(red, Math.Max(green, blue));
        int min = Math.Min(red, Math.Min(green, blue));
        int chroma = max - min;
        if (max < TextFringeMinBrightness ||
            min < TextFringeMinChannel ||
            GetLuminance(red, green, blue) < TextFringeMinLuminance)
        {
            return false;
        }

        return chroma <= TextFringeMaxChroma &&
            chroma * 100 <= Math.Max(1, max) * TextFringeMaxSaturationPercent;
    }

    private static int GetLuminance(int red, int green, int blue)
    {
        return (red * 299 + green * 587 + blue * 114) / 1000;
    }

    private static bool HasTransparentNeighbor(bool[] transparentMask, int width, int height, int x, int y)
    {
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = y + dy;
            if (ny < 0 || ny >= height)
            {
                continue;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                int nx = x + dx;
                if (nx >= 0 && nx < width && transparentMask[ny * width + nx])
                {
                    return true;
                }
            }
        }

        return false;
    }
}
