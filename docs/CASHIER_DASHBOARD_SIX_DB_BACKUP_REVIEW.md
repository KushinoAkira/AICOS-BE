# Dashboard: six-database full backup / restore review

Date: 2026-11-16. Status: BACKUP/RESTORE PASS; OPERATING MIGRATION AND RUNTIME NOT APPROVED.

## Scope and result

User authorized the next backup/restore step after upstream source reconciliation. Existing PostgreSQL container c014190eed2d (cluster 7703616372368646182) was started only for read-only inventory/pg_dump, then returned to stopped in finally. All application containers were already stopped; none were started, recreated or restarted. The backup guard verified no remaining client connection to any of the six databases.

Fresh custom-format full backups cover Ordering, Kitchen, Payments, Reporting, Identity and Workforce in one maintenance window. Dumps and their detailed manifest are stored only in the private LOCALAPPDATA/AICOS/backups directory, outside Git. The protected Windows ACL allows only the current user and SYSTEM. No rows, password, signing key, token or provider secret was printed or committed.

**Actual restore: 9 groups PASS, 0 FAIL**, cluster 7708609727623475241. Fresh PostgreSQL 16 container, internal-only network, tmpfs, no host port, read-only backup bind. Restore used separate aicos_dashboard_full_* databases; it made zero connections to the operating server and ran no migrations. Each dump size/SHA256 and PGDMP header matched its private manifest. Data row counts/fingerprints, every column/type/nullability, PK/FK/CHECK, index and migration history match. CHECK/index deparser differences are canonicalized by PostgreSQL, preserving complete predicates and uniqueness.

| Database | Tables including EF history | Columns | Data/schema restore |
| --- | ---: | ---: | --- |
| aicos_ordering | 7 | 94 | PASS |
| aicos_kitchen | 12 | 160 | PASS |
| aicos_payment | 7 | 92 | PASS |
| aicos_reporting | 2 | 18 | PASS |
| aicos_identity | 11 | 134 | PASS |
| aicos_workforce | 10 | 128 | PASS |

All preexisting container IDs/images/state/start times/restart counts were unchanged during the restore drill. Only this run's scratch container/network were removed; operating volumes and the backup files were retained.

## Files and validation

- scripts/Backup-LocalCashierDashboard.ps1: optional -IncludeUpstream adds Identity/Workforce to the existing four-database whitelist and connection guard; default four-database mode is preserved. PLAN remains default, no automatic service stop/start.
- scripts/tests/local-dashboard-full-restore.mjs: accepts exactly the original four databases or the reviewed six in fixed order. A malformed five-database manifest was tested: refused before Docker or backup access.
- docs/CASHIER_DASHBOARD_SIX_DB_RESTORE_RESULT.json: sanitized actual evidence; earlier four-DB evidence is preserved separately.
- PowerShell parser, PLAN, Node syntax, Git whitespace checks PASS. All 16 existing SQL checksums unchanged.
- No business code changed. Backend/Flutter suites were not rerun for backup tooling/docs; earlier 850/51 and 362/7 counts are prior checkpoints, not new runs.

## Safe reproduction (approved maintenance only)

    powershell -NoProfile -File scripts/Backup-LocalCashierDashboard.ps1 -IncludeUpstream
    powershell -NoProfile -File scripts/Backup-LocalCashierDashboard.ps1 -Run -IncludeUpstream
    node scripts/tests/local-dashboard-full-restore.mjs <private-backup-directory>

First command is PLAN. The Run command requires the pinned existing PostgreSQL running, all Backend applications stopped and zero clients; it never stops or starts containers itself. A fresh backup directory is created; existing backups are not overwritten. Never put dumps/private manifest in Git. The report intentionally omits the private directory path.

## Remaining approval gates

Backup/restore prerequisite now PASS for all six databases. No automatic permission to execute the proposed migrations, grants or deploy runtime follows from this result.

1. Review/approve exactly four original expand SQL files plus three upstream proposal files and their SHA256SUMS. Recheck container/cluster, all histories, zero writers and tenant-transition impact immediately before execution. Any drift/nonzero Identity impact stops the operation.
2. Review/approve four service GRANT proposals separately. This restore uses --no-owner/--no-acl: it verifies recoverable data/schema, **not** operating ACL recreation, role passwords or least privilege. Role ownership/column ACL checks remain mandatory before rollout.
3. Review/approve runtime/image/feature activation separately, MigrateOnStartup=false. No images have been deployed; the operating stack is still stopped. Keep V2 and unrelated feature flags unchanged/off.

If any migration stage fails, stop and compare exact history. Never automatically Down, overwrite an operating database, restore over existing data or reset a Docker volume. Restore recovery is to a separate isolated copy for verification first; after new business writes prefer forward repair. Earlier E2E blockers (net/refund policy, actual Identity multi-role issuance and branch-wide Bill counts) remain unchanged. QR Mock is not production bank verification.
