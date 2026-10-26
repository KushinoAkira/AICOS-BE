# Phase 5.2 - Controlled deployment approval packets

Date: 2026-10-26. Target: PostgreSQL `localhost:5432/aicos_db`, schema `public`.
Current status: Gate B explicitly approved and COMPLETED on 2026-10-26.
Gate A is now conditionally approved and COMPLETED: eleven LOGIN roles with
new hidden-input credentials, actual role connections and effective privileges
verified. PUBLIC ACLs remain unchanged. Gate C remains NOT APPROVED and NOT
EXECUTED. See GATE_A_EXECUTION_RESULT.md. The preparation evidence below
is historical; see GATE_B_EXECUTION_RESULT.md for actual results and the
Docker-engine state warning. Do not reapply the baseline migration.

## Approval boundaries and dependency

| Gate | Explicit scope | Does not authorize |
|---|---|---|
| A | Provision eleven service roles, credentials/LOGIN, reviewed grants and permission probes | Payment migration; service startup; enabling financial writes |
| B | Apply the reviewed Payment SQL stages to the official database while application writers are stopped | CREATE ROLE; GRANT/REVOKE; service startup; destructive rollback |
| C | Start only the named core services and run controlled smoke checks with Payment mutations disabled | Unlisted services; financial transactions; destructive operations; automatic release of maintenance |

The full grants script references the new `payment_events` table and new bill/
payment columns. Running complete A on the baseline schema would roll its grants
transaction back. Labels A/B/C are independent authorizations, not a mandatory
alphabetical execution order.

Proposed order for approval: **B (offline) -> STOP -> A -> STOP -> C -> STOP**.
No gate proceeds automatically when another completes. If the owner wants A
first, stop and prepare/review a baseline-only grant variant; do not silently
skip missing objects or imply approval for B.

The offline B plan is appropriate only while all application writers are stopped
and financial tables remain empty. Earlier runbooks described deploying the
compatible Backend between expand and contract. This plan stages the compatible
artifact but leaves it stopped until C. The SQL bootstrap/integration setup
already applies the full schema before starting test hosts. There is no need
for a live dual-writer during an empty-database maintenance window. If data or
active writers appear, this offline plan must be reviewed again.

## Checks performed in this phase

- All ten Payment migration/rollback SQL hashes still match the frozen Phase 5
  values. No Payment SQL was edited.
- Both applied Identity migration files and their designer/snapshot hashes were
  inventoried; Git reports no changes in the Identity migration directory.
- Backup manifest verification passed; pg_restore --list read 201 archive-list
  lines successfully.
- The retained restore database still matches the backup schema (excluding only
  randomized psql restrict keys) and all public table counts/row digests.
- The current production data fingerprint also matches the backup fingerprint:
  37 public tables, 15 total rows including migration history.
- Read-only preflight passed: bills=0, bill_items=0, payments=0. The additive
  schema remains absent.
- Read-only role audit confirms 0/11 service roles exists.
- No other aicos_db client connection was present at inspection. No listener was
  found on core API ports 5101-5111 or 8080. These are observations, not a durable
  write freeze; recheck immediately before an approved gate.
- Maintenance HTTP test host: **9 passed, 0 failed, 0 skipped**.
- External-database Compose validation passed without starting containers.

No new restore was run in this phase: Phase 5.1's successful real restore was
revalidated read-only. The prior 73-test suite, three real PostgreSQL integration
tests and twelve Docker builds are historical Phase 5.1 results, not fresh runs
claimed for Phase 5.2. Role/ACL SQL was reviewed but not executed.

## Shared preconditions

Before requesting execution of ANY gate, present its exact commands, expected
effects, verification and recovery below together with current observations.
Record a specific approval for that gate. A generic 'continue' or approval for
another gate does not authorize it.

Run commands from AICOS-BE. Example administration connection (no embedded
password):

```powershell
$pgBin = 'C:\Program Files\PostgreSQL\18\bin'
$admin = @('-X','-w','-h','localhost','-p','5432','-U','postgres','-d','aicos_db','-v','ON_ERROR_STOP=1')
```

Inject credentials through a protected PGPASSFILE/secret provider. Do not print
rendered secret-backed Compose config or add secrets to Git. Check native exit
codes after every command and stop on any nonzero result.

