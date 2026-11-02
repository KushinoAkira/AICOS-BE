# Puts every secret and local setting the services need into the shared user-secrets store `aicos-be`, so that
# Visual Studio (Multiple startup projects) runs the whole backend without Docker for the services.
# Reads values from .env; nothing is printed. Run from the AICOS-BE folder:  powershell -ExecutionPolicy Bypass -File scripts/local-secrets.ps1
# Needs: `docker compose up -d postgres redis` (the other containers must be stopped so the ports 5000 and 5101-5111 are free).
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not (Test-Path .env)) { throw ".env not found - run scripts/init-env.ps1 first" }

$envMap = @{}
Get-Content .env | Where-Object { $_ -match '^\s*[A-Za-z_][A-Za-z0-9_]*=' } | ForEach-Object {
    $k, $v = $_ -split '=', 2
    $envMap[$k.Trim()] = $v.Trim().Trim('"')
}
function Need($k) { if (-not $envMap[$k]) { throw "$k is empty in .env" }; $envMap[$k] }

$secrets = [ordered]@{
    "Database:Password"                = Need "SERVICE_DB_PASSWORD"
    "Jwt:SigningKey"                   = Need "JWT_SIGNING_KEY"
    "ConnectionStrings:Redis"          = "localhost:6379,password=" + (Need "REDIS_PASSWORD")
    "Payments:SecretKey"               = Need "PAYMENTS_SECRET_KEY"
    "Payments:AllowSimulation"         = if ($envMap["PAYMENTS_ALLOW_SIMULATION"]) { $envMap["PAYMENTS_ALLOW_SIMULATION"] } else { "true" }
    "Auth:SeedAdmin:Email"             = Need "SEED_ADMIN_EMAIL"
    "Auth:SeedAdmin:Password"          = Need "SEED_ADMIN_PASSWORD"
    # The gateway only lists 5173 and 3000 by default; the web app runs on 8443 (or 8088 from Docker).
    "Cors:AllowedOrigins:0"            = "http://localhost:8443"
    "Cors:AllowedOrigins:1"            = "http://localhost:5173"
    "Cors:AllowedOrigins:2"            = "http://localhost:8088"
    "Cors:AllowedOrigins:3"            = "http://127.0.0.1:8088"
}

# Optional: real mail (sign-up code, password reset) and Google sign-in. Only the values filled in .env are set.
$optional = [ordered]@{
    "Email:Smtp:Host"      = "SMTP_HOST"
    "Email:Smtp:Port"      = "SMTP_PORT"
    "Email:Smtp:Security"  = "SMTP_SECURITY"
    "Email:Smtp:Username"  = "SMTP_USERNAME"
    "Email:Smtp:Password"  = "SMTP_PASSWORD"
    "Email:FromAddress"    = "SMTP_FROM_ADDRESS"
    "Email:FromName"       = "SMTP_FROM_NAME"
    "Email:AppUrl"         = "APP_URL"
    "Google:ClientId"      = "GOOGLE_CLIENT_ID"
}
foreach ($key in $optional.Keys) {
    $value = $envMap[$optional[$key]]
    if ($value) { $secrets[$key] = $value }
}

foreach ($key in $secrets.Keys) {
    dotnet user-secrets set $key $secrets[$key] --id aicos-be | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not set $key" }
}
Write-Host "Done. Keys now in the aicos-be store:"
dotnet user-secrets list --id aicos-be | ForEach-Object { ($_ -split ' = ')[0] } | Sort-Object
