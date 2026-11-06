# P0 — Phục hồi Order đã lưu nhưng Kitchen thiếu ticket

> Cập nhật: sau khi người dùng phê duyệt triển khai, MVP đã được code và kiểm thử trên DB cô lập. Xem [KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md](KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md). Nội dung dưới đây giữ mốc thiết kế ban đầu; EF migration mới chưa áp dụng lên database vận hành.

Ngày 2026-11-05. **Thiết kế chờ duyệt; chưa sửa business code, chưa tạo EF migration, chưa chạy SQL.** Phạm vi whole-order hiện tại. SQL đề xuất nằm trong `AICOS-BE/docs/proposals/kitchen-recovery/`. Không bao gồm Payment/Bill, partial serving, claim hoặc thay đổi chính sách hủy.

## 1. Root cause và bằng chứng

| Source hiện tại | Hành vi | Khoảng mất dữ liệu |
| --- | --- | --- |
| Ordering `Features/Orders/OrderEndpoints.cs::CreateAsync` | SaveChanges + commit, sau đó mới `events.CreatedAsync` | Process chết giữa commit và publish: có Order, không có thông điệp bền vững |
| Ordering `Features/Qr/QrOrderEndpoints.cs::CheckoutAsync` | SaveChanges, sau đó publish | Cùng khoảng trống; cart Redis đã được take là rủi ro riêng, không sửa trong P0 này |
| Ordering `Features/Orders/PickupOrderEndpoints.cs::SaveWithinCapacityAsync` | Commit order trong transaction capacity, publish sau khi trả về | Cùng khoảng trống; cần phủ cả luồng Web pickup |
| `OrderSupport.cs::OrderEventPublisher.SafeAsync` | Log lỗi publish sau khi Order đã lưu | Không có retry ledger; create replay trả Order cũ, không bảo đảm phát lại OrderCreated |
| `RedisEventBus.PublishAsync` | Redis PUBLISH; số subscriber chỉ được log | 0 subscriber vẫn không lỗi; message không nằm trong hàng đợi |
| `RedisEventBusSubscriber` | Pub/Sub callback fire-and-forget; lỗi handler chỉ log | Không ACK/retry, consumer crash hoặc handler lỗi làm mất effect |
| Kitchen `OrderCreatedHandler` | AnyAsync rồi insert ticket | Replay tuần tự được bỏ qua; concurrent replay có thể cùng thấy chưa có ticket. Unique index chặn hai row nhưng request thua có thể lỗi và không có retry |
| Kitchen `KitchenOrderDeliveredHandler` | Không có ticket thì return | Delivered đến trước Created cũng không được giữ để áp dụng sau |

`StaffOperationsInfrastructureTests.Redis_restart_reconnects_but_does_not_recover_an_event_published_while_kitchen_is_offline` đã chạy trên PostgreSQL/Redis thật: Ordering commit khi Kitchen offline; sau Kitchen restart, ticket vẫn thiếu. Live event mới chạy lại sau Redis restart. Test characterization PASS xác nhận **recovery yêu cầu vẫn FAIL**. Xem `STAFF_OPERATIONS_INTEGRATION_TEST_RESULTS.md` và TRX được tham chiếu trong báo cáo đó.

Không phải lỗi DNS/reconnect. Redis Pub/Sub hiện được dùng như thông báo tức thời; DB commit và publish là hai hành động không atomic. Tăng retry connection, poll Mobile, hoặc chỉ thêm Inbox không lấp được khoảng trống này.

## 2. Phương án MVP được đề xuất

**Transactional outbox riêng cho admission Kitchen trong Ordering + HTTP giao nhận có ACK sau DB commit + idempotent admission trong Kitchen + reconciliation quét lại nguồn Order.** Redis Pub/Sub giữ nguyên cho consumer/FE hiện có, không là nguồn bảo đảm admission.

