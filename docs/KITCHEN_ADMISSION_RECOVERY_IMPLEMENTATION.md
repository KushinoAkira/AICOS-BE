# P0 Kitchen Order Admission Recovery — implementation và nghiệm thu

Follow-up 2026-11-07: durable HTTP admission vẫn tạo dữ liệu tem nhưng không phát legacy `CupStickersPrinted` (có thể kích auto-printer). Thêm preview chỉ đọc, common lock cho legacy generation/transfer và chạy lại 18 recovery tests thật có assertion không phát print event, cùng 9 operations tests. Chi tiết [STAFF_ORDER_REMAINING_OPERATIONS_RESULT.md](STAFF_ORDER_REMAINING_OPERATIONS_RESULT.md). Không có migration/rollout mới.

Cập nhật 2026-11-07 (Asia/Saigon). **Code đã triển khai và test trong môi trường cô lập; CHƯA deploy hoặc apply migration trên database vận hành.** Các feature flag mặc định false. Các migration có timestamp UTC 20261106.

## Kết quả chính

- Order và admission outbox được lưu cùng transaction trong OrderingDbContext, phủ POS/QR checkout/Web pickup. Capture failure hoặc commit failure rollback cả hai; client retry cùng actor/key giữ một Order và một admission EventId/hash.
- Kitchen có internal HTTP receiver riêng, machine JWT riêng và scoped theo tenant/branch/purpose. Redis legacy OrderCreated và HTTP dùng chung admission writer/khóa PostgreSQL; receipt và ticket/items/stickers commit cùng transaction trước ACK.
- Dispatcher claim batch bằng SKIP LOCKED, lease token, renew trước xử lý, fencing khi cập nhật ACK/retry; không giữ transaction DB trong lúc HTTP. Timeout/mất ACK không tạo ticket hoặc in thêm sticker.
- Reconciliation lưu checkpoint/sweep upper bound/cursor/lease trong PostgreSQL; upsert admission còn thiếu cho mọi Order còn được lưu, không cutoff theo tuổi. Insert phía trước cursor được sweep tiếp theo nhìn thấy. Kitchen offline không chặn tiến trình capture/sweep; probe receipt được hoãn sang vòng sau.
- Ticket thiếu nhưng source đã Preparing/Ready/Completed không có DeliveredAt chuyển recovery hold; không hiện queue/batches, không start/remake/sticker qua direct ID. Chỉ terminal evidence hợp lệ mới tự clear hold. Ticket Ready/Completed hiện có không bị reset bởi replay cũ.
- HTTP admission không dựa vào Redis Pub/Sub để ACK. Subscriber legacy được bổ sung retry startup khi Redis không sẵn sàng để không làm dừng HTTP host; các channel đã đăng ký không bị gắn callback lần hai khi retry. Không đổi semantics tài chính hoặc triển khai transport durable tổng quát cho toàn hệ thống.

## API và authentication

| API | Auth | Behavior |
| --- | --- | --- |
| POST `/internal/v1/kitchen/order-admissions` | Scheme KitchenAdmissionMachine, scope kitchen.admission.write | 200 Applied sau commit; 202 NeedsReview; 409 identity/hash/integrity conflict |
| POST `/internal/v1/kitchen/order-admissions/lookup` | Cùng scheme, scope kitchen.admission.lookup | Chỉ đọc, tối đa 100 exact OrderIds; không trả row ngoài tenant/branch/request |
| GET `/api/v1/kitchen/recovery/status` | BranchManager/BrandAdmin | Tối đa 100 ticket hold trong phạm vi được phép, không trả raw payload/secret |

Admission JSON thực tế: `eventId`, `schemaVersion`, `consumerKey`, `tenantId`, `branchId`, `orderId`, `creation` (`orderCode`, `items`: productId/productName/quantity/note), `creationHash`, `source` (orderType/status/isPaid/tableNumber/createdAtUtc/pickupSlotStartUtc/deliveredAtUtc/cancelledAtUtc). ACK gồm eventId/consumerKey/tenantId/branchId/orderId/ticketId/result/receiptStatus. Request lookup gồm tenantId/branchId/orderIds; response có một presence cho mỗi OrderId được yêu cầu. Tên JSON là camelCase theo `AdmissionProtocol.Json`; đây là contract mới nội bộ, không đổi DTO public.

