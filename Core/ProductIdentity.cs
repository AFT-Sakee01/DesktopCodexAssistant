using System.Reflection;

[assembly: AssemblyTitle(ProductIdentity.DisplayName)]
[assembly: AssemblyProduct(ProductIdentity.DisplayName)]
[assembly: AssemblyDescription("UX3407N / UX3607O dedicated developer assistance and system monitoring window for Windows on Arm.")]
[assembly: AssemblyCompany("Codex Developer Assistant Window on WOA UX3407N UX3607O contributors")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Codex Developer Assistant Window on WOA UX3407N UX3607O contributors")]
[assembly: AssemblyVersion(ProductIdentity.Version)]
[assembly: AssemblyFileVersion(ProductIdentity.Version)]
[assembly: AssemblyInformationalVersion(ProductIdentity.Version)]

internal static class ProductIdentity
{
    public const string SupportedDeviceLabel = "UX3407N / UX3607O";
    public const string DisplayName = "Codex Developer Assistant Window on WOA UX3407N/UX3607O";
    public const string Version = "1.0.1.0";
    public const string MachineName = "CodexDeveloperAssistantWindowOnWOA";
    public const string ExecutableName = MachineName + ".exe";
    public const string LogFileName = MachineName + ".log";
    public const string UserAgent = MachineName;
    public const string LegacyStorageDirectoryName = "DesktopPerfWidget-Lite";
    public const string LegacyRunValueName = "DesktopPerfWidgetLiteArm64";
    public const string LegacyStopEventName = @"Local\DesktopPerfWidgetLiteArm64Stop";
}
