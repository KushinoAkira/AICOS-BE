# Gate C — Execution result (in progress)

## 2026-10-29 — Gateway PASS_RUNTIME_ROUTING; Gate C overall PARTIAL — STOP

Gateway-only execution completed. Runtime, internal DNS/TCP, all 11 configured
routes and missing/invalid JWT rejection passed. The broader Gate C result is
**PARTIAL**, because authorized business payloads/end-to-end workflows remain
untested; four .NET modules are skeletons and AI is a placeholder. This is not
a deployment/production-readiness approval. No next phase was started.

### Architecture, startup and configuration

The repository uses **YARP 2.3.0**, not Ocelot: the Gateway csproj references
Yarp.ReverseProxy, Program registers AddReverseProxy and loads ReverseProxy
configuration. No Ocelot configuration or package was found/introduced.

Audited Gateway Program, bearer middleware, shared auth/logging/core, base/
development appsettings, Dockerfile and merged Compose. Gateway has no
DbContext, PostgreSQL/Redis client, migration/seed registration, background
writer or database startup path. MigrateOnStartup is N/A to Gateway; the
previous ten .NET services retain false and Payment mutations remain false.
No source/security/configuration fix was needed for Gateway.

Packaged appsettings.json SHA256 matches the reviewed source:
730ba1c951627ebfc7842811c5a877b29286289798d11f0c4e9340306070261f.
The existing loopback Gate C port binding was already correct; no new
host-gateway mapping is required for service-name routing.

Existing image used without rebuild:
sha256:ffc612cd467d794c26e844b5ba9d04bfe51ebcacc959857757b35915af9fb985.
Actual runtime exposure: 127.0.0.1:8080 -> container 8080. Dependencies declare
the eleven existing services. Startup used --no-deps to avoid restarting or
recreating any dependency. Gateway only receives its existing shared JWT key
and downstream addresses, not database credentials.

All 11 path matches preserve the upstream path/query; no rewrite/strip
transform exists. Match.Methods is unset, so the proxy forwards HTTP methods
and each downstream enforces its own endpoint methods/policies. Only GET was
exercised; non-GET routing is NOT TESTED.

BearerTokenValidationMiddleware checks any Authorization header and rejects
failed authentication with 401. Requests without a header pass downstream;
the six implemented .NET modules enforce Staff/role policies on protected
GETs. Public _info is anonymous. Thus public GET without bearer=200,
invalid bearer at Gateway=401, and missing bearer on protected GET=401 are
the expected, independently tested behaviors. No security was disabled.
Valid-token role/branch/tenant rules are NOT TESTED.

Existing DPAPI credentials were reused in process memory only for private
merged-config comparison and READ ONLY owner-role snapshots; JWT was reused
from Identity. No password/key reset, secret file tracked, credential dump or
JWT generation/login. Process environment was restored after execution.

Exact scoped startup in the reviewed secret context:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never gateway
```

.env.example alone does not contain runtime credentials. No whole-stack up,
build, dependency restart or migration/seed command was issued.

### Gateway endpoint checks

| GET / check | Result |
|---|---|
| / | PASS 200; gateway metadata |
| /health/live | PASS 200 |
| /health/ready | N/A; expected 404, no aggregated readiness implementation |
| /swagger/index.html | N/A; expected 404 |
| /swagger/v1/swagger.json | N/A; expected 404 |
| /openapi.json | N/A; expected 404 |
| Correlation header on each routed _info | PASS, present |
| Unexpected 502/503 / DNS / timeout / proxy error flags | None observed in checks or last 200 log lines |

Gateway has no Swagger/OpenAPI registration or separate /_info; root / is its
metadata endpoint. The expected 404s above are capability checks, not runtime
failures. Do not interpret live=200 as aggregated downstream readiness.

### Per-route execution matrix

For EVERY row below, DNS resolution and TCP were tested INSIDE the running
Gateway to the named destination. GET <prefix>/_info returned 200, matched the
direct service's name and public schema; an invalid bearer returned 401.
Every route therefore has PASS_ROUTING. Business status remains separate.

| Prefix | Downstream | DNS/TCP + metadata | Invalid bearer | Business coverage |
|---|---|---|---|---|
| /api/v1/identity | identity:8080 | PASS, 200 | PASS, 401 | NOT TESTED authorized profile/auth workflows |
| /api/v1/menu | menu:8080 | PASS, 200 | PASS, 401 | NOT TESTED authorized catalog payload |
| /api/v1/tables | table:8080 | PASS, 200 | PASS, 401 | NOT TESTED authorized map/session/workflows |
| /api/v1/orders | order:8080 | PASS, 200 | PASS, 401 | NOT TESTED authorized list/detail/workflows |
| /api/v1/payments | payment:8080 | PASS, 200 | PASS, 401 | NOT TESTED authorized bill/settlement/payments |
| /api/v1/inventory | inventory:8080 | PASS, 200 | PASS, 401 | N/A: no implemented business API/DbSet |
| /api/v1/kitchen | kitchen:8080 | PASS, 200 | PASS, 401 | NOT TESTED authorized queue/preparation |
| /api/v1/workforce | workforce:8080 | PASS, 200 | PASS, 401 | N/A: no implemented business API/DbSet |
| /api/v1/reporting | reporting:8080 | PASS, 200 | PASS, 401 | N/A: no implemented business API/DbSet |
| /api/v1/customers | customer:8080 | PASS, 200 | PASS, 401 | N/A: no implemented business API/DbSet |
| /api/v1/ai | ai:8000 | PASS, 200 | PASS, 401 at Gateway | N/A business GET; POST/model work NOT TESTED |

No IP was hardcoded. DNS uses Docker service names on aicos-be_default;
host.docker.internal is not needed for Gateway's downstream service routing.
All destinations in effective config and running Gateway environment matched
the approved service names/ports. Upstream prefixes, including plural tables,
orders, payments and customers, matched the downstream source route groups.

### Protected GET authentication matrix

Each row below was tested directly WITHOUT a token (401), through Gateway
WITHOUT a token (401), and through Gateway WITH an opaque invalid bearer (401).
All three checks PASS for every row. Authorized 200 payload is NOT TESTED.
The fixed placeholder GUID identifies no created fixture; auth rejection
occurs before the business handler. No login/refresh/account/JWT request ran.

| GET path (id = 11111111-1111-1111-1111-111111111111) | Direct absent | Gateway absent | Gateway invalid |
|---|---|---|---|
| /api/v1/identity/auth/me | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/menu/catalog | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/tables/map | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/tables/sessions/{id} | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/orders/?sessionId={id} | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/orders/{id} | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/payments/bills?sessionId={id} | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/payments/bills?orderId={id} | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/payments/bills/{id} | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/payments/sessions/{id}/settlement-status | 401 PASS | 401 PASS | 401 PASS |
| /api/v1/kitchen/queue | 401 PASS | 401 PASS | 401 PASS |

AI's invalid-bearer _info rejection here is Gateway middleware behavior, not a
test of AI's protected POST dependencies. Inventory/Workforce/Reporting/Customer
likewise have no protected business route to test. No false business/auth PASS
is inferred from their public metadata.

### Consolidated component result

| Component | Runtime / dependency result | Business limit |
|---|---|---|
| Identity | PASS, live/ready 200 | Valid JWT/profile/auth flows NOT TESTED |
| Menu | PASS, live/ready 200 | Authorized catalog NOT TESTED |
| Table | PASS, live/ready 200 | Authorized map/session and writes NOT TESTED |
| Ordering | PASS, live/ready 200 | Authorized orders and writes NOT TESTED |
| Payments | PASS, live/ready 200; mutations false | Authorized bill/settlement and payments NOT TESTED |
| Kitchen | PASS, live/ready 200 | Authorized queue/status workflows NOT TESTED |
| Inventory | PASS_RUNTIME_SKELETON, live/ready 200 | Business N/A |
| Workforce | PASS_RUNTIME_SKELETON, live/ready 200 | Business N/A |
| Reporting | PASS_RUNTIME_SKELETON, live/ready 200 | Business N/A |
| Customer | PASS_RUNTIME_SKELETON, live/ready 200 | Business N/A |
| AI | PASS_RUNTIME_PLACEHOLDER, live/ready 200 | Ready TCP-only; native ai_svc auth/ACL and Redis PING passed in AI step; models/stub POST/auth NOT TESTED |
| Gateway | PASS_RUNTIME_ROUTING, root/live 200 | No aggregate ready/docs; authorized/non-GET business routing NOT TESTED |
| Redis | PASS healthy; original ID/image/StartedAt preserved | No write/pubsub/consumer workflow exercised |

### Database, containers and evidence

All direct SQL used BEGIN READ ONLY and default_transaction_read_only=on.
Native inventory was checked under existing identity_svc, not an admin:
aicos_db|identity_svc|public|on|38|244 before and after.
No Gateway database role/client was invented.

The 25 unique approved tables matched the last AI PASS baseline before Gateway
startup and remained identical afterward. Snapshot SELECT privileges were
checked before reading each owner's tables. Kitchen overlaps Ordering:
28 role/table observations, 25 unique tables. No inaccessible table was read
and no grant/ACL change was made. This does not fingerprint the remaining
13 public tables or prove every column/constraint for every future feature.

Non-secret before/after evidence confirms all ten .NET services and AI keep
their IDs/images/StartedAt and runtime environments; all remain running.
Redis ID/image/StartedAt stays unchanged and health is healthy. Only new
container in the final set is aicos-gateway. No unrelated containers touched.

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\gateway_runtime_20261029_101337.
gateway-result.json includes route/auth results, 50 recorded Gateway GET
responses (including expected 404s/401s), database digests and container
baseline/after identifiers. Private env comparisons contain no exported
secrets. Logs were captured privately, reported as flags only.

Checks actually executed: parser and private Compose/config/JWT/address guards,
packaged route-config checksum, real Gateway DNS/TCP, direct/proxied GETs,
authentication rejection, log error checks, PostgreSQL READ ONLY metadata/
privilege/fingerprints and container preservation. No new unit suite or
mutation integration suite was run or claimed; no code/config fix was needed.

### Remaining untested work and stop decision

NOT TESTED under this approval: legitimate JWT/role/branch/tenant authorized
responses; login/refresh/provisioning; table open/close and settlement-gated
closure; order create/change/cancel; Kitchen progress/events; bill creation,
cash/QR confirmation, audit/idempotency/concurrency/print/close workflows;
AI POST authentication and inference; non-GET proxy behavior, CORS browser
flows, rate-limit/load/failover tests and real business service-to-service calls.
Historical tests on aicos_payment_test remain historical, not Gateway run
results. Four placeholder .NET modules still need separately scoped feature/
schema/ACL review before business implementation.

**Gate C infrastructure/routing smoke scope: PASS. Overall Gate C: PARTIAL.**
There is no observed runtime/routing failure in the exercised scope, but the
missing authorized business evidence and placeholder implementations prevent
a full functional PASS. No schema/data mutation, migration/seed,
POST/PUT/PATCH/DELETE, credential/JWT/ACL change, previous container restart/
recreate, Mobile edit, commit/push or next-phase action was performed.

**STOP after Gateway; wait for owner approval before the next stage.**

## 2026-10-29 — AI PASS_RUNTIME_PLACEHOLDER — STOP before Gateway

Only AI started in this step. All ten existing .NET services returned GET
live/ready 200 before startup and retained IDs/images/StartedAt and runtime
environments. Redis retained its ID/image/StartedAt and remained healthy.
Gateway was not started. Full Gate C remains incomplete.

| Check | Actual result |
|---|---|
| AI process/container | PASS_RUNTIME_PLACEHOLDER |
| Framework / listener | FastAPI + Uvicorn, Python 3.11.17; 0.0.0.0:8000 in container |
| Host exposure | 127.0.0.1:5111 only |
| Source/image startup safety | Nine packaged Python files match reviewed source hashes |
| Startup migration/seed/model work | None in reviewed source/image path; no write/model download/training task |
| AI migration flag | N/A: Python has no MigrateOnStartup/EF startup path |
| Dependencies | Configured PostgreSQL and Redis; external override depends_on existing Redis healthy only |
| PostgreSQL from AI readiness | PASS TCP only; does not authenticate/query PostgreSQL |
| Native ai_svc authentication | PASS, real READ ONLY psql to aicos_db.public |
| Redis | PASS TCP plus protocol PING/PONG from AI container |
| ACL | CONNECT/public USAGE true; zero table/column SELECT, as approved |
| Safe GET health/metadata/docs | All 200 |
| Protected business authentication | NOT TESTED: protected routes are POST; no POST/JWT/login allowed |
| Business GET | N/A: no implemented AI business GET |
| Model/LLM/forecast/face features | NOT IMPLEMENTED; NOT TESTED |
| AI-owned dataset fingerprint | N/A: no SQL driver/table mapping implemented |
| Prior 25 unique table fingerprints | Match Customer baseline; unchanged before/after |
| Main database metadata | 38 public tables / 244 columns before/after |
| Logs | No checked traceback/ERROR/auth/permission/timeout/migration-start pattern |

### Source, Dockerfile and actual dependencies

Read all nine app Python files, Dockerfile, requirements, config, auth/routers,
existing tests and Compose layers before starting AI. Source confirms FastAPI
with Uvicorn CMD app.main:app --host 0.0.0.0 --port 8000. The image runs as
non-root user aicos. Its actual CMD/user/Python version were inspected; sha256
of each packaged app file matched the corresponding reviewed repository file:

- app/main.py, config.py, security.py and app/__init__.py;
- app/routers/chat.py, attendance.py, forecast.py, summary.py and __init__.py.

No app startup/lifespan hook runs migrations, seed, SQL writes, model loading,
downloads, training or background business processing. Startup creates settings,
middleware and routes. No application/driver/provider code was changed.

Runtime requirements are fastapi, uvicorn[standard], pydantic-settings and
PyJWT. PostgreSQL/pgvector/Redis driver, LLM client, face recognition and
forecasting packages are commented future work. Config supplies DATABASE_URL,
REDIS_URL and the existing JWT key. Groq/DeepSeek keys are optional settings;
no external provider was invoked or validated.

Health /health/live returns process status. /health/ready uses asyncio TCP
connections only to PostgreSQL and Redis. A 200 readiness response therefore
does NOT establish PostgreSQL authentication, SQL queries, Redis command
permissions or AI functionality. AI currently has no database model/repository
or authenticated PostgreSQL connection implementation. Schema public in
metadata is a declaration, not an exercised ORM mapping.

### DNS fix and controlled startup

An initial Python DNS diagnostic failed due command quoting (NameError); this
was a diagnostic-command error, not evidence of Docker DNS. Repeated with
getent using the same aicos/ai image and aicos-be_default network: unmodified
host.docker.internal resolution exited 2. Dynamic
--add-host host.docker.internal:host-gateway resolved it (exit 0), and TCP
5432 succeeded (exit 0). Diagnostic containers had no application credentials
and did not run app entrypoint/model tasks or send SQL.

Only after this verification, added AI to docker-compose.gate-c.yml:

```yaml
  ai:
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports: !override ["127.0.0.1:5111:8000"]
```

This fixes the observed DNS failure and keeps Gate C exposure on loopback.
No fixed host IP, global DNS, Firewall or PostgreSQL security change. Previous
ten service/Redis configuration was preserved. Existing AI image retained:
sha256:10abb62f9118d37ce6e87d52fbde4d9d72045ce5059f991696da84f6c8e9e3c4.

Reused DPAPI-encrypted credentials and the original Identity JWT key in process
memory. AI DATABASE_URL uses ai_svc's own URI-encoded password, verified
privately against effective config and running container. No credentials/JWT
reset, tracked secret file or plaintext output. Environment restored after
completion. Previous ten .NET migration flags stay false; Payment mutations
stay false. Python AI has no equivalent migration switch.

After quiet merged-config/connection/JWT/dependency guards, ran ONLY:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never ai
```

