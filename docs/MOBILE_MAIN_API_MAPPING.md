# Đối chiếu Mobile với Backend main — 2026-11-03

> Bản đồ audit gốc. Tiến độ tích hợp mới, thay đổi Ordering và các giới hạn đã kiểm thử:
> [STAFF_MOBILE_SYNC.md](STAFF_MOBILE_SYNC.md). Không coi báo cáo snapshot này là trạng thái code mới nhất.

Mobile hiện chạy demo, adapter BE cũ đã gỡ. File này chỉ là audit/đề xuất tích hợp với main d7ab6b7; không bật lại API cũ, không sửa UI hoặc tự nối endpoint trong lượt này.

## Màn hình và API có thể dùng

| UI/luồng Mobile | API main tương ứng | Phần phải xử lý trước khi bật |
| --- | --- | --- |
| Đăng nhập | POST /api/v1/identity/auth/login; POST refresh/logout; GET auth/me | Dùng `{login,password}`, DTO access/refresh/user mới; chưa có account, branch và tenant dữ liệu |
| Phiên/thiết bị | GET /identity/auth/sessions; DELETE /sessions/{id}; POST revoke-others | JWT sid, refresh rotation; revoke không làm mọi access JWT hết hiệu lực ngay |
| Home tổng quan | GET /orders, /kitchen/queue, /tables/dining-tables, /orders/qr/sessions, /customers/staff/notifications | Chưa có một staff dashboard aggregate duy nhất; dùng paging/filter/branch đúng quyền, tránh tải toàn bộ list khi đông khách |
| QR mới/cần xử lý | Orders + QR sessions/billRequested + staff notifications; /hubs/kitchen POS | Không tự lấy QR guest token làm staff session. Khách ẩn danh có thể không có tên, không thay bằng tên mock |
| Sơ đồ bàn/khu vực | GET /tables/areas, /dining-tables, /floor-plan | TableId UUID, tableCode/areaId/status mới; không giữ mapping S02 demo như dữ liệu thật |
| Chi tiết bàn/lượt gọi | GET /orders/qr/sessions/{id}/bill; GET /orders filtered; GET /orders/{id} | Main session thuộc Ordering. Manual staff order chưa tự gắn session; phải chốt cách gom mọi lượt |
| Tạo đơn tại quầy | GET /menu/public/branches/{branchId}/menu; POST /orders | ProductId/optionIds thay variantId; backend tính giá, branch lấy theo JWT/rule; response có thể 201 |
| Mang về tại quầy | POST /orders với type Takeaway; GET orders theo type/branch | Khác POST /orders/pickup dành cho Customer đặt trước/slot. Cần chốt tên khách vãng lai nếu UI bắt buộc vì DTO order không có customerName riêng |
| Theo dõi Order | GET /orders/{id}, /orders/{id}/payment; /hubs/kitchen POS | Order DTO có cashierId/customerId và createdAtUtc; không có cashierName trong DTO đã đọc |
| Bar/kitchen | GET /kitchen/queue, /batches, /tickets/{id}; PUT ticket status | Cashier xem được theo Staff nhưng không tự Brewing/Ready/Completed; handler yêu cầu BarRoles. Không map Waiter thành Barista để vượt quyền |
| Giao món/hoàn thành | POST /orders/{id}/deliver và các rule Order/Kitchen | Không có nguyên bộ claim/complete serving task theo nhân viên như demo; cần test phân quyền và quyết định semantics |
| Xem bill/receipt | GET /orders/{id}/receipt hoặc by-code receipt; GET QR session bill | Receipt GET ghi printed counters. QR bill là aggregate, không có bảng bill độc lập giống cũ |
| Thanh toán cash | POST /payments/transactions/cash; GET transactions theo orderId | Cần chốt/concurrency test và tài khoản có branch. Không coi demo cash flow là API real đã test |
| Thanh toán QR | POST /payments/transactions + checkoutUrl; webhook/query | Adapter provider hiện mô phỏng; chưa có manual evidence confirmation như yêu cầu cũ. Không bật trạng thái đã nhận tiền thật chỉ từ ảnh QR |
| Refund/điều chỉnh | POST transaction/{id}/refunds; approve/reject; Order exception | Refund theo policy khác sửa tổng bill tuỳ ý. Không map nút điều chỉnh bill thành API refund nếu sai nghĩa |
| Kết thúc phiên bàn | POST /orders/qr/sessions/{id}/close | Hiện cho close dù chưa paid; cần sửa/chốt rule trước khi bật nút tương ứng UI |
| Thông báo | GET /customers/staff/notifications và actions read/read-all/preferences theo catalog | Dữ liệu từ events, cần test Pub/Sub/event loss; không phát mock notifications dưới nhãn thật |
| Profile/ca | GET/PUT /identity/auth/me; Workforce /shift-assignments/mine và attendance | Tài khoản login không tự bảo đảm đã có employee/assignment Workforce; fixture/provision là bước riêng |
| Báo cáo | GET /reporting/branch, /chain, /payment-methods, /peak-hours, exports | Management/BrandAdmin, Cashier không có quyền mặc định. Không mở quyền chỉ để lấp card Home |

Các path trong cùng module được viết rút gọn sau `/api/v1` khi rõ ngữ cảnh; đối chiếu path đầy đủ trong [catalog](BACKEND_API_CATALOG.md), không copy mù đường dẫn rút gọn vào code.

## Không có API tương đương demo 1:1

