# Backend main runtime — 2026-11-03

## Kết quả

PASS trong phạm vi build, runtime/dependency connectivity và smoke GET chỉ đọc. Chưa coi đăng nhập hoặc nghiệp vụ order/payment/AI là đã kiểm thử hoàn chỉnh.

- Source main: d7ab6b7cb25634a2fb4635c10ebeb9232db27534.
- Build thành công 11 image riêng aicos-main/<service>:local: 9 API .NET, AI Python và Gateway .NET. Không ghi đè image aicos/<service> cũ.
- Khởi động tuần tự bằng Compose --no-deps --no-build --pull never. Hai container hạ tầng main không restart/recreate; 3 container dữ liệu cũ vẫn exited, giữ nguyên ID/image/StartedAt.
- Gateway: http://127.0.0.1:8081. Chỉ bind loopback; Mobile chưa được tích hợp và chưa gọi qua Wi-Fi.
- PostgreSQL/Redis main vẫn healthy ở host port 5433/6380. API dùng postgres:5432 và redis:6379 trên network aicos-main_default; không dùng PostgreSQL Windows/aicos_db.public cũ.
- Cluster PostgreSQL: 7703616372368646182. Tất cả 9 API .NET có session PostgreSQL thực tế đúng database aicos_<schema>, role <schema>_svc và peer IP đúng container.
- Sau startup: 49 bảng nghiệp vụ / 653 cột vẫn khớp model; 44 lịch sử migration không đổi; toàn bộ bảng nghiệp vụ vẫn 0 dòng. Không tạo user/admin, tenant/branch, order, payment hoặc JWT; không seed demo.

## Service/runtime

| Service | Host port | Live | Ready | Metadata | OpenAPI | Phạm vi |
| --- | ---: | --- | --- | --- | --- | --- |
| identity | 5201 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| menu | 5202 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| table | 5203 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| order | 5204 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| kitchen | 5207 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| payment | 5205 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| workforce | 5208 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| reporting | 5209 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| customer | 5210 | 200 | 200 | 200 | 200 | Runtime, PostgreSQL readiness |
| ai | 5211 | 200 | 200 | 200 | 200 | Placeholder; ready chỉ TCP PostgreSQL/Redis |
| gateway | 8081 | 200 | N/A | Root 200 | N/A | Gateway routing/JWT âm |
Mỗi container ứng dụng giữ nguyên ID/StartedAt từ khi khởi động, RestartCount=0 lúc kiểm tra cuối. Ready .NET thực sự kiểm tra PostgreSQL; AI chỉ mở TCP theo source, không coi đó là truy vấn database của AI. ai_svc đã được kiểm tra đăng nhập PostgreSQL riêng ở bước database trước đó.

Gateway không có Swagger tổng hợp hoặc /health/ready; không tính endpoint không tồn tại là FAIL. API có Swagger trực tiếp /swagger/index.html, OpenAPI /swagger/v1/swagger.json; AI có /openapi.json. Các URL/port đầy đủ ở MAIN_RUNTIME_SETUP.md.

## Route qua Gateway

Chỉ gọi GET, không đăng nhập hoặc tạo JWT. Invalid bearer là chuỗi invalid-token, không phải token tự ký hoặc credentials thật.

| Service | GET path | Không token | Token sai |
| --- | --- | ---: | ---: |
| reporting | /api/v1/reporting/_info | 200 | 401 |
| ai | /api/v1/ai/_info | 200 | 401 |
| menu | /api/v1/menu/_info | 200 | 401 |
| payment | /api/v1/payments/_info | 200 | 401 |
| workforce | /api/v1/workforce/_info | 200 | 401 |
| order | /api/v1/orders/_info | 200 | 401 |
| customer | /api/v1/customers/_info | 200 | 401 |
| table | /api/v1/tables/_info | 200 | 401 |
| kitchen | /api/v1/kitchen/_info | 200 | 401 |
| identity | /api/v1/identity/_info | 200 | 401 |
GET không token 200 trong bảng trên là metadata công khai, không phải API nghiệp vụ được phép truy cập ẩn danh.

Bốn endpoint bảo vệ sau được kiểm tra riêng qua Gateway không token (401), và trực tiếp service với thiếu/sai token (đều 401):

- Identity: /api/v1/identity/auth/me.
- Ordering: /api/v1/orders/.
- Payment: /api/v1/payments/transactions/.
- Kitchen: /api/v1/kitchen/queue.

## Cấu hình và bảo vệ dữ liệu

