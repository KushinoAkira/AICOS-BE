# Seed CapheNha ops data for local feature testing: employees, work-shift templates,
# shift assignments (today + next 6 days), pay rules, and dining tables.
# Usage (from Backend/):  pwsh ./scripts/seed-caphenha-ops.ps1
$ErrorActionPreference = "Stop"
$base = "http://localhost:8080/api/v1"
$brandPassword = "Brand@12345"

function Invoke-Json {
  param(
    [string]$Method,
    [string]$Url,
    [hashtable]$Headers = @{},
    $Body = $null,
    [switch]$AllowConflict
  )
  $payload = if ($null -eq $Body) { $null } else { ($Body | ConvertTo-Json -Depth 10) }
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
    if ($AllowConflict -and $code -in 409, 400, 422) {
      Write-Host "  skip ($code): $detail"
      return $null
    }
    throw "HTTP $Method $Url failed ($code): $detail $($_.Exception.Message)"
  }
}

function Login([string]$login, [string]$password) {
  $r = Invoke-Json -Method Post -Url "$base/identity/auth/login" -Body @{ login = $login; password = $password }
  return $r.accessToken
}

function ItemsOf($response) {
  if ($null -eq $response) { return @() }
  if ($response -isnot [System.Array] -and $response.PSObject.Properties.Name -contains "items") { return @($response.items) }
  if ($response -is [System.Array]) { return @($response) }
  return @($response)
}

function Ymd([datetime]$d) { $d.ToString("yyyy-MM-dd") }

Write-Host "=== Seed CapheNha ops (workforce + tables) ==="

# --- Brand admin: pay rules ---
$ownerToken = Login "owner@caphenha.local" $brandPassword
$ownerH = @{ Authorization = "Bearer $ownerToken" }
Write-Host "[pay-rules]"
$null = Invoke-Json -Method Put -Url "$base/workforce/pay-rules" -Headers $ownerH -Body @{
  fullTimeDayRate = 300000
  partTimeHourRate = 30000
  replacementHourRate = 45000
  overtimeAfterHours = 8
  overtimeMultiplier = 1.5
}
Write-Host "  pay rules set"

$branches = ItemsOf (Invoke-Json -Method Get -Url "$base/identity/branches?pageSize=50" -Headers $ownerH)
$q1 = $branches | Where-Object { $_.name -eq "Quan 1" } | Select-Object -First 1
if (-not $q1) { throw "Branch Quan 1 not found" }
Write-Host "  Quan 1 = $($q1.id)"

# --- Branch manager: employees + work shifts + assignments ---
$mgrToken = Login "manager@caphenha.local" $brandPassword
$mgrH = @{ Authorization = "Bearer $mgrToken" }

Write-Host "[users]"
$users = ItemsOf (Invoke-Json -Method Get -Url "$base/identity/users?pageSize=100" -Headers $mgrH)
$cashier = $users | Where-Object { $_.email -eq "cashier@caphenha.local" } | Select-Object -First 1
$barista = $users | Where-Object { $_.email -eq "barista@caphenha.local" } | Select-Object -First 1
if (-not $cashier -or -not $barista) { throw "cashier/barista accounts missing for Quan 1" }
Write-Host "  cashier=$($cashier.id) barista=$($barista.id)"

Write-Host "[employees]"
$employees = ItemsOf (Invoke-Json -Method Get -Url "$base/workforce/employees?pageSize=100" -Headers $mgrH)
function Ensure-Employee($headers, $user, [string]$position, [string]$contractType) {
  $hit = $employees | Where-Object { $_.userId -eq $user.id } | Select-Object -First 1
  if ($hit) {
    Write-Host "  exists: $($user.email) -> $($hit.id)"
    return $hit
  }
  $created = Invoke-Json -Method Post -Url "$base/workforce/employees" -Headers $headers -Body @{
    userId = $user.id
    branchId = $user.branchId
    fullName = $user.fullName
    position = $position
    hireDate = (Ymd (Get-Date).Date.AddMonths(-3))
    contractType = $contractType
  }
  Write-Host "  created: $($user.email) -> $($created.id)"
  return $created
}
$empCashier = Ensure-Employee $mgrH $cashier "Thu ngan quay" "FullTime"
$empBarista = Ensure-Employee $mgrH $barista "Pha che" "PartTime"
$employees = ItemsOf (Invoke-Json -Method Get -Url "$base/workforce/employees?pageSize=100" -Headers $mgrH)

