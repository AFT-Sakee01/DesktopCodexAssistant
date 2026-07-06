using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class GfwProbeReader
{
    private const int DefaultTimeoutMs = 2800;
    private const int MinControlPassCount = 1;
    private static readonly string[] ControlDomains = new string[]
    {
        "www.microsoft.com",
        "www.bing.com"
    };

    private static readonly string[] CandidateDomains = new string[]
    {
        "www.google.com",
        "www.youtube.com",
        "x.com"
    };

    private readonly object sync = new object();
    private GfwProbeSnapshot snapshot = new GfwProbeSnapshot();
    private DateTime lastProbeStartedUtc;
    private DateTime lastDetailedLogUtc;
    private bool requestRunning;
    private bool localNetworkGateActive;
    private int lastManualRefreshToken;
    private string pendingForcedTrigger = string.Empty;

    public void RequestRefresh()
    {
        RequestRefresh("强制刷新");
    }

    public void RequestRefresh(string trigger)
    {
        lock (this.sync)
        {
            this.lastProbeStartedUtc = DateTime.MinValue;
            this.pendingForcedTrigger = NormalizeRefreshTrigger(trigger);
        }
    }

    public GfwProbeSnapshot GetSnapshot(
        WidgetSettings settings,
        NetworkAccessState networkState,
        bool localNetworkDegraded,
        string localNetworkDegradedReason)
    {
        if (settings == null || !settings.GfwProbeEnabled)
        {
            lock (this.sync)
            {
                this.snapshot = new GfwProbeSnapshot
                {
                    Enabled = false,
                    Running = false,
                    Status = GfwProbeStatus.Disabled,
                    Detail = "关闭",
                    Reason = string.Empty
                };
                this.localNetworkGateActive = false;

                return this.snapshot.Clone();
            }
        }

        // Do not spend DNS/TCP/TLS/HTTP probes when Internet access is not established.
        // Unknown is often a short bridge while NetworkMonitorReader verifies a Windows
        // network event. Preserve the in-memory schedule so a transient state cannot turn
        // the next online tick into another first automatic probe.
        if (networkState != NetworkAccessState.Online)
        {
            lock (this.sync)
            {
                this.localNetworkGateActive = false;
                return CreateUnavailableNetworkClone(this.snapshot, this.requestRunning, networkState);
            }
        }

        if (localNetworkDegraded)
        {
            lock (this.sync)
            {
                this.localNetworkGateActive = true;
                return CreateLocalNetworkDegradedClone(this.snapshot, this.requestRunning, localNetworkDegradedReason);
            }
        }

        DateTime now = DateTime.UtcNow;
        bool shouldStart = false;
        string startTrigger = string.Empty;
        lock (this.sync)
        {
            this.localNetworkGateActive = false;
            bool manualRefresh = settings.GfwProbeManualRefreshToken != this.lastManualRefreshToken;
            int intervalMinutes = Math.Max(WidgetSettings.MinGfwProbeIntervalMinutes, settings.GfwProbeIntervalMinutes);
            bool due = this.lastProbeStartedUtc == DateTime.MinValue ||
                (now - this.lastProbeStartedUtc).TotalMinutes >= intervalMinutes;

            if (manualRefresh || due)
            {
                shouldStart = true;
                startTrigger = manualRefresh
                    ? "手动测试按钮"
                    : SelectAutomaticTrigger(this.lastProbeStartedUtc, this.pendingForcedTrigger);
                this.lastManualRefreshToken = settings.GfwProbeManualRefreshToken;
                this.pendingForcedTrigger = string.Empty;
            }
        }

        if (shouldStart)
        {
            StartProbe(now, startTrigger);
        }

        lock (this.sync)
        {
            GfwProbeSnapshot clone = this.snapshot.Clone();
            clone.Enabled = true;
            clone.Running = this.requestRunning;
            if (clone.Status == GfwProbeStatus.Disabled)
            {
                clone.Status = this.requestRunning ? GfwProbeStatus.Checking : GfwProbeStatus.Unknown;
                clone.Detail = this.requestRunning ? "检测中" : "等待检测";
                clone.Reason = string.Empty;
            }

            return clone;
        }
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

    private static string NormalizeRefreshTrigger(string trigger)
    {
        trigger = trigger == null ? string.Empty : trigger.Trim();
        return trigger.Length == 0 ? "强制刷新" : trigger;
    }

    private static string SelectAutomaticTrigger(DateTime lastProbeStartedUtc, string pendingForcedTrigger)
    {
        if (!string.IsNullOrWhiteSpace(pendingForcedTrigger))
        {
            return pendingForcedTrigger.Trim();
        }

        return lastProbeStartedUtc == DateTime.MinValue ? "首次自动检测" : "定时间隔";
    }

    private static bool HasKnownProbeSnapshot(GfwProbeSnapshot value)
    {
        return value != null &&
            value.Enabled &&
            value.CheckedAtKnown &&
            value.Status != GfwProbeStatus.Disabled &&
            value.Status != GfwProbeStatus.Unknown;
    }

    private static GfwProbeSnapshot CreateUnavailableNetworkClone(
        GfwProbeSnapshot current,
        bool requestRunning,
        NetworkAccessState networkState)
    {
        GfwProbeSnapshot clone = current == null ? new GfwProbeSnapshot() : current.Clone();
        if (networkState == NetworkAccessState.Unknown && HasKnownProbeSnapshot(clone))
        {
            clone.Enabled = true;
            clone.Running = requestRunning;
            return clone;
        }

        string reason = GetUnavailableNetworkReason(networkState);
        clone.Enabled = true;
        clone.Running = false;
        clone.Status = GfwProbeStatus.Inconclusive;
        clone.Detail = "不可判定";
        clone.Reason = reason;
        clone.CheckedAtLocal = DateTime.MinValue;
        clone.CheckedAtKnown = false;
        clone.DomainsTested = 0;
        clone.AnomalyCount = 0;
        clone.CloudEndpoints = CreateUnavailableCloudEndpointSnapshots(reason);
        return clone;
    }

    private static GfwProbeSnapshot CreateLocalNetworkDegradedClone(
        GfwProbeSnapshot current,
        bool requestRunning,
        string reason)
    {
        GfwProbeSnapshot clone = current == null ? new GfwProbeSnapshot() : current.Clone();
        clone.Enabled = true;
        clone.Running = requestRunning;
        clone.Status = GfwProbeStatus.Inconclusive;
        clone.Detail = "不可判定";
        clone.Reason = FormatLocalNetworkDegradedReason(reason);
        clone.CheckedAtLocal = DateTime.Now;
        clone.CheckedAtKnown = true;
        clone.DomainsTested = 0;
        clone.AnomalyCount = 0;
        return clone;
    }

    private static CloudEndpointSnapshot[] CreateUnavailableCloudEndpointSnapshots(string reason)
    {
        CloudEndpointSnapshot[] snapshots = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Unknown);
        for (int i = 0; i < snapshots.Length; i++)
        {
            snapshots[i].Reason = reason;
            snapshots[i].AlertReason = reason;
        }

        return snapshots;
    }

    private void StartProbe(DateTime now, string trigger)
    {
        lock (this.sync)
        {
            if (this.requestRunning)
            {
                return;
            }

            this.requestRunning = true;
            this.lastProbeStartedUtc = now;
            this.snapshot.Enabled = true;
            this.snapshot.Running = true;
            if (!this.snapshot.CheckedAtKnown)
            {
                this.snapshot.Status = GfwProbeStatus.Checking;
                this.snapshot.Detail = "检测中";
                this.snapshot.Reason = string.Empty;
            }
        }

        Task.Run(delegate
        {
            GfwProbeSnapshot result;
            List<string> logLines = new List<string>();
            try
            {
                result = RunProbe(logLines);
            }
            catch (Exception ex)
            {
                logLines.Add("探测异常: " + ex.GetType().Name + " " + ex.Message);
                result = new GfwProbeSnapshot
                {
                    Enabled = true,
                    Running = false,
                    Status = GfwProbeStatus.Inconclusive,
                    Detail = "不可判定",
                    Reason = "探测失败 " + ex.GetType().Name,
                    CheckedAtLocal = DateTime.Now,
                    CheckedAtKnown = true
                };
            }

            logLines.Add("总结: 结果=" + result.Detail + " 理由=" + result.Reason + " 异常=" +
                result.AnomalyCount.ToString(CultureInfo.InvariantCulture) + "/" +
                result.DomainsTested.ToString(CultureInfo.InvariantCulture));

            bool shouldWriteDetailedLog;
            lock (this.sync)
            {
                if (this.localNetworkGateActive)
                {
                    this.requestRunning = false;
                    return;
                }

                // Stable automatic probes remain in memory and only checkpoint every six
                // hours. Manual probes, first results, and state transitions remain immediate.
                bool manualProbe = string.Equals(trigger, "手动测试按钮", StringComparison.Ordinal);
                bool stateChanged =
                    !this.snapshot.CheckedAtKnown ||
                    this.snapshot.Status != result.Status ||
                    !string.Equals(this.snapshot.Reason, result.Reason, StringComparison.Ordinal);
                shouldWriteDetailedLog =
                    manualProbe ||
                    stateChanged ||
                    this.lastDetailedLogUtc == DateTime.MinValue ||
                    (DateTime.UtcNow - this.lastDetailedLogUtc).TotalHours >= 6.0;
                if (shouldWriteDetailedLog)
                {
                    this.lastDetailedLogUtc = DateTime.UtcNow;
                }

                result.Enabled = true;
                result.Running = false;
                this.snapshot = result;
                this.requestRunning = false;
            }

            NetworkCheckHistoryLogger.LogCompleted(
                "network_monitor",
                "gfw",
                trigger ?? "自动检测",
                result.Detail + " " + result.Reason,
                result.Status == GfwProbeStatus.Normal || result.Status == GfwProbeStatus.Inconclusive,
                -1,
                new Dictionary<string, object>
                {
                    { "status", result.Status.ToString() },
                    { "anomaly_count", result.AnomalyCount },
                    { "domains_tested", result.DomainsTested }
                });

            if (shouldWriteDetailedLog)
            {
                Logger.GfwProbe(trigger, logLines);
            }
        });
    }

    private static GfwProbeSnapshot RunProbe(List<string> logLines)
    {
        int controlPasses = 0;
        List<DomainProbeResult> controlResults = new List<DomainProbeResult>();
        logLines.Add("控制站点:");
        for (int i = 0; i < ControlDomains.Length; i++)
        {
            DomainProbeResult control = ProbeDomain(ControlDomains[i], false);
            controlResults.Add(control);
            logLines.Add("  " + FormatProbeResult(control));
            if (control.HttpOk || control.TlsOk)
            {
                controlPasses++;
            }
        }

        logLines.Add("控制站点通过: " + controlPasses.ToString(CultureInfo.InvariantCulture) + "/" + ControlDomains.Length.ToString(CultureInfo.InvariantCulture));

        if (controlPasses < MinControlPassCount)
        {
            return new GfwProbeSnapshot
            {
                Enabled = true,
                Running = false,
                Status = GfwProbeStatus.Inconclusive,
                Detail = "不可判定",
                Reason = "控制站点不可用",
                CheckedAtLocal = DateTime.Now,
                CheckedAtKnown = true,
                DomainsTested = ControlDomains.Length,
                AnomalyCount = ControlDomains.Length
            };
        }

        ProbeSummary summary = new ProbeSummary();
        logLines.Add("候选站点:");
        for (int i = 0; i < CandidateDomains.Length; i++)
        {
            DomainProbeResult result = ProbeDomain(CandidateDomains[i], true);
            logLines.Add("  " + FormatProbeResult(result));
            summary.DomainsTested++;
            if (result.HasDnsAnomaly)
            {
                summary.DnsAnomalies++;
            }
            else if (result.HasTcpAnomaly)
            {
                summary.TcpAnomalies++;
            }
            else if (result.HasTlsAnomaly)
            {
                summary.TlsAnomalies++;
            }
            else if (result.HasHttpAnomaly)
            {
                summary.HttpAnomalies++;
            }
        }

        return BuildSnapshot(summary);
    }

    private static GfwProbeSnapshot BuildSnapshot(ProbeSummary summary)
    {
        GfwProbeStatus status = GfwProbeStatus.Normal;
        string detail = "正常";
        string reason = "控制站点可用，候选站点无异常";
        int anomalies = summary.DnsAnomalies + summary.TcpAnomalies + summary.TlsAnomalies + summary.HttpAnomalies;
        if (anomalies > 0)
        {
            int maxLayerAnomalies = Math.Max(
                Math.Max(summary.DnsAnomalies, summary.TcpAnomalies),
                Math.Max(summary.TlsAnomalies, summary.HttpAnomalies));
            if (maxLayerAnomalies < 2)
            {
                return new GfwProbeSnapshot
                {
                    Enabled = true,
                    Running = false,
                    Status = GfwProbeStatus.Inconclusive,
                    Detail = "不可判定",
                    Reason = "候选站点少量异常 " + FormatCount(anomalies, summary.DomainsTested),
                    CheckedAtLocal = DateTime.Now,
                    CheckedAtKnown = true,
                    DomainsTested = summary.DomainsTested,
                    AnomalyCount = anomalies
                };
            }

            if (summary.DnsAnomalies >= summary.TcpAnomalies &&
                summary.DnsAnomalies >= summary.TlsAnomalies &&
                summary.DnsAnomalies >= summary.HttpAnomalies)
            {
                status = GfwProbeStatus.SuspectedDns;
                detail = "疑似DNS";
                reason = "系统DNS失败但DoH可解析 " + FormatCount(summary.DnsAnomalies, summary.DomainsTested);
            }
            else if (summary.TlsAnomalies >= summary.TcpAnomalies &&
                     summary.TlsAnomalies >= summary.HttpAnomalies)
            {
                status = GfwProbeStatus.SuspectedTlsSni;
                detail = "疑似SNI";
                reason = "TCP可连但TLS/SNI握手失败 " + FormatCount(summary.TlsAnomalies, summary.DomainsTested);
            }
            else if (summary.TcpAnomalies >= summary.HttpAnomalies)
            {
                status = GfwProbeStatus.SuspectedTcp;
                detail = "疑似连接";
                reason = "443连接失败 " + FormatCount(summary.TcpAnomalies, summary.DomainsTested);
            }
            else
            {
                status = GfwProbeStatus.SuspectedHttp;
                detail = "疑似HTTP";
                reason = "HTTPS响应异常 " + FormatCount(summary.HttpAnomalies, summary.DomainsTested);
            }
        }

        return new GfwProbeSnapshot
        {
            Enabled = true,
            Running = false,
            Status = status,
            Detail = detail,
            Reason = reason,
            CheckedAtLocal = DateTime.Now,
            CheckedAtKnown = true,
            DomainsTested = summary.DomainsTested,
            AnomalyCount = anomalies
        };
    }

    private static string FormatCount(int count, int total)
    {
        return count.ToString(CultureInfo.InvariantCulture) + "/" + total.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatLocalNetworkDegradedReason(string reason)
    {
        return string.IsNullOrWhiteSpace(reason) ? "本地网络不稳定" : reason.Trim();
    }

    private static string FormatProbeResult(DomainProbeResult result)
    {
        if (result == null)
        {
            return "null";
        }

        StringBuilder builder = new StringBuilder();
        builder.Append(result.Domain);
        builder.Append(" DNS=");
        builder.Append(result.SystemDnsOk ? "OK" : "FAIL");
        builder.Append(" 系统IP=");
        builder.Append(FormatAddressList(result.SystemAddresses));
        builder.Append(" DoH=");
        builder.Append(result.DohOk ? "OK" : "FAIL");
        builder.Append(" DoH-IP=");
        builder.Append(FormatAddressList(result.DohAddresses));
        builder.Append(" TCP443=");
        builder.Append(result.TcpOk ? "OK" : "FAIL");
        if (!string.IsNullOrEmpty(result.TcpError))
        {
            builder.Append("(");
            builder.Append(result.TcpError);
            builder.Append(")");
        }

        builder.Append(" TLS/SNI=");
        builder.Append(result.TlsOk ? "OK" : "FAIL");
        if (!string.IsNullOrEmpty(result.TlsError))
        {
            builder.Append("(");
            builder.Append(result.TlsError);
            builder.Append(")");
        }

        builder.Append(" HTTPS=");
        builder.Append(result.HttpOk ? "OK" : "FAIL");
        if (result.HttpStatusCode > 0)
        {
            builder.Append("(");
            builder.Append(result.HttpStatusCode.ToString(CultureInfo.InvariantCulture));
            builder.Append(")");
        }
        else if (!string.IsNullOrEmpty(result.HttpError))
        {
            builder.Append("(");
            builder.Append(result.HttpError);
            builder.Append(")");
        }

        builder.Append(" 异常=");
        builder.Append(FormatAnomaly(result));
        return builder.ToString();
    }

    private static string FormatAnomaly(DomainProbeResult result)
    {
        if (result.HasDnsAnomaly)
        {
            return "DNS";
        }

        if (result.HasTcpAnomaly)
        {
            return "TCP";
        }

        if (result.HasTlsAnomaly)
        {
            return "TLS/SNI";
        }

        if (result.HasHttpAnomaly)
        {
            return "HTTP";
        }

        return "无";
    }

    private static string FormatAddressList(List<string> addresses)
    {
        if (addresses == null || addresses.Count == 0)
        {
            return "[]";
        }

        StringBuilder builder = new StringBuilder();
        builder.Append("[");
        int count = Math.Min(3, addresses.Count);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(",");
            }

            builder.Append(addresses[i]);
        }

        if (addresses.Count > count)
        {
            builder.Append(",+");
            builder.Append((addresses.Count - count).ToString(CultureInfo.InvariantCulture));
        }

        builder.Append("]");
        return builder.ToString();
    }

    private static DomainProbeResult ProbeDomain(string domain, bool candidate)
    {
        DomainProbeResult result = new DomainProbeResult();
        result.Domain = domain;
        result.SystemDnsOk = TryResolveSystemIpv4(domain, out result.SystemAddresses);
        result.DohOk = TryResolveDohIpv4(domain, out result.DohAddresses);

        if (candidate && !result.SystemDnsOk && result.DohOk)
        {
            result.HasDnsAnomaly = true;
            return result;
        }

        result.TcpOk = TryTcpConnect(domain, 443, DefaultTimeoutMs, out result.TcpError);
        if (!result.TcpOk)
        {
            if (candidate && (result.SystemDnsOk || result.DohOk))
            {
                result.HasTcpAnomaly = true;
            }

            return result;
        }

        result.TlsOk = TryTlsHandshake(domain, DefaultTimeoutMs, out result.TlsError);
        if (!result.TlsOk)
        {
            if (candidate)
            {
                result.HasTlsAnomaly = true;
            }

            return result;
        }

        result.HttpOk = TryHttpHead(domain, DefaultTimeoutMs, out result.HttpStatusCode, out result.HttpError);
        if (!result.HttpOk && candidate)
        {
            result.HasHttpAnomaly = true;
        }

        return result;
    }

    private static bool TryResolveSystemIpv4(string domain, out List<string> addresses)
    {
        addresses = new List<string>();
        try
        {
            IPAddress[] resolved = Dns.GetHostAddresses(domain);
            for (int i = 0; i < resolved.Length; i++)
            {
                if (resolved[i] != null && resolved[i].AddressFamily == AddressFamily.InterNetwork)
                {
                    AddDistinct(addresses, resolved[i].ToString());
                }
            }

            return addresses.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveDohIpv4(string domain, out List<string> addresses)
    {
        addresses = new List<string>();
        try
        {
            string url = "https://cloudflare-dns.com/dns-query?name=" + Uri.EscapeDataString(domain) + "&type=A";
            string json = FetchText(url, "application/dns-json", DefaultTimeoutMs);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            DnsJsonResponse response = serializer.Deserialize<DnsJsonResponse>(json);
            if (response == null || response.Answer == null)
            {
                return false;
            }

            for (int i = 0; i < response.Answer.Length; i++)
            {
                DnsJsonAnswer answer = response.Answer[i];
                IPAddress address;
                if (answer != null &&
                    answer.type == 1 &&
                    !string.IsNullOrWhiteSpace(answer.data) &&
                    IPAddress.TryParse(answer.data.Trim(), out address) &&
                    address.AddressFamily == AddressFamily.InterNetwork)
                {
                    AddDistinct(addresses, address.ToString());
                }
            }

            return addresses.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTcpConnect(string host, int port, int timeoutMs, out string error)
    {
        error = string.Empty;
        TcpClient client = null;
        try
        {
            client = new TcpClient();
            IAsyncResult result = client.BeginConnect(host, port, null, null);
            if (!result.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                error = "Timeout";
                return false;
            }

            client.EndConnect(result);
            return client.Connected;
        }
        catch (Exception ex)
        {
            error = FormatException(ex);
            return false;
        }
        finally
        {
            if (client != null)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }
        }
    }

    private static bool TryTlsHandshake(string host, int timeoutMs, out string error)
    {
        error = string.Empty;
        TcpClient client = null;
        SslStream ssl = null;
        try
        {
            client = new TcpClient();
            IAsyncResult connect = client.BeginConnect(host, 443, null, null);
            if (!connect.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                error = "TcpTimeout";
                return false;
            }

            client.EndConnect(connect);
            ssl = new SslStream(client.GetStream(), false, ValidateAnyCertificate);
            IAsyncResult auth = ssl.BeginAuthenticateAsClient(
                host,
                new X509CertificateCollection(),
                SslProtocols.Tls12,
                false,
                null,
                null);
            if (!auth.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                error = "TlsTimeout";
                return false;
            }

            ssl.EndAuthenticateAsClient(auth);
            return ssl.IsAuthenticated;
        }
        catch (AuthenticationException ex)
        {
            error = FormatException(ex);
            return false;
        }
        catch (Exception ex)
        {
            error = FormatException(ex);
            return false;
        }
        finally
        {
            if (ssl != null)
            {
                try
                {
                    ssl.Close();
                }
                catch
                {
                }
            }

            if (client != null)
            {
                try
                {
                    client.Close();
                }
                catch
                {
                }
            }
        }
    }

    private static bool TryHttpHead(string host, int timeoutMs, out int statusCode, out string error)
    {
        statusCode = 0;
        error = string.Empty;
        try
        {
            EnsureTls12();
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://" + host + "/");
            request.Method = "HEAD";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AllowAutoRedirect = false;
            request.UserAgent = ProductIdentity.UserAgent;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                statusCode = (int)response.StatusCode;
                return statusCode >= 200 && statusCode < 500 && statusCode != 451;
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                using (response)
                {
                    statusCode = (int)response.StatusCode;
                    return statusCode >= 200 && statusCode < 500 && statusCode != 451;
                }
            }

            error = ex.GetType().Name;
            return false;
        }
        catch (Exception ex)
        {
            error = FormatException(ex);
            return false;
        }
    }

    private static string FetchText(string url, string accept, int timeoutMs)
    {
        EnsureTls12();
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Timeout = timeoutMs;
        request.ReadWriteTimeout = timeoutMs;
        request.UserAgent = ProductIdentity.UserAgent;
        if (!string.IsNullOrEmpty(accept))
        {
            request.Accept = accept;
        }

        using (WebResponse response = request.GetResponse())
        using (System.IO.Stream stream = response.GetResponseStream())
        using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private static void EnsureTls12()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    }

    private static string FormatException(Exception ex)
    {
        if (ex == null)
        {
            return string.Empty;
        }

        string message = ex.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            return ex.GetType().Name;
        }

        message = message.Replace("\r", " ").Replace("\n", " ").Trim();
        if (message.Length > 80)
        {
            message = message.Substring(0, 80);
        }

        return ex.GetType().Name + ":" + message;
    }

    private static bool ValidateAnyCertificate(
        object sender,
        X509Certificate certificate,
        X509Chain chain,
        SslPolicyErrors sslPolicyErrors)
    {
        return true;
    }

    private static void AddDistinct(List<string> values, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        for (int i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        values.Add(value);
    }

    private sealed class DnsJsonResponse
    {
        public int Status { get; set; }
        public DnsJsonAnswer[] Answer { get; set; }
    }

    private sealed class DnsJsonAnswer
    {
        public int type { get; set; }
        public string data { get; set; }
    }

    private sealed class DomainProbeResult
    {
        public string Domain;
        public bool SystemDnsOk;
        public bool DohOk;
        public bool TcpOk;
        public bool TlsOk;
        public bool HttpOk;
        public bool HasDnsAnomaly;
        public bool HasTcpAnomaly;
        public bool HasTlsAnomaly;
        public bool HasHttpAnomaly;
        public int HttpStatusCode;
        public string TcpError;
        public string TlsError;
        public string HttpError;
        public List<string> SystemAddresses;
        public List<string> DohAddresses;
    }

    private struct ProbeSummary
    {
        public int DomainsTested;
        public int DnsAnomalies;
        public int TcpAnomalies;
        public int TlsAnomalies;
        public int HttpAnomalies;
    }
}
