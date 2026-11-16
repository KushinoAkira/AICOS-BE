# Dashboard Gate A — configuration PASS, 2026-11-16

User explicitly approved creating a separate Collections machine key, DPAPI storage, and recreating only Payments/Reporting with CashierShifts enabled, dispatcher OFF and optional Cash shift enforcement. No historical recovery, SessionBilling, app login/JWT, operating payment/shift test, migration or GRANT was approved or performed.

## Preflight

Existing features-OFF runtime images/container IDs matched the prior smoke evidence and were healthy. PostgreSQL container/cluster remained c014190eed2d / 7703616372368646182. No active database writer or recent Gateway business-mutation request was observed. All clients were kept out of business operations during the gate. Private READ ONLY snapshots covered nine .NET service schemas/data/ACL/history and role/container metadata.

Baseline: two missing historical Cash receipts, **258.000đ**; collection_outbox/Reporting collection_facts/cashier_shifts all **0**. These are the previewed legacy Orders, not new Bills. Original Order/Payment/session/receipt states were preserved.

## Secret and config handling

Generated **32 random bytes**, Base64 only in process/container environment. The encrypted artifact COLLECTIONS_GATE_A.dpapi is protected with current-user DPAPI, separate fixed purpose entropy and current-user/SYSTEM directory ACL outside Git. Encrypted GATE_A_RUNTIME_BEFORE.dpapi preserves the two original container configs. Encrypt/decrypt roundtrip and restricted-directory checks passed; neither key nor unencrypted config dump was saved in source, Git or logs. The child process environment was restored/cleared by the wrapper after use.

Private dashboard-gate-a.yml contains only a required environment-variable reference, no secret value. Compose merges base + main-runtime + dashboard-smoke + this private overlay using the already verified source revision 396b7e8596bb1d993272d6fe7c0620b6c62db7c4. The original tracked configuration/.env, DB credentials, JWT and Payment encryption keys were not edited.

For payment/reporting only:

- CashierShifts__Enabled=true.
- CashierShifts__MachineKey supplied from DPAPI, equal producer/receiver values verified in memory.
- CashierShifts__DispatcherEnabled=false.
- CashierShifts__RequireOpenForCash=false.
- Every other actual environment value equals the previous container, including migration/seed/expiry/SessionBilling/recovery/print flags OFF.

A PowerShell wrapper-generation syntax error was caught before execution, corrected, and both PowerShell/Node syntax checks passed before generating any key or replacing a container. It was not a failed activation stage.

## Executed order and results

1. Reporting: single-service Compose up -d --no-build --no-deps --pull never reporting. Same image ID, new container ID, approved environment diff only. Live/ready/Swagger/Gateway metadata PASS; missing/malformed token on collections GET **401/401**. No schema/data/ACL or unrelated-container changes.
2. Payments: same command scoped to payment. Same image ID, new container ID, approved environment diff only. Live/ready/Swagger/Gateway metadata PASS; missing/malformed token on cashier-shifts GET **401/401**. No schema/data/ACL or unrelated-container changes.
3. Quiet observation **12 seconds**, exceeding the worker's ten-second timer interval: dispatcher stayed OFF and no historical recovery occurred. Final outbox **0**, Reporting facts **0**, cashier shifts **0**; original two eligible Cash sources remain **258.000đ**.

Both services have no new migration/seed/error/fatal/reconciliation-warning evidence in the checked startup logs. Role metadata unchanged, all pre-existing non-Payments/Reporting container IDs/images/state/start times/restarts preserved, and nine service DB snapshots matched after each stage and at completion. PostgreSQL Windows was not connected. No rollback or old-image startup.

## Current capabilities and limits

Gate A configuration/runtime **PASS**. CashierShifts now exposes enabled shift/collection contracts and enables capture for future payment transactions; this is not a read-only feature flag. Do not resume payment/shift business actions merely because config passed. Dispatcher remains OFF, so even future captures will not reach Reporting until separately approved dispatch.

No authenticated business GET returned 200 under this gate: no login/JWT was created. Invalid-token 401 is auth protection evidence, not proof of cashier data/RBAC E2E. Mobile Dashboard business E2E and historical receipt delivery are **NOT RUN**. Earlier 18 in-memory Cashier regression tests are a separate checkpoint, not new PostgreSQL or operating tests here. No Dart/UI changes or Flutter suite run.

SessionBilling remains OFF, so the pending Session Bill area can remain unavailable independently of the now-enabled cashier shift/collection APIs. No new Bill was manufactured for the two legacy Dine-in payments; one legacy session remains OPEN. Collections still correctly read empty before any approved recovery/new collection. Old receipts retain original November 4/6 dates, not today's total.

## Continuation and recovery boundaries

Do not rerun the prior features-OFF smoke/old Compose command blindly: its baseline expects the old two container IDs and flags OFF, and omitting the private overlay would disable this approved config. Reload the DPAPI key in process memory and retain this overlay for any separately approved future recreation; never paste/decrypt the key to a file or chat. Docker admin access can inspect environment; DPAPI protects storage, not that boundary.

Next: prepare and isolated-test the guarded one-shot historical dispatcher invocation and review legacy collection policy. Gate B must separately approve the exact two source IDs/258.000đ and outbox/inbox/ACK writes. Normal worker operation, SessionBilling and authenticated business/Mobile testing are additional scopes; no implied approval from Gate A. On failure pause at that stage, retain encrypted config/evidence and repair forward; do not delete collections, rotate unrelated keys, restore old DBs or restart other services automatically.

Evidence: CASHIER_DASHBOARD_GATE_A_RESULT.json. Private per-stage snapshots and encrypted key/config remain outside Git.
