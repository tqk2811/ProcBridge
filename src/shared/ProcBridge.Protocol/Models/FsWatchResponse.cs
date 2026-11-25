using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsWatchResponse
{
    /// <summary>Delay the Agent waits to batch changes before it sends them, for diagnostics.</summary>
    [Key(0)] public int BatchDelayMilliseconds { get; init; }
}