Commands used the reviewed private secret context; .env.example alone does not
contain usable credentials. No service/image rebuild, package installation,
large model download or whole-stack startup was requested.

### Actual GET, authentication and dependency evidence

| Actual safe GET | Result |
|---|---|
| /health/live | 200 |
| /health/ready | 200, postgres=true and redis=true (TCP only) |
| /api/v1/ai/_info | 200, service ai/schema public |
| /api/v1/ai/_info with opaque invalid bearer | 200; metadata is anonymous, not a JWT enforcement check |
| /docs | 200, Swagger UI HTML |
| /redoc | 200, ReDoc HTML |
| /openapi.json | 200 |

OpenAPI confirms exactly four business POST stubs: /api/v1/ai/chat,
/attendance/verify, /forecast/demand and /summary. Source handlers raise 501
after their auth dependencies; no stub was invoked. Chat allows anonymous
callers; the others have role requirements. No protected business GET exists.

Missing/invalid token behavior on these POST routes is NOT TESTED because POST
is explicitly forbidden. GET against a POST-only path would only test method
routing, so it was not used to claim auth PASS. No JWT was minted or login
performed. Existing pytest includes POST requests/token generation and was
NOT RUN under this approval; no unit-suite PASS or simulated integration
result is claimed. Documentation HTML was fetched without a browser/model
task; external CDN rendering was not tested.

Real native psql as ai_svc used BEGIN READ ONLY and
default_transaction_read_only=on. Before/after signature:
aicos_db|ai_svc|public|on|38|244.
CONNECT and public USAGE are true; all 38 table SELECT checks are false and
zero columns are SELECT-accessible, matching approved placeholder ACL.
No GRANT/REVOKE or denied table query was attempted. This native role test is
separate from the application's TCP-only readiness.

A bounded Python socket probe inside AI sent Redis PING and received PONG;
it did not set keys, publish messages or consume events. Readiness TCP and
Redis PING are dependency checks only. No LLM/provider/model task was executed.

### Data/container preservation and evidence

25 prior unique tables were read only under their approved owner roles after
SELECT checks. Their digests match the Customer PASS baseline and stay
identical after AI startup. Kitchen's three views overlap Ordering: 28
role/table observations, 25 unique tables. No AI-owned fingerprint was
invented from the absent dataset; this is not a full 38-table data audit.

The last 200 stdout/stderr log lines were inspected privately for traceback,
ERROR, authentication/permission failure, timeout and migration-start patterns;
all checked flags were false. Container remains running and app ready.

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\ai_runtime_20261029_100709.
ai-result.json records dependency/auth/business distinctions plus matching
before/after non-secret IDs/images/StartedAt for all ten services and Redis.
Privilege metadata and before/after count/digest files contain no row values
or credentials. Runtime environments were compared privately in memory.

Checks actually run: source/image audit, parser/Compose/target/config guards,
DNS/TCP, native PostgreSQL READ ONLY/ACL/fingerprints, Redis PING, safe HTTP
GET/OpenAPI and logs. No POST/PUT/PATCH/DELETE, migration/seed/data/ACL change,
credential/JWT reset, Mobile edit, prior service/Redis restart/recreate,
Gateway startup, model training/download, commit/push or Phase 6.

**STOP after AI runtime/dependency PASS. AI functionality and protected POST
authentication remain unimplemented/not tested as above. Gateway needs its
separate approval; Gate C is not complete.**


## 2026-10-29 — Customer PASS_RUNTIME_SKELETON — STOP after Customer

Customer-only runtime validation completed. All nine previously passed .NET
services returned GET live/ready 200 before this step. No previous service or
Redis was restarted/recreated; their current container state was the baseline.
Only Customer was started. AI and Gateway remain unstarted by this operation.

| Required check | Actual result |
|---|---|
| Customer runtime infrastructure | PASS_RUNTIME_SKELETON |
| PostgreSQL native connection and runtime readiness | PASS_CONNECT_ONLY: customer_svc to aicos_db.public |
| Database metadata before/after | 38 public tables / 244 columns |
| MigrateOnStartup | false in effective Compose and Customer runtime |
| Automatic seed | No seed/EnsureCreated path in reviewed startup code |
| Dependencies | Existing healthy Redis only; no active business service client/handler |
| DNS/TCP | PASS after verified dynamic host-gateway fix; TCP 5432 exit 0 |
| CONNECT / public USAGE | PASS, both true under customer_svc |
| SELECT matrix | PASS: zero public table SELECT, zero column SELECT |
| Health live / ready | GET 200 / 200 |
| Metadata / Swagger UI / OpenAPI | GET 200 / 200 / 200 |
| Business GET / missing-invalid JWT 401 | N/A: no protected Customer business endpoint exists |
| Customer-owned table fingerprint | N/A: no implemented Entity/DbSet/table mapping |
| Prior 25 unique table fingerprints | Match Reporting baseline; unchanged before/after |
| Nine previous services | IDs/images/StartedAt unchanged; runtime environments also matched |
| Redis | ID/image/StartedAt unchanged, still healthy |
| Checked critical log flags | All false |

### Source audit and capability limits

Reviewed CustomerEngagement Program, empty CustomerEngagementDbContext,
CustomerEngagementService, appsettings, launch profile, shared persistence/
startup, Compose configuration and the approved ACL matrix. Customer service
name is customer; API route prefix is customers; schema is public.
Feature endpoint mapping is commented out. No implemented customer, loyalty
points or engagement business API/Entity/DbSet was found, and no active typed
service client/event handler was registered.

Appsettings/launch/base Compose keep MigrateOnStartup=false. Shared migration
startup returns immediately when the flag is false; effective Compose and
runtime were checked before/after startup. No seed/EnsureCreated call was
found on the reviewed startup path. No seed or migration command was executed.

Runtime OpenAPI confirmed no Customer business path beyond anonymous
/api/v1/customers/_info. Therefore no protected business GET exists to test
401 with missing/invalid bearer. Authentication and business/owned-table
checks are N/A, not business PASS or a demonstrated auth failure. No login,
test account or JWT generation was attempted. Readiness validates real
PostgreSQL/Redis connectivity, not customer/loyalty functionality.

### DNS diagnosis and scoped configuration change

Diagnostic containers using existing aicos/customer on the SAME
aicos-be_default network, with application entrypoint overridden and without
credentials, could not resolve host.docker.internal (exit 2). Repeating with
--add-host host.docker.internal:host-gateway resolved the name (exit 0) and
connected TCP 5432 (exit 0), without sending SQL.

Only after that verification, added this Customer section to
docker-compose.gate-c.yml:

```yaml
  customer:
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports: !override ["127.0.0.1:5110:8080"]
```

The mapping fixes the observed Docker DNS failure using Docker's dynamic
host gateway. Loopback binding follows the existing controlled Gate C exposure.
No fixed IP, global DNS, Firewall, PostgreSQL security or previous service
configuration was changed. Existing image retained:
sha256:b495b8a2cc585b1bd0a113c8af30282467e3bdd97cb510914c6f9a38cdda7256.

Reused DPAPI-encrypted credentials and the existing Identity shared JWT key
only in process memory. No credentials/JWT reset or plaintext secret output/
tracked secret file. Environment was restored after completion.

Quiet Compose validation and private guards verified the Customer connection
to aicos_db as customer_svc, shared JWT consistency, migration=false, Redis-only
dependencies and unchanged effective environments/mappings for nine previous
services. Startup was limited to:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never customer
```

These commands ran with the reviewed secret context; .env.example by itself
does not contain usable runtime credentials. No build/restart of another
service was requested. Existing Payment mutation flag stays false.

### Actual READ ONLY and HTTP verification

All direct SQL used BEGIN READ ONLY and default_transaction_read_only=on.
Before/after native signature:
aicos_db|customer_svc|public|on|38|244.
has_database_privilege CONNECT and has_schema_privilege public USAGE returned
true. has_table_privilege SELECT=false for all 38 public tables, and
has_column_privilege found zero SELECT-accessible columns. This matches the
approved placeholder ACL. No denied table was read or privilege added.

Previous 25 tables were fingerprinted only with existing approved owner roles,
after checking SELECT. Digests match the last Reporting PASS baseline and
remain identical after Customer startup. Kitchen overlaps three Ordering
tables, so evidence has 28 role/table observations for 25 unique tables.
Customer has no mapped dataset to fingerprint; no fake empty fingerprint or
privileged bypass was used. This is the approved 25-table data coverage, not
a new full 38-table data audit.

| Actual GET | HTTP |
|---|---|
| /health/live | 200 |
| /health/ready | 200, real PostgreSQL/Redis checks |
| /api/v1/customers/_info | 200, service customer/schema public |
| /swagger/index.html | 200 |
| /swagger/v1/swagger.json | 200, no Customer business routes |

Privately inspected the last 200 stdout/stderr log lines. No checked unhandled
exception, PostgreSQL authentication/permission error, timeout or migration-
start pattern was found. No raw credentials or error dump was published.

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\customer_runtime_20261029_100106.
customer-result.json stores explicit non-secret ContainerBaseline/ContainerAfter
IDs/images/StartedAt for all nine services, matching comparison flags, and
RedisBaseline/RedisAfter including healthy status. Runtime environments were
compared in memory without exporting secrets. Separate privilege metadata and
before/after count/digest files contain no row values or credentials.

Checks actually run: parser/Compose/role/config/target guards, DNS/TCP, real
PostgreSQL READ ONLY connectivity/ACL/fingerprints, live HTTP GET/OpenAPI/UI
and log checks. No unit suite or mutation integration suite was run or claimed;
no .NET business code changed.

**STOP after Customer. 10/10 .NET services now have runtime PASS; Inventory,
Workforce, Reporting and Customer are skeleton-level only. Gate C remains
incomplete: AI and Gateway need separately approved validation.**
No main schema/data write, migration, seed, POST/PUT/PATCH/DELETE, credential/
JWT change, Mobile edit, previous service/Redis restart/recreate, AI/Gateway
startup, commit/push or Phase 6 was performed.


## 2026-10-29 — Reporting PASS_RUNTIME_SKELETON — STOP after Reporting

Reporting-only runtime checks completed. The eight previous .NET services were
already running; each returned live/ready 200 before this step. Their existing
container IDs, images and StartedAt values were preserved. Redis remained
healthy with unchanged ID/StartedAt. No Gateway or further service was started.

| Required check | Actual result |
|---|---|
| Reporting runtime infrastructure | PASS_RUNTIME_SKELETON |
| PostgreSQL connectivity | PASS_CONNECT_ONLY: reporting_svc to aicos_db.public |
| Database metadata before/after | 38 public tables / 244 columns |
| MigrateOnStartup | false in effective Compose and running Reporting |
| Automatic seed | No seed/EnsureCreated path found in reviewed startup code |
| Dependencies | Existing healthy Redis only; no active business service client/handler |
| DNS/TCP | PASS after verified dynamic host-gateway mapping; TCP 5432 exit 0 |
| Approved SELECT matrix | PASS: zero public table SELECT and zero column SELECT |
| Health live / ready | GET 200 / 200 |
| Metadata / Swagger UI / OpenAPI | GET 200 / 200 / 200 |
| Protected business GET / 401 checks | N/A: no Reporting business endpoint exists |
| Reporting-owned table fingerprint | N/A: no implemented Entity/DbSet/table mapping |
| Previous 25 unique table fingerprints | Match Workforce baseline; unchanged before/after |
| Eight previous .NET containers | IDs/images/StartedAt unchanged |
| Critical log flags | No checked unhandled/auth/permission/timeout/migration-start pattern |

### Source, database and authentication boundary

Reviewed ReportingAnalytics Program, empty ReportingAnalyticsDbContext, service
metadata, appsettings, launch profile, shared startup/persistence, Compose
overrides and the approved public-service-grants.sql matrix. Metadata maps
reporting/public. Feature endpoint mapping, service clients and event handlers
remain commented out. No implemented attendance/revenue/analytics/report data
endpoint or Reporting dataset was found. Reporting currently provides shared
infrastructure and metadata; this result does not establish business reporting.

Shared MigrateDatabaseAsync exits when MigrateOnStartup=false. Appsettings,
launch profile, base Compose and runtime configuration keep that flag false.
No automatic seed/EnsureCreated call was found on the reviewed startup path.
No migration or seed tool was invoked.

Runtime OpenAPI confirmed no Reporting business route beyond anonymous
/api/v1/reporting/_info. Shared metadata allows anonymous requests, so 200 is
expected there. No protected business endpoint exists for missing/invalid JWT
401 checks. These checks are N/A, not PASS or an auth failure. No JWT was
generated, no login attempted, and no test account was created.

### Verified DNS fix and minimal configuration change

Diagnostic containers used the existing aicos/reporting image on the SAME
aicos-be_default network, with application entrypoint overridden and no
credentials. Resolution of host.docker.internal failed without mapping
(exit 2). With --add-host host.docker.internal:host-gateway, DNS and TCP 5432
both passed (exit 0). Only then was this Reporting block added to
docker-compose.gate-c.yml:

```yaml
  reporting:
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports: !override ["127.0.0.1:5109:8080"]
```

No fixed IP, global Docker DNS, Firewall or PostgreSQL security change was
made. The previous eight service mappings/configuration were preserved.
Existing image retained:
sha256:8a31e481438c82a62329a718ed93af066dae4fbc07885a06eaf85b2337f5f349.

Reused existing DPAPI-encrypted role credentials and the original shared JWT
key in process memory. No plaintext secret output, tracked secret file,
credential reset or JWT change. Environment was restored after the operation.
Private merged-config checks verified reporting_svc/aicos_db, JWT consistency,
migration=false and Redis-only dependencies before this exact scoped startup:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never reporting
```

