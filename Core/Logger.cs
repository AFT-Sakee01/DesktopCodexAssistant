using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class Logger
{
    private const long MaxLogDirectoryBytes = 10L * 1024L * 1024L;
    private const long MaxActiveLogBytes = 3L * 1024L * 1024L;
    private const int MaxBufferedInfoBytes = 64 * 1024;
    private const int InfoFlushIntervalMs = 5 * 60 * 1000;
    private const int AppendRotateMutexWaitMs = 200;
    private const string RedactedValue = "[REDACTED]";
    private const string AppendRotateMutexName = @"Local\" + ProductIdentity.MachineName + ".Logger.AppendRotate.v1";
    private static readonly TimeSpan DirectoryLimitCheckInterval = TimeSpan.FromMinutes(10);
    private static readonly object SyncRoot = new object();
    private static readonly StringBuilder InfoBuffer = new StringBuilder();
    private static readonly System.Threading.Timer FlushTimer;
    private static readonly AppendCoordinationState ProductionAppendCoordination =
        new AppendCoordinationState(
            AppendRotateMutexName,
            MaxActiveLogBytes,
            AppendRotateMutexWaitMs,
            true);
    private static DateTime lastDirectoryLimitCheckUtc = DateTime.MinValue;
    private static int infoBufferBytes;
    private static bool shuttingDown;
    // Test-only path redirection is changed together with the buffered state under SyncRoot.
    private static volatile string storageDirectoryOverride;
    private static readonly Regex SensitiveHeaderPattern = new Regex(
        @"(?<prefix>\b(?:authorization|proxy[_-]?authorization|cookie|set[_-]?cookie)\s*[:=]\s*)(?<value>.*?)(?=(?:[\s,;&]+(?:error[_-]?code|correlation[_-]?id)\s*[:=])|[\r\n]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex NamedValuePattern = new Regex(
        @"(?<![A-Za-z0-9_-])(?<prefix>(?<key>[A-Za-z][A-Za-z0-9_-]{0,63})\s*[:=]\s*)(?<value>""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*'|[^\s,;&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new Regex(
        @"(?<prefix>\bBearer\s+)(?<value>[A-Za-z0-9._~+/=-]{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex JwtPattern = new Regex(
        @"(?<![A-Za-z0-9_-])(?<value>eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,})(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant);
    private static readonly Regex StandaloneTokenPattern = new Regex(
        @"(?<![A-Za-z0-9_-])(?<value>(?:sk-ant-(?:oat01-)?[A-Za-z0-9_-]{16,}|oauth-[A-Za-z0-9._~-]{16,}|sk-(?:proj-)?[A-Za-z0-9_-]{20,}))(?![A-Za-z0-9_-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static Logger()
    {
        // INFO lines are buffered to avoid waking storage for every sample. Errors still flush immediately.
        FlushTimer = new System.Threading.Timer(
            delegate { Flush(); },
            null,
            InfoFlushIntervalMs,
            InfoFlushIntervalMs);
        AppDomain.CurrentDomain.ProcessExit += delegate { Shutdown(); };
    }

    public static string DirectoryPath
    {
        get
        {
            string overridePath = storageDirectoryOverride;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return overridePath;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.MachineName);
        }
    }

    public static string LogPath
    {
        get { return Path.Combine(DirectoryPath, ProductIdentity.LogFileName); }
    }

    public static string ErrorLogPath
    {
        get { return Path.Combine(DirectoryPath, "error.log"); }
    }

    public static string GfwProbeLogPath
    {
        get { return Path.Combine(DirectoryPath, "gfw-probe.log"); }
    }

    public static void Info(string message)
    {
        string line = FormatLine("INFO", message);
        lock (SyncRoot)
        {
            if (shuttingDown)
            {
                AppendImmediate(LogPath, line);
                return;
            }

            InfoBuffer.Append(line);
            infoBufferBytes += Encoding.UTF8.GetByteCount(line);
            if (infoBufferBytes >= MaxBufferedInfoBytes)
            {
                FlushInfoBufferLocked();
            }
        }
    }

    public static void Error(Exception ex)
    {
        string text = ex.ToString();
        string line = FormatLine("ERROR", text);
        lock (SyncRoot)
        {
            // Preserve preceding context before the error itself in case the process terminates.
            FlushInfoBufferLocked();
            AppendImmediate(LogPath, line);
            AppendImmediate(ErrorLogPath, line);
        }
    }

    public static void GfwProbe(string trigger, IEnumerable<string> lines)
    {
        ProbeDetail("GFW检测", trigger, lines);
    }

    public static void CloudEndpointProbe(string trigger, IEnumerable<string> lines)
    {
        ProbeDetail("云服务检测", trigger, lines);
    }

    private static void ProbeDetail(string label, string trigger, IEnumerable<string> lines)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(DirectoryPath);
                StringBuilder builder = new StringBuilder();
                builder.AppendLine();
                builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture));
                builder.Append(" ");
                builder.Append(string.IsNullOrWhiteSpace(label) ? "网络检测" : label.Trim());
                builder.Append(" 触发=");
                builder.AppendLine(string.IsNullOrWhiteSpace(trigger) ? "未知" : trigger.Trim());

                if (lines != null)
                {
                    foreach (string line in lines)
                    {
                        builder.AppendLine(line ?? string.Empty);
                    }
                }

                string text = builder.ToString();
                AppendImmediate(GfwProbeLogPath, text);
            }
        }
        catch
        {
        }
    }

    public static void Flush()
    {
        try
        {
            lock (SyncRoot)
            {
                FlushInfoBufferLocked();
            }
        }
        catch
        {
        }
    }

    public static void Shutdown()
    {
        lock (SyncRoot)
        {
            if (shuttingDown)
            {
                return;
            }

            shuttingDown = true;
            FlushTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            FlushInfoBufferLocked();
            FlushTimer.Dispose();
        }
    }

    internal static void RunStoragePolicySelfTest()
    {
        // Guard the forensic timestamp contract: local time with a real offset, never a fake "Z".
        string formattedLine = FormatLine("INFO", "utc-offset-selftest");
        string timestampText = formattedLine.Substring(0, formattedLine.IndexOf(" [", StringComparison.Ordinal));
        DateTimeOffset parsedTimestamp;
        if (timestampText.EndsWith("Z", StringComparison.OrdinalIgnoreCase) ||
            !DateTimeOffset.TryParseExact(
                timestampText,
                "yyyy-MM-dd HH:mm:sszzz",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsedTimestamp) ||
            parsedTimestamp.Offset != DateTimeOffset.Now.Offset)
        {
            throw new InvalidOperationException("Logger timestamp self-test failed: expected local time with explicit UTC offset, got '" + timestampText + "'.");
        }

        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            ProductIdentity.MachineName + "-LoggerTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        try
        {
            string activePath = Path.Combine(testDirectory, "test.log");
            File.WriteAllBytes(activePath, new byte[MaxActiveLogBytes]);
            RotateActiveLogIfNeeded(activePath, 1);
            string[] archives = Directory.GetFiles(testDirectory, "test-*.log");
            if (File.Exists(activePath) || archives.Length != 1)
            {
                throw new InvalidOperationException("Logger rotation self-test failed.");
            }

            File.WriteAllBytes(activePath, new byte[128]);
            string oldArchive = Path.Combine(testDirectory, "old.log");
            string newArchive = Path.Combine(testDirectory, "new.log");
            File.WriteAllBytes(oldArchive, new byte[700]);
            File.SetLastWriteTimeUtc(oldArchive, DateTime.UtcNow.AddMinutes(-2));
            File.WriteAllBytes(newArchive, new byte[700]);
            File.SetLastWriteTimeUtc(newArchive, DateTime.UtcNow.AddMinutes(-1));
            EnforceLogDirectoryLimit(
                testDirectory,
                new string[] { activePath },
                900);
            if (!File.Exists(activePath) || File.Exists(oldArchive))
            {
                throw new InvalidOperationException("Logger directory-limit self-test failed.");
            }

            RunAppendCoordinationSelfTest(Path.Combine(testDirectory, "append-coordination"));
            RunAppendMutexTimeoutSelfTest(Path.Combine(testDirectory, "append-timeout"));
            RunRedactionSelfTest(Path.Combine(testDirectory, "redaction"));
            RunWriteFailureSelfTest(Path.Combine(testDirectory, "write-failure"));
        }
        finally
        {
            // The path is generated beneath the system temporary directory and never aliases user logs.
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, true);
            }
        }
    }

    private static void RunAppendCoordinationSelfTest(string testDirectory)
    {
        Directory.CreateDirectory(testDirectory);
        string mutexName = @"Local\" + ProductIdentity.MachineName + ".Logger.SelfTest." + Guid.NewGuid().ToString("N");
        AssertIndependentNamedMutexHandles(mutexName);

        const int writerCount = 2;
        const int markersPerWriter = 24;
        string activePath = Path.Combine(testDirectory, "concurrent.log");
        string seedMarker = "LOGGER_CONCURRENT_SEED_" + Guid.NewGuid().ToString("N");
        File.WriteAllText(activePath, seedMarker + Environment.NewLine, SharedEncoding.Utf8NoBom);

        AppendCoordinationState state = new AppendCoordinationState(mutexName, 512, AppendRotateMutexWaitMs, false);
        ManualResetEvent start = new ManualResetEvent(false);
        ManualResetEvent[] ready = new ManualResetEvent[writerCount];
        Thread[] writers = new Thread[writerCount];
        Exception[] failures = new Exception[writerCount];
        string[,] markers = new string[writerCount, markersPerWriter];

        try
        {
            for (int writerIndex = 0; writerIndex < writerCount; writerIndex++)
            {
                int capturedWriterIndex = writerIndex;
                ready[writerIndex] = new ManualResetEvent(false);
                writers[writerIndex] = new Thread(delegate()
                {
                    try
                    {
                        ready[capturedWriterIndex].Set();
                        if (!start.WaitOne(2000))
                        {
                            throw new TimeoutException("Logger concurrent writer start gate timed out.");
                        }

                        for (int markerIndex = 0; markerIndex < markersPerWriter; markerIndex++)
                        {
                            string marker = "LOGGER_CONCURRENT_W" +
                                capturedWriterIndex.ToString(CultureInfo.InvariantCulture) + "_M" +
                                markerIndex.ToString(CultureInfo.InvariantCulture) + "_" +
                                Guid.NewGuid().ToString("N");
                            markers[capturedWriterIndex, markerIndex] = marker;
                            AppendImmediate(activePath, marker + Environment.NewLine, state);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures[capturedWriterIndex] = ex;
                    }
                });
                writers[writerIndex].IsBackground = true;
                writers[writerIndex].Start();
            }

            bool allReady = true;
            for (int writerIndex = 0; writerIndex < writerCount; writerIndex++)
            {
                if (!ready[writerIndex].WaitOne(2000))
                {
                    allReady = false;
                    break;
                }
            }

            if (!allReady)
            {
                throw new TimeoutException("Logger concurrent writer readiness timed out.");
            }

            start.Set();

            for (int writerIndex = 0; writerIndex < writerCount; writerIndex++)
            {
                if (!writers[writerIndex].Join(5000))
                {
                    throw new TimeoutException("Logger concurrent writer completion timed out.");
                }

                if (failures[writerIndex] != null)
                {
                    throw new InvalidOperationException("Logger concurrent writer failed.", failures[writerIndex]);
                }
            }

            string combined = ReadAllMatchingLogs(testDirectory, "concurrent*.log");
            AssertMarkerOccursOnce(combined, seedMarker, "seed");
            for (int writerIndex = 0; writerIndex < writerCount; writerIndex++)
            {
                for (int markerIndex = 0; markerIndex < markersPerWriter; markerIndex++)
                {
                    AssertMarkerOccursOnce(
                        combined,
                        markers[writerIndex, markerIndex],
                        "writer " + writerIndex.ToString(CultureInfo.InvariantCulture) +
                            " marker " + markerIndex.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (Directory.GetFiles(testDirectory, "concurrent-*.log").Length == 0)
            {
                throw new InvalidOperationException("Logger concurrent rotation self-test did not rotate.");
            }
        }
        finally
        {
            start.Set();
            for (int i = 0; i < writers.Length; i++)
            {
                if (writers[i] != null && writers[i].IsAlive)
                {
                    writers[i].Join(2000);
                }
            }

            start.Dispose();
            for (int i = 0; i < ready.Length; i++)
            {
                if (ready[i] != null)
                {
                    ready[i].Dispose();
                }
            }
        }
    }

    private static void AssertIndependentNamedMutexHandles(string mutexName)
    {
        using (Mutex first = new Mutex(false, mutexName))
        using (Mutex second = new Mutex(false, mutexName))
        using (ManualResetEvent firstAcquired = new ManualResetEvent(false))
        using (ManualResetEvent releaseFirst = new ManualResetEvent(false))
        {
            Exception holderFailure = null;
            Thread holder = new Thread(delegate()
            {
                bool ownsMutex = false;
                try
                {
                    try
                    {
                        ownsMutex = first.WaitOne(1000);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                    {
                        throw new TimeoutException("Logger first named mutex handle was not acquired.");
                    }

                    firstAcquired.Set();
                    releaseFirst.WaitOne(2000);
                }
                catch (Exception ex)
                {
                    holderFailure = ex;
                }
                finally
                {
                    if (ownsMutex)
                    {
                        try
                        {
                            first.ReleaseMutex();
                        }
                        catch (Exception ex)
                        {
                            if (holderFailure == null)
                            {
                                holderFailure = ex;
                            }
                        }
                    }
                }
            });
            holder.IsBackground = true;
            holder.Start();

            if (!firstAcquired.WaitOne(2000))
            {
                releaseFirst.Set();
                holder.Join(2000);
                throw new TimeoutException("Logger named mutex handle self-test did not start.");
            }

            bool secondAcquired = false;
            try
            {
                secondAcquired = second.WaitOne(0);
                if (secondAcquired)
                {
                    throw new InvalidOperationException("Independent logger mutex handles did not coordinate.");
                }
            }
            finally
            {
                if (secondAcquired)
                {
                    second.ReleaseMutex();
                }

                releaseFirst.Set();
            }

            if (!holder.Join(2000))
            {
                throw new TimeoutException("Logger named mutex holder did not stop.");
            }

            if (holderFailure != null)
            {
                throw new InvalidOperationException("Logger named mutex holder failed.", holderFailure);
            }
        }
    }

    private static void RunAppendMutexTimeoutSelfTest(string testDirectory)
    {
        Directory.CreateDirectory(testDirectory);
        string mutexName = @"Local\" + ProductIdentity.MachineName + ".Logger.TimeoutSelfTest." + Guid.NewGuid().ToString("N");
        string activePath = Path.Combine(testDirectory, "timeout.log");
        File.WriteAllBytes(activePath, new byte[256]);

        AppendCoordinationState state = new AppendCoordinationState(
            mutexName,
            128,
            AppendRotateMutexWaitMs,
            false);
        ManualResetEvent holderReady = new ManualResetEvent(false);
        ManualResetEvent releaseHolder = new ManualResetEvent(false);
        Exception holderFailure = null;
        Thread holder = new Thread(delegate()
        {
            bool ownsMutex = false;
            try
            {
                using (Mutex mutex = new Mutex(false, mutexName))
                {
                    try
                    {
                        ownsMutex = mutex.WaitOne(1000);
                    }
                    catch (AbandonedMutexException)
                    {
                        ownsMutex = true;
                    }

                    if (!ownsMutex)
                    {
                        throw new TimeoutException("Logger timeout holder could not acquire the mutex.");
                    }

                    holderReady.Set();
                    releaseHolder.WaitOne(5000);
                    mutex.ReleaseMutex();
                    ownsMutex = false;
                }
            }
            catch (Exception ex)
            {
                holderFailure = ex;
            }
        });
        holder.IsBackground = true;

        try
        {
            holder.Start();
            if (!holderReady.WaitOne(2000))
            {
                throw new TimeoutException("Logger timeout holder did not start.");
            }

            string firstMarker = "LOGGER_TIMEOUT_APPEND_1_" + Guid.NewGuid().ToString("N");
            string secondMarker = "LOGGER_TIMEOUT_APPEND_2_" + Guid.NewGuid().ToString("N");
            Stopwatch stopwatch = Stopwatch.StartNew();
            AppendImmediate(activePath, firstMarker + Environment.NewLine, state);
            long firstElapsedMs = stopwatch.ElapsedMilliseconds;
            stopwatch.Restart();
            AppendImmediate(activePath, secondMarker + Environment.NewLine, state);
            long secondElapsedMs = stopwatch.ElapsedMilliseconds;
            stopwatch.Stop();

            long elapsedLimitMs = AppendRotateMutexWaitMs + 750L;
            if (firstElapsedMs > elapsedLimitMs || secondElapsedMs > elapsedLimitMs)
            {
                throw new InvalidOperationException(
                    "Logger mutex timeout self-test exceeded its bounded wait: " +
                    firstElapsedMs.ToString(CultureInfo.InvariantCulture) + "ms, " +
                    secondElapsedMs.ToString(CultureInfo.InvariantCulture) + "ms.");
            }

            if (Directory.GetFiles(testDirectory, "timeout-*.log").Length != 0)
            {
                throw new InvalidOperationException("Logger mutex timeout fallback performed a rotation.");
            }

            string text = File.ReadAllText(activePath, Encoding.UTF8);
            AssertMarkerOccursOnce(text, firstMarker, "first timeout append");
            AssertMarkerOccursOnce(text, secondMarker, "second timeout append");
            AssertMarkerOccursOnce(text, "LOGGER_MUTEX_TIMEOUT", "one-time mutex timeout diagnostic");
        }
        finally
        {
            releaseHolder.Set();
            if (holder.IsAlive)
            {
                holder.Join(2000);
            }

            holderReady.Dispose();
            releaseHolder.Dispose();
        }

        if (holderFailure != null)
        {
            throw new InvalidOperationException("Logger timeout holder failed.", holderFailure);
        }
    }

    private static string ReadAllMatchingLogs(string directoryPath, string pattern)
    {
        StringBuilder combined = new StringBuilder();
        string[] paths = Directory.GetFiles(directoryPath, pattern);
        for (int i = 0; i < paths.Length; i++)
        {
            combined.Append(File.ReadAllText(paths[i], Encoding.UTF8));
        }

        return combined.ToString();
    }

    private static void AssertMarkerOccursOnce(string text, string marker, string label)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        if (count != 1)
        {
            throw new InvalidOperationException(
                "Logger " + label + " marker count was " + count.ToString(CultureInfo.InvariantCulture) + ", expected 1.");
        }
    }

    private static void RunWriteFailureSelfTest(string testDirectory)
    {
        Directory.CreateDirectory(testDirectory);

        string previousDirectoryOverride;
        string previousInfoBuffer;
        int previousInfoBufferBytes;
        DateTime previousDirectoryLimitCheckUtc;
        lock (SyncRoot)
        {
            previousDirectoryOverride = storageDirectoryOverride;
            previousInfoBuffer = InfoBuffer.ToString();
            previousInfoBufferBytes = infoBufferBytes;
            previousDirectoryLimitCheckUtc = lastDirectoryLimitCheckUtc;

            storageDirectoryOverride = testDirectory;
            InfoBuffer.Clear();
            infoBufferBytes = 0;
            lastDirectoryLimitCheckUtc = DateTime.MinValue;
        }

        try
        {
            string activePath = LogPath;
            File.WriteAllText(activePath, string.Empty, SharedEncoding.Utf8NoBom);
            using (FileStream lockedLog = new FileStream(
                activePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                try
                {
                    Info("logger-storage-selftest-locked-info");
                    Error(new IOException("logger-storage-selftest-locked-error"));
                    Flush();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Logger locked-file self-test failed: logging propagated a storage exception.",
                        ex);
                }
            }

            string recoveryMarker = "logger-storage-selftest-recovery-" + Guid.NewGuid().ToString("N");
            Error(new InvalidOperationException(recoveryMarker));
            Flush();

            string recoveredText = File.ReadAllText(activePath, Encoding.UTF8);
            if (recoveredText.IndexOf(recoveryMarker, StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Logger storage recovery self-test failed.");
            }
        }
        finally
        {
            lock (SyncRoot)
            {
                storageDirectoryOverride = previousDirectoryOverride;
                InfoBuffer.Clear();
                InfoBuffer.Append(previousInfoBuffer);
                infoBufferBytes = previousInfoBufferBytes;
                lastDirectoryLimitCheckUtc = previousDirectoryLimitCheckUtc;
            }
        }
    }

    private static void RunRedactionSelfTest(string testDirectory)
    {
        Directory.CreateDirectory(testDirectory);

        const string authorizationSecret = "C7_AUTHORIZATION_SECRET_31A7";
        const string bearerSecret = "C7_BEARER_SECRET_42B8";
        const string cookieSecret = "C7_COOKIE_SECRET_53C9";
        const string tokenSecret = "C7_ACCESS_TOKEN_SECRET_64D0";
        const string apiKeySecret = "C7_API_KEY_SECRET_75E1";
        const string jwtSecret = "eyJjN19oZWFkZXIiOiIxIn0.eyJjN19wYXlsb2FkIjoiMiJ9.c7_signature_86F2";
        const string setupTokenSecret = "C7_SETUP_TOKEN_SECRET_97A3";
        const string standaloneSetupTokenSecret = "oauth-C7_STANDALONE_SETUP_TOKEN_A8B4";
        const string structuredTokenArraySecret = "C7_STRUCTURED_TOKEN_ARRAY_B9C5";
        const string errorCode = "E_C7_TIMEOUT";
        const string correlationId = "corr-c7-0001";

        string rawFixture =
            "{\"Authorization\":\"" + authorizationSecret +
            "\",\"access_token\":\"" + tokenSecret +
            "\",\"api_key\":\"" + apiKeySecret +
            "\",\"jwt\":\"" + jwtSecret +
            "\",\"setup-token\":\"" + setupTokenSecret +
            "\",\"error_code\":\"" + errorCode +
            "\",\"correlation_id\":\"" + correlationId +
            "\",\"tokens\":[\"" + structuredTokenArraySecret +
            "\"],\"token\":42,\"input_tokens\":128,\"output_tokens\":64,\"token_count\":192,\"token_usage\":{\"total\":192}}" +
            Environment.NewLine +
            "Authorization: Basic " + authorizationSecret + " correlation_id=" + correlationId + " error_code=" + errorCode +
            Environment.NewLine +
            "Bearer " + bearerSecret + " correlation_id=" + correlationId +
            Environment.NewLine +
            "Cookie: sid=" + cookieSecret + " correlation_id=" + correlationId +
            Environment.NewLine +
            "token=" + tokenSecret + " api_key=" + apiKeySecret + " setup-token=" + setupTokenSecret +
            Environment.NewLine +
            "compact_jwt=" + jwtSecret + " setup_source=" + standaloneSetupTokenSecret +
            Environment.NewLine +
            "token=42 token count=192 error_code=" + errorCode + " correlation_id=" + correlationId +
            Environment.NewLine;

        string sanitizedOnce = RedactForPersistence(rawFixture);
        string sanitizedTwice = RedactForPersistence(sanitizedOnce);
        if (!string.Equals(sanitizedOnce, sanitizedTwice, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Logger redaction self-test failed: sanitization is not idempotent.");
        }

        string activePath = Path.Combine(testDirectory, "redaction.log");
        AppendCoordinationState state = new AppendCoordinationState(
            @"Local\" + ProductIdentity.MachineName + ".Logger.RedactionSelfTest.v1",
            128 * 1024,
            AppendRotateMutexWaitMs,
            false);

        // This deliberately bypasses FormatLine so the final persistence boundary is proven independently.
        AppendImmediate(activePath, rawFixture, state);
        string persisted = ReadAllMatchingLogs(testDirectory, "*.log");

        string[] fixtureSecrets = new string[]
        {
            authorizationSecret,
            bearerSecret,
            cookieSecret,
            tokenSecret,
            apiKeySecret,
            jwtSecret,
            setupTokenSecret,
            standaloneSetupTokenSecret,
            structuredTokenArraySecret
        };
        for (int i = 0; i < fixtureSecrets.Length; i++)
        {
            if (persisted.IndexOf(fixtureSecrets[i], StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    "Logger redaction self-test failed: fixture secret index " +
                    i.ToString(CultureInfo.InvariantCulture) + " reached disk.");
            }
        }

        AssertPersistedRedactionText(persisted, RedactedValue, "redaction marker");
        AssertPersistedRedactionText(persisted, "\"error_code\":\"" + errorCode + "\"", "structured error code");
        AssertPersistedRedactionText(persisted, "\"correlation_id\":\"" + correlationId + "\"", "structured correlation id");
        AssertPersistedRedactionText(persisted, "\"input_tokens\":128", "input token metric");
        AssertPersistedRedactionText(persisted, "\"output_tokens\":64", "output token metric");
        AssertPersistedRedactionText(persisted, "\"token_count\":192", "token count metric");
        AssertPersistedRedactionText(persisted, "\"token_usage\":{\"total\":192}", "token usage metric");
        AssertPersistedRedactionText(persisted, "\"token\":42", "structured numeric token metric");
        AssertPersistedRedactionText(persisted, "token=42", "numeric token metric");
        AssertPersistedRedactionText(persisted, "error_code=" + errorCode, "plain error code");
        AssertPersistedRedactionText(persisted, "correlation_id=" + correlationId, "plain correlation id");
    }

    private static void AssertPersistedRedactionText(string text, string expected, string label)
    {
        if (text.IndexOf(expected, StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Logger redaction self-test failed: missing " + label + ".");
        }
    }

    private static string FormatLine(string level, string message)
    {
        // Local time must carry its real UTC offset. The old "u" format stamped local time with a
        // "Z" suffix, which made the text log unusable as UTC evidence during forensic timelines.
        return DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture) +
            " [" + level + "] " + RedactForPersistence(message) + Environment.NewLine;
    }

    private static string RedactForPersistence(string text)
    {
        if (string.IsNullOrEmpty(text) || !MayContainSensitiveText(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            string redacted = RedactStructuredValues(text);
            redacted = SensitiveHeaderPattern.Replace(redacted, delegate(Match match)
            {
                return match.Groups["prefix"].Value + RedactedValue;
            });
            redacted = NamedValuePattern.Replace(redacted, RedactNamedValue);
            redacted = BearerPattern.Replace(redacted, delegate(Match match)
            {
                return match.Groups["prefix"].Value + RedactedValue;
            });
            redacted = JwtPattern.Replace(redacted, RedactedValue);
            redacted = StandaloneTokenPattern.Replace(redacted, RedactedValue);
            return redacted;
        }
        catch
        {
            // A sanitizer failure must fail closed: dropping one diagnostic is safer than persisting its secret.
            return "[LOGGER_REDACTION_FAILED]";
        }
    }

    private static bool MayContainSensitiveText(string text)
    {
        return text.IndexOf("authorization", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("bearer", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("api_key", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("api-key", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("apikey", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("jwt", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("eyJ", StringComparison.Ordinal) >= 0 ||
            text.IndexOf("sk-", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("oauth-", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string RedactStructuredValues(string text)
    {
        StringBuilder output = null;
        int copyStart = 0;
        int index = 0;
        while (index < text.Length)
        {
            if (text[index] != '"')
            {
                index++;
                continue;
            }

            int keyEnd = FindJsonStringEnd(text, index);
            if (keyEnd < 0)
            {
                break;
            }

            int colon = keyEnd + 1;
            while (colon < text.Length && char.IsWhiteSpace(text[colon]))
            {
                colon++;
            }

            if (colon >= text.Length || text[colon] != ':')
            {
                index = keyEnd + 1;
                continue;
            }

            string normalizedKey = NormalizeStructuredKey(text.Substring(index + 1, keyEnd - index - 1));
            if (!IsSensitiveStructuredKey(normalizedKey))
            {
                index = keyEnd + 1;
                continue;
            }

            int valueStart = colon + 1;
            while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
            {
                valueStart++;
            }

            if (valueStart >= text.Length)
            {
                break;
            }

            int valueEnd = FindJsonValueEnd(text, valueStart);
            if (ShouldPreserveNumericTokenMetric(normalizedKey, text, valueStart, valueEnd))
            {
                index = Math.Max(valueStart + 1, valueEnd);
                continue;
            }

            if (output == null)
            {
                output = new StringBuilder(text.Length);
            }

            output.Append(text, copyStart, valueStart - copyStart);
            output.Append('"');
            output.Append(RedactedValue);
            output.Append('"');
            copyStart = valueEnd;
            index = Math.Max(valueStart + 1, valueEnd);
        }

        if (output == null)
        {
            return text;
        }

        output.Append(text, copyStart, text.Length - copyStart);
        return output.ToString();
    }

    private static int FindJsonStringEnd(string text, int openingQuote)
    {
        bool escaped = false;
        for (int i = openingQuote + 1; i < text.Length; i++)
        {
            char current = text[i];
            if (escaped)
            {
                escaped = false;
            }
            else if (current == '\\')
            {
                escaped = true;
            }
            else if (current == '"')
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindJsonValueEnd(string text, int valueStart)
    {
        char first = text[valueStart];
        if (first == '"')
        {
            int stringEnd = FindJsonStringEnd(text, valueStart);
            return stringEnd < 0 ? text.Length : stringEnd + 1;
        }

        if (first == '{' || first == '[')
        {
            int depth = 0;
            bool inString = false;
            bool escaped = false;
            for (int i = valueStart; i < text.Length; i++)
            {
                char current = text[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }
                }
                else if (current == '"')
                {
                    inString = true;
                }
                else if (current == '{' || current == '[')
                {
                    depth++;
                }
                else if (current == '}' || current == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i + 1;
                    }
                }
            }

            return text.Length;
        }

        int end = valueStart;
        while (end < text.Length &&
            text[end] != ',' &&
            text[end] != '}' &&
            text[end] != ']' &&
            text[end] != '\r' &&
            text[end] != '\n' &&
            !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        return Math.Max(valueStart + 1, end);
    }

    private static string NormalizeStructuredKey(string encodedKey)
    {
        string decoded = DecodeJsonKey(encodedKey);
        StringBuilder normalized = new StringBuilder(decoded.Length);
        bool previousWasSeparator = false;
        for (int i = 0; i < decoded.Length; i++)
        {
            char current = decoded[i];
            bool separator = current == '-' || current == ' ' || current == '.';
            if (separator)
            {
                if (normalized.Length > 0 && !previousWasSeparator)
                {
                    normalized.Append('_');
                }

                previousWasSeparator = true;
                continue;
            }

            if (char.IsUpper(current) &&
                normalized.Length > 0 &&
                !previousWasSeparator &&
                char.IsLower(decoded[i - 1]))
            {
                normalized.Append('_');
            }

            normalized.Append(char.ToLowerInvariant(current));
            previousWasSeparator = current == '_';
        }

        return normalized.ToString().Trim('_');
    }

    private static string DecodeJsonKey(string encodedKey)
    {
        if (encodedKey.IndexOf('\\') < 0)
        {
            return encodedKey;
        }

        StringBuilder decoded = new StringBuilder(encodedKey.Length);
        for (int i = 0; i < encodedKey.Length; i++)
        {
            char current = encodedKey[i];
            if (current != '\\' || i + 1 >= encodedKey.Length)
            {
                decoded.Append(current);
                continue;
            }

            char escaped = encodedKey[++i];
            if (escaped == 'u' && i + 4 < encodedKey.Length)
            {
                int codePoint;
                if (int.TryParse(
                    encodedKey.Substring(i + 1, 4),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out codePoint))
                {
                    decoded.Append((char)codePoint);
                    i += 4;
                    continue;
                }
            }

            decoded.Append(escaped);
        }

        return decoded.ToString();
    }

    private static bool IsSensitiveStructuredKey(string normalizedKey)
    {
        if (IsTokenMetricKey(normalizedKey))
        {
            return false;
        }

        if (normalizedKey == "authorization" ||
            normalizedKey == "proxy_authorization" ||
            normalizedKey == "cookie" ||
            normalizedKey == "cookies" ||
            normalizedKey == "set_cookie" ||
            normalizedKey == "api_key" ||
            normalizedKey == "apikey" ||
            normalizedKey == "jwt" ||
            normalizedKey == "setup_token")
        {
            return true;
        }

        return normalizedKey.EndsWith("_authorization", StringComparison.Ordinal) ||
            normalizedKey.EndsWith("_cookie", StringComparison.Ordinal) ||
            normalizedKey.EndsWith("_api_key", StringComparison.Ordinal) ||
            normalizedKey == "token" ||
            normalizedKey == "tokens" ||
            normalizedKey.StartsWith("token_", StringComparison.Ordinal) ||
            normalizedKey.EndsWith("_token", StringComparison.Ordinal) ||
            normalizedKey.IndexOf("_token_", StringComparison.Ordinal) >= 0;
    }

    private static bool IsTokenMetricKey(string normalizedKey)
    {
        return normalizedKey == "input_tokens" ||
            normalizedKey == "output_tokens" ||
            normalizedKey == "total_tokens" ||
            normalizedKey == "prompt_tokens" ||
            normalizedKey == "completion_tokens" ||
            normalizedKey == "cached_tokens" ||
            normalizedKey == "reasoning_tokens" ||
            normalizedKey == "max_tokens" ||
            normalizedKey == "token_count" ||
            normalizedKey == "token_counts" ||
            normalizedKey == "token_usage" ||
            normalizedKey.StartsWith("token_usage_", StringComparison.Ordinal) ||
            normalizedKey == "token_budget" ||
            normalizedKey == "token_limit" ||
            normalizedKey == "token_total" ||
            normalizedKey == "token_rate" ||
            normalizedKey == "token_type" ||
            normalizedKey == "token_source" ||
            normalizedKey == "token_status" ||
            normalizedKey == "token_configured" ||
            normalizedKey == "token_available" ||
            normalizedKey == "token_known" ||
            normalizedKey.EndsWith("_token_count", StringComparison.Ordinal) ||
            normalizedKey.EndsWith("_token_usage", StringComparison.Ordinal);
    }

    private static string RedactNamedValue(Match match)
    {
        string normalizedKey = NormalizeStructuredKey(match.Groups["key"].Value);
        if (!IsSensitiveStructuredKey(normalizedKey) ||
            ShouldPreserveNumericTokenMetric(
                normalizedKey,
                match.Groups["value"].Value,
                0,
                match.Groups["value"].Value.Length))
        {
            return match.Value;
        }

        return match.Groups["prefix"].Value + RedactedValue;
    }

    private static bool ShouldPreserveNumericTokenMetric(
        string normalizedKey,
        string text,
        int valueStart,
        int valueEnd)
    {
        if ((normalizedKey != "token" && normalizedKey != "tokens") ||
            valueStart < 0 ||
            valueEnd <= valueStart ||
            valueEnd > text.Length)
        {
            return false;
        }

        string value = text.Substring(valueStart, valueEnd - valueStart).Trim();
        if (value.Length == 0 || value[0] == '"' || value[0] == '\'')
        {
            return false;
        }

        double numericValue;
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out numericValue);
    }

    private static void FlushInfoBufferLocked()
    {
        if (InfoBuffer.Length == 0)
        {
            return;
        }

        string text = InfoBuffer.ToString();
        AppendImmediate(LogPath, text);

        // AppendImmediate suppresses storage failures. Clearing after every attempt deliberately
        // drops a failed INFO batch so an unavailable log path cannot cause unbounded memory growth.
        InfoBuffer.Clear();
        infoBufferBytes = 0;
    }

    private static void AppendImmediate(string path, string text)
    {
        AppendImmediate(path, text, ProductionAppendCoordination);
    }

    private static void AppendImmediate(string path, string text, AppendCoordinationState coordination)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // Every Logger destination converges here. Redact again before byte sizing, rotation and
        // persistence so direct probe text and already-serialized JSON cannot bypass call-site guards.
        text = RedactForPersistence(text);

        Mutex mutex = null;
        bool ownsMutex = false;
        bool appended = false;
        bool rotated = false;
        try
        {
            string directoryPath = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            bool mutexTimedOut = false;
            try
            {
                mutex = new Mutex(false, coordination.MutexName);
                try
                {
                    ownsMutex = mutex.WaitOne(coordination.WaitMilliseconds);
                    mutexTimedOut = !ownsMutex;
                }
                catch (AbandonedMutexException)
                {
                    // Windows transfers ownership when reporting abandonment; releasing is required.
                    ownsMutex = true;
                }
            }
            catch
            {
                ownsMutex = false;
            }

            if (!ownsMutex)
            {
                // Never wait on another process indefinitely. The degraded path deliberately skips
                // rotation because size-check + rename + append is only safe while the mutex is held.
                string fallbackText = text;
                if (Interlocked.CompareExchange(ref coordination.TimeoutDiagnosticWritten, 1, 0) == 0)
                {
                    fallbackText += FormatLine(
                        "WARN",
                        mutexTimedOut
                            ? "LOGGER_MUTEX_TIMEOUT append-only fallback; rotation skipped."
                            : "LOGGER_MUTEX_UNAVAILABLE append-only fallback; rotation skipped.");
                }

                AppendText(path, fallbackText);
                return;
            }

            long incomingBytes = Encoding.UTF8.GetByteCount(text);
            rotated = RotateActiveLogIfNeeded(path, incomingBytes, coordination.MaxActiveBytes);
            AppendText(path, text);
            appended = true;
        }
        catch
        {
            // Logging must never become an application failure path.
        }
        finally
        {
            if (ownsMutex && mutex != null)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch
                {
                }
            }

            if (mutex != null)
            {
                try
                {
                    mutex.Dispose();
                }
                catch
                {
                }
            }
        }

        if (appended && coordination.EnforceDirectoryLimit)
        {
            try
            {
                // Directory cleanup is intentionally outside the short append/rotation critical section.
                EnforceLogDirectoryLimit(rotated);
            }
            catch
            {
            }
        }
    }

    private static void AppendText(string path, string text)
    {
        // A deployment can briefly overlap old and new processes. Sharing read/write access keeps
        // the timeout fallback best-effort while the named mutex serializes the normal write path.
        using (FileStream stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete))
        using (StreamWriter writer = new StreamWriter(stream, Encoding.UTF8))
        {
            writer.Write(text);
        }
    }

    private static bool RotateActiveLogIfNeeded(string path, long incomingBytes)
    {
        return RotateActiveLogIfNeeded(path, incomingBytes, MaxActiveLogBytes);
    }

    private static bool RotateActiveLogIfNeeded(string path, long incomingBytes, long maxActiveBytes)
    {
        FileInfo file = new FileInfo(path);
        if (!file.Exists || file.Length + Math.Max(0, incomingBytes) <= maxActiveBytes)
        {
            return false;
        }

        // Rotation is an O(1) rename; unlike tail trimming it never rewrites the full active log.
        string baseName = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string directoryPath = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directoryPath))
        {
            directoryPath = DirectoryPath;
        }

        string archivePath = Path.Combine(directoryPath, baseName + "-" + timestamp + extension);
        int suffix = 1;
        while (File.Exists(archivePath))
        {
            archivePath = Path.Combine(
                directoryPath,
                baseName + "-" + timestamp + "-" + suffix.ToString(CultureInfo.InvariantCulture) + extension);
            suffix++;
        }

        File.Move(path, archivePath);
        return true;
    }

    private static void EnforceLogDirectoryLimit(bool force)
    {
        DateTime nowUtc = DateTime.UtcNow;
        if (!force &&
            lastDirectoryLimitCheckUtc != DateTime.MinValue &&
            nowUtc - lastDirectoryLimitCheckUtc < DirectoryLimitCheckInterval)
        {
            return;
        }

        // Directory enumeration is bounded but still touches storage metadata; throttle normal writes
        // while forcing a check after rotation so archived logs cannot grow unchecked.
        lastDirectoryLimitCheckUtc = nowUtc;
        EnforceLogDirectoryLimit(
            DirectoryPath,
            new string[] { LogPath, ErrorLogPath, GfwProbeLogPath },
            MaxLogDirectoryBytes);
    }

    private static void EnforceLogDirectoryLimit(string directoryPath, string[] activePaths, long maxBytes)
    {
        DirectoryInfo directory = new DirectoryInfo(directoryPath);
        if (!directory.Exists)
        {
            return;
        }

        FileInfo[] files = directory.GetFiles("*.log");
        long total = 0;
        for (int i = 0; i < files.Length; i++)
        {
            total += files[i].Length;
        }

        if (total <= maxBytes)
        {
            return;
        }

        Array.Sort(files, CompareLogFileAge);
        for (int i = 0; i < files.Length && total > maxBytes; i++)
        {
            if (IsActiveLogPath(files[i].FullName, activePaths))
            {
                continue;
            }

            long length = files[i].Length;
            try
            {
                files[i].Delete();
                total -= length;
            }
            catch
            {
            }
        }
    }

    private static bool IsActiveLogPath(string path, string[] activePaths)
    {
        for (int i = 0; i < activePaths.Length; i++)
        {
            if (string.Equals(path, activePaths[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int CompareLogFileAge(FileInfo left, FileInfo right)
    {
        int result = left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
        if (result != 0)
        {
            return result;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AppendCoordinationState
    {
        public readonly string MutexName;
        public readonly long MaxActiveBytes;
        public readonly int WaitMilliseconds;
        public readonly bool EnforceDirectoryLimit;
        public int TimeoutDiagnosticWritten;

        public AppendCoordinationState(
            string mutexName,
            long maxActiveBytes,
            int waitMilliseconds,
            bool enforceDirectoryLimit)
        {
            MutexName = mutexName;
            MaxActiveBytes = maxActiveBytes;
            WaitMilliseconds = Math.Max(0, Math.Min(AppendRotateMutexWaitMs, waitMilliseconds));
            EnforceDirectoryLimit = enforceDirectoryLimit;
        }
    }
}
