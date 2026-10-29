# Gate D1-B — Schema và service ACL trên PostgreSQL TEST cô lập

Ngày: 2026-10-29 (Asia/Saigon). Phê duyệt: chỉ schema/service roles/ACL TEST.
Kết quả cuối: **PASS_SCHEMA_ACL — DỪNG SAU D1-B**.
Preflight bên dưới được xuất khi database còn rỗng, trước lần Apply đầu tiên.

## Preflight được xuất trước khi thực thi

| Điều kiện | Kết quả |
|---|---|
| Đích | aicos_payment_test / public trong container aicos-gate-d-postgres |
| Container ID pin | 6219c61c40c586676402a3163780b98dd57e9bdcdac9650a0a9f39241093d43b |
| PostgreSQL / system identifier | 18.6 / 7701931530273472557; khớp manifest, pg_controldata và SQL READ ONLY |
| Transport | DockerExec pin ID; TCP gated-postgres:5432 bên trong container; không host connection |
| Network | aicos-gate-d_internal, internal=true, ID 058719a762be95ed2cef5bcb8a6918320ac0df5d8050d0b7e05c2787ae1a6493; đúng hai container test |
| Volume PG / Redis | aicos-gate-d_pg18-data / aicos-gate-d_redis-data; đúng project, không bind mount |
| Host ports | PostgreSQL và Redis không publish cổng host |
| Trước apply | 0 bảng public, 0 service roles, không function public; không client SQL khác trong DB test |
| Redis test | PONG, DB0 có 0 keys |
| Gate C | 13 container giữ ID/image/StartedAt; Redis healthy |
| Windows PostgreSQL | Không kết nối aicos_db hoặc aicos_payment_test trên Windows |

Preflight thật và checkpoint schema rỗng được lưu ngoài Git:
`%LOCALAPPDATA%/AICOS/gate-d/d1b_20261029_133000/preflight.json` và
`before-schema.sql`. Không chứa mật khẩu/JWT/dữ liệu nghiệp vụ.

### DDL dự kiến, đã review toàn bộ

Nguồn duy nhất là artifact schema-only sau Gate B từ backup Gate A, không restore
file dump dữ liệu. Kiểm tra SHA-256 và phân loại toàn bộ statement; không có
statement chưa phân loại hoặc tham chiếu schema/server ngoài public.

- 38 CREATE TABLE, 244 cột; bao gồm payment_events và cấu trúc Identity/Payment
  đã chốt. Không chạy lại EF/Payment migrations.
- 38 PK, 61 FK, 8 UNIQUE constraints, 20 CHECK constraints.
- 20 CREATE INDEX riêng; cộng index hỗ trợ PK/UNIQUE thành 66 indexes.
- 1 function reject_payment_event_mutation chỉ RAISE EXCEPTION; 1 trigger
  payment_events append-only BEFORE DELETE OR UPDATE; 2 column comments.
- 13 thiết lập pg_dump/search_path. Runner đặt lại public/timeouts sau restore.
- 11 CREATE ROLE NOLOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE, NOINHERIT,
  NOREPLICATION, NOBYPASSRLS, connection limit 10; ALTER ROLE search_path=public.
  Không đặt mật khẩu, không bật LOGIN, không tạo user ứng dụng/JWT.

### Quyền dự kiến

Mọi role nhận CONNECT database TEST và USAGE public. Không service sở hữu object,
không role membership/admin, grant option, DELETE/TRUNCATE/DDL hoặc sequence grants.

| Role | SELECT | INSERT | UPDATE theo cột |
|---|---|---|---|
| identity_svc | tenants, branches, users, roles, tenant_users, staff_assignments, refresh_sessions | users, roles, tenant_users, staff_assignments, refresh_sessions | refresh_sessions: revoked_at_utc, replaced_by_session_id, version |
| menu_svc | categories, products, product_variants, product_option_groups, product_options, branch_products | Không | Không |
| table_reservation_svc | table_areas, cafe_tables, table_sessions, table_session_tables | table_sessions, table_session_tables | cafe_tables.status; table_sessions.status/closed_at; table_session_tables.left_at |
| ordering_svc | orders, order_items, order_item_options, order_item_events | Cùng 4 bảng | Không |
| kitchen_svc | orders, order_items, order_item_events | order_item_events | order_items.status |
| payment_svc | bills, bill_items, payments, payment_events | Cùng 4 bảng | bills: status/closed_at/closed_by_user_id/version; payments: status/confirmed_by_user_id/confirmed_at/evidence_reference/paid_at/version |
| inventory_svc, workforce_svc, reporting_svc, customer_svc, ai_svc | Không | Không | Không |

