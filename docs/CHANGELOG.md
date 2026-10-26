# Backend Changelog

## 2026-10-26 - Gate A completed after hidden all-role reset

- The approved new console assigned all eleven passwords, held NOLOGIN during
  entry, verified credential presence/permissions, then activated LOGIN and
  passed eleven actual role login/read probes.
- Independent READ ONLY checks confirm eleven LOGIN/SCRAM-present roles and
  effective privilege issue_count=0. PUBLIC/non-target ACLs and roles plus all
  38 business-table fingerprints remain unchanged.
- Added GATE_A_EXECUTION_RESULT.md and updated deployment status. No roles
  recreated, grants reapplied, schema/data modified, Gate C, Backend startup,
  Mobile edit, commit or push performed by this reset.

## 2026-10-26 - Approved Gate A all-role reset console opened

- Rechecked all eleven roles NOLOGIN and effective privilege issue_count=0.
- Opened one new masked console with new evidence directory
  gate_a_password_reset_20261026_220544; old progress is preserved.
- Existing reviewed helper will replace all eleven role passwords, keep
  NOLOGIN during assignment, verify credentials/ACLs before LOGIN and then
  test each real role connection. Completion is not yet claimed.
- No role recreation, GRANT/REVOKE reapplication, schema/business-data change,
  Gate C, Backend startup, Mobile edit, commit or push performed.

## 2026-10-26 - Gate A all-eleven password reset prepared, not executed

- READ ONLY inspection confirmed eleven NOLOGIN roles and three existing
  passwords (presence booleans only); the privilege matrix still has zero issues.
- Confirmed the old credential console and psql connection are closed; saved
  progress stopped after identity/menu/table password assignments.
- Added GATE_A_PASSWORD_RESET_PLAN.md with safe console cancellation, a new
  evidence directory, masked all-role replacement and pre-LOGIN checks.
- No password reset, LOGIN change, role recreation, GRANT/REVOKE, schema/data
  mutation, Gate C, service startup, Mobile edit, commit or push performed.

## 2026-10-26 - Gate A status recheck and masked console reopened

- READ ONLY check confirmed all eleven roles remain NOLOGIN with no passwords
  assigned; the reviewed effective privilege matrix still has zero issues.
- Progress stopped at AwaitingHiddenInput; no credential result or old helper
  process remained. Reopened only the existing checksum-verified console.
- No roles recreated, grants reapplied, schema/data changed, Gate C, Backend
  startup, Mobile edit, commit or push performed by this status check.

## 2026-10-26 - Conditional Gate A roles/ACLs applied; hidden input pending

- Removed both unapproved PUBLIC revokes and reported revised SQL hashes before
  execution. PUBLIC and non-target role ACL/metadata digests are unchanged.
- Created eleven NOLOGIN runtime roles and applied only reviewed scoped grants;
  both SQL files exited 0 and the full NOLOGIN effective matrix has zero issues.
- Preserved all 38 table data fingerprints and Gate B structure. No extra role
  attributes, ownership, DELETE/TRUNCATE rights, service startup or migration ran.
- Opened a local masked-input console for password provisioning, later LOGIN
  activation and actual per-role connection probes. This step remains pending
  user input and is not reported complete. No plaintext credential is stored.

## 2026-10-26 - Gate A roles/permissions preparation (not executed)

- Reconfirmed Gate B with READ ONLY metadata/06 verification; eleven runtime
  roles still absent. No Payment migration was reapplied.
- Audited API/domain persistence paths, role usernames and sequence needs.
  Prepared exact table/column grants and explicit revoke review.
- Hardened review-only SQL against reused-role ownership and stale CREATE/
  sequence privileges; proposed revoking PUBLIC TEMPORARY on aicos_db as an
  explicit database-wide effect requiring approval.
- Added READ ONLY effective-privilege verification and a passfile-based actual
  role connection probe. Verifier correctly reports eleven missing roles;
  role creation/grant/login tests remain pending approval, not claimed passing.
- Added GATE_A_ROLES_PERMISSIONS_REVIEW.md. No CREATE ROLE, ALTER ROLE, GRANT,
  REVOKE, service startup, Gate C, Mobile edit, commit or push was executed.

## 2026-10-26 - Gate B applied to aicos_db.public

- Executed approved Payment stages 00-06 sequentially; all SQL exits were zero.
  Backfill reported UPDATE 0 exactly four times; all nine final checks were zero.
- Production inventory is now 38 tables / 244 columns. All original 37 table
  row digests and the original 15 rows remain unchanged; untouched table schemas
  and the two Identity EF history entries match the baseline.
- Recorded the Docker-engine state change: engine was on, but all nine saved
  containers were exited and no AICOS Backend/database writer was running.
