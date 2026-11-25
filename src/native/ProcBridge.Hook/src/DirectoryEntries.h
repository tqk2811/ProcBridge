#pragma once
#include "NtApi.h"
#include <ProcBridge/HookIpc.h>

namespace ProcBridge::Hook
{
    /// Layout of the FILE_*_INFORMATION directory classes the hook builds itself (listings of machine B, the entry of a
    /// virtual root in the listing of its parent).
    class DirectoryEntryFormat final
    {
    public:
        /// The format of informationClass; IsSupported() is false for a class the hook does not build.
        explicit DirectoryEntryFormat(ULONG informationClass) noexcept;

        bool IsSupported() const noexcept { return _layout != nullptr; }

        /// Size of the fixed part (offset of FileName).
        ULONG NameOffset() const noexcept;

        /// Offset of FileNameLength (8 for FileNamesInformation, 60 for the others).
        ULONG NameLengthOffset() const noexcept;

        /// Writes one entry (NextEntryOffset 0): fixed part from info, then the name. The caller checks that
        /// NameOffset() + nameBytes bytes fit at entry.
        void Write(BYTE* entry, const HookIpc::FileInfo& info, const wchar_t* name, ULONG nameBytes) const noexcept;

    private:
        struct Layout;
        const Layout* _layout;
    };

    /// Rounds up to the 8-byte alignment of directory entries.
    constexpr ULONG AlignDirectoryEntry(ULONG value) noexcept
    {
        return (value + 7) & ~7UL;
    }
}
