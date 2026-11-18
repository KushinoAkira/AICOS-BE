# Seed extra demo brands + branches (and bump Cà phê Nhà onto the 5-branch plan).
# Usage (from Backend/):  pwsh ./scripts/seed-demo-brands.ps1
# Then optionally:       pwsh ./scripts/seed-demo-menu-ai.ps1
$ErrorActionPreference = "Stop"
$base = "http://localhost:8080/api/v1"
$envFile = Join-Path $PSScriptRoot "..\.env"
$brandPassword = "Brand@12345"

function Read-Env([string]$key) {
  $line = Get-Content $envFile | Where-Object { $_ -match "^$key=(.+)$" } | Select-Object -First 1
  if (-not $line) { throw "Missing $key in Backend/.env" }
  return $Matches[1]
}

function Invoke-Json {
  param(
    [string]$Method,
    [string]$Url,
    [hashtable]$Headers = @{},
    $Body = $null,
    [int]$Retries = 6
  )
  $payload = if ($null -eq $Body) { $null } else { ($Body | ConvertTo-Json -Depth 8) }
  for ($i = 1; $i -le $Retries; $i++) {
    try {
      if ($null -eq $payload) {
        return Invoke-RestMethod -Method $Method -Uri $Url -Headers $Headers
      }
      return Invoke-RestMethod -Method $Method -Uri $Url -Headers $Headers -ContentType "application/json" -Body $payload
    } catch {
      $code = $_.Exception.Response.StatusCode.value__
      $detail = $null
      try {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $detail = $reader.ReadToEnd()
      } catch { }
      if ($code -eq 429 -and $i -lt $Retries) {
        $wait = [Math]::Min(20, 2 * $i)
        Write-Host "  rate-limited, retry in ${wait}s…"
        Start-Sleep -Seconds $wait
        continue
      }
      throw "HTTP $Method $Url failed ($code): $detail $($_.Exception.Message)"
    }
  }
}

function Login([string]$login, [string]$password) {
  $r = Invoke-Json -Method Post -Url "$base/identity/auth/login" -Body @{ login = $login; password = $password }
  return $r.accessToken
}

function ItemsOf($response) {
  if ($null -eq $response) { return @() }
  if ($response -isnot [System.Array] -and $response.PSObject.Properties.Name -contains "value") { return @($response.value) }
  if ($response -isnot [System.Array] -and $response.PSObject.Properties.Name -contains "items") { return @($response.items) }
  return @($response)
}

function Ensure-Branch($headers, [string]$name, [string]$address, [string]$phone) {
  $existing = ItemsOf (Invoke-Json -Method Get -Url "$base/identity/branches?pageSize=100" -Headers $headers)
  $hit = $existing | Where-Object { $_.name -eq $name } | Select-Object -First 1
  if ($hit) {
    Write-Host "  branch exists: $name ($($hit.id))"
    return $hit
  }
  $created = Invoke-Json -Method Post -Url "$base/identity/branches" -Headers $headers -Body @{
    name = $name; address = $address; phone = $phone
    openingTime = "07:00:00"; closingTime = "22:00:00"
  }
  Write-Host "  created branch: $name ($($created.id))"
  return $created
}

function Ensure-Category($headers, [string]$name, [int]$sort) {
  $cats = ItemsOf (Invoke-Json -Method Get -Url "$base/menu/categories" -Headers $headers)
  $hit = $cats | Where-Object { $_.name -eq $name } | Select-Object -First 1
  if ($hit) { return $hit }
  return Invoke-Json -Method Post -Url "$base/menu/categories" -Headers $headers -Body @{ name = $name; sortOrder = $sort }
}

function Ensure-Product($headers, $byName, $catId, [string]$name, [string]$desc, [int]$price, $tags) {
  if ($byName.ContainsKey($name)) { return $byName[$name] }
  $p = Invoke-Json -Method Post -Url "$base/menu/products" -Headers $headers -Body @{
    categoryId = $catId; name = $name; description = $desc; basePrice = $price; tags = $tags
  }
  $byName[$name] = $p
  Write-Host "  product: $name"
  return $p
}

function Publish-Product($headers, [string]$productId, [string]$branchId, [int]$price) {
  Invoke-Json -Method Put -Url "$base/menu/products/$productId/branches/$branchId" -Headers $headers -Body @{
    isPublished = $true; priceOverride = $price
  } | Out-Null
}

