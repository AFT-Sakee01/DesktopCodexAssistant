using System;
using System.Drawing;
using System.Globalization;

internal enum RadarClockDialPhase
{
    NoData,
    CurrentCycle,
    WaitingCycle,
    MissedCycle
}

internal sealed class RadarClockDialInput
{
    public bool BatchKnown;
    public DateTime BatchTimeLocal;
    public bool LocalKnown;
    public DateTime RefreshMarkerTimeLocal;
    public double CycleHours;
    public DateTime NowLocal;
    public DateTime NowUtc;
    public bool RequestRunning;
    public int RenderTick;
    public string DataLabelText;
    public RadarClockTimeDisplayMode TimeDisplayMode;
    public bool LastAttemptKnown;
    public DateTime LastAttemptLocal;
    public bool LastActualKnown;
    public DateTime LastActualLocal;
    // Optional Codex task-ring projection. Null keeps consumers on the clock-only state.
    public CodexTaskRingModel TaskRing;
}

internal sealed class RadarClockDialState
{
    public RadarClockDialPhase Phase;
    public Color StatusColor;
    public float CurrentAngle;
    public bool RefreshMarkerVisible;
    public float RefreshMarkerAngle;
    public float ArcStartAngle;
    public float ArcSweepDegrees;
    public bool WarningRingVisible;
    public bool SecondRunBadge;
    public string DateText;
    public string TimeText;
    public string ModeLabel;
    public CodexTaskRingModel TaskRing;
}

// Owns the side-effect-free Radar clock state and task-ring projection.
internal static class RadarClockDial
{
    private const float BoundaryAngle = -90.0f;

    internal static RadarClockDialPhase ComputePhase(
        bool batchKnown,
        DateTime batchTime,
        bool localKnown,
        double cycleHours,
        DateTime now)
    {
        if (!batchKnown && !localKnown)
        {
            return RadarClockDialPhase.NoData;
        }

        DateTime boundary = GetCycleBoundaryLocal(now, cycleHours);
        DateTime previousBoundary = boundary.AddHours(-cycleHours);
        if (batchTime >= boundary)
        {
            return RadarClockDialPhase.CurrentCycle;
        }

        if (batchTime >= previousBoundary)
        {
            return RadarClockDialPhase.CurrentCycle;
        }

        return batchTime >= previousBoundary.AddHours(-cycleHours)
            ? RadarClockDialPhase.WaitingCycle
            : RadarClockDialPhase.MissedCycle;
    }

