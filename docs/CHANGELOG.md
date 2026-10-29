# Backend Changelog

## 2026-10-29 - Gate D1-C C1 preparation PASS

- Added apps-only Identity/Gateway TEST Compose, separate single-route Gateway
  config, empty config-validation env file, stage guards and READ ONLY preflight.
- Validated native Compose without interpolation/main env loading, parsed scripts
  with zero errors; 60 synthetic metadata/config boundary cases PASS. Fixed an
  env-guard operator-precedence issue caught by a negative test before runtime use.
- Real PostgreSQL READ ONLY prerequisites PASS: correct isolated cluster/schema,
  eleven NOLOGIN/password-null roles and approved Identity ACL; no fixtures/accounts.
- Pinned local image IDs and artifact hashes in private preparation manifest;
  reserved signing-key cache presence/DACL only, no secret decrypt/distribution.
- No app startup, role/login/password changes, SQL mutation, JWT or business tests.
  Gate C and test infra unchanged; stopped before C2. Added report/usage docs.

## 2026-10-29 - Gate D1-B isolated schema and ACL PASS

- Published preflight before apply; pinned container/cluster/network/volumes and
  exact reviewed schema/guard/role/ACL hashes. No Windows database connection.
- Applied schema-only plus eleven NOLOGIN roles/ACL in one transaction. Initial
  verifier alias collision rolled back completely; corrected alias and used a
  fresh preflight/evidence directory. No reset, DROP or automatic rollback script.
- Verified 38 tables/244 columns, 38 PK/61 FK/66 indexes, 20 CHECKs, no rows and
  exhaustive effective ACLs. Reviewed eight PG18.4/18.6 CHECK array-cast formatting
  differences; verified equivalence on real PG, preserved all other definitions.
- Real SET ROLE SELECT probes PASS; 28 metadata guard and 8 schema-text guard cases
  PASS. No business test suite, credentials/LOGIN, fixtures, application users or JWT.
- Gate C thirteen containers unchanged; main/old Windows test DB and Mobile untouched.
  Added D1-B report/runner/schema reviewer; stopped pending Identity testing approval.

## 2026-10-29 - Gate D1-A isolated infrastructure PASS

- Added standalone pinned PostgreSQL18/Redis Compose, internal network and new
  named volumes. Used local images, fresh DPAPI-protected test secrets, no main
  credentials/key reuse. Reserved signing key only; no JWT/application account.
- Added target/resource/container/cluster guards and readiness-only runner;
  28 synthetic metadata guard cases PASS plus actual PG READ ONLY and Redis
  PING/empty-state readiness PASS. No business test suite ran.
- Fixed PowerShell CLR setter/JSON-array/stdin-LF issues found during validation.
  Removed ineffective internal-network host publication; SQL now uses pinned
  DockerExec. Recreated only new test PostgreSQL, retaining volume/system ID.
- Prepared schema manifest/checker and guarded roles/ACL SQL for review only.
  Test DB remains 0 application tables/0 service roles; schema/ACL not applied.
- Preserved all Gate C container IDs/images/start times and main/old Windows
  databases unconnected. Updated Gate D plan and D1-A result, stopped before D1-B.
  No migration/seed, Mobile change, production credential/ACL change or commit.


## 2026-10-29 - Gateway routing smoke PASS; overall Gate C PARTIAL

- Audited actual YARP 2.3.0 architecture (no Ocelot), unchanged packaged routes,
  auth middleware, methods/path handling and safe startup. No config/code fix.
- Started only Gateway with --no-deps. DNS/TCP and metadata routing to all eleven
  services PASS; invalid bearer rejected on each route. Eleven protected GETs
  return 401 directly without JWT and through Gateway with missing/invalid JWT.
- Root/live GET 200; Gateway ready/Swagger/OpenAPI absent (expected 404, N/A).
  No observed DNS, timeout, proxy or 502/503 error flags.
- Prior 25 unique tables match AI baseline and remain unchanged; database stays
  aicos_db.public, 38 tables/244 columns. Ten .NET services, AI and Redis retain
  IDs/images/StartedAt and configs; Redis healthy.
- Added per-route/auth/component matrices and untested business list to report.
  Runtime/routing scope PASS; overall Gate C PARTIAL pending authorized business
  evidence and placeholder features. Stopped before next phase; no mutation,
  JWT/credentials/ACL/Mobile change, commit/push or dependency restart.


## 2026-10-29 - AI placeholder runtime/dependencies PASS

- Audited FastAPI/Uvicorn Python image, startup/config/routers/requirements;
  nine packaged app files match reviewed source. No startup mutation/model work.
- Verified AI DNS failure and dynamic host-gateway/TCP fix, then added only AI
  mapping/loopback 5111 and started only ai with existing credentials/JWT.
- Safe health/info/docs/ReDoc/OpenAPI GETs all 200. Readiness proves TCP only;
  separately verified real ai_svc READ ONLY auth/ACL and Redis PING from AI.
- Business routes are POST 501 stubs; protected authentication/model/LLM work
  NOT TESTED. No POST/token generation/unit suite was run or claimed.
