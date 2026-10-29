# Gate D1-C — Chuẩn bị Identity Integration Testing

Ngày: 2026-10-29 (Asia/Saigon, UTC+07).
Phạm vi phê duyệt lượt này: **AUDIT VÀ LẬP KẾ HOẠCH**.
Kết quả: **C1 PREPARATION PASS; CHƯA THỰC THI IDENTITY BUSINESS TESTS**.
Sau audit, chủ dự án đã đồng ý làm C1: guard/config và preflight runner, không
credentials/LOGIN, fixtures hoặc runtime. [Kết quả C1](GATE_D1_C_C1_RESULT.md).
Chủ dự án đã duyệt riêng việc start đúng PostgreSQL/Redis TEST để hoàn tất audit.
Phê duyệt này không bao gồm credentials/LOGIN, fixtures, app TEST hoặc business APIs.

Đã đọc D1-A, D1-B và kế hoạch Gate D; đối chiếu source Identity, schema artifact,
ACL, JWT dùng chung, Gateway YARP, Compose/Dockerfile và các tests hiện có.
Trong audit ban đầu chỉ viết tài liệu. Sau phê duyệt C1 đã thêm guard/config/test
tooling và SQL readiness READ ONLY trong AICOS-BE như báo cáo C1; không sửa API
nghiệp vụ, migrations hay dữ liệu/roles. Chưa chạy Identity business tests.

## 1. Đích test và bằng chứng hiện có

### Kết quả sau khi start đúng hai container TEST đã được duyệt

Đã kiểm tra ID/image/labels/network/volume/host bindings trước start và dùng pinned
container IDs để start riêng PostgreSQL/Redis; không compose up/build/recreate.
Một lỗi guard PowerShell khi duyệt network members rỗng đã dừng trước start/SQL;
sửa helper tạm để duyệt property collection rỗng, không bỏ kiểm tra foreign members.
Không sửa guard/runner source của D1-A/B và không thay historical manifests.

| Kiểm tra live | Kết quả thực tế |
|---|---|
| PostgreSQL / Redis TEST | Cả hai healthy, IDs/images/volumes/network giữ nguyên |
| Đích SQL | aicos_payment_test / public; bootstrap probe user postgres; DockerExec pin ID |
| PostgreSQL version | 180006 (18.6) |
| System identifier | 7701931530273472557, khớp manifest + pg_controldata + SQL |
| transaction_read_only | on; BEGIN READ ONLY, không SQL mutation |
| Schema public | 38 bảng / 244 cột / 38 PK / 61 FK / 66 indexes |
| Invalid indexes / unvalidated constraints | 0 / 0 |
| Schema definitions | Khớp artifact đã pin sau chuẩn hóa hẹp 8 CHECK casts PG18.4/18.6 đã review ở D1-B |
| Identity schema | Đủ 7 bảng và columns/constraints/indexes được đọc lại từ catalog |
| Rows của 7 bảng Identity | tenants/branches/users/roles/tenant_users/staff_assignments/refresh_sessions đều 0 |
| 11 service roles | Tất cả NOLOGIN, password NULL; không superuser/createdb/createrole; CONNECT/USAGE đạt |
| identity_svc table ACL | SELECT đúng 7 bảng, INSERT đúng 5 bảng; không full UPDATE/DELETE/TRUNCATE/REFERENCES/TRIGGER/MAINTAIN |
| identity_svc column UPDATE | Chỉ refresh_sessions.revoked_at_utc/replaced_by_session_id/version |
| Redis TEST | PONG, DB0 có 0 keys |
| Gate C trong thao tác đã duyệt | 13 container IDs/images/StartedAt/environment không đổi so với snapshot ngay trước start test; Redis healthy |
| Gate C so với lịch sử D1-A/B | IDs/images khớp; 13 StartedAt đã khác từ lần Docker resume trước đó, không đổi/ghi đè bằng chứng này |

Không có dữ liệu account hay JWT được tạo. Probe dùng SELECT metadata/counts và
schema-only pg_dump; không gọi app APIs. Public count 38/244 là live; row counts
mới chỉ áp dụng 7 bảng Identity, không tuyên bố đã fingerprint main hoặc 31 bảng khác.
Không kết nối PostgreSQL Windows/main và không chạy business test suite.

Evidence mới, không secrets, ngoài Git:
`%LOCALAPPDATA%/AICOS/gate-d/d1c_readonly_20261029_140640/` gồm result.json,
identity-readonly-audit.json, schema-only.sql, gate-c-before.json, gate-c-after.json
và historical-started-at-differences.json. Không ghi đè D1-A/B evidence.

Dừng sau audit này. Các bước C1–C6 ở mục 10 vẫn cần phê duyệt riêng; không tự
bật LOGIN cho identity_svc, tạo credentials/fixtures hoặc start Identity/Gateway TEST.

### Lịch sử khi test containers chưa được phép start

### Cập nhật sau khi chủ dự án bật Docker — 2026-10-29

Đã kiểm tra READ ONLY bằng docker version/ps/inspect, không start/restart/recreate:

