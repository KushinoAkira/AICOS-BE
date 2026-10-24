# FE-INTEGRATION-API-REFERENCE

Tra cứu nhanh để Frontend nối API. Mã SCRUM ở đầu mỗi mục. `…` trong response nghĩa là còn các trường phụ (đã lược bớt, tên trường giữ nguyên camelCase). Mọi enum trả về dạng **chuỗi**.

## 0. Kết nối chung

| Mục | Giá trị |
|---|---|
| Base URL (Docker) | `http://localhost:8080` (API Gateway). Mọi API: `{base}/api/v1/{service}/…` |
| Base URL (chạy local bằng IDE) | Gateway `http://localhost:5000`; hoặc gọi thẳng service: Identity `:5101`, Menu `:5102`, Table `:5103`, Order `:5104`, Payment `:5105`, Kitchen `:5107`, Workforce `:5108`, Reporting `:5109`, Customer `:5110` |
| Prefix theo service | `identity`, `menu`, `tables`, `orders`, `payments`, `kitchen`, `workforce`, `reporting`, `customers` |
| Xác thực | Header `Authorization: Bearer <accessToken>` (lấy từ `POST /api/v1/identity/auth/login`). Token hết hạn: `POST /auth/refresh` |
| `tenant_id` | **FE không gửi.** `tenant_id` và `branch_id` nằm trong JWT (claims `tenant_id`, `branch_id`); server tự lọc theo brand/chi nhánh. Nhân viên chi nhánh luôn làm việc ở chi nhánh trong token; **BrandAdmin phải truyền `branchId`** ở API cần chi nhánh |
| Content-Type | `application/json` |
| Thời gian | UTC ISO-8601 (`2026-11-02T03:00:00Z`); ngày `yyyy-MM-dd`; giờ ca `HH:mm` |
| Tiền | VND, số nguyên (không có phần lẻ) |
| Phân trang | `{ "items": [...], "page": 1, "pageSize": 20, "totalCount": 57 }` với các API ghi chú "phân trang" |
| Vai trò | `SuperAdmin`, `BrandAdmin`, `BranchManager`, `Barista`, `Cashier`, `Customer`, `Guest` |

**Lỗi chuẩn** (HTTP 4xx):
```json
{ "type": "…", "title": "Business rule violated", "status": 422, "detail": "The order is 110000 VND, not 1.", "errorCode": "payment.amount_mismatch", "correlationId": "…", "errors": { "Items[0].Quantity": ["Quantity must be between 1 and 99."] } }
```
`errors` chỉ có ở 400 (validation). Hiển thị `detail`; xử lý logic theo `errorCode`. `401` chưa đăng nhập, `403` sai quyền/chi nhánh, `404` không thấy, `409` trùng, `422` vi phạm nghiệp vụ, `429` quá giới hạn (header `Retry-After`).

**Giới hạn gọi (SCRUM-143):** `POST /identity/auth/login | register | forgot-password | reset-password` tối đa 10 lần/phút/IP; `/ai/chat` 20 lần/phút; còn lại 600 lần/phút.

---

## 1. Xác thực và tài khoản (Identity) [SCRUM-102, 103, 104, 105, 107]: `AICOS.IdentityOrganization.Api`

**Đăng nhập**: `POST /api/v1/identity/auth/login` (công khai)
```json
{ "login": "cashier@demo.com", "password": "Brand@12345" }
```
Response 200:
```json
{ "accessToken": "eyJ…", "accessTokenExpiresAtUtc": "2026-11-02T04:00:00Z", "refreshToken": "…", "refreshTokenExpiresAtUtc": "2026-11-09T03:00:00Z", "tokenType": "Bearer",
  "user": { "id": "…", "fullName": "Thu Ngân", "email": "cashier@demo.com", "username": "cashier1", "role": "Cashier", "branchId": "…", "…": "…" } }
```
Lỗi: `401 auth.invalid_credentials`; `403 account.temporarily_locked` (sai 5 lần, khóa tạm).

| Chức năng | Method và endpoint | Body mẫu | Response |
|---|---|---|---|
| Làm mới token | `POST /auth/refresh` | `{ "refreshToken": "…" }` | như đăng nhập |
| Đăng xuất | `POST /auth/logout` | `{ "refreshToken": "…" }` | 204 |
| Khách đăng ký | `POST /auth/register` | `{ "tenantCode": "demo-coffee", "fullName": "Khách A", "email": "a@x.com", "phone": "0900000009", "password": "Customer@123", "confirmPassword": "Customer@123" }` | 201 trả hồ sơ người dùng `{ "id", "fullName", "email", "role": "Customer", "tenantId", "status", … }` (không có token): gọi tiếp `POST /auth/login` |
| Hồ sơ của tôi | `GET /auth/me` | | `{ "id", "fullName", "email", "username", "phone", "role", … }` |
| Sửa hồ sơ | `PUT /auth/me` | `{ "fullName": "…", "phone": "0900000009", "username": "cashier1" }` | hồ sơ mới |
| Đổi mật khẩu | `POST /auth/change-password` | `{ "currentPassword": "…", "newPassword": "…", "confirmNewPassword": "…" }` | 200 |
| Quên mật khẩu | `POST /auth/forgot-password` | `{ "email": "a@x.com" }` | 200 (luôn trả như nhau) |
| Đặt lại mật khẩu | `POST /auth/reset-password` | `{ "email": "a@x.com", "code": "123456", "newPassword": "…", "confirmNewPassword": "…" }` | 200; sai mã `422 auth.reset_code_invalid` |

### Quản lý tổ chức (BrandAdmin; xem [SCRUM-42])

