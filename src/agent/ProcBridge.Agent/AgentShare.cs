using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Agent;

/// <summary>
/// One folder on machine B, read only unless created writable (M3, see AgentShare.Write.cs). Every request path is share relative and is checked so it can
/// never leave the folder: no "..", no drive or stream syntax, and links/junctions inside the share must point
/// back inside it (plan 4.3).
/// </summary>
public sealed partial class AgentShare
{
    private const int DefaultListPage = 1024;
    private const uint FileTypeDisk = 1;

    /// <summary>Serialized size of an entry besides its name: array and string headers, one u32 and eight 64-bit fields.</summary>
    private const int ListEntryOverhead = 112;

    /// <summary>Response array headers and the has-more flag, with slack.</summary>
    private const int ListResponseOverhead = 64;

    private static readonly char[] Separators = ['/', '\\'];
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Names Windows treats as devices wherever they appear in a path, with any extension. Superscript digits are
    /// included because the console and LPT/COM handling accepts them too.
    /// </summary>
    private static readonly HashSet<string> DosDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };
    internal static readonly EnumerationOptions ListOptions = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly string[] _allowedRoots;

    public AgentShare(string name, string rootPath, bool writable = false)
    {
        IsWritable = writable;
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(RootPath))
            throw new DirectoryNotFoundException($"Share root '{RootPath}' does not exist.");

        // A link inside the share may point at the root through its real path when the root itself is a link.
        string? realRoot = new DirectoryInfo(RootPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        _allowedRoots = realRoot is null
            ? [RootPath]
            : [RootPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(realRoot))];
    }

    public string Name { get; }

    /// <summary>True when machine A may create, write, rename and delete inside the share; off by default.</summary>
    public bool IsWritable { get; }

    /// <summary>Full local path of the share root, without trailing separator.</summary>
    public string RootPath { get; }

    /// <summary>Maps a share-relative path to a local path inside the share, or throws.</summary>
    public string ResolvePath(string relativePath)
    {
        string[] segments = relativePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        foreach (string segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(InvalidNameChars) >= 0 || segment.EndsWith('.') || segment.EndsWith(' ') || IsDosDeviceName(segment))
                throw new ProtocolStatusException(NtStatus.ObjectNameInvalid, $"Invalid path segment '{segment}'.");
        }

        string current = RootPath;
        bool exists = true;
        foreach (string segment in segments)
        {
            current = Path.Join(current, segment);
            if (exists)
                exists = CheckLink(current);
        }

        string full = Path.GetFullPath(current);
        if (!IsInsideShare(full))
            throw new ProtocolStatusException(NtStatus.AccessDenied, "Path leaves the share.");
        return full;
    }

    /// <summary>True for <c>NUL</c>, <c>com1.txt</c>, <c>CONIN$</c> and the like: the part before the first dot, minus trailing spaces.</summary>
    private static bool IsDosDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        string stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');
        return DosDeviceNames.Contains(stem);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle handle);

    /// <summary>Returns false when <paramref name="path"/> does not exist; throws when it is a link leaving the share.</summary>
    private bool CheckLink(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return true;

        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        FileSystemInfo? target;
        try
        {
            target = info.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (IOException)
        {
            throw new ProtocolStatusException(NtStatus.AccessDenied, "Link inside the share cannot be resolved.");
        }

        // Reparse points that are not links (dedup, cloud placeholders) have no target and stay where they are.
        if (target is not null && !IsInsideShare(Path.GetFullPath(target.FullName)))
            throw new ProtocolStatusException(NtStatus.AccessDenied, "Link inside the share points outside it.");
        return true;
    }

    private bool IsInsideShare(string fullPath)
    {
        foreach (string root in _allowedRoots)
        {
            if (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;
            if (fullPath.Length > root.Length
                && fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && fullPath[root.Length] == Path.DirectorySeparatorChar)
                return true;
        }
        return false;
    }

    public FsEntry Stat(string relativePath)
    {
        string path = ResolvePath(relativePath);
        FileSystemInfo info = GetExistingInfo(path);
        return ToEntry(info, path.Length == RootPath.Length ? string.Empty : info.Name, FileDetailsReader.Read(path));
    }

    /// <param name="maxResponseBytes">Upper bound for the serialized page; at least one entry is always returned.</param>
    public FsListResponse List(string relativePath, int startIndex, int maxCount, int maxResponseBytes = int.MaxValue)
    {
        string path = ResolvePath(relativePath);
        FileSystemInfo info = GetExistingInfo(path);
        if (info is not DirectoryInfo directory)
            throw new ProtocolStatusException(NtStatus.NotADirectory, "Not a directory.");

        // Sorted so paging by index is stable between pages.
        FileSystemInfo[] all = directory.EnumerateFileSystemInfos("*", ListOptions)
            .Where(x => !IsStagingName(x.Name))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        int start = Math.Clamp(startIndex, 0, all.Length);
        int limit = Math.Min(maxCount <= 0 ? DefaultListPage : Math.Min(maxCount, DefaultListPage * 4), all.Length - start);

        // Names can be 255 characters, so the count alone does not keep the page inside one frame.
        int budget = maxResponseBytes - ListResponseOverhead;
        int count = 0;
        while (count < limit)
        {
            budget -= EntryCost(all[start + count].Name);
            if (budget < 0 && count > 0)
                break;
            count++;
        }
        // File ids, change times and allocation sizes of the whole directory in one scan.
        Dictionary<string, FileDetailsReader.FileDetails> details = FileDetailsReader.ReadDirectory(path);
        return new FsListResponse
        {
            Entries = all.Skip(start).Take(count)
                .Select(x => ToEntry(x, x.Name, details.TryGetValue(x.Name, out FileDetailsReader.FileDetails d) ? d : null))
                .ToArray(),
            HasMore = start + count < all.Length,
        };
    }

    /// <summary>Upper bound of the serialized size of an entry named <paramref name="name"/>.</summary>
    internal static int EntryCost(string name) => ListEntryOverhead + Encoding.UTF8.GetMaxByteCount(name.Length);

    /// <summary>Starts a breadth-first walk of the directory <paramref name="relativePath"/>.</summary>
    internal ShareTreeWalk StartTree(string relativePath, int maxDepth, int maxEntries)
    {
        string path = ResolvePath(relativePath);
        if (GetExistingInfo(path) is not DirectoryInfo)
            throw new ProtocolStatusException(NtStatus.NotADirectory, "Not a directory.");
        string normalized = string.Join('/', relativePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries));
        return new ShareTreeWalk(normalized, path, maxDepth, maxEntries);
    }

    /// <summary>Reads up to <c>buffer.Length</c> bytes at <paramref name="offset"/>; returns bytes read and the file version.</summary>
    public int Read(string relativePath, long offset, Span<byte> buffer, FsVersion? expectedVersion, out FsVersion version)
    {
        string path = ResolvePath(relativePath);
        if (Directory.Exists(path))
            throw new ProtocolStatusException(NtStatus.FileIsADirectory, "Cannot read a directory.");
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Name checks cannot catch every alias of a device; reading from a pipe or console would block the request.
        if (GetFileType(handle) != FileTypeDisk)
            throw new ProtocolStatusException(NtStatus.ObjectNameInvalid, "Not a disk file.");
        version = new FsVersion { LastWriteTime = File.GetLastWriteTimeUtc(handle).ToFileTimeUtc(), Size = RandomAccess.GetLength(handle) };
        if (expectedVersion is { } expected && expected != version)
            throw new ProtocolStatusException(NtStatus.FileVersionChanged, "File changed on machine B.");

        int total = 0;
        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(handle, buffer[total..], offset + total);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    private static FileSystemInfo GetExistingInfo(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists)
            return directory;
        var file = new FileInfo(path);
        if (file.Exists)
            return file;
        bool parentExists = Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent);
        throw new ProtocolStatusException(parentExists ? NtStatus.ObjectNameNotFound : NtStatus.ObjectPathNotFound, "Not found.");
    }

    /// <param name="details">File id, change time and allocation size when they could be read; otherwise the change
    /// time falls back to the last write time and the id and allocation size stay unknown.</param>
    internal static FsEntry ToEntry(FileSystemInfo info, string name, FileDetailsReader.FileDetails? details)
    {
        long lastWrite = info.LastWriteTimeUtc.ToFileTimeUtc();
        return new FsEntry
        {
            Name = name,
            Attributes = (uint)info.Attributes,
            Size = info is FileInfo file ? file.Length : 0,
            CreationTime = info.CreationTimeUtc.ToFileTimeUtc(),
            LastAccessTime = info.LastAccessTimeUtc.ToFileTimeUtc(),
            LastWriteTime = lastWrite,
            ChangeTime = details?.ChangeTime ?? lastWrite,
            FileId = details?.FileId ?? 0,
            AllocationSize = details?.AllocationSize,
        };
    }
}
