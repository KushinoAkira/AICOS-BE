# CupV1 admission, HTTP operations and projection increment

**Follow-up 2026-11-07:** fresh non-owner service-role validation, actual permission matrix and blocker/approval checklist are in [CUP_SERVING_BLOCKER_RESOLUTION_REVIEW.md](CUP_SERVING_BLOCKER_RESOLUTION_REVIEW.md). The owner-based results below describe the V2 implementation increment; the follow-up adds a separate ACL suite, not operating enablement or completed Mobile E2E.

2026-11-07. **PARTIAL / TEST-ONLY ENABLED. Not production-ready.** Continue the existing 4+2-table foundation; no replacement of completed code. No operating database/container, Payment/Bill service, session-close behavior or deployment changed. No commit/push.

## Schema and safety audit

- Kept both existing additive EF migrations and all seven SQL/migration checksums unchanged. No new EF migration or persistence column/table.
- The isolated test migrates each empty service DB to its recovery baseline, verifies all checksums, invokes the actual `00_preflight.sql` via psql with expected database/schema/cluster, then executes its reviewed generated expand SQL.
- Tests verify no pending EF migrations, table count increases exactly +4 Kitchen/+2 Ordering, no cross-schema FK, and the unique partial claim index includes Reserved/Carrying/Overdue. UUID keys require no new sequences. This does not benchmark deployment locks on a populated database.
- DDL applies only after `OperationalTestTarget` verifies disposable container ID, system identifier, network, tmpfs, random loopback port and database allowlist. Main/Windows/test databases outside this run are rejected.
- Safe rollback is to retain additive storage and committed custody/audit. Never run EF Down automatically, clear a live claim, convert active cups to WholeOrder, or replay physical printing. Stop new Cup admission; preserve reader/commands needed to drain valid custody. Expired custody needs the pending Manager policy.

## APIs and behavior

| API | Contract / authorization |
| --- | --- |
| POST `/internal/v2/kitchen/order-admissions` | Ordering machine JWT, `kitchen.admission.v2.write`, exact tenant/branch; immutable manifest hash, OrderItemId/quantity and UUIDv5 CupId; ACK after ticket/items/cups/stickers/receipt commit |
| GET `/api/v1/kitchen/cups?orderId=...` | Staff JWT, tenant and branch scope; current cups/claims, server time, global hold, eligibility, lease readiness, product/sticker correlation; print status UNKNOWN |
| POST `/api/v1/kitchen/cups/{cupId}/start` or `/ready` | Barista/Manager; per-cup preparation, expected version/cycle |
| POST `/api/v1/kitchen/cups/{cupId}/claim`, `/renew`, `/release`, `/pickup`, `/serve` | Cashier/Manager; current owner required for custody actions; expected version/cycle, claim ID/fence; Idempotency-Key required |
| POST `/internal/v1/ordering/cup-fulfillment` | Separate Kitchen machine issuer/audience/key/scope; inbox + local projection commit before ACK |
| GET `/api/v1/orders/{orderId}/fulfillment` | Staff JWT and branch scope; nonfinancial projection only |

Commands preserve existing core validation: stale state/fence returns 409; denied role/ownership 403; missing JWT 401; invalid payload/key 400. Claim/Renew return 503 until an explicit lease policy is approved/configured. Release requires an unexpired Reserved claim and explicit physical-at-Bar confirmation; Carrying or expired custody is never automatically released. Manager override/reassignment/remake/timeout-resolution endpoints are deliberately absent.

Receipt replay never resets prep, custody, served state or recovery hold. Historical preparation/terminal source without individual proof is held. A monotonic source IsPaid fact can restore Kitchen eligibility; it does not take money, close a bill or close a session. Stickers are generated identities, not hardware print ACKs; no V2 auto-print event is emitted.

### Compatibility

OrderCreated has one additive optional fulfillmentMode marker (old payload defaults WholeOrder). Kitchen legacy Redis admission ignores CupV1/unknown modes so it cannot race V2 into a WholeOrder ticket. Existing consumers must all deploy dual-reader guards before any Cup producer is enabled. Existing public Order requests and financial responses retain their shapes; no legacy endpoint was removed/renamed.

