# CupV1 — blocker review before operating enablement

2026-11-07. Continue `CUP_SERVING_V2_IMPLEMENTATION.md`; existing Admission V2, custody core, snapshot transport and Flutter panel are retained. **PARTIAL. Production remains blocked.** No operating database, credentials, container, Payment/Bill, migration, feature flag or deployment changed. No commit/push.

## 1. Decision boundary

This increment implements isolated non-owner PostgreSQL role validation, SQL grant proposals and additional guard/concurrency assertions. Transfer, TTL/Manager transitions and session-close policy below are **proposals awaiting approval**, not working features. No new business schema or EF migration was generated/applied.

Public Order creation still selects WholeOrder; Cup mode in the current integration suite is an explicit database fixture. Therefore a true Staff Mobile Create Order → CupV1 E2E cannot yet exercise the production path. It is **BLOCKED / NOT RUN**, not a PASS inferred from fixtures or HTTP adapters. A test-only forced mode would not prove the missing production mode-selection policy.

The user confirmed Web FE has not been cloned. Backend contract/guard review is possible; Web client source/runtime validation is **NOT RUN**.

## 2. TTL, custody and Manager policy — proposal only

Current code: `CupTask.RequireClaim` rejects deadline expiry for Pickup/Serve/Release/Renew; `MarkOverdue` preserves live custody but has no worker wired; `CupServingEndpoints` requires explicit lease approval/configuration. A Manager can operate their own claim but cannot override another owner. None of this increment changes those rules.

| Situation | Recommended policy requiring approval |
| --- | --- |
| Reserved at Bar | Initial reservation TTL **120 seconds**; remind around **30 seconds** and retain explicit manual renewal for MVP. Automatic heartbeat needs its own activity/max-extension rules. Offline/background is not physical return |
| Pickup | Explicit physical confirmation; consider starting a distinct **300-second Carrying lease** at successful Pickup rather than retaining the reservation's remaining seconds |
| Renew | Same owner, live/unexpired claim, current cup version/cycle/claim/fence; no silent revival after expiry |
| Timeout/disconnect | Keep owner/claim/fence, reject stale/expired action and show overdue/review. Never return a cup to Available automatically |
| Shift end/logout | Does not release custody. Staff retries or a scoped Manager verifies the physical cup |
| Manager verified cup at Bar | Record reason/evidence/actor, end old claim as Overridden, fence old owner, preserve CupId/cycle/history. Another Staff explicitly claims it; do not silently assign a new courier |
| Manager verified customer received cup | Confirm delivery with evidence; preserve the actual courier identity and separately record Manager confirmation. Do not fabricate that the Manager physically served it |
| Location/custody unknown | Keep Review and original owner. No automatic Serve, release, reprepare, discard or financial refund |
| Carried cup returned to Bar | Separate physical return and quality decision; do not assume it is safe to reissue. Barista quality/remake policy needs approval |

Values above are proposed business values, **not runtime defaults**. Current tests' 120-second lease remains only a fixture. Automatic renewal cadence, maximum extensions, Manager evidence requirements and system audit identity need an explicit decision before implementation.

Proposed new command: `POST /api/v1/kitchen/cups/{cupId}/manager-resolution`, Manager/BrandAdmin in the same tenant/branch; body includes resolution kind, expected version/cycle, claim/fence, reason and evidence reference. Actor comes from JWT, never body. Idempotency-Key required. Replayed effect returns current state; changed payload or concurrent owner/Manager mutation returns 409. No force flag bypasses version/custody checks.

Reuse `cup_claims` history and `cup_operations` for human Manager effects. The ledger currently requires a real ActorId and version_after > version_before; a worker must not invent a Staff ID or write a zero-effect update to that ledger. For MVP, deadline enforcement can stay at command time without a persisted timeout worker. A later worker needs an approved service-principal/audit model and versioned summary; any new column/table goes through separate schema review.

Acceptance after approval: owner vs Manager race, late original response, repeat resolution with same/different key, verified return vs unknown location, expiry during Pickup, Manager role from another branch, app kill after physical handoff, and no financial side effects.

## 3. Table Transfer — why the existing guard stays

Source findings:

