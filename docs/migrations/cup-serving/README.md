# Cup Serving foundation — SQL review bundle

**Cập nhật 2026-11-07:** 7 checksum và hai EF migrations giữ nguyên; SQL đã được áp dụng và kiểm tra trên cluster test disposable sau guard/preflight. **Chưa apply database vận hành.** API và kết quả mới ở [CUP_SERVING_V2_IMPLEMENTATION.md](../../CUP_SERVING_V2_IMPLEMENTATION.md); phần mô tả foundation chưa map API bên dưới là lịch sử review.

Đây là phần storage/core theo phê duyệt có điều kiện, **không phải rollout Item-level Serving**. Hiện không có public Claim API, DI registration/worker hoặc public create CupV1. Capabilities trả version 0. Chưa có PostgreSQL E2E Serving; không dùng bundle này trên DB chính khi chưa được duyệt apply riêng.

| File | Mục đích |
| --- | --- |
| `00_preflight.sql` | READ ONLY, bắt buộc expected DB/schema/system identifier; kiểm tra baseline và dừng nếu đã/partial apply |
| `01_kitchen_expand.sql` | EF generated: 4 bảng mới + 6 cột additive + constraint/index/FK nội bộ Kitchen |
| `02_ordering_expand.sql` | EF generated: 2 bảng mới + orders.fulfillment_mode + constraint/index/FK nội bộ Ordering |
| `SHA256SUMS.txt` | Checksum bundle để review, không phải chứng nhận đã chạy PostgreSQL |

EF migrations mới: `20261106183333_KitchenCupServingFoundation` và `20261106183340_OrderingCupFulfillmentFoundation`. Không sửa hai migration recovery hoặc migration cũ đã chốt; model snapshots được cập nhật cho migration mới. SQL sinh từ baseline `20261106164227_KitchenAdmissionReceipts` / `20261106164220_KitchenAdmissionOutbox`, không tạo lại toàn bộ database. Nếu baseline thực tế khác, dừng để đối chiếu, không chạy file từ `0` trên DB có sẵn.

Tái sử dụng: Kitchen ticket/items/stickers và admission receipt v1/v2; Ordering source OrderItem IDs, admission outbox và checkpoint tách consumer dự kiến. Gộp audit + idempotency thành `cup_operations` append-only nên giảm 5 Kitchen xuống 4. Không FK/read table xuyên service. Không thêm bảng giá, Bill hoặc Payment. Chi tiết reuse/decisions: `../../CUP_SERVING_CONDITIONAL_APPROVAL_REVIEW.md`.

## Trình tự chỉ sau phê duyệt apply riêng

1. Xác minh đúng deployment/database/schema của Backend main, server identifier, service stopped/write quiescence phù hợp; backup từng DB/schema sở hữu và restore drill vào test riêng. Không sử dụng runbook DB public/Backend backup cũ.
2. So checksum SQL và EF migrations, lịch sử migration, column/index names/definitions. Xác nhận quyền DBA và quyền đọc pg_control_system cho preflight; không tự GRANT để làm preflight PASS.
3. Chạy `00_preflight.sql` trên **từng connection riêng** với expected_schema kitchen/ordering, expected_database và cluster ID đã xác minh. PASS chỉ là preflight, không tự chạy stage tiếp theo.
4. Sau duyệt gate database: Kitchen `01`, kiểm tra exit code/history/count/FKs/index/row fingerprints. Sau đó Ordering `02` trên connection Ordering, kiểm tra tương tự. Hai DB không có distributed DDL transaction; stage hai fail không được tự rollback stage một.
5. SQL có transaction riêng và INSERT lịch sử EF; không bọc nó bằng script tự retry hoặc bật application MigrateOnStartup. Không ghi metadata applied nếu DDL chưa commit, không CREATE IF NOT EXISTS rồi giả định tương đương.
6. Sau expand: legacy rows phải WholeOrder, nullable correlation giữ null; không tự backfill claim/Served. Tổng bảng tăng đúng +4 Kitchen/+2 Ordering, cột cũ không mất. Live-claim index phải unique và chứa Reserved/Carrying/Overdue, FKs cùng schema, old unique tenant/order ticket không bị recreate.
7. Quyền service mới phải review riêng: SELECT/INSERT cho operations/inbox, không UPDATE/DELETE ledger; task/claim/projection/outbox cần SELECT/INSERT/UPDATE theo ownership. Không có GRANT/REVOKE trong bundle.
8. Không khởi động binary mới hoặc bật feature chỉ vì migration thành công. V2 admission, machine receiver/transport/reconciliation, chính sách Manager/TTL, Web guards còn lại và Dine-in session policy chưa đủ để rollout.

**Lưu ý deployment:** EF model mới map các cột mới ngay cả khi capabilities=0. Binary mới không tương thích DB chưa expand; không chạy nó vào DB vận hành hiện tại. MigrateOnStartup vẫn false; không restart/recreate container trong lượt chuẩn bị này.

## Lock/data risks và rollback

- ADD COLUMN DEFAULT WholeOrder/version=0 và ADD CHECK/FK/INDEX lấy lock; thời lượng chưa benchmark PostgreSQL thật. Chưa tự chuyển CreateIndex sang CONCURRENTLY vì nó cần bước ngoài transaction được review riêng. Chọn maintenance/lock_timeout và quan sát active writes với DBA.
- Up không DROP/TRUNCATE/DELETE/UPDATE business data. INSERT duy nhất do EF là migration history. Các cột nullable không tạo source identity giả. Preflight SQL chỉ metadata/READ ONLY, chưa chạy hoặc xác nhận syntax bằng PostgreSQL trong lượt này.
- EF-generated Down có DROP bảng/cột/index mới và có thể mất custody/audit. **Không chạy Down tự động**, không có file rollback destructive trong bundle. Khi chưa có CupV1 data, có thể rollback binary sau tương thích review; giữ expand schema là mặc định.
- Nếu đã có claim/Serve thật: dừng tạo CupV1 mới, giữ reader/command/Manager khả năng drain, không đổi mode về WholeOrder hoặc tự release. Trạng thái không rõ → hold/review/backup, không xóa ledger để reset.

Integration test plan và approvals còn thiếu ở `../../CUP_SERVING_CONDITIONAL_APPROVAL_REVIEW.md`; kết quả execution test mới ở `../../CUP_SERVING_V2_IMPLEMENTATION.md`. Bộ SQL chỉ apply test disposable, chưa apply database chính.
