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

- `\DosDevices\...`, `\GLOBAL??\...` chưa được đưa về dạng `\??\` trước khi so. `\Device\HarddiskVolumeN\...` tuyệt đối
  được nhận khi mở (M3, advapi32 mở thư mục cha qua dạng này khi áp ACE kế thừa), các hàm khác theo đường dẫn
  (`NtQueryAttributesFile`, `NtDeleteFile`...) cũng đi qua cùng lối dịch.
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
- Resume từ process khác: Hook chỉ thấy `NtResumeThread`/`NtAlertResumeThread`/`NtResumeProcess` gọi trong
  chính process cha. Cha tạo con `CREATE_SUSPENDED` rồi nhờ process khác (broker, debugger) resume thì con
  chạy không có hook. Cần Broker (M2) hoặc chặn ở phía con. Ghi 2026-11-25.
- Helper khác kiến trúc mở process inject bằng PID (`PROCESS_DUP_HANDLE`) để lấy handle; process inject chạy
  với token hạn chế hơn helper (AppContainer, integrity thấp) mà helper không mở được thì inject thất bại và
  con bị kết thúc. Chưa thử. Ghi 2026-11-25.
- Con ARM64 (hoặc kiến trúc khác x64/x86) bị kết thúc vì chưa có `Hook.dll` ARM64.
- Process con không thuộc Win32 (pico, minimal) chưa thử.
- Bên gọi thẳng `NtCreateUserProcess` không xin thread treo được inject trước khi đăng ký csrss (giống hành
  vi gốc của chúng), chưa có test.
- Khối khởi tạo không còn biến cấu hình khi process đặt `PROCBRIDGE_MAP` khác đi giữa chừng: Hook dùng giá
  trị lúc khởi tạo, cố ý. Ghi 2026-11-25.

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

## [Vừa] Handle thư mục ảo kế thừa sang con bắt đầu lượt liệt kê mới

Khi con kế thừa handle thư mục ảo (chế độ Broker), khối khởi tạo chỉ mang metadata máy B và đường dẫn ảo; con
dựng lại danh sách từ `List` và bắt đầu từ đầu. Trên hệ thống file thật, cha và con dùng chung file object nên
chung con trỏ liệt kê. Ca cha đang liệt kê dở rồi giao handle cho con đọc tiếp rất hiếm. Nếu cần: chuyển con trỏ
(và pattern) qua bản ghi extra, chấp nhận lệch khi hai bên cùng đọc. Handle nhân bản sang process khác
(`DuplicateHandle` vào process khác) vẫn không mang mục `RemoteHandleTable`. Ghi 2026-11-25.

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

## [Vừa] Ghi (M3): các ca chưa phủ

- Phát hiện thay đổi theo kích thước + thời gian ghi của file cache: tiến trình ghi rồi đặt lại đúng thời gian và
  kích thước cũ thì không được ghi ngược. Cân nhắc hook `NtWriteFile`/`NtCreateSection` (ghi) để đánh dấu dirty.
- Ghi ngược trọn file mỗi lần đóng; chưa gửi theo khối thay đổi (file lớn sửa vài byte vẫn tải lên cả file).
- Xoá qua disposition gửi sang máy B ngay lúc đặt cờ: đặt lại `DeleteFile = FALSE` sau đó chỉ huỷ trên bản cache.
  Delete-on-close xoá máy B khi handle có cờ đóng, kể cả khi handle khác của file còn mở.
- Handle có write token kế thừa sang con: token đi theo bản ghi trong khối khởi tạo và cha gửi `Retain` cho Broker
  lúc tạo con, nên lần đóng cuối của con cũng ghi ngược. Còn hở: con bị kết thúc trước khi nhận khối khởi tạo (inject
  thất bại) thì phần giữ của nó không bao giờ được trả, file coi như còn mở tới hết phiên; nhân bản handle sang
  process khác (`DuplicateHandle`) vẫn không mang token. Process thoát không đóng handle (kể cả con giữ handle kế thừa)
  thì Broker ghi ngược khi phiên kết thúc (`SyncOpenAsync`), không phải lúc process thoát.
- Hết phiên chỉ ghi ngược file còn token mở; chưa quét mọi file cache đã đổi so với version đã tải (`_versions`):
  file tạo mới hay ghi đè không có version để so, nên cách quét này chỉ phủ được một phần.
- Delete-on-close mang file ID máy B lúc mở: Agent không xoá nếu tên đã sang file khác. `File.Replace` của commit ghi
  ngược có thể đổi file ID của tên trên máy B, khi đó delete-on-close sau một lần ghi ngược sẽ không xoá (an toàn,
  nhưng file còn lại).
- Hard link (`FileLinkInformation`) trên thư mục ảo trả `STATUS_NOT_SUPPORTED`; đổi tên giữa thư mục ảo và thư mục
  thật trả `STATUS_NOT_SAME_DEVICE` (MoveFileEx tự sao chép rồi xoá).
- Tên trần trong `FILE_RENAME_INFORMATION` (không `\`, không `RootDirectory`): Hook hiểu là cùng thư mục với nguồn,
  nhưng trên NTFS thật `SetFileInformationByHandle` trả `ERROR_ALREADY_EXISTS` trong kịch bản thử; chưa rõ ngữ nghĩa
  nên NativeProbe không kiểm.
- Handle khác đang mở file bị một rename thay thế được đánh dấu mồ côi trong cùng process (ReplaceFile); process khác
  vẫn giữ đường dẫn cũ.
- Xung đột KeepBoth: bản của máy A lưu thành `*.procbridge-conflict-<UTC>` và Broker ghi log, Launcher chưa hiện cảnh
  báo; handle còn mở trên máy A tiếp tục dùng bản cache của mình tới khi đóng hết.
- File staging `*.pbtmp-<id>` của upload dở dang (Agent chết giữa chừng) không được Agent dọn khi khởi động.
- Lỗi vĩnh viễn từ máy B khi ghi ngược (quyền, thư mục đã mất) để lại `<id>.json.failed` + `<id>.data` trong thư mục
  hàng đợi, chưa có công cụ xem/gửi lại.
- Chế độ ghi xuyên gửi Flush sau mỗi `NtWriteFile` xong đồng bộ; ghi overlapped (`STATUS_PENDING`) chỉ được gửi lúc
  flush/đóng. Chưa có cấu hình ghi xuyên theo thư mục (Plan 10).
- Remote Explorer (Plan 14) chưa có thao tác file.

## [Vừa] Thời hạn IPC cho file lớn

Hook chờ Broker tối đa `PROCBRIDGE_IPC_TIMEOUT_MS` (mặc định 60 giây) cho mọi request, kể cả `Resolve` phải tải
trọn file. File lớn qua mạng chậm sẽ quá hạn (`STATUS_IO_TIMEOUT`) dù Broker vẫn đang tải. Cần thời hạn riêng
cho tải, hoặc Broker trả "đang tải" để Hook chờ tiếp.

## [Vừa] Launcher chưa đủ cho người dùng

- `ProcBridge.Spawn`/`ProcBridge.Hook` chưa được đóng gói cạnh Launcher (mặc định tìm trong thư mục của Launcher,
  `--spawn` để chỉ chỗ khác); CLI chưa đọc cấu hình từ file. Launcher WPF cũng tìm Spawn trong thư mục của nó
  (`LaunchOptionsFactory`) nên cần cùng bước đóng gói.
- Broker hay Agent không kết nối được lúc khởi động thì Spawn báo `inject failed` và không chạy
  process đích; chưa có thông báo dễ hiểu hơn.

## [Vừa] Launcher WPF: phía A không biết danh sách chia sẻ và thư mục gốc của chúng

- Vị trí: `src/client/ProcBridge.Launcher.Wpf/Models/LaunchProfile.cs` (`ShareRoot`), `Services/LaunchOptionsFactory.cs`.
- Giao thức `Fs.*` dùng đường dẫn tương đối trong chia sẻ, còn Remote Explorer trả đường dẫn tuyệt đối trên máy B.
  Hồ sơ WPF vì vậy bắt người dùng tự nhập tên chia sẻ và chọn "gốc chia sẻ trên B" (thư mục chọn đầu tiên được
  gợi ý làm gốc), rồi đổi đường dẫn chọn được sang tương đối. Gõ sai gốc thì chỉ lộ lúc chạy (đọc sai thư mục).
- Hướng: thêm message cho A liệt kê chia sẻ (tên + gốc) mà máy B cho phép, để chọn chia sẻ bằng ComboBox và
  kiểm thư mục chọn nằm trong gốc ngay khi chọn.

## [Thấp] Launcher WPF: thư mục làm việc không được nằm trong thư mục ảo

- Vị trí: `src/client/ProcBridge.Launcher/LaunchSession.cs` (kiểm `Directory.Exists(WorkingDirectory)`).
- Spawn được tạo với thư mục làm việc của target, mà Spawn không bị hook nên thư mục ảo không tồn tại với nó.
  Cần Spawn nhận `--cwd` và truyền `lpCurrentDirectory` cho process đích sau khi hook đã nạp (hoặc đặt thư mục
  qua hook lúc khởi động).

## [Thấp] Launcher WPF: Remote Explorer còn thiếu thao tác và test giao diện

- Vị trí: `src/client/ProcBridge.Launcher.Wpf/ViewModels/RemoteExplorerViewModel.cs`, `RegistryViewModel.cs`.
- Chưa có: tải file về A, đổi tên/tạo thư mục, tạo/sửa giá trị và khoá registry (client đã có API), xoá thư mục.
  Cửa sổ chọn thư mục chưa tự mở tới đường dẫn đã chọn lần trước (chỉ hiện gợi ý).
- Test mới phủ ViewModel; giao diện chỉ được thử khởi động/tắt và chụp màn hình bằng tay, chưa có test UI tự động.

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

## [Thấp] Review độc lập cho M2

Đợt M2 (phần C# và phần ghép Hook ↔ Broker) do subagent làm, không được tạo subagent review nên chưa có lượt
review độc lập (READ-ONLY) cho Protocol/Agent/Broker/HookIpc và `BrokerClient`/`BrokerResolver`/`RemoteHandles`
của Hook. Nên review trước khi merge vào `master`.

## [Vừa] Hook NtNotifyChangeDirectoryFile[Ex]: các ca chưa phủ

Hook đã phục vụ thông báo thay đổi trên thư mục ảo (`DirectoryWatches`, test `RemoteNotifyTests`). Còn thiếu:

- Handle bất đồng bộ gọi không kèm `Event`, `ApcRoutine` hay completion port (vd `ReadDirectoryChangesW` với
  `OVERLAPPED.hEvent = NULL` rồi `GetOverlappedResult` chờ trên chính handle): hook không báo hiệu được file object
  nên lời chờ đó treo.
- `FILE_NOTIFY_EXTENDED_INFORMATION` chỉ có `Action` và tên; thời gian, kích thước, thuộc tính, file id để 0 (Broker
  không gửi metadata kèm thay đổi). `DirectoryNotifyFullInformation` (lớp 3) trả `STATUS_INVALID_INFO_CLASS`.
- `CompletionFilter` chỉ lọc theo loại hành động (tên / sửa đổi / stream), không biết thuộc tính nào đổi; filter của
  lời gọi đầu tiên được giữ cho cả watch.
- Handle nhân bản (`DuplicateHandle`) hay kế thừa sang con không dùng chung watch với handle gốc như file object thật;
  bản sao gọi notify thì mở watch riêng.
- Huỷ (`CancelIo*`, alert) một long poll đang chờ thì lô thay đổi Broker gửi đúng lúc đó có thể bị mất (kernel giữ
  lại cho lần gọi sau); Hook bù bằng cách trả `STATUS_NOTIFY_ENUM_DIR` cho yêu cầu kế tiếp (bên gọi quét lại), chưa
  giữ lại đúng lô. Lời gọi đồng bộ trên handle `FILE_SYNCHRONOUS_IO_ALERT` bị APC cắt ngang thì trả `STATUS_USER_APC` và
  huỷ yêu cầu.
- Thread gọi thoát không huỷ các yêu cầu nó đã gửi (kernel huỷ IRP của thread khi thread thoát).

## [Vừa] Mỗi watch có yêu cầu chờ chiếm một thread pool và một pipe instance

`DirectoryWatches` chạy một worker (`QueueUserWorkItem`, `WT_EXECUTELONGFUNCTION`) và giữ một pipe instance riêng
cho mỗi handle thư mục ảo đang có yêu cầu chờ. Process mở hàng trăm handle theo dõi (IDE, công cụ build) sẽ chiếm
nhiều thread pool và nhiều instance của pipe Broker. Phương án: dồn mọi watchId của process vào MỘT long poll
(`WaitChanges` nhận danh sách watchId, trả `(watchId, thay đổi)`), một worker chung phân phát theo handle.
Ghi 2026-11-25.

## [Vừa] Theo dõi thay đổi: phần còn thiếu sau M7 (C#)

- `CompletionFilter` chưa được áp đúng: Agent dùng `FileSystemWatcher` nên không biết thuộc tính nào đổi, Broker
  báo mọi thay đổi trong thư mục (Hook chỉ lọc được theo loại hành động). Cân nhắc P/Invoke `ReadDirectoryChangesExW` với
  `ReadDirectoryNotifyExtendedInformation` để gửi kèm loại thay đổi.
- `FsWatchRequest.Path` mới để dành: Agent luôn theo dõi cả share. Share rất lớn thì nên theo dõi đúng các thư mục
  được map.
- Watcher kết nối lại thì cache bị xoá metadata; chưa nạp lại cây (`PreloadTreeAsync`) sau khi kết nối lại.
- `FileCache.Invalidate` quét toàn bộ khoá theo tiền tố cho mỗi thay đổi xoá/đổi tên (O(số mục cache)); cache rất
  lớn và lô thay đổi lớn thì nên dùng cây theo thư mục.
- Walk `FsTree` bỏ dở giữa chừng giữ trạng thái trên Agent tới khi kết nối đóng (giới hạn bởi
  `MaxTreeWalksPerConnection`); Broker hiện mở kết nối riêng cho mỗi walk nên không bị ảnh hưởng.

## [Vừa] Đo hiệu năng M7 với độ trễ thật

`src/tools/ProcBridge.Bench` mới đo trên loopback. Điều kiện xong M7 là đo thời gian build so với chạy cục bộ: cần
một `IConnectionFactory` chèn độ trễ/băng thông giả lập (hoặc chạy qua LAN/relay thật), và chạy build thật (vd
`dotnet build` một solution mẫu) qua Hook khi Hook đã nối Broker. Read-ahead hiện tranh connection pool với request
tiền cảnh (chưa có ưu tiên, liên quan mục cửa sổ tín dụng; mặc định đã hạ xuống 2 tải song song cho toàn cache) và
không bị huỷ khi `BrokerHost` dừng.

## [Thấp] Đổi tên máy đã ghép cặp

App WPF của Agent (`ProcBridge.Agent.Wpf`) đã liệt kê máy đã ghép cặp (vân tay, tên, lần cuối) và thu hồi; còn thiếu đổi
tên máy (`PairedMachineStore` chưa có API đổi tên) và màn hình tương tự phía Launcher (máy A).

## [Thấp] App WPF của Agent: phần còn thiếu

- Chưa có icon riêng (khay hệ thống dùng icon mặc định), chưa tách chuỗi giao diện ra tài nguyên i18n.
- "Lần cuối" của máy đã ghép cặp chỉ ghi khi app WPF đang chạy và thấy kết nối (Agent console không ghi), độ mịn 1 giây.
- Hộp thoại ghép cặp mới test ở tầng ViewModel; chưa có test UI thật (UIA) và chưa chạy thử ghép cặp với máy A thật.
- Agent console và app WPF dùng chung thư mục dữ liệu; chạy cả hai cùng lúc với cùng cổng thì cái sau không nghe được
  cổng, và hai bên không thấy thay đổi kho của nhau cho tới khi mở lại (kho nạp file một lần lúc khởi tạo).
- Ghi 2026-11-25.

## [Thấp] Giới hạn thử ghép cặp

Mỗi phiên ghép cặp chỉ được nhập mã một lần, nhưng chưa có giới hạn số phiên ghép cặp liên tiếp
(chống đoán mã bằng cách mở lại nhiều lần). Agent nên khoá tạm sau vài lần sai. Đã có: chỉ một phiên chờ
người dùng tại một thời điểm và giới hạn kết nối đang bắt tay theo IP; chưa có khoá tạm theo số lần sai.

## [Thấp] Xoay khoá định danh

Chưa có cách thay khoá định danh của một máy mà không ghép cặp lại từ đầu.

## [Vừa] Explorer: audit log và tách model dùng chung

- Đã nối giao thức (`ExplorerDispatcher` ở Agent, `ExplorerClient` ở A, message `0x11xx`-`0x14xx`) và có
  `ExplorerPolicyStore` theo vân tay A (lưu file DPAPI `explorer-policies.bin`, đặt qua app WPF hoặc `--explorer` của
  Agent console). Còn thiếu: audit log mới là interface (chưa ghi ra file/Event Log).
- Model request/response nằm trong `ProcBridge.Agent.Explorer` (thư viện phía B) nên `ProcBridge.Explorer.Client`
  phía A phải tham chiếu cả provider. Nên tách model ra project dùng chung (vd `ProcBridge.Explorer.Contracts`).
- Hồ sơ quyền đọc lúc mở kết nối: đổi quyền chỉ áp cho kết nối mới (test `ProfileChange_AppliesToNewConnections`).
- Ghi ngày: 2026-11-25.

## [Thấp] Ba kho file bọc DPAPI viết lặp

`IdentityKeyStore`, `PairedMachineStore`, `ExplorerPolicyStore` cùng khuôn "magic + version + blob bọc
`IKeyProtector`, ghi nguyên tử qua file tạm". Cần hỏi user có gom thành một thành phần dùng chung không (quy tắc gom
phần giống nhau); chưa tự gom vì `IdentityKeyStore` thuộc nhánh m5 đã review. Ghi 2026-11-25.

## [Vừa] Explorer: tải file lên, xuất `.reg`, chạy chương trình trên máy B chưa làm

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/FileExplorerProvider.cs:89`, `RegistryExplorerProvider.cs:53`, `ProcessExplorerProvider.cs:19`.
- Vấn đề: Plan mục 14 có tải lên/ghi file, xuất `.reg`, chạy chương trình; lõi hiện chỉ có xoá/đổi tên/tạo thư mục, sửa khoá/giá trị, kill. Tải file về đang trả `byte[]` trong DTO (`FileReadRequest`), khi nối transport nên chuyển sang khung nhị phân thô theo mục 15.1.
- Ghi ngày: 2026-11-25.

