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

## [Vừa] Handle nhân bản sang process khác không được theo dõi

M4 theo dõi `NtDuplicateObject` trong cùng process và handle kế thừa khi tạo con. Nhân bản handle ảo sang
process khác (`DuplicateHandle` với process đích là con, vd truyền handle qua IPC) chưa được chuyển bảng nên
bên nhận thấy tên thật. Cần bên nhận hỏi Broker theo `(pid nguồn, handle)` ở M2. Ghi 2026-11-25.

## [Vừa] Inject con: các đường chưa phủ

- Process con chỉ được inject khi thread chính của nó được resume qua `NtResumeThread`/`NtResumeProcess`
  (hoặc tạo không treo). Bên gọi `CREATE_SUSPENDED` rồi chạy code trong con bằng thread từ xa trước khi
  resume (debugger, công cụ inject khác) sẽ chạy phần đó trước khi có hook.
- Con ARM64 (hoặc kiến trúc khác x64/x86) bị kết thúc vì chưa có `Hook.dll` ARM64.
- Process con không thuộc Win32 (pico, minimal) chưa thử.
- Bên gọi thẳng `NtCreateUserProcess` không xin thread treo được inject trước khi đăng ký csrss (giống hành
  vi gốc của chúng), chưa có test.
- Khối khởi tạo không còn biến cấu hình khi process đặt `PROCBRIDGE_MAP` khác đi giữa chừng: Hook dùng giá
  trị lúc khởi tạo, cố ý. Ghi 2026-11-25.

## [Thấp] Handle tạm của helper khác kiến trúc có thể lọt sang con khác

`InjectWithHelper` (`src/native/ProcBridge.Inject/src/Inject.cpp`) tạo bản sao kế thừa được của handle
process đích và section khối khởi tạo, chỉ cho helper kế thừa qua `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. Một
thread khác của cùng process tạo con với `bInheritHandles` và không có danh sách handle đúng lúc đó sẽ kế
thừa luôn hai handle này (rò handle, không hỏng dữ liệu). Ghi 2026-11-25.

## [Thấp] Build server dùng chung nằm ngoài cây process

`VBCSCompiler`, node MSBuild tái sử dụng và MSBuild server sống lâu hơn lệnh build: Spawn chờ cả Job nên sẽ
chờ tới khi chúng tự thoát, và nếu một server có sẵn từ trước (ngoài cây) nhận việc thì nó không có hook.
Test M4 tắt chúng (`-nodeReuse:false`, `--disable-build-servers`, `UseSharedCompilation=false`,
`DOTNET_CLI_USE_MSBUILD_SERVER=0`). Launcher M2 nên đặt sẵn các biến này hoặc có tuỳ chọn chờ chỉ process
đầu. Ghi 2026-11-25.

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

## [Thấp] Cache tên thư mục gốc chỉ dọn qua NtClose, NtDuplicateObject và lần mở mới

`DirectoryNameCache` (`src/native/ProcBridge.Hook/src/DirectoryNameCache.h`) xoá mục khi `NtClose`,
`NtDuplicateObject` (đóng nguồn hoặc bản sao trong cùng process) hoặc khi `NtCreateFile`/`NtOpenFile` trả lại
cùng giá trị handle. Giá trị handle được kernel cấp lại theo đường khác (vd handle do process khác nhân bản
vào) vẫn có thể dùng tên cũ. Ghi 2026-11-25.
