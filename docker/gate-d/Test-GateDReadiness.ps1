param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'GateD.Common.psm1') -Force
$resolved=(Resolve-Path -LiteralPath $RunDirectory).Path
$privateRoot=Join-Path $env:LOCALAPPDATA 'AICOS\gate-d'
if(-not $resolved.StartsWith($privateRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
   (Split-Path $resolved -Leaf) -notmatch '^d1a_[0-9]{8}_[0-9]{6}$'){throw 'Use the private Gate D1-A run directory'}
$manifest=Get-Content (Join-Path $resolved 'manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
$cache=Import-Clixml (Join-Path $resolved 'infra-secrets.dpapi.xml')
try {
    if($manifest.Stage -cne 'D1-A' -or $manifest.BusinessExecutionAuthorized -ne $false){throw 'D1-A readiness-only manifest required'}
    $actual=Invoke-GateDReadOnlyProbe $manifest $cache.PostgresAdminPassword
    Assert-GateDProtectedUnchanged $manifest.ProtectedContainers
    $result=[pscustomobject]@{Status='PASS_INFRA_ONLY';RealPostgreSql=$true;Probe=$actual;ProtectedGateC='Unchanged';BusinessTests='NOT RUN';SchemaAcl='NOT APPLIED';ApplicationAccountsJwt='NOT CREATED'}
    $result|ConvertTo-Json -Depth 5|Set-Content (Join-Path $resolved 'readiness-result.json') -Encoding UTF8
    $result|ConvertTo-Json -Depth 5
} finally {foreach($value in $cache.Values){$value.Dispose()}}

