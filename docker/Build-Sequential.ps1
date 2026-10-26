param([string] $EnvFile = '.env.example')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    docker info --format '{{.ServerVersion}}'
    if ($LASTEXITCODE) { throw 'Docker Engine unavailable; no reset attempted.' }
    docker compose --env-file $EnvFile config --quiet
    if ($LASTEXITCODE) { throw 'Compose configuration invalid.' }
    foreach ($service in @('gateway','identity','menu','table','order','payment','inventory','kitchen','workforce','reporting','customer','ai')) {
        docker compose --env-file $EnvFile --progress plain build $service
        if ($LASTEXITCODE) { throw "Docker build failed: $service" }
        Write-Output "PASS: $service"
    }
} finally { Pop-Location }