- `TableTransferEndpoints.TransferAsync` locks source/target tables, rejects settled/completed rounds, then calls `RequireWholeOrderOperation` for every active order **before** changing any order/session.
- `CupManifest` hashes original TableId/SessionId. Rewriting it after a move invalidates receipts and the Ordering fulfillment manifest proof.
- `TableTransferredIntegrationEvent` has no monotonic location version, expected previous location or durable transfer ACK. Redis publication happens after commit and can be lost.
- Kitchen's legacy transfer handler routes through the shared writer: active CupV1 becomes recovery-held rather than being moved. It is not a successful Cup transfer.
- Cup commands currently check cup version/cycle/claim/fence, but not the location the Staff physically saw. A delayed Serve after a move could target the previous table even with a valid claim.

Removing the guard or only calling `TransferTable(newCode)` is unsafe. The new ACL test proves the current transfer returns 409 and retains the session table, CupIds, live claims and serving progress. This is a **PASS for refusal**, **BLOCKED for successful transfer**.

### Proposed minimal architecture

Ordering owns current session location; Kitchen owns each cup and custody. Keep SessionId, OrderId, OrderItemId, CupId, ClaimId, owner, preparation cycle and serving state. Keep original admission manifest immutable. Store current location separately; never reprint stickers automatically after transfer.

Proposed API: `POST /api/v1/orders/cup-sessions/{sessionId}/transfers`, Idempotency-Key; expected source table/session location version and target QR reference. Return 202 with TransferId and state; GET transfer status reports the committed location. The legacy WholeOrder transfer endpoint/response stays unchanged and continues to reject CupV1.

1. Under deterministic table/session locks, Ordering validates tenant/branch, open session, empty compatible target, expected location version and current settlement restrictions. It records a durable transfer intent. Add-more/another transfer/close must check pending intent under the same session protocol.
2. Kitchen machine Prepare verifies the exact admitted order set, locks tickets in a deterministic order and persists a dedicated transfer hold + receipt. Pickup/Serve and cup-prep writes are blocked while held. Custody is retained. The hold is separate from recovery review.
3. Ordering commits destination TableId/code and increments session location version in the same transaction as its workflow state/outbox progress. No network call inside that transaction.
4. Kitchen machine Commit validates transfer ID/hash/expected version, applies current location to all affected tickets atomically, preserves cups/claims/states and clears **only that transfer's hold**. ACK follows commit. Duplicate phase requests return the same result; old/out-of-order location versions cannot overwrite a newer move.
5. Durable retry/reconciliation completes lost Prepare/Commit ACKs after process/Redis restarts. An uncertain workflow stays held/pending for review; it does not auto-abort or release custody.

Proposed schema, **outside the approved 4+2 foundation and NOT APPLIED**:

| Owner | Additive change / key rules |
| --- | --- |
| Ordering `table_sessions` | `location_version bigint NOT NULL DEFAULT 0`, nonnegative |
| Ordering new `session_location_transfers` | transfer UUID PK; tenant/branch/session; requester/key/request SHA256; source/target table/code; expected/target versions; captured order set; state/attempts/lease/fence/due times and commit/ACK times. Unique tenant/requester/key and tenant/session/target version; partial unique pending target per tenant/table; local FK to session only |
| Kitchen `kitchen_tickets` | `current_table_id uuid NULL`, `location_version bigint NOT NULL DEFAULT 0`, `location_hold_transfer_id uuid NULL`. Origin SourceTableId/SourceSessionId remain immutable |
| Kitchen new `session_location_receipts` | transfer UUID PK, scoped session/order-set payload SHA256, source/target versions, target location, Prepared/Applied/NeedsReview and timestamps. Ticket hold FK local to this table; no Ordering FK |

These two tables are a proposed workflow outbox/inbox, not a third operating database/service. Reusing admission's single unique row per order/consumer would overwrite transfer history; encoding versions into consumer names would misuse its checkpoint model. Fulfillment's current fixed snapshot protocol also cannot silently become a transfer transport.

Contracts additionally need location version/proof in mutable source facts, Cup responses and Pickup/Serve expectations. Admission replays must accept a destination only when a matching committed transfer proof exists. New orders admitted after earlier moves must initialize the correct session location version. All receivers need the version-aware implementation before a writer is enabled. Original creation hash, CupId and sticker identity remain stable.

Add-more must check pending target/source reservations under the same locks; otherwise two transfers or a new session could occupy the target between workflow stages. Capture every session order that can retain custody, including financially void orders awaiting physical resolution. Prepare must hold the exact order set atomically. Table-service availability can change independently of Ordering: conditional availability/reservation contracts need review before claiming these two proposed tables alone are sufficient. Any extra schema that review reveals requires another approval, not an automatic implementation.