- Docker Engine truy cập được, Server version 29.7.2.
- aicos-gate-d-postgres và aicos-gate-d-redis đều Exited (255), OOMKilled=false,
  RestartPolicy=no. FinishedAt khoảng 20:51:47 ngày 29/10 (Asia/Saigon).
  RestartPolicy=no giải thích việc bật Engine không tự chạy lại hai container test;
  exit 255 tự nó chưa chứng minh nguyên nhân dừng.
- ID/image của cả hai khớp manifest; đúng dedicated volumes, compose project
  aicos-gate-d, network ID khớp và internal=true; không publish host ports.
- 13 Gate C containers đang running; Redis healthy theo docker ps. Tất cả giữ
  ID/image nhưng StartedAt khác evidence D1-A/B: 12 containers khoảng 20:51:50,
  Workforce 21:00:05. Chưa xác định nguyên nhân riêng của mốc Workforce.
- Không sửa snapshot cũ hoặc báo preservation StartedAt PASS. Guard dùng baseline
  StartedAt cũ phải dừng; cần đối chiếu snapshot hiện tại trong bước audit tiếp theo,
  giữ lại sai khác lịch sử, không bỏ kiểm tra ID/image.
- Chưa chạy pg_controldata/SQL vì PostgreSQL test đang dừng. System identifier,
  schema/rows và roles vẫn chưa được xác minh live lại. Không fallback Windows DB.

Để tiếp tục cần cho phép khởi động đúng hai container test hiện có:

```powershell
docker start aicos-gate-d-postgres aicos-gate-d-redis
```

Tại checkpoint lịch sử này lệnh chưa thực thi. Sau đó chủ dự án đã duyệt riêng và
đã start thành công như kết quả mới nhất bên trên. Lệnh tái sử dụng
container/volume, không compose up/build/recreate và không khởi động app TEST.
PostgreSQL có thể tự recovery nội bộ nếu cần; không chạy schema migration/seed.
Sau health PASS mới probe READ ONLY, pin cùng container/system identifier và kiểm
tra roles/ACL/counts; không bật LOGIN, tạo password/account/JWT hoặc chạy business API.
Nếu container health/identity không đạt thì dừng; không tự reset hoặc restore.

### Lịch sử probe trước khi Docker được bật

Docker API lúc đó không kết nối được:

```text
npipe:////./pipe/dockerDesktopLinuxEngine
The system cannot find the file specified.
```

Guard dừng ngay khi inspect Docker resource, trước SQL connection. `docker version`
không có Server; `docker ps -a` cũng không truy cập Engine. Chưa đủ bằng chứng để
kết luận vì sao Engine không hoạt động; không suy ra container/database bị mất.
Không tự bật Docker Desktop, start/restart/recreate container hoặc fallback sang
PostgreSQL Windows. Chỉ đọc evidence đã lưu để tiếp tục phần audit độc lập.

| Thành phần | Giá trị đã pin trong D1-A/D1-B | Trạng thái lượt này |
|---|---|---|
| Database / schema | aicos_payment_test / public | SQL live PASS sau start được duyệt |
| Cluster identifier | 7701931530273472557 | Manifest + pg_controldata + SQL khớp |
| PostgreSQL container | aicos-gate-d-postgres | Healthy, không recreate |
| Container ID | 6219c61c40c586676402a3163780b98dd57e9bdcdac9650a0a9f39241093d43b | Live inspect khớp manifest; không chỉ tin container name |
| Image / version | PostgreSQL 18.6; digest a1d02e4bd40c94d3bf2bdd3678c137388e76d9efcd23c285e9429d336a834b44 | D1-B evidence |
| Network | aicos-gate-d_internal; internal=true; ID 058719a762be95ed2cef5bcb8a6918320ac0df5d8050d0b7e05c2787ae1a6493 | Live inspect khớp |
| Volume PG / Redis | aicos-gate-d_pg18-data / aicos-gate-d_redis-data | Live inspect đúng tên/mount/project |
| Host ports | Không publish PG/Redis; SQL qua DockerExec pinned ID → gated-postgres:5432 | Live inspect không có host bindings; chưa SQL |
| Baseline | 38 bảng / 244 cột; 38 PK / 61 FK / 66 indexes; 0 rows ở D1-B | Schema counts live khớp; row counts live chỉ xác minh 7 bảng Identity rỗng |
| 11 service roles | NOLOGIN, chưa có password theo D1-B | Live xác nhận cả 11 NOLOGIN/password NULL |
| Gate C | 13 containers được bảo toàn ở D1-B | Hiện running, ID/image khớp; StartedAt khác baseline cũ như ghi ở trên |

Evidence offline: `%LOCALAPPDATA%/AICOS/gate-d/d1b_20261029_140000/manifest.json`
và `result.json` ghi PASS_SCHEMA_ACL, RuntimeLoginEnabled=false, FixturesCreated=false,
BusinessExecutionAuthorized=false. Không sửa hoặc ghi đè evidence D1-A/D1-B.

Schema-only artifact vẫn khớp SHA-256
`CC6F19A5BAE3A8ACBCB096EC110A7D172B4C86D63C37E1AE18CF61C8E30158A3`.
DPAPI cache test tồn tại; lượt này không mở/decrypt secrets, không tạo hoặc phát JWT.
Không kết nối aicos_db hoặc aicos_payment_test trên Windows; không có main fingerprint mới.

