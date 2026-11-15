# Cashier Dashboard local — gói SQL để review

Ngày 16/11/2026. **PREPARED / CHƯA DUYỆT ÁP DỤNG MAIN.**

## Đích và phạm vi

- PostgreSQL Docker aicos-main-postgres, container prefix c014190eed2d, cluster 7703616372368646182.
- Bốn database/schema có sẵn: aicos_ordering.ordering, aicos_kitchen.kitchen, aicos_payment.payment, aicos_reporting.reporting.
- Không phải PostgreSQL Windows aicos_db.public. Không tạo database vận hành mới.
- 10 migration đã có trong source, bổ sung **15 bảng và 5 cột**, không sửa EF migration đã chốt.
- Baseline tương ứng Backend ổn định có Dashboard/Cash Precheck. Không thêm sáu bảng Item Transfer V2; không bật CupV1/V2, Split Bill/group collection, Table Transfer hoặc enforcement ca.
- Các bảng transfer/split baseline thuộc chuỗi migration/model ổn định; có schema không đồng nghĩa được bật nghiệp vụ. Không backfill hoặc tạo Bill PAID từ đơn cũ.

| Service | Từ history local | Đến migration | Phần bổ sung |
| --- | --- | --- | --- |
| Ordering | 20261101173630_TableBillRequest | 20261114164841_DurableTableTransfer | 6 bảng; orders.fulfillment_mode, 3 cột trả lời feedback |
| Kitchen | 20261114041318_KitchenBaristaWorkflow | 20261114164849_TableTransferRoutingVersion | kitchen_tickets.table_transfer_version |
| Payments | 20261021102642_ConstraintsAndIndexes | 20261114164856_BillSplitPlans | 8 bảng Bill/attempt/events/ca/collections/split baseline |
| Reporting | 20261021151155_OrderFacts | 20261114125038_ConfirmedCollectionInbox | collection_facts |

Fulfillment mode mặc định WholeOrder, table_transfer_version mặc định 0, ba cột feedback nullable. Đây là giá trị cho cột mới, không sửa trạng thái/tiền cũ. Forward chỉ CREATE/ALTER ADD/index/constraint và INSERT history mới; không DROP/TRUNCATE/DELETE/UPDATE dữ liệu cũ. FK nội bộ service; UUID không cần sequence mới.

## Các file và thứ tự cần duyệt

1. 00_preflight.READ_ONLY.sql ở từng database: pin identity/cluster/schema; báo cột/PK/FK/CHECK/index/connections.
2. 01_ordering_expand.sql.
3. 02_kitchen_expand.sql.
4. 03_payments_expand.sql.
5. 04_reporting_expand.sql.
6. Sau duyệt quyền riêng: 10_ordering_grants.PROPOSAL.sql, 11_kitchen_grants.PROPOSAL.sql, 12_payment_grants.PROPOSAL.sql, 13_reporting_grants.PROPOSAL.sql.
7. Xác minh history, bảng/cột/constraints/indexes, kết nối/quyền role, fingerprints trước khi duyệt rollout.

Mỗi file là một transaction trong một database; không transaction xuyên service. Sai target/history/object đã có sẽ dừng, không tự tạo lại. Lần hai bị từ chối trước DDL. Không dùng EF database update thay gói đã review; giữ MigrateOnStartup=false.

Mỗi SQL yêu cầu đúng aicos.expected_database/schema/cluster. Tên main còn buộc cluster cụ thể ở trên; tên test/restore có prefix cố định và cluster khác main. Expand/grants/rollback yêu cầu approval setting riêng. History guard so sánh **toàn bộ IDs**, không chỉ count/max. Lock timeout 3 giây, statement timeout 60 giây. Lỗi thì dừng, không tự Down.

SHA256 tại SHA256SUMS.txt; LF pin bằng .gitattributes. Khi sửa thiết kế phải review revision/checksum; bộ sinh từ chối âm thầm ghi đè gói đã khác.

## Quyền

- Không tạo role/mật khẩu, không đổi ownership, không PUBLIC REVOKE hoặc thay ACL cũ.
- USAGE trên đúng schema.
- SELECT/INSERT trên bảng mới thuộc service; UPDATE giới hạn các cột workflow/lease/checkpoint/trạng thái/đối soát.
- Không cấp DELETE/TRUNCATE/history writes. Receipt, giá, manifest và payload nguồn giữ quyền ghi hạn chế theo SQL.
- Cột mới trên bảng cũ chỉ cấp SELECT/UPDATE tương ứng.
- Test dùng role không sở hữu schema/table, mật khẩu test ngẫu nhiên và đăng nhập PostgreSQL thật; kiểm tra allowed UPDATE và forbidden DELETE/immutable UPDATE.
- **Giới hạn:** main service roles đang sở hữu database/schema/bảng cũ. Gói chưa hardening toàn bộ ownership/quyền kế thừa. Không tự thu hồi quyền hiện có; DBA cần xét khi duyệt quyền vận hành.

## Rehearsal

Runner scripts/tests/local-dashboard-migration.mjs chỉ đọc main bằng BEGIN READ ONLY và pg_dump --schema-only với default_transaction_read_only=on. Không copy dữ liệu nghiệp vụ/tài khoản/provider keys/credentials main vào test.

