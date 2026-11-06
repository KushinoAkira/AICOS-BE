# Staff Mobile — main Backend synchronization

## Kết quả cuối — bổ sung settlement API chỉ đọc

- Mới: GET /api/v1/payments/orders/{orderId}/settlement (Staff). Implementation tách
  trong Features/Payments/OrderSettlementEndpoints.cs, đăng ký tại Payments/Program.cs.
  Đọc DB Payments và gọi Ordering HTTP bằng caller JWT; không truy cập bảng dịch vụ khác.
- Phân biệt AwaitingPayment/Pending/AwaitingOrderSync/ReconciliationRequired/Settled.
  AdvisoryOnly=true: snapshot không nguyên tử xuyên service, không phải quyền đóng
  session; không dùng để thu thêm hoặc bật Cash/QR. GET không expire, query provider,
  SaveChanges hoặc publish event. Không cần migration/schema mới.
- Mobile có MainSettlement model và repository adapter; hiển thị đối chiếu sau
  order/history. Backend cũ 404 vẫn xem đơn; lỗi 403 chỉ báo phần đối soát, giữ session.
- Tests mới: PaymentSettlementTests.cs có 8 cases (refund pending, void trạng thái,
  thu trùng, pending quá hạn không bị ghi, chờ Ordering sync và authorization).
- Kết quả cuối thực chạy: Backend full 446 PASS, 0 FAIL, 0 SKIP; Mobile full 95 PASS,
  0 FAIL, 0 SKIP; formatter PASS; analyzer 0 issues. Hai warning cũ CS0628/CS8620.
  Các số 438/92/7 bên dưới là các lần kiểm tra trung gian trong cùng lượt.
- Chưa deploy source mới; không mutation test PostgreSQL/Redis hoặc provider thật.
  Không migration/seed/account thật, thay credentials, restart container, commit/push.

## 2026-11-03 — Audit API theo chức năng và contract corrections

Báo cáo: [API_MOBILE_FUNCTION_REVIEW.md](API_MOBILE_FUNCTION_REVIEW.md).

- Giữ API Table/Menu/Kitchen hiện có; Mobile refresh GET table thay snapshot cũ và
  hiển thị options/note Kitchen vốn đã có trong DTO. Không tạo endpoint trùng.
- Ordering OrderSummary thêm optional DeliveredAtUtc; QrBillOrder thêm optional
  CashierId/CustomerId/ReadyAtUtc/DeliveredAtUtc. Giữ enum Ready cho đơn giao nhưng
  chưa trả tiền; Mobile phân biệt trạng thái giao và thanh toán.
- PaymentResponse thêm optional CreatedByUserId/CashierUserId từ entity hiện có.
  OrderChecks chặn NoShow/Unfulfillable qua cùng guard của Cash và Online; giữ
  HTTP 422 + payment.order_cancelled. Không thay request hay URL cũ.
- Tests mới trong MobileCounterTests và PaymentOrderTests. 17 test tập trung PASS;
  toàn bộ AICOS.Services.IntegrationTests: 438 PASS, 0 FAIL, 0 SKIP. Compile trong
  dotnet test PASS; warnings cũ CS0628 WorkforceReviewTests, CS8620 OrderTests.
- WebApplicationFactory HTTP + EF InMemory + fake service clients/event bus và JWT
  tự ký test; chưa phải PostgreSQL/Redis integration, race test hoặc provider thật.
- Lượt này không deploy/restart, gọi business API thật, seed, migration, thay DB/
  ACL/credentials, commit/push. Không sửa Entity mapping hoặc DbContext.
- Mobile formatter/analyze PASS; full 92 tests PASS trước tách Kitchen card;
  sau tách chạy 7 contract/widget tests liên quan PASS, không gọi là full 93 run.
- Các phần dưới là lịch sử lần tích hợp trước. Runtime main đã được chủ dự án bật
  và smoke read-only ở lần mở điện thoại; mốc UNREACHABLE phía dưới là lịch sử.
  Các field DTO mới trong lượt audit này vẫn chưa deploy vào runtime.


