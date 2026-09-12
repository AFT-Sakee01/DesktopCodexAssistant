using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

// Opens LiveCaptionsTranslator's own transparent caption window ("overlay mode") after this app
// starts the translator.
//
// The translator has no setting for it. MainWindow constructs with `OverlayWindow = null` and the
// window is only ever created inside OverlayModeButton_Click, so the overlay is gone after every
// launch -- including the relaunches the keep-alive guard performs, which is where the loss is most
// annoying: the machine wakes up, the guard quietly restores the whole chain, and the captions the
// user actually watches are the one part that does not come back.
//
// So this presses the button. Two upstream identifiers make that reliable rather than a coordinate
// guess: the button is `x:Name="OverlayModeButton"` (WPF publishes x:Name as the UI Automation
// AutomationId) and the overlay window is `Title="Overlay Window"`. The window title is also the
// state check -- the button is a toggle, and pressing it when the overlay is already up would close
// the very thing this is meant to open.
internal static class TranslatorOverlayController
{
    // Upstream XAML: src/windows/MainWindow.xaml (x:Name) and src/windows/OverlayWindow.xaml (Title).
    private const string OverlayButtonAutomationId = "OverlayModeButton";
    private const string OverlayWindowTitle = "Overlay Window";
    private const string TranslatorProcessName = "LiveCaptionsTranslator";
    // The translator's main window takes a moment to exist after Process.Start returns; without a
    // wait the very case this exists for (a freshly relaunched translator) is the one that fails.
    private const int MainWindowWaitMs = 12000;
    private const int PollIntervalMs = 400;
    // UI Automation calls cross a process boundary and can block; the caller is always a background
    // thread, but a stuck provider must not pin it forever.
    private const int AutomationTimeoutMs = 4000;

    internal static bool IsOverlayOpen()
    {
        int[] processIds = GetTranslatorProcessIds();
        for (int i = 0; i < processIds.Length; i++)
        {
            IntPtr handle;
            if (NativeMethods.TryFindProcessWindowByTitle(processIds[i], OverlayWindowTitle, out handle))
            {
                return true;
            }
        }

        return false;
    }

    // Returns true only when this call actually opened the overlay, so callers can report a real
    // action instead of a no-op. `detail` always carries the reason.
    internal static bool TryEnsureOverlayOpen(out string detail)
    {
        detail = string.Empty;
        try
        {
            if (IsOverlayOpen())
            {
                detail = "覆盖字幕已打开";
                return false;
            }

            AutomationElement mainWindow = WaitForTranslatorMainWindow();
            if (mainWindow == null)
            {
                detail = "未找到翻译器主窗口";
                return false;
            }

            AutomationElement button = mainWindow.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, OverlayButtonAutomationId));
            if (button == null)
            {
                // Upstream renamed or restructured the control. Reporting it is the whole value here:
                // silently doing nothing would look identical to "the overlay is already open".
                detail = "未找到覆盖字幕按钮(" + OverlayButtonAutomationId + ")";
                return false;
            }

            object pattern;
            if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern) || pattern == null)
            {
                detail = "覆盖字幕按钮不支持 Invoke";
                return false;
            }

            ((InvokePattern)pattern).Invoke();

            // Confirm by the window, not by the click: Invoke() succeeding only means the click was
            // delivered.
            if (WaitForOverlayWindow())
            {
                detail = "已打开覆盖字幕";
                return true;
            }

            detail = "已点击覆盖字幕按钮但窗口未出现";
            return false;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static bool WaitForOverlayWindow()
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(AutomationTimeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (IsOverlayOpen())
            {
                return true;
            }

            Thread.Sleep(PollIntervalMs);
        }

        return false;
    }

    private static AutomationElement WaitForTranslatorMainWindow()
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(MainWindowWaitMs);
        while (true)
        {
            int[] processIds = GetTranslatorProcessIds();
            for (int i = 0; i < processIds.Length; i++)
            {
                AutomationElement element = FindMainWindowElement(processIds[i]);
                if (element != null)
                {
                    return element;
                }
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    // Resolved from the window handle rather than by walking the automation tree from the desktop
    // root: AutomationElement.FromHandle is one cross-process call, while FindAll over every
    // top-level window costs one per window and is the slow path this app already avoids elsewhere.
    private static AutomationElement FindMainWindowElement(int processId)
    {
        try
        {
            Process process = Process.GetProcessById(processId);
            using (process)
            {
                IntPtr handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                {
                    return null;
                }

                return AutomationElement.FromHandle(handle);
            }
        }
        catch
        {
            return null;
        }
    }

    private static int[] GetTranslatorProcessIds()
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(TranslatorProcessName);
            int[] ids = new int[processes.Length];
            for (int i = 0; i < processes.Length; i++)
            {
                ids[i] = processes[i].Id;
            }

            return ids;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return new int[0];
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
}
