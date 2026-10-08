[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable, [string]$InstallRoot = '', [string]$ReportRoot = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$Executable = (Resolve-Path -LiteralPath $Executable).Path
if (!$InstallRoot) { $InstallRoot = Split-Path -Parent $Executable }
$InstallRoot = (Resolve-Path -LiteralPath $InstallRoot).Path
if (!$ReportRoot) { $ReportRoot = Join-Path $root ('artifacts/checks/lifecycle-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
New-Item -ItemType Directory -Path $ReportRoot -Force | Out-Null
$ReportRoot = (Resolve-Path -LiteralPath $ReportRoot).Path
if (Get-Process LampaWin -ErrorAction SilentlyContinue) { throw 'Close the running LampaWin before the isolated lifecycle checks.' }
$results = @()
function Start-Check([string]$name, [string[]]$extra) {
    $report = Join-Path $ReportRoot ($name + '.json')
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach($argument in @('--lifecycle-check','--install-root',$InstallRoot,'--data-root',(Join-Path $ReportRoot 'data'),'--report',$report) + $extra) { $info.ArgumentList.Add($argument) }
    $launchTimestamp = [Diagnostics.Stopwatch]::GetTimestamp()
    [pscustomobject]@{ Process = [Diagnostics.Process]::Start($info); Report = $report; Name = $name; LaunchTimestamp = $launchTimestamp }
}
function Complete-Check($check) {
    if (!$check.Process.WaitForExit(60000)) { $check.Process.Kill($true); throw "Lifecycle check timed out: $($check.Name)" }
    if ($check.Process.ExitCode -ne 0) { throw "Lifecycle check failed: $($check.Name), exit $($check.Process.ExitCode)" }
    $report = Get-Content -LiteralPath $check.Report -Raw | ConvertFrom-Json
    $exitMilliseconds = ([Diagnostics.Stopwatch]::GetTimestamp() - $report.closeRequestedTimestamp) * 1000 / [Diagnostics.Stopwatch]::Frequency
    $startupMilliseconds = if ($report.catalogReadyTimestamp) { [Math]::Round(($report.catalogReadyTimestamp - $check.LaunchTimestamp) * 1000 / [Diagnostics.Stopwatch]::Frequency, 1) } else { $null }
    $result = [pscustomobject]@{ name = $check.Name; startupMilliseconds = $startupMilliseconds; catalogMilliseconds = $report.catalogMilliseconds; servicesReadyAtCatalog = $report.servicesReadyAtCatalog; nativePlayerLoadedAtCatalog = $report.nativePlayerLoadedAtCatalog; playing = $report.playing; exitMilliseconds = [Math]::Round($exitMilliseconds, 1) }
    Write-Host ($result | ConvertTo-Json -Compress)
    if ($report.errors.Count -ne 0 -or $exitMilliseconds -gt 6500) { throw "Lifecycle errors or slow exit: $($check.Name)" }
    if ($report.nativePlayerLoadedAtCatalog -eq $true) { throw 'The catalog loaded native VLC before playback.' }
    $check.Process.Dispose()
    $result
}
$results += Complete-Check (Start-Check 'close-during-startup' @('--close-after-ms','200'))
$first = Start-Check 'catalog-ready' @()
while (!(Test-Path -LiteralPath $first.Report)) {
    if ($first.Process.HasExited) { throw 'Catalog check exited before reporting.' }
    Start-Sleep -Milliseconds 50
}
# The next process starts as the first is requesting close and uses the real instance mutex.
$next = Start-Check 'rapid-relaunch-playing' @('--fixture',(Join-Path $root 'tests/assets/player-fixture.mp4'))
$results += Complete-Check $first
$results += Complete-Check $next
if (!$results[-1].playing) { throw 'Native playback was not reached.' }
$leftovers = Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('JackettConsole.exe','TorrServer.exe','msedgewebview2.exe') -and $_.CommandLine -and $_.CommandLine.Contains($ReportRoot) }
if ($leftovers) { throw 'A component belonging to the lifecycle check is still running.' }
[IO.File]::WriteAllText((Join-Path $ReportRoot 'result.json'), (@{ success = $true; childrenExited = $true; results = $results } | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
