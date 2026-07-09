using System;
using System.Collections.Generic;
using System.Drawing;

internal enum ClaudeRadarServiceState
{
    Unknown,
    Normal,
    Offline,
    Incomplete,
    Unavailable,
    Unreachable
}

internal sealed class ClaudeRadarSnapshot
{
    public bool Known { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public DateTime CheckedAtLocal { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
    public ClaudeRadarServiceState DataState { get; set; }
    public ClaudeRadarServiceState RatingsState { get; set; }
    public ClaudeRadarServiceState ClaudeStatusState { get; set; }
    public ClaudeRadarServiceState ClaudeCodeState { get; set; }
    public string ClaudeCodeErrorCode { get; set; }
    public string SelectedModelKey { get; set; }
    public string SelectedModelName { get; set; }
    public List<ClaudeRadarModelEntry> Models { get; set; }
    public List<ClaudeRadarModelMetric> ModelMetrics { get; set; }
    public ClaudeRadarModelMetric SelectedModel { get; set; }
    public ClaudeRadarQuotaSnapshot Quota { get; set; }
    public ClaudeRadarQuotaLineSnapshot QuotaLine { get; set; }
    public ClaudeRadarCommunitySnapshot Community { get; set; }
    public List<ClaudeRadarModelCatalogEvent> ModelCatalogEvents { get; set; }
    public string SiteUpdatedAtText { get; set; }
    public DateTime SiteUpdatedAtUtc { get; set; }
    public bool SiteUpdatedAtKnown { get; set; }
    public bool TestMode { get; set; }
    public bool RequestRunning { get; set; }

    public static ClaudeRadarSnapshot CreateDefault()
    {
        return new ClaudeRadarSnapshot
        {
            Known = false,
            CheckedAtUtc = DateTime.MinValue,
            CheckedAtLocal = DateTime.MinValue,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty,
            DataState = ClaudeRadarServiceState.Unknown,
            RatingsState = ClaudeRadarServiceState.Unknown,
            ClaudeStatusState = ClaudeRadarServiceState.Unknown,
            ClaudeCodeState = ClaudeRadarServiceState.Unknown,
            ClaudeCodeErrorCode = string.Empty,
            SelectedModelKey = string.Empty,
            SelectedModelName = string.Empty,
            Models = new List<ClaudeRadarModelEntry>(),
            ModelMetrics = new List<ClaudeRadarModelMetric>(),
            SelectedModel = ClaudeRadarModelMetric.CreateDefault(),
            Quota = ClaudeRadarQuotaSnapshot.CreateDefault(),
            QuotaLine = ClaudeRadarQuotaLineSnapshot.CreateDefault(),
            Community = ClaudeRadarCommunitySnapshot.CreateDefault(),
            ModelCatalogEvents = new List<ClaudeRadarModelCatalogEvent>(),
            SiteUpdatedAtText = string.Empty,
            SiteUpdatedAtUtc = DateTime.MinValue,
            SiteUpdatedAtKnown = false,
            TestMode = false,
            RequestRunning = false
        };
    }

    public ClaudeRadarSnapshot Clone()
    {
        ClaudeRadarSnapshot clone = new ClaudeRadarSnapshot
        {
            Known = this.Known,
            CheckedAtUtc = this.CheckedAtUtc,
            CheckedAtLocal = this.CheckedAtLocal,
            ErrorCode = this.ErrorCode,
            ErrorMessage = this.ErrorMessage,
            DataState = this.DataState,
            RatingsState = this.RatingsState,
            ClaudeStatusState = this.ClaudeStatusState,
            ClaudeCodeState = this.ClaudeCodeState,
            ClaudeCodeErrorCode = this.ClaudeCodeErrorCode,
            SelectedModelKey = this.SelectedModelKey,
            SelectedModelName = this.SelectedModelName,
            Models = new List<ClaudeRadarModelEntry>(),
            ModelMetrics = new List<ClaudeRadarModelMetric>(),
            SelectedModel = this.SelectedModel == null ? ClaudeRadarModelMetric.CreateDefault() : this.SelectedModel.Clone(),
            Quota = this.Quota == null ? ClaudeRadarQuotaSnapshot.CreateDefault() : this.Quota.Clone(),
            QuotaLine = this.QuotaLine == null ? ClaudeRadarQuotaLineSnapshot.CreateDefault() : this.QuotaLine.Clone(),
            Community = this.Community == null ? ClaudeRadarCommunitySnapshot.CreateDefault() : this.Community.Clone(),
            ModelCatalogEvents = new List<ClaudeRadarModelCatalogEvent>(),
            SiteUpdatedAtText = this.SiteUpdatedAtText,
            SiteUpdatedAtUtc = this.SiteUpdatedAtUtc,
            SiteUpdatedAtKnown = this.SiteUpdatedAtKnown,
            TestMode = this.TestMode,
            RequestRunning = this.RequestRunning
        };

        if (this.Models != null)
        {
            for (int i = 0; i < this.Models.Count; i++)
            {
                if (this.Models[i] != null)
                {
                    clone.Models.Add(this.Models[i].Clone());
                }
            }
        }

        if (this.ModelMetrics != null)
        {
            for (int i = 0; i < this.ModelMetrics.Count; i++)
            {
                if (this.ModelMetrics[i] != null)
                {
                    clone.ModelMetrics.Add(this.ModelMetrics[i].Clone());
                }
            }
        }

        if (this.ModelCatalogEvents != null)
        {
            for (int i = 0; i < this.ModelCatalogEvents.Count; i++)
            {
                if (this.ModelCatalogEvents[i] != null)
                {
                    clone.ModelCatalogEvents.Add(this.ModelCatalogEvents[i].Clone());
                }
            }
        }

        return clone;
    }
}

internal enum ClaudeRadarModelCatalogEventKind
{
    Added,
    Reappeared,
    TemporarilyMissing,
    Deleted
}

internal sealed class ClaudeRadarModelCatalogEvent
{
    public ClaudeRadarModelCatalogEventKind Kind { get; set; }
    public string SourceKey { get; set; }
    public string DisplayName { get; set; }
    public string Status { get; set; }

    public ClaudeRadarModelCatalogEvent Clone()
    {
        return new ClaudeRadarModelCatalogEvent
        {
            Kind = this.Kind,
            SourceKey = this.SourceKey,
            DisplayName = this.DisplayName,
            Status = this.Status
        };
    }
}

internal sealed class ClaudeRadarModelEntry
{
    public string SourceKey { get; set; }
    public string DisplayName { get; set; }
    public string RatingKey { get; set; }
    public bool Enabled { get; set; }
    public bool HistoricalOnly { get; set; }
    public string Status { get; set; }
    public int SortOrder { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public int MissingSuccessCount { get; set; }
    public Color Color { get; set; }

    public ClaudeRadarModelEntry Clone()
    {
        return new ClaudeRadarModelEntry
        {
            SourceKey = this.SourceKey,
            DisplayName = this.DisplayName,
            RatingKey = this.RatingKey,
            Enabled = this.Enabled,
            HistoricalOnly = this.HistoricalOnly,
            Status = this.Status,
            SortOrder = this.SortOrder,
            LastSeenUtc = this.LastSeenUtc,
            MissingSuccessCount = this.MissingSuccessCount,
            Color = this.Color
        };
    }
}

internal sealed class ClaudeRadarModelMetric
{
    public bool Known { get; set; }
    public string SourceKey { get; set; }
    public string Name { get; set; }
    public int IqScore { get; set; }
    public int Passed { get; set; }
    public int ValidTasks { get; set; }
    public int TokenEfficiencyPercent { get; set; }
    public int TimeEfficiencyPercent { get; set; }
    public double TotalTokens { get; set; }
    public double CostUsd { get; set; }
    public double Hours { get; set; }
    public string LatestLabel { get; set; }
    public DateTime LatestAtUtc { get; set; }
    public bool LatestAtKnown { get; set; }
    public bool HistoricalOnly { get; set; }
    public int NormalLow { get; set; }
    public int NormalHigh { get; set; }
    public string StatusText { get; set; }
    public string EfficiencyText { get; set; }

    public static ClaudeRadarModelMetric CreateDefault()
    {
        return new ClaudeRadarModelMetric
        {
            Known = false,
            SourceKey = string.Empty,
            Name = string.Empty,
            IqScore = 0,
            Passed = 0,
            ValidTasks = 10,
            TokenEfficiencyPercent = 100,
            TimeEfficiencyPercent = 100,
            TotalTokens = 0.0,
            CostUsd = 0.0,
            Hours = 0.0,
            LatestLabel = string.Empty,
            LatestAtUtc = DateTime.MinValue,
            LatestAtKnown = false,
            HistoricalOnly = false,
            NormalLow = 90,
            NormalHigh = 110,
            StatusText = "未知",
            EfficiencyText = "普通"
        };
    }

    public ClaudeRadarModelMetric Clone()
    {
        return new ClaudeRadarModelMetric
        {
            Known = this.Known,
            SourceKey = this.SourceKey,
            Name = this.Name,
            IqScore = this.IqScore,
            Passed = this.Passed,
            ValidTasks = this.ValidTasks,
            TokenEfficiencyPercent = this.TokenEfficiencyPercent,
            TimeEfficiencyPercent = this.TimeEfficiencyPercent,
            TotalTokens = this.TotalTokens,
            CostUsd = this.CostUsd,
            Hours = this.Hours,
            LatestLabel = this.LatestLabel,
            LatestAtUtc = this.LatestAtUtc,
            LatestAtKnown = this.LatestAtKnown,
            HistoricalOnly = this.HistoricalOnly,
            NormalLow = this.NormalLow,
            NormalHigh = this.NormalHigh,
            StatusText = this.StatusText,
            EfficiencyText = this.EfficiencyText
        };
    }
}

internal sealed class ClaudeRadarQuotaSnapshot
{
    public bool Known { get; set; }
    public int FiveHourPercent { get; set; }
    public int WeeklyPercent { get; set; }
    public string FiveHourResetText { get; set; }
    public string WeeklyResetText { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public bool UpdatedAtKnown { get; set; }

    public static ClaudeRadarQuotaSnapshot CreateDefault()
    {
        return new ClaudeRadarQuotaSnapshot
        {
            Known = false,
            FiveHourPercent = 0,
            WeeklyPercent = 0,
            FiveHourResetText = "N/A",
            WeeklyResetText = "N/A",
            UpdatedAtUtc = DateTime.MinValue,
            UpdatedAtKnown = false
        };
    }

    public ClaudeRadarQuotaSnapshot Clone()
    {
        return new ClaudeRadarQuotaSnapshot
        {
            Known = this.Known,
            FiveHourPercent = this.FiveHourPercent,
            WeeklyPercent = this.WeeklyPercent,
            FiveHourResetText = this.FiveHourResetText,
            WeeklyResetText = this.WeeklyResetText,
            UpdatedAtUtc = this.UpdatedAtUtc,
            UpdatedAtKnown = this.UpdatedAtKnown
        };
    }
}

internal sealed class ClaudeRadarQuotaLineSnapshot
{
    public bool Known { get; set; }
    public double CurrentValue { get; set; }
    public bool PreviousKnown { get; set; }
    public double PreviousValue { get; set; }
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public double AverageValue { get; set; }
    public bool AverageKnown { get; set; }
    public string Metric { get; set; }
    public string SourceMode { get; set; }

    public static ClaudeRadarQuotaLineSnapshot CreateDefault()
    {
        return new ClaudeRadarQuotaLineSnapshot
        {
            Known = false,
            CurrentValue = 0.0,
            PreviousKnown = false,
            PreviousValue = 0.0,
            MinValue = 0.0,
            MaxValue = 0.0,
            AverageValue = 0.0,
            AverageKnown = false,
            Metric = "base_d7",
            SourceMode = "unknown"
        };
    }

    public ClaudeRadarQuotaLineSnapshot Clone()
    {
        return new ClaudeRadarQuotaLineSnapshot
        {
            Known = this.Known,
            CurrentValue = this.CurrentValue,
            PreviousKnown = this.PreviousKnown,
            PreviousValue = this.PreviousValue,
            MinValue = this.MinValue,
            MaxValue = this.MaxValue,
            AverageValue = this.AverageValue,
            AverageKnown = this.AverageKnown,
            Metric = this.Metric,
            SourceMode = this.SourceMode
        };
    }
}

internal sealed class ClaudeRadarCommunitySnapshot
{
    public bool Known { get; set; }
    public string RatingKey { get; set; }
    public string Label { get; set; }
    public double Average { get; set; }
    public int Count { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public int RefreshSeconds { get; set; }

    public static ClaudeRadarCommunitySnapshot CreateDefault()
    {
        return new ClaudeRadarCommunitySnapshot
        {
            Known = false,
            RatingKey = string.Empty,
            Label = string.Empty,
            Average = 0.0,
            Count = 0,
            UpdatedAtUtc = DateTime.MinValue,
            RefreshSeconds = 900
        };
    }

    public ClaudeRadarCommunitySnapshot Clone()
    {
        return new ClaudeRadarCommunitySnapshot
        {
            Known = this.Known,
            RatingKey = this.RatingKey,
            Label = this.Label,
            Average = this.Average,
            Count = this.Count,
            UpdatedAtUtc = this.UpdatedAtUtc,
            RefreshSeconds = this.RefreshSeconds
        };
    }
}
