using System;

internal enum SelectedQuotaRefreshTarget
{
    None,
    Codex,
    Claude
}

internal sealed class RadarSoftwareModeInput
{
    public CodexRadarSoftwareMode ConfiguredMode { get; set; }
    public CodexRadarSoftwareMode PreviousEffectiveMode { get; set; }
    public SoftwareRuntimePresenceSnapshot Presence { get; set; }
    public bool ForegroundDetected { get; set; }
    public CodexRadarSoftwareMode ForegroundMode { get; set; }
}

internal sealed class RadarSoftwareModeDecision
{
    public CodexRadarSoftwareMode PreviousEffectiveMode { get; set; }
    public CodexRadarSoftwareMode EffectiveMode { get; set; }
    public bool Changed { get; set; }
    public bool ShouldDetectForeground { get; set; }
    public SelectedQuotaRefreshTarget SelectedQuotaRefreshTarget { get; set; }
    public string Reason { get; set; }
}

// This controller is deliberately side-effect free: it owns only the Codex/Claude
// selection matrix. Window probing, cache writes, network requests, notifications, and
// painting stay in CodexRadarForm so a mode switch cannot mutate the wrong family state.
internal sealed class RadarSoftwareModeController
{
    public RadarSoftwareModeDecision Resolve(RadarSoftwareModeInput input)
    {
        RadarSoftwareModeInput local = input ?? new RadarSoftwareModeInput();
        CodexRadarSoftwareMode configured = NormalizeConfiguredMode(local.ConfiguredMode);
        CodexRadarSoftwareMode previous = NormalizeEffectiveSoftwareMode(local.PreviousEffectiveMode);
        SoftwareRuntimePresenceSnapshot presence = local.Presence ?? SoftwareRuntimePresenceSnapshot.Empty();
        bool shouldDetectForeground = ShouldDetectForeground(configured, presence);
        CodexRadarSoftwareMode next = ResolveEffectiveMode(
            configured,
            previous,
            presence,
            local.ForegroundDetected && shouldDetectForeground,
            local.ForegroundMode);

        return new RadarSoftwareModeDecision
        {
            PreviousEffectiveMode = previous,
            EffectiveMode = next,
            Changed = next != previous,
            ShouldDetectForeground = shouldDetectForeground,
            SelectedQuotaRefreshTarget = ResolveSelectedQuotaRefreshTarget(next, presence),
            Reason = BuildReason(configured, previous, next, presence, local.ForegroundDetected && shouldDetectForeground)
        };
    }

    public static bool ShouldDetectForeground(
        CodexRadarSoftwareMode configured,
        SoftwareRuntimePresenceSnapshot presence)
    {
        return NormalizeConfiguredMode(configured) == CodexRadarSoftwareMode.Auto &&
            presence != null &&
            presence.CodexRunning &&
            presence.ClaudeRunning;
    }

    public static CodexRadarSoftwareMode ResolveEffectiveMode(
        CodexRadarSoftwareMode configured,
        CodexRadarSoftwareMode previousEffective,
        SoftwareRuntimePresenceSnapshot presence,
        bool foregroundDetected,
        CodexRadarSoftwareMode foregroundMode)
    {
        configured = NormalizeConfiguredMode(configured);
        if (configured == CodexRadarSoftwareMode.Codex ||
            configured == CodexRadarSoftwareMode.Claude)
        {
            return configured;
        }

        SoftwareRuntimePresenceSnapshot localPresence = presence ?? SoftwareRuntimePresenceSnapshot.Empty();
        if (!localPresence.AnySupportedAppRunning)
        {
            return NormalizeEffectiveSoftwareMode(previousEffective);
        }

        if (localPresence.CodexRunning && !localPresence.ClaudeRunning)
        {
            return CodexRadarSoftwareMode.Codex;
        }

        if (localPresence.ClaudeRunning && !localPresence.CodexRunning)
        {
            return CodexRadarSoftwareMode.Claude;
        }

        if (foregroundDetected)
        {
            return foregroundMode == CodexRadarSoftwareMode.Claude
                ? CodexRadarSoftwareMode.Claude
                : CodexRadarSoftwareMode.Codex;
        }

        return NormalizeEffectiveSoftwareMode(previousEffective);
    }