- Prior 25 unique tables unchanged, matching Customer baseline. Ten .NET
  services and Redis retain IDs/images/StartedAt; existing configs preserved.
- Updated execution report and stopped before Gateway. No DB/ACL/security/
  credentials/JWT/Mobile change, migration/seed, heavy model task or commit/push.


## 2026-10-29 - Customer skeleton runtime/connectivity PASS

- Audited empty Customer DbContext/commented feature mapping and startup:
  no business endpoints or automatic seed; business JWT/own-table checks N/A.
- Verified DNS failure and dynamic host-gateway/TCP fix before adding only the
  Customer mapping and loopback port 5110. Started only customer, migration=false.
- Real customer_svc READ ONLY connection, CONNECT/public USAGE and approved zero
  table/column SELECT ACL PASS; health/metadata/Swagger UI/OpenAPI GETs all 200.
- Prior 25 unique table fingerprints match Reporting baseline and remain
  unchanged. Captured matching before/after IDs/images/StartedAt for nine prior
  services and Redis; Redis healthy and previous runtime environments preserved.
- Updated execution report; stopped after Customer. 10/10 .NET runtime PASS,
  four skeletons; Gate C still pending AI/Gateway. No DB/security/credentials/
  JWT/Mobile change, migration/seed, other service restart/start or commit/push.


## 2026-10-29 - Reporting skeleton runtime/connectivity PASS

- Reviewed Reporting startup/empty DbContext: no business API or automatic seed;
  business JWT checks/owned-table fingerprint are N/A.
- Verified DNS failure and dynamic host-gateway TCP fix before adding only the
  Reporting mapping and loopback port. Started only reporting, migration=false.
- Real reporting_svc READ ONLY connection and zero table/column SELECT ACL PASS;
  health, metadata, Swagger UI and OpenAPI GETs returned 200; checked log flags clear.
- Prior 25 unique table fingerprints match Workforce baseline and stay unchanged.
  Eight prior service IDs/images/StartedAt and Redis identity/start stay preserved.
- Updated execution result; stopped after Reporting. No DB/security/credentials/
  JWT/Mobile change, migration/seed, further service startup or commit/push.


## 2026-10-29 - Workforce skeleton runtime/connectivity PASS

- Confirmed owner-restored Docker/seven previous services healthy before
  continuing; preserved their container IDs/images/StartedAt.
- Diagnosed Workforce DNS failure, verified dynamic host-gateway/TCP 5432 and
  added only Workforce mapping/loopback port to Gate C Compose override.
- Started only Workforce, migration=false; actual workforce_svc READ ONLY
  connection and zero table/column SELECT matrix PASS.
- Health/metadata/OpenAPI GETs returned 200; checked log error flags absent.
  No business endpoints/entities yet: business auth/owned-table checks N/A.
- Prior 25 unique tables match Inventory baseline and remain unchanged.
  Updated execution report and stopped after Workforce; no main DB mutation,
  grant, credential/JWT reset, Mobile edit, further service start or commit/push.


## 2026-10-27 - Inventory skeleton runtime/connectivity PASS

- Audited empty Inventory DbContext/commented feature mapping: no stock APIs or
  mapped Inventory dataset. Business auth/own-table fingerprint are N/A, not PASS.
- Confirmed DNS failure and dynamic host-gateway TCP/DNS fix; added Inventory-
  only mapping/loopback port and started only inventory, migration=false.
- Actual inventory_svc READ ONLY connection and approved zero table/column
  SELECT matrix pass. Health/metadata/Swagger GETs return 200; logs clean of
  checked critical patterns. No fake 401 endpoint, grant, token or business data.
- Previous 25 unique table fingerprints match Kitchen baseline and remain
  unchanged. Six passed services retain IDs/images/start times/config.
- Updated report and stopped after Inventory. No stock transaction, migration,
  seed, DB/security/Mobile/credentials change, Gateway/other startup, commit/push
  or Phase 6. No unit/mutation integration-suite result claimed.

## 2026-10-27 - Kitchen runtime PASS after owner restored stack

- Verified user-restored five services live/ready=200, migrations=false and
  Payment mutations=false; did not restart/recreate any passed service/Redis.
- Kitchen native READ ONLY connection and SELECT matrix pass: exactly three
  legacy Ordering tables readable, 35 ungranted tables recorded without grants.
- Deployed already-prepared Kitchen host-gateway mapping; started Kitchen only.
  DNS/TCP, health/metadata/Swagger GETs pass and queue rejects missing/invalid
  bearer with 401. Valid-JWT business/mutation coverage remains NOT RUN.
- Current 25 unique table fingerprints equal the previous Payment baseline and
  stay unchanged after Kitchen; three Kitchen tables overlap Ordering, giving
  28 role/table observations rather than 28 unique tables. Schema stays 38/244.
- Updated report with current restored baselines and stopped after Kitchen.
  No Gateway/other startup, database/security/credentials/Mobile change,
  commit/push or Phase 6. No unit/mutation integration-suite result claimed.

## 2026-10-27 - Read-only AICOS stop incident investigation

- Retained proxy logs confirm six compose/v5.5.1 stop requests at 21:55:01,
  followed by normal exit 0/no OOM. Caller identity/exact command unproven.