```mermaid
sequenceDiagram
    participant API as Ordering API
    participant ODB as Ordering PostgreSQL
    participant W as Admission Dispatcher
    participant K as Kitchen Internal API
    participant KDB as Kitchen PostgreSQL
    API->>ODB: Một transaction: Order + items + admission outbox
    ODB-->>API: Commit
    API-->>API: Publish legacy Redis như hiện tại
    W->>ODB: Claim batch ngắn, lease token, SKIP LOCKED
    W->>K: Ensure admission + immutable creation + fresh source facts
    K->>KDB: Lock tenant/order; ticket + items/stickers + receipt
    KDB-->>K: Commit
    K-->>W: ACK eventId, orderId, ticketId, result
    W->>ODB: ACKED nếu lease token còn khớp
    Note over W,KDB: Mất ACK: retry cùng eventId; không tạo ticket thứ hai
```

### So sánh các lựa chọn

| Phương án | Đánh giá |
| --- | --- |
| Chỉ retry Pub/Sub | Không đủ: publish có 0 subscriber vẫn thành công; process có thể chết trước publish |
| Chỉ Inbox/unique ticket | Chống trùng effect nhưng không tạo lại thông điệp đã mất |
| Chỉ reconciliation | Có thể sửa ticket thiếu nhưng không có ledger admission atomic, lỗi per-order khó theo dõi và phản hồi chậm |
| Outbox → Pub/Sub rồi đánh dấu Published | Vẫn không đủ: Published không chứng minh Kitchen commit |
| Outbox → Redis Streams + consumer group/inbox | Đủ nếu thiết kế ACK/persistence/replay chính xác; cần thêm stream lifecycle, AOF/retention và cơ chế phục hồi khi Redis mất dữ liệu |
| **Outbox → HTTP ACK + Inbox + reconciliation** | Chọn cho P0 MVP: hai service đã có HTTP host, PostgreSQL là nơi giữ retry; Redis restart không ảnh hưởng đường admission bền vững |

Không tạo service mới, không thay toàn bộ IEventBus và không biến mọi event hệ thống thành durable trong P0. Started/Ready, SignalR multi-replica và recovery tài chính vẫn là phạm vi khác.

## 3. Invariant và giới hạn bảo đảm

1. Mỗi Order commit qua mọi entry point hiện có phải có đúng một admission record `(tenantId, orderId, consumerKey)` trong **cùng transaction**. Rollback Order cũng rollback outbox; không enqueue sau commit.
2. EventId của record được sinh một lần trước lần save đầu, giữ qua execution-strategy retry. Immutable creation payload/fingerprint không đổi trên retry; mutable source facts không nằm trong fingerprint đó.
3. Kitchen giữ unique index **đã có** `(tenant_id, order_id)`. Không tạo lại index này. Cả Redis legacy, HTTP recovery và manual replay phải dùng chung admission writer và khóa `aicos:kitchen-operation:{tenant}:{order}`.
4. Receipt/inbox và ticket/items/stickers commit cùng transaction. ACK chỉ sau commit. Trùng eventId + payload khác trả 409 và cảnh báo; cùng business Order với eventId khác vẫn không được thêm ticket/line/sticker.
5. Outbox chỉ ACKED khi response đúng consumer/eventId/tenant/order và ticketId hợp lệ. Timeout, 5xx, mất kết nối đều giữ khả năng retry; không ACK vì Redis publish thành công.
6. Mọi writer dùng transaction retry strategy; lease có fencing token. Không giữ PostgreSQL lock/transaction khi chờ HTTP.
7. Với Order hợp lệ, hai PostgreSQL còn dữ liệu, source không bị xóa và dịch vụ cuối cùng hoạt động trở lại, retry + sweeps sẽ đưa Kitchen về trạng thái có ticket hợp lệ. Không hứa exactly-once transport hoặc phục hồi từ mất cả hai DB. Dữ liệu hỏng/không đủ bằng chứng phải hiện NEEDS_REVIEW, không giả ACK thành công.
8. Đây là eventual consistency. Snapshot có thể cũ ngay sau khi đọc; không bảo đảm atomic Cancel-vs-Brew hoặc Close-vs-Add xuyên service. Không quảng cáo P0 admission là lời giải cho các race đã BLOCKED khác.

## 4. Outbox và background workers

### Capture atomic ở Ordering

