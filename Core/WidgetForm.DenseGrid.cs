using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// Dense grid layout for the main widget (1.0.5.69). Replaces the previous per-metric "graph box on
// the left, text on the right" cells: the graph box cost ~40% of each cell's width to draw a single
// line and squeezed every label into an ellipsis. Here each cell is text-first — headline value,
// utilisation gauge, two sub-values — with the trend reduced to a faint full-cell watermark, so the
// same 2-column grid carries roughly twice the readable data points at a larger type size.
//
// Cells follow MetricOrder and the per-metric Show* flags exactly like the old layout did; the grid
// shape adapts to how many metrics are enabled rather than assuming six.
internal sealed partial class WidgetForm
{
    private const int DenseWatermarkFillAlpha = 16;

    private sealed class DenseCell
    {
        public string Label;
        public double Percent;     // <0 means the metric has no meaningful percentage (network)
        public string Value;
        public string Sub1;
        public string Sub2;
        public Color Accent;
        public List<double> History;
        public bool Disconnected;
        public double AlertPercent;
        public bool AlertIconVisible;
        // CPU only: per-core utilisation. When present the cell swaps its gauge and second
        // sub-value for the per-core bar chart.
        public double[] Cores;
        // Disk / network: a headline figure shown large and grey to the right of the two
        // sub-value lines (capacity, link type).
        public string Corner;
        // Memory: share of total memory held by GPU + NPU, drawn as a segment on the gauge.
        public double SecondaryPercent;
    }

    private List<DenseCell> BuildDenseCells()
    {
        List<DenseCell> cells = new List<DenseCell>();
        string[] order = this.CurrentSettings.MetricOrder ?? WidgetSettings.DefaultMetricOrder;
        for (int i = 0; i < order.Length; i++)
        {
            AddDenseCell(cells, order[i]);
        }

        return cells;
    }

