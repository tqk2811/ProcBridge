using MessagePack;

namespace ProcBridge.Protocol.Models;

/// <summary>Changes attributes, times or the size of a file; null fields stay as they are. Response: <see cref="FsStatResponse"/>.</summary>
[MessagePackObject]
public sealed class FsSetInfoRequest
{
    [Key(0)] public required string Path { get; init; }
    [Key(1)] public uint? Attributes { get; init; }
    [Key(2)] public long? CreationTime { get; init; }
    [Key(3)] public long? LastAccessTime { get; init; }
    [Key(4)] public long? LastWriteTime { get; init; }
    /// <summary>New end of file (files only).</summary>
    [Key(5)] public long? EndOfFile { get; init; }
}
