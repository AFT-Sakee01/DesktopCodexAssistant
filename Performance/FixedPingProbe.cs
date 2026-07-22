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
    // Protected by sync. Completed rows are reusable only while their network identity matches.
    private FixedPingSnapshot lastCompletedSnapshot = new FixedPingSnapshot();
    private DateTime lastRoundUtc = DateTime.MinValue;
    private string configurationSignature = string.Empty;
    private string currentRequestIdentitySignature = string.Empty;
    private long requestEpoch;
    private bool hasCompletedSnapshot;
    private bool wasConnected;
    private bool requestRunning;
    private bool disposed;

    private enum RoundStartReason
    {
        Scheduled,
        ConfigurationChanged,
        Reconnected
    }

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

    public FixedPingSnapshot Poll(
        WidgetSettings settings,
        long networkGeneration,
        string interfaceId,
        bool samplingActive,
        bool connected)
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
        string normalizedInterfaceId = interfaceId ?? string.Empty;
        string requestIdentitySignature = BuildRequestIdentitySignature(
            networkGeneration,
            normalizedInterfaceId,
            signature,
            connected);
        DateTime nowUtc = DateTime.UtcNow;
        bool shouldStart = false;
        long requestEpochAtStart = 0;
        lock (this.sync)
        {
            if (this.disposed)
            {
                return this.snapshot.Clone();
            }

            bool reconnected = connected && !this.wasConnected;
            bool configurationChanged = !string.Equals(this.configurationSignature, signature, StringComparison.Ordinal);
            bool identityChanged = !string.Equals(this.currentRequestIdentitySignature, requestIdentitySignature, StringComparison.Ordinal);
            if (identityChanged)
            {
                this.currentRequestIdentitySignature = requestIdentitySignature;
                this.requestEpoch++;
                this.requestRunning = false;
                this.lastRoundUtc = DateTime.MinValue;
                this.hasCompletedSnapshot = false;
                this.lastCompletedSnapshot = new FixedPingSnapshot();
                this.snapshot = BuildInitialSnapshot(
                    enabled,
                    connected ? FixedPingStatus.Unknown : FixedPingStatus.Down,
                    connected ? "等待检测" : "网络不可用");
                StampNetworkIdentity(this.snapshot, networkGeneration, normalizedInterfaceId, signature);
            }

            if (!string.Equals(this.configurationSignature, signature, StringComparison.Ordinal))
            {
                this.configurationSignature = signature;
            }

            this.wasConnected = connected;
            if (!connected)
            {
                this.snapshot = BuildInitialSnapshot(enabled, FixedPingStatus.Down, "网络不可用");
                StampNetworkIdentity(this.snapshot, networkGeneration, normalizedInterfaceId, signature);
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
                RoundStartReason startReason = configurationChanged
                    ? RoundStartReason.ConfigurationChanged
                    : (reconnected ? RoundStartReason.Reconnected : RoundStartReason.Scheduled);
                this.snapshot = BuildRoundStartSnapshot(
                    this.lastCompletedSnapshot,
                    enabled,
                    this.hasCompletedSnapshot && SnapshotMatchesIdentity(
                        this.lastCompletedSnapshot,
                        networkGeneration,
                        normalizedInterfaceId,
                        signature),
                    startReason);
                StampNetworkIdentity(this.snapshot, networkGeneration, normalizedInterfaceId, signature);
                requestEpochAtStart = this.requestEpoch;
                shouldStart = true;
            }
        }

        if (shouldStart)
        {
            Task.Run(delegate
            {
                try
                {
                    RunRound(
                        enabled,
                        signature,
                        networkGeneration,
                        normalizedInterfaceId,
                        requestIdentitySignature,
                        requestEpochAtStart);
                }
                catch (Exception ex)
                {
                    Program.LogInfo("Fixed ping round failed: " + ex.GetType().Name + " " + ex.Message);
                }
                finally
                {
                    lock (this.sync)
                    {
                        if (IsRequestIdentityCurrent(
                            this.requestEpoch,
                            this.currentRequestIdentitySignature,
                            requestEpochAtStart,
                            requestIdentitySignature))
                        {
                            this.requestRunning = false;
                            this.snapshot.Running = false;
                        }
                    }
                }
            });
        }

        lock (this.sync)
        {
            return this.snapshot.Clone();
        }
    }

    private void RunRound(
        List<NetworkProbeTargetDefinition> definitions,
        string signature,
        long networkGeneration,
        string interfaceId,
        string requestIdentitySignature,
        long requestEpochAtStart)
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
            if (this.disposed ||
                !string.Equals(this.configurationSignature, signature, StringComparison.Ordinal) ||
                !IsRequestIdentityCurrent(
                    this.requestEpoch,
                    this.currentRequestIdentitySignature,
                    requestEpochAtStart,
                    requestIdentitySignature))
            {
                return;
            }

            FixedPingSnapshot completed = new FixedPingSnapshot
            {
                Running = false,
                CheckedAtLocal = DateTime.Now,
                CheckedAtKnown = true,
                Targets = rows
            };
            StampNetworkIdentity(completed, networkGeneration, interfaceId, signature);
            this.lastCompletedSnapshot = completed.Clone();
            this.hasCompletedSnapshot = true;
            this.snapshot = completed;
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

    // A completed round remains the visible truth while the next round is in flight. Configuration
    // changes are the hard boundary because old rows may no longer describe the enabled targets.
    private static FixedPingSnapshot BuildRoundStartSnapshot(
        FixedPingSnapshot previousCompleted,
        List<NetworkProbeTargetDefinition> definitions,
        bool hasCompletedSnapshot,
        RoundStartReason reason)
    {
        if (hasCompletedSnapshot &&
            reason != RoundStartReason.ConfigurationChanged &&
            SnapshotMatchesDefinitions(previousCompleted, definitions))
        {
            FixedPingSnapshot preserved = previousCompleted.Clone();
            preserved.Running = true;
            return preserved;
        }

        FixedPingSnapshot checking = BuildInitialSnapshot(definitions, FixedPingStatus.Checking, "检测中");
        checking.Running = true;
        return checking;
    }

    private static bool SnapshotMatchesDefinitions(
        FixedPingSnapshot candidate,
        List<NetworkProbeTargetDefinition> definitions)
    {
        if (candidate == null || candidate.Targets == null || definitions == null ||
            candidate.Targets.Length != definitions.Count)
        {
            return false;
        }

        for (int i = 0; i < definitions.Count; i++)
        {
            FixedPingTargetSnapshot row = candidate.Targets[i];
            NetworkProbeTargetDefinition definition = definitions[i];
            if (row == null ||
                !string.Equals(row.Key, definition.Key, StringComparison.Ordinal) ||
                !string.Equals(row.DisplayName, definition.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(row.Target, definition.Target, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static void StampNetworkIdentity(
        FixedPingSnapshot value,
        long networkGeneration,
        string interfaceId,
        string targetSignature)
    {
        if (value == null)
        {
            return;
        }

        value.NetworkGeneration = networkGeneration;
        value.InterfaceId = interfaceId ?? string.Empty;
        value.TargetSignature = targetSignature ?? string.Empty;
    }

    private static bool SnapshotMatchesIdentity(
        FixedPingSnapshot value,
        long networkGeneration,
        string interfaceId,
        string targetSignature)
    {
        return value != null &&
            value.NetworkGeneration == networkGeneration &&
            string.Equals(value.InterfaceId, interfaceId ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(value.TargetSignature, targetSignature ?? string.Empty, StringComparison.Ordinal);
    }

    private static string BuildRequestIdentitySignature(
        long networkGeneration,
        string interfaceId,
        string targetSignature,
        bool connected)
    {
        return networkGeneration.ToString(CultureInfo.InvariantCulture) + "|" +
            (interfaceId ?? string.Empty).Trim().ToUpperInvariant() + "|" +
            (targetSignature ?? string.Empty) + "|" +
            (connected ? "online" : "offline");
    }

    private static bool IsRequestIdentityCurrent(
        long currentEpoch,
        string currentIdentitySignature,
        long requestEpoch,
        string requestIdentitySignature)
    {
        return currentEpoch == requestEpoch &&
            string.Equals(currentIdentitySignature, requestIdentitySignature, StringComparison.Ordinal);
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

        FixedPingSnapshot completed = new FixedPingSnapshot
        {
            CheckedAtKnown = true,
            CheckedAtLocal = new DateTime(2026, 7, 19, 12, 0, 0),
            Targets = new FixedPingTargetSnapshot[]
            {
                new FixedPingTargetSnapshot { Key = defaults[0].Key, DisplayName = defaults[0].DisplayName, Target = defaults[0].Target, Status = FixedPingStatus.Normal, LatencyMs = 18, Reason = "18ms" },
                new FixedPingTargetSnapshot { Key = defaults[1].Key, DisplayName = defaults[1].DisplayName, Target = defaults[1].Target, Status = FixedPingStatus.Slow, LatencyMs = 420, Reason = "420ms" },
                new FixedPingTargetSnapshot { Key = defaults[2].Key, DisplayName = defaults[2].DisplayName, Target = defaults[2].Target, Status = FixedPingStatus.Normal, LatencyMs = 72, Reason = "72ms" }
            }
        };
        string defaultTargetSignature = NetworkProbeTargetSettings.BuildSignature(
            NetworkProbeTargetSettings.NormalizeFixedPingTargets(null));
        StampNetworkIdentity(completed, 5, "if-a", defaultTargetSignature);
        FixedPingSnapshot scheduled = BuildRoundStartSnapshot(completed, defaults, true, RoundStartReason.Scheduled);
        if (!scheduled.Running || !scheduled.CheckedAtKnown ||
            scheduled.Targets[0].Status != FixedPingStatus.Normal || scheduled.Targets[0].LatencyMs != 18 || scheduled.Targets[0].Reason != "18ms" ||
            scheduled.Targets[1].Status != FixedPingStatus.Slow || scheduled.Targets[1].LatencyMs != 420 || scheduled.Targets[1].Reason != "420ms" ||
            scheduled.Targets[2].Status != FixedPingStatus.Normal || scheduled.Targets[2].LatencyMs != 72 ||
            scheduled.Targets[2].Reason != "72ms")
        {
            throw new InvalidOperationException("Fixed ping self-test: scheduled round must preserve the last completed rows.");
        }

        scheduled.Targets[0].Reason = "mutated";
        if (!string.Equals(completed.Targets[0].Reason, "18ms", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixed ping self-test: round-start snapshot must be a deep clone.");
        }

        FixedPingSnapshot firstRound = BuildRoundStartSnapshot(completed, defaults, false, RoundStartReason.Scheduled);
        FixedPingSnapshot reconnectedWithoutHistory = BuildRoundStartSnapshot(completed, defaults, false, RoundStartReason.Reconnected);
        FixedPingSnapshot changedConfiguration = BuildRoundStartSnapshot(completed, defaults, true, RoundStartReason.ConfigurationChanged);
        if (!AllTargetsHaveStatus(firstRound, FixedPingStatus.Checking) ||
            !AllTargetsHaveStatus(reconnectedWithoutHistory, FixedPingStatus.Checking) ||
            !AllTargetsHaveStatus(changedConfiguration, FixedPingStatus.Checking))
        {
            throw new InvalidOperationException("Fixed ping self-test: first/configuration-changed rounds must publish checking rows.");
        }

        FixedPingSnapshot reconnected = BuildRoundStartSnapshot(completed, defaults, false, RoundStartReason.Reconnected);
        if (!reconnected.Running || !AllTargetsHaveStatus(reconnected, FixedPingStatus.Checking))
        {
            throw new InvalidOperationException("Fixed ping self-test: reconnect must not expose rows from the old network identity.");
        }

        if (!SnapshotMatchesIdentity(completed, 5, "IF-A", defaultTargetSignature) ||
            SnapshotMatchesIdentity(completed, 6, "if-a", defaultTargetSignature) ||
            SnapshotMatchesIdentity(completed, 5, "if-b", defaultTargetSignature) ||
            SnapshotMatchesIdentity(completed, 5, "if-a", "different-targets") ||
            IsRequestIdentityCurrent(9, "new", 8, "old") ||
            !IsRequestIdentityCurrent(9, "new", 9, "new"))
        {
            throw new InvalidOperationException("Fixed ping self-test: stale network identity validation failed.");
        }

        FixedPingSnapshot identityClone = completed.Clone();
        if (identityClone.NetworkGeneration != 5 ||
            !string.Equals(identityClone.InterfaceId, "if-a", StringComparison.Ordinal) ||
            !string.Equals(identityClone.TargetSignature, defaultTargetSignature, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Fixed ping self-test: snapshot identity did not survive Clone().");
        }

        Console.WriteLine("Fixed ping probe: PASS defaults custom-normalization disabled-traffic-boundary stable-round-transition request-identity");
    }

    private static bool AllTargetsHaveStatus(FixedPingSnapshot value, FixedPingStatus expected)
    {
        if (value == null || !value.Running || value.Targets == null || value.Targets.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < value.Targets.Length; i++)
        {
            if (value.Targets[i] == null || value.Targets[i].Status != expected)
            {
                return false;
            }
        }

        return true;
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            this.disposed = true;
        }
    }
}
