using MessagePack;

namespace ProcBridge.Protocol.Models;

[MessagePackObject]
public sealed class FsCommitResponse
{
    /// <summary>The file at the request path after the commit (machine B's own version on a kept conflict).</summary>
    [Key(0)] public required FsEntry Entry { get; init; }
    /// <summary>Share path the uploaded content was saved to because of a conflict; null when it replaced the file.</summary>
    [Key(1)] public string? ConflictPath { get; init; }
}
