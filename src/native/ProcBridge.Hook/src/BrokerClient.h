#pragma once
// Named pipe client of the Broker (plan section 6, format in ProcBridge/HookIpc.h).
#include "NtApi.h"
#include <ProcBridge/HookIpc.h>

namespace ProcBridge::Hook
{
    /// Reads a message body field by field. Strings are unaligned in the message, so every field is
    /// copied out with memcpy; Remaining()/Position() let the caller copy string text the same way.
    class MessageReader final
    {
    public:
        MessageReader(const BYTE* data, ULONG size) noexcept : _data(data), _size(size) {}

        bool Read(void* destination, ULONG bytes) noexcept;

        /// Reads the uint16 character count of a string and returns a pointer to its (unaligned) text.
        bool ReadString(const BYTE*& text, USHORT& chars) noexcept;

        const BYTE* Position() const noexcept { return _data + _offset; }
        ULONG Remaining() const noexcept { return _size - _offset; }

    private:
        const BYTE* _data;
        ULONG _size;
        ULONG _offset = 0;
    };

    /// One reply of the Broker: the body lives in the client's buffer until Release.
    struct BrokerReply
    {
        const BYTE* Body = nullptr;
        ULONG BodySize = 0;
    };

    /// Pool of pipe instances to \\.\pipe\ProcBridge-<session>. One request then one reply per instance, so
    /// each call takes an idle instance (or opens a new one) and gives it back afterwards: threads run their
    /// calls in parallel on separate instances. Every new instance is checked against the Broker's process id
    /// before it is used.
    class BrokerClient final
    {
    public:
        class Call;
        /// A pipe instance. Pooled ones are hidden in Call; a channel (OpenChannel) belongs to one caller.
        struct Connection;

        BrokerClient() noexcept = default;
        /// Lives as long as the process: its pipes are closed by the system at exit. Closing them from a global
        /// destructor would run NtClose through the hooks after other globals are already destroyed.
        ~BrokerClient() = default;
        BrokerClient(const BrokerClient&) = delete;
        BrokerClient& operator=(const BrokerClient&) = delete;

        /// pipeName is the full \\.\pipe\... name; brokerProcessId the process that must own the pipe.
        DWORD Initialize(const wchar_t* pipeName, DWORD brokerProcessId, DWORD timeoutMs) noexcept;

        /// Changes the per-call timeout; only while no other thread uses the client (end of start-up).
        void SetTimeout(DWORD timeoutMs) noexcept { _timeoutMs = timeoutMs; }

        /// Opens a pipe instance outside the pool for one caller (a directory watch and its WaitChanges long polls,
        /// plan section 10): a long poll never holds an instance other requests wait for, and the open channel keeps
        /// this process known to the Broker while the watch lives. Close it with CloseChannel.
        NTSTATUS OpenChannel(Connection*& channel) noexcept { return Open(channel); }
        static void CloseChannel(Connection* channel) noexcept
        {
            if (channel != nullptr)
            {
                Destroy(channel);
            }
        }

    private:

        static constexpr int MaxIdle = 8;

        /// pooled: the instance came from the idle pool (the Broker may have dropped it meanwhile).
        NTSTATUS Acquire(Connection*& connection, bool& pooled) noexcept;
        void Release(Connection* connection, bool healthy) noexcept;
        NTSTATUS Open(Connection*& connection) noexcept;
        static void Destroy(Connection* connection) noexcept;

        wchar_t* _pipeName = nullptr;
        DWORD _brokerProcessId = 0;
        DWORD _timeoutMs = 0;
        SRWLOCK _lock = SRWLOCK_INIT;
        Connection* _idle[MaxIdle] = {};
        int _idleCount = 0;
        volatile LONG _nextRequestId = 0;

        friend class Call;
    };

    /// One request/reply exchange. Build the body with the Put* methods, then Send; the reply body stays valid
    /// until the Call is destroyed.
    class BrokerClient::Call final
    {
    public:
        Call(BrokerClient& client, HookIpc::MessageType type) noexcept;
        /// Runs the exchange on channel (from OpenChannel), which stays with the caller afterwards.
        Call(BrokerClient& client, HookIpc::MessageType type, Connection* channel) noexcept;
        ~Call();
        Call(const Call&) = delete;
        Call& operator=(const Call&) = delete;

        void Put(const void* data, ULONG bytes) noexcept;
        /// Appends a HookIpc string (uint16 count + UTF-16 text).
        void PutString(const wchar_t* text, ULONG bytes) noexcept;

        /// Returns the NTSTATUS of the exchange: the reply status, STATUS_IO_TIMEOUT when the Broker does not
        /// answer in time, or a network status when the pipe fails.
        NTSTATUS Send(BrokerReply& reply) noexcept { return Send(reply, nullptr, _client._timeoutMs); }

        /// Like Send, waiting at most timeoutMs (INFINITE allowed); a signalled cancelEvent (optional) abandons the
        /// exchange with STATUS_CANCELLED. Either way the instance is then unusable (see Healthy).
        NTSTATUS Send(BrokerReply& reply, HANDLE cancelEvent, DWORD timeoutMs) noexcept;

        /// False when the pipe instance is in an unknown state after Send: a channel must then be closed.
        bool Healthy() const noexcept { return _healthy; }

    private:
        BrokerClient& _client;
        Connection* _connection = nullptr;
        HookIpc::MessageType _type;
        NTSTATUS _status = STATUS_SUCCESS;
        ULONG _size = HookIpc::HeaderSize;
        bool _healthy = true;
        bool _pooled = false;
        bool _channel = false;
    };
}
