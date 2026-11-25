using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Native;

/// <summary>One entry of a directory read through its handle.</summary>
internal readonly record struct NativeDirectoryEntry(string Name, uint Attributes, long Size, long CreationTime, long LastWriteTime)
{
    public bool IsDirectory => (Attributes & NativeMethods.FileAttributeDirectory) != 0;

    public bool IsReparsePoint => (Attributes & NativeMethods.FileAttributeReparsePoint) != 0;
}

/// <summary>
/// File operations done through handles, so the object that passed the scope check is the very
/// object that is read, listed, deleted or renamed (no lookup by name between check and use).
/// </summary>
internal static class NativeFile
{
    /// <summary>Opens an existing file or directory by full path. <paramref name="followLinks"/> false opens a link itself.</summary>
    public static SafeFileHandle Open(string path, uint access, bool followLinks, uint share)
        => TryOpen(path, access, followLinks, share, out var error) ?? throw Error(error, path);

    public static SafeFileHandle? TryOpen(string path, uint access, bool followLinks, uint share, out int error)
    {
        var flags = NativeMethods.FileFlagBackupSemantics | (followLinks ? 0 : NativeMethods.FileFlagOpenReparsePoint);
        var handle = NativeMethods.CreateFile(path, access, share, 0, NativeMethods.OpenExisting, flags, 0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return null;
        }

        error = 0;
        return handle;
    }

    /// <summary>
    /// Opens <paramref name="name"/> (one path component, or empty for the directory itself) relative to
    /// <paramref name="directory"/>. A link is never followed: the link object itself is opened.
    /// </summary>
    public static unsafe SafeFileHandle OpenRelative(SafeFileHandle directory, string name, uint access, uint disposition, uint options, uint share)
    {
        if (name.Contains('\\') || name.Contains('/'))
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Name must be a single path component.");

        bool added = false;
        directory.DangerousAddRef(ref added);
        try
        {
            fixed (char* buffer = name)
            {
                var unicode = new NativeMethods.UnicodeString
                {
                    Length = (ushort)(name.Length * 2),
                    MaximumLength = (ushort)(name.Length * 2),
                    Buffer = buffer,
                };
                var attributes = new NativeMethods.ObjectAttributes
                {
                    Length = (uint)sizeof(NativeMethods.ObjectAttributes),
                    RootDirectory = directory.DangerousGetHandle(),
                    ObjectName = &unicode,
                    Attributes = NativeMethods.ObjCaseInsensitive,
                };
                NativeMethods.IoStatusBlock io;
                var status = NativeMethods.NtCreateFile(out var handle, access | NativeMethods.Synchronize, &attributes, &io, null, 0, share,
                    disposition, options | NativeMethods.NtFileSynchronousIoNonAlert | NativeMethods.NtFileOpenReparsePoint, null, 0);
                if (status < 0)
                {
                    handle.Dispose();
                    throw Error((int)NativeMethods.RtlNtStatusToDosError(status), name);
                }

                return handle;
            }
        }
        finally
        {
            if (added)
                directory.DangerousRelease();
        }
    }

