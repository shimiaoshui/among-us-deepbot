param([switch]$ValidateOnly)

$ErrorActionPreference = 'Stop'
$gameExe = Join-Path $PSScriptRoot 'Among Us.exe'
if (-not (Test-Path -LiteralPath $gameExe)) {
    throw 'Among Us.exe was not found. Install this launcher in the game root directory.'
}
$requiredBootChain = @(
    'winhttp.dll',
    'doorstop_config.ini',
    'dotnet\coreclr.dll',
    'BepInEx\core\BepInEx.Unity.IL2CPP.dll',
    'BepInEx\plugins\Reactor.dll',
    'BepInEx\plugins\TheOtherRoles.dll',
    'BepInEx\plugins\AmongUsDeepSeekBots.dll',
    'DeepBot-Compatibility.json'
)
$missing = @($requiredBootChain | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $_))
})
if ($missing.Count -gt 0) {
    throw "DeepBot cannot start because the BepInEx boot chain is incomplete. Re-run the matching installer. Missing: $($missing -join ', ')"
}
$doorstop = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'doorstop_config.ini') -Raw
if ($doorstop -notmatch '(?im)^\s*enabled\s*=\s*true\s*$' -or
    $doorstop -notmatch '(?im)^\s*coreclr_path\s*=\s*dotnet\\coreclr\.dll\s*$') {
    throw 'DeepBot cannot start because doorstop_config.ini is disabled or does not reference dotnet\coreclr.dll. Re-run the matching installer.'
}
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'DeepBot-Compatibility.json') -Raw | ConvertFrom-Json
$compatibilityFiles = @(
    @{ Path = 'BepInEx\plugins\TheOtherRoles.dll'; Expected = $manifest.TorSha256 },
    @{ Path = 'BepInEx\plugins\Reactor.dll'; Expected = $manifest.ReactorSha256 },
    @{ Path = 'BepInEx\plugins\AmongUsDeepSeekBots.dll'; Expected = $manifest.DeepBotSha256 }
)
foreach ($item in $compatibilityFiles) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $PSScriptRoot $item.Path)).Hash
    if (-not [string]::Equals($actual, [string]$item.Expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "DeepBot cannot start because $($item.Path) is from a different release. Reinstall every Host and Client from the same GitHub release."
    }
}
$keyPath = Join-Path $env:LOCALAPPDATA 'AmongUsDeepSeekBots\api-key.txt'
if (-not (Test-Path -LiteralPath $keyPath)) {
    Write-Host 'API key is not configured; local fallback logic will be used. Run Configure-DeepBot-Key.cmd to configure it.' -ForegroundColor Yellow
}
if ($ValidateOnly) {
    Write-Output "DeepBot boot chain and compatibility fingerprint are valid for release $($manifest.ReleaseVersion) ($($manifest.CompatibilityId))."
    exit 0
}
$oldDoorstopDisable = $env:DOORSTOP_DISABLE
try {
    Remove-Item Env:DOORSTOP_DISABLE -ErrorAction SilentlyContinue
    Start-Process -FilePath $gameExe -WorkingDirectory $PSScriptRoot
}
finally {
    if ($null -ne $oldDoorstopDisable) {
        $env:DOORSTOP_DISABLE = $oldDoorstopDisable
    }
}