Ngày: 2026-11-03. Code Backend nằm trong AICOS-BE. Trạng thái: đã build/test contract,
chưa triển khai runtime hoặc kiểm thử E2E PostgreSQL/Redis/thiết bị thật.

## Các thay đổi đã code (không thay đổi schema)

| File | Thay đổi |
| --- | --- |
| src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderContracts.cs | CreateOrderRequest thêm TableQrToken optional; OrderSummary thêm CashierId/TableId optional |
| src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs | GET staff/capabilities; lọc tableId/type trước paging; counter request replay và session attachment |
| src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/CounterOrderRequests.cs | Helper riêng cho PostgreSQL advisory transaction lock, payload replay validation và Table HTTP contract |
| src/Services/Ordering/AICOS.Ordering.Api/Domain/Entities/Order.cs | Ghi counter request key bằng cột IdempotencyKey hiện có |
| src/Services/Ordering/AICOS.Ordering.Api/Features/Qr/QrOrderEndpoints.cs | Manual close và idle expiry chặn còn nợ; manual close chỉ Counter roles |
| tests/AICOS.Services.IntegrationTests/MobileCounterTests.cs | 5 HTTP contract integration tests mới, EF InMemory/fake services |
| tests/AICOS.Services.IntegrationTests/QrOrderTests.cs | Cập nhật case đóng bàn: unpaid trả 409, đóng sau khi xử lý các round |

Không sửa Entity property mapping, DbContext configuration, migration, Docker/database
configuration, JWT secrets, role credentials hoặc ACL. Không tạo service mới hoặc truy cập
trực tiếp bảng của service khác. Dotnet build tạo lại bin/obj bị ignore sau lần cleanup trước.

## API contract bổ sung

- GET /api/v1/orders/staff/capabilities: Staff. Response gồm counterIdempotency,
  counterTableSessions, tableOrderFilter = true. Không có dữ liệu nghiệp vụ/secret.
- GET /api/v1/orders?branchId=...&tableId=...&type=DineIn&offset=0&limit=30&includePaid=true.
  Type là DineIn/Takeaway/Delivery của Backend; Mobile chỉ tạo DineIn/Takeaway.
  Các filter luôn nằm bên trong tenant/branch scope, trước paging.
- POST /api/v1/orders nhận header Idempotency-Key tối đa 80 ký tự. Body main vẫn lấy
  productId/quantity/note/optionIds; giá/tên được Menu server quyết định. Không nhận giá client.
- DineIn từ Mobile gửi tableId/tableNumber/tableQrToken. Ordering dùng ITableQrClient
  hiện có để đối chiếu tenant, branch, table, table code và CanOrder. Không mượn token guest
  để thực hiện thao tác staff. Lookup Table qua HTTP; ghi session/order chỉ ở Ordering.
- Counter có QR reference sẽ mở hoặc dùng chung open session của table; QR scan sau đó
  cùng tham gia session đó. Client cũ không gửi QR reference được giữ tương thích, chưa có
  bảo đảm tự attach session; không backfill lịch sử vào phiên mới.
- POST /api/v1/orders/qr/sessions/{id}/close chỉ Cashier/BranchManager/BrandAdmin.
  Round không bị void mà Unpaid/PartiallyPaid làm API trả 409 qr.session_unsettled.
  Idle expiry cũng bỏ qua những session này. Đây là kiểm tra projection Ordering, không
  thay thế một settlement contract xác minh trực tiếp Payments khi cần final financial close.

## Idempotency và giới hạn concurrency

Counter requests có cùng tenant/cashier/key được serialize bằng pg_advisory_xact_lock
trong transaction ReadCommitted; replay query sau lock và trả order cũ khi payload phù hợp.
Khác payload trả 409 order.request_key_conflict. Lock theo table bổ sung cho các POS key khác
nhau; unique open-session index hiện có vẫn bảo vệ đường QR scan.

