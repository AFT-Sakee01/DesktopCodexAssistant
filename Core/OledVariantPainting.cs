using System;
using System.Drawing;
using System.Drawing.Drawing2D;

// Shared low-level drawing primitives for the four OLED-safe restyle schemes added in 1.0.3.44:
// Typographic, AmberHud, WarmCard, Phosphor. Every window keeps its own data model and layout math
// in its own Core/<Window>.<Scheme>.cs partial; these helpers only standardize how a single
// (label, value, severity) item is painted so the resulting 24 window*scheme combinations don't
// reimplement identical shape/text drawing six times over. None of these allocate a Font or read
// per-form scale/settings state - callers pass an already-scaled Font from their own UiFontCache.
internal static class OledVariantPainting
{
    internal enum Severity
    {
        Neutral,
        Good,
        Warn,
        Danger
    }

    internal static Color PickSeverityColor(Severity severity, Color goodColor, Color warnColor, Color dangerColor, Color neutralColor)
    {
        if (severity == Severity.Good)
        {
            return goodColor;
        }

        if (severity == Severity.Warn)
        {
            return warnColor;
        }

        if (severity == Severity.Danger)
        {
            return dangerColor;
        }

        return neutralColor;
    }

    // Typographic scheme: big tabular value line over a small letter-spaced label line. No border, no fill.
    internal static void DrawStackedMetric(Graphics g, RectangleF rect, string value, string label, Color valueColor, Color labelColor, Font valueFont, Font labelFont)
    {
        using (SolidBrush valueBrush = new SolidBrush(valueColor))
        using (SolidBrush labelBrush = new SolidBrush(labelColor))
        using (StringFormat format = CreateCenterFormat())
        {
            float labelHeight = labelFont.Height;
            RectangleF valueRect = new RectangleF(rect.Left, rect.Top, rect.Width, rect.Height - labelHeight);
            RectangleF labelRect = new RectangleF(rect.Left, rect.Bottom - labelHeight, rect.Width, labelHeight);
            g.DrawString(value, valueFont, valueBrush, valueRect, format);
            g.DrawString(label, labelFont, labelBrush, labelRect, format);
        }
    }

    // Typographic scheme: thin vertical hairline separating stacked-metric columns.
    internal static void DrawHairlineSeparatorVertical(Graphics g, float x, float top, float bottom, Color color, float thickness)
    {
        using (Pen pen = new Pen(color, thickness))
        {
            g.DrawLine(pen, x, top, x, bottom);
        }
    }

    // AmberHud scheme: thin rectangular hairline chip with a mono/letter-spaced label. Fill is optional
    // and near-transparent; suppressDecorativeFill mirrors the existing burn-in hidden-mode convention.
    internal static void DrawHollowChip(Graphics g, RectangleF rect, string text, Color borderColor, Color textColor, Color fillColor, bool suppressDecorativeFill, Font font, float cornerRadius, float borderWidth)
    {
        using (Pen pen = new Pen(borderColor, borderWidth))
        using (SolidBrush fill = new SolidBrush(fillColor))
        using (SolidBrush textBrush = new SolidBrush(textColor))
        using (StringFormat format = CreateCenterFormat())
        using (GraphicsPath path = RoundedRectangle(rect, cornerRadius))
        {
            if (!suppressDecorativeFill && fillColor.A > 0)
            {
                g.FillPath(fill, path);
            }

            g.DrawPath(pen, path);
            g.DrawString(text, font, textBrush, rect, format);
        }
    }

