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
#include "FileHooks.h"
#include "DirectoryNameCache.h"
#include "HandleInheritance.h"
#include "HandleTable.h"
#include "HookGuard.h"
#include "PathString.h"
#include "RemoteHandles.h"
#include <MinHook.h>
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

        // Caller memory: hooks read and write pointers that the kernel would probe and answer with
        // STATUS_ACCESS_VIOLATION. Every access to caller memory goes through these SEH-guarded copies
        // (no C++ objects in them, so __try is allowed).
        int AccessFilter(DWORD code) noexcept
        {
            return code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_DATATYPE_MISALIGNMENT || code == EXCEPTION_IN_PAGE_ERROR
                ? EXCEPTION_EXECUTE_HANDLER
                : EXCEPTION_CONTINUE_SEARCH;
        }

        bool SafeCopy(void* destination, const void* source, size_t bytes) noexcept
        {
            __try
            {
                memcpy(destination, source, bytes);
                return true;
            }
            __except (AccessFilter(GetExceptionCode()))
            {
                return false;
            }
        }

        /// Writes the IO_STATUS_BLOCK of a hook that answered by itself.
        bool SafeSetIoStatus(PIO_STATUS_BLOCK ioStatusBlock, NTSTATUS status, ULONG_PTR information) noexcept
        {
            IO_STATUS_BLOCK value{};
            value.Status = status;
            value.Information = information;
            return SafeCopy(ioStatusBlock, &value, sizeof(value));
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
                    if (mapping < 0)
                    {
                        return;
                    }
                    _mapping = mapping;
                    Translate(_nameCopy, PathKind::Nt);
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

        private:
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
                const NTSTATUS status = g_resolver->Resolve(_mapping, path, kind, *_intent, _name, &_info);
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
                const bool cached = g_directoryNames.Visit(root, [&](const UNICODE_STRING& rootName)
                    {
                        ResolveJoined(rootName, relative);
                    });
                if (cached)
                {
                    return;
                }
                const size_t bufferBytes = sizeof(ObjectNameInfo) + MaxNameBytes;
                HeapBuffer buffer(bufferBytes);
                if (!buffer)
                {
                    return;
                }
                const auto* info = reinterpret_cast<const ObjectNameInfo*>(buffer.Get());
                const NTSTATUS status = g_ntQueryObject(root, ObjectNameInformation, buffer.Get(), static_cast<ULONG>(bufferBytes), nullptr);
                UNICODE_STRING rootName{};
                if (NT_SUCCESS(status) && info->Name.Buffer != nullptr)
                {
                    rootName = info->Name;
                    while (rootName.Length > 0 && rootName.Buffer[rootName.Length / sizeof(wchar_t) - 1] == L'\\')
                    {
                        rootName.Length -= sizeof(wchar_t);
                    }
                }
                g_directoryNames.Add(root, rootName.Buffer, rootName.Length);
                ResolveJoined(rootName, relative);
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
            wchar_t _inlineName[260];
            wchar_t* _heapName = nullptr;
            int _mapping = -1;
            bool _rewritten = false;
            NTSTATUS _error = STATUS_SUCCESS;
        };

        /// Remote resolver: a handle opened through a virtual path reports machine B's metadata, and a directory
        /// gets its listings from machine B.
        void TrackRemoteHandle(HANDLE handle, const RedirectedAttributes& redirect) noexcept
        {
            const UNICODE_STRING& matched = redirect.MatchedPath();
            if (!g_resolver->IsRemote() || !redirect.Matched() || matched.Length == 0)
            {
                g_remoteHandles.Remove(handle);
                return;
            }
            if (redirect.MatchedKind() == PathKind::Nt)
            {
                g_remoteHandles.Add(handle, redirect.Mapping(), matched, redirect.Info());
                return;
            }
            // Device spelling (joined with the current directory): the same path under the NT root.
            const PathMapping& map = g_resolver->GetMapping(redirect.Mapping());
            UNICODE_STRING ntPath{};
            if (ConcatString(ntPath, map.VirtualNt,
                    reinterpret_cast<const wchar_t*>(reinterpret_cast<const BYTE*>(matched.Buffer) + map.VirtualDevicePath.Length),
                    static_cast<size_t>(matched.Length) - map.VirtualDevicePath.Length))
            {
                g_remoteHandles.Add(handle, redirect.Mapping(), ntPath, redirect.Info());
                FreeString(ntPath);
            }
        }

        /// A new handle value may equal a handle that was closed without NtClose (NtDuplicateObject with
        /// DUPLICATE_CLOSE_SOURCE, kernel): replace or drop stale entries.
        void TrackOpenedHandle(PHANDLE handlePointer, const RedirectedAttributes& redirect) noexcept
        {
            HANDLE handle = nullptr;
            if (!SafeCopy(&handle, handlePointer, sizeof(handle)))
            {
                return;
            }
            g_directoryNames.Remove(handle);
            if (redirect.Matched())
            {
                g_handles.Add(handle, redirect.Mapping());
            }
            else
            {
                g_handles.Remove(handle);
            }
            TrackRemoteHandle(handle, redirect);
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
            const OpenIntent intent{ desiredAccess, createDisposition, createOptions };
            RedirectedAttributes redirect(objectAttributes, &intent);
            if (redirect.Error() != STATUS_SUCCESS)
            {
                return redirect.Error();
            }
            const NTSTATUS status = g_ntCreateFile(fileHandle, desiredAccess, redirect.Get(), ioStatusBlock,
                allocationSize, fileAttributes, shareAccess, createDisposition, createOptions, eaBuffer, eaLength);
            if (NT_SUCCESS(status))
            {
                TrackOpenedHandle(fileHandle, redirect);
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
            const OpenIntent intent{ desiredAccess, FileOpenDisposition, openOptions };
            RedirectedAttributes redirect(objectAttributes, &intent);
            if (redirect.Error() != STATUS_SUCCESS)
            {
                return redirect.Error();
            }
            const NTSTATUS status = g_ntOpenFile(fileHandle, desiredAccess, redirect.Get(), ioStatusBlock, shareAccess, openOptions);
            if (NT_SUCCESS(status))
            {
                TrackOpenedHandle(fileHandle, redirect);
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

        NTSTATUS NTAPI HookNtSetInformationFile(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information,
            ULONG length, ULONG informationClass)
        {
            constexpr size_t headerBytes = offsetof(FileRenameInfo, FileName);
            ReentryGuard guard;
            if (!guard.IsOutermost() || !IsRenameClass(informationClass) || information == nullptr || length < headerBytes)
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
                if (remote)
                {
                    g_remoteHandles.Remove(handle, remoteGeneration);
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
            const bool remote = sourceIsSelf && !g_remoteHandles.IsEmpty()
                && g_remoteHandles.GetEntry(sourceHandle, remoteMapping, remotePath, remoteInfo);
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
                g_remoteHandles.Remove(sourceHandle);
            }
            HANDLE copy = nullptr;
            if (targetHandle != nullptr && targetProcess != nullptr && SafeCopy(&copy, targetHandle, sizeof(copy))
                && IsCurrentProcess(targetProcess))
            {
                g_directoryNames.Remove(copy);
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
                    g_remoteHandles.Add(copy, remoteMapping, remotePath, remoteInfo);
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
            if (guard.IsOutermost())
            {
                bool handled = false;
                const NTSTATUS status = QueryVirtualDirectory(fileHandle, event, ioStatusBlock, information, length, informationClass,
                    returnSingleEntry != FALSE, fileName, restartScan != FALSE, handled);
                if (handled)
                {
                    return status;
                }
            }
            return g_ntQueryDirectoryFile(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, information, length,
                informationClass, returnSingleEntry, fileName, restartScan);
        }

        NTSTATUS NTAPI HookNtQueryDirectoryFileEx(HANDLE fileHandle, HANDLE event, PIO_APC_ROUTINE apcRoutine, PVOID apcContext,
            PIO_STATUS_BLOCK ioStatusBlock, PVOID information, ULONG length, ULONG informationClass, ULONG queryFlags,
            PUNICODE_STRING fileName)
        {
            constexpr ULONG RestartScanFlag = 0x1;       // SL_RESTART_SCAN
            constexpr ULONG ReturnSingleEntryFlag = 0x2; // SL_RETURN_SINGLE_ENTRY
            ReentryGuard guard;
            if (guard.IsOutermost())
            {
                bool handled = false;
                const NTSTATUS status = QueryVirtualDirectory(fileHandle, event, ioStatusBlock, information, length, informationClass,
                    (queryFlags & ReturnSingleEntryFlag) != 0, fileName, (queryFlags & RestartScanFlag) != 0, handled);
                if (handled)
                {
                    return status;
                }
            }
            return g_ntQueryDirectoryFileEx(fileHandle, event, apcRoutine, apcContext, ioStatusBlock, information, length,
                informationClass, queryFlags, fileName);
        }

        /// Extra record of an inherited remote handle (init block): machine B's metadata, then the virtual
        /// NT path (PathBytes bytes, no terminator). Read with memcpy: records are not aligned.
        struct InheritedRemoteRecord
        {
            HookIpc::FileInfo Info;
            ULONG PathBytes;
        };

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
                    g_remoteHandles.Add(handle, mapping, path, record.Info);
                    FreeString(path);
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
                if (!g_remoteHandles.GetEntry(handle, mapping, path, record.Info))
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
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (ntdll == nullptr)
        {
            return GetLastError();
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
        };
        for (const HookEntry& hook : hooks)
        {
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