Before the first production change:
1. Confirm PostgreSQL endpoint, database/schema, privileged migration credential,
   selected artifact and all hashes.
2. Reconfirm writers are stopped. If an existing process is found, obtain
   explicit maintenance/stop authority; do not stop unrelated applications.
3. Take a NEW backup in a new protected directory. Validate/restore it to a NEW
   restore-test database, retain both, and record hash/location.
4. Capture protected pre-change role/schema/table/column ACL and membership
   inventories. Secure a globals backup separately before role/secret changes.
5. Arrange an encrypted off-machine backup copy and maintenance contact.

Verified retained backup:
`C:\Users\ASUS\AppData\Local\AICOS\backups\20261026_100814\aicos_db.dump`.
SHA-256:
`15763FD692AD7CC77937E5E40573863ED030448FBD3D0E2E10A30C66704C77D3`.
Retained restore database: `aicos_restore_test_20261026_100814`.

Backup/restore command templates (replace placeholders with new approved paths/
names; never overwrite existing artifacts):

```powershell
powershell -ExecutionPolicy Bypass -File infra/postgres/operations/Backup-Aicos.ps1 -OutputDirectory <new-protected-directory>
powershell -ExecutionPolicy Bypass -File infra/postgres/operations/Restore-AicosDrill.ps1 -BackupDirectory <new-protected-directory> -TestDatabase aicos_restore_test_YYYYMMDD_HHMMSS
powershell -ExecutionPolicy Bypass -File infra/postgres/operations/Test-PaymentChecksums.ps1
```

## Gate B packet - Payment migration while Backend is stopped

**Gate B completed after separate explicit approval; commands below are the
reviewed execution inventory, not a reapplication instruction.**

Preconditions: fresh backup/drill, no writers, empty financial tables, schema
baseline confirmed and compatible Backend artifact available. Payment processes
remain stopped, so no restart is implicitly authorized by B. Migration uses the
DBA connection, not an unprovisioned runtime role.

Exact SQL command order (each command is a separate stop/verification stage):

```powershell
& "$pgBin/psql.exe" @admin -f docs/migrations/payments/00_preflight.sql
& "$pgBin/psql.exe" @admin -f docs/migrations/payments/01_expand_columns.sql
& "$pgBin/psql.exe" @admin -f docs/migrations/payments/02_create_payment_events.sql
& "$pgBin/psql.exe" @admin -f docs/migrations/payments/03_backfill.sql
& "$pgBin/psql.exe" @admin -f docs/migrations/payments/04_indexes.sql
& "$pgBin/psql.exe" @admin -f docs/migrations/payments/05_contract_constraints.sql
& "$pgBin/psql.exe" @admin -f docs/migrations/payments/06_verify.sql
```

This is a command inventory, not an instruction to paste/run all lines without
checking each exit code and checkpoint. Do not use --single-transaction around
04. After an error, a subsequent command must NOT be run.

| Stage | Expected effect | Checkpoint |
|---|---|---|
| 00 | Read-only validation | Exact target and reviewed zero financial counts |
| 01 | 14 additive columns; defaults for print/version | Inspect type, nullability/default definitions, not just names |
| 02 | payment_events with 11 columns and PK | Inspect definitions; no application traffic |
| 03 | Backfill existing rows; no-op on current empty tables | Every UPDATE must affect 0 here; stop if baseline changed |
| 04 | Eight concurrent indexes | Each index exists, correct definition and indisvalid=true |
| 05 | 28 named FKs/checks, stronger NOT NULL, append-only function/trigger | All constraints validated; trigger enabled on payment_events |
| 06 | Read-only final inventory/data checks | Nine issue counts zero; also inspect full definitions |

Stages 01/02/03/05 are transactional per file. Stage 04 can partially commit and
can leave an invalid concurrent index. The five-second lock timeouts on ALTER
stages bound waiting; they do not eliminate locking or validation scans.

Backend compatibility:
- Baseline: current Payments Entity/DbContext cannot read missing columns.
- After 01 only: incomplete because payment_events is absent.
- After 02: structurally usable by the audit-aware code, but keep it stopped in
  this plan until C; indexes/contracts/permissions are not complete.
- After 03/04: branch/actor backfill and uniqueness ready; no old writer may return.
- After 05/06: current code writes branch/creator, version and payment events,
  cash confirmation audit, pending bank transfers and explicit QR evidence.
  Closed bills require fully succeeded settlement.
