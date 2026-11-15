# Cash Precheck — 15/11/2026

## Contract và phạm vi

GET `/api/v1/payments/cashier-shifts/cash-precheck?branchId=...` thuộc Payments, qua route Gateway hiện có. API lấy ca OPEN cũ không công bố enforcement nên không đủ để Mobile quyết định. Endpoint mới additive, không đổi request/response API cũ, không cần migration.

Counter roles (Cashier/BranchManager/BrandAdmin), tenant/actor từ JWT, branch theo quyền hiện có. Không nhận cashierId do client gửi. Trả `branchId`, `cashierId`, `allowed`, `requiresOpenShift`, `shiftStatus`, `reasonCode`, `message`. `allowed` chỉ xác nhận điều kiện tài khoản/ca tại thời điểm GET, không phải reservation hoặc bằng chứng đã nhận tiền/đủ điều kiện tài chính của Bill.

| Điều kiện | allowed | shiftStatus | reasonCode |
| --- | --- | --- | --- |
| Enforcement OFF, tài khoản hợp lệ | true | NOT_REQUIRED | cashier.allowed |
| Enforcement ON, đúng ca OPEN | true | OPEN | cashier.allowed |
| Enforcement ON, chưa có ca | false | MISSING | cashier.open_shift_required |
| Enforcement ON, ca đã đóng | false | CLOSED | cashier.open_shift_required |
| Bắt buộc ca nhưng tính năng ca chưa bật | false | UNAVAILABLE | cashier.shift_unavailable |

Khi OFF không truy vấn bảng ca để đoán chính sách. Anonymous 401, sai role/branch 403, tài khoản đã thay đổi 403 `cashier.actor_changed`, Identity không sẵn sàng trả lỗi dependency; không fallback sang allowed.

## Tài khoản hiện tại và revalidation

JWT trước đây chỉ kiểm chữ ký/thời hạn nên role/branch cũ có thể còn hiệu lực. Payments thêm `HttpCashActorVerifier`, dùng HTTP service client có sẵn gọi Identity `/auth/me` với bearer token được chuyển tiếp. Kiểm tra id/tenant/branch/role hiện tại, Active, không bắt buộc đổi mật khẩu và không đang lockout.

Verifier chạy trên GET precheck **và** filter trước POST Bill Cash/Order Cash. Role bị thu hồi hoặc branch thay đổi sẽ bị chặn trước ghi tài chính dù JWT cũ còn hạn. Không đọc trực tiếp bảng Identity. Không thay hệ thống JWT/revocation toàn cục: các API khác ngoài ba đường này chưa được thêm kiểm tra tài khoản hiện tại trong task này.

`CashierLedger.CaptureAsync` vẫn kiểm tra flag và đúng ca OPEN dưới actor lock, cùng transaction thanh toán; precheck không thay thế guard này. Replay payment đã commit giữ nguyên idempotency; không ép một ca mới cho receipt cũ. Nếu quyền hiện tại không còn hợp lệ thì API Cash từ chối cả request retry, không tạo tiền mới.

## Flutter

- `MainCashPrecheckRepository`: GET thật, kiểm tra scope actor/branch, kiểu và tổ hợp trạng thái; không dùng flag local hoặc tin message tùy ý từ server. Response thiếu/khác contract không được coi là cho phép.
- `ensureCashPrecheck` dùng chung cho `SessionBillDetailScreen` và `MainOrderDetailScreen`. Caller hiển thị loading/chặn thao tác trùng trước GET; chỉ mở form sau allowed.
- MISSING/CLOSED: dialog tiếng Việt và nút **Mở ca thu ngân** điều hướng tới `MainCashierShiftScreen` hiện có. Không tự POST mở ca. Quay lại phải GET precheck lần nữa; chỉ khi allowed mới tiếp tục.
- Sau khi điều hướng, đọc lại Bill/Order trước form. Network/timeout/401/403/malformed không mở form. Không tự tạo request Cash mới để retry kết quả chưa rõ; giữ request/key của luồng retry cũ.
- Dine-in giữ một Bill tổng hợp theo sessionId; không trả về thu theo từng Order. Takeaway dùng payment/settlement hiện có theo orderId, chưa có entity/API Bill theo orderId riêng. Không bịa thêm Bill hoặc schema.
- QR không gọi precheck, không bắt buộc ca và không cộng vào két; luồng QR giữ nguyên.

## File

