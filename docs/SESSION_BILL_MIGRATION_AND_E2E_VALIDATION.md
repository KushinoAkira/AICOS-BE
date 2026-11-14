# Session Bill migration validation and isolated full-stack E2E

Date: 2026-11-14. No operating database/secret/feature, container or volume was changed. No deploy, commit or push.

## Result

| Area | Status | Evidence |
| --- | --- | --- |
| Reviewed migration SQL and checksum | PASS | Exact committed SQL files executed in scratch PostgreSQL; SHA256 unchanged |
| Full schema comparison | PASS | Tables/columns/types/nullability match all eight service EF models; PK/FK/check/index definitions inspected |
| New-table service ACL | PASS | Non-owner ordering_svc/payment_svc logins, real API writes, column-limited audit updates, denied DELETE/audit payload mutation, denied history UPDATE and cross-service CONNECT |
| Backup/restore and rollback | PASS, isolated only | pg_dump/pg_restore before expansion; guarded Down/Up on separate empty-new-table copies; nonempty ledger rollback refused; paid ledger and Ordering data restored exactly into separate copies |
| Full-stack API and live Flutter rendering | PASS | 28 acceptance groups; real Identity, Gateway, Menu, Table, Ordering, Kitchen, Payments, Reporting, Workforce, PostgreSQL/Redis; one live HTTP Flutter widget test |
| Reporting / cashier shift reconciliation | BLOCKED (financial acceptance not met) | Paid Bill total 210000 VND, Reporting gross 0, Workforce cash ledger 0; new receipt contract absent |
| Real bank QR / physical device/printer | NOT RUN | Mock callback only, no bank verification or physical device claim |

Machine-readable final evidence: [SESSION_BILL_STACK_TEST_EVIDENCE.json](SESSION_BILL_STACK_TEST_EVIDENCE.json). Final test cluster: 7707732407153684522. Scratch containers/network were removed after checks. All preexisting container IDs, image IDs, start times, statuses and restart counters are unchanged, including the stopped operating stack and old Gate D cluster.

## Isolation and execution

- Runner: scripts/tests/session-billing-stack.mjs and session-billing-stack-flow.mjs. Command: node scripts/tests/session-billing-stack.mjs from AICOS-BE, using Docker Desktop Linux containers, .NET 8 and local Flutter SDK.
- Source is published into unique temporary directories and mounted read-only into individually named aicos-bill-e2e-<nonce> containers using existing local runtime images. No main image tag, compose project or volume is overwritten.
- Dedicated network and tmpfs PostgreSQL/Redis, no published PostgreSQL/Redis host port. Only Gateway gets a random 127.0.0.1 port; operating 8080/8081 is untouched.
- Every test SQL call rechecks the exact container ID, purpose label, sole network, tmpfs, no host port and cluster system identifier. Only explicit test database names or guarded restore copies are accepted.
- Databases: aicos_billstack_identity/menu/table_reservation/ordering/kitchen/reporting/workforce and aicos_billflow_test for Payments (required by the existing test-provider guard). These are inside the NEW scratch cluster, never the similarly named Windows database.
- Independent random DB passwords, JWT issuer/audience/key, admission key, Bill machine key and Mock webhook secret; no operating .env or secret store is loaded. Credentials stay in memory / ephemeral container environment, never in tracked files or report output.
- Test roles have no superuser, createdb, createrole, bypassrls, ownership or cross-service database CONNECT. Existing legacy-owned tables get test-only DML to run existing services; EF migration-history privileges are explicitly revoked. This does NOT certify every old operating ACL; the exact new-five-table GRANT proposal was tested.
- MigrateOnStartup=false for all runtime services. Offline test tool exports baseline SQL without building a service host. A guarded one-shot fixture tool seeds only fresh test Identity admin, Mock provider and a synthetic historical closed session. Other accounts/tenant/plan/branch/menu/tables/orders are created through real APIs.
- Gateway source provides / and /health/live, not /health/ready or Swagger. Those two unsupported checks are N/A. All eight downstream services pass live/ready/Swagger; real business requests traverse Gateway routing and service DNS.

## Migration evidence

The approved pair remains unchanged:

1. 20261113125751_OrderingSessionCheckout / 01_ordering_expand.sql: ordering.session_checkouts.
2. 20261113125804_PaymentSessionBills / 02_payments_expand.sql: payment.session_bills, session_bill_orders, session_bill_attempts, session_bill_events.

One tenant/session has one Bill; a Bill has multiple order links. Partial unique pending-attempt/Bill, request-key hashes and transaction references are present. FK constraints reference only Payments-owned tables; no cross-service FK or new duplicate active-session index was introduced. CHECK definitions for states and amounts are present.

