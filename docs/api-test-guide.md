# Hướng dẫn test API – Giai đoạn 1, 2, 3

Tài liệu này dùng để test bằng **Swagger** các API đã viết cho AICOS. Mỗi giai đoạn gồm 4 phần:

1. **Code nằm ở đâu** – service nào, file nào.
2. **Luồng test** – bấm API nào trước, API nào sau.
3. **Validation rules** – dữ liệu thế nào thì bị từ chối.
4. **Test case** – JSON copy-paste được + kết quả mong đợi.

> Đường dẫn file trong tài liệu tính từ thư mục `AICOS-BE/`.

---

## 0. Chuẩn bị chung (làm 1 lần)

### 0.1 Bật hạ tầng và các service

```powershell
docker compose up -d postgres redis
```

Mỗi service chạy ở **một terminal riêng** (lần chạy đầu tự tạo bảng nhờ `Database__MigrateOnStartup=true` trong `launchSettings.json`):

| Service | Lệnh chạy | Swagger |
|---|---|---|
| Identity | `dotnet run --project src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api` | http://localhost:5101/swagger |
| Menu | `dotnet run --project src/Services/MenuCatalog/AICOS.MenuCatalog.Api` | http://localhost:5102/swagger |
| Table | `dotnet run --project src/Services/TableReservation/AICOS.TableReservation.Api` | http://localhost:5103/swagger |
| Inventory | `dotnet run --project src/Services/InventoryPurchasing/AICOS.InventoryPurchasing.Api` | http://localhost:5106/swagger |
| Workforce | `dotnet run --project src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api` | http://localhost:5108/swagger |
| Customer | `dotnet run --project src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api` | http://localhost:5110/swagger |

Chỉ cần bật service đang test. Ngoại lệ:
- Test **Recipe** (Inventory) cần bật thêm **Menu** (Inventory gọi REST sang Menu).
- Test **hồ sơ khách hàng** (Customer) cần bật **Identity + Customer + Redis** (Identity bắn event qua Redis).

### 0.2 Token dùng chung cho mọi service

Token do Identity phát được **mọi service chấp nhận** (cùng khoá JWT). Lấy token ở Swagger Identity rồi dán vào nút **Authorize** ở Swagger của service đang test.

> Dán **chỉ phần `eyJ...`** – không có chữ `Bearer`, không có dấu `"`.

### 0.3 Tạo bộ tài khoản test (ở Swagger Identity – 5101)

1. `POST /api/v1/identity/auth/login`
   ```json
   { "login": "admin", "password": "Admin@12345" }
   ```
   → Authorize bằng token admin.
2. `POST /api/v1/identity/branches` → copy `id` = **BRANCH_ID**
   ```json
   { "name": "AICOS Quận 1", "address": "1 Lê Lợi", "phone": "0901234567", "openingTime": "07:00", "closingTime": "22:00" }
   ```
3. `POST /api/v1/identity/users` – tạo 3 nhân viên (copy `id` của từng người, sẽ cần ở Workforce):
   ```json
   { "fullName": "Hoàng Minh", "email": "manager1@example.com", "username": "manager1", "password": "Temp1234", "role": "Manager", "branchId": "BRANCH_ID" }
   ```
   ```json
   { "fullName": "Phạm Thu Thủy", "email": "cashier1@example.com", "username": "cashier1", "password": "Temp1234", "role": "Cashier", "branchId": "BRANCH_ID" }
   ```
   ```json
   { "fullName": "Lê Văn Kho", "email": "kho1@example.com", "username": "kho1", "password": "Temp1234", "role": "WarehouseStaff", "branchId": "BRANCH_ID" }
   ```
4. Đăng nhập từng người để lấy token: `{ "login": "manager1", "password": "Temp1234" }` (tương tự `cashier1`, `kho1`).

| Token | Dùng để |
|---|---|
| **TOKEN_ADMIN** | Quản trị toàn chuỗi. Ở các service khác **phải gửi kèm `branchId`** (body hoặc query) |
| **TOKEN_MANAGER** | Quản lý 1 chi nhánh – `branchId` tự lấy từ token, không cần gửi |
| **TOKEN_CASHIER** | Thu ngân |
| **TOKEN_WAREHOUSE** | Nhân viên kho |
| **TOKEN_CUSTOMER** | Khách (tự đăng ký ở `POST /auth/register`) |

### 0.4 Quy ước lỗi chung (mọi service)

Mọi lỗi trả về dạng ProblemDetails, có `errorCode` để đối chiếu:

```json
{ "title": "Validation failed", "status": 400, "errorCode": "validation_failed", "errors": { "Name": ["Category name is required."] } }
```

| Mã | Ý nghĩa |
|---|---|
| 400 | Dữ liệu sai (`errors` liệt kê từng field) hoặc JSON hỏng (`malformed_request`) |
| 401 | Không có token / token sai / hết hạn |
| 403 | Sai role, hoặc `branch.forbidden` (đụng dữ liệu chi nhánh khác – BR-58) |
| 404 | Không tìm thấy |
| 409 | Trùng dữ liệu / xung đột (trùng tên, trùng giờ...) |
| 422 | Vi phạm nghiệp vụ (BR-xx, sai trạng thái, không đủ tồn kho...) |
| 503 | Service phụ thuộc không phản hồi (`dependency_unavailable`) |

Quy tắc phạm vi chi nhánh (BR-58) áp dụng cho Menu, Inventory, Table, Workforce, Voucher:
- **Manager / Cashier / WarehouseStaff**: luôn làm việc trên chi nhánh trong token. Gửi `branchId` khác → **403 `branch.forbidden`**.
- **SystemAdmin**: phải gửi `branchId` khi tạo mới (thiếu → **400**). Khi xem danh sách có thể bỏ `branchId` để xem mọi chi nhánh.

---

# GIAI ĐOẠN 1 – Nền tảng (Identity)

## 1.1 Code nằm ở đâu

**Service: Identity & Organization** – `src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/` – schema `identity` – port 5101

| Chức năng | File |
|---|---|
| Entity | `Domain/Branch.cs`, `Domain/User.cs`, `Domain/UserSession.cs`, `Domain/OtpCode.cs` |
| CRUD chi nhánh (UC-03) | `Features/Branches/BranchEndpoints.cs`, `Features/Branches/BranchContracts.cs` |
| Đăng ký, đăng nhập, refresh, logout, me, đổi/quên mật khẩu | `Features/Auth/AuthEndpoints.cs`, `Features/Auth/AuthContracts.cs` |
| Admin quản lý tài khoản (UC-02) | `Features/Users/UserEndpoints.cs`, `UserContracts.cs`, `AccountRules.cs` |
| Rule username / mật khẩu | `Features/Common/InputRules.cs` |
| Hash mật khẩu, phát token, gửi email (dev) | `Infrastructure/Passwords.cs`, `AuthTokenIssuer.cs`, `EmailSender.cs`, `AuthOptions.cs` |
| Tạo admin đầu tiên | `Persistence/IdentitySeeder.cs` |
| Token dev (chỉ Development) | `Features/Dev/DevTokenEndpoints.cs` |
| Cấu hình DB | `Persistence/IdentityOrganizationDbContext.cs`, `Persistence/Configurations/*.cs`, `Persistence/Migrations/` |
| Test tự động | `tests/AICOS.IdentityOrganization.Tests/` |

Dùng chung (BuildingBlocks): `src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/AuthenticationExtensions.cs` (xác thực JWT), `Http/GlobalExceptionHandler.cs`, `Http/TimeOnlyJsonConverter.cs`, `Validation/ValidationErrors.cs`, `Security/BranchAccess.cs`, `Persistence/Paging.cs`.
Event: `src/BuildingBlocks/AICOS.Contracts/Events/IdentityEvents.cs` (`CustomerRegisteredEvent`).

## 1.2 Luồng test

**Luồng A – Chi nhánh (Admin)**
`POST /auth/login` (admin) → **Authorize** → `POST /branches` → `GET /branches` (tìm kiếm) → `GET /branches/{id}` → `PUT /branches/{id}` (sửa thông tin) → `PATCH /branches/{id}/status` (tạm ngưng / kích hoạt) → `GET /branches/active` (không cần token).

**Luồng B – Khách hàng**
`POST /auth/register` → `POST /auth/login` → **Authorize** → `GET /auth/me` → `POST /auth/refresh` (đổi token) → `POST /auth/change-password` → `POST /auth/logout`.

