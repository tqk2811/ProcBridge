#include "LoaderNames.h"
#include "NtApi.h"
#include "PathString.h"
#include <cstring>

namespace ProcBridge::Hook
{
    namespace
    {
        // Leading part of LDR_DATA_TABLE_ENTRY (stable since Windows XP; winternl.h hides BaseDllName).
        struct LoaderEntry
        {
            LIST_ENTRY InLoadOrderLinks;
            LIST_ENTRY InMemoryOrderLinks;
            LIST_ENTRY InInitializationOrderLinks;
            PVOID DllBase;
            PVOID EntryPoint;
            ULONG SizeOfImage;
            UNICODE_STRING FullDllName;
            UNICODE_STRING BaseDllName;
        };

        // Leading part of PEB_LDR_DATA.
        struct LoaderData
        {
            ULONG Length;
            BOOLEAN Initialized;
            PVOID SsHandle;
            LIST_ENTRY InLoadOrderModuleList;
        };

        using LdrLockLoaderLockFn = NTSTATUS(NTAPI*)(ULONG, PULONG, PVOID*);
        using LdrUnlockLoaderLockFn = NTSTATUS(NTAPI*)(ULONG, PVOID);

        UNICODE_STRING View(const std::wstring& text) noexcept
        {
            UNICODE_STRING value{};
            value.Buffer = const_cast<wchar_t*>(text.c_str());
            value.Length = static_cast<USHORT>(text.size() * sizeof(wchar_t));
            value.MaximumLength = value.Length;
            return value;
        }

        /// Renames name (and baseName, which points into it, when given) from the real into the virtual directory. The
        /// new buffer comes from the process heap, like the loader's own; the old one is left alone (the loader or
        /// the parameter block owns it).
        void Rename(UNICODE_STRING& name, UNICODE_STRING* baseName, const UNICODE_STRING& real, const UNICODE_STRING& virtualDirectory) noexcept
        {
            if (name.Buffer == nullptr || !HasPathPrefix(name.Buffer, name.Length, real) || name.Length == real.Length)
            {
                return;
            }
            const size_t tailBytes = name.Length - real.Length;
            const size_t bytes = virtualDirectory.Length + tailBytes;
            if (bytes > 0xFFFC)
            {
                return;
            }
            auto* buffer = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), 0, bytes + sizeof(wchar_t)));
            if (buffer == nullptr)
            {
                return;
            }
            memcpy(buffer, virtualDirectory.Buffer, virtualDirectory.Length);
            memcpy(reinterpret_cast<BYTE*>(buffer) + virtualDirectory.Length, reinterpret_cast<const BYTE*>(name.Buffer) + real.Length, tailBytes);
            buffer[bytes / sizeof(wchar_t)] = L'\0';
            if (baseName != nullptr && baseName->Buffer >= name.Buffer
                && baseName->Buffer + baseName->Length / sizeof(wchar_t) <= name.Buffer + name.Length / sizeof(wchar_t))
            {
                // The base name is the end of the full name.
                baseName->Buffer = buffer + (bytes - baseName->Length) / sizeof(wchar_t);
                baseName->MaximumLength = static_cast<USHORT>(baseName->Length + sizeof(wchar_t));
            }
            name.Buffer = buffer;
            name.Length = static_cast<USHORT>(bytes);
            name.MaximumLength = static_cast<USHORT>(bytes + sizeof(wchar_t));
        }
    }

    void RestoreVirtualImageNames(const std::wstring& realDirectory, const std::wstring& virtualDirectory) noexcept
    {
        if (realDirectory.empty() || virtualDirectory.empty())
        {
            return;
        }
        const UNICODE_STRING real = View(realDirectory);
        const UNICODE_STRING virtualPath = View(virtualDirectory);
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        const auto lockLoader = reinterpret_cast<LdrLockLoaderLockFn>(GetProcAddress(ntdll, "LdrLockLoaderLock"));
        const auto unlockLoader = reinterpret_cast<LdrUnlockLoaderLockFn>(GetProcAddress(ntdll, "LdrUnlockLoaderLock"));
        PVOID cookie = nullptr;
        if (lockLoader == nullptr || unlockLoader == nullptr || !NT_SUCCESS(lockLoader(0, nullptr, &cookie)))
        {
            return;
        }
        PEB* peb = NtCurrentTeb()->ProcessEnvironmentBlock;
        auto* data = reinterpret_cast<LoaderData*>(peb->Ldr);
        for (LIST_ENTRY* link = data->InLoadOrderModuleList.Flink; link != &data->InLoadOrderModuleList; link = link->Flink)
        {
            auto* entry = CONTAINING_RECORD(link, LoaderEntry, InLoadOrderLinks);
            Rename(entry->FullDllName, &entry->BaseDllName, real, virtualPath);
        }
        Rename(peb->ProcessParameters->ImagePathName, nullptr, real, virtualPath);
        unlockLoader(0, cookie);
    }
}
