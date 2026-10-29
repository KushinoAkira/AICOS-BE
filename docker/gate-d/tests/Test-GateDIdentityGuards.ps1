$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../GateD.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '../identity/GateD.Identity.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
Set-Location $repo
$passed=0
function Copy-Case($Value){$Value|ConvertTo-Json -Depth 25|ConvertFrom-Json}
function Reject-Case([string]$Name,[scriptblock]$Action){
    $rejected=$false
    try {& $Action}catch{$rejected=$true}
    if(-not $rejected){throw ('Unsafe case accepted: '+$Name)}
    $script:passed++
}
# Synthetic strings are not runtime passwords/keys and are never used to sign JWT.
$connection='Host=gated-postgres;Port=5432;Database=aicos_payment_test;Username=identity_svc;Password=synthetic-test-input;Search Path=public;Maximum Pool Size=6;Minimum Pool Size=0;Timeout=5;Command Timeout=10'
Assert-GateDIdentityConnection $connection;$passed++
foreach($pair in @(
 @('Database=aicos_payment_test','Database=aicos_db'),
 @('Host=gated-postgres','Host=host.docker.internal'),
 @('Host=gated-postgres','Host=localhost'),
 @('Host=gated-postgres','Host=gated-postgres,main'),
 @('Username=identity_svc','Username=postgres'),
 @('Search Path=public','Search Path=identity'),
 @('Maximum Pool Size=6','Maximum Pool Size=100'),
 @('Password=synthetic-test-input','Password='),
 @('Port=5432;',''),
 @('Timeout=5','Timeout=5;Service=main')
)){
    $bad=$connection.Replace($pair[0],$pair[1])
    Reject-Case ('connection '+$pair[1]) {Assert-GateDIdentityConnection $bad}
}
$shared=@{ASPNETCORE_ENVIRONMENT='Testing';ASPNETCORE_HTTP_PORTS='8080';Database__MigrateOnStartup='false';Jwt__Issuer='aicos-gate-d';Jwt__Audience='aicos-gate-d-clients';Jwt__ExpiryMinutes='1';Jwt__SigningKey=('S'*48)}
$identity=$shared.Clone();$gateway=$shared.Clone()
$identity.ConnectionStrings__Default=$connection;$identity.ConnectionStrings__Redis='gated-redis:6379';$identity.RefreshToken__ExpiryDays='1'
Assert-GateDIdentityEnvironment $identity $gateway ('M'*48);$passed++
foreach($case in @(
 @{Key='Database__MigrateOnStartup';Value='true'},
 @{Key='ASPNETCORE_ENVIRONMENT';Value='Development'},
 @{Key='Jwt__Audience';Value='aicos-clients'},
 @{Key='Jwt__SigningKey';Value=('M'*48)},
 @{Key='Jwt__SigningKey';Value='short'},
 @{Key='ConnectionStrings__Redis';Value='redis:6379'},
 @{Key='Services__Menu__BaseUrl';Value='http://menu:8080'}
)){
    $bad=$identity.Clone();$bad[$case.Key]=$case.Value
    Reject-Case ('runtime env '+$case.Key) {Assert-GateDIdentityEnvironment $bad $gateway ('M'*48)}
}
$badGateway=$gateway.Clone();$badGateway['ConnectionStrings__Default']=$connection
Reject-Case 'Gateway database credential' {Assert-GateDIdentityEnvironment $identity $badGateway ('M'*48)}
$reused=$identity.Clone();$reused.ConnectionStrings__Default=$connection.Replace('synthetic-test-input',('S'*48))
Reject-Case 'DB password reused as signing key' {Assert-GateDIdentityEnvironment $reused $gateway ('M'*48)}
$roleNames=@('identity_svc','menu_svc','table_reservation_svc','ordering_svc','payment_svc','inventory_svc','kitchen_svc','workforce_svc','reporting_svc','customer_svc','ai_svc')
$roles=@($roleNames|ForEach-Object {@{Role=$_;Login=$false;PasswordSet=$false;Elevated=$false;Membership=0;OwnsObjects=$false;Connect=$true;Usage=$true}})
Assert-GateDIdentityRoles (Copy-Case $roles);$passed++
$ready=Copy-Case $roles;$ready[0].Login=$true;$ready[0].PasswordSet=$true
Assert-GateDIdentityRoles $ready -Mode CredentialsReady;$passed++
foreach($case in @(
 @{Key='Login';Value=$true},@{Key='PasswordSet';Value=$true},@{Key='Elevated';Value=$true},
 @{Key='Membership';Value=1},@{Key='OwnsObjects';Value=$true},@{Key='Connect';Value=$false}
)){
    $bad=Copy-Case $roles;$bad[1].($case.Key)=$case.Value
    Reject-Case ('role '+$case.Key) {Assert-GateDIdentityRoles $bad}
}
$bad=Copy-Case $ready;$bad[1].Login=$true;$bad[1].PasswordSet=$true
Reject-Case 'second role LOGIN at C2' {Assert-GateDIdentityRoles $bad -Mode CredentialsReady}
$bad=Copy-Case $roles;$bad[1].Role='identity_svc'
Reject-Case 'duplicate role' {Assert-GateDIdentityRoles $bad}
$gatewayFile=Join-Path $repo 'docker/gate-d/identity/gateway.identity.json'
$gatewayConfig=Get-Content $gatewayFile -Raw -Encoding UTF8|ConvertFrom-Json
Assert-GateDIdentityGateway $gatewayConfig;$passed++
$bad=Copy-Case $gatewayConfig;$bad.ReverseProxy.Clusters.identity.Destinations.primary.Address='http://identity:8080'
Reject-Case 'Gateway Gate C destination' {Assert-GateDIdentityGateway $bad}
$bad=Copy-Case $gatewayConfig;$bad.ReverseProxy.Routes.'identity-route'|Add-Member NoteProperty AuthorizationPolicy anonymous
Reject-Case 'Gateway auth override' {Assert-GateDIdentityGateway $bad}
$bad=Copy-Case $gatewayConfig;$bad.ReverseProxy.Routes|Add-Member NoteProperty 'payment-route' @{}
Reject-Case 'Gateway extra route' {Assert-GateDIdentityGateway $bad}