**Decisions before code:** initially allow only Unpaid same-branch empty-target moves with no live claim/review (conservative MVP), or allow Reserved/Carrying with owner acknowledgement and pending holds. Supporting live custody requires an approved physical target-confirmation and expiry-during-transfer policy. Already-Served cups retain their delivery-location audit; moving the session must not rewrite delivery history. No paid transfer, merge or bill regrouping is implied.

Rollback proposal: retain additive schema and workflow history; disable new transfer requests, reconcile/drain existing pending ones. Never reset location version, rewrite the original manifest, auto-clear transfer/recovery holds, delete receipts or recreate claims. SQL/EF migration should be produced only after this design is approved.

## 4. PostgreSQL permissions — implemented test work

New `-Run -CupAcl` mode creates a separate guarded tmpfs PostgreSQL/Redis cluster, reuses checksum/preflight/schema expansion and creates fresh NOLOGIN `kitchen_svc`/`ordering_svc` with no object ownership, superuser, database/role creation, bypass-RLS or replication. It applies reviewed proposals, sets random per-role credentials in memory and then enables LOGIN. Password/statement/context logging is suppressed for credential setup; no password is stored in source/docs/logs or operating credentials.

The service EF contexts actually connect through those role logins; tests assert current_user, not merely SET ROLE under a superuser. Other-service database connections are denied. Ledger SELECT/INSERT succeeds; UPDATE/DELETE/TRUNCATE rights are absent and actual no-row UPDATE/DELETE attempts fail with SQLSTATE 42501. Schema CREATE is absent. The HTTP flow commits ten physical-cup operations, two claims/two stickers and a complete unpaid projection through those permissions.

- Existing six-table grant proposal: `docs/proposals/cup-serving-runtime-acl.sql`, now tested only on the disposable target.
- Added prerequisite proposal: `docs/proposals/cup-serving-service-prerequisites.sql` for existing ticket/admission/Order/session/item/option tables. No role/password/ownership/PUBLIC/DELETE changes in these proposals.
- Generated actual matrix: `docs/CUP_SERVING_ACL_PERMISSION_MATRIX.md`; execution identity: `docs/CUP_SERVING_ACL_RUN.md`.
- Test-only fixture revokes PUBLIC database rights on its two new DBs to prove connection isolation. No PUBLIC rights were changed on operating DBs.

**Operating blocker:** `infra/postgres/init/01-schemas-and-roles.sh` makes runtime roles DB/schema owners. A GRANT/REVOKE matrix alone cannot enforce least privilege or append-only ledgers for owners; the proposals deliberately refuse that ownership state. Separate migration-owner/runtime-role configuration and effective inherited/PUBLIC ACLs require DBA review and explicit operating approval. This increment does not rotate credentials or change ownership.

**Web permission gap:** existing `DELETE /api/v1/orders/pickup-slots/{id}` calls `db.PickupSlotSettings.Remove`. The proposals intentionally grant no DELETE. Cup workflow rights PASS does not mean every legacy configuration endpoint has permissions. Decide whether to approve DELETE solely on `ordering.pickup_slot_settings`, or separately change the configuration workflow. No broad DELETE grant or endpoint behavior change was made. QR cart DELETE uses Redis, not this ledger ACL.

## 5. Dine-in early-close design — no Payment/Bill edits

Source `QrOrderEndpoints.cs` has three relevant paths: staff CloseSessionAsync checks settlement only; CloseIfSettledAsync closes when active rounds are paid/refunded; expiry closes financially settled idle sessions. `PaymentSucceededHandler` and several cancellation/exception paths call the helper. These paths neither require served-cup proof nor share a complete session lock protocol with add-more.

Recommended gate, awaiting approval: close only when **financial settlement AND complete operational disposition** are proven for the exact session order set. Bill PAID stays independent of physical serving. Every live claim, Review, transfer hold, unresolved ticket hold or missing/stale/incompatible proof prevents releasing the table. A cancelled/refunded/void order with a cup still carried must not be excluded solely because its financial status changed.

All add-more, transfer, manual close, paid-close and expiry writers need one session lock/version protocol. Close reloads the exact order set under the lock, validates proof/manifest/session/location and closes atomically; an add-more retry for the old session receives 409 rather than reopening/attaching to another sitting. Cup Dine-in creation should require verified expectedSessionId; mixed WholeOrder/Cup sessions need an explicit policy.

