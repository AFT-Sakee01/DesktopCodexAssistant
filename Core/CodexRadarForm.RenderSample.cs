using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

// Headless render harness: the Codex monitor is a layered tool window that screenshot tools cannot
// capture (it is not a Start-menu app), so `--render-codexradar --out <dir>` paints one representative
// frame of each render variant to a PNG for visual review during layout work. Test-only; not part of
// the normal runtime path.
internal sealed partial class CodexRadarForm
{
    internal static void RenderVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        CodexRadarRenderVariant[] variants =
        {
            CodexRadarRenderVariant.EvenRow
        };

        foreach (CodexRadarRenderVariant variant in variants)
        {
            WidgetSettings settings = WidgetSettings.CreateDefaults();
            settings.CodexRadarRenderVariant = variant;
            settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Claude;
            settings.Normalize();

            using (CodexRadarForm form = new CodexRadarForm(settings, null))
            {
                // Replicate a 2880x1800 @ 200% display: scale=2 matches real DPI, and CodexRadarWidth/
                // Height are already the real physical pixel size (see CodexRadarForm.cs's
                // `this.Size = new Size(this.CurrentSettings.CodexRadarWidth, ...)` - there is no
                // separate DPI multiplication at runtime). An earlier `* 2` here rendered a canvas
                // twice as wide as any real window ever is, which hid genuine overflow/truncation
                // (e.g. "13:00" clipping to "13...") that only shows up at the true width.
                form.SetLayerScale(2.0f);
                form.MaximumSize = new Size(4000, 4000);
                form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
                form.effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Claude;
                // Normalize() force-disables CodexRadarTestMode, so inject a populated snapshot
                // directly instead - this is what actually exercises the quota radar trend content.
                form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);
                // Realistic DeepSeek balance for the shared window's Claude mode, where the
                // auxiliary bottom slot displays DS instead of Codex reset cards.
                DeepSeekBalanceMonitor.SetSnapshotForTest(new DeepSeekBalanceSnapshot
                {
                    ApiKeyConfigured = true,
                    Known = true,
                    IsAvailable = true,
                    Currency = "CNY",
                    BalanceCny = 473.36,
                    CheckedAtUtc = DateTime.UtcNow,
                    CheckedAtLocal = DateTime.Now
                });

                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(DesignTokens.Colors.AppBackground);
                    form.DrawCodexRadarContent(g);
                    string path = Path.Combine(
                        outputDir,
                        "codexradar-" + variant.ToString().ToLowerInvariant() + ".png");
                    bitmap.Save(path, ImageFormat.Png);
                    Console.WriteLine(
                        variant.ToString() + " -> " + path +
                        " (" + form.Width.ToString(CultureInfo.InvariantCulture) + "x" +
                        form.Height.ToString(CultureInfo.InvariantCulture) + ")");
                }

                // Also render EvenRow at a deliberately narrow width to reproduce the user's
                // truncation-prone case (short window, long DeepSeek balance).
                if (variant == CodexRadarRenderVariant.EvenRow)
                {
                    int narrowW = (int)(settings.CodexRadarWidth * 0.72f);
                    form.Size = new Size(narrowW, form.Height);
                    using (Bitmap nb = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                    using (Graphics ng = Graphics.FromImage(nb))
                    {
                        ng.Clear(DesignTokens.Colors.AppBackground);
                        form.DrawCodexRadarContent(ng);
                        nb.Save(Path.Combine(outputDir, "codexradar-evenrow-narrow.png"), ImageFormat.Png);
                        Console.WriteLine("EvenRow(narrow) -> " + form.Width.ToString(CultureInfo.InvariantCulture) + "x" + form.Height.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        RenderSpeedWindowCountdownSample(outputDir);
        RenderClaudeQuotaSourceSample(outputDir, "claude_site_public", "codexradar-claude-quota-site.png");
        RenderClaudeQuotaSourceSample(outputDir, "claude_personal", "codexradar-claude-quota-personal.png");
        RenderCodexBottomRowSample(outputDir);
        RenderCodexResetSkyBlueSample(outputDir);
        RenderCodexWeeklyBudgetSample(outputDir);
    }

    // Codex software mode's bottom row shows the reset-credit auxiliary as "RS:<n>-<remaining>"
    // ("<hours>h" when the earliest credit is under a day, else "<days>d"). A sub-day earliest also
    // turns the two quota rings into a static rainbow, so this fixture exercises both at once.
    private static void RenderCodexBottomRowSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.CodexRadarRenderVariant = CodexRadarRenderVariant.EvenRow;
        settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
        settings.Normalize();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
            form.effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
            form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);

            CodexResetCreditsSnapshot credits = CodexResetCreditsSnapshot.CreateDefault();
            credits.Known = true;
            credits.ReportedCount = 3;
            credits.AllExpirationTimesKnown = true;
            // Earliest active expiration is <24h so the auxiliary renders "RS:<n>-<hours>h" and the
            // sub-day condition turns the two quota rings into a static rainbow.
            credits.ExpirationTimesUtc.Add(DateTime.UtcNow.AddHours(17.0));
            credits.ExpirationTimesUtc.Add(DateTime.UtcNow.AddHours(40.0));
            credits.ExpirationTimesUtc.Add(DateTime.UtcNow.AddHours(60.0));
            credits.SourceUpdatedUtc = DateTime.UtcNow;
            form.codexResetCreditsSnapshot = credits;

            RenderSampleSupport.SaveComposited(
                outputDir,
                "codexradar-codex-bottomrow.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawRenderSampleIsolatedLayers);
            Console.WriteLine("CodexBottomRow -> codexradar-codex-bottomrow.png");
        }
    }

    // Reset-detected state: weekly quota confirmed back at/above 95%, no sub-day reset credit, so the
    // two quota rings render the solid sky-blue "reset detected" ring (which replaced the old rainbow).
    private static void RenderCodexResetSkyBlueSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.CodexRadarRenderVariant = CodexRadarRenderVariant.EvenRow;
        settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
        settings.Normalize();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
            form.effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
            form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);

