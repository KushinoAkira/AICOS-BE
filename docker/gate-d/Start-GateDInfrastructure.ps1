param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'GateD.Common.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Set-Location $repo
$runDir=Join-Path $env:LOCALAPPDATA ('AICOS\gate-d\d1a_'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
if(Test-Path $runDir){throw 'Evidence path conflict'}
$null=New-Item -ItemType Directory -Path $runDir
Protect-GateDPrivatePath $runDir
$result=[ordered]@{Status='Preparing';Stage='Preflight';RunDirectory=$runDir;ResourcesCreated=$false;MainDatabaseConnected=$false;OldHostTestDatabaseConnected=$false;MigrationOrSeedRun=$false;BusinessTestsRun=$false;ApplicationAccountsCreated=$false;JwtTokensCreated=$false;ReservedSigningKeyOnly=$false;ProtectedUnchanged=$false;Readiness=$null;ErrorType=$null;ErrorLine=$null}
$secretCache=@{};$before=$null;$manifest=$null
$previousPassword=[Environment]::GetEnvironmentVariable('AICOS_GATED_POSTGRES_PASSWORD','Process')
try {
    $null=Invoke-GateDDocker @('info','--format','{{.ServerVersion}}')
    $names=@((Invoke-GateDDocker @('ps','-a','--format','{{.Names}}')) -split '\r?\n')
    if(@($names|Where-Object {$_ -in @('aicos-gate-d-postgres','aicos-gate-d-redis')}).Count){throw 'Test container name conflict; stop without reuse'}
    if(([string](Invoke-GateDDocker @('ps','-aq','--filter','label=com.docker.compose.project=aicos-gate-d'))).Trim()){throw 'Compose project conflict'}
    $nets=@((Invoke-GateDDocker @('network','ls','--format','{{.Name}}')) -split '\r?\n')
    $vols=@((Invoke-GateDDocker @('volume','ls','--format','{{.Name}}')) -split '\r?\n')
    if('aicos-gate-d_internal' -in $nets -or 'aicos-gate-d_pg18-data' -in $vols -or 'aicos-gate-d_redis-data' -in $vols){throw 'Test network/volume conflict; stop without reuse'}
    $before=Get-GateDProtectedSnapshot
    Get-GateDPublicSnapshot $before|ConvertTo-Json -Depth 6|Set-Content (Join-Path $runDir 'gate-c-before.json') -Encoding UTF8
    foreach($name in $before.Keys){
        if($name -notin @('ai','gateway','redis') -and $before[$name].Environment['Database__MigrateOnStartup'] -ne 'false'){throw 'Protected migration flag is not false'}
    }
    if($before['payment'].Environment['Payments__MutationsEnabled'] -ne 'false'){throw 'Protected Payment writes enabled'}
    $secretCache.PostgresAdminPassword=New-GateDSecret
    $secretCache.ReservedJwtSigningKey=New-GateDSecret
    $plain=Get-GateDSecretText $secretCache.PostgresAdminPassword
    $jwt=Get-GateDSecretText $secretCache.ReservedJwtSigningKey
    foreach($item in $before.Values){
        foreach($entry in $item.Environment.Values){
            if(([string]$entry).Contains($plain) -or ([string]$entry).Contains($jwt)){throw 'Generated test secret unexpectedly matches protected environment'}
        }
    }
    if($plain -ceq $jwt){throw 'Test secrets must differ'}
    $env:AICOS_GATED_POSTGRES_PASSWORD=$plain
    $plain=$null;$jwt=$null;$result.ReservedSigningKeyOnly=$true
    $credentialPath=Join-Path $runDir 'infra-secrets.dpapi.xml'
    $null=New-Item -ItemType File -Path $credentialPath
    Protect-GateDPrivatePath $credentialPath
    $secretCache|Export-Clixml -LiteralPath $credentialPath
    $compose=@('compose','-p','aicos-gate-d','-f','docker-compose.gate-d.yml')
    $result.Stage='Validate standalone infrastructure config'
    $null=Invoke-GateDDocker ($compose+@('config','--quiet'))
    $cfg=Invoke-GateDDocker ($compose+@('config','--format','json'))|ConvertFrom-Json
    if($cfg.name -cne 'aicos-gate-d' -or @($cfg.services.PSObject.Properties.Name).Count -ne 2){throw 'Infrastructure-only project required'}
    if($cfg.services.'gated-postgres'.environment.POSTGRES_DB -cne 'aicos_payment_test' -or
       $cfg.services.'gated-postgres'.environment.POSTGRES_USER -cne 'postgres' -or
       $cfg.networks.'gated-internal'.internal -ne $true){throw 'Unexpected test PostgreSQL/network config'}
    foreach($service in @('gated-postgres','gated-redis')){
        $s=$cfg.services.$service
        if(@($s.volumes).Count -ne 1 -or $s.volumes[0].type -cne 'volume'){throw 'No bind/init/source mount allowed'}
        $null=Invoke-GateDDocker @('image','inspect','--format','{{.Id}}',$s.image)
    }
    $expectedPgImage=(Invoke-GateDDocker @('image','inspect','--format','{{.Id}}',$cfg.services.'gated-postgres'.image)).Trim()
    $expectedRedisImage=(Invoke-GateDDocker @('image','inspect','--format','{{.Id}}',$cfg.services.'gated-redis'.image)).Trim()
    $cfg=$null
    $result.Stage='Start PostgreSQL18 and Redis test ONLY'
    $null=Invoke-GateDDocker ($compose+@('up','-d','--no-deps','--no-build','--pull','never','gated-postgres','gated-redis'))
    $result.ResourcesCreated=$true
    $until=[DateTime]::UtcNow.AddSeconds(90)
    do {
        $pg=@((Invoke-GateDDocker @('inspect','aicos-gate-d-postgres')|ConvertFrom-Json))[0]
        $redis=@((Invoke-GateDDocker @('inspect','aicos-gate-d-redis')|ConvertFrom-Json))[0]
        if($pg.State.Health.Status -eq 'healthy' -and $redis.State.Health.Status -eq 'healthy'){break}
        Start-Sleep -Seconds 2
    }while([DateTime]::UtcNow -lt $until)
    if($pg.State.Health.Status -ne 'healthy' -or $redis.State.Health.Status -ne 'healthy'){throw 'Test infrastructure health timeout'}
    if($pg.Image -cne $expectedPgImage -or $redis.Image -cne $expectedRedisImage){throw 'Test image changed'}
    $network=@((Invoke-GateDDocker @('network','inspect','aicos-gate-d_internal')|ConvertFrom-Json))[0]
    $control=Invoke-GateDDocker @('exec','aicos-gate-d-postgres','pg_controldata','/var/lib/postgresql/18/docker')
    $id=[regex]::Match($control,'Database system identifier:\s*(\d+)')
    if(-not $id.Success){throw 'Test cluster identity not available'}
    $manifest=[ordered]@{
        Version=1;Stage='D1-A';Project='aicos-gate-d';Database='aicos_payment_test';Host='gated-postgres';Port=5432;Transport='DockerExecPinned';User='postgres'
        SystemIdentifier=$id.Groups[1].Value;NetworkId=$network.Id
        Postgres=@{Id=$pg.Id;Image=$pg.Image};Redis=@{Id=$redis.Id;Image=$redis.Image}
        ProtectedContainers=(Get-GateDPublicSnapshot $before);SchemaApplied=$false;RuntimeRolesCreated=$false;FixturesCreated=$false;BusinessExecutionAuthorized=$false
    }
    $manifest|ConvertTo-Json -Depth 8|Set-Content (Join-Path $runDir 'manifest.json') -Encoding UTF8
    $result.Stage='Guard and real READ ONLY test readiness'
    $result.Readiness=Invoke-GateDReadOnlyProbe ([pscustomobject]$manifest) $secretCache.PostgresAdminPassword
    Assert-GateDProtectedUnchanged -Before $before -CompareEnvironment
    $result.ProtectedUnchanged=$true
    $manifest|ConvertTo-Json -Depth 8|Set-Content (Join-Path $runDir 'manifest.json') -Encoding UTF8
    $result.Status='PASS_INFRA_ONLY';$result.Stage='STOP D1-A; schema/roles/fixtures/apps await D1-B approval'
} catch {
    $result.Status='FAIL_STOP';$result.ErrorType=$_.Exception.GetType().FullName;$result.ErrorLine=$_.InvocationInfo.ScriptLineNumber
} finally {
    $result|ConvertTo-Json -Depth 8|Set-Content (Join-Path $runDir 'd1a-result.json') -Encoding UTF8
    [Environment]::SetEnvironmentVariable('AICOS_GATED_POSTGRES_PASSWORD',$previousPassword,'Process')
    foreach($value in $secretCache.Values){$value.Dispose()}
    $secretCache=@{};$plain=$null;$jwt=$null;$before=$null;$cfg=$null
    $result|ConvertTo-Json -Depth 8
}
if($result.Status -ne 'PASS_INFRA_ONLY'){exit 1}