- Old writers that omit mandatory actors/branch are incompatible with contract.

**Recovery:** stop at the failing stage and preserve all objects/data. Current
file transactions roll back on error, but earlier stages remain committed.
Retain additive schema, keep Backend stopped and prepare a reviewed forward fix.
Do not run 90/91/92 automatically. No DROP/DELETE/TRUNCATE or full-database
overwrite is authorized by B. Even invalid-index cleanup needs its exact proposed
object/command reviewed separately. Report stage state and STOP before A.

## Gate A packet - roles and privileges

Current Gate A draft: GATE_A_ROLES_PERMISSIONS_REVIEW.md supersedes the security
script hashes and activation order below. It proposes PUBLIC TEMPORARY removal
and direct CREATE/sequence privilege cleanup, keeps roles NOLOGIN until grants
and masked secret provisioning are verified, and adds read-only privilege/login
probes. These new effects/hashes require separate explicit Gate A approval.

**Approval requested separately after B verification; not executed.**
If A alone is approved while schema is still baseline, report the dependency and
stop before the full grants command. Do not execute B on the strength of A.

Inspect/audit first, then provision:

```powershell
& "$pgBin/psql.exe" @admin -f infra/postgres/security/audit-service-roles.sql
& "$pgBin/psql.exe" @admin -f infra/postgres/security/create-service-roles.sql
```

Expected effect: missing roles are created NOLOGIN, with no superuser, CREATEDB,
CREATEROLE, replication, BYPASSRLS or role inheritance. Existing roles/credentials
are preserved; elevated roles/membership abort the transaction. Roles belong to
the cluster even though the script checks the connected database.

Credential activation is part of A only if explicitly included in its approval.
Open interactive psql and set each secret using masked prompts:

```text
\password identity_svc
\password menu_svc
\password table_reservation_svc
\password ordering_svc
\password payment_svc
\password inventory_svc
\password kitchen_svc
\password workforce_svc
\password reporting_svc
\password customer_svc
\password ai_svc
```

Then, after verifying that each role has its approved secret installed, execute
the corresponding explicit LOGIN commands (no automatic password rotation):

```sql
ALTER ROLE identity_svc LOGIN;
ALTER ROLE menu_svc LOGIN;
ALTER ROLE table_reservation_svc LOGIN;
ALTER ROLE ordering_svc LOGIN;
ALTER ROLE payment_svc LOGIN;
ALTER ROLE inventory_svc LOGIN;
ALTER ROLE kitchen_svc LOGIN;
ALTER ROLE workforce_svc LOGIN;
ALTER ROLE reporting_svc LOGIN;
ALTER ROLE customer_svc LOGIN;
ALTER ROLE ai_svc LOGIN;
```

Apply reviewed grants, then audit again:

```powershell
& "$pgBin/psql.exe" @admin -f infra/postgres/security/public-service-grants.sql
& "$pgBin/psql.exe" @admin -f infra/postgres/security/audit-service-roles.sql
```

Effects: CONNECT/USAGE; service-owned SELECT/INSERT and scoped UPDATE; no
runtime DDL/DELETE rights added. Public-schema CREATE is revoked from PUBLIC,
which is a database-wide permission effect, not just a change to the eleven
roles. Prior direct table/column ACLs for named roles are replaced.
Kitchen's Ordering status/event exception remains explicitly scoped.

Guards reject missing roles, elevated/inherited privileges, role object ownership,
PUBLIC table privileges and an incorrect target database/schema. The script
requires expanded Payment objects and is atomic per file. Role creation,
password/LOGIN changes and grants are separate operations; do not treat all of A
as one global transaction.

Verification before C:
- Eleven role names match Development/Compose service usernames exactly.
- No role owns schemas/tables/database, has elevated attributes or memberships.
- Inspect table AND column privileges (table-only output hides scoped UPDATE).
- Actual role authentication succeeds via the real host and port with its secret.
- Migration admin is not used as a substitute for a runtime login.
- Confirm owned reads and negative cross-service/DDL privileges; use metadata
  and SELECT LIMIT 0, not a trial write or DDL wrapped in rollback.

Example payment_svc connection/probe using protected credentials:

