# Table operations implementation

This change continues the existing Ordering-owned session, Payments-owned Bill and Kitchen ticket architecture. No operating migration, deployment, feature enablement, commit or push is authorized or performed.

## Scope and status

| Function | Implemented scope | Remaining limit |
| --- | --- | --- |
| Table Transfer | Durable local session/order move, scoped audit/outbox, idempotent request, same Bill identity, asynchronous Kitchen routing, explicit pending status, Mobile navigation/history | WholeOrder only; CupV1 rejected. Web client 202 compatibility not verified. Requires additive migration and coordinated flags. |
| Item Transfer | Source-based real-transfer proposal in ITEM_TRANSFER_DESIGN_FOR_APPROVAL.md | BLOCKED by user approval; no draft CRUD substitute, no real item/money transfer API. |
| Split Bill | Versioned draft allocation plan, exact item quantities across groups, merge, audit, stale quote handling, one original logical Bill | Group payment/discount/tax/fee/rounding contract BLOCKED; amountDue=null and paymentAvailable=false. No second Bill. |
| Merge groups | Atomic full-plan replacement with key/version; quantity-loss deletion/merge rejected | Only editable draft groups under one original Bill. |

Existing Demo flows are preserved. New API capabilities/flags fail closed instead of silently using Demo data or fake transfers.

## Backend files and API

Ordering: Features/Qr/TableTransferOperation.cs, DurableTableTransferService.cs, TableTransferDispatcher.cs, TableTransferHistoryEndpoints.cs, existing TableTransferEndpoints.cs; OrderingDbContext and Program. Table transfer remains POST /api/v1/orders/qr/sessions/{id}/transfer with Idempotency-Key and expectedTableId. Durable flag ON returns 202/PENDING_SYNC or 200/COMPLETED with operationId. GET /api/v1/orders/qr/transfers/{id}, /qr/sessions/{id}/transfers and /qr/transfers?branchId=&tableId= return scoped status/history. CounterRoles only; Barista denied. Feature OFF preserves legacy handler/API.

Ordering transaction locks the session and sorted source/target table keys, validates branch/occupancy/open session/payment checkout state, persists the original session move and immutable actor/source/target audit together. Kitchen HTTP is outside that transaction. Ambiguous responses retain the original key/body in Mobile. PENDING_SYNC is a committed local move awaiting Kitchen, NOT an editable/cancelable draft. History is append-only; a subsequent authorized move is compensating work, not history deletion.

Kitchen: Features/TableTransferReceiver.cs; KitchenTicket.TableTransferVersion and ApplyTableTransfer; KitchenTableTransferHandler; Program. POST /internal/table-transfers uses separate machine-key authentication, exact operation/version ACK and own transaction. Missing ticket returns pending, replay/older route version cannot reset a newer route. Active flag ignores legacy Redis table labels to prevent stale overwrite. It updates table label/version only, not ticket/item/sticker/preparation/claim state. CupV1 is blocked by the shared WholeOrder guard.

Payments: Features/SplitBilling/SplitPlan.cs and SplitPlanEndpoints.cs; PaymentsDbContext and Program. GET/PUT /api/v1/payments/bills/{billId}/split-plan (GET includes sessionId query; PUT includes sessionId/version/fingerprint/groups and key), GET /split-plan/history?sessionId=. Scope must match the Backend logical Bill/session. PUT is atomic under existing Bill lock, checks PAID/CLOSED/pending/reconciliation, versions, exact quote fingerprint and per-item quantity sums. Draft plan does not materialize another session_bills row or collect money. Deletion cannot lose already allocated quantities. Stale drafts retain their snapshot; explicit reset returns all quantities to unallocated before re-editing current quote.

BillLine.OrderItemId is optional/additive in Contracts/Billing/SessionBilling.cs and populated by SessionBillQuotes. Old frozen snapshots without identities are read-only for allocation. The new split response includes current TableId/TableCode; monetary subtotals exclude unapproved discount/tax/fee distribution. Groups stay DRAFT even when the root is paid and read-only; they do not fabricate group payment history.

## Persistence / configuration

Three NEW additive migrations (do not rewrite previously finalized ones):
- Ordering 20261114164841_DurableTableTransfer: one journal/outbox table.
- Kitchen 20261114164849_TableTransferRoutingVersion: one default-zero bigint column.
- Payments 20261114164856_BillSplitPlans: plan + operation audit tables.

