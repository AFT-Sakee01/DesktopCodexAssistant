using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

// Single owner for "which Codex account is this reading about".
//
// Codex CLI keeps exactly one active account in <CODEX_HOME>/auth.json, so switching accounts is an
// overwrite of that one file. Identity therefore cannot be resolved once at startup: every quota
// reading has to carry the account it belongs to, otherwise two accounts' remaining-percent series
// silently merge into one curve and the burn forecast reads the switch as a huge consumption event.
//
// Nothing in this file persists or logs a token. Only account/user identifiers, the plan name and
// the e-mail (masked for display) leave this class; access/refresh/id tokens are decoded in memory
// and dropped.
internal sealed class CodexAccountIdentity
{
    internal const string UnknownAccountKey = "unknown";

    private CodexAccountIdentity()
    {
        this.AccountKey = UnknownAccountKey;
        this.UserId = string.Empty;
        this.Email = string.Empty;
        this.PlanType = string.Empty;
        this.AuthMode = string.Empty;
        this.SourceKind = "none";
        this.LastRefreshUtc = DateTime.MinValue;
        this.AuthFileWriteUtc = DateTime.MinValue;
    }

    // True once a stable account key was resolved. An unknown identity is still a usable object:
    // it stamps readings as "unknown" so they stay separated from any identified account instead of
    // being charged to whichever account happens to be active now.
    public bool Known { get; private set; }

    // chatgpt_account_id. Primary key, matching the de-facto convention of the Codex account
    // switcher ecosystem (account id > refresh token > e-mail).
    public string AccountKey { get; private set; }

    public string UserId { get; private set; }

    public string Email { get; private set; }

    public string PlanType { get; private set; }

    // "chatgpt" for OAuth sign-in, "apikey" when an OPENAI_API_KEY drives the CLI.
    public string AuthMode { get; private set; }

    // auth_json | provider | none. Provider wins for display because the usage endpoint returns the
    // real account e-mail while id_token may only carry an Apple/Google private relay address.
    public string SourceKind { get; private set; }

    public DateTime LastRefreshUtc { get; private set; }

    public DateTime AuthFileWriteUtc { get; private set; }

    public static CodexAccountIdentity CreateUnknown()
    {
        return new CodexAccountIdentity();
    }

    public static CodexAccountIdentity Create(
        string accountKey,
        string userId,
        string email,
        string planType,
        string authMode,
        string sourceKind,
        DateTime lastRefreshUtc,
        DateTime authFileWriteUtc)
    {
        CodexAccountIdentity identity = new CodexAccountIdentity();
        string normalizedKey = NormalizeKey(accountKey);
        if (string.IsNullOrEmpty(normalizedKey))
        {
            // A user id is a perfectly stable fallback key when the account id is missing; only when
            // neither exists does the reading become genuinely unattributable.
            normalizedKey = NormalizeKey(userId);
        }

        identity.AccountKey = string.IsNullOrEmpty(normalizedKey) ? UnknownAccountKey : normalizedKey;
        identity.Known = !string.IsNullOrEmpty(normalizedKey);
        identity.UserId = Trim(userId);
        identity.Email = Trim(email);
        identity.PlanType = Trim(planType);
        identity.AuthMode = Trim(authMode);
        identity.SourceKind = string.IsNullOrWhiteSpace(sourceKind) ? "none" : sourceKind.Trim();
        identity.LastRefreshUtc = NormalizeUtc(lastRefreshUtc);
        identity.AuthFileWriteUtc = NormalizeUtc(authFileWriteUtc);
        return identity;
    }

    public CodexAccountIdentity Clone()
    {
        return (CodexAccountIdentity)this.MemberwiseClone();
    }

    // Merge a provider-usage identity onto the locally read one. The account key must already agree
    // (callers check); this only lets the richer source fill in display fields.
    public CodexAccountIdentity MergeDisplayFrom(CodexAccountIdentity other)
    {
        if (other == null)
        {
            return this;
        }

        CodexAccountIdentity merged = this.Clone();
        if (!string.IsNullOrEmpty(other.Email))
        {
            merged.Email = other.Email;
        }

        if (!string.IsNullOrEmpty(other.PlanType))
        {
            merged.PlanType = other.PlanType;
        }

        if (!string.IsNullOrEmpty(other.UserId))
        {
            merged.UserId = other.UserId;
        }

        if (!merged.Known && other.Known)
        {
            merged.AccountKey = other.AccountKey;
            merged.Known = true;
        }

        merged.SourceKind = "provider";
        return merged;
    }

