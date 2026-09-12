using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

// Reads the live caption text directly out of Windows' own LiveCaptions.exe (not out of
// LiveCaptionsTranslator's re-displayed window -- see Core/TranslatorCaptionReader.cs for that older
// path). This is step 1 of replacing the LiveCaptionsTranslator dependency: own caption capture and
// sentence segmentation, verified in isolation via --diagnose-native-captions, before anything here
// is wired into a translation call.
//
// The window class name and the caption text block's AutomationId were reverse-engineered from
// LiveCaptionsTranslator's own open-source code (src/utils/LiveCaptionsHandler.cs: ClassName ==
// "LiveCaptionsDesktopWindow", FindElementByAId(window, "CaptionsTextBlock")) -- Windows does not
// document either. Core/LiveCaptionsWindowTidy.cs already matches the same class name to hide this
// window while LiveCaptionsTranslator drives it; this reader is the first thing in this repo to read
// its text.
//
// Sentence segmentation (idle/sync throttling below) ports LiveCaptionsTranslator's own
// Translator.SyncLoop, because the problem it solves is not specific to that app: LiveCaptions hands
// back one continuously growing, continuously self-correcting block of text, not discrete sentences,
// and something has to decide when a stretch of it is "done enough to translate". The tick-count
// thresholds (MaxIdleTicks/MaxSyncTicks below) only mean what upstream intended if this reader is
// actually polled close to every RefreshIntervalMs -- unlike TranslatorCaptionReader (which only
// displays already-finished translations and can afford to poll every 250ms), whoever drives this
// reader in the real pipeline needs a tight background loop, not a UI timer tick, or these thresholds
// need re-deriving against the slower cadence.
//
// Threading: every public entry is called from a background thread. UI Automation calls cross a
// process boundary and can block, so nothing here may be called from the UI thread.
internal sealed class NativeCaptionReader
{
    private const string LiveCaptionsWindowClassName = "LiveCaptionsDesktopWindow";
    private const string CaptionsTextBlockAutomationId = "CaptionsTextBlock";

    // Matches upstream's own SyncLoop cadence (Translator.cs: Thread.Sleep(25) per iteration) --
    // the tick-count thresholds below were tuned by upstream against this exact interval.
    internal const int RefreshIntervalMs = 25;
    private const int ResolveRetryIntervalMs = 2000;

    // Ported from LiveCaptionsTranslator's Setting defaults (MaxIdleInterval=50, MaxSyncInterval=3)
    // and TextUtil (SHORT_THRESHOLD=10, MEDIUM_THRESHOLD=40). These are the values upstream shipped
    // after tuning against real speech; expect to re-tune by ear once this is wired to real audio.
    private const int MaxIdleTicks = 50;
    private const int MaxSyncTicks = 3;
    private const int ShortSentenceByteThreshold = 10;
    private const int NewlineMergeByteThreshold = 40;

    private static readonly char[] SentenceTerminators = ".?!。？！".ToCharArray();

