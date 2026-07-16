using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

internal sealed class SpecBoardSeenStateStore
{
    private readonly string path;
    private readonly Dictionary<string, DateTime> lastSeenUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    public SpecBoardSeenStateStore(string path)
    {
        this.path = path;
    }

    public static string DefaultPath
    {
        get { return Path.Combine(Logger.DirectoryPath, "SpecBoardSeenState.json"); }
    }

    public void LoadOrSeed(SpecBoardSnapshot snapshot)
    {
        this.lastSeenUtc.Clear();
        bool loaded = TryLoad();
        if (loaded)
        {
            return;
        }

        DateTime baseline = snapshot == null ? DateTime.UtcNow : snapshot.ScanTimeUtc;
        IEnumerable<string> projects = snapshot == null
            ? Enumerable.Empty<string>()
            : snapshot.Projects.Select(project => project.Name)
                .Concat(snapshot.Rows.Select(row => row.Project));
        foreach (string project in projects.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            this.lastSeenUtc[project] = baseline;
        }

        Save();
    }

    public bool IsFresh(string project, SpecBoardSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(project) || snapshot == null)
        {
            return false;
        }

        DateTime newest = snapshot.Rows
            .Where(row => !row.IsUnregistered && row.UpdatedUtc.HasValue && string.Equals(row.Project, project, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.UpdatedUtc.Value)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        DateTime seen;
        if (!this.lastSeenUtc.TryGetValue(project, out seen))
        {
            seen = DateTime.MinValue;
        }

        return newest > seen;
    }

    public void MarkSeen(string project, DateTime snapshotScanUtc)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return;
        }

        this.lastSeenUtc[project] = snapshotScanUtc;
        Save();
    }

    private bool TryLoad()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(this.path) || !File.Exists(this.path))
            {
                return false;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            Dictionary<string, object> values = serializer.DeserializeObject(File.ReadAllText(this.path, SharedEncoding.Utf8NoBom)) as Dictionary<string, object>;
            if (values == null)
            {
                return false;
            }

            foreach (KeyValuePair<string, object> pair in values)
            {
                DateTimeOffset parsed;
                if (DateTimeOffset.TryParse(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                {
                    this.lastSeenUtc[pair.Key] = parsed.UtcDateTime;
                }
            }

            return true;
        }
        catch
        {
            this.lastSeenUtc.Clear();
            return false;
        }
    }

    private void Save()
    {
        try
        {
            string directory = Path.GetDirectoryName(this.path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Dictionary<string, string> values = this.lastSeenUtc.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                StringComparer.OrdinalIgnoreCase);
            string temp = this.path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(values), SharedEncoding.Utf8NoBom);
            if (File.Exists(this.path))
            {
                File.Replace(temp, this.path, null);
            }
            else
            {
                File.Move(temp, this.path);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-seen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string statePath = Path.Combine(root, "seen.json");
            DateTime scan = new DateTime(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
            SpecBoardSnapshot first = new SpecBoardSnapshot { ScanTimeUtc = scan };
            first.Projects.Add(new SpecBoardProject { Name = "A" });
            first.Rows.Add(new SpecBoardRow { Project = "A", UpdatedUtc = scan.AddMinutes(-1) });
            SpecBoardSeenStateStore store = new SpecBoardSeenStateStore(statePath);
            store.LoadOrSeed(first);
            if (store.IsFresh("A", first) || !File.Exists(statePath))
            {
                throw new InvalidOperationException("Spec Board seen-state first-run seed failed.");
            }

            first.Rows.Add(new SpecBoardRow { Project = "A", UpdatedUtc = scan.AddMinutes(2) });
            if (!store.IsFresh("A", first))
            {
                throw new InvalidOperationException("Spec Board seen-state freshness detection failed.");
            }

            store.MarkSeen("A", scan.AddMinutes(3));
            if (store.IsFresh("A", first))
            {
                throw new InvalidOperationException("Spec Board seen-state mark-seen failed.");
            }

            first.Projects.Add(new SpecBoardProject { Name = "B" });
            first.Rows.Add(new SpecBoardRow { Project = "B", UpdatedUtc = scan });
            if (!store.IsFresh("B", first))
            {
                throw new InvalidOperationException("Spec Board seen-state new-project policy failed.");
            }

            File.WriteAllText(statePath, "{bad json", SharedEncoding.Utf8NoBom);
            first.ScanTimeUtc = scan.AddMinutes(5);
            SpecBoardSeenStateStore corrupt = new SpecBoardSeenStateStore(statePath);
            corrupt.LoadOrSeed(first);
            if (corrupt.IsFresh("A", first) || corrupt.IsFresh("B", first))
            {
                throw new InvalidOperationException("Spec Board corrupt seen-state reseed failed.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
