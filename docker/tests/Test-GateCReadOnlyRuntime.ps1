# Offline orchestration regression tests. No database connection or container start.
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$helper=Join-Path $root 'docker/Invoke-GateCReadOnlyRuntime.ps1'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Runtime helper syntax invalid'}
Push-Location $root
try {
    $global:gateCTestConfig=(& docker compose --env-file .env.example -f docker-compose.yml -f docker-compose.external-db.yml -f docker-compose.gate-c.yml config --format json) -join [Environment]::NewLine
    if($LASTEXITCODE -ne 0){throw 'Example Compose validation failed'}
} finally {Pop-Location}
$replacements=@{
'Run-Docker'=@'
function Run-Docker([string[]]$Arguments) {
    if($Arguments -contains 'up') {
        $name=$Arguments[-1]
        $global:gateCTestStarted.Add($name)
        if($name -eq $global:gateCTestFailService){throw 'Synthetic service startup failure'}
        return ''
    }
    if($Arguments -contains 'json'){return $global:gateCTestConfig}
    if($Arguments[0] -eq 'info'){return '29.7.2'}
    if($Arguments[0] -eq 'ps'){return "vietai_redis`nvietai_mongodb`nvietai_ollama"}
    if($Arguments[0] -eq 'image'){return 'sha256:synthetic-test-image'}
    if($Arguments[0] -eq 'exec'){return 'false'}
    if($Arguments[0] -eq 'inspect'){return 'healthy'}
    return ''
}
'@
'Read-Database'=@'
function Read-Database($Role,[string[]]$Arguments) {
    if($Arguments -contains '-f'){return 'SUMMARY||issue_count|0|||'}
    if(($Arguments -join ' ') -match 'pg_class'){return '38|244'}
    return 't'
}
'@
'Get-ReadSnapshot'=@'
function Get-ReadSnapshot {return @{synthetic_table='synthetic_table|0|synthetic-hash'}}
'@
'Check-Get'=@'
function Check-Get([string]$Url,[int]$Expected=200,[hashtable]$Headers=@{}) {
    $map=@{identity='identity';menu='menu';payments='payment';tables='table';orders='order';kitchen='kitchen'}
    $service=if($Url -like '*_info'){$prefix=($Url -split '/api/v1/')[1] -split '/'; $map[$prefix[0]]}else{'unused'}
    $state.Checks+=@{Url=$Url;Status=$Expected;Result='MOCK'}
    return [pscustomobject]@{StatusCode=$Expected;Content=(@{schema='public';service=$service}|ConvertTo-Json -Compress)}
}
'@
}
$source=Get-Content -LiteralPath $helper -Raw
$nodes=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in @('Run-Docker','Read-Database','Get-ReadSnapshot','Check-Get')},$true))
foreach($node in ($nodes|Sort-Object {$_.Extent.StartOffset} -Descending)){
    $source=$source.Remove($node.Extent.StartOffset,$node.Extent.EndOffset-$node.Extent.StartOffset).Insert($node.Extent.StartOffset,$replacements[$node.Name])
}
$source=$source.Replace('$root = (Resolve-Path (Join-Path $PSScriptRoot ''..'')).Path',('$root = '''+$root.Replace("'","''")+''''))
$run=[scriptblock]::Create($source)
function docker {$global:LASTEXITCODE=0}
function Invoke-WebRequest {return [pscustomobject]@{StatusCode=200;Content='Healthy'}}
function Read-Host {
    param([string]$Prompt,[switch]$AsSecureString)
    if(-not $AsSecureString){return ''}
    if($Prompt -like 'Existing password:*'){
        $global:gateCTestPasswordPrompts++
        return (ConvertTo-SecureString 'SYNTHETIC-TEST-ONLY-NOT-A-REAL-PASSWORD' -AsPlainText -Force)
    }
    return (New-Object Security.SecureString)
}
$testRoot=Join-Path $env:LOCALAPPDATA ('AICOS/deployments/gate_c_helper_test_'+(Get-Date -Format 'yyyyMMdd_HHmmss'))
$null=New-Item -ItemType Directory -Path $testRoot
function Invoke-Case([string]$Name,[string]$FailService,[string]$Cache=''){
    $caseDir=Join-Path $testRoot $Name
    $null=New-Item -ItemType Directory -Path $caseDir
    $global:gateCTestStarted=New-Object 'Collections.Generic.List[string]'
    $global:gateCTestPasswordPrompts=0
    $global:gateCTestFailService=$FailService
    if($Cache){$null=&$run -EvidenceDirectory $caseDir -CredentialCachePath $Cache}
    else{$null=&$run -EvidenceDirectory $caseDir}
    $state=Get-Content (Join-Path $caseDir 'runtime-result.json') -Raw|ConvertFrom-Json
    return @{State=$state;Directory=$caseDir;Started=@($global:gateCTestStarted.ToArray());Prompts=$global:gateCTestPasswordPrompts}
}
try {
    $ok=Invoke-Case 'success' ''
    if($ok.State.Status -ne 'CompletedLimitedReadOnlySmoke'){throw "Success simulation failed at $($ok.State.Step): $($ok.State.ErrorType) line $($ok.State.ErrorLine)"}
    if(($ok.Started -join ',') -ne 'redis,identity,menu,payment,table,order,kitchen,gateway'){throw 'Startup order mismatch'}
    if($ok.Prompts -ne 11){throw 'Expected eleven initial credential prompts'}
    if(Test-Path (Join-Path $ok.Directory 'gate-c-roles.dpapi.xml')){throw 'Successful run did not delete encrypted cache'}
    Write-Host 'PASS: full mocked success, merged Compose StrictMode checks, eight ordered starts and cache cleanup'

    $failed=Invoke-Case 'fail-menu' 'menu'
    if($failed.State.Status -ne 'Failed' -or $failed.State.Step -ne 'Start/check menu'){throw 'Injected failure was not recorded'}
    if(($failed.Started -join ',') -ne 'redis,identity,menu'){throw 'Later service started after failure'}
    $cache=Join-Path $failed.Directory 'gate-c-roles.dpapi.xml'
    if(-not(Test-Path $cache)){throw 'Encrypted retry cache missing'}
    $xml=Get-Content $cache -Raw
    if($xml.Contains('SYNTHETIC-TEST-ONLY-NOT-A-REAL-PASSWORD')){throw 'Plaintext credential leak'}
    Write-Host 'PASS: injected Menu failure stops all later services; retry cache is encrypted'

    $retry=Invoke-Case 'retry-cache' '' $cache
    if($retry.State.Status -ne 'CompletedLimitedReadOnlySmoke' -or $retry.Prompts -ne 0){throw 'DPAPI retry did not reuse credentials safely'}
    if(Test-Path $cache){throw 'Retry success did not delete encrypted cache'}
    Write-Host 'PASS: Windows DPAPI cache round-trip, zero repeated password prompts, successful cleanup'
    Write-Host 'Offline simulation only: NO real SQL, HTTP or container startup; not a production runtime PASS.'
} finally {
    Remove-Item Function:docker,Function:Invoke-WebRequest,Function:Read-Host -ErrorAction SilentlyContinue
    Remove-Variable -Scope Global -Name gateCTestConfig,gateCTestStarted,gateCTestPasswordPrompts,gateCTestFailService -ErrorAction SilentlyContinue
}
