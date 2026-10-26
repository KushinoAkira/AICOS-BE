# Phase 5.1 - Deployment blockers
Date: 2026-10-26. Operational target: `aicos_db.public` on Windows PostgreSQL 18.4.

## Decision and scope

Preparation and verification only. Production was inspected with READ ONLY
transactions; backup connections enforced default_transaction_read_only=on.
No production migration, role creation, grants, schema/data changes, stack startup,
volume deletion, commit, push, or Mobile changes were performed.

A separate restore-test database was created and retained. Roles are cluster-wide:
creating a role while connected to a test database on the same server would still
change the production cluster. Accordingly, role/grant scripts were NOT executed.

## Initial blockers and resolution

| Blocker | Resolution | Remaining gate |
|---|---|---|
| 11 service roles absent | Inventory and idempotent provisioning SQL prepared | DBA approval and execution |
| Runtime grants unverified | API/column privilege review, privilege guards and column ACL reset prepared | Effective positive/negative probes after approval |
| No backup/restore evidence | Real backup and isolated database restore completed; schema/data match | Fresh backup and protected off-machine copy at deployment |
| No Payment write pause | Service middleware rejects unsafe Payment methods with 503; nine tests pass | Drain in-flight requests on every replica before migration |
| Docker HTTP 500 | Engine healthy; all 12 images built sequentially | Keep sequential build strategy on this machine |
| Compose targets separate PostgreSQL container | External-database override targets existing Windows host | Approve runtime credentials/network access |
| Deployment/rollback ambiguity | Detailed staged procedure below; ten frozen SQL checksums verified | Owner approves actual execution |

## Scripts and files

- `infra/postgres/security/audit-service-roles.sql`: read-only presence, attributes,
  membership and column privilege inventory.
- `infra/postgres/security/create-service-roles.sql`: transactionally creates
  missing least-privileged NOLOGIN roles; preserves existing credentials and
  rejects elevated roles/membership. Does not create schema or tables.
- `infra/postgres/security/public-service-grants.sql`: review-only grants;
  rejects elevated/owning roles and PUBLIC table grants. Clears existing
  column ACLs as well as table ACLs, then assigns only current API privileges.
- `infra/postgres/operations/Backup-Aicos.ps1`: fixed source aicos_db, custom
  dump, schema dump, before/after row fingerprints, archive inventory, SHA-256
  manifest. Refuses an existing output directory.
- `infra/postgres/operations/Restore-AicosDrill.ps1`: validates required archive
  manifest entries and hashes; accepts only a new timestamped restore-test name;
  restores transactionally without owner/ACL replay; verifies schema/counts/digests.
- `infra/postgres/operations/fingerprint.sql`: READ ONLY per-table counts and
  order-independent MD5 row digests; no raw data printed. Suitable for this small
  database; use streaming verification rather than string_agg for large tables.
- `infra/postgres/operations/Test-PaymentChecksums.ps1`: verifies ten SQL files
  against the frozen Phase 5 report and fails if any changed.
- `docker/Build-Sequential.ps1`: validates the engine/config, builds one image
  at a time, stops on the first failure, never starts the stack.
- `docker-compose.external-db.yml`: points services at
  host.docker.internal:5432/aicos_db; removes their postgres dependency and
  puts the bundled postgres service behind provisioning-only profile.
- PaymentMaintenanceMiddleware, Program registration, nine maintenance tests,
  Compose/.env.example maintenance setting, and this report.

## Roles and least privileges

Read-only audit confirmed 0/11 roles exists, no memberships for them, no PUBLIC
table grants, and no unvalidated constraints. PostgreSQL listens on '*'; this
does not by itself verify firewall/pg_hba permission for Docker clients.

