using System;
using System.Collections.Generic;

// Read-only DTO for the eighth left-dock board (Captions/字幕). The presentation surface receives a
// clone of this object and must never reach back into TranslatorControlReader, the external
// setting.json/translation_history.db files, or the monitored processes. Matches the shape of
// ResetSpeedBoardSnapshot / CodexIqBoardSnapshot: CreateEmpty() + instance Clone().
internal sealed class TranslatorControlSnapshot
{
    // Process/service state. IsRunning tracks LiveCaptionsTranslator.exe only -- that is what the
    // header dot/label and the start/stop toolbar button both represent. GenieX and the sanitize
    // proxy are monitored for completeness (and possible future surfacing) but are never
    // started/stopped by this board: GenieX reload is ~10s and must not be triggered casually.
    public bool IsRunning { get; set; }
    public bool GenieXRunning { get; set; }
    public bool SanitizeProxyRunning { get; set; }
    public bool RestartInProgress { get; set; }

    public bool SettingsFileFound { get; set; }
    public bool ContextAwareKnown { get; set; }
    public bool ContextAware { get; set; }
    public bool NumContextsKnown { get; set; }
    public int NumContexts { get; set; }
    public bool ModelNameKnown { get; set; }
    public string ModelName { get; set; }
    public string ApiUrl { get; set; }

    // Cached list of fully-downloaded models discovered under the GenieX model cache, as full
    // "org/model:variant" identifiers usable directly as the setting.json ModelName value. Never
    // hardcoded -- rebuilt from disk each refresh so a newly downloaded/removed model is picked up.
    public List<string> AvailableModels { get; private set; }

    public bool LastSuccessKnown { get; set; }
    public DateTime LastSuccessLocal { get; set; }

    public bool HistoryDatabaseFound { get; set; }
    public List<TranslatorHistoryEntry> RecentHistory { get; private set; }

    public static TranslatorControlSnapshot CreateEmpty()
    {
        return new TranslatorControlSnapshot
        {
            ModelName = string.Empty,
            ApiUrl = string.Empty,
            AvailableModels = new List<string>(),
            RecentHistory = new List<TranslatorHistoryEntry>()
        };
    }

    public TranslatorControlSnapshot Clone()
    {
        TranslatorControlSnapshot clone = CreateEmpty();
        clone.IsRunning = this.IsRunning;
        clone.GenieXRunning = this.GenieXRunning;
        clone.SanitizeProxyRunning = this.SanitizeProxyRunning;
        clone.RestartInProgress = this.RestartInProgress;
        clone.SettingsFileFound = this.SettingsFileFound;
        clone.ContextAwareKnown = this.ContextAwareKnown;
        clone.ContextAware = this.ContextAware;
        clone.NumContextsKnown = this.NumContextsKnown;
        clone.NumContexts = this.NumContexts;
        clone.ModelNameKnown = this.ModelNameKnown;
        clone.ModelName = this.ModelName;
        clone.ApiUrl = this.ApiUrl;
        clone.LastSuccessKnown = this.LastSuccessKnown;
        clone.LastSuccessLocal = this.LastSuccessLocal;
        clone.HistoryDatabaseFound = this.HistoryDatabaseFound;
        for (int i = 0; i < this.AvailableModels.Count; i++)
        {
            clone.AvailableModels.Add(this.AvailableModels[i]);
        }

        for (int i = 0; i < this.RecentHistory.Count; i++)
        {
            if (this.RecentHistory[i] != null)
            {
                clone.RecentHistory.Add(this.RecentHistory[i].Clone());
            }
        }

        return clone;
    }
}

internal sealed class TranslatorHistoryEntry
{
    public bool TimestampKnown { get; set; }
    public DateTime TimestampLocal { get; set; }
    public string SourceText { get; set; }
    public string TranslatedText { get; set; }
    public string TargetLanguage { get; set; }
    public bool IsError { get; set; }

    public TranslatorHistoryEntry Clone()
    {
        return (TranslatorHistoryEntry)this.MemberwiseClone();
    }
}
