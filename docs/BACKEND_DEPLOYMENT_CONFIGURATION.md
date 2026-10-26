# Backend Deployment Configuration - `aicos_db.public`

Phase 5.1 update: use `docker-compose.external-db.yml` alongside the base file
for the existing Windows PostgreSQL instance. Database name alone does not
identify the server: the base file's `postgres` container is a different
instance. The override uses `host.docker.internal`, removes the PostgreSQL
startup dependency and keeps bundled PostgreSQL behind a provisioning profile.
Payment mutations now default to disabled in Compose until explicitly enabled
after migration/permission verification. See `PHASE5_1_DEPLOYMENT_BLOCKERS.md`
for the tested backup/restore scripts and the current deployment procedure.

Status updated 2026-10-26: Gate B is applied to aicos_db.public (38 tables/244
columns). Conditional Gate A is complete: eleven LOGIN roles, scoped ACLs and
new hidden-input credentials; actual role connections/reads and effective
permissions PASS. PUBLIC ACLs remain unchanged. Gate C/service startup is not
approved. See GATE_A_EXECUTION_RESULT.md and GATE_B_EXECUTION_RESULT.md.

## Deployment contract

- One operational PostgreSQL database: `aicos_db`.
- One current application schema: `public`.
- Every service keeps its own DbContext and table ownership boundary.
- PostgreSQL roles enforce table access; a shared schema does not permit
  unrestricted cross-service SQL.
- Runtime roles cannot create or alter database objects.
- `MigrateOnStartup` is `false` in checked-in settings, launch profiles, and
  Docker Compose. Migrations are an explicit deployment step.

## Connection strings

Before standardization, Docker and four placeholder services used database
`aicos`, implemented services used `postgres@aicos_db`, and several development
files contained the shared password `aicos_dev`.

After standardization:

| Service | Database | Schema | Runtime role |
|---|---|---|---|
| Identity | `aicos_db` | `public` | `identity_svc` |
| Menu | `aicos_db` | `public` | `menu_svc` |
| Table Reservation | `aicos_db` | `public` | `table_reservation_svc` |
| Ordering | `aicos_db` | `public` | `ordering_svc` |
| Payments | `aicos_db` | `public` | `payment_svc` |
| Inventory | `aicos_db` | `public` | `inventory_svc` |
| Kitchen | `aicos_db` | `public` | `kitchen_svc` |
| Workforce | `aicos_db` | `public` | `workforce_svc` |
| Reporting | `aicos_db` | `public` | `reporting_svc` |
| Customer | `aicos_db` | `public` | `customer_svc` |
| AI | `aicos_db` | `public` | `ai_svc` |

The checked-in Development connection strings intentionally omit passwords.
Set the complete connection string outside Git when running a service directly:

```powershell
$env:ConnectionStrings__Default = 'Host=localhost;Port=5432;Database=aicos_db;Username=ordering_svc;Password=<secret>'
dotnet run --project src/Services/Ordering/AICOS.Ordering.Api
```

Do not put a real password in `appsettings*.json`, launch profiles, shell
history, or command output captured by CI. Prefer a secret manager or CI secret
in shared environments. A local ignored `.env` is acceptable for Docker
development.

## Docker credentials

Copy `.env.example` to the ignored `.env` and replace every placeholder with a
generated value. Docker Compose requires the admin password, a distinct
`*_DB_PASSWORD` for every runtime role, and a JWT signing key of at least 32
random characters. AI provider keys remain optional.

`docker compose config` renders interpolated environment variables. Treat its
output as secret material and do not upload or commit it. `AI_DB_PASSWORD` must
be URI-encoded if it contains a reserved URL character because the Python
service receives a PostgreSQL URL.

Changing `.env` does not rotate roles in an existing PostgreSQL volume. Perform
password rotation through an approved DBA process. Never delete a production
volume to rotate credentials.

## PostgreSQL permissions

Gate A conditional approval supersedes the original review: PUBLIC CREATE and
TEMPORARY revokes were excluded and removed before execution. Eleven NOLOGIN
roles and their direct table/column ACLs are now applied; PUBLIC/other-role ACLs
were verified unchanged. See GATE_A_CONDITIONAL_EXECUTION.md for current hashes
and masked credential/LOGIN progress. TEMPORARY remains inherited from PUBLIC.

The review-only script is
`infra/postgres/security/public-service-grants.sql`. It is deliberately outside
`infra/postgres/init`, so Docker cannot execute it automatically. The script:

- aborts unless the target database is exactly `aicos_db` and all service roles
  exist;
- revokes public-schema object creation from `PUBLIC`;
- clears table privileges only for the named AICOS runtime roles;
- grants `CONNECT` and schema `USAGE`;
- grants only the operations currently used on service-owned tables;
- limits Kitchen to order reads, `order_items.status` updates, and event inserts;
- gives Inventory, Workforce, Reporting, Customer, and AI no table privilege
  because they have no implemented database-backed feature yet;
