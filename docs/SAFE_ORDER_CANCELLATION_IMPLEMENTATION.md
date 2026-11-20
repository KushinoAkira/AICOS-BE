# Safe order cancellation — 19 November 2026

Status: implemented source, feature OFF; deployment/schema approval and physical full-stack E2E pending. User approved safe whole-order cancellation before Bar acceptance/preparation and without claimed cups. The first scope is unpaid Dine-in/WholeOrder, open verified session, no promotion/voucher, no checkout fence and no unresolved transfer.

## Protocol and consistency

Ordering stores operation, actor/reason, request hash, immutable canonical before snapshot and due time. `QUEUED → RESERVED → APPLIED → COMPLETED`; only the final state proves both services committed. Kitchen persists `HELD → CANCELLED`. Repeated request keys return the same operation; changed payload returns 409. Unique active tenant/order and partial HELD tenant/order indexes prevent competing operations.

1. Ordering captures QUEUED under the existing per-order lock followed by table lock. Financial/session mutations and checkout freeze refuse an active operation.
2. Internal machine-authenticated reserve locks Kitchen's existing tenant/order key. It refuses any acceptance, print job, brewing/remake/history, paid/PayFirst order, Cup task/mode or Recovery Hold. Modern Barista workflow must be enabled. Missing ticket is WAITING, never silently cancelled.
3. HELD receipt commits before ACK. The shared `KitchenTicketWrites` guard prevents acceptance/start/serving/print/admission/legacy mutation while HELD. No transaction is held over HTTP.
4. Ordering rechecks canonical snapshot, session/table/branch and checkout under its locks; Order cancellation + APPLIED snapshot persist atomically.
5. Kernel commit cancels the original ticket and completes its receipt atomically. Only a matching CANCELLED ACK lets Ordering report COMPLETED.
6. A proven stale source before apply creates ABORTING; explicit abort produces ABORTED, including a tombstone when abort arrives before reserve. Late reserve cannot revive it. Timeout/network loss **never** releases a HELD receipt.
7. Worker/new request retry reads durable stages, using the same operation. Concurrent dispatchers recheck stage under SQL locks. Wrong ACK or unresolved post-apply state stays pending for reconciliation; there is no automatic financial undo.

Redis cancellation publication after completion is a compatibility hint, not the cancellation durability mechanism. Voucher cancellation is excluded because its release/recovery contract is not durable here. Admission recovery must be active/verified for a missing-ticket request eventually to progress; otherwise it safely remains pending.

## API / security

- GET `/api/v1/orders/{orderId}/cancellation`: order/session/branch, canonical fingerprint, eligibility/reason and latest operation (active first).
- POST same path: `{expectedSessionId, expectedFingerprint, reason}` with `Idempotency-Key` (8–80 chars), reason 5–250. 202 pending, 200 terminal; inspect state, not only HTTP code.
- POST `.../cancellation/{operationId}/retry`: scoped recovery of an existing operation.
- Internal POST `/internal/order-cancellations/reserve|commit|abort`, separate `X-Aicos-Order-Cancellation-Key` (32–256 chars). No new public route to bypass ticket guards.
- Reuses CounterRoles, tenant/branch access and the existing live `IDiscountActorVerifier` (/auth/me) for matching active account, roles/password/lockout. The verifier name is reused, not a second authorization implementation.
- Legacy staff Dine-in `/cancel` returns `order.cancel_protocol_required` with Guard ON, including mixed Customer roles. Other existing financial/Cup guards remain. Default OFF keeps existing Web contracts; enabling Guard intentionally requires Web callers to handle 409/use the new workflow.
- Additive optional `orderCancellationPending` in Bill quote and `cancellationPending` in table rounds/Barista responses. Financial snapshots/ledger calculation are unchanged. Payments only preserves this specific Ordering conflict as 409; it does not implement new payment logic.

## Files

