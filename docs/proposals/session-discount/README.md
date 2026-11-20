# Session manual discount — deployment proposal, NOT APPLIED

User approved Cashier percent/whole VND + mandatory reason, no promotion stacking, no zero-total Bill. Implemented in Ordering, SessionDiscount flag defaults OFF (missing configuration is false). SessionBilling must also be ON. Payment/Bill collection protocol is reused unchanged.

Both Enabled and GuardEnabled default OFF. Mutation endpoint requires both. Enable GuardEnabled only after schema/ACL verification; once adjustments exist, pause only Enabled and keep GuardEnabled/new image to retain the mandatory review after add/cancel.

## Schema decision

19 November 2026: the subsequent safe-order-cancellation migration intentionally advances the shared Ordering model snapshot. Its current checksum is refreshed in EF_SHA256SUMS.txt; the two manual-discount migration source files are unchanged. See `../order-cancellation/README.md`; neither feature is implicitly approved for operational migration.

After source review, no new `orders` column is necessary: existing `discount_amount` stores the total, while a new append-only `ordering.session_discount_events` records the exact previous/new per-order discounts. Unknown existing discounts or any promotion code block mutation; no promotion is overwritten. This supersedes the earlier column-plus-table idea in TABLE_OPERATIONS_AND_MANUAL_DISCOUNT_PROPOSAL.md.

One EF migration: `20261118143317_OrderingSessionManualDiscount`. It creates one 13-column audit table, a PK, same-service session FK with RESTRICT, input CHECK, FK index and unique tenant/session/revision index. No existing table/column/index is recreated or changed. Operation ID is derived from tenant + actor + idempotency key; request hash includes session, quote/revision and body. Same key/different body is 409. The table is used only through the new gated endpoints, so an OFF deployment does not query it.

## Execution order — requires separate deployment approval

SQL, checksum manifests and the listed EF source/snapshot files use UTF-8 without BOM and canonical LF via `.gitattributes`. Regenerate manifests after any reviewed change; native Windows CRLF hashes must not be mistaken for the reviewed bytes.

All SQL files require the separately verified cluster identifier via `PGOPTIONS=-c aicos.expected_cluster=<verified_identifier>` (or equivalent session setting in the approved DBA runner). Missing/wrong cluster fails closed. Do not infer the target from a matching database name or a Windows localhost connection. Operator/credentials must be established securely outside source/logs. No commands in this directory are executed automatically.

1. Stop mutations/clients, record immutable images/config/flags, verify correct cluster/database, take and restore-check a fresh backup. Feature remains OFF.
2. Check SHA256SUMS and review generated SQL against current EF history. Run `00_preflight.READ_ONLY.sql` using `psql -X -v ON_ERROR_STOP=1`. Inspect any existing target table; never assume same-name objects are equivalent.
3. Apply `01_expand.PROPOSAL.sql` to **aicos_ordering** only using the migration/DBA role. Transactional idempotent EF history guards; no operational row changes. If unknown partial schema exists, STOP. No auto rollback.
4. Apply `02_grants.PROPOSAL.sql` only after permission approval. Existing role only, no password or PUBLIC changes. Required existing SELECT on Ordering session/orders/items/options/checkouts must already be present.
5. Run `03_verify.READ_ONLY.sql`: 13 columns, PK/FK/CHECK/indexes as above, expected role column rights, audit SELECT/INSERT only. If role owns the table or inherits UPDATE/DELETE/TRUNCATE/superuser, **STOP activation**; propose separate privilege repair, do not revoke silently.
6. Deploy reviewed Ordering image with `MigrateOnStartup=false`, `SessionDiscount:Enabled=false`. Route already falls under Gateway `/api/v1/orders/*`; no new public service/DB ownership boundary.
7. Only after approval: enable SessionDiscount local; verify Identity auth/me, appropriate roles, branch and must-change-password checks. Test writes on isolated data first. Current task does not enable the flag or replace operational containers.

## Rollback

Prefer turn Enabled OFF and preserve data/audit; **keep GuardEnabled ON and the new image**. Old Ordering does not enforce manual-discount review; do not fall back to it when audit/review obligations exist.

`90_rollback_EMPTY_ONLY.PROPOSAL.sql` downgrades **only** the new migration. It locks the audit table and refuses if ANY audit row exists, then drops only this empty new table and its migration-history entry. Never run after manual discounts have been applied, even if they were later removed. No automatic destructive rollback. With data: keep audit, restore-drill a backup into a separate DB, and obtain a reviewed financial reconciliation/forward-fix plan. Do not restore the live DB over newer financial activity.

## Verification evidence and limits

`scripts/Test-SessionBillingPostgres.ps1 -Run` creates a labeled RAM-only PostgreSQL scratch container, private network, random loopback port, fresh credentials and explicit cluster guard. It migrates only aicos_billflow_test. No main/Windows credentials or volumes. The runner now includes SessionDiscountPostgresTests: concurrent mutation/replay, Cash freeze race, nonempty-audit rollback rejection and append-only ACL matrix using a new NOLOGIN scratch role.

HTTP TestServer tests use real PostgreSQL where named; Identity/JWT/Menu/Kitchen/Payment transports remain explicit test doubles. These are NOT full Gateway/real Identity/phone E2E. No real Cash taken. Exact results/cluster IDs are in SESSION_MANUAL_DISCOUNT_IMPLEMENTATION.md. Production owner/inherited permissions, deployment SQL on the operational target and device E2E remain NOT RUN.
