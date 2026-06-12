using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal static class CloudEndpointProbe
{
    private const int SampleCount = 3;
    private const int SampleIntervalMs = 10000;
    private const int RequestTimeoutMs = 3800;
    private const int SlowThresholdMs = 1000;
    private const int MaxConcurrentRequests = 3;

    private static readonly object CacheSync = new object();
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly HttpClient Client = CreateHttpClient();
    private static readonly Dictionary<string, CloudEndpointCacheEntry> EndpointCache = new Dictionary<string, CloudEndpointCacheEntry>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HttpTextCacheEntry> TextCache = new Dictionary<string, HttpTextCacheEntry>(StringComparer.OrdinalIgnoreCase);
    private static readonly CloudTarget[] Targets = new CloudTarget[]
    {
        CloudTarget.Statuspage("cloudflare", "https://www.cloudflarestatus.com/api/v2/summary.json"),
        CloudTarget.Http("aws", "aws.amazon.com"),
        CloudTarget.GoogleServiceHealth("google", "https://status.cloud.google.com/incidents.json"),
        CloudTarget.Statuspage("github", "https://www.githubstatus.com/api/v2/summary.json"),
        CloudTarget.Http("aliyun", "www.aliyun.com"),
        CloudTarget.Http("tencent", "cloud.tencent.com")
    };

    public static CloudEndpointSnapshot[] CreateCheckingSnapshots()
    {
        return CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Checking);
    }

    public static CloudEndpointSnapshot[] CreateCheckingSnapshots(CloudEndpointSnapshot[] previous)
    {
        CloudEndpointSnapshot[] result = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Checking);
        if (previous == null || previous.Length == 0)
        {
            return result;
        }

        for (int i = 0; i < result.Length; i++)
        {
            CloudEndpointSnapshot old = FindSnapshot(previous, result[i].Key);
            if (old == null)
            {
                continue;
            }

            result[i].LatencyMs = old.LatencyMs;
            result[i].Reason = "刷新中";
            result[i].AlertReason = old.AlertReason;
            result[i].AlertName = old.AlertName;
            result[i].CheckedAtLocal = old.CheckedAtLocal;
            result[i].CheckedAtKnown = old.CheckedAtKnown;
        }

        return result;
    }

    public static CloudEndpointSnapshot[] Run(
        List<string> logLines,
        int regionMask,
        CloudEndpointSnapshot[] previous,
        bool forceRefresh,
        bool regionChanged)
    {
        return RunAsync(logLines, regionMask, previous, forceRefresh, regionChanged, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public static async Task<CloudEndpointSnapshot[]> RunAsync(
        List<string> logLines,
        int regionMask,
        CloudEndpointSnapshot[] previous,
        bool forceRefresh,
        bool regionChanged,
        CancellationToken cancellationToken)
    {
        regionMask = NormalizeRegionMask(regionMask);
        DateTime nowUtc = DateTime.UtcNow;
        CloudEndpointSnapshot[] snapshots = CloudEndpointSnapshot.CreateDefaults(CloudEndpointStatus.Unknown);
        List<int> refreshIndices = new List<int>();
        List<CloudEndpointSample>[] samples = new List<CloudEndpointSample>[Targets.Length];
        for (int i = 0; i < Targets.Length; i++)
        {
            samples[i] = new List<CloudEndpointSample>();
            CloudEndpointSnapshot cached;
            string cacheReason;
            if (TryGetCachedSnapshot(Targets[i], nowUtc, regionMask, forceRefresh, regionChanged, out cached, out cacheReason))
            {
                snapshots[i] = cached;
                if (logLines != null)
                {
                    logLines.Add("  缓存 " + Targets[i].Key + "=" + FormatSnapshot(cached) + " " + cacheReason);
                }
            }
            else
            {
                CloudEndpointSnapshot previousSnapshot = FindSnapshot(previous, Targets[i].Key);
                if (previousSnapshot != null)
                {
                    snapshots[i] = previousSnapshot.Clone();
                }

                refreshIndices.Add(i);
            }
        }

        if (logLines != null)
        {
            logLines.Add("云服务轻量采样: 刷新=" + refreshIndices.Count.ToString(CultureInfo.InvariantCulture) +
                "/" + Targets.Length.ToString(CultureInfo.InvariantCulture) +
                " 并发上限=" + MaxConcurrentRequests.ToString(CultureInfo.InvariantCulture));
        }

        if (refreshIndices.Count > 0)
        {
            await RunSampleRoundAsync(refreshIndices, samples, regionMask, 1, logLines, cancellationToken).ConfigureAwait(false);
            List<int> confirmationIndices = BuildConfirmationIndices(refreshIndices, samples);
            for (int round = 2; round <= SampleCount && confirmationIndices.Count > 0; round++)
            {
                if (logLines != null)
                {
                    logLines.Add("  第" + round.ToString(CultureInfo.InvariantCulture) + "次确认: " +
                        FormatTargetList(confirmationIndices));
                }

                await Task.Delay(SampleIntervalMs, cancellationToken).ConfigureAwait(false);
                await RunSampleRoundAsync(confirmationIndices, samples, regionMask, round, logLines, cancellationToken).ConfigureAwait(false);
            }

            for (int i = 0; i < refreshIndices.Count; i++)
            {
                int targetIndex = refreshIndices[i];
                ApplySamples(snapshots[targetIndex], samples[targetIndex].ToArray());
                StoreCachedSnapshot(Targets[targetIndex], snapshots[targetIndex], regionMask, nowUtc);
                if (logLines != null)
                {
                    logLines.Add("  结果 " + snapshots[targetIndex].DisplayName + "=" +
                        FormatStatus(snapshots[targetIndex].Status) + " " + snapshots[targetIndex].Reason +
                        " TTL=" + FormatDuration(GetCacheDuration(Targets[targetIndex], snapshots[targetIndex])));
                }
            }
        }

        return snapshots;
    }

    private static Task<CloudEndpointSample> ProbeOnceAsync(CloudTarget target, int regionMask, CancellationToken cancellationToken)
    {
        if (target.Kind == CloudTargetKind.Statuspage)
        {
            return ProbeStatuspageAsync(target, regionMask, cancellationToken);
        }

        if (target.Kind == CloudTargetKind.GoogleServiceHealth)
        {
            return ProbeGoogleServiceHealthAsync(target, regionMask, cancellationToken);
        }

        return ProbeHttpHostAsync(target.Key, "https://" + target.Host + "/", target.Key, cancellationToken);
    }

    private static async Task RunSampleRoundAsync(
        List<int> targetIndices,
        List<CloudEndpointSample>[] samples,
        int regionMask,
        int round,
        List<string> logLines,
        CancellationToken cancellationToken)
    {
        if (targetIndices == null || targetIndices.Count == 0)
        {
            return;
        }

        using (SemaphoreSlim semaphore = new SemaphoreSlim(MaxConcurrentRequests))
        {
            Task<IndexedCloudEndpointSample>[] tasks = new Task<IndexedCloudEndpointSample>[targetIndices.Count];
            for (int i = 0; i < targetIndices.Count; i++)
            {
                int targetIndex = targetIndices[i];
                CloudTarget target = Targets[targetIndex];
                tasks[i] = ProbeWithSemaphoreAsync(targetIndex, target, regionMask, semaphore, cancellationToken);
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
            }

            for (int i = 0; i < tasks.Length; i++)
            {
                int targetIndex = targetIndices[i];
                CloudEndpointSample sample = tasks[i].Status == TaskStatus.RanToCompletion
                    ? tasks[i].Result.Sample
                    : CloudEndpointSample.CreateDown(Targets[targetIndex].Key, "任务失败", "TaskFault", "探测任务异常");
                samples[targetIndex].Add(sample);
                if (logLines != null)
                {
                    logLines.Add("  第" + round.ToString(CultureInfo.InvariantCulture) + "次 " +
                        Targets[targetIndex].Key + "=" + FormatSample(sample));
                }
            }
        }
    }

    private static async Task<IndexedCloudEndpointSample> ProbeWithSemaphoreAsync(
        int targetIndex,
        CloudTarget target,
        int regionMask,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new IndexedCloudEndpointSample
            {
                TargetIndex = targetIndex,
                Sample = await ProbeOnceAsync(target, regionMask, cancellationToken).ConfigureAwait(false)
            };
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static List<int> BuildConfirmationIndices(List<int> refreshIndices, List<CloudEndpointSample>[] samples)
    {
        List<int> result = new List<int>();
        if (refreshIndices == null)
        {
            return result;
        }

        for (int i = 0; i < refreshIndices.Count; i++)
        {
            int targetIndex = refreshIndices[i];
            if (samples[targetIndex] != null &&
                samples[targetIndex].Count > 0 &&
                NeedsConfirmation(samples[targetIndex][0]))
            {
                result.Add(targetIndex);
            }
        }

        return result;
    }

    private static bool NeedsConfirmation(CloudEndpointSample sample)
    {
        if (sample == null)
        {
            return true;
        }

        return sample.Status != CloudEndpointStatus.Normal || sample.LatencyMs >= SlowThresholdMs;
    }

    private static async Task<CloudEndpointSample> ProbeStatuspageAsync(CloudTarget target, int regionMask, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            HttpTextResult response = await FetchTextAsync(target, "GET", cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            CloudEndpointSample statusSample = ClassifyHttpStatus(target.Key, response.StatusCode, response.ElapsedMs, "Statuspage");
            if (statusSample.Status != CloudEndpointStatus.Normal)
            {
                return statusSample;
            }

            Dictionary<string, object> root = Json.DeserializeObject(response.Text) as Dictionary<string, object>;
            Dictionary<string, object> status = GetDictionary(root, "status");
            string indicator = GetString(status, "indicator").ToLowerInvariant();
            string description = GetString(status, "description");
            if (indicator == "none" || indicator.Length == 0)
            {
                return CloudEndpointSample.CreateNormal(target.Key, ClampLatency(stopwatch.ElapsedMilliseconds), "Statuspage", response.FromCache ? "官方正常 304缓存" : "官方正常");
            }

            if (string.Equals(target.Key, "cloudflare", StringComparison.OrdinalIgnoreCase) &&
                !IsCloudflareStatusRelevant(root, regionMask))
            {
                return CloudEndpointSample.CreateNormal(target.Key, ClampLatency(stopwatch.ElapsedMilliseconds), "Statuspage", response.FromCache ? "官方其他地区异常 304缓存" : "官方其他地区异常");
            }

            if (indicator == "major" || indicator == "critical")
            {
                return CloudEndpointSample.CreateDown(target.Key, "官方故障", "Statuspage:" + indicator, EmptyToFallback(description, "官方重大故障"));
            }

            return CloudEndpointSample.CreateAbnormal(target.Key, "官方降级", "Statuspage:" + indicator, EmptyToFallback(description, "官方状态异常"));
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return CloudEndpointSample.CreateDown(target.Key, "请求超时", FormatException(ex), "Statuspage API 超时");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CloudEndpointSample.CreateDown(target.Key, "状态API失败", FormatException(ex), "Statuspage API 失败");
        }
    }

    private static async Task<CloudEndpointSample> ProbeGoogleServiceHealthAsync(CloudTarget target, int regionMask, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            HttpTextResult response = await FetchTextAsync(target, "GET", cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            CloudEndpointSample statusSample = ClassifyHttpStatus(target.Key, response.StatusCode, response.ElapsedMs, "GoogleHealth");
            if (statusSample.Status != CloudEndpointStatus.Normal)
            {
                return statusSample;
            }

            object[] incidents = ToObjectArray(Json.DeserializeObject(response.Text));
            if (incidents == null || incidents.Length == 0)
            {
                return CloudEndpointSample.CreateNormal(target.Key, ClampLatency(stopwatch.ElapsedMilliseconds), "GoogleHealth", response.FromCache ? "官方无事件 304缓存" : "官方无事件");
            }

            CloudEndpointSample worst = null;
            for (int i = 0; i < incidents.Length; i++)
            {
                Dictionary<string, object> incident = incidents[i] as Dictionary<string, object>;
                if (incident == null || incident.ContainsKey("end"))
                {
                    continue;
                }

                string impact = GetString(incident, "status_impact").ToUpperInvariant();
                if (impact == "AVAILABLE" || impact.Length == 0)
                {
                    continue;
                }

                if (!IsGoogleIncidentRelevant(incident, regionMask))
                {
                    continue;
                }

                string title = EmptyToFallback(GetString(incident, "external_desc"), "Google Cloud 当前事件");
                CloudEndpointSample sample = impact == "SERVICE_OUTAGE"
                    ? CloudEndpointSample.CreateDown(target.Key, "官方故障", "GoogleHealth:" + impact, title)
                    : CloudEndpointSample.CreateAbnormal(target.Key, "官方降级", "GoogleHealth:" + impact, title);
                worst = PickWorse(worst, sample);
            }

            return worst ?? CloudEndpointSample.CreateNormal(target.Key, ClampLatency(stopwatch.ElapsedMilliseconds), "GoogleHealth", response.FromCache ? "官方无活动故障 304缓存" : "官方无活动故障");
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return CloudEndpointSample.CreateDown(target.Key, "请求超时", FormatException(ex), "Google Service Health 超时");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CloudEndpointSample.CreateDown(target.Key, "状态API失败", FormatException(ex), "Google Service Health 失败");
        }
    }

    private static async Task<CloudEndpointSample> ProbeHttpHostAsync(string key, string url, string source, CancellationToken cancellationToken)
    {
        CloudEndpointSample head = await ProbeHttpRequestAsync(key, url, "HEAD", source, cancellationToken).ConfigureAwait(false);
        if (head.HttpStatusCode == 403 || head.HttpStatusCode == 405 || head.HttpStatusCode == 501)
        {
            CloudEndpointSample get = await ProbeHttpRequestAsync(key, url, "GET", source + "/GET", cancellationToken).ConfigureAwait(false);
            if (get.Status == CloudEndpointStatus.Normal || get.HttpStatusCode != 0)
            {
                return get;
            }
        }

        return head;
    }

    private static async Task<CloudEndpointSample> ProbeHttpRequestAsync(string key, string url, string method, string source, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using (HttpRequestMessage request = CreateRequest(url, method))
            using (HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                stopwatch.Stop();
                return ClassifyHttpStatus(key, (int)response.StatusCode, ClampLatency(stopwatch.ElapsedMilliseconds), source);
            }
        }
        catch (OperationCanceledException ex)
        {
            stopwatch.Stop();
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return CloudEndpointSample.CreateDown(key, "请求超时", FormatException(ex), source);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return ClassifyRequestException(key, ex, source);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return CloudEndpointSample.CreateDown(key, "请求失败", FormatException(ex), source);
        }
    }

    private static async Task<HttpTextResult> FetchTextAsync(CloudTarget target, string method, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (HttpRequestMessage request = CreateRequest(target.ApiUrl, method))
        {
            ApplyConditionalHeaders(request, target);
            using (HttpResponseMessage response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                stopwatch.Stop();
                if ((int)response.StatusCode == 304)
                {
                    HttpTextResult cached = TryBuildNotModifiedResult(target, ClampLatency(stopwatch.ElapsedMilliseconds));
                    if (cached != null)
                    {
                        return cached;
                    }
                }

                string text = response.Content == null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                StoreTextCache(target, response, text);
                return new HttpTextResult
                {
                    StatusCode = (int)response.StatusCode,
                    Text = text,
                    ElapsedMs = ClampLatency(stopwatch.ElapsedMilliseconds),
                    FromCache = false
                };
            }
        }
    }

    private static HttpRequestMessage CreateRequest(string url, string method)
    {
        EnsureTls12();
        HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.TryAddWithoutValidation("User-Agent", ProductIdentity.UserAgent);
        request.Headers.TryAddWithoutValidation(
            "Accept",
            method == "GET" ? "application/json, application/rss+xml, text/xml, text/html;q=0.8, */*;q=0.5" : "*/*");
        return request;
    }

    private static bool TryGetCachedSnapshot(
        CloudTarget target,
        DateTime nowUtc,
        int regionMask,
        bool forceRefresh,
        bool regionChanged,
        out CloudEndpointSnapshot snapshot,
        out string reason)
    {
        snapshot = null;
        reason = string.Empty;
        if (forceRefresh || (regionChanged && target.UsesRegionFilter))
        {
            return false;
        }

        lock (CacheSync)
        {
            CloudEndpointCacheEntry entry;
            if (!EndpointCache.TryGetValue(target.Key, out entry) || entry == null || entry.Snapshot == null)
            {
                return false;
            }

            if (target.UsesRegionFilter && entry.RegionMask != regionMask)
            {
                return false;
            }

            if (nowUtc >= entry.ExpiresUtc)
            {
                return false;
            }

            snapshot = entry.Snapshot.Clone();
            reason = "有效至 " + entry.ExpiresUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            return true;
        }
    }

    private static void StoreCachedSnapshot(CloudTarget target, CloudEndpointSnapshot snapshot, int regionMask, DateTime nowUtc)
    {
        if (target == null || snapshot == null)
        {
            return;
        }

        TimeSpan duration = GetCacheDuration(target, snapshot);
        lock (CacheSync)
        {
            EndpointCache[target.Key] = new CloudEndpointCacheEntry
            {
                Snapshot = snapshot.Clone(),
                RegionMask = target.UsesRegionFilter ? regionMask : 0,
                ExpiresUtc = nowUtc + duration
            };
        }
    }

    private static TimeSpan GetCacheDuration(CloudTarget target, CloudEndpointSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return TimeSpan.FromSeconds(45);
        }

        if (snapshot.Status == CloudEndpointStatus.Normal)
        {
            return target != null && target.UsesOfficialApi
                ? TimeSpan.FromMinutes(30)
                : TimeSpan.FromMinutes(15);
        }

        if (snapshot.Status == CloudEndpointStatus.Slow || snapshot.Status == CloudEndpointStatus.Abnormal)
        {
            return TimeSpan.FromMinutes(2);
        }

        if (snapshot.Status == CloudEndpointStatus.Down)
        {
            return TimeSpan.FromSeconds(45);
        }

        return TimeSpan.FromSeconds(30);
    }

    private static void ApplyConditionalHeaders(HttpRequestMessage request, CloudTarget target)
    {
        if (request == null || target == null || !target.UsesOfficialApi)
        {
            return;
        }

        lock (CacheSync)
        {
            HttpTextCacheEntry entry;
            if (!TextCache.TryGetValue(target.Key, out entry) || entry == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(entry.ETag))
            {
                try
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
                }
                catch
                {
                }
            }

            if (entry.LastModifiedUtc != DateTime.MinValue)
            {
                try
                {
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", entry.LastModifiedUtc.ToString("R", CultureInfo.InvariantCulture));
                }
                catch
                {
                }
            }
        }
    }

    private static void StoreTextCache(CloudTarget target, HttpResponseMessage response, string text)
    {
        if (target == null || response == null || !target.UsesOfficialApi || string.IsNullOrEmpty(text))
        {
            return;
        }

        string etag = GetHeaderValue(response, "ETag");
        DateTime lastModifiedUtc = DateTime.MinValue;
        string lastModified = GetHeaderValue(response, "Last-Modified");
        DateTime parsedLastModified;
        if (!string.IsNullOrWhiteSpace(lastModified) &&
            DateTime.TryParse(lastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsedLastModified))
        {
            lastModifiedUtc = parsedLastModified.ToUniversalTime();
        }

        lock (CacheSync)
        {
            TextCache[target.Key] = new HttpTextCacheEntry
            {
                Text = text,
                ETag = etag ?? string.Empty,
                LastModifiedUtc = lastModifiedUtc
            };
        }
    }

    private static string GetHeaderValue(HttpResponseMessage response, string name)
    {
        IEnumerable<string> values;
        if (response.Headers.TryGetValues(name, out values))
        {
            return FirstHeaderValue(values);
        }

        if (response.Content != null && response.Content.Headers.TryGetValues(name, out values))
        {
            return FirstHeaderValue(values);
        }

        return string.Empty;
    }

    private static string FirstHeaderValue(IEnumerable<string> values)
    {
        if (values == null)
        {
            return string.Empty;
        }

        foreach (string value in values)
        {
            return value ?? string.Empty;
        }

        return string.Empty;
    }

    private static HttpTextResult TryBuildNotModifiedResult(CloudTarget target, int elapsedMs)
    {
        if (target == null)
        {
            return null;
        }

        lock (CacheSync)
        {
            HttpTextCacheEntry entry;
            if (!TextCache.TryGetValue(target.Key, out entry) || entry == null || string.IsNullOrEmpty(entry.Text))
            {
                return null;
            }

            return new HttpTextResult
            {
                StatusCode = 200,
                Text = entry.Text,
                ElapsedMs = elapsedMs,
                FromCache = true
            };
        }
    }

    private static CloudEndpointSample ClassifyHttpStatus(string key, int statusCode, int latencyMs, string source)
    {
        if (statusCode >= 200 && statusCode < 400)
        {
            return CloudEndpointSample.CreateNormal(key, latencyMs, source, "HTTP=" + statusCode.ToString(CultureInfo.InvariantCulture));
        }

        if (statusCode == 401 || statusCode == 403)
        {
            return CloudEndpointSample.CreateAbnormal(key, "拒绝访问", "HTTP=" + statusCode.ToString(CultureInfo.InvariantCulture), source);
        }

        if (statusCode == 429)
        {
            return CloudEndpointSample.CreateAbnormal(key, "访问限流", "HTTP=429", source);
        }

        if (statusCode == 451)
        {
            return CloudEndpointSample.CreateAbnormal(key, "地区受限", "HTTP=451", source);
        }

        if (statusCode == 404 || statusCode == 410)
        {
            return CloudEndpointSample.CreateAbnormal(key, "入口异常", "HTTP=" + statusCode.ToString(CultureInfo.InvariantCulture), source);
        }

        if (statusCode >= 500 && statusCode < 600)
        {
            return CloudEndpointSample.CreateAbnormal(key, "服务异常", "HTTP=" + statusCode.ToString(CultureInfo.InvariantCulture), source);
        }

        return CloudEndpointSample.CreateAbnormal(key, "HTTP异常", "HTTP=" + statusCode.ToString(CultureInfo.InvariantCulture), source);
    }

    private static CloudEndpointSample ClassifyWebException(string key, WebException ex, string source)
    {
        WebExceptionStatus status = ex == null ? WebExceptionStatus.UnknownError : ex.Status;
        if (status == WebExceptionStatus.NameResolutionFailure ||
            status == WebExceptionStatus.ProxyNameResolutionFailure)
        {
            return CloudEndpointSample.CreateDown(key, "DNS失败", FormatException(ex), source);
        }

        if (status == WebExceptionStatus.ConnectFailure)
        {
            return CloudEndpointSample.CreateDown(key, "TCP失败", FormatException(ex), source);
        }

        if (status == WebExceptionStatus.SecureChannelFailure ||
            status == WebExceptionStatus.TrustFailure)
        {
            return CloudEndpointSample.CreateDown(key, "TLS失败", FormatException(ex), source);
        }

        if (status == WebExceptionStatus.Timeout)
        {
            return CloudEndpointSample.CreateDown(key, "请求超时", FormatException(ex), source);
        }

        if (status == WebExceptionStatus.ConnectionClosed ||
            status == WebExceptionStatus.ReceiveFailure ||
            status == WebExceptionStatus.SendFailure)
        {
            return CloudEndpointSample.CreateDown(key, "连接中断", FormatException(ex), source);
        }

        return CloudEndpointSample.CreateDown(key, "请求失败", FormatException(ex), source);
    }

    private static CloudEndpointSample ClassifyRequestException(string key, Exception ex, string source)
    {
        WebException webException = FindWebException(ex);
        if (webException != null)
        {
            return ClassifyWebException(key, webException, source);
        }

        return CloudEndpointSample.CreateDown(key, "请求失败", FormatException(ex), source);
    }

    private static WebException FindWebException(Exception ex)
    {
        while (ex != null)
        {
            WebException webException = ex as WebException;
            if (webException != null)
            {
                return webException;
            }

            ex = ex.InnerException;
        }

        return null;
    }

    private static void ApplySamples(CloudEndpointSnapshot snapshot, CloudEndpointSample[] samples)
    {
        if (snapshot == null)
        {
            return;
        }

        if (samples == null || samples.Length == 0)
        {
            snapshot.Status = CloudEndpointStatus.Unknown;
            snapshot.LatencyMs = 0;
            snapshot.Reason = "无采样结果";
            snapshot.AlertReason = "无采样结果";
            snapshot.CheckedAtLocal = DateTime.Now;
            snapshot.CheckedAtKnown = true;
            return;
        }

        int normal = 0;
        int down = 0;
        int abnormal = 0;
        int valid = 0;
        List<int> normalLatencies = new List<int>();
        for (int i = 0; i < samples.Length; i++)
        {
            CloudEndpointSample sample = samples[i];
            if (sample == null)
            {
                continue;
            }

            valid++;
            if (sample.Status == CloudEndpointStatus.Normal)
            {
                normal++;
                normalLatencies.Add(sample.LatencyMs);
            }
            else if (sample.Status == CloudEndpointStatus.Down)
            {
                down++;
            }
            else
            {
                abnormal++;
            }
        }

        snapshot.CheckedAtLocal = DateTime.Now;
        snapshot.CheckedAtKnown = true;

        if (valid == 0)
        {
            snapshot.Status = CloudEndpointStatus.Unknown;
            snapshot.LatencyMs = 0;
            snapshot.Reason = "无有效采样";
            snapshot.AlertReason = "无有效采样";
            return;
        }

        int majority = valid <= 1 ? 1 : (valid / 2 + 1);
        if (normal >= majority)
        {
            int latencyMs = PickSimilarLatency(normalLatencies);
            snapshot.LatencyMs = latencyMs;
            snapshot.AlertReason = string.Empty;
            snapshot.AlertName = string.Empty;
            if (latencyMs >= SlowThresholdMs)
            {
                snapshot.Status = CloudEndpointStatus.Slow;
                snapshot.Reason = "延迟过高 " + latencyMs.ToString(CultureInfo.InvariantCulture) + "ms";
                snapshot.AlertReason = "延迟过高";
            }
            else
            {
                snapshot.Status = CloudEndpointStatus.Normal;
                snapshot.Reason = "正常 " + latencyMs.ToString(CultureInfo.InvariantCulture) + "ms";
            }

            return;
        }

        if (down >= majority)
        {
            CloudEndpointSample reason = PickRepresentativeSample(samples, CloudEndpointStatus.Down);
            snapshot.Status = CloudEndpointStatus.Down;
            snapshot.LatencyMs = 0;
            snapshot.AlertReason = reason == null ? "无法连接" : reason.AlertReason;
            snapshot.AlertName = reason == null ? string.Empty : reason.AlertName;
            snapshot.Reason = snapshot.AlertReason + " " + down.ToString(CultureInfo.InvariantCulture) + "/" +
                valid.ToString(CultureInfo.InvariantCulture) + FormatReasonSuffix(reason);
            return;
        }

        if (abnormal >= majority)
        {
            CloudEndpointSample reason = PickRepresentativeSample(samples, CloudEndpointStatus.Abnormal);
            snapshot.Status = CloudEndpointStatus.Abnormal;
            snapshot.LatencyMs = 0;
            snapshot.AlertReason = reason == null ? "状态异常" : reason.AlertReason;
            snapshot.AlertName = reason == null ? string.Empty : reason.AlertName;
            snapshot.Reason = snapshot.AlertReason + " " + abnormal.ToString(CultureInfo.InvariantCulture) + "/" +
                valid.ToString(CultureInfo.InvariantCulture) + FormatReasonSuffix(reason);
            return;
        }

        CloudEndpointSample worst = null;
        for (int i = 0; i < samples.Length; i++)
        {
            worst = PickWorse(worst, samples[i]);
        }

        snapshot.Status = CloudEndpointStatus.Abnormal;
        snapshot.LatencyMs = 0;
        snapshot.AlertReason = worst == null || string.IsNullOrWhiteSpace(worst.AlertReason) ? "结果不一致" : worst.AlertReason;
        snapshot.AlertName = worst == null ? string.Empty : worst.AlertName;
        snapshot.Reason = "三次结果不一致" + FormatReasonSuffix(worst);
    }

    private static CloudEndpointSample PickWorse(CloudEndpointSample left, CloudEndpointSample right)
    {
        if (left == null)
        {
            return right;
        }

        if (right == null)
        {
            return left;
        }

        int leftRank = GetStatusRank(left.Status);
        int rightRank = GetStatusRank(right.Status);
        if (rightRank > leftRank)
        {
            return right;
        }

        if (rightRank == leftRank && right.LatencyMs > left.LatencyMs)
        {
            return right;
        }

        return left;
    }

    private static int GetStatusRank(CloudEndpointStatus status)
    {
        if (status == CloudEndpointStatus.Down)
        {
            return 3;
        }

        if (status == CloudEndpointStatus.Abnormal)
        {
            return 2;
        }

        if (status == CloudEndpointStatus.Slow)
        {
            return 1;
        }

        return 0;
    }

    private static CloudEndpointSample PickRepresentativeSample(CloudEndpointSample[] samples, CloudEndpointStatus status)
    {
        CloudEndpointSample best = null;
        int bestCount = -1;
        for (int i = 0; i < samples.Length; i++)
        {
            CloudEndpointSample sample = samples[i];
            if (sample == null || sample.Status != status)
            {
                continue;
            }

            int count = 0;
            for (int j = 0; j < samples.Length; j++)
            {
                CloudEndpointSample other = samples[j];
                if (other != null &&
                    other.Status == status &&
                    string.Equals(other.AlertName, sample.AlertName, StringComparison.Ordinal) &&
                    string.Equals(other.AlertReason, sample.AlertReason, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            if (count > bestCount)
            {
                bestCount = count;
                best = sample;
            }
        }

        return best;
    }

    private static string FormatReasonSuffix(CloudEndpointSample sample)
    {
        if (sample == null || string.IsNullOrWhiteSpace(sample.Reason))
        {
            return string.Empty;
        }

        return " " + sample.Reason;
    }

    private static int PickSimilarLatency(List<int> values)
    {
        if (values == null || values.Count == 0)
        {
            return 0;
        }

        if (values.Count == 1)
        {
            return values[0];
        }

        int bestA = values[0];
        int bestB = values[1];
        int bestDelta = Math.Abs(bestA - bestB);
        for (int i = 0; i < values.Count; i++)
        {
            for (int j = i + 1; j < values.Count; j++)
            {
                int delta = Math.Abs(values[i] - values[j]);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestA = values[i];
                    bestB = values[j];
                }
            }
        }

        return (int)Math.Round((bestA + bestB) / 2.0);
    }

    private static string FormatSample(CloudEndpointSample sample)
    {
        if (sample == null)
        {
            return "无结果";
        }

        string text = FormatStatus(sample.Status) + " " + sample.LatencyMs.ToString(CultureInfo.InvariantCulture) +
            "ms Layer=" + sample.AlertReason;
        if (sample.HttpStatusCode > 0)
        {
            text += " HTTP=" + sample.HttpStatusCode.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(sample.Source))
        {
            text += " Source=" + sample.Source;
        }

        if (!string.IsNullOrWhiteSpace(sample.AlertName))
        {
            text += " AlertName=" + sample.AlertName;
        }

        if (!string.IsNullOrWhiteSpace(sample.Reason))
        {
            text += " Reason=" + sample.Reason;
        }

        return text;
    }

    private static string FormatSnapshot(CloudEndpointSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return "无结果";
        }

        string text = FormatStatus(snapshot.Status);
        if (snapshot.LatencyMs > 0)
        {
            text += " " + snapshot.LatencyMs.ToString(CultureInfo.InvariantCulture) + "ms";
        }

        if (!string.IsNullOrWhiteSpace(snapshot.AlertReason))
        {
            text += " Layer=" + snapshot.AlertReason;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Reason))
        {
            text += " Reason=" + snapshot.Reason;
        }

        return text;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1.0)
        {
            return ((int)Math.Round(duration.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + "m";
        }

        return ((int)Math.Round(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + "s";
    }

    private static string FormatTargetList(List<int> targetIndices)
    {
        if (targetIndices == null || targetIndices.Count == 0)
        {
            return "无";
        }

        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < targetIndices.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(",");
            }

            int index = targetIndices[i];
            builder.Append(index >= 0 && index < Targets.Length ? Targets[index].Key : "?");
        }

        return builder.ToString();
    }

    private static string FormatStatus(CloudEndpointStatus status)
    {
        if (status == CloudEndpointStatus.Normal)
        {
            return "正常";
        }

        if (status == CloudEndpointStatus.Slow)
        {
            return "延迟过高";
        }

        if (status == CloudEndpointStatus.Down)
        {
            return "无法连接";
        }

        if (status == CloudEndpointStatus.Abnormal)
        {
            return "状态异常";
        }

        if (status == CloudEndpointStatus.Checking)
        {
            return "刷新中";
        }

        return "未知";
    }

    private static int ClampLatency(long latencyMs)
    {
        if (latencyMs < 0)
        {
            return 0;
        }

        if (latencyMs > 99999)
        {
            return 99999;
        }

        return (int)latencyMs;
    }

    private static CloudEndpointSnapshot FindSnapshot(CloudEndpointSnapshot[] snapshots, string key)
    {
        if (snapshots == null || string.IsNullOrEmpty(key))
        {
            return null;
        }

        for (int i = 0; i < snapshots.Length; i++)
        {
            if (snapshots[i] != null && string.Equals(snapshots[i].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return snapshots[i];
            }
        }

        return null;
    }

    private static void EnsureTls12()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClientHandler handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMilliseconds(RequestTimeoutMs)
        };
    }

    private static int NormalizeRegionMask(int regionMask)
    {
        regionMask &= WidgetSettings.CloudStatusRegionMaskAll;
        return regionMask == 0 ? WidgetSettings.DefaultCloudStatusRegionMask : regionMask;
    }

    private static bool IsCloudflareStatusRelevant(Dictionary<string, object> root, int regionMask)
    {
        if (root == null)
        {
            return true;
        }

        bool sawRegionalSignal = false;
        if (HasRelevantCloudflareItems(GetArray(root, "incidents"), regionMask, ref sawRegionalSignal))
        {
            return true;
        }

        if (HasRelevantCloudflareItems(GetArray(root, "scheduled_maintenances"), regionMask, ref sawRegionalSignal))
        {
            return true;
        }

        object[] components = GetArray(root, "components");
        for (int i = 0; components != null && i < components.Length; i++)
        {
            Dictionary<string, object> component = components[i] as Dictionary<string, object>;
            if (component == null)
            {
                continue;
            }

            string status = GetString(component, "status").ToLowerInvariant();
            if (status == "operational" || status.Length == 0)
            {
                continue;
            }

            int componentRegion = GetCloudflareRegionMask(GetString(component, "name"));
            if (componentRegion == 0)
            {
                return true;
            }

            sawRegionalSignal = true;
            if ((componentRegion & regionMask) != 0)
            {
                return true;
            }
        }

        return !sawRegionalSignal;
    }

    private static bool HasRelevantCloudflareItems(object[] items, int regionMask, ref bool sawRegionalSignal)
    {
        for (int i = 0; items != null && i < items.Length; i++)
        {
            Dictionary<string, object> item = items[i] as Dictionary<string, object>;
            if (item == null)
            {
                continue;
            }

            bool itemHasComponent = false;
            if (HasRelevantCloudflareComponents(GetArray(item, "components"), regionMask, ref sawRegionalSignal, ref itemHasComponent) ||
                HasRelevantCloudflareComponents(GetArray(item, "affected_components"), regionMask, ref sawRegionalSignal, ref itemHasComponent))
            {
                return true;
            }

            if (!itemHasComponent)
            {
                int itemRegion = GetCloudflareRegionMask(GetString(item, "name") + " " + GetString(item, "impact_override"));
                if (itemRegion == 0)
                {
                    return true;
                }

                sawRegionalSignal = true;
                if ((itemRegion & regionMask) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasRelevantCloudflareComponents(object[] components, int regionMask, ref bool sawRegionalSignal, ref bool itemHasComponent)
    {
        for (int i = 0; components != null && i < components.Length; i++)
        {
            Dictionary<string, object> component = components[i] as Dictionary<string, object>;
            if (component == null)
            {
                continue;
            }

            itemHasComponent = true;
            string name = GetString(component, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = GetString(component, "code");
            }

            int componentRegion = GetCloudflareRegionMask(name);
            if (componentRegion == 0)
            {
                return true;
            }

            sawRegionalSignal = true;
            if ((componentRegion & regionMask) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGoogleIncidentRelevant(Dictionary<string, object> incident, int regionMask)
    {
        object[] locations = GetArray(incident, "currently_affected_locations");
        if (locations == null || locations.Length == 0)
        {
            return true;
        }

        for (int i = 0; i < locations.Length; i++)
        {
            Dictionary<string, object> location = locations[i] as Dictionary<string, object>;
            if (location == null)
            {
                continue;
            }

            int locationRegion = GetGoogleRegionMask(GetString(location, "id"), GetString(location, "title"));
            if (locationRegion == 0 || (locationRegion & regionMask) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetCloudflareRegionMask(string text)
    {
        string value = (text ?? string.Empty).ToLowerInvariant();
        int mask = 0;
        if (ContainsAny(value, "japan", "tokyo", "osaka", "fukuoka", "nrt", "kix"))
        {
            mask |= WidgetSettings.CloudStatusRegionJapan | WidgetSettings.CloudStatusRegionAsiaPacific;
        }

        if (ContainsAny(value, "asia", "apac", "singapore", "hong kong", "taiwan", "taipei", "seoul", "korea", "india", "mumbai", "chennai", "delhi", "sydney", "melbourne", "australia", "auckland", "jakarta", "manila", "bangkok", "kuala", "china"))
        {
            mask |= WidgetSettings.CloudStatusRegionAsiaPacific;
        }

        if (ContainsAny(value, "north america", "united states", "usa", "canada", "mexico", "los angeles", "san jose", "seattle", "chicago", "dallas", "miami", "newark", "toronto", "vancouver", "atlanta", "denver", "houston", "montreal"))
        {
            mask |= WidgetSettings.CloudStatusRegionNorthAmerica;
        }

        if (ContainsAny(value, "europe", "london", "frankfurt", "amsterdam", "paris", "madrid", "milan", "stockholm", "warsaw", "zurich", "vienna", "dublin", "lisbon", "prague", "rome", "berlin"))
        {
            mask |= WidgetSettings.CloudStatusRegionEurope;
        }

        return mask;
    }

    private static int GetGoogleRegionMask(string id, string title)
    {
        string value = ((id ?? string.Empty) + " " + (title ?? string.Empty)).ToLowerInvariant();
        if (ContainsAny(value, "global", "multi-region"))
        {
            return WidgetSettings.CloudStatusRegionMaskAll;
        }

        int mask = 0;
        if (ContainsAny(value, "asia-northeast1", "asia-northeast2", "tokyo", "osaka", "japan"))
        {
            mask |= WidgetSettings.CloudStatusRegionJapan | WidgetSettings.CloudStatusRegionAsiaPacific;
        }

        if (value.StartsWith("asia-", StringComparison.Ordinal) ||
            value.StartsWith("australia-", StringComparison.Ordinal) ||
            ContainsAny(value, "asia", "australia", "mumbai", "delhi", "chennai", "singapore", "hong kong", "taiwan", "seoul", "sydney"))
        {
            mask |= WidgetSettings.CloudStatusRegionAsiaPacific;
        }

        if (value.StartsWith("us-", StringComparison.Ordinal) ||
            value.StartsWith("northamerica-", StringComparison.Ordinal) ||
            ContainsAny(value, "united states", "north america", "canada", "iowa", "oregon", "virginia", "carolina", "columbus", "dallas", "las vegas", "los angeles", "montreal", "toronto"))
        {
            mask |= WidgetSettings.CloudStatusRegionNorthAmerica;
        }

        if (value.StartsWith("europe-", StringComparison.Ordinal) ||
            ContainsAny(value, "europe", "london", "frankfurt", "belgium", "netherlands", "paris", "madrid", "milan", "warsaw", "zurich", "berlin"))
        {
            mask |= WidgetSettings.CloudStatusRegionEurope;
        }

        return mask;
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
        {
            if (value.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, object> GetDictionary(Dictionary<string, object> dictionary, string key)
    {
        if (dictionary == null || string.IsNullOrEmpty(key) || !dictionary.ContainsKey(key))
        {
            return null;
        }

        return dictionary[key] as Dictionary<string, object>;
    }

    private static object[] GetArray(Dictionary<string, object> dictionary, string key)
    {
        if (dictionary == null || string.IsNullOrEmpty(key) || !dictionary.ContainsKey(key))
        {
            return null;
        }

        return ToObjectArray(dictionary[key]);
    }

    private static object[] ToObjectArray(object value)
    {
        object[] array = value as object[];
        if (array != null)
        {
            return array;
        }

        ArrayList list = value as ArrayList;
        if (list == null)
        {
            return null;
        }

        object[] result = new object[list.Count];
        list.CopyTo(result);
        return result;
    }

    private static string GetString(Dictionary<string, object> dictionary, string key)
    {
        if (dictionary == null || string.IsNullOrEmpty(key) || !dictionary.ContainsKey(key) || dictionary[key] == null)
        {
            return string.Empty;
        }

        return Convert.ToString(dictionary[key], CultureInfo.InvariantCulture);
    }

    private static string EmptyToFallback(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
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
            message = ex.GetType().Name;
        }

        message = message.Replace("\r", " ").Replace("\n", " ").Trim();
        if (message.Length > 64)
        {
            message = message.Substring(0, 64);
        }

        return ex.GetType().Name + ":" + message;
    }

    private enum CloudTargetKind
    {
        Http,
        Statuspage,
        GoogleServiceHealth
    }

    private sealed class CloudTarget
    {
        private CloudTarget(string key, string host, string apiUrl, CloudTargetKind kind)
        {
            this.Key = key;
            this.Host = host;
            this.ApiUrl = apiUrl;
            this.Kind = kind;
        }

        public string Key;
        public string Host;
        public string ApiUrl;
        public CloudTargetKind Kind;
        public bool UsesOfficialApi
        {
            get { return this.Kind == CloudTargetKind.Statuspage || this.Kind == CloudTargetKind.GoogleServiceHealth; }
        }

        public bool UsesRegionFilter
        {
            get
            {
                return string.Equals(this.Key, "cloudflare", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(this.Key, "google", StringComparison.OrdinalIgnoreCase);
            }
        }

        public static CloudTarget Http(string key, string host)
        {
            return new CloudTarget(key, host, string.Empty, CloudTargetKind.Http);
        }

        public static CloudTarget Statuspage(string key, string apiUrl)
        {
            return new CloudTarget(key, string.Empty, apiUrl, CloudTargetKind.Statuspage);
        }

        public static CloudTarget GoogleServiceHealth(string key, string apiUrl)
        {
            return new CloudTarget(key, string.Empty, apiUrl, CloudTargetKind.GoogleServiceHealth);
        }

    }

    private sealed class HttpTextResult
    {
        public int StatusCode;
        public string Text;
        public int ElapsedMs;
        public bool FromCache;
    }

    private sealed class HttpTextCacheEntry
    {
        public string Text;
        public string ETag;
        public DateTime LastModifiedUtc;
    }

    private sealed class CloudEndpointCacheEntry
    {
        public CloudEndpointSnapshot Snapshot;
        public DateTime ExpiresUtc;
        public int RegionMask;
    }

    private sealed class IndexedCloudEndpointSample
    {
        public int TargetIndex;
        public CloudEndpointSample Sample;
    }

    private sealed class CloudEndpointSample
    {
        public string Key;
        public CloudEndpointStatus Status;
        public int HttpStatusCode;
        public int LatencyMs;
        public string AlertName;
        public string AlertReason;
        public string Reason;
        public string Source;

        public static CloudEndpointSample CreateNormal(string key, int latencyMs, string source, string reason)
        {
            return new CloudEndpointSample
            {
                Key = key,
                Status = CloudEndpointStatus.Normal,
                HttpStatusCode = 0,
                LatencyMs = latencyMs,
                AlertName = string.Empty,
                AlertReason = string.Empty,
                Reason = reason ?? string.Empty,
                Source = source ?? string.Empty
            };
        }

        public static CloudEndpointSample CreateAbnormal(string key, string alertReason, string reason, string source)
        {
            return new CloudEndpointSample
            {
                Key = key,
                Status = CloudEndpointStatus.Abnormal,
                HttpStatusCode = ExtractHttpStatusCode(reason),
                LatencyMs = 0,
                AlertName = string.Empty,
                AlertReason = EmptyToFallback(alertReason, "状态异常"),
                Reason = reason ?? string.Empty,
                Source = source ?? string.Empty
            };
        }

        public static CloudEndpointSample CreateDown(string key, string alertReason, string reason, string source)
        {
            return new CloudEndpointSample
            {
                Key = key,
                Status = CloudEndpointStatus.Down,
                HttpStatusCode = ExtractHttpStatusCode(reason),
                LatencyMs = 0,
                AlertName = string.Empty,
                AlertReason = EmptyToFallback(alertReason, "无法连接"),
                Reason = reason ?? string.Empty,
                Source = source ?? string.Empty
            };
        }

        private static int ExtractHttpStatusCode(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason) || !reason.StartsWith("HTTP=", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            int value;
            return int.TryParse(reason.Substring(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }
    }
}
