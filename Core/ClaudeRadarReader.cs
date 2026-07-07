using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

internal static class ClaudeRadarReader
{
    private const string DataUrl = "https://claudecoderadar.com/data/claude-code-radar.json";
    private const string HomeUrl = "https://claudecoderadar.com/";
    private const string RatingsUrl = "https://claudecoderadar.com/api/model-ratings?history=14";
    private const string ClaudeStatusUrl = "https://status.claude.com/api/v2/summary.json";
    private const int RequestTimeoutMs = 10000;
    private const int ModelDeleteMissingThreshold = 3;
    private static readonly object MapLock = new object();
    private static readonly object HistoryLock = new object();
    private static readonly object ClaudeCodeQuotaCacheLock = new object();
    private static string storageDirectoryOverride = string.Empty;

    public static string CachePath
    {
        get { return Path.Combine(GetStorageDirectoryPath(), "claude-radar-cache.ini"); }
    }

    public static string ModelMapPath
    {
        get { return Path.Combine(GetStorageDirectoryPath(), "claude-radar-model-map.ini"); }
    }

    public static string QuotaHistoryPath
    {
        get { return Path.Combine(GetStorageDirectoryPath(), "claude-radar-quota-history.jsonl"); }
    }

    public static string ClaudeCodeQuotaCachePath
    {
        get { return Path.Combine(GetStorageDirectoryPath(), "claude-quota.ini"); }
    }

    private static string GetStorageDirectoryPath()
    {
        return string.IsNullOrWhiteSpace(storageDirectoryOverride)
            ? Logger.DirectoryPath
            : storageDirectoryOverride;
    }

