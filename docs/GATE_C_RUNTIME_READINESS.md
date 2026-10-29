# Gate C — Backend runtime readiness

Execution update: read-only Gate C was subsequently approved. Current results
and blockers are in GATE_C_EXECUTION_RESULT.md. The runtime helper now appends
docker-compose.gate-c.yml after the external-db override to avoid VietAI Redis
host-port collision and bind core API ports to loopback. The original packet
below is preparation history; use the current helper/execution report for retry.

Ngày kiểm tra: 2026-10-26. **PREPARATION ONLY — Gate C chưa được duyệt/thực thi.**

## Kết luận

Gate B đã hoàn tất: 38 bảng / 244 cột trong aicos_db.public. Gate A đã hoàn tất:
11 roles LOGIN và kiểm tra quyền/kết nối PASS theo
[GATE_A_EXECUTION_RESULT.md](GATE_A_EXECUTION_RESULT.md).
Không chạy lại migration hoặc script cấp quyền.

Cấu hình tĩnh phù hợp để chuẩn bị chạy thử có kiểm soát. **Chưa đủ bằng chứng
runtime để kết luận sẵn sàng vận hành.** Docker Engine không truy cập được,
credentials runtime chưa được truyền, token/fixtures chưa được chuẩn bị.
Không khởi động Docker Desktop, container hoặc Backend trong lần audit này.

## Phạm vi và bằng chứng

Đã đọc compose gốc/override, env example, Dockerfiles, Gateway routes/middleware,
10 Program/metadata/config/launch profiles, shared persistence/health/auth/HTTP/
Redis/logging, feature endpoints, AI config/health/routers, báo cáo Gate A/B và
PHASE5_2_CONTROLLED_DEPLOYMENT.md. Gate B report là lịch sử tại thời điểm B;
trạng thái Gate A mới nhất lấy từ Gate A execution result, không lấy dòng cũ
A chưa chạy trong báo cáo B làm trạng thái hiện tại.

Không kết nối ghi database, chạy migration, GRANT/REVOKE, đổi mật khẩu,
khởi động dịch vụ, sửa Mobile hoặc commit/push. Số bảng/cột và kết quả quyền
trên đây là bằng chứng Gate A/B đã lưu, không phải một đợt database audit mới.

## Ma trận runtime

Gateway 8080 → container 8080; không có DbContext.
Mỗi .NET service dùng container 8080. AI dùng container 8000.

| Compose service | Host port | PostgreSQL role | Environment password key | API prefix |
|---|---:|---|---|---|
| identity | 5101 | identity_svc | IDENTITY_DB_PASSWORD | identity |
| menu | 5102 | menu_svc | MENU_DB_PASSWORD | menu |
| table | 5103 | table_reservation_svc | TABLE_DB_PASSWORD | tables |
| order | 5104 | ordering_svc | ORDERING_DB_PASSWORD | orders |
| payment | 5105 | payment_svc | PAYMENT_DB_PASSWORD | payments |
| inventory | 5106 | inventory_svc | INVENTORY_DB_PASSWORD | inventory |
| kitchen | 5107 | kitchen_svc | KITCHEN_DB_PASSWORD | kitchen |
| workforce | 5108 | workforce_svc | WORKFORCE_DB_PASSWORD | workforce |
| reporting | 5109 | reporting_svc | REPORTING_DB_PASSWORD | reporting |
| customer | 5110 | customer_svc | CUSTOMER_DB_PASSWORD | customers |
| ai | 5111 | ai_svc | AI_DB_PASSWORD | ai |

Effective external DB connection cho .NET:

```text
Host=host.docker.internal;Port=5432;Database=aicos_db;Username=<role>;Password=<secret>
```

