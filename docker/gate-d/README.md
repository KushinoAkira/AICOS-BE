# Gate D infrastructure tools

D1-C / C1: [Identity preparation tools](identity/README.md) và
[kết quả C1](../../docs/GATE_D1_C_C1_RESULT.md). Có guard/config/preflight riêng;
chưa tạo credentials/fixtures, bật LOGIN hoặc khởi động test app. Không dùng
readiness D1-A/B để bypass state mới; các đoạn D1-B bên dưới là bối cảnh trước đó.

Hiện tại D1-B đã PASS schema/ACL: PostgreSQL 18 và Redis trên network internal,
38 bảng/244 cột rỗng, 11 service roles NOLOGIN và không password. Không app test
container, fixtures, Identity account, JWT token hay business suite.

- Invoke-GateDSchemaAcl.ps1: Preflight (mặc định), Apply và Verify tách biệt.
  Pin exact SQL checksums/cluster/resources, Apply một transaction duy nhất.
  Không replay khi đã có apply-started; Verify luôn READ ONLY. Không tự rollback
  destructive hoặc bật LOGIN. Nguồn D1-A manifest chỉ dùng resource identity;
  kết quả/stage manifest D1-B nằm trong evidence mới, không ghi đè lịch sử D1-A.
- GateD.SchemaPackage.psm1: phân loại toàn bộ schema dump đã pin, kiểm kê DDL,
  so definition và chuẩn hóa hẹp tám CHECK array casts PG18.4/18.6 đã review.
- tests/Test-GateDSchemaReview.ps1: 8 pure text guard cases; không DB/business API.

- Start-GateDInfrastructure.ps1: chỉ cho environment MỚI. Resource đã tồn tại
  gây STOP; không dùng script này để retry/recreate môi trường đang có.
- Test-GateDReadiness.ps1: guard + READ ONLY SQL qua DockerExec pin container ID,
  PostgreSQL system identifier, image, labels, network, volumes; Redis PING.
  Không mở kết nối database Windows và không chạy business suite.
- GateD.Common.psm1: orchestration/identity/secret helpers; stdin chuẩn LF khi
  chuyển PowerShell sang Linux. Secrets không đưa vào argv/output.
- tests/Test-GateDGuards.ps1: 28 pure metadata/connection-policy cases, không
  Docker/SQL/business API, không token generation. Không phải PostgreSQL tests.
- Test-GateDSchemaPackage.ps1: chỉ kiểm tra artifact/checksum/source SQL files.

Current environment private evidence:

%LOCALAPPDATA%/AICOS/gate-d/d1a_20261029_104735
%LOCALAPPDATA%/AICOS/gate-d/d1b_20261029_140000

Sau D1-B chỉ dùng Verify; không chạy lại Apply/Start. D1-A readiness yêu cầu zero
tables/roles nên không còn phù hợp live state. Lệnh D1-B READ ONLY:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/Invoke-GateDSchemaAcl.ps1 -InfrastructureRunDirectory "$env:LOCALAPPDATA/AICOS/gate-d/d1a_20261029_104735" -EvidenceDirectory "$env:LOCALAPPDATA/AICOS/gate-d/d1b_20261029_140000" -Action Verify
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/Test-GateDSchemaPackage.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/tests/Test-GateDGuards.ps1
```

Không run Start lần nữa trên resources hiện có; không dùng compose down -v.
Manifest/private encrypted DPAPI cache không đưa vào Git. Reserved signing key
khác key Gate C, chưa đưa vào app hoặc dùng để ký JWT. PostgreSQL có bootstrap
system role postgres phục vụ hạ tầng; có 11 service roles NOLOGIN sau D1-B,
không có tài khoản ứng dụng. Chi tiết preflight, rollback attempt và sai khác
cast đã review trong docs/GATE_D1_B_RESULT.md. Static package checker chỉ nói về
review file, không xác định trạng thái apply của database hiện tại.

Không publish PostgreSQL/Redis host ports. Internal-only bridge đã không tạo
host binding hiệu lực ở thử nghiệm ban đầu; mapping không dùng được đã được bỏ.
Database connection hiện dùng gated-postgres:5432 bên trong container test đã
pin, không phải localhost:5432 hay database test Windows cùng tên.

D1-A readiness cố ý yêu cầu 0 bảng, 0 service roles, đúng hai network members.
D1-B dùng manifest/guard stage riêng với allowlist rõ ràng; không nới guard
D1-A để chấp nhận schema hoặc service lạ. Chờ phê duyệt Identity testing.

Phân biệt test hiện có:

| Loại | Phân loại |
|---|---|
| Guard metadata tests trong thư mục này | Synthetic metadata, không PostgreSQL |
| Readiness probe D1-A | PostgreSQL thật, READ ONLY; Redis protocol thật |
| Postgres*IntegrationTests | Có seed/cleanup, một số fake S2S hoặc tự ký token; CHƯA CHẠY |
| GatewayTests | In-process; có test tự ký token và chấp nhận non-401 kể cả downstream lỗi |
| AI pytest | Có POST/token tự ký, CHƯA CHẠY |
| Thiếu AICOS_TEST_POSTGRES | Fact attribute có thể SKIP trước khi REQUIRE check trong body |

Không chạy blanket dotnet test. Future runner phải kiểm tra endpoint/cluster
manifest và phân biệt executed/skipped/real/fake trước business test.