Tổng 44 cặp role/table/privilege và 18 cặp role/table/column UPDATE.
REVOKE chỉ áp dụng 11 roles vừa tạo trong cluster TEST: direct CREATE/TEMPORARY
database, CREATE schema, quyền bảng/cột/sequence. Không thay PUBLIC ACL hay role khác.
TEMPORARY kế thừa PUBLIC có thể vẫn effective=true; ghi nhận ngoại lệ baseline,
không tự thu hồi PUBLIC để làm kết quả đẹp hơn. Kitchen dùng bảng Ordering là ngoại
lệ hiện có đã duyệt, không mở rộng quyền trong D1-B.

### Kiểm tra câu lệnh nguy hiểm

Không có DROP/TRUNCATE/DELETE/INSERT/UPDATE dữ liệu hay COPY, extension/server,
CREATE DATABASE, dblink, shell metacommand hoặc kết nối database khác trong schema.
DELETE/UPDATE chỉ xuất hiện ở định nghĩa protective trigger và tên quyền ACL;
không phải lệnh xóa/sửa dữ liệu. REVOKE là thay đổi quyền TEST trong phạm vi duyệt.
Không dùng SQL của database chính, không mount initializer hay tự chạy startup migration.

### Checksum đã review

| Artifact | SHA-256 |
|---|---|
| schema.sql | CC6F19A5BAE3A8ACBCB096EC110A7D172B4C86D63C37E1AE18CF61C8E30158A3 |
| 00_target_guard.sql | 87AAD55E0859765304A294740856355FC559629278149F310AA960C877C9BFF7 |
| 01_runtime_roles.review.sql | 6142F365F84965E2CDE5C23A8216784290376E70D1BFC5DD96B9BEF7E4DE70E2 |
| 02_runtime_acl.review.sql | D3633094F54D510DDF0CA79867C5D673A795C0F8D15C46F3BE9C30C36EB02CFC |

### Thứ tự thực thi và rollback

Runner: docker/gate-d/Invoke-GateDSchemaAcl.ps1. Mặc định Preflight, target không
nhận tùy ý; bắt buộc manifest private D1-A, pinned container/system ID/network/volume,
SQL hashes khớp và evidence preflight PASS. Apply không tự retry nếu đã có attempt.

1. Preflight READ ONLY và lưu checkpoint TEST rỗng, guard Gate C.
2. BEGIN duy nhất: kiểm tra đích/empty/quiescent lần nữa; schema-only → reset
   search_path → roles → ACL → kiểm tra 38/244/11 NOLOGIN và tất cả bảng rỗng → COMMIT.
   Loại bỏ wrapper BEGIN/COMMIT trong hai script review để không commit sớm.
3. pg_dump schema-only READ ONLY; so definition chính xác với baseline (chỉ bỏ
   comments dump, restrict random tokens, dòng trắng và chuẩn hóa CRLF).
4. READ ONLY kiểm tra PK/FK/constraints/index, toàn bộ effective table/column ACL,
   ownership/membership/grant option/capabilities và Gate C preservation.

Lỗi trước COMMIT: ON_ERROR_STOP đóng connection, PostgreSQL rollback toàn transaction
bao gồm schema/roles/ACL, không cần DROP. Nếu commit rồi verify lỗi: dừng, giữ evidence,
không tự DROP/reset/restore/delete volume. Phương án forward fix hoặc restore checkpoint
trên TEST cần review/phê duyệt riêng; không bao giờ rollback main.

## Kết quả sau apply

