# Dashboard — reconcile upstream main, 2026-11-16

## Source and scope

Merged origin/main e10c499 into review branch be/cashier-dashboard-main-sync (base 98c2783), in an isolated Git worktree. Git found no conflicts. Original AICOS-BE dirty V2/runtime/Barista changes and Mobile V2 changes are preserved, not included in this Backend merge. Backend main is not overwritten or force-pushed.

No operating database migration/GRANT, feature activation, runtime image deployment, credential/JWT change, financial policy change or physical printer integration.

## Completed changes

- Updated real-HTTP test tenant fixture for upstream AwaitingPayment: verifies login is initially 403, then uses existing SuperAdmin plan assignment/payment APIs with synthetic test data before owner login. No fake JWT or production account.
- Billing.StackTools exports columns from exact EF migration TargetModel instead of the moving current model when reproducing the old frozen Dashboard package. Upstream supplementary targets combine baseline and new model columns because RefundToBankAccount was authored without later Dashboard tables in its snapshot; actual PostgreSQL schema is asserted rather than trusting that snapshot alone.
- Separate guarded upstream SQL proposals for five migrations; original SQL/checksums unchanged. Read-only audit pins the operating cluster/history, found zero tenant login transitions. No data or secrets exported to Git.
- Mobile maps tenant.awaiting_payment / tenant.deleted to useful Vietnamese messages and stays logged out; tests use actual Backend errorCode shape. No UI redesign.

## Actual verification

| Check | Result | Limits |
|---|---|---|
| .NET solution | 850 PASS, 51 SKIP, 0 FAIL | BuildingBlocks 15, Identity 206, Services 579/51, Smoke 50; host tests do not replace PostgreSQL E2E |
| Fresh full-stack Dashboard + Cashier enforcement | 54 groups PASS, 3 BLOCKED | Real PostgreSQL/Redis, Gateway and Flutter HTTP, cluster 7708604138493382697; Mock QR only |
| Mobile targeted Identity + Dashboard | 25 PASS | HTTP mock/widget tests, separate from E2E |
| Mobile full regression / analyzer | 362 PASS, 7 SKIP / 0 issues | Demo/API fixture tests; live HTTP counted separately |
| Upstream migration/restore rehearsal | 8 groups PASS, 0 FAIL | Fresh synthetic empty baseline; zero operating connections |
| Full six-DB backup/restore (follow-up) | 9/9 PASS | Includes Identity/Workforce; see CASHIER_DASHBOARD_SIX_DB_BACKUP_REVIEW.md |
| Operating migration (follow-up) | 7/7 SQL PASS | See CASHIER_DASHBOARD_SEVEN_SQL_EXECUTION.md |
| Operating ACL/image/feature rollout | NOT RUN | Requires separate approval; Payments GRANT history guard needs revision |

E2E covers Cash vs shift-close, concurrent Cashiers/AddOrder, missing/closed-shift refusal, retry/lost ACK/restarts, confirmed ledger-to-Reporting totals, recovery while Reporting offline, real Flutter OPEN/CLOSE/Cash forms and Dashboard at 360/393/430px. Final Cash 210000 VND, QR 70000 VND, total 280000 VND; QR remains branch-only, excluded from Cash drawer. Existing container IDs/images/start/status/restart counts unchanged. Test containers removed.

Three E2E blockers remain: gross/net/refund allocation policy; actual Identity combined SERVER+CASHIER issuance (canonical single-role source); global branch all/paid/pending Bill count contract. No fabricated data substitutes.

## Operational status and next gate

Dashboard code is validated on the merged test stack, **not enabled in the operating local stack**. Existing local containers remain stopped; Gateway is not running. A brief read-only audit started only existing PostgreSQL and returned it to stopped; no application started. Read-only audit/result and E2E snapshots are different checkpoints, not claims of continuous uptime.

Five extra upstream migrations mean the old four-DB approval package alone is insufficient. Fresh full backup/restore including Identity/Workforce is now 9/9 PASS; see `CASHIER_DASHBOARD_SIX_DB_BACKUP_REVIEW.md`. Review the upstream supplement alongside the old proposal, then approve exact SQL/ACL stages separately, then runtime and feature activation. Recheck backup freshness and history immediately before any approved execution.

