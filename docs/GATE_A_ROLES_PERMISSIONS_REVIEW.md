# Gate A - Service roles and permissions review

Historical draft. The owner subsequently approved Gate A conditionally and
explicitly excluded both PUBLIC revokes. GATE_A_CONDITIONAL_EXECUTION.md records
the revised hashes/scope and actual progress; do not execute the old draft.

Date: 2026-10-26. Target: localhost:5432/aicos_db, schema public.
**PREPARED ONLY. Gate A NOT APPROVED / NOT EXECUTED. Gate C NOT EXECUTED.**

GATE_B_EXECUTION_RESULT.md and PHASE5_2_CONTROLLED_DEPLOYMENT.md were reviewed.
Live READ ONLY checks reconfirm 38 tables / 244 columns and all nine Gate B
verification counts at zero. No migration was reapplied. All eleven runtime
roles remain absent; no password was generated/assigned and no CREATE ROLE,
ALTER ROLE, GRANT or REVOKE was executed.

## 1. Eleven role/configuration matches

The ten source Development connection strings, Docker usernames and AI config
agree with this list. Compiled bin/obj copies were excluded from the source audit.

| Microservice | PostgreSQL role | Credential environment key |
|---|---|---|
| Identity | identity_svc | IDENTITY_DB_PASSWORD |
| Menu | menu_svc | MENU_DB_PASSWORD |
| Table | table_reservation_svc | TABLE_DB_PASSWORD |
| Ordering | ordering_svc | ORDERING_DB_PASSWORD |
| Payments | payment_svc | PAYMENT_DB_PASSWORD |
| Kitchen | kitchen_svc | KITCHEN_DB_PASSWORD |
| Inventory | inventory_svc | INVENTORY_DB_PASSWORD |
| Workforce | workforce_svc | WORKFORCE_DB_PASSWORD |
| Reporting | reporting_svc | REPORTING_DB_PASSWORD |
| Customer Engagement | customer_svc | CUSTOMER_DB_PASSWORD |
| AI | ai_svc | AI_DB_PASSWORD |

Every new role is proposed as NOLOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE,
NOINHERIT, NOREPLICATION and NOBYPASSRLS, with search_path=public. Existing roles
are preserved rather than silently rotated/altered; elevated flags, membership
or object ownership cause an abort for DBA review. No membership, ownership,
default-privilege change or WITH GRANT OPTION is proposed.

## 2. Exact proposed table grants

All eleven roles receive CONNECT on aicos_db and USAGE on public.
The following is the complete table-level grant list:

| Role | SELECT | INSERT |
|---|---|---|
| identity_svc | tenants, branches, users, roles, tenant_users, staff_assignments, refresh_sessions | users, roles, tenant_users, staff_assignments, refresh_sessions |
| menu_svc | categories, products, product_variants, product_option_groups, product_options, branch_products | None |
| table_reservation_svc | table_areas, cafe_tables, table_sessions, table_session_tables | table_sessions, table_session_tables |
| ordering_svc | orders, order_items, order_item_options, order_item_events | orders, order_items, order_item_options, order_item_events |
| kitchen_svc | orders, order_items, order_item_events | order_item_events |
| payment_svc | bills, bill_items, payments, payment_events | bills, bill_items, payments, payment_events |
| inventory_svc | None | None |
| workforce_svc | None | None |
| reporting_svc | None | None |
| customer_svc | None | None |
| ai_svc | None | None |

No table-level UPDATE is granted. Exact column-level UPDATE grants:

| Role | Table | Allowed UPDATE columns |
|---|---|---|
| identity_svc | refresh_sessions | revoked_at_utc, replaced_by_session_id, version |
| table_reservation_svc | cafe_tables | status |
| table_reservation_svc | table_sessions | status, closed_at |
| table_reservation_svc | table_session_tables | left_at |
| kitchen_svc | order_items | status |
| payment_svc | bills | status, closed_at, closed_by_user_id, version |
| payment_svc | payments | status, confirmed_by_user_id, confirmed_at, evidence_reference, paid_at, version |

There are no DELETE, TRUNCATE, REFERENCES, TRIGGER, MAINTAIN, sequence or schema
CREATE grants. No new public function EXECUTE grant is added. Runtime roles are
not migration administrators and do not receive EF history access.

## 3. API and ownership cross-check