| Chức năng | Endpoint | Body | Ghi chú |
|---|---|---|---|
| Danh sách / tạo chi nhánh | `GET /branches`, `POST /branches` | `{ "name": "Quận 1", "address": "1 Lê Lợi", "phone": "0900000002", "openingTime": "07:00:00", "closingTime": "22:00:00" }` | 201 `{ "id", "name", "address", "phone", "status": "Active", … }`; vượt gói `422 subscription.limit_reached` |
| Sửa / đổi trạng thái | `PUT /branches/{id}`, `PATCH /branches/{id}/status` | `{ "status": "Suspended" }` | `Active \| Suspended` |
| Chi nhánh đang hoạt động | `GET /branches/active` (công khai) | | danh sách |
| Tài khoản nhân viên | `GET/POST /users`, `PUT /users/{id}`, `PATCH /users/{id}/status`, `DELETE /users/{id}` | `{ "fullName": "Thu Ngân", "email": "c@demo.com", "username": "cashier1", "phone": "090…", "password": "Brand@12345", "role": "Cashier", "branchId": "…" }` | status `Active \| Locked`; Manager chỉ tạo Barista/Cashier của chi nhánh mình |
| Nhật ký (audit) | `GET /audit-logs?…` | | phân trang; BrandAdmin xem brand, SuperAdmin xem nền tảng [SCRUM-106] |

### Gói thuê bao của Brand Admin [SCRUM-132] (chỉ `BrandAdmin`, luôn là gói của chính brand)

| Chức năng | Method và endpoint | Body | Response |
|---|---|---|---|
| Xem trạng thái gói | `GET /identity/subscription` | | `{ "subscription": { "planName", "fee", "billingPeriod": "Monthly\|Quarterly\|Yearly", "status", "periodStartUtc", "periodEndUtc", "paymentStatus": "Unpaid\|Paid", "paidAmount", "outstanding", "health": "Current\|DueSoon\|GracePeriod\|Overdue", "daysUntilRenewal", "activeBranches", "staffAccounts", "maxBranches", "maxStaffAccounts" }, "canRenew": true, "renewableFromUtc": null, "amountToPay": 500000 }` |
| Thanh toán / gia hạn mô phỏng | `POST /identity/subscription/renew` | `{}` | `{ "action": "PaidOutstanding\|Renewed", "amountPaid": 500000, "payment": { "id", "amount", "method": "Simulated", "reference", "paidAtUtc" }, "subscription": { …như trên } }` |
| Lịch sử thanh toán | `GET /identity/subscription/payments?page=&pageSize=` | | phân trang `{ "items": [ { "id", "amount", "paidAtUtc", "method", "reference", "periodStartUtc" } ], … }` |

Quy tắc cho FE: nút thanh toán/gia hạn bật khi `canRenew = true`, hiển thị `amountToPay`; khi `canRenew = false` và có `renewableFromUtc` thì hiện "Gia hạn từ ngày …". Kỳ chưa trả: renew chỉ thanh toán kỳ hiện tại. Kỳ đã trả: chỉ gia hạn được khi còn ≤ 7 ngày hoặc quá hạn, nếu không `422 subscription.renewal_not_open`. Brand chưa có gói: `404`. Không có tiền thật chuyển đi (mô phỏng).

### Nền tảng (SuperAdmin) [SCRUM-38, 39, 40, 109, 110, 111]

| Chức năng | Endpoint | Body mẫu |
|---|---|---|
| Gói thuê bao | `POST /subscription-plans`, `GET`, `PUT /{id}`, `PATCH /{id}/status` | `{ "name": "Basic", "tier": "Basic", "fee": 500000, "billingPeriod": "Monthly", "maxBranches": 3, "maxStaffAccounts": 20, "description": "…" }` (`Monthly\|Quarterly\|Yearly`); status `{ "isActive": false }` |
| Tạo tenant + Brand Admin | `POST /tenants` | `{ "name": "Cà phê Demo", "code": "demo-coffee", "firstAdmin": { "fullName": "Chủ Quán", "email": "owner@demo.com", "phone": "0900000001", "password": "Brand@12345" }, "planId": "…" }` → 201 `{ "id", "name", "code", "status": "Active", … }` |
| Tenant | `GET /tenants`, `GET /tenants/{id}`, `PUT /tenants/{id}` `{ "name": "…" }`, `PATCH /tenants/{id}/status` `{ "status": "Suspended" }` | |
| Gán gói | `POST /tenants/{tenantId}/subscription` | `{ "planId": "…", "startDate": "2026-11-01T00:00:00Z" }` |
| Thanh toán gói | `POST /tenants/{tenantId}/subscription/payments`, `GET …/payments` | `{ "amount": 500000, "method": "Transfer", "reference": "CK-001", "paidAt": "2026-11-01T08:00:00Z" }` |
| Gia hạn | `POST /tenants/{tenantId}/subscription/renew` | `{}` |
| Danh sách thuê bao | `GET /subscriptions?health=DueSoon` | |
| Tổng quan, doanh thu SaaS | `GET /platform/overview`, `GET /platform/revenue?from=…&to=…` | |
| Sức khỏe nền tảng | `GET /api/v1/platform/health` (Gateway, không có prefix service) | |

---

## 2. Menu (`AICOS.MenuCatalog.Api`, prefix `menu`) [SCRUM-43, 44, 113]

| Chức năng | Method và endpoint | Quyền | Body mẫu | Response |
|---|---|---|---|---|
| Menu khách thấy | `GET /menu/public/branches/{branchId}/menu` | công khai | | `{ "branchId", "categories": [ { "id", "name", "products": [ { "id", "name", "description", "imageUrl", "basePrice": 55000, "price": 59000, "hasBranchPrice": true, "isAvailable": true, "tags": [], "optionGroups": [ … ] } ] } ] }` |
| Menu nhân viên (có món hết hàng) | `GET /menu/branches/{branchId}/menu` | Staff | | cùng cấu trúc |
| Danh mục | `GET/POST /menu/categories`, `PUT /{id}`, `PATCH /{id}/status`, `DELETE /{id}` | Admin ghi, Staff đọc | `{ "name": "Cà phê", "sortOrder": 1 }` | `{ "id", "name", "sortOrder", "status", "productCount" }` |
| Món | `GET/POST /menu/products`, `GET/PUT/DELETE /{id}` | Admin ghi | `{ "categoryId": "…", "name": "Latte", "description": "…", "basePrice": 55000, "tags": ["coffee"], "optionGroups": [ { "name": "Size", "kind": "Size", "selectionType": "Single", "isRequired": true, "options": [ { "name": "S", "priceDelta": 0, "isDefault": true } ] } ] }` | 201 món (có `id`) |
| Đăng món lên chi nhánh / giá riêng | `PUT /menu/products/{id}/branches/{branchId}` | Admin | `{ "isPublished": true, "priceOverride": 59000 }` | `{ "branchId", "productId", "productName", "isPublished", "isAvailable", "basePrice", "priceOverride", "effectivePrice" }` |
| Đăng hàng loạt | `POST /menu/publications` | Admin | `{ "branchIds": ["…"], "productIds": ["…"], "publish": true }` | `{ "branches": 1, "products": 1, "changed": 1 }` |
| Hết hàng / còn hàng | `PATCH /menu/branches/{branchId}/products/{productId}/availability` | Admin, Manager, Barista | `{ "isAvailable": false }` | `BranchProductResponse` |