## [Thấp] Explorer: danh sách process thiếu dòng lệnh, user, CPU, module

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/ProcessExplorerProvider.cs:55` (`ToEntry`).
- Vấn đề: Plan mục 14 liệt kê dòng lệnh, user, CPU, module đã nạp; hiện chỉ có PID, PID cha, tên, đường dẫn image, session, RAM, giờ bắt đầu. Cần đọc PEB (`NtQueryInformationProcess` class 60 cho command line), token user, thời gian CPU.
- Ghi ngày: 2026-11-25.

## [Thấp] Explorer: hard link trong gốc trỏ tới file ngoài gốc vẫn đọc được

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/PathScopeGuard.cs` (`Checked`).
- Vấn đề: `GetFinalPathNameByHandle` trả tên đã dùng để mở, nên hard link nằm trong gốc tới một file ngoài gốc qua được kiểm phạm vi. Tạo hard link cần quyền ghi thuộc tính trên file đích nên rủi ro thấp, nhưng nếu Agent chạy quyền cao hơn user tạo link thì vẫn đọc được file user đó không đọc được. Hướng: với file có `NumberOfLinks > 1`, liệt kê mọi tên (`FindFirstFileNameW`) và đòi tất cả nằm trong gốc, hoặc từ chối.
- Ghi ngày: 2026-11-25.

