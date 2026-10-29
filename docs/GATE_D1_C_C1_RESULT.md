# Gate D1-C / C1 — Guard, cấu hình và preflight runner

Ngày: 2026-10-29 (Asia/Saigon).
Kết quả: **PASS_C1_PREPARATION**. Chỉ chuẩn bị và kiểm tra C1; C2–C6 chưa thực hiện.

## Phạm vi đã làm

- Thêm Compose apps-only cho Identity/Gateway TEST trên network Gate D hiện có.
  Không khai báo lại PostgreSQL/Redis/volume, không merge base/Gate C Compose.
- Không host ports, host-gateway, build, pull hoặc startup seed command. App dùng
  Testing, migrations=false, PG đúng aicos_payment_test/public/identity_svc,
  Redis gated-redis:6379, JWT issuer/audience riêng; secrets/images không fallback.
- Gateway có JSON cấu hình riêng, **chỉ một Identity route/cluster**, giữ path,
  destination http://gated-identity:8080. Dự kiến mount read-only vào appsettings.json
  để thay toàn bộ base config, tránh vô tình giữ 10 route Gate C. Chưa mount/start.
- Thêm guards riêng cho connection/JWT, roles, topology theo stage và Compose.
  Không chỉnh hoặc nới guard D1-A/B, không sửa Backend business endpoints.
- Thêm runner C1 chỉ config/preflight/READ ONLY; không có action tạo password,
  ALTER ROLE, fixture INSERT/DELETE, JWT issuance, Docker up/start hoặc API write.
  Đây **không** phải runner thực thi toàn bộ Identity integration cases C5.

## Files

| File | Trách nhiệm |
|---|---|
| docker-compose.gate-d.identity.yml | Hai app TEST, profile d1c-runtime, external test network, required secret/image variables |
| docker/gate-d/identity/gateway.identity.json | Full secret-free Gateway config, chỉ route Identity; SigningKey rỗng, bắt buộc env override |
| docker/gate-d/identity/config-only.env.example | File rỗng có comment, ngăn config validation nạp .env chính |
| docker/gate-d/identity/GateD.Identity.psm1 | Pure validation: target/config/key/role/topology/template |
| docker/gate-d/identity/readiness.sql | BEGIN READ ONLY, target guard cùng connection, catalog/count/ACL, không DDL/DML |
| docker/gate-d/identity/Prepare-GateDIdentity.ps1 | Pin resources và images, validate template, SQL read-only và xuất private manifest |
| docker/gate-d/tests/Test-GateDIdentityGuards.ps1 | 60 adversarial metadata/config cases và local Compose rendering |
| docker/gate-d/identity/README.md | Hướng dẫn C1 và điểm dừng trước C2/C4/C5 |

Cập nhật kế hoạch D1-C, Gate D plan, README tools và CHANGELOG để tham chiếu kết quả.
Không sửa Mobile, migrations/schema/service ACL hoặc credentials hiện có.

## Guard theo từng stage

| Guard | Điều kiện bắt buộc |
|---|---|
| Connection | Explicit gated-postgres:5432/aicos_payment_test/identity_svc/public; pool tối đa 6, không main/localhost/multi-host/admin/alias bypass |
| Runtime environment | Testing, migrations=false, đúng Redis; test issuer/audience, key đủ dài/match/different protected key và DB password; không service/Gateway override ngoài scope |
| Roles BeforeCredentials | Chính xác 11 roles, tất cả NOLOGIN/password NULL, không elevated/membership/ownership, CONNECT/USAGE đạt |
| Roles CredentialsReady | Chỉ identity_svc LOGIN/password set; 10 roles khác giữ NOLOGIN/password NULL; không tự chuyển state |
| InfraOnly | Đúng hai infra containers/network members, pin ID/image/labels/volumes; không host ports/cross-network |
| IdentityOnly / IdentityGateway / IdentityGatewayRunner | Đúng 3/4/5 members; mỗi app phải có pin ID/image riêng trong manifest; không tự nhận app lạ |
| App mounts | Identity/runner không mount; Gateway chỉ đúng file appsettings.json read-only với source đã pin; reject privileged/host networking |
| Gateway config | Chỉ route/cluster Identity, không anonymous override/transforms; không destination Gate C |
| Compose template | Chính xác hai apps, external test network; required placeholders chưa interpolate, không plaintext secrets/startup/build/published ports/privileged/unlimited resources |

Manifest C1 hiện Apps={} và RuntimeStarted=false. Guard runtime stages mới được
kiểm tra bằng synthetic metadata; chưa có app IDs hoặc mount-source pin thực tế.
C4 cần review tạo container/pin rồi mới start, không dùng manifest rỗng để auto-allow.
Runtime environment guard cũng mới test bằng synthetic strings, chưa nạp signing
key/password thật và chưa chứng minh cấu hình auth runtime PASS.

## Kết quả thực tế

