# Backend Changelog

## 2026-10-23 — Harden Identity migration

- Tạo backup custom-format và schema-only trước khi thay migration pending.
- Regenerate `InitialIdentitySchema`; migration cũ chưa apply nên không làm thay
  đổi lịch sử database.
- Chuyển 6 FK Identity từ cascade sang `NO ACTION`.
- Đặt branch mới mặc định `INACTIVE`.
- Chuẩn hóa email về lowercase và thêm check constraint tại database.
- Test migration trên database PostgreSQL tạm thành công; không apply vào
  `aicos_db`.
- Build 16 project không có warning/error; Backend tests đạt 27/27.

## 2026-10-22 — Identity Authentication foundation

- Thêm entity/configuration cho tenant, branch, user, role, tenant membership và
  staff assignment trong schema `identity`.
- Thêm migration source `InitialIdentitySchema`; chưa apply database.
- Thêm `POST /api/v1/identity/auth/login` với validation, password hashing,
  branch selection, logging và RFC 7807 errors.
- Thêm role `Waiter` vào JWT/policy Staff để khớp AICOS Staff Mobile.
- Đồng bộ dependency EF Core Relational 8.0.31.
- Thêm unit test auth và HTTP validation test.
- Cài SDK .NET 8.0.426 cho môi trường phát triển; không đổi `global.json`.
- Build toàn solution đạt 0 warning; Backend tests đạt 25/25.
- Sinh idempotent SQL migration thành công vào thư mục tạm; chưa apply database.
