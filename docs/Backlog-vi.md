# Backlog

## [Vừa] Gắn ProcBridge.Crypto vào transport (M2/M6)

- Broker/Agent chạy bắt tay IK khi mở phiên, XX + `PairingInitiator`/`PairingResponder` khi ghép cặp.
- Kết nối phụ của cùng phiên gửi id kênh ở dạng rõ trước khung đầu, bên kia gọi `AcceptChannel`.
- Prologue nên chứa số phiên bản giao thức (`ProtocolInfo.Version`) để hai bản lệch nhau bị từ chối.
- Điều kiện xong M5 "bắt gói trên dây không thấy nội dung" cần kiểm khi đã có transport.

## [Vừa] Lưu danh sách máy đã ghép cặp

Thư viện chỉ trả khoá công khai của bên kia và nhận delegate kiểm khoá; chưa có kho lưu khoá tin cậy
(file bọc DPAPI hoặc ký HMAC), xoá/thu hồi máy, hiển thị vân tay trên WPF của Agent.

## [Thấp] Giới hạn thử ghép cặp

Mỗi phiên ghép cặp chỉ được nhập mã một lần, nhưng chưa có giới hạn số phiên ghép cặp liên tiếp
(chống đoán mã bằng cách mở lại nhiều lần). Agent nên khoá tạm sau vài lần sai.

## [Thấp] Xoay khoá định danh

Chưa có cách thay khoá định danh của một máy mà không ghép cặp lại từ đầu.
