using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

// Data model shared by the right-edge tile column and its hover-expanded detail window
// (canonical edge tile column, 1.0.6.09).
//
// The column and the expand window are two separate layered windows that must always agree on what
// they show, so neither samples anything: WidgetForm pushes one MetricTileFeed per control tick and
// both windows render from that. The feed carries the same PerfSnapshot, history buffers, power
// strip snapshot and guard state the classic dense-grid panel reads, which is what keeps the two
// presentation modes numerically identical.
internal enum MetricTileId
{
    Cpu,
    Memory,
    Disk,
    Network,
    Gpu,
    Npu,
    Power,
    Guard,
    // Quota tiles are permanent members of the same eleven-tile column.
    // The model-quality (IQ) tiles were retired in favour of the left-docked Codex IQ board, so only
    // Codex/Claude quota and the restored DeepSeek balance tile remain here.
    CodexQuota,
    ClaudeQuota,
    DeepSeekQuota
}

internal enum QuotaEasterEggVisual
{
    None,
    FallenLookElsewhere,
    FallenTogether,
    Revived
}

internal sealed class QuotaEasterEggSnapshot
{
    public bool Enabled;
    public bool CodexEmpty;
    public bool ClaudeEmpty;
}

// Process-lifetime transition tracker. A refill is announced only when a family was actually seen
// empty earlier in this run, and the pending announcement is consumed by the first hover-open of
// that family's expanded panel. Repeated feed construction cannot retrigger the same resurrection.
internal sealed class QuotaEasterEggTracker
{
    private bool initialized;
    private bool previousCodexEmpty;
    private bool previousClaudeEmpty;
    private bool previousCodexKnown;
    private bool previousClaudeKnown;
    private bool codexRevivalPending;
    private bool claudeRevivalPending;
    private QuotaEasterEggSnapshot current = new QuotaEasterEggSnapshot();

    public QuotaEasterEggSnapshot Update(
        bool enabled,
        RadarTileSnapshot codex,
        RadarTileSnapshot claude)
    {
        bool codexEmpty = MetricTileModel.IsQuotaEmpty(codex);
        bool claudeEmpty = MetricTileModel.IsQuotaEmpty(claude);
        bool codexKnown = codex != null && codex.QuotaKnown;
        bool claudeKnown = claude != null && claude.QuotaKnown;
        if (!enabled)
        {
            this.initialized = false;
            this.codexRevivalPending = false;
            this.claudeRevivalPending = false;
        }
        else if (!this.initialized)
        {
            this.initialized = true;
            this.previousCodexEmpty = codexEmpty;
            this.previousClaudeEmpty = claudeEmpty;
            this.previousCodexKnown = codexKnown;
            this.previousClaudeKnown = claudeKnown;
        }
        else
        {
            if (this.previousCodexKnown && this.previousCodexEmpty && !codexEmpty)
            {
                this.codexRevivalPending = true;
            }

            if (this.previousClaudeKnown && this.previousClaudeEmpty && !claudeEmpty)
            {
                this.claudeRevivalPending = true;
            }

            if (codexEmpty)
            {
                this.codexRevivalPending = false;
            }

            if (claudeEmpty)
            {
                this.claudeRevivalPending = false;
            }

            this.previousCodexEmpty = codexEmpty;
            this.previousClaudeEmpty = claudeEmpty;
            this.previousCodexKnown = codexKnown;
            this.previousClaudeKnown = claudeKnown;
        }

        this.current = new QuotaEasterEggSnapshot
        {
            Enabled = enabled,
            CodexEmpty = codexEmpty,
            ClaudeEmpty = claudeEmpty
        };
        return CloneCurrent();
    }

    public bool TryConsumeRevival(MetricTileId id)
    {
        if (!this.current.Enabled)
        {
            return false;
        }

        if (id == MetricTileId.CodexQuota && !this.current.CodexEmpty && this.codexRevivalPending)
        {
            this.codexRevivalPending = false;
            return true;
        }

        if (id == MetricTileId.ClaudeQuota && !this.current.ClaudeEmpty && this.claudeRevivalPending)
        {
            this.claudeRevivalPending = false;
            return true;
        }

        return false;
    }

    private QuotaEasterEggSnapshot CloneCurrent()
    {
        return new QuotaEasterEggSnapshot
        {
            Enabled = this.current.Enabled,
            CodexEmpty = this.current.CodexEmpty,
            ClaudeEmpty = this.current.ClaudeEmpty
        };
    }
}