---

## 3. Bàn và QR (`AICOS.TableReservation.Api`, prefix `tables`) [SCRUM-45]

| Chức năng | Endpoint | Quyền | Body | Response |
|---|---|---|---|---|
| Khu vực | `GET/POST /tables/areas`, `PUT/DELETE /{id}` | Manager/Admin ghi | `{ "branchId": "…", "name": "Tầng 1", "sortOrder": 1 }` | `{ "id", "branchId", "name", "sortOrder", "tableCount" }` |
| Bàn | `GET/POST /tables/dining-tables`, `PUT/DELETE /{id}` | Manager/Admin ghi | `{ "branchId": "…", "areaId": "…", "tableCode": "T1", "capacity": 4, "positionX": 10, "positionY": 20 }` | `{ "id", "branchId", "areaId", "areaName", "tableCode", "capacity", "status", "qrToken", … }` |
| Trạng thái bàn | `PATCH /tables/dining-tables/{id}/status` | Staff | `{ "status": "Occupied" }` (`Available\|Occupied\|…\|OutOfService`) | bàn |
| Sơ đồ mặt bằng | `GET /tables/floor-plan?branchId=…` | Staff | | khu vực kèm danh sách bàn |
| Ảnh QR / đổi token | `GET /tables/dining-tables/{id}/qr.svg`, `POST …/{id}/qr/regenerate` | Manager/Admin | | SVG / bàn mới |
| Tra QR (khách quét) | `GET /tables/public/qr/{token}` | công khai | | thông tin bàn/chi nhánh |

---

## 4. Khách hàng, điểm, voucher (`AICOS.CustomerEngagement.Api`, prefix `customers`) [SCRUM-47]

| Chức năng | Endpoint | Quyền | Body | Ghi chú |
|---|---|---|---|---|
| Hồ sơ khách | `GET/PUT /customers/me` | đăng nhập | `{ "fullName": "…", "phone": "…", "birthday": "1995-05-01", "marketingOptIn": true }` | |
| Điểm của tôi | `GET /customers/me/loyalty` | đăng nhập | | số điểm, hạng, lịch sử |
| Sở thích đồ uống | `GET/PUT /customers/me/preferences` | đăng nhập | `{ "sugarPercent": 50, "ice": "Less", "favoriteToppings": ["trân châu"], "notes": "…" }` | |
| Tra cứu khách | `GET /customers`, `GET /customers/{id}`, `GET /customers/{id}/loyalty` | Staff | | |
| Điều chỉnh điểm | `POST /customers/{id}/points-adjustments` | Manager/Admin | `{ "points": 20, "note": "Bù điểm" }` | |
| Quy tắc điểm | `GET /customers/loyalty/rules` (Manager/Admin), `PUT` (Admin) | | `{ "isActive": true, "amountPerPoint": 10000, "redeemValuePerPoint": 1000, "minPointsToRedeem": 10, "silverThreshold": 100, "goldThreshold": 500 }` | trả thêm `isDefault` |
| Voucher | `GET /customers/vouchers`, `GET /{id}`, `POST` (Admin), `POST /{id}/revoke` (Admin) | | `{ "branchId": null, "code": "SAVE10", "type": "Voucher", "discountType": "Percentage", "value": 10, "minOrderAmount": 50000, "expiresAt": "2027-01-01T00:00:00Z", "issuedToName": "Khách A" }` | `type`: `Voucher\|GiftCard`; `discountType`: `Percentage\|FixedAmount` |
| Kiểm tra voucher | `POST /customers/vouchers/validate` | Staff | `{ "code": "SAVE10", "orderAmount": 100000 }` | `{ "code", "orderAmount", "discount": 10000, "amountToPay": 90000 }` |

---

### Đổi điểm sang voucher, voucher và điểm quanh đơn hàng [SCRUM-133, 134]

| Chức năng | Method và endpoint | Quyền | Body | Response |
|---|---|---|---|---|
| Đổi điểm sang voucher (khách tự đổi) | `POST /customers/me/redemptions` | Customer | `{ "points": 50 }` | 201 `{ "voucher": { "id", "code": "PT-9F3A1C2B", "value": 50000, "discountType": "FixedAmount", "expiresAtUtc" }, "pointsSpent": 50, "pointsBalance": 20 }` |
| Đổi điểm hộ khách tại quầy | `POST /customers/{customerId}/redemptions` | Cashier, Manager, Admin | `{ "points": 50 }` | như trên |
| Voucher của tôi | `GET /customers/me/vouchers?status=Active&page=&pageSize=` | Customer | | phân trang `{ "items": [ { "id", "code", "type", "discountType", "value", "minOrderAmount", "expiresAtUtc", "status": "Active\|Redeemed\|Revoked", "isExpired", "branchId" } ], … }` |
| Kiểm tra voucher cho đơn | `POST /customers/vouchers/validate` | Staff | `{ "code": "PT-9F3A1C2B", "orderAmount": 70000, "branchId": "…", "customerId": "…" }` | `{ "code", "orderAmount", "discount": 50000, "amountToPay": 20000 }` |
| Dùng voucher (thường do Order gọi) | `POST /customers/vouchers/redeem` | Staff | `{ "code": "…", "orderAmount": 70000, "orderReference": "ORD-…", "branchId": "…", "customerId": "…" }` | như validate |

