# Nonfinancial operations: serving, staff coordination, and recovery

> Latest P0 admission implementation/test result: [KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md](KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md). Admission outbox/receipt/reconciliation now pass isolated tests; other blocked workflows and general durable event transport remain outside that implementation.

Reviewed against Backend `main` on 2026-11-05. This is a design proposal; no migration or production data change was performed. Latest isolated PostgreSQL/Redis test evidence is in `STAFF_OPERATIONS_INTEGRATION_TEST_RESULTS.md`; the design proposals below remain unimplemented.

Thiết kế P0 admission cụ thể mới nhất: [KITCHEN_ORDER_ADMISSION_RECOVERY_P0_DESIGN.md](KITCHEN_ORDER_ADMISSION_RECOVERY_P0_DESIGN.md). MVP đề xuất outbox PostgreSQL → internal HTTP ACK → Kitchen receipt và reconciliation; không cần chuyển Redis Pub/Sub sang Streams trong bước này. Các phần recovery tổng quát bên dưới là phương án dài hạn, chưa triển khai.

## Implemented without schema changes

- Kitchen now accepts optional `expectedStatus` and `expectedRemakeCount` on the existing `PUT /api/v1/kitchen/tickets/{id}/status`. The old request remains valid for Web clients. A matching replay returns the current ticket without emitting another notification. A stale status or remake cycle returns `409 ticket.stale`. The `GET /api/v1/kitchen/staff/capabilities` response advertises version 1.
- Kitchen ticket status, remake, and order delivered/cancelled/completed event handlers use the same PostgreSQL advisory transaction lock keyed by tenant and order. They reload the ticket under the lock. The latest isolated infrastructure tests verify two concurrent writers waiting in PostgreSQL `pg_locks`, alongside the existing EF InMemory state-rule tests. Separate deployed process/replica testing remains NOT RUN.
- Mobile sends the status and remake count visible at confirmation time, prevents a second tap on the same ticket while confirmation is pending, and reloads on `409`. An old Backend without the capability fails closed. This protects Kitchen status updates; it does not reserve a task for a staff member.
- Mobile order detail no longer claims the creator name is unavailable when the name is already shown.

## Serving one item and multiple staff: BLOCKED by persistence and contract

`OrderItem` has an ID, but `OrderItemDto` in `OrderEvents.cs` does not carry it. `KitchenTicketItem` has no OrderItemId or preparation status. The current main delivery endpoint records one `DeliveredAtUtc`/`DeliveredBy` on the whole order. The demo claim/release state is local and must not be used as a real claim. Matching by product name is ambiguous when a product appears with different options, notes, or quantities.

Proposed additive design, subject to schema and business approval:

1. Add an optional stable OrderItemId to the event line, preserving old event fields/consumers. Populate it from the persisted order item. Backfill old Kitchen tickets only where the mapping is unambiguous; otherwise leave the line legacy whole-order.
2. Persist Kitchen line preparation state and quantity, correlated by OrderItemId. Persist a serving task per line with total quantity, served quantity, claim owner, claim quantity, lease/version, and timestamps. Store an operation key and audit records for claim, release, handoff, and serve.
3. Claim and serve use one transaction with a row lock or compare-and-swap version. Invariant: `served + active claims <= prepared quantity`, each staff claim has one owner, and repeated operation keys replay the same result. Lease expiry releases capacity only; it never marks food served.
4. Preserve whole-order delivery for legacy tickets. New per-item orders emit whole-order Delivered only when all required quantities are served or formally voided. Pending and partly prepared lines remain visible to Kitchen and Serving. Do not enable the partial UI before a versioned API and migration are approved.

The existing Kitchen advisory lock has no claim table and does not serialize writes in Ordering/Table. It cannot establish exclusive staff assignment across services. PostgreSQL/Redis E2E with two simultaneous staff requests is required before claiming this behavior works.

## Order edit and cancel: BLOCKED by state policy and audit

