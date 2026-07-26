param(
    [Parameter(Mandatory = $true)]
    [string]$GameDirectory,

    [Parameter(Mandatory = $true)]
    [string]$DeepBotDll,

    [Parameter(Mandatory = $true)]
    [string]$TheOtherRolesDll,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\release-assets')
)

$ErrorActionPreference = 'Stop'
$GameDirectory = [IO.Path]::GetFullPath($GameDirectory)
$DeepBotDll = [IO.Path]::GetFullPath($DeepBotDll)
$TheOtherRolesDll = [IO.Path]::GetFullPath($TheOtherRolesDll)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$installerProject = Join-Path $PSScriptRoot 'installer\DeepBotInstaller.csproj'
$generatedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'installer\generated'))

foreach ($required in @(
    (Join-Path $GameDirectory 'Among Us.exe'),
    (Join-Path $GameDirectory 'winhttp.dll'),
    (Join-Path $GameDirectory 'doorstop_config.ini'),
    $DeepBotDll,
    $TheOtherRolesDll,
    $installerProject
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required installer input is missing: $required"
    }
}

New-Item -ItemType Directory -Force -Path $generatedRoot, $OutputDirectory | Out-Null

function Reset-GeneratedDirectory([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $prefix = $generatedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a path outside installer/generated: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $resolved | Out-Null
}

function Copy-Runtime([string]$Stage) {
    foreach ($rootFile in @('.doorstop_version', 'doorstop_config.ini', 'winhttp.dll')) {
        $source = Join-Path $GameDirectory $rootFile
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $Stage $rootFile) -Force
        }
    }

    foreach ($directory in @('core', 'interop', 'patchers', 'unity-libs')) {
        $source = Join-Path $GameDirectory "BepInEx\$directory"
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $Stage "BepInEx\$directory") -Recurse -Force
        }
    }

    $plugins = Join-Path $Stage 'BepInEx\plugins'
    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item -LiteralPath $DeepBotDll -Destination (Join-Path $plugins 'AmongUsDeepSeekBots.dll') -Force
    Copy-Item -LiteralPath $TheOtherRolesDll -Destination (Join-Path $plugins 'TheOtherRoles.dll') -Force
    $reactor = Join-Path $GameDirectory 'BepInEx\plugins\Reactor.dll'
    if (Test-Path -LiteralPath $reactor) {
        Copy-Item -LiteralPath $reactor -Destination (Join-Path $plugins 'Reactor.dll') -Force
    }

    foreach ($name in @('Start-DeepBot.ps1', 'Start-DeepBot.cmd')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "common\$name") -Destination (Join-Path $Stage $name) -Force
    }
}

$hostStage = Join-Path $generatedRoot 'host-payload'
$clientStage = Join-Path $generatedRoot 'client-payload'
Reset-GeneratedDirectory $hostStage
Reset-GeneratedDirectory $clientStage
Copy-Runtime $hostStage
Copy-Runtime $clientStage

$hostConfig = @'
## Among Us DeepBot host configuration - no API key is included.
[General]
Enabled = true
[Local]
BotCount = 5
[AI]
Model = agnes-2.0-flash
ApiBaseUrl = https://apihub.agnes-ai.com/v1
MeetingUseDeepSeek = true
[Movement]
SpeedMultiplier = 0.82
[Runtime]
TickIntervalSeconds = 1
DryRun = false
[Social]
Enabled = true
AutoReportBodies = true
MeetingChat = true
MeetingVote = true
[Roles]
BotUseRoleAbilities = true
[Memory]
MaxEventsPerBot = 96
MeetingPromptEvents = 56
PostMatchReflection = true
[Diagnostics]
Verbose = true
'@

$clientConfig = @'
## Among Us DeepBot passive LAN client configuration - no API key is used.
[General]
Enabled = true
[Local]
BotCount = 0
[AI]
Model = agnes-2.0-flash
ApiBaseUrl = https://apihub.agnes-ai.com/v1
MeetingUseDeepSeek = false
[Movement]
SpeedMultiplier = 0.82
[Runtime]
TickIntervalSeconds = 1
DryRun = false
[Social]
Enabled = false
AutoReportBodies = false
MeetingChat = false
MeetingVote = false
[Roles]
BotUseRoleAbilities = false
[Memory]
MaxEventsPerBot = 96
MeetingPromptEvents = 56
PostMatchReflection = false
[Diagnostics]
Verbose = false
'@

foreach ($item in @(
    @{ Stage = $hostStage; Config = $hostConfig; Readme = 'This is the DeepBot host payload. Configure the API key locally after installation.' },
    @{ Stage = $clientStage; Config = $clientConfig; Readme = 'This is the passive DeepBot LAN client payload. The host owns every bot decision.' }
)) {
    $configDirectory = Join-Path $item.Stage 'BepInEx\config'
    New-Item -ItemType Directory -Force -Path $configDirectory | Out-Null
    Set-Content -LiteralPath (Join-Path $configDirectory 'local.amongus.deepseekbots.cfg') -Value $item.Config -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $item.Stage 'README-DeepBot.txt') -Value $item.Readme -Encoding UTF8
}

foreach ($name in @('Configure-DeepBot-Key.ps1', 'Configure-DeepBot-Key.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "common\$name") -Destination (Join-Path $hostStage $name) -Force
}

$hostZip = Join-Path $generatedRoot 'host-payload.zip'
$clientZip = Join-Path $generatedRoot 'client-payload.zip'
Compress-Archive -Path (Join-Path $hostStage '*') -DestinationPath $hostZip -CompressionLevel Optimal -Force
Compress-Archive -Path (Join-Path $clientStage '*') -DestinationPath $clientZip -CompressionLevel Optimal -Force

foreach ($build in @(
    @{ Mode = 'Host'; Zip = $hostZip; Name = 'AmongUs-DeepBot-Host-Installer.exe' },
    @{ Mode = 'Client'; Zip = $clientZip; Name = 'AmongUs-DeepBot-Client-Installer.exe' }
)) {
    dotnet publish $installerProject -c Release -r win-x64 --self-contained true `
        /p:InstallerMode=$($build.Mode) `
        /p:PayloadZip=$($build.Zip) `
        /p:PublishSingleFile=true `
        /p:EnableCompressionInSingleFile=true
    if ($LASTEXITCODE -ne 0) {
        throw "Installer publish failed for $($build.Mode)."
    }
    $published = Join-Path $PSScriptRoot "installer\bin\Release\net8.0-windows\win-x64\publish\$($build.Name)"
    Copy-Item -LiteralPath $published -Destination (Join-Path $OutputDirectory $build.Name) -Force
}

$assets = Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Name -like 'AmongUs-DeepBot-*-Installer.exe'
$checksums = foreach ($asset in $assets) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asset.FullName).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
}
Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS-installers.txt') -Value $checksums -Encoding ASCII
$assets | Select-Object Name, Length, LastWriteTime