Each service starts from the correct predecessor baseline. The runner executes 00_preflight, reviewed expansion, reviewed 03_service_grants and 04_verify. Existing fixture hashes remain unchanged. Repeat raw expansion is intentionally REFUSED transactionally by the absence guard, with no duplicate objects or data changes; it is not a blindly rerunnable no-op. Stop/inspect history after a partial rollout, as the runbook requires.

Baseline backup is restored to a different database; new empty tables can then expand, roll back and expand again while preserving historical rows. Nonempty financial rollback is rejected. After operational tests, only scratch writers are stopped; paid Bill/attempt/order/audit and Ordering sessions/orders/checkouts are backed up and restored into separate scratch databases, with exact row-JSON hashes matching. Backups never overwrite an operating or source test database.

## Real defect found and fixed

The first business run observed Kitchen ReadyForPickup while Ordering was Preparing with ReadyAtUtc already populated. Redis Started and Ready handlers run concurrently in separate scopes. Both could read Confirmed; Ready commits first, then stale Started overwrites only status. SaveChanges table locking serialized writes but did not refresh the previously read state.

Fix (no migration/public API change): KitchenOrderProjection acquires the same per-order delivery lock and the table lock BEFORE reading/transitions. Started, Ready and Remake handlers reuse it. It retries a table move from a fresh transaction, applies domain monotonic/idempotent rules, commits before notifications, and does not touch payment calculations. Existing table/delivery/checkout serialization is retained.

A PostgreSQL interceptor regression deliberately pauses the first order read while Ready races Started. Final Ready/ReadyAt/Paid invariants pass. The full real Redis/Gateway serving and concurrent close flow then passes as well. The live widget harness also needed real socket timers and correct Windows launch/actual Gateway endpoint expectations; those were test harness fixes, not evidence of a payment failure.

## Acceptance cases actually run

- Real staff login and role-scoped accounts, tables, Menu-authoritative price, multiple rounds and one 105000 VND Bill.
- Unpaid DineIn admission; two Orders are explicitly ACKED by the durable Kitchen HTTP receiver, not merely visible through Redis legacy events.
- Cash explicit receipt, a TCP proxy deliberately drops the response AFTER successful Backend collection, GET recovers and same-key retry returns the identical payment.
- Paid invoice GET has the immutable amount/proof; Barista invoice access is 403; missing JWT is 401.
- Add after PAID returns 409; paid-but-unserved close returns 409.
- Actual Flutter Identity and Bill repositories use real HTTP to the scratch Gateway and SessionBillDetailScreen renders verified PAID / 105000 VND. This is a host widget test, not a physical-device gesture E2E.
- Barista Brewing -> ReadyForPickup; real Redis events update Ordering; Staff delivery; two cashiers close concurrently; one CLOSED Bill and Table Available.
- Two distinct cashier accounts race collection: exactly one success, one 409. Add Order vs Cash produces one consistent frozen snapshot (other side 409).
- Kitchen is stopped after admission; Cash remains durably PAID without ACK. Payments and Redis restart, Kitchen returns, and the persisted PAID outbox eventually synchronizes. No second collection.
- Mock QR is initially UNPAID/PENDING and survives Payment process restart. Invalid signature is 401; five duplicate signed success callbacks produce one provider receipt and PAID Bill. Actual contract is attempt SUCCEEDED / Bill PAID, not a newly invented CONFIRMED enum.
- No PAID/CLOSED Bill lacks a matching SUCCEEDED amount proof. Received money/audit is preserved through backup/restore; unsafe rollback is rejected.

## Reporting and shift blocker

The real Reporting branch API responds 200 but reports gross revenue 0 while new Bill receipts total 210000. Workforce cash_transactions is empty. Existing Reporting consumes per-Order PaymentSucceededEvent into OrderFact.PaidAmount; Workforce consumes CashPaymentRecordedEvent into its shift ledger. The new Bill journal deliberately does not fabricate per-order payment rows/events. Existing transaction history/reconciliation also reads legacy payments, not Bill attempts.

Therefore revenue by method/branch/day, transaction history and cashier shift money cannot be certified for Bills. The cash-closing Expected API additionally requires a real checked-in assignment; no shift submission/confirmation was simulated to disguise the absent receipt integration. Existing legacy reports remain unchanged.

Before implementing the adapter, approve a Bill receipt contract containing stable receipt/payment/Bill/session IDs, tenant/branch, collecting actor (attempt actor, not Bill creator), received timestamp, method/provider, and frozen order allocations. Decide discount/tax/fee allocation, rounding invariants, refund/late receipt corrections, order-vs-Bill paid counts, and shift attribution across handovers/midnight. Consumers need durable delivery/reconciliation, dedup by stable receipt ID and explicit source attribution to prevent counting legacy and Bill money twice. Do not use a Redis-only publish as reliable financial reporting. No new revenue policy/schema was invented in this task.

## Activation checklist and limits