    public static SelectedQuotaRefreshTarget ResolveSelectedQuotaRefreshTarget(
        CodexRadarSoftwareMode effectiveMode,
        SoftwareRuntimePresenceSnapshot presence)
    {
        SoftwareRuntimePresenceSnapshot localPresence = presence ?? SoftwareRuntimePresenceSnapshot.Empty();
        if (!localPresence.AnySupportedAppRunning)
        {
            return SelectedQuotaRefreshTarget.None;
        }

        if (NormalizeEffectiveSoftwareMode(effectiveMode) == CodexRadarSoftwareMode.Claude)
        {
            return localPresence.ClaudeRunning
                ? SelectedQuotaRefreshTarget.Claude
                : SelectedQuotaRefreshTarget.None;
        }

        return localPresence.CodexRunning
            ? SelectedQuotaRefreshTarget.Codex
            : SelectedQuotaRefreshTarget.None;
    }

    public static CodexRadarSoftwareMode NormalizeEffectiveSoftwareMode(CodexRadarSoftwareMode mode)
    {
        return mode == CodexRadarSoftwareMode.Claude
            ? CodexRadarSoftwareMode.Claude
            : CodexRadarSoftwareMode.Codex;
    }

    private static CodexRadarSoftwareMode NormalizeConfiguredMode(CodexRadarSoftwareMode mode)
    {
        if (mode == CodexRadarSoftwareMode.Codex ||
            mode == CodexRadarSoftwareMode.Claude ||
            mode == CodexRadarSoftwareMode.Auto)
        {
            return mode;
        }

        return CodexRadarSoftwareMode.Auto;
    }

    private static string BuildReason(
        CodexRadarSoftwareMode configured,
        CodexRadarSoftwareMode previous,
        CodexRadarSoftwareMode next,
        SoftwareRuntimePresenceSnapshot presence,
        bool foregroundUsed)
    {
        if (configured == CodexRadarSoftwareMode.Codex ||
            configured == CodexRadarSoftwareMode.Claude)
        {
            return "fixed_" + configured.ToString().ToLowerInvariant();
        }

        SoftwareRuntimePresenceSnapshot localPresence = presence ?? SoftwareRuntimePresenceSnapshot.Empty();
        if (!localPresence.AnySupportedAppRunning)
        {
            return "auto_no_supported_app_keep_" + previous.ToString().ToLowerInvariant();
        }

        if (localPresence.CodexRunning && !localPresence.ClaudeRunning)
        {
            return "auto_codex_only";
        }

        if (localPresence.ClaudeRunning && !localPresence.CodexRunning)
        {
            return "auto_claude_only";
        }

        if (foregroundUsed)
        {
            return "auto_both_foreground_" + next.ToString().ToLowerInvariant();
        }

        return "auto_both_foreground_unknown_keep_" + previous.ToString().ToLowerInvariant();
    }