**Luồng C – Quên mật khẩu**
`POST /auth/forgot-password` → mở **console của Identity**, tìm dòng `[DEV EMAIL - not really sent] ... reset code is 123456` → `POST /auth/reset-password` → `POST /auth/login` bằng mật khẩu mới.

**Luồng D – Admin quản lý tài khoản**
Login admin → `POST /users` (Cashier có chi nhánh) → `GET /users?role=cashier` → `GET /users/{id}` → `PUT /users/{id}` (đổi role) → `PATCH /users/{id}/status` (khoá / mở) → `DELETE /users/{id}` (xoá mềm).

**Luồng E – Khoá tài khoản (BR-02)**
Đăng nhập sai 5 lần liên tiếp → lần 5 trả 403 `account.temporarily_locked` → đăng nhập đúng vẫn 403 trong 15 phút → đặt lại mật khẩu (Luồng C) sẽ mở khoá.

## 1.3 Validation rules

**Chi nhánh** (`POST/PUT /branches`) – chỉ SystemAdmin

| Field | Rule | Lỗi |
|---|---|---|
| name | Bắt buộc, ≤ 255 ký tự, không trùng (không phân biệt hoa thường) | 400 / 409 `branch.duplicate_name` |
| address | ≤ 500 ký tự | 400 |
| phone | Không bắt buộc; 8–15 chữ số, có thể bắt đầu bằng `+`, không dấu cách/gạch | 400 |
| openingTime / closingTime | Có cả hai hoặc bỏ cả hai; khác nhau; dạng `HH:mm`; cho phép qua đêm (18:00–02:00) | 400 |
| status (PATCH) | `Active` hoặc `Suspended`. Không có API xoá (BR-38) | 400 |

Manager chỉ được `GET /branches/{id}` của chi nhánh mình (khác → 403).

**Mật khẩu / email / username dùng chung**

| Field | Rule |
|---|---|
| email | Bắt buộc, ≤ 255, dạng `a@b.c`, tự đổi chữ thường, không trùng (kể cả tài khoản đã xoá) |
| username | Không bắt buộc; 3–50 ký tự, bắt đầu bằng chữ, chỉ chữ/số/`.`/`_`; không trùng |
| phone | Như chi nhánh |
| fullName | Bắt buộc, ≤ 255 |
| mật khẩu mới | 8–100 ký tự, ≥ 1 chữ cái và ≥ 1 chữ số |
| xác nhận | Phải giống mật khẩu |

**Riêng từng API**

| API | Rule đặc biệt |
|---|---|
| `POST /auth/register` | Role luôn là Customer (gửi `role` cũng bị bỏ qua). Trùng email → 409 `user.duplicate_email` |
| `POST /auth/login` | Sai → 401 `auth.invalid_credentials` (báo số lần còn lại); lần sai thứ 5 → 403 `account.temporarily_locked` (15 phút); admin khoá → 403 `account.locked`; chi nhánh bị tạm ngưng → 403 `branch.suspended` |
| `POST /auth/refresh` | Token dùng rồi / hết hạn / đã logout → 401 `auth.invalid_refresh_token` (token xoay vòng mỗi lần refresh) |
| `POST /auth/logout` | Luôn 204 |
| `POST /auth/change-password` | Sai mật khẩu hiện tại → 400; mật khẩu mới phải khác cũ; thành công → đăng xuất mọi thiết bị |
| `POST /auth/forgot-password` | Email sai dạng → 400; còn lại luôn 202 cùng một câu (không lộ tài khoản); gửi lại trong 60 giây thì không gửi mã mới |
| `POST /auth/reset-password` | `code` đúng 6 số (400); sai / hết hạn 15 phút / đã dùng / sai 5 lần → 422 `auth.reset_code_invalid` |
| `POST/PUT /users` | Role ∈ SystemAdmin, Manager, Cashier, WarehouseStaff, Customer. Manager/Cashier/WarehouseStaff **bắt buộc** `branchId` (chi nhánh tồn tại và Active); SystemAdmin/Customer **không được** có `branchId`. Trùng username → 409 `user.duplicate_username`. Tài khoản mới có `mustChangePassword: true` |
| Tự thao tác | Admin không được đổi role / khoá / xoá chính mình → 409 `user.self_action` |

## 1.4 Test case

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 1-01 | POST /branches | ADMIN | `{ "name": "AICOS Thủ Đức", "phone": "0901234567", "openingTime": "07:00", "closingTime": "22:00" }` | **201** |
| 1-02 | POST /branches | ADMIN | `{ "name": "   ", "phone": "09-abc", "openingTime": "07:00" }` | **400** – `errors` có Name, Phone, OpeningTime |
| 1-03 | POST /branches | ADMIN | `{ "name": "aicos thủ đức" }` | **409** `branch.duplicate_name` |
| 1-04 | POST /branches | CASHIER | `{ "name": "Hack" }` | **403** |
| 1-05 | PATCH /branches/{id}/status | ADMIN | `{ "status": "Suspended" }` | **200** |
| 1-06 | POST /auth/register | – | `{ "fullName": "Nguyễn Văn An", "email": "an@example.com", "phone": "0901234567", "password": "Secret123", "confirmPassword": "Secret123" }` | **201**, role Customer |
| 1-07 | POST /auth/register | – | `{ "fullName": "", "email": "abc", "phone": "12", "password": "1", "confirmPassword": "2" }` | **400** – 5 field lỗi |
| 1-08 | POST /auth/register | – | `{ "fullName": "An 2", "email": "AN@example.com", "password": "Secret123", "confirmPassword": "Secret123" }` | **409** `user.duplicate_email` |
| 1-09 | POST /auth/login | – | `{ "login": "an@example.com", "password": "Wrong1234" }` | **401**, "4 attempt(s) left" |
| 1-10 | POST /auth/refresh (gửi 2 lần cùng token) | – | `{ "refreshToken": "..." }` | lần 1 **200**, lần 2 **401** |
| 1-11 | POST /users | ADMIN | `{ "fullName": "NV", "email": "x1@example.com", "password": "Temp1234", "role": "Cashier" }` | **400** `errors.BranchId` |
| 1-12 | POST /users | ADMIN | `{ "fullName": "X", "email": "x2@example.com", "password": "Temp1234", "role": "Owner" }` | **400** `errors.Role` |
| 1-13 | PATCH /users/{id}/status | ADMIN | `{ "status": "Locked" }` rồi user đó login | **200**, login **403** `account.locked` |
| 1-14 | POST /auth/reset-password | – | `{ "email": "an@example.com", "code": "000000", "newPassword": "Brand1234", "confirmNewPassword": "Brand1234" }` | **422** `auth.reset_code_invalid` |
| 1-15 | GET /users | CASHIER | – | **403** |

---

# GIAI ĐOẠN 2 – CRUD danh mục

## 2.1 Code nằm ở đâu

**Menu Service** – `src/Services/MenuCatalog/AICOS.MenuCatalog.Api/` – schema `menu` – port 5102

| Chức năng | File |
|---|---|
| Entity Category, Product, OptionGroup, ProductOption | `Domain/Catalog.cs` |
| Category (UC-06) | `Features/Categories/CategoryEndpoints.cs` |
| Product + tuỳ chọn size/topping (UC-06) | `Features/Products/ProductEndpoints.cs`, `Features/Products/ProductContracts.cs` |
| Menu công khai không cần đăng nhập (UC-47) | `Features/PublicMenu/PublicMenuEndpoints.cs` |
| DB | `Persistence/MenuCatalogDbContext.cs`, `Persistence/Configurations/CatalogConfigurations.cs`, `Persistence/Migrations/` |

**Inventory Service** – `src/Services/InventoryPurchasing/AICOS.InventoryPurchasing.Api/` – schema `inventory` – port 5106

| Chức năng | File |
|---|---|
| Entity Supplier, Ingredient, RecipeItem | `Domain/Stock.cs` |
| Nhà cung cấp (UC-08) | `Features/Suppliers/SupplierEndpoints.cs` |
| Nguyên liệu + ngưỡng tồn tối thiểu (UC-07) | `Features/Ingredients/IngredientEndpoints.cs` |
| Công thức món (Recipe) | `Features/Recipes/RecipeEndpoints.cs` |
| Gọi REST sang Menu để kiểm tra món | `Infrastructure/MenuClient.cs` |
| DB | `Persistence/InventoryPurchasingDbContext.cs`, `Persistence/Configurations/InventoryConfigurations.cs` |

**Table Service** – `src/Services/TableReservation/AICOS.TableReservation.Api/` – schema `table_reservation` – port 5103

