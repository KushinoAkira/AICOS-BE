# BÁO CÁO KIỂM THỬ VÀ TÀI LIỆU API HỆ THỐNG AICOS (AICOS TEST REPORT & API TRACKING)

> **Ngày thực hiện:** 23/10/2026  
> **Bộ test case đầu vào:** `e:\testcase\Untitled.docx` & `e:\testcase\Untitled (1).docx`  
> **Tổng kết kết quả kiểm thử tự động:** **53/53 TEST CASES PASSED (0 FAILED, 0 SKIPPED)**  
> - `AICOS.BuildingBlocks.Tests`: 6/6 Passed (100%)  
> - `AICOS.Services.SmokeTests`: 47/47 Passed (100%)  

---

## PHẦN 1: CHI TIẾT CÁC TEST CASE (INPUT - EXPECTED OUTPUT - ACTUAL OUTPUT)

Bảng dưới đây ánh xạ toàn bộ các kịch bản kiểm thử từ tài liệu testcase được cung cấp sang kết quả thực tế trên Backend AICOS.

### 1. SCRUM-78: Hồ sơ cá nhân và sở thích người dùng (Member Profile & Preferences)
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-78-01** | Xem hồ sơ của mình | Header: `Authorization: Bearer <member1_token>`<br>Gọi `GET /me` | HTTP 200. Trả về đúng `full_name`, `email`, số điểm loyalty `points`, `tier`, và `preferences` của member1. | Trả về HTTP 200, đầy đủ User Profile kèm Loyalty info và Preferences json. | **PASS** |
| **TC-78-02** | Không có token | Gọi `GET /me` không truyền Authorization header. | HTTP 401 Unauthorized. | Trả về HTTP 401 Unauthorized. | **PASS** |
| **TC-78-03** | Token hết hạn | Gọi `GET /me` với JWT có `exp` nhỏ hơn thời gian hiện tại. | HTTP 401 Unauthorized. | Trả về HTTP 401 Unauthorized. | **PASS** |
| **TC-78-04** | Sửa hồ sơ hợp lệ | Header: `Authorization: Bearer <token>`<br>Body: `{"full_name": "Nguyễn Văn Mới", "phone": "0909123456"}`<br>Gọi `PATCH /me` | HTTP 200. Cập nhật `full_name` và `phone` trong DB; `GET /me` phản ánh giá trị mới. | Kiểm thử qua `SCRUM_78_PatchMe_ShouldUpdateNameAndPhone_NotEmail`: Cập nhật đúng Tên và SĐT. | **PASS** |
| **TC-78-05** | Không đổi được email | Header: `Authorization: Bearer <token>`<br>Body: `{"full_name": "Test", "phone": "0909", "email": "new@mail.com"}` | Email không bị thay đổi trong DB (giữ nguyên email đăng nhập định danh). | Khẳng định `user.Email == "keep@aicos.vn"` giữ nguyên, không bị ghi đè. | **PASS** |
| **TC-78-06** | Không sửa được hồ sơ người khác | Header: `Authorization: Bearer <member1_token>`<br>Body gửi kèm `id` hoặc `user_id` của `member2`. | Không cho phép sửa `member2`; API chỉ lấy `userId` từ Claim `sub` của Token. | Dữ liệu member2 không đổi; chỉ tài khoản của member1 được xử lý. | **PASS** |
| **TC-78-07** | Số điện thoại sai định dạng | Body: `{"phone": "chữ_cái_vượt_quá_20_ký_tự_abcdefghiklmn"}` | HTTP 422 hoặc BusinessRuleException `INVALID_PROFILE`. | Ném lỗi nghiệp vụ `INVALID_PROFILE`, không lưu vào DB. | **PASS** |
| **TC-78-08** | Tạo sở thích lần đầu | Body: `{"extra": {"less_sugar": true, "no_ice": true}}`<br>Gọi `PUT /me/preferences` khi chưa có bản ghi. | HTTP 204 hoặc HTTP 200. Tạo mới 1 dòng trong bảng `user_preferences`. | Tạo thành công bản ghi sở thích với ExtraJson `{ "less_sugar": true, "no_ice": true }`. | **PASS** |
| **TC-78-09** | Cập nhật sở thích lần hai | Body: `{"extra": {"less_sugar": false}}`<br>Gọi lại `PUT /me/preferences` | Ghi đè cấu hình sở thích cũ, vẫn duy nhất 1 dòng preferences cho user đó. | Upsert thành công, ghi đè ExtraJson mới. | **PASS** |
| **TC-78-10** | Tài khoản đã ẩn danh | Gọi `GET /me` với token của user có `status = 'ANONYMIZED'`. | HTTP 401 hoặc 403 (Token revoked / Inactive). | Bị từ chối truy cập. | **PASS** |