Dùng cột IdempotencyKey đã tồn tại. Unique index hiện tại có CustomerId nullable không
đủ bảo vệ counter order nên không giả định index đó tự chống trùng. Chưa thêm index hay
migration. Advisory-lock path chỉ chạy PostgreSQL khi app gọi POST; không được chạy trong
EF InMemory tests và chưa được kiểm thử race bằng PostgreSQL thật trong lượt này.

Giới hạn cần xử lý trước khi chứng nhận vận hành:

- Hoàn thiện PostgreSQL tests cho replay song song, khác payload, nhiều cashier/tenant,
  mở bàn đồng thời với QR scan, close/idle/checkout race, timeout và rollback transaction.
- Không khẳng định mọi đường session close đã an toàn concurrency: các guard hiện tại
  ngăn lỗi tuần tự, còn cần locking protocol chung giữa POS/QR/close/worker và stress test.
- Replay payload hiện đối chiếu một số field của order hiện tại; order đã đổi service/payment
  state có thể trả conflict thay vì replay thành công. Không tạo order thứ hai trong trường hợp đó.
- Không có durable outbox/inbox. Save thành công nhưng Redis publish lỗi có thể làm Kitchen
  chưa nhận ticket; không hứa exactly-once hay tự phát lại event trên mọi request replay.
- Cần cập nhật toàn bộ replica Ordering trước khi mở client mutation; capability check không
  bảo vệ một rollout trộn node mới và node cũ sau load balancer.
- Client giữ request key trong RAM/draft cho cùng payload; app restart mất key. Phải đọc lại
  danh sách order khi kết quả gửi trước đó chưa rõ, trước khi tạo một ý định mới.

## Đối chiếu Mobile và phần chưa đồng bộ

Main Mobile đã có adapter/code cho Identity, branch, floor-plan, session bill, staff menu,
option IDs/cardinality/dependency, order create/list/detail, kitchen queue/status, handover,
GET payment list và GET notifications. Demo Mode, theme và các widget chính được giữ riêng.

Chưa hoàn thiện: toàn bộ dashboard aggregate, user display name theo quyền Cashier,
claim/release serving theo từng món, move/merge/split/adjust bill, item change approvals,
Cleaning state, printer confirmation, staff shifts và notifications read/paging.

Payment mutations trên Mobile đang tắt. Cash hiện thiếu chứng nhận race cash-vs-online/webhook;
provider VietQR/Momo/ZaloPay hiện dùng simulated gateway. Chưa có API manual QR confirmation
với bằng chứng nhận tiền tương đương yêu cầu cũ. Không dùng ảnh QR hoặc simulation làm bằng
chứng nhận tiền. Không thay đổi financial policy hoặc bảng Payment trong lượt này.

## Kiểm thử thực sự đã chạy

1. dotnet build src/Services/Ordering/AICOS.Ordering.Api/AICOS.Ordering.Api.csproj
   --ignore-failed-sources -v quiet: PASS, 3 projects, 0 errors, 0 warnings.
2. dotnet test tests/AICOS.Services.IntegrationTests/AICOS.Services.IntegrationTests.csproj
   --no-restore --filter "FullyQualifiedName~MobileCounterTests|FullyQualifiedName~QrOrderTests|FullyQualifiedName~OrderTests|FullyQualifiedName~OrderOptionTests|FullyQualifiedName~OrderClosingTests|FullyQualifiedName~OrderTrackingTests|FullyQualifiedName~KitchenTests|FullyQualifiedName~KitchenRemakeTests|FullyQualifiedName~MenuTests"
   --verbosity quiet --logger "console;verbosity=quiet": **170 PASS, 0 FAIL, 0 SKIP**.
3. Trong đó MobileCounterTests: 5 PASS. Kiểm tra replay, khác payload, nhiều lượt POS + QR
   chung bill, table/type filter, tenant mismatch, Barista read/create/close authorization,
   và idle session còn nợ không bị đóng.