    public bool HasSameAccount(CodexAccountIdentity other)
    {
        return other != null && KeysEqual(this.AccountKey, other.AccountKey);
    }

    public static bool KeysEqual(string left, string right)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(left) ? UnknownAccountKey : left.Trim(),
            string.IsNullOrWhiteSpace(right) ? UnknownAccountKey : right.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    // Human label for UI and logs. Never the raw e-mail: local part is masked so a screenshot or a
    // log line cannot leak the full address.
    public string ResolveDisplayLabel()
    {
        string masked = MaskEmail(this.Email);
        if (!string.IsNullOrEmpty(masked))
        {
            return masked;
        }

        if (!string.IsNullOrEmpty(this.UserId))
        {
            return ShortenIdentifier(this.UserId);
        }

        return this.Known ? ShortenIdentifier(this.AccountKey) : "未识别账户";
    }

    public static string MaskEmail(string email)
    {
        string value = Trim(email);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int at = value.IndexOf('@');
        if (at <= 0 || at >= value.Length - 1)
        {
            return ShortenIdentifier(value);
        }

        string local = value.Substring(0, at);
        string domain = value.Substring(at);
        if (local.Length <= 2)
        {
            return local.Substring(0, 1) + "*" + domain;
        }

        return local.Substring(0, 2) + new string('*', Math.Min(4, local.Length - 2)) + domain;
    }

    public static string ShortenIdentifier(string value)
    {
        string trimmed = Trim(value);
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        return trimmed.Length <= 12 ? trimmed : trimmed.Substring(0, 12);
    }

    private static string NormalizeKey(string value)
    {
        string trimmed = Trim(value);
        if (string.IsNullOrEmpty(trimmed) ||
            string.Equals(trimmed, UnknownAccountKey, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static string Trim(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == DateTime.MinValue)
        {
            return DateTime.MinValue;
        }

        return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
    }
}

// Resolves the Codex CLI home and the active account from it. Every consumer that touches a Codex
// CLI path must come through here: auth.json honoured CODEX_HOME while the sessions and task-index
// readers hard-coded %USERPROFILE%\.codex, so a CODEX_HOME switch used to read the token of one
// account and the rollout history of another.
internal static class CodexHome
{
    private const int AuthJsonMaxBytes = 1024 * 1024;
    private const int IdTokenMaxChars = 16 * 1024;
    private static readonly object cacheLock = new object();
    private static string cachedAuthPath = string.Empty;
    private static DateTime cachedAuthWriteUtc = DateTime.MinValue;
    private static long cachedAuthLength = -1L;
    private static CodexAccountIdentity cachedIdentity;

    internal static string ResolveRoot()
    {
        string codexHome = GetEnvironmentVariableAnyTarget("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return codexHome.Trim();
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : Path.Combine(profile, ".codex");
    }

    internal static string ResolveAuthJsonPath()
    {
        string root = ResolveRoot();
        return string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, "auth.json");
    }

    internal static string ResolveSessionsPath()
    {
        string root = ResolveRoot();
        return string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, "sessions");
    }

    internal static string ResolveSessionIndexPath()
    {
        string root = ResolveRoot();
        return string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, "session_index.jsonl");
    }

    internal static string GetEnvironmentVariableAnyTarget(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        try
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }
        catch
        {
            value = null;
        }