- Thêm `KitchenAdmissionOutboxWriter` vào pipeline SaveChanges của **OrderingDbContext**, chỉ xử lý Added Order; không mở generic hook ở tất cả microservices. Phủ POS, QR checkout và Web pickup, kể cả entry point thêm sau này.
- Tạo outbox và attach cùng DbContext trước base SaveChanges; existing transaction của POS/pickup được giữ, QR sử dụng transaction SaveChanges local. Không gọi HTTP/Redis bên trong save hook.
- Order entity sống qua retry phải giữ cùng admission EventId; nếu DbContext tracker clear rồi attach lại không sinh row thứ hai. Unique business key và kiểm tra fingerprint giải quyết commit ACK bị mất. Test bắt buộc cho trường hợp này.
- Immutable payload giữ identity và item snapshots; re-use cách ghép options/note của `OrderEventPublisher.BarNote` qua mapper chung, không sao chép giá/amount vào một luồng thanh toán mới. OrderType/table/pickup/CreatedAt thực tế là source facts đọc lại, không đưa vào hash immutable: source có API đổi service type và AuditableEntityInterceptor có thể stamp lại CreatedAt ở transaction retry. Không thay interceptor audit chung chỉ để giữ hash.
- Legacy `events.CreatedAsync` và event contract giữ nguyên cho Reporting/consumer cũ. Không đánh dấu outbox ACKED ở đó. Legacy eventId có thể khác outbox EventId; business-key dedupe trong Kitchen xử lý cả hai.

### Ordering `KitchenAdmissionDispatcher`

- Mặc định tắt cho tới khi schema/receiver/auth được duyệt và sẵn sàng.
- Claim ví dụ 50 record due bằng transaction ngắn `FOR UPDATE SKIP LOCKED`; đặt IN_FLIGHT, lease token UUID, lease_until 30 giây, tăng attempt. Lease hết hạn được claim lại. Endpoint timeout ví dụ 10 giây < lease; cấu hình cần test, không dùng số cố định như cam kết SLA.
- Ngoài transaction: đọc current Order snapshot từ **Ordering DB của chính mình**, gọi Kitchen internal endpoint qua HttpClient có timeout/cancellation. Không query bảng Kitchen.
- ACK/update retry phải `WHERE event_id AND state=IN_FLIGHT AND lease_token=token`. Worker cũ mất lease không ghi đè kết quả worker mới. Không nhận ACK chỉ có HTTP 200 nhưng thiếu ticketId hoặc scope sai.
- Retry exponential backoff + jitter, cap 60 giây; transient failure không bị bỏ vĩnh viễn sau N lần. 401/403/schema-version mismatch là deployment blocker có alert; invalid payload/business conflict chuyển NEEDS_REVIEW với mã lỗi an toàn, không lưu raw response/secret. Sau sửa lỗi có thao tác requeue được audit.
- Requeue thủ công hoặc giải quyết hold chưa được mở cho Staff trong MVP này; cần workflow/audit được duyệt riêng. Outbox/receipt giữ metadata kỹ thuật hiện tại, không được gọi là immutable audit history. Worker retry/reconcile tự động có correlation và structured logs, không dựa vào Redis AuditTrail để giữ bằng chứng duy nhất.
- Crash sau claim: lease hết hạn. Crash sau Kitchen commit trước ACK: gửi lại cùng EventId. Crash sau ACK: không cần publish lại admission. Không tự in thêm tem hoặc tạo effect khách hàng trên replay.

### Ordering `KitchenAdmissionReconciliationWorker`

