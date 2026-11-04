# Menu options expansion and the next cashier flow

Date: 2026-11-04 (Asia/Saigon).

## Verified cause

The local menu fixture supplied only three toppings on six tea products, with
MaxSelections=2. Coffee/other drink/combo fixtures had no topping group. Mobile
correctly enforced that server limit; removing only its guard would make orders
invalid. Sugar/ice fixtures were also less complete than the original Demo UI.

## Implemented without schema or endpoint changes

- Shared definitions: scripts/fixtures/LocalMenuOptions.ps1. Tea has 8 toppings,
  coffee 5, fruit 3, other configurable drinks and fixed combos 5. Product options
  are scoped to each beverage; bottled water/plain hot coffee/cakes do not gain
  inappropriate sugar/ice/topping choices.
- Multiple Topping groups use MaxSelections=null: any distinct available types
  may be selected, not a fixed cap of two. Each option remains one portion; the
  current Orders OptionIds contract has no per-option quantity field.
- Added 70% and 30% sugar and separate ice; retained existing labels/defaults/prices.
- Updated initializer to reuse definitions; Expand-LocalStaffMenuOptions.ps1 is a
  guarded preview by default. Apply snapshots old metadata outside Git, checks
  local tenant/project, preserves existing choices/prices, updates via existing PUT,
  then verifies all three published branch menus. Repeat preview: zero changes.
- Catalog.cs reconciles groups/options by case-insensitive name when replacing
  a product's options. Unchanged groups/options keep IDs; dependency IDs remap
  to retained IDs. Removed names still follow existing PUT replacement semantics.
  No request/response contract or migration changes.
- Rebuilt/deployed only Menu, preserving database/JWT configuration and migration=false.

## Real database/API evidence

Before: 28 products, 58 groups, 156 options. After: 28 products, 69 groups, 290
options. Seventeen drinks/combos have topping groups in every branch. No product,
branch publication, base price or existing option ID was removed or replaced.

A real local Takeaway request selected four toppings on milk tea (plus three
standard default options). Orders accepted all seven IDs and computed 67,000 VND.
Order ORD-20261104-EAC688 / 1d58fe4b-a735-4fec-9717-bf02f4a477a7 was then cancelled
through the normal API; remained Unpaid. No payment or table session was created.
Request key: local-topping-e2e-a911166962134599a9d08793a760a524.

## Mobile and tests

Mobile keeps original size cards/accordions/topping search/cart footer and Demo
Mode. Group descriptions/counts now reflect available server choices. Selection
controller rejects unknown/dependent/over-limit additions and preserves a required
single selection. Edit/remove topping recalculates price; UI tested at 360/393/430.

Backend Menu selection tests: 42 PASS, 0 skipped. New MenuExpansionTests verifies
PUT retains IDs and dependency links across repeated expansion and removes the
artificial two-choice limit. These tests use EF InMemory/fake clients; the live
PostgreSQL/Gateway test above is separate, not mocked. Injected transient faults
and concurrent menu edits were not tested.

## Next flow and limits

Mobile now exposes explicit staff session close when the displayed due is zero,
with confirmation, role check, fresh branch/session/bill checks and authoritative
POST /orders/qr/sessions/{id}/close. Backend 409 remains authoritative if the bill
changed. This is neither receipt printing, cash collection nor cleaning a table.

Cash collection is still not enabled in Mobile: RecordCashAsync currently reads
existing payments and writes a new one without a common per-order concurrency
boundary; online confirmation has its own mutation path. It is not safe to claim
race-free collection from the existing in-memory tests. Next work is a shared
per-order strategy for cash/online confirmation, duplicate/ambiguous retry handling,
and parallel PostgreSQL tests on a separately approved isolated test environment.
Real QR additionally requires provider/evidence verification, never simulated success.
No payment schema change is proposed or applied in this task.

Mobile log records final Flutter results. No connected Android device was available
(adb devices empty); no physical-device install/E2E is claimed. No commits/push.
## Final runtime observation

Payments remains at zero rows. Two other Pending orders and two Open sessions exist outside the two marked E2E order IDs. They were not created by this task's verification scripts and were left untouched. Both marked E2E orders remain Cancelled; no bulk cleanup was performed.
