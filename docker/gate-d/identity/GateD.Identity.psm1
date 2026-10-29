Set-StrictMode -Version Latest

function Get-IdentityKeys($Value) {
    if($Value -is [Collections.IDictionary]){@($Value.Keys)}else{@($Value.PSObject.Properties|ForEach-Object {$_.Name})}
}

function Assert-GateDIdentityConnection {
    param([Parameter(Mandatory)][string]$ConnectionString)
    $b=New-Object System.Data.Common.DbConnectionStringBuilder
    try {$b.set_ConnectionString($ConnectionString)}catch{throw 'Invalid Identity connection; contents suppressed'}
    $expected=@{host='gated-postgres';port='5432';database='aicos_payment_test';username='identity_svc';'search path'='public';'maximum pool size'='6';'minimum pool size'='0';timeout='5';'command timeout'='10'}
    if(@($b.Keys).Count -ne $expected.Count+1){throw 'Unexpected Identity connection options'}
    foreach($key in $expected.Keys){if(-not $b.ContainsKey($key) -or [string]$b[$key] -cne $expected[$key]){throw 'Identity connection outside test allowlist'}}
    if(-not $b.ContainsKey('Password') -or [string]::IsNullOrWhiteSpace([string]$b['Password']) -or [string]$b['Password'] -match '\$\{|C2 approval'){throw 'Runtime password missing/unresolved'}
}

function Assert-GateDIdentityEnvironment {
    param([Parameter(Mandatory)]$Identity,[Parameter(Mandatory)]$Gateway,[Parameter(Mandatory)][string]$ProtectedSigningKey)
    Assert-GateDIdentityConnection $Identity['ConnectionStrings__Default']
    $shared=@{ASPNETCORE_ENVIRONMENT='Testing';ASPNETCORE_HTTP_PORTS='8080';Database__MigrateOnStartup='false';Jwt__Issuer='aicos-gate-d';Jwt__Audience='aicos-gate-d-clients';Jwt__ExpiryMinutes='1'}
    foreach($envMap in @($Identity,$Gateway)){
        foreach($key in $shared.Keys){if([string]$envMap[$key] -cne $shared[$key]){throw 'Unsafe Identity/Gateway test setting'}}
        $keyText=[string]$envMap['Jwt__SigningKey']
        if($keyText.Length -lt 32 -or $keyText -match '\$\{|dev-only|identity-test|CONFIG_ONLY' -or $keyText -ceq $ProtectedSigningKey){throw 'Missing/reused signing key; value suppressed'}
        foreach($key in (Get-IdentityKeys $envMap)){
            if(($key -like 'Services__*') -or ($key -like 'ReverseProxy__*') -or (($key -match '^ConnectionStrings__') -and [object]::ReferenceEquals($envMap,$Gateway))){throw 'Unexpected service/DB override'}
        }
    }
    if($Identity['Jwt__SigningKey'] -cne $Gateway['Jwt__SigningKey'] -or $Identity['ConnectionStrings__Redis'] -cne 'gated-redis:6379' -or [string]$Identity['RefreshToken__ExpiryDays'] -cne '1'){throw 'Test dependency/JWT mismatch'}
    $db=New-Object System.Data.Common.DbConnectionStringBuilder
    $db.set_ConnectionString($Identity['ConnectionStrings__Default'])
    if([string]$db['Password'] -ceq [string]$Identity['Jwt__SigningKey']){throw 'DB password must differ from signing key'}
}

