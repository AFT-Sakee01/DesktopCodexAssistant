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

    private enum SelectedQuotaRefreshTarget
    {
        None,
        Codex,
        Claude
    }

    private CodexRadarSoftwareMode GetEffectiveCodexRadarSoftwareMode()
    {
        return this.effectiveCodexRadarSoftwareMode;
    }

    private SoftwareRuntimePresenceSnapshot RefreshSoftwareRuntimePresenceSnapshot(bool force)
    {
        WidgetPerformanceMode mode = this.currentSettings == null
            ? WidgetPerformanceMode.BatterySaver
            : WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
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
        if (this.currentSettings == null)
        {
            return;
        }

        if (this.currentSettings.CodexRadarSoftwareMode != CodexRadarSoftwareMode.Auto)
        {
            RefreshSoftwareRuntimePresenceSnapshot(false);
            if (this.effectiveCodexRadarSoftwareMode != this.currentSettings.CodexRadarSoftwareMode &&
                UpdateEffectiveCodexRadarSoftwareMode(true))
            {
                HandleCodexRadarSoftwareChanged("软件设置切换");
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
            HandleCodexRadarSoftwareChanged("前台软件切换");
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
        return changed;
    }

    private CodexRadarSoftwareMode ResolveCodexRadarSoftwareMode(bool force)
    {
        CodexRadarSoftwareMode configured = this.currentSettings == null
            ? CodexRadarSoftwareMode.Auto
            : this.currentSettings.CodexRadarSoftwareMode;
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

        return ResolveCodexRadarSoftwareModeFromSignals(
            configured,
            this.effectiveCodexRadarSoftwareMode,
            presence,
            foregroundDetected,
            detected);
    }

    private static bool ShouldDetectForegroundForSoftwareMode(
        CodexRadarSoftwareMode configured,
        SoftwareRuntimePresenceSnapshot presence)
    {
        return configured == CodexRadarSoftwareMode.Auto &&
            presence != null &&
            presence.CodexRunning &&
            presence.ClaudeRunning;
    }

    private static CodexRadarSoftwareMode ResolveCodexRadarSoftwareModeFromSignals(
        CodexRadarSoftwareMode configured,
        CodexRadarSoftwareMode previousEffective,
        SoftwareRuntimePresenceSnapshot presence,
        bool foregroundDetected,
        CodexRadarSoftwareMode foregroundMode)
    {
        if (configured == CodexRadarSoftwareMode.Codex ||
            configured == CodexRadarSoftwareMode.Claude)
        {
            return configured;
        }

        SoftwareRuntimePresenceSnapshot localPresence = presence ?? SoftwareRuntimePresenceSnapshot.Empty();
        if (!localPresence.AnySupportedAppRunning)
        {
            return NormalizeEffectiveSoftwareMode(previousEffective);
        }

        if (localPresence.CodexRunning && !localPresence.ClaudeRunning)
        {
            return CodexRadarSoftwareMode.Codex;
        }

        if (localPresence.ClaudeRunning && !localPresence.CodexRunning)
        {
            return CodexRadarSoftwareMode.Claude;
        }

        if (foregroundDetected)
        {
            return foregroundMode == CodexRadarSoftwareMode.Claude
                ? CodexRadarSoftwareMode.Claude
                : CodexRadarSoftwareMode.Codex;
        }

        return NormalizeEffectiveSoftwareMode(previousEffective);
    }

    private static CodexRadarSoftwareMode NormalizeEffectiveSoftwareMode(CodexRadarSoftwareMode mode)
    {
        return mode == CodexRadarSoftwareMode.Claude
            ? CodexRadarSoftwareMode.Claude
            : CodexRadarSoftwareMode.Codex;
    }

    private int GetCodexRadarSoftwareAutoDetectSeconds()
    {
        WidgetPerformanceMode mode = this.currentSettings == null
            ? WidgetPerformanceMode.BatterySaver
            : WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
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
            }
            catch
            {
                processName = string.Empty;
            }
        }

        string combined = (processName + " " + title).ToLowerInvariant();
        bool claude = combined.IndexOf("claude", StringComparison.Ordinal) >= 0;
        bool codex = combined.IndexOf("codex", StringComparison.Ordinal) >= 0;
        if (claude)
        {
            mode = CodexRadarSoftwareMode.Claude;
            return true;
        }

        if (codex)
        {
            mode = CodexRadarSoftwareMode.Codex;
            return true;
        }

        return false;
    }

    private void HandleCodexRadarSoftwareChanged(string trigger)
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
        bool restoredFromMemory = TryRestoreCodexRadarDisplayModeCache(mode, modelKey);
        if (!restoredFromMemory)
        {
            CodexRadarSnapshot cachedSnapshot = LoadCodexRadarCache(mode, modelKey);
            if (cachedSnapshot != null)
            {
                lock (this.codexRadarStatusLock)
                {
                    this.codexRadarSnapshot = cachedSnapshot;
                }
            }

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
        string modelKey = this.currentSettings == null
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

    private bool TryRestoreCodexRadarDisplayModeCache(CodexRadarSoftwareMode mode, string modelKey)
    {
        mode = NormalizeEffectiveSoftwareMode(mode);
        modelKey = modelKey ?? string.Empty;
        CodexRadarDisplayModeCache cached;
        lock (this.codexRadarDisplayModeCacheLock)
        {
            if (!this.codexRadarDisplayModeCache.TryGetValue(mode, out cached) ||
                cached == null ||
                !string.Equals(cached.ModelKey ?? string.Empty, modelKey, StringComparison.OrdinalIgnoreCase))
            {
                return false;
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

        lock (this.codexRadarStatusLock)
        {
            if (cached.RadarSnapshot != null)
            {
                this.codexRadarSnapshot = cached.RadarSnapshot;
            }
        }

        if (cached.QuotaSnapshot != null)
        {
            this.quotaSnapshot = NormalizeQuotaSnapshot(cached.QuotaSnapshot);
            this.quotaSourceKnown = cached.QuotaSourceKnown;
        }

        lock (this.serviceHealthLock)
        {
            this.radarServiceHealth = this.serviceNetworkAvailable
                ? cached.RadarHealth
                : ServiceHealthState.Offline;
        }

        return cached.RadarSnapshot != null || cached.QuotaSnapshot != null;
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
        SoftwareRuntimePresenceSnapshot localPresence = presence ?? SoftwareRuntimePresenceSnapshot.Empty();
        if (!localPresence.AnySupportedAppRunning)
        {
            return SelectedQuotaRefreshTarget.None;
        }

        if (effectiveMode == CodexRadarSoftwareMode.Claude)
        {
            return localPresence.ClaudeRunning
                ? SelectedQuotaRefreshTarget.Claude
                : SelectedQuotaRefreshTarget.None;
        }

        return localPresence.CodexRunning
            ? SelectedQuotaRefreshTarget.Codex
            : SelectedQuotaRefreshTarget.None;
    }

    internal static void RunSoftwareModeGateSelfTest()
    {
        SoftwareRuntimePresenceSnapshot none = new SoftwareRuntimePresenceSnapshot(false, false, DateTime.UtcNow, "test");
        SoftwareRuntimePresenceSnapshot codexOnly = new SoftwareRuntimePresenceSnapshot(true, false, DateTime.UtcNow, "test");
        SoftwareRuntimePresenceSnapshot claudeOnly = new SoftwareRuntimePresenceSnapshot(false, true, DateTime.UtcNow, "test");
        SoftwareRuntimePresenceSnapshot both = new SoftwareRuntimePresenceSnapshot(true, true, DateTime.UtcNow, "test");

        AssertSoftwareMode(
            ResolveCodexRadarSoftwareModeFromSignals(CodexRadarSoftwareMode.Codex, CodexRadarSoftwareMode.Claude, both, true, CodexRadarSoftwareMode.Claude),
            CodexRadarSoftwareMode.Codex,
            "fixed Codex should ignore foreground and presence");
        AssertSoftwareMode(
            ResolveCodexRadarSoftwareModeFromSignals(CodexRadarSoftwareMode.Claude, CodexRadarSoftwareMode.Codex, both, true, CodexRadarSoftwareMode.Codex),
            CodexRadarSoftwareMode.Claude,
            "fixed Claude should ignore foreground and presence");
        AssertSoftwareMode(
            ResolveCodexRadarSoftwareModeFromSignals(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Claude, none, false, CodexRadarSoftwareMode.Codex),
            CodexRadarSoftwareMode.Claude,
            "no supported apps should keep previous effective mode");
        AssertSoftwareMode(
            ResolveCodexRadarSoftwareModeFromSignals(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Claude, codexOnly, false, CodexRadarSoftwareMode.Claude),
            CodexRadarSoftwareMode.Codex,
            "Codex-only presence should select Codex without foreground detection");
        AssertSoftwareMode(
            ResolveCodexRadarSoftwareModeFromSignals(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Codex, claudeOnly, false, CodexRadarSoftwareMode.Codex),
            CodexRadarSoftwareMode.Claude,
            "Claude-only presence should select Claude without foreground detection");
        AssertSoftwareMode(
            ResolveCodexRadarSoftwareModeFromSignals(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Codex, both, true, CodexRadarSoftwareMode.Claude),
            CodexRadarSoftwareMode.Claude,
            "both running should accept foreground-detected Claude");
        AssertSoftwareMode(
            ResolveCodexRadarSoftwareModeFromSignals(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Claude, both, false, CodexRadarSoftwareMode.Codex),
            CodexRadarSoftwareMode.Claude,
            "foreground detection failure should keep previous effective mode");

        if (ShouldDetectForegroundForSoftwareMode(CodexRadarSoftwareMode.Codex, both) ||
            ShouldDetectForegroundForSoftwareMode(CodexRadarSoftwareMode.Claude, both) ||
            ShouldDetectForegroundForSoftwareMode(CodexRadarSoftwareMode.Auto, none) ||
            ShouldDetectForegroundForSoftwareMode(CodexRadarSoftwareMode.Auto, codexOnly) ||
            ShouldDetectForegroundForSoftwareMode(CodexRadarSoftwareMode.Auto, claudeOnly) ||
            !ShouldDetectForegroundForSoftwareMode(CodexRadarSoftwareMode.Auto, both))
        {
            throw new InvalidOperationException("Codex Radar software mode self-test failed: foreground detection gate.");
        }

        AssertRefreshTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Codex, none), SelectedQuotaRefreshTarget.None, "no apps should not queue quota refresh");
        AssertRefreshTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Codex, codexOnly), SelectedQuotaRefreshTarget.Codex, "Codex mode should queue Codex when Codex is running");
        AssertRefreshTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Codex, claudeOnly), SelectedQuotaRefreshTarget.None, "Codex mode should not queue Claude when only Claude is running");
        AssertRefreshTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Claude, claudeOnly), SelectedQuotaRefreshTarget.Claude, "Claude mode should queue Claude when Claude is running");
        AssertRefreshTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Claude, codexOnly), SelectedQuotaRefreshTarget.None, "Claude mode should not queue Codex when only Codex is running");

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
        CodexRadarSnapshot sharedSnapshot = ConvertClaudeRadarSnapshotForSharedWindow(claudeSnapshot);
        if (sharedSnapshot == null ||
            !sharedSnapshot.ModelIqKnown ||
            sharedSnapshot.ModelIqPassRatePercent != 119 ||
            !sharedSnapshot.CommunityRatingKnown ||
            !string.Equals(sharedSnapshot.CommunityRatingModelId, "opus48_high", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Codex Radar software mode self-test failed: Claude shared snapshot conversion.");
        }
    }

    private static void AssertSoftwareMode(CodexRadarSoftwareMode actual, CodexRadarSoftwareMode expected, string message)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException("Codex Radar software mode self-test failed: " + message);
        }
    }

    private static void AssertRefreshTarget(SelectedQuotaRefreshTarget actual, SelectedQuotaRefreshTarget expected, string message)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException("Codex Radar quota provider self-test failed: " + message);
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
        if (!ClaudeCodeUsageScheduler.TryStartOrJoin("codex_radar", this.currentSettings, "定时间隔", out task))
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
                    this.currentSettings == null ||
                    this.currentSettings.CodexRadarRandomTestEnabled ||
                    GetEffectiveCodexRadarSoftwareMode() != CodexRadarSoftwareMode.Claude)
                {
                    return;
                }

                DateTime nowUtc = DateTime.UtcNow;
                this.lastQuotaRefreshUtc = nowUtc;
                ApplyCodexQuotaSnapshot(snapshot.Clone(), true, true, DateTime.Now, nowUtc, "claude");
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

        if ((IsClaudeCodeSetupTokenMissing(errorCode) || IsClaudeCodeStatusLineCacheMissing(errorCode)) &&
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
            IsClaudeCodeSetupTokenMissing(errorCode))
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

    private static bool IsClaudeCodeSetupTokenMissing(string errorCode)
    {
        return string.Equals(errorCode, "NO_SETUP_TOKEN", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "NO_TOKEN", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClaudeCodeStatusLineCacheMissing(string errorCode)
    {
        return string.Equals(errorCode, "NO_STATUSLINE_CACHE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "STATUSLINE_STALE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(errorCode, "STATUSLINE_CUSTOM", StringComparison.OrdinalIgnoreCase);
    }
}