Backend: `Features/CashierShifts/CashPrecheck.cs`, đăng ký tại `Program.cs`/`CashierShiftEndpoints.cs`; Cash filter tại `PaymentEndpoints.cs`, `SessionBillEndpoints.cs`. Test harness trong `TestKit.cs` dùng Identity verifier double được ghi rõ; `CashPrecheckTests.cs` kiểm thử policy/state change và client authorization. Scripts stack thêm mode `--precheck`, `cash-precheck-flow.mjs`, bổ sung orchestration tại hai flow hiện có.

Flutter: hai file repository/navigation mới, hai screen cũ, error mapping trong `main_backend_client.dart`. Test mới `cash_precheck_test.dart`, `cash_precheck_takeaway_live_test.dart`; cập nhật probe Cash UI và fixtures có precheck thật về mặt contract.

## Kiểm thử hiện tại

- .NET solution: **842 PASS, 51 SKIP**, gồm Services **574/51**, BuildingBlocks **15**, Identity **203**, Smoke **50**. Test role/branch client có HTTP handler double không được gọi là PostgreSQL/Identity E2E. Có test đổi enforcement sau precheck, đóng ca sau precheck, policy OFF/ON và role/branch/dependency client.
- Flutter regression: 358 PASS, 7 SKIP. Probe live chỉ chạy khi runner cấp môi trường riêng; test MockClient không được tính như API thật.
- Analyzer cuối **0 issues**, formatter PASS; 11 test precheck tập trung PASS sau sửa cuối.
- E2E thật ON: **56 nhóm PASS, 3 BLOCKED, 0 FAIL**, exit 0, cluster `7708355191903195177`. Đã kiểm chứng JWT cũ bị chặn ở precheck và POST Cash sau đổi role/branch; Dine-in thiếu ca → UI mở ca → quay lại → GET mới → form → PAID; Takeaway qua cùng precheck và đúng một payment theo orderId. Đối soát **315.000đ** = Cash **245.000đ** + QR **70.000đ**, 7 receipt.
- E2E thật OFF: **56 nhóm PASS, 3 BLOCKED, 0 FAIL**, exit 0, cluster `7708356797376909353`. Backend cho Cash không có ca, precheck trả NOT_REQUIRED; UI giữ luồng thu hiện tại. Đối soát **420.000đ** = Cash **350.000đ** + QR **70.000đ**, 10 receipt. Số tiền khác nhau do fixtures/race hợp lệ, so theo ledger của chính lượt đó.
- Cả hai lượt kiểm chứng lại retry/mất ACK, nhiều thu ngân, Cash vs close, stale version 409, service/Redis restart, QR PENDING, callback trùng/đến trễ, Reporting offline/recovery và restore clone. Container có trước giữ nguyên snapshot; container/network test cuối đã dọn. JSON Backend: `CASH_PRECHECK_E2E_EVIDENCE.json`, `CASH_PRECHECK_OFF_EVIDENCE.json`.
- Probe Takeaway đầu bấm nút khi settlement còn tải nên chưa gọi được form; đã chờ callback onPay được enable rồi chạy lại PASS. Không coi lượt lỗi là PASS hoặc đổi business logic để bỏ guard.

Môi trường từ worktree Backend sạch tại `4bfbb8e` cộng diff task, PostgreSQL/Redis/network/JWT/credentials riêng; không chạm `aicos_db`, PostgreSQL Windows hoặc V2 đang dở. Feature chỉ bật trong test. Không commit/push/deploy.

Các SKIP regression không tính là integration thật. E2E dùng host Flutter widget/IOClient thật, không phải điện thoại Android. HTTP timeout/401/403/malformed và điều hướng retry được kiểm tra qua MockClient riêng; không gọi chúng là sự cố mạng thiết bị thật. Bộ test dùng các schema/migration baseline đã có để dựng DB disposable sau guard; **không tạo migration mới hoặc apply lên database vận hành**.

Chạy trên source Backend build được:

```powershell
$env:AICOS_MOBILE_WORKSPACE = 'D:\ki9\sep490\Sep490\AICOS-Mobile'
node scripts/tests/session-billing-stack.mjs --precheck --require-shift
node scripts/tests/session-billing-stack.mjs --precheck
```

## Trước vận hành

Payments cần kết nối Identity hiện có cả khi enforcement OFF; không được bypass nếu dependency lỗi. Triển khai Backend trước Mobile; Backend cũ trả 404 sẽ khiến Mobile từ chối mở form thay vì tự đoán allowed. Duyệt rollout/flag và kiểm thử thiết bị thật riêng. QR ngân hàng, Refund, group payment, Item Transfer V2 ngoài phạm vi; các blocker về đa role/tổng Bill/gross-net vẫn giữ nguyên.
