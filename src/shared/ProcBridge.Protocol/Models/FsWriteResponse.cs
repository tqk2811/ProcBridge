using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsWriteResponse
{
    /// <summary>Bytes written.</summary>
    [Key(0)] public required int Count { get; init; }
}
