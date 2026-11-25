using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Deletes a file or an empty directory. Response: <see cref="FsDeleteResponse"/>.</summary>
[MessagePackObject]
public sealed class FsDeleteRequest
{
    [Key(0)] public required string Path { get; init; }
    /// <summary>File id the client saw; another file under the name is not deleted (the response says false). Null: no check.</summary>
    [Key(1)] public ulong? ExpectedFileId { get; init; }
}
