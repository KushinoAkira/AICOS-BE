# Backend TODO

## Identity — tiếp theo

- Xây dựng quy trình operator/CLI riêng để cấp SystemAdmin đầu tiên; không mở
  bootstrap endpoint công khai và không seed credential trong source.
- Chuyển migration smoke test PostgreSQL tạm thành integration test tự động
  bằng Testcontainers khi Docker hoạt động.

## Sau Identity

- Xem xét migration bổ sung `guest_count`, người mở phiên và partial unique index
  chống hai phiên active trên một bàn; phải trình SQL trước khi apply.
- Bổ sung role Barista và policy cập nhật pha chế khi nghiệp vụ/DB chính thức duyệt;
  hiện Manager/SystemAdmin cập nhật, Cashier/Waiter chỉ xem.
- Billing/Payment theo thứ tự master plan; không triển khai delivery cho Staff Mobile.
- Trình SQL migration Payment để bổ sung actor audit và print status; chỉ apply sau
  khi người dùng xác nhận, sau đó mới mở payment confirmation/refund endpoints.
- Chỉ bật Flutter API Mode khi API tương ứng và DB test đã đạt.
