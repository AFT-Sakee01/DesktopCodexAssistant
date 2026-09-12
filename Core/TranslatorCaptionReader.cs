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
    // How many finished sentences the reader keeps. The strip shows as many of them as
    // CaptionOverlaySettledLines asks for; keeping the maximum means raising that setting shows
    // real history immediately instead of starting from nothing.
    internal const int SettledHistoryLimit = WidgetSettings.MaxCaptionOverlaySettledLines;

    private readonly object stateLock = new object();

    private AutomationElement originalElement;
    private AutomationElement translatedElement;
    private IntPtr resolvedWindowHandle = IntPtr.Zero;
    private DateTime lastRefreshUtc = DateTime.MinValue;
    private DateTime lastResolveAttemptUtc = DateTime.MinValue;
    private TranslatorCaptionSnapshot snapshot = TranslatorCaptionSnapshot.CreateEmpty();
    private int refreshing;
    // The running article. Owned here because this is where a sentence is known to be finished;
    // the board and the export button only read it.
    internal CaptionTranscript Transcript { get { return this.transcript; } }

    private readonly CaptionTranscript transcript = new CaptionTranscript();
    private string lastLiveOriginal = string.Empty;
    private string lastLiveTranslation = string.Empty;
    // Settled sentences, oldest first, capped at SettledHistoryLimit.
    private readonly System.Collections.Generic.List<string> settledTranslations =
        new System.Collections.Generic.List<string>();

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
            // would keep answering with the text the old process last showed. The latched sentences
            // go with them -- a new session must not open with the last words of the old one.
            ReleaseElements();
            this.lastLiveOriginal = string.Empty;
            this.lastLiveTranslation = string.Empty;
            this.settledTranslations.Clear();
            // The article deliberately survives: the translator restarting (or being restarted by
            // the keep-alive guard) is not a reason to lose what was already said.
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
        next.SettledTranslations = ResolveSettledTranslations(original, translated);
        Publish(next);
    }

    // Newest logged translation, minus the one still on screen. The database write happens when a
    // sentence finishes, so for a moment the newest row IS the sentence the main window is still
    // showing; returning it then would print the same words twice, once as settled and once as
    // in-progress.
    // The settled line is latched from what this reader itself watched happen, not reconstructed
    // from the history table.
    //
    // Reconstructing it from the table does not work, and the live stack shows why: the translator
    // rewrites rather than appends while a sentence is still growing (Translator.IsOverwrite ->
    // DeleteLastTranslation + LogTranslation), it re-translates a lengthening sentence from scratch
    // so even the opening words of the translation change, and the row for a finished sentence
    // lands slightly before the live line catches up. Rows therefore mutate, reorder relative to
    // the live line, and cannot be matched by their translated text.
    //
    // What is stable is the original sentence: while one sentence is being refined the recogniser
    // keeps extending the same text, and a genuinely new sentence starts different text. So this
    // watches the original line, and the moment it becomes a different sentence the translation
    // that was live until then becomes the settled one. Nothing else can move it.
    private string[] ResolveSettledTranslations(string liveOriginal, string liveTranslation)
    {
        string original = (liveOriginal ?? string.Empty).Trim();
        string translation = (liveTranslation ?? string.Empty).Trim();

        if (original.Length == 0)
        {
            return BuildSettledArray(translation);
        }

        if (this.lastLiveOriginal.Length == 0)
        {
            this.lastLiveOriginal = original;
            this.lastLiveTranslation = translation;
            return BuildSettledArray(translation);
        }

        if (!IsSameSpokenSentence(this.lastLiveOriginal, original))
        {
            // A new sentence started, so whatever was live a moment ago is now final. Status strings
            // are the only thing not worth keeping as context.
            //
            // Nothing else may gate this. An earlier version also required the latched text to differ
            // from the current translation, and that stopped the latch from ever firing: the original
            // line moves to the next sentence a beat before the translation does, so at the instant of
            // the transition the translation on screen still IS the sentence being latched.
            if (this.lastLiveTranslation.Length > 0 && !IsStatusText(this.lastLiveTranslation))
            {
                AppendSettled(this.lastLiveTranslation, this.lastLiveOriginal);
            }
        }

        this.lastLiveOriginal = original;
        if (translation.Length > 0)
        {
            this.lastLiveTranslation = translation;
        }

        return BuildSettledArray(translation);
    }

    // Drops the newest settled sentence while it is still the line on screen -- for the beat between
    // the original moving on and the translation catching up, the two would be the same words twice.
    // Only an exact match is dropped: a *similar* line is a different sentence that happens to open
    // the same way, and hiding those made the block flicker at every transition.
    // Replaces the last entry instead of adding a new one when the two are the same sentence said
    // twice.
    //
    // The boundary test upstream gives us is not exact: the recogniser revises a sentence it has
    // already emitted (rewording its opening, adding a clause), the translator re-translates the
    // whole thing, and that can look like a new sentence starting. Upstream hits the same problem
    // and solves it the same way -- IsOverwrite deletes the last logged row and writes the new one.
    // Without this the history block shows the same thought twice in slightly different words.
    private void AppendSettled(string sentence, string spokenSentence)
    {
        int count = this.settledTranslations.Count;
        if (count > 0 && IsSameTranslation(this.settledTranslations[count - 1], sentence))
        {
            // Keep the newer text: it is the more complete translation of the same sentence.
            this.settledTranslations[count - 1] = sentence;
            // The article has to follow the same collapse, or the reworded sentence lands in it
            // twice.
            this.transcript.ReplaceLatest(sentence, spokenSentence);
            return;
        }

        this.settledTranslations.Add(sentence);
        while (this.settledTranslations.Count > SettledHistoryLimit)
        {
            this.settledTranslations.RemoveAt(0);
        }

        this.transcript.Append(sentence, spokenSentence, DateTime.UtcNow);
    }

    // Two translations are the same sentence when one contains the other (a sentence that grew a
    // clause) or when they agree over most of their shared opening (a sentence whose wording was
    // revised). Both shapes were observed live on 2026-09-12, e.g. "而对于下一个请求，如果你需要。"
    // followed by "并且对于下一个请求，如果你需要的话。".
    private static bool IsSameTranslation(string first, string second)
    {
        if (first.Length == 0 || second.Length == 0)
        {
            return false;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return true;
        }

        int shorter = Math.Min(first.Length, second.Length);
        if (shorter < 6)
        {
            // Too short to tell "same sentence, reworded" from "two short sentences".
            return false;
        }

        if (first.IndexOf(second, StringComparison.Ordinal) >= 0 ||
            second.IndexOf(first, StringComparison.Ordinal) >= 0)
        {
            return true;
        }

        int matched = 0;
        for (int i = 0; i < shorter && first[i] == second[i]; i++)
        {
            matched++;
        }

        // Three fifths of the shorter sentence agreeing from the start is a rewording; less than
        // that is a different sentence that happens to open the same way ("所以…", "这个…").
        return matched * 5 >= shorter * 3;
    }

    private string[] BuildSettledArray(string liveTranslation)
    {
        int count = this.settledTranslations.Count;
        if (count > 0 &&
            liveTranslation.Length > 0 &&
            string.Equals(this.settledTranslations[count - 1], liveTranslation, StringComparison.Ordinal))
        {
            count--;
        }

        string[] result = new string[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = this.settledTranslations[i];
        }

        return result;
    }

    // Two readings of the original caption are the same sentence while one is still growing out of
    // the other. Compared on the common length, which is how upstream decides the same thing
    // (Translator.IsOverwrite truncates both to the shorter length before scoring similarity).
    private static bool IsSameSpokenSentence(string previous, string current)
    {
        int common = Math.Min(previous.Length, current.Length);
        if (common == 0)
        {
            return false;
        }

        // Short openings are ambiguous ("So", "And then"), so a new sentence is only declared once
        // there is enough text to tell them apart.
        if (common < 10)
        {
            return true;
        }

        int matched = 0;
        for (int i = 0; i < common; i++)
        {
            if (previous[i] != current[i])
            {
                break;
            }

            matched++;
        }

        // Two thirds of the shared span is enough tolerance for the recogniser correcting a word it
        // had wrong, without treating the next sentence as a continuation of this one.
        return matched * 3 >= common * 2;
    }

    private static bool IsStatusText(string text)
    {
        return text.StartsWith("[", StringComparison.Ordinal);
    }

    // The live line can be a shortened form of the logged sentence (the translator runs long
    // sentences through ShortenDisplaySentence before showing them), so exact equality is not
    // enough to recognise "this row is the line already on screen".
    private static bool IsSameSentence(string candidate, string live)
    {
        if (live.Length == 0)
        {
            return false;
        }

        if (string.Equals(candidate, live, StringComparison.Ordinal))
        {
            return true;
        }

        // Guarded by a length floor: without it a short candidate like "好的。" would match any
        // line that happens to contain those characters.
        int shorter = Math.Min(candidate.Length, live.Length);
        return shorter >= 8 &&
            (candidate.IndexOf(live, StringComparison.Ordinal) >= 0 ||
             live.IndexOf(candidate, StringComparison.Ordinal) >= 0);
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
