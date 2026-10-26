param(
    [Parameter(Mandatory = $true)][string] $PgPassFile,
    [string] $HostName = 'localhost',
    [int] $Port = 5432,
    [string] $PgBin = 'C:\Program Files\PostgreSQL\18\bin'
)
$ErrorActionPreference = 'Stop'
# After Gate A approval/application only. This does not start Backend services.
# Supply an NTFS-restricted passfile outside Git; never pass passwords in argv.
$resolvedPassFile = (Resolve-Path -LiteralPath $PgPassFile).Path
if (-not (Test-Path -LiteralPath $resolvedPassFile -PathType Leaf)) { throw 'Passfile is not a file.' }
$runtimeRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if ($resolvedPassFile.StartsWith($runtimeRepoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep credential files outside the repository.'
}
$psql = Join-Path $PgBin 'psql.exe'
if (-not (Test-Path -LiteralPath $psql -PathType Leaf)) { throw 'psql is missing.' }
$readMatrix = [ordered]@{
    'identity_svc' = @('tenants','branches','users','roles','tenant_users','staff_assignments','refresh_sessions')
    'menu_svc' = @('categories','products','product_variants','product_option_groups','product_options','branch_products')
    'table_reservation_svc' = @('table_areas','cafe_tables','table_sessions','table_session_tables')
    'ordering_svc' = @('orders','order_items','order_item_options','order_item_events')
    'payment_svc' = @('bills','bill_items','payments','payment_events')
    'kitchen_svc' = @('orders','order_items','order_item_events')
    'inventory_svc' = @()
    'workforce_svc' = @()
    'reporting_svc' = @()
    'customer_svc' = @()
    'ai_svc' = @()
}
$previousPassFile = $env:PGPASSFILE
$previousPassword = $env:PGPASSWORD
try {
    $env:PGPASSFILE = $resolvedPassFile
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    foreach ($roleName in $readMatrix.Keys) {
        $statements = @(
            'BEGIN READ ONLY;',
            "SELECT current_database()='aicos_db' AND current_user='$roleName' AND current_setting('transaction_read_only')='on';"
        )
        foreach ($table in $readMatrix[$roleName]) {
            # Names are fixed in the reviewed matrix, not accepted from input.
            $statements += "SELECT * FROM public.$table LIMIT 0;"
        }
        $statements += 'COMMIT;'
        $result = & $psql -X -qAt -w -h $HostName -p $Port -U $roleName -d aicos_db -v ON_ERROR_STOP=1 -c ($statements -join [Environment]::NewLine) 2>&1
        if ($LASTEXITCODE -ne 0 -or @($result | Where-Object { $_ -eq 't' }).Count -ne 1) {
            throw "Connection/owned-read probe failed for $roleName; no services started."
        }
        Write-Output "PASS: $roleName authenticated to aicos_db; approved reads succeeded."
    }
}
finally {
    $env:PGPASSFILE = $previousPassFile
    $env:PGPASSWORD = $previousPassword
}
