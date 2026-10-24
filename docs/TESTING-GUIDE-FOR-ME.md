# TESTING-GUIDE-FOR-ME: tự test hệ thống AICOS-BE trên Swagger

Tài liệu cho người phát triển / kiểm thử. Mọi mã SCRUM, đường dẫn file và mã lỗi dưới đây lấy từ source ở commit mới nhất của nhánh `feature/multi-tenant-roles`. Các luồng chính đã được chạy thật bằng `scripts/smoke-docker.sh` (43 kiểm tra) và 494 test tự động.

---

## 1. Khởi động hệ thống

### 1.1 Cách khuyên dùng: cả hệ thống trong Docker (đã kiểm chứng)

```powershell
# Lần đầu: sinh .env với mật khẩu/khóa ngẫu nhiên (không có secret nào nằm trong repo)
powershell -ExecutionPolicy Bypass -File scripts/init-env.ps1
```

Mở `.env` và đặt các dòng sau để test bằng Swagger:

```
ASPNETCORE_ENVIRONMENT=Development
SWAGGER_ENABLED=true
PAYMENTS_ALLOW_SIMULATION=true
```

```powershell
docker compose up -d --build          # Postgres, Redis, Gateway và 10 service; migration tự chạy
docker compose ps                     # chờ tất cả "running/healthy"
```

Tài khoản Super Admin đầu tiên: email `SEED_ADMIN_EMAIL`, mật khẩu `SEED_ADMIN_PASSWORD` trong `.env` (đừng đưa vào chat/commit).

| Thành phần | Địa chỉ |
|---|---|
| Gateway (FE gọi vào đây) | `http://localhost:8080` |
| Identity | `http://localhost:5101/swagger` |
| Menu | `http://localhost:5102/swagger` |
| Table | `http://localhost:5103/swagger` |
| Order | `http://localhost:5104/swagger` |
| Payment | `http://localhost:5105/swagger` |
| Kitchen | `http://localhost:5107/swagger` |
| Workforce | `http://localhost:5108/swagger` |
| Reporting | `http://localhost:5109/swagger` |
| Customer | `http://localhost:5110/swagger` |
| pgAdmin (tùy chọn) | `docker compose --profile tools up -d` rồi `http://localhost:5050` |

Test nhanh toàn bộ luồng bằng một lệnh: `sh scripts/smoke-docker.sh` (đọc mật khẩu từ `.env`, không in ra).

Dừng và xóa dữ liệu: `docker compose down -v`.

> Ghi chú: nếu bạn đang chạy sẵn container `aicos-postgres`/`aicos-redis` cũ, `docker compose up` sẽ báo trùng tên/cổng. Tắt chúng trước (`docker rm -f aicos-postgres aicos-redis`) hoặc chạy ở project khác.

### 1.2 Chạy từ Visual Studio / `dotnet run` (nhiều service cùng lúc)

1. Chỉ bật hạ tầng: `docker compose up -d postgres redis`.
2. Đặt secret một lần cho mọi service (kho user-secrets dùng chung `aicos-be`):

```powershell
dotnet user-secrets set "Database:Password" "<SERVICE_DB_PASSWORD trong .env>" --id aicos-be
dotnet user-secrets set "Jwt:SigningKey" "<JWT_SIGNING_KEY trong .env>" --id aicos-be
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379,password=<REDIS_PASSWORD>" --id aicos-be
dotnet user-secrets set "Payments:SecretKey" "<PAYMENTS_SECRET_KEY>" --id aicos-be
dotnet user-secrets set "Auth:SeedAdmin:Password" "<SEED_ADMIN_PASSWORD>" --id aicos-be
```

3. Visual Studio: chuột phải Solution, **Configure Startup Projects**, **Multiple startup projects**, chọn *Start* cho: `AICOS.Gateway`, `AICOS.IdentityOrganization.Api`, `AICOS.MenuCatalog.Api`, `AICOS.TableReservation.Api`, `AICOS.Ordering.Api`, `AICOS.Payments.Api`, `AICOS.KitchenOperations.Api`, `AICOS.WorkforceScheduling.Api`, `AICOS.ReportingAnalytics.Api`, `AICOS.CustomerEngagement.Api`. Nhấn F5.
4. Cổng khi chạy local: Gateway `5000`, Identity `5101`, Menu `5102`, Table `5103`, Order `5104`, Payment `5105`, Kitchen `5107`, Workforce `5108`, Reporting `5109`, Customer `5110`. Swagger ở `/swagger` của từng service. Lần đầu cần `Database__MigrateOnStartup=true` (đã bật sẵn trong `appsettings.Development.json`? Nếu không thấy bảng, đặt biến này) để tạo bảng và tạo Super Admin.
5. Mật khẩu role DB phải khớp với volume Postgres đã tạo (đổi `SERVICE_DB_PASSWORD` sau khi tạo volume thì phải `docker compose down -v`).

