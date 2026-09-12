using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

// Stable same-user CLI contract for GUARD. The one-shot CLI never owns a power request: it sends
// a bounded command to the long-lived UI process, where GuardRuntime preserves thread affinity and
// persists the result. The DACL, command allow-list and hour bounds are the security boundary.
internal sealed class GuardControlRequest
{
    public string Action = string.Empty;
    public int Hours;
    // Only meaningful for power_mode ("saver"|"balanced"|"performance"); empty for every other action.
    public string Mode = string.Empty;
}

internal sealed class GuardControlSnapshot
{
    public DateTime GeneratedAtUtc;
    public bool SleepEnabled;
    public DateTime SleepSinceUtc;
    public bool DisplayActive;
    public int DisplayHours;
    public DateTime DisplayUntilUtc;
    public int DisplayRemainingSeconds;
    public bool SystemPowerRequestActive;
    public bool ExecutionPowerRequestActive;
    public bool DisplayPowerRequestActive;
    public bool? OnAcPower;
    public string LastActionDetail = string.Empty;
    // Live-read at response time, so a caller sees the post-write reality rather than a cached
    // intent - the same "freshly built after the mutation" rule the other state fields follow.
    public string PowerModeCurrent = string.Empty;
    public bool EnergySaverActive;
    public bool EnergySaverForcedByGuard;
    public bool PowerScheduleActive;
    public int PowerScheduleHours;
    public DateTime PowerScheduleUntilUtc;
    public int PowerScheduleRemainingSeconds;
}

internal sealed class GuardControlResponse
{
    public bool Ok;
    public bool Changed;
    public string Action = string.Empty;
    public string ErrorCode = string.Empty;
    public string ErrorMessage = string.Empty;
    public GuardControlSnapshot State;
}

// Help is resolved before storage migration and before any IPC connection so it remains available
// when the resident host is stopped. Exact argument shapes also prevent a help token from silently
// suppressing another one-shot command in a mixed command line.
internal static class CommandLineHelp
{
    internal static bool IsGeneralHelpRequest(string[] args)
    {
        return args != null && args.Length == 1 && IsHelpToken(args[0], true);
    }

    internal static bool IsGuardHelpRequest(string[] args)
    {
        return args != null && args.Length == 2 &&
            string.Equals(args[0], "--guard", StringComparison.OrdinalIgnoreCase) &&
            IsHelpToken(args[1], true);
    }

    internal static string BuildGeneralHelp()
    {
        return string.Join(Environment.NewLine, new[]
        {
            ProductIdentity.DisplayName + " " + ProductIdentity.Version,
            "用法: DesktopCodexAssistant.exe <命令> [参数]",
            string.Empty,
            "帮助",
            "  help | --help | -h | /?                 显示本页（无需主程序运行）",
            "  --guard help                            显示 GUARD 完整控制说明",
            string.Empty,
            "本机数据与控制",
            "  --balances                              输出 Codex / Claude / DeepSeek 余额 JSON",
            "  --guard status                          查询 GUARD 状态 JSON",
            "  --guard sleep <on|off>                  开关防睡眠",
            "  --guard display <start [1..24]|stop>    启停亮屏计时",
            "  --guard display hours <1..24>           设置亮屏小时预设",
            "  --guard power mode <saver|balanced|performance>  快速切换电源模式",
            "  --guard power saver <on|off>            强制开关省电模式",
            "  --guard power schedule <start [1..24]|stop>      定时电源模式",
            string.Empty,
            "程序管理",
            "  --install [--no-start]                  安装开机启动",
            "  --uninstall                             移除开机启动",
            "  --stop                                  停止常驻实例",
            string.Empty,
            "诊断与开发",
            "  --test                                  运行完整自检",
            "  --test-layout | --test-settings-bindings | --test-display-recovery",
            "  --diagnose-idle-cpu | --diagnose-radar-runtime | --dump-codex-tasks",
            "  --render-networkmonitor | --render-tilecolumn | --render-operation",
            "  --render-specboard | --render-specboardmanager | --render-guard",
            "  --render-resetspeedboard | --render-systemdayboard",
            string.Empty,
            "GUARD 文档: Docs\\Guard-CLI.md"
        });
    }

