# Cup Serving — conditional approval review và foundation increment

**Cập nhật 2026-11-07:** tài liệu này lưu kết quả foundation ban đầu. Tiến độ mới, API V2, HTTP commands/projection, migration đã chạy riêng trên cluster test và các gate còn lại ở [CUP_SERVING_V2_IMPLEMENTATION.md](CUP_SERVING_V2_IMPLEMENTATION.md). Các câu “chưa map/chưa apply test” bên dưới là trạng thái lịch sử, không phải kết quả increment mới. Database vận hành vẫn chưa apply/enable.

2026-11-07. Tiếp tục thiết kế `ITEM_LEVEL_SERVING_AND_MULTI_STAFF_DESIGN.md` sau phê duyệt có điều kiện. **Chưa production-ready; chưa có public Claim/Pickup/Serve API hoặc create CupV1 hoạt động.** Lượt này làm phần nền độc lập với quyết định đóng phiên/Payment và chuẩn bị migration, không rollout feature.

## 1. Schema review: giảm 5+2 xuống 4+2

| Bảng đề xuất | Quyết định / lý do / reuse |
| --- | --- |
| Kitchen cup_tasks | Giữ: cần một row/quantity với identity ổn định, prep/serving/cycle/version/fence. Ticket item chỉ có quantity; sticker chỉ là label, không thể làm custody authority |
| Kitchen cup_claims | Giữ: active owner/lease và lịch sử các claim trước; ghi đè một assignee trên cup làm mất bằng chứng custody/fence. Unique partial index cả Reserved/Carrying/Overdue |
| Kitchen cup_operations | Giữ, **gộp audit**: một command thành công lưu actor/key/hash + version và JSON trước/sau/kết quả trong một immutable row, dùng cả replay lẫn audit. MVP single-cup command không cần bảng audit riêng |
| Kitchen cup_audit | **Không tạo**: trùng effect history với cup_operations + claim history. Không cần một row audit thứ hai cho cùng single-cup effect. Audit failed authorization dùng security logging riêng; không giả ghi là effect đã commit |
| Kitchen fulfillment_outbox | Giữ: Kitchen chưa có outbox để forward summary. Admission outbox nằm trong Ordering, không được ghi chéo DB; Redis Pub/Sub không có ACK/backlog đủ bền vững |
| Ordering order_fulfillment_projection | Giữ: summary/version/freshness đọc cục bộ, không gọi Payment/Order.Deliver/close session; không nhân đôi quyền ghi từng ly |
| Ordering fulfillment_receipts | Giữ: dedupe EventId, identity/hash và historical ACK. Chỉ giữ last version trên projection không phát hiện đầy đủ event ID bị dùng lại và không lưu được kết quả ACK cũ |

**Thực tế EF increment: 4 Kitchen + 2 Ordering = 6 bảng mới**, không tạo 7 bảng theo proposal ban đầu. Dữ liệu tên món/options/note tiếp tục tái sử dụng immutable KitchenTicketItem snapshot, không nhân đôi text trên mỗi cup. Source ItemId, ordinals và FK local được lưu trên task để kiểm tra correlation.

Reuse đã làm: ticket/items/stickers, source OrderItem ID, common Kitchen transaction lock; core yêu cầu receipt v2 trên bảng `order_admission_receipts` và source outbox v2 trên `ordering.kitchen_admission_outbox`. Đây mới là điều kiện bảo vệ core; **producer/receiver admission v2 chưa được nối**. Bảng receipt hiện hỗ trợ consumer key riêng nên không cần inbox admission mới. Checkpoint `kitchen_admission_reconcile_state` có thể dùng consumer riêng cho reconciliation v2/projection; reuse worker/checkpoint là thiết kế, chưa triển khai.

Không reuse `Order.IdempotencyKey` cho serving: key đó dành cho tạo Order. Không reuse AuditTrail gửi qua Pub/Sub làm audit bền vững. Không thêm FK Identity/Ordering↔Kitchen, không tạo lại unique tenant/order ticket hiện có.

