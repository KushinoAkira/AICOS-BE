# Phase 5 - Payment Migration Deployment Readiness

Review date: 2026-10-26  
Production database: `aicos_db`  
Operational schema: `public`  
Rehearsal database: `aicos_payment_test`

## 1. Safety statement

No mutating statement was executed on `aicos_db`. Production inspection used
`BEGIN READ ONLY`, including the Payment preflight and service-role inventory.
No `GRANT`, `REVOKE`, role creation, migration, or data change was executed on
the production database.

The complete migration and rollback were exercised only on
`aicos_payment_test`. The test cleanup removed six test bills and six test
payments after a successful rollback so the migration could be rehearsed again
from a clean baseline. No production data was involved.

## 2. Readiness decision

The SQL stages are technically ready for a controlled deployment. They are not
yet approved for production execution because the operational prerequisites in
Section 11 remain open.

Production preflight result on 2026-10-26:

- database `aicos_db`, schema `public`, transaction read-only: confirmed;
- `bills = 0`, `bill_items = 0`, `payments = 0`;
- none of the 14 additive columns exists;
- `payment_events` does not exist;
- no legacy Payment row requires branch or actor backfill.

The empty financial tables materially reduce backfill, validation, and lock
duration risk. They do not remove the backup, role, permission, traffic-pause,
or approval requirements.

## 3. Migration readiness by stage

| Stage | Purpose | Transaction behavior | Backend compatibility | Stop/go verification |
|---|---|---|---|---|
| `00_preflight.sql` | Reject schema/data drift | Read-only transaction | Baseline/old Backend only | Must report `aicos_db`, `public`, read-only, and expected counts |
| `01_expand_columns.sql` | Add 14 nullable/defaulted columns | Atomic transaction | Old Backend remains compatible; new Backend still cannot start | Verify all 14 definitions and defaults |
| `02_create_payment_events.sql` | Add Payments audit table | Atomic transaction | New Backend model becomes structurally compatible | Verify 11 columns and PK; do not expose mutations yet |
| Backend deploy | Start the approved dual-write code | Application rollout | Required before Stage 05 | Health checks pass; mutation route remains drained/blocked |
| `03_backfill.sql` | Populate branch/status and reject unknown actors | Atomic transaction | Compatible code must already be deployed | Zero null branch/creator rows; on current empty tables all updates are no-op |
| `04_indexes.sql` | Add uniqueness/idempotency/performance indexes | Eight independent autocommit statements | Compatible before and after | All eight indexes exist and `indisvalid = true` |
| `05_contract_constraints.sql` | Add/validate FKs/checks, NOT NULL, append-only trigger | Atomic transaction | Old write path is no longer compatible | All 28 named constraints validated; trigger exists |
| `06_verify.sql` | Final structural/data gate | Read-only transaction | Required before traffic enablement | Every one of the nine `issue_count` values is `0` |

Stages 01 and 02 are expand-only. Stage 05 is the contract boundary. Running
Stage 05 before deploying the current Backend would reject old writes that omit
`branch_id` and `created_by_user_id`.

`04_indexes.sql` must not be passed to `psql --single-transaction` or wrapped in
`BEGIN`, because PostgreSQL forbids `CREATE INDEX CONCURRENTLY` in a transaction
block. A failure may commit earlier indexes and leave an invalid current index;
inspect each named index before an approved retry.

## 4. Backend compatibility

The current Payments Entity/DbContext agrees with the final schema:

- `Bill` writes `branch_id`, `created_by_user_id`, print state, close audit, and
  application-managed `version`;
- cash creates a `SUCCEEDED` payment with confirmation actor/timestamps;
- QR creates `BANK_TRANSFER/PENDING` and does not set `paid_at`;
- manual QR confirmation requires evidence, records actor/timestamps, increments
  versions, and appends a `payment_events` row;
- bill close requires `PAID`, exact succeeded amount, and no pending payment;
- Table calls the Payments settlement API rather than querying Payment tables;
- Payments obtains order details through the Ordering HTTP contract.

Schema compatibility by rollout point:

- before Stage 01: current Payments Backend is incompatible;
- after Stage 01 only: still incompatible because `payment_events` is absent;
- after Stage 02: model-compatible and safe to deploy behind a traffic gate;
- after Stage 05: only the current audit-aware write path is supported.