Cart editing before submit remains available. Submitted orders are dispatched to Kitchen; there is no durable unsent-to-Bar state in the current contract. `OrderEndpoints.CancelAsync` calls `Order.CancelOrder`, whose guards do not enforce Bar acknowledgement for Preparing/Ready or protect already served unpaid items. It also has Web callers and a payment verification path. Replacing its rules or exposing it in Mobile now would risk a breaking change or a financial inconsistency.

Proposed rule matrix for approval:

| State | Edit/cancel rule | Actor |
| --- | --- | --- |
| Local cart, before submit | Direct edit/remove, server revalidates create | Order creator |
| Submitted, Kitchen Pending | Request change; apply only after version check and Kitchen acknowledgement | Cashier/Manager requests; Bar/Manager approves |
| Brewing or Ready | Request change with reason; cannot silently discard prepared quantity | Cashier/Manager requests; Bar/Manager resolves |
| Partly or fully served | No direct mutation; manager exception and physical recovery record | Manager |
| Paid/closed | Deferred to Payment/Bill policy | Separate approval |

Any new mutation needs expected order/ticket revision, idempotency key, reason, actor/approver, durable audit, and the old/new line snapshots. Existing Web endpoint should remain supported while a versioned request workflow is introduced. No Mobile button for post-submit edit/cancel is enabled in this step.

## Merge occupied tables: BLOCKED by schema and session policy

The existing transfer endpoint rejects an occupied target (`table.target_occupied`). A simple TableId update would leave two active sessions, QR tokens/carts, orders, and the one-bill-per-session invariant inconsistent. Decide which session survives, whether guest carts are moved or invalidated, and how to handle already submitted orders and financial balances. Persist table membership/history and a redirect from the retired session; validate both sessions/branches and lock both in stable order. Keep transfer separate from merge. Bill membership changes require separate Payment/Bill approval. No merge API/UI was activated.

## Redis event recovery: BLOCKED by durable storage

`RedisEventBus.PublishAsync` and `RedisEventBusSubscriber` use Redis Pub/Sub. A subscriber offline during publish misses the message. `AuditTrail` publishes through the same bus. Mobile's 15-second GET refresh can repair its display from Backend state, but it cannot create a missing Kitchen ticket or replay a lost Delivered event.

Proposal: transactional outbox in each producing service, inbox dedupe in each consumer, stable event ID/aggregate version, retry with backoff and dead-letter inspection, plus a service-authenticated reconciliation endpoint/job that compares authoritative Order/Session state with Kitchen projections. Keep Pub/Sub as a wake-up signal if useful; the outbox is the durable source. Define retention, replay order, and idempotent projection rules. The latest isolated infrastructure test demonstrates successful reconnect after Redis restart and a missing Kitchen ticket when OrderCreated was published while Kitchen was offline. Durable recovery still FAILS; the outbox/inbox proposal has not been implemented.

## Approval required

- Additive OrderItem/Kitchen/Serving storage and contract for partial fulfillment and staff claim.
- State/role matrix, manager exception policy, and persisted change-request/audit workflow for submitted orders.
- Session merge behavior involving one aggregate bill, guest cart/token, and occupied table membership.
- Outbox/inbox schema and service-authenticated reconciliation contract.
- Dedicated PostgreSQL/Redis test environment and fixtures were separately approved and exercised in the latest test report. This permission covers disposable test schema only, not these proposed migrations or production deployment.

## Thiết kế MVP chi tiết để duyệt — chưa triển khai

Phạm vi phân quyền dùng đúng role hiện có: `Cashier`, `Barista`, `BranchManager`, `BrandAdmin`. Từ “Staff” dưới đây chỉ nhân viên thuộc nhóm role được cấp quyền; source hiện tại chưa có role Waiter độc lập. Không tự thêm role hoặc mở quyền cho Customer/Guest. Mọi truy vấn/mutation kiểm tra tenant và chi nhánh trên server.

