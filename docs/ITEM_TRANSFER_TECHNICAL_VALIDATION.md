# Item Transfer MVP — xác minh kỹ thuật trước khi triển khai

Ngày: 2026-11-15. Người dùng đã đồng ý nguyên tắc chuyển CupId chưa pha/chưa Claim giữa hai phiên OPEN cùng chi nhánh. Lượt này hoàn thiện thiết kế và kiểm chứng cô lập, **chưa triển khai API chuyển món vận hành**.

## 1. Kết luận

Có thể giữ nguyên Order gốc, nhưng **không thể dùng nguyên contract Quote/PAID/Close Session hiện tại** cho chuyển một phần số lượng. Phải tách nghĩa vụ thanh toán và nơi giao theo từng CupId. Chỉ thêm bảng route hoặc đổi TableId sẽ không đủ an toàn.

Một CupId có đúng một bản ghi phân bổ hiện tại tại Ordering, và tối đa một manifest Bill đã freeze tại Payments. Cùng OrderId/OrderItemId có thể xuất hiện trong hai Bill như thông tin nguồn, nhưng tập CupId của hai Bill phải rời nhau. Không thay đổi Quantity, FinalAmount hoặc SessionId gốc của Order để giả lập chuyển một ly.

Không bật feature. Không thay đổi database vận hành, credentials, JWT, container vận hành, migration history hoặc Mobile UI.

## 2. Contract đã đối chiếu với source

| Thành phần/source | Đã xác minh | Tương thích và phần cần bổ sung |
| --- | --- | --- |
| Contracts/Serving/CupIdentity.cs; CupAdmission.cs | CupId được tạo từ tenant/order/item/unitIndex; không phụ thuộc phiên đích | Tái sử dụng. Không tạo CupId mới khi chuyển hoặc retry |
| Kitchen/Features/CupServing/CupAdmissionWriter.cs | Manifest chứa nguồn TableId/SessionId; replay kiểm tra nguồn, số ly/tem và trạng thái | Giữ nguyên nguồn admission/ticket/tem. Tách nơi giao hiện tại ra bảng route; đổi nguồn sẽ bị 409 hoặc Hold |
| Ordering/Features/SessionBilling/SessionBillQuotes.cs | Chọn toàn bộ Order theo TableSessionId, tổng theo FinalAmount; BillLine có OrderItemId | Chưa tương thích. Quote V2 phải chọn đơn vị theo currentSessionId và tính theo giá snapshot từng ly |
| Ordering/.../SessionBillingEndpoints.FreezeAsync | Khóa table key, kiểm tra fingerprint rồi lưu SessionCheckout | Tái sử dụng barrier, bổ sung chặn transfer đang xử lý và freeze manifest allocation V2 |
| Ordering/.../SessionBillingEndpoints.PaidAsync | Chỉ nhận Order cùng phiên gốc và Amount bằng FinalAmount; sau đó MarkAsPaid cả Order | Chưa tương thích. Unit settlement V2 cần receipt/bằng chứng theo từng ly; không MarkAsPaid cả Order khi mới trả một phần |
| Payments/.../BillLedgerService.CollectAsync | Freeze tại Ordering trước khi thu tiền; local Bill lock/key và attempts; một Bill/Session | Tái sử dụng idempotency/thu Cash/QR, cần hiểu QuoteVersion=2 và lưu manifest unit cùng Bill |
| Payments/.../BillLedger.cs | session_bill_orders PK là BillId+OrderId, không unique riêng OrderId | Có thể giữ association theo nguồn, Amount là phần trong phiên đối với V2. Bảng này không chứng minh chống thu trùng CupId |
| Payments/.../BillOutboxDispatcher.cs | PAID outbox gửi BillPaidNotice + toàn bộ OrderIds | Chưa tương thích với partial Order. Cần nhánh payload/version V2; không đưa partial receipt vào consumer V1 |
| Kitchen/Features/SessionBilling/KitchenBillPaidEndpoints.cs | MarkAsPaid toàn ticket theo OrderId, có early payment theo OrderId | Không được dùng để xác nhận trả tiền từng ly. Ticket.IsPaid chỉ được đổi khi đủ coverage toàn ticket nếu còn consumer cần cờ này |
| Kitchen/Features/CupServing/CupServingUnitOfWork.cs | Có Order lock, expectedVersion/cycle, claim fence và audit/outbox; chưa biết reservation chuyển món | Phải kiểm tra transfer reservation trước Start/Ready/Claim/Pickup/Serve/Release/Renew; chung lock mới chỉ tuần tự hóa, chưa tự ngăn pha ly đang được chuyển |
| Contracts/Serving/CupServingContracts.cs; Ordering/Features/Fulfillment | Snapshot hiện tổng hợp theo Order, không có CupId/currentSession/routeVersion | Cần snapshot V2 theo unit; có thể tái sử dụng receipt/outbox/projection JSON hiện tại với phiên bản/hash rõ ràng |
| Ordering/.../SessionBillCloser.UnservedAsync | Lấy Order theo phiên gốc và yêu cầu toàn bộ Order phục vụ xong | Chưa tương thích. Đếm đơn vị thuộc phiên hiện tại, gồm món chuyển đến và loại món chuyển đi |
| Ordering/.../SessionBillingWriteGuard.cs | Chặn sửa tài chính khi checkout Frozen/Paid/Closed; khóa table gốc | Phải bảo vệ thêm mọi phiên đang sở hữu unit của Order. Chặn sửa/hủy/refund toàn Order đã phân bổ, tránh ảnh hưởng Bill khác |
| Payments/Infrastructure/OrderDirectory.OrderChecks | SessionBilling ON chặn thanh toán từng Order bàn | Giữ guard này; không được tắt SessionBilling để trả tiền vòng qua ledger unit |
| Payments/.../CashierShifts/CashierLedger.cs | Khoản thu gắn BillId, không cần tách thành sự kiện trả tiền từng Order | Tái sử dụng; tổng thu theo Bill/receipt, không phát thêm khoản thu giả cho Order nguồn |

