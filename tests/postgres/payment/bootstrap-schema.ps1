param(
    [Parameter(Mandatory = $true)]
    [string] $BaselineSql,
    [string] $PsqlPath = "C:\Program Files\PostgreSQL\18\bin\psql.exe",
    [string] $HostName = "localhost",
    [int] $Port = 55432,
    [string] $Database = "aicos_payment_test",
    [string] $Username = "postgres",
    [string] $Password = "aicos_payment_test"
)

$ErrorActionPreference = "Stop"

if ($Database -ne "aicos_payment_test") {
    throw "Refusing to mutate database '$Database'. Expected aicos_payment_test."
}

if (-not (Test-Path -LiteralPath $PsqlPath)) {
    throw "psql was not found at '$PsqlPath'."
}

if (-not (Test-Path -LiteralPath $BaselineSql)) {
    throw "Baseline SQL was not found at '$BaselineSql'."
}

$migrationRoot = Resolve-Path (Join-Path $PSScriptRoot "../../../docs/migrations/payments")
$env:PGPASSWORD = $Password

try {
    $actualDatabase = & $PsqlPath -X -h $HostName -p $Port -U $Username -d $Database -tAc "SELECT current_database();"
    if ($LASTEXITCODE -ne 0 -or $actualDatabase.Trim() -ne "aicos_payment_test") {
        throw "Database safety check failed."
    }

    & $PsqlPath -X -v ON_ERROR_STOP=1 -h $HostName -p $Port -U $Username -d $Database -f $BaselineSql
    if ($LASTEXITCODE -ne 0) { throw "Baseline schema failed." }

    foreach ($fileName in @(
        "00_preflight.sql",
        "01_expand_columns.sql",
        "02_create_payment_events.sql",
        "03_backfill.sql",
        "04_indexes.sql",
        "05_contract_constraints.sql",
        "06_verify.sql"
    )) {
        & $PsqlPath -X -v ON_ERROR_STOP=1 -h $HostName -p $Port -U $Username -d $Database -f (Join-Path $migrationRoot $fileName)
        if ($LASTEXITCODE -ne 0) { throw "Payment schema stage '$fileName' failed." }
    }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

