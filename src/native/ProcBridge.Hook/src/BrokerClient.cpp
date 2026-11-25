#include "BrokerClient.h"
#include <cstring>

namespace ProcBridge::Hook
{
    namespace
    {
        // Customer bit of an NTSTATUS: ProcBridge's own codes. The Broker maps them already; a value that
        // still has it must not reach the process.
        constexpr ULONG CustomerBit = 0x20000000;

        NTSTATUS ToProcessStatus(ULONG status) noexcept
        {
            return (status & CustomerBit) != 0 ? STATUS_UNEXPECTED_NETWORK_ERROR : static_cast<NTSTATUS>(status);
        }
    }

    bool MessageReader::Read(void* destination, ULONG bytes) noexcept
    {
        if (bytes > Remaining())
        {
            return false;
        }
        memcpy(destination, _data + _offset, bytes);
        _offset += bytes;
        return true;
    }

    bool MessageReader::ReadString(const BYTE*& text, USHORT& chars) noexcept
    {
        if (!Read(&chars, sizeof(chars)) || static_cast<ULONG>(chars) * sizeof(wchar_t) > Remaining())
        {
            return false;
        }
        text = _data + _offset;
        _offset += static_cast<ULONG>(chars) * sizeof(wchar_t);
        return true;
    }

    struct BrokerClient::Connection
    {
        HANDLE Pipe;
        HANDLE Event;
        BYTE Request[HookIpc::MaxMessageSize];
        BYTE Reply[HookIpc::MaxMessageSize];
    };

