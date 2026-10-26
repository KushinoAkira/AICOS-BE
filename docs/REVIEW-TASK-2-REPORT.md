# Báo cáo Review Task 2: Backend AICOS

Phạm vi: nền tảng SaaS đa tenant cho chuỗi cà phê. Backend gồm ASP.NET Core .NET 8 (9 service nghiệp vụ và 1 API Gateway), PostgreSQL, Redis, SignalR và 1 service AI viết bằng Python FastAPI.

Nguyên tắc của báo cáo: chỉ nêu số liệu đã đo trên mã nguồn và lần chạy Docker thật. Những gì chưa làm được nêu rõ ở mục "Chưa hoàn thành".

---

## 2.1 Cơ sở dữ liệu

### 2.1.1 PostgreSQL trên Docker và tự động migration

- Image `pgvector/pg16`, chạy trong `docker-compose.yml`. Cổng DB chỉ bind `127.0.0.1`.
- Mô hình **một schema cho mỗi service**: `identity, menu, table_reservation, ordering, payment, kitchen, workforce, reporting, customer, ai`.
- Mỗi service có một role DB riêng, chỉ được truy cập schema của mình. Script tạo schema và role là `infra/postgres/init/01-schemas-and-roles.sh`, chạy khi container khởi tạo lần đầu.
- Mật khẩu role lấy từ biến môi trường, không có trong mã nguồn.
- **Tự động migration:** `Database__MigrateOnStartup=true` trong compose. Lúc khởi động, mỗi service gọi `MigrateDatabaseAsync` và áp dụng các EF Core migration còn thiếu.
- Số migration hiện có: Identity 6, Customer 5, Payments 4, Workforce 4, Menu 3, Table 3, Ordering 3, Kitchen 1, Reporting 1 (tổng 30).
- Đã kiểm chứng: dựng Docker từ trạng thái trống, các service tự tạo bảng, `scripts/smoke-docker.sh` chạy khoảng 50 kiểm tra và đều đạt.

**Số liệu schema** (từ `docs/database/schema.sql`, xuất bằng `pg_dump` qua `scripts/dump-schema.sh`):

| Chỉ số | Giá trị |
|---|---|
| Bảng | 51 |
| Index | 92 |
| CHECK constraint | 25 |
| Foreign key | 21 |

Foreign key chỉ tồn tại **bên trong một service**. Đây là chủ đích của kiến trúc microservice: các service liên kết với nhau qua id và sự kiện, không qua FK chéo schema.

### 2.1.2 Ma trận truy vết miền nghiệp vụ sang bảng

| Miền | Service | Bảng chính |
|---|---|---|
| Tenant / tổ chức | Identity | `tenants`, `branches`, `users`, `user_sessions`, `otp_codes`, `audit_logs`, `subscription_plans`, `tenant_subscriptions`, `subscription_payments` |
| Menu | MenuCatalog | categories, products, option groups và options, `branch_products` (giá, hết hàng theo chi nhánh) |
| Bàn / đặt bàn | TableReservation | `table_areas`, `dining_tables` (QR token) |
| Đặt hàng | Ordering | `orders`, `order_items`, `pickup_slot_settings` |
| Thanh toán | Payments | `payment_providers`, `cancellation_policies`, `merchant_settings`, `payments`, `payment_refunds`, `payment_webhook_events` |
| Bếp / quầy bar | KitchenOperations | `kitchen_tickets`, `kitchen_ticket_items`, `cup_stickers` |
| Nhân sự | WorkforceScheduling | `employees`, `work_shifts`, `employee_shifts`, `replacement_*`, `pay_rules`, `payroll_adjustments`, `cash_closings`, `cash_transactions` |
| Khách hàng / loyalty | CustomerEngagement | `customers`, `loyalty_transactions`, `vouchers`, `loyalty_rules` |
| Báo cáo | ReportingAnalytics | `order_facts` (dữ liệu tổng hợp từ sự kiện) |

Cô lập tenant: `tenant_id` lấy **chỉ từ JWT** (`TenantScope.Require`), client không gửi được. Tenant khác trả 404, chi nhánh khác trả 403 `branch.forbidden`.

Tính toàn vẹn dữ liệu đáng chú ý:

- `audit_logs` append-only, bảo vệ 3 lớp, trong đó có DB trigger.
- Token phiên bản lạc quan (`Version`) trên Customer và Voucher để chống ghi đè đồng thời.
- Khóa secret nhà cung cấp thanh toán mã hóa AES-GCM (`SecretProtector`).

### 2.1.3 Thống kê đã làm và chưa làm

**Đã làm và đã kiểm chứng:**

