# Fill CapheNha gaps for local testing: brand settings, multi-branch staff,
# workforce, tables, vouchers/loyalty, sample paid counter orders.
# Usage (from Backend/):  powershell -ExecutionPolicy Bypass -File .\scripts\seed-caphenha-full.ps1
$ErrorActionPreference = "Stop"
$base = "http://localhost:8080/api/v1"
$brandPassword = "Brand@12345"

function Invoke-Json {
  param(
    [string]$Method,
    [string]$Url,
    [hashtable]$Headers = @{},
    $Body = $null,
    [switch]$AllowFail,
    [int]$Retries = 8
  )
  $payload = if ($null -eq $Body) { $null } else { ($Body | ConvertTo-Json -Depth 12) }
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
        $wait = [Math]::Min(25, 3 * $i)
        Write-Host "  rate-limited, wait ${wait}s…"
        Start-Sleep -Seconds $wait
        continue
      }
      if ($AllowFail -and $code -in 400, 409, 422) {
        Write-Host "  skip ($code): $detail"
        return $null
      }
      throw "HTTP $Method $Url failed ($code): $detail $($_.Exception.Message)"
    }
  }
}

function Login([string]$login, [string]$password) {
  Start-Sleep -Milliseconds 800
  $r = Invoke-Json -Method Post -Url "$base/identity/auth/login" -Body @{ login = $login; password = $password }
  return $r
}

function AuthHeaders($token) { @{ Authorization = "Bearer $token" } }
function ItemsOf($response) {
  if ($null -eq $response) { return @() }
  if ($response -isnot [System.Array] -and $response.PSObject.Properties.Name -contains "items") { return @($response.items) }
  if ($response -is [System.Array]) { return @($response) }
  return @($response)
}
function Ymd([datetime]$d) { $d.ToString("yyyy-MM-dd") }

Write-Host "=== CapheNha FULL seed ==="

# ---------- Brand admin settings ----------
$owner = Login "owner@caphenha.local" $brandPassword
$ownerH = AuthHeaders $owner.accessToken
$branches = ItemsOf (Invoke-Json -Method Get -Url "$base/identity/branches?pageSize=50" -Headers $ownerH)
Write-Host "branches: $($branches.Count)"

Write-Host "[loyalty rules]"
$null = Invoke-Json -Method Put -Url "$base/customers/loyalty/rules" -Headers $ownerH -Body @{
  isActive = $true
  amountPerPoint = 10000
  redeemValuePerPoint = 1000
  minPointsToRedeem = 10
  silverThreshold = 100
  goldThreshold = 500
}

Write-Host "[cancellation policy]"
$null = Invoke-Json -Method Put -Url "$base/payments/policies/cancellation" -Headers $ownerH -Body @{
  cancelRefundPercent = 100
  noShowAfterMinutes = 30
  noShowRefundPercent = 50
  unpaidOrderTimeoutMinutes = 15
  autoApproveWithinPolicy = $true
  exceptionApprover = "BranchManager"
}

Write-Host "[pay rules]"
$null = Invoke-Json -Method Put -Url "$base/workforce/pay-rules" -Headers $ownerH -Body @{
  fullTimeDayRate = 300000
  partTimeHourRate = 30000
  replacementHourRate = 45000
  overtimeAfterHours = 8
  overtimeMultiplier = 1.5
}

Write-Host "[merchant Mock + VietQR]"
$null = Invoke-Json -Method Put -Url "$base/payments/merchant-settings" -Headers $ownerH -Body @{
  branchId = $null; providerCode = "Mock"; isEnabled = $true
} -AllowFail
$null = Invoke-Json -Method Put -Url "$base/payments/merchant-settings" -Headers $ownerH -Body @{
  branchId = $null; providerCode = "VietQR"; isEnabled = $true
  accountName = "CA PHE NHA"; bankCode = "970436"; accountNumber = "0123456789"; merchantCode = "CAPHENHA"
} -AllowFail

Write-Host "[pickup slots]"
$null = Invoke-Json -Method Put -Url "$base/orders/pickup-slots" -Headers $ownerH -Body @{
  branchId = $null; slotMinutes = 15; capacityPerSlot = 12; cutoffMinutes = 15; advanceDays = 2
}
foreach ($b in $branches) {
  $null = Invoke-Json -Method Put -Url "$base/orders/pickup-slots" -Headers $ownerH -Body @{
    branchId = $b.id; slotMinutes = 15; capacityPerSlot = 10; cutoffMinutes = 20; advanceDays = 2
  } -AllowFail
  Write-Host "  pickup $($b.name)"
}

