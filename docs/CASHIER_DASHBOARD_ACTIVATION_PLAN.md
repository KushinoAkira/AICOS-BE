# Cashier Dashboard activation — staged execution

Date: 2026-11-16. Initial document was design only; separately approved Gate A configuration has now passed. Runtime smoke has since passed under its separate approval; financial activation remains a later gate. See CASHIER_DASHBOARD_ACTIVATION_REVIEW.md for the current read-only preview and proposed gates.

## Existing contracts to reuse

- Payments: GET /api/v1/payments/cashier-shifts and existing shift/transaction detail routes; Cash precheck remains the shared server contract.
- Reporting: GET /api/v1/reporting/collections; metric CONFIRMED_COLLECTIONS_NOT_NET_SALES, explicit branch and UTC [from,to) boundaries. Keep refund/net fields unavailable rather than deriving unsupported net sales.
- Payments CollectionDispatcher -> Reporting POST /internal/collections uses CollectionMachineAuth and stable EventId acknowledgement. No new endpoint/schema needed.
- CollectionReceipt contains EventId, tenant/branch, BillId XOR OrderId, actor/shift, method, original amount/PaidAtUtc/reference and Recovered. EventId is the original payment/attempt ID, not a newly generated duplicate.
- Mobile already validates the metric and labels the summary 'Khoan thu da xac nhan' (Vietnamese UI). No redesign required for this activation task.

## Flag coupling and startup behavior

CashierShifts:Enabled does more than expose read APIs: it also turns on CashierLedger capture inside subsequent payment transactions. Do not call this a read-only Dashboard-only switch. Keep CashierShifts:RequireOpenForCash=false unless a separate policy decision enables enforcement; this preserves the current optional-shift Cash policy.

CashierShifts:DispatcherEnabled=false is necessary during staged preparation. CollectionDispatcher scans missing historical payment receipts BEFORE checking the Reporting URL/machine key. An absent machine key does not prevent local outbox recovery writes if the dispatcher runs. This is durable-source behavior, not proof that recovery has been approved.

SessionBilling:Enabled is independent and remains OFF until its own machine credential and cross-service configuration are ready. Do not enable Bill mutation just to make Dashboard read routes appear. CupV1, admission/recovery, split/group payment and refund activation are outside this Dashboard proposal.

## Minimal phased proposal (all future actions)

1. Finish approved features-OFF runtime smoke: candidate images/flags/credentials, service health/DNS/routes, original DB fingerprints unchanged. No business login/JWT or writes during that smoke.
2. Prepare separate random CashierShifts machine key for Payments/Reporting in an existing safe secrets mechanism (DPAPI/private environment), without reusing JWT/DB credentials or printing values. Preserve original secrets. Verify producer/consumer key equality in memory; do not create the key before activation preparation is authorized.
3. Read-only preview the two legacy candidates reported by the runtime audit. Recheck at execution time: original payment ID, tenant/branch, status, PaidAtUtc, method/provider/mock status, amount, reference validation, missing outbox/inbox and any existing Bill/order relationship. Store any detailed preview privately; publish only sanitized counts/sums. Do not infer genuine bank verification from a legacy simulated/mock success.
4. Present exact preview and obtain approval for CashierShifts enablement and historical recovery writes. Keep dispatcher OFF while applying/verifying keys and enablement. Do not test real operating payments or create a new shift merely to get a 200 response.
5. Only after explicit recovery approval, run the existing dispatcher in a controlled scope, monitor each of the two stable IDs and committed ACKs. It may create Payments collection_outbox and Reporting collection_facts, then set DeliveredAtUtc; it must not modify original payment/Bill money, create a shift, issue a refund or invent an order.
6. Reconcile original confirmed payment proof -> durable outbox -> Reporting inbox -> scoped Mobile summary. Duplicate retry/restart must not count the same EventId twice. No age cutoff, no current-time replacement for original PaidAtUtc. Report discrepancies and stop, never correct money with ad hoc SQL.
7. Approve normal worker/feature operation separately from smoke. Expiry should return to its approved normal setting when business operation resumes; do not accidentally leave the smoke-only expiry switch OFF indefinitely.

## Financial invariants already approved

- Recovered Cash keeps ShiftId=null: never attach old cash to the currently open/new drawer. Original cashier identity is retained when known, not fabricated.
- QR remains branch-only and does not enter Cash drawer or a cashier shift.
- Collection reporting is confirmed receipts/gross collection evidence, not net sales/refund allocation. Source statuses include PartiallyRefunded/Refunded; preserve the original collection proof, do not silently derive a refund/net policy.
- Dine-in payment remains by one aggregate session Bill, not individually charging each Order. Historical legacy collection does not authorize re-billing or re-charging those Orders.
- Real bank QR/provider integration, refund allocation, and original net-sales policy remain distinct blockers. No simulated status is used to claim a real received bank transaction.

## Acceptance and failure handling

Before enabling: full source/target IDs and exact counts reviewed; producer/receiver machine key valid; schema/ACL match; no pending ambiguity over the two source records; all app/client tests on the isolated environment distinguished from operating checks.

After enabling: durable source/outbox/inbox agree per EventId and scoped amount/method/PaidAtUtc; no duplicate; no old receipt assigned to a new shift; Mobile branch/day totals are based on original payment time; no original financial data or unrelated table altered. Test fresh Cash/QR business operations only on an isolated test stack unless separately authorized.

On failure: pause dispatcher/activation, keep receipts/audit and original payments intact, preserve private diagnostics, repair forward after review. Do not delete committed collections, reset a drawer, restore a pre-migration DB or re-run migrations/grants as a recovery shortcut.

## Current evidence and limits

Features-OFF runtime is now PASS: 11/11 apps, existing PostgreSQL/Redis healthy, all ten DB baselines unchanged during smoke. Separately approved Menu/Customer/GRANT committed and verified. These operations are recorded in their execution reports; they do not authorize financial activation.

Read-only historical preview: exactly **two Cash receipts, 258.000đ**, matching original Order amounts/projections and cashier scope, no outbox/inbox yet. One belongs to an OPEN legacy session with three Orders, the other to a CLOSED legacy session; no synthetic Bill or collection created. Review CASHIER_DASHBOARD_ACTIVATION_REVIEW.md before requesting configuration/recovery approval. Machine keys remain absent, feature/worker flags OFF; no new valid-JWT business or Flutter E2E.

## Gate A executed — 2026-11-16

User separately approved configuration only. Payments/Reporting CashierShifts enabled with one separate DPAPI-stored machine key; dispatcher OFF and optional shift policy preserved. Both service recreations/health/routes/401 PASS, all other containers and DB schema/data/ACL unchanged; outbox/facts/shifts remain zero, legacy Cash still 258.000đ. See CASHIER_DASHBOARD_GATE_A_EXECUTION.md and CASHIER_DASHBOARD_GATE_A_RESULT.json. Historical recovery, authenticated Mobile tests and SessionBilling remain unapproved/not run. Earlier preparation/flags-absent sections are historical evidence, not current config.

## Gate B tooling prepared — 2026-11-16

One-shot test tooling and private exact plan now prepared, PostgreSQL/real HTTP 19 groups PASS plus 18 in-memory related regressions PASS. No service business source/API or operating feature changed. See CASHIER_COLLECTION_RECOVERY_READINESS.md and CASHIER_COLLECTION_RECOVERY_PLAN.json. Historical recovery remains unexecuted and requires separate approval; earlier statements that no one-shot mode existed describe the initial audit.