### 1.3 Đăng nhập trong Swagger

Mỗi service có nút **Authorize**: dán `Bearer <accessToken>`  (hoặc chỉ dán token, tùy Swagger). Lấy token bằng `POST /api/v1/identity/auth/login` (Identity). Token hết hạn thì dùng `POST /auth/refresh`.

Quy ước kết quả lỗi (mọi service):

```json
{ "type": "...", "title": "Business rule violated", "status": 422, "detail": "…", "errorCode": "payment.amount_mismatch", "correlationId": "…",
  "errors": { "Items[0].Quantity": ["Quantity must be between 1 and 99."] } }
```
`errors` chỉ có khi lỗi validation (400).

Mã thường gặp: `401` chưa đăng nhập/token sai; `403` sai vai trò, sai chi nhánh (`branch.forbidden`) hoặc SuperAdmin gọi API của brand (`tenant.required`); `404` không thấy hoặc thuộc brand khác; `409` trùng/đã làm; `422` vi phạm quy tắc nghiệp vụ; `429` quá giới hạn gọi.

---

## 2. Bộ tài khoản dùng cho mọi kịch bản (làm một lần)

Thứ tự gọi (Identity, qua `http://localhost:5101/swagger` hoặc gateway `/api/v1/identity/...`). Mã: SCRUM-38, 103, 109, 110, 111, 42. Code: `Features/Auth/AuthEndpoints.cs`, `Features/Tenants/`, `Features/Subscriptions/`, `Features/Branches/`, `Features/Users/` trong `src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api`.

**Bước 1. Super Admin đăng nhập** `POST /api/v1/identity/auth/login`
```json
{ "login": "admin@aicos.local", "password": "<SEED_ADMIN_PASSWORD>" }
```
Lấy `accessToken` => dùng cho các bước 2 đến 4.

**Bước 2. Tạo gói thuê bao** `POST /api/v1/identity/subscription-plans` (SuperAdmin) [SCRUM-110]
```json
{ "name": "Basic", "tier": "Basic", "fee": 500000, "billingPeriod": "Monthly", "maxBranches": 3, "maxStaffAccounts": 20, "description": "Gói cơ bản" }
```
`billingPeriod`: `Monthly | Quarterly | Yearly`. Lấy `id` = `PLAN_ID`.

**Bước 3. Tạo tenant + Brand Admin đầu tiên** `POST /api/v1/identity/tenants` [SCRUM-109, 38]
```json
{ "name": "Cà phê Demo", "code": "demo-coffee",
  "firstAdmin": { "fullName": "Chủ Quán", "email": "owner@demo.com", "phone": "0900000001", "password": "Brand@12345" },
  "planId": "<PLAN_ID>" }
```

**Bước 4. Ghi nhận thanh toán gói** `POST /api/v1/identity/tenants/{tenantId}/subscription/payments` [SCRUM-111]
```json
{ "amount": 500000, "method": "Transfer", "reference": "CK-001", "paidAt": "2026-11-01T08:00:00Z" }
```

**Bước 5. Brand Admin đăng nhập** (`owner@demo.com` / `Brand@12345`) => `BRAND` token.

**Bước 6. Tạo chi nhánh** `POST /api/v1/identity/branches` (BrandAdmin) [SCRUM-42]
```json
{ "name": "Quận 1", "address": "1 Lê Lợi", "phone": "0900000002", "openingTime": "07:00:00", "closingTime": "22:00:00" }
```
=> `BRANCH_ID`.

**Bước 7. Tạo nhân viên** `POST /api/v1/identity/users` (BrandAdmin tạo mọi vai trò; BranchManager tạo Barista/Cashier của chi nhánh mình)
```json
{ "fullName": "Thu Ngân", "email": "cashier@demo.com", "username": "cashier1", "phone": "0900000003", "password": "Brand@12345", "role": "Cashier", "branchId": "<BRANCH_ID>" }
```
Lặp lại với `role`: `Barista` (`barista@demo.com`), `BranchManager` (`manager@demo.com`). Đăng nhập từng người để có token `CASHIER`, `BARISTA`, `MANAGER`.

Khách hàng tự đăng ký: `POST /api/v1/identity/auth/register`
```json
{ "tenantCode": "demo-coffee", "fullName": "Khách A", "email": "a@x.com", "phone": "0900000009", "password": "Customer@123", "confirmPassword": "Customer@123" }
```

### Unhappy cases của phần này

