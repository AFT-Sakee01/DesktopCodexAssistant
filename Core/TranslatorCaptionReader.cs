using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

// Reads the live caption text out of LiveCaptionsTranslator's main window so this app can render it
// itself.
//
// Why UI Automation and not translation_history.db: the database only receives finished rows, while
// the two text blocks read here are the same live properties the translator's own overlay binds to
// (upstream src/pages/CaptionPage.xaml: OriginalCaption -> DisplayOriginalCaption and
// TranslatedCaption -> DisplayTranslatedCaption, both UpdateSourceTrigger=PropertyChanged). Reading
// them means our overlay says exactly what the translator's would, at the same moment, including the
// part-translated sentence in progress and its own status strings ("[Paused]", restart warnings).
//
// Both identifiers come from upstream x:Name attributes, which WPF publishes as AutomationIds.
//
// Threading: every public entry is called from a background thread (WidgetForm queues it). UIA calls
// cross a process boundary and can block, so nothing here may be called from the UI thread.
internal sealed class TranslatorCaptionReader
{
    private const string TranslatorProcessName = "LiveCaptionsTranslator";
    private const string OriginalCaptionAutomationId = "OriginalCaption";
    private const string TranslatedCaptionAutomationId = "TranslatedCaption";
    // Fast enough that a caption never visibly lags the audio, slow enough that the cross-process
    // property reads stay background noise. The translator itself repaints on a 25ms loop; matching
    // that would buy nothing, because a human cannot read faster than this refreshes.
    internal const int RefreshIntervalMs = 250;
    // Re-resolving the automation elements is the expensive part (a descendant search). Once the
    // window is gone this backs off so a closed translator does not cost a tree walk every tick.
    private const int ResolveRetryIntervalMs = 2000;
    // The confirmed sentence comes from the translator's history database, which is the only
    // place it exists once the overlay window is closed: the in-progress text lives in the main
    // window, but the sentence before it has already left that window and been logged. Rows only
    // appear there for finished translations, which is exactly the definition of "confirmed".
    private const int HistoryRefreshIntervalMs = 1500;
    private const string HistoryDatabaseFileName = "translation_history.db";
    private const string HistoryTableName = "TranslationHistory";

    private readonly object stateLock = new object();

    private AutomationElement originalElement;
    private AutomationElement translatedElement;
    private IntPtr resolvedWindowHandle = IntPtr.Zero;
    private DateTime lastRefreshUtc = DateTime.MinValue;
    private DateTime lastResolveAttemptUtc = DateTime.MinValue;
    private TranslatorCaptionSnapshot snapshot = TranslatorCaptionSnapshot.CreateEmpty();
    private int refreshing;
    private DateTime lastHistoryReadUtc = DateTime.MinValue;
    private string lastConfirmedTranslation = string.Empty;

    // Latest published state. Cache-only: never touches UIA, so the UI thread may call it.
    internal TranslatorCaptionSnapshot GetSnapshot()
    {
        lock (this.stateLock)
        {
            return this.snapshot.Clone();
        }
    }

