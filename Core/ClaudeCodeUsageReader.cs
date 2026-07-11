using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class ClaudeCodeUsageSnapshot
{
    public int FiveHourPercent { get; set; }
    public int WeeklyPercent { get; set; }
    public DateTime FiveHourResetLocal { get; set; }
    public bool FiveHourResetKnown { get; set; }
    public DateTime WeeklyResetLocal { get; set; }
    public bool WeeklyResetKnown { get; set; }
    public DateTime SourceUpdatedUtc { get; set; }
    public bool SourceUpdatedKnown { get; set; }

    public ClaudeRadarQuotaSnapshot ToClaudeRadarQuotaSnapshot()
    {
        return new ClaudeRadarQuotaSnapshot
        {
            Known = true,
            Source = "personal",
            FiveHourPercent = ClampPercent(this.FiveHourPercent),
            WeeklyPercent = ClampPercent(this.WeeklyPercent),
            FiveHourResetText = this.FiveHourResetKnown
                ? this.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture)
                : "N/A",
            WeeklyResetText = this.WeeklyResetKnown
                ? this.WeeklyResetLocal.ToString("MM/dd", CultureInfo.CurrentCulture)
                : "N/A",
            UpdatedAtUtc = this.SourceUpdatedUtc,
            UpdatedAtKnown = this.SourceUpdatedKnown
        };
    }

    public static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }
}

internal sealed class ClaudeCodeUsageReadResult
{
    public bool TokenConfigured { get; set; }
    public bool Success { get; set; }
    public bool RateLimited { get; set; }
    public ClaudeCodeUsageSnapshot Snapshot { get; set; }
    public ClaudeRadarServiceState State { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
}

internal static class ClaudeCodeUsageReader
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    public const string SetupTokenFileName = "claude-code-oauth-token.bin";
    public const string StatusLineQuotaFileName = "claude-statusline-quota.ini";
    public const int NormalRefreshSeconds = 300;
    public const int ErrorRefreshSeconds = 600;
    public const int RateLimitRefreshSeconds = 900;

    private const int RequestTimeoutMs = 10000;
    private const int StatusLineCacheMaxAgeMinutes = 360;
    private const string LegacySetupTokenFileName = "claude-code-oauth-token.txt";
    private const string StatusLineBridgeScriptName = "desktop-codex-statusline-bridge.ps1";
    private const string StatusLineBridgeMarker = "# Desktop Codex Assistant Claude statusline bridge v2";
    private static readonly object StatusLineBridgeInstallLock = new object();
    private static bool statusLineBridgeInstallAttempted;
    private static bool statusLineBridgeInstallReady;
    private static string statusLineBridgeInstallErrorCode = string.Empty;
    private static string statusLineBridgeInstallErrorMessage = string.Empty;
    private static readonly string[] MessagesFallbackModels = new string[]
    {
        "claude-3-haiku-20240307",
        "claude-haiku-4-5-20251001"
    };

    public static ClaudeCodeUsageReadResult Read(WidgetSettings settings)
    {
        string token = ReadConfiguredSetupToken();
        return ResolveReadSources(
            token,
            delegate { return ReadViaSetupToken(settings); },
            ReadStatusLineQuotaCacheResult,
            delegate
            {
                string installErrorCode;
                string installErrorMessage;
                return EnsureStatusLineBridgeInstalled(out installErrorCode, out installErrorMessage);
            });
    }

    private static ClaudeCodeUsageReadResult ResolveReadSources(
        string token,
        Func<ClaudeCodeUsageReadResult> readOAuth,
        Func<ClaudeCodeUsageReadResult> readStatusLine,
        Func<bool> ensureBridge)
    {
        bool tokenConfigured = !string.IsNullOrWhiteSpace(token);
        ClaudeCodeUsageReadResult oauthResult = null;
        if (tokenConfigured)
        {
            oauthResult = readOAuth == null ? null : readOAuth();
            if (oauthResult != null && (oauthResult.Success || IsTokenInvalidResult(oauthResult)))
            {
                return oauthResult;
            }
        }

        ClaudeCodeUsageReadResult statusLineResult = readStatusLine == null ? null : readStatusLine();
        if (statusLineResult != null && statusLineResult.Success)
        {
            return statusLineResult;
        }

        bool bridgeReady = ensureBridge != null && ensureBridge();
        if (bridgeReady)
        {
            statusLineResult = readStatusLine == null ? null : readStatusLine();
            if (statusLineResult != null && statusLineResult.Success)
            {
                return statusLineResult;
            }
        }

        if (tokenConfigured && oauthResult != null)
        {
            return oauthResult;
        }

        return BuildError(false, ClaudeRadarServiceState.Unavailable, "NO_SETUP_TOKEN", "未绑定setup-token");
    }

    private static ClaudeCodeUsageReadResult ReadStatusLineQuotaCacheResult()
    {
        ClaudeCodeUsageSnapshot snapshot;
        string errorCode;
        string errorMessage;
        ClaudeRadarServiceState errorState;
        return TryReadStatusLineQuotaCache(out snapshot, out errorCode, out errorMessage, out errorState)
            ? BuildSuccess(snapshot)
            : BuildError(false, errorState, errorCode, errorMessage);
    }