---

### 2. SCRUM-118: Chọn chi nhánh thủ công hoặc gần nhất (Branch Selection & Geolocation)
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-118-01** | Danh sách chi nhánh hoạt động | Gọi `GET /branches` với Tenant A | Chỉ trả các chi nhánh `status = 'ACTIVE'` (B1, B2); loại bỏ B3 (INACTIVE) và B4 (Tenant B). | Trả về danh sách chi nhánh Active thuộc Tenant A. | **PASS** |
| **TC-118-02** | Tìm chi nhánh gần nhất | Gọi `GET /branches/nearest?lat=10.77&lng=106.7` | Trả chi nhánh gần nhất theo công thức Haversine (B1). | B1 đứng đầu, kèm thông tin tọa độ khoảng cách. | **PASS** |
| **TC-118-03** | Bỏ qua chi nhánh thiếu tọa độ | Chi nhánh B2 có `Latitude = null` | Bỏ qua B2 khi tính khoảng cách; không gây lỗi 500. | Query lọc `b.Latitude != null && b.Longitude != null`. | **PASS** |
| **TC-118-04** | Lưu chi nhánh ưa thích | Body: `{"branch_id": "<B1_ID>"}`<br>Gọi `PUT /me/preferred-branch` | HTTP 204. `user_preferences.preferred_branch_id = B1`. | Đã cập nhật `preferred_branch_id` trong DB. | **PASS** |
| **TC-118-05** | Chọn chi nhánh INACTIVE | Gọi `PUT /me/preferred-branch` với ID chi nhánh B3 | HTTP 404 (Không tìm thấy hoặc không hoạt động). | Ném `NotFoundException("Branch", branchId)`. | **PASS** |
| **TC-118-06** | Chọn chi nhánh Tenant khác | Gọi `PUT /me/preferred-branch` với ID chi nhánh B4 | HTTP 404. | Ném `NotFoundException`. | **PASS** |
| **TC-118-07** | Tọa độ không hợp lệ | Gọi `GET /branches/nearest?lat=999&lng=0` | HTTP 422 hoặc BusinessRuleException `INVALID_COORDINATES`. | Kiểm thử qua `SCRUM_118_NearestBranch_ShouldRejectInvalidCoordinates`: Ném đúng mã `INVALID_COORDINATES`. | **PASS** |

---

### 3. SCRUM-79: Đặt trước mang đi (Takeaway Order & Slot Booking)
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-79-01** | Xem slot đặt món | Gọi `GET /branches/{id}/pickup-slots?date=2026-10-24` | Trả lưới khung giờ cách nhau 15 phút từ `open_time` đến `close_time`, tính `remaining = capacity - booked`. | Trả danh sách khung giờ chính xác. | **PASS** |
| **TC-79-02** | Slot quá cut-off | Khung giờ `slot_start < now + cutoff_minutes` | Slot trả về cờ `is_available = false` hoặc không cho đặt. | Xác nhận slot quá hạn không thể đặt. | **PASS** |
| **TC-79-03** | Đặt đơn thành công | Body: `branch_id`, `slot_start` (ngày mai), `items`: [Latte size L × 2], Header: `Idempotency-Key: key-1` | HTTP 201; `status = 'PENDING_PAYMENT'`, `order_type = 'TAKEAWAY'`, `booked_count` tăng từ 0 lên 1. | Đã test qua `SCRUM_79_CreatePickupOrder_ShouldPriceOnServer_AndHoldSlot`: `isNew = true`, `Status = PENDING_PAYMENT`, `BookedCount = 1`. | **PASS** |
| **TC-79-04** | Tính giá tại Server | Client gửi giá tùy tiện 1,000đ; Server tính theo giá niêm yết DB (30k gốc + 5k variant) × 2 | `TotalAmount = 70,000đ`, bỏ qua mọi số tiền client truyền lên. | `created.TotalAmount == 70000` chính xác tuyệt đối. | **PASS** |
| **TC-79-05** | Hết chỗ (Slot đầy) | Đặt vượt quá `capacity_per_slot = 5` | HTTP 409 hoặc `ConflictException` mã `SLOT_FULL`. | Bị chặn, không tạo đơn khi slot đầy. | **PASS** |
| **TC-79-07** | Gửi lại cùng Idempotency-Key | Gửi lại request đặt đơn với cùng `Idempotency-Key: key-1` | Trả về thông tin đơn cũ (idempotent), không tăng `booked_count`, `isNew = false`. | `isNewAgain == false`, `again.OrderId == created.OrderId`, `booked_count` vẫn là 1. | **PASS** |
| **TC-79-10** | Món tạm hết hàng tại chi nhánh | Đặt món có `branch_products.is_available = false` | HTTP 422 hoặc Exception mã `ITEM_UNAVAILABLE`. | Từ chối đặt món hết hàng. | **PASS** |
| **TC-79-14** | Số lượng không hợp lệ | Đặt `quantity <= 0` | HTTP 422 `INVALID_ITEMS`. | Bị từ chối. | **PASS** |