            CodexQuotaSnapshot quota = CodexQuotaSnapshot.CreateDefault();
            quota.FiveHourPercent = 100;
            quota.WeeklyPercent = 98;
            QuotaRuntimeState quotaState = form.GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex);
            quotaState.Snapshot = quota;
            quotaState.SourceKnown = true;
            quotaState.WeeklyResetRainbowActive = true;
            // No reset credits, so QuotaResetCreditSubDay stays false and the sky-blue ring wins.
            form.codexResetCreditsSnapshot = CodexResetCreditsSnapshot.CreateDefault();

            RenderSampleSupport.SaveComposited(
                outputDir,
                "codexradar-codex-reset-skyblue.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawRenderSampleIsolatedLayers);
            Console.WriteLine("CodexResetSkyBlue -> codexradar-codex-reset-skyblue.png");
        }
    }

    // 5-hour limit removed (FiveHourLimitAbsent): the 5h cell becomes the weekly budget ring. Here
    // weekly remaining is 40% with the reset 4 days out (~57% of the week elapsed) -> consumed 60% vs
    // 43% elapsed -> efficiency ~140 (over budget, red), and the remaining 40% lasts ~48h at that rate
    // -> centre "140" (no %), bottom "48H".
    private static void RenderCodexWeeklyBudgetSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.CodexRadarRenderVariant = CodexRadarRenderVariant.EvenRow;
        settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
        settings.Normalize();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
            form.effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
            form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);

            CodexQuotaSnapshot quota = CodexQuotaSnapshot.CreateDefault();
            quota.FiveHourLimitAbsent = true;
            quota.WeeklyPercent = 40;
            quota.WeeklyResetKnown = true;
            quota.WeeklyResetLocal = DateTime.Now.AddDays(4.0);
            QuotaRuntimeState quotaState = form.GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex);
            quotaState.Snapshot = quota;
            quotaState.SourceKnown = true;

            RenderSampleSupport.SaveComposited(
                outputDir,
                "codexradar-codex-weekly-budget.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawRenderSampleIsolatedLayers);
            Console.WriteLine("CodexWeeklyBudget -> codexradar-codex-weekly-budget.png");
        }
    }

    private static void RenderClaudeQuotaSourceSample(string outputDir, string sourceKind, string fileName)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Claude;
        settings.Normalize();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
            form.effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Claude;
            form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);
            QuotaRuntimeState quotaState = form.GetQuotaRuntimeState(CodexRadarSoftwareMode.Claude);
            quotaState.Snapshot = CodexQuotaSnapshot.CreateDefault();
            quotaState.SourceKnown = false;
            ResetQuotaReadDeltaTracking(quotaState);
            DateTime nowLocal = DateTime.Now;
            CodexQuotaSnapshot quota = CodexQuotaSnapshot.CreateDefault();
            quota.FiveHourPercent = 68;
            quota.WeeklyPercent = 42;
            quota.FiveHourResetKnown = true;
            quota.FiveHourResetLocal = nowLocal.AddHours(2.0);
            quota.WeeklyResetKnown = true;
            quota.WeeklyResetLocal = nowLocal.AddDays(3.0);
            quota.SourceUpdatedKnown = true;
            quota.SourceUpdatedUtc = DateTime.UtcNow;
            form.ApplyQuotaSnapshot(
                CodexRadarSoftwareMode.Claude,
                quota,
                true,
                true,
                nowLocal,
                DateTime.UtcNow,
                sourceKind,
                false);
            RenderSampleSupport.SaveComposited(
                outputDir,
                fileName,
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawRenderSampleIsolatedLayers);
        }
    }

    private static void RenderSpeedWindowCountdownSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.CodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
        settings.CodexRadarSpeedWindowCountdownEnabled = true;
        settings.Normalize();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
            form.effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
            form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);
            DateTime now = DateTime.Now;
            form.codexRadarSnapshot.SpeedWindowKnown = true;
            form.codexRadarSnapshot.SpeedWindowOpen = true;
            form.codexRadarSnapshot.SpeedWindowStatus = "open";
            form.codexRadarSnapshot.SpeedWindowOpenedAtLocal = now.AddHours(-12.0);
            form.codexRadarSnapshot.SpeedWindowOpenedAtKnown = true;
            form.codexRadarSnapshot.SpeedWindowClosedAtLocal = now.AddHours(36.0);
            form.codexRadarSnapshot.SpeedWindowClosedAtKnown = true;
            RenderSpeedWindowCountdownSample(outputDir, form, true);
            RenderSpeedWindowCountdownSample(outputDir, form, false);
        }
    }

    private static void RenderSpeedWindowCountdownSample(
        string outputDir,
        CodexRadarForm form,
        bool countdownEnabled)
    {
        form.CurrentSettings.CodexRadarSpeedWindowCountdownEnabled = countdownEnabled;
        string fileName = countdownEnabled
            ? "codexradar-speed-window-countdown.png"
            : "codexradar-speed-window-countdown-disabled.png";
        RenderSampleSupport.SaveComposited(
            outputDir,
            fileName,
            form.Width,
            form.Height,
            form.GetApplicationOpacityAlpha(),
            form.DrawRenderSampleIsolatedLayers);
        Console.WriteLine(
            countdownEnabled
                ? "SpeedWindowCountdown -> " + fileName
                : "SpeedWindowCountdown(disabled) -> " + fileName);
    }

    private void DrawRenderSampleIsolatedLayers(Graphics target)
    {
        // GDI+ can lose already-painted sibling pixels when text is rasterized directly onto a
        // premultiplied transparent bitmap. Isolate the real background/content paths so every
        // acceptance fixture is a complete frame, including consecutive quota-source variants.
        using (Bitmap background = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppArgb))
        using (Bitmap content = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppArgb))
        using (Graphics backgroundGraphics = Graphics.FromImage(background))
        using (Graphics contentGraphics = Graphics.FromImage(content))
        {
            backgroundGraphics.Clear(Color.Transparent);
            DrawCodexRadarBackground(backgroundGraphics);
            contentGraphics.Clear(Color.Transparent);
            DrawCodexRadarContentLayer(contentGraphics);
            target.DrawImageUnscaled(background, 0, 0);
            target.DrawImageUnscaled(content, 0, 0);
        }
    }


    // Current-mode sample: real settings.ini (size/variant/transparency/manual offsets) plus
    // whatever disk caches the constructor loaded (codex-radar-cache.ini, quota snapshots), drawn
    // through the real full-frame pipeline and composited like the on-screen layered window.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(2.0f);
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.CodexRadarWidth, settings.CodexRadarHeight);
            if (form.codexRadarSnapshot == null)
            {
                // Cold start with no disk cache yet: fall back to the synthetic snapshot so the
                // frame is reviewable instead of blank. The PNG name still says "current" because
                // geometry, variant and transparency remain the user's real configuration.
                form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);
            }

            RenderSampleSupport.SaveComposited(
                outputDir,
                "codexradar-current.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawCodexRadar);
        }
    }

    internal static void RenderRadarSolo(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.Normalize();
        using (CodexRadarForm form = new CodexRadarForm(settings, null))
        {
            form.SetLayerScale(4.0f);
            form.codexRadarSnapshot = form.BuildTestCodexRadarSnapshot(CodexRadarTestMode.Open);
            CodexRadarSnapshot snap = form.GetCodexRadarDisplaySnapshot();
            using (Bitmap bmp = new Bitmap(160, 460, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(DesignTokens.Colors.AppBackground);
                BurnInProtection.ConfigureGraphics(g, false);
                RectangleF radarRect = new RectangleF(66, 20, 28, 420);
                form.DrawCodexQuotaRadarVerticalLine(g, radarRect, snap == null ? null : snap.QuotaRadar, 1.5f);
                bmp.Save(Path.Combine(outputDir, "radar-solo.png"), ImageFormat.Png);
                Console.WriteLine("radar-solo QuotaRadar.Known=" + (snap != null && snap.QuotaRadar != null ? snap.QuotaRadar.Known.ToString() : "null"));
            }
        }
    }
}
