# Gate D — Kế hoạch kiểm thử tích hợp Backend API

Ngày audit: 2026-10-29 (Asia/Saigon, UTC+07).
Trạng thái: **D1-C / C1 PREPARATION PASS — CHỜ DUYỆT C2 CREDENTIALS; CHƯA TEST NGHIỆP VỤ**.

## Cập nhật D1-C / C1 — 2026-10-29

Đã có [kế hoạch D1-C](GATE_D1_C_PREPARATION.md) và [kết quả C1](GATE_D1_C_C1_RESULT.md).
Start riêng hai infra test sau phê duyệt, live READ ONLY cluster/schema/roles/ACL
đạt. Sau đó chuẩn bị apps-only Compose, Gateway chỉ route Identity, guards theo
stage và read-only preflight runner; 60 metadata/config guard cases PASS.
11 roles vẫn NOLOGIN/password NULL, Identity tables rỗng. Chưa runtime credentials,
fixture, app start, JWT hoặc business suite; C2–C6 cần phê duyệt riêng.
Historical StartedAt Gate C đã khác do đợt Docker resume trước đó; giữ evidence cũ,
C1 so sánh snapshot C0 mới và không gây restart/recreate hay thay env Gate C.

## Cập nhật D1-B — 2026-10-29

Đã thực thi schema-only và ACL trong cluster Docker Gate D đã pin, system identifier
7701931530273472557. Kết quả: 38 bảng/244 cột, 38 PK/61 FK/66 indexes; tất cả bảng
rỗng. 11 service roles NOLOGIN, không password, quyền khớp ma trận. Không cấp JWT,
fixture hay khởi động test app. [Kết quả và lịch sử lỗi/rollback](GATE_D1_B_RESULT.md).

Một attempt rollback hoàn toàn do verifier trùng alias; attempt kế tiếp COMMIT.
Tám CHECK text-array cast deparse khác PG18.4/18.6 đã review và xác minh bằng SELECT;
không sửa baseline SQL. PUBLIC TEMPORARY kế thừa vẫn tồn tại, không tự REVOKE PUBLIC.
D1-A readiness zero-table không còn áp dụng; dùng Invoke-GateDSchemaAcl.ps1 -Action
Verify với evidence D1-B. Chờ duyệt test credentials/LOGIN, Identity fixtures và
Identity/Gateway TEST/API business tests. Các phần D1-A/audit dưới đây là lịch sử,
không phải trạng thái hiện tại hoặc quyền chạy D2–D4.

Gate C đã được chủ dự án đóng với kết quả PARTIAL: runtime/routing smoke PASS,
chưa chứng minh đầy đủ nghiệp vụ. Tài liệu này không phê duyệt hoặc thực thi
bất kỳ D1–D4 nào. Database vận hành duy nhất vẫn là aicos_db, schema public.

## Cập nhật D1-A — 2026-10-29

D1-A đã được phê duyệt riêng và hoàn tất **PASS_INFRA_ONLY**.
[Xem kết quả thực tế](GATE_D1_A_RESULT.md). Chỉ tạo PostgreSQL 18.6 và Redis test,
network/volumes mới. Database test hiện 0 bảng và 0 service roles; chưa schema/
ACL/fixtures/account/JWT/business suite. D1-B và D2–D4 chưa được phê duyệt.

Transport đã xác minh: DockerExec pin container ID + cluster system identifier
7701931530273472557, gated-postgres:5432 trong network internal. Không publish
PostgreSQL/Redis host ports; mapping 15432 không hoạt động trên internal network
đã được bỏ. Không kết nối PostgreSQL Windows/main hoặc database test cũ.

Reserved signing key test được lưu mã hóa riêng, chưa dùng phát token. Schema
artifact sau Gate B đã pin 38/244 và checksum; SQL ACL/roles chỉ là review files.
D1-B cần review guarded restore/helper và nâng stage manifest; runner D1-A cố ý
chặn schema/app/network members ngoài hai container infra.

Phần audit/phương án bên dưới giữ bối cảnh ban đầu. Mọi bước D1-B/D2–D4 vẫn là
kế hoạch, không suy ra từ D1-A rằng được seed, tạo JWT hoặc test nghiệp vụ.

## 1. Phạm vi audit gốc và bằng chứng

Đã đọc toàn bộ 2.236 dòng của [báo cáo Gate C](GATE_C_EXECUTION_RESULT.md),
bao gồm lịch sử lỗi, pause/resume và kết quả Gateway mới nhất; dùng kết quả mới
nhất, không lấy checkpoint cũ BLOCKED/PAUSED làm trạng thái hiện tại.

Đối chiếu Program/endpoint/handler/DbContext của 10 service .NET, Gateway,
source/config/router/security/Dockerfile/requirements của AI, shared JWT/
HttpClient/Redis/persistence, ba Compose hiện tại, env example, ACL và các test
liên quan. Danh mục bên dưới dựa trên route thực sự được Program đăng ký;
không coi TODO/comment hoặc yêu cầu UI là API đã tồn tại.

Trong lượt audit này:

- Chỉ đọc file và inspect cấu hình container; không gọi API nghiệp vụ.
- Inspect JWT chỉ xuất boolean so sánh key và issuer/audience; không xuất key,
  password, token hoặc full environment. Không nhập lại/reset credentials.
- Không kết nối SQL vào database, không migration/seed/fixture, không cấp JWT.
- Không chạy test suite, tạo/start/restart/recreate/stop container hoặc sửa Mobile.
- Chỉ thêm tài liệu này; giữ nguyên code, config, ACL và lịch sử migration.
- Số bảng/fingerprint dưới đây là bằng chứng Gate C đã lưu, không phải một
  đợt database audit mới trong Gate D.

Bằng chứng Gate C mới nhất:
gateway_runtime_20261029_101337/gateway-result.json trong thư mục private
%LOCALAPPDATA%/AICOS/deployments. Kết quả: 11 route PASS; 11 trường hợp GET
được bảo vệ bị từ chối đúng 401; 50 phản hồi GET Gateway đã ghi nhận;
38 bảng/244 cột và fingerprint 25 bảng không đổi. Không suy ra toàn bộ 38 bảng
đã được fingerprint hoặc API có JWT hợp lệ đã PASS.

