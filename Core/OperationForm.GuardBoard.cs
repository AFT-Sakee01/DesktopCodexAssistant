using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

// Ownership and command routing for the GUARD board, the fourth left-dock member.
//
// The board draws itself and owns the guard state machine; everything that has to touch the rest of
// the app (MyASUS battery care, the AI-request block, the quota plan, the elevated ctfmon restart,
// settings persistence) is funnelled through this file so the board never reaches past its owner.
internal sealed partial class OperationForm
{
    private GuardBoardForm guardBoardForm;
    private int guardCtfmonRestartRunning;

    // Set by WidgetForm, which owns the network monitor. Null means "no reading available", and
    // GuardRuntime then falls back to NetworkInterface.GetIsNetworkAvailable rather than guessing.
    internal Func<bool?> GuardNetworkOnlineProvider;

    // Read-only peek for the main widget's guard strip: never creates the board. Guard state is
    // persisted in settings and restored when the board is constructed at startup; null only
    // means the board has not been constructed yet.
    internal GuardRuntime PeekGuardRuntime()
    {
        if (this.guardBoardForm == null || this.guardBoardForm.IsDisposed)
        {
            return null;
        }

        return this.guardBoardForm.Runtime;
    }

    internal GuardBoardForm EnsureGuardBoardForm()
    {
        if (this.guardBoardForm == null || this.guardBoardForm.IsDisposed)
        {
            this.guardBoardForm = new GuardBoardForm(
                this,
                this.CurrentSettings,
                delegate { return ResolveGuardNetworkOnline(); });
            this.guardBoardForm.CollapseOtherLeftDockOverlays = delegate
            {
                HideNetworkDockedPanelIfVisible();
            };
        }

        this.guardBoardForm.PreparePresentationState(this.displaySuspended, this.hiddenForFullscreen);
        this.guardBoardForm.ApplyRuntimeSettings(this.CurrentSettings);
        return this.guardBoardForm;
    }

    private bool? ResolveGuardNetworkOnline()
    {
        Func<bool?> provider = this.GuardNetworkOnlineProvider;
        if (provider == null)
        {
            return null;
        }

        try
        {
            return provider();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return null;
        }
    }

    internal void ToggleGuardBoardWindow()
    {
        GuardBoardForm form = EnsureGuardBoardForm();
        if (form.Visible)
        {
            form.HideBoard();
            return;
        }

        form.ShowBoard();
    }

    internal void HideGuardBoardIfVisible()
    {
        if (this.guardBoardForm != null && !this.guardBoardForm.IsDisposed)
        {
            this.guardBoardForm.HideBoardIfVisible();
        }
    }

    internal void SyncGuardBoardDockTab()
    {
        if (this.CurrentSettings == null)
        {
            return;
        }

        // The hidden board owns the execution-state runtime even when its tab is disabled. Always
        // construct it, then let SyncLeftDockTab decide whether the tab itself should exist.
        EnsureGuardBoardForm().SyncLeftDockTab();
    }

    internal void SetGuardBoardHiddenForFullscreen(bool hidden)
    {
        if (this.guardBoardForm != null && !this.guardBoardForm.IsDisposed)
        {
            this.guardBoardForm.SetHiddenForFullscreen(hidden);
        }
    }

    internal void PrepareGuardBoardForDisplaySuspend()
    {
        if (this.guardBoardForm != null && !this.guardBoardForm.IsDisposed)
        {
            this.guardBoardForm.PrepareForDisplaySuspend();
        }
    }

    internal void RecoverGuardBoardAfterDisplayResume()
    {
        if (this.guardBoardForm != null && !this.guardBoardForm.IsDisposed)
        {
            this.guardBoardForm.RecoverAfterDisplayResume();
        }
    }

    internal void NotifyGuardBoardSystemResume()
    {
        if (this.CurrentSettings != null)
        {
            EnsureGuardBoardForm().NotifySystemResume();
        }
    }

    // Expanding the guard board collapses the other dock members, matching what each of them
    // does to the others.
    internal void PrepareForGuardBoardOverlayShow()
    {
        if (this.radialMenuOpen)
        {
            CloseRadialMenu();
        }

        HideLauncherTrioIfVisible();
        CollapseLeftDockBoardsExcept(LeftDockBoardKind.Guard);
    }

