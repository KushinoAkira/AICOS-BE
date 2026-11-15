# Cashier money shifts and confirmed collection reconciliation - 2026-11-14

## Scope and decisions

Cashier money shifts are owned by Payments. Workforce cash closing is tied to attendance/check-in and payroll assignments; it is retained unchanged for Web compatibility, not reused as a money drawer. No new microservice.

- One OPEN money shift per tenant/cashier, across branches; branch authorization is resolved through Identity, cashier from authenticated claims.
- Opening a shift is NOT mandatory for collecting money. Cash without an open shift remains an unassigned confirmed receipt.
- User decision: QR stays unallocated to a cashier shift, reported separately by branch. No QR amount is counted as physical drawer cash. The shift DTO returns qrCollected=null and an explicit allocation label, not a fabricated zero.
- OPEN -> CLOSED. No invented Manager approval state or automatic discrepancy resolution. Manager can inspect authorized shifts and differences but cannot edit confirmed receipts or a closed count.
- Opening cash + confirmed Cash = expected drawer. Refunded cash/unknown refund allocation blocks closing; no invented payout/refund/shift attribution policy. A mismatch requires a counted note, never a compensating fake payment.

## API contract

All public paths are under /api/v1; existing paths/responses are preserved.

| Method and path | Scope / behavior |
| --- | --- |
| POST /payments/cashier-shifts | branchId, openingCash, note; Idempotency-Key 8-80 chars; current actor, branch resolved by Identity; one open drawer |
| GET /payments/cashier-shifts?branchId=&from=&to=&status= | Own shifts for Cashier, branch shifts for Manager/Admin; latest 100 |
| GET /payments/cashier-shifts/{id} | Amounts, immutable closed snapshot, version, discrepancy, refund blocker |
| GET /payments/cashier-shifts/{id}/transactions | Confirmed receipt proofs only, latest 100, authorized shift |
| POST /payments/cashier-shifts/{id}/close | version, countedCash, note; owner only; same closed body replays safely, a different closed body conflicts |
| GET /reporting/collections?branchId=&from=&to= | Confirmed collections by branch and method, latest 100 proofs; maximum 93-day query range |
| POST /internal/collections | Private M2M inbox; separate X-Aicos-Collections-Key; stable EventId and identical payload required; ACK only after commit |

Public shift/collections endpoints require Cashier, BranchManager or BrandAdmin with tenant/branch checks. SERVER alone receives no permission. Role-set claims can satisfy Cashier without manual role switching. Manager viewing another cashier cannot close that cashier's drawer.

## Financial contract and durability

CollectionReceipt is a confirmed-money event: stable payment-attempt EventId, tenant, branch, BillId XOR OrderId, confirming CashierId where known, nullable ShiftId, method, amount, paidAtUtc, reference and Recovered flag. QR confirming cashier is not fabricated from the initiator.

- CashCollection, PaymentProcessor success and BillLedger MarkPaid write CollectionOutbox inside the existing local payment transaction. No cross-service transaction and no Redis dependency for delivery.
- Cash capture and shift open/close use the same PostgreSQL actor advisory lock. A captured Cash receipt increments the shift version in the payment transaction; counting against an older version returns 409.
- CollectionOutbox is both immutable receipt history and durable outbox. Only DeliveredAtUtc is updated after an exact receiver EventId acknowledgement.
- Reporting CollectionFact is an inbox/read model keyed by the stable event ID. Duplicate delivery is ACKed, a different payload for that identity returns 409. PostgreSQL advisory locking serializes concurrent receipt replays.
- The worker retries undelivered batches. It scans all missing durable successful legacy Payment and valid paid Bill sources in batches, without a time cutoff. Missing old receipts are marked Recovered and never inferred into a historical/closed shift.
- A restored outbox that conflicts with an already recorded Reporting payload is not overwritten: it remains pending for reviewed reconciliation. Closed drawer figures remain immutable.

## Collections are not net sales

The new Reporting endpoint aggregates only CollectionFacts, once per confirmed attempt. It does not add old OrderFact payment amounts to these totals, so legacy events and new receipts are not counted twice.

