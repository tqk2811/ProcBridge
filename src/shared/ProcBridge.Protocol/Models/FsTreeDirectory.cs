using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Children of one directory of a tree walk, sorted like <see cref="FsListResponse"/>. A directory whose children
/// do not fit one page comes in several parts with the same <see cref="Path"/>; only the last one has
/// <see cref="Complete"/> set.
/// </summary>
[MessagePackObject]
public sealed class FsTreeDirectory
{
    /// <summary>Share-relative path of the directory, '/' separated.</summary>
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public required FsEntry[] Entries { get; init; }
    [Key(2)] public required bool Complete { get; init; }
}
