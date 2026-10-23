# Microservices Architecture

Tài liệu kiến trúc chuẩn của Backend được duy trì tại
[`PROJECT_ARCHITECTURE.md`](PROJECT_ARCHITECTURE.md) để không lặp lại cùng một
nội dung ở hai file.

Quyết định hiện hành:

- YARP Gateway chỉ xác thực token ở edge và không chứa nghiệp vụ.
- Mỗi service sở hữu schema PostgreSQL riêng và không join xuyên service.
- REST dùng cho truy vấn/command đồng bộ; integration event dùng cho đồng bộ bất
  đồng bộ.
- Identity Organization là nguồn phát JWT và sở hữu tenant, branch, user, role,
  tenant membership và staff assignment.
- Module Authentication đầu tiên không làm thay đổi ranh giới service hiện có.
