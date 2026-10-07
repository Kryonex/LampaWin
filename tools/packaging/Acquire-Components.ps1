[CmdletBinding()]
param([switch]$SkipLampaBuild)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$manifestPath = Join-Path $root 'config/components.json'
$manifest = Get-Content -Raw $manifestPath | ConvertFrom-Json
$downloads = Join-Path $root '.cache/downloads'
New-Item -ItemType Directory -Force -Path $downloads,(Join-Path $root 'components/torrserver'),(Join-Path $root 'components/jackett'),(Join-Path $root 'components/lampa'),(Join-Path $root 'components/webview2'),(Join-Path $root 'artifacts/source') | Out-Null

function Get-VerifiedFile([string]$Url,[string]$Path,[string]$Sha256) {
    if (Test-Path -LiteralPath $Path) {
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.Equals($Sha256,[StringComparison]::OrdinalIgnoreCase)) { return }
        Remove-Item -LiteralPath $Path -Force
    }
    $partial = "$Path.part"
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    try {
        Invoke-WebRequest -Uri $Url -OutFile $partial -Headers @{ 'User-Agent'='LampaWin-component-builder' }
        $actual = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash
        if (!$actual.Equals($Sha256,[StringComparison]::OrdinalIgnoreCase)) { throw "SHA-256 mismatch for $Url (got $actual)" }
        Move-Item -LiteralPath $partial -Destination $Path
    } catch { Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue; throw }
}

$t = $manifest.components.torrserver
$torrCache = Join-Path $downloads $t.asset
Get-VerifiedFile $t.url $torrCache $t.sha256
$torrDestination = Join-Path $root 'components/torrserver/TorrServer.exe'
if (!(Test-Path $torrDestination) -or !(Get-FileHash $torrDestination -Algorithm SHA256).Hash.Equals($t.sha256,[StringComparison]::OrdinalIgnoreCase)) { Copy-Item -LiteralPath $torrCache -Destination $torrDestination -Force }
$torrSource = Join-Path $root '.cache/torrserver-source'
if (!(Test-Path (Join-Path $torrSource '.git'))) { git clone --filter=blob:none --no-checkout $t.repository $torrSource; if ($LASTEXITCODE) { throw 'TorrServer source clone failed' } }
git -C $torrSource fetch --depth 1 origin $t.commit
if ($LASTEXITCODE) { throw 'TorrServer source fetch failed' }
git -C $torrSource checkout --force $t.commit
if ($LASTEXITCODE) { throw 'TorrServer source checkout failed' }
Copy-Item -LiteralPath (Join-Path $torrSource 'LICENSE') -Destination (Join-Path $root 'artifacts/source/TORRSERVER-LICENSE.txt') -Force