Quy tắc đổi điểm: `giá trị = points x redeemValuePerPoint` (quy tắc brand, `GET /customers/loyalty/rules`); tối thiểu `minPointsToRedeem`; tối đa 100 000 điểm một lần; voucher hạn 90 ngày và **chỉ chủ điểm dùng được**. Lỗi: `422 loyalty.below_minimum`, `422 loyalty.insufficient_points`, `422 loyalty.rules_inactive`, `400` points ≤ 0 hoặc quá lớn.

Cách dùng voucher khi đặt hàng: gửi `promotionCode` (và `customerId` của thành viên) trong `POST /orders`; xem mục 5. Lỗi voucher: `422 voucher.not_yours` (của thành viên khác), `voucher.wrong_branch`, `voucher.already_redeemed`, `voucher.expired`, `voucher.revoked`, `voucher.min_order_not_met`.

**Điểm thưởng sau thanh toán:** điểm chỉ được cộng khi đơn **đã thanh toán**, theo `amountPerPoint` của brand, cho đơn có `customerId`. FE không gọi gì thêm: sau 1 đến 2 giây `GET /customers/me/loyalty` có `pointsBalance` mới và dòng lịch sử `Earn`. Đơn đã trả bị quản lý hủy ngoại lệ thì có dòng `Reverse` (âm) và điểm bị trừ lại. Đơn chưa trả bị hủy hoặc quá hạn thanh toán thì voucher đã dùng cho đơn đó trở lại `Active`. Loại dòng lịch sử: `Earn | Adjust | Redeem | Reverse`.

## 5. Đơn hàng (`AICOS.Ordering.Api`, prefix `orders`) [SCRUM-59, 60, 61, 49]

Giá và tên món luôn lấy từ Menu: FE chỉ gửi `productId`, `quantity`, `note`.

**Tạo đơn tại quầy [SCRUM-59]**: `POST /api/v1/orders` (Cashier, Manager, BrandAdmin)
```json
{ "type": "Takeaway", "branchId": null, "customerId": null, "tableNumber": null, "deliveryAddress": null, "note": "ít đá", "promotionCode": "SAVE10",
  "items": [ { "productId": "…", "quantity": 2, "note": "ít đường" } ] }
```
(`branchId` chỉ BrandAdmin cần; `customerId` = id tài khoản hoặc hồ sơ của thành viên để đơn được cộng điểm và dùng voucher riêng của họ; `type`: `DineIn | Takeaway | Delivery`.) Response 201:
```json
{ "id": "…", "orderCode": "ORD-20261102-A1B2C3", "branchId": "…", "cashierId": "…", "type": "Takeaway", "status": "Pending", "paymentStatus": "Unpaid",
  "originalAmount": 110000, "promotionCode": "SAVE10", "discountAmount": 11000, "finalAmount": 99000, "refundedAmount": 0, "refundStatus": "None",
  "createdAtUtc": "2026-11-02T03:00:00Z",
  "items": [ { "id": "…", "productId": "…", "productName": "Latte", "unitPrice": 55000, "quantity": 2, "totalPrice": 110000, "note": "ít đường" } ] }
```
Lỗi: `400` validation; `422 order.item_unavailable`; `422 voucher.invalid`.

| Chức năng | Method và endpoint | Quyền | Body | Response |
|---|---|---|---|---|
| Danh sách đơn | `GET /orders?branchId=&status=&paymentStatus=&from=&to=&limit=&offset=` | Staff | | mảng `{ "id", "orderCode", "branchId", "type", "status", "paymentStatus", "paymentProvider", "tableNumber", "originalAmount", "discountAmount", "finalAmount", "refundedAmount", "refundStatus", "itemCount", "paidAtUtc", "cancelledAtUtc", "createdAtUtc" }` |
| Đơn của khách | `GET /orders/mine` | Customer | | như trên |
| Chi tiết đơn | `GET /orders/{id}` | Staff / khách của đơn | | như response tạo đơn, có thêm `paidAtUtc`, `readyAtUtc`, `deliveredAtUtc`, `completedAtUtc` |
| Kết quả thanh toán [SCRUM-60] | `GET /orders/{id}/payment` | như trên | | `{ "orderId", "orderCode", "amountDue", "paymentStatus": "Paid", "method": "Cash", "provider": "Cash", "paymentId", "paidAtUtc", "refundedAmount", "isPaid": true }` |
| Hóa đơn [SCRUM-60] | `GET /orders/{id}/receipt` hoặc `GET /orders/by-code/{orderCode}/receipt` | Cashier/Manager/Admin | | `{ "receiptNumber", "orderId", "items": [ { "productName", "unitPrice", "quantity", "totalPrice", "note" } ], "originalAmount", "discountAmount", "finalAmount", "paymentStatus", "paymentMethod", "paidAtUtc", "isReprint": false, "printedCount": 1, "printedAtUtc" }` |
| Đổi hình thức phục vụ | `PUT /orders/{id}/service-type` | Cashier/Manager/Admin | `{ "type": "DineIn", "tableNumber": "T5" }` | đơn |
| Hủy đơn chưa trả | `POST /orders/{id}/cancel` | Cashier/Manager/Admin | `{ "reason": "Khách bỏ" }` | đơn `status: "Cancelled"`; đơn đã trả `422 order.paid` |
| Giao món [SCRUM-61] | `POST /orders/{id}/deliver` | Cashier/Manager/Admin | `{}` | đơn `status: "Completed"` (`422 order.not_ready` nếu chưa Ready) |
| Hủy đơn đã trả (ngoại lệ) [SCRUM-49] | `POST /orders/{id}/exception` | Manager/Admin | `{ "reason": "Pha sai món", "refundKind": "Cancellation", "amount": null }` | đơn `status: "Cancelled"`, `refundStatus: "RefundPending"` (rồi `Refunded`) |
| Danh sách ngoại lệ | `GET /orders/exceptions?refundStatus=&from=&to=` | Manager/Admin | | mảng tóm tắt đơn |

