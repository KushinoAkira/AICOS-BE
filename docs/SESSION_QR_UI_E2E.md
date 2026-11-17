# Session QR Mobile E2E — 2026-11-17

Result: **58 PASS groups, 0 FAIL, 3 BLOCKED**, runner exit 0. Isolated PostgreSQL cluster **7709090157328588841**. Evidence: `CASH_PRECHECK_E2E_EVIDENCE.json` (latest run, `operatingContainersUnchanged=true`).

## Changes

- Test harness only: `scripts/tests/session-qr-ui-flow.mjs` and an optional branch in `session-billing-stack-flow.mjs`.
- Opt in with process environment `AICOS_QR_UI_TEST=true`, `AICOS_MOBILE_WORKSPACE=<Mobile path>`, then run `node scripts/tests/session-billing-stack.mjs --precheck --require-shift`.
- The Flutter live test creates the existing B05 fixture QR from its UI. A temporary marker carries only fixture bill/reference/amount. The Node fixture retains the Mock signing secret and sends the signed callbacks; no webhook secret goes to Mobile.
- Existing fixture amounts are unchanged. No new service, endpoint, schema or production feature is introduced.

## Verified

Flutter QR selection -> one PENDING attempt -> Payments restart -> signed provider callback -> read/poll SUCCEEDED proof -> PAID UI -> return to aggregate Bill. No Cash request or automatic session close. One attempt and one provider receipt persist despite duplicate callbacks. Existing Cash/precheck/shift/Serving/close concurrency and Dashboard/Reporting tests remain PASS, including offline/recovery and backup/restore.

Mock is not a real bank integration. Protected roles, process credentials, SQL guards and ephemeral database/network rules remain those of the existing runner. All preexisting container IDs/images/start times/status/restarts were unchanged. The new test stack was removed; no operating volume was touched.

## Remaining blockers

- Gross/net/refund/Manager allocation policy.
- Real Identity account with combined SERVER+CASHIER (current request stores one canonical role).
- Global branch Bill-count contract.
- New Takeaway QR admission/recovery contract and real bank QR remain outside this test; Mobile stays disabled for that path.

The preceding QR attempt (cluster 7708830465671856169) recorded 36 PASS/1 FAIL in the Flutter assertion stage after provider callback. The live harness now waits for repository loading and UI frames and scrolls lazy controls before tapping. This fresh successful run is the recovery evidence; a prior callback-only PASS was not reported as UI PASS. Retained stopped resources from the old disposable run were cleaned with label/network/no-volume guards before this run.

No production migration, deployment, credentials change, commit or push was performed.
## Git publication review — 2026-11-17

User requested commit/push of completed Backend work. Scope is these four test/evidence files only; Item Transfer V2 in the separate migration-review worktree and temporary export directories are excluded. Node syntax checks for both runners and git diff whitespace validation PASS. Existing isolated evidence is 58 PASS/3 BLOCKED; the stack was not rerun for this Git-only step. No service, schema, database, credential or runtime flag change.