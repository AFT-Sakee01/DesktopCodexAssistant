using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

// Builds the read-only RadarTileSnapshot the Radar tiles render from (1.0.6.20).
//
// CodexRadarForm keeps an independent RadarFamilyRuntimeState per service family, so both Codex and
// Claude can be reported here regardless of which family the shared window is currently showing —
// that is what lets the tile column carry four Radar tiles while the shared window only ever shows
// one family at a time.
//
// Called from the UI thread by WidgetForm while building a tile feed. It reads cached state only
// and must never trigger a fetch, matching PowerThermalForm.BuildStripSnapshot.
internal sealed partial class CodexRadarForm
{
    private const double ClaudeQuotaRuntimeTtlMinutes = 360.0;
    private const double ClaudeQuotaFutureClockToleranceMinutes = 5.0;

    internal bool IsPollingAllowedForSelfTest()
    {
        return IsCodexPollingAllowed();
    }

    internal int ResumePrimeCountForSelfTest
    {
        get { return this.codexResumePrimeCountForSelfTest; }
    }

    internal RadarTileSnapshot BuildRadarTileSnapshot(CodexRadarSoftwareMode family)
    {
        RadarTileSnapshot tile = RadarTileSnapshot.CreateEmpty(family);
        try
        {
            RadarPublishedProjectionState published = ClonePublishedProjectionState();
            RadarFamilyProjectionState state = published.GetFamily(family);
            if (state == null)
            {
                return tile;
            }

            CodexQuotaSnapshot quota = state.QuotaSnapshot;
            CodexRadarSnapshot radar = state.RadarSnapshot;

            tile.ModelName = ResolveTileModelName(state.Family, state.ModelKey, radar);

            if (quota != null)
            {
                tile.QuotaKnown = state.QuotaSourceKnown &&
                    (state.Family != CodexRadarSoftwareMode.Claude ||
                     IsClaudeQuotaSnapshotFresh(quota, DateTime.UtcNow));
                tile.QuotaSourceUpdatedKnown = quota.SourceUpdatedKnown;
                tile.QuotaSourceUpdatedUtc = quota.SourceUpdatedUtc;
                tile.FiveHourPercent = quota.FiveHourPercent;
                tile.FiveHourResetKnown = quota.FiveHourResetKnown;
                tile.FiveHourResetLocal = quota.FiveHourResetLocal;
                tile.FiveHourLimitAbsent = quota.FiveHourLimitAbsent;
                tile.WeeklyPercent = quota.WeeklyPercent;
                tile.WeeklyResetKnown = quota.WeeklyResetKnown;
                tile.WeeklyResetLocal = quota.WeeklyResetLocal;
            }

            FillBurnDown(tile, state);

            // Claude's retained CLD surface is quota-only. Even if a stale community Radar snapshot
            // was hydrated by an older build, never project its IQ/rating/efficiency into the tile.
            if (state.Family == CodexRadarSoftwareMode.Codex && radar != null)
            {
                tile.IqKnown = radar.ModelIqKnown;
                tile.Iq = radar.ModelIqPassRatePercent;
                tile.IqUpdatedKnown = radar.ModelIqRefreshedAtKnown;
                tile.IqUpdatedLocal = radar.ModelIqRefreshedAtLocal;
                tile.EfficiencyKnown = radar.ModelIqEfficiencyKnown;
                tile.TokenEfficiencyPercent = radar.ModelIqTokenEfficiencyPercent;
                tile.TimeEfficiencyPercent = radar.ModelIqTimeEfficiencyPercent;
            }
        }
        catch (Exception ex)
        {
            // A tile that cannot read its family must degrade to "unknown", never take the widget
            // tick down with it.
            Program.LogException(ex);
        }

        return tile;
    }

