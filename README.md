# AICOS Backend

> Current database contract: one operational database `aicos_db`, schema
> `public`, explicit reviewed migrations, and `MigrateOnStartup=false`. Runtime
> credentials come from environment variables. See
> [`docs/BACKEND_DEPLOYMENT_CONFIGURATION.md`](docs/BACKEND_DEPLOYMENT_CONFIGURATION.md).

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
Identity hiện có `POST /api/v1/identity/auth/login`; xem request/response tại
[`docs/API_DOCUMENTATION.md`](docs/API_DOCUMENTATION.md).
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
│     ├─ InventoryPurchasing/   KitchenOperations/  WorkforceScheduling/
│     ├─ ReportingAnalytics/    CustomerEngagement/        (mỗi thư mục = 1 project AICOS.<Tên>.Api)
│     └─ Ai/                    FastAPI (RAG chatbot, face-scan, forecast, summary) – hiện là skeleton 501
├─ tests/                         BuildingBlocks.Tests (unit) + Services.SmokeTests (boot 10 service + gateway)
├─ infra/postgres/init/           Tạo runtime role; không tự chạy migration hoặc GRANT
├─ docker/Dockerfile.dotnet       1 Dockerfile dùng chung cho gateway và 10 service .NET
└─ docker-compose.yml
```

| Service (key)              | Project                 | Route qua gateway    | Schema DB           | Port dev |
|----------------------------|-------------------------|----------------------|---------------------|----------|
| Gateway                    | AICOS.Gateway           | `/`                  | –                   | 5000 (Docker: 8080) |
| Identity & Organization    | IdentityOrganization    | `/api/v1/identity`   | `public`            | 5101 |
| Menu & Catalog             | MenuCatalog             | `/api/v1/menu`       | `public`            | 5102 |
| Table & Reservation        | TableReservation        | `/api/v1/tables`     | `public`            | 5103 |
| Order                      | Ordering                | `/api/v1/orders`     | `public`            | 5104 |
| Payment                    | Payments                | `/api/v1/payments`   | `public`            | 5105 |
| Inventory & Purchasing     | InventoryPurchasing     | `/api/v1/inventory`  | `public`            | 5106 |
| Kitchen Operations         | KitchenOperations       | `/api/v1/kitchen`    | `public`            | 5107 |
| Workforce & Scheduling     | WorkforceScheduling     | `/api/v1/workforce`  | `public`            | 5108 |
| Reporting & Analytics      | ReportingAnalytics      | `/api/v1/reporting`  | `public`            | 5109 |
| Customer Engagement        | CustomerEngagement      | `/api/v1/customers`  | `public`            | 5110 |
| AI (Python)                | Ai                      | `/api/v1/ai`         | `public`            | 5111 |

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
| Persistence | `AicosDbContext` dùng `aicos_db.public`, naming snake_case, bảng `__ef_migrations_history`, `AuditableEntity` tự điền CreatedAt/By, UpdatedAt/By, `MigrateDatabaseAsync` mặc định tắt |
| Messaging | `IEventBus.PublishAsync`, `AddIntegrationEventHandler<TEvent, THandler>()` trên Redis Pub/Sub |
| Http | Lỗi trả về `ProblemDetails` (RFC 7807) – ném `NotFoundException`, `BusinessRuleException("BR-06", ...)`, `ValidationFailedException`...; `X-Correlation-Id` xuyên suốt; `AddAicosServiceClient<>` gọi sync service khác (retry/circuit-breaker/timeout + chuyển tiếp token) |

## Việc thường làm

**Thêm entity + migration** (ví dụ Ordering):
```powershell
# thêm DbSet vào OrderingDbContext, rồi:
dotnet ef migrations add InitialCreate --project src/Services/Ordering/AICOS.Ordering.Api --output-dir Persistence/Migrations
# Review SQL rồi chạy bằng migration principal; MigrateOnStartup mặc định là false.
```

**Thêm endpoint**: tạo `Features/Orders/OrderEndpoints.cs` với `public static void MapOrderEndpoints(this RouteGroupBuilder api)`, rồi mở dòng comment `api.Map...` trong `Program.cs`. Dùng `.RequireAuthorization(AicosPolicies.Staff)` cho route cần quyền; route không gắn gì là public (khách quét QR).

**Publish / consume event**
```csharp
await eventBus.PublishAsync(new OrderCreatedEvent(...), ct);                         // ở Ordering
builder.Services.AddIntegrationEventHandler<OrderCreatedEvent, OrderCreatedHandler>(); // ở Kitchen
```
Event mới thêm vào `AICOS.Contracts` (đặt tên thì quá khứ: `XxxCreatedEvent`).

**Gọi sync service khác** (vd. Order → Inventory, BR-06 < 500 ms):
```csharp
builder.Services.AddAicosServiceClient<IInventoryClient, InventoryClient>(builder.Configuration, "Inventory",
    o => o.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(500));
```

**Thêm route mới ở gateway**: sửa `ReverseProxy` trong `src/Gateway/AICOS.Gateway/appsettings.json` (và biến môi trường cluster trong `docker-compose.yml`).

## Quy ước kiến trúc (đã chốt)

- **Mỗi service chỉ đụng nhóm bảng của mình trong `aicos_db.public`.** PostgreSQL role/table grant giới hạn quyền; dữ liệu service khác đi qua REST hoặc event.
- **Xác thực ở 2 lớp**: gateway từ chối token sai/hết hạn ngay tại cổng; token vắng mặt vẫn đi tiếp (guest, login, menu công khai). **Phân quyền do từng service quyết định** bằng policy/role.
- **Sync = REST trực tiếp giữa các service (không qua gateway); async = event trên Redis.**
- Identity đã có module đăng nhập Staff theo chi nhánh. Các service nghiệp vụ còn
  lại vẫn chỉ có endpoint `_info`.

## Lưu ý / việc cần quyết định tiếp

1. **Redis Pub/Sub là at-most-once**: consumer đang tắt thì mất event, và nếu chạy nhiều instance của cùng service thì mỗi instance nhận 1 bản. Đủ cho demo đồ án (đúng sơ đồ); nếu cần đảm bảo cho `PaymentConfirmed`/`OrderCompleted` thì chuyển sang Redis Streams + consumer group (chỉ cần thay `RedisEventBus*`, `IEventBus` giữ nguyên) và thêm Outbox pattern.
2. **Kitchen real-time (WebSocket)**: YARP proxy WebSocket được; khi làm FE-09 chỉ cần thêm SignalR vào KitchenOperations.
3. **Không lưu mật khẩu/khoá JWT trong Git**; dùng secret riêng cho từng service và quy trình rotate được phê duyệt.
4. **CI (GitHub Actions)** chưa được thêm vì chưa rõ repo root là `Backend/` hay thư mục `Project/`. Lệnh cần chạy: `dotnet build -p:TreatWarningsAsErrors=true` và `dotnet test`, và `pytest` trong `src/Services/Ai`.
5. Khi Identity service được code: đăng ký `JwtTokenService` đã có sẵn trong DI để phát token; các service khác không cần sửa gì.