$j = $manifest.components.jackett
$jackettZip = Join-Path $downloads $j.releaseAsset
Get-VerifiedFile $j.releaseUrl $jackettZip $j.releaseSha256
$jackettSource = Join-Path $root '.cache/jackett-source'
if (!(Test-Path (Join-Path $jackettSource '.git'))) { git clone --filter=blob:none --no-checkout $j.repository $jackettSource; if ($LASTEXITCODE) { throw 'Jackett source clone failed' } }
git -C $jackettSource fetch --depth 1 origin $j.commit
if ($LASTEXITCODE) { throw 'Jackett source fetch failed' }
git -C $jackettSource checkout --force $j.commit
if ($LASTEXITCODE) { throw 'Jackett source checkout failed' }
$configService = Join-Path $jackettSource 'src/Jackett.Common/Services/ConfigurationService.cs'
$sourceText = Get-Content -Raw $configService
if ($sourceText -notmatch 'WellKnownSidType.WorldSid') { throw 'Expected upstream ACL sites not found; review patch before building' }
$sourceText = [regex]::Replace($sourceText, '(?ms)\s*if \(RuntimeInformation\.IsOSPlatform\(OSPlatform\.Windows\)\)\s*\{\s*var access = dir\.GetAccessControl\(\);\s*var directorySecurity = new DirectorySecurity\(GetAppDataFolder\(\), AccessControlSections\.All\);\s*directorySecurity\.AddAccessRule\(new FileSystemAccessRule\(new SecurityIdentifier\(WellKnownSidType\.WorldSid, null\), FileSystemRights\.FullControl, InheritanceFlags\.ObjectInherit \| InheritanceFlags\.ContainerInherit, PropagationFlags\.None, AccessControlType\.Allow\)\);\s*dir\.SetAccessControl\(directorySecurity\);\s*\}', '')
$sourceText = [regex]::Replace($sourceText, '(?ms)\s*if \(RuntimeInformation\.IsOSPlatform\(OSPlatform\.Windows\)\)\s*\{\s*var directorySecurity = new DirectorySecurity\(destFolder, AccessControlSections\.All\);\s*directorySecurity\.AddAccessRule\(new FileSystemAccessRule\(new SecurityIdentifier\(WellKnownSidType\.WorldSid, null\), FileSystemRights\.FullControl, InheritanceFlags\.ObjectInherit \| InheritanceFlags\.ContainerInherit, PropagationFlags\.None, AccessControlType\.Allow\)\);\s*dir\.SetAccessControl\(directorySecurity\);\s*\}', '')
$sourceText = [regex]::Replace($sourceText, '(?ms)\s*// The old files were created when running as admin so make sure they are editable by normal users / services\.\s*if \(RuntimeInformation\.IsOSPlatform\(OSPlatform\.Windows\)\)\s*\{\s*var fileInfo = new FileInfo\(destFolder\);\s*var fileSecurity = new FileSecurity\(destPath, AccessControlSections\.All\);\s*fileSecurity\.AddAccessRule\(new FileSystemAccessRule\(new SecurityIdentifier\(WellKnownSidType\.WorldSid, null\), FileSystemRights\.FullControl, InheritanceFlags\.None, PropagationFlags\.None, AccessControlType\.Allow\)\);\s*fileInfo\.SetAccessControl\(fileSecurity\);\s*\}', '')
if ($sourceText -match 'WellKnownSidType\.WorldSid') { throw 'Jackett WorldSid ACL patch incomplete' }
$sourceText = $sourceText.TrimEnd("`r","`n")
$serverConfiguration = Join-Path $jackettSource 'src/Jackett.Server/Controllers/ServerConfigurationController.cs'
$serverText = Get-Content -Raw $serverConfiguration
$adminReservation = 'if \(!ServerUtil\.IsUserAdministrator\(\)\)\s*\{\s*try\s*\{\s*var consoleExePath = EnvironmentUtil\.JackettExecutablePath\(\)\.Replace\("\.dll", "\.exe"\);\s*processService\.StartProcessAndLog\(consoleExePath, "--ReserveUrls", true\);\s*\}\s*catch\s*\{\s*serverConfig\.LocalBindAddress = originalLocalBindAddress;\s*serverConfig\.Port = originalPort;\s*serverConfig\.AllowExternal = originalAllowExternal;\s*configService\.SaveConfig\(serverConfig\);\s*throw new Exception\("Failed to acquire admin permissions to reserve the new local_bind_address/port\."\);\s*\}\s*\}\s*else\s*\{\s*serverService\.ReserveUrls\(\);\s*\}'
if ($serverText -notmatch $adminReservation) { throw 'Jackett URL reservation branch not found; review before patching' }
$replacement = @'
if (!ServerUtil.IsUserAdministrator())
                    {
                        if (!external && IPAddress.IsLoopback(IPAddress.Parse(local_bind_address)))
                        {
                            // Private loopback listeners do not need a machine-wide HTTP.sys URL ACL.
                            // Keep portable mode non-elevated without changing public-listener behavior.
                            logger.Info("Skipping HTTP URL reservation for non-elevated private loopback listener.");
                        }
                        else
                        {
                            try
                            {
                                var consoleExePath = EnvironmentUtil.JackettExecutablePath().Replace(".dll", ".exe");
                                processService.StartProcessAndLog(consoleExePath, "--ReserveUrls", true);
                            }
                            catch
                            {
                                serverConfig.LocalBindAddress = originalLocalBindAddress;
                                serverConfig.Port = originalPort;
                                serverConfig.AllowExternal = originalAllowExternal;
                                configService.SaveConfig(serverConfig);
                                throw new Exception("Failed to acquire admin permissions to reserve the new local_bind_address/port.");
                            }
                        }
                    }
                    else
                    {
                        serverService.ReserveUrls();
                    }
