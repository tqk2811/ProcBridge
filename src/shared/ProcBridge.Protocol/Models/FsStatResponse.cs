using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsStatResponse
{
    [Key(0)] public required FsEntry Entry { get; init; }
}
