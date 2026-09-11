using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// Local roster of Codex accounts plus the switch operation for the Reset / Speed board.
//
// Codex CLI holds one account at a time in <CODEX_HOME>/auth.json, so "switching" means restoring a
// previously captured copy of that file. Captured copies contain live OAuth tokens and are therefore
// stored only as DPAPI blobs scoped to the current Windows user (the same protection class as the
// DeepSeek key and the Claude setup token). The plaintext index deliberately carries identifiers and
// a masked e-mail only - never a token, never a full address.
//
// Capture is automatic and passive: whenever a new account key is observed the current auth.json is
// snapshotted, so the roster builds itself from normal use. Switching is never automatic; it only
// happens when the user clicks an account on the board.
internal static class CodexAccountStore
{
    internal const int MaxAccounts = 12;
    private const string DirectoryName = "codex-accounts";
    private const string IndexFileName = "codex-accounts.jsonl";
    private const string BlobExtension = ".bin";
    private const int MaxAuthJsonBytes = 1024 * 1024;
    private static readonly object syncRoot = new object();
    private static readonly string[] LetterLabels =
    {
        "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L"
    };

    private static string rootOverride;
    // Last account observed as active in this process, used to detect the switch edge.
    private static string lastObservedActiveKey = string.Empty;

    internal static void SetRootOverrideForTests(string path)
    {
        lock (syncRoot)
        {
            rootOverride = path;
        }
    }