// One guard row: the same four guards the classic panel's badge strip carries, in the same order,
// so the tile's 2x2 dot pad and the expanded list cannot drift apart.
internal sealed class MetricTileGuardEntry
{
    public string Label = string.Empty;
    public string Description = string.Empty;
    public bool Active;
    public string Detail = string.Empty;
    public Color Accent;
}

// Snapshot handed to the tile windows. Every list is a reference to WidgetForm's live history
// buffer: both windows only read them, and only on the UI thread that mutates them, so no copy is
// taken. Null lists are tolerated by every renderer.
internal sealed class MetricTileFeed
{
    public PerfSnapshot Snapshot = new PerfSnapshot();
    public List<double> CpuHistory;
    public List<double> MemoryHistory;
    public List<double> MemoryHardwareReservedHistory;
    public List<MemoryPressureHistoryPoint> MemoryPressureHistory;
    public List<double> DiskWriteHistory;
    public List<double> DiskReadHistory;
    public List<double> NetworkSentHistory;
    public List<double> NetworkReceivedHistory;
    public List<double> GpuHistory;
    public List<double> GpuMemoryHistory;
    public List<double> NpuHistory;
    public List<double> NpuMemoryHistory;
    public PowerStripSnapshot Power;
    public SystemDayBoardSnapshot PowerDay;
    public List<MetricTileGuardEntry> Guards = new List<MetricTileGuardEntry>();
    public bool AlertTestEnabled;
    public bool NpuAlertIconActive;
    // Radar families. Null when the Radar window is not available yet; every renderer tolerates it.
    public RadarTileSnapshot CodexRadar;
    public RadarTileSnapshot ClaudeRadar;
    public DeepSeekBalanceSnapshot DeepSeekBalance;
    public DeepSeekServiceSnapshot DeepSeekService;
    public QuotaEasterEggSnapshot QuotaEasterEgg = new QuotaEasterEggSnapshot();

    public RadarTileSnapshot GetRadar(bool claude)
    {
        RadarTileSnapshot s = claude ? this.ClaudeRadar : this.CodexRadar;
        return s ?? RadarTileSnapshot.CreateEmpty(claude ? CodexRadarSoftwareMode.Claude : CodexRadarSoftwareMode.Codex);
    }

    public DeepSeekBalanceSnapshot GetDeepSeekBalance()
    {
        return this.DeepSeekBalance == null ? DeepSeekBalanceSnapshot.CreateEmpty() : this.DeepSeekBalance;
    }

    public DeepSeekServiceSnapshot GetDeepSeekService()
    {
        return this.DeepSeekService == null ? DeepSeekServiceSnapshot.CreateUnknown() : this.DeepSeekService;
    }
}

// What one 60x60 tile draws: a label, up to two concentric rings and one centre number.
// OuterPercent/InnerPercent below zero mean "no ring", which is how the guard tile suppresses both
// and draws its dot pad instead.
internal sealed class MetricTileData
{
    public MetricTileId Id;
    public string Label = string.Empty;
    public Color Accent;
    public Color InnerAccent;
    public double OuterPercent = -1.0;
    public double InnerPercent = -1.0;
    public string CenterValue = string.Empty;
    public string CenterSuffix = string.Empty;
    // Alert drives the tile's own red treatment (outer ring + centre number), replacing the classic
    // panel's red card background — a 60x60 tile has no background area worth tinting.
    public double AlertPercent;
    public bool AlertIconVisible;
    // Guard tile only: the four dots, in the same order as MetricTileFeed.Guards.
    public List<MetricTileGuardEntry> Guards;
    public QuotaEasterEggVisual EasterEggVisual;
    public string EasterEggSecondLine = string.Empty;
}

internal static class MetricTileModel
{
    // Metric tiles only. Radar tiles live in RadarOrder and are counted separately, because the two
    // groups are switched on by independent settings.
    internal const int TileCount = 8;
    internal const int RadarTileCount = 3;
    internal const int AllTileCount = TileCount + RadarTileCount;

