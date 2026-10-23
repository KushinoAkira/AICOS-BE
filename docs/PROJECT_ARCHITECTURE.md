# AICOS Backend - Architecture Overview

> Tài liệu tóm tắt nhanh kiến trúc và trạng thái hiện tại của repository. Hãy cập nhật file này khi có thay đổi kiến trúc đáng kể để lần phân tích sau không phải đọc lại toàn bộ source code.

## 1. Trạng thái hiện tại

- Repository là **backend foundation đang được triển khai theo module**; chưa có
  workflow nghiệp vụ hoàn chỉnh.
- Commit được khảo sát: `cdf983a` (`Initial backend foundation`).
- Đã có gateway, service bootstrap, persistence conventions, JWT, event bus, Docker Compose, health check và một số test nền tảng.
- Identity Organization đã có entity, migration và endpoint đăng nhập Staff theo
  chi nhánh. Chín service .NET còn lại mới có endpoint `_info`.
- Bốn endpoint của AI service đều trả về HTTP `501 Not Implemented`.

## 2. Kiến trúc tổng thể

Hệ thống là một **microservices monorepo** với giao tiếp lai:

```text
React / Flutter / Client
          |
          v
    YARP API Gateway
          |
   +------+----------------------+
   |                             |
10 service .NET 8         AI service (FastAPI)
   |                             |
   +-- REST đồng bộ -------------+
   +-- Redis Pub/Sub events -----+
   |                             |
   +----- PostgreSQL chung ------+
          mỗi service một schema
```

Thành phần triển khai:

- .NET 8 / ASP.NET Core Minimal APIs
- YARP Reverse Proxy
- Entity Framework Core + Npgsql
- PostgreSQL 16 + pgvector
- Redis 7 Pub/Sub
- Python FastAPI cho AI service
- Docker Compose cho môi trường local

## 3. Cấu trúc repository

```text
AICOS-BE/
├── src/
│   ├── BuildingBlocks/
│   │   ├── AICOS.BuildingBlocks/   # infrastructure dùng chung
│   │   └── AICOS.Contracts/        # integration-event contracts
│   ├── Gateway/AICOS.Gateway/      # YARP API Gateway
│   └── Services/
│       ├── IdentityOrganization/
│       ├── MenuCatalog/
│       ├── TableReservation/
│       ├── Ordering/
│       ├── Payments/
│       ├── InventoryPurchasing/
│       ├── KitchenOperations/
│       ├── WorkforceScheduling/
│       ├── ReportingAnalytics/
│       ├── CustomerEngagement/
│       └── Ai/                      # Python/FastAPI
├── tests/
├── infra/postgres/init/
├── docker/
└── docker-compose.yml
```

## 4. Gateway

Gateway là cửa vào chung và sử dụng YARP để route `/api/v1/{prefix}/**` tới từng service.

Trách nhiệm hiện có:

- Reverse proxy tới 11 service.
- Kiểm tra JWT nếu request có `Authorization` header.
- Cho request không có token đi tiếp để hỗ trợ login/menu public/guest ordering; service đích chịu trách nhiệm authorization.
- Rate limit theo IP, mặc định 600 request/phút.
- CORS cho frontend.
- Sinh, giữ và chuyển tiếp `X-Correlation-Id`.
- Serilog request logging.

Gateway không chứa nghiệp vụ và không phải nơi quyết định quyền truy cập chi tiết.

## 5. Danh sách service

| Service | Gateway route | DB schema | Dev port |
|---|---|---|---:|
| Identity & Organization | `/api/v1/identity` | `identity` | 5101 |
| Menu & Catalog | `/api/v1/menu` | `menu` | 5102 |
| Table & Reservation | `/api/v1/tables` | `table_reservation` | 5103 |
| Ordering | `/api/v1/orders` | `ordering` | 5104 |
| Payments | `/api/v1/payments` | `payment` | 5105 |
| Inventory & Purchasing | `/api/v1/inventory` | `inventory` | 5106 |
| Kitchen Operations | `/api/v1/kitchen` | `kitchen` | 5107 |
| Workforce & Scheduling | `/api/v1/workforce` | `workforce` | 5108 |
| Reporting & Analytics | `/api/v1/reporting` | `reporting` | 5109 |
| Customer Engagement | `/api/v1/customers` | `customer` | 5110 |
| AI | `/api/v1/ai` | `ai` | 5111 |

