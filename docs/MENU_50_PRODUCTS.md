# Menu 50 món trên Backend main

Ngày 2026-11-06 (Asia/Saigon). Đã thêm 22 món, nâng menu local từ 28 lên 50 món bằng API hiện có; không chạy migration hoặc ghi SQL trực tiếp.

Target: Docker project aicos-main, tenant aicos-cashier-local, database aicos_menu, schema menu. Công bố tại Hồ Con Rùa (Q.3), Nguyễn Huệ (Q.1), Phan Xích Long (Phú Nhuận).

## Phân bố

30 đồ uống, 15 đồ ăn/bánh và 5 combo. Giá gốc 12.000–69.000đ, chưa gồm size/topping. Combo vẫn là product bán trọn bộ; chưa có thay thế thành phần hoặc tách line combo.

| Nhóm | Số món |
| --- | --- |
| Cà phê | 10 |
| Trà & Trà sữa | 12 |
| Đồ uống khác | 8 |
| Đồ ăn | 9 |
| Bánh ngọt | 6 |
| Combo | 5 |

## Danh sách và giá

| Món | Nhóm | Giá gốc | Dữ liệu |
| --- | --- | --- | --- |
| Cà phê đen đá | Cà phê | 25.000đ | Giữ nguyên |
| Cà phê sữa đá | Cà phê | 29.000đ | Giữ nguyên |
| Bạc xỉu | Cà phê | 32.000đ | Giữ nguyên |
| Espresso | Cà phê | 30.000đ | Giữ nguyên |
| Latte | Cà phê | 39.000đ | Giữ nguyên |
| Cappuccino | Cà phê | 39.000đ | Giữ nguyên |
| Trà đào cam sả | Trà & Trà sữa | 39.000đ | Giữ nguyên |
| Trà vải | Trà & Trà sữa | 35.000đ | Giữ nguyên |
| Trà tắc | Trà & Trà sữa | 25.000đ | Giữ nguyên |
| Trà sen vàng | Trà & Trà sữa | 39.000đ | Giữ nguyên |
| Trà sữa truyền thống | Trà & Trà sữa | 35.000đ | Giữ nguyên |
| Trà sữa ô long | Trà & Trà sữa | 39.000đ | Giữ nguyên |
| Matcha latte | Đồ uống khác | 39.000đ | Giữ nguyên |
| Chocolate đá | Đồ uống khác | 35.000đ | Giữ nguyên |
| Nước cam | Đồ uống khác | 35.000đ | Giữ nguyên |
| Nước suối chai | Đồ uống khác | 12.000đ | Giữ nguyên |
| Bánh mì hotdog | Đồ ăn | 29.000đ | Giữ nguyên |
| Bánh mì gà xé | Đồ ăn | 35.000đ | Giữ nguyên |
| Sandwich jambon phô mai | Đồ ăn | 39.000đ | Giữ nguyên |
| Bánh mì trứng | Đồ ăn | 25.000đ | Giữ nguyên |
| Croissant bơ | Đồ ăn | 29.000đ | Giữ nguyên |
| Tiramisu | Bánh ngọt | 39.000đ | Giữ nguyên |
| Cheesecake | Bánh ngọt | 35.000đ | Giữ nguyên |
| Brownie chocolate | Bánh ngọt | 29.000đ | Giữ nguyên |
| Combo Bạc xỉu + Bánh mì hotdog | Combo | 55.000đ | Giữ nguyên |
| Combo Cà phê sữa + Bánh mì trứng | Combo | 49.000đ | Giữ nguyên |
| Combo Trà đào + Croissant bơ | Combo | 59.000đ | Giữ nguyên |
| Combo Latte + Tiramisu | Combo | 69.000đ | Giữ nguyên |
| Americano đá | Cà phê | 32.000đ | Mới thêm |
| Cold brew nguyên bản | Cà phê | 35.000đ | Mới thêm |
| Cà phê dừa | Cà phê | 42.000đ | Mới thêm |
| Cà phê muối | Cà phê | 39.000đ | Mới thêm |
| Trà dâu tằm | Trà & Trà sữa | 39.000đ | Mới thêm |
| Trà xoài | Trà & Trà sữa | 39.000đ | Mới thêm |
| Trà ổi hồng | Trà & Trà sữa | 39.000đ | Mới thêm |
| Trà chanh dây | Trà & Trà sữa | 35.000đ | Mới thêm |
| Trà sữa matcha | Trà & Trà sữa | 39.000đ | Mới thêm |
| Trà sữa đường đen | Trà & Trà sữa | 42.000đ | Mới thêm |
| Nước ép dưa hấu | Đồ uống khác | 35.000đ | Mới thêm |
| Nước ép dứa | Đồ uống khác | 35.000đ | Mới thêm |
| Sinh tố xoài | Đồ uống khác | 45.000đ | Mới thêm |
| Soda chanh | Đồ uống khác | 32.000đ | Mới thêm |
| Bánh mì bò phô mai | Đồ ăn | 45.000đ | Mới thêm |
| Sandwich gà nướng | Đồ ăn | 42.000đ | Mới thêm |
| Bagel trứng phô mai | Đồ ăn | 39.000đ | Mới thêm |
| Khoai tây chiên | Đồ ăn | 29.000đ | Mới thêm |
| Muffin việt quất | Bánh ngọt | 29.000đ | Mới thêm |
| Bánh chuối hạnh nhân | Bánh ngọt | 32.000đ | Mới thêm |
| Bánh su kem | Bánh ngọt | 25.000đ | Mới thêm |
| Combo Trà sữa ô long + Brownie | Combo | 59.000đ | Mới thêm |

