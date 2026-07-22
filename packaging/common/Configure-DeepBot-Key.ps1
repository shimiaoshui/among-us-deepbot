$ErrorActionPreference = 'Stop'

$targetDirectory = Join-Path $env:LOCALAPPDATA 'AmongUsDeepSeekBots'
$targetPath = Join-Path $targetDirectory 'api-key.txt'

Write-Host 'DeepBot API 密钥永久配置' -ForegroundColor Cyan
Write-Host '密钥只保存在当前 Windows 用户目录，不会写入游戏、配置或发布包。' -ForegroundColor DarkGray
$secureKey = Read-Host '请输入 API Key' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
$plainKey = $null

try {
    $plainKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr).Trim()
    if ([string]::IsNullOrWhiteSpace($plainKey) -or -not $plainKey.StartsWith('sk-')) {
        throw '密钥为空或格式不正确。'
    }
    [IO.Directory]::CreateDirectory($targetDirectory) | Out-Null
    [IO.File]::WriteAllText($targetPath, $plainKey, [Text.UTF8Encoding]::new($false))
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $acl = [Security.AccessControl.FileSecurity]::new()
    $acl.SetOwner([Security.Principal.NTAccount]::new($identity))
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'Allow'))
    Set-Acl -LiteralPath $targetPath -AclObject $acl
    Write-Host '已保存。以后启动游戏不需要再次输入。' -ForegroundColor Green
}
finally {
    if ($bstr -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    $plainKey = $null
}

