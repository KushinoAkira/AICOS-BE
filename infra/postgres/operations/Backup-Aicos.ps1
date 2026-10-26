param(
    [Parameter(Mandatory = $true)][string] $OutputDirectory,
    [string] $HostName = 'localhost', [int] $Port = 5432,
    [string] $Username = 'postgres',
    [string] $PgBin = 'C:\Program Files\PostgreSQL\18\bin'
)
$ErrorActionPreference = 'Stop'
# Use PGPASSFILE or process PGPASSWORD. Never log credentials.
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new backup directory; overwrite forbidden.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$connection = @('-h', $HostName, '-p', $Port, '-U', $Username, '-d', 'aicos_db', '-w')
$previousOptions = $env:PGOPTIONS
try {
    $env:PGOPTIONS = '-c default_transaction_read_only=on'
    & "$PgBin/psql.exe" -X -qAt -v ON_ERROR_STOP=1 @connection -f "$PSScriptRoot/fingerprint.sql" -o "$OutputDirectory/before.txt"
    if ($LASTEXITCODE) { throw 'Source fingerprint failed.' }
    & "$PgBin/pg_dump.exe" @connection -Fc -f "$OutputDirectory/aicos_db.dump"
    if ($LASTEXITCODE) { throw 'Backup failed; directory incomplete.' }
    & "$PgBin/pg_dump.exe" @connection --schema-only --no-owner --no-privileges -f "$OutputDirectory/schema.sql"
    if ($LASTEXITCODE) { throw 'Schema backup failed.' }
    & "$PgBin/psql.exe" -X -qAt -v ON_ERROR_STOP=1 @connection -f "$PSScriptRoot/fingerprint.sql" -o "$OutputDirectory/after.txt"
    if ($LASTEXITCODE) { throw 'Source fingerprint failed.' }
    if ((Get-FileHash "$OutputDirectory/before.txt").Hash -ne (Get-FileHash "$OutputDirectory/after.txt").Hash) { throw 'Source changed during backup; repeat in a stable window.' }
    & "$PgBin/pg_restore.exe" --list "$OutputDirectory/aicos_db.dump" | Out-File "$OutputDirectory/archive-list.txt" -Encoding utf8
    if ($LASTEXITCODE) { throw 'Archive inventory failed.' }
    Get-ChildItem -LiteralPath $OutputDirectory -File | Get-FileHash -Algorithm SHA256 |
        Select-Object @{Name='File';Expression={Split-Path $_.Path -Leaf}},Hash |
        ConvertTo-Json | Set-Content "$OutputDirectory/checksums.json" -Encoding utf8
    Write-Output "Backup complete: $OutputDirectory"
} finally { $env:PGOPTIONS = $previousOptions }
