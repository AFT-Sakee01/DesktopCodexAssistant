using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal enum ClaudeCodeUsageServiceState
{
    Unknown,
    Normal,
    Offline,
    Incomplete,
    Unavailable,
    Unreachable
}

internal sealed class ClaudeCodeUsageSnapshot
{
    public int FiveHourPercent { get; set; }
    public bool FiveHourPercentKnown { get; set; }
    public int WeeklyPercent { get; set; }
    public bool WeeklyPercentKnown { get; set; }
    public DateTime FiveHourResetLocal { get; set; }
    public bool FiveHourResetKnown { get; set; }
    public DateTime WeeklyResetLocal { get; set; }
    public bool WeeklyResetKnown { get; set; }
    public DateTime SourceUpdatedUtc { get; set; }
    public bool SourceUpdatedKnown { get; set; }

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
    public ClaudeCodeUsageServiceState State { get; set; }
    public string ErrorCode { get; set; }
    public string ErrorMessage { get; set; }
}

internal static class ClaudeCodeUsageReader
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    public const string MessagesUrl = "https://api.anthropic.com/v1/messages";
    public const string SetupTokenFileName = "claude-code-oauth-token.bin";
    public const string StatusLineQuotaFileName = "claude-statusline-quota.ini";
    public const string QuotaCacheFileName = "claude-quota.ini";
    public const int NormalRefreshSeconds = 300;
    public const int ErrorRefreshSeconds = 600;
    public const int RateLimitRefreshSeconds = 900;

    private const int RequestTimeoutMs = 10000;
    private const int QuotaCacheMaxAgeMinutes = 360;
    private const string LegacySetupTokenFileName = "claude-code-oauth-token.txt";
    private const string StatusLineBridgeScriptName = "desktop-codex-statusline-bridge.ps1";
    private const string StatusLineBridgeMarker = "# Desktop Codex Assistant Claude statusline bridge v3";
    private const int StatusLineSettingsMergeAttempts = 2;
    private static readonly object StatusLineBridgeInstallLock = new object();
    private static readonly object QuotaCacheLock = new object();
    private static bool statusLineBridgeInstallAttempted;
    private static bool statusLineBridgeInstallReady;
    private static string statusLineBridgeInstallErrorCode = string.Empty;
    private static string statusLineBridgeInstallErrorMessage = string.Empty;

    private sealed class StatusLineFileIdentity
    {
        public bool Exists { get; set; }
        public long Length { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public string Sha256 { get; set; }
    }

    private sealed class StatusLineInstallTestHooks
    {
        public Action<int, string> BeforeSettingsIdentityCheck { get; set; }
        public Action<string> BeforeAtomicCommit { get; set; }
    }
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
            oauthResult = RequireCompleteQuotaSnapshot(
                readOAuth == null ? null : readOAuth(),
                DateTime.UtcNow);
            if (oauthResult != null && (oauthResult.Success || IsTokenInvalidResult(oauthResult)))
            {
                return oauthResult;
            }
        }

        ClaudeCodeUsageReadResult statusLineResult = RequireCompleteQuotaSnapshot(
            readStatusLine == null ? null : readStatusLine(),
            DateTime.UtcNow);
        if (statusLineResult != null && statusLineResult.Success)
        {
            return statusLineResult;
        }

        bool bridgeReady = ensureBridge != null && ensureBridge();
        if (bridgeReady)
        {
            statusLineResult = RequireCompleteQuotaSnapshot(
                readStatusLine == null ? null : readStatusLine(),
                DateTime.UtcNow);
            if (statusLineResult != null && statusLineResult.Success)
            {
                return statusLineResult;
            }
        }

        if (tokenConfigured && oauthResult != null)
        {
            return oauthResult;
        }

        return BuildError(false, ClaudeCodeUsageServiceState.Unavailable, "NO_SETUP_TOKEN", "未绑定setup-token");
    }

    private static ClaudeCodeUsageReadResult ReadStatusLineQuotaCacheResult()
    {
        ClaudeCodeUsageSnapshot snapshot;
        string errorCode;
        string errorMessage;
        ClaudeCodeUsageServiceState errorState;
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
            return BuildError(false, ClaudeCodeUsageServiceState.Offline, "OFFLINE", "无网络");
        }

        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, UsageUrl, out aiBlockReason))
        {
            return BuildError(false, ClaudeCodeUsageServiceState.Unavailable, "AI_BLOCK", "AI阻断");
        }

        string token = ReadConfiguredSetupToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return BuildError(false, ClaudeCodeUsageServiceState.Unavailable, "NO_SETUP_TOKEN", "无setup-token");
        }

        // Use the official OAuth usage endpoint as the authoritative source, then consult
        // Messages API rate-limit headers only as a bounded fallback. Keep that
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
        DateTime nowUtc = DateTime.UtcNow;
        ClaudeCodeUsageReadResult completeUsageResult = RequireCompleteQuotaSnapshot(usageResult, nowUtc);
        if (completeUsageResult != null && completeUsageResult.Success)
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
        ClaudeCodeUsageReadResult completeHeaderResult = RequireCompleteQuotaSnapshot(headerResult, DateTime.UtcNow);
        if (headerResult != null && headerResult.Success)
        {
            if (usageResult != null && usageResult.Success && usageResult.Snapshot != null)
            {
                MergeMissingResetTimes(usageResult.Snapshot, headerResult.Snapshot);
                completeUsageResult = RequireCompleteQuotaSnapshot(usageResult, DateTime.UtcNow);
                if (completeUsageResult != null && completeUsageResult.Success)
                {
                    return usageResult;
                }
            }

            return completeHeaderResult;
        }

        return completeUsageResult ?? completeHeaderResult ?? headerResult ??
            BuildError(true, ClaudeCodeUsageServiceState.Unreachable, "NET", "无法连接");
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
        out ClaudeCodeUsageServiceState errorState)
    {
        snapshot = null;
        errorCode = string.Empty;
        errorMessage = string.Empty;
        errorState = ClaudeCodeUsageServiceState.Incomplete;

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
            errorState = ClaudeCodeUsageServiceState.Unreachable;
            return false;
        }

        return TryParseStatusLineQuotaCache(content, out snapshot, out errorCode, out errorMessage, out errorState);
    }

    private static bool TryParseStatusLineQuotaCache(
        string content,
        out ClaudeCodeUsageSnapshot snapshot,
        out string errorCode,
        out string errorMessage,
        out ClaudeCodeUsageServiceState errorState)
    {
        snapshot = null;
        errorCode = string.Empty;
        errorMessage = string.Empty;
        errorState = ClaudeCodeUsageServiceState.Incomplete;

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

        DateTime fiveHourReset;
        bool fiveHourResetKnown = TryReadLocalDate(values, "FiveHourReset", out fiveHourReset);
        DateTime weeklyReset;
        bool weeklyResetKnown = TryReadLocalDate(values, "WeeklyReset", out weeklyReset);

        snapshot = new ClaudeCodeUsageSnapshot
        {
            FiveHourPercent = ClaudeCodeUsageSnapshot.ClampPercent(fiveHourPercent),
            FiveHourPercentKnown = true,
            WeeklyPercent = ClaudeCodeUsageSnapshot.ClampPercent(weeklyPercent),
            WeeklyPercentKnown = true,
            FiveHourResetLocal = fiveHourReset,
            FiveHourResetKnown = fiveHourResetKnown,
            WeeklyResetLocal = weeklyReset,
            WeeklyResetKnown = weeklyResetKnown,
            SourceUpdatedUtc = updatedUtc,
            SourceUpdatedKnown = updatedKnown
        };

        DateTime nowUtc = DateTime.UtcNow;
        if (!IsCompleteQuotaSnapshot(snapshot, nowUtc))
        {
            bool stale = updatedKnown && !IsQuotaCacheFresh(true, updatedUtc, nowUtc);
            snapshot = null;
            errorCode = stale ? "STATUSLINE_STALE" : "STATUSLINE_INCOMPLETE";
            errorMessage = stale ? "状态缓存过期" : "状态缓存不完整";
            return false;
        }

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
        return TryEnsureStatusLineBridgeInstalledInDirectory(
            claudeDirectory,
            null,
            out errorCode,
            out errorMessage);
    }

    private static bool TryEnsureStatusLineBridgeInstalledInDirectory(
        string claudeDirectory,
        StatusLineInstallTestHooks testHooks,
        out string errorCode,
        out string errorMessage)
    {
        errorCode = string.Empty;
        errorMessage = string.Empty;

        string scriptPath = Path.Combine(claudeDirectory, StatusLineBridgeScriptName);
        string settingsPath = Path.Combine(claudeDirectory, "settings.json");
        try
        {
            Directory.CreateDirectory(claudeDirectory);
        }
        catch
        {
            errorCode = "STATUSLINE_SCRIPT";
            errorMessage = "状态目录不可用";
            return false;
        }

        for (int attempt = 1; attempt <= StatusLineSettingsMergeAttempts; attempt++)
        {
            Dictionary<string, object> settings;
            StatusLineFileIdentity settingsIdentity;
            if (!TryReadStatusLineSettings(
                    settingsPath,
                    out settings,
                    out settingsIdentity,
                    out errorCode,
                    out errorMessage))
            {
                if (string.Equals(errorCode, "STATUSLINE_SETTINGS_CONFLICT", StringComparison.Ordinal) &&
                    attempt < StatusLineSettingsMergeAttempts)
                {
                    continue;
                }

                return false;
            }

            object statusLineObject;
            bool hasStatusLine = settings.TryGetValue("statusLine", out statusLineObject);
            Dictionary<string, object> statusLine = statusLineObject as Dictionary<string, object>;
            if (hasStatusLine && statusLineObject != null && statusLine == null)
            {
                errorCode = "STATUSLINE_CUSTOM";
                errorMessage = "已有状态行";
                return false;
            }

            string currentCommand = statusLine == null
                ? string.Empty
                : Convert.ToString(ReadObject(statusLine, "command"), CultureInfo.InvariantCulture);
            string command = BuildStatusLineBridgeCommand(scriptPath);
            if (!string.IsNullOrWhiteSpace(currentCommand) &&
                !string.Equals(currentCommand, command, StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "STATUSLINE_CUSTOM";
                errorMessage = "已有状态行";
                return false;
            }

            bool settingsAlreadyCurrent = statusLine != null &&
                string.Equals(
                    Convert.ToString(ReadObject(statusLine, "type"), CultureInfo.InvariantCulture),
                    "command",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(currentCommand, command, StringComparison.OrdinalIgnoreCase) &&
                statusLine.ContainsKey("padding");

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

            string nextSettings;
            try
            {
                JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(
                    BoundedHttpTextReader.PublicJsonMaxBytes);
                nextSettings = serializer.Serialize(settings) + Environment.NewLine;
                if (SharedEncoding.Utf8NoBom.GetByteCount(nextSettings) > BoundedHttpTextReader.PublicJsonMaxBytes)
                {
                    throw new InvalidDataException("Claude settings exceed the supported size.");
                }
            }
            catch
            {
                errorCode = "STATUSLINE_SETTINGS_WRITE";
                errorMessage = "设置序列化失败";
                return false;
            }

            try
            {
                WriteStatusLineBridgeScript(scriptPath, testHooks);
            }
            catch
            {
                errorCode = "STATUSLINE_SCRIPT";
                errorMessage = "脚本写入失败";
                return false;
            }

            if (settingsAlreadyCurrent)
            {
                return true;
            }

            string tempPath = string.Empty;
            try
            {
                // External tools may rewrite settings.json while this merge is in progress. Flush the
                // candidate first, then compare length/mtime/hash immediately before the atomic swap.
                // One conflict is re-merged; a second conflict fails closed without overwriting it.
                tempPath = WriteAtomicTempFile(settingsPath, nextSettings);
                if (testHooks != null && testHooks.BeforeSettingsIdentityCheck != null)
                {
                    testHooks.BeforeSettingsIdentityCheck(attempt, settingsPath);
                }

                if (testHooks != null && testHooks.BeforeAtomicCommit != null)
                {
                    testHooks.BeforeAtomicCommit(settingsPath);
                }

                StatusLineFileIdentity currentIdentity;
                byte[] ignoredContent;
                bool currentStable = TryReadStatusLineFileSnapshot(
                    settingsPath,
                    BoundedHttpTextReader.PublicJsonMaxBytes,
                    out currentIdentity,
                    out ignoredContent);
                if (!currentStable || !StatusLineFileIdentityEquals(settingsIdentity, currentIdentity))
                {
                    if (attempt < StatusLineSettingsMergeAttempts)
                    {
                        continue;
                    }

                    errorCode = "STATUSLINE_SETTINGS_CONFLICT";
                    errorMessage = "设置已被并发修改";
                    return false;
                }

                CommitAtomicTempFile(
                    tempPath,
                    settingsPath,
                    settingsPath + ".desktopcodex.bak",
                    settingsIdentity.Exists);
                tempPath = string.Empty;
                return true;
            }
            catch
            {
                errorCode = "STATUSLINE_SETTINGS_WRITE";
                errorMessage = "设置写入失败";
                return false;
            }
            finally
            {
                DeleteTempFileBestEffort(tempPath);
            }
        }

        errorCode = "STATUSLINE_SETTINGS_CONFLICT";
        errorMessage = "设置已被并发修改";
        return false;
    }

    private static bool TryReadStatusLineSettings(
        string settingsPath,
        out Dictionary<string, object> settings,
        out StatusLineFileIdentity identity,
        out string errorCode,
        out string errorMessage)
    {
        settings = null;
        identity = null;
        errorCode = string.Empty;
        errorMessage = string.Empty;

        byte[] contentBytes;
        try
        {
            if (!TryReadStatusLineFileSnapshot(
                    settingsPath,
                    BoundedHttpTextReader.PublicJsonMaxBytes,
                    out identity,
                    out contentBytes))
            {
                errorCode = "STATUSLINE_SETTINGS_CONFLICT";
                errorMessage = "读取设置时发生并发修改";
                return false;
            }
        }
        catch
        {
            errorCode = "STATUSLINE_SETTINGS_READ";
            errorMessage = "设置读取失败";
            return false;
        }

        if (!identity.Exists)
        {
            settings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return true;
        }

        try
        {
            string content = DecodeStatusLineText(contentBytes);
            settings = BoundedHttpTextReader
                .CreateJsonSerializer(BoundedHttpTextReader.PublicJsonMaxBytes)
                .DeserializeObject(content) as Dictionary<string, object>;
            if (settings == null)
            {
                throw new InvalidDataException("Claude settings root must be a JSON object.");
            }

            return true;
        }
        catch
        {
            settings = null;
            errorCode = "STATUSLINE_SETTINGS_PARSE";
            errorMessage = "设置解析失败";
            return false;
        }
    }

    private static bool TryReadStatusLineFileSnapshot(
        string path,
        int maxBytes,
        out StatusLineFileIdentity identity,
        out byte[] content)
    {
        identity = null;
        content = new byte[0];

        FileInfo before = new FileInfo(path);
        before.Refresh();
        if (!before.Exists)
        {
            identity = new StatusLineFileIdentity
            {
                Exists = false,
                Length = 0,
                LastWriteTimeUtc = DateTime.MinValue,
                Sha256 = string.Empty
            };
            return true;
        }

        long lengthBefore = before.Length;
        DateTime writeTimeBefore = before.LastWriteTimeUtc;
        if (lengthBefore < 0 || lengthBefore > maxBytes)
        {
            throw new InvalidDataException("Claude settings exceed the supported size.");
        }

        using (FileStream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        using (MemoryStream buffer = new MemoryStream((int)Math.Min(lengthBefore, 65536L)))
        {
            byte[] chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new InvalidDataException("Claude settings exceed the supported size.");
                }

                buffer.Write(chunk, 0, read);
            }

            content = buffer.ToArray();
        }

        FileInfo after = new FileInfo(path);
        after.Refresh();
        if (!after.Exists ||
            after.Length != lengthBefore ||
            after.LastWriteTimeUtc != writeTimeBefore ||
            content.LongLength != lengthBefore)
        {
            identity = null;
            content = new byte[0];
            return false;
        }

        identity = new StatusLineFileIdentity
        {
            Exists = true,
            Length = lengthBefore,
            LastWriteTimeUtc = writeTimeBefore,
            Sha256 = ComputeSha256Hex(content)
        };
        return true;
    }

    private static bool StatusLineFileIdentityEquals(StatusLineFileIdentity left, StatusLineFileIdentity right)
    {
        return left != null &&
            right != null &&
            left.Exists == right.Exists &&
            left.Length == right.Length &&
            left.LastWriteTimeUtc == right.LastWriteTimeUtc &&
            string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);
    }

    private static string ComputeSha256Hex(byte[] content)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            return BitConverter.ToString(sha256.ComputeHash(content ?? new byte[0])).Replace("-", string.Empty);
        }
    }

    private static string DecodeStatusLineText(byte[] content)
    {
        using (MemoryStream stream = new MemoryStream(content ?? new byte[0], false))
        using (StreamReader reader = new StreamReader(
            stream,
            new UTF8Encoding(false, true),
            true))
        {
            return reader.ReadToEnd();
        }
    }

    private static object ReadObject(Dictionary<string, object> values, string key)
    {
        object value;
        return values != null && values.TryGetValue(key, out value) ? value : null;
    }

    private static string BuildStatusLineBridgeCommand(string scriptPath)
    {
        return "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath.Replace("\"", "\\\"") + "\"";
    }

    private static void WriteStatusLineBridgeScript(string path, StatusLineInstallTestHooks testHooks)
    {
        string content = BuildStatusLineBridgeScript();
        bool destinationExists = File.Exists(path);
        if (destinationExists)
        {
            string existing = File.ReadAllText(path, Encoding.UTF8);
            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                return;
            }
        }

        string tempPath = string.Empty;
        try
        {
            tempPath = WriteAtomicTempFile(path, content);
            if (testHooks != null && testHooks.BeforeAtomicCommit != null)
            {
                testHooks.BeforeAtomicCommit(path);
            }

            CommitAtomicTempFile(tempPath, path, null, destinationExists);
            tempPath = string.Empty;
        }
        finally
        {
            DeleteTempFileBestEffort(tempPath);
        }
    }

    private static string WriteAtomicTempFile(string destinationPath, string content)
    {
        string directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Atomic destination requires a directory.");
        }

        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(
            directory,
            Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            byte[] bytes = SharedEncoding.Utf8NoBom.GetBytes(content ?? string.Empty);
            using (FileStream stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }

            return tempPath;
        }
        catch
        {
            DeleteTempFileBestEffort(tempPath);
            throw;
        }
    }

    private static void CommitAtomicTempFile(
        string tempPath,
        string destinationPath,
        string backupPath,
        bool destinationExpectedToExist)
    {
        if (destinationExpectedToExist)
        {
            if (!File.Exists(destinationPath))
            {
                throw new IOException("Atomic destination disappeared before commit.");
            }

            if ((File.GetAttributes(destinationPath) & FileAttributes.ReadOnly) != 0)
            {
                throw new UnauthorizedAccessException("Atomic destination is read-only.");
            }

            string replacementBackup = !string.IsNullOrWhiteSpace(backupPath) && !File.Exists(backupPath)
                ? backupPath
                : null;
            // File.Replace keeps the destination file identity/security metadata and therefore its DACL.
            // Supplying the one-time backup only when absent preserves the existing backup contract.
            File.Replace(tempPath, destinationPath, replacementBackup);
            return;
        }

        // Same-directory Move is atomic. It also fails closed if another process creates the file
        // after the missing-file identity was captured.
        File.Move(tempPath, destinationPath);
    }

    private static void DeleteTempFileBestEffort(string tempPath)
    {
        if (string.IsNullOrWhiteSpace(tempPath))
        {
            return;
        }

        try
        {
            if (File.Exists(tempPath))
            {
                File.SetAttributes(tempPath, FileAttributes.Normal);
                File.Delete(tempPath);
            }
        }
        catch
        {
        }
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
            "$tmp = $path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'" + nl +
            "$lines = New-Object System.Collections.Generic.List[string]" + nl +
            "$lines.Add('Version=1')" + nl +
            "$lines.Add('Source=claude_statusline')" + nl +
            "$lines.Add('FiveHourPercent=' + $fiveRemaining.ToString([System.Globalization.CultureInfo]::InvariantCulture))" + nl +
            "$lines.Add('WeeklyPercent=' + $sevenRemaining.ToString([System.Globalization.CultureInfo]::InvariantCulture))" + nl +
            "if ($fiveReset) { $lines.Add('FiveHourReset=' + $fiveReset) }" + nl +
            "if ($sevenReset) { $lines.Add('WeeklyReset=' + $sevenReset) }" + nl +
            "$lines.Add('SourceUpdatedUtc=' + [DateTime]::UtcNow.ToString('o'))" + nl +
            "$utf8 = New-Object System.Text.UTF8Encoding($false)" + nl +
            "$bytes = $utf8.GetBytes(($lines -join [Environment]::NewLine) + [Environment]::NewLine)" + nl +
            "try {" + nl +
            "  $stream = [System.IO.File]::Open($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)" + nl +
            "  try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }" + nl +
            "  if ([System.IO.File]::Exists($path)) { [System.IO.File]::Replace($tmp, $path, $null) } else { [System.IO.File]::Move($tmp, $path) }" + nl +
            "} catch { exit 0 } finally { if ([System.IO.File]::Exists($tmp)) { [System.IO.File]::Delete($tmp) } }" + nl +
            "Write-Output ('5h {0}% 7d {1}%' -f $fiveRemaining, $sevenRemaining)" + nl;
    }

    public static string StatusLineQuotaCachePath
    {
        get { return Path.Combine(Logger.DirectoryPath, StatusLineQuotaFileName); }
    }

    public static string QuotaCachePath
    {
        get { return Path.Combine(Logger.DirectoryPath, QuotaCacheFileName); }
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

        BoundedHttpTextResult response = BoundedHttpTextReader.Execute(
            request,
            BoundedHttpTextReader.AuthenticatedJsonMaxBytes,
            RequestTimeoutMs,
            CancellationToken.None);
        if (response.StatusCode <= 0)
        {
            return BuildError(true, ClaudeCodeUsageServiceState.Unreachable, response.ErrorCode, "无法连接");
        }

        ClaudeCodeUsageReadResult parsed = ParseResponse(response.Content, true, response.StatusCode);
        if (parsed != null && (parsed.Success || parsed.RateLimited))
        {
            return parsed;
        }

        if (!string.IsNullOrEmpty(response.ErrorCode) &&
            response.StatusCode >= 200 && response.StatusCode < 300)
        {
            return BuildError(true, ClaudeCodeUsageServiceState.Unreachable, response.ErrorCode, "响应不可用");
        }

        return BuildError(
            true,
            response.StatusCode == 401 || response.StatusCode == 403
                ? ClaudeCodeUsageServiceState.Unavailable
                : ClaudeCodeUsageServiceState.Unreachable,
            response.StatusCode == 401 || response.StatusCode == 403
                ? "TOKEN_INVALID"
                : response.StatusCode.ToString(CultureInfo.InvariantCulture),
            GetHttpErrorReason(response.StatusCode));
    }

    private static ClaudeCodeUsageReadResult FetchUsageViaMessagesHeaders(WidgetSettings settings, string token)
    {
        string aiBlockReason;
        if (AiRequestProtection.ShouldBlock(settings, MessagesUrl, out aiBlockReason))
        {
            return BuildError(true, ClaudeCodeUsageServiceState.Unavailable, "AI_BLOCK", "AI阻断");
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

        return BuildError(true, ClaudeCodeUsageServiceState.Unreachable, "NO_HEADERS", "无统一限额头");
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
                            ? ClaudeCodeUsageServiceState.Unavailable
                            : ClaudeCodeUsageServiceState.Unreachable,
                        statusCode.ToString(CultureInfo.InvariantCulture),
                        GetHttpErrorReason(statusCode));
                }
            }

            return BuildError(true, ClaudeCodeUsageServiceState.Unreachable, "NET", "无法连接");
        }
    }

    public static ClaudeCodeUsageReadResult ParseResponse(
        string content,
        bool tokenConfigured,
        int statusCode)
    {
        if (statusCode == 429)
        {
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Unavailable, "429", "限流");
        }

        if (statusCode < 200 || statusCode >= 300)
        {
            return BuildError(
                tokenConfigured,
                statusCode == 401 || statusCode == 403
                    ? ClaudeCodeUsageServiceState.Unavailable
                    : ClaudeCodeUsageServiceState.Unreachable,
                statusCode == 401 || statusCode == 403
                    ? "TOKEN_INVALID"
                    : statusCode.ToString(CultureInfo.InvariantCulture),
                GetHttpErrorReason(statusCode));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Incomplete, "EMPTY", "空响应");
        }

        Dictionary<string, object> root;
        try
        {
            root = BoundedHttpTextReader
                .CreateJsonSerializer(BoundedHttpTextReader.AuthenticatedJsonMaxBytes)
                .DeserializeObject(content) as Dictionary<string, object>;
        }
        catch
        {
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Incomplete, "PARSE", "解析失败");
        }

        if (root == null)
        {
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Incomplete, "PARSE", "解析失败");
        }

        string errorType = TryGetNestedString(root, "error", "type");
        if (string.Equals(errorType, "rate_limit_error", StringComparison.OrdinalIgnoreCase))
        {
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Unavailable, "429", "限流");
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
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Incomplete, "NO_USAGE", "数据不完整");
        }

        snapshot.FiveHourPercentKnown = true;
        snapshot.WeeklyPercentKnown = true;
        snapshot.SourceUpdatedUtc = DateTime.UtcNow;
        snapshot.SourceUpdatedKnown = true;
        return new ClaudeCodeUsageReadResult
        {
            TokenConfigured = tokenConfigured,
            Success = true,
            RateLimited = false,
            Snapshot = snapshot,
            State = ClaudeCodeUsageServiceState.Normal,
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
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Unavailable, "429", "限流");
        }

        if (headers == null)
        {
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Incomplete, "NO_HEADERS", "无统一限额头");
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
            return BuildError(tokenConfigured, ClaudeCodeUsageServiceState.Incomplete, "NO_HEADERS", "无统一限额头");
        }

        snapshot.FiveHourPercentKnown = true;
        snapshot.WeeklyPercentKnown = true;
        snapshot.SourceUpdatedUtc = DateTime.UtcNow;
        snapshot.SourceUpdatedKnown = true;
        return new ClaudeCodeUsageReadResult
        {
            TokenConfigured = tokenConfigured,
            Success = true,
            RateLimited = false,
            Snapshot = snapshot,
            State = ClaudeCodeUsageServiceState.Normal,
            ErrorCode = string.Empty,
            ErrorMessage = string.Empty
        };
    }

    public static void TryWriteQuotaCache(ClaudeCodeUsageSnapshot snapshot)
    {
        TryWriteQuotaCache(snapshot, QuotaCachePath);
    }

    private static bool TryWriteQuotaCache(ClaudeCodeUsageSnapshot snapshot, string path)
    {
        return TryWriteQuotaCache(snapshot, path, DateTime.UtcNow);
    }

    private static bool TryWriteQuotaCache(ClaudeCodeUsageSnapshot snapshot, string path, DateTime nowUtc)
    {
        if (!IsCompleteQuotaSnapshot(snapshot, nowUtc) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        List<string> lines = new List<string>();
        lines.Add("Version=1");
        lines.Add("FiveHourPercent=" + ClaudeCodeUsageSnapshot.ClampPercent(snapshot.FiveHourPercent).ToString(CultureInfo.InvariantCulture));
        lines.Add("WeeklyPercent=" + ClaudeCodeUsageSnapshot.ClampPercent(snapshot.WeeklyPercent).ToString(CultureInfo.InvariantCulture));
        if (snapshot.FiveHourResetKnown)
        {
            lines.Add("FiveHourReset=" + snapshot.FiveHourResetLocal.ToString("o", CultureInfo.InvariantCulture));
        }

        if (snapshot.WeeklyResetKnown)
        {
            lines.Add("WeeklyReset=" + snapshot.WeeklyResetLocal.ToString("o", CultureInfo.InvariantCulture));
        }

        if (snapshot.SourceUpdatedKnown)
        {
            lines.Add("SourceUpdatedUtc=" + snapshot.SourceUpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
        }

        string tempPath = string.Empty;
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string next = string.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine;
            // The scheduler and form callbacks can converge on the same cache. Serialize writers and
            // replace a complete temp file so readers never observe a partially written quota pair.
            lock (QuotaCacheLock)
            {
                if (File.Exists(path) && string.Equals(File.ReadAllText(path), next, StringComparison.Ordinal))
                {
                    return true;
                }

                tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempPath, next, SharedEncoding.Utf8NoBom);
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, null);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(tempPath))
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }

            Program.LogException(ex);
            return false;
        }
    }

    internal static bool IsQuotaCacheFresh(bool updatedAtKnown, DateTime updatedAtUtc, DateTime nowUtc)
    {
        if (!updatedAtKnown || updatedAtUtc == DateTime.MinValue)
        {
            return false;
        }

        DateTime updated = updatedAtUtc.Kind == DateTimeKind.Utc
            ? updatedAtUtc
            : updatedAtUtc.ToUniversalTime();
        DateTime now = nowUtc.Kind == DateTimeKind.Utc
            ? nowUtc
            : nowUtc.ToUniversalTime();
        TimeSpan age = now - updated;
        // Accept two minutes of source/host clock skew, but never let a persisted official quota
        // snapshot outlive the six-hour freshness contract used by the CLD tile.
        return age >= TimeSpan.FromMinutes(-2.0) &&
            age <= TimeSpan.FromMinutes(QuotaCacheMaxAgeMinutes);
    }

    internal static bool IsCompleteQuotaSnapshot(ClaudeCodeUsageSnapshot snapshot, DateTime nowUtc)
    {
        if (snapshot == null ||
            !snapshot.FiveHourPercentKnown ||
            !snapshot.WeeklyPercentKnown ||
            !snapshot.FiveHourResetKnown ||
            snapshot.FiveHourResetLocal == DateTime.MinValue ||
            !snapshot.WeeklyResetKnown ||
            snapshot.WeeklyResetLocal == DateTime.MinValue)
        {
            return false;
        }

        return IsQuotaCacheFresh(snapshot.SourceUpdatedKnown, snapshot.SourceUpdatedUtc, nowUtc);
    }

    internal static ClaudeCodeUsageReadResult RequireCompleteQuotaSnapshot(
        ClaudeCodeUsageReadResult result,
        DateTime nowUtc)
    {
        if (result == null || !result.Success)
        {
            return result;
        }

        if (IsCompleteQuotaSnapshot(result.Snapshot, nowUtc))
        {
            return result;
        }

        // A partial sample must never replace the last complete CLD snapshot. OAuth parsing may
        // temporarily produce one so Messages headers can supply reset times, but publication,
        // persistence and restore all pass through this gate after that bounded merge attempt.
        return BuildError(
            result.TokenConfigured,
            ClaudeCodeUsageServiceState.Incomplete,
            "QUOTA_INCOMPLETE",
            "额度数据不完整");
    }

    public static void RunSelfTest()
    {
        RunUsedPercentBoundarySelfTest();

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
        if (limited == null || !limited.RateLimited || limited.State != ClaudeCodeUsageServiceState.Unavailable)
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
        RunStatusLineBridgeInstallSelfTest();

        ClaudeCodeUsageSnapshot statusLineSnapshot;
        string statusLineCode;
        string statusLineMessage;
        ClaudeCodeUsageServiceState statusLineState;
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

        string statusLineMissingResetIni =
            "Version=1\n" +
            "Source=claude_statusline\n" +
            "FiveHourPercent=61\n" +
            "WeeklyPercent=34\n" +
            "FiveHourReset=2026-07-04T10:00:00Z\n" +
            "SourceUpdatedUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n";
        if (TryParseStatusLineQuotaCache(
                statusLineMissingResetIni,
                out statusLineSnapshot,
                out statusLineCode,
                out statusLineMessage,
                out statusLineState) ||
            statusLineSnapshot != null ||
            !string.Equals(statusLineCode, "STATUSLINE_INCOMPLETE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Code statusline partial-reset rejection self-test failed.");
        }

        RunQuotaCacheSelfTest();
        RunSourceOrderSelfTest();
    }

    private static void RunStatusLineBridgeInstallSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "desktopcodex-claude-statusline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RunStatusLineBridgeMissingSettingsFixture(root);
            RunStatusLineBridgeSuccessFixture(root);
            RunStatusLineBridgeCustomFixture(root);
            RunStatusLineBridgeCorruptJsonFixture(root);
            RunStatusLineBridgeLockedFixture(root);
            RunStatusLineBridgeReadOnlyFixture(root);
            RunStatusLineBridgeAtomicFailureFixture(root);
            RunStatusLineBridgeScriptFailureFixture(root);
            RunStatusLineBridgeConcurrentMergeFixture(root);
            RunStatusLineBridgePersistentConflictFixture(root);
            AssertNoStatusLineTempFiles(root, "final fixture sweep");
        }
        finally
        {
            DeleteStatusLineFixtureRoot(root);
        }
    }

    private static void RunStatusLineBridgeSuccessFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "success");
        string settingsPath = Path.Combine(directory, "settings.json");
        string scriptPath = Path.Combine(directory, StatusLineBridgeScriptName);
        string backupPath = settingsPath + ".desktopcodex.bak";
        string original = "{\"theme\":\"dark\"}" + Environment.NewLine;
        File.WriteAllText(settingsPath, original, SharedEncoding.Utf8NoBom);

        string errorCode;
        string errorMessage;
        if (!TryEnsureStatusLineBridgeInstalledInDirectory(
                directory,
                null,
                out errorCode,
                out errorMessage))
        {
            throw new InvalidOperationException("Claude statusline success fixture failed: " + errorCode);
        }

        Dictionary<string, object> installed = ReadStatusLineFixtureSettings(settingsPath);
        Dictionary<string, object> statusLine = ReadObject(installed, "statusLine") as Dictionary<string, object>;
        if (statusLine == null ||
            !string.Equals(Convert.ToString(ReadObject(installed, "theme"), CultureInfo.InvariantCulture), "dark", StringComparison.Ordinal) ||
            !string.Equals(Convert.ToString(ReadObject(statusLine, "type"), CultureInfo.InvariantCulture), "command", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Convert.ToString(ReadObject(statusLine, "command"), CultureInfo.InvariantCulture),
                BuildStatusLineBridgeCommand(scriptPath),
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(scriptPath) ||
            !string.Equals(File.ReadAllText(scriptPath, Encoding.UTF8), BuildStatusLineBridgeScript(), StringComparison.Ordinal) ||
            !File.Exists(backupPath) ||
            !string.Equals(File.ReadAllText(backupPath, Encoding.UTF8), original, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude statusline success/backup fixture failed.");
        }

        string backupHash = ComputeSha256Hex(File.ReadAllBytes(backupPath));
        statusLine.Remove("padding");
        installed["externalAfterBackup"] = "kept";
        File.WriteAllText(
            settingsPath,
            BoundedHttpTextReader.CreateJsonSerializer(BoundedHttpTextReader.PublicJsonMaxBytes).Serialize(installed) + Environment.NewLine,
            SharedEncoding.Utf8NoBom);
        if (!TryEnsureStatusLineBridgeInstalledInDirectory(
                directory,
                null,
                out errorCode,
                out errorMessage) ||
            !string.Equals(backupHash, ComputeSha256Hex(File.ReadAllBytes(backupPath)), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude statusline one-time backup fixture failed: " + errorCode);
        }

        installed = ReadStatusLineFixtureSettings(settingsPath);
        if (!string.Equals(
                Convert.ToString(ReadObject(installed, "externalAfterBackup"), CultureInfo.InvariantCulture),
                "kept",
                StringComparison.Ordinal) ||
            !(ReadObject(ReadObject(installed, "statusLine") as Dictionary<string, object>, "padding") is int))
        {
            throw new InvalidOperationException("Claude statusline existing-backup merge fixture failed.");
        }

        string script = BuildStatusLineBridgeScript();
        if (script.IndexOf("[Guid]::NewGuid()", StringComparison.Ordinal) < 0 ||
            script.IndexOf("$stream.Flush($true)", StringComparison.Ordinal) < 0 ||
            script.IndexOf("[System.IO.File]::Replace", StringComparison.Ordinal) < 0 ||
            script.IndexOf("Remove-Item -LiteralPath $path", StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException("Claude statusline generated-script atomic fixture failed.");
        }

        AssertNoStatusLineTempFiles(directory, "success");
    }

    private static void RunStatusLineBridgeMissingSettingsFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "missing-settings");
        string settingsPath = Path.Combine(directory, "settings.json");
        string scriptPath = Path.Combine(directory, StatusLineBridgeScriptName);

        string errorCode;
        string errorMessage;
        if (!TryEnsureStatusLineBridgeInstalledInDirectory(directory, null, out errorCode, out errorMessage) ||
            !File.Exists(settingsPath) ||
            !File.Exists(scriptPath) ||
            File.Exists(settingsPath + ".desktopcodex.bak") ||
            !(ReadObject(ReadStatusLineFixtureSettings(settingsPath), "statusLine") is Dictionary<string, object>))
        {
            throw new InvalidOperationException("Claude missing settings atomic-move fixture failed: " + errorCode);
        }

        AssertStatusLineScriptComplete(directory, "missing-settings");
        AssertNoStatusLineTempFiles(directory, "missing-settings");
    }

    private static void RunStatusLineBridgeCustomFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "custom");
        string settingsPath = Path.Combine(directory, "settings.json");
        string original = "{\"statusLine\":{\"type\":\"command\",\"command\":\"custom-status desktop-codex-statusline-bridge.ps1 --flag\"}}" + Environment.NewLine;
        File.WriteAllText(settingsPath, original, SharedEncoding.Utf8NoBom);

        string errorCode;
        string errorMessage;
        if (TryEnsureStatusLineBridgeInstalledInDirectory(directory, null, out errorCode, out errorMessage) ||
            !string.Equals(errorCode, "STATUSLINE_CUSTOM", StringComparison.Ordinal) ||
            !string.Equals(File.ReadAllText(settingsPath, Encoding.UTF8), original, StringComparison.Ordinal) ||
            File.Exists(Path.Combine(directory, StatusLineBridgeScriptName)))
        {
            throw new InvalidOperationException("Claude custom statusline refusal fixture failed.");
        }

        AssertNoStatusLineTempFiles(directory, "custom");
    }

    private static void RunStatusLineBridgeCorruptJsonFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "corrupt-json");
        string settingsPath = Path.Combine(directory, "settings.json");
        string original = "{\"statusLine\":";
        File.WriteAllText(settingsPath, original, SharedEncoding.Utf8NoBom);

        string errorCode;
        string errorMessage;
        if (TryEnsureStatusLineBridgeInstalledInDirectory(directory, null, out errorCode, out errorMessage) ||
            !string.Equals(errorCode, "STATUSLINE_SETTINGS_PARSE", StringComparison.Ordinal) ||
            !string.Equals(File.ReadAllText(settingsPath, Encoding.UTF8), original, StringComparison.Ordinal) ||
            File.Exists(Path.Combine(directory, StatusLineBridgeScriptName)))
        {
            throw new InvalidOperationException("Claude corrupt settings fixture failed.");
        }

        AssertNoStatusLineTempFiles(directory, "corrupt-json");
    }

    private static void RunStatusLineBridgeLockedFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "locked");
        string settingsPath = Path.Combine(directory, "settings.json");
        string original = "{\"locked\":true}" + Environment.NewLine;
        File.WriteAllText(settingsPath, original, SharedEncoding.Utf8NoBom);

        string errorCode;
        string errorMessage;
        using (FileStream locked = new FileStream(
            settingsPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            if (TryEnsureStatusLineBridgeInstalledInDirectory(directory, null, out errorCode, out errorMessage) ||
                !string.Equals(errorCode, "STATUSLINE_SETTINGS_READ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Claude locked settings fixture failed.");
            }
        }

        if (!string.Equals(File.ReadAllText(settingsPath, Encoding.UTF8), original, StringComparison.Ordinal) ||
            File.Exists(Path.Combine(directory, StatusLineBridgeScriptName)))
        {
            throw new InvalidOperationException("Claude locked settings fixture changed a target file.");
        }

        AssertNoStatusLineTempFiles(directory, "locked");
    }

    private static void RunStatusLineBridgeReadOnlyFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "read-only");
        string settingsPath = Path.Combine(directory, "settings.json");
        string original = "{\"readOnly\":true}" + Environment.NewLine;
        File.WriteAllText(settingsPath, original, SharedEncoding.Utf8NoBom);
        File.SetAttributes(settingsPath, File.GetAttributes(settingsPath) | FileAttributes.ReadOnly);

        string errorCode;
        string errorMessage;
        try
        {
            if (TryEnsureStatusLineBridgeInstalledInDirectory(directory, null, out errorCode, out errorMessage) ||
                !string.Equals(errorCode, "STATUSLINE_SETTINGS_WRITE", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Claude read-only settings fixture failed.");
            }
        }
        finally
        {
            File.SetAttributes(settingsPath, FileAttributes.Normal);
        }

        if (!string.Equals(File.ReadAllText(settingsPath, Encoding.UTF8), original, StringComparison.Ordinal) ||
            File.Exists(settingsPath + ".desktopcodex.bak"))
        {
            throw new InvalidOperationException("Claude read-only settings fixture changed the target.");
        }

        AssertStatusLineScriptComplete(directory, "read-only");
        AssertNoStatusLineTempFiles(directory, "read-only");
    }

    private static void RunStatusLineBridgeAtomicFailureFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "atomic-settings-failure");
        string settingsPath = Path.Combine(directory, "settings.json");
        string original = "{\"atomic\":\"before\"}" + Environment.NewLine;
        File.WriteAllText(settingsPath, original, SharedEncoding.Utf8NoBom);

        StatusLineInstallTestHooks hooks = new StatusLineInstallTestHooks
        {
            BeforeAtomicCommit = delegate(string path)
            {
                if (string.Equals(Path.GetFileName(path), "settings.json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Simulated interruption before settings commit.");
                }
            }
        };
        string errorCode;
        string errorMessage;
        if (TryEnsureStatusLineBridgeInstalledInDirectory(directory, hooks, out errorCode, out errorMessage) ||
            !string.Equals(errorCode, "STATUSLINE_SETTINGS_WRITE", StringComparison.Ordinal) ||
            !string.Equals(File.ReadAllText(settingsPath, Encoding.UTF8), original, StringComparison.Ordinal) ||
            File.Exists(settingsPath + ".desktopcodex.bak"))
        {
            throw new InvalidOperationException("Claude settings atomic-failure fixture failed.");
        }

        AssertStatusLineScriptComplete(directory, "atomic-settings-failure");
        AssertNoStatusLineTempFiles(directory, "atomic-settings-failure");
    }

    private static void RunStatusLineBridgeScriptFailureFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "atomic-script-failure");
        string settingsPath = Path.Combine(directory, "settings.json");
        string scriptPath = Path.Combine(directory, StatusLineBridgeScriptName);
        string original = "{\"script\":\"before\"}" + Environment.NewLine;
        string originalScript = "# preserved script" + Environment.NewLine;
        File.WriteAllText(settingsPath, original, SharedEncoding.Utf8NoBom);
        File.WriteAllText(scriptPath, originalScript, SharedEncoding.Utf8NoBom);

        StatusLineInstallTestHooks hooks = new StatusLineInstallTestHooks
        {
            BeforeAtomicCommit = delegate(string path)
            {
                if (string.Equals(Path.GetFileName(path), StatusLineBridgeScriptName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Simulated interruption before script commit.");
                }
            }
        };
        string errorCode;
        string errorMessage;
        if (TryEnsureStatusLineBridgeInstalledInDirectory(directory, hooks, out errorCode, out errorMessage) ||
            !string.Equals(errorCode, "STATUSLINE_SCRIPT", StringComparison.Ordinal) ||
            !string.Equals(File.ReadAllText(settingsPath, Encoding.UTF8), original, StringComparison.Ordinal) ||
            !string.Equals(File.ReadAllText(scriptPath, Encoding.UTF8), originalScript, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude script atomic-failure fixture failed.");
        }

        AssertNoStatusLineTempFiles(directory, "atomic-script-failure");
    }

    private static void RunStatusLineBridgeConcurrentMergeFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "concurrent-merge");
        string settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, "{\"base\":\"initial\"}" + Environment.NewLine, SharedEncoding.Utf8NoBom);

        int conflictCount = 0;
        StatusLineInstallTestHooks hooks = new StatusLineInstallTestHooks
        {
            BeforeSettingsIdentityCheck = delegate(int attempt, string path)
            {
                if (attempt == 1)
                {
                    conflictCount++;
                    File.WriteAllText(
                        path,
                        "{\"base\":\"external\",\"external\":1}" + Environment.NewLine,
                        SharedEncoding.Utf8NoBom);
                }
            }
        };

        string errorCode;
        string errorMessage;
        if (!TryEnsureStatusLineBridgeInstalledInDirectory(directory, hooks, out errorCode, out errorMessage) ||
            conflictCount != 1)
        {
            throw new InvalidOperationException("Claude statusline conflict re-merge fixture failed: " + errorCode);
        }

        Dictionary<string, object> merged = ReadStatusLineFixtureSettings(settingsPath);
        if (!string.Equals(Convert.ToString(ReadObject(merged, "base"), CultureInfo.InvariantCulture), "external", StringComparison.Ordinal) ||
            Convert.ToInt32(ReadObject(merged, "external"), CultureInfo.InvariantCulture) != 1 ||
            !(ReadObject(merged, "statusLine") is Dictionary<string, object>))
        {
            throw new InvalidOperationException("Claude statusline conflict merge lost external settings.");
        }

        AssertNoStatusLineTempFiles(directory, "concurrent-merge");
    }

    private static void RunStatusLineBridgePersistentConflictFixture(string root)
    {
        string directory = CreateStatusLineFixtureDirectory(root, "persistent-conflict");
        string settingsPath = Path.Combine(directory, "settings.json");
        File.WriteAllText(settingsPath, "{\"base\":true}" + Environment.NewLine, SharedEncoding.Utf8NoBom);

        int conflictCount = 0;
        StatusLineInstallTestHooks hooks = new StatusLineInstallTestHooks
        {
            BeforeSettingsIdentityCheck = delegate(int attempt, string path)
            {
                conflictCount++;
                File.WriteAllText(
                    path,
                    "{\"external\":" + attempt.ToString(CultureInfo.InvariantCulture) + "}" + Environment.NewLine,
                    SharedEncoding.Utf8NoBom);
            }
        };

        string errorCode;
        string errorMessage;
        if (TryEnsureStatusLineBridgeInstalledInDirectory(directory, hooks, out errorCode, out errorMessage) ||
            !string.Equals(errorCode, "STATUSLINE_SETTINGS_CONFLICT", StringComparison.Ordinal) ||
            conflictCount != StatusLineSettingsMergeAttempts)
        {
            throw new InvalidOperationException("Claude statusline persistent-conflict cap fixture failed.");
        }

        Dictionary<string, object> current = ReadStatusLineFixtureSettings(settingsPath);
        if (Convert.ToInt32(ReadObject(current, "external"), CultureInfo.InvariantCulture) != StatusLineSettingsMergeAttempts ||
            current.ContainsKey("statusLine"))
        {
            throw new InvalidOperationException("Claude statusline persistent conflict overwrote external settings.");
        }

        AssertStatusLineScriptComplete(directory, "persistent-conflict");
        AssertNoStatusLineTempFiles(directory, "persistent-conflict");
    }

    private static string CreateStatusLineFixtureDirectory(string root, string name)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static Dictionary<string, object> ReadStatusLineFixtureSettings(string path)
    {
        Dictionary<string, object> settings = BoundedHttpTextReader
            .CreateJsonSerializer(BoundedHttpTextReader.PublicJsonMaxBytes)
            .DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
        if (settings == null)
        {
            throw new InvalidOperationException("Claude statusline fixture settings are not an object.");
        }

        return settings;
    }

    private static void AssertNoStatusLineTempFiles(string directory, string fixtureName)
    {
        if (Directory.Exists(directory) &&
            Directory.GetFiles(directory, "*.tmp", SearchOption.AllDirectories).Length != 0)
        {
            throw new InvalidOperationException("Claude statusline temp cleanup fixture failed: " + fixtureName);
        }
    }

    private static void AssertStatusLineScriptComplete(string directory, string fixtureName)
    {
        string scriptPath = Path.Combine(directory, StatusLineBridgeScriptName);
        if (!File.Exists(scriptPath) ||
            !string.Equals(File.ReadAllText(scriptPath, Encoding.UTF8), BuildStatusLineBridgeScript(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude statusline script completeness fixture failed: " + fixtureName);
        }
    }

    private static void DeleteStatusLineFixtureRoot(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                catch
                {
                }
            }

            Directory.Delete(root, true);
        }
        catch
        {
        }
    }

    private static void RunQuotaCacheSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "desktopcodex-claude-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, QuotaCacheFileName);
            DateTime nowUtc = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
            ClaudeCodeUsageSnapshot snapshot = new ClaudeCodeUsageSnapshot
            {
                FiveHourPercent = 51,
                FiveHourPercentKnown = true,
                WeeklyPercent = 62,
                WeeklyPercentKnown = true,
                FiveHourResetLocal = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Local),
                FiveHourResetKnown = true,
                WeeklyResetLocal = new DateTime(2026, 7, 28, 12, 30, 0, DateTimeKind.Local),
                WeeklyResetKnown = true,
                SourceUpdatedUtc = nowUtc,
                SourceUpdatedKnown = true
            };

            if (!TryWriteQuotaCache(snapshot, path, nowUtc) || !File.Exists(path))
            {
                throw new InvalidOperationException("Claude Code quota cache write self-test failed.");
            }

            string content = File.ReadAllText(path, Encoding.UTF8);
            if (content.IndexOf("FiveHourPercent=51", StringComparison.Ordinal) < 0 ||
                content.IndexOf("WeeklyPercent=62", StringComparison.Ordinal) < 0 ||
                content.IndexOf("FiveHourReset=", StringComparison.Ordinal) < 0 ||
                content.IndexOf("WeeklyReset=", StringComparison.Ordinal) < 0 ||
                content.IndexOf("SourceUpdatedUtc=", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Claude Code quota cache content self-test failed.");
            }

            snapshot.WeeklyPercent = 63;
            if (!TryWriteQuotaCache(snapshot, path, nowUtc) ||
                File.ReadAllText(path, Encoding.UTF8).IndexOf("WeeklyPercent=63", StringComparison.Ordinal) < 0 ||
                Directory.GetFiles(root, QuotaCacheFileName + ".*.tmp").Length != 0)
            {
                throw new InvalidOperationException("Claude Code quota cache atomic replace self-test failed.");
            }

            string lastGoodContent = File.ReadAllText(path, Encoding.UTF8);
            ClaudeCodeUsageSnapshot partial = new ClaudeCodeUsageSnapshot
            {
                FiveHourPercent = 21,
                FiveHourPercentKnown = true,
                WeeklyPercent = 32,
                WeeklyPercentKnown = true,
                FiveHourResetLocal = snapshot.FiveHourResetLocal,
                FiveHourResetKnown = true,
                WeeklyResetKnown = false,
                SourceUpdatedUtc = nowUtc,
                SourceUpdatedKnown = true
            };
            if (TryWriteQuotaCache(partial, path, nowUtc) ||
                !string.Equals(File.ReadAllText(path, Encoding.UTF8), lastGoodContent, StringComparison.Ordinal) ||
                Directory.GetFiles(root, QuotaCacheFileName + ".*.tmp").Length != 0)
            {
                throw new InvalidOperationException("Claude Code partial quota overwrote the last-good cache.");
            }

            if (!IsQuotaCacheFresh(true, nowUtc.AddHours(-6.0), nowUtc) ||
                IsQuotaCacheFresh(true, nowUtc.AddHours(-6.0).AddSeconds(-1.0), nowUtc) ||
                !IsQuotaCacheFresh(true, nowUtc.AddMinutes(2.0), nowUtc) ||
                IsQuotaCacheFresh(true, nowUtc.AddMinutes(2.0).AddSeconds(1.0), nowUtc) ||
                IsQuotaCacheFresh(false, nowUtc, nowUtc))
            {
                throw new InvalidOperationException("Claude Code quota cache freshness self-test failed.");
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

    private static void RunSourceOrderSelfTest()
    {
        ClaudeCodeUsageReadResult oauthSuccess = BuildSuccess(new ClaudeCodeUsageSnapshot
        {
            FiveHourPercent = 81,
            FiveHourPercentKnown = true,
            WeeklyPercent = 72,
            WeeklyPercentKnown = true,
            FiveHourResetLocal = DateTime.Now.AddHours(2.0),
            FiveHourResetKnown = true,
            WeeklyResetLocal = DateTime.Now.AddDays(2.0),
            WeeklyResetKnown = true,
            SourceUpdatedUtc = DateTime.UtcNow,
            SourceUpdatedKnown = true
        });
        oauthSuccess.TokenConfigured = true;
        ClaudeCodeUsageReadResult statusLineSuccess = BuildSuccess(new ClaudeCodeUsageSnapshot
        {
            FiveHourPercent = 31,
            FiveHourPercentKnown = true,
            WeeklyPercent = 22,
            WeeklyPercentKnown = true,
            FiveHourResetLocal = DateTime.Now.AddHours(3.0),
            FiveHourResetKnown = true,
            WeeklyResetLocal = DateTime.Now.AddDays(3.0),
            WeeklyResetKnown = true,
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

        ClaudeCodeUsageReadResult networkFailure = BuildError(true, ClaudeCodeUsageServiceState.Unreachable, "NET", "无法连接");
        selected = ResolveReadSources(
            "oauth-test",
            delegate { return networkFailure; },
            delegate { statusLineCalls++; return statusLineSuccess; },
            delegate { return false; });
        if (!object.ReferenceEquals(selected, statusLineSuccess))
        {
            throw new InvalidOperationException("Claude Code usage statusline fallback self-test failed.");
        }

        ClaudeCodeUsageReadResult missing = BuildError(false, ClaudeCodeUsageServiceState.Incomplete, "NO_STATUSLINE_CACHE", "等Claude刷新");
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

        ClaudeCodeUsageReadResult partialOAuthForMerge = ParseResponse(
            "{\"five_hour\":{\"used_percent\":20},\"seven_day\":{\"used_percent\":30}}",
            true,
            200);
        selected = ResolveSetupTokenResult(partialOAuthForMerge, delegate { return statusLineSuccess; });
        if (!object.ReferenceEquals(selected, partialOAuthForMerge) ||
            !selected.Success ||
            !IsCompleteQuotaSnapshot(selected.Snapshot, DateTime.UtcNow))
        {
            throw new InvalidOperationException("Claude Code usage partial OAuth reset merge self-test failed.");
        }

        ClaudeCodeUsageReadResult partialOAuthWithoutFallback = ParseResponse(
            "{\"five_hour\":{\"used_percent\":20},\"seven_day\":{\"used_percent\":30}}",
            true,
            200);
        selected = ResolveSetupTokenResult(
            partialOAuthWithoutFallback,
            delegate
            {
                return BuildError(true, ClaudeCodeUsageServiceState.Unreachable, "NET", "无法连接");
            });
        if (selected == null ||
            selected.Success ||
            selected.Snapshot != null ||
            !string.Equals(selected.ErrorCode, "QUOTA_INCOMPLETE", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Claude Code usage partial OAuth fallback-failure self-test failed.");
        }

        selected = ResolveReadSources(
            "oauth-test",
            delegate { return partialOAuthWithoutFallback; },
            delegate { return statusLineSuccess; },
            delegate { return false; });
        if (!object.ReferenceEquals(selected, statusLineSuccess))
        {
            throw new InvalidOperationException("Claude Code usage partial OAuth statusline fallback self-test failed.");
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
            if (!SecretStore.TryReadOrMigrateSecret(
                    encrypted,
                    legacy,
                    NormalizeSetupToken,
                    IsSupportedLegacySetupToken,
                    out token,
                    out migrated,
                    out errorCode) ||
                !migrated ||
                !string.Equals(token, "oauth-migrated", StringComparison.Ordinal) ||
                !File.Exists(encrypted) ||
                File.Exists(legacy) ||
                File.Exists(legacy + ".migrated"))
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
            if (double.IsNaN(usedPercent) || double.IsInfinity(usedPercent) ||
                usedPercent < 0.0 || usedPercent > 1.0)
            {
                return false;
            }

            usedPercent *= 100.0;
            return true;
        }

        if (slot != null &&
            (slot.TryGetValue("used_percent", out value) ||
             slot.TryGetValue("used_percentage", out value)) &&
            TryReadNumber(value, out usedPercent))
        {
            if (double.IsNaN(usedPercent) || double.IsInfinity(usedPercent) ||
                usedPercent < 0.0 || usedPercent > 100.0)
            {
                return false;
            }

            return true;
        }

        if (slot != null && slot.TryGetValue("percent", out value) && TryReadNumber(value, out usedPercent))
        {
            if (double.IsNaN(usedPercent) || double.IsInfinity(usedPercent) ||
                usedPercent < 0.0 || usedPercent > 100.0)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private static void RunUsedPercentBoundarySelfTest()
    {
        AssertUsedPercentBoundary("utilization", 0.01, true, 1.0);
        AssertUsedPercentBoundary("utilization", 1.0, true, 100.0);
        AssertUsedPercentBoundary("used_percent", 0.01, true, 0.01);
        AssertUsedPercentBoundary("used_percent", 1.0, true, 1.0);
        AssertUsedPercentBoundary("used_percentage", 100.0, true, 100.0);
        AssertUsedPercentBoundary("percent", 1.0, true, 1.0);
        AssertUsedPercentBoundary("utilization", 1.01, false, 0.0);
        AssertUsedPercentBoundary("used_percent", 100.01, false, 0.0);
        AssertUsedPercentBoundary("percent", -0.01, false, 0.0);
    }

    private static void AssertUsedPercentBoundary(
        string fieldName,
        double input,
        bool expectedSuccess,
        double expectedPercent)
    {
        Dictionary<string, object> slot = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        slot[fieldName] = input;

        double actualPercent;
        bool actualSuccess = TryGetUsedPercent(slot, out actualPercent);
        if (actualSuccess != expectedSuccess ||
            (expectedSuccess && Math.Abs(actualPercent - expectedPercent) > 0.000001))
        {
            throw new InvalidOperationException(
                "Claude Code usage percent-boundary self-test failed for " + fieldName + ".");
        }
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
        ClaudeCodeUsageServiceState state,
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
            State = snapshot == null ? ClaudeCodeUsageServiceState.Incomplete : ClaudeCodeUsageServiceState.Normal,
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

        return ReadConfiguredSetupTokenFiles(SetupTokenFilePath, LegacySetupTokenFilePath);
    }

    internal static string ReadConfiguredSetupTokenFiles(string encryptedPath, string legacyTextPath)
    {
        string secret;
        bool migrated;
        string errorCode;
        return SecretStore.TryReadOrMigrateSecret(
            encryptedPath,
            legacyTextPath,
            NormalizeSetupToken,
            IsSupportedLegacySetupToken,
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

    internal static string LegacySetupTokenFilePath
    {
        get { return Path.Combine(Logger.DirectoryPath, LegacySetupTokenFileName); }
    }

    internal static string NormalizeSetupToken(string value)
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

    private static bool IsSupportedLegacySetupToken(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 8 || value.Length > 8192)
        {
            return false;
        }

        if (!value.StartsWith("oauth-", StringComparison.Ordinal) &&
            !value.StartsWith("sk-ant-oat01-", StringComparison.Ordinal) &&
            !value.StartsWith("sk-ant-", StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool allowed =
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '-' || c == '_' || c == '.';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
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
