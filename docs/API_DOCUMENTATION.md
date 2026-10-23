# API Documentation

## POST `/api/v1/identity/auth/login`

Đăng nhập nhân viên và khóa phiên vào một chi nhánh. Endpoint public; Gateway và
Identity đều kiểm tra JWT cho các endpoint protected sau đó.

Request:

```json
{
  "email": "staff@aicos.vn",
  "password": "user-supplied-password",
  "branchId": "11111111-1111-1111-1111-111111111111"
}
```

`branchId` có thể bỏ trống. Nếu tài khoản chỉ thuộc một chi nhánh, API phát token
ngay. Nếu thuộc nhiều chi nhánh, response yêu cầu chọn chi nhánh:

```json
{
  "requiresBranchSelection": true,
  "accessToken": null,
  "expiresAtUtc": null,
  "staff": null,
  "branches": [
    { "id": "...", "code": "CN01", "name": "Chi nhánh 1" }
  ]
}
```

Response thành công chứa `accessToken`, `expiresAtUtc`, profile Staff và danh
sách branch được phép. JWT có `sub`, `name`, `role`, `branch_id`, `jti`.

Lỗi dùng RFC 7807:

- `400 validation_failed`: email/password không hợp lệ.
- `401 auth.invalid_credentials`: sai email/password hoặc tài khoản không active.
- `401 auth.branch_not_assigned`: user không được phân công vào branch đã chọn.
- `403 forbidden`: role không được phép dùng Staff app.

Không log password, không trả password hash và không hardcode tài khoản demo.