| Chức năng | File |
|---|---|
| Entity DiningTable | `Domain/Tables.cs` |
| Bàn, sơ đồ bàn, QR (UC-38) | `Features/DiningTables/DiningTableEndpoints.cs` |
| DB | `Persistence/TableReservationDbContext.cs` |

**Workforce Service** – `src/Services/WorkforceScheduling/AICOS.WorkforceScheduling.Api/` – schema `workforce` – port 5108

| Chức năng | File |
|---|---|
| Entity Employee, WorkShift | `Domain/Workforce.cs` |
| Hồ sơ nhân viên + ca làm mẫu | `Features/Employees/EmployeeEndpoints.cs` |
| DB | `Persistence/WorkforceSchedulingDbContext.cs` |

Test tự động: `tests/AICOS.Services.IntegrationTests/MenuTests.cs`, `InventoryTests.cs`, `TableTests.cs`, `WorkforceTests.cs`.

## 2.2 Luồng test

**Luồng A – Menu (Swagger 5102, TOKEN_MANAGER)**
`POST /categories` → `GET /categories` → `GET /categories/{id}` → `PUT /categories/{id}` → `POST /products` (dùng categoryId vừa tạo) → `GET /products?search=...` → `GET /products/{id}` → `PUT /products/{id}` (đổi giá, thay tuỳ chọn) → `PATCH /products/{id}/availability` (bằng TOKEN_CASHIER: hết hàng) → `PATCH /categories/{id}/status` (Hidden) → **bỏ Authorize** → `GET /public/branches/{BRANCH_ID}/menu` → `DELETE /products/{id}` → `DELETE /categories/{id}`.

**Luồng B – Nhà cung cấp và nguyên liệu (Swagger 5106, TOKEN_MANAGER)**
`POST /suppliers` → `GET /suppliers?search=...` → `PUT /suppliers/{id}` → `PATCH /suppliers/{id}/status` → `POST /ingredients` → `GET /ingredients` → `PATCH /ingredients/{id}/reorder-level` → `GET /ingredients/low-stock` → `DELETE /ingredients/{id}` (chỉ khi chưa dùng).

**Luồng C – Công thức (cần bật Menu + Inventory)**
Lấy `productId` ở Menu → ở Inventory: `PUT /recipes/{productId}` → `GET /recipes/{productId}` → tắt Menu rồi gọi lại PUT → **503**.

**Luồng D – Bàn (Swagger 5103, TOKEN_MANAGER)**
`POST /dining-tables` → `GET /dining-tables?area=...` → `GET /dining-tables/{id}` → `PUT /dining-tables/{id}` → `PATCH /dining-tables/{id}/status` (TOKEN_CASHIER) → **bỏ Authorize** → `GET /public/qr/{qrToken}` → `POST /dining-tables/{id}/qr/regenerate` → mã QR cũ trả 404 → `DELETE /dining-tables/{id}`.

**Luồng E – Nhân viên và ca mẫu (Swagger 5108, TOKEN_MANAGER)**
`POST /employees` (userId = id của cashier1 ở bước 0.3) → `GET /employees` → `PUT /employees/{id}` → `GET /employees/me` (bằng TOKEN_CASHIER) → `POST /work-shifts` → `GET /work-shifts` → `PUT /work-shifts/{id}` → `PATCH /work-shifts/{id}/status`.

## 2.3 Validation rules

**Category** – đọc: mọi nhân viên; ghi: Admin/Manager

| Field | Rule | Lỗi |
|---|---|---|
| name | Bắt buộc, ≤ 100, không trùng trong chi nhánh (không phân biệt hoa thường) | 400 / 409 `category.duplicate_name` |
| sortOrder | 0–9999 (bỏ trống = 0) | 400 |
| status (PATCH) | `Active` / `Hidden` (Hidden → ẩn cả danh mục khỏi menu công khai) | 400 |
| DELETE | Còn món → 409 `category.has_products` | 409 |

**Product** – đọc + bật/tắt hết hàng: mọi nhân viên; tạo/sửa/xoá: Admin/Manager

| Field | Rule | Lỗi |
|---|---|---|
| categoryId | Bắt buộc, phải thuộc **cùng chi nhánh** | 400 |
| name | Bắt buộc, ≤ 255, không trùng trong chi nhánh | 400 / 409 `product.duplicate_name` |
| description | ≤ 2000 | 400 |
| imageUrl | Không bắt buộc; URL `http(s)://` đầy đủ | 400 |
| basePrice | > 0 và ≤ 100.000.000 | 400 |
| tags | ≤ 10 tag, mỗi tag 1–30 ký tự, không chứa dấu phẩy | 400 |
| optionGroups | ≤ 10 nhóm, tên nhóm không trùng | 400 |
| optionGroups[i].name | Bắt buộc, ≤ 100 | 400 |
| optionGroups[i].selectionType | `Single` (chọn 1) / `Multiple` (chọn nhiều) | 400 |
| optionGroups[i].options | 1–30 lựa chọn, tên không trùng; nhóm `Single` có **tối đa 1** `isDefault: true` | 400 |
| options[j].priceDelta | 0–10.000.000 (bỏ trống = 0) | 400 |
| PUT | Gửi **toàn bộ** danh sách nhóm – nhóm không gửi sẽ bị xoá | |
| DELETE | Xoá mềm: món biến mất khỏi danh sách/menu, GET trả 404 | |

**Supplier** – đọc: Admin/Manager/WarehouseStaff; ghi: Admin/Manager

| Field | Rule | Lỗi |
|---|---|---|
| name | Bắt buộc, ≤ 255, không trùng trong chi nhánh | 400 / 409 `supplier.duplicate_name` |
| phone / email | Đúng định dạng; **phải có ít nhất 1 trong 2** | 400 |
| contactName ≤ 255, address ≤ 500 | | 400 |
| status (PATCH) | `Active` / `Inactive` (không xoá vì đơn mua tham chiếu) | 400 |

**Ingredient** – đọc: Admin/Manager/WarehouseStaff; ghi: Admin/Manager

| Field | Rule | Lỗi |
|---|---|---|
| name | Bắt buộc, ≤ 255, không trùng trong chi nhánh | 400 / 409 `ingredient.duplicate_name` |
| unit | Một trong `g, kg, ml, l, pcs, pack, bottle, box, can` | 400 |
| reorderLevel | 0–10.000.000, **không âm** (BR-14) | 400 |
| PUT đổi unit khi còn tồn kho | | 422 `ingredient.unit_locked` |
| DELETE khi đã có tồn / lịch sử / công thức / đơn mua | | 409 `ingredient.in_use` |
| Tồn kho | **Không sửa trực tiếp** – chỉ thay đổi qua nhập/xuất (giai đoạn 3) | |

**Recipe** – xem: mọi nhân viên; sửa: Admin/Manager

| Rule | Lỗi |
|---|---|
| productId không tồn tại ở Menu | 404 |
| Món thuộc chi nhánh khác | 403 |
| ≤ 50 nguyên liệu, không trùng; quantity > 0 và ≤ 100.000 | 400 |
| Nguyên liệu không thuộc chi nhánh của món | 400 |
| Menu Service tắt / lỗi | 503 `dependency_unavailable` |
| `items: []` | Xoá công thức |

**Dining table** – đọc + đổi trạng thái: mọi nhân viên; tạo/sửa/xoá/QR: Admin/Manager

| Field | Rule | Lỗi |
|---|---|---|
| tableCode | Bắt buộc, ≤ 20, chỉ chữ/số/`-`/`_` (tự viết hoa), không trùng trong chi nhánh | 400 / 409 `table.duplicate_code` |
| capacity | 1–50 | 400 |
| area | ≤ 50 (vd "Tầng 1") | 400 |
| positionX / positionY | 0–10000 (toạ độ vẽ sơ đồ) | 400 |
| status (PATCH) | `Available` / `Occupied` / `OutOfService` | 400 |
| PUT giảm sức chứa < số khách đã đặt sắp tới | | 409 `table.capacity_in_use` |
| DELETE khi đang có khách / có lịch đặt sắp tới | | 409 `table.in_use` |
| DELETE khi đã có lịch sử đặt bàn | | 409 `table.has_history` |
| `GET /public/qr/{token}` | Không cần token; mã QR cũ sau khi regenerate → 404 | |

**Employee** – Admin/Manager (`GET /employees/me`: mọi nhân viên)