Đường dẫn trên tương đối với `src/Services/<service>/<project>/` hoặc `src/BuildingBlocks/AICOS.Contracts/`. Kiểm thử compatibility mới dùng handler HTTP thực tại host, EF InMemory và JWT test; không gọi đó là PostgreSQL E2E.

## 3. Quyền sở hữu và cách tính tiền

Ví dụ đã đưa vào test: Order gốc A có ba ly cà phê, mỗi ly 50.000đ (UnitPrice đã bao gồm options); Order gốc B có một trà 40.000đ. Chuyển một ly từ phiên A sang B:

| | Trước | Sau |
| --- | ---: | ---: |
| Bill A | 150.000đ | 100.000đ |
| Bill B | 40.000đ | 90.000đ |
| Tổng | 190.000đ | 190.000đ |

Order A vẫn Quantity=3, FinalAmount=150.000đ và SessionId gốc A. Bill A có hai CupId nguồn A; Bill B có CupId thứ ba nguồn A cùng CupId nguồn B. Không clone OrderItem hoặc tạo Order/ticket mới.

Công thức MVP: `BillSubtotal(session) = SUM(unit_amount WHERE current_session_id=session)`. `BillRound.Amount` trong Quote V2 là tổng phần unit của Order đang nằm trong phiên, không phải FinalAmount gốc. Tổng phân bổ của một Order trên mọi phiên luôn bằng giá gốc của các unit hợp lệ.

MVP tiếp tục chặn khuyến mãi/discount/tax/fee cần phân bổ chưa được duyệt; yêu cầu cả hai quote không có yếu tố tài chính chưa hỗ trợ. Không lấy giá Menu mới để định giá lại. Không chuyển hết các unit còn lại làm phiên nguồn về 0đ đến khi có contract đóng phiên 0đ. PayFirst từ QR khách vẫn BLOCKED vì cờ trả tiền/cho pha hiện ở cấp Order/ticket; không dùng chuyển món để bỏ qua điều kiện này.

## 4. Contract V2 cần triển khai

Các DTO/route sau là thiết kế, **chưa đăng ký trong service**:

- `SessionBillQuoteV2`: `schemaVersion=2`, tenantId/branchId/sessionId/tableId, billId theo BillIdentity hiện có, fingerprint, totals và `units[]`.
- Mỗi unit: `cupId`, sourceOrderId, sourceOrderItemId, unitIndex, originSessionId, currentSessionId, allocationVersion, routeVersion, unitAmount, options/name snapshot. Backend quyết định số tiền; fingerprint canonical bao phủ đầy đủ unit identity, amount, version và nơi chịu tiền.
- `FreezeBillV2`: session/bill/fingerprint + allocationManifestHash. Ordering khóa và kiểm tra không còn active transfer, đủ manifest, chưa checkout khác; lưu snapshot V2. Không gọi HTTP khi đang giữ transaction của cả hai service.
- `BillUnitPaidNoticeV2`: eventId, tenant/branch/session/bill/paymentId, method, paidAt, schemaVersion, allocationManifestHash, total và exact unit manifest. Payload hash idempotent; cùng bill/payment nhưng nội dung khác phải REVIEW/409.
- `UnitFulfillmentSnapshotV2`: eventId, order/ticket, immutable manifestHash, sourceVersion và từng CupId/preparation/service/currentSession/routeVersion/servedAt. Snapshot phải đủ đơn vị hoặc có giao thức chunk có manifest hoàn chỉnh; không chấp nhận thiếu ly như đã phục vụ hết.
- Internal unit-paid receiver riêng hoặc route version rõ ràng; không gửi V2 partial manifest đến `/internal/session-billing/paid` V1. Public Cash/QR route có thể giữ nguyên, Backend chọn protocol từ frozen snapshot, không để Mobile tự chọn bỏ guard.
- Public preview/create/status/history/cancel-before-commit của item transfer giữ phương án trong `ITEM_TRANSFER_DESIGN_FOR_APPROVAL.md`; `Idempotency-Key`, expected unit/allocation versions và hai quote fingerprints bắt buộc.
- Public transfer: CASHIER/BRANCH_MANAGER/BRAND_ADMIN theo CounterRoles hiện có và scope branch; BARISTA bị từ chối. Receiver private dùng M2M key/scope riêng, ràng buộc tenant/branch/payload hash; không tin actorId public request, lấy actor từ JWT.

Unit settlement không cập nhật Order thành PAID khi mới có một Bill trả tiền. Cần DTO coverage `paidUnits/totalUnits/paidAmount` cho Order mixed-session. Không nhét một PaymentId giả vào Order đại diện nhiều giao dịch; legacy cancel/refund/payment theo Order phải fail closed đối với Order đã phân bổ, đến khi có contract unit-aware. Reporting ghi khoản thu theo Bill receipt, không nhân doanh thu bằng việc phát lại OrderPaid cho từng phần.

## 5. Khóa, đồng bộ và reconciliation

1. Xem trước Payments eligibility ngoài transaction; mặc định chỉ phiên chưa freeze/chưa có attempt, không chỉ kiểm tra chữ UNPAID. Một Bill UNPAID có attempt thất bại/không rõ vẫn cần xác minh.
2. Ordering dùng cùng key với TableSessionLock, khóa source/target table theo thứ tự ổn định và recheck session/table IDs sau khi lấy lock; kiểm tra `SessionCheckouts` cả hai phía. Add Order, Freeze, Close và Table Transfer cần đọc cùng barrier pending transfer.
3. Ghi operation + reservation các unit trong một transaction Ordering; không chấp nhận một phần batch. Không gọi Kitchen khi transaction PostgreSQL đang mở.
4. Kitchen prepare giữ Order lock, kiểm tra expected Cup version/cycle, Pending+Waiting, chưa Claim, Hold/PayFirst/print-routing không mơ hồ, rồi receipt+route reservation cùng transaction. ACK sau commit.
5. Ordering ghi `COMMIT_DECIDED` và đổi currentSession/allocationVersion nguyên tử. Hai bên Quote/Freeze/Close bị khóa khi operation chưa đối soát xong; không để lộ quote thu tiền nửa chừng.
6. Finalize Kitchen chỉ cập nhật route, không xóa/sửa nguồn admission/CupId/Claim/tem. ACK có operation/hash/decisionVersion; lưu FINALIZED trước response. Ordering ACK và giải phóng reservation sau bằng chứng hợp lệ.
7. Retry sau mất ACK hỏi lại receipt và replay cùng operation. Lease hết hạn chỉ cho worker khác xử lý, không release ly. Fencing token bảo vệ kết quả worker cũ; không tự abort khi timeout.
8. Cancellation chỉ trước quyết định commit, phải có abort evidence Kitchen và không có commit decision. Sau commit không sửa/hủy lịch sử; chuyển ngược là tác vụ mới nếu vẫn đủ điều kiện.
9. Reconciliation quét tất cả operation chưa terminal bằng chỉ mục nextAttempt/id; không bỏ Order/operation vì quá cũ. Kiểm tra decision/receipt/hash/route/assignment mismatch; REVIEW và chặn thu tiền nếu không chứng minh được. Redis chỉ là notification.
10. Serving V2 cập nhật projection theo unit và route version hiện tại; event cũ không được đổi chủ sở hữu. Sau transfer finalize phải tạo snapshot V2 mới dù trạng thái pha chế chưa đổi. Close chỉ xét exact units của phiên hiện tại, không dựa trên Order.DeliveredAt hoặc toàn Order đã xong.

