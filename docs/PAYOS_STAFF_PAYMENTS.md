# payOS Staff / Session Bill — 20/11/2026

Latest checkpoint: **Payments local image updated, payOS creation still OFF**. Browser capability/read-only smoke PASS; all 10 operational database fingerprints unchanged and 16 other containers preserved. Full source regression 1,030 PASS/69 SKIP. See `PAYOS_LOCAL_RUNTIME_AND_GIT.md` and `PAYOS_LOCAL_RUNTIME_RESULT.json`. Earlier source-only/no-runtime-change statements below describe prior gates; no public tunnel, real keys or money were activated.

Source implementation; **StaffPaymentsEnabled=false**. No operational deployment, provider setup, live key collection, bank call or bank transaction has been performed. The user reports an existing linked payOS channel; its identity, receiving account and public callback still need verification before activation.

## Existing main and actual local configuration

Read `origin/main` de7e011: payOS adapter, signed callback and order-payment query already exist. Default ReturnUrl is `http://localhost:8080/api/v1/payments/payos/return`, ResultPageUrl is `http://localhost:8443/thanh-toan/ket-qua`. These are not evidence of a public callback deployment.

READ ONLY local inspection: no PayOS provider row in `aicos_payment.payment.payment_providers`; runtime has no PayOs URL environment overrides. The Backend root `.env` has no nonempty PAYOS_CLIENT_ID/API_KEY/CHECKSUM_KEY/RETURN_URL values. Checks returned only existence booleans, never key values. Gateway's payments catch-all forwards to Payments and does not itself require JWT for anonymous callbacks; downstream verifies signatures. Invalid supplied JWT is rejected at Gateway. This is source/config review, not Gateway/provider E2E.

## Scope and reuse

- Dine-in: one existing SessionBill per session, multiple Orders, existing BillAttempt/financial journal/PAID outbox. No individual Order payment is created to represent a Bill.
- Takeaway: existing Payment by orderId, no new Bill entity. Single-attempt admission uses the shared per-order PostgreSQL lock; legacy Cash/online paths see unresolved staff bank attempts and cannot bypass them.
- Reuse PayOsGateway, PayOsSignature, provider/merchant configuration, CashActorFilter, BillWebhookReceiver's transaction/receipt deduplication and CashierLedger capture. No distributed transaction or schema migration added.
- Mock and ZaloPay sandbox APIs remain distinct and usable in their intended tests. ProviderMode currently has only Mock/Sandbox from main; **the name Sandbox does not make payOS calls simulated**. Real eligibility is the explicit server flag, production host, channel/recipient pin and provider credentials.

## API contracts

| API | Behavior |
|---|---|
| GET `/api/v1/payments/bills?sessionId=...` | Existing read-only financial view; adds `qrProviderCode`. `qrAvailable` comes from Backend channel eligibility |
| POST `/bills/{billId}/payments/qr` | Existing request + `providerCode:"PayOS"`, sessionId, current fingerprint, stable Idempotency-Key. Freeze in Ordering precedes durable local intent; bank I/O runs after commit |
| POST `/bills/{billId}/payments/{paymentId}/query` | New explicit provider query; Counter role/live actor/scope checks. GET polling never queries a provider or expires an attempt |
| POST `/api/v1/payments/payos/webhook` | Existing provider-specific signed endpoint; routes Bill order codes to BillAttempt and legacy codes to Payment. Generic Mock webhook cannot confirm payOS |
| GET `/api/v1/payments/payos/return` | Bill branch reads stored state and redirects; unsigned browser parameters cannot confirm Bill money |
| GET/POST `/api/v1/payments/staff/takeaway/{orderId}/payos` | Version 3, ProviderCode=PayOS, ProviderIsTest=false; create/recover one order obligation, branch+amount+stable key validated |
| POST same path + `/{paymentId}/query?branchId=...` | Explicit scoped reconciliation of the existing Takeaway payment; no additional provider create |

BillAttemptView adds optional `providerCode`. Requests/response fields for existing consumers remain; fields are additive. Web FE source has not been cloned, so browser client execution is NOT RUN.

## Channel and receipt safety

`Payments:PayOs:StaffPaymentsEnabled` defaults false. Creation also requires MerchantTenantId matching the actor, exact production HTTPS API host `api-merchant.payos.vn`, expected ReceivingAccountNumber, encrypted provider keys, enabled provider/effective branch merchant setting, public HTTPS return/result URLs and a matching provider callback `/api/v1/payments/payos/webhook`. The first implementation pins **one channel/recipient to one tenant**, not arbitrary brand settlement or multiple receiving bank accounts.

payOS has no separate sandbox: do not enable this flag in an operational environment merely to run a test. Return/callback reachability, channel ownership, recipient, outstanding attempts and a user-approved money amount must be reviewed first.

