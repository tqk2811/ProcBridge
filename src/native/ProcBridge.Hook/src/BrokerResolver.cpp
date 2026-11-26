#include "BrokerResolver.h"
#include "PathString.h"
#include <cstring>

namespace ProcBridge::Hook
{
    namespace
    {
        constexpr wchar_t NtPrefix[] = L"\\??\\";
        constexpr USHORT NtPrefixChars = 4;
        constexpr ULONG FileOpen = 1;
        constexpr ULONG FileDirectoryFile = 0x00000001;

        DWORD ReadNumber(const wchar_t* name, DWORD& value) noexcept
        {
            wchar_t text[16];
            const DWORD length = GetEnvironmentVariableW(name, text, 16);
            if (length == 0 || length >= 16)
            {
                return length == 0 ? GetLastError() : ERROR_INVALID_PARAMETER;
            }
            DWORD result = 0;
            for (DWORD i = 0; i < length; ++i)
            {
                if (text[i] < L'0' || text[i] > L'9' || result > (MAXDWORD - 9) / 10)
                {
                    return ERROR_INVALID_PARAMETER;
                }
                result = result * 10 + static_cast<DWORD>(text[i] - L'0');
            }
            value = result;
            return ERROR_SUCCESS;
        }

        /// Creation time of process (FILETIME UTC); false when it cannot be read (no query access).
        bool GetCreationTime(HANDLE process, uint64_t& creationTime) noexcept
        {
            FILETIME created{};
            FILETIME exited{};
            FILETIME kernel{};
            FILETIME user{};
            if (!GetProcessTimes(process, &created, &exited, &kernel, &user))
            {
                return false;
            }
            creationTime = (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
            return true;
        }

        HookIpc::Architecture CurrentArchitecture() noexcept
        {
#if defined(_M_ARM64)
            return HookIpc::Architecture::Arm64;
#elif defined(_WIN64)
            return HookIpc::Architecture::X64;
#else
            return HookIpc::Architecture::X86;
#endif
        }
    }

    bool BrokerResolver::IsConfigured() noexcept
    {
        return GetEnvironmentVariableW(SessionVariable, nullptr, 0) != 0;
    }