`cup_operations` chống update/delete qua SaveChanges của Kitchen DbContext. Đây là application guard, không phải bảo vệ trước SQL DBA/ExecuteUpdate trực tiếp. Quyền SQL future phải SELECT/INSERT ledger, không UPDATE/DELETE. Chưa cấp/thu hồi quyền trên DB nào. Audit system worker/Manager decisions cần chốt actor/evidence policy trước khi nối; không ghi giả một Staff làm tác giả của system event.

## 2. Phần đã code và giới hạn

| Thành phần | Đã làm | Chưa làm / gate |
| --- | --- | --- |
| Contracts | FulfillmentModes, expectations, full-state snapshot/semantic SHA256 hash/ACK records | HTTP public/machine adapters chưa map; record không có nghĩa endpoint tồn tại |
| Identity ly | UUIDv5 theo tenant/order/OrderItemId/ordinal, namespace cố định, golden vector khác runtime | Chưa tự backfill legacy cup hoặc đổi ID/tem cũ |
| Domain | Prep Pending→Brewing→Ready; serving Waiting→Available→Reserved→Carrying→Served; safe release trước pickup, renew, owner/fence/version/cycle guard; timeout giữ Overdue/Review | TTL runtime chưa chọn; Manager resolve/reassign/reprepare và timeout worker chưa triển khai |
| Kitchen unit of work | Role/scope, order lock + key lock, DB time sau lock, reload; save cup+claim+audit/operation+summary outbox cùng transaction; ACK method return sau commit; replay trả current cup cùng kết quả gốc | Không DI register, không có public route; lease duration là server policy được truyền tường minh trong test, không request field/default policy ngầm |
| Correlation | Kiểm tra ID deterministic, ordinal đủ từng line/cả Order, ticket/item/tenant/branch và receipt v2 APPLIED trước commit effect | V2 canonical admission writer/dual-reader còn thiếu, core không chuyển ticket legacy thành CupV1 |
| Ticket summary | Chỉ complete khi tất cả cup Served, vẫn unpaid; partial served/remaining preparation không complete ticket | Không phát legacy OrderCompleted/payment event hoặc tự in tem |
| Ordering projection writer | Local source Order/mode/quantity/manifest proof; inbox+projection transaction, per-order/event locks; same Event replay, version conflict reject, full snapshot version-gap/superseded handling; không regress Served/final state | Machine auth receiver, sender lease/retry/ACK validation, reconciliation chưa wired |
| Public compatibility | DTO Order detail/list thêm optional fulfillmentMode; WholeOrder default; các guard ở mục 4 | Chưa cho public request tạo CupV1; remaining financial paths cần review, không bật mode |
| Mobile | Đọc mode additive; nút whole-order deliver chỉ có ở WholeOrder, unknown mode fail closed; không đổi Demo/Payment UI | Chưa có real per-cup Serving screen/claim adapter khi chưa có public API |

Capabilities Kitchen trả **cupServingVersion=0, claimVersion=0**, conditionalTicketUpdatesVersion cũ giữ 1. Không có flag/registration cho phép biến core hiện tại thành feature runtime chỉ bằng đổi env; không mở endpoint stub trả thành công. Không deploy/restart container.

## 3. Consistency và async retry/reconciliation

Nguồn chính: Ordering giữ mode + OrderItem/quantity/manifest; Kitchen giữ chuẩn bị/custody từng cup; Ordering projection là read model. Không đồng bộ bằng cách đọc trực tiếp bảng service khác, không lấy Order.Status hay PaymentStatus làm bằng chứng tất cả ly đã giao.

Core đã có hai transaction boundaries:

