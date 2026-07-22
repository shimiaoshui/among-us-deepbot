$ErrorActionPreference = 'Stop'
$gameExe = Join-Path $PSScriptRoot 'Among Us.exe'
if (-not (Test-Path -LiteralPath $gameExe)) {
    throw 'Among Us.exe was not found. Install this launcher in the game root directory.'
}
$keyPath = Join-Path $env:LOCALAPPDATA 'AmongUsDeepSeekBots\api-key.txt'
if (-not (Test-Path -LiteralPath $keyPath)) {
    Write-Host 'API key is not configured; local fallback logic will be used. Run Configure-DeepBot-Key.cmd to configure it.' -ForegroundColor Yellow
}
Start-Process -FilePath $gameExe -WorkingDirectory $PSScriptRoot