- Quét **toàn bộ Order còn được lưu**, kể cả Order có trước rollout; không chỉ cửa sổ 24 giờ. Upsert outbox còn thiếu theo unique business key. Không gọi Menu để định giá lại; dùng snapshot đã lưu trong Order/items/options.
- Cursor bền vững theo UUID OrderId trong PostgreSQL, không dùng identity sequence hoặc CreatedAt làm high-water “đã xử lý vĩnh viễn”. Đầu mỗi sweep chụp upper OrderId; query keyset `(lastId, upperId]`, wrap khi hết. Insert ở trước cursor hoặc commit muộn được sweep kế tiếp nhìn thấy. Checkpoint commit **sau** upsert batch; crash trước checkpoint chỉ gây lặp an toàn.
- Việc quét gồm các tenant trong DB do Ordering sở hữu; HTTP probe/send chia theo tenant/branch với scope machine token tương ứng. Không lộ API GET liệt kê toàn bộ tenant cho nhân viên.
- Với outbox đã ACKED, probe Kitchen theo batch exact OrderId để phát hiện ticket thiếu hoặc cần cập nhật terminal facts. Requeue cùng admission EventId; receipt trong Kitchen chỉ dedupe phần tạo ticket, không bỏ qua xác minh ticket còn tồn tại và terminal facts mới.
- Requeue dùng conditional update ACKED → PENDING, xóa ACK metadata theo constraint và bỏ qua IN_FLIGHT; không ghi đè lease đang xử lý. Checkpoint lease và token cũng phải được fence, không chỉ dispatcher lease.
- Không xóa receipt hoặc reset cursor bừa để replay. Nếu receipt trỏ ticket không còn tồn tại, trả invariant failure/NEEDS_REVIEW và dùng repair flow được audit, không trả ACK giả. Mất/hỏng DB, restore lệch hai service là incident riêng cần phê duyệt.
- Đề xuất poll dispatcher 2 giây, probe/reconcile batch 100 với throttle. Mục tiêu test khi backlog nhỏ: admission xuất hiện trong 30 giây sau services ổn định. Với downtime/backlog lớn phải báo queue age/throughput, không hứa thời gian cố định.

## 5. Receiver, trạng thái và idempotency

Thêm `KitchenAdmissionService` dùng chung cho `OrderCreatedHandler` và internal API. Reuse `KitchenTicketWrites` để serialize create/status/remake/deliver/cancel/complete; hiện create chưa tham gia khóa này.

| Trường hợp | Xử lý |
| --- | --- |
| Source đồng thời có DeliveredAt và trạng thái void/xung đột scope | NEEDS_REVIEW/hold trước các nhánh bên dưới; không tự chọn Completed hay Cancelled |
| Chưa có ticket, Order Pending/Confirmed và không void/delivered | Tạo một ticket + items bằng snapshot đã lưu, giữ nguyên rule DineIn/Takeaway/Delivery hiện có |
| Takeaway chưa trả tiền | Ticket tồn tại nhưng vẫn không vào Bar queue theo `IsEligibleForBar`; không tự đánh dấu đã trả tiền |
| Order đã có DeliveredAt đáng tin cậy | Tạo ticket nếu thiếu và ConfirmDelivered trong cùng transaction, không đưa lại vào hàng chờ pha |
| Order Cancelled/NoShow/Unfulfillable | Tạo lịch sử ticket Cancelled nếu thiếu, không gọi hủy Order/đóng phiên/hoàn tiền |
| Source Completed nhưng thiếu DeliveredAt | Không suy luận đã giao từ thanh toán/Completed; tạo ticket có recovery hold để Manager kiểm tra |
| Source Preparing/Ready nhưng Kitchen ticket không tồn tại | Không có bằng chứng line đã được pha ở Kitchen; tạo Pending có recovery hold, không tự pha lại/đánh dấu Ready |
| Ticket đã tồn tại | Không append lại items, reset trạng thái, đổi actor/thời gian hoặc in lại tem; trả ticket cũ sau scope/fingerprint validation |
| Ticket đã Completed/Cancelled | Không hạ xuống Pending/Brewing; source facts xung đột thì NEEDS_REVIEW |
| Replay cùng eventId | Check immutable hash, ticket existence; không tạo lại effect. Terminal facts mới có thể áp dụng bằng guard idempotent hiện có, audit riêng lần thay đổi thực tế |

Mutable source facts (table display, delivered/void flags, observedAt) nằm ngoài immutable creation hash. Không dùng observedAt làm aggregate revision hoặc ghi đè state do Kitchen sở hữu. Ordering projection Preparing/Ready không được dùng để hạ Kitchen state. Cancel và Delivered đồng thời/xung đột không tự chọn bên thắng; giữ lịch sử và cảnh báo.

