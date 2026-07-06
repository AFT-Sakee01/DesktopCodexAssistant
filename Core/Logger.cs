using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Logger
{
    private const long MaxLogDirectoryBytes = 10L * 1024L * 1024L;
    private const long MaxActiveLogBytes = 3L * 1024L * 1024L;
    private const int MaxBufferedInfoBytes = 64 * 1024;
    private const int InfoFlushIntervalMs = 5 * 60 * 1000;
    private static readonly object SyncRoot = new object();
    private static readonly StringBuilder InfoBuffer = new StringBuilder();
    private static readonly System.Threading.Timer FlushTimer;
    private static int infoBufferBytes;
    private static bool shuttingDown;

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
                builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
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

    private static string FormatLine(string level, string message)
    {
        return DateTime.Now.ToString("u") + " [" + level + "] " + message + Environment.NewLine;
    }

    private static void FlushInfoBufferLocked()
    {
        if (InfoBuffer.Length == 0)
        {
            return;
        }

        string text = InfoBuffer.ToString();
        InfoBuffer.Clear();
        infoBufferBytes = 0;
        AppendImmediate(LogPath, text);
    }

    private static void AppendImmediate(string path, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Directory.CreateDirectory(DirectoryPath);
        long incomingBytes = Encoding.UTF8.GetByteCount(text);
        RotateActiveLogIfNeeded(path, incomingBytes);
        File.AppendAllText(path, text, Encoding.UTF8);
        EnforceLogDirectoryLimit();
    }

    private static void RotateActiveLogIfNeeded(string path, long incomingBytes)
    {
        FileInfo file = new FileInfo(path);
        if (!file.Exists || file.Length + Math.Max(0, incomingBytes) <= MaxActiveLogBytes)
        {
            return;
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
    }

    private static void EnforceLogDirectoryLimit()
    {
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
}
