using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

// GUARD board: the fourth member of the left-edge dock queue, alongside Spec, Codex tasks and the
// docked network panel. It merges the three CodexSleepGuard power guards with the three controls
// from the operation panel's 特殊设置 window, and adds the ASUS battery-care pause countdown.
//
// Layout is scheme D: a countdown ring column on the left carrying everything that changes with
// time (display-guard remaining, offline elapsed, battery-care pause remaining), and a card column
// on the right carrying the six controls. The board borrows the Spec board's footprint the same way
// the docked network panel does, so every dock member expands to an identical rectangle.
internal sealed partial class GuardBoardForm : LayeredWidgetFormBase
{
    private const int MaintenanceIntervalMs = 500;

    private readonly OperationForm owner;
    private readonly UiFontCache fontCache = new UiFontCache();
    private readonly System.Windows.Forms.Timer maintenanceTimer;
    private readonly List<GuardHitTarget> hitTargets = new List<GuardHitTarget>();
    private readonly GuardRuntime runtime;
    private Func<Point> cursorPositionProvider;
    private EdgeDockTabForm dockTab;
    private DateTime dockPointerLeftUtc = DateTime.MinValue;
    private DateTime lastInteractionUtc = DateTime.UtcNow;
    private long outsideClickSequence;
    private DateTime outsideClickCollapseUtc = DateTime.MinValue;
    private bool mouseWasInside;
    private bool displaySuspended;
    private bool hiddenForFullscreen;
    private bool restoreAfterFullscreen;
    private string statusNotice = string.Empty;
    private string lastVisibleStateSignature = string.Empty;

    // Mutual exclusion with the other left-dock boards, wired by the owner the same way the
    // network panel's CollapseOtherLeftDockOverlays is.
    internal Action CollapseOtherLeftDockOverlays;

    internal GuardBoardForm(OperationForm owner, WidgetSettings settings, Func<bool?> onlineProvider)
    {
        this.owner = owner;
        this.cursorPositionProvider = delegate { return Cursor.Position; };
        this.runtime = new GuardRuntime(onlineProvider);
        this.CurrentSettings = settings.Clone();
        this.CurrentSettings.Normalize();
        ApplicationIcon.ApplyTo(this);
        this.SetStyle(ControlStyles.StandardClick, true);
        InitializeLayerScaleFromCurrentDpi();
        ApplyLayerScaleFromSettings(this.CurrentSettings);
        this.FormBorderStyle = FormBorderStyle.None;
        this.Text = "Guard Board";
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.Black;
        this.Cursor = Cursors.Hand;
        this.Size = GetDesiredSize();
        this.runtime.LoadFromSettings(this.CurrentSettings, DateTime.UtcNow);
        this.maintenanceTimer = new System.Windows.Forms.Timer();
        this.maintenanceTimer.Interval = MaintenanceIntervalMs;
        this.maintenanceTimer.Tick += OnMaintenanceTick;
        // GuardRuntime must outlive the tab and the expanded surface. Starting the timer here keeps
        // display expiry, offline protection and battery countdown active even when left docking is
        // disabled and the board remains hidden.
        this.maintenanceTimer.Start();
    }

    internal GuardRuntime Runtime
    {
        get { return this.runtime; }
    }

    protected override string LayeredWindowLogName
    {
        get { return "GuardBoard"; }
    }

    // GUARD shares the Spec board footprint, but owns visual overrides so changing one board cannot
    // silently restyle another board or its dock tab.
    protected override int WindowTransparencyOverridePercent
    {
        get { return this.CurrentSettings.GuardBoardTransparencyOverridePercent; }
    }

    protected override int WindowScaleOverridePercent
    {
        get { return this.CurrentSettings.GuardBoardScaleOverridePercent; }
    }

    protected override bool CanRenderLayeredWindow()
    {
        return !this.displaySuspended;
    }

    internal void PreparePresentationState(bool suspended, bool fullscreenHidden)
    {
        this.displaySuspended = suspended;
        this.hiddenForFullscreen = fullscreenHidden;
    }

    private bool IsLeftDocked
    {
        get { return this.owner != null; }
    }

