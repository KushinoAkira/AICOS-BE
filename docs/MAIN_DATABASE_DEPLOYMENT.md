# Main database deployment — 2026-11-02

## Kết quả

PASS: hạ tầng database riêng của Backend main đã chạy và schema đã được triển khai thực tế, không chỉ tạo file SQL.

Source: KushinoAkira/AICOS-BE, nhánh main, commit d7ab6b7cb25634a2fb4635c10ebeb9232db27534. Không sửa các EF migrations hoặc code nghiệp vụ của main.

- Container aicos-main-postgres (pgvector/pgvector:pg16) và aicos-main-redis (redis:7-alpine): running/healthy.
- Project/network: aicos-main / aicos-main_default.
- Volume mới: aicos-main_postgres-data, aicos-main_redis-data; được xác minh chưa tồn tại trước khi tạo, không dùng lại volume cũ.
- PostgreSQL host endpoint: 127.0.0.1:5433; Redis: 127.0.0.1:6380. Cổng nội bộ vẫn 5432/6379.
- PostgreSQL cluster identifier: 7703616372368646182, được đối chiếu qua container và kết nối TCP host trước từng stage migration.
- 10 database service theo init script main + database bootstrap aicos. PostgreSQL còn có các database hệ thống mặc định, không tính chúng là database ứng dụng.
- 44 EF migrations đã áp dụng tuần tự vào 9 database .NET; lịch sử từng database khớp chính xác các migration source.
- Tổng 49 bảng nghiệp vụ / 653 cột, chưa tính 9 bảng lịch sử EF / 18 cột. Nếu cộng lịch sử: 58 bảng / 671 cột trong 9 database đã migrate.
- AI có database aicos_ai và extension vector trong schema ai; chưa có migration EF hoặc bảng nghiệp vụ AI từ bước này.
- Dữ liệu nghiệp vụ vẫn rỗng. Chưa tạo tài khoản ứng dụng, chưa seed admin/demo plans/payment providers và chưa phát hành JWT.

## Database/service đã kiểm tra

| Service | Database | Bảng nghiệp vụ | Cột nghiệp vụ | Migration | PK gồm EF history | FK |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| identity | aicos_identity | 10 | 132 | 11 | 11 | 7 |
| menu | aicos_menu | 5 | 47 | 3 | 6 | 4 |
| table_reservation | aicos_table_reservation | 2 | 24 | 3 | 3 | 1 |
| ordering | aicos_ordering | 6 | 92 | 8 | 7 | 2 |
| payment | aicos_payment | 6 | 90 | 4 | 7 | 0 |
| kitchen | aicos_kitchen | 3 | 37 | 2 | 4 | 2 |
| workforce | aicos_workforce | 9 | 126 | 4 | 10 | 5 |
| reporting | aicos_reporting | 1 | 16 | 1 | 2 | 0 |
| customer | aicos_customer | 7 | 89 | 8 | 8 | 2 |
Mỗi service kết nối bằng role <schema>_svc và sở hữu database của nó, đúng kiến trúc main. Cả 9 role .NET đã đăng nhập thật, có database ownership, không phải superuser; thử kết nối sang database service khác bị từ chối. ai_svc đã đăng nhập thật và đọc được metadata vector. Init tạo đủ 10 service roles, tất cả đều không có SUPERUSER/CREATEDB/CREATEROLE.

Không tự thêm FK chéo database. FK trong bảng trên chỉ là quan hệ nội bộ service theo migration main.

## Thứ tự thực hiện và kiểm soát an toàn

1. Kiểm tra Engine, container/network/volume, port 5433/6380. Lưu snapshot ID/image/trạng thái/thời điểm start của 3 container dữ liệu cũ và danh sách volume.
2. Tạo docker-compose.main-db.yml riêng, chỉ có PostgreSQL/Redis. Base docker-compose.yml của main không bị sửa hoặc dùng để khởi động toàn bộ stack.
3. Tạo .env mới từ .env.example, secrets ngẫu nhiên bằng CSPRNG; file chỉ current Windows user được truy cập và bị Git ignore. Không ghi giá trị secret vào tài liệu/log/chat. SEED_DEMO_PLANS=false.
4. Validate Compose trong bộ nhớ, không xuất config đã nội suy secrets. Up đúng 2 service bằng --no-build --pull never --wait; init script main tạo 10 database/role/schema và AI vector.
5. Restore dotnet-ef 8.0.31; build AICOS.sln thành công. Sinh SQL Up từ migration 0 tới latest cho từng service bằng dotnet ef migrations script, không khởi động API; môi trường tooling Production, Database__MigrateOnStartup=false và credentials mới chỉ ở process environment.
6. Review SQL scope và checksum. Main có cleanup DELETE ở Menu/Table/Workforce và DROP bảng/cột trung gian; chỉ áp dụng lên các database vừa tạo, tất cả đều có 0 bảng trước migration. Không dùng các script này để nâng cấp database có dữ liệu mà chưa review riêng. Identity có INSERT legacy tenant có điều kiện; không tạo tenant vì không có user/branch dữ liệu cũ.
7. Trước từng stage: xác minh container/project/volume/cluster, host port, checksum. Kết nối từng database bằng đúng service role, guard current_database/current_user trước script, psql ON_ERROR_STOP. Chạy lần lượt Identity → Menu → Table → Ordering → Payments → Kitchen → Workforce → Reporting → Customer. Không chạy seed hoặc Program startup. Nếu stage lỗi thì dừng, không auto rollback/reset; thực tế tất cả stage thành công.
8. Kiểm tra READ ONLY thực tế: bảng/cột/kiểu dữ liệu khớp toàn bộ EF ModelSnapshot; index hợp lệ, constraint đã validate; role ownership, đăng nhập và chặn connect chéo database; dữ liệu nghiệp vụ rỗng; AI vector; container/volume cũ không đổi.