Trạng thái đơn: `Pending → Confirmed → Preparing → Ready → Completed`, hoặc `Cancelled`. `paymentStatus`: `Unpaid | Paid | Refunded`. `refundStatus`: `None | Needed | RefundPending | Refunded | RefundFailed`.

**Pickup slot [SCRUM-114]**: `GET /orders/pickup-slots/effective?branchId=…` (đăng nhập) → `{ "slotMinutes": 15, "capacityPerSlot": 10, "cutoffMinutes": 15, "advanceDays": 1, "source": "Default|Brand|Branch" }`. Cấu hình: `PUT /orders/pickup-slots` `{ "branchId": null, "slotMinutes": 15, "capacityPerSlot": 8, "cutoffMinutes": 20, "advanceDays": 2 }` (Manager/Admin), `GET /orders/pickup-slots`, `DELETE /orders/pickup-slots/{id}`.

---

## 6. Thanh toán (`AICOS.Payments.Api`, prefix `payments`) [SCRUM-85, 86, 87, 88, 89, 59]

| Chức năng | Method và endpoint | Quyền | Body mẫu | Response |
|---|---|---|---|---|
| Phương thức chi nhánh nhận | `GET /payments/merchant-settings/available?branchId=…` | đăng nhập | | `[ { "providerCode": "Mock", "displayName": "Mock", "mode": "Mock", "source": "Platform|Brand|Branch" } ]` |
| Tạo thanh toán online | `POST /payments/transactions` | đăng nhập | `{ "orderId": "…", "amount": 99000, "providerCode": "Mock", "branchId": null }` (`providerCode`: `Mock\|VietQR\|Momo\|ZaloPay`; khách phải gửi `branchId`) | 201 `{ "id", "orderId", "amount", "method", "status": "Pending", "transactionRef", "checkoutUrl", "expiresAtUtc", "paidAtUtc": null, "refundedAmount": 0, "refundable": 0, … }` → FE hiện `checkoutUrl` (ảnh QR / link) |
| Thu tiền mặt | `POST /payments/transactions/cash` | Cashier, Manager, Admin | `{ "orderId": "…", "amount": 99000, "amountTendered": 100000, "branchId": null }` | 201 `{ "payment": { "id", "status": "Succeeded", "method": "Cash", … }, "change": 1000 }` |
| Chi tiết / danh sách | `GET /payments/transactions/{id}`, `GET /payments/transactions?orderId=&branchId=&status=&limit=` | chủ giao dịch / Staff | | giao dịch (có mảng `refunds` ở chi tiết) |
| Hủy giao dịch đang chờ | `POST /payments/transactions/{id}/cancel` | chủ giao dịch / Staff | `{}` | giao dịch `Cancelled` |
| Hỏi lại trạng thái | `POST /payments/transactions/{id}/query` | chủ giao dịch / Staff | `{}` | giao dịch cập nhật |
| Giả lập nhà cung cấp (demo) | `POST /payments/transactions/{id}/simulate` | chủ giao dịch / Staff | `{ "outcome": "success", "notify": true }` | giao dịch; `403 payment.simulation_disabled` nếu tắt |
| Yêu cầu hoàn tiền | `POST /payments/transactions/{id}/refunds` | Cashier, Manager, Admin | `{ "kind": "Cancellation", "amount": null, "reason": "Khách hủy" }` (`Cancellation\|NoShow\|Exception`; Exception cần `amount`) | 201 `{ "id", "paymentId", "orderId", "amount", "kind", "status": "Succeeded\|PendingApproval", "withinPolicy": true, … }` |
| Hoàn tiền chờ duyệt | `GET /payments/refunds?status=PendingApproval`, `GET /payments/refunds/{id}` | Cashier, Manager, Admin | | mảng hoàn tiền |
| Duyệt / từ chối | `POST /payments/refunds/{id}/approve` `{}`; `POST …/reject` `{ "note": "Lý do" }` | Manager, Admin | | hoàn tiền (`Succeeded` / `Rejected`) |
| Chính sách hoàn tiền | `GET /payments/policies/cancellation` (đăng nhập), `PUT` (BrandAdmin) | | `{ "cancelRefundPercent": 100, "noShowAfterMinutes": 30, "noShowRefundPercent": 0, "unpaidOrderTimeoutMinutes": 15, "autoApproveWithinPolicy": true, "exceptionApprover": "BranchManager" }` | cùng trường + `isDefault` |
| Tài khoản nhận tiền | `GET/PUT /payments/merchant-settings`, `DELETE /{id}` (BrandAdmin) | | `{ "branchId": null, "providerCode": "VietQR", "isEnabled": true, "accountName": "CONG TY DEMO", "bankCode": "vcb", "accountNumber": "0123456789012", "merchantCode": null }` | số tài khoản chỉ trả `accountNumberHint: "••••9012"` |
| Cổng thanh toán (SuperAdmin) | `GET/POST /payments/providers`, `PUT /{id}`, `PATCH /{id}/status` | | `{ "code": "VietQR", "displayName": "VietQR", "mode": "Sandbox", "merchantId": "P1", "apiKey": "…", "secretKey": "…", "callbackUrl": null }`; status `{ "isEnabled": true }` | khóa không bao giờ trả về |
| Cổng đang bật | `GET /payments/providers/enabled` | đăng nhập | | `[ { "code", "displayName", "mode" } ]` |
| Webhook nhà cung cấp | `POST /payments/webhooks/{provider}` | công khai + chữ ký | `{ "eventId": "evt-1", "transactionRef": "…", "status": "success", "amount": 99000, "signature": "<HMAC-SHA256 hex>" }` | `{ "outcome": "applied", "duplicate": false }` (`applied\|applied_late\|ignored_final\|amount_mismatch\|unknown_payment`); chữ ký sai `401` |
| Đối soát | `GET /payments/reconciliation?branchId=&from=&to=`; `GET /payments/reconciliation/auto?…`; `POST /payments/reconciliation/orders` | Manager, Admin | `POST`: `{ "branchId": "…", "orders": [ { "orderId": "…", "amount": 99000, "isPaid": true } ] }` | `{ "orders", "matched", "discrepancies": [ { "code", "message", "paymentId", "orderId", "amount" } ] }` |