| Implemented code/API | Database operation behind the matrix |
|---|---|
| Identity login/me | Reads assignments, branches, tenant users/users/roles; login inserts refresh_sessions |
| Identity staff provisioning | Reads active tenant/branch and existing user/role; inserts users/roles/membership/assignment |
| Identity refresh/logout | Reads session and assignment; inserts replacement session; changes only revocation/replacement/version |
| Catalog query | Reads branch_products and product/category/variant/option graph; no catalog mutation |
| Table map/session reads | Reads four Table-owned tables |
| Table open | Inserts session/link and changes cafe_tables.status |
| Table close | Uses Payments HTTP settlement contract; changes session.status/closed_at, link.left_at, table.status |
| Table cleaned | Changes cafe_tables.status |
| Order create/retry/read | SELECT/INSERT on four Ordering tables; catalog/session obtained through HTTP clients |
| Kitchen queue/preparation | SELECT over shared Ordering projection, UPDATE only order_items.status, INSERT event |
| Bill query/create | SELECT/INSERT bill aggregate/items; Order billing source comes via HTTP client |
| Cash / pending QR | INSERT payment/event; change bill status/version |
| Manual QR confirmation | Change payment confirmation/evidence/paid_at/version, bill status/version, INSERT event |
| Close bill | Change status/closed_at/closed_by_user_id/version; no payment success inferred from print or Bar |

Domain methods, EF mappings, includes and SaveChanges paths were inspected.
The shared audit interceptor only stamps IAuditable entities; these concrete
legacy models do not introduce extra UPDATE columns beyond the matrix.

Refunds is mapped in PaymentsDbContext but no current API queries/mutates it;
no refund privilege is added. Print state is mapped/initialized on bill INSERT,
but no print-status mutation API exists, so no print UPDATE grant is invented.

Kitchen's writes to Ordering-owned status/events remain the documented legacy
exception. It is explicitly bounded by column/event privileges; a future
Ordering command/event contract can remove this exception in its own release.

Inventory/Workforce/Reporting/Customer have no active DbSets/DB features; AI
feature routes return 501. Their foundation tables do not justify speculative
runtime permissions. Health SELECT 1 needs a valid connection, not business-table
access.

These are service credentials, not per-cashier DB logins. Branch authorization
still belongs to API logic; this is table/column isolation, not row-level tenant
or branch isolation.

## 4. Sequence review

Live metadata: public sequences=0; identity columns=0; nextval defaults=0.
UUID IDs are supplied/generated by application/EF; timestamp defaults use now().
Current API inserts require no sequence USAGE/SELECT/UPDATE grant.

The proposed script clears any stale direct public-sequence ACL for the eleven
roles (currently zero sequences) and grants none back. A future serial/identity
feature needs a separately reviewed sequence grant, not a blanket privilege.

## 5. Exact proposed REVOKE effects

The current reviewed public-service-grants.sql will, after approval only:

1. REVOKE CREATE ON SCHEMA public FROM PUBLIC.
   Currently PUBLIC has USAGE but not CREATE, so this is presently a no-op.
2. **REVOKE TEMPORARY ON DATABASE aicos_db FROM PUBLIC.**
   This is NEW in this preparation. PUBLIC currently has TEMPORARY and CONNECT.
   This removes inherited temp-table creation for ALL non-owner users of
   aicos_db, not just AICOS runtime roles. DBA/owner effects are not overridden.
3. REVOKE CREATE, TEMPORARY ON DATABASE aicos_db FROM all eleven named roles.
4. REVOKE CREATE ON SCHEMA public FROM all eleven named roles.
5. Remove their existing public column privileges using an explicit catalog
   loop, then REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public from them.
   Table REVOKE alone would not remove old column grants.
6. REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public from them (currently
   no sequences).
7. Apply only the positive grants in Sections 2 and 4.

All of these changes are one grants-file transaction. There is no CASCADE,
table/data deletion, role deletion or schema migration. Grants to other named
users are not cleared. PUBLIC TEMPORARY is deliberately a wider shared-database
effect that MUST appear in the owner's Gate A approval; do not silently apply
this new draft using approval of an older checksum.

PUBLIC CONNECT on aicos_db and PUBLIC USAGE on public are retained for existing
users. Other databases are not changed. PostgreSQL roles are cluster-wide:
PUBLIC CONNECT/TEMP privileges in other databases are outside this plan, so it
does not claim cluster-wide connection isolation. If needed, review pg_hba and
other-database ACL policy separately.

