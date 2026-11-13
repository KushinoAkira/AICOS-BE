> Latest isolated migration/full-stack validation and remaining Reporting blocker: [SESSION_BILL_MIGRATION_AND_E2E_VALIDATION.md](SESSION_BILL_MIGRATION_AND_E2E_VALIDATION.md). Earlier test counts below are historical.

# Session Bill Backend and Mobile implementation

This supersedes the earlier P0-only checkpoint for the new feature-flagged Bill flow. Source implementation is complete for Cash and controlled test QR; this is NOT an operating deployment or real-bank certification.

## Scope and database evidence

The NEW main backend uses service databases and schemas: aicos_ordering.ordering and aicos_payment.payment. Read-only inspection found existing orders/table_sessions and legacy payments/payment_webhook_events, but no session Bill ledger. The old aicos_db.public model must not be assumed to describe this checkout. No operating database, Windows database or service credentials were modified.

The last attempt to inspect operating migration history could not run because aicos-main-postgres had stopped externally. It was NOT restarted. Therefore prerequisite migration history, operating service ACL, and rollout readiness remain NOT VERIFIED. Do not apply these changes to any operating DB until its actual architecture and history are approved.

## Ownership and state machine

- Ordering owns table sessions, orders, the immutable checkout snapshot/fence and the paid acknowledgement inbox.
- Payments owns aggregate Bill, attempts, money proof, audit and durable synchronization journal.
- Kitchen owns production/fulfillment and stores paid projection using its existing EarlyPayments/ticket mechanism. No direct access to another service database.
- Table reads Ordering occupancy over authenticated HTTP; it does not infer a free table merely from payment.
- GET before collection returns DRAFT/version 0, a read-only quote, NOT a persisted or paid Bill. Collection freezes authoritative stored prices and creates UNPAID ledger/attempt. Cash then records SUCCEEDED/PAID in the same local transaction. QR remains PENDING until authenticated provider evidence.
- Bill UNPAID -> PAID -> CLOSED. Paid is independent of cooking, pickup and serving. PAID never closes the session or resets Kitchen/custody. PAID cannot be reopened for new orders.
- Ordering fence Frozen -> Paid -> Closed. Ordering's table lock linearizes Add Order, freeze, transfer and close. Financial writes (including legacy unscoped DineIn paths) cannot bypass a frozen session.
- Conservative safety: checkout is already frozen while QR is PENDING or a collection outcome is uncertain. No timeout automatically releases this fence. Manager cancellation/unfreeze is not implemented without a separate approved policy.
- Close requires valid money proof, matching immutable snapshot, durable paid synchronization, no unresolved legacy/new payment, and all non-void orders delivered. CupV1 requires matching total count, all served, no review, and FulfilledAt. Unknown fulfillment modes fail closed.
- Payments seals under its own lock, Ordering commits closure under its own table lock. This is a retryable saga, not a cross-service transaction. A sealed Bill alone is insufficient for Mobile to show the table closed; it verifies Ordering too.

## Consistency and retry

1. Ordering commits a frozen snapshot before Payments collects money. If Payments stops here, retry resumes the same deterministic BillId/fingerprint.
2. Payments uses PostgreSQL transaction/advisory locks per request key and Bill; unique tenant/session, key hash, transaction reference and partial pending-attempt indexes prevent double collection. Keys are actor-scoped; reuse with a different payload returns 409.
3. Cash requires confirmReceived=true and sufficient whole-VND amountTendered. Paid attempt and PAID audit/outbox commit together.
4. QR intent/reference commits BEFORE gateway I/O. Timeout leaves a discoverable PENDING intent. The same key never calls provider Create again. Frontend polls GET; it cannot assert success.
5. PAID journal dispatches after commit over internal HTTP with leases/fencing/backoff. Both Ordering and Kitchen must ACK after commit. Fast response dispatch is bounded to five seconds; the background worker resumes durable pending work after restart. Redis Pub/Sub is not payment truth.
6. Repeated signed receipt is compared by typed identity, not raw JSON strings (PostgreSQL jsonb normalizes formatting). Duplicate/conflicting evidence is audited. Signed amount mismatch and conflicting receipts require reconciliation rather than automatic success or another collection.
7. Per-order legacy payment is refused for DineIn while SessionBilling is enabled. Takeaway and flag-off public contracts remain. Existing legacy money is detected through Payments' own tables and quarantined before Bill collection.

## Public API (existing Gateway wildcard routes)

| Method / path | Authorization | Behavior |
|---|---|---|
| GET /api/v1/orders/qr/sessions/{sessionId}/billing-quote | Staff, tenant/branch | Authoritative quote or frozen snapshot |
| GET /api/v1/payments/bills?sessionId={id} | Staff, tenant/branch | DRAFT or persisted aggregate Bill |
| GET /api/v1/payments/bills/{billId} | Staff, tenant/branch | Payment status and transaction reference |
| POST /api/v1/payments/bills/{billId}/payments/cash | CASHIER / BRANCH_MANAGER / BRAND_ADMIN | Idempotency-Key; sessionId, fingerprint, amountTendered, confirmReceived |
| POST /api/v1/payments/bills/{billId}/payments/qr | Same counter roles | Idempotency-Key; sessionId, fingerprint, providerCode; test Mock only |
| GET /api/v1/payments/bills/{billId}/invoice | Same counter roles | Verified PAID/CLOSED immutable financial snapshot only |
| POST /api/v1/payments/bills/webhooks/Mock | Test-provider HMAC, not a user JWT | Isolated test only, never frontend confirmation |
| GET /api/v1/orders/qr/sessions/{id}/close-eligibility | Staff, tenant/branch | Advisory only; POST rechecks |
| POST /api/v1/orders/qr/sessions/{id}/close | Existing close RBAC | Rechecks settlement/serving, idempotent |

