#include "HandleInbox.h"
#include <sddl.h>
#include <cstddef>
#include <cstdio>
#include <cstring>

#pragma comment(lib, "advapi32.lib")

namespace ProcBridge::Hook
{
    namespace
    {
        constexpr LONG SlotFree = 0;
        constexpr LONG SlotWriting = 1;
        constexpr LONG SlotReady = 2;
        constexpr LONG SlotTaking = 3; // the receiver is copying or freeing it

        constexpr uint32_t InboxMagic = 0x58424950; // "PIBX"
        constexpr uint32_t InboxVersion = 2;
        // A sender writes a slot in microseconds; one still writing after this long belongs to a dead sender.
        constexpr ULONGLONG StuckSlotMs = 5000;

        void InboxName(DWORD processId, wchar_t (&name)[64]) noexcept
        {
            swprintf_s(name, L"Local\\ProcBridge-HandleInbox-%lu", processId);
        }

        uint64_t CreationTime(HANDLE process) noexcept
        {
            FILETIME created{};
            FILETIME exited{};
            FILETIME kernel{};
            FILETIME user{};
            if (!GetProcessTimes(process, &created, &exited, &kernel, &user))
            {
                return 0;
            }
            return (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime;
        }

        /// "D:P(A;;GA;;;<user>)(A;;GA;;;SY)S:(ML;;NW;;;<integrity level>)" for the token of this process.
        bool BuildSecurityDescriptor(PSECURITY_DESCRIPTOR& descriptor) noexcept
        {
            descriptor = nullptr;
            HANDLE token = nullptr;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token))
            {
                return false;
            }
            BYTE userBuffer[256];
            BYTE labelBuffer[256];
            DWORD bytes = 0;
            const bool queried = GetTokenInformation(token, TokenUser, userBuffer, sizeof(userBuffer), &bytes)
                && GetTokenInformation(token, TokenIntegrityLevel, labelBuffer, sizeof(labelBuffer), &bytes);
            CloseHandle(token);
            if (!queried)
            {
                return false;
            }
            wchar_t* user = nullptr;
            wchar_t* label = nullptr;
            bool built = false;
            if (ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(userBuffer)->User.Sid, &user)
                && ConvertSidToStringSidW(reinterpret_cast<TOKEN_MANDATORY_LABEL*>(labelBuffer)->Label.Sid, &label))
            {
                wchar_t sddl[512];
                if (swprintf_s(sddl, L"D:P(A;;GA;;;%s)(A;;GA;;;SY)S:(ML;;NW;;;%s)", user, label) > 0)
                {
                    built = ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, SDDL_REVISION_1, &descriptor, nullptr) != FALSE;
                }
            }
            LocalFree(user);
            LocalFree(label);
            return built;
        }
    }

    struct HandleInbox::Slot
    {
        volatile LONG State;
        uint32_t Handle;
        uint32_t SenderPid;
        int32_t Mapping;
        uint32_t RootHash;
        uint32_t RangeId;
        volatile ULONGLONG Claimed; // GetTickCount64 of the sender when it claimed the slot, 0 when free
    };

    struct HandleInbox::Header
    {
        uint32_t Magic;
        uint32_t Version;
        uint64_t OwnerCreationTime;
        volatile LONG Sequence; // bumped after each slot becomes ready
        uint32_t Reserved;
        Slot Slots[HandleInbox::SlotCount];
    };

    DWORD HandleInbox::Open() noexcept
    {
        // Layout shared by x64 and x86 processes of the tree: it must not depend on the architecture.
        static_assert(sizeof(Slot) == 32);
        static_assert(offsetof(Slot, Claimed) == 24);
        static_assert(offsetof(Header, Slots) == 24);
        static_assert(sizeof(Header) == 24 + 32 * SlotCount);
        PSECURITY_DESCRIPTOR descriptor = nullptr;
        if (!BuildSecurityDescriptor(descriptor))
        {
            return GetLastError() != ERROR_SUCCESS ? GetLastError() : ERROR_INVALID_SECURITY_DESCR;
        }
        SECURITY_ATTRIBUTES attributes{ sizeof(attributes), descriptor, FALSE };
        wchar_t name[64];
        InboxName(GetCurrentProcessId(), name);
        const HANDLE section = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, sizeof(Header), name);
        const DWORD created = GetLastError();
        LocalFree(descriptor);
        if (section == nullptr)
        {
            return created;
        }
        if (created == ERROR_ALREADY_EXISTS)
        {
            // Someone holds the name already: its content and security are not ours. The feature stays off.
            CloseHandle(section);
            return ERROR_ALREADY_EXISTS;
        }
        auto* view = static_cast<Header*>(MapViewOfFile(section, FILE_MAP_WRITE, 0, 0, sizeof(Header)));
        if (view == nullptr)
        {
            const DWORD error = GetLastError();
            CloseHandle(section);
            return error;
        }
        // The section handle and view stay open for the life of the process (never unmapped, see the class comment).
        view->Magic = InboxMagic;
        view->Version = InboxVersion;
        view->OwnerCreationTime = CreationTime(GetCurrentProcess());
        MemoryBarrier();
        _view = view;
        return ERROR_SUCCESS;
    }

    LONG HandleInbox::ReadSequence(const Header* view) noexcept
    {
        return ReadAcquire(&view->Sequence);
    }

    bool HandleInbox::Take(Header* view, ULONG index, DuplicatedHandle& entry) noexcept
    {
        Slot& slot = view->Slots[index];
        const LONG state = ReadAcquire(&slot.State);
        if (state == SlotWriting)
        {
            const ULONGLONG claimed = slot.Claimed;
            if (claimed != 0 && GetTickCount64() - claimed > StuckSlotMs
                && InterlockedCompareExchange(&slot.State, SlotTaking, SlotWriting) == SlotWriting)
            {
                slot.Claimed = 0;
                InterlockedExchange(&slot.State, SlotFree);
            }
            return false;
        }
        if (state != SlotReady || InterlockedCompareExchange(&slot.State, SlotTaking, SlotReady) != SlotReady)
        {
            return false;
        }
        entry.Handle = slot.Handle;
        entry.SenderPid = slot.SenderPid;
        entry.Mapping = slot.Mapping;
        entry.RootHash = slot.RootHash;
        entry.RangeId = slot.RangeId;
        slot.Claimed = 0;
        InterlockedExchange(&slot.State, SlotFree);
        return true;
    }

    bool HandleInbox::Send(HANDLE targetProcess, const DuplicatedHandle& entry) noexcept
    {
        const DWORD processId = GetProcessId(targetProcess);
        // The target handle pins the process, so its creation time names exactly this process.
        const uint64_t created = CreationTime(targetProcess);
        if (processId == 0 || created == 0)
        {
            return false;
        }
        wchar_t name[64];
        InboxName(processId, name);
        const HANDLE section = OpenFileMappingW(FILE_MAP_WRITE | FILE_MAP_READ, FALSE, name);
        if (section == nullptr)
        {
            return false;
        }
        auto* view = static_cast<Header*>(MapViewOfFile(section, FILE_MAP_WRITE | FILE_MAP_READ, 0, 0, sizeof(Header)));
        bool sent = false;
        if (view != nullptr && view->Magic == InboxMagic && view->Version == InboxVersion && view->OwnerCreationTime == created)
        {
            for (ULONG i = 0; i < SlotCount && !sent; ++i)
            {
                Slot& slot = view->Slots[i];
                if (InterlockedCompareExchange(&slot.State, SlotWriting, SlotFree) != SlotFree)
                {
                    continue;
                }
                slot.Claimed = GetTickCount64() | 1;
                slot.Handle = entry.Handle;
                slot.SenderPid = GetCurrentProcessId();
                slot.Mapping = entry.Mapping;
                slot.RootHash = entry.RootHash;
                slot.RangeId = entry.RangeId;
                InterlockedExchange(&slot.State, SlotReady);
                InterlockedIncrement(&view->Sequence);
                sent = true;
            }
        }
        if (view != nullptr)
        {
            UnmapViewOfFile(view);
        }
        CloseHandle(section);
        return sent;
    }

    uint32_t HandleInbox::RootHash(const UNICODE_STRING& root) noexcept
    {
        uint32_t hash = 2166136261u;
        for (USHORT i = 0; i < root.Length / sizeof(wchar_t); ++i)
        {
            const wchar_t c = RtlUpcaseUnicodeChar(root.Buffer[i]);
            hash = (hash ^ static_cast<uint32_t>(c)) * 16777619u;
        }
        return hash;
    }
}