## 2. Kiểm kê API nghiệp vụ thực tế

Có **24 route nghiệp vụ .NET**: 10 GET, 13 POST, 1 PATCH; không có PUT/DELETE.
Có thêm **4 POST stub của AI**. Hai cách query bill theo sessionId/orderId là
hai test case của cùng một route, không đếm thành hai endpoint.

Quy ước quyền API, khác hoàn toàn PostgreSQL service role:

- S = SystemAdmin; M = Manager; W = Waiter; C = Cashier; H = WarehouseStaff.
- Staff = S/M/W/C/H, theo implementation trong AuthenticationExtensions.
- Management = S/M. Operational = S/M/W/C. Billing/Cashier = S/M/C.
- Có JWT đúng role chưa đủ: handler còn kiểm tra user/branch và dữ liệu liên quan.

Tất cả dòng .NET dưới đây đã có handler xử lý thật/PostgreSQL; trạng thái này
là phân loại source, KHÔNG phải kết quả kiểm thử nghiệp vụ Gate D.

| ID | Service | Method + đường dẫn đầy đủ | JWT/quyền | Khả năng ghi / tác dụng |
|---|---|---|---|---|
| I1 | Identity | POST /api/v1/identity/auth/login | Anonymous; email/password, branch assignment hợp lệ | Có: phát access/refresh, INSERT refresh_sessions khi hoàn tất login |
| I2 | Identity | POST /api/v1/identity/auth/refresh | Anonymous; cần refresh token hợp lệ | Có: revoke/rotate session và INSERT session mới; có nhánh lỗi cũng revoke |
| I3 | Identity | POST /api/v1/identity/auth/logout | Anonymous; xử lý theo refresh token | Có: revoke refresh session; không tự blacklist access JWT |
| I4 | Identity | GET /api/v1/identity/auth/me | Staff; assignment còn ACTIVE | Chỉ đọc hồ sơ hiện tại |
| I5 | Identity | POST /api/v1/identity/staff | Management; M chỉ branch mình, không tạo M | Có: users, tenant_users, staff_assignments, có thể thêm roles |
| M1 | Menu | GET /api/v1/menu/catalog?categoryId=&search= | Staff; branch claim | Chỉ đọc catalog, giá/option và availability |
| T1 | Table | GET /api/v1/tables/map | Staff; branch claim | Chỉ đọc sơ đồ bàn |
| T2 | Table | GET /api/v1/tables/sessions/{sessionId} | Staff; branch scope | Chỉ đọc phiên bàn |
| T3 | Table | POST /api/v1/tables/sessions | Operational | Có: mở phiên, link bàn, đổi cafe_tables.status |
| T4 | Table | POST /api/v1/tables/sessions/{sessionId}/close | Operational | Có: sau kiểm tra settlement, đóng phiên/link, bàn chờ dọn |
| T5 | Table | POST /api/v1/tables/{tableId}/cleaned | Operational | Có: bàn CLEANING thành AVAILABLE |
| O1 | Ordering | GET /api/v1/orders/?sessionId={id} | Staff; branch scope | Chỉ đọc các order trong phiên |
| O2 | Ordering | GET /api/v1/orders/{id} | Staff; branch scope | Chỉ đọc order detail |
| O3 | Ordering | POST /api/v1/orders/ | Staff; branch/user claim | Có: order/items/options/events; gọi Menu và Table nếu DINE_IN |
| K1 | Kitchen | GET /api/v1/kitchen/queue | Staff; branch scope | Chỉ đọc queue |
| K2 | Kitchen | PATCH /api/v1/kitchen/items/{itemId}/status | S/M, không phải mọi Staff | Có: order_items.status và order_item_events |
| P1 | Payments | GET /api/v1/payments/bills?sessionId={id} hoặc orderId={id} | Staff; đúng một source, branch scope | Chỉ đọc bill; gọi Ordering kiểm tra source |
| P2 | Payments | GET /api/v1/payments/bills/{billId} | Staff; branch scope | Chỉ đọc bill; gọi Ordering kiểm tra source |
| P3 | Payments | POST /api/v1/payments/bills | Billing | Có: bills/bill_items; snapshot nhiều order của phiên |
| P4 | Payments | POST /api/v1/payments/bills/{billId}/payments/cash | Billing | Có: payment SUCCEEDED, bill PAID, audit |
| P5 | Payments | POST /api/v1/payments/bills/{billId}/payments/qr | Billing | Có: BANK_TRANSFER/PENDING, tăng version, audit CREATED |
| P6 | Payments | POST /api/v1/payments/payments/{paymentId}/confirm | Billing; EvidenceReference bắt buộc | Có: xác nhận thủ công QR, SUCCEEDED/PAID, audit CONFIRMED |
| P7 | Payments | POST /api/v1/payments/bills/{billId}/close | Billing | Có: chỉ đóng khi PAID, đủ tiền, không PENDING |
| P8 | Payments | GET /api/v1/payments/sessions/{sessionId}/settlement-status | Staff; branch scope | Chỉ đọc; Settled chỉ true khi bill CLOSED, đủ tiền, không PENDING |

P3–P7 đang bị maintenance chặn trên stack Gate C vì MutationsEnabled=false.
Đó là API đã triển khai nhưng CHƯA được phép ghi trên database chính.
Middleware chạy trước authentication của Payments; direct mutation lúc maintenance
có thể trả 503 trước 401/403. Gateway vẫn từ chối Authorization sai trước proxy.
Không dùng 503 maintenance làm bằng chứng kiểm thử role thành công.

### Service khung và AI stub

| Service | Endpoint nghiệp vụ | Hiện trạng / quyền / khả năng ghi |
|---|---|---|
| Inventory | Không có | Feature mapping comment, DbContext không có DbSet; nghiệp vụ N/A |
| Workforce | Không có | Feature mapping comment, DbContext không có DbSet; nghiệp vụ N/A |
| Reporting | Không có | Feature mapping comment, DbContext không có DbSet; nghiệp vụ N/A |
| Customer | Không có | Feature mapping comment, DbContext không có DbSet; nghiệp vụ N/A |
| AI | POST /api/v1/ai/chat | Stub 501; anonymous được phép, bearer sai bị AI reject nếu cung cấp; hiện không ghi/model call |
| AI | POST /api/v1/ai/attendance/verify | Stub 501; S/M/C/H; hiện không ghi/model call |
| AI | POST /api/v1/ai/forecast/demand | Stub 501; S/M/H; hiện không ghi/model call |
| AI | POST /api/v1/ai/summary | Stub 501; S/M/AiBusinessAssistant; hiện không ghi/model call |

