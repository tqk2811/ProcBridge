# Backlog ProcBridge

Việc còn treo, chưa thuộc mốc đang làm. Xử lý xong mục nào thì xoá mục đó trong cùng commit.

## [Vừa] Thư mục gốc ảo không hiện trong thư mục cha

Hook chỉ dịch đường dẫn nằm *trong* thư mục ảo. Liệt kê thư mục cha (`FindFirstFile("D:\Work\virtual")`
không wildcard, `GetLongPathNameW` trên đường dẫn ảo) mở thư mục cha thật nên không thấy thư mục ảo và trả
`ERROR_FILE_NOT_FOUND`. Cần chèn mục giả cho thư mục gốc ảo khi liệt kê thư mục cha (hook
`NtQueryDirectoryFile[Ex]` trên handle của thư mục cha). NativeProbe hiện cố ý không gọi hai API này.

## [Vừa] Ký tự ổ giả (V:\) làm thư mục ảo

Plan 4.3 cho phép thư mục ảo nằm trên ổ không tồn tại. M1 chưa thử: `QueryDosDeviceW` của ổ ảo thất bại nên
`NtQueryObject` trả tên theo volume của thư mục thật (`GetFinalPathNameByHandle` ra sai ổ), và
`GetVolumeInformation("V:\")`, `GetDriveType` chưa được giả lập.

## [Vừa] Đường dẫn dạng khác chưa nhận diện

