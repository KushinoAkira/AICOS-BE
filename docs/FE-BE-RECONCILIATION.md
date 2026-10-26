# Đối chiếu Frontend (AICOS-FE) với Backend (AICOS-BE `main`)

Cách làm: đọc **chỉ đọc** toàn bộ lớp gọi API của FE (`src/lib/api/http.ts`, `tokenStore.ts`, `identity.ts`, `brand.ts`, `orders.ts`, `staff.ts`, `platform.ts`, `engage.ts`, `be/*.ts`), các trang liên quan và `docs/be-integration-gaps.md`, rồi so từng lời gọi với controller và DTO thật của BE. FE chưa được chạy với BE thật (`be-integration-gaps.md` mục 5 tự ghi), nên bảng dưới là đối chiếu trên mã, chưa phải kết quả chạy thử.

Tài liệu `be-integration-gaps.md` của FE được viết theo bản BE cũ: **các mục 2.1 đến 2.13 ở đó nay đều đã có ở BE** (xem mục B). FE chưa nối, không phải BE thiếu.

Chú thích: **OK** = khớp, không cần làm gì. **FE** = FE phải đổi. **BE** = BE đã đổi trong đợt này hoặc cần đổi. Đường dẫn dưới đây tính từ gốc `/api/v1`.

---

## A. Lời gọi FE đang dùng và khớp BE

| Màn hình FE | FE gọi | BE | Kết quả |
|---|---|---|---|
| Đăng nhập, đăng xuất, refresh | `POST /identity/auth/login {login,password}`, `/logout`, `/refresh` | `AuthResponse { accessToken, accessTokenExpiresAtUtc, refreshToken, refreshTokenExpiresAtUtc, user }` | OK. Khớp `StoredSession`. |
| Đăng ký khách | `POST /identity/auth/register` kèm `tenantCode` | `RegisterRequest(tenantCode, fullName, email, phone, password, confirmPassword)` | OK. Brand phải `Active`, nếu không 403 `tenant.suspended`. BE **không trả token** sau đăng ký; FE phải đăng nhập tiếp. |
| Đổi mật khẩu | `POST /identity/auth/change-password {currentPassword,newPassword,confirmNewPassword}` | cùng tên trường | OK. |
| Quên/đặt lại mật khẩu | `forgot-password {email}`, `reset-password {email,code,newPassword,confirmNewPassword}` | cùng tên trường | OK về hợp đồng. Email thật: xem mục D (nay có khung SMTP). |
| Tài khoản nhân viên | `/identity/users` (GET, POST, PUT, PATCH status, DELETE) | `UserResponse` có đủ trường FE dùng | OK. Danh sách có thể là phân trang `{items,...}`: FE đã xử lý cả mảng lẫn phân trang. |
| Chi nhánh | `/identity/branches` (GET, POST, PUT, PATCH status), `openingTime` dạng `HH:mm:ss` | `BranchResponse` | OK. |
| Gói thuê bao của brand, gia hạn | `GET /identity/subscription`, `POST /identity/subscription/renew` | `{ subscription:{...}, canRenew, amountToPay }` | OK. FE chỉ đọc `subscription`; nên dùng thêm `canRenew` và `amountToPay` để bật/tắt nút. |
| Tenant, gói, thuê bao, nhật ký, cổng thanh toán | `/identity/tenants`, `subscription-plans`, `subscriptions`, `audit-logs`, `/payments/providers` | xem mục C với tenant | OK trừ các điểm ở mục C. |
| Thực đơn | `/menu/products`, `categories`, `POST /menu/publications`, `PATCH /menu/branches/{b}/products/{p}/availability {isAvailable}` | cùng đường dẫn và trường | OK. |
| Khung giờ lấy hàng | `GET/PUT/DELETE /orders/pickup-slots*` | cùng | OK, xem mục C về `lockMinutes`. |
| Quy tắc điểm, voucher | `/customers/loyalty/rules`, `/customers/vouchers`, `POST .../revoke` | cùng | OK. |
| Chính sách hoàn tiền, pay-rules, tài khoản nhận tiền | `/payments/policies/cancellation`, `/workforce/pay-rules`, `/payments/merchant-settings` | cùng | OK, xem mục C. |
| Bàn và đơn tại quầy | `/tables/floor-plan?branchId`, `/tables/areas`, `/tables/dining-tables`, `POST /orders`, `/orders/{id}/deliver`, `/cancel`, `/exception`, `/payments/transactions/cash`, `/kitchen/queue`, `PUT /kitchen/tickets/{id}/status` | cùng tên trường | OK. Danh sách đơn của FE gọi chi tiết từng đơn (N+1): chấp nhận được, nhưng chậm khi nhiều đơn. |
| Thanh toán online giả lập | `POST /payments/transactions {orderId,amount,providerCode:"Mock",branchId:null}` rồi `POST .../{id}/simulate {outcome,notify}` | cùng | OK (chỉ khi `Payments__AllowSimulation=true`). |
| Ca làm, check-in/out, ca thay (đăng ký, duyệt), chốt két | `/workforce/shift-assignments`, `/attendance/check-in|check-out`, `/replacement-shifts/*`, `/cash-closings` | cùng | OK trừ các điểm ở mục C. |
| Điểm thưởng của khách | `GET /customers/me/loyalty`, `POST /customers/me/redemptions {points}` | `LoyaltySummaryResponse` | Có lỗi đọc lịch sử, xem mục C. |
| Khóa tài khoản | FE nhận cả `account.temporarily_locked` lẫn 423, đọc `retryAfterSeconds` ở thân lỗi | 403 `account.temporarily_locked` kèm `lockedUntilUtc`, `retryAfterSeconds`, header `Retry-After` | OK. Chỉ cần bỏ giá trị mặc định 15 phút, vì BE đã trả thời gian cụ thể. |
| Sức khỏe nền tảng | `GET /platform/health` | `{ status, services:[{service,status,latencyMs,...}] }` | OK. FE đọc `service` và `latencyMs` đúng. |