AI dùng FastAPI/Uvicorn, cổng 8000; health ready chỉ TCP PostgreSQL/Redis, chưa
có SQL driver/model/repository. Các POST stub có thể trở thành mutation/tác vụ
nặng khi triển khai về sau: hiện loại khỏi D1–D4, không xem 501 là business PASS.
Không chạy training, tải model hoặc gọi provider theo kế hoạch này.

### API hạ tầng và Gateway

Mười service .NET có GET /health/live, /health/ready và
/api/v1/{prefix}/_info anonymous, Swagger UI /swagger/index.html và
OpenAPI /swagger/v1/swagger.json khi Development/Swagger enabled.

| Service | Prefix | Port Gate C loopback |
|---|---|---|
| Identity | identity | 5101 |
| Menu | menu | 5102 |
| Table | tables | 5103 |
| Ordering | orders | 5104 |
| Payments | payments | 5105 |
| Inventory | inventory | 5106 |
| Kitchen | kitchen | 5107 |
| Workforce | workforce | 5108 |
| Reporting | reporting | 5109 |
| Customer | customers | 5110 |
| AI | ai | 5111 |

AI có GET /health/live, /health/ready, /api/v1/ai/_info, /docs, /redoc,
/openapi.json. Gateway là YARP 2.3.0, GET / và /health/live tại 8080; không có
aggregated ready/Swagger riêng. 11 catch-all proxy route giữ path/query/method,
không phải 11 API nghiệp vụ mới. .NET downstream dùng service:8080; AI ai:8000.
Health/metadata/docs không ghi dữ liệu nghiệp vụ.

### Các API chưa có — không tự đưa thành test bắt buộc của implementation hiện tại

Không thấy public register, staff GET detail/list, catalog/table-definition CRUD,
order sửa/hủy, takeaway list độc lập, chuyển/ghép bàn, split/adjust bill,
print-status mutation, payment cancel/fail/refund/provider webhook. Refunds có
mapping nhưng chưa có endpoint và payment_svc không có SELECT refunds.
PrintStatus có model/response, mặc định NOT_ISSUED, chưa có API in bill.
FAILED/CANCELLED trong enum không đồng nghĩa có luồng API tương ứng.

Không tự follow mọi Location của POST: staff detail và payment detail được
tham chiếu trong Location nhưng không có GET route tương ứng trong source.
Báo cáo hợp đồng thiếu nếu cần, không dựng endpoint/mock để test PASS.

## 3. JWT: cấu hình, tương thích và giới hạn

| Thành phần | Kiểm tra source | Kết quả audit |
|---|---|---|
| Identity issuer | JwtTokenService ký HMAC-SHA256 | iss=aicos, aud=aicos-clients, TTL mặc định 60 phút |
| Claims | sub/name/role/jti, thêm branch_id nếu có | Không có tenant_id claim; cần fixture quan hệ tenant/branch đúng |
| Gateway + 10 .NET validators | Shared AuthenticationExtensions | Validate issuer/audience/signature/lifetime; MapInboundClaims=false; skew 30 giây |
| API policy | role raw claim; Staff gồm cả Waiter | Phân biệt endpoint policy với kiểm tra branch trong handler |
| AI validator | PyJWT HS256, iss/aud, leeway 30 | Tương thích cấu hình; protected routes đều POST stub |
| Gate C runtime environment | Read-only inspect 12 app containers | Key match Identity=true cho tất cả; không in key/hash/key prefix |
| Runtime issuer/audience | Không có env override; đối chiếu source defaults | Đồng nhất aicos/aicos-clients |
| Current safeguards | Inspect config flags | Mười migration=false, Payment mutations=false |

Signing key Development trong source là fallback, không dùng cho Gate D.
Khóa thực tế đã được override trong Gate C; audit không thay hoặc phát token.
Cấu hình giống nhau không chứng minh login/role/branch semantics đã PASS.

Gate D phải có khóa test RIÊNG, chỉ dùng trong test stack; không reuse hoặc
rotate key Gate C. Có thể giữ cùng issuer/audience để kiểm tra đúng hợp đồng,
nhưng key khác khiến token test không dùng được trên stack chính.
JWT chỉ nhận qua Identity test login/refresh sau phê duyệt, không tự ký token
để bỏ qua user/assignment trong kiểm thử HTTP end-to-end.

Identity login hiện chỉ map SystemAdmin/Manager/Waiter/Cashier/WarehouseStaff.
Không giả định login này cấp được Customer/Guest/AiBusinessAssistant.
Staff provisioning chỉ tạo Manager/Waiter/Cashier/WarehouseStaff, không tạo
SystemAdmin: tài khoản bootstrap cần fixture riêng trên database TEST.

Logout revoke refresh token, không có access-token denylist. Test không được
kỳ vọng access JWT tự hết hạn ngay khi logout. Expiry test dùng token hợp lệ
do Identity test cấp, TTL ngắn ở test profile và chờ qua exp + clock skew;
không làm giả token hoặc đổi thời gian hệ thống.

## 4. Môi trường test — D1-A đã tạo infra; schema/app chờ duyệt

**Khuyến nghị: một PostgreSQL 18 cluster test riêng**, database
aicos_payment_test/public, để khớp PaymentPostgresTestDatabaseGuard hiện có.
Đây là database TEST, không phải database vận hành thứ hai.

Tên database trùng tên test lịch sử không có nghĩa là cùng server:
không sửa/reset aicos_payment_test đang có trên Windows localhost:5432.
Cluster mới có manifest/identifier/credentials/volume hoàn toàn riêng.