- File overlay mới docker-compose.main-runtime.yml có project aicos-main, container/image namespace riêng, ports !override để không cộng cả port cũ của base Compose.
- MigrateOnStartup=false ở cả 9 API và Gateway. Identity Seed:DemoPlans=false, Subscriptions:ReminderIntervalMinutes=0; Identity/Payment seed cũng bị guard bởi MigrateOnStartup=false. Payments:AllowSimulation=false.
- Production environment, Swagger bật qua config; không bật dev-token endpoints. JWT issuer/audience aicos-main/aicos-main-clients đồng bộ ở Gateway/.NET/AI, signing key mới từ .env của main. Không rotate/tái dùng secrets Gate C/D.
- Merged config chỉ kiểm tra trong bộ nhớ; đối chiếu actual container environment với config. Không xuất docker inspect đầy đủ, merged config hoặc token/password ra log/chat.
- Hash cấu hình hạ tầng PostgreSQL/Redis trong full runtime Compose khớp container đã tạo ở bước database. Vẫn dùng --no-deps khi start app để bảo vệ hạ tầng.
- Ordering có worker dọn pickup/table session và Payment có expiry worker theo source. Chúng không được tuyên bố tắt toàn bộ; database rỗng nên kiểm tra sau startup không thấy dữ liệu phát sinh. Không gọi API ghi hoặc phát integration event nghiệp vụ.
- Log của cả 11 container được đọc riêng trong bộ nhớ: 0 error/fatal level entry và 0 khớp giá trị secrets. Không lưu/hiển thị raw runtime logs chứa credentials.

## Quá trình build và kiểm thử

- Lần helper build đầu dừng vì Windows PowerShell coi dòng tiến độ Docker stderr là NativeCommandError. Đây không phải bằng chứng lỗi compile của Identity. Sửa orchestration để đọc stdout/stderr bằng ProcessStartInfo, chờ và kiểm tra exit code thực tế; build lại được 11/11 image thành công. Không sửa Dockerfile/code nghiệp vụ để che lỗi.
- Docker Compose config/namespace/port/JWT/migration guards: PASS. Infra config hash: PASS.
- Build Docker 11/11: PASS. Runtime 11/11: PASS, trong đó AI là placeholder.
- Gateway 10/10 metadata route GET: 200; invalid token 10/10: 401. Bốn endpoint nghiệp vụ bảo vệ qua Gateway/direct: checks PASS 401, chưa xác minh quyền bằng token hợp lệ.
- PostgreSQL thực tế: session của 9/9 API .NET khớp database/role/container IP; data 0 và migration history không đổi; đối chiếu model/cột/kiểu sau startup PASS, index/constraint hợp lệ. Không dùng mock để thay thế các kết quả này.
- Container dữ liệu cũ và 2 container hạ tầng main giữ nguyên ID/image/StartedAt/trạng thái. Volume cũ còn nguyên qua verifier database.
- Git whitespace và .env ignored check: PASS. Không chạy xUnit suite trong bước runtime, không tính test chưa chạy/skip là PASS.
- Không chỉnh sửa Mobile, migration, dữ liệu/cấu hình PostgreSQL Windows hoặc credentials/ACL cũ; không commit/push.

## File sửa/thêm trong bước này

- docker-compose.main-runtime.yml: cấu hình local runtime an toàn với hạ tầng main mới.
- README.md: link tới hướng dẫn local thay vì chạy nhầm base Compose.
- docs/MAIN_RUNTIME_SETUP.md: lệnh chạy/port/giới hạn.
- docs/MAIN_RUNTIME_EXECUTION.md: báo cáo này.

## Bằng chứng ngoài Git

C:\Users\ASUS\AppData\Local\AICOS\main-deployment\20261102-234808\runtime-20261103-001011

state.json ghi source/image/container ID và từng stage; build-*.txt ghi output build (build không nhận runtime secrets, .env bị Docker ignore); routes.json, direct-auth.json và verification.json ghi kết quả đã che secrets. Metadata SQL/manifest migration ở thư mục cha, verification database được chạy lại sau startup. Không sao chép secrets vào Git.

## Các phần chưa hoàn thành

- Chưa có tài khoản ứng dụng. SEED_ADMIN_EMAIL/SEED_ADMIN_PASSWORD trong .env là cấu hình chưa được sử dụng để tạo user; không coi đó là account đã đăng nhập được.
- Chưa chạy login/refresh/logout hoặc kiểm tra JWT hợp lệ/hết hạn, phân quyền dương theo tenant/branch/role; chưa tạo menu/table/order/payment fixtures.
- Chưa kiểm thử nghiệp vụ payment/refund, SignalR hoặc khả năng xử lý nhiều request/idempotency qua API.
- AI hiện 501 placeholder cho nghiệp vụ; readiness chỉ TCP. Một số yêu cầu role trong router AI vẫn dùng tên cũ SystemAdmin/Manager/WarehouseStaff khác vocab role .NET main; cần review trước khi tích hợp AI bằng tài khoản thật. Không tắt bảo mật hoặc tự sửa role mapping trong bước runtime.
- Mobile vẫn chạy demo; chưa đổi adapter hoặc giả lập đã nối BE main. Để nối cần audit contract mới và mapping role với UI hiện có.

Bước tiếp theo: chuẩn bị bootstrap/provision tài khoản hợp lệ và kiểm thử Identity trên database main mới trước khi tích hợp Mobile. Không bật MigrateOnStartup chỉ để seed; cần cách bootstrap được review riêng. Giữ nguyên hạ tầng/source cũ làm dự phòng.