- Bill codes reserve bit 52 of the safe integer namespace; old order-payment codes stay below that boundary. Original OrderIds and Bill IDs are unchanged. Same-code collision checks use a PostgreSQL advisory transaction lock and fail before bank create, without adding an index/table.
- Creation validates payOS's signed response, exact orderCode/amount/VND/account and HTTPS checkout URL. Provider error details are not propagated by the new staff mode. Redirects are disabled on provider HTTP to prevent forwarding custom key headers to another host.
- Callback verifies the original payOS signature; Bill processing additionally validates channel tenant, VND, receiver, amount and provider reference. Wrong amount/receiver creates journal evidence and requires reconciliation, not PAID. Duplicate/late receipts cannot create another collection or regress a paid attempt.
- Explicit query uses authenticated HTTPS to the pinned payOS host, correlates orderCode and amount, and confirms only an exact amount received. Query payloads can contain nested transaction data; unlike the flat create-response/webhook HMAC checks, query confirmation relies on authenticated provider HTTPS. No result is inferred from Mobile timeout or browser redirect.
- Lost create ACK/crash leaves the original pending intent. Retry/reopen never calls provider Create again. A valid signed callback or explicit query may confirm that intent. Missing checkout URL remains missing until verified receipt/reconciliation; no replacement intent is invented.
- A failed/expired staff payOS attempt is not automatically replaceable with Cash or another bank attempt. Keep it held for reconciliation. No automatic release on timeout. The system does not auto-refund or reopen a PAID Bill.
- PAID uses the existing outbox to Ordering/Kitchen and existing close eligibility. Payment before service does not close the table; add-order remains blocked after PAID. QR receipts have CashierId/ShiftId null in collection capture and are counted as QR by branch, not cash in a drawer; tests enforce RequireOpenForCash=true with no open shift.

## Mobile

Existing theme/navigation retained. Main Takeaway uses the PayOS V3 route; Mock/ZaloPay explicit consumers remain. Session Bill sends the Backend-selected provider. QR is **the payOS checkout URL rendered as a scannable QR**, not a fabricated bank-transfer payload or static QR image. User-facing text says it opens the payOS page. Direct EMV/Napas payload persistence has not been added.

Strict version/provider/test marker, identity/branch/amount and checkout-host validation prevent sandbox payloads masquerading as production. Bill QR renders while pending with valid checkout evidence and exposes explicit reconciliation. GET polling and returning to Bill do not create payments. No manual-success action. Failed payOS attempts cannot open a replacement collection form; the server enforces the same hold. Existing pending/paid proof is restored by Backend reads after screen reopen.

## Secure configuration gate — not executed

User subsequently approved preparation and isolated proxy testing for a temporary HTTPS tunnel. See `PAYOS_LOCAL_WEBHOOK_TUNNEL.md`, `compose.payos-webhook.yml` and `infra/payos-webhook/nginx.conf`. Only the exact POST webhook is proposed; no operational proxy/public tunnel has been started, and browser return/result URLs remain a separate reviewed activation condition.

Subsequent isolated proxy validation: **6/6 HTTP test groups PASS** with real Nginx and synthetic upstream, including byte/header/path/method/body-size checks. Scratch containers/network removed; 17 existing container lifecycle snapshots unchanged. The operational proxy/tunnel is still not started. See `PAYOS_WEBHOOK_PROXY_TEST_RESULT.json` and the runbook for digest, fixes and browser-route proposal.

After separate browser-route approval, final proxy transport is **9/9 PASS** and focused Backend regression **83 PASS/0 SKIP**. Added static GET result page plus a forced read-only return header. Staff Takeaway always reads stored state; proxied legacy returns also read only, while direct legacy Web FE behavior is preserved. A safe no-orderCode capability probe must PASS against the newly approved Payments image before public exposure; old runtime images may ignore the header. No runtime update or public tunnel occurred. Python fixtures are transport tests only, not financial implementation.

1. Confirm intended tenant/branches, linked account and channel ownership. Audit other tenants' effective PayOS merchant settings before using this globally stored channel.
2. Run `powershell -NoProfile -File scripts/Save-PayOsLocalCredentials.ps1` from this Backend worktree when ready. All three inputs are hidden; only DPAPI ciphertext is stored under `%LOCALAPPDATA%/AICOS/payos-staff/credentials.dpapi.json`, current-user/SYSTEM ACL, no overwrite. It does not read an admin JWT, call APIs or activate anything. `-SelfTest` tests DPAPI with a sentinel and writes no files.
3. Review and approve provider/merchant configuration using the existing admin APIs and SecretProtector, plus one tenant/recipient binding. Loading secrets must remain memory-only; do not put real keys in Git, command arguments, transcripts or chat. Setting environment seed values alone does not update an existing provider row.
4. Supply an approved public HTTPS base URL and register the actual webhook with payOS. Main currently only provides localhost defaults. No tunnel/domain was provisioned by this task.
5. Review a Payment-only image/config update against the existing schema/ACL, MigrateOnStartup=false and all unrelated flags preserved. Do not replace the whole merged main stack or apply its seven unrelated pending migrations.
6. After separate approval, validate provider webhook setup and perform one controlled real-value transfer using the user's chosen amount. Check bank receipt → Bill/Order → collection outbox → Reporting without duplicates. Do not use old S01's already-recorded 208,000 VND for this test.

