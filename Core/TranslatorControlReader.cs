using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Headless data owner for the eighth left-dock board (Captions/字幕). Mirrors PowerThermalForm's
// StartHeadlessDataOwner()/StopHeadlessDataOwner() lifecycle contract (idempotent, throws if
// disposed) as constructed and owned by WidgetForm, but is not itself a Form: it monitors an
// external app's config file, SQLite history database and three OS processes -- none of that needs
// an HWND or Windows messages, so there is no InvokeRequired/Invoke marshaling here. Every method on
// this class is only ever called from the UI thread: WidgetForm constructs/starts/stops it,
// CaptionsBoardForm's own maintenance timer drives RefreshIfDue, and CaptionsBoardForm's
// click-triggered background Tasks always marshal their continuation back to the UI thread (via
// Control.BeginInvoke) before calling back into this reader -- see CaptionsBoardForm.cs.
//
// Publishes an immutable cloned TranslatorControlSnapshot (GetSnapshot(), cache-only, no I/O) the
// same way CodexRadarForm's snapshot builders and PowerThermalForm.BuildStripSnapshot() do. All I/O
// (file reads, the SQLite walk, WMI/Process queries, the settings write + app restart) happens here,
// never in the board's paint code.
internal sealed class TranslatorControlReader : IDisposable
{
    // Fixed external deployment paths for the separately-deployed LiveCaptions-Translator app (see
    // this repo's AGENTS.md: never modify anything under this directory, read/monitor only). Not a
    // WidgetSettings entry: the brief that introduced this board describes a fixed, already-deployed
    // location on this machine, not a user-configurable one -- keeping it a constant avoids adding
    // settings-UI surface area nothing asked for.
    private const string TranslatorDirectory = @"D:\E_Drive_Files\Codexproject\LiveCaptions-Translator";
    private const string SettingsFileName = "setting.json";
    private const string HistoryDatabaseFileName = "translation_history.db";
    private const string HistoryTableName = "TranslationHistory";
    private const string TranslatorExecutableName = "LiveCaptionsTranslator.exe";
    private const string TranslatorProcessName = "LiveCaptionsTranslator";
    private const string GenieXProcessName = "geniex";
    private const string NodeProcessName = "node";
    private const string SanitizeProxyScriptName = "sanitize-proxy.js";
    private const string GenieXModelManifestFileName = "geniex.json";

    private const int RefreshIntervalMs = 2000;
    private const int HistoryRowLimit = 8;
    // Matches OperationForm's SeelenUI kill/relaunch precedent (RestartSeelenUiForApplicationRestart):
    // a short pause between Kill() and the next Process.Start() for a clean exit.
    private const int ProcessRestartGraceMs = 350;
    private const int ProcessExitWaitMs = 4000;
    private const int FailureAlertDebounceSeconds = 10;
    private const int AlertReasonMaxLength = 80;

    internal enum SettingChangeKind
    {
        ContextAware,
        NumContexts,
        ModelName
    }

    private readonly Action<string, string, ToolTipIcon> showNotification;
    private readonly Dictionary<string, ServiceAlertDebounceState> alertStates =
        new Dictionary<string, ServiceAlertDebounceState>(StringComparer.OrdinalIgnoreCase);
    private TranslatorControlSnapshot latestSnapshot = TranslatorControlSnapshot.CreateEmpty();
    private DateTime nextRefreshUtc = DateTime.MinValue;
    private bool lastFailureAlertActive;
    private bool restartInProgress;
    private bool started;
    private bool disposed;

    internal TranslatorControlReader(Action<string, string, ToolTipIcon> showNotification)
    {
        this.showNotification = showNotification;
    }

