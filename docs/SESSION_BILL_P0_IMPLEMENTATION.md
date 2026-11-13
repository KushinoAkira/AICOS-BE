> Current feature-flagged Bill implementation: [SESSION_BILL_IMPLEMENTATION.md](SESSION_BILL_IMPLEMENTATION.md). Earlier P0/blocked notes below are historical; no operating schema deployment or real-bank QR is complete.

# Session Bill integration — P0 implementation checkpoint

Ngày: 2026-11-13. Đây là checkpoint triển khai, **chưa hoàn thành end-to-end Cash/QR theo Bill**.
Không deploy, không apply migration, không khởi động/recreate stack, không ghi database vận hành.

## 1. Thay đổi Backend đã triển khai

- `Ordering/Features/Qr/QrOrderEndpoints.cs`: Đã xóa auto-close helper và các call sites Payment/cancel/exception. Manual close xác thực tenant/branch, khóa cùng key POS/transfer, đọc lại trạng thái và trả 409 khi phiên có bất kỳ Order nào nhưng chưa có Bill settlement/fulfillment permit. Không bỏ qua Order void có refund chưa xác minh. Phiên rỗng có thể đóng idempotent.
- `Ordering/Features/Qr/TableSessionLock.cs`: transaction ReadCommitted + PostgreSQL advisory lock `aicos:counter-table:{tenant}:{table}`. QR checkout kiểm tra lại phiên/bàn sau lock, rollback và phục hồi giỏ khi stale; idle sweep chỉ đóng phiên rỗng, kiểm tra lại dưới lock. Không đóng phiên có paid/unserved orders theo tuổi phiên.
- `Ordering/Features/Qr/SessionCloseEligibility.cs`: API GET mới `/api/v1/orders/qr/sessions/{id}/close-eligibility`, Staff JWT, scope tenant/branch. Trả `sessionId,branchId,canClose,unsettledOrders,unservedOrders,requiresBillVerification,reasons,advisoryOnly`. Không gọi provider/SaveChanges; không phải close permit. POST vẫn kiểm tra lại.
- `Ordering/Features/Orders/CounterOrderRequests.cs`: không thêm lượt khi mọi live round đã ghi nhận Paid/Refunded. QR checkout dùng cùng kiểm tra. Đây là guard bảo thủ cho legacy projection, **chưa thay thế atomic Bill checkout fence**.
- `Payments/Features/Payments/OnlinePaymentCreation.cs`: tách orchestration khỏi endpoint validation. Commit intent + transaction reference trước provider I/O; HTTP không giữ transaction PostgreSQL. Mất response hoặc process dừng để lại intent PENDING có thể tìm lại, không mất reference.
- `Payments/Features/Payments/PaymentEndpoints.cs`: giữ nguyên route/request; replay cùng order/method/amount/branch/actor trả attempt cũ. Không tự cancel pending để đổi method/actor. Không gọi provider Create lần hai nếu intent đã commit nhưng chưa có checkout URL.
- `Payments/Domain/Payments.cs`, `Persistence/PaymentsDbContext.cs`: property tính toán `RequiresReconciliation`, EF Ignore, không thêm cột. Expired/Cancelled/Failed local mà provider chưa xác nhận Failed vẫn chặn thu lại; provider đã báo received nhưng chưa apply cũng chặn.
- `CashCollection.cs`, `OrderSettlementEndpoints.cs`: Cash và GET advisory dùng cùng predicate unresolved; local expiry/cancel không trở thành quyền thu tiền mặt.
- `Infrastructure/PaymentProcessor.cs`: expiry chỉ đánh dấu deadline, không phát PaymentFailed(timedOut) để tự hủy Order. Provider failure có thể giải quyết uncertainty của attempt Expired/Cancelled mà giữ lịch sử local; success và failure dùng cùng order lock. Success nhận muộn vẫn được ghi nhận.
- `PaymentSupport.cs`: thêm response field `requiresReconciliation`, không xóa/đổi field cũ.

P0 không cần migration. Chưa sửa DbSet/schema Identity hoặc quyền database. Payments vẫn tham chiếu Order qua HTTP, không đọc trực tiếp bảng Ordering/Kitchen.

## 2. Quyết định nghiệp vụ đã chốt

