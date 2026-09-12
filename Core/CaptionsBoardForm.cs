using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

// Eighth left-dock board (字幕/Captions). Follows the established dock lifecycle exactly like
// ResetSpeedBoardForm (its own maintenanceTimer, dock tab, auto-hide, outside-click collapse), but
// also carries GuardBoardForm's hit-target/action dispatch shape for its three interactive controls
// (context-aware toggle, rounds stepper, model chip) plus a start/stop button and close, all of which
// write to TranslatorControlReader asynchronously and repaint on completion.
//
// TranslatorControlReader is the headless data owner (constructed/started/stopped by WidgetForm, see
// WidgetForm.cs); this board never touches setting.json, translation_history.db or the monitored
// processes directly -- it only calls into the reader, matching "background readers publish cloned
// snapshots; UI code must not mutate reader-owned state or synchronously block on network/file work"
// from this repo's AGENTS.md. The one exception is the reader's own I/O, which this board's
// maintenance timer schedules by calling RefreshIfDue() -- that call, and only that call, is allowed
// to do I/O off the render path, exactly like ResetSpeedBoardForm.RefreshSnapshot calls its
// (cache-only) provider.
internal sealed partial class CaptionsBoardForm : LayeredWidgetFormBase
{
    private const int MaintenanceIntervalMs = 500;
    private const int MinNumContexts = 0;
    private const int MaxNumContexts = 128;
    private const int NumContextsStep = 4;

    private readonly OperationForm owner;
    private readonly Func<TranslatorControlReader> readerProvider;
    private readonly UiFontCache fontCache = new UiFontCache();
    private readonly System.Windows.Forms.Timer maintenanceTimer;
    private readonly List<CaptionsHitTarget> hitTargets = new List<CaptionsHitTarget>();
    private Func<Point> cursorPositionProvider;
    private TranslatorControlSnapshot snapshot = TranslatorControlSnapshot.CreateEmpty();
    private EdgeDockTabForm dockTab;
    private DateTime dockPointerLeftUtc = DateTime.MinValue;
    private DateTime lastInteractionUtc = DateTime.UtcNow;
    private DateTime outsideClickCollapseUtc = DateTime.MinValue;
    private long outsideClickSequence;
    private bool mouseWasInside;
    private bool displaySuspended;
    private bool hiddenForFullscreen;
    private bool restoreAfterFullscreen;
    private string lastVisibleStateSignature = string.Empty;
    private string statusNotice = string.Empty;
    private bool operationRunning;
    private CaptionsHitAction pendingAction = CaptionsHitAction.None;
    // How many article lines the last DrawArticle pass actually painted in the translated half.
    // Purely observational (never read by the draw path itself); it exists so the layout self-test
    // can assert the measured-metrics line budget and the paging maths without re-deriving them.
    private int lastDrawnArticleLineCount;

    internal Action CollapseOtherLeftDockOverlays;

    internal CaptionsBoardForm(OperationForm owner, WidgetSettings settings, Func<TranslatorControlReader> readerProvider)
    {
        this.owner = owner;
        this.readerProvider = readerProvider;
        this.cursorPositionProvider = delegate { return Cursor.Position; };
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        this.SetStyle(ControlStyles.StandardClick | ControlStyles.ResizeRedraw, true);
        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.FormBorderStyle = FormBorderStyle.None;
        this.Text = "字幕";
        this.AccessibleName = "字幕看板";
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        this.Cursor = Cursors.Hand;
        this.Size = GetDesiredSize();
        this.maintenanceTimer = new System.Windows.Forms.Timer();
        this.maintenanceTimer.Interval = MaintenanceIntervalMs;
        this.maintenanceTimer.Tick += OnMaintenanceTick;
        RefreshSnapshot();
    }

