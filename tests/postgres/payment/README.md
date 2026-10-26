# Dedicated PostgreSQL payment integration database

This environment is disposable and must never point to `aicos_db`.

```powershell
docker compose -f tests/postgres/payment/docker-compose.yml up -d --wait

powershell -ExecutionPolicy Bypass `
  -File tests/postgres/payment/bootstrap-schema.ps1 `
  -BaselineSql D:\ki9\sep490\db\Database.sql

$env:AICOS_TEST_POSTGRES = "Host=localhost;Port=55432;Database=aicos_payment_test;Username=postgres;Password=aicos_payment_test"
$env:AICOS_REQUIRE_POSTGRES_INTEGRATION = "true"
dotnet test --filter "FullyQualifiedName~PostgresBillingIntegrationTests|FullyQualifiedName~PostgresPaymentIntegrationTests"

docker compose -f tests/postgres/payment/docker-compose.yml down
```

The bootstrap script rejects every database name except
`aicos_payment_test`. The container stores PostgreSQL data in `tmpfs`, so the
database disappears with the container and cannot affect the development
`aicos_db` instance on port 5432.

Billing and Payment integration tests enforce the same database-name guard and
reject `aicos_db`. Setting `AICOS_REQUIRE_POSTGRES_INTEGRATION` to `true` makes
a missing PostgreSQL connection fail instead of being reported as skipped.