| Field | Rule | Lỗi |
|---|---|---|
| userId | Bắt buộc khi tạo (id tài khoản ở Identity); 1 tài khoản chỉ 1 hồ sơ | 400 / 409 `employee.duplicate_user` |
| fullName | Bắt buộc, ≤ 255 | 400 |
| position | ≤ 50 | 400 |
| paymentType | `Hourly` → bắt buộc `hourlyRate` > 0 (≤ 10tr); `Fixed` → bắt buộc `fixedSalary` > 0 (≤ 1 tỷ) | 400 |
| status (PATCH) `Inactive` khi còn ca sắp tới | | 409 `employee.has_upcoming_shifts` |
| `GET /employees/me` khi chưa có hồ sơ | | 404 |

**Work shift (ca mẫu)** – xem: mọi nhân viên; ghi: Admin/Manager

| Field | Rule | Lỗi |
|---|---|---|
| name | Bắt buộc, ≤ 50, không trùng trong chi nhánh | 400 / 409 `shift.duplicate_name` |
| startTime / endTime | Bắt buộc `HH:mm`; giờ kết thúc **sau** giờ bắt đầu (không hỗ trợ ca qua đêm); dài 1–12 giờ | 400 |
| status (PATCH) | `{ "isActive": true/false }` | 400 |

## 2.4 Test case

### Menu (5102)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 2-01 | POST /categories | MANAGER | `{ "name": "Cà phê", "sortOrder": 1 }` | **201** |
| 2-02 | POST /categories | MANAGER | `{ "name": "cà phê" }` | **409** `category.duplicate_name` |
| 2-03 | POST /categories | MANAGER | `{ "name": " ", "sortOrder": -1 }` | **400** Name, SortOrder |
| 2-04 | POST /categories | ADMIN | `{ "name": "Trà" }` | **400** – thiếu `branchId` |
| 2-05 | POST /categories | MANAGER | `{ "branchId": "00000000-0000-0000-0000-000000000001", "name": "Trà" }` | **403** `branch.forbidden` |
| 2-06 | POST /categories | CASHIER | `{ "name": "Trà" }` | **403** |
| 2-07 | POST /products | MANAGER | *(xem JSON 2-07 bên dưới)* | **201**, có 2 nhóm tuỳ chọn |
| 2-08 | POST /products | MANAGER | *(xem JSON 2-08)* | **400** – Name, BasePrice, ImageUrl, OptionGroups[0].Options |
| 2-09 | POST /products | MANAGER | 2-07 nhưng `"name": "bạc xỉu"` | **409** `product.duplicate_name` |
| 2-10 | PATCH /products/{id}/availability | CASHIER | `{ "isAvailable": false }` | **200** |
| 2-11 | GET /public/branches/{BRANCH_ID}/menu | không token | – | **200**, không có món hết hàng / danh mục Hidden |
| 2-12 | DELETE /categories/{id} còn món | MANAGER | – | **409** `category.has_products` |
| 2-13 | DELETE /products/{id} rồi GET lại | MANAGER | – | **204**, sau đó **404** |

JSON **2-07** (thay `CATEGORY_ID`):
```json
{
  "categoryId": "CATEGORY_ID",
  "name": "Bạc xỉu",
  "description": "Cà phê sữa kiểu Sài Gòn",
  "imageUrl": "https://example.com/bac-xiu.jpg",
  "basePrice": 35000,
  "tags": ["best-seller", "cafe"],
  "optionGroups": [
    { "name": "Size", "selectionType": "Single", "isRequired": true,
      "options": [ { "name": "M", "priceDelta": 0, "isDefault": true }, { "name": "L", "priceDelta": 5000, "isDefault": false } ] },
    { "name": "Topping", "selectionType": "Multiple", "isRequired": false,
      "options": [ { "name": "Trân châu", "priceDelta": 5000 }, { "name": "Kem cheese", "priceDelta": 10000 } ] }
  ]
}
```

JSON **2-08** (lỗi: tên rỗng, giá 0, URL sai, nhóm Single có 2 mặc định):
```json
{
  "categoryId": "CATEGORY_ID",
  "name": "",
  "basePrice": 0,
  "imageUrl": "khong-phai-url",
  "optionGroups": [
    { "name": "Size", "selectionType": "Single", "isRequired": true,
      "options": [ { "name": "M", "priceDelta": 0, "isDefault": true }, { "name": "L", "priceDelta": 5000, "isDefault": true } ] }
  ]
}
```

### Inventory (5106)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 2-14 | POST /suppliers | MANAGER | `{ "name": "Công ty Sữa Đà Lạt", "contactName": "Anh Tâm", "phone": "0281234567", "email": "sales@dalatmilk.vn", "address": "Đà Lạt" }` | **201** |
| 2-15 | POST /suppliers | MANAGER | `{ "name": "NCC không liên lạc" }` | **400** – cần phone hoặc email |
| 2-16 | POST /suppliers | MANAGER | `{ "name": "công ty sữa đà lạt", "phone": "0281234567" }` | **409** |
| 2-17 | POST /suppliers | WAREHOUSE | `{ "name": "X", "phone": "0281234567" }` | **403** |
| 2-18 | POST /ingredients | MANAGER | `{ "name": "Sữa tươi", "unit": "l", "reorderLevel": 5 }` | **201**, `quantityOnHand: 0`, `isLowStock: true` |
| 2-19 | POST /ingredients | MANAGER | `{ "name": "Đường", "unit": "liter", "reorderLevel": -1 }` | **400** Unit, ReorderLevel |
| 2-20 | PATCH /ingredients/{id}/reorder-level | MANAGER | `{ "reorderLevel": -2 }` | **400** (BR-14) |
| 2-21 | PUT /recipes/{productId} | MANAGER | `{ "items": [ { "ingredientId": "INGREDIENT_ID", "quantity": 0.15 } ] }` | **200** (Menu đang bật) |
| 2-22 | PUT /recipes/{guid ngẫu nhiên} | MANAGER | như 2-21 | **404** |
| 2-23 | PUT /recipes/{productId} | MANAGER | `{ "items": [ { "ingredientId": "INGREDIENT_ID", "quantity": 0 } ] }` | **400** |
| 2-24 | PUT /recipes/{productId} khi **tắt Menu** | MANAGER | như 2-21 | **503** `dependency_unavailable` |

### Table (5103)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 2-25 | POST /dining-tables | MANAGER | `{ "tableCode": "t01", "capacity": 4, "area": "Tầng 1", "positionX": 100, "positionY": 50 }` | **201**, `tableCode: "T01"`, có `qrToken`, `qrCodeUrl` |
| 2-26 | POST /dining-tables | MANAGER | `{ "tableCode": "T01", "capacity": 2 }` | **409** `table.duplicate_code` |
| 2-27 | POST /dining-tables | MANAGER | `{ "tableCode": "T 02", "capacity": 0 }` | **400** TableCode, Capacity |
| 2-28 | POST /dining-tables | CASHIER | `{ "tableCode": "T03", "capacity": 2 }` | **403** |
| 2-29 | PATCH /dining-tables/{id}/status | CASHIER | `{ "status": "Occupied" }` | **200** |
| 2-30 | GET /public/qr/{qrToken} | không token | – | **200** trả branchId, tableId |
| 2-31 | POST /dining-tables/{id}/qr/regenerate → GET mã cũ | MANAGER | – | **200**, mã cũ **404** |

### Workforce (5108)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 2-32 | POST /employees | MANAGER | `{ "userId": "CASHIER_USER_ID", "fullName": "Phạm Thu Thủy", "position": "Barista", "hireDate": "2026-09-01", "paymentType": "Hourly", "hourlyRate": 25000 }` | **201** |
| 2-33 | POST /employees | MANAGER | `{ "userId": "KHO_USER_ID", "fullName": "Lê Văn Kho", "paymentType": "Hourly" }` | **400** HourlyRate |
| 2-34 | POST /employees | MANAGER | 2-32 lần nữa | **409** `employee.duplicate_user` |
| 2-35 | GET /employees/me | CASHIER | – | **200** hồ sơ của cashier |
| 2-36 | POST /work-shifts | MANAGER | `{ "name": "Ca sáng", "startTime": "06:00", "endTime": "12:00" }` | **201**, `hours: 6` |
| 2-37 | POST /work-shifts | MANAGER | `{ "name": "Ca đêm", "startTime": "22:00", "endTime": "06:00" }` | **400** |
| 2-38 | POST /work-shifts | MANAGER | `{ "name": "ca sáng", "startTime": "07:00", "endTime": "11:00" }` | **409** `shift.duplicate_name` |

---

# GIAI ĐOẠN 3 – Nghiệp vụ có trạng thái

## 3.1 Code nằm ở đâu

**Inventory Service** (5106)

