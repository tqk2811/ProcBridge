using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Deletes a file or an empty directory. Response: <see cref="FsDeleteResponse"/>.</summary>
[MessagePackObject]
public sealed class FsDeleteRequest
{
    [Key(0)] public required string Path { get; init; }
}