    DWORD BrokerResolver::Initialize() noexcept
    {
        // Session ids are 32 hex digits; the bound only protects the buffer.
        constexpr DWORD sessionCapacity = 128;
        wchar_t pipeName[sessionCapacity + 32];
        const size_t prefixChars = static_cast<size_t>(lstrlenW(HookIpc::PipeNamePrefix));
        memcpy(pipeName, HookIpc::PipeNamePrefix, prefixChars * sizeof(wchar_t));
        const DWORD sessionChars = GetEnvironmentVariableW(SessionVariable, pipeName + prefixChars, sessionCapacity);
        if (sessionChars == 0 || sessionChars >= sessionCapacity)
        {
            return ERROR_INVALID_PARAMETER;
        }
        DWORD brokerProcessId = 0;
        DWORD result = ReadNumber(BrokerProcessVariable, brokerProcessId);
        if (result != ERROR_SUCCESS)
        {
            return result == ERROR_ENVVAR_NOT_FOUND ? ERROR_INVALID_PARAMETER : result;
        }
        DWORD timeoutMs = DefaultTimeoutMs;
        if (ReadNumber(TimeoutVariable, timeoutMs) != ERROR_SUCCESS || timeoutMs == 0)
        {
            timeoutMs = DefaultTimeoutMs;
        }
        result = _client.Initialize(pipeName, brokerProcessId, timeoutMs > StartupTimeoutMs ? timeoutMs : StartupTimeoutMs);
        if (result != ERROR_SUCCESS)
        {
            return result;
        }

        // Hello: the virtual roots, in mapping index order.
        BrokerClient::Call hello(_client, HookIpc::MessageType::Hello);
        HookIpc::HelloRequest request{};
        request.ProcessId = GetCurrentProcessId();
        request.Architecture = static_cast<uint16_t>(CurrentArchitecture());
        uint64_t creationTime = 0;
        GetCreationTime(GetCurrentProcess(), creationTime);
        request.ProcessCreationTime = creationTime;
        hello.Put(&request, sizeof(request));
        BrokerReply reply;
        if (!NT_SUCCESS(hello.Send(reply)))
        {
            return ERROR_PIPE_NOT_CONNECTED;
        }
        MessageReader reader(reply.Body, reply.BodySize);
        HookIpc::HelloReply helloReply{};
        if (!reader.Read(&helloReply, sizeof(helloReply)) || helloReply.MappingCount > PathMapper::MaxMappings)
        {
            return ERROR_INVALID_DATA;
        }
        _writeThrough = (helloReply.Flags & HookIpc::HelloFlagWriteThrough) != 0;
        wchar_t virtualRoot[MAX_PATH];
        for (USHORT index = 0; index < helloReply.MappingCount; ++index)
        {
            const BYTE* text = nullptr;
            USHORT chars = 0;
            if (!reader.ReadString(text, chars) || chars == 0 || chars >= MAX_PATH)
            {
                return ERROR_INVALID_DATA;
            }
            memcpy(virtualRoot, text, chars * sizeof(wchar_t));
            virtualRoot[chars] = L'\0';

            // The root's cache folder: every path below it is cached at the same relative path (the
            // Broker's cache layout), which keeps the local name rewriting of M1.
            const OpenIntent rootIntent{ FILE_LIST_DIRECTORY | SYNCHRONIZE, FileOpen, FileDirectoryFile };
            UNICODE_STRING empty{};
            UNICODE_STRING realRoot{};
            if (!NT_SUCCESS(ResolveRelative(index, empty, rootIntent, realRoot, nullptr, nullptr)))
            {
                return ERROR_PATH_NOT_FOUND;
            }
            result = _paths.AddMapping(virtualRoot, realRoot.Buffer + NtPrefixChars);
            FreeString(realRoot);
            if (result != ERROR_SUCCESS)
            {
                return result;
            }
        }
        // Start-up done: later calls use the configured timeout. hello's instance goes back to the pool here.
        _client.SetTimeout(timeoutMs);
        return ERROR_SUCCESS;
    }

    UNICODE_STRING BrokerResolver::RelativePath(int mapping, const UNICODE_STRING& path, PathKind kind) const noexcept
    {
        const PathMapping& entry = _paths.GetMapping(mapping);
        const UNICODE_STRING& root = kind == PathKind::Nt ? entry.VirtualNt : entry.VirtualDevicePath;
        UNICODE_STRING relative{};
        relative.Buffer = reinterpret_cast<wchar_t*>(reinterpret_cast<BYTE*>(path.Buffer) + root.Length);
        relative.Length = static_cast<USHORT>(path.Length - root.Length);
        while (relative.Length > 0 && relative.Buffer[0] == L'\\')
        {
            ++relative.Buffer;
            relative.Length -= sizeof(wchar_t);
        }
        relative.MaximumLength = relative.Length;
        return relative;
    }

    NTSTATUS BrokerResolver::ResolveRelative(int mapping, const UNICODE_STRING& relative, const OpenIntent& intent,
        UNICODE_STRING& realPath, HookIpc::FileInfo* info, uint64_t* writeToken) const noexcept
    {
        BrokerClient::Call call(_client, HookIpc::MessageType::Resolve);
        HookIpc::ResolveRequest request{};
        request.MappingIndex = static_cast<uint16_t>(mapping);
        request.DesiredAccess = intent.DesiredAccess;
        request.CreateDisposition = intent.CreateDisposition;
        request.CreateOptions = intent.CreateOptions;
        request.FileAttributes = intent.FileAttributes;
        request.ShareAccess = intent.ShareAccess;
        call.Put(&request, sizeof(request));
        call.PutString(relative.Buffer, relative.Length);
        BrokerReply reply;
        const NTSTATUS status = call.Send(reply);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        MessageReader reader(reply.Body, reply.BodySize);
        HookIpc::FileInfo received{};
        uint64_t token = 0;
        const BYTE* cachePath = nullptr;
        USHORT chars = 0;
        if (!reader.Read(&received, sizeof(received)) || !reader.Read(&token, sizeof(token)) || !reader.ReadString(cachePath, chars)
            || chars == 0)
        {
            return STATUS_INVALID_NETWORK_RESPONSE;
        }
        const NTSTATUS built = MakeNtPath(cachePath, chars, realPath);
        if (!NT_SUCCESS(built))
        {
            return built;
        }
        if (info != nullptr)
        {
            *info = received;
        }
        if (writeToken != nullptr)
        {
            *writeToken = token;
        }
        return STATUS_SUCCESS;
    }

