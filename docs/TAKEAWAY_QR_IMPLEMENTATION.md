# Takeaway QR contract V1 — test-only implementation

Date: 2026-11-17. This feature is NOT production-ready or enabled in the operating stack.

## Scope and API

Payments owns both additive routes under `/api/v1/payments/staff/takeaway/{orderId}/qr`:

- GET with `branchId`: strictly read-only attempt recovery. No expiry, provider query, event publication or SaveChanges. Version 1 returns order/branch/amount, availability, Mock/test marker, state, canCreate, reasonCode, attempt and QR payload.
- POST with `{branchId, amount}` and `Idempotency-Key`: Cashier/BranchManager/BrandAdmin, fresh Identity actor verification and active branch. Reads Ordering via its contract; rejects Dine-in and price/scope mismatch.
- Existing `/transactions`, webhook and settlement routes remain. The legacy create path now rejects the reserved `takeaway-qr:v1:` Description prefix so clients cannot forge intent metadata.
- No Takeaway Bill entity, SessionBilling change, new schema, migration or service permission is required.

## Feature gates

New flags default false: `TakeawayQr:Enabled`, `TakeawayQr:AllowTestProvider`.
Both require Development environment and existing `CashierShifts:Enabled` collection capture. Provider is fixed to Mock; no selection of a bank/provider in production. The runner injects random Mock webhook credentials only into test processes, not Mobile/source/logs.

## Durable admission and replay

Reuse the existing Payment row and Description (max 200 chars). Store reserved marker + SHA256(tenant, actor, key) + SHA256(order, branch, amount, Mock); no raw key/secrets. This fits the existing field. Order lock plus key advisory lock serialize admission and cross-order reuse. Actor/key replay is checked before payable-state validation so terminal success/failure returns the original attempt; body drift conflicts. Existing attempt of any origin blocks another staff QR admission.

CreatedBy remains the auditable actor. Commit the row/reference before checkout I/O; do not hold the transaction across provider calls. Mock checkout is deterministic for the same reference. A worker repairs missing checkout on a still-valid Pending attempt; never creates an Order/payment again or restores an expired/cancelled QR. Local expiry hides QR but retains uncertainty and custody of the financial obligation.

`Confirmed` is the API view of a single valid Succeeded Payment, zero refund and matching amount/branch. Other/legacy methods, multiple paid records or mismatched facts produce Unknown/reconciliation, not fabricated success. QR token `aicos-mock://payment/{ref}?amount=...` is test data, not a bank instruction.

## FAILED policy and blockers

A tagged FAILED attempt remains financially held: same-key replay is allowed, but new QR/Cash replacement is denied. This is intentionally stricter than legacy provider-failure retry. Manager release, trustworthy terminal finality, contradictory late callback, Refund and permission/audit for a new financial attempt require separate policy approval. No automatic release is implemented.

## Confirmation, outbox and recovery

Reuse signed HMAC provider webhook and append-only webhook records. Succeeded Payment and immutable collection proof/outbox are saved within the existing payment transaction. Reporting uses its existing durable inbox/ACK, unique receipt identity and branch-only QR allocation; QR does not enter Cash drawer/ShiftId.

A Development/test-only worker continuously pages tagged Succeeded Payment rows and republishes the existing idempotent PaymentSucceededEvent. It has no age cutoff; every pass restarts at the beginning after the last page, and process restart begins from durable ledger state. No cross-service SQL or distributed transaction. Reporting's dispatcher separately retries collection outbox. Redis reconnect alone is not recovery proof.

The scan offset is deliberately non-durable: it is only a fairness hint, not an acknowledgement/watermark, so resetting it cannot lose obligations. Before production, review replay volume, admission metadata/index strategy, atomic webhook audit boundaries and a durable service-to-service ACK contract.

## Flutter

Mobile has a shared repository for create/read/recovery, stable actor/Order request identity across restarts, scope/amount/proof/payload validation and Creating/Pending/Confirmed/Failed/Unknown states. Order Detail shows QR only from Backend capability. Existing Cash flow and SessionBilling UI are retained. QR rendering uses qr_flutter, no remote image service or hardcoded sample QR. The UI never simulates provider callbacks, marks PAID or closes sessions.

## Tests and evidence