Đọc source Order/items/options trong một snapshot transaction ngắn nhất quán, đóng transaction trước HTTP. Dùng orderType và sourceCreatedAtUtc đang được lưu để khởi tạo ticket thiếu; đổi service type trên ticket đã tồn tại phải xác minh rule, nếu không tương thích thì hold/review thay vì tự sửa eligibility. Source facts cũ có thể gây lệch tạm thời tới sweep tiếp theo; loại bỏ hoàn toàn race cross-service với cancel/preparation cần reservation protocol riêng, chưa nằm trong P0.

`IsPaid` chỉ được đọc từ Order hiện có để khởi tạo ticket thiếu theo rule đang chạy; không tạo Payment, không đổi PaymentStatus, không ghi Bill. P0 không sửa handler Payment hoặc sửa projection IsPaid của ticket cũ nếu payment event thất lạc; trường hợp đó báo riêng. Reconciliation được phép áp dụng sự kiện terminal phi thanh toán theo guard hiện có, không gọi cancel/settlement endpoint.

Receipt status: APPLIED hoặc NEEDS_REVIEW. Ticket được hold có `recovery_hold_reason` nullable mới; queue/batches phải loại hold, API phục hồi trả 202/NEEDS_REVIEW để dispatcher không coi là admission hoàn tất. Manager xem lý do qua status API; chưa mở nút tự clear hold hoặc “đã pha” cho trường hợp thiếu bằng chứng. Đây là tình huống dữ liệu ngoại lệ, không phải đường tự động của OrderCreated bị mất thông thường.

Hold phải được kiểm tra cả ở status/remake/sticker endpoint và shared announce helper, không chỉ filter danh sách: gọi trực tiếp ticketId cũng không được bắt đầu pha/in tem (`409 ticket.recovery_review_required`). Payment handler hiện có giữ nguyên; shared operational announce guard ngăn ticket hold bị đưa vào Bar qua notification. Nếu source có DeliveredAt/void fact đủ tin cậy xuất hiện sau đó, áp dụng terminal guard, cập nhật receipt APPLIED và clear hold trong cùng transaction có audit; không tự clear hold từ source Preparing/Ready hoặc chỉ vì đã trả tiền.

Tạo ticket/items/stickers và receipt cùng transaction, rồi mới notifier. Không gọi helper hiện tại nếu helper SaveChanges/notify làm vỡ boundary. Crash trước notifier không làm mất ticket; GET queue/polling đọc trạng thái đã commit. Physical printing exactly-once và durable UI push nằm ngoài P0.

## 6. API/DTO đề xuất và bảo mật

Không xóa/đổi tên request/response public hiện có. Không mở internal routes qua Gateway/Web Swagger công khai.

Gateway phải được kiểm tra cả route catch-all để bảo đảm không forward `/internal/`; internal endpoint dùng authentication scheme riêng và bị exclude khỏi public OpenAPI. Machine principal không được mặc nhiên truy cập các API Staff; giữ Staff bearer scheme/policies hiện tại. Body không được tự chọn producer identity, producer_key lấy từ client_id đã xác thực.

| API mới | Contract tối thiểu | Caller |
| --- | --- | --- |
| POST `/internal/v1/kitchen/order-admissions` | eventId, schemaVersion=1, consumerKey, tenantId, branchId, orderId, immutableCreation, creationHash, currentSourceFacts | Ordering dispatcher duy nhất |
| POST `/internal/v1/kitchen/order-admissions/lookup` | tenantId, branchId, orderIds tối đa 100; read-only dù dùng POST để tránh query dài | Ordering reconciliation duy nhất |
| GET `/api/v1/kitchen/recovery/status` | scoped counts/oldestAge/holds, không trả raw payload hoặc secret | BranchManager/BrandAdmin, đúng tenant/branch |

`immutableCreation`: orderCode và item snapshots (productId/name/quantity/note gồm options), gắn với tenant/branch/order identity; không cần dữ liệu khách hàng hay tổng tiền. `currentSourceFacts` bắt buộc có sourceCreatedAtUtc, orderType, table number, pickup slot, read-only IsPaid, source status/void/delivered timestamps và observedAtUtc. `creationHash` là SHA-256 canonical semantic JSON của immutable identity/items, không gồm mutable source facts; không hash raw serialization vì thứ tự property/whitespace có thể đổi.

