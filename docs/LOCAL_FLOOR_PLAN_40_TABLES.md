# Mỗi chi nhánh thêm 10 bàn — 2026-11-13

> Sau khi hoàn tất, người dùng yêu cầu commit/push script và tài liệu cùng đợt Mobile. Yêu cầu mới về 10 ô MV cố định là UI Takeaway, không tạo thêm bàn Dine-in hoặc thay schema Backend. Kết quả Git được báo sau khi xác minh remote.

Người dùng xác nhận thêm 10 bàn **mỗi chi nhánh**, tiếp nối baseline 30 bàn/chi nhánh. Đã áp dụng qua Table API hiện có, không migration, schema hoặc SQL ghi trực tiếp.

| Chi nhánh | Trước | Thêm | Sau |
| --- | --- | --- | --- |
| Hồ Con Rùa (Q.3) | 30 | 10 | 40 |
| Nguyễn Huệ (Q.1) | 30 | 10 | 40 |
| Phan Xích Long (Phú Nhuận) | 30 | 10 | 40 |
| Tổng | 90 | 30 | 120 |

## Thực thi

Script `scripts/Expand-LocalFloorPlan.ps1` có preview mặc định; chỉ `-Apply` mới tạo bàn còn thiếu. Trước mọi batch, xác minh Docker project aicos-main, các container đang chạy và migrations disabled, Table connection trỏ `postgres/aicos_table_reservation` bằng `table_reservation_svc`. Không kết nối Windows PostgreSQL/aicos_db cũ.

Xác minh BrandAdmin/tenant fixture qua API bằng credential DPAPI đã có; không ghi token/password vào code hoặc log. Ba chi nhánh active, mỗi chi nhánh đúng 3 khu. Giữ khu hiện có, phân thêm 4/3/3 bàn, nối tiếp prefix khu và mã 11 trở đi. Bàn mới capacity 4, Available theo domain Table; capacity không được dùng làm số khách thực tế. Không thêm bàn Mang về cố định, vì Mang về là các đơn hiển thị trong Mobile.

Các API: login/me/branches, GET floor-plan, POST dining-tables/batch, GET readback, logout phiên provisioning. Không gọi API tạo đơn, pha chế hoặc thanh toán. Không rebuild/restart/recreate container.

## Bằng chứng

- Preflight trước apply: 30 bàn còn thiếu, 10 mỗi chi nhánh.
- Apply/readback: cả ba chi nhánh 40 bàn, unique table codes, không tạo khu mới; ID/QR/status/area của toàn bộ bàn cũ giữ nguyên.
- Preview chạy lại: missing tables=0, 40 → 40 mỗi chi nhánh; không gửi batch mới. Không cộng thêm 10 lần nữa khi chạy lại.
- Evidence tạm ngoài Git: `%TEMP%/aicos-floor-expansion-20261113.json`, chỉ tên/ID chi nhánh, số lượng, cờ originalsPreserved; không chứa QR token hoặc credentials.
- PowerShell script thực sự chạy trên Gateway/PostgreSQL local; đây là API readback thật, không phải mock test.

Không có thao tác xóa/rollback tự động. Nếu cần gỡ bàn mới sau này phải kiểm tra chúng đã được sử dụng chưa và duyệt riêng; không xóa bàn đang có phiên. User yêu cầu dữ liệu này trực tiếp nên scope ghi chỉ là 30 bàn mới và các phiên login/logout provisioning thông thường. Không đổi ACL/credentials/plan limit và không commit/push.
