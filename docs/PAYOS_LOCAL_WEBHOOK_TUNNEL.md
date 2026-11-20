# payOS HTTPS tunnel — preparation only, 20/11/2026

User approved preparing/testing the proxy, then adding two read-only browser GET routes, **not opening public ingress or enabling real-money payments**. Two scratch containers per run were started and removed for transport testing. An official Nginx test image was downloaded; no cloudflared package/public URL/provider credentials were configured. No operational container/configuration was changed.

## Restricted route

Future path: payOS → Cloudflare Quick Tunnel → loopback `127.0.0.1:18081` → dedicated Nginx proxy → existing Gateway → Payments HMAC webhook.

`compose.payos-webhook.yml` is a standalone Compose project, not an override for the application stack. It has one optional proxy container, no database credentials/mounts, no app dependencies or automatic restart. It attaches only to the reviewed existing Gateway network; never automatically creates/recreates Gateway or a database. This is temporary ingress infrastructure, not a new business microservice.

`infra/payos-webhook/nginx.conf` now permits exactly:

- POST `/api/v1/payments/payos/webhook`: exact raw path, no query, body forwarded unchanged for existing HMAC verification.
- GET `/api/v1/payments/payos/return`: exact raw path with optional query, no request body/credentials; the proxy overwrites `X-Aicos-Return-Read-Only: 1`.
- GET `/payos/result`: static Vietnamese HTML from `infra/payos-webhook/result.html`, no API call/status inference/query reflection; no-store/no-referrer/nosniff/CSP headers.

Other methods return 405; other/encoded/normalized paths return 404. Incoming authorization/cookie/forwarding/custom headers are not passed upstream. No proxy retry or access/body logging. Loopback-only port, non-root UID, read-only filesystem, dropped capabilities and a small temporary filesystem limit exposure. Nginx error logging is critical-only; troubleshoot in local Payments with secret redaction.

## Review before execution

1. Verify Gateway container/network/alias `gateway:8080`; record existing container IDs/images/start times. Check loopback port 18081 is unused. Stop if alias/network/port differ. Do not recreate existing services to satisfy this proposal.
2. Select an official reviewed Nginx image with executable `nginx`, UID 101 compatibility and immutable digest. Set `AICOS_PAYOS_PROXY_IMAGE` to that digest and `AICOS_PAYOS_GATEWAY_NETWORK` to the verified network name. Neither is a credential. No default image tag/network is silently selected.
3. Validate standalone Compose and run Nginx configuration syntax check in a disposable container **after approval**. No network/provider/DB checks are implied by static validation.
   Before exposing these GET routes, deploy the separately approved updated Payments image. Probe GET `/api/v1/payments/payos/return` **without orderCode**, with `X-Aicos-Return-Read-Only: 1`, through the local proxy. Require 302, `X-Aicos-Return-Mode: read-only`, no-store and the approved fixed result URL. This probe cannot identify a payment or trigger a provider query. Stop if the capability header is absent: an old image can ignore the header and run legacy payment-query mutations. A config-only proxy rollout is therefore insufficient.
4. Start only the proxy after approval. Test rejected GET/PUT/DELETE/query/encoded-path/admin/login routes locally. Do not POST to the operational webhook during proxy validation; use a fake upstream on an isolated test network to verify body preservation and headers.
5. Review/install Cloudflare's official `cloudflared` separately. Only after approval start `cloudflared tunnel --url http://127.0.0.1:18081`. Never point it directly at 8081, Gateway, Payments or PostgreSQL. No email-auth challenge may sit in front of a noninteractive payOS webhook.
6. Confirm the public URL rejects unrelated routes; then separately approve provider setup, hidden credential loading and webhook registration. No financial feature is enabled by merely opening a tunnel.

Commands below are a runbook, **not executed**. Substitute reviewed non-secret environment values first; do not run against the merged operational Compose configuration.

```powershell
docker compose --env-file NUL -f compose.payos-webhook.yml config --quiet
docker compose --env-file NUL -f compose.payos-webhook.yml run --rm --no-deps webhook-proxy -t
docker compose --env-file NUL -f compose.payos-webhook.yml up -d --no-deps webhook-proxy
cloudflared tunnel --url http://127.0.0.1:18081
```

## Browser code completed; runtime/public activation remain separate

Two browser GET routes were approved and implemented in source/test, not deployed. Configure the eventual approved HTTPS base URL with ReturnUrl `/api/v1/payments/payos/return`, ResultPageUrl `/payos/result`, and provider callback `/api/v1/payments/payos/webhook`. Do not substitute localhost or bypass eligibility. The current operational Payments image has not been updated and must not be publicly exposed behind these routes before the safe capability probe passes.

## Verification status