## 6. Cấu trúc bên trong service .NET

Mỗi service hiện là một ASP.NET Core project duy nhất:

```text
AICOS.<Service>.Api/
├── Program.cs
├── <Service>Service.cs        # name, display name, route prefix, schema
├── Persistence/
│   └── <Service>DbContext.cs
├── Domain/                    # dự kiến chứa entity/value object
└── Features/                  # dự kiến chứa endpoint + handler theo use case
```

Định hướng là **Vertical Slice Architecture trong từng microservice**, không phải Clean Architecture nhiều project. `Program.cs` là composition root và gọi các extension dùng chung:

1. `AddAicosServiceDefaults`
2. `AddAicosPostgres`
3. `AddAicosEventBus`
4. `MigrateDatabaseAsync`
5. `UseAicosServiceDefaults`
6. `MapAicosApiGroup`

## 7. Building Blocks và Contracts

`AICOS.BuildingBlocks` cung cấp:

- Logging: Serilog, service name, correlation ID.
- HTTP: global exception handler, RFC 7807 `ProblemDetails`, typed service clients.
- Security: JWT validation, roles, policies, `ICurrentUser`, JWT issuing helper.
- Persistence: base `AicosDbContext`, snake_case naming, migrations per schema, audit interceptor.
- Messaging: Redis publisher/subscriber và event-handler registration.
- Health: `/health/live` và `/health/ready`.

`AICOS.Contracts` chứa integration events dùng chung:

- `OrderCreatedEvent`
- `OrderCompletedEvent`
- `PaymentConfirmedEvent`
- `StockChangedEvent`
- `LowStockAlertEvent`

Việc dùng chung source-level contracts giúp phát triển nhanh nhưng có nguy cơ buộc nhiều service nâng cấp đồng thời khi contract thay đổi breaking.

## 8. Dữ liệu

Hệ thống dùng một PostgreSQL instance nhưng cô lập dữ liệu theo schema và login role:

```text
Database aicos
├── identity          -> identity_svc
├── menu              -> menu_svc
├── ordering          -> ordering_svc
├── payment           -> payment_svc
├── inventory         -> inventory_svc
├── kitchen           -> kitchen_svc
└── ...
```

Quy tắc:

- Mỗi service chỉ sở hữu và truy cập schema của mình.
- Không được SQL join xuyên service.
- Lấy dữ liệu từ service khác bằng REST hoặc integration event.
- Bảng lịch sử EF migration nằm trong schema của service.
- AI schema chứa extension `pgvector` để chuẩn bị cho RAG/embedding.

Đây là **database-per-service về logic**, nhưng vẫn dùng chung PostgreSQL instance về vật lý.

## 9. Giao tiếp giữa các service

### REST đồng bộ

- Service gọi trực tiếp service khác, không đi qua gateway.
- Typed `HttpClient` chuyển tiếp bearer token và correlation ID.
- Có standard resilience handler: retry, circuit breaker và timeout.

### Redis Pub/Sub bất đồng bộ

Các luồng dự kiến:

```text
Ordering --OrderCreatedEvent------> Kitchen
Ordering --OrderCompletedEvent----> Reporting / Customer / AI
Payments --PaymentConfirmedEvent--> Ordering
Inventory --StockChangedEvent-----> Reporting / AI
```

Hạn chế quan trọng:

- Redis Pub/Sub là at-most-once.
- Consumer offline sẽ mất event.
- Handler lỗi chỉ được log, không retry.
- Chưa có dead-letter queue, idempotency hoặc Outbox Pattern.
- Chưa phù hợp cho event quan trọng như `PaymentConfirmedEvent` trong production.

## 10. Authentication và authorization

