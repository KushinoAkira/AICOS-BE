Set-StrictMode -Version Latest

function Get-GateDSchemaPackage {
    param([Parameter(Mandatory)][string]$Repository)
    $directory=Join-Path $Repository 'docs/gate-d'
    $sourceInfo=Get-Content (Join-Path $directory 'schema-source.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $source=[Environment]::ExpandEnvironmentVariables($sourceInfo.source)
    $pins=@{
        'schema'='CC6F19A5BAE3A8ACBCB096EC110A7D172B4C86D63C37E1AE18CF61C8E30158A3'
        '00_target_guard.sql'='87AAD55E0859765304A294740856355FC559629278149F310AA960C877C9BFF7'
        '01_runtime_roles.review.sql'='6142F365F84965E2CDE5C23A8216784290376E70D1BFC5DD96B9BEF7E4DE70E2'
        '02_runtime_acl.review.sql'='D3633094F54D510DDF0CA79867C5D673A795C0F8D15C46F3BE9C30C36EB02CFC'
    }
    $texts=@{}
    foreach($name in $pins.Keys){
        $path=if($name -eq 'schema'){$source}else{Join-Path $directory $name}
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $pins[$name]){throw ('Unreviewed SQL checksum: '+$name)}
        $texts[$name]=Get-Content -LiteralPath $path -Raw -Encoding UTF8
    }
    # These exact bytes were reviewed. Classify every top-level schema statement,
    # rather than relying only on table counts or keyword searches in comments.
    $schema=$texts.schema
    $functionPattern='(?s)CREATE FUNCTION public\.reject_payment_event_mutation\(\) RETURNS trigger\s+LANGUAGE plpgsql\s+AS \$\$\s*BEGIN\s*RAISE EXCEPTION ''payment_events is append-only'';\s*END;\s*\$\$;'
    if([regex]::Matches($schema,$functionPattern).Count -ne 1){throw 'Unreviewed function body'}
    $clean=[regex]::Replace($schema,$functionPattern,'')
    $clean=[regex]::Replace($clean,'(?m)^--[^\r\n]*','')
    $meta=@([regex]::Matches($clean,'(?m)^\\[^\r\n]*')|ForEach-Object {$_.Value})
    if($meta.Count -ne 2 -or $meta[0] -notmatch '^\\restrict ([A-Za-z0-9]+)$' -or $meta[1] -cne ('\unrestrict '+$Matches[1])){throw 'Unexpected psql metacommands'}
    $clean=[regex]::Replace($clean,'(?m)^\\[^\r\n]*','')
    $inventory=[ordered]@{Tables=0;Columns=0;PrimaryKeys=0;ForeignKeys=0;UniqueConstraints=0;Checks=0;ExplicitIndexes=0;Functions=1;Triggers=0;Comments=0;Settings=0}
    $tableNames=@()
    foreach($statement in ($clean -split ';')){
        $s=$statement.Trim()
        if(-not $s){continue}
        if($s -match '(?s)^CREATE TABLE public\.(\w+) \((.*)\)$'){
            $tableNames+=$Matches[1];$body=$Matches[2];$inventory.Tables++
            $inventory.Columns+=@($body -split '\r?\n'|Where-Object {$_ -cmatch '^    [a-z_][a-z_0-9]* '}).Count
            $inventory.Checks+=[regex]::Matches($body,'\bCONSTRAINT \w+ CHECK ').Count
        } elseif($s -match '(?s)^ALTER TABLE ONLY public\.\w+\s+ADD CONSTRAINT \w+ (PRIMARY KEY|FOREIGN KEY|UNIQUE) \(.+\)( REFERENCES public\.\w+\(\w+\))?( DEFERRABLE)?$'){
            switch($Matches[1]){'PRIMARY KEY'{$inventory.PrimaryKeys++};'FOREIGN KEY'{$inventory.ForeignKeys++};'UNIQUE'{$inventory.UniqueConstraints++}}
        } elseif($s -match '(?s)^CREATE (UNIQUE )?INDEX \w+ ON public\.\w+ USING btree \(.+\)( WHERE .+)?$'){$inventory.ExplicitIndexes++
        } elseif($s -ceq 'CREATE TRIGGER trg_payment_events_append_only BEFORE DELETE OR UPDATE ON public.payment_events FOR EACH ROW EXECUTE FUNCTION public.reject_payment_event_mutation()'){$inventory.Triggers++
        } elseif($s -match '^COMMENT ON COLUMN public\.(orders\.order_type|payments\.method) IS ''[A-Za-z_ ]+''$'){$inventory.Comments++
        } elseif($s -match '^SET (statement_timeout|lock_timeout|idle_in_transaction_session_timeout|transaction_timeout|client_encoding|standard_conforming_strings|check_function_bodies|xmloption|client_min_messages|row_security|default_tablespace|default_table_access_method) = (0|''UTF8''|on|false|content|warning|off|''''|heap)$' -or $s -ceq "SELECT pg_catalog.set_config('search_path', '', false)"){$inventory.Settings++
        } else {throw ('Unclassified schema statement: '+$s.Substring(0,[Math]::Min(90,$s.Length)))}
    }
    if($inventory.Tables -ne 38 -or $inventory.Columns -ne 244 -or $inventory.Triggers -ne 1 -or @($tableNames|Sort-Object -Unique).Count -ne 38){throw 'Baseline inventory mismatch'}
    $inventory['TotalIndexes']=$inventory.PrimaryKeys+$inventory.UniqueConstraints+$inventory.ExplicitIndexes
    return @{Texts=$texts;Hashes=$pins;Inventory=$inventory;Tables=$tableNames;Source=$source}
}

function ConvertTo-GateDCanonicalSchema {
    param([Parameter(Mandatory)][string]$Sql,[switch]$NormalizeReviewedTextArrayCasts)
    # Ignore only dump comments, random restrict tokens, blank lines and CRLF.
    # Definitions, literal values, types, defaults, FKs and indexes stay exact.
    if($NormalizeReviewedTextArrayCasts){
        # PG18.4 casts the constant varchar[] as a whole; PG18.6 deparses
        # an identical text[] with per-element casts. Normalize ONLY these eight
        # reviewed CHECKs, retaining every literal, operator and null predicate.
        $names='ck_bills_print_status|ck_bills_status|ck_payment_events_from_status|ck_payment_events_to_status|ck_payment_events_type|ck_payments_method|ck_payments_status|ck_payments_terminal_reason'
        $lines=$Sql -split '\r?\n'
        $matched=0
        $Sql=($lines|ForEach-Object {
            $line=$_
            if($line -match ('^    CONSTRAINT ('+$names+') CHECK ')){
                $matched++
                $old='\(ARRAY\[((?:''[A-Z_]+''::character varying(?:, )?)+)\]\)::text\[\]'
                $new='ARRAY\[((?:\(''[A-Z_]+''::character varying\)::text(?:, )?)+)\]'
                $convert=[Text.RegularExpressions.MatchEvaluator]{param($m)
                    $values=([regex]::Matches($m.Groups[1].Value,"'[A-Z_]+'")|ForEach-Object {$_.Value}) -join ', '
                    return 'ARRAY['+$values+']::text[]'
                }
                if([regex]::Matches($line,$old).Count+[regex]::Matches($line,$new).Count -ne 1){throw 'Unreviewed CHECK array form'}
                $line=[regex]::Replace([regex]::Replace($line,$old,$convert),$new,$convert)
            }
            $line
        }) -join "`n"
        if($matched -ne 8){throw 'Expected exactly eight reviewed CHECK forms'}
    }
    (($Sql -split '\r?\n'|Where-Object {$_ -notmatch '^--|^\\(un)?restrict ' -and $_.Trim()}|ForEach-Object {$_.TrimEnd()}) -join "`n")
}

Export-ModuleMember -Function Get-GateDSchemaPackage,ConvertTo-GateDCanonicalSchema