Write-Host "=== Seed demo brands & branches ==="
$adminPass = Read-Env "SEED_ADMIN_PASSWORD"
Start-Sleep -Seconds 2
$adminToken = Login "admin@aicos.local" $adminPass
$adminH = @{ Authorization = "Bearer $adminToken"; "Content-Type" = "application/json" }

$plans = ItemsOf (Invoke-Json -Method Get -Url "$base/identity/subscription-plans?pageSize=50" -Headers $adminH)
$standard = $plans | Where-Object { $_.tier -eq "Standard" -or $_.maxBranches -eq 5 } | Select-Object -First 1
$basic = $plans | Where-Object { $_.tier -eq "Basic" -or $_.maxBranches -eq 1 } | Select-Object -First 1
if (-not $standard) { throw "Standard plan (5 branches) not found — set Seed:DemoPlans=true and restart Identity." }
Write-Host "Plans: Standard=$($standard.name) Basic=$($basic.name)"

$tenants = ItemsOf (Invoke-Json -Method Get -Url "$base/identity/tenants?pageSize=100" -Headers $adminH)
function Find-Tenant([string]$code) { $tenants | Where-Object { $_.code -eq $code } | Select-Object -First 1 }

# --- Cà phê Nhà: upgrade plan + extra branches ---
$cafe = Find-Tenant "caphenha"
if (-not $cafe) { throw "Tenant caphenha missing" }
Write-Host "`n[caphenha] upgrade plan → $($standard.name)"
try {
  Invoke-Json -Method Post -Url "$base/identity/tenants/$($cafe.id)/subscription" -Headers $adminH -Body @{ planId = $standard.id } | Out-Null
  Write-Host "  plan assigned"
} catch {
  if ("$_" -match "same_plan|already") { Write-Host "  already on target plan" } else { throw }
}

$ownerToken = Login "owner@caphenha.local" $brandPassword
$ownerH = @{ Authorization = "Bearer $ownerToken"; "Content-Type" = "application/json" }
Write-Host "[caphenha] branches"
$null = Ensure-Branch $ownerH "Quan 1" "1 Nguyen Hue, Q1" "0281234567"
$null = Ensure-Branch $ownerH "Thu Duc" "1 Vo Van Ngan, Thu Duc" "0287654321"
$null = Ensure-Branch $ownerH "Quan 3" "88 Nguyen Thi Minh Khai, Q3" "0283456789"
try {
  Invoke-Json -Method Put -Url "$base/identity/brand/profile" -Headers $ownerH -Body @{
    name = "Ca phe Nha"
    description = "Ca phe phin & specialty — mang di nhanh, diem thuong moi ly."
    primaryColor = "#6F4E37"
    secondaryColor = "#C97B4A"
  } | Out-Null
} catch { Write-Host "  profile skip: $_" }

# --- Create extra brands ---
$extraBrands = @(
  @{
    code = "trasuamay"; name = "Tra Sua May"; planId = $standard.id
    adminEmail = "owner@trasuamay.local"; adminName = "Chu Tra Sua May"
    description = "Tra sua, tra trai cay — topping day du."
    primaryColor = "#B5483A"
    branches = @(
      @{ name = "May Quan 3"; address = "88 Nguyen Thi Minh Khai, Q3"; phone = "0281111222" }
      @{ name = "May Binh Thanh"; address = "12 Xo Viet Nghe Tinh, Binh Thanh"; phone = "0281111333" }
    )
    menu = "tea"
  }
  @{
    code = "democoffee"; name = "Demo Coffee"; planId = $basic.id
    adminEmail = "owner@democoffee.local"; adminName = "Chu Demo Coffee"
    description = "Thuong hieu demo thu hai — menu ca phe gon."
    primaryColor = "#3E6A8A"
    branches = @(
      @{ name = "Demo Quan 1"; address = "10 Le Loi, Q1"; phone = "0282222333" }
    )
    menu = "coffee-lite"
  }
)

