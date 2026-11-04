[CmdletBinding()]
param([switch]$Run)
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
if(-not$Run){Write-Output 'Plan: new PostgreSQL 16 scratch container/network, RAM-only data, random loopback port; aicos_cashflow_test only. Test EF EnsureCreated runs only after cluster guard. No main/Gate D/Windows DB access. Add -Run to execute.';return}
$engine=docker info --format '{{.ServerVersion}}' 2>$null
if($LASTEXITCODE-ne0){throw 'Docker Engine unavailable'}
$suffix=[Guid]::NewGuid().ToString('N').Substring(0,10)
$network='aicos-cashflow-test-'+$suffix
$container='aicos-cashflow-postgres-'+$suffix
$containerId=$null;$networkId=$null
$priorPassword=$env:POSTGRES_PASSWORD;$priorConnection=$env:AICOS_CASH_TEST_CONNECTION;$priorCluster=$env:AICOS_CASH_TEST_CLUSTER
try {
    $networkId=docker network create --label aicos.purpose=cashflow-test $network
    if($LASTEXITCODE-ne0){throw 'Scratch network creation failed'}
    $bytes=New-Object byte[] 32;[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $env:POSTGRES_PASSWORD=[Convert]::ToBase64String($bytes)
    $containerId=docker run -d --name $container --label aicos.purpose=cashflow-test --network $network --tmpfs /var/lib/postgresql/data:rw --publish '127.0.0.1::5432' --env POSTGRES_DB=aicos_cashflow_test --env POSTGRES_USER=aicos_test --env POSTGRES_PASSWORD postgres:16-alpine
    if($LASTEXITCODE-ne0){throw 'Scratch PostgreSQL start failed'}
    $inspection=docker inspect $container|ConvertFrom-Json
    if($inspection.Id-ne$containerId-or$inspection.Config.Labels.'aicos.purpose'-ne'cashflow-test'-or$inspection.NetworkSettings.Networks.PSObject.Properties.Name-ne$network){throw 'Scratch identity guard failed'}
    $binding=$null
    for($probeIndex=0;$probeIndex-lt20;$probeIndex++){
        $inspection=docker inspect $container|ConvertFrom-Json
        $bindings=@($inspection.NetworkSettings.Ports.'5432/tcp')
        if($bindings.Count-gt0-and$bindings[0].HostPort){$binding=$bindings[0];break}
        Start-Sleep -Milliseconds 250
    }
    $port=[int]$binding.HostPort
    if($binding.HostIp-ne'127.0.0.1'-or$port-in@(5432,5433)){throw 'Unexpected test host binding'}
    if(@($inspection.Mounts|Where-Object Type -eq 'volume').Count){throw 'Test must not mount persistent volumes'}
    $ready=$false
    for($i=0;$i-lt25;$i++){
        docker exec $container pg_isready -U aicos_test -d aicos_cashflow_test 2>$null|Out-Null
        if($LASTEXITCODE-eq0){$ready=$true;break};Start-Sleep -Seconds 1
    }
    if(-not$ready){throw 'Scratch PostgreSQL not ready'}
    $cluster=docker exec $container psql -U aicos_test -d aicos_cashflow_test -X -A -t -c 'SELECT system_identifier::text FROM pg_control_system();'
    if($LASTEXITCODE-ne0-or$cluster-notmatch'^\d+$'){throw 'Cluster identity probe failed'}
    $env:AICOS_CASH_TEST_CLUSTER=$cluster.Trim()
    $env:AICOS_CASH_TEST_CONNECTION='Host=127.0.0.1;Port='+$port+';Database=aicos_cashflow_test;Username=aicos_test;Password='+$env:POSTGRES_PASSWORD+';Include Error Detail=false'
    Write-Output ('Guard PASS: '+$container+'; random loopback port '+$port+'; database aicos_cashflow_test; cluster '+$cluster+'; private network, tmpfs. No main credentials.')
    Push-Location $repository
    try {
        dotnet test tests/AICOS.Services.IntegrationTests/AICOS.Services.IntegrationTests.csproj --filter 'FullyQualifiedName~CashPostgresTests' --verbosity quiet
        if($LASTEXITCODE-ne0){throw 'PostgreSQL cash tests failed; no main database was used'}
    } finally { Pop-Location }
} finally {
    $env:POSTGRES_PASSWORD=$priorPassword;$env:AICOS_CASH_TEST_CONNECTION=$priorConnection;$env:AICOS_CASH_TEST_CLUSTER=$priorCluster
    if($containerId){
        $actual=docker inspect $container 2>$null|ConvertFrom-Json
        if($actual.Id-eq$containerId-and$actual.Config.Labels.'aicos.purpose'-eq'cashflow-test'){
            docker rm -f $container|Out-Null
            Write-Output 'Removed only this run scratch container and RAM test data.'
        }
    }
    if($networkId){
        $actualNetwork=docker network inspect $network 2>$null|ConvertFrom-Json
        if($actualNetwork.Id-eq$networkId-and$actualNetwork.Labels.'aicos.purpose'-eq'cashflow-test'-and@($actualNetwork.Containers.PSObject.Properties).Count-eq0){docker network rm $network|Out-Null}
    }
}