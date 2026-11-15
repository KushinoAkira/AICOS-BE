# Cashier Dashboard — kiểm chứng full-stack 15/11/2026

## Phạm vi và nguồn kiểm chứng

Đã chạy HTTP thật qua Gateway, PostgreSQL và Redis test riêng. Flutter dùng `IOClient(HttpClient())`, đăng nhập qua Identity thật, đọc repositories/controller và render các màn AICOS hiện có. Đây là **host widget/HTTP integration**, không phải E2E thao tác trên điện thoại Android. MockClient không thay thế kết quả của stack này.

Backend đang có Item Transfer V2 viết dở và lỗi build ngoài phạm vi. Để giữ nguyên phần đó, dùng worktree tạm từ commit ổn định `9161e23`, cộng đúng diff Dashboard/Cashier của task này. Kết quả build/regression dưới đây thuộc tập source đó, **không khẳng định toàn bộ working tree V2 đã build được**. Không reset, sửa hoặc stage phần V2.

Services thật: Identity, Menu, Table, Ordering, Kitchen, Payments, Reporting, Workforce và Gateway. Workforce dùng để xác minh không cộng tiền ca vào ledger chấm công cũ. Không cần AI/Customer cho luồng tài chính này.

Mỗi lượt runner tạo network ngẫu nhiên, PostgreSQL 16 tmpfs không publish port/không volume, Redis riêng, JWT issuer/audience/key và credentials ngẫu nhiên riêng. Gateway chỉ publish loopback tại port ngẫu nhiên. Database Payments tên `aicos_billflow_test` theo guard Mock provider hiện có; các database còn lại là `aicos_billstack_*`. Không kết nối PostgreSQL Windows, `aicos_db` hoặc database test Windows cũ.

Trước mỗi SQL kiểm tra container ID, nhãn mục đích, network, no-host-port, tmpfs và PostgreSQL system identifier. Fixtures tenant/chi nhánh/nhân viên/món/Bill đều được tạo trong test, không sao chép dữ liệu hoặc secrets vận hành. `MigrateOnStartup=false`; migration/ACL chỉ được chuẩn bị và áp dụng rõ ràng sau guard trên cluster test. Cuối lượt so snapshot ID/image/start time/status/restart count của container có trước, rồi chỉ xóa tài nguyên do chính runner tạo.

## Kết quả thực tế

Cập nhật tiếp theo: [Cashier Shift UI E2E](CASHIER_SHIFT_UI_E2E.md) đã nâng lượt ON lên **53 nhóm PASS, 3 BLOCKED**, cluster `7708345966499213353`, gồm OPEN/CLOSE UI và stale version 409. Flutter hiện **347 PASS, 6 SKIP**; các số bên dưới ghi lịch sử lượt Dashboard đầu tiên. JSON ON trong Backend đã cập nhật sang lượt mới, đối soát 280.000đ/6 receipt.

| Bộ kiểm tra | Kết quả |
| --- | --- |
| Backend Services integration regression | 564 PASS, 51 SKIP, 0 FAIL |
| Backend BuildingBlocks / Identity / Smoke | 15 / 203 / 50 PASS, 0 FAIL |
| Tổng `dotnet test AICOS.sln` trên source ổn định + diff Cashier | **832 PASS, 51 SKIP** |
| Flutter regression, Demo define đúng | **347 PASS, 4 SKIP** |
| Flutter analyzer / formatter | **0 issues / PASS** |
| PostgreSQL/Redis/Gateway + Flutter, bắt buộc ca ON | **49 nhóm PASS, 3 BLOCKED**, cluster `7708113109921173545` |
| PostgreSQL/Redis/Gateway + Flutter, bắt buộc ca OFF | **50 nhóm PASS, 3 BLOCKED**, cluster `7708249897289920553`, exit code 0 |

51 Backend SKIP là các probe cần hạ tầng/biến môi trường riêng; không tính chúng là PostgreSQL đã chạy. Bốn Flutter SKIP khi chạy regression thường là hai probe Bill/ca live cũ, Dashboard live mới và thao tác Cash UI live mới. Runner gọi riêng các probe cần thiết bằng môi trường test. Test DTO/UI có MockClient và .NET harness dùng doubles/token test vẫn là regression, được phân biệt với stack đăng nhập Identity thật.

## Scenario và contract

