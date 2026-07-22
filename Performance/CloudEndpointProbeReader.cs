using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal sealed class CloudEndpointProbeReader
{
    private static readonly TimeSpan ManualRefreshCooldown = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan StateConfirmationDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DetailedLogInterval = TimeSpan.FromHours(6);
    private readonly object sync = new object();
    private CloudEndpointSnapshot[] snapshots = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Unknown);
    private DateTime lastProbeStartedUtc = DateTime.MinValue;
    private DateTime nextProbeDueUtc = DateTime.MinValue;
    private DateTime lastManualRefreshAcceptedUtc = DateTime.MinValue;
    private DateTime lastDetailedLogUtc = DateTime.MinValue;
    private DateTime pendingStateFirstSeenUtc = DateTime.MinValue;
    private int lastManualRefreshToken;
    private int lastCloudStatusRegionMask = -1;
    private string lastTargetConfigurationSignature = string.Empty;
    private string pendingStateSignature = string.Empty;
    private string pendingForcedTrigger = string.Empty;
    private string currentNetworkBaseSignature = string.Empty;
    private string currentNetworkIdentitySignature = string.Empty;
    private CancellationTokenSource requestCancellation;
    private long requestEpoch;
    private bool pendingForceRefresh;
    private bool requestRunning;

    public void RequestRefresh()
    {
        RequestRefresh("云服务强制刷新");
    }

    public void RequestRefresh(string trigger)
    {
        CancellationTokenSource cancellation;
        lock (this.sync)
        {
            this.lastProbeStartedUtc = DateTime.MinValue;
            this.nextProbeDueUtc = DateTime.MinValue;
            this.pendingForcedTrigger = NormalizeRefreshTrigger(trigger, "云服务强制刷新");
            // A refresh request invalidates the old result and must bypass the endpoint cache
            // exactly once. The flag is consumed only after the next single-flight slot is owned.
            this.pendingForceRefresh = true;
            this.requestEpoch++;
            cancellation = this.requestCancellation;
        }

        if (cancellation != null)
        {
            CancelRequest(cancellation);
        }
    }

    public CloudEndpointSnapshot[] GetSnapshot(
        WidgetSettings settings,
        NetworkAccessState networkState,
        bool localNetworkDegraded,
        string localNetworkDegradedReason,
        long networkGeneration,
        string interfaceId)
    {
        string[] targetConfiguration = NetworkProbeTargetSettings.NormalizeCloudTargets(
            settings == null ? NetworkProbeTargetSettings.DefaultCloudEndpointTargets : settings.CloudEndpointTargets);
        string targetSignature = NetworkProbeTargetSettings.BuildSignature(targetConfiguration);
        string networkBaseSignature = BuildNetworkBaseSignature(networkGeneration, interfaceId);
        string networkIdentitySignature = networkBaseSignature + "|" + networkState.ToString();
        CancellationTokenSource identityCancellation = null;
        lock (this.sync)
        {
            if (!string.Equals(this.currentNetworkIdentitySignature, networkIdentitySignature, StringComparison.Ordinal))
            {
                bool networkBaseChanged = !string.Equals(
                    this.currentNetworkBaseSignature,
                    networkBaseSignature,
                    StringComparison.Ordinal);
                this.currentNetworkBaseSignature = networkBaseSignature;
                this.currentNetworkIdentitySignature = networkIdentitySignature;
                this.requestEpoch++;
                if (networkBaseChanged)
                {
                    this.pendingForceRefresh = true;
                    this.pendingForcedTrigger = "云服务网络身份变化";
                    this.lastProbeStartedUtc = DateTime.MinValue;
                    this.nextProbeDueUtc = DateTime.MinValue;
                }
                identityCancellation = this.requestCancellation;
                this.requestCancellation = null;
                this.requestRunning = false;
                ClearPendingStateLocked();
            }
        }

        CancelRequest(identityCancellation);
        if (networkState != NetworkAccessState.Online)
        {
            CloudEndpointSnapshot[] unavailable = CreateUnavailableSnapshots(GetUnavailableNetworkReason(networkState), targetConfiguration);
            lock (this.sync)
            {
                if (string.Equals(this.currentNetworkIdentitySignature, networkIdentitySignature, StringComparison.Ordinal))
                {
                    this.snapshots = CloneSnapshots(unavailable);
                    ClearPendingStateLocked();
                }
            }

            return unavailable;
        }

        DateTime now = DateTime.UtcNow;
        int intervalMinutes = settings == null
            ? WidgetSettings.DefaultGfwProbeIntervalMinutes
            : Math.Max(WidgetSettings.MinGfwProbeIntervalMinutes, settings.GfwProbeIntervalMinutes);
        int manualToken = settings == null ? 0 : settings.GfwProbeManualRefreshToken;
        int regionMask = settings == null ? WidgetSettings.DefaultCloudStatusRegionMask : settings.CloudStatusRegionMask;
        bool manualAccepted = false;
        bool regionChanged;
        bool targetsChanged;
        bool due;
        bool shouldStart = false;
        CancellationTokenSource configurationCancellation = null;

        lock (this.sync)
        {
            bool manualRefresh = manualToken != this.lastManualRefreshToken;
            regionChanged = regionMask != this.lastCloudStatusRegionMask;
            targetsChanged = !string.Equals(targetSignature, this.lastTargetConfigurationSignature, StringComparison.Ordinal);
            if (targetsChanged)
            {
                this.lastTargetConfigurationSignature = targetSignature;
                this.lastProbeStartedUtc = DateTime.MinValue;
                this.nextProbeDueUtc = DateTime.MinValue;
                this.pendingForcedTrigger = "云服务检测列表变化";
                this.pendingForceRefresh = true;
                this.requestEpoch++;
                configurationCancellation = this.requestCancellation;
                this.requestCancellation = null;
                this.requestRunning = false;
            }
            DateTime dueUtc = this.nextProbeDueUtc == DateTime.MinValue && this.lastProbeStartedUtc != DateTime.MinValue
                ? this.lastProbeStartedUtc.AddMinutes(intervalMinutes)
                : this.nextProbeDueUtc;
            due = this.lastProbeStartedUtc == DateTime.MinValue ||
                dueUtc == DateTime.MinValue ||
                now >= dueUtc;

            if (manualRefresh)
            {
                if (this.lastManualRefreshAcceptedUtc == DateTime.MinValue ||
                    (now - this.lastManualRefreshAcceptedUtc) >= ManualRefreshCooldown)
                {
                    manualAccepted = true;
                }
                else
                {
                    // A token rejected by the explicit cooldown is intentionally consumed. A
                    // token blocked only by single-flight remains pending until a slot is owned.
                    this.lastManualRefreshToken = manualToken;
                }
            }
            if (manualAccepted || regionChanged || targetsChanged || due || this.pendingForceRefresh)
            {
                shouldStart = true;
            }
        }

        CancelRequest(configurationCancellation);

        if (shouldStart)
        {
            StartProbe(
                now,
                regionMask,
                intervalMinutes,
                manualToken,
                manualAccepted,
                regionChanged,
                targetsChanged,
                localNetworkDegraded,
                localNetworkDegradedReason,
                targetConfiguration,
                targetSignature,
                networkIdentitySignature);
        }

        lock (this.sync)
        {
            return CloneSnapshots(this.snapshots);
        }
    }

    private void StartProbe(
        DateTime now,
        int regionMask,
        int intervalMinutes,
        int manualToken,
        bool manualAccepted,
        bool regionChanged,
        bool targetsChanged,
        bool localNetworkDegraded,
        string localNetworkDegradedReason,
        string[] targetConfiguration,
        string targetSignature,
        string networkIdentitySignature)
    {
        CloudEndpointSnapshot[] previous;
        CancellationTokenSource cancellation = new CancellationTokenSource();
        bool forceRefresh;
        string trigger;
        long requestEpochAtStart;
        lock (this.sync)
        {
            if (!string.Equals(this.currentNetworkIdentitySignature, networkIdentitySignature, StringComparison.Ordinal) ||
                !string.Equals(this.lastTargetConfigurationSignature, targetSignature, StringComparison.Ordinal) ||
                !TryAcquireProbeStart(
                    ref this.requestRunning,
                    ref this.pendingForceRefresh,
                    manualAccepted,
                    manualToken,
                    ref this.lastManualRefreshToken,
                    out forceRefresh))
            {
                cancellation.Dispose();
                return;
            }

            trigger = manualAccepted
                ? "云服务手动刷新"
                : (regionChanged
                    ? "云服务地区设置变化"
                    : (targetsChanged ? "云服务检测列表变化" : SelectAutomaticTrigger(this.pendingForcedTrigger)));
            this.pendingForcedTrigger = string.Empty;
            if (manualAccepted)
            {
                this.lastManualRefreshAcceptedUtc = now;
            }

            this.lastProbeStartedUtc = now;
            this.nextProbeDueUtc = now.AddMinutes(intervalMinutes);
            this.lastCloudStatusRegionMask = regionMask;
            this.requestCancellation = cancellation;
            requestEpochAtStart = this.requestEpoch;
            previous = CloneSnapshots(this.snapshots);
            this.snapshots = CloudEndpointProbe.CreateCheckingSnapshots(previous, targetConfiguration);
        }

        Task.Run(async delegate
        {
            CloudEndpointSnapshot[] result;
            List<string> logLines = new List<string>();
            bool cancelled = false;
            try
            {
                result = await CloudEndpointProbe.RunAsync(
                        logLines,
                        regionMask,
                        previous,
                        forceRefresh,
                        regionChanged,
                        localNetworkDegraded,
                        localNetworkDegradedReason,
                        targetConfiguration,
                        cancellation.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                logLines.Add("云服务探测取消");
                result = CloneSnapshots(previous);
            }
            catch (Exception ex)
            {
                logLines.Add("云服务探测异常: " + ex.GetType().Name + " " + ex.Message);
                result = CreateFailureSnapshots(ex, targetConfiguration);
            }

            bool shouldWriteDetailedLog = false;
            bool staleResult = false;
            CloudEndpointSnapshot[] historySnapshots = null;
            string historyTrigger = trigger ?? "自动检测";
            lock (this.sync)
            {
                bool ownsRequestSlot = object.ReferenceEquals(this.requestCancellation, cancellation);
                if (ownsRequestSlot)
                {
                    this.requestRunning = false;
                    this.requestCancellation = null;
                }

                if (!ownsRequestSlot ||
                    !IsRequestIdentityCurrent(
                        this.requestEpoch,
                        this.currentNetworkIdentitySignature,
                        this.lastTargetConfigurationSignature,
                        requestEpochAtStart,
                        networkIdentitySignature,
                        targetSignature))
                {
                    staleResult = true;
                }
                else
                {
                    DateTime completedUtc = DateTime.UtcNow;
                    if (cancelled)
                    {
                        if (!HasUnavailableSnapshots(this.snapshots))
                        {
                            this.snapshots = CloneSnapshots(previous);
                        }

                        // Cancellation can be caused by a confirmed offline transition. Do not
                        // erase the schedule here; explicit RequestRefresh already cleared the
                        // timestamps before cancellation when an immediate retry is intended.
                        ClearPendingStateLocked();
                    }
                    else
                    {
                        bool stateConfirmationPending;
                        CloudEndpointSnapshot[] committed = ApplyStateHysteresisLocked(result, previous, forceRefresh, regionChanged, completedUtc, logLines, out stateConfirmationPending);
                        bool stateChanged = !HasSameStateSignature(previous, committed);

                        this.snapshots = committed;
                        this.nextProbeDueUtc = stateConfirmationPending
                            ? completedUtc.Add(StateConfirmationDelay)
                            : completedUtc.AddMinutes(intervalMinutes);

                        shouldWriteDetailedLog =
                            forceRefresh ||
                            regionChanged ||
                            stateChanged ||
                            this.lastDetailedLogUtc == DateTime.MinValue ||
                            completedUtc - this.lastDetailedLogUtc >= DetailedLogInterval;
                        if (shouldWriteDetailedLog)
                        {
                            this.lastDetailedLogUtc = completedUtc;
                        }

                        historySnapshots = CloneSnapshots(committed);
                    }
                }
            }

            if (!staleResult && historySnapshots != null)
            {
                NetworkCheckHistoryLogger.LogCompleted(
                    "network_monitor",
                    "cloud_endpoints",
                    historyTrigger,
                    BuildCloudEndpointsSummary(historySnapshots),
                    AreAllCloudEndpointsNormal(historySnapshots),
                    -1,
                    new Dictionary<string, object>
                    {
                        { "endpoint_count", historySnapshots.Length },
                        { "all_normal", AreAllCloudEndpointsNormal(historySnapshots) }
                    });
            }

            cancellation.Dispose();
            if (!staleResult && shouldWriteDetailedLog)
            {
                Logger.CloudEndpointProbe(trigger, logLines);
            }
        });
    }

    // Single-flight ownership is the commit point for refresh intent. Keeping this as a small
    // state transition makes token/force semantics testable without real network traffic.
    private static bool TryAcquireProbeStart(
        ref bool requestRunning,
        ref bool pendingForceRefresh,
        bool manualAccepted,
        int manualToken,
        ref int lastManualRefreshToken,
        out bool forceRefresh)
    {
        forceRefresh = false;
        if (requestRunning)
        {
            return false;
        }

        requestRunning = true;
        forceRefresh = pendingForceRefresh || manualAccepted;
        pendingForceRefresh = false;
        if (manualAccepted)
        {
            lastManualRefreshToken = manualToken;
        }

        return true;
    }

    private static bool IsRequestIdentityCurrent(
        long currentEpoch,
        string currentNetworkIdentitySignature,
        string currentTargetSignature,
        long requestEpoch,
        string requestNetworkIdentitySignature,
        string requestTargetSignature)
    {
        return currentEpoch == requestEpoch &&
            string.Equals(currentNetworkIdentitySignature, requestNetworkIdentitySignature, StringComparison.Ordinal) &&
            string.Equals(currentTargetSignature, requestTargetSignature, StringComparison.Ordinal);
    }

    private static string BuildNetworkBaseSignature(long networkGeneration, string interfaceId)
    {
        return networkGeneration.ToString(CultureInfo.InvariantCulture) + "|" +
            (interfaceId ?? string.Empty).Trim().ToUpperInvariant();
    }

    internal static void RunSelfTest()
    {
        bool running = true;
        bool pendingForce = true;
        int consumedToken = 10;
        bool force;
        if (TryAcquireProbeStart(ref running, ref pendingForce, true, 11, ref consumedToken, out force) ||
            !pendingForce || consumedToken != 10)
        {
            throw new InvalidOperationException("Cloud reader self-test: occupied single-flight consumed pending refresh state.");
        }

        running = false;
        if (!TryAcquireProbeStart(ref running, ref pendingForce, true, 11, ref consumedToken, out force) ||
            !force || pendingForce || consumedToken != 11)
        {
            throw new InvalidOperationException("Cloud reader self-test: pending force/token was not consumed exactly once.");
        }

        running = false;
        if (!TryAcquireProbeStart(ref running, ref pendingForce, false, 11, ref consumedToken, out force) || force)
        {
            throw new InvalidOperationException("Cloud reader self-test: consumed force flag was reused.");
        }

        if (IsRequestIdentityCurrent(3, "net-b", "targets", 2, "net-a", "targets") ||
            IsRequestIdentityCurrent(3, "net-b", "targets", 3, "net-b", "old-targets") ||
            !IsRequestIdentityCurrent(3, "net-b", "targets", 3, "net-b", "targets"))
        {
            throw new InvalidOperationException("Cloud reader self-test: stale request identity validation failed.");
        }

        Console.WriteLine("Cloud endpoint reader: PASS single-flight-token force-once request-identity");
    }

    private CloudEndpointSnapshot[] ApplyStateHysteresisLocked(
        CloudEndpointSnapshot[] result,
        CloudEndpointSnapshot[] previous,
        bool forceRefresh,
        bool regionChanged,
        DateTime nowUtc,
        List<string> logLines,
        out bool confirmationPending)
    {
        confirmationPending = false;
        if (forceRefresh || regionChanged || !HasKnownStableState(previous))
        {
            ClearPendingStateLocked();
            return result;
        }

        string previousSignature = BuildStateSignature(previous);
        string resultSignature = BuildStateSignature(result);
        if (string.Equals(previousSignature, resultSignature, StringComparison.Ordinal))
        {
            ClearPendingStateLocked();
            return result;
        }

        if (string.Equals(this.pendingStateSignature, resultSignature, StringComparison.Ordinal))
        {
            if (nowUtc - this.pendingStateFirstSeenUtc >= StateConfirmationDelay)
            {
                ClearPendingStateLocked();
                if (logLines != null)
                {
                    logLines.Add("云服务状态变化已确认: " + resultSignature);
                }

                return result;
            }

            confirmationPending = true;
            if (logLines != null)
            {
                logLines.Add("云服务状态变化等待确认: " + resultSignature);
            }

            return CloneSnapshots(previous);
        }

        this.pendingStateSignature = resultSignature;
        this.pendingStateFirstSeenUtc = nowUtc;
        confirmationPending = true;
        if (logLines != null)
        {
            logLines.Add("云服务状态变化首次出现，延迟确认: " + resultSignature);
        }

        return CloneSnapshots(previous);
    }

    private static string GetUnavailableNetworkReason(NetworkAccessState networkState)
    {
        if (networkState == NetworkAccessState.AdapterMissing)
        {
            return "网卡未识别";
        }

        if (networkState == NetworkAccessState.NeedsValidation)
        {
            return "需要验证";
        }

        if (networkState == NetworkAccessState.Unknown)
        {
            return "等待网络状态";
        }

        return "断网";
    }

    private static string NormalizeRefreshTrigger(string trigger, string fallback)
    {
        trigger = trigger == null ? string.Empty : trigger.Trim();
        return trigger.Length == 0 ? fallback : trigger;
    }

    private static string SelectAutomaticTrigger(string pendingForcedTrigger)
    {
        return string.IsNullOrWhiteSpace(pendingForcedTrigger)
            ? "云服务定时间隔"
            : pendingForcedTrigger.Trim();
    }

    private static void CancelRequest(CancellationTokenSource cancellation)
    {
        if (cancellation == null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static CloudEndpointSnapshot[] CreateUnavailableSnapshots(string reason, string[] targetConfiguration)
    {
        CloudEndpointSnapshot[] result = CloudEndpointProbe.CreateCheckingSnapshots(null, targetConfiguration);
        for (int i = 0; i < result.Length; i++)
        {
            result[i].Status = CloudEndpointStatus.Unknown;
            result[i].Reason = reason;
            result[i].AlertReason = reason;
        }

        return result;
    }

    private static CloudEndpointSnapshot[] CreateFailureSnapshots(Exception ex, string[] targetConfiguration)
    {
        CloudEndpointSnapshot[] result = CloudEndpointProbe.CreateCheckingSnapshots(null, targetConfiguration);
        string reason = "云服务检测异常 " + (ex == null ? "Unknown" : ex.GetType().Name);
        DateTime checkedAt = DateTime.Now;
        for (int i = 0; i < result.Length; i++)
        {
            result[i].Status = CloudEndpointStatus.Abnormal;
            result[i].CheckedAtLocal = checkedAt;
            result[i].CheckedAtKnown = true;
            result[i].Reason = reason;
            result[i].AlertReason = "状态API失败";
        }

        return result;
    }

    private void ClearPendingStateLocked()
    {
        this.pendingStateSignature = string.Empty;
        this.pendingStateFirstSeenUtc = DateTime.MinValue;
    }

    private static bool HasSameStateSignature(CloudEndpointSnapshot[] left, CloudEndpointSnapshot[] right)
    {
        return string.Equals(BuildStateSignature(left), BuildStateSignature(right), StringComparison.Ordinal);
    }

    private static bool HasKnownStableState(CloudEndpointSnapshot[] snapshots)
    {
        if (snapshots == null || snapshots.Length == 0)
        {
            return false;
        }

        bool sawKnown = false;
        for (int i = 0; i < snapshots.Length; i++)
        {
            CloudEndpointSnapshot snapshot = snapshots[i];
            if (snapshot == null ||
                snapshot.Status == CloudEndpointStatus.Unknown ||
                snapshot.Status == CloudEndpointStatus.Checking ||
                !snapshot.CheckedAtKnown)
            {
                return false;
            }

            sawKnown = true;
        }

        return sawKnown;
    }

    private static bool HasUnavailableSnapshots(CloudEndpointSnapshot[] snapshots)
    {
        if (snapshots == null || snapshots.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < snapshots.Length; i++)
        {
            CloudEndpointSnapshot snapshot = snapshots[i];
            if (snapshot == null ||
                snapshot.Status != CloudEndpointStatus.Unknown ||
                snapshot.CheckedAtKnown ||
                string.IsNullOrWhiteSpace(snapshot.Reason))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildStateSignature(CloudEndpointSnapshot[] snapshots)
    {
        if (snapshots == null || snapshots.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < snapshots.Length; i++)
        {
            CloudEndpointSnapshot snapshot = snapshots[i];
            if (i > 0)
            {
                builder.Append("|");
            }

            if (snapshot == null)
            {
                builder.Append("null");
                continue;
            }

            builder.Append(snapshot.Key ?? string.Empty);
            builder.Append(":");
            builder.Append(snapshot.Status.ToString());
            builder.Append(":");
            builder.Append(snapshot.AlertReason ?? string.Empty);
            builder.Append(":");
            builder.Append(snapshot.AlertName ?? string.Empty);
        }

        return builder.ToString();
    }

    private static string BuildCloudEndpointsSummary(CloudEndpointSnapshot[] snapshots)
    {
        if (snapshots == null || snapshots.Length == 0)
        {
            return "无端点";
        }

        int normal = 0;
        int slow = 0;
        int down = 0;
        int abnormal = 0;
        for (int i = 0; i < snapshots.Length; i++)
        {
            if (snapshots[i] == null) continue;
            switch (snapshots[i].Status)
            {
                case CloudEndpointStatus.Normal: normal++; break;
                case CloudEndpointStatus.Slow: slow++; break;
                case CloudEndpointStatus.Down: down++; break;
                default: abnormal++; break;
            }
        }

        return string.Format("正常{0} 慢{1} 断{2} 异常{3}", normal, slow, down, abnormal);
    }

    private static bool AreAllCloudEndpointsNormal(CloudEndpointSnapshot[] snapshots)
    {
        if (snapshots == null || snapshots.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < snapshots.Length; i++)
        {
            if (snapshots[i] == null || snapshots[i].Status != CloudEndpointStatus.Normal)
            {
                return false;
            }
        }

        return true;
    }

    private static CloudEndpointSnapshot[] CloneSnapshots(CloudEndpointSnapshot[] source)
    {
        if (source == null)
        {
            return CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Unknown);
        }

        if (source.Length == 0)
        {
            return new CloudEndpointSnapshot[0];
        }

        CloudEndpointSnapshot[] result = new CloudEndpointSnapshot[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            result[i] = source[i] == null ? new CloudEndpointSnapshot() : source[i].Clone();
        }

        return result;
    }
}