- Identity service dự kiến phát JWT.
- Gateway kiểm tra token có mặt trước khi proxy.
- Mỗi service vẫn xác thực và phân quyền độc lập.
- Claim chuẩn: `sub`, `name`, `role`, `branch_id`.
- Role: `SystemAdmin`, `Manager`, `Cashier`, `WarehouseStaff`, `AiBusinessAssistant`, `Customer`, `Guest`.
- Policy chung: `AdminOnly`, `Management`, `Staff`.

Điểm cần lưu ý:

- `IBranchScoped` mới chỉ là marker; chưa có EF global query filter hoặc authorization tự động theo branch.
- Tất cả service dùng chung symmetric JWT signing key.
- `JwtTokenService` được đăng ký trong BuildingBlocks của mọi service dù thiết kế nói chỉ Identity nên phát token.
- Production nên cân nhắc asymmetric signing: Identity giữ private key, service khác chỉ giữ public key.

## 11. AI service

AI service dùng FastAPI và sao chép cùng contract JWT/correlation ID với các service .NET.

Các tính năng dự kiến:

- RAG chatbot / Smart Barista.
- Face-scan attendance.
- Demand forecasting.
- AI business summary.

Trạng thái hiện tại:

- Router và authorization skeleton đã có.
- Health check TCP cho PostgreSQL và Redis đã có.
- Cả bốn feature endpoint đều trả `501 Not Implemented`.
- LangChain, pgvector client, Groq/DeepSeek client, InsightFace và XGBoost chưa được cài/implement.

## 12. Deployment và vận hành

`docker-compose.yml` dựng:

- PostgreSQL + pgvector.
- Redis.
- PgAdmin qua profile `tools`.
- Gateway.
- 10 service .NET.
- AI service.

Một Dockerfile .NET dùng chung nhận `PROJECT` và `ASSEMBLY` làm build arguments. Mỗi service vẫn tạo image/container riêng.

Observability hiện có logging, correlation ID và health checks; chưa thấy distributed tracing, metrics hoặc centralized log collector.

## 13. Test và kết quả khảo sát

Test hiện có:

- Unit test cho JWT issuing.
- Unit test cho audit interceptor.
- Smoke test khởi động 10 service và kiểm tra `_info`, live health, correlation ID.
- Gateway test cho JWT và correlation ID.
- FastAPI test cho health, correlation ID và role checks.

Kết quả khảo sát ngày 2026-10-11:

- `docker compose config --quiet`: hợp lệ.
- Chưa chạy được `.NET` tests: repository ghim SDK `8.0.100`, máy chỉ có SDK `10.0.300` và `10.0.302`.
- Chưa chạy được Python tests: Python executable hiện không khả dụng trong môi trường.

## 14. Các rủi ro chính

1. Số lượng service lớn so với phần nghiệp vụ hiện tại, tạo nhiều chi phí triển khai và vận hành.
2. Redis Pub/Sub không đảm bảo giao event cho payment/order flow.
3. Chưa có Outbox, idempotency hoặc eventual-consistency strategy rõ ràng.
4. Chưa enforce branch-level data isolation tự động.
5. JWT symmetric secret dùng chung làm tăng blast radius nếu một service bị lộ secret.
6. Shared BuildingBlocks/Contracts có thể dẫn tới coordinated deployment.
7. Chưa có distributed tracing và metrics.
8. Test mới tập trung vào bootstrap/infrastructure, chưa có business test.

## 15. Thứ tự triển khai đề xuất

1. Identity: user, role, branch và login đã có; refresh token/provisioning còn thiếu.
2. Menu/Catalog.
3. Ordering.
4. Payment và webhook/idempotency.
5. Kitchen queue.
6. Inventory deduction và stock events.
7. Table reservation, workforce, reporting, customer engagement.
8. AI features sau khi dữ liệu nghiệp vụ ổn định.

Trước production nên bổ sung:

- Redis Streams/RabbitMQ hoặc broker bền vững.
- Transactional Outbox và idempotent consumer.
- OpenTelemetry tracing/metrics.
- Testcontainers cho PostgreSQL/Redis integration tests.
- CI build/test/Docker build.
- Secret management và asymmetric JWT signing.
- Contract versioning và backward-compatibility rules.
