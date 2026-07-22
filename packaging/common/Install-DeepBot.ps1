param([string]$GameDirectory)
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($GameDirectory)) {
    $candidates = @(
        'D:\steam\steamapps\common\Among Us',
        (Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Among Us')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'Among Us.exe')) }
    $GameDirectory = $candidates | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($GameDirectory)) {
    $GameDirectory = Read-Host 'Enter the full game directory containing Among Us.exe'
}
$GameDirectory = [IO.Path]::GetFullPath($GameDirectory.Trim('"'))
if (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'Among Us.exe'))) {
    throw "Among Us.exe was not found in: $GameDirectory"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'BepInEx'))) {
    throw 'BepInEx 6 IL2CPP was not found. Install it and launch the game once first.'
}

$pluginSource = Join-Path $PSScriptRoot 'BepInEx\plugins\AmongUsDeepSeekBots.dll'
$configSource = Join-Path $PSScriptRoot 'BepInEx\config\local.amongus.deepseekbots.cfg'
$pluginTarget = Join-Path $GameDirectory 'BepInEx\plugins\AmongUsDeepSeekBots.dll'
$configTarget = Join-Path $GameDirectory 'BepInEx\config\local.amongus.deepseekbots.cfg'
New-Item -ItemType Directory -Force -Path (Split-Path $pluginTarget), (Split-Path $configTarget) | Out-Null
if (Test-Path -LiteralPath $pluginTarget) {
    Copy-Item -LiteralPath $pluginTarget -Destination "$pluginTarget.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
}
Copy-Item -LiteralPath $pluginSource -Destination $pluginTarget -Force
# The TOR strict-rules package carries a clearly marked GPLv3 modified
# TheOtherRoles.dll. Standalone packages do not contain this optional file.
$torSource = Join-Path $PSScriptRoot 'BepInEx\plugins\TheOtherRoles.dll'
$torTarget = Join-Path $GameDirectory 'BepInEx\plugins\TheOtherRoles.dll'
if (Test-Path -LiteralPath $torSource) {
    if (Test-Path -LiteralPath $torTarget) {
        Copy-Item -LiteralPath $torTarget -Destination "$torTarget.bak-$(Get-Date -Format yyyyMMdd-HHmmss)"
    }
    Copy-Item -LiteralPath $torSource -Destination $torTarget -Force
}
if (-not (Test-Path -LiteralPath $configTarget)) {
    Copy-Item -LiteralPath $configSource -Destination $configTarget
}
foreach ($name in 'Configure-DeepBot-Key.ps1','Configure-DeepBot-Key.cmd','Start-DeepBot.ps1','Start-DeepBot.cmd') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $GameDirectory $name) -Force
}
Write-Host "DeepBot installed to: $GameDirectory" -ForegroundColor Green
Write-Host 'Host: run Configure-DeepBot-Key.cmd once. TOR build: set AI Bot count (1-8) in the lobby options.' -ForegroundColor Cyan

