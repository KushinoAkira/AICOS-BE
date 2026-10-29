# Identity TEST — C1 preparation

C1 chuẩn bị guard/config và **read-only preflight runner**, chưa có runtime app,
credentials, fixtures hoặc business API runner. [Kết quả](../../../docs/GATE_D1_C_C1_RESULT.md).

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/tests/Test-GateDIdentityGuards.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/identity/Prepare-GateDIdentity.ps1 -ReferenceDirectory "$env:LOCALAPPDATA/AICOS/gate-d/d1c_readonly_20261029_140640"
```

Guard tests dùng synthetic data, không JWT/password thật và không database connection;
có gọi Docker Compose để render config local, không gọi Engine startup. Prepare cần
Engine/infra đang healthy; chỉ dùng SQL READ ONLY. Không tự khởi động infra bị dừng.

`docker-compose.gate-d.identity.yml` là file apps-only standalone. External network
phải đúng Gate D, không merge base/Gate C/infra Compose. Gateway JSON thay toàn bộ
appsettings.json qua config read-only khi C4 được duyệt, chỉ route Identity TEST.
Các required variables cho image/password/key không có default; C1 config validation
dùng --no-interpolate và config-only.env.example để không nạp .env chính.

Không up/start app tại C1. C2 credentials/LOGIN duy nhất identity_svc, C3 fixtures,
C4 runtime, C5 HTTP integration và C6 cleanup là các bước riêng cần phê duyệt.
Không dùng chuỗi synthetic trong test làm runtime key/password. Reserved DPAPI key
chỉ kiểm tra tồn tại/DACL, chưa decrypt hoặc distribute. Khi runtime cần pin local
image IDs, actual container IDs/mount source và hashes; manifest Apps={} hiện tại
không cho phép tự thêm app hoặc network members.
