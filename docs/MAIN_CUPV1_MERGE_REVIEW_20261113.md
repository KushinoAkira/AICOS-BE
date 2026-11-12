# Tích hợp main và CupV1 — 2026-11-13

## Phạm vi và trạng thái

Người dùng yêu cầu tích hợp an toàn sau audit xung đột. Thực hiện trên nhánh riêng `integration/main-cupv1-20261113`, bắt đầu từ `be/mobile-staff-operations-20261112` (`8de8647`), merge `origin/main` (`02a102c`). Hai nhánh lệch 9 commit main / 2 commit BE trước khi ghép.

Không thay đổi nhánh gốc `main` hoặc `be/mobile-staff-operations-20261112`, không push, không deploy, không khởi động container, không kết nối/thay đổi database. Không sửa Mobile. Không sửa Payments/Bill. Merge được lưu bằng commit cục bộ trên nhánh tích hợp để không để repository trong trạng thái merge dở; chưa đưa vào main.

## Xử lý 5 xung đột

| File | Quyết định |
| --- | --- |
| Contracts/OrderEvents.cs | Giữ PayFirst của main và thêm FulfillmentMode; mặc định false/WholeOrder để đọc được JSON cũ. |
| KitchenEventHandlers.cs | Giữ early-payment persistence và khóa theo order của main; giữ kiểm tra branch, guard CupV1, log và recovery của nhánh BE. Không gọi legacy sticker generation trên ticket CupV1. |
| AdmissionOutbox.cs | Giữ cả request V1 và V2; cả hai truyền PayFirst từ Order. |
| OrderSupport.cs | Publisher truyền cả PayFirst và FulfillmentMode bằng named arguments. |
| KitchenTests.cs | Giữ test của cả hai phía: payment đến sớm, branch isolation và QR pay-first. |

## Lỗi tương thích không được Git phát hiện

`CupAdmissionWriter` trước merge tạo KitchenTicket không truyền PayFirst. Constructor mới có default false, nên compile có thể thành công dù QR Cup ticket trở thành đủ điều kiện pha quá sớm. Đã truyền PayFirst, giữ policy trên replay, và đặt recovery hold nếu source replay đổi policy. Không tự xóa hold.

V2 cũng tiêu thụ bản ghi EarlyPayments do handler của main ghi khi payment đến trước ticket, trong cùng khóa/transaction admission. Replay không tạo lại ticket/cup/sticker hoặc reset preparation. Không thêm bảng, migration hoặc thay đổi mô hình dữ liệu mới cho bản sửa merge này.

## Kiểm thử thực tế

| Kiểm tra | Kết quả |
| --- | --- |
| dotnet build AICOS.sln | PASS; sau restore NuGet assets còn thiếu, 0 errors. |
| BuildingBlocks regression | 15 PASS, 0 FAIL, 0 SKIP |
| Identity regression | 202 PASS, 0 FAIL, 0 SKIP |
| Service regression | 503 PASS, 0 FAIL, 0 SKIP |
| Gateway/service in-process smoke | 50 PASS, 0 FAIL, 0 SKIP |
| Tổng lượt regression solution | 770 PASS, 0 FAIL, 0 SKIP |
| MergeCupCompatibilityTests sau bổ sung metadata check | 7 PASS, 0 FAIL, 0 SKIP |
| Model/snapshot Kitchen và Ordering | PASS bằng EF metadata offline, không mở kết nối PostgreSQL |
| PostgreSQL/Redis E2E và Docker Compose business smoke | NOT RUN trong lượt merge |
| Mobile/device/Web E2E với bản merge | NOT RUN |

6 test compatibility đã nằm trong 770; lượt 7 test sau gồm 6 test đó và 1 metadata test mới, không cộng thành 777 test độc lập. Test service sử dụng TestServer/EF InMemory/recording event bus và test identity; không đại diện cho concurrency/crash atomicity trên PostgreSQL thật. Smoke là host trong process, không phải Docker stack.

