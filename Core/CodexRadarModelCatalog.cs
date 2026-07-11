using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

internal enum CodexModelBaselineMode
{
    Absolute,
    Recent7Average,
    Recent30Average,
    AllRecordsAverage
}

internal sealed class CodexRadarModelInfo
{
    public string Key { get; set; }
    public string Label { get; set; }
    public bool Available { get; set; }
    public int MissingCount { get; set; }
    public DateTime LastSeenUtc { get; set; }

    public CodexRadarModelInfo Clone()
    {
        return new CodexRadarModelInfo
        {
            Key = this.Key,
            Label = this.Label,
            Available = this.Available,
            MissingCount = this.MissingCount,
            LastSeenUtc = this.LastSeenUtc
        };
    }
}

internal sealed class CodexRadarModelCatalogUpdate
{
    public readonly List<CodexRadarModelInfo> Added = new List<CodexRadarModelInfo>();
    public readonly List<CodexRadarModelInfo> Unavailable = new List<CodexRadarModelInfo>();
    public readonly List<CodexRadarModelInfo> Deleted = new List<CodexRadarModelInfo>();
}

internal sealed class CodexRadarModelCatalogNotificationSummary
{
    public int AddedCount { get; set; }
    public int UnavailableCount { get; set; }
    public int DeletedCount { get; set; }
    public readonly List<string> RepresentativeLabels = new List<string>();

    public int TotalCount
    {
        get { return this.AddedCount + this.UnavailableCount + this.DeletedCount; }
    }
}

internal static class CodexRadarModelCatalog
{
    public const string DefaultModelKey = "gpt_56_sol_medium";
    public const string PreviousDefaultModelKey = "gpt_55_xhigh";
    private const int DeleteAfterMissingCount = 3;
    public const int ConsolidatedNotificationThreshold = 4;

    public static string CatalogPath
    {
        get { return Path.Combine(Logger.DirectoryPath, "codex-radar-models.ini"); }
    }

    public static List<CodexRadarModelInfo> LoadModels()
    {
        string path = CatalogPath;
        if (!File.Exists(path))
        {
            return CreateDefaultModels();
        }

        try
        {
            Dictionary<string, string> values = ReadSimpleKeyValueFile(path);
            int count;
            if (!TryReadInt(values, "ModelCount", out count) || count <= 0)
            {
                return CreateDefaultModels();
            }

            List<CodexRadarModelInfo> models = new List<CodexRadarModelInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                string prefix = "Model" + i.ToString(CultureInfo.InvariantCulture);
                string key = NormalizeModelKey(GetValue(values, prefix + "Key", string.Empty));
                if (key.Length == 0)
                {
                    continue;
                }

                bool available;
                int missingCount;
                DateTime lastSeenUtc;
                CodexRadarModelInfo candidate = new CodexRadarModelInfo
                {
                    Key = key,
                    Label = GetDisplayLabel(GetValue(values, prefix + "Label", string.Empty), key),
                    Available = !TryReadBool(values, prefix + "Available", out available) || available,
                    MissingCount = TryReadInt(values, prefix + "MissingCount", out missingCount) ? Math.Max(0, missingCount) : 0,
                    LastSeenUtc = TryReadUtc(values, prefix + "LastSeenUtc", out lastSeenUtc) ? lastSeenUtc : DateTime.MinValue
                };
                if (seen.Contains(key))
                {
                    // Older builds could persist gpt_5_6_* and gpt_56_* as separate records.
                    // Treat one record as an indivisible observation so a newer unavailable
                    // state cannot be revived by an older optimistic field value.
                    for (int modelIndex = 0; modelIndex < models.Count; modelIndex++)
                    {
                        CodexRadarModelInfo existing = models[modelIndex];
                        if (!string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        MergeDuplicateCatalogRecord(existing, candidate);

                        break;
                    }

                    continue;
                }

                models.Add(candidate);
                seen.Add(key);
            }

            return models.Count > 0 ? models : CreateDefaultModels();
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return CreateDefaultModels();
        }
    }

    public static CodexRadarModelCatalogUpdate MergeAndSave(
        IList<CodexRadarModelInfo> discovered,
        bool completeCatalog)
    {
        List<CodexRadarModelInfo> existing = LoadModels();
        CodexRadarModelCatalogUpdate update;
        List<CodexRadarModelInfo> merged = MergeCatalogRecords(
            existing,
            discovered,
            completeCatalog,
            DateTime.UtcNow,
            out update);
        if (merged != null)
        {
            SaveModels(merged);
        }

        return update;
    }

