using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Reads one chunk. The response header is <see cref="FsReadResponse"/>; the bytes are the frame payload.</summary>
[MessagePackObject]
public sealed class FsReadRequest
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public required long Offset { get; init; }
    [Key(2)] public required int Length { get; init; }
    /// <summary>When set, the server fails with <see cref="Enums.NtStatus.FileVersionChanged"/> if the file differs.</summary>
    [Key(3)] public FsVersion? ExpectedVersion { get; init; }
}
