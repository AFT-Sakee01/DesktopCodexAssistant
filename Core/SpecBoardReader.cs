using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;

internal sealed class SpecBoardRow
{
    public string Id;
    public string Project;
    public string SpecPath;
    public string Title;
    public string Status;
    public DateTime? EventTimeUtc;
    public bool FileMissing;
    public bool IsUnregistered;
    public string ProjectRoot;
    public DateTime? UpdatedUtc;
    public DateTime? RegisteredUtc;
    public DateTime? ExecutedUtc;
    public DateTime? VerifiedUtc;
    public DateTime? RevisionRequestedUtc;
    public DateTime? AbandonedUtc;
    public string UpdatedBy;
    public string Note;
    public string AbandonedReason;

    public string AbsolutePath
    {
        get
        {
            if (string.IsNullOrEmpty(this.ProjectRoot) || string.IsNullOrEmpty(this.SpecPath))
            {
                return string.Empty;
            }

            string resolved;
            return SpecBoardPathPolicy.TryResolve(this.ProjectRoot, this.SpecPath, out resolved)
                ? resolved
                : string.Empty;
        }
    }
}

internal sealed class SpecBoardProject
{
    public string Name;
    public string Display;
    public string Root;
    public string SpecGlob;
    public bool Reachable = true;
}

internal sealed class SpecBoardSnapshot
{
    public readonly List<SpecBoardRow> Rows = new List<SpecBoardRow>();
    public readonly List<SpecBoardProject> Projects = new List<SpecBoardProject>();
    public int MalformedLines;
    public bool LedgerMissing;
    public bool ProjectRegistryAvailable;
    public bool ReconciliationTimedOut;
    public int UnreachableProjects;
    public DateTime? LedgerLastWriteLocal;
    public DateTime ScanTimeUtc;
    public string LedgerPath = string.Empty;

    public int Count(string project, string status)
    {
        return this.Rows.Count(row =>
            (string.IsNullOrEmpty(project) || string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase));
    }
}

internal static class SpecBoardReader
{
    internal const int MaxFileBytes = 2 * 1024 * 1024;
    internal const int MaxLineBytes = 64 * 1024;
    internal const int MaxLedgerLines = 5000;
    internal const int MaxProjects = 64;
    internal const int MaxScannedFiles = 512;

    public static SpecBoardSnapshot Read(string ledgerPath, bool reconcile)
    {
        return Read(ledgerPath, reconcile, CancellationToken.None);
    }

    public static SpecBoardSnapshot Read(string ledgerPath, bool reconcile, CancellationToken cancellationToken)
    {
        SpecBoardSnapshot snapshot = new SpecBoardSnapshot();
        snapshot.LedgerPath = ledgerPath ?? string.Empty;
        snapshot.ScanTimeUtc = DateTime.UtcNow;
        ReadDiagnostics diagnostics = new ReadDiagnostics();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, SpecBoardProject> projectsByName = LoadProjects(
                snapshot,
                GetProjectsPath(ledgerPath),
                diagnostics,
                cancellationToken);
            LoadLedger(snapshot, ledgerPath, projectsByName, diagnostics, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AppendLedgerOnlyProjects(snapshot, projectsByName, diagnostics, cancellationToken);
            if (reconcile && !snapshot.LedgerMissing && snapshot.ProjectRegistryAvailable)
            {
                Reconcile(snapshot, projectsByName, diagnostics, cancellationToken);
            }

            return snapshot;
        }
        finally
        {
            diagnostics.LogSummary();
        }
    }

    private static Dictionary<string, SpecBoardProject> LoadProjects(
        SpecBoardSnapshot snapshot,
        string projectsPath,
        ReadDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        Dictionary<string, SpecBoardProject> result = new Dictionary<string, SpecBoardProject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrEmpty(projectsPath) || !File.Exists(projectsPath))
            {
                snapshot.ProjectRegistryAvailable = false;
                return result;
            }

