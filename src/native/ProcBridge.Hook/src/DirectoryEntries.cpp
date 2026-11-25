#include "DirectoryEntries.h"
#include <cstring>

namespace ProcBridge::Hook
{
    /// Where the fields of one FILE_*_INFORMATION directory class live. All classes but FileNamesInformation
    /// share the FILE_DIRECTORY_INFORMATION head (times at 8..39, sizes at 40/48, attributes at 56, name length
    /// at 60). Offsets follow ntifs.h; -1 = field absent.
    struct DirectoryEntryFormat::Layout
    {
        ULONG Class;
        ULONG NameOffset;   // FileName, i.e. size of the fixed part
        int EaSize;
        int ReparseTag;
        int FileId64;
        int FileId128;
        int ShortNameLength; // CCHAR, ShortName[12] follows at +2
    };

    namespace
    {
        constexpr ULONG FileNamesInformationClass = 12;

        template <typename T>
        void Store(BYTE* entry, int offset, const T& value) noexcept
        {
            if (offset >= 0)
            {
                memcpy(entry + offset, &value, sizeof(value));
            }
        }
    }

    DirectoryEntryFormat::DirectoryEntryFormat(ULONG informationClass) noexcept
        : _layout(nullptr)
    {
        static constexpr Layout Layouts[] = {
            { 1, 64, -1, -1, -1, -1, -1 },   // FileDirectoryInformation
            { 2, 68, 64, -1, -1, -1, -1 },   // FileFullDirectoryInformation
            { 3, 94, 64, -1, -1, -1, 68 },   // FileBothDirectoryInformation
            { 12, 12, -1, -1, -1, -1, -1 },  // FileNamesInformation (name length at 8)
            { 37, 104, 64, -1, 96, -1, 68 }, // FileIdBothDirectoryInformation
            { 38, 80, 64, -1, 72, -1, -1 },  // FileIdFullDirectoryInformation
            { 60, 88, 64, 68, -1, 72, -1 },  // FileIdExtdDirectoryInformation
            { 63, 114, 64, 68, -1, 72, 88 }, // FileIdExtdBothDirectoryInformation
            { 78, 80, 64, 68, 72, -1, -1 },  // FileId64ExtdDirectoryInformation
            { 79, 106, 64, 68, 72, -1, 80 }, // FileId64ExtdBothDirectoryInformation
            { 80, 96, 64, 68, 72, 80, -1 },  // FileIdAllExtdDirectoryInformation
            { 81, 122, 64, 68, 72, 80, 96 }, // FileIdAllExtdBothDirectoryInformation
        };
        for (const auto& layout : Layouts)
        {
            if (layout.Class == informationClass)
            {
                _layout = &layout;
                break;
            }
        }
    }

    ULONG DirectoryEntryFormat::NameOffset() const noexcept
    {
        return _layout->NameOffset;
    }

    ULONG DirectoryEntryFormat::NameLengthOffset() const noexcept
    {
        return _layout->Class == FileNamesInformationClass ? 8 : 60;
    }

    void DirectoryEntryFormat::Write(BYTE* entry, const HookIpc::FileInfo& info, const wchar_t* name, ULONG nameBytes) const noexcept
    {
        memset(entry, 0, _layout->NameOffset);
        if (_layout->Class != FileNamesInformationClass)
        {
            Store(entry, 8, info.CreationTime);
            Store(entry, 16, info.LastAccessTime);
            Store(entry, 24, info.LastWriteTime);
            Store(entry, 32, info.ChangeTime);
            Store(entry, 40, info.Size);
            Store(entry, 48, info.AllocationSize);
            Store(entry, 56, info.Attributes);
            Store(entry, _layout->FileId64, info.FileId);
            Store(entry, _layout->FileId128, info.FileId); // low 64 bits, high bits stay 0
        }
        Store(entry, static_cast<int>(NameLengthOffset()), nameBytes);
        memcpy(entry + _layout->NameOffset, name, nameBytes);
    }
}
