# Dashboard: seven approved SQL files executed on Docker local

Date: 2026-11-16. Status: MIGRATION PASS; GRANT/RUNTIME/FEATURE ACTIVATION NOT EXECUTED.

## Authorization and target

User approved the seven-file migration-only step after the six-database backup/restore report. No approval for GRANT, runtime image deployment or feature activation was inferred. Exact existing aicos-main-postgres container c014190eed2d, cluster 7703616372368646182; six aicos_* service databases and their service schemas. PostgreSQL Windows/aicos_db and all other containers were untouched. Reviewed source revision: 7f3a8ba; all 16 SQL SHA256 checksums matched before and after execution. No SQL proposal was edited.

Fresh private backup created 2026-11-16T03:42:20.4427816Z; six PGDMP sizes/hashes matched the manifest and the 9/9 isolated restore result. Backup ACL was rechecked (current user/SYSTEM only), and existing data was compared to the backup immediately before execution.

## Preflight and execution

- Exact container/project/cluster, database, six full migration histories and baseline columns PASS.
- All Backend applications stopped, zero peer database clients PASS.
- Old row counts/fingerprints including original migration history matched the verified backup PASS.
- Identity transition predicate matched zero tenants before execution; SQL UPDATE affected exactly 0 rows.
- Reviewed 00_preflight.READ_ONLY.sql executed successfully on Ordering/Kitchen/Payments/Reporting.
- Before each stage: all applications still stopped, zero peer clients, other container snapshots unchanged and the approved SQL content unchanged.
- Each file ran through Docker exec stdin using psql -XAt -v ON_ERROR_STOP=1 -v VERBOSITY=sqlstate, with expected database/schema/cluster and only the stage-specific dashboard_expand_approved or dashboard_upstream_approved setting. No passwords in CLI/logs. Actual seven-file order and exit codes:

| File | Database | Exit code | Commit and verification |
| --- | --- | ---: | --- |
| cashier-dashboard-local/01_ordering_expand.sql | aicos_ordering | 0 | PASS |
| cashier-dashboard-local/02_kitchen_expand.sql | aicos_kitchen | 0 | PASS |
| cashier-dashboard-local/03_payments_expand.sql | aicos_payment | 0 | PASS |
| cashier-dashboard-local/04_reporting_expand.sql | aicos_reporting | 0 | PASS |
| cashier-dashboard-upstream/01_identity.PROPOSAL.sql | aicos_identity | 0 | PASS |
| cashier-dashboard-upstream/02_payment.PROPOSAL.sql | aicos_payment | 0 | PASS |
| cashier-dashboard-upstream/03_workforce.PROPOSAL.sql | aicos_workforce | 0 | PASS |

After every stage: exact resulting migration IDs and EF columns/types/nullability matched; all constraints were validated and old row values remained equal to backup. Original history rows were retained; 15 expected migration IDs were added in total. All 15 newly created business tables are empty. No Bill/payment/account/test fixture was created.

## Before / after

Counts include each service's EF history table.

| Database | Tables | Columns |
| --- | ---: | ---: |
| aicos_ordering | 7 -> 13 | 94 -> 167 |
| aicos_kitchen | 12 -> 12 | 160 -> 161 |
| aicos_payment | 7 -> 15 | 92 -> 180 |
| aicos_reporting | 2 -> 3 | 18 -> 25 |
| aicos_identity | 11 -> 11 | 134 -> 140 |
| aicos_workforce | 10 -> 10 | 128 -> 134 |
| TOTAL | 49 -> 64 | 626 -> 807 |

Twenty new columns are on existing tables; the remaining column increase belongs to the fifteen new tables. Defaults on added columns are those in the approved SQL. Workforce's shift-name unique index was replaced transactionally with the reviewed live-only partial index; no table or business row was dropped.

PostgreSQL was started for the maintenance operation and returned to stopped in finally. Other container IDs/images/state/start times/restart counts remained unchanged. No application/Gateway/Redis was started, no image rebuilt/deployed, no role/credential/ACL changed, no feature flag enabled, and no rollback was run. Backup files remain private outside Git.

## Warning and next gate

An initial preflight-only attempt stopped before any migration stage because the temporary runner compared short docker ps IDs to full inspect IDs and mistakenly included the intentionally started PostgreSQL in the unchanged-container snapshot. It recorded zero stages/commits. The comparison was corrected to full IDs; all preflight checks ran again before the successful seven stages. No SQL stage failed, no guard was disabled and no automatic rollback/retry of committed stages occurred.

**Next-gate blocker:** the original 12_payment_grants.PROPOSAL.sql guard expects history through BillSplitPlans. The database now also contains 20261115093913_RefundToBankAccount. That old proposal would safely refuse; it must be revised as a separate reviewed/checksummed proposal and tested in isolation before any approval to grant. Do not bypass history verification or re-run migrations to satisfy it. No GRANT was attempted here. Other service grants and inherited/column privileges must also be checked against actual ownership.

Remaining gates: prepare/validate compatible permissions; obtain separate GRANT approval; then separately approve image/runtime rollout and feature activation. The operating Dashboard is still unavailable because the stack remains stopped and current images have not been updated. Existing business-policy/E2E limitations remain unchanged; no claim of production readiness or real bank QR verification.

Evidence: [CASHIER_DASHBOARD_SEVEN_SQL_EXECUTION_RESULT.json](CASHIER_DASHBOARD_SEVEN_SQL_EXECUTION_RESULT.json). This task changed documentation only in Git; SQL files and application code were unchanged. No business/API tests ran against the operating databases. Prior E2E/regression counts are prior checkpoints, not new tests in this migration execution.