ACK 200/201: `{eventId, consumerKey, tenantId, branchId, orderId, ticketId, result: Created|AlreadyPresent, receiptStatus: Applied}` sau commit. 202 NeedsReview không được chuyển outbox thành ACKED. 409 cho eventId/hash hoặc dữ liệu tenant/order xung đột; 400 payload version/schema sai; 401/403 sai principal/scope; 5xx retry. Lookup trả từng OrderId requested với exists, ticketId, status, hold và applied receipt; không trả ID ngoài request.

Source chưa có cơ chế M2M rõ ràng cho hai service này. Cần bổ sung **authentication scheme riêng cho service**, issuer/audience/key riêng, scope `kitchen.admission.write` và `kitchen.admission.lookup`, principal client_id=ordering, token tenant/branch khớp payload. Không ký JWT giả Cashier, không nhận Staff JWT cho internal route, không hardcode key. Key/certificate qua secret store/environment, TLS và network restriction khi deploy; đây là deployment prerequisite, chưa tạo credentials ở lượt thiết kế.

Lookup POST chỉ đọc DB; test/write guard phải xét endpoint cụ thể thay vì mặc định mọi POST là business mutation. Dữ liệu invalid không được nới quyền để “tạo cho đủ ticket”.

## 7. Schema và migration đề xuất

| Service/schema theo source main | Bổ sung | Tác động |
| --- | --- | --- |
| Ordering / `ordering` | `kitchen_admission_outbox` | Stable event ID, unique business key, JSON snapshot/hash, retry/lease/ACK/error metadata |
| Ordering / `ordering` | `kitchen_admission_reconcile_state` | Persistent bounded sweep cursor và lease/fencing giữa replicas |
| Kitchen / `kitchen` | `order_admission_receipts` | Inbox/event hash + ticket reference + result, cùng transaction ticket |
| Kitchen / `kitchen` | `kitchen_tickets.recovery_hold_reason` nullable | Hold dữ liệu lịch sử không đủ bằng chứng; queue filter bắt buộc trước enable backfill |

Không thêm lại unique index Kitchen `(tenant_id, order_id)` đã có, không đổi enum/columns Order/Payment/Bill. Không giả định schema public của Backend cũ áp dụng cho repo main mới; target phải đối chiếu `OrderingService.Info.Schema` và `KitchenOperationsService.Info.Schema` cùng preflight DBA trước triển khai.

SQL review-only: `docs/proposals/kitchen-recovery/01_ordering_expand.sql`, `02_kitchen_expand.sql` trong AICOS-BE. Hai file tách transaction theo DB service, không có cross-service FK, DROP/TRUNCATE/DELETE/backfill/GRANT. Không tự dùng IF NOT EXISTS để che cấu trúc cùng tên khác loại; nếu object đề xuất đã có, preflight dừng để diff. Chưa thực thi hoặc chứng nhận SQL bằng PostgreSQL.

Khi được duyệt triển khai, tạo EF migrations riêng cho từng DbContext, đối chiếu model snapshot với SQL đã duyệt; không sửa migration đã chốt. Quyền tối thiểu cần bổ sung được trình riêng: Ordering SELECT/INSERT/UPDATE hai bảng mới; Kitchen SELECT/INSERT/UPDATE receipts và cột hold; không ownership/superuser/DELETE. Không chạy GRANT trong bước này.

## 8. File dự kiến thay đổi sau phê duyệt

