# Gate D1-A — Schema/ACL package (review only)

Không file SQL nào trong thư mục này đã được chạy ở D1-A.
Live test database vẫn có 0 public tables và 0 service roles.

## Schema source

schema-source.json pin artifact schema-only từ backup sau Gate B:

- %LOCALAPPDATA%/AICOS/backups/gate_a_20261026_213339/schema.sql
- SHA256: CC6F19A5BAE3A8ACBCB096EC110A7D172B4C86D63C37E1AE18CF61C8E30158A3
- 38 bảng / 244 cột; có payment_events.
- Không INSERT/COPY rows, role/password/owner/grant hoặc extension/foreign server.
- Một function/trigger được review: reject_payment_event_mutation và
  trg_payment_events_append_only để bảo vệ audit append-only.
- Artifact được dump từ PostgreSQL 18.4; container test là 18.6. Chưa restore nên
  chưa tuyên bố DDL tương thích/constraint/index đã PASS trên test.
- Không dùng file aicos_db.dump có dữ liệu, không kết nối main/Windows test DB.
- Không copy EF migration history rows; service startup migration phải false.
  Khi cần history test trong D1-B phải review riêng, không sửa migration đã chốt.

Lệnh dưới chỉ đọc file, không mở SQL connection:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/Test-GateDSchemaPackage.ps1
```

## Thứ tự cần phê duyệt trong D1-B

1. Xác minh manifest private, ID/image/network/volume/cluster bằng guard.
2. Review guarded schema restore helper TRƯỚC khi chạy; helper phải xác minh
   checksum source và target guard trên CÙNG kết nối apply schema. D1-A chưa
   cung cấp helper apply SQL và không cho phép apply bằng tay bỏ qua guard.
3. Restore schema-only đã duyệt vào database TEST trống; verify PK/FK/index/
   constraints, 38/244 và Payment append-only trigger; không chạy lại Payment
   00–05 khi baseline đã có đầy đủ.
4. 01_runtime_roles.review.sql: đề xuất 11 roles NOLOGIN, không passwords,
   không superuser/owner/DELETE/DDL. Existing roles gây STOP, không overwrite.
5. 02_runtime_acl.review.sql: clone ma trận cột/bảng đã review cho TEST.
   Không tự grant refunds hoặc quyền cho module chưa triển khai.
6. Mật khẩu TEST mới riêng từng service và LOGIN cần được duyệt/cấu hình sau
   quyền PASS; không reuse Gate C credentials. Không áp dụng bước này ở D1-A.

00_target_guard.sql yêu cầu database aicos_payment_test, PostgreSQL 18, schema
public, cluster identifier khớp manifest và session approval stage D1-B.
Runner phải nạp expected identifier từ manifest đã pin Docker resources;
không được tự điền ID của server khác để vượt guard.

SQL guard này là lớp bổ sung, không thay thế kiểm tra container/network/volume.
Không chạy script gốc public-service-grants.sql vì nó hardcode main database.
Bản TEST giữ các ngoại lệ PUBLIC và quyền Kitchen/Ordering như ma trận cũ;
không tuyên bố nó loại bỏ quyền inherited PUBLIC TEMPORARY.

Phê duyệt schema/ACL không đồng nghĩa cho phép fixtures, Identity login/JWT,
test nghiệp vụ hoặc khởi động service test. Các phần đó cần scope rõ tiếp theo.

