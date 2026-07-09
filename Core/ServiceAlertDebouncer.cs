using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

internal sealed class ServiceAlertCandidate
{
    public string Key { get; set; }
    public string Name { get; set; }
    public string Reason { get; set; }
    public string State { get; set; }
    public Color Color { get; set; }
    public bool Checking { get; set; }

    public ServiceAlertCandidate Clone()
    {
        return new ServiceAlertCandidate
        {
            Key = this.Key ?? string.Empty,
            Name = this.Name ?? string.Empty,
            Reason = this.Reason ?? string.Empty,
            State = this.State ?? string.Empty,
            Color = this.Color,
            Checking = this.Checking
        };
    }
}

internal sealed class ServiceAlertDebounceState
{
    public string PendingSignature { get; set; }
    public DateTime PendingSinceUtc { get; set; }
    public string ActiveSignature { get; set; }
    public ServiceAlertCandidate ActiveCandidate { get; set; }
}

internal static class ServiceAlertDebouncer
{
    public static List<ServiceAlertCandidate> Apply(
        IDictionary<string, ServiceAlertDebounceState> states,
        IEnumerable<ServiceAlertCandidate> candidates,
        DateTime nowUtc,
        TimeSpan debounceWindow,
        bool bypass)
    {
        List<ServiceAlertCandidate> source = CloneCandidates(candidates);
        if (states == null)
        {
            return source;
        }

        if (source.Count == 0 || bypass)
        {
            states.Clear();
            return source;
        }

        HashSet<string> seenServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<ServiceAlertCandidate> result = new List<ServiceAlertCandidate>();
        for (int i = 0; i < source.Count; i++)
        {
            ServiceAlertCandidate candidate = source[i];
            if (candidate == null)
            {
                continue;
            }

            string serviceKey = GetServiceKey(candidate.Key);
            if (serviceKey.Length == 0)
            {
                serviceKey = "candidate-" + i.ToString(CultureInfo.InvariantCulture);
            }

            seenServices.Add(serviceKey);
            if (IsChecking(candidate))
            {
                result.Add(candidate.Clone());
                continue;
            }

            string signature = BuildSignature(candidate);
            ServiceAlertDebounceState state;
            if (!states.TryGetValue(serviceKey, out state) || state == null)
            {
                state = new ServiceAlertDebounceState();
                states[serviceKey] = state;
            }

            if (string.Equals(state.ActiveSignature, signature, StringComparison.Ordinal))
            {
                state.PendingSignature = string.Empty;
                state.PendingSinceUtc = DateTime.MinValue;
                result.Add(candidate.Clone());
                continue;
            }

            if (!string.Equals(state.PendingSignature, signature, StringComparison.Ordinal))
            {
                state.PendingSignature = signature;
                state.PendingSinceUtc = nowUtc;
            }

            // Cloud and API status checks can transiently fail during adapter/VPN handoff.
            // A new non-checking error only replaces the visible stable alert after the
            // same service/signature survives the configured debounce window.
            if (nowUtc - state.PendingSinceUtc >= debounceWindow)
            {
                state.ActiveSignature = signature;
                state.ActiveCandidate = candidate.Clone();
                state.PendingSignature = string.Empty;
                state.PendingSinceUtc = DateTime.MinValue;
                result.Add(candidate.Clone());
            }
            else if (state.ActiveCandidate != null)
            {
                result.Add(state.ActiveCandidate.Clone());
            }
        }

        List<string> stale = new List<string>();
        foreach (string key in states.Keys)
        {
            if (!seenServices.Contains(key))
            {
                stale.Add(key);
            }
        }

        for (int i = 0; i < stale.Count; i++)
        {
            states.Remove(stale[i]);
        }

        return result;
    }

