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
// Before the reply the Broker may send progress messages (Status = StatusInProgress, no body, same RequestId) while a
// request takes long (a large download): the hook reads on and waits its call timeout again from each one.
//
// Bodies:
//   Hello   request : HelloRequest
//           reply   : HelloReply, then MappingCount x String VirtualRoot (DOS path, no trailing '\');
//                     Flags & HelloFlagWriteThrough = send Flush after every write on a handle with a write token
//   Stat    request : PathRequest, then String RelativePath ('\' separated, relative to the virtual root)
//           reply   : FileInfo
//   Resolve request : ResolveRequest, then String RelativePath
//           reply   : FileInfo, uint64 WriteToken (0 = none), then String CachePath (DOS path; open it as
//                     \??\<CachePath>). A writable share creates a missing name on machine B before replying.
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
//   WaitAnyChanges request : WaitAnyChangesRequest (one long poll for every watch of the process; use a pipe
//                            instance of its own)
//                  reply   : WaitAnyChangesReply, then GroupCount x (WatchChangesHeader, Count x (ChangeEntry,
//                            String Name)). No group = timeout or a new watch of the process: poll again.
//                            WatchChangesHeader.Flags & ChangesFlagOverflow = changes of that watch were lost.
//   Unwatch        request : UnwatchRequest; reply has no body
//   Close   request : TokenRequest (handle with a write token closed: write the cache copy back if it changed)
//           reply   : no body
//   Flush   request : TokenRequest; reply: no body
//   Retain  request : TokenRequest (a child inherited a handle holding the token: Close is then expected once more)
//           reply   : no body
//   SetInfo request : SetInfoRequest, then String RelativePath; reply: FileInfo
//   Rename  request : RenameRequest, then String RelativePath, String NewRelativePath
//           reply   : FileInfo, then String NewCachePath (machine B renamed; rename the cache handle to it)
//   Delete  request : PathRequest (Reserved = DeleteFlag*), then String RelativePath, then uint64 ExpectedFileId
//                     when DeleteFlagExpectFileId is set; reply: no body
//   FetchRange request : FetchRangeRequest (M8: FileInfo.RangeId of the Resolve reply was not 0, the cache copy is a
//                        sparse file downloaded by ranges; read or map only after the range is in)
//              reply   : FetchRangeReply; Flags & FetchReplyFlagComplete = the copy is complete, stop asking
//   Lock / Unlock request : LockRequest (M8, handle with a write token: the lock is also taken on machine B)
//                 reply   : no body; STATUS_LOCK_NOT_GRANTED when machine B holds a conflicting lock

#include <cstdint>

namespace ProcBridge::HookIpc
{
    constexpr uint16_t ProtocolVersion = 5;
    constexpr uint32_t HeaderSize = 16;
    constexpr uint32_t MaxMessageSize = 65536;
    constexpr uint32_t FileInfoSize = 64;
    constexpr uint16_t ListFlagHasMore = 1;
    constexpr uint16_t WatchFlagSubtree = 1;
    constexpr uint16_t ChangesFlagOverflow = 1;
    constexpr uint32_t InfiniteTimeout = 0xFFFFFFFF;
    constexpr uint16_t HelloFlagWriteThrough = 1;
    constexpr uint16_t RenameFlagReplaceIfExists = 1;
    constexpr uint16_t DeleteFlagKeepItem = 1; // PathRequest.Reserved of Delete: the hook deletes the cache copy itself
    constexpr uint16_t DeleteFlagExpectFileId = 2; // ExpectedFileId follows: machine B leaves another file under the name alone
    constexpr uint32_t FetchFlagWhole = 1;          // FetchRangeRequest.Flags: the whole file (mapping, inheritance)
    constexpr uint32_t FetchReplyFlagComplete = 1;  // FetchRangeReply.Flags
    constexpr uint16_t LockFlagExclusive = 1;
    constexpr uint16_t LockFlagFailImmediately = 2;
    constexpr uint32_t StatusInProgress = 0x103;   // progress message: the reply follows, wait the call timeout again
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
        Close = 8,
        Flush = 9,
        SetInfo = 10,
        Rename = 11,
        Delete = 12,
        Retain = 13,
        FetchRange = 14,
        Lock = 15,
        Unlock = 16,
        WaitAnyChanges = 17,
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
        uint32_t RangeId;     // Resolve replies only: non-zero = partly downloaded cache copy (FetchRange), 0 elsewhere
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
        uint16_t Flags;       // HelloFlag*
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
        uint32_t FileAttributes;
        uint32_t ShareAccess;       // FILE_SHARE_*: machine B keeps a handle with it for a writer (M8)
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