| Thành phần | Đề xuất Gate D | Bảo vệ Gate C/main |
|---|---|---|
| Compose | File standalone docker-compose.gate-d.yml, project aicos-gate-d | Không merge base/external-db vốn hardcode aicos_db/container_name hiện tại |
| PostgreSQL | Image PostgreSQL 18 phù hợp extension baseline; hostname gated-postgres | Không dùng bundled pg16, không trỏ host.docker.internal |
| DB | aicos_payment_test, schema public | Deny target aicos_db; không overwrite database test cũ |
| Database access | DockerExec pin ID tới gated-postgres:5432; không host publication | Không kết nối PostgreSQL Windows |
| Gateway | Chưa tạo; internal-only test runner trước, host exposure cần review riêng | Không dùng Gateway Gate C |
| Service exposure | Internal-only; host debug ports khác nếu thật sự cần | Không dùng 5101–5111 |
| Network | Network riêng internal, chỉ test services; không nối aicos-be_default | Không có route cấu hình đến main; không thêm host-gateway |
| Redis | Instance/volume riêng, không publish 6379 | Không dùng Redis Gate C kể cả đổi Redis DB number: Pub/Sub không cô lập theo DB |
| Credentials | Password test riêng, runtime env/secret store private | Không nạp lại DPAPI cache/key của Gate C vào test services |
| JWT | Khóa test riêng, shared trong stack test | Không reset key hiện tại |
| Volume/run marker | Volume và manifest runId/cluster identifier riêng | Không mount postgres-data/redis-data hiện hữu |
| Startup | Staged, MigrateOnStartup=false; không business seed tự động | Không restart/recreate container đã PASS |
| Resources | Kiểm tra RAM/disk/port trước mỗi stage; D1 chỉ infra+Identity+Gateway | Không tự dừng Gate C/đổi Docker resources để lấy tài nguyên |

Không mount infra/postgres/init của stack cũ vào test cluster: initializer đó
không phải bộ baseline đã kiểm chứng cho public/Gate D. Không cần khởi động AI
và bốn module khung trong D1–D4. Khi thiếu tài nguyên: dừng trước stage mới,
báo cáo để chủ dự án quyết định; không tự thay cấu hình Docker hoặc tắt Gate C.

### Nguồn schema, migration và ACL test

D1 chuẩn bị SQL để người dùng review TRƯỚC khi chạy trên cluster test:

1. Ưu tiên artifact schema-only sau Gate B đã kiểm chứng, hoặc export metadata
   schema-only mới theo phê duyệt; no-owner/no-privileges, không copy row/user
   data, password hashes, refresh tokens từ main. Review extension/function/
   foreign-server/database reference trước restore.
2. Restore đúng schema hiện tại (mốc 38 bảng/244 cột cùng PK/FK/index/constraint).
   Count đúng chưa đủ: đối chiếu definition/hash, các ràng buộc Payment và kiểu
   timestamp/decimal. Không chạy lại 00_preflight/01–05 nếu đã có Payment schema.
3. Database.sql gốc không đủ thay thế baseline mới vì còn thiếu phần Identity/
   Payment đã được bổ sung. EF baseline không phải công cụ tạo lại toàn bộ
   public. Không sửa hai migration Identity đã chốt.
4. Nếu dùng baseline CŨ thực sự thiếu Payment, phải review SQL/diff và chỉ apply
   phần cần thiết vào TEST theo thứ tự đã kiểm chứng; đây là phương án phụ cần
   duyệt, không tự lựa chọn khi restore lỗi.
5. Tạo runtime roles test trong CLUSTER RIÊNG theo stage, clone ma trận quyền
   đã review, không cho service dùng admin/DELETE/DDL. Script ACL hiện hardcode
   aicos_db và guard database; phải tạo bản test-only được review, không chạy
   nguyên script public-service-grants.sql trên main hoặc bỏ guard cho tiện.
6. Fixture writer/setup role riêng chỉ ở test, phục vụ bootstrap/seed/cleanup
   đã duyệt. App service luôn dùng role tối thiểu của nó.
7. Lưu checkpoint test trước mỗi stage. Chưa chạy bất cứ SQL/restore/migration/
   tạo role hoặc fixture nào trong audit này.

### Guard bắt buộc trước mọi thao tác ghi trong D1–D4

Runner tương lai phải fail closed nếu thiếu một điều kiện:

- Project/container/network/volume thuộc aicos-gate-d; không trùng ID Gate C.
- DB tên aicos_payment_test VÀ host/port nằm đúng cluster test; tên DB một mình
  chưa đủ vì có test database cùng tên trên host. Đối chiếu cluster identifier,
  version, manifest runId, user và schema. Deny aicos_db và main host:5432.
- Mọi connection string, service client URL và Gateway destination chỉ tới
  test dependencies; không fallback localhost/host.docker.internal/source defaults.
- Test signing key khác key Gate C; test passwords không reuse production.
- Migration startup=false; không auto-seed khi app start; mutations Payments
  chỉ được bật ở Payment TEST khi đến D4 và được duyệt.
- Tất cả expected services healthy, schema/ACL đạt, fixture manifest đúng scope.
- Log/snapshot không chứa passwords/access JWT/refresh token; response auth
  chỉ giữ private trong memory/secret storage, không xuất vào báo cáo.
- Chưa có guard test-only thì không chạy blanket dotnet test hoặc gọi API write.

## 5. Tài khoản và fixtures hợp lệ

Không dùng tài khoản có sẵn trong main để login lấy token: login thành công có
ghi refresh_sessions; refresh/logout cũng có ghi, dù không tạo user mới.

D1 đề xuất bootstrap bằng fixture writer trên test cluster, dùng cùng
PasswordHasher<UserAccount> của ASP.NET Identity; không chèn mật khẩu plaintext
vào password_hash, không copy user/password từ main, không hardcode mật khẩu
trong Git. Account IDs/emails gắn runId; ví dụ mẫu địa chỉ dùng miền .invalid,
chưa tạo account nào.

| Nhóm fixture | Mục đích |
|---|---|
| Hai tenant, branch A1/A2 và B1 ACTIVE | Phân biệt cùng tenant/khác tenant, cross-branch denial |
| SystemAdmin bootstrap test | Đăng nhập hợp lệ, sau đó test I5; API không tạo được admin đầu tiên |
| Manager A1, Manager B1 | Positive provisioning và negative khác branch/tenant |
| Cashier/Waiter/WarehouseStaff A1 | Ma trận quyền thật, không cần JWT ký giả |
| Một account có hai assignment A1/A2 | Login yêu cầu chọn branch; không chọn chưa phát access/refresh |
| User/assignment/branch inactive và sai password cases | Kiểm tra 401/denial, không dùng người thật |
| D2 catalog/tables mới | Chỉ tạo sau D2: products/variants/options/branch_products, table_areas/cafe_tables |
| D3/D4 orders/bills/payments | Ưu tiên tạo qua API thật theo stage, không SQL thay thế kết quả API |