There is no checked-in feature flag that disables only Payment mutations.
Operations must drain the Gateway Payment route, use maintenance mode, or keep
the Payment workload unavailable until Stage 06 and runtime permission probes
pass. Merely deploying the service while the Gateway route is live is not an
acceptable gate.

## 5. PostgreSQL permission readiness

The read-only role inventory found that none of the 11 expected runtime roles
currently exists in the PostgreSQL cluster:

`identity_svc`, `menu_svc`, `table_reservation_svc`, `ordering_svc`,
`payment_svc`, `inventory_svc`, `kitchen_svc`, `workforce_svc`,
`reporting_svc`, `customer_svc`, and `ai_svc`.

This is a production deployment blocker. Role creation/rotation needs a
separate DBA-approved action with secrets supplied by a secret manager. It must
not use passwords committed to Git.

Static API-to-privilege review of
`infra/postgres/security/public-service-grants.sql`:

| Role | Current API requirement | Prepared privilege |
|---|---|---|
| `identity_svc` | Read identity/branch data; insert accounts, assignments and refresh sessions; revoke/rotate refresh sessions | Matching SELECT/INSERT plus column-scoped refresh update |
| `menu_svc` | Catalog reads | SELECT only |
| `table_reservation_svc` | Read map/sessions; insert sessions/links; change table/session/link lifecycle fields | Matching SELECT/INSERT and column-scoped UPDATE |
| `ordering_svc` | Read orders and insert order aggregate/items/options/events | Matching SELECT/INSERT |
| `kitchen_svc` | Read preparation queue; change only item status; append item event | Matching SELECT, `UPDATE(status)`, and event INSERT |
| `payment_svc` | Query/create bills, items and payments; confirm payment; close bill; append/read audit events | Matching SELECT/INSERT and column-scoped bill/payment UPDATE |
| Placeholder roles | No implemented database-backed API | CONNECT and schema USAGE only |

The grant script intentionally gives `payment_svc` no direct access to
Ordering, Table, Identity, or `refunds`. PostgreSQL FK triggers can enforce
references without granting the runtime role direct table reads. The current
Payment APIs do not implement refunds or print-state mutations.

The grant script references `payment_events`, so it can run only after Stage
02. It has not been executed end-to-end because the roles do not exist and this
phase forbids creating them or applying privileges. Runtime permission probes
therefore remain mandatory after the approved grant step.

## 6. Kitchen and Ordering ownership

Kitchen currently maps Ordering-owned `orders`, `order_items`, and
`order_item_events`, updates `order_items.status`, and inserts events directly.
The column-scoped grants contain the blast radius, but this remains a legacy
cross-service write boundary.

Recommended transition:

1. Keep the scoped exception for the Payment deployment; do not combine a
   boundary rewrite with this release.
2. Add an idempotent Ordering command such as
   `PATCH /api/v1/orders/items/{id}/preparation-status`, with actor, expected
   version, correlation/request ID, transition validation, and event append in
   one Ordering transaction.
3. Let Kitchen call that contract, or publish an idempotent preparation command
   consumed by Ordering through an outbox/inbox flow.
4. Give Kitchen an owned/read projection for its queue if asynchronous delivery
   is selected.
5. Remove Kitchen's direct Ordering write grants only after the new path is
   deployed, observed, and proven recoverable.

Ordering should remain the single write owner. Revoking the current exception
before a replacement exists would break preparation updates.

## 7. Locking, data, and partial-execution risks

| Risk | Impact | Control |
|---|---|---|
| `ALTER TABLE` lock contention | Stage 01/05 can fail or briefly block writers | Pause writes; retain `lock_timeout = 5s`; abort and investigate rather than increasing it blindly |
| Table scans during backfill/validation/NOT NULL | Longer locks/WAL on populated tables | Current tables are empty; re-run preflight immediately before deployment |
| Duplicate source bills | Unique index failure | Preflight rejects duplicate non-null `session_id` or `order_id` |
| Unknown branch/actor | Backfill or NOT NULL failure | Never fabricate actor identity; stop for manual DBA/business review |
| Invalid FK or status/amount | Constraint validation failure | Preflight plus Stage 03; Stage 05 is transactional |
| Partial concurrent indexes | Some indexes committed or invalid | Inspect the eight exact names and `pg_index.indisvalid`; remove only an approved invalid new index before retry |
| Script rerun | Duplicate object failure | Scripts are intentionally fail-fast, not generally idempotent; inspect actual stage state before resuming |
| New writes during migration | Backfill race or contract failure | Traffic drain/write pause from backup through Stage 06 |
| Destructive rollback after real payments | Loss of financial audit fields/events | Never run Stage 92 after mutations; retain schema and forward-fix/application rollback |
| Manual SQL absent from EF history | Operators can lose stage provenance | Record release ID, operator, timestamps, outputs, and SHA-256 values in the deployment ticket |