Các API dưới đây là **đề xuất**, chưa có endpoint chạy thật. Tất cả command mới yêu cầu `Idempotency-Key`, `expectedVersion`, lý do khi hủy/override và actor lấy từ JWT. Ledger lưu fingerprint request và kết quả: cùng key/cùng payload trả kết quả cũ; khác payload trả 409. Kiểm tra quyền và scope trước khi trả replay. Giữ khóa giao dịch, unique/check constraints và audit cùng transaction. Không dùng GET snapshot làm quyền thực hiện một mutation tiếp theo.

### 1. Serving từng món

**Dữ liệu và ownership**

- Ordering giữ OrderItemId ổn định, fulfillmentMode (`WholeOrder` mặc định cho order cũ, `PerLineV1` chỉ bật sau khi được duyệt), revision và serving task theo line.
- Kitchen giữ OrderItemId nullable cho line cũ, preparation state, prepared quantity và preparation version. Event line mới thêm optional OrderItemId; thiếu correlation thì giữ luồng legacy, không tự suy theo tên/số thứ tự.
- Serving task trong Ordering: id, tenantId, branchId, orderId, orderItemId, requiredQuantity, readyQuantity projection, servedQuantity, state, version và servedAt/servedBy. History phải lưu nhiều lần giao nếu sau này hỗ trợ chia quantity.
- Không đọc bảng service khác. Kitchen phát line-ready event có version; Ordering lưu projection. Chậm event thì chưa cho nhận món, có chỉ dẫn tải lại, không coi order-level Ready là mọi line đã sẵn sàng.

**API/DTO đề xuất**

| Method/path | Request | Response |
| --- | --- | --- |
| GET `/api/v1/orders/{orderId}/serving` | — | fulfillmentMode, orderVersion, lines[{taskId, orderItemId, productName, options/note, requiredQuantity, readyQuantity, servedQuantity, state, version, claim}] |
| PUT `/api/v1/kitchen/tickets/{ticketId}/items/{itemId}/preparation` | targetState, expectedVersion, expectedPreparationCycle | line snapshot/version |
| POST `/api/v1/orders/{orderId}/serving/tasks/{taskId}/serve` | claimId, expectedVersion | authoritative task/order fulfillment snapshot |

**State/rule:** Kitchen line `Pending → Brewing → Ready`; phục vụ `Waiting → Ready → Claimed → Served`. Cancelled chỉ sau workflow duyệt hủy. Ticket có thể Mixed/PartlyReady ở DTO suy ra từ line; không tự đổi enum ticket legacy. Whole-order Delivered chỉ khi mọi quantity bắt buộc đã Served hoặc được void hợp lệ; không tự đóng session hoặc đổi Payment/Bill.

**Quyền:** Barista/Manager cập nhật preparation; Cashier/Manager nhận/giao món trong chi nhánh; Barista không tự xác nhận khách đã nhận món. Manager override cần reason/audit, không sửa lịch sử Served.

**MVP tối thiểu:** giao toàn bộ quantity của một line trong một lần; ví dụ line 2 ly được giao cùng nhau. Hai line khác nhau có thể giao riêng. Chia 1/2 ly của cùng line để sau. Không thêm trạng thái giả trên Mobile trước schema/API.

**Migration đề xuất:** nullable OrderItemId và version/preparation fields ở Kitchen; fulfillmentMode/version và serving task/history ở Ordering; unique tenant/orderItem cho task, FK nội bộ service, check `0 <= served <= ready <= required`. Backfill mode WholeOrder; không backfill line correlation mơ hồ. Chưa tạo/chạy migration.

**Tác động Web:** giữ DTO/event cũ và default WholeOrder. Với order opt-in PerLineV1, endpoint deliver cũ phải trả lỗi rõ `order.partial_serving_required` để tránh bypass claim; Web cần hiểu feature capability và mode trước rollout. Đây là tác động phải review, không thể chỉ thêm endpoint rồi tuyên bố hoàn toàn tương thích.