AICOS_DB_HOST/AICOS_DB_PORT có thể override; phải xác minh vẫn tới PostgreSQL
chính, không đổi database. Local native config dùng localhost:5432/aicos_db,
còn Docker phải dùng host.docker.internal, không dùng localhost.
Cả 10 service metadata đặt Schema=public; shared AicosDbContext dùng
HasDefaultSchema(public). Schema là EF mapping, không phải thuộc tính
Database trong connection string. AI khai báo schema public trong _info;
chưa có database-backed business implementation để kiểm chứng mapping thực.

**PASS:** cả 10 appsettings và launch profiles đặt MigrateOnStartup=false;
effective merged Compose cả 10 cũng false. Shared MigrateDatabaseAsync chỉ
chạy khi explicitly true. Gateway và AI không có EF migration startup.
Không có seed/EnsureCreated trên startup path đã kiểm tra.
Phải kiểm tra lại effective env trong container khi triển khai; không dùng
dotnet ef database update hoặc migrate tooling ở Gate C.

## Credentials và secrets

Gate A đã kiểm chứng password, nhưng không lưu lại password cho runtime.
Hiện tại: không có .env tại Backend; cả 11 password keys và JWT_SIGNING_KEY
đều thiếu trong process environment được audit. Đây là **blocker triển khai**,
không phải lý do reset role hoặc chạy lại GRANT.

Dùng đúng 11 mật khẩu người dùng đã đặt ở Gate A. Gateway, .NET và AI dùng
cùng JWT_SIGNING_KEY mạnh (ít nhất 32 ký tự), issuer aicos, audience
aicos-clients. Không dùng development signing key cho runtime được duyệt.

Ưu tiên process environment từ console nhập ẩn/secret manager, không ghi .env
plaintext. Docker administrators vẫn có thể đọc container environment;
đây không phải cơ chế bảo mật trước người có quyền quản trị Docker.
Không chạy console transcript, config render ra terminal, docker inspect toàn
bộ, printenv/env toàn bộ hoặc ghi Authorization/connection strings vào log.

Bộ lệnh dưới chỉ là chuẩn bị cho lần thực thi được duyệt; chưa chạy. Nó nhập
ẩn, quote password cho .NET connection-string grammar và URL-encode riêng AI.
Không đổi password trong PostgreSQL. Không truyền secret qua command arguments.
Không chép password trực tiếp vào lệnh/history.

```powershell
Set-Location 'D:\ki9\sep490\Sep490\AICOS-BE'
function Read-HiddenValue([string]$Prompt) {
    $secure = Read-Host $Prompt -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
        $secure.Dispose()
    }
}
$roleKeys = @(
    'IDENTITY_DB_PASSWORD','MENU_DB_PASSWORD','TABLE_DB_PASSWORD',
    'ORDERING_DB_PASSWORD','PAYMENT_DB_PASSWORD','INVENTORY_DB_PASSWORD',
    'KITCHEN_DB_PASSWORD','WORKFORCE_DB_PASSWORD','REPORTING_DB_PASSWORD',
    'CUSTOMER_DB_PASSWORD','AI_DB_PASSWORD'
)
try {
    foreach ($key in $roleKeys) {
        $raw = Read-HiddenValue "Gate A credential: $key"
        if ([string]::IsNullOrEmpty($raw)) { throw "Missing credential: $key" }
        $encoded = if ($key -eq 'AI_DB_PASSWORD') {
            [Uri]::EscapeDataString($raw)
        } else {
            '"' + $raw.Replace('"', '""') + '"'
        }
        [Environment]::SetEnvironmentVariable($key, $encoded, 'Process')
        $raw = $null
        $encoded = $null
    }
    $jwt = Read-HiddenValue 'Shared JWT signing key'
    if ([string]::IsNullOrWhiteSpace($jwt) -or $jwt.Length -lt 32) {
        throw 'JWT signing key must have at least 32 characters'
    }
    $env:JWT_SIGNING_KEY = $jwt
    $jwt = $null
    $env:AICOS_DB_HOST = 'host.docker.internal'
    $env:AICOS_DB_PORT = '5432'
    $env:PAYMENT_MUTATIONS_ENABLED = 'false'

    # Base Compose interpolates this even though postgres profile is excluded.
    # Non-secret sentinel, NOT the host administrator password.
    $env:POSTGRES_PASSWORD = 'unused-external-db-profile'
    $compose = @('--env-file','.env.example','-f','docker-compose.yml',
                 '-f','docker-compose.external-db.yml')
    docker compose @compose config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Runtime configuration invalid' }

    # STOP HERE until Gate C approval. Execute approved stages below in this
    # same try scope/session, so required credentials remain available.
    # Finally must run on success or failure.
}
finally {
    foreach ($key in ($roleKeys + @(
        'JWT_SIGNING_KEY','POSTGRES_PASSWORD','AICOS_DB_HOST',
        'AICOS_DB_PORT','PAYMENT_MUTATIONS_ENABLED'
    ))) { [Environment]::SetEnvironmentVariable($key, $null, 'Process') }
    $raw = $null
    $encoded = $null
    $jwt = $null
}
```

