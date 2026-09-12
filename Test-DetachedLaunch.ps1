param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'DesktopCodexAssistant.exe'),
    [switch]$DirectChildControl
)
$ErrorActionPreference = 'Stop'
if (Get-Process DesktopCodexAssistant -ErrorAction SilentlyContinue) {
    throw 'Stop the resident instance with --stop before this integration test.'
}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LaunchJobTest {
    [StructLayout(LayoutKind.Sequential)] public struct Basic {
        public long ProcessTime, JobTime; public uint Flags;
        public UIntPtr Min, Max; public uint Active; public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Io {
        public ulong A, B, C, D, E, F;
    }
    [StructLayout(LayoutKind.Sequential)] public struct Extended {
        public Basic Basic; public Io Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateJobObject(IntPtr attrs, string name);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetInformationJobObject(IntPtr job, int info, ref Extended data, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
}
'@
$root = Join-Path ([IO.Path]::GetTempPath()) ('dca-launch-test-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $root)
$ready = Join-Path $root 'ready'
$resultPath = Join-Path $root 'result.json'
$errorPath = Join-Path $root 'error.txt'
$scriptPath = Join-Path $root 'caller.ps1'
$launcher = Join-Path $PSScriptRoot 'Start-DesktopAssistant.ps1'
# The caller waits for assignment, so the launch cannot race ahead of the kill job.
$wrapper = @'
$ErrorActionPreference = 'Stop'
while (-not (Test-Path -LiteralPath '__READY__')) { Start-Sleep -Milliseconds 100 }
try {
    if (__DIRECT__) {
        $child = Start-Process -FilePath '__EXE__' -WindowStyle Hidden -PassThru
        Start-Sleep -Seconds 5
        @{ ProcessId=$child.Id; Version=$child.MainModule.FileVersionInfo.FileVersion; ParentName='direct-caller' } | ConvertTo-Json | Set-Content -LiteralPath '__RESULT__'
    } else {
        & '__LAUNCHER__' -ExecutablePath '__EXE__' | ConvertTo-Json | Set-Content -LiteralPath '__RESULT__'
    }
} catch { $_ | Out-String | Set-Content -LiteralPath '__ERROR__' }
Start-Sleep -Seconds 60
'@
$wrapper = $wrapper.Replace('__DIRECT__', ('$' + $DirectChildControl.IsPresent.ToString().ToLowerInvariant()))
foreach ($pair in @(@('__READY__',$ready),@('__LAUNCHER__',$launcher),@('__EXE__',$ExecutablePath),@('__RESULT__',$resultPath),@('__ERROR__',$errorPath))) {
    $wrapper = $wrapper.Replace($pair[0], $pair[1].Replace("'", "''"))
}
Set-Content -LiteralPath $scriptPath -Value $wrapper -Encoding UTF8
$job = [LaunchJobTest]::CreateJobObject([IntPtr]::Zero, $null)
$caller = $null
try {
    $limits = New-Object LaunchJobTest+Extended
    $basic = New-Object LaunchJobTest+Basic
    $basic.Flags = 0x2000 # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE; no breakaway permission.
    $limits.Basic = $basic # Nested value types must be assigned back in PowerShell.
    if ($job -eq [IntPtr]::Zero -or -not [LaunchJobTest]::SetInformationJobObject($job,9,[ref]$limits,[Runtime.InteropServices.Marshal]::SizeOf($limits))) {
        throw 'Cannot create the synthetic caller job.'
    }
    $caller = Start-Process powershell.exe -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',('"{0}"' -f $scriptPath))
    if (-not [LaunchJobTest]::AssignProcessToJobObject($job,$caller.Handle)) { throw 'Cannot assign the test caller to its job.' }
    Set-Content -LiteralPath $ready -Value 'go'
    $deadline = (Get-Date).AddSeconds(25)
    while (-not (Test-Path -LiteralPath $resultPath) -and -not (Test-Path -LiteralPath $errorPath) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Test-Path -LiteralPath $errorPath) { throw (Get-Content -LiteralPath $errorPath -Raw) }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Timed out awaiting detached startup.' }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $resident = Get-Process -Id $result.ProcessId -ErrorAction Stop
    [void]$resident.Handle # Retain process identity across termination/PID reuse.
    [void][LaunchJobTest]::CloseHandle($job)
    $job = [IntPtr]::Zero
    if (-not $caller.WaitForExit(5000)) { throw 'The synthetic caller job failed to terminate its caller.' }
    if ($DirectChildControl) {
        if (-not $resident.WaitForExit(5000)) { throw 'The direct-child control did not reproduce caller-job termination.' }
        "PASS direct_child_terminated_with_caller resident_pid=$($resident.Id) exit=$($resident.ExitCode)"
        return
    }
    Start-Sleep -Seconds 3
    if ($resident.HasExited -or -not $resident.Responding) { throw 'Resident failed after caller termination.' }
    "PASS caller_job_closed caller_pid=$($caller.Id) resident_pid=$($resident.Id) version=$($result.Version) parent=$($result.ParentName)"
    # Leave the verified formal instance running for the user.
} finally {
    if ($job -ne [IntPtr]::Zero) { [void][LaunchJobTest]::CloseHandle($job) }
    if ($caller) { $caller.Dispose() }
    # Only explicit files created above are removed; no recursive computed-path delete.
    foreach ($file in @($ready,$resultPath,$errorPath,$scriptPath)) { if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file } }
    Remove-Item -LiteralPath $root
}