- Added GATE_B_EXECUTION_RESULT.md and updated deployment status. No Gate A/C,
  role/grant change, Backend startup, destructive rollback, commit or push ran.

## 2026-10-26 - Final check before Gate B (execution not approved)

- Created and verified a fresh production backup; restored it to a new retained
  test database and matched schema plus all 37 public tables/15 rows.
- Rechecked frozen SQL hashes, reviewed every forward/rollback SQL file, and
  confirmed forward writes/DDL remain scoped to Payment objects.
- Verified native Backend and Docker/WSL are stopped; read-only database checks
  found no other clients, open/prepared transactions, writer locks or proposed
  object-name collisions. Production preflight passed with zero financial rows.
- Added docs/GATE_B_FINAL_CHECK.md with PASS/FAIL evidence, exact proposed
  commands and stop/recovery conditions. No production migration, roles,
  grants, services, destructive rollback or gate transition was executed.

## 2026-10-26 - Phase 5.2 controlled deployment preparation

- Revalidated backup hashes, the retained restore schema/data, production
  read-only preflight and all ten frozen Payment SQL hashes.
- Re-ran nine maintenance tests successfully; no production services started.
- Prepared separate A/B/C approval packets with exact commands, checkpoints,
  expected effects and recovery. Proposed offline B then A then C because
  full runtime grants depend on the expanded Payment schema.
- Recorded that role/migration/startup approval and releasing Payment writes
  are separate decisions. No approval gate was executed.

## 2026-10-26 - Phase 5.1 deployment blockers

- Prepared idempotent NOLOGIN service-role provisioning, read-only role audit,
  and stronger review-only least-privilege grant guards; none executed on the
  production cluster.
- Added guarded PowerShell backup/restore scripts and verified a real aicos_db
  backup against a new retained restore-test database (37 tables, 15 rows).
- Added Payment mutation maintenance middleware with nine passing HTTP tests;
  Compose defaults to paused writes during deployment.
- Added external-database Compose configuration for the existing Windows
  PostgreSQL instance and a sequential image build helper.
- Added SQL checksum validation and the Phase 5.1 deployment/rollback report.
- All 12 Docker images built successfully in sequence. Final validation:
  73 .NET tests passed (8 optional PostgreSQL skips); the separate required
  PostgreSQL Billing/Payment/Table run passed 3/3 with no skips.


## 2026-10-26 - Phase 5 Payment deployment readiness

- Re-audited the staged Payment migration against the current Backend model,
  API behavior, and production `aicos_db` using read-only checks only.
- Confirmed none of the 11 service roles exists yet; role provisioning and
  grants remain explicit deployment blockers.
- Corrected Payment rollback to restore baseline nullability.
- Expanded post-migration verification to assert all additive columns,
  indexes, constraints, and the append-only trigger.
- Tightened runtime UPDATE grants to the columns used by current APIs.
- Rehearsed rollback and full migration on `aicos_payment_test`, then passed the
  required PostgreSQL Billing/Payment/Table tests without skips.
- Added `docs/PHASE5_DEPLOYMENT_READINESS.md` with backup, rollback, risk,
  execution-order, and approval gates.

## 2026-10-25 — Deployment configuration standardized

- Standardized every service on the official `aicos_db.public` deployment
  contract and removed checked-in database passwords.
- Disabled `MigrateOnStartup` in Docker Compose and every launch profile.
- Changed Docker bootstrap from schema-per-service creation to separate runtime
  roles with environment-supplied credentials and `public` search paths.
- Prepared, but did not execute, the review-only least-privilege PostgreSQL
  script in `infra/postgres/security/public-service-grants.sql`.
- Audited Gateway routes and Docker/local service destinations; no route change
  was required. No migration, grant, or data mutation ran on `aicos_db`.

## 2026-10-24 — Cash, QR and table settlement

- Payment domain now persists branch/actor audit, optimistic versions and append-only
  `payment_events` against the approved additive schema.
- Added idempotent Cash payment, pending bank-transfer QR, evidence-based manual
  confirmation, bill close and session settlement endpoints.
- Table Reservation now checks Payments through a typed REST contract and keeps the
  session open when settlement is false or unavailable.
- Added unit tests and real PostgreSQL integration on `aicos_payment_test`, including
  concurrent QR confirmation and the Table-to-Payments close flow. No payment migration
  was run on `aicos_db`.

## 2026-10-24 — Payment additive migration prepared

- Reviewed the live `aicos_db.public` payment schema in a read-only transaction
  and confirmed that one table session can own multiple orders while one bill
  remains unique by session.
