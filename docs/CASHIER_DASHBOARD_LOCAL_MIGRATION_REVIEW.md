# Cashier Dashboard local — migration review result

Ngày 16/11/2026. **READY FOR REVIEW, NOT DEPLOYED.**

## Kết quả

- Root cause đã xác minh: image Payments/Reporting local chưa có Bill/ca/collections routes; log trả 404 và OpenAPI không có endpoint. Gateway vẫn trả 200; không phải lỗi USB ở lượt kiểm tra Dashboard.
- Main Docker cluster 7703616372368646182 được đọc bằng READ ONLY; không phải PostgreSQL Windows aicos_db.
- Đã tạo gói SQL đúng **10 migrations, 15 bảng mới và 5 cột**, tái sử dụng EF đã chốt. Không thay đổi migration cũ, không thêm bảng V2.
- PostgreSQL test cluster cuối: **7708466442335600681**, network internal/tmpfs riêng, không host port.
- **16 nhóm rehearsal PASS, 0 FAIL**. Evidence [REHEARSAL_RESULT.json](proposals/cashier-dashboard-local/REHEARSAL_RESULT.json) khớp toàn bộ 13 SQL checksum đang review.
- 24 bảng gốc trong bốn database: fingerprint/row counts trên các cột gốc không đổi trước/sau. Tất cả container có trước giữ nguyên ID/image/state/start/restart count.
- Chỉ dọn tài nguyên scratch của runner. Không apply/GRANT vào main, không khởi động/restart/recreate service vận hành, không bật flags hoặc dùng credentials main trong test.

## File bàn giao

Gói đầy đủ và thứ tự: [proposals/cashier-dashboard-local/README.md](proposals/cashier-dashboard-local/README.md).

| Nhóm | File |
| --- | --- |
| Preflight | 00_preflight.READ_ONLY.sql |
| Expand | 01_ordering_expand.sql → 02_kitchen_expand.sql → 03_payments_expand.sql → 04_reporting_expand.sql |
| Quyền đề xuất, gate riêng | 10_ordering_grants.PROPOSAL.sql, 11_kitchen_grants.PROPOSAL.sql, 12_payment_grants.PROPOSAL.sql, 13_reporting_grants.PROPOSAL.sql |
| Rollback chỉ khi chưa dùng, không tự chạy | 90_ordering_rollback.UNUSED_ONLY.sql, 91_kitchen_rollback.UNUSED_ONLY.sql, 92_payment_rollback.UNUSED_ONLY.sql, 93_reporting_rollback.UNUSED_ONLY.sql |
| Chứng cứ | SHA256SUMS.txt, REHEARSAL_RESULT.json, .gitattributes LF |
| Backup chuẩn bị | scripts/Backup-LocalCashierDashboard.ps1, mặc định PLAN; không tự pause services |
| Bộ sinh/rehearsal | scripts/lib/local-dashboard-sql.mjs; scripts/tests/local-dashboard-migration.mjs |
| Export offline | tests/AICOS.Billing.StackTools/Program.cs, thêm mode local-dashboard-scripts, chỉ GenerateScript với port 1 |

Không sửa public API, business code, UI hoặc financial policy trong task này. Source V2/Barista và những thay đổi Mobile có trước được giữ nguyên.

## Điều đã kiểm chứng

- Target/container/cluster/schema đúng, main connections READ ONLY.
- Khớp toàn bộ predecessor history; column type/nullability hiện có không khác EF baseline.
- Forward không có DROP/DELETE/TRUNCATE/UPDATE dữ liệu cũ; không có V2 DDL hoặc FK xuyên service.
- Mỗi script transaction riêng, timeout rõ; không có object cùng tên được tự bỏ qua.
- Sai cluster/database, thiếu approval, history bị thay một ID dù count/max giữ nguyên và chạy lại stage đều bị từ chối.
- Schema sau Up khớp toàn bộ cột EF baseline; index mới có mặt, FK cùng schema, constraints validated.
- Bốn role test LOGIN thật: SELECT/INSERT privileges đúng, column-limited workflow UPDATE được phép, DELETE/immutable UPDATE bị từ chối.
- Backup custom-format dữ liệu tổng hợp và restore vào clone riêng; unused Down/Up giữ nguyên dữ liệu gốc.
- Down từ chối checkpoint/routing history/ca/receipt đã tồn tại.
- Scripts mới JS syntax/PowerShell parser/PLAN và whitespace/checksum PASS.
- Lượt đầu FAIL do thiếu USAGE schema cho role test; bổ sung đúng quyền này vào proposal, cập nhật draft checksum trước review, chạy lại PASS. Không tính lượt đầu là PASS; tài nguyên test dở đã được pin label/network/cluster rồi dọn riêng.

## Tests chạy trong lượt này

| Nhóm | Kết quả | Giới hạn |
| --- | --- | --- |
| StackTools build/export offline | PASS, 0 warning/error | Không mở kết nối DB lúc export |
| SQL/ACL/backup/restore/rollback rehearsal | 16 nhóm PASS, 0 FAIL | PostgreSQL thật; schema-only main + fixtures tổng hợp, không phải bản sao dữ liệu main |
| Backend CashPrecheck/CashierShift/SessionBillFlow | 32 PASS, 0 SKIP/FAIL | TestServer/doubles, không phải HTTP tới stack main |
| Flutter cashier_dashboard_test | 13 PASS | Widget/HTTP doubles; không phải Android live E2E |
| Flutter formatter/analyzer | 0 formatting changes, 0 issues | Không thay code UI |
| Backup full main | NOT RUN | Cần maintenance/pause writers và backup ngoài Git với ACL riêng |
| Restore từ backup full main | NOT RUN | Không được thay bằng bằng chứng fixture restore |
| Runtime Dashboard mới trên local | BLOCKED | Chờ schema/quyền/rollout/feature approval |
| Main migration/GRANT | NOT RUN | Chưa được duyệt |
| QR ngân hàng/máy in thật | NOT RUN | Ngoài phạm vi |

