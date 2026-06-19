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

internal static class CodexRadarModelCatalog
{
    public const string DefaultModelKey = "gpt_55_xhigh";
    private const int DeleteAfterMissingCount = 3;

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
                if (key.Length == 0 || seen.Contains(key))
                {
                    continue;
                }

                bool available;
                int missingCount;
                DateTime lastSeenUtc;
                models.Add(new CodexRadarModelInfo
                {
                    Key = key,
                    Label = GetDisplayLabel(GetValue(values, prefix + "Label", string.Empty), key),
                    Available = !TryReadBool(values, prefix + "Available", out available) || available,
                    MissingCount = TryReadInt(values, prefix + "MissingCount", out missingCount) ? Math.Max(0, missingCount) : 0,
                    LastSeenUtc = TryReadUtc(values, prefix + "LastSeenUtc", out lastSeenUtc) ? lastSeenUtc : DateTime.MinValue
                });
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

    public static CodexRadarModelCatalogUpdate MergeAndSave(IList<CodexRadarModelInfo> discovered)
    {
        CodexRadarModelCatalogUpdate update = new CodexRadarModelCatalogUpdate();
        List<CodexRadarModelInfo> existing = LoadModels();
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
                    LastSeenUtc = DateTime.UtcNow
                };
            }
        }

        if (discoveredByKey.Count == 0)
        {
            return update;
        }

        Dictionary<string, CodexRadarModelInfo> existingByKey =
            new Dictionary<string, CodexRadarModelInfo>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < existing.Count; i++)
        {
            CodexRadarModelInfo model = existing[i];
            if (model != null && !string.IsNullOrEmpty(model.Key) && !existingByKey.ContainsKey(model.Key))
            {
                existingByKey[model.Key] = model;
            }
        }

        List<CodexRadarModelInfo> merged = new List<CodexRadarModelInfo>();
        for (int i = 0; i < existing.Count; i++)
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

        SaveModels(merged);
        return update;
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

            File.WriteAllLines(tempPath, lines.ToArray(), new UTF8Encoding(false));
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

        normalized = normalized.Replace("gpt-5.5", "gpt_55");
        normalized = normalized.Replace("gpt-5.4", "gpt_54");
        normalized = Regex.Replace(normalized, "[^a-z0-9]+", "_").Trim('_');
        normalized = normalized.Replace("gpt_5_5", "gpt_55");
        normalized = normalized.Replace("gpt_5_4", "gpt_54");
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
        if (normalized.StartsWith("gpt_55_", StringComparison.OrdinalIgnoreCase))
        {
            return "GPT-5.5 " + normalized.Substring("gpt_55_".Length).Replace('_', ' ');
        }

        if (normalized.StartsWith("gpt_54_", StringComparison.OrdinalIgnoreCase))
        {
            return "GPT-5.4 " + normalized.Substring("gpt_54_".Length).Replace('_', ' ');
        }

        return normalized.Length == 0 ? "--" : normalized.Replace('_', ' ');
    }

    public static bool IsDefaultModelKey(string key)
    {
        return string.Equals(NormalizeModelKey(key), DefaultModelKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeModelKey(key), "gpt_55_medium", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeModelKey(key), "gpt_54_xhigh", StringComparison.OrdinalIgnoreCase);
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

        return DefaultModelKey;
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
            new CodexRadarModelInfo { Key = DefaultModelKey, Label = "GPT-5.5 xhigh", Available = true, LastSeenUtc = nowUtc },
            new CodexRadarModelInfo { Key = "gpt_55_medium", Label = "GPT-5.5 medium", Available = true, LastSeenUtc = nowUtc },
            new CodexRadarModelInfo { Key = "gpt_54_xhigh", Label = "GPT-5.4 xhigh", Available = true, LastSeenUtc = nowUtc }
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
