using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Header of a <see cref="Enums.MessageType.FsChanged"/> notification frame. Changes are batched; when
/// <see cref="Rescan"/> is set the Agent lost events (watcher buffer overflow, slow client) and the receiver must
/// treat everything it cached as stale.
/// </summary>
[MessagePackObject]
public sealed class FsChangeNotification
{
    [Key(0)] public required FsChange[] Changes { get; init; }
    [Key(1)] public bool Rescan { get; init; }
}
