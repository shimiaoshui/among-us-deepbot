param(
    [Parameter(Mandatory = $true)]
    [string]$GameDirectory,

    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\release-assets')
)

$ErrorActionPreference = 'Stop'
$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testBase = [IO.Path]::GetFullPath((Join-Path $repositoryRoot '.installer-tests'))
$testRoot = [IO.Path]::GetFullPath((Join-Path $testBase 'v0.10.4-client-visibility'))
$allowedPrefix = $testBase.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $testRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a test path outside $testBase"
}
if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

$hostDirectory = Join-Path $testRoot 'host\Among Us'
$clientDirectory = Join-Path $testRoot 'client\Among Us'
$multiSteamRoot = Join-Path $testRoot 'multi-install\steam'
$plainGameDirectory = Join-Path $multiSteamRoot 'steamapps\common\Among Us'
$activeTorDirectory = Join-Path $multiSteamRoot 'steamapps\common\TOR Active'
New-Item -ItemType Directory -Force -Path $hostDirectory, $clientDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $GameDirectory 'Among Us.exe') -Destination (Join-Path $hostDirectory 'Among Us.exe')
Copy-Item -LiteralPath (Join-Path $GameDirectory 'Among Us.exe') -Destination (Join-Path $clientDirectory 'Among Us.exe')

$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$keyPath = Join-Path $localAppData 'AmongUsDeepSeekBots\api-key.txt'
$keyBackup = Join-Path $env:TEMP ("deepbot-key-backup-" + [guid]::NewGuid().ToString('N'))
$hadKey = Test-Path -LiteralPath $keyPath
if ($hadKey) {
    Copy-Item -LiteralPath $keyPath -Destination $keyBackup -Force
}