**Tests:** cùng product khác options/note không map nhầm; mới 1/2 line ready không giao cả order; line Served không giao lại; tenant/branch/RBAC; delayed/duplicate/out-of-order line event; legacy thiếu OrderItemId vẫn chạy whole-order; không đổi paid/session khi giao line cuối.

### 2. Multi-Staff Claim

**Dữ liệu:** claimId, taskId, ownerUserId, quantity, status, leaseExpiresAtUtc, version, requestKey, created/released/servedAt và audit. MVP một active claim giữ toàn bộ quantity còn lại của line; chưa chia claim theo ly. Unique partial index một active claim/task; claim/serve cập nhật cùng task version trong transaction. Không chỉ khóa bộ nhớ process.

| Method/path | Request | Response |
| --- | --- | --- |
| POST `.../serving/tasks/{taskId}/claims` | expectedVersion | claimId, ownerDisplay, quantity, version, leaseExpiresAtUtc |
| POST `.../serving/claims/{claimId}/release` | expectedVersion, reason | task snapshot |
| POST `.../serving/claims/{claimId}/renew` | expectedVersion | new expiry/version |
| POST `.../serving/claims/{claimId}/reassign` | targetStaffId, expectedVersion, reason | snapshot; Manager only |

**State:** `Active → Served / Released / Expired`; terminal không kích hoạt lại. Hết lease không tự coi món đã giao; người đang mang món phải xác minh trước reassign. Serve xác minh owner, branch, lease, ready quantity và version trong cùng transaction. Staff khác nhận 409 `serving.claimed`; không trả 200 như thể đã nhận nhiệm vụ. Quyền Manager không cho phép xóa lịch sử.

**MVP:** claim có owner bền vững và release/serve; thời hạn lease có thể cấu hình, chưa tự reassign. Giữ tác vụ quá hạn để Manager kiểm tra thực tế. Migration thuộc Ordering; phụ thuộc task ở mục 1.

**Tác động Web:** thêm claim owner/status vào DTO, Web cũ vẫn xem order legacy. Không cho endpoint giao toàn order bypass task của order opt-in. Cần review Web thao tác trên cùng order, chưa có Web repo để chứng nhận.

**Tests:** hai connection/Staff cùng claim chỉ một winner; replay không tạo claim thứ hai; khác key/payload, sai owner/chi nhánh, hết lease, renew-vs-release, serve-vs-expire, manager reassign và retry sau commit mất response. Kiểm tra `served + activeClaim <= ready`, không chỉ đếm response 200.

### 3. Sửa/hủy Order sau submit

**Source hiện tại:** `OrderEndpoints.CancelAsync → Order.CancelOrder` cho staff chưa yêu cầu Bar duyệt ở Preparing/Ready; CancelAsync còn gọi CloseIfSettled. Vì Payment/Bill tạm hoãn, chưa sửa route này. “Pending” không có nghĩa chưa gửi Bar. Sửa note trong cart khác với sửa món đã submit.

**Dữ liệu:** order_change_requests thuộc Ordering gồm kind, orderId/lineIds, expectedOrderVersion, expectedKitchenVersion, before/proposed snapshot, reason, requester/approver IDs, status, timestamps, operation key, expiry và audit. Kitchen cần reservation/decision theo requestId để không bắt đầu/hoàn tất line đang chờ áp dụng thay đổi. Version check đơn phương không đủ bảo vệ race hai service.

| Method/path | Request | Quyền |
| --- | --- | --- |
| POST `/api/v1/orders/{id}/change-requests` | kind, lines, reason, expectedOrderVersion | Cashier/Manager |
| GET `/api/v1/orders/{id}/change-requests` | — | Staff đúng chi nhánh |
| POST `/api/v1/orders/change-requests/{id}/decision` | Approve/Reject, reason, expectedVersion | Barista xác nhận khả năng dừng pha; Manager duyệt theo policy |
| POST `/api/v1/orders/change-requests/{id}/withdraw` | expectedVersion | người yêu cầu/Manager |

