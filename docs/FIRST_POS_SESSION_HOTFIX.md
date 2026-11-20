# First POS order / empty table — HTTP 409 hotfix

## Approved local runtime result — 19/11/2026

User approved replacing only local Ordering after the initial source/test preparation. The previously documented no-deploy boundary below describes the earlier gate; this approval does not authorize other services, migrations, financial activation or the merged main candidate.

- **Runtime PASS**: new container `df05d9c1e342fade36e4bebe4b94cfe6407f698204ddc32b8c8ed8da7d7a1653`, started `2026-11-19T16:24:54.499551321Z`, runs exact hotfix image `sha256:4938a60a4ee23039f9a7ef901a1f3d0065d831eeb8053d440192c4ba65a38922`.
- Direct `/health/live`, `/health/ready`, Gateway `/api/v1/orders/_info` → **200**; protected capabilities GET without JWT and with invalid JWT → **401**, both direct and through Gateway. No login, JWT generation or business POST for this verification.
- Runtime environment (including credentials/JWT), ports, network and original labels preserved. Docker inherits one extra image label `aicos.hotfix=first-pos-session`; no other added label accepted. `Database__MigrateOnStartup=false`, expiry/admission/transfer workers and other feature flags unchanged.
- Old Ordering container/image retained stopped as `aicos-main-order-before-pos-hotfix-818fff49`. No other container lifecycle changes during rollout, verified through Docker events. No automatic rollback/removal of retained container.
- USB helper `run-local.ps1 -PrepareOnly` reports Android HTTP **PASS** for phone `506e58f8`; user may retry their preserved cart. Actual authenticated order submission from the phone remains **NOT RUN by the agent**.
- Initial post-start verifier returned FAIL because it required exact equality of all container labels and rejected Docker's inherited `aicos.hotfix` label. Application startup and health were successful. Corrected verifier checks every prior label and explicitly permits only that known extra label; resumed verification was **read-only**, no second recreate or restart.
- Initial pre-start row fingerprint was computed but not persisted before that assertion failed. Therefore before/after row equality is **NOT VERIFIED**, not PASS. Current fingerprint of all 13 Ordering tables was recorded; no migration/seed/SQL write or business API write was executed by this rollout. See `FIRST_POS_SESSION_RUNTIME_RESULT.json` for evidence.
- Script: `scripts/Apply-FirstPosSessionHotfix.mjs`; `--check` is read-only preflight; the approved write mode is pinned to the prior container/image/cluster and cannot be rerun after this replacement. `--verify-existing-hotfix` performs read-only verification only.

## Evidence and root cause

19/11/2026: local Gateway and Ordering logs correlate repeated `POST /api/v1/orders/` → HTTP 409 with `SessionBillingWriteGuard.CheckAsync`: `The table session closed or changed` (`qr.session_changed`). This request reached Ordering, not a USB/network or Kitchen admission failure. No user order was retried by this investigation.

Runtime Ordering image `sha256:1eb134f5d0c1f9fb49088cf51c6dd9dddb1f3bf06b4905d16662c3a88d60d503` is labelled revision `396b7e8596bb1d993272d6fe7c0620b6c62db7c4`. SessionBilling is enabled, startup migrations and Kitchen admission capture remain disabled. Runtime containers have not been replaced by this fix.

`CounterOrderRequests.ResolveSessionAsync` adds a new TableSession to EF's tracker for an empty verified table. `OrderEndpoints.CreateAsync` attaches the first Order to it, then saves both atomically. Before that save, `SessionBillingWriteGuard` queried only persisted sessions with AsNoTracking. The legitimate new session is not in PostgreSQL yet, so the guard incorrectly raised 409 and the transaction rolled back. Tests previously opened sessions via QR scan before adding a round, concealing this POS-first path.

## Minimal fix, no migration

Only for an Added Order, accept its matching Added TableSession from this DbContext: exact session ID, tenant, branch and table, still OPEN. For every existing session keep the persisted re-read under the existing PostgreSQL table lock. Retain checkout/payment fences, closed-session validation and cancellation guards. No endpoint, DTO, schema, financial ledger, flags, credentials or public Web contract changes.

Files:

- `src/Services/Ordering/AICOS.Ordering.Api/Features/SessionBilling/SessionBillingWriteGuard.cs`
- `tests/AICOS.Services.IntegrationTests/FirstTableOrderTests.cs`: POS first session + replay + add round; closed/missing/wrong-table/wrong-branch rejection.
- `tests/AICOS.Services.IntegrationTests/SessionBillPostgresTests.cs`: first POS order on a new table, eight identical concurrent requests, one session and one Order persisted.

## Validation

- Before fix, new HTTP test FAIL reproduced; four negative cases PASS.
- Integration candidate after fix: **38 PASS/0 SKIP**, FirstTableOrderTests + SessionBillFlowTests + MobileCounterTests + SafeOrderCancellationTests. EF InMemory and service/auth doubles.
- Exact runtime-base hotfix: **29 PASS/0 SKIP**, same cases excluding cancellation features absent in that revision. Separate branch `fix/first-pos-session-20261119`, worktree `%TEMP%/aicos-first-order-hotfix-20261119`. The only application-source diff versus runtime revision is seven added lines/one replaced line in the write guard; it does not import new main migrations/workers.
- PostgreSQL isolated regression: **23 PASS/0 SKIP**, including the new first-POS/eight-parallel-replay case. Guarded scratch container `aicos-billflow-postgres-fac77c0307`, cluster `7709916023582572579`, random loopback 51216, database `aicos_billflow_test`, private network/tmpfs. Scratch container/network removed by the guarded runner. Auth/Menu/HTTP service bridges remain doubles; this is not a real phone/Gateway/Redis E2E. No operating DB accessed by tests.
- Docker hotfix build **PASS**: `aicos/order:first-pos-session-20261119`, manifest-list image `sha256:4938a60a4ee23039f9a7ef901a1f3d0065d831eeb8053d440192c4ba65a38922`. Built from the exact runtime-base worktree, no app startup performed. No migrations or models were changed, no new feature enabled.

## Runtime boundary

Source is fixed; current local container still runs the old guard. Existing no-deploy restriction remains: obtain approval to replace **only Ordering** with the exact-runtime-base hotfix image before asking the user to retry. Do not deploy the combined main candidate: it has seven independently pending migrations. Preserve all runtime environment, network aliases/ports, credentials and feature flags, especially MigrateOnStartup=false. Do not send a real Order/payment for smoke testing.

Before an approved replacement capture container/image identity and other-container snapshots; retain the previous stopped Ordering container/image. Verify live/ready, Gateway routing and unauthorized requests read-only; do not alter schema/data or start other services. If startup fails, stop and report; restore old image only with an explicit, reviewed recovery action. New image rollback requires no schema rollback.

Separately observed Bill GET 409 reports `Legacy payments require reconciliation before opening a session bill`; this is a different guard and is not bypassed by this hotfix.
