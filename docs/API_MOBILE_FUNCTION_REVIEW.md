# Rà soát API main và Staff Mobile — 2026-11-03

## Phạm vi và bằng chứng

Đối chiếu catalog API hiện có, đăng ký route/group authorization và source DTO/handler của Backend với screens, controllers, repositories và Demo flow của Mobile. Đây là audit contract và sửa các sai lệch đã xác định; **chưa chứng nhận toàn bộ nghiệp vụ E2E trên PostgreSQL/Redis**.

Backend hiện tại là 9 .NET API services, YARP Gateway và FastAPI AI; không dùng lại kiến trúc/database của BE backup. Service sở hữu DbContext của mình; các luồng mới không đọc trực tiếp database service khác.

Catalog toàn hệ thống: `BACKEND_API_CATALOG.md` (snapshot main trước các thay đổi tích hợp). Thay đổi source sau snapshot gồm GET `/api/v1/orders/staff/capabilities` đã thêm ở lượt trước, filter tableId/type, request tableQrToken và các field additive. Catalog snapshot không phải bằng chứng mọi endpoint đã test hoặc container đã có code mới nhất.

Lượt này không deploy/restart container, không gọi API nghiệp vụ trên database thật, không tạo account/JWT, không chạy migration/seed. Không commit/push.

## 1. Tables — giữ API, sửa adapter Mobile

| API | Quyền | Kết luận / ảnh hưởng Mobile |
| --- | --- | --- |
| GET /api/v1/tables/floor-plan?branchId=... | Staff, tenant/branch | Giữ nguyên; dùng khu vực, mã bàn, capacity, status và qrToken thật |
| GET /api/v1/tables/dining-tables/{id} | Staff, tenant/branch | Tái sử dụng; Mobile mới đọc lại khi refresh Chi tiết bàn và trước mở form gọi món |
| GET /api/v1/orders/qr/sessions?branchId=... | Staff | Giữ nguyên; chỉ phiên mở hiện tại, không cộng đơn lịch sử thành phiên mới |
| GET /api/v1/orders/qr/sessions/{id}/bill | Staff, tenant/branch | Mở rộng field mỗi round, xem phần Orders |
| PATCH /api/v1/tables/dining-tables/{id}/status | Staff | Có API Available/Occupied/OutOfService; không tương đương xác nhận dọn bàn |
| POST /api/v1/orders/qr/sessions/{id}/close | Counter | Guard còn nợ đã có; Mobile chưa mở vì chưa có settlement/concurrency xuyên các service được chứng nhận |

Lỗi đã sửa: Chi tiết bàn dùng table snapshot từ lúc vào sơ đồ, có thể còn cho gọi món sau khi bàn ngừng phục vụ. Refresh giờ đọc GET table, kiểm tra id/branch, chặn thao tác nếu load lỗi/đang tải/ngừng phục vụ. Ordering vẫn xác minh table QR và CanOrder ở server khi tạo đơn; kiểm tra client không thay thế server validation.

CanOrder lấy status gốc; TableStatus.cleaning chỉ dùng trình bày màu cho OutOfService trong adapter, không gửi Cleaning lên main. Vẫn thiếu trạng thái Cleaning thực sự và nghiệp vụ move/merge cả phiên.

## 2. Menu — giữ nguyên contract

| API | Quyền | Cách dùng |
| --- | --- | --- |
| GET /api/v1/menu/branches/{branchId}/menu | Staff + branch | Mobile dùng menu published gồm sold-out để hiển thị và khóa chọn |
| GET /api/v1/menu/public/branches/{branchId}/menu | Anonymous | Ordering server dùng nguồn sellable để chốt giá; không thay staff view bằng public menu |
| GET categories, products và GET products/{id} | Staff | API quản lý catalog hiện có; không cần gọi từng product khi branch menu đủ DTO |
| PATCH /api/v1/menu/branches/{branchId}/products/{productId}/availability | AvailabilityEditors | Barista/Manager/Admin, không cấp thêm cho Cashier; Mobile chưa có thao tác quản trị tương ứng |
| PUT products, publications, branch pricing | Admin/Management theo route | Không đưa vào luồng bán hàng của Cashier |

Giá branch, category ID/name, option ID, default, required, single/multiple, maxSelections và requiresOptionId đã có. Không cần endpoint menu mới. Mobile gửi IDs/quantity/note; không gửi giá tin cậy. Sold-out/giá đổi sau lúc mở giỏ phải được server kiểm lại và trả lỗi; không fallback Demo.

## 3. Orders — mở rộng DTO, giữ request cũ