```powershell
& "$pgBin/psql.exe" -X -w -h localhost -p 5432 -U payment_svc -d aicos_db -v ON_ERROR_STOP=1 -c "BEGIN READ ONLY; SELECT current_database(),current_user; SELECT * FROM public.bills LIMIT 0; SELECT * FROM public.bill_items LIMIT 0; SELECT * FROM public.payments LIMIT 0; SELECT * FROM public.payment_events LIMIT 0; SELECT has_column_privilege(current_user,'public.bills','status','UPDATE') AS can_update_status, has_column_privilege(current_user,'public.payments','amount','UPDATE') AS can_change_amount, has_table_privilege(current_user,'public.orders','SELECT') AS can_read_orders, has_table_privilege(current_user,'public.payment_events','DELETE') AS can_delete_audit, has_schema_privilege(current_user,'public','CREATE') AS can_create; COMMIT;"
```

Expected booleans: true, false, false, false, false. Repeat actual login plus
owned SELECT LIMIT 0 for Identity (refresh_sessions/users), Menu (products),
Table (table_sessions), Ordering (orders), Kitchen (order_items). Placeholder
roles must connect but have no owned operational-table read/write grants yet.
Test Docker-origin authentication at C; localhost success alone does not prove
host.docker.internal/firewall/pg_hba access.

**Recovery:** on failure, leave services stopped. A failed grants transaction
rolls back its ACL changes; separately created NOLOGIN roles may remain.
Use the captured pre-change ACL/membership inventory for a separately reviewed
permission repair. Do not DROP roles, REASSIGN OWNED, rotate an existing password,
or blindly REVOKE unrelated permissions. Disabling only a newly provisioned
LOGIN is a candidate containment action to present for approval, not an
automatic step. Report results and STOP before C.

## Gate C packet - controlled startup and smoke tests

**Approval requested separately; not executed.** A and B must have succeeded.
Confirm Docker-origin PostgreSQL access, existing Redis availability/port
conflicts, secrets, immutable image IDs and rollback artifact before starting.
A failed dependency check stops this gate; do not reset Docker or delete volumes.

Payment mutations stay disabled for the entire smoke gate. Configuration alone
does not change a running process; verify the new process environment explicitly.

```powershell
$env:PAYMENT_MUTATIONS_ENABLED = 'false'
$compose = @('--env-file','.env','-f','docker-compose.yml','-f','docker-compose.external-db.yml')
docker compose @compose config --quiet
# Only if Redis startup is explicitly included in C and port 6379 is available:
docker compose @compose up -d --no-deps --no-build --pull never redis
# Start only the reviewed core services; never infer permission for the full stack.
docker compose @compose up -d --no-deps --no-build --pull never identity menu table order payment
docker compose @compose up -d --no-deps --no-build --pull never gateway
```

No bundled PostgreSQL, pgAdmin, Kitchen, AI or placeholder services starts in
this packet. --no-deps prevents Gateway's broad dependency list starting them.
If a different Redis endpoint is selected, review its exact configuration first.

Inspect effective Payment configuration without printing secrets:

```powershell
docker compose @compose exec -T payment printenv Payments__MutationsEnabled Database__MigrateOnStartup
```

Both values must be false. Health alone does not prove correct schema/grants.
Read-only HTTP checks after startup:

```powershell
curl.exe --fail-with-body http://127.0.0.1:5105/health/live
curl.exe --fail-with-body http://127.0.0.1:5105/health/ready
curl.exe --fail-with-body http://127.0.0.1:5105/api/v1/payments/_info
curl.exe --fail-with-body http://127.0.0.1:8080/api/v1/payments/_info
```

Repeat live/ready checks for core services at 5101-5104. _info must report
public; readiness must actually reach PostgreSQL with the service credential.
A protected GET without a token must return 401; with a valid branch-scoped
staff JWT, use these read contracts through Gateway and direct service:

| Request | Expected result |
|---|---|
| GET /api/v1/tables/map | Authorized branch map; route verified against TableOperationsEndpoints |
| GET /api/v1/orders/?sessionId=<existing-owned-session> | Actual session orders |
| GET /api/v1/payments/bills?sessionId=<existing-owned-session> | 200 for an existing bill; otherwise reviewed 404 |
| GET /api/v1/payments/bills?orderId=<existing-owned-takeaway> | Same source/branch isolation checks |
| GET /api/v1/payments/sessions/<existing-owned-session>/settlement-status | With no bill: settled=false |
| Same bill/source with a token from another branch | No cross-branch data; reviewed 403/404 |