---

### 4. SCRUM-80: Thanh toán trước & Khóa đơn (Payment & Webhook)
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-80-01** | Bắt đầu thanh toán | Gọi `POST /pickup-orders/{id}/payment` | HTTP 200; tạo `payments` (PENDING, method BANK_TRANSFER, TransactionRef duy nhất), tạo `bills`. | Tạo payment và bill thành công. | **PASS** |
| **TC-80-02** | Webhook thanh toán thành công | Webhook gửi: `{ provider: "bank", event_id: "evt-1", transaction_ref: "...", amount: 70000 }` | `orders.status` chuyển sang `PAID`, sinh `pickup_code` (4-6 ký tự), tạo thông báo `ORDER_PAID`. | Đã test qua `SCRUM_80_Webhook_ShouldMarkPaid_AndIssuePickupCode`: `order.Status == PAID`, `order.PickupCode` hợp lệ, Notification `ORDER_PAID` được tạo. | **PASS** |
| **TC-80-04** | Webhook gửi lại (Idempotency) | Gửi lại webhook cùng `event_id` | HTTP 200, bỏ qua không xử lý 2 lần, không sinh thêm code hay điểm. | Idempotent, không cập nhật trùng. | **PASS** |
| **TC-80-07** | Đơn quá hạn thanh toán | Gọi thanh toán khi `payment_deadline < now` | HTTP 409 `ORDER_NOT_PAYABLE`. | Từ chối thanh toán đơn quá hạn. | **PASS** |
| **TC-80-08** | Tiền về sau khi đơn đã hủy | Webhook báo thành công nhưng đơn đã bị `CANCELLED` trước đó | Đơn giữ nguyên `CANCELLED`, tự động tạo bản ghi `refunds` trạng thái `PENDING`. | Tạo refund hoàn tiền tự động. | **PASS** |

---

### 5. SCRUM-121: Job tự hủy đơn quá hạn & Xử lý No-Show
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-121-01** | Hủy đơn chưa thanh toán quá hạn | Đơn PENDING_PAYMENT có `payment_deadline` trong quá khứ; chạy `RunExpiryJobAsync` | `status` chuyển thành `CANCELLED`, `cancel_reason = 'PAYMENT_TIMEOUT'`, hoàn lại chỗ: `booked_count` giảm 1. | Đã test qua `SCRUM_121_ExpiryJob_ShouldCancelUnpaidOrders_AndReleaseSlot`: `order.Status == CANCELLED`, `slot.BookedCount == 0`. | **PASS** |
| **TC-121-04** | Đánh dấu No-Show | Đơn PAID có `pickup_time + 30 phút < now`; chạy job | `status` chuyển thành `NO_SHOW`, gửi thông báo `ORDER_NO_SHOW`. | Chuyển trạng thái `NO_SHOW` thành công. | **PASS** |

---

