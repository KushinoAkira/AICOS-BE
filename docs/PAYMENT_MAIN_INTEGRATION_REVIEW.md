# Payment main integration — 19/11/2026

## Current checkpoint — 20/11/2026

The sections below retain the initial merge/sandbox review. PayOS Bill/Takeaway source, browser read-only routes and isolated tests are now implemented; local Payments alone has been updated with payOS OFF and unchanged fingerprints for 10 databases. See [current rollout/Git/cleanup evidence](PAYOS_LOCAL_RUNTIME_AND_GIT.md) and [payOS contract](PAYOS_STAFF_PAYMENTS.md). Backend full regression is now 1,030 PASS/69 SKIP. The user authorized committing/pushing this integration branch, not merging Backend main or activating money/tunnel/migrations.

## Follow-up: source sandbox integration completed, runtime unchanged

After the initial merge review below, a default-OFF ZaloPay sandbox Takeaway V2 adapter and Flutter integration were added. The V1 Mock API remains unchanged; the historical Mobile-impact table below describes the initial review, not the final V2 wiring. See [adapter contracts and final validation](TAKEAWAY_ZALOPAY_SANDBOX.md) and [read-only local schema audit](LOCAL_SCHEMA_REVIEW_20261119.md).

Final validation: Backend **993 PASS/66 SKIP**; isolated PostgreSQL **22 PASS/0 SKIP**; Flutter **427 PASS/9 SKIP**, final focused UI **16 PASS**, analyzer **0 issues**. Provider transports are doubles: no external ZaloPay/Gateway/phone E2E claim. Seven pending migrations were identified read-only; none applied, runtime unchanged. Candidate merge remains uncommitted; original WIP remains preserved.

## Git / preservation

- Reviewed starting branch: `be/cashier-dashboard-main-sync`, HEAD `6b8aff7`.
- Fetched main: `de7e011faa5331930bbb678b30e59b06654e86b5`; Payment PR #40 merged at `31ecd64` on 18/11/2026.
- Isolated candidate branch: `be/payment-main-integration-20261119`, worktree `%TEMP%/aicos-payment-main-integration-20261119`.
- `git merge --no-commit --no-ff origin/main` merged the committed histories without conflict. The uncommitted cancellation patch was then applied by three-way merge; its sole conflict was Ordering model snapshot.
- Snapshot resolution preserves both upstream Apriori entities/relations and local cancellation entities/relations. Only the **unapplied cancellation Designer TargetModel** was synchronized with this combined snapshot; its Up/Down migration commands were not changed. No committed migration source was rewritten.
- Original cancellation WIP: **50 file hashes unchanged** in `%TEMP%/aicos-dashboard-sync-49bd553c`. Export folders were excluded. Original `D:/ki9/sep490/Sep490/AICOS-BE`, its other WIP branch, Mobile code and runtime containers were not reset/rebased/recreated.
- **Merge is pending, not committed**: HEAD is still `6b8aff7`, MERGE_HEAD is `de7e011`. Candidate files/index contain both histories plus copied WIP. No push, merge to local/main, deployment or database mutation outside scratch. Do not delete this worktree or run `git clean/reset/merge --abort` without preserving its untracked files.

## Reused API / actual limits

| API / code | What is ready in source | Mobile impact |
|---|---|---|
| `POST /api/v1/payments/transactions` | Existing orderId payment; main adds payOS, MoMo test, ZaloPay sandbox gateways. Current pending-attempt/order locks and amount/branch checks preserved | Suitable for Takeaway, not individual Dine-in rounds. Not automatically wired by merging code |
| `GET /merchant-settings/available?branchId=...` | Existing branch method list, no credential values | Use this rather than only global enabled providers; backend creation still authorizes the actual payment |
| `GET /transactions?orderId=...` | Existing scoped payment history | Recovery must inspect existing obligation before create; never infer paid from redirect/QR |
| `GET /transactions/{id}` | Returns status, **may expire/save** via PaymentProcessor | Not a strictly read-only smoke endpoint |
| `POST /transactions/{id}/query` | Queries provider and can apply financial result | Explicit reconciliation, not a harmless GET/poll substitute |
| `payos/webhook`, `momo/ipn`, `zalopay/callback` and provider return routes | Existing provider verification + PaymentProcessor flow | Callback/return can mutate state; none called against operational runtime in this task |
| Session Bill endpoints `/bills?...sessionId`, `/{id}/payments/cash`, `/{id}/payments/qr` | Existing aggregate Cash + gated Mock QR preserved | Real provider callbacks target legacy order Payment rows, not BillAttempt rows. **ZaloPay must not be enabled for Bill by merely changing ProviderCode** |
| Staff Takeaway QR V1 `/staff/takeaway/{orderId}/qr` | Existing safe Mock-only contract unchanged | Current Flutter MainTakeawayQrRepository rejects non-Mock payloads intentionally; do not disguise a ZaloPay transaction as Mock |
| Cash precheck, shifts, discount and safe cancellation | Local features retained with their guards | No UI redesign or financial-policy changes |

