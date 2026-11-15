# Cashier money shifts migration review

Not authorized for an operating database. Flags remain OFF. Two additive EF migrations add three tables. No existing rows are rewritten.

## Target and prerequisites

- Payments owns payment.cashier_shifts and payment.collection_outbox in its database. Requires PaymentSessionBills baseline.
- Reporting owns reporting.collection_facts in its separate database. Requires OrderFacts baseline.
- No cross-service foreign key. UUID keys have no sequence grant requirement.
- The only foreign key is collection_outbox.shift_id -> cashier_shifts.id, RESTRICT.
- One OPEN drawer per tenant/cashier across branches; OPEN key uniqueness provides durable retry.
- Verify actual target connection/history before approval. Never use aicos_db or a Windows test database for these test commands.

## Execution order (requires separate operational approval)

1. Keep CashierShifts:Enabled=false and Database:MigrateOnStartup=false. Take fresh backups and restore into a separate drill database.
2. Check SHA256SUMS.txt and review both EF Up/Down methods. Run 00_preflight.READ_ONLY.sql in each target.
3. Set aicos.expected_database and aicos.expected_cluster to verified values, and aicos.cashier_expand_approved=true for the approved stage.
4. Run 01_payments_expand.sql on Payments; run 02_reporting_expand.sql on Reporting. Scripts use EF migration history/idempotent guards; they do not hide an incompatible existing table.
5. Review effective ACL (including inherited grants). After separate permission approval, set aicos.cashier_grants_approved=true and run 03_grants.PROPOSAL.sql on each owned database. No role/password changes.
6. Verify PK/unique/CHECK/owned FK, service connections, no history writes or DELETE permission. Existing blanket grants must be reviewed separately; this script does not silently revoke old access.
7. Only after feature approval configure a separate machine key through environment, Payments Services:Reporting:BaseUrl, and enable both services together. Do not add production secrets to Git.

## Rollback

Disable the new feature/workers first. Prefer rolling code forward and retaining financial data. Scripts 90/91 require target/cluster guards, aicos.cashier_rollback_approved=true AND all new tables empty; otherwise they refuse. Never run EF database update backwards to remove nonempty finance tables. Nonempty recovery uses a reviewed forward fix or restore to a separate copy; do not overwrite operating data.

## Risks and test status

CREATE TABLE/index operations and the owned FK take metadata locks; use a maintenance window, lock_timeout and statement_timeout chosen by DBA. No backfill transaction rewrites existing payment records. Reconciliation reads confirmed sources in batches and creates immutable recovered receipts, unassigned to closed shifts. No inferred historical custody.

The isolated stack runner applies new migrations only after container/network/no-host-port/tmpfs/cluster guard. It checks schema, role access, backups, restore and empty rollback/reapply. Final actual execution evidence is in CASHIER_SHIFT_MANAGEMENT.md and CASHIER_SHIFT_STACK_TEST_EVIDENCE.json. Do not treat older test counts as a pass for a changed SQL manifest.
