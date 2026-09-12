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

    // The translator's own main window: same class of problem, same once-per-instance rule, so it
    // lives here rather than in a second helper. With this app drawing the captions that window has
    // nothing left to show, and it is topmost by default.
    private const string TranslatorMainWindowTitle = "LiveCaptions Translator";
    private const string TranslatorProcessName = "LiveCaptionsTranslator";

    private static IntPtr lastHandledWindow = IntPtr.Zero;
    private static IntPtr lastHandledTranslatorWindow = IntPtr.Zero;

    internal static void ResetForTests()
    {
        lastHandledWindow = IntPtr.Zero;
        lastHandledTranslatorWindow = IntPtr.Zero;
    }

    // Minimises the translator's main window once per instance, with the same restraint as the
    // caption host above: only while this app renders the captions itself, and never a second time
    // for a window the user brought back.
    internal static bool TryHideTranslatorMainWindow(out string detail)
    {
        detail = string.Empty;
        try
        {
            if (!TranslatorControlReader.IsLiveCaptionsTranslatorRunning())
            {
                return false;
            }

            IntPtr handle;
            if (!TryFindTranslatorMainWindow(out handle))
            {
                return false;
            }

            if (handle == lastHandledTranslatorWindow)
            {
                return false;
            }

            if (NativeMethods.IsWindowMinimized(handle))
            {
                lastHandledTranslatorWindow = handle;
                return false;
            }

            // Plain minimise, no WS_EX_TOOLWINDOW: unlike the caption host, this is a window the
            // user may well want back from the taskbar to read its log or change a setting.
            if (!NativeMethods.TryMinimizeWindow(handle))
            {
                detail = "最小化翻译器主窗口失败";
                return false;
            }

            lastHandledTranslatorWindow = handle;
            detail = "已收起翻译器主窗口";
            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool TryFindTranslatorMainWindow(out IntPtr handle)
    {
        handle = IntPtr.Zero;
        System.Diagnostics.Process[] processes = null;
        try
        {
            processes = System.Diagnostics.Process.GetProcessesByName(TranslatorProcessName);
            for (int i = 0; i < processes.Length; i++)
            {
                if (NativeMethods.TryFindProcessWindowByTitle(processes[i].Id, TranslatorMainWindowTitle, out handle))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (processes != null)
            {
                for (int i = 0; i < processes.Length; i++)
                {
                    try
                    {
                        processes[i].Dispose();
                    }
                    catch
                    {
                    }
                }
            }
        }
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