function Assert-GateDIdentityRoles {
    param([Parameter(Mandatory)][object[]]$Roles,[ValidateSet('BeforeCredentials','CredentialsReady')][string]$Mode='BeforeCredentials')
    $names=@('identity_svc','menu_svc','table_reservation_svc','ordering_svc','payment_svc','inventory_svc','kitchen_svc','workforce_svc','reporting_svc','customer_svc','ai_svc')
    if($Roles.Count -ne 11 -or @($Roles|Select-Object -ExpandProperty Role -Unique).Count -ne 11){throw 'Expected exactly eleven distinct runtime roles'}
    foreach($role in $Roles){
        $login=$Mode -ceq 'CredentialsReady' -and $role.Role -ceq 'identity_svc'
        if($role.Role -notin $names -or $role.Login -ne $login -or $role.PasswordSet -ne $login -or $role.Elevated -or $role.Membership -ne 0 -or $role.OwnsObjects -or -not $role.Connect -or -not $role.Usage){throw 'Runtime role state outside approved stage'}
    }
}

function Assert-GateDIdentityGateway {
    param([Parameter(Mandatory)]$Configuration)
    $c=$Configuration
    if((@(Get-IdentityKeys $c.ReverseProxy.Routes) -join ',') -cne 'identity-route' -or (@(Get-IdentityKeys $c.ReverseProxy.Clusters) -join ',') -cne 'identity'){throw 'Gateway must contain only Identity route/cluster'}
    $r=$c.ReverseProxy.Routes.'identity-route'
    if($r.ClusterId -cne 'identity' -or $r.Match.Path -cne '/api/v1/identity/{**catch-all}' -or @(Get-IdentityKeys $r).Count -ne 2 -or @(Get-IdentityKeys $r.Match).Count -ne 1){throw 'Unexpected route transform/auth/method override'}
    $destinations=$c.ReverseProxy.Clusters.identity.Destinations
    if((@(Get-IdentityKeys $destinations) -join ',') -cne 'primary' -or $destinations.primary.Address -cne 'http://gated-identity:8080' -or @(Get-IdentityKeys $destinations.primary).Count -ne 1){throw 'Gateway destination outside test Identity'}
    if($c.Jwt.Issuer -cne 'aicos-gate-d' -or $c.Jwt.Audience -cne 'aicos-gate-d-clients' -or $c.Jwt.SigningKey -cne '' -or $c.Jwt.ExpiryMinutes -ne 1 -or $c.Database.MigrateOnStartup -ne $false){throw 'Gateway file must remain secret-free with migrations disabled'}
}

