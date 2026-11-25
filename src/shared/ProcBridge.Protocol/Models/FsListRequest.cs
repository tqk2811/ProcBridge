using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Lists a directory one page at a time so a huge directory never exceeds the frame limit.</summary>
[MessagePackObject]
public sealed class FsListRequest
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public int StartIndex { get; init; }
    /// <summary>Page size; 0 lets the server choose.</summary>
    [Key(2)] public int MaxCount { get; init; }
}