- Xác thực và phân quyền 7 vai trò (SuperAdmin, BrandAdmin, BranchManager, Barista, Cashier, Customer, Guest), kèm audit log.
- Thuê bao và gói dịch vụ, gồm gia hạn mô phỏng (SCRUM-132).
- Menu, bàn, nhân sự (ca làm, chấm công, thay ca, bảng lương, chốt quỹ).
- Đặt hàng tại quầy (Dine-in, Takeaway), thanh toán tiền mặt và online mô phỏng, hoàn tiền, đối soát.
- Hàng đợi quầy bar theo thời gian thực qua SignalR.
- Báo cáo, điểm thưởng, voucher, đổi điểm sang voucher (SCRUM-133, 134).

**Chưa hoàn thành, nguyên nhân và hướng xử lý:**

| Hạng mục | Hiện trạng | Nguyên nhân | Hướng giải quyết |
|---|---|---|---|
| Guest QR | **Đã hoàn thành (giai đoạn B):** phiên bàn và đơn lưu Postgres có tenant, giỏ hàng dùng chung lưu Redis (Hash), không còn lưu RAM | (đã xử lý) | Khách theo dõi đơn thời gian thực qua SignalR (`/hubs/orders`), thành viên có thông báo lưu trong DB |
| AI Feedback Insights (SCRUM-52) | Chỉ có skeleton, trả 501 | Xem mục 2.3 | Xem mục 2.3 |
| Khách tự đặt hàng online (SCRUM-80) và ép khung giờ nhận hàng | Chưa có | Phụ thuộc Guest/Customer ordering | Làm sau khi Guest QR được tích hợp |
| Gửi email thật, dịch vụ Notification | Chưa có | Ngoài phạm vi các sprint đã chốt | Dựng service thông báo, nhận sự kiện từ Redis |
| Xuất bảng lương PDF | Chưa có (đã có xuất Excel) | Chưa ưu tiên | Thêm renderer PDF |
| Cổng thanh toán thật | Đang mô phỏng (Mock, VietQR tạo ảnh QR công khai, Momo/ZaloPay là link giả) | Cần hợp đồng merchant và sandbox của nhà cung cấp | Giữ giao diện `IPaymentGateway`, thay bằng adapter thật |
| Outbox cho sự kiện | Chưa có, Redis Pub/Sub là at-most-once | Đánh đổi để đơn giản hóa MVP | Thêm bảng outbox và job phát lại |
| SignalR backplane | Chưa có | Mới chạy 1 instance | Thêm Redis backplane trước khi scale ngang |

---

## 2.2 Microservices, Docker và CI/CD

### 2.2.1 Kiến trúc

Mọi truy cập từ client đi qua **API Gateway (YARP) tại `http://localhost:8080`**. Các service nghiệp vụ không bắt buộc phải mở ra ngoài.

| Thành phần | Cổng |
|---|---|
| Gateway | 8080 |
| Identity | 5101 |
| Menu | 5102 |
| Table | 5103 |
| Order | 5104 |
| Payment | 5105 |
| Kitchen | 5107 |
| Workforce | 5108 |
| Reporting | 5109 |
| Customer | 5110 |
| AI (FastAPI) | 5111 |
| PostgreSQL, Redis, pgAdmin | bind `127.0.0.1` |

Giao tiếp giữa service:

- **Đồng bộ:** REST qua `AddAicosServiceClient`, có chuyển tiếp bearer token và resilience handler (retry, timeout, circuit breaker).
- **Bất đồng bộ:** sự kiện tích hợp qua Redis Pub/Sub, như `OrderCreated`, `PaymentSucceeded`, `OrderCancelled`, `KitchenOrderReady`, `AuditRecorded`.

Gateway có giới hạn tần suất: đăng nhập 10 lần/phút, chat 20 lần/phút, chung 600 lần/phút. Vượt ngưỡng trả 429 kèm `Retry-After`.

### 2.2.2 Docker và quản lý secret

- Mỗi service có Dockerfile riêng. `docker-compose.yml` dựng toàn bộ hệ thống.
- Biến bắt buộc khai báo dạng `${VAR:?...}`, thiếu biến thì compose từ chối chạy. Không có secret ghi cứng trong compose hay `appsettings`.
- Secret nằm trong **`.env`, đã được `.gitignore` loại khỏi repo**. File mẫu là `.env.example`.
- Script `scripts/init-env.ps1` và `scripts/init-env.sh` sinh `.env` với giá trị ngẫu nhiên.
- Môi trường dev dùng `dotnet user-secrets` (id `aicos-be`).
- Mặc định `ASPNETCORE_ENVIRONMENT=Production`, Swagger tắt (bật bằng `SWAGGER_ENABLED`). Redis có `requirepass`.
- Cảnh báo cần nêu trung thực: **lịch sử git có chứa một số secret dev cũ**. Cần xoay (rotate) các giá trị đó, và có thể làm sạch lịch sử bằng `git filter-repo` nếu hội đồng yêu cầu.