Trạng thái giao dịch: `Pending | Succeeded | Failed | Expired | Cancelled | PartiallyRefunded | Refunded`. FE nên hỏi lại `GET /orders/{id}/payment` (hoặc `GET /payments/transactions/{id}`) cho đến khi `Succeeded`, hoặc nghe SignalR.

---

## 7. Bếp / pha chế (`AICOS.KitchenOperations.Api`, prefix `kitchen`) [SCRUM-55, 56, 58]

| Chức năng | Method và endpoint | Quyền | Body | Response |
|---|---|---|---|---|
| Hàng chờ | `GET /kitchen/queue?branchId=` | Staff (BrandAdmin gửi `branchId`) | | mảng `{ "id", "orderId", "orderCode", "tableNumber", "orderType", "status": "Pending", "isPaid", "isPriority", "scheduledPickupTimeUtc", "totalCups", "items": [ { "productName", "quantity", "note" } ], "stickers": [ … ] }` |
| Gộp mẻ | `GET /kitchen/batches?branchId=` | Staff | | `[ { "productName", "totalQuantity", "maxBatchCapacity": 4, "cups": [ … ] } ]` |
| Chi tiết ticket | `GET /kitchen/tickets/{id}` | Staff | | ticket |
| Đổi trạng thái | `PUT /kitchen/tickets/{id}/status` | Barista, Manager, Admin | `{ "status": "Brewing" }` → `ReadyForPickup` → `Completed`; `Cancelled` chỉ Manager/Admin | ticket |
| Tem ly | `GET /kitchen/tickets/{id}/stickers` | Barista, Manager, Admin | | `{ "ticketId", "orderCode", "totalStickers", "stickers": [ { "productName", "cupIndex", "totalCupsInOrder", "customizations" } ] }` |

Ticket do hệ thống tự tạo từ đơn (FE **không** tạo ticket). Đơn mang đi chỉ hiện sau khi đã trả tiền; đơn tại bàn hiện ngay.

**SignalR realtime [SCRUM-58]**: kết nối `ws(s)://{gateway}/hubs/kitchen?access_token=<JWT>`.
```js
const conn = new signalR.HubConnectionBuilder().withUrl(`${base}/hubs/kitchen`, { accessTokenFactory: () => token }).withAutomaticReconnect().build();
conn.on("ReceiveKitchenTicket", t => { /* ticket mới vào hàng chờ */ });
conn.on("TicketStatusChanged", e => { /* { ticketId, status } */ });
conn.on("OrderReadyForPickup", n => { /* màn hình thu ngân: { ticketId, orderId, orderCode, tableNumber, message } */ });
conn.on("CupStickersPrinted", s => { /* mảng tem cần in */ });
await conn.start();
await conn.invoke("JoinKitchenGroup", branchId);  // Barista/Manager/Admin
await conn.invoke("JoinPosGroup", branchId);      // Cashier/Manager/Admin
```
Nhân viên chỉ vào được nhóm của chi nhánh mình; không token `401`.

---

## 8. Nhân sự, chấm công, lương, chốt két (`AICOS.WorkforceScheduling.Api`, prefix `workforce`) [SCRUM-125 đến 129, 46, 51, 62, 63]

