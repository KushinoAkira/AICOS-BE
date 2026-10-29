$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../GateD.SchemaPackage.psm1') -Force
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$p=Get-GateDSchemaPackage $repo
$source=$p.Texts.schema
$canonical=ConvertTo-GateDCanonicalSchema $source -NormalizeReviewedTextArrayCasts
$cases=0
foreach($change in @(
    @('PAID','UNEXPECTED'),
    @('numeric(18,2)','numeric(10,1)'),
    @('REFERENCES public.users(id)','REFERENCES public.roles(id)'),
    @('NOT NULL','NULL'),
    @('total_amount >','total_amount >='),
    @('DEFERRABLE','NOT DEFERRABLE')
)){
    $changed=$source.Replace($change[0],$change[1])
    if($changed -ceq $source -or (ConvertTo-GateDCanonicalSchema $changed -NormalizeReviewedTextArrayCasts) -ceq $canonical){throw 'Schema normalizer concealed structural change'}
    $cases++
}
$newForm=[regex]::Replace($source,'\(ARRAY\[((?:''[A-Z_]+''::character varying(?:, )?)+)\]\)::text\[\]',[Text.RegularExpressions.MatchEvaluator]{param($m)
    'ARRAY['+[regex]::Replace($m.Groups[1].Value,"('[A-Z_]+'::character varying)",'($1)::text')+']'
})
if((ConvertTo-GateDCanonicalSchema $newForm -NormalizeReviewedTextArrayCasts) -cne $canonical){throw 'Reviewed PG18 cast form not equivalent'}
$cases++
try { $null=ConvertTo-GateDCanonicalSchema $source.Replace('ck_payments_method','unknown_check') -NormalizeReviewedTextArrayCasts; throw 'Expected narrow CHECK-name guard rejection' } catch {if($_.Exception.Message -cne 'Expected exactly eight reviewed CHECK forms'){throw}}
$cases++
[pscustomobject]@{Status='PASS';Cases=$cases;Kind='Pure schema text guard tests';DatabaseConnection=$false;BusinessTests=$false}|ConvertTo-Json
