# Runtime compatibility execution — 2026-11-16

User explicitly approved all three proposal files after preflight PASS. Target: existing Docker PostgreSQL cluster 7703616372368646182, databases aicos_menu/menu and aicos_customer/customer. Windows PostgreSQL/aicos_db was outside scope and not connected.

## Preflight

- Fresh private full backup of eight databases; all dump sizes/SHA256 verified, isolated restore **11/11 PASS**. Private dumps, manifests and credentials stay outside Git with current-user/SYSTEM ACL.
- **22 SQL checksums PASS**, including the five runtime compatibility proposals; existing EF migrations unchanged.
- All 11 application containers stopped, no client writers, exact predecessor histories matched, Customer note-derived backfill candidates **0**.
- Private schema/ACL/owners/constraints/indexes/data fingerprint and role/container snapshots saved before execution.

## Executed stages

| File | Result | Verification |
| --- | --- | --- |
| 01_menu_expand.PROPOSAL.sql | COMMIT / PASS | Existing ProductPictureUpload recorded; image_url varchar(1000) → varchar(400000). 6 tables/49 columns unchanged in count. |
| 02_customer_expand.PROPOSAL.sql | COMMIT / PASS | Existing HuntVouchers recorded. 8 → 9 tables, 91 → 106 columns including EF history. member_orders backfill INSERT **0**; all old values unchanged. |
| 10_customer_grants.PROPOSAL.sql | COMMIT / PASS | SELECT/INSERT member_orders; UPDATE only is_cancelled/updated_at_utc/updated_by. Effective DELETE/TRUNCATE false; actual SET LOCAL ROLE SELECT returned zero rows. |

Each script ran once in its own psql connection, using ON_ERROR_STOP=1, pinned expected database/schema/cluster and the action-specific approval setting required by the reviewed SQL. No Down script, REVOKE, feature activation or financial reconciliation executed. Catalog verification of INSERT/UPDATE is not a business write test; no fixture or customer was created locally.

## Verification stop and diagnosis

After GRANT COMMIT, the verifier stopped because it compared the raw schema ACL to the previous NULL. The schema owner is customer_svc; GRANT USAGE to that owner materialized the exact PostgreSQL owner-default ACL. Read-only diagnosis verified nspacl = acldefault('n', nspowner), unchanged owner, unchanged effective privileges, and exactly the approved new-table permissions. This was a verifier false positive, not a failed SQL transaction. No SQL was replayed and no automatic rollback occurred.

Full read-only verification then passed: original columns/owners/table ACLs/column ACLs and values preserved; existing constraints/indexes preserved, new constraints validated; exact EF target columns/history match; seven other service schemas unchanged, AI table count unchanged, role flags and all containers unchanged during the three-stage operation. Machine evidence preserves the initial stop and subsequent diagnosis.

## Follow-on gate

The existing runtime features-OFF approval is separate and remains in effect. Runtime smoke resumes only after this three-stage PASS. See CASHIER_DASHBOARD_RUNTIME_SMOKE_RESULT.json and CASHIER_DASHBOARD_RUNTIME_SMOKE_EXECUTION.md for actual application outcomes; this report alone does not prove runtime or Dashboard business PASS.

Backup recovery remains available, but destructive Down is not authorized. After real writes, retain expanded schema and repair forward. Customer's existing reviewed member_orders FK cascades from customers deletion; direct member_orders DELETE is not granted. No Payment/Bill data was changed.
