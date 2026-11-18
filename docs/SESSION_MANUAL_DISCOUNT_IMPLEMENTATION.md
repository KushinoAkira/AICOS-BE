# Session manual discount — 18/11/2026

Implemented in reviewed worktree `be/cashier-dashboard-main-sync`. **Not deployed or enabled on the operational stack. No operational SQL, GRANT, seed, payment request, commit or push.**

## Approved rules

- Cashier/management enters whole VND or a percentage (up to two decimals) with a 5–250 character reason. No automatic loyalty-tier rule, promotion stacking or zero-total Bill/100% discount.
- New value replaces the previous manual discount. Explicit 0 removes it with a new audit receipt, not deletion of history.
- Unpaid staff Dine-in only. PayFirst, partial-paid, refunded, promotion, unknown discount and pending transfer states fail closed. Frozen/paid Bills cannot change.
- After add/cancel, no automatic percentage update: Cashier must check and save/remove discount again. Quote fingerprint mismatch sets `reviewRequired` and Ordering rejects checkout freeze until review is recorded.
- No Takeaway manual discount, Refund, occupied-table merge, group payment or Item Transfer V2 in this task. Cup/claim/sticker/product/quantity/price snapshots remain unchanged.

## Additive API

`GET /api/v1/orders/qr/sessions/{sessionId}/manual-discount`

Returns sessionId, branchId, tableCode, fingerprint, revision, subtotal, discount, total, canEdit/reasonCode, last operationId/mode/value and reviewRequired. Read-only, scoped to authenticated tenant/branch.

`PUT /api/v1/orders/qr/sessions/{sessionId}/manual-discount`, with `Idempotency-Key` (8–80 characters):

```json
{
  "expectedFingerprint": "64-character fingerprint from GET",
  "expectedRevision": 0,
  "mode": "PERCENT",
  "value": 10.5,
  "reason": "Khách hàng thân thiết"
}
```

Alternative mode AMOUNT uses whole VND. Response is a committed operation receipt; replay=true proves a past commit, not current editability. Mobile always GETs current state after any successful command/replay.

401/403 auth/current identity; 400 invalid input; 409 stale fingerprint/revision, lock/payment/promotion/reconciliation/transfer/zero-total/key conflict/review required; 503 mutation/guard/Billing disabled or Identity dependency unavailable. Existing Gateway wildcard `/api/v1/orders/{**catch-all}` covers the new route; no route rename or removal.

## Implementation and financial safety

- Same PostgreSQL tenant/table transaction lock as Add Order, Table Transfer and Freeze; session/table reloaded after lock. Identity HTTP happens before the transaction.
- Fresh `/identity/auth/me` validates user/tenant/branch, active/unlocked account, changed password and intersection of live/JWT counter roles, including a secondary Cashier role. Fail closed on dependency errors.
- Deterministic operation GUID from tenant/actor/key; request hash includes session and normalized body. Persisted receipt handles lost ACK/restart; reused key with different content returns 409. Per-operation advisory lock and unique session revision handle concurrent requests.
- Integer-only allocation with `BigInteger` standard library, percentage truncated to whole VND and deterministic largest remainder by OrderId. Reductions sum exactly to requested amount, each order stays non-negative, combined Bill stays positive. Includes a large-input overflow regression.
- Reuses `orders.discount_amount`. No new Order column or promotion-code overwrite. Existing discounts must match the latest receipt; unexplained data is blocked.
- Order updates plus receipt insert in ONE Ordering transaction. No direct Payments/Kitchen table access, distributed transaction or new financial ledger.
- Ordering Freeze blocks unreviewed discount after quote changes. Payments `BillOrderingClient` only propagates allowlisted `discount.review_required`; collection/ledger/provider logic is unchanged. No raw upstream body is exposed.
- Shared `SessionBillQuote` adds optional `manualDiscountReviewRequired`, omitted when false to preserve old JSON shape. When true it survives Ordering → Payments → Mobile so Cash form is blocked BEFORE receipt confirmation; the Bill panel links directly to discount review. Requires reviewed Ordering/Payments images together before activation. Actual Web FE is not available for client verification.

## Schema and deployment gate

