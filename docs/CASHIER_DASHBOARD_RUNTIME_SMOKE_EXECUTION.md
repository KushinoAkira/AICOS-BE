# Runtime smoke execution: infrastructure PASS, applications BLOCKED

Date: 2026-11-16. User explicitly approved existing PostgreSQL/Redis startup and sequential replacement of 11 apps for features-OFF smoke, excluding migrations/GRANT/reconciliation.

## Actual execution

| Stage | Result |
| --- | --- |
| Candidate IDs/labels/flags/unchanged credentials, 17 earlier SQL checksums, rollback DPAPI | PASS |
| Existing PostgreSQL start/health/pinned cluster | PASS; not recreated |
| Existing Redis start/health/real AUTH PING | PASS; not recreated |
| Full EF/schema compatibility | BLOCKED: Menu and Customer |
| Application start/recreate | **NOT RUN: 0/11**, all old app IDs/images remain |
| Gateway DNS/HTTP/401 checks | NOT RUN |
| Financial activation, login/JWT, migration/GRANT/reconciliation | NOT RUN |

Only infrastructure is running. Windows PostgreSQL/aicos_db and unrelated containers were not touched. No application has been replaced, no smoke business request was sent, and no automatic rollback was performed. Do not describe this as runtime PASS or working Mobile/Dashboard.

## Root causes confirmed READ ONLY

1. Menu products.image_url: actual varchar(1000), model varchar(400000). Exactly pending existing EF migration **20261113170918_ProductPictureUpload** after **20261021102711_ConstraintsAndIndexes**. This affects future large picture writes; the gate refuses to silently waive model compatibility even though simple reads might work.
2. Customer: actual 89 business columns vs 104 model columns. Missing member_orders (11 columns) and vouchers.campaign_id/count_orders_from_utc/required_order_amount/required_orders (4). Exactly pending existing EF migration **20261113175515_HuntVouchers** after **20261101183341_StaffNotifications**.
3. Offline model exporter also lacked Customer. Added its project reference/model/delta export, without changing application source or any EF migration. Full diagnostic then exposed the two real schema gaps above. The first stop was not proof the database needed an arbitrary new migration; existing migrations suffice. The build/model check was not bypassed.

Seven other .NET models match the actual database. AI has no EF model/business DB implementation. Details: RUNTIME_MODEL_SCHEMA_DIFFERENCES.json. Candidate images still pin application source 396b7e8596bb1d993272d6fe7c0620b6c62db7c4; only test tooling/docs changed afterward.

## Prepared fix, not applied locally

Package: docs/proposals/runtime-compatibility/. Reuses the exact two existing EF Up/Down SQL scripts with target/history/approval guards; no edits to closed migrations.

- 01_menu_expand.PROPOSAL.sql: widen only image_url, no value rewriting or image truncation; record the existing pending migration.
- 02_customer_expand.PROPOSAL.sql: add one table/four columns/indexes/own-service FK. Upstream includes note-derived historical member_orders INSERT; wrapper locks source tables and **refuses if any eligible row exists**. Current READ ONLY preview: **0 candidates / 0 distinct Orders**. Raw existing backfill remains in SQL and must affect 0 rows; it is not silently removed or marked applied without execution.
- 10_customer_grants.PROPOSAL.sql: separate permission scope, SELECT/INSERT on member_orders, UPDATE only is_cancelled/updated_at_utc/updated_by (including audit interceptor writes). No DELETE/TRUNCATE, ownership transfer, PUBLIC revoke or role/password changes. Old voucher tables remain service-owned, no extra grants needed.
- 90_menu_rollback.UNUSED_ONLY.sql: refuses narrowing when any picture exceeds 1000 characters.
- 91_customer_rollback.UNUSED_ONLY.sql: refuses if member_orders has rows or any new voucher column is used. No automatic Down.

Customer member_orders references customer.customers only; OrderId has unique index but no cross-service FK. Upstream FK is ON DELETE CASCADE from Customer deletion: review this behavior explicitly. Direct DELETE is not granted, but parent deletion can still cascade under existing customer-erasure behavior. No Payment/Bill financial record is changed by this package.

## Backup and actual tests

New full backup includes eight service databases (previous six + Menu/Customer), private outside Git, same current-user/SYSTEM ACL. Restored into a fresh internal/tmpfs/no-host-port cluster: **11/11 groups PASS**, exact data fingerprints/schema/constraints/indexes/history. Earlier six-DB backup is preserved; it alone did not cover these two new targets.

Compatibility SQL tested on a different fresh synthetic PostgreSQL cluster: **13/13 groups PASS**, zero operating connections. Tests cover exact EF columns, missing approval/wrong target/cluster, duplicate application refusal, validated constraints, own-service CASCADE FK, unique OrderId, actual customer_svc password auth and INSERT/audited cancellation UPDATE, amount/DELETE denial, refusal of nonzero note backfill before DDL, preservation of long images, refusal to rollback nonempty/new-used data, unused rollback and reapply. No customer/payment data was copied into this synthetic fixture test. Full-data restore is the separate evidence above.

Backup helper now accepts -IncludeRuntimeCompatibility for the strict eight-database set. Its default four and optional six target lists are preserved. Restore runner accepts only the exact four/six/eight whitelists, not arbitrary database names. PowerShell parser, Node syntax, StackTools build/export and Git whitespace PASS. No application/Flutter code changed; business regression/E2E counts from earlier are not new tests here.

## Approval required to unblock

Review/approve two expansion files and, separately and explicitly, the Customer grant file. Before any local execution: verify fresh backup SHA/restore result, unchanged cluster/history/column definitions, apps still stopped, no writers, and zero Customer backfill candidates. Stop on drift/nonzero rows. Do not run migration scripts under the existing runtime-only approval.

Proposed order after that approval: Menu expansion -> verify -> Customer expansion (backfill INSERT 0) -> verify -> Customer grant -> verify allowed/forbidden effective permissions and unchanged old data. No financial keys, flags or historical Payment collections are part of this approval. Never run rollback automatically.

Once schema/ACL gates pass, **continue the already approved features-OFF runtime smoke**; no need to ask again for the same app startup scope. Reuse healthy existing infrastructure rather than restart/recreate it. Recheck candidate images/flags, compare all nine models, snapshot fingerprints, then replace/start apps one at a time and verify safe GETs. Stop at the first service failure. If a script is retried, preserve previous evidence and never blindly replay completed app/schema stages.

Evidence: CASHIER_DASHBOARD_RUNTIME_SMOKE_RESULT.json, RUNTIME_MODEL_SCHEMA_DIFFERENCES.json, RUNTIME_COMPATIBILITY_TEST_RESULT.json, RUNTIME_COMPATIBILITY_EIGHT_DB_RESTORE_RESULT.json. No local migration/grant/financial activation occurred during this smoke attempt.
