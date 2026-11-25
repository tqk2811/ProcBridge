using Microsoft.Win32.SafeHandles;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;
using ProcBridge.Agent.Explorer.Native;

namespace ProcBridge.Agent.Explorer;

/// <summary>Drives, folders and files inside the roots of the policy (<c>Sys.Drives</c>, <c>Fs.*</c>).</summary>
public sealed class FileExplorerProvider : ExplorerProviderBase
{
    /// <summary>Largest chunk one <see cref="FileReadRequest"/> returns.</summary>
    public const int MaxReadChunk = 256 * 1024;

    private readonly PathScopeGuard _guard;

    internal FileExplorerProvider(ExplorerPolicy policy, string requesterId, IExplorerAuditLog audit)
        : base(policy, requesterId, audit)
    {
        _guard = new PathScopeGuard(policy.FileRoots);
    }

    internal PathScopeGuard Guard => _guard;

    public DrivesResponse ListDrives(DrivesRequest request) => Read(ExplorerPermissions.FileRead, () =>
    {
        var drives = new List<DriveEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!_guard.Roots.Any(r => r.StartsWith(drive.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var ready = drive.IsReady;
            drives.Add(new DriveEntry
            {
                Name = drive.Name,
                DriveType = drive.DriveType.ToString(),
                IsReady = ready,
                Label = ready ? drive.VolumeLabel : null,
                Format = ready ? drive.DriveFormat : null,
                TotalBytes = ready ? drive.TotalSize : 0,
                FreeBytes = ready ? drive.AvailableFreeSpace : 0,
            });
        }

        return new DrivesResponse { Drives = drives, Roots = [.. _guard.Roots] };
    });

    public DirectoryListResponse ListDirectory(DirectoryListRequest request) => Read(ExplorerPermissions.FileRead, () =>
    {
        using var handle = _guard.OpenForRead(request.Path, NativeMethods.FileListDirectory | NativeMethods.FileReadAttributes, out var path);
        if (!NativeFile.IsDirectory(handle))
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Directory not found.");
        var entries = NativeFile.Enumerate(handle)
            .Select(e => ToEntry(path, e))
            .ToList();
        return new DirectoryListResponse { Path = path, Entries = entries };
    });

    public FileStatResponse Stat(FileStatRequest request) => Read(ExplorerPermissions.FileRead, () =>
    {
        using var handle = _guard.OpenForRead(request.Path, NativeMethods.FileReadAttributes, out var path);
        var basic = NativeFile.GetBasic(handle);
        var standard = NativeFile.GetStandard(handle);
        var name = Path.GetFileName(path);
        var entry = new NativeDirectoryEntry(name.Length == 0 ? path : name, basic.FileAttributes, standard.EndOfFile, basic.CreationTime, basic.LastWriteTime);
        return new FileStatResponse { Entry = ToEntry(Path.GetDirectoryName(path) ?? path, entry) };
    });

    public FileReadResponse ReadFile(FileReadRequest request) => Read(ExplorerPermissions.FileRead, () =>
    {
        if (request.Offset < 0 || request.Count < 0)
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Offset and count must not be negative.");
        var handle = _guard.OpenForRead(request.Path, NativeMethods.GenericRead, out _);
        if (NativeFile.IsDirectory(handle))
        {
            handle.Dispose();
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "File not found.");
        }

        using var stream = new FileStream(handle, FileAccess.Read);

        var length = stream.Length;
        var count = (int)Math.Min(Math.Min(request.Count, MaxReadChunk), Math.Max(0, length - request.Offset));
        var data = new byte[count];
        stream.Position = Math.Min(request.Offset, length);
        stream.ReadExactly(data);
        return new FileReadResponse { Data = data, FileLength = length };
    });

    public ExplorerAckResponse Delete(FileDeleteRequest request) => Write(ExplorerPermissions.FileWrite, "Fs.Delete", request.Path, () =>
    {
        using var handle = _guard.OpenEntryForChange(request.Path, NativeMethods.Delete | NativeMethods.FileReadAttributes, out _);
        var tag = NativeFile.GetAttributeTag(handle);
        var isDirectory = (tag.FileAttributes & NativeMethods.FileAttributeDirectory) != 0;
        var isLink = (tag.FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0;
        if (isDirectory && !isLink && request.Recursive)
            NativeFile.DeleteChildren(handle); // links inside are removed, never followed
        NativeFile.MarkDeleted(handle); // a link is removed itself, its target stays
    });

    public ExplorerAckResponse Rename(FileRenameRequest request) => Write(ExplorerPermissions.FileWrite, "Fs.Rename", $"{request.Path} -> {request.NewPath}", () =>
    {
        using var source = _guard.OpenEntryForChange(request.Path, NativeMethods.Delete | NativeMethods.FileReadAttributes, out _);
        using var targetParent = _guard.OpenParentForChange(request.NewPath, out _, out var name);
        NativeFile.Rename(source, targetParent, name); // fails with AlreadyExists, never replaces
    });

    public ExplorerAckResponse CreateDirectory(DirectoryCreateRequest request) => Write(ExplorerPermissions.FileWrite, "Fs.CreateDirectory", request.Path, () =>
    {
        var handles = new List<SafeFileHandle> { _guard.OpenAncestorForCreate(request.Path, out _, out var missing) };
        try
        {
            for (var i = 0; i < missing.Count; i++)
            {
                var last = i == missing.Count - 1;
                var handle = NativeFile.OpenRelative(handles[^1], missing[i], NativeMethods.FileListDirectory | NativeMethods.FileTraverse | NativeMethods.FileReadAttributes,
                    last ? NativeMethods.NtFileCreate : NativeMethods.NtFileOpenIf, NativeMethods.NtFileDirectoryFile,
                    NativeMethods.FileShareRead | NativeMethods.FileShareWrite);
                handles.Add(handle);
                // An existing folder on the way that is a link (made between the check and now) is not walked through.
                if (!last && (NativeFile.GetAttributeTag(handle).FileAttributes & NativeMethods.FileAttributeReparsePoint) != 0)
                    throw new ExplorerDeniedException(ExplorerStatus.OutOfScope, "A folder on the way became a link.");
            }
        }
        finally
        {
            foreach (var handle in handles)
                handle.Dispose();
        }
    });

    private static FileEntry ToEntry(string directory, NativeDirectoryEntry entry) => new()
    {
        Name = entry.Name,
        FullPath = Path.Combine(directory, entry.Name),
        IsDirectory = entry.IsDirectory,
        IsReparsePoint = entry.IsReparsePoint,
        Size = entry.IsDirectory ? 0 : entry.Size,
        Attributes = (int)entry.Attributes,
        CreationTimeUtc = DateTime.FromFileTimeUtc(entry.CreationTime),
        LastWriteTimeUtc = DateTime.FromFileTimeUtc(entry.LastWriteTime),
    };
}