Write-Host "[vouchers]"
$existingV = ItemsOf (Invoke-Json -Method Get -Url "$base/customers/vouchers?pageSize=50" -Headers $ownerH)
function Ensure-Voucher($headers, [string]$code, [string]$discountType, $value, $minOrder, $requiredOrders = $null, $requiredOrderAmount = $null) {
  if ($existingV | Where-Object { $_.code -eq $code }) {
    Write-Host "  exists $code"
    return
  }
  $expires = (Get-Date).ToUniversalTime().AddMonths(3).ToString("o")
  $body = @{
    branchId = $null; code = $code; type = "Voucher"; discountType = $discountType
    value = $value; minOrderAmount = $minOrder; expiresAt = $expires
  }
  if ($null -ne $requiredOrders) {
    $body.requiredOrders = $requiredOrders
    $body.requiredOrderAmount = $requiredOrderAmount
    $body.countOrdersFrom = (Get-Date).ToUniversalTime().ToString("o")
  }
  $null = Invoke-Json -Method Post -Url "$base/customers/vouchers" -Headers $headers -Body $body -AllowFail
  Write-Host "  created $code"
}
Ensure-Voucher $ownerH "DEMO10K" "FixedAmount" 10000 50000
Ensure-Voucher $ownerH "CHAOMUNG15" "Percentage" 15 30000
Ensure-Voucher $ownerH "TEST50K" "FixedAmount" 50000 100000
Ensure-Voucher $ownerH "HUNT3" "FixedAmount" 20000 0 3 40000

# ---------- Tables (idempotent) ----------
Write-Host "[tables]"
foreach ($branch in $branches) {
  $existing = ItemsOf (Invoke-Json -Method Get -Url "$base/tables/dining-tables?branchId=$($branch.id)&pageSize=5" -Headers $ownerH)
  if ($existing.Count -gt 0) {
    Write-Host "  $($branch.name): already has tables"
    continue
  }
  $rows = @()
  $areas = @(
    @{ name = "Trong nha"; prefix = "A"; count = 6 },
    @{ name = "San thuong"; prefix = "B"; count = 4 },
    @{ name = "Mang di"; prefix = "T"; count = 2 }
  )
  $y = 0
  foreach ($area in $areas) {
    for ($n = 1; $n -le $area.count; $n++) {
      $rows += @{
        areaName = $area.name
        tableCode = ("{0}{1:D2}" -f $area.prefix, $n)
        capacity = if ($area.prefix -eq "T") { 1 } else { 4 }
        positionX = (($n - 1) % 3)
        positionY = $y
      }
    }
    $y++
  }
  $result = Invoke-Json -Method Post -Url "$base/tables/dining-tables/batch" -Headers $ownerH -Body @{ branchId = $branch.id; rows = $rows }
  Write-Host "  $($branch.name): created=$($result.created)"
}

# ---------- Multi-branch staff + workforce ----------
Write-Host "[staff + workforce per branch]"
$q1 = $branches | Where-Object { $_.name -eq "Quan 1" } | Select-Object -First 1
$staffPlan = @(
  @{ branch = ($branches | Where-Object name -eq "Quan 1"); managerEmail = "manager@caphenha.local"; cashierEmail = "cashier@caphenha.local"; baristaEmail = "barista@caphenha.local"; create = $false }
  @{ branch = ($branches | Where-Object name -eq "Thu Duc"); managerEmail = "manager.td@caphenha.local"; cashierEmail = "cashier.td@caphenha.local"; baristaEmail = "barista.td@caphenha.local"; create = $true }
  @{ branch = ($branches | Where-Object name -eq "Quan 3"); managerEmail = "manager.q3@caphenha.local"; cashierEmail = "cashier.q3@caphenha.local"; baristaEmail = "barista.q3@caphenha.local"; create = $true }
)

function Ensure-User($headers, [string]$email, [string]$name, [string]$role, [string]$branchId) {
  $users = ItemsOf (Invoke-Json -Method Get -Url "$base/identity/users?pageSize=200" -Headers $headers)
  $hit = $users | Where-Object { $_.email -eq $email } | Select-Object -First 1
  if ($hit) { return $hit }
  $created = Invoke-Json -Method Post -Url "$base/identity/users" -Headers $headers -Body @{
    fullName = $name; email = $email; password = $brandPassword; role = $role; branchId = $branchId
  } -AllowFail
  if ($created) { Write-Host "  user $email" }
  return $created
}