- does not grant `CREATE`, `ALTER`, ownership, superuser, or bypass-RLS rights.

The script references `payment_events`, so it must only be considered after the
approved Payment schema migration creates that table. It has not been executed.
Migration credentials must be separate from runtime credentials.

## Docker PostgreSQL bootstrap

`infra/postgres/init/01-schemas-and-roles.sh` now creates login roles with
separate environment-supplied passwords and sets their `search_path` to
`public`. It no longer creates per-service schemas, installs pgvector, or
automatically applies grants.

The bootstrap script runs only for a fresh Docker PostgreSQL data directory. It
does not modify an existing volume and must not be used as a procedure for the
official `aicos_db`. The repository still does not automatically build the 37
table baseline on a clean container; provisioning/restoring that approved
baseline remains a separate DBA step.

## Gateway and service discovery audit

Gateway routes and container discovery are aligned:

| Public route | Gateway cluster | Docker destination | Local destination |
|---|---|---|---|
| `/api/v1/identity/**` | `identity` | `identity:8080` | `localhost:5101` |
| `/api/v1/menu/**` | `menu` | `menu:8080` | `localhost:5102` |
| `/api/v1/tables/**` | `table` | `table:8080` | `localhost:5103` |
| `/api/v1/orders/**` | `order` | `order:8080` | `localhost:5104` |
| `/api/v1/payments/**` | `payment` | `payment:8080` | `localhost:5105` |
| `/api/v1/inventory/**` | `inventory` | `inventory:8080` | `localhost:5106` |
| `/api/v1/kitchen/**` | `kitchen` | `kitchen:8080` | `localhost:5107` |
| `/api/v1/workforce/**` | `workforce` | `workforce:8080` | `localhost:5108` |
| `/api/v1/reporting/**` | `reporting` | `reporting:8080` | `localhost:5109` |
| `/api/v1/customers/**` | `customer` | `customer:8080` | `localhost:5110` |
| `/api/v1/ai/**` | `ai` | `ai:8000` | `localhost:5111` |

Synchronous service clients use `Services__{Name}__BaseUrl` and call containers
directly rather than looping through the Gateway. Bearer tokens and correlation
IDs are forwarded by the shared HTTP handler. No route change was required.

## Deployment sequence

1. Review the Phase 3 database audit and this configuration change.
2. Create or rotate service roles without deleting any volume.
3. Backup `aicos_db` and verify checksums.
4. Apply approved Payment migration stages in their documented order.
5. Review and explicitly apply `public-service-grants.sql` using a DBA account.
6. Verify effective privileges with the query at the end of the script.
7. Inject runtime connection strings and secrets.
8. Deploy with `Database__MigrateOnStartup=false`.
9. Check health, `_info`, Gateway routes, Payment settlement, and permission logs.

## Remaining risks

- The grants cannot be tested against `aicos_db` until explicitly approved.
- New tables do not inherit runtime grants automatically; every future migration
  needs a reviewed grant stage.
- `payment_events` does not exist in the main database yet.
- A fresh Docker database has no automatic 37-table baseline.
- Kitchen still writes selected Ordering-owned columns/tables as a documented
  legacy boundary exception.
- All .NET contexts in `public` currently share one EF history table. Automatic
  migration is disabled to avoid concurrent ownership problems.
- Inventory, Workforce, Reporting, Customer, and AI currently receive only
  connection/schema access; their existing foundation tables remain inaccessible
  until the corresponding service feature is implemented and reviewed.

## Validation record

Validation performed on 2026-10-25:

- all checked-in JSON configuration files parsed successfully;
- `docker compose --env-file .env.example config --quiet` passed;
- all 10 .NET service descriptors resolved to schema `public`;
- all 10 launch profiles resolved to `MigrateOnStartup=false`;
- all 10 .NET Development connection strings resolved to `aicos_db` and their
  service-specific usernames without checked-in passwords;
- Gateway route/cluster names matched Docker service DNS names and local ports;
- `dotnet format AICOS.sln --verify-no-changes --no-restore` passed;
- .NET tests passed: 64 passed, 0 failed, 8 optional PostgreSQL tests skipped
  because the default run did not enable a test connection string;
- the AI Docker image completed successfully during the first Compose build;
- the complete Compose image build did not complete: Docker Desktop returned
  HTTP 500 from `dockerDesktopLinuxEngine` and later failed even while building
  only `gateway`. No container was started and no volume was removed or reset;
- host-side AI tests did not run because neither `python` nor the Windows `py`
  launcher is installed.

The Docker engine failure is an environment blocker, not a passing build. Repeat
the full image build after Docker Desktop is repaired/restarted by the developer.

No migration, privilege statement, or data mutation was executed while
preparing this configuration.
