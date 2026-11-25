# Thuật ngữ ProcBridge

## Hook

Chặn một hàm hệ thống: lời gọi tới hàm đó chạy vào code của mình trước, mình quyết định xử lý thay
hay gọi tiếp hàm gốc. ProcBridge hook các hàm `Nt*` trong `ntdll.dll` bằng MinHook.

## Inject (DLL injection)

Nạp một DLL vào process khác. ProcBridge tạo process ở trạng thái suspended, nạp `Hook.dll`, rồi mới
cho process chạy, để hook có hiệu lực trước dòng code đầu tiên của process.

## ntdll và hàm Nt*

`ntdll.dll` là tầng thấp nhất ở user mode, nằm ngay trên kernel. Mọi API file của Win32, CRT, .NET
cuối cùng đều gọi các hàm `Nt*` ở đây (`NtCreateFile`, `NtReadFile`…), nên hook ở tầng này bắt được
mọi đường đi.

## Job Object

Đối tượng của Windows gom nhiều process thành một nhóm: biết khi nào cả nhóm thoát, và có thể tắt cả
nhóm cùng lúc. ProcBridge dùng nó để quản lý cả cây process của một phiên.

## WOW64

Lớp cho phép process 32-bit (x86) chạy trên Windows 64-bit. Process x86 cần `Hook.dll` bản x86.

## Broker

Process .NET chạy trên máy A cho mỗi phiên: giữ kết nối tới máy B, mã hoá, quản lý cache, trả lời
`Hook.dll` qua named pipe.

## Agent

Chương trình chạy trên máy B, phục vụ thư mục thật cho Broker.

## Relay

Server ASP.NET trên internet, tuỳ chọn (LAN hoặc port public thì nối thẳng). Máy A và máy B cùng kết nối ra Relay
để khỏi phải mở port; Relay không có tài khoản, không lưu gì, chỉ ghép lane và chuyển tiếp dữ liệu đã mã hoá.

## Mã hoá đầu cuối (E2E)

Dữ liệu được mã hoá ở máy A và chỉ máy B giải được (và ngược lại). Server trung gian chuyển tiếp nhưng
không đọc được nội dung.

## Noise (Noise Protocol Framework)

Bộ khuôn giao thức bắt tay để hai bên thống nhất khoá phiên từ cặp khoá của mỗi bên. Được dùng trong
WireGuard, WhatsApp.

## Ghi ngược (write-back) và ghi xuyên (write-through)

Ghi ngược: ghi vào cache cục bộ trước, gửi sang máy B sau (khi đóng file hoặc flush). Ghi xuyên: mỗi
lần ghi đều gửi ngay sang máy B.

## Test so sánh (differential testing)

Chạy cùng một kịch bản trên hai môi trường (thư mục thật và thư mục ảo) rồi so kết quả; khác nhau là lỗi.

## Lane (tunnel relay)

Một kết nối WebSocket dữ liệu trong một phiên tunnel qua Relay. Mỗi phiên có nhiều lane song song; lane cùng
số của máy A và máy B được Relay ghép cặp và chép nguyên khung giữa hai đầu.

## HostId (relay)

Tên máy B trên Relay: vân tay khoá định danh Noise của nó. Máy B chứng minh sở hữu khoá bằng một bắt tay Noise
ngắn với Relay trên kênh điều khiển, nên không ai đăng ký được HostId của máy khác; máy A mở phiên theo HostId lấy
từ máy đã ghép cặp.

## Image và loader

*Image* là file PE (exe, DLL) được ánh xạ vào bộ nhớ để chạy. *Loader* là phần của `ntdll` nạp image vào process:
với exe, kernel mở và ánh xạ file ngay trong `NtCreateUserProcess`, rồi loader của process mới nạp các DLL mà exe
import (tìm trước tiên trong thư mục của exe) và ghi tên mỗi module vào danh sách module của PEB, nơi
`GetModuleFileName` đọc ra.
