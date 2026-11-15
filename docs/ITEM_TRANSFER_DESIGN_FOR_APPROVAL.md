# Real Item Transfer - design awaiting approval

Status: MVP principles accepted on 2026-11-15; technical design/isolated proof stage. No runtime ItemTransfer endpoint or feature activation. Authoritative source validation, V2 billing/serving requirements and remaining blockers: ITEM_TRANSFER_TECHNICAL_VALIDATION.md. Six SQL prototype tables were exercised only in new disposable design databases; these are not finalized EF/operating migrations.

## Source findings

- Ordering owns TableSession, Orders and SessionBillQuotes. The quote currently selects complete Orders by TableSessionId and uses whole-Order final amounts. Moving only a display row or changing Order.TableId cannot move an individual unit's financial obligation.
- Kitchen CupTask has stable Id = CupIdentity(tenant, order, orderItem, unitIndex), separate preparation/service states, version, fencing and claim history. An OrderItem may represent several physical units; quantity alone cannot identify which claimed/served cup moved.
- CupAdmissionWriter verifies immutable manifest and SourceTableId/SourceSessionId. Rewriting these during transfer would conflict with Kitchen Recovery/replay. Existing WholeOrder table transfer deliberately rejects CupV1.
- FulfillmentSnapshot and Ordering OrderFulfillmentProjection aggregate by Order, not current destination session/unit. They cannot prove destination-session delivery completion after a partial transfer. SnapshotJson is not a substitute for absent unit-level routing evidence.
- Payments owns one logical Bill per session, frozen quote/payment attempts/PAID state. Draft split groups are item quantity allocations within that Bill; they are not cross-session transfer or a payment ledger.

## Proposed business rules requiring explicit approval

1. MVP: same tenant/branch, two already-open Dine-in sessions; authenticated CASHIER/branch management roles already allowed by CounterRoles, not BARISTA. No automatic target-session opening, Takeaway conversion or cross-branch movement.
2. Select exact stable CupIds plus expected Cup versions and routing versions. Only Preparation=Pending, Service=Waiting, no claim, no recovery hold, no existing transfer reservation. Quantity UI resolves to exact units on Backend. Unknown/legacy WholeOrder unit identity is blocked; do not manufacture history for already-prepared cups.
3. Brewing, Ready, Reserved, Carrying, Served, Review and paid/refunded/frozen/unreconciled units are blocked in MVP. Expiry/offline never releases physical custody. Manager override for these states needs separate policy; this proposal does not grant it.
4. Both sessions must have no checkout freeze, pending payment, PAID/CLOSED Bill, pending transfer or active reconciliation. Reject discounted/promotional/taxed/service-fee items unless the monetary allocation rule has been separately approved. Never silently reprice transferred units from today's Menu.
5. Preserve immutable Order/OrderItem/Cup/Ticket/Sticker IDs, preparation cycle and original price/options. Only current billing assignment and delivery destination change. No new Order, no duplicate ticket, no rebrew, no changed birth manifest.
6. Source Bill loses exactly the transferred immutable unit amount; target Bill gains the same amount. Sum across both sessions is unchanged. Each session retains its own single original Bill; neither paid Bill is reopened. Existing allocation drafts become stale explicitly, never silently reallocated.
7. Block transferring all remaining billable units out of a source session until zero-total Bill/session completion is approved. Reject target/session closure while a transfer is unresolved.
8. Confirmation is final once Ordering has committed the financial assignment. No edit/delete/cancel after commit; a compensating transfer requires a new validated operation. Cancellation is allowed only before the commit decision and after Kitchen has durably confirmed reservation release.

These are proposed restrictions, not approved operating policy. Approve or adjust them before implementation.

## Data ownership and minimal proposed persistence