Lượt đầu dừng trước COMMIT: PostgreSQL báo `record "t" is not assigned yet`.
Biến PL/pgSQL `t record` trùng alias `pg_class t` trong integrity verifier.
Đã sửa alias verifier thành `indexed_table`; không sửa schema/role/ACL SQL đã review.
Transaction rollback toàn bộ; READ ONLY sau lỗi xác minh 0 bảng/0 service roles,
Gate C không đổi. Evidence attempt lỗi giữ tại d1b_20261029_133000, không replay.
Lượt tiếp theo đã chạy preflight mới PASS tại d1b_20261029_140000 rồi Apply;
đã COMMIT schema/roles/ACL trên đúng cluster test, không replay attempt cũ.

### Before / after thực tế

| Thành phần | Trước | Sau | Kết luận |
|---|---:|---:|---|
| Bảng public | 0 | 38 | Khớp baseline |
| Cột public | 0 | 244 | Khớp baseline |
| Primary keys | 0 | 38 | Khớp |
| Foreign keys | 0 | 61 | Khớp, tất cả validated |
| UNIQUE constraints | 0 | 8 | Khớp |
| CHECK constraints | 0 | 20 | Khớp, tất cả validated |
| Indexes | 0 | 66 | Khớp, tất cả valid |
| Sequences | 0 | 0 | UUID baseline, không cần grant sequence |
| Function / trigger public | 0 / 0 | 1 / 1 | Append-only trigger nguyên vẹn |
| Service roles | 0 | 11 | NOLOGIN, không password, connection limit 10 |
| Tổng bản ghi 38 bảng | 0 | 0 | Không account, fixtures, payment hay EF history rows |

Các query kiểm tra catalog/empty state/privileges chạy trên PostgreSQL Docker thật,
không dùng mock. Từng role không ownership/membership, elevated capability hay grant
option. Tổng 3.344 kiểm tra effective table privileges (11 × 38 × 8) và 10.736 kiểm
tra effective column privileges (11 × 244 × 4) cho **0 sai khác** so với ma trận.
CONNECT/USAGE đạt; CREATE database/schema, DELETE/TRUNCATE/REFERENCES/TRIGGER/MAINTAIN
và full-table UPDATE không được cấp. Không có sequences để cấp quyền.

| Role | NOLOGIN / password NULL | SELECT thực qua SET LOCAL ROLE | ACL |
|---|---|---:|---|
| identity_svc | PASS | 7 bảng, LIMIT 0 | PASS |
| menu_svc | PASS | 6 bảng, LIMIT 0 | PASS |
| table_reservation_svc | PASS | 4 bảng, LIMIT 0 | PASS |
| ordering_svc | PASS | 4 bảng, LIMIT 0 | PASS |
| kitchen_svc | PASS | 3 bảng, LIMIT 0 | PASS |
| payment_svc | PASS | 4 bảng, LIMIT 0 | PASS |
| inventory_svc | PASS | 0 bảng được cấp SELECT | PASS |
| workforce_svc | PASS | 0 bảng được cấp SELECT | PASS |
| reporting_svc | PASS | 0 bảng được cấp SELECT | PASS |
| customer_svc | PASS | 0 bảng được cấp SELECT | PASS |
| ai_svc | PASS | 0 bảng được cấp SELECT | PASS |

SET LOCAL ROLE kiểm tra quyền của role thật trong transaction READ ONLY; **không**
phải đăng nhập bằng password. Authentication runtime chưa thực hiện vì NOLOGIN.
Cả 11 role có effective TEMPORARY=true do PUBLIC mặc định. Đã giữ nguyên PUBLIC ACL;
đây là ngoại lệ đã mô tả trước Apply, không coi direct REVOKE là chặn quyền kế thừa.

### Sai khác dump đã review, không bỏ qua constraint

Lượt Verify đầu sau COMMIT dừng do exact schema comparison: tám CHECK dùng mảng
trạng thái được PostgreSQL 18.6 deparse khác bản 18.4:

```sql
-- 18.4: ép toàn bộ mảng varchar[] thành text[]
(ARRAY['CASH'::character varying, 'BANK_TRANSFER'::character varying])::text[]
-- 18.6: ép từng phần tử thành text
ARRAY[('CASH'::character varying)::text, ('BANK_TRANSFER'::character varying)::text]
```

Đã review đủ ck_bills_print_status, ck_bills_status, ck_payment_events_from_status,
ck_payment_events_to_status, ck_payment_events_type, ck_payments_method,
ck_payments_status và ck_payments_terminal_reason. Không đổi trạng thái, toán tử,
null predicate, column type hoặc constraint logic. Tám SELECT thật xác minh cùng
array values và pg_typeof đều PASS; không gọi business API để thử CHECK.