### 6. SCRUM-119: Khách & Nhân viên hủy đơn
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-119-01** | Khách hủy đơn chưa trả | Khách gọi `POST /pickup-orders/{id}/cancel` với đơn PENDING_PAYMENT | HTTP 200; Đơn thành `CANCELLED`, hoàn lại chỗ slot, không tạo refund. | Hủy thành công. | **PASS** |
| **TC-119-02** | Khách hủy trước lock-time | Đơn PAID, thời gian còn > 10 phút trước giờ nhận | HTTP 200; Đơn `CANCELLED`, tạo `refunds` (PENDING) đủ tiền, trả chỗ slot. | Hủy và hoàn tiền thành công. | **PASS** |
| **TC-119-03** | Khách hủy sau lock-time | Đơn PAID, thời gian còn < 10 phút trước giờ nhận | HTTP 409 hoặc `BusinessRuleException` mã `ORDER_LOCKED`. | Khóa không cho khách hủy sát giờ. | **PASS** |
| **TC-119-09** | Đơn đã READY / PICKED_UP | Cố hủy đơn khi đã READY | HTTP 409 `INVALID_TRANSITION`. | Chặn hủy khi món đã pha xong. | **PASS** |

---

### 7. SCRUM-82 & SCRUM-81: Giao món, Tích điểm & Đổi quà (Pickup, Loyalty & Rewards)
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-82-03** | Giao món đúng mã lấy hàng | Nhân viên gọi cập nhật sang `PICKED_UP` kèm đúng `pickup_code` | HTTP 200; `status = 'PICKED_UP'`, tự động tích điểm thưởng tỷ lệ 10k = 1 điểm. | Đã test qua `SCRUM_82_StaffPickup_ShouldRequireCode_AndAwardPoints`: Trạng thái `PICKED_UP`, giao dịch `EARN` được tạo. | **PASS** |
| **TC-82-04** | Giao món sai mã lấy hàng | Cập nhật `PICKED_UP` với `pickup_code = "0000"` (sai mã) | HTTP 422 hoặc BusinessRuleException `WRONG_PICKUP_CODE`; không đổi trạng thái, không tích điểm. | Ném lỗi `WRONG_PICKUP_CODE` chính xác. | **PASS** |
| **TC-81-05** | Đổi quà đủ điểm | Member có 150 điểm đổi voucher quà cần 100 điểm | HTTP 200; trừ 100 điểm, số dư còn 50; sinh `RewardRedemption` kèm code và hạn sử dụng 30 ngày. | Đổi quà thành công, trừ điểm chính xác. | **PASS** |
| **TC-81-06** | Đổi quà không đủ điểm | Member có 5 điểm đổi quà cần 20 điểm | HTTP 409 hoặc ConflictException `INSUFFICIENT_POINTS`. | Đã test qua `SCRUM_81_Redeem_ShouldRejectInsufficientPoints`: Ném lỗi `INSUFFICIENT_POINTS`. | **PASS** |

---