- `\Device\HarddiskVolumeN\...`, `\DosDevices\...`, `\GLOBAL??\...` chưa được đưa về dạng `\??\` trước khi so.
- Tên ngắn 8.3 (`RUNNER~1`) trong đường dẫn ảo hoặc thật: so tiền tố theo chuỗi nên không khớp với dạng tên
  dài, và tên trả về từ `FileNameInformation` có thể không đổi ngược được.
- Mở theo file ID (`FILE_OPEN_BY_FILE_ID`) không qua đường dẫn nên không được ghi vào bảng handle.

## [Vừa] Handle nhân bản không được theo dõi

`DuplicateHandle`/`NtDuplicateObject` và handle kế thừa sang process con không vào bảng handle, nên tên trả
về từ handle nhân bản là đường dẫn thật. Process con thuộc M4 (plan 5.5); `NtDuplicateObject` trong cùng
process nên làm sớm hơn.

## [Thấp] Bảng handle là mảng tìm tuần tự

`HandleTable` dò tuần tự dưới SRW lock; đủ cho spike (ít handle ảo) nhưng process mở hàng nghìn file ảo sẽ chậm
ở `NtClose`. Đổi sang bảng băm theo giá trị handle.

## [Thấp] Test với .NET hiện đại và Notepad

- Test .NET hiện tại dùng PowerShell 5.1 (.NET Framework). Thêm một app console .NET 10 nhỏ đọc file qua thư
  mục ảo để phủ runtime mới (khác đường gọi `CreateFileW` của `System.IO`).
- Notepad trên Windows 11 là app packaged, `notepad.exe` chỉ kích hoạt app ngoài cây process nên không hook
  được. Cần một trình soạn thảo cổ điển (vd Notepad cũ trong `System32` trên máy chưa cài bản packaged) để
  kiểm mở/lưu, hoặc cân nhắc hỗ trợ kích hoạt app packaged (ngoài phạm vi hiện tại).

## [Thấp] Kiểm tra ánh xạ gốc ổ đĩa/UNC ở tầng Hook chưa có test

`PathMapper::AddMapping` (`src/native/ProcBridge.Hook/src/PathMapper.cpp`) từ chối gốc ổ đĩa và UNC, nhưng
test chỉ phủ được lớp kiểm của `ProcBridge.Spawn` (Spawn từ chối trước khi tới Hook). Cần một đường đặt
`PROCBRIDGE_MAP` trực tiếp (vd tuỳ chọn ẩn của Spawn hoặc Launcher M2) để test lớp Hook. Ghi 2026-11-25.

## [Thấp] Giới hạn tên 0xFFFC byte chưa có test

`HookNtQueryObject` (`src/native/ProcBridge.Hook/src/FileHooks.cpp`) giới hạn tên viết lại ở 0xFFFC byte để
`MaximumLength` không tràn `USHORT`. Chạm tới giới hạn cần đường dẫn gần 32767 ký tự nên chưa có test.
Ghi 2026-11-25.

## [Thấp] Cache tên thư mục gốc chỉ dọn qua NtClose và lần mở mới

`DirectoryNameCache` (`src/native/ProcBridge.Hook/src/DirectoryNameCache.h`) xoá mục khi `NtClose` hoặc khi
`NtCreateFile`/`NtOpenFile` trả lại cùng giá trị handle. Handle thư mục đóng bằng đường khác rồi giá trị được
cấp lại qua `NtDuplicateObject` (không bị hook) có thể dùng tên cũ. Gộp vào việc theo dõi
`NtDuplicateObject` ở mục "Handle nhân bản". Ghi 2026-11-25.

## [Vừa] Gộp nhánh M2 với nhánh M4 (cây process)

`feature/m2-integration` sửa `FileHooks.cpp`, `PathMapper.h` (thêm `Resolve`/`Stat`/`List` vào `IPathResolver`),
`dllmain.cpp`, `ProcBridge.Spawn` (`--broker`) và `NativeProbe`; nhánh M4 cũng sửa các file này (bảng handle,
hook process). Khi gộp: `RedirectedAttributes` nay nhận `OpenIntent*`, `TrackOpenedHandle` gọi thêm
`TrackRemoteHandle`, `NtClose` dọn cả `g_remoteHandles`. Process con của M4 cần nhận cùng biến
`PROCBRIDGE_SESSION`/`PROCBRIDGE_BROKER_PID` và chuyển các mục của `RemoteHandleTable` khi kế thừa handle.

## [Vừa] Liệt kê thư mục ảo trên handle bất đồng bộ

Hook `NtQueryDirectoryFile[Ex]` trả kết quả ngay (không `STATUS_PENDING`), chỉ báo `Event`; `ApcRoutine` và
completion port gắn với handle không được dùng. Process mở thư mục bằng `FILE_FLAG_OVERLAPPED` rồi đợi qua
IOCP sẽ không nhận được gói hoàn tất. Bỏ qua `SL_INDEX_SPECIFIED`; `FileIndex` luôn 0, không có tên 8.3
(`ShortName` rỗng); class khác các class phổ biến trả `STATUS_INVALID_INFO_CLASS`.

## [Vừa] Đường dẫn ảo từ xa: các ca chưa hỗ trợ

- Luồng dữ liệu phụ (`a.txt:stream`, `<gốc>:stream`) gửi nguyên sang Broker nên luôn "không tìm thấy".
- `NtQueryInformationByName` với class ngoài Basic/Standard/NetworkOpen/AttributeTag/Stat phải tải trọn
  file vào cache để hỏi bản cache.
- Thư mục cha của gốc ảo không có trên máy B: mục `..` khi liệt kê gốc ảo lặp lại thông tin của chính gốc.
- Volume (serial, cờ, tên hệ thống file) vẫn là của volume chứa cache; `FileIdInformation` chỉ có 64 bit thấp
  (file index của máy B), chưa đúng với ReFS.

## [Vừa] Thời hạn IPC cho file lớn

Hook chờ Broker tối đa `PROCBRIDGE_IPC_TIMEOUT_MS` (mặc định 60 giây) cho mọi request, kể cả `Resolve` phải tải
trọn file. File lớn qua mạng chậm sẽ quá hạn (`STATUS_IO_TIMEOUT`) dù Broker vẫn đang tải. Cần thời hạn riêng
cho tải, hoặc Broker trả "đang tải" để Hook chờ tiếp.

## [Vừa] Launcher chưa đủ cho người dùng

- `ProcBridge.Spawn`/`ProcBridge.Hook` chưa được đóng gói cạnh Launcher (mặc định tìm trong thư mục của Launcher,
  `--spawn` để chỉ chỗ khác); chưa đọc cấu hình từ file.
- Chỉ đợi process đích; process con sống lâu hơn nó mất Broker (đợi cả cây bằng Job Object là M4).
- Broker hay Agent không kết nối được lúc khởi động thì Spawn báo `hook initialization failed` và không chạy
  process đích; chưa có thông báo dễ hiểu hơn.

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

Đợt M2 (phần C# và phần ghép Hook ↔ Broker) do subagent làm, không được tạo subagent review nên chưa có lượt
review độc lập (READ-ONLY) cho Protocol/Agent/Broker/HookIpc và `BrokerClient`/`BrokerResolver`/`RemoteHandles`
của Hook. Nên review trước khi merge vào `master`.
