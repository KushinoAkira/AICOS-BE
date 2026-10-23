# API Integration

## Trạng thái

- Backend Login API: đã triển khai và có unit/HTTP validation test.
- Flutter: chưa kết nối; vẫn dùng mock session theo nguyên tắc chỉ tích hợp sau
  khi API và database provisioning chạy được trong môi trường thật.
- Base URL qua Gateway local: `http://localhost:8080`.

## Contract Flutter cần dùng

1. Gửi email, password, optional branchId tới `/api/v1/identity/auth/login`.
2. Nếu `requiresBranchSelection=true`, hiển thị danh sách branch rồi gửi lại.
3. Lưu access token bằng secure storage, không dùng shared preferences/plain text.
4. Gửi `Authorization: Bearer <token>` qua API client tập trung.
5. Lấy role/branch từ response hoặc verified token; Backend vẫn là nguồn quyết
   định quyền cuối cùng.

Chưa tích hợp vì còn thiếu refresh token, account provisioning và môi trường DB
có dữ liệu thật.