The old /reporting/branch GrossRevenue contract is retained unchanged for Web. A per-Bill allocation of discounts, tax, refund and order revenue is not yet approved; netSales and refundedAmount in the new collection report remain null. Do not relabel collected money as net sales or publish fake PaymentSucceeded events for each order in a Bill.

Workforce attendance-based cash_transactions is also unchanged. The new money drawer is payment.cashier_shifts, not the salary ledger. The old 210000 collected / 0 Reporting gap must be assessed against the new collection endpoint, while legacy gross sales remains a separate known blocker.

## Files and configuration

- Contracts: AICOS.Contracts/Billing/CollectionReceipt.cs; BuildingBlocks/Security/CollectionMachineAuth.cs.
- Payments: Features/CashierShifts/CashierLedger.cs, CashierShiftEndpoints.cs, CollectionDispatcher.cs; hooks in CashCollection, PaymentProcessor, BillLedgerService and BillWebhookReceiver; PaymentsDbContext and Program.
- Reporting: Features/Reports/CollectionReports.cs, DbContext and Program.
- Two new EF migrations: 20261114125030_CashierMoneyShifts and 20261114125038_ConfirmedCollectionInbox. Three new tables; no rewrite of prior migration or existing financial data. Snapshot changes are expected scaffolding output.
- Test runner: --cashier mode in scripts/tests/session-billing-stack.mjs, existing StackTools offline exporter, SessionBillHarness and CashierShiftTests.

Feature CashierShifts:Enabled defaults OFF; no operating Compose/env or secrets changed by this task. Required future environment: CashierShifts__Enabled, CashierShifts__MachineKey (separate secret, at least 32 chars), Services__Reporting__BaseUrl; Database__MigrateOnStartup=false. DispatcherEnabled can pause dispatch independently, while durable receipts remain available. Missing sender URL/key leaves receipts pending, never acknowledged.

## Migration and rollback

Read docs/migrations/cashier-shifts/README.md, SQL files and SHA256SUMS.txt. Additive Up scripts use migration history, target/cluster/approval guards. UUID IDs need no sequence permissions. Payment receives SELECT/INSERT plus specific shift-close/version and outbox-delivery UPDATE columns; Reporting SELECT/INSERT only. No cross-service database reads or foreign keys.

Rollback is empty-only, with an explicit refusal guard in EF Down and SQL. For a nonempty finance ledger disable the feature and retain data; use an approved forward fix or restore drill on a separate database. Never silently delete receipts to roll code back.

## Validation and remaining blockers

See CASHIER_SHIFT_STACK_TEST_EVIDENCE.json for the final isolated run; skipped infrastructure tests are not treated as real PostgreSQL coverage. Initial interrupted runner was cleaned only after exact container label/network verification. A rollback SQL quoting error and first-password-change fixture failure were caught on test copies and corrected; no operating mutation occurred.

Remaining decisions: refund/payout allocation to money shifts, Manager review/approval policy, gross/net sales allocation per Bill, and rollout approval. QR bank production, physical printer and physical-device E2E are outside this change. No deploy, operating migration, production flag enable, commit or push.

## Final execution results

- Backend regression: 555 PASS, 51 SKIP, 0 FAIL. Five CashierShiftTests PASS (including receipt recovery without changing a closed drawer, version stale 409, M2M malformed receipt and duplicate inbox protection). Infrastructure SKIPs are not PostgreSQL results.
- Flutter regression: 326 PASS, 2 SKIP; analyzer 0 issues; focused shift UI/repository 8/8 PASS at 360/393/430px. Two real-HTTP Flutter tests passed inside the isolated runner, including money shift/receipt rendering. They are host widget tests, not device E2E.
- PostgreSQL/Redis/Gateway stack: 37 PASS groups, 1 BLOCKED, 0 FAIL; cluster 7708060898147119145.
- Confirmed collection fixture: 210000 VND now matches Reporting collections. Legacy gross remains 0; this is a distinct unapproved sales-allocation metric, not a hidden collection failure.
- Cash counts, parallel close, foreign-cashier denial, lost-ACK replay, Payments/Reporting/Redis restart, QR Mock PENDING/confirmed callback, nonempty rollback refusal and restore-copy integrity PASS. Existing operating containers unchanged: true. Only this run test resources removed.
- The final worker also keeps retrying internal HTTP timeouts (not shutdown cancellation) and shift DTO exposes pending Reporting delivery count. Focused validation was rerun after these additive safety/presentation changes.
- Remaining BLOCKED: gross/net Bill allocation, refund/payout attribution, Manager approval policy and operational rollout. Real bank and physical device/printer NOT RUN.