- Correlated the later five-minute idle/Resource Saver engine cycle at 22:00;
  no completed down, Windows reboot or incident OOM evidence. Current low free
  RAM is a capacity observation, not the established stop cause.
- Added DOCKER_STACK_STOP_INVESTIGATION_20261027.md and Gate C summary. Only
  documentation changed; no container control, SQL, credentials/config/Mobile
  change, commit/push or runtime resume. Gate C remains paused pending approval.

## 2026-10-27 - Kitchen preparation paused by owner before startup

- Audited Kitchen's three legacy Ordering mappings/SELECT matrix; same-network
  host-gateway DNS/TCP probe passed and mapping added to kitchen only.
- Runtime operation stopped at preflight: five passed services and Redis had
  gracefully exited (code 0, no OOM). No Kitchen start, credentials load, SQL,
  API smoke or fresh fingerprint executed; no runtime FAIL is inferred.
- Owner explicitly paused to inspect the stack. No further service/database
  action, grant, migration, credential reset, Mobile edit or commit/push; Gate C
  remains paused, with no Gateway/Phase 6 transition.

## 2026-10-27 - Gate C Payment runtime PASS, mutations remain disabled

- Verified Payment's host DNS failure and dynamic host-gateway DNS/TCP fix;
  changed only Payment mapping and started only payment with --no-deps.
- Actual payment_svc READ ONLY connection and active-table probes pass. Both
  MigrateOnStartup and Payments:MutationsEnabled are false in config/runtime.
- Live/ready/metadata/Swagger return 200; bill/session/order/settlement GETs
  reject missing/invalid bearer (8 checks). Valid-JWT business coverage and
  non-GET write-blocking probes explicitly NOT RUN. No non-GET request sent.
- Four active financial + 21 protected table fingerprints match, schema stays
  38/244. refunds SELECT=false; no fingerprint/grant escalation for that unused
  mapped table. Four previously passed containers remain unchanged.
- Updated Gate C report and stopped. No migration/seed, financial mutation,
  JWT/credential reset, Mobile/security change, Gateway/other service, commit/
  push or Phase 6. No mutation integration-suite result claimed.

## 2026-10-27 - Gate C Ordering runtime/database/authentication PASS

- Reproduced host DNS failure with Ordering image and verified dynamic mapping
  fixes DNS/TCP. Added host-gateway only to order; started it alone with
  --no-deps/no-build/pull never and migration=false, using existing credentials/JWT.
- Actual ordering_svc READ ONLY connection and four-table probes pass; live,
  ready, metadata and Swagger return 200. Both protected GETs reject missing/
  invalid bearer with 401. Valid-JWT business coverage remains NOT RUN.
- Logs have no flagged errors. Four Ordering + 17 protected table fingerprints
  match; schema stays 38/244. Identity/Menu/Table IDs/images/start times/config
  remain unchanged. No order create/update/cancel, seed or mutation executed.
- Updated Gate C report and stopped after Ordering. No Gateway/other service,
  Mobile change, credentials reset, database/security change, commit/push or
  Phase 6. No new unit or mutation integration-suite result claimed.

## 2026-10-27 - Gate C Table runtime/database/authentication PASS

- Verified Table's same host DNS failure, then added dynamic host-gateway to
  Table only. Started only Table with migration=false and existing image.
- Actual table_reservation_svc READ ONLY connection, DNS/TCP, health, metadata,
  Swagger and four missing/invalid bearer GET rejection checks pass. Valid-JWT
  business GETs and mutation/settlement coverage remain explicitly NOT RUN.
- Table logs have no flagged errors; 4 Table + 7 Identity + 6 Menu fingerprints
  match and schema metadata stays 38/244. Identity/Menu IDs/start times/images
  and environments unchanged; neither service restarted/recreated.
- Updated Gate C report and stopped after Table. No Gateway/other service,
  credentials/JWT reset, database/security/Firewall/Mobile change, commit/push
  or Phase 6. No new unit/mutation integration test result claimed.

## 2026-10-27 - Gate C Menu runtime PASS, stopped after Menu

- Confirmed Menu's same host DNS failure; added dynamic host-gateway mapping
  only to its Gate C override. Identity config/container/image/start time stay
  unchanged; no Firewall/PostgreSQL security or credentials/JWT were reset.
- Actual menu_svc READ ONLY connection and six-table SELECT probes pass.
  Started Menu only with --no-deps/no-build/pull never; existing Redis unchanged.
- Menu DNS/TCP, live/ready/metadata/OpenAPI GETs pass; catalog rejects absent or
  invalid bearer with 401. Authenticated catalog 200 is explicitly NOT RUN
  without a legitimate staff JWT; no login, fake valid token or seed used.
- Six Menu plus seven Identity table fingerprints match; schema stays 38/244.
  Updated Gate C report; no other Microservice/Gateway, Mobile change, database
  mutation, migration, commit/push or Phase 6.

## 2026-10-27 - Gate C Identity DNS repaired and runtime PASS

- Reproduced unresolved host.docker.internal on the Identity network. A dynamic
  host-gateway mapping passed DNS and TCP 5432 probes without fixed IP/security
  changes; added it only to Identity in docker-compose.gate-c.yml.
