# ProcBridge — Kế hoạch thiết kế

> Trạng thái: đã xong M0 (bộ khung), M1 (spike hook cục bộ, mục 13.1), M2 (LAN chỉ đọc, mục 13.2) và M4
> (cây process, mục 13.3; ghép với máy B ở mục 13.4). Ngày lập: 2026-11-25.
> Việc còn treo: xem [Backlog-vi.md](Backlog-vi.md).
> Thuật ngữ: xem [Glossary-vi.md](Glossary-vi.md).

## Mục lục

1. [Mục tiêu và phạm vi](#1-mục-tiêu-và-phạm-vi)
2. [Kiến trúc tổng quan](#2-kiến-trúc-tổng-quan)
3. [Cấu trúc thư mục src](#3-cấu-trúc-thư-mục-src)
4. [Tầng hook (Hook.dll)](#4-tầng-hook-hookdll)
5. [Cây process: inject process cha và process con](#5-cây-process-inject-process-cha-và-process-con)
6. [Broker trên máy A](#6-broker-trên-máy-a)
7. [Giao thức A ↔ B](#7-giao-thức-a--b)
8. [Vận chuyển: LAN trực tiếp và Relay ASP](#8-vận-chuyển-lan-trực-tiếp-và-relay-asp)
9. [Mã hoá đầu cuối và ghép cặp](#9-mã-hoá-đầu-cuối-và-ghép-cặp)
10. [Cache, ghi và nhất quán](#10-cache-ghi-và-nhất-quán)
11. [Ghi chú theo từng mục đích sử dụng](#11-ghi-chú-theo-từng-mục-đích-sử-dụng)
12. [Chiến lược test](#12-chiến-lược-test)
13. [Các mốc (milestone)](#13-các-mốc-milestone)
14. [Duyệt tài nguyên máy B (Remote Explorer)](#14-duyệt-tài-nguyên-máy-b-remote-explorer)
15. [Rủi ro và câu hỏi mở](#15-rủi-ro-và-câu-hỏi-mở)

---

## 1. Mục tiêu và phạm vi

ProcBridge cho phép **một process cha và toàn bộ cây process con của nó** chạy trên máy A nhưng làm
việc với một thư mục nằm trên máy B, như thể thư mục đó có sẵn trên máy A. Process khác trên máy A
không thấy gì. Trong cây process, mọi đường dẫn ngoài thư mục ảo vẫn đi thẳng xuống hệ thống thật.

Đã chốt với user:

| Mục | Quyết định |
|---|---|
| Nền tảng | Chỉ Windows (cả máy A và máy B) |
| Phạm vi hook | Một process cha + mọi process con, cháu do nó tạo ra |
| Quyền | Đọc và ghi |
| Mục đích | Cả bốn: build/chạy công cụ trên source ở máy B, chạy app, chạy game, debug |
| Mạng | LAN kết nối trực tiếp; internet đi qua API ASP.NET trung gian (relay) |
| Bảo mật | Mã hoá đầu cuối A ↔ B, relay chỉ chuyển tiếp, không đọc được nội dung |
| Ngôn ngữ | Hook viết C++; launcher, broker, agent, relay viết C# |
| Duyệt máy B | Có chức năng duyệt từ xa: file/thư mục, registry, danh sách process… (mục 14) |

Mở rộng về sau (không nằm trong các mốc đầu): registry, socket (mở port, kết nối ra ngoài qua máy B),
biến môi trường, và các API khác theo cùng khuôn "chặn ở `ntdll` → chuyển sang máy B".

**Không làm** (ít nhất ở giai đoạn này):

- Mount cho toàn hệ thống (đã có SMB, WinFsp+SSHFS làm việc đó).
- Process dùng syscall trực tiếp, bỏ qua `ntdll` (hiếm; ghi nhận là giới hạn).
- Process được tạo bởi dịch vụ ngoài cây (COM out-of-process, `ShellExecute` qua Explorer, WMI,
  Task Scheduler, UWP broker). Các process đó không nằm trong cây nên không được hook.
- Vượt anti-cheat hoặc cơ chế chống inject của game. Game chặn inject thì ProcBridge không chạy được
  với game đó.

---

## 2. Kiến trúc tổng quan

```mermaid
flowchart LR
    subgraph A["Máy A"]
        L["Launcher"]
        subgraph TREE["Cây process"]
            T["Process đích + Hook.dll"]
            C["Process con + Hook.dll"]
        end
        BR["Broker"]
        CA[("Cache cục bộ")]
    end
    subgraph R["Internet"]
        RL["Relay ASP.NET"]
    end
    subgraph B["Máy B"]
        AG["Agent"]
        FS[("Thư mục thật")]
    end
    L -->|"tạo + inject"| T
    T -->|"tạo + inject"| C
    TREE -->|"named pipe"| BR
    BR --- CA
    BR -->|"LAN: TCP"| AG
    BR -->|"WebSocket"| RL
    RL -->|"WebSocket"| AG
    AG --- FS
```

Vai trò từng thành phần:

| Thành phần | Chạy ở | Việc |
|---|---|---|
| `Launcher` | Máy A | Đọc cấu hình phiên (thư mục ảo ↔ máy B), khởi động Broker, tạo process cha ở trạng thái suspended, inject `Hook.dll`, gán Job Object |
| `Hook.dll` (x64, x86) | Trong mọi process của cây | Chặn các hàm `Nt*` ở `ntdll`, nhận diện đường dẫn thuộc thư mục ảo, hỏi Broker; còn lại gọi hàm gốc |
| `Broker` | Máy A, một process mỗi phiên | Giữ kết nối tới máy B, mã hoá, quản lý cache, trả lời Hook qua named pipe |
| `Agent` | Máy B | Phục vụ thư mục thật theo danh sách chia sẻ, kiểm quyền, báo thay đổi |
| `Relay` | Server internet | Xác thực tài khoản/máy, ghép cặp A ↔ B, chuyển tiếp khung đã mã hoá |

Lý do tách `Broker` khỏi `Hook.dll`: giữ phần code chạy trong process đích nhỏ, không có mạng, không có
crypto, không có .NET. Lỗi mạng hay crash Broker không kéo sập process đích; Hook chỉ trả mã lỗi
`STATUS_*` phù hợp. Đánh đổi là thêm một chặng IPC cục bộ, nhỏ so với độ trễ mạng.

---

## 3. Cấu trúc thư mục src

Theo quy tắc thư mục `src/`: gốc repo chỉ giữ `docs/`, `README.md`, file cấu hình repo và CI.
Phần Relay theo kiến trúc ASP chuẩn (tách đủ `Database.Entities` / `Database.<Provider>` /
`Repositories.Abstractions` / `Repositories`).

```text
src/
├── ProcBridge.slnx                       # phần C#
├── ProcBridge.Native.sln                 # phần C++ (Hook, test native) — vcxproj chưa vào được slnx ổn định
├── Directory.Build.props / .targets / Directory.Packages.props / ProjectBuildProperties.targets
├── native/
│   ├── ProcBridge.Hook/                  # Hook.dll (C++, MinHook), build x64 + Win32
│   ├── ProcBridge.Inject/                # thư viện inject dùng chung (C++), kể cả inject khác kiến trúc
│   ├── ProcBridge.Spawn/                 # launcher native: tạo process suspended + inject (M1); helper inject cùng kiến trúc (mục 5)
│   └── tests/ProcBridge.NativeProbe/     # exe gọi đủ bộ API file để test so sánh (mục 12)
├── shared/
│   ├── ProcBridge.Protocol/              # định nghĩa message, framing, mã lỗi (C#)
│   ├── ProcBridge.Crypto/                # bắt tay, khoá phiên, mã hoá khung
│   └── ProcBridge.HookIpc/               # định dạng IPC Hook ↔ Broker (C# phía Broker; header C++ sinh từ đây)
├── client/
│   ├── ProcBridge.Launcher/              # CLI + về sau là UI
│   └── ProcBridge.Broker/
├── agent/
│   └── ProcBridge.Agent/                 # chạy dạng Windows Service hoặc console
├── relay/
│   ├── ProcBridge.Relay.Shared/
│   ├── ProcBridge.Relay.ViewModels/
│   ├── ProcBridge.Relay.Database.Entities/
│   ├── ProcBridge.Relay.Database.PostgreSql/
│   ├── ProcBridge.Relay.Repositories.Abstractions/
│   ├── ProcBridge.Relay.Repositories/
│   ├── ProcBridge.Relay.Services.*/      # Accounts, Devices, Shares, Tunnel
│   ├── ProcBridge.Relay.Api/             # REST quản lý + endpoint WebSocket /tunnel
│   └── tests/
└── tests/
    └── ProcBridge.Tests/                 # test C# (protocol, crypto, broker, agent, end-to-end)
```

---

## 4. Tầng hook (Hook.dll)

### 4.1 Hook ở tầng nào

Hook ở **`ntdll`** (hàm `Nt*`), dùng MinHook (giấy phép BSD). Mọi API phía trên đều đi qua đây: Win32
(`CreateFileW`, `FindFirstFileW`), CRT, .NET (`FileStream`, `Directory.EnumerateFiles`), Java, Python.
Hook ở tầng Win32 thì hở nhiều đường đi vòng nên không chọn.

### 4.2 Danh sách hàm cần chặn (file)

| Nhóm | Hàm | Ghi chú |
|---|---|---|
| Mở/tạo | `NtCreateFile`, `NtOpenFile` | Điểm vào chính. Quyết định thật/ảo tại đây |
| Thuộc tính theo đường dẫn | `NtQueryAttributesFile`, `NtQueryFullAttributesFile` | `GetFileAttributes`, `PathFileExists` đi qua đây, không mở handle |
| Liệt kê thư mục | `NtQueryDirectoryFile`, `NtQueryDirectoryFileEx` | Trả danh sách từ máy B theo đúng `FILE_INFORMATION_CLASS` được hỏi |
| Thông tin theo handle | `NtQueryInformationFile`, `NtSetInformationFile` | Đổi tên, xoá (`FileDispositionInformation[Ex]`), đổi kích thước, thời gian, tên file |
| Đọc/ghi | `NtReadFile`, `NtWriteFile`, `NtFlushBuffersFile` | Với mô hình handle cache (4.4) phần lớn không cần chặn; chặn để đánh dấu dirty và cho tải theo vùng về sau |
| Đóng | `NtClose` | Kích hoạt ghi ngược khi handle dirty, dọn bảng handle |
| Theo dõi thay đổi | `NtNotifyChangeDirectoryFile`, `NtNotifyChangeDirectoryFileEx` | Mốc M7 |
| Tên handle | `NtQueryObject` (`ObjectNameInformation`), `NtQueryInformationFile` (`FileNameInformation`, `FileNormalizedNameInformation`) | Để `GetFinalPathNameByHandle` trả đường dẫn ảo chứ không lộ đường dẫn cache |
| Volume | `NtQueryVolumeInformationFile` | Tên ổ, serial, hệ thống file, dung lượng trống của thư mục ảo |
| Khoá | `NtLockFile`, `NtUnlockFile` | Chuyển sang máy B ở mốc sau; ban đầu khoá cục bộ trên file cache |
| Tạo process | `NtCreateUserProcess` | Mục 5 |

### 4.3 Nhận diện đường dẫn ảo

Thư mục ảo được khai báo dạng: *đường dẫn trên máy A* → *(máy B, tên chia sẻ, thư mục con)*. Ví dụ
`D:\Remote\MyProject` → `B:share/src/MyProject`. Đường dẫn trên máy A **không cần tồn tại**; có thể
dùng cả ký tự ổ giả như `V:\`.

Đường dẫn tới hook dưới nhiều dạng, phải đưa về một dạng chuẩn trước khi so:

- NT path `\??\D:\...`, `\??\UNC\...`, `\Device\HarddiskVolumeN\...`.
- Đường dẫn tương đối theo `RootDirectory` trong `OBJECT_ATTRIBUTES` (handle thư mục, có thể là handle
  ảo do chính Hook cấp).
- Thư mục hiện hành của process (`SetCurrentDirectory` vào thư mục ảo phải chạy được: handle thư mục
  hiện hành là handle ảo).
- Tên ngắn 8.3, `..`, `.`, dấu `\` thừa, `\\?\` prefix, phân biệt hoa thường (so không phân biệt).
- Symlink, junction bên trong thư mục ảo: Agent trên máy B tự giải và chỉ cho phép trỏ trong phạm vi
  chia sẻ.

### 4.4 Mô hình handle: handle cache cục bộ

Đây là quyết định thiết kế quan trọng nhất của tầng hook.

- Khi process mở một file ảo, Hook hỏi Broker. Broker bảo đảm có **bản sao trong cache cục bộ**
  (`%LOCALAPPDATA%\ProcBridge\cache\<phiên>\...`), tải từ máy B nếu chưa có hoặc đã cũ.
- Hook gọi `NtCreateFile` **thật** trên file cache, trả handle thật đó cho process và ghi vào bảng
  `handle → đường dẫn ảo`.
- Nhờ vậy `NtReadFile`, `NtWriteFile`, memory-mapped file (`NtCreateSection`), overlapped I/O, IOCP
  đều chạy bằng cơ chế thật của Windows, không phải tự giả lập.
- Thứ bị lộ ra là **metadata và tên**: Hook sửa kết quả các hàm hỏi tên, thời gian, file ID, volume
  để process chỉ thấy thông tin của máy B và đường dẫn ảo. Thời gian và thuộc tính của file cache
  được đặt trùng với file gốc trên máy B ngay khi tải về.
- Thư mục ảo: handle là handle thật của một **thư mục giữ chỗ** trong cache. Kết quả liệt kê
  (`NtQueryDirectoryFile`) do Hook dựng từ danh sách của máy B, không đọc thư mục giữ chỗ.

Giới hạn: file rất lớn phải tải trọn mới mở được (M2). Mốc sau thêm tải theo vùng: file cache là
sparse file, Hook chặn `NtReadFile` trên vùng chưa có để kéo về trước khi đọc. Memory-mapped file trên
vùng chưa tải vẫn là điểm khó (ghi ở mục 14).

```mermaid
sequenceDiagram
    participant P as Process + Hook.dll
    participant BR as Broker
    participant AG as Agent (máy B)
    Note over P: NtCreateFile("D:\Remote\MyProject\a.cs")
    P->>BR: Open(path ảo, quyền, disposition)
    BR->>AG: Stat + version
    AG-->>BR: metadata, version
    alt cache cũ hoặc chưa có
        BR->>AG: Read(nội dung)
        AG-->>BR: các khối dữ liệu
        Note over BR: ghi file cache, đặt thời gian và thuộc tính
    end
    BR-->>P: đường dẫn cache
    Note over P: NtCreateFile thật trên file cache, lưu handle vào bảng
    Note over P: NtReadFile / NtWriteFile chạy thẳng trên file cache
    P->>BR: NtClose: handle dirty thì gửi ghi ngược
    BR->>AG: Write(các khối thay đổi, version cũ)
    AG-->>BR: version mới hoặc báo xung đột
```

### 4.5 Ghi, tạo, đổi tên, xoá

- Tạo file mới (`FILE_CREATE`, `FILE_OVERWRITE_IF`…): Broker gửi lệnh tạo sang máy B trước (để báo
  đúng `STATUS_OBJECT_NAME_COLLISION`), rồi tạo file cache.
- Đổi tên, xoá, đổi thuộc tính: gửi đồng bộ sang máy B, thành công mới cập nhật cache. Trả mã lỗi
  của máy B cho process.
- Ghi nội dung: ghi vào file cache, đánh dấu dirty, gửi ngược khi `NtClose`, `NtFlushBuffersFile`,
  hoặc theo chu kỳ. Chi tiết nhất quán ở mục 10.

### 4.6 Quy tắc an toàn trong Hook

- Không cấp phát bằng CRT trong đường nóng nếu tránh được; không gọi API có thể quay lại chính hàm
  đang hook (cờ chống đệ quy theo thread).
- Không gọi `LoadLibrary` trong `DllMain`; khởi động hook ở một thread riêng hoặc qua APC trước khi
  process chạy entry point.
- Broker không trả lời trong thời gian giới hạn → trả `STATUS_IO_TIMEOUT` hoặc `STATUS_NETWORK_UNREACHABLE`,
  không treo vô hạn.
- Đường dẫn không thuộc thư mục ảo phải đi đường nhanh: một phép so tiền tố rồi gọi hàm gốc.

---

## 5. Cây process: inject process cha và process con

1. Launcher tạo process cha với `CREATE_SUSPENDED`, inject `Hook.dll` (thread từ xa gọi
   `LoadLibraryW`, hoặc APC vào thread chính), chờ Hook báo sẵn sàng qua pipe rồi `ResumeThread`.
2. `Hook.dll` chặn `NtCreateUserProcess`. Mỗi lần có process con: thêm cờ suspended vào tham số, gọi
   hàm gốc, inject vào con, rồi mới cho con chạy (nếu bên gọi vốn không yêu cầu suspended).
3. Cả cây được gán vào **một Job Object** (`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`). Launcher dùng Job để
   biết cả cây đã thoát, khi đó đóng phiên, ghi ngược nốt dữ liệu, dọn cache theo cấu hình.
4. Khác kiến trúc: cha x64 sinh con x86 (WOW64) hoặc ngược lại. Hook nhờ Broker hoặc một helper đúng
   kiến trúc làm việc inject. Build cả `ProcBridge.Hook.x64.dll` và `ProcBridge.Hook.x86.dll`.
5. Process con kế thừa handle ảo từ cha (handle thật của file cache): bảng `handle → đường dẫn ảo`
   phải được chuyển cho con. Cách làm: cha chép các mục của handle ảo kế thừa vào khối khởi tạo của con
   (với Broker kèm metadata máy B và đường dẫn ảo), sau giao diện `IHandleInheritance` (mục 13.3, 13.4);
   không cần bảng chung của phiên trong Broker.

---

## 6. Broker trên máy A

- Một process .NET cho mỗi phiên, Launcher khởi động trước khi tạo process cha.
- Kênh với Hook: named pipe `\\.\pipe\ProcBridge-<phiên>`, ACL chỉ cho user hiện tại. Mỗi thread
  của process đích có thể gửi song song; dùng pipe chế độ message, nhiều instance.
- Quản lý: cache metadata (stat, danh sách thư mục) có TTL, cache nội dung file theo version, bảng
  handle của cả phiên, hàng đợi ghi ngược.
- Định dạng IPC Hook ↔ Broker là nhị phân cố định (struct có độ dài), không phải JSON, vì nằm trên
  đường nóng của mọi lần mở file ảo.

---

## 7. Giao thức A ↔ B

- **Một giao thức duy nhất** cho mọi đường vận chuyển (TCP LAN hay WebSocket qua relay). Relay không
  hiểu nội dung giao thức (mục 9).
- Khung (frame): `[độ dài][stream id][loại][payload]`, sau lớp mã hoá. Nhiều request chạy song song
  trên cùng một kết nối, mỗi request một `stream id`.
- Truyền file lớn: chia khối (vd 256 KB), có điều khiển luồng (cửa sổ tín dụng) để một file lớn không
  chặn các request metadata nhỏ.
- Tuần tự hoá: MessagePack hoặc protobuf (chốt ở M2). Có số phiên bản giao thức trong bắt tay.
- Nhóm message theo "miền" để sau này thêm registry, socket mà không đổi khung: `Fs.*` (Stat, List,
  Open, Read, Write, Rename, Delete, SetInfo, Watch), `Sys.*`, `Proc.*`, `Reg.*` cho chức năng duyệt
  (mục 14), về sau `Net.*`.
- Mã lỗi: chuyển nguyên `NTSTATUS` từ máy B về, để process đích nhận đúng lỗi như khi chạy cục bộ.

---

## 8. Vận chuyển: LAN trực tiếp và Relay ASP

### 8.1 LAN

Broker kết nối TCP thẳng tới Agent (cổng cấu hình được). Tìm Agent trong LAN bằng nhập địa chỉ tay
(M2), về sau thêm dò mDNS hoặc UDP broadcast.

### 8.2 Internet qua Relay ASP.NET

- Cả Broker và Agent đều **chủ động kết nối ra** Relay bằng WebSocket (TLS). Không cần mở port, không
  cần xuyên NAT ở hai máy.
- Relay: đăng nhập tài khoản, đăng ký máy (device), khai chia sẻ, cấp quyền máy A dùng chia sẻ nào
  của máy B, cấp token ngắn hạn cho kết nối tunnel, ghép cặp hai đầu và chuyển tiếp khung.
- Relay chỉ nhìn thấy phần đầu định tuyến (id phiên, id đích); payload đã mã hoá đầu cuối.
- Lưu trữ: PostgreSQL (tài khoản, máy, khoá công khai của máy, chia sẻ, quyền, log phiên). Không lưu
  nội dung file.
- Về sau có thể thử kết nối thẳng P2P (UDP hole punching) và chỉ dùng relay khi thất bại; không nằm
  trong các mốc đầu.

---

## 9. Mã hoá đầu cuối và ghép cặp

- Mỗi máy (Broker phía A, Agent phía B) có **cặp khoá định danh lâu dài** (Ed25519 hoặc X25519), sinh
  khi cài, lưu bằng DPAPI.
- **Ghép cặp** lần đầu: máy B hiển thị mã ngắn hoặc QR, người dùng nhập ở máy A (LAN), hoặc duyệt
  trên Relay (internet). Kết quả: mỗi bên lưu khoá công khai của bên kia. Relay chỉ chuyển khoá công
  khai, nhưng người dùng xác nhận vân tay khoá để chống Relay tráo khoá.
- **Mỗi phiên**: bắt tay theo mẫu Noise IK (hoặc XX) trên nền X25519, ra khoá phiên, mã hoá khung bằng
  ChaCha20-Poly1305 hoặc AES-GCM, có số thứ tự chống phát lại.
- Lớp mã hoá này giống nhau ở LAN và internet, nên Relay chỉ là một đường ống. Ở LAN không cần TLS
  riêng.

```mermaid
sequenceDiagram
    participant BR as Broker (A)
    participant RL as Relay
    participant AG as Agent (B)
    AG->>RL: WebSocket + token máy B
    BR->>RL: WebSocket + token máy A, xin mở phiên tới chia sẻ X
    Note over RL: kiểm quyền A dùng X
    RL->>AG: có phiên mới từ A
    BR->>AG: bắt tay Noise, khung đi qua Relay
    AG-->>BR: bắt tay xong, có khoá phiên
    BR->>AG: Fs.* đã mã hoá
    AG-->>BR: kết quả đã mã hoá
```

---

## 10. Cache, ghi và nhất quán

- Mỗi file trên máy B có **version** (tổ hợp thời gian sửa + kích thước + file ID, hoặc số đếm do Agent
  giữ). Broker so version trước khi dùng cache.
- **Đọc**: metadata có TTL ngắn (cấu hình được), nội dung dùng lại nếu version khớp.
- **Ghi**: mặc định ghi ngược khi đóng file hoặc flush. Có chế độ ghi xuyên (write-through) cho dữ liệu
  quan trọng, đổi bằng cấu hình theo thư mục.
- **Xung đột** (file trên máy B đã đổi kể từ lúc tải về): mặc định không ghi đè; giữ bản của A thành
  file `*.procbridge-conflict-<thời gian>` trên máy B và báo trong log, Launcher hiển thị cảnh báo.
  Có tuỳ chọn "bên A thắng".
- **Theo dõi thay đổi** (M7): Agent dùng `ReadDirectoryChangesW` trên máy B và đẩy sự kiện về Broker để
  làm mất hiệu lực cache, đồng thời phục vụ `NtNotifyChangeDirectoryFile` của process đích (IDE, công
  cụ build theo dõi file).
- Mất kết nối giữa phiên: dữ liệu dirty được giữ trong cache và hàng đợi, kết nối lại thì gửi tiếp.
  Đọc file chưa có trong cache thì trả lỗi mạng.

---

## 11. Ghi chú theo từng mục đích sử dụng

| Mục đích | Điểm cần chú ý |
|---|---|
| Build/chạy công cụ trên source máy B | Rất nhiều lần stat, liệt kê thư mục, đọc file nhỏ: tối ưu metadata (lấy cả cây con một lần, đọc trước file nhỏ). Ghi ra `bin/obj`: cân nhắc cấu hình **thư mục loại trừ** để đầu ra build nằm cục bộ trên máy A |
| Chạy app | Cần `GetFinalPathNameByHandle`, thư mục hiện hành, memory-mapped file, khoá file đúng |
| Chạy game | File asset rất lớn: cần tải theo vùng; anti-cheat có thể chặn inject (không xử lý) |
| Debug | Debugger (vd Visual Studio) là process cha thì process được debug là con và cũng được hook. File pdb, source đọc qua thư mục ảo. Cần test riêng với VS và VS Code |

---

## 12. Chiến lược test

- **Test so sánh (differential)**: `ProcBridge.NativeProbe` chạy cùng một kịch bản API (mở, đọc, ghi,
  liệt kê với mọi `FILE_INFORMATION_CLASS`, đổi tên, xoá, khoá, memory map) hai lần: trên thư mục thật
  và trên thư mục ảo trỏ tới đúng thư mục đó. Kết quả và mã lỗi phải giống nhau. Đây là xương sống
  của bộ test hook.
- Test theo runtime: cùng kịch bản viết bằng .NET, Python, `cmd`/PowerShell, để chắc mọi đường đi đều
  qua hook.
- Test cây process: `cmd /c` gọi tiếp `cmd /c` nhiều tầng, con x86 từ cha x64 và ngược lại.
- Test mạng: Agent và Broker trên cùng máy (localhost) trong CI; thêm giả lập trễ và mất gói.
- Test thực tế theo mục đích: `dotnet build` và `msbuild` một solution nằm trên máy B, mở bằng VS Code,
  chạy một app WPF, debug một console app.

---

## 13. Các mốc (milestone)

Mỗi mốc là một nhánh riêng, xong mốc nào build, test và commit mốc đó.

| Mốc | Nội dung | Điều kiện xong |
|---|---|---|
| **M0** Bộ khung | Cấu trúc `src/`, solution C# và C++, build x64 + x86, CI build | CI xanh, `Hook.dll` hai kiến trúc ra được |
| **M1** Spike hook cục bộ | Hook `ntdll` trong một process, chuyển hướng thư mục ảo sang một thư mục **cục bộ khác** (chưa có mạng). Kèm `NativeProbe` và test so sánh | `cmd /c type`, `dir`, .NET `File.ReadAllText`, Notepad mở/lưu được file qua thư mục ảo; test so sánh xanh với bộ API đọc |
| **M2** LAN chỉ đọc | Broker, Agent, giao thức, TCP; stat, liệt kê, tải trọn file vào cache | Mốc M1 chạy lại được với thư mục nằm ở máy B qua LAN |
| **M3** Ghi | Tạo, ghi, đổi tên, xoá, đặt thuộc tính, ghi ngược, phát hiện xung đột | Test so sánh xanh với bộ API ghi; sửa và lưu file trong VS Code |
| **M4** Cây process | Hook `NtCreateUserProcess`, inject con, x86/x64, Job Object, chuyển bảng handle | `dotnet build` chạy trên solution ở máy B (nhiều process con) |
| **M5** Mã hoá đầu cuối | Khoá định danh, ghép cặp, bắt tay Noise, mã hoá khung (áp cả LAN) | Bắt gói trên dây không thấy nội dung; ghép cặp sai khoá bị từ chối |
| **M6** Relay ASP | Relay theo kiến trúc ASP chuẩn: tài khoản, máy, chia sẻ, quyền, tunnel WebSocket | Máy A và máy B ở hai mạng khác nhau làm được M4 qua Relay |
| **M7** Theo dõi thay đổi và hiệu năng | `ReadDirectoryChangesW` phía B, `NtNotifyChangeDirectoryFile` phía A, lấy cây metadata một lần, đọc trước | IDE thấy file đổi trên máy B; đo thời gian build so với chạy cục bộ |
| **M8** Hoàn thiện theo mục đích | Tải theo vùng cho file lớn, khoá file chuyển sang máy B, test app/game/debug | Danh sách test thực tế ở mục 12 chạy được |
| **M9** Remote Explorer mở rộng | Danh sách process, registry, service, thông tin hệ thống (mục 14); thao tác ghi (kill process, sửa registry) sau cùng | Duyệt được cả bốn loại qua LAN và qua Relay; quyền bị tắt thì Agent từ chối |
| Sau M9 | Hook registry (`Reg.*` cho process đích), socket (`Net.*`) | Lập kế hoạch riêng |

Phần Remote Explorer **duyệt file/thư mục và ổ đĩa** đi kèm M2 (chỉ đọc) và M3 (thao tác file), vì
người dùng cần nó để chọn thư mục máy B trước khi mở phiên.

### 13.1 Kết quả M1 (2026-11-25)

- `ProcBridge.Hook` chặn `NtCreateFile`, `NtOpenFile`, `NtQueryAttributesFile`,
  `NtQueryFullAttributesFile`, `NtQueryInformationByName`, `NtDeleteFile`, `NtQueryInformationFile`
  (tên: class 9, 18, 48), `NtSetInformationFile` (chỉ dịch đường dẫn đích của đổi tên/tạo link),
  `NtQueryObject` (`ObjectNameInformation`), `NtClose`.
- `RootDirectory` là handle thư mục thật (kernelbase dùng handle thư mục hiện hành cho mọi đường dẫn nằm
  dưới thư mục hiện hành): Hook lấy tên thiết bị của handle đó (cache theo handle), ghép với tên tương đối,
  thuộc gốc ảo thì viết lại thành đường dẫn thật tuyệt đối. Gốc ảo lồng nhau: gốc dài nhất thắng; `:` sau
  gốc ảo (luồng dữ liệu phụ) cũng là ranh giới. Không nhận gốc ổ đĩa hay đường dẫn UNC làm thư mục ảo/thật.
- Con trỏ của process gọi vào được đọc/ghi qua SEH: con trỏ hỏng thì trả `STATUS_ACCESS_VIOLATION` như
  hệ thống, không làm sập process.
- Theo mô hình 4.4 rút gọn: thư mục thật đóng vai "cache", mở đường dẫn ảo là mở file thật, trả handle
  thật và ghi vào bảng `handle → ánh xạ`. Đọc, map bộ nhớ, overlapped I/O và liệt kê thư mục chạy trên
  handle thật nên **không** chặn `NtReadFile`/`NtQueryDirectoryFile` ở M1 (liệt kê chỉ trả tên lá).
  M2 sẽ chặn `NtQueryDirectoryFile[Ex]` để dựng danh sách từ máy B.
- Cấu hình: biến môi trường `PROCBRIDGE_MAP` (`ảo*thật|ảo*thật`), đọc sau giao diện `IPathResolver`;
  M2 thay bằng client Broker cùng giao diện.
- Khởi động: `DllMain` không làm gì nặng; injector gọi export `ProcBridgeHookInitialize` bằng thread từ
  xa sau `LoadLibraryW`, trước khi resume thread chính.
- Launcher M1 là `ProcBridge.Spawn` (native, dùng `ProcBridge.Inject`); Launcher C# dùng nó từ M2.
  Đích khác kiến trúc với Spawn bị từ chối sớm (`ERROR_EXE_MACHINE_TYPE_MISMATCH`, inject chéo kiến trúc
  là M4).
- Kiểm: `cmd /c type`, `dir`, `cd` vào thư mục ảo, .NET Framework `File.ReadAllText` (qua PowerShell 5.1),
  test so sánh `NativeProbe` thật/ảo giống từng dòng, cả x64 và Win32
  (`src/tests/ProcBridge.Tests/Native`). Notepad **chưa kiểm**: Notepad trên Windows 11 là app packaged,
  `notepad.exe` chỉ kích hoạt app nằm ngoài cây process (giới hạn đã nêu ở mục 1).

### 13.2 Kết quả M2 (2026-11-25)

- Hook nói chuyện với Broker qua `\\.\pipe\ProcBridge-<phiên>` (định dạng `ProcBridge/HookIpc.h`): pool instance
  có khoá (mỗi lời gọi lấy một instance rảnh hoặc mở mới), chế độ message, `TransactNamedPipe` overlapped có
  thời hạn (`PROCBRIDGE_IPC_TIMEOUT_MS`, mặc định 60 s; quá hạn trả `STATUS_IO_TIMEOUT`). Mỗi instance mới được
  kiểm `GetNamedPipeServerProcessId` khớp PID Broker (`PROCBRIDGE_BROKER_PID`), mở với
  `SECURITY_IDENTIFICATION`; sai PID thì khởi động hook thất bại. Mã `0xE000000x` còn sót cũng bị đổi sang mã chuẩn.
- `BrokerResolver` (cùng giao diện `IPathResolver` với `PathMapper` của M1): lúc khởi động gửi `Hello`, rồi
  `Resolve` từng gốc ảo để biết thư mục cache tương ứng; so tiền tố và viết lại tên vẫn cục bộ như M1 nhờ cache
  giữ đúng đường dẫn tương đối. Mở file gọi `Resolve` với quyền/disposition/option thật của lời gọi (Broker từ chối
  ghi bằng `STATUS_MEDIA_WRITE_PROTECTED`, `FILE_CREATE` trên tên đã có thì `STATUS_OBJECT_NAME_COLLISION`).
  `NtQueryAttributesFile`/`NtQueryFullAttributesFile` và các class metadata của `NtQueryInformationByName` dùng
  `Stat`, không tải file.
- `RemoteHandleTable`: handle mở qua đường dẫn ảo giữ metadata của máy B; các class Basic, Standard, Internal,
  All, NetworkOpen, AttributeTag, Id, Stat của `NtQueryInformationFile` báo thời gian, thuộc tính, dung lượng cấp
  phát và file ID của máy B thay cho bản cache. Handle thư mục (thư mục giữ chỗ rỗng) được dựng kết quả
  `NtQueryDirectoryFile[Ex]` từ các trang `List` (`.` và `..` thêm ở đầu): class Directory, Full, Both, Names,
  IdBoth, IdFull, IdExtd, IdExtdBoth, Id64Extd(Both), IdAllExtd(Both); pattern (`RtlIsNameInExpression`), restart
  scan, single entry, `STATUS_NO_SUCH_FILE`/`STATUS_NO_MORE_FILES`. Tên tương đối với handle thư mục ảo (thư mục
  hiện hành) được ghép thành đường dẫn ảo đầy đủ rồi `Resolve`.
- Agent đọc file ID, change time và dung lượng cấp phát thật (`GetFileInformationByHandleEx`, một lượt quét
  `FileIdBothDirectoryInfo` cho mỗi `List`); `FsEntry` thêm `AllocationSize`.
- Cấu hình hook: `PROCBRIDGE_SESSION` (+ `PROCBRIDGE_BROKER_PID`) thắng `PROCBRIDGE_MAP`; `ProcBridge.Spawn --broker
  <phiên> <pid>`. Launcher C# (`ProcBridge.Launcher --agent --share --map ... -- <lệnh>`) sinh id phiên bằng
  `HookIpcProtocol.NewSessionId`, host `BrokerHost`, chạy Spawn, dừng Broker khi process đích thoát.
- Kiểm (`src/tests/ProcBridge.Tests/Native/RemoteHookTests.cs`, Agent trên loopback): `NativeProbe` thật và qua
  Agent giống từng dòng (x64 và Win32), `cmd type`, `dir /s`, `cd` vào thư mục ảo, .NET Framework `ReadAllText`,
  ghi bị từ chối, pipe của process khác bị từ chối, Broker im lặng thì lời gọi trả lỗi sau thời hạn. `NativeProbe`
  không in thời gian của mục `..` và mở file kiểm đổi tên bằng một file tạm ngoài gốc (cần quyền `DELETE`, share
  chỉ đọc từ chối). Bộ test M1 (`PROCBRIDGE_MAP`) vẫn xanh.

### 13.3 Kết quả M4 bản cục bộ (2026-11-25)

Làm song song với M2, chưa có Broker: cấu hình là `PROCBRIDGE_MAP` cục bộ. Phần ghép với Broker ở mục 13.4.

- **Inject con**: Hook chặn `NtCreateUserProcess`: thêm cờ tạo thread treo, bỏ cờ thoát Job
  (`PROCESS_CREATE_FLAGS_BREAKAWAY`), ghi con vào danh sách chờ. Không inject ngay trong
  `NtCreateUserProcess`: kernelbase chỉ đăng ký process mới với csrss *sau* khi hàm này trả về, inject lúc
  đó làm loader và kernel32 của con khởi động quá sớm. kernelbase luôn tạo process treo rồi gọi
  `NtResumeThread` khi xong việc đăng ký (hoặc để bên gọi `CREATE_SUSPENDED` tự resume), nên Hook inject ở
  lần `NtResumeThread` đầu tiên trên thread chính của con (hoặc `NtResumeProcess`), chạy initializer xong
  mới cho resume (`NtAlertResumeThread` cũng được chặn). Hai resume cùng lúc trên một con: một thread inject,
  thread kia chờ event của mục đó rồi mới gọi hàm gốc. Con thoát trước lần resume đầu thì mục chờ được dọn
  ngay (wait đăng ký trên handle process). Bên gọi thẳng `NtCreateUserProcess` không xin thread treo thì
  inject và resume ngay.
  Không cần hook `CreateProcessInternalW`. Inject hỏng thì con bị kết thúc (mã `STATUS_DLL_INIT_FAILED`)
  chứ không để chạy ngoài hook.
- **Khác kiến trúc**: `ProcBridge.Inject::InjectHook` cùng kiến trúc thì inject trực tiếp; khác kiến trúc
  thì chạy `ProcBridge.Spawn.<arch>.exe --inject` nằm cạnh `Hook.dll` của kiến trúc đích. Helper không kế
  thừa gì: nó mở process inject theo PID rồi `DuplicateHandle` hai handle (process đích, section chứa khối
  khởi tạo), nên con do thread khác tạo cùng lúc không nhận nhầm hai handle này.
  Đường dẫn `Hook.dll` hai kiến trúc đi xuống cây qua `PROCBRIDGE_HOOK_X64`/`PROCBRIDGE_HOOK_X86`. Spawn
  cũng dùng đường này nên đích khác kiến trúc không còn bị từ chối.
- **Biến môi trường**: injector chép một khối khởi tạo (`ProcBridge/InitBlock.h`) vào con và truyền địa chỉ
  làm tham số của `ProcBridgeHookInitialize`. Khối mang các biến cấu hình (initializer đặt lại vào môi
  trường của con) và các handle ảo kế thừa. Hook chụp cấu hình lúc khởi tạo, nên process xoá biến hoặc
  truyền environment block riêng thì con vẫn được hook.
- **Job Object**: Spawn tạo Job `KILL_ON_JOB_CLOSE`, gán process đầu trước khi inject; con cháu tự vào Job
  (Hook bỏ cờ breakaway). Spawn chờ Job hết process (`JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO`) rồi trả mã thoát
  của process đầu. Spawn chết thì Job đóng và cả cây bị kết thúc.
- **Handle kế thừa**: chọn chuyển bảng qua khối khởi tạo thay vì để con tự hỏi `NtQueryObject` khi gặp
  handle lạ. Lý do: tên thật của handle không cho biết handle đó được mở qua đường ảo hay đường thật (process
  được phép mở thẳng thư mục thật), còn cha biết chắc. Cha lọc các handle ảo có cờ `HANDLE_FLAG_INHERIT`
  khi tạo con với `bInheritHandles`; con nạp vào bảng trước khi bật hook. Danh sách được chụp ngay trong
  `NtCreateUserProcess` (đóng hoặc dùng lại handle trước lần resume đầu không đổi thứ con đang giữ), thu hẹp
  theo `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` (`PS_ATTRIBUTE_HANDLE_LIST` 0x2000B), và rỗng khi có
  `PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` trỏ process khác (con kế thừa handle của process đó). Không cần shared memory theo
  phiên. Giao diện `IHandleInheritance` (bản Broker ở mục 13.4). Kèm theo: hook `NtDuplicateObject` để bản
  sao handle trong cùng process (vd `cmd` nhân bản handle khi chuyển hướng `<`) vẫn là handle ảo.
- Kiểm (`src/tests/ProcBridge.Tests/Native/ProcessTreeTests.cs`, `SpawnTests.cs`): `cmd /c cmd /c cmd /c type`
  qua thư mục ảo, con x86 từ cha x64 và ngược lại (cả trực tiếp từ Spawn), con tạo sau khi cha xoá
  `PROCBRIDGE_MAP`, handle stdin kế thừa giữ tên ảo, Spawn chờ cháu chạy nền, giết Spawn thì cháu chết,
  `dotnet build` hai project trong thư mục ảo với `-m:2 -nodeReuse:false --disable-build-servers
  -p:UseSharedCompilation=false` (dotnet, node MSBuild, csc là process riêng) rồi chạy được app vừa build.
  `ChildCreationTests.cs`: handle kế thừa bị đóng trước resume, danh sách handle / process cha khác, resume
  bằng `NtAlertResumeThread`, hai thread resume cùng lúc, con chết trước resume không giữ handle, helper khác
  kiến trúc không rò handle sang con anh em.

### 13.4 Ghép M2 + M4: cây process với máy B (2026-11-25)

- **Cấu hình xuống cây**: khối khởi tạo mang cả `PROCBRIDGE_SESSION`, `PROCBRIDGE_BROKER_PID`,
  `PROCBRIDGE_IPC_TIMEOUT_MS` cạnh `PROCBRIDGE_MAP` và đường dẫn `Hook.dll`. Kênh không dùng đi với giá trị rỗng
  và initializer **xoá** biến có giá trị rỗng, nên giá trị thừa kế từ nơi khác không chiếm được kênh. Mỗi process
  con tự kết nối pipe Broker (kiểm PID chủ pipe như process đầu) và gửi `Hello` riêng.
- **Handle kế thừa từ xa**: không cần bảng chung của phiên trong Broker (khác ý ban đầu ở mục 5). Khối khởi tạo bản
  2 thêm vùng `extra`; mỗi handle ảo kế thừa có `ExtraOffset` trỏ tới bản ghi của Hook: `FileInfo` của máy B và
  đường dẫn NT ảo lấy từ `RemoteHandleTable`. Con nạp vào cả `HandleTable` và `RemoteHandleTable` trước khi bật
  hook, nên metadata (file ID, thời gian, thuộc tính) vẫn là của máy B mà không hỏi lại Broker. Chỉ số ánh xạ
  giống nhau giữa cha và con vì cùng Broker trả cùng danh sách gốc trong `Hello`. Handle thư mục sang con bắt đầu
  một lượt liệt kê mới (con trỏ liệt kê của cha không chuyển theo, xem Backlog). `NtDuplicateObject` trong cùng
  process cũng chép mục `RemoteHandleTable`.
- **Launcher**: Spawn chỉ thoát khi Job hết process, nên Launcher chờ Spawn là chờ cả cây; Broker chỉ dừng sau
  đó. `--no-build-servers` (`LauncherOptions.DisableBuildServers`) đặt `DOTNET_CLI_USE_MSBUILD_SERVER=0`,
  `MSBUILDDISABLENODEREUSE=1`, `UseSharedCompilation=false` (MSBuild đọc biến môi trường thành property) cho cả cây.
- Kiểm (`src/tests/ProcBridge.Tests/Native/RemoteProcessTreeTests.cs`, Agent loopback): `cmd /c cmd /c type`,
  con khác kiến trúc sau khi cha xoá `PROCBRIDGE_SESSION`, handle stdin kế thừa báo đúng file ID và thời gian
  của máy B (đỏ khi bỏ bản ghi extra), `dotnet build` một project nằm trên Agent với `BaseOutputPath`/
  `BaseIntermediateOutputPath` trỏ ra thư mục tạm (máy B chỉ đọc tới M3) rồi chạy được app, Launcher chờ cháu
  chạy nền và đặt biến tắt build server.

### 13.5 Kết quả M3 (2026-11-25)

- **Agent**: `AgentShare` (trước là `ReadOnlyShare`) ghi được khi tạo bằng `--write-share name=path` (mặc định chỉ
  đọc, share chỉ đọc trả `STATUS_MEDIA_WRITE_PROTECTED`). Mọi thao tác ghi qua cùng bước kiểm đường dẫn của đọc (không
  `..`, thiết bị, link ra ngoài, không đụng gốc share). Message: `Fs.Create` (file/thư mục mới, trùng tên thì
  `OBJECT_NAME_COLLISION`), `Fs.Write` (một khối vào file staging `name.pbtmp-<id>` cạnh file đích, ẩn khỏi
  `Fs.List`), `Fs.Commit` (cắt staging đúng kích thước, so version máy A bắt đầu với version hiện tại, rồi
  `File.Replace` giữ danh tính file), `Fs.SetInfo` (thuộc tính, thời gian, kích thước), `Fs.Rename`, `Fs.Delete`
  (thư mục không rỗng `DIRECTORY_NOT_EMPTY`, file chỉ đọc `CANNOT_DELETE`). `HelloResponse.Writable` báo share có ghi
  được; giao thức lên bản 2.
- **Xung đột** (Plan 10): version không khớp lúc commit thì mặc định `KeepBoth`: file của máy B giữ nguyên, nội dung máy
  A lưu thành `name.procbridge-conflict-<UTC>`, Broker ghi log và giữ danh sách `WriteBackQueue.Conflicts`; tuỳ chọn
  `Overwrite` cho máy A thắng (`--conflict overwrite` ở Launcher).
- **Broker** (`HookRequestHandler.ResolveWritableAsync`): mở với quyền ghi/tạo hỏi lại stat máy B, tên chưa có thì
  `Fs.Create` trên máy B trước (lỗi trùng tên, thiếu thư mục cha đến từ máy B) rồi để trống tên trong cache cho lời gọi
  thật của Hook tạo; ghi đè/supersede file có sẵn không tải về, chỉ tạo file cache rỗng mang thuộc tính máy B. Cache
  của share ghi được giữ đúng thuộc tính máy B (share chỉ đọc vẫn ép read-only). Mở có thể đổi nội dung nhận
  **write token**; khi file đang có người ghi, lần mở khác dùng luôn bản cache thay vì tải đè.
- **Ghi ngược** (`WriteBackQueue`): lần đóng cuối (hoặc flush) của handle có token so file cache với version máy B nó
  bắt nguồn (kích thước + thời gian ghi); khác thì chép nội dung vào thư mục hàng đợi kèm `<id>.json`
  (`%LOCALAPPDATA%\ProcBridge\queue`, dùng chung các phiên), tải lên song song theo khối rồi commit với version cũ, xong
  mới xoá mục hàng đợi. Lời đóng chờ ghi ngược xong (tiến trình khác thấy ngay). Mất kết nối thì mục ở lại hàng đợi:
  vòng thử lại gửi khi Agent quay lại, Broker sau (cùng Agent + share) gửi lúc khởi động. Phiên kết thúc thì ghi ngược
  nốt file còn mở. Chế độ **ghi xuyên** (`--write-through`): Hello báo cờ, Hook chặn thêm `NtWriteFile` và gửi Flush
  sau mỗi lần ghi xong.
- **Hook**: `NtCreateFile` gửi kèm thuộc tính file; token lưu trong `RemoteHandleTable` (đếm tham chiếu cho bản nhân
  bản trong process, nên Broker chỉ nghe Close ở lần đóng cuối); mở thất bại trả token lại. `NtSetInformationFile`
  trên handle ảo: đổi tên/di chuyển (tên đầy đủ, theo `RootDirectory` ảo, hoặc tên trần cùng thư mục) đi máy B trước
  rồi đổi tên handle cache sang đường dẫn Broker trả; disposition xoá máy B rồi đặt cờ trên bản cache; Basic
  (thời gian, thuộc tính) đi máy B rồi bản cache. `NtDeleteFile` và delete-on-close xoá máy B. `NtFlushBuffersFile`
  gửi Flush. Handle có token báo thời gian/thuộc tính/dung lượng của bản cache (chứa các lần ghi của chính nó), chỉ
  file ID là của máy B. Rename thay thế một file thì handle khác đang mở file cũ trong process bị đánh dấu mồ côi
  (ReplaceFile xoá file cũ qua handle đó, không được đụng file mới). Đường dẫn `\Device\HarddiskVolumeN\...` tuyệt đối
  được dịch khi mở: advapi32 mở thư mục cha qua dạng này lúc áp ACE kế thừa (thiếu thì file cache sau ReplaceFile mang
  DACL rỗng).
- Broker gỡ tên file cache cũ bằng cách đổi tên đi rồi mới xoá, nên một handle còn giữ file không để lại tên
  delete-pending chặn lần mở/thay thế sau.
- **Sửa sau review M3**: mọi việc gửi/sửa mục hàng đợi của một đường dẫn (đóng, flush, vòng thử lại, recovery, rename và
  xoá trên máy B) chạy tuần tự qua cổng theo đường dẫn; bản mới của file đang mở thay mục chưa gửi, mục do vòng thử lại
  gửi xong dời version gốc của file đang mở. Id mục là upload id: Agent nhận lại commit đã áp dụng (nhớ id, hoặc file
  đã đúng kích thước + thời gian + nội dung) thì trả kết quả cũ, không tạo bản conflict. Bản chép vào hàng đợi lấy giữa
  hai lần version giống nhau và mang đúng version đó. Lúc file đang có người ghi, `SetInfo` không gửi thời gian ghi và
  kích thước sang máy B (đi cùng commit), tránh máy B "giống" bản cache rồi bỏ qua ghi ngược. Xoá chỉ chặn ghi ngược sau
  khi máy B xoá thành công; rename/xoá cập nhật hoặc bỏ mục chưa gửi. File đã xung đột vẫn lưu bản cuối ở lần đóng cuối
  (thêm một file conflict, tên trùng mili giây có hậu tố). Staging của Agent mở chia sẻ ghi (khối song song). Handle có
  token kế thừa sang con mang token trong bản ghi khối khởi tạo, cha gửi `Retain` (HookIpc 10) để Broker chờ thêm lần
  đóng của con. Delete-on-close gửi file ID máy B lúc mở (`DeleteFlagExpectFileId`), Agent không xoá file khác cùng tên.
- Kiểm: `NativeProbe --write` (tạo, CREATE_NEW trùng, append, truncate, end-of-file, rename, rename thay thế, CREATE_ALWAYS,
  TRUNCATE_EXISTING, FILE_SUPERSEDE, mkdir/rmdir, rename thư mục, xoá, thuộc tính hidden/read-only, xoá file chỉ đọc,
  SetFileTime, delete-on-close, disposition, disposition thư mục không rỗng, mở trùng với chia sẻ, ReplaceFile, lưu kiểu
  file tạm + rename, liệt kê cuối) thật và qua Agent giống từng dòng, x64 và Win32 (`RemoteWriteTests`); `cmd echo >`/`>>`,
  `mkdir`, PowerShell `File.Replace` có bản backup; ghi xuyên thấy dữ liệu trên máy B khi handle còn mở; `dotnet build`
  một project trên Agent với `bin`/`obj` **trong** thư mục ảo rồi chạy được app từ máy B. `BrokerWriteBackTests` (giả
  Hook qua pipe): tạo trên máy B trước, ghi ngược khi đóng, ghi đè không tải về, sửa trên máy B lúc máy A đang mở thì
  phát hiện xung đột (giữ cả hai / máy A thắng), Agent chết lúc đóng thì mục ở lại hàng đợi và Broker sau gửi, Agent quay
  lại thì vòng thử lại gửi, rename/xoá/SetInfo. `AgentShareWriteTests` cho các mã lỗi phía Agent. Ca chưa phủ ở Backlog.

---

## 14. Duyệt tài nguyên máy B (Remote Explorer)

Vì máy B nằm ở xa, người dùng ở máy A cần xem được máy B mà không phải điều khiển màn hình: chọn thư
mục để mở phiên, kiểm tra process, xem registry. Chức năng này **không cần hook**: Broker (hoặc một
client riêng) gửi request thẳng tới Agent qua đúng kênh đã mã hoá đầu cuối (mục 9), nên chạy được cả
qua LAN lẫn qua Relay.

| Loại | Miền giao thức | Đọc | Thao tác (bật riêng) |
|---|---|---|---|
| Ổ đĩa, file, thư mục | `Fs.*` (dùng lại), `Sys.Drives` | Liệt kê ổ, duyệt cây thư mục, xem thuộc tính, tải file về A | Tải lên, tạo, đổi tên, xoá, đặt thành thư mục ảo cho phiên mới |
| Process | `Proc.*` | Danh sách process (PID, tên, đường dẫn, dòng lệnh, user, CPU/RAM, cây cha-con), module đã nạp | Kill, chạy chương trình trên máy B |
| Registry | `Reg.*` | Duyệt khoá, đọc giá trị | Tạo, sửa, xoá khoá/giá trị, xuất `.reg` |
| Service | `Sys.Services` | Danh sách service, trạng thái, kiểu khởi động | Start, stop, restart |
| Hệ thống | `Sys.Info` | Tên máy, phiên bản Windows, CPU/RAM/ổ đĩa, mạng, user đang đăng nhập | — |

Phân quyền (Agent là bên quyết định, không tin phía A):

- Mỗi máy A đã ghép cặp có một **hồ sơ quyền** do chủ máy B đặt: phạm vi duyệt file (chỉ các thư mục
  chia sẻ hay toàn máy), có được xem process/registry/service không, và **thao tác ghi bật riêng từng
  loại, mặc định tắt**.
- Quyền thực tế còn bị giới hạn bởi tài khoản Windows mà Agent đang chạy (xem câu hỏi mở về Service
  hay app người dùng).
- Mọi thao tác ghi (kill, sửa registry, xoá file) được Agent ghi log kèm máy A đã yêu cầu.

Giao diện: đặt trong Launcher (dạng cây + danh sách kiểu Explorer, tab cho File / Process / Registry /
Service / Hệ thống). Chỗ đặt UI là câu hỏi mở số 5.

Lộ trình: duyệt file và ổ đĩa đi kèm M2 và M3; process, registry, service, thông tin hệ thống là M9.

---

## 15. Rủi ro và câu hỏi mở

| Rủi ro | Ảnh hưởng | Hướng xử lý |
|---|---|---|
| Số lượng `FILE_INFORMATION_CLASS` và hành vi biên của `Nt*` rất lớn | Process gặp lỗi lạ khi gặp class chưa hỗ trợ | Test so sánh (mục 12); class chưa hỗ trợ trả `STATUS_INVALID_INFO_CLASS` và ghi log để bổ sung dần |
| Memory-mapped file trên file chưa tải trọn | Đọc ra dữ liệu sai | M2–M7 luôn tải trọn trước khi mở; tải theo vùng ở M8 chỉ bật khi file không bị map, hoặc tải trọn khi gặp `NtCreateSection` |
| Antivirus coi inject là hành vi độc hại | Bị chặn hoặc xoá DLL | Ký số DLL, tài liệu hướng dẫn loại trừ, không dùng kỹ thuật inject bất thường |
| Độ trễ internet làm build rất chậm | Trải nghiệm kém | Lấy metadata cả cây, đọc trước, thư mục loại trừ cho đầu ra build |
| Ghi ngược trễ, mất điện máy A | Mất dữ liệu chưa gửi | Hàng đợi ghi lưu trên đĩa, gửi lại khi khởi động; có chế độ ghi xuyên |
| Process ngoài cây (COM, ShellExecute) | Không thấy thư mục ảo | Ghi rõ là giới hạn; về sau cân nhắc hook `ShellExecute` để chạy qua Launcher |

Câu hỏi mở đã chốt với user (2026-11-25):

1. Truyền dữ liệu: chạy song song, chia nhiều kết nối càng tốt (nhiều TCP/WebSocket cho một phiên,
   khối dữ liệu lớn chia ra nhiều kết nối). Tuần tự hoá: **MessagePack** (MessagePack-CSharp, source
   generator, không reflection) cho message điều khiển; dữ liệu file đi dạng khung nhị phân thô (header
   MessagePack + payload byte nguyên, không bọc lại) để không tốn copy. Lý do: độ trễ (de)serialize thấp
   nhất trong .NET, gói nhỏ, không cần file `.proto`; độ tin cậy dựa vào framing có độ dài + mã hoá
   AEAD (phát hiện hỏng/giả mạo), không phụ thuộc định dạng.
2. Mã ghép cặp hiển thị trên giao diện WPF (C#) của Agent máy B.
3. Relay ở M6 chỉ REST + CLI, không có giao diện web.
4. Agent là app người dùng, không phải Windows Service; cần quyền cao thì user mở Agent bằng quyền
   admin.
5. Remote Explorer nằm trong bước chọn thư mục đích để "map" của Launcher (WPF).
6. Phạm vi duyệt file do máy chia sẻ (máy B) cấp quyền.