---

## B. FE đang báo "Backend chưa có API" nhưng BE đã có

| # | Mục trong `be-integration-gaps.md` | Endpoint BE | Việc FE phải làm |
|---|---|---|---|
| 2.1 | Khách quét QR gọi món | `POST /orders/qr/scan {qrToken}` -> `sessionToken`; mọi lời gọi sau gửi header `X-Table-Session`. `GET/POST /orders/qr/cart/items`, `PUT/DELETE .../{lineId}`, `DELETE /orders/qr/cart`, `POST /orders/qr/checkout {note}`, `GET /orders/qr/orders`, `GET /orders/qr/session/bill` | Thay `guestPlace`, `guestOrders`, `requestBill`. Giỏ nằm ở server (Redis): FE bỏ giỏ cục bộ hoặc đồng bộ với `GET /orders/qr/cart`. `qrToken` là chuỗi trong mã QR do service Bàn sinh, **không phải** mã bàn `tbl-a1`. "Gọi thanh toán" (`requestBill`) chưa có nút riêng: khách xem hóa đơn `session/bill`, thu ngân thu tiền từng đơn. |
| 2.2 | Đặt món mang đi | `GET /orders/pickup-slots/available?branchId&date`, `POST /orders/pickup {branchId,slotStartUtc,promotionCode?,note?,items}` (header tùy chọn `Idempotency-Key`) | `pickupInfo` suy ra từ `available`: `open` = đầu khung đầu tiên, `close` = cuối khung cuối cùng, `slotMinutes`, `capacityPerSlot`, `cutoffMinutes`. `pickupCheck` = tìm khung theo giờ trong `slots` (`available`, `remaining`, `locked`). Giỏ gửi `slotStartUtc` đúng điểm khung. Thanh toán qua `/payments/transactions` như đơn thường; quá `paymentDueAtUtc` (mặc định 15 phút) đơn tự hủy. |
| 2.3 | Khách tự hủy đơn | `POST /orders/{id}/cancel {reason?}` (role Customer, đơn của mình) | Thay `cancelMine`. Lỗi cần hiện: 422 `order.pickup_locked` (qua giờ khóa), 422 `order.cannot_cancel_now` (bar đã làm), 422 `refund.nothing_to_refund` (chính sách không hoàn). |
| 2.5 | Trang theo thương hiệu | `GET /identity/public/brands/{code}` -> `{name, code, description, logoUrl, primaryColor, secondaryColor, branches[]}`; sửa bằng `GET/PUT /identity/brand/profile` | Thay `bePublicApi.brand` (hiện giả) và `beProfileApi` (hiện `unsupported`). Màu có hai trường `primaryColor`, `secondaryColor` (FE hiện có một `brandColor`). Trả 404 nếu brand không `Active`. |
| 2.6 | Bảng giá landing | `GET /identity/subscription-plans` (công khai, chỉ gói đang bán) | FE không cần đổi, chỉ cần gọi anonymous. |
| 2.7 | Form liên hệ | `POST /identity/public/contact {fullName,email,phone?,subject?,message,website?}` -> 202 | Thay `bePublicApi.contact`. Ô `website` là ô ẩn chống bot, để trống. |
| 2.8 | Thông báo của khách | `GET /customers/me/notifications?unreadOnly&page&pageSize`, `/unread-count`, `POST /{id}/read`, `/read-all` | Ánh xạ `Notice`: `atUtc` <- `createdAtUtc`, `read` <- `isRead`. Thêm SignalR `/hubs/orders` để có tin ngay (xem dưới). |
| 2.9 | Đánh giá đơn | `POST /orders/{id}/feedback {rating,comment?}`, `GET /orders/feedbacks/mine`, `GET /orders/feedbacks` (quản lý), cờ `hasFeedback` trong `/orders/mine` và `/orders/{id}` | `feedbackApi.mine` <- `feedbacks/mine` (cùng trường: `id,orderId,orderCode,rating,comment,createdAtUtc`). `pending` = đơn `Completed` của `/orders/mine` có `hasFeedback=false`. `branch` <- `GET /orders/feedbacks?branchId`. Gợi ý AI vẫn ngoài phạm vi. |
| 2.10 | Lịch ca của quản lý | `GET /workforce/shift-assignments?from&to&branchId&pageSize=100` (phân trang, tối đa 100 dòng mỗi trang: một tuần nhiều nhân viên cần đọc thêm `page=2`, `3`... đến hết `totalCount`) | Bỏ cách dùng `attendance/report`. Trường: `id, employeeId, employeeName, workDate, startTime, endTime, status, checkInAtUtc, checkOutAtUtc`. |
| 2.11 | Ca thay đang mở | `GET /workforce/replacement-shifts?status=Open` (nhân viên thường mặc định chỉ thấy `Open`) | Thay `replacements`. Trường: `workDate, startTime, endTime, originalEmployeeName, reason, status, approvedEmployeeName`; **không có** `assignment` lồng nhau. |
| 2.12 | Gỡ nhân viên khỏi ca | `DELETE /workforce/shift-assignments/{id}` | Thay `unassign`. |
| 2.13 | Tiền mặt dự kiến | `GET /workforce/cash-closings/expected?assignmentId=` -> `{expectedAmount, cashReceived, cashRefunded, sinceUtc, asOfUtc}` | Thay `cashExpected`. Cần `assignmentId` của ca đang làm (lấy từ `shift-assignments/mine`). |
| 2.14 | Realtime | Bar/thu ngân: `/hubs/kitchen` (token ở `access_token`). Khách: `/hubs/orders?sessionToken=` (khách bàn) hoặc `access_token` (thành viên) | `@microsoft/signalr` đã có trong `package.json`. Tin `OrderChanged` (`kind`, `status`, `title`, `message`...) và `SessionClosed`. Bắt `onclose` có lỗi để quay lại hỏi định kỳ. Chi tiết trong `FE-INTEGRATION-API-REFERENCE.md`. |
| 2.15 | Mời Brand Admin | Không cần: `POST /identity/tenants` nhận `firstAdmin.phone` và `password`. Ngoài ra khách tự đăng ký brand: `POST /identity/auth/register-brand`, Super Admin duyệt `POST /identity/tenants/{id}/approve|reject {reason}` | FE thêm trang `/register-brand` và màn duyệt cho Super Admin (danh sách `GET /identity/tenants?status=Pending`). |
| 3.4 | Trạng thái đơn `NoShow`, `Unfulfillable`, pha lại | `POST /orders/{id}/no-show {reason}`, `POST /orders/{id}/unfulfillable {reason}` (Barista cũng gọi được), `POST /kitchen/tickets/{id}/remake {reason}` | `toStatus` hiện đưa mọi trạng thái lạ về `Pending`: phải thêm `NoShow`, `Unfulfillable`. Trường `remakeCount`. Pha lại đưa đơn từ `Ready` về `Preparing`. |
| 3.5 | Tùy chọn món | `optionIds: string[]` trên từng dòng của `POST /orders`, `/orders/pickup`, giỏ QR | Hiện `orderBody` nhét size/đường/đá vào `note` và **không tính phụ thu**. `toProduct` đã giữ `id` cho size và topping, nhưng đường và đá chỉ giữ tên: cần giữ cả id. `LineInput` nên mang `optionIds`. BE trả `unitPrice` đã gồm phụ thu, `basePrice`, `options[]` trên mỗi dòng. Lỗi: `order.option_invalid`, `order.option_too_many`, `order.option_requires` (topping cần lựa chọn khác, ví dụ trân châu cần Lạnh, tức BR-14), `order.option_required`. |
| 3.17 | Trạng thái tenant | `Active`, `Suspended`, `Pending`, `Rejected` | `setTenantStatus` chỉ cho Active/Suspended; Pending/Rejected quyết định bằng approve/reject. `PATCH status` trên Pending/Rejected trả 409 `tenant.use_review`. |