Stages 01, 02, 03, and 05 are atomic per file. Stage 04 is the only deliberately
non-atomic stage. The grants file is atomic but is a separate approved action.

## 8. Backup and restore checklist

DBA must complete this before any production mutation:

- confirm PostgreSQL server and client major versions and sufficient free space;
- announce the maintenance window and pause bill/payment writes;
- capture database size, row counts, active sessions, EF history, and current
  Payment object inventory in a read-only evidence file;
- create a timestamped custom-format `pg_dump -Fc` of `aicos_db`;
- create a separate schema-only dump;
- capture cluster globals with `pg_dumpall --globals-only`; this file may contain
  password hashes and must be encrypted/restricted;
- generate SHA-256 checksums and copy backups to a second protected location;
- run `pg_restore --list` to prove the archive is readable;
- perform a restore drill into an isolated, non-production PostgreSQL instance
  and run structural plus application read-only verification;
- record backup path, checksum, restore duration, PostgreSQL version, operator,
  and ticket/approval ID.

Never test restore by overwriting `aicos_db`. A production restore requires a
new explicit approval, a write outage, an isolated replacement database or
cluster, validation, and controlled connection cutover. Do not use
`pg_restore --clean` against the live database as an ordinary rollback.

Rollback policy:

- before Payment mutation traffic: application rollback is preferred; if full
  schema rollback is approved, run `90`, `91`, then `92`;
- after any Payment mutation: do not run `92`; preserve columns/events, roll
  back the application to a compatible artifact, and prepare a forward fix;
- full backup restore is a last resort because it discards unrelated writes
  made after the backup.

`90_rollback_constraints.sql` now also restores the original nullability of
baseline `bills.status`, `bills.created_at`, `payments.status`, and
`payments.created_at`, as well as nullable expand columns. This correction was
verified on the test database.

## 9. Exact production execution order

Every numbered stop is a go/no-go gate. Commands must use an approved migration
credential and `ON_ERROR_STOP=1`; credentials come from the deployment secret
store, never command-line literals or repository files.

1. Freeze the Backend artifact and the exact SQL checksums in the release
   ticket. **Owner approval required.**
2. Put Payment writes behind maintenance/drain and confirm no active Payment
   mutation requests. **Operations + owner approval required.**
3. Complete and validate the backup/restore checklist. **DBA required. Stop if
   no verified backup exists.**
4. Run `00_preflight.sql` on `aicos_db`. This is read-only. **Stop unless it
   completes with the expected database/schema and reviewed row counts.**
5. Create/rotate all required service roles using secret-managed credentials.
   **DBA + owner approval required; not performed in Phase 5.**
6. Run `01_expand_columns.sql`. Inspect the 14 columns. **DBA required. Stop on
   timeout, unexpected pre-existing object, or definition mismatch.**
7. Run `02_create_payment_events.sql`. Inspect its columns/PK. **DBA required.**
8. Apply `public-service-grants.sql`, then verify effective privileges and
   negative access checks. **DBA + owner approval required; not performed in
   Phase 5.**
9. Deploy the approved Backend with `Database__MigrateOnStartup=false` and the
   service-role connection strings. Keep Payment mutations drained. Verify
   health, Gateway discovery, Ordering client, and Table-to-Payments client.
10. Run `03_backfill.sql`. **DBA required. Stop if any actor requires manual
    attribution; never invent an actor.**
11. Run `04_indexes.sql` in autocommit mode. After every statement verify that
    the named index exists and is valid. **DBA required.**
12. Run `05_contract_constraints.sql`. **DBA + owner approval required because
    this is the contract boundary.**
13. Run `06_verify.sql`. **Stop unless all nine issue counts equal zero.**
14. Run runtime-role permission probes, read-only production API smoke tests,
    and the dedicated PostgreSQL suite against `aicos_payment_test` (never
    against production).
