# Features-OFF runtime smoke — PASS, 2026-11-16

Scope: user-approved startup of existing PostgreSQL/Redis and sequential replacement/start of 11 application containers using verified images. Database expansion/GRANT was separately approved and verified before this smoke. No business API writes, login/JWT creation, financial reconciliation or activation.

## Preflight and migration dependency

The first attempt stopped before any app because Menu/Customer schemas differed from EF. See RUNTIME_MODEL_SCHEMA_DIFFERENCES.json and RUNTIME_COMPATIBILITY_EXECUTION.md. After explicit separate approval, the exact two existing EF migrations and Customer grant committed and passed verification. Customer backfill INSERT 0; legacy data preserved. The raw owner-ACL verifier stop, read-only diagnosis and absence of SQL replay are documented in RUNTIME_COMPATIBILITY_EXECUTION_RESULT.json.

A fresh eight-database backup was restored into an isolated internal/tmpfs/no-host-port cluster: **11/11 groups PASS**. Five new and 17 prior SQL checksums passed. Private full dumps/config/credentials/baselines remain outside Git with restricted ACL. No closed EF migration was edited.

## Actual runtime results

PostgreSQL and Redis reused their exact IDs, healthy, not recreated; real Redis AUTH PING returned PONG. All 11 app containers were replaced one at a time by image IDs pinned to application-source revision 396b7e8596bb1d993272d6fe7c0620b6c62db7c4. No source or image rebuild was needed for subsequent tooling/document changes.

| Application | Runtime/live | Ready | API docs |
| --- | --- | --- | --- |
| identity | PASS | PASS | PASS |
| menu | PASS | PASS | PASS |
| table | PASS | PASS | PASS |
| order | PASS | PASS | PASS |
| kitchen | PASS | PASS | PASS |
| payment | PASS | PASS | PASS |
| reporting | PASS | PASS | PASS |
| workforce | PASS | PASS | PASS |
| customer | PASS | PASS | PASS |
| ai | PASS | PASS | PASS |
| gateway | PASS | N/A (not mapped) | N/A (not mapped) |

Gateway has no separate ready/Swagger endpoint; GET / returned 200. AI passes placeholder runtime/metadata/docs only, not implemented AI business functionality. All app logs checked without raw secret dumps: no migration/seed markers, error/fatal logs or restarts. Unrelated containers unchanged.

## Gateway and security

- GET metadata through Gateway to all **10 downstreams** returned 200 and the expected schema; public Identity subscription plans GET returned 200.
- Missing token and malformed non-JWT bearer string both returned **401** for **9 protected GET routes**: Identity auth/me, Menu categories, Table floor-plan, Ordering orders, Kitchen queue, Payment cashier-shifts, Reporting collections, Workforce employees/me and Customer staff notifications.
- Gateway container DNS and TCP checks passed for all ten downstream names/internal ports. No fake JWT or app login was used.
- Nine .NET EF schemas matched current actual models before startup. Private snapshots covered ten databases; schema, ACL, data fingerprints and EF history stayed identical after every app and at smoke end. Role flags unchanged; post-smoke auth follow-up also preserved business fingerprints/container state.

## Safety and limitations

MigrateOnStartup=false. Seed, expiry, financial, recovery/admission, print and other feature/dispatch/reconciliation flags remain OFF as specified by the candidate overlay. Existing secrets/JWT/roles unchanged. No API POST/PUT/PATCH/DELETE was called and no historical collections were generated. No rollback or unrelated stack startup.

Runtime gate: **PASS within features-OFF scope**. Dashboard business activation, authenticated role/branch data, real Cash/QR/shift mutations, valid-token Mobile E2E and AI business remain **NOT TESTED/OFF** here. Existing two historical paid payments without collection and missing financial machine keys still require the separate activation plan. No new Flutter/regression run for this operational/document task; previous counts are historical checkpoints.

Next: review CASHIER_DASHBOARD_ACTIVATION_PLAN.md for explicit financial-key/flag/history-preview approval. Do not infer permission to activate financial capture or dispatchers from this smoke PASS. The work-in-progress Item Transfer V2 remains separate, uncommitted and unactivated.

Evidence: CASHIER_DASHBOARD_RUNTIME_SMOKE_RESULT.json, RUNTIME_COMPATIBILITY_EXECUTION_RESULT.json, RUNTIME_COMPATIBILITY_EIGHT_DB_RESTORE_RESULT.json and the earlier isolated SQL test RUNTIME_COMPATIBILITY_TEST_RESULT.json (13 groups PASS, not rerun during deployment).
