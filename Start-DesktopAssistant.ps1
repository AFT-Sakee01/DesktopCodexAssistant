param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'DesktopCodexAssistant.exe')
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
if ([IO.Path]::GetFileName($executable) -ne 'DesktopCodexAssistant.exe') {
    throw 'Select the formal DesktopCodexAssistant.exe executable.'
}
$session = (Get-Process -Id $PID).SessionId
$existing = @(Get-Process DesktopCodexAssistant -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $session })
if ($existing.Count -gt 0) {
    throw 'An instance already exists. Inspect it or stop it with --stop before launching.'
}

# Direct Start-Process from an agent/terminal can inherit its kill-on-close job.
# Local WMI brokers creation outside that caller tree; break away from the provider
# job as well. Never fall back to a direct child if detached creation fails.
$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{
    ShowWindow = [uint16]0
    CreateFlags = [uint32]0x01000000
}
$result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
    CommandLine = ('"{0}"' -f $executable)
    CurrentDirectory = [IO.Path]::GetDirectoryName($executable)
    ProcessStartupInformation = $startup
}
if ($result.ReturnValue -ne 0) {
    throw "Detached launch failed: WMI return code $($result.ReturnValue)."
}

# Give the singleton/message loop time to fail before reporting successful startup.
Start-Sleep -Seconds 5
$process = Get-Process -Id $result.ProcessId -ErrorAction Stop
$metadata = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)"
$parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($metadata.ParentProcessId)"
# Windows may assign its own jobs even with BREAKAWAY. Verify broker ancestry,
# not absence of every OS job; caller-job lifetime is covered by the regression test.
if ($parent.Name -ne 'WmiPrvSE.exe' -or $process.SessionId -ne $session) {
    throw "Launch verification failed for PID $($process.Id): parent=$($parent.Name), session=$($process.SessionId)."
}
[pscustomobject]@{
    ProcessId = $process.Id
    SessionId = $process.SessionId
    ParentProcessId = $metadata.ParentProcessId
    ParentName = $parent.Name
    Version = $process.MainModule.FileVersionInfo.FileVersion
    Path = $process.Path
}
