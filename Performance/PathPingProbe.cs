using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

// Pathping-style per-hop quality: discover the route once with TTL-limited probes, then keep a
// rolling latency/loss window per responding hop. Owned state is mutated only on the background
// task; callers always receive a deep clone, matching how the other network readers behave.
//
// The classification rules in ClassifyHops are the point of this module. A traceroute that simply
// paints every lossy hop red is wrong far more often than it is right: intermediate routers
// commonly deprioritise or rate-limit ICMP addressed to themselves while forwarding traffic
// perfectly. Loss only means something when it persists all the way through to the target.
internal sealed class PathPingProbeReader : IDisposable
{
    internal const int MaxHops = 30;
    internal const double HopLossWarnPercent = 2.0;
    private const int TraceTimeoutMs = 1000;
    private const int HopTimeoutMs = 1000;
    private const int HopSpacingMs = 60;
    private const int HopSampleMinCount = 5;
    private const int HopSampleMaxCount = 20;
    private const int PathRediscoverIntervalMinutes = 10;
    private const int PathFailureRediscoverRounds = 3;
    private const int ProbePayloadSize = 32;

    private static readonly TimeSpan HopSampleTtl = TimeSpan.FromMinutes(15);

    private readonly object sync = new object();
    private readonly Dictionary<int, HopSampleWindow> hopSamples = new Dictionary<int, HopSampleWindow>();
    private PathPingSnapshot snapshot = new PathPingSnapshot();
    private List<DiscoveredHop> path = new List<DiscoveredHop>();
    private DateTime lastTraceUtc = DateTime.MinValue;
    private DateTime lastRoundUtc = DateTime.MinValue;
    private long pathGeneration = long.MinValue;
    private string pathInterfaceId = string.Empty;
    private string pathTargetSignature = string.Empty;
    private long currentRequestGeneration = long.MinValue;
    private string currentRequestInterfaceId = string.Empty;
    private string currentRequestTargetSignature = string.Empty;
    private long requestEpoch;
    private bool currentRequestUsable;
    private int consecutiveTargetFailures;
    private bool requestRunning;
    private bool rediscoverRequested = true;
    private int roundCount;
    private bool disposed;

    public PathPingSnapshot GetSnapshot()
    {
        lock (this.sync)
        {
            return this.snapshot.Clone();
        }
    }

    public void RequestRediscover()
    {
        lock (this.sync)
        {
            this.rediscoverRequested = true;
        }
    }

