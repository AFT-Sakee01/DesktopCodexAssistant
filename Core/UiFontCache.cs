using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

/// <summary>
/// Owns layout-dependent UI fonts so frequently redrawn layered windows do not
/// allocate identical GDI font handles on every frame.
/// </summary>
internal sealed class UiFontCache : IDisposable
{
    private readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>(StringComparer.Ordinal);

    public Font GetUi(float size, FontStyle style)
    {
        return Get(size, style, false, GraphicsUnit.Pixel);
    }

    public Font GetMono(float size, FontStyle style)
    {
        return Get(size, style, true, GraphicsUnit.Pixel);
    }

    // WinForms controls use point-sized fonts; layered-window drawing keeps using pixel-sized GetUi/GetMono.
    public Font GetUiPoint(float size, FontStyle style)
    {
        return Get(size, style, false, GraphicsUnit.Point);
    }

    private Font Get(float size, FontStyle style, bool mono, GraphicsUnit unit)
    {
        float normalizedSize = (float)Math.Round(Math.Max(1.0f, size), 2);
        string key =
            (mono ? "M|" : "U|") +
            ((int)unit).ToString(CultureInfo.InvariantCulture) +
            "|" +
            normalizedSize.ToString("0.00", CultureInfo.InvariantCulture) +
            "|" +
            ((int)style).ToString(CultureInfo.InvariantCulture);
        Font font;
        if (!this.fonts.TryGetValue(key, out font))
        {
            font = mono
                ? DesignTokens.CreateMonoFont(normalizedSize, style, unit)
                : DesignTokens.CreateUIFont(normalizedSize, style, unit);
            this.fonts[key] = font;
        }

        return font;
    }

    public void Dispose()
    {
        foreach (Font font in this.fonts.Values)
        {
            font.Dispose();
        }

        this.fonts.Clear();
    }
}
