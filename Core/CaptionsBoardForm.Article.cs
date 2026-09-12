using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

// The article half of the captions board: the running transcript, its paging and its buttons, plus
// the two overlay controls that belong here rather than on the strip itself (edit mode and reset).
//
// Why the article lives here and not in the caption strip: the strip shows at most a handful of
// fixed-height lines and is click-through by design, so it can neither hold a transcript nor a
// button. The board is the surface that already takes clicks for this feature, so every control the
// strip needs is on it.
//
// What goes into the article is the settled (white) text only -- see CaptionTranscript for why the
// in-progress line is excluded. The transcript itself is owned by TranslatorCaptionReader, which is
// where a sentence is first known to be finished; this board only reads it, clears it and exports it.
internal sealed partial class CaptionsBoardForm
{
    // How many of the newest sentences the on-board view considers. Wrapping an hour of speech into
    // lines on every repaint would cost more than the entire rest of the board; the export button is
    // the path to the full article, and this is the path to the part anyone pages through.
    private const int ArticleViewEntryLimit = 240;
    // A clear cannot be undone, so the button arms first and clears on a second click. The arming
    // lapses on its own, because an armed destructive button left sitting there is a trap.
    private const double ClearArmSeconds = 6.0;

    // Set by OperationForm once WidgetForm has handed over its caption reader and overlay. Assigned
    // after construction rather than through the constructor so the layout self-tests and the render
    // sample can build the board with no translator behind it at all.
    internal Func<TranslatorCaptionReader> CaptionReaderProvider;
    internal Func<CaptionOverlayForm> CaptionOverlayProvider;

    // 0 is the live tail; each page up walks one screenful further back. Clamped against what the
    // last draw could actually fit, which is the only place the page size is known.
    private int articlePageBack;
    private int articleMaxPageBack;
    private DateTime clearArmedUtc = DateTime.MinValue;
    private ArticleRenderCache articleCache;
    // Self-test and render-sample seam: with no reader behind the board there is no article to draw,
    // and the paging/empty-state behaviour is exactly what needs proving.
    private CaptionTranscript articleFixture;

    private sealed class ArticleRenderCache
    {
        internal int Revision;
        internal int Width;
        internal float Scale;
        internal List<string> TranslatedLines;
        internal List<string> OriginalLines;
    }

    private CaptionTranscript ResolveTranscript()
    {
        if (this.articleFixture != null)
        {
            return this.articleFixture;
        }

        Func<TranslatorCaptionReader> provider = this.CaptionReaderProvider;
        if (provider == null)
        {
            return null;
        }

        try
        {
            TranslatorCaptionReader reader = provider();
            return reader == null ? null : reader.Transcript;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return null;
        }
    }

    private CaptionOverlayForm ResolveCaptionOverlay()
    {
        Func<CaptionOverlayForm> provider = this.CaptionOverlayProvider;
        if (provider == null)
        {
            return null;
        }

        try
        {
            CaptionOverlayForm overlay = provider();
            return overlay == null || overlay.IsDisposed ? null : overlay;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return null;
        }
    }

    private bool IsCaptionOverlayEditing
    {
        get
        {
            CaptionOverlayForm overlay = ResolveCaptionOverlay();
            return overlay != null && overlay.IsEditing;
        }
    }

    // Handled before ExecuteAction's operationRunning gate and before it resolves the translator
    // reader: none of these touch setting.json or a process, so none of them has any reason to be
    // blocked while a translator restart is in flight.
    private bool TryExecuteArticleAction(CaptionsHitAction action)
    {
        switch (action)
        {
            case CaptionsHitAction.ArticlePageUp:
                this.articlePageBack = Math.Min(this.articlePageBack + 1, this.articleMaxPageBack);
                DisarmClear();
                RenderLayeredWindow();
                return true;

            case CaptionsHitAction.ArticlePageDown:
                this.articlePageBack = Math.Max(0, this.articlePageBack - 1);
                DisarmClear();
                RenderLayeredWindow();
                return true;

            case CaptionsHitAction.ArticleExport:
                DisarmClear();
                ExportArticle();
                RenderLayeredWindow();
                return true;

            case CaptionsHitAction.ArticleClear:
                ClearArticle();
                RenderLayeredWindow();
                return true;

            case CaptionsHitAction.SettledLinesMinus:
                DisarmClear();
                ApplySettledLineChange(-1);
                return true;

            case CaptionsHitAction.SettledLinesPlus:
                DisarmClear();
                ApplySettledLineChange(1);
                return true;

            case CaptionsHitAction.OverlayEditToggle:
                DisarmClear();
                ToggleCaptionOverlayEditMode();
                return true;

            case CaptionsHitAction.OverlayReset:
                DisarmClear();
                ResetCaptionOverlayGeometry();
                return true;

            case CaptionsHitAction.OverlayDisplayToggle:
                DisarmClear();
                ToggleCaptionOverlayDisplay();
                return true;

            case CaptionsHitAction.OverlayHoverAutoHideToggle:
                DisarmClear();
                ToggleCaptionOverlayHoverAutoHide();
                return true;

            default:
                return false;
        }
    }

