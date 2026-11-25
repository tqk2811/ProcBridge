# Đóng gói và phát hành

## Các gói

`src/scripts/Package.ps1` build native (Hook, Spawn cho x64 và x86) rồi `dotnet publish` ra `src/artifacts/<version>/`,
mỗi gói một thư mục và một file zip:

| Gói | Nội dung | Máy |
|---|---|---|
| `ProcBridge-Client-<version>-win.zip` | `ProcBridge.Launcher.exe` (CLI), `ProcBridge.Launcher.Wpf.exe`, `ProcBridge.Spawn.x64.exe`, `ProcBridge.Spawn.x86.exe`, `ProcBridge.Hook.x64.dll`, `ProcBridge.Hook.x86.dll` cùng một thư mục | A |
| `ProcBridge-Agent-<version>-win.zip` | `ProcBridge.Agent.exe` (CLI), `ProcBridge.Agent.Wpf.exe` | B |
| `ProcBridge-Relay-<version>.zip` | `ProcBridge.Relay.Api.dll` (chạy `dotnet ProcBridge.Relay.Api.dll`) | máy chủ relay |

Launcher (CLI và WPF) tìm `ProcBridge.Spawn.<arch>.exe` trong thư mục của nó, Spawn tìm `ProcBridge.Hook.<arch>.dll`
cạnh nó, nên gói Client chạy được ngay sau khi giải nén.

## Framework-dependent thay vì self-contained

Các gói cần cài .NET 10 runtime (Client/Agent: .NET Desktop Runtime; Relay: ASP.NET Core Runtime). Lý do chọn:

- Gói nhỏ (~8 MB mỗi gói thay vì ~70 MB khi self-contained, gói Client và Agent còn phải mang cả WPF).
- Bản vá bảo mật của runtime đến qua Windows Update / cập nhật runtime, không phải phát hành lại ProcBridge.
- Gói Relay để dạng portable (không gắn RID): cùng một zip chạy trên Windows, Linux hay trong container
  `mcr.microsoft.com/dotnet/aspnet`.
- Hook và Spawn là native, không phụ thuộc runtime .NET nên không ảnh hưởng tới lựa chọn này.

## Version

- Build phát hành: `Package.ps1 -Version M.N.P`. Không truyền thì lấy từ tag `vM.N.P` gần nhất
  (`git describe`): đúng commit có tag ra `M.N.P`, sau tag ra `M.N.P-dev.<số commit>`, chưa có tag ra `0.0.0-dev.<số commit>`.
- Version được đưa vào mọi assembly C# (`-p:Version`), CI so `ProductVersion` của `ProcBridge.Launcher.exe` với tên tag.

## Smoke test

`src/scripts/Test-Package.ps1 [-PackageRoot src/artifacts/<version>]` chỉ chạy file trong các thư mục gói:

1. `ProcBridge.Agent.exe` của gói Agent phục vụ một chia sẻ tạm trên `127.0.0.1` (`--plaintext`, chỉ loopback).
2. `ProcBridge.Launcher.exe` của gói Client chạy `cmd.exe /c type <thư mục ảo>\hello.txt` qua Spawn và Hook, với
   `cmd.exe` x64 rồi x86 (`SysWOW64`); output phải chứa nội dung file trên chia sẻ.
3. Gói Relay khởi động và trả `ok` ở `/health/live`.

## Phát hành

1. Người phát hành tự chọn số và gắn tag trên commit muốn phát hành (thường là đầu `master`):
   `git tag v1.2.3` rồi `git push origin v1.2.3`.
2. `.github/workflows/release.yml` chạy: gọi `build.yml` (build + toàn bộ test), rồi đóng gói với version lấy từ tag,
   kiểm version, chạy smoke test, tạo GitHub Release `v1.2.3` (ghi chú tự sinh của GitHub) và tải ba file zip lên
   (`--clobber`, chạy lại không lỗi).
3. Chạy lại một bản: Actions → release → Run workflow, chọn ref là tag. Chạy trên nhánh là chạy thử: gói được build,
   smoke test và giữ ở artifact `packages` của lần chạy, không đụng tới GitHub Release.