Bill response includes id, sessionId, branchId, status, version, fingerprint, quote (rounds/lines/subtotal/discount/tax/serviceFee/total), payment (id/method/status/transactionRef/checkoutUrl/amount/paidAtUtc), orderingAcknowledged, needsReconciliation, providerIsTest, qrAvailable. Current domain has no service charge/tax calculation; both are explicitly zero, not inferred from missing response fields.

Internal machine API uses X-Aicos-Billing-Key (separate >=32-character environment secret): Ordering freeze/paid/occupancy; Payments proof/seal; Kitchen paid. It is outside /api/v1 and is not routed by the Gateway wildcard. Missing/wrong key is rejected; do not expose service ports publicly as an alternative to Gateway.

Feature-off returns unavailable for new Bill APIs. HTTP 409 indicates stale quote, frozen session, conflicting request, unsettled/unfinished session, or reconciliation. Missing auth/role is 401/403. SERVER+CASHIER uses the existing multi-role authorization; SERVER alone cannot collect or fetch paid invoice.

## Source changes

- BuildingBlocks/AICOS.Contracts/Billing/SessionBilling.cs; BuildingBlocks/Security/BillingMachineAuth.cs: additive contracts and machine authentication.
- Ordering/Features/SessionBilling/: checkout entity/mapping, quote, SaveChanges financial write guard, internal endpoints, Payments client and close coordinator.
- Payments/Features/SessionBilling/: Bill entities, contracts, collection ledger, locking, webhook receiver, outbox dispatcher, service clients and endpoints.
- KitchenOperations/Features/SessionBilling/: paid admission synchronization with existing early-payment storage and ticket locks.
- TableReservation/Features/DiningTables/TableOccupancyProjection.cs: authoritative occupancy projection preserving OutOfService and dependency failure.
- Four service Programs and appsettings: registration, key validation only when enabled, explicit default OFF. DbContexts map owned tables only; offline design factories do not start hosts or seed.
- Existing QR/Order delivery/per-order collection paths receive conditional guards; routes/old DTO fields retained, checkoutLocked additive. Existing migrations unchanged.
- Flutter model/repository/screen/widgets: scoped backend Bill state, read-only refresh, explicit Cash receipt, pending QR, exact-key retry, verified paid invoice preview, guarded close and add-more lock. Demo Mode/theme preserved.

## Verification

- Final full Backend regression: **526 PASS, 50 SKIP, 0 FAIL (576 total)**, including the three occupancy tests.
- PostgreSQL isolated: 6/6 PASS, 0 SKIP. First run executes the empty-new-table rollback test alone; second runs five financial/concurrency/restart tests. Guarded container aicos-billflow-postgres-01a4dcdd22, database aicos_billflow_test, cluster 7707708698774257699, random loopback port 57869, own private network and RAM-only data. Resources removed after tests; no operating credentials used.
- The final runner initially stopped before schema creation because startup readiness raced with cluster inspection. TCP cluster-readiness retry fixed the runner; all identity guards remain.
- Tests cover 16 distinct cashiers, Add vs Cash, payment before serving, unserved close refusal, parallel close, lost ACK, Payment host restart, pending QR restart, ten duplicate signed receipts, audit uniqueness, amount mismatch hold, provider timeout intent, legacy guard, scope/auth and default-off compatibility.
- HTTP suite uses WebApplicationFactory + EF InMemory; PostgreSQL suite uses the real Npgsql migrations/locks/constraints and in-process HTTP hosts. Both use explicit Menu/Table client doubles, recorded event delivery, test-signed JWTs, simulated provider. These are NOT deployed full-stack/provider/Redis/physical-phone E2E.
- Flutter: 296 PASS; analyzer 0 issues. Thirteen new tests use mocked HTTP (not real bank E2E), including timeout/exact-key retry, duplicate tap, QR PENDING, paid invoice, dual-service close verification and widths 360/393/430.
- SKIP is not PASS: normal Backend run skips opt-in infrastructure tests; six Bill PostgreSQL cases were actually executed separately. No physical printer, real provider, Gateway/Redis E2E or Web FE client checkout was run.

## Remaining activation blockers

1. Approve actual target databases/schemas, predecessor histories, guarded migrations and service grants. Operating history cannot currently be verified; do not activate against absent schema.
2. Configure separate machine secret and all service URLs; all four flags must be coordinated. No production flag enabled in this task.
3. Real QR provider adapter/credentials/verification/reconciliation is MISSING. Mock requires non-Production environment, AllowTestProviders, separate >=32-character webhook secret AND physical DB name aicos_billflow_test (or the explicit InMemory test provider). It cannot be used against operating service DBs.
4. New Bill refund, cash-shift, Workforce/reporting aggregation are not integrated with the new ledger. No fabricated per-order payments/events were emitted to make them appear complete. Assess downstream consumers before enabling money collection.
5. Zero-total/all-void Bill settlement and authorized frozen-checkout cancellation need policy and separate implementation. No automatic unfreeze.
6. Web FE was not cloned; flag-on requires its DineIn checkout to use Bill rather than per-order payment. Flag-off baseline tests pass; that does not certify live Web FE.
7. Controlled deployed E2E with real Identity, Gateway, Redis, Kitchen recovery/CupV1 and service-role ACL remains NOT RUN. Physical receipt transport remains out of scope.

No deploy, operating migration, commit or push performed. Keep feature OFF until all activation prerequisites are separately approved.
