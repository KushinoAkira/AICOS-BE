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
| Earlier four-DB full restore | 7/7 PASS | Does not cover Identity/Workforce |
| Operating migration/ACL/image/feature rollout | NOT RUN | Needs approval |

E2E covers Cash vs shift-close, concurrent Cashiers/AddOrder, missing/closed-shift refusal, retry/lost ACK/restarts, confirmed ledger-to-Reporting totals, recovery while Reporting offline, real Flutter OPEN/CLOSE/Cash forms and Dashboard at 360/393/430px. Final Cash 210000 VND, QR 70000 VND, total 280000 VND; QR remains branch-only, excluded from Cash drawer. Existing container IDs/images/start/status/restart counts unchanged. Test containers removed.

Three E2E blockers remain: gross/net/refund allocation policy; actual Identity combined SERVER+CASHIER issuance (canonical single-role source); global branch all/paid/pending Bill count contract. No fabricated data substitutes.

## Operational status and next gate

Dashboard code is validated on the merged test stack, **not enabled in the operating local stack**. Existing local containers remain stopped; Gateway is not running. A brief read-only audit started only existing PostgreSQL and returned it to stopped; no application started. Read-only audit/result and E2E snapshots are different checkpoints, not claims of continuous uptime.

Five extra upstream migrations mean the old four-DB approval package alone is insufficient. Review `proposals/cashier-dashboard-upstream/README.md` alongside the old proposal; approve fresh full backups including Identity/Workforce and restore drill first. Then approve exact SQL/ACL stages separately, then runtime and feature activation.

Workforce cash handover (cash_closings/payroll) and Payments cashier drawer remain distinct domains. This merge does not infer that their events, opening balances or QR attribution should be combined. No changes to financial allocation without policy approval. New refund bank fields remain nullable and additive.