using System;
using System.Collections.Generic;
using System.Diagnostics;

internal sealed class ForegroundFpsReader : IDisposable
{
    private const int CounterDiscoveryIntervalMs = 60000;
    private const int ForegroundProcessRediscoveryCooldownMs = 30000;
    private readonly List<FpsCounterCandidate> candidates = new List<FpsCounterCandidate>();
    private DateTime lastDiscoveryUtc;
    private DateTime lastForegroundRediscoveryUtc;
    private int lastForegroundProcessId = -1;
    private string lastForegroundProcessName = string.Empty;
    private bool discoveryFailureLogged;
    private bool disposed;

    public int? ReadForegroundFps()
    {
        if (this.disposed)
        {
            return null;
        }

        IntPtr foreground = NativeMethods.GetForegroundWindowHandle();
        int processId;
        if (!NativeMethods.TryGetWindowProcessId(foreground, out processId))
        {
            return null;
        }

        string processName = GetProcessName(processId);
        if (string.IsNullOrEmpty(processName))
        {
            return null;
        }

        DateTime now = DateTime.UtcNow;
        bool foregroundChanged = processId != this.lastForegroundProcessId ||
            !string.Equals(processName, this.lastForegroundProcessName, StringComparison.Ordinal);
        this.lastForegroundProcessId = processId;
        this.lastForegroundProcessName = processName;

        bool discovered = EnsureCandidates(now, foregroundChanged);
        int? sample = TryReadBestSample(processId, processName);
        if (sample.HasValue)
        {
            return sample;
        }

        if (!discovered && foregroundChanged && CanRediscoverForForegroundChange(now))
        {
            this.lastForegroundRediscoveryUtc = now;
            DiscoverCandidates(now);
            sample = TryReadBestSample(processId, processName);
        }

        return sample;
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        ClearCandidates();
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                return process.ProcessName;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool EnsureCandidates(DateTime now, bool foregroundChanged)
    {
        if (this.lastDiscoveryUtc == DateTime.MinValue)
        {
            DiscoverCandidates(now);
            return true;
        }

        if ((now - this.lastDiscoveryUtc).TotalMilliseconds >= CounterDiscoveryIntervalMs)
        {
            DiscoverCandidates(now);
            return true;
        }

        if (foregroundChanged && CanRediscoverForForegroundChange(now))
        {
            this.lastForegroundRediscoveryUtc = now;
            DiscoverCandidates(now);
            return true;
        }

        return false;
    }

    private bool CanRediscoverForForegroundChange(DateTime now)
    {
        return this.lastForegroundRediscoveryUtc == DateTime.MinValue ||
            (now - this.lastForegroundRediscoveryUtc).TotalMilliseconds >= ForegroundProcessRediscoveryCooldownMs;
    }

    private void DiscoverCandidates(DateTime now)
    {
        this.lastDiscoveryUtc = now;
        List<FpsCounterCandidate> nextCandidates = new List<FpsCounterCandidate>();
        try
        {
            PerformanceCounterCategory[] categories = PerformanceCounterCategory.GetCategories();
            for (int i = 0; i < categories.Length; i++)
            {
                PerformanceCounterCategory category = categories[i];
                string categoryName = category.CategoryName;
                if (!IsPotentialFpsCategory(categoryName))
                {
                    continue;
                }

                AddCategoryCounters(nextCandidates, category, categoryName);
            }

            ClearCandidates();
            this.candidates.AddRange(nextCandidates);
            nextCandidates.Clear();
        }
        catch (Exception ex)
        {
            DisposeCandidates(nextCandidates);
            if (!this.discoveryFailureLogged)
            {
                this.discoveryFailureLogged = true;
                Program.LogInfo("Foreground FPS counter discovery failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    private static void AddCategoryCounters(List<FpsCounterCandidate> target, PerformanceCounterCategory category, string categoryName)
    {
        try
        {
            if (category.CategoryType == PerformanceCounterCategoryType.MultiInstance)
            {
                string[] instances = category.GetInstanceNames();
                for (int i = 0; i < instances.Length; i++)
                {
                    string instanceName = instances[i];
                    if (ShouldSkipInstance(instanceName))
                    {
                        continue;
                    }

                    AddCounters(target, category, categoryName, instanceName);
                }
            }
            else
            {
                AddCounters(target, category, categoryName, string.Empty);
            }
        }
        catch
        {
        }
    }

    private static void AddCounters(List<FpsCounterCandidate> target, PerformanceCounterCategory category, string categoryName, string instanceName)
    {
        try
        {
            PerformanceCounter[] counters = string.IsNullOrEmpty(instanceName)
                ? category.GetCounters()
                : category.GetCounters(instanceName);
            for (int i = 0; i < counters.Length; i++)
            {
                PerformanceCounter counter = counters[i];
                try
                {
                    if (!IsPotentialFpsCounter(categoryName, counter.CounterName))
                    {
                        counter.Dispose();
                        continue;
                    }

                    FpsCounterCandidate candidate = new FpsCounterCandidate(categoryName, counter.CounterName, instanceName, counter);
                    candidate.Prime();
                    target.Add(candidate);
                }
                catch
                {
                    counter.Dispose();
                }
            }
        }
        catch
        {
        }
    }

    private int? TryReadBestSample(int processId, string processName)
    {
        int bestScore = 0;
        int? bestValue = null;
        for (int i = 0; i < this.candidates.Count; i++)
        {
            FpsCounterCandidate candidate = this.candidates[i];
            int score = candidate.GetMatchScore(processId, processName);
            if (score <= 0 || score < bestScore)
            {
                continue;
            }

            int? sample = candidate.TryRead();
            if (!sample.HasValue)
            {
                continue;
            }

            bestScore = score;
            bestValue = sample.Value;
        }

        return bestValue;
    }

    private static bool IsPotentialFpsCategory(string value)
    {
        string text = Normalize(value);
        return ContainsAny(
            text,
            "xbox",
            "game",
            "bar",
            "present",
            "frame",
            "fps",
            "directx",
            "dxgi",
            "gpu",
            "游戏",
            "帧",
            "图形");
    }

    private static bool IsPotentialFpsCounter(string categoryName, string counterName)
    {
        string text = Normalize(categoryName + " " + counterName);
        return ContainsAny(
            text,
            "fps",
            "frame rate",
            "framerate",
            "frames/sec",
            "frames / sec",
            "frames per second",
            "frames rendered/sec",
            "present rate",
            "帧率",
            "帧/秒",
            "每秒帧");
    }

    private static bool ShouldSkipInstance(string instanceName)
    {
        return string.IsNullOrEmpty(instanceName) ||
            string.Equals(instanceName, "_Total", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(instanceName, "Idle", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        for (int i = 0; i < needles.Length; i++)
        {
            if (text.IndexOf(Normalize(needles[i]), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value)
    {
        return string.IsNullOrEmpty(value) ? string.Empty : value.ToLowerInvariant();
    }

    private void ClearCandidates()
    {
        DisposeCandidates(this.candidates);
        this.candidates.Clear();
    }

    private static void DisposeCandidates(List<FpsCounterCandidate> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            values[i].Dispose();
        }
    }

    private sealed class FpsCounterCandidate : IDisposable
    {
        private readonly string instanceName;
        private readonly string normalizedInstanceName;
        private readonly PerformanceCounter counter;

        public FpsCounterCandidate(string categoryName, string counterName, string instanceName, PerformanceCounter counter)
        {
            this.instanceName = instanceName ?? string.Empty;
            this.normalizedInstanceName = Normalize(this.instanceName);
            this.counter = counter;
        }

        public void Prime()
        {
            try
            {
                this.counter.NextValue();
            }
            catch
            {
            }
        }

        public int GetMatchScore(int processId, string processName)
        {
            if (string.IsNullOrEmpty(this.instanceName))
            {
                return 10;
            }

            string processIdText = processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ContainsNumericToken(this.normalizedInstanceName, processIdText))
            {
                return 100;
            }

            string normalizedProcessName = Normalize(processName);
            if (string.Equals(this.normalizedInstanceName, normalizedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return 85;
            }

            if (!string.IsNullOrEmpty(normalizedProcessName) &&
                this.normalizedInstanceName.IndexOf(normalizedProcessName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 70;
            }

            return 0;
        }

        public int? TryRead()
        {
            try
            {
                float value = this.counter.NextValue();
                if (float.IsNaN(value) || float.IsInfinity(value) || value < 0.0f)
                {
                    return null;
                }

                return Math.Max(0, Math.Min(999, (int)Math.Round(value)));
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            this.counter.Dispose();
        }

        private static bool ContainsNumericToken(string text, string token)
        {
            int index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                bool leftOk = index == 0 || !char.IsDigit(text[index - 1]);
                int right = index + token.Length;
                bool rightOk = right >= text.Length || !char.IsDigit(text[right]);
                if (leftOk && rightOk)
                {
                    return true;
                }

                index = text.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}
