#pragma once
#include "BrokerClient.h"
#include "PathMapper.h"

namespace ProcBridge::Hook
{
    /// M2 resolver: virtual folders of machine B served by the Broker (plan 4.4).
    /// Path matching and name rewriting stay local (a PathMapper from each virtual root to its cache folder,
    /// learnt at start-up from Hello and one Resolve of every root); opening asks the Broker to bring the
    /// file into the cache (Resolve), attributes and listings come from machine B (Stat, List).
    /// Configuration: PROCBRIDGE_SESSION (pipe name suffix), PROCBRIDGE_BROKER_PID (process that must own the
    /// pipe), optional PROCBRIDGE_IPC_TIMEOUT_MS.
    class BrokerResolver final : public IPathResolver
    {
    public:
        static constexpr const wchar_t* SessionVariable = L"PROCBRIDGE_SESSION";
        static constexpr const wchar_t* BrokerProcessVariable = L"PROCBRIDGE_BROKER_PID";
        static constexpr const wchar_t* TimeoutVariable = L"PROCBRIDGE_IPC_TIMEOUT_MS";
        static constexpr DWORD DefaultTimeoutMs = 60000;

        /// True when the environment names a Broker session.
        static bool IsConfigured() noexcept;

        /// Connects, checks the pipe owner, sends Hello and resolves every virtual root.
        DWORD Initialize() noexcept;

        int Count() const noexcept { return _paths.Count(); }

        int FindMapping(const UNICODE_STRING& ntPath) const noexcept override { return _paths.FindMapping(ntPath); }
        bool TranslateToReal(int mapping, const UNICODE_STRING& ntPath, UNICODE_STRING& realPath) const noexcept override
        {
            return _paths.TranslateToReal(mapping, ntPath, realPath);
        }
        const PathMapping& GetMapping(int mapping) const noexcept override { return _paths.GetMapping(mapping); }
        bool IsNearVirtualRoot(const UNICODE_STRING& directoryDevicePath) const noexcept override
        {
            return _paths.IsNearVirtualRoot(directoryDevicePath);
        }
        int FindMappingByDevicePath(const UNICODE_STRING& devicePath) const noexcept override
        {
            return _paths.FindMappingByDevicePath(devicePath);
        }
        bool TranslateDeviceToReal(int mapping, const UNICODE_STRING& devicePath, UNICODE_STRING& realPath) const noexcept override
        {
            return _paths.TranslateDeviceToReal(mapping, devicePath, realPath);
        }

        NTSTATUS Resolve(int mapping, const UNICODE_STRING& path, PathKind kind, const OpenIntent& intent,
            UNICODE_STRING& realPath, HookIpc::FileInfo* info) const noexcept override;
        bool IsRemote() const noexcept override { return true; }
        NTSTATUS Stat(int mapping, const UNICODE_STRING& path, PathKind kind, HookIpc::FileInfo& info) const noexcept override;
        NTSTATUS List(int mapping, const UNICODE_STRING& ntPath, uint32_t startIndex, BYTE* buffer,
            DirectoryPage& page) const noexcept override;

    private:
        /// View of path below the virtual root ('\' separated, no leading '\'; empty for the root itself).
        UNICODE_STRING RelativePath(int mapping, const UNICODE_STRING& path, PathKind kind) const noexcept;

        NTSTATUS ResolveRelative(int mapping, const UNICODE_STRING& relative, const OpenIntent& intent,
            UNICODE_STRING& realPath, HookIpc::FileInfo* info) const noexcept;

        PathMapper _paths;
        // Calls are const for IPathResolver; the pool is internally synchronized.
        mutable BrokerClient _client;
    };
}
