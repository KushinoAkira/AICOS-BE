# Cashier Order Detail and Session Bill - 2026-11-14

## Source changes

- Existing bill-scoped Cash/QR/invoice/close flows are reused. No payment rules, migration, production flag or runtime deployment changed.
- Add GET /api/v1/payments/bills/{id}/history?sessionId=... in the existing SessionBilling feature group and Counter authorization policy.
- Persisted Bill history resolves tenant and branch through BillLedgerService. Draft history requires a valid session quote and matching deterministic Bill ID; it never inserts a Bill.
- Response: billId, sessionId, branchId, attempts (maximum 100), events (maximum 100 id/kind/time only), hasMore. No journal payload, signing key or internal lease is exposed.
- BillAttemptView adds optional cashierUserId from the already persisted CreatedByUserId. Existing fields/paths remain compatible; no schema change is necessary.
- Unit/API tests cover empty draft history, paid history actor, non-mutating reads, Barista denial, branch/tenant and session mismatch.
- Isolated stack runner verifies this route through the real Gateway and PostgreSQL, including actor metadata and 403 for Barista. Existing fixtures exercise Cash lost ACK/same-key retry, concurrent cashiers, Add Order race, QR Mock callbacks, serving and session close.

## Mobile integration

- Dine-in order detail uses its original tableSessionId and one aggregate Bill, never per-order collection.
- Takeaway reuses existing cash/transaction/settlement contracts; no invented order-scoped Session Bill.
- Shared cash sheet, Bill history, paid receipt actor, immutable invoice lines, fail-closed scope checks and preview-only HTML are implemented in the Mobile repository.
- A failure reading close eligibility does not erase a successfully verified paid Bill. It blocks closing independently.
- Customer preview contains no remaining-balance line and does not call the legacy Order receipt endpoint that increments print count.

## Safety and remaining blockers

- SessionBilling remains OFF in the operating stack. The two previously prepared Ordering/Payments migrations, machine authentication and role grants still require separate operational approval. No new migration was added here.
- Session Bill QR provider is Mock/test only. Legacy simulated bank providers do not establish actual receipt of funds. No real-bank success is claimed.
- Takeaway Bill by orderId is not implemented by the existing Bill API; cash remains on the existing order payment contract.
- Reporting and Workforce cash-shift consumers still omit Session Bill ledger receipts. A Bill-level revenue/shift/refund allocation contract must be approved before rollout; do not publish fake per-order receipts or count the same revenue twice.
- Existing Identity stores one primary role. Mobile accepts a role set when supplied; this change does not invent backend multi-role persistence.
- Physical printing and device E2E are not included.

## Validation

- Backend regression: 550 PASS, 51 SKIP, 0 FAIL. The skipped infrastructure tests are not treated as PostgreSQL coverage.
- Focused SessionBillFlowTests: 14 PASS, 0 SKIP.
- Final isolated-stack and Flutter results are recorded below after execution completes.
- No operating database, secrets or previously running service was modified by this task. No deploy, commit or push.

## Final execution evidence

- Isolated PostgreSQL cluster: 7708001629431062581; own ephemeral network/tmpfs PostgreSQL, no host PostgreSQL port and synthetic fixtures only.
- Stack: 29 PASS groups, 1 BLOCKED, 0 FAIL. Evidence: SESSION_BILL_STACK_TEST_EVIDENCE.json.
- Includes reviewed migration checksums, additive schema/constraints, least-privilege role connections, repeat refusal, backup/restore and rollback/reapply on copies; no migration applied to an operating database.
- Cash lost ACK/same-key retry, parallel cashiers, Add Order vs payment, paid-before-serving close guard, real Kitchen/Serving, simultaneous session close, Payments/Redis restart, Mock signed duplicate QR callbacks and Bill history through Gateway PASS.
- Real HTTP Flutter login/repository and verified PAID rendering: 1/1 PASS. This is a host widget test, not physical-device E2E.
- Flutter regression: 318 PASS, 1 SKIP, 0 FAIL; final Cashier/Cash focused tests: 19/19 PASS including 130-percent text at 360/393/430px; analyzer: 0 issues.
- Reporting/cash shift BLOCKED: synthetic paid/closed Bill receipts 210000 VND, reported gross 0, workforce cash ledger 0. No accounting workaround applied.
- operatingContainersUnchanged=true; runner removed only its test containers/network. Physical printer and real-bank verification NOT RUN.