| API | Phân loại |
| --- | --- |
| GET /api/v1/orders, /{id} | Giữ endpoint; OrderSummary bổ sung optional deliveredAtUtc |
| GET /api/v1/orders/qr/sessions/{id}/bill và /qr/session/bill | Giữ endpoint; QrBillOrder thêm optional cashierId/customerId/readyAtUtc/deliveredAtUtc |
| POST /api/v1/orders | Giữ endpoint và request cũ; tích hợp trước đã thêm optional tableQrToken + Idempotency-Key |
| GET /api/v1/orders/staff/capabilities | Endpoint đã thêm lượt trước; chưa tạo endpoint mới trong lượt này |
| POST /api/v1/orders/{id}/deliver | Tái sử dụng Counter action; xác nhận nhận đủ cả order, không phải payment |
| POST /api/v1/orders/{id}/cancel, /exception, /no-show, /unfulfillable | Có nghiệp vụ chính thức; không ánh xạ trực tiếp thành yêu cầu sửa/hủy từng món của Demo |
| PUT /api/v1/orders/{id}/service-type | Có API nhưng không dùng thay chuyển/ghép phiên bàn; chưa giải quyết toàn bộ session/cart/settlement |
| GET /api/v1/orders/{id}/receipt, /by-code/{code}/receipt | Có side effect print counters; không tự gọi để xem bill hoặc smoke read-only |

Nguyên nhân lỗi đã sửa: Deliver ghi DeliveredAtUtc nhưng đơn DineIn chưa trả tiền vẫn Ready theo domain. List và bill cũ bỏ field giao món, khiến UI hiểu là chưa giao. Không đổi enum hoặc ép đơn thành Completed. DTO nay giữ metadata, Mobile hiển thị Đã giao món, khóa giao lại và giữ Chưa thanh toán. NoShow/Unfulfillable đều map món đã hủy thay vì còn chờ Bar. Thông tin nhân viên hiển thị ID thật, chưa bịa tên.

Ví dụ phần bổ sung trong một round bill (các field cũ giữ nguyên):

```json
{
  "cashierId": "uuid-or-null",
  "customerId": null,
  "readyAtUtc": "2026-11-03T03:15:00Z",
  "deliveredAtUtc": "2026-11-03T03:20:00Z"
}
```

Các field optional được nối cuối record; request/URL/status thành công hiện có giữ nguyên. Client cũ bỏ qua field mới; Mobile chấp nhận field vắng/null. Consumer parse JSON nghiêm ngặt cần kiểm tra khi rollout. Không đổi nghĩa field hoặc response thành envelope mới.

## 4. Barista / Serving — tái sử dụng API

| API | Quyền và giới hạn |
| --- | --- |
| GET /api/v1/kitchen/queue, /batches, /tickets/{id} | Staff, tenant/branch |
| PUT /api/v1/kitchen/tickets/{id}/status | Route Staff; handler chỉ Barista/Manager/Admin cho Brewing/ReadyForPickup/Completed; hủy ticket chỉ Manager/Admin |
| POST /api/v1/kitchen/tickets/{id}/remake | Bar roles; không phải yêu cầu sửa món |
| GET /api/v1/kitchen/tickets/{id}/stickers | Bar roles; ghi print state, không tự gọi |
| POST /api/v1/orders/{id}/deliver | Counter; Barista không tự giao bằng role Cashier |

Ordering đã ghép options và note vào OrderItemDto.Note; Kitchen trả note thật. Lỗi Mobile là không render note trên hàng đợi, khiến Barista thiếu hướng dẫn pha. Đã tách MainKitchenTicketCard, hiện tùy chỉnh/ghi chú và thời điểm nhận phiếu từ API; Cashier chỉ xem, Barista có nút pha chế như trước. Không đổi API Kitchen, không thêm mock options.

Queue hiện là danh sách server chưa phân trang. Batch grouping, realtime reconnect, claim/release theo món và khôi phục Redis event vẫn cần xử lý riêng. Save order thành công không chứng minh Kitchen đã nhận event: chưa có durable outbox/inbox.

## 5. Cashier / Payment — sửa rule, mở rộng lịch sử, chưa mở thu tiền Mobile