When executing after approval, place the approved stage commands BEFORE the
closing try/finally; do not run the complete snippet and then expect secrets to
remain. Run in a dedicated console to avoid overwriting unrelated process env.
Transient managed plaintext is necessary for process env; no plaintext file or
logging is used. A failed configuration aborts and clears process env.

.env.example supplies non-secret placeholders only; all 11 keys/JWT must have
been explicitly loaded, never accept placeholders as runtime credentials.
Its comment that passwords are consumed only on fresh init is outdated:
runtime services also consume them. AI_DB_PASSWORD must be URI-encoded, whereas
the ten .NET passwords above are connection-string quoted. Do not reuse one
encoded representation for the other consumer.

## Compose, discovery và dependencies

- **PASS static:** external override excludes bundled postgres via
  provisioning-only profile; replaces every .NET/AI dependency with Redis
  service_healthy; each DB target fixed to aicos_db.
- Never use base Compose alone, enable provisioning-only/tools profiles,
  start postgres/pgadmin or reset volumes. Bundled PostgreSQL image is version
  16; official host database is version 18. They are not interchangeable.
- Redis 6379, persistent AOF volume, healthcheck redis-cli ping.
- Gateway depends_on lists ALL 11 services with started semantics, not
  application readiness. A generic compose up gateway would start placeholders
  and AI too. Use explicit services and --no-deps in the approved sequence.
- DNS destinations: gateway → service:8080, AI:8000. Typed clients call direct
  service DNS, not Gateway. Table→Payment; Ordering→Table/Menu;
  Payments→Ordering. The shared handler forwards bearer and X-Correlation-Id.
  Standard HTTP resilience is enabled; do not infer business idempotency from
  transport retries.
- No .NET/Gateway/AI container healthchecks exist. Only Redis/Postgres have
  Compose healthchecks. Docker running/compose wait alone cannot establish
  readiness: explicitly poll HTTP at every stage.
- No active event handler subscriptions are registered (commented examples
  excluded). The Redis subscriber exits when none exist. Redis connection is
  lazy, AbortOnConnectFail=false; readiness still must pass.
- Redis Pub/Sub has no durable replay/retry for failed handlers; do not send
  test events or claim event-delivery guarantees during read-only smoke.
- Current ports: only PostgreSQL 5432 listening; no 6379, 8080, 5101–5111
  listener observed. Recheck immediately before deployment.
- Payment maintenance protects Payment endpoints only. Identity/Table/Ordering/
  Kitchen can still write when running: keep Mobile and other clients disconnected
  during the controlled smoke window and restrict access to the operator. Startup
  approval must not be interpreted as approval for other clients to write.
- Development environment enables Swagger and detailed 500 exception text;
  all published ports bind host interfaces by default. Keep controlled local
  access; this packet does not approve public exposure or production hardening.

## Health và logging