## 2. Schema Identity và quyền hiện có

IdentityOrganizationDbContext khai báo 7 DbSet và mapping `public`. Artifact đã pin
có **7 bảng / 42 cột / 7 PK / 9 FK / 17 indexes** thuộc nhóm này; gồm 3 UNIQUE
constraints và 7 indexes riêng ngoài indexes hỗ trợ PK/UNIQUE. Đây là kiểm kê file.

| Bảng | Vai trò / ràng buộc quan trọng | Quyền identity_svc theo ACL đã review |
|---|---|---|
| tenants | UUID PK; code UNIQUE; status; scope tenant | SELECT |
| branches | UUID PK; tenant_id FK; (tenant_id,code) unique index; status mặc định INACTIVE | SELECT |
| users | UUID PK; email UNIQUE; password_hash text nullable; status | SELECT, INSERT |
| roles | UUID PK; code UNIQUE; vai trò nghiệp vụ, khác PostgreSQL role | SELECT, INSERT |
| tenant_users | FK tenant/user/role; (tenant_id,user_id,role_id) unique index | SELECT, INSERT |
| staff_assignments | FK branch/tenant_user; (branch_id,tenant_user_id) unique index | SELECT, INSERT |
| refresh_sessions | FK user/branch và self-reference replaced_by_session_id; token_hash char(64) unique; version UUID | SELECT, INSERT; UPDATE revoked_at_utc/replaced_by_session_id/version |

Refresh timestamps là timestamptz; created_at ở users/branches/tenants là timestamp
without time zone. Version là concurrency token của EF. Mật khẩu phải qua
`PasswordHasher<UserAccount>` thật; refresh token lưu SHA-256 hash, không plaintext.
Không cần thêm schema/migration cho scope Identity đã có. Không sửa migration đã chốt.

identity_svc không có INSERT tenants/branches, DELETE hoặc UPDATE các bảng account.
Không nới runtime ACL để seed/cleanup cho tiện. D1-B ghi nhận effective TEMPORARY
kế thừa PUBLIC; không tự REVOKE PUBLIC hoặc coi direct REVOKE là chặn kế thừa.

### Service role nào cần LOGIN?

**Chỉ identity_svc cần LOGIN** cho Identity TEST và các SQL assertions bằng cùng
runtime role. Mười roles menu_svc, table_reservation_svc, ordering_svc, payment_svc,
inventory_svc, kitchen_svc, workforce_svc, reporting_svc, customer_svc, ai_svc giữ NOLOGIN.
Gateway và HTTP runner không cần PostgreSQL service role hoặc database password.

Bootstrap/cleanup đề xuất dùng phiên admin TEST hiện có qua DockerExec pinned ID,
chỉ với helper/SQL được review và phê duyệt riêng. Không thêm role thứ 12 ở phương án
tối thiểu; không đưa admin credential vào app/HTTP runner. Các assertion SQL thông
thường dùng identity_svc; metadata đặc quyền như pg_control_system dùng bootstrap
probe đã pin, không cấp thêm quyền system function cho runtime role.

## 3. API Identity thực tế

Prefix: `/api/v1/identity`. Không có public register hoặc GET staff detail/list.

| API | Authentication / nghiệp vụ | Ghi dữ liệu thực tế |
|---|---|---|
| POST /auth/login | Anonymous; email/password, assignment/branch hợp lệ; hỗ trợ chọn branch | Login hoàn tất INSERT refresh_sessions |
| POST /auth/refresh | Anonymous, refresh token hợp lệ | Revoke session cũ và INSERT replacement; một số nhánh lỗi cũng revoke |
| POST /auth/logout | Anonymous, sở hữu refresh token | Revoke refresh session; unknown/revoked token hợp lệ về format trả 204 |
| GET /auth/me | Staff policy; truy vấn lại active assignment/user/branch/membership | Chỉ đọc |
| POST /staff | Management policy; Manager chỉ branch hiện tại, không tạo Manager; SystemAdmin được tạo Manager | INSERT users/tenant_users/staff_assignments; có thể INSERT roles nếu thiếu |

Login: email được trim/lowercase; password non-empty tối đa 200 ký tự. Đúng password
nhưng có nhiều assignment và chưa chọn branch trả 200 RequiresBranchSelection=true,
AccessToken/RefreshToken/Staff=null, không INSERT session. Branch không được gán trả
401 auth.branch_not_assigned; credentials sai/inactive trả 401 auth.invalid_credentials.

Provisioning: password 12–200 ký tự, email tối đa 255, fullName tối đa 200, BranchId
khác Empty. Chỉ tạo Manager/Waiter/Cashier/WarehouseStaff; không tạo SystemAdmin.
Duplicate email được kiểm tra toàn hệ thống, không cho cùng email khác tenant.
201 Location trỏ staff/{id} nhưng source chưa có GET tương ứng; không tự follow
Location rồi gọi 404 là lỗi login hay dựng mock endpoint để có 200.

