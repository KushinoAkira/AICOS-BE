# Backend TODO

## Identity — tiếp theo

- Thiết kế refresh token rotation/revocation và migration tương ứng.
- Thêm endpoint `/auth/refresh`, `/auth/logout`, `/auth/me`.
- Thêm API provisioning user/role/assignment có policy Management hoặc quy trình
  import dữ liệu ban đầu; không seed credential trong source.
- Chuyển migration smoke test PostgreSQL tạm thành integration test tự động
  bằng Testcontainers khi Docker hoạt động.
- Xác nhận chiến lược import/cutover từ `public` sang schema service-owned
  trước khi apply migration vào `aicos_db`.

## Sau Identity

- Table Areas, Tables và Table Sessions trong Table Reservation service.
- Catalog read API trong Menu Catalog service.
- Ordering, Kitchen/Delivery, Billing/Payment theo thứ tự master plan.
- Chỉ bật Flutter API Mode khi API tương ứng và DB test đã đạt.
