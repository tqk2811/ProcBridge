using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProcBridge.Agent.Explorer.Native;

internal static partial class NativeMethods
{
    public const uint FileReadAttributes = 0x0080;
    public const uint FileListDirectory = 0x0001;
    public const uint FileTraverse = 0x0020;
    public const uint GenericRead = 0x80000000;
    public const uint Delete = 0x00010000;
    public const uint Synchronize = 0x00100000;
    public const uint FileShareRead = 0x1;
    public const uint FileShareWrite = 0x2;
    public const uint FileShareDelete = 0x4;
    public const uint FileShareAll = FileShareRead | FileShareWrite | FileShareDelete;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
    public const uint FileFlagOpenReparsePoint = 0x00200000;
    public const uint FileAttributeDirectory = 0x10;
    public const uint FileAttributeReparsePoint = 0x400;

    // NtCreateFile dispositions and options.
    public const uint NtFileOpen = 1;
    public const uint NtFileCreate = 2;
    public const uint NtFileOpenIf = 3;
    public const uint NtFileDirectoryFile = 0x00000001;
    public const uint NtFileSynchronousIoNonAlert = 0x00000020;
    public const uint NtFileOpenReparsePoint = 0x00200000;
    public const uint ObjCaseInsensitive = 0x40;

    // FILE_INFO_BY_HANDLE_CLASS / FILE_INFORMATION_CLASS values.
    public const int FileBasicInfo = 0;
    public const int FileStandardInfo = 1;
    public const int FileDispositionInfo = 4;
    public const int FileAttributeTagInfo = 9;
    public const int FileFullDirectoryInfo = 14;
    public const int FileFullDirectoryRestartInfo = 15;
    public const int NtFileRenameInformation = 10;

    public const int ErrorNoMoreFiles = 18;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    public static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* buffer, uint bufferLength, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, void* buffer, uint bufferSize);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        IoStatusBlock* ioStatusBlock,
        long* allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        void* eaBuffer,
        uint eaLength);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtSetInformationFile(SafeFileHandle file, IoStatusBlock* ioStatusBlock, void* info, uint length, int infoClass);

    [LibraryImport("ntdll.dll")]
    public static partial uint RtlNtStatusToDosError(int status);

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public void* SecurityDescriptor;
        public void* SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    /// <summary>FILE_BASIC_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    /// <summary>FILE_STANDARD_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    /// <summary>FILE_ATTRIBUTE_TAG_INFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FileAttributeTagInformation
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, char* buffer, ref uint size);

    [LibraryImport("ntdll.dll")]
    public static unsafe partial int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, void* info, uint infoLength, out uint returnLength);

    /// <summary>PROCESS_BASIC_INFORMATION (ProcessInformationClass 0).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessBasicInformation
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }
}
