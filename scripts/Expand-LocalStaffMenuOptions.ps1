[CmdletBinding()]
param([switch]$Apply)
$ErrorActionPreference='Stop'
$shouldApply=[bool]$Apply
# Reuse the existing project/database/tenant guards; initializer preview never writes.
. (Join-Path $PSScriptRoot 'Initialize-LocalStaffMenu.ps1')
function Signature($groups) {
    $normalized=@(foreach($g in ($groups|ForEach-Object { [pscustomobject]$_ }|Sort-Object name)){
        [ordered]@{name=$g.name;kind=[string]$g.kind;selectionType=[string]$g.selectionType;isRequired=[bool]$g.isRequired;
          maxSelections=$g.maxSelections;sortOrder=[int]$g.sortOrder;
          options=@(foreach($o in ($g.options|ForEach-Object { [pscustomobject]$_ }|Sort-Object name)){
              [ordered]@{name=$o.name;priceDelta=[double]$o.priceDelta;isDefault=[bool]$o.isDefault}
          })}
    })
    ConvertTo-Json -InputObject $normalized -Depth 12 -Compress
}
$credential=Import-Clixml -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) 'aicos-local-cashier-admin.clixml')
$auth=Invoke-LocalApi POST '/api/v1/identity/auth/login' @{login=$credential.UserName;password=$credential.GetNetworkCredential().Password}
try {
    $me=Invoke-LocalApi GET '/api/v1/identity/auth/me' $null $auth.accessToken
    if($me.role-ne'BrandAdmin'-or$me.email-ne'admin@aicos-cashier-local.test'){throw 'Unexpected fixture admin'}
    $page=Invoke-LocalApi GET '/api/v1/menu/products/?pageSize=100' $null $auth.accessToken
    if($page.totalCount-ne28){throw 'Expected exactly 28 fixture products'}
    $plans=@()
    foreach($item in $fixture.Products){
        $p=@($page.items|Where-Object name -eq $item.Name)
        if($p.Count-ne1){throw 'Missing or duplicate fixture product'};$p=$p[0]
        if($p.tags-notcontains'local-fixture'-or$p.basePrice-ne$item.Price-or$p.description-ne$item.Description){throw 'Fixture drift; no overwrite'}
        $groups=@(Get-OptionGroups $item.Preset)
        foreach($g in $p.optionGroups){
            $target=@($groups|Where-Object name -eq $g.name)
            if($target.Count-ne1-or$target[0].kind-ne$g.kind-or$target[0].selectionType-ne$g.selectionType){throw 'Expansion would remove/change an existing group'}
            foreach($o in $g.options){
                $next=@($target[0].options|Where-Object name -eq $o.name)
                if($next.Count-ne1-or$next[0].priceDelta-ne$o.priceDelta-or$next[0].isDefault-ne$o.isDefault-or$o.requiresOptionId){throw 'Expansion would change an existing option'}
            }
        }
        if((Signature $p.optionGroups)-ne(Signature $groups)){$plans+=@{product=$p;groups=$groups}}
    }
    Write-Output ('Preflight PASS: additive options for '+$plans.Count+' local products; no price/schema/publication changes.')
    if(-not$shouldApply){Write-Output 'Preview only; -Apply is required.';return}
    $backup=Join-Path ([IO.Path]::GetTempPath()) ('aicos-menu-options-before-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss')+'.json')
    $page.items|ConvertTo-Json -Depth 18|Set-Content -LiteralPath $backup -Encoding UTF8
    foreach($plan in $plans){
        $p=$plan.product
        $body=@{categoryId=$p.categoryId;name=$p.name;description=$p.description;imageUrl=$p.imageUrl;basePrice=$p.basePrice;tags=@($p.tags);optionGroups=$plan.groups}
        $updated=Invoke-LocalApi PUT ('/api/v1/menu/products/'+$p.id) $body $auth.accessToken
        foreach($g in $p.optionGroups){
            $after=@($updated.optionGroups|Where-Object name -eq $g.name)[0]
            if($after.id-ne$g.id){throw 'Old group ID changed; stop'}
            foreach($o in $g.options){if(@($after.options|Where-Object name -eq $o.name)[0].id-ne$o.id){throw 'Old option ID changed; stop'}}
        }
        Write-Output ('PASS expanded, old IDs retained: '+$p.name)
    }
    $branches=Invoke-LocalApi GET '/api/v1/identity/branches/?pageSize=100' $null $auth.accessToken
    foreach($branch in $branches.items){
        $menu=Invoke-LocalApi GET ('/api/v1/menu/branches/'+$branch.id+'/menu') $null $auth.accessToken
        $products=@($menu.categories|ForEach-Object products)
        if($products.Count-ne28){throw 'Published menu count drift'}
        $tops=@($products|ForEach-Object optionGroups|Where-Object kind -eq 'Topping')
        if($tops.Count-ne17-or@($tops|Where-Object {$null-ne$_.maxSelections}).Count){throw 'Expanded topping configuration not visible'}
        Write-Output ('PASS '+$branch.name+': 28 products, '+$tops.Count+' topping groups, no two-choice cap')
    }
} finally { Invoke-LocalApi POST '/api/v1/identity/auth/logout' @{refreshToken=$auth.refreshToken}|Out-Null }