- Replaced the unsafe single-step payment SQL draft with staged preflight,
  expand, audit-table, backfill, concurrent-index, contract, verification, and
  rollback scripts under `docs/migrations/payments`.
- Added a disposable PostgreSQL payment test environment fixed to
  `aicos_payment_test`; full apply, verification, rollback, and re-apply passed
  there with zero invalid indexes. No migration was executed on `aicos_db` and
  no real data changed.

## 2026-10-24 — Query bill theo nguồn nghiệp vụ

- Thêm `GET /api/v1/payments/bills?sessionId=...` và `?orderId=...`; request bắt
  buộc đúng một UUID nguồn.
- Payments xác minh branch qua Ordering REST contract trước khi trả bill; không
  query chéo DbContext hoặc bảng thuộc Ordering.
- Siết `GET /bills/{billId}` cho bill tại bàn: session không có order trong branch
  hiện tại trả `404`, không còn coi lời gọi Ordering rỗng là đã xác minh quyền.
- Bổ sung unit test source query, validation, branch isolation và mở rộng PostgreSQL
  integration test để đọc lại bill theo `orderId`.
- Chuẩn hóa PostgreSQL integration tests thành `Skipped` thật khi thiếu
  `AICOS_TEST_POSTGRES`; regression hiện có 59 test pass và 7 test PostgreSQL skip.
- Không thay đổi entity, schema, migration hoặc dữ liệu PostgreSQL.

## 2026-10-23 — Billing foundation

- Ánh xạ bốn bảng có sẵn `bills`, `bill_items`, `payments`, `refunds` vào Payment
  service schema `public`; không tạo migration.
- Thêm contract Ordering liệt kê order theo Table Session qua REST và API tạo/đọc
  bill theo branch token, hỗ trợ idempotency bằng `requestId`.
- Bill tại bàn tổng hợp toàn bộ order trong session; bill mang về dùng một order,
  giá và item snapshot lấy từ Ordering thay vì nhận từ Mobile.
- Thao tác tạo bill dùng transaction `SERIALIZABLE`; 4 test foundation và HTTP
  PostgreSQL integration create → retry → read đều đạt, dữ liệu test được cleanup.
- Chưa mở API xác nhận thanh toán/in bill vì schema chưa có actor audit và trạng
  thái in độc lập; cần trình migration trước khi thay đổi database.
- Toàn Backend hiện đạt 59/59 test (6 BuildingBlocks + 53 smoke/integration).

## 2026-10-23 — Preparation workflow hoàn chỉnh

- Map read/update subset của `orders`, `order_items`, `order_item_events` vào
  Kitchen Operations service; không tạo bảng hoặc migration.
- Thêm queue theo branch và transition `PENDING → PREPARING → READY`.
- Staff chỉ xem; tạm thời chỉ Manager/SystemAdmin được đổi trạng thái vì hệ thống
  chưa có role Barista. Không giả lập Bar đã duyệt.
- Bọc thao tác đổi trạng thái trong transaction `SERIALIZABLE`, trả conflict ổn
  định khi hai thiết bị cùng cập nhật một món và ghi actor vào item event.
- Unit/HTTP test đạt; PostgreSQL thật đã chạy queue → PREPARING → READY và xác
  nhận toàn bộ dữ liệu thử nghiệm được cleanup.
- Toàn Backend đạt 54/54 test (6 BuildingBlocks + 48 smoke/integration).

## 2026-10-23 — Ordering dependency contract

- Audit bốn bảng Ordering và xác nhận chỉ hỗ trợ `DINE_IN`/`TAKEAWAY`, không có
  delivery trong phạm vi Staff Mobile.
- Thêm `GET /api/v1/tables/sessions/{sessionId}` theo branch JWT để Ordering xác
  minh phiên qua REST, không query chéo DbContext.
- Test Table Operations liên quan đạt 5/5; chưa thay đổi schema hoặc migration.
- Ánh xạ bước đầu bốn bảng Ordering hiện có vào `OrderingDbContext` schema
  `public`, gồm order, item, option snapshot và item event; build 16 project đạt.
- Thêm Create/Add Order và GET tracking; hỗ trợ `DINE_IN`/`TAKEAWAY`, giá lấy qua
  Menu REST client, session lấy qua Table REST client và bearer token được forward.
- Dùng `requestId` làm PK/idempotency key, kiểm tra min/max option và ghi actor event.
- Ordering HTTP PostgreSQL integration đạt create → retry idempotent → tracking;
  actor và tổng tiền đúng, toàn bộ dữ liệu test cleanup về 0.
- Sửa cảnh báo multiple collection include bằng `AsSplitQuery`; toàn solution
  đạt 49/49 test.

## 2026-10-23 — Branch-scoped catalog read API

