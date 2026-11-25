using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Interfaces;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Agent.Explorer;

/// <summary>Drives, folders and files inside the roots of the policy (<c>Sys.Drives</c>, <c>Fs.*</c>).</summary>
public sealed class FileExplorerProvider : ExplorerProviderBase
{
    /// <summary>Largest chunk one <see cref="FileReadRequest"/> returns.</summary>
    public const int MaxReadChunk = 256 * 1024;

    private static readonly EnumerationOptions ListOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
    };

    private readonly PathScopeGuard _guard;

    internal FileExplorerProvider(ExplorerPolicy policy, string requesterId, IExplorerAuditLog audit)
        : base(policy, requesterId, audit)
    {
        _guard = new PathScopeGuard(policy.FileRoots);
    }

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
        var path = _guard.ResolveForRead(request.Path);
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Directory not found.");
        var entries = directory.EnumerateFileSystemInfos("*", ListOptions)
            .Select(ToEntry)
            .ToList();
        return new DirectoryListResponse { Path = path, Entries = entries };
    });

    public FileStatResponse Stat(FileStatRequest request) => Read(ExplorerPermissions.FileRead, () =>
    {
        var path = _guard.ResolveForRead(request.Path);
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Path not found.");
        return new FileStatResponse { Entry = ToEntry(info) };
    });

    public FileReadResponse ReadFile(FileReadRequest request) => Read(ExplorerPermissions.FileRead, () =>
    {
        if (request.Offset < 0 || request.Count < 0)
            throw new ExplorerDeniedException(ExplorerStatus.InvalidArgument, "Offset and count must not be negative.");
        var path = _guard.ResolveForRead(request.Path);
        if (!File.Exists(path))
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "File not found.");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var count = (int)Math.Min(Math.Min(request.Count, MaxReadChunk), Math.Max(0, length - request.Offset));
        var data = new byte[count];
        stream.Position = Math.Min(request.Offset, length);
        stream.ReadExactly(data);
        return new FileReadResponse { Data = data, FileLength = length };
    });

    public ExplorerAckResponse Delete(FileDeleteRequest request) => Write(ExplorerPermissions.FileWrite, "Fs.Delete", request.Path, () =>
    {
        var path = _guard.ResolveForChange(request.Path);
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory))
            File.Delete(path);
        else if (attributes.HasFlag(FileAttributes.ReparsePoint))
            Directory.Delete(path, false); // removes the junction/link only
        else
            Directory.Delete(path, request.Recursive);
    });

    public ExplorerAckResponse Rename(FileRenameRequest request) => Write(ExplorerPermissions.FileWrite, "Fs.Rename", $"{request.Path} -> {request.NewPath}", () =>
    {
        var source = _guard.ResolveForChange(request.Path);
        var target = _guard.ResolveForChange(request.NewPath);
        if (File.Exists(target) || Directory.Exists(target))
            throw new ExplorerDeniedException(ExplorerStatus.AlreadyExists, "Target already exists.");
        if (Directory.Exists(source))
            Directory.Move(source, target);
        else if (File.Exists(source))
            File.Move(source, target, overwrite: false);
        else
            throw new ExplorerDeniedException(ExplorerStatus.NotFound, "Source not found.");
    });

    public ExplorerAckResponse CreateDirectory(DirectoryCreateRequest request) => Write(ExplorerPermissions.FileWrite, "Fs.CreateDirectory", request.Path, () =>
    {
        var path = _guard.ResolveForChange(request.Path);
        if (File.Exists(path) || Directory.Exists(path))
            throw new ExplorerDeniedException(ExplorerStatus.AlreadyExists, "Path already exists.");
        Directory.CreateDirectory(path);
    });

    private static FileEntry ToEntry(FileSystemInfo info) => new()
    {
        Name = info.Name,
        FullPath = info.FullName.TrimEnd('\\').Length == 2 ? info.FullName : info.FullName.TrimEnd('\\'),
        IsDirectory = info is DirectoryInfo,
        IsReparsePoint = info.Attributes.HasFlag(FileAttributes.ReparsePoint),
        Size = info is FileInfo file ? file.Length : 0,
        Attributes = (int)info.Attributes,
        CreationTimeUtc = info.CreationTimeUtc,
        LastWriteTimeUtc = info.LastWriteTimeUtc,
    };
}