Role codes phải qua MapRole thật và quan hệ users → tenant_users → roles →
staff_assignments → branches đúng. Manager chỉ được tạo staff branch mình và
không tạo Manager; SystemAdmin mới có thể tạo Manager qua I5.
I5 yêu cầu password 12–200 ký tự; fixture password sinh/nhập ẩn sau phê duyệt.

Không có API catalog/table-definition CRUD để seed D2, nên cần fixture SQL/
setup helper test-only được review. Fixture setup là mutation, phải được duyệt
riêng phạm vi, không tự phát sinh dưới nhãn read-only test.

## 6. Dependencies theo luồng

Các service client đi trực tiếp bằng Docker service DNS, không qua Gateway;
ServiceCallHandler forward bearer/correlation ID. JWT hợp lệ của D1 phải dùng
được qua cả Gateway lẫn chuỗi internal HTTP thật.

| Luồng | Dependencies thật | Điểm cần đo trong Gate D |
|---|---|---|
| Identity login/refresh/me/provision | PostgreSQL Identity; Redis readiness | Password hash/assignment, refresh persistence/rotation/concurrency |
| Menu catalog | PostgreSQL 6 bảng Menu; Redis readiness | Branch availability, active flags, giá/options/filter |
| Table map/open | PostgreSQL 4 bảng Table; Redis readiness | Trạng thái bàn/link, đồng thời mở cùng bàn |
| Ordering DINE_IN | Table session GET + Menu catalog GET + Ordering DB | Forward JWT, snapshot giá/options, persist đúng branch |
| Ordering TAKEAWAY | Menu catalog GET + Ordering DB | Không có session, có pickupTime tùy chọn; không có giao hàng |
| Kitchen queue/status | Orders/order_items/order_item_events chung với Ordering | Quyền UPDATE cột, event actor/status; chưa có queue/message DB riêng |
| Bill create/read | Ordering GET theo session/order + Payments DB | Multi-order total, source access, snapshot/idempotency |
| Cash/QR/confirm/close | Payments DB; actor user/FK fixtures hợp lệ | Serializable/version/idempotency, audit/evidence, số tiền |
| Table close | Table → Payments settlement GET | Fail closed khi unavailable/unpaid/pending/not CLOSED |

Kitchen truy cập các bảng Ordering là ngoại lệ kiến trúc đã tồn tại/được review,
không được biến thành quyền rộng hơn hoặc giả định module độc lập hoàn toàn.
Redis Pub/Sub hiện không có active business handler được đăng ký trong Programs
đã audit; Ordering/Kitchen đồng bộ qua bảng chung, không chứng minh event delivery.
Không dùng “Redis healthy” làm bằng chứng event/outbox/business processing PASS.

## 7. Kế hoạch thực hiện từng gate — CHƯA CHẠY

Mỗi bước dừng báo cáo, chỉ chuyển bước khi được duyệt. D1 không tự cấp quyền D2–D4.

### D1 — Test isolation + Identity

**Prerequisites:** review SQL/fixture/config/guards, chấp thuận tài nguyên,
cluster test/key/credentials mới và phạm vi Identity writes. Giữ nguyên Gate C.

Trình tự đề xuất:

1. Chuẩn bị standalone Compose/runner/preflight và test-only SQL; review allowlist
   đích, baseline schema, grants, fixture manifest trước chạy.
2. Tạo isolated PostgreSQL/Redis, restore schema test đã duyệt; kiểm tra ACL,
   cluster identifier và checkpoint D1-empty. Không sửa DB/roles host.
3. Bootstrap fixture Identity test đã duyệt; checkpoint D1-fixtures.
4. Chỉ chạy Identity test + Gateway test. Những route test service chưa start
   chưa cần PASS; không khởi động mọi service để che thiếu dependency.
5. I1 đúng password/branch trả token và hồ sơ đúng; row refresh_sessions tăng
   đúng một. Sai password/branch/inactive không cấp token, không tạo session.
6. Multi-assignment không chọn branch trả RequiresBranchSelection=true, token
   null và không INSERT refresh; chọn branch đúng mới phát token.
7. I4 qua direct/Gateway trả 200 đúng branch/user; thiếu/sai/expired token 401.
   Không ghi token vào evidence. Expiry đợi qua TTL+30s, không đổi clock hệ thống.
8. I2 rotate refresh; token cũ không dùng lại; concurrent refresh không tạo hai
   active replacements. I3 revoke/idempotent đúng source, sau đó refresh bị từ chối.
9. I5 Manager/Cashier/SystemAdmin test 201/403/409, Manager không tạo Manager/
   khác branch; validate password/email; xác minh 4 nhóm bảng identity đúng.
10. Ghi D1_EXECUTION_RESULT với actual passed/failed/skipped/not-run và checkpoint.

**PASS:** tất cả allowlist/isolation guards đạt; token chỉ do Identity test cấp;
expected 200/201/204, 400/401/403/409 theo contract; refresh hash/revocation và
row deltas đúng; không trùng account/assignment; Gate C/main không đổi.
Không yêu cầu access token cũ bị revoke ngay khi logout vì source chưa có cơ chế đó.

**FAIL/STOP:** target rơi về main, config/key lệch, thiếu schema/ACL, bất kỳ 5xx
ngoài dự kiến, cấp token cho assignment sai, hai refresh successors hoặc test skip
thay cho PostgreSQL. Không tự sửa schema/security khi gặp lỗi.

**Recovery:** dừng runner/test apps được xác định đúng project để chặn writes;
giữ evidence. Khôi phục checkpoint test D1-empty/D1-fixtures chỉ sau xác nhận
đích và phê duyệt restore; không rollback aicos_db. Không auto DROP/down -v.

### D2 — Menu và Table

**Prerequisites:** D1 PASS + token test hợp lệ; duyệt catalog/table fixture writes;
khởi động thêm Menu/Table TEST, không thay container Gate C.

