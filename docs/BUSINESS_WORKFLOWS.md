# Business Workflows

## Staff Login

```text
Nhập email/password
  -> kiểm tra dữ liệu đầu vào
  -> tìm assignment active + user/branch/membership active
  -> verify password hash
  -> một branch: phát JWT branch-scoped + refresh token
  -> nhiều branch: yêu cầu chọn branch
  -> chọn branch hợp lệ: phát JWT branch-scoped + refresh token
```

Các nguyên tắc:

- Phản hồi sai mật khẩu không tiết lộ email có tồn tại hay không.
- User không thể tự chọn branch ngoài `staff_assignments`.
- WAITER và CASHIER là hai role Staff riêng; quyền nghiệp vụ chi tiết tiếp tục
  được kiểm tra tại service sở hữu endpoint.
- Access token hết hạn không được tự coi là phiên hợp lệ.
- Refresh token được rotate mỗi lần dùng; token cũ bị revoke trong cùng
  `SaveChanges` transaction.
- Logout idempotent và chỉ revoke refresh session; access token ngắn hạn hết
  hiệu lực theo expiry.
- `/auth/me` luôn kiểm tra lại assignment/branch active thay vì chỉ tin payload JWT.

Workflow payment đã hoàn thành ở Backend và database test riêng; database chính vẫn
chờ duyệt staged migration trước khi kích hoạt.

## Staff Provisioning

```text
Manager/SystemAdmin đăng nhập
  -> kiểm tra policy Management
  -> validate email/password/branch/role
  -> kiểm tra branch và tenant active
  -> kiểm tra phạm vi branch và quyền cấp role
  -> chuẩn hóa email + hash password
  -> lưu user + tenant_user + staff_assignment nguyên tử
  -> trả profile không chứa password hash
```

- Không hỗ trợ self-registration cho ứng dụng nhân viên.
- Manager chỉ cấp Waiter/Cashier/WarehouseStaff tại branch của mình.
- Chỉ SystemAdmin được cấp role Manager.
- Email là định danh toàn hệ thống; request trùng trả `409`.
- Role chuẩn còn thiếu được tạo bằng code cố định, không nhận tên role tùy ý.

## Table Lifecycle

```text
JWT branch-scoped
  -> GET sơ đồ khu vực/bàn của branch
  -> chọn bàn AVAILABLE
  -> mở TableSession + liên kết bàn trong transaction SERIALIZABLE
  -> bàn OCCUPIED
  -> đóng phiên, kết thúc liên kết bàn
  -> bàn CLEANING
  -> nhân viên xác nhận dọn xong
  -> bàn AVAILABLE
```

- Không nhận `branchId` từ request nghiệp vụ; luôn lấy từ JWT.
- Không cho mở hai phiên active trên cùng bàn.
- Đóng phiên bàn không tự tạo hoặc xác nhận payment. Table chỉ đóng sau khi Payments
  trả `settled=true` cho bill đã `CLOSED` và đủ tiền.
- Schema hiện tại chưa có `guest_count` và `opened_by_user_id`; Backend không giả
  lập hai dữ liệu này và chưa tự ý tạo migration.

## Preparation Workflow

```text
Order item PENDING
  -> Staff xem queue theo branch
  -> Manager/SystemAdmin nhận pha: PREPARING
  -> Manager/SystemAdmin hoàn tất: READY
  -> mỗi bước ghi STATUS_CHANGED + actor_user_id
```

- Không cho nhảy thẳng từ `PENDING` sang `READY` hoặc sửa ngược item đã xong.
- Transaction `SERIALIZABLE` bảo vệ khi nhiều thiết bị cập nhật đồng thời.
- `READY` chỉ là trạng thái pha chế; không đồng nghĩa đã in bill hoặc thanh toán.
- Cashier/Waiter chỉ xem cho đến khi mô hình role có Barista chính thức.

## Billing Workflow (foundation)

```text
Cashier chọn Table Session hoặc order mang về
  -> Payment service gọi Ordering bằng bearer token
  -> lấy order + snapshot item/giá đúng branch
  -> kiểm tra chưa có bill
  -> tạo bill + bill items trong transaction SERIALIZABLE
  -> bill UNPAID
```

- Bill tại bàn tổng hợp các lượt order cùng `session_id`; mang về dùng một order.
- `requestId` chống tạo bill trùng do retry từ Mobile.
- Bill có thể được tải lại theo `sessionId` hoặc `orderId`; Payments luôn xác minh
  nguồn qua Ordering REST contract trước khi trả dữ liệu.
- Tạo bill không đồng nghĩa in thành công hoặc thanh toán thành công.
- Bill lưu branch và actor tạo từ JWT; version được tăng tại mỗi payment/close mutation.

## Payment and Settlement Workflow

```text
Cash:
  UNPAID bill -> Cashier/Manager xác nhận nhận đủ tiền
  -> CASH SUCCEEDED + CONFIRMED audit event
  -> bill PAID -> đóng bill -> CLOSED

QR:
  UNPAID bill -> tạo BANK_TRANSFER PENDING + CREATED audit event
  -> hiển thị QR/reference, chưa coi là đã thanh toán
  -> Cashier/Manager kiểm tra tiền thực nhận + nhập evidence reference
  -> payment SUCCEEDED + CONFIRMED audit event
  -> bill PAID -> đóng bill -> CLOSED

Table close:
  Table service -> Payments settlement-status REST contract
  -> chưa CLOSED/đủ tiền hoặc dependency lỗi: giữ session OPEN
  -> settled=true: đóng session và chuyển bàn CLEANING
```

- Payment mutation chạy `SERIALIZABLE`, kết hợp idempotency UUID và optimistic
  concurrency `version`.
- Hai xác nhận QR đồng thời chỉ một request được phép thành công.
- `payment_events` là audit append-only; runtime không sửa/xóa event cũ.
- QR không có bằng chứng nhận tiền không thể chuyển sang `SUCCEEDED`.
- Table service không đọc bảng Payments và Payments không đọc trực tiếp bảng Table ở runtime.