The existing delivery/cancel/status/remake/sticker/transfer guards remain. Cup transfer still fails closed; there is no table move/merge or paid-exception implementation in this increment. Full Web FE runtime compatibility remains unverified; regression covers current Backend contracts, not a running Web client.

## Capture, retry and reconciliation

- Ordering uses the existing SaveChanges outbox hook: mode-specific capture flag; source Order and V2 manifest outbox commit atomically. Manifest reuses the bounded legacy preparation-note snapshot, including resolved customization text; it is not a new structured-options store. Immutable rows have a separate V2 consumer/schema version.
- Existing admission dispatcher supports V2 under its own flag and validates ACK event/consumer/tenant/branch/order/ticket/receipt status. Reconciler has a separate V2 durable checkpoint, traverses retained Orders without an age cutoff, skips WholeOrder and restores missing admission/eligibility. No direct cross-service table reads.
- Kitchen command commit stores audit + a full snapshot outbox. Fulfillment dispatcher claims SKIP LOCKED batches, uses a fresh lease token, closes transaction before HTTP, validates identity/version/hash/ACK outcome, and only updates the still-current unexpired lease. Timeout/lost ACK retry the original immutable event.
- `next_attempt_at_utc` on each row is the durable reconciliation cursor. ACKED snapshots are rechecked after 60 seconds with the same identity; receiver replay revalidates/rebuilds a missing projection instead of blindly trusting its existing receipt. No new checkpoint table is needed for this replay mechanism. This is not a repair algorithm for deleted source outbox/custody/audit rows.
- Throughput risk: all historical ACKED versions are replayed; the current due index is partial PENDING/IN_FLIGHT. Before rollout, review a latest-per-order sweep/index or retention policy with DBA. No additional index or retention deletion was applied without approval.
- Redis is not a delivery guarantee for this path; restart does not clear custody/outbox. Background worker defaults OFF.

## Feature gates and decisions

All absent boolean settings default false. No operating Compose/appsettings/env file was changed to enable them.

| Setting | Meaning |
| --- | --- |
| `CupServing:CaptureEnabled` | Capture V2 source orders; separate from legacy admission capture |
| `CupServing:AdmissionEnabled` | Kitchen V2 machine receiver |
| `CupServing:AdmissionDispatchEnabled` / `AdmissionReconciliationEnabled` | Ordering V2 dispatcher/sweep |
| `CupServing:ApiEnabled` | Kitchen read/command adapters; capabilities otherwise remain zero |
| `CupServing:LeasePolicyApproved` + `LeaseSeconds` | No runtime TTL default. Isolated tests explicitly use 120 seconds as a fixture, not approval of a business TTL |
| `CupServing:ProjectionReceiverEnabled` | Ordering machine receiver/read projection |
| `CupServing:DispatcherEnabled` | Kitchen snapshot transport and periodic replay |
| `CupServing:MachineKey` | Separate from Staff JWT and admission key; not in source, docs or logs |

**Public production creation of CupV1 remains blocked**, so no ordinary POS/QR/Web request is silently switched into a new workflow. Tests set mode only on approved disposable source fixtures. The capture hook is shared by those creation flows once a future approved mode-selection/rollout gate exists. Do not call that full POS→payment/session→Cup E2E.

### Permissions

Kitchen needs SELECT/INSERT/UPDATE tasks/claims/outbox and SELECT/INSERT operations. Ordering needs SELECT/INSERT/UPDATE projection and SELECT/INSERT fulfillment receipts. Existing admission receipt updates, admission outbox/checkpoint, ticket/items/stickers and scoped reads remain prerequisites. New tables use no sequences. No DELETE/TRUNCATE or cross-service grants are required for normal execution.

Prepared `docs/proposals/cup-serving-runtime-acl.sql` for DBA review only; **NOT EXECUTED**, not added to the migration bundle. It fails closed for mismatched role/database/schema or table ownership. Owner/inherited/PUBLIC privileges can defeat a simple REVOKE, so current init ownership and effective role membership must be reviewed before enforcing an append-only ledger at SQL level. No operating ACL audit/grant was performed in this task. Current integration tests use the disposable schema owner, not production least-privilege credentials; application audit immutability is not proof of SQL owner restriction.

## Flutter

