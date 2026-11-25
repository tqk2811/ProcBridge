# Backlog ProcBridge

Việc còn treo, chưa thuộc mốc đang làm. Xử lý xong mục nào thì xoá mục đó trong cùng commit.

## [Cao] Relay chưa giới hạn tần suất đăng nhập và đăng ký

`POST api/accounts/login` và `register` không có rate limit hay khoá tạm sau nhiều lần sai, nên dò mật khẩu
được; `Relay:Accounts:AllowRegistration` mặc định `true` nên ai cũng tạo được tài khoản. Cần
`AddRateLimiter` theo IP/tên đăng nhập và cân nhắc mặc định tắt đăng ký (tài khoản đầu tạo bằng CLI có quyền
quản trị). Chỗ sửa: `src/relay/ProcBridge.Relay.Api/Extensions/RelayHostServiceCollectionExtensions.cs`,
`src/relay/ProcBridge.Relay.Services.Accounts/Accounts/AccountService.cs`. Ghi 2026-11-25 (M6).

## [Vừa] Agent và Broker chưa đi qua relay

M6 mới có phía server + CLI. Điều kiện xong M6 trong mục 13 của Plan ("làm được M4 qua Relay") cần thư viện
phía client: Agent giữ `/tunnel/control`, nhận `sessionOpened` rồi mở lane; Broker gọi `api/tunnel/sessions`
rồi mở lane, chia khung dữ liệu ra nhiều lane, mã hoá đầu cuối (M5) chạy trên đó. Ghi 2026-11-25 (M6).

## [Vừa] Thu hồi máy hoặc quyền không cắt kết nối tunnel đang mở

`DeviceService.RevokeAsync` và `ShareService.RevokeGrantAsync` chỉ đổi database: kênh điều khiển của máy bị
thu hồi và các lane của phiên đã mở vẫn chạy tới khi tự đóng. Cần `ITunnelHub` có hàm cắt theo máy/chia sẻ
và gọi nó sau khi thu hồi. Chỗ sửa: `src/relay/ProcBridge.Relay.Services.Tunnel/Tunnel/TunnelHub.cs`. Ghi
2026-11-25 (M6).

## [Vừa] Relay chỉ chạy được một instance

`TunnelHub` giữ kênh điều khiển và phiên trong bộ nhớ tiến trình. Chạy nhiều instance sau load balancer thì
hai đầu của một phiên có thể rơi vào hai tiến trình khác nhau và không ghép được. Cần sổ đăng ký dùng chung
(vd PostgreSQL LISTEN/NOTIFY hoặc Redis) và chuyển tiếp giữa instance, hoặc định tuyến dính theo phiên. Ghi
2026-11-25 (M6).

## [Vừa] Chưa có log phiên tunnel

Plan 8.2 có "log phiên" trong PostgreSQL; M6 chỉ ghi log ứng dụng. Cần bảng phiên (ai mở, chia sẻ nào, lúc
mở/đóng, số byte) và API/CLI xem. Ghi 2026-11-25 (M6).

## [Thấp] Giới hạn tài nguyên tunnel

Chưa có trần kích thước message, quota băng thông, thời gian rảnh tối đa của lane, hay số phiên đồng thời mỗi
máy. Một máy được cấp quyền có thể giữ nhiều lane mở mãi. Ghi 2026-11-25 (M6).

## [Thấp] Dọn phiên đăng nhập hết hạn

Bảng `AccountSessions` chỉ thêm, không có việc nền xoá hàng hết hạn/đã thu hồi. Cần `BackgroundService`
(project `ProcBridge.Relay.BackgroundServices` theo kiến trúc ASP). Ghi 2026-11-25 (M6).

## [Thấp] Phần lệch khỏi khuôn ASP chuẩn ở relay M6

Cố ý làm gọn ở M6, nên bù khi relay lớn lên:

- Client typed `ProcBridge.Relay.Integration.Api` dùng `HttpClient` trần + Newtonsoft, chưa dùng
  `TqkLibrary.Asp.ToolKit.Integration`/`TqkLibrary.Http`.
- Không có `Services.Abstractions`: `IDevicePresence` (dùng xuyên feature) đặt tạm ở `Services.Common/Presence`.
- Kiểm dữ liệu vào viết tay trong service, chưa có Command + FluentValidation.
- Chưa có Serilog, OpenAPI/Scalar, `/health/ready`; `ApiErrorException`/filter vỏ JSON nằm trong host thay
  vì project `Api.Core`.
- Fake repository nằm trong `ProcBridge.Relay.Tests/Fakes`, chưa tách `tests/ProcBridge.Relay.Testing`.
- CLI lưu token phiên dạng chữ trong `%APPDATA%\ProcBridge\relay-cli.json`; nên bọc DPAPI.

Ghi 2026-11-25 (M6).