| Gọi | Kết quả mong đợi | Quy tắc |
|---|---|---|
| Tạo tenant trùng `code` | 409 `tenant.duplicate_code` | Mã tenant duy nhất |
| Tạo chi nhánh thứ 4 khi gói `maxBranches=3` | 422 `subscription.limit_reached` | BR-11 giới hạn gói |
| Tạo tài khoản trùng email/username | 409 `user.duplicate_email` / `user.duplicate_username` | |
| Đăng nhập sai 5 lần liên tiếp | lần thứ 5 và các lần sau: 403 `account.temporarily_locked`, kể cả nhập đúng | SCRUM-104 khóa tạm |
| Đăng nhập sai mật khẩu | 401 `auth.invalid_credentials` | |
| BranchManager tạo BrandAdmin | 403 `user.role_not_allowed` | Phân quyền |
| Ghi nhận thanh toán vượt số tiền kỳ | 422 `subscription.overpayment` | |
| SuperAdmin gọi API của brand (vd. `GET /api/v1/orders`) | 403 `tenant.required` | SuperAdmin không thuộc brand |
| Gọi API với token brand A lên dữ liệu brand B | 404 | Cách ly tenant (SCRUM-94) |

**Rate limit (SCRUM-143):** bấm `POST /api/v1/identity/auth/login` (hoặc `forgot-password`, `reset-password`, `register`) hơn 10 lần trong 1 phút **qua Gateway** (`:8080`) => từ lần 11 trả `429` kèm header `Retry-After: 60`. Gọi thẳng vào `:5101` không bị giới hạn này (giới hạn nằm ở Gateway: `src/Gateway/AICOS.Gateway/Program.cs`, `RateLimitGroups.cs`).

**Quên mật khẩu (SCRUM-105):** `POST /auth/forgot-password` `{ "email": "cashier@demo.com" }`, mã 6 số hiện trong log của Identity (`docker compose logs identity`) khi chạy Development; ở Production chỉ ghi email đã che. Rồi `POST /auth/reset-password` `{ "email": "...", "code": "123456", "newPassword": "New@12345", "confirmNewPassword": "New@12345" }`. Mã sai: 422 `auth.reset_code_invalid`; quá số lần thử bị khóa tạm.

---

## 3. Cấu hình của Brand Admin (làm sau bộ tài khoản)

Dùng token `BRAND` (trừ khi ghi khác).

### 3.1 Menu [SCRUM-43, 44, 113]: Service `AICOS.MenuCatalog.Api`, code `Features/Categories`, `Features/Products`, `Features/Branches/BranchMenuEndpoints.cs`, `Features/PublicMenu/`

Thứ tự: danh mục => món => đăng món lên chi nhánh => đánh dấu hết hàng.

1. `POST /api/v1/menu/categories` => `{ "name": "Cà phê", "sortOrder": 1 }`
2. `POST /api/v1/menu/products`
```json
{ "categoryId": "<CATEGORY_ID>", "name": "Latte", "description": "Cà phê sữa", "basePrice": 55000, "tags": ["coffee"],
  "optionGroups": [
    { "name": "Size", "kind": "Size", "selectionType": "Single", "isRequired": true,
      "options": [ { "name": "S", "priceDelta": 0, "isDefault": true }, { "name": "M", "priceDelta": 5000 } ] } ] }
```
3. Đăng lên chi nhánh (kèm giá riêng, tùy chọn): `PUT /api/v1/menu/products/{productId}/branches/{branchId}` `{ "isPublished": true, "priceOverride": 59000 }`. Đăng hàng loạt: `POST /api/v1/menu/publications` `{ "branchIds": ["<BRANCH_ID>"], "productIds": ["<PRODUCT_ID>"], "publish": true }`.
4. Hết hàng (Barista/Manager/Admin): `PATCH /api/v1/menu/branches/{branchId}/products/{productId}/availability` `{ "isAvailable": false }`.
5. Xem menu khách thấy (không cần token): `GET /api/v1/menu/public/branches/{branchId}/menu`. Mỗi thay đổi tăng version cache Redis của chi nhánh ngay (khóa `t:{tenantId}:menu:…`).

| Unhappy | Kết quả |
|---|---|
| `basePrice` âm hoặc 0 | 400 |
| Tên danh mục trùng | 409 `category.duplicate_name` |
| Barista tạo món | 403 |
| Món đã đánh dấu hết hàng | không còn trong menu công khai; tạo đơn chứa món đó => 422 `order.item_unavailable` |

### 3.2 Sơ đồ bàn và QR [SCRUM-45]: Service `AICOS.TableReservation.Api`, `Features/Areas`, `Features/DiningTables`

