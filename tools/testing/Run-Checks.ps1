[CmdletBinding()]
param([switch]$SkipUi)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$localSdk = Join-Path $root '.tools/dotnet/dotnet.exe'
$sdk = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$reportRoot = Join-Path $root 'artifacts/checks'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
Push-Location $root
try {
    & $sdk restore LampaWin.sln --locked-mode
    if ($LASTEXITCODE) { throw 'Locked restore failed' }
    & $sdk build LampaWin.sln -c Release --no-restore
    if ($LASTEXITCODE) { throw 'Solution build failed' }
    & $sdk run --project tests/LampaWin.Tests -c Release --no-build --no-restore
    if ($LASTEXITCODE) { throw 'Core checks failed' }
    & node --test tests/bridge.test.cjs
    if ($LASTEXITCODE) { throw 'Bridge checks failed' }
    if (!$SkipUi) {
        & $sdk restore tools/testing/PlayerUiSmoke/PlayerUiSmoke.csproj --locked-mode
        if ($LASTEXITCODE) { throw 'UI harness restore failed' }
        & $sdk run --project tools/testing/PlayerUiSmoke -c Release --no-restore -- tests/assets/player-fixture.mp4 (Join-Path $reportRoot 'player-ui.json')
        if ($LASTEXITCODE) { throw 'Player UI checks failed' }
    }
    [IO.File]::WriteAllText((Join-Path $reportRoot 'result.json'), (@{ success = $true; utc = [DateTime]::UtcNow.ToString('o'); ui = !$SkipUi } | ConvertTo-Json))
} finally { Pop-Location }
