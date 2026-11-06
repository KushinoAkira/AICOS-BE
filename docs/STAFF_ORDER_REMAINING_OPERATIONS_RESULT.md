# Remaining Staff Order Operations — 2026-11-07

Serving/Claim follow-up **DESIGN ONLY**: [Item-level Serving và Multi-Staff Claim](ITEM_LEVEL_SERVING_AND_MULTI_STAFF_DESIGN.md) đề xuất CupV1 từng quantity, Kitchen ownership, timeout review và durable Ordering projection, khác phạm vi PerLineV1 cũ dưới đây. Chưa được duyệt schema/business và chưa code, không thay đổi kết quả implementation/test lịch sử của báo cáo này.

## Phạm vi và kết quả

Tiếp tục implementation đã có, không viết lại luồng DONE. Đã đọc progress/design trong Mobile và `KITCHEN_ADMISSION_RECOVERY_IMPLEMENTATION.md`, đối chiếu Ordering/Kitchen/Gateway source. GAP chi tiết trong Mobile `docs/STAFF_ORDER_REMAINING_OPERATIONS_AUDIT.md`. Không sửa Payment/Bill, schema/model/migrations hoặc configuration vận hành; không deploy, bật recovery, commit/push.

| Task | Trước | Sau / API / giới hạn |
| --- | --- | --- |
| A — Tem ly | API_ONLY; GET legacy có thể sinh tem không qua common lock; generation bị đặt tên PrintedAtUtc | PARTIAL: preview API + Mobile; serialized legacy generation; không auto-print từ durable admission. Hardware và audited reprint BLOCKED |
| B — Serving | Whole-order idempotent; thiếu per-line correlation và số lượng giao | Giữ whole-order đã đúng; per-item BLOCKED, không bật UI giả |
| C — Multi-Staff | Duplicate delivery được khóa; không có assignment/lease/audit | Claim BLOCKED, không coi khóa Kitchen là claim |
| D — Order management | Xem Order/history/tên người gọi có API; submit edit/cancel chưa đủ policy/audit | Giữ chức năng đọc và cart; không bật mutation có thể thay đổi financial hoặc bỏ qua Kitchen |
| E — Chuyển/ghép bàn | Transfer API/three-dot đã có; Kitchen transfer writer chưa dùng cùng lock | PARTIAL: chuyển Kitchen projection qua common lock; merge/history/versioned transfer recovery BLOCKED |
| F — Mobile | Chưa có preview tem | Preview bằng GET thật, loading/error/retry, chặn Cashier, giữ Demo; 409/404 không fallback GET legacy |
| G — Test | Bằng chứng cũ không đủ cho thay đổi mới | Chạy lại 9 operations + 18 recovery PG/Redis; regression/UI bên dưới |

## A. Kitchen stickers

Thêm `GET /api/v1/kitchen/tickets/{id}/sticker-preview`, chỉ Barista/BranchManager/BrandAdmin, giữ tenant/branch scoping. Gateway đã có YARP catch-all Kitchen, không cần đổi route. Handler `KitchenStickerEndpoints` đọc AsNoTracking, không SaveChanges, không notification, không sinh row. Có hold → `409 ticket.recovery_review_required`; thiếu/sai cup sequence → `409 ticket.stickers_incomplete`; chưa eligible → `422 ticket.not_paid`. Thiếu token 401; sai role/branch 403; ticket tenant khác 404.

Response additive mới:

```json
{
  "ticketId": "<guid>", "orderCode": "BK-50", "tableNumber": "S09",
  "orderType": "DineIn", "totalStickers": 2,
  "printStatus": "UNKNOWN", "canPrint": false,
  "stickers": [{
    "stickerId": "<guid>", "orderId": "<guid>", "orderCode": "BK-50",
    "tableNumber": "S09", "productName": "Bạc xỉu", "cupIndex": 1,
    "totalCupsInOrder": 2, "customizations": "Size L · Ít đá · Trân châu",
    "generatedAtUtc": "2026-11-07T00:00:00Z"
  }]
}
```

Ví dụ rút gọn mảng; response thực phải đủ 2 tem. Tem sắp theo CupIndex, giữ nguyên Customizations từ Ordering bao gồm options/note. Không suy đoán structured size/sugar/ice bằng tách chuỗi tự do. Một quantity tạo một tem với GUID ổn định. Vị trí preview lấy từ Kitchen ticket projection sau transfer, có thể chậm nếu transfer event mất; không sửa lịch sử snapshot CupSticker.TableNumber.