Tests sử dụng WebApplicationFactory HTTP + EF InMemory, fake service clients/event bus/cache
và JWT tự ký test. Không phải PostgreSQL/Redis integration tests hoặc login Identity thật.
Không khởi động service production, không tạo account/JWT/fixtures ở database thật.

Hai warning có sẵn trong test project: CS0628 ở WorkforceReviewTests.cs và CS8620 ở
OrderTests.cs; không phát sinh warning trong Ordering build riêng. Ban đầu --no-restore
không có assets do cleanup bin/obj; đã restore/build rồi kiểm thử thành công.

Runtime read-only: Gateway http://127.0.0.1:8081/health/live UNREACHABLE; Docker Engine
không có pipe dockerDesktopLinuxEngine, kiểm tra ngoài sandbox cũng thất bại. Không khởi
động/recreate Docker stack, migration, seed hoặc kết nối ghi PostgreSQL trong lượt này.

## Trình tự chạy tiếp (chưa thực thi)

1. Bật Docker Desktop/Linux Engine và xác minh đúng project/containers main.
2. Review diff; xác nhận cấu hình main hiện có vẫn MigrateOnStartup=false, seed off.
3. Chỉ build image Ordering, chưa khởi động bằng bare docker compose mặc định:

```powershell
cd D:\\ki9\\sep490\\Sep490\\AICOS-BE
docker compose -f docker-compose.yml -f docker-compose.main-runtime.yml build order
```

4. Sau khi duyệt runtime rollout và rủi ro workers, deploy riêng Ordering. Lệnh dự kiến
   (chưa chạy): docker compose -f docker-compose.yml -f docker-compose.main-runtime.yml
   up -d --no-deps --no-build order. Không dùng -v, down, prune hoặc auto migration.
5. Health/metadata/401 checks trước; dùng account thật thuộc môi trường đã duyệt cho GET
   floor-plan/menu/orders/kitchen. Không gửi credentials qua chat hoặc log.
6. Xin phê duyệt môi trường PostgreSQL/Redis E2E riêng, schema và fixtures tối thiểu trước
   DDL/seed/mutation test. Không tái sử dụng database chính hoặc PostgreSQL test Windows cũ.
7. Hoàn thiện concurrency/event recovery/payment design, rồi mới bật mutation tài chính
   và chứng nhận full flow. Không tự chuyển sang migration hay tạo account trên main.

## Rollback

Chưa deploy nên không có DB/runtime rollback cần chạy. Đã không thay đổi schema hoặc dữ liệu
PostgreSQL. Nếu rollout sau này có lỗi, dừng Mobile mutation, giữ dữ liệu mới và quay image
Ordering về image đã review qua bước deploy được duyệt; không drop/reset hoặc xóa cột/key/order.
Không tự chạy git restore/reset toàn repo vì có thể mất thay đổi của người khác.


## 2026-11-05 — Staff Order operational scope; Payment paused

See [STAFF_ORDER_OPERATIONS_PROGRESS.md](STAFF_ORDER_OPERATIONS_PROGRESS.md).
Only operational delivery/session-intent work continued: additive Delivered event,
Kitchen consumer independent of payment, replay-safe whole-order deliver, optional
expectedSessionId and capability on Orders. POS relational transactions also run
without a request key. Mobile keeps session-scoped drafts and read-only foreground
refresh; main mutations never fall through to Demo controllers.

POST deliver retains URL/body/response and Counter authorization; a repeated
confirmation now returns 200 with the original delivery timestamp/actor instead of
409. Legacy Completed/payment/bill/close behavior stays unchanged. Review Web
callers before rollout; no Web repository/runtime E2E was available here.

