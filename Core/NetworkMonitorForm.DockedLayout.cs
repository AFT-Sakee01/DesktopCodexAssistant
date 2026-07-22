using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// The docked layout, drawn in the same visual family as SpecBoardForm and the Codex task board:
// AppBackground fill, DesignTokens.Border hairlines, UiFontCache pixel fonts at the S(12)/S(9)/
// S(7.8) ladder, and every row height measured from the actual font (never hand-guessed pixels —
// the first version of this file hard-coded row heights and overlapped at runtime, which is the
// exact failure the root AGENTS.md layout rule exists to prevent).
internal sealed partial class NetworkMonitorForm
{
    // Below this logical SpecBoardWidth the two columns cannot both hold a readable address, so
    // the layout collapses to a single column. Mirrors SpecBoardForm.CompactRailMinimumLogicalWidth.
    private const int DockedSingleColumnMinWidth = 460;
    private const int DockedMaxDisplayHops = 12;

    private readonly UiFontCache dockFonts = new UiFontCache();
    private Rectangle dockedRefreshButtonBounds = Rectangle.Empty;
    private Rectangle dockedCloseButtonBounds = Rectangle.Empty;

    private struct DockedFooterLayout
    {
        public Rectangle RefreshAction;
        public Rectangle CloseAction;
        public Rectangle RecentError;
        public Rectangle Trace;
    }

    private bool IsDockedSingleColumn
    {
        get { return this.CurrentSettings != null && this.CurrentSettings.SpecBoardWidth < DockedSingleColumnMinWidth; }
    }

    private static int MeasureDockLineHeight(Graphics g, Font font, int padding)
    {
        return Math.Max(1, (int)Math.Ceiling(g.MeasureString("Ag国", font, int.MaxValue, StringFormat.GenericTypographic).Height) + padding);
    }

    private static StringFormat CreateDockFormat(StringAlignment alignment, StringTrimming trimming)
    {
        StringFormat format = new StringFormat(StringFormat.GenericTypographic);
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Center;
        format.Trimming = trimming;
        format.FormatFlags |= StringFormatFlags.NoWrap;
        return format;
    }

    private static DockedFooterLayout ComputeDockedFooterLayout(
        Rectangle footer,
        float refreshTextWidth,
        float closeTextWidth,
        int minimumActionWidth,
        int actionTextPadding,
        int actionGap,
        int detailsGap)
    {
        int refreshWidth = Math.Min(
            footer.Width,
            Math.Max(minimumActionWidth, (int)Math.Ceiling(refreshTextWidth) + actionTextPadding));
        Rectangle refreshAction = new Rectangle(footer.Left, footer.Top, refreshWidth, footer.Height);
        int closeLeft = Math.Min(footer.Right, refreshAction.Right + actionGap);
        int closeWidth = Math.Min(
            Math.Max(0, footer.Right - closeLeft),
            Math.Max(minimumActionWidth, (int)Math.Ceiling(closeTextWidth) + actionTextPadding));
        Rectangle closeAction = new Rectangle(closeLeft, footer.Top, closeWidth, footer.Height);
        int detailsLeft = Math.Min(footer.Right, closeAction.Right + detailsGap);
        int detailsWidth = Math.Max(0, footer.Right - detailsLeft);

        // The error is the primary footer status and therefore keeps the larger share. Trace
        // metadata remains right-aligned in its own non-overlapping slot, matching the old 55/45
        // footer balance after accounting for the new action rail.
        int errorWidth = detailsWidth <= 1
            ? detailsWidth
            : Math.Max(1, (int)Math.Floor(detailsWidth * 0.58f));
        Rectangle recentError = new Rectangle(detailsLeft, footer.Top, errorWidth, footer.Height);
        Rectangle trace = new Rectangle(recentError.Right, footer.Top, Math.Max(0, footer.Right - recentError.Right), footer.Height);
        return new DockedFooterLayout
        {
            RefreshAction = refreshAction,
            CloseAction = closeAction,
            RecentError = recentError,
            Trace = trace
        };
    }

