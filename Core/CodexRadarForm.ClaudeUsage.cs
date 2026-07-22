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
        return "Codex";
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
        RequestSelectedQuotaUsageRefresh(trigger);

        PublishProjectionStateFromOwner();
    }

    private void RestoreCodexRadarDisplayForCurrentMode(string trigger)
    {
        CodexRadarSoftwareMode mode = GetEffectiveCodexRadarSoftwareMode();
        bool quotaOnlyClaude = mode == CodexRadarSoftwareMode.Claude;
        string modelKey = GetSelectedRadarModelKeyForSoftwareMode(mode);
        RadarDisplayRestoreResult restored = TryRestoreCodexRadarDisplayModeCache(mode, modelKey);

        // Radar and quota restore independently. A quota-known memory state must never stand in for
        // a usable Radar snapshot, otherwise the disk IQ cache is skipped and the current batch is
        // recorded as first-seen with the request time (false refresh marker on family switch).
        if (!quotaOnlyClaude && !restored.RadarRestored)
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
            if (quotaOnlyClaude)
            {
                // Old Claude.* records can still exist in codex-radar-cache.ini after an upgrade.
                // They are deliberately not a compatibility source: CLD owns official quota only.
                this.codexRadarSnapshot = CodexRadarSnapshot.CreateDefault();
            }
            else if (this.codexRadarSnapshot == null)
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
        bool quotaOnlyClaude = mode == CodexRadarSoftwareMode.Claude;
        RadarFamilyRuntimeState state = GetRadarFamilyState(mode);
        string modelKey = quotaOnlyClaude || this.CurrentSettings == null
            ? string.Empty
            : GetSelectedRadarModelKeyForSoftwareMode(mode);
        CodexRadarSnapshot radarSnapshot = null;
        if (!quotaOnlyClaude)
        {
            lock (this.codexRadarStatusLock)
            {
                radarSnapshot = this.codexRadarSnapshot == null ? null : this.codexRadarSnapshot.Clone();
            }
        }

        CodexQuotaSnapshot quotaSnapshot = this.quotaSnapshot == null ? null : this.quotaSnapshot.Clone();
        ServiceHealthState radarHealth = ServiceHealthState.Unknown;
        if (!quotaOnlyClaude)
        {
            lock (this.serviceHealthLock)
            {
                radarHealth = this.radarServiceHealth;
            }
        }

        state.ModelKey = modelKey ?? string.Empty;
        state.RadarSnapshot = radarSnapshot == null
            ? CodexRadarSnapshot.CreateDefault()
            : radarSnapshot.Clone();

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
        bool quotaOnlyClaude = mode == CodexRadarSoftwareMode.Claude;
        modelKey = modelKey ?? string.Empty;
        RadarFamilyRuntimeState state = GetRadarFamilyState(mode);
        if (quotaOnlyClaude)
        {
            modelKey = string.Empty;
            state.ModelKey = string.Empty;
            state.RadarSnapshot = CodexRadarSnapshot.CreateDefault();
            state.RadarSiteHealth = ServiceHealthState.Unknown;
        }

        // Fast path: the family's live runtime snapshot already matches the requested model. Radar is
        // "restored" only when the live snapshot is actually usable; quota-known is reported on its own
        // channel and must not imply the Radar snapshot is present.
        if (string.Equals(state.ModelKey ?? string.Empty, modelKey, StringComparison.OrdinalIgnoreCase))
        {
            bool radarUsable = !quotaOnlyClaude && IsRuntimeRadarSnapshotUsable(state.RadarSnapshot);
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
                RadarSnapshot = quotaOnlyClaude || cached.RadarSnapshot == null ? null : cached.RadarSnapshot.Clone(),
                QuotaSnapshot = cached.QuotaSnapshot == null ? null : cached.QuotaSnapshot.Clone(),
                QuotaSourceKnown = cached.QuotaSourceKnown,
                RadarHealth = cached.RadarHealth,
                UpdatedUtc = cached.UpdatedUtc
            };
        }

        bool radarRestored = false;
        if (!quotaOnlyClaude)
        {
            lock (this.codexRadarStatusLock)
            {
                if (cached.RadarSnapshot != null)
                {
                    this.codexRadarSnapshot = cached.RadarSnapshot;
                    radarRestored = IsRuntimeRadarSnapshotUsable(cached.RadarSnapshot);
                }
            }
        }

        bool quotaRestored = false;
        if (cached.QuotaSnapshot != null)
        {
            this.quotaSnapshot = NormalizeQuotaSnapshot(cached.QuotaSnapshot);
            this.quotaSourceKnown = cached.QuotaSourceKnown;
            quotaRestored = cached.QuotaSourceKnown;
        }

        if (!quotaOnlyClaude)
        {
            lock (this.serviceHealthLock)
            {
                this.radarServiceHealth = this.serviceNetworkAvailable
                    ? cached.RadarHealth
                    : ServiceHealthState.Offline;
            }
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
             snapshot.ModelIqKnown);
    }

    private void RequestSelectedQuotaUsageRefresh(string trigger)
    {
        SoftwareRuntimePresenceSnapshot presence = RefreshSoftwareRuntimePresenceSnapshot(false);
        // Both Radar tiles are permanent consumers. Request each running family independently;
        // the existing schedulers retain their own single-flight and cadence guards.
        if (presence.ClaudeRunning)
        {
            RequestClaudeUsageRefresh(trigger);
        }

        if (presence.CodexRunning)
        {
            RequestCodexProviderUsageRefresh(trigger);
            QuotaRuntimeState codexQuota = GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex);
            codexQuota.LastRefreshUtc = DateTime.MinValue;
            codexQuota.NextInactiveRefreshUtc = DateTime.MinValue;
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
    }

    private void RequestClaudeUsageRefresh(string trigger)
    {
        ClaudeCodeUsageScheduler.RequestRefresh(trigger);
    }

    private void RefreshSelectedQuotaInfoIfNeeded()
    {
        SoftwareRuntimePresenceSnapshot presence = RefreshSoftwareRuntimePresenceSnapshot(false);
        DateTime nowUtc = DateTime.UtcNow;
        bool codexTrendReset = UpdateQuotaBurnObservationClock(
            GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex),
            presence.CodexRunning,
            nowUtc);
        bool claudeTrendReset = UpdateQuotaBurnObservationClock(
            GetQuotaRuntimeState(CodexRadarSoftwareMode.Claude),
            presence.ClaudeRunning,
            nowUtc);
        if (codexTrendReset || claudeTrendReset)
        {
            if (codexTrendReset)
            {
                GetRadarFamilyState(CodexRadarSoftwareMode.Codex).Touch();
            }
            if (claudeTrendReset)
            {
                GetRadarFamilyState(CodexRadarSoftwareMode.Claude).Touch();
            }
            PublishProjectionStateFromOwner();
        }
        if (!presence.AnySupportedAppRunning)
        {
            this.quotaCodexProcessRunning = false;
            return;
        }

        this.quotaCodexProcessRunning = presence.CodexRunning;
        if (presence.CodexRunning)
        {
            RefreshQuotaInfoIfNeeded();
            RefreshCodexProviderUsageIfNeeded();
        }
        else
        {
            UpdateQuotaBurnObservationClock(
                GetQuotaRuntimeState(CodexRadarSoftwareMode.Codex),
                false,
                nowUtc);
        }

        if (presence.ClaudeRunning)
        {
            RefreshClaudeUsageIfNeeded();
        }
    }

    private void RefreshClaudeUsageIfNeeded()
    {
        OwnerOperationLease lease = CaptureOwnerOperation();
        if (lease == null)
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
            // Observe a fault even when this owner generation has already stopped; stale faults are
            // deliberately not persisted as a current-lifetime business event.
            Exception observed = completedTask.Exception == null
                ? null
                : completedTask.Exception.GetBaseException();
            if (!IsOwnerOperationCurrent(lease))
            {
                return;
            }

            if (observed != null)
            {
                TryExecuteOwnerCurrent(lease, delegate { Program.LogException(observed); });
            }

            TryBeginInvokeOwnerCurrent(lease, delegate
            {
                ApplyClaudeUsageSchedulerResult(completedTask, lease);
            });
        });
    }

    private void ApplyClaudeUsageSchedulerResult(
        Task<ClaudeCodeUsageSchedulerOutcome> task,
        OwnerOperationLease lease)
    {
        if (!IsOwnerOperationCurrent(lease))
        {
            return;
        }

        ClaudeCodeUsageSchedulerOutcome outcome = null;
        if (task.Status == TaskStatus.RanToCompletion)
        {
            outcome = task.Result;
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
            ApplyClaudeUsageResultOnUiThread(result.Snapshot, lease);
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

        PublishProjectionStateFromOwner();
    }

    private void ApplyClaudeUsageResultOnUiThread(
        CodexQuotaSnapshot snapshot,
        OwnerOperationLease lease)
    {
        if (snapshot == null || this.IsDisposed || !this.IsHandleCreated)
        {
            return;
        }

        try
        {
            TryBeginInvokeOwnerCurrent(lease, delegate
            {
                if (this.IsDisposed ||
                    this.CurrentSettings == null ||
                    this.CurrentSettings.CodexRadarRandomTestEnabled)
                {
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                GetQuotaRuntimeState(CodexRadarSoftwareMode.Claude).LastRefreshUtc = nowUtc;
                ApplyQuotaSnapshot(
                    CodexRadarSoftwareMode.Claude,
                    snapshot.Clone(),
                    true,
                    true,
                    DateTime.Now,
                    nowUtc,
                    string.IsNullOrWhiteSpace(snapshot.SourceKind) ? "claude_personal" : snapshot.SourceKind);
                PublishProjectionStateFromOwner();
            });
        }
        catch
        {
        }
    }

    private void SetClaudeCodeUsageHealth(ServiceHealthState health, string errorCode, string errorMessage)
    {
        this.claudeCodeUsageHealth = health;
        this.claudeCodeUsageErrorCode = errorCode ?? string.Empty;
        this.claudeCodeUsageErrorMessage = errorMessage ?? string.Empty;
    }

    private static bool IsClaudeSetupTokenMissing(string errorCode)
    {
        return string.Equals(errorCode, "NO_SETUP_TOKEN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "NO_TOKEN", StringComparison.OrdinalIgnoreCase);
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
            IsClaudeSetupTokenMissing(errorCode))
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

    private static ServiceHealthState ConvertClaudeCodeUsageHealth(ClaudeCodeUsageServiceState state)
    {
        switch (state)
        {
            case ClaudeCodeUsageServiceState.Normal:
                return ServiceHealthState.Normal;
            case ClaudeCodeUsageServiceState.Offline:
                return ServiceHealthState.Offline;
            case ClaudeCodeUsageServiceState.Incomplete:
                return ServiceHealthState.Incomplete;
            case ClaudeCodeUsageServiceState.Unavailable:
                return ServiceHealthState.Unavailable;
            case ClaudeCodeUsageServiceState.Unreachable:
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
