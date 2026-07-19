using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

// Guard status strip at the bottom of the main widget (1.0.5.88). One badge per guard, lit while
// armed and dimmed while off, so a sleep guard left running is visible without opening the guard
// board — the whole point of the module is that "still held open" must not be a silent state.
//
// The full set is always drawn, including the inactive ones: a fixed height keeps the layout from
// jumping when a guard toggles, and the dim badges double as a reminder that the guards exist.
internal sealed partial class WidgetForm
{
    private sealed class GuardBadge
    {
        public string Label;
        public bool Active;
        public string Detail;   // elapsed for sleep guard, remaining for timed guards
        public Color Accent;
    }

    // Render-harness seam: the harness never builds an OperationForm, so an armed strip could not
    // be rendered at all without this. Null in normal runs.
    internal GuardRuntime GuardRuntimeOverrideForRenderSample;

    private List<GuardBadge> BuildGuardBadges()
    {
        List<GuardBadge> badges = new List<GuardBadge>();
        GuardRuntime runtime = this.GuardRuntimeOverrideForRenderSample;
        if (runtime == null && this.operationForm != null && !this.operationForm.IsDisposed)
        {
            try
            {
                runtime = this.operationForm.PeekGuardRuntime();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }

        DateTime nowUtc = DateTime.UtcNow;

        // Guard state is per-process and never persisted, so a board that was never opened means
        // everything is genuinely off rather than merely unknown.
        bool sleepOn = runtime != null && runtime.SleepGuardEnabled;
        badges.Add(new GuardBadge
        {
            Label = "防睡眠",
            Active = sleepOn,
            Detail = sleepOn ? FormatGuardSpan(runtime.GetSleepGuardElapsed(nowUtc)) : string.Empty,
            Accent = DesignTokens.Colors.Warning
        });

        bool displayOn = runtime != null && runtime.DisplayGuardActive;
        badges.Add(new GuardBadge
        {
            Label = "防息屏",
            Active = displayOn,
            Detail = displayOn ? FormatGuardSpan(runtime.DisplayGuardUntilUtc - nowUtc) : string.Empty,
            Accent = DesignTokens.Colors.Accent
        });

        bool carePauseOn = runtime != null && runtime.BatteryCarePauseActive;
        badges.Add(new GuardBadge
        {
            Label = "养护暂停",
            Active = carePauseOn,
            Detail = carePauseOn ? FormatGuardSpan(runtime.BatteryCarePauseUntilUtc - nowUtc) : string.Empty,
            Accent = DesignTokens.Colors.AccentAlt
        });

        // Offline is the noteworthy state here, so the badge lights up when the link is down.
        bool offline = runtime != null && !runtime.Online;
        badges.Add(new GuardBadge
        {
            Label = offline ? "离线" : "在线",
            Active = offline,
            Detail = offline ? FormatGuardSpan(nowUtc - runtime.OfflineSinceUtc) : string.Empty,
            Accent = DesignTokens.Colors.DangerStrong
        });

        return badges;
    }

    private static string FormatGuardSpan(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        int totalMinutes = (int)value.TotalMinutes;
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return hours > 0
            ? hours.ToString(CultureInfo.InvariantCulture) + "h" + minutes.ToString("00", CultureInfo.InvariantCulture)
            : minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    private void DrawGuardStrip(Graphics g, RectangleF strip)
    {
        List<GuardBadge> badges = BuildGuardBadges();
        if (badges.Count == 0)
        {
            return;
        }

        float gap = S(5);
        float badgeW = (strip.Width - gap * (badges.Count - 1)) / badges.Count;
        Font labelFont = GetCachedFont(Math.Max(8.0f, strip.Height * 0.46f), FontStyle.Bold);
        Font detailFont = GetCachedFont(Math.Max(7.0f, strip.Height * 0.40f), FontStyle.Bold);
        bool hiddenColorProtection = IsBurnInColorProtectionActive();

        for (int i = 0; i < badges.Count; i++)
        {
            GuardBadge badge = badges[i];
            RectangleF b = new RectangleF(strip.Left + i * (badgeW + gap), strip.Top, badgeW, strip.Height);
            Color accent = ResolveGuardBadgeColor(badge, hiddenColorProtection);

            using (GraphicsPath p = RoundedRectangle(b, S(4)))
            using (SolidBrush fill = new SolidBrush(badge.Active
                ? DesignTokens.WithAlpha(accent, 46)
                : DesignTokens.White(14)))
            using (Pen border = new Pen(DesignTokens.WithAlpha(accent, badge.Active ? 190 : 60), 1.0f))
            {
                g.FillPath(fill, p);
                g.DrawPath(border, p);
            }

            float textW = b.Width - S(5);
            using (SolidBrush tb = new SolidBrush(badge.Active ? accent : DesignTokens.Colors.SubtleText))
            {
                if (string.IsNullOrEmpty(badge.Detail))
                {
                    DrawFittedText(g, badge.Label, labelFont, tb, new RectangleF(b.Left + S(2.5f), b.Top, textW, b.Height));
                }
                else
                {
                    DrawFittedText(g, badge.Label, labelFont, tb, new RectangleF(b.Left + S(2.5f), b.Top, textW * 0.62f, b.Height));
                    DrawRightText(g, badge.Detail, detailFont, tb, new RectangleF(b.Left + S(2.5f) + textW * 0.60f, b.Top, textW * 0.40f, b.Height));
                }
            }
        }
    }

    private Color ResolveGuardBadgeColor(GuardBadge badge, bool hiddenColorProtection)
    {
        if (!badge.Active)
        {
            return DesignTokens.Colors.TextMuted;
        }

        // Hidden mode drops the widget to the protected palette; an armed guard still has to read
        // as armed there, so it keeps an accent rather than falling back to neutral text colour.
        if (hiddenColorProtection)
        {
            return DesignTokens.Colors.AccentAlt;
        }

        return badge.Accent;
    }
}