- Initial Backend HTTP tests: 3 PASS (EF InMemory/recording bus, not PostgreSQL).
- Flutter repository/widget: 5 PASS (HTTP doubles), including lost response, cold recovery, FAILED/offline/scope and 360/393/430px.
- Full-stack PostgreSQL/Redis/Gateway runner: `AICOS_TAKEAWAY_QR_TEST=true`, optional `AICOS_TAKEAWAY_QR_UI_TEST=true`, `AICOS_MOBILE_WORKSPACE=...`, `node scripts/tests/session-billing-stack.mjs --cashier`.
- Guards: new labelled container/network, no host database port, tmpfs rather than operational volume, random credentials, verified cluster before SQL, baseline schema/ACL rehearsal on test only, preexisting container snapshots unchanged, guarded cleanup.
- Evidence file: `docs/TAKEAWAY_QR_E2E_EVIDENCE.json`. Do not treat an earlier failed run as PASS. The first run timed out at health during competing regression load; the second reached QR but its role assertion had an extra helper argument (403 was correct); the next encountered live Flutter timer/FakeAsync teardown during service outage. The updated live scenario closes PENDING, receives callback while offline and cold-reopens only after Backend proof. It does not claim on-screen polling through every crash boundary.
- The full Backend regression attempt was stopped due host memory pressure, not counted as PASS. Restart/SIGKILL at every commit boundary, real phone scan, production provider and banking integration remain NOT RUN.

## Rollback / production approval

No migration/SQL rollback exists because no schema is added. Isolated fixtures are disposable; operating databases are untouched. Do not downgrade away from the marker hold guard while tagged unresolved payments exist: that could permit Cash after FAILED. Preserve ledger/audit and resolve forward under an approved procedure. Before production: approve FAILED/new-attempt rules and provider contract, review storage/permissions/audit/recovery, complete resource-bounded regression and all crash matrix cases, rehearse rollout. Do not simply enable Development flags on production.
## Verified focused E2E result

Final focused runner: **27 PASS groups, 0 FAIL, 0 suite BLOCKED**, exit 0. Read `TAKEAWAY_QR_E2E_EVIDENCE.json` for the exact verified cluster. This is a targeted Takeaway suite, not a declaration that all business blockers are resolved.

Command: enable the two test process variables `AICOS_TAKEAWAY_QR_TEST=true` and `AICOS_TAKEAWAY_QR_UI_TEST=true`, set `AICOS_MOBILE_WORKSPACE`, then `node scripts/tests/session-billing-stack.mjs --cashier --takeaway-only`.

Verified actual PostgreSQL/Redis/Gateway and real Flutter HTTP: same-key concurrent admission with one row, drift rejected, Cash exclusion, checkout NULL fault injection/restart repaired using the same id/reference, cold Order-based recovery, signed callback while Ordering offline, Redis/Payments restart, duplicate webhook one audit row, Flutter creates PENDING then exits and cold-reopens verified CONFIRMED, stable FAILED replay and no replacement, simultaneous fresh Cash/QR exactly one collection, no duplicate Reporting fact after restart.

Ledger/Reporting increase: **70,000 VND across two paid obligations**. Primary QR is 35,000 VND; the other 35,000 VND goes to whichever method wins the deliberate Cash/QR race. Assertions compare the corresponding QR/Cash splits and leave the failed obligation at zero received. The primary QR outbox has ShiftId NULL. Both ledger and Reporting equality were checked, not fabricated to match. The final backup/restore compares original Payment rows, webhook audit and collection outbox in addition to the session tables.

All preexisting container IDs/images/start times/status/restart counts remained unchanged; final runner removed only its own labelled test containers/network and no operating volume. No operating DB connection or migration occurred.

An intermediate focused run passed every QR test but failed at a SessionBilling empty-table rollback assertion not applicable to Takeaway. The new `--takeaway-only` branch omits that unrelated nonempty-Bill assertion, retains schema/ACL rehearsal and nonempty collection rollback refusal, and does not create a fake Bill. A separate combined legacy run timed out at durable Bill sync; it was not relabelled PASS. The final focused result supersedes neither that combined test nor the production/FAILED-policy blockers.
## Final regression checkpoint

- Services integration regression, serialized with one test CPU/collection: **589 PASS, 51 SKIP, 0 FAIL** (640 total, 2m58s). Includes 4 new Takeaway contract cases: replay/drift/success, FAILED hold/Cash exclusion, OFF/reserved-prefix validation, and scoped read that hides expired QR without mutating the Pending row.
- Flutter full regression: **389 PASS, 9 SKIP**, analyzer **0 issues**. Live Takeaway QR is separately PASS inside the focused Gateway runner; it remains opt-in/SKIP in ordinary regression.
- Final focused cluster **7709126715418345513**, 27 PASS/0 FAIL, exit 0. The live harness now starts post-frame HTTP outside FakeAsync; that fixes the earlier timer invariant instead of suppressing it.
- No new migration/schema, operating DB write, production flag activation, deployment, commit or push. Changes stay in the existing Backend worktree on `be/cashier-dashboard-main-sync`; the separate unfinished V2 branch was not overwritten.