---

## C. Chỗ lệch thật trong các lời gọi FE đã nối

| # | Màn hình | Vấn đề | Bên sửa |
|---|---|---|---|
| C1 | Doanh thu nền tảng (`be/platform.ts` `revenue`) | FE đọc `months/points/items`, BE trả `byMonth: [{month, amount}]` (cùng `total`, `byPlan`). Biểu đồ sẽ rỗng. | **FE**: đọc `byMonth`. |
| C2 | Điểm thưởng (`be/staff.ts` `loyaltyMe`) | FE đọc `history` như mảng; BE trả `history` là **phân trang** `{items:[{id,orderId,points,type,note,createdAtUtc}], page, ...}`. Lịch sử sẽ rỗng. Cũng nên dùng `nextTierAt`, `pointsToNextTier` có sẵn thay vì tự tính. | **FE**: dùng `history.items`, ánh xạ `points`, `note` hoặc `type`, `createdAtUtc`. |
| C3 | Chi nhánh đang hoạt động (`GET /identity/branches/active`) | FE gọi không có `tenantCode` (trang Accounts, Menu của Brand Admin). BE trước đây trả 400. | **BE đã sửa trong đợt này**: người đã đăng nhập thuộc một brand thì không cần `tenantCode` (nhận chi nhánh của brand mình). Khách vãng lai vẫn phải gửi `tenantCode`. |
| C4 | Danh sách tenant (Super Admin) | FE đọc `planId`, `branchCount`. | **Đã xử lý ở BE:** `TenantResponse` có thêm `planId`, `planName` (gói đang dùng, null nếu chưa có) và `branchCount`; có ở list, get, create, update, status, approve, reject. |
| C5 | Ca thay (`toReplacement`) | FE đọc `assignment`, `employeeName`, `filledByName`; BE có `originalEmployeeName`, `approvedEmployeeName`, và các trường ca nằm thẳng trên đối tượng. Tên người bị thay và người nhận ca sẽ trống. | **FE**. |
| C6 | Trạng thái đơn trong `toOrder` | Thiếu `NoShow`, `Unfulfillable`, `remakeCount`, `tableSessionId`, `pickupSlotStartUtc`. Dòng đơn không đọc `options` (size, topping hiện `""`). | **FE**. |
| C7 | Khung giờ lấy hàng | FE có hai số `cutoffMinutes` và `lockMinutes` (đặt `0`). BE chỉ có **một** số `cutoffMinutes` làm cả mốc khóa đặt món lẫn mốc khóa hủy đơn (đã chốt trước đó). `advanceDays` FE cố định 1, BE cho 0 đến 14. | **FE**: gộp hai ô làm một, thêm ô số ngày đặt trước. |
| C8 | Chính sách hủy | FE `freeCancelBeforeMinutes`, `refundPercentAfterLock` được ánh xạ gần đúng vào `noShowAfterMinutes`, `noShowRefundPercent`. Ngữ nghĩa BE: `noShowAfterMinutes` là thời gian chờ trước khi ghi nhận khách không đến; `cancelRefundPercent` là tỷ lệ hoàn khi khách hủy (hiện FE giữ nguyên giá trị cũ). Hai bên cần thống nhất tên gọi. | **FE** (cần quyết định nghiệp vụ). |
| C9 | Tài khoản nhận tiền VietQR | BE cần thêm `bankCode`, `accountNumber`. Form hiện chỉ có tên và mã đơn vị. | **FE** thêm hai ô. |
| C10 | Mã bàn | FE dùng `tableCode` làm id bàn. Đơn của BE có `tableNumber` (mã bàn) và `tableId`. Thu tiền cả bàn: FE gọi thu tiền mặt từng đơn chưa trả, đúng. Với bàn QR có thêm `tableSessionId` và `GET /orders/qr/sessions` (nhân viên) cho từng bàn đang mở, kèm `due`. | **FE** (có thể dùng API phiên bàn để thay N+1). |
| C11 | Lấy thông tin bàn từ QR (`GET /tables/public/qr/{token}`) | FE đọc `branchName`; BE trả `tenantId, branchId, tableId, tableCode, areaName, canOrder`, không có tên chi nhánh. Menu đã lấy theo `branchId` nên đúng, chỉ thiếu tên hiển thị. | **FE**: lấy tên từ `GET /identity/public/brands/{code}` (có `branches[].name`). |
| C12 | Đường dẫn mặc định | `API_BASE_URL` mặc định `http://localhost:5000/api/v1`; gateway của BE là cổng **8080**. | **FE** đặt `VITE_API_BASE_URL=http://localhost:8080/api/v1`. |
| C13 | Gói thuê bao, voucher, pay rules | FE có trường BE không có (`graceDays`, `maxUses`, `lateDeductPerHour`); đã nắn ở phía FE. Gói thuê bao đúng như `be-integration-gaps.md` mô tả. | OK, không đổi. |

