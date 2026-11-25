// ntdll file hooks for the M1 spike (plan 4.2, 4.4).
//
// Model: a virtual path is opened as the real backing file (NtCreateFile on the translated path) and
// the real handle is returned to the process. Reads, writes, mapping and directory enumeration then
// run on the real handle unchanged. Only names leak, so the hooks rewrite name queries
// (NtQueryInformationFile name classes, NtQueryObject) for handles recorded in the handle table.
//
// Directory listings (NtQueryDirectoryFile[Ex]) return leaf names only, so with a local backing
// folder they need no rewriting in M1. With the Broker (M2) a virtual directory handle is a handle to an
// empty placeholder folder: its listings are built from machine B (RemoteHandleTable), attributes
// by path come from Stat, and every open asks the Broker to bring the file into the cache first.
//
// M3 (writable share): the Broker creates missing names on machine B before the real create of the cache copy, and
// hands out a write token for opens that may change content; the last close (or a flush) of a handle with a token
// asks the Broker to write the cache copy back. Rename, delete (disposition, delete-on-close, NtDeleteFile) and
// attribute/time changes on virtual handles go to machine B first, then to the cache copy.
//
// M7: change notifications (NtNotifyChangeDirectoryFile[Ex]) on virtual directories come from machine B through the
// Broker (DirectoryWatches); closing or cancelling (NtCancelIoFile[Ex], NtCancelSynchronousIoFile) ends them.
#include "FileHooks.h"
#include "CallerMemory.h"
#include "DirectoryEntries.h"
#include "DirectoryNameCache.h"
#include "DirectoryWatches.h"
#include "HandleInheritance.h"
#include "HandleTable.h"
#include "HookGuard.h"
#include "ParentScans.h"
#include "PathString.h"
#include "RemoteHandles.h"
#include <MinHook.h>
#include <algorithm>
#include <atomic>
#include <cstring>

namespace ProcBridge::Hook
{
    namespace
    {
        using namespace Nt;

        // Largest name a file system returns (32767 characters) plus headroom.
        constexpr size_t MaxNameBytes = 0x10000;
        // Largest UNICODE_STRING text whose MaximumLength (text + NUL) still fits in a USHORT.
        constexpr size_t MaxUnicodeStringBytes = 0xFFFC;

        const IPathResolver* g_resolver = nullptr;
        HandleTable g_handles;
        DirectoryNameCache g_directoryNames;
        RemoteHandleTable g_remoteHandles;
        DirectoryWatches g_watches;
        ParentScans g_parentScans;

        using NtQueryDirectoryFileFn = NTSTATUS(NTAPI*)(HANDLE, HANDLE, PIO_APC_ROUTINE, PVOID, PIO_STATUS_BLOCK, PVOID, ULONG,
            ULONG, BOOLEAN, PUNICODE_STRING, BOOLEAN);
        using NtQueryDirectoryFileExFn = NTSTATUS(NTAPI*)(HANDLE, HANDLE, PIO_APC_ROUTINE, PVOID, PIO_STATUS_BLOCK, PVOID, ULONG,
            ULONG, ULONG, PUNICODE_STRING);
        NtQueryDirectoryFileFn g_ntQueryDirectoryFile = nullptr;
        NtQueryDirectoryFileExFn g_ntQueryDirectoryFileEx = nullptr;

        // NtCreateFile dispositions used to describe opens to the resolver.
        constexpr ULONG FileOpenDisposition = 1;
        constexpr ULONG FileCreateDisposition = 2;
        const OpenIntent ReadAttributesIntent{ FILE_READ_ATTRIBUTES | SYNCHRONIZE, FileOpenDisposition, 0 };

        NtCreateFileFn g_ntCreateFile = nullptr;
        NtOpenFileFn g_ntOpenFile = nullptr;
        NtQueryAttributesFileFn g_ntQueryAttributesFile = nullptr;
        NtQueryAttributesFileFn g_ntQueryFullAttributesFile = nullptr;
        NtQueryInformationFileFn g_ntQueryInformationFile = nullptr;
        NtQueryInformationFileFn g_ntSetInformationFile = nullptr;
        NtQueryObjectFn g_ntQueryObject = nullptr;
        NtCloseFn g_ntClose = nullptr;
        NtQueryInformationByNameFn g_ntQueryInformationByName = nullptr;
        NtDeleteFileFn g_ntDeleteFile = nullptr;
        NtDuplicateObjectFn g_ntDuplicateObject = nullptr;
        NtFlushBuffersFileFn g_ntFlushBuffersFile = nullptr;
        NtWriteFileFn g_ntWriteFile = nullptr;
        NtNotifyChangeDirectoryFileFn g_ntNotifyChangeDirectoryFile = nullptr;
        NtNotifyChangeDirectoryFileExFn g_ntNotifyChangeDirectoryFileEx = nullptr;
        NtCancelIoFileFn g_ntCancelIoFile = nullptr;
        NtCancelIoFileExFn g_ntCancelIoFileEx = nullptr;
        NtCancelSynchronousIoFileFn g_ntCancelSynchronousIoFile = nullptr;
        NtReadFileFn g_ntReadFile = nullptr;
        NtReadFileScatterFn g_ntReadFileScatter = nullptr;
        NtCreateSectionFn g_ntCreateSection = nullptr;
        NtCreateSectionExFn g_ntCreateSectionEx = nullptr;
        NtLockFileFn g_ntLockFile = nullptr;
        NtUnlockFileFn g_ntUnlockFile = nullptr;
        // M8: set once a ranged cache copy was opened (or inherited); until then reads and mappings skip the table.
        std::atomic<bool> g_anyRange{ false };

        /// Calls visitor(const UNICODE_STRING& name) with the NT device name of a real directory handle
        /// (\Device\HarddiskVolume3\dir, no trailing '\'; empty when it cannot be read), cached per handle.
        template <typename Visitor>
        void VisitDirectoryName(HANDLE directory, Visitor&& visitor) noexcept
        {
            if (g_directoryNames.Visit(directory, visitor))
            {
                return;
            }
            const size_t bufferBytes = sizeof(ObjectNameInfo) + MaxNameBytes;
            HeapBuffer buffer(bufferBytes);
            UNICODE_STRING name{};
            if (buffer)
            {
                const auto* info = reinterpret_cast<const ObjectNameInfo*>(buffer.Get());
                const NTSTATUS status = g_ntQueryObject(directory, ObjectNameInformation, buffer.Get(), static_cast<ULONG>(bufferBytes), nullptr);
                if (NT_SUCCESS(status) && info->Name.Buffer != nullptr)
                {
                    name = info->Name;
                    while (name.Length > 0 && name.Buffer[name.Length / sizeof(wchar_t) - 1] == L'\\')
                    {
                        name.Length -= sizeof(wchar_t);
                    }
                }
                g_directoryNames.Add(directory, name.Buffer, name.Length);
            }
            visitor(static_cast<const UNICODE_STRING&>(name));
        }

        /// Resolves OBJECT_ATTRIBUTES against the virtual roots and owns the rewritten name.
        /// The caller's structures are read once into a snapshot (SEH-guarded); an unreadable pointer
        /// makes Error() STATUS_ACCESS_VIOLATION, as the kernel would answer.
        /// intent describes the open the path is resolved for; nullptr only matches (MatchedPath) without
        /// resolving, for callers that answer from metadata (Stat).
        class RedirectedAttributes final
        {
        public:
            RedirectedAttributes(POBJECT_ATTRIBUTES source, const OpenIntent* intent) noexcept
                : _source(source), _intent(intent)
            {
                if (source == nullptr)
                {
                    return;
                }
                UNICODE_STRING name{};
                if (!SafeCopy(&_attributes, source, sizeof(_attributes))
                    || (_attributes.ObjectName != nullptr && !SafeCopy(&name, _attributes.ObjectName, sizeof(name))))
                {
                    _error = STATUS_ACCESS_VIOLATION;
                    return;
                }
                if (_attributes.ObjectName == nullptr || name.Buffer == nullptr || name.Length % sizeof(wchar_t) != 0)
                {
                    return; // nothing to translate; the system validates the rest
                }
                if (!CopyName(name))
                {
                    return;
                }
                if (_attributes.RootDirectory != nullptr)
                {
                    // Relative to a virtual directory handle: that handle already points at the backing
                    // folder, so the name stays as is; only the result has to be tracked.
                    _mapping = g_handles.Find(_attributes.RootDirectory);
                    if (_mapping < 0)
                    {
                        ResolveBelowRealDirectory(_attributes.RootDirectory, _nameCopy);
                    }
                    else if (g_resolver->IsRemote())
                    {
                        // The placeholder folder behind the handle may not hold the file yet: resolve the
                        // full virtual path instead.
                        ResolveBelowVirtualDirectory(_attributes.RootDirectory, _nameCopy);
                    }
                }
                else
                {
                    const int mapping = g_resolver->FindMapping(_nameCopy);
                    if (mapping >= 0)
                    {
                        _mapping = mapping;
                        Translate(_nameCopy, PathKind::Nt);
                    }
                    else if (IsDevicePath(_nameCopy))
                    {
                        // \Device\HarddiskVolumeN\... (the name NtQueryObject reports): advapi32 opens the parent folder
                        // of a file through it when it applies inherited ACEs (ReplaceFile, SetSecurityInfo).
                        const int deviceMapping = g_resolver->FindMappingByDevicePath(_nameCopy);
                        if (deviceMapping < 0)
                        {
                            return;
                        }
                        _mapping = deviceMapping;
                        Translate(_nameCopy, PathKind::Device);
                    }
                    else
                    {
                        return;
                    }
                }
                if (_rewritten)
                {
                    // Always an absolute backing path: RootDirectory (if any) is dropped.
                    _attributes.RootDirectory = nullptr;
                    _attributes.ObjectName = &_name;
                }
            }

            ~RedirectedAttributes()
            {
                if (_rewritten)
                {
                    FreeString(_name);
                }
                if (_heapName != nullptr)
                {
                    HeapFree(GetProcessHeap(), 0, _heapName);
                }
                FreeString(_ownedMatch);
            }

            RedirectedAttributes(const RedirectedAttributes&) = delete;
            RedirectedAttributes& operator=(const RedirectedAttributes&) = delete;

            bool Matched() const noexcept { return _mapping >= 0; }
            /// STATUS_SUCCESS, or the status the hook returns without calling the system.
            NTSTATUS Error() const noexcept { return _error; }
            int Mapping() const noexcept { return _mapping; }
            POBJECT_ATTRIBUTES Get() noexcept { return _rewritten ? &_attributes : _source; }
            /// The virtual path that matched (empty when the match came from a virtual file handle).
            const UNICODE_STRING& MatchedPath() const noexcept { return _matched; }
            PathKind MatchedKind() const noexcept { return _kind; }
            /// Machine B's metadata from the resolve (Attributes 0 when the resolver has none).
            const HookIpc::FileInfo& Info() const noexcept { return _info; }
            /// The Broker's write token of this open (0 when none).
            uint64_t WriteToken() const noexcept { return _writeToken; }

        private:
            static bool IsDevicePath(const UNICODE_STRING& name) noexcept
            {
                constexpr wchar_t prefix[] = L"\\Device\\";
                constexpr USHORT prefixChars = 8;
                if (name.Length < prefixChars * sizeof(wchar_t))
                {
                    return false;
                }
                for (USHORT i = 0; i < prefixChars; ++i)
                {
                    if (RtlUpcaseUnicodeChar(name.Buffer[i]) != RtlUpcaseUnicodeChar(prefix[i]))
                    {
                        return false;
                    }
                }
                return true;
            }

            /// Copies the caller's name text; short names stay in the inline buffer.
            bool CopyName(const UNICODE_STRING& name) noexcept
            {
                wchar_t* text = _inlineName;
                if (name.Length > sizeof(_inlineName))
                {
                    _heapName = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, name.Length));
                    if (_heapName == nullptr)
                    {
                        return false; // leave the call untouched rather than failing it
                    }
                    text = _heapName;
                }
                if (!SafeCopy(text, name.Buffer, name.Length))
                {
                    _error = STATUS_ACCESS_VIOLATION;
                    return false;
                }
                _nameCopy.Buffer = text;
                _nameCopy.Length = name.Length;
                _nameCopy.MaximumLength = name.Length;
                return true;
            }

            /// Records path (a view that must outlive this object, or _ownedMatch) as the match and, unless
            /// only matching, resolves it into _name.
            void Translate(const UNICODE_STRING& path, PathKind kind) noexcept
            {
                _matched = path;
                _kind = kind;
                if (_intent == nullptr)
                {
                    return;
                }
                const NTSTATUS status = g_resolver->Resolve(_mapping, path, kind, *_intent, _name, &_info, &_writeToken);
                _rewritten = NT_SUCCESS(status);
                _error = _rewritten ? STATUS_SUCCESS : status;
            }