- Contracts `Orders/OrderCancellation.cs`; Security `OrderCancellationMachineAuth.cs`.
- Ordering `Features/OrderCancellation/`: endpoints, rules/canonical snapshots, operation mapping, HTTP client, dispatcher/worker. Program/default flags/DbContext wired; shared SessionBillingWriteGuard, quote/freeze/close/discount and legacy cancel/table-round responses extended.
- Kitchen `Features/OrderCancellation/`: internal endpoint/receipt mapping; shared KitchenTicketWrites guard; Program/default flags/DbContext and Barista metadata extended.
- Two additive migrations + Designers/current snapshots; exact SQL/ACL/checksums/runbook in `docs/proposals/order-cancellation/`.
- Tests `SafeOrderCancellationTests`, `SafeOrderCancellationPostgresTests`, `SafeCancellationMigrationPostgresTests`; existing guarded SessionBillHarness/runner extended. Test directories, JWT, live Identity verification and transport errors are explicit doubles. No operational credential reuse.
- Mobile changes are only in AICOS-Mobile; see its `docs/SAFE_ORDER_CANCELLATION.md`.

## Flags / activation / rollback

Ordering `OrderCancellation:Enabled/GuardEnabled/DispatcherEnabled=false`; Kitchen `GuardEnabled=false`. No live activation or MigrateOnStartup changes performed. Operations require SessionBilling, Barista workflow, two approved schema expansions, least-privilege ACL and a separate machine key outside Git/logs. Guard ON must precede capture; keep both guards ON when pausing new requests. Workers may recover existing authorized operations while capture is paused.

EF migrations: Ordering `20261118161648_OrderingSafeOrderCancellation` (22-column operation table), Kitchen `20261118161652_KitchenSafeOrderCancellation` (10-column receipt table). Local FKs only; no old columns/indexes/migrations edited. Ordering history requires the separate manual-discount migration, still not approved on runtime by this task. Proposal SQL is history-idempotent and refuses wrong database/cluster or table/history mismatch. Read-only preflight prints indexes/constraints for review; matching names/count alone do not prove type/constraint equivalence.

Down locks each new table and refuses any row. Empty-only rollback is tested on scratch; after use retain audit/schema/guards and reconcile instead of erasing receipts. Coordinated backup/restore/activation of affected services requires separate approval.

## Remaining blockers

- No-charge session closing after all orders cancelled: existing paid-proof close path cannot be bypassed with fabricated zero payment. Requires a separate approved policy.
- Per-item/Cup cancellation, voucher release, Manager/Bar override after acceptance, occupied-session merge and Split Bill collection are not implemented by this task.
- No operational migration/GRANT, deployment, feature activation, commit or push. New functions will remain unavailable in the currently running old Backend until the reviewed deployment gate.
- No physical phone/Gateway/Web FE end-to-end verification, real Redis notification recovery, or OS process-kill test for this task. New dispatcher instances + deliberate lost reserve/commit ACK are narrower recovery evidence.

## Verification

Final PostgreSQL run: **20 PASS, 0 SKIP** (Bill 6, projection 1, discount 6, cancellation 6, exact SQL/preflight/ACL/idempotent expansion/empty rollback 1). Container `aicos-billflow-postgres-88b64e3677`, cluster `7709828010957238313`, random loopback port 59881, private network/tmpfs. The SQL proposal test creates `aicos_ordering`/`aicos_kitchen` only inside this scratch cluster, after the cluster guard, then tests the exact proposal files twice and column-limited ACL. It does not connect to same-named operational databases. Runner removed only its matching container/network afterward.

Backend full solution: **900 PASS, 64 SKIP** (BuildingBlocks 15, Identity 206, Services Integration 629/64 SKIP, Smoke InMemory 50). The last heartbeat guard refinement was followed by **9 focused PASS**; these overlap, not extra E2E cases. CS0628/CS8620 remain pre-existing test warnings. EF Ordering/Kitchen `has-pending-model-changes` both report no change. No SQL table/column/index is added by startup.

Flutter full correct Demo-mode command: **424 PASS, 9 SKIP**, analyzer 0 issues, responsive 360/393/430px. Initial plain `flutter test` ran default Main API mode and failed 22 Demo-login tests; corrected invocation used the existing documented `--dart-define=AICOS_MAIN_API=false`, without changing product defaults. Live-stack tests remain skipped, not passed. No Redis/Gateway/physical-device or OS process-kill E2E was executed here.
