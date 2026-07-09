using System.Reflection;

[assembly: AssemblyTitle(ProductIdentity.DisplayName)]
[assembly: AssemblyProduct(ProductIdentity.DisplayName)]
[assembly: AssemblyDescription("UX3407N / UX3607O dedicated desktop Codex assistant for Windows on Arm.")]
[assembly: AssemblyCompany("Desktop Codex Assistant contributors")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Desktop Codex Assistant contributors")]
[assembly: AssemblyVersion(ProductIdentity.Version)]
[assembly: AssemblyFileVersion(ProductIdentity.Version)]
[assembly: AssemblyInformationalVersion(ProductIdentity.Version)]

internal static class ProductIdentity
{
    public const string SupportedDeviceLabel = "UX3407N / UX3607O";
    public const string DisplayName = "Desktop Codex Assistant UX3407N/UX3607O";
    public const string Version = "1.0.4.95";
    public const string MachineName = "DesktopCodexAssistant";
    public const string ExecutableName = MachineName + ".exe";
    public const string LogFileName = MachineName + ".log";
    public const string UserAgent = MachineName;
    public static readonly string[] LegacyStorageDirectoryNames = new string[]
    {
        "CodexDeveloperAssistantWindowOnWOA",
        "DesktopPerfWidget-Lite"
    };
    public static readonly string[] LegacyRunValueNames = new string[]
    {
        "CodexDeveloperAssistantWindowOnWOA",
        "DesktopPerfWidgetLiteArm64"
    };
    public static readonly string[] LegacyStopEventNames = new string[]
    {
        @"Local\CodexDeveloperAssistantWindowOnWOAStop",
        @"Local\DesktopPerfWidgetLiteArm64Stop"
    };
}