            BoundedLineReadResult read = ReadBoundedLines(projectsPath, cancellationToken);
            ApplyReadLimits(snapshot, diagnostics, read, "projects");
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(string.Join("\n", read.Lines.ToArray())) as Dictionary<string, object>;
            object projectsValue;
            object[] projects = root != null && root.TryGetValue("projects", out projectsValue) ? projectsValue as object[] : null;
            if (projects == null)
            {
                snapshot.ProjectRegistryAvailable = false;
                return result;
            }

            for (int i = 0; i < projects.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (i >= MaxProjects)
                {
                    int omitted = projects.Length - MaxProjects;
                    snapshot.MalformedLines += omitted;
                    diagnostics.ExcessProjects += omitted;
                    diagnostics.Sources.Add("projects");
                    break;
                }

                Dictionary<string, object> value = projects[i] as Dictionary<string, object>;
                string name = ReadString(value, "name");
                string display = ReadString(value, "display");
                string rootPath = ReadString(value, "root");
                string specGlob = ReadString(value, "spec_glob");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(specGlob))
                {
                    snapshot.MalformedLines++;
                    continue;
                }

                SpecBoardProject project = new SpecBoardProject
                {
                    Name = name.Trim(),
                    Display = string.IsNullOrWhiteSpace(display) ? name.Trim() : display.Trim(),
                    Root = NormalizeRoot(rootPath),
                    SpecGlob = specGlob.Trim().Replace('\\', '/')
                };
                if (!result.ContainsKey(project.Name))
                {
                    result.Add(project.Name, project);
                    snapshot.Projects.Add(project);
                }
            }

            snapshot.ProjectRegistryAvailable = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            snapshot.ProjectRegistryAvailable = false;
            snapshot.Projects.Clear();
            result.Clear();
        }

        return result;
    }

    private static void LoadLedger(
        SpecBoardSnapshot snapshot,
        string ledgerPath,
        Dictionary<string, SpecBoardProject> projectsByName,
        ReadDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ledgerPath) || !File.Exists(ledgerPath))
            {
                snapshot.LedgerMissing = true;
                return;
            }

            snapshot.LedgerLastWriteLocal = File.GetLastWriteTime(ledgerPath);
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            BoundedLineReadResult read = ReadBoundedLines(ledgerPath, cancellationToken);
            ApplyReadLimits(snapshot, diagnostics, read, "ledger");
            for (int i = 0; i < read.Lines.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(read.Lines[i]))
                {
                    continue;
                }

                try
                {
                    Dictionary<string, object> value = serializer.DeserializeObject(read.Lines[i]) as Dictionary<string, object>;
                    SpecBoardRow row = ParseLedgerRow(value, projectsByName);
                    if (row == null)
                    {
                        snapshot.MalformedLines++;
                        continue;
                    }

                    snapshot.Rows.Add(row);
                }
                catch
                {
                    snapshot.MalformedLines++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            snapshot.LedgerMissing = true;
        }
    }

    private static SpecBoardRow ParseLedgerRow(Dictionary<string, object> value, Dictionary<string, SpecBoardProject> projectsByName)
    {
        string id = ReadString(value, "id");
        string projectName = ReadString(value, "project");
        string specPath = ReadString(value, "spec_path");
        string title = ReadString(value, "title");
        string status = ReadString(value, "status");
        string updatedUtc = ReadString(value, "updated_utc");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(projectName) ||
            string.IsNullOrWhiteSpace(specPath) || string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(updatedUtc) || !SpecBoardStatus.IsLedgerValue(status))
        {
            return null;
        }

        SpecBoardProject project;
        projectsByName.TryGetValue(projectName.Trim(), out project);
        string eventField = SpecBoardStatus.EventField(status);
        DateTime eventTime;
        string eventText = ReadString(value, eventField);
        if (!TryParseUtc(eventText, out eventTime))
        {
            TryParseUtc(updatedUtc, out eventTime);
        }

        return new SpecBoardRow
        {
            Id = id.Trim(),
            Project = projectName.Trim(),
            SpecPath = NormalizeRelativePath(specPath),
            Title = title.Trim(),
            Status = status.Trim().ToLowerInvariant(),
            EventTimeUtc = eventTime == DateTime.MinValue ? (DateTime?)null : eventTime,
            ProjectRoot = project == null ? string.Empty : project.Root,
            UpdatedUtc = NullableUtc(updatedUtc),
            RegisteredUtc = NullableUtc(ReadString(value, "registered_utc")),
            ExecutedUtc = NullableUtc(ReadString(value, "executed_utc")),
            VerifiedUtc = NullableUtc(ReadString(value, "verified_utc")),
            RevisionRequestedUtc = NullableUtc(ReadString(value, "revision_requested_utc")),
            AbandonedUtc = NullableUtc(ReadString(value, "abandoned_utc")),
            UpdatedBy = ReadString(value, "updated_by"),
            Note = ReadString(value, "note"),
            AbandonedReason = ReadString(value, "abandoned_reason")
        };
    }

    private static void AppendLedgerOnlyProjects(
        SpecBoardSnapshot snapshot,
        Dictionary<string, SpecBoardProject> projectsByName,
        ReadDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        HashSet<string> omittedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SpecBoardRow row in snapshot.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SpecBoardProject project;
            if (projectsByName.TryGetValue(row.Project, out project))
            {
                row.ProjectRoot = project.Root;
                continue;
            }

            // The registry cap also applies to project names discovered only in the ledger. Without
            // this guard a bounded 64-project registry could still expand to 5000 in-memory projects.
            if (projectsByName.Count >= MaxProjects)
            {
                if (omittedProjects.Add(row.Project))
                {
                    snapshot.MalformedLines++;
                    diagnostics.ExcessProjects++;
                    diagnostics.Sources.Add("ledger-projects");
                }

                continue;
            }

            project = new SpecBoardProject
            {
                Name = row.Project,
                Display = row.Project,
                Root = string.Empty,
                SpecGlob = string.Empty,
                Reachable = false
            };
            projectsByName.Add(project.Name, project);
            snapshot.Projects.Add(project);
        }
    }

    private static void Reconcile(
        SpecBoardSnapshot snapshot,
        Dictionary<string, SpecBoardProject> projectsByName,
        ReadDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        int scannedFiles = 0;
        bool scanLimitReached = false;
        foreach (SpecBoardProject project in snapshot.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(project.Root) || !Directory.Exists(project.Root))
            {
                project.Reachable = false;
                snapshot.UnreachableProjects++;
                continue;
            }

            string globDirectory;
            string pattern;
            SplitGlob(project.Root, project.SpecGlob, out globDirectory, out pattern);
            if (string.IsNullOrEmpty(globDirectory) || !Directory.Exists(globDirectory))
            {
                project.Reachable = false;
                snapshot.UnreachableProjects++;
                continue;
            }

            HashSet<string> ledgerPaths = new HashSet<string>(
                snapshot.Rows.Where(row => string.Equals(row.Project, project.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(row => NormalizeRelativePath(row.SpecPath)),
                StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(globDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scannedFiles >= MaxScannedFiles)
                {
                    snapshot.MalformedLines++;
                    diagnostics.ExcessScannedFiles++;
                    diagnostics.Sources.Add("scan");
                    scanLimitReached = true;
                    break;
                }

                scannedFiles++;
                string fileName = Path.GetFileName(file);
                if (fileName.IndexOf("GoalSpec", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                string relativePath = GetRelativePath(project.Root, file);
                if (!ledgerPaths.Contains(relativePath))
                {
                    snapshot.Rows.Add(new SpecBoardRow
                    {
                        Id = "unregistered:" + project.Name + ":" + relativePath,
                        Project = project.Name,
                        ProjectRoot = project.Root,
                        SpecPath = relativePath,
                        Title = Path.GetFileNameWithoutExtension(file),
                        Status = SpecBoardStatus.Unregistered,
                        EventTimeUtc = File.GetLastWriteTimeUtc(file),
                        IsUnregistered = true
                    });
                }
            }

            foreach (SpecBoardRow row in snapshot.Rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(row.Project, project.Name, StringComparison.OrdinalIgnoreCase) ||
                    (!string.Equals(row.Status, SpecBoardStatus.Pending, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(row.Status, SpecBoardStatus.NeedsRevision, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(row.Status, SpecBoardStatus.AwaitingVerify, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                row.ProjectRoot = project.Root;
                string absolutePath = row.AbsolutePath;
                row.FileMissing = string.IsNullOrEmpty(absolutePath) || !File.Exists(absolutePath);
            }

            if (scanLimitReached)
            {
                break;
            }
        }

        snapshot.ScanTimeUtc = DateTime.UtcNow;
    }

    private static void SplitGlob(string root, string glob, out string directory, out string pattern)
    {
        string normalized = (glob ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
        int slash = normalized.LastIndexOf(Path.DirectorySeparatorChar);
        string relativeDirectory = slash < 0 ? string.Empty : normalized.Substring(0, slash);
        pattern = slash < 0 ? normalized : normalized.Substring(slash + 1);
        directory = Path.GetFullPath(Path.Combine(root, relativeDirectory));
    }

    private static string GetProjectsPath(string ledgerPath)
    {
        try
        {
            string directory = Path.GetDirectoryName(ledgerPath);
            return string.IsNullOrEmpty(directory) ? string.Empty : Path.Combine(directory, "PROJECTS.json");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadString(Dictionary<string, object> value, string key)
    {
        object raw;
        return value != null && value.TryGetValue(key, out raw) && raw != null ? Convert.ToString(raw, CultureInfo.InvariantCulture) : string.Empty;
    }

    private static bool TryParseUtc(string value, out DateTime result)
    {
        DateTimeOffset parsed;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
        {
            result = parsed.UtcDateTime;
            return true;
        }

        result = DateTime.MinValue;
        return false;
    }

    private static DateTime? NullableUtc(string value)
    {
        DateTime parsed;
        return TryParseUtc(value, out parsed) ? (DateTime?)parsed : null;
    }

    private static string NormalizeRoot(string value)
    {
        try
        {
            return Path.GetFullPath((value ?? string.Empty).Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        return (value ?? string.Empty).Trim().Trim('"').Replace('\\', '/').TrimStart('/');
    }

    private static string GetRelativePath(string root, string path)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string pathFull = Path.GetFullPath(path);
        return pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
            ? pathFull.Substring(rootFull.Length).Replace('\\', '/')
            : Path.GetFileName(pathFull);
    }

    internal static List<string> ReadLedgerLinesStrict(string ledgerPath, CancellationToken cancellationToken)
    {
        BoundedLineReadResult read = ReadBoundedLines(ledgerPath, cancellationToken);
        if (read.FileLimitExceeded || read.OversizedLines > 0 || read.LineCountExceeded)
        {
            throw new InvalidDataException(
                "Spec Board 账本超过安全读取上限（2 MiB、64 KiB/行、5000 行），拒绝写入。" );
        }

        return read.Lines;
    }

    private static void ApplyReadLimits(
        SpecBoardSnapshot snapshot,
        ReadDiagnostics diagnostics,
        BoundedLineReadResult read,
        string source)
    {
        int limitEvents = (read.FileLimitExceeded ? 1 : 0) + read.OversizedLines + (read.LineCountExceeded ? 1 : 0);
        snapshot.MalformedLines += limitEvents;
        if (read.FileLimitExceeded)
        {
            diagnostics.OversizedFiles++;
        }

        diagnostics.OversizedLines += read.OversizedLines;
        if (read.LineCountExceeded)
        {
            diagnostics.ExcessLineSets++;
        }

        if (limitEvents > 0)
        {
            diagnostics.Sources.Add(source);
        }
    }

    private static BoundedLineReadResult ReadBoundedLines(string path, CancellationToken cancellationToken)
    {
        BoundedLineReadResult result = new BoundedLineReadResult();
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (MemoryStream line = new MemoryStream(Math.Min(MaxLineBytes, 4096)))
        {
            long readableBytes = Math.Min(stream.Length, MaxFileBytes);
            result.FileLimitExceeded = stream.Length > MaxFileBytes;
            byte[] buffer = new byte[4096];
            long consumed = 0;
            int physicalLines = 0;
            bool discardLine = false;
            while (consumed < readableBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, readableBytes - consumed);
                int read = stream.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    if ((i & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    byte value = buffer[i];
                    consumed++;
                    if (value == (byte)'\n')
                    {
                        physicalLines++;
                        if (!discardLine)
                        {
                            result.Lines.Add(DecodeLine(line));
                        }

                        line.SetLength(0);
                        discardLine = false;
                        if (physicalLines >= MaxLedgerLines &&
                            (i + 1 < read || consumed < readableBytes || stream.Length > readableBytes))
                        {
                            result.LineCountExceeded = true;
                            return result;
                        }

                        continue;
                    }

                    if (discardLine)
                    {
                        continue;
                    }

                    if (line.Length >= MaxLineBytes)
                    {
                        result.OversizedLines++;
                        discardLine = true;
                        line.SetLength(0);
                        continue;
                    }

                    line.WriteByte(value);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!result.FileLimitExceeded && (line.Length > 0 || discardLine))
            {
                physicalLines++;
                if (physicalLines > MaxLedgerLines)
                {
                    result.LineCountExceeded = true;
                }
                else if (!discardLine)
                {
                    result.Lines.Add(DecodeLine(line));
                }
            }
        }

        return result;
    }

    private static string DecodeLine(MemoryStream line)
    {
        byte[] bytes = line.ToArray();
        int count = bytes.Length;
        if (count > 0 && bytes[count - 1] == (byte)'\r')
        {
            count--;
        }

        string value = SharedEncoding.Utf8NoBom.GetString(bytes, 0, count);
        return value.Length > 0 && value[0] == '\uFEFF' ? value.Substring(1) : value;
    }

    private sealed class BoundedLineReadResult
    {
        public readonly List<string> Lines = new List<string>();
        public bool FileLimitExceeded;
        public int OversizedLines;
        public bool LineCountExceeded;
    }

    private sealed class ReadDiagnostics
    {
        public readonly HashSet<string> Sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int OversizedFiles;
        public int OversizedLines;
        public int ExcessLineSets;
        public int ExcessProjects;
        public int ExcessScannedFiles;

        public void LogSummary()
        {
            if (this.OversizedFiles == 0 && this.OversizedLines == 0 && this.ExcessLineSets == 0 &&
                this.ExcessProjects == 0 && this.ExcessScannedFiles == 0)
            {
                return;
            }

            Program.LogInfo(
                "SpecBoard bounded read truncated input. Sources=" + string.Join(",", this.Sources.ToArray()) +
                ", OversizedFiles=" + this.OversizedFiles.ToString(CultureInfo.InvariantCulture) +
                ", OversizedLines=" + this.OversizedLines.ToString(CultureInfo.InvariantCulture) +
                ", ExcessLineSets=" + this.ExcessLineSets.ToString(CultureInfo.InvariantCulture) +
                ", ExcessProjects=" + this.ExcessProjects.ToString(CultureInfo.InvariantCulture) +
                ", ExcessScannedFiles=" + this.ExcessScannedFiles.ToString(CultureInfo.InvariantCulture));
        }
    }

    internal static void RunSelfTest()
    {
        SpecBoardPathPolicy.RunSelfTest();
        RunBoundedReadSelfTest();
        string tempRoot = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-reader-" + Guid.NewGuid().ToString("N"));
        string technical = Path.Combine(tempRoot, "project", "Docs", "Technical");
        Directory.CreateDirectory(technical);
        try
        {
            string ledger = Path.Combine(tempRoot, "SPEC_BOARD.jsonl");
            string projects = Path.Combine(tempRoot, "PROJECTS.json");
            string missingSpec = "Docs/Technical/Missing-SPEC-v1.md";
            File.WriteAllText(
                projects,
                "{\"schema_version\":1,\"projects\":[{\"name\":\"Test\",\"display\":\"Test Project\",\"root\":" +
                new JavaScriptSerializer().Serialize(Path.Combine(tempRoot, "project")) +
                ",\"spec_glob\":\"Docs/Technical/*-SPEC-*.md\"}]}",
                SharedEncoding.Utf8NoBom);
            File.WriteAllText(
                ledger,
                "{\"schema_version\":1,\"id\":\"Test.missing\",\"project\":\"Test\",\"spec_path\":\"" + missingSpec + "\",\"title\":\"Missing\",\"status\":\"pending\",\"registered_utc\":\"2026-07-11T00:00:00Z\",\"updated_utc\":\"2026-07-11T00:00:00Z\"}\n" +
                "{\"schema_version\":1,\"id\":\"Test.revision\",\"project\":\"Test\",\"spec_path\":\"Docs/Technical/Revision-SPEC-v1.md\",\"title\":\"Revision\",\"status\":\"needs_revision\",\"updated_utc\":\"2026-07-12T00:00:00Z\"}\n" +
                "{\"schema_version\":1,\"id\":\"Test.invalid\",\"project\":\"Test\",\"spec_path\":\"Docs/Technical/Invalid-SPEC-v1.md\",\"title\":\"Invalid\",\"status\":\"unknown\",\"updated_utc\":\"2026-07-12T00:00:00Z\"}\n{bad json}\n",
                SharedEncoding.Utf8NoBom);
            File.WriteAllText(Path.Combine(technical, "Gate-SPEC-v1.md"), "fixture", SharedEncoding.Utf8NoBom);
            File.WriteAllText(Path.Combine(technical, "Codex-GoalSpec-ignore.md"), "fixture", SharedEncoding.Utf8NoBom);

            SpecBoardSnapshot snapshot = Read(ledger, true);
            SpecBoardRow revision = snapshot.Rows.FirstOrDefault(row => row.Id == "Test.revision");
            DateTime expectedRevisionUtc = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
            if (snapshot.LedgerMissing || !snapshot.ProjectRegistryAvailable || snapshot.MalformedLines != 2 ||
                snapshot.Count("Test", SpecBoardStatus.Unregistered) != 1 || snapshot.Rows.Count(row => row.Id == "Test.missing" && row.FileMissing) != 1)
            {
                throw new InvalidOperationException("Spec Board reader malformed-line, unregistered, or file-missing policy failed.");
            }

            if (revision == null || revision.UpdatedUtc != expectedRevisionUtc ||
                revision.RevisionRequestedUtc.HasValue || revision.EventTimeUtc != expectedRevisionUtc || !revision.FileMissing)
            {
                throw new InvalidOperationException("Spec Board needs_revision parsing or updated_utc fallback failed.");
            }

            SpecBoardSnapshot missing = Read(Path.Combine(tempRoot, "absent.jsonl"), true);
            if (!missing.LedgerMissing)
            {
                throw new InvalidOperationException("Spec Board missing-ledger policy failed.");
            }
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    internal static void RunBoundedReadSelfTest()
    {
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-bounds-" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(root, "project");
        string technical = Path.Combine(projectRoot, "Docs", "Technical");
        Directory.CreateDirectory(technical);
        try
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string ledger = Path.Combine(root, "SPEC_BOARD.jsonl");
            string projectsPath = Path.Combine(root, "PROJECTS.json");
            List<Dictionary<string, object>> projects = new List<Dictionary<string, object>>();
            for (int i = 0; i < MaxProjects + 1; i++)
            {
                projects.Add(new Dictionary<string, object>
                {
                    { "name", "Project" + i.ToString(CultureInfo.InvariantCulture) },
                    { "display", "Project " + i.ToString(CultureInfo.InvariantCulture) },
                    { "root", projectRoot },
                    { "spec_glob", "Docs/Technical/*-SPEC-*.md" }
                });
            }

            File.WriteAllText(
                projectsPath,
                serializer.Serialize(new Dictionary<string, object> { { "schema_version", 1 }, { "projects", projects.ToArray() } }),
                SharedEncoding.Utf8NoBom);
            File.WriteAllText(ledger, string.Empty, SharedEncoding.Utf8NoBom);
            for (int i = 0; i < MaxScannedFiles + 1; i++)
            {
                File.WriteAllText(
                    Path.Combine(technical, "Bounded-" + i.ToString("D3", CultureInfo.InvariantCulture) + "-SPEC-v1.md"),
                    "fixture",
                    SharedEncoding.Utf8NoBom);
            }

            SpecBoardSnapshot scanLimited = Read(ledger, true, CancellationToken.None);
            if (scanLimited.Projects.Count != MaxProjects ||
                scanLimited.Rows.Count(row => row.IsUnregistered) != MaxScannedFiles ||
                scanLimited.MalformedLines < 2)
            {
                throw new InvalidOperationException("Spec Board project-count or directory-scan limit self-test failed.");
            }

            File.WriteAllText(
                ledger,
                "{\"schema_version\":1,\"id\":\"LedgerOnly.row\",\"project\":\"LedgerOnly\",\"spec_path\":\"Docs/Technical/LedgerOnly.md\",\"title\":\"Ledger only\",\"status\":\"pending\",\"updated_utc\":\"2026-07-20T00:00:00Z\"}\n",
                SharedEncoding.Utf8NoBom);
            SpecBoardSnapshot ledgerProjectLimited = Read(ledger, false, CancellationToken.None);
            if (ledgerProjectLimited.Projects.Count != MaxProjects || ledgerProjectLimited.MalformedLines < 2)
            {
                throw new InvalidOperationException("Spec Board ledger-only project-count limit self-test failed.");
            }

            using (StreamWriter writer = new StreamWriter(ledger, false, SharedEncoding.Utf8NoBom))
            {
                for (int i = 0; i < MaxLedgerLines + 1; i++)
                {
                    writer.WriteLine(
                        "{\"schema_version\":1,\"id\":\"Project0.row" + i.ToString(CultureInfo.InvariantCulture) +
                        "\",\"project\":\"Project0\",\"spec_path\":\"Docs/Technical/Row-" + i.ToString(CultureInfo.InvariantCulture) +
                        ".md\",\"title\":\"Row\",\"status\":\"pending\",\"updated_utc\":\"2026-07-20T00:00:00Z\"}");
                }
            }

            SpecBoardSnapshot lineLimited = Read(ledger, false, CancellationToken.None);
            if (lineLimited.Rows.Count != MaxLedgerLines || lineLimited.MalformedLines < 1)
            {
                throw new InvalidOperationException("Spec Board ledger line-count limit self-test failed.");
            }

            string validRow =
                "{\"schema_version\":1,\"id\":\"Project0.valid\",\"project\":\"Project0\",\"spec_path\":\"Docs/Technical/Valid.md\",\"title\":\"Valid\",\"status\":\"pending\",\"updated_utc\":\"2026-07-20T00:00:00Z\"}";
            File.WriteAllText(ledger, new string('x', MaxLineBytes + 1) + "\n" + validRow + "\n", SharedEncoding.Utf8NoBom);
            SpecBoardSnapshot oversizedLine = Read(ledger, false, CancellationToken.None);
            if (oversizedLine.Rows.Count != 1 || oversizedLine.MalformedLines < 1)
            {
                throw new InvalidOperationException("Spec Board ledger line-length limit self-test failed.");
            }

            File.WriteAllText(ledger, new string('x', MaxFileBytes + 1), SharedEncoding.Utf8NoBom);
            SpecBoardSnapshot oversizedLedger = Read(ledger, false, CancellationToken.None);
            if (oversizedLedger.Rows.Count != 0 || oversizedLedger.MalformedLines < 2)
            {
                throw new InvalidOperationException("Spec Board ledger file-size limit self-test failed.");
            }

            File.WriteAllText(projectsPath, new string(' ', MaxFileBytes + 1), SharedEncoding.Utf8NoBom);
            File.WriteAllText(ledger, validRow + "\n", SharedEncoding.Utf8NoBom);
            SpecBoardSnapshot oversizedProjects = Read(ledger, false, CancellationToken.None);
            if (oversizedProjects.ProjectRegistryAvailable || oversizedProjects.MalformedLines < 2)
            {
                throw new InvalidOperationException("Spec Board project registry file-size limit self-test failed.");
            }

            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool canceled = false;
                try
                {
                    Read(ledger, true, cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                if (!canceled)
                {
                    throw new InvalidOperationException("Spec Board cancellation self-test failed.");
                }
            }

            elapsed.Stop();
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(30))
            {
                throw new InvalidOperationException("Spec Board bounded-read self-test exceeded 30 seconds.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