    // WarmCard scheme: low-luminance filled rounded card, a small status dot, and left-aligned text. No border.
    internal static void DrawDotCard(Graphics g, RectangleF rect, string text, Color dotColor, Color textColor, Color cardFillColor, bool suppressDecorativeFill, Font font, float cornerRadius, float dotDiameter)
    {
        using (SolidBrush cardBrush = new SolidBrush(cardFillColor))
        using (SolidBrush dotBrush = new SolidBrush(dotColor))
        using (SolidBrush textBrush = new SolidBrush(textColor))
        using (GraphicsPath path = RoundedRectangle(rect, cornerRadius))
        {
            if (!suppressDecorativeFill)
            {
                g.FillPath(cardBrush, path);
            }

            float dotX = rect.Left + cornerRadius * 0.9f;
            float dotY = rect.Top + (rect.Height - dotDiameter) / 2.0f;
            g.FillEllipse(dotBrush, dotX, dotY, dotDiameter, dotDiameter);

            float textLeft = dotX + dotDiameter + cornerRadius * 0.6f;
            RectangleF textRect = new RectangleF(textLeft, rect.Top, rect.Right - textLeft - cornerRadius * 0.5f, rect.Height);
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(text, font, textBrush, textRect, format);
            }
        }
    }

    // WarmCard scheme, long-content variant: same low-luminance filled card and status dot as
    // DrawDotCard, but stacks a bold value line over a smaller muted label line instead of forcing
    // both onto one row - for cells whose value text (e.g. "0% 23:16") is too long to share a line
    // with its label at a readable size once the dot and card padding eat into the width.
    internal static void DrawDotCardStacked(Graphics g, RectangleF rect, string valueText, string labelText, Color dotColor, Color valueColor, Color labelColor, Color cardFillColor, bool suppressDecorativeFill, Font valueFont, Font labelFont, float cornerRadius, float dotDiameter)
    {
        using (SolidBrush cardBrush = new SolidBrush(cardFillColor))
        using (SolidBrush dotBrush = new SolidBrush(dotColor))
        using (SolidBrush valueBrush = new SolidBrush(valueColor))
        using (SolidBrush labelBrush = new SolidBrush(labelColor))
        using (GraphicsPath path = RoundedRectangle(rect, cornerRadius))
        {
            if (!suppressDecorativeFill)
            {
                g.FillPath(cardBrush, path);
            }

            float rowHeight = rect.Height / 2.0f;
            float dotX = rect.Left + cornerRadius * 0.9f;
            float dotY = rect.Top + (rowHeight - dotDiameter) / 2.0f;
            g.FillEllipse(dotBrush, dotX, dotY, dotDiameter, dotDiameter);

            float textLeft = dotX + dotDiameter + cornerRadius * 0.6f;
            RectangleF valueRect = new RectangleF(textLeft, rect.Top, rect.Right - textLeft - cornerRadius * 0.5f, rowHeight);
            RectangleF labelRect = new RectangleF(rect.Left + cornerRadius * 0.6f, rect.Top + rowHeight, rect.Width - cornerRadius * 1.2f, rowHeight);
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Near;
                format.LineAlignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                g.DrawString(valueText, valueFont, valueBrush, valueRect, format);
                g.DrawString(labelText, labelFont, labelBrush, labelRect, format);
            }
        }
    }

    // Phosphor scheme: borderless "prefix:value" terminal row. No shapes at all - the lowest-luminance
    // total-screen-area option of the four schemes.
    internal static void DrawTerminalRow(Graphics g, RectangleF rect, string prefix, string value, Color prefixColor, Color valueColor, Font font)
    {
        using (SolidBrush prefixBrush = new SolidBrush(prefixColor))
        using (SolidBrush valueBrush = new SolidBrush(valueColor))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.FormatFlags = StringFormatFlags.NoWrap;

            string prefixText = prefix + ":";
            SizeF prefixSize = g.MeasureString(prefixText, font);
            g.DrawString(prefixText, font, prefixBrush, new RectangleF(rect.Left, rect.Top, prefixSize.Width, rect.Height), format);

            float valueLeft = rect.Left + prefixSize.Width + 2.0f;
            RectangleF valueRect = new RectangleF(valueLeft, rect.Top, rect.Right - valueLeft, rect.Height);
            format.Trimming = StringTrimming.EllipsisCharacter;
            g.DrawString(value, font, valueBrush, valueRect, format);
        }
    }

    // Shrinks from startSize down to minSize (in 1px steps) until text fits maxWidth, so short Chinese
    // labels squeezed into a narrow column (e.g. a 3-way Typographic split) don't get ellipsis-trimmed.
    // Measures with the given font family/style directly rather than going through a form's UiFontCache,
    // since the caller only wants the fitted size back and will request the final Font from its own cache.
    internal static float FitFontSize(Graphics g, string text, string fontFamily, FontStyle style, float startSize, float minSize, float maxWidth)
    {
        float size = Math.Max(minSize, startSize);
        while (size > minSize)
        {
            using (Font probe = new Font(fontFamily, size, style, GraphicsUnit.Pixel))
            {
                if (g.MeasureString(text, probe).Width <= maxWidth)
                {
                    return size;
                }
            }

            size -= 1.0f;
        }

        return minSize;
    }

    internal static StringFormat CreateCenterFormat()
    {
        StringFormat format = new StringFormat();
        format.Alignment = StringAlignment.Center;
        format.LineAlignment = StringAlignment.Center;
        format.Trimming = StringTrimming.EllipsisCharacter;
        format.FormatFlags = StringFormatFlags.NoWrap;
        return format;
    }

    internal static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = radius * 2.0f;
        if (diameter <= 0.01f || diameter > bounds.Width || diameter > bounds.Height)
        {
            path.AddRectangle(bounds);
            return path;
        }

        RectangleF arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