    private Size GetDesiredSize()
    {
        // Same formula as SpecBoardForm.GetDesiredSize and NetworkMonitorForm.GetDockedSize: the
        // stored values are 96-DPI logical, content paints at LayerScale, so the physical window has
        // to grow by the same factor or every element is double-crowded on a high-DPI display.
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

    // Persisting immediately after every mutation is deliberate: an unattended machine can be put to
    // sleep or lose power at any moment, and a guard whose armed state only reaches disk at shutdown
    // is a guard that quietly disappears exactly when it mattered.
    private void PersistRuntimeState()
    {
        if (this.CurrentSettings == null)
        {
            return;
        }

        this.runtime.SaveToSettings(this.CurrentSettings);
        if (this.owner != null)
        {
            this.owner.PersistGuardStateFromBoard(this.CurrentSettings);
        }
    }

    // The dock tab is the board's only always-visible surface, so it is created as soon as the owner
    // builds the (hidden) board at startup and torn down when the setting is turned off.
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

        if (this.dockTab == null || this.dockTab.IsDisposed)
        {
            this.dockTab = new EdgeDockTabForm(
                this.CurrentSettings,
                GetDockTabAccent(),
                BurnInProtection.GuardBoardDockTabSalt,
                "GuardBoardDockTab",
                EdgeDockTabRole.Guard);
            this.dockTab.HoverEntered += OnDockTabHoverEntered;
            this.dockTab.HoverExited += OnDockTabHoverExited;
            this.dockTab.PollTick += OnDockTabPollTick;
        }
        else
        {
            this.dockTab.ApplyRuntimeSettings(this.CurrentSettings, GetDockTabAccent());
        }

        this.dockTab.SetDisplaySuspended(this.displaySuspended);
        this.dockTab.SetHiddenForFullscreen(this.hiddenForFullscreen);
        this.dockTab.ShowTab(ResolveDockTabCenterY());
        this.maintenanceTimer.Start();
    }

    // Queue identity is positional and fixed: GUARD is always the purple fourth tab. Guard state
    // remains on the expanded board; the protected collapsed trapezoid is gray and its purple
    // arrow is the persistent identity cue.
    private Color GetDockTabAccent()
    {
        return EdgeDockTabForm.ResolveQueueAccent(EdgeDockTabRole.Guard);
    }

    // Auto sentinel puts this tab at the bottom of the queue: network sits at -3, Spec at -1 and the
    // Codex task board at +1, so +3 clears all three without moving them.
    private int ResolveDockTabCenterY()
    {
        return LeftDockLayout.ResolveTabCenterY(
            this.CurrentSettings,
            EdgeDockTabRole.Guard,
            this.LayerScale);
    }

    private void OnDockTabHoverEntered(object sender, EventArgs e)
    {
        if (this.IsDisposed || !this.IsLeftDocked || this.Visible ||
            LeftDockLayout.IsPresentationBlocked(this.displaySuspended, this.hiddenForFullscreen))
        {
            return;
        }

        if (OutsideClickDismissalMonitor.ShouldSuppressTabReopen(this.outsideClickCollapseUtc, DateTime.UtcNow))
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
            this.owner.PrepareForGuardBoardOverlayShow();
        }

        Action collapse = this.CollapseOtherLeftDockOverlays;
        if (collapse != null)
        {
            collapse();
        }

