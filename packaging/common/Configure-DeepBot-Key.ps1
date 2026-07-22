$ErrorActionPreference = 'Stop'

$targetDirectory = Join-Path $env:LOCALAPPDATA 'AmongUsDeepSeekBots'
$targetPath = Join-Path $targetDirectory 'api-key.txt'

Write-Host 'DeepBot permanent API key configuration' -ForegroundColor Cyan
Write-Host 'The key is stored only under the current Windows user profile, never in the game, config, or release package.' -ForegroundColor DarkGray
$secureKey = Read-Host 'Enter API Key' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
$plainKey = $null

try {
    $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim()
    if ([string]::IsNullOrWhiteSpace($plainKey) -or -not $plainKey.StartsWith('sk-')) {
        throw 'The key is empty or has an invalid format.'
    }
    [IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
    [IO.File]::WriteAllText($targetPath, $plainKey, [Text.UTF8Encoding]::new($false))
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner([Security.Principal.NTAccount]::new($identity))
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $targetPath -AclObject $acl
    Write-Host 'Saved. Future game launches will not ask for the key again.' -ForegroundColor Green
}
finally {
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $plainKey = $null
}