| Chức năng | Method và endpoint | Quyền | Body mẫu | Response |
|---|---|---|---|---|
| Hồ sơ nhân sự | `GET/POST /workforce/employees`, `PUT /{id}`, `PATCH /{id}/status`, `GET /employees/me` | Manager/Admin | `{ "userId": "…", "contractType": "FullTime", "position": "Thu ngân", "hireDate": "2026-01-01", "branchId": null }` | `{ "id", "branchId", "userId", "fullName", "role", "position", "hireDate", "contractType", "status" }` |
| Ca mẫu | `GET/POST /workforce/work-shifts`, `PUT /{id}`, `PATCH /{id}/status` | Manager/Admin ghi | `{ "branchId": null, "name": "Ca sáng", "startTime": "07:00", "endTime": "12:00" }` | `{ "id", "branchId", "name", "startTime", "endTime", "hours", "isActive" }` |
| Xếp ca | `POST /workforce/shift-assignments` | Manager/Admin | `{ "employeeId": "…", "workShiftId": "…", "workDate": "2026-11-02" }` | phân công `{ "id", "employeeId", "employeeName", "workDate", "startTime", "endTime", "status", "checkInAtUtc", "checkOutAtUtc", "reviewStatus", … }` |
| Lịch của tôi | `GET /workforce/shift-assignments/mine` | Staff | | mảng phân công |
| Xác nhận / từ chối ca | `POST /shift-assignments/{id}/confirm`, `…/decline` | Staff | decline: `{ "reason": "Ốm" }` | phân công |
| Check-in / check-out [SCRUM-63] | `POST /workforce/attendance/check-in`, `…/check-out` | Staff | `{ "assignmentId": "…" }` | phân công đã cập nhật |
| Báo cáo chấm công [SCRUM-51] | `GET /workforce/attendance/report?branchId=&from=&to=` | Manager/Admin | | `{ "from", "to", "rows": [ … ], "summary": [ … ] }` |
| Duyệt công | `POST /attendance/approve-range` `{ "branchId": "…", "from": "2026-11-02", "to": "2026-11-08" }`; `POST /attendance/{id}/approve` `{ "note": "OK" }` | Manager/Admin | | |
| Sửa giờ / ghi vắng | `POST /attendance/{id}/adjust` `{ "checkInAt": "2026-11-02T07:00:00+07:00", "checkOutAt": "2026-11-02T12:00:00+07:00", "reason": "Quên chấm ra" }`; `POST /attendance/{id}/absent` `{ "reason": "Nghỉ không phép" }` | Manager/Admin | | |
| Mở ca thay [SCRUM-127] | `POST /workforce/replacement-shifts` | Manager/Admin | `{ "assignmentId": "…", "reason": "Chi ốm" }` | ca thay |
| Đăng ký ca thay [SCRUM-128] | `POST /replacement-shifts/{id}/register` `{ "note": "Em nhận" }`; `POST …/withdraw` | Staff | | |
| Duyệt ca thay | `GET /replacement-shifts/{id}/applications`; `POST …/approve` `{ "applicationId": "…", "note": null }`; `POST …/cancel` | Manager/Admin | | |
| Pay rules [SCRUM-126] | `GET /workforce/pay-rules` (Manager/Admin), `PUT` (BrandAdmin) | | `{ "fullTimeDayRate": 300000, "partTimeHourRate": 30000, "replacementHourRate": 45000, "overtimeAfterHours": 8, "overtimeMultiplier": 1.5 }` | cùng trường + `isConfigured` |
| Bảng lương [SCRUM-129] | `GET /workforce/payroll/summary?branchId=&from=2026-11-02&to=2026-11-08` | Manager/Admin | | `{ "from", "to", "rules": {…}, "lines": [ { "employeeId", "employeeName", "contractType", "workedDays", "regularHours", "overtimeHours", "replacementHours", "absences", "unreviewedRecords", "basePay", "overtimePay", "replacementPay", "adjustments", "totalPay" } ], "totals": { "basePay", "overtimePay", "replacementPay", "adjustments", "totalPay", "unreviewedRecords" } }` |
| Xuất lương / bảng công | `GET /payroll/export?…&format=xlsx` (bỏ `format` = CSV), `GET /payroll/records/export?…&format=xlsx` | Manager/Admin | | file tải về |
| Thưởng / trừ lương | `GET/POST /payroll/adjustments`, `DELETE /payroll/adjustments/{id}` | Manager/Admin | `{ "employeeId": "…", "workDate": "2026-11-02", "amount": 50000, "reason": "Thưởng" }` (âm = trừ) | `{ "id", "employeeName", "workDate", "amount", "reason", … }` |
| Chốt két [SCRUM-62] | `POST /workforce/cash-closings` | Cashier | `{ "assignmentId": "…", "countedAmount": 99000, "note": "Đủ" }` | `{ "id", "expectedAmount", "countedAmount", "difference", "status": "PendingReview", … }` |
| Chốt két của tôi / danh sách | `GET /cash-closings/mine`; `GET /cash-closings?status=&withDifference=&from=&to=&page=&pageSize=` (Manager/Admin) | | | mảng / phân trang |
| Chi tiết chốt ca [SCRUM-49] | `GET /workforce/cash-closings/{id}/details` | Manager/Admin | | `{ "closing": {…}, "cashReceived", "cashRefunded", "cashTransactions": [ { "orderId", "amount", "occurredAtUtc", "isRefund" } ], "orderExceptions": [ { "orderCode", "finalAmount", "refundStatus", … } ], "unresolvedExceptions": 0 }` |
| Xác nhận chốt ca | `POST /workforce/cash-closings/{id}/confirm` | Manager/Admin | `{ "note": "OK", "acknowledgeExceptions": false }` | closing `status: "Confirmed"`; `422 cash_closing.note_required` nếu có chênh lệch mà không ghi chú |

---

## 9. Báo cáo (`AICOS.ReportingAnalytics.Api`, prefix `reporting`) [SCRUM-50]

Tham số chung: `from`, `to` (yyyy-MM-dd, mặc định hôm nay, tối đa 93 ngày, tính theo múi giờ cửa hàng). Quyền: BrandAdmin, BranchManager (Manager chỉ chi nhánh mình).

| Chức năng | Endpoint | Response |
|---|---|---|
| Báo cáo chuỗi (chỉ BrandAdmin) | `GET /reporting/chain?from=&to=` | `{ "from", "to", "totals": { "orders", "paidOrders", "grossRevenue", "refunded", "netRevenue", "averageOrderValue" }, "branches": [ { "branchId", "branchName", "orders", "paidOrders", "grossRevenue", "refunded", "netRevenue", "averageOrderValue", "revenueShare" } ] }` |
| Báo cáo chi nhánh | `GET /reporting/branch?branchId=&from=&to=` | `{ "branchId", "branchName", "totals": {…}, "paymentMethods": [ { "method", "orders", "grossRevenue", "refunded", "netRevenue", "share" } ], "orderTypes": [ { "orderType", "orders" } ], "hours": [ { "hour": 0, "orders": 0 } … 24 mục ], "peakHours": [ { "hour", "orders" } ], "days": [ { "date", "orders", "grossRevenue" } ] }` |
| Theo phương thức thanh toán | `GET /reporting/payment-methods?branchId=&from=&to=` | mảng như `paymentMethods` (BrandAdmin không gửi `branchId` = cả chuỗi) |
| Giờ cao điểm | `GET /reporting/peak-hours?branchId=&from=&to=` | mảng 24 mục `{ "hour", "orders" }` |
| Xuất CSV | `GET /reporting/chain/export?…`, `GET /reporting/branch/export?…` | file `.csv` |

Doanh thu tính theo ngày tiền về; số đơn theo ngày đặt. Số liệu cập nhật sau 1 đến 2 giây kể từ khi có thanh toán.

---

## 10. Chưa có / lưu ý cho FE

- Khách tự đặt món online và chọn khung giờ lấy hàng (SCRUM-80): chưa có API; vai Customer hiện chỉ xem đơn của mình (`GET /orders/mine`).
- Guest QR (SCRUM-66 đến 117) hiện chạy trong RAM, chưa dùng chung bảng đơn: chưa nối với hàng chờ pha chế và thanh toán.
- Momo/ZaloPay/VietQR là mô phỏng: `checkoutUrl` của Momo/ZaloPay là link giữ chỗ; VietQR trả URL ảnh QR chuyển khoản thật.
- Gợi ý phản hồi AI (SCRUM-52), email quên mật khẩu thật, PDF bảng lương: chưa có.

### Bổ sung theo góp ý của FE (đợt 1)

