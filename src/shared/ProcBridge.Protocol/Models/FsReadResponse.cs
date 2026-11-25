using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsReadResponse
{
    /// <summary>Version of the file at the time the chunk was read.</summary>
    [Key(0)] public required FsVersion Version { get; init; }
}