| Component | Implemented checks | Limitation |
|---|---|---|
| .NET x10 | /health/live; /health/ready PostgreSQL + Redis | Live excludes deps; ready does not validate every EF column/table permission |
| Gateway | /health/live; GET / | No /health/ready and no downstream health aggregation |
| AI | /health/live; /health/ready TCP PostgreSQL/Redis | Does not authenticate DB role or execute SQL; green TCP is insufficient |

.NET/Gateway use Serilog console, Service and correlation id; shared HTTP
middleware propagates correlation. AI returns a correlation header and has
Uvicorn access logs, not identical .NET structured logging. Request logging
does not explicitly log bodies or bearer headers in inspected configuration;
exception logs may include operational details. Inspect/sanitize locally before
sharing any log. Payment maintenance runs before shared middleware, so its 503
need not have the shared correlation header or service request log entry.

## Chức năng và quyền chưa hoàn thiện

Inventory, Workforce, Reporting, Customer have empty feature DbContexts and
only metadata/health routes. Their roles intentionally have no business-table
grants. AI feature routers return 501; no provider credentials needed for
health/metadata checks, and no business-table privileges are granted.
Treat them as skeletons, not functional modules. Recommend excluding these
five from the initial Gate C scope. Starting them later needs an explicit
service list and health-only acceptance, not extra grants or fake business data.
Their Gateway routes will remain unavailable while they are stopped.

Kitchen has real queue/status operations, but accesses Ordering-owned tables
directly under the limited Gate A exception (SELECT orders/order_items/events,
column UPDATE order_items.status, INSERT order_item_events). This remains
architecture debt; Gate C does not redesign the boundary or broaden grants.
No Kitchen PATCH is allowed on production for this smoke.

## Smoke tests chuẩn bị

**None of these runtime requests has been executed in this preparation.**
Use existing authorized branch-scoped data; do not seed production.
Capture status, elapsed time and correlation id, not tokens or PII response
bodies. Validate response contract privately; do not equate every 2xx with
correct business data.

| Service | Read-only request | Expected/acceptance |
|---|---|---|
| Identity | GET /api/v1/identity/auth/me | 401 without token; valid staff JWT → 200 correct identity/branch |
| Menu | GET /api/v1/menu/catalog | 401 without token; JWT → 200 real catalog (empty valid only if consistent with DB) |
| Table | GET /api/v1/tables/map; /sessions/{id} | JWT → authorized branch map/session; no cross-branch data |
| Ordering | GET /api/v1/orders/?sessionId={id}; /{id} | JWT → actual orders in scope; existing fixtures required for positive detail |
| Kitchen | GET /api/v1/kitchen/queue | JWT → branch queue; empty must match DB |
| Payment | GET /api/v1/payments/bills?sessionId={id} OR ?orderId={id}; /bills/{id} | Exactly one source; positive bill 200 requires existing bill |
| Payment | GET /api/v1/payments/sessions/{id}/settlement-status | Authorized request; no bill → settled=false, not permission to close |
| All x6 | Direct GET /health/live and /health/ready; /api/v1/{prefix}/_info | 200; _info schema public, expected service |
| Gateway | GET /health/live and /; each x6 routed /api/v1/{prefix}/_info | 200; service identity/schema match direct endpoint |

For protected GETs, invalid token must yield 401 at Gateway, and an actual
wrong-branch token must not reveal data (reviewed 403/404/filtered collection).
Cross-branch fixtures/token unavailable means NOT RUN, not PASS.
Last Gate A/B evidence has no financial rows; therefore existing-bill 200
and successful cash/QR paths cannot be demonstrated on main by inventing data.
Readiness/empty lists/404 do not replace those tests on aicos_payment_test.

Identity login, refresh, logout and provisioning **write refresh/session/user
data**. They are not read-only smoke even if only obtaining a token.
Provide a legitimate existing unexpired JWT through hidden input; do not mint
one artificially. If none exists, request explicit scope for normal login in
a later gate; authenticated smoke remains blocked meanwhile.

