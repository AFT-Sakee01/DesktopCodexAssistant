using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// The running article: every sentence the translator finished, in order, as prose rather than as a
// list of subtitle lines.
//
// Only settled sentences go in. The line still being translated is deliberately excluded -- it is
// rewritten several times a second and re-translated from scratch as it grows, so an article built
// from it would rewrite its own last paragraph continuously.
//
// Paragraphs come from silence: when nothing new arrives for ParagraphGapSeconds the speaker has
// moved on, which is the only paragraph signal available here (the recogniser gives sentences, never
// sections). Each paragraph is indented with two full-width spaces, the Chinese convention, and the
// original-language half is indented the same way so the two columns line up visually.
//
// Memory-only, on purpose. The article holds everything said in front of the machine, which is not
// something this app should be accumulating on disk behind the user; the export button is the one
// path that writes it out, and it writes only when pressed. RunSelfTest proves that rather than
// merely claiming it -- "it quietly started persisting" is the regression this rule exists to stop.
internal sealed class CaptionTranscript
{
    // A pause this long reads as a break in the material rather than as the speaker drawing breath.
    internal const double ParagraphGapSeconds = 12.0;
    // Roughly an hour of dense speech. Past this the oldest entries are dropped: the article is for
    // reading back over the session, and an unbounded one would grow without limit in a process that
    // runs for days.
    private const int MaxEntries = 4000;
    private const string ParagraphIndent = "　　";

    private sealed class Entry
    {
        internal string Translated;
        internal string Original;
        internal bool StartsParagraph;
    }

    private readonly object entriesLock = new object();
    private readonly List<Entry> entries = new List<Entry>();
    private DateTime lastAppendUtc = DateTime.MinValue;
    private int revision;

    internal void Append(string translated, string original, DateTime nowUtc)
    {
        string translatedText = (translated ?? string.Empty).Trim();
        string originalText = (original ?? string.Empty).Trim();
        if (translatedText.Length == 0 && originalText.Length == 0)
        {
            return;
        }

        lock (this.entriesLock)
        {
            bool startsParagraph = this.entries.Count == 0 ||
                (this.lastAppendUtc != DateTime.MinValue &&
                 (nowUtc - this.lastAppendUtc).TotalSeconds >= ParagraphGapSeconds);

            this.entries.Add(new Entry
            {
                Translated = translatedText,
                Original = originalText,
                StartsParagraph = startsParagraph,
            });
            this.lastAppendUtc = nowUtc;
            this.revision++;

            while (this.entries.Count > MaxEntries)
            {
                this.entries.RemoveAt(0);
            }
        }
    }

    // Replaces the newest entry in place. The reader collapses a sentence that was merely reworded
    // rather than replaced, and the article has to follow: appending both would print the same thought
    // twice, which is the whole reason the collapse exists.
    internal void ReplaceLatest(string translated, string original)
    {
        string translatedText = (translated ?? string.Empty).Trim();
        if (translatedText.Length == 0)
        {
            return;
        }

        lock (this.entriesLock)
        {
            if (this.entries.Count == 0)
            {
                return;
            }

            Entry latest = this.entries[this.entries.Count - 1];
            latest.Translated = translatedText;
            this.revision++;
            string originalText = (original ?? string.Empty).Trim();
            if (originalText.Length > 0)
            {
                latest.Original = originalText;
            }
        }
    }

    internal void Clear()
    {
        lock (this.entriesLock)
        {
            this.entries.Clear();
            this.lastAppendUtc = DateTime.MinValue;
            this.revision++;
        }
    }