    DWORD BrokerClient::Initialize(const wchar_t* pipeName, DWORD brokerProcessId, DWORD timeoutMs) noexcept
    {
        const size_t bytes = (static_cast<size_t>(lstrlenW(pipeName)) + 1) * sizeof(wchar_t);
        _pipeName = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, bytes));
        if (_pipeName == nullptr)
        {
            return ERROR_NOT_ENOUGH_MEMORY;
        }
        memcpy(_pipeName, pipeName, bytes);
        _brokerProcessId = brokerProcessId;
        _timeoutMs = timeoutMs;
        // Open one instance now: a wrong or missing Broker fails the initialization instead of every call.
        Connection* connection = nullptr;
        const NTSTATUS status = Open(connection);
        if (!NT_SUCCESS(status))
        {
            return status == STATUS_ACCESS_DENIED ? ERROR_ACCESS_DENIED : ERROR_PIPE_NOT_CONNECTED;
        }
        Release(connection, true);
        return ERROR_SUCCESS;
    }

    NTSTATUS BrokerClient::Open(Connection*& connection) noexcept
    {
        connection = nullptr;
        auto* created = static_cast<Connection*>(HeapAlloc(GetProcessHeap(), 0, sizeof(Connection)));
        if (created == nullptr)
        {
            return STATUS_NO_MEMORY;
        }
        created->Pipe = INVALID_HANDLE_VALUE;
        created->Event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (created->Event == nullptr)
        {
            Destroy(created);
            return STATUS_NO_MEMORY;
        }

        const ULONGLONG deadline = GetTickCount64() + _timeoutMs;
        while (true)
        {
            // Identification level only: a server that is not the Broker cannot act with our token.
            created->Pipe = CreateFileW(_pipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
            if (created->Pipe != INVALID_HANDLE_VALUE)
            {
                break;
            }
            const DWORD error = GetLastError(); // before Destroy, which may change it
            const ULONGLONG now = GetTickCount64();
            if (error != ERROR_PIPE_BUSY || now >= deadline)
            {
                Destroy(created);
                return error == ERROR_PIPE_BUSY ? STATUS_IO_TIMEOUT : STATUS_NETWORK_UNREACHABLE;
            }
            // WaitNamedPipeW(0) would mean the pipe's default wait: wait at least 1 ms.
            const ULONGLONG left = deadline - now;
            WaitNamedPipeW(_pipeName, left > 0 ? static_cast<DWORD>(left) : 1);
        }

        ULONG serverProcessId = 0;
        DWORD mode = PIPE_READMODE_MESSAGE;
        if (!GetNamedPipeServerProcessId(created->Pipe, &serverProcessId) || serverProcessId != _brokerProcessId)
        {
            OutputDebugStringW(L"ProcBridge.Hook: pipe server is not the Broker\n");
            Destroy(created);
            return STATUS_ACCESS_DENIED;
        }
        if (!SetNamedPipeHandleState(created->Pipe, &mode, nullptr, nullptr))
        {
            Destroy(created);
            return STATUS_NETWORK_UNREACHABLE;
        }
        connection = created;
        return STATUS_SUCCESS;
    }

    void BrokerClient::Destroy(Connection* connection) noexcept
    {
        if (connection->Pipe != INVALID_HANDLE_VALUE)
        {
            CloseHandle(connection->Pipe);
        }
        if (connection->Event != nullptr)
        {
            CloseHandle(connection->Event);
        }
        HeapFree(GetProcessHeap(), 0, connection);
    }

    NTSTATUS BrokerClient::Acquire(Connection*& connection, bool& pooled) noexcept
    {
        AcquireSRWLockExclusive(&_lock);
        connection = _idleCount > 0 ? _idle[--_idleCount] : nullptr;
        ReleaseSRWLockExclusive(&_lock);
        pooled = connection != nullptr;
        return pooled ? STATUS_SUCCESS : Open(connection);
    }

    void BrokerClient::Release(Connection* connection, bool healthy) noexcept
    {
        if (healthy)
        {
            AcquireSRWLockExclusive(&_lock);
            if (_idleCount < MaxIdle)
            {
                _idle[_idleCount++] = connection;
                connection = nullptr;
            }
            ReleaseSRWLockExclusive(&_lock);
        }
        if (connection != nullptr)
        {
            Destroy(connection);
        }
    }

    BrokerClient::Call::Call(BrokerClient& client, HookIpc::MessageType type) noexcept
        : _client(client), _type(type)
    {
        _status = _client.Acquire(_connection, _pooled);
    }

    BrokerClient::Call::Call(BrokerClient& client, HookIpc::MessageType type, Connection* channel) noexcept
        : _client(client), _connection(channel), _type(type), _channel(true)
    {
        _status = channel != nullptr ? STATUS_SUCCESS : STATUS_INVALID_PARAMETER;
    }

    BrokerClient::Call::~Call()
    {
        if (_connection != nullptr && !_channel)
        {
            _client.Release(_connection, _healthy);
        }
    }

    void BrokerClient::Call::Put(const void* data, ULONG bytes) noexcept
    {
        if (_connection == nullptr)
        {
            return;
        }
        if (bytes > HookIpc::MaxMessageSize - _size)
        {
            _status = STATUS_NAME_TOO_LONG;
            return;
        }
        memcpy(_connection->Request + _size, data, bytes);
        _size += bytes;
    }

    void BrokerClient::Call::PutString(const wchar_t* text, ULONG bytes) noexcept
    {
        if (bytes / sizeof(wchar_t) > 0xFFFF)
        {
            _status = STATUS_NAME_TOO_LONG;
            return;
        }
        const USHORT chars = static_cast<USHORT>(bytes / sizeof(wchar_t));
        Put(&chars, sizeof(chars));
        Put(text, bytes);
    }

    NTSTATUS BrokerClient::Call::Send(BrokerReply& reply, HANDLE cancelEvent, DWORD timeoutMs) noexcept
    {
        if (!NT_SUCCESS(_status))
        {
            return _status == STATUS_NAME_TOO_LONG ? STATUS_OBJECT_NAME_INVALID : _status;
        }
        HookIpc::MessageHeader header{};
        header.Size = _size;
        header.Version = HookIpc::ProtocolVersion;
        header.MessageType = static_cast<uint16_t>(_type);
        header.RequestId = static_cast<uint32_t>(InterlockedIncrement(&_client._nextRequestId));
        memcpy(_connection->Request, &header, sizeof(header));

        OVERLAPPED overlapped{};
        DWORD received = 0;
        BOOL done = FALSE;
        while (true)
        {
            overlapped = OVERLAPPED{};
            overlapped.hEvent = _connection->Event;
            ResetEvent(_connection->Event);
            done = TransactNamedPipe(_connection->Pipe, _connection->Request, _size, _connection->Reply,
                HookIpc::MaxMessageSize, nullptr, &overlapped);
            const DWORD error = done ? ERROR_SUCCESS : GetLastError();
            // An idle instance the Broker has dropped (restart, its end closed) fails at once: retry once on a
            // new instance instead of failing the application's call.
            if (done || !_pooled || (error != ERROR_BROKEN_PIPE && error != ERROR_PIPE_NOT_CONNECTED && error != ERROR_NO_DATA
                && error != ERROR_BAD_PIPE))
            {
                if (!done)
                {
                    SetLastError(error);
                }
                break;
            }
            _pooled = false;
            Connection* fresh = nullptr;
            const NTSTATUS opened = _client.Open(fresh);
            if (!NT_SUCCESS(opened))
            {
                _healthy = false;
                return opened;
            }
            memcpy(fresh->Request, _connection->Request, _size);
            Destroy(_connection);
            _connection = fresh;
        }
        if (!done && GetLastError() == ERROR_IO_PENDING)
        {
            const HANDLE events[2] = { _connection->Event, cancelEvent };
            const DWORD waited = WaitForMultipleObjects(cancelEvent != nullptr ? 2 : 1, events, FALSE, timeoutMs);
            if (waited != WAIT_OBJECT_0)
            {
                // The instance is in an unknown state now: drop it.
                CancelIoEx(_connection->Pipe, &overlapped);
                GetOverlappedResult(_connection->Pipe, &overlapped, &received, TRUE);
                _healthy = false;
                return waited == WAIT_OBJECT_0 + 1 ? STATUS_CANCELLED : STATUS_IO_TIMEOUT;
            }
            done = GetOverlappedResult(_connection->Pipe, &overlapped, &received, FALSE);
        }
        else if (done)
        {
            done = GetOverlappedResult(_connection->Pipe, &overlapped, &received, FALSE);
        }
        if (!done)
        {
            _healthy = false;
            // ERROR_MORE_DATA: a reply larger than the protocol allows.
            return GetLastError() == ERROR_MORE_DATA ? STATUS_INVALID_NETWORK_RESPONSE : STATUS_NETWORK_UNREACHABLE;
        }

        HookIpc::MessageHeader answer{};
        if (received < sizeof(answer))
        {
            _healthy = false;
            return STATUS_INVALID_NETWORK_RESPONSE;
        }
        memcpy(&answer, _connection->Reply, sizeof(answer));
        if (answer.Size != received || answer.Version != HookIpc::ProtocolVersion || answer.RequestId != header.RequestId
            || (answer.Status == 0 && answer.MessageType != header.MessageType))
        {
            _healthy = false;
            return STATUS_INVALID_NETWORK_RESPONSE;
        }
        if (answer.Status != 0)
        {
            return ToProcessStatus(answer.Status);
        }
        reply.Body = _connection->Reply + sizeof(answer);
        reply.BodySize = received - static_cast<ULONG>(sizeof(answer));
        return STATUS_SUCCESS;
    }
}