    internal static Color GetPhaseColor(RadarClockDialPhase phase)
    {
        switch (phase)
        {
            case RadarClockDialPhase.CurrentCycle:
                // SuccessSoft avoids a GDI+ PArgb color-key collision observed with the primary
                // success RGB while keeping the approved green state fully opaque.
                return DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, 255);
            case RadarClockDialPhase.WaitingCycle:
                return DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245);
            case RadarClockDialPhase.MissedCycle:
                return DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245);
            case RadarClockDialPhase.NoData:
            default:
                return DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230);
        }
    }

    internal static RadarClockDialState ComputeState(RadarClockDialInput input)
    {
        if (input == null)
        {
            throw new ArgumentNullException("input");
        }

        double cycleHours = input.CycleHours > 0.0 ? input.CycleHours : 1.0;
        RadarClockDialPhase phase = ComputePhase(
            input.BatchKnown,
            input.BatchTimeLocal,
            input.LocalKnown,
            cycleHours,
            input.NowLocal);
        Color statusColor = GetPhaseColor(phase);
        if (input.RequestRunning && (input.RenderTick & 1) == 0)
        {
            statusColor = DesignTokens.WithAlpha(statusColor, 104);
        }

        DateTime boundary = GetCycleBoundaryLocal(input.NowLocal, cycleHours);
        double elapsedHours = Math.Max(0.0, Math.Min(cycleHours, (input.NowLocal - boundary).TotalHours));
        float elapsedSweep = (float)(elapsedHours / cycleHours * 360.0);
        float currentAngle = BoundaryAngle + elapsedSweep;
        float refreshMarkerAngle;
        bool refreshMarkerVisible = GetMarkerAngle(
            input.RefreshMarkerTimeLocal,
            input.NowLocal,
            boundary,
            cycleHours,
            out refreshMarkerAngle);

        float arcStartAngle = refreshMarkerVisible ? refreshMarkerAngle : BoundaryAngle;
        // A completed request without a model batch keeps the legacy red status color, but the
        // missing timestamp cannot prove how many cycle boundaries elapsed. Do not invent a
        // boundary arc or full warning ring until BatchKnown provides that temporal evidence.
        bool batchAgeKnown = input.BatchKnown;
        float arcSweep = refreshMarkerVisible
            ? NormalizeSweep(refreshMarkerAngle, currentAngle)
            : (batchAgeKnown &&
                (phase == RadarClockDialPhase.WaitingCycle || phase == RadarClockDialPhase.MissedCycle)
                ? elapsedSweep
                : 0.0f);
        bool warningRingVisible = batchAgeKnown && phase == RadarClockDialPhase.MissedCycle;
        if (warningRingVisible)
        {
            arcSweep = Math.Max(2.0f, arcSweep);
        }

        string labelMain;
        string labelSuffix;
        SplitDataLabel(input.DataLabelText, out labelMain, out labelSuffix);

        return new RadarClockDialState
        {
            Phase = phase,
            StatusColor = statusColor,
            CurrentAngle = currentAngle,
            RefreshMarkerVisible = refreshMarkerVisible,
            RefreshMarkerAngle = refreshMarkerAngle,
            ArcStartAngle = arcStartAngle,
            ArcSweepDegrees = arcSweep,
            WarningRingVisible = warningRingVisible,
            SecondRunBadge = HasSecondRunSuffix(labelSuffix),
            DateText = FormatDate(labelMain),
            TimeText = GetTimeText(input),
            ModeLabel = GetModeLabel(input.TimeDisplayMode),
            TaskRing = input.TaskRing != null && input.TaskRing.HasSegments ? input.TaskRing : null
        };
    }

    internal static DateTime GetCycleBoundaryLocal(DateTime now, double cycleHours)
    {
        if (cycleHours >= 23.5)
        {
            return now.Date;
        }

        int cycle = Math.Max(1, (int)Math.Round(cycleHours));
        int hour = (now.Hour / cycle) * cycle;
        return now.Date.AddHours(hour);
    }

    internal static bool GetMarkerAngle(
        DateTime markerTime,
        DateTime now,
        DateTime cycleBoundary,
        double cycleHours,
        out float angle)
    {
        angle = BoundaryAngle;
        if (markerTime == DateTime.MinValue || cycleHours <= 0.0)
        {
            return false;
        }

        double ageHours = (now - markerTime).TotalHours;
        if (ageHours < 0.0 || ageHours >= cycleHours)
        {
            return false;
        }

        double elapsedHours = (markerTime - cycleBoundary).TotalHours;
        while (elapsedHours < 0.0)
        {
            elapsedHours += cycleHours;
        }

        while (elapsedHours >= cycleHours)
        {
            elapsedHours -= cycleHours;
        }

        angle = BoundaryAngle + (float)(elapsedHours / cycleHours * 360.0);
        return true;
    }

    internal static float NormalizeSweep(float startAngle, float endAngle)
    {
        float sweep = endAngle - startAngle;
        while (sweep < 0.0f)
        {
            sweep += 360.0f;
        }

        while (sweep > 360.0f)
        {
            sweep -= 360.0f;
        }

        return sweep;
    }

    internal static void SplitDataLabel(string dataLabelText, out string main, out string suffix)
    {
        string raw = (dataLabelText ?? string.Empty).Trim();
        int underscore = raw.IndexOf('_');
        if (underscore > 0 && underscore < raw.Length - 1)
        {
            main = raw.Substring(0, underscore);
            suffix = raw.Substring(underscore + 1);
            return;
        }

        int space = raw.LastIndexOf(' ');
        if (space > 0 && space < raw.Length - 1 && raw.IndexOf(':', space) > space)
        {
            main = raw.Substring(0, space);
            suffix = raw.Substring(space + 1);
            return;
        }

        main = raw;
        suffix = string.Empty;
    }

    internal static bool HasSecondRunSuffix(string suffix)
    {
        string raw = (suffix ?? string.Empty).Trim().ToLowerInvariant();
        int prefixLength;
        if (raw.StartsWith("pm", StringComparison.Ordinal))
        {
            prefixLength = 2;
        }
        else if (raw.StartsWith("n", StringComparison.Ordinal))
        {
            prefixLength = 1;
        }
        else
        {
            return false;
        }

        string rest = raw.Substring(prefixLength).TrimStart('_', '-', ' ');
        int run;
        return rest.Length > 0 &&
            int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out run) &&
            run >= 2;
    }

    internal static string FormatDate(string value)
    {
        string raw = (value ?? string.Empty).Trim();
        if (raw.Length == 0 || raw.IndexOf('月') >= 0)
        {
            return raw;
        }

        int separator = raw.IndexOfAny(new char[] { '.', '/' });
        int month;
        int day;
        if (separator > 0 && separator < raw.Length - 1 &&
            int.TryParse(raw.Substring(0, separator), NumberStyles.Integer, CultureInfo.InvariantCulture, out month) &&
            int.TryParse(raw.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out day) &&
            month >= 1 && month <= 12 && day >= 1 && day <= 31)
        {
            return month.ToString(CultureInfo.InvariantCulture) + "月" + day.ToString(CultureInfo.InvariantCulture) + "日";
        }

        return raw;
    }

    internal static void RunSelfTest()
    {
        DateTime truthNow = new DateTime(2026, 7, 7, 13, 0, 0);
        RunCycleTruthTable(12.0, truthNow, new DateTime(2026, 7, 7, 13, 0, 0), new DateTime(2026, 7, 7, 0, 0, 0), new DateTime(2026, 7, 6, 12, 0, 0), new DateTime(2026, 7, 6, 0, 0, 0));
        RunCycleTruthTable(24.0, truthNow, new DateTime(2026, 7, 7, 13, 0, 0), new DateTime(2026, 7, 6, 0, 0, 0), new DateTime(2026, 7, 5, 0, 0, 0), new DateTime(2026, 7, 4, 0, 0, 0));

        DateTime legacyNow = new DateTime(2026, 7, 7, 13, 30, 0);
        DateTime codexBoundary = GetCycleBoundaryLocal(legacyNow, 12.0);
        float markerAngle;
        if (!GetMarkerAngle(new DateTime(2026, 7, 7, 12, 15, 0), legacyNow, codexBoundary, 12.0, out markerAngle) ||
            Math.Abs(markerAngle - (-82.5f)) > 0.01f)
        {
            throw new InvalidOperationException("Codex 12h refresh marker angle should advance clockwise from the top boundary.");
        }

        if (!GetMarkerAngle(new DateTime(2026, 7, 7, 11, 0, 0), legacyNow, codexBoundary, 12.0, out markerAngle) ||
            Math.Abs(markerAngle - 240.0f) > 0.01f)
        {
            throw new InvalidOperationException("Codex 12h refresh marker should remain visible across the boundary until one full lap.");
        }

        float codexCurrentAngle = BoundaryAngle + (float)((legacyNow - codexBoundary).TotalHours / 12.0 * 360.0);
        if (Math.Abs(NormalizeSweep(markerAngle, codexCurrentAngle) - 75.0f) > 0.01f ||
            GetMarkerAngle(legacyNow.AddHours(-12.0), legacyNow, codexBoundary, 12.0, out markerAngle))
        {
            throw new InvalidOperationException("Codex 12h refresh marker retention or sweep changed.");
        }

        DateTime claudeBoundary = GetCycleBoundaryLocal(legacyNow, 24.0);
        if (!GetMarkerAngle(new DateTime(2026, 7, 7, 1, 0, 0), legacyNow, claudeBoundary, 24.0, out markerAngle) ||
            Math.Abs(markerAngle - (-75.0f)) > 0.01f)
        {
            throw new InvalidOperationException("Claude 24h refresh marker angle should advance clockwise from midnight.");
        }

        if (!GetMarkerAngle(legacyNow.AddHours(-23.9), legacyNow, claudeBoundary, 24.0, out markerAngle))
        {
            throw new InvalidOperationException("Claude 24h refresh marker should remain before one full lap.");
        }

        float claudeCurrentAngle = BoundaryAngle + (float)((legacyNow - claudeBoundary).TotalHours / 24.0 * 360.0);
        if (Math.Abs(NormalizeSweep(markerAngle, claudeCurrentAngle) - 358.5f) > 0.01f ||
            GetMarkerAngle(legacyNow.AddHours(-24.0), legacyNow, claudeBoundary, 24.0, out markerAngle))
        {
            throw new InvalidOperationException("Claude 24h refresh marker retention or sweep changed.");
        }

        AssertSecondRun("7.7_pm2", true);
        AssertSecondRun("7.7_pm_2", true);
        AssertSecondRun("7.7_pm-2", true);
        AssertSecondRun("7.7_n2", true);
        AssertSecondRun("7.7_n_2", true);
        AssertSecondRun("7.7_n-2", true);
        AssertSecondRun("7.7_n", false);
        AssertSecondRun("7.7_am", false);
        AssertSecondRun("7.7_pm", false);
        AssertSecondRun("7/7 07:30", false);

        RadarClockDialInput firstRunInput = CreateTestInput(12.0, truthNow, new DateTime(2026, 7, 7, 12, 0, 0), truthNow);
        firstRunInput.DataLabelText = "7.7_n";
        RadarClockDialState firstRun = ComputeState(firstRunInput);
        firstRunInput.DataLabelText = "7.7_n2";
        RadarClockDialState secondRun = ComputeState(firstRunInput);
        if (firstRun.Phase != secondRun.Phase || firstRun.SecondRunBadge || !secondRun.SecondRunBadge)
        {
            throw new InvalidOperationException("Radar clock same-window n to n2 publication changed phase or failed to toggle the second-run badge.");
        }

        RadarClockDialInput blinkingInput = CreateTestInput(12.0, truthNow, truthNow, truthNow);
        blinkingInput.RequestRunning = true;
        blinkingInput.RenderTick = 2;
        if (ComputeState(blinkingInput).StatusColor.A != 104)
        {
            throw new InvalidOperationException("Radar clock request blink should use alpha 104 on even frames.");
        }

        blinkingInput.RenderTick = 3;
        if (ComputeState(blinkingInput).StatusColor.A != 255)
        {
            throw new InvalidOperationException("Radar clock request blink should preserve status alpha on odd frames.");
        }

        if (!string.Equals(FormatDate("7.6"), "7月6日", StringComparison.Ordinal) ||
            !string.Equals(FormatDate("7/6"), "7月6日", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Radar clock date formatting changed.");
        }

        // Task-ring pass-through: absent and empty rings stay out of the projected clock state.
        RadarClockDialInput ringInput = CreateTestInput(12.0, truthNow, truthNow, truthNow);
        if (ComputeState(ringInput).TaskRing != null)
        {
            throw new InvalidOperationException("Radar clock without a task ring must not expose ring state.");
        }

        ringInput.TaskRing = new CodexTaskRingModel();
        if (ComputeState(ringInput).TaskRing != null)
        {
            throw new InvalidOperationException("Radar clock must treat an empty task ring as absent.");
        }

        ringInput.TaskRing = CodexTaskPresentation.BuildRing(CodexTaskPresentation.CreateFixtureSnapshot(truthNow));
        RadarClockDialState ringState = ComputeState(ringInput);
        if (ringState.TaskRing == null || ringState.TaskRing.Segments.Count != 4)
        {
            throw new InvalidOperationException("Radar clock should pass a populated task ring through to projected state.");
        }
    }

    private static void RunCycleTruthTable(
        double cycleHours,
        DateTime now,
        DateTime currentBatch,
        DateTime oneWindowLateBatch,
        DateTime waitingBatch,
        DateTime missedBatch)
    {
        RadarClockDialState current = ComputeState(CreateTestInput(cycleHours, now, currentBatch, now));
        AssertState(current, RadarClockDialPhase.CurrentCycle, DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, 255), "current cycle");

        RadarClockDialState oneWindowLate = ComputeState(CreateTestInput(cycleHours, now, oneWindowLateBatch, DateTime.MinValue));
        AssertState(oneWindowLate, RadarClockDialPhase.CurrentCycle, DesignTokens.WithAlpha(DesignTokens.Colors.SuccessSoft, 255), "one-window-late current cycle");
        if (oneWindowLate.WarningRingVisible || oneWindowLate.ArcSweepDegrees != 0.0f)
        {
            throw new InvalidOperationException("Radar clock one-window-late current cycle should remain green without warning geometry.");
        }

        RadarClockDialInput waitingInput = CreateTestInput(cycleHours, now, waitingBatch, DateTime.MinValue);
        RadarClockDialState waiting = ComputeState(waitingInput);
        AssertState(waiting, RadarClockDialPhase.WaitingCycle, DesignTokens.WithAlpha(DesignTokens.Colors.Warning, 245), "waiting cycle");
        if (waiting.WarningRingVisible || waiting.ArcSweepDegrees <= 0.0f || waiting.ArcStartAngle != BoundaryAngle)
        {
            throw new InvalidOperationException("Radar clock waiting cycle arc should run from the current boundary.");
        }

        RadarClockDialState missed = ComputeState(CreateTestInput(cycleHours, now, missedBatch, DateTime.MinValue));
        AssertState(missed, RadarClockDialPhase.MissedCycle, DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245), "missed cycle");
        if (!missed.WarningRingVisible || missed.ArcSweepDegrees < 2.0f)
        {
            throw new InvalidOperationException("Radar clock missed cycle should show its warning ring and minimum arc.");
        }

        RadarClockDialInput noDataInput = CreateTestInput(cycleHours, now, DateTime.MinValue, DateTime.MinValue);
        noDataInput.BatchKnown = false;
        noDataInput.LocalKnown = false;
        RadarClockDialState noData = ComputeState(noDataInput);
        AssertState(noData, RadarClockDialPhase.NoData, DesignTokens.WithAlpha(DesignTokens.Colors.GlyphMuted, 230), "no data");
        if (noData.WarningRingVisible || noData.ArcSweepDegrees != 0.0f)
        {
            throw new InvalidOperationException("Radar clock no-data phase should not draw a status arc.");
        }

        RadarClockDialInput checkedWithoutBatchInput = CreateTestInput(cycleHours, now, DateTime.MinValue, now);
        checkedWithoutBatchInput.BatchKnown = false;
        checkedWithoutBatchInput.LocalKnown = true;
        RadarClockDialState checkedWithoutBatch = ComputeState(checkedWithoutBatchInput);
        AssertState(checkedWithoutBatch, RadarClockDialPhase.MissedCycle, DesignTokens.WithAlpha(DesignTokens.Colors.Danger, 245), "checked without batch");
        if (checkedWithoutBatch.WarningRingVisible || checkedWithoutBatch.ArcSweepDegrees != 0.0f)
        {
            throw new InvalidOperationException("Radar clock must not infer a cycle arc when the batch timestamp is unknown.");
        }

        DateTime boundary = GetCycleBoundaryLocal(now, cycleHours);
        float markerAngle;
        if (GetMarkerAngle(now.AddHours(-cycleHours), now, boundary, cycleHours, out markerAngle))
        {
            throw new InvalidOperationException("Radar clock refresh marker should expire at one full cycle.");
        }

        if (!GetMarkerAngle(boundary.AddMinutes(-30.0), now, boundary, cycleHours, out markerAngle) ||
            markerAngle < -90.0f || markerAngle >= 270.0f ||
            NormalizeSweep(markerAngle, current.CurrentAngle) < 0.0f ||
            NormalizeSweep(markerAngle, current.CurrentAngle) > 360.0f)
        {
            throw new InvalidOperationException("Radar clock cross-boundary marker geometry is out of range.");
        }
    }

    private static RadarClockDialInput CreateTestInput(
        double cycleHours,
        DateTime now,
        DateTime batchTime,
        DateTime markerTime)
    {
        return new RadarClockDialInput
        {
            BatchKnown = batchTime != DateTime.MinValue,
            BatchTimeLocal = batchTime,
            LocalKnown = markerTime != DateTime.MinValue,
            RefreshMarkerTimeLocal = markerTime,
            CycleHours = cycleHours,
            NowLocal = now,
            NowUtc = new DateTime(2026, 7, 7, 4, 0, 0, DateTimeKind.Utc),
            DataLabelText = "7.7_pm",
            TimeDisplayMode = RadarClockTimeDisplayMode.Utc,
            LastAttemptKnown = true,
            LastAttemptLocal = now.AddMinutes(-15.0),
            LastActualKnown = markerTime != DateTime.MinValue,
            LastActualLocal = markerTime
        };
    }

    private static void AssertState(
        RadarClockDialState state,
        RadarClockDialPhase phase,
        Color color,
        string scenario)
    {
        if (state.Phase != phase || state.StatusColor.ToArgb() != color.ToArgb())
        {
            throw new InvalidOperationException("Radar clock " + scenario + " phase or color changed.");
        }
    }

    private static void AssertSecondRun(string label, bool expected)
    {
        RadarClockDialInput input = CreateTestInput(24.0, new DateTime(2026, 7, 7, 13, 0, 0), new DateTime(2026, 7, 7, 1, 0, 0), DateTime.MinValue);
        input.DataLabelText = label;
        if (ComputeState(input).SecondRunBadge != expected)
        {
            throw new InvalidOperationException("Radar clock second-run suffix parsing changed for " + label + ".");
        }
    }

    private static string GetTimeText(RadarClockDialInput input)
    {
        switch (input.TimeDisplayMode)
        {
            case RadarClockTimeDisplayMode.CurrentLocal:
                return input.NowLocal.ToString("HH:mm", CultureInfo.CurrentCulture);
            case RadarClockTimeDisplayMode.LastAttemptRefresh:
                return input.LastAttemptKnown && input.LastAttemptLocal != DateTime.MinValue
                    ? input.LastAttemptLocal.ToString("HH:mm", CultureInfo.CurrentCulture)
                    : "--:--";
            case RadarClockTimeDisplayMode.LastActualRefresh:
                return input.LastActualKnown && input.LastActualLocal != DateTime.MinValue
                    ? input.LastActualLocal.ToString("HH:mm", CultureInfo.CurrentCulture)
                    : "--:--";
            case RadarClockTimeDisplayMode.Utc:
            default:
                return input.NowUtc.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
    }

    private static string GetModeLabel(RadarClockTimeDisplayMode mode)
    {
        switch (mode)
        {
            case RadarClockTimeDisplayMode.CurrentLocal:
                return "NOW";
            case RadarClockTimeDisplayMode.LastAttemptRefresh:
                return "LAST";
            case RadarClockTimeDisplayMode.LastActualRefresh:
                return "REF";
            case RadarClockTimeDisplayMode.Utc:
            default:
                return "UTC";
        }
    }

}