Financial tables are currently empty. A 404 alone is not proof of a functioning
existing-bill path. Cover that positive path on aicos_payment_test; do not insert
a demonstration bill into production for smoke testing.

A real authenticated smoke needs a valid JWT supplied through the normal trusted
flow. Login creates refresh_sessions; it is a database write. This read-only
smoke packet does not silently call login/provision accounts. If a token cannot
be supplied, authenticated smoke remains pending until the owner explicitly
approves that authentication action as part of C.

Maintenance rejection can be checked with an INVALID bill-create request (both
source IDs missing) and no token: direct service returns 503 while paused.
If the gate is broken and the request reaches auth/validation, it returns
401/400 and must be reported as a failure, never treated as success. Do not send
a valid financial payload. Gateway with an invalid Authorization header may
return 401 before the maintenance middleware; use no header for this probe.

```powershell
curl.exe -i -X POST http://127.0.0.1:5105/api/v1/payments/bills -H 'Content-Type: application/json' --data '{}'
curl.exe -i -X POST http://127.0.0.1:8080/api/v1/payments/bills -H 'Content-Type: application/json' --data '{}'
```

Expected: 503, Retry-After: 60, no change to bills/payments/payment_events counts.
This specific non-financial rejection probe must appear in the C approval
packet; C does not authorize valid cash/QR/bill mutations or table closure.

**Recovery:** keep maintenance enabled, collect sanitized logs, stop only
services started under C, and present the exact stop commands before execution.
Do not run compose down -v, restore over aicos_db, or return an incompatible old
writer to the contracted schema. Keep schema/audit data intact.

**When maintenance can be turned off:** only after A/B/C evidence passes AND
the owner explicitly approves releasing Payment writes. C's generic startup/
smoke approval is not that release authorization. The proposed future command,
to be shown again for that decision, is:

```powershell
$env:PAYMENT_MUTATIONS_ENABLED = 'true'
docker compose @compose up -d --no-deps --no-build --pull never --force-recreate payment
```

Until that explicit decision, false remains set. An environment change alone
without replacing every Payment replica is not an effective toggle.

## Approval record

| Gate | State | Evidence needed before execution |
|---|---|---|
| B | APPROVED / COMPLETED | All stages passed; see GATE_B_EXECUTION_RESULT.md |
| A | CONDITIONALLY APPROVED / COMPLETED | Eleven LOGIN roles, effective matrix issue_count=0 and eleven actual connection probes PASS |
| C | NOT APPROVED / NOT EXECUTED | A/B verified; exact service list, credentials/token and smoke scope approved |

An approval for A does not approve B or C. An approval for B does not approve A
or C. Completion of C leaves maintenance enabled unless write release was
explicitly and separately authorized.

## Reviewed security and Identity hashes

Payment hashes remain in PHASE5_DEPLOYMENT_READINESS.md and are checked by
Test-PaymentChecksums.ps1. Security scripts reviewed in this phase:

| File | SHA-256 |
|---|---|
| audit-service-roles.sql | B8A4CD94DFE47EDB9EFFEF79C44B382F2D1A833E0948CB106AC78341E2E7F1DB |
| create-service-roles.sql | BB643D0C101D731C7AE876C29AAE1ED4982615065CF10746EDB846F4CAE5B0E7 |
| public-service-grants.sql | 790633AD1CB162E0BC9E6A00E293C95474F3032A94DA53DA5F6E2A2A929967CA |

Applied Identity migration source hashes (no reapplication proposed):
- 20261023040204_BaselineExistingIdentityTables.cs:
  08509EF99719816DF7F9D87ECB533038FDC0E4D0E06583E39DA200B6E4A2B576
- 20261023040310_AddRefreshSessions.cs:
  F84FD1AC11E5F5B7A3EC92BF830F214A0B63E01410BE7C982A881E8F147C4B35

During the initial preparation only this runbook and the changelog changed,
and no gate ran. Subsequently, the owner explicitly approved and Gate B was
executed unchanged. Gate A/C remain pending; executable SQL/code was not edited.
