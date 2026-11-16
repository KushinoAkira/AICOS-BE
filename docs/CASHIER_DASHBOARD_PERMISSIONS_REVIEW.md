# Dashboard permissions after the seven migrations

Date: 2026-11-16. Status: READY FOR SEPARATE GRANT REVIEW, NOT APPLIED.

## Changes

- Created docs/proposals/cashier-dashboard-permissions/12_payment_grants.PROPOSAL.sql. Only the full-history guard adds 20261115093913_RefundToBankAccount; the GRANT body is byte-for-byte identical to the original proposal after newline normalization. The original frozen SQL packages and their 16 checksums are untouched. New proposal SHA256 is in its own SHA256SUMS.txt (17 checked files total).
- Added offline Billing.StackTools dashboard-permissions-scripts mode and scripts/tests/dashboard-permissions.mjs. No business API, financial policy, migration, feature flag or Flutter UI was changed.
- Audited actual Docker local roles/history/ownership/effective ACL using READ ONLY. PostgreSQL was started only for that audit and returned to stopped; other containers were unchanged. No local GRANT/REVOKE, role or credential change.

## Current source audit

Target: existing aicos-main-postgres, cluster 7703616372368646182. Exact proposal histories match the database after seven migrations. All four service roles are LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, no BYPASSRLS; existing CONNECT and schema USAGE are present. New-table ownership is aicos_admin; relevant legacy tables/history remain owned by service roles.

| Service | Proposed scope | Audit / recommendation |
| --- | --- | --- |
| Ordering | SELECT/INSERT on 6 new tables; UPDATE 30 named columns across workflow tables and existing Order/feedback fields | 26 new-table UPDATE column rights missing; 4 legacy-column rights already effective; use original 10_ordering_grants |
| Kitchen | SELECT/UPDATE kitchen_tickets.table_transfer_version | Already effective through existing table ownership; **SKIP local GRANT**, no need to rerun |
| Payments | SELECT/INSERT on 8 new tables; UPDATE 28 named status/lease/shift/split columns | Missing; use revised 12_payment_grants with current history |
| Reporting | SELECT/INSERT collection_facts only | Missing; use original 13_reporting_grants; no UPDATE needed by immutable inbox |
| Identity / Workforce | Existing tables with added upstream columns | Existing table-level SELECT/INSERT/UPDATE cover columns; no extra GRANT proposed |

Exact table/column allowlists are the reviewed SQL; no all-tables/schema-wide write grant, DELETE, TRUNCATE, ownership transfer, superuser, role creation, PUBLIC revoke or credential rotation is proposed. USAGE statements are idempotent. Existing ownership is **not** least-privilege hardening: legacy owners can still modify their old tables/history. The narrow new-table permissions do not revoke or repair that preexisting authority. Keep MigrateOnStartup=false; any ownership hardening is a separate reviewed task.

## Actual PostgreSQL test

**26 executed groups PASS, 1 N/A** on fresh cluster 7708616596042285097: internal network, tmpfs, no host port, no bind/volume, independent random test credentials, synthetic EF schema only. No operating database connection or copied account/customer/payment data. Four roles authenticated via real password/TCP PostgreSQL connections as nonowners; no JWT/mock client.

Tests verify full history, wrong cluster/schema, missing approval and unexpected history refuse without ACL change; the frozen old Payments proposal refuses the new history. Reviewed grants and repeats produce identical ACL. On 15 new tables: SELECT and zero-row INSERT succeed; DELETE/TRUNCATE/ALTER fail 42501. Every explicitly granted UPDATE column succeeds; all other UPDATE columns and migration-history writes are denied. Cross-service database connection is denied. Kitchen's new-table tests are N/A because its proposal only references one existing column. Zero-row statements validate authorization, **not** valid business writes or full HTTP E2E.

Two earlier development runs of the test runner failed on readiness retry handling and psql's pre-connect FATAL formatting; these were corrected and are not counted as a successful suite. The final run passed all actual checks; the vacuous Kitchen new-table category is recorded N/A rather than PASS. Source audit passed separately. Test-only roles/GRANT/REVOKE/DDL ran solely in the isolated container, which was removed with its owned network. Existing containers were unchanged during the test.

Build/offline exporter, Node syntax, SQL checksums and Git whitespace PASS. No Backend/Flutter business regression rerun was needed for this tooling/proposal/docs-only change; older E2E and regression counts are not new evidence here.

## Exact next gate (NOT executed)

Recommended **three** files, sequentially after separate approval:

1. aicos_ordering: docs/proposals/cashier-dashboard-local/10_ordering_grants.PROPOSAL.sql.
2. aicos_payment: docs/proposals/cashier-dashboard-permissions/12_payment_grants.PROPOSAL.sql. **Never use the old Payments file here.**
3. aicos_reporting: docs/proposals/cashier-dashboard-local/13_reporting_grants.PROPOSAL.sql.

Kitchen is skipped because the read-only audit already proves required rights. Original Kitchen proposal remains unchanged and was tested, but is unnecessary on current local state. Re-evaluate if ownership/ACL drifts.

Before execution: verify same container/cluster, all apps stopped, zero writers, current exact histories and the three checksums; record raw relacl/attacl/nspacl/owner/grantor baselines privately for a precise recovery comparison. Use existing credentials safely; do not reset roles/passwords. Each psql connection uses stdin SQL, -X -v ON_ERROR_STOP=1, expected database/schema/cluster and aicos.dashboard_grants_approved=true. Stop on any error, never silently disable a guard.

After each file: query effective table/column/sequence privileges and ensure existing rows/schema/history unchanged. New IDs are UUIDs, no new sequence grants are required. Actual service-role password/authentication and API smoke may be verified only with existing credentials and under the appropriate separately approved runtime step. This read-only audit verifies CONNECT/USAGE, but does not authenticate against the operating server using stored service passwords.

Rollback is not automatic: compare the captured ACL before/after, propose REVOKE of **only newly added explicit rights** under separate approval. Never REVOKE ALL, touch PUBLIC, remove existing owner/inherited rights or rollback schema/data to undo grants.

Runtime/image deployment and Dashboard feature activation remain a separate gate. Stack remains stopped. Permission rehearsal PASS is not operating Dashboard availability, business E2E, production readiness or bank QR verification.

Evidence: CASHIER_DASHBOARD_PERMISSIONS_AUDIT.json and CASHIER_DASHBOARD_PERMISSIONS_TEST_RESULT.json.