Người dùng chọn: **sau Bill PAID, chặn gọi thêm; phục vụ xong, đóng phiên rồi mở phiên mới**.
Thanh toán và phục vụ độc lập. Paid không tự đóng phiên, không reset món/ticket/custody, không tự mở phiên mới.
Guard legacy đã chặn khi mọi live round đều Paid/Refunded; việc chặn chính xác theo Bill và race lúc thu tiền còn cần Ordering checkout fence bền vững.

Manual close phiên đã có Order hiện trả `qr.session_verification_required`, kể cả tổng Due bằng 0 hoặc Order đã void. Đây là fail-closed có chủ đích, không phải close workflow đã hoàn thiện. Không dùng frontend hoặc tổng payment flags thay Payments/Kitchen proof. Web FE có thể nhận thêm 409 ở các thao tác trước đây không an toàn; JSON/route cũ giữ nguyên. Không có Web FE checkout source để kiểm thử client đó.

## 3. Thay đổi Mobile

- Repository đọc GET close-eligibility cùng kiểm tra giao dịch; kiểm tra sessionId/branchId và đọc lại snapshot phiên sau request. 404/403/503/malformed => unknown; 401 => session expiry. Không gọi POST close hoặc tự đánh dấu bàn trống.
- Panel Bill hiển thị lý do tiếng Việt. Kết quả stale không giữ làm kết quả hiện tại, không mở khóa Cash/QR/in invoice.
- `SessionEstimateDocument` tạo HTML in độc lập từ snapshot món/giá/tổng có sẵn. Có cột món/tùy chỉnh, quantity, unit price, line total, notes; giữ riêng các tùy chỉnh khác nhau. Nullable tax/fee/discount giữ unknown, không bịa 0.
- Escape toàn bộ text, không script/external assets; không chứa dòng Còn phải thu hoặc tiền đã trả; luôn nhãn TẠM TÍNH – CHƯA XÁC MINH THANH TOÁN.
- `SessionEstimateExport` cho sao chép HTML, chống double-submit khi đang xuất, retry lỗi clipboard. Không gọi driver máy in, không báo print success. Chưa tạo invoice PAID vì chưa có authoritative Bill response.
- Localize mã lỗi session/online pending, giữ theme/Demo Mode. Không viết BE trong Mobile.

## 4. Schema đề xuất — chưa được thực thi

`docs/proposals/session-billing/000_preflight.READ_ONLY.sql`
`docs/proposals/session-billing/001_payments_expand.PROPOSAL.sql`
`docs/proposals/session-billing/SHA256SUMS.txt`

Đây là **SQL draft để review, không phải EF migration đã sinh/đã kiểm thử triển khai**. Không chạy script này ngay.

Bốn bảng Payments sở hữu:

1. `session_bills`: unique tenant/session, branch/actor, immutable identity, version, ordering revision, tổng snapshot và lifecycle.
2. `session_bill_orders`: snapshot Order/món theo version, không FK sang Ordering.
3. `session_bill_attempts`: idempotency key hash + request fingerprint + stable provider ref; partial unique index chặn nhiều attempt unresolved trên cùng Bill, kể cả khác cashier/method.
4. `session_bill_events`: audit/outbox cùng transaction với tài chính, retry/lease/fencing khi sync sang Ordering.

Không sửa bảng `payments` cũ vì OrderId là bắt buộc và đang phục vụ Web/Takeaway/refund. Không tạo fake Order để chứa tổng session.
Script guarded bởi explicit approval setting và expected database; dừng nếu target tồn tại để đối chiếu. Không backfill/data copy/role grants/drop/truncate/delete. SHA256 được ghi kèm. **Chưa chạy DDL trên bất kỳ DB nào.**

Phần còn phải hoàn thiện trước EF migration/deployment approval: Ordering checkout fence/inbox và protocol prepare/commit/reconcile, snapshot immutable, chặn mọi mutation tiền (gọi thêm/sửa/hủy/giảm giá) trong lúc thanh toán. Không chỉ khóa Payment local rồi giả định Order snapshot không đổi. Cần DDL riêng thuộc Ordering, không cross-service FK hoặc distributed transaction.

Rollback an toàn sau expand: tắt feature mới, giữ dữ liệu/bảng additive, giữ P0 protection. Trước commit có thể rollback transaction triển khai. Không tự drop financial records, không chạy Down destructive. Cần backup và restore drill ở môi trường cô lập trước deployment.

## 5. Kết quả kiểm thử

Kết quả cuối ghi tại báo cáo Mobile và log kiểm thử; không gộp test skip thành PASS.