    // Same four cleanup passes as upstream's RegexPatterns/TextUtil, ported to plain compiled Regex
    // instead of C# source-generated [GeneratedRegex] partials: this project has no .NET SDK-style
    // source-generator step, only a hand-curated csc.exe /reference: list against classic .NET
    // Framework 4.x (see Core/TranslatorControlReader.cs's JSON-serializer comment for the same
    // toolchain constraint). RegexOptions.Compiled matches this codebase's own convention (see
    // Core/CodexTaskMonitorReader.cs's gate patterns).
    private static readonly Regex AcronymPattern = new Regex(
        @"([A-Z])\s*\.\s*([A-Z])(?![A-Za-z]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AcronymWithWordsPattern = new Regex(
        @"([A-Z])\s*\.\s*([A-Z])(?=[A-Za-z]+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PunctuationSpacePattern = new Regex(
        @"\s*([.!?,])\s*", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CjPunctuationSpacePattern = new Regex(
        @"\s*([。！？，、])\s*", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly object stateLock = new object();

    private IntPtr resolvedWindowHandle = IntPtr.Zero;
    private AutomationElement captionsTextElement;
    private DateTime lastRefreshUtc = DateTime.MinValue;
    private DateTime lastResolveAttemptUtc = DateTime.MinValue;
    private int refreshing;

    // Throttle state, persisted tick-to-tick. Reset whenever LiveCaptions' window disappears --
    // matches upstream's own reasoning for ClearContexts(): a sentence half-built by a previous
    // LiveCaptions session must not bleed into a new one.
    private string lastOriginalCaption = string.Empty;
    private int idleTicks;
    private int syncTicks;

    private readonly Queue<string> readyQueue = new Queue<string>();
    private NativeCaptionSnapshot snapshot = NativeCaptionSnapshot.CreateEmpty();

    // Cache-only: returns a clone of whatever the last completed tick published. Safe to call from
    // any thread.
    internal NativeCaptionSnapshot GetSnapshot()
    {
        lock (this.stateLock)
        {
            return this.snapshot.Clone();
        }
    }

    // Pops one sentence considered "ready to translate" -- may be called several times for what is
    // conceptually the same evolving sentence (once early via the sync-tick early-flush, again via
    // the idle-tick fallback, once more when it finally reaches a terminator). Deduping/cancelling
    // stale in-flight translations of an earlier, shorter version of the same sentence is the
    // consuming translation client's job (mirrors upstream's own split between Translator.SyncLoop,
    // which enqueues eagerly, and TranslationTaskQueue, which cancels superseded work) -- this
    // reader does not attempt it.
    internal bool TryDequeueReady(out string sentence)
    {
        lock (this.stateLock)
        {
            if (this.readyQueue.Count == 0)
            {
                sentence = null;
                return false;
            }

            sentence = this.readyQueue.Dequeue();
            return true;
        }
    }

    internal void RefreshIfDue(DateTime nowUtc, bool force)
    {
        if (!force &&
            this.lastRefreshUtc != DateTime.MinValue &&
            (nowUtc - this.lastRefreshUtc).TotalMilliseconds < RefreshIntervalMs)
        {
            return;
        }

        this.lastRefreshUtc = nowUtc;

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
        NativeCaptionSnapshot next = NativeCaptionSnapshot.CreateEmpty();
        next.UpdatedUtc = nowUtc;

        IntPtr windowHandle;
        bool windowFound = NativeMethods.TryFindWindowByClassName(LiveCaptionsWindowClassName, out windowHandle);
        next.LiveCaptionsRunning = windowFound;
        if (!windowFound)
        {
            ReleaseElement();
            ResetThrottleState();
            Publish(next);
            return;
        }

        if (!EnsureElement(windowHandle, nowUtc))
        {
            Publish(next);
            return;
        }

        string rawText;
        if (!TryReadRawText(out rawText))
        {
            ReleaseElement();
            Publish(next);
            return;
        }

        next.CaptionElementResolved = true;
        if (string.IsNullOrEmpty(rawText))
        {
            Publish(next);
            return;
        }

        string fullText = CleanText(rawText);
        next.FullText = fullText;

        string originalCaption = ExtractOriginalCaption(fullText);
        next.PendingSentence = originalCaption;

        AdvanceThrottle(originalCaption);
        Publish(next);
    }

    // Ports Translator.SyncLoop's sentence-extraction (the "get the last sentence, merge back if too
    // short" steps) minus the Overlay/DisplaySentences history-walk, which is a display concern that
    // belongs downstream of this reader, not in caption capture.
    private static string ExtractOriginalCaption(string fullText)
    {
        int lastEosIndex;
        if (Array.IndexOf(SentenceTerminators, fullText[fullText.Length - 1]) != -1)
        {
            lastEosIndex = fullText.Substring(0, fullText.Length - 1).LastIndexOfAny(SentenceTerminators);
        }
        else
        {
            lastEosIndex = fullText.LastIndexOfAny(SentenceTerminators);
        }

        string latestCaption = fullText.Substring(lastEosIndex + 1);

        // LiveCaptions can emit several characters (including a terminator) in one recognition
        // update; if the tail this leaves is implausibly short, merge it back with the sentence
        // before it rather than treating a stray fragment as its own sentence.
        if (lastEosIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < ShortSentenceByteThreshold)
        {
            lastEosIndex = fullText.Substring(0, lastEosIndex).LastIndexOfAny(SentenceTerminators);
            latestCaption = fullText.Substring(lastEosIndex + 1);
        }

        // Defensive trim: keep only up to the tail's own last terminator, so a stray fragment of the
        // next sentence appended after it (should the extraction above ever leave one) never rides
        // along into what gets sent for translation.
        int trailingEos = latestCaption.LastIndexOfAny(SentenceTerminators);
        if (trailingEos != -1)
        {
            latestCaption = latestCaption.Substring(0, trailingEos + 1);
        }

        return latestCaption;
    }

    // Ports Translator.SyncLoop's idle/sync counters verbatim (see class comment for why the tick
    // rate this is driven at has to match RefreshIntervalMs for the thresholds to mean what upstream
    // intended).
    private void AdvanceThrottle(string originalCaption)
    {
        bool changed = string.CompareOrdinal(this.lastOriginalCaption, originalCaption) != 0;
        if (changed)
        {
            this.idleTicks = 0;
            if (originalCaption.Length > 0 &&
                Array.IndexOf(SentenceTerminators, originalCaption[originalCaption.Length - 1]) != -1)
            {
                this.syncTicks = 0;
                Enqueue(originalCaption);
            }
            else if (Encoding.UTF8.GetByteCount(originalCaption) >= ShortSentenceByteThreshold)
            {
                this.syncTicks++;
            }
        }
        else
        {
            this.idleTicks++;
        }

        if (this.syncTicks > MaxSyncTicks || this.idleTicks == MaxIdleTicks)
        {
            this.syncTicks = 0;
            if (originalCaption.Length > 0)
            {
                Enqueue(originalCaption);
            }
        }

        this.lastOriginalCaption = originalCaption;
    }

    private void Enqueue(string sentence)
    {
        lock (this.stateLock)
        {
            this.readyQueue.Enqueue(sentence);
        }
    }

    private void ResetThrottleState()
    {
        this.lastOriginalCaption = string.Empty;
        this.idleTicks = 0;
        this.syncTicks = 0;
        lock (this.stateLock)
        {
            this.readyQueue.Clear();
        }
    }

    private static string CleanText(string text)
    {
        text = AcronymPattern.Replace(text, "$1$2");
        text = AcronymWithWordsPattern.Replace(text, "$1 $2");
        text = PunctuationSpacePattern.Replace(text, "$1 ");
        text = CjPunctuationSpacePattern.Replace(text, "$1");
        return ReplaceNewlines(text, NewlineMergeByteThreshold);
    }

    // Ports TextUtil.ReplaceNewlines: LiveCaptions (Japanese especially) can emit '\n' mid-sentence;
    // fold each line break into real punctuation instead, so sentence-boundary detection above never
    // has to treat '\n' as a terminator in its own right.
    private static string ReplaceNewlines(string text, int byteThreshold)
    {
        string[] splits = text.Split('\n');
        for (int i = 0; i < splits.Length; i++)
        {
            splits[i] = splits[i].Trim();
            if (i == splits.Length - 1 || splits[i].Length == 0)
            {
                continue;
            }

            char lastChar = splits[i][splits[i].Length - 1];
            bool isCj = IsCjChar(lastChar);
            if (Encoding.UTF8.GetByteCount(splits[i]) >= byteThreshold)
            {
                splits[i] += isCj ? "。" : ". ";
            }
            else
            {
                splits[i] += isCj ? "——" : "—";
            }
        }

        return string.Join(string.Empty, splits);
    }

    private static bool IsCjChar(char ch)
    {
        return (ch >= '一' && ch <= '鿿') ||
            (ch >= '㐀' && ch <= '䶿') ||
            (ch >= '　' && ch <= '〿') ||
            (ch >= '぀' && ch <= 'ゟ') ||
            (ch >= '゠' && ch <= 'ヿ') ||
            (ch >= 'ㇰ' && ch <= 'ㇿ') ||
            (ch >= '㈀' && ch <= '㋿') ||
            (ch >= '㌀' && ch <= '㏿');
    }

    private bool EnsureElement(IntPtr windowHandle, DateTime nowUtc)
    {
        if (this.captionsTextElement != null && this.resolvedWindowHandle == windowHandle)
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

            AutomationElement textBlock = window.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, CaptionsTextBlockAutomationId));
            if (textBlock == null)
            {
                return false;
            }

            this.captionsTextElement = textBlock;
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

    private bool TryReadRawText(out string text)
    {
        text = string.Empty;
        try
        {
            text = this.captionsTextElement.Current.Name ?? string.Empty;
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

    private void ReleaseElement()
    {
        this.captionsTextElement = null;
        this.resolvedWindowHandle = IntPtr.Zero;
    }

    private void Publish(NativeCaptionSnapshot next)
    {
        lock (this.stateLock)
        {
            this.snapshot = next;
        }
    }
}
