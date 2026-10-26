# Backend TODO

> Deployment status 2026-10-26: Gate B is complete on aicos_db.public
> (38 tables / 244 columns). Roles/grants and startup now await separate Gate
> A/C approval. Older migration-pending notes below are historical; see
> GATE_B_EXECUTION_RESULT.md for the actual deployment result.

## Identity — tiếp theo

- Xây dựng quy trình operator/CLI riêng để cấp SystemAdmin đầu tiên; không mở
  bootstrap endpoint công khai và không seed credential trong source.
- Chuyển migration smoke test PostgreSQL tạm thành integration test tự động
  bằng Testcontainers khi Docker hoạt động.

## Sau Identity

- Thống nhất cấu hình Docker Compose với database chính thức `aicos_db.public`;
  hiện Docker còn khai báo database `aicos` và role/schema-per-service trong khi sáu
  service nghiệp vụ chính ánh xạ `public`. Không đổi schema hoặc apply migration khi
  chưa có phương án được duyệt.

- Xem xét migration bổ sung `guest_count`, người mở phiên và partial unique index
  chống hai phiên active trên một bàn; phải trình SQL trước khi apply.
- Bổ sung role Barista và policy cập nhật pha chế khi nghiệp vụ/DB chính thức duyệt;
  hiện Manager/SystemAdmin cập nhật, Cashier/Waiter chỉ xem.
- Billing/Payment: Cash, QR pending/manual confirmation, audit, close bill, settlement
  và Table guard đã hoàn thành trên `aicos_payment_test`. Chờ duyệt backup và staged
  migration trước khi bật trên `aicos_db`; chưa triển khai refund hoặc delivery.
- Bổ sung API chuyển trạng thái in bill và integration với printer sau khi contract
  thiết bị được duyệt; không suy diễn `PRINTED` từ trạng thái Bar hoặc Payment.
- Chỉ bật Flutter API Mode khi API tương ứng và DB test đã đạt.
