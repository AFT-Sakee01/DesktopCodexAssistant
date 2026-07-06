using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class ClaudeRadarForm : Form
{
    private const int NormalRefreshMinutes = 60;
    private const int FailureRefreshMinutes = 10;
    private const int RandomRefreshMs = 1000;
    private const int MaxSceneCacheEntries = 6;
    private const int MaxModelIqScore = 200;
    private static readonly Color ClaudeOrange = Color.FromArgb(255, 154, 82);
    private static readonly Color ClaudeOrangeMuted = Color.FromArgb(190, 103, 54);
    private readonly System.Windows.Forms.Timer timer;
    private readonly NativeMethods.LayeredBitmapSurface layeredSurface = new NativeMethods.LayeredBitmapSurface();
    private readonly UiFontCache fontCache = new UiFontCache();
    private readonly Dictionary<string, Bitmap> renderSceneBitmapCache = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
    private readonly Queue<string> renderSceneBitmapCacheOrder = new Queue<string>();
    private readonly object claudeCodeUsageLock = new object();
    private readonly Action<string, string, ToolTipIcon> notificationAction;
    private readonly Dictionary<string, string> notificationState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private WidgetSettings currentSettings;
    private ClaudeRadarSnapshot snapshot;
    private SoftwareRuntimePresenceSnapshot runtimePresenceSnapshot = SoftwareRuntimePresenceSnapshot.Empty();
    private float scale;
    private int renderTickCount;
    private bool hiddenForFullscreen;
    private bool displaySuspended;
    private bool requestRunning;
    private bool claudeCodeUsageRequestRunning;
    private bool layeredUpdateFailureLogged;
    private bool renderBufferValid;
    private bool lastRenderedBurnInColorProtectionActive;
    private int lastRandomRefreshToken;
    private long burnInShiftSlot = long.MinValue;
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private DateTime nextClaudeCodeUsageRefreshUtc = DateTime.MinValue;
    private string claudeCodeUsageRefreshTrigger = "首次刷新";
    private Bitmap renderBitmap;
    private Graphics renderGraphics;

    private sealed class ClaudeCodeUsageRefreshOutcome
    {
        public ClaudeCodeUsageReadResult Result { get; set; }
        public long ElapsedMilliseconds { get; set; }
    }

    public ClaudeRadarForm(WidgetSettings settings)
        : this(settings, null)
    {
    }

    public ClaudeRadarForm(WidgetSettings settings, Action<string, string, ToolTipIcon> notificationAction)
    {
        this.notificationAction = notificationAction;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.snapshot = ClaudeRadarReader.LoadCache(this.currentSettings.ClaudeRadarModelKey) ??
            ClaudeRadarSnapshot.CreateDefault();
        RefreshRuntimePresenceSnapshot(true);
        LoadNotificationState();
        ApplicationIcon.ApplyTo(this);

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
        this.MaximumSize = new Size(WidgetSettings.MaxCodexRadarWidth, WidgetSettings.MaxCodexRadarHeight);
        this.Size = GetDesiredSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = GetTimerIntervalMs();
        this.timer.Tick += OnTimerTick;
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
        this.timer.Start();
        ForceRefresh("启动");
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        DisposeRenderBuffer();
        DisposeSceneCache();
        this.fontCache.Dispose();
        this.layeredSurface.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        DisposeRenderBuffer();
        DisposeSceneCache();
        this.fontCache.Dispose();
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(DesignTokens.Radius.Panel)))
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
        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionClaudeRadarWindow();
        }
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        WidgetSettings next = settings.Clone();
        next.Normalize();
        bool modelChanged = !string.Equals(
            this.currentSettings.ClaudeRadarModelKey,
            next.ClaudeRadarModelKey,
            StringComparison.OrdinalIgnoreCase);
        bool dataSourceChanged =
            this.currentSettings.ClaudeRadarJsonEnabled != next.ClaudeRadarJsonEnabled ||
            this.currentSettings.ClaudeRadarHomepageFallbackEnabled != next.ClaudeRadarHomepageFallbackEnabled ||
            this.currentSettings.ClaudeRadarCommunityRatingsEnabled != next.ClaudeRadarCommunityRatingsEnabled ||
            this.currentSettings.ClaudeRadarLocalQuotaFallbackEnabled != next.ClaudeRadarLocalQuotaFallbackEnabled ||
            this.currentSettings.ClaudeRadarServiceProbeToken != next.ClaudeRadarServiceProbeToken;

        this.currentSettings = next;
        this.timer.Interval = GetTimerIntervalMs();
        if (RefreshRuntimePresenceSnapshot(false))
        {
            this.renderBufferValid = false;
        }

        Size desired = GetDesiredSize();
        if (this.Size != desired)
        {
            this.Size = desired;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        if (!this.currentSettings.ClaudeRadarEnabled || this.hiddenForFullscreen)
        {
            if (this.Visible)
            {
                this.Hide();
            }
            return;
        }

        if (!this.Visible && this.IsHandleCreated)
        {
            this.Show();
        }

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

        PositionClaudeRadarWindow();
        if (modelChanged || dataSourceChanged)
        {
            this.snapshot = ClaudeRadarReader.LoadCache(this.currentSettings.ClaudeRadarModelKey) ??
                ClaudeRadarSnapshot.CreateDefault();
            ForceRefresh(modelChanged ? "模型切换" : "数据源切换");
        }

        RenderLayeredWindow();
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
            this.Hide();
            return;
        }

        if (this.currentSettings.ClaudeRadarEnabled)
        {
            this.Show();
            PositionClaudeRadarWindow();
            RenderLayeredWindow();
        }
    }

    public void SetSharedInteractionPolling(bool enabled)
    {
        // Kept for WidgetForm parity. Claude Radar currently uses the host hover policy only.
    }

    public void ProcessSharedInteractionTick()
    {
        RenderLayeredWindow(false);
    }

    public void PrepareForDisplaySuspend()
    {
        this.displaySuspended = true;
        DisposeRenderBuffer();
        DisposeSceneCache();
        this.layeredSurface.Reset();
        this.layeredUpdateFailureLogged = false;
    }

    public void RecoverAfterDisplayResume()
    {
        this.displaySuspended = false;
        PositionClaudeRadarWindow();
        RenderLayeredWindow();
    }

    public void ForceRefresh(string trigger)
    {
        this.nextRefreshUtc = DateTime.MinValue;
        if (RefreshRuntimePresenceSnapshot(false))
        {
            this.renderBufferValid = false;
        }

        RequestClaudeCodeUsageRefresh(trigger);
        StartRefreshIfDue(trigger);
        RefreshClaudeCodeUsageIfDue(trigger);
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        this.renderTickCount++;
        if (this.currentSettings == null ||
            !this.currentSettings.ClaudeRadarEnabled ||
            this.hiddenForFullscreen ||
            this.displaySuspended)
        {
            return;
        }

        if (BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot))
        {
            PositionClaudeRadarWindow();
        }

        if (RefreshRuntimePresenceSnapshot(false))
        {
            this.renderBufferValid = false;
        }

        StartRefreshIfDue("定时");
        RefreshClaudeCodeUsageIfDue("定时");
        RenderLayeredWindow(false);
    }

    private void StartRefreshIfDue(string trigger)
    {
        if (!this.currentSettings.ClaudeRadarEnabled ||
            this.hiddenForFullscreen ||
            this.displaySuspended)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (this.currentSettings.ClaudeRadarRandomTestEnabled)
        {
            if (this.currentSettings.ClaudeRadarRandomTestRefreshToken != this.lastRandomRefreshToken ||
                (this.currentSettings.ClaudeRadarRandomTestAutoRefresh && nowUtc >= this.nextRefreshUtc))
            {
                this.lastRandomRefreshToken = this.currentSettings.ClaudeRadarRandomTestRefreshToken;
                this.snapshot = ClaudeRadarReader.BuildRandomTestSnapshot(Environment.TickCount);
                this.nextRefreshUtc = nowUtc.AddMilliseconds(RandomRefreshMs);
                this.renderBufferValid = false;
                RenderLayeredWindow();
            }

            return;
        }

        if (nowUtc < this.nextRefreshUtc)
        {
            return;
        }

        if (!TryBeginSingleFlight(ref this.requestRunning))
        {
            return;
        }

        ClaudeRadarSnapshot runningSnapshot = this.snapshot == null
            ? ClaudeRadarSnapshot.CreateDefault()
            : this.snapshot.Clone();
        runningSnapshot.RequestRunning = true;
        this.snapshot = runningSnapshot;
        this.renderBufferValid = false;
        RenderLayeredWindow();

        WidgetSettings requestSettings = this.currentSettings.Clone();
        Task.Run(delegate
        {
            return ClaudeRadarReader.ReadSnapshot(
                requestSettings.ClaudeRadarModelKey,
                requestSettings.ClaudeRadarJsonEnabled,
                requestSettings.ClaudeRadarHomepageFallbackEnabled,
                requestSettings.ClaudeRadarCommunityRatingsEnabled,
                requestSettings.ClaudeRadarLocalQuotaFallbackEnabled);
        }).ContinueWith(delegate(Task<ClaudeRadarSnapshot> task)
        {
            if (this.IsDisposed || !this.IsHandleCreated)
            {
                return;
            }

            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    ApplyRefreshResult(task, trigger);
                });
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private void RequestClaudeCodeUsageRefresh(string trigger)
    {
        lock (this.claudeCodeUsageLock)
        {
            this.nextClaudeCodeUsageRefreshUtc = DateTime.MinValue;
            this.claudeCodeUsageRefreshTrigger = string.IsNullOrWhiteSpace(trigger)
                ? "强制刷新"
                : trigger.Trim();
        }
    }

    private void RefreshClaudeCodeUsageIfDue(string trigger)
    {
        if (this.currentSettings == null ||
            !this.currentSettings.ClaudeRadarEnabled ||
            this.currentSettings.ClaudeRadarRandomTestEnabled ||
            this.hiddenForFullscreen ||
            this.displaySuspended)
        {
            return;
        }

        SoftwareRuntimePresenceSnapshot presence = GetLastRuntimePresenceSnapshot();
        if (!presence.ClaudeRunning)
        {
            return;
        }

        DateTime nowUtc = DateTime.UtcNow;
        string effectiveTrigger;
        lock (this.claudeCodeUsageLock)
        {
            if (this.claudeCodeUsageRequestRunning ||
                (this.nextClaudeCodeUsageRefreshUtc != DateTime.MinValue &&
                 nowUtc < this.nextClaudeCodeUsageRefreshUtc))
            {
                return;
            }

            this.claudeCodeUsageRequestRunning = true;
            effectiveTrigger = string.IsNullOrWhiteSpace(this.claudeCodeUsageRefreshTrigger)
                ? (string.IsNullOrWhiteSpace(trigger) ? "定时间隔" : trigger.Trim())
                : this.claudeCodeUsageRefreshTrigger;
            this.claudeCodeUsageRefreshTrigger = "定时间隔";
        }

        WidgetSettings requestSettings = this.currentSettings.Clone();
        Task.Run(delegate
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            ClaudeCodeUsageReadResult result;
            try
            {
                result = ClaudeCodeUsageReader.Read(requestSettings);
                if (result != null && result.Success && result.Snapshot != null)
                {
                    ClaudeCodeUsageReader.TryWriteQuotaCache(result.Snapshot);
                }
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
                result = BuildClaudeCodeUsageFormError(ClaudeRadarServiceState.Unreachable, "ERROR", "请求失败");
            }

            stopwatch.Stop();
            if (result == null)
            {
                result = BuildClaudeCodeUsageFormError(ClaudeRadarServiceState.Unreachable, "ERROR", "请求失败");
            }

            return new ClaudeCodeUsageRefreshOutcome
            {
                Result = result,
                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
            };
        }).ContinueWith(delegate(Task<ClaudeCodeUsageRefreshOutcome> task)
        {
            if (this.IsDisposed || !this.IsHandleCreated)
            {
                return;
            }

            try
            {
                this.BeginInvoke((MethodInvoker)delegate
                {
                    ApplyClaudeCodeUsageRefreshResult(task, effectiveTrigger);
                });
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private void ApplyClaudeCodeUsageRefreshResult(
        Task<ClaudeCodeUsageRefreshOutcome> task,
        string trigger)
    {
        ClaudeCodeUsageRefreshOutcome outcome = null;
        if (task.Status == TaskStatus.RanToCompletion)
        {
            outcome = task.Result;
        }
        else if (task.Exception != null)
        {
            Program.LogException(task.Exception.GetBaseException());
        }

        ClaudeCodeUsageReadResult result = outcome == null
            ? BuildClaudeCodeUsageFormError(ClaudeRadarServiceState.Unreachable, "ERROR", "请求失败")
            : outcome.Result;
        if (result == null)
        {
            result = BuildClaudeCodeUsageFormError(ClaudeRadarServiceState.Unreachable, "ERROR", "请求失败");
        }

        DateTime nextUtc = DateTime.UtcNow.AddSeconds(
            result.Success
                ? ClaudeCodeUsageReader.NormalRefreshSeconds
                : (result.RateLimited ? ClaudeCodeUsageReader.RateLimitRefreshSeconds : ClaudeCodeUsageReader.ErrorRefreshSeconds));
        lock (this.claudeCodeUsageLock)
        {
            this.claudeCodeUsageRequestRunning = false;
            this.nextClaudeCodeUsageRefreshUtc = nextUtc;
        }

        ClaudeRadarSnapshot current = this.snapshot == null
            ? ClaudeRadarSnapshot.CreateDefault()
            : this.snapshot.Clone();
        current.ClaudeCodeState = result.State;
        if (result.Success && result.Snapshot != null)
        {
            current.Quota = result.Snapshot.ToClaudeRadarQuotaSnapshot();
        }

        this.snapshot = current;
        this.renderBufferValid = false;

        NetworkCheckHistoryLogger.LogCompleted(
            "claude_radar",
            "claude_code_usage",
            string.IsNullOrWhiteSpace(trigger) ? "定时间隔" : trigger,
            result.Success ? "正常" : FallbackText(result.ErrorMessage, result.State.ToString()),
            result.Success,
            (int)Math.Min(int.MaxValue, outcome == null ? 0 : outcome.ElapsedMilliseconds),
            new Dictionary<string, object>
            {
                { "health", result.State.ToString() },
                { "error_code", result.ErrorCode ?? string.Empty },
                { "token_configured", result.TokenConfigured },
                { "rate_limited", result.RateLimited },
                { "quota_known_after", current.Quota != null && current.Quota.Known }
            });

        RenderLayeredWindow();
    }

    private static ClaudeCodeUsageReadResult BuildClaudeCodeUsageFormError(
        ClaudeRadarServiceState state,
        string errorCode,
        string message)
    {
        return new ClaudeCodeUsageReadResult
        {
            TokenConfigured = false,
            Success = false,
            RateLimited = string.Equals(errorCode, "429", StringComparison.OrdinalIgnoreCase),
            Snapshot = null,
            State = state,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = message ?? string.Empty
        };
    }

    private static string FallbackText(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? (fallback ?? string.Empty) : value.Trim();
    }

    private void ApplyRefreshResult(Task<ClaudeRadarSnapshot> task, string trigger)
    {
        CompleteSingleFlight(ref this.requestRunning);
        ClaudeRadarSnapshot next = null;
        if (task.Status == TaskStatus.RanToCompletion)
        {
            next = task.Result;
        }
        else if (task.Exception != null)
        {
            Program.LogException(task.Exception.GetBaseException());
        }

        if (next == null || !next.Known)
        {
            this.snapshot = BuildRefreshFailureDisplaySnapshot(this.snapshot, next);
            this.nextRefreshUtc = DateTime.UtcNow.AddMinutes(FailureRefreshMinutes);
        }
        else
        {
            next.RequestRunning = false;
            PreserveCurrentClaudeCodeQuotaIfNewer(next);
            ShowModelCatalogNotifications(next.ModelCatalogEvents);
            this.snapshot = next.Clone();
            int refreshSeconds = next.Community == null ? 0 : next.Community.RefreshSeconds;
            int successMinutes = Math.Max(15, Math.Min(NormalRefreshMinutes, refreshSeconds <= 0 ? NormalRefreshMinutes : (int)Math.Ceiling(refreshSeconds / 60.0)));
            this.nextRefreshUtc = DateTime.UtcNow.AddMinutes(successMinutes);
        }

        this.renderBufferValid = false;
        Program.LogInfo("Claude Radar refreshed. Trigger=" + (trigger ?? string.Empty) + ", Known=" + (this.snapshot != null && this.snapshot.Known).ToString());
        RenderLayeredWindow();
    }

    private static bool TryBeginSingleFlight(ref bool running)
    {
        if (running)
        {
            return false;
        }

        running = true;
        return true;
    }

    private static void CompleteSingleFlight(ref bool running)
    {
        running = false;
    }

    private static ClaudeRadarSnapshot BuildRefreshFailureDisplaySnapshot(
        ClaudeRadarSnapshot current,
        ClaudeRadarSnapshot failed)
    {
        ClaudeRadarSnapshot fallback = current == null
            ? ClaudeRadarSnapshot.CreateDefault()
            : current.Clone();
        fallback.RequestRunning = false;
        if (failed != null)
        {
            fallback.DataState = failed.DataState;
            fallback.RatingsState = failed.RatingsState;
            fallback.ClaudeStatusState = failed.ClaudeStatusState;
            fallback.ClaudeCodeState = failed.ClaudeCodeState;
            fallback.ErrorCode = failed.ErrorCode;
            fallback.ErrorMessage = failed.ErrorMessage;
            fallback.CheckedAtUtc = failed.CheckedAtUtc;
            fallback.CheckedAtLocal = failed.CheckedAtLocal;
        }

        return fallback;
    }

    private void PreserveCurrentClaudeCodeQuotaIfNewer(ClaudeRadarSnapshot next)
    {
        if (next == null)
        {
            return;
        }

        ClaudeRadarSnapshot current = this.snapshot;
        if (current == null ||
            current.Quota == null ||
            !current.Quota.Known ||
            current.ClaudeCodeState != ClaudeRadarServiceState.Normal)
        {
            return;
        }

        bool shouldPreserve =
            next.Quota == null ||
            next.ClaudeCodeState != ClaudeRadarServiceState.Normal ||
            (current.Quota.UpdatedAtKnown &&
             (next.Quota == null ||
              !next.Quota.UpdatedAtKnown ||
              current.Quota.UpdatedAtUtc > next.Quota.UpdatedAtUtc));
        if (!shouldPreserve)
        {
            return;
        }

        // Website data and Claude Code usage are refreshed independently. A slower
        // website response must not erase a newer personal quota read.
        next.Quota = current.Quota.Clone();
        next.ClaudeCodeState = current.ClaudeCodeState;
    }

    private void PositionClaudeRadarWindow()
    {
        if (this.hiddenForFullscreen || !this.currentSettings.ClaudeRadarEnabled)
        {
            return;
        }

        Rectangle workArea = this.currentSettings.GetWorkAreaForModule(WidgetSettings.ModuleClaudeRadar);
        Size desired = GetDesiredSize();
        if (this.Size != desired)
        {
            this.Size = desired;
        }

        int left = Math.Max(workArea.Left, Math.Min(this.currentSettings.ClaudeRadarLeftX, workArea.Right - this.Width));
        int baseHeight = Math.Max(WidgetSettings.MinCodexRadarHeight, this.currentSettings.ClaudeRadarHeight);
        int top = this.currentSettings.ClaudeRadarBottomY - baseHeight + 1;
        top = Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - this.Height));
        Point shifted = BurnInProtection.ApplyRuntimeOffset(
            new Point(left, top),
            this.Size,
            workArea,
            31);
        this.Location = shifted;

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            shifted.X,
            shifted.Y,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private Size GetDesiredSize()
    {
        return new Size(this.currentSettings.ClaudeRadarWidth, this.currentSettings.ClaudeRadarHeight);
    }

    private int GetTimerIntervalMs()
    {
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
        if (mode == WidgetPerformanceMode.Smooth)
        {
            return 1000;
        }

        if (mode == WidgetPerformanceMode.BatterySaver)
        {
            return 5000;
        }

        return 2500;
    }

    private bool RefreshRuntimePresenceSnapshot(bool force)
    {
        WidgetPerformanceMode mode = this.currentSettings == null
            ? WidgetPerformanceMode.BatterySaver
            : WidgetSettings.GetEffectivePerformanceMode(this.currentSettings.PerformanceMode);
        SoftwareRuntimePresenceSnapshot previous = this.runtimePresenceSnapshot ?? SoftwareRuntimePresenceSnapshot.Empty();
        SoftwareRuntimePresenceSnapshot next = SoftwareRuntimePresence.GetSnapshot(mode, force) ??
            SoftwareRuntimePresenceSnapshot.Empty();
        this.runtimePresenceSnapshot = next;
        return previous.CodexRunning != next.CodexRunning ||
            previous.ClaudeRunning != next.ClaudeRunning;
    }

    private SoftwareRuntimePresenceSnapshot GetLastRuntimePresenceSnapshot()
    {
        return this.runtimePresenceSnapshot ?? SoftwareRuntimePresenceSnapshot.Empty();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawWindow(e.Graphics);
    }

    private void DrawWindow(Graphics g)
    {
        DrawBackground(g);
        DrawContentLayer(g);
    }

    private void DrawBackground(Graphics g)
    {
        ConfigureGraphics(g);
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (SolidBrush background = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.AppBackground, GetBackgroundOpacityAlpha())))
        {
            g.FillPath(background, shell);
        }
    }

    private void DrawContentLayer(Graphics g)
    {
        int alpha = GetContentOpacityAlpha();
        if (alpha <= 0)
        {
            return;
        }

        if (alpha >= 255)
        {
            DrawContent(g);
            return;
        }

        using (Bitmap content = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
        using (Graphics cg = Graphics.FromImage(content))
        {
            cg.Clear(Color.Transparent);
            DrawContent(cg);
            DrawingUtil.DrawImageWithAlpha(g, content, alpha);
        }
    }

    private void DrawContent(Graphics g)
    {
        ConfigureGraphics(g);
        ClaudeRadarSnapshot local = this.snapshot == null ? ClaudeRadarSnapshot.CreateDefault() : this.snapshot.Clone();
        using (GraphicsPath shell = RoundedRectangle(new RectangleF(0, 0, this.Width - 1, this.Height - 1), S(DesignTokens.Radius.Panel)))
        using (Pen outline = new Pen(DesignTokens.White(DesignTokens.Alpha.ShellOutline), Math.Max(1, S(1))))
        {
            g.DrawPath(outline, shell);
        }

        RectangleF bounds = new RectangleF(
            S(8),
            S(3),
            Math.Max(10, this.Width - S(16)),
            Math.Max(10, this.Height - S(6)));

        DrawClaudeRadarModulesEvenRow(g, bounds, local);

        DrawSoftwareBorder(g);
    }

    private void DrawClaudeRadarModulesEvenRow(Graphics g, RectangleF bounds, ClaudeRadarSnapshot local)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // EvenRow v3 - same three-zone grid as CodexRadarForm.EvenRow.cs: [5 rings | radar bar]
        // over one shared info band on the left, content-sized status column on the right. The
        // radar bar shares the rings' compact height so the bottom band can span rings AND radar,
        // buying it a larger never-truncated font. See the Codex version for the full rationale.
        float elementGap = S(5);
        float ringGap = S(4);
        float leftInset = S(2);
        float rightInset = S(2);
        float compactCellHeight = Math.Max(S(32), bounds.Height * (2.0f / 3.0f));
        float ringTextHeight = Math.Max(S(13), compactCellHeight * 0.18f);
        float ringAreaHeight = Math.Max(S(20), compactCellHeight - S(3) - S(2) - ringTextHeight);
        float ringDiameterCap = Math.Max(S(18), ringAreaHeight);
        float radarCellWidth = Math.Max(S(20), Math.Min(S(28), ringDiameterCap * 0.42f));
        // Tightened twice on request: base 10px -> 5px -> 3px per side of the radar bar at 200%.
        float radarGap = elementGap * 0.3f;
        float statusWidth = GetClaudeEvenRowStatusZoneWidth(g, bounds, local);
        // +S(6) slack (was +S(8)): trims each ring cell's empty shoulder.
        float ringCellWidthCap = ringDiameterCap + S(6);
        float ringsAvailable = Math.Max(S(60), bounds.Width - leftInset - rightInset - statusWidth - radarCellWidth - radarGap * 2.0f - ringGap * 4.0f);
        float ringCellWidth = Math.Max(S(20), Math.Min(ringCellWidthCap, ringsAvailable / 5.0f));
        float ringFillFactor = Math.Max(0.6f, Math.Min(0.99f, ringDiameterCap / ringCellWidthCap));

        float x = bounds.Left + leftInset;
        // Every ring cell has identical width/height, so compute the shared divider height once and
        // let the radar bar reuse the rings' exact compact height.
        float dividerY = GetClaudeEvenRowCompactDividerY(
            GetClaudeEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight),
            ringFillFactor);
        RectangleF cellRect;

        ClaudeRadarModelMetric metric = local == null || local.SelectedModel == null
            ? ClaudeRadarModelMetric.CreateDefault()
            : local.SelectedModel;
        ClaudeRadarQuotaSnapshot quota = local == null || local.Quota == null
            ? ClaudeRadarQuotaSnapshot.CreateDefault()
            : local.Quota;
        SoftwareRuntimePresenceSnapshot presence = GetLastRuntimePresenceSnapshot();

        cellRect = GetClaudeEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawClaudeEvenLayoutEfficiencyCell(g, cellRect, metric, true, ringFillFactor);
        x += ringCellWidth + ringGap;

        cellRect = GetClaudeEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawClaudeEvenLayoutEfficiencyCell(g, cellRect, metric, false, ringFillFactor);
        x += ringCellWidth + ringGap;

        cellRect = GetClaudeEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawClaudeEvenLayoutQuotaCell(
            g,
            cellRect,
            quota.FiveHourPercent,
            quota.FiveHourResetText,
            presence.ClaudeRunning,
            presence.AnySupportedAppRunning,
            quota.Known,
            false,
            0,
            ringFillFactor);
        x += ringCellWidth + ringGap;

        cellRect = GetClaudeEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawClaudeEvenLayoutQuotaCell(
            g,
            cellRect,
            quota.WeeklyPercent,
            quota.WeeklyResetText,
            presence.ClaudeRunning,
            presence.AnySupportedAppRunning,
            quota.Known,
            false,
            0,
            ringFillFactor);
        x += ringCellWidth + ringGap;

        cellRect = GetClaudeEvenRowCompactRingCellRect(new RectangleF(x, bounds.Top, ringCellWidth, bounds.Height), compactCellHeight);
        DrawClaudeEvenLayoutIqCell(g, cellRect, metric, ringFillFactor);
        x += ringCellWidth;

        // Divider and info band span only the ring block so the radar bar can run the full window
        // height ("额度线顶到底"); the band holds brand + RC + LLM.
        float ringBlockRight = x;
        DrawClaudeEvenRowCompactDivider(g, bounds.Left + leftInset, ringBlockRight, dividerY);
        DrawClaudeEvenRowBottomInfoPanel(
            g,
            RectangleF.FromLTRB(bounds.Left + leftInset, dividerY, Math.Max(bounds.Left + leftInset + 1.0f, ringBlockRight), bounds.Bottom),
            local);

        float radarLeft = ringBlockRight + radarGap;
        float radarRight = radarLeft + radarCellWidth;
        DrawClaudeEvenLayoutRadarCell(
            g,
            new RectangleF(radarLeft, bounds.Top, radarCellWidth, bounds.Height),
            local == null ? null : local.QuotaLine);

        float statusLeft = radarRight + radarGap;
        float statusCellWidth = Math.Max(S(40), bounds.Right - rightInset - statusLeft);
        DrawClaudeEvenRowStatusCell(g, new RectangleF(statusLeft, bounds.Top, statusCellWidth, bounds.Height), local);
    }

    // Width of the right status zone: the 24h dial is sized by the window HEIGHT plus the vertical
    // LED column and slim pads. Mirrors GetEvenRowStatusZoneWidth in CodexRadarForm.EvenRow.cs.
    private float GetClaudeEvenRowStatusZoneWidth(Graphics g, RectangleF bounds, ClaudeRadarSnapshot local)
    {
        float dialDiameter = Math.Max(S(24), bounds.Height - S(2));
        float desired = dialDiameter + S(3) + S(14) + S(1) + S(2);
        return Math.Max(S(52), Math.Min(bounds.Width * 0.36f, desired));
    }

    private RectangleF GetClaudeEvenRowCompactRingCellRect(RectangleF fullCellRect, float compactHeight)
    {
        float bottomHeight = Math.Max(S(14), fullCellRect.Height - compactHeight);
        return new RectangleF(
            fullCellRect.Left,
            fullCellRect.Top,
            fullCellRect.Width,
            Math.Max(S(34), fullCellRect.Height - bottomHeight));
    }

    private float GetClaudeEvenRowCompactDividerY(RectangleF compactCellRect, float ringFillFactor)
    {
        RectangleF ringRect;
        RectangleF textRect;
        GetClaudeEvenLayoutCellRects(compactCellRect, ringFillFactor, out ringRect, out textRect);
        return Math.Min(compactCellRect.Bottom, textRect.Bottom + S(1));
    }

    private void DrawClaudeEvenRowCompactDivider(Graphics g, float left, float right, float y)
    {
        if (right <= left)
        {
            return;
        }

        using (Pen pen = new Pen(DesignTokens.White(54), Math.Max(1.0f, S(1))))
        {
            g.DrawLine(pen, left, y, right, y);
        }
    }

    private void GetClaudeEvenLayoutCellRects(
        RectangleF cellRect,
        float ringFillFactor,
        out RectangleF ringRect,
        out RectangleF textRect)
    {
        float ringTopInset = S(3);
        float textGap = S(2);
        float textHeight = Math.Max(S(13), cellRect.Height * 0.18f);
        float ringAreaHeight = Math.Max(S(20), cellRect.Height - ringTopInset - textGap - textHeight);
        float ringSize = Math.Max(S(18), Math.Min(cellRect.Width * ringFillFactor, ringAreaHeight));
        ringRect = new RectangleF(
            cellRect.Left + (cellRect.Width - ringSize) / 2.0f,
            cellRect.Top + ringTopInset + (ringAreaHeight - ringSize) / 2.0f,
            ringSize,
            ringSize);
        textRect = new RectangleF(cellRect.Left, ringRect.Bottom + textGap, cellRect.Width, textHeight);
    }

    private void DrawClaudeEvenLayoutEfficiencyCell(
        Graphics g,
        RectangleF cellRect,
        ClaudeRadarModelMetric metric,
        bool timeEfficiency,
        float ringFillFactor)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        RectangleF ringRect;
        RectangleF textRect;
        GetClaudeEvenLayoutCellRects(cellRect, ringFillFactor, out ringRect, out textRect);

        bool known = metric != null && metric.Known;
        int efficiency = known
            ? (timeEfficiency ? metric.TimeEfficiencyPercent : metric.TokenEfficiencyPercent)
            : 100;
        string centerText = known ? ClampEfficiencyPercent(efficiency).ToString(CultureInfo.InvariantCulture) : "-";

        float stroke = Math.Max(2.0f, ringRect.Width * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);
        using (Pen basePen = new Pen(DesignTokens.WithAlpha(GetClaudeRadarLightGreen(), 242), stroke))
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

        Font font = this.fontCache.GetUi(Math.Max(7.0f, ringRect.Width * 0.342f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(centerText, font, brush, ringRect, center);
        }

        string labelText = "-";
        Color labelColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (known)
        {
            GetClaudeModelSingleEfficiencyLabelAndColor(ClampEfficiencyPercent(efficiency), timeEfficiency, out labelText, out labelColor);
        }

        Font labelFont = this.fontCache.GetUi(Math.Max(7.0f, 10.5f * this.scale), FontStyle.Bold);
        using (SolidBrush labelBrush = new SolidBrush(labelColor))
        {
            DrawClaudeRadarFittedText(g, labelText, labelFont, labelBrush, textRect, StringAlignment.Center);
        }
    }

    private void DrawClaudeEvenLayoutQuotaCell(
        Graphics g,
        RectangleF cellRect,
        int percent,
        string resetText,
        bool claudeRunning,
        bool anySupportedAppRunning,
        bool quotaValueKnown,
        bool quotaProtected,
        int consumptionRingPercent,
        float ringFillFactor)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        percent = ClampPercent(percent);
        consumptionRingPercent = ClampPercent(consumptionRingPercent);
        RectangleF ringRect;
        RectangleF textRect;
        GetClaudeEvenLayoutCellRects(cellRect, ringFillFactor, out ringRect, out textRect);

        Color ringColor = GetClaudeQuotaColor(percent);
        int visibleConsumptionRingPercent = Math.Max(percent, consumptionRingPercent);
        float stroke = Math.Max(2.0f, ringRect.Width * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);
        using (Pen backgroundPen = new Pen(DesignTokens.White(78), stroke))
        using (Pen valuePen = new Pen(ringColor, stroke))
        using (Pen consumptionRingPen = new Pen(GetClaudeQuotaConsumptionRingColor(), stroke))
        {
            backgroundPen.StartCap = LineCap.Flat;
            backgroundPen.EndCap = LineCap.Flat;
            valuePen.StartCap = LineCap.Round;
            valuePen.EndCap = LineCap.Round;
            consumptionRingPen.StartCap = LineCap.Round;
            consumptionRingPen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            if (visibleConsumptionRingPercent > percent)
            {
                g.DrawArc(consumptionRingPen, arcRect, -90.0f, 360.0f * visibleConsumptionRingPercent / 100.0f);
            }

            if (percent > 0)
            {
                g.DrawArc(valuePen, arcRect, -90.0f, 360.0f * percent / 100.0f);
            }
        }

        Color displayColor = DesignTokens.TextStrong(226);
        if (quotaProtected)
        {
            displayColor = GetClaudeRadarGoldColor();
            resetText = "已重置";
        }
        if (!claudeRunning)
        {
            displayColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        Font numberFont = this.fontCache.GetUi(Math.Max(7.0f, ringRect.Width * 0.342f), FontStyle.Bold);
        string numberText = quotaValueKnown ? percent.ToString(CultureInfo.InvariantCulture) : "-";
        using (SolidBrush numberBrush = new SolidBrush(GetClaudeQuotaRingNumberColor(displayColor, anySupportedAppRunning, quotaValueKnown)))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.DrawString(numberText, numberFont, numberBrush, ringRect, center);
        }

        Font labelFont = this.fontCache.GetUi(Math.Max(7.0f, 10.5f * this.scale), FontStyle.Bold);
        using (SolidBrush labelBrush = new SolidBrush(displayColor))
        {
            DrawClaudeRadarFittedText(g, string.IsNullOrWhiteSpace(resetText) ? "N/A" : resetText.Trim(), labelFont, labelBrush, textRect, StringAlignment.Center);
        }
    }

    private void DrawClaudeEvenLayoutIqCell(Graphics g, RectangleF cellRect, ClaudeRadarModelMetric metric, float ringFillFactor)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        RectangleF ringRect;
        RectangleF textRect;
        GetClaudeEvenLayoutCellRects(cellRect, ringFillFactor, out ringRect, out textRect);

        bool known = metric != null && metric.Known;
        int score = known ? Math.Max(0, Math.Min(MaxModelIqScore, metric.IqScore)) : 0;
        string centerText = known ? Math.Max(0, metric.IqScore).ToString(CultureInfo.InvariantCulture) : "-";
        int normalLow;
        int normalHigh;
        GetClaudeModelIqNormalScoreRange(metric, out normalLow, out normalHigh);

        float stroke = Math.Max(2.0f, ringRect.Width * 0.14f);
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
            if (known)
            {
                DrawClaudeModelIqNormalRangeArcs(g, arcRect, baselinePen, deficitPen, surplusPen, score, normalLow, normalHigh);
            }
        }

        Font font = this.fontCache.GetUi(Math.Max(7.0f, ringRect.Width * 0.36f), FontStyle.Bold);
        using (SolidBrush brush = new SolidBrush(DesignTokens.TextStrong(238)))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(centerText, font, brush, ringRect, center);
        }

        string labelText = "-";
        Color labelColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        if (known)
        {
            if (score < normalLow)
            {
                labelText = "降智";
                labelColor = DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            }
            else if (score > normalHigh)
            {
                labelText = "增智";
                labelColor = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            }
            else
            {
                labelText = "常态";
                labelColor = DesignTokens.White(245);
            }
        }

        Font labelFont = this.fontCache.GetUi(Math.Max(7.0f, 10.5f * this.scale), FontStyle.Bold);
        using (SolidBrush labelBrush = new SolidBrush(labelColor))
        {
            DrawClaudeRadarFittedText(g, labelText, labelFont, labelBrush, textRect, StringAlignment.Center);
        }
    }

    private void DrawClaudeEvenLayoutRadarCell(Graphics g, RectangleF cellRect, ClaudeRadarQuotaLineSnapshot line)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        float lineWidth = Math.Max(S(8), Math.Min(S(14), cellRect.Width * 0.42f));
        RectangleF radarLineRect = new RectangleF(
            cellRect.Left + (cellRect.Width - lineWidth) / 2.0f,
            cellRect.Top + S(3),
            lineWidth,
            Math.Max(1.0f, cellRect.Height - S(6)));
        DrawClaudeQuotaRadarVerticalLine(g, radarLineRect, line, 1.5f);
    }

    // Graphical status card per the user's sketch, mirroring CodexRadarForm.EvenRow.cs: a 24h
    // batch dial as the main element plus a vertical per-service LED column (R=radar data,
    // C=Claude status, U=Claude Code usage) at the right edge.
    private void DrawClaudeEvenRowStatusCell(Graphics g, RectangleF cellRect, ClaudeRadarSnapshot local)
    {
        if (cellRect.Width <= 0 || cellRect.Height <= 0)
        {
            return;
        }

        // The LED column is anchored to the cell's RIGHT edge (its dots must not move), while the
        // dial is shifted ~3px further left so it reads as optically centered between the radar
        // bar and the LED dots.
        float leftPad = S(1);
        float rightPad = S(2);
        float ledColumnWidth = S(14);
        float dialShift = S(3) * 0.5f;
        float ledLeft = cellRect.Right - rightPad - ledColumnWidth;
        RectangleF ledArea = new RectangleF(ledLeft, cellRect.Top, ledColumnWidth, cellRect.Height);
        float dialLeft = cellRect.Left + leftPad - dialShift;
        RectangleF dialArea = new RectangleF(
            dialLeft,
            cellRect.Top,
            Math.Max(1.0f, ledLeft - S(3) - dialLeft),
            cellRect.Height);

        DrawClaudeEvenRowBatchDial(g, dialArea, local);
        DrawClaudeEvenRowServiceLedColumn(g, ledArea, local);
    }

    private void DrawClaudeEvenRowServiceLedColumn(Graphics g, RectangleF rect, ClaudeRadarSnapshot local)
    {
        string[] labels = new string[] { "R", "C", "U" };
        ClaudeRadarServiceState[] states = new ClaudeRadarServiceState[]
        {
            local == null ? ClaudeRadarServiceState.Unknown : local.DataState,
            local == null ? ClaudeRadarServiceState.Unknown : local.ClaudeStatusState,
            local == null ? ClaudeRadarServiceState.Unknown : local.ClaudeCodeState
        };

        float rowHeight = rect.Height / labels.Length;
        float dotDiameter = S(5);
        Font letterFont = this.fontCache.GetUi(Math.Max(7.0f, 8.0f * this.scale), FontStyle.Bold);
        for (int i = 0; i < labels.Length; i++)
        {
            Color ledColor;
            if (states[i] == ClaudeRadarServiceState.Normal)
            {
                ledColor = DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245);
            }
            else if (states[i] == ClaudeRadarServiceState.Unknown)
            {
                ledColor = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
            }
            else
            {
                ledColor = GetClaudeServiceAlertColor(states[i]);
            }

            if (local != null && local.RequestRunning && (this.renderTickCount & 1) == 0)
            {
                ledColor = DesignTokens.WithAlpha(ledColor, 104);
            }

            float rowTop = rect.Top + rowHeight * i;
            float centerY = rowTop + rowHeight / 2.0f;
            using (SolidBrush dotBrush = new SolidBrush(ledColor))
            {
                g.FillEllipse(dotBrush, rect.Left, centerY - dotDiameter / 2.0f, dotDiameter, dotDiameter);
            }

            RectangleF letterRect = new RectangleF(
                rect.Left + dotDiameter + S(2),
                rowTop,
                Math.Max(1.0f, rect.Width - dotDiameter - S(2)),
                rowHeight);
            using (SolidBrush letterBrush = new SolidBrush(DesignTokens.White(170)))
            {
                DrawClaudeEvenRowStatusText(g, labels[i], letterFont, letterBrush, letterRect);
            }
        }
    }

    // 24h batch dial - mirrors DrawEvenRowBatchDial in CodexRadarForm.EvenRow.cs. Claude tests run
    // once a day -> 24h cycle for the four-level freshness color.
    private void DrawClaudeEvenRowBatchDial(Graphics g, RectangleF rect, ClaudeRadarSnapshot local)
    {
        string updateText;
        Color legacyUpdateColor;
        GetClaudeModelIqUpdateStatusText(local, out updateText, out legacyUpdateColor);

        DateTime nowLocal = DateTime.Now;
        DateTime batchTime = GetClaudeLatestMetricLocalTime(local);
        bool batchKnown = batchTime != DateTime.MinValue;
        DateTime localTime = local != null ? local.CheckedAtLocal : DateTime.MinValue;
        bool localKnown = localTime != DateTime.MinValue;
        Color updateColor = ComputeClaudeEvenRowDialStatusColor(batchKnown, batchTime, localKnown, localTime, 24.0, nowLocal);
        if (local != null && local.RequestRunning && (this.renderTickCount & 1) == 0)
        {
            updateColor = DesignTokens.WithAlpha(updateColor, 104);
        }

        string timeText = updateText;
        int slashIndex = updateText.LastIndexOf('/');
        if (slashIndex >= 0 && slashIndex < updateText.Length - 1)
        {
            timeText = updateText.Substring(slashIndex + 1);
        }

        string dataLabelText = GetClaudeModelIqDataLabelDisplayText(local);
        string heroMain;
        string heroSuffix;
        SplitClaudeEvenRowStatusHeroLabel(dataLabelText, out heroMain, out heroSuffix);
        bool phaseKnown;
        bool night;
        bool secondRun;
        string suffixTimeText;
        ParseClaudeEvenRowBatchSuffix(heroSuffix, out phaseKnown, out night, out secondRun, out suffixTimeText);

        float batchHour;
        bool batchHourKnown = TryGetClaudeEvenRowBatchHour(phaseKnown, night, suffixTimeText, out batchHour);

        float dialDiameter = Math.Min(rect.Width, rect.Height) - S(1);
        dialDiameter = Math.Max(S(20), dialDiameter);
        // Left-aligned so the dial hugs the radar bar; extra width collects toward the LED column.
        RectangleF dial = new RectangleF(
            rect.Left,
            rect.Top + (rect.Height - dialDiameter) / 2.0f,
            dialDiameter,
            dialDiameter);
        float stroke = Math.Max(2.0f, S(2));
        RectangleF arcRect = new RectangleF(
            dial.Left + stroke / 2.0f,
            dial.Top + stroke / 2.0f,
            dial.Width - stroke,
            dial.Height - stroke);

        using (Pen trackPen = new Pen(DesignTokens.White(46), stroke))
        {
            g.DrawArc(trackPen, arcRect, -90.0f, 360.0f);
        }

        if (batchHourKnown)
        {
            float batchAngle = batchHour / 24.0f * 360.0f - 90.0f;
            DateTime now = DateTime.Now;
            float nowAngle = (float)((now.Hour + now.Minute / 60.0) / 24.0 * 360.0 - 90.0);
            float sweep = nowAngle - batchAngle;
            while (sweep < 0.0f)
            {
                sweep += 360.0f;
            }

            if (sweep > 1.0f)
            {
                using (Pen arcPen = new Pen(updateColor, stroke))
                {
                    arcPen.StartCap = LineCap.Round;
                    arcPen.EndCap = LineCap.Round;
                    g.DrawArc(arcPen, arcRect, batchAngle, sweep);
                }
            }

            double markerRadians = batchAngle * Math.PI / 180.0;
            float radius = arcRect.Width / 2.0f;
            float markerX = arcRect.Left + radius + (float)Math.Cos(markerRadians) * radius;
            float markerY = arcRect.Top + radius + (float)Math.Sin(markerRadians) * radius;
            float markerDiameter = Math.Max(3.0f, S(4));
            using (SolidBrush markerBrush = new SolidBrush(DesignTokens.White(235)))
            {
                g.FillEllipse(markerBrush, markerX - markerDiameter / 2.0f, markerY - markerDiameter / 2.0f, markerDiameter, markerDiameter);
            }

            if (secondRun)
            {
                Font badgeFont = this.fontCache.GetUi(Math.Max(6.5f, 7.0f * this.scale), FontStyle.Bold);
                float badgeOffset = markerDiameter + S(2);
                RectangleF badgeRect = new RectangleF(
                    markerX - (float)Math.Cos(markerRadians) * badgeOffset - S(4),
                    markerY - (float)Math.Sin(markerRadians) * badgeOffset - S(4),
                    S(8),
                    S(8));
                using (SolidBrush badgeBrush = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245)))
                {
                    DrawClaudeEvenRowStatusText(g, "2", badgeFont, badgeBrush, badgeRect);
                }
            }
        }

        float innerWidth = dial.Width * 0.72f;
        Font dayFont = this.fontCache.GetUi(Math.Max(9.0f, 11.5f * this.scale), FontStyle.Bold);
        Font timeFont = this.fontCache.GetUi(Math.Max(7.0f, 8.0f * this.scale), FontStyle.Bold);
        float centerX2 = dial.Left + dial.Width / 2.0f;
        float centerY2 = dial.Top + dial.Height / 2.0f;
        RectangleF dayRect = new RectangleF(centerX2 - innerWidth / 2.0f, centerY2 - S(11), innerWidth, S(11));
        RectangleF timeRect2 = new RectangleF(centerX2 - innerWidth / 2.0f, centerY2 + S(1), innerWidth, S(8));
        using (SolidBrush dayBrush = new SolidBrush(DesignTokens.White(235)))
        using (SolidBrush timeBrush = new SolidBrush(updateColor))
        {
            DrawClaudeRadarFittedText(g, FormatClaudeEvenRowDialDate(heroMain), dayFont, dayBrush, dayRect, StringAlignment.Center);
            DrawClaudeRadarFittedText(g, timeText, timeFont, timeBrush, timeRect2, StringAlignment.Center);
        }
    }

    // Four-level dial freshness color - mirrors ComputeEvenRowDialStatusColor in
    // CodexRadarForm.EvenRow.cs (green fresh / yellow site-stale / orange local-stale / red when
    // either side wrapped a full cycle). Claude cycle = 24h.
    private static Color ComputeClaudeEvenRowDialStatusColor(
        bool batchKnown,
        DateTime batchTime,
        bool localKnown,
        DateTime localTime,
        double cycleHours,
        DateTime now)
    {
        if (!batchKnown && !localKnown)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }

        double batchAgeHours = batchKnown ? (now - batchTime).TotalHours : 0.0;
        double localAgeHours = localKnown ? (now - localTime).TotalHours : 0.0;
        bool siteStale = batchKnown && batchAgeHours > cycleHours;
        bool siteWrapped = batchKnown && batchAgeHours > cycleHours * 2.0;
        bool localStale = localKnown && localAgeHours > 2.0;
        bool localWrapped = localKnown && localAgeHours > cycleHours;

        if (siteWrapped || localWrapped)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
        }

        if (localStale)
        {
            return Color.FromArgb(245, 232, 128, 54);
        }

        if (siteStale)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
        }

        return DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245);
    }

    // "7.6" or "7/6" -> "7月6日" - mirrors FormatEvenRowDialDate in CodexRadarForm.EvenRow.cs.
    private static string FormatClaudeEvenRowDialDate(string heroMain)
    {
        string raw = (heroMain ?? string.Empty).Trim();
        if (raw.Length == 0 || raw.IndexOf('月') >= 0)
        {
            return raw;
        }

        char[] separators = new char[] { '.', '/' };
        int sep = raw.IndexOfAny(separators);
        int month;
        int day;
        if (sep > 0 && sep < raw.Length - 1 &&
            int.TryParse(raw.Substring(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out month) &&
            int.TryParse(raw.Substring(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out day) &&
            month >= 1 && month <= 12 && day >= 1 && day <= 31)
        {
            return month.ToString(CultureInfo.InvariantCulture) + "月" + day.ToString(CultureInfo.InvariantCulture) + "日";
        }

        return raw;
    }

    private static bool TryGetClaudeEvenRowBatchHour(bool phaseKnown, bool night, string suffixTimeText, out float batchHour)
    {
        if (phaseKnown)
        {
            batchHour = night ? 12.0f : 0.0f;
            return true;
        }

        if (!string.IsNullOrEmpty(suffixTimeText))
        {
            int colon = suffixTimeText.IndexOf(':');
            int hour;
            int minute;
            if (colon > 0 &&
                int.TryParse(suffixTimeText.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour) &&
                int.TryParse(suffixTimeText.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out minute) &&
                hour >= 0 && hour < 24 && minute >= 0 && minute < 60)
            {
                batchHour = hour + minute / 60.0f;
                return true;
            }
        }

        batchHour = 0.0f;
        return false;
    }

    // "7.6_pm" -> main "7.6" + suffix "pm"; "7/6 10:59" (date + time) also splits so the time can
    // become the dial's batch marker; other labels stay whole in main.
    private static void SplitClaudeEvenRowStatusHeroLabel(string dataLabelText, out string heroMain, out string heroSuffix)
    {
        string raw = (dataLabelText ?? string.Empty).Trim();
        int underscore = raw.IndexOf('_');
        if (underscore > 0 && underscore < raw.Length - 1)
        {
            heroMain = raw.Substring(0, underscore);
            heroSuffix = raw.Substring(underscore + 1);
            return;
        }

        int space = raw.LastIndexOf(' ');
        if (space > 0 && space < raw.Length - 1 && raw.IndexOf(':', space) > space)
        {
            heroMain = raw.Substring(0, space);
            heroSuffix = raw.Substring(space + 1);
            return;
        }

        heroMain = raw;
        heroSuffix = string.Empty;
    }

    private static void ParseClaudeEvenRowBatchSuffix(
        string suffix,
        out bool phaseKnown,
        out bool night,
        out bool secondRun,
        out string suffixTimeText)
    {
        string raw = (suffix ?? string.Empty).Trim().ToLowerInvariant();
        phaseKnown = false;
        night = false;
        secondRun = false;
        suffixTimeText = string.Empty;
        if (raw.Length == 0)
        {
            return;
        }

        if (raw.StartsWith("am", StringComparison.Ordinal))
        {
            phaseKnown = true;
            return;
        }

        if (raw.StartsWith("pm", StringComparison.Ordinal))
        {
            phaseKnown = true;
            night = true;
            string rest = raw.Substring(2).TrimStart('_', '-', ' ');
            int run;
            secondRun = rest.Length > 0 && int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out run) && run >= 2;
            return;
        }

        if (raw.IndexOf(':') > 0)
        {
            suffixTimeText = raw;
        }
    }

    private void DrawClaudeEvenRowBottomInfoPanel(Graphics g, RectangleF rect, ClaudeRadarSnapshot local)
    {
        if (rect.Width <= S(24) || rect.Height <= S(8))
        {
            return;
        }

        // Brand first (leftmost), matching the Codex window's band order.
        string[] texts = new string[]
        {
            "Claude",
            GetClaudeBottomRatingDisplayText(local),
            GetClaudeBottomModelDisplayText(local)
        };

        float gap = S(6);
        float available = Math.Max(1.0f, rect.Width - gap * (texts.Length - 1));
        Font font = GetClaudeEvenRowBottomInfoSharedFont(g, texts, available);
        Font serviceFamilyFont = this.fontCache.GetUi(font.Size, FontStyle.Bold);
        Color bottomTextColor = DesignTokens.White(206);

        float measuredTotal = 0.0f;
        float[] widths = new float[texts.Length];
        for (int i = 0; i < texts.Length; i++)
        {
            Font itemFont = i == 0 ? serviceFamilyFont : font;
            widths[i] = g.MeasureString(texts[i] ?? string.Empty, itemFont).Width;
            measuredTotal += widths[i];
        }

        float leftover = Math.Max(0.0f, available - measuredTotal);
        float share = leftover / texts.Length;
        float x = rect.Left;
        for (int i = 0; i < texts.Length; i++)
        {
            float width = i == texts.Length - 1
                ? Math.Max(1.0f, rect.Right - x)
                : widths[i] + share;
            RectangleF itemRect = new RectangleF(x, rect.Top, Math.Max(1.0f, width), rect.Height);
            Font itemFont = i == 0 ? serviceFamilyFont : font;
            Color itemColor = i == 0 ? ClaudeOrange : bottomTextColor;
            using (SolidBrush brush = new SolidBrush(itemColor))
            {
                DrawClaudeEvenRowStatusText(g, texts[i], itemFont, brush, itemRect);
            }

            x += width + gap;
        }
    }

    private void GetClaudeApiServiceSummaryText(ClaudeRadarSnapshot local, out string text, out Color color)
    {
        List<ClaudeServiceAlertCandidate> candidates = GetClaudeApiServiceAlertCandidates(local);
        if (candidates.Count == 0)
        {
            text = "API无异常";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245);
            return;
        }

        int index = Math.Abs(this.renderTickCount / 2) % candidates.Count;
        ClaudeServiceAlertCandidate candidate = candidates[index];
        bool reasonPhase = (this.renderTickCount & 1) == 1;
        text = reasonPhase && !string.IsNullOrWhiteSpace(candidate.Reason)
            ? candidate.Reason
            : candidate.Name;
        color = candidate.Color;
    }

    private List<ClaudeServiceAlertCandidate> GetClaudeApiServiceAlertCandidates(ClaudeRadarSnapshot local)
    {
        List<ClaudeServiceAlertCandidate> result = new List<ClaudeServiceAlertCandidate>();
        if (local == null)
        {
            result.Add(new ClaudeServiceAlertCandidate("Radar", "无数据", DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230)));
            return result;
        }

        AddClaudeServiceAlertCandidate(result, "Radar", local.DataState, FallbackText(local.ErrorMessage, local.ErrorCode));
        AddClaudeServiceAlertCandidate(result, "Claude", local.ClaudeStatusState, string.Empty);
        AddClaudeServiceAlertCandidate(result, "Usage", local.ClaudeCodeState, string.Empty);
        return result;
    }

    private void AddClaudeServiceAlertCandidate(
        List<ClaudeServiceAlertCandidate> result,
        string name,
        ClaudeRadarServiceState state,
        string detail)
    {
        if (state == ClaudeRadarServiceState.Normal || state == ClaudeRadarServiceState.Unknown)
        {
            return;
        }

        result.Add(new ClaudeServiceAlertCandidate(
            name,
            string.IsNullOrWhiteSpace(detail) ? GetClaudeServiceAlertReason(state) : detail.Trim(),
            GetClaudeServiceAlertColor(state)));
    }

    private static string GetClaudeServiceAlertReason(ClaudeRadarServiceState state)
    {
        switch (state)
        {
            case ClaudeRadarServiceState.Offline:
                return "无网络";
            case ClaudeRadarServiceState.Incomplete:
                return "元素缺失";
            case ClaudeRadarServiceState.Unavailable:
                return "服务异常";
            case ClaudeRadarServiceState.Unreachable:
                return "无法连接";
            default:
                return "状态未知";
        }
    }

    private static Color GetClaudeServiceAlertColor(ClaudeRadarServiceState state)
    {
        switch (state)
        {
            case ClaudeRadarServiceState.Offline:
            case ClaudeRadarServiceState.Incomplete:
                return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
            case ClaudeRadarServiceState.Unavailable:
                return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            case ClaudeRadarServiceState.Unreachable:
                return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            default:
                return DesignTokens.White(210);
        }
    }

    private void GetClaudeModelIqUpdateStatusText(ClaudeRadarSnapshot local, out string text, out Color color)
    {
        if (local != null && local.RequestRunning)
        {
            text = "刷新中";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            return;
        }

        DateTime localTime = GetClaudeLatestMetricLocalTime(local);
        if (localTime == DateTime.MinValue)
        {
            text = "未更新/--:--";
            color = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            return;
        }

        bool currentDate = localTime.Date == DateTime.Now.Date;
        text = (currentDate ? "已更新/" : "未更新/") + localTime.ToString("HH:mm", CultureInfo.CurrentCulture);
        color = currentDate
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245)
            : DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
    }

    private static string GetClaudeModelIqDataLabelDisplayText(ClaudeRadarSnapshot local)
    {
        ClaudeRadarModelMetric metric = local == null ? null : local.SelectedModel;
        if (metric != null && !string.IsNullOrWhiteSpace(metric.LatestLabel))
        {
            return metric.LatestLabel.Trim();
        }

        if (local != null && local.SiteUpdatedAtKnown)
        {
            return local.SiteUpdatedAtUtc.ToLocalTime().ToString("M.d_HH:mm", CultureInfo.CurrentCulture);
        }

        return "--";
    }

    private static DateTime GetClaudeLatestMetricLocalTime(ClaudeRadarSnapshot local)
    {
        ClaudeRadarModelMetric metric = local == null ? null : local.SelectedModel;
        if (metric != null && metric.LatestAtKnown && metric.LatestAtUtc != DateTime.MinValue)
        {
            return metric.LatestAtUtc.ToLocalTime();
        }

        if (local != null && local.SiteUpdatedAtKnown && local.SiteUpdatedAtUtc != DateTime.MinValue)
        {
            return local.SiteUpdatedAtUtc.ToLocalTime();
        }

        if (local != null && local.CheckedAtLocal != DateTime.MinValue)
        {
            return local.CheckedAtLocal;
        }

        return DateTime.MinValue;
    }

    private string GetClaudeBottomRatingDisplayText(ClaudeRadarSnapshot local)
    {
        ClaudeRadarCommunitySnapshot community = local == null ? null : local.Community;
        if (community == null)
        {
            return "RC:--";
        }

        string shortLabel = FormatClaudeRadarShortLabel(community.RatingKey, community.Label);
        return "RC:" + (string.IsNullOrEmpty(shortLabel) ? "--" : shortLabel);
    }

    private string GetClaudeBottomModelDisplayText(ClaudeRadarSnapshot local)
    {
        string key = local == null ? string.Empty : (local.SelectedModelKey ?? string.Empty);
        string label = local == null ? string.Empty : local.SelectedModelName;
        if (string.IsNullOrWhiteSpace(label) && local != null && local.SelectedModel != null)
        {
            label = local.SelectedModel.Name;
        }

        if (string.IsNullOrWhiteSpace(key) && local != null && local.SelectedModel != null)
        {
            key = local.SelectedModel.SourceKey;
        }

        if (string.IsNullOrWhiteSpace(label) && local != null && local.Models != null)
        {
            for (int i = 0; i < local.Models.Count; i++)
            {
                ClaudeRadarModelEntry entry = local.Models[i];
                if (entry != null &&
                    string.Equals(entry.SourceKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    label = entry.DisplayName;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(key) && this.currentSettings != null)
        {
            key = this.currentSettings.ClaudeRadarModelKey ?? string.Empty;
        }

        string shortLabel = FormatClaudeRadarShortLabel(key, label);
        return "LLM:" + (string.IsNullOrEmpty(shortLabel) ? "--" : shortLabel);
    }

    private static string FormatClaudeRadarShortLabel(string key, string label)
    {
        string raw = !string.IsNullOrWhiteSpace(label) ? label : key;
        raw = (raw ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        string lower = raw.ToLowerInvariant();
        Match family = Regex.Match(lower, "(?:claude\\s*[-_\\s]*)?(opus|sonnet|haiku|fable)\\s*[-_\\s]*([0-9]+(?:\\.[0-9]+)?)?\\s*[-_\\s]*(xhigh|high|medium|low|max|ultra)?");
        if (family.Success)
        {
            string name = family.Groups[1].Value;
            string prefix = char.ToUpperInvariant(name[0]) + name.Substring(1, 1);
            string version = family.Groups[2].Success ? family.Groups[2].Value : string.Empty;
            string effort = FormatClaudeRadarEffortSuffix(family.Groups[3].Value);
            return prefix + version + effort;
        }

        string compact = string.Empty;
        for (int i = 0; i < raw.Length; i++)
        {
            char ch = raw[i];
            if (char.IsLetterOrDigit(ch) || ch == '.')
            {
                compact += ch;
            }
        }

        return compact.Length <= 8 ? compact : compact.Substring(0, 8);
    }

    private static string FormatClaudeRadarEffortSuffix(string suffix)
    {
        suffix = (suffix ?? string.Empty).Trim().ToLowerInvariant();
        if (suffix == "xhigh") return "X";
        if (suffix == "high") return "H";
        if (suffix == "medium") return "M";
        if (suffix == "low") return "L";
        if (suffix == "max") return "MAX";
        if (suffix == "ultra") return "Ult";
        return string.Empty;
    }

    private void DrawClaudeQuotaRadarVerticalLine(
        Graphics g,
        RectangleF rect,
        ClaudeRadarQuotaLineSnapshot line,
        float strokeScale)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        float stroke = Math.Max(1.0f, Math.Min(S(3), rect.Width * 0.42f)) * Math.Max(0.1f, strokeScale);
        float x = rect.Left + rect.Width / 2.0f;
        float top = rect.Top + S(1);
        float bottom = rect.Bottom - S(1);
        if (bottom <= top)
        {
            return;
        }

        if (line == null || !line.Known)
        {
            using (Pen emptyPen = new Pen(DesignTokens.White(34), stroke))
            {
                emptyPen.StartCap = LineCap.Round;
                emptyPen.EndCap = LineCap.Round;
                g.DrawLine(emptyPen, x, top, x, bottom);
            }

            return;
        }

        double minValue = Math.Max(0.0, line.MinValue);
        double maxValue = Math.Max(minValue, line.MaxValue);
        if (line.PreviousKnown)
        {
            minValue = Math.Min(minValue, Math.Max(0.0, line.PreviousValue));
            maxValue = Math.Max(maxValue, line.PreviousValue);
        }
        if (line.AverageKnown)
        {
            minValue = Math.Min(minValue, Math.Max(0.0, line.AverageValue));
            maxValue = Math.Max(maxValue, line.AverageValue);
        }
        if (maxValue - minValue < 0.005)
        {
            double padding = Math.Max(1.0, Math.Abs(maxValue) * 0.04);
            minValue = Math.Max(0.0, minValue - padding);
            maxValue += padding;
        }

        float currentY = GetClaudeQuotaRadarLineY(top, bottom, line.CurrentValue, minValue, maxValue);
        float previousY = line.PreviousKnown
            ? GetClaudeQuotaRadarLineY(top, bottom, line.PreviousValue, minValue, maxValue)
            : currentY;
        float averageY = line.AverageKnown
            ? GetClaudeQuotaRadarLineY(top, bottom, line.AverageValue, minValue, maxValue)
            : float.NaN;
        Color segmentColor = GetClaudeQuotaRadarVerticalSegmentColor(line, currentY, averageY, top, bottom);

        using (Pen basePen = new Pen(Color.FromArgb(136, 128, 134, 142), stroke))
        using (Pen segmentPen = new Pen(segmentColor, stroke))
        {
            basePen.StartCap = LineCap.Round;
            basePen.EndCap = LineCap.Round;
            segmentPen.StartCap = LineCap.Round;
            segmentPen.EndCap = LineCap.Round;
            g.DrawLine(basePen, x, bottom, x, top);
            if (line.PreviousKnown && Math.Abs(line.CurrentValue - line.PreviousValue) > 0.005)
            {
                DrawClaudeQuotaRadarVerticalSegment(g, segmentPen, x, currentY, previousY, top, bottom);
            }
        }

        if (!float.IsNaN(averageY))
        {
            using (Pen averagePen = new Pen(DesignTokens.White(214), Math.Max(1.0f, S(1))))
            {
                averagePen.StartCap = LineCap.Round;
                averagePen.EndCap = LineCap.Round;
                averageY = Math.Max(top, Math.Min(bottom, averageY));
                float half = Math.Min(rect.Width * 0.48f, Math.Max(S(2), stroke * 1.15f));
                g.DrawLine(averagePen, Math.Max(rect.Left, x - half), averageY, Math.Min(rect.Right, x + half), averageY);
            }

            DrawClaudeQuotaRadarTrendArrows(g, x, top, bottom, currentY, averageY, line, stroke);
        }

        DrawClaudeQuotaRadarCurrentPoint(g, x, currentY, stroke, top, bottom);
    }

    private void DrawClaudeQuotaRadarTrendArrows(
        Graphics g,
        float x,
        float top,
        float bottom,
        float currentY,
        float averageY,
        ClaudeRadarQuotaLineSnapshot line,
        float stroke)
    {
        if (line == null || !line.PreviousKnown || bottom <= top)
        {
            return;
        }

        const double epsilon = 0.005;
        bool up = line.CurrentValue > line.PreviousValue + epsilon;
        bool down = line.CurrentValue < line.PreviousValue - epsilon;
        if (!up && !down)
        {
            return;
        }

        averageY = Math.Max(top, Math.Min(bottom, averageY));
        currentY = Math.Max(top, Math.Min(bottom, currentY));
        float zoneStart;
        float zoneEnd;
        if (currentY < averageY)
        {
            zoneStart = averageY;
            zoneEnd = bottom;
        }
        else
        {
            zoneStart = top;
            zoneEnd = averageY;
        }

        if (Math.Abs(zoneEnd - zoneStart) < S(10))
        {
            return;
        }

        Color color = up
            ? Color.FromArgb(224, 142, 242, 185)
            : Color.FromArgb(224, 255, 152, 152);
        using (Pen arrowPen = new Pen(color, Math.Max(1.0f, stroke * 0.22f)))
        {
            arrowPen.StartCap = LineCap.Round;
            arrowPen.EndCap = LineCap.Round;
            arrowPen.LineJoin = LineJoin.Round;
            DrawClaudeQuotaRadarChevronLine(g, arrowPen, x, zoneStart + (zoneEnd - zoneStart) / 3.0f, up, stroke);
            DrawClaudeQuotaRadarChevronLine(g, arrowPen, x, zoneStart + (zoneEnd - zoneStart) * 2.0f / 3.0f, up, stroke);
        }
    }

    private void DrawClaudeQuotaRadarChevronLine(Graphics g, Pen pen, float x, float y, bool up, float stroke)
    {
        float dotDiameter = Math.Max(S(1), stroke * 0.55f);
        float width = Math.Max(S(2), dotDiameter * 1.35f);
        float height = Math.Max(S(1), width / 3.4641f);
        PointF left = up ? new PointF(x - width, y + height) : new PointF(x - width, y - height);
        PointF tip = up ? new PointF(x, y - height) : new PointF(x, y + height);
        PointF right = up ? new PointF(x + width, y + height) : new PointF(x + width, y - height);
        g.DrawLine(pen, left, tip);
        g.DrawLine(pen, tip, right);
    }

    private static Color GetClaudeQuotaRadarVerticalSegmentColor(
        ClaudeRadarQuotaLineSnapshot line,
        float currentY,
        float averageY,
        float top,
        float bottom)
    {
        if (line == null)
        {
            return DesignTokens.White(180);
        }

        if (line.PreviousKnown && line.CurrentValue < Math.Min(line.MinValue, line.PreviousValue))
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 238);
        }

        if (line.PreviousKnown && line.CurrentValue > Math.Max(line.MaxValue, line.PreviousValue))
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 238);
        }

        if (float.IsNaN(averageY) || bottom <= top)
        {
            return DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 238);
        }

        currentY = Math.Max(top, Math.Min(bottom, currentY));
        averageY = Math.Max(top, Math.Min(bottom, averageY));
        if (currentY <= averageY)
        {
            float span = Math.Max(1.0f, averageY - top);
            float progressTowardTop = (averageY - currentY) / span;
            return progressTowardTop >= 0.5f
                ? DesignTokens.WithAlpha(DesignTokens.Colors.QuotaGood, 238)
                : Color.FromArgb(238, 142, 242, 185);
        }

        float lowerSpan = Math.Max(1.0f, bottom - averageY);
        float progressTowardBottom = (currentY - averageY) / lowerSpan;
        return progressTowardBottom < 0.5f
            ? DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 238)
            : DesignTokens.WithAlpha(DesignTokens.Colors.WarningDeep, 238);
    }

    private static float GetClaudeQuotaRadarLineY(float top, float bottom, double value, double minValue, double maxValue)
    {
        double span = Math.Max(1.0, maxValue - minValue);
        double ratio = Math.Max(0.0, Math.Min(1.0, (value - minValue) / span));
        return bottom - (float)((bottom - top) * ratio);
    }

    private static void DrawClaudeQuotaRadarVerticalSegment(Graphics g, Pen pen, float x, float y1, float y2, float top, float bottom)
    {
        float segmentTop = Math.Max(top, Math.Min(y1, y2));
        float segmentBottom = Math.Min(bottom, Math.Max(y1, y2));
        if (segmentBottom > segmentTop)
        {
            g.DrawLine(pen, x, segmentBottom, x, segmentTop);
        }
    }

    private static void DrawClaudeQuotaRadarCurrentPoint(Graphics g, float x, float y, float stroke, float top, float bottom)
    {
        float diameter = Math.Max(1.0f, stroke);
        float radius = diameter / 2.0f;
        y = Math.Max(top + radius, Math.Min(bottom - radius, y));
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(246, 56, 189, 248)))
        {
            g.FillEllipse(brush, x - radius, y - radius, diameter, diameter);
        }
    }

    private void GetClaudeModelSingleEfficiencyLabelAndColor(int efficiency, bool timeEfficiency, out string text, out Color color)
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

    private static void GetClaudeModelIqNormalScoreRange(ClaudeRadarModelMetric metric, out int low, out int high)
    {
        low = metric == null ? 90 : metric.NormalLow;
        high = metric == null ? 110 : metric.NormalHigh;
        if (!NormalizeClaudeModelIqNormalRange(ref low, ref high))
        {
            low = 90;
            high = 110;
        }
    }

    private static bool NormalizeClaudeModelIqNormalRange(ref int low, ref int high)
    {
        low = Math.Max(0, Math.Min(MaxModelIqScore, low));
        high = Math.Max(0, Math.Min(MaxModelIqScore, high));
        if (low > high)
        {
            int temp = low;
            low = high;
            high = temp;
        }

        return high > low;
    }

    private static void DrawClaudeModelIqNormalRangeArcs(
        Graphics g,
        RectangleF arcRect,
        Pen normalPen,
        Pen deficitPen,
        Pen surplusPen,
        int score,
        int normalLow,
        int normalHigh)
    {
        score = Math.Max(0, Math.Min(MaxModelIqScore, score));
        if (!NormalizeClaudeModelIqNormalRange(ref normalLow, ref normalHigh))
        {
            normalLow = 90;
            normalHigh = 110;
        }

        g.DrawArc(normalPen, arcRect, -90.0f, 360.0f);
        if (score < normalLow)
        {
            g.DrawArc(deficitPen, arcRect, -90.0f, -ClaudeModelIqScoreToArcSweep(normalLow - score));
        }
        else if (score > normalHigh)
        {
            g.DrawArc(surplusPen, arcRect, -90.0f, ClaudeModelIqScoreToArcSweep(score - normalHigh));
        }
    }

    private static float ClaudeModelIqScoreToArcSweep(int scoreDelta)
    {
        return 360.0f * Math.Max(0, Math.Min(MaxModelIqScore, scoreDelta)) / MaxModelIqScore;
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private static int ClampEfficiencyPercent(int value)
    {
        return Math.Max(0, Math.Min(200, value));
    }

    private static Color GetClaudeQuotaColor(int percent)
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

    private static Color GetClaudeQuotaRingNumberColor(Color fallbackColor, bool anySupportedAppRunning, bool quotaValueKnown)
    {
        if (!quotaValueKnown)
        {
            return fallbackColor;
        }

        return anySupportedAppRunning
            ? DesignTokens.White(246)
            : DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
    }

    private static Color GetClaudeQuotaConsumptionRingColor()
    {
        return DesignTokens.WithAlpha(GetClaudeRadarLightGreen(), 242);
    }

    private static Color GetClaudeRadarLightGreen()
    {
        return Color.FromArgb(142, 242, 185);
    }

    private static Color GetClaudeRadarGoldColor()
    {
        return Color.FromArgb(255, 194, 72);
    }

    private Font GetClaudeEvenRowBottomInfoSharedFont(Graphics g, string[] texts, float available)
    {
        float baseSize = Math.Max(8.5f, 10.5f * this.scale * 1.56f);
        float minSize = Math.Max(4.5f, 4.5f * this.scale);
        float step = Math.Max(0.3f, 0.3f * this.scale);
        return GetClaudeEvenRowSharedFont(g, texts, null, baseSize, minSize, step, available);
    }

    private Font GetClaudeEvenRowSharedFont(
        Graphics g,
        string[] texts,
        RectangleF[] rects,
        float baseSize,
        float minSize,
        float step)
    {
        return GetClaudeEvenRowSharedFont(g, texts, rects, baseSize, minSize, step, -1.0f);
    }

    private Font GetClaudeEvenRowSharedFont(
        Graphics g,
        string[] texts,
        RectangleF[] rects,
        float baseSize,
        float minSize,
        float step,
        float totalAvailableWidth)
    {
        float size = Math.Max(minSize, baseSize);
        while (size > minSize)
        {
            Font probe = this.fontCache.GetUi(size, FontStyle.Bold);
            bool fits = true;
            if (rects != null)
            {
                for (int i = 0; texts != null && i < texts.Length && i < rects.Length; i++)
                {
                    SizeF measured = g.MeasureString(texts[i] ?? string.Empty, probe);
                    if (measured.Width > rects[i].Width * 0.98f || measured.Height > rects[i].Height * 1.18f)
                    {
                        fits = false;
                        break;
                    }
                }
            }
            else if (totalAvailableWidth > 0.0f)
            {
                float total = 0.0f;
                for (int i = 0; texts != null && i < texts.Length; i++)
                {
                    total += g.MeasureString(texts[i] ?? string.Empty, probe).Width;
                }

                fits = total <= totalAvailableWidth * 0.99f;
            }

            if (fits)
            {
                return probe;
            }

            size -= Math.Max(0.1f, step);
        }

        return this.fontCache.GetUi(minSize, FontStyle.Bold);
    }

    private static void DrawClaudeEvenRowStatusText(Graphics g, string text, Font font, Brush brush, RectangleF rect)
    {
        using (StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(text ?? string.Empty, font, brush, rect, format);
        }
    }

    private void DrawClaudeRadarFittedText(Graphics g, string text, Font baseFont, Brush brush, RectangleF rect, StringAlignment alignment)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        text = text ?? string.Empty;
        Font font = baseFont;
        float minSize = Math.Max(5.0f, baseFont.Size * 0.55f);
        // No "+ S(1)" slack here: the draw call below has zero tolerance (EllipsisCharacter trims the
        // instant measured width exceeds rect.Width by even a fraction of a pixel), so a shrink loop
        // that stops a couple of pixels short of actually fitting still gets silently ellipsis-trimmed
        // at draw time - this was truncating short labels like "省时"/"20:30" even though the loop
        // believed it was "close enough".
        while (font.Size > minSize && g.MeasureString(text, font).Width > rect.Width)
        {
            font = this.fontCache.GetUi(font.Size - 0.5f, baseFont.Style);
        }

        // NoWrap is required: without it, text that still doesn't fit at the minimum size wraps onto
        // a second line (e.g. "20:30" -> "20:3" / "0") instead of ellipsis-trimming on one line like
        // CodexRadar's equivalent DrawCodexRadarFittedText.
        using (StringFormat format = new StringFormat { Alignment = alignment, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        {
            g.DrawString(text, font, brush, rect, format);
        }
    }

    private sealed class ClaudeServiceAlertCandidate
    {
        public ClaudeServiceAlertCandidate(string name, string reason, Color color)
        {
            this.Name = name ?? string.Empty;
            this.Reason = reason ?? string.Empty;
            this.Color = color;
        }

        public string Name { get; private set; }
        public string Reason { get; private set; }
        public Color Color { get; private set; }
    }

    private void DrawStatusPanel(Graphics g, RectangleF rect, ClaudeRadarSnapshot local, ClaudeRadarModelMetric metric)
    {
        using (StringFormat near = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
        using (SolidBrush title = new SolidBrush(ClaudeOrange))
        using (SolidBrush text = new SolidBrush(DesignTokens.White(230)))
        using (SolidBrush muted = new SolidBrush(DesignTokens.WithAlpha(DesignTokens.Colors.SubtleText, 210)))
        {
            Font brand = this.fontCache.GetUi(Math.Max(8.0f, S(9.5f)), FontStyle.Bold);
            Font body = this.fontCache.GetUi(Math.Max(7.0f, S(8.0f)), FontStyle.Bold);
            Font small = this.fontCache.GetUi(Math.Max(6.0f, S(6.9f)), FontStyle.Regular);
            g.DrawString("Claude", brand, title, new RectangleF(rect.Left, rect.Top, rect.Width, rect.Height * 0.24f), near);
            string model = string.IsNullOrWhiteSpace(local.SelectedModelName) ? metric.Name : local.SelectedModelName;
            if (string.IsNullOrWhiteSpace(model))
            {
                model = local.SelectedModelKey;
            }

            g.DrawString(model ?? "--", body, text, new RectangleF(rect.Left, rect.Top + rect.Height * 0.24f, rect.Width, rect.Height * 0.25f), near);
            DrawServiceSquares(g, new RectangleF(rect.Left, rect.Top + rect.Height * 0.53f, rect.Width, rect.Height * 0.22f), local);
            string footer = local.RequestRunning ? "刷新中" : FormatLocalTime(local.CheckedAtLocal);
            g.DrawString(footer, small, muted, new RectangleF(rect.Left, rect.Bottom - rect.Height * 0.22f, rect.Width, rect.Height * 0.22f), near);
        }
    }

    private void DrawServiceSquares(Graphics g, RectangleF rect, ClaudeRadarSnapshot local)
    {
        float size = Math.Min(rect.Height, Math.Max(S(13), rect.Width / 5.5f));
        float x = rect.Left;
        DrawServiceSquare(g, new RectangleF(x, rect.Top, size, size), "R", local.DataState);
        x += size + S(4);
        DrawServiceSquare(g, new RectangleF(x, rect.Top, size, size), "C", local.ClaudeStatusState);
        x += size + S(4);
        DrawServiceSquare(g, new RectangleF(x, rect.Top, size, size), "U", local.ClaudeCodeState);
    }

    private void DrawServiceSquare(Graphics g, RectangleF rect, string text, ClaudeRadarServiceState state)
    {
        Color color = GetServiceColor(state);
        using (GraphicsPath path = RoundedRectangle(rect, S(3)))
        using (SolidBrush fill = new SolidBrush(DesignTokens.WithAlpha(color, state == ClaudeRadarServiceState.Normal ? 48 : 30)))
        using (Pen pen = new Pen(DesignTokens.WithAlpha(color, 190), Math.Max(1.0f, S(1))))
        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
            if (state == ClaudeRadarServiceState.Incomplete ||
                state == ClaudeRadarServiceState.Unavailable ||
                state == ClaudeRadarServiceState.Unreachable)
            {
                using (Pen crossPen = new Pen(DesignTokens.WithAlpha(color, 220), Math.Max(1.0f, rect.Width * 0.08f)))
                {
                    crossPen.StartCap = LineCap.Round;
                    crossPen.EndCap = LineCap.Round;
                    float inset = Math.Max(S(3), rect.Width * 0.25f);
                    g.DrawLine(crossPen, rect.Left + inset, rect.Top + inset, rect.Right - inset, rect.Bottom - inset);
                    g.DrawLine(crossPen, rect.Right - inset, rect.Top + inset, rect.Left + inset, rect.Bottom - inset);
                }
            }

            g.DrawString(text, this.fontCache.GetUi(Math.Max(6.5f, S(7.2f)), FontStyle.Bold), brush, rect, center);
        }
    }

    private void DrawMetricRing(Graphics g, RectangleF rect, string kind, int value, int max, Color arcColor, string centerText)
    {
        DrawMetricRing(g, rect, kind, value, max, arcColor, centerText, DesignTokens.White(242));
    }

    private void DrawMetricRing(Graphics g, RectangleF rect, string kind, int value, int max, Color arcColor, string centerText, Color centerColor)
    {
        float stroke = Math.Max(2.2f, rect.Width * 0.12f);
        RectangleF arc = Inflate(rect, -stroke / 2.0f);
        using (Pen basePen = new Pen(DesignTokens.WithAlpha(Color.FromArgb(72, 78, 84), 185), stroke))
        using (Pen valuePen = new Pen(DesignTokens.WithAlpha(arcColor, 225), stroke))
        using (SolidBrush text = new SolidBrush(centerColor))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            basePen.StartCap = LineCap.Round;
            basePen.EndCap = LineCap.Round;
            valuePen.StartCap = LineCap.Round;
            valuePen.EndCap = LineCap.Round;
            g.DrawArc(basePen, arc, -90.0f, 360.0f);
            float sweep = (float)(Math.Max(0, Math.Min(max, value)) / (double)Math.Max(1, max) * 360.0);
            if (sweep > 0.1f)
            {
                g.DrawArc(valuePen, arc, -90.0f, sweep);
            }

            Font font = this.fontCache.GetUi(Math.Max(7.0f, rect.Width * 0.27f), FontStyle.Bold);
            g.DrawString(centerText ?? "--", font, text, rect, center);
        }
    }

    private void DrawSmallLabel(Graphics g, RectangleF rect, string text, Color color)
    {
        using (SolidBrush brush = new SolidBrush(color))
        using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
        {
            Font font = this.fontCache.GetUi(Math.Max(6.0f, S(7.0f)), FontStyle.Bold);
            g.DrawString(string.IsNullOrWhiteSpace(text) ? "--" : text, font, brush, rect, center);
        }
    }

    private void DrawQuotaLine(Graphics g, RectangleF rect, ClaudeRadarQuotaLineSnapshot line)
    {
        float centerX = rect.Left + rect.Width / 2.0f;
        using (Pen basePen = new Pen(DesignTokens.WithAlpha(Color.FromArgb(95, 100, 108), 210), Math.Max(1.4f, rect.Width * 0.18f)))
        {
            basePen.StartCap = LineCap.Round;
            basePen.EndCap = LineCap.Round;
            g.DrawLine(basePen, centerX, rect.Top, centerX, rect.Bottom);
        }

        if (line == null || !line.Known)
        {
            return;
        }

        double min = line.MinValue;
        double max = line.MaxValue;
        if (Math.Abs(max - min) < 0.0001)
        {
            min -= 1.0;
            max += 1.0;
        }

        float currentY = MapValueToY(line.CurrentValue, min, max, rect);
        if (line.AverageKnown)
        {
            float avgY = MapValueToY(line.AverageValue, min, max, rect);
            using (Pen avg = new Pen(DesignTokens.White(220), Math.Max(1.0f, S(1))))
            {
                g.DrawLine(avg, rect.Left, avgY, rect.Right, avgY);
            }
        }

        if (line.PreviousKnown)
        {
            float prevY = MapValueToY(line.PreviousValue, min, max, rect);
            Color segmentColor = GetQuotaLineSegmentColor(line);
            using (Pen segment = new Pen(segmentColor, Math.Max(2.0f, rect.Width * 0.22f)))
            {
                segment.StartCap = LineCap.Round;
                segment.EndCap = LineCap.Round;
                g.DrawLine(segment, centerX, prevY, centerX, currentY);
            }
        }

        using (SolidBrush point = new SolidBrush(DesignTokens.Colors.Accent))
        {
            float r = Math.Max(2.0f, rect.Width * 0.23f);
            g.FillEllipse(point, centerX - r, currentY - r, r * 2.0f, r * 2.0f);
        }
    }

    private Color GetQuotaLineSegmentColor(ClaudeRadarQuotaLineSnapshot line)
    {
        if (!line.PreviousKnown)
        {
            return DesignTokens.Colors.Accent;
        }

        if (line.CurrentValue < Math.Min(line.MinValue, line.PreviousValue))
        {
            return DesignTokens.Colors.Danger;
        }

        if (line.CurrentValue > Math.Max(line.MaxValue, line.PreviousValue))
        {
            return DesignTokens.Colors.Warning;
        }

        bool aboveAverage = !line.AverageKnown || line.CurrentValue >= line.AverageValue;
        bool abovePrevious = line.CurrentValue >= line.PreviousValue;
        if (aboveAverage && abovePrevious)
        {
            return DesignTokens.Colors.QuotaGood;
        }

        if (aboveAverage)
        {
            return Color.FromArgb(160, 236, 184);
        }

        return abovePrevious ? DesignTokens.Colors.Warning : DesignTokens.Colors.WarningDeep;
    }

    private float MapValueToY(double value, double min, double max, RectangleF rect)
    {
        double ratio = (value - min) / Math.Max(0.0001, max - min);
        ratio = Math.Max(0.0, Math.Min(1.0, ratio));
        return rect.Bottom - (float)(ratio * rect.Height);
    }

    private Color GetEfficiencyColor(int value, bool token)
    {
        if (value < 80)
        {
            return token ? Color.FromArgb(255, 124, 138) : Color.FromArgb(196, 132, 255);
        }

        if (value > 100)
        {
            return token ? DesignTokens.Colors.Warning : Color.FromArgb(255, 235, 132);
        }

        return DesignTokens.Colors.QuotaGood;
    }

    private Color GetIqColor(ClaudeRadarModelMetric metric)
    {
        if (metric == null || !metric.Known)
        {
            return DesignTokens.Colors.GlyphMuted;
        }

        if (metric.IqScore < metric.NormalLow)
        {
            return DesignTokens.Colors.Danger;
        }

        if (metric.IqScore > metric.NormalHigh)
        {
            return DesignTokens.Colors.Warning;
        }

        return DesignTokens.Colors.QuotaGood;
    }

    private Color GetQuotaNumberColor(bool known)
    {
        if (!known)
        {
            return DesignTokens.Colors.GlyphMuted;
        }

        SoftwareRuntimePresenceSnapshot presence = GetLastRuntimePresenceSnapshot();
        return presence.AnySupportedAppRunning ? DesignTokens.White(246) : DesignTokens.Colors.GlyphMuted;
    }

    private Color GetServiceColor(ClaudeRadarServiceState state)
    {
        switch (state)
        {
            case ClaudeRadarServiceState.Normal:
                return DesignTokens.Colors.QuotaGood;
            case ClaudeRadarServiceState.Offline:
            case ClaudeRadarServiceState.Incomplete:
                return DesignTokens.Colors.GlyphMuted;
            case ClaudeRadarServiceState.Unavailable:
                return DesignTokens.Colors.Warning;
            case ClaudeRadarServiceState.Unreachable:
                return DesignTokens.Colors.Danger;
            default:
                return DesignTokens.Colors.SubtleText;
        }
    }

    private static string NotificationStatePath
    {
        get { return Path.Combine(Logger.DirectoryPath, "claude-radar-notification-state.ini"); }
    }

    private void LoadNotificationState()
    {
        this.notificationState.Clear();
        try
        {
            if (!File.Exists(NotificationStatePath))
            {
                return;
            }

            string[] lines = File.ReadAllLines(NotificationStatePath, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (key.Length > 0)
                {
                    this.notificationState[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void SaveNotificationState()
    {
        try
        {
            Directory.CreateDirectory(Logger.DirectoryPath);
            List<string> lines = new List<string>();
            lines.Add("# source_key=event_state");
            foreach (KeyValuePair<string, string> pair in this.notificationState)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key))
                {
                    lines.Add(pair.Key.Trim() + "=" + (pair.Value ?? string.Empty).Trim());
                }
            }

            File.WriteAllLines(NotificationStatePath, lines.ToArray(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private void ShowModelCatalogNotifications(List<ClaudeRadarModelCatalogEvent> events)
    {
        if (events == null || events.Count == 0)
        {
            return;
        }

        List<ClaudeRadarModelCatalogEvent> emitted = ApplyModelCatalogNotificationState(events, this.notificationState);
        for (int i = 0; i < emitted.Count; i++)
        {
            ShowModelCatalogNotification(emitted[i]);
        }

        if (emitted.Count > 0)
        {
            SaveNotificationState();
        }
    }

    private static List<ClaudeRadarModelCatalogEvent> ApplyModelCatalogNotificationState(
        List<ClaudeRadarModelCatalogEvent> events,
        Dictionary<string, string> state)
    {
        List<ClaudeRadarModelCatalogEvent> emitted = new List<ClaudeRadarModelCatalogEvent>();
        if (events == null || state == null)
        {
            return emitted;
        }

        for (int i = 0; i < events.Count; i++)
        {
            ClaudeRadarModelCatalogEvent ev = events[i];
            if (ev == null || string.IsNullOrWhiteSpace(ev.SourceKey))
            {
                continue;
            }

            string key = NormalizeNotificationStateKey(ev.SourceKey);
            if (key.Length == 0)
            {
                continue;
            }

            string eventState = BuildNotificationEventState(ev);
            string previous;
            if (state.TryGetValue(key, out previous) &&
                string.Equals(previous, eventState, StringComparison.Ordinal))
            {
                continue;
            }

            state[key] = eventState;
            emitted.Add(ev);
        }

        return emitted;
    }

    private static string NormalizeNotificationStateKey(string sourceKey)
    {
        return string.IsNullOrWhiteSpace(sourceKey)
            ? string.Empty
            : sourceKey.Trim().ToLowerInvariant();
    }

    private static string BuildNotificationEventState(ClaudeRadarModelCatalogEvent ev)
    {
        if (ev == null)
        {
            return string.Empty;
        }

        return ev.Kind.ToString() + "|" + ((ev.Status ?? string.Empty).Trim());
    }

    private void ShowModelCatalogNotification(ClaudeRadarModelCatalogEvent ev)
    {
        string name = string.IsNullOrWhiteSpace(ev.DisplayName) ? ev.SourceKey : ev.DisplayName;
        switch (ev.Kind)
        {
            case ClaudeRadarModelCatalogEventKind.Added:
                ShowClaudeNotification("Claude Radar 新模型", name + " 已加入检测列表。", ToolTipIcon.Info);
                break;
            case ClaudeRadarModelCatalogEventKind.Reappeared:
                ShowClaudeNotification("Claude Radar 模型恢复", name + " 已重新出现在网站模型列表中。", ToolTipIcon.Info);
                break;
            case ClaudeRadarModelCatalogEventKind.TemporarilyMissing:
                ShowClaudeNotification("Claude Radar 模型暂不可用", name + " 本次未出现在完整网站目录中，暂时保留。", ToolTipIcon.Warning);
                break;
            case ClaudeRadarModelCatalogEventKind.Deleted:
                ShowClaudeNotification("Claude Radar 模型已删除", name + " 连续多次未出现在完整网站目录中，已禁用。", ToolTipIcon.Warning);
                break;
        }
    }

    private void ShowClaudeNotification(string title, string message, ToolTipIcon icon)
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

    private void DrawSoftwareBorder(Graphics g)
    {
        float stroke = Math.Max(1.0f, S(3));
        RectangleF rect = new RectangleF(stroke / 2.0f, stroke / 2.0f, this.Width - stroke, this.Height - stroke);
        using (GraphicsPath path = RoundedRectangle(rect, S(DesignTokens.Radius.Panel)))
        using (Pen pen = new Pen(DesignTokens.WithAlpha(ClaudeOrange, 205), stroke))
        {
            g.DrawPath(pen, path);
        }
    }

    private void RenderLayeredWindow()
    {
        RenderLayeredWindow(true);
    }

    private void RenderLayeredWindow(bool redrawContent)
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0 || this.displaySuspended)
        {
            return;
        }

        try
        {
            EnsureRenderBuffer();
            bool burnInColorProtectionActive = IsBurnInColorProtectionActive();
            bool refreshNativeBitmap =
                redrawContent ||
                !this.renderBufferValid ||
                burnInColorProtectionActive != this.lastRenderedBurnInColorProtectionActive;
            if (refreshNativeBitmap)
            {
                string sceneCacheKey = BuildRenderSceneCacheKey(burnInColorProtectionActive);
                Bitmap cachedScene;
                if (this.renderSceneBitmapCache.TryGetValue(sceneCacheKey, out cachedScene) &&
                    cachedScene != null &&
                    cachedScene.Width == this.Width &&
                    cachedScene.Height == this.Height)
                {
                    this.renderGraphics.Clear(Color.Transparent);
                    this.renderGraphics.DrawImageUnscaled(cachedScene, 0, 0);
                }
                else
                {
                    this.renderGraphics.Clear(Color.Transparent);
                    DrawWindow(this.renderGraphics);
                    if (burnInColorProtectionActive)
                    {
                        BurnInProtection.ApplyHiddenModeColorProtection(this.renderBitmap);
                    }

                    StoreRenderSceneCache(sceneCacheKey);
                }

                this.lastRenderedBurnInColorProtectionActive = burnInColorProtectionActive;
                this.renderBufferValid = true;
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
                    Program.LogInfo("ClaudeRadar UpdateLayeredWindow failed; falling back to normal paint.");
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
        this.renderBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb);
        this.renderGraphics = Graphics.FromImage(this.renderBitmap);
        this.renderBufferValid = false;
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

    private void DisposeSceneCache()
    {
        foreach (Bitmap bitmap in this.renderSceneBitmapCache.Values)
        {
            if (bitmap != null)
            {
                bitmap.Dispose();
            }
        }

        this.renderSceneBitmapCache.Clear();
        this.renderSceneBitmapCacheOrder.Clear();
    }

    private void StoreRenderSceneCache(string cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey) || this.renderBitmap == null)
        {
            return;
        }

        Bitmap old;
        if (this.renderSceneBitmapCache.TryGetValue(cacheKey, out old))
        {
            if (old != null)
            {
                old.Dispose();
            }

            this.renderSceneBitmapCache[cacheKey] = (Bitmap)this.renderBitmap.Clone();
            return;
        }

        this.renderSceneBitmapCache[cacheKey] = (Bitmap)this.renderBitmap.Clone();
        this.renderSceneBitmapCacheOrder.Enqueue(cacheKey);
        while (this.renderSceneBitmapCache.Count > MaxSceneCacheEntries && this.renderSceneBitmapCacheOrder.Count > 0)
        {
            string evictKey = this.renderSceneBitmapCacheOrder.Dequeue();
            Bitmap evicted;
            if (this.renderSceneBitmapCache.TryGetValue(evictKey, out evicted))
            {
                if (evicted != null)
                {
                    evicted.Dispose();
                }

                this.renderSceneBitmapCache.Remove(evictKey);
            }
        }
    }

    private string BuildRenderSceneCacheKey(bool burnInColorProtectionActive)
    {
        ClaudeRadarSnapshot local = this.snapshot == null
            ? ClaudeRadarSnapshot.CreateDefault()
            : this.snapshot;
        ClaudeRadarModelMetric metric = local.SelectedModel ?? ClaudeRadarModelMetric.CreateDefault();
        ClaudeRadarQuotaSnapshot quota = local.Quota ?? ClaudeRadarQuotaSnapshot.CreateDefault();
        ClaudeRadarQuotaLineSnapshot quotaLine = local.QuotaLine ?? ClaudeRadarQuotaLineSnapshot.CreateDefault();
        ClaudeRadarCommunitySnapshot community = local.Community ?? ClaudeRadarCommunitySnapshot.CreateDefault();
        SoftwareRuntimePresenceSnapshot presence = GetLastRuntimePresenceSnapshot();
        return string.Join(
            "|",
            new string[]
            {
                this.Width.ToString(CultureInfo.InvariantCulture),
                this.Height.ToString(CultureInfo.InvariantCulture),
                this.currentSettings.ClaudeRadarRenderVariant.ToString(),
                GetBackgroundOpacityAlpha().ToString(CultureInfo.InvariantCulture),
                GetContentOpacityAlpha().ToString(CultureInfo.InvariantCulture),
                burnInColorProtectionActive ? "burn1" : "burn0",
                presence.AnySupportedAppRunning ? "run1" : "run0",
                local.RequestRunning ? "req1" : "req0",
                (this.renderTickCount % 4).ToString(CultureInfo.InvariantCulture),
                local.TestMode ? "test1" : "test0",
                local.DataState.ToString(),
                local.RatingsState.ToString(),
                local.ClaudeStatusState.ToString(),
                local.ClaudeCodeState.ToString(),
                local.SelectedModelKey ?? string.Empty,
                local.SelectedModelName ?? string.Empty,
                metric.Known ? "mk1" : "mk0",
                metric.IqScore.ToString(CultureInfo.InvariantCulture),
                metric.TokenEfficiencyPercent.ToString(CultureInfo.InvariantCulture),
                metric.TimeEfficiencyPercent.ToString(CultureInfo.InvariantCulture),
                metric.StatusText ?? string.Empty,
                quota.Known ? "q1" : "q0",
                quota.FiveHourPercent.ToString(CultureInfo.InvariantCulture),
                quota.WeeklyPercent.ToString(CultureInfo.InvariantCulture),
                quota.FiveHourResetText ?? string.Empty,
                quota.WeeklyResetText ?? string.Empty,
                quotaLine.Known ? "l1" : "l0",
                quotaLine.CurrentValue.ToString("0.###", CultureInfo.InvariantCulture),
                quotaLine.PreviousKnown ? "lp1" : "lp0",
                quotaLine.PreviousValue.ToString("0.###", CultureInfo.InvariantCulture),
                quotaLine.MinValue.ToString("0.###", CultureInfo.InvariantCulture),
                quotaLine.MaxValue.ToString("0.###", CultureInfo.InvariantCulture),
                quotaLine.AverageValue.ToString("0.###", CultureInfo.InvariantCulture),
                quotaLine.SourceMode ?? string.Empty,
                community.Known ? "c1" : "c0",
                community.RatingKey ?? string.Empty,
                community.Label ?? string.Empty,
                community.Average.ToString("0.###", CultureInfo.InvariantCulture),
                local.CheckedAtLocal == DateTime.MinValue ? string.Empty : local.CheckedAtLocal.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            });
    }

    private bool IsBurnInColorProtectionActive()
    {
        return BurnInProtection.ShouldApplyHiddenModeColorProtection(this.currentSettings, false);
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ClaudeRadarTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private byte GetApplicationOpacityAlpha()
    {
        return 255;
    }

    private void ConfigureGraphics(Graphics g)
    {
        BurnInProtection.ConfigureGraphics(g, IsBurnInColorProtectionActive());
    }

    private int S(float value)
    {
        return Math.Max(1, (int)Math.Round(value * this.scale));
    }

    private static RectangleF Inflate(RectangleF rect, float amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }

    private static GraphicsPath RoundedRectangle(RectangleF rect, float radius)
    {
        float d = Math.Max(0.0f, radius * 2.0f);
        GraphicsPath path = new GraphicsPath();
        if (d <= 0.0f)
        {
            path.AddRectangle(rect);
            path.CloseFigure();
            return path;
        }

        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string FormatLocalTime(DateTime local)
    {
        if (local == DateTime.MinValue)
        {
            return "--:--";
        }

        return local.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    internal static void RenderVariantSamples(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.ClaudeRadarEnabled = true;
        settings.ClaudeRadarRandomTestEnabled = false;
        settings.ClaudeRadarWidth = WidgetSettings.DefaultCodexRadarWidth;
        settings.ClaudeRadarHeight = 116;
        settings.Normalize();

        using (ClaudeRadarForm form = new ClaudeRadarForm(settings))
        {
            // scale=2 matches a real 200%-DPI display; ClaudeRadarWidth/Height are already the real
            // physical pixel size (see GetDesiredSize() - no separate DPI multiplication happens at
            // runtime), so the canvas must match them 1:1 or overflow/truncation that only appears at
            // the true width goes unnoticed (see CodexRadarForm.RenderSample.cs for the same fix).
            form.scale = 2.0f;
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.ClaudeRadarWidth, settings.ClaudeRadarHeight);
            ClaudeRadarSnapshot normal = BuildAcceptanceSnapshot("normal", 42);
            RenderAcceptanceSnapshot(form, normal, outputDir, "clauderadar-evenrow.png");
            RenderAcceptanceDesktopSnapshot(form, normal, outputDir, "clauderadar-2880x1800.png");

            string[] scenarios = new string[]
            {
                "normal",
                "missing-data",
                "warning",
                "error",
                "offline",
                "test-randomized"
            };

            for (int i = 0; i < scenarios.Length; i++)
            {
                ClaudeRadarSnapshot snapshot = BuildAcceptanceSnapshot(scenarios[i], 100 + i);
                RenderAcceptanceSnapshot(form, snapshot, outputDir, "clauderadar-" + scenarios[i] + ".png");
                RenderAcceptanceDesktopSnapshot(form, snapshot, outputDir, "clauderadar-2880x1800-" + scenarios[i] + ".png");
            }
        }
    }

    internal static void RunRenderResourceSelfTest()
    {
        RunNotificationStateSelfTest();
        RunRefreshStateSelfTest();
        RunBottomLabelSelfTest();

        WidgetSettings settings = WidgetSettings.CreateDefaults();
        settings.ClaudeRadarEnabled = true;
        settings.ClaudeRadarRandomTestEnabled = false;
        settings.ClaudeRadarWidth = WidgetSettings.DefaultCodexRadarWidth;
        settings.ClaudeRadarHeight = 116;
        settings.Normalize();

        using (ClaudeRadarForm form = new ClaudeRadarForm(settings))
        {
            form.scale = 1.0f;
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.ClaudeRadarWidth, settings.ClaudeRadarHeight);

            string[] scenarios = new string[]
            {
                "normal",
                "missing-data",
                "warning",
                "error",
                "offline",
                "test-randomized"
            };

            for (int i = 0; i < scenarios.Length; i++)
            {
                using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    form.snapshot = BuildAcceptanceSnapshot(scenarios[i], 200 + i);
                    form.DrawWindow(g);
                    if (!HasVisiblePixel(bitmap))
                    {
                        throw new InvalidOperationException("Claude Radar render self-test produced a blank " + scenarios[i] + " image.");
                    }
                }
            }

            form.EnsureRenderBuffer();
            for (int i = 0; i < MaxSceneCacheEntries + 3; i++)
            {
                form.snapshot = BuildAcceptanceSnapshot(scenarios[i % scenarios.Length], 300 + i);
                form.renderGraphics.Clear(Color.Transparent);
                form.DrawWindow(form.renderGraphics);
                form.StoreRenderSceneCache("self-test-" + i.ToString(CultureInfo.InvariantCulture));
            }

            if (form.renderSceneBitmapCache.Count > MaxSceneCacheEntries)
            {
                throw new InvalidOperationException("Claude Radar scene cache exceeded its configured entry limit.");
            }

            form.DisposeSceneCache();
            form.renderBufferValid = false;
            int drawCount = 0;
            int cacheHitCount = 0;
            for (int i = 0; i < 120; i++)
            {
                int scenarioIndex = i % scenarios.Length;
                form.snapshot = BuildAcceptanceSnapshot(scenarios[scenarioIndex], 500 + scenarioIndex);
                form.snapshot.CheckedAtUtc = new DateTime(2026, 7, 5, 0, scenarioIndex, 0, DateTimeKind.Utc);
                form.snapshot.CheckedAtLocal = form.snapshot.CheckedAtUtc.ToLocalTime();
                string cacheKey = form.BuildRenderSceneCacheKey(false);
                Bitmap cached;
                form.renderGraphics.Clear(Color.Transparent);
                if (form.renderSceneBitmapCache.TryGetValue(cacheKey, out cached) &&
                    cached != null &&
                    cached.Width == form.Width &&
                    cached.Height == form.Height)
                {
                    cacheHitCount++;
                    form.renderGraphics.DrawImageUnscaled(cached, 0, 0);
                }
                else
                {
                    drawCount++;
                    form.DrawWindow(form.renderGraphics);
                    form.StoreRenderSceneCache(cacheKey);
                }
            }

            if (form.renderSceneBitmapCache.Count > MaxSceneCacheEntries)
            {
                throw new InvalidOperationException("Claude Radar high-frequency scene cache exceeded its configured entry limit.");
            }

            if (drawCount != scenarios.Length || cacheHitCount != 120 - scenarios.Length)
            {
                throw new InvalidOperationException(
                    "Claude Radar high-frequency scene cache did not reuse warmed scenes. Draws=" +
                    drawCount.ToString(CultureInfo.InvariantCulture) +
                    ", Hits=" +
                    cacheHitCount.ToString(CultureInfo.InvariantCulture));
            }

            form.DisposeSceneCache();
            if (form.renderSceneBitmapCache.Count != 0 || form.renderSceneBitmapCacheOrder.Count != 0)
            {
                throw new InvalidOperationException("Claude Radar scene cache dispose did not clear all state.");
            }

            form.DisposeRenderBuffer();
            if (form.renderBitmap != null || form.renderGraphics != null || form.renderBufferValid)
            {
                throw new InvalidOperationException("Claude Radar render buffer dispose did not clear all state.");
            }
        }
    }

    private static void RunNotificationStateSelfTest()
    {
        Dictionary<string, string> state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        List<ClaudeRadarModelCatalogEvent> events = new List<ClaudeRadarModelCatalogEvent>();
        events.Add(new ClaudeRadarModelCatalogEvent
        {
            Kind = ClaudeRadarModelCatalogEventKind.Added,
            SourceKey = "Model-A",
            DisplayName = "Claude Model A",
            Status = "pending"
        });

        AssertClaudeRadarSelfTest(
            ApplyModelCatalogNotificationState(events, state).Count == 1,
            "first added model did not notify");
        events[0].DisplayName = "Renamed Claude Model A";
        AssertClaudeRadarSelfTest(
            ApplyModelCatalogNotificationState(events, state).Count == 0,
            "same source/state notified again after display-name change");

        Dictionary<string, string> restartedState = new Dictionary<string, string>(state, StringComparer.OrdinalIgnoreCase);
        AssertClaudeRadarSelfTest(
            ApplyModelCatalogNotificationState(events, restartedState).Count == 0,
            "same source/state notified again after restart state reload");

        events[0].Kind = ClaudeRadarModelCatalogEventKind.Deleted;
        events[0].Status = "deleted";
        AssertClaudeRadarSelfTest(
            ApplyModelCatalogNotificationState(events, restartedState).Count == 1,
            "deleted model state change did not notify");
        AssertClaudeRadarSelfTest(
            ApplyModelCatalogNotificationState(events, restartedState).Count == 0,
            "same deleted model state notified twice");

        events[0].Kind = ClaudeRadarModelCatalogEventKind.Added;
        events[0].Status = "active";
        AssertClaudeRadarSelfTest(
            ApplyModelCatalogNotificationState(events, restartedState).Count == 1,
            "deleted then re-added model did not notify");
    }

    private static void RunRefreshStateSelfTest()
    {
        ClaudeRadarSnapshot current = BuildAcceptanceSnapshot("normal", 410);
        current.RequestRunning = true;
        ClaudeRadarSnapshot failed = ClaudeRadarSnapshot.CreateDefault();
        failed.DataState = ClaudeRadarServiceState.Unreachable;
        failed.RatingsState = ClaudeRadarServiceState.Incomplete;
        failed.ClaudeStatusState = ClaudeRadarServiceState.Unavailable;
        failed.ClaudeCodeState = ClaudeRadarServiceState.Unknown;
        failed.ErrorCode = "TIMEOUT";
        failed.ErrorMessage = "请求超时";
        failed.CheckedAtUtc = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);
        failed.CheckedAtLocal = failed.CheckedAtUtc.ToLocalTime();

        ClaudeRadarSnapshot merged = BuildRefreshFailureDisplaySnapshot(current, failed);
        AssertClaudeRadarSelfTest(merged.Known, "failure merge did not preserve last known snapshot");
        AssertClaudeRadarSelfTest(!merged.RequestRunning, "failure merge did not clear request-running state");
        AssertClaudeRadarSelfTest(
            string.Equals(merged.SelectedModelKey, current.SelectedModelKey, StringComparison.Ordinal),
            "failure merge did not preserve selected model key");
        AssertClaudeRadarSelfTest(
            merged.Community != null &&
            current.Community != null &&
            merged.Community.Known == current.Community.Known &&
            string.Equals(merged.Community.RatingKey, current.Community.RatingKey, StringComparison.Ordinal) &&
            string.Equals(merged.Community.Label, current.Community.Label, StringComparison.Ordinal),
            "failure merge did not preserve bottom community metadata");
        AssertClaudeRadarSelfTest(
            merged.DataState == ClaudeRadarServiceState.Unreachable &&
            string.Equals(merged.ErrorCode, "TIMEOUT", StringComparison.Ordinal),
            "failure merge did not apply latest error state");

        bool running = false;
        AssertClaudeRadarSelfTest(TryBeginSingleFlight(ref running), "single-flight did not start");
        AssertClaudeRadarSelfTest(!TryBeginSingleFlight(ref running), "single-flight allowed duplicate start");
        CompleteSingleFlight(ref running);
        AssertClaudeRadarSelfTest(TryBeginSingleFlight(ref running), "single-flight did not restart after completion");
    }

    private static void RunBottomLabelSelfTest()
    {
        AssertClaudeRadarSelfTest(
            string.Equals(FormatClaudeRadarShortLabel(string.Empty, "Opus4.8High"), "Op4.8H", StringComparison.Ordinal),
            "bottom label did not abbreviate Opus high correctly");
        AssertClaudeRadarSelfTest(
            string.Equals(FormatClaudeRadarShortLabel(string.Empty, "Fable5max"), "Fa5MAX", StringComparison.Ordinal),
            "bottom label did not abbreviate Fable max correctly");
        AssertClaudeRadarSelfTest(
            string.Equals(FormatClaudeRadarShortLabel(string.Empty, "Sonnet 5 ultra"), "So5Ult", StringComparison.Ordinal),
            "bottom label did not abbreviate Ultra correctly");
    }

    private static void AssertClaudeRadarSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Claude Radar self-test failed: " + message);
        }
    }

    // Current-mode sample: real settings.ini plus the constructor-loaded disk caches
    // (claude-radar-cache.ini, claude-quota.ini), drawn through the real DrawWindow pipeline and
    // composited like the on-screen layered window. See RenderSampleSupport for the semantics.
    internal static void RenderCurrentSample(string outputDir)
    {
        WidgetSettings settings = WidgetSettings.Load();
        using (ClaudeRadarForm form = new ClaudeRadarForm(settings))
        {
            form.scale = 2.0f;
            form.MaximumSize = new Size(4000, 4000);
            form.Size = new Size(settings.ClaudeRadarWidth, settings.ClaudeRadarHeight);
            if (form.snapshot == null)
            {
                // Cold start with no disk cache yet: keep the frame reviewable instead of blank.
                form.snapshot = BuildAcceptanceSnapshot("normal", 42);
            }

            RenderSampleSupport.SaveComposited(
                outputDir,
                "clauderadar-current.png",
                form.Width,
                form.Height,
                form.GetApplicationOpacityAlpha(),
                form.DrawWindow);
        }
    }

    private static void RenderAcceptanceSnapshot(
        ClaudeRadarForm form,
        ClaudeRadarSnapshot snapshot,
        string outputDir,
        string fileName)
    {
        form.snapshot = snapshot == null ? ClaudeRadarSnapshot.CreateDefault() : snapshot.Clone();
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            form.DrawWindow(g);
            string path = Path.Combine(outputDir, fileName);
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine(
                "ClaudeRadar " +
                fileName +
                " -> " +
                path +
                " (" +
                form.Width.ToString(CultureInfo.InvariantCulture) +
                "x" +
                form.Height.ToString(CultureInfo.InvariantCulture) +
                ")");
        }
    }

    private static void RenderAcceptanceDesktopSnapshot(
        ClaudeRadarForm form,
        ClaudeRadarSnapshot snapshot,
        string outputDir,
        string fileName)
    {
        form.snapshot = snapshot == null ? ClaudeRadarSnapshot.CreateDefault() : snapshot.Clone();
        using (Bitmap desktop = new Bitmap(2880, 1800, PixelFormat.Format32bppPArgb))
        using (Graphics g = Graphics.FromImage(desktop))
        {
            g.Clear(Color.FromArgb(24, 25, 28));
            GraphicsState state = g.Save();
            g.TranslateTransform(1660, 360);
            form.DrawWindow(g);
            g.Restore(state);
            string desktopPath = Path.Combine(outputDir, fileName);
            desktop.Save(desktopPath, ImageFormat.Png);
            Console.WriteLine("ClaudeRadar desktop " + fileName + " -> " + desktopPath);
        }
    }

    private static ClaudeRadarSnapshot BuildAcceptanceSnapshot(string scenario, int seed)
    {
        ClaudeRadarSnapshot snapshot = ClaudeRadarReader.BuildRandomTestSnapshot(seed);
        snapshot.TestMode = false;
        snapshot.Known = true;
        snapshot.CheckedAtUtc = DateTime.UtcNow;
        snapshot.CheckedAtLocal = DateTime.Now;
        snapshot.SelectedModelKey = "opus48_high";
        snapshot.SelectedModelName = "Opus 4.8 high";
        snapshot.SelectedModel.SourceKey = snapshot.SelectedModelKey;
        snapshot.SelectedModel.Name = snapshot.SelectedModelName;
        snapshot.Community = new ClaudeRadarCommunitySnapshot
        {
            Known = true,
            RatingKey = "fable5_max",
            Label = "Fable 5 max",
            Average = 7.3,
            Count = 12,
            UpdatedAtUtc = DateTime.UtcNow,
            RefreshSeconds = 300
        };
        snapshot.DataState = ClaudeRadarServiceState.Normal;
        snapshot.RatingsState = ClaudeRadarServiceState.Normal;
        snapshot.ClaudeStatusState = ClaudeRadarServiceState.Normal;
        snapshot.ClaudeCodeState = ClaudeRadarServiceState.Normal;

        string normalized = string.IsNullOrWhiteSpace(scenario)
            ? "normal"
            : scenario.Trim().ToLowerInvariant();
        if (string.Equals(normalized, "test-randomized", StringComparison.Ordinal))
        {
            snapshot.TestMode = true;
            snapshot.SelectedModelKey = "test";
            snapshot.SelectedModelName = "Claude Test";
            return snapshot;
        }

        if (string.Equals(normalized, "missing-data", StringComparison.Ordinal))
        {
            snapshot.Known = false;
            snapshot.DataState = ClaudeRadarServiceState.Incomplete;
            snapshot.RatingsState = ClaudeRadarServiceState.Incomplete;
            snapshot.ClaudeStatusState = ClaudeRadarServiceState.Normal;
            snapshot.ClaudeCodeState = ClaudeRadarServiceState.Incomplete;
            snapshot.SelectedModelName = "数据缺失";
            snapshot.SelectedModel = ClaudeRadarModelMetric.CreateDefault();
            snapshot.SelectedModel.Name = "数据缺失";
            snapshot.Quota = ClaudeRadarQuotaSnapshot.CreateDefault();
            snapshot.QuotaLine = ClaudeRadarQuotaLineSnapshot.CreateDefault();
            snapshot.Community = ClaudeRadarCommunitySnapshot.CreateDefault();
            return snapshot;
        }

        if (string.Equals(normalized, "warning", StringComparison.Ordinal))
        {
            snapshot.ClaudeStatusState = ClaudeRadarServiceState.Unavailable;
            snapshot.ClaudeCodeState = ClaudeRadarServiceState.Normal;
            snapshot.SelectedModelName = "Claude Warning";
            snapshot.SelectedModel.IqScore = 82;
            snapshot.SelectedModel.Passed = 7;
            snapshot.SelectedModel.TokenEfficiencyPercent = 74;
            snapshot.SelectedModel.TimeEfficiencyPercent = 132;
            snapshot.SelectedModel.StatusText = "降智";
            snapshot.SelectedModel.EfficiencyText = "低效";
            snapshot.Quota.FiveHourPercent = 35;
            snapshot.Quota.WeeklyPercent = 64;
            snapshot.QuotaLine.CurrentValue = 1560;
            snapshot.QuotaLine.PreviousValue = 1880;
            snapshot.QuotaLine.PreviousKnown = true;
            snapshot.QuotaLine.MinValue = 1500;
            snapshot.QuotaLine.MaxValue = 1967;
            snapshot.QuotaLine.AverageValue = 1720;
            snapshot.QuotaLine.AverageKnown = true;
            snapshot.QuotaLine.Known = true;
            return snapshot;
        }

        if (string.Equals(normalized, "error", StringComparison.Ordinal))
        {
            snapshot.Known = false;
            snapshot.DataState = ClaudeRadarServiceState.Unreachable;
            snapshot.RatingsState = ClaudeRadarServiceState.Unreachable;
            snapshot.ClaudeStatusState = ClaudeRadarServiceState.Unreachable;
            snapshot.ClaudeCodeState = ClaudeRadarServiceState.Unreachable;
            snapshot.ErrorCode = "TIMEOUT";
            snapshot.ErrorMessage = "连接失败";
            snapshot.SelectedModelName = "连接失败";
            snapshot.SelectedModel = ClaudeRadarModelMetric.CreateDefault();
            snapshot.SelectedModel.Name = "连接失败";
            snapshot.Quota = ClaudeRadarQuotaSnapshot.CreateDefault();
            snapshot.QuotaLine = ClaudeRadarQuotaLineSnapshot.CreateDefault();
            snapshot.Community = ClaudeRadarCommunitySnapshot.CreateDefault();
            return snapshot;
        }

        if (string.Equals(normalized, "offline", StringComparison.Ordinal))
        {
            snapshot.Known = false;
            snapshot.DataState = ClaudeRadarServiceState.Offline;
            snapshot.RatingsState = ClaudeRadarServiceState.Offline;
            snapshot.ClaudeStatusState = ClaudeRadarServiceState.Offline;
            snapshot.ClaudeCodeState = ClaudeRadarServiceState.Offline;
            snapshot.SelectedModelName = "离线";
            snapshot.SelectedModel = ClaudeRadarModelMetric.CreateDefault();
            snapshot.SelectedModel.Name = "离线";
            snapshot.Quota = ClaudeRadarQuotaSnapshot.CreateDefault();
            snapshot.QuotaLine = ClaudeRadarQuotaLineSnapshot.CreateDefault();
            snapshot.Community = ClaudeRadarCommunitySnapshot.CreateDefault();
            return snapshot;
        }

        snapshot.SelectedModel.IqScore = 111;
        snapshot.SelectedModel.Passed = 8;
        snapshot.SelectedModel.ValidTasks = 10;
        snapshot.SelectedModel.TokenEfficiencyPercent = 118;
        snapshot.SelectedModel.TimeEfficiencyPercent = 106;
        snapshot.SelectedModel.StatusText = "常态";
        snapshot.SelectedModel.EfficiencyText = "高效";
        snapshot.Quota.FiveHourPercent = 88;
        snapshot.Quota.WeeklyPercent = 93;
        snapshot.Quota.FiveHourResetText = "20:30";
        snapshot.Quota.WeeklyResetText = "07/08";
        snapshot.QuotaLine.CurrentValue = 1840;
        snapshot.QuotaLine.PreviousKnown = true;
        snapshot.QuotaLine.PreviousValue = 1760;
        snapshot.QuotaLine.MinValue = 1506;
        snapshot.QuotaLine.MaxValue = 1967;
        snapshot.QuotaLine.AverageValue = 1735;
        snapshot.QuotaLine.AverageKnown = true;
        snapshot.QuotaLine.Known = true;
        return snapshot;
    }

    private static bool HasVisiblePixel(Bitmap bitmap)
    {
        if (bitmap == null)
        {
            return false;
        }

        int xStep = Math.Max(1, bitmap.Width / 24);
        int yStep = Math.Max(1, bitmap.Height / 12);
        for (int y = 0; y < bitmap.Height; y += yStep)
        {
            for (int x = 0; x < bitmap.Width; x += xStep)
            {
                if (bitmap.GetPixel(x, y).A > 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
