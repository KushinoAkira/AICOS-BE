Set-StrictMode -Version Latest

function Invoke-GateDCommand {
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$Arguments=@(), [string]$InputText)
    # Fixed argv only; SQL travels over stdin, credentials through process environment.
    if(@($Arguments|Where-Object {$_ -match '[\s"]'}).Count){throw 'Unsupported native argument'}
    $si=New-Object Diagnostics.ProcessStartInfo
    $si.FileName=$FilePath
    $si.Arguments=$Arguments -join ' '
    $si.UseShellExecute=$false
    $si.CreateNoWindow=$true
    $si.RedirectStandardOutput=$true
    $si.RedirectStandardError=$true
    $si.RedirectStandardInput=$true
    $p=New-Object Diagnostics.Process
    $p.StartInfo=$si
    try {
        $null=$p.Start()
        $stdout=$p.StandardOutput.ReadToEndAsync()
        $stderr=$p.StandardError.ReadToEndAsync()
        if($InputText){
            $p.StandardInput.Write($InputText.Replace([string][char]13,''))
            $p.StandardInput.Write([char]10)
        }
        $p.StandardInput.Close()
        $p.WaitForExit()
        if($p.ExitCode -ne 0){throw ('Native command failed (exit '+$p.ExitCode+'); raw output suppressed')}
        return $stdout.Result
    } finally {$p.Dispose()}
}

function Invoke-GateDDocker {
    param([Parameter(Mandatory)][string[]]$Arguments)
    Invoke-GateDCommand -FilePath ((Get-Command docker.exe -CommandType Application|Select-Object -First 1).Source) -Arguments $Arguments
}

function Get-GateDSecretText {
    param([Parameter(Mandatory)][Security.SecureString]$Secret)
    $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
    try {[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)}
    finally {[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)}
}

function New-GateDSecret {
    $bytes=New-Object byte[] 48
    $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
        ConvertTo-SecureString ([Convert]::ToBase64String($bytes)) -AsPlainText -Force
    } finally {$rng.Dispose();[Array]::Clear($bytes,0,$bytes.Length)}
}