Validation: 214 operational HTTP/InMemory tests PASS/0 SKIP; Identity staff display
1 PASS/0 SKIP. Flutter 146 PASS, API-mode login/branch 5 PASS, analyzer 0 issues.
These are mock/fake-bus/test-JWT component tests, not PostgreSQL race/Redis E2E.
Three new C# files whitespace-formatted; diff checks PASS.

No Payment service, settlement/refund/cash/QR-payment code or main database changed.
No migration/seed, Docker/runtime/deploy, credentials, commit or push. Per-item
preparation/serving/claim, durable audit/recovery and merge need reviewed persistence
or business contracts; they remain BLOCKED/MISSING, not completed with fake state.

## 2026-11-05 — Kitchen conditional transitions

Kitchen exposes `GET /api/v1/kitchen/staff/capabilities` and optional `expectedStatus` / `expectedRemakeCount` on the existing status PUT. Legacy Web requests remain accepted. Status/remake/delivered/cancelled/completed ticket writers share a PostgreSQL advisory transaction lock and reload ticket before mutation. Conditional replay of the same state is a no-op; stale views return 409. Mobile sends observed status/cycle, prevents duplicate ticket taps, refreshes on 409, and fails closed against an old server. No Payment/Bill or schema change.

Backend operational regression: 216 PASS, 0 FAIL, 0 SKIP; Flutter 149 PASS and analyzer 0 issues. Tests use HTTP/EF InMemory or Flutter mocks, not PostgreSQL/Redis E2E. Per-item serving, staff claims, submitted-order change approval, table merge, and durable Redis recovery remain BLOCKED pending reviewed persistence/business contracts. See `SERVING_MULTI_STAFF_AND_RECOVERY_DESIGN.md` and `STAFF_ORDER_OPERATIONS_PROGRESS.md`.
Mobile Kitchen 409 targeted tests: 10 PASS (after the 149-test full suite).

## 2026-11-05 — Real PostgreSQL/Redis staff-operations tests

Added `scripts/Test-StaffOperationsInfrastructure.ps1`, `OperationalTestTarget.cs`, `StaffOperationsInfrastructureTests.cs`; TestKit can opt into guarded real Npgsql + production Redis bus/subscriber. Existing mock tests retain their defaults. User explicitly approved EnsureCreated on disposable test-only schemas; no migration executed. Temporary container IDs/cluster/network/ports are guarded, data is tmpfs, no main/Windows/Gate D credentials or volumes, old container snapshots unchanged and scratch resources cleaned up.

Final infrastructure: 7 PASS, 0 FAIL, 0 SKIP with real pg_locks contention for concurrent Barista/delivery/create; stale/remake 409, expectedSessionId, unpaid Kitchen completion, lost-response retry and Redis restart characterization. Live reconnect PASS; automatic replay of OrderCreated while Kitchen offline FAILS (missing ticket). A characterization test passing is not a durable recovery success. Initial unconfigured discovery skipped 7; first actual run had 5 PASS/2 harness failures, corrected and rerun. Operational regression 216 PASS/0 SKIP; Flutter related tests 18 PASS, analyzer 0 issues. Identity/Menu/Table/JWT/notifier remain explicit doubles; real network timeout/multi-process/Web/production ACL tests NOT RUN.

Expanded MVP designs for per-line serving, claims, submitted-order change, occupied merge and durable recovery. No feature implementation, production schema, Payment/Bill, credentials, deployment or commit/push. See `STAFF_OPERATIONS_INTEGRATION_TEST_RESULTS.md`, `SERVING_MULTI_STAFF_AND_RECOVERY_DESIGN.md`, `STAFF_ORDER_OPERATIONS_PROGRESS.md`, and generated sanitized `STAFF_OPERATIONS_INFRASTRUCTURE_RUN.md`.
## 2026-11-05 — P0 Kitchen admission recovery design only

