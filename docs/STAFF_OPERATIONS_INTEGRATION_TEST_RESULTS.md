# Kết quả kiểm thử tích hợp vận hành phi thanh toán

> Báo cáo này giữ bằng chứng baseline/characterization cũ. Recovery admission bật tính năng mới đã có 18 infrastructure tests PASS: [KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md](KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md). Chưa deploy vào runtime vận hành.

Ngày: 2026-11-05. Chỉ Ordering/Kitchen/Serving và contract gọi thêm món. Không chạy Payment/Bill hoặc migration.

## Môi trường và bằng chứng cô lập

- Được người dùng duyệt khởi tạo schema test tạm bằng EF `EnsureCreated`; không dùng `Migrate`, không thêm migration hoặc sửa schema vận hành.
- Runner `AICOS-BE/scripts/Test-StaffOperationsInfrastructure.ps1 -Run` tạo PostgreSQL 16 và Redis 7 mới. Hai database: `aicos_operations_ordering_test` và `aicos_operations_kitchen_test`; schema từ EF model hiện tại của mỗi service. Không dùng `aicos_db`, database Windows hoặc Gate D cũ.
- Chỉ gắn dedicated bridge mới; port random bind 127.0.0.1, Redis dùng port được chọn ngẫu nhiên nhưng giữ mapping qua restart. Docker Desktop ở máy này không publish port trên network `--internal`; runner Windows cần loopback nên không dùng network đó. Đây không phải network chặn mọi egress: an toàn đích ghi dựa trên container/cluster/database/port guard và không có credentials vận hành.
- PostgreSQL/Redis data paths ở tmpfs; không mount source, volume cũ, dump dữ liệu hoặc secrets. PostgreSQL test credential ngẫu nhiên trong environment của runner, không ghi tài liệu/Git/log. JWT test và các service clients là doubles trong test project, không dùng JWT/credentials Gate C.
- Guard kiểm tra exact container ID, label `aicos.purpose=staff-operations-test`, network duy nhất, tmpfs, loopback binding, database allowlist và PostgreSQL system_identifier trước EnsureCreated/fixture. Connection string không hợp lệ dừng trước khi tạo host. MigrateOnStartup bị ép false; expiry workers Ordering bị gỡ trong test host.
- Runner chụp ID/image/StartedAt/status/RestartCount của toàn bộ container có trước lần chạy; so sánh sau test. Các container có trước giữ nguyên. Cleanup chỉ hai container và network của lần chạy sau kiểm tra ID/label; không xóa volume.
- Bằng chứng raw TRX nằm dưới `%TEMP%/aicos-staff-operations-<run>/staff-operations.trx`; metadata đã che secrets ở `AICOS-BE/docs/STAFF_OPERATIONS_INFRASTRUCTURE_RUN.md`.

## Phạm vi test thật và test double

HTTP handler thật qua ASP.NET TestServer, EF/Npgsql và transaction/constraint/advisory lock PostgreSQL thật; production Redis publisher/subscriber thật, không gọi `HandleAsync` thay cho transport trong bộ này. Test hai Barista/hai Staff/duplicate-create giữ operation lock từ connection riêng và quan sát ít nhất hai writer chờ trong `pg_locks`, rồi thả khóa để xác minh kết quả.

Identity/branch/user directory, Menu pricing, Table QR lookup và notifier màn hình dùng doubles; token do test ký. Không phải full-stack Web/Mobile/Gateway/login E2E. Redis transport thật không chứng minh SignalR đa replica/backplane.

## Ma trận kết quả

