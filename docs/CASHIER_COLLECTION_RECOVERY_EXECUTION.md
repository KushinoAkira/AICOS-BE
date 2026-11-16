# Gate B — approved historical Cash recovery PASS

Date: 2026-11-16. User explicitly approved one execution for the exact two reviewed legacy Cash sources, **258.000đ**, pinned plan SHA256 **91bd27b47249a772c3071acb32f07ccbc1e8b4de22098b896ebf2dd825a858b9**. This was historical proof/outbox/inbox synchronization, not charging a customer or collecting money again.

## Dependency stop and recovery

Initial preflight stopped before a DB connection/write because all 13 existing main containers were stopped. Docker metadata showed exit 0, no OOM, finish times around **19:44 Asia/Saigon**. The recent event window contained no evidence of who/what stopped them; do not infer Docker Desktop restart or a specific compose command.

Only four required existing dependencies were started: PostgreSQL, Redis, Reporting, Payments. IDs/images/config were preserved, no recreate, new flags or keys. The remaining nine containers kept their stopped state. A host Node health waiter exited early after Reporting had already become healthy; a direct GET confirmed 200, a referenced keep-alive timer fixed the waiter, and continuation skipped already running dependencies. This was not a service failure or a financial execution/retry. No receipt was written during either initial preflight stop or waiter issue.

See CASHIER_COLLECTION_RECOVERY_DEPENDENCIES.json. Existing-container preservation below refers to the financial operation after these necessary starts; the starts themselves are disclosed here.

## Fresh preflight and backup

- Exact Docker PostgreSQL c014190eed2d / cluster **7703616372368646182**, never Windows PostgreSQL.
- Private immutable plan hash matched. Both Payment proofs retained original IDs/OrderId/cashier/reference/amount and full UTC microseconds; status Succeeded, no refund or aggregate Bill association. Fresh legacy contexts still OPEN/three Orders and CLOSED/Paid/one Order.
- CashierShifts ON on Payments/Reporting; dispatcher, RequireOpenForCash, SessionBilling, migration/seed/expiry and other reviewed worker flags OFF. Existing key match and source/target role permissions verified; no role/key change.
- No active writer, zero outbox/facts/shifts, exactly two missing eligible sources. Nine service DB schema/data/ACL/history/role/container baselines saved privately before backup/execution.
- Fresh full custom-format backups of **Payment and Reporting only**, restricted current-user/SYSTEM folder outside Git, SHA256 verified. Apps were not stopped for dump; clients were held inactive and writer checks plus identical before/after financial fingerprints verified the quiet backup window. Do not describe this as an offline all-service backup.
- Restore drill in a different internal/tmpfs/no-host-port PostgreSQL: **5/5 PASS**, full rows/fingerprints/columns/PK/FK/CHECK/indexes/history restored exactly. Backup read-only bind, only scratch container/network removed. No restore/migration/GRANT on the source.

## Actual command and results

The reviewed StackTools artifact hash was pinned and rechecked. Ran exactly one ephemeral Docker process on the existing verified shared network, with the current Payments image as a .NET runtime, overridden entrypoint, read-only published-tool mount and read-only single private plan file mount. Credentials/unchanged Gate A key were supplied in process environment only; DPAPI loader verified private ACL/key and cleared/restored its process environment afterward. No raw credentials/env/config or private source IDs were published.

Command mode: **recover-approved-collections /approved-plan.json**, explicit execution approval and exact plan SHA. Source connection authenticated **payment_svc** to aicos_payment; receiver was the existing internal Reporting endpoint using its existing role/config. No public Cash API, provider/bank API or app login/JWT was called.

| Evidence | Before | After |
| --- | --- | --- |
| payment.collection_outbox | 0 | **2**, exact original EventIds |
| reporting.collection_facts | 0 | **2**, exact matching payloads/EventIds |
| Committed delivery ACKs | 0 | **2** |
| Historical Cash sum | 0 in collection reporting | **258.000đ** |
| Cashier shift rows | 0 | **0** |
| ShiftId on both recovered receipts | N/A | **NULL** |
| Original Payment/Order/Bill/session data | Baseline | **Unchanged** |
| New payments/charges/provider calls | 0 | **0** |

208.000đ and 50.000đ retain their original November 4/6 paid dates, actors, references and microseconds. Source proof = durable outbox = committed receiver facts per EventId and whole amount, not merely aggregate totals. No synthetic session Bill, new Order, session close/reopen or current-drawer allocation.

Nine DB schemas/ACL/history and every non-ledger table fingerprint matched baseline. Role metadata and all pre-existing container IDs/images/state/start/restart metadata matched the post-dependency-start baseline. Only the two approved ledger tables gained rows. Waited another **12 seconds**: dispatcher still OFF, exactly two outbox/facts, no extra receipt or shift.

The operating runner was not replayed to prove idempotency and no automatic retry/rollback was used; repeat/crash/duplicate behavior was proved beforehand by **19 isolated PostgreSQL/real-HTTP groups**, not by creating more operating writes. The related **18 in-memory regression tests** are an earlier distinct checkpoint.

## Current runtime and limitations

Gate B **PASS** within the historical recovery scope. Four dependencies healthy; Gateway, Identity, Menu, Table, Ordering, Kitchen, Workforce, Customer and AI remain stopped as they were before this gate. Therefore Mobile cannot yet use the full Gateway flow. Do not call this a full-stack business/UI PASS or report all 13 containers healthy.

CashierShifts remains ON, normal dispatcher OFF, SessionBilling and reviewed workers OFF. Today's total is not increased by these old receipts; select the original historical range/branch to see them once authenticated Dashboard testing is separately authorized. Collection reporting is confirmed gross proof, not net sales and not added again to OrderFacts revenue. The OPEN legacy session remains a separate transition issue guarded by billing.legacy_payment.

Next scopes: controlled restoration of remaining runtime and authenticated read-only Mobile Dashboard validation; separate normal dispatcher/business-operation activation and SessionBilling policy/config readiness. No implied authorization for new payment/shift mutations, normal worker startup, real bank QR, refunds, V2 transfer or real printers from this Gate B result.

Private before/after financial snapshots, source IDs/full payload, backup manifests and encrypted key remain outside Git. Retain committed proof; never delete ledger rows, restore old DBs, run destructive Down or modify original money to undo a gate.

Evidence: CASHIER_COLLECTION_RECOVERY_EXECUTION_RESULT.json, CASHIER_COLLECTION_RECOVERY_BACKUP_RESTORE_RESULT.json, CASHIER_COLLECTION_RECOVERY_DEPENDENCIES.json. Private plan bytes/hash were not changed after approval.
