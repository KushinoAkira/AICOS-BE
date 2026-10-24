# Microservices Architecture

Tài liệu kiến trúc chuẩn của Backend được duy trì tại
[`PROJECT_ARCHITECTURE.md`](PROJECT_ARCHITECTURE.md) để không lặp lại cùng một
nội dung ở hai file.

Quyết định hiện hành:

- YARP Gateway chỉ xác thực token ở edge và không chứa nghiệp vụ.
- Database vật lý chính thức là `aicos_db`, hiện dùng chung schema `public`.
  Microservice giữ ownership logic theo nhóm bảng; service không ghi vào bảng
  thuộc service khác dù chúng cùng schema.
- REST dùng cho truy vấn/command đồng bộ; integration event dùng cho đồng bộ bất
  đồng bộ.
- Identity Organization là nguồn phát JWT và sở hữu tenant, branch, user, role,
  tenant membership và staff assignment.
- Identity map trực tiếp sáu bảng `public` có sẵn; không tạo schema/bảng
  trùng chức năng. Ranh giới service được thi hành ở code, DbContext và API.
- Table Reservation chỉ map `table_areas`, `cafe_tables`, `table_sessions` và
  `table_session_tables`. Service lấy branch từ JWT do Identity phát hành nhưng
  không truy cập bảng user/role trong runtime.
- Menu Catalog chỉ map sáu bảng catalog; phạm vi chi nhánh được lọc qua
  `branch_products` bằng branch claim, không đọc bảng Identity trong runtime.
