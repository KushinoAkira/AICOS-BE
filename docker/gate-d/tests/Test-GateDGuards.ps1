$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../GateD.Common.psm1') -Force
$passed=0
function Expect-Rejected([string]$Name,[scriptblock]$Action) {
    $rejected=$false
    try {& $Action}catch{$rejected=$true}
    if(-not $rejected){throw ('Guard accepted unsafe case: '+$Name)}
    $script:passed++
    Write-Output ('PASS reject '+$Name)
}
$goodId='1234567890123456789'
$goodCs='Host=127.0.0.1;Port=15432;Database=aicos_payment_test;Username=postgres'
Assert-GateDTarget $goodCs $goodId $goodId
$passed++
foreach($case in @(
 @{Name='main database';Value=$goodCs.Replace('Database=aicos_payment_test','Database=aicos_db')},
 @{Name='Windows old test port';Value=$goodCs.Replace('Port=15432','Port=5432')},
 @{Name='localhost alias';Value=$goodCs.Replace('127.0.0.1','localhost')},
 @{Name='host.docker.internal';Value=$goodCs.Replace('127.0.0.1','host.docker.internal')},
 @{Name='multi host';Value=$goodCs.Replace('127.0.0.1','127.0.0.1,other-host')},
 @{Name='libpq service override';Value=($goodCs+';Service=main')},
 @{Name='implicit port';Value=$goodCs.Replace('Port=15432;','')},
 @{Name='unapproved user';Value=$goodCs.Replace('Username=postgres','Username=unknown')},
 @{Name='connection alias bypass';Value=($goodCs+';Server=localhost')}
)){
    Expect-Rejected $case.Name {Assert-GateDTarget $case.Value $goodId $goodId}
}
Expect-Rejected 'different cluster' {Assert-GateDTarget $goodCs '9999999999999999999' $goodId}
Expect-Rejected 'missing trusted cluster' {Assert-GateDTarget $goodCs $goodId 'invalid'}
# Synthetic Docker metadata only; no Docker/SQL/HTTP business call is made.
function New-Metadata {
 $m=@{Project='aicos-gate-d';Database='aicos_payment_test';NetworkId='network-test';Postgres=@{Id='pg-test';Image='pg-image'};Redis=@{Id='redis-test';Image='redis-image'}}
 $state=@{
  Network=@{Name='aicos-gate-d_internal';Id='network-test';Internal=$true;Labels=@{'com.docker.compose.project'='aicos-gate-d'};Containers=@{'pg-test'=@{};'redis-test'=@{}}}
  Volumes=@(@{Name='aicos-gate-d_pg18-data';Labels=@{'com.docker.compose.project'='aicos-gate-d'}},@{Name='aicos-gate-d_redis-data';Labels=@{'com.docker.compose.project'='aicos-gate-d'}})
 }
 foreach($spec in @(@{Key='Postgres';Id='pg-test';Image='pg-image';Name='/aicos-gate-d-postgres';Service='gated-postgres';Volume='aicos-gate-d_pg18-data';Mount='/var/lib/postgresql'},@{Key='Redis';Id='redis-test';Image='redis-image';Name='/aicos-gate-d-redis';Service='gated-redis';Volume='aicos-gate-d_redis-data';Mount='/data'})){
  $state[$spec.Key]=@{Id=$spec.Id;Image=$spec.Image;Name=$spec.Name;State=@{Running=$true};Config=@{Labels=@{'com.docker.compose.project'='aicos-gate-d';'com.docker.compose.service'=$spec.Service;'com.aicos.environment'='isolated-test'}};NetworkSettings=@{Networks=@{'aicos-gate-d_internal'=@{}}};Mounts=@(@{Type='volume';Name=$spec.Volume;Destination=$spec.Mount});HostConfig=@{PortBindings=@{}}}
 }

 @{Manifest=($m|ConvertTo-Json -Depth 12|ConvertFrom-Json);State=($state|ConvertTo-Json -Depth 12|ConvertFrom-Json)}
}
$valid=New-Metadata
Assert-GateDResourceIdentity $valid.Manifest $valid.State
$passed++
foreach($case in @(
 @{Name='external network';Mutate={param($x) $x.State.Network.Internal=$false}},
 @{Name='Gate C volume';Mutate={param($x) $x.State.Postgres.Mounts[0].Name='aicos-be_postgres-data'}},
 @{Name='container replaced';Mutate={param($x) $x.State.Postgres.Id='wrong-id'}},
 @{Name='image changed';Mutate={param($x) $x.State.Postgres.Image='wrong-image'}},
 @{Name='wrong project';Mutate={param($x) $x.State.Redis.Config.Labels.'com.docker.compose.project'='aicos-be'}},
 @{Name='public PG port';Mutate={param($x) $x.State.Postgres.HostConfig.PortBindings|Add-Member NoteProperty '5432/tcp' @(@{HostIp='0.0.0.0';HostPort='15432'})}},
 @{Name='wrong published port';Mutate={param($x) $x.State.Postgres.HostConfig.PortBindings|Add-Member NoteProperty '5432/tcp' @(@{HostIp='127.0.0.1';HostPort='5432'})}},
 @{Name='Redis published';Mutate={param($x) $x.State.Redis.HostConfig.PortBindings|Add-Member NoteProperty '6379/tcp' @(@{HostIp='127.0.0.1';HostPort='6379'})}},
 @{Name='cross-network';Mutate={param($x) $x.State.Redis.NetworkSettings.Networks|Add-Member NoteProperty 'aicos-be_default' @{}}},
 @{Name='foreign network member';Mutate={param($x) $x.State.Network.Containers|Add-Member NoteProperty 'foreign-service' @{}}},
 @{Name='wrong volume owner';Mutate={param($x) $x.State.Volumes[0].Labels.'com.docker.compose.project'='aicos-be'}}
)){
 $item=New-Metadata
 & $case.Mutate $item
 Expect-Rejected $case.Name {Assert-GateDResourceIdentity $item.Manifest $item.State}
}
Write-Output ('PASS guard metadata tests='+$passed+'; no database/business/token tests executed')

$pinned='a'*64
$internalCs='Host=gated-postgres;Port=5432;Database=aicos_payment_test;Username=postgres'
Assert-GateDTarget $internalCs $goodId $goodId -Transport DockerExec -ContainerId $pinned -ExpectedContainerId $pinned
$passed++
Expect-Rejected 'unpinned Docker container' {Assert-GateDTarget $internalCs $goodId $goodId -Transport DockerExec -ContainerId ('b'*64) -ExpectedContainerId $pinned}
Expect-Rejected 'Windows host in Docker transport' {Assert-GateDTarget ($internalCs.Replace('gated-postgres','host.docker.internal')) $goodId $goodId -Transport DockerExec -ContainerId $pinned -ExpectedContainerId $pinned}
Expect-Rejected 'implicit Docker container ID' {Assert-GateDTarget $internalCs $goodId $goodId -Transport DockerExec}
Write-Output ('FINAL_PASS guard metadata tests='+$passed+'; DockerExec pinning verified; no business tests')