- Full Backend HTTP regression: **511 PASS / 44 SKIP / 0 FAIL**, 555 cases. EF InMemory/test doubles/test-signed JWT là chủ yếu, không phải provider/PostgreSQL E2E.
- PostgreSQL riêng: **13 PASS / 0 SKIP / 0 FAIL**, runner `scripts/Test-CashCollectionPostgres.ps1 -Run`. PostgreSQL thật, HTTP handlers thật; Identity/Ordering/Redis/provider có test doubles.
- Run PostgreSQL đầu: scratch `aicos-cashflow-postgres-3ad3f85a1a`, database `aicos_cashflow_test`, loopback random port 55987, cluster `7707625904551301155`, private network + RAM tmpfs; runner xác minh identity trước EnsureCreated. Chỉ cleanup tài nguyên của lượt test. Không dùng `aicos_db`/Windows/Gate D/Gate C.
- Cases mới: sixteen online retries một attempt; tám cash requests bị chặn bởi expired unresolved; timeout sau durable intent không gọi Create lại; provider failure giải quyết expired uncertainty; paid chưa served không close; cancellation không tự close; readonly scoped eligibility; empty close replay; cart/POS post-paid guard.
- Full Flutter: **283 PASS / 0 FAIL**, analyzer 0 issues, gồm layout 360/393/430 và export/escaping/single-flight/session-scope tests. Không coi widget/mock tests là thử trên điện thoại thật.
- PostgreSQL/Redis full session Bill E2E: **NOT RUN** (Bill API chưa triển khai). QR provider thật, máy in thật, Web FE runtime: **NOT RUN**.
- Hai cảnh báo compile cũ: CS0628 WorkforceReviewTests và CS8620 OrderTests. Không thay code ngoài phạm vi để che cảnh báo.

## 6. BLOCKED / chưa hoàn thành

- GET Payments Bills và Cash/QR theo Bill **chưa được tạo**, Mobile chưa gửi payment Bill mutation. Không dùng per-order Cash lặp nhiều lần cho Dine-in.
- Cần hoàn thiện/duyệt schema EF + Ordering checkout contract. Sau PAID không reopen Bill để gọi thêm (quyết định đã chốt).
- Cần admission/query provider thật với signed callbacks, replay protection theo quy tắc provider. Các provider hiện tại là SimulatedGateway, kể cả tên VietQR/Momo/ZaloPay; HMAC/test PASS không chứng minh sandbox ngân hàng thật đã nhận tiền.
- Khi intent không có checkout URL do timeout, retry trả attempt đó; chưa có worker tự phục hồi provider checkout. Không thử Create mới khi chưa đối soát.
- Durable financial outbox/inbox và close permit liên service chưa triển khai; Redis legacy vẫn không phải delivery guarantee cho sự kiện tài chính. Vì vậy giữ manual close used session fail-closed.
- Chưa có thiết bị/transport máy in. HTML export không phải printer job ACK. Chưa có invoice PAID để in lại.
- Identity hiện một role/account theo BR-01; Cashier canonical đủ thao tác quầy/phục vụ hiện có, không tự cấp thêm role SERVER hoặc sửa user database.

Đây là checkpoint P0 + phần Mobile độc lập, **không tuyên bố toàn bộ luồng thanh toán hoàn thành hoặc production-ready**.
### Giới hạn legacy cần lưu ý

`CounterOrderRequests.ResolveSessionAsync` vẫn giữ request legacy không có TableQrToken/ExpectedSessionId ở chế độ unscoped để tránh tự ý phá contract Web FE. Guard post-paid hiện áp dụng cho luồng Mobile/QR có session reference, chưa chứng minh tất cả legacy client đều không bypass session. Phải giải quyết admission của Dine-in legacy trong bước triển khai Bill checkout fence, với test contract Web FE; không tuyên bố post-paid guard đã đủ để mở Bill production.
### PostgreSQL xác nhận lại trên source cuối

Runner cùng guard đã chạy lại: **13 PASS / 0 SKIP / 0 FAIL**. Scratch container
`aicos-cashflow-postgres-a2b33bef7a`, loopback port 61837, cluster
`7707629823731286052`, database `aicos_cashflow_test`, network riêng/tmpfs.
Runner cleanup đúng container/network do lượt này tạo. Không cộng hai lượt chạy
13 test thành 26 test khác nhau.