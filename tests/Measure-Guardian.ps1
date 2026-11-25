[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [ValidateRange(5, 43200)]
    [int]$Seconds = 300,

    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts')
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this measurement in an elevated PowerShell session.'
    }
} finally {
    $identity.Dispose()
}

if ((Get-Process -Name SSDGuardian -ErrorAction SilentlyContinue) -or
    (logman.exe query -ets | Select-String -SimpleMatch 'SSD Guardian Kernel Monitor')) {
    throw 'A Guardian process or kernel session is already running. Use a free test machine.'
}

$application = Get-Item -LiteralPath $Executable
$output = (New-Item -ItemType Directory -Path $OutputDirectory -Force).FullName
$run = (New-Item -ItemType Directory -Path (Join-Path $output ('measurement-' + [Guid]::NewGuid().ToString('N')))).FullName
$profile = Join-Path $run 'profile'
$report = Join-Path $run 'measurement.json'
$launchArguments = "--tray --smoke-test --smoke-seconds $Seconds --data-dir `"$profile`" --smoke-output `"$report`""
$process = Start-Process -FilePath $application.FullName -ArgumentList $launchArguments `
    -WorkingDirectory $application.DirectoryName -WindowStyle Hidden -PassThru

try {
    Write-Output ("Measuring PID {0}. Report will be written to {1}" -f $process.Id, $report)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $nextProgress = 300
    while (-not $process.WaitForExit(1000)) {
        if ($watch.Elapsed.TotalSeconds -ge $nextProgress) {
            Write-Output ("Measurement elapsed {0} seconds" -f ([int]$watch.Elapsed.TotalSeconds))
            $nextProgress += 300
        }
        if ($watch.Elapsed.TotalSeconds -gt $Seconds + 60) {
            throw "Measurement exceeded its duration. Check PID $($process.Id)."
        }
    }
    if ($process.ExitCode -ne 0) { throw "SSD Guardian exited with code $($process.ExitCode)." }
} finally {
    $process.Dispose()
}

$result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
if (-not $result.Elevated -or -not ($result.Statuses | Where-Object { $_.Status.State -eq 'Running' })) {
    throw "ETW monitoring never reached Running. See $report."
}

[pscustomobject]@{
    Report                   = $report
    Seconds                  = $result.WallSeconds
    CpuPercentOfSystem        = $result.CpuPercentOfSystem
    WindowsPeakWorkingSetBytes = $result.WindowsPeakWorkingSetBytes
    SampledPeakWorkingSetBytes = $result.PeakWorkingSetBytes
    ProfileWriteOperations    = $result.ProfileWriteOperations
    ProfileWrittenBytes       = $result.ProfileWrittenBytes
    EtwEventsLost             = $result.Snapshot.LostEvents
    FinalMonitorState         = $result.Statuses[-1].Status.State
}
