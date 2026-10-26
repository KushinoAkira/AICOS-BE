param(
    [Parameter(Mandatory = $true)][string] $BackupDirectory,
    [Parameter(Mandatory = $true)][string] $TestDatabase,
    [string] $HostName = 'localhost', [int] $Port = 5432,
    [string] $Username = 'postgres',
    [string] $PgBin = 'C:\Program Files\PostgreSQL\18\bin'
)
$ErrorActionPreference = 'Stop'
if ($TestDatabase -cnotmatch '^aicos_restore_test_[0-9]{8}_[0-9]{6}$') { throw 'Only a new aicos_restore_test_YYYYMMDD_HHMMSS database is allowed.' }
$manifest = Get-Content "$BackupDirectory/checksums.json" -Raw | ConvertFrom-Json
foreach ($required in @('aicos_db.dump','schema.sql','before.txt','after.txt','archive-list.txt')) {
    if (@($manifest | Where-Object { $_.File -eq $required }).Count -ne 1) { throw 'Incomplete backup manifest.' }
}
foreach ($entry in $manifest) {
    if ($entry.File -ne (Split-Path $entry.File -Leaf)) { throw 'Invalid manifest path.' }
    if ((Get-FileHash (Join-Path $BackupDirectory $entry.File)).Hash -ne $entry.Hash) { throw 'Backup checksum mismatch.' }
}
$connection = @('-h', $HostName, '-p', $Port, '-U', $Username, '-w')
$exists = & "$PgBin/psql.exe" -X -qAt -v ON_ERROR_STOP=1 @connection -d postgres -c "SELECT count(*) FROM pg_database WHERE datname = '$TestDatabase';"
if ($LASTEXITCODE -or $exists.Trim() -ne '0') { throw 'Target exists or check failed; overwrite refused.' }
& "$PgBin/createdb.exe" @connection --template=template0 $TestDatabase
if ($LASTEXITCODE) { throw 'Test database creation failed.' }
& "$PgBin/pg_restore.exe" @connection -d $TestDatabase --exit-on-error --single-transaction --no-owner --no-privileges "$BackupDirectory/aicos_db.dump"
if ($LASTEXITCODE) { throw 'Restore failed. Target retained for inspection.' }
& "$PgBin/psql.exe" -X -qAt -v ON_ERROR_STOP=1 @connection -d $TestDatabase -f "$PSScriptRoot/fingerprint.sql" -o "$BackupDirectory/restored-data.txt"
if ($LASTEXITCODE) { throw 'Restored fingerprint failed.' }
if ((Get-FileHash "$BackupDirectory/after.txt").Hash -ne (Get-FileHash "$BackupDirectory/restored-data.txt").Hash) { throw 'Restored counts/digests differ.' }
& "$PgBin/pg_dump.exe" @connection -d $TestDatabase --schema-only --no-owner --no-privileges -f "$BackupDirectory/restored-schema.sql"
if ($LASTEXITCODE) { throw 'Restored schema export failed.' }
# Random psql restrict keys are not schema differences.
$sourceSchema = Get-Content "$BackupDirectory/schema.sql" | Where-Object { $_ -notmatch '^\\(un)?restrict ' }
$restoredSchema = Get-Content "$BackupDirectory/restored-schema.sql" | Where-Object { $_ -notmatch '^\\(un)?restrict ' }
if (Compare-Object $sourceSchema $restoredSchema) { throw 'Restored schema differs.' }
Write-Output "PASS: schema and public table counts/digests match in $TestDatabase. Database retained."
