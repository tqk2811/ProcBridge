using MessagePack;
using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Explorer.Models;

/// <summary>Reads one chunk of a file (download to machine A).</summary>
[MessagePackObject]
public sealed class FileReadRequest
{
    [Key(0)]
    public required string Path { get; init; }

    [Key(1)]
    public long Offset { get; init; }

    /// <summary>Bytes wanted; the Agent caps it at <c>FileExplorerProvider.MaxReadChunk</c>.</summary>
    [Key(2)]
    public int Count { get; init; }
}