SQL, target/cluster guards, narrow GRANT proposal, empty/unused-only rollback, hashes and stage order: docs/migrations/table-transfer-split/README.md. No cross-service FK or direct cross-service DB access. Down refuses persisted history/used routing. Backup/restore and rollback tests use independent disposable copies. Service roles never receive DELETE/TRUNCATE/ownership through this proposal.

Keep TableTransfer:Enabled=false, SplitBill:Enabled=false and MigrateOnStartup=false on operating environments. For a separately approved rollout, expand schema BEFORE code (Kitchen maps the new column even with flag OFF), inspect prior SessionBilling/Cashier migrations, review ACL, then coordinate Ordering/Kitchen flags and a new environment machine key. Do not enable only one side. Services:Kitchen:BaseUrl selects internal HTTP destination. ItemTransfer has no registered feature to enable.

## Mobile files

Repositories: main_table_transfer_repository.dart, main_split_bill_repository.dart; main_operations_repository.dart delegates to transfer repository. Shared request_id.dart generates secure request IDs. UI: main_split_bill_screen.dart, split_group_card.dart, split_group_items_sheet.dart, table_transfer_pending_dialog.dart, existing table_target_sheet.dart, main_table_action_handler.dart, main_table_detail_screen.dart and session_bill_detail_screen.dart.

Table Detail provides transfer/history and an explicitly blocked item-transfer control. Target sheet filters eligible tables by area. Pending dialog polls only on explicit user action, never announces success prematurely. Completed move refreshes target and navigates to it. Session Bill opens allocation screen with item quantities, rename/add/empty-delete/merge and save/retry. Group payment is disabled with a reason. New source changes do not alter Gradle or operating backend settings.

## Validation

See final validation results below and TABLE_TRANSFER_SPLIT_E2E_EVIDENCE.json. Regression transport tests use test hosts/mock clients and are not claimed as PostgreSQL tests. The --operations runner uses real separate PostgreSQL/Redis, service processes and Gateway with synthetic Identity fixtures only. It fingerprints preexisting containers and removes only resources created by that run.

Initial validation caught a UTF-8 BOM in EF SQL exports and a history snapshot object/array mismatch. Runner SQL normalization and history DTO parsing were corrected; a failed earlier run is not counted as a pass. Final rerun is required for the exact current source.

## Remaining blockers / rollout conditions

- Real Item Transfer: explicit policy/schema/protocol approval per separate proposal; no substitute draft workflow.
- CupV1 table transfer: immutable admission source vs mutable routing/labels and physical sticker workflow not approved; guard stays in place.
- Group collection: monetary allocation, rounding, partial/failed QR, refunds and paid-group lifecycle need a reviewed financial ledger contract.
- Web FE absent: NOT TESTED; old clients must support durable 202 and cannot bypass claim/transfer guards.
- No operating SQL, service deploy or feature switch in this task. Physical printing/bank QR are not tested by Mock payment scenarios.

## Final validation result

- Flutter full regression: 332 PASS, 2 SKIP; analyzer 0 issues. Final focused verification after table-label/error-copy changes: 14 PASS (including 6 new tests) and analyzer 0 issues. UI allocation exercised at 360/393/430px.
- Backend Services.IntegrationTests: 559 PASS, 51 SKIP after fixing snapshot parsing. Other solution suites: BuildingBlocks 15 PASS, Identity 203 PASS, smoke 50 PASS (827 PASS total across these suites; 51 SKIP). Skipped infrastructure tests were not silently treated as real PostgreSQL tests.
- Final real isolated --operations stack: 43 groups PASS, 2 BLOCKED, 0 FAIL; cluster 7708082595661221929. Verified reviewed SQL checksums, repeat execution, role denies, schema, independent backup/restore/empty rollback/reapply and used rollback refusal.
- Real Gateway flow covered split/merge/delete-loss guard, no second Bill, Kitchen offline, lost-response safe replay, Ordering/Kitchen restart, unchanged ticket identity/route update, two real actors racing one move (one winner/one409), audit/source/target and Barista denial. Existing Cash/QR Mock/shift/collection/serving/close regression flows also passed.
- Flutter real HTTP host tests: 2/2 PASS in the isolated runner for existing session-Bill/shift workflows. New transfer/split widgets are tested with transport fixtures; a real phone/tablet end-to-end transfer/split session is NOT RUN.
- All preexisting container ID/image/start/status/restart snapshots unchanged: true. Test-only container/network cleanup completed. No operating DB/schema/credential/feature/deployment change.
- BLOCKED groups are explicit in JSON: item transfer/CupV1/group-payment policy, and gross/net sales/refund/manager allocation policy. Mock QR is not bank integration.
