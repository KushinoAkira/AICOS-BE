# Staff Order phi thanh toán — tiến độ Phase A–F

Ngày: 2026-11-05. Tiếp tục mapping/GAP đã có, không audit lại Phase 1.
**Payment, Cash, QR payment, Refund, Bill Settlement và financial reconciliation
đang tạm dừng theo yêu cầu mới.** Giữ code hiện tại, không xóa hoặc thay hành vi
financial. QR **ordering** (scan/cart/checkout) khác QR **payment**.

## 1. Nhóm đã triển khai trong lượt này

### D — Xác nhận giao toàn Order và đồng bộ Kitchen

- Giữ POST `/api/v1/orders/{id}/deliver`, request/response và Counter RBAC cũ.
- Tách handler `OrderDeliveryEndpoints` khỏi screen/endpoint lớn. Transaction
  chạy trong execution strategy, PostgreSQL advisory lock theo tenant/order cho
  các lần xác nhận deliver. Không thêm column/entity mapped hoặc migration.
- Giao Ready+Unpaid vẫn giữ Ready/Unpaid legacy và DeliveredAtUtc/DeliveredBy.
  Giao lại trả 200 với kết quả cũ, không đổi người/thời điểm, không tạo thêm
  OrderCompleted business effect. Domain guard void/not-ready vẫn áp dụng.
- Thêm `OrderDeliveredIntegrationEvent` độc lập với PaymentSucceeded và
  OrderCompleted; replay deliver gửi lại operational effect để sửa một notification
  thất lạc. Đây là business idempotency theo order, không phải persisted request-key ledger.
- Kitchen consumer mới xác minh tenant/order/branch rồi complete ticket từ
  delivery authoritative, kể cả Ready event bị trễ. Không tạo Paid/ReadyAt giả;
  không ghi đè Cancelled/Completed, duplicate không phát status transition lần hai.
- Không sửa Payment handlers, CloseIfSettled, close/expiry, Bill calculation hoặc
  Payment service. Luồng auto-close theo Payment cũ vẫn còn và được ghi BLOCKED.

**Thay đổi Web cần biết:** deliver lặp trước đây 409 nay 200 với kết quả đã lưu;
Completed legacy giữ nguyên nghĩa. Consumer cũ vẫn có event/DTO cũ. Chưa có repo
Web để chạy regression UI, nên chưa chứng nhận Web PASS. Không deploy.

### A/B — Gọi thêm món vào đúng phiên dự kiến

- Giữ POST Orders, thêm optional `expectedSessionId`; capabilities thêm
  `expectedTableSession=true`. Request Web cũ không có field tiếp tục được nhận.
- `CounterOrderRequests.LockAsync` mở transaction relational ngay cả khi không
  có Idempotency-Key, để khóa bàn POS không bị bỏ qua với caller legacy.
- Có expected session: xác minh cùng tenant/branch/table và Open; stale/closed
  trả 409 `qr.session_changed`, không tự mở phiên mới. Replay đơn đã commit vẫn
  trả order cũ; khác expected session khi dùng lại key bị conflict.
- MainTableDetail truyền session đã load sang CreateOrderScreen; form giữ intent
  đó qua submit/retry. Scope nháp/request tách thêm theo session để không dùng
  nháp của lượt khách trước cho lượt mới. Không sửa danh mục/menu/cart đúng sẵn.
- Mobile capability guard chặn add-more vào expected session trên BE cũ, không
  gửi field mới rồi giả định server đã xử lý. Lỗi session có thông báo tiếng Việt.

**Giới hạn:** kiểm tra này không phải giải pháp race close/create hoàn chỉnh.
Close, Payment auto-close, QR checkout chưa cùng lock protocol; theo yêu cầu mới
không sửa phần close liên quan tài chính. Form được mở sau khi Payment đã tự
đóng phiên vẫn chịu policy cũ: chưa hứa giữ session qua thanh toán trước.
Caller không gửi expectedSessionId chưa có bảo đảm chống stale intent đầy đủ.

### C/E — Metadata thật, menu ba chấm và phần code đang dở

- Giữ các batch GET staff display-names/order creators đã có source từ lượt trước;
  thêm tests xác minh batch tối đa 50, loại ID trùng/ngoài request, 404 không dùng
  tên Demo và không làm mất phiên đăng nhập.