Nguồn Order là lịch sử, không phải chủ sở hữu tài chính hiện tại. ProjectionWriter hiện đọc Order AsNoTracking và ghi projection riêng là điểm có thể tái sử dụng; không được kéo mọi cập nhật phục vụ về việc sửa Order gắn với phiên gốc đã đóng.

## 6. SQL/schema prototype

Thư mục `docs/proposals/item-transfer/`, sáu bảng additive:

| Service | Bảng | Vai trò |
| --- | --- | --- |
| Ordering | unit_session_allocations | Một owner/CupId; giá nguồn, origin/current session, version, reservation, settlement proof và unit serving projection |
| Ordering | item_transfer_operations | Request key/hash, actor, source/target, exact units, decision, audit và retry lease; dùng làm saga/outbox, không thêm outbox trùng |
| Ordering | unit_settlement_receipts | Inbox PAID V2 theo Bill/payment/hash, chống replay sai manifest |
| Kitchen | cup_delivery_routes | Nơi giao hiện tại và reservation/version, own FK CupTask; không sửa source ticket/admission |
| Kitchen | cup_transfer_receipts | Prepare/finalize/abort evidence và audit; không lạm dụng CupOperation vốn yêu cầu version tăng sau mỗi hành động ly |
| Payments | session_bill_units | Manifest freeze bất biến; unique tenant/CupId và tenant/sourceItem/unitIndex, own FK Bill |

Chỉ FK nội bộ service. Không có sequence mới, không GRANT sequence. Quyền proposal: SELECT/INSERT mới, UPDATE đúng cột workflow; không sửa identity/giá nguồn, không UPDATE manifest Bill, không DELETE/TRUNCATE/ownership. Role ở runner là `design_*_svc` NOLOGIN dùng SET ROLE để kiểm tra effective permissions; **chưa kiểm thử LOGIN bằng credentials của service vận hành**.

Prototype tái sử dụng FulfillmentOutbox/Receipt cho V2; một phiên bản nguồn chỉ có một snapshot có phân biệt schemaVersion/hash, không ghi hai event cùng unique sourceVersion. Hai DTO serializer/dispatcher phải hiểu V1/V2 trước khi bật. Không sửa EF model hoặc migration history hiện tại trong lượt này; SQL chưa phải migration EF hoàn chỉnh và chưa được duyệt chạy vận hành.

## 7. Tương thích UI, Recovery và tem

- Flutter/Web phải hiển thị current destination per Cup; tiêu đề Order/ticket cũ chỉ là nguồn. Endpoint Barista hiện trả Cups nhưng summary/print Location còn theo ticket: cần cập nhật trước khi cho mixed routing. Web FE chưa có trong workspace nên client thực tế NOT TESTED.
- CupSticker.PrintedAtUtc là thời điểm sinh dữ liệu, không chứng minh máy in đã in. Không dùng cột này để kết luận được/không được chuyển. Print job PENDING/UNKNOWN/đã gửi hoặc label đã in cần route-version snapshot và relabel policy; mặc định chặn trường hợp mơ hồ. Không tự reprint hoặc tuyên bố tờ tem vật lý đã đổi.
- Recovery replay nguồn ban đầu giữ nguyên; current route lấy từ route projection mới. Source bị thay sẽ conflict/Hold, không được tự xóa Hold để chuyển.
- Table Transfer toàn phiên CupV1 và phiên có unit inbound/outbound vẫn bị chặn tới khi current-route relocation contract đủ; không chuyển nhầm các ly gốc đã thuộc bàn khác.
- Draft SplitBill cũ trở thành stale khi unit membership đổi. Không tự chuyển số tiền hoặc sửa nhóm PAID.

## 8. Kết quả và phân loại kiểm thử