- Reused existing DPAPI credentials/JWT and the same image; validated unchanged
  Identity environment and migration=false, resumed existing Redis dependency,
  then recreated Identity only with --no-deps/no-build/pull never.
- Actual Identity live/ready/metadata GETs return 200; PostgreSQL/Redis ready.
  READ ONLY database check verifies aicos_db.public, identity_svc and 38/244;
  seven owned-table fingerprints match before/after.
- Updated GATE_C_EXECUTION_RESULT.md. Stopped after Identity PASS: no other
  Microservice, mutation/migration/seed, Firewall/PG security edit, Mobile change,
  commit/push or Phase 6. Full Gate C remains pending owner review.

## 2026-10-26 - Gate C real runtime stopped at Identity DNS blocker

- Direct manual attempt authenticated all eleven roles, passed privileges and
  fresh schema count 38/244, then passed configuration/JWT and Redis health.
- Identity readiness failed: host.docker.internal does not resolve inside the
  container (getent exits 2), while Redis DNS works. Correct DB/role metadata
  and runtime migration=false verified without exposing secrets.
- Stopped before every later service/Gateway. Captured read-only diagnostics;
  did not modify DNS, firewall, PostgreSQL configuration/schema/data or grants.
- Updated GATE_C_EXECUTION_RESULT.md with PASS/FAIL/NOT RUN, running containers,
  retained private DPAPI retry cache and next-step review. No Mobile/commit/push
  or Phase 6; whole-database fingerprint equivalence is not claimed.

## 2026-10-26 - Gate C empty credential checkpoint

- New VERIFIED launch stopped at the identity_svc empty-password guard, before
  any database authentication/startup. Recorded separately from the old config
  error; no credential cache or new runtime success exists for this attempt.
- Documented direct local PowerShell execution to make masked input unambiguous.
  PostgreSQL role passwords are required; blank input is valid only for JWT
  prompts. No database/Mobile change, write smoke, commit/push or Phase 6.

## 2026-10-26 - Gate C console reconciliation

- Matched reported configuration stop to the older 23:07 attempt. The 23:16
  console closed with zero role inputs and no credential cache, not a new
  observed helper regression. No new runtime PASS is claimed.
- Reopened the hash-verified, offline-tested helper with a distinct VERIFIED
  window title and fresh evidence. Credentials/runtime smoke remain pending.
  No secret output, PostgreSQL mutation, Mobile edit, commit/push or Phase 6.

## 2026-10-26 - Gate C configuration regression and encrypted retry support

- Second real attempt verified eleven roles and schema 38/244, then stopped
  before startup at StrictMode access to Redis.environment. Fixed that guard.
- Added three offline orchestration scenarios: complete sequence, stop at Menu
  failure, and Windows DPAPI credential-cache retry/cleanup; all PASS, exit 0.
  Mock results are explicitly separate from production runtime smoke.
- Role credentials can now remain encrypted outside Git for controlled retries,
  with a private file DACL and deletion on successful smoke. JWT/access tokens
  remain process-only. No plaintext secrets are written or logged.
- Opened new masked console with fresh evidence after tests. Runtime checks
  pending; no migration, data write, Mobile edit, commit/push or Phase 6.

## 2026-10-26 - Gate C role checks passed; pre-startup JWT retry fix

- Evidence verifies eleven actual role connections/owned reads, effective
  privilege issue_count=0 and public inventory 38 tables/244 columns. Attempt
  stopped at JWT configuration; no AICOS service started and no SQL mutation ran.
- Added JWT short-input retry and redacted exception type/line diagnostics.
  Three synthetic JWT input cases pass; runtime credentials were not logged.
- Added Gate C override: internal-only Redis avoids existing VietAI host 6379,
  core APIs bind loopback. Compose guards pass. Unrelated containers untouched.
- Updated execution/readiness documentation; fresh hidden credentials required
  because the terminated attempt cleared secrets. No migration/Mobile/commit/push.

## 2026-10-26 - Gate C Engine recovery and masked runtime helper

- Verified Engine 29.7.2, no running containers/Backend port conflicts and seven
  existing Backend images. Pulled the missing Redis image without starting it.
- Added Invoke-GateCReadOnlyRuntime.ps1: eleven hidden existing role credentials,
  READ ONLY authentication/permission/snapshot checks, false migration/payment
  mutation guards and staged GET-only runtime smoke. Stops at the first failure.
- Parser/native Docker wrapper checks pass. Opened one interactive console;
  credentials and runtime results remain pending. No secrets in Git/log evidence,
  password reset, migration, write smoke, Mobile edit, commit/push or Phase 6.

## 2026-10-26 - Gate C approved read-only execution blocked before startup

- Recorded GATE_C_EXECUTION_RESULT.md: Docker Engine/ps exit 1 with missing
  desktop-linux pipe. Stopped as required; owner must open Docker Desktop.
- Current process has no eleven service password variables or shared JWT key;
  no Backend .env exists. Checked presence only, without displaying secrets.
- All runtime service/HTTP/database checks remain NOT RUN. No database action,
  Backend startup, Mobile edit, commit/push or Phase 6 transition performed.