## Rollback / limits

Turn the staff creation flag OFF to stop new bank intents. Keep channel keys, recipient/tenant binding and the new callback/query code while any payOS attempt is pending or late money is possible. Do not roll back to an older image which only knows order payments while live Bill attempts remain. No destructive database rollback is needed or authorized; never delete attempts/journal to make totals match. Key/channel/bank-account rotation during outstanding attempts needs its own reviewed procedure.

External payOS/bank, public webhook, phone/Gateway full-stack, actual Reporting-service delivery and Web FE execution remain NOT RUN. PostgreSQL tests use a real guarded scratch database with provider/auth/service HTTP doubles. Existing session S01 remains a separate legacy reconciliation blocker. This source is **not production-ready** until these gates and real E2E evidence are completed.

Official references, rechecked 20/11/2026: [API](https://payos.vn/docs/api/), [webhook](https://payos.vn/docs/du-lieu-tra-ve/webhook/), [production-only testing](https://payos.vn/docs/moi-truong-test/).

## Validation evidence

- Backend full regression: **1,024 PASS, 69 SKIP, 0 FAIL** (BuildingBlocks 15; Identity 210; Services Integration 749/69; Smoke 50). Infrastructure tests without explicit scratch guards are skipped, not reported as successful PostgreSQL E2E.
- Final focused source: **78 PASS, 0 SKIP** after browser-return and shift-policy changes. Includes signed callback/duplicate receipt, lost creation ACK/query recovery, wrong signature/amount/account, disabled/misconfigured channel, cross-branch/Barista rejection, unsigned browser-return guard and existing PayOS/SessionBill/conflict regression. Focused counts overlap full regression.
- PostgreSQL guarded scratch runner final rerun: **25 PASS, 0 SKIP, 0 FAIL**, process exit 0; cluster `7709937460186411043`, container `aicos-billflow-postgres-cc9aee57a0`, random loopback port 57771, private network/tmpfs. Eight stages passed (1+6+1+6+6+1+2+2). Scratch cleanup completed. RequireOpenForCash=true without an open shift verifies QR remains independent of cash shifts. Provider, auth and inter-service HTTP responses are test doubles. The earlier run on cluster `7709932205809917987` also passed; do not add reruns to the count.
- Flutter full: **436 PASS, 9 SKIP**, analyzer **0 issues**; focused QR/Bill **34 PASS**, widths 360/393/430. HTTP doubles, no phone/Gateway/bank E2E.
- DPAPI helper parser and sentinel round-trip PASS. Normal hidden-input/save branch NOT RUN; no real credentials created/read. Fifty original cancellation-WIP file hashes remain unchanged in the preserved worktree.
- Migration/proposal checksums: **26 files PASS** (Session Bill, cancellation and discount manifests). PayOS HTTP headers are all redacted and automatic redirects disabled. PostgreSQL final stages built/tested this configuration; another full focused repetition was not executed after automatic command approval review became unavailable due to usage limits. The 78 PASS focused result predates only the additional header-redaction line. The failed approval was not a test failure or a safety determination; no command was executed by that rejected request.

## Changed source files for this task

- Payments infrastructure: `Infrastructure/PayOs.cs`, `Infrastructure/Gateways.cs`; `Domain/Payments.cs`, `Domain/PaymentProvider.cs`; `Program.cs`, `appsettings.json`.
- Payments API: `Features/Payments/PayOsStaffPolicy.cs`, `TakeawayPayOsEndpoints.cs`, `PayOsEndpoints.cs`; shared `TakeawaySandboxEndpoints.cs` Scope visibility only.
- Session Billing: `BillLedgerService.cs`, `BillContracts.cs`, `BillWebhookReceiver.cs`, `BillPayOsService.cs`, `SessionBillEndpoints.cs` under `Features/SessionBilling/`.
- Tests: `PayOsBillTests.cs`, `PayOsBillPostgresTests.cs`, `TakeawayPayOsTests.cs`, `SessionBillHarness.cs` under `tests/AICOS.Services.IntegrationTests/`; `scripts/Test-SessionBillingPostgres.ps1` adds guarded payOS tests.
- Secret preparation: `scripts/Save-PayOsLocalCredentials.ps1`. Mobile file list and progress are in `docs/PAYOS_STAFF_INTEGRATION.md` and `docs/IMPLEMENTATION_LOG.md` in AICOS-Mobile.

No migration/model snapshot was added or changed for payOS. Existing merge/cancellation/first-order/conflict-diagnosis work is not claimed as new payOS implementation. No files staged, committed or pushed by this task; operational Payments still uses its previous image.
