using System;
using System.Drawing;
using System.Windows.Forms;

// Samples the global left button without installing a mouse hook or taking capture. Every caller
// observes the same monotonically increasing click sequence, so the two boards can independently
// react to one physical click without whichever timer runs first consuming it for the other.
internal static class OutsideClickDismissalMonitor
{
    internal const int ReopenSuppressionMilliseconds = 800;
    private const int CurrentDownMask = 0x8000;
    private const int PressedSinceLastQueryMask = 0x0001;

    private static readonly object SyncRoot = new object();
    private static bool previousButtonDown;
    private static bool currentButtonDown;
    private static long latestClickSequence;
    private static Point latestClickPosition;
    private static DateTime latestClickUtc = DateTime.MinValue;

    public static void Poll()
    {
        // The low bit is cleared by a GetAsyncKeyState query. All in-process VK_LBUTTON reads must
        // route through this class (including NativeMethods.IsAnyMouseButtonDown), otherwise a fast
        // press-and-release could be consumed before the board timers observe it. The high-bit edge
        // remains the primary path; the low bit only bridges a complete click between poll ticks.
        short state = NativeMethods.ReadLeftMouseButtonAsyncState();
        bool buttonDown = (state & unchecked((short)CurrentDownMask)) != 0;
        bool pressedSinceLastQuery = (state & PressedSinceLastQueryMask) != 0;
        Point cursor = Cursor.Position;
        DateTime nowUtc = DateTime.UtcNow;

        lock (SyncRoot)
        {
            bool pressEdge = IsPressEdge(previousButtonDown, buttonDown, pressedSinceLastQuery);
            previousButtonDown = buttonDown;
            currentButtonDown = buttonDown;
            if (!pressEdge)
            {
                return;
            }

            latestClickSequence++;
            latestClickPosition = cursor;
            latestClickUtc = nowUtc;
        }
    }

    public static bool IsLeftButtonDown()
    {
        Poll();
        lock (SyncRoot)
        {
            return currentButtonDown;
        }
    }

    public static long ArmConsumer()
    {
        Poll();
        lock (SyncRoot)
        {
            return latestClickSequence;
        }
    }

    public static bool TryGetClickAfter(ref long observedSequence, out Point position, out DateTime occurredUtc)
    {
        Poll();
        lock (SyncRoot)
        {
            if (!TryAdvanceConsumer(latestClickSequence, ref observedSequence))
            {
                position = Point.Empty;
                occurredUtc = DateTime.MinValue;
                return false;
            }

            position = latestClickPosition;
            occurredUtc = latestClickUtc;
            return true;
        }
    }

    internal static bool IsPressEdge(bool previousDown, bool currentDown, bool pressedSinceLastQuery)
    {
        return pressedSinceLastQuery || currentDown && !previousDown;
    }

    internal static bool TryAdvanceConsumer(long latestSequence, ref long observedSequence)
    {
        if (latestSequence <= observedSequence)
        {
            return false;
        }

        observedSequence = latestSequence;
        return true;
    }

    internal static bool ShouldDismissOutsideClick(
        bool enabled,
        Point clickPosition,
        Rectangle boardBounds,
        Rectangle dockTabBounds,
        Rectangle accessoryBounds)
    {
        return enabled &&
            !boardBounds.Contains(clickPosition) &&
            !dockTabBounds.Contains(clickPosition) &&
            !accessoryBounds.Contains(clickPosition);
    }

    internal static bool ShouldSuppressTabReopen(DateTime collapseUtc, DateTime nowUtc)
    {
        if (collapseUtc == DateTime.MinValue || nowUtc < collapseUtc)
        {
            return false;
        }

        return nowUtc < collapseUtc.AddMilliseconds(ReopenSuppressionMilliseconds);
    }

    internal static void RunSelfTest()
    {
        if (!IsPressEdge(false, true, false) ||
            !IsPressEdge(false, false, true) ||
            !IsPressEdge(true, true, true) ||
            IsPressEdge(true, true, false) ||
            IsPressEdge(false, false, false))
        {
            throw new InvalidOperationException("Outside-click left-button edge policy failed.");
        }

        long firstConsumer = 4;
        long secondConsumer = 4;
        if (!TryAdvanceConsumer(5, ref firstConsumer) ||
            !TryAdvanceConsumer(5, ref secondConsumer) ||
            firstConsumer != 5 || secondConsumer != 5 ||
            TryAdvanceConsumer(5, ref firstConsumer))
        {
            throw new InvalidOperationException("Outside-click sequence fan-out policy failed.");
        }

        Rectangle board = new Rectangle(10, 10, 100, 100);
        Rectangle tab = new Rectangle(0, 40, 10, 30);
        Rectangle manager = new Rectangle(140, 10, 120, 100);
        if (ShouldDismissOutsideClick(true, new Point(20, 20), board, tab, manager) ||
            ShouldDismissOutsideClick(true, new Point(5, 50), board, tab, manager) ||
            ShouldDismissOutsideClick(true, new Point(150, 20), board, tab, manager) ||
            !ShouldDismissOutsideClick(true, new Point(300, 300), board, tab, manager) ||
            ShouldDismissOutsideClick(false, new Point(300, 300), board, tab, manager))
        {
            throw new InvalidOperationException("Outside-click excluded-bounds policy failed.");
        }

        DateTime collapsed = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        if (!ShouldSuppressTabReopen(collapsed, collapsed.AddMilliseconds(799)) ||
            ShouldSuppressTabReopen(collapsed, collapsed.AddMilliseconds(800)) ||
            ShouldSuppressTabReopen(DateTime.MinValue, collapsed))
        {
            throw new InvalidOperationException("Outside-click dock-tab reopen suppression policy failed.");
        }

        Console.WriteLine("Outside click dismissal: PASS edge fan-out exclusions reopen-suppression");
    }
}