            /// RootDirectory is a virtual directory handle of a remote resolver: join its virtual path with
            /// the relative name. File handles (no recorded path) keep the M1 behaviour.
            void ResolveBelowVirtualDirectory(HANDLE root, const UNICODE_STRING& relative) noexcept
            {
                int mapping = -1;
                UNICODE_STRING directory{};
                if (!g_remoteHandles.GetDirectoryPath(root, mapping, directory))
                {
                    return;
                }
                const size_t joinedBytes = static_cast<size_t>(directory.Length) + sizeof(wchar_t) + relative.Length;
                if (joinedBytes > MaxUnicodeStringBytes || !AllocateString(_ownedMatch, joinedBytes))
                {
                    FreeString(directory);
                    _error = joinedBytes > MaxUnicodeStringBytes ? STATUS_OBJECT_NAME_INVALID : STATUS_NO_MEMORY;
                    return;
                }
                BYTE* cursor = reinterpret_cast<BYTE*>(_ownedMatch.Buffer);
                memcpy(cursor, directory.Buffer, directory.Length);
                _ownedMatch.Length = directory.Length;
                if (relative.Length > 0)
                {
                    *reinterpret_cast<wchar_t*>(cursor + directory.Length) = L'\\';
                    memcpy(cursor + directory.Length + sizeof(wchar_t), relative.Buffer, relative.Length);
                    _ownedMatch.Length = static_cast<USHORT>(joinedBytes);
                }
                FreeString(directory);
                _mapping = mapping;
                Translate(_ownedMatch, PathKind::Nt);
            }

            /// RootDirectory is a real directory (typically the current directory handle): join its name
            /// with the relative name and translate when the result lies inside a virtual root.
            void ResolveBelowRealDirectory(HANDLE root, const UNICODE_STRING& relative) noexcept
            {
                VisitDirectoryName(root, [&](const UNICODE_STRING& rootName)
                    {
                        if (rootName.Length > 0)
                        {
                            ResolveJoined(rootName, relative);
                        }
                    });
            }

            void ResolveJoined(const UNICODE_STRING& rootName, const UNICODE_STRING& relative) noexcept
            {
                if (rootName.Length == 0 || !g_resolver->IsNearVirtualRoot(rootName))
                {
                    return;
                }
                const size_t joinedBytes = static_cast<size_t>(rootName.Length) + sizeof(wchar_t) + relative.Length;
                if (joinedBytes > MaxUnicodeStringBytes)
                {
                    return; // too long for any NT name: let the system reject it
                }
                UNICODE_STRING joined{};
                if (!AllocateString(joined, joinedBytes))
                {
                    _error = STATUS_NO_MEMORY;
                    return;
                }
                BYTE* cursor = reinterpret_cast<BYTE*>(joined.Buffer);
                memcpy(cursor, rootName.Buffer, rootName.Length);
                joined.Length = rootName.Length;
                if (relative.Length > 0)
                {
                    *reinterpret_cast<wchar_t*>(cursor + rootName.Length) = L'\\';
                    memcpy(cursor + rootName.Length + sizeof(wchar_t), relative.Buffer, relative.Length);
                    joined.Length = static_cast<USHORT>(joinedBytes);
                }
                const int mapping = g_resolver->FindMappingByDevicePath(joined);
                if (mapping < 0)
                {
                    FreeString(joined);
                    return;
                }
                _mapping = mapping;
                _ownedMatch = joined; // kept as the matched path
                Translate(_ownedMatch, PathKind::Device);
            }

