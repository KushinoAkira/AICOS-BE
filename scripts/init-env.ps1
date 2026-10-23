# Creates .env from .env.example with freshly generated random secrets. Refuses to overwrite an existing .env.
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root '.env'
if (Test-Path $target) { Write-Host '.env already exists - not touching it.'; exit 0 }

function New-RandomText([int]$bytes) {
    $b = New-Object byte[] $bytes
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    ([Convert]::ToBase64String($b) -replace '[^A-Za-z0-9]', '').Substring(0, [Math]::Min($bytes, 40))
}

$values = @{
    POSTGRES_PASSWORD   = New-RandomText 30
    SERVICE_DB_PASSWORD = New-RandomText 30
    REDIS_PASSWORD      = New-RandomText 30
    JWT_SIGNING_KEY     = New-RandomText 48
    PAYMENTS_SECRET_KEY = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 } | ForEach-Object { [byte]$_ }))
    SEED_ADMIN_PASSWORD = (New-RandomText 14) + 'aA1!'
    PGADMIN_PASSWORD    = New-RandomText 20
}
$lines = Get-Content (Join-Path $root '.env.example') | ForEach-Object {
    $name = ($_ -split '=')[0]
    if ($values.ContainsKey($name)) { "$name=$($values[$name])" } else { $_ }
}
Set-Content -Path $target -Value $lines -Encoding ascii
Write-Host "Created $target. Seed admin: see SEED_ADMIN_EMAIL / SEED_ADMIN_PASSWORD in it."