1. `POST /api/v1/tables/areas` `{ "branchId": "<BRANCH_ID>", "name": "Tầng 1", "sortOrder": 1 }`
2. `POST /api/v1/tables/dining-tables` `{ "branchId": "<BRANCH_ID>", "areaId": "<AREA_ID>", "tableCode": "T1", "capacity": 4, "positionX": 10, "positionY": 20 }`
3. Ảnh QR: `GET /api/v1/tables/dining-tables/{id}/qr.svg`; đổi token: `POST …/{id}/qr/regenerate`; sơ đồ: `GET /api/v1/tables/floor-plan?branchId=…`; tra QR công khai: `GET /api/v1/tables/public/qr/{token}`.
- Unhappy: trùng `tableCode` trong chi nhánh => 409; `capacity` 0 => 400; token QR cũ sau khi regenerate => 404.

### 3.3 Điểm thưởng và voucher [SCRUM-47]: Service `AICOS.CustomerEngagement.Api`, `Features/Loyalty`, `Features/Vouchers`

- `PUT /api/v1/customers/loyalty/rules` (BrandAdmin)
```json
{ "isActive": true, "amountPerPoint": 10000, "redeemValuePerPoint": 1000, "minPointsToRedeem": 10, "silverThreshold": 100, "goldThreshold": 500 }
```
  Unhappy: `goldThreshold` <= `silverThreshold` => 400.
- `POST /api/v1/customers/vouchers`
```json
{ "branchId": null, "code": "SAVE10", "type": "Voucher", "discountType": "Percentage", "value": 10, "minOrderAmount": 50000, "expiresAt": "2027-01-01T00:00:00Z", "issuedToName": "Khách A" }
```
  `discountType`: `Percentage | FixedAmount`. Unhappy: `%` > 100 => 400; hạn trong quá khứ => 400; trùng `code` => 409. Kiểm tra: `POST /vouchers/validate` `{ "code": "SAVE10", "orderAmount": 100000 }`. Thu hồi: `POST /vouchers/{id}/revoke` `{ "reason": "…" }`.

### 3.4 Thanh toán [SCRUM-39, 112, 48]: Service `AICOS.Payments.Api`, `Features/Providers`, `Merchants`, `Policies`

Thứ tự: Super Admin bật cổng (toàn cục) => Brand Admin cấu hình tài khoản nhận tiền => Brand Admin đặt chính sách hoàn tiền.

1. (SuperAdmin) `POST /api/v1/payments/providers` `{ "code": "Mock", "displayName": "Mock", "mode": "Mock" }` rồi `PATCH /api/v1/payments/providers/{id}/status` `{ "isEnabled": true }`. (Mock tự được tạo khi migrate lần đầu nếu đã có.)
   VietQR thật: `{ "code": "VietQR", "displayName": "VietQR", "mode": "Sandbox", "merchantId": "P1", "apiKey": "ak_1234567890", "secretKey": "sk_1234567890" }` (khóa không bao giờ được trả về).
2. (BrandAdmin) `PUT /api/v1/payments/merchant-settings` `{ "providerCode": "VietQR", "isEnabled": true, "accountName": "CONG TY DEMO", "bankCode": "vcb", "accountNumber": "0123456789012" }`; ghi đè 1 chi nhánh thêm `"branchId": "<BRANCH_ID>"`. Xem phương thức chi nhánh nhận: `GET /merchant-settings/available`.
3. (BrandAdmin) `PUT /api/v1/payments/policies/cancellation`
```json
{ "cancelRefundPercent": 100, "noShowAfterMinutes": 30, "noShowRefundPercent": 0, "unpaidOrderTimeoutMinutes": 15, "autoApproveWithinPolicy": true, "exceptionApprover": "BranchManager" }
```
| Unhappy | Kết quả |
|---|---|
| Bật VietQR khi Super Admin chưa bật | 422 `merchant.provider_unavailable` / `merchant.provider_disabled` |
| Cổng thật không có số tài khoản/mã merchant | 422 `merchant.details_missing` |
| `cancelRefundPercent` 101 | 400 |
| Manager sửa chính sách | 403 |

### 3.5 Pickup slot [SCRUM-114]: Service `AICOS.Ordering.Api`, `Features/PickupSlots`
`PUT /api/v1/orders/pickup-slots` `{ "branchId": null, "slotMinutes": 15, "capacityPerSlot": 8, "cutoffMinutes": 20, "advanceDays": 2 }` (branchId để trống = mặc định cả brand; điền = ghi đè chi nhánh; Manager chỉ ghi đè chi nhánh mình). Đọc: `GET /pickup-slots/effective?branchId=…`. Unhappy: `slotMinutes` 4 hoặc `capacityPerSlot` 0 => 400. Lưu ý: mới là cấu hình, Order chưa áp vào đơn.

---

## 4. Luồng đơn tại quầy (luồng quan trọng nhất)