        this.outsideClickCollapseUtc = DateTime.MinValue;
        this.outsideClickSequence = OutsideClickDismissalMonitor.ArmConsumer();
        ApplyRuntimeSettings(this.CurrentSettings);
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
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER | NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
        ResetAutoHideClock();
        RenderLayeredWindow();
    }

    internal void HideBoard()
    {
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

        if (!this.hiddenForFullscreen && this.restoreAfterFullscreen)
        {
            this.restoreAfterFullscreen = false;
            ShowBoard();
        }

        SyncLeftDockTab();
        if (this.Visible)
        {
            PositionForDisplay();
            RenderLayeredWindow();
        }
    }

    internal void NotifySystemResume()
    {
        if (this.runtime.OnSystemResume(DateTime.UtcNow))
        {
            if (this.Visible)
            {
                RenderLayeredWindow();
            }
        }
    }

    private void PositionForDisplay()
    {
        if (this.IsLeftDocked)
        {
            PositionAtLeftDock();
            return;
        }

        PositionNearOperationPanel();
    }

    // Docked: flush against the left edge but offset by the tab width so the tab stays visible and
    // the pointer can travel tab -> board without crossing a gap (which would collapse the board).
    private void PositionAtLeftDock()
    {
        if (this.CurrentSettings == null)
        {
            return;
        }

        Rectangle workArea = LeftDockLayout.ResolveWorkArea(this.CurrentSettings);
        Point baseLocation = LeftDockLayout.ResolveBoardBaseLocation(
            this.CurrentSettings,
            EdgeDockTabRole.Guard,
            this.LayerScale,
            this.Size);
        this.Location = BurnInProtection.ApplyRuntimeOffsetWithPinnedX(
            baseLocation,
            this.Size,
            workArea,
            BurnInProtection.GuardBoardSalt);
    }

    private void PositionNearOperationPanel()
    {
        if (this.owner == null || this.CurrentSettings == null)
        {
            return;
        }

        Rectangle workArea = Screen.FromControl(this.owner).WorkingArea;
        int left = this.owner.Left;
        int top = this.owner.Top - S(10) - this.Height;
        left = Math.Max(workArea.Left, Math.Min(left, Math.Max(workArea.Left, workArea.Right - this.Width)));
        top = Math.Max(workArea.Top, Math.Min(top, Math.Max(workArea.Top, workArea.Bottom - this.Height)));
        this.Location = BurnInProtection.ApplyRuntimeOffset(new Point(left, top), this.Size, workArea, BurnInProtection.GuardBoardSalt);
    }

    private bool UpdateOutsideClickDismissal(DateTime nowUtc)
    {
        bool enabled = this.Visible &&
            this.CurrentSettings != null &&
            this.CurrentSettings.LeftDockOutsideClickCollapseEnabled &&
            this.IsLeftDocked;
        if (!enabled)
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
        if (!OutsideClickDismissalMonitor.ShouldDismissOutsideClick(
            true,
            clickPosition,
            this.Bounds,
            tabBounds,
            Rectangle.Empty))
        {
            return false;
        }

        this.outsideClickCollapseUtc = clickUtc == DateTime.MinValue ? nowUtc : clickUtc;
        Program.LogInfo("GuardBoard outside click collapsed transient board.");
        HideBoard();
        return true;
    }

    // Docked boards collapse on their own once the pointer leaves both the board and its tab, on the
    // short LeftDockCollapseSeconds peek timer rather than the much longer idle timeout.
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

        // The state machine keeps running while the board is collapsed: the whole point of the
        // guards is that they hold overnight. Tab identity is fixed purple and does not trigger
        // extra repaints when guard state changes.
        bool stateChanged = this.runtime.Tick(now);
        if (stateChanged)
        {
            PersistRuntimeState();
        }

        if (this.dockTab != null && !this.dockTab.IsDisposed && this.dockTab.Visible)
        {
            this.dockTab.RefreshBurnInPosition();
        }

        if (UpdateOutsideClickDismissal(now) || UpdateDockCollapse(now))
        {
            return;
        }

        if (!this.Visible)
        {
            return;
        }

        // Repaint only when something the user can actually see has changed, exactly like the other
        // three dock boards. An unconditional repaint here was the reason this board alone visibly
        // reacted to the pointer leaving: every RenderLayeredWindow re-evaluates the layered
        // surface's alpha and the burn-in/night passes, so while the other boards kept their last
        // bitmap through the one-second collapse delay, this one re-composited under the freshly
        // enabled hidden mode and flashed before it collapsed.
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

        int autoHideSeconds = this.CurrentSettings.GuardBoardAutoHideSeconds;
        if (autoHideSeconds > 0 && !inside && now >= this.lastInteractionUtc.AddSeconds(autoHideSeconds))
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

    // Everything the board actually paints, at the resolution it paints it. Countdowns are reduced
    // to whole seconds because that is the precision the text carries — comparing raw ticks would
    // report a change on every single maintenance tick and defeat the purpose.
    private string BuildVisibleStateSignature(DateTime nowUtc)
    {
        StringBuilder builder = new StringBuilder(160);
        builder.Append(this.runtime.SleepGuardEnabled ? '1' : '0');
        builder.Append(this.runtime.DisplayGuardActive ? '1' : '0');
        builder.Append(this.runtime.Online ? '1' : '0');
        builder.Append(this.runtime.BatteryCarePauseActive ? '1' : '0');
        AppendSeconds(builder, this.runtime.GetSleepGuardElapsed(nowUtc));
        AppendSeconds(builder, this.runtime.GetDisplayGuardRemaining(nowUtc));
        AppendSeconds(builder, this.runtime.GetOfflineElapsed(nowUtc));
        AppendSeconds(builder, this.runtime.GetBatteryCarePauseRemaining(nowUtc));
        builder.Append('|').Append(this.runtime.DisplayGuardMinutes.ToString(CultureInfo.InvariantCulture));
        builder.Append('|').Append(this.runtime.OfflineThresholdMinutes.ToString(CultureInfo.InvariantCulture));
        // The header clock is minute resolution, so it belongs in the signature too: without it a
        // fully idle board would never repaint and its clock would freeze at the opening minute.
        builder.Append('|').Append(DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture));
        builder.Append('|').Append(this.statusNotice);
        builder.Append('|').Append(this.runtime.LastActionDetail);
        if (this.CurrentSettings != null)
        {
            builder.Append(this.CurrentSettings.AiRequestProtectionManualBlockEnabled ? '1' : '0');
            builder.Append(this.CurrentSettings.CodexQuotaPlanEnabled ? '1' : '0');
        }

        // The power-request info block renders these. The three flags derive from the armed guards
        // already in the signature, but the AC line changes independently (plug / unplug) and its
        // caveat text must repaint when it does.
        builder.Append('|').Append(this.runtime.SystemPowerRequestActive ? '1' : '0');
        builder.Append(this.runtime.ExecutionPowerRequestActive ? '1' : '0');
        builder.Append(this.runtime.DisplayPowerRequestActive ? '1' : '0');
        builder.Append('|').Append(((int)ResolveAcState()).ToString(CultureInfo.InvariantCulture));

        return builder.ToString();
    }

    private static void AppendSeconds(StringBuilder builder, TimeSpan value)
    {
        builder.Append('|').Append(((long)value.TotalSeconds).ToString(CultureInfo.InvariantCulture));
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
        if (!this.mouseWasInside)
        {
            this.mouseWasInside = true;
            ResetAutoHideClock();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        this.mouseWasInside = false;
        ResetAutoHideClock();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
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
            GuardHitTarget target = this.hitTargets[i];
            if (target.Bounds.Contains(e.Location))
            {
                ExecuteGuardAction(target.Action);
                return;
            }
        }
    }

    private void ExecuteGuardAction(GuardHitAction action)
    {
        DateTime now = DateTime.UtcNow;
        bool persist = true;
        switch (action)
        {
            case GuardHitAction.SleepToggle:
                this.runtime.SetSleepGuard(!this.runtime.SleepGuardEnabled);
                this.statusNotice = this.runtime.SleepGuardEnabled ? "睡眠防护已开启。" : "睡眠防护已关闭。";
                break;

            case GuardHitAction.DisplayToggle:
                if (this.runtime.DisplayGuardActive)
                {
                    this.runtime.StopDisplayGuard();
                    this.statusNotice = "亮屏计时已停止。";
                }
                else
                {
                    this.runtime.StartDisplayGuard(now);
                    this.statusNotice = "亮屏计时已开始。";
                }

                break;

            case GuardHitAction.DisplayMinus:
                this.runtime.SetDisplayGuardMinutes(StepValue(WidgetSettings.GuardDisplayMinuteSteps, this.runtime.DisplayGuardMinutes, -1));
                break;

            case GuardHitAction.DisplayPlus:
                this.runtime.SetDisplayGuardMinutes(StepValue(WidgetSettings.GuardDisplayMinuteSteps, this.runtime.DisplayGuardMinutes, 1));
                break;

            case GuardHitAction.OfflineMinus:
                this.runtime.SetOfflineThresholdMinutes(StepValue(WidgetSettings.GuardOfflineThresholdMinuteSteps, this.runtime.OfflineThresholdMinutes, -1));
                break;

            case GuardHitAction.OfflinePlus:
                this.runtime.SetOfflineThresholdMinutes(StepValue(WidgetSettings.GuardOfflineThresholdMinuteSteps, this.runtime.OfflineThresholdMinutes, 1));
                break;

            case GuardHitAction.BatteryToggle:
                RequestBatteryCareToggle();
                persist = false;
                break;

            case GuardHitAction.AiBlockToggle:
                ToggleAiBlock();
                persist = false;
                break;

            case GuardHitAction.QuotaPlanToggle:
                ToggleQuotaPlan();
                persist = false;
                break;

            case GuardHitAction.CtfRestart:
                RequestCtfRestart();
                persist = false;
                break;

            case GuardHitAction.Close:
                HideBoard();
                return;

            case GuardHitAction.Panel:
                if (this.owner != null)
                {
                    this.owner.OpenSettingsFromGuardBoard();
                }

                return;

            default:
                return;
        }

        if (persist)
        {
            PersistRuntimeState();
        }

        RenderLayeredWindow();
    }

    // Steps along a fixed ladder and stops at the ends rather than wrapping: wrapping from 8 hours
    // straight back to 30 minutes on one extra click is the kind of surprise that silently ends a
    // long guard.
    internal static int StepValue(int[] steps, int current, int direction)
    {
        if (steps == null || steps.Length == 0)
        {
            return current;
        }

        int index = Array.IndexOf(steps, current);
        if (index < 0)
        {
            return steps[0];
        }

        int next = index + direction;
        if (next < 0 || next >= steps.Length)
        {
            return current;
        }

        return steps[next];
    }

    internal void ObserveBatteryPercent(bool known, int percent, DateTime nowUtc)
    {
        if (this.runtime.ObserveBatteryPercent(known, percent, nowUtc))
        {
            PersistRuntimeState();
        }
    }

    internal void RecordBatteryCareCommand(bool pause, DateTime requestedUtc)
    {
        if (pause) this.runtime.NoteBatteryCarePaused(requestedUtc);
        else this.runtime.NoteBatteryCareRestored();
        PersistRuntimeState();
    }

    private void RequestBatteryCareToggle()
    {
        if (this.owner == null)
        {
            return;
        }

        bool pauseActive = this.runtime.BatteryCarePauseActive;
        this.statusNotice = pauseActive ? "正在恢复 80% 充电上限…" : "正在暂停电池保护…";
        RenderLayeredWindow();

        // OperationForm owns the MyASUS invocation and reports back only after the command actually
        // left the process, so the 24h clock never starts on a command that failed to send.
        this.owner.RequestBatteryCareFromGuardBoard(!pauseActive, delegate(bool success, string detail)
        {
            if (this.IsDisposed)
            {
                return;
            }

            if (success)
            {
                if (pauseActive)
                {
                    this.statusNotice = "已发送恢复 80% 充电上限指令。";
                }
                else
                {
                    this.statusNotice = "已发送暂停指令，按点击时间记录 24 小时。";
                }
            }
            else
            {
                this.statusNotice = (pauseActive ? "恢复充电上限失败：" : "暂停电池保护失败：") + detail;
            }

            RenderLayeredWindow();
        });
    }

    private void ToggleAiBlock()
    {
        if (this.owner == null)
        {
            return;
        }

        bool next = !this.CurrentSettings.AiRequestProtectionManualBlockEnabled;
        if (this.owner.SetAiRequestBlockingFromGuardBoard(next))
        {
            this.CurrentSettings.AiRequestProtectionManualBlockEnabled = next;
            this.statusNotice = next ? "链接阻断已开启。" : "链接阻断已关闭。";
        }
        else
        {
            this.statusNotice = "链接阻断切换失败，详见日志。";
        }

        RenderLayeredWindow();
    }

    private void ToggleQuotaPlan()
    {
        if (this.owner == null)
        {
            return;
        }

        bool next = !this.CurrentSettings.CodexQuotaPlanEnabled;
        if (this.owner.SetCodexQuotaPlanFromGuardBoard(next))
        {
            this.CurrentSettings.CodexQuotaPlanEnabled = next;
            this.statusNotice = next ? "额度计划已启用。" : "额度计划已关闭。";
        }
        else
        {
            this.statusNotice = "额度计划切换失败，详见日志。";
        }

        RenderLayeredWindow();
    }

    private void RequestCtfRestart()
    {
        if (this.owner == null)
        {
            return;
        }

        this.statusNotice = "正在等待管理员授权并重启 CTF…";
        RenderLayeredWindow();
        this.owner.RequestCtfmonRestartFromGuardBoard(delegate(bool success, string detail)
        {
            if (this.IsDisposed)
            {
                return;
            }

            this.statusNotice = success ? "CTF 输入服务已重启。" : "CTF 重启失败或已取消，详见日志。";
            RenderLayeredWindow();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Releasing the execution-state flags here matters: leaving ES_SYSTEM_REQUIRED set
            // against a thread that is going away leaves Windows holding a requirement nothing owns.
            this.runtime.ReleaseAll();
            DisposeDockTab();
            this.maintenanceTimer.Stop();
            this.maintenanceTimer.Tick -= OnMaintenanceTick;
            this.maintenanceTimer.Dispose();
            this.fontCache.Dispose();
        }

        base.Dispose(disposing);
    }

    private enum GuardHitAction
    {
        None,
        SleepToggle,
        DisplayToggle,
        DisplayMinus,
        DisplayPlus,
        OfflineMinus,
        OfflinePlus,
        BatteryToggle,
        AiBlockToggle,
        QuotaPlanToggle,
        CtfRestart,
        Close,
        Panel
    }

    private struct GuardHitTarget
    {
        public Rectangle Bounds;
        public GuardHitAction Action;
    }
}
