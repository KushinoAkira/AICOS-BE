# Cash collection — main Backend and Staff Mobile

Date: 2026-11-04 (Asia/Saigon). Server changes are only in AICOS-BE.

## API contract and compatibility

- GET /api/v1/payments/staff/capabilities (Staff) now returns cashCollectionVersion=1,
  cashIdempotency=true, cashBlocksPendingOnline=true. Mobile fails closed on an older service.
- POST /api/v1/payments/transactions/cash keeps its existing request body. Optional
  Idempotency-Key (max 80 chars) enables durable replay. Mobile always sends one.
- First confirmation: 201. Same cashier/key/body: 200 and the same payment, even
  after Ordering has already become Paid. Changed order/amount/branch/tendered with
  the same key: 409 payment.request_key_conflict. Keys are scoped to tenant/cashier.
- Existing PaymentResponse adds optional amountTendered and change. Old rows with
  unknown tendered return null; they are not reconstructed as exact change.
- Deliberate behavior correction: pending online attempts are no longer silently
  cancelled by cash. Cash returns 409 payment.online_pending. Known provider receipt
  awaiting application returns payment.reconciliation_required; settlement flags it.
  This applies to existing callers too. Request URLs/body and old response fields remain.
- Barista/customer cannot record cash. Cashier/BranchManager/BrandAdmin retain the
  existing branch and tenant checks. Whole VND and tendered >= due, <=100,000,000 VND.

Example (no credentials):

```http
POST /api/v1/payments/transactions/cash
Authorization: Bearer <normal-login-token>
Idempotency-Key: <new-uuid-for-this-confirmation>
Content-Type: application/json

{"orderId":"<order>","branchId":"<branch>","amount":50000,"amountTendered":100000}
```

Response: payment.status=Succeeded, payment.method=Cash, payment amount=50000,
amountTendered=100000, change=50000, real cashier/timestamps and transactionRef.
Ordering state is read separately; a cash receipt never implies a printed bill.

## Persistence and concurrency

- PaymentOrderLock uses a PostgreSQL advisory transaction lock by tenant/order and
  wraps the whole transaction in the Npgsql execution strategy. Online creation,
  cash, provider success/failure, expiry and cancellation use the same boundary.
- Cash adds an advisory lock for the cashier/request identity, preventing concurrent
  reuse of one key across different orders. A committed replay is found before the
  Ordering unpaid check. Stable prepared identity handles lost commit acknowledgement.
- CashCollection stores a versioned request hash + tendered in existing Description.
  No plaintext token/password, new column/index, migration or backfill. Actor/time
  remain in existing Payment/CashierUserId/auditable fields. Metadata is not a new
  immutable payment-event ledger or transactional outbox.
- Reload under the lock prevents stale expiry/failure/cancel from overwriting a paid
  attempt. New online attempts cannot race past a cash commit. Refund semantics and
  externally received late transfer semantics are preserved.
- Cash ledger commits before notifications. Replaying cash may resend idempotent
  PaymentSucceeded to repair Order projection. CashPaymentRecorded is emitted only
  for a newly created cash row. Notification failure is logged without secrets and
  never converted into a second cash row. No exactly-once event-delivery claim.

## Mobile behavior

Open Orders -> order detail, or Table detail -> view a round -> order detail.
Cash is enabled only for Counter staff and a positive unpaid, non-void order with
an available reconciliation snapshot. Preparation reloads capabilities/order/history/
settlement before opening the confirmation dialog. Backend rechecks authoritatively.

Enter tendered cash, see change, explicitly check received-cash confirmation, then
submit. No automatic POST retry. An ambiguous timeout/500 keeps the immutable request
key/body and offers same-confirmation verification; never tells staff to collect cash
again. Definitive rejection refreshes status. A validated cash response is retained
if the subsequent Order refresh fails. Payment/order/session/print states stay distinct.
Table detail refreshes after returning from order detail.

UI is split into cash DTOs, MainCashPaymentRepository, dialog and receipt card.
Existing screens coordinate navigation/state; Demo and original visual theme remain.

## Tests actually executed

