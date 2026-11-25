// ntdll file hooks for the M1 spike (plan 4.2, 4.4).
//
// Model: a virtual path is opened as the real backing file (NtCreateFile on the translated path) and
// the real handle is returned to the process. Reads, writes, mapping and directory enumeration then
// run on the real handle unchanged. Only names leak, so the hooks rewrite name queries
// (NtQueryInformationFile name classes, NtQueryObject) for handles recorded in the handle table.
//
// Directory listings (NtQueryDirectoryFile[Ex]) return leaf names only, so with a local backing
// folder they need no rewriting in M1; M2 replaces them with listings built from machine B.
#include "FileHooks.h"
#include "DirectoryNameCache.h"
#include "HandleTable.h"
#include "PathString.h"
#include <MinHook.h>
#include <cstring>

namespace ProcBridge::Hook
{
    namespace
    {
        using namespace Nt;

        // Largest name a file system returns (32767 characters) plus headroom.
        constexpr size_t MaxNameBytes = 0x10000;

        const IPathResolver* g_resolver = nullptr;
        HandleTable g_handles;
        DirectoryNameCache g_directoryNames;

        NtCreateFileFn g_ntCreateFile = nullptr;
        NtOpenFileFn g_ntOpenFile = nullptr;
        NtQueryAttributesFileFn g_ntQueryAttributesFile = nullptr;
        NtQueryAttributesFileFn g_ntQueryFullAttributesFile = nullptr;
        NtQueryInformationFileFn g_ntQueryInformationFile = nullptr;
        NtQueryInformationFileFn g_ntSetInformationFile = nullptr;
        NtQueryObjectFn g_ntQueryObject = nullptr;
        NtCloseFn g_ntClose = nullptr;

        thread_local unsigned t_hookDepth = 0;

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

        /// Per-thread re-entry guard (plan 4.6): a hook that ends up calling a hooked function again
        /// on the same thread goes straight to the original.
        class ReentryGuard final
        {
        public:
            ReentryGuard() noexcept : _outermost(t_hookDepth == 0) { ++t_hookDepth; }
            ~ReentryGuard() { --t_hookDepth; }
            ReentryGuard(const ReentryGuard&) = delete;
            ReentryGuard& operator=(const ReentryGuard&) = delete;

            bool IsOutermost() const noexcept { return _outermost; }

        private:
            bool _outermost;
        };

        /// Resolves OBJECT_ATTRIBUTES against the virtual roots and owns the rewritten name.
        /// The caller's structures are read once into a snapshot (SEH-guarded); an unreadable pointer
        /// makes Error() STATUS_ACCESS_VIOLATION, as the kernel would answer.
        class RedirectedAttributes final
        {
        public:
            explicit RedirectedAttributes(POBJECT_ATTRIBUTES source) noexcept
                : _source(source)
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
                }
                else
                {
                    const int mapping = g_resolver->FindMapping(_nameCopy);
                    if (mapping < 0)
                    {
                        return;
                    }
                    _mapping = mapping;
                    SetTranslated(g_resolver->TranslateToReal(mapping, _nameCopy, _name));
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
            }

            RedirectedAttributes(const RedirectedAttributes&) = delete;
            RedirectedAttributes& operator=(const RedirectedAttributes&) = delete;

            bool Matched() const noexcept { return _mapping >= 0; }
            /// STATUS_SUCCESS, or the status the hook returns without calling the system.
            NTSTATUS Error() const noexcept { return _error; }
            int Mapping() const noexcept { return _mapping; }
            POBJECT_ATTRIBUTES Get() noexcept { return _rewritten ? &_attributes : _source; }

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

            void SetTranslated(bool translated) noexcept
            {
                _rewritten = translated;
                _error = translated ? STATUS_SUCCESS : STATUS_NO_MEMORY;
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
                if (mapping >= 0)
                {
                    _mapping = mapping;
                    SetTranslated(g_resolver->TranslateDeviceToReal(mapping, joined, _name));
                }
                FreeString(joined);
            }

            POBJECT_ATTRIBUTES _source;
            OBJECT_ATTRIBUTES _attributes{};
            UNICODE_STRING _name{};
            UNICODE_STRING _nameCopy{};
            wchar_t _inlineName[260];
            wchar_t* _heapName = nullptr;
            int _mapping = -1;
            bool _rewritten = false;
            NTSTATUS _error = STATUS_SUCCESS;
        };

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
            RedirectedAttributes redirect(objectAttributes);
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
            RedirectedAttributes redirect(objectAttributes);
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