    // Used when a setup token is configured (CLAUDE_CODE_OAUTH_TOKEN env var or
    // the local DPAPI-protected setup-token file). Prefers the free OAuth usage endpoint; the
    // Messages-header fallback below can spend a tiny amount of Claude quota.
    public static ClaudeCodeUsageReadResult ReadViaSetupToken(WidgetSettings settings)
    {
        if (!IsNetworkAvailable())
        {
            return BuildError(false, ClaudeRadarServiceState.Offline, "OFFLINE", "无网络");
        }

        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, UsageUrl, out aiBlockReason))
        {
            return BuildError(false, ClaudeRadarServiceState.Unavailable, "AI_BLOCK", "AI阻断");
        }

        string token = ReadConfiguredSetupToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return BuildError(false, ClaudeRadarServiceState.Unavailable, "NO_SETUP_TOKEN", "无setup-token");
        }

        // Community Windows monitors use the OAuth usage endpoint as the authoritative source,
        // then consult Messages API rate-limit headers only as a bounded fallback. Keep that
        // order so we do not burn an extra request every normal polling cycle, and never log or
        // persist the OAuth token or response body.
        ClaudeCodeUsageReadResult usageResult = FetchUsageEndpoint(token);
        return ResolveSetupTokenResult(
            usageResult,
            delegate { return FetchUsageViaMessagesHeaders(settings, token); });
    }

    private static ClaudeCodeUsageReadResult ResolveSetupTokenResult(
        ClaudeCodeUsageReadResult usageResult,
        Func<ClaudeCodeUsageReadResult> readHeaders)
    {
        if (usageResult != null &&
            usageResult.Success &&
            usageResult.Snapshot != null &&
            usageResult.Snapshot.FiveHourResetKnown &&
            usageResult.Snapshot.WeeklyResetKnown)
        {
            return usageResult;
        }

        // An invalid credential cannot be repaired by the billable Messages endpoint. Returning
        // immediately keeps the error actionable and guarantees that a 401/403 consumes no quota.
        if (IsTokenInvalidResult(usageResult))
        {
            return usageResult;
        }

        ClaudeCodeUsageReadResult headerResult = readHeaders == null ? null : readHeaders();
        if (headerResult != null && headerResult.Success)
        {
            if (usageResult != null && usageResult.Success && usageResult.Snapshot != null)
            {
                MergeMissingResetTimes(usageResult.Snapshot, headerResult.Snapshot);
                return usageResult;
            }

            return headerResult;
        }

        return usageResult ?? headerResult ?? BuildError(true, ClaudeRadarServiceState.Unreachable, "NET", "无法连接");
    }

    private static bool IsTokenInvalidResult(ClaudeCodeUsageReadResult result)
    {
        return result != null &&
            string.Equals(result.ErrorCode, "TOKEN_INVALID", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadStatusLineQuotaCache(
        out ClaudeCodeUsageSnapshot snapshot,
        out string errorCode,
        out string errorMessage,
        out ClaudeRadarServiceState errorState)
    {
        snapshot = null;
        errorCode = string.Empty;
        errorMessage = string.Empty;
        errorState = ClaudeRadarServiceState.Incomplete;

        string path = StatusLineQuotaCachePath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            errorCode = "NO_STATUSLINE_CACHE";
            errorMessage = "等Claude刷新";
            return false;
        }

        string content;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            errorCode = "STATUSLINE_READ";
            errorMessage = "缓存读取失败";
            errorState = ClaudeRadarServiceState.Unreachable;
            return false;
        }

        return TryParseStatusLineQuotaCache(content, out snapshot, out errorCode, out errorMessage, out errorState);
    }

    private static bool TryParseStatusLineQuotaCache(
        string content,
        out ClaudeCodeUsageSnapshot snapshot,
        out string errorCode,
        out string errorMessage,
        out ClaudeRadarServiceState errorState)
    {
        snapshot = null;
        errorCode = string.Empty;
        errorMessage = string.Empty;
        errorState = ClaudeRadarServiceState.Incomplete;

        Dictionary<string, string> values = ParseIniContent(content);
        if (values.Count == 0)
        {
            errorCode = "NO_STATUSLINE_CACHE";
            errorMessage = "等Claude刷新";
            return false;
        }

        int fiveHourPercent;
        int weeklyPercent;
        if (!TryReadPercent(values, "FiveHourPercent", out fiveHourPercent) ||
            !TryReadPercent(values, "WeeklyPercent", out weeklyPercent))
        {
            errorCode = "STATUSLINE_INCOMPLETE";
            errorMessage = "状态缓存不完整";
            return false;
        }

        DateTime updatedUtc;
        bool updatedKnown = TryReadUtcDate(values, "SourceUpdatedUtc", out updatedUtc);
        if (updatedKnown)
        {
            TimeSpan age = DateTime.UtcNow - updatedUtc;
            if (age > TimeSpan.FromMinutes(StatusLineCacheMaxAgeMinutes))
            {
                errorCode = "STATUSLINE_STALE";
                errorMessage = "状态缓存过期";
                return false;
            }
        }
        else
        {
            updatedUtc = DateTime.UtcNow;
        }

        DateTime fiveHourReset;
        bool fiveHourResetKnown = TryReadLocalDate(values, "FiveHourReset", out fiveHourReset);
        DateTime weeklyReset;
        bool weeklyResetKnown = TryReadLocalDate(values, "WeeklyReset", out weeklyReset);

        snapshot = new ClaudeCodeUsageSnapshot
        {
            FiveHourPercent = ClaudeCodeUsageSnapshot.ClampPercent(fiveHourPercent),
            WeeklyPercent = ClaudeCodeUsageSnapshot.ClampPercent(weeklyPercent),
            FiveHourResetLocal = fiveHourReset,
            FiveHourResetKnown = fiveHourResetKnown,
            WeeklyResetLocal = weeklyReset,
            WeeklyResetKnown = weeklyResetKnown,
            SourceUpdatedUtc = updatedUtc,
            SourceUpdatedKnown = updatedKnown
        };
        return true;
    }

    private static bool EnsureStatusLineBridgeInstalled(out string errorCode, out string errorMessage)
    {
        lock (StatusLineBridgeInstallLock)
        {
            if (statusLineBridgeInstallAttempted)
            {
                errorCode = statusLineBridgeInstallErrorCode;
                errorMessage = statusLineBridgeInstallErrorMessage;
                return statusLineBridgeInstallReady;
            }

            statusLineBridgeInstallAttempted = true;
            statusLineBridgeInstallReady = TryEnsureStatusLineBridgeInstalled(out statusLineBridgeInstallErrorCode, out statusLineBridgeInstallErrorMessage);
            errorCode = statusLineBridgeInstallErrorCode;
            errorMessage = statusLineBridgeInstallErrorMessage;
            return statusLineBridgeInstallReady;
        }
    }

    private static bool TryEnsureStatusLineBridgeInstalled(out string errorCode, out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
        {
            errorCode = "STATUSLINE_HOME";
            errorMessage = "无用户目录";
            return false;
        }

        string claudeDirectory = Path.Combine(profile, ".claude");
        string scriptPath = Path.Combine(claudeDirectory, StatusLineBridgeScriptName);
        string settingsPath = Path.Combine(claudeDirectory, "settings.json");
        try
        {
            Directory.CreateDirectory(claudeDirectory);
            WriteStatusLineBridgeScript(scriptPath);
        }
        catch
        {
            errorCode = "STATUSLINE_SCRIPT";
            errorMessage = "脚本写入失败";
            return false;
        }

        Dictionary<string, object> settings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(settingsPath))
        {
            try
            {
                string content = File.ReadAllText(settingsPath, Encoding.UTF8);
                Dictionary<string, object> parsed = new JavaScriptSerializer().DeserializeObject(content) as Dictionary<string, object>;
                if (parsed != null)
                {
                    settings = parsed;
                }
            }
            catch
            {
                errorCode = "STATUSLINE_SETTINGS_PARSE";
                errorMessage = "设置解析失败";
                return false;
            }
        }

        object statusLineObject;
        Dictionary<string, object> statusLine = null;
        if (settings.TryGetValue("statusLine", out statusLineObject))
        {
            statusLine = statusLineObject as Dictionary<string, object>;
        }

        string currentCommand = statusLine == null ? string.Empty : Convert.ToString(ReadObject(statusLine, "command"), CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(currentCommand) && !IsStatusLineBridgeCommand(currentCommand))
        {
            errorCode = "STATUSLINE_CUSTOM";
            errorMessage = "已有状态行";
            return false;
        }

        string command = BuildStatusLineBridgeCommand(scriptPath);
        if (statusLine == null)
        {
            statusLine = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        statusLine["type"] = "command";
        statusLine["command"] = command;
        if (!statusLine.ContainsKey("padding"))
        {
            statusLine["padding"] = 0;
        }

        settings["statusLine"] = statusLine;
        try
        {
            if (File.Exists(settingsPath))
            {
                string backupPath = settingsPath + ".desktopcodex.bak";
                if (!File.Exists(backupPath))
                {
                    File.Copy(settingsPath, backupPath, false);
                }
            }

            string json = new JavaScriptSerializer().Serialize(settings);
            File.WriteAllText(settingsPath, json + Environment.NewLine, SharedEncoding.Utf8NoBom);
        }
        catch
        {
            errorCode = "STATUSLINE_SETTINGS_WRITE";
            errorMessage = "设置写入失败";
            return false;
        }

        return true;
    }

    private static object ReadObject(Dictionary<string, object> values, string key)
    {
        object value;
        return values != null && values.TryGetValue(key, out value) ? value : null;
    }

    private static bool IsStatusLineBridgeCommand(string command)
    {
        return !string.IsNullOrWhiteSpace(command) &&
            command.IndexOf(StatusLineBridgeScriptName, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildStatusLineBridgeCommand(string scriptPath)
    {
        return "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath.Replace("\"", "\\\"") + "\"";
    }

    private static void WriteStatusLineBridgeScript(string path)
    {
        string content = BuildStatusLineBridgeScript();
        if (File.Exists(path))
        {
            try
            {
                string existing = File.ReadAllText(path, Encoding.UTF8);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                {
                    return;
                }
            }
            catch
            {
            }
        }

        File.WriteAllText(path, content, SharedEncoding.Utf8NoBom);
    }

    private static string BuildStatusLineBridgeScript()
    {
        string nl = "\r\n";
        return
            StatusLineBridgeMarker + nl +
            "$ErrorActionPreference = 'SilentlyContinue'" + nl +
            "function Get-Field($obj, [string[]]$names) {" + nl +
            "  foreach ($name in $names) {" + nl +
            "    if ($null -eq $obj) { continue }" + nl +
            "    $prop = $obj.PSObject.Properties[$name]" + nl +
            "    if ($null -ne $prop -and $null -ne $prop.Value) { return $prop.Value }" + nl +
            "  }" + nl +
            "  return $null" + nl +
            "}" + nl +
            "function Get-Number($obj, [string[]]$names) {" + nl +
            "  $value = Get-Field $obj $names" + nl +
            "  if ($null -eq $value) { return $null }" + nl +
            "  $text = [string]$value" + nl +
            "  $number = 0.0" + nl +
            "  if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$number)) { return $number }" + nl +
            "  return $null" + nl +
            "}" + nl +
            "function Get-DateIso($obj, [string[]]$names) {" + nl +
            "  $value = Get-Field $obj $names" + nl +
            "  if ($null -eq $value) { return $null }" + nl +
            "  $text = ([string]$value).Trim()" + nl +
            "  if ($text.Length -eq 0) { return $null }" + nl +
            "  $seconds = 0L" + nl +
            "  if ([long]::TryParse($text, [ref]$seconds) -and $seconds -gt 0) { return [DateTimeOffset]::FromUnixTimeSeconds($seconds).UtcDateTime.ToString('o') }" + nl +
            "  $date = [DateTimeOffset]::MinValue" + nl +
            "  if ([DateTimeOffset]::TryParse($text, [ref]$date)) { return $date.UtcDateTime.ToString('o') }" + nl +
            "  return $null" + nl +
            "}" + nl +
            "function Remaining-Percent($slot) {" + nl +
            "  $used = Get-Number $slot @('used_percentage','used_percent','utilization','percent')" + nl +
            "  if ($null -eq $used) { return $null }" + nl +
            "  if ($used -le 1.0) { $used = $used * 100.0 }" + nl +
            "  $remaining = [int][Math]::Round(100.0 - $used)" + nl +
            "  if ($remaining -lt 0) { $remaining = 0 }" + nl +
            "  if ($remaining -gt 100) { $remaining = 100 }" + nl +
            "  return $remaining" + nl +
            "}" + nl +
            "$json = [Console]::In.ReadToEnd()" + nl +
            "if ([string]::IsNullOrWhiteSpace($json)) { exit 0 }" + nl +
            "try { $root = $json | ConvertFrom-Json } catch { exit 0 }" + nl +
            "$rateLimits = Get-Field $root @('rate_limits','rateLimits')" + nl +
            "$five = Get-Field $rateLimits @('five_hour','fiveHour','primary')" + nl +
            "$seven = Get-Field $rateLimits @('seven_day','sevenDay','secondary','weekly')" + nl +
            "$fiveRemaining = Remaining-Percent $five" + nl +
            "$sevenRemaining = Remaining-Percent $seven" + nl +
            "if ($null -eq $fiveRemaining -or $null -eq $sevenRemaining) { Write-Output 'Claude'; exit 0 }" + nl +
            "$fiveReset = Get-DateIso $five @('reset_at','resets_at','reset','resetsAt')" + nl +
            "$sevenReset = Get-DateIso $seven @('reset_at','resets_at','reset','resetsAt')" + nl +
            "$dir = Join-Path $env:LOCALAPPDATA 'DesktopCodexAssistant'" + nl +
            "[System.IO.Directory]::CreateDirectory($dir) | Out-Null" + nl +
            "$path = Join-Path $dir '" + StatusLineQuotaFileName + "'" + nl +
            "$tmp = $path + '.tmp'" + nl +
            "$lines = New-Object System.Collections.Generic.List[string]" + nl +
            "$lines.Add('Version=1')" + nl +
            "$lines.Add('Source=claude_statusline')" + nl +
            "$lines.Add('FiveHourPercent=' + $fiveRemaining.ToString([System.Globalization.CultureInfo]::InvariantCulture))" + nl +
            "$lines.Add('WeeklyPercent=' + $sevenRemaining.ToString([System.Globalization.CultureInfo]::InvariantCulture))" + nl +
            "if ($fiveReset) { $lines.Add('FiveHourReset=' + $fiveReset) }" + nl +
            "if ($sevenReset) { $lines.Add('WeeklyReset=' + $sevenReset) }" + nl +
            "$lines.Add('SourceUpdatedUtc=' + [DateTime]::UtcNow.ToString('o'))" + nl +
            "$utf8 = New-Object System.Text.UTF8Encoding($false)" + nl +
            "[System.IO.File]::WriteAllLines($tmp, $lines.ToArray(), $utf8)" + nl +
            "if (Test-Path $path) { Remove-Item -LiteralPath $path -Force }" + nl +
            "Move-Item -LiteralPath $tmp -Destination $path -Force" + nl +
            "Write-Output ('5h {0}% 7d {1}%' -f $fiveRemaining, $sevenRemaining)" + nl;
    }

    public static string StatusLineQuotaCachePath
    {
        get { return Path.Combine(Logger.DirectoryPath, StatusLineQuotaFileName); }
    }

    private static ClaudeCodeUsageReadResult FetchUsageEndpoint(string token)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(UsageUrl);
        request.Method = "GET";
        request.Accept = "application/json,text/plain,*/*";
        request.UserAgent = "claude-code/2.1.121";
        request.Timeout = RequestTimeoutMs;
        request.ReadWriteTimeout = RequestTimeoutMs;
        request.Headers["Authorization"] = "Bearer " + token;
        request.Headers["anthropic-beta"] = "oauth-2025-04-20";
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        try
        {
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                string content = ReadResponseText(response);
                return ParseResponse(content, true, (int)response.StatusCode);
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                using (response)
                {
                    int statusCode = (int)response.StatusCode;
                    string content = ReadResponseText(response);
                    ClaudeCodeUsageReadResult parsed = ParseResponse(content, true, statusCode);
                    if (parsed != null && parsed.RateLimited)
                    {
                        return parsed;
                    }

                    return BuildError(
                        true,
                        statusCode == 401 || statusCode == 403
                            ? ClaudeRadarServiceState.Unavailable
                            : ClaudeRadarServiceState.Unreachable,
                        statusCode == 401 || statusCode == 403
                            ? "TOKEN_INVALID"
                            : statusCode.ToString(CultureInfo.InvariantCulture),
                        GetHttpErrorReason(statusCode));
                }
            }

            return BuildError(true, ClaudeRadarServiceState.Unreachable, "NET", "无法连接");
        }
    }

    private static ClaudeCodeUsageReadResult FetchUsageViaMessagesHeaders(WidgetSettings settings, string token)
    {
        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, MessagesUrl, out aiBlockReason))
        {
            return BuildError(true, ClaudeRadarServiceState.Unavailable, "AI_BLOCK", "AI阻断");
        }

        for (int i = 0; i < MessagesFallbackModels.Length; i++)
        {
            ClaudeCodeUsageReadResult result = FetchUsageViaMessagesHeaders(token, MessagesFallbackModels[i]);
            if (result != null && result.Success)
            {
                return result;
            }

            if (result != null &&
                (string.Equals(result.ErrorCode, "401", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(result.ErrorCode, "403", StringComparison.OrdinalIgnoreCase)))
            {
                return result;
            }
        }

        return BuildError(true, ClaudeRadarServiceState.Unreachable, "NO_HEADERS", "无统一限额头");
    }

    private static ClaudeCodeUsageReadResult FetchUsageViaMessagesHeaders(string token, string model)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(MessagesUrl);
        request.Method = "POST";
        request.Accept = "application/json,text/plain,*/*";
        request.ContentType = "application/json";
        request.UserAgent = "claude-code/2.1.121";
        request.Timeout = RequestTimeoutMs;
        request.ReadWriteTimeout = RequestTimeoutMs;
        request.Headers["Authorization"] = "Bearer " + token;
        request.Headers["anthropic-version"] = "2023-06-01";
        request.Headers["anthropic-beta"] = "oauth-2025-04-20";
        request.Headers["Cache-Control"] = "no-store, no-cache";
        request.Headers["Pragma"] = "no-cache";

        string safeModel = string.IsNullOrWhiteSpace(model) ? "claude-3-haiku-20240307" : model.Trim();
        string body = "{\"model\":\"" + JsonEscape(safeModel) + "\",\"max_tokens\":1,\"messages\":[{\"role\":\"user\",\"content\":\".\"}]}";
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        request.ContentLength = bytes.Length;
        try
        {
            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            {
                return ParseRateLimitHeaders(response.Headers, true, (int)response.StatusCode);
            }
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                using (response)
                {
                    ClaudeCodeUsageReadResult parsed = ParseRateLimitHeaders(response.Headers, true, (int)response.StatusCode);
                    if (parsed != null && parsed.Success)
                    {
                        return parsed;
                    }

                    int statusCode = (int)response.StatusCode;
                    return BuildError(
                        true,
                        statusCode == 401 || statusCode == 403
                            ? ClaudeRadarServiceState.Unavailable
                            : ClaudeRadarServiceState.Unreachable,
                        statusCode.ToString(CultureInfo.InvariantCulture),
                        GetHttpErrorReason(statusCode));
                }
            }

            return BuildError(true, ClaudeRadarServiceState.Unreachable, "NET", "无法连接");
        }
    }

    public static ClaudeCodeUsageReadResult ParseResponse(
        string content,
        bool tokenConfigured,
        int statusCode)
    {
        if (statusCode == 429)
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Unavailable, "429", "限流");
        }

        if (statusCode < 200 || statusCode >= 300)
        {
            return BuildError(
                tokenConfigured,
                statusCode == 401 || statusCode == 403
                    ? ClaudeRadarServiceState.Unavailable
                    : ClaudeRadarServiceState.Unreachable,
                statusCode == 401 || statusCode == 403
                    ? "TOKEN_INVALID"
                    : statusCode.ToString(CultureInfo.InvariantCulture),
                GetHttpErrorReason(statusCode));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Incomplete, "EMPTY", "空响应");
        }

        Dictionary<string, object> root;
        try
        {
            root = new JavaScriptSerializer().DeserializeObject(content) as Dictionary<string, object>;
        }
        catch
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Incomplete, "PARSE", "解析失败");
        }

        if (root == null)
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Incomplete, "PARSE", "解析失败");
        }

        string errorType = TryGetNestedString(root, "error", "type");
        if (string.Equals(errorType, "rate_limit_error", StringComparison.OrdinalIgnoreCase))
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Unavailable, "429", "限流");
        }

        ClaudeCodeUsageSnapshot snapshot = new ClaudeCodeUsageSnapshot();
        bool foundFiveHour = ApplyUsageSlot(root, "five_hour", true, snapshot);
        bool foundWeekly = ApplyUsageSlot(root, "seven_day", false, snapshot);
        if (!foundFiveHour || !foundWeekly)
        {
            ApplyUsageSlotsFromLimits(root, snapshot, ref foundFiveHour, ref foundWeekly);
        }

        if (!foundFiveHour || !foundWeekly)
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Incomplete, "NO_USAGE", "数据不完整");
        }

        snapshot.SourceUpdatedUtc = DateTime.UtcNow;
        snapshot.SourceUpdatedKnown = true;
        return new ClaudeCodeUsageReadResult
        {
            TokenConfigured = tokenConfigured,
            Success = true,
            RateLimited = false,
            Snapshot = snapshot,
            State = ClaudeRadarServiceState.Normal,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty
        };
    }

    public static ClaudeCodeUsageReadResult ParseRateLimitHeaders(
        WebHeaderCollection headers,
        bool tokenConfigured,
        int statusCode)
    {
        if (statusCode == 429)
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Unavailable, "429", "限流");
        }

        if (headers == null)
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Incomplete, "NO_HEADERS", "无统一限额头");
        }

        ClaudeCodeUsageSnapshot snapshot = new ClaudeCodeUsageSnapshot();
        bool foundFiveHour = ApplyHeaderUsageSlot(
            headers,
            "anthropic-ratelimit-unified-5h-utilization",
            "anthropic-ratelimit-unified-5h-reset",
            true,
            snapshot);
        bool foundWeekly = ApplyHeaderUsageSlot(
            headers,
            "anthropic-ratelimit-unified-7d-utilization",
            "anthropic-ratelimit-unified-7d-reset",
            false,
            snapshot);

        if (!foundFiveHour && !foundWeekly)
        {
            string status = headers["anthropic-ratelimit-unified-status"];
            string claim = headers["anthropic-ratelimit-unified-representative-claim"];
            if (string.Equals(status, "rejected", StringComparison.OrdinalIgnoreCase))
            {
                DateTime overallResetLocal;
                bool overallResetKnown = TryGetUnixHeaderDate(headers, "anthropic-ratelimit-unified-reset", out overallResetLocal);
                if (string.Equals(claim, "five_hour", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.FiveHourPercent = 0;
                    snapshot.FiveHourResetLocal = overallResetLocal;
                    snapshot.FiveHourResetKnown = overallResetKnown;
                    foundFiveHour = true;
                }
                else if (string.Equals(claim, "seven_day", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.WeeklyPercent = 0;
                    snapshot.WeeklyResetLocal = overallResetLocal;
                    snapshot.WeeklyResetKnown = overallResetKnown;
                    foundWeekly = true;
                }
            }
        }

        if (!foundFiveHour || !foundWeekly)
        {
            return BuildError(tokenConfigured, ClaudeRadarServiceState.Incomplete, "NO_HEADERS", "无统一限额头");
        }

        snapshot.SourceUpdatedUtc = DateTime.UtcNow;
        snapshot.SourceUpdatedKnown = true;
        return new ClaudeCodeUsageReadResult
        {
            TokenConfigured = tokenConfigured,
            Success = true,
            RateLimited = false,
            Snapshot = snapshot,
            State = ClaudeRadarServiceState.Normal,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty
        };
    }

    public static void TryWriteQuotaCache(ClaudeCodeUsageSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        ClaudeRadarReader.TryWriteClaudeCodeQuotaCache(snapshot);
    }

    public static void RunSelfTest()
    {
        string sample =
            "{\"five_hour\":{\"utilization\":0.42,\"resets_at\":\"2026-07-04T10:00:00Z\"},\"seven_day\":{\"used_percent\":73,\"resets_at\":\"2026-07-10T12:30:00Z\"}}";
        ClaudeCodeUsageReadResult parsed = ParseResponse(sample, true, 200);
        if (parsed == null ||
            !parsed.Success ||
            parsed.Snapshot == null ||
            parsed.Snapshot.FiveHourPercent != 58 ||
            parsed.Snapshot.WeeklyPercent != 27 ||
            !parsed.Snapshot.FiveHourResetKnown ||
            !parsed.Snapshot.WeeklyResetKnown)
        {
            throw new InvalidOperationException("Claude Code usage parser self-test failed.");
        }

        ClaudeCodeUsageReadResult limited = ParseResponse("{\"error\":{\"type\":\"rate_limit_error\"}}", true, 200);
        if (limited == null || !limited.RateLimited || limited.State != ClaudeRadarServiceState.Unavailable)
        {
            throw new InvalidOperationException("Claude Code usage rate-limit self-test failed.");
        }

        ClaudeCodeUsageReadResult partial = ParseResponse("{\"five_hour\":{\"used_percent\":20}}", true, 200);
        if (partial == null || partial.Success || !string.Equals(partial.ErrorCode, "NO_USAGE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Code usage partial payload self-test failed.");
        }

        string limitsSample =
            "{\"limits\":[{\"group\":\"five_hour\",\"percent\":41,\"resets_at\":\"2026-07-04T10:00:00Z\"},{\"group\":\"seven_day\",\"percent\":72,\"resets_at\":\"2026-07-10T12:30:00Z\"}]}";
        ClaudeCodeUsageReadResult limitsParsed = ParseResponse(limitsSample, true, 200);
        if (limitsParsed == null ||
            !limitsParsed.Success ||
            limitsParsed.Snapshot == null ||
            limitsParsed.Snapshot.FiveHourPercent != 59 ||
            limitsParsed.Snapshot.WeeklyPercent != 28)
        {
            throw new InvalidOperationException("Claude Code usage limits-array parser self-test failed.");
        }

        WebHeaderCollection headers = new WebHeaderCollection();
        headers["anthropic-ratelimit-unified-5h-utilization"] = "0.41";
        headers["anthropic-ratelimit-unified-7d-utilization"] = "0.72";
        headers["anthropic-ratelimit-unified-5h-reset"] = "1783159200";
        headers["anthropic-ratelimit-unified-7d-reset"] = "1783705800";
        ClaudeCodeUsageReadResult headerParsed = ParseRateLimitHeaders(headers, true, 200);
        if (headerParsed == null ||
            !headerParsed.Success ||
            headerParsed.Snapshot == null ||
            headerParsed.Snapshot.FiveHourPercent != 59 ||
            headerParsed.Snapshot.WeeklyPercent != 28 ||
            !headerParsed.Snapshot.FiveHourResetKnown ||
            !headerParsed.Snapshot.WeeklyResetKnown)
        {
            throw new InvalidOperationException("Claude Code usage header fallback self-test failed.");
        }

        if (!string.Equals(NormalizeSetupToken("CLAUDE_CODE_OAUTH_TOKEN=\"oauth-test\"\nignored"), "oauth-test", StringComparison.Ordinal) ||
            !string.Equals(NormalizeSetupToken("export CLAUDE_CODE_OAUTH_TOKEN='oauth-export'"), "oauth-export", StringComparison.Ordinal) ||
            !string.Equals(NormalizeSetupToken("$env:CLAUDE_CODE_OAUTH_TOKEN=\"oauth-powershell\""), "oauth-powershell", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Code setup-token normalization self-test failed.");
        }

        SecretStore.RunSelfTest();
        RunSetupTokenSecretMigrationSelfTest();

        ClaudeCodeUsageSnapshot statusLineSnapshot;
        string statusLineCode;
        string statusLineMessage;
        ClaudeRadarServiceState statusLineState;
        string statusLineIni =
            "Version=1\n" +
            "Source=claude_statusline\n" +
            "FiveHourPercent=61\n" +
            "WeeklyPercent=34\n" +
            "FiveHourReset=2026-07-04T10:00:00Z\n" +
            "WeeklyReset=2026-07-10T12:30:00Z\n" +
            "SourceUpdatedUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n";
        if (!TryParseStatusLineQuotaCache(statusLineIni, out statusLineSnapshot, out statusLineCode, out statusLineMessage, out statusLineState) ||
            statusLineSnapshot == null ||
            statusLineSnapshot.FiveHourPercent != 61 ||
            statusLineSnapshot.WeeklyPercent != 34 ||
            !statusLineSnapshot.FiveHourResetKnown ||
            !statusLineSnapshot.WeeklyResetKnown)
        {
            throw new InvalidOperationException("Claude Code statusline quota cache self-test failed.");
        }

        RunSourceOrderSelfTest();
    }

    private static void RunSourceOrderSelfTest()
    {
        ClaudeCodeUsageReadResult oauthSuccess = BuildSuccess(new ClaudeCodeUsageSnapshot
        {
            FiveHourPercent = 81,
            WeeklyPercent = 72,
            SourceUpdatedUtc = DateTime.UtcNow,
            SourceUpdatedKnown = true
        });
        oauthSuccess.TokenConfigured = true;
        ClaudeCodeUsageReadResult statusLineSuccess = BuildSuccess(new ClaudeCodeUsageSnapshot
        {
            FiveHourPercent = 31,
            WeeklyPercent = 22,
            SourceUpdatedUtc = DateTime.UtcNow,
            SourceUpdatedKnown = true
        });
        int statusLineCalls = 0;
        ClaudeCodeUsageReadResult selected = ResolveReadSources(
            "oauth-test",
            delegate { return oauthSuccess; },
            delegate { statusLineCalls++; return statusLineSuccess; },
            delegate { return true; });
        if (!object.ReferenceEquals(selected, oauthSuccess) || statusLineCalls != 0)
        {
            throw new InvalidOperationException("Claude Code usage OAuth-first source order self-test failed.");
        }

        ClaudeCodeUsageReadResult networkFailure = BuildError(true, ClaudeRadarServiceState.Unreachable, "NET", "无法连接");
        selected = ResolveReadSources(
            "oauth-test",
            delegate { return networkFailure; },
            delegate { statusLineCalls++; return statusLineSuccess; },
            delegate { return false; });
        if (!object.ReferenceEquals(selected, statusLineSuccess))
        {
            throw new InvalidOperationException("Claude Code usage statusline fallback self-test failed.");
        }

        ClaudeCodeUsageReadResult missing = BuildError(false, ClaudeRadarServiceState.Incomplete, "NO_STATUSLINE_CACHE", "等Claude刷新");
        selected = ResolveReadSources(
            string.Empty,
            null,
            delegate { return missing; },
            delegate { return false; });
        if (selected == null || !string.Equals(selected.ErrorCode, "NO_SETUP_TOKEN", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Claude Code usage no-token short-circuit self-test failed.");
        }

        ClaudeCodeUsageReadResult invalid = ParseResponse("{}", true, 401);
        int headerCalls = 0;
        selected = ResolveSetupTokenResult(
            invalid,
            delegate { headerCalls++; return statusLineSuccess; });
        if (!object.ReferenceEquals(selected, invalid) ||
            !string.Equals(selected.ErrorCode, "TOKEN_INVALID", StringComparison.OrdinalIgnoreCase) ||
            headerCalls != 0)
        {
            throw new InvalidOperationException("Claude Code usage invalid-token fallback suppression self-test failed.");
        }

        selected = ResolveSetupTokenResult(
            networkFailure,
            delegate { headerCalls++; return statusLineSuccess; });
        if (!object.ReferenceEquals(selected, statusLineSuccess) || headerCalls != 1)
        {
            throw new InvalidOperationException("Claude Code usage network Messages fallback self-test failed.");
        }
    }

    private static void RunSetupTokenSecretMigrationSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "desktopcodex-claude-token-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string encrypted = Path.Combine(root, SetupTokenFileName);
            string legacy = Path.Combine(root, LegacySetupTokenFileName);
            File.WriteAllText(legacy, "export CLAUDE_CODE_OAUTH_TOKEN='oauth-migrated'\n", SharedEncoding.Utf8NoBom);

            string token;
            bool migrated;
            string errorCode;
            if (!SecretStore.TryReadOrMigrateSecret(encrypted, legacy, NormalizeSetupToken, out token, out migrated, out errorCode) ||
                !migrated ||
                !string.Equals(token, "oauth-migrated", StringComparison.Ordinal) ||
                !File.Exists(encrypted) ||
                File.Exists(legacy) ||
                !File.Exists(legacy + ".migrated"))
            {
                throw new InvalidOperationException("Claude Code setup-token DPAPI migration self-test failed: " + errorCode);
            }

            string encryptedContent = File.ReadAllText(encrypted, Encoding.UTF8);
            if (encryptedContent.IndexOf("oauth-migrated", StringComparison.Ordinal) >= 0 ||
                !string.Equals(SecretStore.Unprotect(encryptedContent), "oauth-migrated", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Claude Code setup-token encrypted content self-test failed.");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
            }
        }
    }

    private static bool ApplyUsageSlot(
        Dictionary<string, object> root,
        string key,
        bool fiveHour,
        ClaudeCodeUsageSnapshot snapshot)
    {
        object slotObject;
        Dictionary<string, object> slot;
        if (root == null ||
            !root.TryGetValue(key, out slotObject) ||
            (slot = slotObject as Dictionary<string, object>) == null)
        {
            return false;
        }

        double usedPercent;
        if (!TryGetUsedPercent(slot, out usedPercent))
        {
            return false;
        }

        int remaining = ClaudeCodeUsageSnapshot.ClampPercent((int)Math.Round(100.0 - usedPercent));
        DateTime resetLocal;
        bool resetKnown = TryGetDate(slot, "resets_at", out resetLocal);
        if (fiveHour)
        {
            snapshot.FiveHourPercent = remaining;
            snapshot.FiveHourResetLocal = resetLocal;
            snapshot.FiveHourResetKnown = resetKnown;
        }
        else
        {
            snapshot.WeeklyPercent = remaining;
            snapshot.WeeklyResetLocal = resetLocal;
            snapshot.WeeklyResetKnown = resetKnown;
        }

        return true;
    }

    private static void ApplyUsageSlotsFromLimits(
        Dictionary<string, object> root,
        ClaudeCodeUsageSnapshot snapshot,
        ref bool foundFiveHour,
        ref bool foundWeekly)
    {
        object limitsObject;
        if (root == null || !root.TryGetValue("limits", out limitsObject))
        {
            return;
        }

        IEnumerable limits = limitsObject as IEnumerable;
        if (limits == null || limitsObject is string)
        {
            return;
        }

        foreach (object item in limits)
        {
            Dictionary<string, object> limit = item as Dictionary<string, object>;
            if (limit == null)
            {
                continue;
            }

            bool fiveHourCandidate;
            bool weeklyCandidate;
            ClassifyLimitGroup(limit, out fiveHourCandidate, out weeklyCandidate);
            if (!fiveHourCandidate && !weeklyCandidate)
            {
                continue;
            }

            double usedPercent;
            if (!TryGetUsedPercent(limit, out usedPercent))
            {
                continue;
            }

            int remaining = ClaudeCodeUsageSnapshot.ClampPercent((int)Math.Round(100.0 - usedPercent));
            DateTime resetLocal;
            bool resetKnown = TryGetDate(limit, "resets_at", out resetLocal);
            if (fiveHourCandidate && !foundFiveHour)
            {
                snapshot.FiveHourPercent = remaining;
                snapshot.FiveHourResetLocal = resetLocal;
                snapshot.FiveHourResetKnown = resetKnown;
                foundFiveHour = true;
            }

            if (weeklyCandidate && !foundWeekly)
            {
                snapshot.WeeklyPercent = remaining;
                snapshot.WeeklyResetLocal = resetLocal;
                snapshot.WeeklyResetKnown = resetKnown;
                foundWeekly = true;
            }
        }
    }

    private static void ClassifyLimitGroup(
        Dictionary<string, object> limit,
        out bool fiveHour,
        out bool weekly)
    {
        fiveHour = false;
        weekly = false;
        if (limit == null)
        {
            return;
        }

        string text = (
            ReadString(limit, "group") + " " +
            ReadString(limit, "name") + " " +
            ReadString(limit, "type") + " " +
            ReadString(limit, "key") + " " +
            ReadString(limit, "period")).ToLowerInvariant();
        if (text.Contains("five") ||
            text.Contains("5h") ||
            text.Contains("5_h") ||
            text.Contains("5-hour") ||
            text.Contains("session"))
        {
            fiveHour = true;
        }

        if (text.Contains("seven") ||
            text.Contains("7d") ||
            text.Contains("7_d") ||
            text.Contains("7-day") ||
            text.Contains("week"))
        {
            weekly = true;
        }
    }

    private static bool ApplyHeaderUsageSlot(
        WebHeaderCollection headers,
        string utilizationHeader,
        string resetHeader,
        bool fiveHour,
        ClaudeCodeUsageSnapshot snapshot)
    {
        double usedFraction;
        if (!TryReadDouble(headers[utilizationHeader], out usedFraction))
        {
            return false;
        }

        double usedPercent = usedFraction <= 1.0 ? usedFraction * 100.0 : usedFraction;
        int remaining = ClaudeCodeUsageSnapshot.ClampPercent((int)Math.Round(100.0 - usedPercent));
        DateTime resetLocal;
        bool resetKnown = TryGetUnixHeaderDate(headers, resetHeader, out resetLocal);
        if (fiveHour)
        {
            snapshot.FiveHourPercent = remaining;
            snapshot.FiveHourResetLocal = resetLocal;
            snapshot.FiveHourResetKnown = resetKnown;
        }
        else
        {
            snapshot.WeeklyPercent = remaining;
            snapshot.WeeklyResetLocal = resetLocal;
            snapshot.WeeklyResetKnown = resetKnown;
        }

        return true;
    }

    private static void MergeMissingResetTimes(ClaudeCodeUsageSnapshot primary, ClaudeCodeUsageSnapshot fallback)
    {
        if (primary == null || fallback == null)
        {
            return;
        }

        if (!primary.FiveHourResetKnown && fallback.FiveHourResetKnown)
        {
            primary.FiveHourResetLocal = fallback.FiveHourResetLocal;
            primary.FiveHourResetKnown = true;
        }

        if (!primary.WeeklyResetKnown && fallback.WeeklyResetKnown)
        {
            primary.WeeklyResetLocal = fallback.WeeklyResetLocal;
            primary.WeeklyResetKnown = true;
        }
    }

    private static bool TryGetUsedPercent(Dictionary<string, object> slot, out double usedPercent)
    {
        usedPercent = 0.0;
        object value;
        if (slot != null && slot.TryGetValue("utilization", out value) && TryReadNumber(value, out usedPercent))
        {
            if (usedPercent <= 1.0)
            {
                usedPercent *= 100.0;
            }

            return true;
        }

        if (slot != null && slot.TryGetValue("used_percent", out value) && TryReadNumber(value, out usedPercent))
        {
            if (usedPercent <= 1.0)
            {
                usedPercent *= 100.0;
            }

            return true;
        }

        if (slot != null && slot.TryGetValue("percent", out value) && TryReadNumber(value, out usedPercent))
        {
            if (usedPercent <= 1.0)
            {
                usedPercent *= 100.0;
            }

            return true;
        }

        return false;
    }

    private static bool TryGetUnixHeaderDate(WebHeaderCollection headers, string key, out DateTime localTime)
    {
        localTime = DateTime.MinValue;
        if (headers == null)
        {
            return false;
        }

        long seconds;
        if (!long.TryParse(headers[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds) || seconds <= 0)
        {
            return false;
        }

        try
        {
            DateTime utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            localTime = utc.ToLocalTime();
            return true;
        }
        catch
        {
            localTime = DateTime.MinValue;
            return false;
        }
    }

    private static bool TryReadDouble(string text, out double value)
    {
        value = 0.0;
        return !string.IsNullOrWhiteSpace(text) &&
            double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string ReadString(Dictionary<string, object> root, string key)
    {
        object value;
        return root != null && root.TryGetValue(key, out value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
    }

    private static string JsonEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static ClaudeCodeUsageReadResult BuildError(
        bool tokenConfigured,
        ClaudeRadarServiceState state,
        string errorCode,
        string message)
    {
        return new ClaudeCodeUsageReadResult
        {
            TokenConfigured = tokenConfigured,
            Success = false,
            RateLimited = string.Equals(errorCode, "429", StringComparison.OrdinalIgnoreCase),
            Snapshot = null,
            State = state,
            ErrorCode = errorCode ?? string.Empty,
            ErrorMessage = message ?? string.Empty
        };
    }

    private static ClaudeCodeUsageReadResult BuildSuccess(ClaudeCodeUsageSnapshot snapshot)
    {
        return new ClaudeCodeUsageReadResult
        {
            TokenConfigured = false,
            Success = snapshot != null,
            RateLimited = false,
            Snapshot = snapshot,
            State = snapshot == null ? ClaudeRadarServiceState.Incomplete : ClaudeRadarServiceState.Normal,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty
        };
    }

    private static Dictionary<string, string> ParseIniContent(string content)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(content))
        {
            return values;
        }

        string[] lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i] == null ? string.Empty : lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = line.Substring(0, equals).Trim();
            string value = line.Substring(equals + 1).Trim();
            if (key.Length > 0)
            {
                values[key] = value;
            }
        }

        return values;
    }

    private static bool TryReadPercent(Dictionary<string, string> values, string key, out int percent)
    {
        percent = 0;
        string text;
        if (values == null || !values.TryGetValue(key, out text))
        {
            return false;
        }

        double value;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        percent = ClaudeCodeUsageSnapshot.ClampPercent((int)Math.Round(value));
        return true;
    }

    private static bool TryReadUtcDate(Dictionary<string, string> values, string key, out DateTime utc)
    {
        utc = DateTime.MinValue;
        DateTime localOrUtc;
        if (!TryReadDate(values, key, out localOrUtc))
        {
            return false;
        }

        utc = localOrUtc.Kind == DateTimeKind.Utc ? localOrUtc : localOrUtc.ToUniversalTime();
        return true;
    }

    private static bool TryReadLocalDate(Dictionary<string, string> values, string key, out DateTime local)
    {
        local = DateTime.MinValue;
        DateTime localOrUtc;
        if (!TryReadDate(values, key, out localOrUtc))
        {
            return false;
        }

        local = localOrUtc.ToLocalTime();
        return true;
    }

    private static bool TryReadDate(Dictionary<string, string> values, string key, out DateTime date)
    {
        date = DateTime.MinValue;
        string text;
        if (values == null || !values.TryGetValue(key, out text) || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        DateTime parsed;
        if (!DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed))
        {
            return false;
        }

        date = parsed;
        return true;
    }

    public static string ReadConfiguredSetupToken()
    {
        string token = GetEnvironmentVariableAnyTarget("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            return token.Trim();
        }

        string secret;
        bool migrated;
        string errorCode;
        return SecretStore.TryReadOrMigrateSecret(
            SetupTokenFilePath,
            LegacySetupTokenFilePath,
            NormalizeSetupToken,
            out secret,
            out migrated,
            out errorCode)
            ? secret
            : string.Empty;
    }

    public static string SetupTokenFilePath
    {
        get { return Path.Combine(Logger.DirectoryPath, SetupTokenFileName); }
    }

    private static string LegacySetupTokenFilePath
    {
        get { return Path.Combine(Logger.DirectoryPath, LegacySetupTokenFileName); }
    }

    private static string NormalizeSetupToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = value.Trim();
        if (text.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("export ".Length).Trim();
        }
        else if (text.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("set ".Length).Trim();
        }

        if (text.StartsWith("$env:CLAUDE_CODE_OAUTH_TOKEN=", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("$env:CLAUDE_CODE_OAUTH_TOKEN=".Length).Trim();
        }
        else if (text.StartsWith("CLAUDE_CODE_OAUTH_TOKEN=", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("CLAUDE_CODE_OAUTH_TOKEN=".Length).Trim();
        }

        int lineBreak = text.IndexOfAny(new char[] { '\r', '\n' });
        if (lineBreak >= 0)
        {
            text = text.Substring(0, lineBreak).Trim();
        }

        text = text.Trim('"', '\'');
        return text;
    }

    private static string GetEnvironmentVariableAnyTarget(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    private static string TryGetNestedString(
        Dictionary<string, object> root,
        string objectKey,
        string valueKey)
    {
        object nestedObject;
        Dictionary<string, object> nested;
        object value;
        if (root != null &&
            root.TryGetValue(objectKey, out nestedObject) &&
            (nested = nestedObject as Dictionary<string, object>) != null &&
            nested.TryGetValue(valueKey, out value))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool TryReadNumber(object value, out double number)
    {
        number = 0.0;
        if (value == null)
        {
            return false;
        }

        if (value is int)
        {
            number = (int)value;
            return true;
        }

        if (value is long)
        {
            number = (long)value;
            return true;
        }

        if (value is double)
        {
            number = (double)value;
            return true;
        }

        if (value is decimal)
        {
            number = (double)(decimal)value;
            return true;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static bool TryGetDate(Dictionary<string, object> root, string key, out DateTime local)
    {
        local = DateTime.MinValue;
        object value;
        if (root == null || !root.TryGetValue(key, out value))
        {
            return false;
        }

        string text = Convert.ToString(value, CultureInfo.InvariantCulture);
        DateTime parsed;
        if (string.IsNullOrWhiteSpace(text) ||
            !DateTime.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed))
        {
            return false;
        }

        local = parsed.ToLocalTime();
        return true;
    }

    private static string ReadResponseText(WebResponse response)
    {
        using (Stream stream = response.GetResponseStream())
        {
            if (stream == null)
            {
                return string.Empty;
            }

            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }
    }

    private static bool IsNetworkAvailable()
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

    private static string GetHttpErrorReason(int statusCode)
    {
        if (statusCode == 401)
        {
            return "鉴权失败";
        }

        if (statusCode == 403)
        {
            return "权限不足";
        }

        if (statusCode == 429)
        {
            return "限流";
        }

        if (statusCode >= 500)
        {
            return "服务异常";
        }

        return "HTTP " + statusCode.ToString(CultureInfo.InvariantCulture);
    }
}