    // Fixed order top-to-bottom. Unlike the classic panel this does not follow MetricOrder or the
    // per-metric Show* flags: the column is a fixed-height edge fixture, and letting it grow and
    // shrink would move every tile's hover target whenever a metric is toggled. The Show* flags
    // still gate the classic panel.
    internal static readonly MetricTileId[] Order =
    {
        MetricTileId.Cpu,
        MetricTileId.Memory,
        MetricTileId.Disk,
        MetricTileId.Network,
        MetricTileId.Gpu,
        MetricTileId.Npu,
        MetricTileId.Power,
        MetricTileId.Guard
    };

    // Radar tiles, in column order. Codex and Claude each keep a quota tile; the model-quality (IQ)
    // tiles moved to the left-docked Codex IQ board. Service health is deliberately absent — it lives
    // in the network dock panel's cloud-service section instead of taking a tile of its own.
    internal static readonly MetricTileId[] RadarOrder =
    {
        MetricTileId.CodexQuota,
        MetricTileId.ClaudeQuota,
        MetricTileId.DeepSeekQuota
    };

    // Every tile, in the order their positions are stored in settings: metric tiles 0-7, Radar
    // tiles 8-9.
    internal static readonly MetricTileId[] AllOrder =
    {
        MetricTileId.Cpu, MetricTileId.Memory, MetricTileId.Disk, MetricTileId.Network,
        MetricTileId.Gpu, MetricTileId.Npu, MetricTileId.Power, MetricTileId.Guard,
        MetricTileId.CodexQuota, MetricTileId.ClaudeQuota, MetricTileId.DeepSeekQuota
    };

    internal static bool IsRadarTile(MetricTileId id)
    {
        return id == MetricTileId.CodexQuota ||
            id == MetricTileId.ClaudeQuota ||
            id == MetricTileId.DeepSeekQuota;
    }

    internal static bool IsClaudeTile(MetricTileId id)
    {
        return id == MetricTileId.ClaudeQuota;
    }

    internal static Color GetAccent(MetricTileId id)
    {
        switch (id)
        {
            case MetricTileId.Cpu: return DesignTokens.Colors.Accent;
            case MetricTileId.Memory: return DesignTokens.Colors.AccentAlt;
            case MetricTileId.Disk: return DesignTokens.Colors.Warning;
            case MetricTileId.Network: return DesignTokens.Colors.Success;
            case MetricTileId.Gpu: return DesignTokens.Colors.AccentSoft;
            case MetricTileId.Npu: return DesignTokens.Colors.TextMuted;
            case MetricTileId.Power: return DesignTokens.Colors.Success;
            // Radar: each quota tile carries its service colour.
            case MetricTileId.CodexQuota: return DesignTokens.Colors.Success;
            case MetricTileId.ClaudeQuota: return DesignTokens.Colors.Warning;
            case MetricTileId.DeepSeekQuota: return DesignTokens.Colors.Accent;
            default: return DesignTokens.Colors.AccentAlt;
        }
    }

    internal static string GetLabel(MetricTileId id)
    {
        switch (id)
        {
            case MetricTileId.Cpu: return "CPU";
            case MetricTileId.Memory: return "MEM";
            case MetricTileId.Disk: return "DISK";
            case MetricTileId.Network: return "NET";
            case MetricTileId.Gpu: return "GPU";
            case MetricTileId.Npu: return "NPU";
            case MetricTileId.Power: return "PWR";
            case MetricTileId.CodexQuota: return "CDX";
            case MetricTileId.ClaudeQuota: return "CLD";
            case MetricTileId.DeepSeekQuota: return "DS";
            default: return "守护";
        }
    }

    internal static List<MetricTileData> BuildTiles(MetricTileFeed feed)
    {
        List<MetricTileData> tiles = new List<MetricTileData>();
        for (int i = 0; i < Order.Length; i++)
        {
            tiles.Add(BuildTile(Order[i], feed));
        }

        return tiles;
    }