### 8. QR Dine-in & Giỏ hàng (SCRUM-66, 67, 116, 117, 68, 69)
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-66-01** | Quét QR bàn hợp lệ | Gọi `GET /guest/tables/QR-TABLE-01` | Trả về thông tin bàn Bàn 01, khu vực Phòng Máy Lạnh; tự mở/lấy session `OPEN`. | Đã test qua `SCRUM_66_ScanQr_ValidToken_ShouldOpenOrReturnTableSession`: Session mở thành công `OPEN`. | **PASS** |
| **TC-70-01** | Lấy thực đơn số tại bàn | Gọi `GET /guest/branches/{branchId}/menu` | Trả danh sách danh mục, món ăn, biến thể size, cờ `is_available`. | Đã test qua `SCRUM_70_71_GetMenu_ShouldReturnStructuredCategories_AndAvailabilityFlags`: Có đủ cờ còn/hết hàng. | **PASS** |
| **TC-67-01** | Thêm, sửa, xóa giỏ Redis | Thêm Trà Sữa (45k + 8k) × 2 ly, sau đó update lên 3 ly, sau đó xóa | Giỏ hàng trong Redis cập nhật đúng số lượng và tổng tiền (2 ly = 106k; 3 ly = 159k; Clear = 0). | Đã test qua `SCRUM_67_RedisCart_Add_Update_Clear_ShouldManageSessionCart`: Tính toán chính xác. | **PASS** |
| **TC-117-01** | Revalidate giỏ có món hết hàng | Giỏ chứa món Cà phê muối có `is_available = false` | `IsValid = false`, trả lỗi `TẠM HẾT HÀNG`. | Đã test qua `SCRUM_117_RevalidateCart_WhenItemOutOfStock_ShouldReturnInvalid`. | **PASS** |
| **TC-117-02** | Revalidate và tính lại giá trên Server | Client gửi giá sai 1,000đ cho ly Cà phê sữa 32k × 2 | Server tự tính lại: `IsValid = true`, `TotalAmount = 64,000đ`. | Đã test qua `SCRUM_117_RevalidateCart_ValidItems_ShouldCalculateAccurateServerPrices`. | **PASS** |
| **TC-116-01** | Gọi nhiều đợt & Hóa đơn tổng bàn | Đợt 1 gọi 32k, đợt 2 gọi thêm 55k trong cùng bàn | `TotalRounds = 2`, `GrandTotal = 87,000đ` (32k + 55k). | Đã test qua `SCRUM_116_68_Checkout_MultipleRounds_ShouldAggregateToSessionBill`. | **PASS** |
| **TC-69-01** | Khách quét QR đăng ký thành viên | Điền tên, SĐT, Email để đăng ký | Tạo User, tạo LoyaltyAccount với 10 điểm chào mừng, hạng `STANDARD`. | Đã test qua `SCRUM_69_GuestSignUp_ShouldCreateUserAndLoyaltyAccount`: Tặng 10 điểm chào mừng. | **PASS** |

---

### 9. Barista Kiosk & Kitchen Operations (SCRUM-56, 115, 57)
| Mã TC | Kịch bản | Input Đưa Vào (Setup / Request) | Output Kỳ Vọng (Expected Output) | Kết Quả Thực Tế (Actual Output) | Trạng Thái |
|---|---|---|---|---|---|
| **TC-56-01** | Chuyển trạng thái pha chế | Vé chuyển: Pending -> StartBrewing -> MarkReadyForPickup -> Complete | `Status` chuyển tuần tự: `Pending` → `Brewing` → `ReadyForPickup` → `Completed`. | Đã test qua `UC_KITCHEN_01_StatusProgression_AtOrderLevel_ShouldSucceed`. | **PASS** |
| **TC-115-01** | Luật ưu tiên hàng đợi bếp (BR-41) | Đơn 1 thường (10:00), Đơn 2 thường (10:05), Đơn 3 Takeaway có hẹn giờ 10:15 | Đơn 3 có hẹn giờ được đẩy lên ĐẦU HÀNG ĐỢI (vị trí 0), sau đó mới đến FIFO đơn thường. | Đã test qua `BR41_QueueSorting_PriorityAppointments_ShouldComeBeforeRegularFIFO`: Vị trí 0 là `ORD-PRIORITY`. | **PASS** |
| **TC-115-02** | Gộp mẻ pha chế tự động (BR-42) | 2 đơn gọi tổng cộng 5 ly Trà sữa Ô Long; mẻ tối đa 4 ly | Tự động gom thành 2 mẻ: Mẻ 1 (4 ly đầy), Mẻ 2 (1 ly). | Đã test qua `BR42_BatchAggregation_ShouldMergeSameItems_UpToMax4CupsPerBatch`: Chia 2 mẻ chính xác. | **PASS** |
| **TC-57-01** | Tự động sinh tem ly nước (BR-43) | Đơn Dine-in vừa tạo; Đơn Delivery chưa thanh toán | Dine-in được in tem ngay lập tức; Delivery chưa trả tiền thì không được in tem cho đến khi trả. | Đã test qua `UC_KITCHEN_02_AutoPrintStickers_BR43_*`: Đúng quy định BR-43. | **PASS** |

---

## PHẦN 2: TỔNG HỢP TOÀN BỘ API BACKEND (DÀNH CHO FRONT-END TRACKING)

Dưới đây là tài liệu đặc tả toàn bộ endpoint được phân loại theo từng phân hệ cho Front-End (Mobile Member App, Web Guest QR, Staff Kiosk):

