# Backlog ProcBridge

Việc còn treo, chưa thuộc mốc đang làm. Xử lý xong mục nào thì xoá mục đó trong cùng commit.

> Nhánh `feature/m1-local-hook` cũng tạo file này với các mục của M1. Khi gộp hai nhánh thì giữ mục của cả hai.

## [Cao] Ghép Hook.dll với Broker (phần còn lại của M2)

Phần C# của M2 (Protocol, Agent, Broker, IPC) đã xong; Hook vẫn dùng `PathMapper` cục bộ của M1. Cần:

- Client named pipe trong Hook theo `src/shared/ProcBridge.HookIpc/native/ProcBridge/HookIpc.h`: mở
  `\\.\pipe\ProcBridge-<phiên>`, đặt `PIPE_READMODE_MESSAGE`, mỗi thread một instance (hoặc pool nhỏ), gửi
  `Hello` lúc khởi động để lấy danh sách thư mục ảo; đọc trường sau chuỗi bằng `memcpy` (không căn lề).
- Cài `IPathResolver` dựa vào Broker: `FindMapping` vẫn so tiền tố cục bộ (từ `HelloReply`), `TranslateToReal`
  gọi `Resolve` rồi đổi `CachePath` (DOS) sang `\??\...`. `NtQueryAttributesFile`/`NtQueryFullAttributesFile`
  dùng `Stat`; `NtQueryDirectoryFile[Ex]` dựng kết quả từ các trang `List` thay vì đọc thư mục giữ chỗ.
- Trả nguyên `Status` của reply làm NTSTATUS cho hàm bị hook; Broker không trả lời trong hạn thì trả
  `STATUS_IO_TIMEOUT` (plan 4.6). Mã riêng của ProcBridge (`0xE000000x`) đổi sang mã chuẩn gần nhất trước khi
  trả cho process.
- Truyền tên phiên/pipe cho Hook (biến môi trường như `PROCBRIDGE_MAP` của M1, hoặc qua Launcher).
- Đưa `HookIpc.h` vào build native (include path của `ProcBridge.Hook`); hiện mới compile thử bằng tay với
  MSVC x86.
- Chạy lại bộ test so sánh M1 với thư mục ảo trỏ sang Agent trên loopback (điều kiện xong M2).

## [Cao] Hook kiểm tra server của pipe trước khi tin nó

Tên pipe là `ProcBridge-<phiên>`; Broker đã tạo instance đầu bằng `FILE_FLAG_FIRST_PIPE_INSTANCE` và sinh id phiên
ngẫu nhiên (`HookIpcProtocol.NewSessionId`), nhưng Hook (C++) vẫn phải chắc là đang nói chuyện với đúng Broker. Sau
khi mở pipe (và trước khi gửi `Hello`) gọi `GetNamedPipeServerProcessId` và so với PID của Broker mà Launcher truyền
qua (cùng nơi truyền tên pipe); không khớp thì đóng pipe và coi như không có thư mục ảo. Cũng nên mở pipe với
`SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION` để server giả không mượn được token của process.

## [Cao] Host Broker trong Launcher

`ProcBridge.Broker` là thư viện (`BrokerHost.Start`). Launcher cần nhận cấu hình (địa chỉ Agent, cổng, tên chia
sẻ, danh sách thư mục ảo ↔ thư mục máy B), sinh id phiên bằng `HookIpcProtocol.NewSessionId()` (không dùng id đoán được), khởi động `BrokerHost` trước khi tạo process cha và
dừng nó khi Job Object báo cả cây đã thoát.

## [Vừa] FileId và ChangeTime thật từ Agent

`FsEntry.FileId` đang là 0 và `ChangeTime` lấy bằng `LastWriteTime`. Cần mở handle (kể cả thư mục, cờ
`FILE_FLAG_BACKUP_SEMANTICS`) và gọi `GetFileInformationByHandleEx` (`FileBasicInfo`, `FileIdInfo`) ở `Stat`;
với `List` thì cân nhắc `NtQueryDirectoryFile` với `FileIdBothDirectoryInformation` để khỏi mở từng file.

## [Vừa] Điều khiển luồng bằng cửa sổ tín dụng

Plan mục 7 nói có cửa sổ tín dụng để file lớn không chặn request nhỏ. Hiện chỉ có: khối đọc tối đa
`MaxReadSize`, giới hạn số request đồng thời mỗi kết nối phía Agent, và nhiều kết nối song song. Chưa có ưu
tiên metadata hơn dữ liệu trên cùng một kết nối.

## [Vừa] Liệt kê thư mục lớn

Agent liệt kê lại và sắp xếp cả thư mục cho mỗi trang `FsList` (O(n²) với thư mục rất lớn). Nên giữ con trỏ
liệt kê theo kết nối hoặc cache danh sách ngắn hạn phía Agent.

## [Vừa] Cache phía Broker

- Bảng version chỉ nằm trong bộ nhớ: khởi động lại Broker là tải lại mọi file. Lưu version cạnh file cache.
- Chưa dọn cache theo dung lượng/phiên.
- File cache đang bị process mở mà máy B có bản mới thì `File.Move` thay thế thất bại (lỗi chia sẻ); cần chiến
  lược (giữ bản cũ cho handle đang mở, tải vào tên khác).
- Thư mục giữ chỗ chưa được đặt thời gian/thuộc tính như máy B (Hook trả metadata từ `FileInfo` nên chưa cần).

## [Vừa] Phát hiện kết nối treo

Request quá hạn trả `STATUS_IO_TIMEOUT` nhưng kết nối vẫn được coi là sống; Agent treo hẳn thì request nào cũng
chờ đủ `RequestTimeout`. Nên có ping/keepalive và đánh dấu kết nối chết sau vài lần quá hạn liên tiếp.

## [Thấp] Agent chưa xác thực, chưa mã hoá

Đến M5 mới có ghép cặp và mã hoá (`IFrameProtector` là chỗ cắm). Hiện Agent lắng nghe `0.0.0.0` không kiểm ai
kết nối; chỉ chạy trong LAN tin cậy.

## [Thấp] Review độc lập cho M2

Đợt M2 (phần C#) do một subagent làm, không được tạo subagent review nên chưa có lượt review độc lập
(READ-ONLY) cho Protocol/Agent/Broker/HookIpc. Nên review trước khi merge vào `master`.
