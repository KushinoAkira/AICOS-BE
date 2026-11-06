# P0 Kitchen admission — SQL để duyệt

> Implementation đã có EF migrations và SQL generated tại `../../migrations/kitchen-recovery/`; xem `../../KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md`. Các proposal/checksum ở thư mục này được giữ nguyên làm mốc review, không chạy cả proposal lẫn EF migration để tạo cùng object.

**Chưa chạy. Không phải migration đã được phê duyệt.** Thiết kế đầy đủ: `../../KITCHEN_ORDER_ADMISSION_RECOVERY_P0_DESIGN.md`.

| File | Đích theo source main | Nội dung |
| --- | --- | --- |
| 01_ordering_expand.sql | Database của Ordering, schema ordering | Outbox và reconcile checkpoint, 2 index mới |
| 02_kitchen_expand.sql | Database của Kitchen, schema kitchen | Receipt/inbox, 2 index mới và cột recovery_hold_reason nullable |

Không target PostgreSQL Windows hoặc schema public của Backend cũ. Mỗi script có transaction riêng, lock_timeout 3 giây và statement_timeout 30 giây. Cần DBA kiểm tra connection/database/schema thực tế rồi đặt session approval settings như header; file không tự chọn connection hoặc có password.

Preflight trước khi dùng ở môi trường được duyệt:

1. Đối chiếu EF model/migration history và cấu trúc DB; nếu bảng/cột đề xuất đã tồn tại thì dừng để diff, không tạo lại hoặc skip mù.
2. Xác nhận Kitchen đã có unique index hợp lệ trên tenant_id/order_id, không partial theo trạng thái. Không tạo index tương đương thứ hai.
3. Kiểm tra kiểu và PK kitchen_tickets.id; FK receipt là nội bộ Kitchen. Outbox giữ logical OrderId và không áp thêm chính sách xóa Order; không có FK xuyên service.
4. Kiểm tra backup/test restore, quyền DDL, lock risk và checksum trước migration. Hash trong SHA256SUMS.txt là bytes của bản review hiện tại; đổi line ending hoặc SQL thì tính lại và review lại.
5. Tạo EF migrations riêng sau khi thiết kế được duyệt, đối chiếu snapshot/SQL; kiểm thử trên database cô lập với phê duyệt schema mới. Không sửa migration đã chốt, không MigrateOnStartup.

Thứ tự dự kiến khi được duyệt triển khai: expand hai DB (thứ tự 01 rồi 02 chỉ để dễ review) → receiver/auth/queue hold filter → atomic capture → dispatcher → reconciliation. Workers mặc định false; chưa bật backfill chỉ vì DDL đã chạy. Reconciliation sẽ ghi dữ liệu operational vào outbox/receipt, cần phê duyệt riêng trong bước thực thi tương ứng.

Không có DROP/TRUNCATE, DML xóa/sửa dữ liệu nền, seed/backfill hoặc GRANT trong hai file. `ON DELETE RESTRICT` trong FK receipt chỉ định chính sách quan hệ, không phải lệnh DELETE. FK này ngăn xóa ticket đã có receipt; cần review retention nếu sau này thêm API xóa.

Rollback ưu tiên tắt capture/worker/reconcile, giữ các bảng và dữ liệu. Không chạy binary cũ bỏ qua hold filter khi còn ticket hold. Chưa cung cấp destructive Down SQL; downgrade cần backup và kế hoạch riêng được duyệt.

Trạng thái: static review bằng source/document; PostgreSQL syntax/application/rollback tests cho SQL mới **NOT RUN**. Không coi checksum hoặc transaction wrapper là bằng chứng migration đã an toàn trên database thật.
