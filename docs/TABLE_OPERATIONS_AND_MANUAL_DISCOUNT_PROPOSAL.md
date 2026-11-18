# Table Detail operations & manual discount proposal — 18/11/2026

Update: the manual-discount text below is the initial proposal, superseded by `SESSION_MANUAL_DISCOUNT_IMPLEMENTATION.md`. Current code reuses discount_amount, adds ONE audit table (no Order column), and enforces user-approved no stacking/no zero Bill/manual review after add or cancel. Operational deployment remains unapproved. Other missing contracts remain BLOCKED.

## Source verified

- Ordering `Features/Orders/OrderEndpoints.cs`: create, GET detail, service-type switch, cancel entire order, deliver, no-show, unfulfillable, manager exception. No submitted-item update/delete endpoint.
- `Order.CancelOrder`: WholeOrder guard, unpaid/not completed guard. Existing counter cancellation does not provide the new expected-version/operation-id contract and does not require Bar approval after brewing starts. Do not expose it as a safe per-item cancellation workflow.
- `Features/Qr/TableTransferEndpoints.cs`: transfer whole session to AVAILABLE table, rejects occupied target with `table.target_occupied`. This is not merge.
- `Features/SessionBilling/SessionBillingWriteGuard.cs`: rejects financial order/item changes after checkout freeze. Reuse this protection and TableSessionLock; never remove it to implement discounts.
- `Features/SessionBilling/SessionBillQuotes.cs`: sums existing Order discount/final amounts and freezes snapshot. No manual session-discount command.
- Payments `Features/SplitBilling/SplitPlanEndpoints.cs`: GET/PUT/history are feature gated. `PaymentAvailable=false`; plans are allocation drafts and never authorize separate group collection. Existing group save has version/fingerprint/idempotency.

## Available Mobile behavior

Table three-dot menu exposes transfer/history, draft split access, and a route to the authoritative Session Bill close checks. Unsupported merge/discount display a precise notice; they do not execute Demo mutations. Main submitted-item edit/cancel remains unavailable. Demo unchanged.

Split route first refreshes the current table session, verifies it has not changed, loads a scoped Backend Bill and then opens the existing draft screen. Backend flag/ACL remains authoritative. 503 means feature unavailable; no local draft is fabricated. No flag is enabled by this change.

## Manual discount — user-approved policy

Cashier enters percent OR whole VND amount and a reason; no automatic loyalty-tier rule. Only pre-payment changes. No PAID reopening and no Refund. This policy approval does not authorize applying schema changes to the operational database.

### Proposed ownership and additive schema (not applied)

Ordering owns the price calculation/snapshot. Keep Payments as collector of that quote, not a second pricing engine.

1. Add `orders.manual_discount_amount` whole-VND numeric, NOT NULL DEFAULT 0. Preserve existing `discount_amount` as the total discount so Web responses, quotes and existing reporting retain their meaning. The promotion component is total discount minus manual component; never overwrite a voucher with a manual discount.
2. Add append-only `ordering.session_adjustment_events`: operation_id PK, tenant_id, branch_id, session_id, revision, actor_id, idempotency_key, request_hash, expected_fingerprint, mode, input_value, reason, allocations_json, result_fingerprint, created_at_utc. Unique (tenant_id, actor_id, idempotency_key) and (session_id, revision). FK only to local Ordering session; no cross-service FK. JSON records immutable per-order before/after amounts and promoted/manual components. SELECT/INSERT only for audit rows; minimum column UPDATE on orders.
3. Require a separate reviewed EF migration, generated SQL, isolated PostgreSQL test and GRANT proposal before local deployment. Existing rows keep manual discount zero; totals/payment history must remain unchanged. No destructive rollback on data containing adjustments: disable mutations, preserve audit and reconcile first.

This is a proposal, not a generated or validated migration. Verify current EF model/history and constraints again before producing SQL.

### Proposed API (not implemented)

`GET /api/v1/orders/qr/sessions/{id}/adjustments`: scoped current discount and audit history.

`PUT /api/v1/orders/qr/sessions/{id}/manual-discount`, with Idempotency-Key:

```json
{
  "expectedFingerprint": "verified current quote",
  "mode": "PERCENT",
  "value": 10,
  "reason": "Khách hàng thân thiết"
}
```

`AMOUNT` is the alternative mode. Response: operationId, replay, sessionId, revision, subtotal, promotionDiscount, manualDiscount, totalDiscount, total, fingerprint, order allocations. Values are Backend-authoritative. Same key/different body -> 409; retry of a committed request returns the original result and does not add another discount.

Under the existing tenant/table/session transaction lock: reload session and orders; require OPEN, correct branch, Cashier/management policy, current actor/must-change-password checks, expected fingerprint, no checkout fence, no existing payment/reconciliation or unresolved transfer. Allocate the requested session discount across eligible order balances proportionally using whole VND and a deterministic remainder order. Per-order reduction sums exactly to the session discount; no negative line/order/bill amount. Do not discount cancelled items. Persist order totals and audit in one local transaction. Payments freezes the updated quote through its existing contract; never write Payments tables from Ordering.

A new order must invalidate the visible quote and require discount review; do not silently apply an old percentage to later orders. Explicit replace/remove with reason is audited, not an extra stacked discount. Exact stacking rules and 100%/zero-total settlement remain decisions to approve before implementation; do not invent a zero-value successful payment.

### Required tests

Cashier allowed/other role denied; cross-branch denied; amount/percent/reason validation; deterministic whole-VND allocation; promotion preservation; replace/remove; concurrent two Cashiers; same-key retry/lost response; different-body conflict; add-order race; Cash/QR freeze race; PAID/CLOSED denied; old orders with manual=0 unchanged; Quote/Freeze/PAID/receipt/reporting totals agree; Web response remains additive. Real PostgreSQL/Gateway tests isolated, never main.

## Other missing contracts

| Feature | Current limitation | Required before enabling |
|---|---|---|
| Edit/delete submitted item | No item command; Cup/Sticker identities and price snapshots must survive | Versioned item-change request, affected Cup reservation/Bar acknowledgement, immutable audit, Billing write guard and durable retry |
| Cancel entire order | Existing legacy API is broader than new staff rules; CupV1 rejects whole-order operation | Separate readiness/authorization contract, Kitchen guard, terminal cancellation synchronization, stale 409, idempotency and no Refund side effect |
| Merge occupied tables | Transfer explicitly rejects occupied target | Approve whether two sessions keep separate original Bills or merge financial ownership; preserve history/CupId/claim and unresolved payment safeguards; do not turn transfer into merge |
| Split payment | Existing API stores drafts only | Approve discount/tax allocation, group settlement, collection and root close rules; no separate order payments for dine-in |

## Safe next implementation order

1. Review/approve the discount schema and remaining stacking/zero-total rules; implement additive migration and tests in an isolated DB only.
2. Add mutation contracts for pre-brew/unclaimed items with Kitchen acknowledgement, then Mobile edit/cancel UI.
3. Approve occupied-table merge financial/session semantics and group-payment contract separately.
4. Full-stack tests, backup/preflight and separate activation approval. Current runtime flags and databases remain unchanged by this task.