All valid bill creation, cash/QR, QR confirmation, close bill/table session,
table open/clean, order create, Kitchen PATCH are **write tests**:
not authorized on aicos_db by this packet. Run only on isolated approved test
DB/environment. QR evidence and cashier confirmation must remain mandatory.

Optional maintenance rejection probe is a non-GET HTTP request, expected NOT
to write data. Requires explicit inclusion in Gate C approval; not executed:

```powershell
curl.exe -i -X POST http://127.0.0.1:5105/api/v1/payments/bills -H 'Content-Type: application/json' --data '{}'
curl.exe -i -X POST http://127.0.0.1:8080/api/v1/payments/bills -H 'Content-Type: application/json' --data '{}'
```

No token or valid source IDs: expect 503 + Retry-After:60. Anything else FAIL;
stop inspection without attempting a valid payment. This probe is separately
classified from the GET-only set.

## Service-to-service verification

1. Gateway routing: compare direct and routed _info, then protected GET results;
   send a non-sensitive X-Correlation-Id and match sanitized downstream traces.
2. Payments→Ordering: billing source GET verifies source access through Ordering
   before bill lookup. Use an existing owned session/order plus legitimate JWT.
   Match correlation in both services. An empty/nonexistent source with 404 does
   not prove positive-path business behavior.
3. Ordering→Table/Menu occurs on order creation; Table→Payment settlement guard
   occurs on table closure. Do not trigger these mutation endpoints to test DNS.
   Their real business-path tests belong on aicos_payment_test/isolated runtime.
4. With approved services running, internal GETs to target _info using a temporary
   HttpClient in a running .NET container can verify DNS/HTTP reachability.
   This is not proof of token forwarding, settlement guard or mutation success.
   Do not introduce a new debug endpoint or tool container on production.
5. Settlement failures must fail closed. Unit/integration evidence must cover
   unpaid bills and Payments outage; no production close-session smoke.

## Exact proposed execution order — only after separate approval

Suggested scope: Redis + identity, menu, table, order, payment, kitchen +
Gateway. Exclude bundled PostgreSQL, pgAdmin, inventory, workforce, reporting,
customer, AI. A/B completion does not authorize these commands.

1. Operator confirms scope, token source, read-only smoke and whether optional
   maintenance rejection is included. Verify baseline backup/evidence and no
   unexpected writer/port conflict. Docker Desktop may autostart unrelated saved
   containers: operator must review restart policies before starting Engine.
2. Start Engine only when approved; verify context desktop-linux and inventory
   running containers. Do not reset/prune. Recheck images exist, since historical
   successful builds are not proof that images still exist today.
3. Load credentials in dedicated masked console using above try/finally.
   Quiet Compose validation must PASS; frozen image inspection and local env
   checks must confirm real credentials, false migration flags, payment writes
   false. Do not display config JSON containing secrets.
4. Use the following commands one stage at a time INSIDE the credential scope.
   Check every native exit code; do not paste all stages as one blind batch:

```powershell
docker context show
docker version --format '{{.Server.Version}}'
docker ps --format '{{.Names}} {{.Status}}'
$services = @('identity','menu','payment','table','order','kitchen','gateway')
foreach ($service in (@('redis:7-alpine') + ($services | ForEach-Object { "aicos/$_" }))) {
    docker image inspect --format '{{.Id}}' $service
    if ($LASTEXITCODE -ne 0) { throw "Missing image: $service" }
}
docker compose @compose config --quiet
if ($LASTEXITCODE -ne 0) { throw 'Compose validation failed' }

docker compose @compose up -d --no-deps --no-build --pull never redis
if ($LASTEXITCODE -ne 0) { throw 'Redis start failed' }
docker inspect --format '{{.State.Health.Status}}' aicos-redis
# Require healthy; if starting, poll with a bounded timeout, never continue blind.
```

