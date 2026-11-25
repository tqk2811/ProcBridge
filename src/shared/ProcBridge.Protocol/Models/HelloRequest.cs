using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>First request on every connection.</summary>
[MessagePackObject]
public sealed class HelloRequest
{
    [Key(0)] public required int ProtocolVersion { get; init; }
    [Key(1)] public required string ClientName { get; init; }
    /// <summary>Share this connection works on.</summary>
    [Key(2)] public required string ShareName { get; init; }
}