- M1: catalog có dữ liệu, category/search, inactive/unavailable exclusion, giá,
  options/min-max, A/B tenant/branch isolation; đọc phải không đổi fingerprint.
- T1/T2: map/session đúng branch, missing source và cross-branch đúng contract;
  các GET không thay đổi dữ liệu.
- T3: AVAILABLE → OCCUPIED, session OPEN và link đúng; hai requests cạnh tranh
  cùng bàn chỉ có một active session; gọi khi OCCUPIED/CLEANING bị chặn.
- T5: chỉ CLEANING không có active session mới → AVAILABLE; role H bị 403.
- T4 closure toàn phần để D4, khi Payment đã có dữ liệu settlement thật.
  Không giả lập settlement=true để tuyên bố test HTTP end-to-end PASS.

**PASS:** catalog/tables đúng fixture, không rò dữ liệu A/B, GET invariant giữ;
open/clean mutations chỉ tác động expected rows, concurrency không tạo hai phiên.
**FAIL:** sai tenant/branch, giá/option lệch, duplicate active session, GET ghi dữ
liệu, unexpected 5xx hoặc thiếu SELECT/column privilege.
**Recovery:** giữ failed case/evidence; phục hồi test checkpoint D1 sau phê duyệt,
hoặc fixture cleanup FK-aware đã review. Không cần/bổ sung API DELETE trên main.

### D3 — Ordering và Kitchen

**Prerequisites:** D2 PASS; thêm Ordering/Kitchen TEST, JWT forwarding thật,
fixture menu/session/takeaway và checkpoint D2; duyệt POST O3/PATCH K2.

- O3 DINE_IN gọi Table/Menu thật; TAKEAWAY không session. Thử quantity/options/
  unavailable variants/invalid session; total = (variant + options) × quantity.
- O1/O2 xác minh persisted snapshot, createdByUserId, loại order, pickupTime,
  multi-order/session và cross-branch. Không test API edit/cancel chưa tồn tại.
- Replay RequestId cùng payload và parallel retries: một order/items/event set.
  Cùng RequestId nhưng payload khác là ca rủi ro bắt buộc; source hiện trả existing
  mà chưa đối chiếu payload. Không đổi tiêu chí để gọi silent mismatch là PASS.
- K1 thấy đúng items đã tạo, không cần message giả hoặc SQL chèn thay kết quả O3.
- K2 Manager: PENDING → PREPARING → READY; chặn nhảy thẳng/lùi/lặp sai state,
  event ghi actor/old/new đúng; Waiter/Cashier/H bị 403 theo policy hiện tại.
- Sau READY, item biến khỏi queue; order detail phản ánh trạng thái item.
  Không tự giả định order.status đổi theo item vì source chưa triển khai việc đó.
- Thử dependency failure chỉ trên test stack bằng config/mock failure được duyệt;
  không stop Gate C, không gọi endpoint tạo dữ liệu trên stack chính.

**PASS:** REST dependencies thật, JWT/correlation đến đúng scope, số tiền và rows
đúng, không duplicate replay, transitions/audit đúng, role/branch denial đúng.
**FAIL:** thiếu authentication forwarding, silent conflicting replay chưa được
chấp nhận contract, lost update, sai actor/giá hoặc bất kỳ main-data write.
**Recovery:** quiesce test writers, lưu lỗi; phục hồi checkpoint D2 sau phê duyệt.
Không dùng DELETE quyền runtime; không sửa legacy boundary Kitchen trong gate này.

### D4 — Billing, Cash/QR và settlement/table closure

**Prerequisites:** D3 PASS; test Payments schema/unique indexes/version/audit/FK
đạt; checkpoint D3; thêm Payment TEST. Chỉ bật MutationsEnabled=true ở Payment
TEST sau duyệt. Main/Gate C vẫn false. Không ngân hàng thật/provider webhook.

- Kiểm tra maintenance 503 theo source trên TEST trước khi bật write. Khi kiểm
  tra role 403 thì write guard phải bật trên TEST, nếu không 503 che auth check.
- P3 snapshot toàn bộ các order của một session; TAKEAWAY bill theo orderId.
  Dine-in theo orderId bị business rule chặn; sessionId/orderId phải XOR.
- P1/P2 200/400/404/branch denial đúng và thực sự gọi Ordering với JWT; P8 trước
  có bill trả settled=false, không suy từ thiếu source thành đã thanh toán.
- CreateBillRequest: requestId + sessionId XOR orderId; replay cùng source không
  thêm bill/items; đổi source cùng id hoặc tạo bill khác cùng source phải conflict.
- Cash: requestId, amount, expectedBillVersion; phải bằng outstanding đầy đủ,
  SUCCEEDED/PAID, actor/confirmed timestamps/audit đúng. Không hỗ trợ partial/
  cash change/split tender chỉ vì UI có chỗ nhập tiền.
- QR: BANK_TRANSFER/PENDING, transaction reference, audit CREATED; không tự
  SUCCEEDED khi poll/refresh. Pending chặn payment mới và close.
- Confirm QR: confirmationRequestId, expectedVersion, evidenceReference bắt buộc,
  note tùy chọn; chỉ S/M/C, version cũ/thiếu evidence/conflicting replay bị chặn.
  Test proof là fixture chứng từ giả lập được đánh dấu test-only, không được
  tuyên bố xác minh giao dịch ngân hàng thật; PostgreSQL vẫn đọc/ghi thật.
- Concurrency hai thu ngân: bounded 2–4 requests, không load test; một kết quả
  hợp lệ hoặc idempotent replay, không hai payment/audit confirmations.
- P7 chỉ PAID đủ tiền/no pending mới CLOSED; replay close không đổi lại closedAt/actor/version.
  P8 chỉ settled=true khi CLOSED và đủ tiền. PAID chưa CLOSED vẫn false.
- T4 qua REST Table → Payments thật: unpaid/pending/PAID-chưa-CLOSED/unavailable
  đều không đóng; CLOSED đủ tiền mới đóng và bàn CLEANING; T5 sau đó AVAILABLE.
- Ca bắt buộc: thêm order SAU bill snapshot và đua close-session/order-create.
  Source hiện chưa thể hiện cơ chế freeze/reconcile bill snapshot; không chấp
  nhận settlement=true nếu bỏ sót món chưa được thanh toán. Nếu tái hiện, dừng
  để chủ dự án duyệt phương án nghiệp vụ/code riêng.
