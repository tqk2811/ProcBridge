# Backlog

Việc còn treo và việc nên làm của ProcBridge. Mỗi mục: vấn đề, vị trí code, vì sao, ngày ghi.

### [Vừa] Explorer: chưa nối transport và chưa có hồ sơ quyền lưu trên máy B

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/ExplorerSession.cs:12`.
- Vấn đề: DTO đã đánh dấu MessagePack nhưng chưa có dispatcher `Fs.*`/`Sys.*`/`Proc.*`/`Reg.*` gắn vào khung giao thức (mục 7), chưa có nơi lưu `ExplorerPolicy` theo từng máy A đã ghép cặp, và audit log mới là interface (chưa ghi ra file/Event Log). Làm cùng M2 (giao thức + Agent).
- Ghi ngày: 2026-11-25.

### [Vừa] Explorer: tải file lên, xuất `.reg`, chạy chương trình trên máy B chưa làm

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/FileExplorerProvider.cs:89`, `RegistryExplorerProvider.cs:53`, `ProcessExplorerProvider.cs:19`.
- Vấn đề: Plan mục 14 có tải lên/ghi file, xuất `.reg`, chạy chương trình; lõi hiện chỉ có xoá/đổi tên/tạo thư mục, sửa khoá/giá trị, kill. Tải file về đang trả `byte[]` trong DTO (`FileReadRequest`), khi nối transport nên chuyển sang khung nhị phân thô theo mục 15.1.
- Ghi ngày: 2026-11-25.

### [Thấp] Explorer: danh sách process thiếu dòng lệnh, user, CPU, module

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/ProcessExplorerProvider.cs:55` (`ToEntry`).
- Vấn đề: Plan mục 14 liệt kê dòng lệnh, user, CPU, module đã nạp; hiện chỉ có PID, PID cha, tên, đường dẫn image, session, RAM, giờ bắt đầu. Cần đọc PEB (`NtQueryInformationProcess` class 60 cho command line), token user, thời gian CPU.
- Ghi ngày: 2026-11-25.

### [Thấp] Explorer: hard link trong gốc trỏ tới file ngoài gốc vẫn đọc được

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/PathScopeGuard.cs` (`Checked`).
- Vấn đề: `GetFinalPathNameByHandle` trả tên đã dùng để mở, nên hard link nằm trong gốc tới một file ngoài gốc qua được kiểm phạm vi. Tạo hard link cần quyền ghi thuộc tính trên file đích nên rủi ro thấp, nhưng nếu Agent chạy quyền cao hơn user tạo link thì vẫn đọc được file user đó không đọc được. Hướng: với file có `NumberOfLinks > 1`, liệt kê mọi tên (`FindFirstFileNameW`) và đòi tất cả nằm trong gốc, hoặc từ chối.
- Ghi ngày: 2026-11-25.

### [Thấp] Explorer: registry chưa chặn symbolic link của registry, chưa hỗ trợ view 32/64-bit

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/RegistryScopeGuard.cs:44`.
- Vấn đề: khoá kiểu `REG_LINK` trong gốc được phép có thể trỏ sang khoá ngoài gốc (hiếm, cần quyền tạo); `RegistryView` đang cố định `Default`, chưa cho chọn WOW6432Node.
- Ghi ngày: 2026-11-25.

### [Thấp] Explorer: thông tin hệ thống còn sơ sài

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/SystemInfoProvider.cs:28`.
- Vấn đề: RAM lấy theo `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` (bị giới hạn nếu có job/container), chưa có dung lượng ổ, user đang đăng nhập phiên console (mới có user chạy Agent). Nên dùng `GlobalMemoryStatusEx` và `WTSEnumerateSessions`.
- Ghi ngày: 2026-11-25.

### [Thấp] Explorer: test chưa phủ start/stop service thật và symlink file

- Vị trí: `src/tests/ProcBridge.Tests/Explorer/ServiceAndSystemInfoTests.cs`, `FileExplorerProviderTests.cs` (`SymbolicLinkFileLeadingOutside_IsOutOfScope`).
- Vấn đề: không đụng service thật nên nhánh Start/Stop/Restart chưa chạy trong test (cần service giả cài tạm, chạy admin); test symlink file bị Inconclusive khi máy không bật Developer Mode/không chạy admin.
- Ghi ngày: 2026-11-25.

### [Thấp] Explorer: handle nguồn của Delete/Rename mở FileShareAll nên process cục bộ có quyền ghi hai nơi có thể dời object ra ngoài sau khi kiểm

- Vị trí: `src/agent/ProcBridge.Agent.Explorer/PathScopeGuard.cs` (`OpenEntryForChange`).
- Vấn đề: handle nguồn của Delete/Rename được mở với `FileShareAll` (cần để xoá/đổi tên khi file đang bị process khác mở). Một process cục bộ có quyền ghi ở cả hai nơi (trong và ngoài gốc) có thể dời object đã qua kiểm phạm vi ra ngoài gốc trước khi thao tác chạy; thao tác vẫn tác động đúng object đã kiểm nhưng object đó nay nằm ngoài gốc. Không mở rộng quyền của máy A (máy A không điều khiển được process cục bộ này); chỉ cần lưu ý nếu Agent chạy quyền cao hơn user cục bộ. Hướng nếu cần: kiểm lại `GetFinalPath` ngay trước bước đánh dấu xoá/đổi tên.
- Ghi ngày: 2026-11-25.