These commands ran in the reviewed secret context; .env.example alone does
not provide usable runtime credentials.

### Actual READ ONLY and GET evidence

Native psql used BEGIN READ ONLY and default_transaction_read_only=on.
Before/after signature: aicos_db|reporting_svc|public|on|38|244.
Live has_table_privilege reports SELECT=false on all 38 public tables;
has_column_privilege reports zero SELECT-accessible columns, matching the
approved placeholder ACL. No denied table was queried and no GRANT/REVOKE ran.

The previous 25 tables were read only using their existing approved owner
roles after SELECT checks. Fingerprints match the last Workforce PASS baseline
and remain identical after startup. Kitchen's three table views overlap
Ordering: 28 role/table observations, 25 unique tables. No Reporting-owned
fingerprint was invented from the empty model and no inaccessible table was
read. This is the approved 25-table data check, not a full 38-table data audit.

Actual HTTP GET results:

| Path | Result |
|---|---|
| /health/live | 200 |
| /health/ready | 200, actual PostgreSQL/Redis readiness |
| /api/v1/reporting/_info | 200, reporting/public |
| /swagger/index.html | 200 |
| /swagger/v1/swagger.json | 200, no Reporting business routes |

The last 200 stdout/stderr log lines were inspected privately for unhandled
exceptions, PostgreSQL authentication/permission failures, timeouts and
migration-start messages; all checked flags were false. No secret/error dump
was published.

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\reporting_runtime_20261029_095701.
reporting-result.json records runtime/business distinctions; privilege metadata
and before/after count/digest files contain no credentials or row values.

Checks actually run: script parser, Compose/config/target guards, DNS/TCP,
real PostgreSQL READ ONLY connectivity/privileges/fingerprints, GET health/
metadata/Swagger/OpenAPI and log inspection. No unit suite or mutation
integration suite was run or claimed; no .NET business code changed.

**STOP after Reporting. 9/10 .NET services now have runtime PASS, including
Inventory, Workforce and Reporting at skeleton level only. Customer, AI and
Gateway remain pending their separate authorized steps. Gate C is incomplete.**
No schema/data mutation, migration, seed, POST/PUT/PATCH/DELETE, credential/JWT
change, previous service restart/recreate, Mobile edit, commit/push or Phase 6.


## 2026-10-29 — Workforce PASS_RUNTIME_SKELETON — STOP after Workforce

The owner enabled Docker Desktop again and approved continuing the pending
Workforce step. Engine 29.7.2 responded. All seven previously passed services
were already running and returned 200 for BOTH live and ready before this step.
Existing Redis was healthy. Their current owner-restored container state was
captured as the baseline; no prior service was restarted or recreated.

| Check | Actual result |
|---|---|
| Workforce infrastructure runtime | PASS_RUNTIME_SKELETON |
| Native PostgreSQL / runtime readiness | PASS_CONNECT_ONLY: workforce_svc, aicos_db.public |
| Main database inventory before/after | 38 public tables / 244 columns |
| MigrateOnStartup | false in effective Compose and running Workforce |
| Previous services' migration flags | false; Payment mutations remain false |
| DNS/TCP after dynamic host mapping | PASS, TCP 5432 exit 0 |
| SELECT permission matrix | PASS: zero public table SELECT, zero column SELECT |
| Health live / ready | GET 200 / 200 |
| Metadata / OpenAPI | GET 200 / 200 |
| Business GET / protected API authentication | N/A: no Workforce business endpoints implemented |
| Workforce-owned table fingerprints | N/A: no implemented entities/table mappings |
| Prior 25 unique table fingerprints | Match last Inventory baseline; unchanged before/after |
| Seven protected containers | IDs/images/StartedAt unchanged |

### Source and capability boundary

Audited WorkforceScheduling Program, DbContext, service metadata, appsettings,
launch profile, Compose base/external/Gate C overrides and approved service
grant matrix. WorkforceSchedulingDbContext has no implemented DbSet/entity;
feature endpoint mapping remains commented out. No active typed service client
or integration-event handler is registered. This service currently provides
shared infrastructure, metadata and health, not attendance or scheduling APIs.

OpenAPI discovery found no Workforce business path beyond anonymous
/api/v1/workforce/_info. No protected endpoint exists on which to exercise a
401 assertion. Authentication/business/owned-table checks are N/A, not a
business PASS. No JWT was minted, login attempted or account created. Health
readiness checks actual PostgreSQL and Redis connectivity but does not prove
attendance/scheduling functionality.

### DNS, configuration and controlled startup

A diagnostic container using the existing aicos/workforce image on the SAME
aicos-be_default network could not resolve host.docker.internal (exit 2).
With --add-host host.docker.internal:host-gateway, resolution and TCP 5432
both succeeded (exit 0). Diagnostics had no application credentials and did
not send SQL. No fixed IP, Docker DNS, Firewall or PostgreSQL security change.

Added ONLY Workforce to docker-compose.gate-c.yml:

```yaml
  workforce:
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports: !override ["127.0.0.1:5108:8080"]
```

Quiet Compose validation and private role/JWT/connection/dependency guards
passed. Previous seven services' effective environments and host mappings
matched their running containers. Workforce depends on existing healthy Redis,
uses aicos_db with workforce_svc, and keeps Database__MigrateOnStartup=false.

Reused the existing encrypted DPAPI credentials and original Identity JWT key
in process memory. No credential/JWT reset or plaintext secret output/file.
Process environment was restored after completion. No grant script was run.

Only this startup command was executed in the reviewed secret context:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never workforce
```

The example env file alone does not contain usable runtime credentials.
Existing Workforce image was preserved:
sha256:907f613bec533fcad0a44d144d27d92de1b7fa62c958743f755285b91b4252f0.
Actual exposure is 127.0.0.1:5108. No Gateway/further Microservice started.

### Actual verification and data preservation

Native psql used BEGIN READ ONLY and default_transaction_read_only=on.
Signature before and after: aicos_db|workforce_svc|public|on|38|244.
Live metadata checks show SELECT=false on all 38 public tables and zero
SELECT-accessible columns, matching the approved placeholder permissions.
No denied table was queried and no privileges were added.

Previous 25 unique tables were fingerprinted only with their approved owner
roles, after validating SELECT. Kitchen's three tables overlap Ordering;
there are 28 role/table observations, not 28 unique tables. These fingerprints
match the last successful Inventory baseline and remain identical after
Workforce startup. No Workforce table fingerprint is invented from an empty
model. This is not a full 38-table data audit.

GET /health/live, /health/ready, /api/v1/workforce/_info and
/swagger/v1/swagger.json all returned 200. Metadata confirms workforce/public.
The last 200 log lines from stdout/stderr had no checked unhandled exception,
PostgreSQL authentication/permission error, connection timeout or migration
start pattern.

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\workforce_runtime_20261029_095319.
workforce-result.json records the result distinctions; permission metadata and
before/after count/digest files contain no credentials or row values.

Checks actually run: script parser, Compose/config/target guards, real DNS/TCP,
READ ONLY PostgreSQL connectivity/privileges/fingerprints, live HTTP GETs,
OpenAPI discovery and log flags. No unit test suite or mutation integration
test is claimed. No POST/PUT/PATCH/DELETE, migration, seed, database change,
GRANT/REVOKE, credentials reset, Mobile edit, commit/push or Phase 6.

**STOP after Workforce. Full Gate C remains pending. Reporting is the next
service to review only after owner approval; Gateway remains unstarted.**


## Current result: Inventory PASS_RUNTIME_SKELETON — STOP after Inventory

2026-10-27 22:48 +07. Only Inventory started in this step. Identity, Menu,
Table, Ordering, Payment, Kitchen and existing Redis were not restarted or
recreated. No Gateway or other Microservice started. Full Gate C is pending.

| Required check | Actual result |
|---|---|
| Inventory runtime infrastructure | PASS (skeleton only) |
| PostgreSQL connectivity | PASS_CONNECT_ONLY: inventory_svc to aicos_db.public |
| MigrateOnStartup | false in config and runtime |
| SELECT matrix | PASS: zero public table SELECT and zero column SELECT |
| Business GET/authentication checks | N/A: no Inventory business endpoints implemented |
| Inventory-owned table fingerprints | N/A: no Inventory entities/table mappings implemented |
| Previously checked fingerprints | 25 unique tables unchanged, matching prior Kitchen baseline |
| Six previous PASS containers | IDs/images/start times/env/host mappings preserved during this step |

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\inventory_runtime_20261027_224635.
inventory-result.json distinguishes infrastructure PASS from business N/A.
inventory-select-matrix.txt holds permission metadata. Owner-role select-check
and before/after count/digest files contain no credentials or row values.

### Implementation audit and actual capability boundary

InventoryPurchasing Program registers shared PostgreSQL, Redis, authentication,
Swagger and health services, then maps the shared API group. Feature endpoint
mapping is COMMENTED OUT. InventoryPurchasingDbContext has no Entity/DbSet
implementation. There is no stock/ingredient list, movement, purchase, supplier,
recipe, receipt or stock-adjustment API to exercise in this repository version.
No active typed service client or integration-event handler was registered.

This agrees with DATABASE_AUDIT_PHASE3.md and the approved service-grant matrix:
Inventory is a placeholder until separately reviewed database-backed features
exist. Its health/metadata responding is NOT proof of stock management working.
No fake endpoint, mock business success, entity, table, seed or grant was added.

Runtime OpenAPI discovery found NO Inventory business path beyond the shared
anonymous /api/v1/inventory/_info endpoint. The metadata endpoint correctly
returns 200 without JWT. There is no protected business endpoint on which a
401 expectation is applicable. Authentication is therefore N/A, not PASS and
not a demonstrated auth failure. Swagger's shared bearer definition alone
does not establish runtime authorization on an anonymous endpoint.

### Dependencies, DNS and minimal configuration change

Existing Redis at redis:6379 was healthy; effective Compose depends_on is Redis
healthy only. PostgreSQL remains the Windows aicos_db.public database. There
is no active inter-service business call to check in Inventory at this stage.

Diagnostic aicos/inventory containers on the SAME aicos-be_default network,
with application entrypoint overridden and no credentials, confirmed:

| Probe | Original config | Dynamic host-gateway mapping |
|---|---|---|
| host.docker.internal resolution | Exit 2 | Exit 0 |
| TCP host.docker.internal:5432 | Not run; name unresolved | Exit 0 |

Added only this Inventory section to docker-compose.gate-c.yml:

```yaml
services:
  inventory:
    extra_hosts:
      - "host.docker.internal:host-gateway"
    ports: !override ["127.0.0.1:5106:8080"]
