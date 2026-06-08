param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "DesktopCodexAssistant-x64.exe")
)

$ErrorActionPreference = "Stop"

& (Join-Path $PSScriptRoot "Build-Arm64.ps1") -OutputPath $OutputPath -Platform x64