- Test phiếu Kitchen hiển thị tên người gọi + thời điểm order/nhận phiếu;
  header options với nội dung dài ở 360/393/430px không overflow.
- Test menu order main: chỉ Xem chi tiết enabled; sửa/hủy chưa nối vẫn disabled,
  không rơi vào Demo mutation. Chưa mở nút hủy/sửa khi policy Backend chưa đủ.
- Test transfer adapter: đúng session route/body/capability, reject response sai,
  không tự retry hoặc fake local transfer. API transfer, handler và UI đã có từ
  lượt trước, chưa deploy; không nhận là toàn bộ concurrency/audit đã hoàn thiện.
- Sửa 5 lint braces của phần actor/transfer đang dở; không đổi luồng close cũ.

### F — Tự làm mới dữ liệu vận hành

- Thêm `OperationalRefreshScope`: GET refresh mỗi 15 giây ở main Home/Table/
  trang đầu Orders/Kitchen, chỉ khi route đang hiển thị, app foreground và không
  có request chạy. Resume tải lại; dispose hủy timer/observer.
- Không poll Profile/Notifications hoặc khi danh sách Orders đã tải >30 dòng để
  tránh xóa phần phân trang đang đọc; vẫn có refresh thủ công.
- Không polling màn Payment, không gọi POST/PUT/PATCH/DELETE tự động, không tự
  gửi lại create/deliver/cash. Response cũ được controller loại theo generation.
- Polling chỉ đọc lại trạng thái Backend, không khôi phục được event Redis đã
  mất giữa services. Không tuyên bố realtime hoặc durable event recovery.

## 2. GAP vận hành hiện tại

DONE chỉ cho phạm vi có bằng chứng trong dòng đó; không có Phase A–F nào được
chứng nhận hoàn tất E2E toàn bộ. Không biến PARTIAL thành DONE vì tests mock PASS.

| Phase | Chức năng | Trạng thái | Đã có / còn thiếu | Tiếp theo |
| --- | --- | --- | --- | --- |
| A | Login, chi nhánh, area/table/filter/detail | DONE | Code/API/tests từ trước; không viết lại | Regression khi rollout |
| A | Mở/lấy session khi tạo order, nhiều lượt | PARTIAL | POS dùng session thật, có expected-session guard; explicit open staff riêng chưa nối | Test concurrency PostgreSQL cô lập cần duyệt môi trường |
| A | Occupancy giữa màn hình/thiết bị | PARTIAL | Overlay session và GET polling; Table physical projection chưa tự đồng bộ đầy đủ | Operational lifecycle contract, không giả Cleaning |
| A | Close/Payment auto-close/race close/add | BLOCKED | Đụng policy Payment đã yêu cầu tạm dừng | Giữ nguyên source financial; chỉ trình phương án |
| B | Menu/options/food/combo/cart | DONE | API thật + tests trước, test responsive mới; combo vẫn product giá bộ | Không tự thêm cấu trúc combo/topping quantity |
| B | Create/add more/retry | PARTIAL | Key và expected session có tests; app restart mất key; QR cart crash boundary chưa chứng nhận | Durable request identity/recovery design |
| C | Queue, bàn, creator name, options/note | PARTIAL | Có source/adapter/tests; chưa deploy metadata mới | Read-only smoke sau duyệt rollout |
| C | Bắt đầu/hoàn thành ticket | PARTIAL | API thực tế; chưa có nhận độc quyền/line-ready, chưa test PG multi-Barista | Thiết kế item states và concurrency |
| C | Hoàn thành từng món | BLOCKED | KitchenTicketItem không có status/OrderItemId liên kết, event lines chưa có OrderItemId | Trình additive schema/event design, không map theo tên |
| D | Giao cả order Ready, Unpaid, retry | PARTIAL | Sửa delivery độc lập tiền và Kitchen consumer; tests component PASS | Test PostgreSQL/Redis thật trong môi trường được duyệt |
| D | Nhận/mang/giao từng món nhiều Staff | DEMO_ONLY | Demo có claim/release; main chưa có persistence claim/quantity/version | BLOCKED schema; chưa bật UI thật |
| D | Không complete cả order khi còn món | BLOCKED | P0 vẫn all-order delivery; không giả partial serving | Item protocol trước khi mở partial buttons |
| E | Xem order/lines/creator/time/danh sách cũ | PARTIAL | Có API thật; event timeline/update audit đầy đủ chưa có trên Mobile | Không coi list order là immutable change history |
| E | Sửa/hủy cart trước submit | DONE | Client draft/cart, server revalidates khi tạo | Giữ |
| E | Sửa/hủy order/line đã submit | BLOCKED | Existing whole-cancel API chưa đủ Bar approval; edit đổi charge phụ thuộc Bill; main menu disabled | Trình change-request/version/audit; giữ endpoint Web cũ |
| E | Chuyển nguyên phiên | PARTIAL | Source API/UI/tests có; race QR/close/payment và audit durable chưa giải quyết | Không bypass unpaid guard, chưa rollout |
| E | Ghép bàn | MISSING | Không có session merge API tương đương; menu chỉ Demo/UI | Đề xuất source/target/membership/cart/token, chưa schema |
| E | Tách/điều chỉnh bill | BLOCKED | Ngoài phạm vi mới | Giữ nguyên, không triển khai |
| F | Refresh operational foreground/resume | DONE | Timer/visibility/no overlap/dispose tests PASS | Không coi là Redis recovery |
| F | Event delivery sau restart/offline | BLOCKED | Pub/Sub best effort, không outbox/inbox; ticket chưa tồn tại vẫn có thể mất Delivered event | Durable recovery/audit thiết kế riêng trước migration |
| F | Web FE regression, thiết bị và E2E cô lập | BLOCKED | Chưa có repo Web; chưa duyệt môi trường fixtures/PG/Redis mới; không deploy | Xin phạm vi test riêng khi có thiết kế cụ thể |

