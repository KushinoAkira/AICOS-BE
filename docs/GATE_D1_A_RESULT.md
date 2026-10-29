# Gate D1-A — Kết quả chuẩn bị hạ tầng test cô lập

Ngày: 2026-10-29 (Asia/Saigon, UTC+07).
Kết quả cuối: **PASS_INFRA_ONLY**. Dừng sau D1-A; D1-B và nghiệp vụ chưa được duyệt/chạy.

## 1. Resource đã tạo

| Resource | Giá trị thực tế |
|---|---|
| Compose project | aicos-gate-d, file standalone docker-compose.gate-d.yml |
| PostgreSQL container | aicos-gate-d-postgres, HEALTHY |
| PostgreSQL image/version | Local image pin digest a1d02e4bd40c94d3bf2bdd3678c137388e76d9efcd23c285e9429d336a834b44, PostgreSQL 18.6 |
| PostgreSQL container ID cuối | 6219c61c40c586676402a3163780b98dd57e9bdcdac9650a0a9f39241093d43b |
| PostgreSQL data volume | aicos-gate-d_pg18-data → /var/lib/postgresql; PGDATA=/var/lib/postgresql/18/docker |
| Database/schema | aicos_payment_test/public, bên trong PostgreSQL TEST mới |
| Cluster system identifier | 7701931530273472557 |
| Redis container | aicos-gate-d-redis, HEALTHY |
| Redis image | Local Redis 7 image pin digest d9436322172a5b482c187bf1f363d3c4fea086fcca6b9c8a94de8f0f39ed3193 |
| Redis container ID | 7c15a4a6ccdd747ccb8821edbfee24f3ea550f5685f210dace2198c2f71bffba |
| Redis data volume | aicos-gate-d_redis-data → /data |
| Network | aicos-gate-d_internal, internal=true, đúng hai container test |
| Network ID | 058719a762be95ed2cef5bcb8a6918320ac0df5d8050d0b7e05c2787ae1a6493 |
| Host ports | Không publish PostgreSQL/Redis; chỉ internal 5432/6379 trong network riêng |
| Giới hạn | PostgreSQL 256 MiB/0.5 CPU, Redis 96 MiB/0.25 CPU; không đổi Docker global |
| App containers test | Chưa tạo/khởi động Identity, Gateway hoặc service nghiệp vụ nào |

Kiểm tra đầu tiên không thấy xung đột project/container/network/volume; port dự
kiến 15432/18080 trống. Không tái sử dụng resource Gate C hay database host.
Image đã có sẵn, không pull/build. PostgreSQL bootstrap chỉ tạo system catalog,
default infrastructure role postgres và database test rỗng theo entrypoint;
không tạo tài khoản ứng dụng AICOS/service roles, không chạy migration/seed.

## 2. Điều chỉnh network/port và lỗi tooling đã xử lý

Giữ network internal=true. Khi thử mapping 127.0.0.1:15432, runtime không tạo
published binding hiệu lực trong NetworkSettings; TCP probe 15432 thất bại.
Đây không phải xung đột với PostgreSQL Windows, và không kết nối SQL tới host.

Đã bỏ mapping host không có hiệu lực; runner dùng DockerExec pin container ID,
kết nối TCP gated-postgres:5432 từ đúng container test. Không nới network hoặc
thêm host-gateway. Chỉ PostgreSQL TEST mới được recreate một lần để bỏ mapping;
volume và system identifier giữ nguyên. Redis test không recreate; toàn bộ
container Gate C không restart/recreate.

Các lỗi tooling trước kết quả cuối:

1. Unit guard phát hiện PowerShell IDictionary adapter khi gán ConnectionString;
   chuyển sang CLR setter, sửa trước khi tạo hạ tầng.
2. Lượt readiness đầu dừng ở guard vì JSON array volume bị lồng một cấp; metadata
   thực tế đúng, chưa mở SQL connection. Đã sửa flatten, không bỏ check volume.
3. Lượt DockerExec SQL ban đầu gặp lỗi kết thúc heredoc/stdin Windows–Linux;
   sửa stdin LF. Chỉ chạy static SELECT trong transaction READ ONLY, không DDL/DML.
4. Guard/readiness cuối đã PASS; giữ nguyên các evidence FAIL lịch sử, không
   đổi chúng thành PASS hoặc reset database để chạy lại.

## 3. Bằng chứng cô lập và target guard

- Compose riêng chỉ có hai services, không merge base/external-db/Gate C.
- Container/project labels, ID/image, network ID/internal flag, đúng hai network
  members và named-volume destinations đều được kiểm tra trước SQL.
- Không bind source tree/init scripts/data của Windows hoặc volume Gate C.
- Không host port PostgreSQL/Redis, không network attachment vào aicos-be_default.
- PostgreSQL system identifier lấy từ pg_controldata trong container đã pin;
  query READ ONLY phải trả cùng identifier, đúng DB/user/schema/version/port.
