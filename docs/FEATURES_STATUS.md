# Features Status

| Module | Trạng thái | Ghi chú |
| --- | --- | --- |
| Backend foundation/Gateway | Đã có | YARP, JWT validation, health, logging |
| Identity database mapping | Đã apply `aicos_db.public` | 35 bảng gốc + EF history + `refresh_sessions`; baseline không DDL |
| Staff login theo branch | Đã code + test PostgreSQL thật | HTTP login/me và đăng nhập tài khoản vừa provision đều đạt trên `aicos_db` |
| Refresh/revoke token | Đã code + apply DB + integration test | HTTP refresh/logout đạt; dữ liệu test đã cleanup |
| Account/branch provisioning | Đã code + test PostgreSQL thật | `POST /identity/staff`, policy Management, branch scope, không seed credential |
| Table/Session API | Đã code + test PostgreSQL thật | Map theo branch, open/close session, cleaned; transaction serializable |
| Catalog read API | Đã code + test PostgreSQL thật | Branch availability, category, product, variant, option, search |
| Create/Add Order + Tracking | Đã code + test PostgreSQL thật | DINE_IN/TAKEAWAY, server pricing, actor event, requestId idempotency |
| Preparation/Kitchen | Đã code + test PostgreSQL thật | Queue theo branch, PENDING → PREPARING → READY, actor event, transaction serializable |
| Billing/Payment | Đã code + test DB riêng + Gate B/A hoàn tất | `aicos_db.public`: 38 bảng/244 cột; 11 LOGIN roles/quyền và kết nối thật PASS; startup/smoke còn chờ Gate C, chưa bật thanh toán thật |
| Flutter API integration | Một phần | Identity, Table, Catalog, Ordering, Kitchen đã nối; Billing chưa bật |
