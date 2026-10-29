param([Parameter(Mandatory)][string]$ReferenceDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../GateD.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'GateD.Identity.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
Set-Location $repo
$privateRoot=Join-Path $env:LOCALAPPDATA 'AICOS/gate-d'
$reference=(Resolve-Path -LiteralPath $ReferenceDirectory).Path
if(-not $reference.StartsWith($privateRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Split-Path $reference -Leaf) -notmatch '^d1c_readonly_\d{8}_\d{6}$'){throw 'Use the approved private D1-C read-only observation'}
$previous=Get-Content (Join-Path $reference 'result.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($previous.Status -cne 'PASS_READONLY_IDENTITY_READINESS' -or $previous.SystemIdentifier -cne '7701931530273472557'){throw 'Unverified reference observation'}
$originDirectory=Join-Path $privateRoot 'd1a_20261029_104735'
$origin=Get-Content (Join-Path $originDirectory 'manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
$before=Get-GateDProtectedSnapshot
Assert-GateDProtectedUnchanged (Get-Content (Join-Path $reference 'gate-c-after.json') -Raw -Encoding UTF8|ConvertFrom-Json)
$state=Get-GateDResourceState
$state.Apps=@{}
$manifest=@{Version=1;Stage='D1-C';Preparation='C1';Project='aicos-gate-d';Database='aicos_payment_test';SystemIdentifier='7701931530273472557';NetworkId=$origin.NetworkId;Postgres=$origin.Postgres;Redis=$origin.Redis;Apps=@{};BusinessExecutionAuthorized=$false;CredentialsCreated=$false;FixturesCreated=$false;RuntimeStarted=$false;ReferenceObservation=$reference;ProtectedContainers=(Get-GateDPublicSnapshot $before)}
Assert-GateDIdentityTopology $manifest $state -Mode InfraOnly
$names=@((Invoke-GateDDocker @('ps','-a','--format','{{.Names}}')) -split '\r?\n')
if(@($names|Where-Object {$_ -in @('aicos-gate-d-identity','aicos-gate-d-gateway','aicos-gate-d-runner')}).Count){throw 'Unexpected existing test app; no reuse/recreate'}
$control=Invoke-GateDDocker @('exec',$manifest.Postgres.Id,'pg_controldata','/var/lib/postgresql/18/docker')
$systemId=[regex]::Match($control,'Database system identifier:\s*(\d+)').Groups[1].Value
Assert-GateDTarget 'Host=gated-postgres;Port=5432;Database=aicos_payment_test;Username=postgres' $systemId $manifest.SystemIdentifier -Transport DockerExec -ContainerId $state.Postgres.Id -ExpectedContainerId $manifest.Postgres.Id
$compose=@('compose','--env-file','docker/gate-d/identity/config-only.env.example','-p','aicos-gate-d','-f','docker-compose.gate-d.identity.yml','--profile','d1c-runtime')
$null=Invoke-GateDDocker ($compose+@('config','--no-interpolate','--quiet'))
$configuration=Invoke-GateDDocker ($compose+@('config','--no-interpolate','--format','json'))|ConvertFrom-Json
$gatewayPath=Join-Path $PSScriptRoot 'gateway.identity.json'
Assert-GateDIdentityCompose $configuration $gatewayPath
Assert-GateDIdentityGateway (Get-Content $gatewayPath -Raw -Encoding UTF8|ConvertFrom-Json)
$imagePins=@{}
foreach($name in @('identity','gateway')){
    $image=$before[$name].Image
    if($image -notmatch '^sha256:[a-f0-9]{64}$' -or (Invoke-GateDDocker @('image','inspect','--format','{{.Id}}',$image)).Trim() -cne $image){throw 'Pinned local app image missing; no pull/build'}
    $imagePins[$name]=$image
}
# No password/key cache is imported. Only existing test-container env supplies
# the bootstrap password inside the isolated process; no secret in argv/output.
$sql=Get-Content (Join-Path $PSScriptRoot 'readiness.sql') -Raw -Encoding UTF8
$shell=@'
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
export PGAPPNAME=aicos-gated-c1-readonly
export PGCONNECT_TIMEOUT=5
export PGOPTIONS='-c default_transaction_read_only=on -c statement_timeout=10000 -c lock_timeout=2000'
exec psql -X -w -qAt -h gated-postgres -p 5432 -U postgres -d aicos_payment_test -v ON_ERROR_STOP=1 -f - <<'C1_READONLY_SQL'
'@
$output=Invoke-GateDCommand -FilePath ((Get-Command docker.exe).Source) -Arguments @('exec','-i',$manifest.Postgres.Id,'sh','-s') -InputText ($shell+"`n"+$sql+"`nC1_READONLY_SQL`n")
$live=$output|ConvertFrom-Json
if($live.Database -cne $manifest.Database -or $live.Schema -cne 'public' -or $live.ReadOnly -cne 'on' -or $live.SystemIdentifier -cne $manifest.SystemIdentifier -or $live.Tables -ne 38 -or $live.Columns -ne 244 -or $live.IdentityTables -ne 7 -or $live.IdentityRows -ne 0 -or $live.IdentityAclErrors -ne 0){throw 'Identity read-only prerequisite mismatch'}
Assert-GateDIdentityRoles $live.Roles -Mode BeforeCredentials
if((@($live.UpdateColumns|Sort-Object) -join ',') -cne 'replaced_by_session_id,revoked_at_utc,version'){throw 'Unexpected Identity column ACL'}
$cachePath=Join-Path $originDirectory 'infra-secrets.dpapi.xml'
if(-not(Test-Path -LiteralPath $cachePath -PathType Leaf) -or -not (Get-Acl -LiteralPath $cachePath).AreAccessRulesProtected){throw 'Existing DPAPI reservation absent/unprotected; no new secret generation'}
$pong=(Invoke-GateDDocker @('exec',$manifest.Redis.Id,'redis-cli','PING')).Trim()
if($pong -cne 'PONG'){throw 'Test Redis not ready'}
Assert-GateDProtectedUnchanged $before -CompareEnvironment
$run=Join-Path $privateRoot ('d1c_c1_'+[DateTime]::UtcNow.ToString('yyyyMMdd_HHmmss'))
$null=New-Item -ItemType Directory -Path $run
Protect-GateDPrivatePath $run
$manifest['ImagePins']=$imagePins
$manifest['ReservedKeyCache']=$cachePath
$hashes=@{}
foreach($relative in @('docker-compose.gate-d.identity.yml','docker/gate-d/identity/gateway.identity.json','docker/gate-d/identity/GateD.Identity.psm1','docker/gate-d/identity/readiness.sql','docker/gate-d/identity/config-only.env.example')){$hashes[$relative]=(Get-FileHash (Join-Path $repo $relative) -Algorithm SHA256).Hash}
$manifest['ArtifactHashes']=$hashes
$manifest|ConvertTo-Json -Depth 10|Set-Content (Join-Path $run 'manifest.json') -Encoding UTF8
$live|ConvertTo-Json -Depth 8|Set-Content (Join-Path $run 'readonly-prerequisites.json') -Encoding UTF8
$configuration|ConvertTo-Json -Depth 12|Set-Content (Join-Path $run 'compose-template-no-interpolate.json') -Encoding UTF8
Get-GateDPublicSnapshot (Get-GateDProtectedSnapshot)|ConvertTo-Json -Depth 6|Set-Content (Join-Path $run 'gate-c-after.json') -Encoding UTF8
$result=@{Status='PASS_C1_PREPARATION';EvidenceDirectory=$run;Database=$live.Database;SystemIdentifier=$live.SystemIdentifier;Compose='PASS template only';Roles='11 NOLOGIN; password NULL';IdentityRows=0;ReservedKey='Existing cache presence/DACL checked; NOT decrypted/distributed';RuntimeCredentials='NOT CREATED';AppsStarted=0;Fixtures='NOT CREATED';JwtTokens='NOT CREATED';BusinessTests='NOT RUN';GateC='Unchanged versus reference and during C1'}
$result|ConvertTo-Json -Depth 5|Set-Content (Join-Path $run 'result.json') -Encoding UTF8
$result|ConvertTo-Json -Depth 5