- Mật khẩu PostgreSQL TEST sinh mới, không lấy từ main; runner đối chiếu cache
  mã hóa với environment test trước authentication. Không đổi credentials cũ.
- Reserved JWT signing key mới/riêng, không bằng key Gate C; chưa cấp cho app và
  chưa ký JWT. Signing key không phải access token.
- Secrets lưu SecureString qua Windows DPAPI ngoài Git; DACL private một rule,
  inheritance protected. Không plaintext secret file, argv, full env/config/log dump.
- Redis test hiện dùng network-private/no host port; PING không ghi key.
- Không có SQL connection tới aicos_db hoặc aicos_payment_test trên Windows.
  Không restore/copy database dump chứa dữ liệu người dùng.

Không coi chỉ tên aicos_payment_test là đủ: database cùng tên trên Windows
localhost:5432 không được phép sử dụng. Transport thật hiện là
DockerExecPinned/container ID, không phải host localhost:5432 hay 15432.

## 4. Guard và readiness đã thực thi

| Kiểm tra | Kết quả thực tế |
|---|---|
| Parse các PS1/PSM1 | PASS |
| Pure metadata/target guard tests | PASS 28 cases; synthetic metadata, không DB/business tests |
| Native Compose config validation | PASS; secrets chỉ nằm private/process memory |
| Docker Postgres health | HEALTHY |
| PostgreSQL connection | PASS thật, SCRAM bootstrap password, READ ONLY |
| Database / schema / user | aicos_payment_test / public / postgres |
| Server version number | 180006 |
| Server port / transport | 5432 trong container đã pin; DockerExecPinned |
| transaction_read_only | on |
| Cluster identifier | 7701931530273472557, khớp manifest + pg_controldata |
| Public application tables | 0 — schema chưa apply |
| AICOS runtime service roles | 0 — ACL/role scripts chưa chạy |
| Redis health / PING | HEALTHY / PONG thật |
| Redis DB0 keys | 0 |
| Schema artifact review | PASS checksum + 38 bảng/244 cột trong FILE, không phải live test DB |
| Gate C preservation | 10 .NET + AI + Gateway + Redis giữ ID/image/StartedAt, vẫn running; Redis healthy |

28 guard cases bao gồm chặn main DB, old Windows port 5432, host.docker.internal,
implicit/multi-host/alias target, cluster sai, container/image/project sai, volume
Gate C, public port, Redis publication, cross-network và Docker container không pin.
Đây là kiểm thử guard thực sự chạy, không phải business PostgreSQL suite.

Readiness script không có tùy chọn apply SQL, seed hoặc gọi test nghiệp vụ.
Nó cố ý chỉ chấp nhận D1-A: zero application tables/roles, hai network members,
BusinessExecutionAuthorized=false. D1-B cần stage manifest/guard mới được review;
không sửa guard D1-A để chấp nhận schema/app ngoài phạm vi.

## 5. Schema/ACL đã chuẩn bị, chưa áp dụng

[Xem gói review](gate-d/SCHEMA_ACL_REVIEW.md).

| File/artifact | Trạng thái |
|---|---|
| gate-d/schema-source.json | Pin schema-only backup sau Gate B; không cần kết nối main |
| %LOCALAPPDATA%/AICOS/backups/gate_a_20261026_213339/schema.sql | 38 tables/244 columns, payment_events, checksum khớp backup manifest |
| gate-d/00_target_guard.sql | Guard DB/PG18/public/expected system identifier/approval stage D1-B |
| gate-d/01_runtime_roles.review.sql | Đề xuất 11 NOLOGIN roles, không passwords; NOT EXECUTED |
| gate-d/02_runtime_acl.review.sql | Test-only copy ma trận bảng/cột; guard trước SQL, NOT EXECUTED |
| Test-GateDSchemaPackage.ps1 | Read-only file review/checksum checker; không apply |

Schema artifact SHA256:
CC6F19A5BAE3A8ACBCB096EC110A7D172B4C86D63C37E1AE18CF61C8E30158A3.

Artifact không có INSERT/COPY business data, role/password/owner/grant/extension
hoặc foreign-server creation. Function/trigger duy nhất được review là bảo vệ
payment_events append-only. Nguồn PG18.4, test PG18.6; chưa restore nên chưa
tuyên bố schema/FK/index/constraint/ACL hoạt động trên test.

**Còn thiếu trên live test DB:** toàn bộ 38 bảng/244 cột, các PK/FK/index/
constraint/audit trigger, 11 runtime roles và quyền theo ma trận, service login
passwords, fixtures và Identity/Gateway test. Không tự tạo phần nào trong D1-A.

Không sửa hai EF migration Identity đã chốt; không chạy Payment migration 00–06
hoặc rollback 90–92. Không dùng Database.sql gốc để giả định schema mới đầy đủ.

