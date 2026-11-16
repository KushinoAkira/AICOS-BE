# Dashboard Backend runtime rollout readiness

Date: 2026-11-16. **READY FOR FEATURES-OFF RUNTIME SMOKE REVIEW.** Runtime/feature activation remains UNEXECUTED. All **11/11 candidate images built PASS**, revision labels verified; existing containers and :local tags unchanged. Full Backend regression 856 PASS / 51 SKIP. See image IDs in the evidence manifest.

## Current state

Seven migration files and three GRANT files already committed/verified on existing Docker PostgreSQL (cluster 7703616372368646182): 64 tables / 807 columns. No reapply is needed. All application/infra containers remain stopped. The original Backend directory's dirty V2 and runtime files remain untouched; candidate source is the separate review branch.

Readiness audit: merged Compose validates; nine real service credentials authenticate to their own PostgreSQL database/schema with READ ONLY. Existing JWT key/issuer/audience, Payment encryption key, DB credentials and Redis configuration are preserved. AI URL config is preserved but AI is still a placeholder, not a real database/business implementation. Redis live auth, service Docker DNS and HTTP smoke are NOT RUN until a startup gate is approved. No user login/API account/JWT was created.

## Minimal operational change

PickupOrderExpiryWorker, TableSessionExpiryWorker and PaymentExpiryJob previously ran regardless of migration/feature flags. Added `BackgroundJobs:ExpiryEnabled`, default **true**, checked at ExecuteAsync entry. False returns before creating timers/scopes or running expiry operations. Existing APIs, finance calculations, expiry rules and default behavior remain unchanged. No Entity/migration/DTO change.

Six host tests verify default/true keep the tasks running and false completes them without running the worker. Full .NET regression: **856 PASS, 51 SKIP, 0 FAIL** (BuildingBlocks 15; Identity 206; service tests 585/51; smoke 50). These host tests use test doubles/in-memory fixtures, not PostgreSQL financial E2E. The nine existing-password PostgreSQL checks are separate real READ ONLY evidence. Initial registration-based test implementation had 2 failures because the factory applies configuration after registration; the final implementation checks the final configuration inside ExecuteAsync and passed all six tests. No failure was counted as PASS.

## Candidate Compose and images

`docker-compose.dashboard-smoke.yml` is an additional, **not activated** overlay after base + main-runtime. It pins reviewed images by source revision and explicitly disables automatic migrations/seed/reminders, all expiry workers, admission/cup/bill/collection/transfer/print dispatch/reconciliation and financial feature flags. SessionBilling, CashierShifts, CupV1, split and transfer remain OFF. Simulation remains OFF. This is maintenance smoke configuration, not the final business configuration.

Image source pin: **396b7e8596bb1d993272d6fe7c0620b6c62db7c4**. Use this exact value, not a later documentation-only HEAD. Tags: `aicos-main/<service>:dashboard-396b7e8596bb1d993272d6fe7c0620b6c62db7c4`. Image revision label must match. Builds are sequential and never replace `:local` tags. See `CASHIER_DASHBOARD_CANDIDATE_IMAGES.json` for actual build PASS/FAIL and image IDs; a partially built manifest is not success for all images.

Existing gateway binds 0.0.0.0:8081; the reviewed smoke candidate binds **127.0.0.1:8081**, with direct service ports on loopback too. This is a proposed smoke binding change, not applied. USB/ADB reverse can use loopback; same-Wi-Fi access will not work under this smoke overlay. The original runtime file and existing bindings were not changed.

Base Compose has MigrateOnStartup=true; never start it alone. All three Compose files are mandatory. Existing PostgreSQL/Redis bind mounts resolve from the original checkout, while the candidate source is a worktree: **do not recreate infrastructure through this candidate Compose**. Start only the pinned existing infra IDs; for apps use explicit service names with --no-deps.

## Credentials and rollback backup

Use the original Git-ignored `D:/ki9/sep490/Sep490/AICOS-BE/.env`; do not initialize a new .env or rotate credentials. Raw merged config was parsed only in memory, never printed or committed. No secrets are baked into images or passed as build arguments; build args are PROJECT/ASSEMBLY only.

A rollback/config backup of 13 existing containers is stored outside Git under LOCALAPPDATA/AICOS/runtime-backups, encrypted with Windows DPAPI and restricted to current user/SYSTEM. Encryption/decryption roundtrip PASS. It contains old image IDs/env/ports; no plaintext file was written. The public readiness result contains only booleans/presence metadata, not values or ciphertext. Recovery requires the same Windows user/machine and a separately reviewed action.

Financial machine keys for SessionBilling/CashierShifts/KitchenAdmission/CupServing are absent in the candidate and existing containers. **This does not block features-OFF smoke, but blocks feature activation.** Prepare independent machine secrets safely in a later activation step; never reuse DB passwords/JWT or request values over chat. Existing DB/JWT/encryption secrets need no reset.

## Startup mutation risk and later financial gate

Actual READ ONLY counts at the audit checkpoint:

- Pending legacy payments currently due for expiry: 0.
- Pending unpaid Orders past payment deadline: 0.
- Empty OPEN sessions idle over six hours: 0.
- Undelivered Bill events: 0.
- Admission outbox rows: 0.
- **Historic successful/refunded payments missing collection receipts: 2.**

