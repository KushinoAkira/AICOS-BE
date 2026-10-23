# Database Analysis

Nguồn đối chiếu: `D:\ki9\sep490\db\Database.sql`.

## Kết luận

File SQL và PostgreSQL runtime có 35 bảng, 208 cột trong schema `public`
và có khóa ngoại xuyên domain.
Trong Backend hiện tại, PostgreSQL được chia schema/role theo service nên không
thể chạy nguyên file này rồi coi đó là database ownership cuối cùng.

Mapping chính:

- `identity`: tenants, branches, users, roles, tenant_users, staff_assignments.
- `table_reservation`: table_areas, cafe_tables, table_sessions,
  table_session_tables.
- `menu`: categories, products, variants, option groups/options,
  branch_products.
- `ordering`: orders, order_items, item options/events.
- `payment`: bills, bill_items, payments, refunds.
- `workforce`: shifts, attendance, cash closing.
- `customer`: loyalty, rewards, feedback.

## Quyết định cho Identity module

- Migration pending `20261023032947_InitialIdentitySchema` chỉ tạo sáu bảng
  thuộc schema `identity`; migration pending cũ `20261022161359` đã được
  regenerate và chưa từng apply.
- Không áp dụng migration tự động trong lần triển khai này.
- Quan hệ bên trong Identity giữ foreign key; domain khác chỉ giữ external ID.
- Tất cả FK Identity dùng `NO ACTION`; không cascade delete tenant, user,
  branch, membership hoặc assignment.
- Branch mặc định `INACTIVE`. Email được trim/lowercase khi ghi và có
  check constraint bắt buộc dạng chuẩn trước unique index.
- `Database.sql` chưa có refresh token, idempotency key, row version/outbox và
  nhiều index phục vụ truy vấn; bổ sung sau theo từng module, không sửa hàng loạt.
- Timestamp trong migration dùng `timestamp with time zone`; file SQL gốc dùng
  `timestamp`. Backend chuẩn hóa UTC và cần kế hoạch import dữ liệu riêng.

## Xác minh migration ngày 2026-10-23

- Đã backup `aicos_db` bằng custom-format và schema-only; custom archive đọc
  được 188 TOC entries.
- Idempotent SQL có 6 `identity` table, 9 business/FK index, không tham chiếu
  object `public`, không có `DROP TABLE` hoặc `DELETE`.
- Migration đã apply thành công trên database kiểm thử tạm; sau khi kiểm
  tra schema/constraint/history, database tạm đã được xóa.
- `aicos_db` thật không thay đổi: chỉ có schema `public`, 35 bảng và
  không có `__ef_migrations_history`.