$dummyKey = 'db-test-key-102-not-a-secret'
try {
    $env:DEEPBOT_INSTALL_API_BASE_URL = 'https://example.invalid/v1'
    $env:DEEPBOT_INSTALL_API_KEY = $dummyKey
    $hostInstallProcess = Start-Process -FilePath (Join-Path $AssetDirectory 'AmongUs-DeepBot-Host-Installer.exe') -ArgumentList @('--silent', '--path', ('"' + $hostDirectory + '"')) -Wait -PassThru
    $hostInstallExit = $hostInstallProcess.ExitCode
    if ($hostInstallExit -ne 0) { throw "Host install failed: $hostInstallExit" }

    Remove-Item Env:DEEPBOT_INSTALL_API_BASE_URL, Env:DEEPBOT_INSTALL_API_KEY -ErrorAction SilentlyContinue
    $clientInstallProcess = Start-Process -FilePath (Join-Path $AssetDirectory 'AmongUs-DeepBot-Client-Installer.exe') -ArgumentList @('--silent', '--path', ('"' + $clientDirectory + '"')) -Wait -PassThru
    $clientInstallExit = $clientInstallProcess.ExitCode
    if ($clientInstallExit -ne 0) { throw "Client install failed: $clientInstallExit" }

    if (-not (Test-Path -LiteralPath $keyPath)) { throw 'Host installer did not create the local key file.' }
    if ((Get-Content -LiteralPath $keyPath -Raw).Trim() -ne $dummyKey) { throw 'Host installer key write mismatch.' }
    $hostConfig = Get-Content -LiteralPath (Join-Path $hostDirectory 'BepInEx\config\local.amongus.deepseekbots.cfg') -Raw
    if ($hostConfig -notmatch '(?m)^ApiBaseUrl = https://example\.invalid/v1$') {
        throw 'Host API base URL was not written.'
    }

    $hostManifest = Get-Content -LiteralPath (Join-Path $hostDirectory 'DeepBot-Compatibility.json') -Raw | ConvertFrom-Json
    $clientManifest = Get-Content -LiteralPath (Join-Path $clientDirectory 'DeepBot-Compatibility.json') -Raw | ConvertFrom-Json
    if ($hostManifest.CompatibilityId -ne $clientManifest.CompatibilityId) {
        throw 'Host/client compatibility IDs differ.'
    }

    foreach ($relative in @(
        'BepInEx\plugins\TheOtherRoles.dll',
        'BepInEx\plugins\Reactor.dll',
        'BepInEx\plugins\AmongUsDeepSeekBots.dll'
    )) {
        $hostHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $hostDirectory $relative)).Hash
        $clientHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $clientDirectory $relative)).Hash
        if ($hostHash -ne $clientHash) { throw "Host/client hash mismatch: $relative" }
    }

    $hostValidation = & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $hostDirectory 'Start-DeepBot.ps1') -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw 'Host launcher validation failed.' }
    $clientValidation = & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $clientDirectory 'Start-DeepBot.ps1') -ValidateOnly
    if ($LASTEXITCODE -ne 0) { throw 'Client launcher validation failed.' }

    # A Steam library can contain a plain folder named "Among Us" alongside the
    # actively used TOR copy.  Installing from the Steam root must prefer the
    # TOR copy instead of silently modifying the unrelated vanilla folder.
    New-Item -ItemType Directory -Force -Path $plainGameDirectory, (Join-Path $activeTorDirectory 'BepInEx\plugins') | Out-Null
    Copy-Item -LiteralPath (Join-Path $GameDirectory 'Among Us.exe') -Destination (Join-Path $plainGameDirectory 'Among Us.exe')
    Copy-Item -LiteralPath (Join-Path $GameDirectory 'Among Us.exe') -Destination (Join-Path $activeTorDirectory 'Among Us.exe')
    Copy-Item -LiteralPath (Join-Path $GameDirectory 'BepInEx\plugins\TheOtherRoles.dll') -Destination (Join-Path $activeTorDirectory 'BepInEx\plugins\TheOtherRoles.dll')
    Copy-Item -LiteralPath (Join-Path $GameDirectory 'BepInEx\plugins\AmongUsDeepSeekBots.dll') -Destination (Join-Path $activeTorDirectory 'BepInEx\plugins\AmongUsDeepSeekBots.dll')
    New-Item -ItemType File -Force -Path (Join-Path $activeTorDirectory 'BepInEx\LogOutput.log') | Out-Null

    $multiInstallProcess = Start-Process -FilePath (Join-Path $AssetDirectory 'AmongUs-DeepBot-Host-Installer.exe') -ArgumentList @('--silent', '--path', ('"' + $multiSteamRoot + '"')) -Wait -PassThru
    if ($multiInstallProcess.ExitCode -ne 0) { throw "Multi-install host install failed: $($multiInstallProcess.ExitCode)" }
    if (-not (Test-Path -LiteralPath (Join-Path $activeTorDirectory 'DeepBot-Compatibility.json'))) {
        throw 'Steam-root install did not select the active TOR directory.'
    }
    if (Test-Path -LiteralPath (Join-Path $plainGameDirectory 'DeepBot-Compatibility.json')) {
        throw 'Steam-root install incorrectly selected the plain Among Us directory.'
    }

    $multiUninstallProcess = Start-Process -FilePath (Join-Path $AssetDirectory 'AmongUs-DeepBot-Host-Uninstaller.exe') -ArgumentList @('--silent', '--path', ('"' + $multiSteamRoot + '"')) -Wait -PassThru
    if ($multiUninstallProcess.ExitCode -ne 0) { throw "Multi-install host uninstall failed: $($multiUninstallProcess.ExitCode)" }
    if (Test-Path -LiteralPath (Join-Path $activeTorDirectory 'DeepBot-Compatibility.json')) {
        throw 'Steam-root uninstall left the TOR compatibility manifest behind.'
    }

    $hostUninstallProcess = Start-Process -FilePath (Join-Path $AssetDirectory 'AmongUs-DeepBot-Host-Uninstaller.exe') -ArgumentList @('--silent', '--path', ('"' + $hostDirectory + '"')) -Wait -PassThru
    $hostUninstallExit = $hostUninstallProcess.ExitCode
    $clientUninstallProcess = Start-Process -FilePath (Join-Path $AssetDirectory 'AmongUs-DeepBot-Client-Uninstaller.exe') -ArgumentList @('--silent', '--path', ('"' + $clientDirectory + '"')) -Wait -PassThru
    $clientUninstallExit = $clientUninstallProcess.ExitCode
    if ($hostUninstallExit -ne 0 -or $clientUninstallExit -ne 0) {
        throw "Uninstall failed: host=$hostUninstallExit client=$clientUninstallExit"
    }

    $managedNames = @(
        'TheOtherRoles.dll',
        'Reactor.dll',
        'AmongUsDeepSeekBots.dll',
        'DeepBot-Compatibility.json',
        'Start-DeepBot.ps1',
        'Start-DeepBot.cmd',
        'local.amongus.deepseekbots.cfg'
    )
    $leftovers = @($hostDirectory, $clientDirectory | ForEach-Object {
        Get-ChildItem -LiteralPath $_ -Depth 4 -File | Where-Object { $_.Name -in $managedNames }
    })
    if ($leftovers.Count -ne 0) { throw "Uninstall left $($leftovers.Count) managed files." }

    [pscustomobject]@{
        HostInstallExit = $hostInstallExit
        ClientInstallExit = $clientInstallExit
        CompatibilityId = $hostManifest.CompatibilityId
        ReleaseVersion = $hostManifest.ReleaseVersion
        HostLauncherValidation = $hostValidation -join ' '
        ClientLauncherValidation = $clientValidation -join ' '
        HostUninstallExit = $hostUninstallExit
        ClientUninstallExit = $clientUninstallExit
        ManagedLeftovers = $leftovers.Count
        ApiEndpointWritten = $true
        ApiKeyBoundaryTest = $true
        MultiInstallSelectionTest = $true
    }
}
finally {
    Remove-Item Env:DEEPBOT_INSTALL_API_BASE_URL, Env:DEEPBOT_INSTALL_API_KEY -ErrorAction SilentlyContinue
    if ($hadKey) {
        New-Item -ItemType Directory -Force -Path (Split-Path $keyPath) | Out-Null
        Copy-Item -LiteralPath $keyBackup -Destination $keyPath -Force
    }
    elseif (Test-Path -LiteralPath $keyPath) {
        Remove-Item -LiteralPath $keyPath -Force
    }
    if (Test-Path -LiteralPath $keyBackup) {
        Remove-Item -LiteralPath $keyBackup -Force
    }
}
