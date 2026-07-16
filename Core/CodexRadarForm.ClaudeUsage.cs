using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class CodexRadarForm
{
    private CodexQuotaSnapshot claudeQuotaSnapshot = CodexQuotaSnapshot.CreateDefault();
    private bool claudeQuotaSourceKnown;
    private ServiceHealthState claudeCodeUsageHealth = ServiceHealthState.Unknown;
    private string claudeCodeUsageErrorCode = string.Empty;
    private string claudeCodeUsageErrorMessage = string.Empty;
    private CodexRadarSoftwareMode effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
    private DateTime lastCodexRadarSoftwareAutoCheckUtc = DateTime.MinValue;

    private sealed class ClaudeCodeUsageResult
    {
        public bool TokenConfigured { get; set; }
        public bool Success { get; set; }
        public bool RateLimited { get; set; }
        public CodexQuotaSnapshot Snapshot { get; set; }
        public ServiceHealthState Health { get; set; }
        public string ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
    }

    private CodexRadarSoftwareMode GetEffectiveCodexRadarSoftwareMode()
    {
        return this.effectiveCodexRadarSoftwareMode;
    }

    private SoftwareRuntimePresenceSnapshot RefreshSoftwareRuntimePresenceSnapshot(bool force)
    {
        WidgetPerformanceMode mode = this.CurrentSettings == null
            ? WidgetPerformanceMode.BatterySaver
            : WidgetSettings.GetEffectivePerformanceMode(this.CurrentSettings.PerformanceMode);
        SoftwareRuntimePresenceSnapshot snapshot = SoftwareRuntimePresence.GetSnapshot(mode, force);
        this.softwareRuntimePresenceSnapshot = snapshot ?? SoftwareRuntimePresenceSnapshot.Empty();
        return this.softwareRuntimePresenceSnapshot;
    }

    private SoftwareRuntimePresenceSnapshot GetLastSoftwareRuntimePresenceSnapshot()
    {
        return this.softwareRuntimePresenceSnapshot ?? SoftwareRuntimePresenceSnapshot.Empty();
    }

    private string GetCodexRadarServiceFamilyDisplayText()
    {
        return GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude ? "Claude" : "Codex";
    }

    private void UpdateEffectiveCodexRadarSoftwareModeIfNeeded()
    {
        if (this.CurrentSettings == null)
        {
            return;
        }

        if (this.CurrentSettings.CodexRadarSoftwareMode != CodexRadarSoftwareMode.Auto)
        {
            RefreshSoftwareRuntimePresenceSnapshot(false);
            if (this.effectiveCodexRadarSoftwareMode != this.CurrentSettings.CodexRadarSoftwareMode &&
                UpdateEffectiveCodexRadarSoftwareMode(true))
            {
                SwitchCodexRadarSoftwareFamily("软件设置切换");
            }

            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (this.lastCodexRadarSoftwareAutoCheckUtc != DateTime.MinValue &&
            (nowUtc - this.lastCodexRadarSoftwareAutoCheckUtc).TotalSeconds < GetCodexRadarSoftwareAutoDetectSeconds())
        {
            return;
        }

        if (UpdateEffectiveCodexRadarSoftwareMode(false))
        {
            SwitchCodexRadarSoftwareFamily("前台软件切换");
        }
    }

    private bool UpdateEffectiveCodexRadarSoftwareMode(bool force)
    {
        CodexRadarSoftwareMode next = ResolveCodexRadarSoftwareMode(force);
        this.lastCodexRadarSoftwareAutoCheckUtc = DateTime.UtcNow;
        if (!force && next == this.effectiveCodexRadarSoftwareMode)
        {
            return false;
        }

        CodexRadarSoftwareMode previous = this.effectiveCodexRadarSoftwareMode;
        bool changed = next != previous;
        if (changed)
        {
            CacheCodexRadarDisplayMode(previous);
        }

        this.effectiveCodexRadarSoftwareMode = next;
        if (changed)
        {
            SoftwareRuntimePresenceSnapshot presence = this.softwareRuntimePresenceSnapshot ??
                SoftwareRuntimePresenceSnapshot.Empty();
            Program.LogInfo(
                "Radar software family changed. Previous=" +
                previous +
                " Next=" +
                next +
                " CodexRunning=" +
                presence.CodexRunning +
                " ClaudeRunning=" +
                presence.ClaudeRunning);
        }

        return changed;
    }

    private CodexRadarSoftwareMode ResolveCodexRadarSoftwareMode(bool force)
    {
        CodexRadarSoftwareMode configured = this.CurrentSettings == null
            ? CodexRadarSoftwareMode.Auto
            : this.CurrentSettings.CodexRadarSoftwareMode;
        SoftwareRuntimePresenceSnapshot presence = RefreshSoftwareRuntimePresenceSnapshot(force);
        bool foregroundDetected = false;
        CodexRadarSoftwareMode detected;
        if (ShouldDetectForegroundForSoftwareMode(configured, presence) &&
            TryDetectForegroundCodexRadarSoftware(out detected))
        {
            foregroundDetected = true;
        }
        else
        {
            detected = CodexRadarSoftwareMode.Codex;
        }

        RadarSoftwareModeDecision decision = this.radarSoftwareModeController.Resolve(new RadarSoftwareModeInput
        {
            ConfiguredMode = configured,
            PreviousEffectiveMode = this.effectiveCodexRadarSoftwareMode,
            Presence = presence,
            ForegroundDetected = foregroundDetected,
            ForegroundMode = detected
        });
        return decision.EffectiveMode;
    }

    private static bool ShouldDetectForegroundForSoftwareMode(
        CodexRadarSoftwareMode configured,
        SoftwareRuntimePresenceSnapshot presence)
    {
        return RadarSoftwareModeController.ShouldDetectForeground(configured, presence);
    }

    private static CodexRadarSoftwareMode ResolveCodexRadarSoftwareModeFromSignals(
        CodexRadarSoftwareMode configured,
        CodexRadarSoftwareMode previousEffective,
        SoftwareRuntimePresenceSnapshot presence,
        bool foregroundDetected,
        CodexRadarSoftwareMode foregroundMode)
    {
        return RadarSoftwareModeController.ResolveEffectiveMode(
            configured,
            previousEffective,
            presence,
            foregroundDetected,
            foregroundMode);
    }

    private static CodexRadarSoftwareMode NormalizeEffectiveSoftwareMode(CodexRadarSoftwareMode mode)
    {
        return RadarSoftwareModeController.NormalizeEffectiveSoftwareMode(mode);
    }

    private int GetCodexRadarSoftwareAutoDetectSeconds()
    {
        WidgetPerformanceMode mode = this.CurrentSettings == null
            ? WidgetPerformanceMode.BatterySaver
            : WidgetSettings.GetEffectivePerformanceMode(this.CurrentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 2;
        }

        if (mode == WidgetPerformanceMode.Balanced)
        {
            return 5;
        }

        return 10;
    }

    private static bool TryDetectForegroundCodexRadarSoftware(out CodexRadarSoftwareMode mode)
    {
        mode = CodexRadarSoftwareMode.Codex;
        IntPtr handle = NativeMethods.GetForegroundWindowHandle();
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        string title = NativeMethods.GetWindowTitleForDisplay(handle);
        string processName = string.Empty;
        string executablePath = string.Empty;
        int processId;
        if (NativeMethods.TryGetWindowProcessId(handle, out processId))
        {
            if (processId == Process.GetCurrentProcess().Id)
            {
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    processName = process.ProcessName ?? string.Empty;
                }

                executablePath = NativeMethods.TryGetProcessImagePath(processId);
            }
            catch
            {
                processName = string.Empty;
                executablePath = string.Empty;
            }
        }

        return SoftwareRuntimePresence.TryClassifySoftwareProcess(
            processName,
            executablePath,
            title,
            out mode);
    }

    private void SwitchCodexRadarSoftwareFamily(string trigger)
    {
        RestoreCodexRadarDisplayForCurrentMode(trigger);
        ResetCodexApiServiceAlertDebounceForDisplayContextSwitch();
        RequestSelectedQuotaUsageRefresh(trigger);

        RenderLayeredWindow();
    }

    private void RestoreCodexRadarDisplayForCurrentMode(string trigger)
    {
        CodexRadarSoftwareMode mode = GetEffectiveCodexRadarSoftwareMode();
        string modelKey = GetSelectedRadarModelKeyForSoftwareMode(mode);
        RadarDisplayRestoreResult restored = TryRestoreCodexRadarDisplayModeCache(mode, modelKey);

        // Radar and quota restore independently. A quota-known memory state must never stand in for
        // a usable Radar snapshot, otherwise the disk IQ cache is skipped and the current batch is
        // recorded as first-seen with the request time (false refresh marker on family switch).
        if (!restored.RadarRestored)
        {
            CodexRadarSnapshot cachedSnapshot = LoadCodexRadarCache(mode, modelKey);
            if (cachedSnapshot != null)
            {
                lock (this.codexRadarStatusLock)
                {
                    this.codexRadarSnapshot = cachedSnapshot;
                }
            }
        }

        if (!restored.QuotaRestored)
        {
            // During a software/model switch a missing target cache must not blank the UI for a
            // single frame. Keep the previous visible quota until the selected provider returns.
            LoadSelectedQuotaCacheIntoDisplay(true);
        }

        lock (this.codexRadarStatusLock)
        {
            if (this.codexRadarSnapshot == null)
            {
                this.codexRadarSnapshot = CodexRadarSnapshot.CreateDefault();
            }

            this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
            this.codexRadarStatusRefreshTrigger = string.IsNullOrWhiteSpace(trigger) ? "软件切换" : trigger.Trim();
        }

        InitializeQuotaReadDeltaTracking(this.quotaSnapshot, this.quotaSourceKnown);
    }

    private void CacheCodexRadarDisplayMode(CodexRadarSoftwareMode mode)
    {
        mode = NormalizeEffectiveSoftwareMode(mode);
        RadarFamilyRuntimeState state = GetRadarFamilyState(mode);
        string modelKey = this.CurrentSettings == null
            ? string.Empty
            : GetSelectedRadarModelKeyForSoftwareMode(mode);
        CodexRadarSnapshot radarSnapshot;
        lock (this.codexRadarStatusLock)
        {
            radarSnapshot = this.codexRadarSnapshot == null ? null : this.codexRadarSnapshot.Clone();
        }

        CodexQuotaSnapshot quotaSnapshot = this.quotaSnapshot == null ? null : this.quotaSnapshot.Clone();
        ServiceHealthState radarHealth;
        lock (this.serviceHealthLock)
        {
            radarHealth = this.radarServiceHealth;
        }

        state.ModelKey = modelKey ?? string.Empty;
        if (radarSnapshot != null)
        {
            state.RadarSnapshot = radarSnapshot.Clone();
        }

        if (quotaSnapshot != null)
        {
            state.Quota.Snapshot = NormalizeQuotaSnapshot(quotaSnapshot);
            state.Quota.SourceKnown = this.quotaSourceKnown;
        }

        state.RadarSiteHealth = radarHealth;
        state.Touch();

        lock (this.codexRadarDisplayModeCacheLock)
        {
            this.codexRadarDisplayModeCache[mode] = new CodexRadarDisplayModeCache
            {
                ModelKey = modelKey ?? string.Empty,
                RadarSnapshot = radarSnapshot,
                QuotaSnapshot = quotaSnapshot,
                QuotaSourceKnown = this.quotaSourceKnown,
                RadarHealth = radarHealth,
                UpdatedUtc = DateTime.UtcNow
            };
        }
    }

    // Radar and quota restore are reported independently so a family switch that only carried quota
    // into memory still loads the disk IQ snapshot. Conflating the two (the old "quota known ==
    // radar restored" shortcut) produced false refresh markers on Claude->Codex switches.
    private struct RadarDisplayRestoreResult
    {
        public bool RadarRestored;
        public bool QuotaRestored;
    }

    private RadarDisplayRestoreResult TryRestoreCodexRadarDisplayModeCache(CodexRadarSoftwareMode mode, string modelKey)
    {
        mode = NormalizeEffectiveSoftwareMode(mode);
        modelKey = modelKey ?? string.Empty;
        RadarFamilyRuntimeState state = GetRadarFamilyState(mode);

        // Fast path: the family's live runtime snapshot already matches the requested model. Radar is
        // "restored" only when the live snapshot is actually usable; quota-known is reported on its own
        // channel and must not imply the Radar snapshot is present.
        if (string.Equals(state.ModelKey ?? string.Empty, modelKey, StringComparison.OrdinalIgnoreCase))
        {
            bool radarUsable = IsRuntimeRadarSnapshotUsable(state.RadarSnapshot);
            if (radarUsable || state.Quota.SourceKnown)
            {
                return new RadarDisplayRestoreResult
                {
                    RadarRestored = radarUsable,
                    QuotaRestored = state.Quota.SourceKnown
                };
            }
        }

        CodexRadarDisplayModeCache cached;
        lock (this.codexRadarDisplayModeCacheLock)
        {
            if (!this.codexRadarDisplayModeCache.TryGetValue(mode, out cached) ||
                cached == null ||
                !string.Equals(cached.ModelKey ?? string.Empty, modelKey, StringComparison.OrdinalIgnoreCase))
            {
                return default(RadarDisplayRestoreResult);
            }

            cached = new CodexRadarDisplayModeCache
            {
                ModelKey = cached.ModelKey,
                RadarSnapshot = cached.RadarSnapshot == null ? null : cached.RadarSnapshot.Clone(),
                QuotaSnapshot = cached.QuotaSnapshot == null ? null : cached.QuotaSnapshot.Clone(),
                QuotaSourceKnown = cached.QuotaSourceKnown,
                RadarHealth = cached.RadarHealth,
                UpdatedUtc = cached.UpdatedUtc
            };
        }

        bool radarRestored = false;
        lock (this.codexRadarStatusLock)
        {
            if (cached.RadarSnapshot != null)
            {
                this.codexRadarSnapshot = cached.RadarSnapshot;
                radarRestored = IsRuntimeRadarSnapshotUsable(cached.RadarSnapshot);
            }
        }

        bool quotaRestored = false;
        if (cached.QuotaSnapshot != null)
        {
            this.quotaSnapshot = NormalizeQuotaSnapshot(cached.QuotaSnapshot);
            this.quotaSourceKnown = cached.QuotaSourceKnown;
            quotaRestored = cached.QuotaSourceKnown;
        }

        lock (this.serviceHealthLock)
        {
            this.radarServiceHealth = this.serviceNetworkAvailable
                ? cached.RadarHealth
                : ServiceHealthState.Offline;
        }

        return new RadarDisplayRestoreResult
        {
            RadarRestored = radarRestored,
            QuotaRestored = quotaRestored
        };
    }

    private static bool IsRuntimeRadarSnapshotUsable(CodexRadarSnapshot snapshot)
    {
        return snapshot != null &&
            (snapshot.CheckedAtKnown ||
             snapshot.ModelIqKnown ||
             snapshot.CommunityRatingKnown ||
             IsCodexQuotaRadarKnown(snapshot));
    }

    private void RequestSelectedQuotaUsageRefresh(string trigger)
    {
        SoftwareRuntimePresenceSnapshot presence = RefreshSoftwareRuntimePresenceSnapshot(false);
        CodexRadarSoftwareMode mode = GetEffectiveCodexRadarSoftwareMode();
        SelectedQuotaRefreshTarget target = ResolveSelectedQuotaRefreshTarget(mode, presence);
        if (target == SelectedQuotaRefreshTarget.Claude)
        {
            RequestClaudeUsageRefresh(trigger);
            return;
        }

        if (target == SelectedQuotaRefreshTarget.Codex)
        {
            RequestCodexProviderUsageRefresh(trigger);
            this.lastQuotaRefreshUtc = DateTime.MinValue;
            this.nextQuotaInactiveRefreshUtc = DateTime.MinValue;
        }
    }

    private static SelectedQuotaRefreshTarget ResolveSelectedQuotaRefreshTarget(
        CodexRadarSoftwareMode effectiveMode,
        SoftwareRuntimePresenceSnapshot presence)
    {
        return RadarSoftwareModeController.ResolveSelectedQuotaRefreshTarget(effectiveMode, presence);
    }

    internal static void RunSoftwareModeGateSelfTest()
    {
        SoftwareRuntimePresence.RunSelfTest();
        RadarSoftwareModeController.RunSelfTest();

        ClaudeRadarSnapshot claudeSnapshot = ClaudeRadarSnapshot.CreateDefault();
        claudeSnapshot.Known = true;
        claudeSnapshot.CheckedAtLocal = new DateTime(2026, 7, 5, 13, 30, 0);
        claudeSnapshot.DataState = ClaudeRadarServiceState.Normal;
        claudeSnapshot.SelectedModelKey = "m1";
        claudeSnapshot.SelectedModelName = "Opus 4.8 high";
        claudeSnapshot.SelectedModel = new ClaudeRadarModelMetric
        {
            Known = true,
            SourceKey = "m1",
            Name = "Opus 4.8 high",
            LatestAtKnown = true,
            LatestAtUtc = new DateTime(2026, 7, 5, 4, 20, 0, DateTimeKind.Utc),
            IqScore = 119,
            Passed = 8,
            ValidTasks = 10,
            TokenEfficiencyPercent = 123,
            TimeEfficiencyPercent = 88,
            StatusText = "常态"
        };
        claudeSnapshot.Community = new ClaudeRadarCommunitySnapshot
        {
            Known = true,
            RatingKey = "opus48_high",
            Label = "Opus 4.8 high",
            Average = 7.5,
            Count = 42,
            UpdatedAtUtc = new DateTime(2026, 7, 5, 4, 0, 0, DateTimeKind.Utc)
        };
        CodexRadarSnapshot sharedSnapshot = ConvertClaudeRadarSnapshotForSharedWindow(claudeSnapshot, true);
        if (sharedSnapshot == null ||
            !sharedSnapshot.ModelIqKnown ||
            sharedSnapshot.ModelIqPassRatePercent != 119 ||
            sharedSnapshot.ModelIqTokenEfficiencyPercent != 123 ||
            sharedSnapshot.ModelIqTimeEfficiencyPercent != 88 ||
            sharedSnapshot.CheckedAtLocal != claudeSnapshot.SelectedModel.LatestAtUtc.ToLocalTime() ||
            !sharedSnapshot.CommunityRatingKnown ||
            !string.Equals(sharedSnapshot.CommunityRatingModelId, "opus48_high", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Codex Radar software mode self-test failed: Claude shared snapshot conversion.");
        }
    }

    private void RequestClaudeUsageRefresh(string trigger)
    {
        ClaudeCodeUsageScheduler.RequestRefresh(trigger);
    }

    private void RefreshSelectedQuotaInfoIfNeeded()
    {
        SoftwareRuntimePresenceSnapshot presence = RefreshSoftwareRuntimePresenceSnapshot(false);
        if (!presence.AnySupportedAppRunning)
        {
            this.quotaCodexProcessRunning = false;
            return;
        }

        if (GetEffectiveCodexRadarSoftwareMode() == CodexRadarSoftwareMode.Claude)
        {
            this.quotaCodexProcessRunning = presence.CodexRunning;
            if (!presence.ClaudeRunning)
            {
                return;
            }

            RefreshClaudeUsageIfNeeded();
            return;
        }

        if (!presence.CodexRunning)
        {
            this.quotaCodexProcessRunning = false;
            return;
        }

        RefreshQuotaInfoIfNeeded();
        RefreshCodexProviderUsageIfNeeded();
    }

    private void RefreshClaudeUsageIfNeeded()
    {
        if (GetEffectiveCodexRadarSoftwareMode() != CodexRadarSoftwareMode.Claude)
        {
            return;
        }

        Task<ClaudeCodeUsageSchedulerOutcome> task;
        if (!ClaudeCodeUsageScheduler.TryStartOrJoin("codex_radar", this.CurrentSettings, "定时间隔", out task))
        {
            return;
        }

        task.ContinueWith(delegate(Task<ClaudeCodeUsageSchedulerOutcome> completedTask)
        {
            if (this.IsDisposed || !this.IsHandleCreated)
            {
                return;
            }

            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    ApplyClaudeUsageSchedulerResult(completedTask);
                });
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private void ApplyClaudeUsageSchedulerResult(Task<ClaudeCodeUsageSchedulerOutcome> task)
    {
        ClaudeCodeUsageSchedulerOutcome outcome = null;
        if (task.Status == TaskStatus.RanToCompletion)
        {
            outcome = task.Result;
        }
        else if (task.Exception != null)
        {
            Program.LogException(task.Exception.GetBaseException());
        }

        ClaudeCodeUsageResult result = ConvertClaudeCodeUsageReadResult(outcome == null ? null : outcome.Result);
        if (result.Success && result.Snapshot != null)
        {
            this.claudeQuotaSnapshot = NormalizeQuotaSnapshot(result.Snapshot);
            this.claudeQuotaSourceKnown = true;
        }

        this.claudeCodeUsageHealth = result.Health;
        this.claudeCodeUsageErrorCode = result.ErrorCode ?? string.Empty;
        this.claudeCodeUsageErrorMessage = result.ErrorMessage ?? string.Empty;
        bool sourceKnownAfter = this.claudeQuotaSourceKnown;

        if (result.Success && result.Snapshot != null)
        {
            ApplyClaudeUsageResultOnUiThread(result.Snapshot);
        }

        NetworkCheckHistoryLogger.LogCompleted(
            "codex_radar",
            "claude_code_usage",
            outcome == null ? "定时间隔" : outcome.Trigger,
            result.Success ? "正常" : EmptyFallback(result.ErrorMessage, result.Health.ToString()),
            result.Success,
            (int)Math.Min(int.MaxValue, outcome == null ? 0 : outcome.ElapsedMilliseconds),
            new Dictionary<string, object>
            {
                { "health", result.Health.ToString() },
                { "error_code", result.ErrorCode ?? string.Empty },
                { "token_configured", result.TokenConfigured },
                { "rate_limited", result.RateLimited },
                { "source_known_after", sourceKnownAfter }
            });

        RenderLayeredWindow();
    }

    private void ApplyClaudeUsageResultOnUiThread(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        try
        {
            this.BeginInvoke((MethodInvoker)delegate
            {
                if (this.IsDisposed ||
                    this.CurrentSettings == null ||
                    this.CurrentSettings.CodexRadarRandomTestEnabled ||
                    GetEffectiveCodexRadarSoftwareMode() != CodexRadarSoftwareMode.Claude)
                {
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                this.lastQuotaRefreshUtc = nowUtc;
                ApplyQuotaSnapshot(
                    CodexRadarSoftwareMode.Claude,
                    snapshot.Clone(),
                    true,
                    true,
                    DateTime.Now,
                    nowUtc,
                    string.IsNullOrWhiteSpace(snapshot.SourceKind) ? "claude_personal" : snapshot.SourceKind);
                RenderLayeredWindow();
            });
        }
        catch
        {
        }
    }

    private bool IsClaudeQuotaDisplayAvailable()
    {
        return this.quotaSourceKnown ||
            this.claudeQuotaSourceKnown ||
            (ClaudeCodeUsageScheduler.IsRequestRunning && this.claudeCodeUsageHealth != ServiceHealthState.Offline);
    }

    private void SetClaudeCodeUsageHealth(ServiceHealthState health, string errorCode, string errorMessage)
    {
        this.claudeCodeUsageHealth = health;
        this.claudeCodeUsageErrorCode = errorCode ?? string.Empty;
        this.claudeCodeUsageErrorMessage = errorMessage ?? string.Empty;
    }

    private void AddClaudeCodeUsageAlertCandidate(List<CodexConnectionAlertCandidate> candidates)
    {
        if (GetEffectiveCodexRadarSoftwareMode() != CodexRadarSoftwareMode.Claude)
        {
            return;
        }

        bool checking;
        bool known;
        ServiceHealthState health;
        string errorCode;
        string errorMessage;
        checking = ClaudeCodeUsageScheduler.IsRequestRunning;
        known = this.claudeQuotaSourceKnown;
        health = this.claudeCodeUsageHealth;
        errorCode = this.claudeCodeUsageErrorCode ?? string.Empty;
        errorMessage = this.claudeCodeUsageErrorMessage ?? string.Empty;

        if (checking && !known)
        {
            candidates.Add(new CodexConnectionAlertCandidate
            {
                Key = "claude_code_usage:checking",
                Name = "Claude",
                Reason = "检测中",
                Color = DesignTokens.Colors.Warning
            });
            return;
        }

        if (health == ServiceHealthState.Normal || health == ServiceHealthState.Unknown)
        {
            return;
        }

        if ((QuotaRingPresentation.IsSetupTokenMissing(errorCode) || IsClaudeCodeStatusLineCacheMissing(errorCode)) &&
            (this.quotaSourceKnown || known))
        {
            return;
        }

        candidates.Add(new CodexConnectionAlertCandidate
        {
            Key = "claude_code_usage:" + health.ToString() + ":" + errorCode,
            Name = "Claude",
            Reason = string.IsNullOrWhiteSpace(errorMessage) ? GetServiceHealthAlertReason(health) : errorMessage,
            Color = GetClaudeCodeUsageAlertColor(health, errorCode)
        });
    }

    private static Color GetClaudeCodeUsageAlertColor(ServiceHealthState health, string errorCode)
    {
        if (health == ServiceHealthState.Offline)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        if (string.Equals(errorCode, "429", StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.Colors.Warning;
        }

        if (string.Equals(errorCode, "401", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "403", StringComparison.OrdinalIgnoreCase) ||
            QuotaRingPresentation.IsSetupTokenMissing(errorCode))
        {
            return DesignTokens.Colors.WarningDeep;
        }

        if (IsClaudeCodeStatusLineCacheMissing(errorCode))
        {
            return DesignTokens.Colors.Warning;
        }

        if (health == ServiceHealthState.Unreachable)
        {
            return DesignTokens.Colors.DangerStrong;
        }

        return GetServiceHealthAlertColor(health);
    }

    private static ClaudeCodeUsageResult ConvertClaudeCodeUsageReadResult(ClaudeCodeUsageReadResult result)
    {
        if (result == null)
        {
            return BuildClaudeCodeUsageError(false, ServiceHealthState.Unreachable, "NET", "无法连接");
        }

        CodexQuotaSnapshot snapshot = null;
        if (result.Success && result.Snapshot != null)
        {
            snapshot = CodexQuotaSnapshot.CreateDefault();
            snapshot.FiveHourPercent = ClampPercent(result.Snapshot.FiveHourPercent);
            snapshot.FiveHourResetLocal = result.Snapshot.FiveHourResetLocal;
            snapshot.FiveHourResetKnown = result.Snapshot.FiveHourResetKnown;
            snapshot.WeeklyPercent = ClampPercent(result.Snapshot.WeeklyPercent);
            snapshot.WeeklyResetLocal = result.Snapshot.WeeklyResetLocal;
            snapshot.WeeklyResetKnown = result.Snapshot.WeeklyResetKnown;
            snapshot.SourceUpdatedUtc = result.Snapshot.SourceUpdatedUtc;
            snapshot.SourceUpdatedKnown = result.Snapshot.SourceUpdatedKnown;
            MarkQuotaSnapshotSource(snapshot, "claude_personal");
        }

        return new ClaudeCodeUsageResult
        {
            TokenConfigured = result.TokenConfigured,
            Success = result.Success && snapshot != null,
            RateLimited = result.RateLimited,
            Snapshot = snapshot,
            Health = ConvertClaudeCodeUsageHealth(result.State),
            ErrorCode = result.ErrorCode ?? string.Empty,
            ErrorMessage = result.ErrorMessage ?? string.Empty
        };
    }

    private static ServiceHealthState ConvertClaudeCodeUsageHealth(ClaudeRadarServiceState state)
    {
        switch (state)
        {
            case ClaudeRadarServiceState.Normal:
                return ServiceHealthState.Normal;
            case ClaudeRadarServiceState.Offline:
                return ServiceHealthState.Offline;
            case ClaudeRadarServiceState.Incomplete:
                return ServiceHealthState.Incomplete;
            case ClaudeRadarServiceState.Unavailable:
                return ServiceHealthState.Unavailable;
            case ClaudeRadarServiceState.Unreachable:
                return ServiceHealthState.Unreachable;
            default:
                return ServiceHealthState.Unknown;
        }
    }

    private static ClaudeCodeUsageResult BuildClaudeCodeUsageError(
        bool tokenConfigured,
        ServiceHealthState health,
        string errorCode,
        string message)
    {
        return new ClaudeCodeUsageResult
        {
            TokenConfigured = tokenConfigured,
            Success = false,
            RateLimited = string.Equals(errorCode, "429", StringComparison.OrdinalIgnoreCase),
            Snapshot = null,
            Health = health,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = message ?? string.Empty
        };
    }

    private static bool IsClaudeCodeStatusLineCacheMissing(string errorCode)
    {
        return string.Equals(errorCode, "NO_STATUSLINE_CACHE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "STATUSLINE_STALE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "STATUSLINE_CUSTOM", StringComparison.OrdinalIgnoreCase);
    }
}
