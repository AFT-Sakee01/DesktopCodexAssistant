using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class CleanIpConnectionReader : IDisposable
{
    private const int RequestTimeoutMs = 9000;
    private const int HourlyRefreshJitterWindowSeconds = 300;
    private const int ScheduledRetrySlotMinutes = 10;
    private const string CleanIpMeUrl = "https://cleanip.io/api/v2/me";
    private readonly object sync = new object();
    private readonly Random random = new Random();
    private CleanIpConnectionSnapshot snapshot = new CleanIpConnectionSnapshot();
    private DateTime lastRefreshUtc;
    private DateTime nextHourlyRefreshLocal;
    private DateTime lastErrorRetrySlotLocal;
    private bool networkStateKnown;
    private bool lastNetworkConnected;
    private int lastManualRefreshToken;
    private bool requestRunning;
    private bool forceRefreshRequested;
    private bool networkStateRefreshRequested = true;
    private bool disposed;
    private CleanIpBadgeTestMode lastTestMode = CleanIpBadgeTestMode.Off;
    private int lastTestManualRefreshToken;
    private CleanIpConnectionSnapshot testSnapshot;

    public CleanIpConnectionReader()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public void RequestRefresh()
    {
        lock (this.sync)
        {
            this.forceRefreshRequested = true;
        }
    }

    public CleanIpConnectionSnapshot GetSnapshot(WidgetSettings settings)
    {
        CleanIpBadgeTestMode testMode = settings == null ? CleanIpBadgeTestMode.Off : settings.CleanIpBadgeTestMode;
        int manualRefreshToken = settings == null ? 0 : settings.ConnectionCheckManualRefreshToken;
        if (testMode != CleanIpBadgeTestMode.Off)
        {
            lock (this.sync)
            {
                // Keep test snapshots stable so their timestamp does not force a redraw every UI tick.
                if (this.testSnapshot == null ||
                    this.lastTestMode != testMode ||
                    this.lastTestManualRefreshToken != manualRefreshToken)
                {
                    this.testSnapshot = BuildTestSnapshot(testMode);
                    this.lastTestMode = testMode;
                    this.lastTestManualRefreshToken = manualRefreshToken;
                }

                return this.testSnapshot.Clone();
            }
        }

        DateTime now = DateTime.UtcNow;
        DateTime nowLocal = DateTime.Now;
        string trigger = string.Empty;
        CleanIpConnectionSnapshot clone;
        bool shouldStart = false;

        lock (this.sync)
        {
            bool networkConnected = this.lastNetworkConnected;
            if (!this.networkStateKnown || this.networkStateRefreshRequested)
            {
                // Query the system only at startup or after a NetworkChange notification.
                networkConnected = IsNetworkConnected();
                this.networkStateRefreshRequested = false;
            }

            if (!networkConnected)
            {
                this.networkStateKnown = true;
                this.lastNetworkConnected = false;
                if (!this.requestRunning)
                {
                    this.snapshot = BuildErrorSnapshot("断网: 未检测到可用网络", nowLocal);
                }

                clone = this.snapshot.Clone();
                clone.Running = this.requestRunning;
                return clone;
            }

            bool connectedNow = !this.networkStateKnown || !this.lastNetworkConnected;
            this.networkStateKnown = true;
            this.lastNetworkConnected = true;

            bool manualRefresh = manualRefreshToken != this.lastManualRefreshToken;
            bool firstRefresh = this.lastRefreshUtc == DateTime.MinValue || !this.snapshot.CheckedAtKnown;
            int refreshIntervalSeconds = settings == null
                ? WidgetSettings.DefaultConnectionCheckIntervalSeconds
                : settings.ConnectionCheckIntervalSeconds;
            refreshIntervalSeconds = Math.Max(
                WidgetSettings.MinConnectionCheckIntervalSeconds,
                Math.Min(WidgetSettings.MaxConnectionCheckIntervalSeconds, refreshIntervalSeconds));
            bool configuredRefresh = !firstRefresh && (now - this.lastRefreshUtc).TotalSeconds >= refreshIntervalSeconds;
            bool hourlyRefresh = this.nextHourlyRefreshLocal != DateTime.MinValue && nowLocal >= this.nextHourlyRefreshLocal;
            bool errorRetry = this.snapshot.CheckedAtKnown && !this.snapshot.Success && IsErrorRetrySlotDue(nowLocal);
            bool forcedRefresh = this.forceRefreshRequested;

            if (!this.requestRunning && (firstRefresh || connectedNow || manualRefresh || configuredRefresh || hourlyRefresh || errorRetry || forcedRefresh))
            {
                this.requestRunning = true;
                this.forceRefreshRequested = false;
                this.lastRefreshUtc = now;
                this.snapshot.Running = true;
                shouldStart = true;

                if (forcedRefresh)
                {
                    trigger = "操作面板刷新";
                }
                else if (manualRefresh)
                {
                    this.lastManualRefreshToken = manualRefreshToken;
                    trigger = "手动刷新";
                }
                else if (configuredRefresh)
                {
                    trigger = "定时间隔";
                }
                else if (connectedNow)
                {
                    trigger = "网络连接";
                }
                else if (errorRetry)
                {
                    MarkErrorRetrySlot(nowLocal);
                    trigger = "错误重试";
                }
                else if (hourlyRefresh)
                {
                    trigger = "小时计划";
                }
                else
                {
                    trigger = "首次检测";
                }

                if (firstRefresh || connectedNow || configuredRefresh || hourlyRefresh || this.nextHourlyRefreshLocal == DateTime.MinValue)
                {
                    ScheduleNextHourlyRefresh(nowLocal);
                }
            }

            clone = this.snapshot.Clone();
            clone.Running = this.requestRunning;
        }

        if (shouldStart)
        {
            RunRefreshAsync(trigger);
        }

        return clone;
    }

    private void OnNetworkAddressChanged(object sender, EventArgs e)
    {
        MarkNetworkChanged();
    }

    private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
    {
        MarkNetworkChanged();
    }

    private void MarkNetworkChanged()
    {
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            // Invalidate connectivity without changing the user-visible refresh trigger.
            this.networkStateRefreshRequested = true;
            this.networkStateKnown = false;
        }
    }

    private void RunRefreshAsync(string trigger)
    {
        Task.Run(delegate
        {
            CleanIpConnectionSnapshot next = new CleanIpConnectionSnapshot();
            next.Running = true;
            next.RefreshTrigger = string.IsNullOrWhiteSpace(trigger) ? "自动刷新" : trigger;
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                string json = FetchText(CleanIpMeUrl);
                ApplyCleanIpResponse(next, json);
                next.Success = true;
                next.Error = string.Empty;
            }
            catch (Exception ex)
            {
                next.Success = false;
                next.Error = FormatException(ex);
            }

            stopwatch.Stop();
            next.LatencyMs = (int)Math.Min(int.MaxValue, stopwatch.ElapsedMilliseconds);
            next.CheckedAtLocal = DateTime.Now;
            next.CheckedAtKnown = true;
            next.Running = false;

            lock (this.sync)
            {
                this.snapshot = next;
                this.requestRunning = false;
            }
        });
    }

    private void ScheduleNextHourlyRefresh(DateTime nowLocal)
    {
        DateTime nominal = new DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0).AddHours(1.0);
        DateTime next = DateTime.MinValue;
        do
        {
            int jitterSeconds = this.random.Next(-HourlyRefreshJitterWindowSeconds, HourlyRefreshJitterWindowSeconds + 1);
            next = nominal.AddSeconds(jitterSeconds);
            if (next <= nowLocal)
            {
                nominal = nominal.AddHours(1.0);
            }
        }
        while (next <= nowLocal);

        this.nextHourlyRefreshLocal = next;
    }

    private bool IsErrorRetrySlotDue(DateTime nowLocal)
    {
        DateTime slot;
        return TryGetScheduledRetrySlot(nowLocal, out slot) && this.lastErrorRetrySlotLocal != slot;
    }

    private void MarkErrorRetrySlot(DateTime nowLocal)
    {
        DateTime slot;
        if (TryGetScheduledRetrySlot(nowLocal, out slot))
        {
            this.lastErrorRetrySlotLocal = slot;
        }
    }

    private static bool TryGetScheduledRetrySlot(DateTime nowLocal, out DateTime slot)
    {
        slot = DateTime.MinValue;
        if (nowLocal.Minute % ScheduledRetrySlotMinutes != 0)
        {
            return false;
        }

        slot = new DateTime(
            nowLocal.Year,
            nowLocal.Month,
            nowLocal.Day,
            nowLocal.Hour,
            nowLocal.Minute,
            0);
        return true;
    }

    private static bool IsNetworkConnected()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;
        }
    }

    private static CleanIpConnectionSnapshot BuildErrorSnapshot(string error, DateTime checkedAtLocal)
    {
        CleanIpConnectionSnapshot snapshot = new CleanIpConnectionSnapshot();
        snapshot.Success = false;
        snapshot.CheckedAtKnown = true;
        snapshot.CheckedAtLocal = checkedAtLocal;
        snapshot.Error = string.IsNullOrWhiteSpace(error) ? "未知错误" : error.Trim();
        snapshot.RefreshTrigger = "网络状态";
        return snapshot;
    }

    private static void ApplyCleanIpResponse(CleanIpConnectionSnapshot snapshot, string json)
    {
        Dictionary<string, object> root = ParseObject(json);
        if (!BoolValue(root, "ok", true))
        {
            throw new InvalidOperationException(FirstNonEmpty(StringValue(root, "error"), "CleanIP返回失败"));
        }

        Dictionary<string, object> purity = DictionaryValue(root, "purity");
        Dictionary<string, object> network = DictionaryValue(root, "network");
        Dictionary<string, object> geo = DictionaryValue(root, "geo");

        snapshot.Ip = EmptyToDash(StringValue(root, "ip"));
        snapshot.Location = JoinNonEmpty(
            StringValue(geo, "country"),
            StringValue(geo, "region"),
            StringValue(geo, "city"));
        snapshot.Asn = FormatAsn(IntValue(network, "asn", 0));
        snapshot.Organization = EmptyToDash(FirstNonEmpty(
            StringValue(network, "asn_name"),
            StringValue(network, "asn_org"),
            StringValue(network, "isp"),
            StringValue(network, "org")));

        int score = IntValue(purity, "score", -1);
        if (score >= 0)
        {
            snapshot.ScoreKnown = true;
            snapshot.Score = Math.Max(0, Math.Min(100, score));
        }

        snapshot.Grade = EmptyToDash(StringValue(purity, "grade"));
        string nativeKey = NormalizeNativeKey(
            StringValue(network, "geo_association"),
            StringValue(network, "native_or_broadcast"),
            BoolValue(purity, "is_native", false));
        ApplyNativeBadge(snapshot, nativeKey);

        string ipType = FirstNonEmpty(
            StringValue(purity, "ip_type"),
            StringValue(network, "ip_type"),
            StringValue(network, "network_type"));
        ApplyIpTypeBadge(snapshot, ipType);
        snapshot.IpTypeReason = EmptyToDash(StringValue(purity, "ip_type_reason"));
    }

    private static CleanIpConnectionSnapshot BuildTestSnapshot(CleanIpBadgeTestMode mode)
    {
        CleanIpConnectionSnapshot snapshot = new CleanIpConnectionSnapshot();
        snapshot.TestMode = true;
        snapshot.CheckedAtKnown = true;
        snapshot.CheckedAtLocal = DateTime.Now;
        snapshot.RefreshTrigger = "强制测试";
        snapshot.Ip = "TEST";
        snapshot.Location = "CleanIP测试";
        snapshot.Asn = "AS0";
        snapshot.Organization = ProductIdentity.DisplayName;

        if (mode == CleanIpBadgeTestMode.ErrorHttp403)
        {
            return BuildTestErrorSnapshot(snapshot, "HTTP 403 Forbidden");
        }

        if (mode == CleanIpBadgeTestMode.ErrorHttp429)
        {
            return BuildTestErrorSnapshot(snapshot, "HTTP 429 Too Many Requests");
        }

        if (mode == CleanIpBadgeTestMode.ErrorTimeout)
        {
            return BuildTestErrorSnapshot(snapshot, "超时: 请求超过9秒");
        }

        if (mode == CleanIpBadgeTestMode.ErrorDns)
        {
            return BuildTestErrorSnapshot(snapshot, "DNS解析失败: 无法解析 cleanip.io");
        }

        if (mode == CleanIpBadgeTestMode.ErrorConnect)
        {
            return BuildTestErrorSnapshot(snapshot, "连接失败: 无法连接 cleanip.io");
        }

        snapshot.Success = true;

        if (mode == CleanIpBadgeTestMode.BroadcastBusiness)
        {
            snapshot.ScoreKnown = true;
            snapshot.Score = 74;
            snapshot.Grade = "B";
            ApplyNativeBadge(snapshot, "broadcast");
            ApplyIpTypeBadge(snapshot, "Business IP");
            snapshot.IpTypeReason = "测试: 广播商业网络";
            return snapshot;
        }

        if (mode == CleanIpBadgeTestMode.UnannouncedIdc)
        {
            snapshot.ScoreKnown = true;
            snapshot.Score = 52;
            snapshot.Grade = "C";
            ApplyNativeBadge(snapshot, "unannounced");
            ApplyIpTypeBadge(snapshot, "IDC");
            snapshot.IpTypeReason = "测试: 未通告机房网络";
            return snapshot;
        }

        if (mode == CleanIpBadgeTestMode.ProxyRisk)
        {
            snapshot.ScoreKnown = true;
            snapshot.Score = 28;
            snapshot.Grade = "D";
            ApplyNativeBadge(snapshot, "broadcast");
            ApplyIpTypeBadge(snapshot, "Proxy IP");
            snapshot.IpTypeReason = "测试: 代理风险出口";
            return snapshot;
        }

        snapshot.ScoreKnown = true;
        snapshot.Score = 94;
        snapshot.Grade = "A";
        ApplyNativeBadge(snapshot, "native");
        ApplyIpTypeBadge(snapshot, "Residential IP");
        snapshot.IpTypeReason = "测试: 原生住宅出口";
        return snapshot;
    }

    private static CleanIpConnectionSnapshot BuildTestErrorSnapshot(CleanIpConnectionSnapshot snapshot, string error)
    {
        snapshot.Success = false;
        snapshot.Error = error;
        snapshot.Ip = "--";
        snapshot.Location = "CleanIP测试";
        snapshot.Asn = "--";
        snapshot.Organization = "--";
        return snapshot;
    }

    private static void ApplyNativeBadge(CleanIpConnectionSnapshot snapshot, string key)
    {
        snapshot.NativeKey = key;
        if (key == "native")
        {
            snapshot.NativeLabel = "原生IP";
            snapshot.NativeIconClass = "fa-solid fa-location-check";
            return;
        }

        if (key == "unannounced")
        {
            snapshot.NativeLabel = "未通告";
            snapshot.NativeIconClass = "fa-solid fa-circle-minus";
            return;
        }

        if (key == "broadcast")
        {
            snapshot.NativeLabel = "广播IP";
            snapshot.NativeIconClass = "fa-solid fa-router";
            return;
        }

        snapshot.NativeLabel = "待确认";
        snapshot.NativeIconClass = "fa-solid fa-circle-question";
    }

    private static void ApplyIpTypeBadge(CleanIpConnectionSnapshot snapshot, string ipType)
    {
        string key = string.IsNullOrWhiteSpace(ipType) ? "Unknown" : ipType.Trim();
        snapshot.IpTypeKey = key;
        snapshot.IpTypeLabel = GetIpTypeLabel(key);
        snapshot.IpTypeIconClass = GetIpTypeIconClass(key);
    }

    private static string NormalizeNativeKey(string geoAssociation, string nativeOrBroadcast, bool isNative)
    {
        string value = (geoAssociation ?? string.Empty).Trim().ToLowerInvariant();
        if (value == "native")
        {
            return "native";
        }

        if (value == "unannounced")
        {
            return "unannounced";
        }

        if (value == "broadcast" || value == "cross_border" || value == "multi_region")
        {
            return "broadcast";
        }

        value = (nativeOrBroadcast ?? string.Empty).Trim().ToUpperInvariant();
        if (value == "NATIVE")
        {
            return "native";
        }

        if (value == "UNANNOUNCED")
        {
            return "unannounced";
        }

        if (value == "BROADCAST")
        {
            return "broadcast";
        }

        return isNative ? "native" : string.Empty;
    }

    private static string GetIpTypeLabel(string key)
    {
        if (key == "Residential IP") return "住宅IP";
        if (key == "Mobile IP") return "移动IP";
        if (key == "Business IP") return "商业IP";
        if (key == "Education IP") return "教育IP";
        if (key == "Government IP") return "政府IP";
        if (key == "Public DNS") return "公共 DNS";
        if (key == "Root DNS") return "根 DNS";
        if (key == "Public CDN") return "公共 CDN";
        if (key == "IDC" || key == "Datacenter IP") return "IDC";
        if (key == "Residential Proxy") return "住宅代理";
        if (key == "VPN IP") return "VPN";
        if (key == "Proxy IP") return "代理";
        if (key == "Tor IP" || key == "Tor Exit") return "Tor";
        if (key == "Relay IP") return "中继";
        if (key == "Unknown") return "未知";
        return string.IsNullOrWhiteSpace(key) ? "--" : key;
    }

    private static string GetIpTypeIconClass(string key)
    {
        if (key == "Residential IP") return "fa-solid fa-house";
        if (key == "Mobile IP") return "fa-solid fa-mobile-screen";
        if (key == "Business IP") return "fa-solid fa-briefcase";
        if (key == "Education IP") return "fa-solid fa-graduation-cap";
        if (key == "Government IP") return "fa-solid fa-landmark";
        if (key == "Public DNS" || key == "Root DNS" || key == "Public CDN") return "fa-solid fa-network-wired";
        if (key == "IDC" || key == "Datacenter IP") return "fa-sharp fa-solid fa-server";
        if (key == "Residential Proxy") return "fa-solid fa-user-secret";
        if (key == "VPN IP") return "fa-solid fa-shield-halved";
        if (key == "Proxy IP") return "fa-solid fa-filter";
        if (key == "Tor IP" || key == "Tor Exit") return "fa-solid fa-mask";
        if (key == "Relay IP") return "fa-solid fa-link";
        return "fa-solid fa-circle-question";
    }

    private static string FetchText(string url)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Timeout = RequestTimeoutMs;
        request.ReadWriteTimeout = RequestTimeoutMs;
        request.UserAgent = ProductIdentity.UserAgent;
        request.Accept = "application/json,text/plain,*/*";
        request.Referer = "https://cleanip.io/";
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

        using (WebResponse response = request.GetResponse())
        using (System.IO.Stream stream = response.GetResponseStream())
        using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8))
        {
            return reader.ReadToEnd();
        }
    }

    private static Dictionary<string, object> ParseObject(string json)
    {
        object parsed = new JavaScriptSerializer().DeserializeObject(json);
        Dictionary<string, object> dictionary = parsed as Dictionary<string, object>;
        if (dictionary == null)
        {
            throw new InvalidOperationException("CleanIP响应格式无效");
        }

        return dictionary;
    }

    private static Dictionary<string, object> DictionaryValue(Dictionary<string, object> source, string key)
    {
        if (source == null || !source.ContainsKey(key))
        {
            return new Dictionary<string, object>();
        }

        Dictionary<string, object> dictionary = source[key] as Dictionary<string, object>;
        return dictionary ?? new Dictionary<string, object>();
    }

    private static string StringValue(Dictionary<string, object> source, string key)
    {
        if (source == null || !source.ContainsKey(key) || source[key] == null)
        {
            return string.Empty;
        }

        return Convert.ToString(source[key], CultureInfo.InvariantCulture);
    }

    private static int IntValue(Dictionary<string, object> source, string key, int fallback)
    {
        if (source == null || !source.ContainsKey(key) || source[key] == null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToInt32(source[key], CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool BoolValue(Dictionary<string, object> source, string key, bool fallback)
    {
        if (source == null || !source.ContainsKey(key) || source[key] == null)
        {
            return fallback;
        }

        try
        {
            return Convert.ToBoolean(source[key], CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }

    private static string FormatAsn(int asn)
    {
        return asn > 0 ? "AS" + asn.ToString(CultureInfo.InvariantCulture) : "--";
    }

    private static string JoinNonEmpty(params string[] values)
    {
        StringBuilder builder = new StringBuilder();
        if (values != null)
        {
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i];
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(" · ");
                }

                builder.Append(value.Trim());
            }
        }

        return builder.Length == 0 ? "--" : builder.ToString();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
        {
            return string.Empty;
        }

        for (int i = 0; i < values.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
            {
                return values[i].Trim();
            }
        }

        return string.Empty;
    }

    private static string EmptyToDash(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "--" : value.Trim();
    }

    private static string FormatException(Exception ex)
    {
        if (ex == null)
        {
            return "未知错误";
        }

        WebException web = ex as WebException;
        if (web != null)
        {
            return FormatWebException(web);
        }

        if (!string.IsNullOrWhiteSpace(ex.Message))
        {
            return ex.Message.Trim();
        }

        return ex.GetType().Name;
    }

    private static string FormatWebException(WebException web)
    {
        HttpWebResponse http = web.Response as HttpWebResponse;
        if (web.Status == WebExceptionStatus.ProtocolError && http != null)
        {
            string status = "HTTP " + ((int)http.StatusCode).ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(http.StatusDescription))
            {
                status += " " + http.StatusDescription.Trim();
            }

            string body = ReadResponseBody(http);
            if (!string.IsNullOrWhiteSpace(body))
            {
                status += ": " + TrimErrorDetail(body);
            }

            return status;
        }

        if (web.Status == WebExceptionStatus.Timeout)
        {
            return "超时: 请求超过" + (RequestTimeoutMs / 1000).ToString(CultureInfo.InvariantCulture) + "秒";
        }

        if (web.Status == WebExceptionStatus.NameResolutionFailure)
        {
            return "DNS解析失败: 无法解析 cleanip.io";
        }

        if (web.Status == WebExceptionStatus.ConnectFailure)
        {
            return "连接失败: 无法连接 cleanip.io";
        }

        if (web.Status == WebExceptionStatus.TrustFailure)
        {
            return "证书验证失败: TLS连接不可信";
        }

        if (!string.IsNullOrWhiteSpace(web.Message))
        {
            return web.Status.ToString() + ": " + web.Message.Trim();
        }

        return web.Status.ToString();
    }

    private static string ReadResponseBody(HttpWebResponse response)
    {
        try
        {
            using (System.IO.Stream stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    return string.Empty;
                }

                using (System.IO.StreamReader reader = new System.IO.StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TrimErrorDetail(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return string.Empty;
        }

        string value = detail.Replace("\r", " ").Replace("\n", " ").Trim();
        while (value.IndexOf("  ", StringComparison.Ordinal) >= 0)
        {
            value = value.Replace("  ", " ");
        }

        const int maxLength = 96;
        if (value.Length > maxLength)
        {
            value = value.Substring(0, maxLength - 1) + "...";
        }

        return value;
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}