        return string.IsNullOrWhiteSpace(value) ? string.Empty : value;
    }

    // Cheap enough for an owner tick: re-parses only when auth.json's write time or length moved,
    // which is exactly the signal that an account switch happened.
    internal static CodexAccountIdentity ReadCurrentIdentity()
    {
        string path = ResolveAuthJsonPath();
        if (string.IsNullOrEmpty(path))
        {
            return CodexAccountIdentity.CreateUnknown();
        }

        DateTime writeUtc = DateTime.MinValue;
        long length = -1L;
        try
        {
            FileInfo info = new FileInfo(path);
            if (info.Exists)
            {
                writeUtc = info.LastWriteTimeUtc;
                length = info.Length;
            }
        }
        catch
        {
            writeUtc = DateTime.MinValue;
            length = -1L;
        }

        lock (cacheLock)
        {
            if (cachedIdentity != null &&
                string.Equals(cachedAuthPath, path, StringComparison.OrdinalIgnoreCase) &&
                cachedAuthWriteUtc == writeUtc &&
                cachedAuthLength == length)
            {
                return cachedIdentity.Clone();
            }
        }

        CodexAccountIdentity identity = ReadIdentityFromFile(path, writeUtc);
        lock (cacheLock)
        {
            cachedAuthPath = path;
            cachedAuthWriteUtc = writeUtc;
            cachedAuthLength = length;
            cachedIdentity = identity;
        }

        return identity.Clone();
    }

    internal static void InvalidateIdentityCache()
    {
        lock (cacheLock)
        {
            cachedIdentity = null;
            cachedAuthPath = string.Empty;
            cachedAuthWriteUtc = DateTime.MinValue;
            cachedAuthLength = -1L;
        }
    }

    internal static CodexAccountIdentity ParseIdentityJson(string content, DateTime authFileWriteUtc)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return CodexAccountIdentity.CreateUnknown();
        }

        Dictionary<string, object> root;
        try
        {
            JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(AuthJsonMaxBytes);
            root = serializer.DeserializeObject(content) as Dictionary<string, object>;
        }
        catch
        {
            root = null;
        }

        if (root == null)
        {
            return CodexAccountIdentity.CreateUnknown();
        }

        Dictionary<string, object> tokens = ReadObject(root, "tokens");
        string accountKey = ReadString(tokens, "account_id");
        string authMode = ReadString(root, "auth_mode");
        DateTime lastRefreshUtc = ParseTimestamp(ReadString(root, "last_refresh"));

        string userId = string.Empty;
        string email = string.Empty;
        string planType = string.Empty;

        // The id_token is a JWT; its payload carries the authoritative account/user/plan claims.
        // Only the payload segment is base64-decoded and none of it is retained beyond these fields.
        Dictionary<string, object> claims = DecodeJwtPayload(ReadString(tokens, "id_token"));
        if (claims != null)
        {
            email = ReadString(claims, "email");
            Dictionary<string, object> auth = ReadObject(claims, "https://api.openai.com/auth");
            if (auth != null)
            {
                if (string.IsNullOrEmpty(accountKey))
                {
                    accountKey = ReadString(auth, "chatgpt_account_id");
                }

                userId = ReadString(auth, "chatgpt_user_id");
                if (string.IsNullOrEmpty(userId))
                {
                    userId = ReadString(auth, "user_id");
                }

                planType = ReadString(auth, "chatgpt_plan_type");
            }
        }

        if (string.IsNullOrEmpty(accountKey) &&
            string.IsNullOrEmpty(userId) &&
            string.Equals(authMode, "apikey", StringComparison.OrdinalIgnoreCase))
        {
            // API-key mode has no ChatGPT account at all. Give it one stable synthetic key so its
            // usage does not merge with a signed-in account's history.
            accountKey = "apikey";
        }

        return CodexAccountIdentity.Create(
            accountKey,
            userId,
            email,
            planType,
            authMode,
            "auth_json",
            lastRefreshUtc,
            authFileWriteUtc);
    }

    internal static Dictionary<string, object> DecodeJwtPayload(string jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt) || jwt.Length > IdTokenMaxChars)
        {
            return null;
        }

        string[] parts = jwt.Trim().Split('.');
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            int padding = payload.Length % 4;
            if (padding == 1)
            {
                return null;
            }

            if (padding > 0)
            {
                payload = payload.PadRight(payload.Length + (4 - padding), '=');
            }

            byte[] raw = Convert.FromBase64String(payload);
            string json = new UTF8Encoding(false, false).GetString(raw);
            JavaScriptSerializer serializer = BoundedHttpTextReader.CreateJsonSerializer(IdTokenMaxChars);
            return serializer.DeserializeObject(json) as Dictionary<string, object>;
        }
        catch
        {
            return null;
        }
    }

    private static CodexAccountIdentity ReadIdentityFromFile(string path, DateTime writeUtc)
    {
        string content;
        if (!TryReadBoundedUtf8File(path, AuthJsonMaxBytes, out content))
        {
            return CodexAccountIdentity.CreateUnknown();
        }

        return ParseIdentityJson(content, writeUtc);
    }

    internal static bool TryReadBoundedUtf8File(string path, int maxBytes, out string content)
    {
        content = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || maxBytes <= 0)
        {
            return false;
        }

        try
        {
            FileInfo info = new FileInfo(path);
            if (!info.Exists || info.Length < 0 || info.Length > maxBytes)
            {
                return false;
            }

            using (FileStream stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (MemoryStream buffer = new MemoryStream((int)Math.Min(info.Length, maxBytes)))
            {
                byte[] chunk = new byte[8192];
                int total = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        return false;
                    }

                    buffer.Write(chunk, 0, read);
                }

                content = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                return true;
            }
        }
        catch
        {
            content = string.Empty;
            return false;
        }
    }

    private static Dictionary<string, object> ReadObject(Dictionary<string, object> source, string key)
    {
        object value;
        if (source == null || !source.TryGetValue(key, out value))
        {
            return null;
        }

        return value as Dictionary<string, object>;
    }

    private static string ReadString(Dictionary<string, object> source, string key)
    {
        object value;
        if (source == null || !source.TryGetValue(key, out value) || value == null)
        {
            return string.Empty;
        }

        string text = value as string;
        if (text != null)
        {
            return text.Trim();
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture).Trim();
    }

    private static DateTime ParseTimestamp(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
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

    internal static void RunSelfTest()
    {
        string payloadJson =
            "{\"email\":\"person@example.com\"," +
            "\"https://api.openai.com/auth\":{" +
            "\"chatgpt_account_id\":\"11111111-2222-3333-4444-555555555555\"," +
            "\"chatgpt_user_id\":\"user-ABC123\"," +
            "\"chatgpt_plan_type\":\"pro\"}}";
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        string idToken = "header." + payload + ".signature";
        string authJson =
            "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"id_token\":\"" + idToken +
            "\",\"account_id\":\"11111111-2222-3333-4444-555555555555\"}," +
            "\"last_refresh\":\"2026-09-10T03:18:00Z\"}";

        CodexAccountIdentity identity = ParseIdentityJson(authJson, DateTime.MinValue);
        if (!identity.Known ||
            !string.Equals(identity.AccountKey, "11111111-2222-3333-4444-555555555555", StringComparison.Ordinal) ||
            !string.Equals(identity.UserId, "user-ABC123", StringComparison.Ordinal) ||
            !string.Equals(identity.PlanType, "pro", StringComparison.Ordinal) ||
            identity.LastRefreshUtc == DateTime.MinValue)
        {
            throw new InvalidOperationException("Codex account identity auth.json parse self-test failed.");
        }

        // The label must never contain the full local part of an address.
        string label = identity.ResolveDisplayLabel();
        if (label.IndexOf("person@", StringComparison.OrdinalIgnoreCase) >= 0 ||
            label.IndexOf('*') < 0)
        {
            throw new InvalidOperationException("Codex account identity e-mail masking self-test failed.");
        }

        // Two different accounts must never compare equal, and unknown must stay its own bucket.
        CodexAccountIdentity other = CodexAccountIdentity.Create(
            "99999999-2222-3333-4444-555555555555", "user-ZZZ", string.Empty, "plus",
            "chatgpt", "auth_json", DateTime.MinValue, DateTime.MinValue);
        if (identity.HasSameAccount(other) ||
            identity.HasSameAccount(CodexAccountIdentity.CreateUnknown()) ||
            !CodexAccountIdentity.CreateUnknown().HasSameAccount(CodexAccountIdentity.CreateUnknown()))
        {
            throw new InvalidOperationException("Codex account identity comparison self-test failed.");
        }

        // A malformed or missing token must degrade to unknown, never throw into the owner tick.
        if (ParseIdentityJson("{\"tokens\":{\"id_token\":\"not-a-jwt\"}}", DateTime.MinValue).Known ||
            ParseIdentityJson("not json at all", DateTime.MinValue).Known ||
            ParseIdentityJson(string.Empty, DateTime.MinValue).Known)
        {
            throw new InvalidOperationException("Codex account identity malformed-input self-test failed.");
        }

        // API-key mode gets its own stable bucket instead of merging into a signed-in account.
        CodexAccountIdentity apiKey = ParseIdentityJson("{\"auth_mode\":\"apikey\"}", DateTime.MinValue);
        if (!apiKey.Known || !string.Equals(apiKey.AccountKey, "apikey", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex account identity api-key mode self-test failed.");
        }

        if (string.IsNullOrEmpty(ResolveRoot()) ||
            !ResolveAuthJsonPath().EndsWith("auth.json", StringComparison.OrdinalIgnoreCase) ||
            !ResolveSessionsPath().EndsWith("sessions", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Codex home resolution self-test failed.");
        }

        Console.WriteLine("Codex account identity: PASS auth.json parse, JWT claims, masking, comparison, malformed input, api-key mode, home resolution");
    }
}
