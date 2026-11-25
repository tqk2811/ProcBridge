#pragma once
// Minimal ntdll declarations used by the hooks (types not exposed by winternl.h).
#define WIN32_NO_STATUS
#include <windows.h>
#undef WIN32_NO_STATUS
#include <ntstatus.h>
#include <winternl.h>

namespace ProcBridge::Hook::Nt
{
    // FILE_INFORMATION_CLASS values (wdm.h / ntifs.h).
    constexpr ULONG FileNameInformation = 9;
    constexpr ULONG FileRenameInformation = 10;
    constexpr ULONG FileLinkInformation = 11;
    constexpr ULONG FileAllInformation = 18;
    constexpr ULONG FileNormalizedNameInformation = 48;
    constexpr ULONG FileRenameInformationEx = 65;
    constexpr ULONG FileLinkInformationEx = 72;
    constexpr ULONG FileBasicInformation = 4;
    constexpr ULONG FileDispositionInformation = 13;
    constexpr ULONG FileDispositionInformationEx = 64;

    // FILE_DISPOSITION_INFORMATION_EX flags.
    constexpr ULONG FileDispositionDelete = 0x1;
    constexpr ULONG FileDispositionOnClose = 0x8;

    // NtCreateFile create option.
    constexpr ULONG FileDeleteOnCloseOption = 0x00001000;

    // OBJECT_INFORMATION_CLASS.
    constexpr ULONG ObjectNameInformation = 1;

    struct FileNameInfo
    {
        ULONG FileNameLength;
        WCHAR FileName[1];
    };

    // FILE_ALL_INFORMATION up to (not including) NameInformation.
    struct FileAllInfoHeader
    {
        LARGE_INTEGER CreationTime;
        LARGE_INTEGER LastAccessTime;
        LARGE_INTEGER LastWriteTime;
        LARGE_INTEGER ChangeTime;
        ULONG FileAttributes;
        LARGE_INTEGER AllocationSize;
        LARGE_INTEGER EndOfFile;
        ULONG NumberOfLinks;
        BOOLEAN DeletePending;
        BOOLEAN Directory;
        LARGE_INTEGER IndexNumber;
        ULONG EaSize;
        ACCESS_MASK AccessFlags;
        LARGE_INTEGER CurrentByteOffset;
        ULONG Mode;
        ULONG AlignmentRequirement;
    };
    static_assert(sizeof(FileAllInfoHeader) == 96, "FILE_ALL_INFORMATION.NameInformation must start at 96");

    // FILE_RENAME_INFORMATION / FILE_LINK_INFORMATION (and their Ex forms).
    struct FileRenameInfo
    {
        union
        {
            BOOLEAN ReplaceIfExists;
            ULONG Flags;
        };
        HANDLE RootDirectory;
        ULONG FileNameLength;
        WCHAR FileName[1];
    };

    struct ObjectNameInfo
    {
        UNICODE_STRING Name;
    };

    using NtCreateFileFn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK,
        PLARGE_INTEGER, ULONG, ULONG, ULONG, ULONG, PVOID, ULONG);
    using NtOpenFileFn = NTSTATUS(NTAPI*)(PHANDLE, ACCESS_MASK, POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, ULONG, ULONG);
    using NtQueryAttributesFileFn = NTSTATUS(NTAPI*)(POBJECT_ATTRIBUTES, PVOID);
    using NtQueryInformationFileFn = NTSTATUS(NTAPI*)(HANDLE, PIO_STATUS_BLOCK, PVOID, ULONG, ULONG);
    using NtQueryObjectFn = NTSTATUS(NTAPI*)(HANDLE, ULONG, PVOID, ULONG, PULONG);
    using NtCloseFn = NTSTATUS(NTAPI*)(HANDLE);
    using NtQueryInformationByNameFn = NTSTATUS(NTAPI*)(POBJECT_ATTRIBUTES, PIO_STATUS_BLOCK, PVOID, ULONG, ULONG);
    using NtDeleteFileFn = NTSTATUS(NTAPI*)(POBJECT_ATTRIBUTES);
    using NtDuplicateObjectFn = NTSTATUS(NTAPI*)(HANDLE, HANDLE, HANDLE, PHANDLE, ACCESS_MASK, ULONG, ULONG);
    using NtFlushBuffersFileFn = NTSTATUS(NTAPI*)(HANDLE, PIO_STATUS_BLOCK);
    using NtWriteFileFn = NTSTATUS(NTAPI*)(HANDLE, HANDLE, PIO_APC_ROUTINE, PVOID, PIO_STATUS_BLOCK, PVOID, ULONG, PLARGE_INTEGER, PULONG);
}

extern "C"
{
    NTSYSAPI WCHAR NTAPI RtlUpcaseUnicodeChar(WCHAR sourceCharacter);
    NTSYSAPI BOOLEAN NTAPI RtlDosPathNameToNtPathName_U(PCWSTR dosFileName, PUNICODE_STRING ntFileName,
        PWSTR* filePart, PVOID relativeName);
}
