# Tự tiếp tục tạo ảnh và áp vào project

Trong Settings → Tools, tùy chọn **Tự tiếp tục tạo ảnh → đưa vào project → tích hợp** mặc định bật. Nó áp dụng trong session đã được cấp WriteProject, với connector đang bật và có quyền ghi/gọi AI. Không cần duyệt lại riêng từng bước nội bộ của chuỗi ảnh; kết quả cuối cùng vẫn được review tại task cha. Chọn provider/model theo chế độ routing hiện tại vẫn có hiệu lực.

## Luồng thực hiện

1. Bước chuẩn bị chỉ đọc project và chuẩn bị đầu vào.
2. Tool tạo ảnh, Manager lưu PNG thật cùng SHA256.
3. Manager tự chép PNG vào `Assets/AIEmployeeGenerated` nếu project có `Assets`, hoặc `AIEmployeeGenerated` ở project khác. Tên file chứa ID request và một phần hash. File có sẵn cùng nội dung được dùng lại; không ghi đè file đã được người dùng chỉnh.
4. Worker tiếp theo nhận `projectAssetPath` trong kết quả dependency, cấu hình import/slicing/pivot và nối ảnh vào scene, prefab hoặc UI thực tế. Chép file vào project chưa đồng nghĩa đã tích hợp vào game.
5. Với kế hoạch mới dùng Pixel mà chưa có bước tích hợp phía sau, Manager bổ sung bước WriteProject trong giới hạn số task đã cấp. Với imagegen trực tiếp trong Codex, yêu cầu worker tạo và tích hợp trong cùng lượt khi có thể.

Đề xuất sửa file từ provider không có công cụ trực tiếp được áp bằng giao dịch kiểm tra hash hiện có, chỉ trong chuỗi ảnh đã được cấp quyền ghi. Mọi giới hạn đường dẫn/loại file, hash hiện trạng và xung đột file vẫn có hiệu lực. Không tự nâng quyền, mở rộng project, bỏ qua lỗi file thật hay cấp thêm usage.

## Timeout và Resume

Khi đã có remote job ID của Pixel, Manager dừng chờ khi timeout/dừng task nhưng không gửi lệnh hủy job ảnh. Job có thể tiếp tục chạy ở backend. Run / resume truy vấn lại đúng job đó, không gửi lại lệnh tạo ảnh và không giữ thêm lượt gọi model. Mất kết nối tạm thời cũng giữ job để resume. Nút dừng vẫn dừng scheduler/worker hiện tại.

Ảnh đã hoàn tất được giữ trong thư mục data của Manager và có bản chép vào project. Task cũ đang chờ review nội bộ có thể tiếp tục từ kết quả đã có sau Run / resume. Job đã bị backend hủy trước bản sửa này, job bị backend xóa, hoặc ảnh chưa từng tạo thành công không thể tự khôi phục thành ảnh.

Giới hạn thời gian/usage, quyền thực tế, connector thiếu hoặc backend lỗi vẫn được báo cụ thể. Không tự tạo thêm ảnh để chữa lỗi import, không tuyên bố đã tích hợp chỉ vì có PNG.

Chỉ thay đổi source trong lần triển khai này: chưa build/test, chạy task, gọi AI hay chỉnh dữ liệu task đang có.