function New-Topology([string]$Mode){
 $m=@{Stage='D1-C';Project='aicos-gate-d';Database='aicos_payment_test';SystemIdentifier='7701931530273472557';NetworkId='synthetic-network';BusinessExecutionAuthorized=$false;Apps=@{};GatewayConfigSource='/approved/gateway.identity.json'}
 $s=@{Network=@{Name='aicos-gate-d_internal';Id='synthetic-network';Internal=$true;Labels=@{'com.docker.compose.project'='aicos-gate-d'};Containers=@{}};Apps=@{};Volumes=@(@{Name='aicos-gate-d_pg18-data';Labels=@{'com.docker.compose.project'='aicos-gate-d'}},@{Name='aicos-gate-d_redis-data';Labels=@{'com.docker.compose.project'='aicos-gate-d'}})}
 $keys=@('Postgres','Redis')
 switch($Mode){'IdentityOnly'{$keys+='Identity'};'IdentityGateway'{$keys+=@('Identity','Gateway')};'IdentityGatewayRunner'{$keys+=@('Identity','Gateway','Runner')}}
 $i=0
 foreach($key in $keys){
  $hex=[string]'abcde'[$i];$i++;$id=$hex*64;$image='sha256:'+($hex*64)
  $service=switch($key){'Postgres'{'gated-postgres'};'Redis'{'gated-redis'};default{'gated-'+$key.ToLowerInvariant()}}
  $pin=@{Id=$id;Image=$image}
  $container=@{Id=$id;Image=$image;Name=('/aicos-gate-d-'+$service.Substring(6));State=@{Running=$true};Config=@{Labels=@{'com.docker.compose.project'='aicos-gate-d';'com.docker.compose.service'=$service;'com.aicos.environment'='isolated-test'}};HostConfig=@{PortBindings=@{};Privileged=$false;NetworkMode='aicos-gate-d_internal'};NetworkSettings=@{Networks=@{'aicos-gate-d_internal'=@{}}};Mounts=@()}
  if($key -eq 'Postgres'){$container.Mounts=@(@{Type='volume';Name='aicos-gate-d_pg18-data';Destination='/var/lib/postgresql'})}
  elseif($key -eq 'Redis'){$container.Mounts=@(@{Type='volume';Name='aicos-gate-d_redis-data';Destination='/data'})}
  elseif($key -eq 'Gateway'){$container.Mounts=@(@{Type='bind';Source=$m.GatewayConfigSource;Destination='/app/appsettings.json';RW=$false})}
  if($key -in @('Postgres','Redis')){$m[$key]=$pin;$s[$key]=$container}else{$m.Apps[$key]=$pin;$s.Apps[$key]=$container}
  $s.Network.Containers[$id]=@{}
 }
 Copy-Case @{Manifest=$m;State=$s}
}
foreach($mode in @('InfraOnly','IdentityOnly','IdentityGateway','IdentityGatewayRunner')){
 $topology=New-Topology $mode
 Assert-GateDIdentityTopology $topology.Manifest $topology.State -Mode $mode;$passed++
}
foreach($case in @(
 @{Name='main db manifest';Change={param($x)$x.Manifest.Database='aicos_db'}},
 @{Name='wrong cluster';Change={param($x)$x.Manifest.SystemIdentifier='9999999999999999999'}},
 @{Name='wrong network';Change={param($x)$x.State.Network.Id='main-network'}},
 @{Name='external network';Change={param($x)$x.State.Network.Internal=$false}},
 @{Name='host port';Change={param($x)$x.State.Postgres.HostConfig.PortBindings|Add-Member NoteProperty '5432/tcp' @()}},
 @{Name='Gate C volume';Change={param($x)$x.State.Postgres.Mounts[0].Name='aicos-be_postgres-data'}},
 @{Name='replacement container';Change={param($x)$x.State.Redis.Id=('f'*64)}},
 @{Name='foreign member';Change={param($x)$x.State.Network.Containers|Add-Member NoteProperty ('f'*64) @{}}},
 @{Name='unapproved business scope';Change={param($x)$x.Manifest.BusinessExecutionAuthorized=$true}}
)){
 $x=New-Topology InfraOnly;& $case.Change $x
 Reject-Case $case.Name {Assert-GateDIdentityTopology $x.Manifest $x.State}
}
$x=New-Topology IdentityGateway
Reject-Case 'apps present during infra-only stage' {Assert-GateDIdentityTopology $x.Manifest $x.State -Mode InfraOnly}
$x.State.Apps.Gateway.Mounts[0].RW=$true
Reject-Case 'writable Gateway config' {Assert-GateDIdentityTopology $x.Manifest $x.State -Mode IdentityGateway}
$x=New-Topology IdentityOnly;$x.State.Apps.Identity.HostConfig.Privileged=$true
Reject-Case 'privileged Identity' {Assert-GateDIdentityTopology $x.Manifest $x.State -Mode IdentityOnly}