Machine issuer `aicos-ordering-admission`, audience `aicos-kitchen-admission`, client_id=ordering, TTL 1 phút/max lifetime 2 phút; HMAC-SHA256 key riêng tối thiểu 32 bytes và khác Staff Jwt:SigningKey. Staff/Guest JWT không truy cập internal endpoints. Request body tenant/branch phải khớp machine claims; producer identity không lấy từ body. Không tạo signing key vận hành trong lượt này.

Internal routes exclude public OpenAPI; Gateway hiện chỉ forward các prefix `/api/v1/...` và hubs nên không thêm route `/internal/`. Không đổi endpoint/request/response public cũ. Status endpoint mới trả danh sách hold, chưa triển khai dashboard backlog/lag metric tổng hợp hoặc API clear hold/requeue thủ công.

## Code thay đổi

| Nhóm | File chính |
| --- | --- |
| Contract/auth | `src/BuildingBlocks/AICOS.Contracts/KitchenAdmission.cs`, `src/BuildingBlocks/AICOS.BuildingBlocks/Security/AdmissionMachineAuthentication.cs` |
| Ordering model/capture | `Persistence/OrderingDbContext.cs`, `Features/KitchenAdmission/AdmissionOutbox.cs` |
| Ordering transport/workers | `Features/KitchenAdmission/KitchenAdmissionTransport.cs`, `KitchenAdmissionDispatcher.cs`, `KitchenAdmissionReconciler.cs`, `KitchenAdmissionWorkers.cs`, `Program.cs` |
| Kitchen model/receiver | `Features/KitchenAdmission/AdmissionReceipt.cs`, `KitchenAdmissionService.cs`, `KitchenAdmissionEndpoints.cs`, `Persistence/KitchenOperationsDbContext.cs` |
| Kitchen shared writer/hold | `Features/KitchenEventHandlers.cs` (OrderCreated/shared announce only), `KitchenTicketTransitions.cs`, `KitchenEndpoints.cs`, `Domain/Entities/KitchenTicket.cs`, `Program.cs` |
| Redis availability | `BuildingBlocks/Messaging/RedisEventBusSubscriber.cs` |
| Config | Hai appsettings.json, hai launchSettings.json, environment riêng order/kitchen trong docker-compose.yml và `.env.example` |
| Tests | `KitchenAdmissionRecoveryTests.cs`, `TestKit.cs`, `OperationalTestTarget.cs`, `tests/AICOS.AdmissionCrashProbe/`, `scripts/Test-StaffOperationsInfrastructure.ps1` |

Không sửa service Payments/Bill, handler KitchenPaymentSucceeded, migration cũ hoặc Mobile/UI. Working tree đã có nhiều thay đổi từ lượt trước; không coi toàn bộ Git diff là code mới của task này.

## Database/migration

| Service | Migration mới | Thay đổi |
| --- | --- | --- |
| Ordering | `20261106164220_KitchenAdmissionOutbox` | kitchen_admission_outbox + kitchen_admission_reconcile_state |
| Kitchen | `20261106164227_KitchenAdmissionReceipts` | order_admission_receipts + nullable kitchen_tickets.recovery_hold_reason |

Model snapshots được cập nhật; không chỉnh migration lịch sử đã chốt. Không tạo lại unique kitchen_tickets(tenant_id, order_id), không có FK xuyên service. FK receipt→ticket là nội bộ Kitchen với RESTRICT.

SQL xuất từ EF để review, chưa chạy trên vận hành:

- `docs/migrations/kitchen-recovery/01_ordering_generated.sql`
- `docs/migrations/kitchen-recovery/02_kitchen_generated.sql`
- `docs/migrations/kitchen-recovery/SHA256SUMS.txt`