    private void AddDenseCell(List<DenseCell> cells, string metricId)
    {
        if (string.Equals(metricId, WidgetSettings.MetricCpu, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowCpu)
        {
            DenseCell cell = new DenseCell
            {
                Label = "CPU",
                Percent = this.snapshot.CpuPercent,
                Value = string.Format(CultureInfo.InvariantCulture, "{0:0}%", this.snapshot.CpuPercent),
                Sub1 = FormatCpuFrequencyPair(this.snapshot.CpuFrequencyGhz, this.snapshot.CpuBaseFrequencyGhz),
                Sub2 = this.snapshot.CpuCorePercents != null && this.snapshot.CpuCorePercents.Length > 0
                    ? this.snapshot.CpuCorePercents.Length.ToString(CultureInfo.InvariantCulture) + " cores"
                    : string.Empty,
                Accent = DesignTokens.Colors.Accent,
                History = this.cpuHistory,
                Cores = this.snapshot.CpuCorePercents
            };
            if (this.CurrentSettings.AlertTestEnabled)
            {
                cell.AlertPercent = 100.0;
                cell.AlertIconVisible = true;
            }

            cells.Add(cell);
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricMemory, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowMemory)
        {
            cells.Add(new DenseCell
            {
                Label = "MEM",
                Percent = this.snapshot.MemoryPercent,
                Value = string.Format(CultureInfo.InvariantCulture, "{0:0}%", this.snapshot.MemoryPercent),
                Sub1 = FormatGbPair(this.snapshot.MemoryUsedGb, this.snapshot.MemoryTotalGb),
                Sub2 = string.Format(CultureInfo.InvariantCulture, "HW {0:0.0}%", this.snapshot.MemoryHardwareReservedPercent),
                Accent = DesignTokens.Colors.AccentAlt,
                History = this.memoryHistory,
                // GPU and NPU memory come out of the same pool on this platform, so their combined
                // footprint is shown as a segment of the memory gauge rather than a separate bar.
                SecondaryPercent = this.snapshot.MemoryTotalGb > 0.0
                    ? (this.snapshot.GpuMemoryUsedGb + this.snapshot.NpuMemoryUsedGb) / this.snapshot.MemoryTotalGb * 100.0
                    : 0.0,
                // Page file actually written to disk, to the right of the two sub-lines, same
                // treatment as disk capacity and link type. Two decimals at most: this figure sits
                // near zero on a healthy machine, so "0.09 GB" has to stay readable while a machine
                // under real pressure shows "12.4 GB". No used/total pair — the allocation size says
                // nothing about memory health, and pairing them made the number read as a fill level.
                Corner = this.snapshot.PageFileTotalGb > 0.0
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "PF {0:0.##} GB",
                        this.snapshot.PageFileUsedGb)
                    : string.Empty
            });
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricDisk, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowDisk)
        {
            cells.Add(new DenseCell
            {
                Label = "DISK",
                Percent = this.snapshot.DiskPercent,
                Value = string.Format(CultureInfo.InvariantCulture, "{0:0}%", this.snapshot.DiskPercent),
                Sub1 = "W " + FormatRate(this.snapshot.DiskWriteBytesPerSecond),
                Sub2 = "R " + FormatRate(this.snapshot.DiskReadBytesPerSecond),
                Accent = DesignTokens.Colors.Warning,
                History = this.diskWriteHistory,
                // Whole GB: the corner figure is a large glanceable number, and a decimal on a
                // ~950 GB volume only costs width.
                Corner = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0}/{1:0} GB",
                    this.snapshot.DiskUsedGb,
                    this.snapshot.DiskTotalGb)
            });
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNetwork, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowNetwork)
        {
            cells.Add(new DenseCell
            {
                Label = "NET",
                Percent = -1.0,
                Value = FormatRate(this.snapshot.NetworkReceivedBytesPerSecond),
                Sub1 = "UP " + FormatRate(this.snapshot.NetworkSentBytesPerSecond),
                Sub2 = FormatWifiRssi(this.snapshot.NetworkRssiKnown, this.snapshot.NetworkRssiDbm),
                Accent = DesignTokens.Colors.Success,
                History = this.networkHistory,
                Disconnected = !this.snapshot.NetworkConnected,
                Corner = FormatLinkKind(this.snapshot.NetworkConnected, this.snapshot.NetworkIsWifi, this.snapshot.NetworkRssiKnown)
            });
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricGpu, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowGpu)
        {
            cells.Add(new DenseCell
            {
                Label = "GPU",
                Percent = this.snapshot.GpuPercent,
                Value = string.Format(CultureInfo.InvariantCulture, "{0:0}%", this.snapshot.GpuPercent),
                Sub1 = FormatGbPair(this.snapshot.GpuMemoryUsedGb, this.snapshot.GpuMemoryTotalGb),
                Sub2 = string.Format(CultureInfo.InvariantCulture, "VRAM {0:0}%", this.snapshot.GpuMemoryPercent),
                Accent = DesignTokens.Colors.AccentSoft,
                History = this.gpuHistory
            });
            return;
        }

        if (string.Equals(metricId, WidgetSettings.MetricNpu, StringComparison.OrdinalIgnoreCase) && this.CurrentSettings.ShowNpu)
        {
            DenseCell cell = new DenseCell
            {
                Label = "NPU",
                Percent = this.snapshot.NpuPercent,
                Value = string.Format(CultureInfo.InvariantCulture, "{0:0}%", this.snapshot.NpuPercent),
                Sub1 = FormatGbPair(this.snapshot.NpuMemoryUsedGb, this.snapshot.NpuMemoryTotalGb),
                Sub2 = string.Format(CultureInfo.InvariantCulture, "MEM {0:0}%", this.snapshot.NpuMemoryPercent),
                Accent = DesignTokens.Colors.AccentDeep,
                History = this.npuHistory
            };
            if (this.npuAlertIconActive)
            {
                cell.AlertIconVisible = true;
            }

            cells.Add(cell);
        }
    }

    // The reader distinguishes Wi-Fi from everything else; a wired adapter has no RSSI, so a
    // connected non-Wi-Fi link with no signal reading is reported as Ethernet rather than guessing.
    private static string FormatLinkKind(bool connected, bool isWifi, bool rssiKnown)
    {
        if (!connected)
        {
            return "断开";
        }

        if (isWifi)
        {
            return "Wi-Fi";
        }

        return rssiKnown ? "无线" : "有线";
    }

    // Spacing between cells, in logical units (S() scales them). These are the only thing that
    // controls the gaps — window size changes the cell size, not the spacing — so tightening the
    // layout means lowering these, not resizing the window. Chosen 1.0.5.85 from rendered
    // comparisons: at 522x408 they cost 34px of height, down from 84px at the previous 9/9/8/8.
    // NOTE: these are LOGICAL units — S() multiplies by the layer scale (2.0 on this device), so
    // S(4) is 8 physical px. The chosen preset was specified in physical pixels (8px inset,
    // 8/6px gaps, 34px total gap height); filling those numbers in here directly would double
    // every gap, which is exactly what shipped in 1.0.5.85 through 1.0.5.90.
    private const int PadUnits = 4;
    private const int GapXUnits = 2;
    private const int GapYUnits = 2;
    private const int StripGapUnits = 2;
    private const int GuardStripUnits = 13;
    private const int GuardGapUnits = 2;

    private void DrawDenseGrid(Graphics g)
    {
        List<DenseCell> cells = BuildDenseCells();
        if (cells.Count == 0)
        {
            Font empty = GetCachedFont(13.0f * this.LayerScale, FontStyle.Bold);
            using (SolidBrush brush = new SolidBrush(DesignTokens.Colors.TextMuted))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString("No metrics enabled", empty, brush, this.ClientRectangle, format);
            }

            return;
        }

        float pad = S(PadUnits);
        float gapX = S(GapXUnits);
        float gapY = S(GapYUnits);

        // The power strip is integrated at the bottom (1.0.5.83); the metric grid keeps whatever
        // height is left above it.
        // Guard badges sit inside the window below the power strip, so the grid gives up that band
        // too. Height is fixed whether or not any guard is armed — the strip must not make the
        // layout jump when one toggles.
        float guardH = S(GuardStripUnits);
        float guardBlock = guardH + S(GuardGapUnits);

        int cols = cells.Count == 1 ? 1 : 2;
        float cellW = (this.Width - pad * 2.0f - gapX * (cols - 1)) / cols;

        // The power strip is laid out on the SAME column grid as the metric cells (two columns of
        // cellW) rather than in three panes of its own. Three panes split the width at 174/349
        // while the metric grid and the four guard badges both break at 261, so the old strip was
        // the one band whose dividers lined up with nothing above or below it.
        bool showStrip = ShouldShowIntegratedPowerStrip();
        int metricRows = (cells.Count + cols - 1) / cols;
        int stripRows = showStrip ? 1 : 0;
        int totalRows = metricRows + stripRows;

        float available = this.Height - pad * 2.0f - guardBlock
            - gapY * Math.Max(0, metricRows - 1)
            - (showStrip ? S(StripGapUnits) : 0.0f);
        float cellH = available / Math.Max(1, totalRows);

        for (int i = 0; i < cells.Count; i++)
        {
            RectangleF cell = new RectangleF(
                pad + (i % cols) * (cellW + gapX),
                pad + (i / cols) * (cellH + gapY),
                cellW,
                cellH);
            DrawDenseCell(g, cell, cells[i]);
        }

        if (showStrip)
        {
            float stripTop = pad + metricRows * (cellH + gapY) - gapY + S(StripGapUnits);
            DrawPowerStrip(
                g,
                new RectangleF(pad, stripTop, this.Width - pad * 2.0f, cellH),
                cellW,
                cellH,
                gapX,
                gapY);
        }

        // Guard badges are inset a further 5px per side relative to the cards above. Their 1px
        // outline sits brighter than the cards', so matching the x range exactly still reads as if
        // they stick out; pulling them in slightly settles the edge visually.
        float guardInset = S(5);
        DrawGuardStrip(
            g,
            new RectangleF(
                pad + guardInset,
                this.Height - pad - guardH,
                this.Width - pad * 2.0f - guardInset * 2.0f,
                guardH));
    }

    private bool ShouldShowIntegratedPowerStrip()
    {
        return this.CurrentSettings != null &&
            this.CurrentSettings.PowerThermalIntegratedEnabled &&
            this.powerThermalForm != null &&
            !this.powerThermalForm.IsDisposed;
    }

    // Vertical rhythm is laid out in real pixels rather than fractions of the cell: the fonts below
    // are absolute sizes, so a proportional band would not track them and the gauge ended up drawn
    // through the sub-value text. Bands are summed at their natural height and scaled down together
    // only if the cell is too short to seat them.
    private void DrawDenseCell(Graphics g, RectangleF cell, DenseCell cell_)
    {
        // The guard badges below carry a 1px outline; without a matching one the cards' soft
        // translucent fill reads as if it stops short of the badge edge even though both span the
        // same x range. The outline makes the shared boundary visible rather than implied.
        using (GraphicsPath p = RoundedRectangle(cell, S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 92)))
        using (Pen border = new Pen(DesignTokens.White(34), 1.0f))
        {
            g.FillPath(fill, p);
            g.DrawPath(border, p);
        }

        // Trend as a faint watermark behind the numbers: it costs no layout height, and every text
        // line below is painted over it.
        DrawDenseWatermark(g, new RectangleF(cell.Left + S(2), cell.Top + cell.Height * 0.34f, cell.Width - S(4), cell.Height * 0.66f - S(2)), cell_);

        bool quotaAlertsVisible = AlertPresentationPolicy.ShouldPresent(
            this.CurrentSettings,
            AlertPresentationCategory.Quota);
        if (quotaAlertsVisible && cell_.AlertPercent > 0.0)
        {
            DrawUsageAlertLayer(g, cell, cell_.AlertPercent, quotaAlertsVisible && cell_.AlertIconVisible);
        }

        // Padding and band gaps are deliberately tight: the cell size is fixed by the grid, so every
        // pixel spent on inset is a pixel the type cannot use. Content is pushed close to the card
        // outline — earlier values (inset 9, padTop 3, gaugeGap 2) left it visibly floating.
        float x = cell.Left + S(3);
        float w = cell.Width - S(6);

        float valueBandH = S(22);   // seats the headline number at its natural size
        float gaugeGap = S(1);
        float gaugeH = S(4);
        float subH = S(11);
        float padTop = S(1);
        float padBottom = S(1);

        float natural = padTop + valueBandH + gaugeGap + gaugeH + gaugeGap + subH * 2.0f + padBottom;
        float scale = natural > cell.Height ? cell.Height / natural : 1.0f;
        valueBandH *= scale;
        gaugeGap *= scale;
        gaugeH *= scale;
        subH *= scale;
        padTop *= scale;

        // Fonts follow the bands they sit in, so a shorter cell shrinks type instead of overlapping.
        Font labelFont = GetCachedFont(Math.Max(7.0f, valueBandH * 0.52f), FontStyle.Bold);
        Font valueFont = GetCachedFont(Math.Max(9.0f, valueBandH * 0.96f), FontStyle.Bold);
        Font subFont = GetCachedFont(Math.Max(7.0f, subH * 0.92f), FontStyle.Bold);

        float y = cell.Top + padTop;
        RectangleF valueBand = new RectangleF(x, y, w, valueBandH);
        float labelW = Math.Min(S(32), w * 0.32f);
        using (SolidBrush b = new SolidBrush(DesignTokens.WithAlpha(cell_.Accent, 250)))
        {
            DrawFittedText(g, cell_.Label, labelFont, b, new RectangleF(valueBand.Left, valueBand.Top, labelW, valueBand.Height));
        }

        using (SolidBrush b = new SolidBrush(cell_.Disconnected ? DesignTokens.Colors.Danger : DesignTokens.Colors.TextStrong))
        {
            DrawFittedText(g, cell_.Value, valueFont, b, new RectangleF(valueBand.Left + labelW, valueBand.Top, valueBand.Width - labelW, valueBand.Height));
        }

        y = valueBand.Bottom + gaugeGap;
        bool hasCores = cell_.Cores != null && cell_.Cores.Length > 0;
        if (hasCores)
        {
            // Per-core bars take the gauge slot plus the second sub-value row: the overall
            // percentage is already the headline number, and "18 cores" is static text, so the
            // space buys the per-core breakdown the old graph-box layout used to show.
            DrawDenseCoreBars(g, new RectangleF(x, y, w, gaugeH + gaugeGap + subH), cell_.Cores);
            y += gaugeH + gaugeGap + subH + gaugeGap;
            using (SolidBrush b = new SolidBrush(DesignTokens.Colors.Text))
            {
                DrawFittedText(g, cell_.Sub1, subFont, b, new RectangleF(x, y, w, subH));
            }

            return;
        }

        if (cell_.Percent >= 0.0)
        {
            DrawDenseGauge(g, new RectangleF(x, y, w, gaugeH), cell_.Percent, cell_.Accent);
            if (cell_.SecondaryPercent > 0.0)
            {
                DrawDenseGaugeSegment(g, new RectangleF(x, y, w, gaugeH), cell_.Percent, cell_.SecondaryPercent);
            }
        }

        y += gaugeH + gaugeGap;

        // The corner figure sits to the right of the two sub-value lines and is sized against their
        // combined height, held back a little so it never reaches the cell edge.
        float subsW = w;
        if (!string.IsNullOrEmpty(cell_.Corner))
        {
            float cornerH = subH * 2.0f;
            Font cornerFont = GetCachedFont(Math.Max(9.0f, cornerH * 0.56f), FontStyle.Bold);
            // Claim only the width this particular string needs, capped so the two sub-value lines
            // on the left always keep enough room: a short "Wi-Fi" must not reserve the space that
            // "UP 205 Kbps" needs, and a long "283/954 GB" must not squeeze "W 2.1 Mbps" into an
            // ellipsis. Past the cap DrawRightText shrinks the corner text instead.
            float measured = g.MeasureString(cell_.Corner, cornerFont).Width;
            float cornerW = Math.Min(w * 0.46f, measured + S(3));
            RectangleF cornerRect = new RectangleF(x + w - cornerW, y, cornerW, cornerH);
            using (SolidBrush cb = new SolidBrush(DesignTokens.Colors.TextMuted))
            {
                DrawRightText(g, cell_.Corner, cornerFont, cb, cornerRect);
            }

            subsW = w - cornerW - S(5);
        }

        using (SolidBrush b = new SolidBrush(DesignTokens.Colors.Text))
        using (SolidBrush bm = new SolidBrush(DesignTokens.Colors.TextMuted))
        {
            DrawFittedText(g, cell_.Sub1, subFont, b, new RectangleF(x, y, subsW, subH));
            DrawFittedText(g, cell_.Sub2, subFont, bm, new RectangleF(x, y + subH, subsW, subH));
        }
    }

    // Highlights the tail of the filled gauge: on this platform GPU and NPU memory are carved out
    // of the same pool, so their share is drawn inside the used portion rather than appended to it.
    private void DrawDenseGaugeSegment(Graphics g, RectangleF rect, double usedPercent, double segmentPercent)
    {
        double used = Clamp(usedPercent, 0.0, 100.0);
        double segment = Clamp(segmentPercent, 0.0, used);
        if (segment <= 0.0 || rect.Width <= 1.0f)
        {
            return;
        }

        float usedW = (float)(rect.Width * used / 100.0);
        float segW = (float)(rect.Width * segment / 100.0);
        if (segW < 1.0f)
        {
            segW = 1.0f;
        }

        RectangleF seg = new RectangleF(rect.Left + usedW - segW, rect.Top, segW, rect.Height);
        using (GraphicsPath p = RoundedRectangle(seg, rect.Height / 2.0f))
        using (SolidBrush b = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245)))
        {
            g.FillPath(b, p);
        }
    }

    // Per-core utilisation bars, restored from the pre-1.0.5.69 graph-box layout (the original
    // DrawCoreBars). Colour is banded rather than continuous: up to 80% the accent, the 80-95%
    // portion overlaid in warning, and a fully saturated danger bar past 95%.
    private void DrawDenseCoreBars(Graphics g, RectangleF rect, double[] values)
    {
        if (values == null || values.Length == 0 || rect.Width <= 2.0f || rect.Height <= 2.0f)
        {
            return;
        }

        using (GraphicsPath trackPath = RoundedRectangle(rect, S(2)))
        using (SolidBrush track = new SolidBrush(DesignTokens.White(22)))
        {
            g.FillPath(track, trackPath);
        }

        float bottom = rect.Bottom;
        float height = rect.Height;
        float slot = rect.Width / values.Length;
        float gap = slot >= 4.0f ? Math.Min(1.5f * this.LayerScale, slot * 0.24f) : 0.0f;
        float barWidth = Math.Max(1.0f, slot - gap);

        // Thresholds are user-configurable; critical is clamped to at least warning so an inverted
        // pair cannot make the warning band disappear.
        double warningThreshold = Clamp(this.CurrentSettings.CpuCoreWarningPercent, 1.0, 100.0);
        double criticalThreshold = Math.Max(warningThreshold, Clamp(this.CurrentSettings.CpuCoreCriticalPercent, 1.0, 100.0));

        using (SolidBrush normalBrush = new SolidBrush(DesignTokens.Accent(150)))
        using (SolidBrush warningBrush = new SolidBrush(DesignTokens.Warning(215)))
        using (SolidBrush criticalBrush = new SolidBrush(DesignTokens.Danger(230)))
        {
            for (int i = 0; i < values.Length; i++)
            {
                double value = Clamp(values[i], 0.0, 100.0);
                float x = rect.Left + slot * i + gap / 2.0f;
                float valueTop = bottom - (float)(height * value / 100.0);

                if (value >= criticalThreshold)
                {
                    g.FillRectangle(criticalBrush, x, valueTop, barWidth, bottom - valueTop);
                    continue;
                }

                float normalValue = (float)Math.Min(value, warningThreshold);
                if (normalValue > 0.0f)
                {
                    float normalTop = bottom - height * normalValue / 100.0f;
                    g.FillRectangle(normalBrush, x, normalTop, barWidth, bottom - normalTop);
                }

                if (value > warningThreshold)
                {
                    float warningBottom = bottom - height * (float)warningThreshold / 100.0f;
                    g.FillRectangle(warningBrush, x, valueTop, barWidth, warningBottom - valueTop);
                }
            }
        }
    }

    private void DrawDenseWatermark(Graphics g, RectangleF rect, DenseCell cell)
    {
        List<double> history = cell.History;
        if (history == null || history.Count < 2 || rect.Width <= 2.0f || rect.Height <= 2.0f)
        {
            return;
        }

        double max = 1.0;
        for (int i = 0; i < history.Count; i++)
        {
            max = Math.Max(max, history[i]);
        }

        PointF[] points = new PointF[history.Count];
        for (int i = 0; i < history.Count; i++)
        {
            float px = rect.Left + rect.Width * i / Math.Max(1, history.Count - 1);
            float py = rect.Bottom - (float)(Clamp(history[i] / max, 0.0, 1.0) * (rect.Height - 1));
            points[i] = new PointF(px, py);
        }

        PointF[] area = new PointF[points.Length + 2];
        Array.Copy(points, area, points.Length);
        area[points.Length] = new PointF(rect.Right, rect.Bottom);
        area[points.Length + 1] = new PointF(rect.Left, rect.Bottom);

        // Normal rendering stays faint so history does not compete with the text. Hidden-mode
        // inversion runs after SourceOver composition; alpha 16 lets the dark card dominate the
        // source pixel, whose inverse becomes nearly white. Raising only the hidden-mode source
        // alpha preserves enough chroma for the post-process while the window-wide 95% transparency
        // still keeps the result subtle.
        Color accent = cell.Disconnected ? DesignTokens.Colors.TextMuted : cell.Accent;
        int fillAlpha = DenseWatermarkFillAlpha;
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(accent, fillAlpha)))
        using (Pen line = new Pen(DesignTokens.WithAlpha(accent, 64), Math.Max(1.0f, this.LayerScale)))
        {
            g.FillPolygon(fill, area);
            g.DrawLines(line, points);
        }
    }

    private void DrawDenseGauge(Graphics g, RectangleF rect, double percent, Color accent)
    {
        if (rect.Width <= 1.0f)
        {
            return;
        }

        Color fillColor = accent;
        if (percent >= 90.0)
        {
            fillColor = DesignTokens.Colors.DangerStrong;
        }
        else if (percent >= 75.0)
        {
            fillColor = DesignTokens.Colors.Warning;
        }

        using (GraphicsPath track = RoundedRectangle(rect, rect.Height / 2.0f))
        using (SolidBrush trackBrush = new SolidBrush(DesignTokens.White(30)))
        {
            g.FillPath(trackBrush, track);
        }

        float w = (float)(rect.Width * Clamp(percent, 0.0, 100.0) / 100.0);
        if (w > 1.0f)
        {
            using (GraphicsPath p = RoundedRectangle(new RectangleF(rect.Left, rect.Top, w, rect.Height), rect.Height / 2.0f))
            using (SolidBrush b = new SolidBrush(DesignTokens.WithAlpha(fillColor, 235)))
            {
                g.FillPath(b, p);
            }
        }
    }
}