15. Enable Payment mutation traffic gradually; monitor 409/5xx responses,
    PostgreSQL locks, constraint violations, payment-event insert failures, and
    settlement checks. Record the release as complete.

Do not skip directly from Stage 02 to Stage 05. Do not run Stage 04 inside a
transaction. Do not enable traffic before Stage 06 and permission verification.

## 10. Test results

Executed on 2026-10-26:

- production `00_preflight.sql`: passed in a read-only transaction; financial
  row counts remained zero;
- production service-role inventory: read-only query passed; 0 of 11 roles
  exists;
- test rollback `90 -> 91 -> 92`: passed on non-empty
  `aicos_payment_test`;
- post-rollback test verification: `payment_events` absent, all 14 additive
  columns absent, baseline status/created timestamps nullable again;
- clean test apply `00 -> 06`: passed;
- post-migration verification: 14 columns, eight indexes, 28 named constraints,
  append-only trigger all present; all nine issue counts `0`;
- PostgreSQL Billing + Payment + Table integration subset: 3 passed, 0 failed,
  0 skipped using `aicos_payment_test` with skip disabled;
- full .NET suite: 64 passed, 0 failed; eight optional PostgreSQL tests skipped
  in the default run. These skips are distinct from the required three-test
  PostgreSQL run above, which ran without skips.

The test database was left on the fully migrated schema. No migration or
permission test ran against `aicos_db`.

## 11. Deployment blockers and approval checklist

Deployment remains blocked until all items below are checked:

- [ ] Owner approves the frozen Backend artifact and SQL checksums.
- [ ] DBA creates/rotates the 11 service roles; currently none exists.
- [ ] Owner and DBA approve executing `public-service-grants.sql` after Stage
      02.
- [ ] A verified custom dump, schema dump, globals backup, checksums, and
      isolated restore drill exist.
- [ ] Operations provides a reliable Payment mutation traffic gate.
- [ ] The full Docker image build blocker from Phase 4 is resolved and the
      deployment images are built from the frozen artifact.
- [ ] DBA confirms the maintenance window, lock monitoring, free disk/WAL
      capacity, and abort contact.
- [ ] Owner explicitly approves production Stages 01 through 05.
- [ ] Runtime-role positive and negative permission probes are scheduled after
      the grant step.
- [ ] Rollback decision owner and the no-Stage-92-after-mutations rule are
      acknowledged.

Until these approvals are recorded, `aicos_db` must remain unchanged and the
current Payment Backend must not be pointed at its incomplete Payment schema.

## 12. Reviewed SQL checksums

These hashes identify the scripts tested in this phase:

| File | SHA-256 |
|---|---|
| `00_preflight.sql` | `92a7ea85b4a471cf004ef47108322d184ab5eefe8eb310c347791b1e813a3da9` |
| `01_expand_columns.sql` | `95d334a972f679c271dc19a2b4d5fd338e3551ec616a7e0e64bf5c628e849224` |
| `02_create_payment_events.sql` | `744d15d898a5120208747d2ba62043836d92e4392c0c425b92d848b04a313da9` |
| `03_backfill.sql` | `e87abb3c0290c1a82e0f629f10a21d208ef0fd0d672cf9a65574e019a8372698` |
| `04_indexes.sql` | `cb26917bc582259b8fb8426b040e2bb4c58e3811d811a9a97dfa8d964a1369fd` |
| `05_contract_constraints.sql` | `3b48fe526de75397daae6bf0ef5b8e181ae60a6fbd9786d425ff4f2d2ae15a46` |
| `06_verify.sql` | `ef6e8ed625be76e9c850890008ba970cae24d17e0ef1fc3428a7cf17993220f8` |
| `90_rollback_constraints.sql` | `d3140f9ab6827a0a858f0f4df68034041eb44e4aef3b746bf915241c21dc1ceb` |
| `91_rollback_indexes.sql` | `7f2576751266b328670f507dc4c32b09007971be78d073c3b45da2cbbe43c9cd` |
| `92_rollback_schema.sql` | `d81291d558b40a301b7e67e7bbff90cb0a058c9eb2fd1759a8ef0588c8dfe3b0` |

If any script changes after this report, the DBA must treat the checksum set as
stale, repeat the test rehearsal, and obtain approval for the new set.
