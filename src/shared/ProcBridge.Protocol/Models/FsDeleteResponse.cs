using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsDeleteResponse
{
    /// <summary>True when the name existed and was removed.</summary>
    [Key(0)] public required bool Deleted { get; init; }
}