function Assert-GateDIdentityTopology {
    param([Parameter(Mandatory)]$Manifest,[Parameter(Mandatory)]$State,
          [ValidateSet('InfraOnly','IdentityOnly','IdentityGateway','IdentityGatewayRunner')][string]$Mode='InfraOnly')
    if($Manifest.Stage -cne 'D1-C' -or $Manifest.Project -cne 'aicos-gate-d' -or $Manifest.Database -cne 'aicos_payment_test' -or $Manifest.SystemIdentifier -cne '7701931530273472557' -or $Manifest.BusinessExecutionAuthorized -ne $false){throw 'Unapproved Identity preparation manifest'}
    if($State.Network.Name -cne 'aicos-gate-d_internal' -or $State.Network.Id -cne $Manifest.NetworkId -or -not $State.Network.Internal -or $State.Network.Labels.'com.docker.compose.project' -cne 'aicos-gate-d'){throw 'Test network mismatch'}
    $apps=@(switch($Mode){'InfraOnly'{};'IdentityOnly'{'Identity'};'IdentityGateway'{'Identity';'Gateway'};'IdentityGatewayRunner'{'Identity';'Gateway';'Runner'}})
    if(@(Get-IdentityKeys $Manifest.Apps).Count -ne @($apps).Count -or @(Get-IdentityKeys $State.Apps).Count -ne @($apps).Count){throw 'App state does not match stage'}
    $specs=@(
        @{Key='Postgres';Service='gated-postgres';Name='aicos-gate-d-postgres';Volume='aicos-gate-d_pg18-data';Mount='/var/lib/postgresql'},
        @{Key='Redis';Service='gated-redis';Name='aicos-gate-d-redis';Volume='aicos-gate-d_redis-data';Mount='/data'}
    )
    foreach($app in $apps){$service='gated-'+$app.ToLowerInvariant();$specs+=@{Key=$app;Service=$service;Name='aicos-gate-d-'+$app.ToLowerInvariant();Volume=$null;Mount=$null}}
    $ids=@()
    foreach($spec in $specs){
        $isApp=$spec.Key -in @('Identity','Gateway','Runner')
        $c=if($isApp){$State.Apps.($spec.Key)}else{$State.($spec.Key)}
        $pin=if($isApp){$Manifest.Apps.($spec.Key)}else{$Manifest.($spec.Key)}
        if($pin.Id -notmatch '^[a-f0-9]{64}$' -or $pin.Image -notmatch '^sha256:[a-f0-9]{64}$' -or $c.Id -cne $pin.Id -or $c.Image -cne $pin.Image -or $c.Name -cne ('/'+$spec.Name) -or -not $c.State.Running){throw 'Container pin/state mismatch'}
        if($c.Config.Labels.'com.docker.compose.project' -cne 'aicos-gate-d' -or $c.Config.Labels.'com.docker.compose.service' -cne $spec.Service -or $c.Config.Labels.'com.aicos.environment' -cne 'isolated-test'){throw 'Container labels outside Gate D'}
        if((@(Get-IdentityKeys $c.NetworkSettings.Networks) -join ',') -cne 'aicos-gate-d_internal' -or @(Get-IdentityKeys $c.HostConfig.PortBindings).Count -ne 0){throw 'Cross-network or host publication'}
        if($isApp){
            if($c.HostConfig.Privileged -or $c.HostConfig.NetworkMode -ceq 'host'){throw 'Privileged/host app forbidden'}
            if($spec.Key -ceq 'Gateway'){
                if(@($c.Mounts).Count -ne 1 -or $c.Mounts[0].Type -cne 'bind' -or $c.Mounts[0].Destination -cne '/app/appsettings.json' -or $c.Mounts[0].RW -or $c.Mounts[0].Source -cne $Manifest.GatewayConfigSource){throw 'Unapproved Gateway config mount'}
            } elseif(@($c.Mounts).Count -ne 0){throw 'Unexpected app mount'}
        } elseif(@($c.Mounts).Count -ne 1 -or $c.Mounts[0].Type -cne 'volume' -or $c.Mounts[0].Name -cne $spec.Volume -or $c.Mounts[0].Destination -cne $spec.Mount){throw 'Infra volume mismatch'}
        $ids+=$c.Id
    }
    if(@($ids|Select-Object -Unique).Count -ne $ids.Count){throw 'Duplicate container pin'}
    $members=@(Get-IdentityKeys $State.Network.Containers)
    if($members.Count -ne $ids.Count -or @($members|Where-Object {$_ -notin $ids}).Count){throw 'Network has unexpected members'}
    if(@($State.Volumes).Count -ne 2){throw 'Unexpected infrastructure volumes'}
    foreach($v in $State.Volumes){if($v.Name -notin @('aicos-gate-d_pg18-data','aicos-gate-d_redis-data') -or $v.Labels.'com.docker.compose.project' -cne 'aicos-gate-d'){throw 'Volume project mismatch'}}
}