### 2.2.3 Kiểm thử (số liệu thật)

**Tổng cộng 627 test, 0 lỗi** (lần chạy gần nhất trước commit `15f7fd8`).

| Dự án test | Số test |
|---|---|
| BuildingBlocks.Tests | 15 |
| IdentityOrganization.Tests | 169 |
| Services.IntegrationTests | 397 |
| Services.SmokeTests | 46 |
| **Tổng** | **627** |

Tích hợp dùng `ServiceFactory<TService,TDbContext>` với InMemory DB, `RecordingEventBus`, `TestClock` và fake client. Bộ test cũng chạy trong smoke test khởi động service và gateway. Ngoài ra, `scripts/smoke-docker.sh` chạy kịch bản đầu cuối trên Docker thật (Production mode): menu, đơn hàng, thu tiền mặt, bếp, hoàn tất đơn, thanh toán online, hoàn tiền ngoại lệ, đối soát, báo cáo, SignalR, thuê bao, điểm và voucher.

### 2.2.4 CI/CD: GitHub Actions

Đã tích hợp GitHub Actions CI tự động tại `.github/workflows/ci.yml`:

- **Kích hoạt:** `push` và `pull_request` lên nhánh `main` hoặc `develop`. Có `concurrency` để hủy lần chạy cũ khi đẩy commit mới.
- **Môi trường:** runner `ubuntu-latest`, cài .NET 8 SDK (`actions/setup-dotnet@v4`), kèm service container PostgreSQL (`pgvector/pgvector:pg16`) và Redis 7, có health check.
- **Các bước:** `dotnet restore`, `dotnet build --configuration Release --no-restore`, `dotnet test --no-build --configuration Release` (627 test), rồi tải file kết quả `.trx` lên artifact.
- Secret của test tự cấp trong `TestEnvironment.cs` nên pipeline không cần secret của repo.

Lưu ý trung thực khi trình bày:

- Workflow mới được thêm vào repo, **chưa có lần chạy nào trên GitHub** tại thời điểm viết báo cáo. Cần đẩy lên `main`/`develop` (hoặc mở PR vào đó) và chụp màn hình lần chạy xanh làm bằng chứng.
- Bộ test hiện dùng InMemory DB nên Postgres và Redis trong CI chưa được test dùng tới; chúng có sẵn để mở rộng integration test trên DB thật.
- Chưa có bước lint (`dotnet format`), quét secret và CodeQL; đây là phần bổ sung tiếp theo.

---

## 2.3 Model AI (SCRUM-52, AI Feedback Insights)

### Đánh giá thẳng thắn

- Có service AI dạng skeleton (Python FastAPI, cổng 5111, schema `ai` trong DB, image dựng được). Các router `chat`, `forecast`, `summary`, `attendance` đều trả **501 Not implemented**.
- **Chưa có model nào**: không huấn luyện, không self-host, không gọi API ngoài. Tính năng phân tích phản hồi khách hàng **chưa hoạt động**.
- Phía .NET đã có sẵn những gì AI sẽ cần: dữ liệu đơn hàng đã chuẩn hóa (`order_facts`), sự kiện `OrderCompleted`, và gateway có route cùng giới hạn tốc độ riêng cho `/chat`.

### Nguyên nhân

1. Thứ tự ưu tiên: luồng tiền (đơn, thanh toán, quỹ, lương) có tính bắt buộc cao hơn nên được làm trước.
2. Chưa có tập phản hồi khách thật để đánh giá, vì kênh khách chưa chạy (Guest QR và đặt online chưa tích hợp).
3. Self-host model cần GPU hoặc chi phí hạ tầng, ngoài ngân sách đồ án.

### Cách bảo vệ trước hội đồng

- Thừa nhận đây là hạng mục chưa hoàn thành, và trình bày nó là **giai đoạn kế tiếp có điều kiện đầu vào đã sẵn sàng**.
- Chỉ ra phần nền đã làm: dữ liệu `order_facts`, hợp đồng service, route gateway, rate limit.
- Đưa lộ trình: giai đoạn 1 gọi LLM qua API (không self-host) để tóm tắt và phân loại cảm xúc phản hồi, lưu kết quả vào schema `ai`; giai đoạn 2 mới cân nhắc self-host nếu có yêu cầu về dữ liệu.
- Không nên trình bày skeleton là "đã có AI".

