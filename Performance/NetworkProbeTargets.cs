using System;
using System.Collections.Generic;
using System.Net;

// Canonical settings representation shared by WidgetSettings, the settings editor and both
// probe readers. Keeping parsing here prevents the UI and background workers from disagreeing
// about whether a disabled row is still eligible for network traffic.
internal sealed class NetworkProbeTargetDefinition
{
    public string Key;
    public string DisplayName;
    public string Target;
    public bool Enabled;
    public bool BuiltIn;
}

internal static class NetworkProbeTargetSettings
{
    internal const int MaxCloudCustomTargets = 8;
    internal const int MaxFixedPingTargets = 8;

    private static readonly string[] CloudKeys =
    {
        "cloudflare", "akamai", "github", "aws", "azure", "google"
    };

    private static readonly string[] CloudNames =
    {
        "Cloudflare", "Akamai", "GitHub", "AWS", "Azure", "Google"
    };

    internal static readonly string[] DefaultCloudEndpointTargets =
    {
        "builtin|cloudflare|1",
        "builtin|akamai|1",
        "builtin|github|1",
        "builtin|aws|1",
        "builtin|azure|1",
        "builtin|google|1"
    };

    internal static readonly string[] DefaultFixedPingTargets =
    {
        "target|Google|8.8.8.8|1",
        "target|百度|180.101.50.188|1",
        "target|Yahoo|98.137.11.163|1"
    };

    internal static string[] CloneArray(string[] values)
    {
        if (values == null || values.Length == 0)
        {
            return new string[0];
        }

        string[] clone = new string[values.Length];
        Array.Copy(values, clone, values.Length);
        return clone;
    }

    internal static string[] NormalizeCloudTargets(string[] values)
    {
        List<NetworkProbeTargetDefinition> definitions = ParseCloudTargets(values);
        string[] result = new string[definitions.Count];
        for (int i = 0; i < definitions.Count; i++)
        {
            NetworkProbeTargetDefinition definition = definitions[i];
            result[i] = definition.BuiltIn
                ? "builtin|" + definition.Key + "|" + FormatEnabled(definition.Enabled)
                : "custom|" + EscapePart(definition.DisplayName) + "|" + EscapePart(definition.Target) + "|" + FormatEnabled(definition.Enabled);
        }

        return result;
    }

    internal static string[] NormalizeFixedPingTargets(string[] values)
    {
        List<NetworkProbeTargetDefinition> definitions = ParseFixedPingTargets(values);
        string[] result = new string[definitions.Count];
        for (int i = 0; i < definitions.Count; i++)
        {
            NetworkProbeTargetDefinition definition = definitions[i];
            result[i] = "target|" + EscapePart(definition.DisplayName) + "|" + EscapePart(definition.Target) + "|" + FormatEnabled(definition.Enabled);
        }

        return result;
    }