    internal static string BuildGuardHelp()
    {
        return string.Join(Environment.NewLine, new[]
        {
            "GUARD CLI - 睡眠防护、亮屏计时与电源模式代理控制",
            "用法: DesktopCodexAssistant.exe --guard <命令>",
            string.Empty,
            "命令",
            "  --guard help                            显示本页；主程序可不运行",
            "  --guard status                          查询状态，不修改设置",
            "  --guard sleep on                        开启防睡眠",
            "  --guard sleep off                       关闭防睡眠",
            "  --guard display start                   按已保存的小时预设启动亮屏",
            "  --guard display start <1..24>           设置预设并启动亮屏",
            "  --guard display stop                    停止亮屏；不改变防睡眠",
            "  --guard display hours <1..24>           只修改小时预设",
            "  --guard power mode <saver|balanced|performance>",
            "                                           立即切换电源模式，不会自动还原",
            "  --guard power saver on                  强制开启省电模式（改写自动阈值）",
            "  --guard power saver off                 关闭强制省电模式，恢复原自动阈值",
            "  --guard power schedule start             按已保存的小时预设锁定当前电源模式",
            "  --guard power schedule start <1..24>     设置预设并锁定当前电源模式",
            "  --guard power schedule stop              取消定时，不改变当前电源模式",
            string.Empty,
            "行为",
            "  sleep 与 display 相互独立；display start 不会自动 sleep on。",
            "  power mode 立即切换且会取消尚未到期的 power schedule，因为手动切换应当覆盖旧定时。",
            "  power schedule start 不改变当前电源模式，只是把它锁定 N 小时，到点固定恢复至平衡；",
            "  如需锁定某个具体模式，先执行 power mode 再执行 power schedule start。",
            "  power saver 强制的是 Windows 省电模式的自动开启阈值（ESBATTTHRESHOLD），只在电池供电",
            "  时才会实际生效，这是 Windows 省电策略本身的限制，接通电源时开关会成功但不会显示为省电中。",
            "  除 help 外，命令要求同一 Windows 用户的常驻实例正在运行。",
            "  成功结果写 stdout；错误 JSON 写 stderr。所有协议响应为单行 JSON。",
            string.Empty,
            "退出码",
            "  0  成功",
            "  2  参数错误、主程序不可用或管道错误",
            "  3  主程序收到请求但拒绝或执行失败",
            string.Empty,
            "自动化提示",
            "  修改后再次执行 --guard status，并检查 sleep_power_request_ready",
            "  或 display_power_request_ready；不要只把开关值当作 OS 请求已生效。",
            "  power_mode_current 与 energy_saver_active 均为调用后即时重读的实际状态，",
            "  而非请求是否被接受的镜像；请以它们判断电源模式或省电模式是否真的生效。",
            string.Empty,
            "完整字段与调用示例: Docs\\Guard-CLI.md"
        });
    }

    internal static void RunSelfTest()
    {
        if (!IsGeneralHelpRequest(new[] { "help" }) ||
            !IsGeneralHelpRequest(new[] { "--help" }) ||
            !IsGeneralHelpRequest(new[] { "-h" }) ||
            !IsGeneralHelpRequest(new[] { "/?" }) ||
            IsGeneralHelpRequest(new[] { "--stop", "--help" }))
            throw new InvalidOperationException("General CLI help routing failed.");
        if (!IsGuardHelpRequest(new[] { "--guard", "help" }) ||
            !IsGuardHelpRequest(new[] { "--guard", "--help" }) ||
            IsGuardHelpRequest(new[] { "--guard", "help", "extra" }))
            throw new InvalidOperationException("GUARD CLI help routing failed.");
        string general = BuildGeneralHelp();
        string guard = BuildGuardHelp();
        if (general.IndexOf("--balances", StringComparison.Ordinal) < 0 ||
            general.IndexOf("--guard help", StringComparison.Ordinal) < 0 ||
            general.IndexOf("power mode", StringComparison.Ordinal) < 0 ||
            guard.IndexOf("display_power_request_ready", StringComparison.Ordinal) < 0 ||
            guard.IndexOf("1..24", StringComparison.Ordinal) < 0 ||
            guard.IndexOf("power mode <saver|balanced|performance>", StringComparison.Ordinal) < 0 ||
            guard.IndexOf("power saver", StringComparison.Ordinal) < 0 ||
            guard.IndexOf("power schedule start", StringComparison.Ordinal) < 0 ||
            guard.IndexOf("power_mode_current", StringComparison.Ordinal) < 0)
            throw new InvalidOperationException("CLI help content is incomplete.");
        Console.WriteLine("Command-line help: PASS aliases, routing, documented GUARD readiness");
    }

