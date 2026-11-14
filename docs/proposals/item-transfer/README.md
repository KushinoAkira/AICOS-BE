# Item Transfer — SQL design prototype

These are **test-only SQL proposals**, not finalized EF migrations and not approved operating migration scripts. They deliberately refuse aicos_db, Windows databases and every database except the three exact scratch targets below. Source models and finalized migrations are unchanged.

## Safety / execution

Run `node scripts/tests/item-transfer-design.mjs` from AICOS-BE only with Docker Engine running. It creates a new internal Docker network, PostgreSQL 16 and Redis 7 with tmpfs, no published host ports or existing volumes. Password is freshly generated in memory and passed through environment; no source credentials, primary .env or Gate C credentials are read.

Every SQL call verifies the exact container ID, purpose label, only-network membership, tmpfs/no-volume/no-host-port properties and PostgreSQL system identifier. The C# fixture independently verifies Host=postgres, exact database and system identifier before any write. No service stack is started. The only dotnet container is a short-lived test tool using a local runtime image.

Targets:

- aicos_item_transfer_design_ordering / schema ordering
- aicos_item_transfer_design_kitchen / schema kitchen
- aicos_item_transfer_design_payment / schema payment

Each SQL file also requires `aicos.design_approved=true` and `aicos.expected_cluster` equal to pg_control_system(). Do not weaken those guards or rename the scripts as operating migrations.

## DDL / exact order in isolated runner

1. Verify SHA256SUMS.txt. Export current EF migration baselines offline through `ItemTransferDesignProbe` and apply to the three guarded scratch databases. No MigrateOnStartup, application service, seed from production or migration history edit.
2. `01_ordering.sql`: item_transfer_operations, unit_session_allocations, unit_settlement_receipts. One financial owner per Cup; own Order/OrderItem/Session FKs; explicit zero/negative amount and invalid version checks; transfer retry index and session allocation index. Unit assignment also holds the proposed unit serving projection, avoiding an extra table.
3. `02_kitchen.sql`: cup_transfer_receipts, cup_delivery_routes. Own CupTask FK and own receipt FKs; no foreign database/session FK. Does not alter CupTask, admission source, ticket, sticker or claim history.
4. `03_payment.sql`: session_bill_units, own Bill FK, unique tenant/CupId and tenant/sourceOrderItem/unitIndex. No UPDATE rights on this frozen manifest.
5. Run repeated expand expecting a duplicate-object failure inside the atomic transaction; it does not silently accept an incompatible preexisting object. Rollback empty proposals and reapply to verify unused rollback before fixtures.
6. Create scratch-only `design_ordering_svc`, `design_kitchen_svc`, `design_payment_svc` as NOLOGIN; `04_acl.PROPOSAL.sql` grants only SELECT/INSERT and required workflow UPDATE columns. No passwords, PUBLIC REVOKE, DELETE, TRUNCATE, sequence, ownership or elevated role options.
7. Populate synthetic fixtures from actual Entity/EF/CupAdmissionWriter, then execute the transaction sketches in `item-transfer-design-scenarios.mjs`. Financial snapshots and unit serving projections in this test are SQL fixtures, not real Cash/QR/Serving API results.
8. `90_rollback.EMPTY_ONLY.sql` must refuse after allocations, receipts or frozen unit manifests exist. Snapshot preexisting containers and remove only this runner's own scratch containers/network.

Only rollback has DROP statements. They target exactly the six new proposal tables, never use CASCADE, run transactionally after exclusive-lock empty checks and require the same exact database/cluster guard. There is no DELETE/TRUNCATE/backfill or DROP of baseline business tables in the proposal. Never apply a partial destructive rollback after a committed transfer. Keep audit, disable new commands and reconcile/forward-fix instead.

## What this proves / does not prove

PASS means SQL constraints, isolation, transaction and replay design, amount conservation, exact-unit ownership, own FK/ACL, actual CupAdmissionWriter replay, empty rollback/reapply and nonempty rollback refusal were exercised against real PostgreSQL. Redis restart demonstrates the SQL operation survives loss of Redis; it is not service crash recovery proof.

This does not prove JWT authorization, M2M signing, domain handlers, workers, process-kill, HTTP lost ACK, provider payment or Mobile/Web E2E. Those APIs are not implemented in this task. The design runner is intentionally outside production code; do not expose its SQL sketches as endpoints.

Current incompatibilities and V2 contract requirements: `docs/ITEM_TRANSFER_TECHNICAL_VALIDATION.md`. Results: `docs/ITEM_TRANSFER_DESIGN_TEST_EVIDENCE.json`.

## Remaining schema/rollout work

After the protocol review, create separate additive EF migrations per owning service for the approved model; compare generated SQL and snapshots, inspect actual history and preflight the approved target. Revalidate per-unit immutable data and historical backfill evidence; block unknown historical units, never guess which cup was served. Prepare operating role grants using the deployed service-role matrix, not the scratch NOLOGIN role names here.

No operating SQL, service role/secret changes, feature activation, deployment, commit or push is authorized by this README.