    internal static MetricTileData BuildTile(MetricTileId id, MetricTileFeed feed)
    {
        if (feed == null)
        {
            feed = new MetricTileFeed();
        }

        PerfSnapshot s = feed.Snapshot ?? new PerfSnapshot();
        MetricTileData tile = new MetricTileData
        {
            Id = id,
            Label = GetLabel(id),
            Accent = GetAccent(id)
        };
        tile.InnerAccent = Dimmed(tile.Accent);

        switch (id)
        {
            case MetricTileId.Cpu:
                tile.OuterPercent = s.CpuPercent;
                // Inner ring is the clock ratio, not a second load figure: at a glance it separates
                // "busy and boosting" from "busy but throttled", which the classic panel only told
                // you by reading two GHz numbers.
                tile.InnerPercent = s.CpuBaseFrequencyGhz > 0.0
                    ? Clamp(s.CpuFrequencyGhz / s.CpuBaseFrequencyGhz * 100.0, 0.0, 100.0)
                    : -1.0;
                tile.CenterValue = Round(s.CpuPercent);
                tile.AlertPercent = feed.AlertTestEnabled ? 100.0 : 0.0;
                tile.AlertIconVisible = feed.AlertTestEnabled;
                break;

            case MetricTileId.Memory:
                tile.OuterPercent = s.MemoryPercent;
                // The outer ring and centre keep the familiar physical-use reading. The inner ring
                // is the service-efficiency pressure index: available headroom and sustained
                // page-outs lead, while commit is only a late allocation-safety guard.
                tile.InnerPercent = Clamp(s.MemoryPressurePercent, 0.0, 100.0);
                tile.InnerAccent = GetMemoryPressureColor(s.MemoryPressureLevel);
                tile.CenterValue = Round(s.MemoryPercent);
                tile.AlertPercent = s.MemoryPressureLevel == MemoryPressureLevel.Critical ? 100.0 : 0.0;
                tile.AlertIconVisible = s.MemoryPressureLevel == MemoryPressureLevel.Critical;
                break;

            case MetricTileId.Disk:
                tile.OuterPercent = s.DiskPercent;
                tile.InnerPercent = s.DiskTotalGb > 0.0
                    ? Clamp(s.DiskUsedGb / s.DiskTotalGb * 100.0, 0.0, 100.0)
                    : -1.0;
                tile.CenterValue = Round(s.DiskPercent);
                tile.AlertPercent = s.DiskPercent;
                break;

            case MetricTileId.Network:
                {
                    // Rates have no natural 0-100 axis, so both rings are scaled against the recent
                    // peak across both directions: a full ring means "as busy as this link has been
                    // in the last minute", which is the only honest fixed-size reading available.
                    double peak = Math.Max(
                        PeakOf(feed.NetworkReceivedHistory),
                        PeakOf(feed.NetworkSentHistory));
                    double downKbps = ToKbps(s.NetworkReceivedBytesPerSecond);
                    double upKbps = ToKbps(s.NetworkSentBytesPerSecond);
                    peak = Math.Max(peak, Math.Max(downKbps, upKbps));
                    tile.OuterPercent = peak > 0.0 ? Clamp(downKbps / peak * 100.0, 0.0, 100.0) : 0.0;
                    tile.InnerPercent = peak > 0.0 ? Clamp(upKbps / peak * 100.0, 0.0, 100.0) : 0.0;
                    string rate = FormatCompactRate(s.NetworkReceivedBytesPerSecond);
                    tile.CenterValue = rate;
                    if (!s.NetworkConnected)
                    {
                        tile.CenterValue = "--";
                        tile.OuterPercent = 0.0;
                        tile.InnerPercent = 0.0;
                        tile.AlertPercent = 100.0;
                    }

                    break;
                }

            case MetricTileId.Gpu:
                tile.OuterPercent = s.GpuPercent;
                tile.InnerPercent = Clamp(s.GpuMemoryPercent, 0.0, 100.0);
                tile.CenterValue = Round(s.GpuPercent);
                tile.AlertPercent = Math.Max(s.GpuPercent, s.GpuMemoryPercent);
                break;

            case MetricTileId.Npu:
                tile.OuterPercent = s.NpuPercent;
                tile.InnerPercent = Clamp(s.NpuMemoryPercent, 0.0, 100.0);
                tile.CenterValue = Round(s.NpuPercent);
                tile.AlertPercent = Math.Max(s.NpuPercent, s.NpuMemoryPercent);
                tile.AlertIconVisible = feed.NpuAlertIconActive;
                break;

            case MetricTileId.Power:
                {
                    PowerStripSnapshot p = feed.Power;
                    int battery = p != null && p.BatteryPercentKnown ? p.BatteryPercent : -1;
                    tile.OuterPercent = battery >= 0 ? battery : -1.0;
                    // PWR is now a battery/runway surface. Thermal readings remain in the shared
                    // snapshot for System Day, but the compact tile deliberately carries one clear
                    // capacity ring instead of mixing temperature into a second axis.
                    tile.InnerPercent = -1.0;
                    tile.CenterValue = battery >= 0 ? battery.ToString(CultureInfo.InvariantCulture) : "--";
                    if (p != null && p.Charging)
                    {
                        tile.Accent = DesignTokens.Colors.Success;
                    }
                    else if (battery >= 0 && battery <= 20)
                    {
                        tile.Accent = DesignTokens.Colors.DangerStrong;
                        tile.AlertPercent = 100.0;
                    }

                    break;
                }

            case MetricTileId.Guard:
                // No rings: four independent on/off states have no shared axis, so the tile shows a
                // 2x2 dot pad instead and the expanded window carries the timers.
                tile.Guards = feed.Guards ?? new List<MetricTileGuardEntry>();
                break;

            case MetricTileId.CodexQuota:
            case MetricTileId.ClaudeQuota:
                {
                    RadarTileSnapshot r = feed.GetRadar(id == MetricTileId.ClaudeQuota);
                    // Outer ring is the weekly balance, inner the 5-hour window — the same pairing
                    // the Radar rings use, so the tile reads as a compression of that window rather
                    // than a new metric. Both are REMAINING, so a full ring means "plenty left".
                    tile.OuterPercent = r.QuotaKnown ? Clamp(r.WeeklyPercent, 0.0, 100.0) : -1.0;
                    tile.InnerPercent = r.QuotaKnown && !r.FiveHourLimitAbsent
                        ? Clamp(r.FiveHourPercent, 0.0, 100.0)
                        : -1.0;
                    tile.CenterValue = r.QuotaKnown
                        ? Math.Round(Clamp(r.WeeklyPercent, 0.0, 100.0)).ToString("0", CultureInfo.InvariantCulture)
                        : "--";
                    // Running out is the alert, so the alert axis is inverted against the ring.
                    if (r.QuotaKnown && r.WeeklyPercent <= 10)
                    {
                        tile.AlertPercent = 100.0;
                    }

                    tile.EasterEggVisual = ResolveQuotaEasterEggVisual(
                        id,
                        feed.QuotaEasterEgg,
                        out tile.EasterEggSecondLine);

                    break;
                }

            case MetricTileId.DeepSeekQuota:
                {
                    DeepSeekBalanceSnapshot d = feed.GetDeepSeekBalance();
                    tile.InnerAccent = DesignTokens.Colors.Warning;
                    tile.OuterPercent = d.Known
                        ? (d.ReferenceBalance > 0.0001
                            ? Clamp(d.Balance / d.ReferenceBalance * 100.0, 0.0, 100.0)
                            : (d.Balance > 0.0 ? 100.0 : 0.0))
                        : -1.0;
                    tile.InnerPercent = d.Known && d.Last24HourUsageKnown
                        ? Clamp(d.Last24HourUsage / Math.Max(0.0001, d.Balance + d.Last24HourUsage) * 100.0, 0.0, 100.0)
                        : -1.0;
                    tile.CenterValue = d.Known ? FormatCompactBalance(d.Balance) : (d.RequestRunning ? "..." : "--");
                    if (d.Known && (!d.IsAvailable || d.Balance <= 0.0001))
                    {
                        tile.AlertPercent = 100.0;
                    }

                    break;
                }
        }

        return tile;
    }

