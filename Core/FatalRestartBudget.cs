using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

internal static class FatalRestartBudget
{
    private const int SchemaVersion = 1;
    private static readonly object SyncRoot = new object();

    public static string StatePath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductIdentity.MachineName,
                "fatal-restart-state.json");
        }
    }

    public static bool TryAcquire(string statePath, DateTime nowUtc, TimeSpan suppressionWindow, out string diagnostic)
    {
        diagnostic = string.Empty;
        lock (SyncRoot)
        {
            DateTime? previous = null;
            try
            {
                previous = ReadLastFatalUtc(statePath);
            }
            catch (Exception ex)
            {
                diagnostic = "fatal_restart_state_read_failed:" + ex.GetType().Name;
            }

            if (previous.HasValue &&
                nowUtc >= previous.Value &&
                nowUtc - previous.Value < suppressionWindow)
            {
                diagnostic = "fatal_restart_suppressed";
                return false;
            }

            try
            {
                WriteStateAtomic(statePath, nowUtc);
                if (diagnostic.Length == 0)
                {
                    diagnostic = previous.HasValue
                        ? "fatal_restart_budget_refreshed"
                        : "fatal_restart_budget_created";
                }
            }
            catch (Exception ex)
            {
                // State I/O must never prevent the one recovery attempt that could restore the UI.
                diagnostic = "fatal_restart_state_write_failed:" + ex.GetType().Name;
            }

            return true;
        }
    }

    internal static void RunSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-fatal-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string statePath = Path.Combine(root, "fatal-restart-state.json");
            DateTime nowUtc = new DateTime(2026, 7, 20, 1, 0, 0, DateTimeKind.Utc);
            string diagnostic;
            if (!TryAcquire(statePath, nowUtc, TimeSpan.FromMinutes(15), out diagnostic) || !File.Exists(statePath))
            {
                throw new InvalidOperationException("Fatal restart missing-state self-test failed: " + diagnostic);
            }

            if (TryAcquire(statePath, nowUtc.AddMinutes(5), TimeSpan.FromMinutes(15), out diagnostic))
            {
                throw new InvalidOperationException("Fatal restart recent-state suppression self-test failed.");
            }

            WriteStateAtomic(statePath, nowUtc.AddMinutes(-16));
            if (!TryAcquire(statePath, nowUtc, TimeSpan.FromMinutes(15), out diagnostic))
            {
                throw new InvalidOperationException("Fatal restart expired-state self-test failed: " + diagnostic);
            }

            File.WriteAllText(statePath, "{corrupt", SharedEncoding.Utf8NoBom);
            if (!TryAcquire(statePath, nowUtc.AddMinutes(20), TimeSpan.FromMinutes(15), out diagnostic) ||
                diagnostic.IndexOf("read_failed", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Fatal restart corrupt-state fail-open self-test failed: " + diagnostic);
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static DateTime? ReadLastFatalUtc(string statePath)
    {
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
        {
            return null;
        }

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        Dictionary<string, object> state = serializer.DeserializeObject(
            File.ReadAllText(statePath, SharedEncoding.Utf8NoBom)) as Dictionary<string, object>;
        object schema;
        object timestamp;
        int parsedSchema;
        DateTimeOffset parsedTimestamp;
        if (state == null ||
            !state.TryGetValue("schema_version", out schema) ||
            !int.TryParse(Convert.ToString(schema, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedSchema) ||
            parsedSchema != SchemaVersion ||
            !state.TryGetValue("last_fatal_utc", out timestamp) ||
            !DateTimeOffset.TryParse(
                Convert.ToString(timestamp, CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsedTimestamp))
        {
            throw new InvalidDataException("Fatal restart state is invalid.");
        }

        return parsedTimestamp.UtcDateTime;
    }

    private static void WriteStateAtomic(string statePath, DateTime timestampUtc)
    {
        if (string.IsNullOrWhiteSpace(statePath))
        {
            throw new ArgumentException("Fatal restart state path is required.", "statePath");
        }

        string directory = Path.GetDirectoryName(statePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Dictionary<string, object> state = new Dictionary<string, object>
        {
            { "schema_version", SchemaVersion },
            { "last_fatal_utc", timestampUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) }
        };
        string tempPath = statePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, new JavaScriptSerializer().Serialize(state), SharedEncoding.Utf8NoBom);
            if (File.Exists(statePath))
            {
                File.Replace(tempPath, statePath, null);
            }
            else
            {
                File.Move(tempPath, statePath);
            }
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}