```

No fixed PostgreSQL IP was used. The observed fdc4:f303:9324::254 is a Docker-
selected diagnostic address, not source configuration. Existing six service
sections, global Docker DNS, Firewall and PostgreSQL security were unchanged.

### Actual role/privilege verification and startup

- Native READ ONLY connection verified inventory_svc, aicos_db, public,
  transaction_read_only=on. Before/after metadata inventories stay 38 public
  tables / 244 columns. This is an actual connection, not an in-memory result.
- Live has_table_privilege over all 38 public tables reports SELECT=false for
  every table, exactly matching the approved empty Inventory SELECT matrix.
  A separate has_column_privilege check reports zero SELECT-accessible columns.
- These denials are expected for the current placeholder; no denied row query
  was tried and no GRANT/REVOKE/admin bypass was used. CONNECT/public USAGE and
  dependency health are sufficient for this skeleton, not future business data.
- No Inventory fingerprint is invented from an empty model. The previous 25
  tables were read only with the existing approved roles that already have
  SELECT; all planned per-role SELECT probes passed before data reads.
- Reused encrypted DPAPI credentials and the original shared JWT key in memory;
  no reset, login, provisioning, token generation or plaintext secret output.
  Process environment was restored after completion.
- Quiet Compose validation, role/JWT/connection guards and migration=false
  passed before starting ONLY inventory with --no-deps --no-build --pull never.
  Existing six service environments/host mappings were compared privately and
  remained unchanged. Runtime connection matches inventory_svc; migration=false.
- Existing image was used, unchanged:
  sha256:9f9849cff80cd32b1e08d1aad2752e2abebafed3e4056da3ccffe65cee546ff7.
  Actual host binding is 127.0.0.1:5106 only.

Commands below were executed WITH the reviewed secret context loaded; the
example env file alone does not contain usable runtime credentials:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never inventory
docker exec aicos-inventory printenv Database__MigrateOnStartup
docker exec aicos-inventory getent hosts host.docker.internal
docker exec aicos-inventory timeout 5 bash -c '</dev/tcp/host.docker.internal/5432'
```

### Actual GET/log checks and data preservation

| GET/check | Result |
|---|---|
| /health/live | 200 |
| /health/ready | 200; actual PostgreSQL connection and Redis checks |
| /api/v1/inventory/_info | 200; service inventory, schema public |
| /swagger/v1/swagger.json | 200; no Inventory business endpoint discovered |
| Logs, stdout/stderr last 200 lines | No flagged unhandled, PG auth/permission, timeout or migration-start error |
| Protected stock/ingredient GET with absent/invalid bearer | N/A, no such endpoint exists |
| Own Inventory table data | N/A, no mapped Inventory dataset and no SELECT grants |

All direct SQL used BEGIN READ ONLY and default_transaction_read_only=on.
The prior 25 unique tables matched the last Kitchen PASS baseline BEFORE
Inventory started and stayed identical AFTER. Kitchen's three tables overlap
Ordering, so there are 28 role/table observations per snapshot, NOT 28 unique
tables. No table data was read as inventory_svc, no unavailable SELECT grant was
added and no Inventory-owned empty/fake fingerprint was reported.

Coverage is the approved 25-table set, not a new full 38-table data audit;
refunds and other ungranted/out-of-scope data were not fingerprinted. No
schema/data mutation, import/export/adjustment, material transaction, migration
or seed was issued. No POST/PUT/PATCH/DELETE request was sent.

Identity/Menu/Table/Ordering/Payment retain their owner-restored 15:12 UTC
StartedAt values from the Kitchen report; Kitchen retains
2026-10-27T15:21:15.99038147Z. All six original IDs/images/start times match the
pre-Inventory baseline. Existing Payment mutation configuration stays false.
Unrelated VietAI containers were untouched.

**NOT IMPLEMENTED / NOT TESTED:** stock/ingredient/business GETs, business JWT
authorization, Inventory entity/schema mapping, and stock/purchase/recipe/
movement workflows. Those need a separately authorized implementation/schema/
permissions phase; they are not runtime-smoke successes or missing grants to
silently fix during Gate C.

Final running AICOS inventory: the six previous services, Inventory and Redis.
No Gateway/further service, credentials/JWT change, .NET/Flutter edit, commit/
push or Phase 6. Repository changes this step are the Inventory Docker override,
this report and CHANGELOG.md. Checks actually run were config/syntax/target/
GET-only guards plus real DNS/TCP, READ ONLY PostgreSQL, privilege metadata,
anonymous GET/OpenAPI/log checks and 25-table fingerprints. No unit/mutation
integration-suite PASS is claimed. Encrypted role cache stays outside Git.

**STOP after Inventory PASS_RUNTIME_SKELETON. Authentication/business/own-table
checks remain N/A as above. Wait for owner approval of the next step.**

## Current result: Kitchen PASS_RUNTIME after owner restored the baseline

2026-10-27 22:23 +07. The owner reported the stack was started again. READ ONLY
checks confirmed all five previously passed services live/ready=200, all five
MigrateOnStartup=false, and Payment mutations=false. The previously authorized
Kitchen-only step then continued; the agent did NOT restore/restart/recreate
those five services or Redis. Their USER-RESTORED state was the new baseline.

**Kitchen runtime/database/authentication and SELECT-matrix checks PASS.
STOP after Kitchen; Gate C is not complete and no Gateway/Phase 6 was started.**

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\kitchen_runtime_20261027_222111.
kitchen-result.json records actual results. The -select-check.txt files and
kitchen-select-matrix.txt contain privilege metadata; each group's -before.txt
and -after.txt contains only row counts/digests, no row values or credentials.

| Required check | Actual result |
|---|---|
| Kitchen runtime | PASS |
| PostgreSQL | PASS: kitchen_svc, aicos_db.public, READ ONLY |
| Live SELECT matrix | PASS: exactly the three previously approved tables |
| Database__MigrateOnStartup | false in effective config and running Kitchen |
| Queue authentication | PASS: 401 with no bearer and invalid bearer |
| Authenticated queue/business payload | NOT RUN: no legitimate JWT provided |
| Previous 25-table Payment baseline after restoration | Identical before Kitchen startup |
| Fingerprints before/after Kitchen | 25 unique tables unchanged; Kitchen subset additionally read under kitchen_svc |
| Five previously passed containers | IDs, image IDs and restored StartedAt unchanged during this step |

### Dependencies and deployed configuration

Kitchen's active dependencies are PostgreSQL and the existing healthy Redis on
aicos-be_default. Effective Compose depends_on is Redis healthy only. Program
has no active typed service client or integration-event subscription. Reviewed
GetQueueAsync uses AsNoTracking reads and branch-scoped staff authorization.
Its PATCH preparation-status path writes status/events, but was NEVER called.

The earlier paused attempt already verified unresolved host.docker.internal
(getent exit 2), versus DNS/TCP exit 0 with dynamic host-gateway, and prepared
the Kitchen-only mapping. This resumed step DEPLOYED that prepared override:

```yaml
services:
  kitchen:
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

No IP is hardcoded. The address fdc4:f303:9324::254 observed at runtime is
Docker-selected metadata. No further Docker config edit was needed this resume;
five previous mappings/configurations stayed unchanged. No global DNS, Firewall
or PostgreSQL security change occurred.

Used the existing image, unchanged:
sha256:3ea8fe869ccbd17abd637957d2823588614822061c2b8c86a47943701ea8ca83.
Actual host binding is 127.0.0.1:5107 only. Credentials/shared JWT were reused
from the existing private DPAPI/context in process memory, not reset, printed
or stored in a plaintext file. Environment was restored after execution.

### Actual SELECT permissions and architecture exception

The mapped Kitchen entities point directly to existing public tables:

| DbContext entity | Physical table | kitchen_svc SELECT |
|---|---|---|
| PreparationOrder | orders | true |
| PreparationItem | order_items | true |
| PreparationEvent | order_item_events | true |

Live has_table_privilege metadata over all 38 public tables matches the Gate A
matrix exactly: three SELECT grants and 35 tables without table SELECT for
kitchen_svc. Denied examples include order_item_options and refunds; they are
not required by the currently mapped Kitchen GET model. No query of their row
data was attempted as kitchen_svc, and no privilege was added.

For the previously checked 25-table set, each table was first checked using its
existing approved owner/read role (identity_svc, menu_svc,
table_reservation_svc, ordering_svc, payment_svc). Every planned SELECT check
passed. This does not give Kitchen access to those other tables or bypass a
denied Kitchen permission. No admin credential, SET ROLE or GRANT/REVOKE used.

Kitchen's three physical tables ALREADY occur in Ordering's four-table set.
Thus this run has **25 UNIQUE tables**, with 28 role/table observations at each
snapshot (25 existing + three overlapping Kitchen-role reads), NOT 28 unique
tables. Kitchen's three before-digests also exactly match the corresponding
Ordering-role before-digests. No new Kitchen table was created or proposed.
The direct Ordering/Kitchen coupling remains the previously reviewed legacy
architecture exception; this runtime task did not refactor it.

### Startup and actual GET/log results

Before startup: verified quiet Compose validation, scoped role/connection/JWT,
migration=false, preserved five service env/host mappings, native kitchen_svc
READ ONLY signature, SELECT LIMIT 0 on all three mapped tables, all snapshot
SELECT checks and all 25 previous Payment fingerprints. Then started ONLY
kitchen with --no-deps --no-build --pull never.

Commands below were executed WITH the reviewed scoped secret context; the
example env file is not a substitute for the actual encrypted credentials:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never kitchen
docker exec aicos-kitchen printenv Database__MigrateOnStartup
docker exec aicos-kitchen getent hosts host.docker.internal
docker exec aicos-kitchen timeout 5 bash -c '</dev/tcp/host.docker.internal/5432'
```

| Request/check | Result |
|---|---|
| Runtime DNS / TCP host port 5432 | PASS, exit 0 |
| GET /health/live | 200 |
| GET /health/ready | 200, actual PostgreSQL + Redis checks |
| GET /api/v1/kitchen/_info | 200, service kitchen, schema public |
| GET /swagger/v1/swagger.json | 200, queue GET path present |
| GET /api/v1/kitchen/queue, no bearer | 401 |
| GET /api/v1/kitchen/queue, invalid bearer | 401 |
| Logs, both stdout/stderr, last 200 lines | No flagged unhandled, PG auth/permission, timeout or migration-start error |

Invalid bearer is deliberately invalid opaque text, not a signed/artificially
issued JWT. No login, refresh, account provisioning or token generation ran.
Only GET HTTP requests were sent. No PATCH/POST/PUT/DELETE request, including a
negative mutation probe, was attempted.

**NOT RUN:** authenticated queue 200 payload/filtering, branch/role isolation,
and receiving/preparing/completing/status transitions. The first set needs an
existing legitimate JWT and scoped fixtures; mutation tests need a separately
authorized test environment. No ticket/order/item/event was created or changed
to force coverage. Queue auth rejection is not full business-flow validation.

### Data integrity and preserved five services

SQL used BEGIN READ ONLY and default_transaction_read_only=on. Fresh public
inventory before/after stays 38 tables/244 columns. All current 25-table
fingerprints matched the last successful Payment result at
payment_runtime_20261027_214531 BEFORE Kitchen startup, confirming that the
owner-restored runtime did not introduce a difference in that checked set.
All per-role fingerprints also match AFTER Kitchen. Kitchen's three tables
each contain zero rows; none were seeded.

Coverage is the approved 25 unique tables, not a fresh complete 38-table audit.
refunds and other ungranted/out-of-scope data were not fingerprinted or read.
No schema/data/permission change, migration or seed was issued.

The five original container IDs/images remain; their USER-RESTORED start times
were captured before Kitchen and unchanged afterward:

| Service | Current restored StartedAt (UTC), unchanged by this step |
|---|---|
| Identity | 2026-10-27T15:12:02.793318446Z |
| Menu | 2026-10-27T15:12:00.888599492Z |
| Table | 2026-10-27T15:12:01.243452198Z |
| Ordering | 2026-10-27T15:12:02.301548643Z |
| Payment | 2026-10-27T15:12:00.493516575Z |

These are newer than historical pre-stop timestamps because the owner restored
the stack, not because this Kitchen operation restarted a passed service.
Existing Payment mutations remain disabled; no existing service configuration
or credentials/JWT were reset. Unrelated VietAI containers were not modified.

Final running AICOS inventory: Identity, Menu, Table, Ordering, Payment, Kitchen,
Redis. Gateway and all remaining services were not started. This resume changed
only report Markdown/changelog in the repository; no .NET/Flutter source edit,
commit/push or Phase 6 occurred. Actual DNS/TCP, SQL READ ONLY, permission and
fingerprint/GET/log checks ran; no unit or mutation integration-suite PASS is
claimed. Existing encrypted cache remains private outside Git.

**STOP after Kitchen PASS_RUNTIME. Await owner approval of the next step.**

## Latest: READ ONLY stop investigation complete; runtime remains PAUSED

2026-10-27. See [Docker stack stop investigation](DOCKER_STACK_STOP_INVESTIGATION_20261027.md)
for timestamped evidence, resources, attribution limits and safe recovery plan.

- Retained API proxy logs record SIX incoming container stop requests at
  21:55:01 with client string compose/v5.5.1. All six exit 0, no OOM, and normal
  application/SIGTERM shutdown. This confirms the immediate stop mechanism.
- Exact person, CLI stop versus UI action, or interrupted down is unproven.
  No completed down is evidenced: six original containers/network remain;
  inspected API v1.55 window has zero container/network DELETE requests.
- At 21:55:07 Docker starts a five-minute idle timer; the engine idle cycle
  occurs around 22:00, AFTER the original stops. No Windows reboot or matching
  resource-exhaustion/OOM evidence was found for the incident window.
- Host RAM currently has limited free headroom, but current measurements are
  not proof of historical exhaustion. No configuration change is justified by
  that data alone.
- Six AICOS containers remain stopped. Unrelated VietAI containers were seen
  running after the engine wake under always policies; no explicit container
  start/stop/restart/delete command was issued by this investigation.
- No SQL, credential load/reset, config/Mobile change, commit/push or runtime
  smoke occurred. Only report Markdown changed. Kitchen remains NOT STARTED.

**Keep Gate C PAUSED. Recovery/startup needs separate owner approval; this
audit does not authorize restarting the five passed services or resuming Kitchen.**

## Current checkpoint: Kitchen step PAUSED BY OWNER before startup

2026-10-27. The owner explicitly selected: pause while they inspect the stack.
No further startup, database query, permission audit or runtime smoke will run
until the owner resumes. No Gateway or Phase 6 transition.

