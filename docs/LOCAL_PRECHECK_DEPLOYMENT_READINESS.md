# Cash Precheck — điều kiện chạy Backend local

> Cập nhật 16/11/2026: gói SQL Dashboard local đã chuẩn bị và rehearsal 16 nhóm PASS trên PostgreSQL riêng; xem [CASHIER_DASHBOARD_LOCAL_MIGRATION_REVIEW.md](CASHIER_DASHBOARD_LOCAL_MIGRATION_REVIEW.md). Main chưa apply/GRANT; full-data backup/restore và rollout vẫn chờ duyệt. Các trạng thái container dừng/số test bên dưới là snapshot audit ngày 15/11, không thay thế kiểm tra runtime mới.

Ngày kiểm tra: 2026-11-15. Kết quả: **CODE PASS, LOCAL RUNTIME BLOCKED do schema**.

## Phạm vi đã được thực hiện

- Commit riêng Cash Precheck: Backend `694aca0`, Mobile `51ac435`. Không push.
- Checkout sạch: `D:\ki9\sep490\Sep490\AICOS-BE-local`, nhánh `local/cash-precheck`. Đây là Git worktree dùng chung lịch sử với AICOS-BE, không phải một Backend viết lại hoặc database mới. Không copy `.env`/credentials sang checkout.
- Giữ nguyên source V2/Item Transfer và cấu hình Barista local đang dở tại AICOS-BE; không đưa vào commit hoặc dùng để build. Không xóa/reset/stash phần này.
- Build solution trong checkout sạch: PASS, 0 errors, 2 warning cũ trong WorkforceReviewTests (CS0628) và OrderTests (CS8620). 10 test CashPrecheckTests PASS, 0 FAIL/SKIP; đây là test host/test doubles, không phải PostgreSQL E2E.
- Flutter kiểm tra lại formatter 7 file không đổi, analyzer 0 issues và 11 focused tests PASS. Regression trước đó: Flutter 358 PASS/7 SKIP; Backend 842 PASS/51 SKIP. E2E PostgreSQL/Redis/Gateway riêng của task Precheck: ON/OFF đều 56 nhóm PASS, 3 blocker cũ, 0 FAIL. Không tuyên bố kiểm thử điện thoại thật hoặc ngân hàng thật.

## Runtime được kiểm tra

Stack hiện tại là `aicos-main`, PostgreSQL container `aicos-main-postgres`, cluster **7703616372368646182**, volume sẵn có `aicos-main_postgres-data`, PG16. Host binding PostgreSQL `127.0.0.1:5433`; đây là cluster Docker với database/schema riêng theo service, **không phải Windows PostgreSQL `aicos_db.public`**.

Toàn bộ service ứng dụng đã dừng trước kiểm tra. Đã xác minh PG_VERSION/pg_controldata trên volume gắn read-only trước khi bật riêng PostgreSQL để audit. Không chạy Compose up, init schema, migration hoặc seed. Mọi truy vấn database nằm trong `BEGIN READ ONLY ... COMMIT`. PostgreSQL đã dừng lại sau kiểm tra; các container khác không bị start/restart/recreate.

Các container .NET hiện có đều cấu hình `Database__MigrateOnStartup=false`. Chúng dùng image local cũ; Ordering dùng `aicos-main/order:session-guard-20261112`. Không có biến bật SessionBilling/CashierShifts trong các container đã inspect, tương ứng mặc định OFF trong source. Không đổi flags, JWT, mật khẩu hoặc ACL.

## Kết quả schema thực tế

Số bảng/cột dưới đây tính cả EF migration history. Fingerprint từng bảng và metadata cột tại [LOCAL_PRECHECK_SCHEMA_READINESS.json](LOCAL_PRECHECK_SCHEMA_READINESS.json). Không lưu giá trị nghiệp vụ hoặc credentials trong báo cáo.

| Database / schema | Bảng | Cột | Chênh lệch với EF model sạch |
| --- | ---: | ---: | --- |
| aicos_identity / identity | 11 | 134 | Không thiếu bảng/cột |
| aicos_menu / menu | 6 | 49 | Không thiếu bảng/cột |
| aicos_table_reservation / table_reservation | 3 | 26 | Không thiếu bảng/cột |
| aicos_ordering / ordering | 7 | 94 | Thiếu 6 bảng, 4 cột |
| aicos_kitchen / kitchen | 12 | 160 | Thiếu 1 cột |
| aicos_payment / payment | 7 | 92 | Thiếu 8 bảng |
| aicos_reporting / reporting | 2 | 18 | Thiếu 1 bảng |
| Tổng 7 database đã audit | **48** | **573** | **15 bảng, 5 cột** |

Không có khác biệt nullable trên các cột đã tồn tại trong so sánh này. Đây là kiểm tra hiện diện/nullable và migration history; chưa phải chứng nhận toàn bộ type length, FK/CHECK/index/ACL tương đương. Quyền cho bảng mới phải được review trước rollout, không tự GRANT.

### Những phần thiếu

- Ordering: `kitchen_admission_outbox`, `kitchen_admission_reconcile_state`, `fulfillment_receipts`, `order_fulfillment_projection`, `session_checkouts`, `table_transfer_operations`.
- Ordering `orders.fulfillment_mode`; `order_feedbacks.replied_at_utc`, `replied_by_user_id`, `reply`.
- Kitchen `kitchen_tickets.table_transfer_version`.
- Payments: `session_bills`, `session_bill_orders`, `session_bill_attempts`, `session_bill_events`, `cashier_shifts`, `collection_outbox`, `bill_split_plans`, `bill_split_operations`.
- Reporting: `collection_facts`.

