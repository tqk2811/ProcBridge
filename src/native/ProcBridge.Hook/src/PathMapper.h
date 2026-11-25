#pragma once
#include "NtApi.h"
#include <ProcBridge/HookIpc.h>

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

    /// Arguments of the open a path is resolved for (NtCreateFile semantics).
    struct OpenIntent
    {
        ACCESS_MASK DesiredAccess;
        ULONG CreateDisposition;
        ULONG CreateOptions;
        ULONG FileAttributes = 0; // given to a file the open creates
    };

    /// How a path given to the resolver is spelled.
    enum class PathKind
    {
        Nt,     // \??\D:\Work\virtual\a.txt, matched by FindMapping
        Device, // \Device\HarddiskVolume3\Work\virtual\a.txt, matched by FindMappingByDevicePath
    };

    /// Changes of one WaitChanges reply: Count x (HookIpc::ChangeEntry, String Name) in Entries (see HookIpc.h).
    struct ChangeBatch
    {
        const BYTE* Entries;
        ULONG Bytes;
        USHORT Count;
        bool Overflow; // changes were lost: the notify completes with STATUS_NOTIFY_ENUM_DIR
    };

    /// Pipe instance of one directory watch, owned by the resolver (opaque to the hooks).
    struct WatchChannel;

    /// One page of a directory listing: Count x (FileInfo, String Name) as in a HookIpc List reply.
    struct DirectoryPage
    {
        const BYTE* Entries;
        ULONG Bytes;
        USHORT Count;
        bool HasMore;
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

        /// Number of mappings (valid indexes are 0..Count()-1).
        virtual int Count() const noexcept = 0;

        /// True when a virtual root lies inside directoryDevicePath (\Device\...\dir) or contains it, i.e.
        /// a name relative to that directory may be virtual. Cheap: decides whether joining is needed.
        virtual bool IsNearVirtualRoot(const UNICODE_STRING& directoryDevicePath) const noexcept = 0;

        /// Like FindMapping, for an NT device path (\Device\HarddiskVolume3\...).
        virtual int FindMappingByDevicePath(const UNICODE_STRING& devicePath) const noexcept = 0;

        /// Like TranslateToReal, for a device path matched by FindMappingByDevicePath.
        virtual bool TranslateDeviceToReal(int mapping, const UNICODE_STRING& devicePath, UNICODE_STRING& realPath) const noexcept = 0;

        /// Makes the backing file of path available for an open with these arguments and builds its NT path
        /// (freed by the caller with FreeString). Returns the NTSTATUS the hooked call fails with otherwise.
        /// info (optional) receives machine B's metadata when the resolver has it (left untouched otherwise);
        /// writeToken (optional) the Broker's write token of an open that may change content (0 when none, M3).
        /// M1 (local folder): a plain translation.
        virtual NTSTATUS Resolve(int mapping, const UNICODE_STRING& path, PathKind kind, const OpenIntent& /*intent*/,
            UNICODE_STRING& realPath, HookIpc::FileInfo* /*info*/, uint64_t* writeToken = nullptr) const noexcept
        {
            if (writeToken != nullptr)
            {
                *writeToken = 0;
            }
            const bool translated = kind == PathKind::Nt ? TranslateToReal(mapping, path, realPath)
                                                         : TranslateDeviceToReal(mapping, path, realPath);
            return translated ? STATUS_SUCCESS : STATUS_NO_MEMORY;
        }

        /// True when metadata and listings come from machine B (Stat/List below), not from the backing folder.
        virtual bool IsRemote() const noexcept { return false; }

        /// Metadata of path without downloading it. STATUS_NOT_SUPPORTED for a local resolver.
        virtual NTSTATUS Stat(int /*mapping*/, const UNICODE_STRING& /*path*/, PathKind /*kind*/, HookIpc::FileInfo& /*info*/) const noexcept
        {
            return STATUS_NOT_SUPPORTED;
        }

        /// One page of the listing of the directory ntPath, starting at entry startIndex. The entries live in
        /// buffer (HookIpc::MaxMessageSize bytes). STATUS_NOT_SUPPORTED for a local resolver.
        virtual NTSTATUS List(int /*mapping*/, const UNICODE_STRING& /*ntPath*/, uint32_t /*startIndex*/, BYTE* /*buffer*/,
            DirectoryPage& /*page*/) const noexcept
        {
            return STATUS_NOT_SUPPORTED;
        }

        // Write operations of a remote resolver (M3, plan 4.5). ntPath arguments are virtual NT paths matched by
        // FindMapping. A local resolver never gets these calls (the backing folder is changed directly).

        /// The last handle holding writeToken was closed (Close) or flushed (Flush): write back if changed.
        virtual NTSTATUS CloseWrite(uint64_t /*writeToken*/) const noexcept { return STATUS_NOT_SUPPORTED; }
        virtual NTSTATUS FlushWrite(uint64_t /*writeToken*/) const noexcept { return STATUS_NOT_SUPPORTED; }
        /// A child inherited a handle holding writeToken: the Broker then expects one more CloseWrite (the child's).
        virtual NTSTATUS RetainWrite(uint64_t /*writeToken*/) const noexcept { return STATUS_NOT_SUPPORTED; }

        /// True when every write on a handle with a write token must be followed by FlushWrite.
        virtual bool IsWriteThrough() const noexcept { return false; }

        /// Applies the fields set in HookIpc::SetInfoRequest::Fields on machine B; info receives the new metadata.
        virtual NTSTATUS SetInfo(int /*mapping*/, const UNICODE_STRING& /*ntPath*/, const HookIpc::SetInfoRequest& /*request*/,
            HookIpc::FileInfo& /*info*/) const noexcept
        {
            return STATUS_NOT_SUPPORTED;
        }

        /// Renames on machine B; newRealPath (freed with FreeString) is the NT path the cache handle must be renamed to.
        virtual NTSTATUS Rename(int /*mapping*/, const UNICODE_STRING& /*ntPath*/, int /*newMapping*/, const UNICODE_STRING& /*newNtPath*/,
            bool /*replaceIfExists*/, HookIpc::FileInfo& /*info*/, UNICODE_STRING& /*newRealPath*/) const noexcept
        {
            return STATUS_NOT_SUPPORTED;
        }

        /// Deletes on machine B. keepItem: the caller deletes the cache copy itself through its handle. expectedFileId
        /// (non-zero): machine B's file id the handle was opened on; another file now under the name is left alone.
        virtual NTSTATUS Delete(int /*mapping*/, const UNICODE_STRING& /*ntPath*/, bool /*keepItem*/,
            uint64_t /*expectedFileId*/) const noexcept
        {
            return STATUS_NOT_SUPPORTED;
        }

        // Directory watches of a remote resolver (M7, NtNotifyChangeDirectoryFile[Ex]).

        /// Starts watching the virtual directory ntPath on machine B. channel receives the watch's own pipe instance,
        /// which keeps the watch alive and carries its long polls; give it back with Unwatch.
        virtual NTSTATUS Watch(int /*mapping*/, const UNICODE_STRING& /*ntPath*/, bool /*subtree*/, ULONG /*completionFilter*/,
            WatchChannel*& channel, uint32_t& watchId) const noexcept
        {
            channel = nullptr;
            watchId = 0;
            return STATUS_NOT_SUPPORTED;
        }

        /// Waits until watch watchId has changes (long poll on channel; reopened when it was dropped). The changes
        /// live in buffer (HookIpc::MaxMessageSize bytes). A signalled cancelEvent abandons the wait with
        /// STATUS_CANCELLED (changes the Broker sent meanwhile are lost).
        virtual NTSTATUS WaitChanges(WatchChannel*& /*channel*/, uint32_t /*watchId*/, HANDLE /*cancelEvent*/, BYTE* /*buffer*/,
            ChangeBatch& /*batch*/) const noexcept
        {
            return STATUS_NOT_SUPPORTED;
        }

        /// Removes watch watchId on machine B, then closes its channel (may be null).
        virtual void Unwatch(WatchChannel* /*channel*/, uint32_t /*watchId*/) const noexcept {}
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

        int Count() const noexcept override { return _count; }

        int FindMapping(const UNICODE_STRING& ntPath) const noexcept override;
        bool TranslateToReal(int mapping, const UNICODE_STRING& ntPath, UNICODE_STRING& realPath) const noexcept override;
        const PathMapping& GetMapping(int mapping) const noexcept override { return _mappings[mapping]; }
        bool IsNearVirtualRoot(const UNICODE_STRING& directoryDevicePath) const noexcept override;
        int FindMappingByDevicePath(const UNICODE_STRING& devicePath) const noexcept override;
        bool TranslateDeviceToReal(int mapping, const UNICODE_STRING& devicePath, UNICODE_STRING& realPath) const noexcept override;

        /// Adds "virtual DOS folder -> real DOS folder"; the Broker resolver passes the cache folder as real.
        DWORD AddMapping(const wchar_t* virtualDos, const wchar_t* realDos) noexcept;

    private:

        PathMapping _mappings[MaxMappings] = {};
        int _count = 0;
    };
}
