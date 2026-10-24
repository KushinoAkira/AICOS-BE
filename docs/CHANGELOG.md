# Backend Changelog

## 2026-10-23 — Billing foundation

- Ánh xạ bốn bảng có sẵn `bills`, `bill_items`, `payments`, `refunds` vào Payment
  service schema `public`; không tạo migration.
- Thêm contract Ordering liệt kê order theo Table Session qua REST và API tạo/đọc
  bill theo branch token, hỗ trợ idempotency bằng `requestId`.
- Bill tại bàn tổng hợp toàn bộ order trong session; bill mang về dùng một order,
  giá và item snapshot lấy từ Ordering thay vì nhận từ Mobile.
- Thao tác tạo bill dùng transaction `SERIALIZABLE`; 4 test foundation và HTTP
  PostgreSQL integration create → retry → read đều đạt, dữ liệu test được cleanup.
- Chưa mở API xác nhận thanh toán/in bill vì schema chưa có actor audit và trạng
  thái in độc lập; cần trình migration trước khi thay đổi database.
- Toàn Backend hiện đạt 59/59 test (6 BuildingBlocks + 53 smoke/integration).

## 2026-10-23 — Preparation workflow hoàn chỉnh

- Map read/update subset của `orders`, `order_items`, `order_item_events` vào
  Kitchen Operations service; không tạo bảng hoặc migration.
- Thêm queue theo branch và transition `PENDING → PREPARING → READY`.
- Staff chỉ xem; tạm thời chỉ Manager/SystemAdmin được đổi trạng thái vì hệ thống
  chưa có role Barista. Không giả lập Bar đã duyệt.
- Bọc thao tác đổi trạng thái trong transaction `SERIALIZABLE`, trả conflict ổn
  định khi hai thiết bị cùng cập nhật một món và ghi actor vào item event.
- Unit/HTTP test đạt; PostgreSQL thật đã chạy queue → PREPARING → READY và xác
  nhận toàn bộ dữ liệu thử nghiệm được cleanup.
- Toàn Backend đạt 54/54 test (6 BuildingBlocks + 48 smoke/integration).

## 2026-10-23 — Ordering dependency contract

- Audit bốn bảng Ordering và xác nhận chỉ hỗ trợ `DINE_IN`/`TAKEAWAY`, không có
  delivery trong phạm vi Staff Mobile.
- Thêm `GET /api/v1/tables/sessions/{sessionId}` theo branch JWT để Ordering xác
  minh phiên qua REST, không query chéo DbContext.
- Test Table Operations liên quan đạt 5/5; chưa thay đổi schema hoặc migration.
- Ánh xạ bước đầu bốn bảng Ordering hiện có vào `OrderingDbContext` schema
  `public`, gồm order, item, option snapshot và item event; build 16 project đạt.
- Thêm Create/Add Order và GET tracking; hỗ trợ `DINE_IN`/`TAKEAWAY`, giá lấy qua
  Menu REST client, session lấy qua Table REST client và bearer token được forward.
- Dùng `requestId` làm PK/idempotency key, kiểm tra min/max option và ghi actor event.
- Ordering HTTP PostgreSQL integration đạt create → retry idempotent → tracking;
  actor và tổng tiền đúng, toàn bộ dữ liệu test cleanup về 0.
- Sửa cảnh báo multiple collection include bằng `AsSplitQuery`; toàn solution
  đạt 49/49 test.

## 2026-10-23 — Branch-scoped catalog read API

- Ánh xạ sáu bảng Catalog có sẵn trong `aicos_db.public`; không tạo migration.
- Thêm `GET /api/v1/menu/catalog` với category filter và tìm kiếm ILIKE.
- Chỉ trả sản phẩm được mở bán tại branch JWT cùng category, variant và option active.
- Thêm model/DbContext configuration, DTO, query service, endpoint và tests tách biệt.
- PostgreSQL HTTP integration test đạt; tám bảng catalog/tenant/branch test trở
  về 0 row sau cleanup.

## 2026-10-23 — Table map and session lifecycle

- Ánh xạ bốn bảng Table có sẵn trong `aicos_db.public`; không tạo migration.
- Thêm API map theo branch JWT, mở phiên, đóng phiên và xác nhận dọn xong.
- Áp dụng state machine `AVAILABLE → OCCUPIED → CLEANING → AVAILABLE`.
- Dùng transaction `SERIALIZABLE`, branch scope, role authorization, logging và
  RFC 7807 conflict để bảo vệ thao tác đồng thời.
- Build 16 project đạt 0 warning/error; smoke tests đạt 37/37 trước lần kiểm tra
  toàn solution cuối.
- PostgreSQL integration test thật đạt bốn HTTP endpoint; sáu bảng Table và dữ
  liệu tenant/branch test trở về 0 row sau cleanup.
