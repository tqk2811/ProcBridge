using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Metadata of one file or directory on machine B. Times are FILETIME (UTC, 100 ns since 1601).</summary>
[MessagePackObject]
public sealed class FsEntry
{
    public const uint DirectoryAttribute = 0x10;

    /// <summary>File name only (no directory part). Empty for the share root.</summary>
    [Key(0)] public required string Name { get; init; }
    /// <summary>Win32 FILE_ATTRIBUTE_* flags as reported on machine B.</summary>
    [Key(1)] public required uint Attributes { get; init; }
    [Key(2)] public required long Size { get; init; }
    [Key(3)] public required long CreationTime { get; init; }
    [Key(4)] public required long LastAccessTime { get; init; }
    [Key(5)] public required long LastWriteTime { get; init; }
    [Key(6)] public required long ChangeTime { get; init; }
    /// <summary>File id on machine B; 0 when unknown.</summary>
    [Key(7)] public ulong FileId { get; init; }
    /// <summary>Allocation size on machine B; null when unknown.</summary>
    [Key(8)] public long? AllocationSize { get; init; }

    [IgnoreMember] public bool IsDirectory => (Attributes & DirectoryAttribute) != 0;

    [IgnoreMember] public FsVersion Version => new() { LastWriteTime = LastWriteTime, Size = IsDirectory ? 0 : Size };
}
