# API Integration

## Trạng thái

- Backend Login/Refresh/Logout/Me API: đã triển khai và có unit/HTTP test.
- PostgreSQL integration: đã chạy HTTP round-trip trên `aicos_db` thật cho
  login → me → refresh → logout; dữ liệu test đã cleanup hoàn toàn.
- Staff provisioning: đã chạy HTTP Manager login → tạo Cashier → chặn email trùng
  → Cashier login trên `aicos_db`; dữ liệu test đã cleanup về 0.
- Table lifecycle: đã chạy HTTP map → mở phiên → đóng phiên → dọn xong trên
  `aicos_db`; dữ liệu tenant/branch/area/table/session test đã cleanup về 0.
- Catalog read: đã chạy HTTP với branch, category và tìm kiếm trên `aicos_db`;
  toàn bộ dữ liệu catalog test đã cleanup về 0.
- Ordering: HTTP create/retry/tracking dùng PostgreSQL thật đã đạt; dependency
  Menu/Table được thay bằng contract fake trong test này vì từng service đã có
  PostgreSQL integration riêng. Cross-service E2E sẽ chạy ở Phase 6.
- Preparation: HTTP queue → PREPARING → READY đã chạy trên `aicos_db`; trạng thái
  và hai actor event được kiểm tra trực tiếp, sau đó toàn bộ dữ liệu test cleanup.
- Billing: HTTP create → retry idempotent → read bill đã chạy trên `aicos_db`;
  bill/order/item thử nghiệm được cleanup. Payment confirmation chưa mở.
- Flutter: chưa kết nối; vẫn dùng mock session theo nguyên tắc chỉ tích hợp sau
  khi API và database provisioning chạy được trong môi trường thật.
- Base URL qua Gateway local: `http://localhost:8080`.

## Contract Flutter cần dùng

1. Gửi email, password, optional branchId tới `/api/v1/identity/auth/login`.
2. Nếu `requiresBranchSelection=true`, hiển thị danh sách branch rồi gửi lại.
3. Lưu access token bằng secure storage, không dùng shared preferences/plain text.
4. Gửi `Authorization: Bearer <token>` qua API client tập trung.
5. Lấy role/branch từ response hoặc verified token; Backend vẫn là nguồn quyết
   định quyền cuối cùng.
6. Sơ đồ bàn gọi `/api/v1/tables/map`; không gửi branchId ngoài JWT.
7. Tạo order tại bàn chỉ sau khi `/api/v1/tables/sessions` trả `201`.
8. Sau khi Billing/Payment cho phép đóng phiên, gọi endpoint close rồi xác nhận
   dọn xong để bàn trở lại `AVAILABLE`.
9. Màn hình tạo đơn tải menu từ `/api/v1/menu/catalog`, dùng ID variant/option
   trả về thay vì gửi giá do client tự tính.
10. Màn hình theo dõi pha chế đọc `/api/v1/kitchen/queue`; Staff chỉ xem và
    không tự giả lập Bar đã nhận/xong.
11. Thu ngân tạo bill qua `/api/v1/payments/bills`; không gửi tổng tiền tự tính.

Flutter chưa tích hợp ở module này vì master plan ưu tiên hoàn thiện các API
nghiệp vụ Backend tiếp theo. Tài khoản production đầu tiên vẫn phải được cấp qua
quy trình operator an toàn; source không chứa credential bootstrap.