- Ghi nhận schema chưa có số khách/người mở phiên và partial unique index; chưa
  tự ý tạo SQL hoặc apply migration.

## 2026-10-23 — Identity staff provisioning

- Thêm `POST /api/v1/identity/staff` với policy `Management`.
- Tạo đồng thời user, role chuẩn nếu còn thiếu, tenant membership và branch
  assignment; password chỉ được lưu dưới dạng hash.
- Manager bị giới hạn trong branch của JWT và không thể cấp role Manager;
  SystemAdmin được phép cấp Manager tại branch active.
- Chặn email trùng, role không hỗ trợ và xung đột ghi đồng thời bằng lỗi RFC 7807.
- Không thêm migration hoặc thay đổi schema PostgreSQL.
- Build 16 project đạt 0 warning/error; smoke tests đạt 31/31.
- Integration test thật đạt luồng Manager login → tạo Cashier → chặn email trùng
  → Cashier login; 7 bảng Identity/session trở về 0 row sau cleanup.

## 2026-10-23 — Apply Identity public baseline

- Người dùng đã duyệt SQL và migration đã apply vào `aicos_db`.
- Giữ nguyên 35 bảng gốc; thêm `public.__ef_migrations_history` và
  `public.refresh_sessions`.
- History ghi nhận baseline public và AddRefreshSessions; không còn migration
  pending cho Identity.
- Xác minh 3/3 FK refresh là `NO ACTION`, 5 index và 0 refresh row ban đầu.
- Thêm PostgreSQL integration test tùy chọn qua `AICOS_TEST_POSTGRES`; khi bắt
  buộc chạy trên `aicos_db`, HTTP login/me/refresh/logout đều đạt.
- Integration test cleanup đúng các ID ngẫu nhiên; row count Identity/refresh
  sau test trở về 0.

## 2026-10-23 — Map Identity trực tiếp vào `aicos_db.public`

- Xác nhận connection runtime tới `aicos_db` và 35 bảng `public`.
- Loại connection Development cũ trỏ nhầm database `aicos`; source không lưu password.
- Chuyển Identity DbContext sang sáu bảng `public`, khớp nullability và
  `timestamp without time zone` hiện tại.
- Loại hai migration schema `identity` chưa apply; thay bằng baseline không DDL
  và migration chỉ thêm `public.refresh_sessions`.
- Sinh `docs/PENDING_IDENTITY_PUBLIC_MIGRATION.sql` để chờ duyệt; chưa apply
  vào `aicos_db`.
- Test migration trên bản sao schema-only đạt; 35 bảng gốc không bị sửa.
- Build 16 project không warning/error; Backend tests đạt 32/32.

## 2026-10-23 — Refresh session lifecycle

- Thêm refresh-token rotation, hash-only persistence và optimistic concurrency.
- Thêm `POST /auth/refresh`, `POST /auth/logout` và `GET /auth/me`.
- Thêm migration pending `20261023034706_AddRefreshSessions` với 3 FK
  `NO ACTION`; không apply vào `aicos_db`.
- Review SQL idempotent: không chạm schema `public`, không drop/delete dữ liệu.
- Hai migration Identity chạy thành công trên PostgreSQL tạm; database tạm
  đã được xóa.
- Build 16 project không có warning/error; Backend tests đạt 32/32.

## 2026-10-23 — Harden Identity migration

- Tạo backup custom-format và schema-only trước khi thay migration pending.
- Regenerate `InitialIdentitySchema`; migration cũ chưa apply nên không làm thay
  đổi lịch sử database.
- Chuyển 6 FK Identity từ cascade sang `NO ACTION`.
- Đặt branch mới mặc định `INACTIVE`.
- Chuẩn hóa email về lowercase và thêm check constraint tại database.
- Test migration trên database PostgreSQL tạm thành công; không apply vào
  `aicos_db`.
- Build 16 project không có warning/error; Backend tests đạt 27/27.

## 2026-10-22 — Identity Authentication foundation

- Thêm entity/configuration cho tenant, branch, user, role, tenant membership và
  staff assignment trong schema `identity`.
- Thêm migration source `InitialIdentitySchema`; chưa apply database.
- Thêm `POST /api/v1/identity/auth/login` với validation, password hashing,
  branch selection, logging và RFC 7807 errors.
- Thêm role `Waiter` vào JWT/policy Staff để khớp AICOS Staff Mobile.
- Đồng bộ dependency EF Core Relational 8.0.31.
- Thêm unit test auth và HTTP validation test.
- Cài SDK .NET 8.0.426 cho môi trường phát triển; không đổi `global.json`.
- Build toàn solution đạt 0 warning; Backend tests đạt 25/25.
- Sinh idempotent SQL migration thành công vào thư mục tạm; chưa apply database.