    internal void RefreshIfDue()
    {
        DateTime nowUtc = DateTime.UtcNow;
        lock (this.stateLock)
        {
            if (this.lastRefreshUtc != DateTime.MinValue &&
                (nowUtc - this.lastRefreshUtc).TotalMilliseconds < RefreshIntervalMs)
            {
                return;
            }

            this.lastRefreshUtc = nowUtc;
        }

        // Single-flight: a UIA read that blocks must not stack up behind itself.
        if (Interlocked.CompareExchange(ref this.refreshing, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Refresh(nowUtc);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
        finally
        {
            Interlocked.Exchange(ref this.refreshing, 0);
        }
    }

    private void Refresh(DateTime nowUtc)
    {
        TranslatorCaptionSnapshot next = TranslatorCaptionSnapshot.CreateEmpty();
        next.UpdatedUtc = nowUtc;

        IntPtr windowHandle = FindTranslatorMainWindow();
        next.TranslatorRunning = windowHandle != IntPtr.Zero;
        if (!next.TranslatorRunning)
        {
            // Drop the cached elements: a relaunched translator gets new ones, and a stale element
            // would keep answering with the text the old process last showed.
            ReleaseElements();
            Publish(next);
            return;
        }

        if (!EnsureElements(windowHandle, nowUtc))
        {
            Publish(next);
            return;
        }

        string original;
        string translated;
        if (!TryReadCaption(out original, out translated))
        {
            // The window went away between resolve and read, or the provider faulted. Report the
            // translator as running but unreadable rather than inventing empty captions.
            ReleaseElements();
            Publish(next);
            return;
        }

        next.CaptionElementsResolved = true;
        next.OriginalCaption = original;
        next.TranslatedCaption = translated;
        next.PreviousTranslation = ResolveConfirmedTranslation(nowUtc, translated);
        Publish(next);
    }

    // Newest logged translation, minus the one still on screen. The database write happens when a
    // sentence finishes, so for a moment the newest row IS the sentence the main window is still
    // showing; returning it then would print the same words twice, once as settled and once as
    // in-progress.
    private string ResolveConfirmedTranslation(DateTime nowUtc, string currentTranslation)
    {
        if (this.lastHistoryReadUtc == DateTime.MinValue ||
            (nowUtc - this.lastHistoryReadUtc).TotalMilliseconds >= HistoryRefreshIntervalMs)
        {
            this.lastHistoryReadUtc = nowUtc;
            this.lastConfirmedTranslation = ReadNewestTranslation();
        }

        string confirmed = this.lastConfirmedTranslation ?? string.Empty;
        if (confirmed.Length == 0)
        {
            return string.Empty;
        }

        if (string.Equals(confirmed, (currentTranslation ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return confirmed;
    }

    private static string ReadNewestTranslation()
    {
        try
        {
            string path = System.IO.Path.Combine(TranslatorControlReader.TranslatorDirectory, HistoryDatabaseFileName);
            if (!System.IO.File.Exists(path))
            {
                return string.Empty;
            }

            System.Collections.Generic.List<MinimalSqliteReader.Row> rows =
                MinimalSqliteReader.ReadLastRowsByRowIdDescending(path, HistoryTableName, 1);
            if (rows.Count == 0 || rows[0].Values == null || rows[0].Values.Length < 4)
            {
                return string.Empty;
            }

            // Column order matches TranslatorControlReader.ReadHistory: [3] = TranslatedText.
            string translated = (rows[0].Values[3] ?? string.Empty).Trim();
            // Errors and warnings are shown by the live line already; repeating a failed sentence
            // as settled context would be worse than showing nothing.
            if (translated.StartsWith("[ERROR]", StringComparison.Ordinal) ||
                translated.StartsWith("[WARNING]", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return translated;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return string.Empty;
        }
    }

    private void Publish(TranslatorCaptionSnapshot next)
    {
        lock (this.stateLock)
        {
            this.snapshot = next;
        }
    }

    private bool EnsureElements(IntPtr windowHandle, DateTime nowUtc)
    {
        if (this.originalElement != null && this.translatedElement != null &&
            this.resolvedWindowHandle == windowHandle)
        {
            return true;
        }

        if (this.lastResolveAttemptUtc != DateTime.MinValue &&
            (nowUtc - this.lastResolveAttemptUtc).TotalMilliseconds < ResolveRetryIntervalMs)
        {
            return false;
        }

        this.lastResolveAttemptUtc = nowUtc;
        try
        {
            AutomationElement window = AutomationElement.FromHandle(windowHandle);
            if (window == null)
            {
                return false;
            }

            AutomationElement original = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, OriginalCaptionAutomationId));
            AutomationElement translated = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, TranslatedCaptionAutomationId));
            if (original == null || translated == null)
            {
                return false;
            }

            this.originalElement = original;
            this.translatedElement = translated;
            this.resolvedWindowHandle = windowHandle;
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    private bool TryReadCaption(out string original, out string translated)
    {
        original = string.Empty;
        translated = string.Empty;
        try
        {
            // TextBlock exposes its text as the element Name, which is what the translator's own
            // overlay binding produces.
            original = (this.originalElement.Current.Name ?? string.Empty).Trim();
            translated = (this.translatedElement.Current.Name ?? string.Empty).Trim();
            return true;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    private void ReleaseElements()
    {
        this.originalElement = null;
        this.translatedElement = null;
        this.resolvedWindowHandle = IntPtr.Zero;
    }

    private static IntPtr FindTranslatorMainWindow()
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(TranslatorProcessName);
            for (int i = 0; i < processes.Length; i++)
            {
                IntPtr handle = processes[i].MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return IntPtr.Zero;
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