Kitchen preparation completed before the pause:

- Audited Program, DbContext mappings, queue GET/PATCH endpoints, read method
  and approved SELECT matrix. Kitchen maps orders, order_items and
  order_item_events under the existing Ordering-boundary exception; these are
  already within the 25 previously fingerprinted unique tables, not three new
  physical tables. No dedicated kitchen tables were created.
- Diagnostic container on the existing network reproduced DNS exit 2; dynamic
  host-gateway DNS and TCP 5432 probes both passed. Added this mapping ONLY to
  kitchen in docker-compose.gate-c.yml; existing five mappings untouched.
- Prepared a GET-only, READ ONLY operation with explicit privilege checks,
  but it STOPPED at preflight BEFORE any credential load/SQL/service startup:
  a previously passed service was no longer running. Runtime result JSON is
  C:\Users\ASUS\AppData\Local\AICOS\deployments\kitchen_runtime_20261027_215619\kitchen-result.json; its FAIL means preflight failed,
  not that Kitchen application readiness failed.

Read-only inspection then found all five previously passed services AND Redis
exited with code 0, OOMKilled=false, RestartCount=0, FinishedAt around
2026-10-27T14:55:02Z (21:55 +07). Docker Desktop/Engine remained running.
The executed commands did not stop or restart these containers; the cause of
their graceful stop is not attributed without evidence.

| Kitchen check | Actual status |
|---|---|
| Same-network diagnostic DNS/TCP with host-gateway | PASS |
| Kitchen startup/readiness | NOT RUN; blocked preflight, then owner pause |
| Actual kitchen_svc database authentication | NOT RUN |
| Live SELECT-matrix audit/fingerprints | NOT RUN |
| API GET authentication/metadata/Swagger/log smoke | NOT RUN |
| Ticket creation/update/completion or non-GET HTTP | NOT RUN |
| Existing five services restarted/recreated by this step | NO |

No new assertion is made that the 25-table fingerprints still match: the new
Kitchen database checks did not execute. Historical results above remain
historical. No migration, seed, database/schema/data write, GRANT, credential/
JWT reset, Firewall/PG security change, Mobile edit, commit or push occurred.
Only the un-deployed Kitchen mapping and this report/changelog changed in the
repository. Existing encrypted credentials were not reset or displayed.

**PAUSED. Wait for owner instruction after their stack inspection.**

## Current result: Payment PASS_RUNTIME — STOP after Payment

2026-10-27 21:46 +07. Only Payment (Compose payment) started in this step.
Identity, Menu, Table, Ordering and existing Redis were NOT restarted/recreated.
No Gateway, Kitchen or further Microservice was started. Gate C remains pending
the owner's approval for its next step; no Phase 6 transition occurred.

| Required result | Actual result |
|---|---|
| Payment runtime | PASS |
| Database connectivity | PASS: payment_svc to aicos_db.public |
| MigrateOnStartup | false in effective config AND running container |
| Payments:MutationsEnabled | false in effective config AND running container |
| Authentication | PASS: 8 missing/invalid-bearer GET probes return 401 |
| Authenticated bill/settlement payloads | NOT RUN: no legitimate JWT provided |
| Financial/protected fingerprints | Unchanged: 4 Payment + 21 previous-service tables |
| Refunds fingerprint | NOT CHECKED: payment_svc has no SELECT privilege; current refunds feature not implemented |

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\payment_runtime_20261027_214531.
payment-result.json records the actual flags/statuses/GET codes, and the
payment/ordering/table/menu/identity-before.txt and matching -after.txt files
contain only table counts/digests. No credential, JWT or row value is exposed.

### Audit, dependencies and verified config change

Reviewed Payment Program, metadata, DbContext, bill/payment endpoints,
PaymentMaintenanceMiddleware and the bill/settlement read implementations.
Current active Payment tables are bills, bill_items, payments, payment_events.
PaymentsDbContext also declares Refunds; no current refund API was exercised
and the approved payment_svc grants do not include reading refunds. A live
READ ONLY has_table_privilege check confirmed SELECT=false. No grant or admin
bypass was used to fingerprint that unused mapped table.

Readiness requires PostgreSQL and the existing healthy Redis on the AICOS
network. Effective Compose depends_on is Redis healthy only. The typed Ordering
client points to http://order:8080; bill reads use it for source access checks
after authorization. The settlement GET reads Bills/Payments with AsNoTracking
and does not save changes. No authenticated delegate/business RPC was invoked
by the unauthorized probes; configuration alone is not an S2S contract test.

An entrypoint-overridden diagnostic aicos/payment container on the SAME network
reproduced the unresolved host name, then passed using dynamic mapping:

| Probe | Original config | With host-gateway |
|---|---|---|
| Resolve host.docker.internal | Exit 2 | Exit 0 |
| TCP host.docker.internal:5432 | Not run; name unresolved | Exit 0 |

Only Payment in docker-compose.gate-c.yml was changed:

```yaml
services:
  payment:
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

No IP is hardcoded. Docker-selected fdc4:f303:9324::254 is diagnostic output
only. The four previous service mappings/configurations stayed unchanged.
No global Docker DNS, Windows Firewall or PostgreSQL security change ran.

### Credentials, startup guards and exact deployment boundary

- Reused the existing DPAPI credentials and original shared JWT key privately
  in process environment, restoring the environment afterward. No reset, login,
  account creation, token generation or plaintext secret output/file.
- Actual READ ONLY PostgreSQL connection verified payment_svc, aicos_db, public,
  transaction_read_only=on. SELECT LIMIT 0 passed on the four active tables.
  Schema inventories before/after both remain 38 tables / 244 columns.
- Quiet Compose validation, role/connection/JWT checks and both false guards
  passed BEFORE startup. Both runtime flags were checked again with printenv.
- Only payment started using --no-deps --no-build --pull never; Redis was
  already healthy. No dependency or previous Microservice was started/replaced.
- Existing image was used, unchanged:
  sha256:6335297a27c87752dadd9a67cac3d0ec84b0177d452332bcf7fd327989316f5e.
  Actual host binding: 127.0.0.1:5105 only.

Commands executed WITH the reviewed scoped secret context loaded; the example
env file alone does not contain real credentials:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never payment
docker exec aicos-payment printenv Database__MigrateOnStartup
docker exec aicos-payment printenv Payments__MutationsEnabled
docker exec aicos-payment getent hosts host.docker.internal
docker exec aicos-payment timeout 5 bash -c '</dev/tcp/host.docker.internal/5432'
```

**No mutation-rejection HTTP probe ran.** Middleware was inspected and its
configuration disabled writes, but POST/PUT/PATCH/DELETE blocking (e.g. 503)
was NOT actively tested because every non-GET request was forbidden in this
step. Do not report a live write-blocking test PASS or enable mutations.

### Actual GET and log results

| Request/check | No bearer | Deliberately invalid bearer |
|---|---|---|
| GET /api/v1/payments/bills?sessionId={probe-guid} | 401 | 401 |
| GET /api/v1/payments/bills?orderId={probe-guid} | 401 | 401 |
| GET /api/v1/payments/bills/{probe-guid} | 401 | 401 |
| GET /api/v1/payments/sessions/{probe-guid}/settlement-status | 401 | 401 |

Additional actual GETs: /health/live=200, /health/ready=200 (PostgreSQL+Redis),
/api/v1/payments/_info=200 (service payment/schema public), and
/swagger/v1/swagger.json=200 with all three GET route templates present.
Inspected last 200 stdout/stderr log lines privately: no flagged unhandled
error, PostgreSQL auth/permission error, timeout or migration-start pattern.

Probe GUID 00000000-0000-0000-0000-000000000001 is not claimed to identify an
existing bill/order/session. Invalid bearer was opaque invalid text, not a
signed or artificially issued JWT. The eight 401 probes verify rejection only.

**NOT RUN:** authenticated bill lookup/content/404 semantics, cross-branch or
role isolation, real settlement amounts/statuses, Payment-to-Ordering/Table
business communication, idempotency/concurrency, audit-writing, cash/QR flows,
confirmation and bill closure. No legitimate JWT/fixtures were available, and
mutation coverage is outside the approval. No test account/login/seed was used
to force successful financial responses.

### Data integrity and preserved services

All direct SQL used BEGIN READ ONLY and default_transaction_read_only=on.
bills, bill_items, payments and payment_events each contain zero rows; all
four before/after count/fingerprints match. Identity's 7, Menu's 6, Table's 4
and Ordering's 4 also match: 25 tables in total. refunds and other ungranted/
out-of-scope tables were NOT re-fingerprinted; this is not a full 38-table data
audit. No schema/data mutation was issued, and schema counts stay 38/244.

The four protected containers retain their original IDs/images/start times;
their environments and host mappings matched Compose before Payment startup:

| Service | Original StartedAt (UTC), unchanged |
|---|---|
| Identity | 2026-10-27T13:44:36.094713931Z |
| Menu | 2026-10-27T14:00:15.182727805Z |
| Table | 2026-10-27T14:09:15.409851471Z |
| Ordering | 2026-10-27T14:28:46.779215092Z |

Final running AICOS inventory: Identity, Menu, Table, Ordering, Payment, Redis.
Only the Payment mapping in docker-compose.gate-c.yml, this report and
CHANGELOG.md changed in the repository this step. No .NET/Flutter code change,
migration/seed, bill/payment/order mutation, credentials reset, commit/push,
Gateway/other-service startup or Phase 6 occurred.

Validation used actual DNS/TCP/READ ONLY PostgreSQL and GET requests, plus
config/syntax/target-role/GET-only guards, log inspection and fingerprints.
No unit/mutation integration-suite result is claimed. Existing encrypted role
cache stays private outside Git for future approved steps. No rollback ran.

**Payment runtime/database/authentication PASS, mutations remain disabled;
authenticated business coverage and refunds fingerprint are pending as above.
STOP after Payment and wait for approval of the next step.**

## Current result: Ordering PASS_RUNTIME — STOP after Ordering

2026-10-27 21:30 +07. Only Ordering (Compose service order) started in this
step. Identity, Menu, Table and existing Redis were NOT restarted/recreated.
No Gateway, Payments, Kitchen or other Microservice was started. Full Gate C
is still incomplete and requires separate approval for the next service.

| Required result | Actual result |
|---|---|
| Ordering runtime | PASS |
| PostgreSQL connectivity | PASS: aicos_db.public, ordering_svc |
| Authentication checks | PASS: both protected GETs reject absent/invalid bearer with 401 |
| Authenticated business responses | NOT RUN: no legitimate branch-scoped JWT provided |
| Ordering fingerprints | All 4 tables unchanged |
| Identity/Menu/Table fingerprints | All 17 tables unchanged |
| Existing PASS containers | IDs, images, StartedAt and composed environments unchanged |

Evidence:
C:\Users\ASUS\AppData\Local\AICOS\deployments\ordering_runtime_20261027_212831.
ordering-result.json contains actual statuses/GET codes/redacted log flags.
ordering/table/menu/identity-before.txt and matching -after.txt contain only
table counts/digests; no row values, passwords or JWTs are exposed.

### Audit and verified Docker change

Reviewed Ordering Program, OrderingDbContext, service metadata, OrderEndpoints
and OrderApplicationService read methods. Owned public tables: orders,
order_items, order_item_options, order_item_events. Current endpoint group is
/api/v1/orders; metadata service name is order. No startup seed or active
event-handler subscription was invoked.

Readiness uses PostgreSQL and existing Redis on aicos-be_default; effective
Compose depends_on is Redis healthy only. Typed clients are configured to
http://table:8080 and http://menu:8080. Existing Table/Menu were running.
The inspected create-order path calls those clients; the current GET methods
read Ordering tables directly. No create-order request or service-to-service
business flow was tested. The session list uses AsNoTracking; order detail
loads entities but does not call SaveChanges in its GET path.

Temporary diagnostic containers used aicos/order on the SAME AICOS network,
with an overridden diagnostic entrypoint and no credentials:

| Probe | Original config | Dynamic host-gateway mapping |
|---|---|---|
| Resolve host.docker.internal | Exit 2 | Exit 0 |
| TCP host.docker.internal:5432 | Not run; name unresolved | Exit 0 |

After this verification, only order in docker-compose.gate-c.yml received:

```yaml
services:
  order:
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

No IP was hardcoded. Docker selected fdc4:f303:9324::254 during probes; that
address is diagnostic metadata only. Identity/Menu/Table mappings were not
edited. No global Docker DNS, Firewall or PostgreSQL security change ran.

### Credentials, PostgreSQL and startup controls

- Reused original DPAPI credentials and Identity's existing shared JWT key in
  scoped process environment. No credential/JWT reset, token creation, login
  or test-account provisioning. Environment was restored afterward.
- Actual ordering_svc connection verified current_database=aicos_db,
  current_schema=public and transaction_read_only=on. SELECT LIMIT 0 succeeded
  on all four owned tables. Before/after metadata inventories stay 38/244.
- Quiet Compose validation and Ordering connection/JWT/migration guards passed
  before startup; actual container connection uses ordering_svc. Runtime
  Database__MigrateOnStartup remains false. No migration/seed/DDL/DML ran.
- Only order was started, with --no-deps --no-build --pull never. Redis was
  already healthy and no other dependency was started or reconfigured.
- Existing Ordering image used, unchanged:
  sha256:1693f1aea47b523093604fc9459b2cbf3d4f541b306a3220f25021bd88689c58.
  Actual host binding: 127.0.0.1:5104 only.

Commands were executed WITH the reviewed secret context already loaded; the
example env file alone does not contain valid runtime credentials:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never order
docker exec aicos-order printenv Database__MigrateOnStartup
docker exec aicos-order getent hosts host.docker.internal
docker exec aicos-order timeout 5 bash -c '</dev/tcp/host.docker.internal/5432'
```

