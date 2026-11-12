[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-FloorApi([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null) {
    $parameters = @{ Uri = 'http://127.0.0.1:8081' + $Path; Method = $Method; TimeoutSec = 30; ContentType = 'application/json; charset=utf-8' }
    if ($Token) { $parameters.Headers = @{ Authorization = 'Bearer ' + $Token } }
    if ($null -ne $Body) { $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress)) }
    try { Invoke-RestMethod @parameters }
    catch { throw ('Floor-plan API stopped: ' + $Method + ' ' + $Path + '. Response and credentials omitted.') }
}

foreach ($name in @('postgres','table','identity','gateway')) {
    $container = docker inspect ('aicos-main-' + $name) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $container.Config.Labels.'com.docker.compose.project' -ne 'aicos-main' -or -not $container.State.Running) {
        throw 'Expected running aicos-main containers were not verified.'
    }
    if ($name -in @('table','identity') -and $container.Config.Env -notcontains 'Database__MigrateOnStartup=false') { throw 'Startup migrations must be disabled.' }
    if ($name -eq 'table') {
        $connection = $container.Config.Env | Where-Object { $_.StartsWith('ConnectionStrings__Default=') }
        if ($connection -notmatch 'Host=postgres;' -or $connection -notmatch 'Database=aicos_table_reservation;' -or $connection -notmatch 'Username=table_reservation_svc;') {
            throw 'Unexpected Table database target.'
        }
    }
}

$credential = Import-Clixml -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) 'aicos-local-cashier-admin.clixml')
$auth = Invoke-FloorApi POST '/api/v1/identity/auth/login' @{ login = $credential.UserName; password = $credential.GetNetworkCredential().Password }
try {
    $me = Invoke-FloorApi GET '/api/v1/identity/auth/me' $null $auth.accessToken
    if ($me.role -ne 'BrandAdmin' -or $me.email -ne 'admin@aicos-cashier-local.test' -or -not $me.tenantId) { throw 'Unexpected fixture administrator scope.' }
    $page = Invoke-FloorApi GET '/api/v1/identity/branches/?pageSize=100' $null $auth.accessToken
    if ($page.totalCount -ne 3 -or @($page.items).Count -ne 3 -or @($page.items | Where-Object { $_.status -ne 'Active' }).Count) { throw 'Expected exactly three active local branches.' }
    $plans = @()
    foreach ($branch in $page.items) {
        $floor = Invoke-FloorApi GET ('/api/v1/tables/floor-plan?branchId=' + $branch.id) $null $auth.accessToken
        $areas = @($floor.areas)
        $before = @($areas | ForEach-Object { $_.tables })
        if ($floor.branchId -ne $branch.id -or $areas.Count -ne 3 -or $before.Count -lt 30 -or $before.Count -gt 40) { throw 'Unexpected floor-plan baseline; no automatic repair.' }
        $rows = @()
        for ($i = 0; $i -lt $areas.Count; $i++) {
            $area = $areas[$i]
            $prefixes = @($area.tables | ForEach-Object { if ($_.tableCode -match '^([A-Z]+)[0-9]+$') { $Matches[1] } } | Select-Object -Unique)
            if ($prefixes.Count -ne 1) { throw 'Area table-code prefix is ambiguous.' }
            $prefix = $prefixes[0]
            for ($base = 1; $base -le 10; $base++) {
                if ($area.tables.tableCode -notcontains ($prefix + $base.ToString('00'))) { throw 'Original ten-table area baseline was not verified.' }
            }
            $last = if ($i -eq 0) { 14 } else { 13 }
            for ($number = 11; $number -le $last; $number++) {
                $code = $prefix + $number.ToString('00')
                $existing = @($before | Where-Object { $_.tableCode -eq $code })
                if ($existing.Count) {
                    if ($existing.Count -ne 1 -or $existing[0].areaId -ne $area.id) { throw 'Existing code belongs to a different area.' }
                } else {
                    $rows += @{ areaId = $area.id; tableCode = $code; capacity = 4; positionX = (($number - 1) % 2); positionY = [int][Math]::Floor(($number - 1) / 2) }
                }
            }
        }
        if ($before.Count + $rows.Count -ne 40) { throw 'Planned result must be exactly forty tables per branch.' }
        $plans += @{ Branch = $branch; Before = $before; Rows = $rows }
    }
    Write-Output ('Preflight PASS: three branches; missing tables=' + (($plans | ForEach-Object { $_.Rows.Count } | Measure-Object -Sum).Sum))
    foreach ($plan in $plans) { Write-Output ($plan.Branch.name + ': ' + $plan.Before.Count + ' -> 40; add ' + $plan.Rows.Count) }
    if (-not $Apply) { Write-Output 'Preview only; add -Apply to provision missing tables.'; return }
    $evidence = @()
    foreach ($plan in $plans) {
        if ($plan.Rows.Count) {
            $result = Invoke-FloorApi POST '/api/v1/tables/dining-tables/batch' @{ branchId = $plan.Branch.id; rows = @($plan.Rows) } $auth.accessToken
            if ($result.created -ne $plan.Rows.Count -or $result.areasCreated -ne 0) { throw 'Unexpected batch result; stop without deleting or retrying.' }
        }
        $readback = Invoke-FloorApi GET ('/api/v1/tables/floor-plan?branchId=' + $plan.Branch.id) $null $auth.accessToken
        $after = @($readback.areas | ForEach-Object { $_.tables })
        if ($after.Count -ne 40 -or @($after.tableCode | Select-Object -Unique).Count -ne 40) { throw 'Readback count/uniqueness failed.' }
        foreach ($original in $plan.Before) {
            $same = @($after | Where-Object { $_.id -eq $original.id })
            if ($same.Count -ne 1 -or $same[0].qrToken -ne $original.qrToken -or $same[0].status -ne $original.status -or $same[0].areaId -ne $original.areaId) { throw 'An original table changed; stop for review.' }
        }
        $evidence += @{ branch = $plan.Branch.name; branchId = $plan.Branch.id; before = $plan.Before.Count; after = $after.Count; created = $plan.Rows.Count; originalsPreserved = $true }
        Write-Output ('PASS: ' + $plan.Branch.name + ' has 40 tables; original IDs/QR/status preserved.')
        $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) 'aicos-floor-expansion-20261113.json') -Encoding UTF8
    }
} finally {
    if ($auth.refreshToken) { $null = Invoke-FloorApi POST '/api/v1/identity/auth/logout' @{ refreshToken = $auth.refreshToken } $auth.accessToken }
    $auth = $null
    $credential = $null
}
