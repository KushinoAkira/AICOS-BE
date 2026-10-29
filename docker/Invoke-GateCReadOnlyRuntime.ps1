param(
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [string]$CredentialCachePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Gate C only: hidden local input, READ ONLY SQL, HTTP GET, staged startup.
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$roles = @(
    @{Name='identity_svc';Key='IDENTITY_DB_PASSWORD';Tables=@('tenants','branches','users','roles','tenant_users','staff_assignments','refresh_sessions')},
    @{Name='menu_svc';Key='MENU_DB_PASSWORD';Tables=@('categories','products','product_variants','product_option_groups','product_options','branch_products')},
    @{Name='table_reservation_svc';Key='TABLE_DB_PASSWORD';Tables=@('table_areas','cafe_tables','table_sessions','table_session_tables')},
    @{Name='ordering_svc';Key='ORDERING_DB_PASSWORD';Tables=@('orders','order_items','order_item_options','order_item_events')},
    @{Name='payment_svc';Key='PAYMENT_DB_PASSWORD';Tables=@('bills','bill_items','payments','payment_events')},
    @{Name='inventory_svc';Key='INVENTORY_DB_PASSWORD';Tables=@()},
    @{Name='kitchen_svc';Key='KITCHEN_DB_PASSWORD';Tables=@('orders','order_items','order_item_events')},
    @{Name='workforce_svc';Key='WORKFORCE_DB_PASSWORD';Tables=@()},
    @{Name='reporting_svc';Key='REPORTING_DB_PASSWORD';Tables=@()},
    @{Name='customer_svc';Key='CUSTOMER_DB_PASSWORD';Tables=@()},
    @{Name='ai_svc';Key='AI_DB_PASSWORD';Tables=@()}
)
$services = @(
    @{Name='identity';Port=5101;Prefix='identity';Read='/api/v1/identity/auth/me'},
    @{Name='menu';Port=5102;Prefix='menu';Read='/api/v1/menu/catalog'},
    @{Name='payment';Port=5105;Prefix='payments';Read=''},
    @{Name='table';Port=5103;Prefix='tables';Read='/api/v1/tables/map'},
    @{Name='order';Port=5104;Prefix='orders';Read=''},
    @{Name='kitchen';Port=5107;Prefix='kitchen';Read='/api/v1/kitchen/queue'}
)
$compose = @('--env-file','.env.example','-f','docker-compose.yml','-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
$state = [ordered]@{Status='Preparing';Step='Preflight';RolesVerified=@();Services=@();Checks=@();Images=@{};PublicTables=$null;PublicColumns=$null;SnapshotUnchanged=$null;AuthenticatedSmoke='NOT RUN';Error=$null;ErrorType=$null;ErrorLine=$null;ExistingUnrelatedContainers=@()}
$secrets = @{}
$cachedSecrets = @{}
if (-not $CredentialCachePath) { $CredentialCachePath=Join-Path $EvidenceDirectory 'gate-c-roles.dpapi.xml' }
$envKeys = @($roles | ForEach-Object {$_.Key}) + @('JWT_SIGNING_KEY','POSTGRES_PASSWORD','AICOS_DB_HOST','AICOS_DB_PORT','PAYMENT_MUTATIONS_ENABLED','PGPASSWORD','PGOPTIONS','PGAPPNAME','PGCONNECT_TIMEOUT')
$previousEnv = @{}
$accessToken = $null
function Save-State { $state | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $EvidenceDirectory 'runtime-result.json') -Encoding UTF8 }
function Set-Step([string]$Name) { $state.Step=$Name; Save-State; Write-Host $Name }
function Reveal([Security.SecureString]$Secret) {
    $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secret)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
}
function Save-CredentialCache {
    # Windows DPAPI encrypts SecureString values for this Windows account.
    # Private cache stays outside Git; never export decrypted password strings.
    if (-not (Test-Path -LiteralPath $CredentialCachePath)) {
        $null=New-Item -ItemType File -Path $CredentialCachePath
        # Apply the private DACL once, before any ciphertext is written.
        $acl=New-Object Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true,$false)
        $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
        $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow')
        $acl.SetAccessRule($rule)
        Set-Acl -LiteralPath $CredentialCachePath -AclObject $acl
    }
    $cachedSecrets | Export-Clixml -LiteralPath $CredentialCachePath
}
function Initialize-SharedJwtKey {
    Write-Host 'JWT signing key KHONG PHAI mat khau database. NHAN ENTER de tu sinh key an toan.'
    while ($true) {
        $provided=Read-Host 'JWT signing key: NHAN ENTER neu chua co key rieng' -AsSecureString
        $plain=$null
        try {
            if ($provided.Length -eq 0) {
                $bytes=New-Object byte[] 48
                $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
                try { $rng.GetBytes($bytes); $env:JWT_SIGNING_KEY=[Convert]::ToBase64String($bytes) }
                finally { $rng.Dispose(); [Array]::Clear($bytes,0,$bytes.Length) }
                return
            }
            if ($provided.Length -lt 32) {
                Write-Host 'Key too short (minimum 32 characters). Retry or press Enter; verified role credentials are retained.'
                continue
            }
            $plain=Reveal $provided
            $env:JWT_SIGNING_KEY=$plain
            return
        } finally { $plain=$null; $provided.Dispose() }
    }
}
function Run-Docker([string[]]$Arguments) {
    # Compose writes normal progress to stderr. Capture privately without
    # Windows PowerShell converting that progress to a terminating error.
    # All callers use fixed arguments without spaces or quotes; reject others.
    if (@($Arguments | Where-Object { $_ -match '[\s\x22]' }).Count) { throw 'Unsupported Docker argument.' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = (Get-Command docker.exe -CommandType Application | Select-Object -First 1).Source
    $start.Arguments = $Arguments -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "Docker command failed at $($state.Step). Output was not logged." }
        $null = $stderr.Result
        return $stdout.Result
    } finally { $process.Dispose() }
}
function Read-Database($Role, [string[]]$Arguments) {
    try {
        $env:PGPASSWORD = Reveal $secrets[$Role.Name]
        $output = & $psql -X -w -qAt -F '|' -h localhost -p 5432 -d aicos_db -U $Role.Name -v ON_ERROR_STOP=1 @Arguments 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Read-only database check failed for $($Role.Name)." }
        return ($output -join [Environment]::NewLine)
    } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}
