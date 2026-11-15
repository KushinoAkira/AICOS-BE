# Kitchen Barista additive migration (pending operational approval)

Date: 2026-11-14. EF migration 20261114041318_KitchenBaristaWorkflow; predecessor 20261108121518_KitchenEarlyPayments. Never edit earlier migrations. Two new Kitchen-owned tables / 33 columns, two PKs, three own-schema FKs, four CHECKs, six secondary indexes (three unique, including one partial SLIP index). Existing Cup/Recovery/order/payment tables are not modified. No cross-service FK or business-data backfill.

## Exact order (DBA, after separate deployment approval)

1. Leave BaristaWorkflow:Enabled=false and Database:MigrateOnStartup=false. Stop new print/acceptance writes before any rollback. Inventory active workers and take/verify a pg_dump custom-format backup of the **actual Kitchen database**, restoring a copy separately. These scripts do not infer the target from old aicos_db.public documentation: current source uses per-service kitchen schema.
2. Verify SHA256SUMS.txt against the reviewed revision. Set aicos.expected_database, aicos.expected_schema='kitchen' and aicos.expected_cluster to the inspected target values in each SQL session. Scripts compare current_database and pg_control_system; this requires a DBA with permission to inspect the cluster. Never supply service passwords in argv/source.
3. Run 00_preflight.READ_ONLY.sql with ON_ERROR_STOP=1. Existing/partial tables or an already recorded migration stop preflight; inspect instead of recreating. Confirm exact predecessor and current EF history.
4. Only after approval set aicos.barista_expand_approved='true' and run 01_expand.sql. It is a single transaction generated from EF migration Up. CREATE new tables/constraints/indexes plus one EF history insert; no UPDATE/DELETE/DROP of business data. Re-running is deliberately refused and rolled back, not silently accepted.
5. Only after approval set aicos.barista_grants_approved='true' and run 02_service_grants.PROPOSAL.sql. It revokes/replaces rights **only kitchen_svc on the two new tables**. SELECT/INSERT plus completion columns on acceptance and lifecycle/lease columns on jobs; no DELETE/TRUNCATE, no payload/acceptance-owner UPDATE, no sequence, ownership, role creation, PUBLIC or other-service ACL change. Existing CONNECT/USAGE are prerequisites.
6. Run 03_verify.READ_ONLY.sql. Check actual columns/FKs/index definitions, EF history and effective inherited rights. An inherited broader role must be investigated; do not widen or revoke other roles automatically.
7. Deploy compatible source while flag remains OFF; separately approve enablement only after hardware/legacy/client acceptance.

Example invocation is psql -X -v ON_ERROR_STOP=1 -f <script> with the above settings supplied securely for that session. These instructions are not authorization to run against any operating database.

## Rollback

Preferred rollback after any acceptance/print activity: disable the new feature/worker, preserve both tables and audit, deploy the previous compatible app configuration after legacy workflow review. Never erase print evidence or automatically retry UNKNOWN jobs.

90_rollback.EMPTY_ONLY.sql requires the same identity guard and aicos.barista_rollback_approved='true'; it refuses when either table contains any row. Only then does it DROP these two newly added empty tables and remove this migration's history row, in one transaction. It is destructive DDL and always needs explicit DBA approval. Never run EF Down directly on nonempty tables: the generated EF Down lacks this extra operational guard. Backup restore is to a separate target first; do not overwrite an operating cluster or other services' databases.

## Risk / test scope

New FK creation briefly takes locks on existing Kitchen ticket metadata; deploy in a controlled window, use a DBA-defined lock/statement timeout and stop on errors. Disk/WAL sizing, production load and permissions inheritance need actual-target review. No silent partial recovery: inspect schema/history after a failed stage before resuming.

Scratch test runner: node scripts/tests/session-billing-stack.mjs --barista. It checks a nonce-labelled Docker network, exact PostgreSQL cluster, no DB host port and tmpfs storage; uses random test-only credentials; applies predecessor then reviewed SQL; verifies model/ACL, rejects duplicate expand and payload/DELETE privilege; restores a separate database, rolls back/reapplies empty tables; later refuses rollback with actual test audit rows. It compares preexisting container identity/state before/after and removes only its own containers/network. No Windows PostgreSQL connection or operating data is used.

Actual deployment ACL is not certified by scratch role tests: the source Docker initializer may give service roles database/schema ownership. A DBA must separately inspect ownership and inherited memberships; column grants alone cannot constrain an owning/admin role. Stop deployment for that review instead of automatically changing ownership or other roles. Only the isolated non-owner kitchen_svc matrix was executed here.

Commit preparation normalized SQL line endings to LF and removed empty EOF lines; assertions verified that the executed SQL text is identical after newline normalization. SHA256SUMS was regenerated and .gitattributes pins these reviewed SQL/manifests to LF for identical checksums on Windows/Linux. No SQL statement or migration operation changed.