Guards abort for wrong database/schema, missing roles, elevated/membership/
ownership cases or PUBLIC business-table grants. No WITH GRANT OPTION is added;
audit existing grant options and stop for review if a reused role has any
unexpected delegation. No automatic CASCADE cleanup or password rotation.

## 6. Credential workflow (not executed)

1. Use a password manager/secret provider to create ELEVEN independent secrets,
   preferably 32 random bytes encoded as 64 hex characters. Do not print values,
   paste them into chat, shell command arguments, SQL files or tickets.
2. After CREATE ROLE approval/application, keep roles NOLOGIN while applying
   reviewed grants. Check their effective ACLs with expect_login=false.
3. Open trusted interactive psql as DBA. Use masked \password <role> prompts
   for each role listed in Section 1. Do not use ALTER ROLE ... PASSWORD
   '<plaintext>' in shell/SQL source or a transcript.
4. SCRAM-SHA-256 is currently configured. Confirm the setting and log/audit
   policy again during execution. Stop terminal/session recording and never
   enable SQL/parameter tracing around secret provisioning. Password verifiers
   are also sensitive; do not include them in logs or reports.
5. Store the same role-specific secrets in the protected deployment provider.
   Native connection tests use a PostgreSQL passfile outside Git with restricted
   NTFS ACLs; libpq reads it directly. Existing .env is ignored by Git, but a
   secret manager is preferable. Example files contain placeholders only.
6. Enable LOGIN only after secrets and ACLs are ready and that activation was
   explicitly included in Gate A approval. Use ALTER ROLE <reviewed-role> LOGIN;
   for the eleven names, without granting any new role attributes/membership.
7. Verify using the ACTUAL runtime credential, not admin SET ROLE. Keep Backend
   services stopped; no API login/bootstrap action is part of Gate A.

The fresh-container Docker init shell script is NOT this Gate A provisioner.
It passes a password in a psql variable argument and is not suitable for this
no-password-in-command-line procedure. It will not run against the existing
host database in this gate.

Do not render secret-backed docker compose config or dump process environments.
For AI DATABASE_URL, percent-encode reserved URI characters if non-hex passwords
are chosen. None of the passwords or passfiles was created in this preparation.

## 7. Prepared post-grant verification

Files:
- audit-service-roles.sql: READ ONLY role attributes/memberships, column grant
  options, database/schema ACL sources and sequence ACLs.
- verify-service-permissions.sql: READ ONLY complete positive/negative effective
  matrix across public tables/columns and sequences, DB/schema privileges,
  safe role flags, memberships, ownership and expected LOGIN state.
- Test-ServiceRoleConnections.ps1: actual psql login and SELECT LIMIT 0 over each
  role's approved read tables. Uses a passfile outside the repo, suppresses raw
  connection errors, clears PGPASSWORD precedence during checks and restores
  environment afterward. Starts no service.

Example execution inventory AFTER separate Gate A approval/application:

```powershell
$pg = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$admin = @('-X','-w','-h','localhost','-p','5432','-U','postgres','-d','aicos_db','-v','ON_ERROR_STOP=1')

& $pg @admin -f infra/postgres/security/audit-service-roles.sql
& $pg @admin -f infra/postgres/security/create-service-roles.sql
& $pg @admin -f infra/postgres/security/public-service-grants.sql
& $pg @admin -v expect_login=false -f infra/postgres/security/verify-service-permissions.sql
# STOP unless SUMMARY issue_count=0; install secrets with masked prompts.
# Enable approved LOGIN roles only after that step succeeds.
& $pg @admin -v expect_login=true -f infra/postgres/security/verify-service-permissions.sql
powershell -ExecutionPolicy Bypass -File infra/postgres/security/Test-ServiceRoleConnections.ps1 -PgPassFile <protected-outside-Git-passfile>
```

Each command is checked independently; do not paste/run as an unattended batch.
Credentials in the DBA connection also come from a protected provider.

Verification acceptance criteria:
- Eleven ACTUAL role logins connect to aicos_db as the expected current_user.
- Owned SELECT LIMIT 0 succeeds; placeholders connect but cannot read business
  tables. No application row values are dumped.