    internal void StartHeadlessDataOwner()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(TranslatorControlReader));
        }

        if (this.started)
        {
            return;
        }

        this.started = true;
        this.nextRefreshUtc = DateTime.MinValue; // force an immediate refresh on the next poll
    }

    internal void StopHeadlessDataOwner()
    {
        if (this.disposed)
        {
            return;
        }

        this.started = false;
    }

    public void Dispose()
    {
        this.disposed = true;
        this.started = false;
    }

    // Cache-only: returns a clone of whatever RefreshIfDue last published. Safe to call from board
    // paint code -- never touches disk, the registry, or a process list.
    internal TranslatorControlSnapshot GetSnapshot()
    {
        return this.latestSnapshot.Clone();
    }

    // Set synchronously (from the UI thread, immediately before/after a background apply Task) so
    // GetSnapshot() reflects "a write is in flight" without waiting for the next 2000ms poll.
    internal void SetRestartInProgress(bool inProgress)
    {
        this.restartInProgress = inProgress;
        this.latestSnapshot.RestartInProgress = inProgress;
    }

    // Self-gated refresh, matching OperationForm.RefreshSeelenUiStatus's shape: called from a
    // frequent maintenance tick (CaptionsBoardForm's own 500ms timer, itself only running while the
    // board is visible -- see CaptionsBoardForm.cs), but only actually does I/O once every
    // RefreshIntervalMs. This is what keeps this headless owner from polling three processes, a JSON
    // file and a SQLite file every tick while the board is collapsed or hidden.
    internal void RefreshIfDue(DateTime nowUtc, bool force)
    {
        if (this.disposed || !this.started)
        {
            return;
        }

        if (!force && nowUtc < this.nextRefreshUtc)
        {
            return;
        }

        this.nextRefreshUtc = nowUtc.AddMilliseconds(RefreshIntervalMs);

        TranslatorControlSnapshot next = TranslatorControlSnapshot.CreateEmpty();
        try
        {
            next.IsRunning = IsLiveCaptionsTranslatorRunning();
            next.GenieXRunning = IsGenieXRunning();
            next.SanitizeProxyRunning = IsSanitizeProxyRunning();
            next.RestartInProgress = this.restartInProgress;

            ReadSettingsFile(Path.Combine(TranslatorDirectory, SettingsFileName), next);
            ReadHistory(Path.Combine(TranslatorDirectory, HistoryDatabaseFileName), next);

            List<string> models = BuildAvailableModels();
            for (int i = 0; i < models.Count; i++)
            {
                next.AvailableModels.Add(models[i]);
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        UpdateFailureAlert(next);
        this.latestSnapshot = next;
    }

    // ── Write path: setting.json mutation + conditional restart ────────────────────────────────
    //
    // Synchronous and blocking (file I/O + process kill/relaunch): callers run this on a background
    // Task (see CaptionsBoardForm's async apply pattern, mirroring
    // OperationForm.RequestBatteryCareFromGuardBoard) and marshal the result back to the UI thread
    // themselves.
    internal bool TryApplySettingChange(SettingChangeKind kind, bool boolValue, int intValue, string stringValue, out string detail)
    {
        string settingsPath = Path.Combine(TranslatorDirectory, SettingsFileName);
        if (!TryMutateSettingsJson(settingsPath, kind, boolValue, intValue, stringValue, out detail))
        {
            return false;
        }

        string restartDetail;
        if (!TryRestartTranslatorForSettingsChange(out restartDetail))
        {
            detail = "已保存设置，但重启翻译进程失败：" + restartDetail;
            return false;
        }

        return true;
    }

    internal bool TryToggleTranslatorRunning(bool start, out string detail)
    {
        detail = string.Empty;
        if (start)
        {
            if (IsLiveCaptionsTranslatorRunning())
            {
                return true;
            }

            return TryStartTranslator(out detail);
        }

        KillTranslatorProcesses();
        Thread.Sleep(ProcessRestartGraceMs);
        if (IsLiveCaptionsTranslatorRunning())
        {
            detail = "进程未能退出";
            return false;
        }

        return true;
    }

    // LiveCaptionsTranslator reads setting.json once at process startup only (no hot reload, no
    // file watcher in its own source -- see this repo's AGENTS.md background for this board). If it
    // is not currently running, the fresh file is simply picked up whenever it is next launched, so
    // there is nothing to restart.
    private static bool TryRestartTranslatorForSettingsChange(out string detail)
    {
        detail = string.Empty;
        if (!IsLiveCaptionsTranslatorRunning())
        {
            return true;
        }

        KillTranslatorProcesses();
        Thread.Sleep(ProcessRestartGraceMs);
        return TryStartTranslator(out detail);
    }

    private static bool TryStartTranslator(out string detail)
    {
        detail = string.Empty;
        string exePath = Path.Combine(TranslatorDirectory, TranslatorExecutableName);
        if (!File.Exists(exePath))
        {
            detail = "未找到 " + exePath;
            return false;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = exePath;
            // WorkingDirectory must be the app's own folder so it resolves setting.json and
            // translation_history.db (both working-directory-relative) the same way the
            // Start-Translator.ps1 convenience launcher does.
            startInfo.WorkingDirectory = TranslatorDirectory;
            startInfo.UseShellExecute = true;
            Process process = Process.Start(startInfo);
            if (process != null)
            {
                process.Dispose();
                return true;
            }

            detail = "Process.Start 返回 null";
            return false;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static int KillTranslatorProcesses()
    {
        int killed = 0;
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(TranslatorProcessName);
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    if (!processes[i].HasExited)
                    {
                        processes[i].Kill();
                        processes[i].WaitForExit(ProcessExitWaitMs);
                    }

                    killed++;
                }
                catch (Exception ex)
                {
                    Program.LogException(ex);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
        finally
        {
            DisposeProcesses(processes);
        }

        return killed;
    }

    // ── Process monitoring ──────────────────────────────────────────────────────────────────────

    internal static bool IsLiveCaptionsTranslatorRunning()
    {
        return IsProcessRunningByName(TranslatorProcessName);
    }

    internal static bool IsGenieXRunning()
    {
        return IsProcessRunningByName(GenieXProcessName);
    }

    private static bool IsProcessRunningByName(string processName)
    {
        Process[] processes = null;
        try
        {
            processes = Process.GetProcessesByName(processName);
            return processes != null && processes.Length > 0;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }
        finally
        {
            DisposeProcesses(processes);
        }
    }

    private static void DisposeProcesses(Process[] processes)
    {
        if (processes == null)
        {
            return;
        }

        for (int i = 0; i < processes.Length; i++)
        {
            try
            {
                processes[i].Dispose();
            }
            catch
            {
            }
        }
    }

    // Multiple unrelated node.exe processes typically run on this machine, so the sanitize-proxy one
    // is identified by command line (Win32_Process.CommandLine containing sanitize-proxy.js), not by
    // process name alone -- per this board's own background brief.
    internal static bool IsSanitizeProxyRunning()
    {
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT CommandLine FROM Win32_Process WHERE Name = '" + NodeProcessName + ".exe'"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        string commandLine = Convert.ToString(item["CommandLine"], CultureInfo.InvariantCulture) ?? string.Empty;
                        if (commandLine.IndexOf(SanitizeProxyScriptName, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    // ── setting.json read ───────────────────────────────────────────────────────────────────────
    //
    // Uses System.Web.Script.Serialization.JavaScriptSerializer (already available through
    // System.Web.Extensions.dll, which Build-Arm64.ps1 already references) rather than
    // System.Text.Json.Nodes.JsonNode: this project compiles via a hand-curated csc.exe /reference:
    // list against classic .NET Framework 4.x assemblies with no NuGet/MSBuild package step
    // anywhere in the toolchain, so System.Text.Json is not available without a build-pipeline
    // change. JavaScriptSerializer.DeserializeObject decodes a JSON object to
    // Dictionary<string, object> and a JSON array to object[] (verified empirically while building
    // this reader) -- walking and mutating that graph in place, then re-serializing the whole root,
    // reads/writes every field the live setting.json actually has (ApiKey, Prompt, TargetLanguage,
    // ConfigIndices, ...) even though this class only ever looks at a handful of them by name, so an
    // unrelated field this reader doesn't know about is never dropped.
    private static void ReadSettingsFile(string settingsPath, TranslatorControlSnapshot snapshot)
    {
        if (!File.Exists(settingsPath))
        {
            snapshot.SettingsFileFound = false;
            return;
        }

        try
        {
            Dictionary<string, object> root = DeserializeJsonObject(File.ReadAllText(settingsPath));
            if (root == null)
            {
                return;
            }

            snapshot.SettingsFileFound = true;

            object contextAwareValue;
            if (root.TryGetValue("ContextAware", out contextAwareValue) && contextAwareValue is bool)
            {
                snapshot.ContextAwareKnown = true;
                snapshot.ContextAware = (bool)contextAwareValue;
            }

            object numContextsValue;
            int numContexts;
            if (root.TryGetValue("NumContexts", out numContextsValue) && TryConvertToInt(numContextsValue, out numContexts))
            {
                snapshot.NumContextsKnown = true;
                snapshot.NumContexts = numContexts;
            }

            Dictionary<string, object> activeConfig = ResolveActiveOpenAiConfig(root);
            if (activeConfig != null)
            {
                object modelNameValue;
                if (activeConfig.TryGetValue("ModelName", out modelNameValue) && modelNameValue is string)
                {
                    snapshot.ModelNameKnown = true;
                    snapshot.ModelName = (string)modelNameValue;
                }

                object apiUrlValue;
                if (activeConfig.TryGetValue("ApiUrl", out apiUrlValue) && apiUrlValue is string)
                {
                    snapshot.ApiUrl = (string)apiUrlValue;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    // ── setting.json write ──────────────────────────────────────────────────────────────────────

    internal static bool TryMutateSettingsJson(
        string settingsPath,
        SettingChangeKind kind,
        bool boolValue,
        int intValue,
        string stringValue,
        out string detail)
    {
        detail = string.Empty;
        try
        {
            if (!File.Exists(settingsPath))
            {
                detail = "setting.json 不存在";
                return false;
            }

            Dictionary<string, object> root = DeserializeJsonObject(File.ReadAllText(settingsPath));
            if (root == null)
            {
                detail = "setting.json 未能解析为 JSON 对象";
                return false;
            }

            switch (kind)
            {
                case SettingChangeKind.ContextAware:
                    root["ContextAware"] = boolValue;
                    break;

                case SettingChangeKind.NumContexts:
                    root["NumContexts"] = intValue;
                    break;

                case SettingChangeKind.ModelName:
                    Dictionary<string, object> activeConfig = ResolveActiveOpenAiConfig(root);
                    if (activeConfig == null)
                    {
                        detail = "未找到 Configs.OpenAI[当前索引]";
                        return false;
                    }

                    activeConfig["ModelName"] = stringValue ?? string.Empty;
                    break;

                default:
                    detail = "未知的设置变更类型";
                    return false;
            }

            // Only the targeted field(s) above were mutated; every other field in `root` -- ApiKey,
            // Temperature, Prompt, TargetLanguage, ConfigIndices, and anything this reader does not
            // know about -- round-trips through the same in-memory graph unchanged. This is a
            // targeted-field write, not a hand-authored full-file rewrite; see the INTERFACE_INDEX
            // entry for setting.json for why this is narrower than "full file rewrite" in effect
            // even though the bytes on disk are fully replaced by WriteFileAtomically.
            string newJson = new JavaScriptSerializer().Serialize(root);
            WriteFileAtomically(settingsPath, newJson);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static Dictionary<string, object> DeserializeJsonObject(string json)
    {
        object parsed = new JavaScriptSerializer().DeserializeObject(json);
        return parsed as Dictionary<string, object>;
    }

    private static bool TryConvertToInt(object value, out int result)
    {
        result = 0;
        if (value is int)
        {
            result = (int)value;
            return true;
        }

        if (value is long)
        {
            result = (int)(long)value;
            return true;
        }

        if (value is double)
        {
            result = (int)(double)value;
            return true;
        }

        return false;
    }

    // Configs.OpenAI is an array of provider configs; ConfigIndices.OpenAI selects which entry is
    // active (currently always 0 in the live file, but this does not assume that stays true).
    private static Dictionary<string, object> ResolveActiveOpenAiConfig(Dictionary<string, object> root)
    {
        object configsValue;
        if (!root.TryGetValue("Configs", out configsValue))
        {
            return null;
        }

        Dictionary<string, object> configs = configsValue as Dictionary<string, object>;
        object openAiValue;
        if (configs == null || !configs.TryGetValue("OpenAI", out openAiValue))
        {
            return null;
        }

        object[] openAiList = openAiValue as object[];
        if (openAiList == null || openAiList.Length == 0)
        {
            return null;
        }

        int index = 0;
        object configIndicesValue;
        if (root.TryGetValue("ConfigIndices", out configIndicesValue))
        {
            Dictionary<string, object> configIndices = configIndicesValue as Dictionary<string, object>;
            object openAiIndexValue;
            int parsedIndex;
            if (configIndices != null &&
                configIndices.TryGetValue("OpenAI", out openAiIndexValue) &&
                TryConvertToInt(openAiIndexValue, out parsedIndex))
            {
                index = parsedIndex;
            }
        }

        if (index < 0 || index >= openAiList.Length)
        {
            index = 0;
        }

        return openAiList[index] as Dictionary<string, object>;
    }

    // Same atomic-replace shape as SecretStore's WriteSecret: write to a same-directory temp file,
    // then File.Replace (or File.Move if the target does not exist yet) so a crash mid-write can
    // never leave setting.json half-written for LiveCaptionsTranslator's next startup read.
    private static void WriteFileAtomically(string targetPath, string content)
    {
        string directory = Path.GetDirectoryName(targetPath);
        string tempPath = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(tempPath, content, SharedEncoding.Utf8NoBom);
            if (File.Exists(targetPath))
            {
                File.Replace(tempPath, targetPath, null);
            }
            else
            {
                File.Move(tempPath, targetPath);
            }
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    // ── Model discovery ─────────────────────────────────────────────────────────────────────────
    //
    // Enumerates %USERPROFILE%\.cache\geniex\models\<org>\<model>\geniex.json (verified against the
    // real cache layout on this machine while building this reader: two levels deep, one geniex.json
    // manifest per model with a top-level "Name" = "<org>/<model>" and a "ModelFile" map keyed by
    // quantization variant, e.g. "W4A16", each with a "Downloaded" bool). The full setting.json
    // ModelName value is "<Name>:<variant>" -- reconstructed here, never hardcoded, so a model
    // GenieX finishes downloading (or one that is removed) is picked up on the next refresh without
    // this reader needing to change. An in-progress download (Downloaded == false) is skipped.
    private static List<string> BuildAvailableModels()
    {
        List<string> models = new List<string>();
        try
        {
            string modelsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.Combine(".cache", Path.Combine("geniex", "models")));
            if (!Directory.Exists(modelsRoot))
            {
                return models;
            }

            string[] orgDirectories = Directory.GetDirectories(modelsRoot);
            for (int i = 0; i < orgDirectories.Length; i++)
            {
                string[] modelDirectories;
                try
                {
                    modelDirectories = Directory.GetDirectories(orgDirectories[i]);
                }
                catch (Exception ex)
                {
                    Program.LogException(ex);
                    continue;
                }

                for (int j = 0; j < modelDirectories.Length; j++)
                {
                    AddModelsFromManifest(Path.Combine(modelDirectories[j], GenieXModelManifestFileName), models);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        models.Sort(StringComparer.OrdinalIgnoreCase);
        return models;
    }

    private static void AddModelsFromManifest(string manifestPath, List<string> models)
    {
        if (!File.Exists(manifestPath))
        {
            return;
        }

        try
        {
            Dictionary<string, object> manifest = DeserializeJsonObject(File.ReadAllText(manifestPath));
            if (manifest == null)
            {
                return;
            }

            object nameValue;
            string name = manifest.TryGetValue("Name", out nameValue) ? nameValue as string : null;
            object modelFileValue;
            Dictionary<string, object> modelFile = manifest.TryGetValue("ModelFile", out modelFileValue)
                ? modelFileValue as Dictionary<string, object>
                : null;
            if (string.IsNullOrEmpty(name) || modelFile == null)
            {
                return;
            }

            foreach (KeyValuePair<string, object> variant in modelFile)
            {
                Dictionary<string, object> variantInfo = variant.Value as Dictionary<string, object>;
                object downloadedValue;
                bool downloaded = variantInfo != null &&
                    variantInfo.TryGetValue("Downloaded", out downloadedValue) &&
                    downloadedValue is bool &&
                    (bool)downloadedValue;
                if (downloaded)
                {
                    models.Add(name + ":" + variant.Key);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    // ── History read ────────────────────────────────────────────────────────────────────────────

    private static void ReadHistory(string databasePath, TranslatorControlSnapshot snapshot)
    {
        if (!File.Exists(databasePath))
        {
            snapshot.HistoryDatabaseFound = false;
            return;
        }

        snapshot.HistoryDatabaseFound = true;
        List<MinimalSqliteReader.Row> rows = MinimalSqliteReader.ReadLastRowsByRowIdDescending(databasePath, HistoryTableName, HistoryRowLimit);
        bool foundLastSuccess = false;
        for (int i = 0; i < rows.Count; i++)
        {
            MinimalSqliteReader.Row row = rows[i];
            // Column order: [0]=Id (INTEGER PRIMARY KEY AUTOINCREMENT -> NULL placeholder, see
            // MinimalSqliteReader.Row.Values), [1]=Timestamp, [2]=SourceText, [3]=TranslatedText,
            // [4]=TargetLanguage, [5]=ApiUsed (not surfaced by this board).
            if (row.Values == null || row.Values.Length < 5)
            {
                continue;
            }

            TranslatorHistoryEntry entry = new TranslatorHistoryEntry();
            entry.SourceText = row.Values[2] ?? string.Empty;
            entry.TranslatedText = row.Values[3] ?? string.Empty;
            entry.TargetLanguage = row.Values[4] ?? string.Empty;
            entry.IsError = entry.TranslatedText.StartsWith("[ERROR]", StringComparison.Ordinal);

            long unixSeconds;
            if (long.TryParse(row.Values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out unixSeconds))
            {
                entry.TimestampKnown = true;
                entry.TimestampLocal = UnixSecondsToLocal(unixSeconds);
                if (!entry.IsError && !foundLastSuccess)
                {
                    // Rows arrive newest-Id-first, so the first non-error row is the most recent
                    // success -- no need to scan for a maximum.
                    foundLastSuccess = true;
                    snapshot.LastSuccessKnown = true;
                    snapshot.LastSuccessLocal = entry.TimestampLocal;
                }
            }

            snapshot.RecentHistory.Add(entry);
        }
    }

    private static DateTime UnixSecondsToLocal(long unixSeconds)
    {
        DateTime utc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unixSeconds);
        return utc.ToLocalTime();
    }

    // ── Failure toast, debounced ────────────────────────────────────────────────────────────────
    //
    // The board's failure-alert strip (see CaptionsBoardForm.Layout.cs) reflects the newest history
    // row directly and immediately -- no debounce, since it is just showing current state. The toast
    // notification is a different concern (announcing a *change*), and reuses
    // Core/ServiceAlertDebouncer.cs's existing checking/10s-stable/immediate-recovery semantics so a
    // burst of consecutive translation failures produces at most one toast rather than one per
    // failure: candidates.Count is 0 or 1 (a single synthetic "translator:failure" service), and only
    // that debounced set's 0->1 rising edge fires ShowWindowsNotification.
    private void UpdateFailureAlert(TranslatorControlSnapshot snapshot)
    {
        List<ServiceAlertCandidate> candidates = new List<ServiceAlertCandidate>();
        if (snapshot.RecentHistory.Count > 0 && snapshot.RecentHistory[0].IsError)
        {
            candidates.Add(new ServiceAlertCandidate
            {
                Key = "translator:failure",
                Name = "字幕翻译",
                Reason = ExtractFailureReason(snapshot.RecentHistory[0].TranslatedText),
                State = "Error",
                Color = DesignTokens.Colors.Danger,
                Checking = false
            });
        }

        List<ServiceAlertCandidate> active = ServiceAlertDebouncer.Apply(
            this.alertStates,
            candidates,
            DateTime.UtcNow,
            TimeSpan.FromSeconds(FailureAlertDebounceSeconds),
            false);

        bool isActiveNow = active.Count > 0;
        if (isActiveNow && !this.lastFailureAlertActive)
        {
            NotifyFailure(active[0]);
        }

        this.lastFailureAlertActive = isActiveNow;
    }

    private void NotifyFailure(ServiceAlertCandidate candidate)
    {
        Action<string, string, ToolTipIcon> notify = this.showNotification;
        if (notify == null)
        {
            return;
        }

        try
        {
            notify("字幕翻译", "最近一次翻译失败：" + candidate.Reason, ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }
    }

    internal static string ExtractFailureReason(string translatedText)
    {
        string reason = (translatedText ?? string.Empty).Trim();
        const string prefix = "[ERROR]";
        if (reason.StartsWith(prefix, StringComparison.Ordinal))
        {
            reason = reason.Substring(prefix.Length).Trim();
        }

        if (reason.Length == 0)
        {
            return "未知错误";
        }

        return reason.Length > AlertReasonMaxLength
            ? reason.Substring(0, AlertReasonMaxLength) + "…"
            : reason;
    }

    internal static void RunSelfTest()
    {
        RunSettingsJsonMutationSelfTest();
        RunModelManifestSelfTest();
        RunFailureReasonSelfTest();
        Console.WriteLine("TranslatorControlReader: PASS settings JSON targeted mutation, model manifest parsing, failure reason extraction");
    }

    private static void RunSettingsJsonMutationSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-translator-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, SettingsFileName);
            // Mirrors the live setting.json shape exactly (field-for-field), including fields this
            // reader never looks at, to prove those survive a targeted mutation untouched.
            string original =
                "{\"ApiName\":\"OpenAI\",\"TargetLanguage\":\"zh-CN\",\"ContextAware\":true,\"NumContexts\":64," +
                "\"Prompt\":\"rules line1\\nrules line2\"," +
                "\"Configs\":{\"OpenAI\":[{\"ApiKey\":\"geniex-local\",\"ApiUrl\":\"http://127.0.0.1:18182/v1/chat/completions\"," +
                "\"ModelName\":\"qualcomm/Qwen3-4B-Instruct-2507:W4A16\",\"Temperature\":0.3}]}," +
                "\"ConfigIndices\":{\"OpenAI\":0}}";
            File.WriteAllText(path, original, SharedEncoding.Utf8NoBom);

            string detail;
            if (!TryMutateSettingsJson(path, SettingChangeKind.ContextAware, false, 0, null, out detail))
            {
                throw new InvalidOperationException("TranslatorControlReader ContextAware mutation self-test failed: " + detail);
            }

            TranslatorControlSnapshot snapshot = TranslatorControlSnapshot.CreateEmpty();
            ReadSettingsFile(path, snapshot);
            if (!snapshot.ContextAwareKnown || snapshot.ContextAware ||
                !snapshot.NumContextsKnown || snapshot.NumContexts != 64 ||
                !string.Equals(snapshot.ModelName, "qualcomm/Qwen3-4B-Instruct-2507:W4A16", StringComparison.Ordinal) ||
                !string.Equals(snapshot.ApiUrl, "http://127.0.0.1:18182/v1/chat/completions", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("TranslatorControlReader ContextAware mutation must leave NumContexts/ModelName/ApiUrl untouched.");
            }

            if (!TryMutateSettingsJson(path, SettingChangeKind.NumContexts, false, 32, null, out detail))
            {
                throw new InvalidOperationException("TranslatorControlReader NumContexts mutation self-test failed: " + detail);
            }

            if (!TryMutateSettingsJson(path, SettingChangeKind.ModelName, false, 0, "qualcomm/Qwen3-8B:W4A16", out detail))
            {
                throw new InvalidOperationException("TranslatorControlReader ModelName mutation self-test failed: " + detail);
            }

            snapshot = TranslatorControlSnapshot.CreateEmpty();
            ReadSettingsFile(path, snapshot);
            if (snapshot.ContextAware ||
                snapshot.NumContexts != 32 ||
                !string.Equals(snapshot.ModelName, "qualcomm/Qwen3-8B:W4A16", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("TranslatorControlReader sequential mutations did not all persist.");
            }

            // Fields this reader never edits (ApiKey, Temperature, Prompt, TargetLanguage,
            // ConfigIndices) must still be present with their original values -- this is the
            // "targeted field writes only, never a full-file rewrite" contract.
            string finalJson = File.ReadAllText(path);
            if (finalJson.IndexOf("geniex-local", StringComparison.Ordinal) < 0 ||
                finalJson.IndexOf("0.3", StringComparison.Ordinal) < 0 ||
                finalJson.IndexOf("rules line1", StringComparison.Ordinal) < 0 ||
                finalJson.IndexOf("\"TargetLanguage\":\"zh-CN\"", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("TranslatorControlReader mutation must preserve fields it does not target.");
            }

            if (Directory.GetFiles(root, "*.tmp").Length != 0)
            {
                throw new InvalidOperationException("TranslatorControlReader atomic write must not leave a temp file behind.");
            }

            string missingPath = Path.Combine(root, "missing.json");
            if (TryMutateSettingsJson(missingPath, SettingChangeKind.ContextAware, true, 0, null, out detail) || detail.Length == 0)
            {
                throw new InvalidOperationException("TranslatorControlReader must fail cleanly for a missing settings file.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void RunModelManifestSelfTest()
    {
        string root = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-translator-models-" + Guid.NewGuid().ToString("N"));
        string modelsRoot = Path.Combine(root, Path.Combine("qualcomm", "Qwen3-4B-Instruct-2507"));
        Directory.CreateDirectory(modelsRoot);
        try
        {
            // Mirrors the real geniex.json shape verified on this machine's model cache: a "Name",
            // and a "ModelFile" map keyed by quantization variant with a "Downloaded" flag.
            string manifest =
                "{\"Name\":\"qualcomm/Qwen3-4B-Instruct-2507\",\"ModelType\":\"llm\"," +
                "\"ModelFile\":{\"W4A16\":{\"Name\":\"part1_of_4.bin\",\"Downloaded\":true,\"Size\":778174464}}}";
            File.WriteAllText(Path.Combine(modelsRoot, GenieXModelManifestFileName), manifest, SharedEncoding.Utf8NoBom);

            List<string> models = new List<string>();
            AddModelsFromManifest(Path.Combine(modelsRoot, GenieXModelManifestFileName), models);
            if (models.Count != 1 || !string.Equals(models[0], "qualcomm/Qwen3-4B-Instruct-2507:W4A16", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("TranslatorControlReader model manifest self-test failed to reconstruct the full identifier.");
            }

            // An in-progress download (Downloaded=false) must not be offered as selectable yet.
            string inProgressManifest =
                "{\"Name\":\"qualcomm/Qwen3-8B\",\"ModelType\":\"llm\"," +
                "\"ModelFile\":{\"W4A16\":{\"Name\":\"part1_of_4.bin\",\"Downloaded\":false,\"Size\":0}}}";
            string inProgressDir = Path.Combine(root, Path.Combine("qualcomm", "Qwen3-8B"));
            Directory.CreateDirectory(inProgressDir);
            File.WriteAllText(Path.Combine(inProgressDir, GenieXModelManifestFileName), inProgressManifest, SharedEncoding.Utf8NoBom);
            List<string> modelsWithInProgress = new List<string>();
            AddModelsFromManifest(Path.Combine(inProgressDir, GenieXModelManifestFileName), modelsWithInProgress);
            if (modelsWithInProgress.Count != 0)
            {
                throw new InvalidOperationException("TranslatorControlReader must not offer an in-progress download as an available model.");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void RunFailureReasonSelfTest()
    {
        string reason = ExtractFailureReason("[ERROR] Translation Failed: connection refused");
        if (!string.Equals(reason, "Translation Failed: connection refused", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TranslatorControlReader failure reason self-test failed to strip the [ERROR] prefix. got=" + reason);
        }

        if (!string.Equals(ExtractFailureReason(string.Empty), "未知错误", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("TranslatorControlReader failure reason self-test failed for an empty message.");
        }

        string longReason = ExtractFailureReason("[ERROR] " + new string('x', 200));
        if (longReason.Length != AlertReasonMaxLength + 1) // +1 for the trailing ellipsis character
        {
            throw new InvalidOperationException("TranslatorControlReader failure reason self-test failed to truncate a long message.");
        }
    }
}
