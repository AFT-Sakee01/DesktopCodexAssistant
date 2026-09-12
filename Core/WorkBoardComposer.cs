using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;

// Composition layer for the merged Work Board (displayed title WORKBENCH), which folds the Spec
// Board's cross-project ledger view and the Codex Task board's live session view into one surface.
//
// This file is PURE: it takes two already-materialised, cache-only snapshots and returns a view
// model. No file access, no timers, no UI types beyond Color. It never mutates its inputs. That is
// what lets the whole attribution and section-ordering contract be self-tested without constructing
// a window, and it is why the merge does not touch the Codex task backend at all -- the backend is
// owned by the headless CodexRadarForm and reaches us only through CodexTaskPresentation.
//
// ATTRIBUTION BOUNDARY: a session is attributed to a project only at PROJECT granularity, by cwd
// leaf name. The reader deliberately does not expose the full cwd, and nothing in a session tells us
// which spec it is working on, so this file must never claim a session-to-spec relationship. The
// optional P3 hint is a separate, explicitly-marked textual guess and is not produced here.

internal sealed class WorkBoardProjectRow
{
    // Null Name marks the synthetic "unattributed" rail row, which owns no ledger rows and is never
    // written to SpecBoardSeenState.json.
    public string Name;
    public string Display;
    public bool IsUnattributed;
    public int RedCount;
    public int RevisionCount;
    public int AwaitingCount;
    public int LiveCount;

    public bool HasWork
    {
        get { return this.RedCount > 0 || this.RevisionCount > 0 || this.AwaitingCount > 0; }
    }
}

internal sealed class WorkBoardLiveRow
{
    // The row model is produced by CodexTaskPresentation so status text, status colour, context bar
    // colour and attention semantics have exactly one implementation in the process.
    public CodexTaskRowModel Task;
    public string ProjectName;
    // Opportunistic, explicitly-marked guess only: the session title textually matched a spec in the
    // same project. Never authoritative, never written to the ledger, never used for grouping.
    public string SpecHintTitle;
    public int SpecHintOverflow;

    public bool IsUnattributed
    {
        get { return string.IsNullOrEmpty(this.ProjectName); }
    }

    public bool HasSpecHint
    {
        get { return !string.IsNullOrEmpty(this.SpecHintTitle); }
    }
}

internal sealed class WorkBoardSection
{
    public string Status;
    public string Label;
    public Color Color;
    public readonly List<SpecBoardRow> Rows = new List<SpecBoardRow>();

    public int Count
    {
        get { return this.Rows.Count; }
    }

    public bool IsEmpty
    {
        get { return this.Rows.Count == 0; }
    }
}

internal sealed class WorkBoardDiagnostics
{
    public int UnattributedSessions;
    public int AmbiguousLeafCount;
    public readonly List<string> AmbiguousLeaves = new List<string>();

    public bool HasAmbiguity
    {
        get { return this.AmbiguousLeafCount > 0; }
    }

    // Deliberately body-free: leaf names are user folder names, so the summary carries the count and
    // a bounded list of keys, never session titles or paths.
    public string BuildSummary()
    {
        return "WorkBoard attribution: ambiguous leaf names=" +
            this.AmbiguousLeafCount.ToString(CultureInfo.InvariantCulture) +
            ", keys=" + string.Join(",", this.AmbiguousLeaves.ToArray()) +
            ", unattributed sessions=" + this.UnattributedSessions.ToString(CultureInfo.InvariantCulture);
    }
}

// Filter is a struct rather than a magic string so "all", "one project" and "unattributed" cannot
// collide with a real project name coming out of user-authored JSON.
internal struct WorkBoardFilter
{
    private readonly string projectName;
    private readonly bool unattributed;

    private WorkBoardFilter(string projectName, bool unattributed)
    {
        this.projectName = projectName;
        this.unattributed = unattributed;
    }

    public static WorkBoardFilter All
    {
        get { return new WorkBoardFilter(null, false); }
    }

    public static WorkBoardFilter Unattributed
    {
        get { return new WorkBoardFilter(null, true); }
    }

    public static WorkBoardFilter ForProject(string name)
    {
        return string.IsNullOrEmpty(name) ? All : new WorkBoardFilter(name, false);
    }

    public bool IsAll
    {
        get { return !this.unattributed && string.IsNullOrEmpty(this.projectName); }
    }

    public bool IsUnattributed
    {
        get { return this.unattributed; }
    }

    public string ProjectName
    {
        get { return this.projectName; }
    }

