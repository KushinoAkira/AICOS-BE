# Mở/kết ca qua Flutter HTTP thật — 15/11/2026

## Phạm vi

Tiếp tục phần thiếu của Dashboard E2E: thao tác mở và kết ca bằng `MainCashierShiftScreen`/`CashierShiftMoneyForm`, thay cho việc runner gọi POST mở/kết ca hộ Flutter. Giữ nguyên UI, API, schema và chính sách tài chính hiện tại. Không giải quyết Identity đa role, tổng Bill toàn chi nhánh hoặc gross/net/refund/Manager policy khi chưa được duyệt.

## Các kiểm tra mới

- Test `test/cashier_shift_live_stack_test.dart`, hai bước OPEN/CLOSE, chỉ chạy khi runner cấp môi trường test rõ ràng. HTTP dùng IOClient thật và đăng nhập Identity qua Gateway.
- Mở form, nhập tiền/ghi chú nhưng chưa tích checkbox: nút xác nhận bị khóa, GET lại danh sách ca chứng minh chưa có mutation.
- Xác nhận mở ca 100.000đ qua UI; Backend lưu đúng actor/branch, chỉ một ca OPEN; tải lại không tạo ca mới.
- Dùng Cash UI test hiện có: Bill 35.000đ, đưa 50.000đ, trả 15.000đ. Khoản Cash thuộc đúng ca vừa mở.
- Gửi close với version trước khoản Cash: phải trả 409 `cashier_shift.stale`, ca vẫn OPEN và version không bị thay đổi bởi request bị từ chối.
- UI đọc lại snapshot rồi xác nhận kiểm đếm 135.000đ, chênh lệch 0, ghi chú `Mobile matched count`. Xác minh CLOSED và lịch sử sau reload.
- Retry close bằng đúng số tiền/ghi chú đã chốt không thay version/thời điểm đóng/số kiểm đếm. Đóng ca không đóng Bill hoặc phiên bàn đang còn món chưa phục vụ.

## Môi trường và kết quả

Backend source dùng worktree sạch từ `27ab924`, giữ nguyên Item Transfer V2 và cấu hình Barista local đang dở ở repository chính. Runner sử dụng PostgreSQL tmpfs/Redis/network/JWT/credentials test riêng, guard container/cluster trước SQL và snapshot container có trước. Chỉ bật yêu cầu ca trong test, không dùng `aicos_db` hoặc PostgreSQL Windows.

- Formatter/analyzer: PASS, 0 issues.
- Flutter regression mới: 347 PASS, 6 SKIP. Hai SKIP thêm là OPEN/CLOSE live probes; không tính là API thật đã PASS khi chạy regression thường.
- Lượt E2E đầu bị Docker Engine tắt giữa chừng: FAIL hạ tầng, không tính PASS. Container test đã dừng được xác minh nhãn/network/no-volume rồi dọn riêng khi Engine hoạt động lại.
- Probe mở form ban đầu bị `pumpAndSettle` timeout vì màn cha giữ loading khi chờ form. Đã dùng khoảng pump hữu hạn cho animation; không sửa code ứng dụng hoặc bỏ assertion dữ liệu.
- Lượt E2E cuối: **53 nhóm PASS, 3 BLOCKED, 0 FAIL**, exit code **0**, PostgreSQL cluster `7708345966499213353`. OPEN/CLOSE UI và stale-version 409 qua Gateway/PostgreSQL đều PASS. Khoản thu **280.000đ** = Cash **210.000đ** + QR **70.000đ**, **6 receipt**, khớp Payment/Reporting/Dashboard. Hai lượt có race có thể ra số tiền khác; so theo chính ledger của lượt đó.
- Snapshot container có trước không thay đổi; toàn bộ container/network của lượt test cuối đã dọn. JSON bằng chứng nằm tại Backend `docs/CASHIER_DASHBOARD_ENFORCED_EVIDENCE.json`.

Không thay Backend nghiệp vụ, Mobile production code, migration, credentials vận hành hoặc feature vận hành. Đây là host widget/HTTP integration, không phải thao tác trên Android hay ngân hàng thật. Chỉ commit phần đã kiểm chứng; không push.
