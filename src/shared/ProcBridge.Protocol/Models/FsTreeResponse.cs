using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsTreeResponse
{
    [Key(0)] public required FsTreeDirectory[] Directories { get; init; }
    /// <summary>Send back to get the next page; 0 when the walk is over.</summary>
    [Key(1)] public required ulong Cursor { get; init; }
    /// <summary>The walk stopped at the server's entry limit; directories not listed must be listed on demand.</summary>
    [Key(2)] public bool Truncated { get; init; }
}
