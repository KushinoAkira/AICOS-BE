$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$manifestPath=Join-Path $repo 'docs/gate-d/schema-source.json'
$m=Get-Content $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
$source=[Environment]::ExpandEnvironmentVariables($m.source)
if(-not(Test-Path -LiteralPath $source -PathType Leaf)){throw 'Approved schema artifact missing'}
if((Get-FileHash $source -Algorithm SHA256).Hash -cne $m.sha256){throw 'Schema checksum mismatch'}
$sql=Get-Content $source -Raw -Encoding UTF8
if($sql -match '(?m)^(COPY |INSERT INTO |UPDATE public\.|DELETE FROM |TRUNCATE |CREATE (ROLE|USER|DATABASE|SERVER|EXTENSION) |\\(connect|!|i\s|ir\s))'){throw 'Unexpected executable/data/role content in schema artifact'}
$blocks=[regex]::Matches($sql,'(?ms)^CREATE TABLE public\.\w+ \((.*?)^\);')
$columns=0
foreach($block in $blocks){
    $columns+=@($block.Groups[1].Value -split '\r?\n'|Where-Object {$_ -match '^    [a-z_][a-z_0-9]* ' -and $_ -notmatch '^    (CONSTRAINT|PRIMARY|FOREIGN|CHECK|UNIQUE) '}).Count
}
if($blocks.Count -ne 38 -or $columns -ne 244 -or -not $sql.Contains('CREATE TABLE public.payment_events')){throw 'Unexpected baseline table/column inventory'}
$functions=[regex]::Matches($sql,'(?m)^CREATE FUNCTION (\S+)')
if($functions.Count -ne 1 -or $functions[0].Groups[1].Value -cne 'public.reject_payment_event_mutation()'){throw 'Unreviewed schema function'}
if(-not $sql.Contains('CREATE TRIGGER trg_payment_events_append_only BEFORE DELETE OR UPDATE ON public.payment_events')){throw 'Append-only trigger missing'}
foreach($file in @('01_runtime_roles.review.sql','02_runtime_acl.review.sql')){
    $text=Get-Content (Join-Path $repo ('docs/gate-d/'+$file)) -Raw -Encoding UTF8
    if(-not $text.Contains('\ir 00_target_guard.sql')){throw 'Target guard missing'}
    if($text -match "'aicos_db'|ON DATABASE aicos_db"){throw 'Main database reference in test SQL'}
}
[pscustomobject]@{Status='PASS_PACKAGE_REVIEW_ONLY';Tables=$blocks.Count;Columns=$columns;SchemaSha256=$m.sha256;SchemaApplied=$false;AclApplied=$false;MainDatabaseConnection=$false}|ConvertTo-Json