Mã: [SCRUM-59] tạo đơn và thu tiền mặt, [SCRUM-60] hóa đơn, [SCRUM-58] thông báo sẵn sàng, [SCRUM-61] giao món, [SCRUM-55/56] hàng chờ pha chế. Điều kiện: đã làm mục 2 và 3.1 (có món đã đăng lên chi nhánh), Mock đã bật.

| Service | Code chính |
|---|---|
| Order `AICOS.Ordering.Api` | `Features/Orders/OrderEndpoints.cs`, `OrderEventHandlers.cs`, `Domain/Entities/Order.cs` |
| Payment `AICOS.Payments.Api` | `Features/Payments/PaymentEndpoints.cs`, `WebhookEndpoints.cs`, `Infrastructure/OrderDirectory.cs` |
| Kitchen `AICOS.KitchenOperations.Api` | `Features/KitchenEndpoints.cs`, `KitchenEventHandlers.cs`, `Hubs/KitchenHub.cs` |

**Thứ tự gọi:**

1. **Thu ngân tạo đơn** `POST /api/v1/orders` (token `CASHIER`)
```json
{ "type": "Takeaway", "note": "ít đá", "promotionCode": null,
  "items": [ { "productId": "<PRODUCT_ID>", "quantity": 2, "note": "ít đường" } ] }
```
   `type`: `DineIn | Takeaway | Delivery`; Delivery cần `deliveryAddress`. Giá và tên món lấy từ Menu. Trả `201` với `id`, `finalAmount`.
2. **Thu tiền mặt** `POST /api/v1/payments/transactions/cash` (token `CASHIER`)
```json
{ "orderId": "<ORDER_ID>", "amount": 110000, "amountTendered": 150000 }
```
   Trả `change: 40000`. Vài giây sau (qua sự kiện) `GET /api/v1/orders/{id}/payment` => `paymentStatus: "Paid"`.
   Hoặc **thanh toán online** `POST /api/v1/payments/transactions` `{ "orderId": "<ORDER_ID>", "amount": 110000, "providerCode": "Mock" }` => có `checkoutUrl`; giả lập nhà cung cấp: `POST /api/v1/payments/transactions/{id}/simulate` `{ "outcome": "success", "notify": true }` (chỉ khi `PAYMENTS_ALLOW_SIMULATION=true`).
3. **Hàng chờ pha chế** `GET /api/v1/kitchen/queue` (token `BARISTA`). Đơn mang đi chỉ hiện sau khi đã trả tiền; đơn DineIn hiện ngay (BR-43). Lấy `ticketId`.
4. **Barista pha** `PUT /api/v1/kitchen/tickets/{ticketId}/status` `{ "status": "Brewing" }` rồi `{ "status": "ReadyForPickup" }`.
5. **Thu ngân thấy món sẵn sàng** `GET /api/v1/orders?status=Ready` (và SignalR, xem mục 8). Đơn chuyển `Ready` nhờ sự kiện Kitchen.
6. **Hóa đơn** `GET /api/v1/orders/{id}/receipt` (lần 2 trở đi `isReprint: true`); theo mã: `GET /api/v1/orders/by-code/{orderCode}/receipt`.
7. **Giao món** `POST /api/v1/orders/{id}/deliver` => `status: "Completed"`.

### Unhappy cases của luồng đơn

| Gọi | Kết quả | Quy tắc |
|---|---|---|
| Tạo đơn `items: []` | 400 | Phải có ít nhất 1 món |
| `quantity: 0` hoặc `100` | 400 | 1 đến 99 |
| `productId` không tồn tại / đã hết hàng | 422 `order.item_unavailable` | Giá/món lấy từ Menu |
| Gửi `unitPrice: 1` trong item | bị bỏ qua, tính giá Menu | Không tin giá từ client |
| Barista / Customer tạo đơn | 403 | Chỉ Cashier, Manager, Admin |
| BrandAdmin tạo đơn không có `branchId` | 400 | Admin phải chọn chi nhánh |
| Cashier chi nhánh khác đọc đơn | 403 `branch.forbidden` | BR-05 |
| Tenant khác đọc đơn | 404 | BR-04 |
| Trả tiền sai số (`amount: 1`) | 422 `payment.amount_mismatch` | Số tiền phải bằng đơn |
| Trả tiền đơn không tồn tại / đã hủy | 422 `payment.order_not_found` / `payment.order_cancelled` | |
| Trả tiền lần 2 cho đơn đã trả | 409 `payment.already_paid` | Một đơn trả một lần |
| `amountTendered` nhỏ hơn `amount` | 400 | |
| Hủy đơn chưa trả `POST /orders/{id}/cancel` `{ "reason": "Khách bỏ" }` | 200, hủy 2 lần => 409 `order.already_cancelled` | |
| Hủy đơn **đã trả** ở quầy | 422 `order.paid` | BR-04, phải qua ngoại lệ (mục 5) |
| Giao món khi chưa Ready | 422 `order.not_ready` | |
| Giao món 2 lần | 409 `order.already_delivered` | |
| Barista đổi sang `Completed` khi chưa Ready | 422 `ticket.not_ready` | |
| Cashier đổi trạng thái pha chế | 403 | Chỉ Barista/Manager/Admin |
| Pha đơn mang đi chưa trả | 422 `ticket.not_paid` | BR-43 |