---

## 2.4 Thực nghiệm và failure cases

### Kịch bản kiểm thử

Happy path (đầu cuối, chạy trên Docker thật bằng `scripts/smoke-docker.sh`):

1. Đăng nhập Super Admin, tạo tenant, chi nhánh, tài khoản nhân viên.
2. Tạo menu, thu ngân tạo đơn, thu tiền mặt, đơn thành `Paid`, vào hàng đợi quầy bar, `Ready`, `Completed`.
3. Thanh toán online mô phỏng (Mock), webhook cập nhật trạng thái.
4. Điểm thưởng được cộng sau thanh toán, đổi điểm sang voucher, dùng voucher ở đơn kế tiếp.
5. Báo cáo, đối soát, SignalR negotiate, xem và gia hạn thuê bao.

Unhappy path (nằm trong 627 test): sai quyền (403), thiếu token (401), tenant khác (404), chi nhánh khác (403), khóa tài khoản sau 5 lần sai mật khẩu (`account.temporarily_locked`), voucher sai chủ, sai chi nhánh, hết hạn, đã dùng, chưa đủ đơn tối thiểu, đổi điểm vượt số dư, gia hạn khi chưa đến hạn (`subscription.renewal_not_open`), hủy đơn đã trả (`order.paid`), rate limit 429.

### Các failure case đã gặp và xử lý thật

| Tình huống | Hiện tượng | Cách xử lý | Bằng chứng |
|---|---|---|---|
| **Đồng bộ sự kiện qua Redis Pub/Sub** | Pub/Sub là at-most-once: sự kiện gửi lại hoặc mất thì trạng thái lệch | Handler **idempotent**: mỗi đơn chỉ cộng điểm một lần, webhook trùng `eventId` bị bỏ qua. Có API truy vấn trạng thái thanh toán để bù khi mất webhook, và job hết hạn thanh toán chạy mỗi 60 giây. | Test "sự kiện thanh toán gửi lại thì điểm chỉ cộng một lần" |
| **Dùng voucher hai lần** | Hai đơn cùng lúc dùng một voucher | Voucher chuyển `Redeemed` ngay khi tạo đơn, kèm token `Version` lạc quan. Khi tranh chấp, một bên nhận 409 hoặc `voucher.already_redeemed`. Hủy đơn chưa trả thì voucher được trả lại `Active`. | Test voucher, và chỉ dùng được bởi chủ điểm |
| **Đổi điểm hai lần song song** | Tiêu điểm hai lần | Lần hai nhận `loyalty.insufficient_points`; nếu song song thì một bên 409 | Test đổi điểm |
| **Ngoại lệ hoàn tiền trên đơn** | Hoàn tiền xong mà đơn chưa lưu trạng thái `Refunded` (race giữa hoàn tiền và lưu đơn) | Tải lại đơn, đặt `Refunded` khi `RefundedAmount > 0`, nới điều kiện chặn; thêm test hồi quy | Commit sửa và test hồi quy |
| **Đơn đã trả bị hủy ngoại lệ** | Điểm đã cộng vẫn còn | Sự kiện `OrderCancelled` kích hoạt dòng `Reverse` (âm), chỉ trừ một lần, không xuống dưới 0 | Test điểm |
| **Webhook giả mạo** | Bên thứ ba gọi webhook | Kiểm HMAC-SHA256 trên `eventId\|transactionRef\|status\|amount` | Test thanh toán |
| **Đơn id giả gửi sang Payment** | Thanh toán cho đơn không có thật | Payment gọi Ordering xác minh đơn trước khi thu tiền | Smoke script phải sửa lại vì lỗi này |

### Hạn chế thực nghiệm cần nêu

- Test tích hợp dùng InMemory DB, nên chưa phản ánh hết hành vi khóa đồng thời của PostgreSQL. Smoke Docker chạy trên Postgres thật nhưng chỉ ở mức luồng chính.
- Chưa có kiểm thử tải (load test) và chưa đo hiệu năng.
- Chưa có CI nên chưa có bằng chứng "mỗi PR đều xanh".

---

## Danh sách việc cần làm tiếp (theo mức ưu tiên)

1. Chạy workflow CI lần đầu trên GitHub; bổ sung lint, quét secret, CodeQL. Xoay các secret dev cũ trong lịch sử git.
2. Tích hợp Guest QR vào Ordering (có tenant, lưu DB).
3. AI Feedback Insights ở dạng gọi API ngoài, lưu kết quả vào schema `ai`.
4. Outbox cho sự kiện, Redis backplane cho SignalR.
5. Cổng thanh toán thật, gửi email, xuất lương PDF.