Không chạy lại full Backend/Flutter regression do chỉ thay test tooling/SQL proposal/docs; các count full regression trước đó không được đổi tên thành kết quả mới của gói này.

## Rủi ro và các gate còn lại

1. **Maintenance + backup/restore full:** cần duyệt tạm dừng writers. Backup script từ chối khi service main chạy hoặc còn client connection; không tự stop. Dumps binary và manifest lưu riêng dưới LOCALAPPDATA/AICOS/backups, ACL current user/SYSTEM; cần restore test/full-data fingerprints trước khi apply.
2. **Schema:** duyệt đúng bốn expand/checksum sau full restore PASS. Đây là ALTER/CREATE thật, có khóa metadata; rehearsal fixture nhỏ chưa chứng minh thời gian khóa trên dữ liệu vận hành. Không tự sửa/xóa dữ liệu cũ hoặc mark history.
3. **Quyền:** GRANT proposal chỉ thêm quyền cụ thể, không sửa PUBLIC/ownership/role/password. Roles main đã sở hữu database/schema/bảng cũ nên không tuyên bố hardening least privilege toàn bộ hệ thống.
4. **Runtime:** build đúng snapshot được duyệt, giữ MigrateOnStartup=false và features OFF trước smoke. Việc SQL PASS không chứng minh image cũ hiện tại đã có API mới.
5. **Feature/data:** bật CashierShifts dispatcher sẽ có thể tạo receipts đối soát từ payments thành công cũ; cần duyệt và kiểm tra lịch sử, không tự gán khoản cũ vào ca mới. QR không vào két Cash. Các phiên legacy đã thu tiền phải được xử lý qua reconciliation, không thu lại bằng Bill mới.

Down có thao tác DROP phần mới và DELETE history mới; chỉ dùng khi rỗng/chưa dùng dưới phê duyệt riêng. Có ledger/audit/custody mới thì giữ schema và forward repair; không auto rollback hoặc restore đè main.

**App chưa được sửa bằng cách bật cờ tạm.** Nó vẫn báo chưa khả dụng cho tới khi các gate triển khai được duyệt và thực hiện. Không commit/push/deploy trong lượt này.

## Cập nhật source remote trước khi triển khai — 16/11/2026

Remote origin/main đã tiến tới e10c499 và diverge với local 7f24e10 (6 commit remote, 4 commit local). Upstream bổ sung migration Identity TenantPaymentAndSoftDelete, Payments RefundToBankAccount và Workforce CashHandoverAtShiftStart; có thay đổi Order/Payment contracts, authorization và nghiệp vụ ca/hoàn tiền.

Gói SQL ở báo cáo này được kiểm thử với baseline local 7f24e10, **không phải chứng nhận tương thích với main mới e10c499**. Chỉ push nhánh review, chưa merge/force-push main. Phải đối chiếu dependencies và hợp đồng tài chính upstream trước khi duyệt apply schema hoặc rollout image. Phần V2 và cấu hình runtime đang dở không thuộc commit gói Dashboard.

Người dùng đã duyệt riêng backup/restore full, chưa migration. Việc chuẩn bị/kiểm chứng backup không được xem là phê duyệt các migration mới trên remote hoặc kích hoạt financial workers.

Commit preflight: đã build và rehearsal đúng snapshot Git index không V2: build 0 warning/error, 16 nhóm PASS. Chuẩn hóa LF trong bộ sinh trước khi hash; checksum được kiểm tra lại theo bytes Git index để không hỏng sau checkout Windows.

## 16/11/2026 — Backup/restore full đã được duyệt và hoàn tất

Bốn dump đầy đủ đã tạo ngoài Git, ACL chỉ current user/SYSTEM; kiểm tra size/SHA256/header PGDMP PASS. Backend được tạm dừng theo phê duyệt rồi khôi phục đúng IDs/images cũ; Gateway HTTP 200. Không chạy migration/GRANT hoặc bật feature.

Restore sang cluster internal/tmpfs mới, backup bind read-only: 7/7 nhóm PASS, mọi bảng/data fingerprint/schema/PK/FK/CHECK/index/history khớp. Xem CASHIER_DASHBOARD_FULL_RESTORE_RESULT.json. Không copy dump hoặc manifest chứa inventory vào Git. Chỉ role names NOLOGIN tạo trong test; không khởi động provider/API worker trên dữ liệu restore.

Lượt đầu backup lỗi quoting SQL trên Windows đã dừng và resume Backend trong finally; sửa sang stdin rồi backup thành công. Checker ban đầu báo khác CHECK/index do parser/deparser biểu diễn ngoặc/cast khác nhau; chuẩn hóa bằng parser PostgreSQL trên TEMP table trong test, giữ nguyên uniqueness/predicate và constraint, rồi chạy lại PASS. Không tính các lượt FAIL là PASS.

Full restore dùng --no-owner/--no-acl; không chứng nhận quyền vận hành đã cấp. Gói SQL vẫn chờ duyệt và cần đối chiếu với origin/main mới trước rollout; chưa merge main. Các phần NOT RUN trong báo cáo ban đầu là checkpoint trước bước được duyệt này.


## Upstream review — 2026-11-16

See [DASHBOARD_MAIN_SYNC_RESULT.md](DASHBOARD_MAIN_SYNC_RESULT.md) and the separate [upstream supplement](proposals/cashier-dashboard-upstream/README.md). The original package remains frozen; five upstream migrations and Identity/Workforce full backup requirements must be reviewed before local rollout.