Zero due rows is time-sensitive, not proof workers are harmless. Expiry remains OFF for smoke. CollectionDispatcher scans old durable payments without an age cutoff; enabling it may create two recovered receipts and Reporting facts. Recovered Cash is not assigned to a newly opened drawer; QR remains branch-only, never Cash drawer. No reconciliation was run and no historical receipt/shift was created. Review those records/policy and machine-key setup before enabling collection workers. Do not display an empty projection as proof there was no historical revenue.

## Exact proposed rollout (requires separate approval)

The commands below are a runbook, NOT executed in this preparation task. Paths must point to the reviewed worktree; no whole-stack `up`, `down -v`, migrate or GRANT.

    $env:AICOS_RUNTIME_REVISION = '396b7e8596bb1d993272d6fe7c0620b6c62db7c4'
    $reviewPath = (Get-Content "$env:TEMP/aicos-dashboard-sync-workspace.txt" -Raw).Trim()
    $composeArgs = @('compose', '--env-file', 'D:/ki9/sep490/Sep490/AICOS-BE/.env', '-f', "$reviewPath/docker-compose.yml", '-f', "$reviewPath/docker-compose.main-runtime.yml", '-f', "$reviewPath/docker-compose.dashboard-smoke.yml")
    docker @composeArgs config --quiet

Before startup: verify exact cluster/container IDs, 17 SQL checksums/histories/ACL without reapplying them, old image IDs, DPAPI backup, candidate image labels, zero active external writers and no business clients. Snapshot DB fingerprints. Request approval for **features-OFF runtime smoke only**.

After that approval only:

1. Start existing pinned aicos-main-postgres and aicos-main-redis (docker start existing IDs, no recreate); validate health/Redis auth without logging credentials.
2. `docker @composeArgs up -d --no-build --no-deps identity`; check health live/ready, Swagger, logs, database fingerprint.
3. Repeat that exact command one service at a time: menu, table, order, kitchen, payment, reporting, workforce, customer, ai; stop on failure and report. No automatic migration/seed or workers should run under candidate flags.
4. Repeat for gateway last. Verify DNS/internal connectivity and gateway root/health, safe public subscription-plan GET, missing/malformed-token 401. Gateway ready/Swagger are N/A by source; AI business functionality is placeholder. Do not claim a protected business flow passed on 401.
5. Compare fingerprints/schema/history/role metadata, new tables, previous image/container changes against the approved replacement list. Stop on unexpected writes; never auto-rollback data or restart unrelated stacks.

Do not log in/create JWT or call POST/PUT/PATCH/DELETE during this smoke, and do not operate Mobile interactively until the following business-activation gate. Guarded financial endpoints remaining unavailable is expected with flags OFF; this phase cannot claim Dashboard business completion.

## Rollback and acceptance

On service failure: stop that candidate app and pause rollout; keep DB schema/ACL and durable data intact. Old image tags/IDs and encrypted env backup are retained, but **do not automatically start old images**, whose expiry workers may be unguarded. Reverting image/config needs review. Never restore old DB snapshots or revoke approved grants just to undo an image change.

Smoke PASS requires approved services healthy, correct route behavior/flags, existing credentials valid, and no unexpected business data/schema changes. Only then propose separate machine-key/feature activation, historical collection reconciliation and real Cashier/Staff/API tests. Keep unrelated CupV2, refund/split/group collection and real bank/printer integrations outside this step.

Evidence: `CASHIER_DASHBOARD_RUNTIME_READINESS_RESULT.json`, `CASHIER_DASHBOARD_CANDIDATE_IMAGES.json`. Flutter UI/code unchanged; no Flutter test rerun in this Backend operational task. No operating Backend service has been started by this preparation.

## Approved smoke attempt: schema blocker

Infrastructure healthy, applications 0/11 NOT RUN: full model preflight found pending Menu ProductPictureUpload and Customer HuntVouchers. See [CASHIER_DASHBOARD_RUNTIME_SMOKE_EXECUTION.md](CASHIER_DASHBOARD_RUNTIME_SMOKE_EXECUTION.md). Separate guarded compatibility proposals tested 13/13 PASS; eight-DB full restore 11/11 PASS. No local extra migration/GRANT. Runtime approval remains valid after the separately reviewed schema/ACL blockers are resolved.

## Runtime gate completed — 2026-11-16

Separate Menu/Customer expansion and Customer GRANT approval executed: 3/3 PASS, legacy data unchanged, backfill 0. Features-OFF runtime smoke: 11/11 apps PASS, existing PostgreSQL/Redis healthy; 10 downstream metadata/DNS/TCP checks and 9 protected GET auth checks PASS. All ten DB schema/ACL/data/history baselines unchanged during smoke. See CASHIER_DASHBOARD_RUNTIME_SMOKE_EXECUTION.md and RUNTIME_COMPATIBILITY_EXECUTION.md. This supersedes the earlier schema-blocked attempt. Financial activation/history reconciliation and valid-token business/Mobile E2E remain OFF/NOT TESTED; no implied approval.
