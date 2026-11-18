# CI Kitchen queue diagnosis — 2026-11-07

## Evidence from the supplied CI logs

- Kitchen subscribed to OrderCreatedIntegrationEvent and PaymentSucceededEvent at 19:39:13, before the Order POST at 19:39:29.
- Ordering creation returned 201; cash returned 201; Ordering subsequently reported Paid.
- Kitchen queue returned 200 repeatedly from 19:39:32 to 19:39:57, but the smoke assertion did not find the Order ID.
- No Kitchen handler exception appears in the supplied logs. Absence of an exception does not prove ticket creation or payment propagation succeeded.
- Early Gateway/Identity connection-refused errors resolved before setup succeeded. Expected subscription 404, branch 422 and wrong-amount 422 are negative tests, not evidence of this failure.
- PostgreSQL fast shutdown during its image initialization is followed by successful final startup. It is not evidence of a database crash during this Order.

## Source findings and uncertainty

Legacy KitchenPaymentSucceededHandler returns when no committed ticket exists and does not retry. Redis callbacks for different events run independently, so payment can be processed before admission commits even when both subscriptions were ready. This is a source-level race, **not yet the confirmed cause of this specific CI run**.

Queue visibility also requires matching tenant/branch, WholeOrder mode, no recovery hold, a nonterminal status, and either payment or DineIn. The supplied log has neither ticket state nor queue response, so it cannot distinguish missing admission, unapplied payment, filtering, or an unexpected response shape.

The CI recovery flags remain off; no durable recovery guarantee may be inferred from Redis subscriptions or HTTP health checks.

## Initial diagnostic changes (before reproduction)

- KitchenAdmissionService logs the committed ticket identity and relevant eligibility fields after its transaction returns.
- KitchenPaymentSucceededHandler separately logs a missing ticket, an already-paid ticket, and a successful payment-state save.
- Logs exclude credentials, tokens, customer details, item notes and raw request/response bodies.
- No business transition, endpoint, schema, migration, Payment/Bill or Mobile UI change. The existing failing CI assertion remains intact; no extra wait or forced ticket creation masks it.

## Initial validation (before Docker was available)

`dotnet test tests/AICOS.Services.IntegrationTests/AICOS.Services.IntegrationTests.csproj --no-restore --filter 'FullyQualifiedName~KitchenTests|FullyQualifiedName~KitchenRemakeTests|FullyQualifiedName~CupServingGuardTests'`: **27 PASS, 0 FAIL, 0 SKIP**. These tests use the existing HTTP/InMemory harness and offline migration metadata, not real Redis/PostgreSQL. Existing CS0628/CS8620 warnings remain. This is regression evidence for the diagnostic change, not proof that the CI failure is fixed.

Real infrastructure reproduction was initially **BLOCKED / NOT RUN**. Docker Desktop Linux Engine pipe was unavailable, including outside the sandbox. This blocker was resolved when the user started Docker; subsequent test execution is recorded below.

## Isolated reproduction and fix

Docker Engine 29.7.2 became available after the user started Docker Desktop. The existing guarded runner created PostgreSQL 16/Redis 7 containers with random loopback ports, dedicated network, tmpfs storage and random credentials. It used EnsureCreated only on the approved disposable test databases. No EF migration, persistent volume or operating database was used.

1. Normal Takeaway creation and payment propagation through the production Redis event bus: **10 PASS** including 9 existing operational tests. Payment is a synthetic integration event; the Payments service/cash collection HTTP endpoint is not part of this harness.
2. Added a deterministic commit gate: admission has inserted its ticket inside a transaction, but cannot commit until the test releases it. The real Kitchen payment handler is invoked directly in a separate scope, using real PostgreSQL. The gate releases when the handler returns or PostgreSQL reports its advisory-lock wait; no arbitrary sleep controls the race.
3. Before the fix: **10 PASS / 1 FAIL / 0 SKIP**, with `IsPaid` remaining false. TRX: `%TEMP%/aicos-staff-operations-ab2dfa75ef/staff-operations.trx`. This demonstrates the defect, not recovery success.
4. Fix: KitchenPaymentSucceededHandler now obtains the same tenant/Order transaction lock as admission before looking up/updating the ticket. It verifies branch identity, performs the payment mirror update transactionally, and announces visibility after commit. No HTTP/SignalR call is added inside this transaction. Public routes/DTOs and Payment/Bill code are unchanged; no migration required.
5. After the fix: **11 PASS / 0 FAIL / 0 SKIP**. TRX: `%TEMP%/aicos-staff-operations-e1490b4d5e/staff-operations.trx`. The same regression that failed before now passes. The controlled overlap uses direct handler invocation; the normal case uses real Redis delivery.

Both runs verified existing container ID/image/start/status/restart snapshots unchanged and cleaned up only their new containers/network. An initial test compile attempt failed because it referenced an internal serialization helper; the test was corrected to use the public production IEventBus and rerun successfully. This attempt is not counted as a test PASS.

Changed files: `Features/KitchenEventHandlers.cs` (transactional mirror update/branch guard/logs), `Features/KitchenAdmission/KitchenAdmissionService.cs` (post-commit diagnostics), `tests/AICOS.Services.IntegrationTests/StaffOperationsInfrastructureTests.cs` (normal real-Redis path, controlled PostgreSQL overlap, duplicate assertions), `tests/AICOS.Services.IntegrationTests/KitchenTests.cs` (wrong-branch payment regression), this report and generated `docs/STAFF_OPERATIONS_INFRASTRUCTURE_RUN.md`. Mobile only records the work in its implementation log; no Dart/UI changes.

Backend regression excluding the three opt-in infrastructure classes: **483 PASS / 0 FAIL / 0 SKIP**. This includes the new wrong-branch check. Excluded classes are not counted as PASS: StaffOperationsInfrastructureTests runs separately, KitchenAdmissionRecoveryTests and CashPostgresTests were not run in this task. Existing CS0628/CS8620 warnings remain.

Final infrastructure run after adding concurrent duplicate assertions: **11 PASS / 0 FAIL / 0 SKIP**, `%TEMP%/aicos-staff-operations-29953c9f0e/staff-operations.trx`. The real-Redis path plus four concurrent replays retained one ticket and exactly two stickers for two cups. Guard cluster `7705381838323441699`, dedicated network `aicos-staff-operations-29953c9f0e`, loopback ports 62466/62467. Existing containers unchanged; temporary resources removed by guarded runner. Formatter verification for changed Kitchen source and `git diff --check`: PASS. No Flutter suite rerun because no Dart changed.

## Limits and remaining verification

- This fixes **overlap with admission already in progress**. A payment event received before admission starts, lost Redis messages, or a permanently missing OrderCreated event still require the separate durable recovery/reconciliation design. No recovery flags were enabled here.
- The historical CI log is consistent with this reproduced race, but contains no persisted ticket state; it cannot prove the original run took exactly this path.
- Full Docker Compose/Gateway/cash-payment HTTP smoke and GitHub Actions rerun are still **NOT RUN**. Re-run CI after the user commits/pushes; retain fail-fast checks and inspect the new diagnostics if it fails.

Do not enable recovery in the operating environment or apply database migrations as a workaround. No commit/push was performed.
