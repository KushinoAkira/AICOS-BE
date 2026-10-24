# Database Analysis

## Quyết định hiện hành — dùng trực tiếp `aicos_db.public`

Theo xác nhận ngày 2026-10-23, `aicos_db` là database chính thức. Runtime
có 35 bảng/208 cột trong schema `public`; Identity Service sở hữu logic
sáu bảng có sẵn `tenants`, `branches`, `users`, `roles`, `tenant_users` và
`staff_assignments`, không tạo bản sao trong schema `identity`.

- Connection Development trỏ `localhost:5432/aicos_db` với user `postgres`;
  password phải truyền qua `ConnectionStrings__Default`, không lưu trong Git.
- `IdentityOrganizationDbContext` dùng schema `public` và nullability/timestamp
  khớp 33 cột Identity hiện có.
- Hai migration schema `identity` cũ đều chưa apply và đã được loại bỏ.
- `20261023040204_BaselineExistingIdentityTables` có `Up/Down` rỗng, chỉ
  ghi nhận model của sáu bảng có sẵn; không create/drop bảng gốc.
- `20261023040310_AddRefreshSessions` tạo duy nhất
  `public.refresh_sessions` và bảng lịch sử EF.
- SQL đã được duyệt và apply ngày 2026-10-23; bản SQL lưu tại
  `docs/PENDING_IDENTITY_PUBLIC_MIGRATION.sql` để audit.
  File có 0 lệnh tạo/sửa/xóa sáu bảng gốc, 0 `DELETE`, 0 `TRUNCATE`.
- Sau apply, `aicos_db` có 37 bảng: nguyên vẹn 35 bảng gốc, EF history
  và refresh session. History ghi nhận đủ 2 migration; 3/3 FK refresh dùng
  `NO ACTION`.

API login/refresh/provisioning dùng EF Core/Npgsql, không dùng mock hoặc InMemory
trong runtime. Provisioning được bảo vệ bằng policy Management và đã được kiểm
thử HTTP với PostgreSQL thật.

Integration test HTTP trên `aicos_db` đã xác minh login, `/auth/me`, refresh-token
rotation và logout lần lượt trả 200/200/200/204. Test chỉ tạo dữ liệu
có ID ngẫu nhiên và cleanup theo chính các ID đó; sau test, sáu bảng
Identity và `refresh_sessions` đều có 0 bản ghi như trước test.

Table Reservation map trực tiếp bốn bảng `public`: `table_areas`, `cafe_tables`,
`table_sessions`, `table_session_tables`. Đối chiếu database xác nhận 20 cột,
4 PK, 3 FK `NO ACTION`, unique `(area_id, table_number)` và unique `qr_token`.
Không có migration Table mới. Integration test HTTP thật đã xác minh map, mở phiên,
đóng phiên và dọn xong; sau cleanup bốn bảng Table cùng tenant/branch test đều
trở về 0 row.

Khoảng trống schema cần duyệt trước khi thay đổi:

- `table_sessions` chưa lưu số khách hoặc người mở phiên.
- Chưa có partial unique index tại database để cấm hai liên kết active trên cùng
  bàn; service hiện bảo vệ bằng transaction `SERIALIZABLE`.
- Nếu cần bổ sung các trường/index trên, phải sinh SQL riêng và chờ người dùng xác
  nhận trước khi apply.

Menu Catalog map trực tiếp sáu bảng `categories`, `products`, `product_variants`,
`product_option_groups`, `product_options`, `branch_products`: 32 cột, 6 PK,
7 FK `NO ACTION`, unique variant theo `(product_id, size_name)` và unique sản phẩm
chi nhánh theo `(branch_id, product_id)`. Không có migration Catalog mới. HTTP
integration test thật đã xác minh scope branch, category filter, tìm kiếm ILIKE,
variant và option; tám bảng catalog/tenant/branch test đều trở về 0 row.

Ordering và Kitchen dùng chung dữ liệu nghiệp vụ có sẵn trong `orders`,
`order_items`, `order_item_options`, `order_item_events`. Ordering tạo snapshot
món/giá và event `CREATED`; Kitchen chỉ đọc order/item, cập nhật `order_items.status`
và thêm event `STATUS_CHANGED`. Các quan hệ đều `NO ACTION`, không có migration
Kitchen mới. PostgreSQL integration đã xác minh queue và hai transition, sau đó
kiểm tra lại order/item/event/catalog/identity thử nghiệm đều không còn dữ liệu.

Payment map trực tiếp bốn bảng `bills`, `bill_items`, `payments`, `refunds` trong
`public`; không tạo schema/bảng song song và chưa có migration Payment. Schema đủ
cho bill cơ bản nhưng còn thiếu dữ liệu vận hành quan trọng: `branch_id`/actor audit
trên payment, người in/thời điểm in và trạng thái in độc lập. Vì vậy foundation chỉ
mở create/read bill; chưa giả lập xác nhận thanh toán hoặc in bill thành công.
SQL đề xuất đang chờ duyệt tại `docs/PENDING_PAYMENT_AUDIT_MIGRATION.sql`; file
chưa được chạy và chưa có migration Payment nào được apply.

Nguồn đối chiếu: `D:\ki9\sep490\db\Database.sql`.

## Ownership logic trong schema `public`

- Identity Organization: `tenants`, `branches`, `users`, `roles`,
  `tenant_users`, `staff_assignments` và `refresh_sessions` sau khi được duyệt.
- Table Reservation: `table_areas`, `cafe_tables`, `table_sessions`,
  `table_session_tables`.
- Menu Catalog: `categories`, `products`, `product_variants`, option groups,
  options và `branch_products`.
- Ordering/Kitchen: `orders`, `order_items`, item options và events.
- Payments: `bills`, `bill_items`, `payments`, `refunds`.
- Workforce và Customer Engagement sở hữu các bảng còn lại theo domain.

Việc cùng dùng `public` không cho phép service truy cập tùy ý bảng của
domain khác. Ownership được giới hạn bằng DbContext, repository và API.

## Lịch sử audit migration

- Backup custom-format và schema-only đã được tạo; custom archive đọc
  được 188 TOC entries.
- Các migration `20261023032947_InitialIdentitySchema` và
  `20261023034706_AddRefreshSessions` theo schema `identity` chưa từng apply và
  đã được thay thế do không phù hợp database chính thức.
- Baseline public không chứa DDL; migration refresh chỉ thêm bảng cần
  thiết. Cả hai đã apply sau khi người dùng duyệt SQL.