    private static bool IsHelpToken(string value, bool allowBareHelp)
    {
        string token = (value ?? string.Empty).Trim();
        return (allowBareHelp && string.Equals(token, "help", StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(token, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "-h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(token, "/?", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class GuardControlProtocol
{
    internal const int SchemaVersion = 1;
    internal const int MinDisplayHours = 1;
    internal const int MaxDisplayHours = 24;
    private const string PipePrefix = ProductIdentity.MachineName + ".GuardControl.v1.";

    internal static string CurrentUserPipeName { get { return PipePrefix + CurrentUserKey(); } }

    internal static bool TryParseArguments(string[] args, out GuardControlRequest request, out string error)
    {
        request = null;
        error = string.Empty;
        int index = Array.FindIndex(args ?? new string[0], value =>
            string.Equals(value, "--guard", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length)
        {
            error = "Usage: --guard status | sleep <on|off> | display <start [hours]|stop|hours N> | " +
                "power <mode <saver|balanced|performance>|saver <on|off>|schedule <start [hours]|stop>>";
            return false;
        }

        string group = args[index + 1].Trim().ToLowerInvariant();
        if (group == "status")
        {
            request = new GuardControlRequest { Action = "status" };
            return index + 2 == args.Length || FailExtra(out request, out error);
        }

        if (group == "sleep" && index + 2 < args.Length)
        {
            string state = args[index + 2].Trim().ToLowerInvariant();
            if ((state == "on" || state == "off") && index + 3 == args.Length)
            {
                request = new GuardControlRequest { Action = "sleep_" + state };
                return true;
            }
        }

        if (group == "display" && index + 2 < args.Length)
        {
            string verb = args[index + 2].Trim().ToLowerInvariant();
            if ((verb == "stop" || verb == "off") && index + 3 == args.Length)
            {
                request = new GuardControlRequest { Action = "display_stop" };
                return true;
            }

            if (verb == "start" || verb == "on")
            {
                int hours = 0;
                if (index + 3 == args.Length ||
                    (index + 4 == args.Length && TryParseHours(args[index + 3], out hours)))
                {
                    request = new GuardControlRequest { Action = "display_start", Hours = hours };
                    return true;
                }
            }

            if ((verb == "hours" || verb == "set-hours") && index + 4 == args.Length)
            {
                int hours;
                if (TryParseHours(args[index + 3], out hours))
                {
                    request = new GuardControlRequest { Action = "display_hours", Hours = hours };
                    return true;
                }
            }
        }

        if (group == "power" && index + 2 < args.Length)
        {
            string sub = args[index + 2].Trim().ToLowerInvariant();
            if (sub == "mode" && index + 4 == args.Length)
            {
                string mode = args[index + 3].Trim().ToLowerInvariant();
                if (mode == "saver" || mode == "balanced" || mode == "performance")
                {
                    request = new GuardControlRequest { Action = "power_mode", Mode = mode };
                    return true;
                }
            }

            if (sub == "saver" && index + 4 == args.Length)
            {
                string state = args[index + 3].Trim().ToLowerInvariant();
                if (state == "on" || state == "off")
                {
                    request = new GuardControlRequest { Action = "energy_saver_" + state };
                    return true;
                }
            }

            if (sub == "schedule" && index + 3 < args.Length)
            {
                string verb = args[index + 3].Trim().ToLowerInvariant();
                if (verb == "stop" && index + 4 == args.Length)
                {
                    request = new GuardControlRequest { Action = "power_schedule_stop" };
                    return true;
                }

                if (verb == "start")
                {
                    int hours = 0;
                    if (index + 4 == args.Length ||
                        (index + 5 == args.Length && TryParseHours(args[index + 4], out hours)))
                    {
                        request = new GuardControlRequest { Action = "power_schedule_start", Hours = hours };
                        return true;
                    }
                }
            }
        }

        error = "Invalid GUARD command. Hours must be an integer from 1 to 24.";
        return false;
    }

    private static bool FailExtra(out GuardControlRequest request, out string error)
    {
        request = null;
        error = "Unexpected arguments after --guard status.";
        return false;
    }

    private static bool TryParseHours(string value, out int hours)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out hours) &&
            hours >= MinDisplayHours && hours <= MaxDisplayHours;
    }

    internal static string SerializeRequest(GuardControlRequest request)
    {
        Dictionary<string, object> data = new Dictionary<string, object>();
        data["schema_version"] = SchemaVersion;
        data["action"] = request == null ? string.Empty : request.Action;
        if (request != null && request.Hours > 0) data["hours"] = request.Hours;
        if (request != null && !string.IsNullOrEmpty(request.Mode)) data["mode"] = request.Mode;
        return new JavaScriptSerializer().Serialize(data);
    }

    internal static bool TryDeserializeRequest(string json, out GuardControlRequest request)
    {
        request = null;
        try
        {
            Dictionary<string, object> data = new JavaScriptSerializer().DeserializeObject(json)
                as Dictionary<string, object>;
            if (data == null || ReadInt(data, "schema_version") != SchemaVersion) return false;
            string action = ReadString(data, "action");
            string mode = ReadString(data, "mode");
            int hours;
            if (!TryReadWholeNumber(data, "hours", out hours)) return false;
            bool allowed = action == "status" || action == "sleep_on" || action == "sleep_off" ||
                action == "display_stop" || action == "display_start" || action == "display_hours" ||
                action == "power_mode" || action == "energy_saver_on" || action == "energy_saver_off" ||
                action == "power_schedule_start" || action == "power_schedule_stop";
            bool hoursInRange = hours >= MinDisplayHours && hours <= MaxDisplayHours;
            bool hoursValid = action == "display_hours"
                ? hoursInRange
                : (action == "display_start" || action == "power_schedule_start") ? hours == 0 || hoursInRange : hours == 0;
            bool modeValid = action == "power_mode"
                ? (mode == "saver" || mode == "balanced" || mode == "performance")
                : string.IsNullOrEmpty(mode);
            if (!allowed || !hoursValid || !modeValid) return false;
            request = new GuardControlRequest { Action = action, Hours = hours, Mode = mode };
            return true;
        }
        catch { return false; }
    }

    internal static string SerializeResponse(GuardControlResponse response)
    {
        GuardControlResponse value = response ?? Error("INTERNAL_ERROR", "No response was produced.");
        Dictionary<string, object> root = new Dictionary<string, object>();
        root["schema_version"] = SchemaVersion;
        root["product"] = ProductIdentity.MachineName;
        root["product_version"] = ProductIdentity.Version;
        root["ok"] = value.Ok;
        root["changed"] = value.Changed;
        root["action"] = value.Action;
        if (!value.Ok)
        {
            root["error"] = new Dictionary<string, object>
            {
                { "code", value.ErrorCode }, { "message", value.ErrorMessage }
            };
        }
        if (value.State != null) root["state"] = BuildState(value.State);
        return new JavaScriptSerializer().Serialize(root);
    }

    internal static GuardControlResponse Error(string code, string message)
    {
        return new GuardControlResponse { Ok = false, ErrorCode = code, ErrorMessage = message };
    }

    internal static bool IsSuccessfulResponse(string json)
    {
        try
        {
            Dictionary<string, object> root = new JavaScriptSerializer().DeserializeObject(json)
                as Dictionary<string, object>;
            return root != null && ReadInt(root, "schema_version") == SchemaVersion &&
                root.ContainsKey("ok") && Convert.ToBoolean(root["ok"], CultureInfo.InvariantCulture);
        }
        catch { return false; }
    }

    private static Dictionary<string, object> BuildState(GuardControlSnapshot state)
    {
        return new Dictionary<string, object>
        {
            { "generated_at_utc", FormatUtc(state.GeneratedAtUtc) },
            { "sleep_enabled", state.SleepEnabled },
            { "sleep_since_utc", state.SleepSinceUtc == DateTime.MinValue ? null : (object)FormatUtc(state.SleepSinceUtc) },
            { "sleep_power_request_ready", !state.SleepEnabled ||
                (state.SystemPowerRequestActive && state.ExecutionPowerRequestActive) },
            { "display_active", state.DisplayActive },
            { "display_hours", state.DisplayHours },
            { "display_until_utc", state.DisplayUntilUtc == DateTime.MinValue ? null : (object)FormatUtc(state.DisplayUntilUtc) },
            { "display_remaining_seconds", state.DisplayRemainingSeconds },
            { "display_power_request_ready", !state.DisplayActive || state.DisplayPowerRequestActive },
            { "system_power_request_active", state.SystemPowerRequestActive },
            { "execution_power_request_active", state.ExecutionPowerRequestActive },
            { "display_power_request_active", state.DisplayPowerRequestActive },
            { "on_ac_power", state.OnAcPower.HasValue ? (object)state.OnAcPower.Value : null },
            { "last_action_detail", string.IsNullOrWhiteSpace(state.LastActionDetail) ? null : (object)state.LastActionDetail },
            { "power_mode_current", state.PowerModeCurrent },
            { "energy_saver_active", state.EnergySaverActive },
            { "energy_saver_forced_by_guard", state.EnergySaverForcedByGuard },
            { "power_schedule_active", state.PowerScheduleActive },
            { "power_schedule_hours", state.PowerScheduleHours },
            { "power_schedule_until_utc", state.PowerScheduleUntilUtc == DateTime.MinValue ? null : (object)FormatUtc(state.PowerScheduleUntilUtc) },
            { "power_schedule_remaining_seconds", state.PowerScheduleRemainingSeconds }
        };
    }

    private static string CurrentUserKey()
    {
        string value = string.Empty;
        try { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) value = id.User == null ? "" : id.User.Value; }
        catch { }
        if (string.IsNullOrWhiteSpace(value)) value = Environment.UserName ?? "user";
        StringBuilder result = new StringBuilder();
        foreach (char c in value) result.Append(char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_');
        return result.Length == 0 ? "user" : result.ToString();
    }

    private static string FormatUtc(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string ReadString(Dictionary<string, object> data, string key)
    {
        object value;
        return data.TryGetValue(key, out value) && value != null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
    }

    private static int ReadInt(Dictionary<string, object> data, string key)
    {
        object value;
        try { return data.TryGetValue(key, out value) && value != null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : 0; }
        catch { return 0; }
    }

    private static bool TryReadWholeNumber(Dictionary<string, object> data, string key, out int value)
    {
        value = 0;
        object raw;
        if (!data.TryGetValue(key, out raw) || raw == null) return true;
        try
        {
            decimal number = Convert.ToDecimal(raw, CultureInfo.InvariantCulture);
            if (number != decimal.Truncate(number) || number < int.MinValue || number > int.MaxValue)
                return false;
            value = decimal.ToInt32(number);
            return true;
        }
        catch { return false; }
    }

    internal static void RunSelfTest()
    {
        GuardControlRequest request;
        string error;
        AssertParsed(new[] { "--guard", "status" }, "status", 0);
        AssertParsed(new[] { "--guard", "sleep", "on" }, "sleep_on", 0);
        AssertParsed(new[] { "--guard", "sleep", "off" }, "sleep_off", 0);
        AssertParsed(new[] { "--guard", "display", "start" }, "display_start", 0);
        AssertParsed(new[] { "--guard", "display", "start", "6" }, "display_start", 6);
        AssertParsed(new[] { "--guard", "display", "stop" }, "display_stop", 0);
        AssertParsed(new[] { "--guard", "display", "hours", "1" }, "display_hours", 1);
        AssertParsed(new[] { "--guard", "display", "set-hours", "24" }, "display_hours", 24);
        if (TryParseArguments(new[] { "--guard", "display", "hours", "25" }, out request, out error))
            throw new InvalidOperationException("GUARD CLI hour bound failed.");
        if (TryParseArguments(new[] { "--guard", "display", "hours", "0" }, out request, out error) ||
            TryParseArguments(new[] { "--guard", "display", "start", "1.5" }, out request, out error) ||
            TryParseArguments(new[] { "--guard", "status", "extra" }, out request, out error))
            throw new InvalidOperationException("GUARD CLI invalid argument rejection failed.");
        if (TryDeserializeRequest("{\"schema_version\":1,\"action\":\"display_start\",\"hours\":-1}", out request))
            throw new InvalidOperationException("GUARD CLI request bounds failed.");
        if (TryDeserializeRequest("{\"schema_version\":1,\"action\":\"display_start\",\"hours\":1.5}", out request))
            throw new InvalidOperationException("GUARD CLI request integer validation failed.");

        AssertParsed(new[] { "--guard", "power", "mode", "saver" }, "power_mode", 0);
        AssertParsed(new[] { "--guard", "power", "mode", "performance" }, "power_mode", 0);
        if (!TryParseArguments(new[] { "--guard", "power", "mode", "balanced" }, out request, out error) ||
            request.Mode != "balanced")
            throw new InvalidOperationException("GUARD CLI power mode parsing failed.");
        AssertParsed(new[] { "--guard", "power", "saver", "on" }, "energy_saver_on", 0);
        AssertParsed(new[] { "--guard", "power", "saver", "off" }, "energy_saver_off", 0);
        AssertParsed(new[] { "--guard", "power", "schedule", "start" }, "power_schedule_start", 0);
        AssertParsed(new[] { "--guard", "power", "schedule", "start", "3" }, "power_schedule_start", 3);
        AssertParsed(new[] { "--guard", "power", "schedule", "stop" }, "power_schedule_stop", 0);
        if (TryParseArguments(new[] { "--guard", "power", "mode", "turbo" }, out request, out error) ||
            TryParseArguments(new[] { "--guard", "power", "saver", "maybe" }, out request, out error) ||
            TryParseArguments(new[] { "--guard", "power", "schedule", "start", "25" }, out request, out error))
            throw new InvalidOperationException("GUARD CLI power command rejection failed.");
        if (TryDeserializeRequest("{\"schema_version\":1,\"action\":\"power_mode\",\"hours\":0,\"mode\":\"turbo\"}", out request))
            throw new InvalidOperationException("GUARD CLI power mode value validation failed.");
        if (TryDeserializeRequest("{\"schema_version\":1,\"action\":\"energy_saver_on\",\"hours\":0,\"mode\":\"saver\"}", out request))
            throw new InvalidOperationException("GUARD CLI unexpected mode rejection failed.");

        string pipe = ProductIdentity.MachineName + ".GuardControl.Test." + Guid.NewGuid().ToString("N");
        using (GuardControlServer server = new GuardControlServer(pipe, delegate(GuardControlRequest input)
        {
            return new GuardControlResponse { Ok = true, Changed = input.Action != "status",
                Action = input.Action, State = new GuardControlSnapshot { GeneratedAtUtc = DateTime.UtcNow,
                DisplayHours = 5 } };
        }))
        {
            server.Start();
            string json;
            string code;
            if (!GuardControlClient.TrySend(pipe, new GuardControlRequest { Action = "sleep_on" },
                2000, out json, out code) || !IsSuccessfulResponse(json))
                throw new InvalidOperationException("GUARD CLI pipe round-trip failed: " + code);
        }
        Console.WriteLine("GUARD control CLI: PASS parser, bounds, same-user pipe, stable JSON");
    }

    private static void AssertParsed(string[] args, string action, int hours)
    {
        GuardControlRequest request;
        string error;
        if (!TryParseArguments(args, out request, out error) || request == null ||
            request.Action != action || request.Hours != hours)
            throw new InvalidOperationException("GUARD CLI parser failed for " + string.Join(" ", args) + ".");
    }
}

internal sealed class GuardControlServer : IDisposable
{
    private readonly string pipeName;
    private readonly Func<GuardControlRequest, GuardControlResponse> handler;
    private volatile bool stopping;
    private Task worker;

    internal GuardControlServer(string pipeName, Func<GuardControlRequest, GuardControlResponse> handler)
    { this.pipeName = pipeName; this.handler = handler; }

    internal void Start()
    {
        if (this.worker != null) return;
        this.worker = Task.Factory.StartNew(Run, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Dispose()
    {
        this.stopping = true;
        try
        {
            using (NamedPipeClientStream wake = new NamedPipeClientStream(".", this.pipeName,
                PipeDirection.InOut, PipeOptions.None)) { wake.Connect(250); }
        }
        catch { }
        try { if (this.worker != null) this.worker.Wait(2000); } catch { }
        this.worker = null;
    }

    private void Run()
    {
        while (!this.stopping)
        {
            try
            {
                using (NamedPipeServerStream pipe = CreatePipe())
                {
                    pipe.WaitForConnection();
                    if (this.stopping) continue;
                    using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                    using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                    {
                        string line = reader.ReadLine();
                        GuardControlRequest request;
                        GuardControlResponse response = GuardControlProtocol.TryDeserializeRequest(line, out request)
                            ? this.handler(request)
                            : GuardControlProtocol.Error("INVALID_REQUEST", "The GUARD request is invalid.");
                        writer.WriteLine(GuardControlProtocol.SerializeResponse(response));
                        writer.Flush();
                    }
                }
            }
            catch (Exception ex)
            {
                if (!this.stopping) { Program.LogException(ex); Thread.Sleep(100); }
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        SecurityIdentifier sid;
        using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) sid = identity.User;
        if (sid == null) throw new UnauthorizedAccessException("Current user SID is unavailable.");
        PipeSecurity security = new PipeSecurity();
        security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(this.pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, security);
    }
}

internal static class GuardControlClient
{
    internal static bool TrySendCurrent(GuardControlRequest request, int timeout, out string json, out string code)
    { return TrySend(GuardControlProtocol.CurrentUserPipeName, request, timeout, out json, out code); }

    internal static bool TrySend(string name, GuardControlRequest request, int timeout,
        out string json, out string code)
    {
        json = string.Empty; code = string.Empty;
        try
        {
            int bounded = Math.Max(100, Math.Min(10000, timeout));
            using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", name,
                PipeDirection.InOut, PipeOptions.None))
            {
                pipe.Connect(bounded);
                using (StreamWriter writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true))
                using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true))
                {
                    writer.WriteLine(GuardControlProtocol.SerializeRequest(request)); writer.Flush();
                    Task<string> read = reader.ReadLineAsync();
                    if (!read.Wait(bounded)) { code = "PIPE_TIMEOUT"; return false; }
                    json = read.Result ?? string.Empty;
                }
            }
            if (string.IsNullOrWhiteSpace(json)) { code = "INVALID_RESPONSE"; return false; }
            return true;
        }
        catch (TimeoutException) { code = "HOST_UNAVAILABLE"; return false; }
        catch (UnauthorizedAccessException) { code = "ACCESS_DENIED"; return false; }
        catch (IOException) { code = "PIPE_ERROR"; return false; }
        catch { code = "PIPE_ERROR"; return false; }
    }
}
