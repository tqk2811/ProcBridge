using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsStatRequest
{
    /// <summary>Share-relative path, segments separated by '/', empty for the share root.</summary>
    [Key(0)] public required string Path { get; init; }
}