Health GET /health/live, /health/ready; metadata GET /api/v1/identity/_info;
Swagger/OpenAPI khi bật cấu hình. Health ready kiểm tra PG và Redis TEST, không
chứng minh account/ACL/API nghiệp vụ đã PASS. Gateway dùng YARP, không Ocelot;
Gateway không có aggregated ready/Swagger trong source hiện tại.

## 4. Credentials và fixture tối thiểu đề xuất

**Danh sách dưới đây chưa tạo.** UUID/runId, password/hash và token chỉ được sinh
sau phê duyệt thực thi; các alias bên dưới không phải tài khoản đã tồn tại.

| Secret / cấu hình | Nhu cầu | Cách cung cấp sau phê duyệt |
|---|---|---|
| PostgreSQL TEST bootstrap password | Đã có từ D1-A, không tạo/reset | DPAPI private hiện có, chỉ helper pin container sử dụng |
| identity_svc TEST password | 1 password mới, riêng môi trường test | Prompt ẩn hoặc sinh ngẫu nhiên sau duyệt; SecureString/DPAPI ngoài Git |
| 10 fixture account passwords | Mỗi account một password riêng | Sinh an toàn sau duyệt, hash bằng PasswordHasher thật; không đưa vào SQL/log/source |
| Tối đa 4 passwords cho accounts tạo qua API | 4 positive provisioning cases đề xuất | Chỉ trong memory/private secret store của runner, không body dump |
| JWT signing key TEST | Tái dùng reserved key D1-A nếu kiểm tra đạt | Không tạo key mới ở lượt này; không dùng key Gate C/Development/test constants |
| Access/refresh tokens | Chỉ Identity TEST phát sau duyệt API calls | Memory; không xuất token, hash/prefix token hoặc auth response vào báo cáo |
| Redis / Gateway DB credentials | Không cần credentials mới | Redis private hiện không password; Gateway không dùng DB |

Không gửi password qua chat. Không đưa secrets vào command arguments, Docker build
args, git-tracked env file hoặc full compose config/inspect output. Runtime env có
thể chứa secret trong container metadata; Docker access vẫn là quyền nhạy cảm.
Runner phải redact env/config/log, không khẳng định environment là secret vault.

### Fixture nền: 42 rows, không refresh sessions ban đầu

- 2 tenants ACTIVE: A và B; dữ liệu hoàn toàn tổng hợp.
- 4 branches: A1 ACTIVE, A2 ACTIVE, A-OFF INACTIVE, B1 ACTIVE.
- 5 business role rows: SYSTEM_ADMIN, MANAGER, CASHIER, WAITER, WAREHOUSE_STAFF.
- 10 users, 10 tenant_users, 11 staff_assignments như bảng dưới.
- 0 refresh_sessions, 0 dữ liệu của 31 bảng ngoài Identity; không fixture Menu/Table/Payment.

| Alias account | Business role | Branch assignment | Mục đích |
|---|---|---|---|
| admin-A | SYSTEM_ADMIN | A1 | Bootstrap admin, kiểm tra quyền tạo Manager |
| manager-A | MANAGER | A1 | Provision cùng branch, chặn khác branch/tenant |
| manager-B | MANAGER | B1 | Tenant B độc lập và kiểm tra chiều ngược |
| cashier-A | CASHIER | A1 | Login/me; POST staff bị 403 |
| waiter-A | WAITER | A1 và A2 | Multi-branch selection; me/branch claim |
| warehouse-A | WAREHOUSE_STAFF | A1 | Staff policy và Management denial |
| user-off | CASHIER | A1 | Chỉ users.status=INACTIVE |
| membership-off | CASHIER | A1 | Chỉ tenant_users.status=INACTIVE |
| assignment-off | CASHIER | A1 | Chỉ staff_assignments.status=INACTIVE |
| branch-off | CASHIER | A-OFF | User/membership/assignment ACTIVE, branch INACTIVE |

Tổng: 2+4+5+10+10+11=42 rows. Email dùng mẫu `<alias>+<runId>@gate-d.invalid`;
không dùng tên/email/số điện thoại của người thật. Mỗi user một membership, riêng
waiter-A có hai assignments thuộc cùng tenant. Không tạo quan hệ cross-tenant sai.
SystemAdmin vẫn cần assignment/branch cho luồng login/me hiện có; không giả định
Identity cấp token admin không branch chỉ vì helper JWT có tham số branch nullable.

Thứ tự bootstrap dự kiến: tenants → branches/roles/users → tenant_users →
staff_assignments, trong một transaction fixture được duyệt. Startup app không seed.
Fixture manifest ghi UUID từng row/expected counts/checksums, không password/hash/token.
Seed phải fail nếu baseline không rỗng hoặc có collision; không overwrite/upsert
dữ liệu không thuộc run. Writer chỉ được phép chạm 6 bảng fixture Identity này.

API positive provisioning tạo thêm tối đa 4 users + 4 memberships + 4 assignments:
Manager A tạo Cashier/Waiter/WarehouseStaff ở A1; SystemAdmin tạo Manager ở B1.
Roles đã có sẵn nên expected roles delta=0. Refresh sessions tăng đúng từng login/
refresh thành công, không cố định tổng tùy tiện. Mỗi request/case có row delta riêng.

