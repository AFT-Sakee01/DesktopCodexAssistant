using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal static class ApplicationIcon
{
    public static Icon CreateIcon()
    {
        return CreateIcon(32);
    }

    public static Icon CreateIcon(int size)
    {
        int iconSize = Math.Max(16, size);
        using (Bitmap bitmap = new Bitmap(iconSize, iconSize))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            DrawIcon(g, new RectangleF(0.0f, 0.0f, iconSize, iconSize));
            IntPtr handle = bitmap.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(handle).Clone();
            }
            finally
            {
                NativeMethods.DestroyIcon(handle);
            }
        }
    }

    public static void ApplyTo(Form form)
    {
        if (form == null)
        {
            return;
        }

        form.Icon = CreateIcon();
    }

    public static void DrawIcon(Graphics g, RectangleF bounds)
    {
        if (g == null)
        {
            return;
        }

        GraphicsState state = g.Save();
        try
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float unit = Math.Min(bounds.Width, bounds.Height) / 32.0f;
            float left = bounds.Left + (bounds.Width - unit * 32.0f) / 2.0f;
            float top = bounds.Top + (bounds.Height - unit * 32.0f) / 2.0f;
            RectangleF circle = new RectangleF(left + unit * 2.0f, top + unit * 2.0f, unit * 28.0f, unit * 28.0f);

            using (SolidBrush background = new SolidBrush(DesignTokens.Colors.NotifyIconSurface))
            using (Pen border = new Pen(DesignTokens.White(120), Math.Max(1.0f, unit * 2.0f)))
            {
                g.FillEllipse(background, circle);
                g.DrawEllipse(border, circle);
            }

            using (Pen cpu = CreateRoundedPen(DesignTokens.Colors.Accent, unit * 3.0f))
            using (Pen memory = CreateRoundedPen(DesignTokens.Colors.AccentAlt, unit * 3.0f))
            using (Pen disk = CreateRoundedPen(DesignTokens.Colors.Success, unit * 3.0f))
            {
                g.DrawLine(cpu, left + unit * 9.0f, top + unit * 21.0f, left + unit * 9.0f, top + unit * 12.0f);
                g.DrawLine(memory, left + unit * 16.0f, top + unit * 21.0f, left + unit * 16.0f, top + unit * 8.0f);
                g.DrawLine(disk, left + unit * 23.0f, top + unit * 21.0f, left + unit * 23.0f, top + unit * 15.0f);
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    private static Pen CreateRoundedPen(Color color, float width)
    {
        Pen pen = new Pen(color, Math.Max(1.0f, width));
        pen.StartCap = LineCap.Round;
        pen.EndCap = LineCap.Round;
        pen.LineJoin = LineJoin.Round;
        return pen;
    }
}