## 3. Phương án serving theo món để trình tiếp

- Thêm optional OrderItemId vào operational event line; giữ ProductId/fields cũ.
  Không ghép Kitchen lines với Order lines chỉ bằng ProductName, vì một product
  có nhiều cấu hình/note/quantity trong cùng order.
- Kitchen line cần correlation và preparation state persist; Serving cần
  orderItemId, quantity/assignee/state/version, thời điểm claim/release/serve,
  operation identity và audit. Cần tính phần đã giao/đang giữ/khả dụng trên server.
- Claim/giao kiểm tra quantity còn lại và version trong transaction; hai Staff
  không giữ/giao trùng; lease hết hạn không tự coi món đã giao hoặc hoàn tiền.
- Chỉ phát whole-order Delivered khi toàn bộ quantity hợp lệ đã Served/Cancelled
  qua rule; ready từng món không tự làm mọi món Ready. Không gán enum giả trên
  Mobile khi Backend chưa lưu.
- Những phần này cần migration/additive constraints được review. Lượt này không
  thêm mapped property/table, không tạo SQL hoặc chạy schema mới.

Ghép bàn chưa có API: cần quyết định giữ session nguồn hay session đích, xử lý
guest token/cart/order membership và một bill tổng hợp. Vì có liên quan bill,
chỉ giữ đề xuất, không giả ghép bằng sửa TableId từng order hoặc tạo lại order.

## 4. File và API thay đổi

Backend (`AICOS-BE`):

- Mới: `src/BuildingBlocks/AICOS.Contracts/Events/OrderDeliveredIntegrationEvent.cs`.
- Mới: `src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderDeliveryEndpoints.cs`.
- Ordering sửa `OrderEndpoints.cs`, `OrderSupport.cs`, `OrderContracts.cs`,
  `CounterOrderRequests.cs` trong cùng Features/Orders.
- Mới: `src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/KitchenOrderDeliveredHandler.cs`.
- Kitchen sửa `Domain/Entities/KitchenTicket.cs`, `Program.cs`.
- Tests: `OrderTests.cs`, `KitchenTests.cs`, `MobileCounterTests.cs`.
- API hiện có mở rộng: POST Orders optional expectedSessionId; GET staff/capabilities
  thêm expectedTableSession; POST deliver business replay 200. Không thêm REST route
  trùng chức năng, không sửa DTO/endpoint Payment hoặc Bill.

Mobile:

- `lib/repositories/main_operations_repository.dart`, `main_order_actor_repository.dart`.
- `lib/screens/create_order_screen.dart`, `main_table_detail_screen.dart`, `main_staff_screen.dart`.
- `lib/services/main_backend_client.dart`, `lib/widgets/table_detail/main_table_action_handler.dart`.
- Mới `lib/widgets/serving/operational_refresh_scope.dart`.
- Mới `test/main_operational_completion_test.dart`, `test/operational_refresh_scope_test.dart`.
- Source actor/UI/transfer chưa commit từ trước được giữ; không nhận toàn bộ diff
  working tree là thay đổi mới của lượt này.

## 5. Kiểm tra và giới hạn

- Nhóm delivery/Kitchen: 111 Backend tests PASS, 0 skip.
- Nhóm expected session: 105 Backend tests PASS, 0 skip. Hai nhóm có overlap,
  không cộng thành tổng số tests độc lập.
- Regression phi thanh toán cuối: **214 PASS, 0 FAIL, 0 SKIP** trên
  Table/Menu/Order/OrderOptions/OrderTracking/OrderClosing/QR-order/MobileCounter/
  Kitchen/Remake. HTTP WebApplicationFactory, EF InMemory, fake clients/bus,
  JWT test. Không phải PostgreSQL race, Redis transport hoặc Web UI E2E.
- Dart formatter và dotnet whitespace formatter trên 3 file C# mới: PASS.
- Flutter analyze sau sửa null-aware lint: **0 issues**.
- Full Flutter suite API=false: **146 PASS, 0 FAIL**; riêng login/branch ở
  API=true: **5 PASS**, có overlap với full suite. Tests dùng HTTP MockClient/
  widget, không phải thiết bị thật. Full suite giữ cả regression Demo/Payment
  mock đã có, không bổ sung nghiệp vụ Payment trong lượt này.
- Identity staff display-names: **1 PASS, 0 SKIP** trên HTTP/InMemory; test quyền
  staff và tenant/branch, không mở directory Management cho Cashier.
- Không chạy Payment E2E/PG scratch hoặc thêm financial tests mới, không dùng simulation,
  không gọi API vận hành, không khởi động Docker, seed/migration, thay DB/hạ tầng,
  credentials, deploy hoặc commit/push.

Delivery lock chỉ serialize các delivery confirmations; cancel/remake và event
handlers khác chưa dùng chung operation protocol. Ticket consumer duplicate
sequential được test, chưa chứng nhận concurrent multi-replica. Trạng thái DB
vận hành không được truy vấn trong lượt này; không báo fingerprint PASS mới.

## 6. Bước tiếp theo

Trình thiết kế storage nhỏ nhất cho item preparation/serving claim/quantity/
version/audit và durable operational recovery; review Web consumer trước siết
cancel/remake/service-type. Chuẩn bị plan test PG/Redis cô lập, không tự dựng
hạ tầng hoặc fixtures. Payment/Bill tiếp tục giữ nguyên cho giai đoạn riêng.

## 7. Cập nhật 2026-11-05: Kitchen concurrency và phần còn bị chặn

### Hoàn thành trong lượt này

- Backend Kitchen thêm `GET /api/v1/kitchen/staff/capabilities` và trường tùy chọn `expectedStatus`, `expectedRemakeCount` vào `PUT /tickets/{id}/status`. Request Web cũ vẫn được chấp nhận. Request có điều kiện phát hiện trạng thái/lượt remake cũ bằng `409 ticket.stale`; gửi lại cùng trạng thái trả 200 mà không ghi/thông báo thêm.
- Các writer trạng thái Kitchen, remake, OrderDelivered/Cancelled/Completed dùng chung khóa giao dịch PostgreSQL theo tenant/order và đọc lại ticket dưới khóa. Đây là bảo vệ state Kitchen, chưa phải claim độc quyền và chưa được chứng minh bằng E2E PostgreSQL nhiều replica.
- Mobile gửi snapshot trạng thái/lượt remake, chặn thao tác lặp cùng ticket khi đang xác nhận, tải lại khi nhận 409. Server cũ không có capability thì dừng trước PUT. Bỏ thông báo sai rằng tên người gọi món chưa có khi đã hiển thị tên.
- Test Kitchen chuyên biệt: **21 PASS, 0 FAIL, 0 SKIP**, EF InMemory/HTTP; kiểm tra replay, stale, remake cycle, RBAC và terminal Delivered.

### Giữ BLOCKED, không giả lập bằng UI