Loại khỏi lệnh regression: StaffOperationsInfrastructureTests, KitchenAdmissionRecoveryTests, CashPostgresTests, CupServingInfrastructureTests, CupServingAclInfrastructureTests. Đây là NOT RUN, không phải PASS hoặc test bị skip bởi runner. Hai warning CS0628/CS8620 có sẵn trong test suite vẫn tồn tại.

Trong quá trình kiểm tra: build no-restore đầu tiên thiếu assets của Gateway; đã restore/build thành công. Test mới ban đầu thiếu Role của test principal, sau đó dùng entity đã bị unit-of-work clear tracking; đã sửa test principal và mô phỏng fresh request tracking, chạy lại 7 PASS. Không bỏ assertion nghiệp vụ để làm test xanh.

Lệnh regression:

```powershell
dotnet test AICOS.sln --no-restore --filter 'FullyQualifiedName!~StaffOperationsInfrastructureTests&FullyQualifiedName!~KitchenAdmissionRecoveryTests&FullyQualifiedName!~CashPostgresTests&FullyQualifiedName!~CupServingInfrastructureTests&FullyQualifiedName!~CupServingAclInfrastructureTests' --logger 'console;verbosity=minimal'
dotnet test tests/AICOS.Services.IntegrationTests --no-restore --filter FullyQualifiedName~MergeCupCompatibilityTests --logger 'console;verbosity=minimal'
```

## Schema, runtime và rủi ro còn lại

- Giữ nguyên các migration đã có. Import từ main gồm `QrOrdersPayFirst`, `KitchenEarlyPayments`, `WorkShiftSoftDelete` cùng designers/snapshots. Không chỉnh sửa nội dung hoặc thực thi chúng.
- Cup foundations đã tồn tại trên cả hai phía; không tạo lại unique index hoặc migration Cup mới. EF model/snapshot Ordering/Kitchen sau ghép khớp nhau. Điều này không chứng minh database vận hành đã có schema đó.
- Compose gốc kế thừa `Database__MigrateOnStartup=true`; overlay `docker-compose.main-runtime.yml` đặt false. Không chạy Compose gốc lên môi trường đang dùng. Phải xác minh cấu hình merged với overlay và đúng image/DB trước bất kỳ rollout riêng nào.
- Runtime Ordering hiện dùng image session hotfix trước Cup schema. Bản ghép source không được dùng thay image này khi chưa audit/apply schema có phê duyệt riêng.
- Feature flags Cup/admission/reconciliation không được bật trong task này. TTL/Manager, Dine-in close/transfer và E2E runtime còn theo các blocker đã ghi trong CUP_SERVING_V2_IMPLEMENTATION.md.
- EarlyPayments kế thừa main dùng khóa tenant/order, không lưu branch; nhánh tích hợp giữ nguyên schema này. Kiểm tra event provenance/ACL và race trên PostgreSQL thật thuộc bước runtime/test cô lập tiếp theo.
- Push main kích hoạt CI/image workflows của repository; docker-images có build/scan/push GHCR và Compose smoke. Không coi build/test local là CI Docker PASS; chưa push hoặc chạy workflow này.

## Đưa vào main và quay lại an toàn

1. Review diff và kết quả này; test PostgreSQL/Redis/Docker trên môi trường cô lập trước khi quyết định deploy.
2. Khi được duyệt đưa code vào main: fetch lại, kiểm tra origin/main vẫn là commit đã ghép. Nếu main thay đổi, ghép thay đổi mới trên nhánh tích hợp và chạy lại test liên quan. Không force push.
3. Chỉ fast-forward main đến merge commit đã duyệt rồi push; database deployment là bước tách biệt, chưa được phê duyệt tại đây.
4. Hiện main và nhánh BE gốc vẫn trỏ commit cũ, nên có thể quay về nhánh gốc khi working tree sạch. Không cần reset, xóa code hoặc rollback database. Nếu sau này cần hoàn tác trên main đã chia sẻ, chuẩn bị revert merge qua review; không rewrite lịch sử.

Kết luận: PASS cho build/regression và tích hợp source trên nhánh riêng; chưa production-ready hoặc xác nhận runtime/schema E2E.