- PrintStatus chỉ kiểm tra response/default NOT_ISSUED; không tuyên bố đã test
  printer/print status mutation/refund/cancel vì thiếu endpoint.

**PASS:** no double payment, amount/audit/evidence/version chính xác; không tự
QR success; table không close trước settlement; bill đủ tất cả order phải trả.
Negative cases không ghi ngoài allowlist; API 5xx không được tính “đã chặn đúng”.
**FAIL:** lost money/unbilled late order, duplicate confirmation, stale version
được chấp nhận sai, cross-branch access, missing audit, close sai hoặc main bị ghi.
**Recovery:** tắt test runner/quiesce writers, giữ DB/evidence thất bại; có thể
đưa riêng Payment TEST về maintenance (không main), sau đó restore checkpoint D3
chỉ khi được duyệt. Không xóa audit để che lỗi và không chạy rollback 90–92 main.

## 8. Quy tắc đánh giá và bằng chứng

| Loại | Tiêu chí |
|---|---|
| PASS | Đã thực thi trên đích xác minh; HTTP + nội dung + persisted state + expected row deltas/invariants cùng đạt |
| FAIL | Actual khác contract, 5xx bất ngờ, sai isolation/amount/state/ACL hoặc unsafe target |
| BLOCKED | Prerequisite thiếu; không gọi mutation để tìm cách lách |
| SKIPPED | Framework skip; nêu tên/lý do/số lượng, không cộng PASS integration |
| NOT RUN / NOT TESTED | Chưa thực hiện; tất cả D1–D4 hiện ở trạng thái này |
| N/A | Chưa có implementation/endpoint tương ứng, nêu rõ scope thay vì báo PASS |
| PARTIAL | Một phần scope PASS nhưng business/prerequisite/coverage bắt buộc còn thiếu |

ValidationFailed thường 400, Unauthorized 401, Forbidden 403, NotFound 404,
Conflict/concurrency 409, BusinessRule 422. Ghi expected cụ thể theo từng handler,
không mặc định mọi negative đều 400 hoặc cho mọi 4xx là PASS. Login/QR data nhạy
cảm không xuất nguyên response. Ghi errorCode/correlationId đã redaction khi có.

Mỗi Dn cần manifest/checksum/schema version/target cluster/user, image IDs,
test account aliases (không password), case IDs/method/path/expected/actual,
latency/correlationId, persisted row/count/digest assertions, skipped tests,
dependency implementation thật hay fake, và checkpoint/restore status.
Lưu private evidence ngoài Git; chỉ tổng hợp đã ẩn secrets vào Markdown.

Giữ Gate C/main container snapshot. Nếu được duyệt read-only audit main trước/
sau một gate, chỉ fingerprint 25 bảng bằng roles hiện có, không xin thêm SELECT
cho 13 bảng còn lại. Test stack không được thiết kế để chạm main; kiểm tra 25
bảng là lớp xác minh bổ sung, không thay cho isolation guards.

Không coi outer SQL transaction trên runner là rollback của nhiều HTTP request:
mỗi service có connection/transaction riêng và commits thật. Rollback test phải
dùng checkpoint/fixture cleanup được review; restore không chạy trong lúc test
writers còn hoạt động. Không tự DROP database/volume, kể cả test database cũ.

## 9. Test đã có và khoảng trống

| Tài nguyên hiện có | Có thể tái dùng sau phê duyệt | Không chứng minh |
|---|---|---|
| tests/AICOS.Services.SmokeTests/ServiceSmokeTests.cs | Host/pipeline/info/live | Không thực hiện business SQL chỉ nhờ host boot PASS |
| AuthenticationService/TableOperations/Catalog/Ordering/Preparation/Billing/Payment unit tests | Logic cục bộ | Không chứng minh runtime ACL/S2S/Docker |
| PostgresAuthentication/StaffProvisioning/Catalog/TableOperations/Ordering/Preparation/Billing/PaymentIntegrationTests.cs | Real PostgreSQL có seed/cleanup | Một số test thay client dependency/tự ký JWT, không phải full chain qua Identity + Gateway |
| GatewayTests.cs | Middleware/correlation | Test “not 401” còn chấp nhận downstream 502/503/504, không phải routing PASS |
| Ai/tests/test_app.py | Sau duyệt scope riêng: contract stub/auth | Có POST/tự ký token; không chạy trong audit hoặc gọi AI functionality PASS |
| PaymentPostgresTestDatabaseGuard.cs | Chặn tên DB khác aicos_payment_test | Chỉ kiểm tra tên, chưa chặn cùng tên trên server sai |
| PostgresIntegrationFactAttribute.cs | Nhận biết khi thiếu AICOS_TEST_POSTGRES | Attribute sẽ Skip trước test body; REQUIRE=true một mình chưa bảo đảm fail thay vì skip |

Các suite SQL có seed/ExecuteDelete cleanup, không chạy dưới runtime roles không
có DELETE để rồi nới ACL cho tiện. Dùng fixture-writer test hoặc snapshot theo
phương án được duyệt; HTTP services vẫn phải chạy đúng least-privilege roles.
Chưa chạy dotnet test/pytest/build/Docker up ở lượt lập kế hoạch này.

Trước khi chạy legacy integration suite, runner phải xác minh và nạp cả
AICOS_TEST_POSTGRES đúng test host/port/database/cluster và
AICOS_REQUIRE_POSTGRES_INTEGRATION=true; kiểm tra TRX số lượng executed/skipped,
không chỉ exit code. Không set biến nào trong audit này.
Phải xác định rõ test nào tự ký token/fake dependencies; chỉ HTTP runner dùng
login Identity thật mới cung cấp bằng chứng Gate D end-to-end.
Historical PostgreSQL PASS trên aicos_payment_test không tự trở thành D1–D4 PASS.

## 10. Vấn đề phát hiện và ưu tiên

