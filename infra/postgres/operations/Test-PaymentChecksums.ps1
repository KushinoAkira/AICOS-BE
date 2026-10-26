$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot/../../..").Path
$report = Get-Content "$root/docs/PHASE5_DEPLOYMENT_READINESS.md" -Raw
$entries = [regex]::Matches($report, '\| `([^`]+\.sql)` \| `([a-f0-9]{64})` \|')
if ($entries.Count -ne 10) { throw 'Expected ten frozen SQL checksums.' }
foreach ($entry in $entries) {
    $file = Join-Path "$root/docs/migrations/payments" $entry.Groups[1].Value
    if ((Get-FileHash $file -Algorithm SHA256).Hash -ne $entry.Groups[2].Value) {
        throw "Checksum changed: $file. Rehearsal and approval must be repeated."
    }
}
Write-Output 'PASS: all ten migration/rollback scripts match the Phase 5 review.'
