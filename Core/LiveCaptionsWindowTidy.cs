using System;

// Keeps Windows' own Live Captions host out of the way while the translator is driving it.
//
// LiveCaptionsTranslator minimises that window at its own startup (LiveCaptionsHandler
// .HideLiveCaptions), but not on the path that matters most here: when the caption host dies, its
// TranslateLoop calls LaunchLiveCaptions() again -- which kills and relaunches the process -- and
// never hides the new window. The same gap applies to this app's keep-alive guard, which can start
// LiveCaptions.exe on its own. Either way the user ends up with a topmost caption bar across the
// video they are watching, showing the untranslated text they already decided not to read.
//
// The window is hidden once per instance, never on a schedule. If the user restores it deliberately
// -- to read the original captions, say -- it stays restored: re-minimising it every few seconds
// would be a program arguing with its owner.
internal static class LiveCaptionsWindowTidy
{
    // Class name rather than title: the title is localised (即時輔助字幕 on this machine) while the
    // class is what LiveCaptionsTranslator itself matches on.
    private const string LiveCaptionsWindowClassName = "LiveCaptionsDesktopWindow";

    private static IntPtr lastHandledWindow = IntPtr.Zero;

    internal static void ResetForTests()
    {
        lastHandledWindow = IntPtr.Zero;
    }

    // Returns true only when this call actually minimised a window, so callers can log a real action.
    internal static bool TryHideNewCaptionWindow(out string detail)
    {
        detail = string.Empty;
        try
        {
            // Only while the translator is running. With no translator there is nothing consuming the
            // captions, so the window is the user's own business and must be left alone.
            if (!TranslatorControlReader.IsLiveCaptionsTranslatorRunning())
            {
                return false;
            }

            IntPtr handle;
            if (!NativeMethods.TryFindWindowByClassName(LiveCaptionsWindowClassName, out handle))
            {
                return false;
            }

            if (handle == lastHandledWindow)
            {
                // Already dealt with this instance. Whatever state it is in now is the user's doing.
                return false;
            }

            if (NativeMethods.IsWindowMinimized(handle))
            {
                // The translator hid it at its own startup; nothing to do beyond remembering that
                // this instance needs no further attention.
                lastHandledWindow = handle;
                return false;
            }

            if (!NativeMethods.TryMinimizeWindowAsToolWindow(handle))
            {
                detail = "最小化实时辅助字幕窗口失败";
                return false;
            }

            lastHandledWindow = handle;
            detail = "已收起实时辅助字幕窗口";
            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }
}