Thử voucher: tạo đơn với `"promotionCode": "SAVE10"` => `discountAmount`, `finalAmount` giảm; dùng lại mã đã dùng => 422 `voucher.invalid`.

---

## 5. Hoàn tiền và đơn ngoại lệ [SCRUM-88, 49, 48]

Service Payment `Features/Payments/RefundEndpoints.cs` và Order `OrderEndpoints.cs` (`ExceptionAsync`).

**Cách 1: Quản lý hủy đơn đã trả (khuyên dùng, nối cả hai service)** `POST /api/v1/orders/{id}/exception` (token `MANAGER`)
```json
{ "reason": "Pha sai món", "refundKind": "Cancellation" }
```
`refundKind`: `Cancellation` (theo % chính sách), `NoShow`, `Exception` (bắt buộc kèm `"amount": 20000`). Đơn thành `Cancelled`, `refundStatus` `RefundPending` rồi `Refunded` (hoàn tự động nếu trong chính sách và `autoApproveWithinPolicy=true`). Xem: `GET /api/v1/orders/exceptions`.

**Cách 2: Gọi thẳng Payment** `POST /api/v1/payments/transactions/{paymentId}/refunds` (Cashier/Manager/Admin)
```json
{ "kind": "Exception", "amount": 30000, "reason": "Khiếu nại chất lượng" }
```
Ngoài chính sách thì `status: "PendingApproval"`; người duyệt: `POST /api/v1/payments/refunds/{id}/approve` `{}` hoặc `…/reject` `{ "note": "Lý do" }` (Manager/Admin theo `exceptionApprover`). Danh sách chờ duyệt: `GET /api/v1/payments/refunds?status=PendingApproval`.

| Unhappy | Kết quả |
|---|---|
| Ngoại lệ cho đơn chưa trả | 422 `order.not_paid` |
| Cashier gọi ngoại lệ | 403 |
| `refundKind: "Exception"` không có `amount` | 400 |
| Hủy ngoại lệ lần 2 | 409 `order.already_cancelled` |
| Hoàn quá số đã trả | 422 `refund.exceeds_payment` |
| Hoàn lại khi đã hoàn hết | 422 `refund.nothing_to_refund` |
| `NoShow` khi chính sách 0% | 422 `refund.nothing_to_refund` |
| Manager tự duyệt yêu cầu của mình | 403 `refund.self_approval` |
| Brand đặt người duyệt là BrandAdmin mà Manager duyệt | 403 `refund.approver_required` |
| Duyệt lần 2 | 422 `refund.not_pending` |

---

## 6. Webhook, trạng thái giao dịch, đối soát [SCRUM-86, 87, 89]

- **Webhook** `POST /api/v1/payments/webhooks/Mock` (không cần token, bắt buộc chữ ký)
```json
{ "eventId": "evt-1", "transactionRef": "<transactionRef của giao dịch>", "status": "success", "amount": 110000, "signature": "<HMAC>" }
```
  `signature` = HMAC-SHA256 (hex, chữ thường) của chuỗi `eventId|transactionRef|status|amount` với khóa `Payments:MockWebhookSecret` (mặc định `aicos-mock-webhook-secret`; cổng thật dùng secret key của cổng). Thử nhanh bằng PowerShell:
```powershell
$k=[Text.Encoding]::UTF8.GetBytes("aicos-mock-webhook-secret"); $h=New-Object Security.Cryptography.HMACSHA256(,$k)
($h.ComputeHash([Text.Encoding]::UTF8.GetBytes("evt-1|TXREF|success|110000")) | % { $_.ToString("x2") }) -join ""
```
  Gửi lại cùng `eventId` => `{ "duplicate": true }`, không đổi gì. Chữ ký sai => 401 `webhook.invalid_signature`. Sai số tiền => `outcome: "amount_mismatch"` (không ghi nhận). Tiền về sau khi hết hạn => `applied_late`.
