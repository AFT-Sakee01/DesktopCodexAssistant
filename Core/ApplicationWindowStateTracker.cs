using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

internal sealed class ApplicationWindowStateTracker : IDisposable
{
    private const int AssistantProcessCacheSeconds = 5;
    private const int MaxAssistantProcessCacheEntries = 128;
    private readonly IntPtr ownHandle;
    private readonly int currentProcessId;
    private readonly Dictionary<IntPtr, TrackedWindow> windows =
        new Dictionary<IntPtr, TrackedWindow>();
    private readonly WindowEventAccumulator pendingEvents = new WindowEventAccumulator();
    private readonly object processIdentitySync = new object();
    private readonly Dictionary<int, ProcessIdentityCacheEntry> processIdentityCache =
        new Dictionary<int, ProcessIdentityCacheEntry>();
    private NativeMethods.WindowEventHook hook;
    private bool disposed;

    public ApplicationWindowStateTracker(
        IntPtr ownHandle,
        NativeMethods.WindowEventHandler handler,
        bool includeObjectEvents)
    {
        this.ownHandle = ownHandle;
        this.currentProcessId = Process.GetCurrentProcess().Id;
        RefreshAll();
        this.hook = new NativeMethods.WindowEventHook(handler, includeObjectEvents);
        Program.LogInfo(
            "Application window state tracker started. HookActive=" +
            this.hook.IsActive.ToString() +
            ", ObjectEvents=" +
            this.hook.ObjectEventsEnabled.ToString());
    }

    public bool ObjectEventsEnabled
    {
        get
        {
            NativeMethods.WindowEventHook activeHook = this.hook;
            return activeHook != null && activeHook.ObjectEventsEnabled;
        }
    }

    public void SetObjectEventsEnabled(bool enabled)
    {
        NativeMethods.WindowEventHook activeHook = this.hook;
        if (this.disposed || activeHook == null || activeHook.ObjectEventsEnabled == enabled)
        {
            return;
        }

        if (!enabled)
        {
            this.pendingEvents.ClearObjectEvents();
        }
        else
        {
            RefreshAll();
        }

        activeHook.SetObjectEventsEnabled(enabled);
        Program.LogInfo("Application window object events enabled=" + activeHook.ObjectEventsEnabled.ToString() + ".");
    }

    public WindowEventQueueResult QueueWindowEvent(
        uint eventId,
        IntPtr windowHandle,
        int maximumPendingEvents,
        out int pendingCount)
    {
        pendingCount = 0;
        if (this.disposed || windowHandle == IntPtr.Zero)
        {
            return WindowEventQueueResult.Ignored;
        }

        bool foreground = eventId == NativeMethods.EVENT_SYSTEM_FOREGROUND;
        if (!foreground && !ObjectEventsEnabled)
        {
            return WindowEventQueueResult.Ignored;
        }

        if (IsSiblingAssistantWindow(windowHandle))
        {
            return WindowEventQueueResult.Ignored;
        }

        return this.pendingEvents.Enqueue(
            eventId,
            windowHandle,
            Math.Max(1, maximumPendingEvents),
            out pendingCount);
    }

    public PendingWindowEventBatch DrainPendingWindowEvents(int maximumEvents)
    {
        return this.pendingEvents.Drain(Math.Max(1, maximumEvents));
    }

    public void ClearPendingWindowEvents()
    {
        this.pendingEvents.ClearAll();
    }

    public void RefreshAll()
    {
        List<NativeMethods.ApplicationWindowInfo> current =
            NativeMethods.EnumerateApplicationWindows(this.ownHandle);
        Dictionary<IntPtr, TrackedWindow> next = new Dictionary<IntPtr, TrackedWindow>();
        long baseTicks = DateTime.UtcNow.Ticks;
        for (int i = 0; i < current.Count; i++)
        {
            NativeMethods.ApplicationWindowInfo info = current[i];
            TrackedWindow previous;
            long foregroundTicks = this.windows.TryGetValue(info.Handle, out previous)
                ? previous.LastForegroundTicks
                : baseTicks - i;
            next[info.Handle] = new TrackedWindow(info, foregroundTicks);
        }

        this.windows.Clear();
        foreach (KeyValuePair<IntPtr, TrackedWindow> pair in next)
        {
            this.windows[pair.Key] = pair.Value;
        }
    }

