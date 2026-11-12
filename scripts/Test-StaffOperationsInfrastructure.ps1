[CmdletBinding()]
param([switch]$Run, [switch]$Recovery, [switch]$Cups, [switch]$CupAcl)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
if (@(@($Recovery, $Cups, $CupAcl) | Where-Object { $_ }).Count -gt 1) { throw 'Choose only one infrastructure test mode.' }
if (-not $Run) {
    if ($CupAcl) { Write-Output 'ACL plan: guarded disposable PostgreSQL/Redis; reviewed Cup schema, two non-owner test service logins, reviewed GRANT proposals, real HTTP/SQL workflow, deny audit mutation; never operating roles.'; return }
    if ($Cups) { Write-Output 'Cup plan: guarded disposable PostgreSQL/Redis; checksum/preflight, reviewed test schema, HTTP/crash/retry/fencing tests; no operating database.'; return }
    if ($Recovery) { Write-Output 'Recovery plan: new guarded PostgreSQL/Redis only; apply EF migrations on two empty disposable test databases, execute HTTP/worker/process-crash tests, then remove only this run resources. Never use a deployment database.'; return }
    Write-Output 'Plan: disposable PostgreSQL 16 + Redis 7, dedicated bridge network, tmpfs only, random loopback ports. Guard container/cluster/database before approved test-only EnsureCreated and fixtures. Real Redis transport; fake Identity/Menu/Table and test-signed JWT. No migrations or Payment/Bill API.'
    return
}
docker info --format '{{.ServerVersion}}' 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Docker Engine unavailable.' }

function Invoke-TestDocker {
    param([string[]]$Arguments)
    $result = & docker @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw ('Test Docker command failed: ' + $Arguments[0]) }
    return $result
}
function Get-ExistingSnapshot {
    param([string[]]$Ids)
    $snapshot = @()
    foreach ($id in $Ids) {
        $item = Invoke-TestDocker @('inspect', $id) | ConvertFrom-Json
        $snapshot += [pscustomobject]@{ Id = $item.Id; Image = $item.Image; Started = $item.State.StartedAt; Status = $item.State.Status; Restarts = $item.RestartCount }
    }
    return ($snapshot | Sort-Object Id | ConvertTo-Json -Compress)
}
function Assert-TestContainer {
    param([string]$Id, [string]$Network, [int]$InternalPort)
    $item = Invoke-TestDocker @('inspect', $Id) | ConvertFrom-Json
    $networks = @($item.NetworkSettings.Networks.PSObject.Properties.Name)
    $portName = $InternalPort.ToString() + '/tcp'
    $bindings = @($item.NetworkSettings.Ports.$portName)
    if ($item.Id -ne $Id -or $item.Config.Labels.'aicos.purpose' -ne 'staff-operations-test' -or $networks.Count -ne 1 -or $networks[0] -ne $Network) { throw 'Disposable test container/network guard failed.' }
    if (@($item.Mounts | Where-Object { $null -ne $_ -and $_.Type -ne 'tmpfs' }).Count -ne 0) {
        $mounts = $item.Mounts | Select-Object Type, Destination | ConvertTo-Json -Compress
        throw ('Test container must not use persistent volumes or bind mounts: ' + $mounts)
    }
    $tmpfsPath = if ($InternalPort -eq 5432) { '/var/lib/postgresql/data' } else { '/data' }
    if ($null -eq $item.HostConfig.Tmpfs.PSObject.Properties[$tmpfsPath]) { throw 'Test data path is not backed by tmpfs.' }
    if ($bindings.Count -ne 1 -or $bindings[0].HostIp -ne '127.0.0.1' -or [int]$bindings[0].HostPort -in @(5432,5433,5434,6379,6380)) {
        throw ('Unexpected test port binding: ' + ($item.NetworkSettings.Ports | ConvertTo-Json -Compress))
    }
    return [int]$bindings[0].HostPort
}