    public static void RunSelfTest()
    {
        Dictionary<string, ServiceAlertDebounceState> states =
            new Dictionary<string, ServiceAlertDebounceState>(StringComparer.OrdinalIgnoreCase);
        TimeSpan window = TimeSpan.FromSeconds(10.0);
        DateTime start = new DateTime(2026, 7, 9, 0, 0, 0, DateTimeKind.Utc);

        List<ServiceAlertCandidate> checking = new List<ServiceAlertCandidate>
        {
            Build("openai:checking", "OpenAI", "检测中")
        };
        if (Apply(states, checking, start, window, false).Count != 1)
        {
            throw new InvalidOperationException("Service alert debounce self-test: checking alert was not immediate.");
        }

        List<ServiceAlertCandidate> error = new List<ServiceAlertCandidate>
        {
            Build("openai:Unavailable", "OpenAI", "服务异常")
        };
        if (Apply(states, error, start, window, false).Count != 0)
        {
            throw new InvalidOperationException("Service alert debounce self-test: new error bypassed debounce.");
        }

        if (Apply(states, error, start.AddSeconds(9), window, false).Count != 0)
        {
            throw new InvalidOperationException("Service alert debounce self-test: early error became visible.");
        }

        List<ServiceAlertCandidate> stable = Apply(states, error, start.AddSeconds(10), window, false);
        if (stable.Count != 1 || !string.Equals(stable[0].Key, "openai:Unavailable", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Service alert debounce self-test: stable error was not shown.");
        }

        List<ServiceAlertCandidate> changed = new List<ServiceAlertCandidate>
        {
            Build("openai:Unreachable", "OpenAI", "无法连接")
        };
        List<ServiceAlertCandidate> held = Apply(states, changed, start.AddSeconds(12), window, false);
        if (held.Count != 1 || !string.Equals(held[0].Key, "openai:Unavailable", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Service alert debounce self-test: old stable error was not retained.");
        }

        if (Apply(states, new List<ServiceAlertCandidate>(), start.AddSeconds(13), window, false).Count != 0 ||
            states.Count != 0)
        {
            throw new InvalidOperationException("Service alert debounce self-test: normal recovery did not clear state.");
        }

        List<ServiceAlertCandidate> bypassed = Apply(states, changed, start.AddSeconds(14), window, true);
        if (bypassed.Count != 1 || states.Count != 0)
        {
            throw new InvalidOperationException("Service alert debounce self-test: bypass did not return source and clear state.");
        }
    }

    private static ServiceAlertCandidate Build(string key, string name, string reason)
    {
        return new ServiceAlertCandidate
        {
            Key = key,
            Name = name,
            Reason = reason,
            State = string.Empty,
            Color = Color.White,
            Checking = (key ?? string.Empty).IndexOf(":checking", StringComparison.OrdinalIgnoreCase) >= 0
        };
    }

    private static List<ServiceAlertCandidate> CloneCandidates(IEnumerable<ServiceAlertCandidate> candidates)
    {
        List<ServiceAlertCandidate> clone = new List<ServiceAlertCandidate>();
        if (candidates == null)
        {
            return clone;
        }

        foreach (ServiceAlertCandidate candidate in candidates)
        {
            if (candidate != null)
            {
                clone.Add(candidate.Clone());
            }
        }

        return clone;
    }

    private static bool IsChecking(ServiceAlertCandidate candidate)
    {
        return candidate != null &&
            (candidate.Checking ||
                (candidate.Key ?? string.Empty).IndexOf(":checking", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string GetServiceKey(string key)
    {
        string raw = (key ?? string.Empty).Trim();
        int split = raw.IndexOf(':');
        return split <= 0 ? raw : raw.Substring(0, split);
    }

    private static string BuildSignature(ServiceAlertCandidate candidate)
    {
        if (candidate == null)
        {
            return string.Empty;
        }

        return (candidate.Key ?? string.Empty) +
            "|" +
            (candidate.Name ?? string.Empty) +
            "|" +
            (candidate.Reason ?? string.Empty) +
            "|" +
            (candidate.State ?? string.Empty) +
            "|" +
            candidate.Color.ToArgb().ToString(CultureInfo.InvariantCulture);
    }
}