    internal static bool IsQuotaEmpty(RadarTileSnapshot snapshot)
    {
        if (snapshot == null || !snapshot.QuotaKnown || snapshot.WeeklyPercent <= 0)
        {
            return true;
        }

        return !snapshot.FiveHourLimitAbsent && snapshot.FiveHourPercent <= 0;
    }

    internal static QuotaEasterEggVisual ResolveQuotaEasterEggVisual(
        MetricTileId id,
        QuotaEasterEggSnapshot snapshot,
        out string secondLine)
    {
        secondLine = string.Empty;
        if (snapshot == null || !snapshot.Enabled ||
            (id != MetricTileId.CodexQuota && id != MetricTileId.ClaudeQuota))
        {
            return QuotaEasterEggVisual.None;
        }

        if (snapshot.CodexEmpty && snapshot.ClaudeEmpty)
        {
            secondLine = "已经陨落...";
            return QuotaEasterEggVisual.FallenTogether;
        }

        bool currentEmpty = id == MetricTileId.CodexQuota ? snapshot.CodexEmpty : snapshot.ClaudeEmpty;
        if (!currentEmpty)
        {
            return QuotaEasterEggVisual.None;
        }

        secondLine = id == MetricTileId.CodexQuota
            ? "陨落...了吗？看向Claude"
            : "陨落...了吗？看向Codex";
        return QuotaEasterEggVisual.FallenLookElsewhere;
    }

