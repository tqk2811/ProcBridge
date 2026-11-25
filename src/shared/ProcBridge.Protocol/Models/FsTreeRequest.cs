using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Lists a subtree breadth first, one page per request. The first request has <see cref="Cursor"/> 0; the next
/// ones send back the cursor of the previous response. The walk state lives on the connection that started it.
/// </summary>
[MessagePackObject]
public sealed class FsTreeRequest
{
    /// <summary>Share-relative directory to start from; empty for the share root.</summary>
    [Key(0)] public required string Path { get; init; }
    /// <summary>Levels below <see cref="Path"/> to list; 1 lists only its children, 0 or less lets the server choose.</summary>
    [Key(1)] public int MaxDepth { get; init; }
    /// <summary>Continuation from the previous page; 0 starts a new walk.</summary>
    [Key(2)] public ulong Cursor { get; init; }
}