| Service | Role | Secret environment key |
|---|---|---|
| Identity | identity_svc | IDENTITY_DB_PASSWORD |
| Menu | menu_svc | MENU_DB_PASSWORD |
| Table | table_reservation_svc | TABLE_DB_PASSWORD |
| Ordering | ordering_svc | ORDERING_DB_PASSWORD |
| Payments | payment_svc | PAYMENT_DB_PASSWORD |
| Inventory | inventory_svc | INVENTORY_DB_PASSWORD |
| Kitchen | kitchen_svc | KITCHEN_DB_PASSWORD |
| Workforce | workforce_svc | WORKFORCE_DB_PASSWORD |
| Reporting | reporting_svc | REPORTING_DB_PASSWORD |
| Customer | customer_svc | CUSTOMER_DB_PASSWORD |
| AI | ai_svc | AI_DB_PASSWORD |

After approval, DBA runs create-service-roles.sql. New roles deliberately cannot
log in until credentials are installed. In interactive psql, use
`\password payment_svc` (repeat for each role) so passwords are prompted rather
than committed or passed on the command line; then explicitly enable LOGIN.
Store the matching secrets in a secret manager/ignored local environment.
Existing roles are not automatically rotated. Backup/restore scripts consume
PGPASSFILE (preferred) or process-scoped PGPASSWORD; they contain no passwords.

Identity updates only refresh revocation/replacement/version fields. Menu has
SELECT only. Table updates lifecycle fields only. Ordering has SELECT/INSERT on
its four tables. Payment reads/inserts bills/items/payments/events and updates
only bill status/close/version and payment confirmation/version fields.
No refund or print mutation API currently exists; no extra grant is invented.
UUID keys do not require sequence grants for these API writes.

Kitchen retains the documented exception: SELECT orders/items/events,
UPDATE(order_items.status), INSERT order_item_events. Move writes to an
idempotent Ordering contract in a separate release; revoking these privileges
now would break preparation. Placeholder services receive no table privileges.

Role/ACL syntax and permissions have been statically reviewed, NOT proven by
runtime role login: roles are absent and applying them is outside this approval.

## Backup and restore drill

Source: localhost:5432/aicos_db; server/client PostgreSQL 18.
Backup directory (outside Git):
`C:\Users\ASUS\AppData\Local\AICOS\backups\20261026_100814`.

Archive: `aicos_db.dump`.
SHA-256:
`15763FD692AD7CC77937E5E40573863ED030448FBD3D0E2E10A30C66704C77D3`.

Restored database: `aicos_restore_test_20261026_100814`.
It is a test-only copy on the local server, not a second operational database.
No existing database was overwritten or deleted.

Results:
- Archive listing and checksum validation passed.
- All 37 public tables restored; total 15 rows including EF history.
- All per-table row counts and sorted row digests matched.
- Normalized schema-only dumps matched, including PK/FK/index/constraint DDL.
  Only randomized pg_dump restrict keys were excluded from comparison.
- Source before/after fingerprints matched during the backup window.
- Restore to aicos_db and backup directory overwrite were rejected by guards.
- Database and backup artifacts remain available for inspection.

The drill excludes owner/ACL replay by design; it does not prove runtime service
permissions. Cluster globals/password hashes were not exported. DBA must capture
protected globals/ACL evidence before changing roles/grants and arrange an
encrypted off-machine copy. These local artifacts are evidence, not an off-site
disaster-recovery backup. Repeat the backup immediately before deployment.

Example commands after injecting PGPASSFILE, using a NEW timestamp for each run:

```powershell
powershell -ExecutionPolicy Bypass -File infra/postgres/operations/Backup-Aicos.ps1 -OutputDirectory <new-protected-directory>
powershell -ExecutionPolicy Bypass -File infra/postgres/operations/Restore-AicosDrill.ps1 -BackupDirectory <backup-directory> -TestDatabase aicos_restore_test_YYYYMMDD_HHMMSS
powershell -ExecutionPolicy Bypass -File infra/postgres/operations/Test-PaymentChecksums.ps1
```

## Payment maintenance and stage compatibility