| API | Kết luận |
| --- | --- |
| GET /api/v1/payments/orders/{orderId}/settlement | **Endpoint mới đã code**, Staff; snapshot read-only từ Payments + Ordering HTTP contract, advisoryOnly=true |
| GET /api/v1/payments/transactions?orderId=...&branchId=...&limit=50 | Giữ endpoint; thêm optional createdByUserId, cashierUserId trong PaymentResponse |
| GET /api/v1/orders/{id}/payment | Có projection Ordering; không coi là settlement xác thực trực tiếp Payments; AmountDue hiện trả FinalAmount nên không tự đổi nghĩa field |
| POST /api/v1/payments/transactions/cash | Có thật; Counter; sửa payable guard để từ chối NoShow/Unfulfillable cùng Cancelled |
| POST /api/v1/payments/transactions | Có provider creation; không tương đương nhận tiền |
| POST /api/v1/payments/transactions/{id}/query, /cancel | Có mutation; không tự polling như GET read-only |
| GET /api/v1/payments/transactions/{id} | Có expire side effect; lịch sử Mobile dùng list, không dùng detail để smoke |
| POST /api/v1/payments/transactions/{id}/simulate | Chỉ simulation theo cấu hình; không bật để làm QR thật |
| POST /api/v1/payments/webhooks/{provider} | Có kiểm tra provider; provider adapter hiện vẫn mô phỏng, cần tích hợp thật |
| Refund / reconciliation / merchant-settings / providers | Có API theo management/platform quyền; không dùng thay điều chỉnh bill hoặc manual QR evidence approval |

Giữ mã lỗi payment.order_cancelled và HTTP 422; mở rộng guard cho các trạng thái terminal bị bỏ sót. Cả cash và online đi qua OrderChecks nên đều được sửa; test xác minh không tạo payment hoặc phát PaymentSucceededEvent khi từ chối.

PaymentResponse lấy CreatedBy và CashierUserId có sẵn trong entity, không thêm cột. Mobile hiển thị thời điểm tạo/nhận tiền, ID thu ngân, số tiền hoàn và cảnh báo nhận tiền muộn nếu API có. Không suy ra đã thu tiền từ ảnh QR hoặc Bar xong. List 50 giao dịch có ghi rõ giới hạn; chưa có pagination đầy đủ, không dùng tổng list làm settlement.

Các nút Cash/QR trong Mobile vẫn disabled: cần race test cash/online/webhook trên PostgreSQL, cơ chế idempotency chung, audit bằng chứng QR và provider thật. Bật AllowSimulation=false không biến SimulatedGateway thành provider thật.

### Endpoint mới: đối chiếu settlement theo order

`GET /api/v1/payments/orders/{orderId}/settlement`, không có body, cần Bearer JWT Staff. Ordering xác minh tenant/branch bằng token forward; Payments giới hạn tenant và kiểm tra branch. Không đọc DbContext service khác, không gọi provider, expire, SaveChanges hoặc publish event.

Ví dụ response (sau Cash đã ghi nhận nhưng event chưa đồng bộ Ordering):

```json
{
  "orderId": "00000000-0000-0000-0000-000000000001",
  "branchId": "00000000-0000-0000-0000-000000000002",
  "orderStatus": "Ready",
  "orderingPaymentStatus": "Unpaid",
  "state": "AwaitingOrderSync",
  "isSettled": false,
  "orderAmount": 50000,
  "netReceived": 50000,
  "outstandingAmount": 0,
  "hasPendingPayments": false,
  "hasUnresolvedRefunds": false,
  "advisoryOnly": true
}
```

State: AwaitingPayment, Pending, AwaitingOrderSync, ReconciliationRequired, Settled.
Tiền nhận là tổng giao dịch đã thành công trừ hoàn; nhiều giao dịch paid, sai branch,
thừa tiền, refund trên order đang hoạt động hoặc refund chưa giải quyết đều cần đối soát.
Đơn void không có khoản thu và không có pending/refund tồn đọng không còn nghĩa vụ thu;
đơn void đã nhận tiền chưa hoàn không settled. Pending quá hạn vẫn là Pending trong GET,
để worker/provider query xử lý riêng, không phát sinh write từ tra cứu.

401 khi thiếu/sai JWT, 403 khi role không phù hợp hoặc khác branch; order không truy cập
được theo contract IOrderDirectory trả 422 payment.order_not_found; dependency lỗi trả 503.
Không dùng isSettled làm authorization đóng phiên hoặc đề nghị thu thêm: đây là snapshot
đối chiếu không nguyên tử giữa hai service; webhook/checkout/refund có thể đến sau response.

Mobile gọi endpoint sau khi đã hiển thị order/history. Backend cũ trả 404 thì hiển thị chưa
có kết quả; lỗi khác hiện thông báo riêng, không làm mất chi tiết đơn. 401 vẫn hết phiên,
403 không đăng xuất. Chưa tự động áp dụng kết quả vào Table close hay bật nút Cash/QR.

## API thiếu nghiệp vụ — đề xuất, chưa triển khai

Không tạo endpoint trùng khi API hiện có đáp ứng. Các endpoint sau là **contract đề xuất**, không phải URL đã hoạt động:

