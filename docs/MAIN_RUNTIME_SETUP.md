# Chạy Backend main tại máy local

Cấu hình này dùng database mới của main, tách biệt AICOS-BE-backup và PostgreSQL Windows cũ. Kết quả kiểm tra thực tế được ghi riêng trong MAIN_RUNTIME_EXECUTION.md sau startup.

## Cấu hình cần dùng

- Hạ tầng: docker-compose.main-db.yml, project aicos-main.
- Runtime: docker-compose.yml + docker-compose.main-runtime.yml, cùng project/network/volume aicos-main.
- Credentials/JWT key: .env local bị Git ignore, không đưa vào Git hoặc gửi qua chat.
- Chỉ bind port vào 127.0.0.1; điện thoại chưa thể gọi qua Wi-Fi trực tiếp.
- PostgreSQL/Redis nội bộ vẫn là postgres:5432 và redis:6379; host dùng 5433/6380.
- Tất cả API .NET dùng database theo service của main, MigrateOnStartup=false. Identity seed/demo plans tắt; không bật mô phỏng thanh toán. Không phát hành JWT hoặc có tài khoản chỉ vì .env đã có signing key/seed password.
- JWT issuer/audience riêng: aicos-main / aicos-main-clients, đồng bộ Gateway/.NET/AI. Production environment, Swagger bật rõ qua cấu hình, không bật dev-token endpoints.

## Địa chỉ local

| Thành phần | Địa chỉ |
| --- | --- |
| Gateway | http://127.0.0.1:8081 |
| Identity | http://127.0.0.1:5201 |
| Menu | http://127.0.0.1:5202 |
| Table | http://127.0.0.1:5203 |
| Ordering | http://127.0.0.1:5204 |
| Payment | http://127.0.0.1:5205 |
| Kitchen | http://127.0.0.1:5207 |
| Workforce | http://127.0.0.1:5208 |
| Reporting | http://127.0.0.1:5209 |
| Customer | http://127.0.0.1:5210 |
| AI | http://127.0.0.1:5211 |

Swagger của từng API .NET: /swagger/index.html hoặc /swagger/v1/swagger.json. AI: /docs và /openapi.json. Gateway không có Swagger tổng hợp; health live ở /health/live, không giả định có /health/ready tổng hợp.

## Lệnh sử dụng sau khi triển khai thành công

Chạy tại D:\ki9\sep490\Sep490\AICOS-BE:

```powershell
# Xem hạ tầng và runtime:
docker compose -f docker-compose.yml -f docker-compose.main-runtime.yml ps

# Nếu đã dừng hạ tầng mới, bật lại đúng hạ tầng này:
docker compose -f docker-compose.main-db.yml up -d --wait

# Bật lại API từ image đã build; không recreate hạ tầng theo dependency:
docker compose -f docker-compose.yml -f docker-compose.main-runtime.yml up -d --no-deps --no-build identity menu table order kitchen payment workforce reporting customer ai gateway

# Sau khi thay source mới, build lại đúng image namespace aicos-main:
docker compose -f docker-compose.yml -f docker-compose.main-runtime.yml build
```

Không dùng lệnh base docker compose up đơn lẻ cho cấu hình local này: nó có tên/port/project khác và bật auto-migration. Không dùng down -v, prune hoặc volume cũ để đổi schema/mật khẩu. Hướng dẫn này không phê duyệt tự migrate khi cập nhật source; migration tiếp theo phải review và triển khai riêng.

Dotnet run không tự đọc .env; nếu chạy ngoài Docker phải cấu hình connection string theo host 127.0.0.1:5433, đúng database/role và Redis 127.0.0.1:6380. Không dùng user-secrets BE cũ để chạy main mới.

## Dọn build output — 2026-11-03

- Đã xóa 32 thư mục bin/obj trong src và tests, dung lượng đo trước khi xóa 547.982.793 bytes (522,60 MiB).
- Chỉ chọn thư mục sinh tự động bị Git ignore; kiểm tra đường dẫn tuyệt đối nằm trong AICOS-BE, không có tracked file hoặc reparse point trước khi xóa.
- Xác minh git diff --binary trước/sau giống nhau; hash của .env và toàn bộ file untracked (tài liệu/cấu hình đang làm) giữ nguyên trước khi bổ sung ghi chú này. Không dùng git clean vì .env cũng bị ignore và phải được giữ lại.
- Không sửa code, xóa tài liệu, credentials, file backup, image/container/volume hoặc database. Docker runtime dùng bản publish trong image, không dùng các thư mục build host vừa dọn.
- Không chạy build/test lại trong tác vụ dọn vì sẽ tái tạo chính build output. Khi cần chạy .NET hoặc EF tooling từ host, chạy restore/build trước; không dùng --no-build khi chưa build lại.
- Kiểm tra sau dọn: đủ 32 đường dẫn đã biến mất, không còn bin/obj bị ignore trong src/tests; .env vẫn tồn tại và Git-ignored; Git whitespace PASS. Không commit/push.

## Giới hạn

Health/metadata/OpenAPI và kiểm tra GET không chứng minh đăng nhập, phân quyền bằng token hợp lệ hoặc luồng thanh toán hoạt động hoàn chỉnh. Tài khoản, fixtures và integration Mobile là bước tiếp theo. Mobile hiện vẫn chạy demo độc lập.

AI hiện là placeholder; readiness chỉ thử TCP tới PostgreSQL/Redis. Các endpoint AI nghiệp vụ chưa được coi là hoàn thành; không chạy tác vụ AI nặng hoặc gửi prompt trong bước runtime.

Các expiry worker Ordering/Payment của source main vẫn có trong runtime, chỉ xử lý khi có bản ghi đến hạn. Subscription reminder đã tắt bằng cấu hình. Bước smoke không tạo dữ liệu hoặc gọi API ghi; phải đối chiếu database sau startup để xác nhận chưa có phát sinh.
