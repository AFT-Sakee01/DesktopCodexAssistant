using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

internal static class TimingStats
{
    private const int RollingWindowHours = 12;
    private const int SummaryIntervalMinutes = 15;
    private const int MaxSamplesPerMetric = 200000;
    private const long RollingWindowTicks = TimeSpan.TicksPerHour * RollingWindowHours;
    private static readonly object SyncRoot = new object();
    private static readonly Dictionary<string, RollingMetric> Metrics =
        new Dictionary<string, RollingMetric>(StringComparer.Ordinal);
    private static DateTime lastSummaryUtc = DateTime.MinValue;

    public static long StartTimestamp()
    {
        return Stopwatch.GetTimestamp();
    }

    public static void RecordElapsed(string name, long startTimestamp)
    {
        if (string.IsNullOrEmpty(name) || startTimestamp <= 0)
        {
            return;
        }

        long elapsedTicks = Stopwatch.GetTimestamp() - startTimestamp;
        if (elapsedTicks < 0)
        {
            return;
        }

        double durationMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
        Record(name, durationMs, DateTime.UtcNow);
    }

    public static void TryLogSummary(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            nowUtc = nowUtc.ToUniversalTime();
        }

        string summary;
        lock (SyncRoot)
        {
            if (lastSummaryUtc != DateTime.MinValue &&
                (nowUtc - lastSummaryUtc).TotalMinutes < SummaryIntervalMinutes)
            {
                return;
            }

            lastSummaryUtc = nowUtc;
            summary = BuildSummaryLocked(nowUtc);
        }

        if (!string.IsNullOrEmpty(summary))
        {
            Program.LogInfo(summary);
        }
    }

    private static void Record(string name, double durationMs, DateTime timestampUtc)
    {
        if (timestampUtc.Kind != DateTimeKind.Utc)
        {
            timestampUtc = timestampUtc.ToUniversalTime();
        }

        lock (SyncRoot)
        {
            RollingMetric metric;
            if (!Metrics.TryGetValue(name, out metric))
            {
                metric = new RollingMetric();
                Metrics.Add(name, metric);
            }

            metric.Add(timestampUtc, durationMs);
            metric.Prune(timestampUtc.AddTicks(-RollingWindowTicks), MaxSamplesPerMetric);
        }
    }

    private static string BuildSummaryLocked(DateTime nowUtc)
    {
        DateTime cutoffUtc = nowUtc.AddTicks(-RollingWindowTicks);
        List<string> names = new List<string>(Metrics.Keys);
        names.Sort(StringComparer.Ordinal);

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < names.Count; i++)
        {
            RollingMetric metric = Metrics[names[i]];
            metric.Prune(cutoffUtc, MaxSamplesPerMetric);
            if (metric.Count == 0)
            {
                continue;
            }

            MetricSummary summary = metric.CreateSummary();
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(names[i]);
            builder.Append(" n=");
            builder.Append(summary.Count.ToString(CultureInfo.InvariantCulture));
            builder.Append(" avg=");
            builder.Append(summary.AverageMs.ToString("0.00", CultureInfo.InvariantCulture));
            builder.Append("ms p95=");
            builder.Append(summary.P95Ms.ToString("0.00", CultureInfo.InvariantCulture));
            builder.Append("ms p99=");
            builder.Append(summary.P99Ms.ToString("0.00", CultureInfo.InvariantCulture));
            builder.Append("ms max=");
            builder.Append(summary.MaxMs.ToString("0.00", CultureInfo.InvariantCulture));
            builder.Append("ms");
        }

        if (builder.Length == 0)
        {
            return string.Empty;
        }

        return "TimingStats12h " + builder.ToString();
    }

    private struct TimingSample
    {
        public DateTime TimestampUtc;
        public double DurationMs;
    }

    private struct MetricSummary
    {
        public int Count;
        public double AverageMs;
        public double P95Ms;
        public double P99Ms;
        public double MaxMs;
    }

    private sealed class RollingMetric
    {
        private readonly Queue<TimingSample> samples = new Queue<TimingSample>();
        private double sumMs;

        public int Count
        {
            get { return this.samples.Count; }
        }

        public void Add(DateTime timestampUtc, double durationMs)
        {
            if (durationMs < 0.0)
            {
                durationMs = 0.0;
            }

            this.samples.Enqueue(new TimingSample
            {
                TimestampUtc = timestampUtc,
                DurationMs = durationMs
            });
            this.sumMs += durationMs;
        }

        public void Prune(DateTime cutoffUtc, int maxSamples)
        {
            while (this.samples.Count > 0 &&
                (this.samples.Peek().TimestampUtc < cutoffUtc ||
                 this.samples.Count > maxSamples))
            {
                TimingSample sample = this.samples.Dequeue();
                this.sumMs -= sample.DurationMs;
            }

            if (this.samples.Count == 0)
            {
                this.sumMs = 0.0;
            }
        }

        public MetricSummary CreateSummary()
        {
            List<double> durations = new List<double>(this.samples.Count);
            double maxMs = 0.0;
            foreach (TimingSample sample in this.samples)
            {
                durations.Add(sample.DurationMs);
                if (sample.DurationMs > maxMs)
                {
                    maxMs = sample.DurationMs;
                }
            }

            durations.Sort();
            int p95Index = durations.Count == 0
                ? 0
                : Math.Max(0, (int)Math.Ceiling(durations.Count * 0.95) - 1);
            int p99Index = durations.Count == 0
                ? 0
                : Math.Max(0, (int)Math.Ceiling(durations.Count * 0.99) - 1);

            MetricSummary summary = new MetricSummary();
            summary.Count = durations.Count;
            summary.AverageMs = durations.Count == 0 ? 0.0 : this.sumMs / durations.Count;
            summary.P95Ms = durations.Count == 0 ? 0.0 : durations[p95Index];
            summary.P99Ms = durations.Count == 0 ? 0.0 : durations[p99Index];
            summary.MaxMs = maxMs;
            return summary;
        }
    }

    internal static void RunSelfTest()
    {
        const string MetricName = "selftest.percentiles";
        DateTime nowUtc = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
        lock (SyncRoot)
        {
            Metrics.Remove(MetricName);
        }

        try
        {
            for (int durationMs = 1; durationMs <= 100; durationMs++)
            {
                Record(MetricName, durationMs, nowUtc);
            }

            MetricSummary summary;
            lock (SyncRoot)
            {
                summary = Metrics[MetricName].CreateSummary();
            }

            if (summary.Count != 100 ||
                Math.Abs(summary.P95Ms - 95.0) > 0.001 ||
                Math.Abs(summary.P99Ms - 99.0) > 0.001 ||
                Math.Abs(summary.MaxMs - 100.0) > 0.001)
            {
                throw new InvalidOperationException("TimingStats percentile self-test failed.");
            }
        }
        finally
        {
            lock (SyncRoot)
            {
                Metrics.Remove(MetricName);
            }
        }
    }
}