- Không có Waiter trong role .NET main. Cashier, Barista và quản lý là những quyền khác nhau; phải chốt persona của app, không đổi nhãn role để cấp thêm quyền ngoài thiết kế.
- Chuyển bàn/ghép bàn/tách bill/chỉnh bill và yêu cầu sửa/hủy món không có nguyên bộ contract giống UI demo. Table CRUD hoặc đổi loại phục vụ không tương đương toàn bộ các thao tác này.
- Main bill theo phiên QR là DTO aggregate; payment theo order. Thanh toán toàn bill nhiều order cần orchestration, không cộng tổng rồi trả một orderId bất kỳ vào payment API.
- Receipt JSON/printed flag không phải xác nhận máy in phần cứng. Không hiển thị “In thành công” chỉ vì API receipt trả 200.
- Một staff-created DineIn Order gắn tableId không mặc định gắn tableSessionId. Phải thống nhất trước khi UI gom lượt hoặc tính tổng chưa thanh toán.
- Thông tin “ai order” có IDs, timestamp nhưng thiếu display name của cashier. GET Identity /users/{id} thuộc Management, Cashier không được tự dùng để đọc mọi user; nên enrich DTO hoặc thêm directory contract đúng quyền ở Backend.
- Body login không nhận branchId chọn tuỳ ý. Staff làm việc ở branch được gán; cần API/thủ tục riêng nếu nghiệp vụ cho phép chuyển branch.

## Thứ tự tích hợp đề xuất

1. **Bootstrap và Identity:** chuẩn bị SuperAdmin/brand/branch/Cashier hợp lệ bằng cách review riêng; hiện chưa có account nào. Test login đúng/sai, refresh, expiry/revoke, tenant/branch. Không tạo JWT tự ký để thay login thật.
2. **Adapter Mobile mới:** HTTP client, token storage namespace mới, base URL runtime main, errors/DTO/role mapping theo source hiện tại. Không khôi phục ApiRepository cũ chỉ bằng đổi URL. Giữ interface repository để UI không chứa JSON/network logic.
3. **Menu + Table read:** danh mục/sizes/options/availability và sơ đồ bàn; paging, loading/error/empty. Data thật trống phải hiển thị empty, không bù demo silently.
4. **Ordering + Kitchen:** tạo một order test trên môi trường dữ liệu được duyệt, kiểm tra giá từ BE, nhiều lượt/table session, status/event/SignalR, thông tin người và thời gian. Giới hạn Mobile DineIn/Takeaway dù Backend có Delivery.
5. **Payment:** xử lý concurrency/idempotency, manual bank evidence hoặc provider thật, receipt/print semantics, settlement guard của mọi đường đóng phiên trước khi bật thanh toán thật.
6. **Workforce/notifications/reporting** theo role cần thiết; AI là bước độc lập sau khi hai module AI được tích hợp và có dữ liệu/role đúng.

## Quy ước kỹ thuật cần giữ

- API gateway local hiện ở http://127.0.0.1:8081 trên PC. Port bind loopback; điện thoại Android thật không tự truy cập localhost của PC qua Wi-Fi. Cần chọn USB adb reverse hoặc cấu hình LAN phù hợp ở bước tích hợp, chưa đổi firewall/listen host trong audit.
- Trên emulator, localhost cũng là máy ảo. Cấu hình URL theo thiết bị; không hardcode một IP Wi-Fi vào source.
- Parse camelCase/UUID/enum PascalCase/UTC và xử lý null rõ ràng; UI dùng tiếng Việt, backend detail có thể tiếng Anh nên map errorCode.
- Không dùng một enum duy nhất cho OrderStatus, PaymentStatus, KitchenTicketStatus và trạng thái máy in. Các enum khác tên/kind phải map có kiểm soát.
- Dùng timestamp openedAtUtc/createdAtUtc để tính thời lượng; không lưu cố định “14:15” hoặc 29 phút demo thành dữ liệu thật.
- Không auto retry GET receipt/stickers/payment detail vì chúng có thể ghi. POST tài chính chỉ retry khi đã có idempotency guarantee được kiểm thử.
- UI theo dõi order vẫn phải cho xem detail; quyền mutate và quyền đọc là riêng. Chỉ enable thao tác khi cả role, trạng thái và Backend contract cho phép.

## Bằng chứng và giới hạn

Audit dựa trên source + GET OpenAPI runtime; không thực hiện nghiệp vụ viết hoặc thay đổi Mobile. Danh mục 268 operation không chứng minh 268 flow đã PASS. Tests tên IntegrationTests hiện dùng EF InMemory/recording bus; cần PostgreSQL/Redis/API integration thật cho các điểm concurrency, FK, sự kiện và thanh toán.

Tài liệu này là bản đồ tích hợp, không phê duyệt tạo fixtures, bật payment thật hoặc đổi role/database. Các bước triển khai tiếp theo cần phạm vi cụ thể; không tạo service mới chỉ để giải quyết khác biệt UI khi có thể bổ sung contract đúng service sở hữu.

## 2026-11-04 — Cash v1 follow-up

See [CASH_COLLECTION_MAIN.md](CASH_COLLECTION_MAIN.md): GET Payments staff/capabilities; optional Idempotency-Key replay for existing POST transactions/cash; optional amountTendered/change response fields. Pending online attempts now block cash (409), instead of silent replacement. Mobile cash confirmation is connected; QR remains disabled. No schema migration.
