using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

internal static class SpecBoardLedgerStore
{
    private const string ManagerUpdatedBy = "User (SpecBoardManager)";

    public static bool TrySetStatus(string ledgerPath, IList<SpecBoardRow> rows, string status, out string error)
    {
        if (!SpecBoardStatus.IsLedgerValue(status))
        {
            error = "未知的目标状态。";
            return false;
        }

        return TryMutate(ledgerPath, rows, delegate(List<Dictionary<string, object>> rawRows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                SpecBoardRow selected = rows[i];
                Dictionary<string, object> raw = FindById(rawRows, selected.Id);
                if (raw == null && selected.IsUnregistered)
                {
                    raw = CreateRegisteredRow(selected);
                    raw["id"] = MakeUniqueId(rawRows, Convert.ToString(raw["id"], CultureInfo.InvariantCulture));
                    rawRows.Add(raw);
                }

                if (raw == null)
                {
                    throw new InvalidOperationException("账本条目已不存在：" + selected.Id);
                }

                raw["status"] = status;
                StampStatus(raw, status);
                StampUpdated(raw);
            }
        }, out error);
    }

    public static bool TrySetNote(string ledgerPath, SpecBoardRow row, string note, out string error)
    {
        return TryMutate(ledgerPath, new[] { row }, delegate(List<Dictionary<string, object>> rawRows)
        {
            Dictionary<string, object> raw = FindById(rawRows, row.Id);
            if (raw == null)
            {
                throw new InvalidOperationException("账本条目已不存在：" + row.Id);
            }

            raw["note"] = note ?? string.Empty;
            StampUpdated(raw);
        }, out error);
    }

    public static bool TryRegister(string ledgerPath, SpecBoardRow row, out string error)
    {
        if (row == null || !row.IsUnregistered)
        {
            error = "只能登记未登记项。";
            return false;
        }

        return TryMutate(ledgerPath, new SpecBoardRow[0], delegate(List<Dictionary<string, object>> rawRows)
        {
            string normalizedPath = NormalizeRelative(row.SpecPath);
            if (rawRows.Any(raw => string.Equals(ReadString(raw, "project"), row.Project, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(NormalizeRelative(ReadString(raw, "spec_path")), normalizedPath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("该 spec 已被其他会话登记。请刷新列表。" );
            }

            Dictionary<string, object> created = CreateRegisteredRow(row);
            string baseId = BuildId(row.Project, Path.GetFileNameWithoutExtension(row.SpecPath));
            created["id"] = MakeUniqueId(rawRows, baseId);
            rawRows.Add(created);
        }, out error);
    }

    public static bool TryRemoveRows(string ledgerPath, IList<SpecBoardRow> rows, out string error)
    {
        return TryMutate(ledgerPath, rows, delegate(List<Dictionary<string, object>> rawRows)
        {
            HashSet<string> ids = new HashSet<string>(rows.Where(row => !row.IsUnregistered).Select(row => row.Id), StringComparer.OrdinalIgnoreCase);
            rawRows.RemoveAll(raw => ids.Contains(ReadString(raw, "id")));
        }, out error);
    }

    public static bool TryRemoveRowAndRecycleFile(string ledgerPath, SpecBoardRow row, out string error)
    {
        error = string.Empty;
        try
        {
            List<Dictionary<string, object>> rawRows = ReadRawRows(ledgerPath);
            ValidateExpected(rawRows, new[] { row });
            Dictionary<string, object> raw = FindById(rawRows, row.Id);
            if (raw == null)
            {
                error = "账本条目已不存在，请刷新列表。";
                return false;
            }

            rawRows.Remove(raw);
            string path;
            if (!SpecBoardPathPolicy.TryResolve(row.ProjectRoot, row.SpecPath, out path) || !File.Exists(path))
            {
                error = "源文件不存在或超出项目目录，账本未修改。";
                return false;
            }

            PreparedWrite prepared = PrepareWrite(ledgerPath, rawRows);
            try
            {
                string revalidatedPath;
                if (!SpecBoardPathPolicy.TryResolve(row.ProjectRoot, row.SpecPath, out revalidatedPath) ||
                    !string.Equals(path, revalidatedPath, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(revalidatedPath))
                {
                    error = "源文件路径在删除前发生变化，账本未修改。";
                    return false;
                }

                if (!RecycleFile(revalidatedPath, out error))
                {
                    return false;
                }

                CommitPreparedWrite(prepared);
                return true;
            }
            finally
            {
                DeleteTemp(prepared.TempPath);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsReferencedByTechnicalIndex(SpecBoardRow row, out string indexPath)
    {
        indexPath = string.Empty;
        try
        {
            if (row == null || string.IsNullOrEmpty(row.ProjectRoot))
            {
                return false;
            }

            indexPath = Path.Combine(row.ProjectRoot, "Docs", "Technical", "INDEX.jsonl");
            if (!File.Exists(indexPath))
            {
                return false;
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string expected = NormalizeRelative(row.SpecPath);
            foreach (string line in File.ReadAllLines(indexPath, SharedEncoding.Utf8NoBom))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Dictionary<string, object> raw = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (raw != null && string.Equals(NormalizeRelative(ReadString(raw, "doc_path")), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryMutate(string ledgerPath, IList<SpecBoardRow> expectedRows, Action<List<Dictionary<string, object>>> mutation, out string error)
    {
        error = string.Empty;
        try
        {
            List<Dictionary<string, object>> rows = ReadRawRows(ledgerPath);
            ValidateExpected(rows, expectedRows);
            mutation(rows);
            PreparedWrite prepared = PrepareWrite(ledgerPath, rows);
            try
            {
                CommitPreparedWrite(prepared);
                return true;
            }
            finally
            {
                DeleteTemp(prepared.TempPath);
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<Dictionary<string, object>> ReadRawRows(string ledgerPath)
    {
        if (string.IsNullOrWhiteSpace(ledgerPath) || !File.Exists(ledgerPath))
        {
            throw new FileNotFoundException("Spec Board 账本不存在。", ledgerPath);
        }

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
        HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in SpecBoardReader.ReadLedgerLinesStrict(ledgerPath, CancellationToken.None))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Dictionary<string, object> raw = serializer.DeserializeObject(line) as Dictionary<string, object>;
            string id = ReadString(raw, "id");
            if (raw == null || string.IsNullOrWhiteSpace(id) || !ids.Add(id))
            {
                throw new InvalidDataException("账本包含坏行或重复 id，拒绝写入。" );
            }

            rows.Add(raw);
        }

        return rows;
    }

    private static void ValidateExpected(List<Dictionary<string, object>> rawRows, IList<SpecBoardRow> expectedRows)
    {
        for (int i = 0; i < expectedRows.Count; i++)
        {
            SpecBoardRow expected = expectedRows[i];
            if (expected == null || expected.IsUnregistered) continue;
            Dictionary<string, object> raw = FindById(rawRows, expected.Id);
            if (raw == null)
            {
                throw new InvalidOperationException("冲突：条目已被其他会话删除，请刷新列表。" );
            }

            DateTime diskUpdated;
            DateTimeOffset parsed;
            string text = ReadString(raw, "updated_utc");
            diskUpdated = DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
                ? parsed.UtcDateTime
                : DateTime.MinValue;
            DateTime expectedUpdated = expected.UpdatedUtc ?? DateTime.MinValue;
            if (diskUpdated != expectedUpdated)
            {
                throw new InvalidOperationException("冲突：条目已被其他会话修改，未覆盖最新值。" );
            }
        }
    }

    private static PreparedWrite PrepareWrite(string ledgerPath, List<Dictionary<string, object>> rows)
    {
        JavaScriptSerializer serializer = new JavaScriptSerializer();
        string temp = ledgerPath + ".tmp";
        string backup = ledgerPath + ".bak";
        if (rows.Count > SpecBoardReader.MaxLedgerLines)
        {
            throw new InvalidDataException("写入结果超过 5000 行，账本未修改。" );
        }

        List<string> serializedRows = new List<string>(rows.Count);
        long totalBytes = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            string serialized = serializer.Serialize(rows[i]);
            int lineBytes = SharedEncoding.Utf8NoBom.GetByteCount(serialized);
            if (lineBytes > SpecBoardReader.MaxLineBytes)
            {
                throw new InvalidDataException("写入结果包含超过 64 KiB 的行，账本未修改。" );
            }

            totalBytes += lineBytes + 2;
            if (totalBytes > SpecBoardReader.MaxFileBytes)
            {
                throw new InvalidDataException("写入结果超过 2 MiB，账本未修改。" );
            }

            serializedRows.Add(serialized);
        }

        // Validate the complete replacement before touching the rolling backup. A rejected
        // oversized mutation must leave both the ledger and its recovery copy unchanged.
        File.Copy(ledgerPath, backup, true);
        using (StreamWriter writer = new StreamWriter(temp, false, SharedEncoding.Utf8NoBom))
        {
            for (int i = 0; i < serializedRows.Count; i++)
            {
                writer.WriteLine(serializedRows[i]);
            }
        }

        return new PreparedWrite { LedgerPath = ledgerPath, TempPath = temp };
    }

    private static void CommitPreparedWrite(PreparedWrite prepared)
    {
        File.Replace(prepared.TempPath, prepared.LedgerPath, null);
    }

    private static void DeleteTemp(string path)
    {
        try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
    }

    private static Dictionary<string, object> CreateRegisteredRow(SpecBoardRow row)
    {
        DateTimeOffset utc = DateTimeOffset.UtcNow;
        DateTimeOffset local = utc.ToOffset(TimeSpan.FromHours(9));
        return new Dictionary<string, object>
        {
            { "schema_version", 1 },
            { "id", BuildId(row.Project, Path.GetFileNameWithoutExtension(row.SpecPath)) },
            { "project", row.Project },
            { "spec_path", NormalizeRelative(row.SpecPath) },
            { "title", row.Title },
            { "status", SpecBoardStatus.Pending },
            { "registered_utc", utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
            { "registered_local", local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) },
            { "updated_utc", utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) },
            { "updated_by", ManagerUpdatedBy },
            { "note", string.Empty }
        };
    }

    private static void StampStatus(Dictionary<string, object> raw, string status)
    {
        DateTimeOffset utc = DateTimeOffset.UtcNow;
        DateTimeOffset local = utc.ToOffset(TimeSpan.FromHours(9));
        string utcText = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        string localText = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        if (status == SpecBoardStatus.NeedsRevision)
        {
            raw["revision_requested_utc"] = utcText;
            raw["revision_requested_local"] = localText;
        }
        else if (status == SpecBoardStatus.AwaitingVerify)
        {
            raw["executed_utc"] = utcText;
            raw["executed_local"] = localText;
        }
        else if (status == SpecBoardStatus.Done)
        {
            raw["verified_utc"] = utcText;
            raw["verified_local"] = localText;
        }
        else if (status == SpecBoardStatus.Abandoned)
        {
            raw["abandoned_utc"] = utcText;
            raw["abandoned_reason"] = "管理窗口强制设置";
        }
    }

    private static void StampUpdated(Dictionary<string, object> raw)
    {
        DateTimeOffset utc = DateTimeOffset.UtcNow;
        DateTimeOffset local = utc.ToOffset(TimeSpan.FromHours(9));
        raw["updated_utc"] = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        raw["updated_local"] = local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        raw["updated_by"] = ManagerUpdatedBy;
    }

    private static Dictionary<string, object> FindById(List<Dictionary<string, object>> rows, string id)
    {
        return rows.FirstOrDefault(raw => string.Equals(ReadString(raw, "id"), id, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadString(Dictionary<string, object> raw, string key)
    {
        object value;
        return raw != null && raw.TryGetValue(key, out value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string NormalizeRelative(string value)
    {
        return (value ?? string.Empty).Trim().Trim('"').Replace('\\', '/').TrimStart('/');
    }

    private static string BuildId(string project, string name)
    {
        string slug = new string((name ?? "spec").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());
        while (slug.Contains("__")) slug = slug.Replace("__", "_");
        slug = slug.Trim('_');
        return (project ?? "project") + "." + (slug.Length == 0 ? "spec" : slug);
    }

    private static string MakeUniqueId(List<Dictionary<string, object>> rows, string baseId)
    {
        string id = baseId;
        int suffix = 2;
        while (FindById(rows, id) != null)
        {
            id = baseId + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            suffix++;
        }

        return id;
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-ledger-store-" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(root, "project");
        string technical = Path.Combine(projectRoot, "Docs", "Technical");
        Directory.CreateDirectory(technical);
        try
        {
            string ledger = Path.Combine(root, "SPEC_BOARD.jsonl");
            string projects = Path.Combine(root, "PROJECTS.json");
            File.WriteAllText(projects, "{\"schema_version\":1,\"projects\":[{\"name\":\"Test\",\"root\":" + new JavaScriptSerializer().Serialize(projectRoot) + ",\"spec_glob\":\"Docs/Technical/*-SPEC-*.md\"}]}", SharedEncoding.Utf8NoBom);
            File.WriteAllText(Path.Combine(technical, "One-SPEC-v1.md"), "one", SharedEncoding.Utf8NoBom);
            File.WriteAllText(Path.Combine(technical, "Two-SPEC-v1.md"), "two", SharedEncoding.Utf8NoBom);
            File.WriteAllText(ledger,
                "{\"schema_version\":1,\"id\":\"Test.one\",\"project\":\"Test\",\"spec_path\":\"Docs/Technical/One-SPEC-v1.md\",\"title\":\"One\",\"status\":\"pending\",\"registered_utc\":\"2026-07-11T00:00:00Z\",\"updated_utc\":\"2026-07-11T00:00:00Z\"}\n" +
                "{\"schema_version\":1,\"id\":\"Test.two\",\"project\":\"Test\",\"spec_path\":\"Docs/Technical/Two-SPEC-v1.md\",\"title\":\"Two\",\"status\":\"awaiting_verify\",\"executed_utc\":\"2026-07-11T00:00:00Z\",\"updated_utc\":\"2026-07-11T00:00:00Z\"}\n" +
                "{\"schema_version\":1,\"id\":\"Other.three\",\"project\":\"Other\",\"spec_path\":\"Docs/Technical/Three-SPEC-v1.md\",\"title\":\"Three Other\",\"status\":\"abandoned\",\"abandoned_utc\":\"2026-07-11T00:00:00Z\",\"updated_utc\":\"2026-07-11T00:00:00Z\"}\n",
                SharedEncoding.Utf8NoBom);

            SpecBoardSnapshot initial = SpecBoardReader.Read(ledger, true);
            SpecBoardRow stale = initial.Rows.First(row => row.Id == "Test.one");
            string error;
            if (!TrySetStatus(ledger, new[] { stale }, SpecBoardStatus.NeedsRevision, out error) || !File.Exists(ledger + ".bak"))
            {
                throw new InvalidOperationException("Spec Board ledger status write or backup failed: " + error);
            }

            SpecBoardSnapshot changed = SpecBoardReader.Read(ledger, true);
            SpecBoardRow revision = changed.Rows.First(row => row.Id == "Test.one");
            if (revision.Status != SpecBoardStatus.NeedsRevision || !revision.RevisionRequestedUtc.HasValue || revision.UpdatedBy != ManagerUpdatedBy || changed.MalformedLines != 0)
            {
                throw new InvalidOperationException("Spec Board ledger needs_revision stamp or atomic parse failed.");
            }

            if (TrySetNote(ledger, stale, "must conflict", out error) || error.IndexOf("冲突", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Spec Board stale-write conflict policy failed.");
            }

            if (!TrySetNote(ledger, revision, "manager note", out error))
            {
                throw new InvalidOperationException("Spec Board note write failed: " + error);
            }

            SpecBoardSnapshot noted = SpecBoardReader.Read(ledger, true);
            SpecBoardRow one = noted.Rows.First(row => row.Id == "Test.one");
            SpecBoardRow two = noted.Rows.First(row => row.Id == "Test.two");
            SpecBoardRow three = noted.Rows.First(row => row.Id == "Other.three");
            if (one.Note != "manager note" || !TrySetStatus(ledger, new[] { one, two, three }, SpecBoardStatus.Done, out error))
            {
                throw new InvalidOperationException("Spec Board note or batch status failed: " + error);
            }

            File.WriteAllText(Path.Combine(technical, "Three-SPEC-v1.md"), "three", SharedEncoding.Utf8NoBom);
            SpecBoardRow unregistered = SpecBoardReader.Read(ledger, true).Rows.First(row => row.IsUnregistered && row.SpecPath.EndsWith("Three-SPEC-v1.md", StringComparison.OrdinalIgnoreCase));
            if (!TryRegister(ledger, unregistered, out error))
            {
                throw new InvalidOperationException("Spec Board unregistered registration failed: " + error);
            }

            SpecBoardSnapshot final = SpecBoardReader.Read(ledger, true);
            if (final.Rows.Count(row => !row.IsUnregistered) != 4 || final.Rows.Any(row => !row.IsUnregistered && string.IsNullOrEmpty(row.UpdatedBy)))
            {
                throw new InvalidOperationException("Spec Board register result failed.");
            }

            List<SpecBoardRow> remove = final.Rows.Where(row => !row.IsUnregistered).Take(3).ToList();
            if (!TryRemoveRows(ledger, remove, out error) || !File.Exists(Path.Combine(technical, "One-SPEC-v1.md")) || !File.Exists(Path.Combine(technical, "Two-SPEC-v1.md")))
            {
                throw new InvalidOperationException("Spec Board batch ledger-only delete failed: " + error);
            }

            RunOversizedWriteRejectionSelfTest(root, projectRoot);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void RunOversizedWriteRejectionSelfTest(string root, string projectRoot)
    {
        string oversizedLedger = Path.Combine(root, "oversized-write.jsonl");
        string validLine =
            "{\"schema_version\":1,\"id\":\"Test.oversized\",\"project\":\"Test\",\"spec_path\":\"Docs/Technical/Oversized.md\",\"title\":\"Oversized\",\"status\":\"pending\",\"updated_utc\":\"2026-07-20T00:00:00Z\"}";
        File.WriteAllText(
            oversizedLedger,
            validLine + "\n" + new string('x', SpecBoardReader.MaxFileBytes),
            SharedEncoding.Utf8NoBom);
        byte[] before = ComputeSha256(oversizedLedger);
        string error;
        if (TrySetNote(
            oversizedLedger,
            new SpecBoardRow { Id = "Test.oversized", UpdatedUtc = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc) },
            "must fail",
            out error) ||
            !before.SequenceEqual(ComputeSha256(oversizedLedger)))
        {
            throw new InvalidOperationException("Spec Board oversized input write rejection changed the ledger.");
        }

        string outputLedger = Path.Combine(root, "oversized-output.jsonl");
        File.WriteAllText(outputLedger, validLine + "\n", SharedEncoding.Utf8NoBom);
        before = ComputeSha256(outputLedger);
        SpecBoardRow row = new SpecBoardRow
        {
            Id = "Test.oversized",
            Project = "Test",
            ProjectRoot = projectRoot,
            SpecPath = "Docs/Technical/Oversized.md",
            UpdatedUtc = new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc)
        };
        if (TrySetNote(outputLedger, row, new string('n', SpecBoardReader.MaxLineBytes + 1), out error) ||
            !before.SequenceEqual(ComputeSha256(outputLedger)) || File.Exists(outputLedger + ".bak"))
        {
            throw new InvalidOperationException("Spec Board oversized output write rejection changed the ledger or backup.");
        }
    }

    private static byte[] ComputeSha256(string path)
    {
        using (SHA256 hash = SHA256.Create())
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            return hash.ComputeHash(stream);
        }
    }

    internal static void RunRecycleAcceptanceSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-specboard-recycle-" + Guid.NewGuid().ToString("N"));
        string projectRoot = Path.Combine(root, "project");
        string technical = Path.Combine(projectRoot, "Docs", "Technical");
        Directory.CreateDirectory(technical);
        try
        {
            string ledger = Path.Combine(root, "SPEC_BOARD.jsonl");
            string projects = Path.Combine(root, "PROJECTS.json");
            string source = Path.Combine(technical, "Recycle-SPEC-v1.md");
            File.WriteAllText(source, "recycle acceptance", SharedEncoding.Utf8NoBom);
            File.WriteAllText(projects, "{\"schema_version\":1,\"projects\":[{\"name\":\"Test\",\"root\":" + new JavaScriptSerializer().Serialize(projectRoot) + ",\"spec_glob\":\"Docs/Technical/*-SPEC-*.md\"}]}", SharedEncoding.Utf8NoBom);
            File.WriteAllText(ledger, "{\"schema_version\":1,\"id\":\"Test.recycle\",\"project\":\"Test\",\"spec_path\":\"Docs/Technical/Recycle-SPEC-v1.md\",\"title\":\"Recycle\",\"status\":\"pending\",\"registered_utc\":\"2026-07-11T00:00:00Z\",\"updated_utc\":\"2026-07-11T00:00:00Z\"}\n", SharedEncoding.Utf8NoBom);
            string index = Path.Combine(technical, "INDEX.jsonl");
            File.WriteAllText(index, "{\"schema_version\":1,\"id\":\"spec.recycle\",\"doc_path\":\"Docs/Technical/Recycle-SPEC-v1.md\"}\n", SharedEncoding.Utf8NoBom);
            SpecBoardRow row = SpecBoardReader.Read(ledger, true).Rows.First(value => value.Id == "Test.recycle");
            string indexPath;
            if (!IsReferencedByTechnicalIndex(row, out indexPath) || !string.Equals(indexPath, index, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Spec Board INDEX reference detection failed.");
            }

            string error;
            using (FileStream locked = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (TryRemoveRowAndRecycleFile(ledger, row, out error) || !File.Exists(source) || SpecBoardReader.Read(ledger, false).Rows.All(value => value.Id != row.Id))
                {
                    throw new InvalidOperationException("Spec Board locked-file rollback failed.");
                }
            }

            row = SpecBoardReader.Read(ledger, true).Rows.First(value => value.Id == "Test.recycle");
            if (!TryRemoveRowAndRecycleFile(ledger, row, out error) || File.Exists(source) || SpecBoardReader.Read(ledger, false).Rows.Any(value => value.Id == row.Id))
            {
                throw new InvalidOperationException("Spec Board recycle-bin delete failed: " + error);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static bool RecycleFile(string path, out string error)
    {
        SHFILEOPSTRUCT operation = new SHFILEOPSTRUCT
        {
            wFunc = 3,
            pFrom = path + '\0' + '\0',
            fFlags = 0x0040 | 0x0010 | 0x0004
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.fAnyOperationsAborted)
        {
            error = "源文件未能移入回收站（错误 " + result.ToString(CultureInfo.InvariantCulture) + "），账本未修改。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT operation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    private sealed class PreparedWrite
    {
        public string LedgerPath;
        public string TempPath;
    }
}