function Protect-GateDPrivatePath {
    param([Parameter(Mandatory)][string]$Path)
    $acl=if(Test-Path $Path -PathType Container){New-Object Security.AccessControl.DirectorySecurity}else{New-Object Security.AccessControl.FileSecurity}
    $acl.SetAccessRuleProtection($true,$false)
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
    if(Test-Path $Path -PathType Container){
        $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
    } else {$rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow')}
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}

function Assert-GateDTarget {
    param([Parameter(Mandatory)][string]$ConnectionString,
          [Parameter(Mandatory)][string]$SystemIdentifier,
          [Parameter(Mandatory)][string]$ExpectedSystemIdentifier,
          [ValidateSet('Host','DockerExec')][string]$Transport='Host',
          [string]$ContainerId,[string]$ExpectedContainerId)
    $builder=New-Object System.Data.Common.DbConnectionStringBuilder
    try {$builder.set_ConnectionString($ConnectionString)} catch {throw 'Invalid connection string; contents suppressed'}
    foreach($key in @('Host','Port','Database','Username')){
        if(-not $builder.ContainsKey($key)){throw 'Explicit target fields required'}
    }
    $allowed=@('host','port','database','username','password')
    if(@($builder.Keys|Where-Object {([string]$_).ToLowerInvariant() -notin $allowed}).Count){throw 'Unexpected connection-string option'}
    if([string]$builder['Database'] -cne 'aicos_payment_test'){throw 'Target database invalid'}
    if($Transport -eq 'Host'){
        if([string]$builder['Host'] -cne '127.0.0.1' -or [string]$builder['Port'] -cne '15432'){throw 'Target outside isolated host/port'}
    } else {
        if([string]$builder['Host'] -cne 'gated-postgres' -or [string]$builder['Port'] -cne '5432' -or
           $ContainerId -notmatch '^[a-f0-9]{64}$' -or $ContainerId -cne $ExpectedContainerId){throw 'Pinned Docker SQL target invalid'}
    }
    if([string]$builder['Username'] -notin @('postgres','identity_svc','menu_svc','table_reservation_svc','ordering_svc','payment_svc','kitchen_svc','inventory_svc','workforce_svc','reporting_svc','customer_svc','ai_svc')){
        throw 'Unexpected database user'
    }
    if($ExpectedSystemIdentifier -notmatch '^[0-9]{15,20}$' -or $SystemIdentifier -cne $ExpectedSystemIdentifier){
        throw 'PostgreSQL cluster identity mismatch'
    }
}

function Assert-GateDResourceIdentity {
    param([Parameter(Mandatory)]$Manifest,[Parameter(Mandatory)]$State)
    if($Manifest.Project -cne 'aicos-gate-d' -or $Manifest.Database -cne 'aicos_payment_test'){throw 'Manifest target invalid'}
    if($State.Network.Name -cne 'aicos-gate-d_internal' -or -not $State.Network.Internal -or
       $State.Network.Id -cne $Manifest.NetworkId -or
       $State.Network.Labels.'com.docker.compose.project' -cne 'aicos-gate-d'){throw 'Network identity/isolation mismatch'}
    foreach($spec in @(
        @{Key='Postgres';Name='aicos-gate-d-postgres';Service='gated-postgres';Volume='aicos-gate-d_pg18-data';Mount='/var/lib/postgresql'},
        @{Key='Redis';Name='aicos-gate-d-redis';Service='gated-redis';Volume='aicos-gate-d_redis-data';Mount='/data'}
    )){
        $c=$State.($spec.Key)
        $expected=$Manifest.($spec.Key)
        if($c.Id -cne $expected.Id -or $c.Image -cne $expected.Image -or $c.Name -cne ('/'+$spec.Name) -or -not $c.State.Running){throw 'Container identity/state mismatch'}
        if($c.Config.Labels.'com.docker.compose.project' -cne 'aicos-gate-d' -or
           $c.Config.Labels.'com.docker.compose.service' -cne $spec.Service -or
           $c.Config.Labels.'com.aicos.environment' -cne 'isolated-test'){throw 'Container labels mismatch'}
        $nets=@($c.NetworkSettings.Networks.PSObject.Properties.Name)
        if($nets.Count -ne 1 -or $nets[0] -cne 'aicos-gate-d_internal'){throw 'Cross-environment network attachment'}
        $mounts=@($c.Mounts)
        if($mounts.Count -ne 1 -or $mounts[0].Type -cne 'volume' -or $mounts[0].Name -cne $spec.Volume -or $mounts[0].Destination -cne $spec.Mount){throw 'Unexpected volume/mount'}
    }
    if(@($State.Postgres.HostConfig.PortBindings.PSObject.Properties).Count){throw 'Internal-only PostgreSQL must not publish host ports'}
    if(@($State.Redis.HostConfig.PortBindings.PSObject.Properties).Count){throw 'Redis must not publish host ports'}
    foreach($v in @($State.Volumes)){
        if($v.Name -notin @('aicos-gate-d_pg18-data','aicos-gate-d_redis-data') -or
           $v.Labels.'com.docker.compose.project' -cne 'aicos-gate-d'){throw 'Volume label/identity mismatch'}
    }
    if(@($State.Volumes).Count -ne 2){throw 'Expected exactly two test volumes'}
    $members=@($State.Network.Containers.PSObject.Properties.Name)
    if($members.Count -ne 2 -or @($members|Where-Object {$_ -notin @($State.Postgres.Id,$State.Redis.Id)}).Count){throw 'Unexpected container on test network'}
}

function Get-GateDResourceState {
    $pg=@((Invoke-GateDDocker @('inspect','aicos-gate-d-postgres')|ConvertFrom-Json))[0]
    $redis=@((Invoke-GateDDocker @('inspect','aicos-gate-d-redis')|ConvertFrom-Json))[0]
    $network=@((Invoke-GateDDocker @('network','inspect','aicos-gate-d_internal')|ConvertFrom-Json))[0]
    $volumes=Invoke-GateDDocker @('volume','inspect','aicos-gate-d_pg18-data','aicos-gate-d_redis-data')|ConvertFrom-Json
    @{Postgres=$pg;Redis=$redis;Network=$network;Volumes=$volumes}
}

function Get-GateDProtectedSnapshot {
    $names=@('identity','menu','table','order','payment','inventory','kitchen','workforce','reporting','customer','ai','gateway','redis')
    $snapshot=@{}
    foreach($name in $names){
        $c=@((Invoke-GateDDocker @('inspect',('aicos-'+$name))|ConvertFrom-Json))[0]
        if(-not $c.State.Running){throw 'Protected Gate C container is not running'}
        $envMap=@{};foreach($entry in $c.Config.Env){$i=$entry.IndexOf('=');$envMap[$entry.Substring(0,$i)]=$entry.Substring($i+1)}
        $snapshot[$name]=@{Id=$c.Id;Image=$c.Image;StartedAt=$c.State.StartedAt;Environment=$envMap}
    }
    return $snapshot
}

function Get-GateDPublicSnapshot {
    param([Parameter(Mandatory)]$Snapshot)
    $result=@{}
    foreach($name in $Snapshot.Keys){$c=$Snapshot[$name];$result[$name]=@{Id=$c.Id;Image=$c.Image;StartedAt=$c.StartedAt}}
    return $result
}

function Assert-GateDProtectedUnchanged {
    param([Parameter(Mandatory)]$Before,[switch]$CompareEnvironment)
    $now=Get-GateDProtectedSnapshot
    $names=if($Before -is [Collections.IDictionary]){@($Before.Keys)}else{@($Before.PSObject.Properties.Name)}
    foreach($name in $names){
        $b=$Before.$name;$a=$now[$name]
        if($b.Id -cne $a.Id -or $b.Image -cne $a.Image -or $b.StartedAt -cne $a.StartedAt){throw 'Protected Gate C container changed'}
    }
    if($CompareEnvironment){
        foreach($name in $Before.Keys){
            $b=$Before[$name];$a=$now[$name]
            if($b.Id -cne $a.Id -or $b.Image -cne $a.Image -or $b.StartedAt -cne $a.StartedAt -or $b.Environment.Count -ne $a.Environment.Count){throw 'Protected Gate C container changed'}
            foreach($k in $b.Environment.Keys){if(-not $a.Environment.ContainsKey($k) -or $a.Environment[$k] -cne $b.Environment[$k]){throw 'Protected Gate C configuration changed'}}
        }
    }
    if((Invoke-GateDDocker @('inspect','--format','{{.State.Health.Status}}','aicos-redis')).Trim() -ne 'healthy'){throw 'Protected Redis unhealthy'}
}

function Invoke-GateDReadOnlyProbe {
    param([Parameter(Mandatory)]$Manifest,[Parameter(Mandatory)][Security.SecureString]$Password)
    $state=Get-GateDResourceState
    Assert-GateDResourceIdentity $Manifest $state
    if($Manifest.Transport -cne 'DockerExecPinned'){throw 'D1-A is container-only; no host database connection allowed'}
    $pgdata=@($state.Postgres.Config.Env|Where-Object {$_ -like 'PGDATA=*'})[0].Substring(7)
    if($pgdata -cne '/var/lib/postgresql/18/docker'){throw 'Unexpected PG18 data path'}
    $control=Invoke-GateDDocker @('exec',$Manifest.Postgres.Id,'pg_controldata',$pgdata)
    $match=[regex]::Match($control,'Database system identifier:\s*(\d+)')
    if(-not $match.Success){throw 'Cannot determine PostgreSQL cluster identity'}
    $cs='Host=gated-postgres;Port=5432;Database=aicos_payment_test;Username=postgres'
    Assert-GateDTarget $cs $match.Groups[1].Value $Manifest.SystemIdentifier -Transport DockerExec -ContainerId $state.Postgres.Id -ExpectedContainerId $Manifest.Postgres.Id
    $expectedPassword=Get-GateDSecretText $Password
    $actualPassword=@($state.Postgres.Config.Env|Where-Object {$_ -like 'POSTGRES_PASSWORD=*'})[0].Substring(18)
    if($expectedPassword -cne $actualPassword){throw 'Test bootstrap password mismatch'}
    $expectedPassword=$null;$actualPassword=$null
    # Static read-only script travels over stdin. No password in argv or script output.
    $probe=@'
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
export PGOPTIONS='-c default_transaction_read_only=on -c statement_timeout=10000 -c lock_timeout=2000'
export PGCONNECT_TIMEOUT=5
export PGAPPNAME=aicos-gated-d1a-readonly
exec psql -X -w -qAt -F '|' -h gated-postgres -p 5432 -U postgres -d aicos_payment_test -v ON_ERROR_STOP=1 -f - <<'GATED_SQL'
BEGIN READ ONLY;
SELECT current_database(),current_user,current_schema(),current_setting('transaction_read_only'),current_setting('server_version_num'),inet_server_port(),(SELECT system_identifier::text FROM pg_control_system());
SELECT 'public_tables',count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p');
SELECT 'service_roles',count(*) FROM pg_roles WHERE rolname IN ('identity_svc','menu_svc','table_reservation_svc','ordering_svc','payment_svc','kitchen_svc','inventory_svc','workforce_svc','reporting_svc','customer_svc','ai_svc');
COMMIT;
GATED_SQL
'@
    $out=Invoke-GateDCommand -FilePath ((Get-Command docker.exe -CommandType Application|Select-Object -First 1).Source) -Arguments @('exec','-i',$Manifest.Postgres.Id,'sh','-s') -InputText $probe
    $lines=@($out -split '\r?\n'|Where-Object {$_})
    if($lines.Count -ne 3){throw 'Unexpected read-only probe response'}
    $sig=$lines[0] -split '\|'
    if($sig.Count -ne 7 -or $sig[0] -cne 'aicos_payment_test' -or $sig[1] -cne 'postgres' -or $sig[2] -cne 'public' -or $sig[3] -cne 'on' -or [int]$sig[4] -lt 180000 -or [int]$sig[4] -ge 190000 -or $sig[5] -cne '5432' -or $sig[6] -cne $Manifest.SystemIdentifier){throw 'Connected database identity mismatch'}
    if($lines[1] -cne 'public_tables|0' -or $lines[2] -cne 'service_roles|0'){throw 'D1-A expects empty application schema and no runtime roles'}
    $pong=(Invoke-GateDDocker @('exec',$Manifest.Redis.Id,'redis-cli','PING')).Trim()
    $size=(Invoke-GateDDocker @('exec',$Manifest.Redis.Id,'redis-cli','DBSIZE')).Trim()
    if($pong -cne 'PONG' -or $size -cne '0'){throw 'Test Redis readiness/empty DB check failed'}
    return @{Database='aicos_payment_test';Host='gated-postgres';Port=5432;Transport='DockerExecPinned';ServerVersionNumber=[int]$sig[4];SystemIdentifier=$sig[6];ReadOnly=$true;PublicTables=0;RuntimeRoles=0;Redis='PONG';RedisDb0Keys=0;WindowsDatabaseConnection=$false}
}

Export-ModuleMember -Function Invoke-GateDCommand,Invoke-GateDDocker,Get-GateDSecretText,New-GateDSecret,Protect-GateDPrivatePath,Assert-GateDTarget,Assert-GateDResourceIdentity,Get-GateDResourceState,Get-GateDProtectedSnapshot,Get-GateDPublicSnapshot,Assert-GateDProtectedUnchanged,Invoke-GateDReadOnlyProbe