    internal static List<NetworkProbeTargetDefinition> ParseCloudTargets(string[] values)
    {
        bool[] enabled = new bool[CloudKeys.Length];
        for (int i = 0; i < enabled.Length; i++)
        {
            enabled[i] = true;
        }

        bool sawBuiltIn = false;
        List<NetworkProbeTargetDefinition> custom = new List<NetworkProbeTargetDefinition>();
        HashSet<string> customTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                string[] parts = SplitEntry(values[i]);
                if (parts.Length >= 3 && string.Equals(parts[0], "builtin", StringComparison.OrdinalIgnoreCase))
                {
                    int index = IndexOfCloudKey(parts[1]);
                    if (index >= 0)
                    {
                        enabled[index] = ParseEnabled(parts[2], true);
                        sawBuiltIn = true;
                    }

                    continue;
                }

                if (parts.Length >= 4 && string.Equals(parts[0], "custom", StringComparison.OrdinalIgnoreCase) &&
                    custom.Count < MaxCloudCustomTargets)
                {
                    string name = NormalizeDisplayName(UnescapePart(parts[1]));
                    string target = NormalizeTarget(UnescapePart(parts[2]));
                    if (name.Length == 0 || target.Length == 0 || !customTargets.Add(target))
                    {
                        continue;
                    }

                    custom.Add(new NetworkProbeTargetDefinition
                    {
                        Key = "custom:" + target.ToLowerInvariant(),
                        DisplayName = name,
                        Target = target,
                        Enabled = ParseEnabled(parts[3], true),
                        BuiltIn = false
                    });
                }
            }
        }

        // Invalid legacy/sentinel arrays contain no built-in declarations; treat those as the
        // documented all-enabled default. A real "disable all" choice still contains six rows
        // whose enabled flags are false, so it is preserved.
        if (!sawBuiltIn)
        {
            for (int i = 0; i < enabled.Length; i++)
            {
                enabled[i] = true;
            }
        }

        List<NetworkProbeTargetDefinition> result = new List<NetworkProbeTargetDefinition>();
        for (int i = 0; i < CloudKeys.Length; i++)
        {
            result.Add(new NetworkProbeTargetDefinition
            {
                Key = CloudKeys[i],
                DisplayName = CloudNames[i],
                Target = string.Empty,
                Enabled = enabled[i],
                BuiltIn = true
            });
        }

        result.AddRange(custom);
        return result;
    }

    internal static List<NetworkProbeTargetDefinition> ParseFixedPingTargets(string[] values)
    {
        string[] source = values;
        if (source == null)
        {
            source = DefaultFixedPingTargets;
        }

        List<NetworkProbeTargetDefinition> result = new List<NetworkProbeTargetDefinition>();
        HashSet<string> targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < source.Length && result.Count < MaxFixedPingTargets; i++)
        {
            string[] parts = SplitEntry(source[i]);
            if (parts.Length < 4 || !string.Equals(parts[0], "target", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string name = NormalizeDisplayName(UnescapePart(parts[1]));
            string target = NormalizeTarget(UnescapePart(parts[2]));
            if (name.Length == 0 || target.Length == 0 || !targets.Add(target))
            {
                continue;
            }

            result.Add(new NetworkProbeTargetDefinition
            {
                Key = "ping:" + target.ToLowerInvariant(),
                DisplayName = name,
                Target = target,
                Enabled = ParseEnabled(parts[3], true),
                BuiltIn = false
            });
        }

        return result;
    }

    internal static string SerializeArray(string[] values)
    {
        if (values == null || values.Length == 0)
        {
            return string.Empty;
        }

        string[] encoded = new string[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            encoded[i] = Uri.EscapeDataString(values[i] ?? string.Empty);
        }

        return string.Join(";", encoded);
    }

    internal static string[] DeserializeArray(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new string[0];
        }

        string[] parts = value.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            try
            {
                parts[i] = Uri.UnescapeDataString(parts[i]);
            }
            catch (UriFormatException)
            {
                parts[i] = string.Empty;
            }
        }

        return parts;
    }

    internal static string BuildSignature(string[] values)
    {
        return string.Join("\n", values ?? new string[0]);
    }

    internal static string NormalizeDisplayName(string value)
    {
        string text = (value ?? string.Empty).Trim().Replace("|", "/").Replace(";", ",");
        return text.Length <= 24 ? text : text.Substring(0, 24);
    }

    internal static string NormalizeTarget(string value)
    {
        string text = (value ?? string.Empty).Trim().Trim('[', ']');
        if (text.Length == 0 || text.Length > 255 || text.IndexOfAny(new char[] { '/', '\\', '|', ';', ' ', '\t', '\r', '\n' }) >= 0)
        {
            return string.Empty;
        }

        IPAddress address;
        if (IPAddress.TryParse(text, out address))
        {
            return address.ToString();
        }

        UriHostNameType kind = Uri.CheckHostName(text);
        return kind == UriHostNameType.Dns ? text.ToLowerInvariant() : string.Empty;
    }

    internal static string GetCloudDisplayName(string key)
    {
        int index = IndexOfCloudKey(key);
        return index < 0 ? string.Empty : CloudNames[index];
    }

    private static int IndexOfCloudKey(string key)
    {
        for (int i = 0; i < CloudKeys.Length; i++)
        {
            if (string.Equals(CloudKeys[i], key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string[] SplitEntry(string value)
    {
        return (value ?? string.Empty).Split(new char[] { '|' }, StringSplitOptions.None);
    }

    private static string EscapePart(string value)
    {
        return (value ?? string.Empty).Replace("|", "/").Replace(";", ",");
    }

    private static string UnescapePart(string value)
    {
        return value ?? string.Empty;
    }

    private static bool ParseEnabled(string value, bool fallback)
    {
        if (string.Equals(value, "1", StringComparison.Ordinal) || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(value, "0", StringComparison.Ordinal) || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fallback;
    }

    private static string FormatEnabled(bool enabled)
    {
        return enabled ? "1" : "0";
    }
}
