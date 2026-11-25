using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Creates a new empty file or directory; an existing name fails with ObjectNameCollision. Response: <see cref="FsStatResponse"/>.</summary>
[MessagePackObject]
public sealed class FsCreateRequest
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public required bool IsDirectory { get; init; }
    /// <summary>FILE_ATTRIBUTE_* given to the new file (read-only, hidden, system, archive, not-indexed are kept).</summary>
    [Key(2)] public uint Attributes { get; init; }
}