| Chức năng | File |
|---|---|
| Entity PurchaseOrder, StockAdjustment, WasteReport, StockTransaction | `Domain/Documents.cs`, `Domain/Stock.cs` |
| Sổ kho – nơi **duy nhất** thay đổi tồn, bắn event tồn thấp | `Infrastructure/StockLedger.cs` |
| Nhập / xuất kho (UC-20, BR-11) | `Features/StockMovements/StockMovementEndpoints.cs` |
| Đơn mua hàng + Manager duyệt (UC-22, UC-09, BR-12) | `Features/PurchaseOrders/PurchaseOrderEndpoints.cs` |
| Kiểm kê + duyệt điều chỉnh (UC-21, BR-15) | `Features/StockCounts/StockAdjustmentEndpoints.cs` |
| Báo huỷ hàng (UC-35, BR-31) | `Features/Waste/WasteReportEndpoints.cs` |
| Event `StockChangedEvent`, `LowStockAlertEvent` | `src/BuildingBlocks/AICOS.Contracts/Events/InventoryEvents.cs` |

**Table Service** (5103)

| Chức năng | File |
|---|---|
| Entity Reservation (+ chuyển trạng thái) | `Domain/Tables.cs` |
| Đặt bàn online / tại quầy, bàn trống (UC-29, UC-18, BR-10) | `Features/Reservations/ReservationEndpoints.cs` |

**Workforce Service** (5108)

| Chức năng | File |
|---|---|
| Entity ShiftAssignment, ShiftSwapRequest | `Domain/Workforce.cs` |
| Xếp ca, xác nhận/từ chối ca, đổi ca (UC-10, UC-11, UC-32, BR-22, BR-23) | `Features/Scheduling/ScheduleEndpoints.cs` |
| Chấm công vào/ra (UC-42, BR-24, BR-46) | `Features/Attendance/AttendanceEndpoints.cs` |
| Giờ Việt Nam (`Workforce:TimeZone`) | `Infrastructure/ShopClock.cs` |

**Customer Service** – `src/Services/CustomerEngagement/AICOS.CustomerEngagement.Api/` – schema `customer` – port 5110

| Chức năng | File |
|---|---|
| Entity Customer, LoyaltyTransaction, Voucher | `Domain/Customers.cs`, `Domain/Voucher.cs` |
| Hồ sơ khách, điểm thưởng (UC-44, UC-30) | `Features/Customers/CustomerEndpoints.cs` |
| Nhận event: tạo hồ sơ khi đăng ký, cộng điểm khi đơn hoàn tất (BR-25) | `Features/Loyalty/EventHandlers.cs` |
| Voucher / gift card (UC-41, BR-35) | `Features/Vouchers/VoucherEndpoints.cs` |
| DB | `Persistence/CustomerEngagementDbContext.cs` |

**Menu Service** (5102)

| Chức năng | File |
|---|---|
| Entity Promotion | `Domain/Promotion.cs` |
| Khuyến mãi (UC-14, BR-26) + giá khuyến mãi trên menu công khai | `Features/Promotions/PromotionEndpoints.cs`, `Features/PublicMenu/PublicMenuEndpoints.cs` |

Test tự động: `tests/AICOS.Services.IntegrationTests/` (InventoryTests, TableTests, WorkforceTests, CustomerTests, MenuTests).

## 3.2 Luồng test

**Luồng A – Nhập / xuất kho (5106)**
TOKEN_WAREHOUSE: `POST /stock-movements` (Import) → `GET /ingredients/{id}` (tồn tăng) → `POST /stock-movements` (Export) → `POST /stock-movements` (Export quá tồn → 422, tồn không đổi) → `GET /stock-movements?ingredientId=...` (sổ kho) → `GET /ingredients/low-stock`.

**Luồng B – Đơn mua hàng (5106)**
TOKEN_WAREHOUSE `POST /purchase-orders` (PendingApproval) → `PUT /purchase-orders/{id}` (sửa khi còn chờ duyệt) → thử `POST /{id}/approve` bằng WAREHOUSE (403) → TOKEN_MANAGER `POST /{id}/approve` (Approved) → TOKEN_WAREHOUSE `POST /{id}/receive` (Received – tồn kho tăng) → `GET /stock-movements?documentNo=PO-...`.
Nhánh phụ: Manager `POST /{id}/reject` (Rejected), hoặc `POST /{id}/cancel`.

**Luồng C – Kiểm kê (5106)**
TOKEN_WAREHOUSE `POST /stock-adjustments` (đếm thực tế) → `GET /ingredients/{id}` (tồn **chưa** đổi) → TOKEN_MANAGER `POST /stock-adjustments/{id}/approve` → tồn đổi theo chênh lệch. Nhánh phụ: `/reject`.

**Luồng D – Báo huỷ (5106)**
TOKEN_WAREHOUSE `POST /waste-reports` (quá tồn → 422) → `POST /waste-reports` hợp lệ → `GET /waste-reports`.

**Luồng E – Đặt bàn (5103)**
1. TOKEN_CUSTOMER: `POST /reservations` (có branchId, chưa chọn bàn → Pending) → `GET /reservations/mine`.
2. Không token: `GET /availability?branchId=...&at=...&partySize=2` (xem bàn trống).
3. TOKEN_CASHIER: `GET /reservations?status=Pending` → `POST /reservations/{id}/confirm` (gán bàn) → `POST /{id}/seat` (bàn chuyển Occupied) → `POST /{id}/complete` (bàn trở lại Available).
4. Nhánh phụ: khách `POST /{id}/cancel`; thu ngân `POST /{id}/no-show` (sau giờ hẹn).
5. Test BR-10: tạo 2 lịch cùng bàn trùng giờ → lịch thứ 2 bị 409.

**Luồng F – Xếp ca và đổi ca (5108)**
1. Cần 2 nhân viên có hồ sơ (bước 2-32 cho cashier1 và tạo thêm cho kho1).
2. TOKEN_MANAGER: `POST /shift-assignments` (cashier1, ca sáng, ngày mai) → thử xếp ca trùng giờ (409) → `GET /shift-assignments?from=...&to=...`.
3. TOKEN_CASHIER: `GET /shift-assignments/mine` → `POST /shift-assignments/{id}/confirm`.
4. TOKEN_CASHIER: `POST /shift-swaps` (nhờ kho1 làm thay) → `GET /shift-swaps/mine`.
5. TOKEN_MANAGER: `GET /shift-swaps?status=Pending` → `POST /shift-swaps/{id}/approve` → ca chuyển sang kho1 (trạng thái Assigned, kho1 phải confirm lại).

**Luồng G – Chấm công (5108)**
TOKEN_MANAGER tạo ca mẫu **bao trùm giờ hiện tại hôm nay** (vd bây giờ 14:20 → ca `14:00`–`18:00`), xếp cho cashier1 ngày hôm nay → TOKEN_CASHIER `POST /attendance/check-in` → bấm check-out ngay (422 BR-46 – phải cách 5 phút) → đợi 5 phút → `POST /attendance/check-out` → TOKEN_MANAGER `GET /attendance`.

**Luồng H – Hồ sơ khách và điểm (Identity + Customer + Redis)**
Ở Identity `POST /auth/register` → đợi 1–2 giây → `POST /auth/login` → ở Customer (5110) Authorize bằng token khách → `GET /me` → `PUT /me` → `GET /me/loyalty`.
TOKEN_CASHIER: `GET /customers?search=090...` (tìm khách theo SĐT). TOKEN_MANAGER: `POST /customers/{id}/points-adjustments`.
> Cộng điểm tự động (BR-25) chạy khi Order Service bắn `OrderCompletedEvent` – Order Service **chưa làm**, hiện chỉ test được bằng test tự động (`CustomerTests`).

**Luồng I – Voucher (5110)**
TOKEN_MANAGER `POST /vouchers` → `GET /vouchers` → TOKEN_CASHIER `POST /vouchers/validate` (xem trước số tiền giảm) → `POST /vouchers/redeem` → redeem lại (422) → Manager `POST /vouchers/{id}/revoke` (với voucher khác).

**Luồng J – Khuyến mãi (5102)**
TOKEN_MANAGER `POST /promotions` → `GET /promotions?runningOnly=true` → `PUT /promotions/{id}` → `PATCH /promotions/{id}/status` → bỏ Authorize → `GET /public/branches/{BRANCH_ID}/menu` (món có `promotionPrice`).

## 3.3 Validation rules

**Nhập / xuất kho** – Admin/Manager/WarehouseStaff

