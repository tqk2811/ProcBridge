using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProcBridge.Agent.Explorer.Native;

internal static partial class NativeMethods
{
    public const uint FileReadAttributes = 0x0080;
    public const uint FileShareAll = 0x1 | 0x2 | 0x4;
    public const uint OpenExisting = 3;
    public const uint FileFlagBackupSemantics = 0x02000000;
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
