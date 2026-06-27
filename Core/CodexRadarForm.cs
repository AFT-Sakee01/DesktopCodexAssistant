using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

/// <summary>
/// Owns the layered Codex monitor and schedules local quota reads plus selected
/// current.json model snapshots without performing blocking work in paint code.
/// </summary>
internal sealed class CodexRadarForm : Form
{
    private const int CodexRadarSecondBoundaryOffsetMs = 30;
    private const int QuotaTailChunkBytes = 1024 * 1024;
    private const int MaxQuotaRolloutFilesToScan = 80;
    private const string ClaudeStatusUrl = "https://status.claude.com/api/v2/status.json";
    private const int ClaudeStatusTimeoutMs = 10000;
    private const string CodexRadarStatusUrl = "https://codexradar.com/current.json";
    private const string ChatGptProbeUrl = "https://chatgpt.com/";
    private const string OpenAiStatusUrl = "https://status.openai.com/api/v2/summary.json";
    private const int CodexConnectionProbeTimeoutMs = 5000;
    private const int CodexConnectionDnsTimeoutMs = 3500;
    private const int CodexConnectionLogTailBytes = 128 * 1024;
    private const int CodexModelIqNominalTasks = WidgetSettings.MaxCodexModelIqPassed;
    private const int MaxCodexModelIqScore = 200;
    private const int CodexRadarStatusTimeoutMs = 10000;
    private const int CodexModelHistoryDays = 366;
    private const int CodexModelCacheRetentionDays = 7;
    private readonly System.Windows.Forms.Timer timer;
    private readonly System.Windows.Forms.Timer hoverTimer;
    private readonly Action<string, string, ToolTipIcon> notificationAction;
    // Cache the newest rollout result while its identity and append-sensitive metadata stay unchanged.
    private static readonly object codexQuotaSnapshotCacheLock = new object();
    private static readonly object codexRadarDiskCacheLock = new object();
    private static string codexQuotaSnapshotCachePath = string.Empty;
    private static DateTime codexQuotaSnapshotCacheWriteUtc;
    private static long codexQuotaSnapshotCacheLength = -1;
    private static CodexQuotaSnapshot codexQuotaSnapshotCache;
    private static DateTime codexQuotaSnapshotNewestVerifyUtc;
    private readonly object claudeStatusLock = new object();
    private readonly object codexRadarStatusLock = new object();
    private readonly object codexConnectionLock = new object();
    private readonly object quotaResetStateLock = new object();
    private readonly object serviceHealthLock = new object();
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private int renderTickCount;
    private double hoverOpacityProgress;
    private DateTime hoverOpacityLastUtc;
    private DateTime reverseHoverRevealUntilUtc;
    private readonly HoverInteractionPolicy.HoverOpacityDelayState hoverOpacityDelayState = new HoverInteractionPolicy.HoverOpacityDelayState();
    private bool sharedInteractionPolling;
    private DateTime lastQuotaRefreshUtc;
    private DateTime lastQuotaProcessCheckUtc;
    private CodexQuotaSnapshot quotaSnapshot;
    private bool quotaSourceKnown;
    private bool quotaCodexProcessRunning;
    private int lastFiveHourQuotaReadPercent = -1;
    private int lastWeeklyQuotaReadPercent = -1;
    private DateTime lastQuotaReadDeltaSourceUtc;
    private int fiveHourConsumptionRingBaselinePercent = -1;
    // The weekly consumption ring uses the weekly balance observed when the current five-hour window began.
    private DateTime trackedFiveHourResetLocal;
    private int weeklyQuotaAtFiveHourWindowStartPercent = -1;
    private DateTime fiveHourQuotaProtectionUtc;
    private DateTime weeklyQuotaProtectionUtc;
    private bool fiveHourQuotaProtectionGold;
    private bool weeklyQuotaProtectionGold;
    private DateTime nextQuotaInactiveRefreshUtc;
    private DateTime nextClaudeStatusRefreshUtc;
    private bool claudeStatusRequestRunning;
    private DateTime nextCodexRadarStatusRefreshUtc;
    private bool codexRadarStatusRequestRunning;
    private CodexRadarSnapshot codexRadarSnapshot;
    private DateTime nextCodexConnectionRefreshUtc;
    private bool codexConnectionRequestRunning;
    private CodexConnectionSnapshot codexConnectionSnapshot;
    private int codexRadarRandomTestRefreshToken = int.MinValue;
    private DateTime nextCodexRadarRandomTestRefreshUtc;
    private CodexRadarRandomTestSnapshot codexRadarRandomTestSnapshot;
    private IntPtr displayPowerNotificationHandle;
    private bool codexDisplayActive = true;
    private bool codexSessionActive = true;
    private bool codexPowerSuspended;
    private bool serviceNetworkAvailable = true;
    private ServiceHealthState radarServiceHealth = ServiceHealthState.Unknown;
    private ServiceHealthState codexServiceHealth = ServiceHealthState.Unknown;
    private ServiceHealthState claudeServiceHealth = ServiceHealthState.Unknown;
    private bool serviceNetworkRefreshRequested = true;
    private string codexConnectionAlertSignature = string.Empty;
    private int codexConnectionAlertIndex;
    private bool codexConnectionAlertNamePhase = true;
    private string lastRadarResetEventId = string.Empty;
    private DateTime lastRadarResetEventUtc;
    private string lastRadarProtectedResetEventId = string.Empty;
    private string lastRadarOpenEventId = string.Empty;
    private DateTime lastRadarOpenEventUtc;
    private FileSystemWatcher quotaSessionWatcher;
    private string quotaSessionsPath = string.Empty;
    private int quotaSessionFilesChanged = 1;
    private Bitmap renderBitmap;
    private Graphics renderGraphics;
    private bool renderBufferValid;
    private bool lastRenderedBurnInColorProtectionActive;
    private long burnInShiftSlot = long.MinValue;
    // The native surface keeps the HBITMAP alive across alpha-only hover updates.
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();
    private readonly UiFontCache fontCache = new UiFontCache();
    private DateTime lastRenderedClockSecondLocal;

    private enum ServiceHealthState
    {
        Unknown,
        Normal,
        Degraded,
        Incomplete,
        Offline,
        Unavailable,
        Unreachable
    }

    private enum CodexConnectionStageState
    {
        Unknown,
        Passed,
        Warning,
        Unavailable,
        Blocked,
        Offline
    }

    private sealed class CodexConnectionStage
    {
        public string Name { get; set; }
        public CodexConnectionStageState State { get; set; }
        public string ErrorCode { get; set; }

        public CodexConnectionStage Clone()
        {
            return new CodexConnectionStage
            {
                Name = this.Name,
                State = this.State,
                ErrorCode = this.ErrorCode
            };
        }
    }

    private sealed class CodexConnectionSnapshot
    {
        public CodexConnectionStage[] Stages { get; set; }
        public DateTime CheckedAtUtc { get; set; }
        public bool CheckedAtKnown { get; set; }
        public bool Offline { get; set; }

        public static CodexConnectionSnapshot CreateDefault()
        {
            string[] names = new string[] { "网络", "DNS", "隧道", "OpenAI", "Codex" };
            CodexConnectionStage[] stages = new CodexConnectionStage[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                stages[i] = new CodexConnectionStage
                {
                    Name = names[i],
                    State = CodexConnectionStageState.Unknown,
                    ErrorCode = string.Empty
                };
            }

            return new CodexConnectionSnapshot
            {
                Stages = stages,
                CheckedAtUtc = DateTime.MinValue,
                CheckedAtKnown = false,
                Offline = false
            };
        }

        public CodexConnectionSnapshot Clone()
        {
            CodexConnectionSnapshot clone = CreateDefault();
            clone.CheckedAtUtc = this.CheckedAtUtc;
            clone.CheckedAtKnown = this.CheckedAtKnown;
            clone.Offline = this.Offline;
            if (this.Stages != null)
            {
                int count = Math.Min(clone.Stages.Length, this.Stages.Length);
                for (int i = 0; i < count; i++)
                {
                    if (this.Stages[i] != null)
                    {
                        clone.Stages[i] = this.Stages[i].Clone();
                    }
                }
            }

            return clone;
        }
    }