Legacy GET `/tickets/{id}/stickers` giữ nguyên shape/RBAC/semantics sinh thiếu; nay dùng `KitchenTicketWrites.RunAsync`, reload dưới advisory transaction lock cùng admission/preparation/transfer. Không có unique cup-index mới hoặc migration. Chưa chạy old/new binary đồng thời trong deployment; replica cũ không tuân thủ lock vẫn là rủi ro rollout.

`PrintedAtUtc` legacy được gán khi constructor tạo row; không có máy in ACK. Preview đổi tên biểu diễn thành GeneratedAtUtc và luôn báo UNKNOWN/CanPrint=false. Không đổi cột hoặc trường DTO legacy, không tuyên bố đã in.

`KitchenAdmissionService.EnsureAsync(durable:true)` vẫn tạo dữ liệu tem và thông báo ticket hợp lệ, nhưng không phát `CupStickersPrinted`: recovery không được tự kích hoạt auto-printer legacy. Replay cũng không tạo thêm tem/reset trạng thái. Redis legacy live flow giữ event cũ tương thích. Source Web FE/driver ngoài repo chưa kiểm tra; nếu Web tự in dựa trên thông báo ticket mới thay vì print event, cần review riêng trước rollout.

Files Backend: `Features/KitchenStickerEndpoints.cs`, `KitchenEndpoints.cs`, `KitchenAdmission/KitchenAdmissionService.cs`; tests `KitchenTests.cs`, `KitchenAdmissionRecoveryTests.cs`, `StaffOperationsInfrastructureTests.cs` (đường dẫn tương đối dưới KitchenOperations API hoặc integration tests). Mobile: model `main_kitchen_stickers.dart`, repository adapter, `MainKitchenStickerPreview`, ticket card/orders view và route `main_staff_screen.dart`.

## E. Transfer writer

`KitchenTableTransferHandler.HandleAsync` xử lý OrderIds distinct/sorted, dùng `KitchenTicketWrites.ApplyAsync` cho từng Order, reload trong transaction và giữ terminal guards. Transfer chạy đồng thời StartBrewing không ghi đè status từ snapshot cũ. Không thay public transfer API hoặc Order/session/Bill mapping. CupSticker giữ snapshot tạo ban đầu; preview dùng vị trí ticket.

Chưa giải quyết event đảo thứ tự, transfer crash sau Ordering commit/trước Pub/Sub, permanent history hoặc Table service projection bị chậm. Cần transfer revision + durable event/audit riêng. Không gọi sửa này là hoàn thiện toàn bộ chuyển bàn. Ghép occupied session vẫn reject và disabled trên Mobile, giữ mô hình một session/một Bill.

## Những phần cần duyệt riêng

1. **Print lần đầu/in lại có kiểm soát:** cần driver/printer protocol và xác nhận hardware; đề xuất service-owned `kitchen.print_jobs` + `print_attempts`, không tạo ở lượt này. Job lưu tenant/branch/ticket/sticker snapshot, printer, operation key, copy type, requestedBy/time, expected ticket cycle/version, reason và approver cho reprint. Unique tenant/printer/requestKey; attempts lưu dispatch/result/device ACK, không overwrite lịch sử. State `Pending → Dispatched → Acknowledged`, timeout sau dispatch là `Unknown`, không tự retry ra thêm bản giấy. Cần kiểm tra hold khi enqueue và trước dispatch; không enqueue từ recovery. Đề xuất GET jobs, POST print-jobs và POST reprint với reason/idempotency/expectedVersion, Barista/Manager trong branch; quyền duyệt in lại chờ policy. Không cấp endpoint hoặc lưu trạng thái giả trước schema/approval.
2. **Per-item Serving:** Kitchen OrderItemId/prepared quantities + Ordering fulfillmentMode/task/servedQuantity/version/history; MVP giao toàn quantity một line, không giao phần còn thiếu, giữ WholeOrder cho legacy. Schema/API/RBAC/tests trong `SERVING_MULTI_STAFF_AND_RECOVERY_DESIGN.md` (Mobile). Không ánh xạ theo product name hoặc tạo served state trong RAM.
3. **Claim:** persisted owner/quantity/lease/version và audit; transaction/version conflict trả 409; expiry chỉ giải phóng capacity, không coi là đã giao. Cashier/Manager theo RBAC hiện có, Manager override có reason. Hai request deliver thành công idempotent không chứng minh exclusive claim.
4. **Submitted edit/cancel:** request/approval/version/reason và before/after audit, Kitchen acknowledgement; bảo vệ Brewing/Ready/Delivered-unpaid. Legacy CancelAsync còn financial side effect và Web callers; không tự đổi hoặc expose trên Mobile.
5. **Merge:** cần membership/surviving session/redirect/cart/token/history, lock hai session/bàn và policy một Bill. Không tự dồn Order hoặc gọi đây là chuyển bàn.
6. **General event recovery:** admission đã có outbox/receipt/HTTP/checkpoint; Started/Ready/Delivered/Transferred chưa cùng guarantee. Không dùng Redis reconnect làm bằng chứng tất cả event đã phục hồi.

