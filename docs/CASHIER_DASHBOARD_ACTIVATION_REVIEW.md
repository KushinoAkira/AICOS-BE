# Dashboard activation review — 2026-11-16

Preparation and READ ONLY preview only. Existing features-OFF runtime smoke passed 11/11 apps. This step did not create/install a machine key, change flags, restart a container, create an app JWT, run migration/GRANT, or write a historical receipt. The next financial configuration and write gates remain separate from runtime approval.

## Confirmed operating preview

Target: existing Docker cluster 7703616372368646182, never Windows PostgreSQL. All queries used BEGIN READ ONLY; fingerprints/schema/ACL of nine .NET service databases, role flags and all container IDs/state were checked unchanged. The AI placeholder has no business tables; it was not queried for business receipts. Detailed payment/Order/actor/session IDs remain in the existing restricted private backup directory outside Git. Public evidence uses aliases only: CASHIER_DASHBOARD_ACTIVATION_PREVIEW.json.

| Legacy receipt | Method | Amount | Original paid time (Asia/Saigon) | Session | Order amount/scope |
| --- | --- | --- | --- | --- | --- |
| legacy-1 | Cash | 208.000đ | 04/11/2026 23:38 | OPEN, three Orders | Matches; cashier/tenant/branch match |
| legacy-2 | Cash | 50.000đ | 06/11/2026 23:25 | CLOSED, close reason Paid, one Order | Matches; cashier/tenant/branch match |

**Total 258.000đ**, same branch. Both original Payments are Succeeded, Order payment projections refer to those exact IDs, reference/amount meet receiver validation. No refund/pending-refund ambiguity was found by the source preview. Cash does not require a bank webhook; provider references/webhooks are absent as expected and are not manufactured.

Currently: collection_outbox **0**, Reporting collection_facts **0**, OPEN cashier shifts **0**, paid session-bill recovery sources **0**. Neither legacy Order is assigned to a new aggregate Bill and neither session has a session_bills row. These are legacy order-level money records, not proof that aggregate Bill integration is active.

## Legacy Dine-in decision before historical recovery

Proposed minimal treatment: preserve each original Payment ID as EventId and original OrderId as the historical collection source. Keep BillId=null, Recovered=true, ShiftId=null and original amount/PaidAtUtc/cashier/reference. Write only durable collection proof/outbox/inbox/ACK, not new payments or synthetic session Bills.

Do not reopen/close either session, mark all three Orders paid in the OPEN session, transfer old money to a new shift, regenerate payment IDs, or charge the customer again. Existing SessionBillQuotes and BillLedgerService reject legacy-paid money with billing.legacy_payment; leave those guards intact. Collection recovery does not resolve or convert the OPEN legacy session to SessionBilling V2. That operational transition remains separate.

Collection reporting is CONFIRMED_COLLECTIONS_NOT_NET_SALES, separate from OrderFacts revenue. Do not add both totals or infer refund/net allocation. Preserve original dates: these two receipts would appear in the selected historical range, **not today's 16/11 total**. An empty new drawer and zero current-day collection are valid; never seed receipts or change PaidAtUtc to make Dashboard nonempty.

## Gate A proposed configuration — requires approval

Only Payments/Reporting would be recreated; retain the already verified candidate image IDs, database credentials/JWT, all other containers and smoke safety flags.

1. Generate a separate random 32-byte Collections machine key, store via the existing current-user DPAPI/private directory mechanism, and supply it through a process environment variable to Compose. Do not edit tracked .env/source, expose config dumps, print or log key values. Verify producer/consumer key equality in memory. Docker administrators can inspect container environment; DPAPI protects storage, not the Docker admin boundary.
2. Proposed private overlay for **payment** and **reporting** only: CashierShifts__Enabled=true; CashierShifts__MachineKey from the required runtime-only variable; CashierShifts__DispatcherEnabled=false; CashierShifts__RequireOpenForCash=false. Other flags remain OFF, especially SessionBilling, expiry, migration/seed, Cup/Recovery/print/refund/split.
3. Reuse base + main-runtime + dashboard-smoke + the private activation overlay with the same source revision. Exact startup shape: docker compose [existing env/overlay arguments plus private activation overlay] up -d --no-build --no-deps --pull never reporting, then payment; verify IDs/config/health and fingerprints after each. No whole-stack up.
4. Enabled affects future CashierLedger capture in payment transactions as well as read APIs: it is **not** a read-only switch. Keep business clients/tests paused; no operating payment/shift mutation or login/JWT under this gate. Historical worker stays OFF, so outbox/inbox must remain empty.