    private sealed class CodexConnectionAlertCandidate
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string Reason { get; set; }
        public Color Color { get; set; }
    }

    private sealed class CodexRadarResetEvent
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public DateTime EventUtc { get; set; }
        public bool EventUtcKnown { get; set; }
    }

    private sealed class CodexRadarRandomTestSnapshot
    {
        public CodexRadarSnapshot Radar { get; set; }
        public CodexQuotaSnapshot Quota { get; set; }
        public CodexConnectionSnapshot Connection { get; set; }
        public ServiceHealthState RadarHealth { get; set; }
        public ServiceHealthState ClaudeHealth { get; set; }
        public ServiceHealthState CodexHealth { get; set; }
        public bool NetworkAvailable { get; set; }
        public bool CodexRunning { get; set; }
        public bool FiveHourGold { get; set; }
        public bool WeeklyGold { get; set; }
        public int FiveHourDropPercent { get; set; }
        public int WeeklyUsedSinceFiveHourResetPercent { get; set; }
    }

    private sealed class CodexQuotaSnapshot
    {
        public int FiveHourPercent { get; set; }
        public int WeeklyPercent { get; set; }
        public DateTime FiveHourResetLocal { get; set; }
        public DateTime WeeklyResetLocal { get; set; }
        public bool FiveHourResetKnown { get; set; }
        public bool WeeklyResetKnown { get; set; }
        public DateTime SourceUpdatedUtc { get; set; }
        public bool SourceUpdatedKnown { get; set; }

        public static CodexQuotaSnapshot CreateDefault()
        {
            return new CodexQuotaSnapshot
            {
                FiveHourPercent = 100,
                WeeklyPercent = 100,
                FiveHourResetLocal = DateTime.MinValue,
                WeeklyResetLocal = DateTime.MinValue,
                FiveHourResetKnown = false,
                WeeklyResetKnown = false,
                SourceUpdatedUtc = DateTime.MinValue,
                SourceUpdatedKnown = false
            };
        }

        public CodexQuotaSnapshot Clone()
        {
            return new CodexQuotaSnapshot
            {
                FiveHourPercent = this.FiveHourPercent,
                WeeklyPercent = this.WeeklyPercent,
                FiveHourResetLocal = this.FiveHourResetLocal,
                WeeklyResetLocal = this.WeeklyResetLocal,
                FiveHourResetKnown = this.FiveHourResetKnown,
                WeeklyResetKnown = this.WeeklyResetKnown,
                SourceUpdatedUtc = this.SourceUpdatedUtc,
                SourceUpdatedKnown = this.SourceUpdatedKnown
            };
        }
    }

    private sealed class CodexQuotaEvent
    {
        public CodexQuotaSnapshot Snapshot { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class CodexRadarSnapshot
    {
        public DateTime CheckedAtLocal { get; set; }
        public bool CheckedAtKnown { get; set; }
        public DateTime ModelIqRefreshedAtLocal { get; set; }
        public DateTime ModelIqDataDateLocal { get; set; }
        public int ModelIqDataWindowStartHourLocal { get; set; }
        public bool ModelIqRefreshedAtKnown { get; set; }
        public bool ModelIqDataDateKnown { get; set; }
        public bool ModelIqDataWindowKnown { get; set; }
        public bool ModelIqRefreshSucceeded { get; set; }
        public bool SpeedWindowKnown { get; set; }
        public bool SpeedWindowOpen { get; set; }
        public string SpeedWindowStatus { get; set; }
        public string SpeedWindowEventId { get; set; }
        public DateTime SpeedWindowOpenedAtLocal { get; set; }
        public DateTime SpeedWindowClosedAtLocal { get; set; }
        public bool SpeedWindowOpenedAtKnown { get; set; }
        public bool SpeedWindowClosedAtKnown { get; set; }
        public bool ResetEventKnown { get; set; }
        public string ResetEventId { get; set; }
        public string ResetEventTitle { get; set; }
        public DateTime ResetEventUtc { get; set; }
        public string ModelIqStatus { get; set; }
        public int ModelIqPassRatePercent { get; set; }
        public int ModelIqPassed { get; set; }
        public int ModelIqValidTasks { get; set; }
        public int ModelIqTokenEfficiencyPercent { get; set; }
        public int ModelIqTimeEfficiencyPercent { get; set; }
        public double ModelIqEfficiencyPassed { get; set; }
        public double ModelIqEfficiencyTotalTokens { get; set; }
        public double ModelIqEfficiencySerialSeconds { get; set; }
        public bool ModelIqPassedKnown { get; set; }
        public bool ModelIqEfficiencyInputKnown { get; set; }
        public bool ModelIqEfficiencyKnown { get; set; }
        public bool ModelIqKnown { get; set; }
        public List<CodexModelHistoryPoint> ModelIqHistory { get; set; }

        public static CodexRadarSnapshot CreateDefault()
        {
            return new CodexRadarSnapshot
            {
                CheckedAtLocal = DateTime.MinValue,
                CheckedAtKnown = false,
                ModelIqRefreshedAtLocal = DateTime.MinValue,
                ModelIqDataDateLocal = DateTime.MinValue,
                ModelIqDataWindowStartHourLocal = 0,
                ModelIqRefreshedAtKnown = false,
                ModelIqDataDateKnown = false,
                ModelIqDataWindowKnown = false,
                ModelIqRefreshSucceeded = false,
                SpeedWindowKnown = false,
                SpeedWindowOpen = false,
                SpeedWindowStatus = string.Empty,
                SpeedWindowEventId = string.Empty,
                SpeedWindowOpenedAtLocal = DateTime.MinValue,
                SpeedWindowClosedAtLocal = DateTime.MinValue,
                SpeedWindowOpenedAtKnown = false,
                SpeedWindowClosedAtKnown = false,
                ResetEventKnown = false,
                ResetEventId = string.Empty,
                ResetEventTitle = string.Empty,
                ResetEventUtc = DateTime.MinValue,
                ModelIqStatus = "invalid",
                ModelIqPassRatePercent = 0,
                ModelIqPassed = 0,
                ModelIqValidTasks = CodexModelIqNominalTasks,
                ModelIqTokenEfficiencyPercent = 100,
                ModelIqTimeEfficiencyPercent = 100,
                ModelIqEfficiencyPassed = 0.0,
                ModelIqEfficiencyTotalTokens = 0.0,
                ModelIqEfficiencySerialSeconds = 0.0,
                ModelIqPassedKnown = false,
                ModelIqEfficiencyInputKnown = false,
                ModelIqEfficiencyKnown = false,
                ModelIqKnown = false,
                ModelIqHistory = new List<CodexModelHistoryPoint>()
            };
        }

        public CodexRadarSnapshot Clone()
        {
            return new CodexRadarSnapshot
            {
                CheckedAtLocal = this.CheckedAtLocal,
                CheckedAtKnown = this.CheckedAtKnown,
                ModelIqRefreshedAtLocal = this.ModelIqRefreshedAtLocal,
                ModelIqDataDateLocal = this.ModelIqDataDateLocal,
                ModelIqDataWindowStartHourLocal = this.ModelIqDataWindowStartHourLocal,
                ModelIqRefreshedAtKnown = this.ModelIqRefreshedAtKnown,
                ModelIqDataDateKnown = this.ModelIqDataDateKnown,
                ModelIqDataWindowKnown = this.ModelIqDataWindowKnown,
                ModelIqRefreshSucceeded = this.ModelIqRefreshSucceeded,
                SpeedWindowKnown = this.SpeedWindowKnown,
                SpeedWindowOpen = this.SpeedWindowOpen,
                SpeedWindowStatus = this.SpeedWindowStatus,
                SpeedWindowEventId = this.SpeedWindowEventId,
                SpeedWindowOpenedAtLocal = this.SpeedWindowOpenedAtLocal,
                SpeedWindowClosedAtLocal = this.SpeedWindowClosedAtLocal,
                SpeedWindowOpenedAtKnown = this.SpeedWindowOpenedAtKnown,
                SpeedWindowClosedAtKnown = this.SpeedWindowClosedAtKnown,
                ResetEventKnown = this.ResetEventKnown,
                ResetEventId = this.ResetEventId,
                ResetEventTitle = this.ResetEventTitle,
                ResetEventUtc = this.ResetEventUtc,
                ModelIqStatus = this.ModelIqStatus,
                ModelIqPassRatePercent = this.ModelIqPassRatePercent,
                ModelIqPassed = this.ModelIqPassed,
                ModelIqValidTasks = this.ModelIqValidTasks,
                ModelIqTokenEfficiencyPercent = this.ModelIqTokenEfficiencyPercent,
                ModelIqTimeEfficiencyPercent = this.ModelIqTimeEfficiencyPercent,
                ModelIqEfficiencyPassed = this.ModelIqEfficiencyPassed,
                ModelIqEfficiencyTotalTokens = this.ModelIqEfficiencyTotalTokens,
                ModelIqEfficiencySerialSeconds = this.ModelIqEfficiencySerialSeconds,
                ModelIqPassedKnown = this.ModelIqPassedKnown,
                ModelIqEfficiencyInputKnown = this.ModelIqEfficiencyInputKnown,
                ModelIqEfficiencyKnown = this.ModelIqEfficiencyKnown,
                ModelIqKnown = this.ModelIqKnown,
                ModelIqHistory = CloneCodexModelHistory(this.ModelIqHistory)
            };
        }
    }

    private sealed class CodexModelHistoryPoint
    {
        public DateTime DateLocal { get; set; }
        public double Score { get; set; }
        public double Passed { get; set; }
        public double TotalTokens { get; set; }
        public double SerialSeconds { get; set; }
        public double CachedInputTokens { get; set; }
        public double InputTokens { get; set; }
        public double Tasks { get; set; }
        public double InvalidTasks { get; set; }
        public double TokenEfficiencyPercent { get; set; }
        public double TimeEfficiencyPercent { get; set; }
        public bool EfficiencyKnown { get; set; }
        public bool CacheRateKnown { get; set; }
        public bool ValidityKnown { get; set; }

        public CodexModelHistoryPoint Clone()
        {
            return new CodexModelHistoryPoint
            {
                DateLocal = this.DateLocal,
                Score = this.Score,
                Passed = this.Passed,
                TotalTokens = this.TotalTokens,
                SerialSeconds = this.SerialSeconds,
                CachedInputTokens = this.CachedInputTokens,
                InputTokens = this.InputTokens,
                Tasks = this.Tasks,
                InvalidTasks = this.InvalidTasks,
                TokenEfficiencyPercent = this.TokenEfficiencyPercent,
                TimeEfficiencyPercent = this.TimeEfficiencyPercent,
                EfficiencyKnown = this.EfficiencyKnown,
                CacheRateKnown = this.CacheRateKnown,
                ValidityKnown = this.ValidityKnown
            };
        }
    }

    public CodexRadarForm(WidgetSettings settings, Action<string, string, ToolTipIcon> notificationAction)
    {
        this.notificationAction = notificationAction;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        CodexQuotaSnapshot cachedQuotaSnapshot;
        if (TryReadQuotaIniSnapshot(out cachedQuotaSnapshot))
        {
            this.quotaSnapshot = NormalizeQuotaSnapshot(cachedQuotaSnapshot);
            this.quotaSourceKnown = true;
        }
        else
        {
            this.quotaSnapshot = CodexQuotaSnapshot.CreateDefault();
        }
        InitializeQuotaReadDeltaTracking(this.quotaSnapshot, this.quotaSourceKnown);
        this.codexRadarSnapshot = LoadCodexRadarCache(this.currentSettings.CodexRadarModelKey) ??
            CodexRadarSnapshot.CreateDefault();
        this.codexConnectionSnapshot = CodexConnectionSnapshot.CreateDefault();
        LoadQuotaResetState();
        InitializeQuotaSessionWatcher();

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = DesignTokens.Colors.AppBackground;
        this.MinimumSize = new Size(WidgetSettings.MinCodexRadarWidth, WidgetSettings.MinCodexRadarHeight);
        this.MaximumSize = new Size(WidgetSettings.MaxCodexRadarWidth, WidgetSettings.MaxCodexRadarHeight + S(32));
        this.Size = new Size(this.currentSettings.CodexRadarWidth, this.currentSettings.CodexRadarHeight);

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetNextCodexRadarTickIntervalMs();
        this.timer.Tick += OnTimerTick;
        this.hoverTimer = new System.Windows.Forms.Timer();
        this.hoverTimer.Interval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        this.hoverTimer.Tick += OnHoverTimerTick;
        PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
        this.nextCodexConnectionRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
        SystemEvents.SessionSwitch += OnSystemSessionSwitch;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.currentSettings);
        ScheduleNextCodexRadarTick();
        this.timer.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (this.displayPowerNotificationHandle == IntPtr.Zero)
        {
            this.displayPowerNotificationHandle = NativeMethods.RegisterConsoleDisplayStateNotification(this.Handle);
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (this.displayPowerNotificationHandle != IntPtr.Zero)
        {
            NativeMethods.UnregisterPowerNotification(this.displayPowerNotificationHandle);
            this.displayPowerNotificationHandle = IntPtr.Zero;
        }

        base.OnHandleDestroyed(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSystemSessionSwitch;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        DisposeQuotaSessionWatcher();
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        this.hoverTimer.Stop();
        this.hoverTimer.Tick -= OnHoverTimerTick;
        this.hoverTimer.Dispose();
        DisposeRenderBuffer();
        this.fontCache.Dispose();
        this.layeredSurface.Dispose();
        base.OnFormClosed(e);
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        try
        {
            this.renderTickCount++;
            if (!IsCodexPollingAllowed())
            {
                return;
            }

            // This timer is only a lightweight scheduler. Each data source owns its business
            // interval and single-flight guard, so a faster UI mode does not multiply web traffic.
            UpdateCodexRadarRandomTestIfNeeded();
            if (!this.currentSettings.CodexRadarRandomTestEnabled)
            {
                UpdateServiceConnectivityHealth();
                RefreshQuotaInfoIfNeeded();
                RefreshCodexConnectionIfNeeded();
                RefreshCodexRadarStatusIfNeeded();
                RefreshClaudeStatusIfNeeded();
            }
            bool alertChanged = AdvanceCodexConnectionAlertRotation();
            Size desiredSize = GetDesiredCodexRadarSize();
            bool sizeChanged = false;
            if (this.Size != desiredSize)
            {
                this.Size = desiredSize;
                PositionCodexRadar();
                sizeChanged = true;
            }

            bool positionChanged = false;
            if (!this.hiddenForFullscreen &&
                this.Visible &&
                BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot))
            {
                PositionCodexRadar();
                positionChanged = true;
            }

            DateTime renderSecond = TruncateToSecond(DateTime.Now);
            if (!this.hiddenForFullscreen &&
                this.Visible &&
                (sizeChanged || positionChanged || alertChanged || this.lastRenderedClockSecondLocal != renderSecond))
            {
                RenderLayeredWindow();
            }
        }
        finally
        {
            ScheduleNextCodexRadarTick();
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        DisposeRenderBuffer();
        this.fontCache.Dispose();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(12)))
        {
            Region oldRegion = this.Region;
            this.Region = new Region(path);
            if (oldRegion != null)
            {
                oldRegion.Dispose();
            }
        }

        RenderLayeredWindow();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;

        base.WndProc(ref m);

        if (m.Msg == NativeMethods.WM_POWERBROADCAST)
        {
            HandlePowerBroadcast(m.WParam, m.LParam);
        }

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionCodexRadar();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        CodexRadarTestMode oldCodexRadarTestMode = this.currentSettings.CodexRadarTestMode;
        ServiceHealthTestMode oldServiceHealthTestMode = this.currentSettings.ServiceHealthTestMode;
        string oldModelKey = this.currentSettings.CodexRadarModelKey;
        bool oldRandomTestEnabled = this.currentSettings.CodexRadarRandomTestEnabled;
        int oldRandomTestToken = this.currentSettings.CodexRadarRandomTestRefreshToken;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        ApplyPerformanceTimerIntervals();

        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            (!oldRandomTestEnabled ||
             oldRandomTestToken != this.currentSettings.CodexRadarRandomTestRefreshToken ||
             this.codexRadarRandomTestSnapshot == null))
        {
            GenerateCodexRadarRandomTestSnapshot();
        }
        else if (oldRandomTestEnabled && !this.currentSettings.CodexRadarRandomTestEnabled)
        {
            this.codexRadarRandomTestSnapshot = null;
            PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
            RequestServiceNetworkRefresh();
        }

        if (!string.Equals(oldModelKey, this.currentSettings.CodexRadarModelKey, StringComparison.OrdinalIgnoreCase))
        {
            lock (this.codexRadarStatusLock)
            {
                this.codexRadarSnapshot = LoadCodexRadarCache(this.currentSettings.CodexRadarModelKey) ??
                    CodexRadarSnapshot.CreateDefault();
                this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
            }

            SetRadarServiceHealth(ServiceHealthState.Unknown);
        }

        if (oldCodexRadarTestMode != this.currentSettings.CodexRadarTestMode)
        {
            if (this.currentSettings.CodexRadarTestMode == CodexRadarTestMode.Off)
            {
                PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
            }

            RenderLayeredWindow();
        }

        if (oldServiceHealthTestMode != this.currentSettings.ServiceHealthTestMode)
        {
            if (this.currentSettings.ServiceHealthTestMode == ServiceHealthTestMode.Off)
            {
                ResetServiceHealthAfterTestMode();
            }
            else
            {
                ApplyServiceHealthTestMode();
            }

            RenderLayeredWindow();
        }
        else if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
        }

        Size desiredSize = GetDesiredCodexRadarSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        ApplyClickThroughStyle();
        UpdateHoverAnimationTimer();
        NativeMethods.SetWindowPos(
            this.Handle,
            shouldBeTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionCodexRadar();
        RenderLayeredWindow();
    }

    public void ForceRefresh()
    {
        this.lastQuotaRefreshUtc = DateTime.MinValue;
        this.nextQuotaInactiveRefreshUtc = DateTime.MinValue;
        RequestServiceNetworkRefresh();

        lock (this.claudeStatusLock)
        {
            this.nextClaudeStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
        }

        lock (this.codexRadarStatusLock)
        {
            DateTime nowUtc = DateTime.UtcNow;
            this.nextCodexRadarStatusRefreshUtc = nowUtc.AddSeconds(4.0);
        }

        lock (this.codexConnectionLock)
        {
            this.nextCodexConnectionRefreshUtc = DateTime.UtcNow;
        }

        OnTimerTick(this, EventArgs.Empty);
    }

    public void RecoverAfterDisplayResume()
    {
        this.codexPowerSuspended = false;
        this.codexDisplayActive = true;
        this.codexSessionActive = true;
        ResetDisplayRenderResources();
        PositionCodexRadar();
        ResumeCodexPollingSoon();
        ScheduleNextCodexRadarTick();
    }

    public void PrepareForDisplaySuspend()
    {
        ResetDisplayRenderResources();
    }

    private void PrimeCodexWebRefreshSchedule(DateTime nowUtc)
    {
        lock (this.claudeStatusLock)
        {
            this.nextClaudeStatusRefreshUtc = nowUtc.AddSeconds(1.0);
        }

        lock (this.codexRadarStatusLock)
        {
            this.nextCodexRadarStatusRefreshUtc = nowUtc.AddSeconds(4.0);
        }

        lock (this.codexConnectionLock)
        {
            this.nextCodexConnectionRefreshUtc = nowUtc.AddSeconds(1.0);
        }
    }

    private bool IsCodexPollingAllowed()
    {
        return this.codexDisplayActive && this.codexSessionActive && !this.codexPowerSuspended;
    }

    private void ResumeCodexPollingSoon()
    {
        this.lastQuotaRefreshUtc = DateTime.MinValue;
        this.nextQuotaInactiveRefreshUtc = DateTime.MinValue;
        PrimeCodexWebRefreshSchedule(DateTime.UtcNow);
        RenderLayeredWindow();
    }

    private void OnSystemSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (this.IsDisposed)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    OnSystemSessionSwitch(sender, e);
                });
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            this.codexSessionActive = false;
            return;
        }

        if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            this.codexSessionActive = true;
            ResumeCodexPollingSoon();
        }
    }

    private void HandlePowerBroadcast(IntPtr eventTypePtr, IntPtr dataPtr)
    {
        int eventType = eventTypePtr.ToInt32();
        if (eventType == NativeMethods.PBT_APMSUSPEND)
        {
            this.codexPowerSuspended = true;
            return;
        }

        if (eventType == NativeMethods.PBT_APMRESUMEAUTOMATIC ||
            eventType == NativeMethods.PBT_APMRESUMESUSPEND ||
            eventType == NativeMethods.PBT_APMRESUMECRITICAL)
        {
            this.codexPowerSuspended = false;
            this.codexDisplayActive = true;
            ResumeCodexPollingSoon();
            return;
        }

        if (eventType == NativeMethods.PBT_POWERSETTINGCHANGE && dataPtr != IntPtr.Zero)
        {
            NativeMethods.POWERBROADCAST_SETTING setting =
                (NativeMethods.POWERBROADCAST_SETTING)Marshal.PtrToStructure(
                    dataPtr,
                    typeof(NativeMethods.POWERBROADCAST_SETTING));
            if (setting.PowerSetting == NativeMethods.GUID_CONSOLE_DISPLAY_STATE)
            {
                bool active = setting.Data != 0;
                if (this.codexDisplayActive != active)
                {
                    this.codexDisplayActive = active;
                    if (active)
                    {
                        ResumeCodexPollingSoon();
                    }
                }
            }
        }
    }

    private void ApplyPerformanceTimerIntervals()
    {
        ScheduleNextCodexRadarTick();

        int hoverInterval = WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != hoverInterval)
        {
            this.hoverTimer.Interval = hoverInterval;
        }
    }

    private void ScheduleNextCodexRadarTick()
    {
        int interval = GetNextCodexRadarTickIntervalMs();
        if (this.timer.Interval != interval)
        {
            this.timer.Interval = interval;
        }
    }

    private int GetNextCodexRadarTickIntervalMs()
    {
        // Boundary alignment keeps the clock stable and groups wakeups with the other panels.
        DateTime now = DateTime.Now;
        int targetInterval = WidgetSettings.GetPanelRenderIntervalMs(this.currentSettings.PerformanceMode);
        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            this.currentSettings.CodexRadarRandomTestAutoRefresh)
        {
            targetInterval = Math.Min(targetInterval, 1000);
        }

        int elapsedInInterval = (int)(now.TimeOfDay.TotalMilliseconds % targetInterval);
        int interval = targetInterval - elapsedInInterval + CodexRadarSecondBoundaryOffsetMs;
        if (interval <= CodexRadarSecondBoundaryOffsetMs)
        {
            interval += targetInterval;
        }

        return Math.Max(50, Math.Min(targetInterval + 100, interval));
    }

    private void UpdateCodexRadarRandomTestIfNeeded()
    {
        if (!this.currentSettings.CodexRadarRandomTestEnabled)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool tokenChanged =
            this.codexRadarRandomTestRefreshToken !=
            this.currentSettings.CodexRadarRandomTestRefreshToken;
        bool automaticDue =
            this.currentSettings.CodexRadarRandomTestAutoRefresh &&
            (this.nextCodexRadarRandomTestRefreshUtc == DateTime.MinValue ||
             nowUtc >= this.nextCodexRadarRandomTestRefreshUtc);
        if (this.codexRadarRandomTestSnapshot == null || tokenChanged || automaticDue)
        {
            GenerateCodexRadarRandomTestSnapshot();
        }
    }

    private void GenerateCodexRadarRandomTestSnapshot()
    {
        int seed = unchecked(
            this.currentSettings.CodexRadarRandomTestRefreshToken * 397 ^
            DateTime.UtcNow.Ticks.GetHashCode());
        Random random = new Random(seed);
        CodexRadarRandomTestSnapshot test = new CodexRadarRandomTestSnapshot();

        CodexRadarSnapshot radar = CodexRadarSnapshot.CreateDefault();
        int passed = random.Next(0, CodexModelIqNominalTasks + 1);
        radar.CheckedAtLocal = DateTime.Now;
        radar.CheckedAtKnown = true;
        radar.ModelIqRefreshedAtLocal = DateTime.Now;
        radar.ModelIqRefreshedAtKnown = true;
        DateTime randomBeijingWindow = TimeZoneUtilities.GetCurrentBeijingHalfDayStart(DateTime.UtcNow).AddDays(-random.Next(0, 3));
        radar.ModelIqDataDateLocal = randomBeijingWindow.Date;
        radar.ModelIqDataWindowStartHourLocal = randomBeijingWindow.Hour >= 12 ? 12 : 0;
        radar.ModelIqDataDateKnown = true;
        radar.ModelIqDataWindowKnown = true;
        radar.ModelIqRefreshSucceeded = true;
        radar.ModelIqKnown = true;
        radar.ModelIqPassedKnown = true;
        radar.ModelIqPassed = passed;
        radar.ModelIqValidTasks = CodexModelIqNominalTasks;
        radar.ModelIqPassRatePercent = Math.Max(
            0,
            Math.Min(
                MaxCodexModelIqScore,
                (int)Math.Round(
                    passed / (double)WidgetSettings.DefaultCodexModelIqBaselinePassed * 100.0,
                    MidpointRounding.AwayFromZero)));
        radar.ModelIqStatus = passed < WidgetSettings.DefaultCodexModelIqBaselinePassed
            ? "red"
            : (passed == WidgetSettings.DefaultCodexModelIqBaselinePassed ? "green" : "yellow");
        radar.ModelIqTokenEfficiencyPercent = random.Next(0, 201);
        radar.ModelIqTimeEfficiencyPercent = random.Next(0, 201);
        radar.ModelIqEfficiencyPassed = Math.Max(1, passed);
        radar.ModelIqEfficiencyTotalTokens = random.Next(18000000, 60000001);
        radar.ModelIqEfficiencySerialSeconds = random.Next(1200, 4801);
        radar.ModelIqEfficiencyInputKnown = true;
        radar.ModelIqEfficiencyKnown = true;
        radar.SpeedWindowKnown = true;
        radar.SpeedWindowOpen = random.Next(0, 4) == 0;
        radar.SpeedWindowStatus = radar.SpeedWindowOpen ? "open" : "none";
        radar.SpeedWindowEventId = radar.SpeedWindowOpen
            ? "random-speed-window-" + seed.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        radar.SpeedWindowOpenedAtLocal = DateTime.Now.AddMinutes(-random.Next(5, 181));
        radar.SpeedWindowOpenedAtKnown = radar.SpeedWindowOpen;
        radar.SpeedWindowClosedAtLocal = DateTime.Now.AddMinutes(random.Next(5, 181));
        radar.SpeedWindowClosedAtKnown = radar.SpeedWindowOpen && random.Next(0, 2) == 0;
        radar.ResetEventKnown = random.Next(0, 6) == 0;
        radar.ResetEventId = radar.ResetEventKnown
            ? "random-reset-" + seed.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        radar.ResetEventTitle = radar.ResetEventKnown ? "测试重置" : string.Empty;
        radar.ResetEventUtc = radar.ResetEventKnown ? DateTime.UtcNow : DateTime.MinValue;
        test.Radar = radar;

        CodexQuotaSnapshot quota = CodexQuotaSnapshot.CreateDefault();
        quota.FiveHourPercent = random.Next(0, 101);
        quota.WeeklyPercent = random.Next(0, 101);
        quota.FiveHourResetLocal = DateTime.Now.AddMinutes(random.Next(5, 301));
        quota.WeeklyResetLocal = DateTime.Now.AddDays(random.Next(1, 8));
        quota.FiveHourResetKnown = true;
        quota.WeeklyResetKnown = true;
        quota.SourceUpdatedUtc = DateTime.UtcNow;
        quota.SourceUpdatedKnown = true;
        test.Quota = quota;
        test.CodexRunning = random.Next(0, 5) != 0;
        test.FiveHourGold = random.Next(0, 5) == 0;
        test.WeeklyGold = random.Next(0, 7) == 0;
        test.FiveHourDropPercent = test.FiveHourGold ? 0 : random.Next(0, Math.Min(18, quota.FiveHourPercent) + 1);
        test.WeeklyUsedSinceFiveHourResetPercent = test.WeeklyGold
            ? 0
            : random.Next(0, Math.Min(28, 100 - quota.WeeklyPercent) + 1);

        CodexConnectionSnapshot connection = BuildRandomCodexConnectionSnapshot(random);
        test.Connection = connection;
        test.NetworkAvailable = !connection.Offline;
        if (!test.NetworkAvailable)
        {
            test.RadarHealth = ServiceHealthState.Offline;
            test.ClaudeHealth = ServiceHealthState.Offline;
            test.CodexHealth = ServiceHealthState.Offline;
        }
        else
        {
            test.RadarHealth = GetRandomServiceHealth(random);
            test.ClaudeHealth = GetRandomServiceHealth(random);
            test.CodexHealth = GetRandomServiceHealth(random);
        }

        this.codexRadarRandomTestSnapshot = test;
        this.codexRadarRandomTestRefreshToken =
            this.currentSettings.CodexRadarRandomTestRefreshToken;
        this.nextCodexRadarRandomTestRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
    }

    private static CodexConnectionSnapshot BuildRandomCodexConnectionSnapshot(Random random)
    {
        CodexConnectionSnapshot snapshot = CodexConnectionSnapshot.CreateDefault();
        snapshot.CheckedAtUtc = DateTime.UtcNow;
        snapshot.CheckedAtKnown = true;
        for (int i = 0; i < snapshot.Stages.Length; i++)
        {
            snapshot.Stages[i].State = CodexConnectionStageState.Passed;
        }

        int scenario = random.Next(0, 7);
        if (scenario == 1)
        {
            snapshot.Offline = true;
            for (int i = 0; i < snapshot.Stages.Length; i++)
            {
                snapshot.Stages[i].State = CodexConnectionStageState.Offline;
                snapshot.Stages[i].ErrorCode = "OFFLINE";
            }
        }
        else if (scenario >= 2 && scenario <= 4)
        {
            int blockedIndex = scenario - 1;
            snapshot.Stages[blockedIndex].State = CodexConnectionStageState.Blocked;
            snapshot.Stages[blockedIndex].ErrorCode =
                blockedIndex == 1 ? "DNS" : (blockedIndex == 2 ? "TLS" : "TIMEOUT");
            MarkRemainingCodexConnectionStages(
                snapshot,
                blockedIndex + 1,
                CodexConnectionStageState.Unknown);
        }
        else if (scenario == 5)
        {
            snapshot.Stages[3].State = CodexConnectionStageState.Unavailable;
            snapshot.Stages[3].ErrorCode = "HTTP503";
        }
        else if (scenario == 6)
        {
            snapshot.Stages[4].State = CodexConnectionStageState.Warning;
            snapshot.Stages[4].ErrorCode = "HTTP429";
        }

        return snapshot;
    }

    private static ServiceHealthState GetRandomServiceHealth(Random random)
    {
        ServiceHealthState[] states = new ServiceHealthState[]
        {
            ServiceHealthState.Normal,
            ServiceHealthState.Normal,
            ServiceHealthState.Degraded,
            ServiceHealthState.Incomplete,
            ServiceHealthState.Unavailable,
            ServiceHealthState.Unreachable
        };
        return states[random.Next(0, states.Length)];
    }

    private void OnHoverTimerTick(object sender, EventArgs e)
    {
        bool animationActive = ProcessInteractionTick();
        int desiredInterval = animationActive
            ? WidgetSettings.GetHoverAnimationIntervalMs(this.currentSettings.PerformanceMode)
            : WidgetSettings.GetInteractionIdlePollingIntervalMs(this.currentSettings.PerformanceMode);
        if (this.hoverTimer.Interval != desiredInterval)
        {
            this.hoverTimer.Interval = desiredInterval;
        }
    }

    private bool ProcessInteractionTick()
    {
        ApplyClickThroughStyle();
        bool opacityChanged = UpdateHoverOpacityAnimation();
        bool hoverTarget = IsHoverOpacityTargetActive();
        bool animationActive = Math.Abs(this.hoverOpacityProgress - (hoverTarget ? 1.0 : 0.0)) > 0.001;
        if (opacityChanged && !this.hiddenForFullscreen && this.Visible)
        {
            RenderLayeredWindow(false);
        }

        return animationActive;
    }

    public void SetSharedInteractionPolling(bool shared)
    {
        this.sharedInteractionPolling = shared;
        this.hoverOpacityLastUtc = DateTime.UtcNow;
        UpdateHoverAnimationTimer();
    }

    public bool ProcessSharedInteractionTick()
    {
        if (!this.sharedInteractionPolling ||
            this.hiddenForFullscreen ||
            (!IsHoverOpacityRuntimeEnabled() && !NeedsClickThroughPolling()))
        {
            return false;
        }

        return ProcessInteractionTick();
    }

    private void UpdateHoverAnimationTimer()
    {
        if (!this.hiddenForFullscreen &&
            (IsHoverOpacityRuntimeEnabled() || NeedsClickThroughPolling()))
        {
            if (this.sharedInteractionPolling)
            {
                this.hoverTimer.Stop();
                return;
            }

            if (!this.hoverTimer.Enabled)
            {
                this.hoverOpacityLastUtc = DateTime.UtcNow;
                this.hoverTimer.Start();
            }

            return;
        }

        if (this.hoverTimer.Enabled)
        {
            this.hoverTimer.Stop();
        }

        if (this.hoverOpacityProgress > 0.0)
        {
            this.hoverOpacityProgress = 0.0;
            RenderLayeredWindow(false);
        }
    }

    private bool UpdateHoverOpacityAnimation()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = this.hoverOpacityLastUtc == DateTime.MinValue ? 0.03 : (now - this.hoverOpacityLastUtc).TotalSeconds;
        this.hoverOpacityLastUtc = now;

        bool hovered = IsHoverOpacityTargetActive();

        double target = hovered ? 1.0 : 0.0;
        double old = this.hoverOpacityProgress;
        double step = Math.Max(0.0, Math.Min(1.0, elapsed / 0.15));
        if (this.hoverOpacityProgress < target)
        {
            this.hoverOpacityProgress = Math.Min(target, this.hoverOpacityProgress + step);
        }
        else if (this.hoverOpacityProgress > target)
        {
            this.hoverOpacityProgress = Math.Max(target, this.hoverOpacityProgress - step);
        }

        return Math.Abs(old - this.hoverOpacityProgress) > 0.001;
    }

    private bool IsHoverOpacityTargetActive()
    {
        return HoverInteractionPolicy.IsHoverOpacityTargetActive(
            this.currentSettings,
            this.Bounds,
            this.hiddenForFullscreen,
            this.Visible,
            ref this.reverseHoverRevealUntilUtc,
            this.hoverOpacityDelayState);
    }

    private bool IsHoverOpacityRuntimeEnabled()
    {
        return this.currentSettings.HoverOpacityEnabled || this.currentSettings.ForceHoverOpacityActive;
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        if (this.hiddenForFullscreen == hidden &&
            ((hidden && !this.Visible) || (!hidden && this.Visible)))
        {
            return;
        }

        this.hiddenForFullscreen = hidden;
        if (hidden)
        {
            if (this.Visible)
            {
                this.Hide();
            }

            UpdateHoverAnimationTimer();
            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionCodexRadar();
        RenderLayeredWindow();
        UpdateHoverAnimationTimer();
    }

    private void PositionCodexRadar()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        Size desiredSize = GetDesiredCodexRadarSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.CodexRadarLeftX, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinCodexRadarHeight, this.currentSettings.CodexRadarHeight);
        int top = this.currentSettings.CodexRadarBottomY - baseHeight + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - baseHeight));
        Point shiftedLocation = BurnInProtection.ApplyRuntimeOffset(
            new Point(left, top),
            this.Size,
            workArea,
            BurnInProtection.CodexRadarSalt);
        left = shiftedLocation.X;
        top = shiftedLocation.Y;
        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private Size GetDesiredCodexRadarSize()
    {
        return new Size(this.currentSettings.CodexRadarWidth, this.currentSettings.CodexRadarHeight);
    }

    private int GetThermalAlertExtraHeight()
    {
        return Math.Max(S(24), Math.Min(S(32), (int)Math.Round(this.currentSettings.CodexRadarHeight * 0.42f)));
    }

    private void ApplyClickThroughStyle()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        bool clickThrough = ShouldClickThroughNow();
        int exStyle = NativeMethods.GetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE);
        int desired = clickThrough ?
            (exStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED) :
            ((exStyle & ~NativeMethods.WS_EX_TRANSPARENT) | NativeMethods.WS_EX_LAYERED);

        if (desired == exStyle)
        {
            return;
        }

        NativeMethods.SetWindowLong(this.Handle, NativeMethods.GWL_EXSTYLE, desired);
        NativeMethods.SetWindowPos(
            this.Handle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE |
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_FRAMECHANGED);
    }

    private bool ShouldClickThroughNow()
    {
        if (NativeMethods.IsClickThroughModifierDown())
        {
            return false;
        }

        return WidgetSettings.ShouldEnableClickThrough(
            this.currentSettings.ClickThroughMode,
            this.currentSettings.VisibilityMode);
    }

    private bool NeedsClickThroughPolling()
    {
        return WidgetSettings.ShouldEnableClickThrough(
            this.currentSettings.ClickThroughMode,
            this.currentSettings.VisibilityMode);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawCodexRadar(e.Graphics);
    }

    private void DrawCodexRadar(Graphics g)
    {
        DrawCodexRadarBackground(g);
        DrawCodexRadarContentLayer(g);
    }

    private void ConfigureCodexRadarGraphics(Graphics g)
    {
        BurnInProtection.ConfigureGraphics(g, IsBurnInColorProtectionActive());
    }

    private void DrawCodexRadarBackground(Graphics g)
    {
        ConfigureCodexRadarGraphics(g);

        int alpha = GetBackgroundOpacityAlpha();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, alpha)))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawCodexRadarContentLayer(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha >= 255)
        {
            DrawCodexRadarContent(g);
            return;
        }

        using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
        {
            contentGraphics.Clear(Color.Transparent);
            DrawCodexRadarContent(contentGraphics);
            DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
        }
    }

    private void DrawCodexRadarContent(Graphics g)
    {
        ConfigureCodexRadarGraphics(g);

        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        RectangleF textRect = new RectangleF(
            S(8),
            S(3),
            Math.Max(10, this.Width - S(16)),
            Math.Max(10, this.Height - S(6)));

        DrawCodexRadarModules(g, textRect);
    }

    private void DrawCodexRadarModules(Graphics g, RectangleF bounds)
    {
        float gap = S(4);
        float quotaWidth = Math.Max(S(230), Math.Min(S(390), bounds.Width * 0.64f));
        float minRadarWidth = Math.Max(S(116), Math.Min(S(176), bounds.Width * 0.30f));
        if (quotaWidth > bounds.Width - minRadarWidth - gap)
        {
            quotaWidth = Math.Max(S(150), bounds.Width - minRadarWidth - gap);
        }

        float radarWidth = Math.Max(0.0f, bounds.Width - quotaWidth - gap);
        if (radarWidth < S(96) && bounds.Width > S(180))
        {
            radarWidth = S(96);
            quotaWidth = Math.Max(S(44), bounds.Width - radarWidth - gap);
        }

        RectangleF radarRect = new RectangleF(bounds.Left, bounds.Top, Math.Max(0.0f, radarWidth), bounds.Height);
        RectangleF quotaRect = new RectangleF(radarRect.Right + gap, bounds.Top, Math.Max(0.0f, bounds.Right - radarRect.Right - gap), bounds.Height);
        CodexRadarSnapshot radarSnapshot = GetCodexRadarDisplaySnapshot();

        DrawCodexRadarWidget(g, radarRect, radarSnapshot);
        DrawQuotaWidget(g, quotaRect, radarSnapshot);
    }

    private void DrawCodexRadarWidget(Graphics g, RectangleF rect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= S(58) || rect.Height <= 0)
        {
            return;
        }

        float ringColumnWidth = Math.Max(S(36), Math.Min(S(50), rect.Width * 0.30f));
        float ringShiftLeft = S(5);
        float iqTextWidth = Math.Max(S(26), Math.Min(S(32), rect.Width * 0.18f));
        float iqTextShiftLeft = S(6);
        float statusGap = 0.0f;
        RectangleF ringsRect = new RectangleF(rect.Left - ringShiftLeft, rect.Top, ringColumnWidth, rect.Height);
        RectangleF iqTextRect = new RectangleF(
            rect.Left + ringColumnWidth - ringShiftLeft - iqTextShiftLeft,
            rect.Top,
            iqTextWidth,
            rect.Height);
        RectangleF fractionRect = new RectangleF(
            iqTextRect.Right + statusGap,
            rect.Top,
            Math.Max(1.0f, rect.Right - iqTextRect.Right - statusGap),
            rect.Height);
        DrawCodexConnectionFlow(g, fractionRect, snapshot);
        DrawCodexModelIqStack(g, ringsRect, iqTextRect, snapshot);
    }

    private void DrawCodexConnectionFlow(Graphics g, RectangleF rect, CodexRadarSnapshot radarSnapshot)
    {
        if (rect.Width <= S(24) || rect.Height <= S(18))
        {
            return;
        }

        bool requestRunning;
        CodexConnectionSnapshot snapshot = GetCodexConnectionDisplaySnapshot(out requestRunning);

        string topText;
        Color topColor;
        if (!TryGetCodexConnectionAlertText(snapshot, requestRunning, out topText, out topColor))
        {
            GetCodexConnectionSummary(snapshot, requestRunning, out topText, out topColor);
        }
        bool radarRequestRunning;
        lock (this.codexRadarStatusLock)
        {
            radarRequestRunning = this.codexRadarStatusRequestRunning;
        }

        string bottomText;
        Color bottomColor;
        GetCodexModelIqUpdateStatusText(
            radarSnapshot,
            radarRequestRunning,
            out bottomText,
            out bottomColor);
        if (radarRequestRunning && (this.renderTickCount & 1) == 0)
        {
            bottomColor = DesignTokens.WithAlpha(bottomColor, 104);
        }

        RectangleF topRow;
        RectangleF bottomRow;
        GetStackRowRects(rect, out topRow, out bottomRow);
        RectangleF topRect = GetCodexRadarSideTextRect(rect, topRow);
        RectangleF bottomRect = GetCodexRadarSideTextRect(rect, bottomRow);
        float textVisualOffsetY = Math.Max(1.0f, this.scale * 0.5f);
        topRect.Y -= textVisualOffsetY;
        bottomRect.Y -= textVisualOffsetY;
        float lineY = rect.Top + rect.Height * 0.50f;
        float lineLeft = rect.Left + S(7);
        float lineRight = rect.Right - S(7);
        if (lineRight <= lineLeft)
        {
            lineLeft = rect.Left + S(2);
            lineRight = rect.Right - S(2);
        }

        CodexConnectionStage[] stages = snapshot.Stages ?? new CodexConnectionStage[0];
        int stageCount = Math.Min(5, stages.Length);
        if (!snapshot.Offline && stageCount > 1)
        {
            float step = (lineRight - lineLeft) / (stageCount - 1);
            for (int i = 0; i < stageCount - 1; i++)
            {
                CodexConnectionStageState nextState = stages[i + 1] != null
                    ? stages[i + 1].State
                    : CodexConnectionStageState.Unknown;
                bool nextReachable =
                    nextState == CodexConnectionStageState.Passed ||
                    nextState == CodexConnectionStageState.Warning ||
                    nextState == CodexConnectionStageState.Unavailable;
                Color lineColor = nextReachable
                    ? DesignTokens.White(210)
                    : DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 150);
                using (Pen linePen = new Pen(lineColor, Math.Max(1.0f, S(1))))
                {
                    g.DrawLine(
                        linePen,
                        lineLeft + step * i,
                        lineY,
                        lineLeft + step * (i + 1),
                        lineY);
                }
            }
        }

        if (stageCount > 0)
        {
            float step = stageCount > 1 ? (lineRight - lineLeft) / (stageCount - 1) : 0.0f;
            float dotSize = Math.Max(S(5), Math.Min(S(8), rect.Height * 0.10f));
            for (int i = 0; i < stageCount; i++)
            {
                CodexConnectionStageState state = stages[i] != null
                    ? stages[i].State
                    : CodexConnectionStageState.Unknown;
                Color dotColor = GetCodexConnectionStageColor(state);
                float x = lineLeft + step * i;
                using (SolidBrush dotBrush = new SolidBrush(dotColor))
                {
                    g.FillEllipse(
                        dotBrush,
                        x - dotSize / 2.0f,
                        lineY - dotSize / 2.0f,
                        dotSize,
                        dotSize);
                }
            }
        }

        Font font = this.fontCache.GetUi(
            Math.Max(7.0f, Math.Min(topRow.Height * 0.36f, rect.Width * 0.13f)),
            FontStyle.Bold);
        using (SolidBrush topBrush = new SolidBrush(topColor))
        using (SolidBrush bottomBrush = new SolidBrush(bottomColor))
        {
            DrawCodexRadarFittedText(g, topText, font, topBrush, topRect, StringAlignment.Center);
            DrawCodexRadarFittedText(g, bottomText, font, bottomBrush, bottomRect, StringAlignment.Center);
        }
    }

    private CodexConnectionSnapshot GetCodexConnectionDisplaySnapshot(out bool requestRunning)
    {
        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            this.codexRadarRandomTestSnapshot != null)
        {
            requestRunning = false;
            return this.codexRadarRandomTestSnapshot.Connection != null
                ? this.codexRadarRandomTestSnapshot.Connection.Clone()
                : CodexConnectionSnapshot.CreateDefault();
        }

        lock (this.codexConnectionLock)
        {
            requestRunning = this.codexConnectionRequestRunning;
            return this.codexConnectionSnapshot != null
                ? this.codexConnectionSnapshot.Clone()
                : CodexConnectionSnapshot.CreateDefault();
        }
    }

    private bool TryGetCodexConnectionAlertText(
        CodexConnectionSnapshot snapshot,
        bool requestRunning,
        out string text,
        out Color color)
    {
        text = string.Empty;
        color = DesignTokens.White(230);
        CodexConnectionAlertCandidate[] candidates = GetCodexConnectionAlertCandidates(snapshot, requestRunning);
        if (candidates.Length == 0)
        {
            return false;
        }

        int index = Math.Max(0, Math.Min(this.codexConnectionAlertIndex, candidates.Length - 1));
        CodexConnectionAlertCandidate candidate = candidates[index];
        text = (this.codexConnectionAlertNamePhase ? candidate.Name : candidate.Reason) + "!";
        color = candidate.Color;
        return true;
    }

    private bool AdvanceCodexConnectionAlertRotation()
    {
        bool requestRunning;
        CodexConnectionSnapshot snapshot = GetCodexConnectionDisplaySnapshot(out requestRunning);
        CodexConnectionAlertCandidate[] candidates = GetCodexConnectionAlertCandidates(snapshot, requestRunning);
        if (candidates.Length == 0)
        {
            bool hadAlert = !string.IsNullOrEmpty(this.codexConnectionAlertSignature);
            this.codexConnectionAlertSignature = string.Empty;
            this.codexConnectionAlertIndex = 0;
            this.codexConnectionAlertNamePhase = true;
            return hadAlert;
        }

        string signature = BuildCodexConnectionAlertSignature(candidates);
        if (!string.Equals(signature, this.codexConnectionAlertSignature, StringComparison.Ordinal))
        {
            this.codexConnectionAlertSignature = signature;
            this.codexConnectionAlertIndex = 0;
            this.codexConnectionAlertNamePhase = true;
            return true;
        }

        if (this.codexConnectionAlertNamePhase)
        {
            this.codexConnectionAlertNamePhase = false;
        }
        else
        {
            this.codexConnectionAlertNamePhase = true;
            this.codexConnectionAlertIndex = (this.codexConnectionAlertIndex + 1) % candidates.Length;
        }

        return true;
    }

    private CodexConnectionAlertCandidate[] GetCodexConnectionAlertCandidates(
        CodexConnectionSnapshot snapshot,
        bool requestRunning)
    {
        List<CodexConnectionAlertCandidate> candidates = new List<CodexConnectionAlertCandidate>();
        if (snapshot == null || !snapshot.CheckedAtKnown)
        {
            if (requestRunning)
            {
                candidates.Add(new CodexConnectionAlertCandidate
                {
                    Key = "checking",
                    Name = "连接",
                    Reason = "检测中",
                    Color = DesignTokens.Colors.Warning
                });
            }

            return candidates.ToArray();
        }

        if (snapshot.Offline)
        {
            candidates.Add(new CodexConnectionAlertCandidate
            {
                Key = "offline",
                Name = "网络",
                Reason = "无网络",
                Color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230)
            });
            return candidates.ToArray();
        }

        CodexConnectionStage[] stages = snapshot.Stages ?? new CodexConnectionStage[0];
        int count = Math.Min(5, stages.Length);
        for (int i = 0; i < count; i++)
        {
            CodexConnectionStage stage = stages[i];
            if (stage == null ||
                (stage.State != CodexConnectionStageState.Warning &&
                 stage.State != CodexConnectionStageState.Unavailable &&
                 stage.State != CodexConnectionStageState.Blocked &&
                 stage.State != CodexConnectionStageState.Offline))
            {
                continue;
            }

            candidates.Add(new CodexConnectionAlertCandidate
            {
                Key = i.ToString(CultureInfo.InvariantCulture) + ":" + stage.State.ToString() + ":" + (stage.ErrorCode ?? string.Empty),
                Name = GetCodexConnectionAlertName(stage),
                Reason = GetCodexConnectionAlertReason(stage),
                Color = GetCodexConnectionAlertColor(stage.State)
            });
        }

        return candidates.ToArray();
    }

    private static string BuildCodexConnectionAlertSignature(CodexConnectionAlertCandidate[] candidates)
    {
        if (candidates == null || candidates.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (i > 0)
            {
                builder.Append("|");
            }

            builder.Append(candidates[i].Key);
            builder.Append(":");
            builder.Append(candidates[i].Name);
            builder.Append(":");
            builder.Append(candidates[i].Reason);
        }

        return builder.ToString();
    }

    private static string GetCodexConnectionAlertName(CodexConnectionStage stage)
    {
        if (stage == null || string.IsNullOrWhiteSpace(stage.Name))
        {
            return "连接";
        }

        return string.Equals(stage.Name, "隧道", StringComparison.OrdinalIgnoreCase)
            ? "VPN"
            : stage.Name.Trim();
    }

    private static string GetCodexConnectionAlertReason(CodexConnectionStage stage)
    {
        if (stage == null)
        {
            return "未知异常";
        }

        string code = (stage.ErrorCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            if (string.Equals(stage.Name, "DNS", StringComparison.OrdinalIgnoreCase))
            {
                return "解析失败";
            }

            if (string.Equals(stage.Name, "隧道", StringComparison.OrdinalIgnoreCase))
            {
                return "网络设置不正确";
            }

            if (string.Equals(stage.Name, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                return "服务异常";
            }

            if (string.Equals(stage.Name, "Codex", StringComparison.OrdinalIgnoreCase))
            {
                return "本地异常";
            }

            return "无法继续";
        }

        if (string.Equals(code, "STATUS", StringComparison.OrdinalIgnoreCase))
        {
            return "服务状态异常";
        }

        if (string.Equals(code, "STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return "未运行";
        }

        if (string.Equals(code, "NO_DATA", StringComparison.OrdinalIgnoreCase))
        {
            return "无额度数据";
        }

        if (string.Equals(code, "OFFLINE", StringComparison.OrdinalIgnoreCase))
        {
            return "无网络";
        }

        if (string.Equals(code, "DNS", StringComparison.OrdinalIgnoreCase))
        {
            return "解析失败";
        }

        if (string.Equals(code, "TIMEOUT", StringComparison.OrdinalIgnoreCase))
        {
            return "请求超时";
        }

        if (string.Equals(code, "TLS", StringComparison.OrdinalIgnoreCase))
        {
            return "TLS失败";
        }

        if (string.Equals(code, "TCP", StringComparison.OrdinalIgnoreCase))
        {
            return "连接失败";
        }

        return code;
    }

    private static Color GetCodexConnectionAlertColor(CodexConnectionStageState state)
    {
        if (state == CodexConnectionStageState.Warning)
        {
            return DesignTokens.Colors.Warning;
        }

        if (state == CodexConnectionStageState.Unavailable)
        {
            return DesignTokens.Colors.WarningDeep;
        }

        if (state == CodexConnectionStageState.Blocked)
        {
            return DesignTokens.Colors.DangerStrong;
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
    }

    private void GetCodexModelIqUpdateStatusText(
        CodexRadarSnapshot snapshot,
        bool requestRunning,
        out string text,
        out Color color)
    {
        if (snapshot == null || !snapshot.ModelIqRefreshedAtKnown)
        {
            text = requestRunning ? "更新中/--:--" : "等待/--:--";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
            return;
        }

        string time = TimeZoneUtilities.ConvertToDisplayTime(
                snapshot.ModelIqRefreshedAtLocal,
                this.currentSettings)
            .ToString("HH:mm", CultureInfo.CurrentCulture);
        if (IsCodexModelIqCurrentForBeijingWindow(snapshot, DateTime.UtcNow))
        {
            text = "已更新/" + time;
            color = DesignTokens.Colors.QuotaGood;
            return;
        }

        text = "未更新/" + time;
        color = DesignTokens.Colors.Warning;
    }

    private static bool IsCodexModelIqCurrentForBeijingWindow(
        CodexRadarSnapshot snapshot,
        DateTime nowUtc)
    {
        if (snapshot == null || !snapshot.ModelIqDataDateKnown)
        {
            return false;
        }

        DateTime requiredWindow = TimeZoneUtilities.GetCurrentBeijingHalfDayStart(nowUtc);
        DateTime snapshotWindow = snapshot.ModelIqDataDateLocal.Date.AddHours(
            snapshot.ModelIqDataWindowKnown
                ? (snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0)
                : 0);
        return snapshotWindow >= requiredWindow;
    }

    private static void GetCodexConnectionSummary(
        CodexConnectionSnapshot snapshot,
        bool requestRunning,
        out string text,
        out Color color)
    {
        if (snapshot == null || !snapshot.CheckedAtKnown)
        {
            text = requestRunning ? "检测中" : "等待检测";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
            return;
        }

        if (snapshot.Offline)
        {
            text = "无网络";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
            return;
        }

        CodexConnectionStage selected = FindCodexConnectionStage(
            snapshot,
            CodexConnectionStageState.Blocked);
        if (selected != null)
        {
            text = FormatCodexConnectionStageSummary(selected);
            color = DesignTokens.Colors.DangerStrong;
            return;
        }

        selected = FindCodexConnectionStage(snapshot, CodexConnectionStageState.Unavailable);
        if (selected != null)
        {
            text = FormatCodexConnectionStageSummary(selected);
            color = DesignTokens.Colors.WarningDeep;
            return;
        }

        selected = FindCodexConnectionStage(snapshot, CodexConnectionStageState.Warning);
        if (selected != null)
        {
            text = FormatCodexConnectionStageSummary(selected);
            color = DesignTokens.Colors.Warning;
            return;
        }

        text = "已通过";
        color = DesignTokens.Colors.QuotaGood;
    }

    private static CodexConnectionStage FindCodexConnectionStage(
        CodexConnectionSnapshot snapshot,
        CodexConnectionStageState state)
    {
        if (snapshot == null || snapshot.Stages == null)
        {
            return null;
        }

        for (int i = 0; i < snapshot.Stages.Length; i++)
        {
            if (snapshot.Stages[i] != null && snapshot.Stages[i].State == state)
            {
                return snapshot.Stages[i];
            }
        }

        return null;
    }

    private static string FormatCodexConnectionStageSummary(CodexConnectionStage stage)
    {
        if (stage == null)
        {
            return "未知";
        }

        string code = stage.ErrorCode ?? string.Empty;
        if (code.Length == 0 || string.Equals(code, stage.Name, StringComparison.OrdinalIgnoreCase))
        {
            return stage.Name;
        }

        return stage.Name + " " + code;
    }

    private static Color GetCodexConnectionStageColor(CodexConnectionStageState state)
    {
        if (state == CodexConnectionStageState.Passed)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 245);
        }

        if (state == CodexConnectionStageState.Warning)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
        }

        if (state == CodexConnectionStageState.Unavailable)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 245);
        }

        if (state == CodexConnectionStageState.Blocked)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.DangerStrong, 245);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 220);
    }

    private void DrawCodexModelIqStack(Graphics g, RectangleF rect, RectangleF iqTextRect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        RectangleF efficiencyRect;
        RectangleF tokenEfficiencyRect;
        GetStackRowRects(rect, out efficiencyRect, out tokenEfficiencyRect);

        DrawCodexModelSingleEfficiencyRing(g, efficiencyRect, snapshot, true);
        DrawCodexModelSingleEfficiencyData(g, iqTextRect, efficiencyRect, snapshot, true);
        DrawCodexModelSingleEfficiencyRing(g, tokenEfficiencyRect, snapshot, false);
        DrawCodexModelSingleEfficiencyData(g, iqTextRect, tokenEfficiencyRect, snapshot, false);
    }

    private void DrawCodexModelSingleEfficiencyRing(Graphics g, RectangleF rect, CodexRadarSnapshot snapshot, bool timeEfficiency)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool known = snapshot != null && snapshot.ModelIqEfficiencyKnown;
        int efficiency = known
            ? (timeEfficiency ? snapshot.ModelIqTimeEfficiencyPercent : snapshot.ModelIqTokenEfficiencyPercent)
            : 100;
        string centerText = known ? ClampEfficiencyPercent(efficiency).ToString(CultureInfo.InvariantCulture) : "-";
        float ringSize = Math.Max(S(22), Math.Min(Math.Min(rect.Height, rect.Width - S(2)), S(34)));
        RectangleF ringRect = new RectangleF(
            rect.Left + (rect.Width - ringSize) / 2.0f,
            rect.Top + (rect.Height - ringSize) / 2.0f,
            ringSize,
            ringSize);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        using (Pen basePen = new Pen(DesignTokens.WithAlpha(GetCodexRadarLightGreen(), 242), stroke))
        using (Pen lowPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 244), stroke))
        using (Pen highPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245), stroke))
        {
            basePen.StartCap = LineCap.Round;
            basePen.EndCap = LineCap.Round;
            lowPen.StartCap = LineCap.Round;
            lowPen.EndCap = LineCap.Round;
            highPen.StartCap = LineCap.Round;
            highPen.EndCap = LineCap.Round;

            g.DrawArc(basePen, arcRect, -90.0f, 360.0f);
            if (known)
            {
                int clamped = Math.Max(0, Math.Min(200, efficiency));
                if (clamped < 100)
                {
                    g.DrawArc(lowPen, arcRect, -90.0f, -360.0f * ((100 - clamped) / 100.0f));
                }
                else if (clamped > 100)
                {
                    g.DrawArc(highPen, arcRect, -90.0f, 360.0f * ((clamped - 100) / 100.0f));
                }
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(8.0f, ringSize * 0.38f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            center.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(centerText, font, brush, ringRect, center);
        }
    }

    private void DrawCodexModelSingleEfficiencyData(Graphics g, RectangleF rect, RectangleF rowRect, CodexRadarSnapshot snapshot, bool timeEfficiency)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        string text = "-";
        Color color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (snapshot != null && snapshot.ModelIqEfficiencyKnown)
        {
            int value = timeEfficiency ? snapshot.ModelIqTimeEfficiencyPercent : snapshot.ModelIqTokenEfficiencyPercent;
            GetCodexModelSingleEfficiencyLabelAndColor(value, timeEfficiency, out text, out color);
        }

        RectangleF textRect = GetCodexRadarSideTextRect(rect, rowRect);
        Font font = this.fontCache.GetUi(GetCodexRadarQuotaSideTextFontSize(rowRect), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(color))
        {
            DrawCodexRadarFittedText(g, text, font, brush, textRect, StringAlignment.Center);
        }
    }

    private void GetCodexModelSingleEfficiencyLabelAndColor(int efficiency, bool timeEfficiency, out string text, out Color color)
    {
        int lowThreshold = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent,
                timeEfficiency
                    ? this.currentSettings.CodexModelTimeEfficiencyLowThresholdPercent
                    : this.currentSettings.CodexModelTokenEfficiencyLowThresholdPercent));
        if (efficiency < lowThreshold)
        {
            text = timeEfficiency ? "耗时" : "低效";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            return;
        }

        if (efficiency > 100)
        {
            text = timeEfficiency ? "省时" : "高效";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            return;
        }

        text = "普通";
        color = DesignTokens.White(245);
    }

    private void DrawCodexModelEfficiencyRing(Graphics g, RectangleF rect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool known = snapshot != null && snapshot.ModelIqEfficiencyKnown;
        int tokenEfficiency = known ? snapshot.ModelIqTokenEfficiencyPercent : 100;
        int timeEfficiency = known ? snapshot.ModelIqTimeEfficiencyPercent : 100;
        string centerText = known
            ? GetCodexModelCompositeEfficiency(tokenEfficiency, timeEfficiency).ToString(CultureInfo.InvariantCulture)
            : "-";
        float ringSize = Math.Max(S(22), Math.Min(Math.Min(rect.Height, rect.Width - S(2)), S(34)));
        RectangleF ringRect = new RectangleF(
            rect.Left + (rect.Width - ringSize) / 2.0f,
            rect.Top + (rect.Height - ringSize) / 2.0f,
            ringSize,
            ringSize);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        using (Pen basePen = new Pen(DesignTokens.WithAlpha(GetCodexRadarLightGreen(), 242), stroke))
        using (Pen tokenLowPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 244), stroke))
        using (Pen tokenHighPen = new Pen(DesignTokens.WithAlpha(Color.FromArgb(255, 184, 54), 245), stroke))
        using (Pen timeLowPen = new Pen(DesignTokens.WithAlpha(Color.FromArgb(176, 112, 255), 244), stroke))
        using (Pen timeHighPen = new Pen(DesignTokens.WithAlpha(Color.FromArgb(255, 226, 92), 245), stroke))
        {
            basePen.StartCap = LineCap.Round;
            basePen.EndCap = LineCap.Round;
            tokenLowPen.StartCap = LineCap.Round;
            tokenLowPen.EndCap = LineCap.Round;
            tokenHighPen.StartCap = LineCap.Round;
            tokenHighPen.EndCap = LineCap.Round;
            timeLowPen.StartCap = LineCap.Round;
            timeLowPen.EndCap = LineCap.Round;
            timeHighPen.StartCap = LineCap.Round;
            timeHighPen.EndCap = LineCap.Round;

            g.DrawArc(basePen, arcRect, -90.0f, 360.0f);
            if (known)
            {
                DrawEfficiencyHalfArc(g, arcRect, tokenEfficiency, true, tokenLowPen, tokenHighPen);
                DrawEfficiencyHalfArc(g, arcRect, timeEfficiency, false, timeLowPen, timeHighPen);
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(8.0f, ringSize * 0.38f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            center.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(centerText, font, brush, ringRect, center);
        }
    }

    private static void DrawEfficiencyHalfArc(Graphics g, RectangleF arcRect, int efficiency, bool leftHalf, Pen lowPen, Pen highPen)
    {
        int clamped = Math.Max(0, Math.Min(200, efficiency));
        if (clamped == 100)
        {
            return;
        }

        float progress = Math.Min(1.0f, Math.Abs(clamped - 100) / 100.0f);
        if (leftHalf)
        {
            if (clamped < 100)
            {
                g.DrawArc(lowPen, arcRect, 90.0f, 180.0f * progress);
            }
            else
            {
                g.DrawArc(highPen, arcRect, -90.0f, -180.0f * progress);
            }

            return;
        }

        if (clamped < 100)
        {
            g.DrawArc(lowPen, arcRect, 90.0f, -180.0f * progress);
        }
        else
        {
            g.DrawArc(highPen, arcRect, -90.0f, 180.0f * progress);
        }
    }

    private void DrawCodexModelEfficiencyData(Graphics g, RectangleF rect, RectangleF efficiencyRowRect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        string text = "-";
        Color color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (snapshot != null && snapshot.ModelIqEfficiencyKnown)
        {
            GetCodexModelEfficiencyLabelAndColor(
                snapshot.ModelIqTokenEfficiencyPercent,
                snapshot.ModelIqTimeEfficiencyPercent,
                out text,
                out color);
        }

        RectangleF textRect = GetCodexRadarSideTextRect(rect, efficiencyRowRect);

        Font font = this.fontCache.GetUi(GetCodexRadarModelStateTextFontSize(rect, efficiencyRowRect), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(color))
        {
            DrawCodexRadarFittedText(g, text, font, brush, textRect, StringAlignment.Center);
        }

    }

    private static int GetCodexModelCompositeEfficiency(int tokenEfficiency, int timeEfficiency)
    {
        int token = Math.Max(0, tokenEfficiency);
        int time = Math.Max(0, timeEfficiency);
        return ClampEfficiencyPercent((int)Math.Round(Math.Sqrt(token * (double)time), MidpointRounding.AwayFromZero));
    }

    private void GetCodexModelEfficiencyLabelAndColor(
        int tokenEfficiency,
        int timeEfficiency,
        out string text,
        out Color color)
    {
        int tokenLowThreshold = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent,
                this.currentSettings.CodexModelTokenEfficiencyLowThresholdPercent));
        int timeLowThreshold = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyLowThresholdPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyLowThresholdPercent,
                this.currentSettings.CodexModelTimeEfficiencyLowThresholdPercent));
        bool tokenLow = tokenEfficiency < tokenLowThreshold;
        bool timeLow = timeEfficiency < timeLowThreshold;
        bool tokenHigh = tokenEfficiency > 100;
        bool timeHigh = timeEfficiency > 100;
        bool tokenBelowBaseline = tokenEfficiency < 100;
        bool timeBelowBaseline = timeEfficiency < 100;
        if ((tokenBelowBaseline && timeHigh) || (timeBelowBaseline && tokenHigh))
        {
            int compositeEfficiency = GetCodexModelCompositeEfficiency(tokenEfficiency, timeEfficiency);
            if (compositeEfficiency > 100)
            {
                text = "较高";
                color = DesignTokens.WithAlpha(GetCodexRadarLightGreen(), 245);
                return;
            }

            if (compositeEfficiency < 100)
            {
                text = "较低";
                color = DesignTokens.WithAlpha(GetCodexRadarLightRed(), 245);
                return;
            }

            text = "普通";
            color = DesignTokens.White(245);
            return;
        }

        if (tokenLow || timeLow)
        {
            text = "低效";
            if (tokenLow && timeLow)
            {
                color = tokenEfficiency <= timeEfficiency
                    ? DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245)
                    : DesignTokens.WithAlpha(Color.FromArgb(176, 112, 255), 245);
                return;
            }

            color = tokenLow
                ? DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245)
                : DesignTokens.WithAlpha(Color.FromArgb(176, 112, 255), 245);
            return;
        }

        if (tokenHigh || timeHigh)
        {
            text = "高效";
            if (tokenHigh && timeHigh)
            {
                color = tokenEfficiency >= timeEfficiency
                    ? DesignTokens.WithAlpha(Color.FromArgb(255, 184, 54), 245)
                    : DesignTokens.WithAlpha(Color.FromArgb(255, 226, 92), 245);
                return;
            }

            color = tokenHigh
                ? DesignTokens.WithAlpha(Color.FromArgb(255, 184, 54), 245)
                : DesignTokens.WithAlpha(Color.FromArgb(255, 226, 92), 245);
            return;
        }

        text = "普通";
        color = DesignTokens.White(245);
    }

    private static Color GetCodexRadarLightGreen()
    {
        return Color.FromArgb(142, 242, 185);
    }

    private static Color GetCodexRadarLightRed()
    {
        return Color.FromArgb(255, 151, 151);
    }

    private void DrawCodexModelIqRing(Graphics g, RectangleF rect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool known = snapshot != null && snapshot.ModelIqKnown;
        int passRatePercent = known
            ? Math.Max(0, Math.Min(MaxCodexModelIqScore, snapshot.ModelIqPassRatePercent))
            : 0;
        string centerText = known ? passRatePercent.ToString(CultureInfo.InvariantCulture) : "-";
        int passed;
        int validTasks;
        bool scoreKnown = TryGetCodexModelIqPassed(snapshot, out passed, out validTasks);
        float ringSize = Math.Max(S(22), Math.Min(Math.Min(rect.Height, rect.Width - S(2)), S(34)));
        RectangleF ringRect = new RectangleF(
            rect.Left + (rect.Width - ringSize) / 2.0f,
            rect.Top + (rect.Height - ringSize) / 2.0f,
            ringSize,
            ringSize);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        using (Pen backgroundPen = new Pen(DesignTokens.White(72), stroke))
        using (Pen baselinePen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235), stroke))
        using (Pen deficitPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 242), stroke))
        using (Pen surplusPen = new Pen(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245), stroke))
        {
            backgroundPen.StartCap = LineCap.Round;
            backgroundPen.EndCap = LineCap.Round;
            baselinePen.StartCap = LineCap.Round;
            baselinePen.EndCap = LineCap.Round;
            deficitPen.StartCap = LineCap.Round;
            deficitPen.EndCap = LineCap.Round;
            surplusPen.StartCap = LineCap.Round;
            surplusPen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            if (scoreKnown)
            {
                g.DrawArc(baselinePen, arcRect, -90.0f, 360.0f);
                double baselinePassed = GetCodexModelIqBaselinePassed(snapshot);
                double delta = passed - baselinePassed;
                if (delta < 0)
                {
                    float deficitProgress = (float)Math.Min(1.0, Math.Abs(delta) / Math.Max(1.0, baselinePassed));
                    g.DrawArc(deficitPen, arcRect, -90.0f, -360.0f * deficitProgress);
                }
                else if (delta > 0)
                {
                    double surplusCapacity = Math.Max(1.0, validTasks - baselinePassed);
                    float surplusProgress = (float)Math.Min(1.0, delta / surplusCapacity);
                    g.DrawArc(surplusPen, arcRect, -90.0f, 360.0f * surplusProgress);
                }
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(8.0f, ringSize * 0.40f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            center.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(centerText, font, brush, ringRect, center);
        }
    }

    private void DrawCodexModelIqDeltaData(Graphics g, RectangleF rect, RectangleF modelIqRowRect, CodexRadarSnapshot snapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int passed;
        int validTasks;
        bool known = TryGetCodexModelIqPassed(snapshot, out passed, out validTasks);
        string text = "-";
        Color color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (known)
        {
            double delta = passed - GetCodexModelIqBaselinePassed(snapshot);
            if (delta < -0.05)
            {
                text = "降智";
                color = DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            }
            else if (delta > 0.05)
            {
                text = "增智";
                color = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            }
            else
            {
                text = "正常";
                color = DesignTokens.White(245);
            }
        }

        RectangleF textRect = GetCodexRadarSideTextRect(rect, modelIqRowRect);

        float fontSize = GetCodexRadarModelStateTextFontSize(rect, modelIqRowRect);
        if (string.Equals(text, "降智", StringComparison.Ordinal))
        {
            fontSize *= 0.63f;
        }

        Font font = this.fontCache.GetUi(fontSize, FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(color))
        {
            DrawCodexRadarFittedText(g, text, font, brush, textRect, StringAlignment.Center);
        }

    }

    private RectangleF GetCodexRadarSideTextRect(RectangleF textColumnRect, RectangleF rowRect)
    {
        float rowHeight = rowRect.Height > 0 ? rowRect.Height : textColumnRect.Height;
        float verticalOffset = S(1);
        return new RectangleF(
            textColumnRect.Left,
            rowRect.Top + verticalOffset,
            Math.Max(1.0f, textColumnRect.Width),
            rowHeight);
    }

    private float GetCodexRadarSideTextFontSize(RectangleF textColumnRect, RectangleF rowRect)
    {
        float rowHeight = rowRect.Height > 0 ? rowRect.Height : textColumnRect.Height;
        return Math.Max(8.0f, Math.Min(rowHeight * 0.46f, textColumnRect.Width * 0.42f));
    }

    private float GetCodexRadarModelStateTextFontSize(RectangleF textColumnRect, RectangleF rowRect)
    {
        return GetCodexRadarSideTextFontSize(textColumnRect, rowRect) * 2.36f;
    }

    private float GetCodexRadarQuotaSideTextFontSize(RectangleF rowRect)
    {
        float ringSize = Math.Max(S(22), Math.Min(rowRect.Height, S(34)));
        return Math.Max(10.0f, ringSize * 0.66f);
    }

    private void DrawQuotaWidget(Graphics g, RectangleF rect, CodexRadarSnapshot radarSnapshot)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool randomTest =
            this.currentSettings.CodexRadarRandomTestEnabled &&
            this.codexRadarRandomTestSnapshot != null;
        CodexQuotaSnapshot snapshot = randomTest
            ? this.codexRadarRandomTestSnapshot.Quota
            : (this.quotaSnapshot ?? CodexQuotaSnapshot.CreateDefault());
        bool fiveHourGold;
        bool weeklyGold;
        bool codexRunning;
        int fiveHourConsumptionRingPercent;
        int weeklyConsumptionRingPercent;
        bool weeklyConsumptionRingBlocked;
        if (randomTest)
        {
            fiveHourGold = this.codexRadarRandomTestSnapshot.FiveHourGold;
            weeklyGold = this.codexRadarRandomTestSnapshot.WeeklyGold;
            codexRunning = this.codexRadarRandomTestSnapshot.CodexRunning;
            fiveHourConsumptionRingPercent = fiveHourGold
                ? 0
                : ClampPercent(snapshot.FiveHourPercent + this.codexRadarRandomTestSnapshot.FiveHourDropPercent);
            weeklyConsumptionRingPercent = weeklyGold
                ? 0
                : ClampPercent(snapshot.WeeklyPercent + this.codexRadarRandomTestSnapshot.WeeklyUsedSinceFiveHourResetPercent);
            weeklyConsumptionRingBlocked = weeklyGold;
        }
        else
        {
            bool fiveHourProtected;
            bool weeklyProtected;
            lock (this.quotaResetStateLock)
            {
                fiveHourGold = this.fiveHourQuotaProtectionGold;
                weeklyGold = this.weeklyQuotaProtectionGold;
                fiveHourProtected = this.fiveHourQuotaProtectionUtc != DateTime.MinValue;
                weeklyProtected = this.weeklyQuotaProtectionUtc != DateTime.MinValue;
            }

            codexRunning = this.quotaCodexProcessRunning;
            fiveHourConsumptionRingPercent = fiveHourProtected
                ? 0
                : (this.fiveHourConsumptionRingBaselinePercent >= 0
                    ? ClampPercent(this.fiveHourConsumptionRingBaselinePercent)
                    : 0);
            weeklyConsumptionRingPercent = this.quotaSourceKnown &&
                !fiveHourProtected &&
                this.weeklyQuotaAtFiveHourWindowStartPercent >= 0
                ? ClampPercent(this.weeklyQuotaAtFiveHourWindowStartPercent)
                : 0;
            weeklyConsumptionRingBlocked = weeklyProtected;
        }

        float statusGap = S(4);
        float statusWidth = Math.Max(S(42), Math.Min(S(52), rect.Width * 0.16f));
        float healthWidth = Math.Max(S(58), Math.Min(S(72), rect.Width * 0.20f));
        float iqBaseLeft = rect.Right - statusWidth;
        RectangleF iqStatusRect = new RectangleF(iqBaseLeft + S(5), rect.Top, statusWidth, rect.Height);
        RectangleF healthRect = new RectangleF(iqBaseLeft - statusGap - healthWidth, rect.Top, healthWidth, rect.Height);
        RectangleF rowsBounds = new RectangleF(rect.Left, rect.Top, Math.Max(0.0f, healthRect.Left - rect.Left - statusGap), rect.Height);
        if (rowsBounds.Width >= S(54))
        {
            RectangleF firstRow;
            RectangleF secondRow;
            GetStackRowRects(rowsBounds, out firstRow, out secondRow);
            DrawQuotaRow(
                g,
                firstRow,
                snapshot.FiveHourPercent,
                snapshot.FiveHourResetKnown ? snapshot.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : "N/A",
                codexRunning,
                fiveHourGold,
                fiveHourConsumptionRingPercent,
                radarSnapshot,
                false);
            DrawQuotaRow(
                g,
                secondRow,
                snapshot.WeeklyPercent,
                snapshot.WeeklyResetKnown ? snapshot.WeeklyResetLocal.ToString("MM/dd", CultureInfo.CurrentCulture) : "N/A",
                codexRunning,
                weeklyGold,
                weeklyConsumptionRingBlocked ? 0 : weeklyConsumptionRingPercent,
                radarSnapshot,
                true);
        }

        DrawServiceHealthWidget(g, healthRect);
        using (Pen divider = new Pen(DesignTokens.White(46), Math.Max(1.0f, S(1))))
        {
            g.DrawLine(
                divider,
                iqBaseLeft + S(1),
                rect.Top + S(8),
                iqBaseLeft + S(1),
                rect.Bottom - S(8));
        }

        DrawCodexModelIqStatus(g, iqStatusRect, radarSnapshot);
    }

    private void DrawCodexModelIqStatus(Graphics g, RectangleF bounds, CodexRadarSnapshot snapshot)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        RectangleF firstRow;
        RectangleF secondRow;
        GetStackRowRects(bounds, out firstRow, out secondRow);
        DrawCodexModelIqRing(g, firstRow, snapshot);
        DrawCodexModelIqDeltaData(g, bounds, secondRow, snapshot);
    }

    private void DrawServiceHealthWidget(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        bool online;
        ServiceHealthState radarHealth;
        ServiceHealthState codexHealth;
        ServiceHealthState claudeHealth;
        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            this.codexRadarRandomTestSnapshot != null)
        {
            online = this.codexRadarRandomTestSnapshot.NetworkAvailable;
            radarHealth = this.codexRadarRandomTestSnapshot.RadarHealth;
            codexHealth = this.codexRadarRandomTestSnapshot.CodexHealth;
            claudeHealth = this.codexRadarRandomTestSnapshot.ClaudeHealth;
        }
        else
        {
            lock (this.serviceHealthLock)
            {
                online = this.serviceNetworkAvailable;
                radarHealth = online ? this.radarServiceHealth : ServiceHealthState.Offline;
                codexHealth = online ? this.codexServiceHealth : ServiceHealthState.Offline;
                claudeHealth = online ? this.claudeServiceHealth : ServiceHealthState.Offline;
            }
        }

        float gap = S(1);
        float rowHeight = Math.Max(1.0f, (rect.Height - gap * 2.0f) / 3.0f);
        RectangleF radarRect = new RectangleF(rect.Left, rect.Top, rect.Width, rowHeight);
        RectangleF codexRect = new RectangleF(rect.Left, radarRect.Bottom + gap, rect.Width, rowHeight);
        RectangleF resetRect = new RectangleF(rect.Left, codexRect.Bottom + gap, rect.Width, rowHeight);

        DrawServiceHealthRow(g, radarRect, "Rader", radarHealth);
        DrawServiceHealthRow(g, codexRect, "Claude", claudeHealth);
        DrawServiceHealthRow(g, resetRect, "ChatGPT", codexHealth);
    }

    private void DrawServiceHealthRow(Graphics g, RectangleF rect, string label, ServiceHealthState state)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        Color textColor = state == ServiceHealthState.Offline
            ? DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230)
            : DesignTokens.White(245);
        bool smallCross =
            state == ServiceHealthState.Degraded ||
            state == ServiceHealthState.Incomplete ||
            state == ServiceHealthState.Offline ||
            state == ServiceHealthState.Unavailable ||
            state == ServiceHealthState.Unreachable;
        float crossReserve = smallCross ? Math.Max(S(10), rect.Height * 0.52f) + S(2) : 0.0f;
        RectangleF textRect = new RectangleF(
            rect.Left + S(1),
            rect.Top,
            Math.Max(1.0f, rect.Width - S(2) - crossReserve),
            rect.Height);

        Font font = this.fontCache.GetUi(Math.Max(8.0f, Math.Min(rect.Height * 0.56f, rect.Width * 0.18f)), FontStyle.Bold);
        using (SolidBrush textBrush = new SolidBrush(textColor))
        {
            DrawCodexRadarFittedText(g, label, font, textBrush, textRect, StringAlignment.Near);
        }

        if (smallCross)
        {
            float crossSize = Math.Max(S(8), Math.Min(S(13), rect.Height * 0.56f));
            RectangleF crossRect = new RectangleF(
                rect.Right - crossSize - S(2),
                rect.Top + (rect.Height - crossSize) / 2.0f,
                crossSize,
                crossSize);
            Color crossColor = state == ServiceHealthState.Unreachable
                ? DesignTokens.Colors.DangerStrong
                : (state == ServiceHealthState.Unavailable ||
                   state == ServiceHealthState.Degraded
                    ? DesignTokens.Colors.Warning
                    : DesignTokens.Colors.GlyphMuted);
            DrawServiceHealthSmallCross(g, crossRect, crossColor);
        }
    }

    private void DrawServiceHealthSmallCross(Graphics g, RectangleF rect, Color color)
    {
        using (Pen pen = new Pen(DesignTokens.WithAlpha(color, 230), Math.Max(1.0f, S(2))))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(pen, rect.Left, rect.Top, rect.Right, rect.Bottom);
            g.DrawLine(pen, rect.Right, rect.Top, rect.Left, rect.Bottom);
        }
    }

    private void GetStackRowRects(RectangleF bounds, out RectangleF firstRow, out RectangleF secondRow)
    {
        float rowGap = S(8);
        float rowInsetX = S(2);
        float rowTopInset = S(1);
        float rowHeight = Math.Max(1.0f, (bounds.Height - rowGap) / 2.0f);
        float rowWidth = Math.Max(1.0f, bounds.Width - rowInsetX * 2.0f);
        float contentHeight = Math.Max(1.0f, rowHeight);
        float secondTop = bounds.Top + S(4) + rowHeight + S(3);
        firstRow = new RectangleF(bounds.Left + rowInsetX, bounds.Top + rowTopInset, rowWidth, contentHeight);
        secondRow = new RectangleF(bounds.Left + rowInsetX, secondTop, rowWidth, contentHeight);
    }

    private void DrawQuotaRow(
        Graphics g,
        RectangleF rect,
        int percent,
        string resetText,
        bool codexRunning,
        bool quotaProtected,
        int consumptionRingPercent,
        CodexRadarSnapshot radarSnapshot,
        bool dateText)
    {
        percent = ClampPercent(percent);
        consumptionRingPercent = ClampPercent(consumptionRingPercent);
        float ringSize = Math.Max(S(22), Math.Min(rect.Height, S(34)));
        RectangleF ringRect = new RectangleF(rect.Left, rect.Top + (rect.Height - ringSize) / 2.0f, ringSize, ringSize);
        string displayText;
        Color displayColor;
        GetQuotaResetDisplayText(resetText, quotaProtected, radarSnapshot, dateText, out displayText, out displayColor);
        if (!codexRunning)
        {
            displayColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        Color ringColor = GetQuotaColor(percent);
        int visibleConsumptionRingPercent = Math.Max(percent, consumptionRingPercent);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        float remainingSweep = 360.0f * percent / 100.0f;
        float consumptionRingSweep = 360.0f * visibleConsumptionRingPercent / 100.0f;
        using (Pen backgroundPen = new Pen(DesignTokens.White(78), stroke))
        using (Pen valuePen = new Pen(ringColor, stroke))
        using (Pen consumptionRingPen = new Pen(GetQuotaConsumptionRingColor(), stroke))
        {
            backgroundPen.StartCap = LineCap.Flat;
            backgroundPen.EndCap = LineCap.Flat;
            valuePen.StartCap = LineCap.Round;
            valuePen.EndCap = LineCap.Round;
            consumptionRingPen.StartCap = LineCap.Round;
            consumptionRingPen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);

            // The consumption ring is a complete previous/baseline balance arc:
            // bottom ring -> consumption ring -> current balance. The current arc covers
            // the shared portion, leaving only the consumed tail visible.
            if (visibleConsumptionRingPercent > percent)
            {
                g.DrawArc(consumptionRingPen, arcRect, -90.0f, consumptionRingSweep);
            }

            if (percent > 0)
            {
                g.DrawArc(valuePen, arcRect, -90.0f, remainingSweep);
            }
        }

        Font numberFont = this.fontCache.GetUi(Math.Max(8.5f, ringSize * 0.38f), FontStyle.Bold);
        using (SolidBrush numberBrush = new SolidBrush(displayColor))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            g.DrawString(percent.ToString(CultureInfo.InvariantCulture), numberFont, numberBrush, ringRect, center);
        }

        RectangleF resetRect = new RectangleF(
            ringRect.Right + S(2),
            rect.Top,
            Math.Max(1.0f, rect.Right - ringRect.Right - S(2)),
            rect.Height);

        Font resetFont = this.fontCache.GetUi(GetCodexRadarQuotaSideTextFontSize(rect), FontStyle.Bold);
        using (SolidBrush textBrush = new SolidBrush(displayColor))
        {
            DrawCodexRadarFittedText(g, displayText, resetFont, textBrush, resetRect, StringAlignment.Near);
        }
    }

    private void GetQuotaResetDisplayText(
        string resetText,
        bool quotaProtected,
        CodexRadarSnapshot radarSnapshot,
        bool dateText,
        out string displayText,
        out Color displayColor)
    {
        if (quotaProtected)
        {
            displayText = "已重置";
            displayColor = GetCodexRadarSpeedWindowGoldColor();
            return;
        }

        if (radarSnapshot != null && radarSnapshot.SpeedWindowKnown && radarSnapshot.SpeedWindowOpen)
        {
            int phase = Math.Abs(this.renderTickCount % 3);
            if (phase == 1)
            {
                if (TryGetCodexRadarSpeedWindowEndText(radarSnapshot, dateText, out displayText))
                {
                    displayColor = DesignTokens.Colors.Warning;
                    return;
                }
            }

            if (phase == 2)
            {
                displayText = "速蹬！";
                displayColor = GetCodexRadarSpeedWindowGoldColor();
                return;
            }
        }

        displayText = resetText;
        displayColor = DesignTokens.TextStrong(226);
    }

    private bool TryGetCodexRadarSpeedWindowEndText(
        CodexRadarSnapshot snapshot,
        bool dateText,
        out string text)
    {
        text = string.Empty;
        if (snapshot == null)
        {
            return false;
        }

        DateTime local = DateTime.MinValue;
        if (snapshot.SpeedWindowClosedAtKnown)
        {
            local = snapshot.SpeedWindowClosedAtLocal;
        }

        if (local == DateTime.MinValue)
        {
            return false;
        }

        text = TimeZoneUtilities.ConvertToDisplayTime(local, this.currentSettings)
            .ToString(dateText ? "MM/dd" : "HH:mm", CultureInfo.CurrentCulture);
        return true;
    }

    private static Color GetCodexRadarSpeedWindowGoldColor()
    {
        return Color.FromArgb(255, 194, 72);
    }

    private CodexRadarSnapshot GetCodexRadarDisplaySnapshot()
    {
        CodexRadarSnapshot snapshot;
        if (this.currentSettings.CodexRadarRandomTestEnabled &&
            this.codexRadarRandomTestSnapshot != null)
        {
            return this.codexRadarRandomTestSnapshot.Radar.Clone();
        }

        if (this.currentSettings.CodexRadarTestMode != CodexRadarTestMode.Off)
        {
            snapshot = BuildTestCodexRadarSnapshot(this.currentSettings.CodexRadarTestMode);
            ApplyCodexModelEfficiencyBaselineOverride(snapshot);
            ApplyCodexModelIqTestOverride(snapshot);
            ApplyCodexModelEfficiencyTestOverride(snapshot);
            return snapshot;
        }

        lock (this.codexRadarStatusLock)
        {
            snapshot = this.codexRadarSnapshot != null
                ? this.codexRadarSnapshot.Clone()
                : CodexRadarSnapshot.CreateDefault();
        }

        ApplyCodexModelEfficiencyBaselineOverride(snapshot);
        ApplyCodexModelIqTestOverride(snapshot);
        ApplyCodexModelEfficiencyTestOverride(snapshot);
        return snapshot;
    }

    private CodexRadarSnapshot BuildTestCodexRadarSnapshot(CodexRadarTestMode mode)
    {
        DateTime now = DateTime.Now;
        CodexRadarSnapshot snapshot = CodexRadarSnapshot.CreateDefault();
        snapshot.CheckedAtLocal = now;
        snapshot.CheckedAtKnown = true;
        snapshot.ModelIqKnown = true;
        if (mode == CodexRadarTestMode.Open)
        {
            ApplyCodexModelIqScore(snapshot, 9);
            return snapshot;
        }

        if (mode == CodexRadarTestMode.Closed)
        {
            ApplyCodexModelIqScore(snapshot, 6);
            return snapshot;
        }

            ApplyCodexModelIqScore(snapshot, GetCodexModelIqAbsoluteBaselinePassed());
        return snapshot;
    }

    private void ApplyCodexModelIqTestOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !this.currentSettings.CodexModelIqTestEnabled)
        {
            return;
        }

        ApplyCodexModelIqScore(snapshot, this.currentSettings.CodexModelIqTestPassed);
    }

    private void ApplyCodexModelEfficiencyBaselineOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ModelIqEfficiencyInputKnown)
        {
            return;
        }

        bool changed = false;
        int tokenEfficiency;
        if (TryCalculateCodexModelEfficiencyPercentForMode(
            snapshot,
            true,
            this.currentSettings.CodexModelTokenEfficiencyBaselineMode,
            out tokenEfficiency))
        {
            snapshot.ModelIqTokenEfficiencyPercent = tokenEfficiency;
            changed = true;
        }

        int timeEfficiency;
        if (TryCalculateCodexModelEfficiencyPercentForMode(
            snapshot,
            false,
            this.currentSettings.CodexModelTimeEfficiencyBaselineMode,
            out timeEfficiency))
        {
            snapshot.ModelIqTimeEfficiencyPercent = timeEfficiency;
            changed = true;
        }

        if (changed)
        {
            snapshot.ModelIqEfficiencyKnown = true;
        }
    }

    private void ApplyCodexModelEfficiencyTestOverride(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !this.currentSettings.CodexModelEfficiencyTestEnabled)
        {
            return;
        }

        snapshot.ModelIqTokenEfficiencyPercent = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyPercent,
                this.currentSettings.CodexModelTokenEfficiencyTestPercent));
        snapshot.ModelIqTimeEfficiencyPercent = Math.Max(
            WidgetSettings.MinCodexModelEfficiencyPercent,
            Math.Min(
                WidgetSettings.MaxCodexModelEfficiencyPercent,
                this.currentSettings.CodexModelTimeEfficiencyTestPercent));
        snapshot.ModelIqEfficiencyKnown = true;
    }

    private bool TryCalculateCodexModelEfficiencyPercentForMode(
        CodexRadarSnapshot snapshot,
        bool tokenEfficiency,
        CodexModelBaselineMode mode,
        out int efficiencyPercent)
    {
        efficiencyPercent = 100;
        if (snapshot == null || !snapshot.ModelIqEfficiencyInputKnown)
        {
            return false;
        }

        if (mode == CodexModelBaselineMode.Absolute)
        {
            return TryCalculateCodexModelEfficiencyPercent(
                snapshot.ModelIqEfficiencyPassed,
                tokenEfficiency ? snapshot.ModelIqEfficiencyTotalTokens : snapshot.ModelIqEfficiencySerialSeconds,
                tokenEfficiency
                    ? this.currentSettings.CodexModelTokenEfficiencyBaselinePassed
                    : this.currentSettings.CodexModelTimeEfficiencyBaselinePassed,
                tokenEfficiency
                    ? this.currentSettings.CodexModelTokenEfficiencyBaselineTokens
                    : this.currentSettings.CodexModelTimeEfficiencyBaselineSeconds,
                out efficiencyPercent);
        }

        double baselinePassed;
        double baselineValue;
        if (!TryGetCodexModelEfficiencyBaseline(
            snapshot,
            tokenEfficiency,
            mode,
            out baselinePassed,
            out baselineValue))
        {
            return false;
        }

        return TryCalculateCodexModelEfficiencyPercent(
            snapshot.ModelIqEfficiencyPassed,
            tokenEfficiency ? snapshot.ModelIqEfficiencyTotalTokens : snapshot.ModelIqEfficiencySerialSeconds,
            baselinePassed,
            baselineValue,
            out efficiencyPercent);
    }

    private static bool TryCalculateCodexModelEfficiencyPercent(
        double currentPassed,
        double currentValue,
        double baselinePassed,
        double baselineValue,
        out int efficiencyPercent)
    {
        efficiencyPercent = 100;
        if (currentPassed <= 0.0 || currentValue <= 0.0 || baselinePassed <= 0.0 || baselineValue <= 0.0)
        {
            return false;
        }

        double baselineRate = baselinePassed / baselineValue;
        if (baselineRate <= 0.0)
        {
            return false;
        }

        efficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentValue) / baselineRate * 100.0, MidpointRounding.AwayFromZero));
        return true;
    }

    private void ApplyCodexModelIqScore(CodexRadarSnapshot snapshot, int passed)
    {
        if (snapshot == null)
        {
            return;
        }

        int validTasks = CodexModelIqNominalTasks;
        passed = Math.Max(WidgetSettings.MinCodexModelIqPassed, Math.Min(validTasks, passed));
        snapshot.ModelIqKnown = true;
        snapshot.ModelIqPassedKnown = true;
        snapshot.ModelIqPassed = passed;
        snapshot.ModelIqValidTasks = validTasks;
        snapshot.ModelIqPassRatePercent = NormalizePassRatePercent(
            passed / (double)Math.Max(1, GetCodexModelIqAbsoluteBaselinePassed()) * 100.0);
        snapshot.ModelIqTokenEfficiencyPercent = 100;
        snapshot.ModelIqTimeEfficiencyPercent = 100;
        snapshot.ModelIqEfficiencyKnown = true;
        if (!snapshot.ModelIqDataDateKnown)
        {
            DateTime beijingWindow = TimeZoneUtilities.GetCurrentBeijingHalfDayStart(DateTime.UtcNow);
            snapshot.ModelIqDataDateLocal = beijingWindow.Date;
            snapshot.ModelIqDataWindowStartHourLocal = beijingWindow.Hour >= 12 ? 12 : 0;
            snapshot.ModelIqDataDateKnown = true;
            snapshot.ModelIqDataWindowKnown = true;
        }

        if (!snapshot.ModelIqRefreshedAtKnown)
        {
            snapshot.ModelIqRefreshedAtLocal = DateTime.Now;
            snapshot.ModelIqRefreshedAtKnown = true;
        }

        snapshot.ModelIqRefreshSucceeded = true;
        int delta = passed - GetCodexModelIqAbsoluteBaselinePassed();
        snapshot.ModelIqStatus = delta < 0 ? "red" : "green";
        UpsertCodexModelHistoryPoint(
            snapshot.ModelIqHistory,
            snapshot.ModelIqDataDateLocal.Date.AddHours(snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0),
            snapshot.ModelIqPassRatePercent);
        snapshot.ModelIqHistory = NormalizeCodexModelHistory(snapshot.ModelIqHistory);
    }

    private int GetCodexModelIqAbsoluteBaselinePassed()
    {
        return Math.Max(
            WidgetSettings.MinCodexModelIqPassed,
            Math.Min(CodexModelIqNominalTasks, this.currentSettings.CodexModelIqBaselinePassed));
    }

    private double GetCodexModelIqBaselinePassed(CodexRadarSnapshot snapshot)
    {
        if (this.currentSettings.CodexModelIqBaselineMode == CodexModelBaselineMode.Absolute)
        {
            return GetCodexModelIqAbsoluteBaselinePassed();
        }

        double average;
        if (TryGetAverageCodexModelPassed(
            snapshot,
            this.currentSettings.CodexModelIqBaselineMode,
            out average))
        {
            return Math.Max(
                WidgetSettings.MinCodexModelIqPassed,
                Math.Min(CodexModelIqNominalTasks, average));
        }

        return GetCodexModelIqAbsoluteBaselinePassed();
    }

    private static bool TryGetAverageCodexModelPassed(
        CodexRadarSnapshot snapshot,
        CodexModelBaselineMode mode,
        out double average)
    {
        average = 0.0;
        List<CodexModelHistoryPoint> points = SelectCodexModelBaselinePoints(snapshot, mode);
        double total = 0.0;
        int count = 0;
        for (int i = 0; i < points.Count; i++)
        {
            CodexModelHistoryPoint point = points[i];
            if (point != null && point.Passed > 0.0)
            {
                total += point.Passed;
                count++;
            }
        }

        if (count == 0)
        {
            return false;
        }

        average = total / count;
        return true;
    }

    private static bool TryGetCodexModelEfficiencyBaseline(
        CodexRadarSnapshot snapshot,
        bool tokenEfficiency,
        CodexModelBaselineMode mode,
        out double baselinePassed,
        out double baselineValue)
    {
        baselinePassed = 0.0;
        baselineValue = 0.0;
        List<CodexModelHistoryPoint> points = SelectCodexModelBaselinePoints(snapshot, mode);
        for (int i = 0; i < points.Count; i++)
        {
            CodexModelHistoryPoint point = points[i];
            if (point == null || point.Passed <= 0.0)
            {
                continue;
            }

            double value = tokenEfficiency ? point.TotalTokens : point.SerialSeconds;
            if (value <= 0.0)
            {
                continue;
            }

            baselinePassed += point.Passed;
            baselineValue += value;
        }

        return baselinePassed > 0.0 && baselineValue > 0.0;
    }

    private static List<CodexModelHistoryPoint> SelectCodexModelBaselinePoints(
        CodexRadarSnapshot snapshot,
        CodexModelBaselineMode mode)
    {
        List<CodexModelHistoryPoint> all = GetRecentCodexModelHistory(snapshot);
        if (all.Count == 0)
        {
            return all;
        }

        int requestedCount = 0;
        if (mode == CodexModelBaselineMode.Recent7Average)
        {
            requestedCount = 7;
        }
        else if (mode == CodexModelBaselineMode.Recent30Average)
        {
            requestedCount = 30;
        }

        if (requestedCount <= 0)
        {
            return all;
        }

        if (all.Count < requestedCount)
        {
            return all;
        }

        return all.GetRange(all.Count - requestedCount, requestedCount);
    }

    private static bool TryGetCodexModelIqPassed(CodexRadarSnapshot snapshot, out int passed, out int validTasks)
    {
        passed = 0;
        validTasks = CodexModelIqNominalTasks;
        if (snapshot == null || !snapshot.ModelIqKnown)
        {
            return false;
        }

        validTasks = snapshot.ModelIqValidTasks > 0 ? snapshot.ModelIqValidTasks : CodexModelIqNominalTasks;
        validTasks = Math.Max(1, Math.Min(CodexModelIqNominalTasks, validTasks));
        if (snapshot.ModelIqPassedKnown)
        {
            passed = Math.Max(0, Math.Min(validTasks, snapshot.ModelIqPassed));
            return true;
        }

        passed = (int)Math.Round(
            Math.Max(0, Math.Min(MaxCodexModelIqScore, snapshot.ModelIqPassRatePercent)) /
                100.0 * WidgetSettings.DefaultCodexModelIqBaselinePassed,
            MidpointRounding.AwayFromZero);
        passed = Math.Max(0, Math.Min(validTasks, passed));
        return true;
    }

    private static Color GetCodexModelIqStatusColor(string status, bool known)
    {
        if (!known)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 235);
        }

        string normalized = NormalizeCodexModelIqStatus(status);
        if (normalized == "green")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235);
        }

        if (normalized == "yellow")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 238);
        }

        if (normalized == "orange")
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 238);
    }

    private bool RefreshQuotaInfoIfNeeded()
    {
        DateTime nowUtc = DateTime.UtcNow;
        DateTime nowLocal = DateTime.Now;
        bool codexProcessChanged;
        bool codexRunning = UpdateCodexProcessRunningStatus(nowUtc, out codexProcessChanged);
        bool resetDue = IsQuotaResetDue(this.quotaSnapshot, nowLocal);
        // Active Codex sessions need prompt quota updates; inactive sessions use a much slower
        // schedule unless a reset boundary or process transition requires an immediate read.
        bool refreshDue =
            resetDue ||
            this.lastQuotaRefreshUtc == DateTime.MinValue ||
            (codexProcessChanged && codexRunning);

        if (!refreshDue)
        {
            if (codexRunning)
            {
                refreshDue = (nowUtc - this.lastQuotaRefreshUtc).TotalSeconds >= GetQuotaActiveRefreshSeconds();
            }
            else
            {
                refreshDue = IsInactiveQuotaRefreshDue(nowUtc);
            }
        }

        if (!refreshDue)
        {
            return codexProcessChanged;
        }

        if (!codexRunning)
        {
            MarkInactiveQuotaRefresh(nowUtc);
        }

        if (resetDue)
        {
            ActivateDueQuotaResetProtections(this.quotaSnapshot, nowLocal, nowUtc);
        }

        this.lastQuotaRefreshUtc = nowUtc;
        bool quotaKnown = false;
        CodexQuotaSnapshot nextSnapshot;
        long quotaReadStart = TimingStats.StartTimestamp();
        try
        {
            nextSnapshot = ReadQuotaSnapshot(out quotaKnown);
        }
        finally
        {
            TimingStats.RecordElapsed("codex.quota_read", quotaReadStart);
        }

        if (IsQuotaResetDue(nextSnapshot, nowLocal))
        {
            ActivateDueQuotaResetProtections(nextSnapshot, nowLocal, nowUtc);
        }

        UpdateQuotaReadDeltaTracking(nextSnapshot, quotaKnown);
        this.quotaSnapshot = ApplyQuotaResetProtections(nextSnapshot);
        this.quotaSourceKnown = quotaKnown;
        UpdateCodexServiceHealth(quotaKnown);
        return true;
    }

    private void InitializeQuotaReadDeltaTracking(CodexQuotaSnapshot snapshot, bool sourceKnown)
    {
        if (!sourceKnown || snapshot == null)
        {
            ResetQuotaReadDeltaTracking();
            return;
        }

        this.lastFiveHourQuotaReadPercent = ClampPercent(snapshot.FiveHourPercent);
        this.lastWeeklyQuotaReadPercent = ClampPercent(snapshot.WeeklyPercent);
        this.lastQuotaReadDeltaSourceUtc = snapshot.SourceUpdatedKnown
            ? snapshot.SourceUpdatedUtc
            : DateTime.MinValue;
        this.fiveHourConsumptionRingBaselinePercent = -1;
        this.trackedFiveHourResetLocal = snapshot.FiveHourResetKnown
            ? snapshot.FiveHourResetLocal
            : DateTime.MinValue;
        this.weeklyQuotaAtFiveHourWindowStartPercent = ClampPercent(snapshot.WeeklyPercent);
    }

    private void UpdateQuotaReadDeltaTracking(CodexQuotaSnapshot snapshot, bool sourceKnown)
    {
        if (!sourceKnown || snapshot == null)
        {
            ResetQuotaReadDeltaTracking();
            return;
        }

        int fiveHourPercent = ClampPercent(snapshot.FiveHourPercent);
        int weeklyPercent = ClampPercent(snapshot.WeeklyPercent);
        DateTime sourceUtc = snapshot.SourceUpdatedKnown
            ? snapshot.SourceUpdatedUtc
            : DateTime.MinValue;
        DateTime fiveHourResetLocal = snapshot.FiveHourResetKnown
            ? snapshot.FiveHourResetLocal
            : DateTime.MinValue;
        if (this.lastFiveHourQuotaReadPercent < 0 || this.lastWeeklyQuotaReadPercent < 0)
        {
            InitializeQuotaReadDeltaTracking(snapshot, true);
            return;
        }

        if (sourceUtc != DateTime.MinValue &&
            this.lastQuotaReadDeltaSourceUtc != DateTime.MinValue &&
            sourceUtc < this.lastQuotaReadDeltaSourceUtc)
        {
            return;
        }

        if (sourceUtc != DateTime.MinValue &&
            this.lastQuotaReadDeltaSourceUtc != DateTime.MinValue &&
            sourceUtc == this.lastQuotaReadDeltaSourceUtc &&
            fiveHourPercent == this.lastFiveHourQuotaReadPercent &&
            weeklyPercent == this.lastWeeklyQuotaReadPercent &&
            (fiveHourResetLocal == DateTime.MinValue || fiveHourResetLocal == this.trackedFiveHourResetLocal))
        {
            return;
        }

        bool fiveHourChanged = fiveHourPercent != this.lastFiveHourQuotaReadPercent;
        bool weeklyChanged = weeklyPercent != this.lastWeeklyQuotaReadPercent;
        int nextFiveHourConsumptionRingBaseline = GetNextFiveHourConsumptionRingBaseline(
            this.fiveHourConsumptionRingBaselinePercent,
            this.lastFiveHourQuotaReadPercent,
            fiveHourPercent);
        bool fiveHourResetMoved =
            fiveHourResetLocal != DateTime.MinValue &&
            this.trackedFiveHourResetLocal != DateTime.MinValue &&
            fiveHourResetLocal > this.trackedFiveHourResetLocal.AddMinutes(1.0);
        bool fiveHourResetBecameKnown =
            fiveHourResetLocal != DateTime.MinValue &&
            this.trackedFiveHourResetLocal == DateTime.MinValue;
        bool fiveHourWindowAdvanced =
            fiveHourResetMoved ||
            (fiveHourChanged && fiveHourPercent > this.lastFiveHourQuotaReadPercent);
        bool weeklyWindowAdvanced = weeklyChanged && weeklyPercent > this.lastWeeklyQuotaReadPercent;
        if (!fiveHourChanged && !weeklyChanged)
        {
            // A newer log can repeat the same rounded balance. Keep the previous visible
            // consumption baseline until a real decrease or reset/increase changes it.
            this.fiveHourConsumptionRingBaselinePercent = nextFiveHourConsumptionRingBaseline;
            if (fiveHourResetMoved || fiveHourResetBecameKnown)
            {
                this.trackedFiveHourResetLocal = fiveHourResetLocal;
                this.weeklyQuotaAtFiveHourWindowStartPercent = weeklyPercent;
            }

            if (sourceUtc != DateTime.MinValue)
            {
                this.lastQuotaReadDeltaSourceUtc = sourceUtc;
            }

            return;
        }

        if (fiveHourWindowAdvanced || weeklyWindowAdvanced || this.weeklyQuotaAtFiveHourWindowStartPercent < 0)
        {
            this.weeklyQuotaAtFiveHourWindowStartPercent = weeklyPercent;
        }

        if (fiveHourResetLocal != DateTime.MinValue)
        {
            this.trackedFiveHourResetLocal = fiveHourResetLocal;
        }

        if (fiveHourChanged)
        {
            this.fiveHourConsumptionRingBaselinePercent = nextFiveHourConsumptionRingBaseline;
            this.lastFiveHourQuotaReadPercent = fiveHourPercent;
        }

        if (weeklyChanged)
        {
            this.lastWeeklyQuotaReadPercent = weeklyPercent;
        }

        if (sourceUtc != DateTime.MinValue)
        {
            this.lastQuotaReadDeltaSourceUtc = sourceUtc;
        }
    }

    private static int GetNextFiveHourConsumptionRingBaseline(
        int currentBaselinePercent,
        int previousBalancePercent,
        int currentBalancePercent)
    {
        previousBalancePercent = ClampPercent(previousBalancePercent);
        currentBalancePercent = ClampPercent(currentBalancePercent);
        if (currentBalancePercent == previousBalancePercent)
        {
            return currentBaselinePercent >= 0
                ? ClampPercent(currentBaselinePercent)
                : -1;
        }

        return previousBalancePercent > currentBalancePercent
            ? previousBalancePercent
            : -1;
    }

    private void ResetQuotaReadDeltaTracking()
    {
        this.lastFiveHourQuotaReadPercent = -1;
        this.lastWeeklyQuotaReadPercent = -1;
        this.lastQuotaReadDeltaSourceUtc = DateTime.MinValue;
        this.fiveHourConsumptionRingBaselinePercent = -1;
        this.trackedFiveHourResetLocal = DateTime.MinValue;
        this.weeklyQuotaAtFiveHourWindowStartPercent = -1;
    }

    private void UpdateServiceConnectivityHealth()
    {
        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            // NetworkChange callbacks only set this flag; the UI scheduler performs the actual query.
            if (!this.serviceNetworkRefreshRequested)
            {
                return;
            }

            this.serviceNetworkRefreshRequested = false;
        }

        bool networkAvailable = IsNetworkAvailable();
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkAvailable = networkAvailable;
            if (!networkAvailable)
            {
                this.radarServiceHealth = ServiceHealthState.Offline;
                this.codexServiceHealth = ServiceHealthState.Offline;
                this.claudeServiceHealth = ServiceHealthState.Offline;
                return;
            }

            if (this.radarServiceHealth == ServiceHealthState.Offline)
            {
                this.radarServiceHealth = ServiceHealthState.Unknown;
            }

            if (this.claudeServiceHealth == ServiceHealthState.Offline)
            {
                this.claudeServiceHealth = ServiceHealthState.Unknown;
            }

            this.codexServiceHealth = this.quotaSourceKnown ? ServiceHealthState.Normal : ServiceHealthState.Unavailable;
        }
    }

    private void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        RequestServiceNetworkRefresh();
        RequestCodexConnectionRefresh();
    }

    private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        RequestServiceNetworkRefresh();
        RequestCodexConnectionRefresh();
    }

    private void RequestServiceNetworkRefresh()
    {
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkRefreshRequested = true;
        }
    }

    private void RequestCodexConnectionRefresh()
    {
        lock (this.codexConnectionLock)
        {
            this.nextCodexConnectionRefreshUtc = DateTime.UtcNow;
        }
    }

    private void UpdateCodexServiceHealth(bool quotaKnown)
    {
        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.codexServiceHealth = this.serviceNetworkAvailable
                ? (quotaKnown ? ServiceHealthState.Normal : ServiceHealthState.Unavailable)
                : ServiceHealthState.Offline;
        }
    }

    private bool IsServiceNetworkAvailable()
    {
        lock (this.serviceHealthLock)
        {
            return this.serviceNetworkAvailable;
        }
    }

    private bool ShouldForceServiceHealthRefresh(ServiceHealthState state)
    {
        lock (this.serviceHealthLock)
        {
            return state == ServiceHealthState.Unknown || state == ServiceHealthState.Offline;
        }
    }

    private void SetRadarServiceHealth(ServiceHealthState health)
    {
        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.radarServiceHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
        }
    }

    private void SetClaudeServiceHealth(ServiceHealthState health)
    {
        if (this.currentSettings.ServiceHealthTestMode != ServiceHealthTestMode.Off)
        {
            ApplyServiceHealthTestMode();
            return;
        }

        lock (this.serviceHealthLock)
        {
            this.claudeServiceHealth = this.serviceNetworkAvailable ? health : ServiceHealthState.Offline;
        }
    }

    private void ApplyServiceHealthTestMode()
    {
        ServiceHealthTestMode mode = this.currentSettings.ServiceHealthTestMode;
        if (mode == ServiceHealthTestMode.Off)
        {
            return;
        }

        ServiceHealthState state = ConvertServiceHealthTestMode(mode);
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkAvailable = mode != ServiceHealthTestMode.Offline;
            this.radarServiceHealth = state;
            this.codexServiceHealth = state;
            this.claudeServiceHealth = state;
        }
    }

    private void ResetServiceHealthAfterTestMode()
    {
        bool networkAvailable = IsNetworkAvailable();
        lock (this.serviceHealthLock)
        {
            this.serviceNetworkRefreshRequested = false;
            this.serviceNetworkAvailable = networkAvailable;
            this.radarServiceHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
            this.codexServiceHealth = networkAvailable
                ? (this.quotaSourceKnown ? ServiceHealthState.Normal : ServiceHealthState.Unavailable)
                : ServiceHealthState.Offline;
            this.claudeServiceHealth = networkAvailable ? ServiceHealthState.Unknown : ServiceHealthState.Offline;
        }

        lock (this.claudeStatusLock)
        {
            this.nextClaudeStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
        }

        lock (this.codexRadarStatusLock)
        {
            this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(4.0);
        }
    }

    private static ServiceHealthState ConvertServiceHealthTestMode(ServiceHealthTestMode mode)
    {
        if (mode == ServiceHealthTestMode.Normal)
        {
            return ServiceHealthState.Normal;
        }

        if (mode == ServiceHealthTestMode.Offline)
        {
            return ServiceHealthState.Offline;
        }

        if (mode == ServiceHealthTestMode.Unavailable)
        {
            return ServiceHealthState.Unavailable;
        }

        if (mode == ServiceHealthTestMode.Unreachable)
        {
            return ServiceHealthState.Unreachable;
        }

        return ServiceHealthState.Unknown;
    }

    private static bool IsNetworkAvailable()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;
        }
    }

    private void RefreshCodexConnectionIfNeeded()
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (this.codexConnectionLock)
        {
            if (this.codexConnectionRequestRunning ||
                (this.nextCodexConnectionRefreshUtc != DateTime.MinValue &&
                 nowUtc < this.nextCodexConnectionRefreshUtc))
            {
                return;
            }

            this.codexConnectionRequestRunning = true;
        }

        Task.Run((Action)delegate
        {
            CodexConnectionSnapshot snapshot;
            try
            {
                snapshot = BuildCodexConnectionSnapshot();
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                snapshot = CodexConnectionSnapshot.CreateDefault();
                snapshot.CheckedAtUtc = DateTime.UtcNow;
                snapshot.CheckedAtKnown = true;
                snapshot.Stages[0].State = CodexConnectionStageState.Blocked;
                snapshot.Stages[0].ErrorCode = "ERROR";
            }

            lock (this.codexConnectionLock)
            {
                this.codexConnectionSnapshot = snapshot;
                this.codexConnectionRequestRunning = false;
                this.nextCodexConnectionRefreshUtc =
                    DateTime.UtcNow + GetCodexConnectionRefreshInterval(snapshot);
            }

            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            RenderLayeredWindow();
                        }
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private CodexConnectionSnapshot BuildCodexConnectionSnapshot()
    {
        CodexConnectionSnapshot snapshot = CodexConnectionSnapshot.CreateDefault();
        snapshot.CheckedAtUtc = DateTime.UtcNow;
        snapshot.CheckedAtKnown = true;
        if (!IsNetworkAvailable() || !HasActiveNetworkInterface())
        {
            snapshot.Offline = true;
            for (int i = 0; i < snapshot.Stages.Length; i++)
            {
                snapshot.Stages[i].State = CodexConnectionStageState.Offline;
                snapshot.Stages[i].ErrorCode = "OFFLINE";
            }

            return snapshot;
        }

        snapshot.Stages[0].State = CodexConnectionStageState.Passed;

        string dnsError;
        if (!TryResolveCodexHost(out dnsError))
        {
            snapshot.Stages[1].State = CodexConnectionStageState.Blocked;
            snapshot.Stages[1].ErrorCode = dnsError;
            MarkRemainingCodexConnectionStages(snapshot, 2, CodexConnectionStageState.Unknown);
            return snapshot;
        }

        snapshot.Stages[1].State = CodexConnectionStageState.Passed;
        ProbeCodexTunnel(snapshot.Stages[2]);
        ProbeOpenAiStatus(snapshot.Stages[3]);
        ProbeLocalCodexState(snapshot.Stages[4]);
        return snapshot;
    }

    private static bool HasActiveNetworkInterface()
    {
        try
        {
            NetworkInterface[] interfaces = NetworkInterface.GetAllNetworkInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
            {
                NetworkInterface item = interfaces[i];
                if (item != null &&
                    item.OperationalStatus == OperationalStatus.Up &&
                    item.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    item.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                {
                    return true;
                }
            }
        }
        catch
        {
            return IsNetworkAvailable();
        }

        return false;
    }

    private static bool TryResolveCodexHost(out string errorCode)
    {
        errorCode = "DNS";
        IAsyncResult result = null;
        try
        {
            result = Dns.BeginGetHostAddresses("chatgpt.com", null, null);
            if (!result.AsyncWaitHandle.WaitOne(CodexConnectionDnsTimeoutMs))
            {
                errorCode = "TIMEOUT";
                return false;
            }

            IPAddress[] addresses = Dns.EndGetHostAddresses(result);
            return addresses != null && addresses.Length > 0;
        }
        catch (SocketException)
        {
            errorCode = "DNS";
            return false;
        }
        catch
        {
            errorCode = "ERROR";
            return false;
        }
        finally
        {
            if (result != null && result.AsyncWaitHandle != null)
            {
                result.AsyncWaitHandle.Close();
            }
        }
    }

    private static void ProbeCodexTunnel(CodexConnectionStage stage)
    {
        int statusCode;
        string errorCode;
        if (!TryProbeHttpEndpoint(ChatGptProbeUrl, out statusCode, out errorCode))
        {
            stage.State = CodexConnectionStageState.Blocked;
            stage.ErrorCode = errorCode;
            return;
        }

        stage.ErrorCode = statusCode > 0
            ? "HTTP" + statusCode.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        // Any HTTP response proves DNS, TCP, TLS, and the tunnel route are reachable.
        stage.State = CodexConnectionStageState.Passed;
        stage.ErrorCode = string.Empty;
    }

    private static void ProbeOpenAiStatus(CodexConnectionStage stage)
    {
        try
        {
            HttpWebRequest request = CreateCodexConnectionRequest(OpenAiStatusUrl);
            request.Accept = "application/json,text/plain,*/*";
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                int statusCode = httpResponse != null ? (int)httpResponse.StatusCode : 200;
                if (statusCode < 200 || statusCode >= 300 || stream == null)
                {
                    stage.State = CodexConnectionStageState.Unavailable;
                    stage.ErrorCode = "HTTP" + statusCode.ToString(CultureInfo.InvariantCulture);
                    return;
                }

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    JavaScriptSerializer serializer = new JavaScriptSerializer();
                    Dictionary<string, object> root =
                        serializer.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                    Dictionary<string, object> status = GetQuotaObject(root, "status");
                    string indicator = GetQuotaString(status, "indicator").Trim();
                    stage.State = GetOpenAiCodexComponentState(root, indicator);
                    stage.ErrorCode = stage.State == CodexConnectionStageState.Passed
                        ? string.Empty
                        : "STATUS";
                }
            }
        }
        catch (WebException ex)
        {
            int statusCode;
            if (TryGetWebExceptionStatusCode(ex, out statusCode))
            {
                stage.State = CodexConnectionStageState.Unavailable;
                stage.ErrorCode = "HTTP" + statusCode.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                stage.State = CodexConnectionStageState.Blocked;
                stage.ErrorCode = GetWebExceptionCode(ex);
            }
        }
        catch
        {
            stage.State = CodexConnectionStageState.Blocked;
            stage.ErrorCode = "ERROR";
        }
    }

    private static CodexConnectionStageState GetOpenAiCodexComponentState(
        Dictionary<string, object> root,
        string indicator)
    {
        if (root == null)
        {
            return CodexConnectionStageState.Unavailable;
        }

        object rawComponents;
        object[] components = root.TryGetValue("components", out rawComponents)
            ? rawComponents as object[]
            : null;

        string[] relevantNames = new string[]
        {
            "CLI",
            "VS Code extension",
            "Codex Web",
            "App",
            "Codex API",
            "Login"
        };
        bool relevantComponentFound = false;
        bool warningFound = false;
        bool unavailableFound = false;
        if (components != null)
        {
            for (int i = 0; i < components.Length; i++)
            {
                Dictionary<string, object> component = components[i] as Dictionary<string, object>;
                string name = GetQuotaString(component, "name");
                bool relevant = false;
                for (int j = 0; j < relevantNames.Length; j++)
                {
                    if (string.Equals(name, relevantNames[j], StringComparison.OrdinalIgnoreCase))
                    {
                        relevant = true;
                        break;
                    }
                }

                if (!relevant)
                {
                    continue;
                }

                relevantComponentFound = true;
                string componentStatus = GetQuotaString(component, "status").Trim();
                if (string.Equals(componentStatus, "operational", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (string.Equals(componentStatus, "partial_outage", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(componentStatus, "major_outage", StringComparison.OrdinalIgnoreCase))
                {
                    unavailableFound = true;
                }
                else
                {
                    warningFound = true;
                }
            }
        }

        // The global Statuspage indicator may describe an unrelated product or region.
        // Once Codex-related components are present, only their states may color this node.
        if (relevantComponentFound)
        {
            if (unavailableFound)
            {
                return CodexConnectionStageState.Unavailable;
            }

            return warningFound
                ? CodexConnectionStageState.Warning
                : CodexConnectionStageState.Passed;
        }

        if (string.Equals(indicator, "major", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(indicator, "critical", StringComparison.OrdinalIgnoreCase))
        {
            return CodexConnectionStageState.Unavailable;
        }

        if (string.Equals(indicator, "minor", StringComparison.OrdinalIgnoreCase))
        {
            return CodexConnectionStageState.Warning;
        }

        return string.Equals(indicator, "none", StringComparison.OrdinalIgnoreCase)
            ? CodexConnectionStageState.Passed
            : CodexConnectionStageState.Unavailable;
    }

    private void ProbeLocalCodexState(CodexConnectionStage stage)
    {
        if (!this.quotaCodexProcessRunning)
        {
            stage.State = CodexConnectionStageState.Warning;
            stage.ErrorCode = "STOPPED";
            return;
        }

        int recentStatusCode;
        if (TryReadRecentCodexHttpError(out recentStatusCode))
        {
            stage.ErrorCode = "HTTP" + recentStatusCode.ToString(CultureInfo.InvariantCulture);
            stage.State = recentStatusCode == 429
                ? CodexConnectionStageState.Warning
                : CodexConnectionStageState.Unavailable;
            return;
        }

        if (this.quotaSourceKnown)
        {
            stage.State = CodexConnectionStageState.Passed;
            stage.ErrorCode = string.Empty;
            return;
        }

        stage.State = CodexConnectionStageState.Blocked;
        stage.ErrorCode = "NO_DATA";
    }

    private bool TryReadRecentCodexHttpError(out int statusCode)
    {
        statusCode = 0;
        string path = GetNewestCodexSessionPath();
        if (string.IsNullOrEmpty(path) ||
            SafeGetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-5.0))
        {
            return false;
        }

        try
        {
            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            {
                int count = (int)Math.Min(CodexConnectionLogTailBytes, stream.Length);
                if (count <= 0)
                {
                    return false;
                }

                stream.Seek(-count, SeekOrigin.End);
                byte[] buffer = new byte[count];
                int read = stream.Read(buffer, 0, count);
                string text = Encoding.UTF8.GetString(buffer, 0, read);
                MatchCollection matches = Regex.Matches(
                    text,
                    "(?:HTTP(?:/\\d(?:\\.\\d)?)?\\s+|\\\"status(?:_code)?\\\"\\s*:\\s*)(401|403|429|451|5\\d\\d)",
                    RegexOptions.IgnoreCase);
                if (matches.Count > 0 &&
                    int.TryParse(matches[matches.Count - 1].Groups[1].Value, out statusCode))
                {
                    return true;
                }

                if (text.IndexOf("rate_limit_exceeded", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    statusCode = 429;
                    return true;
                }

                if (text.IndexOf("authentication_error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.IndexOf("invalid_api_key", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    statusCode = 401;
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private string GetNewestCodexSessionPath()
    {
        lock (codexQuotaSnapshotCacheLock)
        {
            if (!string.IsNullOrEmpty(codexQuotaSnapshotCachePath) &&
                File.Exists(codexQuotaSnapshotCachePath))
            {
                return codexQuotaSnapshotCachePath;
            }
        }

        if (string.IsNullOrEmpty(this.quotaSessionsPath) ||
            !Directory.Exists(this.quotaSessionsPath))
        {
            return string.Empty;
        }

        string newest = string.Empty;
        DateTime newestWriteUtc = DateTime.MinValue;
        try
        {
            foreach (string file in Directory.EnumerateFiles(
                this.quotaSessionsPath,
                "rollout-*.jsonl",
                SearchOption.AllDirectories))
            {
                DateTime writeUtc = SafeGetLastWriteTimeUtc(file);
                if (writeUtc > newestWriteUtc)
                {
                    newest = file;
                    newestWriteUtc = writeUtc;
                }
            }
        }
        catch
        {
            return string.Empty;
        }

        return newest;
    }

    private static bool TryProbeHttpEndpoint(string url, out int statusCode, out string errorCode)
    {
        statusCode = 0;
        errorCode = string.Empty;
        try
        {
            HttpWebRequest request = CreateCodexConnectionRequest(url);
            request.AllowAutoRedirect = false;
            using (WebResponse response = request.GetResponse())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                statusCode = httpResponse != null ? (int)httpResponse.StatusCode : 200;
                return true;
            }
        }
        catch (WebException ex)
        {
            if (TryGetWebExceptionStatusCode(ex, out statusCode))
            {
                return true;
            }

            errorCode = GetWebExceptionCode(ex);
            return false;
        }
        catch
        {
            errorCode = "ERROR";
            return false;
        }
    }

    private static HttpWebRequest CreateCodexConnectionRequest(string url)
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = CodexConnectionProbeTimeoutMs;
        request.ReadWriteTimeout = CodexConnectionProbeTimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";
        return request;
    }

    private static bool TryGetWebExceptionStatusCode(WebException ex, out int statusCode)
    {
        statusCode = 0;
        HttpWebResponse response = ex != null ? ex.Response as HttpWebResponse : null;
        if (response == null)
        {
            return false;
        }

        statusCode = (int)response.StatusCode;
        response.Dispose();
        return true;
    }

    private static string GetWebExceptionCode(WebException ex)
    {
        if (ex == null)
        {
            return "ERROR";
        }

        if (ex.Status == WebExceptionStatus.NameResolutionFailure)
        {
            return "DNS";
        }

        if (ex.Status == WebExceptionStatus.Timeout)
        {
            return "TIMEOUT";
        }

        if (ex.Status == WebExceptionStatus.TrustFailure ||
            ex.Status == WebExceptionStatus.SecureChannelFailure)
        {
            return "TLS";
        }

        if (ex.Status == WebExceptionStatus.ConnectFailure ||
            ex.Status == WebExceptionStatus.ConnectionClosed ||
            ex.Status == WebExceptionStatus.KeepAliveFailure ||
            ex.Status == WebExceptionStatus.ReceiveFailure ||
            ex.Status == WebExceptionStatus.SendFailure)
        {
            return "TCP";
        }

        return ex.Status.ToString().ToUpperInvariant();
    }

    private static void MarkRemainingCodexConnectionStages(
        CodexConnectionSnapshot snapshot,
        int startIndex,
        CodexConnectionStageState state)
    {
        if (snapshot == null || snapshot.Stages == null)
        {
            return;
        }

        for (int i = Math.Max(0, startIndex); i < snapshot.Stages.Length; i++)
        {
            snapshot.Stages[i].State = state;
            snapshot.Stages[i].ErrorCode = string.Empty;
        }
    }

    private TimeSpan GetCodexConnectionRefreshInterval(CodexConnectionSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Offline || HasCodexConnectionProblem(snapshot))
        {
            return TimeSpan.FromMinutes(1.0);
        }

        WidgetPerformanceMode mode =
            WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return TimeSpan.FromMinutes(3.0);
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return TimeSpan.FromMinutes(10.0);
        }

        return TimeSpan.FromMinutes(5.0);
    }

    private static bool HasCodexConnectionProblem(CodexConnectionSnapshot snapshot)
    {
        if (snapshot == null || snapshot.Stages == null)
        {
            return true;
        }

        for (int i = 0; i < snapshot.Stages.Length; i++)
        {
            CodexConnectionStageState state = snapshot.Stages[i].State;
            if (state != CodexConnectionStageState.Passed)
            {
                return true;
            }
        }

        return false;
    }

    private TimeSpan GetClaudeRefreshInterval()
    {
        return TimeSpan.FromMinutes(15.0);
    }

    private static DateTime GetNextCodexRadarScheduledRefreshUtc(
        DateTime nowUtc,
        CodexRadarSnapshot snapshot,
        ServiceHealthState health)
    {
        if (health != ServiceHealthState.Normal)
        {
            return nowUtc.AddMinutes(10.0);
        }

        return TimeZoneUtilities.GetNextBeijingHourUtc(nowUtc);
    }

    private TimeSpan GetCodexWebRetryDelay()
    {
        return TimeSpan.FromMinutes(2.0);
    }

    private void RefreshClaudeStatusIfNeeded()
    {
        if (!IsServiceNetworkAvailable())
        {
            SetClaudeServiceHealth(ServiceHealthState.Offline);
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool shouldStart = false;
        bool forceRefresh = ShouldForceServiceHealthRefresh(this.claudeServiceHealth);
        lock (this.claudeStatusLock)
        {
            bool scheduledRefreshDue = this.nextClaudeStatusRefreshUtc == DateTime.MinValue ||
                nowUtc >= this.nextClaudeStatusRefreshUtc;
            if (!this.claudeStatusRequestRunning &&
                (scheduledRefreshDue || forceRefresh))
            {
                this.claudeStatusRequestRunning = true;
                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        Task.Run((Action)delegate
        {
            ServiceHealthState health = ServiceHealthState.Unknown;
            try
            {
                health = TryReadClaudeStatus();
            }
            catch (Exception ex)
            {
                health = ServiceHealthState.Unreachable;
                Program.LogException(ex);
            }

            lock (this.claudeStatusLock)
            {
                this.nextClaudeStatusRefreshUtc = DateTime.UtcNow +
                    (health == ServiceHealthState.Normal ? GetClaudeRefreshInterval() : GetCodexWebRetryDelay());
                this.claudeStatusRequestRunning = false;
            }

            SetClaudeServiceHealth(health);

            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            RenderLayeredWindow();
                        }
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private void RefreshCodexRadarStatusIfNeeded()
    {
        if (this.currentSettings.CodexRadarTestMode != CodexRadarTestMode.Off)
        {
            return;
        }

        if (!IsServiceNetworkAvailable())
        {
            SetRadarServiceHealth(ServiceHealthState.Offline);
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        bool shouldStart = false;
        bool forceRefresh = ShouldForceServiceHealthRefresh(this.radarServiceHealth);
        lock (this.codexRadarStatusLock)
        {
            bool scheduledRefreshDue = this.nextCodexRadarStatusRefreshUtc == DateTime.MinValue ||
                nowUtc >= this.nextCodexRadarStatusRefreshUtc;
            if (!this.codexRadarStatusRequestRunning &&
                (scheduledRefreshDue || forceRefresh))
            {
                this.codexRadarStatusRequestRunning = true;
                shouldStart = true;
            }
        }

        if (!shouldStart)
        {
            return;
        }

        Task.Run((Action)delegate
        {
            string requestedModelKey = this.currentSettings.CodexRadarModelKey;
            CodexRadarSnapshot snapshot;
            bool known = false;
            ServiceHealthState health = ServiceHealthState.Unknown;
            CodexRadarModelCatalogUpdate catalogUpdate = null;
            try
            {
                known = TryReadCodexRadarStatus(requestedModelKey, out snapshot, out health, out catalogUpdate);
            }
            catch (Exception ex)
            {
                snapshot = null;
                health = ServiceHealthState.Unreachable;
                Program.LogException(ex);
            }

            CodexRadarSnapshot snapshotToCache = null;
            lock (this.codexRadarStatusLock)
            {
                bool modelStillSelected = string.Equals(
                    requestedModelKey,
                    this.currentSettings.CodexRadarModelKey,
                    StringComparison.OrdinalIgnoreCase);
                if (!modelStillSelected)
                {
                    this.nextCodexRadarStatusRefreshUtc = DateTime.UtcNow.AddSeconds(1.0);
                }
                else if (known && snapshot != null)
                {
                    MergeCodexModelIqHistory(snapshot, this.codexRadarSnapshot);
                    ApplyCodexModelIqEfficiencyFromHistory(snapshot);
                    PreserveCodexModelIqSnapshot(snapshot, this.codexRadarSnapshot);
                    this.codexRadarSnapshot = snapshot;
                    snapshotToCache = snapshot.Clone();
                }
                else if (this.codexRadarSnapshot != null)
                {
                    this.codexRadarSnapshot.ModelIqRefreshSucceeded = false;
                }

                if (modelStillSelected)
                {
                    this.nextCodexRadarStatusRefreshUtc = GetNextCodexRadarScheduledRefreshUtc(
                        DateTime.UtcNow,
                        snapshot,
                        health);
                }

                this.codexRadarStatusRequestRunning = false;
            }

            if (snapshotToCache != null)
            {
                SaveCodexRadarCache(requestedModelKey, snapshotToCache);
                HandleCodexRadarWindowAndResetEvents(snapshotToCache);
            }

            ShowCodexRadarModelCatalogNotifications(catalogUpdate);
            SetRadarServiceHealth(health);

            try
            {
                if (!this.IsDisposed && this.IsHandleCreated)
                {
                    this.BeginInvoke((MethodInvoker)delegate
                    {
                        if (!this.IsDisposed)
                        {
                            RenderLayeredWindow();
                        }
                    });
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private static void PreserveCodexModelIqSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || target.ModelIqKnown || !source.ModelIqKnown)
        {
            return;
        }

        // current.json may temporarily omit model_iq; preserve the last known IQ fields then.
        bool refreshSucceeded = target.ModelIqRefreshSucceeded;
        CopyCodexModelIqSnapshot(target, source);
        target.ModelIqRefreshSucceeded = refreshSucceeded;
    }

    private static void CopyCodexModelIqSnapshot(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null || source == null || !source.ModelIqKnown)
        {
            return;
        }

        target.ModelIqStatus = source.ModelIqStatus;
        target.ModelIqPassRatePercent = source.ModelIqPassRatePercent;
        target.ModelIqPassed = source.ModelIqPassed;
        target.ModelIqValidTasks = source.ModelIqValidTasks;
        target.ModelIqTokenEfficiencyPercent = source.ModelIqTokenEfficiencyPercent;
        target.ModelIqTimeEfficiencyPercent = source.ModelIqTimeEfficiencyPercent;
        target.ModelIqEfficiencyPassed = source.ModelIqEfficiencyPassed;
        target.ModelIqEfficiencyTotalTokens = source.ModelIqEfficiencyTotalTokens;
        target.ModelIqEfficiencySerialSeconds = source.ModelIqEfficiencySerialSeconds;
        target.ModelIqPassedKnown = source.ModelIqPassedKnown;
        target.ModelIqEfficiencyInputKnown = source.ModelIqEfficiencyInputKnown;
        target.ModelIqEfficiencyKnown = source.ModelIqEfficiencyKnown;
        target.ModelIqRefreshedAtLocal = source.ModelIqRefreshedAtLocal;
        target.ModelIqDataDateLocal = source.ModelIqDataDateLocal;
        target.ModelIqDataWindowStartHourLocal = source.ModelIqDataWindowStartHourLocal;
        target.ModelIqRefreshedAtKnown = source.ModelIqRefreshedAtKnown;
        target.ModelIqDataDateKnown = source.ModelIqDataDateKnown;
        target.ModelIqDataWindowKnown = source.ModelIqDataWindowKnown;
        target.ModelIqRefreshSucceeded = source.ModelIqRefreshSucceeded;
        target.ModelIqKnown = source.ModelIqKnown;
        target.ModelIqHistory = CloneCodexModelHistory(source.ModelIqHistory);
    }

    private static ServiceHealthState TryReadClaudeStatus()
    {
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        string url = ClaudeStatusUrl + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = ClaudeStatusTimeoutMs;
        request.ReadWriteTimeout = ClaudeStatusTimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        try
        {
            using (WebResponse response = request.GetResponse())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    return ServiceHealthState.Unavailable;
                }

                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        return ServiceHealthState.Unavailable;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        JavaScriptSerializer serializer = new JavaScriptSerializer();
                        Dictionary<string, object> root =
                            serializer.DeserializeObject(content) as Dictionary<string, object>;
                        Dictionary<string, object> status = GetQuotaObject(root, "status");
                        string indicator = GetQuotaString(status, "indicator").Trim();
                        if (string.Equals(indicator, "none", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Normal;
                        }

                        if (string.Equals(indicator, "minor", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Degraded;
                        }

                        if (string.Equals(indicator, "major", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(indicator, "critical", StringComparison.OrdinalIgnoreCase))
                        {
                            return ServiceHealthState.Unavailable;
                        }

                        return ServiceHealthState.Unavailable;
                    }
                }
            }
        }
        catch (WebException ex)
        {
            return ClassifyWebException(ex);
        }
        catch
        {
            return ServiceHealthState.Unreachable;
        }
    }

    private static bool TryReadCodexRadarStatus(
        string modelKey,
        out CodexRadarSnapshot snapshot,
        out ServiceHealthState health,
        out CodexRadarModelCatalogUpdate catalogUpdate)
    {
        snapshot = null;
        health = ServiceHealthState.Unreachable;
        catalogUpdate = null;
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        string url = CodexRadarStatusUrl + "?t=" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = ProductIdentity.UserAgent;
        request.Timeout = CodexRadarStatusTimeoutMs;
        request.ReadWriteTimeout = CodexRadarStatusTimeoutMs;
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        try
        {
            using (WebResponse response = request.GetResponse())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    health = ServiceHealthState.Unavailable;
                    return false;
                }

                using (Stream stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        health = ServiceHealthState.Unavailable;
                        return false;
                    }

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        bool parsed =
                            TryParseCodexRadarStatus(content, modelKey, out snapshot, out catalogUpdate) ||
                            TryParseCodexRadarHtmlStatus(content, modelKey, out snapshot);
                        health = parsed ? GetCodexRadarSnapshotHealth(snapshot) : ServiceHealthState.Unavailable;
                        return parsed;
                    }
                }
            }
        }
        catch (WebException ex)
        {
            health = ClassifyWebException(ex);
            return false;
        }
        catch
        {
            health = ServiceHealthState.Unreachable;
            return false;
        }
    }

    private static ServiceHealthState ClassifyWebException(WebException ex)
    {
        if (ex != null &&
            ex.Status == WebExceptionStatus.ProtocolError &&
            ex.Response != null)
        {
            return ServiceHealthState.Unavailable;
        }

        return ServiceHealthState.Unreachable;
    }

    private static ServiceHealthState GetCodexRadarSnapshotHealth(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return ServiceHealthState.Unavailable;
        }

        return snapshot.ModelIqKnown
                ? ServiceHealthState.Normal
                : ServiceHealthState.Incomplete;
    }

    private static void ApplyCodexRadarWindowStatus(
        Dictionary<string, object> root,
        CodexRadarSnapshot snapshot)
    {
        if (root == null || snapshot == null)
        {
            return;
        }

        Dictionary<string, object> window = GetQuotaObject(root, "window");
        bool open;
        bool openKnown = TryGetJsonBool(root, "window_open", out open) ||
            TryGetJsonBool(window, "open", out open);
        string status = GetQuotaString(window, "status");
        if (string.IsNullOrWhiteSpace(status))
        {
            status = GetQuotaString(root, "status");
        }

        if (!openKnown && !string.IsNullOrWhiteSpace(status))
        {
            open = string.Equals(status, "open", StringComparison.OrdinalIgnoreCase);
            openKnown = true;
        }

        snapshot.SpeedWindowKnown = openKnown || !string.IsNullOrWhiteSpace(status);
        snapshot.SpeedWindowOpen = openKnown && open;
        snapshot.SpeedWindowStatus = status ?? string.Empty;
        snapshot.SpeedWindowEventId = BuildCodexRadarSpeedWindowEventId(root, window, snapshot);

        DateTime openedAt;
        if (TryGetQuotaDate(window, "opened_at", out openedAt))
        {
            snapshot.SpeedWindowOpenedAtLocal = openedAt;
            snapshot.SpeedWindowOpenedAtKnown = true;
        }

        DateTime closedAt;
        if (TryGetQuotaDate(window, "closed_at", out closedAt))
        {
            snapshot.SpeedWindowClosedAtLocal = closedAt;
            snapshot.SpeedWindowClosedAtKnown = true;
        }
    }

    private static string BuildCodexRadarSpeedWindowEventId(
        Dictionary<string, object> root,
        Dictionary<string, object> window,
        CodexRadarSnapshot snapshot)
    {
        string id = GetQuotaString(window, "id");
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id.Trim();
        }

        string sourceUrl = GetQuotaString(window, "source_url");
        if (!string.IsNullOrWhiteSpace(sourceUrl))
        {
            return sourceUrl.Trim();
        }

        string status = snapshot == null ? string.Empty : snapshot.SpeedWindowStatus;
        string monitored = GetQuotaString(root, "monitored_at");
        if (!string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(monitored))
        {
            return (status ?? string.Empty).Trim() + ":" + (monitored ?? string.Empty).Trim();
        }

        return string.Empty;
    }

    private static void ApplyCodexRadarFeedResetStatus(
        Dictionary<string, object> root,
        CodexRadarSnapshot snapshot)
    {
        if (root == null || snapshot == null)
        {
            return;
        }

        Dictionary<string, object> links = GetQuotaObject(root, "links");
        string rssUrl = GetQuotaString(links, "rss");
        CodexRadarResetEvent resetEvent;
        if (!TryReadCodexRadarFeedReset(rssUrl, out resetEvent))
        {
            return;
        }

        snapshot.ResetEventKnown = true;
        snapshot.ResetEventId = resetEvent.Id ?? string.Empty;
        snapshot.ResetEventTitle = resetEvent.Title ?? string.Empty;
        snapshot.ResetEventUtc = resetEvent.EventUtcKnown
            ? resetEvent.EventUtc
            : DateTime.MinValue;
    }

    private static bool TryReadCodexRadarFeedReset(
        string rssUrl,
        out CodexRadarResetEvent resetEvent)
    {
        resetEvent = null;
        string url = NormalizeCodexRadarFeedUrl(rssUrl);
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch
        {
        }

        try
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/rss+xml,application/xml,text/xml,*/*";
            request.UserAgent = ProductIdentity.UserAgent;
            request.Timeout = CodexRadarStatusTimeoutMs;
            request.ReadWriteTimeout = CodexRadarStatusTimeoutMs;
            request.Headers["Cache-Control"] = "no-store, no-cache";
            request.Headers["Pragma"] = "no-cache";
            using (WebResponse response = request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            {
                HttpWebResponse httpResponse = response as HttpWebResponse;
                if (httpResponse != null &&
                    ((int)httpResponse.StatusCode < 200 || (int)httpResponse.StatusCode >= 300))
                {
                    return false;
                }

                if (stream == null)
                {
                    return false;
                }

                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return TryParseCodexRadarFeedReset(reader.ReadToEnd(), out resetEvent);
                }
            }
        }
        catch
        {
            resetEvent = null;
            return false;
        }
    }

    private static string NormalizeCodexRadarFeedUrl(string rssUrl)
    {
        string value = (rssUrl ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return "https://codexradar.com/feed.xml";
        }

        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
        {
            Uri baseUri = new Uri("https://codexradar.com/");
            if (!Uri.TryCreate(baseUri, value, out uri))
            {
                return string.Empty;
            }
        }

        if (uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "codexradar.com", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return uri.ToString();
    }

    private static bool TryParseCodexRadarFeedReset(
        string content,
        out CodexRadarResetEvent resetEvent)
    {
        resetEvent = null;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        MatchCollection items = Regex.Matches(
            content,
            "<item\\b[^>]*>(.*?)</item>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        for (int i = 0; i < items.Count; i++)
        {
            string item = items[i].Groups[1].Value;
            string title = ExtractXmlTagText(item, "title");
            string description = ExtractXmlTagText(item, "description");
            if (!IsCodexRadarResetFeedItem(title, description))
            {
                continue;
            }

            DateTime eventUtc = DateTime.MinValue;
            bool eventUtcKnown = false;
            string pubDate = ExtractXmlTagText(item, "pubDate");
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(
                pubDate,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
            {
                eventUtc = parsed.UtcDateTime;
                eventUtcKnown = true;
            }

            string guid = ExtractXmlTagText(item, "guid");
            if (string.IsNullOrWhiteSpace(guid))
            {
                guid = ExtractXmlTagText(item, "link");
            }

            resetEvent = new CodexRadarResetEvent
            {
                Id = (guid ?? string.Empty).Trim(),
                Title = (title ?? string.Empty).Trim(),
                EventUtc = eventUtc,
                EventUtcKnown = eventUtcKnown
            };
            return true;
        }

        return false;
    }

    private static bool IsCodexRadarResetFeedItem(string title, string description)
    {
        string combined = ((title ?? string.Empty) + "\n" + (description ?? string.Empty)).Trim();
        return combined.IndexOf("已重置", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("用量限制重置", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("恢复到 100", StringComparison.OrdinalIgnoreCase) >= 0 ||
            combined.IndexOf("恢复至 100", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ExtractXmlTagText(string xml, string tagName)
    {
        if (string.IsNullOrEmpty(xml) || string.IsNullOrEmpty(tagName))
        {
            return string.Empty;
        }

        Match match = Regex.Match(
            xml,
            "<" + Regex.Escape(tagName) + "\\b[^>]*>(.*?)</" + Regex.Escape(tagName) + ">",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            return string.Empty;
        }

        string value = match.Groups[1].Value;
        value = Regex.Replace(value, "^\\s*<!\\[CDATA\\[(.*)\\]\\]>\\s*$", "$1", RegexOptions.Singleline);
        value = Regex.Replace(value, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(value).Trim();
    }

    private static bool TryParseCodexRadarStatus(
        string content,
        string modelKey,
        out CodexRadarSnapshot snapshot,
        out CodexRadarModelCatalogUpdate catalogUpdate)
    {
        snapshot = null;
        catalogUpdate = null;
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = serializer.DeserializeObject(content) as Dictionary<string, object>;
            if (root == null)
            {
                return false;
            }

            Dictionary<string, object> rootModelIq = GetQuotaObject(root, "model_iq");
            catalogUpdate = CodexRadarModelCatalog.MergeAndSave(
                ExtractCodexRadarModelCatalog(rootModelIq));
            snapshot = CodexRadarSnapshot.CreateDefault();
            DateTime checkedAt;
            if (TryGetQuotaDate(root, "checked_at", out checkedAt) ||
                TryGetQuotaDate(root, "monitored_at", out checkedAt))
            {
                snapshot.CheckedAtLocal = checkedAt;
                snapshot.CheckedAtKnown = true;
            }

            ApplyCodexRadarWindowStatus(root, snapshot);
            ApplyCodexRadarFeedResetStatus(root, snapshot);

            Dictionary<string, object> modelIq = SelectCodexModelIqRoot(
                rootModelIq,
                modelKey);
            snapshot.ModelIqRefreshedAtLocal = DateTime.Now;
            snapshot.ModelIqRefreshedAtKnown = true;
            if (TryApplyCodexModelIqStatus(modelIq, snapshot))
            {
                snapshot.ModelIqRefreshSucceeded = true;
            }

            return true;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private static bool TryParseCodexRadarHtmlStatus(
        string content,
        string modelKey,
        out CodexRadarSnapshot snapshot)
    {
        snapshot = null;
        if (string.IsNullOrEmpty(content) ||
            content.IndexOf("codex-radar:summary:start", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        try
        {
            string passedText = GetCodexRadarHtmlCompareValue(content, "通过数", modelKey);
            string scoreText = GetCodexRadarHtmlCompareValue(content, "IQ", modelKey);
            string durationText = GetCodexRadarHtmlCompareValue(content, "耗时", modelKey);
            string tokensText = GetCodexRadarHtmlCompareValue(content, "总tokens", modelKey);
            Match passedMatch = Regex.Match(passedText, "(\\d+)\\s*/\\s*(\\d+)");
            double score;
            double durationMinutes;
            double totalTokens;
            if (!passedMatch.Success ||
                !double.TryParse(scoreText, NumberStyles.Float, CultureInfo.InvariantCulture, out score) ||
                !TryParseCodexRadarHtmlNumber(durationText, out durationMinutes) ||
                !TryParseCodexRadarHtmlNumber(tokensText, out totalTokens))
            {
                return false;
            }

            int passed;
            int validTasks;
            if (!int.TryParse(passedMatch.Groups[1].Value, out passed) ||
                !int.TryParse(passedMatch.Groups[2].Value, out validTasks) ||
                validTasks <= 0)
            {
                return false;
            }

            snapshot = CodexRadarSnapshot.CreateDefault();
            snapshot.CheckedAtLocal = DateTime.Now;
            snapshot.CheckedAtKnown = true;
            snapshot.ModelIqRefreshedAtLocal = DateTime.Now;
            snapshot.ModelIqRefreshedAtKnown = true;
            snapshot.ModelIqRefreshSucceeded = true;
            snapshot.ModelIqKnown = true;
            snapshot.ModelIqPassedKnown = true;
            snapshot.ModelIqPassed = Math.Max(0, Math.Min(validTasks, passed));
            snapshot.ModelIqValidTasks = Math.Max(1, Math.Min(CodexModelIqNominalTasks, validTasks));
            snapshot.ModelIqPassRatePercent = NormalizePassRatePercent(score);
            snapshot.ModelIqEfficiencyPassed = snapshot.ModelIqPassed;
            snapshot.ModelIqEfficiencyTotalTokens = Math.Max(0.0, totalTokens);
            snapshot.ModelIqEfficiencySerialSeconds = Math.Max(0.0, durationMinutes * 60.0);
            snapshot.ModelIqEfficiencyInputKnown =
                snapshot.ModelIqEfficiencyPassed > 0.0 &&
                snapshot.ModelIqEfficiencyTotalTokens > 0.0 &&
                snapshot.ModelIqEfficiencySerialSeconds > 0.0;

            Match statusMatch = Regex.Match(
                content,
                "<section\\s+class=\"[^\"]*model-iq-([a-z]+)[^\"]*\"",
                RegexOptions.IgnoreCase);
            snapshot.ModelIqStatus = statusMatch.Success
                ? NormalizeCodexModelIqStatus(statusMatch.Groups[1].Value)
                : (snapshot.ModelIqPassed >= 8 ? "green" : "red");

            Match timeMatch = Regex.Match(
                content,
                "<time\\s+datetime=\"([^\"]+)\"",
                RegexOptions.IgnoreCase);
            DateTimeOffset updatedAt;
            if (timeMatch.Success &&
                DateTimeOffset.TryParse(
                    WebUtility.HtmlDecode(timeMatch.Groups[1].Value),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out updatedAt))
            {
                DateTime updatedBeijing = TimeZoneInfo.ConvertTime(
                    updatedAt,
                    TimeZoneUtilities.GetBeijingTimeZone()).DateTime;
                snapshot.ModelIqDataDateLocal = updatedBeijing.Date;
                snapshot.ModelIqDataWindowStartHourLocal = updatedBeijing.Hour >= 12 ? 12 : 0;
                snapshot.ModelIqDataDateKnown = true;
                snapshot.ModelIqDataWindowKnown = true;
            }

            snapshot.ModelIqHistory = ParseCodexRadarHtmlHistory(
                content,
                modelKey,
                snapshot.ModelIqDataDateKnown ? snapshot.ModelIqDataDateLocal : DateTime.Today,
                totalTokens);
            if (snapshot.ModelIqDataDateKnown)
            {
                CodexModelHistoryPoint latestPoint = new CodexModelHistoryPoint
                {
                    DateLocal = snapshot.ModelIqDataDateLocal.Date.AddHours(
                        snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0),
                    Score = snapshot.ModelIqPassRatePercent,
                    Passed = snapshot.ModelIqPassed,
                    Tasks = snapshot.ModelIqValidTasks,
                    TotalTokens = snapshot.ModelIqEfficiencyTotalTokens,
                    SerialSeconds = snapshot.ModelIqEfficiencySerialSeconds,
                    ValidityKnown = true
                };
                UpsertCodexModelHistoryPoint(snapshot.ModelIqHistory, latestPoint);
                snapshot.ModelIqHistory = NormalizeCodexModelHistory(snapshot.ModelIqHistory);
            }

            return true;
        }
        catch
        {
            snapshot = null;
            return false;
        }
    }

    private static string GetCodexRadarHtmlCompareValue(
        string content,
        string rowLabel,
        string modelKey)
    {
        Match rowMatch = Regex.Match(
            content,
            "<div\\s+class=\"model-iq-compare-row\"[^>]*>\\s*<span>\\s*" +
                Regex.Escape(rowLabel) +
                "\\s*</span>(.*?)</div>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!rowMatch.Success)
        {
            return string.Empty;
        }

        MatchCollection values = Regex.Matches(
            rowMatch.Groups[1].Value,
            "<strong\\s+class=\"([^\"]*)\"[^>]*>(.*?)</strong>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        string expectedClass = "model-iq-column-" +
            CodexRadarModelCatalog.NormalizeModelKey(modelKey);

        for (int i = 0; i < values.Count; i++)
        {
            if (values[i].Groups[1].Value.IndexOf(expectedClass, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NormalizeCodexRadarHtmlText(values[i].Groups[2].Value);
            }
        }

        return string.Empty;
    }

    private static string NormalizeCodexRadarHtmlText(string value)
    {
        string withoutTags = Regex.Replace(value ?? string.Empty, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Trim();
    }

    private static bool TryParseCodexRadarHtmlNumber(string value, out double number)
    {
        string normalized = Regex.Replace(value ?? string.Empty, "[^0-9.\\-]", string.Empty);
        return double.TryParse(
            normalized,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static List<CodexModelHistoryPoint> ParseCodexRadarHtmlHistory(
        string content,
        string modelKey,
        DateTime referenceDate,
        double latestTotalTokens)
    {
        List<CodexModelHistoryPoint> history = new List<CodexModelHistoryPoint>();
        MatchCollection matches = Regex.Matches(
            WebUtility.HtmlDecode(content),
            "(\\d{1,2})月(\\d{1,2})日\\s+GPT-5\\.(\\d+)\\s+([a-z0-9_-]+):\\s*" +
                "IQ指数\\s*([0-9.]+),\\s*(\\d+)\\s*/\\s*(\\d+),\\s*" +
                "费用\\s*\\$[0-9.]+,\\s*耗时\\s*(\\d+)分钟,\\s*" +
                "cache命中率\\s*([0-9.]+)%",
            RegexOptions.IgnoreCase);
        string expectedKey = CodexRadarModelCatalog.NormalizeModelKey(modelKey);
        for (int i = 0; i < matches.Count; i++)
        {
            Match match = matches[i];
            string candidateKey = CodexRadarModelCatalog.BuildModelKey(
                "gpt-5." + match.Groups[3].Value,
                match.Groups[4].Value,
                string.Empty);
            if (!string.Equals(candidateKey, expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int month;
            int day;
            int passed;
            int tasks;
            double score;
            double minutes;
            double cacheRate;
            if (!int.TryParse(match.Groups[1].Value, out month) ||
                !int.TryParse(match.Groups[2].Value, out day) ||
                !double.TryParse(match.Groups[5].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out score) ||
                !int.TryParse(match.Groups[6].Value, out passed) ||
                !int.TryParse(match.Groups[7].Value, out tasks) ||
                !double.TryParse(match.Groups[8].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out minutes) ||
                !double.TryParse(match.Groups[9].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out cacheRate))
            {
                continue;
            }

            DateTime date;
            if (!TryResolveCodexRadarHistoryDate(referenceDate, month, day, out date))
            {
                continue;
            }

            CodexModelHistoryPoint point = new CodexModelHistoryPoint
            {
                DateLocal = date,
                Score = score,
                Passed = passed,
                Tasks = tasks,
                SerialSeconds = minutes * 60.0,
                InputTokens = 100.0,
                CachedInputTokens = cacheRate,
                CacheRateKnown = true,
                ValidityKnown = tasks > 0
            };
            if (date.Date == referenceDate.Date && latestTotalTokens > 0.0)
            {
                point.TotalTokens = latestTotalTokens;
            }

            UpsertCodexModelHistoryPoint(history, point);
        }

        return NormalizeCodexModelHistory(history);
    }

    private static bool TryResolveCodexRadarHistoryDate(
        DateTime referenceDate,
        int month,
        int day,
        out DateTime date)
    {
        date = DateTime.MinValue;
        int year = referenceDate.Year;
        try
        {
            DateTime candidate = new DateTime(year, month, day);
            if (candidate > referenceDate.AddMonths(6))
            {
                candidate = candidate.AddYears(-1);
            }
            else if (candidate < referenceDate.AddMonths(-6))
            {
                candidate = candidate.AddYears(1);
            }

            date = candidate.Date;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, object> SelectCodexModelIqRoot(
        Dictionary<string, object> modelIq,
        string modelKey)
    {
        if (modelIq == null)
        {
            return null;
        }

        string normalizedKey = CodexRadarModelCatalog.NormalizeModelKey(modelKey);
        string latestKey = GetCodexRadarModelKeyFromNode(
            GetQuotaObject(modelIq, "latest") ?? modelIq,
            CodexRadarModelCatalog.DefaultModelKey);
        if (normalizedKey.Length == 0 ||
            string.Equals(normalizedKey, latestKey, StringComparison.OrdinalIgnoreCase))
        {
            return modelIq;
        }

        Dictionary<string, object> comparisons = GetQuotaObject(modelIq, "comparisons");
        return GetQuotaObject(comparisons, normalizedKey);
    }

    private static List<CodexRadarModelInfo> ExtractCodexRadarModelCatalog(
        Dictionary<string, object> modelIq)
    {
        List<CodexRadarModelInfo> models = new List<CodexRadarModelInfo>();
        if (modelIq == null)
        {
            return models;
        }

        Dictionary<string, object> latest = GetQuotaObject(modelIq, "latest") ?? modelIq;
        string latestKey = GetCodexRadarModelKeyFromNode(latest, CodexRadarModelCatalog.DefaultModelKey);
        AddCodexRadarModelInfo(models, latestKey, GetCodexRadarModelLabel(modelIq, latest, latestKey));

        Dictionary<string, object> comparisons = GetQuotaObject(modelIq, "comparisons");
        if (comparisons != null)
        {
            foreach (KeyValuePair<string, object> pair in comparisons)
            {
                Dictionary<string, object> comparison = pair.Value as Dictionary<string, object>;
                if (comparison == null)
                {
                    continue;
                }

                Dictionary<string, object> comparisonLatest =
                    GetQuotaObject(comparison, "latest") ?? comparison;
                string key = GetCodexRadarModelKeyFromNode(
                    comparisonLatest,
                    CodexRadarModelCatalog.NormalizeModelKey(pair.Key));
                AddCodexRadarModelInfo(models, key, GetCodexRadarModelLabel(comparison, comparisonLatest, key));
            }
        }

        return models;
    }

    private static void AddCodexRadarModelInfo(
        List<CodexRadarModelInfo> models,
        string key,
        string label)
    {
        key = CodexRadarModelCatalog.NormalizeModelKey(key);
        if (key.Length == 0)
        {
            return;
        }

        for (int i = 0; i < models.Count; i++)
        {
            if (string.Equals(models[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        models.Add(new CodexRadarModelInfo
        {
            Key = key,
            Label = CodexRadarModelCatalog.GetDisplayLabel(label, key),
            Available = true,
            LastSeenUtc = DateTime.UtcNow
        });
    }

    private static string GetCodexRadarModelKeyFromNode(
        Dictionary<string, object> node,
        string fallback)
    {
        if (node == null)
        {
            return CodexRadarModelCatalog.NormalizeModelKey(fallback);
        }

        return CodexRadarModelCatalog.BuildModelKey(
            GetQuotaString(node, "model"),
            GetQuotaString(node, "reasoning_effort"),
            fallback);
    }

    private static string GetCodexRadarModelLabel(
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        string key)
    {
        string label = GetQuotaString(root, "label");
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label.Trim();
        }

        string model = GetQuotaString(latest, "model").Trim();
        string effort = GetQuotaString(latest, "reasoning_effort").Trim();
        if (model.Length > 0 || effort.Length > 0)
        {
            return (model.Length > 0 ? model.ToUpperInvariant() : string.Empty) +
                (effort.Length > 0 ? " " + effort : string.Empty);
        }

        return CodexRadarModelCatalog.GetDisplayLabel(string.Empty, key);
    }

    private static bool TryApplyCodexModelIqStatus(Dictionary<string, object> root, CodexRadarSnapshot snapshot)
    {
        if (root == null || snapshot == null)
        {
            return false;
        }

        try
        {
            Dictionary<string, object> latest = GetQuotaObject(root, "latest") ?? root;
            DateTime dataDate;
            int dataWindowHour;
            if (TryGetCodexModelIqDataWindow(latest, "date", out dataDate, out dataWindowHour) ||
                TryGetCodexModelIqDataWindow(root, "date", out dataDate, out dataWindowHour))
            {
                snapshot.ModelIqDataDateLocal = dataDate.Date;
                snapshot.ModelIqDataWindowStartHourLocal = dataWindowHour >= 12 ? 12 : 0;
                snapshot.ModelIqDataDateKnown = true;
                snapshot.ModelIqDataWindowKnown = true;
            }

            string status = GetQuotaString(latest, "status");
            if (string.IsNullOrEmpty(status))
            {
                status = GetQuotaString(root, "status");
            }

            double passRate;
            bool hasPassRate =
                TryGetQuotaNumber(latest, "score", out passRate) ||
                TryGetQuotaNumber(latest, "pass_rate", out passRate) ||
                TryGetQuotaNumber(latest, "passrate", out passRate) ||
                TryGetQuotaNumber(latest, "passRate", out passRate);
            double passed;
            double validTasks;
            bool hasPassed = TryGetQuotaNumber(latest, "passed", out passed);
            bool hasValidTasks =
                TryGetQuotaNumber(latest, "valid_tasks", out validTasks) ||
                TryGetQuotaNumber(latest, "validTasks", out validTasks) ||
                TryGetQuotaNumber(latest, "tasks", out validTasks);
            if (!hasPassRate)
            {
                if (hasPassed && hasValidTasks && validTasks > 0.0)
                {
                    passRate = passed / validTasks;
                    hasPassRate = true;
                }
            }

            if (string.IsNullOrEmpty(status) && !hasPassRate)
            {
                return false;
            }

            snapshot.ModelIqStatus = NormalizeCodexModelIqStatus(status);
            if (hasPassRate)
            {
                snapshot.ModelIqPassRatePercent = NormalizePassRatePercent(passRate);
            }

            if (hasPassed)
            {
                int validTaskCount = hasValidTasks && validTasks > 0.0
                    ? (int)Math.Round(validTasks, MidpointRounding.AwayFromZero)
                    : CodexModelIqNominalTasks;
                validTaskCount = Math.Max(1, Math.Min(CodexModelIqNominalTasks, validTaskCount));
                snapshot.ModelIqValidTasks = validTaskCount;
                snapshot.ModelIqPassed = Math.Max(0, Math.Min(validTaskCount, (int)Math.Round(passed, MidpointRounding.AwayFromZero)));
                snapshot.ModelIqPassedKnown = true;
            }
            else if (hasPassRate)
            {
                int validTaskCount = hasValidTasks && validTasks > 0.0
                    ? (int)Math.Round(validTasks, MidpointRounding.AwayFromZero)
                    : CodexModelIqNominalTasks;
                validTaskCount = Math.Max(1, Math.Min(CodexModelIqNominalTasks, validTaskCount));
                snapshot.ModelIqValidTasks = validTaskCount;
                snapshot.ModelIqPassed = Math.Max(0, Math.Min(validTaskCount, (int)Math.Round(NormalizePassRatePercent(passRate) / 100.0 * validTaskCount, MidpointRounding.AwayFromZero)));
                snapshot.ModelIqPassedKnown = true;
            }

            ApplyCodexModelIqEfficiency(root, latest, snapshot);
            ApplyCodexModelIqHistory(root, latest, snapshot);
            snapshot.ModelIqKnown = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyCodexModelIqHistory(
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        List<CodexModelHistoryPoint> history = new List<CodexModelHistoryPoint>();
        List<Dictionary<string, object>> entries = GetQuotaObjectsFromArray(root, "recent_days");
        if (entries.Count == 0)
        {
            entries = GetQuotaObjectsFromArray(root, "history");
        }

        CodexModelHistoryPoint baselinePoint = null;
        if (entries.Count > 0)
        {
            TryReadCodexModelHistoryPoint(entries[0], out baselinePoint);
        }

        for (int i = 0; i < entries.Count; i++)
        {
            CodexModelHistoryPoint point;
            if (TryReadCodexModelHistoryPoint(entries[i], out point))
            {
                ApplyCodexModelHistoryEfficiencies(point, baselinePoint);
                UpsertCodexModelHistoryPoint(history, point);
            }
        }

        CodexModelHistoryPoint latestPoint;
        if (TryReadCodexModelHistoryPoint(latest, out latestPoint))
        {
            ApplyCodexModelHistoryEfficiencies(latestPoint, baselinePoint);
            UpsertCodexModelHistoryPoint(history, latestPoint);
        }

        snapshot.ModelIqHistory = NormalizeCodexModelHistory(history);
    }

    private static bool TryReadCodexModelHistoryPoint(
        Dictionary<string, object> values,
        out CodexModelHistoryPoint point)
    {
        point = null;
        DateTime date;
        int dataWindowHour;
        double score;
        if (!TryGetCodexModelIqDataWindow(values, "date", out date, out dataWindowHour) ||
            !TryGetCodexModelIqScore(values, out score))
        {
            return false;
        }

        point = new CodexModelHistoryPoint
        {
            DateLocal = NormalizeCodexModelHistoryKey(date.Date.AddHours(dataWindowHour >= 12 ? 12 : 0)),
            Score = score
        };
        double passed;
        double totalTokens;
        double serialSeconds;
        double inputTokens;
        double cachedInputTokens;
        double tasks;
        double invalidTasks;
        TryGetQuotaNumber(values, "passed", out passed);
        TryGetModelIqTotalTokens(values, out totalTokens);
        if (!TryGetQuotaNumber(values, "serial_task_seconds", out serialSeconds) &&
            !TryGetQuotaNumber(values, "serialTaskSeconds", out serialSeconds))
        {
            TryGetQuotaNumber(values, "wall_seconds", out serialSeconds);
        }

        bool hasInput =
            TryGetQuotaNumber(values, "input_tokens", out inputTokens) ||
            TryGetQuotaNumber(values, "n_input_tokens", out inputTokens);
        bool hasCached =
            TryGetQuotaNumber(values, "cached_input_tokens", out cachedInputTokens) ||
            TryGetQuotaNumber(values, "cachedInputTokens", out cachedInputTokens);

        bool hasTasks =
            TryGetQuotaNumber(values, "tasks", out tasks) ||
            TryGetQuotaNumber(values, "valid_tasks", out tasks) ||
            TryGetQuotaNumber(values, "validTasks", out tasks);
        TryGetQuotaNumber(values, "invalid", out invalidTasks);
        point.Passed = passed;
        point.TotalTokens = totalTokens;
        point.SerialSeconds = serialSeconds;
        point.InputTokens = inputTokens;
        point.CachedInputTokens = cachedInputTokens;
        point.Tasks = tasks;
        point.InvalidTasks = invalidTasks;
        point.CacheRateKnown = hasInput && hasCached && inputTokens > 0.0;
        point.ValidityKnown = hasTasks && tasks > 0.0;
        return true;
    }

    private static void ApplyCodexModelHistoryEfficiencies(
        CodexModelHistoryPoint point,
        CodexModelHistoryPoint baseline)
    {
        if (point == null ||
            baseline == null ||
            point.Passed <= 0.0 ||
            baseline.Passed <= 0.0 ||
            point.TotalTokens <= 0.0 ||
            baseline.TotalTokens <= 0.0 ||
            point.SerialSeconds <= 0.0 ||
            baseline.SerialSeconds <= 0.0)
        {
            return;
        }

        double baselineTokenRate = baseline.Passed / baseline.TotalTokens;
        double baselineTimeRate = baseline.Passed / baseline.SerialSeconds;
        if (baselineTokenRate <= 0.0 || baselineTimeRate <= 0.0)
        {
            return;
        }

        point.TokenEfficiencyPercent = Math.Max(
            0.0,
            Math.Min(200.0, (point.Passed / point.TotalTokens) / baselineTokenRate * 100.0));
        point.TimeEfficiencyPercent = Math.Max(
            0.0,
            Math.Min(200.0, (point.Passed / point.SerialSeconds) / baselineTimeRate * 100.0));
        point.EfficiencyKnown = true;
    }

    private static bool TryGetCodexModelIqScore(Dictionary<string, object> values, out double score)
    {
        score = 0.0;
        if (values == null)
        {
            return false;
        }

        double rawScore;
        if (TryGetQuotaNumber(values, "score", out rawScore) ||
            TryGetQuotaNumber(values, "pass_rate", out rawScore) ||
            TryGetQuotaNumber(values, "passrate", out rawScore) ||
            TryGetQuotaNumber(values, "passRate", out rawScore))
        {
            score = NormalizePassRateValue(rawScore);
            return true;
        }

        double passed;
        double validTasks;
        if (TryGetQuotaNumber(values, "passed", out passed) &&
            (TryGetQuotaNumber(values, "valid_tasks", out validTasks) ||
             TryGetQuotaNumber(values, "validTasks", out validTasks) ||
             TryGetQuotaNumber(values, "tasks", out validTasks)) &&
            validTasks > 0.0)
        {
            score = NormalizePassRateValue(passed / validTasks);
            return true;
        }

        return false;
    }

    private static List<CodexModelHistoryPoint> GetRecentCodexModelHistory(CodexRadarSnapshot snapshot)
    {
        return snapshot == null
            ? new List<CodexModelHistoryPoint>()
            : NormalizeCodexModelHistory(snapshot.ModelIqHistory);
    }

    private static List<CodexModelHistoryPoint> NormalizeCodexModelHistory(
        IEnumerable<CodexModelHistoryPoint> source)
    {
        SortedDictionary<DateTime, CodexModelHistoryPoint> byDate =
            new SortedDictionary<DateTime, CodexModelHistoryPoint>();
        if (source != null)
        {
            foreach (CodexModelHistoryPoint point in source)
            {
                if (point == null || point.DateLocal == DateTime.MinValue)
                {
                    continue;
                }

                DateTime date = NormalizeCodexModelHistoryKey(point.DateLocal);
                CodexModelHistoryPoint normalized = point.Clone();
                normalized.DateLocal = date;
                normalized.Score = Math.Max(0.0, Math.Min(MaxCodexModelIqScore, point.Score));
                normalized.TokenEfficiencyPercent = Math.Max(
                    0.0,
                    Math.Min(200.0, point.TokenEfficiencyPercent));
                normalized.TimeEfficiencyPercent = Math.Max(
                    0.0,
                    Math.Min(200.0, point.TimeEfficiencyPercent));
                byDate[date] = normalized;
            }
        }

        List<CodexModelHistoryPoint> result = new List<CodexModelHistoryPoint>(byDate.Values);
        if (result.Count > CodexModelHistoryDays)
        {
            result.RemoveRange(0, result.Count - CodexModelHistoryDays);
        }

        return result;
    }

    private static DateTime NormalizeCodexModelHistoryKey(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return DateTime.MinValue;
        }

        return value.Date.AddHours(value.Hour >= 12 ? 12 : 0);
    }

    private static List<CodexModelHistoryPoint> CloneCodexModelHistory(
        IEnumerable<CodexModelHistoryPoint> source)
    {
        List<CodexModelHistoryPoint> result = new List<CodexModelHistoryPoint>();
        if (source == null)
        {
            return result;
        }

        foreach (CodexModelHistoryPoint point in source)
        {
            if (point != null)
            {
                result.Add(point.Clone());
            }
        }

        return result;
    }

    private static void UpsertCodexModelHistoryPoint(
        List<CodexModelHistoryPoint> history,
        DateTime date,
        double score)
    {
        if (history == null || date == DateTime.MinValue)
        {
            return;
        }

        DateTime day = NormalizeCodexModelHistoryKey(date);
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] != null && NormalizeCodexModelHistoryKey(history[i].DateLocal) == day)
            {
                history[i].Score = Math.Max(0.0, Math.Min(MaxCodexModelIqScore, score));
                return;
            }
        }

        history.Add(new CodexModelHistoryPoint
        {
            DateLocal = day,
            Score = Math.Max(0.0, Math.Min(MaxCodexModelIqScore, score))
        });
    }

    private static void UpsertCodexModelHistoryPoint(
        List<CodexModelHistoryPoint> history,
        CodexModelHistoryPoint point)
    {
        if (history == null || point == null || point.DateLocal == DateTime.MinValue)
        {
            return;
        }

        DateTime day = NormalizeCodexModelHistoryKey(point.DateLocal);
        CodexModelHistoryPoint normalized = point.Clone();
        normalized.DateLocal = day;
        normalized.Score = Math.Max(0.0, Math.Min(MaxCodexModelIqScore, point.Score));
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] != null && NormalizeCodexModelHistoryKey(history[i].DateLocal) == day)
            {
                CodexModelHistoryPoint existing = history[i];
                if (normalized.TotalTokens <= 0.0)
                {
                    normalized.TotalTokens = existing.TotalTokens;
                }

                if (normalized.SerialSeconds <= 0.0)
                {
                    normalized.SerialSeconds = existing.SerialSeconds;
                }

                if (!normalized.CacheRateKnown && existing.CacheRateKnown)
                {
                    normalized.InputTokens = existing.InputTokens;
                    normalized.CachedInputTokens = existing.CachedInputTokens;
                    normalized.CacheRateKnown = true;
                }

                if (!normalized.EfficiencyKnown && existing.EfficiencyKnown)
                {
                    normalized.TokenEfficiencyPercent = existing.TokenEfficiencyPercent;
                    normalized.TimeEfficiencyPercent = existing.TimeEfficiencyPercent;
                    normalized.EfficiencyKnown = true;
                }

                history[i] = normalized;
                return;
            }
        }

        history.Add(normalized);
    }

    private static void MergeCodexModelIqHistory(CodexRadarSnapshot target, CodexRadarSnapshot source)
    {
        if (target == null)
        {
            return;
        }

        List<CodexModelHistoryPoint> merged = CloneCodexModelHistory(
            source != null ? source.ModelIqHistory : null);
        if (target.ModelIqHistory != null)
        {
            for (int i = 0; i < target.ModelIqHistory.Count; i++)
            {
                CodexModelHistoryPoint point = target.ModelIqHistory[i];
                if (point != null)
                {
                    UpsertCodexModelHistoryPoint(merged, point);
                }
            }
        }

        target.ModelIqHistory = NormalizeCodexModelHistory(merged);
    }

    private static void ApplyCodexModelIqEfficiencyFromHistory(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null ||
            snapshot.ModelIqEfficiencyKnown ||
            !snapshot.ModelIqEfficiencyInputKnown ||
            snapshot.ModelIqHistory == null)
        {
            return;
        }

        CodexModelHistoryPoint baseline = null;
        for (int i = 0; i < snapshot.ModelIqHistory.Count; i++)
        {
            CodexModelHistoryPoint point = snapshot.ModelIqHistory[i];
            if (point != null &&
                point.Passed > 0.0 &&
                point.TotalTokens > 0.0 &&
                point.SerialSeconds > 0.0)
            {
                baseline = point;
                break;
            }
        }

        if (baseline == null)
        {
            return;
        }

        double baselineTokenRate = baseline.Passed / baseline.TotalTokens;
        double baselineTimeRate = baseline.Passed / baseline.SerialSeconds;
        if (baselineTokenRate <= 0.0 || baselineTimeRate <= 0.0)
        {
            return;
        }

        snapshot.ModelIqTokenEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round(
                (snapshot.ModelIqEfficiencyPassed / snapshot.ModelIqEfficiencyTotalTokens) /
                    baselineTokenRate * 100.0,
                MidpointRounding.AwayFromZero));
        snapshot.ModelIqTimeEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round(
                (snapshot.ModelIqEfficiencyPassed / snapshot.ModelIqEfficiencySerialSeconds) /
                    baselineTimeRate * 100.0,
                MidpointRounding.AwayFromZero));
        snapshot.ModelIqEfficiencyKnown = true;
    }

    private static void ApplyCodexModelIqEfficiency(
        Dictionary<string, object> root,
        Dictionary<string, object> latest,
        CodexRadarSnapshot snapshot)
    {
        double currentPassed;
        double currentTotalTokens;
        double currentSerialSeconds;
        if (!TryReadModelIqEfficiencyInput(latest, out currentPassed, out currentTotalTokens, out currentSerialSeconds))
        {
            return;
        }

        snapshot.ModelIqEfficiencyPassed = currentPassed;
        snapshot.ModelIqEfficiencyTotalTokens = currentTotalTokens;
        snapshot.ModelIqEfficiencySerialSeconds = currentSerialSeconds;
        snapshot.ModelIqEfficiencyInputKnown = true;

        Dictionary<string, object> baseline =
            GetFirstQuotaObjectFromArray(root, "history") ??
            GetFirstQuotaObjectFromArray(root, "recent_days");
        if (baseline == null)
        {
            return;
        }

        double baselinePassed;
        double baselineTotalTokens;
        double baselineSerialSeconds;
        if (!TryReadModelIqEfficiencyInput(baseline, out baselinePassed, out baselineTotalTokens, out baselineSerialSeconds))
        {
            return;
        }

        double baselineTokenRate = baselinePassed / baselineTotalTokens;
        double baselineTimeRate = baselinePassed / baselineSerialSeconds;
        if (baselineTokenRate <= 0.0 || baselineTimeRate <= 0.0)
        {
            return;
        }

        snapshot.ModelIqTokenEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentTotalTokens) / baselineTokenRate * 100.0, MidpointRounding.AwayFromZero));
        snapshot.ModelIqTimeEfficiencyPercent = ClampEfficiencyPercent(
            (int)Math.Round((currentPassed / currentSerialSeconds) / baselineTimeRate * 100.0, MidpointRounding.AwayFromZero));
        snapshot.ModelIqEfficiencyKnown = true;
    }

    private static bool TryReadModelIqEfficiencyInput(
        Dictionary<string, object> values,
        out double passed,
        out double totalTokens,
        out double serialSeconds)
    {
        passed = 0.0;
        totalTokens = 0.0;
        serialSeconds = 0.0;
        if (values == null || !TryGetQuotaNumber(values, "passed", out passed) || passed <= 0.0)
        {
            return false;
        }

        if (!TryGetModelIqTotalTokens(values, out totalTokens) || totalTokens <= 0.0)
        {
            return false;
        }

        return (TryGetQuotaNumber(values, "serial_task_seconds", out serialSeconds) ||
                TryGetQuotaNumber(values, "serialTaskSeconds", out serialSeconds) ||
                TryGetQuotaNumber(values, "wall_seconds", out serialSeconds)) &&
            serialSeconds > 0.0;
    }

    private static bool TryGetModelIqTotalTokens(Dictionary<string, object> values, out double totalTokens)
    {
        totalTokens = 0.0;
        if (TryGetQuotaNumber(values, "total_tokens", out totalTokens) ||
            TryGetQuotaNumber(values, "totalTokens", out totalTokens) ||
            TryGetQuotaNumber(values, "n_total_tokens", out totalTokens))
        {
            return totalTokens > 0.0;
        }

        double inputTokens;
        double outputTokens;
        if (!TryGetQuotaNumber(values, "n_input_tokens", out inputTokens) &&
            !TryGetQuotaNumber(values, "input_tokens", out inputTokens))
        {
            return false;
        }

        if (!TryGetQuotaNumber(values, "n_output_tokens", out outputTokens) &&
            !TryGetQuotaNumber(values, "output_tokens", out outputTokens))
        {
            outputTokens = 0.0;
        }

        totalTokens = inputTokens + Math.Max(0.0, outputTokens);
        return totalTokens > 0.0;
    }

    private static string NormalizeCodexModelIqStatus(string status)
    {
        if (string.IsNullOrEmpty(status))
        {
            return "invalid";
        }

        string normalized = status.Trim().ToLowerInvariant();
        if (normalized == "green" ||
            normalized == "ok" ||
            normalized == "normal" ||
            normalized == "stable")
        {
            return "green";
        }

        if (normalized == "yellow" ||
            normalized == "amber" ||
            normalized == "warning")
        {
            return "yellow";
        }

        if (normalized == "orange")
        {
            return "orange";
        }

        if (normalized == "red" ||
            normalized == "danger" ||
            normalized == "critical")
        {
            return "red";
        }

        return "invalid";
    }

    private static int NormalizePassRatePercent(double value)
    {
        return Math.Max(
            0,
            Math.Min(
                MaxCodexModelIqScore,
                (int)Math.Round(NormalizePassRateValue(value), MidpointRounding.AwayFromZero)));
    }

    private static double NormalizePassRateValue(double value)
    {
        if (value <= 1.0)
        {
            value *= 100.0;
        }

        return Math.Max(0.0, Math.Min(MaxCodexModelIqScore, value));
    }

    private double GetQuotaActiveRefreshSeconds()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 10.0;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 30.0;
        }

        return 15.0;
    }

    private double GetQuotaProcessCheckSeconds()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 3.0;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 10.0;
        }

        return 5.0;
    }

    private TimeSpan GetQuotaInactiveRefreshInterval()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return TimeSpan.FromMinutes(10.0);
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return TimeSpan.FromMinutes(60.0);
        }

        return TimeSpan.FromMinutes(20.0);
    }

    private bool UpdateCodexProcessRunningStatus(DateTime nowUtc, out bool changed)
    {
        if (this.lastQuotaProcessCheckUtc != DateTime.MinValue &&
            (nowUtc - this.lastQuotaProcessCheckUtc).TotalSeconds < GetQuotaProcessCheckSeconds())
        {
            changed = false;
            return this.quotaCodexProcessRunning;
        }

        bool running = IsCodexProcessRunning();
        changed = running != this.quotaCodexProcessRunning;
        this.quotaCodexProcessRunning = running;
        this.lastQuotaProcessCheckUtc = nowUtc;
        return running;
    }

    private static bool IsCodexProcessRunning()
    {
        Process[] processes = null;
        try
        {
            // Query only the required executable name instead of opening every process on the machine.
            processes = Process.GetProcessesByName("codex");
            return processes.Length > 0;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
        finally
        {
            if (processes != null)
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    if (processes[i] != null)
                    {
                        processes[i].Dispose();
                    }
                }
            }
        }

        return false;
    }

    private bool IsInactiveQuotaRefreshDue(DateTime nowUtc)
    {
        return this.nextQuotaInactiveRefreshUtc == DateTime.MinValue ||
            nowUtc >= this.nextQuotaInactiveRefreshUtc;
    }

    private void MarkInactiveQuotaRefresh(DateTime nowUtc)
    {
        this.nextQuotaInactiveRefreshUtc = nowUtc + GetQuotaInactiveRefreshInterval();
    }

    private static bool IsQuotaResetDue(CodexQuotaSnapshot snapshot, DateTime nowLocal)
    {
        if (snapshot == null)
        {
            return true;
        }

        return (snapshot.FiveHourResetKnown && snapshot.FiveHourResetLocal <= nowLocal) ||
            (snapshot.WeeklyResetKnown && snapshot.WeeklyResetLocal <= nowLocal);
    }

    private void ShowCodexNotification(string title, string message, ToolTipIcon icon)
    {
        if (this.notificationAction == null)
        {
            return;
        }

        try
        {
            this.notificationAction(title, message, icon);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void ShowCodexRadarModelCatalogNotifications(CodexRadarModelCatalogUpdate update)
    {
        if (update == null)
        {
            return;
        }

        for (int i = 0; i < update.Added.Count; i++)
        {
            CodexRadarModelInfo model = update.Added[i];
            ShowCodexNotification(
                "Codex Radar 新模型",
                CodexRadarModelCatalog.GetDisplayLabel(model == null ? string.Empty : model.Label, model == null ? string.Empty : model.Key) + " 已加入检测列表。",
                ToolTipIcon.Info);
        }

        for (int i = 0; i < update.Unavailable.Count; i++)
        {
            CodexRadarModelInfo model = update.Unavailable[i];
            ShowCodexNotification(
                "Codex Radar 模型暂不可用",
                CodexRadarModelCatalog.GetDisplayLabel(model == null ? string.Empty : model.Label, model == null ? string.Empty : model.Key) + " 本次没有出现在网站模型列表中，暂时保留但不可选。",
                ToolTipIcon.Warning);
        }

        for (int i = 0; i < update.Deleted.Count; i++)
        {
            CodexRadarModelInfo model = update.Deleted[i];
            ShowCodexNotification(
                "Codex Radar 模型已删除",
                CodexRadarModelCatalog.GetDisplayLabel(model == null ? string.Empty : model.Label, model == null ? string.Empty : model.Key) + " 连续多次未出现在网站模型列表中，已从检测列表移除。",
                ToolTipIcon.Warning);
        }
    }

    private void HandleCodexRadarWindowAndResetEvents(CodexRadarSnapshot snapshot)
    {
        HandleCodexRadarOpenEvent(snapshot);
        HandleCodexRadarResetEvent(snapshot);
    }

    private void HandleCodexRadarOpenEvent(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.SpeedWindowKnown || !snapshot.SpeedWindowOpen)
        {
            return;
        }

        string eventId = (snapshot.SpeedWindowEventId ?? string.Empty).Trim();
        DateTime openedUtc = snapshot.SpeedWindowOpenedAtKnown
            ? snapshot.SpeedWindowOpenedAtLocal.ToUniversalTime()
            : DateTime.MinValue;
        if (eventId.Length == 0 && openedUtc == DateTime.MinValue)
        {
            return;
        }

        bool stateChanged = false;
        bool isNewOpen = false;
        lock (this.quotaResetStateLock)
        {
            bool firstOpen = this.lastRadarOpenEventId.Length == 0 &&
                this.lastRadarOpenEventUtc == DateTime.MinValue;
            bool newerOpen = openedUtc != DateTime.MinValue && openedUtc > this.lastRadarOpenEventUtc;
            bool differentIdWithoutTime = openedUtc == DateTime.MinValue &&
                eventId.Length > 0 &&
                !string.Equals(eventId, this.lastRadarOpenEventId, StringComparison.Ordinal);
            if (firstOpen || newerOpen || differentIdWithoutTime)
            {
                this.lastRadarOpenEventId = eventId;
                this.lastRadarOpenEventUtc = openedUtc;
                stateChanged = true;
                isNewOpen = true;
            }
            else if (openedUtc == this.lastRadarOpenEventUtc &&
                eventId.Length > 0 &&
                !string.Equals(eventId, this.lastRadarOpenEventId, StringComparison.Ordinal))
            {
                this.lastRadarOpenEventId = eventId;
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
        }

        if (isNewOpen)
        {
            ShowCodexNotification(
                "Codex 速蹬窗口开启",
                "检测到速蹬窗口已开启。",
                ToolTipIcon.Info);
        }
    }

    private void HandleCodexRadarResetEvent(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ResetEventKnown)
        {
            return;
        }

        string eventId = (snapshot.ResetEventId ?? string.Empty).Trim();
        DateTime eventUtc = NormalizeStateUtc(snapshot.ResetEventUtc);
        if (eventId.Length == 0 && eventUtc == DateTime.MinValue)
        {
            return;
        }

        bool stateChanged = false;
        bool isNewReset = false;
        string eventKey = GetRadarResetEventKey(eventId, eventUtc);
        lock (this.quotaResetStateLock)
        {
            bool sameEventId = eventId.Length > 0 &&
                string.Equals(eventId, this.lastRadarResetEventId, StringComparison.Ordinal);
            bool alreadyProtected = eventKey.Length > 0 &&
                string.Equals(eventKey, this.lastRadarProtectedResetEventId, StringComparison.Ordinal);
            bool newerEvent = eventUtc != DateTime.MinValue && eventUtc > this.lastRadarResetEventUtc;
            bool firstRecentEvent = this.lastRadarResetEventUtc == DateTime.MinValue &&
                eventUtc != DateTime.MinValue &&
                DateTime.UtcNow - eventUtc <= TimeSpan.FromHours(36.0);
            bool sameRecentEventNotProtected = sameEventId &&
                !alreadyProtected &&
                eventUtc != DateTime.MinValue &&
                DateTime.UtcNow - eventUtc <= TimeSpan.FromHours(36.0);
            bool differentIdWithoutTime = eventUtc == DateTime.MinValue &&
                eventId.Length > 0 &&
                !sameEventId;
            if (!alreadyProtected &&
                (!sameEventId && (newerEvent || firstRecentEvent || differentIdWithoutTime) ||
                 sameRecentEventNotProtected))
            {
                this.lastRadarResetEventId = eventId;
                this.lastRadarResetEventUtc = eventUtc;
                stateChanged = true;
                isNewReset = true;
            }
            else if (eventUtc == this.lastRadarResetEventUtc &&
                eventId.Length > 0 &&
                !sameEventId)
            {
                this.lastRadarResetEventId = eventId;
                stateChanged = true;
            }
        }

        if (isNewReset)
        {
            DateTime detectedUtc = DateTime.UtcNow;
            bool protectionSaved = ActivateQuotaResetProtections(
                true,
                detectedUtc,
                true,
                detectedUtc,
                "CodexRadar RSS reset event " + eventId,
                true);
            lock (this.quotaResetStateLock)
            {
                this.lastRadarProtectedResetEventId = eventKey;
            }

            if (!protectionSaved)
            {
                SaveQuotaResetState();
            }
            else
            {
                SaveQuotaResetState();
            }

            ShowCodexNotification(
                "Codex 额外重置",
                "检测到新的 Codex 重置记录，余额已恢复至 100。",
                ToolTipIcon.Warning);
            this.lastQuotaRefreshUtc = DateTime.MinValue;
            RenderLayeredWindow();
            return;
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
        }
    }

    private static string GetRadarResetEventKey(string eventId, DateTime eventUtc)
    {
        string key = (eventId ?? string.Empty).Trim();
        if (key.Length > 0)
        {
            return key;
        }

        DateTime normalized = NormalizeStateUtc(eventUtc);
        return normalized == DateTime.MinValue
            ? string.Empty
            : normalized.ToString("o", CultureInfo.InvariantCulture);
    }

    private void ActivateDueQuotaResetProtections(CodexQuotaSnapshot snapshot, DateTime nowLocal, DateTime detectedUtc)
    {
        if (snapshot == null)
        {
            return;
        }

        bool fiveHourDue = snapshot.FiveHourResetKnown && snapshot.FiveHourResetLocal <= nowLocal;
        bool weeklyDue = snapshot.WeeklyResetKnown && snapshot.WeeklyResetLocal <= nowLocal;
        lock (this.quotaResetStateLock)
        {
            fiveHourDue = fiveHourDue && this.fiveHourQuotaProtectionUtc == DateTime.MinValue;
            weeklyDue = weeklyDue && this.weeklyQuotaProtectionUtc == DateTime.MinValue;
        }

        if (!fiveHourDue && !weeklyDue)
        {
            return;
        }

        ActivateQuotaResetProtections(
            fiveHourDue,
            detectedUtc,
            weeklyDue,
            detectedUtc,
            "local resets_at reached",
            false);
    }

    private bool ActivateQuotaResetProtections(
        bool protectFiveHour,
        DateTime fiveHourProtectionUtc,
        bool protectWeekly,
        DateTime weeklyProtectionUtc,
        string reason,
        bool gold)
    {
        bool stateChanged = false;
        lock (this.quotaResetStateLock)
        {
            if (protectFiveHour)
            {
                DateTime normalized = NormalizeStateUtc(fiveHourProtectionUtc);
                if (normalized == DateTime.MinValue)
                {
                    normalized = DateTime.UtcNow;
                }

                if (normalized > this.fiveHourQuotaProtectionUtc)
                {
                    this.fiveHourQuotaProtectionUtc = normalized;
                    this.fiveHourQuotaProtectionGold = gold;
                    stateChanged = true;
                }
                else if (gold && !this.fiveHourQuotaProtectionGold)
                {
                    this.fiveHourQuotaProtectionGold = true;
                    stateChanged = true;
                }
            }

            if (protectWeekly)
            {
                DateTime normalized = NormalizeStateUtc(weeklyProtectionUtc);
                if (normalized == DateTime.MinValue)
                {
                    normalized = DateTime.UtcNow;
                }

                if (normalized > this.weeklyQuotaProtectionUtc)
                {
                    this.weeklyQuotaProtectionUtc = normalized;
                    this.weeklyQuotaProtectionGold = gold;
                    stateChanged = true;
                }
                else if (gold && !this.weeklyQuotaProtectionGold)
                {
                    this.weeklyQuotaProtectionGold = true;
                    stateChanged = true;
                }
            }
        }

        if (this.quotaSnapshot == null)
        {
            this.quotaSnapshot = CodexQuotaSnapshot.CreateDefault();
        }

        if (protectFiveHour)
        {
            ForceFiveHourQuotaToFull(this.quotaSnapshot);
        }

        if (protectWeekly)
        {
            ForceWeeklyQuotaToFull(this.quotaSnapshot);
        }

        if (stateChanged)
        {
            Program.LogInfo("Quota reset protection activated. Reason=" + reason);
            SaveQuotaResetState();
        }

        return stateChanged;
    }

    private CodexQuotaSnapshot ApplyQuotaResetProtections(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            snapshot = CodexQuotaSnapshot.CreateDefault();
        }

        bool stateChanged = false;
        bool fiveHourReleased = false;
        bool weeklyReleased = false;
        lock (this.quotaResetStateLock)
        {
            if (this.fiveHourQuotaProtectionUtc != DateTime.MinValue)
            {
                if (IsQuotaProtectionReleaseSample(
                    snapshot,
                    this.fiveHourQuotaProtectionUtc,
                    snapshot.FiveHourResetKnown,
                    snapshot.FiveHourResetLocal))
                {
                    this.fiveHourQuotaProtectionUtc = DateTime.MinValue;
                    this.fiveHourQuotaProtectionGold = false;
                    stateChanged = true;
                    fiveHourReleased = true;
                }
                else
                {
                    ForceFiveHourQuotaToFull(snapshot);
                }
            }

            if (this.weeklyQuotaProtectionUtc != DateTime.MinValue)
            {
                if (IsQuotaProtectionReleaseSample(
                    snapshot,
                    this.weeklyQuotaProtectionUtc,
                    snapshot.WeeklyResetKnown,
                    snapshot.WeeklyResetLocal))
                {
                    this.weeklyQuotaProtectionUtc = DateTime.MinValue;
                    this.weeklyQuotaProtectionGold = false;
                    stateChanged = true;
                    weeklyReleased = true;
                }
                else
                {
                    ForceWeeklyQuotaToFull(snapshot);
                }
            }
        }

        if (stateChanged)
        {
            SaveQuotaResetState();
            if (fiveHourReleased)
            {
                Program.LogInfo("Five-hour quota reset protection released by a newer quota sample.");
            }

            if (weeklyReleased)
            {
                Program.LogInfo("Weekly quota reset protection released by a newer quota sample.");
            }
        }

        return snapshot;
    }

    private static bool IsQuotaProtectionReleaseSample(
        CodexQuotaSnapshot snapshot,
        DateTime protectionUtc,
        bool resetKnown,
        DateTime resetLocal)
    {
        // Keep 100 visible until a post-protection sample proves the next quota window exists.
        return snapshot.SourceUpdatedKnown &&
            snapshot.SourceUpdatedUtc > protectionUtc &&
            (!resetKnown || resetLocal > DateTime.Now);
    }

    private static void ForceFiveHourQuotaToFull(CodexQuotaSnapshot snapshot)
    {
        snapshot.FiveHourPercent = 100;
        snapshot.FiveHourResetLocal = DateTime.MinValue;
        snapshot.FiveHourResetKnown = false;
    }

    private static void ForceWeeklyQuotaToFull(CodexQuotaSnapshot snapshot)
    {
        snapshot.WeeklyPercent = 100;
        snapshot.WeeklyResetLocal = DateTime.MinValue;
        snapshot.WeeklyResetKnown = false;
    }

    private static string CodexRadarCachePath
    {
        get { return Path.Combine(Logger.DirectoryPath, "codex-radar-cache.ini"); }
    }

    private static CodexRadarSnapshot LoadCodexRadarCache(string modelKey)
    {
        lock (codexRadarDiskCacheLock)
        {
            string path = CodexRadarCachePath;
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                Dictionary<string, string> values = ReadSimpleKeyValueFile(path);
                string prefix = GetCodexRadarCachePrefix(modelKey);
                DateTime savedUtc;
                if (!TryReadCacheUtc(values, prefix + "SavedUtc", out savedUtc) ||
                    savedUtc == DateTime.MinValue ||
                    DateTime.UtcNow - savedUtc > TimeSpan.FromDays(CodexModelCacheRetentionDays))
                {
                    return null;
                }

                DateTime dataDate;
                int passRate;
                if (!TryReadCacheDate(values, prefix + "DataDate", out dataDate) ||
                    !TryReadCacheInt(values, prefix + "PassRate", out passRate))
                {
                    return null;
                }

                CodexRadarSnapshot snapshot = CodexRadarSnapshot.CreateDefault();
                snapshot.ModelIqDataDateLocal = dataDate.Date;
                snapshot.ModelIqDataDateKnown = true;
                int dataWindowHour;
                if (TryReadCacheInt(values, prefix + "DataWindowHour", out dataWindowHour))
                {
                    snapshot.ModelIqDataWindowStartHourLocal = dataWindowHour >= 12 ? 12 : 0;
                    snapshot.ModelIqDataWindowKnown = true;
                }
                snapshot.ModelIqPassRatePercent = Math.Max(
                    0,
                    Math.Min(MaxCodexModelIqScore, passRate));
                snapshot.ModelIqStatus = GetCacheValue(values, prefix + "Status", "invalid");
                int passed;
                if (TryReadCacheInt(values, prefix + "Passed", out passed))
                {
                    snapshot.ModelIqPassed = passed;
                }

                int validTasks;
                if (!TryReadCacheInt(values, prefix + "ValidTasks", out validTasks) ||
                    validTasks <= 0)
                {
                    validTasks = CodexModelIqNominalTasks;
                }

                snapshot.ModelIqValidTasks = validTasks;
                snapshot.ModelIqPassedKnown = true;
                int tokenEfficiency;
                int timeEfficiency;
                if (TryReadCacheInt(values, prefix + "TokenEfficiency", out tokenEfficiency))
                {
                    snapshot.ModelIqTokenEfficiencyPercent = tokenEfficiency;
                }

                if (TryReadCacheInt(values, prefix + "TimeEfficiency", out timeEfficiency))
                {
                    snapshot.ModelIqTimeEfficiencyPercent = timeEfficiency;
                }

                snapshot.ModelIqEfficiencyKnown =
                    snapshot.ModelIqTokenEfficiencyPercent > 0 ||
                    snapshot.ModelIqTimeEfficiencyPercent > 0;
                double efficiencyPassed;
                double efficiencyTokens;
                double efficiencySeconds;
                if (TryReadCacheDouble(values, prefix + "EfficiencyPassed", out efficiencyPassed))
                {
                    snapshot.ModelIqEfficiencyPassed = efficiencyPassed;
                }

                if (TryReadCacheDouble(values, prefix + "EfficiencyTokens", out efficiencyTokens))
                {
                    snapshot.ModelIqEfficiencyTotalTokens = efficiencyTokens;
                }

                if (TryReadCacheDouble(values, prefix + "EfficiencySeconds", out efficiencySeconds))
                {
                    snapshot.ModelIqEfficiencySerialSeconds = efficiencySeconds;
                }

                snapshot.ModelIqEfficiencyInputKnown =
                    snapshot.ModelIqEfficiencyPassed > 0.0 &&
                    snapshot.ModelIqEfficiencyTotalTokens > 0.0 &&
                    snapshot.ModelIqEfficiencySerialSeconds > 0.0;

                DateTime refreshedUtc;
                if (TryReadCacheUtc(values, prefix + "RefreshedUtc", out refreshedUtc))
                {
                    snapshot.ModelIqRefreshedAtLocal = refreshedUtc.ToLocalTime();
                    snapshot.ModelIqRefreshedAtKnown = true;
                }

                snapshot.ModelIqHistory = ParseCodexModelHistory(
                    GetCacheValue(values, prefix + "History", string.Empty));
                UpsertCodexModelHistoryPoint(
                    snapshot.ModelIqHistory,
                    snapshot.ModelIqDataDateLocal.Date.AddHours(snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0),
                    snapshot.ModelIqPassRatePercent);
                snapshot.ModelIqHistory = NormalizeCodexModelHistory(snapshot.ModelIqHistory);
                snapshot.ModelIqRefreshSucceeded = false;
                snapshot.ModelIqKnown = true;
                return snapshot;
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                return null;
            }
        }
    }

    private static void SaveCodexRadarCache(
        string modelKey,
        CodexRadarSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.ModelIqKnown || !snapshot.ModelIqDataDateKnown)
        {
            return;
        }

        lock (codexRadarDiskCacheLock)
        {
            try
            {
                Directory.CreateDirectory(Logger.DirectoryPath);
                string path = CodexRadarCachePath;
                Dictionary<string, string> values = File.Exists(path)
                    ? ReadSimpleKeyValueFile(path)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                RemoveExpiredCodexRadarCacheModels(values);

                string prefix = GetCodexRadarCachePrefix(modelKey);
                values[prefix + "SavedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                values[prefix + "RefreshedUtc"] = snapshot.ModelIqRefreshedAtKnown
                    ? snapshot.ModelIqRefreshedAtLocal.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                    : string.Empty;
                values[prefix + "DataDate"] = snapshot.ModelIqDataDateLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                values[prefix + "DataWindowHour"] = (snapshot.ModelIqDataWindowKnown
                    ? (snapshot.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0)
                    : 0).ToString(CultureInfo.InvariantCulture);
                values[prefix + "Status"] = snapshot.ModelIqStatus ?? "invalid";
                values[prefix + "PassRate"] = snapshot.ModelIqPassRatePercent.ToString(CultureInfo.InvariantCulture);
                values[prefix + "Passed"] = snapshot.ModelIqPassed.ToString(CultureInfo.InvariantCulture);
                values[prefix + "ValidTasks"] = snapshot.ModelIqValidTasks.ToString(CultureInfo.InvariantCulture);
                values[prefix + "TokenEfficiency"] = snapshot.ModelIqTokenEfficiencyPercent.ToString(CultureInfo.InvariantCulture);
                values[prefix + "TimeEfficiency"] = snapshot.ModelIqTimeEfficiencyPercent.ToString(CultureInfo.InvariantCulture);
                values[prefix + "EfficiencyPassed"] = snapshot.ModelIqEfficiencyPassed.ToString("R", CultureInfo.InvariantCulture);
                values[prefix + "EfficiencyTokens"] = snapshot.ModelIqEfficiencyTotalTokens.ToString("R", CultureInfo.InvariantCulture);
                values[prefix + "EfficiencySeconds"] = snapshot.ModelIqEfficiencySerialSeconds.ToString("R", CultureInfo.InvariantCulture);
                values[prefix + "History"] = FormatCodexModelHistory(snapshot.ModelIqHistory);

                string tempPath = path + ".tmp";
                List<string> lines = new List<string>();
                lines.Add("Version=1");
                foreach (KeyValuePair<string, string> pair in values)
                {
                    if (!string.Equals(pair.Key, "Version", StringComparison.OrdinalIgnoreCase))
                    {
                        lines.Add(pair.Key + "=" + (pair.Value ?? string.Empty));
                    }
                }

                File.WriteAllLines(tempPath, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }
    }

    private static Dictionary<string, string> ReadSimpleKeyValueFile(string path)
    {
        Dictionary<string, string> values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
        }

        return values;
    }

    private static string GetCodexRadarCachePrefix(string modelKey)
    {
        string key = CodexRadarModelCatalog.NormalizeModelKey(modelKey);
        if (string.Equals(key, "gpt_55_medium", StringComparison.OrdinalIgnoreCase))
        {
            return "Gpt55Medium.";
        }

        if (string.Equals(key, "gpt_54_xhigh", StringComparison.OrdinalIgnoreCase))
        {
            return "Gpt54.";
        }

        if (string.Equals(key, CodexRadarModelCatalog.DefaultModelKey, StringComparison.OrdinalIgnoreCase))
        {
            return "Gpt55.";
        }

        return "Model." + key + ".";
    }

    private static void RemoveExpiredCodexRadarCacheModels(Dictionary<string, string> values)
    {
        List<string> prefixes = new List<string>();
        foreach (string key in values.Keys)
        {
            int split = key.LastIndexOf('.');
            if (split > 0)
            {
                string prefix = key.Substring(0, split + 1);
                if (!prefixes.Contains(prefix))
                {
                    prefixes.Add(prefix);
                }
            }
        }

        List<string> keys = new List<string>();
        for (int i = 0; i < prefixes.Count; i++)
        {
            string prefix = prefixes[i];
            DateTime savedUtc;
            if (TryReadCacheUtc(values, prefix + "SavedUtc", out savedUtc) &&
                DateTime.UtcNow - savedUtc <= TimeSpan.FromDays(CodexModelCacheRetentionDays))
            {
                continue;
            }

            foreach (string key in values.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(key);
                }
            }
        }

        for (int i = 0; i < keys.Count; i++)
        {
            values.Remove(keys[i]);
        }
    }

    private static string GetCacheValue(
        Dictionary<string, string> values,
        string key,
        string fallback)
    {
        string value;
        return values != null && values.TryGetValue(key, out value) ? value : fallback;
    }

    private static bool TryReadCacheUtc(
        Dictionary<string, string> values,
        string key,
        out DateTime utc)
    {
        utc = DateTime.MinValue;
        string text = GetCacheValue(values, key, string.Empty);
        DateTimeOffset parsed;
        if (!DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return true;
    }

    private static bool TryReadCacheDate(
        Dictionary<string, string> values,
        string key,
        out DateTime date)
    {
        return DateTime.TryParseExact(
            GetCacheValue(values, key, string.Empty),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static bool TryReadCacheInt(
        Dictionary<string, string> values,
        string key,
        out int number)
    {
        return int.TryParse(
            GetCacheValue(values, key, string.Empty),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static bool TryReadCacheDouble(
        Dictionary<string, string> values,
        string key,
        out double number)
    {
        return double.TryParse(
            GetCacheValue(values, key, string.Empty),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static string FormatCodexModelHistory(IEnumerable<CodexModelHistoryPoint> history)
    {
        List<CodexModelHistoryPoint> points = NormalizeCodexModelHistory(history);
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < points.Count; i++)
        {
            if (builder.Length > 0)
            {
                builder.Append(';');
            }

            CodexModelHistoryPoint point = points[i];
            builder.Append(FormatCodexModelHistoryDate(point.DateLocal));
            builder.Append(',');
            builder.Append(point.Score.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.Passed.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.TotalTokens.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.SerialSeconds.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.CachedInputTokens.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.InputTokens.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.Tasks.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.InvalidTasks.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.TokenEfficiencyPercent.ToString("0.##", CultureInfo.InvariantCulture));
            builder.Append(',');
            builder.Append(point.TimeEfficiencyPercent.ToString("0.##", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private static string FormatCodexModelHistoryDate(DateTime value)
    {
        DateTime key = NormalizeCodexModelHistoryKey(value);
        string suffix = key.Hour >= 12 ? "-pm" : "-am";
        return key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + suffix;
    }

    private static bool TryParseCodexModelHistoryDate(string value, out DateTime date)
    {
        date = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        DateTime parsed;
        int windowHour;
        if (TryReadCodexModelIqDataWindow(value.Trim(), out parsed, out windowHour))
        {
            date = NormalizeCodexModelHistoryKey(parsed.Date.AddHours(windowHour));
            return true;
        }

        return false;
    }

    private static List<CodexModelHistoryPoint> ParseCodexModelHistory(string text)
    {
        List<CodexModelHistoryPoint> history = new List<CodexModelHistoryPoint>();
        if (string.IsNullOrEmpty(text))
        {
            return history;
        }

        string[] entries = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < entries.Length; i++)
        {
            string[] fields = entries[i].Split(',');
            if (fields.Length >= 11)
            {
                DateTime richDate;
                double[] numbers = new double[10];
                bool valid = TryParseCodexModelHistoryDate(fields[0], out richDate);
                for (int field = 1; field < fields.Length && field <= numbers.Length; field++)
                {
                    double number;
                    valid &= double.TryParse(
                        fields[field],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out number);
                    numbers[field - 1] = number;
                }

                if (valid)
                {
                    UpsertCodexModelHistoryPoint(
                        history,
                        new CodexModelHistoryPoint
                        {
                            DateLocal = richDate,
                            Score = numbers[0],
                            Passed = numbers[1],
                            TotalTokens = numbers[2],
                            SerialSeconds = numbers[3],
                            CachedInputTokens = numbers[4],
                            InputTokens = numbers[5],
                            Tasks = numbers[6],
                            InvalidTasks = numbers[7],
                            TokenEfficiencyPercent = numbers[8],
                            TimeEfficiencyPercent = numbers[9],
                            EfficiencyKnown = numbers[8] > 0.0 || numbers[9] > 0.0,
                            CacheRateKnown = numbers[5] > 0.0,
                            ValidityKnown = numbers[6] > 0.0
                        });
                    continue;
                }
            }

            int split = entries[i].LastIndexOf(':');
            DateTime date;
            double score;
            if (split > 0 &&
                TryParseCodexModelHistoryDate(entries[i].Substring(0, split), out date) &&
                double.TryParse(
                    entries[i].Substring(split + 1),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out score))
            {
                UpsertCodexModelHistoryPoint(history, date, score);
            }
        }

        return NormalizeCodexModelHistory(history);
    }

    private static string QuotaResetStatePath
    {
        get { return Path.Combine(Logger.DirectoryPath, "quota-reset-state.ini"); }
    }

    private void LoadQuotaResetState()
    {
        string path = QuotaResetStatePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            string[] lines = File.ReadAllLines(path);
            lock (this.quotaResetStateLock)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    int split = line.IndexOf('=');
                    if (split <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    bool boolValue;
                    DateTime utcValue;
                    if ((string.Equals(key, "LastRadarResetEventId", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(key, "LastRadarEventId", StringComparison.OrdinalIgnoreCase)) &&
                        value.Length > 0)
                    {
                        this.lastRadarResetEventId = value;
                    }
                    else if ((string.Equals(key, "LastRadarResetEventUtc", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(key, "LastRadarEventClosedUtc", StringComparison.OrdinalIgnoreCase)) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.lastRadarResetEventUtc = utcValue;
                    }
                    else if (string.Equals(key, "LastRadarProtectedResetEventId", StringComparison.OrdinalIgnoreCase) &&
                        value.Length > 0)
                    {
                        this.lastRadarProtectedResetEventId = value;
                    }
                    else if (string.Equals(key, "LastRadarOpenEventId", StringComparison.OrdinalIgnoreCase) &&
                        value.Length > 0)
                    {
                        this.lastRadarOpenEventId = value;
                    }
                    else if (string.Equals(key, "LastRadarOpenEventUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.lastRadarOpenEventUtc = utcValue;
                    }
                    else if (string.Equals(key, "FiveHourProtectionUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.fiveHourQuotaProtectionUtc = utcValue;
                    }
                    else if (string.Equals(key, "WeeklyProtectionUtc", StringComparison.OrdinalIgnoreCase) &&
                        TryParseStateUtc(value, out utcValue))
                    {
                        this.weeklyQuotaProtectionUtc = utcValue;
                    }
                    else if (string.Equals(key, "FiveHourProtectionGold", StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value, out boolValue))
                    {
                        this.fiveHourQuotaProtectionGold = boolValue;
                    }
                    else if (string.Equals(key, "WeeklyProtectionGold", StringComparison.OrdinalIgnoreCase) &&
                        bool.TryParse(value, out boolValue))
                    {
                        this.weeklyQuotaProtectionGold = boolValue;
                    }
                }

                if (this.fiveHourQuotaProtectionUtc == DateTime.MinValue)
                {
                    this.fiveHourQuotaProtectionGold = false;
                }

                if (this.weeklyQuotaProtectionUtc == DateTime.MinValue)
                {
                    this.weeklyQuotaProtectionGold = false;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void SaveQuotaResetState()
    {
        try
        {
            lock (this.quotaResetStateLock)
            {
                Directory.CreateDirectory(Logger.DirectoryPath);
                string resetEventId = SanitizeStateValue(this.lastRadarResetEventId);
                string protectedResetEventId = SanitizeStateValue(this.lastRadarProtectedResetEventId);
                string openEventId = SanitizeStateValue(this.lastRadarOpenEventId);
                File.WriteAllLines(
                    QuotaResetStatePath,
                    new[]
                    {
                        "Version=5",
                        "LastRadarResetEventId=" + resetEventId,
                        "LastRadarResetEventUtc=" + FormatStateUtc(this.lastRadarResetEventUtc),
                        "LastRadarProtectedResetEventId=" + protectedResetEventId,
                        "LastRadarOpenEventId=" + openEventId,
                        "LastRadarOpenEventUtc=" + FormatStateUtc(this.lastRadarOpenEventUtc),
                        "FiveHourProtectionUtc=" + FormatStateUtc(this.fiveHourQuotaProtectionUtc),
                        "WeeklyProtectionUtc=" + FormatStateUtc(this.weeklyQuotaProtectionUtc),
                        "FiveHourProtectionGold=" + this.fiveHourQuotaProtectionGold.ToString(CultureInfo.InvariantCulture),
                        "WeeklyProtectionGold=" + this.weeklyQuotaProtectionGold.ToString(CultureInfo.InvariantCulture)
                    },
                    Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static string SanitizeStateValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Trim();
    }

    private static string FormatStateUtc(DateTime value)
    {
        DateTime normalized = NormalizeStateUtc(value);
        return normalized == DateTime.MinValue
            ? string.Empty
            : normalized.ToString("o", CultureInfo.InvariantCulture);
    }

    private static bool TryParseStateUtc(string text, out DateTime value)
    {
        value = DateTime.MinValue;
        DateTimeOffset parsed;
        if (!DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed))
        {
            return false;
        }

        value = parsed.UtcDateTime;
        return true;
    }

    private static DateTime NormalizeStateUtc(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return DateTime.MinValue;
        }

        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }

    private CodexQuotaSnapshot ReadQuotaSnapshot(out bool sourceKnown)
    {
        CodexQuotaSnapshot snapshot;
        if (TryReadCodexSessionQuota(out snapshot))
        {
            sourceKnown = true;
            snapshot = NormalizeQuotaSnapshot(snapshot);
            TryWriteQuotaIniSnapshot(snapshot);
            return snapshot;
        }

        if (TryReadQuotaIniSnapshot(out snapshot))
        {
            sourceKnown = true;
            return NormalizeQuotaSnapshot(snapshot);
        }

        sourceKnown = false;
        return CodexQuotaSnapshot.CreateDefault();
    }

    private static CodexQuotaSnapshot NormalizeQuotaSnapshot(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return CodexQuotaSnapshot.CreateDefault();
        }

        snapshot.FiveHourPercent = ClampPercent(snapshot.FiveHourPercent);
        snapshot.WeeklyPercent = ClampPercent(snapshot.WeeklyPercent);
        return snapshot;
    }

    private static bool IsCachedQuotaSnapshotCurrent(
        string sessionsPath,
        string cachedPath,
        DateTime cachedWriteUtc)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (codexQuotaSnapshotNewestVerifyUtc != DateTime.MinValue &&
            (nowUtc - codexQuotaSnapshotNewestVerifyUtc).TotalSeconds < 30.0)
        {
            return true;
        }

        codexQuotaSnapshotNewestVerifyUtc = nowUtc;
        string newestPath;
        DateTime newestWriteUtc;
        if (!TryFindNewestQuotaRolloutFile(sessionsPath, out newestPath, out newestWriteUtc))
        {
            return true;
        }

        return string.Equals(newestPath, cachedPath, StringComparison.OrdinalIgnoreCase) &&
            newestWriteUtc <= cachedWriteUtc;
    }

    private static bool TryFindNewestQuotaRolloutFile(
        string sessionsPath,
        out string newestPath,
        out DateTime newestWriteUtc)
    {
        newestPath = string.Empty;
        newestWriteUtc = DateTime.MinValue;
        if (string.IsNullOrEmpty(sessionsPath) || !Directory.Exists(sessionsPath))
        {
            return false;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(
                sessionsPath,
                "rollout-*.jsonl",
                SearchOption.AllDirectories))
            {
                DateTime writeUtc = SafeGetLastWriteTimeUtc(file);
                if (writeUtc > newestWriteUtc)
                {
                    newestPath = file;
                    newestWriteUtc = writeUtc;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        return !string.IsNullOrEmpty(newestPath);
    }

    private bool TryReadCodexSessionQuota(out CodexQuotaSnapshot snapshot)
    {
        snapshot = null;
        string sessionsPath = this.quotaSessionsPath;
        if (string.IsNullOrEmpty(sessionsPath))
        {
            return false;
        }

        if (!Directory.Exists(sessionsPath))
        {
            return false;
        }

        // Clear the watcher hint before scanning so an append that happens during the scan
        // sets it again and is observed on the next quota refresh.
        bool filesChanged = this.quotaSessionWatcher == null ||
            Interlocked.Exchange(ref this.quotaSessionFilesChanged, 0) != 0;
        if (!filesChanged)
        {
            lock (codexQuotaSnapshotCacheLock)
            {
                // The watcher is only an invalidation hint; metadata still verifies append-only changes.
                if (codexQuotaSnapshotCache != null &&
                    File.Exists(codexQuotaSnapshotCachePath) &&
                    codexQuotaSnapshotCacheWriteUtc == SafeGetLastWriteTimeUtc(codexQuotaSnapshotCachePath) &&
                    codexQuotaSnapshotCacheLength == SafeGetFileLength(codexQuotaSnapshotCachePath) &&
                    IsCachedQuotaSnapshotCurrent(sessionsPath, codexQuotaSnapshotCachePath, codexQuotaSnapshotCacheWriteUtc))
                {
                    snapshot = codexQuotaSnapshotCache.Clone();
                    return true;
                }
            }
        }

        List<string> rolloutFiles = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name != null && name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
                {
                    rolloutFiles.Add(file);
                }
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
            Program.LogException(ex);
            return false;
        }

        if (rolloutFiles.Count == 0)
        {
            return false;
        }

        rolloutFiles.Sort(delegate(string left, string right)
        {
            return SafeGetLastWriteTimeUtc(right).CompareTo(SafeGetLastWriteTimeUtc(left));
        });

        string newestPath = rolloutFiles[0];
        DateTime newestWriteUtc = SafeGetLastWriteTimeUtc(newestPath);
        long newestLength = SafeGetFileLength(newestPath);
        lock (codexQuotaSnapshotCacheLock)
        {
            // Length participates in the key because the active JSONL file is append-only.
            if (codexQuotaSnapshotCache != null &&
                string.Equals(codexQuotaSnapshotCachePath, newestPath, StringComparison.OrdinalIgnoreCase) &&
                codexQuotaSnapshotCacheWriteUtc == newestWriteUtc &&
                codexQuotaSnapshotCacheLength == newestLength)
            {
                snapshot = codexQuotaSnapshotCache.Clone();
                return true;
            }
        }

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;

        CodexQuotaEvent latestEvent = null;
        int count = Math.Min(rolloutFiles.Count, MaxQuotaRolloutFilesToScan);
        for (int i = 0; i < count; i++)
        {
            string file = rolloutFiles[i];
            if (latestEvent != null && SafeGetLastWriteTimeUtc(file) < latestEvent.UpdatedUtc)
            {
                break;
            }

            CodexQuotaEvent quotaEvent;
            if (TryParseLatestQuotaEventFromFile(file, serializer, out quotaEvent) &&
                (latestEvent == null || quotaEvent.UpdatedUtc > latestEvent.UpdatedUtc))
            {
                latestEvent = quotaEvent;
            }
        }

        if (latestEvent == null)
        {
            return false;
        }

        snapshot = latestEvent.Snapshot;
        if (snapshot != null)
        {
            snapshot.SourceUpdatedUtc = latestEvent.UpdatedUtc;
            snapshot.SourceUpdatedKnown = latestEvent.UpdatedUtc != DateTime.MinValue;
        }

        if (snapshot != null)
        {
            lock (codexQuotaSnapshotCacheLock)
            {
                codexQuotaSnapshotCachePath = newestPath;
                codexQuotaSnapshotCacheWriteUtc = newestWriteUtc;
                codexQuotaSnapshotCacheLength = newestLength;
                codexQuotaSnapshotCache = snapshot.Clone();
            }

        }

        return snapshot != null;
    }

    private void InitializeQuotaSessionWatcher()
    {
        string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profilePath))
        {
            return;
        }

        this.quotaSessionsPath = Path.Combine(Path.Combine(profilePath, ".codex"), "sessions");
        if (!Directory.Exists(this.quotaSessionsPath))
        {
            return;
        }

        try
        {
            FileSystemWatcher watcher = new FileSystemWatcher(this.quotaSessionsPath, "rollout-*.jsonl");
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter =
                NotifyFilters.FileName |
                NotifyFilters.DirectoryName |
                NotifyFilters.LastWrite |
                NotifyFilters.Size;
            watcher.Changed += OnQuotaSessionFileChanged;
            watcher.Created += OnQuotaSessionFileChanged;
            watcher.Deleted += OnQuotaSessionFileChanged;
            watcher.Renamed += OnQuotaSessionFileRenamed;
            watcher.Error += OnQuotaSessionWatcherError;
            watcher.EnableRaisingEvents = true;
            this.quotaSessionWatcher = watcher;
        }
        catch (Exception ex)
        {
            // Without a watcher the changed flag remains set, preserving the original polling behavior.
            Program.LogException(ex);
        }
    }

    private void OnQuotaSessionFileChanged(object sender, FileSystemEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
        codexQuotaSnapshotNewestVerifyUtc = DateTime.MinValue;
    }

    private void OnQuotaSessionFileRenamed(object sender, RenamedEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
        codexQuotaSnapshotNewestVerifyUtc = DateTime.MinValue;
    }

    private void OnQuotaSessionWatcherError(object sender, ErrorEventArgs e)
    {
        Interlocked.Exchange(ref this.quotaSessionFilesChanged, 1);
        codexQuotaSnapshotNewestVerifyUtc = DateTime.MinValue;
    }

    private void DisposeQuotaSessionWatcher()
    {
        FileSystemWatcher watcher = this.quotaSessionWatcher;
        this.quotaSessionWatcher = null;
        if (watcher == null)
        {
            return;
        }

        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnQuotaSessionFileChanged;
        watcher.Created -= OnQuotaSessionFileChanged;
        watcher.Deleted -= OnQuotaSessionFileChanged;
        watcher.Renamed -= OnQuotaSessionFileRenamed;
        watcher.Error -= OnQuotaSessionWatcherError;
        watcher.Dispose();
    }

    private static bool TryParseLatestQuotaEventFromFile(string path, JavaScriptSerializer serializer, out CodexQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // Quota events are near the end of rollout files. Read backwards in bounded
                // chunks instead of deserializing the complete session history on every refresh.
                long offset = stream.Length;
                byte[] tail = new byte[0];
                while (offset > 0)
                {
                    int readSize = (int)Math.Min(QuotaTailChunkBytes, offset);
                    offset -= readSize;
                    stream.Seek(offset, SeekOrigin.Begin);

                    byte[] chunk = new byte[readSize];
                    int read = stream.Read(chunk, 0, readSize);
                    if (read <= 0)
                    {
                        continue;
                    }

                    byte[] expandedTail = new byte[read + tail.Length];
                    Buffer.BlockCopy(chunk, 0, expandedTail, 0, read);
                    if (tail.Length > 0)
                    {
                        Buffer.BlockCopy(tail, 0, expandedTail, read, tail.Length);
                    }

                    tail = expandedTail;
                    string text = Encoding.UTF8.GetString(tail, 0, tail.Length);
                    if (TryParseLatestQuotaEventFromText(text, path, serializer, out quotaEvent))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static bool TryParseLatestQuotaEventFromText(string text, string path, JavaScriptSerializer serializer, out CodexQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        string[] lines = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 ||
                line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 ||
                line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            Dictionary<string, object> root;
            try
            {
                root = serializer.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                continue;
            }

            if (root == null ||
                !string.Equals(GetQuotaString(root, "type"), "event_msg", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> payload = GetQuotaObject(root, "payload");
            if (payload == null ||
                !string.Equals(GetQuotaString(payload, "type"), "token_count", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> rateLimits = GetQuotaObject(payload, "rate_limits");
            CodexQuotaSnapshot snapshot;
            if (rateLimits == null || !TryBuildQuotaSnapshot(rateLimits, out snapshot))
            {
                continue;
            }

            DateTime updatedLocal;
            DateTime updatedUtc = SafeGetLastWriteTimeUtc(path);
            if (TryGetQuotaDate(root, "timestamp", out updatedLocal))
            {
                updatedUtc = updatedLocal.ToUniversalTime();
            }
            else if (updatedUtc == DateTime.MinValue)
            {
                updatedUtc = DateTime.UtcNow;
            }

            quotaEvent = new CodexQuotaEvent();
            quotaEvent.Snapshot = snapshot;
            quotaEvent.UpdatedUtc = updatedUtc;
            return true;
        }

        return false;
    }

    private static bool TryBuildQuotaSnapshot(Dictionary<string, object> rateLimits, out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.CreateDefault();
        bool found = false;
        found = ApplyQuotaSlot(rateLimits, "primary", snapshot) || found;
        found = ApplyQuotaSlot(rateLimits, "secondary", snapshot) || found;
        return found;
    }

    private static bool ApplyQuotaSlot(Dictionary<string, object> rateLimits, string key, CodexQuotaSnapshot snapshot)
    {
        Dictionary<string, object> slot = GetQuotaObject(rateLimits, key);
        if (slot == null)
        {
            return false;
        }

        double usedPercent;
        if (!TryGetQuotaNumber(slot, "used_percent", out usedPercent) &&
            !TryGetQuotaNumber(slot, "used_percentage", out usedPercent))
        {
            return false;
        }

        double windowMinutes;
        bool hasWindowMinutes = TryGetQuotaNumber(slot, "window_minutes", out windowMinutes);
        bool isFiveHour = string.Equals(key, "primary", StringComparison.OrdinalIgnoreCase);
        if (hasWindowMinutes)
        {
            isFiveHour = windowMinutes <= 300.0;
        }

        int remainingPercent = ClampPercent((int)Math.Round(100.0 - usedPercent));
        DateTime resetLocal;
        bool hasReset = TryGetQuotaDate(slot, "resets_at", out resetLocal);
        if (isFiveHour)
        {
            snapshot.FiveHourPercent = remainingPercent;
            if (hasReset)
            {
                snapshot.FiveHourResetLocal = resetLocal;
                snapshot.FiveHourResetKnown = true;
            }
        }
        else
        {
            snapshot.WeeklyPercent = remainingPercent;
            if (hasReset)
            {
                snapshot.WeeklyResetLocal = resetLocal;
                snapshot.WeeklyResetKnown = true;
            }
        }

        return true;
    }

    private static bool TryReadQuotaIniSnapshot(out CodexQuotaSnapshot snapshot)
    {
        snapshot = CodexQuotaSnapshot.CreateDefault();
        string path = Path.Combine(Logger.DirectoryPath, "quota.ini");
        if (!File.Exists(path))
        {
            return false;
        }

        bool found = false;
        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                int percent;
                DateTime dateTime;
                if (string.Equals(key, "FiveHourPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.FiveHourPercent = ClampPercent(percent);
                    found = true;
                }
                else if (string.Equals(key, "WeeklyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.WeeklyPercent = ClampPercent(percent);
                    found = true;
                }
                else if (string.Equals(key, "FiveHourReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.FiveHourResetLocal = dateTime;
                    snapshot.FiveHourResetKnown = true;
                    found = true;
                }
                else if (string.Equals(key, "WeeklyReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.WeeklyResetLocal = dateTime;
                    snapshot.WeeklyResetKnown = true;
                    found = true;
                }
                else if (string.Equals(key, "SourceUpdatedUtc", StringComparison.OrdinalIgnoreCase) &&
                    DateTime.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out dateTime))
                {
                    snapshot.SourceUpdatedUtc = dateTime.ToUniversalTime();
                    snapshot.SourceUpdatedKnown = true;
                    found = true;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        if (found)
        {
            if (!snapshot.SourceUpdatedKnown)
            {
                snapshot.SourceUpdatedUtc = SafeGetLastWriteTimeUtc(path);
                snapshot.SourceUpdatedKnown = snapshot.SourceUpdatedUtc != DateTime.MinValue;
            }
        }

        return found;
    }

    private static void TryWriteQuotaIniSnapshot(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        try
        {
            string path = Path.Combine(Logger.DirectoryPath, "quota.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            List<string> lines = new List<string>();
            lines.Add("Version=1");
            lines.Add("FiveHourPercent=" + ClampPercent(snapshot.FiveHourPercent).ToString(CultureInfo.InvariantCulture));
            lines.Add("WeeklyPercent=" + ClampPercent(snapshot.WeeklyPercent).ToString(CultureInfo.InvariantCulture));
            if (snapshot.FiveHourResetKnown)
            {
                lines.Add("FiveHourReset=" + snapshot.FiveHourResetLocal.ToString("o", CultureInfo.InvariantCulture));
            }

            if (snapshot.WeeklyResetKnown)
            {
                lines.Add("WeeklyReset=" + snapshot.WeeklyResetLocal.ToString("o", CultureInfo.InvariantCulture));
            }

            if (snapshot.SourceUpdatedKnown)
            {
                lines.Add("SourceUpdatedUtc=" + snapshot.SourceUpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
            }

            string next = string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), next, StringComparison.Ordinal))
            {
                return;
            }

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, next, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static Dictionary<string, object> GetQuotaObject(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value))
        {
            return null;
        }

        return value as Dictionary<string, object>;
    }

    private static Dictionary<string, object> GetFirstQuotaObjectFromArray(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return null;
        }

        object[] array = value as object[];
        if (array != null)
        {
            for (int i = 0; i < array.Length; i++)
            {
                Dictionary<string, object> item = array[i] as Dictionary<string, object>;
                if (item != null)
                {
                    return item;
                }
            }
        }

        System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null || value is string)
        {
            return null;
        }

        foreach (object entry in enumerable)
        {
            Dictionary<string, object> item = entry as Dictionary<string, object>;
            if (item != null)
            {
                return item;
            }
        }

        return null;
    }

    private static List<Dictionary<string, object>> GetQuotaObjectsFromArray(
        Dictionary<string, object> values,
        string key)
    {
        List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return result;
        }

        System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
        if (enumerable == null || value is string)
        {
            return result;
        }

        foreach (object entry in enumerable)
        {
            Dictionary<string, object> item = entry as Dictionary<string, object>;
            if (item != null)
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static string GetQuotaString(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TryGetJsonBool(Dictionary<string, object> values, string key, out bool result)
    {
        result = false;
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return false;
        }

        if (value is bool)
        {
            result = (bool)value;
            return true;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return bool.TryParse(text, out result);
    }

    private static bool TryGetQuotaNumber(Dictionary<string, object> values, string key, out double number)
    {
        number = 0.0;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaNumber(value, out number);
    }

    private static bool TryReadQuotaNumber(object value, out double number)
    {
        number = 0.0;
        if (value == null)
        {
            return false;
        }

        string text = value as string;
        if (text != null)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            number = 0.0;
            return false;
        }
    }

    private static bool TryGetQuotaDate(Dictionary<string, object> values, string key, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaDate(value, out localDate);
    }

    private static bool TryGetCodexModelIqDataWindow(
        Dictionary<string, object> values,
        string key,
        out DateTime localDate,
        out int windowStartHour)
    {
        localDate = DateTime.MinValue;
        windowStartHour = 0;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadCodexModelIqDataWindow(value, out localDate, out windowStartHour);
    }

    private static bool TryReadCodexModelIqDataWindow(
        object value,
        out DateTime localDate,
        out int windowStartHour)
    {
        localDate = DateTime.MinValue;
        windowStartHour = 0;
        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(text))
        {
            Match match = Regex.Match(
                text.Trim(),
                "^(\\d{4}-\\d{2}-\\d{2})(?:[-_\\s]*(am|pm))?$",
                RegexOptions.IgnoreCase);
            DateTime date;
            if (match.Success &&
                DateTime.TryParseExact(
                    match.Groups[1].Value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date))
            {
                localDate = date.Date;
                windowStartHour = string.Equals(match.Groups[2].Value, "pm", StringComparison.OrdinalIgnoreCase)
                    ? 12
                    : 0;
                return true;
            }
        }

        if (TryReadQuotaDate(value, out localDate))
        {
            localDate = localDate.Date;
            windowStartHour = 0;
            return true;
        }

        return false;
    }

    private static bool TryReadQuotaDate(object value, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        double seconds;
        if (TryReadQuotaNumber(value, out seconds))
        {
            if (seconds > 10000000000.0)
            {
                seconds /= 1000.0;
            }

            try
            {
                DateTimeOffset epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
                localDate = epoch.AddSeconds(seconds).LocalDateTime;
                return true;
            }
            catch
            {
                localDate = DateTime.MinValue;
                return false;
            }
        }

        string text = value as string;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        DateTimeOffset offsetDate;
        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out offsetDate))
        {
            localDate = offsetDate.LocalDateTime;
            return true;
        }

        DateTime dateTime;
        if (DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out dateTime))
        {
            localDate = dateTime.ToLocalTime();
            return true;
        }

        return false;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static long SafeGetFileLength(string path)
    {
        try
        {
            FileInfo info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private static int ClampEfficiencyPercent(int value)
    {
        return Math.Max(0, Math.Min(999, value));
    }

    private static Color GetQuotaColor(int percent)
    {
        if (percent >= 80)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 235);
        }

        if (percent >= 30)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.WarningSoft, 238);
        }

        if (percent <= 5)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaDanger, 238);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238);
    }

    private static Color GetQuotaConsumptionRingColor()
    {
        return DesignTokens.WithAlpha(GetCodexRadarLightGreen(), 242);
    }

    internal static void RunStatusAndQuotaSelfTest()
    {
        Dictionary<string, object> operationalComponent = new Dictionary<string, object>();
        operationalComponent["name"] = "Codex API";
        operationalComponent["status"] = "operational";
        Dictionary<string, object> root = new Dictionary<string, object>();
        root["components"] = new object[] { operationalComponent };
        if (GetOpenAiCodexComponentState(root, "minor") != CodexConnectionStageState.Passed)
        {
            throw new InvalidOperationException("Unrelated global minor status colored the Codex component.");
        }

        operationalComponent["status"] = "degraded_performance";
        if (GetOpenAiCodexComponentState(root, "minor") != CodexConnectionStageState.Warning)
        {
            throw new InvalidOperationException("Codex component degradation was not reported.");
        }

        int baseline = GetNextFiveHourConsumptionRingBaseline(-1, 67, 57);
        if (baseline != 67)
        {
            throw new InvalidOperationException("Five-hour consumption decrease baseline failed.");
        }

        baseline = GetNextFiveHourConsumptionRingBaseline(baseline, 57, 57);
        if (baseline != 67)
        {
            throw new InvalidOperationException("Equal five-hour balances cleared the consumption ring.");
        }

        baseline = GetNextFiveHourConsumptionRingBaseline(baseline, 57, 72);
        if (baseline != -1)
        {
            throw new InvalidOperationException("Five-hour reset/increase did not clear the old baseline.");
        }
    }

    // Legacy power/thermal UI is retained only as reference; PowerThermalForm owns that workload.
#if false
    private void DrawThermalAlerts(Graphics g, RectangleF bounds, List<ThermalReading> alerts)
    {
        if (alerts == null || alerts.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        int total = alerts.Count;
        int visibleSensors = Math.Min(3, total);
        bool hasMore = total > 3;
        if (visibleSensors <= 0)
        {
            return;
        }

        float gap = S(6);
        float chipHeight = Math.Max(S(16), bounds.Height - S(2));
        float chipTop = bounds.Top + Math.Max(0.0f, (bounds.Height - chipHeight) / 2.0f);

        using (Font chipFont = DesignTokens.CreateUIFont(Math.Max(8.0f, 9.5f * this.scale), FontStyle.Bold, GraphicsUnit.Pixel))
        {
            float moreWidth = 0.0f;
            if (hasMore)
            {
                string moreText = "+" + (total - visibleSensors).ToString();
                moreWidth = Math.Max(S(30), g.MeasureString(moreText, chipFont).Width + S(18));
                moreWidth = Math.Min(moreWidth, bounds.Width * 0.28f);
                RectangleF moreRect = new RectangleF(bounds.Right - moreWidth, chipTop, moreWidth, chipHeight);
                double hiddenMaxTemp = 0.0;
                for (int i = visibleSensors; i < total; i++)
                {
                    hiddenMaxTemp = Math.Max(hiddenMaxTemp, alerts[i].Celsius);
                }

                DrawThermalChip(g, moreRect, moreText, hiddenMaxTemp, false, chipFont);
            }

            float sensorAreaRight = hasMore ? bounds.Right - moreWidth - gap : bounds.Right;
            float sensorAreaWidth = Math.Max(S(30), sensorAreaRight - bounds.Left);
            float slotWidth = Math.Max(S(30), (sensorAreaWidth - gap * 2.0f) / 3.0f);
            float x = bounds.Left;
            for (int i = 0; i < visibleSensors; i++)
            {
                string text = FormatThermalSensorName(alerts[i].Name);
                float desiredWidth = g.MeasureString(text, chipFont).Width + S(alerts[i].CriticalActive ? 32 : 20);
                float width = Math.Min(slotWidth, Math.Max(S(30), desiredWidth));
                RectangleF chipRect = new RectangleF(x, chipTop, width, chipHeight);
                DrawThermalChip(g, chipRect, text, alerts[i].Celsius, alerts[i].CriticalActive, chipFont);
                x += slotWidth + gap;
            }
        }
    }

    private void DrawThermalChip(Graphics g, RectangleF rect, string text, double celsius, bool criticalActive, Font font)
    {
        float radius = Math.Min(rect.Height / 2.0f, S(11));
        int redAlpha = GetThermalRedAlpha(celsius);
        using (GraphicsPath path = RoundedRectangle(rect, radius))
        using (SolidBrush baseBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.ThermalChipSurface, 160)))
        using (SolidBrush redBrush = new SolidBrush(DesignTokens.DangerStrong(redAlpha)))
        using (Pen border = new Pen(DesignTokens.White(45), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(baseBrush, path);
            g.FillPath(redBrush, path);
            g.DrawPath(border, path);
        }

        RectangleF textRect = rect;
        if (criticalActive)
        {
            float iconSize = Math.Max(S(12), Math.Min(rect.Height * 0.70f, S(17)));
            RectangleF iconRect = new RectangleF(rect.Right - iconSize - S(7), rect.Top + (rect.Height - iconSize) / 2.0f, iconSize, iconSize);
            DrawSmallWarningIcon(g, iconRect);
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - iconSize - S(18)), rect.Height);
        }
        else
        {
            textRect = new RectangleF(rect.Left + S(8), rect.Top, Math.Max(4, rect.Width - S(16)), rect.Height);
        }

        using (SolidBrush textBrush = new SolidBrush(DesignTokens.Colors.TextStrong))
        {
            DrawCodexRadarFittedText(g, text, font, textBrush, textRect, StringAlignment.Near);
        }
    }

    private void DrawSmallWarningIcon(Graphics g, RectangleF rect)
    {
        int warningAlpha = (this.renderTickCount % 2 == 0) ? 77 : 179;
        float centerX = rect.Left + rect.Width / 2.0f;
        float centerY = rect.Top + rect.Height / 2.0f;
        float size = Math.Min(rect.Width, rect.Height);
        PointF[] triangle = new PointF[]
        {
            new PointF(centerX, centerY - size * 0.46f),
            new PointF(centerX - size * 0.48f, centerY + size * 0.42f),
            new PointF(centerX + size * 0.48f, centerY + size * 0.42f)
        };

        using (Pen pen = new Pen(DesignTokens.Warning(warningAlpha), Math.Max(1.0f, 2.0f * this.scale)))
        {
            pen.LineJoin = LineJoin.Round;
            g.DrawPolygon(pen, triangle);
        }

        using (Font markFont = DesignTokens.CreateUIFont(Math.Max(7.0f, size * 0.66f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush markBrush = new SolidBrush(DesignTokens.Warning(warningAlpha)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString("!", markFont, markBrush, rect, format);
        }
    }

    private static int GetThermalRedAlpha(double celsius)
    {
        double progress = (celsius - 70.0) / 30.0;
        if (progress < 0.0)
        {
            progress = 0.0;
        }
        else if (progress > 1.0)
        {
            progress = 1.0;
        }

        double alpha = 0.30 + progress * (0.85 - 0.30);
        return (int)Math.Round(alpha * 255.0);
    }

    private static string FormatThermalSensorName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "TZ";
        }

        return name.Trim();
    }

#endif

    private void DrawCodexRadarFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment)
    {
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = alignment;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;

            Font drawFont = baseFont;
            bool disposeFont = false;
            float size = baseFont.Size;
            while (size > 8.0f * this.scale && g.MeasureString(text, drawFont).Width > rect.Width)
            {
                if (disposeFont)
                {
                    drawFont.Dispose();
                }

                size -= 0.8f * this.scale;
                drawFont = new Font(baseFont.FontFamily, size, baseFont.Style, GraphicsUnit.Pixel);
                disposeFont = true;
            }

            g.DrawString(text, drawFont, brush, rect, format);

            if (disposeFont)
            {
                drawFont.Dispose();
            }
        }
    }

#if false
    private PowerReading GetPowerReading()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedPowerReadingUtc).TotalSeconds < 2.0)
        {
            return this.cachedPowerReading;
        }

        this.cachedPowerReading = ReadPowerReading();
        this.cachedPowerReadingUtc = now;
        return this.cachedPowerReading;
    }

    private List<ThermalReading> GetThermalAlerts()
    {
        DateTime now = DateTime.UtcNow;
        if (this.currentSettings.ThermalTestMode != ThermalTestMode.Off)
        {
            List<ThermalReading> simulated = BuildSimulatedThermalReadings(this.currentSettings.ThermalTestMode);
            UpdateThermalCriticalStates(simulated, now, true);
            simulated.Sort(CompareThermalReading);
            return simulated;
        }

        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
            UpdateThermalCriticalStates(this.cachedThermalReadings, now, false);
        }

        List<ThermalReading> alerts = new List<ThermalReading>();
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            if (this.cachedThermalReadings[i].Celsius >= 70.0)
            {
                alerts.Add(this.cachedThermalReadings[i]);
            }
        }

        alerts.Sort(CompareThermalReading);
        return alerts;
    }

    private void UpdateThermalCriticalStates(List<ThermalReading> readings, DateTime now, bool instantCritical)
    {
        if (readings == null)
        {
            return;
        }

        HashSet<string> activeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < readings.Count; i++)
        {
            ThermalReading reading = readings[i];
            if (reading == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(reading.Name))
            {
                continue;
            }

            activeNames.Add(reading.Name);
            if (reading.Celsius >= 95.0)
            {
                DateTime since;
                if (!this.thermalCriticalSinceUtc.TryGetValue(reading.Name, out since))
                {
                    since = instantCritical ? now.AddSeconds(-3.0) : now;
                    this.thermalCriticalSinceUtc[reading.Name] = since;
                }

                reading.CriticalActive = (now - since).TotalSeconds >= 3.0;
            }
            else
            {
                this.thermalCriticalSinceUtc.Remove(reading.Name);
                reading.CriticalActive = false;
            }
        }

        List<string> stale = new List<string>();
        foreach (string name in this.thermalCriticalSinceUtc.Keys)
        {
            if (!activeNames.Contains(name))
            {
                stale.Add(name);
            }
        }

        for (int i = 0; i < stale.Count; i++)
        {
            this.thermalCriticalSinceUtc.Remove(stale[i]);
        }
    }

    private static int CompareThermalReading(ThermalReading left, ThermalReading right)
    {
        int value = right.Celsius.CompareTo(left.Celsius);
        if (value != 0)
        {
            return value;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static List<ThermalReading> ReadThermalReadings()
    {
        List<ThermalReading> readings = new List<ThermalReading>();
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("root\\cimv2", "SELECT Name, Temperature, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    string name = Convert.ToString(GetManagementValue(item, "Name"));
                    double celsius = ConvertThermalZoneCelsius(
                        GetManagementValue(item, "Temperature"),
                        GetManagementValue(item, "HighPrecisionTemperature"));
                    if (string.IsNullOrEmpty(name) || celsius <= 0.0)
                    {
                        continue;
                    }

                    readings.Add(new ThermalReading
                    {
                        Name = name.Trim(),
                        Celsius = celsius,
                        CriticalActive = false
                    });
                }
            }
        }
        catch
        {
        }

        return readings;
    }

    private List<ThermalReading> BuildSimulatedThermalReadings(ThermalTestMode mode)
    {
        double celsius = mode == ThermalTestMode.Simulate100 ? 100.0 : 75.0;
        DateTime now = DateTime.UtcNow;
        if ((now - this.cachedThermalReadingsUtc).TotalSeconds >= 2.0 || this.cachedThermalReadings.Count == 0)
        {
            this.cachedThermalReadings = ReadThermalReadings();
            if (this.cachedThermalReadings == null)
            {
                this.cachedThermalReadings = new List<ThermalReading>();
            }

            this.cachedThermalReadingsUtc = now;
        }

        List<ThermalReading> readings = new List<ThermalReading>();
        HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < this.cachedThermalReadings.Count; i++)
        {
            string name = this.cachedThermalReadings[i].Name;
            if (string.IsNullOrEmpty(name) || !usedNames.Add(name))
            {
                continue;
            }

            readings.Add(new ThermalReading
            {
                Name = name,
                Celsius = celsius,
                CriticalActive = false
            });
        }

        if (readings.Count > 0)
        {
            return readings;
        }

        for (int i = 0; i < 6; i++)
        {
            readings.Add(new ThermalReading
            {
                Name = @"\_SB.TZ" + i.ToString(),
                Celsius = celsius,
                CriticalActive = false
            });
        }

        return readings;
    }

    private static double ConvertThermalZoneCelsius(object temperature, object highPrecisionTemperature)
    {
        double highPrecision = ToPositiveDouble(highPrecisionTemperature);
        if (highPrecision > 0.0)
        {
            return highPrecision / 10.0 - 273.15;
        }

        double standard = ToPositiveDouble(temperature);
        if (standard > 0.0)
        {
            return standard - 273.15;
        }

        return 0.0;
    }

    private static PowerReading ReadPowerReading()
    {
        PowerReading reading = new PowerReading();
        try
        {
            PowerLineStatus lineStatus = SystemInformation.PowerStatus.PowerLineStatus;
            if (lineStatus != PowerLineStatus.Unknown)
            {
                reading.StatusKnown = true;
                reading.IsCharging = lineStatus == PowerLineStatus.Online;
            }
        }
        catch
        {
        }

        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM BatteryStatus"))
            using (ManagementObjectCollection collection = searcher.Get())
            {
                foreach (ManagementObject item in collection)
                {
                    double chargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "ChargeRate"));
                    double dischargeMilliwatts = ToPositiveMilliwatts(GetManagementValue(item, "DischargeRate"));
                    object charging = GetManagementValue(item, "Charging");
                    object discharging = GetManagementValue(item, "Discharging");
                    object powerOnline = GetManagementValue(item, "PowerOnline");

                    if (chargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = true;
                        reading.WattsKnown = true;
                        reading.Watts = chargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (dischargeMilliwatts > 0)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                        reading.WattsKnown = true;
                        reading.Watts = dischargeMilliwatts / 1000.0;
                        return reading;
                    }

                    if (charging != null)
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = Convert.ToBoolean(charging);
                    }

                    if (discharging != null && Convert.ToBoolean(discharging))
                    {
                        reading.StatusKnown = true;
                        reading.IsCharging = false;
                    }

                    if (powerOnline != null)
                    {
                        reading.StatusKnown = true;
                        if (!Convert.ToBoolean(powerOnline))
                        {
                            reading.IsCharging = false;
                        }
                    }

                    return reading;
                }
            }
        }
        catch
        {
        }

        return reading;
    }

    private static object GetManagementValue(ManagementBaseObject item, string name)
    {
        try
        {
            PropertyData property = item.Properties[name];
            return property == null ? null : property.Value;
        }
        catch
        {
            return null;
        }
    }

    private static double ToPositiveDouble(object value)
    {
        if (value == null)
        {
            return 0.0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            return number > 0.0 ? number : 0.0;
        }
        catch
        {
            return 0.0;
        }
    }

    private static double ToPositiveMilliwatts(object value)
    {
        if (value == null)
        {
            return 0;
        }

        try
        {
            double number = Convert.ToDouble(value);
            if (number <= 0 || number >= 4294967294.0)
            {
                return 0;
            }

            return number;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatWatts(double watts)
    {
        if (watts >= 100.0)
        {
            return watts.ToString("0") + " W";
        }

        return watts.ToString("0.0") + " W";
    }

#endif

    private static string GetOrdinalSuffix(int day)
    {
        int lastTwo = day % 100;
        if (lastTwo >= 11 && lastTwo <= 13)
        {
            return "th";
        }

        switch (day % 10)
        {
            case 1:
                return "st";
            case 2:
                return "nd";
            case 3:
                return "rd";
            default:
                return "th";
        }
    }

    private void RenderLayeredWindow()
    {
        RenderLayeredWindow(true);
    }

    private void RenderLayeredWindow(bool redrawContent)
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            EnsureRenderBuffer();
            // Hover opacity can reuse the previous content and update only the layered-window alpha.
            bool burnInColorProtectionActive = IsBurnInColorProtectionActive();
            bool refreshNativeBitmap =
                redrawContent ||
                !this.renderBufferValid ||
                burnInColorProtectionActive != this.lastRenderedBurnInColorProtectionActive;
            if (refreshNativeBitmap)
            {
                this.renderGraphics.Clear(Color.Transparent);
                DrawCodexRadarBackground(this.renderGraphics);
                DrawCodexRadarContentLayer(this.renderGraphics);
                if (burnInColorProtectionActive)
                {
                    BurnInProtection.ApplyHiddenModeColorProtection(this.renderBitmap);
                }

                this.lastRenderedBurnInColorProtectionActive = burnInColorProtectionActive;
                this.renderBufferValid = true;
                this.lastRenderedClockSecondLocal = TruncateToSecond(DateTime.Now);
            }

            if (!this.layeredSurface.Update(
                this.Handle,
                this.Location,
                this.renderBitmap,
                GetApplicationOpacityAlpha(),
                refreshNativeBitmap))
            {
                if (!this.layeredUpdateFailureLogged)
                {
                    this.layeredUpdateFailureLogged = true;
                    Program.LogInfo("CodexRadar UpdateLayeredWindow failed; falling back to normal paint.");
                }

                this.Invalidate();
            }
        }
        catch (Exception ex)
        {
            if (!this.layeredUpdateFailureLogged)
            {
                this.layeredUpdateFailureLogged = true;
                Program.LogException(ex);
            }
        }
    }

    private void EnsureRenderBuffer()
    {
        if (this.renderBitmap != null &&
            this.renderGraphics != null &&
            this.renderBitmap.Width == this.Width &&
            this.renderBitmap.Height == this.Height)
        {
            return;
        }

        DisposeRenderBuffer();
        this.renderBitmap = new Bitmap(this.Width, this.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        this.renderGraphics = Graphics.FromImage(this.renderBitmap);
        this.renderBufferValid = false;
    }

    private bool IsBurnInColorProtectionActive()
    {
        return BurnInProtection.ShouldApplyHiddenModeColorProtection(
            this.currentSettings,
            IsHoverOpacityTargetActive());
    }

    private void DisposeRenderBuffer()
    {
        if (this.renderGraphics != null)
        {
            this.renderGraphics.Dispose();
            this.renderGraphics = null;
        }

        if (this.renderBitmap != null)
        {
            this.renderBitmap.Dispose();
            this.renderBitmap = null;
        }

        this.renderBufferValid = false;
    }

    private void ResetDisplayRenderResources()
    {
        DisposeRenderBuffer();
        this.layeredSurface.Reset();
        this.layeredUpdateFailureLogged = false;
    }

    private static DateTime TruncateToSecond(DateTime value)
    {
        return new DateTime(
            value.Year,
            value.Month,
            value.Day,
            value.Hour,
            value.Minute,
            value.Second,
            value.Kind);
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.CodexRadarTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private byte GetApplicationOpacityAlpha()
    {
        return (byte)ApplyHoverTransparencyTarget(255);
    }

    private int ApplyHoverTransparencyTarget(int alpha)
    {
        if (!IsHoverOpacityRuntimeEnabled() || this.hoverOpacityProgress <= 0.0)
        {
            return alpha;
        }

        int hoverAlpha = (int)Math.Round(255.0 * 0.05);
        if (alpha <= hoverAlpha)
        {
            return alpha;
        }

        double animated = alpha + (hoverAlpha - alpha) * this.hoverOpacityProgress;
        return Math.Max(0, Math.Min(255, (int)Math.Round(animated)));
    }

    private int S(int value)
    {
        return (int)Math.Round(value * this.scale);
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