| Chức năng | Lý do | Cần duyệt |
| --- | --- | --- |
| Serving theo món | Event line thiếu OrderItemId; Kitchen line thiếu state và quantity đã giao | Additive schema, contract, migration và PostgreSQL E2E |
| Nhiều Staff claim cùng món | Không có assignee, claim quantity, version/lease, audit bền vững | Protocol claim/serve và transaction test hai Staff |
| Sửa/hủy Order sau submit | API cancel cũ không yêu cầu Bar duyệt khi đang pha/đã sẵn sàng; edit có ảnh hưởng charge | State/RBAC matrix, change request, audit, Web compatibility |
| Ghép hai bàn có khách | Transfer hiện trả `table.target_occupied`; chưa có session membership/redirect policy | Chọn session sống sót, guest token/cart và Bill policy |
| Phục hồi event Redis | Pub/Sub không lưu event khi consumer offline; polling Mobile chỉ sửa hiển thị | Outbox/inbox và reconciliation có kiểm soát |

Thiết kế, source evidence, API và test/rollback đề xuất: `docs/SERVING_MULTI_STAFF_AND_RECOVERY_DESIGN.md`. Không chạy SQL, migration, Docker, seed, deploy hoặc thay đổi Payment/Bill.
- Regression sau thay đổi: Backend vận hành **216 PASS, 0 FAIL, 0 SKIP** (HTTP/EF InMemory); Flutter full suite **149 PASS**, analyzer **0 issues**, Dart format **0 thay đổi**. Không chạy PostgreSQL/Redis E2E.

- Sau khi thêm case stale 409, test Mobile liên quan **10 PASS**; full suite trước case này **149 PASS**. Case mới chỉ kiểm tra phản hồi 409, không thay đổi code sản phẩm.

## 8. Integration PostgreSQL/Redis thật và thiết kế MVP BLOCKED

Theo phê duyệt của người dùng trong lượt này, runner tạo schema Ordering/Kitchen bằng EnsureCreated chỉ trên PostgreSQL tạm. Không dùng migration hoặc truy cập database vận hành. Script: `AICOS-BE/scripts/Test-StaffOperationsInfrastructure.ps1 -Run`; source tests: `StaffOperationsInfrastructureTests.cs`, `OperationalTestTarget.cs` và tùy chọn real Redis trong `TestKit.cs`.

- **7 PASS, 0 FAIL, 0 SKIP** trên PostgreSQL 16/Redis 7 thật. Hai Barista, hai Staff deliver và duplicate create có kiểm tra ít nhất hai writer đang chờ trong pg_locks; còn có stale status/remake 409, expectedSessionId cũ, unpaid completion và retry mất response sau commit.
- **Redis live reconnect PASS; durable recovery FAIL**: restart Redis không mất dữ liệu PostgreSQL, event mới chạy lại; OrderCreated khi Kitchen offline vẫn không tạo ticket sau consumer restart. Test characterization PASS không đồng nghĩa recovery đạt yêu cầu.
- Timeout được fault-inject sau commit HTTP thật; network timeout/process kill, Web FE, production ACL và test nhiều API process riêng **NOT RUN**.
- Cluster cuối `7704701984166989859`, network tạm `aicos-staff-operations-a8190207fe`. Hai container và network tạm đã cleanup; snapshot ID/image/start/status/restart của toàn bộ container cũ không đổi. Không xóa volume.
- **216 Backend regression PASS, 0 SKIP** sau thay đổi test harness; đây là HTTP/InMemory/fake bus, không cộng với 7 infrastructure test thành số E2E.
- **18 Flutter tests liên quan PASS**, analyzer 0 issues; formatter 195 file không thay đổi. Không sửa UI/Dart trong lượt này.
- Thiết kế MVP cho 5 chức năng BLOCKED đã bổ sung dữ liệu/ownership, API/DTO, state/rule, RBAC hiện có, migration cần thiết, Web impact, test cases và rollback. Chưa triển khai chức năng BLOCKED.

Chi tiết PASS/FAIL/NOT RUN/BLOCKED: `STAFF_OPERATIONS_INTEGRATION_TEST_RESULTS.md`.
Thiết kế chờ duyệt: `SERVING_MULTI_STAFF_AND_RECOVERY_DESIGN.md`.
Phê duyệt schema test tạm không phải phê duyệt migration hoặc deploy môi trường chính. Payment/Bill giữ nguyên; không commit/push.
## 9. Thiết kế P0 admission recovery — chờ duyệt