Workforce cash handover (cash_closings/payroll) and Payments cashier drawer remain distinct domains. This merge does not infer that their events, opening balances or QR attribution should be combined. No changes to financial allocation without policy approval. New refund bank fields remain nullable and additive.

## Approved migration execution follow-up

The seven approved SQL files have now committed and verified PASS on Docker local: 49 -> 64 tables, 626 -> 807 columns; old data preserved, new business tables empty, Identity UPDATE 0. See [CASHIER_DASHBOARD_SEVEN_SQL_EXECUTION.md](CASHIER_DASHBOARD_SEVEN_SQL_EXECUTION.md). Earlier NOT RUN/pre-approval notes are historical for migration only. GRANT/runtime/features remain unexecuted; Payments GRANT history guard must be revised and rehearsed before its separate approval.


## Post-migration permissions review

See [CASHIER_DASHBOARD_PERMISSIONS_REVIEW.md](CASHIER_DASHBOARD_PERMISSIONS_REVIEW.md): revised Payments history-only guard, isolated PostgreSQL ACL 26 PASS / 1 N/A. Local GRANT remains unexecuted; recommend Ordering/Payments/Reporting only, skip Kitchen already sufficient. The original Payments GRANT must not be used unchanged.


## Approved three-GRANT execution follow-up

Ordering, revised Payments and Reporting GRANT files have now committed and verified 3/3 PASS. Kitchen skipped; 15 new tables accessible, 58 UPDATE columns checked, six DB data/schema/history and roles unchanged. PostgreSQL returned to stopped; no application/feature rollout. Earlier NOT APPLIED notes describe the preparation checkpoint. See [CASHIER_DASHBOARD_GRANTS_EXECUTION.md](CASHIER_DASHBOARD_GRANTS_EXECUTION.md).


## Runtime preparation follow-up

See [CASHIER_DASHBOARD_RUNTIME_READINESS.md](CASHIER_DASHBOARD_RUNTIME_READINESS.md): 11/11 candidate image builds PASS, nine actual PostgreSQL role logins PASS, Backend 856 PASS / 51 SKIP. Operational expiry switch defaults true; smoke overlay disables expiry and all financial/recovery workers/features. No runtime deployment/activation performed. Two historic payments lack collection receipts; financial keys/reconciliation need separate review.


## Approved smoke attempt: schema blocker

Infrastructure healthy, applications 0/11 NOT RUN: full model preflight found pending Menu ProductPictureUpload and Customer HuntVouchers. See [CASHIER_DASHBOARD_RUNTIME_SMOKE_EXECUTION.md](CASHIER_DASHBOARD_RUNTIME_SMOKE_EXECUTION.md). Separate guarded compatibility proposals tested 13/13 PASS; eight-DB full restore 11/11 PASS. No local extra migration/GRANT. Runtime approval remains valid after the separately reviewed schema/ACL blockers are resolved.

## Runtime gate completed — 2026-11-16

Separate Menu/Customer expansion and Customer GRANT approval executed: 3/3 PASS, legacy data unchanged, backfill 0. Features-OFF runtime smoke: 11/11 apps PASS, existing PostgreSQL/Redis healthy; 10 downstream metadata/DNS/TCP checks and 9 protected GET auth checks PASS. All ten DB schema/ACL/data/history baselines unchanged during smoke. See CASHIER_DASHBOARD_RUNTIME_SMOKE_EXECUTION.md and RUNTIME_COMPATIBILITY_EXECUTION.md. This supersedes the earlier schema-blocked attempt. Financial activation/history reconciliation and valid-token business/Mobile E2E remain OFF/NOT TESTED; no implied approval.

## Gate A executed — 2026-11-16

User separately approved configuration only. Payments/Reporting CashierShifts enabled with one separate DPAPI-stored machine key; dispatcher OFF and optional shift policy preserved. Both service recreations/health/routes/401 PASS, all other containers and DB schema/data/ACL unchanged; outbox/facts/shifts remain zero, legacy Cash still 258.000đ. See CASHIER_DASHBOARD_GATE_A_EXECUTION.md and CASHIER_DASHBOARD_GATE_A_RESULT.json. Historical recovery, authenticated Mobile tests and SessionBilling remain unapproved/not run. Earlier preparation/flags-absent sections are historical evidence, not current config.