Khác biệt có chủ đích với proposal: EF dùng unique index thay unique constraint cho business key (tương đương cho ON CONFLICT), thêm index FK receipt.ticket_id; gom một số CHECK thành constraint có tên ổn định. EF không thêm server defaults ở các cột app luôn cung cấp. Proposal cũ và checksum giữ nguyên như bản review lịch sử; không chạy cả proposal lẫn EF migration để tạo cùng object.

Trong test, chạy full EF migration chain trên **hai database trống tạm** sau guard, không dùng EnsureCreated thay cho bằng chứng migration. App startup vẫn MigrateOnStartup=false. Migration Up mới chỉ thêm object; generated Down có xóa object mới/cột hold, **không được tự chạy** để rollback dữ liệu đã dùng. Không có Grant/Revoke được thực thi.

## Kiểm thử thực tế

### Infrastructure: 18 PASS, 0 FAIL, 0 SKIP

Runner: `powershell -NoProfile -File scripts/Test-StaffOperationsInfrastructure.ps1 -Run -Recovery`.

PostgreSQL 16 + Redis 7 mới, network riêng, tmpfs, cổng loopback ngẫu nhiên. Database allowlist `aicos_operations_ordering_test`, `aicos_operations_kitchen_test`; exact container/cluster/network/port guards trước test schema/fixtures. Machine key random riêng mỗi fixture. Không dùng Windows PostgreSQL, main DB hoặc credentials vận hành.

| Case | Kết quả |
| --- | --- |
| Kitchen offline lúc tạo Order; restart rồi nhận qua outbox | PASS |
| Mất HTTP ACK sau commit, restart cả service test và replay | PASS |
| Hai dispatcher + legacy Redis cùng Order; ticket/items/stickers một bộ | PASS |
| Legacy Order 3 năm trước, >100 rows, checkpoint qua batch và insert trước cursor | PASS |
| Hold bị chặn queue/direct start/remake/sticker; chỉ clear khi có delivered evidence | PASS |
| Staff JWT, thiếu token, sai signature/hết hạn/scope/tenant | PASS |
| Cùng eventId khác hash/OrderId; eventId mới cùng business Order | PASS |
| Capture failure và transaction commit failure | PASS: không commit nửa Order/outbox |
| POS, QR checkout, Web pickup đều capture | PASS |
| OS process Ordering bị kill sau commit trước publish | PASS |
| OS process Kitchen bị kill trước và sau local commit | PASS |
| Redis container tạm stop thật; Kitchen khởi động khi Redis offline; HTTP vẫn admit và health live | PASS |
| Hosted dispatcher chạy tự động, không gọi DispatchOnce từ test | PASS |
| Lease cũ hết hạn, worker khác ACK; ACK cũ không ghi đè; không idle-in-transaction lúc HTTP | PASS |
| Replay admission cũ không reset Ready/Completed | PASS |
| Retry public create cùng actor/key giữ một Order + EventId/hash | PASS |

Process probes là **OS processes thật bị kill**, chạy DbContext/shared admission writer tại ranh giới persistence; không phải kill các production containers. HTTP/machine auth được test riêng qua ASP.NET TestServer. Identity/Menu/Table/SignalR notifier và QR cart cache trong harness là doubles; PostgreSQL, Redis publisher/subscriber, migration, DB constraint, transaction và machine auth là thật. Không gọi test characterization lỗi cũ là recovery thành công.

Lần chạy cuối: cluster `7705106591987855395`, network `aicos-staff-operations-187061540c`, cổng 62146/62147. Raw TRX ở đường dẫn temp trong `KITCHEN_ADMISSION_RECOVERY_RUN.md`. Snapshot ID/image/StartedAt/status/RestartCount của các container có trước giữ nguyên; chỉ hai container/network tạm của run được cleanup, không xóa Docker volumes.

### Regression và validation

