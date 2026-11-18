# Table map API sync — 2026-11-13

## Additive read-only API

`GET /api/v1/orders/staff/takeaway-summary?branchId={guid}`

Requires Staff policy, authenticated tenant and permitted branch. Missing/empty branch is rejected; cross-branch access is forbidden. No public endpoint is renamed or removed.

```json
{
  "branchId": "8493872e-bf01-4723-b6c2-385e70bee66e",
  "activeOrders": 36,
  "readyOrders": 1,
  "observedAtUtc": "2026-11-13T07:00:00Z",
  "readyOrdersKnown": true
}
```

Counts are SELECT aggregates over the whole branch, not a paginated list. Active means Takeaway, not delivered, with status Pending/Confirmed/Preparing/Ready. Ready counts Order.Status Ready. When any active order uses cup fulfillment, readyOrdersKnown is false: consumers must not treat the legacy count as authoritative cup readiness. No save, event publish, payment query, DDL or new ACL is required by this endpoint.

## Mobile

Shared Demo/API table widgets preserve visual design. Floor-plan and session APIs remain in use. Summary is optional: an older runtime returning 404 produces unknown counts, never fake zero. Main DB and running containers were not changed. The current pre-Cup Ordering hotfix cannot be replaced by full source without a separately approved schema/runtime rollout; this API is source-only in this delivery.

Per-table operational statuses remain a GAP. Occupancy cannot establish preparation, serving or payment completion; Mobile disables unsupported operational filters instead of guessing.

## Validation

4 new TestServer/InMemory tests: whole-branch counts above 30 and exclusion rules; tenant/branch and authentication; empty branch and validation; cup readiness unknown. Safe regression suite: 488 PASS, 0 FAIL, 0 SKIP. Opt-in PostgreSQL/Redis infrastructure classes were explicitly excluded, not counted as PASS. No fresh database-backed E2E claim.

Changed code: `TakeawaySummaryEndpoints.cs`, route registration in `OrderEndpoints.cs`, and `TakeawaySummaryTests.cs`. No migration, deployment or Payment/Bill changes. User subsequently authorized commit/push; retain branch `be/mobile-staff-operations-20261112` without merging main. Verify remote after push and report the result separately.