        NTSTATUS QueryAttributes(NtQueryAttributesFileFn original, POBJECT_ATTRIBUTES objectAttributes, PVOID information)
        {
            ReentryGuard guard;
            if (!guard.IsOutermost())
            {
                return original(objectAttributes, information);
            }
            RedirectedAttributes redirect(objectAttributes);
            if (redirect.Error() != STATUS_SUCCESS)
            {
                return redirect.Error();
            }
            return original(redirect.Get(), information);
        }

        NTSTATUS NTAPI HookNtQueryAttributesFile(POBJECT_ATTRIBUTES objectAttributes, PVOID information)
        {
            return QueryAttributes(g_ntQueryAttributesFile, objectAttributes, information);
        }

        NTSTATUS NTAPI HookNtQueryFullAttributesFile(POBJECT_ATTRIBUTES objectAttributes, PVOID information)
        {
            return QueryAttributes(g_ntQueryFullAttributesFile, objectAttributes, information);
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

        NTSTATUS NTAPI HookNtQueryInformationFile(HANDLE fileHandle, PIO_STATUS_BLOCK ioStatusBlock, PVOID information,
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
            HeapBuffer real(bufferBytes);
            HeapBuffer rewritten(bufferBytes);
            if (!real || !rewritten || map.RealVolumeRelative.Length == 0)
            {
                return g_ntQueryInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }

            IO_STATUS_BLOCK realStatus{};
            const NTSTATUS status = g_ntQueryInformationFile(fileHandle, &realStatus, real.Get(),
                static_cast<ULONG>(bufferBytes), informationClass);
            const auto* realName = reinterpret_cast<const FileNameInfo*>(real.Get() + nameOffset);
            auto* newName = reinterpret_cast<FileNameInfo*>(rewritten.Get() + nameOffset);
            size_t newBytes = 0;
            if (!NT_SUCCESS(status)
                || !ReplacePathPrefix(realName->FileName, realName->FileNameLength, map.RealVolumeRelative,
                    map.VirtualVolumeRelative, newName->FileName, MaxNameBytes, newBytes))
            {
                // Name outside the backing root (or query failed): answer exactly as the system does.
                return g_ntQueryInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            memcpy(rewritten.Get(), real.Get(), nameOffset);
            newName->FileNameLength = static_cast<ULONG>(newBytes);

            const size_t required = headerBytes + newBytes;
            const size_t copied = required <= length ? required : length;
            const NTSTATUS result = required <= length ? STATUS_SUCCESS : STATUS_BUFFER_OVERFLOW;
            if (!SafeCopy(information, rewritten.Get(), copied) || !SafeSetIoStatus(ioStatusBlock, result, copied))
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
            if (!g_resolver->TranslateToReal(mapping, target, realTarget))
            {
                return STATUS_NO_MEMORY;
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
            HeapBuffer real(bufferBytes);
            HeapBuffer rewritten(MaxNameBytes);
            if (!real || !rewritten || map.RealDevicePath.Length == 0)
            {
                return g_ntQueryObject(handle, informationClass, information, length, returnLength);
            }
            const NTSTATUS status = g_ntQueryObject(handle, informationClass, real.Get(), static_cast<ULONG>(bufferBytes), nullptr);
            const auto* realName = reinterpret_cast<const ObjectNameInfo*>(real.Get());
            auto* newText = reinterpret_cast<wchar_t*>(rewritten.Get());
            size_t newBytes = 0;
            if (!NT_SUCCESS(status)
                || !ReplacePathPrefix(realName->Name.Buffer, realName->Name.Length, map.RealDevicePath, map.VirtualDevicePath,
                    newText, MaxNameBytes - sizeof(wchar_t), newBytes))
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
            // Build the answer in the scratch buffer (header + text), then copy it out in one guarded step.
            BYTE* answer = real.Get();
            auto* result = reinterpret_cast<ObjectNameInfo*>(answer);
            auto* text = reinterpret_cast<wchar_t*>(answer + sizeof(ObjectNameInfo));
            memcpy(text, newText, newBytes);
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
            ULONG64 generation = 0;
            const bool tracked = g_handles.Find(handle, &generation) >= 0;
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
            }
            return status;
        }

        struct HookEntry
        {
            const char* Name;
            void* Detour;
            void** Original;
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
        };
        for (const HookEntry& hook : hooks)
        {
            void* target = reinterpret_cast<void*>(GetProcAddress(ntdll, hook.Name));
            if (target == nullptr)
            {
                return ERROR_PROC_NOT_FOUND;
            }
            if (MH_CreateHook(target, hook.Detour, hook.Original) != MH_OK)
            {
                return ERROR_GEN_FAILURE;
            }
        }
        return MH_EnableHook(MH_ALL_HOOKS) == MH_OK ? ERROR_SUCCESS : ERROR_GEN_FAILURE;
    }
}