- Đã truy vết POS/QR/pickup: Order commit trước publish; OrderEventPublisher log lỗi, Redis Pub/Sub không có backlog/ACK, subscriber chỉ log handler failure. Retry create idempotent không đảm bảo publish lại. Root cause dựa vào source và case real PostgreSQL/Redis đã ghi ở mục 8, không suy từ reconnect.
- Đề xuất MVP: transactional outbox trong Ordering + HTTP admission có ACK sau Kitchen commit + receipt/idempotent consumer/shared tenant-order lock + reconciliation quét lại tất cả Order còn lưu. Không chọn Published Pub/Sub làm bằng chứng Kitchen đã nhận.
- Schema review-only: 3 bảng mới (`ordering.kitchen_admission_outbox`, `ordering.kitchen_admission_reconcile_state`, `kitchen.order_admission_receipts`) và nullable `kitchen_tickets.recovery_hold_reason`. Tái sử dụng unique index ticket hiện có; không sửa Payment/Bill hoặc Order enum.
- Đã trình DTO/API internal, machine auth riêng, dispatcher lease/fencing/backoff, bounded persistent sweep, atomic receiver/receipt, replay/terminal guards, hold dữ liệu legacy và rollback feature flags. Chưa tạo EF migration hoặc code nghiệp vụ.
- Tài liệu thiết kế: `KITCHEN_ORDER_ADMISSION_RECOVERY_P0_DESIGN.md`; SQL review-only trong `AICOS-BE/docs/proposals/kitchen-recovery/`.
- Trạng thái recovery hiện tại vẫn FAIL; implementation/SQL/acceptance mới NOT RUN. Các con số test ở mục 8 là bằng chứng lượt trước, không phải đã triển khai recovery.
- Validation design-only: static SQL/diff review PASS; SQL execution/migration/rollback/recovery acceptance NOT RUN. Flutter formatter 195 files unchanged, analyze 0 issues, 10 related repository mock tests PASS. These do not certify the proposed recovery implementation.

## 10. P0 Kitchen admission recovery — implementation và test cô lập hoàn tất

- Implement transactional capture ở OrderingDbContext (POS/QR/Web pickup), internal HTTP receiver với machine auth riêng, shared Redis/HTTP admission writer và local receipt, lease/fencing dispatcher, checkpoint reconciliation và recovery hold guards.
- Tạo hai EF migrations additive mới: 20261106164220_KitchenAdmissionOutbox và 20261106164227_KitchenAdmissionReceipts. Không sửa migration đã chốt, không tạo lại unique ticket tenant/order, không cross-service FK. SQL generated/checksum ở docs/migrations/kitchen-recovery/; proposal cũ giữ nguyên.
- Nghiệm thu PostgreSQL/Redis thật: **18 PASS, 0 FAIL, 0 SKIP**, gồm OS process kill trước/sau commit, HTTP ACK mất, nhiều dispatcher, Redis+HTTP cùng Order, Redis tạm stop, worker tự động, cursor legacy >100 records, fencing, hold/RBAC/auth, rollback Order+outbox và API create retry.
- Regression: **458 PASS/0 SKIP** HTTP/InMemory suite hiện có (không phải PG E2E); **15 PASS/0 SKIP** BuildingBlocks. Build không lỗi, còn 2 warnings cũ. Compose config --quiet PASS, snapshots models khớp migrations.
- Cả hai database test đều là cluster mới có guard và full EF migration chain; các container có trước không đổi ID/image/start/status/restart. Tài nguyên tạm đã cleanup. Không thay database vận hành, không deploy/recreate container, không sửa Payment/Bill hoặc Mobile, không commit/push.
- Recovery feature flags mặc định false; MigrateOnStartup=false ở app/launch/override Compose cho Ordering và Kitchen. Runtime đang chạy chưa tự nhận code mới. Chỉ bật sau phê duyệt migration/ACL/auth/rollout riêng.
- P0 admission: IMPLEMENTED + PASS trong test. Main deployment/Web UI E2E/production ACL: NOT RUN. Partial Serving/Staff Claim và durable event recovery tổng quát vẫn ngoài phạm vi.

Xem `KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md`, `KITCHEN_ADMISSION_RECOVERY_RUN.md` và TRX được ghi trong run evidence để đối chiếu.