Response: requestId, state, version, blockedReason, proposedChanges, kitchenDecision, approvedBy, appliedAt. State `Requested → AwaitingKitchen → Approved → Applying → Applied`; nhánh Reject/Withdraw/Expire, cùng request không áp dụng hai lần. Kitchen giữ reservation bền vững; durable command/ack hoặc reconciliation khôi phục giữa Approved và Applied. Không coi HTTP ACK đồng nghĩa thay đổi đã áp dụng.

**MVP:** quản lý yêu cầu và từ chối an toàn khi line đã Served; chỉ áp dụng chỉnh ghi chú không thay giá/quantity khi Kitchen còn Pending và đã giữ reservation. Hủy/chỉnh quantity sau submit vẫn BLOCKED cho tới khi duyệt tác động charge, CloseIfSettled và Bill. Không dùng legacy cancel để lách policy. Giữ cart editing hiện có.

**Migration:** order/ticket operation version, change-request/audit, reservation và inbox/outbox. Không thêm trigger đọc chéo DB. Order opt-in cần chặn mutation legacy bypass reservation; đây là thay đổi hành vi cần Web review trước bật feature.

**Tests:** Pending-vs-start, approval-vs-ready, reject/withdraw/expiry, replay apply, Kitchen offline sau reserve, restart giữa approve/apply, đã giao nhưng unpaid, RBAC mỗi decision, audit before/after; không tự đóng session và không gọi Payment/Bill trong MVP.

### 4. Ghép bàn có khách

**Dữ liệu:** table_session_memberships (sessionId, tableId, active, joined/leftAt), merge request/operation (sourceSessionId, targetSessionId, expectedVersions, actor/reason), retiredSessionRedirect và history. Ownership thuộc Ordering; Table giữ physical table projection qua contract/event. Unique active tenant/table membership; backfill một membership bàn chính cho mỗi session Open không mơ hồ.

**API đề xuất:** `POST /api/v1/orders/qr/sessions/{sourceId}/merge-preview` nhận targetSessionId và versions, trả read-only eligibility/conflicts/cart counts/order membership; `POST .../merge` xác nhận cùng preview fingerprint, key và reason; GET operation trả `Requested/Blocked/Applied`. Preview không phải giấy phép apply: server khóa hai session và các table theo thứ tự cố định, revalidate tất cả.

**Quyền:** Cashier/Manager xem preview; MVP apply chỉ Manager cùng branch/tenant. Không merge xuyên chi nhánh. Không cộng lại order bằng cách tạo order mới. Guest token/session cũ phải bị vô hiệu hóa hoặc redirect có kiểm soát; request add-more cũ trả 409 để xác nhận intent, không âm thầm ghi vào bàn khác. Cart phải quyết định giữ/gộp/xóa trước apply, không tự mất cart.

**State:** phiên source có thể thêm `Merged` với target canonical, target vẫn Open; không dùng Closed/Paid giả. Order vẫn giữ ID; membership và lifecycle history được lưu. Không chốt schema/state này trước review một Bill/phiên và các Web callers.

**MVP trong phạm vi phi thanh toán:** chỉ đề xuất ghép vật lý một bàn trống vào session đang có khách (membership), giữ một session; chưa phải merge hai session có order. Merge hai bàn đều có khách vẫn BLOCKED do Bill policy, cart/token và migration; không triển khai nút apply lúc này.

**Tác động:** POS và QR scan phải tra membership trước mở session; chỉ giữ unique index ở table_sessions.TableId hiện tại không ngăn một bàn phụ bị mở phiên thứ hai. Web cần hiển thị bàn phụ/canonical session và xử lý 409. Table service chỉ nhận projection, không truy vấn bảng Ordering.

**Tests:** opposing merge deadlock, đồng thời add-more/scan/transfer, source/target version cũ, replay key, cart đang sửa, token cũ, member table được scan, tenant/branch, rollback không orphan order/session. Tests tài chính chờ giai đoạn riêng.

### 5. Redis Event Recovery