### Actual HTTP and log results

| GET/check | Result |
|---|---|
| /health/live | 200 |
| /health/ready | 200; actual PostgreSQL and Redis checks |
| /api/v1/orders/_info | 200; service order, schema public |
| /swagger/v1/swagger.json | 200; session-list and order-detail GETs present |
| /api/v1/orders/?sessionId={probe-guid}, no bearer | 401 |
| Same session-list GET, invalid bearer | 401 |
| /api/v1/orders/{probe-guid}, no bearer | 401 |
| Same detail GET, invalid bearer | 401 |
| Logs, stdout/stderr last 200 lines | No flagged unhandled, PostgreSQL auth/permission, timeout or migration-start error |

Probe GUID 00000000-0000-0000-0000-000000000001 only supplies a syntactically
valid route/query value for auth rejection. It is not an existing session/order
fixture. The invalid bearer is deliberately invalid opaque text, not an issued
or forged signed JWT. No token/account/login API was used to force a 200.

**NOT RUN due to missing legitimate JWT/fixtures:** authenticated order list
and detail 200 payloads, branch/role isolation, authorized missing-order 404,
and related business query behavior. Missing/invalid-bearer rejection does not
prove those business paths. Creating, modifying or canceling orders was not
attempted, nor were Table/Menu calls in the create-order flow.

### Data integrity and preserved services

All direct SQL used BEGIN READ ONLY and default_transaction_read_only=on.
Ordering's four tables each have zero rows; no order/item/event was inserted.
All 21 before/after table count/fingerprints match: 4 Ordering + 4 Table +
6 Menu + 7 Identity. Remaining tables were not re-fingerprinted; this is not a
fresh full-database data audit. No schema/data mutation was issued.

Protected container IDs, images and start timestamps match before/after:

| Service | Original StartedAt (UTC), unchanged |
|---|---|
| Identity | 2026-10-27T13:44:36.094713931Z |
| Menu | 2026-10-27T14:00:15.182727805Z |
| Table | 2026-10-27T14:09:15.409851471Z |

Their environments and host mappings also matched the effective Compose config
before startup. Final running AICOS inventory: Identity, Menu, Table, Ordering
and Redis. No Gateway or further service was started.

Repository files changed this step: order mapping in docker-compose.gate-c.yml,
this report and CHANGELOG.md. No .NET/Flutter source changed. Checks run:
Compose/operation syntax/target-role guards, actual DNS/TCP/READ ONLY PostgreSQL,
GET health/metadata/OpenAPI/auth rejection, logs and before/after fingerprints.
No .NET unit suite or PostgreSQL mutation integration-test result is claimed.

No credentials shown, Mobile change, commit/push, schema/data/security change,
seed/migration or Phase 6 occurred. Existing encrypted cache remains private
outside Git for later authorized steps. No rollback ran; undoing only this
mapping would reintroduce DNS dependency and must not reset database/volumes.

**Ordering runtime/database/authentication PASS, business coverage with valid
JWT pending. STOP after Ordering and await approval for the next step.**

## Current result: Table PASS_RUNTIME — STOP after Table

2026-10-27 21:11 +07. Only Table started in this step. Identity, Menu and Redis
were already running and were not restarted/recreated. No Gateway, Payments,
Ordering, Kitchen or other Microservice was started. Full Gate C is pending.

| Required result | Actual result |
|---|---|
| Table runtime | PASS |
| Database connectivity | PASS: aicos_db.public, table_reservation_svc |
| API authentication checks | PASS: missing/invalid bearer rejected with 401 |
| Authenticated business GET responses/branch isolation | NOT RUN: no legitimate JWT provided |
| Fingerprints | Unchanged: 4 Table + 7 Identity + 6 Menu tables |
| Identity/Menu containers | Unchanged IDs, images, start timestamps and composed environment |

Evidence:
C:\Users\ASUS\AppData\Local\AICOS\deployments\table_runtime_20261027_210910.
table-result.json contains the above actual status, GET codes and redacted log
flags. table/identity/menu-before.txt and matching -after.txt contain only table
counts/digests, no row data or credentials.

### Audit, dependencies and minimal configuration change

Reviewed Table Program, service metadata, TableReservationDbContext,
TableOperationsEndpoints, GET implementations and PaymentSettlementClient.
Table owns table_areas, cafe_tables, table_sessions, table_session_tables in
public. GetMapAsync/GetSessionAsync use AsNoTracking reads and staff/branch
authorization. No seed or active integration-event subscription was invoked.

Startup/readiness dependencies are PostgreSQL and the existing Redis service
on aicos-be_default; effective Compose depends_on is Redis healthy only.
The Payments typed client is configured at http://payment:8080, but is used
when closing a session. It is not called by the inspected map/session GETs or
health checks. Payments was not started or contacted in this step. Startup
PASS does not prove the settlement/close-session business path works live.

Temporary diagnostic containers used the existing aicos/table image, the SAME
Identity/Menu Docker network, and an overridden non-app entrypoint:

| Probe | Without mapping | With dynamic host-gateway mapping |
|---|---|---|
| host.docker.internal resolution | Exit 2 | Exit 0 |
| TCP host.docker.internal:5432 | Not run; hostname unresolved | Exit 0 |

After verification, only Table in docker-compose.gate-c.yml received:

```yaml
services:
  table:
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

No fixed IP is configured. The observed Docker-selected address
fdc4:f303:9324::254 is diagnostic metadata only. Identity/Menu mappings and
environments were compared privately with their existing containers and match.
No Firewall, global Docker DNS, PostgreSQL security or Flutter change occurred.

### Database, credentials and deployment controls

- Actual PostgreSQL authentication as table_reservation_svc verified aicos_db,
  public and transaction_read_only=on. SELECT LIMIT 0 succeeded on all four
  owned tables. Fresh metadata signature before/after stays 38 tables/244 cols.
- Reused existing encrypted DPAPI credentials and the original shared JWT key
  in scoped process environment; no reset or token issuance. Composed connection/JWT settings and
  actual runtime role/migration settings were checked without printing
  secrets. Process environment was restored on completion.
- MigrateOnStartup=false before startup and in the running Table container.
  No migration/seed/mutation SQL, GRANT/REVOKE or writing HTTP request ran.
- Used the existing Table image with no build, pull or application-code change:
  sha256:30a8f00534593d5fe5495d9c205936b59bf15d0da3ee7fa9a39d61e2b6b89d40.
- Quiet Compose validation passed before starting Table with --no-deps.
  Redis was already healthy; no dependency was implicitly started.
- Table host binding is 127.0.0.1:5103 only. Identity/Menu container IDs, image
  IDs and StartedAt values matched before/after; neither service was touched.

Commands executed within the reviewed DPAPI/shared-JWT context (the example
file alone contains no real credentials):

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never table
docker exec aicos-table printenv Database__MigrateOnStartup
docker exec aicos-table getent hosts host.docker.internal
docker exec aicos-table timeout 5 bash -c '</dev/tcp/host.docker.internal/5432'
```

### Actual GET and log results

| Request/check | Result |
|---|---|
| GET /health/live | 200 |
| GET /health/ready | 200; actual PostgreSQL and Redis dependency checks |
| GET /api/v1/tables/_info | 200; service table, schema public |
| GET /swagger/v1/swagger.json | 200; map and session GET routes present |
| GET /api/v1/tables/map, without token | 401 |
| GET /api/v1/tables/map, invalid bearer | 401 |
| GET /api/v1/tables/sessions/{probe-guid}, without token | 401 |
| GET /api/v1/tables/sessions/{probe-guid}, invalid bearer | 401 |
| Console logs, both stdout/stderr, last 200 lines | No unhandled-error, PG auth/permission, connection-timeout or migration-start pattern detected |

The probe GUID was 00000000-0000-0000-0000-000000000001, used only to match the
route for authentication rejection. It is not claimed to be an existing
session. The invalid bearer was deliberately invalid text, not a signed or
artificially issued JWT. No login/refresh or valid-token fabrication occurred.

**NOT RUN:** authenticated map/session 200 responses, actual-session 404
handling after authorization, branch/role isolation, open/close/clean commands,
and Table-to-Payments settlement behavior. The first set needs an existing
legitimate JWT and scoped fixtures; mutation scenarios require separate test
scope/environment. Four 401 checks prove rejection only, not business data
access or settlement correctness.

### Data integrity and stop boundary

All SQL probes/fingerprints used BEGIN READ ONLY plus
default_transaction_read_only=on. The four Table tables currently each have
zero rows; no demonstration table/session was inserted. Their fingerprints,
the seven Identity fingerprints and six Menu fingerprints are identical before
and after (17 tables total). Remaining tables were not re-fingerprinted; this
does not claim a new full 38-table data audit. No schema/data write was issued.

Identity remains container 29e61d4414409c5b5f4ce6379cd2fba0e129e393ee8dbac4f97803071ba61027,
StartedAt 2026-10-27T13:44:36.094713931Z. Menu remains container
f6baffd414cf87f95574884137092c6c3a44fc6c5f0cd27db30361f7e30c0f8f,
StartedAt 2026-10-27T14:00:15.182727805Z. Their original image IDs also match.

Changed repository files this step: Table mapping in docker-compose.gate-c.yml,
this report and CHANGELOG.md. No .NET/Flutter source changed. Validation was
Compose/syntax checks and actual DNS/TCP, READ ONLY PostgreSQL, GET auth/health/
metadata/OpenAPI, log scans and before/after fingerprints. No unit-suite or
mutation integration-test result is claimed.

Final running AICOS inventory: Identity, Menu, Table, Redis. Existing encrypted
credentials remain outside Git. No Gateway, further Microservice, commit/push,
Firewall/PG security edit or Phase 6 transition. To undo only this config fix,
remove the new Table extra_hosts entry and recreate Table in the same reviewed
secret context; no rollback was performed and that would restore DNS dependence.

**STOP after Table PASS_RUNTIME. Wait for owner approval of the next step.**

## Current result: Menu PASS_RUNTIME — stopped after Menu

2026-10-27 21:00 +07. Only Menu was started in this step. Identity and the
existing Redis dependency stayed running; neither was restarted/recreated.
No Gateway or further Microservice was started. Full Gate C is not complete.

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\menu_runtime_20261027_210012.
menu-result.json records PASS_RUNTIME and explicitly marks authenticated
catalog 200 as NOT RUN. Before/after files contain table counts/digests only.

### Audit and minimal Docker configuration change

Reviewed Menu Program, service metadata, MenuCatalogDbContext, catalog endpoint
and CatalogQueryService. Menu owns six mapped public tables: categories,
products, product_variants, product_option_groups, product_options,
branch_products. The catalog uses AsNoTracking reads, branch-scoped staff
authorization and optional category/search filters. No Menu mutation endpoint,
startup seed or active integration-event handler was added or invoked.

Actual dependencies: PostgreSQL on Windows and the existing healthy Redis at
redis:6379 on aicos-be_default. Menu has no typed service-to-service client in
its current startup. Compose depends_on for Menu is Redis healthy only; the
bundled PostgreSQL profile remains excluded.

The diagnostic container used aicos/menu on the SAME aicos-be_default network,
overriding the app entrypoint and supplying no credentials:

| Probe | Original configuration | Dynamic host-gateway mapping |
|---|---|---|
| host.docker.internal resolution | Exit 2 | Exit 0 |
| TCP host.docker.internal:5432 | Not run; hostname unresolved | Exit 0 |

Only the Menu section of docker-compose.gate-c.yml changed in this step:

```yaml
services:
  menu:
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

No IP was hardcoded. The observed Docker-selected address
fdc4:f303:9324::254 is diagnostic output only. Identity's mapping, environment,
JWT, image, port mapping and container remained unchanged. No global DNS,
Firewall, PostgreSQL security, Flutter Mobile or service architecture changed.

### Credentials, database and deployment validation

- Used the existing DPAPI role cache in memory; no password or JWT reset and
  no plaintext credential output/file. Reused the unchanged Identity JWT key
  for Menu. Restored process environment after execution.
- Actual READ ONLY PostgreSQL connection verified aicos_db, menu_svc, public,
  transaction_read_only=on. SELECT LIMIT 0 passed on all six owned tables.
- Before and after metadata inventories both report 38 public tables /
  244 columns. No migration, seed, GRANT/REVOKE or mutation SQL was executed.
- Quiet Compose validation passed. Checked the Menu role, connection string
  and shared JWT privately against expected values; actual runtime environment
  matches. Database__MigrateOnStartup=false in config and running container.
- Existing Redis was already healthy; it was not restarted. Menu started with
  --no-deps --no-build --pull never. No dependency service was implicitly started.
- Menu used the existing image, unchanged:
  sha256:e248478edeb7676025a4d71347987f7828e68c5b22cd1df0b57fa7fb35efde39.
  Actual port binding is 127.0.0.1:5102 only.

Commands were executed with the reviewed, scoped DPAPI secret context loaded;
the example env file alone does NOT contain runtime credentials:

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker compose @compose up -d --no-deps --no-build --pull never menu
docker exec aicos-menu printenv Database__MigrateOnStartup
docker exec aicos-menu getent hosts host.docker.internal
docker exec aicos-menu timeout 5 bash -c '</dev/tcp/host.docker.internal/5432'
```

### Actual read-only runtime results