## 2026-10-26 - Gate C runtime readiness preparation

- Added GATE_C_RUNTIME_READINESS.md: audited Gateway, ten .NET services and AI,
  effective external database configuration, role secret injection, health,
  dependencies, logging and existing database-backed functionality.
- Quiet Compose validation and static public-schema/migration-off checks pass.
  Docker Engine is unavailable; runtime credentials/JWT and authenticated smoke
  fixtures remain blockers. No runtime smoke or new test/build result claimed.
- Prepared staged startup and read-only smoke commands, separate write-test
  scope, recovery and approval checklist. Gate C remains unapproved/unexecuted;
  no service startup, database change, Mobile edit, commit or push performed.

## 2026-10-26 - Gate A completed after hidden all-role reset

- The approved new console assigned all eleven passwords, held NOLOGIN during
  entry, verified credential presence/permissions, then activated LOGIN and
  passed eleven actual role login/read probes.
- Independent READ ONLY checks confirm eleven LOGIN/SCRAM-present roles and
  effective privilege issue_count=0. PUBLIC/non-target ACLs and roles plus all
  38 business-table fingerprints remain unchanged.
- Added GATE_A_EXECUTION_RESULT.md and updated deployment status. No roles
  recreated, grants reapplied, schema/data modified, Gate C, Backend startup,
  Mobile edit, commit or push performed by this reset.

## 2026-10-26 - Approved Gate A all-role reset console opened

- Rechecked all eleven roles NOLOGIN and effective privilege issue_count=0.
- Opened one new masked console with new evidence directory
  gate_a_password_reset_20261026_220544; old progress is preserved.
- Existing reviewed helper will replace all eleven role passwords, keep
  NOLOGIN during assignment, verify credentials/ACLs before LOGIN and then
  test each real role connection. Completion is not yet claimed.
- No role recreation, GRANT/REVOKE reapplication, schema/business-data change,
  Gate C, Backend startup, Mobile edit, commit or push performed.

## 2026-10-26 - Gate A all-eleven password reset prepared, not executed

- READ ONLY inspection confirmed eleven NOLOGIN roles and three existing
  passwords (presence booleans only); the privilege matrix still has zero issues.
- Confirmed the old credential console and psql connection are closed; saved
  progress stopped after identity/menu/table password assignments.
- Added GATE_A_PASSWORD_RESET_PLAN.md with safe console cancellation, a new
  evidence directory, masked all-role replacement and pre-LOGIN checks.
- No password reset, LOGIN change, role recreation, GRANT/REVOKE, schema/data
  mutation, Gate C, service startup, Mobile edit, commit or push performed.

## 2026-10-26 - Gate A status recheck and masked console reopened

- READ ONLY check confirmed all eleven roles remain NOLOGIN with no passwords
  assigned; the reviewed effective privilege matrix still has zero issues.
- Progress stopped at AwaitingHiddenInput; no credential result or old helper
  process remained. Reopened only the existing checksum-verified console.
- No roles recreated, grants reapplied, schema/data changed, Gate C, Backend
  startup, Mobile edit, commit or push performed by this status check.

## 2026-10-26 - Conditional Gate A roles/ACLs applied; hidden input pending

- Removed both unapproved PUBLIC revokes and reported revised SQL hashes before
  execution. PUBLIC and non-target role ACL/metadata digests are unchanged.
- Created eleven NOLOGIN runtime roles and applied only reviewed scoped grants;
  both SQL files exited 0 and the full NOLOGIN effective matrix has zero issues.
- Preserved all 38 table data fingerprints and Gate B structure. No extra role
  attributes, ownership, DELETE/TRUNCATE rights, service startup or migration ran.
- Opened a local masked-input console for password provisioning, later LOGIN
  activation and actual per-role connection probes. This step remains pending
  user input and is not reported complete. No plaintext credential is stored.

## 2026-10-26 - Gate A roles/permissions preparation (not executed)

- Reconfirmed Gate B with READ ONLY metadata/06 verification; eleven runtime
  roles still absent. No Payment migration was reapplied.
- Audited API/domain persistence paths, role usernames and sequence needs.
  Prepared exact table/column grants and explicit revoke review.
- Hardened review-only SQL against reused-role ownership and stale CREATE/
  sequence privileges; proposed revoking PUBLIC TEMPORARY on aicos_db as an
  explicit database-wide effect requiring approval.
- Added READ ONLY effective-privilege verification and a passfile-based actual
  role connection probe. Verifier correctly reports eleven missing roles;
  role creation/grant/login tests remain pending approval, not claimed passing.
- Added GATE_A_ROLES_PERMISSIONS_REVIEW.md. No CREATE ROLE, ALTER ROLE, GRANT,
  REVOKE, service startup, Gate C, Mobile edit, commit or push was executed.

## 2026-10-26 - Gate B applied to aicos_db.public

- Executed approved Payment stages 00-06 sequentially; all SQL exits were zero.
  Backfill reported UPDATE 0 exactly four times; all nine final checks were zero.
- Production inventory is now 38 tables / 244 columns. All original 37 table
  row digests and the original 15 rows remain unchanged; untouched table schemas
  and the two Identity EF history entries match the baseline.
