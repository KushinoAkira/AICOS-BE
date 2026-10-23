# Business Workflows

## Staff Login

```text
Nhập email/password
  -> kiểm tra dữ liệu đầu vào
  -> tìm assignment active + user/branch/membership active
  -> verify password hash
  -> một branch: phát JWT branch-scoped
  -> nhiều branch: yêu cầu chọn branch
  -> chọn branch hợp lệ: phát JWT branch-scoped
```

Các nguyên tắc:

- Phản hồi sai mật khẩu không tiết lộ email có tồn tại hay không.
- User không thể tự chọn branch ngoài `staff_assignments`.
- WAITER và CASHIER là hai role Staff riêng; quyền nghiệp vụ chi tiết tiếp tục
  được kiểm tra tại service sở hữu endpoint.
- Access token hết hạn không được tự coi là phiên hợp lệ. Refresh token là task
  tiếp theo của Identity.

Workflow order/payment chưa được đánh dấu hoàn thành vì các API tương ứng chưa có.