    protected override string LayeredWindowLogName
    {
        get { return "CaptionsBoard"; }
    }

    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.CaptionsBoardTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.CaptionsBoardScaleOverridePercent; }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
    }

    private bool IsLeftDocked
    {
        get { return this.owner != null; }
    }

    internal void PreparePresentationState(bool suspended, bool fullscreenHidden)
    {
        this.displaySuspended = suspended;
        this.hiddenForFullscreen = fullscreenHidden;
    }

    internal Size GetDesiredSize()
    {
        // Same fixed 648x400 logical footprint as every other left-dock board (SpecBoardWidth/Height
        // is the shared board canvas size; this board has no width/height settings of its own).
        return new Size(
            Math.Max(1, (int)Math.Round(this.CurrentSettings.SpecBoardWidth * this.LayerScale)),
            Math.Max(1, (int)Math.Round(this.CurrentSettings.SpecBoardHeight * this.LayerScale)));
    }

    internal void ApplyRuntimeSettings(WidgetSettings settings)
    {
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        Size desired = GetDesiredSize();
        if (this.Size != desired)
        {
            this.Size = desired;
        }

        if (this.Visible)
        {
            PositionForDisplay();
            ResetAutoHideClock();
            RenderLayeredWindow();
        }
        else
        {
            InvalidateLayeredRenderBuffer();
        }

        SyncLeftDockTab();
    }

    internal void SyncLeftDockTab()
    {
        if (this.IsDisposed)
        {
            return;
        }

        if (!this.IsLeftDocked)
        {
            DisposeDockTab();
            return;
        }

        Color accent = EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Captions);
        if (this.dockTab == null || this.dockTab.IsDisposed)
        {
            this.dockTab = new EdgeDockTabForm(
                this.CurrentSettings,
                accent,
                BurnInProtection.CaptionsBoardDockTabSalt,
                "CaptionsBoardDockTab",
                EdgeDockTabRole.Captions);
            this.dockTab.HoverEntered += OnDockTabHoverEntered;
            this.dockTab.HoverExited += OnDockTabHoverExited;
            this.dockTab.PollTick += OnDockTabPollTick;
        }
        else
        {
            this.dockTab.ApplyRuntimeSettings(this.CurrentSettings, accent);
        }

        this.dockTab.SetDisplaySuspended(this.displaySuspended);
        this.dockTab.SetHiddenForFullscreen(this.hiddenForFullscreen);
        this.dockTab.ShowTab(ResolveDockTabCenterY());
    }

    private int ResolveDockTabCenterY()
    {
        return LeftDockLayout.ResolveTabCenterY(this.CurrentSettings, EdgeDockTabRole.Captions, this.LayerScale);
    }

    private void OnDockTabHoverEntered(object sender, EventArgs e)
    {
        if (this.IsDisposed || !this.IsLeftDocked || this.Visible ||
            LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen) ||
            OutsideClickDismissalMonitor.ShouldSuppressTabReopen(this.outsideClickCollapseUtc, DateTime.UtcNow))
        {
            return;
        }

        this.outsideClickCollapseUtc = DateTime.MinValue;
        this.dockPointerLeftUtc = DateTime.MinValue;
        ShowBoard();
    }

    private void OnDockTabHoverExited(object sender, EventArgs e)
    {
        this.outsideClickCollapseUtc = DateTime.MinValue;
    }

    private void OnDockTabPollTick(object sender, EventArgs e)
    {
        UpdateOutsideClickDismissal(DateTime.UtcNow);
    }

    private void DisposeDockTab()
    {
        if (this.dockTab == null)
        {
            return;
        }

        try
        {
            this.dockTab.HoverEntered -= OnDockTabHoverEntered;
            this.dockTab.HoverExited -= OnDockTabHoverExited;
            this.dockTab.PollTick -= OnDockTabPollTick;
            this.dockTab.Close();
            this.dockTab.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.dockTab = null;
        }
    }

    internal void ShowBoard()
    {
        if (LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
        {
            return;
        }

        if (this.owner != null)
        {
            this.owner.PrepareForCaptionsBoardOverlayShow();
        }

        Action collapse = this.CollapseOtherLeftDockOverlays;
        if (collapse != null)
        {
            collapse();
        }

        RefreshSnapshot();
        this.outsideClickCollapseUtc = DateTime.MinValue;
        this.outsideClickSequence = OutsideClickDismissalMonitor.ArmConsumer();
        PositionForDisplay();
        if (!this.Visible)
        {
            if (this.owner == null)
            {
                Show();
            }
            else
            {
                Show(this.owner);
            }
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            GetLayeredWidgetInsertAfter(true, this.CurrentSettings.CodexPetZOrderProtectionEnabled),
            this.Left,
            this.Top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
        this.maintenanceTimer.Start();
        ResetAutoHideClock();
        RenderLayeredWindow();
        Program.LogInfo("Captions board expanded from left dock.");
    }

    internal void HideBoard()
    {
        this.maintenanceTimer.Stop();
        // Edit mode must not outlive the board that is the only way out of it: the caption strip
        // takes clicks while editing, so a strip left in that state would silently eat clicks aimed
        // at the video underneath until the user found their way back here. Closing counts as done,
        // which also means the rectangle the user just dragged is kept rather than discarded.
        if (IsCaptionOverlayEditing)
        {
            ToggleCaptionOverlayEditMode();
        }

        DisarmClear();
        if (this.Visible)
        {
            Hide();
        }
    }

    internal void HideBoardIfVisible()
    {
        if (this.Visible)
        {
            HideBoard();
        }
    }

    internal void SetHiddenForFullscreen(bool hidden)
    {
        if (this.hiddenForFullscreen == hidden)
        {
            return;
        }

        this.hiddenForFullscreen = hidden;
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetHiddenForFullscreen(hidden);
            if (!hidden && !this.displaySuspended && this.IsLeftDocked)
            {
                this.dockTab.ShowTab(ResolveDockTabCenterY());
            }
        }

        if (hidden)
        {
            this.restoreAfterFullscreen = this.Visible;
            HideBoard();
        }
        else if (this.restoreAfterFullscreen && !this.displaySuspended)
        {
            this.restoreAfterFullscreen = false;
            ShowBoard();
        }
    }

    internal void PrepareForDisplaySuspend()
    {
        this.displaySuspended = true;
        this.maintenanceTimer.Stop();
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetDisplaySuspended(true);
        }

        ResetDisplayRenderResources();
    }

    internal void RecoverAfterDisplayResume()
    {
        this.displaySuspended = false;
        ResetDisplayRenderResources();
        if (this.dockTab != null && !this.dockTab.IsDisposed)
        {
            this.dockTab.SetDisplaySuspended(false);
        }

        SyncLeftDockTab();
        if (this.Visible)
        {
            this.maintenanceTimer.Start();
            PositionForDisplay();
            RenderLayeredWindow();
        }
    }

    private void PositionForDisplay()
    {
        Rectangle workArea = LeftDockLayout.ResolveWorkArea(this.CurrentSettings);
        Point baseLocation = LeftDockLayout.ResolveBoardBaseLocation(
            this.CurrentSettings,
            EdgeDockTabRole.Captions,
            this.LayerScale,
            this.Size);
        this.Location = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            baseLocation,
            this.Size,
            workArea,
            BurnInProtection.CaptionsBoardSalt);
    }

    private TranslatorControlReader ResolveReader()
    {
        Func<TranslatorControlReader> provider = this.readerProvider;
        return provider == null ? null : provider();
    }

    // Cache-only read of the reader's last published snapshot; the reader's own maintenance-driven
    // RefreshIfDue() is what actually performs I/O (see OnMaintenanceTick below).
    private void RefreshSnapshot()
    {
        TranslatorControlReader reader = ResolveReader();
        this.snapshot = reader == null ? TranslatorControlSnapshot.CreateEmpty() : reader.GetSnapshot();
    }

    private bool UpdateOutsideClickDismissal(DateTime nowUtc)
    {
        if (!this.Visible || !this.IsLeftDocked || this.CurrentSettings == null ||
            !this.CurrentSettings.LeftDockOutsideClickCollapseEnabled)
        {
            return false;
        }

        Point clickPosition;
        DateTime clickUtc;
        if (!OutsideClickDismissalMonitor.TryGetClickAfter(ref this.outsideClickSequence, out clickPosition, out clickUtc))
        {
            return false;
        }

        Rectangle tabBounds = this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible
            ? this.dockTab.Bounds
            : Rectangle.Empty;
        if (!OutsideClickDismissalMonitor.ShouldDismissOutsideClick(true, clickPosition, this.Bounds, tabBounds, Rectangle.Empty))
        {
            return false;
        }

        this.outsideClickCollapseUtc = clickUtc == DateTime.MinValue ? nowUtc : clickUtc;
        HideBoard();
        return true;
    }

    private bool UpdateDockCollapse(DateTime nowUtc)
    {
        if (!this.IsLeftDocked || !this.Visible)
        {
            this.dockPointerLeftUtc = DateTime.MinValue;
            return false;
        }

        Point cursor = this.cursorPositionProvider();
        bool overBoard = this.Bounds.Contains(cursor);
        bool overTab = this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible && this.dockTab.Bounds.Contains(cursor);
        if (overBoard || overTab)
        {
            this.dockPointerLeftUtc = DateTime.MinValue;
            return false;
        }

        if (this.dockPointerLeftUtc == DateTime.MinValue)
        {
            this.dockPointerLeftUtc = nowUtc;
            return false;
        }

        if (nowUtc < this.dockPointerLeftUtc.AddSeconds(this.CurrentSettings.LeftDockCollapseSeconds))
        {
            return false;
        }

        this.dockPointerLeftUtc = DateTime.MinValue;
        HideBoard();
        return true;
    }

    private void OnMaintenanceTick(object sender, EventArgs e)
    {
        RefreshNightScheduleAtExistingTick();
        DateTime now = DateTime.UtcNow;
        if (this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible)
        {
            this.dockTab.RefreshBurnInPosition();
        }

        if (UpdateOutsideClickDismissal(now) || UpdateDockCollapse(now) || !this.Visible)
        {
            return;
        }

        // The board is visible: this is the only place TranslatorControlReader's I/O-performing
        // refresh is driven from, and only while something is actually showing this board -- the
        // reader's own 2000ms self-gate (see TranslatorControlReader.RefreshIfDue) throttles this
        // 500ms tick down to the documented poll interval.
        TranslatorControlReader reader = ResolveReader();
        if (reader != null)
        {
            reader.RefreshIfDue(now, false);
        }

        RefreshSnapshot();
        string signature = BuildVisibleStateSignature(now);
        if (!string.Equals(signature, this.lastVisibleStateSignature, StringComparison.Ordinal))
        {
            this.lastVisibleStateSignature = signature;
            RenderLayeredWindow();
        }

        bool inside = this.Bounds.Contains(this.cursorPositionProvider());
        if (inside)
        {
            this.mouseWasInside = true;
        }
        else if (this.mouseWasInside)
        {
            this.mouseWasInside = false;
            ResetAutoHideClock();
        }

        int autoHideSeconds = this.CurrentSettings.CaptionsBoardAutoHideSeconds;
        if (autoHideSeconds > 0 && !inside && !this.operationRunning && now >= this.lastInteractionUtc.AddSeconds(autoHideSeconds))
        {
            HideBoard();
            return;
        }

        if (ShouldRefreshBurnInPosition())
        {
            PositionForDisplay();
        }
    }

    private void ResetAutoHideClock()
    {
        this.lastInteractionUtc = DateTime.UtcNow;
    }

    // Everything the board actually paints, at the resolution it paints it -- matching
    // GuardBoardForm's BuildVisibleStateSignature shape. The "distance since last success" header
    // text is minute-resolution, so it belongs in the signature too, otherwise a fully idle board
    // showing "3 分钟前" would never repaint and the text would silently go stale.
    private string BuildVisibleStateSignature(DateTime nowUtc)
    {
        StringBuilder builder = new StringBuilder(256);
        builder.Append(this.snapshot.IsRunning ? '1' : '0');
        builder.Append(this.snapshot.GenieXRunning ? '1' : '0');
        builder.Append(this.snapshot.SanitizeProxyRunning ? '1' : '0');
        builder.Append(this.snapshot.LiveCaptionsRunning ? '1' : '0');
        builder.Append(this.snapshot.RestartInProgress ? '1' : '0');
        builder.Append(this.operationRunning ? '1' : '0');
        builder.Append('|').Append((int)this.pendingAction);
        builder.Append('|').Append(this.snapshot.ContextAwareKnown ? '1' : '0').Append(this.snapshot.ContextAware ? '1' : '0');
        builder.Append('|').Append(this.snapshot.NumContextsKnown ? this.snapshot.NumContexts : -1);
        builder.Append('|').Append(this.snapshot.CaptionLanguageKnown ? this.snapshot.CaptionLanguage : "?");
        builder.Append('|').Append(this.snapshot.ModelNameKnown ? this.snapshot.ModelName : string.Empty);
        builder.Append('|').Append(this.snapshot.AvailableModels.Count);
        builder.Append('|').Append(this.snapshot.LastSuccessKnown ? (nowUtc - this.snapshot.LastSuccessLocal.ToUniversalTime()).TotalMinutes.ToString("F0", CultureInfo.InvariantCulture) : "?");
        builder.Append('|').Append(this.snapshot.HistoryDatabaseFound ? '1' : '0');
        builder.Append('|').Append(this.snapshot.RecentHistory.Count);
        for (int i = 0; i < this.snapshot.RecentHistory.Count; i++)
        {
            TranslatorHistoryEntry entry = this.snapshot.RecentHistory[i];
            builder.Append('')
                .Append(entry.IsError ? '1' : '0')
                .Append(entry.TimestampKnown ? entry.TimestampLocal.Ticks : 0L)
                .Append('').Append(entry.SourceText)
                .Append('').Append(entry.TranslatedText);
        }

        builder.Append('|').Append(this.statusNotice);
        return builder.ToString();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        this.fontCache.Dispose();
        ResetDisplayRenderResources();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), Math.Max(3, S(10))))
        {
            Region old = this.Region;
            this.Region = new Region(path);
            if (old != null)
            {
                old.Dispose();
            }
        }

        RenderLayeredWindow();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        this.mouseWasInside = true;
        ResetAutoHideClock();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        this.mouseWasInside = false;
        ResetAutoHideClock();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        ResetAutoHideClock();
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        for (int i = 0; i < this.hitTargets.Count; i++)
        {
            CaptionsHitTarget target = this.hitTargets[i];
            if (target.Bounds.Contains(e.Location))
            {
                ExecuteAction(target.Action, target.Payload);
                return;
            }
        }
    }

    private void ExecuteAction(CaptionsHitAction action, string payload)
    {
        if (action == CaptionsHitAction.Close)
        {
            HideBoard();
            return;
        }

        // The article controls are local state and a settings file write; they never touch the
        // translator, so they are handled ahead of both the busy gate and the reader lookup (which
        // would abort them when no translator is configured at all).
        if (TryExecuteArticleAction(action))
        {
            return;
        }

        if (this.operationRunning)
        {
            // One targeted write/restart at a time -- a second click on any of the three settings
            // controls or the start/stop button while one is already applying is ignored rather than
            // racing a second setting.json write against the first.
            return;
        }

        TranslatorControlReader reader = ResolveReader();
        if (reader == null)
        {
            return;
        }

        switch (action)
        {
            case CaptionsHitAction.ContextAwareToggle:
                RequestSettingChange(
                    action,
                    reader,
                    TranslatorControlReader.SettingChangeKind.ContextAware,
                    !this.snapshot.ContextAware,
                    0,
                    null);
                break;

            case CaptionsHitAction.NumContextsMinus:
                RequestSettingChange(
                    action,
                    reader,
                    TranslatorControlReader.SettingChangeKind.NumContexts,
                    false,
                    ClampNumContexts(this.snapshot.NumContexts - NumContextsStep),
                    null);
                break;

            case CaptionsHitAction.NumContextsPlus:
                RequestSettingChange(
                    action,
                    reader,
                    TranslatorControlReader.SettingChangeKind.NumContexts,
                    false,
                    ClampNumContexts(this.snapshot.NumContexts + NumContextsStep),
                    null);
                break;

            case CaptionsHitAction.ModelSet:
                // The row registers no target for the model already in effect, so reaching here is
                // always a real change -- re-applying the current one would restart the translator
                // and reload the model for nothing.
                if (!string.IsNullOrEmpty(payload))
                {
                    RequestSettingChange(
                        action,
                        reader,
                        TranslatorControlReader.SettingChangeKind.ModelName,
                        false,
                        0,
                        payload);
                }

                break;

            case CaptionsHitAction.ToggleRunning:
                RequestTranslatorToggle(action, reader, !this.snapshot.IsRunning);
                break;

            case CaptionsHitAction.TranslatorStart:
                // The status strip's 翻译器 chip is only clickable while the translator is down, so
                // this is always a start. It deliberately goes through the same
                // RequestTranslatorToggle -> TryToggleTranslatorRunning path the toolbar's
                // start/stop button uses rather than introducing a second launcher.
                RequestTranslatorToggle(action, reader, true);
                break;

            case CaptionsHitAction.GenieXStart:
                RequestServiceStart(action, reader, TranslatorControlReader.MonitoredServiceKind.GenieX);
                break;

            case CaptionsHitAction.SanitizeProxyStart:
                RequestServiceStart(action, reader, TranslatorControlReader.MonitoredServiceKind.SanitizeProxy);
                break;

            case CaptionsHitAction.LiveCaptionsStart:
                RequestServiceStart(action, reader, TranslatorControlReader.MonitoredServiceKind.LiveCaptions);
                break;

            case CaptionsHitAction.CaptionLanguageSet:
                // The row registers no target for the language already in effect, so reaching here
                // always means a real change.
                if (!string.IsNullOrEmpty(payload))
                {
                    RequestCaptionLanguageApply(action, reader, payload);
                }

                break;

            default:
                break;
        }
    }

    private static int ClampNumContexts(int value)
    {
        return Math.Max(MinNumContexts, Math.Min(MaxNumContexts, value));
    }

    // Async apply, mirroring OperationForm.RequestBatteryCareFromGuardBoard's shape: the actual
    // file write + conditional app restart runs on a background Task (it blocks on file and process
    // I/O), and the continuation marshals back to this form's own UI thread via BeginInvoke before
    // touching any UI state or the reader's cache-only surface again.
    private void RequestSettingChange(
        CaptionsHitAction sourceControl,
        TranslatorControlReader reader,
        TranslatorControlReader.SettingChangeKind kind,
        bool boolValue,
        int intValue,
        string stringValue)
    {
        this.operationRunning = true;
        this.pendingAction = sourceControl;
        reader.SetRestartInProgress(true);
        RefreshSnapshot();
        RenderLayeredWindow();

        Task.Run((Action)delegate
        {
            string detail;
            bool success = false;
            try
            {
                success = reader.TryApplySettingChange(kind, boolValue, intValue, stringValue, out detail);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                detail = ex.GetType().Name + ": " + ex.Message;
            }

            CompleteAsyncOperation(reader, success, detail);
        });
    }

    // Starting a down service is the same async shape as a settings write: the click only sets the
    // pending state and repaints, the blocking Process.Start happens on a background Task, and the
    // continuation marshals back through CompleteAsyncOperation. The chip does not wait for the
    // service to finish coming up -- the reader's next 2000ms refresh is what flips the dot green.
    private void RequestServiceStart(
        CaptionsHitAction sourceControl,
        TranslatorControlReader reader,
        TranslatorControlReader.MonitoredServiceKind kind)
    {
        this.operationRunning = true;
        this.pendingAction = sourceControl;
        reader.SetRestartInProgress(true);
        RefreshSnapshot();
        RenderLayeredWindow();

        Task.Run((Action)delegate
        {
            string detail;
            bool success = false;
            try
            {
                success = TranslatorControlReader.TryStartMonitoredService(kind, out detail);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                detail = ex.GetType().Name + ": " + ex.Message;
            }

            CompleteAsyncOperation(reader, success, detail);
        });
    }

    // Registry write + Live Captions/translator restart. Same async contract as every other control
    // on this board; the language tag comes from TranslatorControlReader.CaptionLanguageOptions so
    // the board never encodes language codes itself.
    private void RequestCaptionLanguageApply(CaptionsHitAction sourceControl, TranslatorControlReader reader, string nextLanguage)
    {
        this.operationRunning = true;
        this.pendingAction = sourceControl;
        reader.SetRestartInProgress(true);
        RefreshSnapshot();
        RenderLayeredWindow();

        Task.Run((Action)delegate
        {
            string detail;
            bool success = false;
            try
            {
                success = reader.TryApplyCaptionLanguage(nextLanguage, out detail);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                detail = ex.GetType().Name + ": " + ex.Message;
            }

            CompleteAsyncOperation(reader, success, detail);
        });
    }

    private void RequestTranslatorToggle(CaptionsHitAction sourceControl, TranslatorControlReader reader, bool start)
    {
        this.operationRunning = true;
        this.pendingAction = sourceControl;
        reader.SetRestartInProgress(true);
        RefreshSnapshot();
        RenderLayeredWindow();

        Task.Run((Action)delegate
        {
            string detail;
            bool success = false;
            try
            {
                success = reader.TryToggleTranslatorRunning(start, out detail);
                if (success && start && this.CurrentSettings != null && this.CurrentSettings.TranslatorOverlayAutoOpenEnabled)
                {
                    // Same rule as the keep-alive guard: only when this app started the translator.
                    string overlayDetail;
                    TranslatorOverlayController.TryEnsureOverlayOpen(out overlayDetail);
                    Program.LogInfo("Translator overlay auto-open: " + overlayDetail);
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                detail = ex.GetType().Name + ": " + ex.Message;
            }

            CompleteAsyncOperation(reader, success, detail);
        });
    }

    private void CompleteAsyncOperation(TranslatorControlReader reader, bool success, string detail)
    {
        try
        {
            if (!this.IsDisposed && this.IsHandleCreated)
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    if (this.IsDisposed)
                    {
                        return;
                    }

                    this.operationRunning = false;
                    this.pendingAction = CaptionsHitAction.None;
                    reader.SetRestartInProgress(false);
                    reader.RefreshIfDue(DateTime.UtcNow, true);
                    RefreshSnapshot();
                    this.statusNotice = success ? string.Empty : (detail ?? string.Empty);
                    ResetAutoHideClock();
                    RenderLayeredWindow();
                });
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeDockTab();
            this.maintenanceTimer.Stop();
            this.maintenanceTimer.Tick -= OnMaintenanceTick;
            this.maintenanceTimer.Dispose();
            this.fontCache.Dispose();
        }

        base.Dispose(disposing);
    }

    private enum CaptionsHitAction
    {
        None,
        ContextAwareToggle,
        NumContextsMinus,
        NumContextsPlus,
        ModelSet,
        ToggleRunning,
        Close,
        // Status-strip start actions. Each is registered only while its own service is down, so a
        // chip that already shows green is inert rather than a restart trap.
        GenieXStart,
        SanitizeProxyStart,
        LiveCaptionsStart,
        TranslatorStart,
        CaptionLanguageSet,
        // Article controls. None of these reaches the translator: they page the transcript, write
        // it out, empty it, or change one of this app's own settings.
        ArticlePageUp,
        ArticlePageDown,
        ArticleExport,
        ArticleClear,
        SettledLinesMinus,
        SettledLinesPlus,
        OverlayEditToggle,
        OverlayReset,
        OverlayDisplayToggle,
        OverlayHoverAutoHideToggle
    }

    private struct CaptionsHitTarget
    {
        public Rectangle Bounds;
        public CaptionsHitAction Action;
        // The two "pick one of N" rows use this: the caption-source row carries a language tag and
        // the model row a full model id, and the value has to survive the hit test. Widening the
        // action enum into one member per option instead would push the same string into the type
        // system.
        public string Payload;
    }
}
