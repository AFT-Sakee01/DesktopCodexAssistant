using System;

// Cache-only projection of what LiveCaptionsTranslator is showing right now. Published by
// TranslatorCaptionReader and consumed by CaptionOverlayForm; like every other reader snapshot in
// this app it is cloned before it leaves the owner, so the UI never holds reader-owned state.
internal sealed class TranslatorCaptionSnapshot
{
    internal bool TranslatorRunning { get; set; }

    // True once the two caption text blocks have been resolved at least once. Distinguishes "the
    // translator is up but has not said anything yet" from "we cannot read it", which the overlay
    // needs: the first is a blank screen on purpose, the second is a fault worth reporting.
    internal bool CaptionElementsResolved { get; set; }

    internal string OriginalCaption { get; set; }

    internal string TranslatedCaption { get; set; }

    internal DateTime UpdatedUtc { get; set; }

    internal TranslatorCaptionSnapshot()
    {
        this.OriginalCaption = string.Empty;
        this.TranslatedCaption = string.Empty;
        this.UpdatedUtc = DateTime.MinValue;
    }

    internal static TranslatorCaptionSnapshot CreateEmpty()
    {
        return new TranslatorCaptionSnapshot();
    }

    internal TranslatorCaptionSnapshot Clone()
    {
        return new TranslatorCaptionSnapshot
        {
            TranslatorRunning = this.TranslatorRunning,
            CaptionElementsResolved = this.CaptionElementsResolved,
            OriginalCaption = this.OriginalCaption ?? string.Empty,
            TranslatedCaption = this.TranslatedCaption ?? string.Empty,
            UpdatedUtc = this.UpdatedUtc,
        };
    }

    // Everything the overlay draws, in one comparable string. The overlay repaints only when this
    // changes -- caption text updates a few times a second and an unconditional repaint of a
    // full-width layered window would be the most expensive thing this app does.
    internal string BuildRenderSignature()
    {
        return (this.TranslatorRunning ? "1" : "0") +
            (this.CaptionElementsResolved ? "1" : "0") + "|" +
            (this.TranslatedCaption ?? string.Empty) + "|" +
            (this.OriginalCaption ?? string.Empty);
    }

    internal bool HasText()
    {
        return !string.IsNullOrWhiteSpace(this.TranslatedCaption) ||
            !string.IsNullOrWhiteSpace(this.OriginalCaption);
    }
}
