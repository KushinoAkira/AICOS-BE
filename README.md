# AICOS Backend

Nền backend microservice cho **AI-Integrated Smart Coffee Shop Platform** (.NET 8 · PostgreSQL · Redis · Docker).
Kiến trúc bám theo sơ đồ `AICOS_Architecture_Diagram.png`: API Gateway + 11 service (10 .NET + 1 Python AI).

## Yêu cầu

- .NET SDK 8 (`global.json` ghim 8.0.x), Docker Desktop, Python 3.11 (chỉ khi chạy AI service ngoài Docker)

## Chạy nhanh

```powershell
# 1. Hạ tầng (PostgreSQL+pgvector, Redis)
docker compose up -d postgres redis

# 2. Chạy 1 service từ IDE / CLI (port theo bảng bên dưới, tự bật Swagger ở /swagger)
dotnet run --project src/Services/Ordering/AICOS.Ordering.Api
dotnet run --project src/Gateway/AICOS.Gateway

# hoặc chạy cả hệ thống trong Docker (qua gateway: http://localhost:8080)
docker compose up -d --build

# Test
dotnet test AICOS.sln
```

Kiểm tra: `GET http://localhost:8080/api/v1/orders/_info`, health: `/health/live`, `/health/ready` (mỗi service).
Mọi giá trị mặc định là **dev-only**; đổi qua `.env` (xem `.env.example`).

## Cấu trúc

```
Backend/
├─ src/
│  ├─ BuildingBlocks/
│  │  ├─ AICOS.Contracts/          Integration events (OrderCreated, PaymentConfirmed, StockChanged, ...) – không phụ thuộc gì
│  │  └─ AICOS.BuildingBlocks/     Thư viện dùng chung cho mọi service (xem bên dưới)
│  ├─ Gateway/AICOS.Gateway/       YARP: routing, validate JWT, rate limit, CORS, correlation id
│  └─ Services/
│     ├─ IdentityOrganization/  MenuCatalog/  TableReservation/  Ordering/  Payments/
│     ├─ KitchenOperations/  WorkforceScheduling/
│     ├─ ReportingAnalytics/    CustomerEngagement/        (mỗi thư mục = 1 project AICOS.<Tên>.Api)
│     └─ Ai/                    FastAPI (RAG chatbot, face-scan, forecast, summary) – hiện là skeleton 501
├─ tests/                         BuildingBlocks.Tests (unit) + Services.SmokeTests (boot 10 service + gateway)
├─ infra/postgres/init/           Tạo schema + role riêng cho từng service, bật pgvector
├─ docker/Dockerfile.dotnet       1 Dockerfile dùng chung cho gateway và 10 service .NET
└─ docker-compose.yml
```

| Service (key)              | Project                 | Route qua gateway    | Schema DB           | Port dev |
|----------------------------|-------------------------|----------------------|---------------------|----------|
| Gateway                    | AICOS.Gateway           | `/`                  | –                   | 5000 (Docker: 8080) |
| Identity & Organization    | IdentityOrganization    | `/api/v1/identity`   | `identity`          | 5101 |
| Menu & Catalog             | MenuCatalog             | `/api/v1/menu`       | `menu`              | 5102 |
| Table & Reservation        | TableReservation        | `/api/v1/tables`     | `table_reservation` | 5103 |
| Order                      | Ordering                | `/api/v1/orders`     | `ordering`          | 5104 |
| Payment                    | Payments                | `/api/v1/payments`   | `payment`           | 5105 |
| Kitchen Operations         | KitchenOperations       | `/api/v1/kitchen`    | `kitchen`           | 5107 |
| Workforce & Scheduling     | WorkforceScheduling     | `/api/v1/workforce`  | `workforce`         | 5108 |
| Reporting & Analytics      | ReportingAnalytics      | `/api/v1/reporting`  | `reporting`         | 5109 |
| Customer Engagement        | CustomerEngagement      | `/api/v1/customers`  | `customer`          | 5110 |
| AI (Python)                | Ai                      | `/api/v1/ai`         | `ai` (+ pgvector)   | 5111 |