---

## D. Còn thiếu hoặc cần quyết định ở BE

| Việc | Hiện trạng |
|---|---|
| Gửi email thật (mã đặt lại mật khẩu, duyệt brand) | **Nay đã có khung SMTP (MailKit) và mẫu thư HTML.** Để trống thì chỉ ghi log. Điền `SMTP_*` trong `.env` để gửi thật. Xem mục E. |
| Trang OTP khi đăng ký (`/auth/otp`) | BE **không có** xác thực OTP lúc đăng ký (OTP chỉ dùng cho đặt lại mật khẩu). Cần FE cho biết trang này phục vụ luồng nào. |
| Đặt lại mật khẩu bằng liên kết `/reset-password/abc` | BE dùng email và mã 6 số, không có token qua link. FE giữ cách "nhập mã" (đã nắn). |
| Đẩy thông báo ra điện thoại khi đóng app | Chưa có. Thông báo hiện lưu DB và đẩy live qua SignalR khi app mở. |
| Độ tin cậy sự kiện giữa service (Outbox), SignalR nhiều instance (Redis backplane) | Chưa làm. |
| Thanh toán thật (VietQR, Momo, ZaloPay) | Vẫn giả lập. |
| Gợi ý AI, phân tích phản hồi | Ngoài phạm vi theo quyết định trước. |