| Việc | Method và endpoint | Quyền | Response |
|---|---|---|---|
| Bảng giá landing (không cần đăng nhập) | `GET /identity/public/subscription-plans` | Public | mảng `[ { "id", "name", "tier", "fee", "billingPeriod", "maxBranches", "maxStaffAccounts", "description" } ]`, chỉ gói đang hoạt động, sắp xếp theo giá. Đường dẫn quản trị `GET /identity/subscription-plans` vẫn chỉ cho SuperAdmin. |
| Tiền mặt dự kiến trước khi chốt két | `GET /workforce/cash-closings/expected?assignmentId=` | Cashier (ca của chính mình) | `{ "assignmentId", "expectedAmount", "cashReceived", "cashRefunded", "sinceUtc", "asOfUtc" }`. Cùng số với lúc nộp chốt két. Lỗi: 400 thiếu `assignmentId`, 403 `cash_closing.cashier_only`, 422 `cash_closing.shift_not_worked`, 404 ca của người khác, 409 `cash_closing.duplicate`. |
| Thời gian khóa tài khoản | `POST /identity/auth/login` khi bị khóa | Public | 403 `account.temporarily_locked`, thêm `lockedUntilUtc` (giờ UTC) và `retryAfterSeconds` (cũng có header `Retry-After`). FE dùng để đếm ngược. |

### Đặt món mang đi của khách, khách tự hủy đơn, đánh giá đơn (giai đoạn A)

Tất cả theo brand (tenant lấy từ token). Giờ lấy món theo múi giờ cửa hàng (`Asia/Ho_Chi_Minh`), mặc định mở 07:00 đến 22:00 (cấu hình `Ordering:Pickup:OpenTime/CloseTime`). Luật khung giờ do Quản lý/Admin đặt ở `PUT /orders/pickup-slots` (mặc định: 15 phút một khung, 10 đơn một khung, khóa 15 phút trước giờ, đặt trước 1 ngày).

| Chức năng | Method và endpoint | Quyền | Body / Response |
|---|---|---|---|
| Xem khung giờ trong ngày | `GET /orders/pickup-slots/available?branchId=&date=yyyy-MM-dd` | Đã đăng nhập | `{ "branchId", "date", "slotMinutes", "capacityPerSlot", "cutoffMinutes", "slots": [ { "startUtc", "endUtc", "capacity", "booked", "remaining", "locked", "available" } ] }`. `available=false` khi khung đã đầy hoặc đã khóa. Ngoài cửa sổ đặt trước thì `slots` rỗng. |
| Đặt món mang đi | `POST /orders/pickup` | Customer | Header tùy chọn `Idempotency-Key` (tối đa 80 ký tự). Body `{ "branchId", "slotStartUtc", "promotionCode"?, "note"?, "items": [ { "productId", "quantity", "note"? } ] }`. 201 trả như `OrderResponse` kèm `pickupSlotStartUtc`, `paymentDueAtUtc`. Gửi lại cùng `Idempotency-Key` thì 200 và trả đơn cũ. Giá luôn lấy từ Menu. |
| Thanh toán đơn | `POST /payments/transactions` `{ "orderId", "branchId", "amount", "providerCode" }` | Customer | như mục thanh toán. Phải trả trước `paymentDueAtUtc` (mặc định 15 phút) nếu không đơn tự hủy, khung giờ và voucher được trả lại. |
| Khách tự hủy đơn | `POST /orders/{id}/cancel` `{ "reason"? }` | Customer (đơn của mình) | Đơn chưa trả: hủy ngay. Đơn đã trả: hoàn tiền theo chính sách hủy của brand (`refundStatus = RefundPending` rồi `Refunded`). |
| Đơn của tôi | `GET /orders/mine` | Customer | danh sách, có `pickupSlotStartUtc` |
| Đánh giá đơn | `POST /orders/{id}/feedback` `{ "rating": 1-5, "comment"? }` | Customer (đơn của mình, đã `Completed`) | 201 `{ "id", "orderId", "orderCode", "branchId", "customerId", "rating", "comment", "createdAtUtc" }`. Mỗi đơn một đánh giá. |
| Xem đánh giá của đơn | `GET /orders/{id}/feedback` | Chủ đơn hoặc nhân viên chi nhánh | như trên, 404 nếu chưa có |
| Danh sách đánh giá | `GET /orders/feedbacks?branchId=&minRating=&maxRating=&limit=` | Manager, Admin | mảng đánh giá, mới nhất trước (Quản lý chi nhánh chỉ thấy chi nhánh mình) |

Luồng của đơn mang đi: `Pending` (chưa trả) -> thanh toán thành công -> `Confirmed` -> quầy bar thấy phiếu -> `Preparing` -> `Ready` -> nhân viên `POST /orders/{id}/deliver` -> `Completed`.

Lỗi nên xử lý:

| Mã | Ý nghĩa |
|---|---|
| 422 `pickup.invalid_slot` | khung giờ không nằm trên lưới / ngoài giờ mở cửa |
| 422 `pickup.slot_locked` | đã quá giờ chốt đặt (BR-23) |
| 422 `pickup.too_far_ahead` | đặt trước quá số ngày cho phép |
| 409 `pickup.slot_full` | khung đã đầy (BR-22), voucher chưa bị dùng |
| 422 `order.item_unavailable` | món hết hoặc không bán ở chi nhánh |
| 422 `order.cannot_cancel_now` | quầy bar đã bắt đầu làm, khách không tự hủy được |
| 422 `order.pickup_locked` | khung giờ đã khóa, khách không tự hủy được (BR-24) |
| 422 `refund.nothing_to_refund` | chính sách brand không hoàn tiền cho trường hợp này, đơn giữ nguyên |
| 422 `feedback.order_not_completed` | chưa nhận hàng |
| 409 `feedback.duplicate` | đã đánh giá |
| 403 `refund.customer_policy_only` | khách chỉ xin được hoàn tiền theo chính sách (loại Cancellation, không nêu số tiền) |

Thay đổi liên quan: `GET /identity/branches/{id}` nay cho mọi người dùng đã đăng nhập trong brand (khách chọn chi nhánh để đặt). Quản lý chi nhánh vẫn chỉ xem chi nhánh của mình.
