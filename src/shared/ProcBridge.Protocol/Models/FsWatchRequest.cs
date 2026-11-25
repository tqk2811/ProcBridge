using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Subscribes the connection to change notifications of the whole share. The empty response confirms the watcher
/// is running; from then on <see cref="Enums.MessageType.FsChanged"/> notifications arrive on this connection until
/// it closes.
/// </summary>
[MessagePackObject]
public sealed class FsWatchRequest
{
    /// <summary>Reserved for watching only a subtree; the Agent currently watches the whole share.</summary>
    [Key(0)] public string? Path { get; init; }
}