1. Kitchen: common tenant/order advisory lock → tenant/actor/key lock → role/scope/expectedVersion/cycle/owner/fence/deadline/hold → mutation → tăng ticket fulfillment_version → insert append-only operation và immutable snapshot outbox → SaveChanges/commit. Không giữ transaction khi gọi HTTP/Redis. Cùng key/cùng payload replay không tăng version/lease hoặc tạo claim/outbox thứ hai; khác payload trả 409. Claim quá hạn vẫn giữ active-claim constraint, không ai claim lại.
2. Ordering: order/event locks → kiểm tra local Order CupV1, không void, đúng quantity và source v2 hash → receipt/version checks → inbox + projection commit → ACK. Các payment/bill/session fields không bị sửa. Snapshot full-state và source version cho phép nhận version 8 sau version 1, sau đó version 2 chỉ Superseded, không ghi đè.

Phần **chưa triển khai nhưng thiết kế bắt buộc trước enable**:

- Outbox dispatcher claim batches bằng SKIP LOCKED/lease/fencing; đóng transaction trước HTTP. Chỉ ACKED khi nhận đúng event/tenant/branch/order/version/hash và receipt/projection đã commit. Lost ACK/network/timeout retry cùng payload/EventId, không tạo effect mới. Conflict identity/hash → NEEDS_REVIEW, không retry vô hạn như transient error.
- Machine identity/audience/scope riêng, không dùng Staff JWT hoặc đưa internal route qua Gateway. Consumer xác thực scope trước writer; core writer không phải authentication middleware.
- Reconciliation có durable checkpoint/consumer namespace, bounded sweep hết các Order CupV1 còn lưu, không cutoff tuổi; so source version/hash, phát lại latest immutable snapshot/outbox nếu target thiếu. Không rebuild payload khác hash cho cùng version. Checkpoint cũ admission v1 không được ghi đè.
- Redis chỉ wake-up/live notification. PostgreSQL/service restart không reset task/claim/cycle; Redis reconnect không đồng nghĩa durable snapshot đã phục hồi.
- Trước enable, mọi writer thay đổi global RecoveryHold/Manager decision/location của CupV1 cũng phải tăng aggregate version và có outbox/snapshot biểu diễn hold, không chỉ cup commands. Guard legacy hiện chỉ ghi hold để fail closed, chưa forward hold thành một phiên bản projection mới; không dùng projection hiện tại làm settlement/close permission. Contract hold/freshness và system audit principal cần chốt trong bước transport/worker, không được tự clear review khi nhận summary cũ.
- Nếu mất custody rows khi restore, hold và restore/review; không dùng aggregate servedCount để dựng lại ai đang cầm ly.

Persistence core đã có test InMemory; transaction atomicity, SQL lease/fencing và reconciliation sau crash trên PostgreSQL thật **NOT RUN**, không suy từ tên test hoặc unique-index metadata.

## 4. Web FE legacy guards và phần dừng đúng phạm vi

| Đường ghi/đọc | Trạng thái increment |
| --- | --- |
| POST whole-order deliver | Guard trước cả replay; CupV1 trả 409 order.cup_serving_required; không phát event hoặc ghi DeliveredAt |
| Staff CancelOrder unpaid | Domain guard CupV1 trước mutation/CloseIfSettled; không đổi hành vi WholeOrder |
| SwitchServiceType / Table Transfer | Domain + transfer precheck chặn CupV1, kể cả sau fulfilled trong foundation; không giả rằng đã support chuyển các cup đang mang |
| Kitchen whole-ticket status/remake | Endpoint/domain guard CupV1; old update không được bypass per-cup version/claim |
| Kitchen legacy Delivered/Completed/Cancelled/Transferred handler | Shared writer giữ recovery hold cho CupV1, không complete/reset custody. Không tự clear hold bằng aggregate event |
| Admission v1 gặp ticket CupV1 | Reject trước ApplySource/hash reuse; không clear hold, không convert hoặc tạo lại ticket |
| Legacy queue/batches | Chỉ WholeOrder, không cho Web cũ điều khiển CupV1 từ danh sách pha legacy |
| Order detail/list DTO | Thêm mode optional/default WholeOrder; không đổi field/type cũ |
| CupV1 real queue/cup endpoints | **Chưa mở**, vì chưa có admission v2/full policy. Không chuyển task hold vào queue legacy để lách gate |