- **Mất webhook**: `POST /api/v1/payments/transactions/{id}/query` `{}` hỏi lại nhà cung cấp. Thử: simulate với `"notify": false` rồi gọi query.
- **Hết hạn**: giao dịch chưa trả quá `unpaidOrderTimeoutMinutes` tự thành `Expired` (job nền quét mỗi 60 giây) và đơn tự `Cancelled`.
- **Đối soát** (Manager/Admin): `GET /api/v1/payments/reconciliation?from=2026-11-01&to=2026-11-01` (tổng theo phương thức, bất thường); `GET /reconciliation/auto?from=…&to=…` (tự lấy đơn từ Order và so khớp); `POST /reconciliation/orders` (gửi danh sách đơn).

---

## 7. Nhân sự, chấm công, ca thay, lương, chốt ca

Service `AICOS.WorkforceScheduling.Api`, `src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/Features/{Employees,Scheduling,Attendance,Replacements,Payroll,CashClosing}`. Mã: [SCRUM-125] nhân sự, [SCRUM-126] pay rules, [SCRUM-46/127/128] ca và ca thay, [SCRUM-63] chấm công, [SCRUM-129/51] lương, [SCRUM-62] chốt két.

**Thứ tự gọi (token `MANAGER` trừ khi ghi khác):**

1. Hồ sơ nhân sự: `POST /api/v1/workforce/employees` `{ "userId": "<ID tài khoản cashier>", "contractType": "FullTime", "position": "Thu ngân", "hireDate": "2026-01-01" }` (`FullTime` tính theo ngày, `PartTime` theo giờ).
2. Ca mẫu: `POST /api/v1/workforce/work-shifts` `{ "name": "Ca sáng", "startTime": "07:00", "endTime": "12:00" }`.
3. Xếp ca: `POST /api/v1/workforce/shift-assignments` `{ "employeeId": "<EMP_ID>", "workShiftId": "<SHIFT_ID>", "workDate": "2026-11-02" }`. Nhân viên xem lịch `GET /shift-assignments/mine`; xác nhận `POST /{id}/confirm`; từ chối `POST /{id}/decline` `{ "reason": "Ốm" }`.
4. Pay rules (BrandAdmin): `PUT /api/v1/workforce/pay-rules`
```json
{ "fullTimeDayRate": 300000, "partTimeHourRate": 30000, "replacementHourRate": 45000, "overtimeAfterHours": 8, "overtimeMultiplier": 1.5 }
```
5. Chấm công (token `CASHIER`/`BARISTA`): `POST /api/v1/workforce/attendance/check-in` `{ "assignmentId": "<ASSIGNMENT_ID>" }` rồi `…/check-out` cùng body.
6. Quản lý duyệt công: `POST /api/v1/workforce/attendance/approve-range` `{ "branchId": "<BRANCH_ID>", "from": "2026-11-02", "to": "2026-11-02" }`; sửa giờ: `POST /attendance/{id}/adjust` `{ "checkInAt": "2026-11-02T07:00:00+07:00", "checkOutAt": "2026-11-02T12:00:00+07:00", "reason": "Quên chấm ra" }`; ghi vắng: `POST /attendance/{id}/absent` `{ "reason": "Nghỉ không phép" }`.
7. Ca thay: Manager mở `POST /replacement-shifts` `{ "assignmentId": "<ASSIGNMENT_ID>", "reason": "Chi ốm" }` => nhân viên khác `POST /replacement-shifts/{id}/register` `{ "note": "Em nhận" }` => Manager `GET /replacement-shifts/{id}/applications` rồi `POST /replacement-shifts/{id}/approve` `{ "applicationId": "<APP_ID>" }`.
8. Lương: `GET /payroll/summary?from=2026-11-02&to=2026-11-08`; xuất: `GET /payroll/export?from=…&to=…&format=xlsx` (bỏ `format` = CSV); bảng công chi tiết `GET /payroll/records/export?…`; thưởng/trừ: `POST /payroll/adjustments` `{ "employeeId": "<EMP_ID>", "workDate": "2026-11-02", "amount": 50000, "reason": "Thưởng chuyên cần" }` (âm là trừ).
9. Chốt két (Cashier): `POST /cash-closings` `{ "assignmentId": "<ASSIGNMENT_ID>", "countedAmount": 110000, "note": "Đủ" }` (số kỳ vọng tự tính từ tiền mặt đã thu/hoàn trong ca). Quản lý xem chi tiết `GET /cash-closings/{id}/details` (tiền mặt và đơn ngoại lệ trong ca) rồi `POST /cash-closings/{id}/confirm` `{ "note": "OK" }`.

