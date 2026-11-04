[CmdletBinding()]
param([switch]$Apply)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repositoryRoot = if ($PSScriptRoot) { Split-Path $PSScriptRoot -Parent } else { (Get-Location).Path }
$fixturePath = Join-Path $repositoryRoot 'docs/fixtures/local-staff-menu.psd1'
# UTF-8 BOM keeps Vietnamese data portable to Windows PowerShell 5.1.
$fixture = Import-PowerShellDataFile -LiteralPath $fixturePath

function Invoke-LocalApi([string]$Method, [string]$Path, $Body = $null, [string]$Token = $null) {
    $parameters = @{
        Uri = 'http://127.0.0.1:8081' + $Path
        Method = $Method
        ContentType = 'application/json; charset=utf-8'
        TimeoutSec = 30
        ErrorAction = 'Stop'
    }
    if ($Token) { $parameters.Headers = @{ Authorization = 'Bearer ' + $Token } }
    if ($null -ne $Body) {
        $parameters.Body = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 16 -Compress))
    }
    try {
        $response = Invoke-RestMethod @parameters
        if ($response -is [Array]) {
            foreach ($item in $response) { Write-Output $item }
        } else {
            return $response
        }
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'unreachable' }
        throw ('Local menu API stopped: ' + $Method + ' ' + $Path + ' (' + $status + '). Response/secrets omitted.')
    }
}

. (Join-Path $PSScriptRoot 'fixtures/LocalMenuOptions.ps1')

$postgres = docker inspect aicos-main-postgres | ConvertFrom-Json
$menu = docker inspect aicos-main-menu | ConvertFrom-Json
if ($LASTEXITCODE -ne 0 -or $postgres.Config.Labels.'com.docker.compose.project' -ne 'aicos-main' -or $menu.Config.Labels.'com.docker.compose.project' -ne 'aicos-main') {
    throw 'Expected main Docker containers were not verified.'
}
if (-not ($menu.Config.Env -contains 'Database__MigrateOnStartup=false')) { throw 'Menu migrations must be disabled.' }
foreach ($container in @($postgres, $menu)) {
    if (-not $container.State.Running) { throw 'Start the existing main PostgreSQL and Menu containers before provisioning.' }
}
foreach ($name in @('aicos-main-identity', 'aicos-main-gateway', 'aicos-main-redis')) {
    $dependency = docker inspect $name | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $dependency.Config.Labels.'com.docker.compose.project' -ne 'aicos-main' -or -not $dependency.State.Running) {
        throw 'A main runtime dependency is stopped or not verified. No fixtures were written.'
    }
}
$connection = $menu.Config.Env | Where-Object { $_.StartsWith('ConnectionStrings__Default=') }
if ($connection -notmatch 'Host=postgres;' -or $connection -notmatch 'Database=aicos_menu;' -or $connection -notmatch 'Username=menu_svc;') {
    throw 'Menu database target is not the expected main Docker database.'
}
if ($fixture.TenantCode -ne 'aicos-cashier-local' -or $fixture.Categories.Count -ne 6 -or $fixture.Products.Count -ne 28 -or @($fixture.Products.Name | Select-Object -Unique).Count -ne 28) {
    throw 'Fixture target/count/uniqueness guard failed.'
}
foreach ($item in $fixture.Products) {
    if ($fixture.Categories -notcontains $item.Category -or $item.Price -le 0 -or @('none','drink','tea','food','combo','coffee','fruit') -notcontains $item.Preset) {
        throw 'Invalid local menu fixture.'
    }
}
Write-Output 'Preflight PASS: aicos-main / aicos_menu, 6 categories, 28 products. No migration or direct SQL.'
if (-not $Apply) { Write-Output 'Preview only. Add -Apply to create missing fixtures and publish to the three local branches.'; return }

$credential = Import-Clixml -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) 'aicos-local-cashier-admin.clixml')
$auth = Invoke-LocalApi POST '/api/v1/identity/auth/login' @{ login = $credential.UserName; password = $credential.GetNetworkCredential().Password }
try {
    $me = Invoke-LocalApi GET '/api/v1/identity/auth/me' $null $auth.accessToken
    if ($me.role -ne 'BrandAdmin' -or $me.email -ne 'admin@aicos-cashier-local.test' -or -not $me.tenantId) { throw 'Unexpected local BrandAdmin scope.' }
    $branches = Invoke-LocalApi GET '/api/v1/identity/branches/?pageSize=100' $null $auth.accessToken
    if ($branches.items.Count -ne 3 -or @($branches.items | Where-Object { $_.status -ne 'Active' }).Count) { throw 'Expected three active fixture branches.' }
    $categories = @(Invoke-LocalApi GET '/api/v1/menu/categories/' $null $auth.accessToken)
    for ($index = 0; $index -lt $fixture.Categories.Count; $index++) {
        $name = $fixture.Categories[$index]
        if (-not ($categories | Where-Object { $_.name -eq $name })) {
            $categories += Invoke-LocalApi POST '/api/v1/menu/categories/' @{ name = $name; sortOrder = $index } $auth.accessToken
        }
    }
    $page = Invoke-LocalApi GET '/api/v1/menu/products/?pageSize=100' $null $auth.accessToken
    if ($page.totalCount -gt 100) { throw 'Unexpected catalog size; review pagination before provisioning.' }
    $products = @($page.items)
    $ids = @()
    foreach ($item in $fixture.Products) {
        $category = $categories | Where-Object { $_.name -eq $item.Category } | Select-Object -First 1
        if ($category.status -ne 'Active') { throw 'Fixture category is hidden; no automatic status change.' }
        $product = $products | Where-Object { $_.name -eq $item.Name } | Select-Object -First 1
        if ($product) {
            if ($product.categoryId -ne $category.id -or $product.basePrice -ne $item.Price -or $product.description -ne $item.Description) { throw 'Existing product differs; not overwriting it.' }
        } else {
            $body = @{ categoryId = $category.id; name = $item.Name; description = $item.Description; imageUrl = $null; basePrice = $item.Price; tags = @($item.Kind, 'local-fixture'); optionGroups = @(Get-OptionGroups $item.Preset) }
            $product = Invoke-LocalApi POST '/api/v1/menu/products/' $body $auth.accessToken
            $products += $product
        }
        $ids += $product.id
    }
    $result = Invoke-LocalApi POST '/api/v1/menu/publications' @{ branchIds = @($branches.items.id); productIds = $ids; publish = $true } $auth.accessToken
    Write-Output ('Publication: branches=' + $result.branches + ', products=' + $result.products + ', changed=' + $result.changed)
    foreach ($branch in $branches.items) {
        $view = Invoke-LocalApi GET ('/api/v1/menu/branches/' + $branch.id + '/menu') $null $auth.accessToken
        $visibleIds = @($view.categories | ForEach-Object { $_.products } | ForEach-Object { $_.id })
        if ($view.branchId -ne $branch.id -or @($ids | Where-Object { $visibleIds -notcontains $_ }).Count) { throw 'Published branch menu is incomplete.' }
        Write-Output ('PASS ' + $branch.name + ': all 28 fixture products readable from the branch menu.')
    }
} finally {
    Invoke-LocalApi POST '/api/v1/identity/auth/logout' @{ refreshToken = $auth.refreshToken } | Out-Null
}