Then start each .NET service explicitly in this order, replacing NAME with the
literal name at each step: identity → menu → payment → table → order → kitchen.
After each, run live/ready at its host port and inspect false migration flag.
Payment must also report mutations false. Timeout/failure stops further starts.

```powershell
# Example first service; repeat individually using the reviewed port matrix.
docker compose @compose up -d --no-deps --no-build --pull never identity
if ($LASTEXITCODE -ne 0) { throw 'Identity start failed' }
curl.exe --fail-with-body http://127.0.0.1:5101/health/live
if ($LASTEXITCODE -ne 0) { throw 'Identity liveness failed' }
curl.exe --fail-with-body http://127.0.0.1:5101/health/ready
if ($LASTEXITCODE -ne 0) { throw 'Identity readiness failed' }
docker compose @compose exec -T identity printenv Database__MigrateOnStartup
if ($LASTEXITCODE -ne 0) { throw 'Migration flag inspection failed' }
# Require literal false. Do not print all environment variables.
# For payment additionally:
docker compose @compose exec -T payment printenv Payments__MutationsEnabled
# Run only AFTER payment started; require false and check exit code.
```

Exact remaining startup commands (interleave the same checks; no automatic
dependency starts). No build/pull or new image version is implicit:

```powershell
docker compose @compose up -d --no-deps --no-build --pull never menu
docker compose @compose up -d --no-deps --no-build --pull never payment
docker compose @compose up -d --no-deps --no-build --pull never table
docker compose @compose up -d --no-deps --no-build --pull never order
docker compose @compose up -d --no-deps --no-build --pull never kitchen
# ONLY after all six direct live/ready/metadata checks PASS:
docker compose @compose up -d --no-deps --no-build --pull never gateway
curl.exe --fail-with-body http://127.0.0.1:8080/health/live
# Check each exit code, stop on failure. Gateway has no /health/ready.
```

GET-only metadata checks, no auth needed; execute only after C approval:

```powershell
$targets = @(
    @{Port=5101;Prefix='identity'}, @{Port=5102;Prefix='menu'},
    @{Port=5103;Prefix='tables'}, @{Port=5104;Prefix='orders'},
    @{Port=5105;Prefix='payments'}, @{Port=5107;Prefix='kitchen'}
)
foreach ($target in $targets) {
    foreach ($port in @($target.Port,8080)) {
        $uri = "http://127.0.0.1:$port/api/v1/$($target.Prefix)/_info"
        $info = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 15
        if ($info.schema -ne 'public') { throw "Unexpected schema on port $port" }
        # Also require service name matching the service/prefix matrix.
        Write-Host "$port $($target.Prefix) schema=public"
    }
}
```

Authenticated GET example: use hidden token, not curl bearer arguments or
console history. Replace IDs only with existing reviewed fixtures; no random
IDs treated as successful detail tests. Store response privately, display only
status/contract checks. Do not print $headers or response bodies.

```powershell
$token = Read-HiddenValue 'Existing legitimate staff access token'
$headers = @{Authorization="Bearer $token";'X-Correlation-Id'='gate-c-readonly'}
try {
    $me = Invoke-RestMethod 'http://127.0.0.1:8080/api/v1/identity/auth/me' -Headers $headers -TimeoutSec 15
    $catalog = Invoke-RestMethod 'http://127.0.0.1:8080/api/v1/menu/catalog' -Headers $headers -TimeoutSec 15
    $map = Invoke-RestMethod 'http://127.0.0.1:8080/api/v1/tables/map' -Headers $headers -TimeoutSec 15
    $queue = Invoke-RestMethod 'http://127.0.0.1:8080/api/v1/kitchen/queue' -Headers $headers -TimeoutSec 15
    # Apply reviewed assertions and run Ordering/Payment GETs from smoke matrix
    # with actual owned session/order IDs; no login or mutation here.
} finally {
    $headers.Clear()
    $token = $null
    $me = $catalog = $map = $queue = $null
}
```