Các route no-show/unfulfillable/exception/paid cancellation/customer cancellation/refund effects, session manual close/expiry và PaymentSucceeded→Kitchen eligibility path cần review chung với owner nghiệp vụ/Web trước enable. **Không sửa Payment handlers, Payment service, Bill/Refund hoặc CloseIfSettled trong increment này.** Không tuyên bố mọi legacy mutation đã được khóa đầy đủ. Những đường còn thiếu là deployment blocker; public CupV1 creation/API không được mở trước khi chốt chúng.

Web cũ giữ nguyên cho WholeOrder. Web thao tác trên **order opt-in CupV1** sẽ gặp 409 nếu gọi đường cũ đã được guard; đây là behavior change có chủ đích phải review, không gọi là hoàn toàn backward-compatible. Không có source Web/E2E thực được xác minh nên Web UI compatibility **UNVERIFIED**.

## 5. Transfer / Sticker / Recovery

- Transfer CupV1 hiện **fail closed**, không triển khai merge hoặc sửa mô hình Bill. Đề nghị MVP giữ block tới khi có location version + durable transfer và quy tắc cho cup Reserved/Carrying; nếu chỉ cho transfer sau fulfilled cũng cần duyệt contract/read proof, chưa tự mở trong code.
- Sticker correlation column nullable và unique local FK đã chuẩn bị, legacy rows/IDs giữ nguyên. **Chưa** có admission v2 gắn CupId/sticker mapping hoặc v2 preview/print. GET sticker/preview legacy reject CupV1, không tự tạo tem thiếu hoặc claim đã in. Recovery không được enqueue/in lại tem. Hardware ACK/reprint audit vẫn ngoài phạm vi.
- Admission v1 payload/hash/consumer giữ nguyên. Code chỉ thêm guard đối với ticket đã có CupV1. V2 phải có mode marker/OrderItemId/structured options, hash/protocol/consumer riêng, single writer tạo ticket+cup+sticker+receipt trước ACK. Tất cả Kitchen replicas dual-reader/guards trước capture v2; Redis legacy không được chạy đua tạo ticket WholeOrder cho cùng Order CupV1.
- Current EF fields không tạo public opt-in: Source Order/ticket legacy default WholeOrder, không chuyển dữ liệu lịch sử hoặc dựng Served/claim bằng tên món/CupIndex.

## 6. Blocker Dine-in: chỉ phương án, chưa sửa Payment/Bill

Source `TableSessions.CloseIfSettledAsync` chỉ xét trạng thái paid/refunded; PaymentSucceededHandler gọi helper đó. Manual close/idle-expiry cũng cần audit. Một phiên đóng trước khi cup Carrying xong có thể khiến bàn được tái sử dụng; giữ tableCode trên cup không đủ bảo vệ giao đúng lượt khách.

| Phương án | Đánh giá |
| --- | --- |
| **A — Đề nghị:** session chỉ được close khi financial settlement hợp lệ **và** operational fulfillment đã hoàn tất | Dùng local Ordering projection/proof đúng version, thiếu/chậm proof thì giữ Open/ClosePending. Không sửa tính tiền, nhưng thay đổi trigger/điều kiện close có ảnh hưởng payment-driven flow nên **cần duyệt riêng** |
| B — Tách sitting/occupancy phục vụ khỏi financial session | Có thể giữ bill closed mà sitting chưa kết thúc, nhưng thêm schema/session identity/occupancy membership và coordination; không phải MVP nhỏ, cần quyết định kiến trúc/Bill policy |
| C — Chỉ phát triển/test nonfinancial core và chưa rollout DineIn | **Áp dụng hiện tại**; không sửa tài chính để vượt blocker. Takeaway vẫn cần admission/eligibility/RBAC/policy/E2E riêng trước runtime |

Nếu duyệt A: phải dùng một protocol lock/session version cho add-more và **mọi** nguồn close (manual, paid helper, expiry), chống add Order sau khi xác nhận close. Projection thiếu/stale không được coi là đã giao. Late ACK/retry/reconciliation chỉ đề xuất close khi có proof đầy đủ; không auto-reopen session đã đóng hoặc gắn cup cũ vào session mới. Thứ tự table/session/order locks cần audit để không deadlock. Đây là task proposal, chưa code hoặc migration thêm session fields.