    private static string FormatCompactBalance(double balance)
    {
        double value = Math.Max(0.0, balance);
        if (value >= 1000.0)
        {
            return (value / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "K";
        }

        if (value >= 10.0)
        {
            return Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
        }

        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    // The four guards, in the fixed order the tile's dot pad reads top-left, top-right,
    // bottom-left, bottom-right. Mirrors WidgetForm.BuildGuardBadges so both presentation modes
    // report the same state; kept here so the tile windows do not depend on WidgetForm internals.
    internal static List<MetricTileGuardEntry> BuildGuardEntries(GuardRuntime runtime, DateTime nowUtc)
    {
        List<MetricTileGuardEntry> entries = new List<MetricTileGuardEntry>();

        bool sleepOn = runtime != null && runtime.SleepGuardEnabled;
        entries.Add(new MetricTileGuardEntry
        {
            Label = "防睡眠",
            Description = "阻止系统进入睡眠",
            Active = sleepOn,
            Detail = sleepOn ? "已持续 " + FormatSpan(runtime.GetSleepGuardElapsed(nowUtc)) : "未启用",
            Accent = DesignTokens.Colors.Accent
        });

        bool displayOn = runtime != null && runtime.DisplayGuardActive;
        entries.Add(new MetricTileGuardEntry
        {
            Label = "防息屏",
            Description = "阻止显示器关闭",
            Active = displayOn,
            Detail = displayOn ? "剩余 " + FormatSpan(runtime.DisplayGuardUntilUtc - nowUtc) : "未启用",
            Accent = DesignTokens.Colors.Warning
        });

        bool careOn = runtime != null && runtime.BatteryCarePauseActive;
        entries.Add(new MetricTileGuardEntry
        {
            Label = "养护暂停",
            Description = "电池养护充电暂停",
            Active = careOn,
            Detail = careOn ? "剩余 " + FormatSpan(runtime.BatteryCarePauseUntilUtc - nowUtc) : "未启用",
            Accent = DesignTokens.Colors.AccentAlt
        });

        // Offline is the noteworthy state, matching the classic strip: the dot lights up when the
        // link is down rather than when it is healthy.
        bool offline = runtime != null && !runtime.Online;
        entries.Add(new MetricTileGuardEntry
        {
            Label = offline ? "离线" : "在线",
            Description = "网络与服务在线",
            Active = offline,
            Detail = offline ? "已离线 " + FormatSpan(nowUtc - runtime.OfflineSinceUtc) : "正常",
            Accent = offline ? DesignTokens.Colors.DangerStrong : DesignTokens.Colors.Success
        });

        return entries;
    }

    internal static string FormatSpan(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        int totalMinutes = (int)value.TotalMinutes;
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        return hours > 0
            ? hours.ToString(CultureInfo.InvariantCulture) + "h" + minutes.ToString("00", CultureInfo.InvariantCulture)
            : minutes.ToString(CultureInfo.InvariantCulture) + "m";
    }

    // Compact enough for a 60x60 tile centre: "58K" rather than "58 Kbps". The expanded window
    // carries the full unit string.
    internal static string FormatCompactRate(double bytesPerSecond)
    {
        double kbps = ToKbps(bytesPerSecond);
        if (kbps < 1.0)
        {
            return "0";
        }

        if (kbps < 1000.0)
        {
            return Math.Round(kbps).ToString("0", CultureInfo.InvariantCulture) + "K";
        }

        double mbps = kbps / 1000.0;
        if (mbps < 1000.0)
        {
            return mbps < 10.0
                ? mbps.ToString("0.0", CultureInfo.InvariantCulture) + "M"
                : Math.Round(mbps).ToString("0", CultureInfo.InvariantCulture) + "M";
        }

        double gbps = mbps / 1000.0;
        return gbps < 10.0
            ? gbps.ToString("0.0", CultureInfo.InvariantCulture) + "G"
            : Math.Round(gbps).ToString("0", CultureInfo.InvariantCulture) + "G";
    }

    internal static double ToKbps(double bytesPerSecond)
    {
        return Math.Max(0.0, bytesPerSecond) * 8.0 / 1000.0;
    }

    private static double PeakOf(List<double> history)
    {
        if (history == null || history.Count == 0)
        {
            return 0.0;
        }

        double peak = 0.0;
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] > peak)
            {
                peak = history[i];
            }
        }