| Ưu tiên | Finding từ source/config | Tác động / bước xử lý đề xuất |
|---|---|---|
| P0 trước D1 | Chưa có isolated test stack/fixture writer/guards/manifest | Cần tạo theo phê duyệt; không repoint container hiện tại |
| P0 trước D1 | Login/refresh/logout đều có ghi session | Chỉ dùng Identity TEST; không lấy JWT qua login main |
| P0 trước D1 | Schema/ACL scripts chứa giả định database chính; baseline gốc thiếu patch | Review test-only schema/grants, không reuse mù |
| P0 trước chạy suite | Một số test thiếu dedicated-server guard; Skip attribute che REQUIRE | Wrapper fail-closed + parse TRX, không blanket test main |
| P1 D3 | O3 replay RequestId chưa so payload | Case conflicting replay cần contract rõ; chưa sửa |
| P1 D4 | Bill snapshot + late order/close concurrency chưa có freeze/reconcile rõ | Bắt buộc test; có thể cần design/code riêng nếu tái hiện |
| P1 kiến trúc | Kitchen trực tiếp dùng bảng Ordering | Giữ ngoại lệ cũ, không mở quyền; refactor cần scope riêng |
| P1 D4 | QR evidence hiện validate non-empty/length, không xác minh provider | Manual/test proof only; không claim bank integration hay chống tái dùng chứng từ đã PASS |
| P2 scope | Print/refund/edit-cancel/split APIs thiếu; bốn module khung/AI stub | N/A, tách backlog; không fake success |
| P2 tài nguyên | Stack Gate C phải giữ nguyên trong khi chạy test riêng | Đo RAM/port/disk trước D1; không tự restart/stop để giải quyết |

Các rủi ro D3/D4 là kết luận audit source để lập ca kiểm thử, chưa phải lỗi
runtime vừa được tái hiện. Audit này không sửa chúng và không thay đổi tiêu chí
thanh toán đã được phê duyệt.

## 11. Checklist cần chủ dự án phê duyệt

D1 cần xác nhận cụ thể:

- [ ] PostgreSQL 18 TEST cluster mới, DB aicos_payment_test/public, Redis/network/
      volumes/project/port riêng; giữ nguyên test database lịch sử trên host.
- [ ] D1 được tạo file standalone Compose/guards/fixture helper và review SQL
      schema/ACL test-only trước khi thực thi; không đổi migration đã chốt.
- [ ] Cho phép restore schema-only và tạo setup/runtime roles CHỈ trong cluster
      test mới; không thay credentials/ACL của cluster main.
- [ ] Fixture tenants/branches/roles/accounts synthetic và PasswordHasher thật;
      bootstrap admin TEST, lưu secrets private, không copy dữ liệu cá nhân main.
- [ ] Tạo khóa JWT test riêng, khởi động riêng Identity/Gateway TEST.
- [ ] Cho phép I1/I2/I3/I5 và GET I4 trên TEST, kể cả negative/concurrency cases;
      đây là ghi dữ liệu test, không còn là read-only smoke.
- [ ] Chính sách giữ snapshot/evidence, restore/cleanup test khi cần phải có
      kiểm tra đích và phê duyệt; không auto reset/drop/volume delete.
- [ ] Nếu tài nguyên không đủ hoặc cần pull image mới: dừng báo cáo, chờ quyết định.

D2/D3/D4 cần phê duyệt độc lập: fixtures Menu/Table; POST/PATCH Ordering/Kitchen;
và Payment TEST mutations/manual QR proof/concurrency/settlement. Phê duyệt D1
không cho chạy tiếp ba gate này, không thay database chính và không cho tải model.

D1-A đã chạy riêng hạ tầng và guard/readiness. D1-B chưa chạy: còn thiếu schema/
ACL được phê duyệt/apply, runtime test logins, fixture, Identity/Gateway test,
business runner được review và tài khoản/JWT hợp lệ TEST cho API integration.
Không coi readiness hạ tầng là bằng chứng nghiệp vụ.

**DỪNG SAU D1-A. Chờ phê duyệt D1-B; không tự apply schema/ACL, seed hoặc test nghiệp vụ.**

## 12. File nguồn tham chiếu

- [Gate C toàn bộ lịch sử và kết quả](GATE_C_EXECUTION_RESULT.md)
- [Gate C readiness](GATE_C_RUNTIME_READINESS.md)
- [Compose gốc](../docker-compose.yml),
  [external DB](../docker-compose.external-db.yml),
  [Gate C override](../docker-compose.gate-c.yml)
- [Shared authentication/policies](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/AuthenticationExtensions.cs),
  [JWT issuance](../src/BuildingBlocks/AICOS.BuildingBlocks/Security/JwtTokenService.cs),
  [HTTP forwarding](../src/BuildingBlocks/AICOS.BuildingBlocks/Http/ServiceClientExtensions.cs)
- [Identity endpoints](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Authentication/AuthenticationEndpoints.cs),
  [Staff provisioning](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/StaffProvisioning/StaffProvisioningEndpoints.cs)
- [Menu](../src/Services/MenuCatalog/AICOS.MenuCatalog.Api/Features/Catalog/CatalogEndpoints.cs),
  [Table](../src/Services/TableReservation/AICOS.TableReservation.Api/Features/TableOperations/TableOperationsEndpoints.cs),
  [Ordering](../src/Services/Ordering/AICOS.Ordering.Api/Features/Orders/OrderEndpoints.cs),
  [Kitchen](../src/Services/KitchenOperations/AICOS.KitchenOperations.Api/Features/Preparation/PreparationEndpoints.cs)
- [Billing](../src/Services/Payments/AICOS.Payments.Api/Features/Billing/BillingEndpoints.cs),
  [Payment](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentEndpoints.cs),
  [Maintenance](../src/Services/Payments/AICOS.Payments.Api/Features/Payments/PaymentMaintenanceMiddleware.cs)
- [ACL đã review](../infra/postgres/security/public-service-grants.sql),
  [Payment test guard](../tests/AICOS.Services.SmokeTests/PaymentPostgresTestDatabaseGuard.cs),
  [Skip attribute](../tests/AICOS.Services.SmokeTests/PostgresIntegrationFactAttribute.cs)
- [AI app](../src/Services/Ai/app/main.py),
  [AI auth](../src/Services/Ai/app/security.py),
  [Gateway](../src/Gateway/AICOS.Gateway/Program.cs),
  [Gateway routes](../src/Gateway/AICOS.Gateway/appsettings.json)


