#pragma once
#include "NtApi.h"

namespace ProcBridge::Hook
{
    /// One virtual root and the folder backing it. All strings are NT paths without trailing '\'.
    struct PathMapping
    {
        UNICODE_STRING VirtualNt;             // \??\D:\Work\virtual
        UNICODE_STRING RealNt;                // \??\D:\Cache\real
        UNICODE_STRING VirtualVolumeRelative; // \Work\virtual (empty when not a drive-letter path)
        UNICODE_STRING RealVolumeRelative;    // \Cache\real
        UNICODE_STRING VirtualDevicePath;     // \Device\HarddiskVolume3\Work\virtual (empty if unknown)
        UNICODE_STRING RealDevicePath;        // \Device\HarddiskVolume3\Cache\real
    };

    /// Decides whether an NT path is virtual and where its backing file lives.
    /// M1: backed by a local folder (PathMapper). M2: implemented by the Broker client, which returns
    /// the cache path after making sure the file is downloaded.
    class IPathResolver
    {
    public:
        virtual ~IPathResolver() = default;

        /// Index of the mapping whose virtual root contains ntPath, or -1. Must stay cheap: it runs
        /// for every path-based call in the process.
        virtual int FindMapping(const UNICODE_STRING& ntPath) const noexcept = 0;

        /// Builds the backing NT path for ntPath. The caller frees realPath with FreeString.
        virtual bool TranslateToReal(int mapping, const UNICODE_STRING& ntPath, UNICODE_STRING& realPath) const noexcept = 0;

        virtual const PathMapping& GetMapping(int mapping) const noexcept = 0;

        /// True when a virtual root lies inside directoryDevicePath (\Device\...\dir) or contains it, i.e.
        /// a name relative to that directory may be virtual. Cheap: decides whether joining is needed.
        virtual bool IsNearVirtualRoot(const UNICODE_STRING& directoryDevicePath) const noexcept = 0;

        /// Like FindMapping, for an NT device path (\Device\HarddiskVolume3\...).
        virtual int FindMappingByDevicePath(const UNICODE_STRING& devicePath) const noexcept = 0;

        /// Like TranslateToReal, for a device path matched by FindMappingByDevicePath.
        virtual bool TranslateDeviceToReal(int mapping, const UNICODE_STRING& devicePath, UNICODE_STRING& realPath) const noexcept = 0;
    };

    /// Mappings read from the PROCBRIDGE_MAP environment variable:
    /// "<virtual dir>*<real dir>|<virtual dir>*<real dir>..." ('*' and '|' cannot appear in paths).
    class PathMapper final : public IPathResolver
    {
    public:
        static constexpr const wchar_t* EnvironmentVariable = L"PROCBRIDGE_MAP";
        static constexpr int MaxMappings = 16;

        /// Returns ERROR_SUCCESS (also when the variable is missing: no mappings) or a Win32 error.
        DWORD LoadFromEnvironment() noexcept;

        int Count() const noexcept { return _count; }

        int FindMapping(const UNICODE_STRING& ntPath) const noexcept override;
        bool TranslateToReal(int mapping, const UNICODE_STRING& ntPath, UNICODE_STRING& realPath) const noexcept override;
        const PathMapping& GetMapping(int mapping) const noexcept override { return _mappings[mapping]; }
        bool IsNearVirtualRoot(const UNICODE_STRING& directoryDevicePath) const noexcept override;
        int FindMappingByDevicePath(const UNICODE_STRING& devicePath) const noexcept override;
        bool TranslateDeviceToReal(int mapping, const UNICODE_STRING& devicePath, UNICODE_STRING& realPath) const noexcept override;

    private:
        DWORD AddMapping(const wchar_t* virtualDos, const wchar_t* realDos) noexcept;

        PathMapping _mappings[MaxMappings] = {};
        int _count = 0;
    };
}