# Compose rendering is local config validation, no Engine API/SQL/app startup.
$compose=Invoke-GateDDocker @('compose','--env-file','docker/gate-d/identity/config-only.env.example','-p','aicos-gate-d','-f','docker-compose.gate-d.identity.yml','--profile','d1c-runtime','config','--no-interpolate','--format','json')|ConvertFrom-Json
Assert-GateDIdentityCompose $compose $gatewayFile;$passed++
$bad=Copy-Case $compose;$bad.services.'gated-identity'.environment.Database__MigrateOnStartup='true'
Reject-Case 'template migration startup' {Assert-GateDIdentityCompose $bad $gatewayFile}
$bad=Copy-Case $compose;$bad.services.'gated-identity'.environment.Jwt__SigningKey='plaintext-in-source'
Reject-Case 'template resolved secret' {Assert-GateDIdentityCompose $bad $gatewayFile}
$bad=Copy-Case $compose;$bad.services.'gated-gateway'|Add-Member NoteProperty ports @('8080:8080')
Reject-Case 'template host publication' {Assert-GateDIdentityCompose $bad $gatewayFile}
$bad=Copy-Case $compose;$bad.services|Add-Member NoteProperty postgres @{}
Reject-Case 'template infrastructure recreation' {Assert-GateDIdentityCompose $bad $gatewayFile}
$bad=Copy-Case $compose;$bad.services.'gated-identity'|Add-Member NoteProperty privileged $true
Reject-Case 'template privileged app' {Assert-GateDIdentityCompose $bad $gatewayFile}
$bad=Copy-Case $compose;$bad.services.'gated-identity'|Add-Member NoteProperty command @('seed')
Reject-Case 'template startup seed command' {Assert-GateDIdentityCompose $bad $gatewayFile}
$bad=Copy-Case $compose;$bad.services.'gated-identity'.mem_limit=0
Reject-Case 'template unlimited memory' {Assert-GateDIdentityCompose $bad $gatewayFile}
$bad=Copy-Case $compose;$bad.services.'gated-identity'.cpus=0
Reject-Case 'template unlimited cpu' {Assert-GateDIdentityCompose $bad $gatewayFile}
[pscustomobject]@{Status='PASS';Cases=$passed;Kind='Synthetic metadata/config and local Compose rendering';DatabaseConnection=$false;RuntimeCredentialsCreated=$false;JwtCreated=$false;BusinessTests=$false}|ConvertTo-Json
