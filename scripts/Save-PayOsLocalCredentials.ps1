[CmdletBinding()]
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'

if ($SelfTest) {
    $sample = ConvertTo-SecureString 'aicos-dpapi-self-test-not-a-key' -AsPlainText -Force
    $cipher = ConvertFrom-SecureString $sample
    if ($cipher.Contains('aicos-dpapi-self-test')) { throw 'DPAPI did not protect the sample' }
    $roundtrip = ConvertTo-SecureString $cipher
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($roundtrip)
    try {
        if ([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer) -cne 'aicos-dpapi-self-test-not-a-key') { throw 'DPAPI round-trip failed' }
    } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $sample.Dispose(); $roundtrip.Dispose() }
    Write-Output 'PASS: Windows DPAPI round-trip; no file, real credential, database or provider call.'
    return
}

$secretDirectory = Join-Path $env:LOCALAPPDATA 'AICOS\payos-staff'
$secretPath = Join-Path $secretDirectory 'credentials.dpapi.json'
if (Test-Path -LiteralPath $secretPath) { throw 'Protected payOS credentials already exist. No overwrite performed.' }
$null = New-Item -ItemType Directory -Path $secretDirectory -Force
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
$permissions = New-Object Security.AccessControl.DirectorySecurity
$permissions.SetOwner($owner)
$permissions.SetAccessRuleProtection($true, $false)
foreach ($sid in @($owner, (New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'))) {
    $permissions.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -LiteralPath $secretDirectory -AclObject $permissions

$protected = [ordered]@{ Format = 'AICOS-payOS-DPAPI-v1' }
foreach ($name in @('ClientId', 'ApiKey', 'ChecksumKey')) {
    $secret = Read-Host ("payOS " + $name + ' (nhap an, khong phai mat khau DB)') -AsSecureString
    try {
        if ($secret.Length -eq 0) { throw 'A required payOS value was empty; nothing saved.' }
        $protected[$name] = ConvertFrom-SecureString $secret
    } finally { $secret.Dispose() }
}
$bytes = [Text.Encoding]::UTF8.GetBytes(($protected | ConvertTo-Json))
$secretFile = [IO.File]::Open($secretPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $secretFile.Write($bytes, 0, $bytes.Length) } finally { $secretFile.Dispose() }
Write-Output ('Saved encrypted credentials: ' + $secretPath)
Write-Output 'No provider/database configuration changed. StaffPaymentsEnabled remains OFF; no bank transaction sent.'