- Ánh xạ sáu bảng Catalog có sẵn trong `aicos_db.public`; không tạo migration.
- Thêm `GET /api/v1/menu/catalog` với category filter và tìm kiếm ILIKE.
- Chỉ trả sản phẩm được mở bán tại branch JWT cùng category, variant và option active.
- Thêm model/DbContext configuration, DTO, query service, endpoint và tests tách biệt.
- PostgreSQL HTTP integration test đạt; tám bảng catalog/tenant/branch test trở
  về 0 row sau cleanup.

## 2026-10-23 — Table map and session lifecycle

- Ánh xạ bốn bảng Table có sẵn trong `aicos_db.public`; không tạo migration.
- Thêm API map theo branch JWT, mở phiên, đóng phiên và xác nhận dọn xong.
- Áp dụng state machine `AVAILABLE → OCCUPIED → CLEANING → AVAILABLE`.
- Dùng transaction `SERIALIZABLE`, branch scope, role authorization, logging và
  RFC 7807 conflict để bảo vệ thao tác đồng thời.
- Build 16 project đạt 0 warning/error; smoke tests đạt 37/37 trước lần kiểm tra
  toàn solution cuối.
- PostgreSQL integration test thật đạt bốn HTTP endpoint; sáu bảng Table và dữ
  liệu tenant/branch test trở về 0 row sau cleanup.
- Ghi nhận schema chưa có số khách/người mở phiên và partial unique index; chưa
  tự ý tạo SQL hoặc apply migration.

## 2026-10-23 — Identity staff provisioning

- Thêm `POST /api/v1/identity/staff` với policy `Management`.
- Tạo đồng thời user, role chuẩn nếu còn thiếu, tenant membership và branch
  assignment; password chỉ được lưu dưới dạng hash.
- Manager bị giới hạn trong branch của JWT và không thể cấp role Manager;
  SystemAdmin được phép cấp Manager tại branch active.
- Chặn email trùng, role không hỗ trợ và xung đột ghi đồng thời bằng lỗi RFC 7807.
- Không thêm migration hoặc thay đổi schema PostgreSQL.
- Build 16 project đạt 0 warning/error; smoke tests đạt 31/31.
- Integration test thật đạt luồng Manager login → tạo Cashier → chặn email trùng
  → Cashier login; 7 bảng Identity/session trở về 0 row sau cleanup.

## 2026-10-23 — Apply Identity public baseline

- Người dùng đã duyệt SQL và migration đã apply vào `aicos_db`.
- Giữ nguyên 35 bảng gốc; thêm `public.__ef_migrations_history` và
  `public.refresh_sessions`.
- History ghi nhận baseline public và AddRefreshSessions; không còn migration
  pending cho Identity.
- Xác minh 3/3 FK refresh là `NO ACTION`, 5 index và 0 refresh row ban đầu.
- Thêm PostgreSQL integration test tùy chọn qua `AICOS_TEST_POSTGRES`; khi bắt
  buộc chạy trên `aicos_db`, HTTP login/me/refresh/logout đều đạt.
- Integration test cleanup đúng các ID ngẫu nhiên; row count Identity/refresh
  sau test trở về 0.

## 2026-10-23 — Map Identity trực tiếp vào `aicos_db.public`

- Xác nhận connection runtime tới `aicos_db` và 35 bảng `public`.
- Loại connection Development cũ trỏ nhầm database `aicos`; source không lưu password.
- Chuyển Identity DbContext sang sáu bảng `public`, khớp nullability và
  `timestamp without time zone` hiện tại.
- Loại hai migration schema `identity` chưa apply; thay bằng baseline không DDL
  và migration chỉ thêm `public.refresh_sessions`.
- Sinh `docs/PENDING_IDENTITY_PUBLIC_MIGRATION.sql` để chờ duyệt; chưa apply
  vào `aicos_db`.
- Test migration trên bản sao schema-only đạt; 35 bảng gốc không bị sửa.
- Build 16 project không warning/error; Backend tests đạt 32/32.

## 2026-10-23 — Refresh session lifecycle

- Thêm refresh-token rotation, hash-only persistence và optimistic concurrency.
- Thêm `POST /auth/refresh`, `POST /auth/logout` và `GET /auth/me`.
- Thêm migration pending `20261023034706_AddRefreshSessions` với 3 FK
  `NO ACTION`; không apply vào `aicos_db`.
- Review SQL idempotent: không chạm schema `public`, không drop/delete dữ liệu.
- Hai migration Identity chạy thành công trên PostgreSQL tạm; database tạm
  đã được xóa.
- Build 16 project không có warning/error; Backend tests đạt 32/32.

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
