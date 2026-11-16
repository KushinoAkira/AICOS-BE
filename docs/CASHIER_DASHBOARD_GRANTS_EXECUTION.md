# Dashboard: approved service GRANT execution

Date: 2026-11-16. Status: THREE GRANT FILES APPLIED AND VERIFIED PASS; RUNTIME NOT STARTED.

## Scope and authorization

User approved the separate three-GRANT step after the permissions review. Applied only Ordering, revised Payments and Reporting proposals, on existing aicos-main-postgres (prefix c014190eed2d), cluster 7703616372368646182. No Kitchen GRANT because required column access was already effective. No Identity/Workforce grant, role/password reset, ownership change, PUBLIC revoke, schema migration or application deployment. PostgreSQL Windows/aicos_db was not used.

Exact files:

1. docs/proposals/cashier-dashboard-local/10_ordering_grants.PROPOSAL.sql.
2. docs/proposals/cashier-dashboard-permissions/12_payment_grants.PROPOSAL.sql.
3. docs/proposals/cashier-dashboard-local/13_reporting_grants.PROPOSAL.sql.

Reviewed revision ffaab80. All 17 package checksums matched before and after execution; no SQL body was edited or guard disabled. The old Payments proposal was not executed.

## Preflight and private recovery snapshot

- Isolated PostgreSQL permission evidence checked: 26 PASS / 1 N/A, not confused with operating tests.
- Existing container/project/cluster pinned; all application containers stopped, no peer database clients.
- Exact full migration histories and preexisting effective privileges matched the read-only audit; Kitchen's column access reverified.
- Raw database/schema/table/column ACLs, owners, grantors and default ACLs saved before mutation in a fresh private LOCALAPPDATA/AICOS/backups/dashboard-acl-* folder. Windows DACL allows only current user and SYSTEM. Snapshots and row fingerprints are outside Git; no raw business rows, passwords or tokens were recorded in public evidence.
- Schema, complete per-table data fingerprints/counts, constraints, indexes and history captured across all six databases for before/after comparison. Role metadata saved without password hashes.

## Execution and verification

Each approved SQL file ran through Docker exec stdin using psql -XAt -v ON_ERROR_STOP=1 -v VERBOSITY=sqlstate, existing administrator credentials, correct expected database/schema/cluster and dashboard_grants_approved=true. Files are individually transactional. Execution stopped for any failed stage; no rollback/revoke was automatic.

| Database / service role | Exit | New tables SELECT/INSERT | UPDATE columns checked | Added explicit ACL entries | COMMIT / verify |
| --- | ---: | ---: | ---: | ---: | --- |
| aicos_ordering / ordering_svc | 0 | 6 | 30 | 46 | PASS |
| aicos_payment / payment_svc | 0 | 8 | 28 | 44 | PASS |
| aicos_reporting / reporting_svc | 0 | 1 | 0 | 2 | PASS |

Every effective UPDATE allowlist was verified; all other columns of the 15 new tables remain non-updatable by the service role. DELETE/TRUNCATE on those new tables remain denied. A SET LOCAL ROLE read-only SELECT proved actual access on each table. INSERT/UPDATE permissions were checked through PostgreSQL catalogs, without business DML or zero-row writes on the operating database.

Ordering's 30 checked UPDATE columns include four legacy columns whose effective owner rights already existed; all 46 added ACL entries still match the exact reviewed column/table allowlist. Payments adds 44 entries; Reporting adds 2. Normalization accounts for implicit owner defaults when comparing ACLs. No previous ACL entry was removed and no added entry was outside the approved grantee/object/privilege allowlist; none adds grant option. Default privileges/database CONNECT were not altered. Kitchen, Identity and Workforce raw ACLs remained unchanged.

All six database states stayed identical: **64 tables / 807 columns**, same rows/fingerprints/history, columns/types/nullability/owners, PK/FK/CHECK/index definitions. Role metadata unchanged; no credentials were reset. Private AFTER files and FINAL.json retain full ACL comparison evidence for a later separately approved recovery, if needed.

## Preflight warning

The first preflight stopped with SQLSTATE 22023 before any stage/GRANT: coalescing NULL column ACL to '{}' produced a zero-dimensional ACL array rejected by aclexplode. Reproduced with a static READ ONLY SELECT; corrected to aclexplode(attacl), where NULL correctly yields no explicit column entries. Confirmed zero committed stages, saved the stopped attempt privately, and reran all guards. No GRANT stage failed, no permission guard was bypassed, no committed stage was replayed and no REVOKE ran.

## Final state and remaining gate

PostgreSQL was started only for maintenance and returned to stopped. All other container IDs/images/state/start times/restart counts unchanged. Kitchen skipped. No Backend/Gateway/Redis application startup, new image, feature activation or runtime secrets change. Runtime and Dashboard remain unavailable until the separate rollout gate.

Next: prepare/check the intended Backend revision, Compose overrides, credentials, MigrateOnStartup=false, feature flags, startup dependencies, effects of workers/reconciliation on existing data and smoke-test plan. Obtain separate approval before image/runtime rollout or feature activation. Do not rerun migrations or GRANT merely to start services.

Operating password authentication and HTTP/API smoke tests are **NOT RUN** here. SET ROLE checks verify PostgreSQL authorization, not authentication with service passwords. Earlier isolated E2E/regression counts are historical; no business regression suite ran against the operating databases. Existing broad ownership on legacy tables/history was preserved, not hardened; do not claim whole-system least privilege or production readiness. Never rollback schema/data to undo a GRANT; ACL recovery must compare private snapshots and revoke only explicitly approved additions under separate approval.

Evidence: [CASHIER_DASHBOARD_GRANTS_EXECUTION_RESULT.json](CASHIER_DASHBOARD_GRANTS_EXECUTION_RESULT.json).