| Field | Rule | Lỗi |
|---|---|---|
| type | `Import` / `Export` | 400 |
| documentNo | **Bắt buộc** (số phiếu – BR-11), ≤ 50 | 400 |
| note | ≤ 500 | 400 |
| lines | 1–100 dòng, nguyên liệu không trùng, quantity > 0 (≤ 10tr), nguyên liệu thuộc chi nhánh | 400 |
| Xuất quá tồn | **Cả phiếu bị huỷ**, không dòng nào được lưu | 422 `stock.insufficient` |
| Tồn rơi xuống dưới ngưỡng | Bắn `LowStockAlertEvent` **1 lần** khi vừa vượt ngưỡng (NFR-09) | |

**Đơn mua hàng** – tạo/sửa/nhận/huỷ: Admin/Manager/WarehouseStaff; duyệt/từ chối: Admin/Manager

| Rule | Lỗi |
|---|---|
| supplierId bắt buộc, cùng chi nhánh, đang Active | 400 |
| expectedDate không ở quá khứ; note ≤ 1000 | 400 |
| items 1–100, không trùng nguyên liệu, quantity > 0, unitPrice ≥ 0 | 400 |
| `totalAmount` = Σ quantity × unitPrice (server tự tính) | |
| Trạng thái: `PendingApproval → Approved/Rejected`, `Approved → Received`, `PendingApproval/Approved → Cancelled` | sai → 422 `purchase_order.invalid_status` |
| PUT chỉ khi còn PendingApproval | 422 |
| reject bắt buộc `reason` (≤ 500) | 400 |
| WarehouseStaff gọi approve/reject (BR-12) | 403 |
| receive → nhập kho từng dòng, số phiếu = mã PO (`PO-yyyyMMdd-XXXXXX`) | |

**Kiểm kê** – tạo: Admin/Manager/WarehouseStaff; duyệt: Admin/Manager

| Rule | Lỗi |
|---|---|
| lines 1–200, không trùng, countedQuantity ≥ 0 | 400 |
| Lưu tồn hệ thống lúc đếm (`systemQuantity`) và `difference = counted − system` | |
| Tồn **chỉ thay đổi khi Manager duyệt** (BR-15), áp dụng phần chênh lệch | |
| Đã duyệt/từ chối rồi mà gọi lại | 422 `stock_adjustment.invalid_status` |
| reject bắt buộc `reason` | 400 |

**Báo huỷ** – Admin/Manager/WarehouseStaff

| Rule | Lỗi |
|---|---|
| ingredientId bắt buộc; quantity > 0; reason bắt buộc ≤ 500; evidenceImageUrl là URL http(s) | 400 |
| quantity > tồn hiện tại (BR-31) | 422 `BR-31` |
| Trừ kho ngay, không cần duyệt | |

**Đặt bàn**

| Rule | Lỗi |
|---|---|
| Ai được tạo: Customer, Cashier, Manager, Admin (WarehouseStaff → 403) | 403 |
| Customer: **bắt buộc** `branchId`; staff: tự lấy chi nhánh trong token | 400 |
| customerName bắt buộc ≤ 100; customerPhone bắt buộc, đúng định dạng | 400 |
| partySize 1–50; durationMinutes 30–240 (mặc định 120); note ≤ 500 | 400 |
| reservedAt: khách đặt trước **≥ 15 phút**; nhân viên không được ở quá khứ; tối đa **30 ngày** tới | 400 |
| tableId (nếu có): thuộc chi nhánh, không OutOfService, đủ chỗ | 400 |
| Trùng khung giờ trên cùng bàn (BR-10) – 2 khoảng [bắt đầu, kết thúc) giao nhau | 409 `reservation.table_unavailable` |
| Nhân viên tạo **có** bàn → `Confirmed`; còn lại → `Pending` | |
| Trạng thái: `Pending → Confirmed → Seated → Completed`; `Pending/Confirmed → Cancelled / NoShow` | sai → 422 `reservation.invalid_status` |
| confirm không có bàn (cả trong body lẫn lúc đặt) | 400 |
| no-show trước giờ hẹn | 422 `reservation.too_early` |
| Khách huỷ sau giờ hẹn | 422 `reservation.too_late` |
| Khách xem lịch của người khác | 404 |

**Xếp ca** – Admin/Manager

| Rule | Lỗi |
|---|---|
| employeeId, workShiftId, workDate (`yyyy-MM-dd`) bắt buộc | 400 |
| workDate ≥ hôm nay (giờ VN), ≤ 60 ngày tới | 400 |
| Nhân viên và ca cùng chi nhánh; nhân viên Active; ca mẫu Active | 400 |
| Trùng giờ với ca khác cùng ngày (BR-22) | 409 `shift.overlap` |
| Tổng giờ trong tuần (Thứ 2–CN) > 48 (`Workforce:MaxWeeklyHours`) | 422 `shift.weekly_hours_exceeded` |
| DELETE ca đã check-in | 409 `shift.already_started` |

**Xác nhận / từ chối / đổi ca** – chính nhân viên đó

| Rule | Lỗi |
|---|---|
| Thao tác ca của người khác | 404 |
| confirm khi không ở trạng thái Assigned | 422 `shift.invalid_status` |
| decline bắt buộc `reason`; không decline sau khi đã check-in | 400 / 422 |
| Đổi ca: ca sắp tới, chưa bắt đầu, chưa từ chối | 422 `swap.not_swappable` |
| Đồng nghiệp: khác mình, cùng chi nhánh, Active | 400 |
| Đã có yêu cầu đổi đang chờ cho ca này | 409 `swap.already_pending` |
| Đồng nghiệp bị trùng giờ / quá 48h | 409 `shift.overlap` / 422 |
| Ca chỉ chuyển sau khi Manager **approve** (BR-23); reject bắt buộc `note` | 403 nếu không phải Manager / 400 |

**Chấm công**

| Rule | Lỗi |
|---|---|
| assignmentId bắt buộc, phải là ca **của chính mình** (BR-24) | 400 / 404 |
| Ca đã từ chối | 422 `BR-24` |
| Check-in chỉ từ **15 phút trước giờ bắt đầu đến giờ kết thúc** (giờ VN) | 422 `BR-24` |
| 2 lần chấm công phải cách nhau **≥ 5 phút** (BR-46) | 422 `BR-46` |
| Check-in 2 lần | 409 `attendance.already_checked_in` |
| Check-out khi chưa check-in / check-out 2 lần | 422 `attendance.not_checked_in` / 409 `attendance.already_checked_out` |
| Tài khoản chưa có hồ sơ nhân viên | 404 |

**Hồ sơ khách và điểm**

| Rule | Lỗi |
|---|---|
| Hồ sơ được tạo **tự động** khi đăng ký (event qua Redis); gọi `/me` trước khi có | 404 |
| PUT /me: fullName bắt buộc ≤ 255; phone đúng định dạng; birthday từ 1900-01-01 đến hôm nay; `marketingOptIn` (BR-34, mặc định false) | 400 |
| Điểm (BR-25): 1 điểm / 10.000đ của đơn **Completed**; mỗi đơn chỉ cộng 1 lần; hạng: Silver ≥ 1.000, Gold ≥ 5.000 điểm tích luỹ | |
| Điều chỉnh tay (Manager): points ≠ 0, trong −100.000..100.000; note bắt buộc | 400 |
| Trừ quá số dư | 422 `loyalty.insufficient_points` |

**Voucher / gift card** – tạo/xem/thu hồi: Admin/Manager; validate/redeem: mọi nhân viên

| Field / Rule | Lỗi |
|---|---|
| code: không bắt buộc (tự sinh `VC-xxxx` / `GC-xxxx`); nếu nhập: 4–30 ký tự A-Z, 0-9, `-` (tự viết hoa); không trùng | 400 / 409 `voucher.duplicate_code` |
| type: `Voucher` / `GiftCard` | 400 |
| Voucher: bắt buộc `discountType` (`Percentage` 1–100 / `FixedAmount` > 0) | 400 |
| GiftCard: luôn là số tiền cố định, `value` > 0 (≤ 10tr) | 400 |
| minOrderAmount ≥ 0; expiresAt ở tương lai, tối đa 2 năm | 400 |
| issuedToCustomerId (nếu có) phải tồn tại | 400 |
| validate/redeem: code bắt buộc, orderAmount > 0; redeem bắt buộc `orderReference` ≤ 50 | 400 |
| Mã không tồn tại hoặc thuộc chi nhánh khác | 404 |
| Đã dùng / đã thu hồi / hết hạn (BR-35) | 422 `voucher.already_redeemed` / `voucher.revoked` / `voucher.expired` |
| Đơn chưa đạt mức tối thiểu | 422 `voucher.min_order_not_met` |
| Thu hồi: bắt buộc `reason`; chỉ thu hồi được mã Active | 400 / 422 `voucher.invalid_status` |
| Số tiền giảm không bao giờ vượt giá trị đơn | |