## 5. JWT TEST và môi trường runtime dự kiến

| Thành phần | Giá trị đề xuất, chưa cấu hình |
|---|---|
| Jwt__Issuer | aicos-gate-d |
| Jwt__Audience | aicos-gate-d-clients |
| Jwt__SigningKey | Reserved DPAPI test key; phải khác Gate C và mọi fallback trong source |
| Jwt__ExpiryMinutes | 1 cho profile test; source cho phép 1–1440 |
| RefreshToken__ExpiryDays | 1; source tối thiểu 1 ngày, không phải 1 phút |
| Signing algorithm | Identity phát HS256 theo JwtTokenService |
| Validation | Signature, issuer, audience, lifetime; skew 30 giây |
| Claims | sub/name/role/branch_id/jti/iss/aud/nbf/exp; không có tenant_id claim |
| ASPNETCORE_ENVIRONMENT | Testing, tránh nạp fallback Development |
| Database__MigrateOnStartup | false, bắt buộc xác minh trước start |
| PostgreSQL app target | gated-postgres:5432 / aicos_payment_test / identity_svc / public |
| ConnectionStrings__Redis | gated-redis:6379, chỉ test network |
| Swagger__Enabled | true nếu cần OpenAPI trong test; không đưa secrets vào docs |

Identity và Gateway TEST phải dùng cùng test key/issuer/audience. Chọn issuer/audience
riêng để dễ phân biệt; không đổi môi trường chính. Trước start, so key ở memory chỉ
xuất boolean match/separate, không key/hash/prefix. Nếu cache không hợp lệ/mất quyền
DPAPI, dừng xin duyệt phương án thay thế, không fallback key Development.

Positive JWT chỉ lấy từ POST login/refresh của Identity TEST. Sai chữ ký: lấy token
Identity thật, flip một bit trong signature bytes rồi base64url encode lại; không
đổi claims/expiry, không tự ký bằng key khác và không dùng malformed string thay
cho bằng chứng cryptographic rejection. Đây là negative input, không positive token.

Hết hạn: dùng JWT vừa cấp, gọi me thành công trước exp, chờ qua exp+30 giây skew
cộng khoảng đệm 2 giây, rồi me phải 401 ở direct và Gateway. Không sửa exp, fake clock
hoặc chỉnh thời gian Windows. Các case khác login/refresh mới theo phase để TTL
ngắn không tạo false failures. Refresh expiry một ngày không được coi là đã test
chỉ nhờ access JWT hết hạn; cần fixture expired-session riêng nếu duyệt mở rộng.

### Topology test cần chuẩn bị sau phê duyệt

Giữ nguyên hai container infra/volumes hiện có, không merge Compose Gate C/base.
Thêm riêng gated-identity (container aicos-gate-d-identity), gated-gateway và một
HTTP runner internal-only. Không publish host ports, không host-gateway hay network
Gate C. Identity/Gateway dùng cổng nội bộ 8080 theo Dockerfile; Gateway chỉ route
`/api/v1/identity/{**catch-all}` tới `http://gated-identity:8080`, không fallback
localhost/identity Gate C. Chỉ depends_on test dependencies, không start service khác.
Gateway nhận requests không bearer rồi để Identity enforce policy; bearer sai bị
edge reject trước proxy. Không thêm anonymous override hoặc tắt authorization.

D1-A/B guard hiện chỉ chấp nhận đúng 2 network members; D1-B verifier cũng yêu cầu
11 NOLOGIN/password NULL. Khi thêm app hoặc bật identity LOGIN, guard cũ phải reject.
Cần guard/manifest D1-C riêng với danh sách 2 infra + Identity/Gateway/runner, pinned
IDs/images/network và chỉ identity_svc LOGIN. Không nới guard cũ thành allow-any.
Trước SQL metadata đặc quyền kiểm tra pg_controldata+SQL cùng cluster; trước app
assertions kiểm tra current_database/current_user/current_schema, server address,
role ACL và connection config. Không GRANT quyền đặc quyền cho service để qua guard.

## 6. Ma trận kiểm thử đề xuất — tất cả NOT RUN

Mỗi case gọi HTTP thật direct và qua Gateway khi phù hợp; kiểm tra nội dung,
claims và rows thật, không chỉ status code. Header bearer của case anonymous phải
được xóa; Gateway reject bearer cũ/sai ngay cả trên URL login anonymous.

