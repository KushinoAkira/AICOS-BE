# Kitchen admission recovery — EF SQL review artifacts

Hai script generated **chưa được chạy trên database vận hành**. Chúng được xuất từ EF migration additive mới; không thay thế backup/preflight/quyền DB và phê duyệt rollout.

| File | Service migration |
| --- | --- |
| 01_ordering_generated.sql | 20261106164220_KitchenAdmissionOutbox, từ baseline TableBillRequest |
| 02_kitchen_generated.sql | 20261106164227_KitchenAdmissionReceipts, từ baseline TicketRemake |

SQL idempotent dựa trên history `__ef_migrations_history` của schema từng service. Nếu object đã có nhưng migration history chưa ghi đúng, dừng và đối chiếu; không assume wrapper sẽ sửa cấu trúc khác. Không chạy các proposal SQL cũ cùng generated migration.

Migrations đã được apply trên hai DB PostgreSQL trống tạm có guard qua full migration chain khi chạy `scripts/Test-StaffOperationsInfrastructure.ps1 -Run -Recovery`. Main DB/schema/data và containers đã có không thay đổi.

Generated Up chỉ thêm 3 bảng/các index mới và 1 cột nullable, không tạo lại unique ticket tenant/order, không cross-service FK. FK receipt→ticket có RESTRICT và index mới hỗ trợ FK. EF app cung cấp mọi giá trị bắt buộc, không cần server defaults như proposal ban đầu. Chi tiết CHECK/unique index được ghi trong implementation report.

Appsettings, launch profiles và override order/kitchen trong Compose giữ MigrateOnStartup=false. Feature flags false chưa đủ để chạy binary mới vào schema cũ vì Kitchen queries map cột hold. DDL/ACL và receiver phải sẵn sàng trước enable capture/dispatcher/reconcile.

Generated Down có xóa object mới và cột hold: không tự chạy, không dùng làm rollback dữ liệu đã vận hành. Rollback mặc định bằng feature flags, giữ ledger/checkpoint/hold guard. Xem `../../KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md`.

SHA256SUMS.txt là checksum bytes của SQL generated hiện tại; thay đổi SQL hoặc line endings cần hash/review lại. Không có secret hoặc connection string vận hành trong các file này.