    internal static void MergeDuplicateCatalogRecord(
        CodexRadarModelInfo existing,
        CodexRadarModelInfo candidate)
    {
        if (existing == null || candidate == null)
        {
            return;
        }

        bool candidateWins =
            candidate.LastSeenUtc > existing.LastSeenUtc ||
            (candidate.LastSeenUtc == existing.LastSeenUtc &&
             candidate.Available && !existing.Available);
        if (!candidateWins)
        {
            return;
        }

        existing.Label = candidate.Label;
        existing.Available = candidate.Available;
        existing.MissingCount = candidate.MissingCount;
        existing.LastSeenUtc = candidate.LastSeenUtc;
    }

    private static List<CodexRadarModelInfo> MergeCatalogRecords(
        IList<CodexRadarModelInfo> existing,
        IList<CodexRadarModelInfo> discovered,
        bool completeCatalog,
        DateTime observedUtc,
        out CodexRadarModelCatalogUpdate update)
    {
        update = new CodexRadarModelCatalogUpdate();
        Dictionary<string, CodexRadarModelInfo> discoveredByKey =
            new Dictionary<string, CodexRadarModelInfo>(StringComparer.OrdinalIgnoreCase);
        if (discovered != null)
        {
            for (int i = 0; i < discovered.Count; i++)
            {
                CodexRadarModelInfo model = discovered[i];
                if (model == null)
                {
                    continue;
                }

                string key = NormalizeModelKey(model.Key);
                if (key.Length == 0 || discoveredByKey.ContainsKey(key))
                {
                    continue;
                }

                discoveredByKey[key] = new CodexRadarModelInfo
                {
                    Key = key,
                    Label = GetDisplayLabel(model.Label, key),
                    Available = true,
                    MissingCount = 0,
                    LastSeenUtc = observedUtc
                };
            }
        }

        if (discoveredByKey.Count == 0)
        {
            return null;
        }

        Dictionary<string, CodexRadarModelInfo> existingByKey =
            new Dictionary<string, CodexRadarModelInfo>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; existing != null && i < existing.Count; i++)
        {
            CodexRadarModelInfo model = existing[i];
            if (model != null && !string.IsNullOrEmpty(model.Key) && !existingByKey.ContainsKey(model.Key))
            {
                existingByKey[model.Key] = model;
            }
        }

        List<CodexRadarModelInfo> merged = new List<CodexRadarModelInfo>();
        for (int i = 0; existing != null && i < existing.Count; i++)
        {
            CodexRadarModelInfo old = existing[i];
            if (old == null || string.IsNullOrEmpty(old.Key))
            {
                continue;
            }

            CodexRadarModelInfo fresh;
            if (discoveredByKey.TryGetValue(old.Key, out fresh))
            {
                merged.Add(fresh.Clone());
                discoveredByKey.Remove(old.Key);
                continue;
            }

            if (!completeCatalog)
            {
                merged.Add(old.Clone());
                continue;
            }

            CodexRadarModelInfo missing = old.Clone();
            missing.Available = false;
            missing.MissingCount = Math.Max(0, missing.MissingCount) + 1;
            if (missing.MissingCount >= DeleteAfterMissingCount)
            {
                update.Deleted.Add(missing.Clone());
            }
            else
            {
                if (missing.MissingCount == 1)
                {
                    update.Unavailable.Add(missing.Clone());
                }

                merged.Add(missing);
            }
        }

        foreach (CodexRadarModelInfo fresh in discoveredByKey.Values)
        {
            merged.Add(fresh.Clone());
            if (!IsDefaultModelKey(fresh.Key) && !existingByKey.ContainsKey(fresh.Key))
            {
                update.Added.Add(fresh.Clone());
            }
        }

