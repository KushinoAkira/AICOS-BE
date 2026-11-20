# Safe order cancellation — proposal, NOT deployed

User approved automatic whole-order cancellation only before Bar acceptance/preparation and without claimed cups. This implementation restricts the first rollout further to **unpaid Staff Dine-in / WholeOrder**, no voucher/promotion and no checkout fence. CupV1, per-item cancellation, after-preparation overrides and refunds remain blocked.

Two additive EF migrations, one table per owning service, no cross-service FK:

Integration candidate 19/11/2026 additionally contains upstream main Apriori. Its `20261113093202_OrderBasketsAssociationRules` baseline must be separately reviewed/present before these Ordering proposals; the preflight refuses a missing baseline. Snapshot and the unapplied cancellation TargetModel include both models. See `../../PAYMENT_MAIN_INTEGRATION_REVIEW.md`; no extra baseline is applied implicitly on runtime.

- Ordering `20261118161648_OrderingSafeOrderCancellation`: `ordering.order_cancellation_operations` (22 columns), local Order/Session FKs, unique active tenant/order, pending worker index. Depends on the separate, not-yet-approved local manual-discount migration `20261118143317_OrderingSessionManualDiscount` in migration history. Do not apply either implicitly.
- Kitchen `20261118161652_KitchenSafeOrderCancellation`: `kitchen.order_cancellation_receipts` (10 columns), nullable local Ticket FK, unique HELD tenant/order. Baseline `20261114164849_TableTransferRoutingVersion`.

## Execution gates

No SQL in this directory is authorized on operational databases. Generated `01_ordering_expand.PROPOSAL.sql` and `02_kitchen_expand.PROPOSAL.sql` are EF **idempotent history-based** scripts, not same-name-object equivalence checks. Review checksums, backups, exact database/cluster and prior migration history first. An existing new table without its migration-history entry is a STOP condition. A completed-history entry with missing/different columns/indexes/constraints is also STOP, not an invitation to recreate.

1. Stop affected business writers, back up both service databases consistently and restore-drill separately. Keep all cancellation flags OFF.
2. Supply verified `aicos.expected_cluster` to `00_preflight.READ_ONLY.sql`, separately on `aicos_ordering` and `aicos_kitchen`. This is the **current main architecture**, not the retired shared `aicos_db.public` architecture.
3. After separate user approval, apply Ordering and Kitchen expand scripts only on their named service database. Each script includes its own exact database/cluster/baseline guard; `ON_ERROR_STOP=1` is mandatory. No old migration is edited.
4. Review/apply only the appropriate `03_ordering_grants.PROPOSAL.sql` or `04_kitchen_grants.PROPOSAL.sql`. No role creation, password, ownership, PUBLIC REVOKE, DELETE or TRUNCATE. UUID keys require no sequence rights. Existing baseline service privileges must already be correct.
5. Verify table columns/PK/local FK/partial unique/CHECK/history and actual service-role SELECT/INSERT/allowed UPDATE. Never let a service own its audit table when enforcing ACL immutability.
6. Deploy matching versions with `MigrateOnStartup=false`, separate >=32-character machine key outside Git/logs; first enable **GuardEnabled on both services**, verify probes and legacy guards, then enable Ordering capture/dispatcher. `BaristaWorkflow:Enabled=true` and SessionBilling must be active; legacy Bar flow alone cannot prove “not yet accepted”. Do not enable this task on runtime automatically.

## Stop / rollback

Pause **new capture** (`OrderCancellation:Enabled=false`), retain both guards and dispatcher until all active operations resolve. Never release HELD because of timeout. If an invalid ACK/snapshot prevents completion, preserve audit/receipt and reconcile manually; no automatic rollback of a cancelled financial order.

Both EF Down scripts take ACCESS EXCLUSIVE lock and refuse a nonempty new table. Schema rollback is only for a verified **empty**, unused expansion and must be separately approved; never delete receipts/audit to force rollback. Once used, retain schema and matching guard-capable services. Restoring one service database alone can resurrect an order/ticket; use a coordinated recovery plan and reconcile source/receiver histories before resuming.

`SHA256SUMS.txt` covers the reviewed SQL. `EF_SHA256SUMS.txt` covers the two new migrations, Designers and current model snapshots using repository-relative paths, canonical UTF-8/LF. The session-discount manifest's current snapshot checksum is refreshed for this intentional model advance; its two migration source files remain byte-identical to commit `6b8aff7`.

## Remaining decisions

- All orders cancelled => current close protocol still requires PAID proof. Do not fabricate zero-value payment or auto-close; a no-charge-session closing policy needs separate approval.
- Voucher release, Cup cancellation/custody and Manager override need durable, separately reviewed contracts.
- Internal HTTP is the cancellation truth. Legacy Redis notification remains best-effort compatibility; no claim of durable voucher/customer/reporting cancellation delivery.
- Schema expansion/ACL, real Gateway + physical Mobile E2E and a controlled activation gate are required before operational use.
