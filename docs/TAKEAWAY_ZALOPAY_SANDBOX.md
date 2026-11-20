# Takeaway ZaloPay sandbox adapter

Status: source implemented, default **OFF**, no operational provider call/deployment or migration performed. This adapter is not bank production readiness.

## Contracts

- `GET /api/v1/payments/staff/takeaway/{orderId}/online?branchId=...`: pure database/order-directory read; no expiry, provider query, SaveChanges or event.
- `POST` same path with `{branchId,amount}` + stable Idempotency-Key: CounterRoles + live CashActorFilter; validates branch, non-session order and exact whole VND amount. Calls existing OnlinePaymentCreation with a single-attempt guard under the existing per-order PostgreSQL lock. No Bill entity or second financial ledger.
- `POST .../online/{paymentId}/query?branchId=...`: explicit provider reconciliation, reuses main's query/PaymentProcessor. Not invoked by read polling. Payment ID, order, branch, tenant and provider must match.

Response Version=2, ProviderCode=ZaloPay, ProviderIsTest=true, state None/Pending/Confirmed/Failed/Unknown, Available/CanCreate, existing PaymentResponse and QrPayload. QR content is the HTTPS **sandbox checkout page URL**, not invented bank-transfer data.

Availability requires Development, `TakeawayQr:ZaloPaySandboxEnabled=true`, network ZaloPay integration enabled, TestPage=false, exact `https://sb-openapi.zalopay.vn` endpoint, enabled Sandbox-mode ZaloPay provider and a valid merchant setting for the branch. Flag defaults false. Production host, misleading Sandbox label with wrong endpoint, internal AICOS test page or unsupported provider cannot activate the adapter.

## Retry and safety

- Existing order payment is the durable intent. Concurrent creates coalesce to one row/provider create. Replay remains the same attempt after terminal failure/success; this new adapter does not adopt Web FE's policy of starting another attempt after failure.
- Existing Cash/foreign-provider/mismatched amount/branch/multiple attempts require reconciliation; no new QR is created.
- Provider HTTP occurs after intent commit. Lost create ACK leaves an existing attempt; replay never resends create. Read restores by orderId; explicit query/callback may confirm backend financial truth.
- If the provider still reports pending and checkout URL was lost, **do not create again**. Recovering the checkout URL itself needs provider-supported lookup/manual reconciliation; no unsafe retry or “failed because timeout” fallback.
- Main signed callback/return/query handlers remain authoritative; Mobile has no manual-success/simulate action. QR does not require an OPEN cash shift and is not assigned to a cash drawer by this adapter.
- Existing public API behavior remains unchanged by the optional `singleAttempt=false` default. V1 `/qr` remains Mock-compatible. Aggregate Session Bill continues rejecting ZaloPay until its own BillAttempt callback/recovery contract is approved.

## Mobile

Main order detail uses `MainTakeawayQrRepository(providerCode: 'ZaloPay')` for Takeaway. Repository reuses the existing QR screen, loading/error/retry, strict scope/amount/provider/version validation, HTTPS sandbox URL allowlist, and GET-only polling. A separate reconciliation button calls POST query. Mock remains the repository default for old explicit consumers/tests; Demo Mode remains unchanged.

The QR action can open the status/explanation screen even when unavailable, so a disabled backend does not look like an unresponsive button. CanCreate still comes from backend, never a local enforcement flag. Current runtime old image/missing flag/credentials can legitimately show unavailable until an approved deployment/configuration gate.

## Files / tests

Backend: `TakeawaySandboxEndpoints.cs`, optional singleAttempt logic in `OnlinePaymentCreation.cs`, existing query handler reused via internal visibility, Program registration and default OFF config. Tests: `TakeawaySandboxTests`, `TakeawaySandboxPostgresTests`; harness constructor/guarded runner extended.

Mobile: main_takeaway_qr_repository, main_order_detail_screen, takeaway_qr_payment_screen and `takeaway_sandbox_test.dart`. No printer, Session Bill financial logic or Refund feature change.

Regression results are recorded in Mobile IMPLEMENTATION_LOG and the final validation note below. HTTP provider transports/Identity/JWT/Menu are test doubles; PostgreSQL tests prove SQL/locking/lost-ACK recovery only. No actual external ZaloPay sandbox, bank production, Redis/Gateway/phone E2E or Reporting settlement test was performed for this adapter. No real secrets were requested/generated/reset.

See [local schema read-only review](LOCAL_SCHEMA_REVIEW_20261119.md) for the seven separate pending migrations blocking a blanket runtime replacement. No new migration is needed for this adapter itself.

## Validation — 19/11/2026

| Run | Actual result | Scope |
|---|---|---|
| Backend solution regression | 993 PASS, 66 SKIP, 0 FAIL | BuildingBlocks 15; Identity 210; Services Integration 718/66 SKIP; Smoke 50. Not a Docker stack E2E |
| Guarded PostgreSQL runner | 22 PASS, 0 SKIP, 0 FAIL | Scratch cluster 7709907286060032035, container aicos-billflow-postgres-2caf7153f9; test-only EF/SQL, private network/tmpfs, cleaned afterward |
| New PostgreSQL sandbox cases | 2 PASS, included in 22 | Eight parallel HTTP creates → one Payment/provider create; lost create ACK → replay/query same intent. Provider gateway is a double |
| Flutter full | 427 PASS, 9 SKIP | `flutter test --dart-define=AICOS_MAIN_API=false`; HTTP MockClient, not external provider |
| Flutter final focused UI | 16 PASS | Sandbox QR, order detail layout and cashier order flow after final diagnostic-navigation change; overlap with full suite |
| Flutter analyzer | 0 issues | After final code changes |
| External ZaloPay / Gateway / Redis / phone / Reporting adapter E2E | NOT RUN | Requires separately approved sandbox runtime/credentials/callback and deployment gate |

Skipped tests are not PASS. Do not add focused/PostgreSQL counts to regression totals as independent coverage. Existing test warnings CS0628/CS8620 remain; no operational schema/data/image/secret change, no commit or push.