            POBJECT_ATTRIBUTES _source;
            const OpenIntent* _intent;
            OBJECT_ATTRIBUTES _attributes{};
            UNICODE_STRING _name{};
            UNICODE_STRING _nameCopy{};
            UNICODE_STRING _matched{};
            UNICODE_STRING _ownedMatch{};
            PathKind _kind = PathKind::Nt;
            HookIpc::FileInfo _info{};
            uint64_t _writeToken = 0;
            wchar_t _inlineName[260];
            wchar_t* _heapName = nullptr;
            int _mapping = -1;
            bool _rewritten = false;
            NTSTATUS _error = STATUS_SUCCESS;
        };

        /// The matched virtual path as an NT path (\??\...), copied into ntPath (freed with FreeString). A device
        /// spelling (joined with the current directory) is moved under the NT root of its mapping.
        bool ToVirtualNtPath(const RedirectedAttributes& redirect, UNICODE_STRING& ntPath) noexcept
        {
            const UNICODE_STRING& matched = redirect.MatchedPath();
            if (redirect.MatchedKind() == PathKind::Nt)
            {
                return ConcatString(ntPath, matched, nullptr, 0);
            }
            const PathMapping& map = g_resolver->GetMapping(redirect.Mapping());
            return ConcatString(ntPath, map.VirtualNt,
                reinterpret_cast<const wchar_t*>(reinterpret_cast<const BYTE*>(matched.Buffer) + map.VirtualDevicePath.Length),
                static_cast<size_t>(matched.Length) - map.VirtualDevicePath.Length);
        }

        /// Remote resolver: a handle opened through a virtual path reports machine B's metadata, and a directory
        /// gets its listings from machine B. It also keeps the open's write token and delete-on-close (M3).
        void TrackRemoteHandle(HANDLE handle, const RedirectedAttributes& redirect, bool deleteOnClose) noexcept
        {
            const UNICODE_STRING& matched = redirect.MatchedPath();
            if (!g_resolver->IsRemote() || !redirect.Matched() || matched.Length == 0)
            {
                g_remoteHandles.Remove(handle);
                return;
            }
            UNICODE_STRING ntPath{};
            if (ToVirtualNtPath(redirect, ntPath))
            {
                g_remoteHandles.Add(handle, redirect.Mapping(), ntPath, redirect.Info(), redirect.WriteToken(), deleteOnClose);
                FreeString(ntPath);
            }
            else if (redirect.WriteToken() != 0)
            {
                g_resolver->CloseWrite(redirect.WriteToken());
            }
        }

        /// A failed open gives its write token back so the Broker does not keep the file busy.
        void ReleaseUnusedToken(const RedirectedAttributes& redirect) noexcept
        {
            if (redirect.WriteToken() != 0)
            {
                g_resolver->CloseWrite(redirect.WriteToken());
            }
        }

        /// A new handle value may equal a handle that was closed without NtClose (NtDuplicateObject with
        /// DUPLICATE_CLOSE_SOURCE, kernel): replace or drop stale entries.
        void TrackOpenedHandle(PHANDLE handlePointer, const RedirectedAttributes& redirect, bool deleteOnClose) noexcept
        {
            HANDLE handle = nullptr;
            if (!SafeCopy(&handle, handlePointer, sizeof(handle)))
            {
                ReleaseUnusedToken(redirect);
                return;
            }
            g_directoryNames.Remove(handle);
            g_parentScans.Remove(handle);
            if (redirect.Matched())
            {
                g_handles.Add(handle, redirect.Mapping());
            }
            else
            {
                g_handles.Remove(handle);
            }
            TrackRemoteHandle(handle, redirect, deleteOnClose);
        }

        /// A ranged cache copy (M8) is still written by the Broker's own handle: the process's open must share writing
        /// (and deleting, so a newer copy can take the name) or it would fail with a sharing violation. The copy is
        /// private to the session, so nothing else is let in.
        ULONG RangeShareAccess(const RedirectedAttributes& redirect, ULONG shareAccess) noexcept
        {
            if (redirect.Info().RangeId == 0)
            {
                return shareAccess;
            }
            g_anyRange.store(true, std::memory_order_relaxed);
            return shareAccess | FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;
        }

        /// Largest range one FetchRange request asks for: each request gets the ordinary IPC time limit, so a large read or
        /// a whole file (a mapping) goes in pieces instead of one request with a longer limit.
        constexpr int64_t FetchRangeChunk = 16LL * 1024 * 1024;

        /// Brings [start, start + length) of ranged copy rangeId in, FetchRangeChunk at a time; complete tells whether the
        /// copy is complete afterwards.
        NTSTATUS FetchRangeInChunks(uint32_t rangeId, int64_t start, int64_t length, bool& complete) noexcept
        {
            complete = false;
            int64_t done = 0;
            do
            {
                const int64_t piece = length - done < FetchRangeChunk ? length - done : FetchRangeChunk;
                const NTSTATUS status = g_resolver->FetchRange(rangeId, start + done, piece, false, complete);
                if (!NT_SUCCESS(status))
                {
                    return status;
                }
                done += piece;
            } while (done < length && !complete);
            return STATUS_SUCCESS;
        }

        /// Ranged cache copy of handle (M8): brings [offset, offset + length) in before a read (offset nullptr = the file
        /// pointer), or the whole file. Untracked or complete handles return at once. A read at the file pointer of a
        /// synchronous handle gets the position it was fetched for in filePointer (otherwise left alone): the caller reads
        /// at that offset, so another thread moving the pointer meanwhile cannot make it read a range not fetched.
        NTSTATUS EnsureRange(HANDLE fileHandle, const LARGE_INTEGER* offset, ULONG length, bool whole,
            LARGE_INTEGER* filePointer = nullptr) noexcept
        {
            HookIpc::FileInfo info{};
            if (g_remoteHandles.IsEmpty() || !g_remoteHandles.GetInfo(fileHandle, info) || info.RangeId == 0)
            {
                return STATUS_SUCCESS;
            }
            int64_t start = 0;
            int64_t size = length;
            if (!whole)
            {
                if (offset != nullptr)
                {
                    start = offset->QuadPart;
                }
                else
                {
                    LARGE_INTEGER position{};
                    IO_STATUS_BLOCK io{};
                    ULONG mode = 0;
                    IO_STATUS_BLOCK modeStatus{};
                    if (NT_SUCCESS(g_ntQueryInformationFile(fileHandle, &io, &position, sizeof(position), FilePositionInformation)))
                    {
                        start = position.QuadPart;
                        if (filePointer != nullptr
                            && NT_SUCCESS(g_ntQueryInformationFile(fileHandle, &modeStatus, &mode, sizeof(mode), FileModeInformation))
                            && (mode & (FileSynchronousIoAlert | FileSynchronousIoNonAlert)) != 0)
                        {
                            *filePointer = position;
                        }
                    }
                    else
                    {
                        whole = true;
                    }
                }
            }
            if (whole)
            {
                // The whole file in pieces of the ordinary time limit each; without its size, one request as before.
                FileStandardInfo standard{};
                IO_STATUS_BLOCK io{};
                if (NT_SUCCESS(g_ntQueryInformationFile(fileHandle, &io, &standard, sizeof(standard), FileStandardInformation)))
                {
                    whole = false;
                    start = 0;
                    size = standard.EndOfFile.QuadPart;
                }
            }
            bool complete = false;
            const NTSTATUS status = whole ? g_resolver->FetchRange(info.RangeId, 0, 0, true, complete)
                                          : FetchRangeInChunks(info.RangeId, start, size, complete);
            if (NT_SUCCESS(status) && complete)
            {
                g_remoteHandles.ClearRange(fileHandle);
            }
            return status;
        }

        /// Offset argument of NtReadFile[Scatter]: nullptr when the read uses the file pointer.
        bool ReadOffset(PLARGE_INTEGER byteOffset, LARGE_INTEGER& offset) noexcept
        {
            if (byteOffset == nullptr || !SafeCopy(&offset, byteOffset, sizeof(offset)))
            {
                return false;
            }
            return !(offset.HighPart == -1 && offset.LowPart == FileUseFilePointerPosition);
        }

        NTSTATUS NTAPI HookNtReadFile(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID buffer, ULONG length, PLARGE_INTEGER byteOffset, PULONG key)
        {
            if (g_anyRange.load(std::memory_order_relaxed))
            {
                ReentryGuard guard;
                if (guard.IsOutermost())
                {
                    LARGE_INTEGER offset{};
                    const bool explicitOffset = ReadOffset(byteOffset, offset);
                    LARGE_INTEGER fetchedAt{ { 0, -1 } };
                    const NTSTATUS fetched = EnsureRange(fileHandle, explicitOffset ? &offset : nullptr, length, false, &fetchedAt);
                    if (!NT_SUCCESS(fetched))
                    {
                        return fetched;
                    }
                    if (!explicitOffset && fetchedAt.QuadPart >= 0)
                    {
                        return g_ntReadFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, buffer, length, &fetchedAt, key);
                    }
                }
            }
            return g_ntReadFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, buffer, length, byteOffset, key);
        }

        NTSTATUS NTAPI HookNtReadFileScatter(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID segments, ULONG length, PLARGE_INTEGER byteOffset, PULONG key)
        {
            if (g_anyRange.load(std::memory_order_relaxed))
            {
                ReentryGuard guard;
                if (guard.IsOutermost())
                {
                    LARGE_INTEGER offset{};
                    const bool explicitOffset = ReadOffset(byteOffset, offset);
                    LARGE_INTEGER fetchedAt{ { 0, -1 } };
                    const NTSTATUS fetched = EnsureRange(fileHandle, explicitOffset ? &offset : nullptr, length, false, &fetchedAt);
                    if (!NT_SUCCESS(fetched))
                    {
                        return fetched;
                    }
                    if (!explicitOffset && fetchedAt.QuadPart >= 0)
                    {
                        return g_ntReadFileScatter(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, segments, length, &fetchedAt,
                            key);
                    }
                }
            }
            return g_ntReadFileScatter(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, segments, length, byteOffset, key);
        }

        /// A mapping (data or image, plan 15) sees the file without reads to hook: bring the whole copy in first.
        NTSTATUS EnsureWholeForSection(HANDLE fileHandle) noexcept
        {
            if (fileHandle == nullptr || !g_anyRange.load(std::memory_order_relaxed))
            {
                return STATUS_SUCCESS;
            }
            ReentryGuard guard;
            return guard.IsOutermost() ? EnsureRange(fileHandle, nullptr, 0, true) : STATUS_SUCCESS;
        }

        NTSTATUS NTAPI HookNtCreateSection(PHANDLE sectionHandle, ACCESS_MASK desiredAccess, POBJECT_ATTRIBUTES objectAttributes,
            PLARGE_INTEGER maximumSize, ULONG pageProtection, ULONG allocationAttributes, HANDLE fileHandle)
        {
            const NTSTATUS fetched = EnsureWholeForSection(fileHandle);
            if (!NT_SUCCESS(fetched))
            {
                return fetched;
            }
            return g_ntCreateSection(sectionHandle, desiredAccess, objectAttributes, maximumSize, pageProtection, allocationAttributes, fileHandle);
        }

        NTSTATUS NTAPI HookNtCreateSectionEx(PHANDLE sectionHandle, ACCESS_MASK desiredAccess, POBJECT_ATTRIBUTES objectAttributes,
            PLARGE_INTEGER maximumSize, ULONG pageProtection, ULONG allocationAttributes, HANDLE fileHandle, PVOID extendedParameters,
            ULONG extendedParameterCount)
        {
            const NTSTATUS fetched = EnsureWholeForSection(fileHandle);
            if (!NT_SUCCESS(fetched))
            {
                return fetched;
            }
            return g_ntCreateSectionEx(sectionHandle, desiredAccess, objectAttributes, maximumSize, pageProtection, allocationAttributes,
                fileHandle, extendedParameters, extendedParameterCount);
        }

        /// Write token and range of a lock call on a virtual handle; false when the call stays local.
        bool RemoteLockArguments(HANDLE fileHandle, PLARGE_INTEGER byteOffset, PLARGE_INTEGER length, uint64_t& token,
            LARGE_INTEGER& offset, LARGE_INTEGER& size) noexcept
        {
            if (g_remoteHandles.IsEmpty())
            {
                return false;
            }
            token = g_remoteHandles.GetWriteToken(fileHandle);
            return token != 0 && byteOffset != nullptr && length != nullptr && SafeCopy(&offset, byteOffset, sizeof(offset))
                && SafeCopy(&size, length, sizeof(size));
        }

        /// Completes a lock call the hook answered itself: synchronously, with the I/O status block and the event set.
        NTSTATUS CompleteLockCall(PIO_STATUS_BLOCK ioStatusBlock, HANDLE event, NTSTATUS status) noexcept
        {
            SafeSetIoStatus(ioStatusBlock, status, 0);
            if (event != nullptr && NT_SUCCESS(status))
            {
                SetEvent(event);
            }
            return status;
        }

        /// A lock granted on machine B completes like a real one: the I/O manager does it for a zero length lock taken
        /// (and dropped at once) on the cache copy with the caller's event, APC, completion port binding and skip
        /// notification modes, which the hook cannot see on a file handle. A zero length lock blocks nobody, so the
        /// Broker still reads the whole copy. Falls back to completing synchronously when that lock fails.
        NTSTATUS CompleteGrantedLock(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, const LARGE_INTEGER& offset, ULONG key) noexcept
        {
            LARGE_INTEGER at = offset;
            LARGE_INTEGER zero{};
            const NTSTATUS status = g_ntLockFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, &at, &zero, key, TRUE, FALSE);
            if (!NT_SUCCESS(status))
            {
                return CompleteLockCall(ioStatusBlock, event, STATUS_SUCCESS);
            }
            IO_STATUS_BLOCK unlockStatus{};
            g_ntUnlockFile(fileHandle, &unlockStatus, &at, &zero, key);
            return status;
        }

        /// NtLockFile on a virtual handle that may write (M8): the lock is taken on machine B only, where the Agent holds a
        /// handle for every such open (of any process of this machine too), so machine B arbitrates between all of them.
        /// The cache copy is not locked: the Broker must be able to read all of it for a write-back while locks are held.
        /// The Broker is asked synchronously; a granted lock then completes through the I/O manager (CompleteGrantedLock),
        /// a refused one fails at once like a lock refused before it could pend.
        NTSTATUS NTAPI HookNtLockFile(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PLARGE_INTEGER byteOffset, PLARGE_INTEGER length, ULONG key, BOOLEAN failImmediately,
            BOOLEAN exclusiveLock)
        {
            ReentryGuard guard;
            uint64_t token = 0;
            LARGE_INTEGER offset{};
            LARGE_INTEGER size{};
            if (!guard.IsOutermost() || !RemoteLockArguments(fileHandle, byteOffset, length, token, offset, size))
            {
                return g_ntLockFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, byteOffset, length, key, failImmediately,
                    exclusiveLock);
            }
            const uint16_t flags = static_cast<uint16_t>((exclusiveLock ? HookIpc::LockFlagExclusive : 0)
                | (failImmediately ? HookIpc::LockFlagFailImmediately : 0));
            const NTSTATUS status = g_resolver->LockRange(true, token, offset.QuadPart, size.QuadPart, key, flags);
            if (!NT_SUCCESS(status))
            {
                return CompleteLockCall(ioStatusBlock, event, status);
            }
            return CompleteGrantedLock(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, offset, key);
        }

        NTSTATUS NTAPI HookNtUnlockFile(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PLARGE_INTEGER byteOffset, PLARGE_INTEGER length,
            ULONG key)
        {
            ReentryGuard guard;
            uint64_t token = 0;
            LARGE_INTEGER offset{};
            LARGE_INTEGER size{};
            if (!guard.IsOutermost() || !RemoteLockArguments(fileHandle, byteOffset, length, token, offset, size))
            {
                return g_ntUnlockFile(fileHandle, ioStatusBlock, byteOffset, length, key);
            }
            return CompleteLockCall(ioStatusBlock, nullptr, g_resolver->LockRange(false, token, offset.QuadPart, size.QuadPart, key, 0));
        }

        NTSTATUS NTAPI HookNtCreateFile(PHANDLE fileHandle, ACCESS_MASK desiredAccess, POBJECT_ATTRIBUTES objectAttributes,
            PIO_STATUS_BLOCK ioStatusBlock, PLARGE_INTEGER allocationSize, ULONG fileAttributes, ULONG shareAccess,
            ULONG createDisposition, ULONG createOptions, PVOID eaBuffer, ULONG eaLength)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return g_ntCreateFile(fileHandle, desiredAccess, objectAttributes, ioStatusBlock, allocationSize,
                    fileAttributes, shareAccess, createDisposition, createOptions, eaBuffer, eaLength);
            }
            const OpenIntent intent{ desiredAccess, createDisposition, createOptions, fileAttributes, shareAccess };
            RedirectedAttributes redirect(objectAttributes, &intent);
            if (redirect.Error() != STATUS_SUCCESS)
            {
                return redirect.Error();
            }
            shareAccess = RangeShareAccess(redirect, shareAccess);
            const NTSTATUS status = g_ntCreateFile(fileHandle, desiredAccess, redirect.Get(), ioStatusBlock,
                allocationSize, fileAttributes, shareAccess, createDisposition, createOptions, eaBuffer, eaLength);
            if (NT_SUCCESS(status))
            {
                TrackOpenedHandle(fileHandle, redirect, (createOptions & FileDeleteOnCloseOption) != 0);
            }
            else
            {
                ReleaseUnusedToken(redirect);
            }
            return status;
        }

        NTSTATUS NTAPI HookNtOpenFile(PHANDLE fileHandle, ACCESS_MASK desiredAccess, POBJECT_ATTRIBUTES objectAttributes,
            PIO_STATUS_BLOCK ioStatusBlock, ULONG shareAccess, ULONG openOptions)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return g_ntOpenFile(fileHandle, desiredAccess, objectAttributes, ioStatusBlock, shareAccess, openOptions);
            }
            const OpenIntent intent{ desiredAccess, FileOpenDisposition, openOptions, 0, shareAccess };
            RedirectedAttributes redirect(objectAttributes, &intent);
            if (redirect.Error() != STATUS_SUCCESS)
            {
                return redirect.Error();
            }
            shareAccess = RangeShareAccess(redirect, shareAccess);
            const NTSTATUS status = g_ntOpenFile(fileHandle, desiredAccess, redirect.Get(), ioStatusBlock, shareAccess, openOptions);
            if (NT_SUCCESS(status))
            {
                TrackOpenedHandle(fileHandle, redirect, (openOptions & FileDeleteOnCloseOption) != 0);
            }
            else
            {
                ReleaseUnusedToken(redirect);
            }
            return status;
        }

        /// FILE_NETWORK_OPEN_INFORMATION; its first five fields are FILE_BASIC_INFORMATION.
        struct NetworkOpenInfo
        {
            LARGE_INTEGER CreationTime;
            LARGE_INTEGER LastAccessTime;
            LARGE_INTEGER LastWriteTime;
            LARGE_INTEGER ChangeTime;
            LARGE_INTEGER AllocationSize;
            LARGE_INTEGER EndOfFile;
            ULONG FileAttributes;
        };
        constexpr size_t BasicInfoSize = 40;
        static_assert(sizeof(NetworkOpenInfo) == 56);

        NetworkOpenInfo ToNetworkOpenInfo(const HookIpc::FileInfo& info) noexcept
        {
            NetworkOpenInfo result{};
            result.CreationTime.QuadPart = info.CreationTime;
            result.LastAccessTime.QuadPart = info.LastAccessTime;
            result.LastWriteTime.QuadPart = info.LastWriteTime;
            result.ChangeTime.QuadPart = info.ChangeTime;
            result.AllocationSize.QuadPart = info.AllocationSize;
            result.EndOfFile.QuadPart = info.Size;
            result.FileAttributes = info.Attributes;
            return result;
        }

        NTSTATUS QueryAttributes(NtQueryAttributesFileFn original, bool full, POBJECT_ATTRIBUTES objectAttributes, PVOID information)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return original(objectAttributes, information);
            }
            const bool remote = g_resolver->IsRemote();
            RedirectedAttributes redirect(objectAttributes, remote ? nullptr : &ReadAttributesIntent);
            if (redirect.Error() != STATUS_SUCCESS)
            {
                return redirect.Error();
            }
            if (!remote || redirect.MatchedPath().Length == 0)
            {
                return original(redirect.Get(), information);
            }
            // Machine B's metadata, without downloading the file.
            HookIpc::FileInfo info{};
            const NTSTATUS status = g_resolver->Stat(redirect.Mapping(), redirect.MatchedPath(), redirect.MatchedKind(), info);
            if (!NT_SUCCESS(status))
            {
                return status;
            }
            const NetworkOpenInfo answer = ToNetworkOpenInfo(info);
            if (full)
            {
                return SafeCopy(information, &answer, sizeof(answer)) ? STATUS_SUCCESS : STATUS_ACCESS_VIOLATION;
            }
            BYTE basic[BasicInfoSize] = {};
            memcpy(basic, &answer, 32);
            memcpy(basic + 32, &answer.FileAttributes, sizeof(ULONG));
            return SafeCopy(information, basic, sizeof(basic)) ? STATUS_SUCCESS : STATUS_ACCESS_VIOLATION;
        }

        NTSTATUS NTAPI HookNtQueryAttributesFile(POBJECT_ATTRIBUTES objectAttributes, PVOID information)
        {
            return QueryAttributes(g_ntQueryAttributesFile, false, objectAttributes, information);
        }

        NTSTATUS NTAPI HookNtQueryFullAttributesFile(POBJECT_ATTRIBUTES objectAttributes, PVOID information)
        {
            return QueryAttributes(g_ntQueryFullAttributesFile, true, objectAttributes, information);
        }

        /// Remote NtQueryInformationByName: metadata classes come from Stat; the others open the cache copy.
        NTSTATUS QueryRemoteByName(const RedirectedAttributes& match, PIO_STATUS_BLOCK ioStatusBlock, PVOID information,
            ULONG length, ULONG informationClass)
        {
            constexpr ULONG FileBasicInformationClass = 4;
            constexpr ULONG FileStandardInformationClass = 5;
            constexpr ULONG FileNetworkOpenInformationClass = 34;
            constexpr ULONG FileAttributeTagInformationClass = 35;
            constexpr ULONG FileStatInformationClass = 68;
            BYTE answer[72] = {};
            ULONG size = 0;
            switch (informationClass)
            {
            case FileBasicInformationClass: size = BasicInfoSize; break;
            case FileStandardInformationClass: size = 24; break;
            case FileNetworkOpenInformationClass: size = sizeof(NetworkOpenInfo); break;
            case FileAttributeTagInformationClass: size = 8; break;
            case FileStatInformationClass: size = 72; break;
            default:
            {
                UNICODE_STRING realPath{};
                const NTSTATUS status = g_resolver->Resolve(match.Mapping(), match.MatchedPath(), match.MatchedKind(),
                    ReadAttributesIntent, realPath, nullptr);
                if (!NT_SUCCESS(status))
                {
                    return status;
                }
                OBJECT_ATTRIBUTES attributes{};
                InitializeObjectAttributes(&attributes, &realPath, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
                const NTSTATUS result = g_ntQueryInformationByName(&attributes, ioStatusBlock, information, length, informationClass);
                FreeString(realPath);
                return result;
            }
            }
            if (length < size)
            {
                return STATUS_INFO_LENGTH_MISMATCH;
            }
            HookIpc::FileInfo info{};
            const NTSTATUS status = g_resolver->Stat(match.Mapping(), match.MatchedPath(), match.MatchedKind(), info);
            if (!NT_SUCCESS(status))
            {
                return status;
            }
            const NetworkOpenInfo open = ToNetworkOpenInfo(info);
            const bool directory = (info.Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
            const ULONG links = 1;
            switch (informationClass)
            {
            case FileBasicInformationClass:
                memcpy(answer, &open, 32);
                memcpy(answer + 32, &open.FileAttributes, sizeof(ULONG));
                break;
            case FileStandardInformationClass: // AllocationSize, EndOfFile, NumberOfLinks, DeletePending, Directory
                memcpy(answer, &open.AllocationSize, 16);
                memcpy(answer + 16, &links, sizeof(links));
                answer[21] = directory ? 1 : 0;
                break;
            case FileNetworkOpenInformationClass:
                memcpy(answer, &open, sizeof(open));
                break;
            case FileAttributeTagInformationClass:
                memcpy(answer, &open.FileAttributes, sizeof(ULONG));
                break;
            default: // FILE_STAT_INFORMATION
            {
                const ACCESS_MASK access = FILE_GENERIC_READ | FILE_GENERIC_EXECUTE; // read-only share
                memcpy(answer, &info.FileId, 8);
                memcpy(answer + 8, &open, 32);
                memcpy(answer + 40, &open.AllocationSize, 16);
                memcpy(answer + 56, &open.FileAttributes, sizeof(ULONG));
                memcpy(answer + 64, &links, sizeof(links));
                memcpy(answer + 68, &access, sizeof(access));
                break;
            }
            }
            if (!SafeCopy(information, answer, size) || !SafeSetIoStatus(ioStatusBlock, STATUS_SUCCESS, size))
            {
                return STATUS_ACCESS_VIOLATION;
            }
            return STATUS_SUCCESS;
        }

        /// Path-based query without a handle (GetFileInformationByName, newer kernelbase attribute paths).
        NTSTATUS NTAPI HookNtQueryInformationByName(POBJECT_ATTRIBUTES objectAttributes, PIO_STATUS_BLOCK ioStatusBlock,
            PVOID information, ULONG length, ULONG informationClass)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return g_ntQueryInformationByName(objectAttributes, ioStatusBlock, information, length, informationClass);
            }
            const bool remote = g_resolver->IsRemote();
            RedirectedAttributes redirect(objectAttributes, remote ? nullptr : &ReadAttributesIntent);
            if (redirect.Error() != STATUS_SUCCESS)
            {
                return redirect.Error();
            }
            if (remote && redirect.MatchedPath().Length != 0)
            {
                return QueryRemoteByName(redirect, ioStatusBlock, information, length, informationClass);
            }
            return g_ntQueryInformationByName(redirect.Get(), ioStatusBlock, information, length, informationClass);
        }

        NTSTATUS NTAPI HookNtDeleteFile(POBJECT_ATTRIBUTES objectAttributes)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return g_ntDeleteFile(objectAttributes);
            }
            if (g_resolver->IsRemote())
            {
                // Machine B deletes; the cache copy goes with it (the Broker removes it).
                RedirectedAttributes match(objectAttributes, nullptr);
                if (match.Error() != STATUS_SUCCESS)
                {
                    return match.Error();
                }
                if (match.MatchedPath().Length == 0)
                {
                    return g_ntDeleteFile(match.Get());
                }
                UNICODE_STRING ntPath{};
                if (!ToVirtualNtPath(match, ntPath))
                {
                    return STATUS_NO_MEMORY;
                }
                const NTSTATUS status = g_resolver->Delete(match.Mapping(), ntPath, false, 0);
                FreeString(ntPath);
                return status;
            }
            const OpenIntent intent{ DELETE, FileOpenDisposition, 0 };
            RedirectedAttributes redirect(objectAttributes, &intent);
            if (redirect.Error() != STATUS_SUCCESS)
            {
                return redirect.Error();
            }
            return g_ntDeleteFile(redirect.Get());
        }

        bool TryGetNameOffset(ULONG informationClass, ULONG& nameOffset) noexcept
        {
            switch (informationClass)
            {
            case FileNameInformation:
            case FileNormalizedNameInformation:
                nameOffset = 0;
                return true;
            case FileAllInformation:
                nameOffset = sizeof(FileAllInfoHeader);
                return true;
            default:
                return false;
            }
        }

        /// Remote handles: the cache copy has its own times, attributes (read-only), allocation size and file id;
        /// replace them with machine B's in the metadata classes. The volume (serial, flags) stays the cache's.
        void PatchRemoteMetadata(HANDLE fileHandle, PVOID information, ULONG length, ULONG informationClass) noexcept
        {
            constexpr ULONG FileBasicInformationClass = 4;
            constexpr ULONG FileStandardInformationClass = 5;
            constexpr ULONG FileInternalInformationClass = 6;
            constexpr ULONG FileNetworkOpenInformationClass = 34;
            constexpr ULONG FileAttributeTagInformationClass = 35;
            constexpr ULONG FileIdInformationClass = 59;
            constexpr ULONG FileStatInformationClass = 68;
            // Field offsets in each class; -1 = not in the class.
            int times = -1;
            int attributes = -1;
            int allocation = -1;
            int fileId = -1;
            bool fileId128 = false;
            switch (informationClass)
            {
            case FileBasicInformationClass: times = 0; attributes = 32; break;
            case FileStandardInformationClass: allocation = 0; break;
            case FileInternalInformationClass: fileId = 0; break;
            case FileAllInformation: times = 0; attributes = 32; allocation = 40; fileId = 64; break; // Basic, Standard, Internal
            case FileNetworkOpenInformationClass: times = 0; allocation = 32; attributes = 48; break;
            case FileAttributeTagInformationClass: attributes = 0; break;
            case FileIdInformationClass: fileId = 8; fileId128 = true; break; // after the volume serial number
            case FileStatInformationClass: fileId = 0; times = 8; allocation = 40; attributes = 56; break;
            default: return;
            }
            HookIpc::FileInfo info{};
            if (!g_remoteHandles.GetInfo(fileHandle, info))
            {
                return;
            }
            if (g_remoteHandles.GetWriteToken(fileHandle) != 0)
            {
                // The cache copy holds this handle's writes and times: only the identity stays machine B's.
                times = -1;
                attributes = -1;
                allocation = -1;
            }
            BYTE* target = static_cast<BYTE*>(information);
            const auto patch = [&](int offset, const void* value, ULONG bytes) noexcept
            {
                if (offset >= 0 && static_cast<ULONG>(offset) + bytes <= length)
                {
                    SafeCopy(target + offset, value, bytes);
                }
            };
            const int64_t fileTimes[4] = { info.CreationTime, info.LastAccessTime, info.LastWriteTime, info.ChangeTime };
            patch(times, fileTimes, sizeof(fileTimes));
            patch(attributes, &info.Attributes, sizeof(info.Attributes));
            patch(allocation, &info.AllocationSize, sizeof(info.AllocationSize));
            if (info.FileId != 0) // unknown on machine B: keep the cache's
            {
                const uint64_t id[2] = { info.FileId, 0 };
                patch(fileId, id, fileId128 ? sizeof(id) : sizeof(id[0]));
            }
        }

        NTSTATUS QueryInformationFileNames(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information,
            ULONG length, ULONG informationClass);

        NTSTATUS NTAPI HookNtQueryInformationFile(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information,
            ULONG length, ULONG informationClass)
        {
            const NTSTATUS status = QueryInformationFileNames(fileHandle, ioStatusBlock, information, length, informationClass);
            // STATUS_BUFFER_OVERFLOW (FileAllInformation with a long name) still filled the fixed part.
            if ((NT_SUCCESS(status) || (status == STATUS_BUFFER_OVERFLOW && informationClass == FileAllInformation))
                && !g_remoteHandles.IsEmpty())
            {
                PatchRemoteMetadata(fileHandle, information, length, informationClass);
            }
            return status;
        }

        /// Name classes of virtual handles report the virtual name; the rest goes to the system.
        NTSTATUS QueryInformationFileNames(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information,
            ULONG length, ULONG informationClass)
        {
            ULONG nameOffset = 0;
            if (!TryGetNameOffset(informationClass, nameOffset))
            {
                return g_ntQueryInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            ReentryGuard guard;
            const int mapping = guard.IsOutermost() ? g_handles.Find(fileHandle) : -1;
            if (mapping < 0 || length < nameOffset + sizeof(ULONG))
            {
                return g_ntQueryInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            const PathMapping& map = g_resolver->GetMapping(mapping);
            const size_t headerBytes = nameOffset + sizeof(ULONG);
            const size_t bufferBytes = headerBytes + MaxNameBytes;
            HeapBuffer buffer(bufferBytes);
            if (!buffer || map.RealVolumeRelative.Length == 0)
            {
                return g_ntQueryInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }

            IO_STATUS_BLOCK realStatus{};
            const NTSTATUS status = g_ntQueryInformationFile(fileHandle, &realStatus, buffer.Get(),
                static_cast<ULONG>(bufferBytes), informationClass);
            // The name is rewritten in place (one buffer per call).
            auto* name = reinterpret_cast<FileNameInfo*>(buffer.Get() + nameOffset);
            size_t newBytes = 0;
            if (!NT_SUCCESS(status)
                || !ReplacePathPrefix(name->FileName, name->FileNameLength, map.RealVolumeRelative,
                    map.VirtualVolumeRelative, name->FileName, MaxNameBytes, newBytes))
            {
                // Name outside the backing root (or query failed): answer exactly as the system does.
                return g_ntQueryInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            name->FileNameLength = static_cast<ULONG>(newBytes);

            const size_t required = headerBytes + newBytes;
            const size_t copied = required <= length ? required : length;
            const NTSTATUS result = required <= length ? STATUS_SUCCESS : STATUS_BUFFER_OVERFLOW;
            if (!SafeCopy(information, buffer.Get(), copied) || !SafeSetIoStatus(ioStatusBlock, result, copied))
            {
                return STATUS_ACCESS_VIOLATION;
            }
            return result;
        }

        bool IsRenameClass(ULONG informationClass) noexcept
        {
            return informationClass == FileRenameInformation || informationClass == FileLinkInformation
                || informationClass == FileRenameInformationEx || informationClass == FileLinkInformationEx;
        }

        /// Copies the rename/link target of a FILE_RENAME_INFORMATION into text (at least 0xFFFE bytes).
        NTSTATUS ReadRenameTarget(PVOID information, ULONG length, FileRenameInfo& header, HeapBuffer& text, UNICODE_STRING& target) noexcept
        {
            constexpr size_t headerBytes = offsetof(FileRenameInfo, FileName);
            if (!SafeCopy(&header, information, headerBytes))
            {
                return STATUS_ACCESS_VIOLATION;
            }
            if (header.FileNameLength > 0xFFFC || header.FileNameLength % sizeof(wchar_t) != 0 || headerBytes + header.FileNameLength > length)
            {
                return STATUS_INVALID_PARAMETER;
            }
            if (!text)
            {
                return STATUS_NO_MEMORY;
            }
            if (!SafeCopy(text.Get(), static_cast<const BYTE*>(information) + headerBytes, header.FileNameLength))
            {
                return STATUS_ACCESS_VIOLATION;
            }
            target.Buffer = reinterpret_cast<PWSTR>(text.Get());
            target.Length = static_cast<USHORT>(header.FileNameLength);
            target.MaximumLength = target.Length;
            return STATUS_SUCCESS;
        }

        /// Virtual NT path a rename target names, into owned (freed with FreeString); mapping -1 when it is not virtual.
        /// A bare name (no '\', no root directory) renames inside the source's folder.
        NTSTATUS ResolveRenameTarget(const FileRenameInfo& header, const UNICODE_STRING& target, bool sourceTracked,
            const UNICODE_STRING& sourcePath, int sourceMapping, int& mapping, UNICODE_STRING& owned) noexcept
        {
            mapping = -1;
            const auto join = [&](const UNICODE_STRING& folder, int folderMapping) noexcept -> NTSTATUS
            {
                const size_t bytes = static_cast<size_t>(folder.Length) + sizeof(wchar_t) + target.Length;
                if (bytes > MaxUnicodeStringBytes || !AllocateString(owned, bytes))
                {
                    return bytes > MaxUnicodeStringBytes ? STATUS_OBJECT_NAME_INVALID : STATUS_NO_MEMORY;
                }
                BYTE* cursor = reinterpret_cast<BYTE*>(owned.Buffer);
                memcpy(cursor, folder.Buffer, folder.Length);
                *reinterpret_cast<wchar_t*>(cursor + folder.Length) = L'\\';
                memcpy(cursor + folder.Length + sizeof(wchar_t), target.Buffer, target.Length);
                owned.Length = static_cast<USHORT>(bytes);
                mapping = folderMapping;
                return STATUS_SUCCESS;
            };
            if (header.RootDirectory != nullptr)
            {
                int rootMapping = -1;
                UNICODE_STRING rootPath{};
                if (!g_remoteHandles.GetDirectoryPath(header.RootDirectory, rootMapping, rootPath))
                {
                    return STATUS_SUCCESS; // relative to a real folder
                }
                const NTSTATUS status = join(rootPath, rootMapping);
                FreeString(rootPath);
                return status;
            }
            if (target.Length > 0 && target.Buffer[0] == L'\\')
            {
                const int found = g_resolver->FindMapping(target);
                if (found >= 0)
                {
                    if (!ConcatString(owned, target, nullptr, 0))
                    {
                        return STATUS_NO_MEMORY;
                    }
                    mapping = found;
                }
                return STATUS_SUCCESS;
            }
            if (!sourceTracked)
            {
                return STATUS_SUCCESS;
            }
            UNICODE_STRING folder = sourcePath;
            while (folder.Length > 0 && folder.Buffer[folder.Length / sizeof(wchar_t) - 1] != L'\\')
            {
                folder.Length -= sizeof(wchar_t);
            }
            if (folder.Length > 0)
            {
                folder.Length -= sizeof(wchar_t);
            }
            return join(folder, sourceMapping);
        }

        /// NtSetInformationFile with a remote resolver (M3). handled=false lets the system (or the M1 path) answer.
        NTSTATUS SetRemoteInformation(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information, ULONG length,
            ULONG informationClass, bool& handled) noexcept
        {
            handled = false;
            const bool rename = IsRenameClass(informationClass);
            if (!rename && informationClass != FileBasicInformation && informationClass != FileDispositionInformation
                && informationClass != FileDispositionInformationEx)
            {
                return STATUS_SUCCESS;
            }
            int mapping = -1;
            UNICODE_STRING path{};
            HookIpc::FileInfo info{};
            bool tracked = !g_remoteHandles.IsEmpty() && g_remoteHandles.GetEntry(fileHandle, mapping, path, info);
            if (tracked && g_remoteHandles.IsOrphaned(fileHandle))
            {
                // Replaced by a rename: the file has no name on machine B, only its cache copy is left.
                if (!rename)
                {
                    FreeString(path);
                    return STATUS_SUCCESS;
                }
                tracked = false;
            }
            struct PathOwner
            {
                UNICODE_STRING& Value;
                ~PathOwner() { FreeString(Value); }
            } pathOwner{ path };
            if (!tracked && !rename)
            {
                return STATUS_SUCCESS;
            }

            if (rename)
            {
                if (length < offsetof(FileRenameInfo, FileName))
                {
                    return STATUS_SUCCESS;
                }
                FileRenameInfo header{};
                HeapBuffer text(0x10000);
                UNICODE_STRING target{};
                NTSTATUS status = ReadRenameTarget(information, length, header, text, target);
                if (!NT_SUCCESS(status))
                {
                    if (!tracked)
                    {
                        return STATUS_SUCCESS; // let the system report it
                    }
                    handled = true;
                    return status;
                }
                int targetMapping = -1;
                UNICODE_STRING virtualTarget{};
                status = ResolveRenameTarget(header, target, tracked, path, mapping, targetMapping, virtualTarget);
                struct TargetOwner
                {
                    UNICODE_STRING& Value;
                    ~TargetOwner() { FreeString(Value); }
                } targetOwner{ virtualTarget };
                if (!NT_SUCCESS(status))
                {
                    handled = true;
                    return status;
                }
                if (!tracked && targetMapping < 0)
                {
                    return STATUS_SUCCESS; // real to real
                }
                handled = true;
                if (informationClass == FileLinkInformation || informationClass == FileLinkInformationEx)
                {
                    return STATUS_NOT_SUPPORTED; // hard links on machine B: backlog
                }
                if (!tracked || targetMapping < 0)
                {
                    return STATUS_NOT_SAME_DEVICE; // MoveFileEx then copies and deletes
                }
                const bool replace = informationClass == FileRenameInformationEx ? (header.Flags & 0x1) != 0 : header.ReplaceIfExists != FALSE;
                HookIpc::FileInfo newInfo{};
                UNICODE_STRING newReal{};
                status = g_resolver->Rename(mapping, path, targetMapping, virtualTarget, replace, newInfo, newReal);
                if (!NT_SUCCESS(status))
                {
                    return status;
                }
                constexpr size_t headerBytes = offsetof(FileRenameInfo, FileName);
                const size_t newLength = headerBytes + newReal.Length;
                HeapBuffer buffer(newLength + sizeof(wchar_t));
                if (!buffer)
                {
                    FreeString(newReal);
                    return STATUS_NO_MEMORY;
                }
                memcpy(buffer.Get(), &header, headerBytes);
                auto* newRename = reinterpret_cast<FileRenameInfo*>(buffer.Get());
                newRename->RootDirectory = nullptr;
                newRename->FileNameLength = newReal.Length;
                memcpy(newRename->FileName, newReal.Buffer, newReal.Length);
                FreeString(newReal);
                status = g_ntSetInformationFile(fileHandle, ioStatusBlock, buffer.Get(), static_cast<ULONG>(newLength), informationClass);
                if (NT_SUCCESS(status))
                {
                    g_remoteHandles.OrphanPath(virtualTarget, fileHandle);
                    g_remoteHandles.Update(fileHandle, &virtualTarget, &newInfo);
                }
                return status;
            }

            if (informationClass == FileDispositionInformation || informationClass == FileDispositionInformationEx)
            {
                ULONG flags = 0;
                if (informationClass == FileDispositionInformation)
                {
                    BOOLEAN deleteFile = FALSE;
                    if (length < sizeof(deleteFile) || !SafeCopy(&deleteFile, information, sizeof(deleteFile)))
                    {
                        return STATUS_SUCCESS;
                    }
                    flags = deleteFile ? FileDispositionDelete : 0;
                }
                else if (length < sizeof(flags) || !SafeCopy(&flags, information, sizeof(flags)))
                {
                    return STATUS_SUCCESS;
                }
                handled = true;
                if ((flags & FileDispositionOnClose) != 0)
                {
                    const NTSTATUS status = g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
                    if (NT_SUCCESS(status))
                    {
                        g_remoteHandles.SetDeleteOnClose(fileHandle, (flags & FileDispositionDelete) != 0);
                    }
                    return status;
                }
                if ((flags & FileDispositionDelete) != 0)
                {
                    // Machine B decides (directory not empty, read only...). Undoing it later is not possible (backlog).
                    const NTSTATUS status = g_resolver->Delete(mapping, path, true, 0);
                    if (!NT_SUCCESS(status))
                    {
                        return status;
                    }
                }
                return g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }

            // FileBasicInformation: times (0 = keep, -1/-2 = stop/resume automatic updates) and attributes (0 = keep).
            BYTE basic[BasicInfoSize] = {};
            if (length < BasicInfoSize || !SafeCopy(basic, information, BasicInfoSize))
            {
                return STATUS_SUCCESS;
            }
            HookIpc::SetInfoRequest request{};
            int64_t times[3] = {};
            memcpy(times, basic, sizeof(times));
            const uint16_t timeFields[3] = { HookIpc::SetCreationTime, HookIpc::SetLastAccessTime, HookIpc::SetLastWriteTime };
            for (int i = 0; i < 3; ++i)
            {
                if (times[i] > 0)
                {
                    request.Fields |= timeFields[i];
                }
            }
            request.CreationTime = times[0];
            request.LastAccessTime = times[1];
            request.LastWriteTime = times[2];
            memcpy(&request.Attributes, basic + 32, sizeof(request.Attributes));
            if (request.Attributes != 0)
            {
                request.Fields |= HookIpc::SetAttributes;
            }
            if (request.Fields == 0)
            {
                return STATUS_SUCCESS;
            }
            handled = true;
            HookIpc::FileInfo newInfo{};
            NTSTATUS status = g_resolver->SetInfo(mapping, path, request, newInfo);
            if (!NT_SUCCESS(status))
            {
                return status;
            }
            status = g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            if (NT_SUCCESS(status))
            {
                g_remoteHandles.Update(fileHandle, nullptr, &newInfo);
            }
            return status;
        }

        /// FileCompletionInformation (CreateIoCompletionPort) on a virtual directory: the hook completes its change
        /// notifications itself, so it records the port; the binding is applied to the handle as well.
        NTSTATUS SetCompletionPort(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information, ULONG length,
            ULONG informationClass)
        {
            FileCompletionInfo completion{};
            const bool copied = length >= sizeof(completion) && SafeCopy(&completion, information, sizeof(completion));
            const NTSTATUS status = g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            ULONG64 generation = 0;
            int mapping = -1;
            UNICODE_STRING path{};
            if (NT_SUCCESS(status) && copied && g_resolver->IsRemote() && !g_remoteHandles.IsEmpty()
                && g_remoteHandles.Find(fileHandle, generation) && g_remoteHandles.GetDirectoryPath(fileHandle, mapping, path))
            {
                FreeString(path);
                g_watches.SetCompletionPort(fileHandle, generation, completion.Port, completion.Key);
            }
            return status;
        }

        NTSTATUS NTAPI HookNtSetInformationFile(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information,
            ULONG length, ULONG informationClass)
        {
            constexpr size_t headerBytes = offsetof(FileRenameInfo, FileName);
            ReentryGuard guard;
            if (!guard.IsOutermost() || information == nullptr)
            {
                return g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            if (informationClass == FileCompletionInformation || informationClass == FileReplaceCompletionInformation)
            {
                return SetCompletionPort(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            if (g_resolver->IsRemote())
            {
                bool handled = false;
                const NTSTATUS status = SetRemoteInformation(fileHandle, ioStatusBlock, information, length, informationClass, handled);
                return handled ? status : g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            if (!IsRenameClass(informationClass) || length < headerBytes)
            {
                return g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            FileRenameInfo header{};
            if (!SafeCopy(&header, information, headerBytes))
            {
                return STATUS_ACCESS_VIOLATION;
            }
            if (header.RootDirectory != nullptr || header.FileNameLength > 0xFFFC || header.FileNameLength % sizeof(wchar_t) != 0
                || headerBytes + header.FileNameLength > length)
            {
                return g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            HeapBuffer targetText(header.FileNameLength + sizeof(wchar_t));
            if (!targetText)
            {
                return STATUS_NO_MEMORY;
            }
            if (!SafeCopy(targetText.Get(), static_cast<const BYTE*>(information) + headerBytes, header.FileNameLength))
            {
                return STATUS_ACCESS_VIOLATION;
            }
            UNICODE_STRING target{};
            target.Buffer = reinterpret_cast<PWSTR>(targetText.Get());
            target.Length = static_cast<USHORT>(header.FileNameLength);
            target.MaximumLength = target.Length;
            const int mapping = g_resolver->FindMapping(target);
            if (mapping < 0)
            {
                return g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            UNICODE_STRING realTarget{};
            const OpenIntent intent{ DELETE, FileCreateDisposition, 0 }; // the rename creates the target name
            const NTSTATUS resolved = g_resolver->Resolve(mapping, target, PathKind::Nt, intent, realTarget, nullptr);
            if (!NT_SUCCESS(resolved))
            {
                return resolved;
            }
            const size_t newLength = headerBytes + realTarget.Length;
            HeapBuffer buffer(newLength + sizeof(wchar_t));
            if (!buffer)
            {
                FreeString(realTarget);
                return STATUS_NO_MEMORY;
            }
            memcpy(buffer.Get(), &header, headerBytes);
            auto* newRename = reinterpret_cast<FileRenameInfo*>(buffer.Get());
            newRename->FileNameLength = realTarget.Length;
            memcpy(newRename->FileName, realTarget.Buffer, realTarget.Length);
            FreeString(realTarget);
            return g_ntSetInformationFile(fileHandle, ioStatusBlock, buffer.Get(), static_cast<ULONG>(newLength), informationClass);
        }

        NTSTATUS NTAPI HookNtQueryObject(HANDLE handle, ULONG informationClass, PVOID information, ULONG length, PULONG returnLength)
        {
            if (informationClass != ObjectNameInformation)
            {
                return g_ntQueryObject(handle, informationClass, information, length, returnLength);
            }
            ReentryGuard guard;
            const int mapping = guard.IsOutermost() ? g_handles.Find(handle) : -1;
            if (mapping < 0 || length < sizeof(ObjectNameInfo))
            {
                return g_ntQueryObject(handle, informationClass, information, length, returnLength);
            }
            const PathMapping& map = g_resolver->GetMapping(mapping);
            const size_t bufferBytes = sizeof(ObjectNameInfo) + MaxNameBytes;
            HeapBuffer buffer(bufferBytes);
            if (!buffer || map.RealDevicePath.Length == 0)
            {
                return g_ntQueryObject(handle, informationClass, information, length, returnLength);
            }
            const NTSTATUS status = g_ntQueryObject(handle, informationClass, buffer.Get(), static_cast<ULONG>(bufferBytes), nullptr);
            // The answer (header + text) is built in place in the same buffer.
            BYTE* answer = buffer.Get();
            auto* result = reinterpret_cast<ObjectNameInfo*>(answer);
            auto* text = reinterpret_cast<wchar_t*>(answer + sizeof(ObjectNameInfo));
            size_t newBytes = 0;
            if (!NT_SUCCESS(status)
                || !ReplacePathPrefix(result->Name.Buffer, result->Name.Length, map.RealDevicePath, map.VirtualDevicePath,
                    text, MaxUnicodeStringBytes, newBytes))
            {
                return g_ntQueryObject(handle, informationClass, information, length, returnLength);
            }
            const size_t required = sizeof(ObjectNameInfo) + newBytes + sizeof(wchar_t);
            const ULONG requiredLength = static_cast<ULONG>(required);
            if (returnLength != nullptr && !SafeCopy(returnLength, &requiredLength, sizeof(requiredLength)))
            {
                return STATUS_ACCESS_VIOLATION;
            }
            if (required > length)
            {
                return STATUS_BUFFER_OVERFLOW;
            }
            text[newBytes / sizeof(wchar_t)] = L'\0';
            result->Name.Buffer = reinterpret_cast<wchar_t*>(static_cast<BYTE*>(information) + sizeof(ObjectNameInfo));
            result->Name.Length = static_cast<USHORT>(newBytes);
            result->Name.MaximumLength = static_cast<USHORT>(newBytes + sizeof(wchar_t));
            return SafeCopy(information, answer, required) ? STATUS_SUCCESS : STATUS_ACCESS_VIOLATION;
        }

        NTSTATUS NTAPI HookNtClose(HANDLE handle)
        {
            // Remove only after the close succeeded (a failed close keeps the handle open). Once closed,
            // another thread may already have received the same value and a new entry: the generation
            // check leaves that entry alone.
            // A close made by the hook itself (BrokerClient dropping a pipe instance during a directory query,
            // which holds a RemoteHandleTable state lock) never touches the remote table: those handles are
            // not in it.
            ReentryGuard guard;
            ULONG64 generation = 0;
            const bool tracked = g_handles.Find(handle, &generation) >= 0;
            ULONG64 remoteGeneration = 0;
            const bool remote = guard.IsOutermost() && g_remoteHandles.Find(handle, remoteGeneration);
            const NTSTATUS status = g_ntClose(handle);
            if (NT_SUCCESS(status))
            {
                if (tracked)
                {
                    g_handles.Remove(handle, generation);
                }
                // A cached directory name is only a cache: a stale entry for a reused value is also
                // dropped by the next hooked open that returns it.
                g_directoryNames.Remove(handle);
                g_parentScans.Remove(handle);
                if (remote)
                {
                    RemovedRemoteHandle removed;
                    g_remoteHandles.Remove(handle, remoteGeneration, &removed);
                    // A watched directory: Unwatch, and what waits completes with STATUS_NOTIFY_CLEANUP.
                    g_watches.Closed(handle);
                    if (removed.DeleteOnClose)
                    {
                        // The system deleted the cache copy with the handle; machine B follows, unless another file
                        // took the name on machine B since the open (the file id differs).
                        g_resolver->Delete(removed.Mapping, removed.Path, true, removed.FileId);
                        FreeString(removed.Path);
                    }
                    if (removed.WriteToken != 0)
                    {
                        g_resolver->CloseWrite(removed.WriteToken);
                    }
                }
            }
            return status;
        }

        /// A flush of a handle with a write token writes the cache copy back to machine B (plan 4.5).
        NTSTATUS NTAPI HookNtFlushBuffersFile(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock)
        {
            const NTSTATUS status = g_ntFlushBuffersFile(fileHandle, ioStatusBlock);
            ReentryGuard guard;
            if (NT_SUCCESS(status) && guard.IsOutermost())
            {
                const uint64_t token = g_remoteHandles.GetWriteToken(fileHandle);
                if (token != 0)
                {
                    g_resolver->FlushWrite(token);
                }
            }
            return status;
        }

        /// Write-through mode only (installed when the Broker asks): each completed write is sent to machine B.
        NTSTATUS NTAPI HookNtWriteFile(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID buffer, ULONG length, PLARGE_INTEGER byteOffset, PULONG key)
        {
            const NTSTATUS status = g_ntWriteFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, buffer, length, byteOffset, key);
            ReentryGuard guard;
            if (status == STATUS_SUCCESS && guard.IsOutermost())
            {
                const uint64_t token = g_remoteHandles.GetWriteToken(fileHandle);
                if (token != 0)
                {
                    g_resolver->FlushWrite(token);
                }
            }
            return status;
        }

        /// NtQueryDirectoryFile[Ex] on a virtual directory of a remote resolver: answered from machine B's
        /// listing. The answer is complete when the hook returns (no STATUS_PENDING); the event is signalled,
        /// an APC or completion port is not used (see docs/Backlog-vi.md).
        NTSTATUS QueryVirtualDirectory(HANDLE fileHandle, HANDLE event, PIO_STATUS_BLOCK ioStatusBlock, PVOID information,
            ULONG length, ULONG informationClass, bool returnSingleEntry, PUNICODE_STRING fileName, bool restartScan, bool& handled)
        {
            handled = false;
            // Most listings are of real folders: decide before copying anything.
            ULONG64 generation = 0;
            if (!g_resolver->IsRemote() || g_remoteHandles.IsEmpty() || !g_remoteHandles.Find(fileHandle, generation))
            {
                return STATUS_SUCCESS;
            }
            UNICODE_STRING pattern{};
            HeapBuffer patternText(fileName != nullptr ? 0x10000 : 1);
            if (fileName != nullptr)
            {
                if (!SafeCopy(&pattern, fileName, sizeof(pattern)))
                {
                    handled = true;
                    return STATUS_ACCESS_VIOLATION;
                }
                if (pattern.Length % sizeof(wchar_t) != 0 || (pattern.Length > 0 && pattern.Buffer == nullptr))
                {
                    handled = true;
                    return STATUS_INVALID_PARAMETER;
                }
                if (!patternText || !SafeCopy(patternText.Get(), pattern.Buffer, pattern.Length))
                {
                    handled = true;
                    return patternText ? STATUS_ACCESS_VIOLATION : STATUS_NO_MEMORY;
                }
                pattern.Buffer = reinterpret_cast<PWSTR>(patternText.Get());
                pattern.MaximumLength = pattern.Length;
            }
            // Directory buffers are small in practice; a huge one is answered in a bounded scratch buffer.
            constexpr ULONG MaxScratch = 4 * 1024 * 1024;
            const ULONG scratchBytes = length < MaxScratch ? length : MaxScratch;
            HeapBuffer scratch(scratchBytes > 0 ? scratchBytes : 1);
            if (!scratch)
            {
                handled = true;
                return STATUS_NO_MEMORY;
            }
            DirectoryQuery query{};
            query.Output = scratch.Get();
            query.Length = scratchBytes;
            query.InformationClass = informationClass;
            query.ReturnSingleEntry = returnSingleEntry;
            query.RestartScan = restartScan;
            query.FileName = fileName != nullptr ? &pattern : nullptr;
            NTSTATUS status = STATUS_SUCCESS;
            ULONG written = 0;
            if (!g_remoteHandles.Query(fileHandle, *g_resolver, query, status, written))
            {
                return STATUS_SUCCESS; // not a virtual directory
            }
            handled = true;
            if ((written > 0 && !SafeCopy(information, scratch.Get(), written))
                || !SafeSetIoStatus(ioStatusBlock, status, NT_SUCCESS(status) || status == STATUS_BUFFER_OVERFLOW ? written : 0))
            {
                return STATUS_ACCESS_VIOLATION;
            }
            if (event != nullptr)
            {
                SetEvent(event);
            }
            return status;
        }

        // ---- Virtual roots in the listing of their real parent folder ----

        /// A virtual root seen from its parent folder: views into the mapping's VirtualDevicePath.
        struct ParentRoot
        {
            UNICODE_STRING Parent; // \Device\HarddiskVolume3\Work
            UNICODE_STRING Leaf;   // virtual
        };
        constexpr int MaxParentRoots = 32;
        ParentRoot g_parentRoots[MaxParentRoots] = {}; // by mapping index
        uint32_t g_parentRootMask = 0;

        bool EqualPath(const UNICODE_STRING& left, const UNICODE_STRING& right) noexcept
        {
            if (left.Length != right.Length)
            {
                return false;
            }
            for (USHORT i = 0; i < left.Length / sizeof(wchar_t); ++i)
            {
                if (RtlUpcaseUnicodeChar(left.Buffer[i]) != RtlUpcaseUnicodeChar(right.Buffer[i]))
                {
                    return false;
                }
            }
            return true;
        }

        /// Virtual roots whose parent folder is a real folder on a known volume. A root nested in another virtual root
        /// is listed by that root's own listing, a root on a missing drive letter (V:\) has no real parent.
        void CollectParentRoots() noexcept
        {
            const int count = g_resolver->Count();
            for (int i = 0; i < count && i < MaxParentRoots; ++i)
            {
                const PathMapping& map = g_resolver->GetMapping(i);
                const UNICODE_STRING& path = map.VirtualDevicePath;
                if (map.VirtualDriveMissing || path.Length == 0)
                {
                    continue;
                }
                const USHORT chars = path.Length / sizeof(wchar_t);
                USHORT leafStart = chars;
                while (leafStart > 0 && path.Buffer[leafStart - 1] != L'\\')
                {
                    --leafStart;
                }
                if (leafStart <= 1 || leafStart == chars)
                {
                    continue;
                }
                ParentRoot& root = g_parentRoots[i];
                root.Parent.Buffer = path.Buffer;
                root.Parent.Length = static_cast<USHORT>((leafStart - 1) * sizeof(wchar_t));
                root.Parent.MaximumLength = root.Parent.Length;
                root.Leaf.Buffer = path.Buffer + leafStart;
                root.Leaf.Length = static_cast<USHORT>((chars - leafStart) * sizeof(wchar_t));
                root.Leaf.MaximumLength = root.Leaf.Length;
                if (g_resolver->FindMappingByDevicePath(root.Parent) >= 0)
                {
                    root = {};
                    continue;
                }
                g_parentRootMask |= 1u << i;
            }
        }

        /// Mask of the virtual roots that sit directly in the folder of a real directory handle.
        uint32_t RootsInFolder(HANDLE directory) noexcept
        {
            uint32_t roots = 0;
            VisitDirectoryName(directory, [&](const UNICODE_STRING& name)
                {
                    for (int i = 0; i < MaxParentRoots; ++i)
                    {
                        if ((g_parentRootMask & (1u << i)) != 0 && EqualPath(g_parentRoots[i].Parent, name))
                        {
                            roots |= 1u << i;
                        }
                    }
                });
            return roots;
        }

        /// Metadata of a virtual root for its entry in the parent listing: machine B's (Broker) or the backing folder's.
        bool GetRootInfo(int mapping, HookIpc::FileInfo& info) noexcept
        {
            const PathMapping& map = g_resolver->GetMapping(mapping);
            info = {};
            if (g_resolver->IsRemote())
            {
                return NT_SUCCESS(g_resolver->Stat(mapping, map.VirtualNt, PathKind::Nt, info));
            }
            UNICODE_STRING name = map.RealNt;
            OBJECT_ATTRIBUTES attributes{};
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
            NetworkOpenInfo open{};
            if (!NT_SUCCESS(g_ntQueryFullAttributesFile(&attributes, &open)))
            {
                return false;
            }
            info.Attributes = open.FileAttributes;
            info.Size = open.EndOfFile.QuadPart;
            info.AllocationSize = open.AllocationSize.QuadPart;
            info.CreationTime = open.CreationTime.QuadPart;
            info.LastAccessTime = open.LastAccessTime.QuadPart;
            info.LastWriteTime = open.LastWriteTime.QuadPart;
            info.ChangeTime = open.ChangeTime.QuadPart;
            return true;
        }

        /// True when the real folder already holds a file or folder with the root's name (the system listed it).
        bool ExistsInParent(int mapping) noexcept
        {
            UNICODE_STRING name = g_resolver->GetMapping(mapping).VirtualDevicePath;
            OBJECT_ATTRIBUTES attributes{};
            InitializeObjectAttributes(&attributes, &name, OBJ_CASE_INSENSITIVE, nullptr, nullptr);
            NetworkOpenInfo open{};
            return NT_SUCCESS(g_ntQueryFullAttributesFile(&attributes, &open));
        }

        /// After the system answered NtQueryDirectoryFile[Ex] on a real directory handle (synchronously): a scan of the
        /// parent folder of virtual roots gets their entries once the system's own entries are exhausted
        /// (STATUS_NO_MORE_FILES, or STATUS_NO_SUCH_FILE for a pattern nothing real matches). status is replaced when
        /// entries were added. A pattern given with the first query is remembered, since later queries come without one.
        void AddVirtualRootEntries(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information, ULONG length,
            ULONG informationClass, bool returnSingleEntry, PUNICODE_STRING fileName, bool restartScan, NTSTATUS& status) noexcept
        {
            const bool exhausted = status == STATUS_NO_MORE_FILES || status == STATUS_NO_SUCH_FILE;
            const bool startsScan = fileName != nullptr || restartScan;
            if (g_parentRootMask == 0 || (!exhausted && !startsScan) || status == STATUS_PENDING)
            {
                return;
            }
            if (startsScan)
            {
                const uint32_t roots = RootsInFolder(fileHandle);
                if (roots == 0)
                {
                    g_parentScans.Remove(fileHandle);
                    return;
                }
                UNICODE_STRING pattern{};
                HeapBuffer patternText(fileName != nullptr ? MaxUnicodeStringBytes : 1);
                if (fileName != nullptr)
                {
                    if (!patternText || !SafeCopy(&pattern, fileName, sizeof(pattern)) || pattern.Length > MaxUnicodeStringBytes
                        || (pattern.Length > 0 && !SafeCopy(patternText.Get(), pattern.Buffer, pattern.Length)))
                    {
                        return;
                    }
                    pattern.Buffer = reinterpret_cast<PWSTR>(patternText.Get());
                    pattern.MaximumLength = pattern.Length;
                }
                g_parentScans.Start(fileHandle, roots, fileName != nullptr ? &pattern : nullptr);
            }
            if (!exhausted)
            {
                return;
            }
            uint32_t pending = 0;
            UNICODE_STRING pattern{};
            if (!g_parentScans.Get(fileHandle, pending, pattern))
            {
                // A scan started without a pattern (.NET passes none): any name matches.
                const uint32_t roots = startsScan ? 0 : RootsInFolder(fileHandle);
                if (roots == 0)
                {
                    return;
                }
                g_parentScans.Start(fileHandle, roots, nullptr);
                pending = roots;
            }
            const DirectoryEntryFormat format(informationClass);
            constexpr ULONG MaxScratch = 64 * 1024;
            const ULONG scratchBytes = length < MaxScratch ? length : MaxScratch;
            HeapBuffer scratch(scratchBytes > 0 ? scratchBytes : 1);
            if (!format.IsSupported() || !scratch || scratchBytes < format.NameOffset())
            {
                FreeString(pattern);
                return;
            }
            ULONG written = 0;
            ULONG previous = 0;
            bool any = false;
            uint32_t listed = 0;
            for (int i = 0; i < MaxParentRoots; ++i)
            {
                if ((pending & (1u << i)) == 0)
                {
                    continue;
                }
                UNICODE_STRING leaf = g_parentRoots[i].Leaf;
                HookIpc::FileInfo info{};
                if ((pattern.Length > 0 && !RtlIsNameInExpression(&pattern, &leaf, TRUE, nullptr)) || ExistsInParent(i)
                    || !GetRootInfo(i, info))
                {
                    listed |= 1u << i; // not part of this scan
                    continue;
                }
                const ULONG start = AlignDirectoryEntry(written);
                if (static_cast<ULONGLONG>(start) + format.NameOffset() + leaf.Length > scratchBytes)
                {
                    if (!any)
                    {
                        status = STATUS_BUFFER_OVERFLOW;
                        SafeSetIoStatus(ioStatusBlock, status, 0);
                    }
                    break;
                }
                format.Write(scratch.Get() + start, info, leaf.Buffer, leaf.Length);
                if (any)
                {
                    const ULONG next = start - previous;
                    memcpy(scratch.Get() + previous, &next, sizeof(next));
                }
                any = true;
                previous = start;
                written = start + format.NameOffset() + leaf.Length;
                listed |= 1u << i;
                if (returnSingleEntry)
                {
                    break;
                }
            }
            FreeString(pattern);
            g_parentScans.MarkListed(fileHandle, listed);
            if (!any)
            {
                return;
            }
            if (!SafeCopy(information, scratch.Get(), written) || !SafeSetIoStatus(ioStatusBlock, STATUS_SUCCESS, written))
            {
                status = STATUS_ACCESS_VIOLATION;
                return;
            }
            status = STATUS_SUCCESS;
        }

        bool IsCurrentProcess(HANDLE process) noexcept
        {
            return process == GetCurrentProcess() || (process != nullptr && GetProcessId(process) == GetCurrentProcessId());
        }

        /// DuplicateHandle inside the process: the copy of a virtual handle is virtual too, a copy that
        /// lands on a stale value replaces its entry, and DUPLICATE_CLOSE_SOURCE closes like NtClose.
        /// Duplicates into other processes are not tracked (backlog).
        NTSTATUS NTAPI HookNtDuplicateObject(HANDLE sourceProcess, HANDLE sourceHandle, HANDLE targetProcess, PHANDLE targetHandle,
            ACCESS_MASK desiredAccess, ULONG handleAttributes, ULONG options)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return g_ntDuplicateObject(sourceProcess, sourceHandle, targetProcess, targetHandle, desiredAccess, handleAttributes, options);
            }
            const bool sourceIsSelf = IsCurrentProcess(sourceProcess);
            ULONG64 generation = 0;
            const int mapping = sourceIsSelf ? g_handles.Find(sourceHandle, &generation) : -1;
            // Remote entry of the source (Broker): the copy reports the same machine B metadata.
            int remoteMapping = -1;
            UNICODE_STRING remotePath{};
            HookIpc::FileInfo remoteInfo{};
            uint64_t remoteToken = 0;
            bool remoteDeleteOnClose = false;
            const bool remote = sourceIsSelf && !g_remoteHandles.IsEmpty()
                && g_remoteHandles.GetEntry(sourceHandle, remoteMapping, remotePath, remoteInfo, &remoteToken, &remoteDeleteOnClose);
            const NTSTATUS status = g_ntDuplicateObject(sourceProcess, sourceHandle, targetProcess, targetHandle, desiredAccess,
                handleAttributes, options);
            if (!NT_SUCCESS(status))
            {
                FreeString(remotePath);
                return status;
            }
            if (sourceIsSelf && (options & DUPLICATE_CLOSE_SOURCE) != 0)
            {
                if (mapping >= 0)
                {
                    g_handles.Remove(sourceHandle, generation);
                }
                g_directoryNames.Remove(sourceHandle);
                g_parentScans.Remove(sourceHandle);
                g_remoteHandles.Remove(sourceHandle);
                g_watches.Closed(sourceHandle);
            }
            HANDLE copy = nullptr;
            if (targetHandle != nullptr && targetProcess != nullptr && SafeCopy(&copy, targetHandle, sizeof(copy))
                && IsCurrentProcess(targetProcess))
            {
                g_directoryNames.Remove(copy);
                g_parentScans.Remove(copy);
                if (mapping >= 0)
                {
                    g_handles.Add(copy, mapping);
                }
                else
                {
                    g_handles.Remove(copy);
                }
                if (remote)
                {
                    // Same file object: the copy shares the write token (counted) and delete-on-close.
                    g_remoteHandles.Add(copy, remoteMapping, remotePath, remoteInfo, remoteToken, remoteDeleteOnClose);
                }
                else
                {
                    g_remoteHandles.Remove(copy);
                }
            }
            FreeString(remotePath);
            return status;
        }

        NTSTATUS NTAPI HookNtQueryDirectoryFile(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID information, ULONG length, ULONG informationClass, BOOLEAN returnSingleEntry,
            PUNICODE_STRING fileName, BOOLEAN restartScan)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return g_ntQueryDirectoryFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, information, length,
                    informationClass, returnSingleEntry, fileName, restartScan);
            }
            bool handled = false;
            NTSTATUS status = QueryVirtualDirectory(fileHandle, event, ioStatusBlock, information, length, informationClass,
                returnSingleEntry != FALSE, fileName, restartScan != FALSE, handled);
            if (handled)
            {
                return status;
            }
            status = g_ntQueryDirectoryFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, information, length,
                informationClass, returnSingleEntry, fileName, restartScan);
            AddVirtualRootEntries(fileHandle, ioStatusBlock, information, length, informationClass, returnSingleEntry != FALSE, fileName,
                restartScan != FALSE, status);
            return status;
        }

        NTSTATUS NTAPI HookNtQueryDirectoryFileEx(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID information, ULONG length, ULONG informationClass, ULONG queryFlags,
            PUNICODE_STRING fileName)
        {
            constexpr ULONG RestartScanFlag = 0x1;       // SL_RESTART_SCAN
            constexpr ULONG ReturnSingleEntryFlag = 0x2; // SL_RETURN_SINGLE_ENTRY
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return g_ntQueryDirectoryFileEx(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, information, length,
                    informationClass, queryFlags, fileName);
            }
            const bool single = (queryFlags & ReturnSingleEntryFlag) != 0;
            const bool restart = (queryFlags & RestartScanFlag) != 0;
            bool handled = false;
            NTSTATUS status = QueryVirtualDirectory(fileHandle, event, ioStatusBlock, information, length, informationClass,
                single, fileName, restart, handled);
            if (handled)
            {
                return status;
            }
            status = g_ntQueryDirectoryFileEx(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, information, length,
                informationClass, queryFlags, fileName);
            AddVirtualRootEntries(fileHandle, ioStatusBlock, information, length, informationClass, single, fileName, restart, status);
            return status;
        }

        /// NtNotifyChangeDirectoryFile[Ex] on a virtual directory of a remote resolver: changes come from machine B
        /// (DirectoryWatches). Other handles, a virtual file included, go to the system.
        NTSTATUS NotifyVirtualDirectory(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID buffer, ULONG length, ULONG completionFilter, BOOLEAN watchTree,
            ULONG informationClass, bool& handled)
        {
            handled = false;
            ULONG64 generation = 0;
            int mapping = -1;
            UNICODE_STRING path{};
            if (!g_resolver->IsRemote() || g_remoteHandles.IsEmpty() || !g_remoteHandles.Find(fileHandle, generation)
                || !g_remoteHandles.GetDirectoryPath(fileHandle, mapping, path))
            {
                return STATUS_SUCCESS;
            }
            handled = true;
            // Synchronous file object: the call itself waits for the change, like the I/O manager.
            ULONG mode = 0;
            IO_STATUS_BLOCK modeStatus{};
            const NTSTATUS queried = g_ntQueryInformationFile(fileHandle, &modeStatus, &mode, sizeof(mode), FileModeInformation);
            if (!NT_SUCCESS(queried))
            {
                FreeString(path);
                return queried;
            }
            NotifyCall call{};
            call.Handle = fileHandle;
            call.Generation = generation;
            call.Mapping = mapping;
            call.Path = &path;
            call.Event = event;
            call.ApcRoutine = apcRoutine;
            call.ApcContext = apcContext;
            call.IoStatusBlock = ioStatusBlock;
            call.Buffer = buffer;
            call.Length = length;
            call.CompletionFilter = completionFilter;
            call.WatchTree = watchTree != FALSE;
            call.InformationClass = informationClass;
            call.Synchronous = (mode & (FileSynchronousIoAlert | FileSynchronousIoNonAlert)) != 0;
            call.Alertable = (mode & FileSynchronousIoAlert) != 0;
            const NTSTATUS status = g_watches.Notify(call);
            FreeString(path);
            return status;
        }

        NTSTATUS NTAPI HookNtNotifyChangeDirectoryFile(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID buffer, ULONG length, ULONG completionFilter, BOOLEAN watchTree)
        {
            constexpr ULONG DirectoryNotifyInformation = 1;
            ReentryGuard guard;
            if (guard.IsOutermost())
            {
                bool handled = false;
                const NTSTATUS status = NotifyVirtualDirectory(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, buffer, length,
                    completionFilter, watchTree, DirectoryNotifyInformation, handled);
                if (handled)
                {
                    return status;
                }
            }
            return g_ntNotifyChangeDirectoryFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, buffer, length,
                completionFilter, watchTree);
        }

        NTSTATUS NTAPI HookNtNotifyChangeDirectoryFileEx(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID buffer, ULONG length, ULONG completionFilter, BOOLEAN watchTree,
            ULONG informationClass)
        {
            ReentryGuard guard;
            if (guard.IsOutermost())
            {
                bool handled = false;
                const NTSTATUS status = NotifyVirtualDirectory(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, buffer, length,
                    completionFilter, watchTree, informationClass, handled);
                if (handled)
                {
                    return status;
                }
            }
            return g_ntNotifyChangeDirectoryFileEx(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, buffer, length,
                completionFilter, watchTree, informationClass);
        }

        /// CancelIo: the requests of this thread on the handle.
        NTSTATUS NTAPI HookNtCancelIoFile(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock)
        {
            const NTSTATUS status = g_ntCancelIoFile(fileHandle, ioStatusBlock);
            ReentryGuard guard;
            if (guard.IsOutermost() && !g_watches.IsEmpty() && g_watches.Cancel(fileHandle, GetCurrentThreadId(), nullptr, false) > 0)
            {
                return SafeSetIoStatus(ioStatusBlock, STATUS_SUCCESS, 0) ? STATUS_SUCCESS : STATUS_ACCESS_VIOLATION;
            }
            return status;
        }

        /// CancelIoEx: the request with that IO_STATUS_BLOCK (every request when none) on the handle, from any thread.
        NTSTATUS NTAPI HookNtCancelIoFileEx(HANDLE fileHandle, PIO_STATUS_BLOCK ioRequestToCancel, PIO_STATUS_BLOCK ioStatusBlock)
        {
            const NTSTATUS status = g_ntCancelIoFileEx(fileHandle, ioRequestToCancel, ioStatusBlock);
            ReentryGuard guard;
            if (guard.IsOutermost() && !g_watches.IsEmpty() && g_watches.Cancel(fileHandle, 0, ioRequestToCancel, false) > 0)
            {
                return SafeSetIoStatus(ioStatusBlock, STATUS_SUCCESS, 0) ? STATUS_SUCCESS : STATUS_ACCESS_VIOLATION;
            }
            return status;
        }

        /// CancelSynchronousIo: the synchronous requests of that thread.
        NTSTATUS NTAPI HookNtCancelSynchronousIoFile(HANDLE threadHandle, PIO_STATUS_BLOCK ioRequestToCancel, PIO_STATUS_BLOCK ioStatusBlock)
        {
            const NTSTATUS status = g_ntCancelSynchronousIoFile(threadHandle, ioRequestToCancel, ioStatusBlock);
            ReentryGuard guard;
            const DWORD threadId = GetThreadId(threadHandle);
            if (guard.IsOutermost() && !g_watches.IsEmpty() && threadId != 0
                && g_watches.Cancel(nullptr, threadId, ioRequestToCancel, true) > 0)
            {
                return SafeSetIoStatus(ioStatusBlock, STATUS_SUCCESS, 0) ? STATUS_SUCCESS : STATUS_ACCESS_VIOLATION;
            }
            return status;
        }

        /// Extra record of an inherited remote handle (init block): machine B's metadata and the Broker's write
        /// token, then the virtual NT path (PathBytes bytes, no terminator). Read with memcpy: records are not
        /// aligned. Same layout in x86 and x64 (a child may have the other architecture).
        struct InheritedRemoteRecord
        {
            HookIpc::FileInfo Info;
            uint32_t PathBytes;
            uint32_t Reserved;
            uint64_t WriteToken; // retained for the child by RetainForChild; 0 = none
        };
        static_assert(sizeof(InheritedRemoteRecord) == HookIpc::FileInfoSize + 16, "record layout must not depend on the architecture");

        /// Handle inheritance (plan 5.5): entries travel in the child's init block (see HandleInheritance.h).
        /// With the Broker the record of a remote handle also carries its virtual path and machine B's
        /// metadata, so the child tracks it like its own open without asking the Broker; a directory starts
        /// a new listing in the child.
        class InitBlockHandleInheritance final : public IHandleInheritance
        {
        public:
            void ExportForChild(std::vector<Inject::InheritedHandle>& handles, std::vector<BYTE>& extra) const override
            {
                // A full copy may have been cut short: grow until the whole table fits.
                std::vector<HANDLE> values(256);
                std::vector<int> mappings(256);
                size_t count = g_handles.CopyEntries(values.data(), mappings.data(), values.size());
                while (count == values.size())
                {
                    values.resize(values.size() * 2);
                    mappings.resize(mappings.size() * 2);
                    count = g_handles.CopyEntries(values.data(), mappings.data(), values.size());
                }
                for (size_t i = 0; i < count; ++i)
                {
                    DWORD flags = 0;
                    if (GetHandleInformation(values[i], &flags) && (flags & HANDLE_FLAG_INHERIT) != 0)
                    {
                        handles.push_back({ reinterpret_cast<ULONG_PTR>(values[i]), mappings[i], Inject::NoExtra });
                        AppendRemoteRecord(values[i], handles.back(), extra);
                    }
                }
            }

            void ImportFromParent(const Inject::InheritedHandle* handles, size_t count, const BYTE* extra,
                size_t extraBytes) noexcept override
            {
                for (size_t i = 0; i < count; ++i)
                {
                    const int mapping = handles[i].Mapping;
                    if (mapping < 0 || mapping >= g_resolver->Count())
                    {
                        continue;
                    }
                    const HANDLE handle = reinterpret_cast<HANDLE>(static_cast<ULONG_PTR>(handles[i].Handle));
                    g_handles.Add(handle, mapping);
                    const ULONG offset = handles[i].ExtraOffset;
                    InheritedRemoteRecord record{};
                    if (!g_resolver->IsRemote() || offset == Inject::NoExtra || offset > extraBytes
                        || extraBytes - offset < sizeof(record))
                    {
                        continue;
                    }
                    memcpy(&record, extra + offset, sizeof(record));
                    const size_t pathOffset = offset + sizeof(record);
                    if (record.PathBytes == 0 || record.PathBytes % sizeof(wchar_t) != 0 || record.PathBytes > 0xFFFE
                        || extraBytes - pathOffset < record.PathBytes)
                    {
                        continue;
                    }
                    UNICODE_STRING path{};
                    if (!AllocateString(path, record.PathBytes))
                    {
                        continue;
                    }
                    memcpy(path.Buffer, extra + pathOffset, record.PathBytes);
                    path.Length = static_cast<USHORT>(record.PathBytes);
                    // The parent retained the token for this process: its last close sends Close like the parent's.
                    g_remoteHandles.Add(handle, mapping, path, record.Info, record.WriteToken);
                    if (record.Info.RangeId != 0)
                    {
                        g_anyRange.store(true, std::memory_order_relaxed); // reads of the inherited handle ask for ranges too
                    }
                    FreeString(path);
                }
            }

            void RetainForChild(const std::vector<Inject::InheritedHandle>& handles, const std::vector<BYTE>& extra) const noexcept override
            {
                // One retain per token, as the child's table counts its copies and closes the token once.
                std::vector<uint64_t> retained;
                for (const Inject::InheritedHandle& handle : handles)
                {
                    InheritedRemoteRecord record{};
                    const size_t offset = handle.ExtraOffset;
                    if (offset == Inject::NoExtra || offset > extra.size() || extra.size() - offset < sizeof(record))
                    {
                        continue;
                    }
                    memcpy(&record, extra.data() + offset, sizeof(record));
                    if (record.WriteToken == 0 || std::find(retained.begin(), retained.end(), record.WriteToken) != retained.end())
                    {
                        continue;
                    }
                    try
                    {
                        retained.push_back(record.WriteToken);
                    }
                    catch (...)
                    {
                        // Out of memory: retain anyway (a duplicate retain only keeps the file open until the session ends).
                    }
                    g_resolver->RetainWrite(record.WriteToken);
                }
            }

        private:
            static void AppendRemoteRecord(HANDLE handle, Inject::InheritedHandle& entry, std::vector<BYTE>& extra)
            {
                if (g_remoteHandles.IsEmpty())
                {
                    return;
                }
                InheritedRemoteRecord record{};
                int mapping = -1;
                UNICODE_STRING path{};
                if (!g_remoteHandles.GetEntry(handle, mapping, path, record.Info, &record.WriteToken))
                {
                    return;
                }
                record.PathBytes = path.Length;
                try
                {
                    const BYTE* head = reinterpret_cast<const BYTE*>(&record);
                    const BYTE* text = reinterpret_cast<const BYTE*>(path.Buffer);
                    const size_t offset = extra.size();
                    extra.insert(extra.end(), head, head + sizeof(record));
                    extra.insert(extra.end(), text, text + path.Length);
                    entry.ExtraOffset = static_cast<ULONG>(offset);
                }
                catch (...)
                {
                    FreeString(path);
                    throw;
                }
                FreeString(path);
            }
        };

        InitBlockHandleInheritance g_inheritance;

        struct HookEntry
        {
            const char* Name;
            void* Detour;
            void** Original;
            bool Optional; // export missing on older Windows: skip instead of failing
        };
    }

    DWORD InstallFileHooks(const IPathResolver& resolver) noexcept
    {
        g_resolver = &resolver;
        CollectParentRoots();
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll == nullptr)
        {
            return GetLastError();
        }
        const bool remote = resolver.IsRemote();
        if (remote)
        {
            const DWORD initialized = g_watches.Initialize(resolver);
            if (initialized != ERROR_SUCCESS)
            {
                return initialized;
            }
        }
        const HookEntry hooks[] = {
            { "NtCreateFile", reinterpret_cast<void*>(&HookNtCreateFile), reinterpret_cast<void**>(&g_ntCreateFile) },
            { "NtOpenFile", reinterpret_cast<void*>(&HookNtOpenFile), reinterpret_cast<void**>(&g_ntOpenFile) },
            { "NtQueryAttributesFile", reinterpret_cast<void*>(&HookNtQueryAttributesFile), reinterpret_cast<void**>(&g_ntQueryAttributesFile) },
            { "NtQueryFullAttributesFile", reinterpret_cast<void*>(&HookNtQueryFullAttributesFile), reinterpret_cast<void**>(&g_ntQueryFullAttributesFile) },
            { "NtQueryInformationFile", reinterpret_cast<void*>(&HookNtQueryInformationFile), reinterpret_cast<void**>(&g_ntQueryInformationFile) },
            { "NtSetInformationFile", reinterpret_cast<void*>(&HookNtSetInformationFile), reinterpret_cast<void**>(&g_ntSetInformationFile) },
            { "NtQueryObject", reinterpret_cast<void*>(&HookNtQueryObject), reinterpret_cast<void**>(&g_ntQueryObject) },
            { "NtClose", reinterpret_cast<void*>(&HookNtClose), reinterpret_cast<void**>(&g_ntClose) },
            { "NtDeleteFile", reinterpret_cast<void*>(&HookNtDeleteFile), reinterpret_cast<void**>(&g_ntDeleteFile) },
            { "NtDuplicateObject", reinterpret_cast<void*>(&HookNtDuplicateObject), reinterpret_cast<void**>(&g_ntDuplicateObject) },
            { "NtQueryInformationByName", reinterpret_cast<void*>(&HookNtQueryInformationByName),
                reinterpret_cast<void**>(&g_ntQueryInformationByName), true },
            { "NtQueryDirectoryFile", reinterpret_cast<void*>(&HookNtQueryDirectoryFile), reinterpret_cast<void**>(&g_ntQueryDirectoryFile) },
            { "NtQueryDirectoryFileEx", reinterpret_cast<void*>(&HookNtQueryDirectoryFileEx),
                reinterpret_cast<void**>(&g_ntQueryDirectoryFileEx), true },
            { "NtFlushBuffersFile", reinterpret_cast<void*>(&HookNtFlushBuffersFile), reinterpret_cast<void**>(&g_ntFlushBuffersFile) },
            // Write-through only: NtWriteFile is the hottest call, leave it alone otherwise.
            { resolver.IsWriteThrough() ? "NtWriteFile" : nullptr, reinterpret_cast<void*>(&HookNtWriteFile),
                reinterpret_cast<void**>(&g_ntWriteFile), true },
            // Change notifications come from machine B only with the Broker.
            { remote ? "NtNotifyChangeDirectoryFile" : nullptr, reinterpret_cast<void*>(&HookNtNotifyChangeDirectoryFile),
                reinterpret_cast<void**>(&g_ntNotifyChangeDirectoryFile) },
            { remote ? "NtNotifyChangeDirectoryFileEx" : nullptr, reinterpret_cast<void*>(&HookNtNotifyChangeDirectoryFileEx),
                reinterpret_cast<void**>(&g_ntNotifyChangeDirectoryFileEx), true },
            // M8 (Broker only): reads and mappings of ranged cache copies, byte range locks forwarded to machine B.
            { remote ? "NtReadFile" : nullptr, reinterpret_cast<void*>(&HookNtReadFile), reinterpret_cast<void**>(&g_ntReadFile) },
            { remote ? "NtReadFileScatter" : nullptr, reinterpret_cast<void*>(&HookNtReadFileScatter),
                reinterpret_cast<void**>(&g_ntReadFileScatter), true },
            { remote ? "NtCreateSection" : nullptr, reinterpret_cast<void*>(&HookNtCreateSection), reinterpret_cast<void**>(&g_ntCreateSection) },
            { remote ? "NtCreateSectionEx" : nullptr, reinterpret_cast<void*>(&HookNtCreateSectionEx),
                reinterpret_cast<void**>(&g_ntCreateSectionEx), true },
            { remote ? "NtLockFile" : nullptr, reinterpret_cast<void*>(&HookNtLockFile), reinterpret_cast<void**>(&g_ntLockFile) },
            { remote ? "NtUnlockFile" : nullptr, reinterpret_cast<void*>(&HookNtUnlockFile), reinterpret_cast<void**>(&g_ntUnlockFile) },
            { remote ? "NtCancelIoFile" : nullptr, reinterpret_cast<void*>(&HookNtCancelIoFile), reinterpret_cast<void**>(&g_ntCancelIoFile) },
            { remote ? "NtCancelIoFileEx" : nullptr, reinterpret_cast<void*>(&HookNtCancelIoFileEx),
                reinterpret_cast<void**>(&g_ntCancelIoFileEx), true },
            { remote ? "NtCancelSynchronousIoFile" : nullptr, reinterpret_cast<void*>(&HookNtCancelSynchronousIoFile),
                reinterpret_cast<void**>(&g_ntCancelSynchronousIoFile), true },
        };
        for (const HookEntry& hook : hooks)
        {
            if (hook.Name == nullptr)
            {
                continue;
            }
            void* target = reinterpret_cast<void*>(GetProcAddress(ntdll, hook.Name));
            if (target == nullptr)
            {
                if (hook.Optional)
                {
                    continue;
                }
                return ERROR_PROC_NOT_FOUND;
            }
            if (MH_CreateHook(target, hook.Detour, hook.Original) != MH_OK)
            {
                return ERROR_GEN_FAILURE;
            }
        }
        return ERROR_SUCCESS;
    }

    IHandleInheritance& GetLocalHandleInheritance() noexcept
    {
        return g_inheritance;
    }
}