Chưa chốt policy sync legacy Order.Completed/DeliveredAt sau Cup Fulfilled. Projection hiện độc lập, không gọi Order.Deliver vì có nhánh dựa IsPaid và có thể race với payment handlers. UI tương lai phải tách “Đã phục vụ” với “Đã thanh toán/đã đóng phiên”.

## 7. API contract, authorization và transitions

Runtime thực sự hiện có: các API legacy + GET Kitchen capabilities version 0 cho Cup. Các path Serving trong design vẫn **PROPOSED, chưa map**. Contract core (không public HTTP DTO) đã có:

- `CupCommand(CupId, Key, Action, ExpectedVersion, ExpectedCycle, ClaimId?, Fence?, ConfirmedAtBar)`; **không** nhận actor/owner/expiry từ request. Core lấy tenant/user/role/branch từ ICurrentUser. Lease duration là server policy tường minh, chưa default 120s trong runtime.
- Action Start/Ready: Barista/Manager. Claim/Pickup/Serve/Release/Renew: Cashier/Manager, owner enforced. BrandAdmin cùng tenant và BranchAccess; Customer/Guest/SuperAdmin không được mặc nhiên ghi cup.
- `CupEffect(OperationId, Replayed, Cup hiện tại, live Claim hiện tại, OriginalResult, ServerTimeUtc, TicketHoldReason)`; replay luôn kèm server clock và hold mới nhất, không tái kích hoạt quyền/lease cũ hoặc giấu ticket hold mới. Ledger result lưu actor role tại thời điểm effect. Release chỉ Reserved + owner + atBar confirmation + lease hợp lệ; Carrying/Overdue không release thường.
- `FulfillmentSnapshot`: scoped identity, source version, manifest hash, 6 bucket loại trừ nhau, total 1..4950, fulfilledAt chỉ khi served==total; SHA256 semantic hash không lấy EventId làm content identity.
- `FulfillmentAck`: EventId/Tenant/Branch/Order/Version/Hash/Outcome. Không có payment amount/bill mutation trong payload.

HTTP đề xuất sau duyệt: GET cups/detail/operation; PUT cup preparation; POST claim/renew/release/pickup/serve/issues/Manager resolve; GET Ordering fulfillment; internal POST snapshot + read reconciliation endpoint. `Idempotency-Key` bắt buộc, expected state/cycle/fence bắt buộc theo action; JWT/machine authentication adapters cần triển khai và test riêng trước map route.

States core: Pending→Brewing→Ready; Waiting→Available→Reserved→Carrying→Served. Timeout `MarkOverdue` giữ claim IsLive=true và cup Review; renew/serve/pickup bị chặn ngay khi deadline qua, kể cả worker chưa chạy. TTL/renew cadence, Manager evidence/resolution/reassign/reprepare, system audit principal và app-persistent pending intent **chưa chốt policy triển khai**; không tự chọn mặc định hoặc thêm API force success.

## 8. EF migrations và safety

Chuẩn bị mới, **chưa apply lên DB nào**:

- `20261106183333_KitchenCupServingFoundation`: 4 bảng, 6 cột additive, nullable source/sticker correlation; FK chỉ Kitchen.
- `20261106183340_OrderingCupFulfillmentFoundation`: 2 bảng, orders.fulfillment_mode default WholeOrder; FK chỉ Ordering.
- Model snapshots cập nhật; không sửa applied migration .cs. Offline `has-pending-model-changes` của hai context PASS.
- Bundle [migrations/cup-serving/README.md](migrations/cup-serving/README.md): preflight READ ONLY, generated Up SQL, SHA256SUMS; SQL không DROP/TRUNCATE/DELETE/UPDATE business rows, chỉ INSERT EF history.
- EF tools được ép connection **giả localhost:1/aicos_schema_review**, migrations false, JWT design-time ngẫu nhiên trong process; không tiếp xúc DB chính/test Windows hoặc tạo credential runtime.