Sau guard mới tạo PostgreSQL 16: network internal riêng, tmpfs, không host port, pin cluster khác main, credentials test ngẫu nhiên. Restore schema thực tế và migration metadata vào bốn DB test; thêm fixtures tổng hợp rồi kiểm tra:

- Preflight/expand/grants đúng scope.
- Refuse sai database/cluster, thiếu approval, history bị tráo dù count/max giữ nguyên và chạy lại SQL.
- Tất cả cột/type/nullability khớp EF baseline không V2; index mới tồn tại, constraints validated, FK không xuyên schema.
- Role login/ACL thật và fingerprints/row counts cột cũ không đổi sau expand.
- Custom-format backup dữ liệu mẫu đã migrate, restore bốn clone riêng, Down phần mới chưa dùng rồi Up lại.
- Refuse Down khi có checkpoint/routing history/ca/receipt mới.
- Main container IDs/images/state/start/restarts và fingerprints/counts giữ nguyên.
- Chỉ dọn tài nguyên scratch do lượt đó tạo, không reset Docker hoặc volume.

Kết quả cuối và checksum của đúng lượt chạy: [REHEARSAL_RESULT.json](REHEARSAL_RESULT.json). Không coi test này là đã triển khai local, Mobile E2E hoặc ngân hàng thật.

Lệnh tái lập từ Backend (không ghi main):

    dotnet build tests/AICOS.Billing.StackTools/AICOS.Billing.StackTools.csproj
    dotnet tests/AICOS.Billing.StackTools/bin/Debug/net8.0/AICOS.Billing.StackTools.dll local-dashboard-scripts "$env:TEMP\aicos-dashboard-local-sql"
    node scripts/tests/local-dashboard-migration.mjs "$env:TEMP\aicos-dashboard-local-sql"

## Backup thật trước khi xin duyệt apply

Script scripts/Backup-LocalCashierDashboard.ps1 mặc định chỉ PLAN. Run từ chối khi main application containers đang chạy hoặc còn client connection vào bốn database. Script không tự dừng service.

Sau phê duyệt maintenance riêng:

1. Pause writers trong cửa sổ đã thống nhất; bảo đảm không có client ngoài Docker ghi dữ liệu.
2. Chạy backup script với -Run: pin container/cluster; pg_dump -Fc READ ONLY; binary stream không qua PowerShell text redirection.
3. Files ngoài Git trong LOCALAPPDATA/AICOS/backups/dashboard-..., ACL chỉ current user và SYSTEM, có checksum/size/header PGDMP/manifest. Giữ riêng artifact partial, không dùng restore hoặc ghi đè backup cũ.
4. Restore bốn dump **vào cluster scratch mới**, pin cluster khác main trước CREATE/restore, không host port hoặc startup workers/providers.
5. Đối chiếu counts/fingerprints/constraints/history và quyền. Chỉ sau restore full PASS mới xin duyệt apply 01→04 và quyền 10→13.
6. Không restore đè main; lỗi thì dừng, không tự rollback destructive.

**Backup full dữ liệu local và restore full đó chưa chạy.** Rehearsal hiện dùng schema-only main và fixtures tổng hợp để không sao chép secrets/dữ liệu đang sử dụng trước maintenance.

## Rollback chỉ khi chưa sử dụng

90_ordering_rollback.UNUSED_ONLY.sql, 91_kitchen_rollback.UNUSED_ONLY.sql, 92_payment_rollback.UNUSED_ONLY.sql, 93_reporting_rollback.UNUSED_ONLY.sql **có DROP bảng/cột mới và DELETE đúng history mới**.

Chỉ sau duyệt rollback riêng, writers dừng, không có dữ liệu/audit trong phần mới, cột mới còn default/NULL và không migration tiếp theo. Scripts khóa bảng trước kiểm tra. Không tự chạy khi stage thất bại.

Có dữ liệu thì giữ schema và forward repair/reconciliation. Không drop ledger hoặc trở về binary cũ rồi thu lại tiền. Không có rollback dữ liệu tài chính bằng Down; bốn transaction không nguyên tử xuyên database.

## Các điểm còn cần duyệt

- Maintenance/pause writers để backup full và restore drill full riêng.
- Schema: đúng 4 expand scripts/checksum sau backup/restore PASS.
- Quyền: 4 GRANT scripts và xem ownership/quyền kế thừa hiện tại.
- Runtime: build/deploy bản Backend được duyệt, giữ MigrateOnStartup=false và feature OFF trước smoke. Không lấy nguyên dirty V2 làm rollout được chứng nhận.
- Feature activation và test có ghi dữ liệu duyệt riêng. Không tạo giao dịch thật để smoke test.

Khi bật CashierShifts:Enabled/dispatcher, Payments có thể đối soát payment thành công cũ thành collection receipts rồi đồng bộ Reporting. Cần duyệt/đối soát lịch sử trước khi bật worker này; không tự gán khoản cũ vào ca mới hoặc sửa ca đã chốt. QR vẫn theo chi nhánh, không vào két Cash.

App vẫn báo chưa khả dụng tới khi schema/quyền/image/flags được cập nhật theo các gate trên. Task này không thay business API/UI, bật feature, deploy, commit hoặc push.