    NTSTATUS BrokerResolver::MakeNtPath(const BYTE* text, USHORT chars, UNICODE_STRING& realPath) noexcept
    {
        const size_t bytes = (static_cast<size_t>(NtPrefixChars) + chars) * sizeof(wchar_t);
        if (bytes > 0xFFFC || !AllocateString(realPath, bytes))
        {
            return bytes > 0xFFFC ? STATUS_NAME_TOO_LONG : STATUS_NO_MEMORY;
        }
        memcpy(realPath.Buffer, NtPrefix, NtPrefixChars * sizeof(wchar_t));
        memcpy(realPath.Buffer + NtPrefixChars, text, chars * sizeof(wchar_t));
        realPath.Length = static_cast<USHORT>(bytes);
        realPath.Buffer[bytes / sizeof(wchar_t)] = L'\0';
        return STATUS_SUCCESS;
    }

    NTSTATUS BrokerResolver::FetchRange(uint32_t rangeId, int64_t offset, int64_t length, bool whole, bool& complete) const noexcept
    {
        complete = false;
        BrokerClient::Call call(_client, HookIpc::MessageType::FetchRange);
        HookIpc::FetchRangeRequest request{};
        request.RangeId = rangeId;
        request.Flags = whole ? HookIpc::FetchFlagWhole : 0;
        request.Offset = offset;
        request.Length = length;
        call.Put(&request, sizeof(request));
        BrokerReply reply;
        // A whole file (a mapping) may be large: give it more time than one request, but never wait forever.
        const ULONGLONG timeout = whole ? static_cast<ULONGLONG>(_client.Timeout()) * 10 : _client.Timeout();
        const NTSTATUS status = call.Send(reply, nullptr, static_cast<DWORD>(timeout < INFINITE ? timeout : INFINITE - 1));
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        MessageReader reader(reply.Body, reply.BodySize);
        HookIpc::FetchRangeReply body{};
        if (!reader.Read(&body, sizeof(body)))
        {
            return STATUS_INVALID_NETWORK_RESPONSE;
        }
        complete = (body.Flags & HookIpc::FetchReplyFlagComplete) != 0;
        return STATUS_SUCCESS;
    }

    NTSTATUS BrokerResolver::LockRange(bool lock, uint64_t writeToken, int64_t offset, int64_t length, ULONG key, uint16_t flags) const noexcept
    {
        BrokerClient::Call call(_client, lock ? HookIpc::MessageType::Lock : HookIpc::MessageType::Unlock);
        HookIpc::LockRequest request{};
        request.WriteToken = writeToken;
        request.Offset = offset;
        request.Length = length;
        request.Key = key;
        request.Flags = lock ? flags : 0;
        call.Put(&request, sizeof(request));
        BrokerReply reply;
        return call.Send(reply);
    }

    NTSTATUS BrokerResolver::ShareWrite(uint64_t writeToken, HANDLE targetProcess) const noexcept
    {
        HookIpc::ShareRequest request{};
        request.WriteToken = writeToken;
        request.TargetProcessId = GetProcessId(targetProcess);
        uint64_t creationTime = 0;
        if (request.TargetProcessId == 0 || !GetCreationTime(targetProcess, creationTime))
        {
            return STATUS_ACCESS_DENIED;
        }
        request.TargetCreationTime = creationTime;
        BrokerClient::Call call(_client, HookIpc::MessageType::Share);
        call.Put(&request, sizeof(request));
        BrokerReply reply;
        return call.Send(reply);
    }

