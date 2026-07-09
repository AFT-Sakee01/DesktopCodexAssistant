using System;
using System.Collections.Generic;
using System.Drawing;

internal sealed class ApplicationWindowStateTracker : IDisposable
{
    private readonly IntPtr ownHandle;
    private readonly Dictionary<IntPtr, TrackedWindow> windows =
        new Dictionary<IntPtr, TrackedWindow>();
    private NativeMethods.WindowEventHook hook;
    private bool disposed;

    public ApplicationWindowStateTracker(IntPtr ownHandle, NativeMethods.WindowEventHandler handler)
    {
        this.ownHandle = ownHandle;
        RefreshAll();
        this.hook = new NativeMethods.WindowEventHook(handler);
        Program.LogInfo("Application window state tracker started. HookActive=" + this.hook.IsActive);
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

        this.windows.Clear();
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