Kết quả thực tế cuối lấy từ `ITEM_TRANSFER_DESIGN_TEST_EVIDENCE.json`; không dùng kết quả task cũ để tuyên bố lần này PASS.

- Runner `node scripts/tests/item-transfer-design.mjs`: PostgreSQL/Redis mới, internal network riêng, tmpfs, không publish host port, ba database `aicos_item_transfer_design_*`, mật khẩu ngẫu nhiên chỉ truyền environment; kiểm tra container/cluster trước mọi SQL.
- Baseline là SQL EF hiện tại của ba service, không sao chép dữ liệu/secrets vận hành. Fixture được tạo từ Entity và CupAdmissionWriter thật; các Bill/checkout/unit-serving sau đó là fixture SQL để kiểm tra thiết kế, **không phải Cash/QR/Close API thật**.
- Đã test DDL, unique/FK, repeat refusal, rollback empty/reapply, nonempty rollback refusal, ACL cột, cùng chi nhánh/OPEN/Pending, bảo toàn tiền, same-key retry/hash conflict, hai actor tranh reservation, transaction rollback, unit routing replay, Redis restart, receipt recovery, giữ nguồn admission/Order/ticket/tem, PAID fence và serving query theo currentSession.
- HTTP compatibility probes dùng EF InMemory/JWT test: quote vẫn tính toàn Order và freeze từ chối partial fingerprint; PAID receiver V1 từ chối partial obligation mà không MarkAsPaid Order. Test PASS có nghĩa là **đã xác nhận giới hạn hiện tại**, không phải đã sửa recovery/payment V2.
- Chưa có API transfer/M2M V2, worker thật, process-kill/crash HTTP E2E, mobile-device E2E, Web E2E hoặc QR ngân hàng. Không coi Redis restart hoặc hàm SQL replay là crash recovery PASS của service.

## 9. Blocker trước đề xuất kích hoạt

1. Triển khai Quote/Freeze/Unit PAID V2 và serializer/outbox/inbox thật, unique unit manifest; cập nhật coverage Order mà không tạo payment giả.
2. Bổ sung reservation guard tại mọi Cup/Barista/preparation/claim/print entrypoint cùng lock; hiện SQL reservation chưa được UoW vận hành đọc.
3. Triển khai unit fulfillment V2, pending transfer barrier và close eligibility theo phiên hiện tại; tests thật cho source đóng trước khi target phục vụ xong.
4. Chốt tem/relabel/PayFirst, zero-total source, promotions/tax/fee và tác vụ sửa/hủy/refund sau phân bổ. Chưa được tự mở rộng MVP vào các trường hợp này.
5. Chuyển proposal thành EF migrations additive sau khi contract được chốt, verify schema/ACL/backup-restore/rollback trên bản sao; giữ feature OFF trong quá trình chuẩn bị.
6. HTTP/Identity/Gateway E2E cô lập: hai Cashiers, Barista vs prepare, Freeze/AddOrder/Close vs transfer, duplicate/late events, timeout, worker takeover, abort race, mất ACK và process kill trước/sau từng commit; Web/Mobile review và feature rollout phê duyệt riêng.

Chưa đủ điều kiện đề xuất bật Cup transfer trên môi trường vận hành. Không apply aicos_db, không deploy, không commit/push.

## 10. Kết quả cuối của lượt kiểm chứng

- PostgreSQL/Redis design probe: **15 nhóm PASS, 2 BLOCKED, 1 NOT RUN, 0 FAIL**; cluster **7708093778038202403**. Containers có trước giữ nguyên: **true**. Runner đã dọn đúng tài nguyên test của nó.
- Backend compatibility/CupV1/Bill focused: **48 PASS, 9 SKIP**. Hai test mới xác nhận ranh giới Quote/PAID V1; không phải chứng minh đã thanh toán phần chuyển được.
- Backend integration regression: **561 PASS, 51 SKIP**, không có FAIL. Các test hạ tầng yêu cầu biến môi trường riêng vẫn SKIP; kết quả SQL probe không được đổi tên thành những test đó PASS.
- Build probe thành công trong runner. Lỗi import khi chuẩn bị test đã được sửa trước các lượt PASS cuối. Không tính lượt compile fail là PASS.
- Chưa thực thi Cash/QR hoặc đóng phiên bằng manifest chuyển món V2. Chưa có EF migration cho model V2, chưa chạy SQL trên database vận hành.