$existingIds = @(Invoke-TestDocker @('ps', '-aq'))
$before = Get-ExistingSnapshot $existingIds
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$network = 'aicos-staff-operations-' + $suffix
$pgName = $network + '-postgres'
$redisName = $network + '-redis'
$pgId = $null; $redisId = $null; $networkId = $null
$variables = @('POSTGRES_PASSWORD','AICOS_OPERATIONS_TEST_CLUSTER','AICOS_OPERATIONS_ORDERING_CONNECTION','AICOS_OPERATIONS_KITCHEN_CONNECTION','AICOS_OPERATIONS_REDIS','AICOS_OPERATIONS_POSTGRES_ID','AICOS_OPERATIONS_REDIS_ID','AICOS_OPERATIONS_TEST_NETWORK','AICOS_OPERATIONS_POSTGRES_PORT','AICOS_OPERATIONS_REDIS_PORT','AICOS_OPERATIONS_CRASH_PROBE')
$prior = @{}; foreach ($name in $variables) { $prior[$name] = [Environment]::GetEnvironmentVariable($name) }
$exit = 1
try {
    # Docker Desktop does not publish loopback ports on an --internal network here.
    # A dedicated bridge with no existing containers plus exact endpoint/cluster guards isolates this Windows-hosted test runner.
    $networkId = Invoke-TestDocker @('network', 'create', '--label', 'aicos.purpose=staff-operations-test', $network)
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $env:POSTGRES_PASSWORD = [Convert]::ToBase64String($bytes)
    $pgId = Invoke-TestDocker @('run','-d','--name',$pgName,'--label','aicos.purpose=staff-operations-test','--network',$network,'--tmpfs','/var/lib/postgresql/data:rw','--publish','127.0.0.1::5432','--env','POSTGRES_DB=aicos_operations_ordering_test','--env','POSTGRES_USER=aicos_operations_test','--env','POSTGRES_PASSWORD','postgres:16-alpine')
    # Allocate a random loopback port explicitly so a Docker restart retains the same mapping.
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { $stableRedisPort = $listener.LocalEndpoint.Port } finally { $listener.Stop() }
    $redisBinding = '127.0.0.1:' + $stableRedisPort + ':6379'
    $redisId = Invoke-TestDocker @('run','-d','--name',$redisName,'--label','aicos.purpose=staff-operations-test','--network',$network,'--tmpfs','/data:rw','--publish',$redisBinding,'redis:7-alpine','redis-server','--appendonly','no')
    $pgPort = Assert-TestContainer $pgId $network 5432
    $redisPort = Assert-TestContainer $redisId $network 6379
    $ready = $false
    for ($i = 0; $i -lt 30; $i++) {
        # The image's temporary init server accepts Unix sockets before POSTGRES_DB exists.
        # TCP is enabled only by the final server, after bootstrap completes.
        & docker exec $pgId pg_isready -h 127.0.0.1 -U aicos_operations_test -d aicos_operations_ordering_test 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Disposable PostgreSQL did not become ready.' }
    $cluster = Invoke-TestDocker @('exec',$pgId,'psql','-U','aicos_operations_test','-d','aicos_operations_ordering_test','-X','-A','-t','-c','SELECT system_identifier::text FROM pg_control_system();')
    if ($cluster -notmatch '^\d+$') { throw 'Cluster identity query failed.' }
    # Allowed only after disposable identity/network/port guard. No production/Gate D/Windows database target is accepted.
    Invoke-TestDocker @('exec',$pgId,'psql','-U','aicos_operations_test','-d','aicos_operations_ordering_test','-X','-v','ON_ERROR_STOP=1','-c','CREATE DATABASE aicos_operations_kitchen_test;') | Out-Null
    $env:AICOS_OPERATIONS_TEST_CLUSTER = $cluster.Trim()
    $env:AICOS_OPERATIONS_TEST_NETWORK = $network
    $env:AICOS_OPERATIONS_POSTGRES_ID = $pgId
    $env:AICOS_OPERATIONS_REDIS_ID = $redisId
    $env:AICOS_OPERATIONS_POSTGRES_PORT = [string]$pgPort
    $env:AICOS_OPERATIONS_REDIS_PORT = [string]$redisPort
    $base = 'Host=127.0.0.1;Port=' + $pgPort + ';Username=aicos_operations_test;Password=' + $env:POSTGRES_PASSWORD + ';Include Error Detail=false;Database='
    $env:AICOS_OPERATIONS_ORDERING_CONNECTION = $base + 'aicos_operations_ordering_test'
    $env:AICOS_OPERATIONS_KITCHEN_CONNECTION = $base + 'aicos_operations_kitchen_test'
    $env:AICOS_OPERATIONS_REDIS = '127.0.0.1:' + $redisPort + ',abortConnect=false,connectTimeout=3000,asyncTimeout=3000'
    Write-Output ('Guard PASS: PostgreSQL ' + $pgId + '; cluster ' + $cluster + '; Redis ' + $redisId + '; dedicated network ' + $network + '; tmpfs; random loopback ports ' + $pgPort + '/' + $redisPort)
    $results = Join-Path $env:TEMP $network
    Push-Location $repository
    try {
        $testFilter = 'FullyQualifiedName~StaffOperationsInfrastructureTests'
        if ($Cups) { $testFilter = 'FullyQualifiedName~CupServingInfrastructureTests' }
        if ($CupAcl) { $testFilter = 'FullyQualifiedName~CupServingAclInfrastructureTests' }
        if ($Recovery -or $Cups) {
            dotnet build tests/AICOS.AdmissionCrashProbe/AICOS.AdmissionCrashProbe.csproj --no-restore --verbosity quiet
            if ($LASTEXITCODE -ne 0) { throw 'Crash probe build failed.' }
            $env:AICOS_OPERATIONS_CRASH_PROBE = Join-Path $repository 'tests/AICOS.AdmissionCrashProbe/bin/Debug/net8.0/AICOS.AdmissionCrashProbe.dll'
            if ($Recovery) { $testFilter = 'FullyQualifiedName~KitchenAdmissionRecoveryTests' }
        }
        dotnet test tests/AICOS.Services.IntegrationTests/AICOS.Services.IntegrationTests.csproj --no-restore --filter $testFilter --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=staff-operations.trx' --results-directory $results
        $exit = $LASTEXITCODE
    } finally { Pop-Location }
    $after = Get-ExistingSnapshot $existingIds
    if ($before -ne $after) { throw 'Existing container ID/image/start/status/restart snapshot changed.' }
    Write-Output 'Existing container snapshots unchanged.'
    $evidence = @("# Disposable staff operations infrastructure run", "", ('UTC: ' + [DateTime]::UtcNow.ToString('o')), ('PostgreSQL container: ' + $pgId), ('Redis container: ' + $redisId), ('Cluster: ' + $cluster), ('Network: ' + $network), ('Loopback ports: ' + $pgPort + '/' + $redisPort), 'Databases: aicos_operations_ordering_test, aicos_operations_kitchen_test', 'Storage: tmpfs; no production credentials, mounts or volumes', 'Existing containers: ID/image/start/status/restart snapshot unchanged', ('Test exit code: ' + $exit), ('TRX evidence (local temp): ' + (Join-Path $results 'staff-operations.trx')))
    if ($Recovery) { $evidence += 'Recovery mode: EF migrations applied only to the guarded disposable databases; machine credentials random per test, no deployment migrations.' }
    if ($CupAcl) { $evidence += 'ACL mode: two fresh non-owner runtime roles and random in-memory credentials in this disposable cluster only; no operating credentials.' }
    $reportName = if ($CupAcl) { 'docs/CUP_SERVING_ACL_RUN.md' } elseif ($Cups) { 'docs/CUP_SERVING_INFRASTRUCTURE_RUN.md' } elseif ($Recovery) { 'docs/KITCHEN_ADMISSION_RECOVERY_RUN.md' } else { 'docs/STAFF_OPERATIONS_INFRASTRUCTURE_RUN.md' }
    [IO.File]::WriteAllLines((Join-Path $repository $reportName), $evidence, (New-Object Text.UTF8Encoding($false)))
    if ($exit -ne 0) { throw 'Operational infrastructure tests failed; inspect test output. No main database was used.' }
} finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $prior[$name]) }
    foreach ($id in @($redisId, $pgId)) {
        if (-not $id) { continue }
        $item = Invoke-TestDocker @('inspect', $id) | ConvertFrom-Json
        if ($item.Id -ne $id -or $item.Config.Labels.'aicos.purpose' -ne 'staff-operations-test') { throw 'Cleanup identity guard failed; no removal attempted.' }
        Invoke-TestDocker @('rm','-f',$id) | Out-Null
    }
    if ($networkId) {
        $item = Invoke-TestDocker @('network','inspect',$networkId) | ConvertFrom-Json
        if ($item.Id -eq $networkId -and $item.Labels.'aicos.purpose' -eq 'staff-operations-test' -and @($item.Containers.PSObject.Properties).Count -eq 0) { Invoke-TestDocker @('network','rm',$networkId) | Out-Null }
    }
    Write-Output 'Cleaned up only this run disposable containers/network and RAM fixtures; no Docker volumes removed.'
}