    public static unsafe NativeMethods.FileAttributeTagInformation GetAttributeTag(SafeFileHandle handle)
    {
        NativeMethods.FileAttributeTagInformation info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileAttributeTagInfo, &info, (uint)sizeof(NativeMethods.FileAttributeTagInformation)))
            throw Error(Marshal.GetLastPInvokeError(), null);
        return info;
    }

    public static unsafe NativeMethods.FileBasicInformation GetBasic(SafeFileHandle handle)
    {
        NativeMethods.FileBasicInformation info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileBasicInfo, &info, (uint)sizeof(NativeMethods.FileBasicInformation)))
            throw Error(Marshal.GetLastPInvokeError(), null);
        return info;
    }

    public static unsafe NativeMethods.FileStandardInformation GetStandard(SafeFileHandle handle)
    {
        NativeMethods.FileStandardInformation info;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, NativeMethods.FileStandardInfo, &info, (uint)sizeof(NativeMethods.FileStandardInformation)))
            throw Error(Marshal.GetLastPInvokeError(), null);
        return info;
    }

    public static bool IsDirectory(SafeFileHandle handle)
        => (GetAttributeTag(handle).FileAttributes & NativeMethods.FileAttributeDirectory) != 0;

    /// <summary>Lists a directory through a handle opened with <see cref="NativeMethods.FileListDirectory"/>.</summary>
    public static unsafe List<NativeDirectoryEntry> Enumerate(SafeFileHandle directory)
    {
        var entries = new List<NativeDirectoryEntry>();
        var buffer = new byte[64 * 1024];
        var infoClass = NativeMethods.FileFullDirectoryRestartInfo;
        fixed (byte* start = buffer)
        {
            while (true)
            {
                if (!NativeMethods.GetFileInformationByHandleEx(directory, infoClass, start, (uint)buffer.Length))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == NativeMethods.ErrorNoMoreFiles)
                        break;
                    throw Error(error, null);
                }

                infoClass = NativeMethods.FileFullDirectoryInfo;
                var entry = start;
                while (true)
                {
                    // FILE_FULL_DIR_INFO
                    var next = *(uint*)entry;
                    var nameLength = *(uint*)(entry + 60);
                    var name = new string((char*)(entry + 68), 0, (int)(nameLength / 2));
                    if (name is not "." and not "..")
                        entries.Add(new NativeDirectoryEntry(name, *(uint*)(entry + 56), *(long*)(entry + 40), *(long*)(entry + 8), *(long*)(entry + 24)));
                    if (next == 0)
                        break;
                    entry += next;
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// Marks the object of the handle (opened with DELETE) for deletion. POSIX semantics unlink the name at
    /// once even while other processes keep the file open (share-delete), so a recursive delete is not blocked
    /// by them; a read-only file is deleted too. Older systems fall back to the plain disposition.
    /// </summary>
    public static unsafe void MarkDeleted(SafeFileHandle handle)
    {
        uint flags = NativeMethods.FileDispositionDelete | NativeMethods.FileDispositionPosixSemantics | NativeMethods.FileDispositionIgnoreReadonlyAttribute;
        if (NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileDispositionInfoEx, &flags, sizeof(uint)))
            return;
        var error = Marshal.GetLastPInvokeError();
        if (error != NativeMethods.ErrorInvalidParameter)
            throw Error(error, null);

        byte delete = 1;
        if (!NativeMethods.SetFileInformationByHandle(handle, NativeMethods.FileDispositionInfo, &delete, 1))
            throw Error(Marshal.GetLastPInvokeError(), null);
    }

    /// <summary>Deletes everything inside a directory handle. Links inside are removed, never followed.</summary>
    public static void DeleteChildren(SafeFileHandle directory)
    {
        List<NativeDirectoryEntry> children;
        using (var list = OpenRelative(directory, string.Empty, NativeMethods.FileListDirectory, NativeMethods.NtFileOpen,
                   NativeMethods.NtFileDirectoryFile, NativeMethods.FileShareAll))
            children = Enumerate(list);

        foreach (var child in children)
        {
            using var handle = OpenRelative(directory, child.Name, NativeMethods.Delete | NativeMethods.FileReadAttributes,
                NativeMethods.NtFileOpen, 0, NativeMethods.FileShareAll);
            var tag = GetAttributeTag(handle);
            if ((tag.FileAttributes & NativeMethods.FileAttributeDirectory) != 0 && (tag.FileAttributes & NativeMethods.FileAttributeReparsePoint) == 0)
                DeleteChildren(handle);
            MarkDeleted(handle);
        }
    }

    /// <summary>Moves the object of <paramref name="source"/> (opened with DELETE) to <paramref name="name"/> inside <paramref name="targetDirectory"/>.</summary>
    public static unsafe void Rename(SafeFileHandle source, SafeFileHandle targetDirectory, string name)
    {
        if (name.Length == 0 || name.Contains('\\') || name.Contains('/'))
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Name must be a single path component.");

        // FILE_RENAME_INFORMATION: { BOOLEAN/ULONG ReplaceIfExists; HANDLE RootDirectory; ULONG FileNameLength; WCHAR FileName[]; }
        var rootOffset = nint.Size;
        var lengthOffset = 2 * nint.Size;
        var nameOffset = lengthOffset + 4;
        var size = Math.Max(nameOffset + name.Length * 2, 3 * nint.Size + 8);
        var buffer = new byte[size];
        bool added = false;
        targetDirectory.DangerousAddRef(ref added);
        try
        {
            fixed (byte* p = buffer)
            fixed (char* n = name)
            {
                *(nint*)(p + rootOffset) = targetDirectory.DangerousGetHandle();
                *(uint*)(p + lengthOffset) = (uint)(name.Length * 2);
                Buffer.MemoryCopy(n, p + nameOffset, size - nameOffset, name.Length * 2);
                NativeMethods.IoStatusBlock io;
                var status = NativeMethods.NtSetInformationFile(source, &io, p, (uint)size, NativeMethods.NtFileRenameInformation);
                if (status < 0)
                    throw Error((int)NativeMethods.RtlNtStatusToDosError(status), name);
            }
        }
        finally
        {
            if (added)
                targetDirectory.DangerousRelease();
        }
    }

    /// <summary>Final path of an open handle with every link resolved, without the <c>\\?\</c> prefix; null when it cannot be read.</summary>
    public static unsafe string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* p = buffer)
                length = NativeMethods.GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, 0);
            if (length == 0)
                return null;
            if (length < buffer.Length)
            {
                var result = new string(buffer, 0, (int)length);
                if (result.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                    return @"\\" + result[8..];
                if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                    return result[4..];
                return result;
            }

            buffer = new char[length];
        }
    }

    /// <summary>Maps a Win32 error to the exception the providers turn into a status.</summary>
    public static Exception Error(int code, string? path)
    {
        var message = new Win32Exception(code).Message;
        return code switch
        {
            2 => new FileNotFoundException(message, path),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message),
            17 => new ExplorerDeniedException(ExplorerStatus.Failed, "Renaming across drives is not supported."),
            80 or 183 => new ExplorerDeniedException(ExplorerStatus.AlreadyExists, "Path already exists."),
            123 => new ArgumentException(message),
            // STATUS_NOT_A_DIRECTORY: a component on the way is a file.
            267 => new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "A component of the path is not a directory."),
            145 => new ExplorerDeniedException(ExplorerStatus.Failed, "The directory is not empty."),
            _ => new Win32Exception(code),
        };
    }
}