The current v1 FulfillmentSnapshot lacks global ticket hold/location/pending-transfer proof. LastVerifiedAt alone is not permission to close. Design a versioned proof contract with safe old/new hash handling; simply adding fields to the old semantic-hash record would invalidate stored hashes. A proof-version transition needs a separate review, including audit of aggregate hold/location changes and the race between proof verification and another operational writer. An alternative is a persisted operational prepare-close/freeze handshake; it likewise needs schema/contract approval.

No automatic close, paid/refund state, totals, bill count, session model or Payment/Bill endpoint changed. Acceptance requires Paid-before-Serve, Serve-before-Paid, void with live custody, stale proof, close vs add-more/transfer/Manager action, expiry, lost ACK and late retry after table reuse.

## 6. Web legacy guard/API review

| Path | Audit result |
| --- | --- |
| Whole-order deliver/unpaid Staff cancel | Cup guard before mutation/replay; current regression and role-based HTTP tests |
| Kitchen whole-ticket status/remake/stickers/preview | Cup guards; old queue/batches exclude CupV1 |
| Legacy Redis creation/admission | Mode marker rejects Cup creation through legacy writer; canonical v2 hash/correlation retained |
| Session transfer/service-type | Cup rejected; no partial move is authorized |
| Customer cancel / no-show / unfulfillable / paid exception | **FAIL readiness audit:** aggregate prechecks/refund paths are not uniformly Cup-guarded. `EnsureCustomerCanCancel`, `EnsureCanBeMarkedNoShow`, `EnsureCanBeMarkedUnfulfillable` use legacy Order.Status while Cup serving updates a separate projection. ExceptionAsync may request refund before an operational guard. Needs separately approved safety gates before those side effects |
| Manual/paid/expiry close | **BLOCKED:** financial-only close condition |
| Web FE client source/runtime | **NOT RUN:** not cloned; cannot certify error handling, stale UI or Cup fallback |

No public endpoint was renamed/deleted and no old request shape changed this increment. Fixing the financial/void paths can affect refund and close behavior; it was not silently implemented under a nonfinancial task. Before enablement, review guards before any remote refund/DB write, with the Web owner; keep WholeOrder behavior unchanged and return an actionable 409 for unsupported Cup legacy mutations.

## 7. E2E gate and test plan

Current full Staff Mobile → public Create Order → Cup admission → Barista → Claim/Pickup/Serve: **BLOCKED / NOT RUN** because approved server mode selection is absent. TTL/Manager/close policies remain separate operating gates; an isolated nonfinancial E2E could use the existing explicit test lease once its source opt-in is approved, without changing Payment/Bill. The ACL test explicitly uses a Cup source fixture, fake Table/branch directories and test-signed Staff JWTs; it is real PostgreSQL/Redis/HTTP integration, not a replacement for Mobile E2E.

After mode/policy approval: use a fresh dedicated test stack and identities, real Identity login for Cashier/Barista/two Staff, scoped Menu/Table fixtures, foreground Mobile UI with its real adapters, source creation through the actual POST, independent HTTP services/background dispatchers and projection checks. No operating credentials/database/device session. First propose Takeaway-only source opt-in if Dine-in remains blocked; this is a proposal, not an implemented restriction/default.

Required evidence: network target/cluster guards, source mode chosen by actual approved server policy, UI product/cup ownership/location at each transition, real outbox/inbox rows, no duplicate effects after lost response, full order creation retry, two same-cup claimers and independent-cup Staff, backend process/Redis restart, and successful transfer only after its schema/protocol approval. Device/Web/hardware tests must be reported separately from mocks/TestServer and from tests that merely demonstrate a blocked operation.

## 8. Final evidence and operating checklist

Final fresh run results and remaining gates are appended after execution. Every approved test uses only new disposable resources and snapshots existing containers before/after. No new business migration, operating GRANT/REVOKE, deployment, production enablement, commit or push.