- **458 PASS, 0 FAIL, 0 SKIP**: toàn bộ HTTP/InMemory regression trong Services.IntegrationTests, loại các suite infrastructure/CashPostgres opt-in. Bao gồm test tài chính mock đã có để kiểm tra tương thích; không gọi API tài chính vận hành.
- **15 PASS, 0 FAIL, 0 SKIP**: BuildingBlocks.Tests.
- Build thành công; còn 2 warnings có sẵn ở WorkforceReviewTests/OrderTests, không có lỗi compile mới.
- Compose merged configuration validation `--quiet` PASS; không in secrets hoặc chạy stack.
- Formatter và Git diff checks chạy trên các file recovery; không viết lại code unrelated.

Các failure đã sửa trong quá trình test: fault ACK bám nhầm Order trong shared test DB; reconciler lookup offline chặn sweep; test checkpoint giả định Order mới luôn nằm trong sweep đang dở. Lần cuối toàn bộ 18 cases trên PASS.

## Cấu hình rollout — chỉ chuẩn bị

| Biến Compose | Default |
| --- | --- |
| KITCHEN_ADMISSION_CAPTURE_ENABLED | false |
| KITCHEN_ADMISSION_DISPATCHER_ENABLED | false |
| KITCHEN_ADMISSION_RECONCILIATION_ENABLED | false |
| KITCHEN_ADMISSION_RECEIVER_ENABLED | false |
| KITCHEN_ADMISSION_MACHINE_KEY | rỗng, lấy qua secret store/env khi được duyệt |

Local .NET dùng các biến tương đương KitchenAdmission__CaptureEnabled/DispatcherEnabled/ReconciliationEnabled/ReceiverEnabled/MachineKey. Không sử dụng key minh họa trong tài liệu cho vận hành. `Database__MigrateOnStartup=false` ở hai appsettings, launch profiles và override riêng order/kitchen trong Compose; không thay flag các service khác.

**Không chạy binary mới vào DB vận hành chưa expand schema:** flags false tắt capture/worker/receiver nhưng EF Kitchen vẫn map cột hold trong query ticket. Runtime containers hiện chưa được rebuild/deploy nên chưa tự nhận code mới.

Thứ tự cần duyệt riêng trước rollout: backup/restore+preflight → hai EF migrations → quyền SELECT/INSERT/UPDATE của roles với bảng mới → machine key/TLS/network → receiver/hold guards → capture → dispatcher → reconciliation có throttle và theo dõi backlog. Không tự áp các bước này.

## FAIL / NOT RUN / BLOCKED còn lại

- **FAIL hiện tại của phiên bản test cuối: 0**. Bằng chứng recovery PASS chỉ áp dụng implementation và test target này; không tuyên bố main runtime đã được sửa/deploy.
- **NOT RUN:** production migration/ACL, Docker build/deploy image mới, full Web FE UI E2E, kill toàn bộ container API runtime, network proxy chaos/long-outage/load soak, migration downgrade trên dữ liệu thật.
- **BLOCKED/ngoài phạm vi:** Serving từng món, Staff Claim, sửa/hủy workflow mới, merge bàn có khách, recovery tài chính hoặc Started/Ready durable toàn hệ thống, API giải quyết hold/requeue thủ công và immutable audit cho các thao tác đó.
- Source snapshot eventual consistency không giải quyết atomic Cancel-vs-Brew. IsPaid hiện hữu của ticket không bị reconciliation ghi đè. Source Preparing/Ready thiếu ticket hoặc legacy immutable content/hash mâu thuẫn phải review, không đoán rồi pha lại.
- Reconciliation giữ ledger không giới hạn thời gian trong MVP; cần retention/observability và Web review trước production. Không tự bỏ Order quá cũ để làm test/metric xanh.

## Rollback

Tắt capture/dispatcher/reconciliation, giữ outbox/receipts/checkpoint; dừng backfill trước đổi version. Không rollback binary mất hold guard khi còn ticket hold. Không xóa ledger, không reset DB, không tự chạy EF Down. Nếu tắt recovery phải ghi nhận degraded và giữ backlog để resume. Chỉ downgrade schema sau backup và phê duyệt riêng.
