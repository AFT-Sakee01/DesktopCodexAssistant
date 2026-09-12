using System;

// Cache-only projection of what NativeCaptionReader last read directly from Windows' own
// LiveCaptions.exe. Mirrors TranslatorCaptionSnapshot's shape (immutable clone leaves the owner) but
// carries raw, untranslated English/source text plus the sentence-segmentation state instead of a
// translation pair -- this is the input side of the pipeline TranslatorCaptionSnapshot is the output
// side of.
internal sealed class NativeCaptionSnapshot
{
    internal bool LiveCaptionsRunning { get; set; }

    // True once the CaptionsTextBlock element has been resolved at least once. Same "up but silent
    // vs. actually broken" distinction TranslatorCaptionSnapshot.CaptionElementsResolved makes.
    internal bool CaptionElementResolved { get; set; }

    // The full, cleaned, ever-growing text LiveCaptions.exe is currently showing (after acronym/
    // punctuation/newline cleanup, before any sentence splitting). Diagnostic/debugging value only --
    // nothing downstream should translate this directly, since it is not sentence-bounded.
    internal string FullText { get; set; }

    // The tail sentence currently being recognised/refined, whether or not it has reached a
    // terminator yet. This is what a live caption strip would show for "what's being said right now".
    internal string PendingSentence { get; set; }

    internal DateTime UpdatedUtc { get; set; }

    internal NativeCaptionSnapshot()
    {
        this.FullText = string.Empty;
        this.PendingSentence = string.Empty;
        this.UpdatedUtc = DateTime.MinValue;
    }

    internal static NativeCaptionSnapshot CreateEmpty()
    {
        return new NativeCaptionSnapshot();
    }

    internal NativeCaptionSnapshot Clone()
    {
        return new NativeCaptionSnapshot
        {
            LiveCaptionsRunning = this.LiveCaptionsRunning,
            CaptionElementResolved = this.CaptionElementResolved,
            FullText = this.FullText ?? string.Empty,
            PendingSentence = this.PendingSentence ?? string.Empty,
            UpdatedUtc = this.UpdatedUtc,
        };
    }
}