foreach ($plan in $staffPlan) {
  if (-not $plan.branch) { continue }
  $b = $plan.branch
  Write-Host " -- $($b.name)"
  if ($plan.create) {
    $null = Ensure-User $ownerH $plan.managerEmail ("QL " + $b.name) "BranchManager" $b.id
    $null = Ensure-User $ownerH $plan.cashierEmail ("Thu ngan " + $b.name) "Cashier" $b.id
    $null = Ensure-User $ownerH $plan.baristaEmail ("Pha che " + $b.name) "Barista" $b.id
  }

  $mgr = Login $plan.managerEmail $brandPassword
  $mgrH = AuthHeaders $mgr.accessToken
  $users = ItemsOf (Invoke-Json -Method Get -Url "$base/identity/users?pageSize=200" -Headers $mgrH)
  $cashier = $users | Where-Object { $_.email -eq $plan.cashierEmail } | Select-Object -First 1
  $barista = $users | Where-Object { $_.email -eq $plan.baristaEmail } | Select-Object -First 1

  $emps = ItemsOf (Invoke-Json -Method Get -Url "$base/workforce/employees?pageSize=100" -Headers $mgrH)
  function Ensure-Emp($headers, $user, $position, $contract) {
    if (-not $user) { return $null }
    $hit = $emps | Where-Object { $_.userId -eq $user.id } | Select-Object -First 1
    if ($hit) { return $hit }
    $created = Invoke-Json -Method Post -Url "$base/workforce/employees" -Headers $headers -Body @{
      userId = $user.id; branchId = $user.branchId; fullName = $user.fullName
      position = $position; hireDate = (Ymd (Get-Date).Date.AddMonths(-2)); contractType = $contract
    } -AllowFail
    if ($created) { Write-Host "  employee $($user.email)" }
    return $created
  }
  $empC = Ensure-Emp $mgrH $cashier "Thu ngan" "FullTime"
  $empB = Ensure-Emp $mgrH $barista "Pha che" "PartTime"

  $shifts = ItemsOf (Invoke-Json -Method Get -Url "$base/workforce/work-shifts" -Headers $mgrH)
  function Ensure-Shift($headers, $name, $start, $end) {
    $hit = $shifts | Where-Object { $_.name -eq $name } | Select-Object -First 1
    if ($hit) { return $hit }
    $created = Invoke-Json -Method Post -Url "$base/workforce/work-shifts" -Headers $headers -Body @{
      name = $name; startTime = $start; endTime = $end
    } -AllowFail
    if ($created) { Write-Host "  shift $name" }
    $script:shifts = ItemsOf (Invoke-Json -Method Get -Url "$base/workforce/work-shifts" -Headers $headers)
    return $created
  }
  $morning = Ensure-Shift $mgrH "Ca sang" "07:00:00" "12:00:00"
  $afternoon = Ensure-Shift $mgrH "Ca chieu" "12:00:00" "18:00:00"
  $evening = Ensure-Shift $mgrH "Ca toi" "18:00:00" "22:00:00"

  $today = (Get-Date).Date
  $existingAssign = ItemsOf (Invoke-Json -Method Get -Url ("$base/workforce/shift-assignments?from={0}&to={1}&pageSize=200" -f (Ymd $today), (Ymd $today.AddDays(6))) -Headers $mgrH)
  for ($i = 0; $i -le 3; $i++) {
    $d = Ymd $today.AddDays($i)
    if ($empC -and $morning) {
      $exists = @($existingAssign | Where-Object { $_.employeeId -eq $empC.id -and $_.workShiftId -eq $morning.id -and ("$($_.workDate)").StartsWith($d) })
      if ($exists.Count -eq 0) {
        $null = Invoke-Json -Method Post -Url "$base/workforce/shift-assignments" -Headers $mgrH -Body @{
          employeeId = $empC.id; workShiftId = $morning.id; workDate = $d
        } -AllowFail
      }
    }
    if ($empB -and $afternoon) {
      $exists = @($existingAssign | Where-Object { $_.employeeId -eq $empB.id -and $_.workShiftId -eq $afternoon.id -and ("$($_.workDate)").StartsWith($d) })
      if ($exists.Count -eq 0) {
        $null = Invoke-Json -Method Post -Url "$base/workforce/shift-assignments" -Headers $mgrH -Body @{
          employeeId = $empB.id; workShiftId = $afternoon.id; workDate = $d
        } -AllowFail
      }
    }
  }

  # Payroll adjustment sample (manager)
  if ($empC) {
    $null = Invoke-Json -Method Post -Url "$base/workforce/payroll/adjustments" -Headers $mgrH -Body @{
      employeeId = $empC.id; workDate = (Ymd $today); amount = 50000; reason = "Thuong demo seed"
    } -AllowFail
  }
}