function Assert-GateDIdentityCompose {
    param([Parameter(Mandatory)]$Configuration,[Parameter(Mandatory)][string]$GatewayFile)
    $c=$Configuration
    if($c.name -cne 'aicos-gate-d' -or (@(Get-IdentityKeys $c.services|Sort-Object) -join ',') -cne 'gated-gateway,gated-identity'){throw 'Apps-only test Compose required'}
    if((@(Get-IdentityKeys $c.networks) -join ',') -cne 'gated-internal' -or -not $c.networks.'gated-internal'.external -or $c.networks.'gated-internal'.name -cne 'aicos-gate-d_internal'){throw 'Compose must reuse only test network'}
    if((@(Get-IdentityKeys $c.configs) -join ',') -cne 'gated-gateway-identity' -or [IO.Path]::GetFullPath($c.configs.'gated-gateway-identity'.file) -cne [IO.Path]::GetFullPath($GatewayFile)){throw 'Unexpected Gateway config file'}
    if(@(Get-IdentityKeys $c) -contains 'volumes'){throw 'No infrastructure volume declarations allowed'}
    $shared=@{ASPNETCORE_ENVIRONMENT='Testing';ASPNETCORE_HTTP_PORTS='8080';Database__MigrateOnStartup='false';Jwt__Issuer='aicos-gate-d';Jwt__Audience='aicos-gate-d-clients';Jwt__ExpiryMinutes='1';Jwt__SigningKey='${AICOS_GATED_TEST_JWT_KEY:?Use the reserved Gate D key after approval}'}
    foreach($name in @('gated-identity','gated-gateway')){
        $s=$c.services.$name
        foreach($forbidden in @('ports','build','entrypoint','command','volumes','extra_hosts','privileged','network_mode','pid','ipc','devices','cap_add','env_file','secrets')){if(@(Get-IdentityKeys $s) -contains $forbidden){throw 'Unsupported Compose startup/mount/port setting'}}
        $imageVar=if($name -ceq 'gated-identity'){'${AICOS_GATED_IDENTITY_IMAGE:?Use the immutable image ID from C1 manifest}'}else{'${AICOS_GATED_GATEWAY_IMAGE:?Use the immutable image ID from C1 manifest}'}
        if($s.image -cne $imageVar -or $s.pull_policy -cne 'never' -or $s.restart -cne 'no' -or $s.container_name -cne ('aicos-gate-d-'+$name.Substring(6)) -or (@($s.profiles) -join ',') -cne 'd1c-runtime' -or (@(Get-IdentityKeys $s.networks) -join ',') -cne 'gated-internal'){throw 'Compose app identity/policy mismatch'}
        if($s.labels.'com.aicos.environment' -cne 'isolated-test' -or $s.labels.'com.aicos.gate' -cne 'D1-C' -or $s.mem_limit -le 0 -or $s.mem_limit -gt 268435456 -or $s.cpus -le 0 -or $s.cpus -gt 0.5){throw 'Unexpected app labels/resources'}
        foreach($key in $shared.Keys){if([string]$s.environment.$key -cne $shared[$key]){throw 'Unsafe or resolved secret in committed Compose template'}}
        $expectedEnv=if($name -ceq 'gated-identity'){11}else{7}
        if(@(Get-IdentityKeys $s.environment).Count -ne $expectedEnv){throw 'Unexpected environment override'}
    }
    $identity=$c.services.'gated-identity'
    $expectedCs='Host=gated-postgres;Port=5432;Database=aicos_payment_test;Username=identity_svc;Password=${AICOS_GATED_IDENTITY_PASSWORD:?C2 credentials approval required};Search Path=public;Maximum Pool Size=6;Minimum Pool Size=0;Timeout=5;Command Timeout=10'
    if($identity.environment.ConnectionStrings__Default -cne $expectedCs -or $identity.environment.ConnectionStrings__Redis -cne 'gated-redis:6379' -or [string]$identity.environment.RefreshToken__ExpiryDays -cne '1' -or [string]$identity.environment.Swagger__Enabled -cne 'true'){throw 'Identity dependency/template mismatch'}
    $gateway=$c.services.'gated-gateway'
    if(@($gateway.configs).Count -ne 1 -or $gateway.configs[0].source -cne 'gated-gateway-identity' -or $gateway.configs[0].target -cne '/app/appsettings.json' -or (@(Get-IdentityKeys $gateway.depends_on) -join ',') -cne 'gated-identity'){throw 'Unexpected Gateway config/dependency'}
}

Export-ModuleMember -Function Assert-GateDIdentityConnection,Assert-GateDIdentityEnvironment,Assert-GateDIdentityRoles,Assert-GateDIdentityGateway,Assert-GateDIdentityTopology,Assert-GateDIdentityCompose