## [Thấp] Explorer: registry chưa chặn symbolic link của registry, chưa hỗ trợ view 32/64-bit

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/RegistryScopeGuard.cs:44`.
- Vấn đề: khoá kiểu `REG_LINK` trong gốc được phép có thể trỏ sang khoá ngoài gốc (hiếm, cần quyền tạo); `RegistryView` đang cố định `Default`, chưa cho chọn WOW6432Node.
- Ghi ngày: 2026-11-25.

## [Thấp] Explorer: thông tin hệ thống còn sơ sài

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/SystemInfoProvider.cs:28`.
- Vấn đề: RAM lấy theo `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` (bị giới hạn nếu có job/container), chưa có dung lượng ổ, user đang đăng nhập phiên console (mới có user chạy Agent). Nên dùng `GlobalMemoryStatusEx` và `WTSEnumerateSessions`.
- Ghi ngày: 2026-11-25.

## [Thấp] Explorer: test chưa phủ start/stop service thật và symlink file

- Vị trí: `src/tests/ProcBridge.Tests/Explorer/ServiceAndSystemInfoTests.cs`, `FileExplorerProviderTests.cs` (`SymbolicLinkFileLeadingOutside_IsOutOfScope`).
- Vấn đề: không đụng service thật nên nhánh Start/Stop/Restart chưa chạy trong test (cần service giả cài tạm, chạy admin); test symlink file bị Inconclusive khi máy không bật Developer Mode/không chạy admin.
- Ghi ngày: 2026-11-25.