# ---------- Sample orders (Quan 1 cashier) ----------
Write-Host "[sample counter orders]"
$cashierLogin = Login "cashier@caphenha.local" $brandPassword
$cH = AuthHeaders $cashierLogin.accessToken
$products = ItemsOf (Invoke-Json -Method Get -Url "$base/menu/products?pageSize=20" -Headers $cH)
$sellable = @($products | Where-Object { $_.isActive -ne $false } | Select-Object -First 3)
if ($sellable.Count -eq 0) { Write-Host "  no products" }
else {
  for ($n = 1; $n -le 3; $n++) {
    $p = $sellable[($n - 1) % $sellable.Count]
    $orderHeaders = @{
      Authorization = "Bearer $($cashierLogin.accessToken)"
      "Idempotency-Key" = [guid]::NewGuid().ToString()
    }
    $order = Invoke-Json -Method Post -Url "$base/orders" -Headers $orderHeaders -Body @{
      type = "Takeaway"
      tableNumber = $null
      note = "Seed demo order $n"
      promotionCode = $null
      items = @(@{ productId = $p.id; quantity = 1; note = $null; optionIds = @() })
    } -AllowFail
    if ($order) {
      $amount = $order.finalAmount
      if (-not $amount) { $amount = $order.totalAmount }
      if (-not $amount) { $amount = $p.basePrice }
      $null = Invoke-Json -Method Post -Url "$base/payments/transactions/cash" -Headers $cH -Body @{
        orderId = $order.id; amount = $amount; amountTendered = ([decimal]$amount + 10000)
      } -AllowFail
      Write-Host "  order $($order.orderCode) paid cash"
    }
  }
}

# ---------- Staff notices via SQL (no public create API) ----------
Write-Host "[staff notifications SQL]"
$tenantId = $owner.user.tenantId
$q1Id = $q1.id
$sql = @"
INSERT INTO customer.staff_notifications (id, tenant_id, branch_id, audience, kind, dedupe_key, title, body, created_at_utc)
SELECT gen_random_uuid(), '$tenantId', '$q1Id', 'Cashier', 'Seed', 'seed:cashier:ready', 'Demo: ca sang da san sang', 'Day la thong bao seed de test chuong thong bao.', now()
WHERE NOT EXISTS (SELECT 1 FROM customer.staff_notifications WHERE dedupe_key = 'seed:cashier:ready');
INSERT INTO customer.staff_notifications (id, tenant_id, branch_id, audience, kind, dedupe_key, title, body, created_at_utc)
SELECT gen_random_uuid(), '$tenantId', '$q1Id', 'Barista', 'Seed', 'seed:barista:prio', 'Demo: mon uu tien', 'Thong bao seed cho pha che.', now()
WHERE NOT EXISTS (SELECT 1 FROM customer.staff_notifications WHERE dedupe_key = 'seed:barista:prio');
INSERT INTO customer.staff_notifications (id, tenant_id, branch_id, audience, kind, dedupe_key, title, body, created_at_utc)
SELECT gen_random_uuid(), '$tenantId', NULL, 'BranchManager', 'Seed', 'seed:manager:doi-soat', 'Demo: doi soat', 'Thong bao seed cho quan ly chi nhanh.', now()
WHERE NOT EXISTS (SELECT 1 FROM customer.staff_notifications WHERE dedupe_key = 'seed:manager:doi-soat');
"@
$sql | docker exec -i aicos-postgres psql -U aicos_admin -d aicos_customer | Out-Null
Write-Host "  staff notices upserted"

Write-Host "`n=== FULL seed done ==="
Write-Host "Accounts (password Brand@12345):"
Write-Host "  owner@caphenha.local"
Write-Host "  manager@caphenha.local / cashier@caphenha.local / barista@caphenha.local  (Quan 1)"
Write-Host "  manager.td@caphenha.local / cashier.td@caphenha.local / barista.td@caphenha.local"
Write-Host "  manager.q3@caphenha.local / cashier.q3@caphenha.local / barista.q3@caphenha.local"
Write-Host "Vouchers: DEMO10K, CHAOMUNG15, TEST50K, HUNT3"