- Source route/method/body/secret-boundary review: completed.
- Standalone Compose parse: **PASS**, exit 0 with deliberately dummy image digest and network name, `--env-file NUL`; no Engine request/start/image pull. Docker emitted a warning that its user config was inaccessible in the sandbox; parsing still completed. This is not Docker authentication or runtime readiness evidence.
- Missing-image and missing-network guards: **PASS**, Compose refuses both cases before execution. No placeholder values were saved into operational configuration.
- Nginx 1.30.7 runtime syntax/non-root UID 101/read-only filesystem: **PASS** on official `nginx@sha256:caf4e1e032a20f9c3c5e28fc7f25ffd768ad8677b4975c54193075997b3a97e3`. This is a tested local image, not a production security certification.
- Final actual HTTP transport: **9/9 test groups PASS, 0 FAIL, 0 SKIP**, including the original six groups plus forced-readonly browser return/body stripping, static no-store/CSP/result anti-reflection, and browser path/method guards. Raw UTF-8/chunked body and 32 KiB limit remain verified. Upstream is a synthetic Python fixture, not Gateway/Payments/payOS.
- Final network `aicos-payos-proxy-test-456e07010e` was internal with no host ports/volumes/real credentials. Both scratch containers/network removed; all **17** pre-existing containers retained ID/image/StartedAt/state. The previous webhook-only 6/6 run also passed (network `aicos-payos-proxy-test-478cbfa1f1`, cleanup independently checked). Do not add reruns to the count. Database access was not performed, so no DB fingerprint comparison is claimed.
- Backend focused payOS/Bill/conflict regression after browser code: **83 PASS, 0 FAIL/SKIP**, process exit 0. Tests prove Staff returns stay read-only with creation flag OFF, proxy read-only applies to legacy attempts even if the provider would report PAID/CANCELLED, unsigned URL status cannot confirm money and old Web FE returns retain their behavior. Provider/auth/DB doubles, not bank/Gateway E2E. Full Backend 1,024 PASS/69 SKIP and PostgreSQL 25 PASS are earlier results, not rerun for these GET changes.
- Actual Gateway network/DNS, Cloudflare tunnel, public HTTPS and payOS provider: **NOT RUN**. Syntax/transport results do not prove public callback delivery.
- Operational services/database/keys/JWT/feature flags: unchanged. No migration, seed, financial request, deploy, commit or push.

## Reproducible isolated test

`scripts/Test-PayOsWebhookProxy.ps1` defaults to plan-only. `-Run` requires an immutable local Python image; the verified local AI image is reused only as a Python runtime with entrypoint overridden, no application startup/runtime environment copied. The runner pins the Nginx digest above and refuses missing images rather than silently pulling. `tests/fixtures/payos-webhook/upstream.py` and `checks.py` use Python standard library only. IDs/labels/network isolation are checked before cleanup; only this run's resources are removed. Evidence: `PAYOS_WEBHOOK_PROXY_TEST_RESULT.json`.

The first harness attempts exposed PowerShell argument binding/array-enumeration errors; both were fixed. An existing Nginx cache digest `20316569...` failed even bare `nginx -v` with input/output error (BusyBox also failed); it was preserved, not repaired/deleted. A separately downloaded official stable image ran successfully. Runtime testing then found unused module temp directories defaulted to read-only `/var/cache/nginx`; FastCGI/uWSGI/SCGI temp paths now use the existing `/tmp` tmpfs. No filesystem security restriction was relaxed. The final rerun passed.

## Browser safety contract — source implemented, not publicly exposed

User approved these two read-only routes:

- GET `/api/v1/payments/payos/return`: Bill reads stored status. Staff Takeaway attempts marked `takeaway-payos:v1:` always read stored state, even if a client omits/forges the proxy header. Proxy header also opts legacy attempts into read-only mode. Direct legacy Web FE behavior is retained when it does not opt in. Redirect is the configured result URL, never a query-supplied destination. Read-only response capability is `X-Aicos-Return-Mode: read-only`; no-store applies to returns. Tests prove no provider query or payment-success event from browser reads.
- GET `/payos/result`: serve a static Vietnamese page inviting the customer back to AICOS/the cashier to check status. Ignore status/amount/identifier query values, do not show PAID from browser input, no API lookup/login/form, no secrets/cache. No financial confirmation occurs on this page.

Every other route/method stays blocked; expanded allowlist passed isolation tests. Creation remains OFF until tenant/receiver, hidden keys, public callback registration, approved Payment image/config and a separately approved low-value transfer are ready. No real gateway/phone/payOS test has run.

Python here is a standard-library HTTP **test fixture** only. It does not implement Payments, hold production keys, run inside Mobile or join the operational network. The existing AI image supplied a Python executable with its application entrypoint overridden; the AI Service was not started/restarted by tests.

## Stop / recovery

Stop the dedicated cloudflared process to revoke that temporary public URL; stop only this standalone proxy project. No `down -v` or database rollback. With real pending payments, first pause new creates and arrange continuous callback/query reconciliation before shutting down ingress. Preserve receipts/keys/attempts for late money. A Quick Tunnel has a changing hostname/no uptime guarantee, so it is only suitable for supervised testing, not production payment availability.

Official sources: [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/), [Nginx exact locations](https://nginx.org/en/docs/http/ngx_http_core_module.html#location), [request headers](https://nginx.org/en/docs/http/ngx_http_proxy_module.html#proxy_pass_request_headers).