Key/config installation commands must be generated only after Gate A approval using the exact existing DPAPI path and image/config guards; no nonexistent helper is claimed available. Public read/auth smoke is allowed; successful authenticated Dashboard data still needs a separately approved login/test account path. Enabling CashierShifts alone will not enable Session Bill APIs/bill queue: SessionBilling has its own machine contracts and remains OFF.

## Gate B proposed historical writes — not ready for execution

Existing CollectionDispatcher scans all missing sources (up to 100) **before checking machine key**, and the hosted worker repeats every ten seconds. Merely omitting the key does not prevent local recovery writes. There is no existing one-shot CLI mode in StackTools and no allowlist endpoint; do not invent a command or call a fake internal API.

Before requesting execution: prepare/test a guarded one-shot invocation of the existing dispatcher, or obtain explicit approval for a wider normal worker window. It must assert the exact private two-EventId fingerprint/count/258.000đ, no new eligible sources, same source statuses/amounts/references/session relationships, no clients writing money, working source/target schema/ACL and matching machine keys. Test it in isolation. If scope changes or proof is ambiguous, stop before writing.

Only after separate review of legacy treatment and write approval: recover exactly two outbox receipts, receiver facts and stable committed ACKs. Verify source financial data/session status unchanged, no shift allocation, sums by method/branch/date/EventId agree, and retry cannot duplicate totals. Keep normal dispatcher activation separate unless explicitly included. No direct INSERT or original-payment update as a shortcut.

## Acceptance, failure and remaining work

- Current preview **PASS READ ONLY**; financial activation/recovery **NOT RUN**, keys absent, flags OFF. No full-stack business E2E is claimed from this audit.
- Gate A failure: stop at the failed Payments/Reporting stage; preserve keys/evidence, no automatic data rollback or old-image startup. Other containers remain untouched.
- Gate B failure: pause dispatch, retain durable receipts/audit and repair forward. Never delete collections, restore old DB snapshots, reassign a drawer or weaken legacy guards.
- Remaining: financial config approval; isolated one-shot recovery guard tests and explicit legacy policy/write approval; authenticated Mobile Dashboard test; separate SessionBilling/old OPEN-session transition. Real bank QR, printers and V2 transfers stay outside this step.

## Regression actually run

CashierShiftTests and CashPrecheckTests: **18 PASS, 0 FAIL, 0 SKIP**, from the new TRX result. These exercise real in-process HTTP handlers with EF in-memory storage and explicit test JWT/client/event doubles, including old receipt recovery and timeout behavior. They are not PostgreSQL/Redis full-stack E2E and do not validate operating financial writes. No Flutter run or operating app login was performed.

## Gate A executed — 2026-11-16

User separately approved configuration only. Payments/Reporting CashierShifts enabled with one separate DPAPI-stored machine key; dispatcher OFF and optional shift policy preserved. Both service recreations/health/routes/401 PASS, all other containers and DB schema/data/ACL unchanged; outbox/facts/shifts remain zero, legacy Cash still 258.000đ. See CASHIER_DASHBOARD_GATE_A_EXECUTION.md and CASHIER_DASHBOARD_GATE_A_RESULT.json. Historical recovery, authenticated Mobile tests and SessionBilling remain unapproved/not run. Earlier preparation/flags-absent sections are historical evidence, not current config.

## Gate B tooling prepared — 2026-11-16

One-shot test tooling and private exact plan now prepared, PostgreSQL/real HTTP 19 groups PASS plus 18 in-memory related regressions PASS. No service business source/API or operating feature changed. See CASHIER_COLLECTION_RECOVERY_READINESS.md and CASHIER_COLLECTION_RECOVERY_PLAN.json. Historical recovery remains unexecuted and requires separate approval; earlier statements that no one-shot mode existed describe the initial audit.
