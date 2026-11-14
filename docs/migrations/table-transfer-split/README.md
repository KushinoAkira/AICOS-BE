# Table transfer and split allocation migration runbook

Three additive EF migrations, NOT approved on any operating database. Flags TableTransfer and SplitBill remain OFF. ItemTransfer is not implemented or registered pending design approval; setting a flag cannot activate it. Group payment has no mutation endpoint.

## Prerequisites and ownership

- Ordering: OrderingSessionCheckout -> 20261114164841_DurableTableTransfer (one transfer journal/outbox table, owned FK to table_sessions, unique tenant/session/version).
- Kitchen: KitchenBaristaWorkflow -> 20261114164849_TableTransferRoutingVersion (bigint NOT NULL default 0 on kitchen_tickets).
- Payments: CashierMoneyShifts -> 20261114164856_BillSplitPlans (draft plan and append-only operation/audit tables, unique tenant/session, owned operation -> plan FK).
- Prior SessionBilling/CashierShift migrations may still be pending on the actual operating stack; inspect history before choosing a target. Do not treat this list as proof they were applied.
- Split plan BillId is a logical deterministic reference validated by Payments, not a second original Bill. A Bill need not be materialized for a draft plan, so there is deliberately no FK to session_bills.
- No cross-service FK or direct cross-database reads.

## Review and execution order, after separate approval

1. Keep new flags OFF, MigrateOnStartup=false and mutation traffic controlled. Take fresh backups, verify hashes and restore into separate drill databases.
2. Verify exact database/cluster/history, existing columns/tables/indexes/FKs and service ACL with 00_preflight.READ_ONLY.sql. Stop for any incompatible preexisting structure.
3. Pin aicos.expected_database and aicos.expected_cluster in the same psql session. Set aicos.operations_expand_approved=true only for an approved stage. Apply 01 Ordering, 02 Kitchen, 03 Payments to their separate owned databases.
4. Scripts use EF history/idempotent guards; re-running skips equivalent applied migrations but does not silently accept a different table. The new Kitchen column must exist BEFORE deploying code that maps it, even with flags OFF.
5. Review and separately approve 04_grants.PROPOSAL.sql with aicos.operations_grants_approved=true. Verify effective/inherited/owner rights; do not assume an explicit narrow GRANT removes old broad access.
6. Compare row counts/original-column fingerprints, constraints, migration history and service role SELECT/INSERT/update-column/deny checks. Set lock_timeout/statement_timeout according to DBA policy; use a maintenance window for metadata locks.
7. Only after full readiness approval deploy matching Ordering/Kitchen versions with identical TableTransfer flag and a separate machine key via environment. Never enable one side alone. Configure Services:Kitchen:BaseUrl, MigrateOnStartup=false. Web client compatibility must be checked before enabling the new 202/PENDING_SYNC contract.
8. SplitBill enables draft allocation only; it never enables group payment. CashierShift/Reporting/financial rules remain independent.

## Rollback

Disable flags and workers first; retain financial/audit data and prefer forward fixes. 90/92 refuse nonempty journals/plans/audits. 91 refuses any routing version !=0, so it cannot erase applied transfer fencing/history. All require target/cluster and aicos.operations_rollback_approved=true. Do not use destructive rollback on an operating ledger. Restore only to a separately verified copy.

## Validation

The --operations isolated runner verifies SHA256, no-host-port/tmpfs/cluster/network guards, applies migrations on test only, restores separate copies, rolls back unused structures, re-applies the reviewed SQL, checks repeated execution and denies DELETE for service roles. Nonempty rollback refusal is verified after real HTTP scenarios. Final results are in TABLE_OPERATIONS_IMPLEMENTATION.md and TABLE_TRANSFER_SPLIT_E2E_EVIDENCE.json.
