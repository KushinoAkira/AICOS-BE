[CmdletBinding()]
param([switch]$Run,[switch]$IncludeUpstream,[switch]$IncludeRuntimeCompatibility)
$ErrorActionPreference='Stop'
$schemas=[ordered]@{aicos_ordering='ordering';aicos_kitchen='kitchen';aicos_payment='payment';aicos_reporting='reporting'}
if($IncludeUpstream-or$IncludeRuntimeCompatibility){$schemas.Add('aicos_identity','identity');$schemas.Add('aicos_workforce','workforce')}
if($IncludeRuntimeCompatibility){$schemas.Add('aicos_menu','menu');$schemas.Add('aicos_customer','customer')}
$databases=@($schemas.Keys)
$databaseList=($databases|ForEach-Object {"'$_'"})-join','
if(-not$Run){
    Write-Output 'PLAN ONLY: after separate maintenance approval, require main application containers stopped, verify cluster, then back up the selected four service databases (six with -IncludeUpstream, eight with -IncludeRuntimeCompatibility) to a private LOCALAPPDATA folder. No database, container or file changed.'
    return
}
$inspection=docker inspect aicos-main-postgres | ConvertFrom-Json
if($LASTEXITCODE-ne0-or-not$inspection[0].Id.StartsWith('c014190eed2d')){throw 'Unexpected PostgreSQL container'}
$pg=$inspection[0]
$writers=@('identity','menu','table','order','kitchen','payment','reporting','workforce','customer','ai','gateway')
foreach($service in $writers){
    $container=docker inspect ('aicos-main-'+$service) | ConvertFrom-Json
    if($LASTEXITCODE-ne0-or$container[0].State.Running){throw 'Approved maintenance pause required. This script never stops application containers.'}
}
$admin=(@($pg.Config.Env|Where-Object {$_-match'^POSTGRES_USER='})-replace'^POSTGRES_USER=','')|Select-Object -First 1
if(-not$admin){$admin='postgres'}
if($admin-notmatch'^[A-Za-z_][A-Za-z0-9_]*$'){throw 'Unexpected administrator name'}
$identity=docker exec $pg.Id psql -XqAt -v ON_ERROR_STOP=1 -U $admin -d postgres -c "BEGIN READ ONLY; SELECT system_identifier::text FROM pg_control_system(); SELECT count(*) FROM pg_stat_activity WHERE datname IN ($databaseList) AND pid<>pg_backend_pid() AND backend_type='client backend'; COMMIT;"
if($LASTEXITCODE-ne0-or$identity[0]-ne'7703616372368646182'-or$identity[1]-ne'0'){throw 'Cluster mismatch or remaining database connections; backup stopped'}
$base=[IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'AICOS\backups'))
if(Test-Path $base){if((Get-Item -LiteralPath $base).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Backup root must not be a reparse point'}}
$backupPath=[IO.Path]::GetFullPath((Join-Path $base ('dashboard-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))))
if(-not$backupPath.StartsWith($base+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Backup path escaped approved root'}
$null=New-Item -ItemType Directory -Path $backupPath -Force
$security=New-Object Security.AccessControl.DirectorySecurity
$security.SetAccessRuleProtection($true,$false)
foreach($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User,(New-Object Security.Principal.SecurityIdentifier 'S-1-5-18'))){
    $rule=New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
    $security.AddAccessRule($rule)
}
Set-Acl -LiteralPath $backupPath -AclObject $security
function Read-BackupSnapshot([string]$Database,[string]$Schema){
    $guard="DO " + '$$' + " BEGIN IF current_database()<>'$Database' OR (SELECT system_identifier::text FROM pg_control_system())<>'7703616372368646182' THEN RAISE EXCEPTION 'Backup target changed'; END IF; END " + '$$' + ";"
    function Read-Query([string]$Query){
        $lines=("BEGIN READ ONLY; SET LOCAL timezone='UTC'; "+$guard+$Query+" COMMIT;") | docker exec -i $pg.Id psql -XqAt -v ON_ERROR_STOP=1 -U $admin -d $Database
        if($LASTEXITCODE-ne0){throw 'Backup inventory query failed; no raw error persisted'}
        return (($lines-join [char]10)|ConvertFrom-Json)
    }
    $columns=Read-Query "SELECT json_agg(json_build_object('table',c.relname,'name',a.attname,'type',format_type(a.atttypid,a.atttypmod),'nullable',NOT a.attnotnull) ORDER BY c.relname,a.attnum) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped WHERE n.nspname='$Schema' AND c.relkind='r';"
    $constraints=Read-Query "SELECT json_agg(json_build_object('table',c.relname,'name',k.conname,'definition',pg_get_constraintdef(k.oid),'validated',k.convalidated) ORDER BY c.relname,k.conname) FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='$Schema';"
    $indexes=Read-Query "SELECT json_agg(json_build_object('table',tablename,'name',indexname,'definition',indexdef) ORDER BY tablename,indexname) FROM pg_indexes WHERE schemaname='$Schema';"
    $fingerprints=@{}
    foreach($table in @($columns.table|Sort-Object -Unique)){
        if($table-notmatch'^[a-z_][a-z0-9_]*$'){throw 'Unexpected table identifier'}
        $fingerprints[$table]=Read-Query ("SELECT json_build_object('rows',count(*),'hash',md5(COALESCE(string_agg(row_to_json(t)::text,chr(10) ORDER BY row_to_json(t)::text COLLATE ""C""),''))) FROM $Schema.$table t;")
    }
    return [pscustomobject]@{Columns=@($columns);Constraints=@($constraints);Indexes=@($indexes);Fingerprints=$fingerprints}
}
$manifest=@()
foreach($database in $databases){
    $schema=$schemas[$database]
    $snapshot=Read-BackupSnapshot -Database $database -Schema $schema
    $file=Join-Path $backupPath ($database+'.dump')
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=(Get-Command docker -ErrorAction Stop).Source
    $start.Arguments='exec -e "PGOPTIONS=-c default_transaction_read_only=on" '+$pg.Id+' pg_dump -U '+$admin+' -d '+$database+' -Fc'
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    $stream=[IO.File]::Create($file)
    try{$process.StandardOutput.BaseStream.CopyTo($stream)}finally{$stream.Dispose()}
    $null=$process.StandardError.ReadToEnd();$process.WaitForExit()
    if($process.ExitCode-ne0){throw 'Backup failed; private partial artifact retained. Never use it for restore.'}
    $header=[IO.File]::OpenRead($file)
    try{$bytes=New-Object byte[] 5;$null=$header.Read($bytes,0,5);if([Text.Encoding]::ASCII.GetString($bytes)-ne'PGDMP'){throw 'Invalid custom-format backup header'}}finally{$header.Dispose()}
    $manifest+=[pscustomobject]@{Database=$database;File=[IO.Path]::GetFileName($file);SHA256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant();Bytes=(Get-Item -LiteralPath $file).Length;Schema=$schema;Snapshot=$snapshot}
}
$record=[pscustomobject]@{Cluster='7703616372368646182';CreatedAtUtc=[DateTime]::UtcNow.ToString('o');Container=$pg.Id;Databases=$manifest;RestoreVerified=$false}
[IO.File]::WriteAllText((Join-Path $backupPath 'MANIFEST.json'),($record|ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
Write-Output ('Backup files stored privately at '+$backupPath+'. Restore drill still required; do not apply migration yet.')