## [Thấp] Explorer: handle nguồn của Delete/Rename mở FileShareAll nên process cục bộ có quyền ghi hai nơi có thể dời object ra ngoài sau khi kiểm

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/PathScopeGuard.cs` (`OpenEntryForChange`).
- Vấn đề: handle nguồn của Delete/Rename được mở với `FileShareAll` (cần để xoá/đổi tên khi file đang bị process khác mở). Một process cục bộ có quyền ghi ở cả hai nơi (trong và ngoài gốc) có thể dời object đã qua kiểm phạm vi ra ngoài gốc trước khi thao tác chạy; thao tác vẫn tác động đúng object đã kiểm nhưng object đó nay nằm ngoài gốc. Không mở rộng quyền của máy A (máy A không điều khiển được process cục bộ này); chỉ cần lưu ý nếu Agent chạy quyền cao hơn user cục bộ. Hướng nếu cần: kiểm lại `GetFinalPath` ngay trước bước đánh dấu xoá/đổi tên.
- Ghi ngày: 2026-11-25.

## [Thấp] Join kênh bằng id phiên gửi rõ

Kết nối phụ gửi rõ `id phiên (16 byte đầu của handshake hash) | id kênh`; Agent gọi `AcceptChannel` ngay nên kẻ
nghe lén trong LAN có thể gửi trước id kênh kế tiếp để "đốt" nó: kết nối thật bị từ chối và Broker phải bắt tay
lại (chỉ là từ chối dịch vụ, không lộ dữ liệu). Nên chỉ ghi nhận id kênh sau khi khung đầu tiên giải mã được, hoặc
thêm MAC theo khoá phiên vào yêu cầu join. Ghi 2026-11-25.

## [Thấp] Phiên mã hoá theo đường relay (M6)

`SecureConnectionFactory`/`SecureServer` làm việc trên `IStreamConnector`/`Stream`, mới có `TcpStreamConnector`. Relay
cần một connector WebSocket và phía Agent gọi `SecureServer.AcceptAsync` cho từng tunnel. Ghi 2026-11-25.