- Recorded the Docker-engine state change: engine was on, but all nine saved
  containers were exited and no AICOS Backend/database writer was running.
- Added GATE_B_EXECUTION_RESULT.md and updated deployment status. No Gate A/C,
  role/grant change, Backend startup, destructive rollback, commit or push ran.

## 2026-10-26 - Final check before Gate B (execution not approved)

- Created and verified a fresh production backup; restored it to a new retained
  test database and matched schema plus all 37 public tables/15 rows.
- Rechecked frozen SQL hashes, reviewed every forward/rollback SQL file, and
  confirmed forward writes/DDL remain scoped to Payment objects.
- Verified native Backend and Docker/WSL are stopped; read-only database checks
  found no other clients, open/prepared transactions, writer locks or proposed
  object-name collisions. Production preflight passed with zero financial rows.
- Added docs/GATE_B_FINAL_CHECK.md with PASS/FAIL evidence, exact proposed
  commands and stop/recovery conditions. No production migration, roles,
  grants, services, destructive rollback or gate transition was executed.

## 2026-10-26 - Phase 5.2 controlled deployment preparation

- Revalidated backup hashes, the retained restore schema/data, production
  read-only preflight and all ten frozen Payment SQL hashes.
- Re-ran nine maintenance tests successfully; no production services started.
- Prepared separate A/B/C approval packets with exact commands, checkpoints,
  expected effects and recovery. Proposed offline B then A then C because
  full runtime grants depend on the expanded Payment schema.
- Recorded that role/migration/startup approval and releasing Payment writes
  are separate decisions. No approval gate was executed.

## 2026-10-26 - Phase 5.1 deployment blockers

- Prepared idempotent NOLOGIN service-role provisioning, read-only role audit,
  and stronger review-only least-privilege grant guards; none executed on the
  production cluster.
- Added guarded PowerShell backup/restore scripts and verified a real aicos_db
  backup against a new retained restore-test database (37 tables, 15 rows).
- Added Payment mutation maintenance middleware with nine passing HTTP tests;
  Compose defaults to paused writes during deployment.
- Added external-database Compose configuration for the existing Windows
  PostgreSQL instance and a sequential image build helper.
- Added SQL checksum validation and the Phase 5.1 deployment/rollback report.
- All 12 Docker images built successfully in sequence. Final validation:
  73 .NET tests passed (8 optional PostgreSQL skips); the separate required
  PostgreSQL Billing/Payment/Table run passed 3/3 with no skips.


## 2026-10-26 - Phase 5 Payment deployment readiness

- Re-audited the staged Payment migration against the current Backend model,
  API behavior, and production `aicos_db` using read-only checks only.
- Confirmed none of the 11 service roles exists yet; role provisioning and
  grants remain explicit deployment blockers.
- Corrected Payment rollback to restore baseline nullability.
- Expanded post-migration verification to assert all additive columns,
  indexes, constraints, and the append-only trigger.
- Tightened runtime UPDATE grants to the columns used by current APIs.
- Rehearsed rollback and full migration on `aicos_payment_test`, then passed the
  required PostgreSQL Billing/Payment/Table tests without skips.
- Added `docs/PHASE5_DEPLOYMENT_READINESS.md` with backup, rollback, risk,
  execution-order, and approval gates.

## 2026-10-25 — Deployment configuration standardized

- Standardized every service on the official `aicos_db.public` deployment
  contract and removed checked-in database passwords.
- Disabled `MigrateOnStartup` in Docker Compose and every launch profile.
- Changed Docker bootstrap from schema-per-service creation to separate runtime
  roles with environment-supplied credentials and `public` search paths.
- Prepared, but did not execute, the review-only least-privilege PostgreSQL
  script in `infra/postgres/security/public-service-grants.sql`.
- Audited Gateway routes and Docker/local service destinations; no route change
  was required. No migration, grant, or data mutation ran on `aicos_db`.

## 2026-10-24 — Cash, QR and table settlement

- Payment domain now persists branch/actor audit, optimistic versions and append-only
  `payment_events` against the approved additive schema.
- Added idempotent Cash payment, pending bank-transfer QR, evidence-based manual
  confirmation, bill close and session settlement endpoints.
- Table Reservation now checks Payments through a typed REST contract and keeps the
  session open when settlement is false or unavailable.
- Added unit tests and real PostgreSQL integration on `aicos_payment_test`, including
  concurrent QR confirmation and the Table-to-Payments close flow. No payment migration
  was run on `aicos_db`.

## 2026-10-24 — Payment additive migration prepared

- Reviewed the live `aicos_db.public` payment schema in a read-only transaction
  and confirmed that one table session can own multiple orders while one bill
  remains unique by session.
- Replaced the unsafe single-step payment SQL draft with staged preflight,
  expand, audit-table, backfill, concurrent-index, contract, verification, and
  rollback scripts under `docs/migrations/payments`.
- Added a disposable PostgreSQL payment test environment fixed to
  `aicos_payment_test`; full apply, verification, rollback, and re-apply passed
  there with zero invalid indexes. No migration was executed on `aicos_db` and
  no real data changed.

## 2026-10-24 — Query bill theo nguồn nghiệp vụ