    struct WaitAnyChangesRequest
    {
        uint32_t TimeoutMilliseconds; // InfiniteTimeout = until a change or a new watch
    };

    struct WaitAnyChangesReply
    {
        uint16_t GroupCount;
        uint16_t Reserved;
    };

    struct WatchChangesHeader
    {
        uint32_t WatchId;
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

    struct TokenRequest
    {
        uint64_t WriteToken;
    };

    enum SetInfoField : uint16_t
    {
        SetAttributes = 1,
        SetCreationTime = 2,
        SetLastAccessTime = 4,
        SetLastWriteTime = 8,
        SetEndOfFile = 16,
    };

    struct SetInfoRequest
    {
        uint16_t MappingIndex;
        uint16_t Fields;      // SetInfoField bits
        uint32_t Attributes;
        int64_t CreationTime;
        int64_t LastAccessTime;
        int64_t LastWriteTime;
        int64_t EndOfFile;
    };

    struct RenameRequest
    {
        uint16_t MappingIndex;
        uint16_t NewMappingIndex;
        uint16_t Flags;       // RenameFlagReplaceIfExists
        uint16_t Reserved;
    };
    struct FetchRangeRequest
    {
        uint32_t RangeId;
        uint32_t Flags;       // FetchFlagWhole
        int64_t Offset;
        int64_t Length;
    };

    struct FetchRangeReply
    {
        uint32_t Flags;       // FetchReplyFlagComplete
    };

    struct LockRequest
    {
        uint64_t WriteToken;
        int64_t Offset;
        int64_t Length;
        uint32_t Key;
        uint16_t Flags;       // LockFlag* (Lock only)
        uint16_t Reserved;
    };
#pragma pack(pop)

    static_assert(sizeof(MessageHeader) == HeaderSize);
    static_assert(sizeof(FileInfo) == FileInfoSize);
    static_assert(sizeof(HelloRequest) == 8);
    static_assert(sizeof(HelloReply) == 4);
    static_assert(sizeof(PathRequest) == 4);
    static_assert(sizeof(ResolveRequest) == 24);
    static_assert(sizeof(ListRequest) == 8);
    static_assert(sizeof(ListReply) == 4);
    static_assert(sizeof(WatchRequest) == 8);
    static_assert(sizeof(WatchReply) == 8);
    static_assert(sizeof(WaitChangesRequest) == 8);
    static_assert(sizeof(WaitChangesReply) == 4);
    static_assert(sizeof(WaitAnyChangesRequest) == 4);
    static_assert(sizeof(WaitAnyChangesReply) == 4);
    static_assert(sizeof(WatchChangesHeader) == 8);
    static_assert(sizeof(ChangeEntry) == 4);
    static_assert(sizeof(UnwatchRequest) == 4);
    static_assert(sizeof(TokenRequest) == 8);
    static_assert(sizeof(SetInfoRequest) == 40);
    static_assert(sizeof(RenameRequest) == 8);
    static_assert(sizeof(FetchRangeRequest) == 24);
    static_assert(sizeof(FetchRangeReply) == 4);
    static_assert(sizeof(LockRequest) == 32);
}