    public static void TryWriteClaudeCodeQuotaCache(ClaudeCodeUsageSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        List<string> lines = new List<string>();
        lines.Add("Version=1");
        lines.Add("FiveHourPercent=" + ClampPercent(snapshot.FiveHourPercent).ToString(CultureInfo.InvariantCulture));
        lines.Add("WeeklyPercent=" + ClampPercent(snapshot.WeeklyPercent).ToString(CultureInfo.InvariantCulture));
        if (snapshot.FiveHourResetKnown)
        {
            lines.Add("FiveHourReset=" + snapshot.FiveHourResetLocal.ToString("o", CultureInfo.InvariantCulture));
        }

        if (snapshot.WeeklyResetKnown)
        {
            lines.Add("WeeklyReset=" + snapshot.WeeklyResetLocal.ToString("o", CultureInfo.InvariantCulture));
        }

        if (snapshot.SourceUpdatedKnown)
        {
            lines.Add("SourceUpdatedUtc=" + snapshot.SourceUpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
        }

        TryWriteClaudeCodeQuotaCacheLines(lines);
    }

    private static void TryWriteClaudeCodeQuotaCacheLines(List<string> lines)
    {
        string tempPath = string.Empty;
        try
        {
            string path = ClaudeCodeQuotaCachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string next = string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
            lock (ClaudeCodeQuotaCacheLock)
            {
                if (File.Exists(path) && string.Equals(File.ReadAllText(path), next, StringComparison.Ordinal))
                {
                    return;
                }

                tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempPath, next, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }

            Program.LogException(ex);
        }
    }

    public static ClaudeRadarSnapshot ReadSnapshot(
        string selectedModelKey,
        bool jsonEnabled,
        bool homepageFallbackEnabled,
        bool ratingsEnabled,
        bool localQuotaFallbackEnabled)
    {
        ClaudeRadarSnapshot snapshot = ClaudeRadarSnapshot.CreateDefault();
        snapshot.CheckedAtUtc = DateTime.UtcNow;
        snapshot.CheckedAtLocal = DateTime.Now;
        if (!jsonEnabled)
        {
            ApplyJsonDisabled(snapshot);
            return snapshot;
        }

        Dictionary<string, object> dataRoot;
        string dataErrorCode;
        string dataErrorMessage;
        if (!TryFetchJson(DataUrl, out dataRoot, out dataErrorCode, out dataErrorMessage))
        {
            ApplyDataFetchFailure(snapshot, dataErrorCode, dataErrorMessage, IsOffline());
            ClaudeRadarHomepageMetadata failedDataMetadata;
            if (homepageFallbackEnabled &&
                TryFetchHomepageMetadata(out failedDataMetadata) &&
                failedDataMetadata.ModelNames.Count > 0)
            {
                List<ClaudeRadarModelMetric> fallbackMetrics = BuildHomepageModelMetrics(failedDataMetadata);
                ClaudeRadarModelMapUpdate fallbackMapUpdate = UpdateModelMap(fallbackMetrics, null, false);
                snapshot.Models = fallbackMapUpdate.Entries;
                snapshot.ModelCatalogEvents = fallbackMapUpdate.Events;
                string fallbackSelectedKey = NormalizeSourceKey(selectedModelKey);
                if (fallbackSelectedKey.Length == 0 || !ContainsModel(fallbackMetrics, fallbackSelectedKey))
                {
                    fallbackSelectedKey = PickDefaultModel(fallbackMetrics, fallbackMapUpdate.Entries);
                }

                ClaudeRadarModelMetric fallbackMetric = FindMetric(fallbackMetrics, fallbackSelectedKey);
                snapshot.SelectedModelKey = fallbackSelectedKey;
                snapshot.SelectedModelName = fallbackMetric == null ? string.Empty : fallbackMetric.Name;
                snapshot.ModelMetrics = CloneModelMetrics(fallbackMetrics);
                snapshot.SelectedModel = fallbackMetric == null ? ClaudeRadarModelMetric.CreateDefault() : fallbackMetric.Clone();
                snapshot.DataState = ClaudeRadarServiceState.Incomplete;
                snapshot.ErrorCode = string.IsNullOrWhiteSpace(dataErrorCode) ? "HOME_METADATA" : dataErrorCode;
                snapshot.ErrorMessage = "首页metadata fallback";
            }

            snapshot.ClaudeStatusState = ReadClaudePublicStatusState();
            return snapshot;
        }

        if (!ReadBool(dataRoot, "ok", true))
        {
            snapshot.ClaudeStatusState = ReadClaudePublicStatusState();
            ApplyUnavailableDataRoot(snapshot, dataRoot);
            return snapshot;
        }

        Dictionary<string, object> ratingsRoot = null;
        if (ratingsEnabled)
        {
            string ratingErrorCode;
            string ratingErrorMessage;
            if (!TryFetchJson(RatingsUrl, out ratingsRoot, out ratingErrorCode, out ratingErrorMessage))
            {
                snapshot.RatingsState = IsOffline() ? ClaudeRadarServiceState.Offline : ClaudeRadarServiceState.Unreachable;
            }
            else
            {
                snapshot.RatingsState = ClaudeRadarServiceState.Normal;
            }
        }

        snapshot.ClaudeStatusState = ReadClaudePublicStatusState();
        List<ClaudeRadarModelMetric> metrics = ParseModelMetrics(dataRoot);
        bool completeModelCatalog = IsCompleteModelCatalog(dataRoot, metrics);
        if (homepageFallbackEnabled && NeedsHomepageMetadata(metrics))
        {
            ClaudeRadarHomepageMetadata metadata;
            if (TryFetchHomepageMetadata(out metadata))
            {
                if (metrics.Count == 0)
                {
                    metrics = BuildHomepageModelMetrics(metadata);
                    completeModelCatalog = false;
                }
                else
                {
                    ApplyHomepageModelNames(metrics, metadata);
                }
            }
        }

        ClaudeRadarModelMapUpdate mapUpdate = UpdateModelMap(metrics, ratingsRoot, completeModelCatalog);
        List<ClaudeRadarModelEntry> entries = mapUpdate.Entries;
        snapshot.Models = entries;
        snapshot.ModelCatalogEvents = mapUpdate.Events;
        snapshot.ModelMetrics = CloneModelMetrics(metrics);
        string normalizedSelectedKey = NormalizeSourceKey(selectedModelKey);
        if (normalizedSelectedKey.Length == 0 || !ContainsModel(metrics, normalizedSelectedKey))
        {
            normalizedSelectedKey = PickDefaultModel(metrics, entries);
        }

        ClaudeRadarModelMetric selectedMetric = FindMetric(metrics, normalizedSelectedKey);
        snapshot.SelectedModelKey = normalizedSelectedKey;
        snapshot.SelectedModelName = selectedMetric == null ? string.Empty : selectedMetric.Name;
        snapshot.SelectedModel = selectedMetric == null ? ClaudeRadarModelMetric.CreateDefault() : selectedMetric.Clone();
        snapshot.Community = ParseCommunitySnapshot(ratingsRoot, FindMapEntry(entries, normalizedSelectedKey));
        snapshot.Quota = ParseQuotaSnapshot(dataRoot);
        snapshot.QuotaLine = BuildQuotaLineSnapshot(dataRoot, localQuotaFallbackEnabled, true);
        snapshot.SiteUpdatedAtText = ReadString(dataRoot, "updated_at");
        DateTime updatedUtc;
        if (TryParseDate(snapshot.SiteUpdatedAtText, out updatedUtc))
        {
            snapshot.SiteUpdatedAtUtc = updatedUtc;
            snapshot.SiteUpdatedAtKnown = true;
        }

        snapshot.Known = selectedMetric != null && selectedMetric.Known;
        snapshot.DataState = snapshot.Known ? ClaudeRadarServiceState.Normal : ClaudeRadarServiceState.Incomplete;
        if (!snapshot.Known)
        {
            snapshot.ErrorCode = "NO_MODEL";
            snapshot.ErrorMessage = "模型数据缺失";
        }

        TrySaveCache(snapshot);
        ClaudeRadarQuotaSnapshot personalQuota;
        if (TryReadClaudeCodeQuotaCache(out personalQuota))
        {
            snapshot.Quota = personalQuota;
            snapshot.ClaudeCodeState = ClaudeRadarServiceState.Normal;
        }
        else
        {
            snapshot.ClaudeCodeState = ClaudeRadarServiceState.Unknown;
        }

        return snapshot;
    }

    private static void ApplyJsonDisabled(ClaudeRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        snapshot.DataState = ClaudeRadarServiceState.Unavailable;
        snapshot.ErrorCode = "DISABLED";
        snapshot.ErrorMessage = "JSON关闭";
    }

    private static void ApplyDataFetchFailure(
        ClaudeRadarSnapshot snapshot,
        string errorCode,
        string errorMessage,
        bool offline)
    {
        if (snapshot == null)
        {
            return;
        }

        snapshot.DataState = offline ? ClaudeRadarServiceState.Offline : ClaudeRadarServiceState.Unreachable;
        snapshot.ErrorCode = errorCode ?? string.Empty;
        snapshot.ErrorMessage = errorMessage ?? string.Empty;
    }

    private static void ApplyUnavailableDataRoot(
        ClaudeRadarSnapshot snapshot,
        Dictionary<string, object> dataRoot)
    {
        if (snapshot == null)
        {
            return;
        }

        snapshot.DataState = ClaudeRadarServiceState.Unavailable;
        snapshot.ErrorCode = EmptyFallback(ReadString(dataRoot, "error"), "UNAVAILABLE");
        snapshot.ErrorMessage = EmptyFallback(ReadString(dataRoot, "message"), "服务不可用");
    }

    public static ClaudeRadarSnapshot BuildRandomTestSnapshot(int seed)
    {
        Random random = seed == 0 ? new Random() : new Random(seed);
        ClaudeRadarSnapshot snapshot = ClaudeRadarSnapshot.CreateDefault();
        snapshot.Known = true;
        snapshot.TestMode = true;
        snapshot.CheckedAtUtc = DateTime.UtcNow;
        snapshot.CheckedAtLocal = DateTime.Now;
        snapshot.DataState = ClaudeRadarServiceState.Normal;
        snapshot.RatingsState = ClaudeRadarServiceState.Normal;
        snapshot.ClaudeStatusState = ClaudeRadarServiceState.Normal;
        snapshot.ClaudeCodeState = ClaudeRadarServiceState.Normal;
        snapshot.SelectedModelKey = "test";
        snapshot.SelectedModelName = "Claude Test";
        snapshot.SelectedModel = new ClaudeRadarModelMetric
        {
            Known = true,
            SourceKey = "test",
            Name = "Claude Test",
            IqScore = random.Next(45, 150),
            Passed = random.Next(2, 10),
            ValidTasks = 10,
            TokenEfficiencyPercent = random.Next(45, 180),
            TimeEfficiencyPercent = random.Next(45, 180),
            TotalTokens = random.Next(20000000, 160000000),
            CostUsd = random.Next(15, 140),
            Hours = Math.Round(1.0 + random.NextDouble() * 7.0, 2),
            LatestLabel = DateTime.Now.ToString("M/d HH:mm", CultureInfo.CurrentCulture),
            LatestAtUtc = DateTime.UtcNow,
            LatestAtKnown = true,
            NormalLow = 90,
            NormalHigh = 110
        };
        ApplyDerivedLabels(snapshot.SelectedModel);
        snapshot.ModelMetrics.Add(snapshot.SelectedModel.Clone());
        snapshot.Quota = new ClaudeRadarQuotaSnapshot
        {
            Known = true,
            FiveHourPercent = random.Next(5, 98),
            WeeklyPercent = random.Next(5, 98),
            FiveHourResetText = DateTime.Now.AddHours(2).ToString("HH:mm", CultureInfo.CurrentCulture),
            WeeklyResetText = DateTime.Now.AddDays(2).ToString("MM/dd", CultureInfo.CurrentCulture),
            UpdatedAtUtc = DateTime.UtcNow,
            UpdatedAtKnown = true
        };
        snapshot.QuotaLine = new ClaudeRadarQuotaLineSnapshot
        {
            Known = true,
            CurrentValue = random.Next(1200, 2600),
            PreviousKnown = true,
            PreviousValue = random.Next(1200, 2600),
            MinValue = 1100,
            MaxValue = 2700,
            AverageValue = 1900,
            AverageKnown = true,
            Metric = "base_d7",
            SourceMode = "test"
        };
        snapshot.Community = new ClaudeRadarCommunitySnapshot
        {
            Known = true,
            RatingKey = "test",
            Label = "体感",
            Average = Math.Round(2.0 + random.NextDouble() * 7.0, 1),
            Count = random.Next(1, 20),
            UpdatedAtUtc = DateTime.UtcNow,
            RefreshSeconds = 300
        };
        snapshot.Models.Add(new ClaudeRadarModelEntry
        {
            SourceKey = "test",
            DisplayName = "Claude Test",
            RatingKey = "test",
            Enabled = true,
            Status = "active",
            SortOrder = 0,
            LastSeenUtc = DateTime.UtcNow,
            Color = System.Drawing.Color.FromArgb(255, 244, 128, 66)
        });
        return snapshot;
    }

    public static ClaudeRadarSnapshot LoadCache(string selectedModelKey)
    {
        if (!File.Exists(CachePath))
        {
            ClaudeRadarQuotaSnapshot quotaOnly;
            if (TryReadClaudeCodeQuotaCache(out quotaOnly))
            {
                ClaudeRadarSnapshot quotaSnapshot = ClaudeRadarSnapshot.CreateDefault();
                quotaSnapshot.Quota = quotaOnly;
                quotaSnapshot.ClaudeCodeState = ClaudeRadarServiceState.Normal;
                return quotaSnapshot;
            }

            return null;
        }

        try
        {
            Dictionary<string, string> values = ReadIniValues(CachePath);
            ClaudeRadarSnapshot snapshot = ClaudeRadarSnapshot.CreateDefault();
            snapshot.CheckedAtUtc = ReadDateValue(values, "CheckedAtUtc");
            snapshot.CheckedAtLocal = snapshot.CheckedAtUtc == DateTime.MinValue
                ? DateTime.MinValue
                : snapshot.CheckedAtUtc.ToLocalTime();
            snapshot.SelectedModelKey = ReadIniValue(values, "SelectedModelKey");
            if (!string.IsNullOrWhiteSpace(selectedModelKey) &&
                !string.Equals(snapshot.SelectedModelKey, NormalizeSourceKey(selectedModelKey), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            snapshot.SelectedModelName = ReadIniValue(values, "SelectedModelName");
            snapshot.Known = bool.TrueString.Equals(ReadIniValue(values, "Known"), StringComparison.OrdinalIgnoreCase);
            snapshot.DataState = snapshot.Known ? ClaudeRadarServiceState.Normal : ClaudeRadarServiceState.Unknown;
            snapshot.SelectedModel = ClaudeRadarModelMetric.CreateDefault();
            snapshot.SelectedModel.Known = snapshot.Known;
            snapshot.SelectedModel.SourceKey = snapshot.SelectedModelKey;
            snapshot.SelectedModel.Name = snapshot.SelectedModelName;
            snapshot.SelectedModel.IqScore = ReadIntValue(values, "IqScore", 0);
            snapshot.SelectedModel.Passed = ReadIntValue(values, "Passed", 0);
            snapshot.SelectedModel.ValidTasks = ReadIntValue(values, "ValidTasks", 10);
            snapshot.SelectedModel.TokenEfficiencyPercent = ReadIntValue(values, "TokenEfficiencyPercent", 100);
            snapshot.SelectedModel.TimeEfficiencyPercent = ReadIntValue(values, "TimeEfficiencyPercent", 100);
            snapshot.SelectedModel.LatestLabel = ReadIniValue(values, "LatestLabel");
            snapshot.SelectedModel.LatestAtUtc = ReadDateValue(values, "LatestAtUtc");
            snapshot.SelectedModel.LatestAtKnown = snapshot.SelectedModel.LatestAtUtc != DateTime.MinValue;
            snapshot.SelectedModel.NormalLow = ReadIntValue(values, "NormalLow", 90);
            snapshot.SelectedModel.NormalHigh = ReadIntValue(values, "NormalHigh", 110);
            ApplyDerivedLabels(snapshot.SelectedModel);
            if (snapshot.SelectedModel.Known)
            {
                snapshot.ModelMetrics.Add(snapshot.SelectedModel.Clone());
            }
            snapshot.Community.Known = bool.TrueString.Equals(ReadIniValue(values, "CommunityKnown"), StringComparison.OrdinalIgnoreCase);
            snapshot.Community.RatingKey = ReadIniValue(values, "CommunityRatingKey");
            snapshot.Community.Label = ReadIniValue(values, "CommunityLabel");
            snapshot.Community.Average = ReadDoubleValue(values, "CommunityAverage", 0.0);
            snapshot.Community.Count = ReadIntValue(values, "CommunityCount", 0);
            snapshot.Community.UpdatedAtUtc = ReadDateValue(values, "CommunityUpdatedAtUtc");
            snapshot.Community.RefreshSeconds = Math.Max(60, ReadIntValue(values, "CommunityRefreshSeconds", 900));
            if (string.Equals(ReadIniValue(values, "QuotaSource"), "site", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.Quota.FiveHourPercent = ReadIntValue(values, "FiveHourPercent", 0);
                snapshot.Quota.WeeklyPercent = ReadIntValue(values, "WeeklyPercent", 0);
                snapshot.Quota.FiveHourResetText = ReadIniValue(values, "FiveHourResetText");
                snapshot.Quota.WeeklyResetText = ReadIniValue(values, "WeeklyResetText");
                snapshot.Quota.Known = snapshot.Quota.FiveHourPercent > 0 || snapshot.Quota.WeeklyPercent > 0;
            }

            snapshot.QuotaLine = ReadCachedQuotaLine(values);
            if (snapshot.QuotaLine == null || !snapshot.QuotaLine.Known)
            {
                List<double> localLineValues = ReadQuotaHistoryValues("base_d7", DateTime.UtcNow.AddDays(-7.0));
                snapshot.QuotaLine = localLineValues.Count > 0
                    ? BuildQuotaLineFromValues(localLineValues, "base_d7", "local7d_cache")
                    : ClaudeRadarQuotaLineSnapshot.CreateDefault();
            }

            ClaudeRadarQuotaSnapshot personalQuota;
            if (TryReadClaudeCodeQuotaCache(out personalQuota))
            {
                snapshot.Quota = personalQuota;
                snapshot.ClaudeCodeState = ClaudeRadarServiceState.Normal;
            }

            return snapshot;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return null;
        }
    }

    public static List<ClaudeRadarModelEntry> LoadModelMap()
    {
        lock (MapLock)
        {
            return LoadModelMapUnlocked();
        }
    }

    public static void SaveModelMap(List<ClaudeRadarModelEntry> entries)
    {
        lock (MapLock)
        {
            Dictionary<string, bool> keys = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<ClaudeRadarModelEntry> normalized = new List<ClaudeRadarModelEntry>();
            for (int i = 0; entries != null && i < entries.Count; i++)
            {
                ClaudeRadarModelEntry entry = entries[i];
                if (entry == null)
                {
                    continue;
                }

                string sourceKey = NormalizeSourceKey(entry.SourceKey);
                if (sourceKey.Length == 0)
                {
                    throw new InvalidOperationException("Claude Radar 模型映射缺少 source_key。");
                }

                if (keys.ContainsKey(sourceKey))
                {
                    throw new InvalidOperationException("Claude Radar 模型映射重复 source_key: " + sourceKey);
                }

                keys[sourceKey] = true;
                normalized.Add(new ClaudeRadarModelEntry
                {
                    SourceKey = sourceKey,
                    DisplayName = EmptyFallback(entry.DisplayName, sourceKey),
                    RatingKey = (entry.RatingKey ?? string.Empty).Trim(),
                    SortOrder = entry.SortOrder,
                    Enabled = entry.Enabled,
                    HistoricalOnly = entry.HistoricalOnly,
                    Status = NormalizePersistedModelStatus(entry),
                    LastSeenUtc = entry.LastSeenUtc,
                    MissingSuccessCount = Math.Max(0, entry.MissingSuccessCount),
                    Color = entry.Color.IsEmpty ? GenerateModelColor(sourceKey) : entry.Color
                });
            }

            SaveModelMapUnlocked(normalized);
        }
    }

    internal static void RunSelfTest()
    {
        string sample =
            "{\"ok\":true,\"updated_at\":\"2026-07-04T12:40:00+09:00\",\"quota\":{\"updated_at\":\"2026-07-04T09:46:15+09:00\",\"base_d7\":2270.63,\"base_d7_trend\":[2200.0,2270.63],\"chart\":{\"key\":\"d7\",\"trend\":[2100.0,2200.0,2270.63]},\"cal\":{\"run_id\":\"sample\"},\"usage\":[{\"key\":\"h5\",\"used_pct\":41,\"reset_text_zh\":\"13:00 重置\"},{\"key\":\"d7\",\"used_pct\":60,\"reset_text_zh\":\"7月4日 16:00 重置\"}]},\"iq\":{\"models\":[{\"key\":\"m1\",\"name\":\"Opus 4.8 high\",\"score\":60,\"pass\":[null,4],\"valid\":[null,10],\"cost\":[null,31.08],\"time\":[null,1.8],\"latest_label\":\"7月4日 09:46\"}],\"table\":{\"rows\":[{\"name\":\"总tokens\",\"nums\":[27727109]}]}}}";
        Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(sample) as Dictionary<string, object>;
        List<ClaudeRadarModelMetric> metrics = ParseModelMetrics(root);
        if (metrics.Count != 1 || metrics[0].IqScore != 60 || metrics[0].Passed != 4)
        {
            throw new InvalidOperationException("Claude Radar model parser self-test failed.");
        }

        ClaudeRadarQuotaSnapshot quota = ParseQuotaSnapshot(root);
        if (!quota.Known || quota.FiveHourPercent != 59 || quota.WeeklyPercent != 40)
        {
            throw new InvalidOperationException("Claude Radar quota parser self-test failed.");
        }

        ClaudeRadarQuotaLineSnapshot line = BuildQuotaLineSnapshot(root, false, false);
        if (!line.Known || !line.PreviousKnown || Math.Abs(line.CurrentValue - 2270.63) > 0.01 ||
            !string.Equals(line.SourceMode, "site_chart", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Radar quota line parser self-test failed.");
        }

        string quotaMetricsSample =
            "{\"quota\":{\"updated_at\":\"2026-07-06T10:59:00+08:00\",\"metrics\":[{\"key\":\"h5\",\"value\":343.06},{\"key\":\"d7\",\"value\":2470}],\"chart\":{\"key\":\"total_7d\",\"trend\":[2270.63,2470]},\"cal\":{\"run_id\":\"site-shape\"}}}";
        Dictionary<string, object> quotaMetricsRoot = new JavaScriptSerializer().DeserializeObject(quotaMetricsSample) as Dictionary<string, object>;
        ClaudeRadarQuotaLineSnapshot metricsLine = BuildQuotaLineSnapshot(quotaMetricsRoot, false, false);
        if (!metricsLine.Known || !metricsLine.PreviousKnown ||
            Math.Abs(metricsLine.CurrentValue - 2470.0) > 0.01 ||
            !string.Equals(metricsLine.SourceMode, "site_chart", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Radar quota metrics parser self-test failed.");
        }

        Dictionary<string, object> ratingsRoot = new JavaScriptSerializer().DeserializeObject(
            "{\"models\":[" +
            "{\"id\":\"opus48_high\",\"label\":\"Opus 4.8 high\",\"average\":93,\"count\":5}," +
            "{\"id\":\"fable5_max\",\"label\":\"Fable 5 max\",\"average\":94,\"count\":4}," +
            "{\"id\":\"fable5_high\",\"label\":\"Fable 5 high\",\"average\":94,\"count\":8}]}") as Dictionary<string, object>;
        HashSet<string> ratingKeys = ReadRatingKeys(ratingsRoot);
        ClaudeRadarCommunitySnapshot topCommunity = ParseCommunitySnapshot(ratingsRoot, null);
        if (!topCommunity.Known ||
            !string.Equals(topCommunity.RatingKey, "fable5_high", StringComparison.OrdinalIgnoreCase) ||
            Math.Abs(topCommunity.Average - 94.0) > 0.0001 ||
            topCommunity.Count != 8)
        {
            throw new InvalidOperationException("Claude Radar community rating self-test failed: highest community model was not selected.");
        }

        List<ClaudeRadarModelEntry> entries = new List<ClaudeRadarModelEntry>();
        List<ClaudeRadarModelCatalogEvent> events = new List<ClaudeRadarModelCatalogEvent>();
        ApplyModelMapUpdate(entries, metrics, ratingKeys, true, new DateTime(2026, 7, 4, 0, 0, 0, DateTimeKind.Utc), events, true);
        if (entries.Count != 1 ||
            !string.IsNullOrEmpty(entries[0].RatingKey) ||
            !string.Equals(entries[0].Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Radar model map self-test failed: new model auto-merged rating key.");
        }

        entries[0].RatingKey = "opus48_high";
        entries[0].Enabled = true;
        ApplyModelMapUpdate(entries, metrics, ratingKeys, true, new DateTime(2026, 7, 4, 1, 0, 0, DateTimeKind.Utc), events, true);
        if (!string.Equals(entries[0].RatingKey, "opus48_high", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(entries[0].Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Radar model map self-test failed: explicit rating key did not activate.");
        }

        entries[0].RatingKey = string.Empty;
        entries[0].Status = "active";
        ApplyModelMapUpdate(entries, metrics, ratingKeys, true, new DateTime(2026, 7, 4, 2, 0, 0, DateTimeKind.Utc), events, true);
        if (!string.Equals(entries[0].Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Radar model map self-test failed: existing empty rating key stayed active.");
        }

        entries[0].RatingKey = "missing_semantic_key";
        entries[0].Status = "active";
        ApplyModelMapUpdate(entries, metrics, ratingKeys, true, new DateTime(2026, 7, 4, 3, 0, 0, DateTimeKind.Utc), events, true);
        if (!string.Equals(entries[0].Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Radar model map self-test failed: invalid explicit rating key stayed active.");
        }

        List<ClaudeRadarModelMetric> duplicateNameMetrics = new List<ClaudeRadarModelMetric>();
        duplicateNameMetrics.Add(metrics[0]);
        ClaudeRadarModelMetric duplicateName = metrics[0].Clone();
        duplicateName.SourceKey = "m2";
        duplicateNameMetrics.Add(duplicateName);
        entries = new List<ClaudeRadarModelEntry>();
        ApplyModelMapUpdate(entries, duplicateNameMetrics, ratingKeys, true, new DateTime(2026, 7, 4, 4, 0, 0, DateTimeKind.Utc), events, true);
        if (entries.Count != 2 ||
            !string.IsNullOrEmpty(entries[0].RatingKey) ||
            !string.IsNullOrEmpty(entries[1].RatingKey) ||
            string.Equals(entries[0].SourceKey, entries[1].SourceKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Radar model map self-test failed: same-name source keys were merged.");
        }

        if (!string.Equals(NormalizePersistedModelStatus(new ClaudeRadarModelEntry { Enabled = true, RatingKey = string.Empty }), "pending", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(NormalizePersistedModelStatus(new ClaudeRadarModelEntry { Enabled = true, RatingKey = string.Empty, Status = "active" }), "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Radar model map self-test failed: empty persisted status defaulted active.");
        }

        ClaudeRadarHomepageMetadata metadata = ParseHomepageMetadata(
            "<script>const MODEL_NAMES = { m1:\"Opus 4.8 high\", m2:\"Sonnet 5 max\" };</script>");
        List<ClaudeRadarModelMetric> homepageMetrics = BuildHomepageModelMetrics(metadata);
        if (homepageMetrics.Count != 2 ||
            homepageMetrics[0].Known ||
            !string.Equals(homepageMetrics[0].SourceKey, "m1", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(homepageMetrics[0].Name, "Opus 4.8 high", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Radar homepage metadata self-test failed.");
        }

        entries = new List<ClaudeRadarModelEntry>();
        entries.Add(new ClaudeRadarModelEntry
        {
            SourceKey = "old",
            DisplayName = "Old model",
            RatingKey = "old_rating",
            Enabled = true,
            Status = "active",
            MissingSuccessCount = 0
        });
        events.Clear();
        ApplyModelMapUpdate(entries, homepageMetrics, ratingKeys, true, new DateTime(2026, 7, 4, 5, 0, 0, DateTimeKind.Utc), events, false);
        ClaudeRadarModelEntry oldEntry = FindMapEntry(entries, "old");
        if (oldEntry == null ||
            oldEntry.MissingSuccessCount != 0 ||
            string.Equals(oldEntry.Status, "temporarily_missing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(oldEntry.Status, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Radar weak homepage catalog self-test deleted an existing model.");
        }

        Dictionary<string, object> statusRoot = new JavaScriptSerializer().DeserializeObject(
            "{\"status\":{\"indicator\":\"none\"},\"components\":[{\"name\":\"Claude API (api.anthropic.com)\",\"status\":\"operational\"},{\"name\":\"Claude Code\",\"status\":\"operational\"}]}") as Dictionary<string, object>;
        if (ParseClaudePublicStatusState(statusRoot) != ClaudeRadarServiceState.Normal)
        {
            throw new InvalidOperationException("Claude public status self-test failed: operational summary not normal.");
        }

        statusRoot = new JavaScriptSerializer().DeserializeObject(
            "{\"status\":{\"indicator\":\"minor\"},\"components\":[{\"name\":\"Claude API (api.anthropic.com)\",\"status\":\"degraded_performance\"}]}") as Dictionary<string, object>;
        if (ParseClaudePublicStatusState(statusRoot) != ClaudeRadarServiceState.Unavailable)
        {
            throw new InvalidOperationException("Claude public status self-test failed: degraded summary not unavailable.");
        }

        statusRoot = new JavaScriptSerializer().DeserializeObject("{\"components\":[]}") as Dictionary<string, object>;
        if (ParseClaudePublicStatusState(statusRoot) != ClaudeRadarServiceState.Incomplete)
        {
            throw new InvalidOperationException("Claude public status self-test failed: schema gap not incomplete.");
        }

        Dictionary<string, object> partialRoot = new JavaScriptSerializer().DeserializeObject(
            "{\"ok\":true,\"iq\":{\"models\":[{\"key\":\"m1\",\"name\":\"Opus 4.8 high\",\"score\":90},{\"name\":\"missing key\",\"score\":90}]}}") as Dictionary<string, object>;
        List<ClaudeRadarModelMetric> partialMetrics = ParseModelMetrics(partialRoot);
        if (partialMetrics.Count != 1 || IsCompleteModelCatalog(partialRoot, partialMetrics))
        {
            throw new InvalidOperationException("Claude Radar partial model catalog self-test failed: partial catalog marked complete.");
        }

        entries = new List<ClaudeRadarModelEntry>();
        entries.Add(new ClaudeRadarModelEntry
        {
            SourceKey = "m2",
            DisplayName = "Existing model",
            RatingKey = "existing_rating",
            Enabled = true,
            Status = "active",
            MissingSuccessCount = 0
        });
        ApplyModelMapUpdate(entries, partialMetrics, ratingKeys, true, new DateTime(2026, 7, 4, 6, 0, 0, DateTimeKind.Utc), events, IsCompleteModelCatalog(partialRoot, partialMetrics));
        ClaudeRadarModelEntry existingEntry = FindMapEntry(entries, "m2");
        if (existingEntry == null ||
            existingEntry.MissingSuccessCount != 0 ||
            string.Equals(existingEntry.Status, "temporarily_missing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(existingEntry.Status, "deleted", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Radar partial catalog self-test deleted an existing model.");
        }

        RunStorageIsolationSelfTest();
        RunDataFailureFixtureSelfTest();

        string historyPath = Path.Combine(Path.GetTempPath(), "claude-radar-history-selftest-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            AppendQuotaHistoryIfNew(historyPath, "base_d7", 100.0, "2026-07-04T00:00:00Z", "same");
            AppendQuotaHistoryIfNew(historyPath, "base_d7", 100.0, "2026-07-04T00:00:00Z", "same");
            string[] historyLines = File.ReadAllLines(historyPath, Encoding.UTF8);
            if (historyLines.Length != 1)
            {
                throw new InvalidOperationException("Claude Radar quota history self-test failed: duplicate signature was appended.");
            }

            File.AppendAllText(
                historyPath,
                "{bad json" + Environment.NewLine +
                "{\"schema_version\":1,\"timestamp_utc\":\"2026-07-05T00:00:00Z\",\"metric\":\"base_d7\",\"value\":125.0,\"signature\":\"good2\"}" + Environment.NewLine,
                new UTF8Encoding(false));
            List<double> values = ReadQuotaHistoryValues(historyPath, "base_d7", new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc));
            if (values.Count != 2 || Math.Abs(values[0] - 100.0) > 0.01 || Math.Abs(values[1] - 125.0) > 0.01)
            {
                throw new InvalidOperationException("Claude Radar quota history self-test failed: bad line blocked later good rows.");
            }

            File.AppendAllText(
                historyPath,
                "{\"schema_version\":1,\"timestamp_utc\":\"2026-05-01T00:00:00Z\",\"metric\":\"base_d7\",\"value\":90.0,\"signature\":\"old\"}" + Environment.NewLine,
                new UTF8Encoding(false));
            TrimQuotaHistory(historyPath, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
            values = ReadQuotaHistoryValues(historyPath, "base_d7", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
            if (values.Count != 2)
            {
                throw new InvalidOperationException("Claude Radar quota history self-test failed: trim removed current rows or kept old rows.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(historyPath))
                {
                    File.Delete(historyPath);
                }
            }
            catch
            {
            }
        }
    }

    private static void RunStorageIsolationSelfTest()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "claude-radar-storage-selftest-" + Guid.NewGuid().ToString("N"));
        string previousOverride = storageDirectoryOverride;
        storageDirectoryOverride = tempDir;
        try
        {
            Directory.CreateDirectory(tempDir);
            string codexCachePath = Path.Combine(tempDir, "codex-radar-cache.ini");
            string codexQuotaPath = Path.Combine(tempDir, "quota.ini");
            File.WriteAllText(codexCachePath, "codex-cache-sentinel", new UTF8Encoding(false));
            File.WriteAllText(codexQuotaPath, "codex-quota-sentinel", new UTF8Encoding(false));

            if (LoadCache("selftest") != null)
            {
                throw new InvalidOperationException("Claude Radar storage self-test failed: read Codex cache as Claude cache.");
            }

            BuildRandomTestSnapshot(721);
            if (File.Exists(CachePath) ||
                File.Exists(ModelMapPath) ||
                File.Exists(QuotaHistoryPath) ||
                File.Exists(ClaudeCodeQuotaCachePath))
            {
                throw new InvalidOperationException("Claude Radar storage self-test failed: random test snapshot wrote storage.");
            }

            ClaudeRadarSnapshot snapshot = BuildRandomTestSnapshot(722);
            snapshot.TestMode = false;
            snapshot.SelectedModelKey = "selftest";
            snapshot.SelectedModelName = "Self Test";
            TrySaveCache(snapshot);
            SaveModelMap(new List<ClaudeRadarModelEntry>
            {
                new ClaudeRadarModelEntry
                {
                    SourceKey = "selftest",
                    DisplayName = "Self Test",
                    RatingKey = "selftest_rating",
                    Enabled = true,
                    Status = "active",
                    SortOrder = 1,
                    LastSeenUtc = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc)
                }
            });
            TryWriteClaudeCodeQuotaCache(new ClaudeCodeUsageSnapshot
            {
                FiveHourPercent = 51,
                WeeklyPercent = 62,
                SourceUpdatedUtc = new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc),
                SourceUpdatedKnown = true
            });
            AppendQuotaHistoryIfNew("base_d7", 1234.0, "2026-07-05T00:00:00Z", "storage-selftest");
            ClaudeRadarSnapshot loaded = LoadCache("selftest");
            if (loaded == null ||
                loaded.Community == null ||
                !loaded.Community.Known ||
                !string.Equals(loaded.Community.RatingKey, snapshot.Community.RatingKey, StringComparison.Ordinal) ||
                !string.Equals(loaded.Community.Label, snapshot.Community.Label, StringComparison.Ordinal) ||
                Math.Abs(loaded.Community.Average - snapshot.Community.Average) > 0.0001)
            {
                throw new InvalidOperationException("Claude Radar storage self-test failed: community metadata was not preserved in cache.");
            }

            if (loaded.QuotaLine == null ||
                !loaded.QuotaLine.Known ||
                !loaded.QuotaLine.PreviousKnown ||
                Math.Abs(loaded.QuotaLine.CurrentValue - snapshot.QuotaLine.CurrentValue) > 0.0001 ||
                Math.Abs(loaded.QuotaLine.PreviousValue - snapshot.QuotaLine.PreviousValue) > 0.0001)
            {
                throw new InvalidOperationException("Claude Radar storage self-test failed: quota line was not preserved in cache.");
            }

            if (!File.Exists(CachePath) ||
                !File.Exists(ModelMapPath) ||
                !File.Exists(QuotaHistoryPath) ||
                !File.Exists(ClaudeCodeQuotaCachePath))
            {
                throw new InvalidOperationException("Claude Radar storage self-test failed: expected Claude storage files were not written.");
            }

            string codexCache = File.ReadAllText(codexCachePath, Encoding.UTF8);
            string codexQuota = File.ReadAllText(codexQuotaPath, Encoding.UTF8);
            if (!string.Equals(codexCache, "codex-cache-sentinel", StringComparison.Ordinal) ||
                !string.Equals(codexQuota, "codex-quota-sentinel", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Claude Radar storage self-test failed: Codex sentinel files were modified.");
            }
        }
        finally
        {
            storageDirectoryOverride = previousOverride;
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
            }
        }
    }

    private static void RunDataFailureFixtureSelfTest()
    {
        ClaudeRadarSnapshot disabled = ClaudeRadarSnapshot.CreateDefault();
        ApplyJsonDisabled(disabled);
        if (disabled.DataState != ClaudeRadarServiceState.Unavailable ||
            !string.Equals(disabled.ErrorCode, "DISABLED", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Radar failure fixture self-test failed: disabled JSON state.");
        }

        ClaudeRadarSnapshot httpFailure = ClaudeRadarSnapshot.CreateDefault();
        ApplyDataFetchFailure(httpFailure, "500", "HTTP 500", false);
        if (httpFailure.DataState != ClaudeRadarServiceState.Unreachable ||
            !string.Equals(httpFailure.ErrorCode, "500", StringComparison.Ordinal) ||
            !string.Equals(httpFailure.ErrorMessage, "HTTP 500", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Radar failure fixture self-test failed: HTTP failure state.");
        }

        ClaudeRadarSnapshot timeout = ClaudeRadarSnapshot.CreateDefault();
        ApplyDataFetchFailure(timeout, "TIMEOUT", "请求超时", false);
        if (timeout.DataState != ClaudeRadarServiceState.Unreachable ||
            !string.Equals(timeout.ErrorCode, "TIMEOUT", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Radar failure fixture self-test failed: timeout state.");
        }

        ClaudeRadarSnapshot offline = ClaudeRadarSnapshot.CreateDefault();
        ApplyDataFetchFailure(offline, "OFFLINE", "无网络", true);
        if (offline.DataState != ClaudeRadarServiceState.Offline)
        {
            throw new InvalidOperationException("Claude Radar failure fixture self-test failed: offline state.");
        }

        Dictionary<string, object> unavailableRoot = new JavaScriptSerializer().DeserializeObject(
            "{\"ok\":false,\"error\":\"UNSUPPORTED_REGION\",\"message\":\"region not supported\"}") as Dictionary<string, object>;
        ClaudeRadarSnapshot unavailable = ClaudeRadarSnapshot.CreateDefault();
        ApplyUnavailableDataRoot(unavailable, unavailableRoot);
        if (unavailable.DataState != ClaudeRadarServiceState.Unavailable ||
            !string.Equals(unavailable.ErrorCode, "UNSUPPORTED_REGION", StringComparison.Ordinal) ||
            unavailable.Models.Count != 0 ||
            unavailable.ModelCatalogEvents.Count != 0)
        {
            throw new InvalidOperationException("Claude Radar failure fixture self-test failed: unsupported/unavailable state.");
        }
    }

    private sealed class ClaudeRadarModelMapUpdate
    {
        public List<ClaudeRadarModelEntry> Entries { get; set; }
        public List<ClaudeRadarModelCatalogEvent> Events { get; set; }

        public static ClaudeRadarModelMapUpdate CreateEmpty()
        {
            return new ClaudeRadarModelMapUpdate
            {
                Entries = new List<ClaudeRadarModelEntry>(),
                Events = new List<ClaudeRadarModelCatalogEvent>()
            };
        }
    }

    private sealed class ClaudeRadarHomepageModel
    {
        public string SourceKey { get; set; }
        public string DisplayName { get; set; }
    }

    private sealed class ClaudeRadarHomepageMetadata
    {
        public List<ClaudeRadarHomepageModel> ModelNames { get; set; }

        public static ClaudeRadarHomepageMetadata CreateEmpty()
        {
            return new ClaudeRadarHomepageMetadata
            {
                ModelNames = new List<ClaudeRadarHomepageModel>()
            };
        }
    }

    private static bool TryFetchJson(
        string url,
        out Dictionary<string, object> root,
        out string errorCode,
        out string errorMessage)
    {
        root = null;
        errorCode = string.Empty;
        errorMessage = string.Empty;
        if (!IsNetworkAvailable())
        {
            errorCode = "OFFLINE";
            errorMessage = "无网络";
            return false;
        }

        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url + (url.IndexOf('?') >= 0 ? "&" : "?") + "t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            request.Method = "GET";
            request.Accept = "application/json,text/plain,*/*";
            request.UserAgent = ProductIdentity.UserAgent + "/" + ProductIdentity.Version;
            request.Timeout = RequestTimeoutMs;
            request.ReadWriteTimeout = RequestTimeoutMs;
            request.Headers["Cache-Control"] = "no-store, no-cache";
            request.Headers["Pragma"] = "no-cache";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string content = reader.ReadToEnd();
                    root = new JavaScriptSerializer().DeserializeObject(content) as Dictionary<string, object>;
                    if (root == null)
                    {
                        errorCode = "PARSE";
                        errorMessage = "解析失败";
                        return false;
                    }

                    return true;
                }
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                errorCode = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
                errorMessage = "HTTP " + errorCode;
                response.Dispose();
            }
            else
            {
                errorCode = "NET";
                errorMessage = "无法连接";
            }

            return false;
        }
        catch (Exception ex)
        {
            errorCode = ex.GetType().Name;
            errorMessage = "请求失败";
            Program.LogException(ex);
            return false;
        }
    }

    private static ClaudeRadarServiceState ReadClaudePublicStatusState()
    {
        Dictionary<string, object> root;
        string errorCode;
        string errorMessage;
        if (!TryFetchJson(ClaudeStatusUrl, out root, out errorCode, out errorMessage))
        {
            return IsOffline() ? ClaudeRadarServiceState.Offline : ClaudeRadarServiceState.Unreachable;
        }

        return ParseClaudePublicStatusState(root);
    }

    private static ClaudeRadarServiceState ParseClaudePublicStatusState(Dictionary<string, object> root)
    {
        if (root == null)
        {
            return ClaudeRadarServiceState.Incomplete;
        }

        bool sawStatus = false;
        Dictionary<string, object> status = ReadObject(root, "status");
        string indicator = ReadString(status, "indicator").Trim().ToLowerInvariant();
        if (indicator.Length > 0)
        {
            sawStatus = true;
            if (!string.Equals(indicator, "none", StringComparison.OrdinalIgnoreCase))
            {
                return ClaudeRadarServiceState.Unavailable;
            }
        }

        object componentsObject;
        object[] components = null;
        if (root.TryGetValue("components", out componentsObject))
        {
            components = componentsObject as object[];
        }

        if (components != null)
        {
            for (int i = 0; i < components.Length; i++)
            {
                Dictionary<string, object> component = components[i] as Dictionary<string, object>;
                if (component == null)
                {
                    continue;
                }

                string name = ReadString(component, "name");
                if (!IsClaudePublicStatusComponent(name))
                {
                    continue;
                }

                sawStatus = true;
                string componentStatus = ReadString(component, "status").Trim().ToLowerInvariant();
                if (componentStatus.Length == 0)
                {
                    return ClaudeRadarServiceState.Incomplete;
                }

                if (!string.Equals(componentStatus, "operational", StringComparison.OrdinalIgnoreCase))
                {
                    return ClaudeRadarServiceState.Unavailable;
                }
            }
        }

        return sawStatus ? ClaudeRadarServiceState.Normal : ClaudeRadarServiceState.Incomplete;
    }

    private static bool IsClaudePublicStatusComponent(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        string value = name.Trim().ToLowerInvariant();
        return value.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("anthropic", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TryFetchHomepageMetadata(out ClaudeRadarHomepageMetadata metadata)
    {
        metadata = ClaudeRadarHomepageMetadata.CreateEmpty();
        if (!IsNetworkAvailable())
        {
            return false;
        }

        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(HomeUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            request.Method = "GET";
            request.Accept = "text/html,application/xhtml+xml,*/*";
            request.UserAgent = ProductIdentity.UserAgent + "/" + ProductIdentity.Version;
            request.Timeout = RequestTimeoutMs;
            request.ReadWriteTimeout = RequestTimeoutMs;
            request.Headers["Cache-Control"] = "no-store, no-cache";
            request.Headers["Pragma"] = "no-cache";
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                metadata = ParseHomepageMetadata(reader.ReadToEnd());
                return metadata.ModelNames.Count > 0;
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
    }

    private static ClaudeRadarHomepageMetadata ParseHomepageMetadata(string html)
    {
        ClaudeRadarHomepageMetadata metadata = ClaudeRadarHomepageMetadata.CreateEmpty();
        if (string.IsNullOrWhiteSpace(html))
        {
            return metadata;
        }

        Match match = Regex.Match(
            html,
            @"MODEL_NAMES\s*=\s*\{(?<body>.*?)\}",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return metadata;
        }

        string body = match.Groups["body"].Value;
        MatchCollection pairs = Regex.Matches(
            body,
            @"[""']?(?<key>[A-Za-z0-9_\-]+)[""']?\s*:\s*[""'](?<name>(?:\\.|[^""'])*)[""']",
            RegexOptions.CultureInvariant);
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pairs.Count; i++)
        {
            string sourceKey = NormalizeSourceKey(pairs[i].Groups["key"].Value);
            string displayName = DecodeJavaScriptString(pairs[i].Groups["name"].Value).Trim();
            if (sourceKey.Length == 0 || displayName.Length == 0 || seen.Contains(sourceKey))
            {
                continue;
            }

            seen.Add(sourceKey);
            metadata.ModelNames.Add(new ClaudeRadarHomepageModel
            {
                SourceKey = sourceKey,
                DisplayName = displayName
            });
        }

        return metadata;
    }

    private static string DecodeJavaScriptString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            return new JavaScriptSerializer().Deserialize<string>("\"" + value.Replace("\"", "\\\"") + "\"") ?? string.Empty;
        }
        catch
        {
            return value
                .Replace("\\\"", "\"")
                .Replace("\\'", "'")
                .Replace("\\\\", "\\");
        }
    }

    private static List<ClaudeRadarModelMetric> BuildHomepageModelMetrics(ClaudeRadarHomepageMetadata metadata)
    {
        List<ClaudeRadarModelMetric> metrics = new List<ClaudeRadarModelMetric>();
        if (metadata == null || metadata.ModelNames == null)
        {
            return metrics;
        }

        for (int i = 0; i < metadata.ModelNames.Count; i++)
        {
            ClaudeRadarHomepageModel model = metadata.ModelNames[i];
            if (model == null || string.IsNullOrWhiteSpace(model.SourceKey))
            {
                continue;
            }

            ClaudeRadarModelMetric metric = ClaudeRadarModelMetric.CreateDefault();
            metric.SourceKey = NormalizeSourceKey(model.SourceKey);
            metric.Name = EmptyFallback(model.DisplayName, metric.SourceKey);
            metric.Known = false;
            metric.HistoricalOnly = true;
            metrics.Add(metric);
        }

        return metrics;
    }

    private static bool NeedsHomepageMetadata(List<ClaudeRadarModelMetric> metrics)
    {
        if (metrics == null || metrics.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < metrics.Count; i++)
        {
            ClaudeRadarModelMetric metric = metrics[i];
            if (metric == null ||
                string.IsNullOrWhiteSpace(metric.Name) ||
                string.Equals(metric.Name, metric.SourceKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyHomepageModelNames(List<ClaudeRadarModelMetric> metrics, ClaudeRadarHomepageMetadata metadata)
    {
        if (metrics == null || metadata == null || metadata.ModelNames == null || metadata.ModelNames.Count == 0)
        {
            return;
        }

        Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < metadata.ModelNames.Count; i++)
        {
            ClaudeRadarHomepageModel model = metadata.ModelNames[i];
            if (model != null && !string.IsNullOrWhiteSpace(model.SourceKey) && !string.IsNullOrWhiteSpace(model.DisplayName))
            {
                names[NormalizeSourceKey(model.SourceKey)] = model.DisplayName.Trim();
            }
        }

        for (int i = 0; i < metrics.Count; i++)
        {
            ClaudeRadarModelMetric metric = metrics[i];
            if (metric == null || string.IsNullOrWhiteSpace(metric.SourceKey))
            {
                continue;
            }

            string name;
            if (names.TryGetValue(metric.SourceKey, out name) &&
                (string.IsNullOrWhiteSpace(metric.Name) ||
                 string.Equals(metric.Name, metric.SourceKey, StringComparison.OrdinalIgnoreCase)))
            {
                metric.Name = name;
            }
        }
    }

    private static List<ClaudeRadarModelMetric> ParseModelMetrics(Dictionary<string, object> root)
    {
        List<ClaudeRadarModelMetric> metrics = new List<ClaudeRadarModelMetric>();
        Dictionary<string, object> iq = ReadObject(root, "iq");
        object modelsObject;
        object[] models = null;
        if (iq != null && iq.TryGetValue("models", out modelsObject))
        {
            models = modelsObject as object[];
        }

        Dictionary<string, double> tokenTotals = ParseTableNumberRow(iq, "总tokens");
        if (models == null)
        {
            return metrics;
        }

        for (int i = 0; i < models.Length; i++)
        {
            Dictionary<string, object> model = models[i] as Dictionary<string, object>;
            if (model == null)
            {
                continue;
            }

            ClaudeRadarModelMetric metric = ClaudeRadarModelMetric.CreateDefault();
            metric.SourceKey = NormalizeSourceKey(ReadString(model, "key"));
            if (metric.SourceKey.Length == 0)
            {
                continue;
            }

            metric.Name = EmptyFallback(ReadString(model, "name"), metric.SourceKey);
            metric.IqScore = ClampScore(ReadIntOrDouble(model, "score", 0));
            metric.Passed = ReadLastArrayInt(model, "pass", 0);
            metric.ValidTasks = Math.Max(1, ReadLastArrayInt(model, "valid", 10));
            if (metric.IqScore <= 0 && metric.ValidTasks > 0)
            {
                metric.IqScore = ClampScore((int)Math.Round(metric.Passed * 150.0 / metric.ValidTasks));
            }

            metric.CostUsd = ReadLastArrayDouble(model, "cost", 0.0);
            metric.Hours = ReadLastArrayDouble(model, "time", 0.0);
            double tokenTotal;
            if (tokenTotals.TryGetValue(metric.SourceKey, out tokenTotal))
            {
                metric.TotalTokens = tokenTotal;
            }

            metric.LatestLabel = ReadString(model, "latest_label");
            DateTime latestUtc;
            if (TryParseDate(ReadString(model, "latest_at"), out latestUtc))
            {
                metric.LatestAtUtc = latestUtc;
                metric.LatestAtKnown = true;
            }

            metric.HistoricalOnly = ReadBool(model, "historical_only", false);
            metric.TokenEfficiencyPercent = CalculateEfficiency(metric.Passed, metric.TotalTokens, 6, 60000000.0, false);
            metric.TimeEfficiencyPercent = CalculateEfficiency(metric.Passed, metric.Hours * 3600.0, 6, 3.0 * 3600.0, false);
            metric.Known = true;
            ApplyDerivedLabels(metric);
            metrics.Add(metric);
        }

        return metrics;
    }

    private static bool IsCompleteModelCatalog(Dictionary<string, object> root, List<ClaudeRadarModelMetric> metrics)
    {
        if (root == null || !ReadBool(root, "ok", true) || metrics == null || metrics.Count == 0)
        {
            return false;
        }

        Dictionary<string, object> iq = ReadObject(root, "iq");
        object modelsObject;
        object[] models = null;
        if (iq == null || !iq.TryGetValue("models", out modelsObject))
        {
            return false;
        }

        models = modelsObject as object[];
        if (models == null || models.Length == 0 || models.Length != metrics.Count)
        {
            return false;
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < models.Length; i++)
        {
            Dictionary<string, object> model = models[i] as Dictionary<string, object>;
            string sourceKey = model == null ? string.Empty : NormalizeSourceKey(ReadString(model, "key"));
            if (sourceKey.Length == 0 || seen.Contains(sourceKey))
            {
                return false;
            }

            seen.Add(sourceKey);
        }

        for (int i = 0; i < metrics.Count; i++)
        {
            if (metrics[i] == null ||
                string.IsNullOrWhiteSpace(metrics[i].SourceKey) ||
                !seen.Contains(metrics[i].SourceKey))
            {
                return false;
            }
        }

        return true;
    }

    private static void ApplyDerivedLabels(ClaudeRadarModelMetric metric)
    {
        if (metric == null)
        {
            return;
        }

        if (metric.IqScore < metric.NormalLow)
        {
            metric.StatusText = "降智";
        }
        else if (metric.IqScore > metric.NormalHigh)
        {
            metric.StatusText = "增智";
        }
        else
        {
            metric.StatusText = "常态";
        }

        int combined = (metric.TokenEfficiencyPercent + metric.TimeEfficiencyPercent) / 2;
        if (combined < 80)
        {
            metric.EfficiencyText = "低效";
        }
        else if (combined > 110)
        {
            metric.EfficiencyText = "高效";
        }
        else
        {
            metric.EfficiencyText = "普通";
        }
    }

    private static ClaudeRadarQuotaSnapshot ParseQuotaSnapshot(Dictionary<string, object> root)
    {
        ClaudeRadarQuotaSnapshot quota = ClaudeRadarQuotaSnapshot.CreateDefault();
        Dictionary<string, object> quotaRoot = ReadObject(root, "quota");
        if (quotaRoot == null)
        {
            return quota;
        }

        string updated = ReadString(quotaRoot, "updated_at");
        DateTime updatedUtc;
        if (TryParseDate(updated, out updatedUtc))
        {
            quota.UpdatedAtUtc = updatedUtc;
            quota.UpdatedAtKnown = true;
        }

        object usageObject;
        object[] usage = null;
        if (quotaRoot.TryGetValue("usage", out usageObject))
        {
            usage = usageObject as object[];
        }

        if (usage != null)
        {
            for (int i = 0; i < usage.Length; i++)
            {
                Dictionary<string, object> row = usage[i] as Dictionary<string, object>;
                if (row == null)
                {
                    continue;
                }

                string key = ReadString(row, "key").ToLowerInvariant();
                int remaining = ClampPercent(100 - ReadIntOrDouble(row, "used_pct", 0));
                if (key == "h5")
                {
                    quota.FiveHourPercent = remaining;
                    quota.FiveHourResetText = EmptyFallback(ReadString(row, "reset_text_zh"), ReadString(row, "reset_text_en"));
                    quota.Known = true;
                }
                else if (key == "d7")
                {
                    quota.WeeklyPercent = remaining;
                    quota.WeeklyResetText = EmptyFallback(ReadString(row, "reset_text_zh"), ReadString(row, "reset_text_en"));
                    quota.Known = true;
                }
            }
        }

        return quota;
    }

    private static bool TryReadClaudeCodeQuotaCache(out ClaudeRadarQuotaSnapshot quota)
    {
        quota = ClaudeRadarQuotaSnapshot.CreateDefault();
        if (!File.Exists(ClaudeCodeQuotaCachePath))
        {
            return false;
        }

        bool foundFiveHourPercent = false;
        bool foundWeeklyPercent = false;
        try
        {
            string[] lines = File.ReadAllLines(ClaudeCodeQuotaCachePath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                int percent;
                DateTime dateTime;
                if (string.Equals(key, "FiveHourPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    quota.FiveHourPercent = ClampPercent(percent);
                    foundFiveHourPercent = true;
                }
                else if (string.Equals(key, "WeeklyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    quota.WeeklyPercent = ClampPercent(percent);
                    foundWeeklyPercent = true;
                }
                else if (string.Equals(key, "FiveHourReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    quota.FiveHourResetText = dateTime.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
                }
                else if (string.Equals(key, "WeeklyReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    quota.WeeklyResetText = dateTime.ToLocalTime().ToString("MM/dd", CultureInfo.CurrentCulture);
                }
                else if (string.Equals(key, "SourceUpdatedUtc", StringComparison.OrdinalIgnoreCase) &&
                    DateTime.TryParse(
                        value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out dateTime))
                {
                    quota.UpdatedAtUtc = dateTime.ToUniversalTime();
                    quota.UpdatedAtKnown = true;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        if (!foundFiveHourPercent || !foundWeeklyPercent)
        {
            return false;
        }

        if (!quota.UpdatedAtKnown)
        {
            try
            {
                quota.UpdatedAtUtc = File.GetLastWriteTimeUtc(ClaudeCodeQuotaCachePath);
                quota.UpdatedAtKnown = quota.UpdatedAtUtc != DateTime.MinValue;
            }
            catch
            {
                quota.UpdatedAtUtc = DateTime.MinValue;
                quota.UpdatedAtKnown = false;
            }
        }

        quota.Known = true;
        return true;
    }

    private static ClaudeRadarQuotaLineSnapshot BuildQuotaLineSnapshot(Dictionary<string, object> root, bool localFallbackEnabled, bool writeHistory)
    {
        Dictionary<string, object> quotaRoot = ReadObject(root, "quota");
        if (quotaRoot == null)
        {
            return ClaudeRadarQuotaLineSnapshot.CreateDefault();
        }

        string metric = "base_d7";
        string updatedAt = ReadString(quotaRoot, "updated_at");
        string runId = ReadString(ReadObject(quotaRoot, "cal"), "run_id");
        double current = ReadDouble(quotaRoot, metric, 0.0);
        if (current <= 0.0)
        {
            current = ReadQuotaMetricValue(quotaRoot, metric);
        }
        if (writeHistory && current > 0.0)
        {
            AppendQuotaHistoryIfNew(metric, current, updatedAt, runId);
        }

        List<double> trend = ReadQuotaChartTrend(quotaRoot, metric);
        if (trend.Count >= 2)
        {
            return BuildQuotaLineFromValues(trend, metric, "site_chart");
        }

        trend = ReadNumberArray(quotaRoot, "base_d7_trend");
        if (trend.Count >= 2)
        {
            return BuildQuotaLineFromValues(trend, metric, "site");
        }

        if (localFallbackEnabled)
        {
            List<double> local = ReadQuotaHistoryValues(metric, DateTime.UtcNow.AddDays(-7.0));
            if (local.Count > 0)
            {
                return BuildQuotaLineFromValues(local, metric, "local7d");
            }
        }

        if (current > 0.0)
        {
            List<double> single = new List<double>();
            single.Add(current);
            return BuildQuotaLineFromValues(single, metric, "single");
        }

        return ClaudeRadarQuotaLineSnapshot.CreateDefault();
    }

    private static ClaudeRadarQuotaLineSnapshot BuildQuotaLineFromValues(List<double> values, string metric, string sourceMode)
    {
        ClaudeRadarQuotaLineSnapshot line = ClaudeRadarQuotaLineSnapshot.CreateDefault();
        if (values == null || values.Count == 0)
        {
            return line;
        }

        double min = values[0];
        double max = values[0];
        double sum = 0.0;
        for (int i = 0; i < values.Count; i++)
        {
            min = Math.Min(min, values[i]);
            max = Math.Max(max, values[i]);
            sum += values[i];
        }

        if (Math.Abs(max - min) < 0.0001)
        {
            double pad = Math.Max(1.0, Math.Abs(max) * 0.05);
            min -= pad;
            max += pad;
        }

        line.Known = true;
        line.Metric = metric;
        line.SourceMode = sourceMode;
        line.CurrentValue = values[values.Count - 1];
        line.PreviousKnown = values.Count >= 2;
        line.PreviousValue = values.Count >= 2 ? values[values.Count - 2] : line.CurrentValue;
        line.MinValue = min;
        line.MaxValue = max;
        line.AverageValue = sum / values.Count;
        line.AverageKnown = true;
        return line;
    }

    private static ClaudeRadarCommunitySnapshot ParseCommunitySnapshot(
        Dictionary<string, object> ratingsRoot,
        ClaudeRadarModelEntry entry)
    {
        ClaudeRadarCommunitySnapshot snapshot = ClaudeRadarCommunitySnapshot.CreateDefault();
        if (ratingsRoot == null)
        {
            return snapshot;
        }

        snapshot.RefreshSeconds = Math.Max(60, ReadIntOrDouble(ratingsRoot, "refresh_seconds", 900));
        DateTime updatedUtc;
        if (TryParseDate(ReadString(ratingsRoot, "updated_at"), out updatedUtc))
        {
            snapshot.UpdatedAtUtc = updatedUtc;
        }

        object modelsObject;
        object[] models = null;
        if (ratingsRoot.TryGetValue("models", out modelsObject))
        {
            models = modelsObject as object[];
        }

        if (models == null)
        {
            return snapshot;
        }

        double bestAverage = double.MinValue;
        int bestCount = 0;
        string bestId = string.Empty;
        string bestLabel = string.Empty;
        for (int i = 0; i < models.Length; i++)
        {
            Dictionary<string, object> model = models[i] as Dictionary<string, object>;
            if (model == null)
            {
                continue;
            }

            object averageObject;
            double average;
            if (!model.TryGetValue("average", out averageObject) ||
                !TryReadDouble(averageObject, out average))
            {
                continue;
            }

            int count = ReadIntOrDouble(model, "count", 0);
            if (count <= 0)
            {
                continue;
            }

            string id = ReadString(model, "id");
            if (string.IsNullOrWhiteSpace(id) &&
                string.IsNullOrWhiteSpace(ReadString(model, "label")))
            {
                continue;
            }

            if (average > bestAverage + 0.0001 ||
                (Math.Abs(average - bestAverage) <= 0.0001 && count > bestCount))
            {
                bestAverage = average;
                bestCount = count;
                bestId = id;
                bestLabel = ReadString(model, "label");
            }
        }

        if (!string.IsNullOrWhiteSpace(bestId) || !string.IsNullOrWhiteSpace(bestLabel))
        {
            snapshot.Known = true;
            snapshot.RatingKey = bestId;
            snapshot.Label = bestLabel;
            snapshot.Average = bestAverage;
            snapshot.Count = bestCount;
        }

        return snapshot;
    }

    private static ClaudeRadarModelMapUpdate UpdateModelMap(
        List<ClaudeRadarModelMetric> metrics,
        Dictionary<string, object> ratingsRoot,
        bool completeModelCatalog)
    {
        lock (MapLock)
        {
            bool mapExistedBeforeUpdate = File.Exists(ModelMapPath);
            ClaudeRadarModelMapUpdate update = ClaudeRadarModelMapUpdate.CreateEmpty();
            List<ClaudeRadarModelEntry> entries = LoadModelMapUnlocked();
            HashSet<string> ratingKeys = ReadRatingKeys(ratingsRoot);
            ApplyModelMapUpdate(entries, metrics, ratingKeys, mapExistedBeforeUpdate, DateTime.UtcNow, update.Events, completeModelCatalog);

            SaveModelMapUnlocked(entries);
            update.Entries = CloneEntries(entries);
            return update;
        }
    }

    private static void ApplyModelMapUpdate(
        List<ClaudeRadarModelEntry> entries,
        List<ClaudeRadarModelMetric> metrics,
        HashSet<string> ratingKeys,
        bool mapExistedBeforeUpdate,
        DateTime nowUtc,
        List<ClaudeRadarModelCatalogEvent> events,
        bool completeModelCatalog)
    {
        if (entries == null)
        {
            return;
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> knownRatingKeys = ratingKeys ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool ratingsAvailable = knownRatingKeys.Count > 0;

        for (int i = 0; metrics != null && i < metrics.Count; i++)
        {
            ClaudeRadarModelMetric metric = metrics[i];
            if (metric == null || string.IsNullOrWhiteSpace(metric.SourceKey))
            {
                continue;
            }

            seen.Add(metric.SourceKey);
            ClaudeRadarModelEntry entry = FindMapEntry(entries, metric.SourceKey);
            if (entry == null)
            {
                // source_key comes from the model catalog while rating_key comes from
                // the community-ratings API. Identical display names are not enough
                // to prove both endpoints mean the same model.
                entry = new ClaudeRadarModelEntry
                {
                    SourceKey = metric.SourceKey,
                    DisplayName = metric.Name,
                    RatingKey = string.Empty,
                    Enabled = !metric.HistoricalOnly,
                    HistoricalOnly = metric.HistoricalOnly,
                    Status = "pending",
                    SortOrder = i,
                    LastSeenUtc = nowUtc,
                    MissingSuccessCount = 0,
                    Color = GenerateModelColor(metric.SourceKey)
                };
                entries.Add(entry);
                if (mapExistedBeforeUpdate)
                {
                    AddModelMapEvent(events, ClaudeRadarModelCatalogEventKind.Added, entry);
                }

                continue;
            }

            string previousStatus = entry.Status ?? string.Empty;
            if (string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                entry.DisplayName = metric.Name;
            }

            entry.RatingKey = (entry.RatingKey ?? string.Empty).Trim();
            entry.HistoricalOnly = metric.HistoricalOnly;
            entry.LastSeenUtc = nowUtc;
            entry.MissingSuccessCount = 0;
            entry.Status = ResolveMappedModelStatus(entry, ratingsAvailable, knownRatingKeys);

            if ((string.Equals(previousStatus, "deleted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(previousStatus, "temporarily_missing", StringComparison.OrdinalIgnoreCase)) &&
                !string.Equals(previousStatus, entry.Status, StringComparison.OrdinalIgnoreCase))
            {
                AddModelMapEvent(events, ClaudeRadarModelCatalogEventKind.Reappeared, entry);
            }
        }

        if (!completeModelCatalog)
        {
            entries.Sort(delegate(ClaudeRadarModelEntry left, ClaudeRadarModelEntry right)
            {
                int order = left.SortOrder.CompareTo(right.SortOrder);
                if (order != 0)
                {
                    return order;
                }

                return string.Compare(left.SourceKey, right.SourceKey, StringComparison.OrdinalIgnoreCase);
            });
            return;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            ClaudeRadarModelEntry entry = entries[i];
            if (entry == null || seen.Contains(entry.SourceKey))
            {
                continue;
            }

            string previousStatus = entry.Status ?? string.Empty;
            entry.MissingSuccessCount++;
            string nextStatus = entry.MissingSuccessCount >= ModelDeleteMissingThreshold
                ? "deleted"
                : "temporarily_missing";
            entry.Status = nextStatus;
            if (entry.MissingSuccessCount >= ModelDeleteMissingThreshold)
            {
                entry.Enabled = false;
            }

            if (!string.Equals(previousStatus, nextStatus, StringComparison.OrdinalIgnoreCase))
            {
                AddModelMapEvent(
                    events,
                    entry.MissingSuccessCount >= ModelDeleteMissingThreshold
                        ? ClaudeRadarModelCatalogEventKind.Deleted
                        : ClaudeRadarModelCatalogEventKind.TemporarilyMissing,
                    entry);
            }
        }

        entries.Sort(delegate(ClaudeRadarModelEntry left, ClaudeRadarModelEntry right)
        {
            int order = left.SortOrder.CompareTo(right.SortOrder);
            if (order != 0)
            {
                return order;
            }

            return string.Compare(left.SourceKey, right.SourceKey, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string ResolveMappedModelStatus(
        ClaudeRadarModelEntry entry,
        bool ratingsAvailable,
        HashSet<string> ratingKeys)
    {
        if (entry == null)
        {
            return "pending";
        }

        if (!entry.Enabled)
        {
            return "disabled";
        }

        string ratingKey = (entry.RatingKey ?? string.Empty).Trim();
        if (ratingKey.Length == 0)
        {
            return "pending";
        }

        if (ratingsAvailable && (ratingKeys == null || !ratingKeys.Contains(ratingKey)))
        {
            return "pending";
        }

        return "active";
    }

    private static string NormalizePersistedModelStatus(ClaudeRadarModelEntry entry)
    {
        if (entry == null)
        {
            return "pending";
        }

        if (!entry.Enabled)
        {
            string disabledStatus = (entry.Status ?? string.Empty).Trim();
            if (string.Equals(disabledStatus, "deleted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(disabledStatus, "temporarily_missing", StringComparison.OrdinalIgnoreCase))
            {
                return disabledStatus;
            }

            return "disabled";
        }

        if (string.IsNullOrWhiteSpace(entry.RatingKey))
        {
            return "pending";
        }

        string status = (entry.Status ?? string.Empty).Trim();
        return status.Length == 0 ? "active" : status;
    }

    private static void AddModelMapEvent(
        List<ClaudeRadarModelCatalogEvent> events,
        ClaudeRadarModelCatalogEventKind kind,
        ClaudeRadarModelEntry entry)
    {
        if (events == null || entry == null || string.IsNullOrWhiteSpace(entry.SourceKey))
        {
            return;
        }

        events.Add(new ClaudeRadarModelCatalogEvent
        {
            Kind = kind,
            SourceKey = NormalizeSourceKey(entry.SourceKey),
            DisplayName = EmptyFallback(entry.DisplayName, entry.SourceKey),
            Status = entry.Status ?? string.Empty
        });
    }

    private static List<ClaudeRadarModelEntry> LoadModelMapUnlocked()
    {
        List<ClaudeRadarModelEntry> entries = new List<ClaudeRadarModelEntry>();
        if (!File.Exists(ModelMapPath))
        {
            return entries;
        }

        try
        {
            string[] lines = File.ReadAllLines(ModelMapPath);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] parts = line.Split('|');
                if (parts.Length < 9)
                {
                    continue;
                }

                DateTime lastSeen;
                DateTime.TryParse(parts[7], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out lastSeen);
                int sortOrder;
                int.TryParse(parts[3], out sortOrder);
                int missing;
                int.TryParse(parts[8], out missing);
                entries.Add(new ClaudeRadarModelEntry
                {
                    SourceKey = NormalizeSourceKey(parts[0]),
                    DisplayName = Unescape(parts[1]),
                    RatingKey = parts[2],
                    SortOrder = sortOrder,
                    Enabled = bool.TrueString.Equals(parts[4], StringComparison.OrdinalIgnoreCase),
                    HistoricalOnly = bool.TrueString.Equals(parts[5], StringComparison.OrdinalIgnoreCase),
                    Status = parts[6],
                    LastSeenUtc = lastSeen == DateTime.MinValue ? DateTime.MinValue : lastSeen.ToUniversalTime(),
                    MissingSuccessCount = missing,
                    Color = parts.Length >= 10 ? ParseColor(parts[9], GenerateModelColor(parts[0])) : GenerateModelColor(parts[0])
                });
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return entries;
    }

    private static void SaveModelMapUnlocked(List<ClaudeRadarModelEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(GetStorageDirectoryPath());
            List<string> lines = new List<string>();
            lines.Add("# source_key|display_name|rating_key|sort_order|enabled|historical_only|status|last_seen_utc|missing_success_count|color");
            for (int i = 0; entries != null && i < entries.Count; i++)
            {
                ClaudeRadarModelEntry entry = entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.SourceKey))
                {
                    continue;
                }

                lines.Add(
                    NormalizeSourceKey(entry.SourceKey) +
                    "|" + Escape(entry.DisplayName) +
                    "|" + (entry.RatingKey ?? string.Empty).Trim() +
                    "|" + entry.SortOrder.ToString(CultureInfo.InvariantCulture) +
                    "|" + entry.Enabled.ToString(CultureInfo.InvariantCulture) +
                    "|" + entry.HistoricalOnly.ToString(CultureInfo.InvariantCulture) +
                    "|" + NormalizePersistedModelStatus(entry) +
                    "|" + (entry.LastSeenUtc == DateTime.MinValue ? string.Empty : entry.LastSeenUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)) +
                    "|" + entry.MissingSuccessCount.ToString(CultureInfo.InvariantCulture) +
                    "|" + ColorTranslator.ToHtml(entry.Color));
            }

            File.WriteAllLines(ModelMapPath, lines.ToArray(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static void TrySaveCache(ClaudeRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(GetStorageDirectoryPath());
            List<string> lines = new List<string>();
            lines.Add("Version=1");
            lines.Add("Known=" + snapshot.Known.ToString(CultureInfo.InvariantCulture));
            lines.Add("CheckedAtUtc=" + snapshot.CheckedAtUtc.ToString("o", CultureInfo.InvariantCulture));
            lines.Add("SelectedModelKey=" + snapshot.SelectedModelKey);
            lines.Add("SelectedModelName=" + Escape(snapshot.SelectedModelName));
            ClaudeRadarModelMetric metric = snapshot.SelectedModel ?? ClaudeRadarModelMetric.CreateDefault();
            lines.Add("IqScore=" + metric.IqScore.ToString(CultureInfo.InvariantCulture));
            lines.Add("Passed=" + metric.Passed.ToString(CultureInfo.InvariantCulture));
            lines.Add("ValidTasks=" + metric.ValidTasks.ToString(CultureInfo.InvariantCulture));
            lines.Add("TokenEfficiencyPercent=" + metric.TokenEfficiencyPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add("TimeEfficiencyPercent=" + metric.TimeEfficiencyPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add("LatestLabel=" + Escape(metric.LatestLabel));
            lines.Add("LatestAtUtc=" + (metric.LatestAtKnown && metric.LatestAtUtc != DateTime.MinValue ? metric.LatestAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) : string.Empty));
            lines.Add("NormalLow=" + metric.NormalLow.ToString(CultureInfo.InvariantCulture));
            lines.Add("NormalHigh=" + metric.NormalHigh.ToString(CultureInfo.InvariantCulture));
            ClaudeRadarCommunitySnapshot community = snapshot.Community ?? ClaudeRadarCommunitySnapshot.CreateDefault();
            lines.Add("CommunityKnown=" + community.Known.ToString(CultureInfo.InvariantCulture));
            lines.Add("CommunityRatingKey=" + Escape(community.RatingKey));
            lines.Add("CommunityLabel=" + Escape(community.Label));
            lines.Add("CommunityAverage=" + community.Average.ToString("R", CultureInfo.InvariantCulture));
            lines.Add("CommunityCount=" + community.Count.ToString(CultureInfo.InvariantCulture));
            lines.Add("CommunityUpdatedAtUtc=" + (community.UpdatedAtUtc == DateTime.MinValue ? string.Empty : community.UpdatedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
            lines.Add("CommunityRefreshSeconds=" + Math.Max(60, community.RefreshSeconds).ToString(CultureInfo.InvariantCulture));
            ClaudeRadarQuotaSnapshot quota = snapshot.Quota ?? ClaudeRadarQuotaSnapshot.CreateDefault();
            lines.Add("QuotaSource=site");
            lines.Add("FiveHourPercent=" + quota.FiveHourPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add("WeeklyPercent=" + quota.WeeklyPercent.ToString(CultureInfo.InvariantCulture));
            lines.Add("FiveHourResetText=" + Escape(quota.FiveHourResetText));
            lines.Add("WeeklyResetText=" + Escape(quota.WeeklyResetText));
            ClaudeRadarQuotaLineSnapshot line = snapshot.QuotaLine ?? ClaudeRadarQuotaLineSnapshot.CreateDefault();
            lines.Add("QuotaLineKnown=" + line.Known.ToString(CultureInfo.InvariantCulture));
            lines.Add("QuotaLineCurrentValue=" + line.CurrentValue.ToString("R", CultureInfo.InvariantCulture));
            lines.Add("QuotaLinePreviousKnown=" + line.PreviousKnown.ToString(CultureInfo.InvariantCulture));
            lines.Add("QuotaLinePreviousValue=" + line.PreviousValue.ToString("R", CultureInfo.InvariantCulture));
            lines.Add("QuotaLineMinValue=" + line.MinValue.ToString("R", CultureInfo.InvariantCulture));
            lines.Add("QuotaLineMaxValue=" + line.MaxValue.ToString("R", CultureInfo.InvariantCulture));
            lines.Add("QuotaLineAverageValue=" + line.AverageValue.ToString("R", CultureInfo.InvariantCulture));
            lines.Add("QuotaLineAverageKnown=" + line.AverageKnown.ToString(CultureInfo.InvariantCulture));
            lines.Add("QuotaLineMetric=" + Escape(line.Metric));
            lines.Add("QuotaLineSourceMode=" + Escape(line.SourceMode));
            File.WriteAllLines(CachePath, lines.ToArray(), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    private static void AppendQuotaHistoryIfNew(string metric, double value, string updatedAt, string runId)
    {
        AppendQuotaHistoryIfNew(QuotaHistoryPath, metric, value, updatedAt, runId);
    }

    private static void AppendQuotaHistoryIfNew(string path, string metric, double value, string updatedAt, string runId)
    {
        if (value <= 0.0)
        {
            return;
        }

        lock (HistoryLock)
        {
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string signature = metric + "|" + updatedAt + "|" + runId;
                if (File.Exists(path))
                {
                    string[] existing = File.ReadAllLines(path);
                    for (int i = existing.Length - 1; i >= 0; i--)
                    {
                        if (existing[i].IndexOf("\"signature\":\"" + JsonEscape(signature) + "\"", StringComparison.Ordinal) >= 0)
                        {
                            return;
                        }
                    }
                }

                string line =
                    "{\"schema_version\":1" +
                    ",\"timestamp_utc\":\"" + JsonEscape(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)) + "\"" +
                    ",\"source_updated_at\":\"" + JsonEscape(updatedAt) + "\"" +
                    ",\"run_id\":\"" + JsonEscape(runId) + "\"" +
                    ",\"metric\":\"" + JsonEscape(metric) + "\"" +
                    ",\"value\":" + value.ToString("0.###", CultureInfo.InvariantCulture) +
                    ",\"source_url\":\"" + JsonEscape(DataUrl) + "\"" +
                    ",\"signature\":\"" + JsonEscape(signature) + "\"}";
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                TrimQuotaHistory(path, DateTime.UtcNow.AddDays(-30.0));
            }
            catch (Exception ex)
            {
                Program.LogException(ex);
            }
        }
    }

    private static void TrimQuotaHistory()
    {
        TrimQuotaHistory(QuotaHistoryPath, DateTime.UtcNow.AddDays(-30.0));
    }

    private static void TrimQuotaHistory(string path, DateTime cutoffUtc)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string[] lines = File.ReadAllLines(path);
        List<string> kept = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            try
            {
                Dictionary<string, object> row = new JavaScriptSerializer().DeserializeObject(lines[i]) as Dictionary<string, object>;
                DateTime timestamp;
                if (row != null && TryParseDate(ReadString(row, "timestamp_utc"), out timestamp) && timestamp >= cutoffUtc)
                {
                    kept.Add(lines[i]);
                }
            }
            catch
            {
            }
        }

        if (kept.Count != lines.Length)
        {
            File.WriteAllLines(path, kept.ToArray(), new UTF8Encoding(false));
        }
    }

    private static List<double> ReadQuotaHistoryValues(string metric, DateTime cutoffUtc)
    {
        return ReadQuotaHistoryValues(QuotaHistoryPath, metric, cutoffUtc);
    }

    private static List<double> ReadQuotaHistoryValues(string path, string metric, DateTime cutoffUtc)
    {
        List<double> values = new List<double>();
        if (!File.Exists(path))
        {
            return values;
        }

        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            try
            {
                Dictionary<string, object> row = new JavaScriptSerializer().DeserializeObject(lines[i]) as Dictionary<string, object>;
                if (row == null || !string.Equals(ReadString(row, "metric"), metric, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime timestamp;
                if (!TryParseDate(ReadString(row, "timestamp_utc"), out timestamp) || timestamp < cutoffUtc)
                {
                    continue;
                }

                double value = ReadDouble(row, "value", 0.0);
                if (value > 0.0)
                {
                    values.Add(value);
                }
            }
            catch
            {
                // A single corrupt history row must not block later valid rows; trim will
                // eventually discard malformed lines. Avoid logging every bad line on startup.
            }
        }

        return values;
    }

    private static Dictionary<string, double> ParseTableNumberRow(Dictionary<string, object> iq, string rowName)
    {
        Dictionary<string, double> result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, object> table = ReadObject(iq, "table");
        if (table == null)
        {
            return result;
        }

        List<string> keys = new List<string>();
        object colsObject;
        object[] cols = null;
        if (table.TryGetValue("cols", out colsObject))
        {
            cols = colsObject as object[];
        }

        if (cols != null)
        {
            for (int i = 0; i < cols.Length; i++)
            {
                Dictionary<string, object> col = cols[i] as Dictionary<string, object>;
                keys.Add(col == null ? string.Empty : NormalizeSourceKey(ReadString(col, "key")));
            }
        }

        object rowsObject;
        object[] rows = null;
        if (table.TryGetValue("rows", out rowsObject))
        {
            rows = rowsObject as object[];
        }

        if (rows == null)
        {
            return result;
        }

        for (int i = 0; i < rows.Length; i++)
        {
            Dictionary<string, object> row = rows[i] as Dictionary<string, object>;
            if (row == null || ReadString(row, "name").IndexOf(rowName, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            object numsObject;
            object[] nums = null;
            if (row.TryGetValue("nums", out numsObject))
            {
                nums = numsObject as object[];
            }

            if (nums == null)
            {
                continue;
            }

            for (int j = 0; j < nums.Length && j < keys.Count; j++)
            {
                double value;
                if (TryReadDouble(nums[j], out value))
                {
                    result[keys[j]] = value;
                }
            }
        }

        return result;
    }

    private static List<double> ReadNumberArray(Dictionary<string, object> root, string key)
    {
        List<double> values = new List<double>();
        object value;
        object[] array = null;
        if (root != null && root.TryGetValue(key, out value))
        {
            array = value as object[];
        }

        if (array == null)
        {
            return values;
        }

        for (int i = 0; i < array.Length; i++)
        {
            double number;
            if (TryReadDouble(array[i], out number))
            {
                values.Add(number);
            }
        }

        return values;
    }

    private static List<double> ReadQuotaChartTrend(Dictionary<string, object> quotaRoot, string metric)
    {
        List<double> values = new List<double>();
        Dictionary<string, object> chart = ReadObject(quotaRoot, "chart");
        if (chart == null)
        {
            return values;
        }

        string key = ReadString(chart, "key");
        if (!string.IsNullOrWhiteSpace(key) && !IsClaudeQuotaD7MetricKey(key, metric))
        {
            return values;
        }

        return ReadNumberArray(chart, "trend");
    }

    private static ClaudeRadarQuotaLineSnapshot ReadCachedQuotaLine(Dictionary<string, string> values)
    {
        ClaudeRadarQuotaLineSnapshot line = ClaudeRadarQuotaLineSnapshot.CreateDefault();
        if (!bool.TrueString.Equals(ReadIniValue(values, "QuotaLineKnown"), StringComparison.OrdinalIgnoreCase))
        {
            return line;
        }

        line.Known = true;
        line.CurrentValue = ReadDoubleValue(values, "QuotaLineCurrentValue", 0.0);
        line.PreviousKnown = bool.TrueString.Equals(ReadIniValue(values, "QuotaLinePreviousKnown"), StringComparison.OrdinalIgnoreCase);
        line.PreviousValue = ReadDoubleValue(values, "QuotaLinePreviousValue", line.CurrentValue);
        line.MinValue = ReadDoubleValue(values, "QuotaLineMinValue", Math.Min(line.CurrentValue, line.PreviousValue));
        line.MaxValue = ReadDoubleValue(values, "QuotaLineMaxValue", Math.Max(line.CurrentValue, line.PreviousValue));
        line.AverageValue = ReadDoubleValue(values, "QuotaLineAverageValue", line.CurrentValue);
        line.AverageKnown = bool.TrueString.Equals(ReadIniValue(values, "QuotaLineAverageKnown"), StringComparison.OrdinalIgnoreCase);
        line.Metric = EmptyFallback(ReadIniValue(values, "QuotaLineMetric"), "base_d7");
        line.SourceMode = EmptyFallback(ReadIniValue(values, "QuotaLineSourceMode"), "cache");
        if (line.CurrentValue <= 0.0 || line.MaxValue <= line.MinValue)
        {
            return ClaudeRadarQuotaLineSnapshot.CreateDefault();
        }

        return line;
    }

    private static double ReadQuotaMetricValue(Dictionary<string, object> quotaRoot, string metric)
    {
        object metricsObject;
        object[] metrics = null;
        if (quotaRoot != null && quotaRoot.TryGetValue("metrics", out metricsObject))
        {
            metrics = metricsObject as object[];
        }

        if (metrics == null)
        {
            return 0.0;
        }

        for (int i = 0; i < metrics.Length; i++)
        {
            Dictionary<string, object> row = metrics[i] as Dictionary<string, object>;
            if (row == null)
            {
                continue;
            }

            string key = ReadString(row, "key");
            if (string.IsNullOrWhiteSpace(key) || !IsClaudeQuotaD7MetricKey(key, metric))
            {
                continue;
            }

            double value = ReadDouble(row, "value", 0.0);
            if (value > 0.0)
            {
                return value;
            }
        }

        return 0.0;
    }

    private static bool IsClaudeQuotaD7MetricKey(string key, string metric)
    {
        string normalized = NormalizeSourceKey(key);
        string normalizedMetric = NormalizeSourceKey(metric);
        return normalized.Length == 0 ||
            string.Equals(normalized, normalizedMetric, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "d7", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "7d", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "total_7d", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "quota_7d", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "d7_quota", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "weekly", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "week", StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<string> ReadRatingKeys(Dictionary<string, object> ratingsRoot)
    {
        HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object modelsObject;
        object[] models = null;
        if (ratingsRoot != null && ratingsRoot.TryGetValue("models", out modelsObject))
        {
            models = modelsObject as object[];
        }

        if (models == null)
        {
            return keys;
        }

        for (int i = 0; i < models.Length; i++)
        {
            Dictionary<string, object> model = models[i] as Dictionary<string, object>;
            string id = model == null ? string.Empty : ReadString(model, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                keys.Add(id);
            }
        }

        return keys;
    }

    private static string PickDefaultModel(List<ClaudeRadarModelMetric> metrics, List<ClaudeRadarModelEntry> entries)
    {
        if (metrics != null)
        {
            for (int i = 0; i < metrics.Count; i++)
            {
                if (metrics[i] != null && !metrics[i].HistoricalOnly)
                {
                    return metrics[i].SourceKey;
                }
            }

            if (metrics.Count > 0 && metrics[0] != null)
            {
                return metrics[0].SourceKey;
            }
        }

        if (entries != null && entries.Count > 0)
        {
            return entries[0].SourceKey;
        }

        return string.Empty;
    }

    private static bool ContainsModel(List<ClaudeRadarModelMetric> metrics, string key)
    {
        return FindMetric(metrics, key) != null;
    }

    private static List<ClaudeRadarModelMetric> CloneModelMetrics(List<ClaudeRadarModelMetric> metrics)
    {
        List<ClaudeRadarModelMetric> result = new List<ClaudeRadarModelMetric>();
        if (metrics == null)
        {
            return result;
        }

        for (int i = 0; i < metrics.Count; i++)
        {
            if (metrics[i] != null)
            {
                result.Add(metrics[i].Clone());
            }
        }

        return result;
    }

    private static ClaudeRadarModelMetric FindMetric(List<ClaudeRadarModelMetric> metrics, string key)
    {
        for (int i = 0; metrics != null && i < metrics.Count; i++)
        {
            if (metrics[i] != null && string.Equals(metrics[i].SourceKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return metrics[i];
            }
        }

        return null;
    }

    private static ClaudeRadarModelEntry FindMapEntry(List<ClaudeRadarModelEntry> entries, string key)
    {
        for (int i = 0; entries != null && i < entries.Count; i++)
        {
            if (entries[i] != null && string.Equals(entries[i].SourceKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return entries[i];
            }
        }

        return null;
    }

    private static List<ClaudeRadarModelEntry> CloneEntries(List<ClaudeRadarModelEntry> entries)
    {
        List<ClaudeRadarModelEntry> clone = new List<ClaudeRadarModelEntry>();
        for (int i = 0; entries != null && i < entries.Count; i++)
        {
            if (entries[i] != null)
            {
                clone.Add(entries[i].Clone());
            }
        }

        return clone;
    }

    private static Color GenerateModelColor(string key)
    {
        int hash = string.IsNullOrEmpty(key) ? 0 : key.GetHashCode();
        int r = 180 + Math.Abs(hash % 60);
        int g = 95 + Math.Abs((hash >> 8) % 80);
        int b = 55 + Math.Abs((hash >> 16) % 90);
        return Color.FromArgb(255, Math.Min(245, r), Math.Min(210, g), Math.Min(190, b));
    }

    private static Color ParseColor(string value, Color fallback)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            return ColorTranslator.FromHtml(value.Trim());
        }
        catch
        {
            return fallback;
        }
    }

    private static int CalculateEfficiency(int passed, double value, int baselinePassed, double baselineValue, bool higherValueIsBetter)
    {
        if (passed <= 0 || value <= 0.0 || baselinePassed <= 0 || baselineValue <= 0.0)
        {
            return 100;
        }

        double currentPerPass = value / passed;
        double baselinePerPass = baselineValue / baselinePassed;
        double efficiency = higherValueIsBetter
            ? currentPerPass / baselinePerPass * 100.0
            : baselinePerPass / currentPerPass * 100.0;
        return Math.Max(0, Math.Min(999, (int)Math.Round(efficiency)));
    }

    private static int ReadLastArrayInt(Dictionary<string, object> values, string key, int fallback)
    {
        double value = ReadLastArrayDouble(values, key, fallback);
        return (int)Math.Round(value);
    }

    private static double ReadLastArrayDouble(Dictionary<string, object> values, string key, double fallback)
    {
        object raw;
        object[] array = null;
        if (values != null && values.TryGetValue(key, out raw))
        {
            array = raw as object[];
        }

        if (array == null)
        {
            return fallback;
        }

        for (int i = array.Length - 1; i >= 0; i--)
        {
            double value;
            if (TryReadDouble(array[i], out value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static Dictionary<string, object> ReadObject(Dictionary<string, object> values, string key)
    {
        object value;
        if (values != null && values.TryGetValue(key, out value))
        {
            return value as Dictionary<string, object>;
        }

        return null;
    }

    private static string ReadString(Dictionary<string, object> values, string key)
    {
        object value;
        if (values != null && values.TryGetValue(key, out value) && value != null)
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return string.Empty;
    }

    private static int ReadIntOrDouble(Dictionary<string, object> values, string key, int fallback)
    {
        double value;
        object raw;
        if (values != null && values.TryGetValue(key, out raw) && TryReadDouble(raw, out value))
        {
            return (int)Math.Round(value);
        }

        return fallback;
    }

    private static double ReadDouble(Dictionary<string, object> values, string key, double fallback)
    {
        double value;
        object raw;
        if (values != null && values.TryGetValue(key, out raw) && TryReadDouble(raw, out value))
        {
            return value;
        }

        return fallback;
    }

    private static bool ReadBool(Dictionary<string, object> values, string key, bool fallback)
    {
        string value = ReadString(values, key);
        bool parsed;
        return bool.TryParse(value, out parsed) ? parsed : fallback;
    }

    private static bool TryReadDouble(object raw, out double value)
    {
        value = 0.0;
        if (raw == null)
        {
            return false;
        }

        if (raw is double)
        {
            value = (double)raw;
            return true;
        }

        if (raw is decimal)
        {
            value = (double)(decimal)raw;
            return true;
        }

        if (raw is int)
        {
            value = (int)raw;
            return true;
        }

        if (raw is long)
        {
            value = (long)raw;
            return true;
        }

        return double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static DateTime ReadDateValue(Dictionary<string, string> values, string key)
    {
        DateTime value;
        if (DateTime.TryParse(ReadIniValue(values, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            return value.ToUniversalTime();
        }

        return DateTime.MinValue;
    }

    private static int ReadIntValue(Dictionary<string, string> values, string key, int fallback)
    {
        int value;
        return int.TryParse(ReadIniValue(values, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback;
    }

    private static double ReadDoubleValue(Dictionary<string, string> values, string key, double fallback)
    {
        double value;
        return double.TryParse(ReadIniValue(values, key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
    }

    private static string ReadIniValue(Dictionary<string, string> values, string key)
    {
        string value;
        return values != null && values.TryGetValue(key, out value) ? Unescape(value) : string.Empty;
    }

    private static Dictionary<string, string> ReadIniValues(string path)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = File.ReadAllLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int split = line.IndexOf('=');
            if (split <= 0)
            {
                continue;
            }

            values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
        }

        return values;
    }

    private static bool TryParseDate(string text, out DateTime utc)
    {
        utc = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        DateTimeOffset offset;
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out offset))
        {
            utc = offset.UtcDateTime;
            return true;
        }

        DateTime parsed;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
        {
            utc = parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static bool IsOffline()
    {
        try
        {
            return !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNetworkAvailable()
    {
        return !IsOffline();
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private static int ClampScore(int value)
    {
        return Math.Max(0, Math.Min(999, value));
    }

    private static string NormalizeSourceKey(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string EmptyFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? (fallback ?? string.Empty) : value;
    }

    private static string Escape(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("|", "\\p").Replace("\r", " ").Replace("\n", " ");
    }

    private static string Unescape(string value)
    {
        return (value ?? string.Empty).Replace("\\p", "|").Replace("\\\\", "\\");
    }

    private static string JsonEscape(string value)
    {
        return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