| Owner | Addition | Reason / reuse |
| --- | --- | --- |
| Ordering | unit_session_allocations: immutable source identity, CupId, currentSessionId, amount snapshot, routingVersion, transfer lock/version | Stable exact-unit financial/delivery ownership. Unique tenant/CupId; own OrderItem FK only. No Kitchen DB FK. Materialize original allocation transactionally at create; historical backfill requires explicit safe evidence. |
| Ordering | item_transfer_operations: actor, source/target, key/hash, versions, immutable selected-unit payload, commit decision, phase/ACK/error/audit timestamps | Durable saga/outbox and operation audit in one table; do not add a second generic outbox unnecessarily. Unique tenant/actor/idempotency key and reservation ownership. Retain terminal history. |
| Kitchen | per-cup mutable current session/table/code/routing version and transfer reservation metadata | Reuse CupTask when EF boundaries permit; preserve SourceTableId/SessionId on ticket and all admission manifests. Preparation/claim/pickup must check the reservation under the existing Order lock. |
| Kitchen | durable item-transfer receipt keyed operation/phase, payload hash and commit evidence | Extend existing operation ledger only if it can distinguish M2M operations, replay and phase safely; otherwise a dedicated small inbox is needed. Do not abuse admission receipts as a mutable transfer ledger. |
| Ordering | versioned per-unit fulfillment/routing projection | Extend transport with a new versioned contract while preserving aggregate legacy events. Required for session-close and quote membership after partial movement. Reuse existing fulfillment inbox/outbox infrastructure; add missing unit projection only after schema review. |

Exact EF/SQL, CHECK/index definitions and GRANT proposal must be reviewed before execution. No cross-service FK, physical DELETE, existing-ID rewrite or distributed database transaction. This design changes Billing quote membership/close evidence and therefore requires explicit financial/session policy approval before code.

## Durable protocol; no distributed transaction

1. Preview: resolve current sessions, units, amounts, permissions and restrictions from owning APIs. Return source/target fingerprints, versions and violations. Preview is not a reservation or money mutation.
2. Create: lock Ordering session/table keys in sorted order; share the same fences with Add Order, checkout freeze, Table Transfer and Close Session. Persist operation/reservations in Ordering. Release DB transaction before HTTP.
3. Prepare Kitchen: M2M authenticated command with operation/hash/CupIds/expected versions. Lock original Orders in sorted order and atomically reserve all eligible cups or none. ACK only after commit. Serving/preparation cannot bypass reservations, including legacy endpoints.
4. Commit decision: recheck both session fences and Kitchen prepare evidence, then atomically update both unit assignments and mark COMMIT_DECIDED in Ordering. Ordering owns both session assignments, so this is one local transaction. Emit durable finalize work. Until full synchronization, both Bills/checkout/close fail closed with transfer.pending, not inconsistent totals or false completion.
5. Finalize Kitchen: idempotently apply exact destination/version, retain immutable birth identity, clear only that operation's reservation and write receipt/outbox. ACK after commit; lost ACK replays return the same result.
6. Ordering applies Kitchen evidence through inbox, marks COMPLETED and unblocks quote/checkout/close. Worker and full pending-operation reconciliation resume after crash/Redis restart; Redis is notification only.
7. Ambiguous operations stay PENDING/REVIEW with visible reason. No auto-cancel on timeout. Recovery queries durable owner status; abort requires proof that no financial commit exists. A delayed finalize cannot affect a newer operation because of fencing.

If a cup is served meanwhile, one shared lock/reservation check wins; the loser gets 409. Claims never migrate owner or reset by network retry. Reconciliation must also inspect nonterminal operations without an age cutoff, including exhausted retries.

## Proposed additive API contracts (not registered)

