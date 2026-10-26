# Gate B - Final verification before execution

Historical pre-execution snapshot. Gate B was later explicitly approved and
completed; see GATE_B_EXECUTION_RESULT.md for the current deployed state and
the Docker runtime-state warning. Do not reapply baseline scripts from here.

Checked: 2026-10-26, Asia/Saigon. Last database evidence: 21:00:09 +07.
Target: PostgreSQL 18.4, localhost:5432/aicos_db, schema public.

**Decision: PASS for the checks below at the recorded time. Execution remains
NOT APPROVED. No production migration, role/grant action or service startup ran.**
The owner approved this final verification only. B -> A -> C order is accepted;
approval to execute each gate is still independent.

## PASS/FAIL results

| Condition | Result | Evidence |
|---|---|---|
| Fresh backup created | PASS | New custom archive created at 20:58:19; existing backup directories preserved |
| Backup manifest and archive | PASS | SHA-256 manifest validated before restore and rechecked after restore; archive readable |
| Real restore into NEW test database | PASS | aicos_restore_test_20261026_205819; no existing DB overwritten |
| Restored schema | PASS | Normalized schema dump matches, including table/PK/FK/index/constraint DDL |
| Restored data | PASS | All 37 public tables and 15 rows match source counts and ordered row digests |
| Source stable through backup and final check | PASS | Before/after source fingerprints match; final production fingerprint still matches fresh archive evidence |
| SQL checksums | PASS | All ten migration/rollback hashes match frozen Phase 5 values |
| Final executable SQL contents | PASS | All seven files 00-06 reviewed in full; exact targets listed below |
| No destructive statement in B | PASS | No DROP, TRUNCATE, DELETE statement, data overwrite, or rollback file in B command set |
| Payment ownership scope | PASS | Explicit write/DDL targets are Payments-owned; cross-service access is read/FK reference only |
| Native Backend processes | PASS | No AICOS application process, dotnet process, Windows service or AICOS scheduled task found |
| API listeners | PASS | None on 8080 or 5101-5111 |
| Docker runtime stopped | PASS | Docker backend/Desktop process absent; com.docker.service Stopped; WSL docker-desktop Stopped |
| Other database clients/backends | PASS | 0 clients, 0 active clients, 0 other backends in aicos_db |
| Open/prepared transactions | PASS | 0 other open transactions; 0 prepared transactions |
| Write-lock inventory | PASS | 0 RowExclusive/ShareRowExclusive/Exclusive/AccessExclusive locks from other backends |
| Other identified DB writers | PASS | 0 enabled subscriptions; only plpgsql installed; no pgAgent job table |
| Preflight 00 | PASS | READ ONLY on aicos_db.public; bills=0, bill_items=0, payments=0 |
| Proposed object names | PASS | No collisions for eight indexes, 28 named constraints, append-only function or trigger |
| EF history preserved | PASS | Same two applied Identity migrations; no Identity migration source diff |
| Approval boundaries | PASS | Only checks performed; no B/A/C execution or automatic transition |

These are timestamped observations, not a permanent write barrier. Recheck
processes, listeners, database connections/transactions and preflight immediately
after execution approval and before 01. Any new writer or unexpected row/object
invalidates this PASS and requires stopping for review.

Docker CLI initially failed because its engine pipe does not exist while Docker
is stopped. This was not counted as a successful container listing. Runtime
stoppage was established independently from Windows process/service and WSL
state. Saved container restart policies cannot be inspected with the engine off.
Do not start Docker before Gate C: doing so could auto-start saved containers.
No Docker reset, startup or volume operation was performed.

## Fresh backup and restore evidence

Backup directory:
`C:\Users\ASUS\AppData\Local\AICOS\backups\20261026_205819`.

Archive: `aicos_db.dump`  
SHA-256:
`7948DA1CA43657142281BA70A1D52AAEDB59333CB5E77EDF4826250C4B1AA981`.

New retained test database:
`aicos_restore_test_20261026_205819`.

Restored per-table count/digest evidence:
`restored-data.txt` SHA-256:
`7A968EA53F17971016734E80930B24762C1D47A15227C6AB6C515A5F392C3D6F`.

The authorized commands actually used for this verification were the existing
Backup-Aicos.ps1 and Restore-AicosDrill.ps1, with the new directory/database
above. Source backup connections forced default_transaction_read_only=on.
The restore wrote only to the NEW test database, which remains for inspection.

