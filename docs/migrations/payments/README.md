# Payment schema additive migration

Status: Gate B approved and applied to `aicos_db.public` on 2026-10-26.
Stages 00-06 passed; backfill affected zero rows; all nine checks are zero.
See `docs/GATE_B_EXECUTION_RESULT.md`. Roles/grants and startup await Gate A/C.
Do not reapply 00-05 to the migrated database; 00 is a baseline-only preflight.

## Confirmed business model

- A dine-in `table_session` may contain many `orders`.
- A dine-in bill is unique by `bills.session_id` and aggregates every order in
  that session. Dine-in orders must not receive separate `order_id` bills.
- A takeaway bill is unique by `bills.order_id` and has no `session_id`.
- A bill therefore references exactly one source.
- Cash is confirmed by a Cashier or Manager. `BANK_TRANSFER` QR starts as
  `PENDING` and cannot become `SUCCEEDED` without manual evidence.
- Bill transitions are `UNPAID -> PAID -> CLOSED`.

The live database currently has no rows in `bills`, `bill_items`, or
`payments`. That lowers backfill risk but does not remove the need for a backup
and an approved deployment window.

## Required execution order

The following describes the original live-rollout design. The approved Gate B
used the offline variant in PHASE5_2_CONTROLLED_DEPLOYMENT.md: no application
writers, all financial tables empty, compatible artifact kept stopped throughout
00-06. The schema is now deployed; this list is reference, not a pending action.

Do not run any mutating stage until it is explicitly approved.

1. Create a PostgreSQL custom-format backup and record its checksum/location.
2. Run `00_preflight.sql`. It is read-only and must complete successfully.
3. Run `01_expand_columns.sql`.
4. Run `02_create_payment_events.sql`.
5. Deploy the approved Backend version that dual-writes branch, actor, version,
   close/print state, and payment audit fields. Until that code exists, pause
   bill/payment writes before continuing.
6. Run `03_backfill.sql`. It refuses to invent missing actor identities.
7. Run `04_indexes.sql` with autocommit enabled. It uses `CONCURRENTLY` and must
   not be wrapped in another transaction.
8. Run `05_contract_constraints.sql` only after the new Backend is active.
9. Run `06_verify.sql`; every reported issue count must be zero.
10. Run the PostgreSQL payment integration suite against the dedicated test
    database before enabling mutation endpoints in a shared environment.

`PENDING_PAYMENT_AUDIT_MIGRATION.sql` is now a non-executable pointer to this
staged set.

## Code compatibility gate

Stages 01 and 02 are backward-compatible because new audit columns are nullable
and version/print columns have defaults. The Backend implementation prepared on
2026-10-24 now writes `branch_id`, creator/confirmation actors, version fields,
and append-only payment events. Cash is explicitly confirmed at creation while
bank-transfer QR starts `PENDING` and has a separate confirmation transition.

For a live rollout, expand must precede the compatible Backend, which must
precede contract enforcement. For the approved offline Gate B, the compatible
artifact remained stopped until the complete schema was applied and verified.
Schema prerequisites are now met; Backend startup still requires Gate A/C.

`version` is an application-managed optimistic concurrency token. Every
mutation must update rows with `WHERE version = @expectedVersion`, increment
the version in the same transaction, and return `409` when no row is updated.

`payment_events.request_id` is globally unique. A retry with the same request
and identical payload returns the existing result; a different payload returns
`409`.

## Microservice boundary

Runtime Payments code may use Ordering contracts to resolve session orders and
must not query Ordering or Table tables directly. The one-time backfill is the
only script that joins those legacy shared-schema tables.

The existing Ordering query filters by `(branch_id, session_id)` but the live
database has no index containing `orders.session_id`. That index belongs in a
separate Ordering-owned migration, not this Payments migration.

## Rollback

Prefer application rollback while retaining additive columns and audit data.
Database rollback is split and must run in this order:

1. `90_rollback_constraints.sql`
2. `91_rollback_indexes.sql` with autocommit enabled
3. `92_rollback_schema.sql` only if no payment mutation has occurred

Stage 92 is destructive. If mutations have occurred, export `payment_events`
and every new audit column before considering it. Do not remove financial audit
data merely to roll back an application deployment.

## Main risks

- `ALTER TABLE` requires locks; the scripts use short lock timeouts so they fail
  instead of waiting indefinitely.
- A concurrent index failure can leave an invalid index. Inspect `pg_index`
  before retrying.
- Running Stage 05 before the compatible Backend is deployed breaks bill
  creation because audit fields become mandatory.
- The actor foreign keys preserve audit identity but physically couple the
  current shared schema to `public.users`. Runtime services still must use API
  contracts rather than reading Identity tables.
- Existing timestamps are `timestamp without time zone`; this migration keeps
  that type to avoid an unrelated conversion. Backend code must use the
  project's established timestamp convention consistently.

## Validation record

On 2026-10-24 the complete sequence was executed only on the dedicated local
database `aicos_payment_test`, initialized from `Database.sql`:

- preflight, expand, audit table, backfill, indexes, constraints, and verify:
  passed;
- all new foreign keys and check constraints: validated;
- invalid index count: `0`;
- post-migration issue counts: `0`;
- rollback constraints, indexes, and schema: passed;
- after rollback, `payment_events` was absent and no additive column remained;
- the full migration was applied again successfully, leaving
  `aicos_payment_test` ready for future integration tests.
- Cash/QR/audit/concurrent confirmation/bill close and Table settlement tests
  executed against `aicos_payment_test`: passed without skips.

At that historical 2026-10-24 test rehearsal, no migration statement was
executed on `aicos_db`. Gate B subsequently applied it on 2026-10-26.

On 2026-10-26 Phase 5 repeated the rehearsal on `aicos_payment_test`:

- rollback `90 -> 91 -> 92` passed with existing test Payment rows;
- rollback restored the original nullable baseline columns as well as removing
  the additive schema;
- after test-only financial cleanup, apply `00 -> 06` passed;
- the expanded verifier reported zero missing columns, indexes, constraints, or
  append-only trigger and zero data issues;
- Billing, Payment, and Table PostgreSQL integration tests passed 3/3 with no
  skip.

The production readiness gates and exact runbook are in
`docs/PHASE5_DEPLOYMENT_READINESS.md`.

