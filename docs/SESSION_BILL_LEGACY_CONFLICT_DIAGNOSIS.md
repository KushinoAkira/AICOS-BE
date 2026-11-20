# Session Bill legacy-payment conflict — 19/11/2026

## Verified cause

Local logs correlate repeated `GET /api/v1/payments/bills` HTTP 409 with Ordering's `billing-quote` for session `56c92d69-9a12-4a8a-811f-eeeeb7593fcd`. Ordering reports **billing.legacy_payment**, not a quote fingerprint mismatch. Payments' runtime `HttpBillOrderingClient.Read` replaces every upstream 409 with **billing.stale**, so Mobile incorrectly tells the cashier that the amount changed and to reload before confirming.

READ ONLY catalog-verified queries on local Docker cluster `7703616372368646182` found:

- Table S01, session opened 04/11/2026, still OPEN.
- Three Orders: `ORD-20261104-12812A` Ready/Unpaid; `ORD-20261104-E5137B` Completed/Paid; `ORD-20261113-3920C5` Ready/Unpaid.
- The paid Order references Payment `3d40949e-51eb-4654-8137-e29a0b32fe43`: **208,000 VND Cash, Succeeded**, confirmed 04/11/2026 16:38:21 UTC, refunded_amount=0.
- No aggregate `payment.session_bills` row for this session.

This is an old mixed-payment session. Refresh cannot resolve it. Simply excluding the paid Order or marking a new aggregate Bill PAID would change financial meaning and risks collecting twice. No such repair was performed. Logs separately show a new aggregate Bill Cash confirmation at 16:31:32 UTC returned 200; the failure is not every Bill or every Cash collection.

## Safe code fix

`Payments/Features/SessionBilling/BillOrderingClient.cs`: retain only allowlisted business error codes on Quote/Freeze conflicts: billing.legacy_payment, billing.empty, qr.session_closed, discount.review_required and order.cancel_pending. Unknown/malformed upstream conflicts keep the old billing.stale fallback; never copy upstream private details to the caller. A real fingerprint mismatch still produces billing.stale.

No endpoint/DTO/schema/migration/amount/status/ledger/payment behavior change. Existing Mobile error mapping already has an appropriate Vietnamese message for billing.legacy_payment. No alternate payment path, automatic retry, QR confirmation, session closure or finance-guard bypass added.

Source updated both in integration candidate `be/payment-main-integration-20261119` and exact-runtime-base worktree `fix/first-pos-session-20261119`. The latter starts from deployed revision 396b7e8 and does not import the new main migrations. Only Payments needs a later runtime update for the corrected reason to reach the current app. Ordering's previously approved first-order hotfix remains untouched at runtime.

## Tests and runtime boundary

- Before fix: 6 FAIL/6 PASS in expanded HTTP typed-client tests reproduced lost legacy/empty/closed reasons.
- Runtime-base focused regression: **41 PASS/0 SKIP** (12 typed-client Quote/Freeze cases plus prior session/first-POS/mobile-counter regression). Service/auth/EF InMemory doubles, not PostgreSQL/Gateway/phone E2E.
- Flutter Session Bill widget/repository suite: **19 PASS**, including separate legacy/stale explanations, no Cash form, no payment POST on conflict. Formatter unchanged; analyzer **0 issues**.
- Candidate typed-client regression: **12 PASS/0 SKIP**; overlaps the 12 cases included in the runtime-base 41, do not add these as distinct coverage.
- Payments image build **PASS**: `aicos/payment:bill-conflict-reason-20261119`, image `sha256:8615677d13581b97008d5f71c4b1e1e5cfc2f2e0d8e135a790a67970be59d012`. Runtime is still original image `sha256:97a20b743bc2c4ffccef8ece325d3a877cffff6daefb2ff1e84ac84462b0d5b0`, container `b48d6cec0579fe48e2fd413f3e83cf26a4cd756c409546727450aa97559eaf21`, revision 396b7e8. Building the image did not replace or start a container.
- No PostgreSQL mutation or provider E2E run for this reason-code fix. Previous 23 PostgreSQL tests concern the prior first-POS patch, not new proof of legacy financial reconciliation.

Backend test: `tests/AICOS.Services.IntegrationTests/DiscountReviewClientTests.cs`. Mobile test: `test/session_bill_backend_integration_test.dart`.

The user's latest runtime approval covered **only Ordering**. Payments container/credentials/flags stay unchanged until separately approved; no commit/push. A Payments update would correct the explanation only. Resolving S01 still requires a separately reviewed reconciliation plan preserving the existing successful Cash Payment, outstanding orders and exactly one aggregate billing obligation. Do not reset/delete the session or collect the paid 208,000 VND again.
