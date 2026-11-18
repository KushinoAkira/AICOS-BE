# Session Billing migration review/runbook (NOT APPROVED FOR OPERATING EXECUTION)

## Exact deltas

1. 01_ordering_expand.sql: EF 20261113125751_OrderingSessionCheckout, predecessor 20261106183340_OrderingCupFulfillmentFoundation; creates ordering.session_checkouts (9 columns, PK, state check, unique tenant/Bill).
2. 02_payments_expand.sql: EF 20261113125804_PaymentSessionBills, predecessor 20261021102642_ConstraintsAndIndexes; creates payment.session_bills, session_bill_orders, session_bill_attempts, session_bill_events. Own FKs only (RESTRICT), unique tenant/session, actor-scoped hashed request keys, references, partial one-PENDING-attempt/Bill and outbox scheduling index.
3. No Kitchen/Table DDL. Existing active-session unique index is reused, not recreated. No public remap or cross-service FK.

Scripts were generated OFFLINE with dotnet ef migrations script FROM TO --no-build using design factories pointing at port 1. Added target, approval, predecessor and absence guards and short lock/statement timeouts. New-table schema was migration-tested on disposable PostgreSQL, including EF Down/Up while NEW tables were empty. The guarded deployment wrapper itself has NOT been executed against an operating database. The stale docs/proposals/session-billing/001_payments_expand.PROPOSAL.sql now unconditionally fails; never run it as an alternate schema.

## Before requesting approval

- Read [the implementation report](../../SESSION_BILL_IMPLEMENTATION.md).
- Inventory target host/cluster/database/schema and EF history with read-only queries. NEW main currently uses separate service DBs, not aicos_db.public. The latest operating history inspection is blocked because main PostgreSQL was stopped; do not restart it without authorization.
- Verify all SQL SHA256 values match SHA256SUMS.txt. Review every statement; forward scripts CREATE only five new tables and indexes/constraints and INSERT two own EF history records. No existing business data UPDATE/DELETE/DROP/TRUNCATE.
- Obtain consistent backup of affected Ordering and Payments DBs plus tested restore into separate targets. Record counts/fingerprints and current settings/ACL. No backup/restore was run as part of this implementation.
- Confirm predecessor history exactly and no partial/new objects; guards fail on preexisting target tables. If partial deployment, inspect and repair under approval; do not blindly rerun or mark history.
- Keep SessionBilling.Enabled=false; stop writes/collection under an approved maintenance window. Existing real paid/pending legacy DineIn sessions must be reconciled separately; no automated backfill or charge migration exists.

## Approved execution order (commands are templates, NOT executed)

Use approved DBA credentials securely (pgpass/secret prompt); never place passwords in command strings/logs. Set connection identity through libpq environment. For EACH connection set PGOPTIONS to include aicos.expected_database=<approved_db> and aicos.expected_schema=ordering OR payment. Do not copy test names into an operating deployment blindly.

1. psql -X -v ON_ERROR_STOP=1 -f docs/migrations/session-billing/00_preflight.READ_ONLY.sql ; review metadata and own __ef_migrations_history in each target.
2. After explicit schema approval set aicos.bill_expand_approved=true in session options; psql -X -v ON_ERROR_STOP=1 -f docs/migrations/session-billing/01_ordering_expand.sql on Ordering target only.
3. Verify Ordering objects/history. Then run 02_payments_expand.sql on Payments target only with the matching options. Each script is one local transaction. There is no transaction spanning the two DBs.
4. Under SEPARATE permissions approval set aicos.bill_grants_approved=true; run 03_service_grants.PROPOSAL.sql once per service DB/schema. It adds SELECT/INSERT on new owned tables and column-limited UPDATE only. No ownership, role/password changes, DELETE, TRUNCATE, REVOKE or cross-service grants. No sequences required (UUID).
5. Run 04_verify.READ_ONLY.sql on each DB; verify row counts remain zero, PK/checks/FKs/index definitions/history and real least-privilege service login. Schema/role verification is mandatory, not implied by health live.
6. Deploy approved Backend build with flags still OFF and MigrateOnStartup=false. Set separate SessionBilling__MachineKey securely, existing Services__Order/Payment/Kitchen__BaseUrl correctly. Default DispatcherEnabled=true; do not disable retry worker when collecting money.
7. Controlled read-only smoke first. Only after explicit feature/payment approval enable Ordering, Payments, Kitchen, Table consistently. Client rollout must include new Bill flow. No test-provider setting on operating DB. Production QR remains unavailable until a real provider is added.
8. Run separately approved test-data-only E2E, monitor PAID events/leases/ACK/reconciliation and serving/close. Do not use genuine customer orders as a smoke fixture.

## Rollback

Preferred runtime rollback: suspend new collection, retain the ledger/fences and reconcile in-flight intents/received money. Disabling flags alone does not make legacy per-order payment safe for sessions already frozen/paid under Bills. Quarantine these sessions; retain read-only receipt access under a separately approved procedure. Do not switch live clients back to per-order collection for those sessions.

90_payments_rollback.sql and 91_ordering_rollback.sql are DESTRUCTIVE EF Down scripts. Both require aicos.bill_rollback_approved=true and ALL new owned tables empty. They DROP only the new tables and DELETE only the corresponding new EF history row, never old business tables. Tested only empty-new-table rollback; once any ledger/fence/audit exists, the guard refuses. Use data-preserving forward repair/reconciliation rather than dropping financial evidence. Never auto-run rollback when a stage fails.

## Risks / stop conditions

Missing history, wrong DB/cluster, existing target objects, active writers, unexpected schema/ACL, transaction timeout, invalid money proof or unresolved legacy payments: STOP. Additive DDL locks are limited to newly created objects plus schema/catalog activity; short timeouts reduce wait risk but do not replace a maintenance plan. Backups, least-privilege deployment rehearsal and full service E2E remain deployment prerequisites.

SQL and checksum files in this directory are pinned to LF by local .gitattributes so Windows checkout does not invalidate reviewed SHA256 values.
