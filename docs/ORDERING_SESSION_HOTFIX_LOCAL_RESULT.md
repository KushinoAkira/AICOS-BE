# Ordering session guard local hotfix — 2026-11-12

## Result

Only Ordering replaced with schema-compatible pre-Cup hotfix. No operational migration, seed, login, test order, SQL DDL/DML, credential rotation, dependency restart, commit or push. Working tree containing Cup/Recovery development kept intact.

Full current source cannot run against the existing schema: orders.fulfillment_mode and four admission/fulfillment tables are absent. Instead, preparation exports baseline `ff3a474` to a unique TEMP directory, backports reviewed CounterOrderRequests from `5636b52`, adds optional ExpectedSessionId to CreateOrderRequest and advertises ExpectedTableSession only with real validation implemented. No entity, EF model or migration changed in that export.

Guards: verified table reference required with expected session; tenant/branch/table/open session validated; stale/closed gives 409 qr.session_changed; retry must match original session and content; same-key replay returns prior committed order. Existing no-key relational requests still get a transaction/table lock. This does not solve the separate financial close/add-more race, whose lock protocol remains outside scope.

## Artifacts

- `scripts/Prepare-OrderingSessionHotfix.ps1`: reproducible export without modifying main working source; reviewed guard/test revision pinned.
- `docker-compose.order-session-hotfix.yml`: use LAST after base/main-runtime overlays. Unique image, migration false, Recovery/Cup workers false, unused Recovery machine key removed from merged configuration.
- Export used: `%TEMP%/aicos-ordering-session-hotfix-c2a203015692402d87a53467f95e466c/source`.
- Image `aicos-main/order:session-guard-20261112`, ID `sha256:fe56d97051e15a1747bca08946b98770a60034bac39b0a1d86c6156db762e888`.
- Old image retained as `aicos-main/order:before-session-guard-20261112` (ID `sha256:5b6197ba70bf4e34ce1cfa4884cf892f9a57af8a5441dbef8f4d8028a59ec1a8`).
- New container ID `238f33cf6d67583428371e4e5277d9a5594049b81e1155c494d3cb492343d8e3`.

Actual update command:

```powershell
docker compose -f docker-compose.yml -f docker-compose.main-runtime.yml -f docker-compose.order-session-hotfix.yml up -d --no-deps --no-build order
```

Do NOT run the full current working tree build into this tag, use a blanket Compose up or omit the hotfix overlay for subsequent Ordering starts. The separate hotfix intentionally does not deploy Cup/Recovery. Source development remains pending its schema rollout.

## Evidence

- Dedicated guard + legacy MobileCounter HTTP tests: 8 PASS, 0 FAIL/SKIP.
- Expanded Ordering/QR/Mobile regression: 94 PASS, 0 FAIL/SKIP, including those 8 (not 102 separate tests).
- Real Docker build PASS. Existing CS0628/CS8620 warnings unchanged.
- HTTP tests use TestServer/EF InMemory/fake service directories and test authentication; not PostgreSQL concurrency or actual Mobile E2E.
- Ordering localhost:5204 /health/live and /health/ready: HTTP 200; Gateway localhost:8081 /health/live: 200; GET capabilities via Gateway without JWT: 401. Authenticated capability value verified in isolated HTTP test, not with an invented operational JWT.
- PostgreSQL `aicos_ordering.ordering`: all seven table row counts/content fingerprints, including eight-entry migration history, unchanged before/after; queries explicitly BEGIN READ ONLY. No rows/secrets exported, only hashes/counts.
- Non-Ordering container IDs/images/start times unchanged.
- Docker effective config retains credentials/JWT/service network, with explicit new workers disabled. MigrateOnStartup=false confirmed after startup; no migration startup log.
- Phone initially had reverse 8081; at final check device 506e58f8 no longer attached. Actual Mobile submission NOT RUN; no test data created in operational DB.

## Rollback / next check

If runtime fails, prepare a last overlay pointing only order.image to the retained before-session-guard image with migration/worker flags still OFF, and run the same `up -d --no-deps --no-build order`. No database rollback is required or authorized; data/schema were not changed. Rolling back restores the old capability limitation.

Reconnect the phone/authorize USB, run Mobile `scripts/run-local.ps1`, reopen the actual table/session and retry. Keep prior request key when retrying an uncertain request; do not clear drafts to bypass a conflict. The original observed failure was before POST, not evidence an Order had already been stored. Full source schema/Recovery/Cup rollout needs separate approval.