'@
$serverText = [regex]::Replace($serverText, $adminReservation, [System.Text.RegularExpressions.MatchEvaluator]{ param($m) $replacement }, 1)
$initialisation = Join-Path $jackettSource 'src/Jackett.Server/Initialisation.cs'
$initialisationText = Get-Content -Raw $initialisation
$settingsService = 'IServerService serverService = new ServerService\(null, processService, null, null, logger, null, null, null, serverConfig\);\s*\r?\n'
$methodStart = $initialisationText.IndexOf('public static void ProcessConsoleOverrides(',[StringComparison]::Ordinal)
if ($methodStart -lt 0) { throw 'Jackett startup override entry point not found' }
$methodPrefix = $initialisationText.Substring(0,$methodStart)
$methodBody = $initialisationText.Substring($methodStart)
if ($methodBody -notmatch $settingsService) { throw 'Jackett startup override server setup not found' }
$methodBody = [regex]::Replace($methodBody, $settingsService, '$0            var privateLoopbackListener = consoleOptions.ListenPrivate && !consoleOptions.ListenPublic && System.Net.IPAddress.TryParse(serverConfig.LocalBindAddress, out var bindAddress) && System.Net.IPAddress.IsLoopback(bindAddress);' + "`r`n", 1)
$initialisationText = $methodPrefix + $methodBody
$windowsCondition = 'if \(EnvironmentUtil\.IsWindows\)'
if ([regex]::Matches($initialisationText, $windowsCondition).Count -ne 2) { throw 'Unexpected Windows reservation branches in Jackett startup' }
$initialisationText = [regex]::Replace($initialisationText, $windowsCondition, 'if (EnvironmentUtil.IsWindows && !privateLoopbackListener)')
$serverText = $serverText.TrimEnd("`r","`n")
$initialisationText = $initialisationText.TrimEnd("`r","`n")
if ($serverText -notmatch 'if \(!external && IPAddress\.IsLoopback\(IPAddress\.Parse\(local_bind_address\)\)\)' -or $initialisationText -notmatch 'privateLoopbackListener = consoleOptions\.ListenPrivate' -or $initialisationText -match 'if \(EnvironmentUtil\.IsWindows\)') { throw 'Jackett private-listener URL reservation patch incomplete' }
Set-Content -LiteralPath $configService -Value $sourceText -Encoding utf8NoBOM
Set-Content -LiteralPath $serverConfiguration -Value $serverText -Encoding utf8NoBOM
Set-Content -LiteralPath $initialisation -Value $initialisationText -Encoding utf8NoBOM
$patch = Join-Path $root 'artifacts/source/jackett-private-data-folder-acl.patch'
git -C $jackettSource diff -- src/Jackett.Common/Services/ConfigurationService.cs src/Jackett.Server/Controllers/ServerConfigurationController.cs src/Jackett.Server/Initialisation.cs | Set-Content -LiteralPath $patch -Encoding utf8
Copy-Item -LiteralPath (Join-Path $jackettSource 'LICENSE') -Destination (Join-Path $root 'artifacts/source/JACKETT-LICENSE.txt') -Force