| Check | Result |
|---|---|
| Menu DNS and TCP after startup | PASS, both exit 0 |
| GET /health/live | 200 |
| GET /health/ready | 200; actual PostgreSQL and Redis checks |
| GET /api/v1/menu/_info | 200; service menu, schema public |
| GET /swagger/v1/swagger.json | 200; catalog GET path present |
| GET /api/v1/menu/catalog without bearer | 401 as required |
| Same GET with a deliberately invalid bearer | 401 as required |
| Same GET with a legitimate branch-scoped staff JWT | NOT RUN; no existing legitimate token provided |
| Checked Menu logs | No unhandled error, PostgreSQL auth/permission error, connection timeout or migration-start pattern detected |
| Identity original container ID/image/StartedAt | Identical before/after |
| Six Menu table count/fingerprints | Identical before/after |
| Seven Identity table count/fingerprints | Identical before/after |

No login/refresh/provisioning call or artificial valid token was used to turn
the protected catalog test into a 200. Login writes refresh_sessions, outside
this read-only approval. Missing-token and invalid-token rejection verifies
authorization only, NOT the authenticated catalog query/filter/branch behavior.
That coverage remains pending; do not report every Menu business API test PASS.

All six Menu tables currently contain zero rows, unchanged by this step. No
demo catalog was seeded. Populated catalog/filter/branch-isolation scenarios
need existing authorized fixtures and token, or an isolated test environment
under separate scope; no mutation/test setup was executed here.

### Data integrity, files and stop boundary

SQL used BEGIN READ ONLY and default_transaction_read_only=on. Fingerprints
cover six Menu tables and seven Identity tables (13 in total); remaining
tables were not re-fingerprinted, so this is not a new full 38-table data audit.
No database write was issued. Schema inventory matches before/after. Only GET
HTTP probes were sent; no migration/seed/payment/order/session mutation ran.

Identity container is still 29e61d4414409c5b5f4ce6379cd2fba0e129e393ee8dbac4f97803071ba61027,
StartedAt 2026-10-27T13:44:36.094713931Z, with the original image and environment.
Menu configuration processing explicitly checked Identity environment equality;
only docker-compose.gate-c.yml Menu host mapping and this report/changelog
changed in the repository during this step. No .NET/Flutter code changed.
Validation was Compose config, operation syntax, actual DNS/TCP/READ ONLY SQL,
health/metadata/OpenAPI/auth-rejection GETs and before/after snapshots; no new
.NET unit test or PostgreSQL mutation test result is claimed.

Final running AICOS inventory: Identity, Menu and Redis. DPAPI credentials
remain private outside Git for later explicitly authorized Gate C steps.
No Gateway/other Microservice, commit, push or Phase 6 was performed.

**Menu PASS_RUNTIME, with authenticated catalog coverage pending. STOP here
for owner review before any next service.**

## Current result: Identity PASS after project-local Docker host mapping

2026-10-27 20:45 +07. **STOP AFTER IDENTITY, as requested. Gate C as a whole
is not yet complete; no other Microservice or Phase 6 was started.**

Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\identity_dns_20261027_204431.
identity-result.json reports PASS; identity-before.txt / identity-after.txt
contain only seven owned-table counts/digests, not row values or credentials.

### Root cause verified today

Docker Desktop initially reported starting and Engine pipe was unavailable.
It then became running, Engine 29.7.2, context desktop-linux, without any
Desktop start/reset/reconfiguration by this execution. Identity/Redis were
stopped containers retained from yesterday.

The baseline diagnostic container used the SAME aicos-be_default bridge network
and existing aicos/identity image with application entrypoint overridden:

| Probe | Without mapping | With dynamic host-gateway mapping |
|---|---|---|
| getent hosts host.docker.internal | Exit 2; unresolved | Exit 0 |
| TCP host.docker.internal:5432 | Name-resolution error, exit 1 | Exit 0 |

The Docker-selected address observed was fdc4:f303:9324::254. It is diagnostic
output only, NOT an address hardcoded in repository configuration. PostgreSQL
18 service was running with listeners on 0.0.0.0:5432 and [::]:5432. Network
aicos-be_default is not internal-only.

**Confirmed blocker: Docker's container resolver did not supply the special
Windows-host name on this network.** Configured custom upstream DNS servers
8.8.8.8 / 1.1.1.1 were observed, but were NOT changed. Their causal relationship
to Desktop's missing special name was not separately proven. The successful
mapped TCP and authenticated PostgreSQL readiness, with security unchanged,
show that Firewall/PostgreSQL security changes were unnecessary for this route.

### Minimal configuration change

Only Identity in docker-compose.gate-c.yml received:

```yaml
services:
  identity:
    extra_hosts:
      - "host.docker.internal:host-gateway"
```

This keeps hostname-based connection strings and lets Docker select the host
address dynamically. It does not modify global daemon DNS, Windows hosts,
Windows Firewall, pg_hba.conf, PostgreSQL listener/security, other service
configuration, database schema/data or Flutter Mobile.

