# API Documentation

## POST `/api/v1/identity/auth/login`

Đăng nhập nhân viên và khóa phiên vào một chi nhánh. Endpoint public; Gateway và
Identity đều kiểm tra JWT cho các endpoint protected sau đó.

Request:

```json
{
  "email": "staff@aicos.vn",
  "password": "user-supplied-password",
  "branchId": "11111111-1111-1111-1111-111111111111"
}
```

`branchId` có thể bỏ trống. Nếu tài khoản chỉ thuộc một chi nhánh, API phát token
ngay. Nếu thuộc nhiều chi nhánh, response yêu cầu chọn chi nhánh:

```json
{
  "requiresBranchSelection": true,
  "accessToken": null,
  "expiresAtUtc": null,
  "refreshToken": null,
  "refreshTokenExpiresAtUtc": null,
  "staff": null,
  "branches": [
    { "id": "...", "code": "CN01", "name": "Chi nhánh 1" }
  ]
}
```

Response thành công chứa `accessToken`, `expiresAtUtc`, `refreshToken`,
`refreshTokenExpiresAtUtc`, profile Staff và danh sách branch được phép. JWT
có `sub`, `name`, `role`, `branch_id`, `jti`.

Lỗi dùng RFC 7807:

- `400 validation_failed`: email/password không hợp lệ.
- `401 auth.invalid_credentials`: sai email/password hoặc tài khoản không active.
- `401 auth.branch_not_assigned`: user không được phân công vào branch đã chọn.
- `403 forbidden`: role không được phép dùng Staff app.

Không log password, không trả password hash và không hardcode tài khoản demo.

## POST `/api/v1/identity/auth/refresh`

Endpoint public vì access token có thể đã hết hạn. Nhận refresh token hiện
tại, revoke token cũ và phát cặp access/refresh token mới. Token cũ không thể
dùng lại.

```json
{
  "refreshToken": "user-supplied-refresh-token"
}
```

Raw refresh token chỉ trả về client một lần; database chỉ lưu SHA-256 hash.
Session dùng concurrency token để chặn hai request rotation cùng thành công.

- `400 validation_failed`: token trống hoặc quá dài.
- `401 auth.invalid_refresh_token`: token sai, hết hạn, đã revoke hoặc assignment
  không còn active.

## POST `/api/v1/identity/auth/logout`

Nhận `refreshToken`, revoke session nếu tồn tại và trả `204 No Content`.
Endpoint idempotent, không tiết lộ token có tồn tại hay không.

## GET `/api/v1/identity/auth/me`

Yêu cầu access token và policy `Staff`. Trả profile theo `user_id` và
`branch_id` trong JWT sau khi kiểm tra lại user, membership, assignment và branch
vẫn active.

## POST `/api/v1/identity/staff`

Tạo tài khoản nhân viên, tenant membership và branch assignment trong một lần
lưu. Endpoint yêu cầu Bearer token với policy `Management`; không có API đăng ký
nhân viên công khai.

```json
{
  "email": "cashier@aicos.vn",
  "password": "user-supplied-strong-password",
  "fullName": "Nguyễn Văn A",
  "branchId": "11111111-1111-1111-1111-111111111111",
  "role": "Cashier"
}
```

Role cho phép: `Manager`, `Waiter`, `Cashier`, `WarehouseStaff`. Manager chỉ được
tạo nhân viên không phải Manager trong chính branch ghi trên JWT; SystemAdmin có
thể tạo Manager và nhân viên tại branch active bất kỳ. API chuẩn hóa email, hash
password và không trả password/hash trong response.

- `201 Created`: user, membership và assignment đã được lưu.
- `400 validation_failed`: request, password, role hoặc branchId không hợp lệ.
- `401/403`: thiếu token hoặc vượt quyền/khác branch.
- `404`: branch hoặc tenant không active.
- `409 identity.email_exists`: email đã có trong hệ thống.
- `409 identity.provisioning_conflict`: xung đột unique/FK khi ghi đồng thời.

## GET `/api/v1/tables/map`

Trả sơ đồ khu vực và bàn của đúng `branch_id` trong JWT. Endpoint yêu cầu policy
`Staff`; client không được truyền branch tùy ý. Mỗi bàn gồm số bàn, sức chứa,
trạng thái và phiên đang mở nếu có.

Trạng thái bàn hiện dùng: `AVAILABLE`, `OCCUPIED`, `CLEANING`.

## POST `/api/v1/tables/sessions`

Mở phiên phục vụ cho một bàn đang trống.

```json
{
  "tableId": "22222222-2222-2222-2222-222222222222"
}
```

API tạo `table_sessions` và `table_session_tables` trong cùng transaction
`SERIALIZABLE`, sau đó chuyển bàn sang `OCCUPIED`. Chỉ các role SystemAdmin,
Manager, Waiter và Cashier được thao tác.

- `201 Created`: phiên đã mở.
- `404`: bàn không thuộc branch trên JWT.
- `409 table.not_available`: bàn không trống hoặc đã có phiên active.
- `409 table.concurrent_update`: có thao tác đồng thời trên cùng bàn.