## 6. Phân loại kiểm thử

| Nhóm | D1-A |
|---|---|
| Real PostgreSQL | Readiness + SELECT metadata thật trong test container, READ ONLY |
| Real Redis | Health/PING/DBSIZE thật trên Redis test |
| Synthetic/mock metadata | 28 guard cases, ghi rõ không PostgreSQL |
| Mock service clients / self-signed test tokens trong legacy tests | Chỉ audit source, KHÔNG CHẠY |
| PostgreSQL business integration suite | NOT RUN; chưa có schema/roles/fixtures |
| Identity login/refresh/provisioning | NOT RUN; không application account/JWT |
| Tests bị framework SKIP | Không chạy suite nên không có số lượng skip mới; không lấy exit code xanh làm integration PASS |

Existing PostgresIntegrationFact có thể skip trước REQUIRE check; guard legacy
Payments mới kiểm tra tên DB, chưa kiểm tra server. Không chạy blanket dotnet
test hoặc tests/postgres/payment/bootstrap-schema.ps1 trên môi trường hiện tại.
D1-B/business runner phải sử dụng manifest/cluster guard và kiểm tra TRX/skips,
phân biệt real/fake clients và nguồn token Identity thật.

## 7. Files và evidence

File cấu hình/source tooling mới:

- [Compose infrastructure](../docker-compose.gate-d.yml)
- [Common guards](../docker/gate-d/GateD.Common.psm1)
- [Fresh startup](../docker/gate-d/Start-GateDInfrastructure.ps1)
- [Readiness-only runner](../docker/gate-d/Test-GateDReadiness.ps1)
- [Guard metadata tests](../docker/gate-d/tests/Test-GateDGuards.ps1)
- [Schema-package checker](../docker/gate-d/Test-GateDSchemaPackage.ps1)
- [Runner guide](../docker/gate-d/README.md)

Private run directory:
%LOCALAPPDATA%/AICOS/gate-d/d1a_20261029_104735.

- manifest.json: current test IDs/image/network/system identifier và Gate C snapshot.
- infra-secrets.dpapi.xml: encrypted SecureStrings; không mở/đưa vào Git/log.
- gate-c-before-resume.json: non-secret snapshot; đối chiếu thêm Gate C Gateway
  result lịch sử trước D1-A, tất cả IDs/images/start times khớp.
- d1a-result.json và d1a-completion.json: các FAIL_STOP lịch sử được giữ.
- **readiness-result.json: PASS_INFRA_ONLY cuối cùng, dùng để đọc kết quả hiện tại.**

Lệnh kiểm tra lại hạ tầng hiện có, không startup/mutate:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/Test-GateDReadiness.ps1 -RunDirectory "$env:LOCALAPPDATA/AICOS/gate-d/d1a_20261029_104735"
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/Test-GateDSchemaPackage.ps1
```

Start-GateDInfrastructure.ps1 chỉ dùng khi chưa có resources cùng tên; có conflict
sẽ STOP, không tự reuse/reset. Không rerun nó để retry môi trường đang tồn tại.

Không lấy fingerprint mới trên main trong D1-A vì không kết nối main. Bằng chứng
25 bảng không đổi là từ Gate C; D1-A xác nhận không thực hiện SQL/DML main và
giữ nguyên containers, không tự báo cáo một lần main fingerprint PASS mới.

## 8. Phê duyệt cần thiết cho D1-B

Chưa thực hiện bất cứ bước nào dưới đây:

1. Review và duyệt guarded schema-only restore helper trên đúng cluster
   7701931530273472557; checksum artifact, guard cùng kết nối, không data restore.
2. Duyệt apply schema rồi verify 38/244/PK/FK/index/constraints/audit trigger;
   review rõ nếu cần bổ sung migration history TEST, không sửa migration main.
3. Duyệt tạo runtime roles NOLOGIN và ACL TEST theo SQL review, thiết lập mật khẩu
   TEST riêng/kiểm tra quyền rồi mới LOGIN; không thay roles main.
4. Duyệt manifest/guard stage D1-B và container-based runner trên internal network.
   Host PG access hiện không được bật; muốn expose host port cần review riêng.
5. Nếu muốn tiếp tục Identity/Gateway test: duyệt riêng khởi động các app TEST,
   fixtures/accounts synthetic, phân phối reserved test key và login/JWT/API
   mutation scope. Phê duyệt schema/ACL không tự phê duyệt các phần này.
6. Snapshot/restore/cleanup test cần xác minh target và scope rõ; không tự DROP,
   reset database hoặc xóa volume, kể cả test database cũ.

**D1-A hoàn tất ở mức hạ tầng cô lập. Dừng, chờ phê duyệt D1-B.**
Không migration/seed, dữ liệu nghiệp vụ, app test account/JWT, business suite,
Mobile edit, Gate C restart/recreate, credentials/ACL chính hoặc commit/push.