    private void DrawContentDocked(Graphics g)
    {
        BurnInProtection.ConfigureGraphics(g, IsBurnInColorProtectionActive());
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Family shell: AppBackground wash plus the Codex board's subtle Border outline. The
        // window transparency knob is the SpecBoard override at the layered-window level, so the
        // wash alpha here matches SpecBoardForm exactly.
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, 238)))
        {
            g.FillRectangle(background, 0, 0, this.Width, this.Height);
        }

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0.5f, 0.5f, this.Width - 1, this.Height - 1), Math.Max(3, S(10))))
        using (Pen border = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 96), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawPath(border, shell);
        }

        Font headerFont = this.dockFonts.GetUi(S(12.0f), FontStyle.Bold);
        Font monoFont = this.dockFonts.GetMono(S(9.0f), FontStyle.Bold);
        Font bodyFont = this.dockFonts.GetUi(S(9.0f), FontStyle.Regular);
        Font bodyBold = this.dockFonts.GetUi(S(9.2f), FontStyle.Bold);
        Font smallFont = this.dockFonts.GetUi(S(7.8f), FontStyle.Regular);
        Font smallBold = this.dockFonts.GetUi(S(7.8f), FontStyle.Bold);
        Font hopFont = this.dockFonts.GetMono(S(8.6f), FontStyle.Regular);

        int pad = S(10);
        int headerHeight = MeasureDockLineHeight(g, headerFont, S(6));
        int bodyRow = MeasureDockLineHeight(g, bodyFont, S(4));
        int smallRow = MeasureDockLineHeight(g, smallFont, S(2));
        int sectionRow = MeasureDockLineHeight(g, smallBold, S(4));
        int footerHeight = MeasureDockLineHeight(g, smallFont, S(5));
        int pillHeight = MeasureDockLineHeight(g, bodyBold, S(6));
        int bandHeight = Math.Max(pillHeight, smallRow * 2 + S(2));

        Rectangle content = new Rectangle(pad, pad, Math.Max(1, this.Width - pad * 2), Math.Max(1, this.Height - pad * 2));
        Rectangle header = new Rectangle(content.Left, content.Top, content.Width, headerHeight);
        DrawDockedHeader(g, header, headerFont, monoFont, smallFont);

        Rectangle band = new Rectangle(content.Left, header.Bottom + S(4), content.Width, bandHeight);
        DrawDockedProfileBand(g, band, pillHeight, bodyBold, bodyFont, smallFont);

        Rectangle footer = new Rectangle(content.Left, Math.Max(band.Bottom, content.Bottom - footerHeight), content.Width, footerHeight);
        int bodyTop = band.Bottom + S(6);
        Rectangle body = new Rectangle(content.Left, bodyTop, content.Width, Math.Max(1, footer.Top - S(3) - bodyTop));

        using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 112), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawLine(divider, band.Left, band.Bottom + S(3), band.Right, band.Bottom + S(3));
        }

        if (this.IsDockedSingleColumn)
        {
            // The stacked layout must retain both egress module headings. PathPing and fixed Ping
            // already have explicit narrow-mode row caps, so identity/egress receives the larger
            // share instead of clipping the cloud-service heading while still drawing its first row.
            int identityHeight = Math.Max(1, (int)(body.Height * 0.62f));
            Rectangle identity = new Rectangle(body.Left, body.Top, body.Width, identityHeight);
            Rectangle quality = new Rectangle(body.Left, identity.Bottom + S(4), body.Width, Math.Max(1, body.Bottom - identity.Bottom - S(4)));
            DrawDockedIdentityColumn(g, identity, bodyRow, sectionRow, bodyFont, smallBold);
            DrawDockedPathPingColumn(g, quality, bodyRow, sectionRow, bodyFont, bodyBold, smallBold, hopFont, monoFont);
        }
        else
        {
            int leftWidth = Math.Max(S(150), (int)Math.Round(body.Width * 0.52));
            Rectangle left = new Rectangle(body.Left, body.Top, leftWidth, body.Height);
            Rectangle right = new Rectangle(left.Right + S(7), body.Top, Math.Max(1, body.Right - left.Right - S(7)), body.Height);
            using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 112), Math.Max(1.0f, this.LayerScale)))
            {
                g.DrawLine(divider, left.Right + S(3), left.Top, left.Right + S(3), left.Bottom);
            }

            DrawDockedIdentityColumn(g, left, bodyRow, sectionRow, bodyFont, smallBold);
            DrawDockedPathPingColumn(g, right, bodyRow, sectionRow, bodyFont, bodyBold, smallBold, hopFont, monoFont);
        }

        DrawDockedFooter(g, footer, smallFont);
        EdgeDockTabForm.DrawBoardAccentBorder(g, this.Size, EdgeDockTabRole.Network, this.LayerScale);
    }

    private void DrawDockedHeader(Graphics g, Rectangle bounds, Font headerFont, Font monoFont, Font smallFont)
    {
        NetworkAccessState accessState = GetDisplayAccessState();
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush status = new SolidBrush(GetHeaderStatusColor(accessState)))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateDockFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateDockFormat(StringAlignment.Far, StringTrimming.None))
        using (StringFormat farClipped = CreateDockFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            string title = "NETWORK";
            float titleWidth = g.MeasureString(title, headerFont).Width + S(8);
            g.DrawString(title, headerFont, text, new RectangleF(bounds.Left, bounds.Top, titleWidth, bounds.Height), near);

            string time = this.snapshot == null || this.snapshot.UpdatedLocal == DateTime.MinValue
                ? "--:--"
                : this.snapshot.UpdatedLocal.ToString("HH:mm", CultureInfo.InvariantCulture);
            float timeWidth = g.MeasureString(time, monoFont).Width + 4;
            RectangleF timeRect = new RectangleF(bounds.Right - timeWidth, bounds.Top, timeWidth, bounds.Height);
            g.DrawString(time, monoFont, text, timeRect, far);

            // Status and summary must clip inside their slots: at the single-column minimum width
            // an untrimmed Far-aligned summary spills left across the title.
            string statusText = GetHeaderStatusText(accessState);
            float statusLimit = Math.Max(10.0f, timeRect.Left - S(6) - (bounds.Left + titleWidth));
            float statusWidth = Math.Min(statusLimit, g.MeasureString(statusText, smallFont).Width + S(6));
            RectangleF statusRect = new RectangleF(bounds.Left + titleWidth, bounds.Top, statusWidth, bounds.Height);
            g.DrawString(statusText, smallFont, status, statusRect, near);

            RectangleF summaryRect = new RectangleF(
                statusRect.Right,
                bounds.Top,
                Math.Max(0.0f, timeRect.Left - S(6) - statusRect.Right),
                bounds.Height);
            if (summaryRect.Width > S(30))
            {
                g.DrawString(BuildNetworkLinkSummary(), smallFont, muted, summaryRect, farClipped);
            }
        }
    }

    // The three verdict pills mirror the connection check window's badges, restyled to this
    // family's Surface-fill/colored-border chip (same recipe as SpecBoard's copy notice). They
    // stay in place with "--" when unchecked: a band that appears and disappears would shift
    // every row below it.
    private void DrawDockedProfileBand(Graphics g, Rectangle bounds, int pillHeight, Font bodyBold, Font bodyFont, Font smallFont)
    {
        CleanIpConnectionSnapshot profile = this.cleanIpSnapshot ?? new CleanIpConnectionSnapshot();
        int pillTop = bounds.Top + Math.Max(0, (bounds.Height - pillHeight) / 2);
        float x = bounds.Left;
        x = DrawDockedPill(g, x, pillTop, pillHeight, "纯净度 " + profile.ScoreLabel, DesignTokens.Colors.Success, bodyBold);
        x = DrawDockedPill(g, x, pillTop, pillHeight, EmptyToDash(profile.NativeLabel), DesignTokens.Colors.Accent, bodyBold);
        x = DrawDockedPill(g, x, pillTop, pillHeight, EmptyToDash(profile.IpTypeLabel), DesignTokens.Colors.AccentAlt, bodyBold);

        float textLeft = x + S(6);
        float textWidth = Math.Max(10.0f, bounds.Right - textLeft);
        int lineHeight = Math.Max(1, bounds.Height / 2);
        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.TextStrong))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateDockFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(
                BuildDockedEgressLine(profile),
                bodyFont,
                text,
                new RectangleF(textLeft, bounds.Top, textWidth, lineHeight),
                near);
            g.DrawString(
                BuildDockedEgressReasonLine(profile),
                smallFont,
                muted,
                new RectangleF(textLeft, bounds.Top + lineHeight, textWidth, bounds.Height - lineHeight),
                near);
        }
    }

    private float DrawDockedPill(Graphics g, float x, int top, int height, string label, Color accent, Font font)
    {
        string text = string.IsNullOrWhiteSpace(label) ? "--" : label.Trim();
        float textWidth = g.MeasureString(text, font, int.MaxValue, StringFormat.GenericTypographic).Width;
        int width = Math.Max(S(30), (int)Math.Ceiling(textWidth) + S(14));
        Rectangle bounds = new Rectangle((int)Math.Round(x), top, width, height);
        using (GraphicsPath path = RoundedRectangle(bounds, S(5)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Surface, 220)))
        using (Pen border = new Pen(DesignTokens.WithAlpha(accent, 200), Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush textBrush = new SolidBrush(accent))
        using (StringFormat centered = CreateDockFormat(StringAlignment.Center, StringTrimming.EllipsisCharacter))
        {
            g.FillPath(fill, path);
            g.DrawPath(border, path);
            g.DrawString(text, font, textBrush, bounds, centered);
        }

        return bounds.Right + S(5);
    }

    private string BuildDockedEgressLine(CleanIpConnectionSnapshot profile)
    {
        string ip = EmptyToDash(profile.Ip);
        if (string.Equals(ip, "--", StringComparison.Ordinal) && this.snapshot != null && this.snapshot.PublicIpKnown)
        {
            ip = EmptyToDash(this.snapshot.PublicIp);
        }

        return "出口 " + ip + " · " + EmptyToDash(profile.Location) + " · " + EmptyToDash(profile.Organization);
    }

    private string BuildDockedEgressReasonLine(CleanIpConnectionSnapshot profile)
    {
        string checkedAt = profile.CheckedAtKnown
            ? profile.CheckedAtLocal.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "--";
        string reason = string.IsNullOrWhiteSpace(profile.IpTypeReason)
            ? (string.IsNullOrWhiteSpace(profile.Error) ? "判定待确认" : profile.Error)
            : profile.IpTypeReason;
        return EmptyToDash(profile.Asn) + " · " + reason + " · 检测 " + checkedAt;
    }

    private void DrawDockedIdentityColumn(Graphics g, Rectangle bounds, int bodyRow, int sectionRow, Font bodyFont, Font smallBold)
    {
        int y = bounds.Top;
        y = DrawDockedSectionLabel(g, bounds, y, sectionRow, "身份 / 地址", smallBold);
        y = DrawDockedTextRow(g, bounds, y, bodyRow, BuildDockedInterfaceLine(), DesignTokens.Colors.TextStrong, bodyFont);
        y = DrawDockedTextRow(
            g,
            bounds,
            y,
            bodyRow,
            "IPv4 " + BuildSingleAddressRowText(this.snapshot == null ? null : this.snapshot.IPv4, int.MaxValue) +
            "  网关 " + EmptyToDash(this.snapshot == null ? null : this.snapshot.DefaultGatewayAddress),
            DesignTokens.Colors.TextStrong,
            bodyFont);
        y = DrawDockedTextRow(
            g,
            bounds,
            y,
            bodyRow,
            "IPv6 " + BuildSingleAddressRowText(this.snapshot == null ? null : this.snapshot.IPv6, int.MaxValue),
            DesignTokens.Colors.TextStrong,
            bodyFont);

        y = DrawDockedSectionLabel(g, bounds, y, sectionRow, "DNS", smallBold);
        DnsServerSnapshot[] dnsServers = this.snapshot == null ? null : this.snapshot.DnsServerDetails;
        if (dnsServers == null || dnsServers.Length == 0)
        {
            y = DrawDockedTextRow(g, bounds, y, bodyRow, "--", DesignTokens.Colors.GlyphMuted, bodyFont);
        }
        else
        {
            for (int i = 0; i < dnsServers.Length; i++)
            {
                DnsServerSnapshot dns = dnsServers[i];
                if (dns == null)
                {
                    continue;
                }

                string latency = dns.LatencyMs > 0
                    ? dns.LatencyMs.ToString(CultureInfo.InvariantCulture) + "ms"
                    : "--";
                y = DrawDockedTextRow(
                    g,
                    bounds,
                    y,
                    bodyRow,
                    dns.Address + "  " + latency + "  " + GetDnsStatusText(dns.Status),
                    GetDockedDnsStatusColor(dns.Status),
                    bodyFont);
            }
        }

        y = DrawDockedModuleDivider(g, bounds, y);
        y = DrawDockedSectionLabel(g, bounds, y, sectionRow, "出境", smallBold);
        y = DrawDockedTextRow(g, bounds, y, bodyRow, "GFW " + BuildDockedGfwText(), GetGfwProbeColor(), bodyFont);

        // GFW is a censorship-path verdict while cloud endpoints are independent service-health
        // probes. Keeping a visible module boundary prevents the cloud rows from reading as GFW
        // evidence, without changing either probe's scheduling or status semantics.
        y = DrawDockedModuleDivider(g, bounds, y);
        y = DrawDockedSectionLabel(g, bounds, y, sectionRow, "云服务检测", smallBold);
        CloudEndpointSnapshot[] endpoints = this.snapshot == null || this.snapshot.GfwProbe == null
            ? null
            : this.snapshot.GfwProbe.CloudEndpoints;
        if (endpoints == null || endpoints.Length == 0)
        {
            DrawDockedTextRow(g, bounds, y, bodyRow, "--", DesignTokens.Colors.GlyphMuted, bodyFont);
            return;
        }

        NetworkAccessState accessState = GetDisplayAccessState();
        for (int i = 0; i < endpoints.Length; i++)
        {
            CloudEndpointSnapshot endpoint = endpoints[i];
            if (endpoint == null)
            {
                continue;
            }

            string name = string.IsNullOrWhiteSpace(endpoint.DisplayName) ? endpoint.ShortLabel : endpoint.DisplayName;
            string detail = endpoint.LatencyMs > 0
                ? endpoint.LatencyMs.ToString(CultureInfo.InvariantCulture) + "ms"
                : EmptyToDash(endpoint.Reason);
            y = DrawDockedTextRow(g, bounds, y, bodyRow, name + "  " + detail, GetCloudEndpointDockedColor(endpoint, accessState), bodyFont);
            if (y > bounds.Bottom)
            {
                break;
            }
        }

        // Radar service LEDs (Radar / OpenAI / Claude / DeepSeek) moved to the Codex IQ board's
        // status band (1.0.6.x); the network panel keeps only the cloud-endpoint probes here.
    }

    private string BuildDockedInterfaceLine()
    {
        if (this.snapshot == null || !this.snapshot.InterfaceKnown)
        {
            return "接口 --";
        }

        return EmptyToDash(this.snapshot.InterfaceName) + " · MAC " + EmptyToDash(this.snapshot.MacAddress);
    }

    private void DrawDockedPathPingColumn(
        Graphics g,
        Rectangle bounds,
        int bodyRow,
        int sectionRow,
        Font bodyFont,
        Font bodyBold,
        Font smallBold,
        Font hopFont,
        Font monoFont)
    {
        PathPingSnapshot pathPing = this.snapshot == null ? null : this.snapshot.PathPing;
        int fixedPingHeight = GetDockedFixedPingHeight(bodyRow, sectionRow);
        Rectangle pathBounds = new Rectangle(
            bounds.Left,
            bounds.Top,
            bounds.Width,
            Math.Max(1, bounds.Height - fixedPingHeight - S(3)));
        Rectangle fixedPingBounds = new Rectangle(
            bounds.Left,
            pathBounds.Bottom + S(3),
            bounds.Width,
            Math.Max(1, bounds.Bottom - pathBounds.Bottom - S(3)));
        string target = pathPing == null || string.IsNullOrWhiteSpace(pathPing.TargetLabel)
            ? EmptyToDash(this.snapshot == null ? null : this.snapshot.ConnectivityTarget)
            : pathPing.TargetLabel;
        string heading = "PATHPING → " + target;
        if (pathPing != null && pathPing.Stale)
        {
            heading += "（路径刷新中）";
        }

        int y = pathBounds.Top;
        if (pathPing != null && pathPing.DiscoveryInProgress && !pathPing.PathKnown)
        {
            // First discovery has no old hop table to preserve, so combine the section heading and
            // progress on one measured row. This also guarantees PATHPING stays visible instead of
            // looking like an unlabelled generic loading bar on compact boards.
            y = DrawDockedPathProgress(g, pathBounds, y, bodyRow, pathPing, bodyFont, heading + "  ");
        }
        else
        {
            y = DrawDockedSectionLabel(g, pathBounds, y, sectionRow, heading, smallBold);
            if (pathPing != null && pathPing.DiscoveryInProgress)
            {
                y = DrawDockedPathProgress(g, pathBounds, y, bodyRow, pathPing, bodyFont, string.Empty);
            }
        }

        if (pathPing == null || pathPing.IcmpUnavailable)
        {
            // No usable ICMP means no hop data exists to show. Falling back to the coarse rolling
            // diagnosis keeps the column informative instead of empty.
            y = DrawDockedTextRow(
                g,
                pathBounds,
                y,
                bodyRow,
                pathPing == null ? "逐跳探测不可用" : pathPing.BlameText,
                DesignTokens.Colors.Warning,
                bodyFont);
            DrawDockedTextRow(g, pathBounds, y, bodyRow, BuildDockedRollingFallbackLine(), GetConnectivityColor(), bodyFont);
            DrawDockedFixedPingSection(g, fixedPingBounds, bodyRow, sectionRow, bodyFont, smallBold, monoFont);
            return;
        }

        if (!pathPing.PathKnown)
        {
            if (!pathPing.DiscoveryInProgress)
            {
                DrawDockedTextRow(g, pathBounds, y, bodyRow, "正在发现路径…", DesignTokens.Colors.GlyphMuted, bodyFont);
            }

            DrawDockedFixedPingSection(g, fixedPingBounds, bodyRow, sectionRow, bodyFont, smallBold, monoFont);
            return;
        }

        string endToEnd = pathPing.EndToEndKnown
            ? "端到端 " + FormatLatencyMs(pathPing.EndToEndLatencyMs) + " · 丢包 " + PathPingProbeReader.FormatPercent(pathPing.EndToEndLossPercent)
            : "端到端 采样中";
        y = DrawDockedTextRow(g, pathBounds, y, bodyRow, endToEnd, DesignTokens.Colors.TextStrong, bodyFont);

        // Reserve the blame verdict row up front so a long hop list cannot push it off the bottom;
        // the verdict is the single most valuable line in this column.
        bool hasBlame = !string.IsNullOrEmpty(pathPing.BlameText);
        int blameReserve = hasBlame ? bodyRow + S(2) : 0;
        int hopBottom = pathBounds.Bottom - blameReserve;

        PathPingHopSnapshot[] hops = pathPing.Hops ?? new PathPingHopSnapshot[0];
        int hopCapacity = bodyRow <= 0 ? 0 : Math.Max(0, (hopBottom - y) / bodyRow);
        int cap = this.IsDockedSingleColumn ? DockedMaxDisplayHops / 2 : DockedMaxDisplayHops;
        int visibleHops = Math.Min(hops.Length, Math.Min(cap, hopCapacity));
        bool hasOverflow = hops.Length > visibleHops;
        if (hasOverflow && visibleHops > 0)
        {
            visibleHops--;
        }

        for (int i = 0; i < visibleHops; i++)
        {
            y = DrawDockedHopRow(g, pathBounds, y, bodyRow, hops[i], hopFont, monoFont);
        }

        if (hasOverflow)
        {
            y = DrawDockedTextRow(
                g,
                pathBounds,
                y,
                bodyRow,
                "… 其余 " + (hops.Length - visibleHops).ToString(CultureInfo.InvariantCulture) + " 跳未显示",
                DesignTokens.Colors.GlyphMuted,
                bodyFont);
        }

        if (hasBlame)
        {
            DrawDockedTextRow(
                g,
                pathBounds,
                Math.Max(y, pathBounds.Bottom - bodyRow),
                bodyRow,
                pathPing.BlameText,
                GetPathPingBlameColor(pathPing.Blame),
                bodyBold);
        }

        DrawDockedFixedPingSection(g, fixedPingBounds, bodyRow, sectionRow, bodyFont, smallBold, monoFont);
    }

    private int DrawDockedModuleDivider(Graphics g, Rectangle bounds, int y)
    {
        int height = S(10);
        if (y + height > bounds.Bottom)
        {
            return y;
        }

        int lineY = y + S(6);
        using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 144), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawLine(divider, bounds.Left, lineY, bounds.Right, lineY);
        }

        return y + height;
    }

    private int DrawDockedPathProgress(
        Graphics g,
        Rectangle bounds,
        int y,
        int rowHeight,
        PathPingSnapshot pathPing,
        Font bodyFont,
        string labelPrefix)
    {
        if (y + rowHeight > bounds.Bottom)
        {
            return y;
        }

        int current = Math.Max(0, pathPing.DiscoveryCurrentHop);
        int maximum = Math.Max(1, pathPing.DiscoveryMaxHops);
        int percent = PathPingProbeReader.GetDiscoveryPercent(current, maximum);
        int barHeight = Math.Max(S(3), (int)Math.Ceiling(this.LayerScale * 2.0f));
        Rectangle bar = new Rectangle(bounds.Left, y + rowHeight - barHeight - S(1), bounds.Width, barHeight);
        Rectangle fill = new Rectangle(bar.Left, bar.Top, (int)Math.Round(bar.Width * percent / 100.0), bar.Height);
        string progressText = current.ToString(CultureInfo.InvariantCulture) + "/" +
            maximum.ToString(CultureInfo.InvariantCulture) + "  " + percent.ToString(CultureInfo.InvariantCulture) + "%";
        string label = string.IsNullOrEmpty(labelPrefix)
            ? "发现路径 " + progressText
            : labelPrefix + progressText;

        using (SolidBrush text = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (SolidBrush track = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 170)))
        using (SolidBrush progress = new SolidBrush(DesignTokens.Colors.Success))
        using (StringFormat near = CreateDockFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(label, bodyFont, text, new RectangleF(bounds.Left, y, bounds.Width, rowHeight - barHeight), near);
            g.FillRectangle(track, bar);
            if (fill.Width > 0)
            {
                g.FillRectangle(progress, fill);
            }
        }

        return y + rowHeight;
    }

    private int GetDockedFixedPingHeight(int bodyRow, int sectionRow)
    {
        FixedPingSnapshot fixedPing = this.snapshot == null ? null : this.snapshot.FixedPing;
        FixedPingTargetSnapshot[] targets = fixedPing == null ? null : fixedPing.Targets;
        int maxRows = this.IsDockedSingleColumn ? 2 : 3;
        int rowCount = targets == null || targets.Length == 0 ? 1 : Math.Min(maxRows, targets.Length);
        return S(8) + sectionRow + rowCount * bodyRow;
    }

    private void DrawDockedFixedPingSection(
        Graphics g,
        Rectangle bounds,
        int bodyRow,
        int sectionRow,
        Font bodyFont,
        Font smallBold,
        Font monoFont)
    {
        if (bounds.Height <= 0)
        {
            return;
        }

        int y = bounds.Top;
        using (Pen divider = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Border, 144), Math.Max(1.0f, this.LayerScale)))
        {
            g.DrawLine(divider, bounds.Left, y + S(2), bounds.Right, y + S(2));
        }

        y += S(8);
        y = DrawDockedSectionLabel(g, bounds, y, sectionRow, "固定 PING", smallBold);
        FixedPingSnapshot fixedPing = this.snapshot == null ? null : this.snapshot.FixedPing;
        FixedPingTargetSnapshot[] targets = fixedPing == null ? null : fixedPing.Targets;
        if (targets == null || targets.Length == 0)
        {
            DrawDockedTextRow(
                g,
                bounds,
                y,
                bodyRow,
                fixedPing == null ? "等待检测" : "未启用固定站点",
                DesignTokens.Colors.GlyphMuted,
                bodyFont);
            return;
        }

        int maxRows = this.IsDockedSingleColumn ? 2 : 3;
        int visibleTargets = Math.Min(targets.Length, maxRows);
        bool overflow = targets.Length > visibleTargets;
        if (overflow && visibleTargets > 0)
        {
            visibleTargets--;
        }

        for (int i = 0; i < visibleTargets; i++)
        {
            y = DrawDockedFixedPingRow(g, bounds, y, bodyRow, targets[i], bodyFont, monoFont);
        }

        if (overflow)
        {
            DrawDockedTextRow(
                g,
                bounds,
                y,
                bodyRow,
                "… 其余 " + (targets.Length - visibleTargets).ToString(CultureInfo.InvariantCulture) + " 个站点",
                DesignTokens.Colors.GlyphMuted,
                bodyFont);
        }
    }

    private int DrawDockedFixedPingRow(
        Graphics g,
        Rectangle bounds,
        int y,
        int rowHeight,
        FixedPingTargetSnapshot target,
        Font bodyFont,
        Font monoFont)
    {
        if (target == null || y + rowHeight > bounds.Bottom)
        {
            return y;
        }

        string detail;
        if (target.Status == FixedPingStatus.Normal || target.Status == FixedPingStatus.Slow)
        {
            detail = target.LatencyMs.ToString(CultureInfo.InvariantCulture) + "ms";
        }
        else if (target.Status == FixedPingStatus.Checking)
        {
            detail = "检测中";
        }
        else
        {
            detail = EmptyToDash(target.Reason);
        }

        float detailWidth = Math.Min(bounds.Width * 0.36f, Math.Max(S(48), g.MeasureString(detail, monoFont).Width + S(5)));
        RectangleF nameBounds = new RectangleF(bounds.Left, y, Math.Max(1, bounds.Width - detailWidth), rowHeight);
        RectangleF detailBounds = new RectangleF(bounds.Right - detailWidth, y, detailWidth, rowHeight);
        string name = EmptyToDash(target.DisplayName) + "  " + EmptyToDash(target.Target);
        Color color = GetFixedPingColor(target.Status);
        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat near = CreateDockFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateDockFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(name, bodyFont, brush, nameBounds, near);
            g.DrawString(detail, monoFont, brush, detailBounds, far);
        }

        return y + rowHeight;
    }

    private static Color GetFixedPingColor(FixedPingStatus status)
    {
        if (status == FixedPingStatus.Normal)
        {
            return DesignTokens.Colors.Success;
        }

        if (status == FixedPingStatus.Slow || status == FixedPingStatus.Checking)
        {
            return DesignTokens.Colors.Warning;
        }

        if (status == FixedPingStatus.Down)
        {
            return DesignTokens.Colors.Danger;
        }

        return DesignTokens.Colors.GlyphMuted;
    }

    private Color GetCloudEndpointDockedColor(CloudEndpointSnapshot endpoint, NetworkAccessState accessState)
    {
        CloudEndpointStatus status = GetEffectiveCloudEndpointStatus(endpoint, accessState);
        if (status == CloudEndpointStatus.Normal)
        {
            return Color.FromArgb(143, 220, 168);
        }

        if (status == CloudEndpointStatus.Slow || status == CloudEndpointStatus.Checking)
        {
            return DesignTokens.Colors.Warning;
        }

        if (status == CloudEndpointStatus.Down)
        {
            return DesignTokens.Colors.Danger;
        }

        if (status == CloudEndpointStatus.Abnormal)
        {
            return DesignTokens.Colors.WarningDeep;
        }

        return DesignTokens.Colors.GlyphMuted;
    }

    private string BuildDockedRollingFallbackLine()
    {
        PingRollingSnapshot rolling = this.snapshot == null ? null : this.snapshot.PingRolling;
        if (rolling == null || !rolling.StatsReady)
        {
            return "PING 采样中";
        }

        string text = "PING " + FormatLatencyMs(rolling.LatencyMs) + " · 丢包 " + PathPingProbeReader.FormatPercent(rolling.LossPercent);
        if (!string.IsNullOrWhiteSpace(rolling.DiagnosisText))
        {
            text += " · " + rolling.DiagnosisText;
        }

        return text;
    }

    private int DrawDockedSectionLabel(Graphics g, Rectangle bounds, int y, int sectionRow, string text, Font smallBold)
    {
        if (y + sectionRow > bounds.Bottom)
        {
            return y;
        }

        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateDockFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(text, smallBold, muted, new RectangleF(bounds.Left, y, bounds.Width, sectionRow), near);
        }

        return y + sectionRow;
    }

    private int DrawDockedTextRow(Graphics g, Rectangle bounds, int y, int rowHeight, string text, Color color, Font font)
    {
        if (y + rowHeight > bounds.Bottom + rowHeight / 2)
        {
            return y;
        }

        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat near = CreateDockFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(text, font, brush, new RectangleF(bounds.Left, y, bounds.Width, rowHeight), near);
        }

        return y + rowHeight;
    }

    // Hop rows are a four-column table rather than a sentence: numeric columns right-align in the
    // mono font so latency and loss scan straight down the path.
    private int DrawDockedHopRow(Graphics g, Rectangle bounds, int y, int rowHeight, PathPingHopSnapshot hop, Font hopFont, Font monoFont)
    {
        if (y + rowHeight > bounds.Bottom)
        {
            return y;
        }

        float lossWidth = Math.Max(g.MeasureString("100%", monoFont).Width + 4, bounds.Width * 0.13f);
        float latencyWidth = Math.Max(g.MeasureString("9999ms", monoFont).Width + 4, bounds.Width * 0.16f);
        float numberWidth = Math.Max(g.MeasureString("30", monoFont).Width + 4, bounds.Width * 0.07f);
        float nodeWidth = Math.Max(10.0f, bounds.Width - numberWidth - latencyWidth - lossWidth);

        string node;
        if (!hop.Responding)
        {
            node = hop.MergedHopCount > 1
                ? "×" + hop.MergedHopCount.ToString(CultureInfo.InvariantCulture) + " 跳合并 · 不响应"
                : "* 不响应";
        }
        else
        {
            node = hop.Address;
            if (hop.IsGateway)
            {
                node += " 网关";
            }
            else if (hop.IsTarget)
            {
                node += " 目标";
            }
        }

        bool hasStats = hop.Responding && hop.SampleCount > 0;
        string latency = hasStats ? FormatLatencyMs(hop.AvgLatencyMs) : "--";
        string loss = hasStats ? PathPingProbeReader.FormatPercent(hop.LossPercent) : "--";
        Color rowColor = GetPathPingHopColor(hop.Severity);

        using (SolidBrush brush = new SolidBrush(rowColor))
        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateDockFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateDockFormat(StringAlignment.Far, StringTrimming.None))
        {
            Brush rowBrush = hop.Responding ? (Brush)brush : muted;
            Brush statBrush = hasStats ? (Brush)brush : muted;
            float x = bounds.Left;
            g.DrawString(hop.HopNumber.ToString(CultureInfo.InvariantCulture), monoFont, rowBrush, new RectangleF(x, y, numberWidth, rowHeight), near);
            x += numberWidth;
            g.DrawString(node, hopFont, rowBrush, new RectangleF(x, y, nodeWidth, rowHeight), near);
            x += nodeWidth;
            g.DrawString(latency, monoFont, statBrush, new RectangleF(x, y, latencyWidth, rowHeight), far);
            x += latencyWidth;
            g.DrawString(loss, monoFont, statBrush, new RectangleF(x, y, lossWidth, rowHeight), far);
        }

        return y + rowHeight;
    }

    private void DrawDockedFooter(Graphics g, Rectangle bounds, Font smallFont)
    {
        PathPingSnapshot pathPing = this.snapshot == null ? null : this.snapshot.PathPing;
        string error = this.snapshot == null || string.IsNullOrWhiteSpace(this.snapshot.LastError)
            ? "最近错误：无"
            : "最近错误：" + this.snapshot.LastError;
        string trace = pathPing != null && pathPing.LastTraceKnown
            ? "轮次 #" + pathPing.RoundCount.ToString(CultureInfo.InvariantCulture) +
              " · 路径 " + pathPing.LastTraceLocal.ToString("HH:mm", CultureInfo.InvariantCulture) + " 发现"
            : "路径未发现";

        DockedFooterLayout layout = ComputeDockedFooterLayout(
            bounds,
            g.MeasureString("刷新", smallFont).Width,
            g.MeasureString("关闭", smallFont).Width,
            S(42),
            S(14),
            S(4),
            S(5));
        this.dockedRefreshButtonBounds = layout.RefreshAction;
        this.dockedCloseButtonBounds = layout.CloseAction;
        DrawDockedFooterAction(g, layout.RefreshAction, "刷新", DesignTokens.Colors.Success, smallFont);
        DrawDockedFooterAction(g, layout.CloseAction, "关闭", DesignTokens.Colors.Danger, smallFont);

        using (SolidBrush muted = new SolidBrush(DesignTokens.Colors.GlyphMuted))
        using (StringFormat near = CreateDockFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter))
        using (StringFormat far = CreateDockFormat(StringAlignment.Far, StringTrimming.EllipsisCharacter))
        {
            g.DrawString(error, smallFont, muted, layout.RecentError, near);
            g.DrawString(trace, smallFont, muted, layout.Trace, far);
        }
    }

    private void DrawDockedFooterAction(Graphics g, Rectangle bounds, string text, Color semanticColor, Font font)
    {
        // Exact sibling-board action language: restrained 4px corners, Control fill, semantic
        // outline and neutral text. The button is intentionally not a blue capsule and carries no
        // hover inversion, so the docked network board stays visually aligned with Spec/Codex.
        RectangleF actionBounds = bounds;
        using (GraphicsPath action = RoundedRectangle(RectangleF.Inflate(actionBounds, -1.0f, -1.0f), S(4)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Control, 220)))
        using (Pen border = new Pen(
            DesignTokens.WithAlpha(semanticColor, 170),
            Math.Max(1.0f, this.LayerScale)))
        using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.Text))
        using (StringFormat centered = CreateDockFormat(StringAlignment.Center, StringTrimming.None))
        {
            g.FillPath(fill, action);
            g.DrawPath(border, action);
            g.DrawString(text, font, textBrush, bounds, centered);
        }
    }

    private Color GetPathPingHopColor(PathPingHopSeverity severity)
    {
        if (severity == PathPingHopSeverity.Loss)
        {
            return DesignTokens.Colors.Danger;
        }

        if (severity == PathPingHopSeverity.RateLimited)
        {
            return DesignTokens.Colors.Warning;
        }

        if (severity == PathPingHopSeverity.Unresponsive)
        {
            return DesignTokens.Colors.GlyphMuted;
        }

        return DesignTokens.Colors.TextStrong;
    }

    private Color GetPathPingBlameColor(PathPingBlame blame)
    {
        if (blame == PathPingBlame.LinkLoss || blame == PathPingBlame.Unreachable)
        {
            return DesignTokens.Colors.Danger;
        }

        if (blame == PathPingBlame.NodeRateLimit || blame == PathPingBlame.IcmpUnavailable)
        {
            return DesignTokens.Colors.Warning;
        }

        return DesignTokens.Colors.Success;
    }

    private static string FormatLatencyMs(double value)
    {
        if (value <= 0.0)
        {
            return "--";
        }

        return value < 10.0
            ? value.ToString("0.0", CultureInfo.InvariantCulture) + "ms"
            : Math.Round(value).ToString("0", CultureInfo.InvariantCulture) + "ms";
    }

    private static string GetDnsStatusText(DnsServerStatus status)
    {
        if (status == DnsServerStatus.Normal)
        {
            return "正常";
        }

        if (status == DnsServerStatus.Problem)
        {
            return "异常";
        }

        if (status == DnsServerStatus.Hijacked)
        {
            return "劫持";
        }

        if (status == DnsServerStatus.Unavailable)
        {
            return "不可用";
        }

        return "待检测";
    }
}
