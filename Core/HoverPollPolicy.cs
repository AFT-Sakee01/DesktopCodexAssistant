// The one place the hover-poll numbers live.
//
// Every visible surface in this app is a layered WS_EX_NOACTIVATE tool window, and the caption strip
// is WS_EX_TRANSPARENT on top of that, so none of them receives a reliable mouse enter/leave stream.
// Hover is therefore decided by comparing the cursor against the window rectangle on a clock, and
// these are the numbers that clock runs on.
//
// They are a plain static class rather than constants on LayeredWidgetFormBase because MetricTileForm
// and EdgeDockTabForm each still carry their own private copies from before the shared poll existed;
// protected constants of the same names on their base class would hide those and warn (CS0108) in
// files this change has no business touching. When those two adopt
// LayeredWidgetFormBase.StartHoverPolling, their copies go away and this stays the single source.
internal static class HoverPollPolicy
{
    internal const int IntervalMs = 120;
    // Entering reacts on the first poll; leaving waits three, so a pointer that grazes an edge or
    // crosses on its way somewhere else does not make the surface flicker.
    internal const int EnterTicks = 1;
    internal const int ExitTicks = 3;
}
