[CmdletBinding()]
param(
    [switch]$Verify,
    [string]$VideoPath
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$fixtureScript = Join-Path $workspaceRoot 'tests\torrent_fixture.py'
if (-not $VideoPath) { $VideoPath = Join-Path $workspaceRoot '.cache\fixture.mp4' }
$VideoPath = (Resolve-Path -LiteralPath $VideoPath).Path

$pythonCommand = Get-Command 'python.exe' -ErrorAction SilentlyContinue
if (-not $pythonCommand) { $pythonCommand = Get-Command 'python' -ErrorAction SilentlyContinue }
if (-not $pythonCommand) { throw 'Python 3 is required to run the local torrent fixture.' }

if ($Verify) {
    & $pythonCommand.Source $fixtureScript --video $VideoPath --self-test
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    exit 0
}

# Prints one JSON manifest line, then serves the localhost tracker and seed until Ctrl+C.
& $pythonCommand.Source $fixtureScript --video $VideoPath
exit $LASTEXITCODE