- Effective table SELECT/INSERT exactly matches Section 2; table UPDATE=false,
  and only the listed columns have UPDATE.
- DELETE/TRUNCATE/REFERENCES/TRIGGER/MAINTAIN=false, schema/database CREATE=false,
  database TEMPORARY=false; no sequence privilege.
- No elevated role flags, membership, ownership or unexpected grant option.
- Column checks detect unexpected UPDATE on payment amount/audit columns; Table
  checks detect cross-service read/write access.
- Example negative read: payment_svc selecting public.users LIMIT 0 must fail
  with 42501 (test it in its own READ ONLY connection). No trial INSERT, UPDATE,
  DELETE, CREATE TABLE or transaction-rollback mutation test runs on production.
- The metadata verifier's SUMMARY issue_count must be zero; exit code 0 alone
  is insufficient.
- Actual insert/update/transaction behavior is not proven by metadata checks.
  Business mutation tests remain on aicos_payment_test; production API startup/
  smoke and Docker-origin connection checks belong to Gate C.

## 8. Checks actually performed now

| Check | Result |
|---|---|
| Gate B state | PASS: 38 tables/244 columns, nine issue counts zero; only 06 READ ONLY verification ran |
| Roles/config usernames | PASS: eleven source role names match |
| Proposed table/column existence | PASS: zero missing targets |
| Sequences/defaults | PASS: zero sequence/nextval/identity dependencies |
| CREATE ROLE review | New roles explicitly non-privileged/NOLOGIN; reused unsafe roles abort |
| Grants review | API matrix fits; proposed TEMP/CREATE/sequence hardening documented, not applied |
| Updated audit SQL | Executes successfully in READ ONLY |
| Effective permission verifier syntax | Executes READ ONLY; correctly reports 11 missing roles, SUMMARY=11 |
| Actual runtime ACL/login result | PENDING: roles do not exist; no role was created to simulate a pass |
| Connection probe | PowerShell syntax checked; NOT executed |
| Payment migration hashes | All ten frozen SQL hashes unchanged; migrations not rerun |

The public table owner is postgres (38 tables); schema owner is pg_database_owner.
The only public function is the non-SECURITY-DEFINER rejecting Payment trigger
function. PUBLIC has no business table/column grants.

Current server settings inspected: password_encryption=scram-sha-256,
log_statement=none, log_min_duration_statement=-1,
log_parameter_max_length=-1 and log_parameter_max_length_on_error=0.
These observations are not permission to log secret provisioning; recheck before
execution and keep both plaintext and verifiers out of logs.

## 9. Reviewed file hashes

These security drafts supersede their historical Phase 5.2 hashes. Payment
migration files were not changed.

| File | SHA-256 |
|---|---|
| create-service-roles.sql | 01F5846EAF8DFF4612F62923CB06A6D26BE53F2F9602FE26843953F7ACE837A4 |
| public-service-grants.sql | 4F009D10FF82F5056C888DC5518C4E872C6DD3F45AB56FB90264E3FA479B937B |
| audit-service-roles.sql | 09637C61062F2C41E233C9A3436457A8903E3BA6D7765F1EE9ABA0874B272E4D |
| verify-service-permissions.sql | F0C693C7CF3D761E526DCD3E28B390F616F3EC5EC627E71C2FC8F31F2A79F119 |
| Test-ServiceRoleConnections.ps1 | ED03CB86F0203CD34C3D23FD7B622129907D3E199DC1098936E1E06FB71D2FD9 |

## 10. Approval and recovery boundaries

Before execution, obtain Gate A approval explicitly covering the current hashes,
eleven NOLOGIN roles, exact grants/revokes including PUBLIC TEMPORARY, masked
credential provisioning and LOGIN activation. Capture pre-change role/ACL/global
evidence and a protected post-Gate-B backup before modifying access.

Role creation and grants are separate transactions; password and LOGIN changes
are separate steps. A failure stops before the next action. A failed grants
transaction rolls back that file, but new NOLOGIN roles can remain.
Use captured ACLs for a separately reviewed permission repair; do not drop
roles, clear unrelated grants, restore over aicos_db, undo Gate B or launch
services as a recovery shortcut.

Gate A readiness: prepared for review. Execution still needs the owner's
separate approval, including the database-wide TEMPORARY change.
Gate C, service startup, Mobile changes, commit/push and Payment mutations remain
outside this preparation.
