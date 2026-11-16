# Dashboard: upstream main migration supplement — PROPOSAL ONLY

This package follows `cashier-dashboard-local`; it does not replace or modify its SQL/checksums. Source review branch merges origin/main e10c499 with Dashboard 98c2783. No Item Transfer V2 migrations are included.

## Scope and order (requires separate approval)

1. Stop writers; verify exact operating container/cluster; take fresh full backups of Ordering, Kitchen, Payments, Reporting **and Identity/Workforce**; restore to isolated copies and compare data/schema/history. Earlier full restore covered only the first four databases.
2. Re-run the original four-database read-only preflight and the Identity/Workforce/Payments history/tenant-impact audit. Check all SHA256 files against the reviewed commit. Keep automatic migration/seed/features OFF.
3. Execute the existing four Dashboard expands and per-service ACL proposals only after approval, one database/stage at a time. Payment predecessor for this supplement is `20261114164856_BillSplitPlans`.
4. Execute `01_identity.PROPOSAL.sql` against aicos_identity/identity, `02_payment.PROPOSAL.sql` against aicos_payment/payment, and `03_workforce.PROPOSAL.sql` against aicos_workforce/workforce. Stop immediately on any error; each file is its own transaction, not a cross-service transaction. Execution is NOT approved by preparation/test authorization.
5. Validate schema/history and existing/new-column ACL with the real service roles; no role creation/secret rotation is included. Existing table-wide privileges cover added columns; column-restricted roles may need a separately reviewed GRANT. The rehearsal role is only a synthetic SELECT role, not proof of operating least privilege.
6. Build/restart and feature rollout require a separate explicit decision. SQL success alone does not make the old Docker images support Dashboard.

For each approved file, use psql with `-X -v ON_ERROR_STOP=1` and set `aicos.expected_database`, `aicos.expected_schema`, `aicos.expected_cluster`, `aicos.dashboard_upstream_approved=true` in the SAME connection. Do not paste credentials into CLI/history. Operating cluster must be 7703616372368646182. No command is automatically executed by this README.

## What changes

- Identity: TenantPaymentAndSoftDelete, 6 columns. Upstream also contains an UPDATE that moves unpaid active brands to AwaitingPayment. **This wrapper locks relevant tables and refuses when any brand matches that UPDATE**. Current read-only audit found zero, including the local tenant; recheck at rollout. Nonzero impact needs a separate business decision, not a bypass of this guard.
- Payment: RefundToBankAccount, 3 nullable refund bank columns; existing Staff/Dashboard payment contracts retained. This does not activate Refund UI or change allocation policy.
- Workforce: WorkShiftSoftDelete, AttendanceManagerStatus, CashHandoverAtShiftStart, 6 columns; replaces the existing shift-name unique index with a live-only partial unique index and adds 2 indexes. No table/row is dropped. The DROP INDEX is intentional and transactional; lock time and table/index build size require review.
- These five migrations add 15 columns and zero tables. Together with the old Dashboard package: 15 migrations, 15 new tables and 20 new columns across six service databases.
- No CREATE ROLE, GRANT, REVOKE, DELETE, TRUNCATE, DROP TABLE, secret or feature activation in this supplement. Only EF history INSERTs and the guarded Identity UPDATE from upstream; no payment/receipt update.

## Retry, recovery and rollback

Wrong database/schema/cluster, missing approval, changed history, repeat execution and affected Identity tenants are refused. A failed file rolls back its own transaction; completed files in other databases remain completed. Compare exact history before resuming; do not reapply completed files or forge history.

No automatic Down script is provided: dropping new columns would lose soft-delete, handover or bank evidence. Before any business write, recovery is restore to a **separate isolated database**, verify it, then propose a controlled recovery; never overwrite operating databases automatically. After business writes, retain schema and repair forward. Original Dashboard unused-only rollback remains separately guarded and cannot remove upstream changes.

## Reproduce isolated validation

Run from the Backend review checkout:

    dotnet run --project tests/AICOS.Billing.StackTools -- dashboard-upstream-scripts docs/DASHBOARD_UPSTREAM_DATABASE_PREFLIGHT.json <private-temp-export-folder>
    node scripts/tests/dashboard-upstream-migration.mjs <private-temp-export-folder>

The exporter opens no database. The runner creates only a fresh internal/tmpfs PostgreSQL container, no host port, different cluster, synthetic empty baseline and role, with no operating database connection. Generated baselines stay outside Git. It checks target/approval/repeat refusal, all EF columns, history, SELECT access, clone restore/PK/FK/CHECK and the Workforce partial unique index. It removes only its own container/network.

Evidence: `../../DASHBOARD_UPSTREAM_REHEARSAL_RESULT.json`. Full-data Identity/Workforce restore, existing nonzero-tenant transition and operating ACL verification remain NOT TESTED/BLOCKED until approved. This package is not production-ready or permission to deploy.