Các query materialize entity Order/KitchenTicket có thể tham chiếu cột mới ngay cả khi feature OFF. Vì vậy không coi tắt flag là đủ để chạy image mới trên schema cũ. Riêng GET Cash Precheck khi enforcement OFF không cần đọc bảng ca, nhưng chưa đủ để chứng minh toàn bộ luồng thanh toán hoạt động.

## Migration cần review riêng — chưa thực thi

Các migration sau có trong source sạch nhưng chưa ghi nhận trong history local:

| Service | Thứ tự trong service | SQL nguồn / runbook |
| --- | --- | --- |
| Ordering | 20261106164220_KitchenAdmissionOutbox → 20261106183340_OrderingCupFulfillmentFoundation | docs/migrations/kitchen-recovery/01_ordering_generated.sql; docs/migrations/cup-serving/02_ordering_expand.sql |
| Ordering | 20261113125751_OrderingSessionCheckout | docs/migrations/session-billing/01_ordering_expand.sql |
| Ordering | 20261113151937_FeedbackReply → 20261114164841_DurableTableTransfer | EF FeedbackReply; docs/migrations/table-transfer-split/01_ordering_expand.sql |
| Kitchen | 20261114164849_TableTransferRoutingVersion | docs/migrations/table-transfer-split/02_kitchen_expand.sql |
| Payments | 20261113125804_PaymentSessionBills → 20261114125030_CashierMoneyShifts → 20261114164856_BillSplitPlans | docs/migrations/session-billing/02_payments_expand.sql; docs/migrations/cashier-shifts/01_payments_expand.sql; docs/migrations/table-transfer-split/03_payments_expand.sql |
| Reporting | 20261114125038_ConfirmedCollectionInbox | docs/migrations/cashier-shifts/02_reporting_expand.sql |

Đây là **danh sách prerequisite cần duyệt, không phải lệnh triển khai đã được phê duyệt**. Không chạy lại KitchenAdmissionReceipts/CupServing/Barista đã có trong Kitchen history; không dùng baseline tạo database mới lên volume hiện tại. Không chạy trực tiếp SQL generated thiếu guard đích. Trước apply cần tạo gói SQL đúng phần thiếu, xác minh cluster/database/schema, lịch sử, FK/index/constraint, quyền role và dừng nếu có đối tượng cùng tên khác cấu trúc.

30 file trong 5 manifest `kitchen-recovery`, `cup-serving`, `session-billing`, `cashier-shifts`, `table-transfer-split`: SHA256 PASS theo bytes hiện tại. Không sửa SQL/EF migration hoặc checksum trong lượt này. Không chạy EF database update và không bật MigrateOnStartup.

## Dữ liệu được bảo toàn

Audit trước/sau: **48/48 bảng giữ nguyên số dòng và fingerprint**, số cột không đổi. Các dữ liệu hiện có gồm 14 orders, 6 table_sessions, 14 Kitchen tickets, 2 payments, 50 products, 120 dining tables và 9 users; không backfill hoặc tạo dữ liệu thử vào đây. Mọi container local giữ nguyên ID/image reference; PostgreSQL được trả về stopped, các Backend service vẫn stopped.

Hai payments và những phiên/order cũ cần đối soát trước khi bật SessionBilling. Không tự tạo Bill PAID từ dữ liệu cũ, không replay event hoặc sửa trạng thái để làm test PASS.

## Checkout sạch và các lệnh kiểm tra đã chạy

```powershell
cd D:\ki9\sep490\Sep490\AICOS-BE-local
rtk git status --short
dotnet build AICOS.sln --nologo -v minimal
$env:DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE = 'false'
dotnet test tests/AICOS.Services.IntegrationTests/AICOS.Services.IntegrationTests.csproj --no-build --filter 'FullyQualifiedName~CashPrecheckTests' --nologo -v minimal
```

Không có lệnh khởi động Backend hoặc mutation database nào được thực thi từ checkout này. Không publish port, tạo volume, role, JWT hoặc account mới.

## Điểm chờ phê duyệt trước khi chạy thử local

1. Duyệt **chỉ** gói SQL delta/prerequisite cho các database Docker nêu trên; xem SQL/GRANT cụ thể, không đồng nhất với phê duyệt database Windows hoặc production.
2. Sau khi được duyệt: backup từng database bị ảnh hưởng, checksum, restore drill trên cluster test riêng; kiểm tra dữ liệu cũ và rehearsal delta trên bản sao. Không dùng rollback DROP khi đã có ledger/audit; giữ schema mới và rollback runtime nếu có phát sinh dữ liệu.
3. Apply từng stage đúng target sau guard, kiểm tra exit code/history/schema/ACL/fingerprint; lỗi thì dừng, không tự rollback destructive.
4. Duyệt cấu hình rollout local riêng: dùng credentials hiện có qua environment an toàn, giữ MigrateOnStartup=false, chưa bật enforcement production. Không lấy nguyên dirty V2 compose làm cấu hình đã kiểm thử.
5. Build và khởi động từng service cần thiết từ checkout sạch, health/metadata và xác minh precheck qua Gateway. Chỉ sau khi có dữ liệu/fixture test được phép mới kiểm thử thao tác thu tiền; không dùng payments thật để smoke test.
6. Sau cùng xác minh ADB/reverse theo thiết bị rồi chạy Flutter; test mở ca/recheck/Cash/QR/đổi actor thực tế được ghi riêng, không coi regression host là thiết bị thật.

Chưa thực hiện các bước 1–6. Hiện **không đủ điều kiện chạy luồng Cashier/Bill mới trên database local đang dùng**. Máy in thật, QR ngân hàng, Refund và Item Transfer V2 vẫn ngoài phạm vi.