| Scenario | Bằng chứng / giới hạn |
| --- | --- |
| Authentication | Identity thật tạo và đăng nhập Cashier/Barista; đổi mật khẩu đầu tiên trong fixtures; tên và branch Mobile khớp JWT/me. Không giả JWT nhiều role. |
| Branch/RBAC | Reporting/ca sai branch bị từ chối; Barista không được dùng API tài chính. Probe bổ sung Cash khác branch chấp nhận 403 hoặc 404 từ chối, không coi 404 là API thành công. |
| Mở ca | OPEN không phụ thuộc attendance; cùng key concurrent trả cùng ca; key khác không tạo ca OPEN thứ hai. |
| Cash theo Bill | Nhiều Order/một session/một Bill; khách đưa đủ và xác nhận nhận tiền; chỉ Backend chuyển PAID. Không thu riêng từng Order Dine-in. |
| Mất phản hồi | Proxy thật cắt response sau khi Backend commit Cash; GET và retry cùng key tìm lại đúng một payment. |
| Race | Hai Cashier thu cùng Bill chỉ một kết quả thành công; Add Order vs Cash không bỏ sót lượt; Cash vs close bảo toàn tiền két đã chốt. |
| Ca bắt buộc ON | Không có ca OPEN đúng actor/branch hoặc ca đã đóng: Cash 409, không có receipt tiền mới. Có ca hợp lệ: thành công. Replay payment đã commit vẫn hợp lệ sau đóng ca. |
| Ca bắt buộc OFF | PASS: giữ hành vi Cash không bắt buộc mở ca; receipt ngoài ca không bị gán ngược vào két đóng. |
| QR Mock | PENDING không cộng tiền; survives Payments restart; HMAC sai 401; callback trùng chỉ ghi một lần; FAILED không cộng tiền; success đến trễ sau đóng ca chỉ tăng QR chi nhánh. Không phải ngân hàng thật. |
| Đóng phiên | PAID chặn gọi thêm; còn món chưa phục vụ thì không đóng phiên; Barista/Serving hoàn tất độc lập; close concurrent trả bàn Available, không tự đóng bằng Mobile. |
| Reporting/recovery | Durable HTTP collection outbox/inbox; replay ACK sau Payments/Reporting/Redis restart không cộng đôi. Reporting offline hiện section lỗi, không biến thành số 0; service phục hồi thì đọc lại số liệu. |
| Đối soát | So tổng `payment.collection_outbox` với Reporting collections và Flutter DTO; chỉ receipt thành công, không cộng OrderFact legacy lần thứ hai. |
| Khoảng ngày/actor | UTC+7 inclusive/exclusive; ngày trước không có receipt trả 0 thật; đổi actor làm sạch dữ liệu controller cũ. |
| Mobile | Home, Overview, ca và bottom navigation gọi GET thật; reload/refresh; no-shift/OPEN/CLOSED; responsive 360/393/430px; Bill PAID và lịch sử receipt thật. Cash UI PASS: đưa 50.000đ, trả 15.000đ, checkbox nhận tiền, API ghi đúng một Bill 35.000đ và receipt đúng ca. |
| Migration/rollback | Mô hình/PK/FK/unique/CHECK/ACL khớp; chỉ FK thuộc service. Restore clone/empty rollback/reapply và nonempty rollback refusal; restore fingerprint paid ledger/session/orders chính xác. |

Ca ON: Payment = Reporting = Dashboard **245.000đ**, gồm Cash **175.000đ**, QR **70.000đ**. QR không vào tiền mặt trong két; tiền đầu ca không tính như một khoản thu. Đây là khoản thu xác nhận, **không phải net sales hoặc lợi nhuận**. Số tiền ở hai lượt có thể khác vì các race hợp lệ có thứ tự thắng khác nhau; đối chiếu dựa vào receipt đã commit của chính lượt đó.

Ca OFF: cả ba nguồn cùng **385.000đ**, gồm Cash **315.000đ**, QR **70.000đ**, **9 receipt**, không pagination. Cả hai JSON bằng chứng có 0 FAIL và `operatingContainersUnchanged=true`. Sáu lần gọi probe Dashboard/lượt cộng hai probe Bill/ca thật; lượt OFF thêm một probe thao tác Cash UI. Các lần gọi này không phải số lượng test regression mới.

## Thay đổi