| Unhappy | Kết quả | Quy tắc |
|---|---|---|
| Xếp 2 ca chồng giờ cho một người | 409 `shift.overlap` | BR-48 |
| Check-in ca của người khác | bị từ chối (không thấy ca đó) | BR-49 |
| Check-in trước giờ ca hơn 15 phút hoặc sau khi ca kết thúc | 422 `attendance.outside_window` | BR-49 |
| Check-in lần 2 | 409 `attendance.already_checked_in` | |
| Hai thao tác chấm công cách nhau dưới 5 phút | 422 `attendance.too_soon` | BR-50 |
| Check-out khi chưa check-in | 422 `attendance.not_checked_in` | |
| Xem lương khi brand chưa đặt pay rules | 422 `payroll.rules_missing` | |
| Lương chỉ tính công **đã duyệt** | bản ghi chưa duyệt không được trả, được đếm ở `unreviewedRecords` | BR-54 |
| Giờ làm vượt 8 giờ/ngày | phần vượt nhân 1.5 (`overtimePay`) | BR-53 |
| Thưởng/trừ `amount: 0` hoặc quá 50 triệu | 400 | |
| Chốt két ca chưa check-in | 422 `cash_closing.shift_not_worked` | |
| Chốt két 2 lần | 409 | |
| Xác nhận ca có chênh lệch mà không ghi chú | 422 `cash_closing.note_required` | BR-55 |
| Xác nhận ca còn đơn ngoại lệ chưa hoàn tiền | 422 `cash_closing.exceptions_unresolved` | gửi `acknowledgeExceptions: true` để vẫn chốt (có audit) |
| Đăng ký ca thay của chính mình | 422 `replacement.own_shift` | |
| Tổng giờ tuần vượt giới hạn | 422 `shift.weekly_hours_exceeded` | |

---

## 8. Báo cáo, audit, SignalR

- **Báo cáo [SCRUM-50]** (Manager/Admin) Service `AICOS.ReportingAnalytics.Api`, `Features/Reports/ReportEndpoints.cs`: `GET /api/v1/reporting/chain?from=2026-11-01&to=2026-11-07` (chỉ BrandAdmin), `GET /reporting/branch?branchId=…&from=…&to=…`, `GET /reporting/payment-methods`, `GET /reporting/peak-hours`, xuất CSV `GET /reporting/chain/export`, `GET /reporting/branch/export`. Số liệu đến từ sự kiện, nên sau khi trả tiền có thể chờ 1 đến 2 giây. Unhappy: `from` sau `to` hoặc quá 93 ngày => 400; Manager xem chi nhánh khác => 403.
- **Audit [SCRUM-106, 107]**: `GET /api/v1/identity/audit-logs` (BrandAdmin: của brand; SuperAdmin: cấp nền tảng). Không có API sửa/xóa. Mọi thay đổi chính sách, lương, hoàn tiền, chốt ca, check-in/out đều ghi vào đây.
- **Sức khỏe nền tảng [SCRUM-40]**: `GET /api/v1/platform/health` (Gateway, SuperAdmin); `GET /api/v1/identity/platform/overview`, `…/platform/revenue`.
- **SignalR [SCRUM-58/55]**: hub `ws://localhost:8080/hubs/kitchen?access_token=<token>` (hoặc `:5107`). Kiểm tra nhanh: `POST http://localhost:8080/hubs/kitchen/negotiate?negotiateVersion=1&access_token=<token>` => 200; không token => 401. Client gọi `JoinKitchenGroup("<BRANCH_ID>")` (Barista/Manager/Admin) hoặc `JoinPosGroup("<BRANCH_ID>")` (Cashier/Manager/Admin); nhận `ReceiveKitchenTicket`, `TicketStatusChanged`, `OrderReadyForPickup`, `CupStickersPrinted`. Cashier chi nhánh khác `JoinPosGroup` => lỗi "own branch".

---

## 9. Danh sách kiểm tra nhanh trước khi báo hoàn thành

1. `docker compose up -d --build` chạy sạch; `sh scripts/smoke-docker.sh` in `ALL CHECKS PASSED`.
2. `dotnet test AICOS.sln` không lỗi.
3. Mỗi kịch bản ở mục 4 đến 7: một happy case và ít nhất hai unhappy case trong bảng đều đúng mã lỗi.
4. Đổi sang token brand khác: mọi dữ liệu brand cũ phải 404 (cách ly tenant).

## 10. Giới hạn đã biết (đừng báo là lỗi)

- Cổng Momo/ZaloPay/VietQR là mô phỏng, chưa gọi API sandbox thật.
- Guest QR (SCRUM-66 đến 117) vẫn chạy trong RAM, chưa gắn tenant: đơn QR chưa vào hàng chờ pha chế.
- Khách tự đặt hàng online và việc áp pickup slot vào đơn chưa làm.
- Email quên mật khẩu chưa gửi thật (xem log Identity khi chạy Development).
- Chưa có PDF bảng lương (đã có CSV và Excel).
- Sự kiện đi qua Redis Pub/Sub: nếu một service tắt đúng lúc có thể trễ/mất sự kiện, dùng `reconciliation/auto` để phát hiện.
