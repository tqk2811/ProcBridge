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

        NtCreateFileFn g_ntCreateFile = nullptr;
        NtOpenFileFn g_ntOpenFile = nullptr;
        NtQueryAttributesFileFn g_ntQueryAttributesFile = nullptr;
        NtQueryAttributesFileFn g_ntQueryFullAttributesFile = nullptr;
        NtQueryInformationFileFn g_ntQueryInformationFile = nullptr;
        NtQueryInformationFileFn g_ntSetInformationFile = nullptr;
        NtQueryObjectFn g_ntQueryObject = nullptr;
        NtCloseFn g_ntClose = nullptr;

        thread_local unsigned t_hookDepth = 0;

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
        class RedirectedAttributes final
        {
        public:
            explicit RedirectedAttributes(POBJECT_ATTRIBUTES source) noexcept
                : _source(source)
            {
                if (source == nullptr || source->ObjectName == nullptr || source->ObjectName->Buffer == nullptr)
                {
                    return;
                }
                if (source->RootDirectory != nullptr)
                {
                    // Relative to a virtual directory handle: that handle already points at the backing
                    // folder, so the name stays as is; only the result has to be tracked.
                    _mapping = g_handles.Find(source->RootDirectory);
                    return;
                }
                const int mapping = g_resolver->FindMapping(*source->ObjectName);
                if (mapping < 0)
                {
                    return;
                }
                _mapping = mapping;
                if (!g_resolver->TranslateToReal(mapping, *source->ObjectName, _name))
                {
                    _failed = true;
                    return;
                }
                _attributes = *source;
                _attributes.ObjectName = &_name;
                _rewritten = true;
            }

            ~RedirectedAttributes()
            {
                if (_rewritten)
                {
                    FreeString(_name);
                }
            }

            RedirectedAttributes(const RedirectedAttributes&) = delete;
            RedirectedAttributes& operator=(const RedirectedAttributes&) = delete;

            bool Matched() const noexcept { return _mapping >= 0; }
            bool Failed() const noexcept { return _failed; }
            int Mapping() const noexcept { return _mapping; }
            POBJECT_ATTRIBUTES Get() noexcept { return _rewritten ? &_attributes : _source; }

        private:
            POBJECT_ATTRIBUTES _source;
            OBJECT_ATTRIBUTES _attributes{};
            UNICODE_STRING _name{};
            int _mapping = -1;
            bool _rewritten = false;
            bool _failed = false;
        };

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
            if (redirect.Failed())
            {
                return STATUS_NO_MEMORY;
            }
            const NTSTATUS status = g_ntCreateFile(fileHandle, desiredAccess, redirect.Get(), ioStatusBlock,
                allocationSize, fileAttributes, shareAccess, createDisposition, createOptions, eaBuffer, eaLength);
            if (redirect.Matched() && NT_SUCCESS(status))
            {
                g_handles.Add(*fileHandle, redirect.Mapping());
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
            if (redirect.Failed())
            {
                return STATUS_NO_MEMORY;
            }
            const NTSTATUS status = g_ntOpenFile(fileHandle, desiredAccess, redirect.Get(), ioStatusBlock, shareAccess, openOptions);
            if (redirect.Matched() && NT_SUCCESS(status))
            {
                g_handles.Add(*fileHandle, redirect.Mapping());
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
            if (redirect.Failed())
            {
                return STATUS_NO_MEMORY;
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
            memcpy(information, rewritten.Get(), copied);
            const NTSTATUS result = required <= length ? STATUS_SUCCESS : STATUS_BUFFER_OVERFLOW;
            ioStatusBlock->Status = result;
            ioStatusBlock->Information = copied;
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
            const auto* rename = static_cast<const FileRenameInfo*>(information);
            if (rename->RootDirectory != nullptr || rename->FileNameLength > 0xFFFC || headerBytes + rename->FileNameLength > length)
            {
                return g_ntSetInformationFile(fileHandle, ioStatusBlock, information, length, informationClass);
            }
            UNICODE_STRING target{};
            target.Buffer = const_cast<PWSTR>(rename->FileName);
            target.Length = static_cast<USHORT>(rename->FileNameLength);
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
            memcpy(buffer.Get(), information, headerBytes);
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
            if (returnLength != nullptr)
            {
                *returnLength = static_cast<ULONG>(required);
            }
            if (required > length)
            {
                return STATUS_BUFFER_OVERFLOW;
            }
            auto* result = static_cast<ObjectNameInfo*>(information);
            auto* text = reinterpret_cast<wchar_t*>(static_cast<BYTE*>(information) + sizeof(ObjectNameInfo));
            memcpy(text, newText, newBytes);
            text[newBytes / sizeof(wchar_t)] = L'\0';
            result->Name.Buffer = text;
            result->Name.Length = static_cast<USHORT>(newBytes);
            result->Name.MaximumLength = static_cast<USHORT>(newBytes + sizeof(wchar_t));
            return STATUS_SUCCESS;
        }

        NTSTATUS NTAPI HookNtClose(HANDLE handle)
        {
            // Remove before closing: once closed, another thread may receive the same handle value.
            g_handles.Remove(handle);
            return g_ntClose(handle);
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