        return merged;
    }

    public static void SaveModels(IList<CodexRadarModelInfo> models)
    {
        try
        {
            Directory.CreateDirectory(Logger.DirectoryPath);
            string tempPath = CatalogPath + ".tmp";
            List<string> lines = new List<string>();
            lines.Add("Version=1");
            int count = models == null ? 0 : models.Count;
            lines.Add("ModelCount=" + count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < count; i++)
            {
                CodexRadarModelInfo model = models[i] ?? new CodexRadarModelInfo();
                string prefix = "Model" + i.ToString(CultureInfo.InvariantCulture);
                lines.Add(prefix + "Key=" + NormalizeModelKey(model.Key));
                lines.Add(prefix + "Label=" + (model.Label ?? string.Empty));
                lines.Add(prefix + "Available=" + (model.Available ? "True" : "False"));
                lines.Add(prefix + "MissingCount=" + Math.Max(0, model.MissingCount).ToString(CultureInfo.InvariantCulture));
                lines.Add(prefix + "LastSeenUtc=" + (model.LastSeenUtc == DateTime.MinValue ? string.Empty : model.LastSeenUtc.ToString("o", CultureInfo.InvariantCulture)));
            }

            File.WriteAllLines(tempPath, lines.ToArray(), SharedEncoding.Utf8NoBom);
            if (File.Exists(CatalogPath))
            {
                File.Replace(tempPath, CatalogPath, null);
            }
            else
            {
                File.Move(tempPath, CatalogPath);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    public static string NormalizeModelKey(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        normalized = Regex.Replace(normalized, "[^a-z0-9]+", "_").Trim('_');
        // Website JSON mixes model values such as gpt-5.6-sol with comparison keys such as
        // gpt_56_sol_medium. Collapse the dotted form generically so one model cannot occupy
        // two catalog/cache identities when the site adds another minor version.
        normalized = Regex.Replace(
            normalized,
            "^gpt_([0-9]+)_([0-9]+)(?=_|$)",
            "gpt_$1$2",
            RegexOptions.IgnoreCase);
        return normalized;
    }

    public static string BuildModelKey(string model, string reasoningEffort, string fallback)
    {
        string combined = (model ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            combined += "_" + reasoningEffort.Trim();
        }

        string normalized = NormalizeModelKey(combined);
        if (normalized.Length == 0)
        {
            normalized = NormalizeModelKey(fallback);
        }

        return normalized;
    }

    public static string GetDisplayLabel(string label, string key)
    {
        string trimmed = (label ?? string.Empty).Trim();
        if (trimmed.Length > 0)
        {
            return trimmed;
        }

        string normalized = NormalizeModelKey(key);
        Match versioned = Regex.Match(
            normalized,
            "^gpt_([0-9])([0-9]+)_(.+)$",
            RegexOptions.IgnoreCase);
        if (versioned.Success)
        {
            string[] segments = versioned.Groups[3].Value.Split('_');
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = (segments[i] ?? string.Empty).ToLowerInvariant();
                if (!IsReasoningEffortSegment(segment) && segment.Length > 0)
                {
                    segment = char.ToUpperInvariant(segment[0]) + segment.Substring(1);
                }

                segments[i] = segment;
            }

            return "GPT-" + versioned.Groups[1].Value + "." + versioned.Groups[2].Value +
                " " + string.Join(" ", segments);
        }

        return normalized.Length == 0 ? "--" : normalized.Replace('_', ' ');
    }

    public static bool IsDefaultModelKey(string key)
    {
        string normalized = NormalizeModelKey(key);
        return string.Equals(normalized, DefaultModelKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "gpt_56_sol_ultra", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "gpt_56_sol_low", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "gpt_56_terra_medium", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "gpt_56_luna_medium", StringComparison.OrdinalIgnoreCase);
    }

    internal static CodexRadarModelCatalogNotificationSummary BuildNotificationSummary(
        CodexRadarModelCatalogUpdate update)
    {
        CodexRadarModelCatalogNotificationSummary summary = new CodexRadarModelCatalogNotificationSummary();
        if (update == null)
        {
            return summary;
        }

        summary.AddedCount = update.Added.Count;
        summary.UnavailableCount = update.Unavailable.Count;
        summary.DeletedCount = update.Deleted.Count;
        AddRepresentativeLabels(summary, update.Added);
        AddRepresentativeLabels(summary, update.Unavailable);
        AddRepresentativeLabels(summary, update.Deleted);
        return summary;
    }

    internal static void RunSelfTest()
    {
        DateTime t1 = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        DateTime t2 = t1.AddMinutes(1);
        CodexRadarModelInfo existing = TestModel("gpt_56_sol_medium", "old", true, 0, t1);
        MergeDuplicateCatalogRecord(existing, TestModel("gpt_56_sol_medium", "new", false, 2, t2));
        AssertCatalog(existing.Label == "new" && !existing.Available && existing.MissingCount == 2 && existing.LastSeenUtc == t2,
            "newer unavailable duplicate must replace the older optimistic record");

        existing = TestModel("gpt_56_sol_medium", "old", false, 2, t1);
        MergeDuplicateCatalogRecord(existing, TestModel("gpt_56_sol_medium", "new", true, 0, t2));
        AssertCatalog(existing.Available && existing.MissingCount == 0 && existing.Label == "new",
            "newer available duplicate must replace the older unavailable record");

        existing = TestModel("gpt_56_sol_medium", "old", false, 2, DateTime.MinValue);
        MergeDuplicateCatalogRecord(existing, TestModel("gpt_56_sol_medium", "new", true, 0, DateTime.MinValue));
        AssertCatalog(existing.Available && existing.Label == "new",
            "equal legacy timestamps must prefer the available record");

        existing = TestModel("gpt_56_sol_medium", "first", true, 0, t1);
        MergeDuplicateCatalogRecord(existing, TestModel("gpt_56_sol_medium", "second", true, 0, t1));
        AssertCatalog(existing.Label == "first", "equal duplicate observations must preserve file order");

        AssertCatalog(GetDisplayLabel(string.Empty, "gpt_56_sol_medium") == "GPT-5.6 Sol medium", "Sol label casing");
        AssertCatalog(GetDisplayLabel(string.Empty, "gpt_56_terra_medium") == "GPT-5.6 Terra medium", "Terra label casing");
        AssertCatalog(GetDisplayLabel(string.Empty, "gpt_57_nova_high") == "GPT-5.7 Nova high", "Nova label casing");
        AssertCatalog(GetDisplayLabel(string.Empty, "gpt_55_xhigh") == "GPT-5.5 xhigh", "effort-only label casing");

        List<CodexRadarModelInfo> baseline = new List<CodexRadarModelInfo>
        {
            TestModel("gpt_56_sol_medium", "Sol", true, 0, t1),
            TestModel("gpt_56_terra_medium", "Terra", true, 0, t1)
        };
        List<CodexRadarModelInfo> partial = new List<CodexRadarModelInfo>
        {
            TestModel("gpt_56_sol_medium", "Sol", true, 0, t2),
            TestModel("gpt_57_nova_high", "Nova", true, 0, t2)
        };
        CodexRadarModelCatalogUpdate partialUpdate;
        List<CodexRadarModelInfo> partialMerged = MergeCatalogRecords(baseline, partial, false, t2, out partialUpdate);
        AssertCatalog(FindModel(partialMerged, "gpt_56_terra_medium").MissingCount == 0 && partialUpdate.Added.Count == 1,
            "an incomplete catalog must preserve missing records and still add discoveries");

        CodexRadarModelCatalogUpdate completeUpdate;
        List<CodexRadarModelInfo> completeMerged = MergeCatalogRecords(baseline, partial, true, t2, out completeUpdate);
        AssertCatalog(FindModel(completeMerged, "gpt_56_terra_medium").MissingCount == 1 && completeUpdate.Unavailable.Count == 1,
            "a complete catalog must advance missing counters");

        CodexRadarModelCatalogUpdate notificationUpdate = new CodexRadarModelCatalogUpdate();
        for (int i = 0; i < 5; i++)
        {
            notificationUpdate.Added.Add(TestModel("gpt_57_test_" + i.ToString(CultureInfo.InvariantCulture), "Model " + i.ToString(CultureInfo.InvariantCulture), true, 0, t2));
        }

        CodexRadarModelCatalogNotificationSummary summary = BuildNotificationSummary(notificationUpdate);
        AssertCatalog(summary.TotalCount == 5 && summary.AddedCount == 5 && summary.RepresentativeLabels.Count == 3,
            "catalog replacement summary must cap representative labels at three");
    }

    private static bool IsReasoningEffortSegment(string value)
    {
        return string.Equals(value, "xhigh", StringComparison.Ordinal) ||
            string.Equals(value, "ultra", StringComparison.Ordinal) ||
            string.Equals(value, "high", StringComparison.Ordinal) ||
            string.Equals(value, "medium", StringComparison.Ordinal) ||
            string.Equals(value, "low", StringComparison.Ordinal);
    }

    private static void AddRepresentativeLabels(
        CodexRadarModelCatalogNotificationSummary summary,
        IList<CodexRadarModelInfo> models)
    {
        for (int i = 0; models != null && i < models.Count && summary.RepresentativeLabels.Count < 3; i++)
        {
            CodexRadarModelInfo model = models[i];
            summary.RepresentativeLabels.Add(GetDisplayLabel(
                model == null ? string.Empty : model.Label,
                model == null ? string.Empty : model.Key));
        }
    }

    private static CodexRadarModelInfo TestModel(
        string key,
        string label,
        bool available,
        int missingCount,
        DateTime lastSeenUtc)
    {
        return new CodexRadarModelInfo
        {
            Key = key,
            Label = label,
            Available = available,
            MissingCount = missingCount,
            LastSeenUtc = lastSeenUtc
        };
    }

    private static CodexRadarModelInfo FindModel(IList<CodexRadarModelInfo> models, string key)
    {
        for (int i = 0; models != null && i < models.Count; i++)
        {
            if (string.Equals(models[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return models[i];
            }
        }

        throw new InvalidOperationException("Codex Radar model catalog self-test could not find " + key + ".");
    }

    private static void AssertCatalog(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Codex Radar model catalog self-test failed: " + message + ".");
        }
    }

    public static string LegacyKeyFromVersion(CodexRadarModelVersion version)
    {
        if (version == CodexRadarModelVersion.Gpt55Medium)
        {
            return "gpt_55_medium";
        }

        if (version == CodexRadarModelVersion.Gpt54)
        {
            return "gpt_54_xhigh";
        }

        return PreviousDefaultModelKey;
    }

    public static CodexRadarModelVersion LegacyVersionFromKey(string key)
    {
        string normalized = NormalizeModelKey(key);
        if (string.Equals(normalized, "gpt_55_medium", StringComparison.OrdinalIgnoreCase))
        {
            return CodexRadarModelVersion.Gpt55Medium;
        }

        if (string.Equals(normalized, "gpt_54_xhigh", StringComparison.OrdinalIgnoreCase))
        {
            return CodexRadarModelVersion.Gpt54;
        }

        return CodexRadarModelVersion.Gpt55;
    }

    private static List<CodexRadarModelInfo> CreateDefaultModels()
    {
        DateTime nowUtc = DateTime.UtcNow;
        return new List<CodexRadarModelInfo>
        {
            new CodexRadarModelInfo { Key = "gpt_56_sol_ultra", Label = "GPT-5.6 Sol ultra", Available = true, LastSeenUtc = nowUtc },
            new CodexRadarModelInfo { Key = DefaultModelKey, Label = "GPT-5.6 Sol medium", Available = true, LastSeenUtc = nowUtc },
            new CodexRadarModelInfo { Key = "gpt_56_sol_low", Label = "GPT-5.6 Sol low", Available = true, LastSeenUtc = nowUtc },
            new CodexRadarModelInfo { Key = "gpt_56_terra_medium", Label = "GPT-5.6 Terra medium", Available = true, LastSeenUtc = nowUtc },
            new CodexRadarModelInfo { Key = "gpt_56_luna_medium", Label = "GPT-5.6 Luna medium", Available = true, LastSeenUtc = nowUtc }
        };
    }

    private static Dictionary<string, string> ReadSimpleKeyValueFile(string path)
    {
        Dictionary<string, string> values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

    private static string GetValue(Dictionary<string, string> values, string key, string fallback)
    {
        string value;
        return values != null && values.TryGetValue(key, out value) ? value : fallback;
    }

    private static bool TryReadBool(Dictionary<string, string> values, string key, out bool value)
    {
        value = false;
        string raw;
        return values != null && values.TryGetValue(key, out raw) && bool.TryParse(raw, out value);
    }

    private static bool TryReadInt(Dictionary<string, string> values, string key, out int value)
    {
        value = 0;
        string raw;
        return values != null && values.TryGetValue(key, out raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReadUtc(Dictionary<string, string> values, string key, out DateTime value)
    {
        value = DateTime.MinValue;
        string raw;
        DateTimeOffset offset;
        if (values == null ||
            !values.TryGetValue(key, out raw) ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out offset))
        {
            return false;
        }

        value = offset.UtcDateTime;
        return true;
    }
}