- Thêm `GET /api/v1/payments/bills?sessionId=...` và `?orderId=...`; request bắt
  buộc đúng một UUID nguồn.
- Payments xác minh branch qua Ordering REST contract trước khi trả bill; không
  query chéo DbContext hoặc bảng thuộc Ordering.
- Siết `GET /bills/{billId}` cho bill tại bàn: session không có order trong branch
  hiện tại trả `404`, không còn coi lời gọi Ordering rỗng là đã xác minh quyền.
- Bổ sung unit test source query, validation, branch isolation và mở rộng PostgreSQL
  integration test để đọc lại bill theo `orderId`.
- Chuẩn hóa PostgreSQL integration tests thành `Skipped` thật khi thiếu
  `AICOS_TEST_POSTGRES`; regression hiện có 59 test pass và 7 test PostgreSQL skip.
- Không thay đổi entity, schema, migration hoặc dữ liệu PostgreSQL.

## 2026-10-23 — Billing foundation

- Ánh xạ bốn bảng có sẵn `bills`, `bill_items`, `payments`, `refunds` vào Payment
  service schema `public`; không tạo migration.
- Thêm contract Ordering liệt kê order theo Table Session qua REST và API tạo/đọc
  bill theo branch token, hỗ trợ idempotency bằng `requestId`.
- Bill tại bàn tổng hợp toàn bộ order trong session; bill mang về dùng một order,
  giá và item snapshot lấy từ Ordering thay vì nhận từ Mobile.
- Thao tác tạo bill dùng transaction `SERIALIZABLE`; 4 test foundation và HTTP
  PostgreSQL integration create → retry → read đều đạt, dữ liệu test được cleanup.
- Chưa mở API xác nhận thanh toán/in bill vì schema chưa có actor audit và trạng
  thái in độc lập; cần trình migration trước khi thay đổi database.
- Toàn Backend hiện đạt 59/59 test (6 BuildingBlocks + 53 smoke/integration).

## 2026-10-23 — Preparation workflow hoàn chỉnh

- Map read/update subset của `orders`, `order_items`, `order_item_events` vào
  Kitchen Operations service; không tạo bảng hoặc migration.
- Thêm queue theo branch và transition `PENDING → PREPARING → READY`.
- Staff chỉ xem; tạm thời chỉ Manager/SystemAdmin được đổi trạng thái vì hệ thống
  chưa có role Barista. Không giả lập Bar đã duyệt.
- Bọc thao tác đổi trạng thái trong transaction `SERIALIZABLE`, trả conflict ổn
  định khi hai thiết bị cùng cập nhật một món và ghi actor vào item event.
- Unit/HTTP test đạt; PostgreSQL thật đã chạy queue → PREPARING → READY và xác
  nhận toàn bộ dữ liệu thử nghiệm được cleanup.
- Toàn Backend đạt 54/54 test (6 BuildingBlocks + 48 smoke/integration).

## 2026-10-23 — Ordering dependency contract

- Audit bốn bảng Ordering và xác nhận chỉ hỗ trợ `DINE_IN`/`TAKEAWAY`, không có
  delivery trong phạm vi Staff Mobile.
- Thêm `GET /api/v1/tables/sessions/{sessionId}` theo branch JWT để Ordering xác
  minh phiên qua REST, không query chéo DbContext.
- Test Table Operations liên quan đạt 5/5; chưa thay đổi schema hoặc migration.
- Ánh xạ bước đầu bốn bảng Ordering hiện có vào `OrderingDbContext` schema
  `public`, gồm order, item, option snapshot và item event; build 16 project đạt.
- Thêm Create/Add Order và GET tracking; hỗ trợ `DINE_IN`/`TAKEAWAY`, giá lấy qua
  Menu REST client, session lấy qua Table REST client và bearer token được forward.
- Dùng `requestId` làm PK/idempotency key, kiểm tra min/max option và ghi actor event.
- Ordering HTTP PostgreSQL integration đạt create → retry idempotent → tracking;
  actor và tổng tiền đúng, toàn bộ dữ liệu test cleanup về 0.
- Sửa cảnh báo multiple collection include bằng `AsSplitQuery`; toàn solution
  đạt 49/49 test.

## 2026-10-23 — Branch-scoped catalog read API

- Ánh xạ sáu bảng Catalog có sẵn trong `aicos_db.public`; không tạo migration.
- Thêm `GET /api/v1/menu/catalog` với category filter và tìm kiếm ILIKE.
- Chỉ trả sản phẩm được mở bán tại branch JWT cùng category, variant và option active.
- Thêm model/DbContext configuration, DTO, query service, endpoint và tests tách biệt.
- PostgreSQL HTTP integration test đạt; tám bảng catalog/tenant/branch test trở
  về 0 row sau cleanup.

## 2026-10-23 — Table map and session lifecycle

- Ánh xạ bốn bảng Table có sẵn trong `aicos_db.public`; không tạo migration.
- Thêm API map theo branch JWT, mở phiên, đóng phiên và xác nhận dọn xong.
- Áp dụng state machine `AVAILABLE → OCCUPIED → CLEANING → AVAILABLE`.
- Dùng transaction `SERIALIZABLE`, branch scope, role authorization, logging và
  RFC 7807 conflict để bảo vệ thao tác đồng thời.