    NTSTATUS BrokerResolver::Adopt(int mapping, const UNICODE_STRING& path, PathKind kind, ACCESS_MASK grantedAccess,
        bool writable, HookIpc::FileInfo& info, uint64_t& writeToken) const noexcept
    {
        writeToken = 0;
        const UNICODE_STRING relative = RelativePath(mapping, path, kind);
        BrokerClient::Call call(_client, HookIpc::MessageType::Adopt);
        HookIpc::AdoptRequest request{};
        request.MappingIndex = static_cast<uint16_t>(mapping);
        request.Flags = writable ? HookIpc::AdoptFlagWrite : 0;
        request.GrantedAccess = grantedAccess;
        call.Put(&request, sizeof(request));
        call.PutString(relative.Buffer, relative.Length);
        BrokerReply reply;
        const NTSTATUS status = call.Send(reply);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        MessageReader reader(reply.Body, reply.BodySize);
        return reader.Read(&info, sizeof(info)) && reader.Read(&writeToken, sizeof(writeToken)) ? STATUS_SUCCESS : STATUS_INVALID_NETWORK_RESPONSE;
    }

    NTSTATUS BrokerResolver::SendToken(HookIpc::MessageType type, uint64_t writeToken) const noexcept
    {
        BrokerClient::Call call(_client, type);
        HookIpc::TokenRequest request{};
        request.WriteToken = writeToken;
        call.Put(&request, sizeof(request));
        BrokerReply reply;
        return call.Send(reply);
    }

    NTSTATUS BrokerResolver::SetInfo(int mapping, const UNICODE_STRING& ntPath, const HookIpc::SetInfoRequest& request,
        HookIpc::FileInfo& info) const noexcept
    {
        const UNICODE_STRING relative = RelativePath(mapping, ntPath, PathKind::Nt);
        BrokerClient::Call call(_client, HookIpc::MessageType::SetInfo);
        HookIpc::SetInfoRequest body = request;
        body.MappingIndex = static_cast<uint16_t>(mapping);
        call.Put(&body, sizeof(body));
        call.PutString(relative.Buffer, relative.Length);
        BrokerReply reply;
        const NTSTATUS status = call.Send(reply);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        MessageReader reader(reply.Body, reply.BodySize);
        return reader.Read(&info, sizeof(info)) ? STATUS_SUCCESS : STATUS_INVALID_NETWORK_RESPONSE;
    }

    NTSTATUS BrokerResolver::Rename(int mapping, const UNICODE_STRING& ntPath, int newMapping, const UNICODE_STRING& newNtPath,
        bool replaceIfExists, HookIpc::FileInfo& info, UNICODE_STRING& newRealPath) const noexcept
    {
        const UNICODE_STRING relative = RelativePath(mapping, ntPath, PathKind::Nt);
        const UNICODE_STRING newRelative = RelativePath(newMapping, newNtPath, PathKind::Nt);
        BrokerClient::Call call(_client, HookIpc::MessageType::Rename);
        HookIpc::RenameRequest request{};
        request.MappingIndex = static_cast<uint16_t>(mapping);
        request.NewMappingIndex = static_cast<uint16_t>(newMapping);
        request.Flags = replaceIfExists ? HookIpc::RenameFlagReplaceIfExists : 0;
        call.Put(&request, sizeof(request));
        call.PutString(relative.Buffer, relative.Length);
        call.PutString(newRelative.Buffer, newRelative.Length);
        BrokerReply reply;
        const NTSTATUS status = call.Send(reply);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        MessageReader reader(reply.Body, reply.BodySize);
        const BYTE* cachePath = nullptr;
        USHORT chars = 0;
        if (!reader.Read(&info, sizeof(info)) || !reader.ReadString(cachePath, chars) || chars == 0)
        {
            return STATUS_INVALID_NETWORK_RESPONSE;
        }
        return MakeNtPath(cachePath, chars, newRealPath);
    }