## GET `/api/v1/tables/sessions/{sessionId}`

Trả trạng thái phiên và các bàn còn liên kết active, giới hạn theo branch JWT.
Ordering service dùng contract này qua REST để xác minh order `DINE_IN`; không
đọc trực tiếp bảng thuộc Table Reservation.

## POST `/api/v1/tables/sessions/{sessionId}/close`

Đóng phiên thuộc branch trên JWT, ghi `closed_at`, kết thúc các liên kết bàn active
và chuyển bàn sang `CLEANING`. Endpoint không tự xác nhận thanh toán; Payment/Billing
sẽ chịu trách nhiệm nghiệp vụ đó ở module sau.

## POST `/api/v1/tables/{tableId}/cleaned`

Xác nhận đã dọn xong bàn. Chỉ bàn `CLEANING` và không còn phiên active mới được
chuyển về `AVAILABLE`.

## GET `/api/v1/menu/catalog`

Trả menu đang bán tại branch trong JWT, nhóm theo category và kèm variants,
option groups, options. Chỉ trả category/product/variant/option active và
`branch_products.is_available != false`.

Query tùy chọn:

- `categoryId`: lọc một category.
- `search`: tìm tên sản phẩm không phân biệt hoa thường, tối đa 200 ký tự.

Client không truyền tenant hoặc branch; phạm vi branch lấy từ access token.

## POST `/api/v1/orders`

Tạo một lượt order mới. `requestId` là UUID do client tạo và đồng thời là khóa
idempotency; gửi lại cùng ID trả order đã có thay vì tạo bản ghi trùng.

```json
{
  "requestId": "33333333-3333-3333-3333-333333333333",
  "orderType": "TAKEAWAY",
  "sessionId": null,
  "pickupTime": null,
  "items": [
    { "variantId": "...", "quantity": 2, "note": "Ít đá", "optionIds": ["..."] }
  ]
}
```

- `DINE_IN` bắt buộc có Table Session đang `OPEN` cùng branch.
- `TAKEAWAY` không có session; không hỗ trợ delivery.
- Backend lấy tên/giá/quy tắc option từ Menu service, không nhận giá từ client.
- Mỗi item tạo event `CREATED` với `actor_user_id` từ JWT.
- Gọi thêm món là tạo order mới với `requestId` mới nhưng cùng `sessionId`.

## GET `/api/v1/orders/{id}`

Trả snapshot order, item, option và người tạo theo đúng branch JWT.

## GET `/api/v1/orders?sessionId={sessionId}`

Trả toàn bộ lượt order của một Table Session trong đúng branch JWT. Payment
service dùng contract này để tổng hợp bill tại bàn mà không đọc chéo DbContext.

## GET `/api/v1/kitchen/queue`

Trả các order thuộc branch trong JWT còn ít nhất một item chưa `READY`. Response
chỉ giữ các item đang `PENDING` hoặc `PREPARING`; endpoint yêu cầu policy `Staff`.

## PATCH `/api/v1/kitchen/items/{itemId}/status`

Chuyển trạng thái pha chế của một item theo đúng thứ tự:

```text
PENDING -> PREPARING -> READY
```

```json
{ "status": "PREPARING" }
```

Mỗi lần chuyển ghi một `STATUS_CHANGED` event với `actor_user_id` từ JWT. Thao
tác chạy trong transaction `SERIALIZABLE` để ngăn cập nhật đồng thời. Hiện chỉ
`Manager` và `SystemAdmin` được cập nhật vì role chuẩn chưa có Barista; Cashier
và Waiter chỉ xem queue.

- `400 validation_failed`: ID hoặc trạng thái không hợp lệ.
- `404`: item không thuộc branch trên JWT.
- `409 preparation.invalid_transition`: bỏ bước hoặc sửa item đã `READY`.
- `409 preparation.concurrent_update`: item vừa bị thiết bị khác cập nhật.

## POST `/api/v1/payments/bills`

Tạo bill từ đúng một nguồn: `sessionId` cho tại bàn hoặc `orderId` cho mang về.
`requestId` là UUID idempotency và đồng thời là bill ID.

```json
{
  "requestId": "44444444-4444-4444-4444-444444444444",
  "sessionId": null,
  "orderId": "33333333-3333-3333-3333-333333333333"
}
```

Backend lấy order/item/giá qua Ordering REST contract, không nhận tổng tiền từ
Mobile. Chỉ SystemAdmin, Manager và Cashier được tạo bill.

- `201 Created`: bill `UNPAID` được lưu.
- `400 validation_failed`: không truyền đúng một nguồn hoặc UUID rỗng.
- `409 billing.already_exists`: nguồn đã có bill.
- `409 billing.concurrent_update`: request cạnh tranh trên cùng nguồn.

## GET `/api/v1/payments/bills/{billId}`

Trả bill, bill items và các payment hiện có sau khi xác minh nguồn order/session
thuộc branch JWT. API xác nhận thanh toán chưa mở trong foundation này.
