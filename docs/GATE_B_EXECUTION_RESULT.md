# Gate B - Payment Migration execution result

Completed: 2026-10-26 21:12:00 +07 (Asia/Saigon).
Target: PostgreSQL 18.4, localhost:5432/aicos_db, schema public.

**Gate B: COMPLETED / PASS. Gate A and Gate C: NOT APPROVED / NOT EXECUTED.**

The owner explicitly approved Gate B only. The reviewed forward SQL files were
executed unchanged, one stage at a time, with ON_ERROR_STOP and exit-code checks.
No role/grant action, Backend startup, rollback, data deletion, Mobile edit,
commit or push was performed.

## Pre-execution checks

- Reviewed backup archive SHA-256 and every manifest entry matched.
- Archive list was readable. Main and retained restore-test schema/data still
  matched the reviewed fresh backup before expansion.
- All ten forward/rollback SQL hashes matched Phase 5 before execution and again
  after completion. No migration SQL was edited.
- No native Backend/dotnet application process, API listener or active AICOS
  Windows service was found.
- Main database had no other backends, prepared transaction or writer lock.
  The same read-only guard was repeated before stages 01 through 06.
- Stage 00 ran successfully before any production schema change and reported
  aicos_db/public, read_only=on and zero financial rows.
- The source baseline was 37 public tables / 219 columns, including the same
  two applied Identity migration-history entries.

Backup used:
`C:\Users\ASUS\AppData\Local\AICOS\backups\20261026_205819\aicos_db.dump`.

Archive SHA-256:
`7948DA1CA43657142281BA70A1D52AAEDB59333CB5E77EDF4826250C4B1AA981`.

Retained restore baseline:
`aicos_restore_test_20261026_205819`.

## Warning observed and resolved by read-only inspection

The first precautionary check stopped because Docker Desktop/WSL had been
started since the prior final check. This initial check returned exit 1; it was
not a failed SQL migration stage, and no migration had run at that point.

Read-only follow-up confirmed all nine existing containers were exited, the
AICOS Compose project had no containers, native Backend/API processes were
absent and aicos_db had no other client or write transaction/lock. Thus the
owner's mandatory no-Backend/no-writer condition still held. Execution continued
with that condition verified; Docker itself was not stopped, started or reset.

This is a recorded difference from the earlier runbook's expectation that the
engine would remain off. It was not hidden or counted as an active Backend.
At completion docker ps still listed zero running containers. No new source of
writes appeared and no unrelated container was modified.

## Stage results

| Stage | Exit | Result and verified checkpoint |
|---|---:|---|
| 00_preflight.sql | 0 | PASS; correct read-only target and bills/items/payments counts 0 |
| 01_expand_columns.sql | 0 | PASS; 14 additive columns with reviewed types/defaults/nullability; total columns 233 |
| 02_create_payment_events.sql | 0 | PASS; 11-column table with UUID PK; total tables 38, columns 244 |
| 03_backfill.sql | 0 | PASS; exactly four UPDATE 0 responses; no actor/branch gaps |
| 04_indexes.sql | 0 | PASS; eight concurrent indexes, each indisvalid=true and indisready=true; definitions inspected |
| 05_contract_constraints.sql | 0 | PASS; 28 named constraints validated, seven strengthened NOT NULL columns verified, append-only trigger enabled and bound to the rejecting function |
| 06_verify.sql | 0 | PASS; all nine issue counts exactly 0; checked in addition to the psql exit code |

Stage 04 ran in autocommit mode, without an outer transaction. Every later stage
was held until the preceding output and verification had been inspected.
Stage 03 was guarded by a zero-row financial count immediately before execution;
its output was parsed to require exactly four UPDATE statements affecting zero
rows. No migration stage failed. No rollback or invalid-index cleanup ran.

## Before/after inventory

| Object | Before | After | Delta |
|---|---:|---:|---:|
| public tables | 37 | 38 | +1 payment_events |
| public columns | 219 | 244 | +25 |
| bills columns | 7 | 15 | +8 |
| bill_items columns | 5 | 5 | 0 |
| payments columns | 8 | 14 | +6 |
| payment_events columns | absent | 11 | +11 |
| public indexes | 57 | 66 | +8 reviewed indexes + new table PK |
| primary keys | 37 | 38 | +1 |
| foreign keys | 53 | 61 | +8 |
| business CHECK constraints | 0 | 20 | +20 |
| original logical data rows | 15 | 15 | 0 |

The 28 new named contract constraints are eight FKs plus twenty CHECKs.
PostgreSQL 18 also represents NOT NULL metadata in catalogs; those entries are
not miscounted as new business CHECK constraints above.

## All nine migration checks

| Check | issue_count |
|---|---:|
| invalid_indexes | 0 |
| invalid_bill_sources | 0 |
| unmapped_bill_branches | 0 |
| successful_payments_without_audit | 0 |
| successful_bank_transfers_without_evidence | 0 |
| missing_payment_columns | 0 |
| missing_payment_indexes | 0 |
| missing_payment_constraints | 0 |
| missing_append_only_trigger | 0 |

Additional verification went beyond these name/count checks:
- inspected all eight index definitions, uniqueness/partial filters, readiness
  and validity;
- inspected all 28 validated FK/CHECK definitions;
- verified the seven strengthened columns are NOT NULL;
- inspected enabled trigger definition and rejecting function body without
  executing trial UPDATE/DELETE operations against production.

## Existing data and ownership scope

All original 37 per-table row counts and sorted row digests match the backup.
The original 15 rows, including EF history, are unchanged. bills, bill_items,
payments and the new payment_events table all contain zero rows.

Schemas of the 34 original tables other than bills/bill_items/payments were
compared against the retained restore baseline using table-filtered schema-only
dumps, excluding randomized pg_dump restrict keys. They match. This also covers
the unchanged refunds table and all non-Payment business tables.

The EF history still contains exactly:
- 20261023040204_BaselineExistingIdentityTables, EF 8.0.31;
- 20261023040310_AddRefreshSessions, EF 8.0.31.

Payment SQL is manually deployed and does not add a fabricated EF history row.
Only Payments-owned objects were explicitly changed. Cross-service tables were
read or referenced by FKs; PostgreSQL's associated referential-integrity metadata
is expected. No cross-service business data was written.

## Execution evidence

Local evidence directory (outside Git):
`C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_b_20261026_210935`.

Files:
- 01_expand.log;
- 02_payment_events.log;
- 03_backfill.log;
- 04_indexes.log and 04_indexes_verify.log;
- 05_contract.log and 05_contract_verify.log;
- 06_verify.log;
- after-data.txt;
- final_inventory.log.

Stage 00/pre-execution observations are also recorded in the tool execution
transcript and the preceding final-check report. Evidence contains schema/status
output, not application row values or connection passwords.

## Gate A readiness and stop

Payment schema prerequisites for the full public-service-grants.sql now exist.
Gate A is ready for its own review/approval, not yet executed.

- All eleven service roles are still absent (verified read-only).
- No GRANT/REVOKE or secret/LOGIN provisioning was performed.
- No Backend API or Docker container is running at completion.
- Runtime authentication/privilege probes remain Gate A work.
- Services/API smoke tests remain Gate C work.
- Payment writes have not been released.
- Do not re-run 00_preflight.sql against the migrated schema: its baseline-only
  guard is expected to reject existing additive objects.
- Do not reapply 01-05; these scripts are intentionally fail-fast, not a general
  idempotent migration runner. Use 06 for read-only post-migration verification.

Stop here and await separate Gate A authorization.
