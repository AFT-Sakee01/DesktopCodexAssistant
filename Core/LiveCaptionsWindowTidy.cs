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
// Hiding it takes two steps, not one: minimising a window that has no taskbar button (which is what
// WS_EX_TOOLWINDOW makes it) leaves the classic minimised placeholder — a title-bar-sized box parked
// in a screen corner — so the window also has to be parked off-screen. IsIconic goes true after the
// first step and says nothing about whether that box is still in the user's face.
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
    // 手动收起：忽略「每个窗口只处理一次」的记账。自动收起刻意只收一次，好让用户
    // 自己还原之后不再被抢；但用户亲手点的按钮必须每次都生效，否则按钮就是个哑巴。
    internal static bool TryCollapseNow(out string detail)
    {
        detail = string.Empty;
        try
        {
            IntPtr handle;
            if (!NativeMethods.TryFindWindowByClassName(LiveCaptionsWindowClassName, out handle))
            {
                detail = "没有找到实时辅助字幕窗口";
                return false;
            }

            bool acted = false;
            if (!NativeMethods.IsWindowMinimized(handle))
            {
                if (!NativeMethods.TryMinimizeWindowAsToolWindow(handle))
                {
                    detail = "最小化实时辅助字幕窗口失败";
                    return false;
                }

                acted = true;
            }

            // 最小化之后还得把它挪走，否则按钮看上去就是坏的。WS_EX_TOOLWINDOW 让这个窗口没有
            // 任务栏按钮，于是 Windows 保留了经典的「最小化占位框」——一个标题栏高的深色方块停在
            // 屏幕角落，在这台机器上正是左上角那个 314x50 的黑框。IsIconic 这时早就返回 true，
            // 旧代码据此直接判定「已经是收起状态」掉头就走：标志位说的和用户看见的根本不是一回事。
            if (NativeMethods.IsWindowRectangleOnScreen(handle))
            {
                if (!NativeMethods.TryParkWindowOffScreen(handle))
                {
                    detail = "移开实时辅助字幕窗口失败";
                    return false;
                }

                acted = true;
            }

            lastHandledWindow = handle;
            if (!acted)
            {
                detail = "实时辅助字幕窗口已经是收起状态";
                return false;
            }

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

            bool acted = false;
            if (!NativeMethods.IsWindowMinimized(handle))
            {
                if (!NativeMethods.TryMinimizeWindowAsToolWindow(handle))
                {
                    detail = "最小化实时辅助字幕窗口失败";
                    return false;
                }

                acted = true;
            }

            // 和手动路径同一条理由：最小化只是把窗口变成角落里的占位框，那个框还在屏幕上。
            // 翻译器在自己启动时最小化过一次的实例也会留下这个框，所以这里不能因为
            // IsIconic 为真就当作已经处理完。
            if (NativeMethods.IsWindowRectangleOnScreen(handle))
            {
                if (NativeMethods.TryParkWindowOffScreen(handle))
                {
                    acted = true;
                }
            }

            lastHandledWindow = handle;
            if (!acted)
            {
                // 已经收起且不在屏幕上；记下这个实例，之后它是什么状态都是用户自己的事。
                return false;
            }

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