---

## E. Thay đổi BE trong đợt này

1. **Đánh giá của khách:** `GET /orders/feedbacks/mine?limit=` (role Customer, mới nhất trước, chỉ đánh giá của chính khách trong brand đó) và cờ `hasFeedback` trên `GET /orders/mine`, `GET /orders/{id}` và `GET /orders` (nhân viên). Test: 2 test mới.
2. **`GET /identity/branches/active`:** không cần `tenantCode` khi đã đăng nhập (xem C3). Test: 1 test mới.
3. **Khung gửi email thật:**
   - `IEmailSender` có thêm lệnh gửi thư gồm phần văn bản và phần HTML; `SmtpEmailSender` (MailKit) gửi nhiều phần, kết nối ngắn, có thời gian chờ, **không ghi mật khẩu, nội dung thư hay địa chỉ đầy đủ ra log**.
   - Tự chọn theo cấu hình: có `Email:Smtp:Host` và `Email:FromAddress` thì gửi qua SMTP, ngược lại chỉ ghi log (như cũ).
   - Mẫu thư tiếng Việt (văn bản và HTML): mã đặt lại mật khẩu, đã nhận đăng ký brand, brand được duyệt (kèm liên kết đăng nhập khi có `Email:AppUrl`), brand bị từ chối (kèm lý do). Mọi giá trị người dùng nhập đều được thoát ký tự HTML.
   - Quên mật khẩu và duyệt brand vẫn thành công khi máy chủ thư lỗi (câu trả lời không phụ thuộc máy chủ thư, nên không lộ tài khoản nào tồn tại).
   - Cấu hình qua biến môi trường, để trống mặc định, ghi trong `.env.example` và `docker-compose.yml`:

     | Biến `.env` | Ý nghĩa |
     |---|---|
     | `SMTP_HOST` | Máy chủ thư. **Để trống = chỉ ghi log.** |
     | `SMTP_PORT` | Mặc định 587 |
     | `SMTP_SECURITY` | `StartTls` (mặc định), `Ssl` (cổng 465) hoặc `None` (chỉ máy chủ thử cục bộ) |
     | `SMTP_USERNAME`, `SMTP_PASSWORD` | Tài khoản gửi thư (bí mật, tự điền) |
     | `SMTP_FROM_ADDRESS`, `SMTP_FROM_NAME` | Địa chỉ và tên người gửi (bắt buộc cùng host) |
     | `APP_URL` | Địa chỉ web app cho liên kết "Đăng nhập" trong thư duyệt brand |

   - Test: 12 test (mẫu thư, chống chèn HTML, chọn nhà cung cấp theo cấu hình, **gửi thật qua một máy chủ SMTP giả bằng MailKit**, lỗi SMTP không lộ bí mật vào log, các luồng quên mật khẩu và duyệt brand).

---

## F. Danh sách việc FE nên làm, theo thứ tự

1. Sửa các lệch C1, C2, C5, C6, C11, C12 (nhỏ, chắc chắn đúng).
2. Nối tùy chọn món bằng `optionIds` (B 3.5) vì đang làm sai tiền.
3. Nối QR gọi món và đặt mang đi (B 2.1, 2.2) cùng khách tự hủy (2.3).
4. Nối thông báo, đánh giá, form liên hệ, trang brand công khai, đăng ký brand (2.5, 2.7, 2.8, 2.9, 2.15).
5. Thay các `unsupported` còn lại ở lịch ca, ca thay, tiền mặt dự kiến (2.10 đến 2.13).
6. Nối SignalR cho khách và bar (2.14), bỏ polling 5 giây.
7. Trang cũ `CustomerRegister` chuyển sang gọi `POST /identity/auth/register`.
