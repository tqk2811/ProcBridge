using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Version of a file on machine B (plan section 10): last write time + size. Equal versions mean the cached
/// copy can be reused.
/// </summary>
[MessagePackObject]
public readonly record struct FsVersion
{
    [Key(0)] public long LastWriteTime { get; init; }
    [Key(1)] public long Size { get; init; }
}