- Keep all operating SessionBilling flags OFF. No operating migration, role grant, backup overwrite, credential change or deployment was performed.
- Approve actual operating target DB/schema/history and backups separately; isolated success is not authorization to migrate aicos_db or the newer service databases.
- Resolve Reporting/shift/history integration and receipt reconciliation before enabling collection.
- Real bank provider still missing; Mock is restricted to non-Production plus the named scratch Payments DB and test secrets.
- Frozen checkout cancellation/unfreeze, zero-total settlement and Bill refund remain separate policy/implementation blockers.
- WholeOrder flow was exercised. CupV1 device gestures, Web FE client, physical device/printer and real bank sandbox were NOT RUN.
- This fixes concurrency of Kitchen state projection; it does not make legacy Started/Ready/Delivered Redis Pub/Sub durable during arbitrary event loss. PAID outbox recovery and Kitchen admission ACK were tested specifically; general operational event replay remains a separate requirement.

## Final test results

| Run | PASS | FAIL | SKIP / BLOCKED |
| --- | ---: | ---: | --- |
| Backend regression after current main integration | 532 | 0 | 51 SKIP / 583 total |
| Related Tracking/Closing/Bill subset | 33 | 0 | 0 (subset, not extra cases) |
| Isolated PostgreSQL Bill suite | 7 | 0 | 0; 1 rollback + 5 financial + 1 controlled projection race |
| Isolated stack acceptance | 28 groups | 0 | 1 Reporting/shift BLOCKED |
| Flutter regular regression | 296 | 0 | 1 opt-in live-stack SKIP |
| Flutter live-stack test (run by stack runner) | 1 | 0 | 0; real HTTP, not a mock transport |
| Flutter analyze | 0 issues | | |

The 51 Backend skips include the seven opt-in Bill PostgreSQL cases; those seven were executed separately and are NOT silently counted as passed in the normal run. Other existing opt-in infrastructure cases remain skipped. Standard Flutter deliberately skips the live case without test runner environment; the same case was actually executed separately. Node acceptance groups are assertions/scenarios, not xUnit test counts. Do not add subset counts to regression totals.

Commands executed: dotnet test tests/AICOS.Services.IntegrationTests/AICOS.Services.IntegrationTests.csproj --no-restore; powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-SessionBillingPostgres.ps1 -Run; node scripts/tests/session-billing-stack.mjs; flutter analyze; flutter test --dart-define=AICOS_MAIN_API=false. The runner also invokes the opt-in Flutter file with ephemeral environment variables and no credentials in argv. ExecutionPolicy Bypass is process-local only, not a Windows policy change.

## Changed files

- src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/KitchenOrderProjection.cs (new) and OrderEventHandlers.cs: atomic Kitchen projection read/transition, shared by Started/Ready/Remake.
- tests/AICOS.Services.IntegrationTests/SessionBillProjectionPostgresTests.cs and scripts/Test-SessionBillingPostgres.ps1: deterministic new PostgreSQL race regression and explicit third test stage.
- tests/AICOS.Billing.StackTools/AICOS.Billing.StackTools.csproj and Program.cs: offline EF baseline/schema exporter, strictly guarded synthetic fixtures and network health probes. No service startup migrations.
- scripts/tests/session-billing-stack.mjs: container/cluster/database guards, SQL/ACL/backup/restore checks, source publishing to disposable containers and safe container cleanup.
- scripts/tests/session-billing-stack-flow.mjs: real HTTP/Gateway business scenarios, live Flutter invocation, restart/duplicate/concurrency and Reporting boundary checks.
- docs/SESSION_BILL_STACK_TEST_EVIDENCE.json, this report and SESSION_BILL_IMPLEMENTATION.md: evidence and scope/limits.
- AICOS-Mobile/test/session_bill_live_stack_test.dart, docs/IMPLEMENTATION_LOG.md and docs/SESSION_BILL_INTEGRATION.md: opt-in actual Gateway/PAID UI test and results. No production Mobile UI/configuration changed.

No additional migration was required for the race fix. The two existing proposed Bill migrations, SQL contents and SHA256SUMS remain unchanged in this task. Formatting and git diff whitespace checks completed. Existing compiler warnings are recorded in test logs; no compilation error remains.

## Revalidation before push after newer main integration

Integrated origin/main 5a4792a (seven newer commits) without source conflicts; only the unpublished local commit was replayed. New upstream Feedback follows the Bill migration, so the test exporter now applies later main migration deltas only inside scratch databases after the reviewed Bill expansion. Rollback tests target the exact Bill migration instead of assuming the last migration is Bill, and assert the complete migration-history set is restored. Unrelated newer migrations remain applied. The reviewed Bill SQL/checksums and operating configuration are unchanged.

Revalidation: Services integration **532 PASS / 51 SKIP**, Identity **203 PASS / 0 SKIP**, PostgreSQL Bill **7/7 PASS**, isolated full stack **28 PASS groups / 1 Reporting BLOCKED**, including the live Flutter HTTP test. This was test-only validation; no operating database or container was changed.
