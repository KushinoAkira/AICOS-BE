[CmdletBinding()]
param([switch]$Run)
$ErrorActionPreference='Stop'
$repository=Split-Path $PSScriptRoot -Parent
if(-not$Run){Write-Output 'Plan: new PostgreSQL 16 scratch container/network, RAM-only data, random loopback port; aicos_billflow_test only. Test EF migrations run only after cluster guard. No main/Gate D/Windows DB access. Add -Run to execute.';return}
$engine=docker info --format '{{.ServerVersion}}' 2>$null
if($LASTEXITCODE-ne0){throw 'Docker Engine unavailable'}
$suffix=[Guid]::NewGuid().ToString('N').Substring(0,10)
$network='aicos-billflow-test-'+$suffix
$container='aicos-billflow-postgres-'+$suffix
$containerId=$null;$networkId=$null
$priorPassword=$env:POSTGRES_PASSWORD;$priorConnection=$env:AICOS_BILL_TEST_CONNECTION;$priorCluster=$env:AICOS_BILL_TEST_CLUSTER
try {
    $networkId=docker network create --label aicos.purpose=billflow-test $network
    if($LASTEXITCODE-ne0){throw 'Scratch network creation failed'}
    $bytes=New-Object byte[] 32;[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $env:POSTGRES_PASSWORD=[Convert]::ToBase64String($bytes)
    $containerId=docker run -d --name $container --label aicos.purpose=billflow-test --network $network --tmpfs /var/lib/postgresql/data:rw --publish '127.0.0.1::5432' --env POSTGRES_DB=aicos_billflow_test --env POSTGRES_USER=aicos_test --env POSTGRES_PASSWORD postgres:16-alpine
    if($LASTEXITCODE-ne0){throw 'Scratch PostgreSQL start failed'}
    $inspection=docker inspect $container|ConvertFrom-Json
    if($inspection.Id-ne$containerId-or$inspection.Config.Labels.'aicos.purpose'-ne'billflow-test'-or$inspection.NetworkSettings.Networks.PSObject.Properties.Name-ne$network){throw 'Scratch identity guard failed'}
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
        docker exec $container pg_isready -h 127.0.0.1 -U aicos_test -d aicos_billflow_test 2>$null|Out-Null
        if($LASTEXITCODE-eq0){$ready=$true;break};Start-Sleep -Seconds 1
    }
    if(-not$ready){throw 'Scratch PostgreSQL not ready'}
    $cluster=$null
    for($identityProbe=0;$identityProbe-lt25;$identityProbe++){
        $candidate=docker exec $container psql -h 127.0.0.1 -U aicos_test -d aicos_billflow_test -X -A -t -c 'SELECT system_identifier::text FROM pg_control_system();' 2>$null
        if($LASTEXITCODE-eq0-and$candidate-match'^\d+$'){$cluster=$candidate.Trim();break}
        Start-Sleep -Seconds 1
    }
    if(-not$cluster){throw 'Cluster identity probe failed'}
    $env:AICOS_BILL_TEST_CLUSTER=$cluster.Trim()
    $env:AICOS_BILL_TEST_CONNECTION='Host=127.0.0.1;Port='+$port+';Database=aicos_billflow_test;Username=aicos_test;Password='+$env:POSTGRES_PASSWORD+';Include Error Detail=false'
    Write-Output ('Guard PASS: '+$container+'; random loopback port '+$port+'; database aicos_billflow_test; cluster '+$cluster+'; private network, tmpfs. No main credentials.')
    Push-Location $repository
    try {
        $filters=@(
            'FullyQualifiedName~SessionBillPostgresTests.Additive_migrations_can_rollback_empty_new_tables_without_losing_legacy_session',
            'FullyQualifiedName~SessionBillPostgresTests&FullyQualifiedName!~Additive_migrations_can_rollback_empty_new_tables_without_losing_legacy_session',
            'FullyQualifiedName~SessionBillProjectionPostgresTests'
        )
        $stage=0
        foreach($filter in $filters){
            $stage++
            $outputPath=Join-Path $env:TEMP ('aicos-session-bill-'+$suffix+'-'+$stage+'.log')
            $beforeNative=$ErrorActionPreference
            $ErrorActionPreference='Continue'
            try {
                dotnet test tests/AICOS.Services.IntegrationTests/AICOS.Services.IntegrationTests.csproj --filter $filter --verbosity normal > $outputPath 2>&1
                $testExit=$LASTEXITCODE
            } finally { $ErrorActionPreference=$beforeNative }
            Get-Content -LiteralPath $outputPath -Tail 25
            if($testExit-ne0){throw ('Session Bill PostgreSQL tests failed. Details: '+$outputPath)}
        }
    } finally { Pop-Location }
} finally {
    $env:POSTGRES_PASSWORD=$priorPassword;$env:AICOS_BILL_TEST_CONNECTION=$priorConnection;$env:AICOS_BILL_TEST_CLUSTER=$priorCluster
    if($containerId){
        $actual=docker inspect $container 2>$null|ConvertFrom-Json
        if($actual.Id-eq$containerId-and$actual.Config.Labels.'aicos.purpose'-eq'billflow-test'){
            docker rm -f $container|Out-Null
            Write-Output 'Removed only this run scratch container and RAM test data.'
        }
    }
    if($networkId){
        $actualNetwork=docker network inspect $network 2>$null|ConvertFrom-Json
        if($actualNetwork.Id-eq$networkId-and$actualNetwork.Labels.'aicos.purpose'-eq'billflow-test'-and@($actualNetwork.Containers.PSObject.Properties).Count-eq0){docker network rm $network|Out-Null}
    }
}