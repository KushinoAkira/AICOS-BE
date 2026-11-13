[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$baseline = 'ff3a474'
$guardRevision = '5636b52'
$target = Join-Path $env:TEMP ('aicos-ordering-session-hotfix-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $target | Out-Null
$archive = Join-Path $target 'baseline.zip'
git -C $repo archive --format=zip --output=$archive $baseline
if ($LASTEXITCODE -ne 0) { throw 'Cannot export baseline.' }
$source = Join-Path $target 'source'
Expand-Archive -LiteralPath $archive -DestinationPath $source
$utf8 = New-Object Text.UTF8Encoding($false)
$guardPath = 'src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/CounterOrderRequests.cs'
$guard = git -C $repo show ($guardRevision + ':' + $guardPath)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read reviewed session guard.' }
[IO.File]::WriteAllText((Join-Path $source $guardPath), ($guard -join "`n") + "`n", $utf8)
$contracts = Join-Path $source 'src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderContracts.cs'
$text = [IO.File]::ReadAllText($contracts)
$old = 'string? TableQrToken = null);'
if (-not $text.Contains($old)) { throw 'Unexpected DTO baseline.' }
[IO.File]::WriteAllText($contracts, $text.Replace($old, 'string? TableQrToken = null, Guid? ExpectedSessionId = null);'), $utf8)
$endpoints = Join-Path $source 'src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs'
$text = [IO.File]::ReadAllText($endpoints)
$old = 'CounterIdempotency = true, CounterTableSessions = true, TableOrderFilter = true }'
if (-not $text.Contains($old)) { throw 'Unexpected capability baseline.' }
[IO.File]::WriteAllText($endpoints, $text.Replace($old, 'CounterIdempotency = true, CounterTableSessions = true, TableOrderFilter = true, ExpectedTableSession = true }'), $utf8)
# Reuse the reviewed HTTP guard case, without importing later Cup/Recovery dependencies.
$testLines = git -C $repo show ($guardRevision + ':tests/AICOS.Services.IntegrationTests/MobileCounterTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Cannot read reviewed session tests.' }
$testText = ($testLines -join "`n") + "`n"
$boundary = $testText.IndexOf('    [Fact]', $testText.IndexOf('private async Task<HttpClient> CashierAsync'))
if ($boundary -lt 0) { throw 'Unexpected test layout.' }
$testText = $testText.Substring(0, $boundary).Replace('MobileCounterTests', 'MobileSessionHotfixTests') + "}`n"
$testText = $testText.Replace('capabilities.GetProperty("counterIdempotency").GetBoolean().Should().BeTrue();', 'capabilities.GetProperty("counterIdempotency").GetBoolean().Should().BeTrue(); capabilities.GetProperty("expectedTableSession").GetBoolean().Should().BeTrue();')
[IO.File]::WriteAllText((Join-Path $source 'tests/AICOS.Services.IntegrationTests/MobileSessionHotfixTests.cs'), $testText, $utf8)
# No migrations/models/entities are copied or modified. No Docker or DB mutation here.
[pscustomobject]@{ Source = $source; Baseline = $baseline; GuardRevision = $guardRevision } | ConvertTo-Json
