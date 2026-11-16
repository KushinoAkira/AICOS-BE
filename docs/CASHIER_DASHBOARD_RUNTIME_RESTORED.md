# Existing Backend runtime restored — PASS

Date: 2026-11-16. User requested restoring the remaining runtime, then explicitly selected **Backend only**. No device launch, application login/JWT, payment/shift mutation, feature activation, migration, GRANT, image build or container recreation was performed.

## Actual operations

Kept PostgreSQL, Redis, Payments and Reporting running with the exact same IDs/images/start times/restart counts. Started the nine stopped existing containers in dependency order: Identity, Menu, Table, Ordering, Kitchen, Workforce, Customer, AI and Gateway. All retained their IDs/images/environment. Financial Gate A/B config and data were not reset by a generic Compose up.

All **13 containers running**, zero restarts. PostgreSQL/Redis Docker health healthy; ten downstream apps passed HTTP ready/live/docs/metadata. Gateway live/root PASS; its separate ready/Swagger endpoints are N/A by source. AI remains a placeholder: runtime/docs PASS does not mean implemented AI business/model functionality.

Initial verification stopped at AI because the host waiter incorrectly used 8011; actual immutable Docker mapping was **127.0.0.1:5211 -> 8000**. This was my verification-script error, not an AI service error. Confirmed AI was still running, derived all host ports from HostConfig, preserved the original DB baseline, and resumed AI verification/Gateway startup without restarting the seven previously verified services or AI. No Docker/service config was edited.

## Gateway/API evidence

Gateway base: **http://127.0.0.1:8081**.

- GET metadata via Gateway to all ten downstreams returned 200; Identity public subscription-plan GET returned 200.
- Missing token and malformed non-JWT bearer string both returned **401** for nine protected GET routes: Identity auth/me, Menu categories, Table floor-plan, Ordering orders, Kitchen queue, Payment cashier-shifts, Reporting collections, Workforce employees/me and Customer staff notifications.
- This proves routing/negative authentication, not successful user login, current-user RBAC/branch isolation or authenticated Dashboard payload correctness. No token was forged or generated to force 200.
- Existing App credentials/JWT/Collections key unchanged. CashierShifts remains ON for Payments/Reporting; dispatcher/RequireOpenForCash remain OFF. SessionBilling/migration/seed/expiry/admission/recovery/print and other previously OFF features remain OFF.

## Database and financial checks

READ ONLY baseline for all nine .NET service DBs plus role metadata matched after each startup and at final verification. No schema/ACL/history/table data changed. Unrelated containers retained IDs/images/state/start/restart counts. The four kept dependencies were not restarted.

Approved Gate B receipts remain **2 outbox / 2 Reporting facts / 2 ACKs / 258.000đ Cash**, both ShiftId=null, cashier-shift rows still 0. Actual reporting_svc READ ONLY SELECT confirms two facts and 258.000đ historical Cash. Client-day check for **16/11/2026 Asia/Saigon**, UTC [2026-11-15T17:00Z,2026-11-16T17:00Z), returns **0đ**: the two receipts keep November 4/6 dates. This SQL audit is not an authenticated user/branch API response.

## Mobile and remaining scope

Per user choice, Mobile/device verification is **DEFERRED**. ADB discovery before that choice found no attached device; no emulator, app install, reverse mapping or app operation was performed. No production Dart/UI/test change remains from this step, and no Flutter/analyzer result is claimed. Existing V2 work is preserved separately.

Cashier Dashboard code composes shift/collection/Bill reads independently. The enabled shift/collection APIs should be tested with a valid existing account later; pending Bill area can remain unavailable because SessionBilling is still OFF. Zero current-day Cash and no open shift are valid states, not reason to seed money or invent an OPEN drawer. Do not count old receipts in today's total.

Current verdict: Backend runtime/routing and unchanged-data verification **PASS**. Authenticated Dashboard/business/Mobile E2E **NOT RUN**, normal financial dispatcher and SessionBilling **OFF**, real bank QR/printer/V2 transfer not part of this task. No old ledger recovery was rerun.

Evidence: CASHIER_DASHBOARD_RUNTIME_RESTORE_RESULT.json. Private original baseline is retained outside Git. The earlier Gate B execution report accurately described the four-up/nine-stopped state at that earlier stage; this restoration supersedes that runtime state.