    // The board mutates guard state on its own clone. WidgetForm owns the committed settings
    // snapshot, so only it may merge these six runtime fields and persist them.
    internal void PersistGuardStateFromBoard(WidgetSettings boardSettings)
    {
        if (boardSettings == null || this.CurrentSettings == null)
        {
            return;
        }

        this.CurrentSettings.GuardSleepEnabled = boardSettings.GuardSleepEnabled;
        this.CurrentSettings.GuardSleepSinceUtcTicks = boardSettings.GuardSleepSinceUtcTicks;
        this.CurrentSettings.GuardDisplayMinutes = boardSettings.GuardDisplayMinutes;
        this.CurrentSettings.GuardOfflineThresholdMinutes = boardSettings.GuardOfflineThresholdMinutes;
        this.CurrentSettings.GuardDisplayUntilUtcTicks = boardSettings.GuardDisplayUntilUtcTicks;
        this.CurrentSettings.GuardBatteryCarePauseUntilUtcTicks = boardSettings.GuardBatteryCarePauseUntilUtcTicks;

        Action<WidgetSettings> persist = this.persistGuardStateAction;
        if (persist == null)
        {
            return;
        }

        try
        {
            persist(boardSettings.Clone());
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    internal void OpenSettingsFromGuardBoard()
    {
        if (this.openSettingsAction == null)
        {
            return;
        }

        try
        {
            this.openSettingsAction();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    internal bool SetAiRequestBlockingFromGuardBoard(bool enabled)
    {
        if (this.setAiBlockAction == null)
        {
            return false;
        }

        try
        {
            return this.setAiBlockAction(enabled);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    internal bool SetCodexQuotaPlanFromGuardBoard(bool enabled)
    {
        if (this.setQuotaPlanAction == null)
        {
            return false;
        }

        try
        {
            return this.setQuotaPlanAction(enabled);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    // Battery care runs the same MyASUS invocations as the operation panel's two radial buttons, but
    // reports completion back so the board can start (or clear) its 24h clock only on success.
    internal void RequestBatteryCareFromGuardBoard(bool pause, Action<bool, string> completion)
    {
        // Pause and restore are opposite writes to the same vendor setting. Letting one start while
        // the other is still in flight makes the final 80% state depend on process timing.
        bool alreadyRunning = this.batteryCarePauseRunning || this.batteryLimitRestoreRunning;
        if (alreadyRunning)
        {
            InvokeGuardCompletion(completion, false, "指令已在执行。");
            return;
        }

        if (pause)
        {
            this.batteryCarePauseRunning = true;
        }
        else
        {
            this.batteryLimitRestoreRunning = true;
        }

        Program.LogInfo("Guard board battery care requested. Pause=" + pause.ToString());
        RenderLayeredWindow();
        Task.Run((Action)delegate
        {
            string detail = string.Empty;
            bool success = false;
            try
            {
                success = pause
                    ? TryInvokeAsusBatteryCarePause(out detail)
                    : TryInvokeAsusBatteryLimitRestore(out detail);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                detail = ex.GetType().Name + ": " + ex.Message;
            }

            Program.LogInfo(
                "Guard board battery care finished. Pause=" +
                pause.ToString() +
                ", Success=" +
                success.ToString() +
                ", Detail=" +
                detail);

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

                        if (pause)
                        {
                            this.batteryCarePauseRunning = false;
                        }
                        else
                        {
                            this.batteryLimitRestoreRunning = false;
                        }

                        RenderLayeredWindow();
                        if (completion != null)
                        {
                            completion(success, detail);
                        }
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    internal void RequestCtfmonRestartFromGuardBoard(Action<bool, string> completion)
    {
        if (Interlocked.CompareExchange(ref this.guardCtfmonRestartRunning, 1, 0) != 0)
        {
            InvokeGuardCompletion(completion, false, "CTF 重启已在执行。");
            return;
        }

        string correlationId = Guid.NewGuid().ToString("N");
        Stopwatch stopwatch = Stopwatch.StartNew();
        Program.LogInfo("guard_board_ctfmon_restart_requested correlation_id=" + correlationId);

        Task.Run((Action)delegate
        {
            bool success = false;
            string detail = string.Empty;
            try
            {
                success = Program.RunElevatedCtfmonRestartHelper(correlationId, out detail);
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                detail = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                stopwatch.Stop();
                Program.LogInfo(
                    "guard_board_ctfmon_restart_completed correlation_id=" +
                    correlationId +
                    ", success=" +
                    success.ToString() +
                    ", elapsed_ms=" +
                    stopwatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    ", detail=" +
                    detail);
                Interlocked.Exchange(ref this.guardCtfmonRestartRunning, 0);
            }

            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    bool completed = success;
                    string completedDetail = detail;
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed && completion != null)
                        {
                            completion(completed, completedDetail);
                        }
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private void InvokeGuardCompletion(Action<bool, string> completion, bool success, string detail)
    {
        if (completion == null)
        {
            return;
        }

        try
        {
            completion(success, detail);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void DisposeGuardBoardForm()
    {
        if (this.guardBoardForm == null)
        {
            return;
        }

        try
        {
            this.guardBoardForm.Close();
            this.guardBoardForm.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.guardBoardForm = null;
        }
    }
}
