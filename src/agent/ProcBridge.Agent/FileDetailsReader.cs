using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcBridge.Agent;

/// <summary>
/// The metadata <see cref="FileSystemInfo"/> does not expose: file id, change time and allocation size, read
/// from the file system (plan 4.4: the hook reports machine B's values for them). Failures leave the caller with
/// the values it already has.
/// </summary>
internal static class FileDetailsReader
{
    private const uint FileReadAttributes = 0x80;
    private const uint FileListDirectory = 0x1;
    private const uint Synchronize = 0x00100000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileBasicInfoClass = 0;
    private const int FileStandardInfoClass = 1;
    private const int FileIdBothDirectoryInfoClass = 10;
    private const int FileIdBothDirectoryRestartInfoClass = 11;

    /// <param name="FileId">NTFS file reference (64-bit file index).</param>
    public readonly record struct FileDetails(ulong FileId, long ChangeTime, long AllocationSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    // BY_HANDLE_FILE_INFORMATION: FILETIME fields are two DWORDs, so 4-byte packing.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileBasicInfo information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileStandardInfo information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, byte[] information, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    private static SafeFileHandle Open(string path, uint access)
        => CreateFileW(path, access, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);

    /// <summary>Details of one file or directory; null when they cannot be read.</summary>
    public static FileDetails? Read(string path)
    {
        using SafeFileHandle handle = Open(path, FileReadAttributes);
        if (handle.IsInvalid)
            return null;
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo basic, Marshal.SizeOf<FileBasicInfo>())
            || !GetFileInformationByHandleEx(handle, FileStandardInfoClass, out FileStandardInfo standard, Marshal.SizeOf<FileStandardInfo>())
            || !GetFileInformationByHandle(handle, out ByHandleFileInformation byHandle))
            return null;
        return new FileDetails(((ulong)byHandle.FileIndexHigh << 32) | byHandle.FileIndexLow, basic.ChangeTime, standard.AllocationSize);
    }

    /// <summary>Details of every entry of a directory, by name (case-insensitive), in one scan of the directory.</summary>
    /// <remarks>A failed scan returns what it has; the caller keeps its own values for the missing entries.</remarks>
    public static Dictionary<string, FileDetails> ReadDirectory(string path)
    {
        var result = new Dictionary<string, FileDetails>(StringComparer.OrdinalIgnoreCase);
        // Listing needs FILE_LIST_DIRECTORY; SYNCHRONIZE for synchronous I/O on the handle.
        using SafeFileHandle handle = Open(path, FileListDirectory | FileReadAttributes | Synchronize);
        if (handle.IsInvalid)
            return result;
        byte[] buffer = new byte[64 * 1024];
        int informationClass = FileIdBothDirectoryRestartInfoClass;
        while (GetFileInformationByHandleEx(handle, informationClass, buffer, buffer.Length))
        {
            informationClass = FileIdBothDirectoryInfoClass;
            // FILE_ID_BOTH_DIR_INFO: NextEntryOffset @0, ChangeTime @32, AllocationSize @48, FileNameLength @60,
            // FileId @96, FileName @104.
            int offset = 0;
            while (true)
            {
                ReadOnlySpan<byte> entry = buffer.AsSpan(offset);
                int nameLength = BinaryPrimitives.ReadInt32LittleEndian(entry[60..]);
                string name = Encoding.Unicode.GetString(entry.Slice(104, nameLength));
                result[name] = new FileDetails(
                    BinaryPrimitives.ReadUInt64LittleEndian(entry[96..]),
                    BinaryPrimitives.ReadInt64LittleEndian(entry[32..]),
                    BinaryPrimitives.ReadInt64LittleEndian(entry[48..]));
                int next = BinaryPrimitives.ReadInt32LittleEndian(entry);
                if (next == 0)
                    break;
                offset += next;
            }
        }
        return result;
    }
}