    internal static void RunSelfTest()
    {
        SoftwareRuntimePresenceSnapshot none = new SoftwareRuntimePresenceSnapshot(false, false, DateTime.UtcNow, "test");
        SoftwareRuntimePresenceSnapshot codexOnly = new SoftwareRuntimePresenceSnapshot(true, false, DateTime.UtcNow, "test");
        SoftwareRuntimePresenceSnapshot claudeOnly = new SoftwareRuntimePresenceSnapshot(false, true, DateTime.UtcNow, "test");
        SoftwareRuntimePresenceSnapshot both = new SoftwareRuntimePresenceSnapshot(true, true, DateTime.UtcNow, "test");

        AssertMode(
            ResolveEffectiveMode(CodexRadarSoftwareMode.Codex, CodexRadarSoftwareMode.Claude, both, true, CodexRadarSoftwareMode.Claude),
            CodexRadarSoftwareMode.Codex,
            "fixed Codex should ignore foreground and presence");
        AssertMode(
            ResolveEffectiveMode(CodexRadarSoftwareMode.Claude, CodexRadarSoftwareMode.Codex, both, true, CodexRadarSoftwareMode.Codex),
            CodexRadarSoftwareMode.Claude,
            "fixed Claude should ignore foreground and presence");
        AssertMode(
            ResolveEffectiveMode(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Claude, none, false, CodexRadarSoftwareMode.Codex),
            CodexRadarSoftwareMode.Claude,
            "no supported apps should keep previous effective mode");
        AssertMode(
            ResolveEffectiveMode(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Claude, codexOnly, false, CodexRadarSoftwareMode.Claude),
            CodexRadarSoftwareMode.Codex,
            "Codex-only presence should select Codex without foreground detection");
        AssertMode(
            ResolveEffectiveMode(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Codex, claudeOnly, false, CodexRadarSoftwareMode.Codex),
            CodexRadarSoftwareMode.Claude,
            "Claude-only presence should select Claude without foreground detection");
        AssertMode(
            ResolveEffectiveMode(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Codex, both, true, CodexRadarSoftwareMode.Claude),
            CodexRadarSoftwareMode.Claude,
            "both running should accept foreground-detected Claude");
        AssertMode(
            ResolveEffectiveMode(CodexRadarSoftwareMode.Auto, CodexRadarSoftwareMode.Claude, both, false, CodexRadarSoftwareMode.Codex),
            CodexRadarSoftwareMode.Claude,
            "foreground detection failure should keep previous effective mode");

        if (ShouldDetectForeground(CodexRadarSoftwareMode.Codex, both) ||
            ShouldDetectForeground(CodexRadarSoftwareMode.Claude, both) ||
            ShouldDetectForeground(CodexRadarSoftwareMode.Auto, none) ||
            ShouldDetectForeground(CodexRadarSoftwareMode.Auto, codexOnly) ||
            ShouldDetectForeground(CodexRadarSoftwareMode.Auto, claudeOnly) ||
            !ShouldDetectForeground(CodexRadarSoftwareMode.Auto, both))
        {
            throw new InvalidOperationException("Radar software mode self-test failed: foreground detection gate.");
        }

        AssertTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Codex, none), SelectedQuotaRefreshTarget.None, "no apps should not queue quota refresh");
        AssertTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Codex, codexOnly), SelectedQuotaRefreshTarget.Codex, "Codex mode should queue Codex when Codex is running");
        AssertTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Codex, claudeOnly), SelectedQuotaRefreshTarget.None, "Codex mode should not queue Claude when only Claude is running");
        AssertTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Claude, claudeOnly), SelectedQuotaRefreshTarget.Claude, "Claude mode should queue Claude when Claude is running");
        AssertTarget(ResolveSelectedQuotaRefreshTarget(CodexRadarSoftwareMode.Claude, codexOnly), SelectedQuotaRefreshTarget.None, "Claude mode should not queue Codex when only Codex is running");

        RadarSoftwareModeController controller = new RadarSoftwareModeController();
        RadarSoftwareModeDecision decision = controller.Resolve(new RadarSoftwareModeInput
        {
            ConfiguredMode = CodexRadarSoftwareMode.Auto,
            PreviousEffectiveMode = CodexRadarSoftwareMode.Codex,
            Presence = both,
            ForegroundDetected = true,
            ForegroundMode = CodexRadarSoftwareMode.Claude
        });
        if (!decision.Changed ||
            !decision.ShouldDetectForeground ||
            decision.EffectiveMode != CodexRadarSoftwareMode.Claude ||
            decision.SelectedQuotaRefreshTarget != SelectedQuotaRefreshTarget.Claude)
        {
            throw new InvalidOperationException("Radar software mode self-test failed: decision shape.");
        }

        ClaudeRadarClockAutoSwitchSelector.RunSelfTest();
        RadarClockDial.RunSelfTest();
        CodexRadarModelCatalog.RunSelfTest();
        RadarBottomInfoTextRenderer.RunSelfTest();
    }

    private static void AssertMode(CodexRadarSoftwareMode actual, CodexRadarSoftwareMode expected, string message)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException("Radar software mode self-test failed: " + message);
        }
    }

    private static void AssertTarget(SelectedQuotaRefreshTarget actual, SelectedQuotaRefreshTarget expected, string message)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException("Radar quota provider self-test failed: " + message);
        }
    }
}