**Khuyến mãi** – xem: mọi nhân viên; ghi: Admin/Manager

| Field | Rule | Lỗi |
|---|---|---|
| name | Bắt buộc ≤ 255; description ≤ 2000 | 400 |
| discountType | `Percentage` (discountValue 1–100) / `FixedAmount` (> 0) | 400 |
| startDate, endDate | Bắt buộc; **startDate < endDate** (BR-26); khi tạo endDate phải ở tương lai | 400 |
| productIds | 1–200 món, tất cả thuộc chi nhánh | 400 |
| status (PATCH) | `Active` / `Inactive` | 400 |
| `isRunning` | true khi Active và đang trong khoảng ngày | |

## 3.4 Test case

> Các ngày giờ trong ví dụ là **2026-10-20**. Nếu ngày đó đã qua, đổi sang ngày trong tương lai (≤ 30 ngày với đặt bàn, ≤ 60 ngày với xếp ca).

### Inventory (5106)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 3-01 | POST /stock-movements | WAREHOUSE | `{ "type": "Import", "documentNo": "PN-0001", "note": "Nhập đầu tuần", "lines": [ { "ingredientId": "SUA_ID", "quantity": 20 } ] }` | **201**, tồn = 20 |
| 3-02 | POST /stock-movements | WAREHOUSE | `{ "type": "Export", "documentNo": "PX-0001", "lines": [ { "ingredientId": "SUA_ID", "quantity": 4 } ] }` | **201**, tồn = 16 |
| 3-03 | POST /stock-movements | WAREHOUSE | `{ "type": "Export", "documentNo": "PX-0002", "lines": [ { "ingredientId": "SUA_ID", "quantity": 1000 } ] }` | **422** `stock.insufficient`, tồn vẫn 16 |
| 3-04 | POST /stock-movements | WAREHOUSE | `{ "type": "Import", "lines": [ { "ingredientId": "SUA_ID", "quantity": 1 } ] }` | **400** DocumentNo (BR-11) |
| 3-05 | POST /stock-movements | WAREHOUSE | `{ "type": "Import", "documentNo": "PN-2", "lines": [ { "ingredientId": "SUA_ID", "quantity": 1 }, { "ingredientId": "SUA_ID", "quantity": 2 } ] }` | **400** trùng nguyên liệu |
| 3-06 | POST /purchase-orders | WAREHOUSE | `{ "supplierId": "NCC_ID", "expectedDate": "2026-10-20", "note": "Giao buổi sáng", "items": [ { "ingredientId": "SUA_ID", "quantity": 30, "unitPrice": 25000 } ] }` | **201** PendingApproval, totalAmount 750000 |
| 3-07 | POST /purchase-orders/{id}/approve | WAREHOUSE | – | **403** (BR-12) |
| 3-08 | POST /purchase-orders/{id}/receive (chưa duyệt) | WAREHOUSE | – | **422** `purchase_order.invalid_status` |
| 3-09 | POST /purchase-orders/{id}/approve | MANAGER | – | **200** Approved |
| 3-10 | POST /purchase-orders/{id}/receive | WAREHOUSE | – | **200** Received, tồn +30 |
| 3-11 | POST /purchase-orders/{id}/reject (đơn khác) | MANAGER | `{ "reason": "" }` | **400** |
| 3-12 | POST /purchase-orders/{id}/reject | MANAGER | `{ "reason": "Giá cao hơn thị trường" }` | **200** Rejected |
| 3-13 | POST /purchase-orders (NCC đã Inactive) | WAREHOUSE | như 3-06 | **400** SupplierId |
| 3-14 | POST /stock-adjustments | WAREHOUSE | `{ "note": "Kiểm kê cuối tháng", "lines": [ { "ingredientId": "SUA_ID", "countedQuantity": 44 } ] }` | **201** PendingApproval, có `difference` |
| 3-15 | POST /stock-adjustments/{id}/approve | WAREHOUSE | – | **403** |
| 3-16 | POST /stock-adjustments/{id}/approve | MANAGER | – | **200**, tồn = 44 |
| 3-17 | POST /waste-reports | WAREHOUSE | `{ "ingredientId": "SUA_ID", "quantity": 9999, "reason": "Sữa chua hỏng" }` | **422** `BR-31` |
| 3-18 | POST /waste-reports | WAREHOUSE | `{ "ingredientId": "SUA_ID", "quantity": 2, "reason": "Quá hạn sử dụng", "evidenceImageUrl": "https://example.com/sua-hong.jpg" }` | **201**, `quantityOnHandAfter` giảm 2 |

### Table (5103)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 3-19 | POST /reservations | CASHIER | `{ "tableId": "T01_ID", "customerName": "Nguyễn An", "customerPhone": "0901234567", "partySize": 2, "reservedAt": "2026-10-20T19:00:00+07:00", "durationMinutes": 120, "note": "Gần cửa sổ" }` | **201** Confirmed |
| 3-20 | POST /reservations | CASHIER | `{ "tableId": "T01_ID", "customerName": "Trần Bình", "customerPhone": "0907654321", "partySize": 2, "reservedAt": "2026-10-20T20:30:00+07:00", "durationMinutes": 60 }` | **409** `reservation.table_unavailable` (BR-10) |
| 3-21 | POST /reservations | CASHIER | như 3-20 nhưng `"reservedAt": "2026-10-20T21:00:00+07:00"` | **201** (liền kề, không trùng) |
| 3-22 | POST /reservations | CASHIER | `{ "tableId": "T01_ID", "customerName": "Đoàn", "customerPhone": "0901234567", "partySize": 10, "reservedAt": "2026-10-21T19:00:00+07:00" }` | **400** PartySize (bàn 4 chỗ) |
| 3-23 | POST /reservations | CUSTOMER | `{ "branchId": "BRANCH_ID", "customerName": "Nguyễn An", "customerPhone": "0901234567", "partySize": 2, "reservedAt": "2026-10-22T18:00:00+07:00" }` | **201** Pending |
| 3-24 | POST /reservations | CUSTOMER | 3-23 nhưng bỏ `branchId` | **400** BranchId |
| 3-25 | POST /reservations | CUSTOMER | 3-23 với `reservedAt` = **5 phút nữa** | **400** (đặt trước ≥ 15 phút) |
| 3-26 | POST /reservations/{id 3-23}/confirm | CASHIER | `{ "tableId": "T01_ID" }` | **200** Confirmed |
| 3-27 | POST /reservations/{id}/seat → /complete | CASHIER | `{}` | **200** Seated → **200** Completed |
| 3-28 | POST /reservations/{id đã Completed}/cancel | CUSTOMER | `{ "reason": "Bận" }` | **422** `reservation.invalid_status` |
| 3-29 | GET /reservations/{id của khách A} | token khách B | – | **404** |
| 3-30 | GET /availability?branchId=BRANCH_ID&at=2026-10-20T19:30:00%2B07:00&partySize=2 | không token | – | **200**, không có T01 |
| 3-31 | DELETE /dining-tables/{T01_ID} | MANAGER | – | **409** `table.in_use` |
| 3-32 | POST /reservations | WAREHOUSE | như 3-19 | **403** |