## Credentials và cách kết nối

Credentials của main nằm trong .env bị Git ignore, không tái dùng Gate C/D hoặc PostgreSQL Windows. Main hiện dùng SERVICE_DB_PASSWORD chung cho các role dev theo thiết kế source; đây không phải cấu hình secrets riêng từng service cho production.

Kết nối bằng công cụ PostgreSQL trên Windows:

- Host 127.0.0.1, port 5433.
- Database chọn aicos_identity/aicos_ordering/aicos_payment... tùy service.
- Username đúng service, ví dụ identity_svc; password lấy cục bộ từ SERVICE_DB_PASSWORD trong .env, không gửi qua chat.
- Quản trị cluster mới: aicos_admin với POSTGRES_PASSWORD trong .env. Database bootstrap aicos không chứa tất cả bảng service.

Không dùng port 5432 trên Windows: đó có thể vẫn là PostgreSQL cũ. Dotnet run không tự đọc .env; phải cấu hình connection string/credentials mới trước khi chạy API. Không copy user-secrets cũ hoặc bật base Compose nhầm project/volume.

## Lệnh hạ tầng hiện tại

Từ D:\ki9\sep490\Sep490\AICOS-BE:

```powershell
docker compose -f docker-compose.main-db.yml ps
# Nếu cần chạy lại đúng hạ tầng mới đã tạo:
docker compose -f docker-compose.main-db.yml up -d --wait
# Dừng an toàn khi cần, không xóa dữ liệu:
docker compose -f docker-compose.main-db.yml stop
```

Không cần chạy lại migration đã áp dụng. Không dùng down -v, prune hoặc xóa volume để đổi mật khẩu. Muốn thay đổi credentials hoặc schema sau này phải có kế hoạch riêng và backup.

## Kết quả kiểm tra và giới hạn

- Compose config: PASS; chỉ 2 service main mới, tên/volume/port riêng.
- .NET build: PASS, 17 projects, 0 errors; 2 warning có sẵn trong project test (CS0628 helper protected ở sealed class; CS8620 tuple nullability). Không chỉnh sửa test/code nghiệp vụ để che warning.
- PostgreSQL thật: 44/44 migration history entries khớp source, 9/9 schema/model table/column/type comparisons PASS, 9/9 role login + negative cross-database checks PASS. AI role/vector PASS; cả 2 infrastructure health checks PASS.
- Kiểm đếm PK đã được giới hạn theo schema service sau khi phát hiện truy vấn không lọc sẽ tính cả PK catalog PostgreSQL; số trong bảng là kết quả đã sửa scope và xác minh lại.
- Không chạy xUnit/API business suite hoặc smoke login/order/payment. Các truy vấn/catalog/ACL nêu trên thực sự chạy trên PostgreSQL mới, không dùng mock để thay thế. Không có test skip được tính là PASS.
- 3 container dữ liệu cũ giữ nguyên ID/image/StartedAt/trạng thái exited; toàn bộ volume cũ vẫn còn. Không kết nối hoặc thực thi SQL lên PostgreSQL Windows/aicos_db/aicos_payment_test cũ. Không khẳng định fingerprint dữ liệu cũ đã được đo lại trong task này.
- Không sửa Mobile, không tạo JWT/tài khoản, không khởi động API/Gateway/AI, không commit/push.

## File và bằng chứng

File mới trong repo: docker-compose.main-db.yml và docs/MAIN_DATABASE_DEPLOYMENT.md. .env là file local ignored, không đưa vào Git.

Bằng chứng SQL/manifest/checksum/inventory trước-sau/verification nằm ngoài Git:

C:\Users\ASUS\AppData\Local\AICOS\main-deployment\20261102-234808

Các file chính: plan.json, 9 file <service>.sql, before.json, applied-stages.json, after.json, verification.json. SQL là output của đúng EF Up migrations; không chứa credentials. Directory evidence có ACL riêng current user.

## Trạng thái tiếp theo

Database main READY cho bước cấu hình runtime. Backend API và Mobile integration chưa READY: cần cấu hình API dùng các database mới, tắt auto-migration sau deployment, khởi động theo phụ thuộc và chuẩn bị tài khoản hợp lệ bằng luồng seed/provision được thống nhất. Không mặc định đã có tài khoản admin chỉ vì .env có SEED_ADMIN_EMAIL/SEED_ADMIN_PASSWORD.

Phục hồi sự cố hiện tại: dừng đúng hạ tầng main mới, giữ volume và bằng chứng; không tự xóa database/volume hoặc rollback destructive. Code và dữ liệu cũ vẫn được giữ ở AICOS-BE-backup và hạ tầng cũ.