Real API repository/model and a panel inside existing Order Detail, only when mode is CupV1. Separate reusable card shows each cup, owner name through the existing bounded/scoped staff display-name API (ID fallback if unavailable), prep/custody state, global hold and overdue warning. API drives buttons and refresh; no demo state stands in for an ACK. GET includes sticker identity but no hardware-print success claim.

Loading/read failure blocks actions. The same actor-bound intent/key/expected version is retained after an uncertain response; only explicit staff retry resends it. A definitive 409 causes a state reload. Physical Pickup/Serve/Release require confirmation. The panel refreshes read-only every 15 seconds while foreground, and never auto-releases custody. Pending intent is in memory; process-kill persistence remains a limitation. After reopening, server state remains authority; the app does not auto-resend an unknown previous action or retry under a different signed-in actor.

## Dine-in / remaining blockers

`TableSessions.CloseIfSettledAsync` can still close after payment before every cup is served. Proposed separate task: financial settlement AND operational fulfillment proof, with a common session lock/version for add-more/manual-close/payment-close/expiry; missing/stale/held proof must not free the table. No Payment/Bill/session-close code was changed. Global ticket holds/location changes are not yet represented as versioned full projections, so this projection must not authorize close/settlement.

TTL/cadence, Manager evidence/override/reprepare, Dine-in close policy, location-versioned transfer, remaining financial legacy routes and rollout mode selection still require approval/review. Do not enable CupV1 on the operating environment. The current schema is retained; no new schema proposal was silently applied.

## Verification

Execution evidence and final counts are recorded below after the final validation run. The infrastructure suite is one composite test with multiple assertions, not a claim of many independent E2E tests. It uses PostgreSQL/Redis and process kills for real; TestServer supplies HTTP hosting, test-signed Staff JWTs and fake branch directory. No real Identity login, payment provider, printer or Web/mobile device E2E.

| Validation | Actual result |
| --- | --- |
| Backend regression, excluding four opt-in infrastructure classes | **484 PASS / 0 FAIL / 0 SKIP**; primarily InMemory/mock HTTP, not PostgreSQL E2E |
| CupV1 infrastructure composite | **1 PASS / 0 FAIL / 0 SKIP**; latest TRX `%TEMP%/aicos-staff-operations-dc14d3f18e/staff-operations.trx` |
| Existing admission recovery infrastructure | **18 PASS / 0 FAIL / 0 SKIP**; `%TEMP%/aicos-staff-operations-6ef6d299d8/staff-operations.trx` |
| Flutter full suite | **166 PASS**, including 7 new Cup tests; mock HTTP/model/widget, not real Backend/device |
| Flutter analyzer | **0 issues** |
| EF model/snapshot check | Both contexts: no pending model changes, offline fake localhost:1 target; no database contacted |
| Formatter / Git whitespace | Formatter applied to changed source only; diff checks PASS |
| Runtime least-privilege ACL script / hardware / full deployed Web/Mobile flow | **NOT RUN** |
| Manager resolution, successful Cup transfer, Dine-in financial close behavior | **BLOCKED** pending decisions; not claimed as tested features |

Final Cup cluster `7705395476521599011`, network `aicos-staff-operations-dc14d3f18e`, random loopback ports 51028/51029; source databases only `aicos_operations_ordering_test` and `aicos_operations_kitchen_test`. Runner recorded existing container ID/image/start/status/restart snapshots unchanged and removed only its new containers/network. It did not remove any Docker volume. The separate recovery run used another new cluster; neither run used main or Windows databases.

The composite asserts:

- checksum/preflight and exact additive table counts, local FKs and the live-claim unique index;
- authenticated admission, invalid/missing machine token rejection, concurrent replay, stable CupId/OrderItem/sticker mapping, and rejection of a receipt deliberately pointed at the wrong test ticket;
- source capture retained while the admission transport injects Kitchen-unavailable failure, then real HTTP retry/ACK and separate V2 reconciliation checkpoint. This offline leg is **transport fault injection**, not a powered-off production container;
- production Redis publication of the Cup marker alongside V2 admission; no legacy WholeOrder ticket creation;
- killed OS processes before admission Save/commit and after commit, then replay with no duplicate cups/stickers; killed Pickup process after commit, then replay with the original key;
- two staff claiming one cup: one 200 and one 409; wrong role 403, bad fence 409, renew, safe release, unsafe/expired custody rejection and no automatic reallocation;
- snapshot receiver commit with lost ACK injection, fresh dispatcher/context retry, concurrent dispatchers and reconstruction of a deleted **test-only** Ordering projection while retaining inbox receipts. Dispatcher also revalidates/renews each still-owned lease before HTTP, so later expired batch rows are skipped;
- restart of only the newly-created guarded Redis; SQL-backed serving/projection remains usable;
- source cancellation causes a hold without resetting custody, new operations are blocked, old admission replay cannot clear the hold.

