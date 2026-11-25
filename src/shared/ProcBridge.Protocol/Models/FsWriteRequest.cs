using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>
/// Writes one block of an upload; the bytes are the frame payload. Blocks go to a staging file next to the target
/// (named by <see cref="UploadId"/>), so machine B's file only changes at <see cref="FsCommitRequest"/>. Response:
/// <see cref="FsWriteResponse"/>.
/// </summary>
[MessagePackObject]
public sealed class FsWriteRequest
{
    [Key(0)] public required string Path { get; init; }
    /// <summary>32 lowercase hex digits chosen by the client.</summary>
    [Key(1)] public required string UploadId { get; init; }
    [Key(2)] public required long Offset { get; init; }
}
