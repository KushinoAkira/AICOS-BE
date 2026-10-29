param(
    [Parameter(Mandatory)][string]$InfrastructureRunDirectory,
    [ValidateSet('Preflight','Apply','Verify')][string]$Action='Preflight',
    [Parameter(Mandatory)][string]$EvidenceDirectory
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'GateD.Common.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'GateD.SchemaPackage.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$privateRoot=Join-Path $env:LOCALAPPDATA 'AICOS/gate-d'
$infra=(Resolve-Path -LiteralPath $InfrastructureRunDirectory).Path
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
foreach($path in @($infra,$evidence)){
    if(-not $path.StartsWith($privateRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must stay in private Gate D directory'}
}
if((Split-Path $infra -Leaf) -notmatch '^d1a_\d{8}_\d{6}$' -or (Split-Path $evidence -Leaf) -notmatch '^d1b_\d{8}_\d{6}$'){throw 'Invalid stage directory'}
$manifest=Get-Content (Join-Path $infra 'manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($manifest.Stage -cne 'D1-A' -or $manifest.BusinessExecutionAuthorized -ne $false -or $manifest.Transport -cne 'DockerExecPinned'){throw 'Invalid infrastructure approval boundary'}
$package=Get-GateDSchemaPackage $repo
$roles=@('identity_svc','menu_svc','table_reservation_svc','ordering_svc','payment_svc','inventory_svc','kitchen_svc','workforce_svc','reporting_svc','customer_svc','ai_svc')
$roleList=($roles|ForEach-Object {"'$_'"}) -join ','
$expectedTables=@();$expectedColumns=@()
$grantPattern='(?s)GRANT (SELECT(?:, INSERT)?|INSERT|UPDATE \([^;]+?\))\s+ON\s+(.*?)\s+TO (\w+);'
foreach($grant in [regex]::Matches($package.Texts['02_runtime_acl.review.sql'],$grantPattern)){
    $privileges=$grant.Groups[1].Value;$targets=$grant.Groups[2].Value;$role=$grant.Groups[3].Value
    if($role -notin $roles){throw 'Unexpected role in grant matrix'}
    foreach($target in ($targets -split ',\s*')){
        if($target.Trim() -notmatch '^public\.(\w+)$'){throw 'Unexpected table in grant matrix'}
        $table=$Matches[1]
        if($privileges -match '(?s)^UPDATE \((.+)\)$'){
            foreach($column in ($Matches[1] -split ',\s*')){$expectedColumns+="('$role','$table','$($column.Trim())','UPDATE')"}
        } else {
            foreach($privilege in ($privileges -split ', ')){$expectedTables+="('$role','$table','$privilege')"}
        }
    }
}
if($expectedTables.Count -ne 44 -or $expectedColumns.Count -ne 18){throw ('Unexpected reviewed grant matrix counts: '+$expectedTables.Count+'/'+$expectedColumns.Count)}

function Assert-PinnedTarget {
    $state=Get-GateDResourceState
    Assert-GateDResourceIdentity $manifest $state
    $control=Invoke-GateDDocker @('exec',$manifest.Postgres.Id,'pg_controldata','/var/lib/postgresql/18/docker')
    $id=[regex]::Match($control,'Database system identifier:\s*(\d+)').Groups[1].Value
    Assert-GateDTarget 'Host=gated-postgres;Port=5432;Database=aicos_payment_test;Username=postgres' $id $manifest.SystemIdentifier -Transport DockerExec -ContainerId $state.Postgres.Id -ExpectedContainerId $manifest.Postgres.Id
    Assert-GateDProtectedUnchanged $manifest.ProtectedContainers
}

function Invoke-PinnedSql {
    param([Parameter(Mandatory)][string]$Sql,[switch]$Write)
    Assert-PinnedTarget
    if($Sql.Contains('GATED_D1B_SQL')){throw 'SQL contains runner delimiter'}
    $readOnly=if($Write){'off'}else{'on'}
    $guard=$package.Texts['00_target_guard.sql']
    $prefix="SET aicos.gated_expected_system_identifier = '$($manifest.SystemIdentifier)';`nSET aicos.gated_approved_stage = 'D1-B';`nSET search_path=public;`n$guard`n"
    $shell=@'
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
export PGCONNECT_TIMEOUT=5
export PGAPPNAME=aicos-gated-d1b
export PGOPTIONS='-c default_transaction_read_only=READ_ONLY -c statement_timeout=30000 -c lock_timeout=2000'
exec psql -X -w -qAt -h gated-postgres -p 5432 -U postgres -d aicos_payment_test -v ON_ERROR_STOP=1 -f - <<'GATED_D1B_SQL'
'@
    $shell=$shell.Replace('READ_ONLY',$readOnly)+"`n"+$prefix+$Sql+"`nGATED_D1B_SQL`n"
    Invoke-GateDCommand -FilePath ((Get-Command docker.exe).Source) -Arguments @('exec','-i',$manifest.Postgres.Id,'sh','-s') -InputText $shell
}

function Get-PinnedSchema {
    Assert-PinnedTarget
    $shell=@'
set -eu
export PGPASSWORD="$POSTGRES_PASSWORD"
export PGOPTIONS='-c default_transaction_read_only=on'
exec pg_dump -h gated-postgres -p 5432 -U postgres -d aicos_payment_test --schema-only --no-owner --no-privileges
'@
    Invoke-GateDCommand -FilePath ((Get-Command docker.exe).Source) -Arguments @('exec','-i',$manifest.Postgres.Id,'sh','-s') -InputText $shell
}

$emptyAssertion=@'
DO $empty$
BEGIN
 IF EXISTS(SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','S','f'))
 OR EXISTS(SELECT 1 FROM pg_roles WHERE rolname IN (ROLE_LIST))
 OR EXISTS(SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public')
 OR EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid())
 THEN RAISE EXCEPTION 'Test target not empty/quiescent; stop, no overwrite'; END IF;
END $empty$;
'@
$emptyAssertion=$emptyAssertion.Replace('ROLE_LIST',$roleList)
$integrityAssertion=@'
DO $integrity$
DECLARE t record; amount bigint;
BEGIN
 IF (SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE')<>38
 OR (SELECT count(*) FROM information_schema.columns WHERE table_schema='public')<>244
 OR (SELECT count(*) FROM pg_roles WHERE rolname IN (ROLE_LIST))<>11
 OR EXISTS(SELECT 1 FROM pg_authid WHERE rolname IN (ROLE_LIST) AND (rolcanlogin OR rolpassword IS NOT NULL OR rolsuper OR rolcreatedb OR rolcreaterole OR rolinherit OR rolreplication OR rolbypassrls))
 OR EXISTS(SELECT 1 FROM pg_constraint WHERE connamespace='public'::regnamespace AND NOT convalidated)
 OR EXISTS(SELECT 1 FROM pg_index i JOIN pg_class indexed_table ON indexed_table.oid=i.indrelid WHERE indexed_table.relnamespace='public'::regnamespace AND NOT i.indisvalid)
 THEN RAISE EXCEPTION 'Schema/role integrity failed'; END IF;
 FOR t IN SELECT tablename FROM pg_tables WHERE schemaname='public' LOOP
  EXECUTE format('SELECT count(*) FROM public.%I',t.tablename) INTO amount;
  IF amount<>0 THEN RAISE EXCEPTION 'Business rows found; no fixtures authorized'; END IF;
 END LOOP;
END $integrity$;
'@
$integrityAssertion=$integrityAssertion.Replace('ROLE_LIST',$roleList)
$matrixQuery=@'
WITH expected_tables(role_name,table_name,privilege) AS (VALUES TABLE_VALUES),
expected_columns(role_name,table_name,column_name,privilege) AS (VALUES COLUMN_VALUES),
service_roles AS(SELECT oid,rolname FROM pg_roles WHERE rolname IN (ROLE_LIST)),
tables AS(SELECT oid,relname FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='r'),
table_errors AS(
 SELECT r.rolname,t.relname,p.privilege FROM service_roles r CROSS JOIN tables t
 CROSS JOIN (VALUES('SELECT'),('INSERT'),('UPDATE'),('DELETE'),('TRUNCATE'),('REFERENCES'),('TRIGGER'),('MAINTAIN')) p(privilege)
 WHERE has_table_privilege(r.oid,t.oid,p.privilege) IS DISTINCT FROM EXISTS(SELECT 1 FROM expected_tables e WHERE e.role_name=r.rolname AND e.table_name=t.relname AND e.privilege=p.privilege)),
column_errors AS(
 SELECT r.rolname,t.relname,a.attname,p.privilege FROM service_roles r CROSS JOIN tables t
 JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum>0 AND NOT a.attisdropped
 CROSS JOIN (VALUES('SELECT'),('INSERT'),('UPDATE'),('REFERENCES')) p(privilege)
 WHERE has_column_privilege(r.oid,t.oid,a.attnum,p.privilege) IS DISTINCT FROM (
 EXISTS(SELECT 1 FROM expected_tables e WHERE e.role_name=r.rolname AND e.table_name=t.relname AND e.privilege=p.privilege)
 OR EXISTS(SELECT 1 FROM expected_columns e WHERE e.role_name=r.rolname AND e.table_name=t.relname AND e.column_name=a.attname AND e.privilege=p.privilege))),
capability_errors AS(
 SELECT r.rolname FROM service_roles r WHERE NOT has_database_privilege(r.oid,current_database(),'CONNECT')
 OR has_database_privilege(r.oid,current_database(),'CREATE') OR NOT has_schema_privilege(r.oid,'public','USAGE') OR has_schema_privilege(r.oid,'public','CREATE')
 OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=r.oid OR roleid=r.oid)
 OR EXISTS(SELECT 1 FROM pg_shdepend WHERE refclassid='pg_authid'::regclass AND refobjid=r.oid AND deptype='o')
 OR EXISTS(SELECT 1 FROM pg_class c CROSS JOIN LATERAL aclexplode(c.relacl) a WHERE a.grantee=r.oid AND a.is_grantable)
 OR EXISTS(SELECT 1 FROM pg_attribute c CROSS JOIN LATERAL aclexplode(c.attacl) a WHERE a.grantee=r.oid AND a.is_grantable))
SELECT json_build_object(
 'TablePrivilegeErrors',(SELECT count(*) FROM table_errors),
 'ColumnPrivilegeErrors',(SELECT count(*) FROM column_errors),
 'CapabilityErrors',(SELECT count(*) FROM capability_errors),
 'Roles',(SELECT json_agg(json_build_object('Role',rolname,'Login',rolcanlogin,'ConnectionLimit',rolconnlimit,'EffectiveTemporary',has_database_privilege(oid,current_database(),'TEMPORARY')) ORDER BY rolname) FROM pg_roles WHERE rolname IN (ROLE_LIST)),
 'Tables',(SELECT count(*) FROM tables),
 'Columns',(SELECT count(*) FROM information_schema.columns WHERE table_schema='public'),
 'PK',(SELECT count(*) FROM pg_constraint WHERE connamespace='public'::regnamespace AND contype='p'),
 'FK',(SELECT count(*) FROM pg_constraint WHERE connamespace='public'::regnamespace AND contype='f'),
 'UniqueConstraints',(SELECT count(*) FROM pg_constraint WHERE connamespace='public'::regnamespace AND contype='u'),
 'Checks',(SELECT count(*) FROM pg_constraint WHERE connamespace='public'::regnamespace AND contype='c'),
 'Indexes',(SELECT count(*) FROM pg_indexes WHERE schemaname='public'),
 'Sequences',(SELECT count(*) FROM pg_class WHERE relnamespace='public'::regnamespace AND relkind='S'),
 'TotalRows',0);
'@
$matrixQuery=$matrixQuery.Replace('ROLE_LIST',$roleList).Replace('TABLE_VALUES',($expectedTables -join ',')).Replace('COLUMN_VALUES',($expectedColumns -join ','))

Assert-PinnedTarget
if($Action -eq 'Preflight'){
    if(Test-Path -LiteralPath $evidence){throw 'Use a new evidence directory; no overwrite'}
    $cache=Import-Clixml (Join-Path $infra 'infra-secrets.dpapi.xml')
    try {$probe=Invoke-GateDReadOnlyProbe $manifest $cache.PostgresAdminPassword} finally {foreach($v in $cache.Values){$v.Dispose()}}
    $null=Invoke-PinnedSql ("BEGIN READ ONLY;`n"+$emptyAssertion+"`nCOMMIT;")
    $checkpoint=Get-PinnedSchema
    $null=New-Item -ItemType Directory -Path $evidence
    Protect-GateDPrivatePath $evidence
    $checkpoint|Set-Content (Join-Path $evidence 'before-schema.sql') -Encoding UTF8
    $before=Get-GateDPublicSnapshot (Get-GateDProtectedSnapshot)
    $result=@{Stage='D1-B';Status='PASS_PREFLIGHT';Database=$manifest.Database;ContainerId=$manifest.Postgres.Id;SystemIdentifier=$manifest.SystemIdentifier;Probe=$probe;SqlHashes=$package.Hashes;Ddl=$package.Inventory;Roles=$roles;ExpectedTablePrivileges=$expectedTables.Count;ExpectedColumnPrivileges=$expectedColumns.Count;ProtectedContainers=$before;SchemaApplied=$false;BusinessExecutionAuthorized=$false;Rollback='Atomic transaction; preserve state on post-commit failure, no automatic DROP/restore'}
    $result|ConvertTo-Json -Depth 10|Set-Content (Join-Path $evidence 'preflight.json') -Encoding UTF8
    [pscustomobject]$result|Select-Object Status,Database,SystemIdentifier,Ddl,ExpectedTablePrivileges,ExpectedColumnPrivileges|ConvertTo-Json -Depth 5
    exit
}
$preflight=Get-Content (Join-Path $evidence 'preflight.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($preflight.Status -cne 'PASS_PREFLIGHT' -or $preflight.ContainerId -cne $manifest.Postgres.Id -or $preflight.SystemIdentifier -cne $manifest.SystemIdentifier -or $preflight.BusinessExecutionAuthorized -ne $false){throw 'Preflight evidence mismatch'}
foreach($name in $package.Hashes.Keys){if($package.Hashes[$name] -cne $preflight.SqlHashes.$name){throw 'SQL changed after preflight'}}
Assert-GateDProtectedUnchanged $preflight.ProtectedContainers
if($Action -eq 'Apply'){
    if(Test-Path (Join-Path $evidence 'apply-started.json')){throw 'Prior apply attempt exists; inspect state, never blind replay'}
    @{Stage='D1-B';Status='STARTED';TimeUtc=[DateTime]::UtcNow}|ConvertTo-Json|Set-Content (Join-Path $evidence 'apply-started.json') -Encoding UTF8
    $sql="BEGIN;`n"+$emptyAssertion+"`n"+$package.Texts.schema+"`nSET search_path=public;`nSET statement_timeout=30000;`nSET lock_timeout=2000;`n"
    foreach($name in @('01_runtime_roles.review.sql','02_runtime_acl.review.sql')){
        $part=$package.Texts[$name].Replace('\ir 00_target_guard.sql',$package.Texts['00_target_guard.sql'])
        # Reviewed files contain their own standalone transactions. Keep exactly one
        # outer transaction for schema + roles + ACL; no nested/early COMMIT.
        $part=[regex]::Replace($part,'(?m)^(BEGIN|COMMIT);\s*$','')
        $sql+=$part+"`n"
    }
    $sql+=$integrityAssertion+"`nCOMMIT;`nSELECT 'D1B_COMMITTED';"
    $output=Invoke-PinnedSql $sql -Write
    if(($output -split '\r?\n') -cnotcontains 'D1B_COMMITTED'){throw 'Commit marker missing; inspect state before any retry'}
    @{Status='COMMITTED';SchemaApplied=$true;Roles=11;Login=$false}|ConvertTo-Json|Set-Content (Join-Path $evidence 'commit-result.json') -Encoding UTF8
}
$actualSql=Get-PinnedSchema
$actualSql|Set-Content (Join-Path $evidence 'after-schema.sql') -Encoding UTF8
$exact=(ConvertTo-GateDCanonicalSchema $actualSql) -ceq (ConvertTo-GateDCanonicalSchema $package.Texts.schema)
$same=(ConvertTo-GateDCanonicalSchema $actualSql -NormalizeReviewedTextArrayCasts) -ceq (ConvertTo-GateDCanonicalSchema $package.Texts.schema -NormalizeReviewedTextArrayCasts)
if(-not $same){throw 'Schema definition differs from baseline; preserve evidence, no automatic rollback'}
$actual=Invoke-PinnedSql ("BEGIN READ ONLY;`n"+$integrityAssertion+"`n"+$matrixQuery+"`nCOMMIT;")|ConvertFrom-Json
$actual|ConvertTo-Json -Depth 8|Set-Content (Join-Path $evidence 'schema-acl-verification.json') -Encoding UTF8
if($actual.TablePrivilegeErrors -ne 0 -or $actual.ColumnPrivilegeErrors -ne 0 -or $actual.CapabilityErrors -ne 0 -or $actual.Sequences -ne 0){throw 'ACL mismatch; stop, preserve evidence'}
if($actual.PK -ne $package.Inventory.PrimaryKeys -or $actual.FK -ne $package.Inventory.ForeignKeys -or $actual.Indexes -ne $package.Inventory.TotalIndexes -or $actual.Checks -ne $package.Inventory.Checks -or $actual.UniqueConstraints -ne $package.Inventory.UniqueConstraints){throw 'Catalog inventory mismatch'}
# Exercise SELECT as each actual NOLOGIN role via SET LOCAL ROLE. This is not
# password authentication and does not enable login or create application users.
$roleProbe="BEGIN READ ONLY;`n"
foreach($role in $roles){
    $roleProbe+="SET LOCAL ROLE $role;`n"
    $allowed=@($expectedTables|Where-Object {$_ -match ("^\('"+$role+"','\w+','SELECT'\)$")})
    foreach($entry in $allowed){
        $null=$entry -match "^\('\w+','(\w+)','SELECT'\)$"
        $roleProbe+='SELECT 1 FROM public.'+$Matches[1]+" LIMIT 0;`n"
    }
    $roleProbe+="SELECT json_build_object('Role',current_user,'SelectTables',$($allowed.Count));`nRESET ROLE;`n"
}
$roleProbe+="COMMIT;"
$roleChecks=@((Invoke-PinnedSql $roleProbe) -split '\r?\n'|Where-Object {$_}|ForEach-Object {$_|ConvertFrom-Json})
if($roleChecks.Count -ne 11 -or ($roleChecks|Measure-Object SelectTables -Sum).Sum -ne 28){throw 'Real-role SELECT checks incomplete'}
$roleChecks|ConvertTo-Json -Depth 4|Set-Content (Join-Path $evidence 'role-select-probes.json') -Encoding UTF8
# Constant SELECTs on real PG18 verify the eight explicitly reviewed cast forms.
$castProbe="BEGIN READ ONLY;`n"
foreach($line in ($package.Texts.schema -split '\r?\n')){
    if($line -match '(\(ARRAY\[((?:''[A-Z_]+''::character varying(?:, )?)+)\]\)::text\[\])'){
        $old=$Matches[1];$elements=$Matches[2]
        $new='ARRAY['+([regex]::Replace($elements,"('[A-Z_]+'::character varying)",'($1)::text'))+']'
        $castProbe+="SELECT (($old) IS NOT DISTINCT FROM ($new)) AND pg_typeof($old)=pg_typeof($new);`n"
    }
}
$castProbe+="COMMIT;"
$castChecks=@((Invoke-PinnedSql $castProbe) -split '\r?\n'|Where-Object {$_})
if($castChecks.Count -ne 8 -or @($castChecks|Where-Object {$_ -cne 't'}).Count){throw 'Reviewed CHECK cast equivalence failed'}
@{Count=8;Status='PASS';RealPostgreSql=$true;DataMutation=$false}|ConvertTo-Json|Set-Content (Join-Path $evidence 'cast-equivalence.json') -Encoding UTF8
Assert-GateDProtectedUnchanged $preflight.ProtectedContainers
$result=@{Stage='D1-B';Status='PASS_SCHEMA_ACL';Database=$manifest.Database;SystemIdentifier=$manifest.SystemIdentifier;ExactDumpMatch=$exact;SchemaDefinitionMatch=$same;ReviewedDifference='8 constant text-array CHECK casts: PG18.4 versus PG18.6';Actual=$actual;SqlHashes=$package.Hashes;SchemaApplied=$true;RuntimeRolesCreated=$true;RuntimeLoginEnabled=$false;FixturesCreated=$false;JwtCreated=$false;BusinessExecutionAuthorized=$false;ProtectedGateC='13 unchanged; Redis healthy';WindowsDatabaseConnection=$false}
$result|ConvertTo-Json -Depth 10|Set-Content (Join-Path $evidence 'result.json') -Encoding UTF8
$stageManifest=@{Version=1;Stage='D1-B';InfrastructureManifest=(Join-Path $infra 'manifest.json');Project=$manifest.Project;Database=$manifest.Database;Transport=$manifest.Transport;SystemIdentifier=$manifest.SystemIdentifier;NetworkId=$manifest.NetworkId;Postgres=$manifest.Postgres;Redis=$manifest.Redis;ExpectedTables=38;ExpectedColumns=244;ExpectedServiceRoles=11;SchemaApplied=$true;RuntimeRolesCreated=$true;RuntimeLoginEnabled=$false;FixturesCreated=$false;BusinessExecutionAuthorized=$false;SqlHashes=$package.Hashes}
$stageManifest|ConvertTo-Json -Depth 8|Set-Content (Join-Path $evidence 'manifest.json') -Encoding UTF8
[pscustomobject]$result|Select-Object Stage,Status,Database,SystemIdentifier,SchemaDefinitionMatch,Actual,ProtectedGateC|ConvertTo-Json -Depth 8