| Case | Trạng thái | Bằng chứng và giới hạn |
| --- | --- | --- |
| Hai Barista cập nhật một Order | PASS | 8 PUT từ 2 Barista, PostgreSQL lock contention; một transition Brewing/notification, Ordering nhận Started qua Redis. Không phải exclusive claim. |
| Hai Staff xác nhận phục vụ | PASS | 2 POST deliver cùng lúc, một timestamp/actor bền vững, replay không đổi người/thời gian; Kitchen Completed qua Redis. |
| Request trùng | PASS | 10 create cùng key: một 201, chín 200, cùng OrderId; một persisted order và một ticket. |
| Stale trạng thái/lượt remake | PASS | PUT expectedStatus cũ và expectedRemakeCount cũ đều 409 ticket.stale. |
| Gọi thêm món sau đổi phiên | PASS | Fixture đóng phiên cũ, mở lượt mới; expectedSessionId cũ trả 409 qr.session_changed, không tạo order thứ ba. Đây không phải test public Close API hoặc race Close/Add hoàn chỉnh. |
| Kitchen hoàn tất khi chưa trả tiền | PASS | Sau deliver: Order vẫn Ready/Unpaid, Kitchen Completed và IsPaid=false; không gọi payment endpoint. |
| Retry sau mất HTTP response | PASS (fault injection) | Handler đã commit thật, DelegatingHandler test làm rơi response; retry create/deliver giữ ID/actor/timestamp. Không dùng database hoặc event bus mock. |
| Timeout mạng/process kill thực tế | NOT RUN | Chưa dùng proxy mạng hoặc kill API process; test trên chỉ mô phỏng mất response sau commit. |
| Redis restart và live reconnect | PASS | Restart duy nhất Redis tạm có guard; dữ liệu PostgreSQL còn, publisher/subscriber kết nối lại, OrderCreated mới tới Kitchen. |
| Tự phục hồi event lúc Kitchen offline | FAIL về yêu cầu recovery | Order commit khi không có Kitchen subscriber; sau Kitchen khởi động lại ticket vẫn thiếu. Test characterization PASS vì xác nhận đúng giới hạn hiện tại; không coi đó là recovery PASS. |
| Serving từng món, exclusive claim, sửa/hủy mới, occupied merge, outbox/inbox | BLOCKED | Thiếu schema/policy đã được duyệt; không triển khai để làm test xanh. |
| Web FE/production ACL/deploy | NOT RUN | Không có Web UI/runtime trong harness; admin role chỉ của cluster tạm, chưa kiểm chứng ma trận service ACL vận hành. |

## Kết quả tổng và lịch sử chạy

- Chạy trực tiếp thiếu cấu hình test: **0 PASS, 0 FAIL, 7 SKIP**. Đây là guard, không phải 7 test đã được thực thi thành công.
- Lần chạy infrastructure đầu có **5 PASS, 2 FAIL, 0 SKIP**: fixture bàn thiếu tableNumber và Redis probe timeout sau restart. Đã bổ sung tableNumber, chọn port Redis ổn định và kiểm tra lại binding sau restart; chạy lại PASS. Lần lỗi chưa chụp binding sau restart nên không kết luận chắc chắn nguyên nhân timeout chỉ từ giả thuyết đổi port. Không sửa nghiệp vụ để che failure.
- Lần chạy cuối có kiểm tra `pg_locks`: **7 PASS, 0 FAIL, 0 SKIP**, thời gian test 12 giây trên PostgreSQL/Redis thật. Cluster `7704701984166989859`, network `aicos-staff-operations-a8190207fe`, loopback ports `53867/53868`; runner đã cleanup hai container/network tạm. Xem metadata/TRX tương ứng trong Backend để đối chiếu.
- Backend regression phi thanh toán sau thay đổi TestKit: **216 PASS, 0 FAIL, 0 SKIP** (HTTP/InMemory, fake bus), tách biệt với 7 infrastructure tests thật.
- Flutter regression liên quan: **18 PASS** trên 3 file test; analyzer **0 issues**, formatter 195 file không thay đổi. Full Flutter 149 từ lượt trước là kết quả lịch sử, không cộng với test mới thành số E2E.
- Còn warning C# có sẵn tại WorkforceReviewTests và OrderTests; không sửa ngoài phạm vi.

## Kết luận và điều kiện tiếp theo

Nghiệp vụ whole-order hiện có đã có bằng chứng PostgreSQL/Redis tích hợp cho các case trên. Durable event recovery vẫn không đạt: Pub/Sub không replay event đã mất. Không có thay đổi business code trong lượt này để xử lý chức năng BLOCKED. Thiết kế MVP và phê duyệt cần thiết ở `SERVING_MULTI_STAFF_AND_RECOVERY_DESIGN.md`.

Trước rollout cần test instance/process riêng, network timeout thực, service roles ACL và Web compatibility; trước partial serving/change/merge/recovery cần duyệt schema/contract và migration riêng. Rollback test: dispose host và xóa duy nhất tài nguyên tạm có ID/label xác minh; không restore/reset database vận hành.
