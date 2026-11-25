using System.Globalization;
using Microsoft.Win32.SafeHandles;
using ProcBridge.Protocol;
using ProcBridge.Protocol.Enums;
using ProcBridge.Protocol.Models;

namespace ProcBridge.Agent;

/// <summary>
/// Write side of a share (M3, plan 4.5 and 10). Every operation needs <see cref="IsWritable"/> and goes through the
/// same path checks as reads (no traversal, devices or links leaving the share). Content is uploaded into a staging
/// file next to the target and swapped in by <see cref="Commit"/>, which compares the version machine A started
/// from with the current one to detect conflicts.
/// </summary>
public sealed partial class AgentShare
{
    private const string StagingMarker = ".pbtmp-";
    private const string ConflictMarker = ".procbridge-conflict-";

    /// <summary>Attributes machine A may set; the others (directory, sparse, reparse, compressed...) describe storage.</summary>
    private const FileAttributes SettableAttributes =
        FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive
        | FileAttributes.NotContentIndexed | FileAttributes.Offline | FileAttributes.Temporary;

    /// <summary>Commits, renames and deletes are serialized so a version check and the swap that follows are atomic.</summary>
    private readonly Lock _writeLock = new();

    /// <summary>
    /// Recent commits under <see cref="_writeLock"/>: upload id, the file it went to (the target, or the conflict copy when
    /// <c>ConflictPath</c> is set) and that file's version afterwards. A repeated commit is answered again.
    /// </summary>
    private readonly Queue<(string UploadId, string Path, string? ConflictPath, FsVersion Version)> _commits = new();
    private const int RememberedCommits = 256;

    /// <summary>Upload staging files (<c>name.pbtmp-&lt;id&gt;</c>) are hidden from listings.</summary>
    private static bool IsStagingName(string name) => name.Contains(StagingMarker, StringComparison.OrdinalIgnoreCase);

    private void EnsureWritable()
    {
        if (!IsWritable)
            throw new ProtocolStatusException(NtStatus.MediaWriteProtected, "Share is read only.");
    }

    /// <summary>Resolves a path that must name something below the root (not the root itself).</summary>
    private string ResolveChild(string relativePath)
    {
        string path = ResolvePath(relativePath);
        if (path.Length == RootPath.Length)
            throw new ProtocolStatusException(NtStatus.AccessDenied, "The share root cannot be changed.");
        if (IsStagingName(Path.GetFileName(path)))
            throw new ProtocolStatusException(NtStatus.ObjectNameInvalid, "Reserved name.");
        return path;
    }

