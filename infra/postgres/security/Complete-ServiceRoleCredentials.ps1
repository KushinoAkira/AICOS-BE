param(
    [Parameter(Mandatory = $true)][string] $EvidenceDirectory,
    [string] $PgBin = 'C:\Program Files\PostgreSQL\18\bin'
)
$ErrorActionPreference = 'Stop'
# Interactive console only. No plaintext credential file, transcript or argv.
# Run after approved role creation and ACL verification. Starts no service.
$roles = @('identity_svc','menu_svc','table_reservation_svc','ordering_svc','payment_svc','inventory_svc','kitchen_svc','workforce_svc','reporting_svc','customer_svc','ai_svc')
$readMatrix = @{
    'identity_svc' = @('tenants','branches','users','roles','tenant_users','staff_assignments','refresh_sessions')
    'menu_svc' = @('categories','products','product_variants','product_option_groups','product_options','branch_products')
    'table_reservation_svc' = @('table_areas','cafe_tables','table_sessions','table_session_tables')
    'ordering_svc' = @('orders','order_items','order_item_options','order_item_events')
    'payment_svc' = @('bills','bill_items','payments','payment_events')
    'kitchen_svc' = @('orders','order_items','order_item_events')
}
$psql = Join-Path $PgBin 'psql.exe'
$verifier = Join-Path $PSScriptRoot 'verify-service-permissions.sql'
$connection = @('-X','-h','localhost','-p','5432','-U','postgres','-d','aicos_db','-v','ON_ERROR_STOP=1')
$progressPath = Join-Path $EvidenceDirectory 'credential-progress.json'
$resultPath = Join-Path $EvidenceDirectory 'credential-result.json'
$state = [ordered]@{ Status='AwaitingHiddenInput'; PasswordsAssigned=@(); LoginEnabled=$false; ConnectionsVerified=@() }
function Save-Progress { $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $progressPath -Encoding utf8 }
function Assert-Privileges([string] $login) {
    $output = & $psql @connection -qAt -F '|' -v "expect_login=$login" -f $verifier 2>&1
    if ($LASTEXITCODE -ne 0 -or @($output | Where-Object { $_ -match '^SUMMARY\|\|issue_count\|0\|' }).Count -ne 1) {
        throw 'Effective privilege matrix did not pass.'
    }
}
$previousPassword = $env:PGPASSWORD
try {
    if (-not [Environment]::UserInteractive) { throw 'A local interactive console is required.' }
    if (-not (Test-Path -LiteralPath $EvidenceDirectory -PathType Container)) { throw 'Evidence directory missing.' }
    if (Test-Path -LiteralPath $resultPath) { throw 'A result already exists; do not silently rotate credentials.' }
    Save-Progress
    Write-Host 'AICOS Gate A: enter secrets only at masked prompts. Nothing is written to a plaintext file.'
    $adminSecret = Read-Host 'PostgreSQL administrator password' -AsSecureString
    $env:PGPASSWORD = ([PSCredential]::new('postgres', $adminSecret)).GetNetworkCredential().Password
    Assert-Privileges 'false'
    $state.Status='AssigningPasswords'
    Save-Progress
    foreach ($role in $roles) {
        Write-Host "Assign a unique secret for $role (psql asks twice; input is hidden)."
        & $psql @connection -w -c "\password $role"
        if ($LASTEXITCODE -ne 0) { throw "Password step failed for $role." }
        $state.PasswordsAssigned += $role
        Save-Progress
    }
    # Refuse LOGIN if any approved role lacks a SCRAM credential. Never output it.
    $names = ($roles | ForEach-Object { "'$_'" }) -join ','
    $missing = & $psql @connection -w -qAt -c "BEGIN READ ONLY; SELECT count(*) FROM pg_authid WHERE rolname IN ($names) AND (rolpassword IS NULL OR rolpassword NOT LIKE 'SCRAM-SHA-256$%'); COMMIT;"
    if ($LASTEXITCODE -ne 0 -or $missing.Trim() -ne '0') { throw 'At least one SCRAM credential is missing.' }
    Assert-Privileges 'false'
    $activation = 'BEGIN; ' + (($roles | ForEach-Object { "ALTER ROLE $_ LOGIN;" }) -join ' ') + ' COMMIT;'
    & $psql @connection -w -c $activation
    if ($LASTEXITCODE -ne 0) { throw 'LOGIN activation failed.' }
    $state.LoginEnabled=$true
    $state.Status='VerifyingActualLogins'
    Save-Progress
    Assert-Privileges 'true'
    # Force each actual service password prompt; do not use admin SET ROLE.
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    foreach ($role in $roles) {
        Write-Host "Verify the password just assigned to $role (hidden prompt)."
        $statements = @('BEGIN READ ONLY;', "SELECT current_user='$role' AND current_database()='aicos_db' AND current_setting('transaction_read_only')='on';")
        foreach ($table in $readMatrix[$role]) { $statements += "SELECT * FROM public.$table LIMIT 0;" }
        $statements += 'COMMIT;'
        $actual = & $psql -X -qAt -W -h localhost -p 5432 -U $role -d aicos_db -v ON_ERROR_STOP=1 -c ($statements -join [Environment]::NewLine)
        if ($LASTEXITCODE -ne 0 -or @($actual | Where-Object { $_ -eq 't' }).Count -ne 1) { throw "Actual login/read failed for $role." }
        $state.ConnectionsVerified += $role
        Save-Progress
    }
    $state.Status='Completed'
    Save-Progress
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Host 'Completed: 11 hidden password assignments and actual logins. Backend remains stopped.'
}
catch {
    $state.Status='Stopped'
    $state | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Host 'Stopped. No automatic rollback, permission change or service startup will run.'
    exit 1
}
finally {
    $env:PGPASSWORD = $previousPassword
    if ($adminSecret) { $adminSecret.Dispose() }
}