Set `Payments__MutationsEnabled=false` for direct .NET execution, or
`PAYMENT_MUTATIONS_ENABLED=false` for Compose (now its default).
POST/PUT/PATCH/DELETE and other unsafe methods under /api/v1/payments return
503 ProblemDetails and Retry-After: 60 before database access. This covers bill
creation, cash, QR creation/confirmation and bill close through both Gateway and
direct service access. GET/HEAD/OPTIONS and health remain available; auth still
protects data reads. Without a setting, native .NET retains existing enabled
behavior; operators must explicitly set false during rollout.

An environment change requires replacement/restart of every Payment process.
The gate cannot cancel already-running requests. Stop/drain all old Payment
replicas first, wait for active transactions/requests to finish, then start the
new version with false after expand. Do not use a mixed rolling deployment with
old enabled replicas. Maintenance does not block superuser SQL or Ordering
writes; coordinate a short business write freeze through verification.

Before 01/02 the current Payment model is incompatible. After 01/02 it can run
behind the gate. Legacy rows with nullable new actors/branch fields must be
backfilled before entity reads; currently production financial tables are empty.
After 03/04/05/06, reviewed runtime privileges and API probes must pass before
re-enabling writes. Auto-migration stays false.

The ten frozen migration/rollback SQL files are unchanged. Stage 05's ADD and
VALIDATE steps share one transaction and retain locks until commit; NOT VALID
does not make that file a zero-downtime migration. Use the maintenance window.
Stage 04 is autocommit/concurrent; inspect each named index after partial failure.

## Exact approved deployment procedure (not executed)

1. Owner approves artifact, SQL hashes, maintenance window and rollback owner.
2. Drain all Payment replicas and coordinate Ordering/Table writes; verify no
   in-flight transactions remain. Keep PostgreSQL/volumes running.
3. Take a fresh protected backup, inventory current ACLs/globals, verify restore.
4. Run read-only 00_preflight.sql and Test-PaymentChecksums.ps1; stop on any drift.
5. DBA provisions roles, credentials and LOGIN after explicit approval.
6. DBA runs 01_expand_columns.sql, verifies 14 new columns, then
   02_create_payment_events.sql and verifies its 11 columns/PK.
7. DBA applies reviewed public-service-grants.sql. Check role membership,
   ownership, table and column grants, and negative cross-service/DDL access.
8. Deploy all new Payment instances with mutations disabled and auto-migration
   false. Use external-db Compose override for the existing Windows database.
9. Run 03_backfill.sql; stop on missing actor/branch. Never invent an actor.
10. Run 04_indexes.sql with autocommit and ON_ERROR_STOP; all eight indexes
    must exist and be valid. Do not wrap in --single-transaction.
11. Run 05_contract_constraints.sql; verify all constraints, NOT NULL and trigger.
12. Run 06_verify.sql; all issue counts zero, plus inspect actual definitions,
    validation flags and trigger enabled state. Name counts alone are insufficient.
13. Run role login/read probes and review staging evidence; mutation integration
    tests stay on aicos_payment_test. Check Gateway/direct-service maintenance,
    Ordering client and Table settlement contract.
14. Owner approves enabling PAYMENT_MUTATIONS_ENABLED=true on all replicas.
    Monitor constraint/permission errors, 409 conflicts and settlement failures.

For configuration validation only (does not start containers):

```powershell
docker compose --env-file .env.example -f docker-compose.yml -f docker-compose.external-db.yml config --quiet
powershell -ExecutionPolicy Bypass -File docker/Build-Sequential.ps1
```

Use secret-backed .env for actual deployment, never the example credentials.
Do not enable provisioning-only or start bundled postgres for this host database.
Host authentication/firewall and service-role credentials still need a reviewed
connection test. Base Compose PostgreSQL uses version 16 whereas the official
host is version 18: do not restore the v18 dump into that v16 container.

## Rollback procedure

Prefer application rollback with additive schema retained and mutations paused.
Never send a legacy writer to the contracted schema. Use a schema-compatible
artifact or keep Payment unavailable until a forward fix is ready.

