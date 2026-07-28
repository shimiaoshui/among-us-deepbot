param(
    [Parameter(Mandatory = $true)]
    [string]$TheOtherRolesSource,

    [string]$ReleaseVersion = '0.10.5',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\release-assets')
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$stageRoot = [IO.Path]::GetFullPath((Join-Path $outputRoot "source-staging-$ReleaseVersion"))
$allowedPrefix = $outputRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $stageRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a staging path outside $outputRoot"
}
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

$deepBotStage = Join-Path $stageRoot "DeepBot-$ReleaseVersion"
$torStage = Join-Path $stageRoot 'TheOtherRoles-4.6.0'
New-Item -ItemType Directory -Force -Path $deepBotStage, $torStage | Out-Null

foreach ($name in @(
    '.gitignore',
    'LICENSE',
    'README.md',
    'RELEASE_NOTES.md',
    'SECURITY.md',
    'THIRD_PARTY_NOTICES.md'
)) {
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination (Join-Path $deepBotStage $name)
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs') -Destination (Join-Path $deepBotStage 'docs') -Recurse

$packagingStage = Join-Path $deepBotStage 'packaging'
$sourceStage = Join-Path $deepBotStage 'src'
New-Item -ItemType Directory -Force -Path $packagingStage, $sourceStage | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\Build-Installers.ps1') -Destination $packagingStage
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\Build-SourceArchive.ps1') -Destination $packagingStage
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\Test-Installers.ps1') -Destination $packagingStage
foreach ($directory in @('common', 'standalone', 'tor46', 'uninstaller', 'installer')) {
    $destination = Join-Path $packagingStage $directory
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "packaging\$directory") -File |
        Copy-Item -Destination $destination
}
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -File |
    Copy-Item -Destination $sourceStage

& robocopy ([IO.Path]::GetFullPath($TheOtherRolesSource)) $torStage /E /XD .git bin obj /XF '*.user' '*.suo' '*.log' | Out-Null
if ($LASTEXITCODE -gt 7) {
    throw "TOR source staging failed with robocopy exit code $LASTEXITCODE"
}

$archive = Join-Path $outputRoot "TheOtherRoles-4.6.0-DeepBot-$ReleaseVersion-Source.zip"
if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $archive -CompressionLevel Optimal
Remove-Item -LiteralPath $stageRoot -Recurse -Force

$releaseNames = @(
    'AmongUs-DeepBot-Host-Installer.exe',
    'AmongUs-DeepBot-Client-Installer.exe',
    'AmongUs-DeepBot-Host-Uninstaller.exe',
    'AmongUs-DeepBot-Client-Uninstaller.exe',
    [IO.Path]::GetFileName($archive)
)
$checksums = foreach ($name in $releaseNames) {
    $asset = Join-Path $outputRoot $name
    if (-not (Test-Path -LiteralPath $asset)) {
        throw "Release checksum input is missing: $asset"
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash.ToLowerInvariant()
    "$hash  $name"
}
Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Value $checksums -Encoding ASCII
Get-Item -LiteralPath $archive