| Check | Actual |
|---|---|
| PowerShell parse | 0 errors cho 3 PS1/PSM1 mới |
| Guard tests | **60 PASS**; metadata/config synthetic và Compose rendering local |
| Native docker compose config | PASS --no-interpolate, env-file riêng không secrets |
| Live PostgreSQL metadata | PASS READ ONLY, cluster 7701931530273472557; DB aicos_payment_test/public |
| Schema / Identity | 38 bảng/244 cột, đủ 7 bảng Identity, tổng rows Identity=0 |
| Roles / ACL | 11 NOLOGIN/password NULL; không elevated/membership/ownership; Identity table/column ACL đúng |
| Redis | PONG; chỉ readiness, không business event processing test |
| Existing DPAPI reservation | File tồn tại/DACL protected; không import/decrypt/distribute key |
| Image availability | Hai image IDs local đã pin từ container Gate C; không build/pull |
| Gate C / infra | Không start/restart/recreate; IDs/images/StartedAt/env Gate C giữ nguyên so C0 và trong C1; dedicated test infra giữ nguyên |
| App runtime credentials / LOGIN | NOT CREATED / NOT ENABLED |
| Fixtures / accounts / access-refresh JWT | NOT CREATED |
| HTTP login/refresh/logout/provisioning | NOT RUN |
| .NET business suite / xUnit skips | NOT RUN; không có số skip mới, không cộng tests chưa chạy vào PASS |

Negative test đầu tiên phát hiện guard env mới xử lý sai độ ưu tiên -or/-and nên
Services__Menu__BaseUrl chưa bị chặn. Đã sửa bằng nhóm điều kiện rõ ràng, bổ sung
chặn DB password dùng làm signing key và chạy lại đủ 60 cases PASS. Không có app
start hay DB mutation trong các lượt test lỗi/sửa này.

Các số PASS không đồng nghĩa login/JWT/concurrency đã hoạt động. Real SQL chỉ
catalog/ACL/counts trên PostgreSQL thật; các tình huống app/network/JWT guard là
synthetic. Không mock database để báo PostgreSQL integration PASS.

## Evidence và lệnh kiểm tra an toàn

Evidence cuối: `%LOCALAPPDATA%/AICOS/gate-d/d1c_c1_20261029_142251/`.
Gồm result.json, manifest.json, readonly-prerequisites.json,
compose-template-no-interpolate.json và gate-c-after.json. Không secrets/fixtures.
Manifest ghi SQL/config/module hashes, image IDs, reference C0 và quyền business=false.
Lượt đầu trước khi sửa guard giữ tại d1c_c1_20261029_141841; không thay historical evidence.

```powershell
# Metadata/config only: không tạo credentials hoặc gọi business API.
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/tests/Test-GateDIdentityGuards.ps1

# READ ONLY metadata, không phát sinh login/startup/seed.
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/identity/Prepare-GateDIdentity.ps1 `
  -ReferenceDirectory "$env:LOCALAPPDATA/AICOS/gate-d/d1c_readonly_20261029_140640"
```

Không chạy Compose up ở checkpoint C1. Profile d1c-runtime giúp tránh start mặc
định nhưng không thay thế phê duyệt. Khi C4 được duyệt mới dùng image IDs đã review,
credentials test, merged runtime config và explicit app names/--no-deps; không
merge Compose infra để tránh reconcile/recreate PG/Redis.

## Còn thiếu trước runtime/business tests

- **C2:** cung cấp riêng password test identity_svc theo prompt ẩn/private DPAPI,
  kiểm tra ACL rồi bật LOGIN duy nhất role này; không reset 11 roles, không grant lại.
- Reserved signing key chưa được phân phối. Khi được duyệt, load qua DPAPI private,
  so sánh separation/match ở memory rồi truyền runtime env; không in key/hash/prefix,
  không generate/rotate key Gate C, không dùng dummy strings của guard tests.
- **C3:** helper fixture/PasswordHasher + 42 rows/10 synthetic accounts phải được
  chuẩn bị/review theo scope đã đề xuất trước seed. Chưa viết/chạy fixture writer.
- **C4:** kiểm tra tài nguyên, source/image provenance hoặc build tag TEST riêng
  nếu cần, cấu hình/pin runtime app containers, Gateway config mount và health checks.
  Local image pin hiện chỉ chứng minh đúng binary đang có; không chứng minh mọi
  source change tương lai đã nằm trong image. Không sửa image Gate C.
- **C5:** runner HTTP nghiệp vụ, JWT Identity thật, case/state assertions và coverage
  A/J/R/L/P; preflight runner C1 không thay thế runner này.
- **C6:** cleanup/NOLOGIN/restore chỉ sau dry-run UUID/FK/counts và phê duyệt riêng.

Dừng sau C1. Lượt này không SQL mutation/migration/seed, không credentials/JWT,
không khởi động apps hay test nghiệp vụ, không commit/push hoặc thay đổi Mobile/main.