Before real Payment writes only, and with separate rollback approval, apply
90_rollback_constraints.sql, then 91_rollback_indexes.sql in autocommit, then
92_rollback_schema.sql. Verify baseline definitions and restore pre-change ACLs
from the approved inventory if needed. Stage 90 restores baseline nullability.
Do not automatically drop roles during rollback.

After financial writes occur, do not execute Stage 92: retain audit/events and
forward-fix. Full restore requires an explicit outage/cutover decision because it
would revert other services' later data. No restore command targets aicos_db.
Partial migration: inspect catalog and ticket stage logs before resuming; do not
blindly rerun object-creation SQL or drop valid baseline objects.

## Docker results

Engine 29.7.2 / Docker Desktop 4.90.0, Compose 5.5.1 responded successfully.
Docker reports 12 CPUs and 3,969,077,248 bytes RAM. Historical log evidence:

- host/electron-2026-10-25.log: GET /containers/json repeatedly returned HTTP 500.
- vm/init.log.20261026-100924.218: at 2026-10-25T14:24:59Z a .NET TP Worker
  invoked the OOM killer; global memory exhaustion killed a mongosh process.
- host/com.docker.backend.exe.log: at 2026-10-26T02:54:09Z engine _ping returned
  HTTP 500 while WSL/socket forwarding was still starting.

These establish real VM memory pressure and transient engine unavailability;
they do not prove every historical HTTP 500 had the same cause. Build one service
at a time within the current memory budget. No reset, prune, volume removal,
unrelated container shutdown or production stack startup was used.

Sequential rebuild completed successfully (exit code 0):

| Image | Result |
|---|---|
| aicos/gateway | PASS |
| aicos/identity | PASS |
| aicos/menu | PASS |
| aicos/table | PASS |
| aicos/order | PASS |
| aicos/payment | PASS, includes maintenance middleware |
| aicos/inventory | PASS |
| aicos/kitchen | PASS |
| aicos/workforce | PASS |
| aicos/reporting | PASS |
| aicos/customer | PASS |
| aicos/ai | PASS, reused existing dependency/application layers |

Build success does not claim production readiness or service-role connectivity.
No production containers were started. The Docker build blocker is resolved for
this sequential build; the historical engine issue was not fixed by a reset.

## Validation

- .NET build: passed, 0 warnings, 0 errors.
- Full .NET tests: 73 passed, 0 failed, 8 optional PostgreSQL skips.
- Required real PostgreSQL Billing/Payment/Table run: 3 passed, 0 failed, 0 skipped,
  using aicos_payment_test with AICOS_REQUIRE_POSTGRES_INTEGRATION=true.
- Nine new maintenance HTTP tests passed as part of the full suite.
- dotnet format verification of changed C# files passed.
- Base and external-db Compose configuration validation passed.
- External effective config: all database services depend only on Redis;
  ten .NET services have MigrateOnStartup=false; Payment mutations=false.
- Ten Payment SQL checksums match Phase 5 exactly.
- Production preflight read-only passed: bills/items/payments all zero.
- PowerShell scripts parsed successfully; backup/restore and refusal guards tested.
- SQL create-role/GRANT scripts were NOT executed; no claim of runtime ACL testing.
- Docker: all 12 image builds passed; no HTTP 500 during this build sequence.
- Git diff --check and whitespace inspection of new files passed. Existing
  uncommitted work from earlier phases was preserved; nothing staged or committed.

## Remaining approvals

- Create the eleven cluster roles; install secret-managed credentials and LOGIN.
- Apply grants after expanded Payment objects exist; test effective role access.
- Approve the maintenance window, business write freeze and in-flight drain.
- Approve production Payment migration stages only after fresh backup verification.
- Approve enabling live Payment mutation traffic after final verification.
- Arrange encrypted backup replication and protected globals/ACL inventory.

This phase prepares these operations; it does not authorize or execute them.