**Dữ liệu:** producer outbox (eventId, aggregateId, aggregateVersion, type/schemaVersion, payload, createdAt, attempt/nextAttempt, publication state); consumer inbox (consumer/eventId unique, processedAt) và cursor/reconciliation state, failure/dead-letter audit. Không lưu secrets hoặc dữ liệu khách hàng không cần thiết trong payload.

**State:** outbox `Pending → Publishing → Published`, lỗi retry có backoff; Published chỉ xác nhận transport nhận, không khẳng định consumer xử lý. Inbox và projection commit cùng transaction. Out-of-order version thiếu khoảng phải retry/reconcile, không áp dụng mù. Hai event khác EventId nhưng cùng business effect vẫn cần dedupe bằng aggregate/version hoặc effect key.

**Transport:** outbox + Pub/Sub đơn thuần vẫn mất event nếu không có subscriber. MVP cần durable Redis Streams/consumer groups với ACK sau DB commit, hoặc một cơ chế đọc lại outbox/cursor từ service chủ. Không mark “delivered” chỉ vì PublishAsync trả thành công. Với Redis mất cả dữ liệu, PostgreSQL outbox giữ retention và reconciliation phải dựng lại projection. Cần chốt retention, Redis persistence/volume và replay policy trước rollout.

**API/DTO đề xuất:** service-authenticated `GET /internal/v1/operations/order-snapshots?cursor=...&limit=...` trả orderId, tenant/branch, aggregateVersion, fulfillment state, lines correlation, delivered metadata và tombstones cần thiết; cursor ổn định theo version/change log. Endpoint Kitchen snapshot tương tự cho prep state. Manager chỉ xem GET `/api/v1/operations/recovery/status`; manual replay là tác vụ quản trị có reason/audit và giới hạn scope, không cấp cho Cashier/Barista. Chưa triển khai endpoint hoặc tạo service credential.

**MVP:** ưu tiên OrderCreated/Started/Ready/Delivered với outbox/inbox và kiểm tra thiếu ticket/thiếu Delivered projection. Polling Mobile giữ vai trò refresh hiển thị. Không tuyên bố exactly-once; bảo đảm at-least-once + idempotent business effect. Payment event giữ nguyên ngoài phạm vi.

**Migration:** outbox/inbox/version/cursor ở service sở hữu; additive, không sửa migration chốt. Recovery worker rollout sau schema, cấu hình tắt mặc định và checkpoint có quan sát. Web không phải đổi endpoint legacy; sẽ thấy trạng thái khớp trở lại. Contract version phải hỗ trợ consumer cũ trong thời gian nâng cấp.

**Tests:** DB commit rồi process chết trước publish; consumer chết trước/sau ACK; Redis restart/mất stream, duplicate event, thiếu OrderCreated nhưng Delivered tới trước; poison payload; version gap; cross-tenant; reconciliation retry; không hoàn tất ticket Cancelled/Served sai. Cần kiểm tra lỗi business recovery riêng với test chỉ chứng minh Pub/Sub bị mất event.

### Thứ tự triển khai và rollback đề xuất

1. Duyệt contract/version và test harness cô lập; không áp dụng schema vận hành.
2. Durable outbox/inbox/recovery trước khi dựa vào change request hoặc line-ready handshake. Backfill không tự phát lại trạng thái thiếu căn cứ.
3. Additive correlation/task/claim; chỉ enable PerLineV1 ở test/chi nhánh được duyệt sau Web compatibility review.
4. Change-request phi tài chính; cancellation/quantity và occupied-session merge chờ policy riêng.

Rollback mặc định: tắt tạo order opt-in và worker mới, giữ dữ liệu/audit/outbox, drain hoặc xử lý thủ công task đang claim. Không tự chuyển order PerLineV1 về WholeOrder nếu đã giao một phần. Không DROP bảng mới hay xóa ledger để “rollback”; review SQL downgrade riêng sau backup. Các migration/schema/API trên chỉ là đề xuất, chưa được thực thi.