> Tên project tránh trùng tên entity (vd. `AICOS.Ordering` chứ không phải `AICOS.Order`, để sau này khai báo class `Order` không bị C# hiểu nhầm là namespace).

## Mỗi service .NET trông thế nào

Một project `Microsoft.NET.Sdk.Web` duy nhất, chia thư mục theo feature (đủ cho team 3 backend dev / 12 tuần; nếu 1 service phình to thì tách Domain/Application ra sau):

```
AICOS.Ordering.Api/
├─ Program.cs                 ~15 dòng, gọi các extension của BuildingBlocks
├─ OrderingService.cs         AicosServiceInfo (tên, route prefix, schema) – cũng là marker cho test
├─ Persistence/OrderingDbContext.cs
├─ Domain/                    entity, value object
└─ Features/                  endpoint + handler theo từng use case
```

`AICOS.BuildingBlocks` cung cấp sẵn:

| Nhóm | Nội dung |
|------|----------|
| Hosting | `AddAicosServiceDefaults` / `UseAicosServiceDefaults` / `MapAicosApiGroup` (`/api/v1/{prefix}` + `_info`), Serilog, Swagger có nút Bearer, health `live`/`ready` |
| Security | JWT bearer dùng chung (`Jwt:*`), 7 role (`AicosRoles`), policy (`AdminOnly`, `Management`, `Staff`), `ICurrentUser` (UserId, Role, BranchId), `JwtTokenService` (dành cho Identity) |
| Persistence | `AicosDbContext` (khoá vào schema của service), naming snake_case, bảng `__ef_migrations_history` nằm trong schema riêng, `AuditableEntity` tự điền CreatedAt/By, UpdatedAt/By, `MigrateDatabaseAsync` |
| Messaging | `IEventBus.PublishAsync`, `AddIntegrationEventHandler<TEvent, THandler>()` trên Redis Pub/Sub |
| Http | Lỗi trả về `ProblemDetails` (RFC 7807) – ném `NotFoundException`, `BusinessRuleException("BR-06", ...)`, `ValidationFailedException`...; `X-Correlation-Id` xuyên suốt; `AddAicosServiceClient<>` gọi sync service khác (retry/circuit-breaker/timeout + chuyển tiếp token) |

## Việc thường làm

**Thêm entity + migration** (ví dụ Ordering):
```powershell
# thêm DbSet vào OrderingDbContext, rồi:
dotnet ef migrations add InitialCreate --project src/Services/Ordering/AICOS.Ordering.Api --output-dir Persistence/Migrations
# Chạy service với Database__MigrateOnStartup=true (đã bật sẵn trong launchSettings và docker-compose) là DB tự cập nhật
```

**Thêm endpoint**: tạo `Features/Orders/OrderEndpoints.cs` với `public static void MapOrderEndpoints(this RouteGroupBuilder api)`, rồi mở dòng comment `api.Map...` trong `Program.cs`. Dùng `.RequireAuthorization(AicosPolicies.Staff)` cho route cần quyền; route không gắn gì là public (khách quét QR).

**Publish / consume event**
```csharp
await eventBus.PublishAsync(new OrderCreatedEvent(...), ct);                         // ở Ordering
builder.Services.AddIntegrationEventHandler<OrderCreatedEvent, OrderCreatedHandler>(); // ở Kitchen
```
Event mới thêm vào `AICOS.Contracts` (đặt tên thì quá khứ: `XxxCreatedEvent`).

**Gọi sync service khác** (vd. Order → Menu, BR-06 < 500 ms):
```csharp
builder.Services.AddAicosServiceClient<IMenuClient, MenuClient>(builder.Configuration, "Menu",
    o => o.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(500));
```

**Thêm route mới ở gateway**: sửa `ReverseProxy` trong `src/Gateway/AICOS.Gateway/appsettings.json` (và biến môi trường cluster trong `docker-compose.yml`).

## Quy ước kiến trúc (đã chốt)

- **Mỗi service chỉ đụng schema của mình.** `infra/postgres/init` tạo role `<schema>_svc` chỉ thấy đúng schema đó → không thể join chéo service. Cần dữ liệu service khác thì gọi REST hoặc lắng nghe event.
- **Xác thực ở 2 lớp**: gateway từ chối token sai/hết hạn ngay tại cổng; token vắng mặt vẫn đi tiếp (guest, login, menu công khai). **Phân quyền do từng service quyết định** bằng policy/role.
- **Sync = REST trực tiếp giữa các service (không qua gateway); async = event trên Redis.**
- **Chưa implement logic nghiệp vụ nào** (không có entity, endpoint ngoài `_info`). Chỉ có hạ tầng và quy ước.

## Lưu ý / việc cần quyết định tiếp

1. **Redis Pub/Sub là at-most-once**: consumer đang tắt thì mất event, và nếu chạy nhiều instance của cùng service thì mỗi instance nhận 1 bản. Đủ cho demo đồ án (đúng sơ đồ); nếu cần đảm bảo cho `PaymentConfirmed`/`OrderCompleted` thì chuyển sang Redis Streams + consumer group (chỉ cần thay `RedisEventBus*`, `IEventBus` giữ nguyên) và thêm Outbox pattern.
2. **Kitchen real-time (WebSocket)**: YARP proxy WebSocket được; khi làm FE-09 chỉ cần thêm SignalR vào KitchenOperations.
3. **Mật khẩu/khoá JWT là dev-only**; production cần secret riêng cho từng service và rotate key.
4. **CI (GitHub Actions)** chưa được thêm vì chưa rõ repo root là `Backend/` hay thư mục `Project/`. Lệnh cần chạy: `dotnet build -p:TreatWarningsAsErrors=true` và `dotnet test`, và `pytest` trong `src/Services/Ai`.
5. Khi Identity service được code: đăng ký `JwtTokenService` đã có sẵn trong DI để phát token; các service khác không cần sửa gì.