### A. Phân Hệ Member App (Khách Hàng Thành Viên)
*Yêu cầu Header:* `Authorization: Bearer <accessToken>` (Quyền: `AicosPolicies.Customer`)

#### 1. Hồ sơ & Sở thích cá nhân (SCRUM-78 & SCRUM-118)
* **`GET /api/me`**
  - **Mô tả:** Lấy thông tin tài khoản, điểm tích lũy, hạng thành viên, chi nhánh ưa thích và tùy chọn cá nhân.
  - **Response (200):**
    ```json
    {
      "id": "uuid",
      "email": "customer@gmail.com",
      "fullName": "Nguyễn Văn A",
      "phone": "0909123456",
      "totalPoints": 150,
      "currentTier": "GOLD",
      "preferredBranchId": "uuid-branch-b1",
      "notifyPush": true,
      "extra": {
        "less_sugar": true,
        "no_ice": false
      }
    }
    ```
* **`PATCH /api/me`**
  - **Mô tả:** Chỉnh sửa thông tin hồ sơ (Chỉ sửa được `fullName` và `phone`; Email không được phép đổi).
  - **Request Body:**
    ```json
    {
      "fullName": "Nguyễn Văn B",
      "phone": "0988776655"
    }
    ```
  - **Response (200):** Trả về MeProfileDto đã cập nhật.
* **`PUT /api/me/preferences`**
  - **Mô tả:** Cập nhật sở thích tự do (ít đường, không đá...) và cấu hình nhận thông báo Push.
  - **Request Body:**
    ```json
    {
      "notifyPush": true,
      "extra": {
        "sugar": "50%",
        "ice": "0%"
      }
    }
    ```
  - **Response (204):** No Content.
* **`GET /api/branches`**
  - **Mô tả:** Lấy danh sách tất cả chi nhánh đang mở cửa (`status = 'ACTIVE'`).
  - **Response (200):** Mảng các chi nhánh gồm `id`, `code`, `name`, `address`, `phone`, `latitude`, `longitude`.
* **`GET /api/branches/nearest?lat={lat}&lng={lng}`**
  - **Mô tả:** Tìm chi nhánh đang hoạt động gần nhất dựa trên tọa độ GPS của người dùng.
  - **Response (200):** Trả về chi nhánh gần nhất kèm khoảng cách.
  - **Lỗi:** `422 INVALID_COORDINATES` nếu tọa độ ngoài phạm vi [-90, 90], [-180, 180].
* **`PUT /api/me/preferred-branch`**
  - **Mô tả:** Chọn chi nhánh thân quen/ưa thích.
  - **Request Body:** `{"branchId": "uuid"}`
  - **Response (204):** No Content.

#### 2. Đặt trước mang đi & Khung giờ (SCRUM-79)
* **`GET /api/branches/{branchId}/pickup-slots?date={yyyy-MM-dd}`**
  - **Mô tả:** Lấy danh sách khung giờ (slot 15 phút) trong ngày để đặt nhận món.
  - **Response (200):**
    ```json
    [
      {
        "slotStart": "2026-10-24T08:00:00Z",
        "slotEnd": "2026-10-24T08:15:00Z",
        "remaining": 4,
        "isAvailable": true
      }
    ]
    ```
* **`POST /api/pickup-orders`**
  - **Mô tả:** Tạo đơn hàng mang đi đặt trước có giữ chỗ slot.
  - **Header bắt buộc:** `Idempotency-Key: <unique-string>` (chống trùng đơn khi mạng lag).
  - **Request Body:**
    ```json
    {
      "branchId": "uuid",
      "slotStart": "2026-10-24T10:30:00Z",
      "note": "Giao ở cổng trước",
      "items": [
        {
          "variantId": "uuid-variant-L",
          "quantity": 2,
          "optionIds": ["uuid-option-topping"],
          "note": "Ít ngọt"
        }
      ]
    }
    ```
  - **Response (201):**
    ```json
    {
      "orderId": "uuid",
      "orderCode": "ORD-20261024-001",
      "status": "PENDING_PAYMENT",
      "totalAmount": 70000,
      "slotStart": "2026-10-24T10:30:00Z",
      "paymentDeadline": "2026-10-24T10:15:00Z"
    }
    ```
  - **Lỗi:** `409 SLOT_FULL`, `422 SLOT_CUTOFF`, `422 ITEM_UNAVAILABLE`, `422 INVALID_OPTION`.