    // Called from NetworkMonitorReader.GetSnapshot on the UI thread. Never blocks: it either
    // starts a background round or returns immediately. samplingActive is false while the docked
    // panel is collapsed, which suspends per-hop probing entirely.
    public PathPingSnapshot Poll(
        WidgetSettings settings,
        string target,
        string gateway,
        long generation,
        string interfaceId,
        bool samplingActive,
        bool icmpBlocked,
        bool connected)
    {
        DateTime nowUtc = DateTime.UtcNow;
        bool shouldStart = false;
        bool traceNeeded = false;
        string requestTarget = string.IsNullOrWhiteSpace(target) ? string.Empty : target.Trim();
        string requestGateway = string.IsNullOrWhiteSpace(gateway) ? string.Empty : gateway.Trim();
        string requestInterfaceId = interfaceId ?? string.Empty;
        string requestTargetSignature = BuildRequestTargetSignature(requestTarget, requestGateway);
        bool requestUsable = connected && !icmpBlocked && requestTarget.Length > 0;
        long requestEpochAtStart = 0;

        lock (this.sync)
        {
            if (this.disposed)
            {
                return this.snapshot.Clone();
            }

            if (!IsRequestIdentityCurrent(
                    this.currentRequestGeneration,
                    this.currentRequestInterfaceId,
                    this.currentRequestTargetSignature,
                    this.currentRequestUsable,
                    generation,
                    requestInterfaceId,
                    requestTargetSignature,
                    requestUsable))
            {
                // Relinquish ownership immediately. The old task may still finish, but every
                // mutation and its finally block validates this epoch before touching live state.
                this.currentRequestGeneration = generation;
                this.currentRequestInterfaceId = requestInterfaceId;
                this.currentRequestTargetSignature = requestTargetSignature;
                this.currentRequestUsable = requestUsable;
                this.requestEpoch++;
                this.requestRunning = false;
                this.rediscoverRequested = true;
            }

            if (!requestUsable)
            {
                // No usable ICMP: publish the degraded verdict and drop the path so a later
                // recovery re-discovers instead of resuming a route that may no longer exist.
                this.snapshot = BuildUnavailableSnapshot(requestTarget, icmpBlocked);
                this.path = new List<DiscoveredHop>();
                this.hopSamples.Clear();
                this.rediscoverRequested = true;
                this.consecutiveTargetFailures = 0;
                return this.snapshot.Clone();
            }

            if (this.snapshot.IcmpUnavailable)
            {
                this.snapshot.IcmpUnavailable = false;
            }

            traceNeeded = this.rediscoverRequested ||
                this.path.Count == 0 ||
                this.pathGeneration != generation ||
                !string.Equals(this.pathInterfaceId, requestInterfaceId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(this.pathTargetSignature, requestTargetSignature, StringComparison.OrdinalIgnoreCase) ||
                this.consecutiveTargetFailures >= PathFailureRediscoverRounds ||
                (this.lastTraceUtc != DateTime.MinValue &&
                 (nowUtc - this.lastTraceUtc).TotalMinutes >= PathRediscoverIntervalMinutes);

            if (!traceNeeded && !samplingActive)
            {
                return this.snapshot.Clone();
            }

            int intervalMs = GetRoundIntervalMs(settings);
            bool roundDue = this.lastRoundUtc == DateTime.MinValue ||
                (nowUtc - this.lastRoundUtc).TotalMilliseconds >= intervalMs;
            if (!this.requestRunning && (traceNeeded || (samplingActive && roundDue)))
            {
                this.requestRunning = true;
                this.lastRoundUtc = nowUtc;
                if (traceNeeded)
                {
                    this.snapshot.Stale = this.snapshot.PathKnown;
                    this.snapshot.DiscoveryInProgress = true;
                    this.snapshot.DiscoveryCurrentHop = 0;
                    this.snapshot.DiscoveryMaxHops = MaxHops;
                }

                requestEpochAtStart = this.requestEpoch;
                shouldStart = true;
            }

            if (!shouldStart)
            {
                return this.snapshot.Clone();
            }
        }

        bool runTrace = traceNeeded;
        Task.Run(delegate
        {
            try
            {
                RunRound(
                    runTrace,
                    requestTarget,
                    requestGateway,
                    generation,
                    requestInterfaceId,
                    requestTargetSignature,
                    requestEpochAtStart,
                    samplingActive);
            }
            catch (Exception ex)
            {
                Program.LogInfo("PathPing round failed: " + ex.Message);
                lock (this.sync)
                {
                    if (IsRequestIdentityCurrentLocked(
                        generation,
                        requestInterfaceId,
                        requestTargetSignature,
                        requestEpochAtStart))
                    {
                        this.snapshot.DiscoveryInProgress = false;
                    }
                }
            }
            finally
            {
                lock (this.sync)
                {
                    if (IsRequestIdentityCurrentLocked(
                        generation,
                        requestInterfaceId,
                        requestTargetSignature,
                        requestEpochAtStart))
                    {
                        this.requestRunning = false;
                    }
                }
            }
        });

        return GetSnapshot();
    }

    internal static int GetRoundIntervalMs(WidgetSettings settings)
    {
        // The default mode follows Windows, so the raw setting has to be resolved before it can be
        // compared: without this, a machine in Windows battery saver would keep probing every hop
        // at the full balanced rate.
        WidgetPerformanceMode mode = WidgetSettings.GetEffectivePerformanceMode(
            settings == null ? WidgetPerformanceMode.Balanced : settings.PerformanceMode);
        return GetRoundIntervalMsForMode(mode);
    }

    internal static int GetRoundIntervalMsForMode(WidgetPerformanceMode mode)
    {
        return mode == WidgetPerformanceMode.BatterySaver ? 10000 : 3000;
    }

    private void RunRound(
        bool runTrace,
        string target,
        string gateway,
        long generation,
        string interfaceId,
        string targetSignature,
        long requestEpochAtStart,
        bool samplingActive)
    {
        List<DiscoveredHop> activePath;
        if (runTrace)
        {
            DateTime traceStartedUtc = DateTime.UtcNow;
            List<DiscoveredHop> discovered = DiscoverPath(
                target,
                gateway,
                generation,
                interfaceId,
                targetSignature,
                requestEpochAtStart);
            lock (this.sync)
            {
                if (!IsRequestIdentityCurrentLocked(
                    generation,
                    interfaceId,
                    targetSignature,
                    requestEpochAtStart))
                {
                    return;
                }

                if (discovered.Count == 0)
                {
                    // Keep the previous path rather than blanking: a single failed trace during a
                    // transient outage should not destroy accumulated per-hop history.
                    this.snapshot.Stale = false;
                    this.snapshot.DiscoveryInProgress = false;
                    this.rediscoverRequested = true;
                    return;
                }

                this.path = discovered;
                this.pathGeneration = generation;
                this.pathInterfaceId = interfaceId;
                this.pathTargetSignature = targetSignature;
                this.lastTraceUtc = DateTime.UtcNow;
                this.rediscoverRequested = false;
                this.consecutiveTargetFailures = 0;
                this.roundCount = 0;
                PruneHopSamples(discovered);
            }

            Program.LogInfo(
                "PathPing route discovered: hops=" + discovered.Count.ToString(CultureInfo.InvariantCulture) +
                " duration_ms=" + Math.Max(0, (int)(DateTime.UtcNow - traceStartedUtc).TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        }

        lock (this.sync)
        {
            if (!IsRequestIdentityCurrentLocked(
                generation,
                interfaceId,
                targetSignature,
                requestEpochAtStart))
            {
                return;
            }

            activePath = new List<DiscoveredHop>(this.path);
        }

        if (activePath.Count == 0)
        {
            return;
        }

        if (samplingActive)
        {
            SampleHops(activePath, generation, interfaceId, targetSignature, requestEpochAtStart);
        }

        PublishSnapshot(target, activePath, runTrace, generation, interfaceId, targetSignature, requestEpochAtStart);
    }

    private void SampleHops(
        List<DiscoveredHop> activePath,
        long generation,
        string interfaceId,
        string targetSignature,
        long requestEpochAtStart)
    {
        DateTime nowUtc = DateTime.UtcNow;
        for (int i = 0; i < activePath.Count; i++)
        {
            DiscoveredHop hop = activePath[i];
            if (!hop.Responding || string.IsNullOrEmpty(hop.Address))
            {
                continue;
            }

            bool success;
            int latencyMs;
            SendEcho(hop.Address, HopTimeoutMs, out success, out latencyMs);
            lock (this.sync)
            {
                if (!IsRequestIdentityCurrentLocked(
                    generation,
                    interfaceId,
                    targetSignature,
                    requestEpochAtStart))
                {
                    return;
                }

                HopSampleWindow window;
                if (!this.hopSamples.TryGetValue(hop.HopNumber, out window))
                {
                    window = new HopSampleWindow();
                    this.hopSamples[hop.HopNumber] = window;
                }

                window.Add(success, latencyMs, nowUtc);
                if (hop.IsTarget)
                {
                    this.consecutiveTargetFailures = success ? 0 : this.consecutiveTargetFailures + 1;
                }
            }

            // Spacing keeps a round from arriving at the path as one burst, which is itself a
            // trigger for the rate limiting this module is trying to measure.
            if (i < activePath.Count - 1)
            {
                Thread.Sleep(HopSpacingMs);
            }
        }
    }

    private void PublishSnapshot(
        string target,
        List<DiscoveredHop> activePath,
        bool traced,
        long generation,
        string interfaceId,
        string targetSignature,
        long requestEpochAtStart)
    {
        DateTime nowUtc = DateTime.UtcNow;
        List<PathPingHopSnapshot> raw = new List<PathPingHopSnapshot>();
        lock (this.sync)
        {
            if (!IsRequestIdentityCurrentLocked(
                generation,
                interfaceId,
                targetSignature,
                requestEpochAtStart))
            {
                return;
            }

            if (traced)
            {
                this.roundCount = 0;
            }

            this.roundCount++;
            for (int i = 0; i < activePath.Count; i++)
            {
                DiscoveredHop hop = activePath[i];
                PathPingHopSnapshot row = new PathPingHopSnapshot
                {
                    HopNumber = hop.HopNumber,
                    Address = hop.Address,
                    Responding = hop.Responding,
                    IsGateway = hop.IsGateway,
                    IsTarget = hop.IsTarget,
                    MergedHopCount = 1
                };

                HopSampleWindow window;
                if (hop.Responding && this.hopSamples.TryGetValue(hop.HopNumber, out window))
                {
                    HopStats stats = window.BuildStats(nowUtc);
                    row.AvgLatencyMs = stats.LatencyMs;
                    row.LossPercent = stats.LossPercent;
                    row.SampleCount = stats.TotalCount;
                }

                raw.Add(row);
            }

            PathPingSnapshot next = new PathPingSnapshot
            {
                TargetLabel = target,
                PathKnown = true,
                DiscoveryInProgress = false,
                DiscoveryCurrentHop = activePath.Count,
                DiscoveryMaxHops = MaxHops,
                Stale = false,
                LastTraceLocal = this.lastTraceUtc == DateTime.MinValue
                    ? DateTime.MinValue
                    : this.lastTraceUtc.ToLocalTime(),
                LastTraceKnown = this.lastTraceUtc != DateTime.MinValue,
                RoundCount = this.roundCount
            };

            ClassifyHops(raw, next);
            next.Hops = MergeSilentHops(raw).ToArray();
            this.snapshot = next;
        }
    }

    // Pure classification: given per-hop loss, decide what is actually wrong with the path.
    // Exercised directly by RunSelfTest, which is why it takes and mutates plain snapshot objects
    // rather than reading reader state.
    internal static void ClassifyHops(List<PathPingHopSnapshot> hops, PathPingSnapshot result)
    {
        if (hops == null || hops.Count == 0 || result == null)
        {
            return;
        }

        PathPingHopSnapshot targetHop = null;
        for (int i = hops.Count - 1; i >= 0; i--)
        {
            if (hops[i].IsTarget)
            {
                targetHop = hops[i];
                break;
            }
        }

        bool targetHealthy = targetHop != null &&
            targetHop.Responding &&
            targetHop.SampleCount > 0 &&
            targetHop.LossPercent < HopLossWarnPercent;

        if (targetHop != null && targetHop.SampleCount > 0)
        {
            result.EndToEndLatencyMs = targetHop.AvgLatencyMs;
            result.EndToEndLossPercent = targetHop.LossPercent;
            result.EndToEndKnown = true;
        }

        // Walk forward looking for the first hop where loss starts and never recovers through to
        // the target. Only that pattern is real forwarding loss.
        int linkLossHop = -1;
        for (int i = 0; i < hops.Count; i++)
        {
            PathPingHopSnapshot hop = hops[i];
            if (!hop.Responding || hop.SampleCount <= 0 || hop.LossPercent < HopLossWarnPercent)
            {
                continue;
            }

            bool sustained = true;
            for (int j = i + 1; j < hops.Count; j++)
            {
                PathPingHopSnapshot later = hops[j];
                if (!later.Responding || later.SampleCount <= 0)
                {
                    continue;
                }

                if (later.LossPercent < HopLossWarnPercent)
                {
                    sustained = false;
                    break;
                }
            }

            if (sustained && !targetHealthy)
            {
                linkLossHop = i;
                break;
            }
        }

        for (int i = 0; i < hops.Count; i++)
        {
            PathPingHopSnapshot hop = hops[i];
            if (!hop.Responding)
            {
                hop.Severity = PathPingHopSeverity.Unresponsive;
                continue;
            }

            if (hop.SampleCount <= 0)
            {
                hop.Severity = PathPingHopSeverity.None;
                continue;
            }

            if (hop.LossPercent < HopLossWarnPercent)
            {
                hop.Severity = PathPingHopSeverity.Normal;
                continue;
            }

            hop.Severity = linkLossHop >= 0 && i >= linkLossHop
                ? PathPingHopSeverity.Loss
                : PathPingHopSeverity.RateLimited;
        }

        if (linkLossHop >= 0)
        {
            PathPingHopSnapshot blamed = hops[linkLossHop];
            result.Blame = PathPingBlame.LinkLoss;
            result.BlameHopNumber = blamed.HopNumber;
            result.BlameText = "丢包始于第 " + blamed.HopNumber.ToString(CultureInfo.InvariantCulture) +
                " 跳 " + blamed.Address;
            return;
        }

        if (targetHop != null && targetHop.Responding && targetHop.SampleCount > 0 &&
            targetHop.LossPercent >= HopLossWarnPercent)
        {
            result.Blame = PathPingBlame.LinkLoss;
            result.BlameHopNumber = targetHop.HopNumber;
            result.BlameText = "目标节点丢包 " + FormatPercent(targetHop.LossPercent);
            return;
        }

        int rateLimited = 0;
        int firstRateLimitedHop = 0;
        for (int i = 0; i < hops.Count; i++)
        {
            if (hops[i].Severity == PathPingHopSeverity.RateLimited)
            {
                if (rateLimited == 0)
                {
                    firstRateLimitedHop = hops[i].HopNumber;
                }

                rateLimited++;
            }
        }

        if (rateLimited > 0)
        {
            result.Blame = PathPingBlame.NodeRateLimit;
            result.BlameHopNumber = firstRateLimitedHop;
            result.BlameText = "第 " + firstRateLimitedHop.ToString(CultureInfo.InvariantCulture) +
                " 跳节点限速，链路无实际丢包";
            return;
        }

        if (targetHop == null || !targetHop.Responding)
        {
            result.Blame = PathPingBlame.Unreachable;
            result.BlameHopNumber = 0;
            result.BlameText = "目标不可达，路径在中途中断";
            return;
        }

        result.Blame = PathPingBlame.None;
        result.BlameHopNumber = 0;
        result.BlameText = "全程无异常丢包";
    }

    // Consecutive silent hops carry no information individually; collapsing them keeps the table
    // from being flooded by a long anonymous stretch inside a carrier backbone.
    internal static List<PathPingHopSnapshot> MergeSilentHops(List<PathPingHopSnapshot> hops)
    {
        List<PathPingHopSnapshot> merged = new List<PathPingHopSnapshot>();
        if (hops == null)
        {
            return merged;
        }

        int index = 0;
        while (index < hops.Count)
        {
            PathPingHopSnapshot hop = hops[index];
            if (hop.Responding)
            {
                merged.Add(hop);
                index++;
                continue;
            }

            int runLength = 0;
            int startHopNumber = hop.HopNumber;
            while (index < hops.Count && !hops[index].Responding)
            {
                runLength++;
                index++;
            }

            merged.Add(new PathPingHopSnapshot
            {
                HopNumber = startHopNumber,
                Address = string.Empty,
                Responding = false,
                MergedHopCount = runLength,
                Severity = PathPingHopSeverity.Unresponsive
            });
        }

        return merged;
    }

    internal static string FormatPercent(double value)
    {
        double clamped = Math.Max(0.0, Math.Min(100.0, value));
        return clamped.ToString("0.#", CultureInfo.InvariantCulture) + "%";
    }

    private static PathPingSnapshot BuildUnavailableSnapshot(string target, bool icmpBlocked)
    {
        return new PathPingSnapshot
        {
            TargetLabel = target,
            PathKnown = false,
            Stale = false,
            IcmpUnavailable = true,
            Blame = PathPingBlame.IcmpUnavailable,
            BlameText = icmpBlocked ? "ICMP 不可用，无法逐跳探测" : "网络不可用，暂停逐跳探测"
        };
    }

    private List<DiscoveredHop> DiscoverPath(
        string target,
        string gateway,
        long generation,
        string interfaceId,
        string targetSignature,
        long requestEpochAtStart)
    {
        List<DiscoveredHop> discovered = new List<DiscoveredHop>();
        // Ping.Send(string, ...) may resolve the host for every TTL. Keep one concrete address for
        // the whole trace so discovery cost is bounded and every hop follows the same address path.
        IPAddress targetAddress;
        if (!TryResolveTraceTarget(target, out targetAddress))
        {
            return discovered;
        }

        byte[] payload = new byte[ProbePayloadSize];
        for (int ttl = 1; ttl <= MaxHops; ttl++)
        {
            lock (this.sync)
            {
                if (!IsRequestIdentityCurrentLocked(
                    generation,
                    interfaceId,
                    targetSignature,
                    requestEpochAtStart))
                {
                    return new List<DiscoveredHop>();
                }
            }

            string address = string.Empty;
            bool responding = false;
            bool reachedTarget = false;
            try
            {
                using (Ping ping = new Ping())
                {
                    PingOptions options = new PingOptions(ttl, true);
                    PingReply reply = ping.Send(targetAddress, TraceTimeoutMs, payload, options);
                    if (reply != null &&
                        (reply.Status == IPStatus.TtlExpired || reply.Status == IPStatus.Success) &&
                        reply.Address != null)
                    {
                        address = reply.Address.ToString();
                        responding = !string.IsNullOrEmpty(address) && !string.Equals(address, "0.0.0.0", StringComparison.Ordinal);
                        reachedTarget = reply.Status == IPStatus.Success;
                    }
                }
            }
            catch (PingException)
            {
            }
            catch (SocketException)
            {
            }

            discovered.Add(new DiscoveredHop
            {
                HopNumber = ttl,
                Address = address,
                Responding = responding,
                IsGateway = responding && !string.IsNullOrEmpty(gateway) &&
                    string.Equals(address, gateway, StringComparison.OrdinalIgnoreCase),
                IsTarget = reachedTarget
            });

            UpdateDiscoveryProgress(
                target,
                ttl,
                generation,
                interfaceId,
                targetSignature,
                requestEpochAtStart);

            if (reachedTarget)
            {
                break;
            }
        }

        // A trace that never reached the target is only useful if something answered along the
        // way; an all-silent result means ICMP is filtered and should not become the live path.
        bool anyResponding = false;
        bool anyTarget = false;
        for (int i = 0; i < discovered.Count; i++)
        {
            anyResponding |= discovered[i].Responding;
            anyTarget |= discovered[i].IsTarget;
        }

        if (!anyResponding)
        {
            return new List<DiscoveredHop>();
        }

        if (!anyTarget)
        {
            // Trim the trailing silent tail so the table ends at the last hop that answered.
            int lastResponding = -1;
            for (int i = discovered.Count - 1; i >= 0; i--)
            {
                if (discovered[i].Responding)
                {
                    lastResponding = i;
                    break;
                }
            }

            if (lastResponding >= 0 && lastResponding < discovered.Count - 1)
            {
                discovered.RemoveRange(lastResponding + 1, discovered.Count - lastResponding - 1);
            }
        }

        return discovered;
    }

    private static bool TryResolveTraceTarget(string target, out IPAddress targetAddress)
    {
        targetAddress = null;
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        string normalizedTarget = target.Trim();
        IPAddress parsed;
        if (IPAddress.TryParse(normalizedTarget, out parsed))
        {
            targetAddress = SelectFirstUsableAddress(new IPAddress[] { parsed });
            return targetAddress != null;
        }

        try
        {
            targetAddress = SelectFirstUsableAddress(Dns.GetHostAddresses(normalizedTarget));
            return targetAddress != null;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static IPAddress SelectFirstUsableAddress(IPAddress[] addresses)
    {
        if (addresses == null)
        {
            return null;
        }

        for (int i = 0; i < addresses.Length; i++)
        {
            IPAddress candidate = addresses[i];
            if (candidate != null &&
                (candidate.AddressFamily == AddressFamily.InterNetwork ||
                 candidate.AddressFamily == AddressFamily.InterNetworkV6))
            {
                return candidate;
            }
        }

        return null;
    }

    private void UpdateDiscoveryProgress(
        string target,
        int currentHop,
        long generation,
        string interfaceId,
        string targetSignature,
        long requestEpochAtStart)
    {
        lock (this.sync)
        {
            if (!IsRequestIdentityCurrentLocked(
                generation,
                interfaceId,
                targetSignature,
                requestEpochAtStart))
            {
                return;
            }

            this.snapshot.TargetLabel = target ?? string.Empty;
            this.snapshot.DiscoveryInProgress = true;
            this.snapshot.DiscoveryCurrentHop = Math.Max(0, Math.Min(MaxHops, currentHop));
            this.snapshot.DiscoveryMaxHops = MaxHops;
        }
    }

    private bool IsRequestIdentityCurrentLocked(
        long generation,
        string interfaceId,
        string targetSignature,
        long requestEpochAtStart)
    {
        return !this.disposed &&
            this.requestEpoch == requestEpochAtStart &&
            IsRequestIdentityCurrent(
                this.currentRequestGeneration,
                this.currentRequestInterfaceId,
                this.currentRequestTargetSignature,
                this.currentRequestUsable,
                generation,
                interfaceId,
                targetSignature,
                true);
    }

    private static bool IsRequestIdentityCurrent(
        long currentGeneration,
        string currentInterfaceId,
        string currentTargetSignature,
        bool currentUsable,
        long requestGeneration,
        string requestInterfaceId,
        string requestTargetSignature,
        bool requestUsable)
    {
        return currentGeneration == requestGeneration &&
            currentUsable == requestUsable &&
            string.Equals(currentInterfaceId, requestInterfaceId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(currentTargetSignature, requestTargetSignature, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRequestTargetSignature(string target, string gateway)
    {
        return (target ?? string.Empty).Trim().ToUpperInvariant() + "|" +
            (gateway ?? string.Empty).Trim().ToUpperInvariant();
    }

    internal static int GetDiscoveryPercent(int currentHop, int maxHops)
    {
        if (maxHops <= 0)
        {
            return 0;
        }

        return Math.Max(0, Math.Min(100, (int)Math.Round(currentHop * 100.0 / maxHops)));
    }

    private static void SendEcho(string address, int timeoutMs, out bool success, out int latencyMs)
    {
        success = false;
        latencyMs = 0;
        try
        {
            using (Ping ping = new Ping())
            {
                PingReply reply = ping.Send(address, timeoutMs);
                if (reply != null && reply.Status == IPStatus.Success)
                {
                    success = true;
                    latencyMs = (int)Math.Min(int.MaxValue, Math.Max(0L, reply.RoundtripTime));
                }
            }
        }
        catch (PingException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private void PruneHopSamples(List<DiscoveredHop> discovered)
    {
        HashSet<int> live = new HashSet<int>();
        for (int i = 0; i < discovered.Count; i++)
        {
            live.Add(discovered[i].HopNumber);
        }

        List<int> stale = new List<int>();
        foreach (KeyValuePair<int, HopSampleWindow> pair in this.hopSamples)
        {
            if (!live.Contains(pair.Key))
            {
                stale.Add(pair.Key);
            }
        }

        for (int i = 0; i < stale.Count; i++)
        {
            this.hopSamples.Remove(stale[i]);
        }
    }

    internal static void RunSelfTest()
    {
        // Node rate limiting: a middle hop drops direct ICMP while the target stays clean.
        // Calling this a link fault is the classic traceroute misdiagnosis.
        List<PathPingHopSnapshot> rateLimited = BuildTestHops(
            new int[] { 1, 2, 3, 4 },
            new bool[] { true, true, true, true },
            new double[] { 0.0, 0.0, 40.0, 0.0 });
        rateLimited[3].IsTarget = true;
        PathPingSnapshot rateLimitedResult = new PathPingSnapshot();
        ClassifyHops(rateLimited, rateLimitedResult);
        if (rateLimitedResult.Blame != PathPingBlame.NodeRateLimit ||
            rateLimitedResult.BlameHopNumber != 3 ||
            rateLimited[2].Severity != PathPingHopSeverity.RateLimited ||
            rateLimited[3].Severity != PathPingHopSeverity.Normal)
        {
            throw new InvalidOperationException("PathPing self-test: node rate limiting must not be reported as link loss.");
        }

        // Real forwarding loss: it starts at hop 3 and persists through the target.
        List<PathPingHopSnapshot> linkLoss = BuildTestHops(
            new int[] { 1, 2, 3, 4, 5 },
            new bool[] { true, true, true, true, true },
            new double[] { 0.0, 0.0, 12.0, 14.0, 13.0 });
        linkLoss[4].IsTarget = true;
        PathPingSnapshot linkLossResult = new PathPingSnapshot();
        ClassifyHops(linkLoss, linkLossResult);
        if (linkLossResult.Blame != PathPingBlame.LinkLoss ||
            linkLossResult.BlameHopNumber != 3 ||
            linkLoss[2].Severity != PathPingHopSeverity.Loss ||
            linkLoss[1].Severity != PathPingHopSeverity.Normal)
        {
            throw new InvalidOperationException("PathPing self-test: sustained loss must be blamed on its first hop.");
        }

        if (!linkLossResult.EndToEndKnown || Math.Abs(linkLossResult.EndToEndLossPercent - 13.0) > 0.01)
        {
            throw new InvalidOperationException("PathPing self-test: end-to-end stats must come from the target hop.");
        }

        // Healthy path.
        List<PathPingHopSnapshot> healthy = BuildTestHops(
            new int[] { 1, 2, 3 },
            new bool[] { true, true, true },
            new double[] { 0.0, 0.0, 0.0 });
        healthy[2].IsTarget = true;
        PathPingSnapshot healthyResult = new PathPingSnapshot();
        ClassifyHops(healthy, healthyResult);
        if (healthyResult.Blame != PathPingBlame.None)
        {
            throw new InvalidOperationException("PathPing self-test: a clean path must not report blame.");
        }

        // Unreachable target: the path dies partway and nothing answers at the end.
        List<PathPingHopSnapshot> broken = BuildTestHops(
            new int[] { 1, 2, 3 },
            new bool[] { true, true, false },
            new double[] { 0.0, 0.0, 0.0 });
        PathPingSnapshot brokenResult = new PathPingSnapshot();
        ClassifyHops(broken, brokenResult);
        if (brokenResult.Blame != PathPingBlame.Unreachable)
        {
            throw new InvalidOperationException("PathPing self-test: a path with no target must report unreachable.");
        }

        // Silent-hop merging: hops 3 and 4 collapse into one row that remembers the run length.
        List<PathPingHopSnapshot> silent = BuildTestHops(
            new int[] { 1, 2, 3, 4, 5 },
            new bool[] { true, true, false, false, true },
            new double[] { 0.0, 0.0, 0.0, 0.0, 0.0 });
        List<PathPingHopSnapshot> merged = MergeSilentHops(silent);
        if (merged.Count != 4 || merged[2].MergedHopCount != 2 || merged[2].HopNumber != 3 || merged[2].Responding)
        {
            throw new InvalidOperationException("PathPing self-test: consecutive silent hops must merge into one row.");
        }

        // ICMP unavailable degrades to an explicit verdict with no hop rows at all.
        PathPingSnapshot unavailable = BuildUnavailableSnapshot("1.1.1.1", true);
        if (!unavailable.IcmpUnavailable || unavailable.Hops.Length != 0 || unavailable.Blame != PathPingBlame.IcmpUnavailable)
        {
            throw new InvalidOperationException("PathPing self-test: blocked ICMP must degrade without hop rows.");
        }

        if (GetDiscoveryPercent(0, MaxHops) != 0 ||
            GetDiscoveryPercent(15, MaxHops) != 50 ||
            GetDiscoveryPercent(MaxHops + 10, MaxHops) != 100)
        {
            throw new InvalidOperationException("PathPing self-test: discovery progress must clamp to 0..100.");
        }

        IPAddress literalTarget;
        if (!TryResolveTraceTarget("203.0.113.10", out literalTarget) ||
            literalTarget == null ||
            !string.Equals(literalTarget.ToString(), "203.0.113.10", StringComparison.Ordinal) ||
            TryResolveTraceTarget(" ", out literalTarget))
        {
            throw new InvalidOperationException("PathPing self-test: trace target resolution failed.");
        }

        IPAddress firstResolved = SelectFirstUsableAddress(new IPAddress[]
        {
            null,
            IPAddress.Parse("2001:db8::1"),
            IPAddress.Parse("203.0.113.20")
        });
        if (firstResolved == null || !string.Equals(firstResolved.ToString(), "2001:db8::1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PathPing self-test: resolver must keep the first usable address.");
        }

        // Battery saver must slow the round cadence rather than keep the balanced rate. Comparing
        // resolved modes directly keeps this independent of the machine's current power state.
        if (GetRoundIntervalMsForMode(WidgetPerformanceMode.BatterySaver) <=
            GetRoundIntervalMsForMode(WidgetPerformanceMode.Balanced))
        {
            throw new InvalidOperationException("PathPing self-test: battery saver must widen the round interval.");
        }

        // WindowsPowerMode is the shipped default and must resolve rather than fall through to the
        // balanced branch by accident.
        WidgetSettings followWindows = WidgetSettings.CreateDefaults();
        followWindows.PerformanceMode = WidgetPerformanceMode.WindowsPowerMode;
        int resolved = GetRoundIntervalMs(followWindows);
        if (resolved != GetRoundIntervalMsForMode(WidgetPerformanceMode.Balanced) &&
            resolved != GetRoundIntervalMsForMode(WidgetPerformanceMode.BatterySaver))
        {
            throw new InvalidOperationException("PathPing self-test: follow-Windows mode must resolve to a known cadence.");
        }

        string targetSignature = BuildRequestTargetSignature("1.1.1.1", "192.168.1.1");
        if (!IsRequestIdentityCurrent(4, "if-a", targetSignature, true, 4, "IF-A", targetSignature, true) ||
            IsRequestIdentityCurrent(4, "if-a", targetSignature, true, 3, "if-a", targetSignature, true) ||
            IsRequestIdentityCurrent(4, "if-a", targetSignature, true, 4, "if-b", targetSignature, true) ||
            IsRequestIdentityCurrent(4, "if-a", targetSignature, true, 4, "if-a", BuildRequestTargetSignature("8.8.8.8", "192.168.1.1"), true) ||
            IsRequestIdentityCurrent(4, "if-a", targetSignature, false, 4, "if-a", targetSignature, true))
        {
            throw new InvalidOperationException("PathPing self-test: stale request identity must reject all mutations.");
        }

        Console.WriteLine("PathPing probe: PASS rate-limit-vs-link-loss unreachable silent-merge icmp-degrade interval progress single-resolution request-identity");
    }

    private static List<PathPingHopSnapshot> BuildTestHops(int[] numbers, bool[] responding, double[] loss)
    {
        List<PathPingHopSnapshot> hops = new List<PathPingHopSnapshot>();
        for (int i = 0; i < numbers.Length; i++)
        {
            hops.Add(new PathPingHopSnapshot
            {
                HopNumber = numbers[i],
                Address = "10.0.0." + numbers[i].ToString(CultureInfo.InvariantCulture),
                Responding = responding[i],
                SampleCount = responding[i] ? HopSampleMaxCount : 0,
                LossPercent = loss[i],
                AvgLatencyMs = 10.0 + numbers[i]
            });
        }

        return hops;
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            this.disposed = true;
            this.hopSamples.Clear();
            this.path = new List<DiscoveredHop>();
        }
    }

    private sealed class DiscoveredHop
    {
        public int HopNumber;
        public string Address;
        public bool Responding;
        public bool IsGateway;
        public bool IsTarget;
    }

    private struct HopStats
    {
        public int TotalCount;
        public double LossPercent;
        public double LatencyMs;
    }

    private sealed class HopSampleWindow
    {
        private readonly List<HopSample> samples = new List<HopSample>();

        public void Add(bool success, int latencyMs, DateTime timestampUtc)
        {
            Prune(timestampUtc);
            this.samples.Add(new HopSample
            {
                TimestampUtc = timestampUtc,
                Success = success,
                LatencyMs = success ? Math.Max(0, latencyMs) : 0
            });

            while (this.samples.Count > HopSampleMaxCount)
            {
                this.samples.RemoveAt(0);
            }
        }

        public HopStats BuildStats(DateTime nowUtc)
        {
            Prune(nowUtc);
            HopStats stats = new HopStats();
            if (this.samples.Count == 0)
            {
                return stats;
            }

            int successCount = 0;
            double latencySum = 0.0;
            for (int i = 0; i < this.samples.Count; i++)
            {
                if (this.samples[i].Success)
                {
                    successCount++;
                    latencySum += this.samples[i].LatencyMs;
                }
            }

            stats.TotalCount = this.samples.Count;
            stats.LossPercent = (this.samples.Count - successCount) * 100.0 / this.samples.Count;
            stats.LatencyMs = successCount > 0 ? latencySum / successCount : 0.0;

            // Below the minimum the window is too short for a loss figure to mean anything, so
            // report the latency but suppress the count that drives classification.
            if (this.samples.Count < HopSampleMinCount)
            {
                stats.TotalCount = 0;
            }

            return stats;
        }

        private void Prune(DateTime nowUtc)
        {
            if (this.samples.Count == 0)
            {
                return;
            }

            DateTime cutoff = nowUtc - HopSampleTtl;
            int removeCount = 0;
            for (int i = 0; i < this.samples.Count; i++)
            {
                if (this.samples[i].TimestampUtc >= cutoff)
                {
                    break;
                }

                removeCount++;
            }

            if (removeCount > 0)
            {
                this.samples.RemoveRange(0, removeCount);
            }
        }
    }

    private struct HopSample
    {
        public DateTime TimestampUtc;
        public bool Success;
        public int LatencyMs;
    }
}