    private void DisarmClear()
    {
        this.clearArmedUtc = DateTime.MinValue;
    }

    private bool IsClearArmed
    {
        get
        {
            return this.clearArmedUtc != DateTime.MinValue &&
                (DateTime.UtcNow - this.clearArmedUtc).TotalSeconds < ClearArmSeconds;
        }
    }

    private void ClearArticle()
    {
        if (!IsClearArmed)
        {
            this.clearArmedUtc = DateTime.UtcNow;
            this.statusNotice = "再次点击「清除」确认清空文章";
            return;
        }

        DisarmClear();
        CaptionTranscript transcript = ResolveTranscript();
        if (transcript != null)
        {
            transcript.Clear();
        }

        this.articlePageBack = 0;
        this.articleCache = null;
        this.statusNotice = string.Empty;
    }

    private void ExportArticle()
    {
        CaptionTranscript transcript = ResolveTranscript();
        if (transcript == null || transcript.Count == 0)
        {
            this.statusNotice = "文章还是空的，没有可导出的内容";
            return;
        }

        try
        {
            DateTime nowLocal = DateTime.Now;
            string directory = ResolveArticleExportDirectory();
            Directory.CreateDirectory(directory);
            string fileName = "article-" + nowLocal.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".md";
            string path = Path.Combine(directory, fileName);
            File.WriteAllText(path, transcript.BuildExportText(nowLocal), System.Text.Encoding.UTF8);
            this.statusNotice = "已导出 " + fileName;
            Program.LogInfo("Caption article exported to " + path + " (" + transcript.Count + " sentences).");
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            this.statusNotice = "导出失败：" + ex.Message;
        }
    }