### Workforce (5108)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 3-33 | POST /shift-assignments | MANAGER | `{ "employeeId": "EMP_CASHIER_ID", "workShiftId": "CA_SANG_ID", "workDate": "2026-10-20" }` | **201** Assigned |
| 3-34 | POST /shift-assignments | MANAGER | cùng nhân viên, ca `Ca trưa 10:00–14:00`, cùng ngày | **409** `shift.overlap` (BR-22) |
| 3-35 | POST /shift-assignments | MANAGER | `{ "employeeId": "EMP_CASHIER_ID", "workShiftId": "CA_SANG_ID", "workDate": "2020-01-01" }` | **400** WorkDate |
| 3-36 | POST /shift-assignments/{id}/confirm | token **kho1** | `{}` | **404** (không phải ca của mình) |
| 3-37 | POST /shift-assignments/{id}/confirm | CASHIER | `{}` | **200** Confirmed |
| 3-38 | POST /shift-assignments/{id}/decline | CASHIER | `{ "reason": "" }` | **400** |
| 3-39 | POST /shift-swaps | CASHIER | `{ "assignmentId": "ASSIGNMENT_ID", "targetEmployeeId": "EMP_KHO_ID", "reason": "Có việc gia đình" }` | **201** Pending |
| 3-40 | POST /shift-swaps (lần 2, cùng ca) | CASHIER | như 3-39 | **409** `swap.already_pending` |
| 3-41 | POST /shift-swaps/{id}/approve | CASHIER | `{}` | **403** |
| 3-42 | POST /shift-swaps/{id}/approve | MANAGER | `{ "note": "Đồng ý" }` | **200** Approved, ca chuyển sang kho1 |
| 3-43 | POST /shift-swaps/{id}/reject (yêu cầu khác) | MANAGER | `{ "note": "" }` | **400** |
| 3-44 | POST /attendance/check-in (ngoài giờ ca) | CASHIER | `{ "assignmentId": "ASSIGNMENT_ID" }` | **422** `BR-24` |
| 3-45 | POST /attendance/check-in (trong giờ ca – luồng G) | CASHIER | như trên | **200**, có `checkInAtUtc` |
| 3-46 | POST /attendance/check-out ngay sau đó | CASHIER | như trên | **422** `BR-46` |
| 3-47 | POST /attendance/check-out sau ≥ 5 phút | CASHIER | như trên | **200**, có `checkOutAtUtc` |
| 3-48 | POST /attendance/check-in | MANAGER chưa có hồ sơ | như trên | **404** |

### Customer (5110)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 3-49 | GET /me (sau khi đăng ký ở Identity 1–2 giây) | CUSTOMER | – | **200**, `tier: "Member"`, `pointsBalance: 0` |
| 3-50 | PUT /me | CUSTOMER | `{ "fullName": "Nguyễn Văn An", "phone": "0911111111", "birthday": "2000-05-20", "marketingOptIn": true }` | **200** |
| 3-51 | PUT /me | CUSTOMER | `{ "fullName": "", "birthday": "2999-01-01" }` | **400** FullName, Birthday |
| 3-52 | GET /customers?search=0911 | CASHIER | – | **200**, totalCount 1 |
| 3-53 | POST /customers/{id}/points-adjustments | MANAGER | `{ "points": 50, "note": "Bù đơn bị lỗi" }` | **200**, pointsBalance 50 |
| 3-54 | POST /customers/{id}/points-adjustments | MANAGER | `{ "points": -80, "note": "Trừ" }` | **422** `loyalty.insufficient_points` |
| 3-55 | POST /customers/{id}/points-adjustments | CASHIER | `{ "points": 1, "note": "x" }` | **403** |
| 3-56 | POST /vouchers | MANAGER | `{ "code": "sale20", "type": "Voucher", "discountType": "Percentage", "value": 20, "minOrderAmount": 100000, "expiresAt": "2026-12-31T23:59:00+07:00" }` | **201**, code `SALE20` |
| 3-57 | POST /vouchers | MANAGER | `{ "type": "GiftCard", "value": 100000, "expiresAt": "2026-12-31T23:59:00+07:00", "issuedToName": "Nhân viên xuất sắc" }` | **201**, code `GC-...` |
| 3-58 | POST /vouchers | MANAGER | `{ "type": "Voucher", "discountType": "Percentage", "value": 150, "expiresAt": "2026-12-31T23:59:00+07:00" }` | **400** Value |
| 3-59 | POST /vouchers | MANAGER | `{ "code": "SALE20", "type": "GiftCard", "value": 1, "expiresAt": "2026-12-31T23:59:00+07:00" }` | **409** `voucher.duplicate_code` |
| 3-60 | POST /vouchers/validate | CASHIER | `{ "code": "SALE20", "orderAmount": 50000 }` | **422** `voucher.min_order_not_met` |
| 3-61 | POST /vouchers/validate | CASHIER | `{ "code": "sale20", "orderAmount": 200000 }` | **200**, discount 40000, amountToPay 160000 |
| 3-62 | POST /vouchers/redeem | CASHIER | `{ "code": "SALE20", "orderAmount": 200000, "orderReference": "HD-0001" }` | **200** |
| 3-63 | POST /vouchers/redeem (lại) | CASHIER | `{ "code": "SALE20", "orderAmount": 200000, "orderReference": "HD-0002" }` | **422** `voucher.already_redeemed` (BR-35) |
| 3-64 | POST /vouchers/{id GC}/revoke → validate mã đó | MANAGER / CASHIER | `{ "reason": "Phát nhầm" }` | **200** → **422** `voucher.revoked` |
| 3-65 | POST /vouchers/validate bằng cashier **chi nhánh khác** | CASHIER khác | `{ "code": "SALE20", "orderAmount": 200000 }` | **404** |

### Menu – Khuyến mãi (5102)

| # | API | Token | Body | Mong đợi |
|---|---|---|---|---|
| 3-66 | POST /promotions | MANAGER | `{ "name": "Happy hour", "description": "Giảm 20% cà phê", "discountType": "Percentage", "discountValue": 20, "startDate": "2026-10-01T00:00:00+07:00", "endDate": "2026-12-31T23:59:00+07:00", "productIds": ["PRODUCT_ID"] }` | **201**, `isRunning: true` |
| 3-67 | POST /promotions | MANAGER | 3-66 với `"startDate": "2026-12-31T00:00:00+07:00", "endDate": "2026-12-01T00:00:00+07:00"` | **400** EndDate (BR-26) |
| 3-68 | POST /promotions | MANAGER | 3-66 với `"discountValue": 150` | **400** DiscountValue |
| 3-69 | POST /promotions | MANAGER | 3-66 với `"productIds": ["00000000-0000-0000-0000-000000000001"]` | **400** ProductIds |
| 3-70 | GET /public/branches/{BRANCH_ID}/menu | không token | – | món có `price: 35000`, `promotionPrice: 28000`, `promotionName: "Happy hour"` |
| 3-71 | PATCH /promotions/{id}/status | MANAGER | `{ "status": "Inactive" }` | **200**, menu công khai không còn `promotionPrice` |

---

## 4. Chạy test tự động

```powershell
dotnet test AICOS.sln
```

| Project | Nội dung | Số test |
|---|---|---|
| `tests/AICOS.BuildingBlocks.Tests` | JWT, audit, đọc token | 15 |
| `tests/AICOS.Services.SmokeTests` | Khởi động 10 service + gateway | 15 |
| `tests/AICOS.IdentityOrganization.Tests` | Giai đoạn 1 | 56 |
| `tests/AICOS.Services.IntegrationTests` | Giai đoạn 2 + 3 (Menu, Inventory, Table, Workforce, Customer) | 40 |

Test tự động dùng DB in-memory, không cần Docker.

## 5. Kiểm tra dữ liệu bằng DBeaver

```sql
select name, status from identity.branches;
select email, username, role, status from identity.users;
select name, status from menu.categories;
select name, base_price, is_available, deleted_at_utc from menu.products;
select name, unit, quantity_on_hand, reorder_level from inventory.ingredients;
select type, quantity_delta, quantity_after, document_no, created_at_utc from inventory.stock_transactions order by created_at_utc desc;
select code, status, total_amount from inventory.purchase_orders;
select table_code, capacity, status from table_reservation.dining_tables;
select customer_name, reserved_at_utc, ends_at_utc, status from table_reservation.reservations;
select full_name, payment_type, status from workforce.employees;
select work_date, start_time, end_time, status, check_in_at_utc, check_out_at_utc from workforce.employee_shifts;
select full_name, email, points_balance, tier from customer.customers;
select code, type, status, expires_at_utc from customer.vouchers;
```

## 6. Ghi chú / giới hạn hiện tại

- **Gửi email thật** chưa có: mã đặt lại mật khẩu in ra console của Identity.
- **Cộng điểm khi đơn hoàn tất** đã có code nhận event, nhưng Order Service chưa làm nên chưa test được trên Swagger.
- Workforce lưu `userId` của nhân viên nhưng **chưa gọi Identity để kiểm tra** tài khoản có tồn tại – nhập đúng `id` từ `POST /users`.
- Menu / Inventory / Table **chưa kiểm tra `branchId` của SystemAdmin có tồn tại** ở Identity – Admin nhập đúng `BRANCH_ID`.
- Ca làm **không hỗ trợ qua đêm**; đặt bàn **chưa kiểm tra giờ mở cửa** của chi nhánh.
- Thông báo (Notification) khi có tồn kho thấp / xếp ca chỉ mới bắn event, chưa có service nhận.