Không tạo EF migration mới cho các đề xuất này. Phải duyệt SQL, RBAC/API impact, Web rollout và test trước schema/feature implementation. Rollback feature mặc định giữ ledgers/fixtures lịch sử, không DROP/xóa dữ liệu để bỏ trạng thái đã phục vụ/đã in.

## Kết quả kiểm thử thực tế

| Kiểm tra | Kết quả | Phạm vi |
| --- | --- | --- |
| Kitchen HTTP/EF InMemory | PASS 20, FAIL 0, SKIP 0 | Preview IDs/options/cup numbers/readonly, RBAC/scope, hold/missing, table snapshot + terminal guard |
| Operations PostgreSQL16/Redis7 | PASS 9, FAIL 0, SKIP 0 | 7 case cũ + 2 mới: concurrent legacy generation và transfer/preparation cùng lock, có pg_locks wait |
| Admission recovery PostgreSQL16/Redis7 | PASS 18, FAIL 0, SKIP 0 | Offline/restart/process crash/replay/Redis restart; thêm assertion durable recovery không phát legacy print event và preview không trùng |
| Backend regression lần đầu | FAIL 1, PASS 460, SKIP 11 | PaymentConfig branch override fail; 11 CashPostgres cần runner riêng, không tính PASS |
| PaymentConfig failed case chạy riêng | PASS 1 | TestServer/InMemory; chưa kết luận nguyên nhân lỗi lượt full đầu |
| Backend regression chạy lại, có TRX | PASS 461, FAIL 0, SKIP 0 | Exclude 2 operations infrastructure classes và CashPostgresTests; không phải 461 PostgreSQL E2E |
| Flutter full regression | PASS 155 | API mocked/widget + Demo config false; trước khi thêm 2 case role/409 cuối |
| Flutter sticker suite cuối | PASS 7, FAIL 0 | 360/393/430px, role/409; không cộng chồng với full suite |
| Analyzer / format | PASS: analyzer cuối 0 issues; formatter 198 files 0 change | Test bổ sung cuối cũng đã format |
| BuildingBlocks | PASS 15, FAIL 0, SKIP 0 | Không cộng với PG/Redis |
| Máy in / UI thiết bị / Web E2E | NOT RUN | Không có hardware/driver/Web repo/full real stack trong test |
| Per-item/claim/edit/merge | BLOCKED | Chưa có schema/policy được duyệt |

Initial failure PaymentConfig chưa rõ root cause; riêng case và full rerun đều PASS không xóa bằng chứng fail ban đầu. Không sửa Payment code để làm xanh suite. Có hai C# warnings cũ CS0628 WorkforceReviewTests và CS8620 OrderTests ở build đầy đủ; incremental build crash probe 0 warning/error.

Metadata real test: `STAFF_OPERATIONS_INFRASTRUCTURE_RUN.md`, `KITCHEN_ADMISSION_RECOVERY_RUN.md`. Cluster operations `7705117448861712419`, recovery `7705117784544059427`, tmpfs/network/credentials riêng, không dùng Windows/operating DB. Runner guard xác minh target trước schema/fixtures; EnsureCreated cho operations và EF chain cho recovery chỉ trên DB tạm đã được phép. Sau cả hai lượt, container cũ ID/image/start/status/restart không đổi, chỉ tài nguyên tạm được cleanup, không xóa volume. Regression TRX nằm `%TEMP%/aicos-remaining-operations-regression/remaining-operations-regression.trx`.

## Trình tự tiếp theo và rollback

Không deploy binary mới tới DB vận hành chưa expand recovery schema. Cần duyệt riêng deployment/migrations/ACL/machine key của recovery trước runtime. Phần không cần schema của lượt này đã có test, nhưng hardware print/per-item/claim/edit/merge không được coi DONE. Ưu tiên duyệt serving correlation/task/claim hoặc printer/audit theo nhu cầu vận hành; transfer/general event version/outbox là backlog reliability tiếp theo.

Rollback code phải review cùng recovery hold guards hiện có; không bỏ hold protection hoặc xóa receipt/outbox. Preview có thể gỡ route/button độc lập khi chưa có consumer. Không phục hồi auto-print cho durable admission khi chưa review rủi ro bản giấy trùng. Không có SQL rollback cần chạy vì không thay schema vận hành.