- POST /api/v1/orders/item-transfers/preview: sourceSessionId, targetSessionId, units[{cupId,expectedCupVersion,expectedRouteVersion}]. Response eligible/violations, immutable amounts, quote fingerprints, expected session versions.
- POST /api/v1/orders/item-transfers + Idempotency-Key: preview expectations and reason; 202 operationId/status, or 409 stale/frozen/claimed; 403 role/branch; 422 malformed input. Backend resolves amounts, ignores caller monetary totals.
- GET /api/v1/orders/item-transfers/{id}; GET /api/v1/orders/qr/sessions/{id}/item-transfers: scoped status, exact units, source/target, actor, audit, safe retry details.
- POST /api/v1/orders/item-transfers/{id}/cancel: only pre-commit with expectedVersion/key and verified Kitchen abort evidence; never delete history.
- Private Kitchen prepare/status/finalize/abort endpoints: separate M2M authentication, bounded unit batches, operation payload hash, monotonically fenced route versions and phase receipts. Public user JWT cannot impersonate M2M.
- Versioned unit fulfillment/assignment snapshot contract: immutable source IDs, mutable current session/table, routing version and preparation/service evidence. Keep existing Web aggregate DTOs additive; old serving endpoints must reject an unsupported mixed-route Order.

Proposed operation states: PREPARING -> PREPARED -> COMMIT_DECIDED/PENDING_SYNC -> COMPLETED; before commit only ABORTING -> CANCELLED; REVIEW for contradictory evidence. Public 202 is pending, never success. APIs are proposals; names can reuse established route conventions after approval.

## Mobile and Web impact

Mobile: choose exact units and destination session, preview source/target amounts, explicit confirmation, durable pending/history screen, same key/body retry, stale409 refresh and no local money/state optimism. Display current destination on Barista/Serving while retaining origin in audit. Disable unavailable states with Vietnamese reasons.

Web FE is not cloned: compatibility is NOT TESTED. Preserve current endpoints/DTOs; add fields/versioned endpoints. Gate mixed routing on Backend so a legacy Order-level Delivered action cannot skip claim or destination checks. Updating only Flutter is insufficient to safely enable this feature.

Kitchen stickers: preserve physical code/CupId/print history. New print data includes current route with version and origin; do not silently reprint or pretend an old paper label changed. If already printed cups are allowed later, approve an explicit relabel workflow first. Recovery uses immutable birth manifest, then replays the latest authorized routing projection; never clear Recovery Hold from a transfer event.

## Acceptance tests before feature enablement

Real isolated PostgreSQL/Redis + Gateway: partial quantity of identical cups; unchanged combined totals/IDs/ticket/sticker counts; stale quote/route/Cup versions409; two Cashiers competing; Barista prepare vs reserve; checkout/Add Order/Close vs transfer; PAID/frozen/refund/hold rejection; both source and destination close require their assigned units served; split drafts visibly stale; no cross-branch tenant access; legacy guard.

Crash before/after Kitchen prepare, before/after financial commit, lost finalize ACK, two dispatchers, out-of-order duplicate/replayed phases, Redis offline/restart, historical reconciliation and cancellation race. Verify operation resumes without duplicate financial assignment, new Order/ticket or auto custody release. Audit must identify actor/reason/evidence. Mock transport tests must not be reported as PostgreSQL/process-crash E2E.

## Rollout / rollback / approval checklist

1. Approve eligibility, roles, monetary restrictions, zero-total source policy, reservation/Manager rules and per-unit session-close evidence.
2. Implement additive contracts/fences and isolated schema migration; run all tests and backup/restore on copies. Validate narrow service ACL.
3. Independently review Web client, routing/printed-label UX and operating schema/history. Flags OFF by default; no operating migration/deploy is authorized here.
4. After use, disable new commands and drain/reconcile in-flight operations; retain assignment/audit rows. Prefer forward fixes. Empty-only rollback must refuse persisted assignments/commit decisions. Do not restore one service alone after a cross-service commit; reconcile both sides from durable decisions.

No operating schema or runtime item-transfer feature has been applied. Isolated SQL proposals/test tools are documented separately. Group-payment allocation, Payment/Bill production behavior, Serving custody policy and operational migration remain outside this approval.