- Build 16 project đạt 0 warning/error; smoke tests đạt 37/37 trước lần kiểm tra
  toàn solution cuối.
- PostgreSQL integration test thật đạt bốn HTTP endpoint; sáu bảng Table và dữ
  liệu tenant/branch test trở về 0 row sau cleanup.
- Ghi nhận schema chưa có số khách/người mở phiên và partial unique index; chưa
  tự ý tạo SQL hoặc apply migration.

## 2026-10-23 — Identity staff provisioning

- Thêm `POST /api/v1/identity/staff` với policy `Management`.
- Tạo đồng thời user, role chuẩn nếu còn thiếu, tenant membership và branch
  assignment; password chỉ được lưu dưới dạng hash.
- Manager bị giới hạn trong branch của JWT và không thể cấp role Manager;
  SystemAdmin được phép cấp Manager tại branch active.
- Chặn email trùng, role không hỗ trợ và xung đột ghi đồng thời bằng lỗi RFC 7807.
- Không thêm migration hoặc thay đổi schema PostgreSQL.
- Build 16 project đạt 0 warning/error; smoke tests đạt 31/31.
- Integration test thật đạt luồng Manager login → tạo Cashier → chặn email trùng
  → Cashier login; 7 bảng Identity/session trở về 0 row sau cleanup.

## 2026-10-23 — Apply Identity public baseline

- Người dùng đã duyệt SQL và migration đã apply vào `aicos_db`.
- Giữ nguyên 35 bảng gốc; thêm `public.__ef_migrations_history` và
  `public.refresh_sessions`.
- History ghi nhận baseline public và AddRefreshSessions; không còn migration
  pending cho Identity.
- Xác minh 3/3 FK refresh là `NO ACTION`, 5 index và 0 refresh row ban đầu.
- Thêm PostgreSQL integration test tùy chọn qua `AICOS_TEST_POSTGRES`; khi bắt
  buộc chạy trên `aicos_db`, HTTP login/me/refresh/logout đều đạt.
- Integration test cleanup đúng các ID ngẫu nhiên; row count Identity/refresh
  sau test trở về 0.

## 2026-10-23 — Map Identity trực tiếp vào `aicos_db.public`

- Xác nhận connection runtime tới `aicos_db` và 35 bảng `public`.
- Loại connection Development cũ trỏ nhầm database `aicos`; source không lưu password.
- Chuyển Identity DbContext sang sáu bảng `public`, khớp nullability và
  `timestamp without time zone` hiện tại.
- Loại hai migration schema `identity` chưa apply; thay bằng baseline không DDL
  và migration chỉ thêm `public.refresh_sessions`.
- Sinh `docs/PENDING_IDENTITY_PUBLIC_MIGRATION.sql` để chờ duyệt; chưa apply
  vào `aicos_db`.
- Test migration trên bản sao schema-only đạt; 35 bảng gốc không bị sửa.
- Build 16 project không warning/error; Backend tests đạt 32/32.

## 2026-10-23 — Refresh session lifecycle

- Thêm refresh-token rotation, hash-only persistence và optimistic concurrency.
- Thêm `POST /auth/refresh`, `POST /auth/logout` và `GET /auth/me`.
- Thêm migration pending `20261023034706_AddRefreshSessions` với 3 FK
  `NO ACTION`; không apply vào `aicos_db`.
- Review SQL idempotent: không chạm schema `public`, không drop/delete dữ liệu.
- Hai migration Identity chạy thành công trên PostgreSQL tạm; database tạm
  đã được xóa.
- Build 16 project không có warning/error; Backend tests đạt 32/32.

## 2026-10-23 — Harden Identity migration

- Tạo backup custom-format và schema-only trước khi thay migration pending.
- Regenerate `InitialIdentitySchema`; migration cũ chưa apply nên không làm thay
  đổi lịch sử database.
- Chuyển 6 FK Identity từ cascade sang `NO ACTION`.
- Đặt branch mới mặc định `INACTIVE`.
- Chuẩn hóa email về lowercase và thêm check constraint tại database.
- Test migration trên database PostgreSQL tạm thành công; không apply vào
  `aicos_db`.
- Build 16 project không có warning/error; Backend tests đạt 27/27.

## 2026-10-22 — Identity Authentication foundation

- Thêm entity/configuration cho tenant, branch, user, role, tenant membership và
  staff assignment trong schema `identity`.
- Thêm migration source `InitialIdentitySchema`; chưa apply database.
- Thêm `POST /api/v1/identity/auth/login` với validation, password hashing,
  branch selection, logging và RFC 7807 errors.
- Thêm role `Waiter` vào JWT/policy Staff để khớp AICOS Staff Mobile.
- Đồng bộ dependency EF Core Relational 8.0.31.
- Thêm unit test auth và HTTP validation test.
- Cài SDK .NET 8.0.426 cho môi trường phát triển; không đổi `global.json`.
- Build toàn solution đạt 0 warning; Backend tests đạt 25/25.
- Sinh idempotent SQL migration thành công vào thư mục tạm; chưa apply database.
