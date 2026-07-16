using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

            try
            {
                return Path.GetFullPath(Path.Combine(this.ProjectRoot, this.SpecPath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                return string.Empty;
            }
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

    public static SpecBoardSnapshot Read(string ledgerPath, bool reconcile)
    {
        SpecBoardSnapshot snapshot = new SpecBoardSnapshot();
        snapshot.LedgerPath = ledgerPath ?? string.Empty;
        snapshot.ScanTimeUtc = DateTime.UtcNow;
        Dictionary<string, SpecBoardProject> projectsByName = LoadProjects(snapshot, GetProjectsPath(ledgerPath));
        LoadLedger(snapshot, ledgerPath, projectsByName);
        AppendLedgerOnlyProjects(snapshot, projectsByName);
        if (reconcile && !snapshot.LedgerMissing && snapshot.ProjectRegistryAvailable)
        {
            Reconcile(snapshot, projectsByName);
        }

        return snapshot;
    }

    private static Dictionary<string, SpecBoardProject> LoadProjects(SpecBoardSnapshot snapshot, string projectsPath)
    {
        Dictionary<string, SpecBoardProject> result = new Dictionary<string, SpecBoardProject>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (string.IsNullOrEmpty(projectsPath) || !File.Exists(projectsPath))
            {
                snapshot.ProjectRegistryAvailable = false;
                return result;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> root = serializer.DeserializeObject(File.ReadAllText(projectsPath, SharedEncoding.Utf8NoBom)) as Dictionary<string, object>;
            object projectsValue;
            object[] projects = root != null && root.TryGetValue("projects", out projectsValue) ? projectsValue as object[] : null;
            if (projects == null)
            {
                snapshot.ProjectRegistryAvailable = false;
                return result;
            }

            for (int i = 0; i < projects.Length; i++)
            {
                Dictionary<string, object> value = projects[i] as Dictionary<string, object>;
                string name = ReadString(value, "name");
                string display = ReadString(value, "display");
                string rootPath = ReadString(value, "root");
                string specGlob = ReadString(value, "spec_glob");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(specGlob))
                {
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
        catch
        {
            snapshot.ProjectRegistryAvailable = false;
            snapshot.Projects.Clear();
            result.Clear();
        }

        return result;
    }

    private static void LoadLedger(SpecBoardSnapshot snapshot, string ledgerPath, Dictionary<string, SpecBoardProject> projectsByName)
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
            string[] lines = File.ReadAllLines(ledgerPath, SharedEncoding.Utf8NoBom);
            for (int i = 0; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                try
                {
                    Dictionary<string, object> value = serializer.DeserializeObject(lines[i]) as Dictionary<string, object>;
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

    private static void AppendLedgerOnlyProjects(SpecBoardSnapshot snapshot, Dictionary<string, SpecBoardProject> projectsByName)
    {
        foreach (SpecBoardRow row in snapshot.Rows)
        {
            SpecBoardProject project;
            if (projectsByName.TryGetValue(row.Project, out project))
            {
                row.ProjectRoot = project.Root;
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

    private static void Reconcile(SpecBoardSnapshot snapshot, Dictionary<string, SpecBoardProject> projectsByName)
    {
        foreach (SpecBoardProject project in snapshot.Projects)
        {
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
            string[] files = Directory.GetFiles(globDirectory, pattern, SearchOption.TopDirectoryOnly);
            for (int i = 0; i < files.Length; i++)
            {
                string fileName = Path.GetFileName(files[i]);
                if (fileName.IndexOf("GoalSpec", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                string relativePath = GetRelativePath(project.Root, files[i]);
                if (!ledgerPaths.Contains(relativePath))
                {
                    snapshot.Rows.Add(new SpecBoardRow
                    {
                        Id = "unregistered:" + project.Name + ":" + relativePath,
                        Project = project.Name,
                        ProjectRoot = project.Root,
                        SpecPath = relativePath,
                        Title = Path.GetFileNameWithoutExtension(files[i]),
                        Status = SpecBoardStatus.Unregistered,
                        EventTimeUtc = File.GetLastWriteTimeUtc(files[i]),
                        IsUnregistered = true
                    });
                }
            }

            foreach (SpecBoardRow row in snapshot.Rows)
            {
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

    internal static void RunSelfTest()
    {
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
}