One new 13-column table `ordering.session_discount_events`, PK, local session FK/RESTRICT, input CHECK, FK index and unique tenant/session/revision index. One new migration: `20261118143317_OrderingSessionManualDiscount`. Old migrations remain untouched; EF snapshot updated. The previous column-plus-table proposal is superseded.

`docs/proposals/session-discount/` contains preflight READ ONLY, generated idempotent expand, GRANT proposal, readonly verify, guarded EMPTY-ONLY rollback and SQL checksums. Source migration Down also locks and refuses rollback when ANY audit exists. Never erase audit/recalculate paid funds as rollback.

Defaults in Ordering appsettings:

```json
"SessionDiscount": { "Enabled": false, "GuardEnabled": false }
```

No runtime flag changed. Both flags are required by GET/PUT; SessionBilling must also be enabled. GuardEnabled protects Freeze independently of Enabled. Once adjustments exist, pause mutations with Enabled=false while **keeping GuardEnabled=true and the new image**. An old image ignores review; do not roll back to it while review/discount obligations exist.

Approval order: fresh backup + restore check → cluster/schema/history preflight → audit-only migration → separately approved role grants → readonly schema/ACL verify → reviewed image/flags OFF → enable guard → separately enable mutation API. Current task stops before all operational steps.

## Files

- Ordering: Order.cs guarded setter; OrderingDbContext.cs; Program.cs; appsettings.json; SessionBillingEndpoints.cs review guard; new SessionDiscountEvent/Calculation/Endpoints, DiscountActorVerifier; migration/Designer/snapshot.
- Payments: BillOrderingClient.cs allowlisted error propagation only.
- Tests: SessionBillHarness.cs, SessionDiscountTests.cs, SessionDiscountPostgresTests.cs, DiscountActorTests.cs, DiscountReviewClientTests.cs; scratch runner `scripts/Test-SessionBillingPostgres.ps1` adds a discount stage.
- Mobile: new SessionDiscount model/repository/screen/form; Main table action route; shared safe error mappings. Existing Bill/receipt uses server totals.

## Evidence and limitations

Backend final regression: **620 PASS / 57 SKIP / 0 FAIL**. Tests requiring PostgreSQL/Redis are honestly skipped in this run. Focused HTTP/actor/typed-client tests use EF InMemory, test JWT and explicit service doubles; not full-stack E2E.

Final focused compatibility run after false-flag serialization annotation: **31 PASS / 0 SKIP / 0 FAIL**, overlaps the regression count. Flutter current source: **413 PASS / 9 SKIP / 0 FAIL**, targeted pre-form guard **36 PASS**, analyzer **0 issues**; no device deployment. Known unrelated existing test-build warnings CS0628 (WorkforceReviewTests) and CS8620 (OrderTests) were not changed.

Latest PostgreSQL scratch run with the pre-form review contract: cluster **7709530390205382691**, random loopback port **54201**, aicos_billflow_test, private network/tmpfs; **13 PASS** (7 existing Bill + 6 discount). Covers concurrency, replay, Cash race, mandatory review, rollback refusal and ACL. Scratch container/network removed after verification. The final compatibility annotation only omits the false review flag; its focused tests are recorded separately. No operational volumes, roles, credentials or DB.

Offline `dotnet ef migrations has-pending-model-changes --no-build`: **no model changes since migration**. SQL guards additionally require database aicos_ordering and explicitly supplied expected cluster setting; operational execution of those wrapper/GRANT files is NOT RUN. EF expand/repeat/guarded rollback and ACL behavior were tested only in scratch.

ACL probe creates a fresh NOLOGIN scratch role: audit SELECT/INSERT, no audit UPDATE/DELETE, column UPDATE on discount, no Payment SELECT. It does NOT prove current operational role ownership/inheritance or actual service LOGIN/API behavior. Intentional stale/rollback tests may log expected 409/SQL failures.

Remaining before activation: operational schema/GRANT approval, fresh backup restore verification, actual role ownership/inheritance checks, real Identity/Gateway + mixed SERVER/CASHIER + Flutter device E2E. No production-readiness claim. Runtime still has the previous image/API; Mobile shows unavailable until reviewed deployment. Other item-edit/cancel, occupied-merge and group-collection contracts remain blocked.