Exact dump match=false. Definition match=true **sau chuẩn hóa hẹp đúng tám CHECK
casts đã review**, cùng loại bỏ dump comments/restrict tokens/dòng trắng/CRLF.
Mọi definition còn lại giống baseline, bao gồm FK target, defaults, indexes,
triggers/function body và kiểu dữ liệu. Không chỉ so tổng số bảng/cột.

### Kiểm tra đã chạy và chưa chạy

- PASS guard metadata D1-A: 28 cases, synthetic metadata/connection strings;
  không được gọi là PostgreSQL business integration tests.
- PASS schema review guard: 8 cases thuần text, bao gồm chặn thay trạng thái,
  decimal type, FK target, NOT NULL, operator và DEFERRABLE; chỉ chấp nhận
  tám CHECK cast forms được review.
- PASS PostgreSQL thật: target/system ID, schema-only apply + transaction rollback
  lượt lỗi, catalog/row counts, exhaustive ACL/capabilities, 11 SET ROLE probes,
  28 SELECT LIMIT 0 và 8 constant-array equivalence SELECTs.
- PASS bảo toàn: 13 Gate C container IDs/images/StartedAt giữ nguyên, Redis Gate C
  healthy; test network/volume/IDs không đổi, không publish host port.
- NOT RUN: ứng dụng Identity/Gateway TEST, API nghiệp vụ, seed/fixtures, JWT,
  password authentication cho service roles, dotnet business suites.
- Không có test suite nghiệp vụ được chạy hay báo skip thay cho PASS.

Không kết nối database Windows trong D1-B. Vì không audit SQL trên main, báo cáo
không đưa ra fingerprint main mới; bằng chứng bảo vệ là pinned isolated cluster,
không host port/network main, cùng Gate C container preservation. Không sao chép
dữ liệu/credentials/JWT main. Không sửa Mobile, credentials/ACL main, commit/push.

### Evidence cuối và file liên quan

Private run `%LOCALAPPDATA%/AICOS/gate-d/d1b_20261029_140000` gồm preflight.json,
before-schema.sql, apply-started.json, commit-result.json, after-schema.sql,
schema-acl-verification.json, role-select-probes.json, cast-equivalence.json,
result.json và manifest.json. Giữ attempt rollback trước tại d1b_20261029_133000.
Không có dữ liệu nghiệp vụ hoặc secrets trong các evidence này.

Files mới: docker/gate-d/GateD.SchemaPackage.psm1, Invoke-GateDSchemaAcl.ps1,
tests/Test-GateDSchemaReview.ps1 và tài liệu kết quả này. Cập nhật Gate D plan,
README runner và CHANGELOG. Không sửa bốn artifact schema/guard/roles/ACL đã pin.

Lệnh Verify READ ONLY để đối chiếu lại (không chạy lại Apply):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File docker/gate-d/Invoke-GateDSchemaAcl.ps1 `
  -InfrastructureRunDirectory "$env:LOCALAPPDATA/AICOS/gate-d/d1a_20261029_104735" `
  -EvidenceDirectory "$env:LOCALAPPDATA/AICOS/gate-d/d1b_20261029_140000" `
  -Action Verify
```

D1-A readiness cố ý yêu cầu 0 bảng/0 roles nên không còn là verifier thích hợp sau
D1-B; không nới guard cũ. Dùng runner stage D1-B với Preflight/Apply/Verify tách biệt.

## Điểm dừng và phần cần duyệt tiếp

Schema/ACL đã đủ cho bước chuẩn bị Identity test. Chưa sẵn sàng đăng nhập service
vì 11 role vẫn NOLOGIN/không password. Cần duyệt riêng: test credentials và bật LOGIN
đúng roles cần thiết; synthetic Identity fixtures/PasswordHasher; khởi động riêng
Identity/Gateway TEST với MigrateOnStartup=false và không auto-seed; kế hoạch test
Identity có ghi refresh sessions, provisioning và phát JWT hợp lệ trên TEST.
Không tự chuyển sang bước này, D2 hoặc Payment mutations.
