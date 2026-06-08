param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "DesktopCodexAssistant.exe"),
    [ValidateSet("arm64", "x64")]
    [string]$Platform = "arm64"
)

$ErrorActionPreference = "Stop"

$compilerCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\17\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe")
)
$mainSource = Join-Path $PSScriptRoot "DesktopCodexAssistant.cs"
$sourceDirectories = @(
    "Core",
    "Settings",
    "Performance",
    "Interop"
)

$compiler = $compilerCandidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
if (-not $compiler) {
    throw "A Roslyn C# compiler with /platform:$Platform support was not found. Install Visual Studio Build Tools 2022 or newer."
}

if (-not (Test-Path -LiteralPath $mainSource)) {
    throw "Source file was not found: $mainSource"
}

$sourceFiles = @($mainSource)
foreach ($directory in $sourceDirectories) {
    $sourceRoot = Join-Path $PSScriptRoot $directory
    if (-not (Test-Path -LiteralPath $sourceRoot)) {
        throw "Source directory was not found: $sourceRoot"
    }

    $sourceFiles += Get-ChildItem -Path $sourceRoot -Recurse -Filter *.cs -File |
        Sort-Object FullName |
        ForEach-Object { $_.FullName }
}

$outputDirectory = Split-Path -Parent $OutputPath
if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$windowsWinmd = Get-ChildItem -Path (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\UnionMetadata") -Recurse -Filter Windows.winmd -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch "\\Facade\\" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
$frameworkPriority = if ($Platform -eq "x64") {
    @("Framework64", "FrameworkArm64", "Framework")
}
else {
    @("FrameworkArm64", "Framework64", "Framework")
}

$windowsRuntime = $frameworkPriority | ForEach-Object {
    "C:\Windows\Microsoft.NET\$_\v4.0.30319\System.Runtime.WindowsRuntime.dll"
} | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$systemRuntimeCandidates = @($frameworkPriority | ForEach-Object {
    "C:\Windows\Microsoft.NET\$_\v4.0.30319\System.Runtime.dll"
})
$systemRuntimeCandidates += "C:\Windows\Microsoft.NET\assembly\GAC_MSIL\System.Runtime\v4.0_4.0.0.0__b03f5f7f11d50a3a\System.Runtime.dll"
$systemRuntime = $systemRuntimeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$uiAutomationClient = $frameworkPriority | ForEach-Object {
    "C:\Windows\Microsoft.NET\$_\v4.0.30319\WPF\UIAutomationClient.dll"
} | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$uiAutomationTypes = $frameworkPriority | ForEach-Object {
    "C:\Windows\Microsoft.NET\$_\v4.0.30319\WPF\UIAutomationTypes.dll"
} | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$windowsBase = $frameworkPriority | ForEach-Object {
    "C:\Windows\Microsoft.NET\$_\v4.0.30319\WPF\WindowsBase.dll"
} | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $windowsWinmd -or -not $windowsRuntime -or -not $systemRuntime -or -not $uiAutomationClient -or -not $uiAutomationTypes -or -not $windowsBase) {
    throw "Windows SDK WinRT metadata was not found. Install the Windows 10/11 SDK."
}

$compilerArgs = @(
    "/nologo",
    "/target:winexe",
    "/platform:$Platform",
    "/optimize+",
    "/reference:System.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Management.dll",
    "/reference:System.Web.Extensions.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:$systemRuntime",
    "/reference:$uiAutomationClient",
    "/reference:$uiAutomationTypes",
    "/reference:$windowsBase",
    "/reference:$($windowsWinmd.FullName)",
    "/reference:$windowsRuntime",
    "/out:$OutputPath"
) + $sourceFiles

& $compiler @compilerArgs

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

$assetSource = Join-Path $PSScriptRoot "Assets"
if ($outputDirectory -and
    (Test-Path -LiteralPath $assetSource) -and
    ([IO.Path]::GetFullPath($outputDirectory).TrimEnd('\') -ine [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\'))) {
    Copy-Item -LiteralPath $assetSource -Destination $outputDirectory -Recurse -Force
}

Write-Host "Built $OutputPath"