## Kết quả thực tế

- API publication lần đầu: branches=3, products=50, changed=66 (22 món × 3 chi nhánh).
- Chạy lại initializer: changed=0, không tạo món trùng; 50 món có ID duy nhất ở từng chi nhánh.
- Đọc staff menu API của cả 3 chi nhánh: 50 món khả dụng, giá dương; required Single groups có đúng một default.
- Có 32 nhóm topping, maxSelections=null, không có giới hạn chọn 2 topping. Preview đồng bộ options: 0 món cần thay đổi.
- Dùng preset size/đường/đá/topping theo loại đồ uống, chế biến nóng/giữ nguyên cho đồ ăn phù hợp. Món cũ không đổi giá/mô tả/category/option IDs. Image URL vẫn dùng placeholder hiện có của Mobile.
- Credential BrandAdmin dùng DPAPI ngoài Git, không hiển thị; session kiểm tra đã logout. Không tạo tài khoản mới.

## File và cách tái sử dụng

Trong AICOS-BE: docs/fixtures/local-staff-menu.psd1, scripts/Initialize-LocalStaffMenu.ps1 và scripts/Expand-LocalStaffMenuOptions.ps1. Các file fixture/script giữ UTF-8 BOM cho PowerShell 5.1.

Initializer kiểm tra project/database/tenant/migrations=false, chỉ tạo món thiếu, dừng khi món cũ khác giá/mô tả/category. Expand script lấy số topping/món từ fixture thay vì hardcode 28/17. Preview không ghi dữ liệu.

Mobile lấy menu thật qua GET /api/v1/menu/branches/{branchId}/menu; mở lại màn chọn món để lấy danh mục mới. Không đổi UI hoặc Demo Mode. Dữ liệu này thuộc Docker local, người khác clone repo cần chạy fixture với local tenant/chi nhánh phù hợp; Git không mang theo volume dữ liệu.

Chỉ khởi động các container đã có phục vụ thao tác này: PostgreSQL, Redis, Identity, Menu và Gateway. Không khởi động Order/Kitchen/Payment, không recreate container/volume, không deploy hoặc commit/push.