| Ưu tiên | Contract đề xuất | Service sở hữu | Điều kiện / tác động |
| --- | --- | --- | --- |
| P0 | Settlement contract có khóa và điều phối đóng session | Payments + Ordering | Endpoint snapshot đã code nhưng chưa phải permit đóng session; cần PostgreSQL race tests và protocol đồng thời với payment/checkout |
| P0 | POST /api/v1/payments/transactions/{id}/manual-confirmation | Payments | Cashier/Manager, evidence/reference, idempotency, row locking, audit bền vững; cần review schema trước |
| P0 | Chốt cash/online/webhook concurrency trên API đang có | Payments | Tái sử dụng endpoint, không tạo cash API thứ hai; cần PostgreSQL test riêng và phương án event recovery |
| P1 | POST /api/v1/orders/qr/sessions/{id}/move và /merge | Ordering | Orchestrate Table HTTP contract; không chỉ sửa tableId của một order; cần ledger/audit, concurrency/session policy |
| P1 | POST /api/v1/orders/{id}/change-requests, /{requestId}/approve hoặc /reject | Ordering; Kitchen xác nhận qua contract/event | Ghi before/after, lý do, người duyệt, tác động giá/Bar; không dùng Cancel API trực tiếp cho món đang pha |
| P1 | Serving claim/release theo line | Ordering; Kitchen giữ preparation state | Cần persistence quyền sở hữu/lease và chống phục vụ trùng; chưa có schema phù hợp |
| P1 | Bill split/adjust, print acknowledgement | Payments (money/print audit); Ordering cung cấp lines/session | Cần business rule cho nhiều order, refunds, quyền và audit; print request không phải in thành công |
| P2 | Cleaning transition có settlement precondition | Table + Ordering/Payments contract | Cần trạng thái/lifecycle thực sự, không đổi enum/DB ngầm |
| P2 | Snapshot tên người tạo order / ID-to-display-name staff-safe | Identity contract + Ordering response | Không mở directory Management cho Cashier; tránh N+1, đánh giá tên hiện tại so với snapshot lịch sử |

Không chạy SQL hoặc tự thêm Entity mapped property cho các mục cần schema. Cần trình thiết kế/migration trước khi triển khai những flow này. Chưa thể tuyên bố full business sync hoàn tất.

## Các nhóm API khác đã đối chiếu

- Identity: login/me/refresh/logout và branches dùng thật; user management, tenant approval, subscription là management/platform, không phải màn Staff bán hàng. Chưa tạo account để E2E.
- Customer: voucher, loyalty, preferences có nghiệp vụ; không biến ghi chú khách mang về thành tài khoản Customer. Staff notifications đã có list/unread-count/read/read-all; phần mark-read và paging Mobile còn chưa nối.
- Workforce: employees/me, work-shifts, lịch/attendance/replacements/payroll/cash-closing có API; Demo ca/quyền không thay thế dữ liệu hoặc JWT main. Chưa nối màn HR trong ưu tiên hiện tại.
- Reporting: báo cáo từ event projection; không dùng tổng một trang Orders để giả doanh thu toàn ca. Trễ event cần thể hiện rõ.
- AI: FastAPI health/metadata có; attendance/chat/forecast/summary là placeholder 501 với tên role cũ, không coi là nghiệp vụ sẵn sàng.
- Gateway: YARP giữ prefix và token, route theo service; không phải Ocelot. Không sửa routing/auth hoặc tắt bảo mật.

## Kiểm thử và rollout

Kết quả cuối: formatter PASS; flutter analyze 0 issues; full Flutter 95 PASS/0 FAIL/0 SKIP; full AICOS.Services.IntegrationTests 446 PASS/0 FAIL/0 SKIP. Tám settlement cases gồm refund chờ duyệt, ba trạng thái void, pending quá hạn, thu trùng, chờ đồng bộ và authorization. Hai warning test cũ CS0628/CS8620 còn tồn tại. Chi tiết ghi trong IMPLEMENTATION_LOG của Mobile và STAFF_MOBILE_SYNC của Backend. Test Backend là WebApplicationFactory + EF InMemory + fake service clients/event bus; JWT tự ký test. Mobile là HTTP MockClient và widget test. Không phải E2E, PostgreSQL concurrency hoặc nhà cung cấp QR thật.

Backend changes chỉ trong Ordering/Payments và tests/docs. Trước deploy: review additive fields và rule terminal, build image Ordering/Payment, triển khai theo quy trình được duyệt. Lượt này không restart/recreate runtime. Mobile tương thích cả field mới và field chưa có; dữ liệu metadata thiếu được bỏ qua, không giả lập.

Rollback code: quay về bản image/app đã review nếu cần, giữ nguyên dữ liệu. Lượt này không có schema/data migration cần rollback. Không reset/restore cả working tree vì repo có thay đổi trước lượt này.