Write-Host "[work-shifts]"
$shifts = ItemsOf (Invoke-Json -Method Get -Url "$base/workforce/work-shifts" -Headers $mgrH)
function Ensure-WorkShift($headers, [string]$name, [string]$start, [string]$end) {
  $hit = $shifts | Where-Object { $_.name -eq $name } | Select-Object -First 1
  if ($hit) {
    Write-Host "  exists: $name ($($hit.id))"
    return $hit
  }
  $created = Invoke-Json -Method Post -Url "$base/workforce/work-shifts" -Headers $headers -Body @{
    name = $name
    startTime = $start
    endTime = $end
  }
  Write-Host "  created: $name ($($created.id))"
  return $created
}
$morning = Ensure-WorkShift $mgrH "Ca sang" "07:00:00" "12:00:00"
$afternoon = Ensure-WorkShift $mgrH "Ca chieu" "12:00:00" "18:00:00"
$evening = Ensure-WorkShift $mgrH "Ca toi" "18:00:00" "22:00:00"
$shifts = ItemsOf (Invoke-Json -Method Get -Url "$base/workforce/work-shifts" -Headers $mgrH)

Write-Host "[shift-assignments] today + 3 days (stay under 48h/week)"
$today = (Get-Date).Date
$existingAssign = ItemsOf (Invoke-Json -Method Get -Url ("$base/workforce/shift-assignments?from={0}&to={1}&pageSize=200" -f (Ymd $today), (Ymd $today.AddDays(6))) -Headers $mgrH)
function Ensure-Assign($headers, $employeeId, $workShiftId, [string]$date) {
  $hit = @($existingAssign | Where-Object {
    $_.employeeId -eq $employeeId -and $_.workShiftId -eq $workShiftId -and ("$($_.workDate)").StartsWith($date)
  })
  if ($hit.Count -gt 0) {
    Write-Host "  exists: $date employee=$employeeId shift=$workShiftId"
    return
  }
  $created = Invoke-Json -Method Post -Url "$base/workforce/shift-assignments" -Headers $headers -Body @{
    employeeId = $employeeId
    workShiftId = $workShiftId
    workDate = $date
  } -AllowConflict
  if ($created) { Write-Host "  assigned: $date employee=$employeeId shift=$($created.shiftName)" }
}
# One shift/day keeps weekly hours under the 48h cap (5h * 4 days = 20h, 6h * 4 = 24h).
for ($i = 0; $i -le 3; $i++) {
  $d = Ymd $today.AddDays($i)
  Ensure-Assign $mgrH $empCashier.id $morning.id $d
  Ensure-Assign $mgrH $empBarista.id $afternoon.id $d
}

# --- Tables for each CapheNha branch (owner can name branchId) ---
Write-Host "[tables]"
foreach ($branch in $branches) {
  $existing = ItemsOf (Invoke-Json -Method Get -Url "$base/tables/dining-tables?branchId=$($branch.id)&pageSize=5" -Headers $ownerH)
  if ($existing.Count -gt 0) {
    Write-Host "  $($branch.name): already has tables ($($existing.Count)+)"
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
        positionX = (($n - 1) % 3) * 80 + 40
        positionY = $y * 80 + 40
      }
    }
    $y++
  }
  $result = Invoke-Json -Method Post -Url "$base/tables/dining-tables/batch" -Headers $ownerH -Body @{
    branchId = $branch.id
    rows = $rows
  }
  Write-Host "  $($branch.name): created=$($result.created) areasCreated=$($result.areasCreated)"
}

Write-Host "`n=== Done ==="
Write-Host "Test as cashier@caphenha.local / Brand@12345 → Ca của tôi"
Write-Host "Test as manager@caphenha.local / Brand@12345 → Nhân sự / Lịch làm việc"
Write-Host "Floor plan / Bàn & QR: Quan 1, Thu Duc, Quan 3 now have sample tables"