| Vị trí | Thay đổi dự kiến |
| --- | --- |
| Ordering `Persistence/OrderingDbContext.cs` | Model outbox/checkpoint và hook capture Order Added atomically |
| Ordering `Features/KitchenAdmission/` (mới) | Payload mapper/canonical hash, outbox writer, scoped Kitchen client, dispatcher, reconciliation worker/checkpoint lease |
| Ordering `Program.cs`, config + launch/Compose test | DI/worker flags mặc định false, dedicated M2M client config, MigrateOnStartup=false |
| POS `OrderEndpoints.cs`, QR `QrOrderEndpoints.cs`, pickup `PickupOrderEndpoints.cs` | Kiểm tra/enforce capture/retry boundary ở tất cả create paths; giữ public API và legacy Redis publish |
| Kitchen `Features/KitchenEventHandlers.cs::OrderCreatedHandler` | Gọi shared admission service thay AnyAsync+insert không khóa |
| Kitchen `Features/KitchenAdmission/` (mới) | Internal endpoints, admission writer, inbox/hash, read-only lookup/status |
| Kitchen `KitchenTicketWrites.cs`, `Domain/Entities/KitchenTicket.cs`, DbContext | Reuse operation lock; nullable hold, local receipt config/transaction |
| Kitchen `KitchenEndpoints.cs`, `Program.cs` | Queue/batches exclude holds; receiver DI/auth, không đổi payment eligibility |
| Kitchen `KitchenTicketTransitions.cs`, shared announce/sticker guards | Chặn direct-ID thao tác trên hold, không sửa Payment handler |
| BuildingBlocks security hoặc service-local auth extension mới | Dedicated machine scheme, không thay validation Staff JWT hiện có |
| EF migrations + snapshots từng service | Additive, chỉ tạo sau duyệt; không áp dụng lên main tự động |
| Tests `StaffOperationsInfrastructureTests`, harness cô lập | Recovery success/fault-injection + concurrency; giữ test legacy chứng minh behavior cũ khi feature off |

RedisEventBus/Subscriber hiện tại không cần thay transport cho P0 này. Event contracts public và FE endpoint giữ nguyên. Web sẽ thấy ticket phục hồi qua queue hiện có; trạng thái hold quản trị thêm mới. Không có Web repo trong audit nên chưa chứng nhận Web regression PASS.

## 9. Rủi ro và rollback

- Recovered Order cũ có thể đã được pha thủ công: hold các trạng thái thiếu bằng chứng, không tự Ready/Served hoặc gọi in tem lần hai. Staff/Manager xử lý exception qua quy trình được duyệt.
- Backlog lớn gây tải DB/Kitchen: batch/index/throttle, metric oldest unacked, retry count, hold count, last sweep completed. Không bỏ order quá cũ để làm số liệu xanh.
- Khoảng trống rollout: thêm schema trước, receiver/auth/queue-hold filter trước capture+dispatcher; reconciliation bù Order commit trước capture. Không bật capture nếu outbox insert lỗi bị catch rồi vẫn cho Order commit.
- Transaction retry/hook tạo hai event, lease timeout, ACK mất và subscriber legacy cùng lúc: unique keys + shared lock + inbox + fencing; bắt buộc test thực trước deploy.
- Security: route nội bộ bị gọi bằng Staff JWT hoặc scope khác tenant phải bị chặn; không log payload/secret.
- `recovery_hold_reason` yêu cầu queue filter tương ứng. Khi rollback code cũ, ticket hold có thể lại lộ ra queue nếu receiver còn chạy. Vì vậy **không rollback binary mù khi còn hold**: dừng capture/dispatcher/reconcile, giữ receiver compatible hoặc drain/review holds trước; không clear hold hàng loạt.
- Rollback vận hành mặc định bằng feature flags; giữ outbox/receipt/checkpoint và dữ liệu để resume. Không DROP, xóa receipt hoặc reset database. Downgrade SQL chỉ đề xuất riêng sau backup, không có rollback destructive tự động.
- Nếu tắt durable recovery, hệ thống quay về rủi ro P0 cũ; phải báo degraded, không gọi đó là recovery thành công. Dừng nhận order mới khi không thể bảo đảm capture là quyết định vận hành cần duyệt riêng.

## 10. Bộ test nghiệm thu bắt buộc

Tất cả chạy trên PostgreSQL/Redis cô lập được guard; test schema/migration mới phải được duyệt riêng. API process crash tests cần host process riêng, không chỉ TestServer/DelegatingHandler.