    public bool MatchesProject(string name)
    {
        if (this.IsAll)
        {
            return true;
        }

        return !this.unattributed && string.Equals(this.projectName, name, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class WorkBoardLimits
{
    public int MaxLiveRows = 10;
    public int MaxAmbiguousLeavesLogged = 8;
    // P3 hint. Off means the composer does no title matching at all, not merely that the hint is
    // hidden, so the gate genuinely removes the behaviour.
    public bool SpecSessionHintEnabled = true;

    public static WorkBoardLimits Default
    {
        get { return new WorkBoardLimits(); }
    }
}

internal sealed class WorkBoardModel
{
    public readonly List<WorkBoardProjectRow> ProjectRows = new List<WorkBoardProjectRow>();
    public readonly List<WorkBoardLiveRow> LiveRows = new List<WorkBoardLiveRow>();
    public readonly List<WorkBoardSection> SpecSections = new List<WorkBoardSection>();
    public WorkBoardDiagnostics Diagnostics = new WorkBoardDiagnostics();

    // Header counts, already reduced by the active filter.
    public int RedCount;
    public int RevisionCount;
    public int AwaitingCount;
    public int LiveCount;

    public int DoneCount;
    public int AbandonedCount;
    public bool ProjectRegistryAvailable;
}

internal static class WorkBoardComposer
{
    // Section order is fixed and deliberate: the live band is the most volatile information on the
    // board, so it sits first; the ledger sections then follow the spec lifecycle.
    internal static readonly string[] SectionOrder =
    {
        SpecBoardStatus.Unregistered,
        SpecBoardStatus.Pending,
        SpecBoardStatus.NeedsRevision,
        SpecBoardStatus.AwaitingVerify
    };

    internal static WorkBoardModel Compose(
        SpecBoardSnapshot spec,
        CodexTaskMonitorSnapshot tasks,
        WorkBoardFilter filter,
        DateTime nowLocal,
        WorkBoardLimits limits)
    {
        WorkBoardModel model = new WorkBoardModel();
        limits = limits ?? WorkBoardLimits.Default;
        List<SpecBoardProject> projects = spec != null
            ? new List<SpecBoardProject>(spec.Projects)
            : new List<SpecBoardProject>();
        List<SpecBoardRow> rows = spec != null
            ? new List<SpecBoardRow>(spec.Rows)
            : new List<SpecBoardRow>();
        model.ProjectRegistryAvailable = spec != null && spec.ProjectRegistryAvailable;

        AttributionLookup lookup = AttributionLookup.Build(projects, limits, model.Diagnostics);
        List<WorkBoardLiveRow> allLive = BuildLiveRows(tasks, lookup, nowLocal, limits, model.Diagnostics);
        if (limits.SpecSessionHintEnabled)
        {
            ApplySpecHints(allLive, rows);
        }

        BuildProjectRows(model, projects, rows, allLive);
        BuildLiveSection(model, allLive, filter);
        BuildSpecSections(model, rows, filter);
        BuildHeaderCounts(model, rows, filter);

        model.DoneCount = CountStatus(rows, SpecBoardStatus.Done);
        model.AbandonedCount = CountStatus(rows, SpecBoardStatus.Abandoned);
        return model;
    }

    // ---- attribution -------------------------------------------------------

    internal static string ResolveProjectName(string workspaceLeaf, IList<SpecBoardProject> projects)
    {
        AttributionLookup lookup = AttributionLookup.Build(
            projects, WorkBoardLimits.Default, new WorkBoardDiagnostics());
        return lookup.Resolve(workspaceLeaf);
    }

    internal static string ExtractRootLeaf(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return string.Empty;
        }

        try
        {
            string trimmed = root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }

            string leaf = Path.GetFileName(trimmed);
            // A bare drive root ("D:") has no file name; it is not a usable attribution key.
            return leaf ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    // Three tiers probed in order: root leaf, then project name, then declared aliases. Within a
    // tier, the registry order wins a collision -- a later project never steals an earlier one's
    // key, and never steals a key from a stronger tier either.
    private sealed class AttributionLookup
    {
        private readonly Dictionary<string, string> byRootLeaf =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> byName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> byAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static AttributionLookup Build(
            IList<SpecBoardProject> projects,
            WorkBoardLimits limits,
            WorkBoardDiagnostics diagnostics)
        {
            AttributionLookup lookup = new AttributionLookup();
            if (projects == null)
            {
                return lookup;
            }

            for (int i = 0; i < projects.Count; i++)
            {
                SpecBoardProject project = projects[i];
                if (project == null || string.IsNullOrWhiteSpace(project.Name))
                {
                    continue;
                }

                lookup.Add(lookup.byRootLeaf, ExtractRootLeaf(project.Root), project.Name, limits, diagnostics);
                lookup.Add(lookup.byName, project.Name, project.Name, limits, diagnostics);
                for (int a = 0; a < project.WorkspaceAliases.Count; a++)
                {
                    lookup.Add(lookup.byAlias, project.WorkspaceAliases[a], project.Name, limits, diagnostics);
                }
            }

            return lookup;
        }

        private void Add(
            Dictionary<string, string> target,
            string key,
            string projectName,
            WorkBoardLimits limits,
            WorkBoardDiagnostics diagnostics)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            key = key.Trim();
            string existing;
            if (target.TryGetValue(key, out existing))
            {
                // Same project registering the same spelling twice (name == root leaf, the common
                // case) is not an ambiguity.
                if (!string.Equals(existing, projectName, StringComparison.OrdinalIgnoreCase))
                {
                    RecordAmbiguity(key, limits, diagnostics);
                }

                return;
            }

            target.Add(key, projectName);
        }

        private static void RecordAmbiguity(string key, WorkBoardLimits limits, WorkBoardDiagnostics diagnostics)
        {
            if (diagnostics == null)
            {
                return;
            }

            diagnostics.AmbiguousLeafCount++;
            if (diagnostics.AmbiguousLeaves.Count < Math.Max(1, limits.MaxAmbiguousLeavesLogged) &&
                !diagnostics.AmbiguousLeaves.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                diagnostics.AmbiguousLeaves.Add(key);
            }
        }

        public string Resolve(string workspaceLeaf)
        {
            if (string.IsNullOrWhiteSpace(workspaceLeaf))
            {
                return null;
            }

            string key = workspaceLeaf.Trim();
            string project;
            if (this.byRootLeaf.TryGetValue(key, out project) ||
                this.byName.TryGetValue(key, out project) ||
                this.byAlias.TryGetValue(key, out project))
            {
                return project;
            }

            return null;
        }
    }

    // ---- live rows ---------------------------------------------------------

    private static List<WorkBoardLiveRow> BuildLiveRows(
        CodexTaskMonitorSnapshot tasks,
        AttributionLookup lookup,
        DateTime nowLocal,
        WorkBoardLimits limits,
        WorkBoardDiagnostics diagnostics)
    {
        List<WorkBoardLiveRow> live = new List<WorkBoardLiveRow>();
        if (tasks == null || tasks.Tasks == null || tasks.Tasks.Count == 0)
        {
            return live;
        }

        // BuildRows owns urgency ordering and every presentation string; we only attach attribution.
        IList<CodexTaskRowModel> rows = CodexTaskPresentation.BuildRows(
            tasks, nowLocal, Math.Max(1, limits.MaxLiveRows));
        for (int i = 0; i < rows.Count; i++)
        {
            CodexTaskRowModel row = rows[i];
            string project = lookup.Resolve(row.WorkspaceLeaf);
            if (project == null)
            {
                diagnostics.UnattributedSessions++;
            }

            live.Add(new WorkBoardLiveRow { Task = row, ProjectName = project });
        }

        return live;
    }

    // P3: purely textual, same-project-only guess at which spec a session might be working on.
    //
    // The backend cannot tell us this -- it never parses session content -- so the match is made on
    // the official session title alone, and the caller must render it behind an explicit "≈" marker.
    // Cross-project matching is deliberately impossible: a session with no attribution gets no hint,
    // which keeps the honest "we only know project granularity" boundary intact.
    internal static void ApplySpecHints(List<WorkBoardLiveRow> live, List<SpecBoardRow> rows)
    {
        for (int i = 0; i < live.Count; i++)
        {
            WorkBoardLiveRow row = live[i];
            if (row.IsUnattributed || row.Task == null || string.IsNullOrEmpty(row.Task.Title))
            {
                continue;
            }

            string title = row.Task.Title;
            SpecBoardRow best = null;
            int matches = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                SpecBoardRow spec = rows[r];
                if (spec == null ||
                    !string.Equals(spec.Project, row.ProjectName, StringComparison.OrdinalIgnoreCase) ||
                    !IsHintMatch(title, spec))
                {
                    continue;
                }

                matches++;
                if (best == null || Later(spec) > Later(best))
                {
                    best = spec;
                }
            }

            if (best != null)
            {
                row.SpecHintTitle = string.IsNullOrEmpty(best.Title) ? best.SpecPath : best.Title;
                row.SpecHintOverflow = matches - 1;
            }
        }
    }

    private static DateTime Later(SpecBoardRow row)
    {
        return row.UpdatedUtc ?? row.EventTimeUtc ?? DateTime.MinValue;
    }

    private static bool IsHintMatch(string sessionTitle, SpecBoardRow spec)
    {
        if (!string.IsNullOrEmpty(spec.Title) && spec.Title.Length >= 4 &&
            sessionTitle.IndexOf(spec.Title, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return true;
        }

        string stem = ExtractSpecStem(spec.SpecPath);
        return stem.Length >= 4 && sessionTitle.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // "Docs/Technical/Opus5-WorkBoardMerge-SPEC-v2.0.0.51-20260912-133632.md" -> "WorkBoardMerge".
    // The version and timestamp are never typed into a session title, and the model prefix is noise,
    // so only the subject segment is a usable needle.
    internal static string ExtractSpecStem(string specPath)
    {
        if (string.IsNullOrEmpty(specPath))
        {
            return string.Empty;
        }

        string name = specPath.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name.Substring(slash + 1);
        }

        int marker = name.IndexOf("-SPEC-", StringComparison.OrdinalIgnoreCase);
        if (marker <= 0)
        {
            return string.Empty;
        }

        name = name.Substring(0, marker);
        int dash = name.IndexOf('-');
        return dash >= 0 && dash + 1 < name.Length ? name.Substring(dash + 1) : name;
    }

    private static void BuildLiveSection(WorkBoardModel model, List<WorkBoardLiveRow> allLive, WorkBoardFilter filter)
    {
        for (int i = 0; i < allLive.Count; i++)
        {
            WorkBoardLiveRow row = allLive[i];
            bool include = filter.IsAll ||
                (filter.IsUnattributed && row.IsUnattributed) ||
                (!filter.IsUnattributed && filter.MatchesProject(row.ProjectName));
            if (include)
            {
                model.LiveRows.Add(row);
            }
        }

        model.LiveCount = model.LiveRows.Count;
    }

    // ---- rail --------------------------------------------------------------

    private static void BuildProjectRows(
        WorkBoardModel model,
        List<SpecBoardProject> projects,
        List<SpecBoardRow> rows,
        List<WorkBoardLiveRow> allLive)
    {
        for (int i = 0; i < projects.Count; i++)
        {
            SpecBoardProject project = projects[i];
            if (project == null || string.IsNullOrWhiteSpace(project.Name))
            {
                continue;
            }

            WorkBoardProjectRow row = new WorkBoardProjectRow
            {
                Name = project.Name,
                Display = string.IsNullOrWhiteSpace(project.Display) ? project.Name : project.Display,
                IsUnattributed = false,
                RedCount = CountStatus(rows, project.Name, SpecBoardStatus.Unregistered) +
                           CountStatus(rows, project.Name, SpecBoardStatus.Pending),
                RevisionCount = CountStatus(rows, project.Name, SpecBoardStatus.NeedsRevision),
                AwaitingCount = CountStatus(rows, project.Name, SpecBoardStatus.AwaitingVerify),
                LiveCount = allLive.Count(candidate =>
                    string.Equals(candidate.ProjectName, project.Name, StringComparison.OrdinalIgnoreCase))
            };
            model.ProjectRows.Add(row);
        }

        // The unattributed pseudo row exists only while something is actually unattributed; it never
        // carries ledger counts because a session with no project has no spec rows to show.
        int orphans = allLive.Count(candidate => candidate.IsUnattributed);
        if (orphans > 0)
        {
            model.ProjectRows.Add(new WorkBoardProjectRow
            {
                Name = null,
                Display = "未归属",
                IsUnattributed = true,
                LiveCount = orphans
            });
        }
    }

    // ---- sections ----------------------------------------------------------

    private static void BuildSpecSections(WorkBoardModel model, List<SpecBoardRow> rows, WorkBoardFilter filter)
    {
        for (int i = 0; i < SectionOrder.Length; i++)
        {
            string status = SectionOrder[i];
            WorkBoardSection section = new WorkBoardSection
            {
                Status = status,
                Label = GetSectionLabel(status),
                Color = GetSectionColor(status)
            };

            // "Unattributed" selects sessions with no project, so by construction it selects no
            // ledger rows at all; every section stays present but empty.
            if (!filter.IsUnattributed)
            {
                section.Rows.AddRange(rows
                    .Where(row => row != null &&
                        string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase) &&
                        filter.MatchesProject(row.Project))
                    // Matches SpecBoardForm.DrawSection exactly: oldest event first, unknown last.
                    // Parity here is what keeps the merged board's counts and ordering identical to
                    // the pre-merge Spec Board.
                    .OrderBy(row => row.EventTimeUtc ?? DateTime.MaxValue));
            }

            model.SpecSections.Add(section);
        }
    }

    // Board section headings, deliberately separate from SpecBoardStatus.DisplayName: the manager
    // window labels a row's state ("未执行"), the board labels the action the section asks for
    // ("需要执行"). These are the wordings the pre-merge board used, kept so the merge does not
    // silently rename the sections users already read.
    internal static string GetSectionLabel(string status)
    {
        if (string.Equals(status, SpecBoardStatus.Unregistered, StringComparison.OrdinalIgnoreCase))
        {
            return "未登记";
        }

        if (string.Equals(status, SpecBoardStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return "需要执行";
        }

        if (string.Equals(status, SpecBoardStatus.NeedsRevision, StringComparison.OrdinalIgnoreCase))
        {
            return "需要修改";
        }

        if (string.Equals(status, SpecBoardStatus.AwaitingVerify, StringComparison.OrdinalIgnoreCase))
        {
            return "等待验证";
        }

        return SpecBoardStatus.DisplayName(status);
    }

    internal static Color GetSectionColor(string status)
    {
        if (string.Equals(status, SpecBoardStatus.Unregistered, StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.Colors.WarningDeep;
        }

        if (string.Equals(status, SpecBoardStatus.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.Colors.Danger;
        }

        if (string.Equals(status, SpecBoardStatus.NeedsRevision, StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.Colors.AccentAlt;
        }

        if (string.Equals(status, SpecBoardStatus.AwaitingVerify, StringComparison.OrdinalIgnoreCase))
        {
            return DesignTokens.Colors.Warning;
        }

        return DesignTokens.Colors.GlyphMuted;
    }

    private static void BuildHeaderCounts(WorkBoardModel model, List<SpecBoardRow> rows, WorkBoardFilter filter)
    {
        if (filter.IsUnattributed)
        {
            return;
        }

        model.RedCount = CountStatus(rows, filter, SpecBoardStatus.Unregistered) +
                         CountStatus(rows, filter, SpecBoardStatus.Pending);
        model.RevisionCount = CountStatus(rows, filter, SpecBoardStatus.NeedsRevision);
        model.AwaitingCount = CountStatus(rows, filter, SpecBoardStatus.AwaitingVerify);
    }

    private static int CountStatus(List<SpecBoardRow> rows, WorkBoardFilter filter, string status)
    {
        return rows.Count(row => row != null &&
            string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase) &&
            filter.MatchesProject(row.Project));
    }

    private static int CountStatus(List<SpecBoardRow> rows, string project, string status)
    {
        return rows.Count(row => row != null &&
            string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountStatus(List<SpecBoardRow> rows, string status)
    {
        return rows.Count(row => row != null &&
            string.Equals(row.Status, status, StringComparison.OrdinalIgnoreCase));
    }

    // ---- self test ---------------------------------------------------------

    internal static void RunSelfTest()
    {
        RunAttributionSelfTest();
        RunFilterSelfTest();
        RunSectionSelfTest();
        RunDegenerateInputSelfTest();
        RunInputImmutabilitySelfTest();
        RunSpecHintSelfTest();
        Console.WriteLine("Work Board composition policy: PASS");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("WorkBoardComposer self-test failed: " + message);
        }
    }

    private static SpecBoardProject Project(string name, string root, params string[] aliases)
    {
        SpecBoardProject project = new SpecBoardProject
        {
            Name = name,
            Display = name,
            Root = root,
            SpecGlob = "Docs/Technical/*-SPEC-*.md"
        };
        if (aliases != null)
        {
            project.WorkspaceAliases.AddRange(aliases);
        }

        return project;
    }

    private static SpecBoardRow Row(string project, string status, int ageDays)
    {
        return new SpecBoardRow
        {
            Id = project + "." + status + "." + ageDays.ToString(CultureInfo.InvariantCulture),
            Project = project,
            ProjectRoot = @"D:\Demo\" + project,
            SpecPath = "Docs/Technical/Demo-SPEC-v1.md",
            Title = status + " " + ageDays.ToString(CultureInfo.InvariantCulture),
            Status = status,
            EventTimeUtc = DateTime.UtcNow.AddDays(-ageDays),
            IsUnregistered = string.Equals(status, SpecBoardStatus.Unregistered, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static CodexTaskSnapshot Task(int number, string leaf, CodexTaskStatus status)
    {
        DateTime now = DateTime.Now;
        return new CodexTaskSnapshot(
            "rollout:" + number.ToString(CultureInfo.InvariantCulture),
            number,
            leaf,
            "gpt-6-astra",
            status,
            now.AddMinutes(-30),
            now.AddMinutes(-1),
            null,
            null,
            false,
            CodexTaskTokenUsage.Empty,
            CodexTaskTokenUsage.Empty,
            25.0,
            "demo " + leaf);
    }

    private static SpecBoardSnapshot Snapshot(IEnumerable<SpecBoardProject> projects, IEnumerable<SpecBoardRow> rows)
    {
        SpecBoardSnapshot snapshot = new SpecBoardSnapshot();
        snapshot.ProjectRegistryAvailable = true;
        snapshot.ScanTimeUtc = DateTime.UtcNow;
        if (projects != null)
        {
            snapshot.Projects.AddRange(projects);
        }

        if (rows != null)
        {
            snapshot.Rows.AddRange(rows);
        }

        return snapshot;
    }

    private static CodexTaskMonitorSnapshot Tasks(params CodexTaskSnapshot[] tasks)
    {
        return new CodexTaskMonitorSnapshot(tasks, tasks == null ? 0 : tasks.Length, DateTime.Now);
    }

    private static void RunAttributionSelfTest()
    {
        List<SpecBoardProject> projects = new List<SpecBoardProject>
        {
            Project("DesktopCodexAssistant", @"D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant"),
            Project("WSLmanager", @"D:\E_Drive_Files\Codexproject\WSLmanager"),
            Project("BunkyoUNV", @"C:\Users\Demo\obsidianDB\BunkyoUNV\根本俊男教授课程库", "BunkyoUNV")
        };

        Assert(ExtractRootLeaf(@"D:\a\b\DesktopCodexAssistant") == "DesktopCodexAssistant",
            "root leaf extraction");
        Assert(ExtractRootLeaf(@"D:\a\b\Trailing\") == "Trailing", "root leaf ignores trailing separator");
        Assert(ExtractRootLeaf(null) == string.Empty, "null root yields empty leaf");

        Assert(ResolveProjectName("DesktopCodexAssistant", projects) == "DesktopCodexAssistant",
            "root leaf hit");
        Assert(ResolveProjectName("desktopcodexassistant", projects) == "DesktopCodexAssistant",
            "root leaf hit is case insensitive");
        Assert(ResolveProjectName("根本俊男教授课程库", projects) == "BunkyoUNV",
            "non-ASCII root leaf hit");
        Assert(ResolveProjectName("BunkyoUNV", projects) == "BunkyoUNV",
            "project name hit when root leaf differs");
        Assert(ResolveProjectName("qiyangtracker-x64", projects) == null,
            "unknown leaf falls through to unattributed");
        Assert(ResolveProjectName(null, projects) == null, "null leaf is unattributed");
        Assert(ResolveProjectName("   ", projects) == null, "blank leaf is unattributed");

        // Alias tier: only reached when neither root leaf nor project name matched.
        List<SpecBoardProject> aliased = new List<SpecBoardProject>
        {
            Project("BunkyoUNV", @"C:\Users\Demo\BunkyoUNV\根本俊男教授课程库", "Bunkyo", "  ", "bunkyo")
        };
        Assert(ResolveProjectName("Bunkyo", aliased) == "BunkyoUNV", "alias hit");
        Assert(ResolveProjectName("BUNKYO", aliased) == "BunkyoUNV", "alias hit is case insensitive");
        // A blank entry that slipped through must not become a lookup key, and must not stop the
        // entries after it from registering. (Reader-level filtering is covered by
        // SpecBoardReader.RunSelfTest; this asserts the composer is defensive on its own.)
        Assert(ResolveProjectName("  ", aliased) == null, "blank alias never becomes a lookup key");
        Assert(ResolveProjectName("bunkyo", aliased) == "BunkyoUNV",
            "an alias after a blank entry still registers");

        // Missing / empty alias list must not disturb anything else.
        List<SpecBoardProject> noAliases = new List<SpecBoardProject>
        {
            Project("Alpha", @"D:\x\Alpha"),
            Project("Beta", @"D:\x\Beta")
        };
        Assert(ResolveProjectName("Beta", noAliases) == "Beta", "empty alias list does not break resolution");

        // Leaf collision: registry order wins, exactly one ambiguity recorded, resolution still works.
        List<SpecBoardProject> colliding = new List<SpecBoardProject>
        {
            Project("First", @"D:\one\Shared"),
            Project("Second", @"D:\two\Shared")
        };
        WorkBoardDiagnostics diagnostics = new WorkBoardDiagnostics();
        AttributionLookup lookup = AttributionLookup.Build(colliding, WorkBoardLimits.Default, diagnostics);
        Assert(lookup.Resolve("Shared") == "First", "leaf collision resolves to registry order");
        Assert(diagnostics.AmbiguousLeafCount == 1, "leaf collision records exactly one ambiguity");
        Assert(diagnostics.AmbiguousLeaves.Count == 1 && diagnostics.AmbiguousLeaves[0] == "Shared",
            "ambiguity records the colliding key");
        Assert(diagnostics.BuildSummary().IndexOf("Shared", StringComparison.Ordinal) >= 0,
            "diagnostic summary names the key");

        // The overwhelmingly common case -- project name equal to root leaf -- is not an ambiguity.
        WorkBoardDiagnostics clean = new WorkBoardDiagnostics();
        AttributionLookup.Build(projects, WorkBoardLimits.Default, clean);
        Assert(clean.AmbiguousLeafCount == 0, "name equal to root leaf is not ambiguous");
    }

    private static void RunFilterSelfTest()
    {
        List<SpecBoardProject> projects = new List<SpecBoardProject>
        {
            Project("Alpha", @"D:\x\Alpha"),
            Project("Beta", @"D:\x\Beta")
        };
        List<SpecBoardRow> rows = new List<SpecBoardRow>
        {
            Row("Alpha", SpecBoardStatus.Unregistered, 10),
            Row("Alpha", SpecBoardStatus.Pending, 5),
            Row("Alpha", SpecBoardStatus.AwaitingVerify, 3),
            Row("Beta", SpecBoardStatus.Unregistered, 7),
            Row("Alpha", SpecBoardStatus.Done, 1),
            Row("Beta", SpecBoardStatus.Abandoned, 2)
        };
        CodexTaskMonitorSnapshot tasks = Tasks(
            Task(1, "Alpha", CodexTaskStatus.Active),
            Task(2, "Beta", CodexTaskStatus.Idle),
            Task(3, "qiyangtracker-x64", CodexTaskStatus.Idle));
        SpecBoardSnapshot spec = Snapshot(projects, rows);
        DateTime now = DateTime.Now;

        WorkBoardModel all = Compose(spec, tasks, WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(all.LiveRows.Count == 3, "all filter keeps every session");
        Assert(all.RedCount == 3 && all.RevisionCount == 0 && all.AwaitingCount == 1,
            "all filter header counts");
        Assert(all.DoneCount == 1 && all.AbandonedCount == 1, "terminal counts are unfiltered totals");
        Assert(all.ProjectRows.Count == 3, "rail lists both projects plus the unattributed row");
        Assert(all.ProjectRows[2].IsUnattributed && all.ProjectRows[2].LiveCount == 1,
            "unattributed rail row carries the orphan session");
        Assert(all.ProjectRows[0].LiveCount == 1 && all.ProjectRows[0].RedCount == 2 &&
            all.ProjectRows[0].AwaitingCount == 1, "Alpha rail counts");
        Assert(all.ProjectRows[1].HasWork, "Beta has ledger work");
        Assert(all.Diagnostics.UnattributedSessions == 1, "one unattributed session counted");

        WorkBoardModel alpha = Compose(spec, tasks, WorkBoardFilter.ForProject("Alpha"), now, WorkBoardLimits.Default);
        Assert(alpha.LiveRows.Count == 1 && alpha.LiveRows[0].ProjectName == "Alpha",
            "project filter narrows the live band");
        Assert(alpha.RedCount == 2 && alpha.AwaitingCount == 1, "project filter narrows header counts");
        Assert(alpha.SpecSections.Sum(section => section.Count) == 3,
            "project filter narrows the ledger sections");

        // The rail filter applies to BOTH halves -- that is the whole point of the merge.
        WorkBoardModel orphan = Compose(spec, tasks, WorkBoardFilter.Unattributed, now, WorkBoardLimits.Default);
        Assert(orphan.LiveRows.Count == 1 && orphan.LiveRows[0].IsUnattributed,
            "unattributed filter keeps only orphan sessions");
        Assert(orphan.SpecSections.Count == SectionOrder.Length,
            "unattributed filter still emits every section");
        Assert(orphan.SpecSections.All(section => section.IsEmpty),
            "unattributed filter yields no ledger rows");
        Assert(orphan.RedCount == 0 && orphan.RevisionCount == 0 && orphan.AwaitingCount == 0,
            "unattributed filter zeroes the spec header counts");

        Assert(WorkBoardFilter.ForProject(null).IsAll, "null project name degrades to the all filter");
        Assert(!WorkBoardFilter.All.IsUnattributed, "all filter is not the unattributed filter");
    }

    private static void RunSectionSelfTest()
    {
        List<SpecBoardProject> projects = new List<SpecBoardProject> { Project("Alpha", @"D:\x\Alpha") };
        List<SpecBoardRow> rows = new List<SpecBoardRow>
        {
            Row("Alpha", SpecBoardStatus.Unregistered, 3),
            Row("Alpha", SpecBoardStatus.Unregistered, 30),
            Row("Alpha", SpecBoardStatus.Unregistered, 10),
            Row("Alpha", SpecBoardStatus.AwaitingVerify, 4)
        };
        WorkBoardModel model = Compose(
            Snapshot(projects, rows), Tasks(), WorkBoardFilter.All, DateTime.Now, WorkBoardLimits.Default);

        Assert(model.SpecSections.Count == 4, "four ledger sections");
        Assert(model.SpecSections[0].Status == SpecBoardStatus.Unregistered &&
            model.SpecSections[1].Status == SpecBoardStatus.Pending &&
            model.SpecSections[2].Status == SpecBoardStatus.NeedsRevision &&
            model.SpecSections[3].Status == SpecBoardStatus.AwaitingVerify,
            "section order is unregistered, pending, needs_revision, awaiting_verify");
        Assert(model.SpecSections[1].IsEmpty && model.SpecSections[2].IsEmpty,
            "sections with no rows report empty rather than being dropped");

        // Oldest event first, matching SpecBoardForm.DrawSection so the merged board renders the
        // same order the Spec Board does today.
        List<SpecBoardRow> unregistered = model.SpecSections[0].Rows;
        Assert(unregistered.Count == 3, "unregistered section keeps every row");
        Assert(unregistered[0].EventTimeUtc < unregistered[1].EventTimeUtc &&
            unregistered[1].EventTimeUtc < unregistered[2].EventTimeUtc,
            "section rows are ordered oldest event first");

        // Rows with no event time sort last, again matching the existing board.
        SpecBoardRow undated = Row("Alpha", SpecBoardStatus.Unregistered, 0);
        undated.EventTimeUtc = null;
        rows.Add(undated);
        WorkBoardModel withUndated = Compose(
            Snapshot(projects, rows), Tasks(), WorkBoardFilter.All, DateTime.Now, WorkBoardLimits.Default);
        List<SpecBoardRow> ordered = withUndated.SpecSections[0].Rows;
        Assert(ordered[ordered.Count - 1].EventTimeUtc == null, "undated rows sort last");

        Assert(GetSectionLabel(SpecBoardStatus.Unregistered) == "未登记" &&
            GetSectionLabel(SpecBoardStatus.Pending) == "需要执行" &&
            GetSectionLabel(SpecBoardStatus.NeedsRevision) == "需要修改" &&
            GetSectionLabel(SpecBoardStatus.AwaitingVerify) == "等待验证",
            "board section labels keep the pre-merge wording");

        Assert(GetSectionColor(SpecBoardStatus.Unregistered).ToArgb() == DesignTokens.Colors.WarningDeep.ToArgb() &&
            GetSectionColor(SpecBoardStatus.Pending).ToArgb() == DesignTokens.Colors.Danger.ToArgb() &&
            GetSectionColor(SpecBoardStatus.NeedsRevision).ToArgb() == DesignTokens.Colors.AccentAlt.ToArgb() &&
            GetSectionColor(SpecBoardStatus.AwaitingVerify).ToArgb() == DesignTokens.Colors.Warning.ToArgb(),
            "section colours come from the shared design tokens");
    }

    private static void RunDegenerateInputSelfTest()
    {
        List<SpecBoardProject> projects = new List<SpecBoardProject> { Project("Alpha", @"D:\x\Alpha") };
        DateTime now = DateTime.Now;

        WorkBoardModel noSpec = Compose(
            null, Tasks(Task(1, "Alpha", CodexTaskStatus.Active)), WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(noSpec.LiveRows.Count == 1, "null ledger snapshot still yields sessions");
        Assert(noSpec.LiveRows[0].IsUnattributed,
            "without a registry a session cannot be attributed and must not be guessed");
        Assert(noSpec.SpecSections.Count == SectionOrder.Length, "null ledger snapshot still emits sections");
        Assert(!noSpec.ProjectRegistryAvailable, "null ledger snapshot reports no registry");

        WorkBoardModel noTasks = Compose(
            Snapshot(projects, new List<SpecBoardRow> { Row("Alpha", SpecBoardStatus.Pending, 2) }),
            null, WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(noTasks.LiveRows.Count == 0 && noTasks.LiveCount == 0, "null task snapshot yields no sessions");
        Assert(noTasks.RedCount == 1, "null task snapshot leaves ledger counts intact");
        Assert(noTasks.ProjectRows.Count == 1 && !noTasks.ProjectRows[0].IsUnattributed,
            "no orphan session means no unattributed rail row");

        WorkBoardModel empty = Compose(null, null, WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(empty.LiveRows.Count == 0 && empty.ProjectRows.Count == 0, "fully empty input composes cleanly");
        Assert(empty.SpecSections.Count == SectionOrder.Length && empty.SpecSections.All(s => s.IsEmpty),
            "fully empty input still emits empty sections");
        Assert(empty.RedCount == 0 && empty.DoneCount == 0, "fully empty input has zero counts");

        WorkBoardModel nullLimits = Compose(
            Snapshot(projects, null), Tasks(Task(1, "Alpha", CodexTaskStatus.Active)),
            WorkBoardFilter.All, now, null);
        Assert(nullLimits.LiveRows.Count == 1, "null limits falls back to defaults");

        // The live band is bounded even when the backend reports an unusual number of sessions.
        CodexTaskSnapshot[] many = new CodexTaskSnapshot[20];
        for (int i = 0; i < many.Length; i++)
        {
            many[i] = Task(i + 1, "Alpha", CodexTaskStatus.Active);
        }

        WorkBoardModel bounded = Compose(
            Snapshot(projects, null), Tasks(many), WorkBoardFilter.All, now,
            new WorkBoardLimits { MaxLiveRows = 6 });
        Assert(bounded.LiveRows.Count == 6, "live rows honour the configured bound");
    }

    private static void RunSpecHintSelfTest()
    {
        Assert(ExtractSpecStem("Docs/Technical/Opus5-WorkBoardMerge-SPEC-v2.0.0.51-20260912-133632.md") == "WorkBoardMerge",
            "spec stem drops the model prefix, version and timestamp");
        Assert(ExtractSpecStem("Docs/Technical/NotASpec.md") == string.Empty, "non-spec file names yield no needle");
        Assert(ExtractSpecStem(null) == string.Empty, "null spec path yields no needle");

        List<SpecBoardProject> projects = new List<SpecBoardProject>
        {
            Project("Alpha", @"D:\x\Alpha"),
            Project("Beta", @"D:\x\Beta")
        };
        SpecBoardRow alphaSpec = Row("Alpha", SpecBoardStatus.Pending, 3);
        alphaSpec.Title = "左侧七停靠板 macOS 移植";
        alphaSpec.SpecPath = "Docs/Technical/Opus5-MacLeftBoardPort-SPEC-v1.md";
        SpecBoardRow betaSpec = Row("Beta", SpecBoardStatus.Pending, 4);
        betaSpec.Title = "GUI 三发行版矩阵";
        betaSpec.SpecPath = "Docs/Technical/Codex-WslGuiMatrix-SPEC-v1.md";
        List<SpecBoardRow> rows = new List<SpecBoardRow> { alphaSpec, betaSpec };
        SpecBoardSnapshot spec = Snapshot(projects, rows);
        DateTime now = DateTime.Now;

        // Exact title hit, inside the same project.
        CodexTaskSnapshot hit = Task(1, "Alpha", CodexTaskStatus.Active);
        WorkBoardModel model = Compose(spec, Tasks(WithTitle(hit, "继续 左侧七停靠板 macOS 移植 的 B 批")),
            WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(model.LiveRows[0].HasSpecHint && model.LiveRows[0].SpecHintTitle == "左侧七停靠板 macOS 移植",
            "session title containing a same-project spec title produces a hint");

        // File-stem hit with different casing.
        WorkBoardModel stemModel = Compose(spec, Tasks(WithTitle(hit, "run macleftboardport now")),
            WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(stemModel.LiveRows[0].HasSpecHint, "spec file stem matches case-insensitively");

        // Cross-project text must never match: the Beta spec title in an Alpha session.
        WorkBoardModel cross = Compose(spec, Tasks(WithTitle(hit, "GUI 三发行版矩阵")),
            WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(!cross.LiveRows[0].HasSpecHint, "a spec from another project must never be hinted");

        // Multiple hits in one project: newest wins and the rest are counted.
        SpecBoardRow second = Row("Alpha", SpecBoardStatus.AwaitingVerify, 1);
        second.Title = "左侧七停靠板 macOS 移植";
        second.SpecPath = "Docs/Technical/Opus5-MacLeftBoardPort2-SPEC-v1.md";
        second.UpdatedUtc = DateTime.UtcNow;
        alphaSpec.UpdatedUtc = DateTime.UtcNow.AddDays(-9);
        SpecBoardSnapshot twoSpec = Snapshot(projects, new List<SpecBoardRow> { alphaSpec, second });
        WorkBoardModel many = Compose(twoSpec, Tasks(WithTitle(hit, "左侧七停靠板 macOS 移植")),
            WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(many.LiveRows[0].SpecHintOverflow == 1, "extra same-project matches are counted, not listed");

        // Gate off means no matching happens at all.
        WorkBoardLimits off = WorkBoardLimits.Default;
        off.SpecSessionHintEnabled = false;
        WorkBoardModel gated = Compose(spec, Tasks(WithTitle(hit, "继续 左侧七停靠板 macOS 移植 的 B 批")),
            WorkBoardFilter.All, now, off);
        Assert(!gated.LiveRows[0].HasSpecHint, "disabling the gate removes the hint entirely");

        // An unattributed session has no project, so it can never be hinted.
        WorkBoardModel orphan = Compose(spec, Tasks(WithTitle(Task(2, "qiyangtracker-x64", CodexTaskStatus.Active), "左侧七停靠板 macOS 移植")),
            WorkBoardFilter.All, now, WorkBoardLimits.Default);
        Assert(!orphan.LiveRows[0].HasSpecHint, "an unattributed session never receives a spec hint");
    }

    private static CodexTaskSnapshot WithTitle(CodexTaskSnapshot task, string title)
    {
        return new CodexTaskSnapshot(
            task.FileKey, task.TaskNumber, task.WorkspaceLeaf, task.Model, task.Status,
            task.StartedAtLocal, task.LastEventLocal, task.TerminalStatus, task.TerminalAtLocal,
            task.TerminalSilent, task.LastTokenUsage, task.TotalTokenUsage, task.ContextPercent, title);
    }

    private static void RunInputImmutabilitySelfTest()
    {
        List<SpecBoardProject> projects = new List<SpecBoardProject> { Project("Alpha", @"D:\x\Alpha") };
        List<SpecBoardRow> rows = new List<SpecBoardRow>
        {
            Row("Alpha", SpecBoardStatus.Unregistered, 9),
            Row("Alpha", SpecBoardStatus.Pending, 2)
        };
        SpecBoardSnapshot spec = Snapshot(projects, rows);
        CodexTaskMonitorSnapshot tasks = Tasks(Task(1, "Alpha", CodexTaskStatus.Active));

        int rowCount = spec.Rows.Count;
        int projectCount = spec.Projects.Count;
        string firstRowStatus = spec.Rows[0].Status;
        int taskCount = tasks.Tasks.Count;
        int aliasCount = projects[0].WorkspaceAliases.Count;

        Compose(spec, tasks, WorkBoardFilter.All, DateTime.Now, WorkBoardLimits.Default);
        Compose(spec, tasks, WorkBoardFilter.ForProject("Alpha"), DateTime.Now, WorkBoardLimits.Default);

        Assert(spec.Rows.Count == rowCount, "compose does not add or remove ledger rows");
        Assert(spec.Projects.Count == projectCount, "compose does not add or remove projects");
        Assert(spec.Rows[0].Status == firstRowStatus, "compose does not rewrite ledger row state");
        Assert(tasks.Tasks.Count == taskCount, "compose does not disturb the task snapshot");
        Assert(projects[0].WorkspaceAliases.Count == aliasCount, "compose does not mutate alias lists");
    }
}