    // Same storage root every other persistent artefact in this app uses, never beside the
    // executable (AGENTS.md "Persistent runtime data belongs under %LOCALAPPDATA%").
    internal static string ResolveArticleExportDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductIdentity.MachineName,
            "captions");
    }

    private void ApplySettledLineChange(int delta)
    {
        int current = this.CurrentSettings == null
            ? WidgetSettings.DefaultCaptionOverlaySettledLines
            : this.CurrentSettings.CaptionOverlaySettledLines;
        int next = Math.Max(
            WidgetSettings.MinCaptionOverlaySettledLines,
            Math.Min(WidgetSettings.MaxCaptionOverlaySettledLines, current + delta));
        if (next == current)
        {
            return;
        }

        int applied = next;
        PersistSettings(delegate(WidgetSettings settings)
        {
            settings.CaptionOverlaySettledLines = applied;
        });
    }

    private bool IsCaptionOverlayDisplayEnabled
    {
        get
        {
            return this.CurrentSettings == null || this.CurrentSettings.CaptionOverlayDisplayEnabled;
        }
    }

    // Hides the banner without touching the chain behind it. Deliberately not the same thing as
    // the CaptionOverlayEnabled master switch in the settings window: the reader keeps polling and
    // the article keeps recording, so the screen can be clear while the session is still being
    // written down. Leaving edit mode first, because there is nothing to drag once it is hidden.
    private void ToggleCaptionOverlayDisplay()
    {
        bool next = !IsCaptionOverlayDisplayEnabled;
        if (!next)
        {
            CaptionOverlayForm editing = ResolveCaptionOverlay();
            if (editing != null && editing.IsEditing)
            {
                editing.SetEditMode(false);
            }
        }

        PersistSettings(delegate(WidgetSettings settings)
        {
            settings.CaptionOverlayDisplayEnabled = next;
        });
        this.statusNotice = next ? string.Empty : "字幕条已隐藏，字幕仍在记录进文章";
        RenderLayeredWindow();
    }

    private bool IsCaptionOverlayHoverAutoHideEnabled
    {
        get
        {
            return this.CurrentSettings != null && this.CurrentSettings.CaptionOverlayHoverAutoHideEnabled;
        }
    }

    private void ToggleCaptionOverlayHoverAutoHide()
    {
        bool next = !IsCaptionOverlayHoverAutoHideEnabled;
        PersistSettings(delegate(WidgetSettings settings)
        {
            settings.CaptionOverlayHoverAutoHideEnabled = next;
        });
        this.statusNotice = next
            ? "鼠标移到字幕条上时它会淡到几乎看不见，移开恢复"
            : string.Empty;
        RenderLayeredWindow();
    }

    private void ToggleCaptionOverlayEditMode()
    {
        if (!IsCaptionOverlayDisplayEnabled)
        {
            // Placing something invisible is not a thing. Say so rather than silently turning the
            // strip back on: the user just chose to hide it.
            this.statusNotice = "字幕条已隐藏，先按「显示」再调整";
            RenderLayeredWindow();
            return;
        }

        CaptionOverlayForm overlay = ResolveCaptionOverlay();
        if (overlay == null)
        {
            this.statusNotice = "字幕面板未启用，无法进入编辑模式";
            RenderLayeredWindow();
            return;
        }

        if (!overlay.IsEditing)
        {
            overlay.SetEditMode(true);
            this.statusNotice = "编辑模式：拖动字幕条移动，拖两端改宽度，完成后按「完成」";
            RenderLayeredWindow();
            return;
        }

        // Read the rectangle before leaving edit mode: leaving it re-runs the automatic geometry,
        // which would overwrite exactly the numbers being saved.
        Rectangle bounds = overlay.GetLogicalBounds();
        overlay.SetEditMode(false);
        PersistSettings(delegate(WidgetSettings settings)
        {
            settings.CaptionOverlayLeft = bounds.Left;
            settings.CaptionOverlayTop = bounds.Top;
            settings.CaptionOverlayWidth = bounds.Width;
            settings.CaptionOverlayHeight = bounds.Height;
        });
        this.statusNotice = "字幕条位置已保存";
        RenderLayeredWindow();
    }

    private void ResetCaptionOverlayGeometry()
    {
        CaptionOverlayForm overlay = ResolveCaptionOverlay();
        if (overlay != null && overlay.IsEditing)
        {
            // Resetting while still dragging would save the rectangle being discarded.
            overlay.SetEditMode(false);
        }

        PersistSettings(delegate(WidgetSettings settings)
        {
            settings.CaptionOverlayLeft = WidgetSettings.AutoCaptionOverlayBounds;
            settings.CaptionOverlayTop = WidgetSettings.AutoCaptionOverlayBounds;
            settings.CaptionOverlayWidth = WidgetSettings.AutoCaptionOverlayBounds;
            settings.CaptionOverlayHeight = WidgetSettings.AutoCaptionOverlayBounds;
        });
        this.statusNotice = "字幕条已恢复默认大小和位置";
        RenderLayeredWindow();
    }

    // Load-mutate-save against the file rather than against this board's clone: the settings file is
    // the shared source of truth and the settings window may have changed other fields since this
    // board last saw it. WidgetForm's file watcher picks the write up and re-applies it everywhere;
    // the overlay is pushed the new settings directly as well so the strip does not visibly lag a
    // tick behind the button that changed it.
    private void PersistSettings(Action<WidgetSettings> mutate)
    {
        try
        {
            WidgetSettings settings = WidgetSettings.Load();
            mutate(settings);
            settings.Normalize();
            settings.Save();

            CaptionOverlayForm overlay = ResolveCaptionOverlay();
            if (overlay != null)
            {
                overlay.ApplySettings(settings);
            }

            ApplyRuntimeSettings(settings);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            this.statusNotice = "设置保存失败：" + ex.Message;
            RenderLayeredWindow();
        }
    }

    private List<string> ResolveArticleLines(Graphics g, bool translated, Font font, int width)
    {
        CaptionTranscript transcript = ResolveTranscript();
        int revision = transcript == null ? -1 : transcript.Revision;
        ArticleRenderCache cache = this.articleCache;
        if (cache == null || cache.Revision != revision || cache.Width != width || cache.Scale != this.LayerScale)
        {
            cache = new ArticleRenderCache
            {
                Revision = revision,
                Width = width,
                Scale = this.LayerScale,
                TranslatedLines = new List<string>(),
                OriginalLines = new List<string>(),
            };
            this.articleCache = cache;
        }

        List<string> lines = translated ? cache.TranslatedLines : cache.OriginalLines;
        if (lines.Count > 0 || transcript == null)
        {
            return lines;
        }

        List<string> paragraphs = transcript.BuildParagraphs(translated, ArticleViewEntryLimit);
        for (int i = 0; i < paragraphs.Count; i++)
        {
            WrapParagraph(g, paragraphs[i], font, width, lines);
        }

        return lines;
    }

    // Breaks one paragraph into the lines GDI+ would lay out in a box this wide, by asking it how
    // many characters fit in a single-line-tall box and walking forward. Done explicitly rather than
    // by handing the paragraph to DrawString because paging needs the line count, and a wrapped
    // DrawString never reports one.
    private static void WrapParagraph(Graphics g, string text, Font font, int width, List<string> lines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int safeWidth = Math.Max(8, width);
        using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
        {
            format.Trimming = StringTrimming.None;
            // LineLimit stops a box one line tall from reporting characters from a second, clipped
            // line, which would make the walk below skip text.
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.LineLimit;
            // Measured, not Font.GetHeight: a CJK line lays out taller than the font reports, and a
            // box even a pixel short of one real line fits zero characters -- which is how an
            // earlier version of this wrapped Chinese one character per line.
            float lineHeight = Math.Max(
                font.GetHeight(g),
                g.MeasureString("Ag国", font, int.MaxValue, format).Height) + 1.0f;
            int index = 0;
            while (index < text.Length)
            {
                int charactersFitted;
                int linesFilled;
                g.MeasureString(
                    text.Substring(index),
                    font,
                    new SizeF(safeWidth, lineHeight),
                    format,
                    out charactersFitted,
                    out linesFilled);
                // A width too narrow for even one character would otherwise spin here forever.
                int take = Math.Max(1, Math.Min(charactersFitted, text.Length - index));
                lines.Add(text.Substring(index, take));
                index += take;
            }
        }
    }
}