    private static void RequireParent(string path)
    {
        string parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent))
            throw new ProtocolStatusException(File.Exists(parent) ? NtStatus.NotADirectory : NtStatus.ObjectPathNotFound, "Parent folder not found.");
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    public FsEntry Create(string relativePath, bool isDirectory, uint attributes)
    {
        EnsureWritable();
        string path = ResolveChild(relativePath);
        lock (_writeLock)
        {
            RequireParent(path);
            if (Exists(path))
                throw new ProtocolStatusException(NtStatus.ObjectNameCollision, "Name already exists.");
            if (isDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                using SafeFileHandle handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                if (GetFileType(handle) != FileTypeDisk)
                    throw new ProtocolStatusException(NtStatus.ObjectNameInvalid, "Not a disk file.");
            }
            FileAttributes settable = (FileAttributes)attributes & SettableAttributes;
            if (settable != 0)
                File.SetAttributes(path, settable | (isDirectory ? FileAttributes.Directory : 0));
        }
        return Stat(relativePath);
    }

    /// <summary>Writes one upload block into the staging file of <paramref name="relativePath"/>; returns bytes written.</summary>
    public int WriteBlock(string relativePath, string uploadId, long offset, ReadOnlySpan<byte> data)
    {
        EnsureWritable();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        string staging = GetStagingPath(ResolveChild(relativePath), uploadId);
        RequireParent(staging);
        // Blocks of one upload arrive in parallel on several connections: every writer shares the staging file.
        using SafeFileHandle handle = File.OpenHandle(staging, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
        if (GetFileType(handle) != FileTypeDisk)
            throw new ProtocolStatusException(NtStatus.ObjectNameInvalid, "Not a disk file.");
        RandomAccess.Write(handle, data, offset);
        return data.Length;
    }

    private static string GetStagingPath(string path, string uploadId)
    {
        if (uploadId.Length != 32 || !uploadId.All(char.IsAsciiHexDigitLower))
            throw new ProtocolStatusException(NtStatus.InvalidParameter, "Upload id must be 32 lowercase hex digits.");
        return path + StagingMarker + uploadId;
    }

    /// <summary>
    /// Swaps the staging file in. A version mismatch with <see cref="ConflictPolicy.KeepBoth"/> keeps machine B's file
    /// and saves the upload as <c>name.procbridge-conflict-&lt;UTC time&gt;</c> instead.
    /// </summary>
    public FsCommitResponse Commit(string relativePath, string uploadId, long size, FsVersion? expectedVersion, long? lastWriteTime, ConflictPolicy policy)
    {
        EnsureWritable();
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        string path = ResolveChild(relativePath);
        string staging = GetStagingPath(path, uploadId);
        try
        {
            lock (_writeLock)
            {
                RequireParent(path);
                if (Directory.Exists(path))
                    throw new ProtocolStatusException(NtStatus.FileIsADirectory, "Target is a directory.");
                using (SafeFileHandle handle = File.OpenHandle(staging, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                    RandomAccess.SetLength(handle, size);
                if (lastWriteTime is { } time)
                    File.SetLastWriteTimeUtc(staging, DateTime.FromFileTimeUtc(time));

                bool exists = File.Exists(path);
                // The same commit again (its reply was lost): it is done already, do not apply it twice.
                if (GetRepeatedCommit(relativePath, path, staging, uploadId, size, lastWriteTime) is { } repeated)
                    return repeated;
                if (expectedVersion is { } expected && (!exists || CurrentVersion(path) != expected) && policy == ConflictPolicy.KeepBoth)
                {
                    string conflict = GetConflictPath(path);
                    File.Move(staging, conflict);
                    RememberCommit(uploadId, path, conflict);
                    string relativeConflict = ToRelative(conflict);
                    return new FsCommitResponse { Entry = exists ? Stat(relativePath) : Stat(relativeConflict), ConflictPath = relativeConflict };
                }

                if (exists && FindHeldWriter(path) is { } held)
                {
                    // Machine A holds the file open (M8): write in place through that handle, which keeps the file's
                    // identity, its share mode and the byte range locks taken on it.
                    CommitInPlace(held, staging, size, lastWriteTime);
                }
                else if (exists)
                {
                    // Replace keeps the target's identity: creation time, attributes, ACL, file id of the name.
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    File.Replace(staging, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
                }
                else
                {
                    File.Move(staging, path);
                }
                RememberCommit(uploadId, path, null);
                return new FsCommitResponse { Entry = Stat(relativePath) };
            }
        }
        finally
        {
            TryDeleteFile(staging);
        }
    }

    private string ToRelative(string path) => Path.GetRelativePath(RootPath, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>
    /// The answer of an earlier commit of <paramref name="uploadId"/> when nothing changed its result since, or a plain
    /// success when the target already is the upload (same size, write time and content: committed by an Agent that
    /// forgot it). Null for a new commit.
    /// </summary>
    private FsCommitResponse? GetRepeatedCommit(string relativePath, string path, string staging, string uploadId, long size, long? lastWriteTime)
    {
        foreach ((string id, string committedPath, string? conflict, FsVersion version) in _commits)
        {
            if (id != uploadId || !committedPath.Equals(path, StringComparison.OrdinalIgnoreCase))
                continue;
            string result = conflict ?? path;
            if (!File.Exists(result) || CurrentVersion(result) != version)
                return null;
            return conflict is null
                ? new FsCommitResponse { Entry = Stat(relativePath) }
                : new FsCommitResponse { Entry = File.Exists(path) ? Stat(relativePath) : Stat(ToRelative(conflict)), ConflictPath = ToRelative(conflict) };
        }
        if (lastWriteTime is not { } time || !File.Exists(path) || CurrentVersion(path) != new FsVersion { LastWriteTime = time, Size = size })
            return null;
        return SameContent(path, staging) ? new FsCommitResponse { Entry = Stat(relativePath) } : null;
    }

    private static bool SameContent(string first, string second)
    {
        using var a = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var b = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (a.Length != b.Length)
            return false;
        byte[] left = new byte[81920];
        byte[] right = new byte[81920];
        while (true)
        {
            int read = a.ReadAtLeast(left, left.Length, throwOnEndOfStream: false);
            if (read == 0)
                return true;
            b.ReadExactly(right, 0, read);
            if (!left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read)))
                return false;
        }
    }

    private void RememberCommit(string uploadId, string path, string? conflictPath)
    {
        if (_commits.Count == RememberedCommits)
            _commits.Dequeue();
        _commits.Enqueue((uploadId, path, conflictPath, CurrentVersion(conflictPath ?? path)));
    }

    /// <summary>A free <c>name.procbridge-conflict-&lt;UTC time&gt;</c> (two conflicts within one millisecond get a suffix).</summary>
    private static string GetConflictPath(string path)
    {
        string conflict = path + ConflictMarker + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        string candidate = conflict;
        for (int i = 2; Exists(candidate); i++)
            candidate = conflict + "-" + i.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    private static FsVersion CurrentVersion(string path)
    {
        var info = new FileInfo(path);
        return new FsVersion { LastWriteTime = info.LastWriteTimeUtc.ToFileTimeUtc(), Size = info.Length };
    }

    public FsEntry SetInfo(string relativePath, uint? attributes, long? creationTime, long? lastAccessTime, long? lastWriteTime, long? endOfFile)
    {
        EnsureWritable();
        string path = ResolvePath(relativePath);
        FileSystemInfo info = GetExistingInfo(path);
        bool isDirectory = info is DirectoryInfo;
        if (endOfFile is { } length)
        {
            if (isDirectory)
                throw new ProtocolStatusException(NtStatus.FileIsADirectory, "A directory has no size.");
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            if (FindHeldWriter(path) is { } held)
            {
                // Its share mode may refuse a second writer: use the held handle itself (M8).
                RandomAccess.SetLength(held.Handle, length);
            }
            else
            {
                using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                if (GetFileType(handle) != FileTypeDisk)
                    throw new ProtocolStatusException(NtStatus.ObjectNameInvalid, "Not a disk file.");
                RandomAccess.SetLength(handle, length);
            }
        }

        // Times first: a read-only attribute set afterwards would refuse them.
        FileAttributes current = info.Attributes;
        bool readOnly = (current & FileAttributes.ReadOnly) != 0;
        if (readOnly && (creationTime ?? lastAccessTime ?? lastWriteTime) is not null)
            File.SetAttributes(path, current & ~FileAttributes.ReadOnly);
        if (creationTime is { } created)
            info.CreationTimeUtc = DateTime.FromFileTimeUtc(created);
        if (lastAccessTime is { } accessed)
            info.LastAccessTimeUtc = DateTime.FromFileTimeUtc(accessed);
        if (lastWriteTime is { } written)
            info.LastWriteTimeUtc = DateTime.FromFileTimeUtc(written);

        if (attributes is { } value)
        {
            FileAttributes settable = (FileAttributes)value & SettableAttributes;
            File.SetAttributes(path, settable == 0 && !isDirectory ? FileAttributes.Normal : settable | (isDirectory ? FileAttributes.Directory : 0));
        }
        else if (readOnly && (creationTime ?? lastAccessTime ?? lastWriteTime) is not null)
        {
            File.SetAttributes(path, current);
        }
        return Stat(relativePath);
    }

    public FsEntry Rename(string relativePath, string newRelativePath, bool replaceIfExists)
    {
        EnsureWritable();
        string source = ResolveChild(relativePath);
        string target = ResolveChild(newRelativePath);
        lock (_writeLock)
        {
            FileSystemInfo info = GetExistingInfo(source);
            RequireParent(target);
            bool sameName = source.Equals(target, StringComparison.OrdinalIgnoreCase);
            if (!sameName && Exists(target))
            {
                if (!replaceIfExists)
                    throw new ProtocolStatusException(NtStatus.ObjectNameCollision, "Target name exists.");
                if (Directory.Exists(target) || info is DirectoryInfo)
                    throw new ProtocolStatusException(NtStatus.AccessDenied, "A directory cannot be replaced.");
                if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                    throw new ProtocolStatusException(NtStatus.AccessDenied, "Target is read only.");
            }
            if (target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ProtocolStatusException(NtStatus.InvalidParameter, "A directory cannot move into itself.");

            if (info is DirectoryInfo)
                Directory.Move(source, target);
            else
                File.Move(source, target, overwrite: replaceIfExists && !sameName);
            RenameHeld(source, target);
        }
        return Stat(newRelativePath);
    }

    /// <summary>Deletes a file or an empty directory, with the status NTFS gives a local caller on refusal.</summary>
    /// <param name="expectedFileId">File id the caller saw (a delete-on-close sent after the fact): another file now under
    /// the same name is left alone and false is returned.</param>
    public bool Delete(string relativePath, ulong? expectedFileId = null)
    {
        EnsureWritable();
        string path = ResolveChild(relativePath);
        lock (_writeLock)
        {
            FileSystemInfo info = GetExistingInfo(path);
            if (expectedFileId is { } fileId && fileId != 0 && Stat(relativePath).FileId is var current && current != 0 && current != fileId)
                return false;
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                throw new ProtocolStatusException(NtStatus.CannotDelete, "Read-only file or directory.");
            if (info is DirectoryInfo directory)
            {
                if (directory.EnumerateFileSystemInfos("*", ListOptions).Any())
                    throw new ProtocolStatusException(NtStatus.DirectoryNotEmpty, "Directory is not empty.");
                directory.Delete(recursive: false);
            }
            else
            {
                info.Delete();
            }
        }
        return true;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
