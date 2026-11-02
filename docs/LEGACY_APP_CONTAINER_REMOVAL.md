# Xóa container ứng dụng BE cũ — 2026-11-02

Theo yêu cầu tiếp theo của chủ dự án, đã xóa đúng 13 container ứng dụng AICOS cũ sau khi xác minh chúng đều exited và thuộc project aicos-be hoặc aicos-gate-d. Chỉ dùng docker rm theo ID đã kiểm tra; không dùng --force, -v, down hoặc prune.

Đây là cập nhật sau docs/LEGACY_CONTAINER_STATUS.md: báo cáo đó ghi trạng thái trước khi được phê duyệt xóa container ứng dụng, không phải inventory hiện tại.

## Container ứng dụng đã xóa
- aicos-gateway
- aicos-ai
- aicos-customer
- aicos-reporting
- aicos-workforce
- aicos-inventory
- aicos-kitchen
- aicos-payment
- aicos-order
- aicos-table
- aicos-menu
- aicos-identity
- aicos-gate-d-identity

## Giữ nguyên và xác minh

- aicos-redis, aicos-gate-d-postgres, aicos-gate-d-redis giữ nguyên ID.
- Danh sách toàn bộ Docker volume trước/sau hoàn toàn giống nhau; không xóa dữ liệu volume.
- Không xóa image, source code, nhánh/stash backup hoặc credentials cache. Metadata riêng của 13 container đã xóa không còn trong Docker; tạo lại runtime cần cấu hình phù hợp từ source/secrets có sẵn.
- Không kết nối PostgreSQL Windows, không migration/seed hoặc thay đổi database chính/test.
- Không đụng container của dự án khác; không khởi động stack main hoặc sửa Mobile; không commit/push.

## Trước khi chạy main

Container Redis cũ vẫn giữ tên aicos-redis và project aicos-be. Cần cấu hình project/container/volume riêng cho main để tránh dùng lại dữ liệu cũ. Kiểm tra port 5432/6379/8080 và migration target trước khi khởi động. Xóa container ứng dụng không đồng nghĩa đã dựng xong database theo main.

Bằng chứng ngoài Git: %TEMP%/aicos-legacy-app-removal-20261102.json (chỉ tên/ID/project, không có environment/secrets).