Reference: [Docker Compose: dynamic host-gateway mapping](https://docs.docker.com/compose/how-tos/networking/#custom-dns-with-extra_hosts).
The behavior was additionally verified by actual DNS/TCP probes above.

### Controlled execution and credentials

- Reused the existing eleven-role Windows DPAPI cache through SecureString
  decryption in process memory only. No password reset or GRANT/REVOKE.
- Copied the existing Identity JWT key in memory. Compared every composed
  Identity environment value with its original container configuration before
  recreation: all unchanged. No credentials/signing key/token were output.
- Verified original image equals the current image tag; no build, pull or code
  upgrade occurred. Resulting Identity image remains
  sha256:ab6c76febf5e1630b5f2cd3422aa5ee8a0ac90a858d31be6118824a58e3a4c4c.
- Runtime environment was loaded in a scoped try/finally and restored after
  execution. No plaintext credential or full inspect/config output was saved.
- Quiet Compose validation and dynamic-mapping/migration guards passed before
  restarting anything. Actual Identity migration flag remains false.
- Resumed ONLY the existing AICOS Redis infrastructure dependency, waited for
  healthy, then recreated ONLY Identity with --no-deps. No Menu/Payments/Table/
  Ordering/Kitchen/Gateway or placeholder service was started.

Exact deployment commands, executed WITH the above secret context already
loaded (not standalone placeholder-credential commands):

```powershell
$compose = @('--env-file','.env.example','-f','docker-compose.yml',
             '-f','docker-compose.external-db.yml','-f','docker-compose.gate-c.yml')
docker compose @compose config --quiet
docker start aicos-redis
# Require Redis healthy before Identity recreation.
docker compose @compose up -d --no-deps --no-build --pull never --force-recreate identity
docker exec aicos-identity printenv Database__MigrateOnStartup
docker exec aicos-identity getent hosts host.docker.internal
docker exec aicos-identity timeout 5 bash -c '</dev/tcp/host.docker.internal/5432'
```

### Actual results and limits

| Check | Result |
|---|---|
| Native READ ONLY connection using existing identity_svc credential | PASS: aicos_db, identity_svc, public, transaction_read_only=on |
| Fresh preflight schema inventory | 38 public tables / 244 columns |
| Identity migration flag before/after recreation | false |
| Identity connection environment, including existing JWT/credential | Unchanged; compared privately |
| Identity DNS / TCP after recreation | PASS / exit 0 |
| GET http://127.0.0.1:5101/health/live | 200 |
| GET http://127.0.0.1:5101/health/ready | 200; actual PostgreSQL and Redis checks |
| GET http://127.0.0.1:5101/api/v1/identity/_info | 200; service identity, schema public |
| Seven owned Identity table fingerprints before/after | Identical |
| Identity host port | 127.0.0.1:5101 only |
| Other Microservices / Gateway | NOT RUN |
| Login/refresh/provision/payment mutation tests | NOT RUN |
| Migration / seed / Firewall / PostgreSQL security edits | NOT RUN |

SQL used BEGIN READ ONLY plus default_transaction_read_only=on. No production
database write was issued. Fingerprint comparison covers tenants, branches,
users, roles, tenant_users, staff_assignments and refresh_sessions only; it is
not a claim of a new full 38-table data audit. No .NET code changed, so no new
unit/PostgreSQL mutation tests were run for this configuration-only fix.

Final running AICOS inventory: aicos-identity and aicos-redis (healthy).
Unrelated containers were not modified by the executed commands. The encrypted
role cache remains outside Git for later authorized Gate C work; do not print
or commit it. No Mobile edit, commit, push or Phase 6 transition occurred.

### Recovery and next boundary

To undo this configuration change, remove ONLY the new Identity extra_hosts
entry and recreate ONLY Identity with the same reviewed credential/JWT context.
That would restore the previous DNS-dependent configuration and may reintroduce
the blocker; no rollback was performed. Never restore/reset aicos_db or delete
Docker volumes for a hostname-mapping rollback.

**Identity is PASS. STOP here for owner review before continuing other
Microservices.** Do not re-run the earlier full-stack helper blindly: it refuses
existing AICOS containers, and broad startup is outside this Identity-only task.

## Latest execution result: STOPPED AT IDENTITY — Docker host DNS failure

2026-10-26 23:43 +07. Attempt gate_c_manual_20261026_234007.
**Gate C INCOMPLETE / BLOCKED. No Phase 6.**

| Check/component | Actual result |
|---|---|
| Eleven actual PostgreSQL role authentications / owned SELECT probes | PASS |
| Effective privilege matrix | PASS, issue_count=0 |
| Main database/schema | aicos_db.public, fresh preflight 38 tables / 244 columns |
| Shared JWT + merged runtime configuration | PASS; no signing key/token logged |
| Redis | PASS, container healthy; no published host 6379 port |
| Identity | FAIL, readiness timeout; container remains running for diagnosis |
| Menu, Payments, Table, Ordering, Kitchen | NOT RUN, never started in this attempt |
| Gateway routing | NOT RUN, Gateway never started |
| Inventory, Workforce, Reporting, Customer, AI | NOT RUN, excluded from initial scope |
| Authenticated business GETs / S2S paths | NOT RUN |
| Mutation smoke / login / migration / seed | NOT RUN |

### Confirmed cause and diagnostic evidence

The helper stopped at Start/check identity, readiness timeout (line 159 in the
executed version), and did not start any later service. Redacted log analysis
found PostgreSQL health timeout, not a Redis failure; no password-authentication
or pg_hba rejection was detected in the inspected logs. No raw logs were exposed.

Actual Identity connection metadata, excluding all password content:
Host=host.docker.internal;Port=5432;Database=aicos_db;Username=identity_svc.
Actual runtime Database__MigrateOnStartup=false. Identity host port is loopback
only under the Gate C override. Redis publishes no host port, so VietAI Redis
on host 6379 is unaffected.

Read-only DNS/TCP probes from the running Identity container:

| Probe | Result |
|---|---|
| getent ahostsv4 host.docker.internal | Exit 2, no IPv4 resolution |
| getent hosts host.docker.internal | Exit 2, no host resolution |
| getent hosts redis | Exit 0, 172.22.0.2 |
| TCP probe host.docker.internal:5432 | Name resolution error; could not test TCP reachability |
| Windows PostgreSQL listener | 0.0.0.0:5432 and [::]:5432, PID 8732 |

Container resolver is Docker DNS 127.0.0.11, with upstream overrides listed as
8.8.8.8 and 1.1.1.1; ExtraHosts is empty; context desktop-linux. This identifies
failed host-name resolution as the current blocker. It does not prove those
upstreams are its ultimate cause, nor establish firewall/pg_hba readiness after
DNS is repaired. No firewall, pg_hba, Docker daemon DNS or network change ran.

### Database state and credential handling

All database operations used READ ONLY sessions. Runtime HTTP so far was GET
health polling; no login, order creation, payment or other mutation was sent.
No schema/data/role/grant change, seed, migration or rollback was performed.
The 25-table baseline readable by service roles exists; no after-snapshot was
executed because the sequence stopped. Whole-database fingerprint equivalence
is therefore NOT independently claimed. Financial state was not mutated by
these actions; current counts across all 38 tables were not re-audited.

The private Windows DPAPI cache exists at:
C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_c_manual_20261026_234007\gate-c-roles.dpapi.xml.
No plaintext password/key/token was written into source, logs or Git. Preserve
the encrypted cache for controlled retry; do not display/decrypt it in chat or
logs. Every cached role must still be authenticated again by the helper.

Current AICOS containers left running: aicos-redis and aicos-identity. No other
AICOS service was started. Unrelated VietAI containers were not changed by
these actions; their current inventory differs from earlier checkpoints.

### Next step requiring review before continuation

Resolve Docker-to-host DNS/addressing, then verify name resolution and TCP
5432 from the existing Identity container before a startup retry. Prefer a
project-local, validated connection/host mapping solution. Do not blindly
substitute a bridge gateway for the Windows PostgreSQL host. Global Docker DNS
changes/restarts could affect VietAI and must be presented for review first.
Firewall/pg_hba changes, if later found necessary, also require an explicit
impact review; no broad network allow rule is proposed or executed here.

The helper deliberately refuses existing AICOS containers on retry. Before
reusing/removing/recreating anything, present a precise plan for only the two
AICOS containers and the fresh evidence directory / existing encrypted cache.
Do not reset volumes or rerun migration/grants. Keep migrations and Payment
mutations disabled. Stop here and report the Identity failure before further
startup, as required by the Gate C approval.

## Current checkpoint: direct interactive execution needed

2026-10-26 23:38 +07. The reopened VERIFIED attempt
gate_c_verified_20261026_233739 stopped at Authenticate identity_svc, helper
line 211: the explicit empty-credential guard (RuntimeException). Zero role
credentials were verified and no DPAPI credential cache was created. No
database authentication was attempted at that step. This is distinct from the
older Validate merged runtime configuration failure. The evidence does not
establish whether empty input resulted from console IO or user input.

All AICOS services remain NOT RUN. No production PostgreSQL data/schema/role
change, migration, seed or writing HTTP request occurred in this checkpoint.
The unchanged hash-verified helper remains the offline-tested version. For
unambiguous input, the owner should invoke it directly in their own PowerShell:

```powershell
Set-Location 'D:\ki9\sep490\Sep490\AICOS-BE'
$gateCLog = Join-Path $env:LOCALAPPDATA ('AICOS\deployments\gate_c_manual_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$null = New-Item -ItemType Directory -Path $gateCLog
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docker\Invoke-GateCReadOnlyRuntime.ps1 -EvidenceDirectory $gateCLog
```

Each PostgreSQL role prompt needs its existing Gate A password; only the two
JWT prompts may be left empty. Credentials are never requested through chat.
Monitor the resulting runtime-result.json, not evidence from an older attempt.
No new runtime PASS is claimed until the direct execution result is verified.

## Latest console reconciliation: 2026-10-26 23:37 +07

The owner reported a Validate merged runtime configuration stop. Evidence
reconciliation found that failure in gate_c_retry_20261026_230618 (23:07).
The later tested-helper attempt gate_c_validated_20261026_231624 remained at
Authenticate identity_svc with zero roles and no encrypted credential cache;
its console PID 23972 had closed. No new runtime failure is inferred from the
older pasted output. No actual credential cache was available from either run.

Reopened the unchanged offline-tested helper (SHA-256 3693F89EB3010D603A5FE279A03165EC10E1A1C96C649ABAF5D4A9EED86BD98E),
with a distinct window title AICOS Gate C - VERIFIED - 20261026_233739,
PID 16500. Fresh evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_c_verified_20261026_233739.
Credential input and runtime results for this attempt remain pending. Window
title/launch arguments contain only non-secret metadata. No AICOS service has
been started by these reconciliation actions; PostgreSQL unchanged by them.

## Current checkpoint: configuration guard fixed and offline regression suite PASS

2026-10-26 23:16 +07. Gate C remains IN PROGRESS; production runtime smoke
has not passed. No Phase 6 transition.

Retry gate_c_retry_20261026_230618 again verified all eleven credentials,
effective permissions and schema 38/244. It then failed BEFORE startup at
Validate merged runtime configuration: PropertyNotFoundException, helper line
210. Exact cause: StrictMode property access on Redis, whose Compose service
has no environment property. This was an orchestration helper bug, not invalid
user credentials or a PostgreSQL failure. No AICOS containers were started.

Remediation and actual checks:

- The configuration validator now checks that environment exists before
  inspecting ConnectionStrings__Default; Redis is excluded from that loop.
- Added docker/tests/Test-GateCReadOnlyRuntime.ps1. Three full offline scenarios
  PASS (exit 0): success in eight ordered starts, injected Menu failure stops
  all later starts, encrypted cache retry uses zero password prompts and cleans
  up on success. These scenarios mock SQL/HTTP/container startup; they are NOT
  PostgreSQL integration tests or production runtime evidence.
- Added Windows DPAPI SecureString cache under the private evidence directory,
  named gate-c-roles.dpapi.xml. Only the Windows account can normally decrypt
  it; a private user DACL is applied when the file is created. It contains no
  plaintext password, JWT signing key or access token. No cache is stored in Git.
  Successful role credentials are cached to permit retry after a failure and
  are re-authenticated every attempt. Successful smoke deletes this cache.
- Offline testing caught repeated Set-Acl privilege failures during cache saves;
  applying the private DACL once at file creation fixed that regression. The
  successful cache round-trip test exercised actual Windows DPAPI with synthetic
  secrets. No real user credential was used in the offline tests.
- Latest helper SHA-256:
  3693F89EB3010D603A5FE279A03165EC10E1A1C96C649ABAF5D4A9EED86BD98E.

New local masked console: PID 23972, evidence directory
C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_c_validated_20261026_231624.
Its own authentication/startup results remain pending. The two earlier closed
attempts cleared their process credentials, so this first cache-enabled attempt
requires local re-entry; no password reset or role/grant operation is performed.

Current service classification: Redis plus the six AICOS services and Gateway
NOT RUN in the failed production attempts; no service runtime FAIL is inferred
from helper validation failure. Unrelated VietAI services remain untouched.

To resume a failed cache-enabled attempt, use a fresh EvidenceDirectory and
pass -CredentialCachePath pointing to its existing private DPAPI file. Do not
display/import/decrypt that file in logs or copy it into source control. Review
and report a service failure before another startup attempt, as required.

## Latest result: 11 role checks PASS; stopped at JWT before startup

Checkpoint 2026-10-26 23:01 +07, attempt gate_c_20261026_224904.
**Gate C NOT COMPLETE. No AICOS application/Redis container was started.**

| Executed check | Result |
|---|---|
| Actual authentication + public schema/READ ONLY checks for all 11 roles | PASS |
| Approved role SELECT LIMIT 0 probes | PASS |
| Effective permission matrix | PASS, issue_count=0 |
| Current public schema inventory | PASS, 38 tables / 244 columns |
| Baseline fingerprints of approved readable tables | Captured for 25 unique tables; no after-snapshot yet |
| Shared JWT configuration | FAILED at this step; old helper retained only step name, not exact exception |
| Redis/Identity/Menu/Payments/Table/Ordering/Kitchen/Gateway runtime | NOT RUN |
| Protected GET/Gateway/S2S smoke | NOT RUN |

The old evidence does not prove the exact JWT error. A too-short nonempty key
was a terminating branch in the old helper, but is not asserted as the observed
cause without an exception record. The closed attempt cleaned process secrets;
passwords cannot be recovered or reused from its JSON files.

Prepared remediation without starting services or changing PostgreSQL:

- JWT input now retries a short key while keeping verified role credentials in
  the current console; blank input generates a strong per-run key. Prompts
  clearly distinguish signing key/access token from database password.
- Failure evidence now includes exception TYPE and script LINE only, never
  exception text, tokens, connection strings or passwords.
- Existing VietAI containers were observed running after the initial preflight.
  vietai_redis publishes host 6379. None was stopped/reconfigured. New
  docker-compose.gate-c.yml removes AICOS Redis host publication and binds
  the seven core API/Gateway ports to loopback only. Internal Redis DNS/6379
  remains unchanged. Preflight permits only these three observed unrelated
  containers and still refuses existing AICOS containers/API port conflicts.
- Compose merge validation PASS: no AICOS Redis host port; core API loopback;
  all ten MigrateOnStartup=false; Payment mutations=false.
- Helper parser and three JWT input cases PASS using synthetic test inputs:
  empty, short-then-retry, valid existing key. No real credential used in tests.
- Current helper SHA-256:
  C02530AE36B076A3A2B79CEB89FBB9F5CA1CCDB44BDB11B2036EA39BD7D2874A.

Active execution command now uses the helper (which appends the third override):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\docker\Invoke-GateCReadOnlyRuntime.ps1 -EvidenceDirectory <fresh-directory-outside-Git>
```

Only READ ONLY SQL and HTTP GET are permitted. No production data/schema write
occurred in this attempt. Post-smoke full-data equivalence is not claimed:
only the before snapshot exists, and service roles cannot read all 38 tables.
The next local attempt needs hidden credential re-entry; no role/password
reset, GRANT, migration, login API or Phase 6 is authorized by this retry.

New retry console opened 2026-10-26 23:06 +07, PID 28036. Evidence directory:
C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_c_retry_20261026_230618.
Retry authentication and runtime results remain pending; do not reuse the old
attempt's PASS results as proof that the new startup completed.

## Latest checkpoint: Engine recovered; masked runtime console opened

2026-10-26 22:49 +07. **Gate C IN PROGRESS; not complete.**

- Docker Engine 29.7.2 verified with exit 0; docker ps exit 0 listed no running
  containers. Only reviewed PostgreSQL port 5432 was listening before launch.
- All seven required Backend images exist. Missing redis:7-alpine was pulled
  successfully; digest d9436322172a5b482c187bf1f363d3c4fea086fcca6b9c8a94de8f0f39ed3193.
- Added docker/Invoke-GateCReadOnlyRuntime.ps1 for the approved local masked
  input and staged checks. Syntax and native Docker capture validation PASS.
  SHA-256: 5E3A02EE0B21D8C5B17515D394960E22F2F281EACFE86C78951E1CF617BFF331.
- Opened interactive console PID 27816; evidence outside Git:
  C:\Users\ASUS\AppData\Local\AICOS\deployments\gate_c_20261026_224904.
  runtime-result.json contains status/role names/check results only, no secrets.
- The helper requires actual role authentication and the complete privilege
  matrix before service startup; SQL uses default_transaction_read_only=on
  plus explicit READ ONLY transactions. It checks schema counts and snapshots
  tables readable by the reviewed roles, not all ungranted business tables.
- Runtime order: Redis, Identity, Menu, Payments, Table, Ordering, Kitchen,
  Gateway. Each service must pass before the next starts. HTTP smoke uses GET
  only. Failure stops the sequence; no automatic rollback or later service start.
- JWT key is entered hidden; empty input generates a fresh per-run random key
  shared by Gateway/services. Previously issued tokens will not validate under
  a fresh key. Optional legitimate access token may be omitted; no API login is
  performed. Ordering/Payment positive fixture tests remain pending when no
  existing scoped fixture is available.
- No runtime service result is claimed yet. The state was Preflight with zero
  verified roles/services at the first monitor. Credential input and actual
  smoke results must be read from the new evidence before claiming completion.

The earlier blocked checkpoint below is retained as history. Its Docker blocker
is resolved; credential/runtime validation is now pending in the local console.

Checkpoint: 2026-10-26 22:35 +07.
**APPROVED FOR CONTROLLED READ-ONLY RUNTIME SMOKE / BLOCKED BEFORE STARTUP.**
Gate C is not complete. No transition to Phase 6.

The owner approved blocker resolution and read-only runtime smoke, with a
mandatory stop if Docker Engine is unavailable. This supersedes the earlier
preparation-only approval status in GATE_C_RUNTIME_READINESS.md; its technical
sequence still applies. Mutation/rejection POST probes are excluded from this
execution scope; no test login or other writing API is authorized.

## Checks actually executed

| Check | Result | Evidence |
|---|---|---|
| Docker context | PASS | desktop-linux |
| Docker Engine availability | FAIL / BLOCKER | docker info exit 1; pipe dockerDesktopLinuxEngine not found |
| Running-container inventory | NOT AVAILABLE | docker ps exit 1; cannot infer zero running containers from this failure |
| Backend .env presence | Absent | Test-Path .env = false; contents of secret files not read |
| 11 role password process variables | Absent | Presence booleans only; no secret printed |
| JWT_SIGNING_KEY process variable | Absent | Presence boolean only |
| Runtime database authentication / role privileges | NOT RUN | Engine and runtime credentials prerequisites not met |
| Runtime health / Gateway / service-to-service smoke | NOT RUN | Stopped before startup |

Credential absence means absent in the agent's current process environment.
It does not mean the PostgreSQL role passwords are missing or invalid. Gate A
passwords must not be reset simply to resolve runtime injection. No assumption
is made about credentials in another shell or secret manager.

## Service results

| Component | Runtime result |
|---|---|
| Redis | NOT RUN |
| Identity | NOT RUN |
| Menu | NOT RUN |
| Payments | NOT RUN |
| Table | NOT RUN |
| Ordering | NOT RUN |
| Kitchen | NOT RUN |
| Gateway | NOT RUN |
| Inventory, Workforce, Reporting, Customer | NOT RUN; skeleton services excluded from proposed initial core scope |
| AI | NOT RUN; unfinished business routes, excluded from proposed initial core scope |

No service has a runtime PASS/FAIL result yet. The FAIL above concerns Engine
availability, not application code or PostgreSQL.

## Database status

No PostgreSQL connection or SQL was executed at this checkpoint. No schema,
data, roles, credentials or grants were changed by these actions.
Last independently recorded Gate A/B baseline is aicos_db.public, 38 tables /
244 columns. That is historical evidence, not a fresh count at this checkpoint.
No migration, seed, rollback, write smoke, Mobile modification, commit/push or
Backend startup occurred.

The previously checked configuration keeps all ten .NET migration startup flags
false and Payment mutations false. Effective runtime values still require
verification after credentials are loaded and containers exist.

## Blockers and safe continuation

1. **Docker Engine unavailable.** Owner opens Docker Desktop from Start menu,
   waits for the Linux Engine to be running, and confirms readiness. Do not
   reset Docker, delete volumes or run Compose up. Desktop may restart saved
   containers: inspect the inventory before any AICOS service start.
2. **Runtime secrets not injected.** After Engine confirmation, use a dedicated
   local masked-input console/approved secret manager for the existing eleven
   role passwords and a shared JWT key. Do not send secrets in chat. Use the
   reviewed connection-string quoting / AI URI encoding in the readiness
   report, with process cleanup. Never print full Compose config/environment.
3. **Authenticated smoke needs a legitimate unexpired JWT and existing scoped
   fixtures.** Obtaining one through login writes refresh_sessions and is not
   allowed by the current read-only scope. If unavailable, leave protected
   positive-path smoke pending; prepare isolated-test plans without executing
   mutation tests.
4. Once Engine is confirmed, recheck context, running containers, image
   availability and port conflicts before staged startup. Then quiet config
   validation, role-specific readiness, direct read-only API checks and Gateway
   routing, stopping at the first service failure and reporting before further
   starts. Keep Payment mutations disabled.

Owner can verify Engine without starting the stack:

```powershell
docker info --format '{{.ServerVersion}}'
docker ps --format '{{.Names}} {{.Status}}'
```

Require successful exit codes; do not share logs containing credentials.
The next action is owner-controlled Docker Desktop startup and confirmation,
not a renewed approval request for the already approved read-only smoke scope.

