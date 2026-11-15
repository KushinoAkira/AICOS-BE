# Seed a richer Cà phê Nhà menu (2 branches) so AI can recommend/pair within each branch.
# Usage (from Backend/):  pwsh ./scripts/seed-demo-menu-ai.ps1
$ErrorActionPreference = "Stop"
$base = "http://localhost:8080/api/v1"

function Login($login, $password) {
  $r = Invoke-RestMethod -Method Post -Uri "$base/identity/auth/login" -ContentType "application/json" -Body (@{ login = $login; password = $password } | ConvertTo-Json)
  return $r.accessToken
}

$token = Login "owner@caphenha.local" "Brand@12345"
$h = @{ Authorization = "Bearer $token"; "Content-Type" = "application/json" }

$branches = Invoke-RestMethod -Uri "$base/identity/branches/active" -Headers $h
$q1 = @($branches)[0]
Write-Host "Branch Q1: $($q1.id) $($q1.name)"

# Starter plan often allows only 1 branch — keep a second branch optional.
$td = @($branches) | Where-Object { $_.name -match "Thu|Thủ|Duc|Đức" } | Select-Object -First 1
if (-not $td) {
  try {
    $td = Invoke-RestMethod -Method Post -Uri "$base/identity/branches" -Headers $h -Body (@{
        name = "Thu Duc"; address = "1 Vo Van Ngan, Thu Duc"; phone = "0287654321"
        openingTime = "07:00:00"; closingTime = "22:00:00"
      } | ConvertTo-Json)
    Write-Host "Created branch TD: $($td.id)"
  } catch {
    Write-Host "Skip second branch (plan limit or API error): $($_.Exception.Message)"
  }
}

function Ensure-Category($name, $sort) {
  $cats = Invoke-RestMethod -Uri "$base/menu/categories" -Headers $h
  $list = if ($cats.value) { $cats.value } elseif ($cats.items) { $cats.items } else { @($cats) }
  $hit = @($list) | Where-Object { $_.name -eq $name } | Select-Object -First 1
  if ($hit) { return $hit }
  return Invoke-RestMethod -Method Post -Uri "$base/menu/categories" -Headers $h -Body (@{ name = $name; sortOrder = $sort } | ConvertTo-Json)
}

$catCoffee = Ensure-Category "Ca phe" 1
$catTea = Ensure-Category "Tra" 2
$catBakery = Ensure-Category "Banh" 3
$catSnack = Ensure-Category "An vat" 4

$products = @(
  @{ cat = $catCoffee.id; name = "Ca phe den"; desc = "Ca phe phin dam da, khong duong."; price = 25000; tags = @("dam", "truyen thong") }
  @{ cat = $catCoffee.id; name = "Ca phe sua"; desc = "Ca phe phin pha sua dac."; price = 29000; tags = @("ban chay") }
  @{ cat = $catCoffee.id; name = "Bac xiu"; desc = "Nhieu sua it ca phe, ngot diu."; price = 32000; tags = @("nhe", "beo") }
  @{ cat = $catCoffee.id; name = "Americano"; desc = "Espresso pha loang, it calo."; price = 35000; tags = @("it calo") }
  @{ cat = $catCoffee.id; name = "Latte"; desc = "Espresso voi sua tuoi va bot min."; price = 45000; tags = @("beo", "ban chay") }
  @{ cat = $catCoffee.id; name = "Cappuccino"; desc = "Espresso, sua nong va bot day."; price = 45000; tags = @("dam") }
  @{ cat = $catCoffee.id; name = "Cold brew"; desc = "Ca phe u lanh 18 gio."; price = 49000; tags = @("mua he") }
  @{ cat = $catCoffee.id; name = "Espresso"; desc = "Mot shot espresso dam dac."; price = 30000; tags = @("dam", "tinh tao") }
  @{ cat = $catTea.id; name = "Matcha latte"; desc = "Matcha Nhat pha sua tuoi."; price = 48000; tags = @("matcha") }
  @{ cat = $catTea.id; name = "Tra dao cam sa"; desc = "Tra den, dao, cam va sa."; price = 42000; tags = @("giai nhiet", "mua he") }
  @{ cat = $catTea.id; name = "Tra gung mat ong"; desc = "Tra gung am bung, khong caffeine."; price = 38000; tags = @("am", "khong caffeine") }
  @{ cat = $catBakery.id; name = "Croissant bo"; desc = "Banh sung bo gion xop, hop ca phe."; price = 35000; tags = @("an kem", "bua sang") }
  @{ cat = $catBakery.id; name = "Banh tiramisu"; desc = "Tiramisu vi ca phe."; price = 45000; tags = @("ngot", "an kem") }
  @{ cat = $catBakery.id; name = "Banh mi que pate"; desc = "Banh mi que gion nhan pate."; price = 20000; tags = @("an sang") }
  @{ cat = $catSnack.id; name = "Hat dieu rang muoi"; desc = "Hat dieu rang muoi gion."; price = 28000; tags = @("an vat") }
  @{ cat = $catTea.id; name = "Sinh to xoai"; desc = "Xoai chin xay da - mon Thu Duc."; price = 42000; tags = @("trai cay", "mua he") }
)

