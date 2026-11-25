using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsListResponse
{
    [Key(0)] public required FsEntry[] Entries { get; init; }
    [Key(1)] public required bool HasMore { get; init; }
}