| Check | Fresh result / evidence |
| --- | --- |
| Non-owner role/HTTP workflow | **PASS: 1 composite, 0 FAIL, 0 SKIP**. Real logins, role-context current_user checks, two simultaneous Staff on different cups, Manager-owner refusal, replay, immutable-ledger denials, no cross-service DB connection, source table retained on rejected transfer. Ten operations/ten inbox receipts remain after retries |
| Existing Cup concurrency/crash/recovery | **PASS: 1 composite, 0 FAIL, 0 SKIP**. Re-ran `-Run -Cups`: same-cup contention, lost ACK, real process kills, guarded Redis restart, projection recovery and hold checks |
| Backend regression | **484 PASS / 0 FAIL / 0 SKIP**; excludes five explicitly opt-in infrastructure classes. No excluded/NOT RUN suite counted as PASS |
| Flutter check | Formatter: 5 files, 0 changes; analyzer **0 issues**; **7 Cup model/repository/widget tests PASS**. These are mock HTTP/widget tests, not real Backend E2E. Full 166-test result belongs to the previous increment and was not repeated |
| Source financial/legacy readiness | **FAIL audit gate**, not a failing automated test: unsupported Cup financial/void/close paths described above need review before enablement |
| Successful Cup transfer / Manager resolution | **BLOCKED**, proposal only; rejection/ownership guard assertions do not count as feature success |
| Full Staff Mobile → public Create Order → Cup serving E2E | **BLOCKED / NOT RUN**, missing approved server mode-selection path; no fake E2E declaration |
| Actual Web FE / hardware | **NOT RUN**; user confirmed FE not cloned; no physical print test |

Final ACL evidence: cluster `7705410743844257826`, network `aicos-staff-operations-b31b6de247`, loopback ports 58173/58176, TRX `%TEMP%/aicos-staff-operations-b31b6de247/staff-operations.trx`. Separate Cup run: cluster `7705408163724202019`, TRX `%TEMP%/aicos-staff-operations-7e10a5776d/staff-operations.trx`. Both runners confirmed unchanged existing container ID/image/start/status/restart snapshots and cleaned up only their new containers/network. No Docker volumes removed.

The recorded matrix is service-table access, not a certification of tenant RLS or every legacy endpoint. Runtime roles have no migration-history access, schema CREATE or object ownership; schema installation was done through a separate disposable admin before switching service contexts.

Reviewed SQL SHA256 after preflight hardening:

- `cup-serving-runtime-acl.sql`: `37E71FC89FCEC3080A81E03A320B13D4E0B2928CD3ED554A66773F0C889896C8`.
- `cup-serving-service-prerequisites.sql`: `FD79FD0ECE7B210E18DCE847457D5869E6B5463D7A195B28BECB62099660A295`.

These are review-proposal hashes, separate from the unchanged seven-entry Cup migration bundle. Proposals reject DB/schema/table owners, role memberships, unsafe attributes and wrong target identity. They do not change ownership or other roles to force a PASS.

Run corrections: the first harness attempt observed PostgreSQL's temporary bootstrap socket before the database existed; readiness now waits on final TCP. A subsequent added assertion read a response stream twice; the test now caches parsed claim bodies. Those attempts were NOT PASS; final fresh runs above passed. Neither issue required production code, operating data or credential changes. Existing CS0628/CS8620 build warnings remain.

Changed this increment: `tests/AICOS.Services.IntegrationTests/CupServingAclFixture.cs`, `CupServingAclInfrastructureTests.cs`, internal reuse of existing `CupServingInfrastructureTests.ExpandAsync`; `scripts/Test-StaffOperationsInfrastructure.ps1` adds `-CupAcl`, rejects multiple modes and fixes final-TCP readiness; the two ACL proposals; this report, generated ACL run/matrix, refreshed Cup run and progress links. Mobile changed only its implementation log. Existing service business code, endpoint contracts and EF migrations remain intact.

- [ ] Approve reservation/carrying TTL, renewal/expiry handling and Manager physical evidence/override transitions.
- [ ] Approve the transfer eligibility policy, location protocol and additive schema; implement and test successful transfer before declaring it supported.
- [ ] Approve financial + operational session-close proof and common locks, including void orders with custody.
- [ ] Approve legacy financial mutation safety gates and decide the narrow pickup-slot DELETE permission.
- [ ] Review separate DDL owner/effective runtime ACLs and backup/restore; approve any operating change independently.
- [ ] Approve real server Cup mode selection and mixed-session rules; run actual Mobile/service E2E and Web FE once available.
- [ ] Complete versioned hold/location proofs, reconciliation scale/retention review and hardware/device validation.
- [ ] Keep all operating Cup flags OFF until the gates above have evidence; test TTL and component PASS do not authorize rollout.