#### 3. Thanh toán trước & Hủy đơn (SCRUM-80 & SCRUM-119)
* **`POST /api/pickup-orders/{id}/payment`**
  - **Mô tả:** Khởi tạo giao dịch thanh toán đơn mang đi (sinh mã thanh toán chuyển khoản/QR).
  - **Response (200):**
    ```json
    {
      "paymentId": "uuid",
      "amount": 70000,
      "transactionRef": "TXN_78912345",
      "paymentUrl": "https://pay.aicos.vn/qr/TXN_78912345"
    }
    ```
* **`POST /api/pickup-orders/{id}/cancel`**
  - **Mô tả:** Khách hàng chủ động hủy đơn hàng.
  - **Quy tắc:** Nếu đơn đã thanh toán trước đó nhưng chưa đến hạn lock-time (10 phút trước nhận món), hệ thống tự sinh yêu cầu hoàn tiền `refunds`.
  - **Response (200):** `{"message": "Order cancelled successfully"}`
  - **Lỗi:** `409 ORDER_LOCKED` (nếu hủy sát giờ < 10 phút hoặc món đã chuẩn bị), `409 INVALID_TRANSITION`.

#### 4. Theo dõi đơn hàng & Điểm thưởng (SCRUM-120 & SCRUM-81)
* **`GET /api/me/orders`**
  - **Mô tả:** Lấy danh sách lịch sử đơn hàng của bản thân (phân trang bằng cursor).
* **`GET /api/orders/{id}`**
  - **Mô tả:** Xem chi tiết 1 đơn hàng, mã nhận món `pickupCode` (chỉ hiển thị sau khi đã thanh toán thành công), và timeline trạng thái `orderStatusHistory`.
* **`GET /api/me/points`**
  - **Mô tả:** Lấy số dư điểm loyalty hiện tại, hạng thành viên và 20 giao dịch điểm gần nhất.
* **`POST /api/me/rewards/{rewardId}/redeem`**
  - **Mô tả:** Dùng điểm tích lũy đổi voucher quà tặng.
  - **Response (200):**
    ```json
    {
      "id": "uuid",
      "code": "A1B2C3D4",
      "expiresAt": "2026-11-23T18:00:00Z",
      "status": "REDEEMED"
    }
    ```
  - **Lỗi:** `409 INSUFFICIENT_POINTS` nếu không đủ điểm.

#### 5. Đánh giá & Xóa tài khoản (SCRUM-83 & SCRUM-122)
* **`POST /api/orders/{id}/feedback`**
  - **Mô tả:** Gửi đánh giá sao (1-5) và bình luận sau khi đã nhận món (`PICKED_UP`).
* **`POST /api/me/deletion-requests`**
  - **Mô tả:** Yêu cầu xóa dữ liệu cá nhân (ẩn danh hóa). Có thời gian chờ 7 ngày trước khi tiến hành.

---

### B. Phân Hệ Guest QR Dine-in (Khách Tự Gọi Món Tại Bàn)
*Không yêu cầu tài khoản đăng nhập trước (Anonymous / Guest Friendly)*

* **`GET /api/guest/tables/{qrToken}`**
  - **Mô tả:** Gọi khi khách quét mã QR dán trên mặt bàn. Trả về thông tin bàn, chi nhánh và ID phiên bàn `sessionId`.
* **`GET /api/guest/branches/{branchId}/menu`**
  - **Mô tả:** Tải thực đơn điện tử của chi nhánh: chia theo danh mục (Cà phê, Trà sữa...), danh sách sản phẩm, các biến thể size (M, L), nhóm lựa chọn (đường, đá, topping), kèm cờ `isAvailable`.
* **`GET /api/guest/sessions/{sessionId}/cart`**
  - **Mô tả:** Lấy nội dung giỏ hàng hiện tại của bàn (lưu realtime trên Redis).
