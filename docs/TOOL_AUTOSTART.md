# Tự mở kết nối tool khi task cần

Trong **Settings → Tools**, chọn connector, bật connector và **Tự mở backend khi task cần**, rồi lưu. Các quyền ghi/gọi AI/tự sử dụng trong Tasks vẫn áp dụng như trước.

Task và workflow dùng chung luồng: kiểm tra cổng → kiểm tra API → nếu cổng chưa chạy thì khởi động → đợi API sẵn sàng → gửi thao tác. Không mở thêm tiến trình khi cổng đã có dịch vụ nhưng bị lỗi token/API. Các task cùng cổng dùng chung bước khởi động. Kiểm tra kết nối thủ công chỉ đọc trạng thái, không khởi động.

- **Pixel Art:** Source folder trỏ tới Pixel Foundry có `server.mjs`. Để trống Backend .exe để dùng Node trên PATH, chạy lệnh đã đối chiếu từ source (`node --env-file-if-exists=.env server.mjs`) với PORT theo Local API URL. Cần Node >=22 đã cài và tài khoản Codex đã đăng nhập. Không tự mở giao diện trình duyệt.
- **Marketing:** Source folder trỏ tới Rikerat có `.venv\Scripts\python.exe` và `services\backend\rikerat`. Nhập Local API URL với một cổng cố định, token phiên và **Marketing backend DataRoot** tuyệt đối. Dùng DataRoot của dữ liệu bạn muốn làm việc; thư mục này không được thuộc output `runtime`, `staging`, `releases` theo quy tắc Rikerat. Manager chạy backend Python, truyền token qua stdin, không qua command line. Không tự tìm/lấy token của phiên khác. Đóng Manager gửi yêu cầu shutdown cho backend Marketing do Manager mở.
- **Video:** renderer chạy trực tiếp theo từng thao tác; không cần mở cửa sổ ứng dụng trước.
- **Backend tùy chỉnh:** nhập executable native `.exe`, thư mục chạy và danh sách tham số JSON (ví dụ `["server.mjs"]`). Không dùng BAT/CMD/PowerShell launcher. Khi cấu hình executable tùy chỉnh, bạn chịu trách nhiệm cung cấp cơ chế khởi động tương ứng; giao thức stdin-token tự động chỉ dành cho preset Marketing.

Thời gian chờ mở mặc định **90 giây**, chỉnh từ 5–600 giây, tách riêng timeout thao tác. Giới hạn thời gian của task/session và nút dừng vẫn có hiệu lực trong lúc chờ. Lỗi mở/kết nối chưa gửi thao tác, chưa giữ lượt gọi model. Log kết nối nằm trong lịch sử task và phần KẾT NỐI của request Tools.

Manager không tự cài dependencies, build tool, đăng nhập hay chạy lại thao tác ghi đã thất bại. Backend Pixel/tùy chỉnh do Manager mở tiếp tục chạy sau khi đóng Manager; lần sau sẽ kết nối lại. Không tắt ứng dụng người dùng đã mở sẵn. Đổi cấu hình backend làm các request chờ thực hiện cần duyệt lại.

Thay đổi này chỉ chỉnh source. Chưa build, chạy test, khởi động backend hay gọi model trong phiên triển khai.
