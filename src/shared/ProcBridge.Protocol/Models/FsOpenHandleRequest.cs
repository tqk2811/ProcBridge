using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Opens a handle machine B keeps for an open on machine A (M8): byte range locks are taken on it and its share mode
/// keeps users of machine B out the way the open on machine A would. The handle belongs to the connection: it is
/// closed by <see cref="Enums.MessageType.FsCloseHandle"/> or when the connection ends. Response: <see cref="FsHandleResponse"/>.
/// </summary>
[MessagePackObject]
public sealed class FsOpenHandleRequest
{
    [Key(0)] public required string Path { get; init; }
    /// <summary>FILE_SHARE_* of the open on machine A; machine B always adds FILE_SHARE_DELETE (renames and deletes from machine A).</summary>
    [Key(1)] public uint ShareAccess { get; init; }
    /// <summary>Open for writing (a commit then writes through this handle, in place).</summary>
    [Key(2)] public bool Write { get; init; }
}