Final safety validation: a linked Bill marked NeedsReconciliation blocks closing the drawer; actor lock/version rules remain intact. A separate injected-HttpMessageHandler timeout test proves the background worker stays alive and leaves the receipt unacknowledged; it is a mock transport test, not PostgreSQL evidence. Final full regression after these changes: 555 PASS / 51 SKIP / 0 FAIL.

## Dashboard validation — 2026-11-15

CashierShifts:RequireOpenForCash defaults false. Existing Cash without a money shift remains supported when OFF. When ON, CashierLedger checks the actor's own OPEN drawer in the same branch under the existing transaction-scoped actor lock before saving the collection receipt. A CLOSED/no drawer rejects new Cash with 409 cashier.open_shift_required. RequireOpenForCash=true with CashierShifts disabled rejects Cash with cashier.shift_unavailable instead of bypassing the guard. No schema, public DTO or endpoint change.

QR is still branch-only, never counted as drawer cash. Recovered receipts do not retroactively change a closed drawer. Idempotent replay of an already committed payment does not become a new cash collection after close. Both legacy Cash and aggregate Bill Cash use this shared capture path. Feature is enabled only in disposable test containers; no operating environment modified.

Read CASHIER_DASHBOARD_E2E_VALIDATION.md for the actual source boundary, PostgreSQL/Redis/Gateway and real Flutter HTTP results. The paused Item Transfer V2 working tree is excluded from the validated stable worktree. Do not claim its full build or production readiness from these tests.

Validation complete: full .NET solution on stable source plus this Cashier diff is 832 PASS / 51 SKIP (Services 564/51, BuildingBlocks 15, Identity 203, Smoke 50). Flutter 347 PASS / 4 SKIP, analyzer 0 issues. Real test stack enforcement ON: 49 PASS groups / 3 BLOCKED, Cash 175000 + QR 70000 = 245000. OFF: 50 PASS groups / 3 BLOCKED, Cash 315000 + QR 70000 = 385000, nine receipts. Mobile Cash UI itself confirms tender 50000/change 15000 on one 35000 Bill. These are actual HTTP host-widget tests, not Android device tests or bank certification. Both runs preserve existing containers and clean their own resources. No operating migration/feature enable or push. User later approved committing only validated scope; paused V2/local rollout excluded.

## Flutter opening/closing shift validation — 2026-11-15

MainCashierShiftScreen now has real HTTP host-widget probes for OPEN/CLOSE. Existing API only; no application code, schema or financial policy changes. The isolated stack performs UI opening 100000, Cash 35000 with tender/change, stale close version 409, UI counted close 135000, immutable close retry and history reload. Closing the cashier drawer does not close the unserved Bill/table session. Final 53 PASS groups / 3 BLOCKED / 0 FAIL, cluster 7708345966499213353; Payment = Reporting = Dashboard 280000 (Cash 210000, QR 70000, six receipts). Flutter 347 PASS / 6 SKIP, analyzer 0 issues. Existing containers unchanged and test resources cleaned. See CASHIER_SHIFT_UI_E2E.md; no operational feature enable or push.

## Cash precheck before collection — 2026-11-15

GET /api/v1/payments/cashier-shifts/cash-precheck is additive and exposes authoritative account/shift eligibility before opening a Cash form. It is outside the shifts-enabled filter so OFF can return NOT_REQUIRED without querying shift tables. Counter roles and branch/tenant scope still apply. Both Cash POST routes and precheck reread Identity /auth/me (forwarded bearer) to reject revoked/changed role or branch; Identity failures deny collection. Transactional shift guard and idempotency remain authoritative, QR unchanged. Read CASH_PRECHECK.md for contract, ownership, rollout dependency, scope and actual ON/OFF E2E evidence. Backend 842 PASS/51 SKIP; Flutter 358 PASS/7 SKIP, analyzer 0 issues; each isolated mode 56 PASS groups/3 existing blockers. No new migration, operational database change or commit/push.
