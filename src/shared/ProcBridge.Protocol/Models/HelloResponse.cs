using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class HelloResponse
{
    [Key(0)] public required int ProtocolVersion { get; init; }
    [Key(1)] public required string ServerName { get; init; }
    /// <summary>Largest frame body the server accepts or sends.</summary>
    [Key(2)] public required int MaxFrameSize { get; init; }
    /// <summary>Largest <see cref="FsReadRequest.Length"/> the server serves in one response.</summary>
    [Key(3)] public required int MaxReadSize { get; init; }
}
