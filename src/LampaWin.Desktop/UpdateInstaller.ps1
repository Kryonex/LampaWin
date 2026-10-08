param(
    [int]$ProcessIdToWait,
    [string]$InstallerPath,
    [string]$InstallPath,
    [string]$ExpectedHash,
    [string]$ExpectedVersion,
    [string]$ExpectedPublisher = '',
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
$updateRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'LampaWin\Updates'))
$installFull = [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')
$installerFull = [IO.Path]::GetFullPath($InstallerPath)
$runRoot = [IO.Path]::GetDirectoryName($installerFull)
$appPath = Join-Path $installFull 'LampaWin.exe'
$backup = Join-Path $runRoot 'rollback'
$resultPath = Join-Path $updateRoot 'result.json'
$state = 'validation'
$changed = $false
$backedUp = $false
function Write-Result([string]$status, [string]$code) {
    $payload = @{ status = $status; stage = $state; code = $code; version = $ExpectedVersion; run = [IO.Path]::GetFileName($runRoot); utc = [DateTime]::UtcNow.ToString('o') }
    $tempResult = $resultPath + '.tmp'
    [IO.File]::WriteAllText($tempResult, ($payload | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tempResult -Destination $resultPath -Force
}
function Assert-Paths {
    $registered = (Get-ItemProperty -LiteralPath 'HKCU:\Software\LampaWin' -Name InstallPath -ErrorAction SilentlyContinue).InstallPath
    if (!$registered) {
        $legacy = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{C7A5C33F-3531-44A0-AB47-622913A015D9}_is1' -ErrorAction Stop
        $registered = if ($legacy.InstallLocation) { $legacy.InstallLocation } else { $legacy.'Inno Setup: App Path' }
    }
    if (![String]::Equals([IO.Path]::GetFullPath($registered).TrimEnd('\'), $installFull, [StringComparison]::OrdinalIgnoreCase)) { throw 'Install registration mismatch' }
    if (!$runRoot.StartsWith($updateRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetDirectoryName($runRoot) -ne $updateRoot -or [IO.Path]::GetFileName($runRoot) -notmatch '^run-[a-f0-9]{32}$') { throw 'Invalid update workspace' }
    if ($installFull.StartsWith($updateRoot, [StringComparison]::OrdinalIgnoreCase) -or $updateRoot.StartsWith($installFull + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Overlapping paths' }
    foreach ($target in @($installFull, $runRoot, $updateRoot)) {
        $current = Get-Item -LiteralPath $target -Force
        while ($null -ne $current) {
            if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point not permitted' }
            $current = $current.Parent
        }
    }
    if (Get-ChildItem -LiteralPath $installFull -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw 'Install contains a reparse point' }
    if ((Get-Item -LiteralPath $installerFull).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Installer is a reparse point' }
    if ((Get-FileHash -LiteralPath $installerFull -Algorithm SHA256).Hash -ne $ExpectedHash) { throw 'Installer checksum mismatch' }
    if ($ExpectedPublisher) {
        Add-Type -AssemblyName System.Security
        $checksums = [IO.File]::ReadAllBytes((Join-Path $runRoot 'SHA256SUMS.txt'))
        $content = [Security.Cryptography.Pkcs.ContentInfo]::new($checksums)
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new($content, $true)
        $cms.Decode([IO.File]::ReadAllBytes((Join-Path $runRoot 'SHA256SUMS.txt.p7s')))
        $cms.CheckSignature($false)
        if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Subject -ne $ExpectedPublisher) { throw 'Manifest publisher mismatch' }
        if ([Text.Encoding]::UTF8.GetString($checksums) -notmatch ('(?m)^' + [regex]::Escape($ExpectedHash) + '\s+\*?' + [regex]::Escape([IO.Path]::GetFileName($installerFull)) + '\s*$')) { throw 'Signed manifest checksum mismatch' }
        $signature = Get-AuthenticodeSignature -LiteralPath $installerFull
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -ne $ExpectedPublisher) { throw 'Installer publisher mismatch' }
    }
    $installBytes = (Get-ChildItem -LiteralPath $installFull -File -Recurse -Force | Measure-Object -Property Length -Sum).Sum
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($runRoot))
    $installDrive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($installFull))
    $required = [long]$installBytes + (Get-Item -LiteralPath $installerFull).Length * 3 + 104857600
    if ($drive.AvailableFreeSpace -lt $required -or $installDrive.AvailableFreeSpace -lt (Get-Item -LiteralPath $installerFull).Length * 3 + 104857600) { throw 'Insufficient free space for update and rollback' }
    if ([version]$ExpectedVersion -le [version](Get-Item -LiteralPath $appPath).VersionInfo.FileVersion) { throw 'Update is not newer than installed version' }
}
try {
    Assert-Paths
    if ($ValidateOnly) { exit 0 }
    $state = 'waiting-for-app'
    Wait-Process -Id $ProcessIdToWait -Timeout 60 -ErrorAction SilentlyContinue
    if (Get-Process -Id $ProcessIdToWait -ErrorAction SilentlyContinue) { throw 'Application did not exit' }
    # Recheck the installer immediately before execution.
    Assert-Paths
    $state = 'backup'
    New-Item -ItemType Directory -Path $backup | Out-Null
    Get-ChildItem -LiteralPath $installFull -Force | Copy-Item -Destination $backup -Recurse -Force
    $backedUp = $true
    $state = 'install'
    Write-Result 'pending' 'install-started'
    $changed = $true
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/CLOSEAPPLICATIONS', ('/DIR="' + $installFull + '"'))
    $setup = Start-Process -FilePath $installerFull -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (!$setup.WaitForExit(600000)) {
        Stop-Process -Id $setup.Id -Force -ErrorAction SilentlyContinue
        $setup.WaitForExit()
        throw 'Installer timeout'
    }
    if ($setup.ExitCode -ne 0) { throw ('Installer exit ' + $setup.ExitCode) }
    $state = 'verify-version'
    if ([version](Get-Item -LiteralPath $appPath).VersionInfo.FileVersion -ne [version]$ExpectedVersion) { throw 'Installed version mismatch' }
    if ($ExpectedPublisher) {
        $signature = Get-AuthenticodeSignature -LiteralPath $appPath
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -ne $ExpectedPublisher) { throw 'Installed publisher mismatch' }
    }
    Write-Result 'installed' 'awaiting-application-start'
    # Kept until the new application reports successful initialization.
    Start-Process -FilePath $appPath -ArgumentList @('--update-result', $resultPath)
} catch {
    if ($ValidateOnly) { exit 2 }
    $errorCode = if ($changed) { 'installation-failed' } else { 'preparation-failed' }
    if ($changed -and $backedUp) {
        try {
            $state = 'rollback'
            # Validate the final absolute targets before any recursive removal.
            if ([IO.Path]::GetFullPath($backup) -ne (Join-Path $runRoot 'rollback') -or [IO.Path]::GetFullPath($installFull) -ne [IO.Path]::GetFullPath($InstallPath).TrimEnd('\')) { throw 'Rollback path mismatch' }
            foreach ($target in @($installFull, $backup)) {
                if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Rollback reparse point' }
                if (Get-ChildItem -LiteralPath $target -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | Select-Object -First 1) { throw 'Rollback tree contains a reparse point' }
            }
            Get-ChildItem -LiteralPath $installFull -Force | Remove-Item -Recurse -Force
            Get-ChildItem -LiteralPath $backup -Force | Copy-Item -Destination $installFull -Recurse -Force
            $errorCode = 'restored-previous-version'
        } catch { $errorCode = 'rollback-failed'; }
    }
    try { Write-Result 'failed' $errorCode } catch { }
    if (Test-Path -LiteralPath $appPath) { Start-Process -FilePath $appPath }
    exit 1
}