    NTSTATUS BrokerResolver::Delete(int mapping, const UNICODE_STRING& ntPath, bool keepItem, uint64_t expectedFileId) const noexcept
    {
        const UNICODE_STRING relative = RelativePath(mapping, ntPath, PathKind::Nt);
        BrokerClient::Call call(_client, HookIpc::MessageType::Delete);
        HookIpc::PathRequest request{};
        request.MappingIndex = static_cast<uint16_t>(mapping);
        request.Reserved = static_cast<uint16_t>((keepItem ? HookIpc::DeleteFlagKeepItem : 0)
            | (expectedFileId != 0 ? HookIpc::DeleteFlagExpectFileId : 0));
        call.Put(&request, sizeof(request));
        call.PutString(relative.Buffer, relative.Length);
        if (expectedFileId != 0)
        {
            call.Put(&expectedFileId, sizeof(expectedFileId));
        }
        BrokerReply reply;
        return call.Send(reply);
    }

    NTSTATUS BrokerResolver::Resolve(int mapping, const UNICODE_STRING& path, PathKind kind, const OpenIntent& intent,
        UNICODE_STRING& realPath, HookIpc::FileInfo* info, uint64_t* writeToken) const noexcept
    {
        return ResolveRelative(mapping, RelativePath(mapping, path, kind), intent, realPath, info, writeToken);
    }

    NTSTATUS BrokerResolver::Stat(int mapping, const UNICODE_STRING& path, PathKind kind, HookIpc::FileInfo& info) const noexcept
    {
        const UNICODE_STRING relative = RelativePath(mapping, path, kind);
        BrokerClient::Call call(_client, HookIpc::MessageType::Stat);
        HookIpc::PathRequest request{};
        request.MappingIndex = static_cast<uint16_t>(mapping);
        call.Put(&request, sizeof(request));
        call.PutString(relative.Buffer, relative.Length);
        BrokerReply reply;
        const NTSTATUS status = call.Send(reply);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        MessageReader reader(reply.Body, reply.BodySize);
        return reader.Read(&info, sizeof(info)) ? STATUS_SUCCESS : STATUS_INVALID_NETWORK_RESPONSE;
    }

    NTSTATUS BrokerResolver::List(int mapping, const UNICODE_STRING& ntPath, uint32_t startIndex, BYTE* buffer,
        DirectoryPage& page) const noexcept
    {
        const UNICODE_STRING relative = RelativePath(mapping, ntPath, PathKind::Nt);
        BrokerClient::Call call(_client, HookIpc::MessageType::List);
        HookIpc::ListRequest request{};
        request.MappingIndex = static_cast<uint16_t>(mapping);
        request.StartIndex = startIndex;
        call.Put(&request, sizeof(request));
        call.PutString(relative.Buffer, relative.Length);
        BrokerReply reply;
        const NTSTATUS status = call.Send(reply);
        if (!NT_SUCCESS(status))
        {
            return status;
        }
        MessageReader reader(reply.Body, reply.BodySize);
        HookIpc::ListReply header{};
        if (!reader.Read(&header, sizeof(header)))
        {
            return STATUS_INVALID_NETWORK_RESPONSE;
        }
        // Validate every entry now so the consumer can walk them without bounds checks.
        MessageReader entries(reader.Position(), reader.Remaining());
        for (USHORT i = 0; i < header.Count; ++i)
        {
            HookIpc::FileInfo info{};
            const BYTE* name = nullptr;
            USHORT chars = 0;
            if (!entries.Read(&info, sizeof(info)) || !entries.ReadString(name, chars) || chars == 0)
            {
                return STATUS_INVALID_NETWORK_RESPONSE;
            }
        }
        page.Bytes = reader.Remaining() - entries.Remaining();
        memcpy(buffer, reader.Position(), page.Bytes);
        page.Entries = buffer;
        page.Count = header.Count;
        page.HasMore = (header.Flags & HookIpc::ListFlagHasMore) != 0;
        return STATUS_SUCCESS;
    }

