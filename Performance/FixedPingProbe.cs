using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

// Compact fixed-target ICMP sampling shown directly below PathPing. Poll is driven by the
// existing network-window refresh path; no timer is created here. Disabled rows are excluded
// before a background task starts, which makes the settings checkboxes a traffic boundary rather
// than a presentation-only filter.
internal sealed class FixedPingProbeReader : IDisposable
{
    private const int TimeoutMs = 1000;
    private const int SlowThresholdMs = 350;
    private readonly object sync = new object();
    private FixedPingSnapshot snapshot = new FixedPingSnapshot();
    private DateTime lastRoundUtc = DateTime.MinValue;
    private string configurationSignature = string.Empty;
    private bool requestRunning;
    private bool disposed;

    public void RequestRefresh()
    {
        lock (this.sync)
        {
            if (!this.disposed)
            {
                this.lastRoundUtc = DateTime.MinValue;
            }
        }
    }

    public FixedPingSnapshot Poll(WidgetSettings settings, bool samplingActive, bool connected)
    {
        string[] configured = settings == null
            ? NetworkProbeTargetSettings.DefaultFixedPingTargets
            : settings.FixedPingTargets;
        List<NetworkProbeTargetDefinition> definitions = NetworkProbeTargetSettings.ParseFixedPingTargets(configured);
        List<NetworkProbeTargetDefinition> enabled = new List<NetworkProbeTargetDefinition>();
        for (int i = 0; i < definitions.Count; i++)
        {
            if (definitions[i].Enabled)
            {
                enabled.Add(definitions[i]);
            }
        }

        string signature = NetworkProbeTargetSettings.BuildSignature(
            NetworkProbeTargetSettings.NormalizeFixedPingTargets(configured));
        DateTime nowUtc = DateTime.UtcNow;
        bool shouldStart = false;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return this.snapshot.Clone();
            }

            if (!string.Equals(this.configurationSignature, signature, StringComparison.Ordinal))
            {
                this.configurationSignature = signature;
                this.lastRoundUtc = DateTime.MinValue;
                this.snapshot = BuildInitialSnapshot(enabled, connected ? FixedPingStatus.Unknown : FixedPingStatus.Down, connected ? "等待检测" : "网络不可用");
            }

            if (!connected)
            {
                this.snapshot = BuildInitialSnapshot(enabled, FixedPingStatus.Down, "网络不可用");
                this.lastRoundUtc = DateTime.MinValue;
                return this.snapshot.Clone();
            }

            if (!samplingActive || enabled.Count == 0)
            {
                return this.snapshot.Clone();
            }

            int intervalMs = PathPingProbeReader.GetRoundIntervalMs(settings);
            bool due = this.lastRoundUtc == DateTime.MinValue ||
                (nowUtc - this.lastRoundUtc).TotalMilliseconds >= intervalMs;
            if (!this.requestRunning && due)
            {
                this.requestRunning = true;
                this.lastRoundUtc = nowUtc;
                this.snapshot = BuildInitialSnapshot(enabled, FixedPingStatus.Checking, "检测中");
                this.snapshot.Running = true;
                shouldStart = true;
            }
        }

        if (shouldStart)
        {
            Task.Run(delegate
            {
                try
                {
                    RunRound(enabled, signature);
                }
                catch (Exception ex)
                {
                    Program.LogInfo("Fixed ping round failed: " + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    lock (this.sync)
                    {
                        this.requestRunning = false;
                        this.snapshot.Running = false;
                    }
                }
            });
        }

        lock (this.sync)
        {
            return this.snapshot.Clone();
        }
    }

    private void RunRound(List<NetworkProbeTargetDefinition> definitions, string signature)
    {
        Task<FixedPingTargetSnapshot>[] tasks = new Task<FixedPingTargetSnapshot>[definitions.Count];
        for (int i = 0; i < definitions.Count; i++)
        {
            NetworkProbeTargetDefinition definition = definitions[i];
            tasks[i] = Task.Run(delegate { return Probe(definition); });
        }

        Task.WaitAll(tasks);
        FixedPingTargetSnapshot[] rows = new FixedPingTargetSnapshot[tasks.Length];
        for (int i = 0; i < tasks.Length; i++)
        {
            rows[i] = tasks[i].Result;
        }

        lock (this.sync)
        {
            if (this.disposed || !string.Equals(this.configurationSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            this.snapshot = new FixedPingSnapshot
            {
                Running = false,
                CheckedAtLocal = DateTime.Now,
                CheckedAtKnown = true,
                Targets = rows
            };
        }
    }

    private static FixedPingTargetSnapshot Probe(NetworkProbeTargetDefinition definition)
    {
        FixedPingTargetSnapshot row = new FixedPingTargetSnapshot
        {
            Key = definition.Key,
            DisplayName = definition.DisplayName,
            Target = definition.Target,
            Status = FixedPingStatus.Down,
            Reason = "超时"
        };

        try
        {
            using (Ping ping = new Ping())
            {
                PingReply reply = ping.Send(definition.Target, TimeoutMs);
                if (reply != null && reply.Status == IPStatus.Success)
                {
                    row.LatencyMs = (int)Math.Min(int.MaxValue, Math.Max(0L, reply.RoundtripTime));
                    row.Status = row.LatencyMs >= SlowThresholdMs ? FixedPingStatus.Slow : FixedPingStatus.Normal;
                    row.Reason = row.LatencyMs.ToString(CultureInfo.InvariantCulture) + "ms";
                }
                else if (reply != null)
                {
                    row.Reason = reply.Status.ToString();
                }
            }
        }
        catch (PingException ex)
        {
            row.Reason = ex.InnerException == null ? "Ping失败" : ex.InnerException.GetType().Name;
        }
        catch (SocketException ex)
        {
            row.Reason = "Socket " + ex.SocketErrorCode.ToString();
        }

        return row;
    }

    private static FixedPingSnapshot BuildInitialSnapshot(
        List<NetworkProbeTargetDefinition> definitions,
        FixedPingStatus status,
        string reason)
    {
        FixedPingTargetSnapshot[] rows = new FixedPingTargetSnapshot[definitions.Count];
        for (int i = 0; i < definitions.Count; i++)
        {
            NetworkProbeTargetDefinition definition = definitions[i];
            rows[i] = new FixedPingTargetSnapshot
            {
                Key = definition.Key,
                DisplayName = definition.DisplayName,
                Target = definition.Target,
                Status = status,
                Reason = reason ?? string.Empty
            };
        }

        return new FixedPingSnapshot { Targets = rows };
    }

    internal static void RunSelfTest()
    {
        List<NetworkProbeTargetDefinition> defaults = NetworkProbeTargetSettings.ParseFixedPingTargets(null);
        if (defaults.Count != 3 ||
            !string.Equals(defaults[0].DisplayName, "Google", StringComparison.Ordinal) ||
            !string.Equals(defaults[1].DisplayName, "百度", StringComparison.Ordinal) ||
            !string.Equals(defaults[2].DisplayName, "Yahoo", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixed ping self-test: default target order mismatch.");
        }

        string[] normalized = NetworkProbeTargetSettings.NormalizeFixedPingTargets(new string[]
        {
            "target|自定义|9.9.9.9|1",
            "target|重复|9.9.9.9|1",
            "target|无效|https://example.com|1"
        });
        if (normalized.Length != 1 || normalized[0].IndexOf("9.9.9.9", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Fixed ping self-test: custom target normalization failed.");
        }

        Console.WriteLine("Fixed ping probe: PASS defaults custom-normalization disabled-traffic-boundary");
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            this.disposed = true;
        }
    }
}