    internal static string ResolveRoot()
    {
        lock (syncRoot)
        {
            if (!string.IsNullOrWhiteSpace(rootOverride))
            {
                return rootOverride;
            }
        }

        return Path.Combine(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.MachineName),
            DirectoryName);
    }

    internal static string ResolveIndexPath()
    {
        return Path.Combine(ResolveRoot(), IndexFileName);
    }

    internal static List<CodexAccountRecord> ListAccounts()
    {
        lock (syncRoot)
        {
            return LoadIndexLocked();
        }
    }

    // Observe the currently active identity. Returns true when the roster changed, which the caller
    // uses to refresh the board without polling the disk on every paint.
    internal static bool ObserveActiveIdentity(CodexAccountIdentity identity)
    {
        if (identity == null || !identity.Known)
        {
            return false;
        }

        lock (syncRoot)
        {
            List<CodexAccountRecord> records = LoadIndexLocked();
            CodexAccountRecord existing = FindLocked(records, identity.AccountKey);
            bool changed = false;
            DateTime nowUtc = DateTime.UtcNow;

            if (existing == null)
            {
                if (records.Count >= MaxAccounts)
                {
                    return false;
                }

                existing = new CodexAccountRecord
                {
                    AccountKey = identity.AccountKey,
                    Letter = NextLetterLocked(records),
                    CapturedAtUtc = nowUtc
                };
                records.Add(existing);
                changed = true;
            }

            // Mark when this account became the active one. Readings from account-blind sources that
            // predate this moment may belong to the previous sign-in and are refused.
            if (!CodexAccountIdentity.KeysEqual(lastObservedActiveKey, identity.AccountKey))
            {
                lastObservedActiveKey = identity.AccountKey;
                existing.ActiveSinceUtc = nowUtc;
                changed = true;
            }
            else if (existing.ActiveSinceUtc == DateTime.MinValue)
            {
                existing.ActiveSinceUtc = nowUtc;
                changed = true;
            }

            // Display fields follow the live identity so a plan upgrade or a first-seen e-mail is
            // reflected without needing a re-capture.
            string maskedEmail = CodexAccountIdentity.MaskEmail(identity.Email);
            if (!string.Equals(existing.MaskedEmail, maskedEmail, StringComparison.Ordinal) ||
                !string.Equals(existing.PlanType, identity.PlanType, StringComparison.Ordinal) ||
                !string.Equals(existing.UserId, identity.UserId, StringComparison.Ordinal) ||
                !string.Equals(existing.AuthMode, identity.AuthMode, StringComparison.Ordinal))
            {
                existing.MaskedEmail = maskedEmail;
                existing.PlanType = identity.PlanType;
                existing.UserId = identity.UserId;
                existing.AuthMode = identity.AuthMode;
                changed = true;
            }

            existing.LastSeenAtUtc = nowUtc;

            // Refresh the stored credential blob whenever auth.json moved on, so a switch back does
            // not restore an expired refresh token.
            if (TryCaptureBlobLocked(existing, identity))
            {
                changed = true;
            }

            if (changed)
            {
                SaveIndexLocked(records);
            }

            return changed;
        }
    }

    // Restore a captured account into auth.json. The outgoing account is captured first so the swap
    // is reversible even if it was never observed before.
    internal static bool TrySwitchTo(string accountKey, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (string.IsNullOrWhiteSpace(accountKey))
        {
            errorMessage = "账户标识为空。";
            return false;
        }

        lock (syncRoot)
        {
            List<CodexAccountRecord> records = LoadIndexLocked();
            CodexAccountRecord target = FindLocked(records, accountKey);
            if (target == null)
            {
                errorMessage = "账户不在本机名单内。";
                return false;
            }

            if (!target.CredentialStored)
            {
                errorMessage = "该账户没有可用的本机凭据快照。";
                return false;
            }

            string authPath = CodexHome.ResolveAuthJsonPath();
            if (string.IsNullOrEmpty(authPath))
            {
                errorMessage = "无法定位 auth.json。";
                return false;
            }

            string payload;
            try
            {
                payload = SecretStore.Unprotect(File.ReadAllText(BlobPathLocked(target.AccountKey), SharedEncoding.Utf8NoBom));
            }
            catch (Exception ex)
            {
                errorMessage = "读取凭据快照失败：" + ex.GetType().Name;
                return false;
            }

            if (string.IsNullOrWhiteSpace(payload))
            {
                errorMessage = "凭据快照为空。";
                return false;
            }

            // Reject a blob that no longer parses as the account it claims to be. Restoring a
            // mismatched file would silently sign the CLI into the wrong account.
            CodexAccountIdentity restored = CodexHome.ParseIdentityJson(payload, DateTime.MinValue);
            if (!restored.Known || !CodexAccountIdentity.KeysEqual(restored.AccountKey, target.AccountKey))
            {
                errorMessage = "凭据快照与账户不匹配。";
                return false;
            }

            try
            {
                if (File.Exists(authPath))
                {
                    // Keep exactly one rollback copy next to auth.json so a bad switch is undoable
                    // by hand without this application running.
                    File.Copy(authPath, authPath + ".dca-bak", true);
                }

                string directory = Path.GetDirectoryName(authPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string tempPath = authPath + ".dca-tmp";
                File.WriteAllText(tempPath, payload, SharedEncoding.Utf8NoBom);
                if (File.Exists(authPath))
                {
                    File.Replace(tempPath, authPath, null);
                }
                else
                {
                    File.Move(tempPath, authPath);
                }
            }
            catch (Exception ex)
            {
                errorMessage = "写入 auth.json 失败：" + ex.GetType().Name;
                return false;
            }

            DateTime switchedAtUtc = DateTime.UtcNow;
            target.LastSeenAtUtc = switchedAtUtc;
            target.ActiveSinceUtc = switchedAtUtc;
            lastObservedActiveKey = target.AccountKey;
            SaveIndexLocked(records);
        }

        CodexHome.InvalidateIdentityCache();
        return true;
    }

    // The point from which an account-blind reading may be trusted for this account.
    //
    // DateTime.MinValue disables the gate. That is deliberate for a machine that has only ever seen
    // one account: there is no second account to confuse it with, and gating a fresh install would
    // reject its entire existing rollout history for no benefit.
    internal static DateTime ResolveSessionTrustBoundaryUtc(string accountKey)
    {
        lock (syncRoot)
        {
            List<CodexAccountRecord> records = LoadIndexLocked();
            if (records.Count <= 1)
            {
                return DateTime.MinValue;
            }

            CodexAccountRecord record = FindLocked(records, accountKey);
            return record == null ? DateTime.MinValue : record.ActiveSinceUtc;
        }
    }

    internal static bool TryRemove(string accountKey)
    {
        lock (syncRoot)
        {
            List<CodexAccountRecord> records = LoadIndexLocked();
            CodexAccountRecord target = FindLocked(records, accountKey);
            if (target == null)
            {
                return false;
            }

            records.Remove(target);
            try
            {
                string blob = BlobPathLocked(target.AccountKey);
                if (File.Exists(blob))
                {
                    File.Delete(blob);
                }
            }
            catch
            {
            }

            SaveIndexLocked(records);
            return true;
        }
    }

    private static bool TryCaptureBlobLocked(CodexAccountRecord record, CodexAccountIdentity identity)
    {
        string authPath = CodexHome.ResolveAuthJsonPath();
        if (string.IsNullOrEmpty(authPath))
        {
            return false;
        }

        DateTime writeUtc;
        try
        {
            FileInfo info = new FileInfo(authPath);
            if (!info.Exists)
            {
                return false;
            }

            writeUtc = info.LastWriteTimeUtc;
        }
        catch
        {
            return false;
        }

        if (record.CredentialStored && record.CredentialSourceWriteUtc == writeUtc)
        {
            return false;
        }

        string content;
        if (!CodexHome.TryReadBoundedUtf8File(authPath, MaxAuthJsonBytes, out content))
        {
            return false;
        }

        // Only capture when the file on disk really is the account we were told about; during a
        // switch the two can disagree for a moment.
        CodexAccountIdentity onDisk = CodexHome.ParseIdentityJson(content, writeUtc);
        if (!onDisk.Known || !CodexAccountIdentity.KeysEqual(onDisk.AccountKey, identity.AccountKey))
        {
            return false;
        }

        try
        {
            EnsureRootLocked();
            File.WriteAllText(
                BlobPathLocked(record.AccountKey),
                SecretStore.Protect(content),
                SharedEncoding.Utf8NoBom);
        }
        catch
        {
            return false;
        }

        record.CredentialStored = true;
        record.CredentialSourceWriteUtc = writeUtc;
        return true;
    }

    private static CodexAccountRecord FindLocked(List<CodexAccountRecord> records, string accountKey)
    {
        for (int i = 0; i < records.Count; i++)
        {
            if (CodexAccountIdentity.KeysEqual(records[i].AccountKey, accountKey))
            {
                return records[i];
            }
        }

        return null;
    }

    private static string NextLetterLocked(List<CodexAccountRecord> records)
    {
        for (int i = 0; i < LetterLabels.Length; i++)
        {
            bool taken = false;
            for (int j = 0; j < records.Count; j++)
            {
                if (string.Equals(records[j].Letter, LetterLabels[i], StringComparison.Ordinal))
                {
                    taken = true;
                    break;
                }
            }

            if (!taken)
            {
                return LetterLabels[i];
            }
        }

        return "?";
    }

    private static void EnsureRootLocked()
    {
        string root = ResolveRoot();
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
        }
    }

    private static string BlobPathLocked(string accountKey)
    {
        return Path.Combine(ResolveRoot(), SanitizeFileName(accountKey) + BlobExtension);
    }

    internal static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        StringBuilder builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length && builder.Length < 64; i++)
        {
            char c = value[i];
            bool safe = (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                c == '-' || c == '_';
            builder.Append(safe ? c : '_');
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static List<CodexAccountRecord> LoadIndexLocked()
    {
        List<CodexAccountRecord> records = new List<CodexAccountRecord>();
        string path = ResolveIndexPath();
        if (!File.Exists(path))
        {
            return records;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path, SharedEncoding.Utf8NoBom);
        }
        catch
        {
            return records;
        }

        JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(MaxAuthJsonBytes);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i] == null ? string.Empty : lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            Dictionary<string, object> data;
            try
            {
                data = serializer.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                continue;
            }

            if (data == null)
            {
                continue;
            }

            string accountKey = ReadString(data, "account_key");
            if (string.IsNullOrEmpty(accountKey) || FindLocked(records, accountKey) != null)
            {
                continue;
            }

            CodexAccountRecord record = new CodexAccountRecord
            {
                AccountKey = accountKey,
                Letter = ReadString(data, "letter"),
                MaskedEmail = ReadString(data, "masked_email"),
                PlanType = ReadString(data, "plan_type"),
                UserId = ReadString(data, "user_id"),
                AuthMode = ReadString(data, "auth_mode"),
                CapturedAtUtc = ReadTimestamp(data, "captured_at_utc"),
                LastSeenAtUtc = ReadTimestamp(data, "last_seen_at_utc"),
                ActiveSinceUtc = ReadTimestamp(data, "active_since_utc"),
                CredentialSourceWriteUtc = ReadTimestamp(data, "credential_source_write_utc")
            };

            if (string.IsNullOrEmpty(record.Letter))
            {
                record.Letter = NextLetterLocked(records);
            }

            try
            {
                record.CredentialStored = File.Exists(BlobPathLocked(record.AccountKey));
            }
            catch
            {
                record.CredentialStored = false;
            }

            records.Add(record);
        }

        return records;
    }

    private static void SaveIndexLocked(List<CodexAccountRecord> records)
    {
        try
        {
            EnsureRootLocked();
            StringBuilder builder = new StringBuilder();
            JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(MaxAuthJsonBytes);
            for (int i = 0; i < records.Count; i++)
            {
                CodexAccountRecord record = records[i];
                Dictionary<string, object> data = new Dictionary<string, object>();
                data["schema_version"] = 1;
                data["account_key"] = record.AccountKey;
                data["letter"] = record.Letter ?? string.Empty;
                // Masked only. The full address never reaches disk.
                data["masked_email"] = record.MaskedEmail ?? string.Empty;
                data["plan_type"] = record.PlanType ?? string.Empty;
                data["user_id"] = record.UserId ?? string.Empty;
                data["auth_mode"] = record.AuthMode ?? string.Empty;
                data["captured_at_utc"] = FormatTimestamp(record.CapturedAtUtc);
                data["last_seen_at_utc"] = FormatTimestamp(record.LastSeenAtUtc);
                data["active_since_utc"] = FormatTimestamp(record.ActiveSinceUtc);
                data["credential_source_write_utc"] = FormatTimestamp(record.CredentialSourceWriteUtc);
                builder.AppendLine(serializer.Serialize(data));
            }

            string path = ResolveIndexPath();
            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, builder.ToString(), SharedEncoding.Utf8NoBom);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
        }
    }

    private static string ReadString(Dictionary<string, object> data, string key)
    {
        object value;
        if (data == null || !data.TryGetValue(key, out value) || value == null)
        {
            return string.Empty;
        }

        string text = value as string;
        return text == null
            ? Convert.ToString(value, CultureInfo.InvariantCulture).Trim()
            : text.Trim();
    }

    private static DateTime ReadTimestamp(Dictionary<string, object> data, string key)
    {
        string value = ReadString(data, key);
        if (string.IsNullOrEmpty(value))
        {
            return DateTime.MinValue;
        }

        DateTime parsed;
        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return DateTime.MinValue;
    }

    private static string FormatTimestamp(DateTime value)
    {
        return value == DateTime.MinValue
            ? string.Empty
            : value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "dca-codex-accounts-" + Guid.NewGuid().ToString("N"));
        string previousOverride;
        lock (syncRoot)
        {
            previousOverride = rootOverride;
        }

        try
        {
            SetRootOverrideForTests(root);
            Directory.CreateDirectory(root);

            if (ListAccounts().Count != 0)
            {
                throw new InvalidOperationException("Codex account store empty-roster self-test failed.");
            }

            CodexAccountIdentity first = CodexAccountIdentity.Create(
                "aaaa-1111", "user-A", "alpha@example.com", "pro",
                "chatgpt", "auth_json", DateTime.MinValue, DateTime.MinValue);
            CodexAccountIdentity second = CodexAccountIdentity.Create(
                "bbbb-2222", "user-B", string.Empty, "plus",
                "chatgpt", "auth_json", DateTime.MinValue, DateTime.MinValue);

            ObserveActiveIdentity(first);
            ObserveActiveIdentity(second);
            List<CodexAccountRecord> records = ListAccounts();
            if (records.Count != 2 ||
                !string.Equals(records[0].Letter, "A", StringComparison.Ordinal) ||
                !string.Equals(records[1].Letter, "B", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex account store letter assignment self-test failed.");
            }

            // Letters must stay stable across reloads so the board does not renumber accounts.
            ObserveActiveIdentity(first);
            records = ListAccounts();
            if (records.Count != 2 || !string.Equals(records[0].Letter, "A", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex account store letter stability self-test failed.");
            }

            // The index must never contain a full address.
            string indexText = File.ReadAllText(ResolveIndexPath(), SharedEncoding.Utf8NoBom);
            if (indexText.IndexOf("alpha@example.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new InvalidOperationException("Codex account store e-mail masking self-test failed.");
            }

            // Display label falls back to the letter when no address was ever seen.
            if (!string.Equals(records[1].ResolveDisplayLabel(), "账户 B", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex account store letter fallback self-test failed.");
            }

            // Switching to an account with no stored credential must fail closed.
            string error;
            if (TrySwitchTo("bbbb-2222", out error) || string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException("Codex account store missing-credential self-test failed.");
            }

            if (TrySwitchTo("does-not-exist", out error) || string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException("Codex account store unknown-account self-test failed.");
            }

            if (!TryRemove("bbbb-2222") || ListAccounts().Count != 1 || TryRemove("bbbb-2222"))
            {
                throw new InvalidOperationException("Codex account store removal self-test failed.");
            }
        }
        finally
        {
            SetRootOverrideForTests(previousOverride);
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine("Codex account store: PASS roster capture, stable letters, masking, fail-closed switch, removal");
    }
}

internal sealed class CodexAccountRecord
{
    public string AccountKey { get; set; }

    // Stable A/B/C/D badge assigned on first capture. This is what the board shows when the account
    // has no readable e-mail at all.
    public string Letter { get; set; }

    public string MaskedEmail { get; set; }

    public string PlanType { get; set; }

    public string UserId { get; set; }

    public string AuthMode { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public DateTime LastSeenAtUtc { get; set; }

    // When this account most recently became the active one. Readings from sources that carry no
    // account id are only trusted from this moment onward.
    public DateTime ActiveSinceUtc { get; set; }

    public bool CredentialStored { get; set; }

    public DateTime CredentialSourceWriteUtc { get; set; }

    public string ResolveDisplayLabel()
    {
        if (!string.IsNullOrWhiteSpace(this.MaskedEmail))
        {
            return this.MaskedEmail;
        }

        if (!string.IsNullOrWhiteSpace(this.Letter))
        {
            return "账户 " + this.Letter;
        }

        return CodexAccountIdentity.ShortenIdentifier(this.AccountKey);
    }

    public CodexAccountRecord Clone()
    {
        return (CodexAccountRecord)this.MemberwiseClone();
    }
}