    public void ProcessWindowEvent(uint eventId, IntPtr windowHandle)
    {
        if (this.disposed)
        {
            return;
        }

        if (eventId == NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            RefreshWindow(windowHandle, true);
            return;
        }

        if (eventId == NativeMethods.EVENT_OBJECT_DESTROY ||
            eventId == NativeMethods.EVENT_OBJECT_HIDE ||
            eventId == NativeMethods.EVENT_OBJECT_CLOAKED)
        {
            this.windows.Remove(windowHandle);
            return;
        }

        RefreshWindow(windowHandle, false);
    }

    public bool IsTopWindowFullscreenOnScreen(Rectangle screenBounds)
    {
        TrackedWindow top = GetTopWindowOnScreen(screenBounds);
        return top != null && top.Info.IsFullscreen && !top.Info.IsMinimized;
    }

    public bool HasMaximizedOrFullscreenWindow(Rectangle screenBounds)
    {
        foreach (TrackedWindow window in this.windows.Values)
        {
            if (!window.Info.IsMinimized &&
                SameScreen(window.Info.ScreenBounds, screenBounds) &&
                (window.Info.IsMaximized || window.Info.IsFullscreen))
            {
                return true;
            }
        }

        return false;
    }

    public bool HasMaximizedOrFullscreenWindow()
    {
        foreach (TrackedWindow window in this.windows.Values)
        {
            if (!window.Info.IsMinimized && (window.Info.IsMaximized || window.Info.IsFullscreen))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsWindowOverlapped(Rectangle formBounds, Rectangle screenBounds)
    {
        foreach (TrackedWindow window in this.windows.Values)
        {
            if (!window.Info.IsMinimized &&
                SameScreen(window.Info.ScreenBounds, screenBounds) &&
                window.Info.Bounds.IntersectsWith(formBounds))
            {
                return true;
            }
        }

        return false;
    }

    public bool ShouldHideForVisibilityMode(
        WidgetVisibilityMode mode,
        Rectangle formBounds,
        Rectangle screenBounds,
        bool ignoreOverlapForTarget)
    {
        switch (mode)
        {
            case WidgetVisibilityMode.AlwaysVisible:
            case WidgetVisibilityMode.DesktopOnly:
                return false;
            case WidgetVisibilityMode.HideWhenFullscreen:
                return IsTopWindowFullscreenOnScreen(screenBounds);
            case WidgetVisibilityMode.HideWhenMaximized:
                return HasMaximizedOrFullscreenWindow(screenBounds);
            case WidgetVisibilityMode.HideWhenOverlapped:
                if (ignoreOverlapForTarget)
                {
                    return false;
                }

                return IsWindowOverlapped(formBounds, screenBounds);
            default:
                return false;
        }
    }

    public bool ShouldHideForVisibilityMode(
        WidgetVisibilityMode mode,
        Rectangle formBounds,
        Rectangle screenBounds)
    {
        return ShouldHideForVisibilityMode(mode, formBounds, screenBounds, false);
    }

    internal static bool ShouldHideForVisibilityMode(
        WidgetVisibilityMode mode,
        Rectangle formBounds,
        Rectangle screenBounds,
        IList<NativeMethods.ApplicationWindowInfo> windows,
        bool ignoreOverlapForTarget)
    {
        switch (mode)
        {
            case WidgetVisibilityMode.AlwaysVisible:
            case WidgetVisibilityMode.DesktopOnly:
                return false;
            case WidgetVisibilityMode.HideWhenFullscreen:
                NativeMethods.ApplicationWindowInfo top = GetTopWindowOnScreen(screenBounds, windows);
                return top != null && !top.IsMinimized && top.IsFullscreen;
            case WidgetVisibilityMode.HideWhenMaximized:
                return HasMaximizedOrFullscreenWindow(screenBounds, windows);
            case WidgetVisibilityMode.HideWhenOverlapped:
                if (ignoreOverlapForTarget)
                {
                    return false;
                }

                return IsWindowOverlapped(formBounds, screenBounds, windows);
            default:
                return false;
        }
    }

    internal static bool ShouldHideForVisibilityMode(
        WidgetVisibilityMode mode,
        Rectangle formBounds,
        Rectangle screenBounds,
        IList<NativeMethods.ApplicationWindowInfo> windows)
    {
        return ShouldHideForVisibilityMode(mode, formBounds, screenBounds, windows, false);
    }

    internal static void RunSelfTest()
    {
        RunWindowEventAccumulatorSelfTest();
        Rectangle screen = new Rectangle(0, 0, 1920, 1080);
        Rectangle widget = new Rectangle(20, 20, 200, 120);
        Rectangle otherScreen = new Rectangle(1920, 0, 1920, 1080);
        List<NativeMethods.ApplicationWindowInfo> none =
            new List<NativeMethods.ApplicationWindowInfo>();
        List<NativeMethods.ApplicationWindowInfo> fullscreen =
            new List<NativeMethods.ApplicationWindowInfo>
            {
                NewTestWindow(screen, screen, false, false, true)
            };
        List<NativeMethods.ApplicationWindowInfo> maximized =
            new List<NativeMethods.ApplicationWindowInfo>
            {
                NewTestWindow(new Rectangle(0, 0, 1920, 1040), screen, false, true, false)
            };
        List<NativeMethods.ApplicationWindowInfo> overlapped =
            new List<NativeMethods.ApplicationWindowInfo>
            {
                NewTestWindow(new Rectangle(10, 10, 80, 80), screen, false, false, false)
            };
        List<NativeMethods.ApplicationWindowInfo> notOverlapped =
            new List<NativeMethods.ApplicationWindowInfo>
            {
                NewTestWindow(new Rectangle(400, 400, 120, 120), screen, false, false, false)
            };
        List<NativeMethods.ApplicationWindowInfo> offscreen =
            new List<NativeMethods.ApplicationWindowInfo>
            {
                NewTestWindow(otherScreen, otherScreen, false, true, true)
            };

        AssertSelfTest(!ShouldHideForVisibilityMode(WidgetVisibilityMode.AlwaysVisible, widget, screen, fullscreen), "AlwaysVisible hid on fullscreen");
        AssertSelfTest(ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenFullscreen, widget, screen, fullscreen), "HideWhenFullscreen did not hide fullscreen");
        AssertSelfTest(!ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenFullscreen, widget, screen, maximized), "HideWhenFullscreen hid maximized");
        AssertSelfTest(ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenMaximized, widget, screen, maximized), "HideWhenMaximized did not hide maximized");
        AssertSelfTest(ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenMaximized, widget, screen, fullscreen), "HideWhenMaximized did not include fullscreen");
        AssertSelfTest(ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenOverlapped, widget, screen, overlapped), "HideWhenOverlapped did not hide intersecting window");
        AssertSelfTest(ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenOverlapped, widget, screen, maximized), "HideWhenOverlapped did not hide intersecting maximized window");
        AssertSelfTest(!ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenOverlapped, widget, screen, overlapped, true), "Operation-panel overlap ignore still hid on intersecting window");
        AssertSelfTest(ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenFullscreen, widget, screen, fullscreen, true), "Operation-panel overlap ignore affected fullscreen mode");
        AssertSelfTest(!ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenOverlapped, widget, screen, notOverlapped), "HideWhenOverlapped hid non-intersecting window");
        AssertSelfTest(!ShouldHideForVisibilityMode(WidgetVisibilityMode.DesktopOnly, widget, screen, fullscreen), "DesktopOnly should not hide; z-order handles desktop-only visibility");
        AssertSelfTest(!ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenMaximized, widget, screen, offscreen), "Visibility decision crossed monitor boundary");
        AssertSelfTest(!ShouldHideForVisibilityMode(WidgetVisibilityMode.HideWhenFullscreen, widget, screen, none), "Empty window list hid widget");

        AssertSelfTest(
            !NativeMethods.IsApplicationWindowFullscreenForState(NativeMethods.WS_THICKFRAME, screen, screen),
            "Thick-frame maximized window was classified as fullscreen");
        AssertSelfTest(
            NativeMethods.IsApplicationWindowFullscreenForState(0, screen, screen),
            "Borderless screen-covering window was not classified as fullscreen");
    }

    private static void RunWindowEventAccumulatorSelfTest()
    {
        WindowEventAccumulator accumulator = new WindowEventAccumulator();
        int pending;
        AssertSelfTest(
            accumulator.Enqueue(NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, new IntPtr(11), 2, out pending) == WindowEventQueueResult.Added &&
            pending == 1,
            "Window event accumulator did not queue the first object event.");
        AssertSelfTest(
            accumulator.Enqueue(NativeMethods.EVENT_OBJECT_STATECHANGE, new IntPtr(11), 2, out pending) == WindowEventQueueResult.Coalesced &&
            pending == 1,
            "Window event accumulator did not coalesce the same HWND.");
        accumulator.Enqueue(NativeMethods.EVENT_SYSTEM_FOREGROUND, new IntPtr(21), 2, out pending);
        AssertSelfTest(
            accumulator.Enqueue(NativeMethods.EVENT_SYSTEM_FOREGROUND, new IntPtr(22), 2, out pending) == WindowEventQueueResult.Coalesced &&
            pending == 2,
            "Window event accumulator did not retain only the latest foreground HWND.");
        accumulator.Enqueue(NativeMethods.EVENT_OBJECT_SHOW, new IntPtr(12), 2, out pending);
        AssertSelfTest(
            accumulator.Enqueue(NativeMethods.EVENT_OBJECT_CREATE, new IntPtr(13), 2, out pending) == WindowEventQueueResult.Overflowed,
            "Window event accumulator did not collapse overflow into a full refresh.");

        PendingWindowEventBatch overflow = accumulator.Drain(1);
        AssertSelfTest(
            overflow.FullRefresh && overflow.HasForegroundEvent && overflow.ForegroundWindowHandle == new IntPtr(22) &&
            overflow.Events.Count == 0 && overflow.RemainingCount == 0,
            "Window event overflow batch did not preserve the latest foreground event.");

        accumulator.Enqueue(NativeMethods.EVENT_OBJECT_SHOW, new IntPtr(31), 3, out pending);
        accumulator.Enqueue(NativeMethods.EVENT_OBJECT_HIDE, new IntPtr(32), 3, out pending);
        PendingWindowEventBatch first = accumulator.Drain(1);
        PendingWindowEventBatch second = accumulator.Drain(1);
        AssertSelfTest(
            first.Events.Count == 1 && first.RemainingCount == 1 &&
            second.Events.Count == 1 && second.RemainingCount == 0,
            "Window event accumulator did not enforce bounded batches.");
    }

    private bool IsSiblingAssistantWindow(IntPtr windowHandle)
    {
        int processId;
        if (!NativeMethods.TryGetWindowProcessId(windowHandle, out processId) || processId <= 0)
        {
            return false;
        }

        if (processId == this.currentProcessId)
        {
            return true;
        }

        DateTime nowUtc = DateTime.UtcNow;
        lock (this.processIdentitySync)
        {
            ProcessIdentityCacheEntry cached;
            if (this.processIdentityCache.TryGetValue(processId, out cached) && cached.ExpiresUtc > nowUtc)
            {
                return cached.IsAssistant;
            }
        }

        bool isAssistant = false;
        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                string processName = process.ProcessName ?? string.Empty;
                isAssistant = processName.StartsWith(ProductIdentity.MachineName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
        }

        lock (this.processIdentitySync)
        {
            if (this.processIdentityCache.Count >= MaxAssistantProcessCacheEntries)
            {
                this.processIdentityCache.Clear();
            }

            this.processIdentityCache[processId] = new ProcessIdentityCacheEntry(
                isAssistant,
                nowUtc.AddSeconds(AssistantProcessCacheSeconds));
        }

        return isAssistant;
    }

    private void RefreshWindow(IntPtr windowHandle, bool foreground)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.ApplicationWindowInfo info;
        if (!NativeMethods.TryGetApplicationWindowInfo(windowHandle, this.ownHandle, out info))
        {
            this.windows.Remove(windowHandle);
            return;
        }

        TrackedWindow previous;
        long foregroundTicks = foreground
            ? DateTime.UtcNow.Ticks
            : (this.windows.TryGetValue(windowHandle, out previous)
                ? previous.LastForegroundTicks
                : 0L);
        if (foregroundTicks == 0L)
        {
            foregroundTicks = DateTime.UtcNow.Ticks - this.windows.Count - 1L;
        }

        this.windows[windowHandle] = new TrackedWindow(info, foregroundTicks);
    }

    private TrackedWindow GetTopWindowOnScreen(Rectangle screenBounds)
    {
        TrackedWindow top = null;
        foreach (TrackedWindow window in this.windows.Values)
        {
            if (window.Info.IsMinimized ||
                !SameScreen(window.Info.ScreenBounds, screenBounds))
            {
                continue;
            }

            if (top == null || window.LastForegroundTicks > top.LastForegroundTicks)
            {
                top = window;
            }
        }

        return top;
    }

    private static bool HasMaximizedOrFullscreenWindow(
        Rectangle screenBounds,
        IList<NativeMethods.ApplicationWindowInfo> windows)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            NativeMethods.ApplicationWindowInfo window = windows[i];
            if (!window.IsMinimized &&
                SameScreen(window.ScreenBounds, screenBounds) &&
                (window.IsMaximized || window.IsFullscreen))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWindowOverlapped(
        Rectangle formBounds,
        Rectangle screenBounds,
        IList<NativeMethods.ApplicationWindowInfo> windows)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            NativeMethods.ApplicationWindowInfo window = windows[i];
            if (!window.IsMinimized &&
                SameScreen(window.ScreenBounds, screenBounds) &&
                window.Bounds.IntersectsWith(formBounds))
            {
                return true;
            }
        }

        return false;
    }

    private static NativeMethods.ApplicationWindowInfo GetTopWindowOnScreen(
        Rectangle screenBounds,
        IList<NativeMethods.ApplicationWindowInfo> windows)
    {
        for (int i = 0; i < windows.Count; i++)
        {
            NativeMethods.ApplicationWindowInfo window = windows[i];
            if (!window.IsMinimized && SameScreen(window.ScreenBounds, screenBounds))
            {
                return window;
            }
        }

        return null;
    }

    private static bool SameScreen(Rectangle left, Rectangle right)
    {
        return left.Left == right.Left &&
            left.Top == right.Top &&
            left.Width == right.Width &&
            left.Height == right.Height;
    }

    private static NativeMethods.ApplicationWindowInfo NewTestWindow(
        Rectangle bounds,
        Rectangle screenBounds,
        bool minimized,
        bool maximized,
        bool fullscreen)
    {
        return new NativeMethods.ApplicationWindowInfo
        {
            Handle = IntPtr.Zero,
            ProcessId = 1,
            Title = "test",
            ClassName = "TestWindow",
            Bounds = bounds,
            ScreenBounds = screenBounds,
            IsMinimized = minimized,
            IsMaximized = maximized,
            IsFullscreen = fullscreen
        };
    }

    private static void AssertSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        NativeMethods.WindowEventHook activeHook = this.hook;
        this.hook = null;
        if (activeHook != null)
        {
            activeHook.Dispose();
        }

        this.pendingEvents.ClearAll();
        lock (this.processIdentitySync)
        {
            this.processIdentityCache.Clear();
        }

        this.windows.Clear();
    }

    internal enum WindowEventQueueResult
    {
        Ignored,
        Added,
        Coalesced,
        Overflowed
    }

    internal struct PendingWindowEvent
    {
        public PendingWindowEvent(uint eventId, IntPtr windowHandle)
        {
            this.EventId = eventId;
            this.WindowHandle = windowHandle;
        }

        public readonly uint EventId;
        public readonly IntPtr WindowHandle;
    }

    internal sealed class PendingWindowEventBatch
    {
        public PendingWindowEventBatch(
            bool fullRefresh,
            bool hasForegroundEvent,
            IntPtr foregroundWindowHandle,
            List<PendingWindowEvent> events,
            int remainingCount)
        {
            this.FullRefresh = fullRefresh;
            this.HasForegroundEvent = hasForegroundEvent;
            this.ForegroundWindowHandle = foregroundWindowHandle;
            this.Events = events ?? new List<PendingWindowEvent>();
            this.RemainingCount = remainingCount;
        }

        public bool FullRefresh { get; private set; }
        public bool HasForegroundEvent { get; private set; }
        public IntPtr ForegroundWindowHandle { get; private set; }
        public List<PendingWindowEvent> Events { get; private set; }
        public int RemainingCount { get; private set; }
    }

    private sealed class WindowEventAccumulator
    {
        private readonly object syncRoot = new object();
        private readonly Dictionary<IntPtr, uint> objectEvents = new Dictionary<IntPtr, uint>();
        private bool fullRefreshPending;
        private bool foregroundPending;
        private IntPtr foregroundWindowHandle;

        public WindowEventQueueResult Enqueue(
            uint eventId,
            IntPtr windowHandle,
            int maximumPendingEvents,
            out int pendingCount)
        {
            lock (this.syncRoot)
            {
                if (eventId == NativeMethods.EVENT_SYSTEM_FOREGROUND)
                {
                    bool coalesced = this.foregroundPending;
                    this.foregroundPending = true;
                    this.foregroundWindowHandle = windowHandle;
                    pendingCount = GetPendingCount();
                    return coalesced ? WindowEventQueueResult.Coalesced : WindowEventQueueResult.Added;
                }

                if (this.fullRefreshPending)
                {
                    pendingCount = GetPendingCount();
                    return WindowEventQueueResult.Coalesced;
                }

                if (this.objectEvents.ContainsKey(windowHandle))
                {
                    this.objectEvents[windowHandle] = eventId;
                    pendingCount = GetPendingCount();
                    return WindowEventQueueResult.Coalesced;
                }

                if (this.objectEvents.Count >= maximumPendingEvents)
                {
                    // Once the bounded map fills, one full enumeration is cheaper and more accurate
                    // than retaining an unbounded sequence of stale intermediate HWND transitions.
                    this.objectEvents.Clear();
                    this.fullRefreshPending = true;
                    pendingCount = GetPendingCount();
                    return WindowEventQueueResult.Overflowed;
                }

                this.objectEvents[windowHandle] = eventId;
                pendingCount = GetPendingCount();
                return WindowEventQueueResult.Added;
            }
        }

        public PendingWindowEventBatch Drain(int maximumEvents)
        {
            lock (this.syncRoot)
            {
                bool fullRefresh = this.fullRefreshPending;
                bool hasForeground = this.foregroundPending;
                IntPtr foreground = this.foregroundWindowHandle;
                this.fullRefreshPending = false;
                this.foregroundPending = false;
                this.foregroundWindowHandle = IntPtr.Zero;

                List<PendingWindowEvent> events = new List<PendingWindowEvent>();
                if (fullRefresh)
                {
                    this.objectEvents.Clear();
                }
                else
                {
                    List<IntPtr> drainedHandles = new List<IntPtr>();
                    foreach (KeyValuePair<IntPtr, uint> pair in this.objectEvents)
                    {
                        events.Add(new PendingWindowEvent(pair.Value, pair.Key));
                        drainedHandles.Add(pair.Key);
                        if (events.Count >= maximumEvents)
                        {
                            break;
                        }
                    }

                    for (int i = 0; i < drainedHandles.Count; i++)
                    {
                        this.objectEvents.Remove(drainedHandles[i]);
                    }
                }

                return new PendingWindowEventBatch(
                    fullRefresh,
                    hasForeground,
                    foreground,
                    events,
                    GetPendingCount());
            }
        }

        public void ClearObjectEvents()
        {
            lock (this.syncRoot)
            {
                this.objectEvents.Clear();
                this.fullRefreshPending = false;
            }
        }

        public void ClearAll()
        {
            lock (this.syncRoot)
            {
                this.objectEvents.Clear();
                this.fullRefreshPending = false;
                this.foregroundPending = false;
                this.foregroundWindowHandle = IntPtr.Zero;
            }
        }

        private int GetPendingCount()
        {
            return (this.fullRefreshPending ? 1 : this.objectEvents.Count) +
                (this.foregroundPending ? 1 : 0);
        }
    }

    private struct ProcessIdentityCacheEntry
    {
        public ProcessIdentityCacheEntry(bool isAssistant, DateTime expiresUtc)
        {
            this.IsAssistant = isAssistant;
            this.ExpiresUtc = expiresUtc;
        }

        public readonly bool IsAssistant;
        public readonly DateTime ExpiresUtc;
    }

    private sealed class TrackedWindow
    {
        public TrackedWindow(NativeMethods.ApplicationWindowInfo info, long lastForegroundTicks)
        {
            this.Info = info;
            this.LastForegroundTicks = lastForegroundTicks;
        }

        public NativeMethods.ApplicationWindowInfo Info { get; private set; }
        public long LastForegroundTicks { get; private set; }
    }
}