| Case | Thực hiện sau duyệt | Expected HTTP / state |
|---|---|---|
| A1 | Login từng staff role với password đúng/branch đúng | 200, RequiresBranchSelection=false, đúng hồ sơ/claims; +1 refresh row hash-only mỗi login |
| A2 | Email uppercase/có khoảng trắng | 200 về cùng user, normalized email; không account mới |
| A3 | waiter-A không chọn branch | 200, lựa chọn A1/A2, không access/refresh/profile; session delta=0 |
| A4 | waiter-A chọn A1 rồi A2; user một assignment bỏ branch | 200, branch claim/profile/session FK đúng lựa chọn |
| A5 | Sai password, unknown email, 4 account inactive độc lập | 401 auth.invalid_credentials, không token hoặc INSERT session |
| A6 | Password đúng nhưng branch không được gán/khác tenant | 401 auth.branch_not_assigned, không token/session mới |
| A7 | Email sai format, password rỗng/>200 | 400 validation_failed, không SQL mutation |
| J1 | me bằng token Identity thật cho 5 Staff roles | 200, UserId/role/tenant/branch đúng DB; không row delta |
| J2 | me không token / malformed bearer | 401 direct và Gateway; không row delta |
| J3 | me với signature bị đổi một bit, claims giữ nguyên | 401 direct và Gateway; không row delta |
| J4 | me trước rồi sau exp+skew | 200 rồi 401; token do Identity cấp, không giả lập expiry |
| R1 | Refresh token hợp lệ | 200, token mới khác cũ; +1 replacement row; cũ revoked/replaced_by; version đổi |
| R2 | Reuse old/revoked/unknown refresh token | 401 auth.invalid_refresh_token, không replacement mới |
| R3 | Hai refresh đồng thời cùng token, giới hạn 2 requests | Một 200, một 401 theo SessionService; chỉ một replacement committed |
| R4 | Refresh rỗng/>512 ký tự | 400; không row delta |
| L1 | Logout active refresh; logout lại/unknown token đúng format | 204; lần đầu revoke/version đổi, lần sau no-op; refresh sau logout 401 |
| P1 | Management positive 4 target roles như scope fixture | 201, +1 user/membership/assignment mỗi case, roles không tăng; account mới login được |
| P2 | Manager A tạo account ở A2 hoặc B1; tạo Manager | 403; user/membership/assignment/session không tăng |
| P3 | Cashier/Waiter/WarehouseStaff gọi staff; không token | 403 với JWT hợp lệ của các role; 401 khi thiếu token |
| P4 | Duplicate email kể cả khác chữ hoa/thường; validation sai | 409 identity.email_exists hoặc 400 validation_failed theo case; không partial rows |
| P5 | SystemAdmin target branch không tồn tại/INACTIVE | 404; không tạo rows, không nhầm branch denial 403 của Manager |

R3 không mặc định 409 chỉ vì annotation endpoint có ProducesProblem(409): source
catch DbUpdateConcurrencyException rồi trả auth.invalid_refresh_token/401. Nếu
bất ngờ 500 hoặc hai replacement committed thì FAIL và giữ evidence.

Login không idempotent: hai login hợp lệ độc lập được phép tạo hai sessions.
Logout không blacklist access token; không kỳ vọng JWT vừa logout lập tức 401.
me chỉ xem caller hiện tại, không có tham số để xem user/tenant khác. AdminOnly
policy không có endpoint Identity tương ứng; Customer/Guest/AiBusinessAssistant
không được login staff cấp token, không tự ký token của họ để gọi là full integration.

PASS phải đủ HTTP body/claims/DB delta, auth và branch isolation; tất cả case bắt
buộc thực sự chạy, không SKIP. Không có 5xx/502/503/504 ngoài dự kiến. Nếu guard,
key/role/target/migration flag không đạt: BLOCKED trước mutation. Không sửa code,
schema hay tiêu chí test giữa chừng để biến failure thành PASS.

## 7. Vấn đề từ source cần chốt trước case mở rộng

Đây là findings audit, **chưa tái hiện bằng API**:

| Finding | Bằng chứng / tác động cần kiểm tra | Quyết định cần duyệt |
|---|---|---|
| Tenant INACTIVE chưa được lọc ở login/ActiveAssignment | Login/me/refresh kiểm tra user/membership/assignment/branch ACTIVE; provisioning có kiểm tra tenant ACTIVE | Chốt tenant khóa có phải chặn login/refresh/me; duyệt thêm fixture/case nếu cần, không mặc định PASS |
| Quản trị bằng access JWT cũ | me đọc lại active assignment, nhưng CreateStaff dùng role/branch từ JWT, không kiểm tra lại actor active assignment | Chốt thu hồi quyền/disable account trong thời gian token còn hạn; case SQL toggle test-only cần duyệt riêng |
| Nhiều memberships/roles cùng user/branch | Unique hiện tại là branch_id+tenant_user_id; me/refresh dùng SingleOrDefault cho user+branch | Chốt hỗ trợ multi-role; không seed dữ liệu gây mơ hồ trong fixture tối thiểu, bổ sung case sau duyệt |
| Đồng nhất tenant giữa membership và branch | Hai FK độc lập không tự ràng buộc cùng tenant | Fixture writer bắt buộc validate tenant khớp; case dữ liệu sai quan hệ là scope mở rộng |

Không tự patch các finding này trong lượt chuẩn bị. Bốn account inactive trong
fixture nền không thay thế tests disable actor sau login hoặc tenant bị khóa.
Wrong issuer/audience nhưng chữ ký hợp lệ cần cơ chế phát token test được review
riêng; sửa claims làm signature sai không chứng minh độc lập issuer/audience check.

## 8. Phân loại tests và khoảng trống runner