- Full Backend HTTP suite: 448 PASS, 11 SKIP, 0 FAIL. The 11 skips are deliberate
  PostgreSQL facts when the scratch connection is absent; other tests use InMemory,
  fake service clients/Redis and test-signed JWTs.
- Dedicated PostgreSQL run: all 11 PASS, 0 SKIP. Real HTTP Payment handlers and real
  PostgreSQL; Ordering/Identity directory/event bus are explicit doubles, not full
  multi-service E2E or a real payment provider.
- Cases: 16 same-key concurrent confirmations; different keys/two cashiers; replay
  after Order sync; changed payload; cash vs provider success; duplicate provider
  notifications; rollback retry; lost commit acknowledgement; stale expiry/failure/
  cancel race; known receipt blocks recollection; cash vs online creation; one key
  concurrently used across two orders; persisted tendered/change assertions.
- Runner: scripts/Test-CashCollectionPostgres.ps1 -Run. Creates only a new PG16 scratch
  container, dedicated bridge network, tmpfs data and random loopback port. Database
  must be aicos_cashflow_test; port 5432/5433 and wrong cluster are rejected. Cluster
  identifier is probed from the owned container before EF EnsureCreated/test DML.
  Main/Windows/Gate D databases, volumes, credentials and JWTs are never used.
- Last real run: container aicos-cashflow-postgres-a194532705, cluster
  7704335304050274345, loopback port 63420. Removed owned scratch container/RAM data
  and empty network afterward. Initial internal-network port setup failed safely;
  the runner now uses a separate bridge and validates the assigned loopback binding.
- Flutter: 139 PASS; analyzer 0 issues after fixing three test-only brace lints.
  Includes money/confirmation at 360/393/430px and ambiguous-response same-key UI.
  Flutter tests use HTTP MockClient; no connected-device or real-money test claimed.
- Existing unrelated compiler warnings CS0628 and CS8620 remain.

## Runtime deployment and evidence

Built and recreated only aicos-main-payment with the main runtime overlay after
verifying credentials/JWT exactly match the prior runtime in memory. MigrateOnStartup
and AllowSimulation remain false. Seeder exits when migrations are disabled.
Payment health/ready=200; a normal local Cashier login accessed the capabilities
through Gateway and read settlement successfully. Logged out verification session.
Other running container IDs/images/StartedAt are unchanged.
Main payment rows: 0 before/after. Payment schema columns: 92 before/after. No cash
POST, refund, payment cancellation, seed or migration ran against operational data.
Existing orders/sessions were preserved. No Mobile build/device install was run.

## Limits and follow-up

- Real QR is still disabled in Mobile; simulated provider success is not receipt evidence.
- Advisory locking assumes all writers/replicas run this version. Never mix old and new
  Payment instances during a rollout; arbitrary direct SQL bypasses service guards.
- No transactional outbox: projection/event delivery can need reconciliation. Cash
  replay repairs PaymentSucceeded but is not a durable retry of every downstream event.
- Client pending keys live in the current screen session. After restart, inspect real
  payment history first; server order locks still prevent another cash row.
- A real transfer arriving after explicit cancellation/expiry must still be recorded
  as late money and reconciled, including actual duplicate external funding. Cash
  guards cannot prevent a customer sending a transfer outside the app.
- Cross-service Order cancellation/payment is not a distributed atomic transaction.
  Existing late-payment/refund/reconciliation rules apply; no saga/outbox schema was added.
- Main integrated cash -> Redis -> Ordering -> Kitchen/session was not mutation-tested
  here. Next step is a controlled multi-service test using isolated fixtures, not
  recording fictitious cash against a user's live order.

## Rollback without SQL

Stop new cash confirmations first. Previous container/image IDs are in the local
TEMP/aicos-payment-before-cash-runtime.json evidence (no secrets). Retag that image
as aicos-main/payment:local and recreate only Payment using both Compose files with
--no-deps --no-build. Old Backend lacks capabilities, so updated Mobile blocks cash.
No schema/data rollback is needed; do not delete real payment rows. No rollback,
commit or push was performed automatically.