    // Bumped on every change. The board wraps the article into lines to page through it, which is
    // far too expensive to redo on every 500ms repaint; this is what lets it cache that work and
    // rebuild only when the article actually moved.
    internal int Revision
    {
        get
        {
            lock (this.entriesLock)
            {
                return this.revision;
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (this.entriesLock)
            {
                return this.entries.Count;
            }
        }
    }

    // Paragraph text for one half of the article. Returned as separate paragraphs rather than one
    // string so the board can lay them out (and page through them) without re-splitting text it just
    // joined.
    internal List<string> BuildParagraphs(bool translated)
    {
        return BuildParagraphs(translated, 0);
    }

    // maxEntries > 0 keeps only the newest that many sentences. The board uses it because wrapping
    // an hour of speech into lines on every repaint would cost more than the whole rest of the
    // board does; the export path passes 0 and gets everything.
    internal List<string> BuildParagraphs(bool translated, int maxEntries)
    {
        List<string> paragraphs = new List<string>();
        StringBuilder current = new StringBuilder();
        lock (this.entriesLock)
        {
            int first = maxEntries > 0 ? Math.Max(0, this.entries.Count - maxEntries) : 0;
            for (int i = first; i < this.entries.Count; i++)
            {
                Entry entry = this.entries[i];
                string text = translated ? entry.Translated : entry.Original;
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                if (entry.StartsParagraph && current.Length > 0)
                {
                    paragraphs.Add(current.ToString());
                    current.Length = 0;
                }

                if (current.Length == 0)
                {
                    current.Append(ParagraphIndent);
                }
                else if (NeedsSpaceBetween(current[current.Length - 1], text[0]))
                {
                    // Latin sentences need the space that Chinese ones do not.
                    current.Append(' ');
                }

                current.Append(text);
            }

            if (current.Length > 0)
            {
                paragraphs.Add(current.ToString());
            }
        }

        return paragraphs;
    }

    private static bool NeedsSpaceBetween(char previous, char next)
    {
        return previous < 0x2E80 && next < 0x2E80;
    }

    // Plain text for export: the article twice, translation first, then the original language, with
    // the same paragraph breaks. Kept deliberately simple -- this is something a person reads or
    // pastes elsewhere, not a format another tool parses.
    internal string BuildExportText(DateTime nowLocal)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("# 字幕文章 ")
            .Append(nowLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
            .AppendLine();
        builder.AppendLine();
        AppendSection(builder, "## 译文", BuildParagraphs(true));
        builder.AppendLine();
        AppendSection(builder, "## 原文", BuildParagraphs(false));
        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, string title, List<string> paragraphs)
    {
        builder.AppendLine(title);
        builder.AppendLine();
        for (int i = 0; i < paragraphs.Count; i++)
        {
            builder.AppendLine(paragraphs[i]);
            builder.AppendLine();
        }
    }

    internal static void RunSelfTest()
    {
        string exportDirectory = CaptionsBoardForm.ResolveArticleExportDirectory();
        string[] filesBefore = Directory.Exists(exportDirectory)
            ? Directory.GetFiles(exportDirectory)
            : new string[0];

        CaptionTranscript transcript = new CaptionTranscript();
        DateTime start = new DateTime(2026, 9, 13, 1, 0, 0, DateTimeKind.Utc);
        transcript.Append("第一句。", "First sentence.", start);
        transcript.Append("第二句。", "Second sentence.", start.AddSeconds(3));
        // Past the gap: a new paragraph starts here.
        transcript.Append("第三句。", "Third sentence.", start.AddSeconds(3 + ParagraphGapSeconds));

        List<string> translated = transcript.BuildParagraphs(true);
        AssertSelfTest(translated.Count == 2, "a gap longer than the threshold must start a paragraph");
        AssertSelfTest(translated[0] == "　　第一句。第二句。", "sentences in one paragraph run together: " + translated[0]);
        AssertSelfTest(translated[1] == "　　第三句。", "each paragraph is indented by two full-width spaces");

        List<string> original = transcript.BuildParagraphs(false);
        AssertSelfTest(
            original[0] == "　　First sentence. Second sentence.",
            "latin sentences are joined with a space: " + original[0]);

        transcript.ReplaceLatest("第三句改写。", "Third sentence, revised.");
        AssertSelfTest(
            transcript.BuildParagraphs(true)[1] == "　　第三句改写。",
            "a reworded sentence replaces the last entry instead of being appended");

        AssertSelfTest(transcript.Count == 3, "replacing must not change the entry count");
        string export = transcript.BuildExportText(new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Local));
        AssertSelfTest(
            export.Contains("## 译文") && export.Contains("## 原文") && export.Contains("第三句改写。"),
            "the export carries both halves and the latest text");

        List<string> capped = transcript.BuildParagraphs(true, 1);
        AssertSelfTest(
            capped.Count == 1 && capped[0] == "　　第三句改写。",
            "a capped build keeps only the newest entries");

        int revisionBefore = transcript.Revision;
        transcript.Append("第四句。", "Fourth sentence.", start.AddSeconds(60));
        AssertSelfTest(transcript.Revision > revisionBefore, "appending must move the revision");

        transcript.Clear();
        AssertSelfTest(transcript.Count == 0 && transcript.BuildParagraphs(true).Count == 0, "clear empties the article");

        // Everything above exercised append, rewrite, paragraph building, export-text building and
        // clear. If any of them had a persistence side effect, it would have landed by now.
        string[] filesAfter = Directory.Exists(exportDirectory)
            ? Directory.GetFiles(exportDirectory)
            : new string[0];
        AssertSelfTest(
            filesBefore.Length == filesAfter.Length,
            "the article must stay in memory: only the export button may write it to disk, but " +
                exportDirectory + " gained " + (filesAfter.Length - filesBefore.Length) + " file(s)");

        Console.WriteLine("Caption transcript: PASS paragraph gap, indent, latin spacing, replace-latest, capped build, revision, export text, clear, memory-only (no disk write outside the export button)");
    }

    private static void AssertSelfTest(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Caption transcript self-test failed: " + message);
        }
    }
}
