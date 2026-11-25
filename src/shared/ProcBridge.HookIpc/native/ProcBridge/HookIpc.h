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
//   Close   request : TokenRequest (handle with a write token closed: write the cache copy back if it changed)
//           reply   : no body
//   Flush   request : TokenRequest; reply: no body
//   SetInfo request : SetInfoRequest, then String RelativePath; reply: FileInfo
//   Rename  request : RenameRequest, then String RelativePath, String NewRelativePath
//           reply   : FileInfo, then String NewCachePath (machine B renamed; rename the cache handle to it)
//   Delete  request : PathRequest (Reserved = DeleteFlag*), then String RelativePath; reply: no body

#include <cstdint>

namespace ProcBridge::HookIpc
{
    constexpr uint16_t ProtocolVersion = 2;
    constexpr uint32_t HeaderSize = 16;
    constexpr uint32_t MaxMessageSize = 65536;
    constexpr uint32_t FileInfoSize = 64;
    constexpr uint16_t ListFlagHasMore = 1;
    constexpr uint16_t HelloFlagWriteThrough = 1;
    constexpr uint16_t RenameFlagReplaceIfExists = 1;
    constexpr uint16_t DeleteFlagKeepItem = 1; // PathRequest.Reserved of Delete: the hook deletes the cache copy itself
    constexpr const wchar_t* PipeNamePrefix = L"\\\\.\\pipe\\ProcBridge-";

    enum class MessageType : uint16_t
    {
        Hello = 1,
        Stat = 2,
        Resolve = 3,
        List = 4,
        Close = 5,
        Flush = 6,
        SetInfo = 7,
        Rename = 8,
        Delete = 9,
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
        uint32_t Reserved;
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
#pragma pack(pop)

    static_assert(sizeof(MessageHeader) == HeaderSize);
    static_assert(sizeof(FileInfo) == FileInfoSize);
    static_assert(sizeof(HelloRequest) == 8);
    static_assert(sizeof(HelloReply) == 4);
    static_assert(sizeof(PathRequest) == 4);
    static_assert(sizeof(ResolveRequest) == 24);
    static_assert(sizeof(ListRequest) == 8);
    static_assert(sizeof(ListReply) == 4);
    static_assert(sizeof(TokenRequest) == 8);
    static_assert(sizeof(SetInfoRequest) == 40);
    static_assert(sizeof(RenameRequest) == 8);
}