foreach ($b in $extraBrands) {
  Write-Host "`n[$($b.code)]"
  $t = Find-Tenant $b.code
  if (-not $t) {
    $t = Invoke-Json -Method Post -Url "$base/identity/tenants" -Headers $adminH -Body @{
      name = $b.name
      code = $b.code
      planId = $b.planId
      firstAdmin = @{
        fullName = $b.adminName
        email = $b.adminEmail
        phone = "0900000000"
        password = $brandPassword
      }
    }
    Write-Host "  created tenant $($t.code) ($($t.id))"
    $tenants = @($tenants) + $t
  } else {
    Write-Host "  tenant exists ($($t.id))"
    try {
      Invoke-Json -Method Post -Url "$base/identity/tenants/$($t.id)/subscription" -Headers $adminH -Body @{ planId = $b.planId } | Out-Null
    } catch {
      if ("$_" -notmatch "same_plan|already") { Write-Host "  plan note: $_" }
    }
  }

  $tok = Login $b.adminEmail $brandPassword
  $h = @{ Authorization = "Bearer $tok"; "Content-Type" = "application/json" }
  try {
    Invoke-Json -Method Put -Url "$base/identity/brand/profile" -Headers $h -Body @{
      name = $b.name
      description = $b.description
      primaryColor = $b.primaryColor
    } | Out-Null
  } catch { Write-Host "  profile skip: $_" }

  $branchIds = @()
  foreach ($br in $b.branches) {
    $row = Ensure-Branch $h $br.name $br.address $br.phone
    $branchIds += $row.id
  }

  # Menu seed for this brand
  Write-Host "  seeding menu ($($b.menu))"
  $existing = ItemsOf (Invoke-Json -Method Get -Url "$base/menu/products?pageSize=100" -Headers $h)
  $byName = @{}
  foreach ($p in $existing) {
    if ($p -and $p.name) { $byName[[string]$p.name] = $p }
  }

  if ($b.menu -eq "tea") {
    $catTea = Ensure-Category $h "Tra sua" 1
    $catFruit = Ensure-Category $h "Tra trai cay" 2
    $catTop = Ensure-Category $h "An kem" 3
    $rows = @(
      @{ cat = $catTea.id; name = "Tra sua truyen thong"; desc = "Tra den pha sua tuoi."; price = 32000; tags = @("ban chay") }
      @{ cat = $catTea.id; name = "Tra sua khoai mon"; desc = "Khoai mon beo, ngot diu."; price = 38000; tags = @("beo") }
      @{ cat = $catTea.id; name = "Tra sua matcha"; desc = "Matcha Nhat pha sua."; price = 42000; tags = @("matcha") }
      @{ cat = $catFruit.id; name = "Tra dao"; desc = "Tra den, dao tuoi."; price = 35000; tags = @("giai nhiet") }
      @{ cat = $catFruit.id; name = "Tra vai"; desc = "Tra oolong, vai tuoi."; price = 36000; tags = @("mua he") }
      @{ cat = $catTop.id; name = "Banh flan"; desc = "Flan caramen mem."; price = 18000; tags = @("an kem") }
      @{ cat = $catTop.id; name = "Khoai mon vien"; desc = "Khoai mon chien gion."; price = 15000; tags = @("an vat") }
    )
  } else {
    $catCoffee = Ensure-Category $h "Ca phe" 1
    $catSnack = Ensure-Category $h "An vat" 2
    $rows = @(
      @{ cat = $catCoffee.id; name = "Ca phe den"; desc = "Phin dam da."; price = 25000; tags = @("dam") }
      @{ cat = $catCoffee.id; name = "Ca phe sua"; desc = "Phin pha sua dac."; price = 30000; tags = @("ban chay") }
      @{ cat = $catCoffee.id; name = "Bac xiu"; desc = "Nhieu sua it ca phe."; price = 32000; tags = @("nhe") }
      @{ cat = $catCoffee.id; name = "Americano"; desc = "Espresso pha loang."; price = 35000; tags = @("it calo") }
      @{ cat = $catSnack.id; name = "Banh mi que"; desc = "Que gion nhan pate."; price = 20000; tags = @("an sang") }
    )
  }

  foreach ($row in $rows) {
    $p = Ensure-Product $h $byName $row.cat $row.name $row.desc ([int]$row.price) $row.tags
    foreach ($bid in $branchIds) {
      $price = [int]$p.basePrice
      if ($b.code -eq "democoffee" -and $row.name -eq "Ca phe sua") { $price = 35000 }
      Publish-Product $h $p.id $bid $price
    }
  }
}

# Refresh Cà phê Nhà menu across all its branches
Write-Host "`n[caphenha] refresh menu publish"
& (Join-Path $PSScriptRoot "seed-demo-menu-ai.ps1")

Write-Host "`n=== Public brands ==="
ItemsOf (Invoke-Json -Method Get -Url "$base/identity/public/brands") | ForEach-Object {
  Write-Host ("- {0} ({1}) · {2} chi nhanh" -f $_.name, $_.code, $_.branchCount)
}
Write-Host "Done. Brand admin password: $brandPassword"
Write-Host "  owner@caphenha.local / owner@trasuamay.local / owner@democoffee.local"