Traced commit/publish gap in POS/QR/pickup, non-durable Redis Pub/Sub and logged-only handler failures. Proposed MVP: Ordering transactional admission outbox + scoped internal HTTP ACK after Kitchen local commit + receipt/shared idempotent writer + full bounded reconciliation. Keeps legacy Redis and public FE APIs unchanged. Includes service-auth prerequisite, atomic capture/retry/lease/fencing, legacy/terminal recovery hold policy, schema, workers, acceptance tests, Web impact and rollback.

New design `KITCHEN_ORDER_ADMISSION_RECOVERY_P0_DESIGN.md`; review-only SQL under `docs/proposals/kitchen-recovery/` (3 new tables, 1 nullable hold column). No business code, EF migration, SQL execution, Docker/runtime, main DB, Payment/Bill, secrets, deploy or commit/push. Recovery remains FAIL from prior evidence; proposed implementation/SQL/acceptance NOT RUN. Await design/schema approval before coding.
- Validation design-only: static SQL/diff review PASS; SQL execution/migration/rollback/recovery acceptance NOT RUN. Flutter formatter 195 files unchanged, analyze 0 issues, 10 related repository mock tests PASS. These do not certify the proposed recovery implementation.

## 2026-11-06 — Local menu expanded to 50 real products

Added 22 products to the guarded aicos-cashier-local tenant via existing Menu APIs: total 30 drinks, 15 foods/cakes and 5 fixed-price combos across 6 categories. Published to all 3 branches (50 products each, changed=66); repeat initializer changed=0. Branch GET checks verify 50 unique available products, positive prices, 32 topping groups without a two-choice cap and valid required defaults. Options preview requires 0 changes.

Updated docs/fixtures/local-staff-menu.psd1 and scripts/Initialize-LocalStaffMenu.ps1; Expand-LocalStaffMenuOptions.ps1 uses fixture-derived product/topping counts. UTF-8 BOM retained for PowerShell 5.1; syntax/count/uniqueness validation PASS. No business API/schema/migration changes, no existing product/option overwrite. Existing PostgreSQL/Redis/Identity/Menu/Gateway containers were started with migrations=false. DPAPI credential never printed, inspection sessions logged out. Flutter analyzer 0 issues, formatter 195 files unchanged, food/combo tests 2 PASS. Actual API checks are separate from the mocked Flutter tests.

See docs/MENU_50_PRODUCTS.md for the catalog/prices and results. No Payment/Bill changes, deploy, commit or push.
## 2026-11-07 — P0 Kitchen admission recovery implemented, isolated tests PASS

Added service-owned transactional outbox/checkpoint, dedicated machine authentication + HTTP admission/lookup, receipt/idempotent shared legacy writer, dispatcher with lease/fencing/backoff, durable full-sweep reconciliation, and recovery hold guards. Legacy Redis/public API remain supported. Subscription startup now retries Redis outages without terminating HTTP; no change to Payment/Bill handlers. New features disabled by default, startup migrations false (including Ordering/Kitchen launch and Compose overrides).

New additive EF migrations 20261106164220_KitchenAdmissionOutbox and 20261106164227_KitchenAdmissionReceipts, snapshots and generated SQL/checksums are prepared. Applied only via guarded migrations on disposable PostgreSQL test databases. No operational DB, grants, credentials, deploy, commit/push, or Mobile edits.

Final infrastructure suite 18 PASS/0 FAIL/0 SKIP; actual process probes killed around persistence commit, real PostgreSQL/Redis + authenticated HTTP and automatic worker tests. 458 HTTP/InMemory regressions and 15 BuildingBlocks regressions PASS/0 SKIP. Source-specific doubles and remaining NOT RUN/BLOCKED gates are documented. Existing Docker container snapshots unchanged and only new temporary resources cleaned up.

Full report/config/API/file inventory and rollback: docs/KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md. Evidence: docs/KITCHEN_ADMISSION_RECOVERY_RUN.md. SQL for separate future approval: docs/migrations/kitchen-recovery/. Do not run the new Kitchen binary on an unexpanded operational schema even with recovery disabled: EF ticket queries map the hold column.