    namespace
    {
        BrokerClient::Connection* ToConnection(WatchChannel* channel) noexcept
        {
            return reinterpret_cast<BrokerClient::Connection*>(channel);
        }
    }

    NTSTATUS BrokerResolver::OpenWatchChannel(WatchChannel*& channel) const noexcept
    {
        BrokerClient::Connection* connection = nullptr;
        const NTSTATUS status = _client.OpenChannel(connection);
        channel = NT_SUCCESS(status) ? reinterpret_cast<WatchChannel*>(connection) : nullptr;
        return status;
    }

    NTSTATUS BrokerResolver::Watch(int mapping, const UNICODE_STRING& ntPath, bool subtree, ULONG completionFilter,
        uint32_t& watchId) const noexcept
    {
        watchId = 0;
        const UNICODE_STRING relative = RelativePath(mapping, ntPath, PathKind::Nt);
        BrokerClient::Call call(_client, HookIpc::MessageType::WatchDirectory);
        HookIpc::WatchRequest request{};
        request.MappingIndex = static_cast<uint16_t>(mapping);
        request.Flags = subtree ? HookIpc::WatchFlagSubtree : 0;
        request.CompletionFilter = completionFilter;
        call.Put(&request, sizeof(request));
        call.PutString(relative.Buffer, relative.Length);
        BrokerReply reply;
        NTSTATUS status = call.Send(reply);
        if (NT_SUCCESS(status))
        {
            HookIpc::WatchReply answer{};
            MessageReader reader(reply.Body, reply.BodySize);
            if (reader.Read(&answer, sizeof(answer)) && answer.WatchId != 0)
            {
                watchId = answer.WatchId;
            }
            else
            {
                status = STATUS_INVALID_NETWORK_RESPONSE;
            }
        }
        return status;
    }

    NTSTATUS BrokerResolver::WaitAnyChanges(WatchChannel*& channel, BYTE* buffer, ULONG& bytes) const noexcept
    {
        bytes = 0;
        BrokerClient::Connection* connection = ToConnection(channel);
        if (connection == nullptr)
        {
            const NTSTATUS opened = _client.OpenChannel(connection);
            if (!NT_SUCCESS(opened))
            {
                return opened;
            }
            channel = reinterpret_cast<WatchChannel*>(connection);
        }
        NTSTATUS status = STATUS_SUCCESS;
        bool healthy = true;
        {
            BrokerClient::Call call(_client, HookIpc::MessageType::WaitAnyChanges, connection);
            HookIpc::WaitAnyChangesRequest request{};
            request.TimeoutMilliseconds = HookIpc::InfiniteTimeout;
            call.Put(&request, sizeof(request));
            BrokerReply reply;
            status = call.Send(reply, nullptr, INFINITE);
            healthy = call.Healthy();
            if (NT_SUCCESS(status))
            {
                memcpy(buffer, reply.Body, reply.BodySize);
                bytes = reply.BodySize;
            }
        }
        if (!healthy)
        {
            // A broken instance is unusable. Open the next one before closing it: the Broker drops the watches of
            // this process once its last pipe closes.
            BrokerClient::Connection* fresh = nullptr;
            channel = NT_SUCCESS(_client.OpenChannel(fresh)) ? reinterpret_cast<WatchChannel*>(fresh) : nullptr;
            BrokerClient::CloseChannel(connection);
        }
        return status;
    }

    void BrokerResolver::Unwatch(uint32_t watchId) const noexcept
    {
        if (watchId == 0)
        {
            return;
        }
        BrokerClient::Call call(_client, HookIpc::MessageType::Unwatch);
        HookIpc::UnwatchRequest request{};
        request.WatchId = watchId;
        call.Put(&request, sizeof(request));
        BrokerReply reply;
        call.Send(reply);
    }
}
