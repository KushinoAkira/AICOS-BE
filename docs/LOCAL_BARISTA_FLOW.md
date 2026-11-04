# Local Barista accounts and Cashier to Bar verification

Date: 2026-11-04 (Asia/Saigon).

## Scope and accounts

User-approved local fixtures in Docker project `aicos-main`, tenant
`aicos-cashier-local`. Accounts were created through Identity API, not SQL.

| Name | Email / username | Branch |
| --- | --- | --- |
| Nguyễn Hoàng Phúc | phuc.barista@aicos-local.test / phuc.barista | Hồ Con Rùa (Q.3) |
| Trần Ngọc Mai | mai.barista@aicos-local.test / mai.barista | Nguyễn Huệ (Q.1) |
| Lê Gia Bảo | bao.barista@aicos-local.test / bao.barista | Phan Xích Long (Phú Nhuận) |

All three: Barista, correct branch, successful login and GET me/queue,
MustChangePassword=false after the normal password-change endpoint. The approved
local fixture password matches the named Cashier fixtures; no password/token is
included here. Current-user Windows DPAPI checkpoints are outside Git. Existing
accounts, role permissions, schema and database credentials were not changed.

## Runtime and bug fix

Docker was stopped initially. Started existing main PostgreSQL, Redis, Identity,
Menu, Table, Ordering, Kitchen and Gateway containers after checking the project
label and migration-disabled configuration. Other stacks were not started.

The first POST orders returned HTTP 500. Logs identified
NpgsqlRetryingExecutionStrategy rejecting a user-initiated transaction outside
its execution strategy. Database counts remained zero orders / zero sessions.

Changed `Features/Orders/OrderEndpoints.cs`: execute the complete counter-order
transaction inside Database.CreateExecutionStrategy().ExecuteAsync; clear tracking
between attempts; retain a prepared order across attempts; recognize a prior
commit; publish the created event after the retry/transaction boundary. Preserve
request-key conflict checks, advisory locks, API contracts and schema. A completed
request replay still returns the original order without publishing a second event.
This is not an outbox implementation; crash recovery and injected lost-commit /
Redis-delivery failure tests are not certified by this verification.

Rebuilt/recreated only Ordering with the main runtime overlay. Verified its
connection string and JWT configuration matched the previous container in memory;
no secrets printed. MigrateOnStartup=false remained in effect.

## Real API and PostgreSQL verification

One explicitly marked local order at S10, Hồ Con Rùa:

- Order: `ORD-20261104-F68F64` / `191f386a-2d0a-4e39-a27d-4de690c8289c`.
- Session: `7e51878c-bfe1-499a-a3e3-18fa2e511494`.
- Ticket: `77ff46b2-1da8-4379-8197-d980b159e402`.
- Request key: `local-barista-e2e-09e7e23fe6ef4d28aac2a992b759c7ab`.
- One Bạc xỉu, server price 32,000 VND, defaults M / 100% sugar / normal ice.

| Check | Result |
| --- | --- |
| Table available, no existing open S10 session | PASS |
| Cashier creates DineIn order and real table session | PASS |
| Same Idempotency-Key returns the same order | PASS |
| Redis order event creates Kitchen ticket | PASS |
| Unpaid dine-in is eligible for the Bar queue | PASS |
| Cashier attempting Brewing | PASS: 403 |
| Barista of another branch reading ticket | PASS: 403 |
| Barista Brewing -> Order Preparing | PASS |
| Barista ReadyForPickup -> Order Ready | PASS |
| Cashier deliver records timestamp but remains Unpaid | PASS |
| Options and note reach Kitchen item | PASS: PostgreSQL SELECT |
| Cancel marked test order -> Kitchen Cancelled | PASS |
| Session due=0 after cancellation; explicit staff close | PASS |

Initial cleanup incorrectly expected auto-close after the only round was
cancelled. Existing business logic intentionally does not mark an all-cancelled
session as Paid. Used POST sessions/{id}/close after verifying due=0. No logic or
schema change was required for this case.

Final PostgreSQL SELECT: one retained Cancelled/Unpaid order with delivery metadata,
one Closed/Staff session, one Cancelled/unpaid Kitchen ticket, zero payment rows.
No open test session or queued test ticket remains. History was retained; no SQL
deletion or fake payment was used. The order is only a simulated local workflow,
not evidence that a physical drink was prepared or handed over.

## Tests and limits

- Docker Ordering build: PASS.
- Initial MobileCounterTests: 6 PASS, 0 skipped (EF InMemory/fake clients).
- Ordering / QR / Kitchen / MobileCounter selection: initially 103 PASS, 0 skipped.
- Added regression: replay publishes only one creation event; unpaid delivered
  round blocks session close until cancelled; explicit close leaves payment Unpaid.
- Final related selection: 104 PASS, 0 failed, 0 skipped. Existing warnings CS0628 (WorkforceReviewTests) and CS8620 (OrderTests) remain outside this change.
- Real runtime checks above use actual Gateway, PostgreSQL and Redis; normal login
  issued tokens. They are distinct from the EF InMemory/self-signed test harness.
- No payment/cash/refund, takeaway-paid flow, other-branch create-order flow,
  transient-fault injection or physical-device UI E2E was performed.
- Mobile application code was unchanged in this task. No migration, ACL change,
  database reset, commit or push.