function Get-ReadSnapshot {
    $snapshot = @{}
    foreach ($role in $roles) {
        $queries = @('BEGIN READ ONLY;')
        foreach ($table in $role.Tables) {
            if (-not $snapshot.ContainsKey($table)) {
                $queries += "SELECT '$table',count(*),md5(coalesce(string_agg(md5(row_to_json(t)::text),'' ORDER BY md5(row_to_json(t)::text)),'')) FROM public.$table t;"
            }
        }
        $queries += 'COMMIT;'
        if ($queries.Count -gt 2) {
            $result = Read-Database $role @('-c',($queries -join ' '))
            foreach ($row in ($result -split '\r?\n')) {
                if ($row) { $snapshot[($row -split '\|')[0]]=$row }
            }
        }
    }
    return $snapshot
}
function Check-Get([string]$Url,[int]$Expected=200,[hashtable]$Headers=@{}) {
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $Url -Method Get -Headers $Headers -TimeoutSec 10
        $code = [int]$response.StatusCode
    } catch {
        $code = 0
        if ($_.Exception.Response) { $code=[int]$_.Exception.Response.StatusCode }
        if ($code -ne $Expected) { throw "GET failed at $($state.Step): status $code (expected $Expected)." }
        $response = $null
    }
    if ($code -ne $Expected) { throw "Unexpected HTTP status at $($state.Step)." }
    $state.Checks += @{Url=$Url;Status=$code;Result='PASS'}
    Save-State
    return $response
}
function Wait-Ready([int]$Port) {
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $response=Invoke-WebRequest -UseBasicParsing "http://127.0.0.1:$Port/health/ready" -TimeoutSec 5
            if ([int]$response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Readiness timeout at $($state.Step); later services not started."
}
Push-Location $root
try {
    if (-not [Environment]::UserInteractive) { throw 'Interactive local console required.' }
    if (-not (Test-Path $EvidenceDirectory -PathType Container)) { throw 'Evidence directory must exist outside Git.' }
    $evidencePath=(Resolve-Path $EvidenceDirectory).Path
    if ($evidencePath.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence must be outside repository.' }
    if (Test-Path (Join-Path $EvidenceDirectory 'runtime-result.json')) { throw 'Refusing to reuse existing execution evidence.' }
    $cachePath=[IO.Path]::GetFullPath($CredentialCachePath)
    $privateRoot=Join-Path $env:LOCALAPPDATA 'AICOS\deployments'
    if (-not $cachePath.StartsWith($privateRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($cachePath) -ne 'gate-c-roles.dpapi.xml') {
        throw 'Credential cache must be a named DPAPI file in private AICOS deployment evidence.'
    }
    if (Test-Path -LiteralPath $CredentialCachePath) {
        $cachedSecrets=Import-Clixml -LiteralPath $CredentialCachePath
        foreach ($name in $cachedSecrets.Keys) {
            if ($name -notin $roles.Name -or $cachedSecrets[$name] -isnot [Security.SecureString]) {
                throw 'Invalid encrypted credential cache.'
            }
        }
    }
    foreach ($key in $envKeys) { $previousEnv[$key]=[Environment]::GetEnvironmentVariable($key,'Process') }
    Save-State
    $null=Run-Docker @('info','--format','{{.ServerVersion}}')
    $running=(Run-Docker @('ps','--format','{{.Names}}')).Trim()
    $state.ExistingUnrelatedContainers=@($running -split '\r?\n' | Where-Object { $_ })
    $allowedUnrelated=@('vietai_redis','vietai_mongodb','vietai_ollama')
    if (@($state.ExistingUnrelatedContainers | Where-Object { $_ -notin $allowedUnrelated }).Count) {
        throw 'Unexpected running containers; inspect before startup.'
    }
    # Reviewed VietAI containers remain untouched. AICOS Redis has no host port.
    $ports=@(8080,5101,5102,5103,5104,5105,5107)
    if (@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in $ports }).Count) {
        throw 'A required AICOS API port is occupied; inspect before startup.'
    }
    foreach ($name in @('redis','identity','menu','payment','table','order','kitchen','gateway')) {
        $existing = & docker ps -aq --filter "name=^/aicos-$name$"
        if ($LASTEXITCODE -ne 0 -or $existing) { throw "Existing container aicos-$name; inspect before reuse." }
    }
    foreach ($image in (@('redis:7-alpine') + @($services | ForEach-Object {"aicos/$($_.Name)"}) + @('aicos/gateway'))) {
        $state.Images[$image]=(Run-Docker @('image','inspect','--format','{{.Id}}',$image)).Trim()
    }
    $env:PGOPTIONS='-c default_transaction_read_only=on -c statement_timeout=10000 -c lock_timeout=2000'
    $env:PGAPPNAME='aicos-gate-c-readonly'
    $env:PGCONNECT_TIMEOUT='5'
    Write-Host 'Enter EXISTING Gate A passwords locally. No password reset, login API, mutation request or SQL migration will run.'
    foreach ($role in $roles) {
        Set-Step "Authenticate $($role.Name)"
        $secrets[$role.Name]=if($cachedSecrets.ContainsKey($role.Name)) {
            $cachedSecrets[$role.Name]
        } else { Read-Host "Existing password: $($role.Name)" -AsSecureString }
        if ($secrets[$role.Name].Length -eq 0) { throw "Empty credential for $($role.Name)." }
        $queries=@('BEGIN READ ONLY;', "SELECT current_database()='aicos_db' AND current_user='$($role.Name)' AND current_schema()='public' AND current_setting('transaction_read_only')='on';")
        foreach ($table in $role.Tables) { $queries += "SELECT * FROM public.$table LIMIT 0;" }
        $queries += 'COMMIT;'
        $result=Read-Database $role @('-c',($queries -join ' '))
        if (@($result -split '\r?\n' | Where-Object {$_ -eq 't'}).Count -ne 1) { throw "Unexpected database/role/schema: $($role.Name)." }
        $state.RolesVerified += $role.Name
        $cachedSecrets[$role.Name]=$secrets[$role.Name]
        Save-CredentialCache
        $raw=Reveal $secrets[$role.Name]
        $encoded=if($role.Name -eq 'ai_svc'){[Uri]::EscapeDataString($raw)}else{'"'+$raw.Replace('"','""')+'"'}
        [Environment]::SetEnvironmentVariable($role.Key,$encoded,'Process')
        $raw=$null; $encoded=$null
        Save-State
    }
    Set-Step 'Verify effective permission matrix'
    $matrix=Read-Database $roles[0] @('-v','expect_login=true','-f',(Join-Path $root 'infra/postgres/security/verify-service-permissions.sql'))
    if ($matrix -notmatch '(?m)^SUMMARY\|\|issue_count\|0\|') { throw 'Privilege matrix failed; no containers started.' }
    $state.Checks += @{Name='11-role privilege matrix';Result='PASS';IssueCount=0}
    $counts=Read-Database $roles[0] @('-c',"BEGIN READ ONLY; SELECT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p')),(SELECT count(*) FROM pg_attribute a JOIN pg_class c ON c.oid=a.attrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p') AND a.attnum>0 AND NOT a.attisdropped); COMMIT;")
    $parts=$counts.Trim() -split '\|'
    $state.PublicTables=[int]$parts[0]; $state.PublicColumns=[int]$parts[1]
    if ($state.PublicTables -ne 38 -or $state.PublicColumns -ne 244) { throw 'Schema counts differ from reviewed Gate B baseline.' }
    $before=Get-ReadSnapshot
    $before | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory 'owned-read-before.json') -Encoding UTF8
    Set-Step 'Configure shared JWT key'
    Initialize-SharedJwtKey
    $accessToken=Read-Host 'Access JWT (khong phai mat khau DB): NHAN ENTER neu chua dang nhap' -AsSecureString
    $env:AICOS_DB_HOST='host.docker.internal'; $env:AICOS_DB_PORT='5432'
    $env:POSTGRES_PASSWORD='unused-external-db-profile'
    $env:PAYMENT_MUTATIONS_ENABLED='false'
    Set-Step 'Validate merged runtime configuration'
    $null=Run-Docker (@('compose')+$compose+@('config','--quiet'))
    $config=(Run-Docker (@('compose')+$compose+@('config','--format','json'))) | ConvertFrom-Json
    if ($config.services.PSObject.Properties.Name -contains 'postgres') { throw 'Bundled PostgreSQL unexpectedly active.' }
    foreach ($role in $roles | Where-Object {$_.Name -ne 'ai_svc'}) {
        $matches=@($config.services.PSObject.Properties | Where-Object {$_.Value.PSObject.Properties.Name -contains 'environment'} | Where-Object {$_.Value.environment.PSObject.Properties.Name -contains 'ConnectionStrings__Default'} | Where-Object {$_.Value.environment.ConnectionStrings__Default -like "*Username=$($role.Name);*"})
        if ($matches.Count -ne 1) { throw "Unexpected runtime role mapping: $($role.Name)." }
        $service=$matches[0].Value
        if ($service.environment.Database__MigrateOnStartup -ne 'false' -or $service.environment.ConnectionStrings__Default -notlike 'Host=host.docker.internal;Port=5432;Database=aicos_db;*') { throw 'Database/migration guard mismatch.' }
    }
    if ($config.services.payment.environment.Payments__MutationsEnabled -ne 'false') { throw 'Payment mutations must stay disabled.' }
    $config=$null
    # No other clients may send business requests during this controlled window.
    Set-Step 'Start Redis'
    $null=Run-Docker (@('compose')+$compose+@('up','-d','--no-deps','--no-build','--pull','never','redis'))
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    do {
        $health=(Run-Docker @('inspect','--format','{{.State.Health.Status}}','aicos-redis')).Trim()
        if ($health -eq 'healthy') { break }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($health -ne 'healthy') { throw 'Redis health failed; no application services started.' }
    $state.Services += @{Name='redis';Result='PASS'}
    foreach ($service in $services) {
        Set-Step "Start/check $($service.Name)"
        $null=Run-Docker (@('compose')+$compose+@('up','-d','--no-deps','--no-build','--pull','never',$service.Name))
        $flag=(Run-Docker @('exec',"aicos-$($service.Name)",'printenv','Database__MigrateOnStartup')).Trim()
        if ($flag -ne 'false') { throw 'Runtime migration flag mismatch.' }
        if ($service.Name -eq 'payment' -and (Run-Docker @('exec','aicos-payment','printenv','Payments__MutationsEnabled')).Trim() -ne 'false') { throw 'Runtime payment maintenance guard mismatch.' }
        Wait-Ready $service.Port
        $base="http://127.0.0.1:$($service.Port)"
        $null=Check-Get "$base/health/live"
        $null=Check-Get "$base/health/ready"
        $info=(Check-Get "$base/api/v1/$($service.Prefix)/_info").Content | ConvertFrom-Json
        if ($info.schema -ne 'public' -or $info.service -ne $service.Name) { throw 'Unexpected service metadata.' }
        if ($service.Read) {
            $null=Check-Get ($base+$service.Read) 401
            if ($accessToken.Length -gt 0) {
                $headers=@{Authorization=('Bearer '+(Reveal $accessToken));'X-Correlation-Id'='gate-c-readonly'}
                try { $null=Check-Get ($base+$service.Read) 200 $headers }
                finally { $headers.Clear() }
            }
        }
        $state.Services += @{Name=$service.Name;Result='PASS';Scope='health, metadata, applicable read routes'}
        Save-State
    }
    Set-Step 'Start/check Gateway'
    $null=Run-Docker (@('compose')+$compose+@('up','-d','--no-deps','--no-build','--pull','never','gateway'))
    $deadline=[DateTime]::UtcNow.AddSeconds(60)
    do {
        try { $null=Invoke-WebRequest -UseBasicParsing 'http://127.0.0.1:8080/health/live' -TimeoutSec 5; break }
        catch { Start-Sleep -Seconds 2 }
    } while ([DateTime]::UtcNow -lt $deadline)
    $null=Check-Get 'http://127.0.0.1:8080/health/live'
    foreach ($service in $services) {
        $base='http://127.0.0.1:8080'
        $info=(Check-Get "$base/api/v1/$($service.Prefix)/_info").Content | ConvertFrom-Json
        if ($info.schema -ne 'public' -or $info.service -ne $service.Name) { throw 'Gateway destination mismatch.' }
        if ($service.Read) {
            $null=Check-Get ($base+$service.Read) 401
            if ($accessToken.Length -gt 0) {
                $headers=@{Authorization=('Bearer '+(Reveal $accessToken));'X-Correlation-Id'='gate-c-readonly'}
                try { $null=Check-Get ($base+$service.Read) 200 $headers }
                finally { $headers.Clear() }
            }
        }
    }
    $state.Services += @{Name='gateway';Result='PASS';Scope='health and six routed metadata groups'}
    if ($accessToken.Length -gt 0) { $state.AuthenticatedSmoke='PASS for Identity/Menu/Table/Kitchen GET only; Ordering/Payment positive paths NOT RUN without fixtures' }
    Set-Step 'Compare approved-table read snapshots'
    $after=Get-ReadSnapshot
    $after | ConvertTo-Json | Set-Content (Join-Path $EvidenceDirectory 'owned-read-after.json') -Encoding UTF8
    $state.SnapshotUnchanged=($before.Count -eq $after.Count)
    foreach ($key in $before.Keys) { if ($before[$key] -ne $after[$key]) { $state.SnapshotUnchanged=$false } }
    if (-not $state.SnapshotUnchanged) { throw 'Approved-table snapshot changed; investigate without undoing data.' }
    $state.Checks += @{Name='Approved readable table snapshots';Result='PASS';Tables=$before.Count}
    Set-Step 'Remove encrypted credential cache'
    Remove-Item -LiteralPath $CredentialCachePath -Force
    $state.Status='CompletedLimitedReadOnlySmoke'
    $state.Step='AwaitingAgentReview'
    Write-Host 'Controlled read-only runtime checks finished. Payment mutations remain disabled. No Phase 6.'
} catch {
    $state.Status='Failed'
    $state.ErrorType=$_.Exception.GetType().FullName
    $state.ErrorLine=$_.InvocationInfo.ScriptLineNumber
    $state.Error="Stopped at: $($state.Step). No later service started. Investigate locally; no secret/error dump saved."
    Write-Host $state.Error -ForegroundColor Red
} finally {
    if ($state.Status -eq 'Failed' -and $state.Step -like 'Start/check *') {
        $state.Services += @{Name=($state.Step -replace '^Start/check ','');Result='FAIL'}
    }
    Save-State
    foreach ($secret in $secrets.Values) { $secret.Dispose() }
    foreach ($secret in $cachedSecrets.Values) { $secret.Dispose() }
    if ($accessToken) { $accessToken.Dispose() }
    foreach ($key in $previousEnv.Keys) { [Environment]::SetEnvironmentVariable($key,$previousEnv[$key],'Process') }
    $raw=$null; $encoded=$null; $config=$null; $headers=$null; $output=$null; $matrix=$null
    $cachedSecrets=@{}
    Pop-Location
    $null=Read-Host 'Press Enter to close this console'
}