Test corrections during implementation: missing namespace/internal-helper compile references and a void-returning assertion were corrected; one test expected 422 where existing validation correctly returns 400. A regression exposed changed replay ACK outcome; the writer now revalidates/repairs projection while returning the original historical ACK. Failed attempts were rerun; they are not counted as PASS. Existing CS0628/CS8620 warnings remain unchanged.

## Changed files (this increment)

- Contracts/security: `src/BuildingBlocks/AICOS.Contracts/Serving/CupAdmission.cs`, `AICOS.Contracts/{KitchenAdmission,OrderEvents}.cs`, `AICOS.BuildingBlocks/Security/FulfillmentMachineAuthentication.cs`.
- Kitchen: `src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/CupServing/{CupAdmissionWriter,CupServingEndpoints,FulfillmentDispatcher,FulfillmentTransport,CupServingUnitOfWork}.cs`; `Domain/Entities/KitchenTicket.cs`; `Features/{KitchenEndpoints,KitchenEventHandlers}.cs`; `Features/KitchenAdmission/KitchenAdmissionEndpoints.cs`; `Program.cs`.
- Ordering: `src/Services/Ordering/AICOS.Ordering.Api/Features/Fulfillment/{FulfillmentEndpoints,FulfillmentProjectionWriter}.cs`; `Features/KitchenAdmission/{AdmissionOutbox,KitchenAdmissionDispatcher,KitchenAdmissionReconciler,KitchenAdmissionTransport,KitchenAdmissionWorkers}.cs`; `Features/Orders/OrderSupport.cs`; `Persistence/OrderingDbContext.cs`; `Program.cs`.
- Tests/runner: `tests/AICOS.Services.IntegrationTests/{CupServingInfrastructureTests,CupServingGuardTests}.cs`; `tests/AICOS.AdmissionCrashProbe/Program.cs`; `scripts/Test-StaffOperationsInfrastructure.ps1` (`-Run -Cups`). Generated evidence: `docs/{CUP_SERVING_INFRASTRUCTURE_RUN,KITCHEN_ADMISSION_RECOVERY_RUN}.md`.
- SQL/docs: this report, `docs/proposals/cup-serving-runtime-acl.sql`, links in foundation review/progress and SQL bundle README. The 2 EF migrations, designers, snapshots and generated expand/preflight SQL **were not edited**.
- Mobile: `lib/models/main_cup_serving.dart`, `lib/repositories/main_cup_serving_repository.dart`, `lib/widgets/serving/{main_cup_card,main_cup_serving_panel,main_order_detail_view}.dart`, `lib/screens/main_order_detail_screen.dart`, `test/main_cup_serving_test.dart`, `docs/IMPLEMENTATION_LOG.md`.

Prior uncommitted CI/Kitchen race/sticker work remains intact; it was not reset or attributed as newly implemented Cup functionality.

## Conditions before any operating enablement

1. Approve lease TTL/cadence and Manager evidence/resolution/reassignment policy; implement and test those paths without automatic custody release.
2. Approve/implement Dine-in close protection and a location-versioned transfer protocol; finish legacy financial mutation guard audit with the Web owner. Until then no production Cup mode selection/creation.
3. Review effective service ownership/ACLs and the proposed grants; test with least-privilege runtime roles. Approve operating schema expansion independently with backup/restore and lock plan.
4. Review bounded latest-version reconciliation/index/retention and global hold/location proof versioning. Projection is not a settlement permission.
5. Run actual deployed service/Gateway/Mobile/Web E2E, eligible Takeaway payment propagation, foreground/background/device loss and physical sticker printing tests. Current flags remain OFF; no deploy, migration, role change or rollout on the operating environment is implied by these test results.