| Nhóm | Phân loại đúng | Giới hạn / trạng thái hiện tại |
|---|---|---|
| HTTP Identity TEST → real PG + role identity_svc → Gateway TEST | Integration/runtime thật dự kiến | Chưa có stack app/fixtures/runner D1-C; NOT RUN |
| PostgresAuthenticationIntegrationTests | Real PostgreSQL + in-process WebApplicationFactory; token lấy qua login thật | Cùng connection cho seed/app/ExecuteDelete cleanup; không chứng minh runtime least-privilege/Gateway; chưa chạy |
| PostgresStaffProvisioningIntegrationTests | Real PostgreSQL + login/provisioning in-process thật | Tương tự; cần quyền setup/delete khác service role, không nới ACL để chạy; chưa chạy |
| AuthenticationServiceTests | EF InMemory/service logic, TestCurrentUser giả; có endpoint pipeline tests | Không chứng minh PostgreSQL/FK/concurrency/runtime ACL |
| JwtTokenServiceTests | Unit tests gọi issuer helper trực tiếp | Token tự tạo trong test, không chứng minh login/assignment |
| GatewayTests valid bearer | Token tự ký qua JwtTokenService; chỉ assert !=401 | Có thể chấp nhận 502/503/504; không được tính routing/business PASS |
| Một số PostgreSQL service suites khác | DB thật nhưng fake clients hoặc tự ký token | Ngoài D1-C; không blanket run để lấy tổng xanh |
| 28 metadata guard + 8 schema-text guard cases | Synthetic metadata/text | D1-A/B evidence, không PostgreSQL business tests; lượt này không chạy lại |
| PostgresIntegrationFactAttribute | Có Skip khi thiếu AICOS_TEST_POSTGRES | Skip xảy ra trước REQUIRE check trong test body; không coi exit 0 là integration PASS |

Runner D1-C cần fail-closed trước discovery/execution: đích/server/cluster/schema,
role/login, service URLs, JWT config và expected test list. Nếu tái dùng xUnit phải
nạp AICOS_TEST_POSTGRES đúng đích test trong container và REQUIRE=true rồi đọc TRX:
executed/passed/failed/skipped theo tên case, không chỉ exit code; required skips=0.
Không chỉ thêm DB-name guard vì Windows có test database cùng tên.

Ưu tiên HTTP runner trên test network với fixture helper tách biệt. App không dùng
admin; setup không giả DB response; token positive chỉ từ Identity. Evidence ghi
case ID, expected/actual/errorCode/correlation ID, row deltas và nguồn token; không
ghi raw JWT/refresh/password/hash. Lượt này không gọi API hoặc chạy bất kỳ suite nào.

## 9. Cleanup / rollback đề xuất, chưa được phép thực hiện

1. Trước fixture, xác minh target và lưu checkpoint TEST schema/ACL/empty-state,
   role attributes + manifest; không backup/copy dữ liệu main. Sau seed lưu
   checkpoint fixtures và UUID manifest riêng, có DACL private.
2. Khi FAIL: dừng runner/quiesce riêng các app TEST được duyệt, chụp evidence rồi
   giữ dữ liệu để kiểm tra. Không tự gọi cleanup trong finally hoặc reset test DB.
3. Sau duyệt cleanup riêng: dry-run liệt kê UUID, row counts và incoming FK của
   fixture lẫn accounts API đã tạo. Có reference ngoài run thì STOP, không CASCADE.
4. Targeted cleanup chỉ trên cluster pinned và các UUID trong manifest: toàn bộ
   refresh_sessions của run (gồm replacements) → staff_assignments → tenant_users
   → users → branches → tenants; roles business của run xóa cuối nếu không còn
   reference. Self-FK refresh phải xóa trọn chain thuộc run trong cùng statement/
   transaction; nếu có chain ngoài run thì dừng, không sửa reference ngoài scope.
5. Xác minh row delta đúng expected, 38 bảng/244 cột/ACL nguyên vẹn và không rows
   ngoài run thay đổi. Runtime identity_svc không nhận DELETE/DDL để cleanup.
6. Nếu được duyệt trả identity_svc về NOLOGIN: drain/đóng các connection TEST của
   chính run trước, kiểm tra hết sessions. NOLOGIN không tự ngắt connection cũ.
   Không đụng 10 role còn lại hoặc Gate C; không tự reset password/key.

Mọi transaction SQL setup/cleanup chỉ rollback chính transaction đó; không bọc
HTTP requests đa connection rồi giả định rollback toàn bộ. D1-B schema-only dump
không phải data checkpoint để restore đè tùy tiện. Restore chỉ là phương án sau
review/phê duyệt riêng nếu cleanup không đủ; không pg_restore --clean, DROP, TRUNCATE,
volume delete, compose down -v hoặc restore đè Windows DB. Giữ schema/11 roles hiện có.

## 10. Thứ tự và các phê duyệt còn cần

C0 và C1 đã hoàn tất theo các phê duyệt bổ sung, xem kết quả live/C1 bên trên.
C2–C6 vẫn chưa thực hiện và cần phê duyệt riêng. Bảng dưới giữ thứ tự/scope:

| Bước | Nội dung cần xác nhận | Điểm dừng / verification |
|---|---|---|
| C0 | Docker Desktop/Engine hoạt động trở lại; review mọi thay đổi StartedAt do Engine resume, không tự recreate Gate C | Fresh READ ONLY pin container/image/network/volume, pg_controldata và SQL identifier; roles/ACL/row counts; FAIL thì dừng |
| C1 | Chuẩn bị helper/manifest D1-C, test Compose/app configs và runner; phân phối reserved JWT test key | Review guard cho từng stage/merged config/private bool checks, không fallback main; chưa bật LOGIN hoặc HTTP business |
| C2 | Tạo riêng password TEST cho identity_svc, giữ NOLOGIN lúc cấu hình; sau ACL PASS chỉ bật LOGIN role này | Kết nối thật đúng DB/user/schema, 10 roles khác vẫn NOLOGIN; dùng guard D1-C đã review, không CREATE ROLE/GRANT/REVOKE lại |
| C3 | Bootstrap đúng 42 fixture rows / 10 users bằng PasswordHasher và admin TEST helper đã review | Guard cùng connection, transaction/row counts/FK, 0 refresh rows và 31 bảng khác nguyên vẹn |
| C4 | Start riêng Identity/Gateway TEST và runner, không recreate infra/Gate C; chỉ test network, no host ports | Health/metadata/OpenAPI; migrations=false/no seed, Identity dùng identity_svc, đúng Redis/route TEST |
| C5 | Cho phép A/J/R/L/P cases, POST login/refresh/logout/staff; tối đa 4 accounts API mới, JWT Identity thật, bounded 2-request refresh race | Case status+claims+persisted deltas; required cases/skips rõ; dừng nếu guard/5xx/ACL/security invariant sai |
| C6 | Cleanup hoặc restore TEST / trả identity_svc NOLOGIN | Phê duyệt riêng sau xem UUID/FK/count dry-run; mặc định giữ evidence/dữ liệu |

Không coi C2 là quyền seed/start app hoặc C3 là quyền chạy mutation tests. C0 chỉ
là điều kiện để xác minh lại, không cấp phép restart các container Gate C. Nếu
Engine resume làm container start times khác evidence cũ, báo cáo trạng thái mới
và chờ chốt baseline; không thay snapshot cũ hoặc âm thầm gọi preservation PASS.

Hiện đã sẵn sàng: audit source/schema artifact/ACL, live READ ONLY verification và
kế hoạch fixtures/JWT/cases/cleanup cùng C1 guards/config/preflight runner.
Còn thiếu: password/LOGIN identity_svc, fixtures, business HTTP runner và app TEST
runtime. Không có Identity API test PASS/FAIL
mới; business tests là **NOT RUN/CHỜ DUYỆT**, không phải Identity runtime FAIL.

## 11. Nguồn tham chiếu

- [Gate D plan](GATE_D_TEST_PLAN.md), [D1-A](GATE_D1_A_RESULT.md), [D1-B](GATE_D1_B_RESULT.md).
- [Identity endpoints](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Authentication/AuthenticationEndpoints.cs),
  [login service](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Authentication/AuthenticationService.cs),
  [session service](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Authentication/SessionService.cs),
  [refresh factory](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/Authentication/RefreshTokenFactory.cs).
- [Provisioning](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Features/StaffProvisioning/StaffProvisioningService.cs),
  [DbContext](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Persistence/IdentityOrganizationDbContext.cs),
  [Identity mapping](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Persistence/IdentityModelConfiguration.cs),
  [refresh mapping](../src/Services/IdentityOrganization/AICOS.IdentityOrganization.Api/Persistence/RefreshSessionConfiguration.cs).
- [JWT validation/policies](../src/BuildingBlocks/AICOS.BuildingBlocks/Hosting/AuthenticationExtensions.cs),
  [issuer](../src/BuildingBlocks/AICOS.BuildingBlocks/Security/JwtTokenService.cs),
  [JWT options](../src/BuildingBlocks/AICOS.BuildingBlocks/Security/JwtOptions.cs),
  [Gateway middleware](../src/Gateway/AICOS.Gateway/BearerTokenValidationMiddleware.cs).
- [Test infra Compose](../docker-compose.gate-d.yml), [target/resource guards](../docker/gate-d/GateD.Common.psm1),
  [schema manifest](gate-d/schema-source.json), [test ACL](gate-d/02_runtime_acl.review.sql).
- [Postgres auth test](../tests/AICOS.Services.SmokeTests/PostgresAuthenticationIntegrationTests.cs),
  [Postgres provisioning test](../tests/AICOS.Services.SmokeTests/PostgresStaffProvisioningIntegrationTests.cs),
  [skip attribute](../tests/AICOS.Services.SmokeTests/PostgresIntegrationFactAttribute.cs),
  [Gateway tests](../tests/AICOS.Services.SmokeTests/GatewayTests.cs).

**Dừng sau C1 D1-C.** Chỉ start hai container infra TEST theo phê duyệt bổ sung;
không SQL DDL/DML hay thay roles/credentials/JWT, không migration/seed/test nghiệp vụ,
không start app TEST hoặc restart/recreate Gate C, không sửa Mobile,
không commit/push. Chờ chủ dự án xác nhận các bước thực thi cụ thể ở mục 10.