EF Down có thể xóa ledger mới, **không chạy**. Safe rollback giữ expand schema và dữ liệu, tắt tạo CupV1 mới nhưng không biến cup Carrying/Served thành WholeOrder/release tự động. Current runtime chưa bật feature, không có data CupV1 vận hành được tạo trong lượt này.

**Binary mới map cột mới dù capability version 0. Không chạy binary này vào DB chưa expand.** Không deploy/restart ở lượt này. Lock duration, migration syntax/apply trên PostgreSQL thật, schema drift/current history/ACL phải preflight sau phê duyệt apply; offline EF metadata không chứng nhận các phần đó.

## 9. Integration test plan — isolated only

Chưa chạy PostgreSQL/Redis mới vì bước này chuẩn bị migration và test plan; không suy phê duyệt test/schema recovery cũ thành quyền chạy migration Serving. Dùng guard đã có làm nền, cần duyệt/apply schema test mới riêng trước execution.

Môi trường đề nghị: cluster PostgreSQL/Redis disposable mới, tmpfs, dedicated network/labels, random loopback ports và credentials test riêng; không dùng main DB/Windows test DB hoặc container vận hành. Mỗi lần phải xác minh container ID/system identifier/DB allowlist/port/mount/label trước schema/fixtures. Lưu snapshot ID/image/start/restart container cũ, không restart chúng. Không copy account hoặc dữ liệu thật.

| Stage | Nội dung | Tiêu chí nghiệm thu / trạng thái |
| --- | --- | --- |
| T0 schema | Full EF baseline + 2 foundation migrations trên cluster tạm, verify PK/FK/index/check/default/history; legacy mode và row fingerprints | PG apply/SQL syntax/locking **NOT RUN**; so tăng +4/+2 bảng và không mất row |
| T1 core PG | 2+ Npgsql contexts/process, cùng Cup claim; khác cup/Staff; stale/fence/deadline/release; ledger append-only; unique active Overdue | Chỉ 1 winner cùng cup; rows/audit/outbox nhất quán; pg_locks barrier, không chỉ đếm HTTP 200. **NOT RUN** |
| T2 crash/retry | Fail trước Save/sau Save/trước commit/sau commit; duplicate request/key misuse, response mất | Rollback toàn effect hoặc replay đúng effect, không tăng owner/fence/outbox lần hai. **NOT RUN** |
| T3 v2 admission | Kitchen offline, v1/v2 concurrent Redis/HTTP, missing/corrupt identity, source hash/options, repeated cups/tem | Cần producer/receiver v2 chưa có; **BLOCKED** |
| T4 async projection | Real machine HTTP, Ordering offline, lease expires/old ACK fence, Redis restart, lost ACK, out-of-order/full snapshots/checkpoint resume | Cần transport/workers/auth adapters; chỉ writer core có test hiện tại. **BLOCKED** |
| T5 Manager | Timeout/quarantine, reassignment vs late owner, physical evidence Unknown, remake cycle | Chờ policy/implementation; **BLOCKED** |
| T6 Web/Mobile | Public API RBAC, no old-route bypass, Cup UI/loading/offline/app kill, 360/393/430/large font | Legacy fail-closed đã mock-test; real Cup HTTP/UI và Web E2E **NOT RUN** |
| T7 Dine-in | Paid-before-serve, close vs add-more/serve/expiry, table reused, transfer held/carrying | Chờ decision mục 6, không thay Payment/Bill để chạy; **BLOCKED** |

Existing operations runner `-Run` dùng EnsureCreated và recovery runner `-Run -Recovery` migrate latest EF chain vào test cluster. Với source mới, latest chain đã gồm foundation; **chưa chạy hai runner trong lượt này**, không coi kết quả 9/18 lịch sử là test schema Serving. Cần Serving execution mode/filter riêng hoặc explicit schema approval rồi mới chạy. Phân loại TestServer/fake clients/test-signed JWT, PG thật, Redis thật, process crash và hardware/Web riêng. Máy in thật **NOT TESTED**.