The restore excluded ownership/ACL replay as designed. Schema comparison removed
only randomized pg_dump psql restrict keys. This proves schema/data recovery
from this local archive; it does not prove role/ACL restoration or off-machine
disaster recovery. No claim of a new PostgreSQL migration rehearsal is made in
this check; frozen migration rehearsal results remain those of Phase 5/5.1.

## Final SQL content and scope review

| File | Reviewed behavior | Explicit write/DDL targets |
|---|---|---|
| 00_preflight.sql | READ ONLY checks and inventory | None |
| 01_expand_columns.sql | Adds eight bill columns and six payment columns | public.bills, public.payments |
| 02_create_payment_events.sql | Creates 11-column append-only audit table's base/PK | public.payment_events |
| 03_backfill.sql | Four UPDATE statements; rejects unknown actors/branches | public.bills, public.payments |
| 04_indexes.sql | Creates eight concurrent indexes | bills, bill_items, payments, payment_events |
| 05_contract_constraints.sql | Adds/validates 28 FKs/checks, NOT NULL, function/trigger | bills, bill_items, payments, payment_events, public.reject_payment_event_mutation(), trg_payment_events_append_only |
| 06_verify.sql | READ ONLY schema/index/constraint/data verification | None |

Cross-service dependencies are intentional and explicit:
- 00 reads orders and table_sessions to check branch mapping.
- 03 reads orders/table_sessions as backfill sources, but does not UPDATE them.
- 05 adds Payment FKs referencing users/branches; no ALTER/UPDATE of those
  business tables is issued. PostgreSQL creates the associated referential
  integrity metadata/triggers as part of those FKs. That dependency can restrict
  future deletion of referenced identities/branches; it is not a runtime query
  privilege or transfer of table ownership.
- bill_items.order_item_id and bill source relationships remain baseline links.
- No schema switch, role, GRANT/REVOKE, EF history write, unrelated table creation,
  or use of Database.sql is part of this deployment.

The text DELETE occurs in `ON DELETE NO ACTION` and
`BEFORE UPDATE OR DELETE`. These specify referential restrictions and a trigger
that raises an exception to prevent audit mutation. They do not delete rows.
There is no ON DELETE CASCADE introduced.

The UPDATE statements in 03 are REAL mutation statements, not read-only checks.
With the confirmed empty financial tables, all four must affect 0 rows. Recheck
that condition before migration; do not describe the script as universally
non-mutating. No actor identity is fabricated.

The rollback files 90/91/92 were also read in full:
- 90 drops newly added contracts/trigger and weakens nullability.
- 91 drops new indexes.
- 92 drops payment_events and additive audit columns and can lose audit data.
They are explicitly EXCLUDED from all commands below. No destructive rollback
is authorized.

## Exact proposed commands after a separate Gate B approval

The following is a reviewed command inventory, NOT a record of commands already
executed and NOT permission to execute them. Workdir:
`D:\ki9\sep490\Sep490\AICOS-BE`.

Use a protected credential provider/PGPASSFILE. No password is embedded below.

```powershell
Set-Location 'D:\ki9\sep490\Sep490\AICOS-BE'
$gateBPsql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
$gateBConnection = @(
    '-X', '-w', '-h', 'localhost', '-p', '5432',
    '-U', 'postgres', '-d', 'aicos_db', '-v', 'ON_ERROR_STOP=1'
)
powershell -ExecutionPolicy Bypass -File infra/postgres/operations/Test-PaymentChecksums.ps1
if ($LASTEXITCODE -ne 0) { throw 'Checksum gate failed' }
```

Before continuing: compare the archive hash above again, and repeat the read-only
connection/transaction/write-lock and host process/listener checks from this
report. They must still show no application writers. Keep Docker and Backend
stopped. If the source has changed, stop and create/review a new backup instead
of relying on this archive.

Then execute ONE stage at a time, inspect its output, and stop on failure:

```powershell
& $gateBPsql @gateBConnection -f docs/migrations/payments/00_preflight.sql
if ($LASTEXITCODE -ne 0) { throw 'STOP: preflight failed' }

& $gateBPsql @gateBConnection -f docs/migrations/payments/01_expand_columns.sql
if ($LASTEXITCODE -ne 0) { throw 'STOP: expand failed' }

& $gateBPsql @gateBConnection -f docs/migrations/payments/02_create_payment_events.sql
if ($LASTEXITCODE -ne 0) { throw 'STOP: audit table failed' }

& $gateBPsql @gateBConnection -f docs/migrations/payments/03_backfill.sql
if ($LASTEXITCODE -ne 0) { throw 'STOP: backfill failed' }

& $gateBPsql @gateBConnection -f docs/migrations/payments/04_indexes.sql
if ($LASTEXITCODE -ne 0) { throw 'STOP: indexes failed' }

& $gateBPsql @gateBConnection -f docs/migrations/payments/05_contract_constraints.sql
if ($LASTEXITCODE -ne 0) { throw 'STOP: contract failed' }

& $gateBPsql @gateBConnection -f docs/migrations/payments/06_verify.sql
if ($LASTEXITCODE -ne 0) { throw 'STOP: verification failed' }
```

