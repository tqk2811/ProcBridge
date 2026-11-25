using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Byte range lock (FsLock) or unlock (FsUnlock) on a handle of <see cref="FsOpenHandleRequest"/> (M8). A conflicting
/// lock fails with <see cref="Enums.NtStatus.LockNotGranted"/>, at once or after <see cref="WaitMilliseconds"/>; an
/// unlock of a range that is not locked with <see cref="Enums.NtStatus.RangeNotLocked"/>. FsCloseHandle uses only
/// <see cref="HandleId"/>.
/// </summary>
[MessagePackObject]
public sealed class FsLockRequest
{
    [Key(0)] public required ulong HandleId { get; init; }
    [Key(1)] public long Offset { get; init; }
    [Key(2)] public long Length { get; init; }
    [Key(3)] public bool Exclusive { get; init; }
    /// <summary>How long a lock waits for a conflicting one to go away; 0 fails at once.</summary>
    [Key(4)] public int WaitMilliseconds { get; init; }
}
