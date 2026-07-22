$ErrorActionPreference = 'Stop'
$gameExe = Join-Path $PSScriptRoot 'Among Us.exe'
if (-not (Test-Path -LiteralPath $gameExe)) {
    throw '找不到 Among Us.exe。请把启动脚本安装到游戏根目录。'
}
$keyPath = Join-Path $env:LOCALAPPDATA 'AmongUsDeepSeekBots\api-key.txt'
if (-not (Test-Path -LiteralPath $keyPath)) {
    Write-Host '尚未配置 API 密钥；本次将使用本地后备逻辑。可先运行 Configure-DeepBot-Key.cmd。' -ForegroundColor Yellow
}
Start-Process -FilePath $gameExe -WorkingDirectory $PSScriptRoot

