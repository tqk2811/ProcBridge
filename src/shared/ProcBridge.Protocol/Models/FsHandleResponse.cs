using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Answer of FsOpenHandle, FsCloseHandle, FsLock and FsUnlock (M8).</summary>
[MessagePackObject]
public sealed class FsHandleResponse
{
    [Key(0)] public required ulong HandleId { get; init; }
}