Manual checkpoints before moving to the next line:
1. After 00: target aicos_db/public, read_only=on, financial counts zero.
2. After 01: exact 14 columns/types/defaults; original columns retained.
3. After 02: exact 11-column table/PK; no replacement of an existing table.
4. After 03: every UPDATE reports 0; actors/branches have no null rows.
5. After 04 succeeds: inspect all eight named indexes and their definitions/
   validity before 05. The -f command executes the statements consecutively;
   ON_ERROR_STOP halts on the first failed statement, not at manual checkpoints
   between successful statements. Do not use --single-transaction or wrap 04
   in BEGIN.
6. After 05: 28 named constraints validated, strengthened nullability correct,
   trigger enabled and bound to the rejecting function.
7. After 06: all nine issue counts zero; definitions and trigger state inspected.
   psql exit 0 alone does not turn nonzero SELECT issue counts into a PASS.
8. Recompare logical data counts/digests for the original 37 tables, original
   table definitions and EF history. The new payment_events table adds one
   empty table; only the approved Payment structure may differ. Original data
   stays.
9. STOP and report Gate B results. Await Gate A approval separately.

Stage 04 is non-atomic across statements; a failure may leave valid earlier
indexes and an invalid new index. Its current frozen SQL has no explicit
lock_timeout/statement_timeout. Stages 01/02 use five-second lock and 60-second
statement timeouts; 03/05 use five-second lock and five-minute statement
timeouts. Do not silently change these scripts/checksums.

## Failure recovery and stop conditions

On any timeout, error, nonzero issue count, nonzero unexpected backfill count,
writer appearance, object collision or scope drift:
- stop before the next statement/file;
- keep Backend/Docker stopped and preserve schema/data/evidence;
- report which stages committed and inspect catalog state read-only;
- prepare a forward fix or an explicit object-specific repair for review;
- do not run 90/91/92, drop an invalid index, overwrite aicos_db, delete data,
  grant permissions, create roles, or start services automatically.

Existing data is not overwritten by a backup restore as a routine recovery.
If a restore is ever needed, propose an isolated replacement and cutover plan
with its own explicit authorization.

## Frozen SQL hashes

| File | SHA-256 |
|---|---|
| 00_preflight.sql | 92A7EA85B4A471CF004EF47108322D184AB5EEFE8EB310C347791B1E813A3DA9 |
| 01_expand_columns.sql | 95D334A972F679C271DC19A2B4D5FD338E3551EC616A7E0E64BF5C628E849224 |
| 02_create_payment_events.sql | 744D15D898A5120208747D2BA62043836D92E4392C0C425B92D848B04A313DA9 |
| 03_backfill.sql | E87ABB3C0290C1A82E0F629F10A21D208EF0FD0D672CF9A65574E019A8372698 |
| 04_indexes.sql | CB26917BC582259B8FB8426B040E2BB4C58E3811D811A9A97DFA8D964A1369FD |
| 05_contract_constraints.sql | 3B48FE526DE75397DAAE6BF0EF5B8E181AE60A6FBD9786D425FF4F2D2AE15A46 |
| 06_verify.sql | EF6E8ED625BE76E9C850890008BA970CAE24D17E0EF1FC3428A7CF17993220F8 |
| 90_rollback_constraints.sql | D3140F9AB6827A0A858F0F4DF68034041EB44E4AEF3B746BF915241C21DC1CEB |
| 91_rollback_indexes.sql | 7F2576751266B328670F507DC4C32B09007971BE78D073C3B45DA2CBBE43C9CD |
| 92_rollback_schema.sql | D81291D558B40A301B7E67E7BBFF90CB0A058C9EB2FD1759A8EF0588C8DFE3B0 |

Only documentation was changed in AICOS-BE for this final-check task. Fresh
backup artifacts and the new restore-test database were created as explicitly
requested. Existing databases/data and Docker volumes were not removed.
No commit/push, Mobile edit, Gate B execution, Gate A or Gate C action occurred.