- Backend thêm cờ `CashierShifts:RequireOpenForCash`, mặc định false. Kiểm tra trong `CashierLedger.CaptureAsync`, dùng actor transaction lock hiện có, trước khi lưu receipt, cùng transaction payment. Không có ca hợp lệ trả `cashier.open_shift_required`; cờ bắt buộc bật trong khi CashierShifts tắt thì fail closed `cashier.shift_unavailable`. QR và receipt recovery đã thu trước đây không bị gán ca giả.
- Không thêm endpoint hoặc migration mới. Dùng lại Bills, cashier-shifts, Reporting collections và các API vận hành hiện có. Không sửa Web FE/public DTO, không đổi số tiền hay trạng thái giao dịch cũ.
- Mobile ánh xạ hai mã lỗi trên thành thông báo tiếng Việt. Thêm live Dashboard test, Cash UI test và hai test error mapping; giữ theme/Demo/navigation hiện có.
- Runner có `--dashboard`, `--require-shift`, `AICOS_MOBILE_WORKSPACE`; thêm `cashier-dashboard-flow.mjs`. Không đọc `.env` vận hành.
- Chốt LF trong `.gitattributes` cho SQL/manifest session-billing và cashier-shifts. Cập nhật bốn hash Cashier theo LF chuẩn. **Không thay nội dung DDL, EF migration, guard, history ID hoặc quyền**. Thay đổi này khắc phục checksum khác nhau giữa checkout Windows/Linux.

Các lượt đầu bị chặn do xuống dòng checksum, assertion text UI chưa dùng prefix chi nhánh, phép đối chiếu test lọc provider Mock thay vì method QR, và assertion chỉ chấp nhận 403 cho Bill ngoài branch. Probe Cash UI đầu tiên mở form trong FakeAsync nên continuation HTTP không tiến đúng nhịp; đã gọi thao tác trong `tester.runAsync` và kiểm chứng lại PASS. Không cần đổi luồng thanh toán ứng dụng vì lỗi clock của test.

Không coi lượt lỗi/bị ngắt là PASS. Các stack test bị ngắt được kiểm tra đúng nhãn/network/no-volume trước khi dọn riêng. Runner sau đó chạy nền ẩn có lưu exit code để theo dõi tới cuối. Polling dùng deadline monotonic thật. `--keep-failed-test` chỉ giữ stack disposable có nhãn khi lỗi để chẩn đoán; mặc định vẫn cleanup, và lượt cuối PASS đã xóa toàn bộ container/network của nó.

Kết luận: các luồng tài chính/Dashboard đã thực thi ở trên **PASS**; độ đầy đủ toàn bộ contract là **PARTIAL** do ba nhóm blocker phía dưới. Không coi kết quả này là phê duyệt vận hành.

## API được dùng

Đường dẫn dưới `/api/v1`: Identity auth/login, auth/change-password, auth/me, branches; Table areas/dining-tables; Ordering qr/scan, orders, qr/sessions, close-eligibility/close; Kitchen queue/ticket status; Payments bills theo session, Bill cash/qr, invoice/history, Mock webhook; cashier-shifts open/list/detail/transactions/close; Reporting collections theo branch/from/to. Các API mutation chỉ gọi trên fixtures test cô lập.

Không thêm endpoint tổng hợp Dashboard: Mobile dùng các API có owner rõ ràng. Không gọi endpoint GET in bill cũ có side effect để làm kiểm thử đọc.

## Blocker và điều kiện mở vận hành

1. **BLOCKED:** Identity hiện lưu một canonical role/account. Chưa có E2E tài khoản SERVER+CASHIER thật; Mobile đa role mới được kiểm chứng qua DTO tests. Cần thống nhất Identity/Web contract trước khi đổi.
2. **BLOCKED:** Chưa có định nghĩa/endpoint tổng Bill toàn chi nhánh bao gồm Bill UNPAID chưa persist. Số Bill chờ đã tải không phải tổng; không suy tổng PAID/chờ từ trang 6 session.
3. **BLOCKED:** Gross/net sales, refund/payout allocation và Manager approval chưa được chốt. Collections không thay thế các metric này; không mở group payment/Split Bill hoặc Item Transfer V2.
4. **NOT RUN:** QR ngân hàng thật, máy in thật, điện thoại Android; token expiry thực theo thời gian sống JWT không được đợi trong stack. Không tuyên bố production-ready.
5. Source V2 còn dở phải được xử lý ở task riêng trước khi build toàn working tree/deploy. Duyệt schema/ACL/secrets/feature rollout riêng; không tự chạy migration lên database vận hành.

Lệnh runner trên source Backend build được:

```powershell
$env:AICOS_MOBILE_WORKSPACE = 'D:\ki9\sep490\Sep490\AICOS-Mobile'
node scripts/tests/session-billing-stack.mjs --dashboard
node scripts/tests/session-billing-stack.mjs --dashboard --require-shift
```

Trong lượt này source build được nằm tại worktree tạm `C:\Users\ASUS\AppData\Local\Temp\aicos-dashboard-validation-9295d954`. Các bản JSON bằng chứng được lưu trong Backend docs, không chứa mật khẩu/token. Feature chỉ ON ở container test; database chính, secrets vận hành và các container có trước không bị thay đổi. Chỉ commit những file đã kiểm chứng theo yêu cầu mới của người dùng; không push.