    internal CodexIqBoardSnapshot BuildCodexIqBoardSnapshot()
    {
        CodexIqBoardSnapshot board = CodexIqBoardSnapshot.CreateEmpty();
        try
        {
            RadarPublishedProjectionState published = ClonePublishedProjectionState();
            RadarFamilyProjectionState state = published.GetFamily(CodexRadarSoftwareMode.Codex);
            CodexRadarSnapshot radar = state != null ? state.RadarSnapshot : null;
            board.SelectedModelKey = state != null ? (state.ModelKey ?? string.Empty) : string.Empty;
            board.SelectedModelLabel = ResolveTileModelName(
                state == null ? CodexRadarSoftwareMode.Codex : state.Family,
                state == null ? string.Empty : state.ModelKey,
                radar);
            if (radar != null)
            {
                board.SourceStale = radar.ModelIqKnown && !radar.ModelIqRefreshSucceeded;
                board.SourceStatus = board.SourceStale ? "缓存数据" : string.Empty;
                board.UpdatedKnown = radar.ModelIqSourceUpdatedAtKnown || radar.CheckedAtKnown;
                board.UpdatedLocal = radar.ModelIqSourceUpdatedAtKnown
                    ? radar.ModelIqSourceUpdatedAtLocal
                    : radar.CheckedAtLocal;
                List<CodexIqBoardModelPoint> models = CloneCodexIqBoardModels(radar.CodexIqModels);
                for (int i = 0; i < models.Count; i++)
                {
                    board.Models.Add(models[i]);
                    if (string.Equals(models[i].Key, board.SelectedModelKey, StringComparison.OrdinalIgnoreCase))
                    {
                        board.SelectedModelLabel = models[i].Label ?? board.SelectedModelLabel;
                    }
                }

                List<CodexModelHistoryPoint> history = NormalizeCodexModelHistory(radar.ModelIqHistory);
                for (int i = 0; i < history.Count; i++)
                {
                    CodexModelHistoryPoint point = history[i];
                    if (point == null)
                    {
                        continue;
                    }

                    board.Trends.Add(new CodexIqBoardTrendPoint
                    {
                        DateLocal = point.DateLocal,
                        AverageTaskSeconds = point.Tasks > 0.0
                            ? point.SerialSeconds / point.Tasks
                            : point.SerialSeconds,
                        TokenEfficiencyPercent = point.TokenEfficiencyPercent,
                        TotalTokens = point.TotalTokens,
                        EfficiencyKnown = point.EfficiencyKnown
                    });
                }
            }

            if (state != null && state.WeeklyBurnSamples != null)
            {
                List<WeeklyBurnSample> samples = state.WeeklyBurnSamples;
                int step = Math.Max(1, (int)Math.Ceiling(samples.Count / 24.0));
                for (int i = 0; i < samples.Count; i += step)
                {
                    board.WeeklyQuotaRemaining.Add(samples[i].RemainingPercent);
                }

                if (samples.Count > 0 && (samples.Count - 1) % step != 0)
                {
                    board.WeeklyQuotaRemaining.Add(samples[samples.Count - 1].RemainingPercent);
                }

                CodexQuotaSnapshot quota = state.QuotaSnapshot;
                if (board.WeeklyQuotaRemaining.Count == 0 && quota != null && state.QuotaSourceKnown)
                {
                    board.WeeklyQuotaRemaining.Add(quota.WeeklyPercent);
                }
            }

            FillCodexIqBoardRoster(board, published.Catalog);

            // Relocated from the network dock panel: the four upstream service LEDs share the board's
            // status band. Same read-only projection the panel used.
            board.Services.AddRange(BuildServiceHealth(published));
            FillCodexIqBoardRefresh(
                board,
                radar,
                state != null && state.RadarRequestRunning,
                published.RadarClockTimeDisplayMode);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return board;
    }

    // Cache-only projection for the sixth left-dock board. Seven-day rows come from the owner's
    // already-loaded history store; reset credits, speed-window state, and Reset Radar judgement
    // are cloned from their current caches. This method never reads disk, credentials, or the network.
    internal ResetSpeedBoardSnapshot BuildResetSpeedBoardSnapshot()
    {
        ResetSpeedBoardSnapshot board = ResetSpeedBoardSnapshot.CreateEmpty();
        try
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime nowLocal = nowUtc.ToLocalTime();
            RadarPublishedProjectionState published = ClonePublishedProjectionState();
            RadarFamilyProjectionState state = published.GetFamily(CodexRadarSoftwareMode.Codex);
            CodexQuotaSnapshot quota = state != null ? state.QuotaSnapshot : null;
            CodexRadarSnapshot radar = state != null ? state.RadarSnapshot : null;
            if (quota != null)
            {
                board.QuotaKnown = state.QuotaSourceKnown;
                board.FiveHourRemainingPercent = quota.FiveHourPercent;
                board.FiveHourLimitAbsent = quota.FiveHourLimitAbsent;
                board.FiveHourResetKnown = quota.FiveHourResetKnown;
                board.FiveHourResetLocal = quota.FiveHourResetLocal;
                board.WeeklyRemainingPercent = quota.WeeklyPercent;
                board.WeeklyResetKnown = quota.WeeklyResetKnown;
                board.WeeklyResetLocal = quota.WeeklyResetLocal;
                board.UpdatedKnown = quota.SourceUpdatedKnown;
                board.UpdatedLocal = quota.SourceUpdatedKnown
                    ? quota.SourceUpdatedUtc.ToLocalTime()
                    : DateTime.MinValue;
            }

            if (radar != null)
            {
                board.SpeedWindowKnown = radar.SpeedWindowKnown;
                board.SpeedWindowOpen = IsCodexRadarSpeedWindowCurrentlyOpen(radar, nowLocal);
                board.SpeedWindowOpenedAtKnown = radar.SpeedWindowOpenedAtKnown;
                board.SpeedWindowOpenedAtLocal = radar.SpeedWindowOpenedAtLocal;
                board.SpeedWindowClosedAtKnown = radar.SpeedWindowClosedAtKnown;
                board.SpeedWindowClosedAtLocal = radar.SpeedWindowClosedAtLocal;
                board.ResetRadarKnown = radar.ResetRadarKnown;
                board.ResetRadarUpdatedAtKnown = radar.ResetRadarUpdatedAtKnown;
                board.ResetRadarUpdatedAtLocal = radar.ResetRadarUpdatedAtLocal;
                board.ResetCardStatus = radar.ResetCardStatus;
                board.ResetCardDescription = radar.ResetCardDescription;
                board.HardResetStatus = radar.HardResetStatus;
                board.HardResetDescription = radar.HardResetDescription;
                int remainingMinutes;
                float remainingRatio;
                if (TryGetCodexRadarSpeedWindowCountdown(radar, nowLocal, out remainingMinutes, out remainingRatio))
                {
                    board.SpeedWindowRemainingMinutes = remainingMinutes;
                    board.SpeedWindowRemainingRatio = remainingRatio;
                }
            }

            CodexResetCreditsSnapshot credits = GetCodexResetCreditsDisplaySnapshot();
            if (credits != null)
            {
                board.ResetCreditsKnown = credits.Known;
                board.ResetCreditsRequestRunning = credits.RequestRunning;
                board.ResetCreditCount = credits.GetActiveCount(nowUtc);
                DateTime expirationUtc;
                if (credits.TryGetEarliestActiveExpirationUtc(nowUtc, out expirationUtc))
                {
                    board.ResetCreditExpirationKnown = true;
                    board.ResetCreditExpirationLocal = expirationUtc.ToLocalTime();
                }
            }

            CodexQuotaHistorySnapshot history = this.codexQuotaHistoryStore.GetSnapshot(nowUtc);
            DateTime firstDate = nowLocal.Date.AddDays(-6.0);
            for (int dayOffset = 0; dayOffset < 7; dayOffset++)
            {
                DateTime day = firstDate.AddDays(dayOffset);
                CodexQuotaHistoryEntry selected = null;
                for (int i = 0; i < history.Entries.Count; i++)
                {
                    CodexQuotaHistoryEntry entry = history.Entries[i];
                    if (entry != null && entry.TimestampUtc.ToLocalTime().Date == day)
                    {
                        selected = entry;
                    }
                }

                bool useCurrent = selected == null && day == nowLocal.Date && board.QuotaKnown;
                board.QuotaHistory.Add(new ResetSpeedQuotaPoint
                {
                    DateLocal = day,
                    Known = selected != null || useCurrent,
                    WeeklyRemainingPercent = selected != null
                        ? selected.WeeklyRemainingPercent
                        : useCurrent ? board.WeeklyRemainingPercent : 0
                });
            }

            for (int i = history.Entries.Count - 1; i >= 0 && board.ResetEvents.Count < 8; i--)
            {
                CodexQuotaHistoryEntry entry = history.Entries[i];
                if (entry == null || string.IsNullOrWhiteSpace(entry.ResetKind)) continue;
                ResetSpeedResetKind kind = string.Equals(entry.ResetKind, "credit", StringComparison.OrdinalIgnoreCase)
                    ? ResetSpeedResetKind.Credit
                    : string.Equals(entry.ResetKind, "natural", StringComparison.OrdinalIgnoreCase)
                        ? ResetSpeedResetKind.Natural
                        : ResetSpeedResetKind.Hard;
                board.ResetEvents.Add(new ResetSpeedResetEvent
                {
                    TimestampLocal = entry.TimestampUtc.ToLocalTime(),
                    Kind = kind,
                    WeeklyRemainingPercent = entry.WeeklyRemainingPercent
                });
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return board;
    }

    private static bool IsClaudeQuotaSnapshotFresh(CodexQuotaSnapshot snapshot, DateTime nowUtc)
    {
        if (snapshot == null || !snapshot.SourceUpdatedKnown || snapshot.SourceUpdatedUtc == DateTime.MinValue)
        {
            return false;
        }

        DateTime normalizedNowUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime();
        DateTime normalizedSourceUtc = snapshot.SourceUpdatedUtc.Kind == DateTimeKind.Utc
            ? snapshot.SourceUpdatedUtc
            : snapshot.SourceUpdatedUtc.ToUniversalTime();
        double ageMinutes = (normalizedNowUtc - normalizedSourceUtc).TotalMinutes;
        return ageMinutes >= -ClaudeQuotaFutureClockToleranceMinutes &&
            ageMinutes <= ClaudeQuotaRuntimeTtlMinutes;
    }

    private void ReloadCodexIqCatalogSnapshot(bool publish = true)
    {
        List<CodexRadarModelInfo> loaded = CodexRadarModelCatalog.LoadModels();
        List<CodexRadarModelInfo> replacement = CloneCodexRadarModelCatalog(loaded);
        lock (this.codexIqCatalogSnapshotLock)
        {
            this.codexIqCatalogSnapshot = replacement;
            unchecked
            {
                this.codexIqCatalogRevision++;
            }
        }

        if (publish)
        {
            PublishProjectionStateFromOwner();
        }
    }

    private static List<CodexRadarModelInfo> CloneCodexRadarModelCatalog(
        IList<CodexRadarModelInfo> source)
    {
        List<CodexRadarModelInfo> result = new List<CodexRadarModelInfo>();
        for (int i = 0; source != null && i < source.Count; i++)
        {
            if (source[i] != null)
            {
                result.Add(source[i].Clone());
            }
        }

        return result;
    }

    // Flattens the circular Radar refresh dial into fractions a horizontal track can paint, reusing
    // the same RadarClockDial state machine the radar window's dial does so phase, colour and the
    // refresh-marker geometry stay identical — only the projection from angle to 0..1 differs.
    private void FillCodexIqBoardRefresh(
        CodexIqBoardSnapshot board,
        CodexRadarSnapshot radar,
        bool requestRunning,
        RadarClockTimeDisplayMode timeDisplayMode)
    {
        CodexIqBoardRefreshStatus refresh = board.Refresh;
        if (refresh == null)
        {
            refresh = new CodexIqBoardRefreshStatus();
            board.Refresh = refresh;
        }

        if (radar == null)
        {
            refresh.Known = false;
            refresh.PhaseText = "无数据";
            return;
        }

        double cycleHours = GetEvenRowDialCycleHours();
        if (cycleHours <= 0.0)
        {
            cycleHours = 1.0;
        }

        bool batchKnown = radar.ModelIqDataDateKnown;
        DateTime batchTime = batchKnown
            ? radar.ModelIqDataDateLocal.Date.AddHours(radar.ModelIqDataWindowStartHourLocal >= 12 ? 12 : 0)
            : DateTime.MinValue;
        bool localKnown = radar.ModelIqRefreshedAtKnown;
        DateTime localTime = localKnown ? radar.ModelIqRefreshedAtLocal : DateTime.MinValue;
        RadarClockDialState state = RadarClockDial.ComputeState(new RadarClockDialInput
        {
            BatchKnown = batchKnown,
            BatchTimeLocal = batchTime,
            LocalKnown = localKnown,
            RefreshMarkerTimeLocal = localTime,
            CycleHours = cycleHours,
            NowLocal = DateTime.Now,
            NowUtc = DateTime.UtcNow,
            RequestRunning = requestRunning,
            RenderTick = 0,
            DataLabelText = GetCodexModelIqDataLabelDisplayText(radar),
            TimeDisplayMode = timeDisplayMode,
            LastActualKnown = localKnown,
            LastActualLocal = localTime
        });

        refresh.Known = batchKnown || localKnown;
        refresh.StatusColor = state.StatusColor;
        refresh.RequestRunning = requestRunning;
        refresh.Warning = state.WarningRingVisible;
        refresh.CurrentFraction = AngleToTrackFraction(state.CurrentAngle);
        refresh.MarkerVisible = state.RefreshMarkerVisible;
        refresh.MarkerFraction = AngleToTrackFraction(state.RefreshMarkerAngle);
        refresh.ArcStartFraction = AngleToTrackFraction(state.ArcStartAngle);
        refresh.ArcSweepFraction = Math.Max(0.0f, Math.Min(1.0f, state.ArcSweepDegrees / 360.0f));
        refresh.PhaseText = GetCodexIqBoardRefreshPhaseText(state.Phase);
        refresh.DetailText = state.DateText ?? string.Empty;
    }

    // The dial's boundary sits at -90 degrees; NormalizeSweep from there gives 0..360 clockwise,
    // which maps straight onto a left-to-right track.
    private static float AngleToTrackFraction(float angle)
    {
        return RadarClockDial.NormalizeSweep(-90.0f, angle) / 360.0f;
    }

    private static string GetCodexIqBoardRefreshPhaseText(RadarClockDialPhase phase)
    {
        switch (phase)
        {
            case RadarClockDialPhase.CurrentCycle:
                return "本轮已刷新";
            case RadarClockDialPhase.WaitingCycle:
                return "等待新批次";
            case RadarClockDialPhase.MissedCycle:
                return "缺刷";
            case RadarClockDialPhase.NoData:
            default:
                return "无数据";
        }
    }

    private static double GetEvenRowDialCycleHours()
    {
        return 12.0;
    }

    private static bool TryGetCodexRadarSpeedWindowCountdown(
        CodexRadarSnapshot snapshot,
        DateTime nowLocal,
        out int remainingMinutes,
        out float remainingRatio)
    {
        const int maximumMinutes = 100 * 60;
        remainingMinutes = 0;
        remainingRatio = 0.0f;
        if (!IsCodexRadarSpeedWindowCurrentlyOpen(snapshot, nowLocal) ||
            snapshot == null ||
            !snapshot.SpeedWindowClosedAtKnown ||
            snapshot.SpeedWindowClosedAtLocal == DateTime.MinValue)
        {
            return false;
        }

        DateTime closedAt = snapshot.SpeedWindowClosedAtLocal;
        double remainingRawMinutes = (closedAt - nowLocal).TotalMinutes;
        if (remainingRawMinutes <= 0.0)
        {
            return false;
        }

        DateTime effectiveStart = closedAt.AddMinutes(-maximumMinutes);
        if (snapshot.SpeedWindowOpenedAtKnown &&
            snapshot.SpeedWindowOpenedAtLocal != DateTime.MinValue &&
            snapshot.SpeedWindowOpenedAtLocal < closedAt &&
            snapshot.SpeedWindowOpenedAtLocal > effectiveStart)
        {
            effectiveStart = snapshot.SpeedWindowOpenedAtLocal;
        }

        double totalMinutes = Math.Max(1.0, (closedAt - effectiveStart).TotalMinutes);
        remainingRatio = (float)Math.Max(0.0, Math.Min(1.0, remainingRawMinutes / totalMinutes));
        remainingMinutes = Math.Max(1, Math.Min(maximumMinutes, (int)Math.Ceiling(remainingRawMinutes)));
        return true;
    }

    private static string FormatSpeedWindowCountdownTime(int totalMinutes)
    {
        int clamped = Math.Max(0, Math.Min(100 * 60, totalMinutes));
        int hours = clamped / 60;
        int minutes = clamped % 60;
        return hours.ToString(hours >= 100 ? "000" : "00", CultureInfo.InvariantCulture) +
            ":" + minutes.ToString("00", CultureInfo.InvariantCulture);
    }

    private static string GetCodexModelIqDataLabelDisplayText(CodexRadarSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return "--";
        }

        if (snapshot.ModelIqDataLabelKnown && !string.IsNullOrWhiteSpace(snapshot.ModelIqDataLabel))
        {
            return snapshot.ModelIqDataLabel.Trim();
        }

        if (snapshot.ModelIqDataDateKnown)
        {
            return FormatCodexModelIqDataLabel(
                string.Empty,
                snapshot.ModelIqDataDateLocal,
                snapshot.ModelIqDataWindowStartHourLocal,
                snapshot.ModelIqDataWindowKnown);
        }

        return "--";
    }

    private static void FillCodexIqBoardRoster(
        CodexIqBoardSnapshot board,
        IList<CodexRadarModelInfo> catalog)
    {
        if (board == null)
        {
            return;
        }

        HashSet<string> currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < board.Models.Count; i++)
        {
            CodexIqBoardModelPoint point = board.Models[i];
            if (point == null || string.IsNullOrEmpty(point.Key))
            {
                continue;
            }

            currentKeys.Add(point.Key);
            board.Roster.Add(new CodexIqBoardRosterEntry
            {
                Key = point.Key,
                Label = point.Label,
                State = CodexIqBoardRosterState.Active
            });
        }

        for (int i = 0; catalog != null && i < catalog.Count; i++)
        {
            CodexRadarModelInfo model = catalog[i];
            if (model == null || string.IsNullOrEmpty(model.Key) || currentKeys.Contains(model.Key))
            {
                continue;
            }

            board.Roster.Add(new CodexIqBoardRosterEntry
            {
                Key = model.Key,
                Label = model.Label,
                State = model.Available
                    ? CodexIqBoardRosterState.Intermittent
                    : CodexIqBoardRosterState.Retired
            });
        }
    }

