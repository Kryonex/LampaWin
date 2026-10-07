[CmdletBinding()]
param([switch]$SkipInstaller,[switch]$SkipJackettPublish,[string]$CandidateName = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$localDotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$dotnetRoot = Split-Path -Parent $dotnet
$desktop = Join-Path $root 'src/LampaWin.Desktop/LampaWin.Desktop.csproj'
$jackettSource = Join-Path $root '.cache/jackett-source/src/Jackett.Server/Jackett.Server.csproj'
$artifactsBase = (Resolve-Path (Join-Path $root 'artifacts')).Path
if ($CandidateName -and $CandidateName -notmatch '^[a-zA-Z0-9][a-zA-Z0-9-]{0,63}$') { throw 'CandidateName must be a simple directory name.' }
$artifactsRoot = if ($CandidateName) { Join-Path $artifactsBase $CandidateName } else { $artifactsBase }
New-Item -ItemType Directory -Force $artifactsRoot | Out-Null
$publish = Join-Path $artifactsRoot 'publish'
if ($CandidateName) { Copy-Item -LiteralPath (Join-Path $artifactsBase 'source') -Destination $artifactsRoot -Recurse -Force }
$lgplLicense = Join-Path $root 'licenses/LGPL-2.1-or-later.txt'
if (!(Test-Path $dotnet)) { throw "Local .NET SDK missing: $dotnet" }
if (!(Test-Path $desktop)) { throw "Desktop project missing: $desktop" }
if (!(Test-Path $lgplLicense)) { throw "Required third-party license is missing: $lgplLicense" }
if (!(Test-Path (Join-Path $root 'components/lampa/index.html'))) { throw 'Run Acquire-Components.ps1 first (Lampa bundle missing)' }
if (!(Test-Path (Join-Path $root 'components/torrserver/TorrServer.exe'))) { throw 'Run Acquire-Components.ps1 first (TorrServer missing)' }

if (!$SkipJackettPublish) {
    & $dotnet publish $jackettSource -c Release -f net9.0 -r win-x64 --self-contained true -p:TargetFrameworks=net9.0 -p:Version=0.24.2798 -p:AssemblyVersion=0.24.2798 -p:FileVersion=0.24.2798 -p:InformationalVersion=0.24.2798 --source https://api.nuget.org/v3/index.json -o (Join-Path $root 'components/jackett')
    if ($LASTEXITCODE) { throw 'Patched Jackett source publish failed' }
}
if (!(Test-Path (Join-Path $root 'components/jackett/JackettConsole.exe'))) { throw 'JackettConsole.exe missing from publish output' }

$publishFull = [IO.Path]::GetFullPath($publish)
if (!$publishFull.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to remove publish output outside artifacts: $publishFull" }
if (Test-Path -LiteralPath $publishFull) { Remove-Item -LiteralPath $publishFull -Recurse -Force }
New-Item -ItemType Directory -Force $publish | Out-Null
& $dotnet publish $desktop -c Release -r win-x64 --self-contained true --source https://api.nuget.org/v3/index.json -o $publish
if ($LASTEXITCODE) { throw 'LampaWin publish failed' }

# The desktop publish is win-x64; discard only the unused native VLC RIDs
# copied by the NuGet package so an x64 installer cannot accidentally load them.
$libVlcRoot = Join-Path $publish 'libvlc'
if (Test-Path -LiteralPath $libVlcRoot) {
    $libVlcFull = (Resolve-Path -LiteralPath $libVlcRoot).Path
    foreach ($ridFolder in Get-ChildItem -LiteralPath $libVlcFull -Directory | Where-Object Name -ne 'win-x64') {
        $ridFull = (Resolve-Path -LiteralPath $ridFolder.FullName).Path
        if (!$ridFull.StartsWith($libVlcFull + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to prune native VLC payload outside libvlc: $ridFull" }
        Remove-Item -LiteralPath $ridFull -Recurse -Force
    }
}

# Component data is immutable at runtime. The application's writable state is per-user.
$publishComponents = Join-Path $publish 'components'
New-Item -ItemType Directory -Force $publishComponents | Out-Null
foreach ($name in @('torrserver','lampa','webview2')) { Copy-Item (Join-Path $root "components/$name") -Destination $publishComponents -Recurse -Force }
$jackettSourceDir = Join-Path $root 'components/jackett'
$jackettTargetDir = Join-Path $publishComponents 'jackett'
New-Item -ItemType Directory -Force $jackettTargetDir | Out-Null
Get-ChildItem -LiteralPath $jackettSourceDir | Where-Object Name -ne 'Jackett' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $jackettTargetDir -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $publish 'THIRD-PARTY-NOTICES.md') -Force
foreach ($rootDocument in @('README.md','LICENSE','VALIDATION.md')) {
    $documentPath = Join-Path $root $rootDocument
    if (!(Test-Path -LiteralPath $documentPath)) { throw "Required release document is missing: $rootDocument" }
    Copy-Item -LiteralPath $documentPath -Destination (Join-Path $publish $rootDocument) -Force
}
$licenses = Join-Path $publish 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$nuget = Join-Path $env:USERPROFILE '.nuget/packages'
$licenseCopies = @(
    @("$nuget/microsoft.web.webview2/1.0.4258.31/LICENSE.txt", 'Microsoft.Web.WebView2-LICENSE.txt'),
    @("$nuget/microsoft.web.webview2/1.0.4258.31/NOTICE.txt", 'Microsoft.Web.WebView2-NOTICE.txt'),
    @("$nuget/system.security.cryptography.protecteddata/10.0.0/THIRD-PARTY-NOTICES.TXT", 'System.Security.Cryptography.ProtectedData-THIRD-PARTY-NOTICES.txt'),
    @("$dotnetRoot/ThirdPartyNotices.txt", 'dotnet-ThirdPartyNotices.txt'),
    @("$dotnetRoot/LICENSE.txt", 'dotnet-LICENSE.txt')
)
foreach ($copy in $licenseCopies) {
    if (!(Test-Path -LiteralPath $copy[0])) { throw "Required third-party notice file is missing: $($copy[0])" }
    Copy-Item -LiteralPath $copy[0] -Destination (Join-Path $licenses $copy[1]) -Force
}
Copy-Item -LiteralPath $lgplLicense -Destination (Join-Path $licenses 'LGPL-2.1-or-later.txt') -Force
Copy-Item -LiteralPath $lgplLicense -Destination (Join-Path $artifactsRoot 'source/LGPL-2.1-or-later.txt') -Force
@'
MIT License

Copyright (c) .NET Foundation and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@ | Set-Content -LiteralPath (Join-Path $licenses 'MIT.txt') -Encoding utf8

# Create a first-party source archive without caches, binaries, package outputs, or user state.
$sourceStage = Join-Path $artifactsRoot 'source/LampaWin-source'
$sourceStageFull = [IO.Path]::GetFullPath($sourceStage)
if (!$sourceStageFull.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to create source staging outside artifacts: $sourceStageFull" }
if (Test-Path -LiteralPath $sourceStageFull) { Remove-Item -LiteralPath $sourceStageFull -Recurse -Force }
New-Item -ItemType Directory -Force $sourceStageFull | Out-Null
foreach ($relative in @('src','tests','tools/packaging','tools/runtime','tools/testing','installer','config','licenses','releases','THIRD-PARTY-NOTICES.md','README.md','VALIDATION.md','LICENSE','global.json','Directory.Build.props','NuGet.Config','LampaWin.sln')) {
    $from = Join-Path $root $relative
    if (!(Test-Path -LiteralPath $from)) { continue }
    if ((Get-Item -LiteralPath $from).PSIsContainer) {
        $targetRoot = Join-Path $sourceStageFull $relative
        New-Item -ItemType Directory -Force $targetRoot | Out-Null
        Get-ChildItem -LiteralPath $from -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.cache|\.tools|components|artifacts|TestResults)[\\/]' } | ForEach-Object {
            $relativeFile = $_.FullName.Substring($from.Length).TrimStart('\', '/')
            $target = Join-Path $targetRoot $relativeFile
            New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    } else { Copy-Item -LiteralPath $from -Destination $sourceStageFull -Force }
}
$firstPartySourceZip = Join-Path $artifactsRoot 'source/LampaWin-source.zip'
Remove-Item -LiteralPath $firstPartySourceZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $sourceStageFull '*') -DestinationPath $firstPartySourceZip -CompressionLevel Optimal
Remove-Item -LiteralPath $sourceStageFull -Recurse -Force
Copy-Item -LiteralPath (Join-Path $artifactsRoot 'source') -Destination (Join-Path $publish 'source') -Recurse -Force
$zip = Join-Path $artifactsRoot 'LampaWin-win-x64-portable.zip'
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -CompressionLevel Optimal
if (!$SkipInstaller) {
    $iscc = Join-Path $root '.tools/inno/ISCC.exe'
    if (!(Test-Path $iscc)) {
        $innoSetup = Join-Path $root '.cache/downloads/innosetup-7.1.0-x64.exe'
        $innoHash = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
        if (!(Test-Path $innoSetup)) {
            New-Item -ItemType Directory -Force (Split-Path $innoSetup) | Out-Null
            Invoke-WebRequest -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' -OutFile ($innoSetup + '.part') -Headers @{ 'User-Agent'='LampaWin-build' }
            $downloadHash = (Get-FileHash ($innoSetup + '.part') -Algorithm SHA256).Hash
            if (!$downloadHash.Equals($innoHash,[StringComparison]::OrdinalIgnoreCase)) { Remove-Item ($innoSetup + '.part') -Force; throw 'Inno Setup SHA-256 mismatch' }
            Move-Item ($innoSetup + '.part') $innoSetup
        }
        $actualHash = (Get-FileHash $innoSetup -Algorithm SHA256).Hash
        if (!$actualHash.Equals($innoHash,[StringComparison]::OrdinalIgnoreCase)) { throw 'Cached Inno Setup SHA-256 mismatch' }
        $signature = Get-AuthenticodeSignature -FilePath $innoSetup
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.|Jordan Russell|JRSoftware') { throw 'Inno Setup Authenticode signature is invalid or unexpected' }
        $innoDir = Join-Path $root '.tools/inno'
        New-Item -ItemType Directory -Force $innoDir | Out-Null
        $installer = Start-Process -FilePath $innoSetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CURRENTUSER',"/DIR=`"$innoDir`"") -WindowStyle Hidden -Wait -PassThru
        if ($installer.ExitCode -ne 0) { throw "Inno Setup local install failed with exit code $($installer.ExitCode)" }
    }
    if (!(Test-Path $iscc)) { throw "ISCC.exe not found under .tools/inno after setup: $iscc" }
    [xml]$project = Get-Content -Raw $desktop
    $appVersion = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (!$appVersion -or $appVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Invalid application version: $appVersion" }
    & $iscc (Join-Path $root 'installer/LampaWin.iss') "/DPublishDir=$publish" "/DOutputDir=$artifactsRoot" "/DAppVersion=$appVersion"
    if ($LASTEXITCODE) { throw 'Inno Setup compile failed' }
}
$checksumFiles = @('LampaWin-win-x64-portable.zip')
if (!$SkipInstaller) { $checksumFiles += 'LampaWin-Setup-win-x64.exe' }
$checksumLines = foreach ($name in $checksumFiles) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $artifactsRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash *$name"
}
Set-Content -LiteralPath (Join-Path $artifactsRoot 'SHA256SUMS.txt') -Value $checksumLines -Encoding ascii
Write-Host "Portable ZIP: $zip"