## 10. Test results thực tế và files

| Kiểm tra lượt này | Kết quả / giới hạn |
| --- | --- |
| Backend final regression | **482 PASS / 0 FAIL / 0 SKIP**, gồm 21 tests mới domain/core InMemory/HTTP guards/offline migration metadata; exclude PG infrastructure/recovery/CashPostgres classes để không giả chạy chúng |
| BuildingBlocks | **15 PASS / 0 SKIP**, unit tests |
| Flutter targeted | **15 PASS**, gồm 2 mode guards và các mock/widget staff-flow tests; có overlap với full suite |
| Flutter full | **159 PASS**, Demo/API mock/widget; không phải real per-cup E2E |
| Flutter analyze / format | Analyzer **0 issues**; formatter **199 files, 0 changed** |
| EF generation / model | 2 migration + SQL sinh thành công, both contexts no pending model changes; metadata test chỉ cho AddColumn/CreateTable/CreateIndex/AddFK/AddCheck, FK cùng schema, không recreate old ticket unique index |
| SQL/preflight | Quét tĩnh Up/destructive/scope/checksum; **chưa chạy preflight hoặc SQL trên PostgreSQL** |
| New PostgreSQL/Redis / rollout | **NOT RUN**; không có new schema/data/runtime credential/container changes |

Lịch sử lỗi test mới: 18/20 PASS ở lần đầu (assert OnlyContain không chấp nhận FK collection rỗng của outbox; fixture kiểm FinalAmount sau reload chưa Include Items nên tính 0). Sửa assertion/fixture, không sửa logic tài chính. Chạy lại intermediate 19/20 rồi final 482 PASS sau source proof hardening + golden vector. Build còn 2 warnings cũ CS0628 WorkforceReviewTests / CS8620 OrderTests; không sửa ngoài scope.

Source chính:

- Contracts `Serving/CupIdentity.cs`, `CupServingContracts.cs`.
- Kitchen `Features/CupServing/{CupTask,CupClaim,CupOperation,FulfillmentOutbox,CupServingUnitOfWork}.cs`, `Persistence/CupServing/CupServingConfigurations.cs`, DbContext/domain/legacy guards.
- Ordering `Features/Fulfillment/{OrderFulfillmentProjection,FulfillmentProjectionWriter}.cs`, DbContext/mode/DTO/domain/delivery/transfer guards.
- Tests `CupServingFoundationTests.cs`, `CupServingGuardTests.cs`, `CupServingPersistenceTests.cs`. TRX `%TEMP%/aicos-cup-serving-regression-final/cup-serving-regression.trx`.
- Mobile `MainOrder.fulfillmentMode/canDeliver` và `main_fulfillment_mode_test.dart`; không tạo UI mới báo claim giả.
- New EF migrations/designer/model snapshots và review bundle; các thay đổi cũ từ lượt trước được giữ nguyên, không commit/push.

## 11. Quyết định/gate tiếp theo

1. Duyệt schema rút gọn **4+2** và SQL/checksum trước apply; cho phép test migrations trên cluster disposable mới nếu muốn chạy T0–T2. Không mặc nhiên cho phép DB chính.
2. Chốt TTL/cadence + Manager evidence/resolve/reassign/reprepare và system audit identity; sau đó mới map public command/worker/API/UI. Timeout không auto-release là invariant đã giữ.
3. Hoàn thiện admission v2 + machine projection transport/reconciliation, rồi real PG/Redis E2E. Không bật CupV1 trước dual-writer/reader guards.
4. Review financial/legacy Web mutation paths còn lại và chọn session policy A/B; **DineIn rollout dừng tới khi được duyệt và test**, không tự sửa Payment/Bill trong increment.

Trạng thái chung: **PARTIAL — FOUNDATION IMPLEMENTED, PUBLIC FLOW/REAL E2E/PRODUCTION BLOCKED**. Không tạo order/claim thật, không enable feature, không deploy hoặc apply migration lên database chính.