    // Service LEDs are a health projection, not an alert presentation. They deliberately bypass
    // AlertServiceHealthEnabled and the alert debounce window: disabling notifications must not
    // repaint an unavailable upstream as green.
    internal List<RadarServiceHealthEntry> BuildServiceHealth()
    {
        return BuildServiceHealth(ClonePublishedProjectionState());
    }

    private List<RadarServiceHealthEntry> BuildServiceHealth(RadarPublishedProjectionState published)
    {
        List<RadarServiceHealthEntry> list = RadarServiceHealth.CreateUnknown();
        try
        {
            ServiceProjectionState service = published == null || published.Services == null
                ? ServiceProjectionState.CreateDefault()
                : published.Services;
            bool online = service.NetworkAvailable;
            ApplyServiceHealthProjection(
                list[0],
                online ? service.RadarHealth : ServiceHealthState.Offline,
                online && service.RadarRequestRunning);
            ApplyServiceHealthProjection(
                list[1],
                online ? service.OpenAiHealth : ServiceHealthState.Offline,
                online && service.OpenAiRequestRunning);
            ApplyServiceHealthProjection(
                list[2],
                online ? service.ClaudeHealth : ServiceHealthState.Offline,
                online && service.ClaudeRequestRunning);
            ApplyClaudeUsageHealthProjection(list[2], service);
            ApplyDeepSeekServiceHealthProjection(list[3], service.DeepSeekSource, online);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return list;
    }

    private void ApplyClaudeUsageHealthProjection(
        RadarServiceHealthEntry entry,
        ServiceProjectionState service)
    {
        if (entry == null || service == null || !service.NetworkAvailable)
        {
            return;
        }

        bool quotaKnown = service.ClaudeQuotaKnown;
        bool checking = service.ClaudeUsageRequestRunning;
        ServiceHealthState statuspageHealth = service.ClaudeHealth;
        if (checking && !quotaKnown && GetServiceHealthSeverity(statuspageHealth) == 0)
        {
            ApplyServiceHealthProjection(entry, ServiceHealthState.Unknown, true);
            return;
        }

        ServiceHealthState usageHealth = service.ClaudeUsageHealth;
        string errorCode = service.ClaudeUsageErrorCode ?? string.Empty;
        bool ignorableMissingSource =
            (IsClaudeSetupTokenMissing(errorCode) || IsClaudeCodeStatusLineCacheMissing(errorCode)) &&
            quotaKnown;
        if (ignorableMissingSource ||
            usageHealth == ServiceHealthState.Normal ||
            usageHealth == ServiceHealthState.Unknown ||
            GetServiceHealthSeverity(usageHealth) < GetServiceHealthSeverity(statuspageHealth))
        {
            return;
        }

        entry.Checking = false;
        entry.Color = GetClaudeCodeUsageAlertColor(usageHealth, errorCode);
    }

    private static void ApplyServiceHealthProjection(
        RadarServiceHealthEntry entry,
        ServiceHealthState state,
        bool checking)
    {
        if (entry == null)
        {
            return;
        }

        entry.Checking = checking;
        if (checking)
        {
            entry.Color = DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
        }
        else if (state == ServiceHealthState.Normal)
        {
            entry.Color = DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245);
        }
        else if (state == ServiceHealthState.Unknown)
        {
            entry.Color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }
        else
        {
            entry.Color = GetServiceHealthAlertColor(state);
        }
    }