$l = $manifest.components.lampa
if (!$SkipLampaBuild) {
    $lampaSource = Join-Path $root '.cache/lampa-source'
    if (!(Test-Path (Join-Path $lampaSource '.git'))) { git clone --filter=blob:none --no-checkout $l.repository $lampaSource; if ($LASTEXITCODE) { throw 'Lampa source clone failed' } }
    git -C $lampaSource fetch --depth 1 origin $l.commit
    if ($LASTEXITCODE) { throw 'Lampa source fetch failed' }
    git -C $lampaSource checkout --force $l.commit
    if ($LASTEXITCODE) { throw 'Lampa source checkout failed' }
    $lampaPatch = Join-Path $PSScriptRoot 'lampa-desktop.patch'
    git -C $lampaSource apply --ignore-whitespace --check $lampaPatch
    if ($LASTEXITCODE) { throw 'Lampa desktop hardening patch no longer applies cleanly to the pinned source' }
    git -C $lampaSource apply --ignore-whitespace $lampaPatch
    if ($LASTEXITCODE) { throw 'Lampa desktop hardening patch failed to apply' }
    $lockPath = Join-Path $root $l.packageLock
    if (!(Test-Path $lockPath) -or !(Get-FileHash $lockPath -Algorithm SHA256).Hash.Equals($l.packageLockSha256,[StringComparison]::OrdinalIgnoreCase)) { throw 'Pinned Lampa package-lock.json is missing or has a different digest' }
    Copy-Item -LiteralPath $lockPath -Destination (Join-Path $lampaSource 'package-lock.json') -Force
    Push-Location $lampaSource
    try {
        npm ci --no-audit --no-fund
        if ($LASTEXITCODE) { throw 'Lampa npm dependency install failed' }
        npx gulp pack_desktop
        if ($LASTEXITCODE) { throw 'Lampa production bundle failed' }
    } finally { Pop-Location }
    $built = Join-Path $lampaSource 'build/github/lampa'
    if (!(Test-Path (Join-Path $built 'index.html'))) { throw "Lampa build output has no index.html: $built" }
    if (!(Test-Path (Join-Path $built 'plugins/modification.js'))) { throw 'Lampa desktop bundle is missing its local no-op user modification hook' }
    $builtApp = Join-Path $built 'app.min.js'
    if (!(Test-Path -LiteralPath $builtApp)) { throw "Lampa build output has no app.min.js: $builtApp" }
    $builtAppText = Get-Content -Raw -LiteralPath $builtApp
    if ($builtAppText -notmatch 'window\.__lampawinOrigin' -or $builtAppText -notmatch "'/plugin/sport'" -or $builtAppText -notmatch "'/plugin/tsarea'" -or $builtAppText -notmatch "'/plugin/shots'" -or $builtAppText -notmatch 'if \(window\.__lampawinOrigin\)\s*\{\s*if \(!window\.lampa_settings\.disable_features\.install_proxy\) TMDBProxy\.init\(\);\s*return call && call\(\);') {
        throw 'Lampa desktop bundle does not contain the expected guarded remote plugins and fast TMDB proxy initialization'
    }
    Copy-Item -Path (Join-Path $built '*') -Destination (Join-Path $root 'components/lampa') -Recurse -Force
    Copy-Item (Join-Path $lampaSource 'LICENSE') (Join-Path $root 'artifacts/source/LAMPA-LICENSE.txt') -Force
}

$lampaSourceForArchive = Join-Path $root '.cache/lampa-source'
Copy-Item (Join-Path $root $l.packageLock) (Join-Path $root 'artifacts/source/LAMPA-package-lock.json') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'lampa-desktop.patch') -Destination (Join-Path $root 'artifacts/source/lampa-desktop.patch') -Force

$w = $manifest.components.webview2Bootstrapper
$webviewCache = Join-Path $downloads 'MicrosoftEdgeWebView2Setup.exe'
Get-VerifiedFile $w.url $webviewCache $w.sha256
$webviewSignature = Get-AuthenticodeSignature -FilePath $webviewCache
if ($webviewSignature.Status -ne 'Valid' -or $webviewSignature.SignerCertificate.Subject -notmatch [regex]::Escape($w.signer)) { throw 'WebView2 bootstrapper Authenticode signature is invalid or unexpected' }
Copy-Item -LiteralPath $webviewCache -Destination (Join-Path $root 'components/webview2/MicrosoftEdgeWebView2Setup.exe') -Force

foreach ($source in @(@{path=$jackettSource;archive='jackett-v0.24.2798-source.zip';commit=$j.commit},@{path=$torrSource;archive='torrserver-MatriX.145.2-source.zip';commit=$t.commit},@{path=(Join-Path $root '.cache/lampa-source');archive='lampa-source-b4a13b6-source.zip';commit=$l.commit})) {
    $archivePath = Join-Path $root ('artifacts/source/' + $source.archive)
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    git -C $source.path archive --format=zip --output=$archivePath $source.commit
    if ($LASTEXITCODE) { throw "Could not create source archive $($source.archive)" }
}

Write-Host "TorrServer $( $t.version ) ready"
Write-Host "Jackett source $( $j.commit ) patched; run Build-Release.ps1 to compile"
if (!$SkipLampaBuild) { Write-Host "Lampa $( $l.commit ) built into components/lampa" }

