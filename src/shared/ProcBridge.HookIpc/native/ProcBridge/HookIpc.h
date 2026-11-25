#pragma once
// Hook <-> Broker IPC format (plan section 6). Mirrors src/shared/ProcBridge.HookIpc (C#).
// Keep both sides in sync; HookIpcHeaderTests (C#) parses this file and compares the constants.
//
// Transport: named pipe \\.\pipe\ProcBridge-<session>, message mode, many instances, ACL = current user.
// One request then one reply per pipe instance at a time.
// Message: MessageHeader (16 bytes) followed by the body, little endian, no padding.
// String: uint16 charCount followed by charCount UTF-16 code units, no terminator, no alignment.
// Because strings are unaligned, read the fixed structs below with memcpy, not by casting pointers.
// A reply whose Status is not 0 (an NTSTATUS for the hooked call) has no body.
//
// Bodies:
//   Hello   request : HelloRequest
//           reply   : HelloReply, then MappingCount x String VirtualRoot (DOS path, no trailing '\')
//   Stat    request : PathRequest, then String RelativePath ('\' separated, relative to the virtual root)
//           reply   : FileInfo
//   Resolve request : ResolveRequest, then String RelativePath
//           reply   : FileInfo, then String CachePath (DOS path; open it as \??\<CachePath>)
//   List    request : ListRequest, then String RelativePath
//           reply   : ListReply, then Count x (FileInfo, String Name); Flags & ListFlagHasMore = ask again with
//                     StartIndex += Count
//   WatchDirectory request : WatchRequest, then String RelativePath (directory to watch)
//                  reply   : WatchReply
//   WaitChanges    request : WaitChangesRequest (long poll; use a pipe instance of its own)
//                  reply   : WaitChangesReply, then Count x (ChangeEntry, String Name); Name is relative to the
//                            watched directory like FILE_NOTIFY_INFORMATION.FileName. Count 0 without
//                            ChangesFlagOverflow = timeout. ChangesFlagOverflow = changes lost: complete the
//                            notify with STATUS_NOTIFY_ENUM_DIR.
//   Unwatch        request : UnwatchRequest; reply has no body

#include <cstdint>

namespace ProcBridge::HookIpc
{
    constexpr uint16_t ProtocolVersion = 1;
    constexpr uint32_t HeaderSize = 16;
    constexpr uint32_t MaxMessageSize = 65536;
    constexpr uint32_t FileInfoSize = 64;
    constexpr uint16_t ListFlagHasMore = 1;
    constexpr uint16_t WatchFlagSubtree = 1;
    constexpr uint16_t ChangesFlagOverflow = 1;
    constexpr uint32_t InfiniteTimeout = 0xFFFFFFFF;
    constexpr const wchar_t* PipeNamePrefix = L"\\\\.\\pipe\\ProcBridge-";

    enum class MessageType : uint16_t
    {
        Hello = 1,
        Stat = 2,
        Resolve = 3,
        List = 4,
        WatchDirectory = 5,
        WaitChanges = 6,
        Unwatch = 7,
    };

    enum class Architecture : uint16_t
    {
        X86 = 0,
        X64 = 1,
        Arm64 = 2,
    };

#pragma pack(push, 1)
    struct MessageHeader
    {
        uint32_t Size;        // whole message, header included
        uint16_t Version;     // ProtocolVersion
        uint16_t MessageType; // MessageType
        uint32_t RequestId;   // echoed by the reply
        uint32_t Status;      // NTSTATUS in replies, 0 in requests
    };

    struct FileInfo
    {
        uint32_t Attributes;  // FILE_ATTRIBUTE_* of machine B
        uint32_t Reserved;
        int64_t Size;
        int64_t AllocationSize;
        int64_t CreationTime; // FILETIME UTC
        int64_t LastAccessTime;
        int64_t LastWriteTime;
        int64_t ChangeTime;
        uint64_t FileId;      // 0 when unknown
    };

    struct HelloRequest
    {
        uint32_t ProcessId;
        uint16_t Architecture;
        uint16_t Reserved;
    };

    struct HelloReply
    {
        uint16_t MappingCount;
        uint16_t Reserved;
    };

    struct PathRequest
    {
        uint16_t MappingIndex;
        uint16_t Reserved;
    };

    struct ResolveRequest
    {
        uint16_t MappingIndex;
        uint16_t Flags;
        uint32_t DesiredAccess;     // NtCreateFile arguments, as received by the hook
        uint32_t CreateDisposition;
        uint32_t CreateOptions;
    };

    struct ListRequest
    {
        uint16_t MappingIndex;
        uint16_t Reserved;
        uint32_t StartIndex;
    };

    struct ListReply
    {
        uint16_t Count;
        uint16_t Flags;
    };

    struct WatchRequest
    {
        uint16_t MappingIndex;
        uint16_t Flags;            // WatchFlagSubtree = WatchTree argument
        uint32_t CompletionFilter; // FILE_NOTIFY_CHANGE_*, as received (not applied exactly by the Broker)
    };

    struct WatchReply
    {
        uint32_t WatchId;
        uint32_t Reserved;
    };

    struct WaitChangesRequest
    {
        uint32_t WatchId;
        uint32_t TimeoutMilliseconds; // InfiniteTimeout = until a change or Unwatch
    };

    struct WaitChangesReply
    {
        uint16_t Count;
        uint16_t Flags; // ChangesFlagOverflow
    };

    struct ChangeEntry
    {
        uint32_t Action; // FILE_ACTION_*
    };

    struct UnwatchRequest
    {
        uint32_t WatchId;
    };
#pragma pack(pop)

    static_assert(sizeof(MessageHeader) == HeaderSize);
    static_assert(sizeof(FileInfo) == FileInfoSize);
    static_assert(sizeof(HelloRequest) == 8);
    static_assert(sizeof(HelloReply) == 4);
    static_assert(sizeof(PathRequest) == 4);
    static_assert(sizeof(ResolveRequest) == 16);
    static_assert(sizeof(ListRequest) == 8);
    static_assert(sizeof(ListReply) == 4);
    static_assert(sizeof(WatchRequest) == 8);
    static_assert(sizeof(WatchReply) == 8);
    static_assert(sizeof(WaitChangesRequest) == 8);
    static_assert(sizeof(WaitChangesReply) == 4);
    static_assert(sizeof(ChangeEntry) == 4);
    static_assert(sizeof(UnwatchRequest) == 4);
}