| Test | Điều kiện PASS |
| --- | --- |
| Atomic capture POS/QR/pickup | Mỗi Order committed có 1 outbox; lỗi outbox làm rollback Order/items/session effect trong transaction tương ứng |
| Audit stamp/type change | Audit restamp ở retry không đổi immutable hash; service-type change không tạo ticket sai eligibility hoặc reset ticket cũ |
| Rollback và mất commit ACK | Retry không tạo Order/outbox thứ hai; payload/EventId giữ nguyên |
| Kill Ordering sau DB commit trước Redis/HTTP | Restart dispatcher tạo ticket cuối cùng mà không cần khách gọi lại create |
| Kitchen offline trước create, Redis event mất | Order vẫn lưu; khi Kitchen lên, đúng một ticket đúng items/options/note |
| Redis restart/mất toàn bộ Pub/Sub messages | Đường HTTP/outbox vẫn hoàn tất admission, không cần Redis để ACK |
| Crash Kitchen trước local commit | Không receipt APPLIED/ticket nửa vời; retry hoàn tất |
| Crash Kitchen sau commit trước response | Retry trả cùng ticketId; không thêm items/stickers/notification effect |
| Hai dispatcher + legacy Redis + reconciliation cùng Order | Có ít nhất hai DB writers chờ pg_locks; một ticket, receipt hợp lệ, không handler 500 do unique race |
| Lease hết hạn/worker cũ ACK trễ | Fencing chặn worker cũ ghi đè; worker mới replay an toàn |
| EventId trùng hash khác hoặc sai branch | 409/403, không thêm row/effect; chưa ACKED |
| Terminal-before-admission | Delivered tạo Completed, void tạo Cancelled, không vào queue; source Completed không có DeliveredAt chuyển hold |
| Reconcile legacy/no outbox | Phục hồi Order từ trước rollout, không giới hạn 24 giờ; restart checkpoint không mất row |
| Insert/commit sau cursor hoặc Guid trước cursor | Sweep kế tiếp nhìn thấy, không bị high-water bỏ qua mãi mãi |
| ACKED receipt nhưng ticket thiếu | Không ACK giả; invariant alarm/repair có kiểm soát, không duplicate partial rows |
| Preparing/Ready legacy thiếu ticket | Hold, không tự pha lại; không đổi Payment/Bill |
| Hold bypass | Direct-ID start/remake/sticker bị chặn; announce helper không đẩy hold vào Bar; terminal fact đáng tin cậy mới được clear hold idempotently |
| Poison payload/401/schema mismatch | Alert/NEEDS_REVIEW đúng scope; transient lỗi vẫn retry; không cắt sau N lần để coi hoàn tất |
| M2M auth | Staff JWT/Guest/unknown client/scope khác tenant bị chặn; machine đúng scope mới được nhận/lookup |
| Compatibility | Public Order/Kitchen API và legacy payload còn hợp lệ; 216 regression giữ PASS, Web contract tests bổ sung trước rollout |
| Recovery SLA test | Khi services ổn định, backlog nhỏ: các Order hợp lệ đều có ticket trong budget 30 giây; không có unacked lỗi bị im lặng |

**Trạng thái hiện tại:** root cause có bằng chứng từ lượt integration trước. Thiết kế/SQL/test mới: NOT RUN; recovery P0 vẫn FAIL cho tới khi implementation và nghiệm thu mới PASS. Không chạy lại infrastructure để tạo ấn tượng đã sửa lỗi khi chưa đổi code.

## 11. Thứ tự triển khai đề nghị

1. Duyệt kiến trúc HTTP ACK, SQL additive, hold policy, machine authentication và phạm vi test schema. Chưa duyệt production migration/deploy bằng việc duyệt thiết kế.
2. Viết migration/model + guard/preflight trên test riêng; kiểm tra SQL/checksum, không main DB.
3. Code receiver/shared idempotent writer/receipt và test legacy concurrency, flags off.
4. Code capture atomic phủ POS/QR/pickup; test rollback/commit ambiguity, giữ API/Redis legacy.
5. Code dispatcher lease/fencing/ACK/backoff, rồi checkpoint/reconciliation/backfill không giới hạn thời gian.
6. Chạy fault/process crash/Redis loss tests và acceptance recovery thật, phân biệt PASS/FAIL/NOT RUN/BLOCKED. Review Web/M2M ACL/metrics/rollback.
7. Trình riêng kế hoạch backup, migration theo service, quyền và rollout/canary để duyệt trước database vận hành. Không tự chuyển sang gate này.