5. Record PASS/FAIL/NOT RUN per endpoint and direct/routed path, no secrets.
   Keep PAYMENT_MUTATIONS_ENABLED=false after smoke; enabling writes requires
   another explicit approval. Gate C is not an approval to test payments on main.
6. On failure: stop further starts, retain evidence and propose stopping only
   containers started by this gate. Recovery command to present before action:

```powershell
docker compose @compose stop gateway kitchen order table payment menu identity
# Stop redis only if this gate started it and no other consumer needs it.
```

No compose down -v, SQL rollback, restore over main, role/grant changes or schema
changes are part of runtime recovery.

## Kết quả kiểm tra lần này

| Check | Result |
|---|---|
| Compose config --quiet with example credentials | PASS exit 0 |
| Merged config: external target, 10 false flags, Payment false, Redis-only service dependencies | PASS |
| Automated merged-config invariant assertions | PASS: 10 targets/flags/dependencies, Payment false, bundled postgres excluded |
| Embedded PowerShell examples | PASS: 8 code blocks parsed with zero syntax errors; not executed |
| Markdown diff whitespace check | PASS for tracked changelog; new report checked separately |
| 10 service schemas / local launch flags / JWT issuer+audience | PASS static |
| Docker context | desktop-linux |
| Docker Engine version/ps/image inspection | BLOCKED: pipe dockerDesktopLinuxEngine absent; Engine not started |
| Host listener audit | PostgreSQL 5432 only among reviewed ports |
| Real credentials injection | BLOCKED: .env absent and 12 required process keys absent |
| Fresh container-to-host PostgreSQL authentication | NOT RUN; requires Engine/runtime secrets and Gate C approval |
| Runtime HTTP, protected GET, Gateway/S2S smoke | NOT RUN; services not started, token/fixtures unavailable |
| .NET/Python tests or image build | NOT RUN in this documentation/config audit |
| Production database writes/migrations | NOT RUN |

No skipped test is reported as a passing test. Earlier Phase 5.1 build/unit/
PostgreSQL test results remain historical; not evidence of a fresh runtime
smoke. Existing ServiceSmokeTests boots in-memory test hosts and does not prove
PostgreSQL/Redis readiness. Optional PostgreSQL tests skip without
AICOS_TEST_POSTGRES. Mutation integration tests must target aicos_payment_test,
never main; do not run the broad suite against aicos_db for this gate.

## Blockers và điều kiện phê duyệt

| Priority | Blocker/gap | Resolution before/within approved Gate C |
|---|---|---|
| P0 | No Engine connection/image inventory | Operator-controlled Engine start, inspect existing containers/images; no reset |
| P0 | Runtime credentials/JWT missing | Hidden process injection from Gate A secrets; validate quiet config; no password reset |
| P0 | Container → host PostgreSQL not revalidated | Role-specific readiness and owned-table API reads; review pg_hba/firewall only if denied, do not auto-change |
| P0 authenticated smoke | No legitimate current JWT | Supply trusted token; normal login is separate write scope |
| P1 positive-path coverage | Empty financial tables / missing existing fixtures | Test DB evidence; mark main positive paths NOT RUN rather than seed |
| P1 orchestration | Gateway all-service dependencies; no app health gates | Explicit --no-deps staged sequence with HTTP checks |
| P1 observability | AI TCP-only ready, Gateway live-only | Document limitations, keep AI out of initial scope, verify Gateway routes explicitly |
| P1 architecture | Kitchen legacy direct Ordering table writes | Retain reviewed exception, no Kitchen mutations during main smoke |
| P1 exposure | Development mode, public host port bindings | Controlled local access only; separate production hardening plan |

Approval requested NEXT, not assumed: exact service list and Docker Engine start,
read-only HTTP scope, credential input method and token source; separately decide
whether to include the invalid maintenance-rejection POST. No Payment write
release, migrations, grant operations, login/provisioning or positive mutation
smoke are included by default.

**Dừng tại đây chờ chủ dự án phê duyệt thực thi Gate C.**