No new Bill/ledger/Refund implementation was added. Main's existing AwaitingTransfer refund state is retained by the merge; its settlement/reconciliation treatment is not bypassed. Provider return URLs still refer to the Web FE result pages by default; these are not automatically a Flutter deep-link contract.

## Recommended next provider

Recommend **ZaloPay sandbox** first: source already has create, signed callback, query/return and provider transport tests. MoMo test remains available later; payOS defaults to `api-merchant.payos.vn`, so an enabled Sandbox-mode row alone must never be treated as proof that calls are non-monetary.

ZaloPay has three distinct modes in source:

1. `TestPage=true`: AICOS internal test-card page, no request to ZaloPay; not external sandbox evidence.
2. Network integration with `ApiUrl=https://sb-openapi.zalopay.vn`: actual external sandbox, requires proper test AppID/key1/key2 and a reachable callback.
3. Production API: not authorized here.

`Live` in the source means making HTTP provider calls; it is also inferred from configured AppID. It does not by itself mean production. Do not configure operational keys or expose a callback/tunnel automatically. Credentials must be supplied through an approved local secret mechanism, not chat/source/logs.

Official references: [sandbox setup](https://developer.zalopay.vn/v2/start/), [callback verification](https://docs.zalopay.vn/docs/developer-tools/knowledge-base/callback/), [query recovery](https://docs.zalopay.vn/docs/specs/order-query/).

## Schema / rollout blockers

- Upstream main brings `20261113093202_OrderBasketsAssociationRules` and its basket/rules schema, beyond Payment. Paid-order legacy event handling calls BasketUpsert; AprioriMineJob is registered and writes after startup delay. **Do not deploy the candidate against the old operational schema or treat MigrateOnStartup=false as disabling this worker.** Schema/ACL and startup-worker review are separate deployment gates.
- Existing local manual-discount and two cancellation migrations remain unapplied on operational databases by this task. Cancellation SQL preflight/expand now also requires the merged main Apriori baseline; it will not create that baseline implicitly.
- Ordering and Payments model checks report no pending changes after the merged snapshot. The new Designer TargetModel has Apriori too; original committed migration sources remain intact.
- QR for a session Bill is still blocked on a provider/BillAttempt callback-and-recovery contract. Keep using the aggregate bill, never pay each order to bypass it.
- Takeaway real-provider Mobile UI needs a provider-aware contract/checkout navigation and safe recovery; current Mock V1 was intentionally not broken or replaced in this merge task. Choosing the test provider does not authorize activating runtime or using production credentials.

## Verification

- `dotnet build AICOS.sln`: PASS, 0 errors; existing CS0628/CS8620 test warnings.
- `dotnet test AICOS.sln --no-build --no-restore`: **986 PASS, 64 SKIP**, 0 FAIL (BuildingBlocks 15, Identity 210, Services Integration 711/64 SKIP, Smoke InMemory 50).
- Added `PaymentMainCompatibilityTests`: unsupported ZaloPay on aggregate Bill returns 503, creates no BillAttempt and does not freeze the session. Focused provider/Takeaway/cancellation/compatibility tests: **67 PASS**, overlapping the broad suite except for this new test; do not add counts as distinct E2E evidence.
- Final combined-metadata/PostgreSQL runner: **20 PASS**, 0 SKIP. Fresh container `aicos-billflow-postgres-1f0445e968`, cluster `7709835691304419363`, random loopback 64901, private network/tmpfs; exact SQL/ACL/idempotent expand/rollback tests ran on databases only inside that cluster, then runner removed its own container/network. No Windows/main credentials reused.
- Provider tests use **fake HTTP provider transports**, fake Identity/JWT and InMemory/TestServer. PostgreSQL tests prove SQL and transaction behavior, not real bank/sandbox network, Redis delivery, Web FE or phone E2E.
- Local patch whitespace check passes. Whole staged merge has trailing whitespace warnings inherited from upstream `ai-models/docs/Customer_FAQ_AI_Rules_Coffee_Tea_Bakery.md`; preserve that unrelated main file, do not claim the whole upstream diff is whitespace-clean.
- Current Mobile code was not changed in this task. Fresh Cash/Takeaway QR/Session Bill contract widget tests: **32 PASS** (MockClient), analyzer **0 issues**, formatter unchanged. Previous full 424 PASS/9 SKIP is historical. Neither set is live-provider E2E evidence. Upstream Python/AI was not independently exercised in this Payment integration task.

## Safe next step

Review/commit the isolated candidate when requested, then implement the chosen **test-only Takeaway provider adapter** and test it without operating credentials. Real ZaloPay sandbox verification needs approved test credentials/callback infrastructure; aggregate Bill provider work needs a separate contract. Operational schema, migrations/ACL, runtime feature activation and deployment remain unapproved. No automatic provider switch or financial charge was performed.