$existing = Invoke-RestMethod -Uri "$base/menu/products?pageSize=100" -Headers $h
$existItems = if ($existing.items) { $existing.items } else { @() }
$byName = @{}
foreach ($p in @($existItems)) { $byName[$p.name] = $p }

$created = @{}
foreach ($row in $products) {
  if ($byName.ContainsKey($row.name)) {
    $created[$row.name] = $byName[$row.name]
    continue
  }
  $body = @{
    categoryId = $row.cat
    name       = $row.name
    description = $row.desc
    basePrice  = $row.price
    tags       = $row.tags
  } | ConvertTo-Json -Depth 5
  $p = Invoke-RestMethod -Method Post -Uri "$base/menu/products" -Headers $h -Body $body
  $created[$row.name] = $p
  Write-Host "Created product $($p.name) $($p.id)"
}

function Publish($productId, $branchId, $price) {
  Invoke-RestMethod -Method Put -Uri "$base/menu/products/$productId/branches/$branchId" -Headers $h -Body (@{
      isPublished = $true; priceOverride = $price
    } | ConvertTo-Json) | Out-Null
}

# Quận 1: almost everything except Sinh to xoai
$q1Names = $products | Where-Object { $_.name -ne "Sinh to xoai" } | ForEach-Object { $_.name }
foreach ($n in $q1Names) {
  $p = $created[$n]
  Publish $p.id $q1.id ([int]$p.basePrice)
}

if ($td) {
  # Thủ Đức: narrower set + Sinh to xoai (only when the plan allows a 2nd branch)
  $tdNames = @("Ca phe den", "Ca phe sua", "Bac xiu", "Cold brew", "Tra dao cam sa", "Sinh to xoai", "Banh mi que pate", "Hat dieu rang muoi")
  foreach ($n in $tdNames) {
    $p = $created[$n]
    $price = if ($n -eq "Bac xiu") { 30000 } elseif ($n -eq "Cold brew") { 45000 } else { [int]$p.basePrice }
    Publish $p.id $td.id $price
  }
}

Write-Host "--- Public menu Q1 ---"
(Invoke-RestMethod -Uri "$base/menu/public/branches/$($q1.id)/menu").categories | ForEach-Object {
  Write-Host $_.name
  $_.products | ForEach-Object { Write-Host ("  - {0} {1}" -f $_.name, $_.price) }
}
if ($td) {
  Write-Host "--- Public menu TD ---"
  (Invoke-RestMethod -Uri "$base/menu/public/branches/$($td.id)/menu").categories | ForEach-Object {
    Write-Host $_.name
    $_.products | ForEach-Object { Write-Host ("  - {0} {1}" -f $_.name, $_.price) }
  }
}
Write-Host "Done."