    private static void ApplyDeepSeekServiceHealthProjection(
        RadarServiceHealthEntry entry,
        DeepSeekServiceSnapshot snapshot,
        bool online)
    {
        if (entry == null)
        {
            return;
        }

        if (!online)
        {
            ApplyServiceHealthProjection(entry, ServiceHealthState.Offline, false);
            return;
        }

        if (snapshot != null && snapshot.RequestRunning)
        {
            ApplyServiceHealthProjection(entry, ServiceHealthState.Unknown, true);
            return;
        }

        entry.Checking = false;
        if (snapshot == null)
        {
            entry.Color = DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }
        else if (!snapshot.Known)
        {
            bool attempted = !string.IsNullOrWhiteSpace(snapshot.ErrorCode) ||
                snapshot.CheckedAtUtc != DateTime.MinValue ||
                snapshot.CheckedAtLocal != DateTime.MinValue;
            entry.Color = attempted
                ? GetDeepSeekApiAlertColor(snapshot)
                : DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }
        else if (snapshot.IsAvailable)
        {
            entry.Color = DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245);
        }
        else
        {
            entry.Color = GetDeepSeekApiAlertColor(snapshot);
        }
    }

    private static int GetServiceHealthSeverity(ServiceHealthState state)
    {
        if (state == ServiceHealthState.Unreachable)
        {
            return 3;
        }

        if (state == ServiceHealthState.Unavailable)
        {
            return 2;
        }

        if (state == ServiceHealthState.Degraded ||
            state == ServiceHealthState.Incomplete ||
            state == ServiceHealthState.Offline)
        {
            return 1;
        }

        return 0;
    }

    private static void RunServiceHealthProjectionSelfTest()
    {
        RadarServiceHealthEntry entry = new RadarServiceHealthEntry();
        ApplyDeepSeekServiceHealthProjection(
            entry,
            new DeepSeekServiceSnapshot
            {
                Known = true,
                IsAvailable = false,
                ErrorCode = "503",
                ErrorMessage = "服务不可用"
            },
            true);
        if (entry.Checking || entry.Color == DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245))
        {
            throw new InvalidOperationException("Service health projection self-test failed: DeepSeek unavailable rendered green.");
        }

        ApplyDeepSeekServiceHealthProjection(
            entry,
            new DeepSeekServiceSnapshot
            {
                Known = false,
                IsAvailable = false,
                ErrorCode = "NET",
                ErrorMessage = "无法连接",
                CheckedAtUtc = new DateTime(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc)
            },
            true);
        if (entry.Checking || entry.Color != DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245))
        {
            throw new InvalidOperationException("Service health projection self-test failed: DeepSeek network failure rendered as unknown.");
        }

        ApplyDeepSeekServiceHealthProjection(
            entry,
            new DeepSeekServiceSnapshot { Known = true, IsAvailable = true },
            true);
        if (entry.Checking || entry.Color != DesignTokens.WithAlpha(DesignTokens.Colors.Success, 245))
        {
            throw new InvalidOperationException("Service health projection self-test failed: DeepSeek available was not green.");
        }
    }

    private string ResolveTileModelName(
        CodexRadarSoftwareMode family,
        string modelKey,
        CodexRadarSnapshot radar)
    {
        string key = modelKey ?? string.Empty;
        string label = string.Empty;
        if (family == CodexRadarSoftwareMode.Claude)
        {
            // OAuth usage is account-wide and does not identify a model. Keep the CLD tile stable
            // and independent from the retired community model map/snapshot.
            return "Claude";
        }

        if (radar != null && radar.CodexIqModels != null)
        {
            for (int i = 0; i < radar.CodexIqModels.Count; i++)
            {
                CodexIqBoardModelPoint point = radar.CodexIqModels[i];
                if (point != null && string.Equals(point.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    label = point.Label ?? string.Empty;
                    break;
                }
            }
        }

        string codexShort = FormatCodexRadarCurrentModelShortLabel(key, label);
        return "LLM:" + (string.IsNullOrEmpty(codexShort) ? "--" : codexShort);
    }

    private static string FormatCodexRadarCurrentModelShortLabel(string key, string label)
    {
        string normalizedKey = CodexRadarModelCatalog.NormalizeModelKey(key);
        Match keyed = Regex.Match(
            normalizedKey,
            "^gpt_([0-9])([0-9]+)(?:_([a-z0-9]+))?_(xhigh|ultra|high|medium|low)$",
            RegexOptions.IgnoreCase);
        if (keyed.Success)
        {
            string family = keyed.Groups[3].Value;
            string effort = keyed.Groups[4].Value;
            string familyShort = family.Length == 0
                ? string.Empty
                : family.Substring(0, 1).ToUpperInvariant();
            string effortShort = effort == "xhigh" ? "XH" :
                effort == "ultra" ? "U" :
                effort == "high" ? "H" :
                effort == "medium" ? "M" : "L";
            return keyed.Groups[1].Value + "." + keyed.Groups[2].Value + familyShort + effortShort;
        }

        string raw = !string.IsNullOrWhiteSpace(label) ? label : key;
        string lower = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (lower.Length == 0)
        {
            return string.Empty;
        }

        Match gpt = Regex.Match(lower, "gpt[-\\s_]*([0-9]+(?:\\.[0-9]+)?)\\s*[-\\s_]*(xhigh|high|medium|low)?");
        if (gpt.Success)
        {
            string suffix = gpt.Groups[2].Value;
            string effort = string.Empty;
            if (suffix == "xhigh") effort = "XH";
            else if (suffix == "high") effort = "H";
            else if (suffix == "medium") effort = "M";
            else if (suffix == "low") effort = "L";
            return gpt.Groups[1].Value + effort;
        }

        string compact = Regex.Replace(raw, "[^A-Za-z0-9.]+", string.Empty);
        return compact.Length <= 8 ? compact : compact.Substring(0, 8);
    }

    internal static void RunTileSnapshotFamilySelfTest()
    {
        RunProjectionStateAtomicitySelfTest();
        WidgetSettings settings = new WidgetSettings();
        settings.Normalize();
        settings.CodexRadarModelKey = "gpt_56_sol_low";
        string historyPath = Path.Combine(
            Path.GetTempPath(),
            ProductIdentity.MachineName + "-tile-snapshot-" + Guid.NewGuid().ToString("N") + ".jsonl");
        using (CodexRadarForm form = new CodexRadarForm(settings, null, historyPath))
        {
            form.codexRuntimeState.ModelKey = settings.CodexRadarModelKey;
            form.codexRuntimeState.RadarSnapshot = CodexRadarSnapshot.CreateDefault();
            form.codexRuntimeState.RadarSnapshot.CodexIqModels.Add(new CodexIqBoardModelPoint
            {
                Key = settings.CodexRadarModelKey,
                Label = "GPT-5.6 Sol low"
            });
            form.claudeRuntimeState.ModelKey = "retired-community-model";
            form.claudeRuntimeState.RadarSnapshot = CodexRadarSnapshot.CreateDefault();
            form.claudeRuntimeState.RadarSnapshot.ModelIqKnown = true;
            form.claudeRuntimeState.RadarSnapshot.ModelIqEfficiencyKnown = true;
            DateTime fiveHourReset = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Local);
            DateTime weeklyReset = new DateTime(2026, 7, 28, 12, 30, 0, DateTimeKind.Local);
            form.claudeRuntimeState.Quota.Snapshot = CodexQuotaSnapshot.CreateDefault();
            form.claudeRuntimeState.Quota.Snapshot.FiveHourPercent = 61;
            form.claudeRuntimeState.Quota.Snapshot.FiveHourResetKnown = true;
            form.claudeRuntimeState.Quota.Snapshot.FiveHourResetLocal = fiveHourReset;
            form.claudeRuntimeState.Quota.Snapshot.WeeklyPercent = 34;
            form.claudeRuntimeState.Quota.Snapshot.WeeklyResetKnown = true;
            form.claudeRuntimeState.Quota.Snapshot.WeeklyResetLocal = weeklyReset;
            DateTime quotaNowUtc = DateTime.UtcNow;
            form.claudeRuntimeState.Quota.Snapshot.SourceUpdatedKnown = true;
            form.claudeRuntimeState.Quota.Snapshot.SourceUpdatedUtc = quotaNowUtc;
            form.claudeRuntimeState.Quota.SourceKnown = true;
            form.PublishProjectionStateFromOwner();

            form.effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Codex;
            RadarTileSnapshot codexWhileCodex = form.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Codex);
            RadarTileSnapshot claudeWhileCodex = form.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Claude);
            form.effectiveCodexRadarSoftwareMode = CodexRadarSoftwareMode.Claude;
            RadarTileSnapshot codexWhileClaude = form.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Codex);
            RadarTileSnapshot claudeWhileClaude = form.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Claude);

            if (!string.Equals(codexWhileCodex.ModelName, codexWhileClaude.ModelName, StringComparison.Ordinal) ||
                !string.Equals(claudeWhileCodex.ModelName, "Claude", StringComparison.Ordinal) ||
                !string.Equals(claudeWhileClaude.ModelName, "Claude", StringComparison.Ordinal) ||
                codexWhileCodex.ModelName.IndexOf("5.6", StringComparison.OrdinalIgnoreCase) < 0 ||
                !claudeWhileCodex.QuotaKnown ||
                !claudeWhileClaude.QuotaKnown ||
                claudeWhileCodex.FiveHourPercent != 61 ||
                claudeWhileClaude.FiveHourPercent != 61 ||
                !claudeWhileCodex.FiveHourResetKnown ||
                claudeWhileCodex.FiveHourResetLocal != fiveHourReset ||
                claudeWhileCodex.WeeklyPercent != 34 ||
                claudeWhileClaude.WeeklyPercent != 34 ||
                !claudeWhileCodex.WeeklyResetKnown ||
                claudeWhileCodex.WeeklyResetLocal != weeklyReset ||
                claudeWhileCodex.IqKnown ||
                claudeWhileCodex.EfficiencyKnown ||
                claudeWhileClaude.IqKnown ||
                claudeWhileClaude.EfficiencyKnown)
            {
                throw new InvalidOperationException("Radar tile snapshot family self-test failed.");
            }

            CodexQuotaSnapshot ttlProbe = form.claudeRuntimeState.Quota.Snapshot.Clone();
            ttlProbe.SourceUpdatedUtc = quotaNowUtc.AddMinutes(-359.0).AddSeconds(-59.0);
            bool fresh35959 = IsClaudeQuotaSnapshotFresh(ttlProbe, quotaNowUtc);
            ttlProbe.SourceUpdatedUtc = quotaNowUtc.AddMinutes(-360.0);
            bool fresh36000 = IsClaudeQuotaSnapshotFresh(ttlProbe, quotaNowUtc);
            ttlProbe.SourceUpdatedUtc = quotaNowUtc.AddMinutes(-360.0).AddSeconds(-1.0);
            bool stale36001 = !IsClaudeQuotaSnapshotFresh(ttlProbe, quotaNowUtc);
            ttlProbe.SourceUpdatedUtc = quotaNowUtc.AddMinutes(4.0);
            bool smallFutureFresh = IsClaudeQuotaSnapshotFresh(ttlProbe, quotaNowUtc);
            ttlProbe.SourceUpdatedUtc = quotaNowUtc.AddMinutes(6.0);
            bool largeFutureStale = !IsClaudeQuotaSnapshotFresh(ttlProbe, quotaNowUtc);
            DateTime midnightNow = new DateTime(2026, 7, 23, 0, 1, 0, DateTimeKind.Utc);
            ttlProbe.SourceUpdatedUtc = new DateTime(2026, 7, 22, 23, 59, 0, DateTimeKind.Utc);
            bool midnightFresh = IsClaudeQuotaSnapshotFresh(ttlProbe, midnightNow);
            ttlProbe.SourceUpdatedUtc = quotaNowUtc.AddMinutes(-361.0);
            form.claudeRuntimeState.Quota.Snapshot = ttlProbe.Clone();
            form.PublishProjectionStateFromOwner();
            RadarTileSnapshot staleTile = form.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Claude);
            ttlProbe.SourceUpdatedUtc = quotaNowUtc;
            form.claudeRuntimeState.Quota.Snapshot = ttlProbe.Clone();
            form.PublishProjectionStateFromOwner();
            RadarTileSnapshot recoveredTile = form.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Claude);
            if (!fresh35959 || !fresh36000 || !stale36001 || !smallFutureFresh ||
                !largeFutureStale || !midnightFresh || staleTile.QuotaKnown ||
                staleTile.FiveHourPercent != 61 || !recoveredTile.QuotaKnown)
            {
                throw new InvalidOperationException("Claude quota runtime TTL self-test failed.");
            }

            // Claude owns the same dual-window estimator as Codex. Exercise the published clone,
            // not just the private calculator, so a missing list in projection cannot silently make
            // the CLD panel stay in "采样中" forever.
            QuotaRuntimeState claudeQuota = form.claudeRuntimeState.Quota;
            CodexQuotaSnapshot forecastStart = ttlProbe.Clone();
            forecastStart.FiveHourPercent = 61;
            forecastStart.FiveHourResetKnown = true;
            forecastStart.FiveHourResetLocal = DateTime.Now.AddHours(2.0);
            forecastStart.WeeklyPercent = 34;
            forecastStart.WeeklyResetKnown = true;
            forecastStart.WeeklyResetLocal = DateTime.Now.AddHours(10.0);
            claudeQuota.WeeklyBurnClockActive = true;
            claudeQuota.WeeklyBurnActiveHours = 0.0;
            RecordQuotaBurnSamples(claudeQuota, forecastStart, quotaNowUtc.AddHours(-1.0));
            CodexQuotaSnapshot forecastEnd = forecastStart.Clone();
            forecastEnd.FiveHourPercent = 55;
            forecastEnd.WeeklyPercent = 30;
            forecastEnd.SourceUpdatedUtc = quotaNowUtc;
            claudeQuota.WeeklyBurnActiveHours = 1.0;
            RecordQuotaBurnSamples(claudeQuota, forecastEnd, quotaNowUtc);
            claudeQuota.Snapshot = forecastEnd;
            form.PublishProjectionStateFromOwner();
            RadarTileSnapshot claudeForecast = form.BuildRadarTileSnapshot(CodexRadarSoftwareMode.Claude);
            if (!claudeForecast.BurnRateKnown ||
                !claudeForecast.CalendarRunwayKnown ||
                !claudeForecast.FiveHourBurnRateKnown ||
                !claudeForecast.HasBurnCurve)
            {
                throw new InvalidOperationException("Claude dual-window quota forecast projection self-test failed.");
            }

            int projectionFileReads = 0;
            CodexRadarModelCatalog.LoadModelsObserverForSelfTest = delegate { projectionFileReads++; };
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    CodexIqBoardSnapshot projection = form.BuildCodexIqBoardSnapshot();
                    if (projection == null)
                    {
                        throw new InvalidOperationException("Codex IQ cache-only projection returned null.");
                    }
                }
            }
            finally
            {
                CodexRadarModelCatalog.LoadModelsObserverForSelfTest = null;
            }

            if (projectionFileReads != 0)
            {
                throw new InvalidOperationException("Codex IQ cache-only projection performed file I/O.");
            }

            form.StartHeadlessDataOwner();
            if (!form.IsHeadlessDataOwner || !form.IsBackendSchedulerRunning || form.Visible)
            {
                throw new InvalidOperationException("Radar headless data-owner lifecycle self-test failed.");
            }

            form.StopHeadlessDataOwner();
        }
        try { File.Delete(historyPath); } catch { }
    }

    // The burn-down series and forecasts come from the same per-family accepted histories. Active
    // histories answer continuous use; wall histories provide the recent-rhythm estimate.
    private void FillBurnDown(RadarTileSnapshot tile, RadarFamilyProjectionState state)
    {
        if (tile == null || state == null || !tile.QuotaKnown)
        {
            return;
        }

        List<WeeklyBurnSample> weeklyActive = state.WeeklyBurnSamples;
        List<WeeklyBurnSample> weeklyWall = state.WeeklyWallBurnSamples;
        List<WeeklyBurnSample> visualSamples = weeklyActive != null && weeklyActive.Count >= 2
            ? weeklyActive
            : weeklyWall;
        if (visualSamples != null)
        {
            for (int i = 0; i < visualSamples.Count; i++)
            {
                tile.WeeklyBurnRemaining.Add(visualSamples[i].RemainingPercent);
            }
        }

        double burn;
        double runway;
        double hoursToReset;
        double observedHours;
        QuotaForecastConfidence confidence;
        CodexQuotaSnapshot quota = state.QuotaSnapshot;
        DateTime nowLocal = DateTime.Now;

        if (TryComputeQuotaBurnRate(
                weeklyActive,
                false,
                WeeklyBurnRateWindowActiveHours,
                WeeklyBurnRateMinimumActiveMinutes,
                quota.WeeklyPercent,
                quota.WeeklyResetKnown,
                quota.WeeklyResetLocal,
                nowLocal,
                out burn,
                out runway,
                out hoursToReset,
                out observedHours,
                out confidence))
        {
            tile.BurnRateKnown = true;
            tile.BurnPercentPerHour = burn;
            tile.RunwayHours = runway;
            tile.HoursToReset = hoursToReset;
            tile.BurnObservedHours = observedHours;
            tile.BurnConfidence = confidence;
        }
        else if (quota.WeeklyResetKnown && quota.WeeklyResetLocal != DateTime.MinValue)
        {
            tile.HoursToReset = (quota.WeeklyResetLocal - nowLocal).TotalHours;
        }

        if (TryComputeQuotaBurnRate(
                weeklyWall,
                true,
                WeeklyBurnRateWindowWallHours,
                BurnRateMinimumWallMinutes,
                quota.WeeklyPercent,
                quota.WeeklyResetKnown,
                quota.WeeklyResetLocal,
                nowLocal,
                out burn,
                out runway,
                out hoursToReset,
                out observedHours,
                out confidence))
        {
            tile.CalendarRunwayKnown = true;
            tile.CalendarBurnPercentPerHour = burn;
            tile.CalendarRunwayHours = runway;
            tile.CalendarConfidence = confidence;
        }

        if (!quota.FiveHourLimitAbsent &&
            TryComputeQuotaBurnRate(
                state.FiveHourBurnSamples,
                false,
                FiveHourBurnRateWindowActiveHours,
                WeeklyBurnRateMinimumActiveMinutes,
                quota.FiveHourPercent,
                quota.FiveHourResetKnown,
                quota.FiveHourResetLocal,
                nowLocal,
                out burn,
                out runway,
                out hoursToReset,
                out observedHours,
                out confidence))
        {
            tile.FiveHourBurnRateKnown = true;
            tile.FiveHourBurnPercentPerHour = burn;
            tile.FiveHourRunwayHours = runway;
            tile.FiveHourHoursToReset = hoursToReset;
            tile.FiveHourBurnConfidence = confidence;
        }
        else if (!quota.FiveHourLimitAbsent &&
                 TryComputeQuotaBurnRate(
                    state.FiveHourWallBurnSamples,
                    true,
                    FiveHourBurnRateWindowWallHours,
                    BurnRateMinimumWallMinutes,
                    quota.FiveHourPercent,
                    quota.FiveHourResetKnown,
                    quota.FiveHourResetLocal,
                    nowLocal,
                    out burn,
                    out runway,
                    out hoursToReset,
                    out observedHours,
                    out confidence))
        {
            tile.FiveHourBurnRateKnown = true;
            tile.FiveHourBurnPercentPerHour = burn;
            tile.FiveHourRunwayHours = runway;
            tile.FiveHourHoursToReset = hoursToReset;
            tile.FiveHourBurnConfidence = confidence;
        }
        else if (!quota.FiveHourLimitAbsent &&
                 quota.FiveHourResetKnown &&
                 quota.FiveHourResetLocal != DateTime.MinValue)
        {
            tile.FiveHourHoursToReset = (quota.FiveHourResetLocal - nowLocal).TotalHours;
        }
    }
}
