using System;

internal static class SpecBoardStatus
{
    public const string Pending = "pending";
    public const string AwaitingVerify = "awaiting_verify";
    public const string Done = "done";
    public const string Abandoned = "abandoned";
    public const string NeedsRevision = "needs_revision";
    public const string Unregistered = "unregistered";

    public static readonly string[] LedgerValues =
    {
        Pending,
        NeedsRevision,
        AwaitingVerify,
        Done,
        Abandoned
    };

    public static bool IsLedgerValue(string value)
    {
        for (int i = 0; i < LedgerValues.Length; i++)
        {
            if (string.Equals(LedgerValues[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static string DisplayName(string value)
    {
        if (string.Equals(value, Pending, StringComparison.OrdinalIgnoreCase)) return "未执行";
        if (string.Equals(value, NeedsRevision, StringComparison.OrdinalIgnoreCase)) return "需要修改";
        if (string.Equals(value, AwaitingVerify, StringComparison.OrdinalIgnoreCase)) return "待验证";
        if (string.Equals(value, Done, StringComparison.OrdinalIgnoreCase)) return "完成";
        if (string.Equals(value, Abandoned, StringComparison.OrdinalIgnoreCase)) return "中断";
        if (string.Equals(value, Unregistered, StringComparison.OrdinalIgnoreCase)) return "未登记";
        return value ?? string.Empty;
    }

    public static string EventField(string value)
    {
        if (string.Equals(value, Pending, StringComparison.OrdinalIgnoreCase)) return "registered_utc";
        if (string.Equals(value, NeedsRevision, StringComparison.OrdinalIgnoreCase)) return "revision_requested_utc";
        if (string.Equals(value, AwaitingVerify, StringComparison.OrdinalIgnoreCase)) return "executed_utc";
        if (string.Equals(value, Done, StringComparison.OrdinalIgnoreCase)) return "verified_utc";
        return "abandoned_utc";
    }
}
