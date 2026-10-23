# Service Boundaries

| Service | Sở hữu dữ liệu/nghiệp vụ | Không được sở hữu |
| --- | --- | --- |
| Identity Organization | Tenant, branch, user, role, staff assignment, login/token | Bàn, menu, order, payment |
| Table Reservation | Khu vực, bàn vật lý, phiên bàn, ghép/chuyển bàn | User, sản phẩm, chi tiết thanh toán |
| Menu Catalog | Category, product, variant, option, availability theo chi nhánh | Order, tồn kho thực tế |
| Ordering | Order, order item, customization snapshot, idempotency tạo đơn | Product master, payment transaction |
| Kitchen Operations | Hàng đợi pha chế và trạng thái chuẩn bị | Giá bán, bill, xác thực user |
| Payments | Bill, payment, refund, webhook/idempotency thanh toán | Order item preparation |
| Inventory Purchasing | Nguyên liệu, tồn kho, nhập hàng | Menu presentation, payment |
| Workforce Scheduling | Ca làm, phân ca, chấm công | Đăng nhập Staff |
| Reporting Analytics | Read model/báo cáo từ event | Ghi ngược domain nguồn |
| Customer Engagement | Loyalty, reward, feedback | Staff authentication |
| AI | RAG/forecast/analysis từ dữ liệu được cấp | Transaction nghiệp vụ nguồn |

## Quy tắc branch scope

- Identity xác minh `staff_assignments` và phát claim `branch_id`.
- Service nhận request phải kiểm tra claim và lọc dữ liệu theo branch của chính
  nó; không tin `branchId` do client gửi nếu khác claim.
- ID của domain khác được lưu như external reference, không tạo khóa ngoại xuyên
  schema/service.
