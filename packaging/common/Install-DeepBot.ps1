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
    $GameDirectory = Read-Host '请输入包含 Among Us.exe 的完整游戏目录'
}
$GameDirectory = [IO.Path]::GetFullPath($GameDirectory.Trim('"'))
if (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'Among Us.exe'))) {
    throw "目标目录中找不到 Among Us.exe：$GameDirectory"
}
if (-not (Test-Path -LiteralPath (Join-Path $GameDirectory 'BepInEx'))) {
    throw '目标目录尚未安装 BepInEx 6 IL2CPP。请先安装并启动一次游戏。'
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
if (-not (Test-Path -LiteralPath $configTarget)) {
    Copy-Item -LiteralPath $configSource -Destination $configTarget
}
foreach ($name in 'Configure-DeepBot-Key.ps1','Configure-DeepBot-Key.cmd','Start-DeepBot.ps1','Start-DeepBot.cmd') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $GameDirectory $name) -Force
}
Write-Host "DeepBot 已安装到：$GameDirectory" -ForegroundColor Green
Write-Host '房主下一步运行 Configure-DeepBot-Key.cmd；客端把 BotCount 设置为 0。' -ForegroundColor Cyan