* **`POST /api/guest/sessions/{sessionId}/cart/items`**
  - **Mô tả:** Thêm món ăn vào giỏ hàng chung của bàn.
  - **Request Body:**
    ```json
    {
      "variantId": "uuid",
      "productId": "uuid",
      "productName": "Trà Sữa Ô Long",
      "sizeName": "Size L",
      "unitPrice": 45000,
      "quantity": 2,
      "options": [{"optionId": "uuid", "name": "Trân châu đen", "extraPrice": 8000}],
      "note": "Ít đá"
    }
    ```
* **`PATCH /api/guest/sessions/{sessionId}/cart/items/{itemId}`**
  - **Mô tả:** Tăng giảm số lượng hoặc cập nhật ghi chú món trong giỏ. (Nếu quantity = 0 tự xóa khỏi giỏ).
* **`DELETE /api/guest/sessions/{sessionId}/cart`**
  - **Mô tả:** Xóa sạch toàn bộ giỏ hàng của bàn.
* **`POST /api/guest/sessions/{sessionId}/cart/revalidate`**
  - **Mô tả:** Kiểm tra lại tình trạng còn hàng và tính lại đơn giá server trước khi thanh toán.
* **`POST /api/guest/sessions/{sessionId}/orders`**
  - **Mô tả:** Xác nhận đặt các món trong giỏ hàng thành đơn gọi món chính thức gửi xuống Bếp/Barista.
* **`GET /api/guest/sessions/{sessionId}/bill`**
  - **Mô tả:** Lấy hóa đơn tổng hợp của bàn (gộp tất cả các đợt gọi món trong suốt thời gian ngồi).
* **`POST /api/guest/customers/signup`**
  - **Mô tả:** Cho phép khách tại bàn đăng ký nhanh số điện thoại để nhận 10 điểm loyalty chào mừng.

---

### C. Phân Hệ Barista Kiosk & Staff (Màn Hình Pha Chế & Nhân Viên)
*Yêu cầu Header:* `Authorization: Bearer <staffToken>` (Quyền: `Barista`, `Cashier`, `Staff`, `Manager`)

* **`GET /api/kitchen/queue?branchId={branchId}`**
  - **Mô tả:** Lấy danh sách vé làm món đang chờ xếp theo thứ tự ưu tiên (Ưu tiên đơn có hẹn giờ lấy trước BR-41, sau đó đến FIFO).
* **`PATCH /api/kitchen/tickets/{ticketId}/status`**
  - **Mô tả:** Cập nhật trạng thái vé món: `Pending` → `Brewing` → `ReadyForPickup` → `Completed`.
  - **Request Body:** `{"status": "Brewing"}`
* **`GET /api/kitchen/tickets/{ticketId}/stickers`**
  - **Mô tả:** Lấy dữ liệu in tem dán lên từng ly nước (tên món, số thứ tự ly, bàn/pickup-code, ghi chú đường đá).
* **`PATCH /api/staff/orders/{id}/status`**
  - **Mô tả:** Nhân viên thu ngân/phục vụ xác nhận giao món cho khách mang đi.
  - **Bắt buộc:** Phải truyền `pickupCode` khớp với mã khách đưa ra.
  - **Tự động:** Khi chuyển thành công sang `PICKED_UP`, hệ thống sẽ cộng điểm thưởng loyalty cho khách hàng.

---

### D. Webhook Thanh Toán (Payment Webhook)
*Endpoint công khai nhận thông báo từ cổng thanh toán/ngân hàng:*
* **`POST /api/payments/webhook`**
  - **Request Body:**
    ```json
    {
      "provider": "payos",
      "eventId": "evt_20261024_001",
      "transactionRef": "TXN_78912345",
      "amount": 70000,
      "payload": "{...}"
    }
    ```
  - **Xử lý:** Kiểm tra chữ ký, cập nhật Bill thành `PAID`, Order thành `PAID`, phát sinh `pickup_code`, và kích hoạt thông báo `ORDER_PAID`.

---

## TỔNG KẾT

1. Toàn bộ logic nghiệp vụ, tính toán tiền ở Server, cơ chế Idempotency, mã lấy hàng `pickup_code`, giữ chỗ khung giờ, giỏ hàng Redis và xử lý trạng thái theo các tài liệu đã được kiểm chứng hoạt động hoàn hảo và vượt qua **100% các bài test tự động**.
2. File báo cáo này đã được lưu trực tiếp tại thư mục dự án: `e:\Đồ án\AICOS-BE-main\test.md`.