        return peak;
    }

    private static string Round(double percent)
    {
        return Math.Round(Clamp(percent, 0.0, 100.0)).ToString("0", CultureInfo.InvariantCulture);
    }

    private static Color Dimmed(Color color)
    {
        return Color.FromArgb(
            color.A,
            (int)(color.R * 0.62),
            (int)(color.G * 0.62),
            (int)(color.B * 0.62));
    }

    internal static Color GetMemoryPressureColor(MemoryPressureLevel level)
    {
        switch (level)
        {
            case MemoryPressureLevel.Warning: return DesignTokens.Colors.Warning;
            case MemoryPressureLevel.Critical: return DesignTokens.Colors.DangerStrong;
            default: return DesignTokens.Colors.Success;
        }
    }

    internal static string GetMemoryPressureLabel(MemoryPressureLevel level)
    {
        switch (level)
        {
            case MemoryPressureLevel.Warning: return "有压力";
            case MemoryPressureLevel.Critical: return "严重";
            default: return "正常";
        }
    }

    internal static double Clamp(double value, double min, double max)
    {
        if (double.IsNaN(value))
        {
            return min;
        }

        return value < min ? min : (value > max ? max : value);
    }

    internal static void RunSelfTest()
    {
        MetricTileFeed feed = new MetricTileFeed();
        feed.Snapshot.CpuPercent = 32.0;
        feed.Snapshot.CpuFrequencyGhz = 4.15;
        feed.Snapshot.CpuBaseFrequencyGhz = 4.45;
        feed.Snapshot.MemoryPercent = 63.0;
        feed.Snapshot.MemoryTotalGb = 47.6;
        feed.Snapshot.MemoryPressurePercent = 8.0;
        feed.Snapshot.MemoryPressureLevel = MemoryPressureLevel.Normal;
        feed.Snapshot.GpuMemoryUsedGb = 1.3;
        feed.Snapshot.NpuMemoryUsedGb = 0.7;
        feed.Snapshot.NetworkConnected = true;
        feed.Snapshot.NetworkReceivedBytesPerSecond = 58.0 * 1000.0 / 8.0;
        feed.Snapshot.NetworkSentBytesPerSecond = 17.0 * 1000.0 / 8.0;
        feed.Guards = BuildGuardEntries(null, DateTime.UtcNow);

        List<MetricTileData> tiles = BuildTiles(feed);
        if (tiles.Count != TileCount)
        {
            throw new InvalidOperationException("Metric tile column must build exactly " + TileCount + " tiles.");
        }

        MetricTileData cpu = tiles[0];
        if (cpu.Id != MetricTileId.Cpu || cpu.CenterValue != "32")
        {
            throw new InvalidOperationException("CPU tile centre value did not round the sampled percentage.");
        }

        if (Math.Abs(cpu.InnerPercent - (4.15 / 4.45 * 100.0)) > 0.01)
        {
            throw new InvalidOperationException("CPU tile inner ring must carry the clock ratio.");
        }

        MetricTileData memory = tiles[1];
        if (Math.Abs(memory.InnerPercent - 8.0) > 0.01 ||
            memory.InnerAccent != DesignTokens.Colors.Success ||
            memory.AlertPercent > 0.0)
        {
            throw new InvalidOperationException("Memory tile inner ring must carry normal pressure without alerting.");
        }

        feed.Snapshot.MemoryPressurePercent = 58.0;
        feed.Snapshot.MemoryPressureLevel = MemoryPressureLevel.Warning;
        memory = BuildTile(MetricTileId.Memory, feed);
        if (memory.InnerAccent != DesignTokens.Colors.Warning || memory.AlertPercent > 0.0 || memory.AlertIconVisible)
        {
            throw new InvalidOperationException("Warning memory pressure must use yellow without the critical alert treatment.");
        }

        feed.Snapshot.MemoryPressurePercent = 88.0;
        feed.Snapshot.MemoryPressureLevel = MemoryPressureLevel.Critical;
        memory = BuildTile(MetricTileId.Memory, feed);
        if (memory.InnerAccent != DesignTokens.Colors.DangerStrong ||
            memory.AlertPercent < 80.0 ||
            !memory.AlertIconVisible)
        {
            throw new InvalidOperationException("Critical memory pressure must drive the red inner ring and tile alert.");
        }

        MetricTileData network = tiles[3];
        // Down is the recent peak here, so its ring is full and up scales against the same peak.
        if (Math.Abs(network.OuterPercent - 100.0) > 0.01 ||
            Math.Abs(network.InnerPercent - (17.0 / 58.0 * 100.0)) > 0.01)
        {
            throw new InvalidOperationException("Network tile rings must scale both directions against the shared recent peak.");
        }

        if (network.CenterValue != "58K")
        {
            throw new InvalidOperationException("Network tile centre must use the compact rate format.");
        }

        MetricTileData guard = tiles[7];
        if (guard.Id != MetricTileId.Guard || guard.Guards == null || guard.Guards.Count != 4)
        {
            throw new InvalidOperationException("Guard tile must carry exactly four guard entries.");
        }

        if (guard.OuterPercent >= 0.0 || guard.InnerPercent >= 0.0)
        {
            throw new InvalidOperationException("Guard tile must suppress both rings in favour of its dot pad.");
        }

        RadarTileSnapshot emptyCodex = RadarTileSnapshot.CreateEmpty(CodexRadarSoftwareMode.Codex);
        RadarTileSnapshot emptyClaude = RadarTileSnapshot.CreateEmpty(CodexRadarSoftwareMode.Claude);
        emptyCodex.QuotaKnown = true;
        emptyClaude.QuotaKnown = true;
        RadarTileSnapshot aliveCodex = RadarTileSnapshot.CreateEmpty(CodexRadarSoftwareMode.Codex);
        aliveCodex.QuotaKnown = true;
        aliveCodex.WeeklyPercent = 50;
        aliveCodex.FiveHourPercent = 50;
        RadarTileSnapshot aliveClaude = RadarTileSnapshot.CreateEmpty(CodexRadarSoftwareMode.Claude);
        aliveClaude.QuotaKnown = true;
        aliveClaude.WeeklyPercent = 50;
        aliveClaude.FiveHourPercent = 50;
        QuotaEasterEggTracker tracker = new QuotaEasterEggTracker();
        QuotaEasterEggSnapshot fallen = tracker.Update(true, emptyCodex, aliveClaude);
        string secondLine;
        if (ResolveQuotaEasterEggVisual(MetricTileId.CodexQuota, fallen, out secondLine) !=
                QuotaEasterEggVisual.FallenLookElsewhere ||
            secondLine.IndexOf("Claude", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("Single-family quota easter-egg state failed.");
        }

        tracker.Update(true, aliveCodex, aliveClaude);
        if (!tracker.TryConsumeRevival(MetricTileId.CodexQuota) ||
            tracker.TryConsumeRevival(MetricTileId.CodexQuota))
        {
            throw new InvalidOperationException("Quota revival must be consumed exactly once.");
        }

        QuotaEasterEggSnapshot bothFallen = tracker.Update(true, emptyCodex, emptyClaude);
        if (ResolveQuotaEasterEggVisual(MetricTileId.ClaudeQuota, bothFallen, out secondLine) !=
                QuotaEasterEggVisual.FallenTogether ||
            !string.Equals(secondLine, "已经陨落...", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Dual-family quota easter-egg state failed.");
        }

        DeepSeekBalanceSnapshot deepSeek = DeepSeekBalanceSnapshot.CreateEmpty();
        deepSeek.ApiKeyConfigured = true;
        deepSeek.Known = true;
        deepSeek.IsAvailable = true;
        deepSeek.Balance = 88.5;
        deepSeek.ReferenceBalance = 100.0;
        deepSeek.Last24HourUsageKnown = true;
        deepSeek.Last24HourUsage = 11.5;
        feed.DeepSeekBalance = deepSeek;
        MetricTileData deepSeekTile = BuildTile(MetricTileId.DeepSeekQuota, feed);
        if (deepSeekTile.CenterValue != "89" || Math.Abs(deepSeekTile.OuterPercent - 88.5) > 0.01)
        {
            throw new InvalidOperationException("DeepSeek tile balance mapping failed.");
        }

        if (AllTileCount != 11 || AllOrder.Length != 11)
        {
            throw new InvalidOperationException("Metric tile topology must contain eleven tiles.");
        }

        Console.WriteLine("Metric tile model: PASS 11-tile topology, quota easter egg, DeepSeek balance, memory and guard mapping");
    }
}
